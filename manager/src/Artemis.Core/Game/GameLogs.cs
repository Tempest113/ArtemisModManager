using System.Text;
using System.Text.RegularExpressions;

namespace Artemis.Core.Game;

/// <summary>The game's own log files, read-only (see <see cref="LogTail"/> for reading one).</summary>
public static class GameLogs
{
    /// <summary>Non-empty .log files of the game's logs folder, the two that matter most first.</summary>
    public static IReadOnlyList<string> List(string userDir)
    {
        var dir = Path.Combine(userDir, "logs");
        if (!Directory.Exists(dir)) return [];
        return Directory.EnumerateFiles(dir, "*.log")
            .Where(f => new FileInfo(f).Length > 0)
            .OrderBy(f => Path.GetFileName(f) switch { "error.log" => 0, "game.log" => 1, _ => 2 })
            .ThenBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static readonly Regex EntryStart = new(@"^\[\d\d:\d\d:\d\d\]", RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>
    /// The game writes an entry per "[hh:mm:ss][file:line]: message" line, but a message may contain line breaks of its own. This
    /// joins such continuation lines to their entry, so every entry is one line. A text with no such entries is returned as it is.
    /// </summary>
    public static string OneLinePerEntry(string text)
    {
        if (!EntryStart.IsMatch(text)) return text;
        var entries = new List<StringBuilder>();
        var fresh = true;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (fresh || EntryStart.IsMatch(line))
            {
                entries.Add(new StringBuilder(line));
                fresh = line == "…"; // the mark of a cut tail: whatever follows starts a new entry
            }
            else if (line.Length > 0) entries[^1].Append(char.IsLetterOrDigit(line[0]) ? " " : "").Append(line);
        }
        return string.Join('\n', entries);
    }
}
