using Artemis.Core.Plugins;

namespace Artemis.Core.Game;

/// <summary>A log that nothing needs any more, and why.</summary>
public sealed record StaleLog(string File, long Bytes, string Reason);

/// <summary>
/// Finds the logs of the game and of Orion that are safe to put away, and sends them to the Recycle Bin. What counts as still needed:
/// anything the game's latest run wrote, Orion's own log, and the log of every installed plugin, in a playset or not. Only what is clearly left over is offered.
/// </summary>
public static class LogCleanup
{
    /// <summary>A game log this much older than the game's newest log is a leftover from some other run or tool. Measured against the newest log, not today, so a long break from playing never makes the last run's logs look old.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(30);

    public static IReadOnlyList<StaleLog> Find(string userDir, IEnumerable<string> installedPluginKeys)
    {
        var found = new List<StaleLog>();
        void Add(FileInfo f, string reason) => found.Add(new StaleLog(f.FullName, f.Length, reason));

        var game = Directory.Exists(Path.Combine(userDir, "logs")) ? Logs(Path.Combine(userDir, "logs")) : [];
        if (game.Count > 0)
        {
            var latest = game.Max(f => f.LastWriteTimeUtc);
            foreach (var f in game.Where(f => latest - f.LastWriteTimeUtc > StaleAfter)) Add(f, "not from the game's latest run");
        }

        var orion = OrionFiles.FolderIn(userDir);
        if (Directory.Exists(orion))
            foreach (var f in Logs(orion).Where(f => f.Name.Equals("modloader.log", StringComparison.OrdinalIgnoreCase))) Add(f, "written by an earlier loader, not by Orion");
        var plugins = Path.Combine(orion, "plugins");
        if (Directory.Exists(plugins))
        {
            var installed = installedPluginKeys.Select(SafeFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var f in Logs(plugins).Where(f => !installed.Contains(Path.GetFileNameWithoutExtension(f.Name)))) Add(f, "its plugin is not installed any more");
        }
        return found;
    }

    /// <summary>
    /// Sends the logs to <paramref name="remove"/> (the Recycle Bin, <see cref="IO.RecycleBin.Send"/>). Each is looked at again first and one that is no longer a
    /// leftover (the game wrote to it, a plugin was installed) is left alone, as is one that can't be removed (the game has it open). Returns how many went.
    /// </summary>
    public static int Recycle(string userDir, IEnumerable<string> installedPluginKeys, IEnumerable<StaleLog> logs, Action<string> remove)
    {
        var still = Find(userDir, installedPluginKeys).Select(s => s.File).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removed = 0;
        foreach (var log in logs.Where(l => still.Contains(l.File)))
        {
            try
            {
                remove(log.File);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException) { }
        }
        return removed;
    }

    /// <summary>The name Orion gives a plugin's log (<c>SafeFileName</c> in orion/src/core/core.cpp): the mod's key, with characters a file name can't have replaced.</summary>
    public static string SafeFileName(string key)
    {
        var name = new string(key.Select(c => c < ' ' || "\\/:*?\"<>|".Contains(c) ? '_' : c).ToArray()).TrimEnd('.', ' ');
        return name.Length == 0 ? "_" : name;
    }

    private static List<FileInfo> Logs(string folder) => new DirectoryInfo(folder).GetFiles("*.log").Where(f => f.Length > 0).ToList();
}
