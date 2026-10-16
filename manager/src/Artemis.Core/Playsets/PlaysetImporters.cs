using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Artemis.Core.Playsets;

/// <summary>A playset found in another tool. Read-only: Artemis never writes to the Launcher's or Irony's files.</summary>
public sealed record ExternalPlayset(string Source, string Name, IReadOnlyList<PlaysetMod> Mods);

public static class PlaysetImporters
{
    public const string LauncherName = "Paradox Launcher";
    public const string IronyName = "Irony";

    public static string LauncherFile(string userDir) => Path.Combine(userDir, "launcher-v2.sqlite");

    /// <summary>Irony's default storage folder (its appSettings.json can move it; we only look at the default).</summary>
    public static string IronyFolder() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mario", "IronyModManager");

    /// <summary>Playsets in the Launcher's database, in the Launcher's order, with its enabled flags.</summary>
    public static IReadOnlyList<ExternalPlayset> FromLauncher(string sqlitePath)
    {
        if (!File.Exists(sqlitePath)) return [];
        // Work on a copy so the Launcher's lock or an unflushed -wal file never gets in the way, and we can never write to it.
        var dir = Path.Combine(Path.GetTempPath(), "artemis-launcher-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var copy = Path.Combine(dir, "launcher.sqlite");
            File.Copy(sqlitePath, copy);
            foreach (var ext in new[] { "-wal", "-shm" })
                if (File.Exists(sqlitePath + ext)) File.Copy(sqlitePath + ext, copy + ext);

            var connection = new SqliteConnectionStringBuilder { DataSource = copy, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString();
            using var db = new SqliteConnection(connection);
            db.Open();

            var mods = new Dictionary<string, List<PlaysetMod>>();
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT pm.playsetId, m.gameRegistryId, pm.enabled
                    FROM playsets_mods pm JOIN mods m ON m.id = pm.modId
                    WHERE m.gameRegistryId IS NOT NULL
                    ORDER BY pm.playsetId, pm.position
                    """;
                using var rows = cmd.ExecuteReader();
                while (rows.Read())
                {
                    var key = KeyOf(rows.GetString(1));
                    if (key.Length == 0) continue;
                    var id = rows.GetString(0);
                    if (!mods.TryGetValue(id, out var list)) mods[id] = list = [];
                    list.Add(new PlaysetMod { Key = key, Enabled = !rows.IsDBNull(2) && rows.GetInt64(2) != 0 });
                }
            }

            var result = new List<ExternalPlayset>();
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = "SELECT id, name FROM playsets WHERE COALESCE(isRemoved, 0) = 0 ORDER BY name COLLATE NOCASE";
                using var rows = cmd.ExecuteReader();
                while (rows.Read())
                    result.Add(new ExternalPlayset(LauncherName, rows.IsDBNull(1) ? "Unnamed" : rows.GetString(1), mods.GetValueOrDefault(rows.GetString(0)) ?? []));
            }
            return result;
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>Irony's "collections": it keeps them in its newest Database_x.y.json. Irony lists enabled mods only.</summary>
    public static IReadOnlyList<ExternalPlayset> FromIrony(string folder)
    {
        if (!Directory.Exists(folder)) return [];
        var newest = Directory.GetFiles(folder, "Database_*.json").OrderByDescending(VersionOf).FirstOrDefault();
        if (newest is null) return [];
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(newest));
            var result = new List<ExternalPlayset>();
            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                if (entry.GetProperty("Name").GetString() != "ModCollection") continue;
                foreach (var collection in entry.GetProperty("Value").EnumerateArray())
                {
                    if (collection.TryGetProperty("Game", out var game) && game.GetString() != "Stellaris") continue;
                    var name = collection.TryGetProperty("Name", out var n) ? n.GetString() : null;
                    var keys = collection.TryGetProperty("Mods", out var m) ? m.EnumerateArray().Select(e => KeyOf(e.GetString() ?? "")).Where(k => k.Length > 0) : [];
                    result.Add(new ExternalPlayset(IronyName, string.IsNullOrWhiteSpace(name) ? "Unnamed" : name, keys.Select(k => new PlaysetMod { Key = k }).ToList()));
                }
            }
            return result;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or IOException)
        {
            return []; // a file we don't understand is the same as nothing to import
        }
    }

    /// <summary>"mod/ugc_123.mod" or "mod\\x.mod" to the key Artemis uses: the descriptor's file name without extension.</summary>
    private static string KeyOf(string entry) => Path.GetFileNameWithoutExtension(entry.Replace('\\', '/'));

    private static Version VersionOf(string file) =>
        Version.TryParse(Path.GetFileNameWithoutExtension(file)["Database_".Length..], out var v) ? v : new Version(0, 0);
}
