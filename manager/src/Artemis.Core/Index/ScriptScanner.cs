using System.Buffers;
using System.Text;

namespace Artemis.Core.Index;

public readonly record struct Def(string Name, int Line);

/// <summary>
/// Reads only the names conflicts are decided on, straight from the file's bytes: the top-level keys of a script file,
/// the id of each event, the keys of a localisation file. Blocks are skipped by counting braces, so no tree is built.
/// Never throws; a broken file gives whatever names came before the break.
/// </summary>
public static class ScriptScanner
{
    private static readonly SearchValues<byte> BlockMarks = SearchValues.Create("{}\"#"u8);
    private static readonly SearchValues<byte> WordEnd = SearchValues.Create(" \t\r\n={}\"#<>!?"u8);

    private const string ScriptedVariables = "common/scripted_variables";

    private static byte[] Field(string name) => Encoding.ASCII.GetBytes(name);

    private static Dictionary<string, byte[]> Wrap(string field, params string[] keys) =>
        keys.ToDictionary(k => k, _ => Field(field), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Folders whose top-level blocks all start with the same word and carry the real name inside ("weapon_component_template = { key = X ... }"), so the
    /// word is no name: the field that holds it, per word. An empty field means the blocks are not named definitions (a link between two planet classes).
    /// Read off the game's own files; a block with no such field (the template comes from an inline script) is left out, because its name can't be known.
    /// </summary>
    private static readonly Dictionary<string, Dictionary<string, byte[]>> Wrapped = new(StringComparer.OrdinalIgnoreCase)
    {
        ["common/scripted_loc"] = Wrap("name", "defined_text", "define_text"),
        ["common/component_templates"] = Wrap("key", "utility_component_template", "weapon_component_template", "strike_craft_component_template"),
        ["common/component_sets"] = Wrap("key", "component_set"),
        ["common/section_templates"] = Wrap("key", "ship_section_template"),
        ["common/special_projects"] = Wrap("key", "special_project"),
        ["common/message_types"] = Wrap("key", "message_type"),
        ["common/global_ship_designs"] = Wrap("name", "ship_design"),
        ["common/ship_behaviors"] = Wrap("name", "ship_behavior"),
        ["common/ambient_objects"] = Wrap("name", "ambient_object"),
        ["common/planet_classes"] = Wrap("name", "random_list"),
        ["common/terraform"] = Wrap("", "terraform_link"),
        ["common/start_screen_messages"] = Wrap("", "part"),
        ["common/random_names"] = new(StringComparer.OrdinalIgnoreCase), // by suffix, see WrapperField
    };

    /// <summary>Null: the key is the name. Empty: not a definition. Otherwise the field inside the block that holds the name.</summary>
    private static byte[]? WrapperField(string? domain, ReadOnlySpan<byte> key)
    {
        if (domain is null || !Wrapped.TryGetValue(domain, out var words)) return null; // only folders with wrappers pay for the string below
        var word = Encoding.UTF8.GetString(key);
        if (words.TryGetValue(word, out var field)) return field;
        if (!domain.Equals("common/random_names", StringComparison.OrdinalIgnoreCase)) return null;
        return word.EndsWith("_parts_list", StringComparison.OrdinalIgnoreCase) ? Field("key") : word.EndsWith("_format", StringComparison.OrdinalIgnoreCase) ? [] : null;
    }

    /// <summary>
    /// Top-level names of a script file. With <paramref name="events"/>, the ids of its *_event blocks instead. <paramref name="domain"/> is the folder the file counts
    /// under ("common/buildings"), which decides what a top-level word means there. Never a name, in any folder: <c>inline_script</c>, which is a statement that
    /// expands to definitions and not a definition itself. A <c>@variable</c> is a name only in common/scripted_variables; anywhere else it belongs to its file.
    /// </summary>
    public static List<Def> TopLevel(ReadOnlySpan<byte> text, bool events = false, string? domain = null)
    {
        var defs = new List<Def>();
        var lines = new LineCounter();
        var pos = StartAfterBom(text);
        while (true)
        {
            SkipBlank(text, ref pos);
            if (pos >= text.Length) break;
            var c = text[pos];
            if (c == '}') { pos++; continue; }
            if (c == '{') { pos = EndOfBlock(text, pos); continue; }

            var keyStart = pos;
            var keyEnd = ReadWord(text, ref pos);
            if (keyEnd <= keyStart) { pos++; continue; }
            SkipBlank(text, ref pos);
            if (pos >= text.Length || !IsOperator(text[pos])) continue; // a bare word defines nothing
            while (pos < text.Length && IsOperator(text[pos])) pos++;
            SkipBlank(text, ref pos);
            if (pos >= text.Length) break;

            if (text[keyStart] == '"') keyStart++;
            var key = text[keyStart..Math.Max(keyStart, keyEnd)];
            var named = !events && !(key.SequenceEqual("inline_script"u8) || (domain is not null && key.Length > 0 && key[0] == '@' && domain != ScriptedVariables));
            var wrapper = named ? WrapperField(domain, key) : null;
            if (text[pos] == '{')
            {
                var blockStart = pos;
                pos = EndOfBlock(text, pos);
                if (named && wrapper is null) defs.Add(new Def(Encoding.UTF8.GetString(key), lines.At(text, keyStart)));
                else if (wrapper is { Length: > 0 } && FindValue(text[blockStart..pos], wrapper) is { } inner)
                    defs.Add(new Def(Encoding.UTF8.GetString(text.Slice(blockStart + inner.Start, inner.Length)), lines.At(text, blockStart + inner.Start)));
                else if (events && EndsWithEvent(key) && FindValue(text[blockStart..pos], "id"u8) is { } id)
                    defs.Add(new Def(Encoding.UTF8.GetString(text.Slice(blockStart + id.Start, id.Length)), lines.At(text, blockStart + id.Start)));
            }
            else
            {
                var valueStart = pos;
                ReadWord(text, ref pos);
                if (pos == valueStart) pos++;
                if (named && wrapper is null) defs.Add(new Def(Encoding.UTF8.GetString(key), lines.At(text, keyStart)));
            }
        }
        return defs;
    }

    /// <summary>The language named by the file's "l_english:" header, and every key that has a quoted text.</summary>
    public static (string Language, List<Def> Keys) Localisation(ReadOnlySpan<byte> text)
    {
        var keys = new List<Def>();
        var language = "";
        var pos = StartAfterBom(text);
        for (var line = 1; pos < text.Length; line++)
        {
            var eol = text[pos..].IndexOf((byte)'\n');
            var row = eol < 0 ? text[pos..] : text.Slice(pos, eol);
            pos = eol < 0 ? text.Length : pos + eol + 1;

            var i = 0;
            while (i < row.Length && row[i] is (byte)' ' or (byte)'\t') i++;
            if (i >= row.Length || row[i] is (byte)'#' or (byte)'\r') continue;
            var colon = row[i..].IndexOf((byte)':');
            if (colon <= 0) continue;
            var key = row.Slice(i, colon);
            if (language.Length == 0 && key.Length > 2 && key[0] == 'l' && key[1] == '_') { language = Encoding.UTF8.GetString(key); continue; }

            // after the colon: an optional version number, then the quoted text
            var j = i + colon + 1;
            while (j < row.Length && row[j] is >= (byte)'0' and <= (byte)'9') j++;
            while (j < row.Length && row[j] is (byte)' ' or (byte)'\t') j++;
            if (j < row.Length && row[j] == '"') keys.Add(new Def(Encoding.UTF8.GetString(key), line));
        }
        return (language, keys);
    }

    private static int StartAfterBom(ReadOnlySpan<byte> t) => t.Length >= 3 && t[0] == 0xEF && t[1] == 0xBB && t[2] == 0xBF ? 3 : 0;

    private static bool IsOperator(byte b) => b is (byte)'=' or (byte)'<' or (byte)'>' or (byte)'!' or (byte)'?';

    private static void SkipBlank(ReadOnlySpan<byte> t, ref int pos)
    {
        while (pos < t.Length)
        {
            var b = t[pos];
            if (b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') pos++;
            else if (b == '#') pos = EndOfLine(t, pos);
            else return;
        }
    }

    private static int EndOfLine(ReadOnlySpan<byte> t, int pos)
    {
        var eol = t[pos..].IndexOf((byte)'\n');
        return eol < 0 ? t.Length : pos + eol + 1;
    }

    /// <summary>Moves past a word or quoted string and returns where its text ends (before the closing quote).</summary>
    private static int ReadWord(ReadOnlySpan<byte> t, ref int pos)
    {
        var start = pos;
        if (pos < t.Length && t[pos] == '"')
        {
            var i = pos + 1;
            while (i < t.Length && t[i] != '"') i += t[i] == '\\' && i + 1 < t.Length ? 2 : 1;   // a string may run over several lines
            pos = i < t.Length && t[i] == '"' ? i + 1 : i;
            return i;
        }
        var length = t[pos..].IndexOfAny(WordEnd);
        pos = length < 0 ? t.Length : pos + length;
        return pos;
    }

    /// <summary>Where the block opened at <paramref name="open"/> ends, skipping nested blocks, strings and comments.</summary>
    private static int EndOfBlock(ReadOnlySpan<byte> t, int open)
    {
        int depth = 1, pos = open + 1;
        while (pos < t.Length)
        {
            var k = t[pos..].IndexOfAny(BlockMarks);
            if (k < 0) return t.Length;
            pos += k;
            switch (t[pos])
            {
                case (byte)'{': depth++; pos++; break;
                case (byte)'}':
                    pos++;
                    if (--depth == 0) return pos;
                    break;
                case (byte)'#': pos = EndOfLine(t, pos); break;
                default: // a string ends at its closing quote, which may be lines later: an inline script's TRIGGER = "owner = { ... }" is a block in a string
                    pos++;
                    while (pos < t.Length && t[pos] != '"') pos += t[pos] == '\\' && pos + 1 < t.Length ? 2 : 1;
                    if (pos < t.Length && t[pos] == '"') pos++;
                    break;
            }
        }
        return t.Length;
    }

    private static bool EndsWithEvent(ReadOnlySpan<byte> key) =>
        key.Length > 6 && Ascii.EqualsIgnoreCase(key[^6..], "_event"u8);

    /// <summary>The value of "field = ..." directly inside a block (not in a block within it), as an offset and length into the block. The field's case doesn't matter.</summary>
    private static (int Start, int Length)? FindValue(ReadOnlySpan<byte> block, ReadOnlySpan<byte> field)
    {
        var pos = 1;
        var end = block.Length - 1;
        while (pos < end)
        {
            SkipBlank(block, ref pos);
            if (pos >= end || block[pos] == '}') break;
            if (block[pos] == '{') { pos = EndOfBlock(block, pos); continue; }

            var keyStart = pos;
            var keyEnd = ReadWord(block, ref pos);
            if (keyEnd <= keyStart) { pos++; continue; }
            SkipBlank(block, ref pos);
            if (pos >= block.Length || !IsOperator(block[pos])) continue;
            while (pos < block.Length && IsOperator(block[pos])) pos++;
            SkipBlank(block, ref pos);
            if (pos >= block.Length) break;

            if (block[pos] == '{') { pos = EndOfBlock(block, pos); continue; }
            var valueStart = pos;
            var valueEnd = ReadWord(block, ref pos);
            if (pos == valueStart) pos++;
            if (Ascii.EqualsIgnoreCase(block[keyStart..keyEnd], field))
            {
                var start = block[valueStart] == '"' ? valueStart + 1 : valueStart;
                return valueEnd > start ? (start, valueEnd - start) : null;
            }
        }
        return null;
    }

    /// <summary>Line numbers for increasing positions, counting only the text between one lookup and the next.</summary>
    private struct LineCounter
    {
        private int _pos;
        private int _line;

        public int At(ReadOnlySpan<byte> text, int position)
        {
            if (_line == 0) _line = 1;
            if (position > _pos)
            {
                _line += text[_pos..position].Count((byte)'\n');
                _pos = position;
            }
            return _line;
        }
    }
}
