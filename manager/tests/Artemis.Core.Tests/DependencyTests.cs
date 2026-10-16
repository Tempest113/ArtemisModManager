using Artemis.Core.Mods;

namespace Artemis.Core.Tests;

public class DependencyTests
{
    private static ModInfo Mod(string key, params string[] listed) =>
        new(key, key.ToUpperInvariant(), ModSource.Local, "", null, null, null, null, null, null, [], listed, false);

    private static readonly Dictionary<string, ModInfo> Installed = new(StringComparer.OrdinalIgnoreCase) { ["BASE"] = Mod("base") };

    private static DependencyStatus One(ModInfo mod, int position, params (string Key, int Index, bool Enabled)[] playset) =>
        Assert.Single(DependencyCheck.For(mod, Installed, playset.ToDictionary(p => p.Key, p => (p.Index, p.Enabled)), position));

    [Fact]
    public void A_listed_mod_above_this_one_is_fine()
    {
        Assert.True(One(Mod("child", "BASE"), position: 3, ("base", 1, true)).Ok);
    }

    [Fact]
    public void A_listed_mod_that_loads_below_is_the_only_problem()
    {
        var status = One(Mod("child", "BASE"), position: 3, ("base", 5, true));
        Assert.False(status.Ok);
        Assert.Contains("below", status.Text);
    }

    [Fact]
    public void Listed_mods_that_are_missing_absent_or_off_are_never_problems()
    {
        var child = Mod("child", "BASE");
        Assert.True(One(child, 2, ("base", 1, false)).Ok);     // switched off: not loaded, so no order to get wrong
        Assert.True(One(child, 2).Ok);                         // installed, not in the playset
        Assert.True(One(child, -1, ("base", 1, true)).Ok);     // this mod isn't in the playset: no order to judge

        var missing = One(Mod("child", "Nope"), 2);            // the descriptor lists it; it still works without it
        Assert.True(missing.Ok);
        Assert.Equal("Not installed", missing.Text);
    }

    [Fact]
    public void A_mod_without_a_list_has_nothing_to_check()
    {
        Assert.Empty(DependencyCheck.For(Mod("solo"), Installed, new Dictionary<string, (int, bool)>(), 0));
    }
}
