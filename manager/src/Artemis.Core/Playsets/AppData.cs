using System.Text.Json;
using System.Text.RegularExpressions;
using Artemis.Core.IO;

namespace Artemis.Core.Playsets;

/// <summary>Where the manager keeps its own files. Tests and the --data-dir switch point it somewhere else.</summary>
public sealed class AppPaths(string root)
{
    public string Root { get; } = root;
    public string PlaysetsDir => Path.Combine(Root, "playsets");
    public string BackupDir => Path.Combine(Root, "backup");
    public string SettingsFile => Path.Combine(Root, "settings.json");

    public string LogFile => Path.Combine(Root, "artemis.log");

    /// <summary>The approved plugins and the hashes they were approved with. Written by Artemis, read by Orion.</summary>
    public string TrustFile => Path.Combine(Root, "trust.json");

    /// <summary>What Orion reads at start-up for the playset about to be played: which plugins are on and their settings. Written at every launch.</summary>
    public string PluginSettingsFile => Path.Combine(Root, "plugin-settings.json");

    /// <summary>Orion's files (D50): the core in a version folder, the pointer file the stub reads, build data, and the copies of plugins it loads.</summary>
    public string OrionDir => Path.Combine(Root, "orion");
    public string OrionPointerFile => Path.Combine(OrionDir, "current.txt");
    public string OrionBuildsDir => Path.Combine(OrionDir, "builds");

    /// <summary>The disposable index cache. Safe to delete at any time.</summary>
    public string IndexFile => Path.Combine(Root, "index.db");

    public static AppPaths Default() =>
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Artemis"));
}

/// <summary>Where the main window was, in device-independent pixels. Maximized windows keep their restored size here.</summary>
public sealed class WindowPlacement
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Maximized { get; set; }
}

public sealed class AppSettings
{
    public string? ActivePlaysetId { get; set; }
    public string? GameDir { get; set; }
    public WindowPlacement? Window { get; set; }

    /// <summary>Fingerprint of dlc_load.json as Artemis last saw or wrote it. A different one on the file means another tool changed it.</summary>
    public string? GameListStamp { get; set; }

    /// <summary>The Installed pane's share (0 to 1) of the two list panes.</summary>
    public double? LibrarySplit { get; set; }
    public bool LibraryCollapsed { get; set; }

    /// <summary>Mod key to the mod's <see cref="Artemis.Core.Mods.ModInfo.SeenStamp"/> at the last launch. Null until the first scan.</summary>
    public Dictionary<string, string>? SeenMods { get; set; }

    /// <summary>The user's note per mod key.</summary>
    public Dictionary<string, string> Notes { get; set; } = [];

    /// <summary>The master switch for plugins: off, Orion loads none of them, whatever is approved.</summary>
    public bool PluginsEnabled { get; set; } = true;

    /// <summary>The user agreed to install Orion (D24). While this is set, Artemis puts back what Steam's file check removes and brings Orion up to date at launch.</summary>
    public bool OrionInstalled { get; set; }

    /// <summary>Notices the user has dismissed, by the id each one carries, so they are not shown again until something changes.</summary>
    public List<string> DismissedNotices { get; set; } = [];

    public static AppSettings Load(AppPaths paths)
    {
        try { return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(paths.SettingsFile), Playset.JsonOptions) ?? new(); }
        catch (Exception ex) when (ex is IOException or JsonException) { return new(); }
    }

    public void Save(AppPaths paths) =>
        AtomicFile.WriteAllText(paths.SettingsFile, JsonSerializer.Serialize(this, Playset.JsonOptions));
}

public sealed record PlaysetList(IReadOnlyList<Playset> Playsets, IReadOnlyList<string> Warnings);

/// <summary>One JSON file per playset, named by its id. The file name, not the content, is the id.</summary>
public sealed partial class PlaysetStore(AppPaths paths)
{
    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex SafeId();

    public PlaysetList List()
    {
        var playsets = new List<Playset>();
        var warnings = new List<string>();
        if (Directory.Exists(paths.PlaysetsDir))
        {
            foreach (var file in Directory.EnumerateFiles(paths.PlaysetsDir, "*.json"))
            {
                try
                {
                    var playset = Playset.FromJson(File.ReadAllText(file));
                    playset.Id = Path.GetFileNameWithoutExtension(file);
                    playsets.Add(playset);
                }
                catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException)
                {
                    warnings.Add($"{Path.GetFileName(file)}: {ex.Message}");
                }
            }
        }
        playsets.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        return new PlaysetList(playsets, warnings);
    }

    public void Save(Playset playset)
    {
        if (!SafeId().IsMatch(playset.Id)) throw new ArtemisException("That playset has an invalid id.");
        AtomicFile.WriteAllText(FileFor(playset.Id), playset.ToJson());
    }

    public void Delete(string id)
    {
        if (SafeId().IsMatch(id)) File.Delete(FileFor(id));
    }

    private string FileFor(string id) => Path.Combine(paths.PlaysetsDir, id + ".json");
}
