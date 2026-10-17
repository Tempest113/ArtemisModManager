using Artemis.Core.Playsets;

namespace Artemis.Core.Tests;

public class PlaysetHistoryTests
{
    private static Playset Of(params string[] keys) => new() { Mods = keys.Select(k => new PlaysetMod { Key = k }).ToList() };
    private static string Keys(Playset p) => string.Join(',', p.Mods.Select(m => m.Separator is { } s ? $"[{s}]" : m.Key + (m.Enabled ? "" : "-")));

    [Fact]
    public void Undo_and_redo_walk_back_and_forward()
    {
        var p = Of("a", "b");
        var h = new PlaysetHistory();
        h.Reset(p);

        p.Add("c"); h.Record(p);
        p.Mods[0].Enabled = false; h.Record(p);
        p.Mods.Insert(1, new PlaysetMod { Separator = "Fixes" }); h.Record(p);
        Assert.Equal("a-,[Fixes],b,c", Keys(p));

        Assert.True(h.Undo(p)); Assert.Equal("a-,b,c", Keys(p));
        Assert.True(h.Undo(p)); Assert.Equal("a,b,c", Keys(p));
        Assert.True(h.Undo(p)); Assert.Equal("a,b", Keys(p));
        Assert.False(h.Undo(p));
        Assert.True(h.Redo(p)); Assert.Equal("a,b,c", Keys(p));
        Assert.True(h.Redo(p)); Assert.True(h.Redo(p));
        Assert.Equal("a-,[Fixes],b,c", Keys(p));
        Assert.False(h.Redo(p));
    }

    [Fact]
    public void A_change_that_changes_nothing_is_not_a_step()
    {
        var p = Of("a");
        var h = new PlaysetHistory();
        h.Reset(p);
        h.Record(p);
        Assert.False(h.CanUndo);
    }

    [Fact]
    public void A_new_change_after_an_undo_drops_the_redo()
    {
        var p = Of("a");
        var h = new PlaysetHistory();
        h.Reset(p);
        p.Add("b"); h.Record(p);
        h.Undo(p);
        Assert.True(h.CanRedo);
        p.Add("c"); h.Record(p);
        Assert.False(h.CanRedo);
        Assert.Equal("a,c", Keys(p));
    }

    [Fact]
    public void Undo_and_redo_bring_back_a_mods_plugin_switch_and_a_groups_fold()
    {
        var p = new Playset { Mods = [new PlaysetMod { Key = "a", PluginOff = true }, new PlaysetMod { Separator = "Group", Collapsed = true }, new PlaysetMod { Key = "b" }] };
        var h = new PlaysetHistory();
        h.Reset(p);
        p.Add("c"); h.Record(p);
        p.Mods.Reverse(); h.Record(p);

        Assert.True(h.Undo(p));
        Assert.Equal("a,[Group],b,c", Keys(p));
        Assert.True(p.Mods.Single(m => m.Key == "a").PluginOff);
        Assert.True(p.Mods.Single(m => m.IsSeparator).Collapsed);

        Assert.True(h.Redo(p));
        Assert.True(p.Mods.Single(m => m.Key == "a").PluginOff);
        Assert.True(p.Mods.Single(m => m.IsSeparator).Collapsed);
    }

    [Fact]
    public void Reset_forgets_everything_and_the_history_is_capped()
    {
        var p = Of("a");
        var h = new PlaysetHistory();
        h.Reset(p);
        for (var i = 0; i < 150; i++) { p.Add("m" + i); h.Record(p); }
        var steps = 0;
        while (h.Undo(p)) steps++;
        Assert.Equal(100, steps);

        h.Reset(p);
        Assert.False(h.CanUndo);
    }
}
