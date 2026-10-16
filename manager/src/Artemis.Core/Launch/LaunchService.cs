using Artemis.Core.Text;
using System.Diagnostics;
using System.Text.Json;
using Artemis.Core.Game;
using Artemis.Core.IO;
using Artemis.Core.Mods;
using Artemis.Core.Playsets;

namespace Artemis.Core.Launch;

/// <summary>The save "continue" would load: its name and in-game date.</summary>
public sealed record LastSave(string Name, string Date);

public sealed record ApplyResult(int EnabledMods, int SkippedMods, IReadOnlyList<string> Notes);

public sealed class LaunchService(GameInstall game, AppPaths paths, Func<bool>? isRunning = null)
{
    private bool Running => (isRunning ?? GameIsRunning)();

    private string OriginalBackup => Path.Combine(paths.BackupDir, "dlc_load.original.json");

    public static bool GameIsRunning()
    {
        var processes = Process.GetProcessesByName("stellaris");
        foreach (var p in processes) p.Dispose();
        return processes.Length > 0;
    }

    /// <summary>Writes the playset to dlc_load.json. The original file is saved once, before the first write.</summary>
    public ApplyResult Apply(Playset playset, IReadOnlyDictionary<string, ModInfo> installed)
    {
        if (Running) throw new ArtemisException(Loc.T("core2.launch_running_close"));
        var notes = new List<string>();
        var disabledDlcs = (IReadOnlyList<string>)[];

        if (File.Exists(game.DlcLoadPath))
        {
            if (!File.Exists(OriginalBackup))
            {
                Directory.CreateDirectory(paths.BackupDir);
                File.Copy(game.DlcLoadPath, OriginalBackup);
                notes.Add(Loc.T("core2.launch_saved_dlc_backup"));
            }
            try { disabledDlcs = DlcLoad.Read(game.DlcLoadPath).DisabledDlcs; }
            catch (System.Text.Json.JsonException) { notes.Add(Loc.T("core2.launch_dlc_unreadable")); }
        }

        var entries = DlcLoad.EntriesFor(playset, installed);
        AtomicFile.WriteAllText(game.DlcLoadPath, DlcLoad.Render(new DlcLoad.State(entries, disabledDlcs)));
        return new ApplyResult(entries.Count, playset.EnabledMods.Count() - entries.Count, notes);
    }

    public bool RestoreOriginal()
    {
        if (!File.Exists(OriginalBackup)) return false;
        File.Copy(OriginalBackup, game.DlcLoadPath, overwrite: true);
        return true;
    }

    /// <summary>What the game is started with: the arguments of its own launcher settings, plus "--continuelastsave" (the form Irony uses and the game accepts) to load the newest save at once.</summary>
    public IReadOnlyList<string> Arguments(bool continueLastSave) =>
        continueLastSave ? [.. game.Settings.ExeArgs, "--continuelastsave"] : game.Settings.ExeArgs;

    public const int SteamAppId = 281990;

    /// <summary>
    /// stellaris.exe started directly (not through the Paradox Launcher), with the three variables that tell Steam the process is the game:
    /// then Steam counts the playtime and shows its overlay. The same variables Irony's game handler sets.
    /// </summary>
    public ProcessStartInfo StartInfo(bool continueLastSave, bool withoutPlugins = false)
    {
        var info = new ProcessStartInfo(game.ExePath) { WorkingDirectory = game.GameDir, UseShellExecute = false };
        foreach (var arg in Arguments(continueLastSave)) info.ArgumentList.Add(arg);
        foreach (var name in new[] { "SteamAppId", "SteamGameId", "SteamOverlayGameId" }) info.Environment[name] = SteamAppId.ToString();
        // For Orion (D48): where Artemis keeps the trust store and plugin settings, and a one-off "load no plugins this time".
        info.Environment[DataDirVariable] = paths.Root;
        if (withoutPlugins) info.Environment[PluginsVariable] = "off";
        return info;
    }

    public const string DataDirVariable = "ARTEMIS_DATA_DIR";
    public const string PluginsVariable = "ORION_PLUGINS";

    public static bool SteamIsRunning()
    {
        var processes = Process.GetProcessesByName("steam");
        foreach (var p in processes) p.Dispose();
        return processes.Length > 0;
    }

    /// <summary>Starts Steam if it is not running and waits for it, since the game counts as played only while Steam is there. False if it didn't come up in time.</summary>
    public static bool EnsureSteam(TimeSpan timeout)
    {
        if (SteamIsRunning()) return true;
        Process.Start(new ProcessStartInfo("steam://open/main") { UseShellExecute = true });
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            Thread.Sleep(500);
            if (!SteamIsRunning()) continue;
            Thread.Sleep(4000); // it is up, but not yet ready to be told about a game
            return true;
        }
        return false;
    }

    /// <summary>The newest save, as the game itself notes it in continue_game.json; null when there is none yet.</summary>
    public static LastSave? FindLastSave(string userDir)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(userDir, "continue_game.json")),
                new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            string Text(string name) => doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            var name = Text("desc").Length > 0 ? Text("desc") : Text("title");
            return name.Length == 0 ? null : new LastSave(name, Text("date"));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    public Process? Launch(bool continueLastSave = false, bool withoutPlugins = false)
    {
        if (Running) throw new ArtemisException(Loc.T("core2.launch_already_running"));
        return Process.Start(StartInfo(continueLastSave, withoutPlugins));
    }

}
