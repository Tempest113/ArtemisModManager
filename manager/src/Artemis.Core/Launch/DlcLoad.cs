using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Artemis.Core.Mods;
using Artemis.Core.Playsets;

namespace Artemis.Core.Launch;

/// <summary>The game's own list of enabled mods (dlc_load.json). The game reads it; whoever wrote it last wins.</summary>
public static class DlcLoad
{
    public sealed record State(IReadOnlyList<string> EnabledMods, IReadOnlyList<string> DisabledDlcs);

    private sealed record Wire(
        [property: JsonPropertyName("disabled_dlcs")] IReadOnlyList<string> DisabledDlcs,
        [property: JsonPropertyName("enabled_mods")] IReadOnlyList<string> EnabledMods);

    private static readonly JsonSerializerOptions WriteOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static State Read(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        List<string> Strings(string name) =>
            doc.RootElement.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array
                ? a.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
                : [];
        return new State(Strings("enabled_mods"), Strings("disabled_dlcs"));
    }

    public static string Render(State state) =>
        JsonSerializer.Serialize(new Wire(state.DisabledDlcs, state.EnabledMods), WriteOptions);

    /// <summary>A short fingerprint of an enabled-mods list, order included.</summary>
    public static string Stamp(IEnumerable<string> entries) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join('\n', entries))));

    /// <summary>The entries to write for a playset, in order. Mods that aren't installed, and repeats, are left out.</summary>
    public static List<string> EntriesFor(Playset playset, IReadOnlyDictionary<string, ModInfo> installed)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return playset.EnabledMods
            .Where(m => installed.ContainsKey(m.Key) && seen.Add(m.Key))
            .Select(m => installed[m.Key].LoadEntry)
            .ToList();
    }

    /// <summary>Turns what the game has enabled into playset entries, for importing the current setup.</summary>
    public static List<PlaysetMod> ToPlaysetMods(IEnumerable<string> entries) =>
        entries.Select(e => Path.GetFileNameWithoutExtension(e.Replace('\\', '/')))
               .Where(key => key.Length > 0)
               .Select(key => new PlaysetMod { Key = key })
               .ToList();
}
