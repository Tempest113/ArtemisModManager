using Artemis.Core.Text;

namespace Artemis.Core.Tests;

public class LocTests
{
    [Fact]
    public void Lines_are_read_with_comments_escapes_and_later_files_replacing_earlier_keys()
    {
        var text = new Dictionary<string, string>();
        Loc.Merge(text, "# note\r\na.b: \"one \\\"two\\\"\\nthree\"\r\n\r\nc: \"x: y\"\n", "first");
        Loc.Merge(text, "c: \"over\"\n", "second");
        Assert.Equal("one \"two\"\nthree", text["a.b"]);
        Assert.Equal("over", text["c"]);
    }

    [Fact]
    public void A_bad_line_is_reported_and_skipped_not_fatal()
    {
        var text = new Dictionary<string, string>();
        var before = Loc.Problems.Count;
        Loc.Merge(text, "good: \"ok\"\nno quotes here\nalso: bad\n", "f");
        Assert.Equal(["good"], text.Keys);
        Assert.Equal(2, Loc.Problems.Count - before);
    }

    [Fact]
    public void An_override_file_replaces_only_its_keys_and_missing_keys_show_as_the_key()
    {
        using var t = new TempDir();
        Loc.Load(t.File("en.loc", "mod.exists: \"A mod called {folder} is {state}.\"\n"));
        Assert.Equal("A mod called Foo is here.", Loc.T("mod.exists", ("folder", "Foo"), ("state", "here")));
        Assert.Equal("nope.key", Loc.T("nope.key"));
        Assert.Contains("nope.key", Loc.Missing);
        Loc.Load();   // leave the shared state as it was found
    }
}

public class LocKeyTests
{
    private static readonly string[] Roots = ["Artemis.App", "Artemis.Core"];

    private static string Src()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "manager", "src", "Artemis.Core", "Text", "en.loc"))) return Path.Combine(d.FullName, "manager", "src");
        throw new InvalidOperationException("manager/src not found above " + AppContext.BaseDirectory);
    }

    [Fact]
    public void Every_key_in_the_code_is_in_en_loc_and_every_key_in_en_loc_is_used_and_none_is_defined_twice()
    {
        var src = Src();
        var file = File.ReadAllText(Path.Combine(src, "Artemis.Core", "Text", "en.loc"));
        var defined = new Dictionary<string, string>();
        Loc.Merge(defined, file, "en.loc");
        var lines = file.Split('\n').Count(l => System.Text.RegularExpressions.Regex.IsMatch(l, @"^[a-z0-9_.]+: "));
        Assert.Equal(lines, defined.Count);   // a repeated key would silently replace the first

        var used = new HashSet<string>();
        foreach (var root in Roots)
            foreach (var f in Directory.EnumerateFiles(Path.Combine(src, root), "*.*", SearchOption.AllDirectories)
                         .Where(f => (f.EndsWith(".cs") || f.EndsWith(".xaml")) && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
            {
                var text = File.ReadAllText(f);
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, @"""((?:ui|win|vm|plug|core2?)\.[a-z0-9_]+)""|\{local:T ([a-z0-9_.]+)\}"))
                    used.Add(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
            }

        Assert.Empty(used.Except(defined.Keys).Order());
        Assert.Empty(defined.Keys.Except(used).Order());
    }
}
