using System.Text;

namespace Artemis.Core.Pdx;

/// <summary>A node of a Paradox script file: a key with a value, a key with a block, or a bare value in a block.</summary>
public sealed class PdxNode
{
    public string? Key { get; init; }
    public string? Value { get; init; }
    public List<PdxNode>? Children { get; init; }

    public IEnumerable<PdxNode> Get(string key) =>
        Children?.Where(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase)) ?? [];

    public string? GetString(string key) => Get(key).FirstOrDefault()?.Value;

    public List<string> GetList(string key) =>
        Get(key).FirstOrDefault()?.Children?.Where(c => c.Key is null && c.Value is not null).Select(c => c.Value!).ToList() ?? [];
}

/// <summary>
/// A small tolerant reader for descriptor-sized files (.mod, descriptor.mod). It never throws on bad input.
/// Whole-game scanning uses a separate, faster scanner; this one builds a tree.
/// </summary>
public static class PdxScript
{
    public static PdxNode Parse(string text)
    {
        var tokens = Tokenize(text);
        var pos = 0;
        return new PdxNode { Children = Block(tokens, ref pos, topLevel: true) };
    }

    public static PdxNode ParseFile(string path) => Parse(File.ReadAllText(path));

    private enum Kind { Word, Quoted, Operator, Open, Close }

    private readonly record struct Token(Kind Kind, string Text);

    private static List<Token> Tokenize(string s)
    {
        var tokens = new List<Token>();
        int i = s.Length > 0 && s[0] == '﻿' ? 1 : 0, n = s.Length;
        while (i < n)
        {
            var c = s[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '#') { while (i < n && s[i] != '\n') i++; continue; }
            if (c == '{') { tokens.Add(new Token(Kind.Open, "{")); i++; continue; }
            if (c == '}') { tokens.Add(new Token(Kind.Close, "}")); i++; continue; }
            if (c == '"')
            {
                var sb = new StringBuilder();
                for (i++; i < n && s[i] != '"'; i++)
                {
                    if (s[i] == '\\' && i + 1 < n && s[i + 1] is '"' or '\\') i++;
                    sb.Append(s[i]);
                }
                i++; // closing quote, or past the end on an unterminated string
                tokens.Add(new Token(Kind.Quoted, sb.ToString()));
                continue;
            }
            if (c is '=' or '<' or '>' or '!' or '?')
            {
                var start = i++;
                if (i < n && s[i] == '=') i++;
                tokens.Add(new Token(Kind.Operator, s[start..i]));
                continue;
            }
            var wordStart = i;
            while (i < n && !char.IsWhiteSpace(s[i]) && s[i] is not ('{' or '}' or '=' or '"' or '#' or '<' or '>')) i++;
            if (i == wordStart) i++;
            else tokens.Add(new Token(Kind.Word, s[wordStart..i]));
        }
        return tokens;
    }

    private static List<PdxNode> Block(List<Token> t, ref int pos, bool topLevel)
    {
        var nodes = new List<PdxNode>();
        while (pos < t.Count)
        {
            var tok = t[pos++];
            switch (tok.Kind)
            {
                case Kind.Close:
                    if (topLevel) continue; // stray '}' at top level
                    return nodes;
                case Kind.Open:
                    nodes.Add(new PdxNode { Children = Block(t, ref pos, false) });
                    continue;
                case Kind.Operator:
                    continue; // dangling operator
            }

            if (pos < t.Count && t[pos].Kind == Kind.Operator)
            {
                pos++;
                if (pos < t.Count && t[pos].Kind == Kind.Open)
                {
                    pos++;
                    nodes.Add(new PdxNode { Key = tok.Text, Children = Block(t, ref pos, false) });
                }
                else if (pos < t.Count && t[pos].Kind is Kind.Word or Kind.Quoted)
                {
                    nodes.Add(new PdxNode { Key = tok.Text, Value = t[pos++].Text });
                }
                else
                {
                    nodes.Add(new PdxNode { Key = tok.Text, Value = "" });
                }
            }
            else
            {
                nodes.Add(new PdxNode { Value = tok.Text });
            }
        }
        return nodes;
    }
}
