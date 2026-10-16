using Artemis.Core.Text;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Artemis.Core.Game;

public sealed record LauncherSettings(
    string Version,
    string RawVersion,
    string ModsCompatibilityVersion,
    string ExePath,
    IReadOnlyList<string> ExeArgs,
    string GameDataPath)
{
    public static LauncherSettings Read(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path),
            new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        var root = doc.RootElement;

        string Str(string name, string fallback = "") =>
            root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : fallback;

        var args = root.TryGetProperty("exeArgs", out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray().Select(x => x.GetString() ?? "").ToList()
            : [];

        return new LauncherSettings(Str("version"), Str("rawVersion"), Str("modsCompatibilityVersion"),
            Str("exePath", "./stellaris.exe"), args, Str("gameDataPath"));
    }
}

public sealed class GameInstall
{
    public required string GameDir { get; init; }
    public required string UserDir { get; init; }
    public required LauncherSettings Settings { get; init; }

    public string ExePath => Path.GetFullPath(Path.Combine(GameDir, Settings.ExePath));
    public string ModDir => Path.Combine(UserDir, "mod");
    public string DlcLoadPath => Path.Combine(UserDir, "dlc_load.json");

    public static GameInstall Open(string gameDir, string? userDirOverride = null)
    {
        var settingsPath = Path.Combine(gameDir, "launcher-settings.json");
        if (!File.Exists(settingsPath)) settingsPath = Path.Combine(gameDir, "launcher", "launcher-settings.json");
        if (!File.Exists(settingsPath))
            throw new FileNotFoundException(Loc.T("core2.game_settings_missing"), settingsPath);

        var settings = LauncherSettings.Read(settingsPath);
        return new GameInstall
        {
            GameDir = gameDir,
            UserDir = userDirOverride ?? ExpandDataPath(settings.GameDataPath),
            Settings = settings,
        };
    }

    public static string ExpandDataPath(string gameDataPath)
    {
        // MyDocuments follows folder redirection (OneDrive), which is where the game really writes.
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrWhiteSpace(gameDataPath)) return Path.Combine(docs, "Paradox Interactive", "Stellaris");
        return gameDataPath.Replace("%USER_DOCUMENTS%", docs, StringComparison.OrdinalIgnoreCase)
                           .Replace('/', Path.DirectorySeparatorChar);
    }
}

public static class GameLocator
{
    [SupportedOSPlatform("windows")]
    public static string? FindGameDir()
    {
        foreach (var library in SteamLibraries())
        {
            var dir = Path.Combine(library, "steamapps", "common", "Stellaris");
            if (File.Exists(Path.Combine(dir, "stellaris.exe"))) return dir;
        }
        return null;
    }

    /// <summary>Every Steam library folder: the Steam install itself plus those in libraryfolders.vdf.</summary>
    [SupportedOSPlatform("windows")]
    public static IEnumerable<string> SteamLibraries()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var roots = new List<string>();
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        foreach (var sub in new[] { @"Software\Valve\Steam", @"SOFTWARE\WOW6432Node\Valve\Steam" })
        {
            using var key = hive.OpenSubKey(sub);
            var path = (key?.GetValue("SteamPath") ?? key?.GetValue("InstallPath")) as string;
            if (!string.IsNullOrWhiteSpace(path)) roots.Add(Path.GetFullPath(path));
        }

        foreach (var root in roots)
        {
            if (seen.Add(root)) yield return root;
            var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) continue;
            foreach (var library in ParseLibraryFolders(File.ReadAllText(vdf)))
                if (seen.Add(library)) yield return library;
        }
    }

    public static IEnumerable<string> ParseLibraryFolders(string vdfText) =>
        Regex.Matches(vdfText, "\"path\"\\s+\"([^\"]+)\"")
             .Select(m => Path.GetFullPath(m.Groups[1].Value.Replace(@"\\", @"\")));
}
