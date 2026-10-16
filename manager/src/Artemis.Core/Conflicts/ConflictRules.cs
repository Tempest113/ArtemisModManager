namespace Artemis.Core.Conflicts;

public enum MergeRule
{
    /// <summary>Files of the folder are read in file-name order and the last definition of a name wins.</summary>
    LastWins,

    /// <summary>The first definition of a name wins.</summary>
    FirstWins,

    /// <summary>Like last wins, but the game doesn't always follow it in this folder.</summary>
    Unreliable,
}

/// <summary>
/// Which of two definitions of the same name the game uses, per folder. This follows the community Stellaris wiki's
/// "Common folder" table. Paradox does not document it and it may change between patches, so a winner shown here is
/// the documented expectation, not something Artemis has verified in the game.
/// </summary>
public static class ConflictRules
{
    private static readonly HashSet<string> FirstWins = new(StringComparer.OrdinalIgnoreCase)
    {
        "events", "common/component_sets", "common/component_templates", "common/event_chains", "common/global_ship_designs",
        "common/scripted_loc", "common/scripted_variables", "common/ship_behaviors", "common/solar_system_initializers",
        "common/special_projects", "common/start_screen_messages", "common/section_templates",
    };

    private static readonly HashSet<string> Unreliable = new(StringComparer.OrdinalIgnoreCase)
    {
        "common/traits", "common/strategic_resources", "common/terraform", "common/name_lists",
        "common/observation_station_missions", "common/opinion_modifiers", "common/planet_classes",
    };

    /// <summary>
    /// Folders whose names are not definitions that replace each other: on_actions are merged, defines are merged value by value,
    /// and an inline script's name is its file path (a same-path file is already a whole-file conflict).
    /// </summary>
    private static readonly HashSet<string> NotDefinitions = new(StringComparer.OrdinalIgnoreCase)
    {
        "common/on_actions", "common/defines", "common/inline_scripts",
    };

    public static bool IsDefinitionFolder(string domain) => !NotDefinitions.Contains(domain);

    public static MergeRule For(string domain) =>
        FirstWins.Contains(domain) ? MergeRule.FirstWins : Unreliable.Contains(domain) ? MergeRule.Unreliable : MergeRule.LastWins;
}

/// <summary>Which files of a mod or of the game count as game content: the folders and file types the game reads.</summary>
public static class GameFiles
{
    private static readonly HashSet<string> Roots = new(StringComparer.OrdinalIgnoreCase)
    {
        "common", "events", "flags", "fonts", "gfx", "history", "interface", "localisation", "localisation_synced",
        "map", "music", "prescripted_countries", "sound", "tutorial", "dlc_metadata",
    };

    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "txt", "yml", "gui", "gfx", "dds", "tga", "png", "jpg", "mesh", "anim", "asset", "shader", "fxh", "ogg", "wav",
        "mp3", "fnt", "ttf", "otf", "csv", "lua", "animsm", "editordata", "swatch", "cur", "ani", "bank", "settings",
    };

    /// <summary>Hidden folders (.git, .idea), files at the mod's root and editor leftovers are not game content.</summary>
    public static bool IsLoadedByGame(string relativePath)
    {
        var first = relativePath.IndexOf('/');
        if (first <= 0 || !Roots.Contains(relativePath[..first])) return false;

        var segments = relativePath.Split('/');
        if (segments.Any(s => s.Length == 0 || s[0] == '.')) return false;
        var name = segments[^1];
        var dot = name.LastIndexOf('.');
        return dot > 0 && Extensions.Contains(name[(dot + 1)..]);
    }

    /// <summary>The folder a file counts under: "common/technology" below common, the top folder elsewhere.</summary>
    public static string Domain(string relativePath)
    {
        var parts = relativePath.Split('/');
        return parts.Length >= 3 && parts[0].Equals("common", StringComparison.OrdinalIgnoreCase)
            ? "common/" + parts[1].ToLowerInvariant()
            : parts[0].ToLowerInvariant();
    }
}
