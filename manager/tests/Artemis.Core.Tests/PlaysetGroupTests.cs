using Artemis.Core.Playsets;

namespace Artemis.Core.Tests;

public class PlaysetGroupTests
{
    private static PlaysetMod Mod(string key) => new() { Key = key };

    private static PlaysetMod Sep(string name, bool collapsed = false) => new() { Separator = name, Collapsed = collapsed };

    [Fact]
    public void A_collapsed_separator_hides_the_mods_below_it_down_to_the_next_separator()
    {
        var mods = new[] { Mod("a"), Sep("X", collapsed: true), Mod("b"), Mod("c"), Sep("Y"), Mod("d"), Mod("e") };
        var (hidden, size) = Playset.Groups(mods);
        Assert.Equal([false, false, true, true, false, false, false], hidden);
        Assert.Equal(2, size[1]);
        Assert.Equal(2, size[4]);
    }

    [Fact]
    public void Mods_above_the_first_separator_and_the_separators_themselves_are_never_hidden()
    {
        var mods = new[] { Mod("a"), Mod("b"), Sep("X", collapsed: true), Sep("Y", collapsed: true), Mod("c") };
        var (hidden, size) = Playset.Groups(mods);
        Assert.Equal([false, false, false, false, true], hidden);
        Assert.Equal([0, 0, 0, 1, 0], size);
    }

    [Fact]
    public void Nothing_is_hidden_without_a_collapsed_separator_or_in_an_empty_list()
    {
        Assert.DoesNotContain(true, Playset.Groups([Mod("a"), Sep("X"), Mod("b")]).Hidden);
        var (hidden, size) = Playset.Groups([]);
        Assert.Empty(hidden);
        Assert.Empty(size);
    }

    [Fact]
    public void The_collapsed_state_is_saved_only_when_set_and_survives_a_copy()
    {
        var p = new Playset { Mods = [Mod("a"), Sep("Open"), Sep("Folded", collapsed: true)] };
        var json = p.ToJson();
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(json, "\"collapsed\""));
        Assert.Equal([false, false, true], Playset.FromJson(json).Mods.Select(m => m.Collapsed));
        Assert.Equal([false, false, true], p.Clone().Mods.Select(m => m.Collapsed));
    }
}
