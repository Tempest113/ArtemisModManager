using Artemis.Core.Index;

namespace Artemis.Core.Achievements;

/// <summary>One line of the game's checksum list: a folder, whether its subfolders count, and a file type.</summary>
public sealed record ChecksumRule(string Directory, bool SubDirectories, string Extension);

/// <summary>
/// The game's own list of which files its checksum covers (checksum_manifest.txt in the game folder). A mod that adds or changes
/// no such file leaves the checksum alone and stays achievement-compatible; one that does, does not. The list is read from the
/// player's install when Artemis starts and never copied anywhere.
/// </summary>
public sealed class ChecksumRules(IReadOnlyList<ChecksumRule> rules)
{
    public const string FileName = "checksum_manifest.txt";

    public IReadOnlyList<ChecksumRule> Rules => rules;

    /// <summary>The rules of the game at <paramref name="gameDir"/>, or null if it has no list (an older or different build).</summary>
    public static ChecksumRules? Load(string gameDir)
    {
        var file = Path.Combine(gameDir, FileName);
        return File.Exists(file) ? Parse(File.ReadAllText(file)) : null;
    }

    /// <summary>Blocks of "directory", "name = ...", "sub_directories = yes|no", "file_extension = ...". A block missing its name or extension is skipped.</summary>
    public static ChecksumRules Parse(string text)
    {
        var found = new List<ChecksumRule>();
        string? name = null, extension = null;
        var subs = false;

        void Finish()
        {
            if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(extension))
                found.Add(new ChecksumRule(name.Trim().Replace('\\', '/').Trim('/'), subs, extension.Trim()));
            (name, extension, subs) = (null, null, false);
        }

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            if (line == "directory") { Finish(); continue; }
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var value = line[(eq + 1)..].Trim().Trim('"');
            switch (line[..eq].Trim())
            {
                case "name": name = value; break;
                case "file_extension": extension = value; break;
                case "sub_directories": subs = value.Equals("yes", StringComparison.OrdinalIgnoreCase); break;
            }
        }
        Finish();
        return new ChecksumRules(found);
    }

    /// <summary>Whether a file, as a path with forward slashes relative to the mod or game folder, is covered.</summary>
    public bool Matches(string relativePath) =>
        rules.Any(r => relativePath.StartsWith(r.Directory + "/", StringComparison.OrdinalIgnoreCase)
                       && relativePath.EndsWith(r.Extension, StringComparison.OrdinalIgnoreCase)
                       && (r.SubDirectories || relativePath.IndexOf('/', r.Directory.Length + 1) < 0));
}

public static class AchievementCheck
{
    /// <summary>For each indexed mod that has files the checksum covers: those files. Mods that have none are left out.</summary>
    public static Dictionary<string, IReadOnlyList<string>> Scan(IndexStore store, IEnumerable<string> modKeys, ChecksumRules rules)
    {
        var changes = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in modKeys)
        {
            var covered = store.Paths(SourceKind.Mod, key).Where(rules.Matches).Order(StringComparer.OrdinalIgnoreCase).ToList();
            if (covered.Count > 0) changes[key] = covered;
        }
        return changes;
    }

    /// <summary>
    /// What a mod does to achievements. <paramref name="coveredFiles"/> are the files of the mod the checksum covers (null: not looked at yet). A plugin that counts
    /// toward the checksum (honest mode, D53: not cosmetic, not switched off) turns achievements off on its own, whatever the files say.
    /// </summary>
    public static AchievementVerdict Judge(IReadOnlyList<string>? coveredFiles, bool pluginCounts)
    {
        var reasons = new List<string>();
        if (coveredFiles is { Count: > 0 })
            reasons.Add($"adds or changes {coveredFiles.Count:N0} file{(coveredFiles.Count == 1 ? "" : "s")} the game's checksum covers, such as {coveredFiles[0]}");
        if (pluginCounts) reasons.Add("its plugin counts toward the game's checksum while it is switched on");
        if (reasons.Count > 0)
        {
            var text = string.Join(", and ", reasons);
            return new AchievementVerdict(AchievementState.Off, char.ToUpper(text[0]) + text[1..] + ". Achievements turn off.");
        }
        return coveredFiles is null
            ? new AchievementVerdict(AchievementState.Unknown, "")
            : new AchievementVerdict(AchievementState.On, "Adds or changes no file the game's checksum covers, so achievements stay on.");
    }
}

public enum AchievementState { Unknown, On, Off }

/// <summary>The state and the sentence that explains it, for a tooltip.</summary>
public sealed record AchievementVerdict(AchievementState State, string Tip);
