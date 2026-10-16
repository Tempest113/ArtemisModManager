namespace Artemis.Core.Mods;

public enum ModSource { Local, Workshop }

/// <summary>One installed mod, read from its outer .mod file (and descriptor.mod when the outer file lacks fields).</summary>
public sealed record ModInfo(
    string Key,
    string Name,
    ModSource Source,
    string OuterFile,
    string? ContentDir,
    string? ArchivePath,
    string? Version,
    string? SupportedVersion,
    string? RemoteFileId,
    string? Picture,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> Dependencies,
    bool ContentMissing,
    DateTime Modified = default,
    DateTime ContentModified = default,
    Plugins.PluginInfo? Plugin = null)
{
    /// <summary>The entry the game expects in dlc_load.json.</summary>
    public string LoadEntry => $"mod/{Key}.mod";

    /// <summary>Changes when the mod's folder or version changes: what "updated since I last looked" compares.</summary>
    public string SeenStamp => $"{ContentModified.Ticks}|{Version}";
}

public enum ModChange { None, New, Updated }

/// <summary>Which mods are new or updated since a snapshot taken at the last launch. No snapshot yet means nothing counts as changed.</summary>
public static class SeenMods
{
    public static Dictionary<string, string> Snapshot(IEnumerable<ModInfo> mods) =>
        mods.ToDictionary(m => m.Key, m => m.SeenStamp, StringComparer.OrdinalIgnoreCase);

    public static ModChange Check(ModInfo mod, IReadOnlyDictionary<string, string>? seen) =>
        seen is null ? ModChange.None
        : !seen.TryGetValue(mod.Key, out var stamp) ? ModChange.New
        : stamp == mod.SeenStamp ? ModChange.None
        : ModChange.Updated;
}
