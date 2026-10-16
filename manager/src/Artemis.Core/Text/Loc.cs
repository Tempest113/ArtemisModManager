using System.Text;

namespace Artemis.Core.Text;

/// <summary>
/// User-facing text by key. The defaults are embedded (en.loc); a file next to the exe with the same format replaces
/// them key by key, so it can hold only the lines you changed. Format: <c>key: "text"</c>, <c>#</c> starts a comment,
/// <c>{name}</c> is filled in by the caller, <c>\"</c> <c>\n</c> <c>\\</c> are escapes.
/// </summary>
public static class Loc
{
    /// <summary>Lines of the loaded files that were skipped, for the user to see.</summary>
    public static List<string> Problems { get; } = [];

    /// <summary>Keys asked for that no file defines; the key itself is shown instead so the gap is visible.</summary>
    public static HashSet<string> Missing { get; } = [];

    private static Dictionary<string, string> _text = Defaults();   // usable before Load, e.g. in tests

    /// <summary>Call once at startup, before any text is asked for.</summary>
    public static void Load(string? overridePath = null)
    {
        Problems.Clear();
        Missing.Clear();
        var text = Defaults();
        if (overridePath != null && File.Exists(overridePath))
        {
            try { Merge(text, File.ReadAllText(overridePath), Path.GetFileName(overridePath)); }
            catch (IOException e) { Problems.Add($"{overridePath}: {e.Message}"); }
        }
        _text = text;
    }

    private static Dictionary<string, string> Defaults()
    {
        var text = new Dictionary<string, string>();
        using var s = typeof(Loc).Assembly.GetManifestResourceStream("en.loc");
        if (s != null) Merge(text, new StreamReader(s, Encoding.UTF8).ReadToEnd(), "en.loc (built in)");
        return text;
    }

    public static string T(string key, params (string Name, object? Value)[] args)
    {
        if (!_text.TryGetValue(key, out var s)) { lock (Missing) Missing.Add(key); return key; }
        foreach (var (name, value) in args) s = s.Replace("{" + name + "}", value?.ToString());
        return s;
    }

    /// <summary>Adds the file's entries to <paramref name="into"/>, later ones replacing earlier; bad lines go to <see cref="Problems"/>.</summary>
    public static void Merge(Dictionary<string, string> into, string file, string source)
    {
        var n = 0;
        foreach (var raw in file.Split('\n'))
        {
            n++;
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            var colon = line.IndexOf(':');
            var rest = colon > 0 ? line[(colon + 1)..].Trim() : "";
            if (rest.Length < 2 || rest[0] != '"' || rest[^1] != '"') { Problems.Add($"{source} line {n}: expected key: \"text\""); continue; }
            into[line[..colon].Trim()] = Unescape(rest[1..^1]);
        }
    }

    private static string Unescape(string s)
    {
        if (!s.Contains('\\')) return s;
        var b = new StringBuilder();
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length)
                b.Append(s[++i] switch { 'n' => '\n', var c => c });   // \" and \\ stay as the character itself
            else b.Append(s[i]);
        }
        return b.ToString();
    }
}
