using Artemis.Core.Conflicts;
using Artemis.Core.Game;
using Artemis.Core.Index;
using Artemis.Core.Mods;

namespace Artemis.Core.Tests;

public class ConflictTests : IDisposable
{
    private readonly TempDir _t = new();
    private readonly IndexStore _store;
    private readonly List<ModInfo> _installed = [];
    private readonly GameInstall _game;

    public ConflictTests()
    {
        _store = new IndexStore(Path.Combine(_t.Path, "cache", "index.db"));
        _game = new GameInstall
        {
            GameDir = _t.Dir("game"),
            UserDir = _t.Path,
            Settings = new LauncherSettings("4.5", "4.5", "", "./stellaris.exe", [], ""),
        };
    }

    public void Dispose() => _t.Dispose();

    private void Game(string path, string text) => _t.File("game/" + path, text);

    private void Mod(string key, string path, string text) => _t.File($"mods/{key}/{path}", text);

    private ModInfo Add(string key, string name, params string[] dependencies)
    {
        var mod = new ModInfo(key, name, ModSource.Local, "", _t.Dir("mods/" + key), null, "1", null, null, null, [], dependencies, false);
        _installed.Add(mod);
        return mod;
    }

    /// <summary>Indexes everything installed, then looks for conflicts among the named mods in that order.</summary>
    private ConflictReport Find(params string[] order)
    {
        new Indexer(_store).Update(_installed, _game);
        var mods = order.Select(k => _installed.Single(m => m.Key == k)).Select(m => new ConflictMod(m.Key, m.Name, m.Dependencies)).ToList();
        return ConflictEngine.Find(_store, mods, Indexer.GameBuildKey(_game));
    }

    private static string[] Entries(Conflict c) => c.Entries.Select(e => e.SourceName).ToArray();

    [Fact]
    public void Inline_script_statements_in_different_files_are_not_a_conflict_but_the_same_template_key_is()
    {
        Add("a", "Mod A"); Add("b", "Mod B");
        Mod("a", "common/buildings/a.txt", "inline_script = { script = buildings/gen PARAM = a }\nbuilding_a = { }");
        Mod("b", "common/buildings/b.txt", "inline_script = { script = buildings/gen PARAM = b }\nbuilding_b = { }");
        Mod("a", "common/component_templates/a.txt", "weapon_component_template = { key = \"LASER_1\" }\nweapon_component_template = { key = \"LASER_A\" }");
        Mod("b", "common/component_templates/b.txt", "weapon_component_template = { key = \"LASER_1\" }\nweapon_component_template = { key = \"LASER_B\" }");

        var conflict = Assert.Single(Find("a", "b").Conflicts);
        Assert.Equal(("common/component_templates", "LASER_1"), (conflict.Domain, conflict.Name));
    }

    [Fact]
    public void Two_mods_with_the_same_file_path_conflict_and_the_later_one_wins()
    {
        Add("a", "Mod A"); Add("b", "Mod B");
        Mod("a", "gfx/interface/icon.dds", "x");
        Mod("b", "gfx/interface/icon.dds", "y");
        Mod("a", "descriptor.mod", "name=A");   // loose files at the root are never game content
        Mod("b", "descriptor.mod", "name=B");

        var conflict = Assert.Single(Find("a", "b").Conflicts);
        Assert.Equal(ConflictKind.File, conflict.Kind);
        Assert.Equal("gfx", conflict.Domain);
        Assert.Equal(["Mod A", "Mod B"], Entries(conflict));
        Assert.Equal("Mod B", conflict.WinnerEntry.SourceName);
    }

    [Fact]
    public void The_order_of_the_mods_decides_a_whole_file_conflict()
    {
        Add("a", "Mod A"); Add("b", "Mod B");
        Mod("a", "gfx/x.dds", "1");
        Mod("b", "gfx/x.dds", "2");
        Assert.Equal("Mod A", Assert.Single(Find("b", "a").Conflicts).WinnerEntry.SourceName);
    }

    [Fact]
    public void A_mod_replacing_a_game_file_conflicts_with_the_game_and_wins()
    {
        Add("a", "Mod A");
        Game("interface/main.gui", "g");
        Mod("a", "interface/main.gui", "m");
        var conflict = Assert.Single(Find("a").Conflicts);
        Assert.True(conflict.InvolvesGame);
        Assert.Equal([ConflictEngine.GameName, "Mod A"], Entries(conflict));
        Assert.Equal(1, conflict.Winner);
        Assert.Contains("game's", conflict.Why);
    }

    [Fact]
    public void In_a_last_wins_folder_the_file_that_sorts_last_wins_even_if_its_mod_loads_first()
    {
        Add("a", "Mod A"); Add("b", "Mod B");
        Mod("a", "common/technology/zz_patch.txt", "tech_one = { }");
        Mod("b", "common/technology/aa_tech.txt", "tech_one = { }");

        var conflict = Assert.Single(Find("a", "b").Conflicts);
        Assert.Equal(ConflictKind.Definition, conflict.Kind);
        Assert.Equal(("common/technology", "tech_one"), (conflict.Domain, conflict.Name));
        Assert.Equal("Mod A", conflict.WinnerEntry.SourceName);
        Assert.Equal("common/technology/zz_patch.txt", conflict.WinnerEntry.Path);
        Assert.Equal("Technology definitions use last in, only served.", conflict.Why);
    }

    [Fact]
    public void In_a_first_wins_folder_the_file_that_sorts_first_wins()
    {
        Add("a", "Mod A"); Add("b", "Mod B");
        Mod("a", "common/scripted_variables/zz.txt", "@cost = 1");
        Mod("b", "common/scripted_variables/aa.txt", "@cost = 2");

        var conflict = Assert.Single(Find("a", "b").Conflicts);
        Assert.Equal(MergeRule.FirstWins, conflict.Rule);
        Assert.Equal("Mod B", conflict.WinnerEntry.SourceName);
        Assert.Equal("Scripted variables definitions use first in, only served.", conflict.Why);
    }

    [Fact]
    public void Files_with_the_same_name_in_different_folders_are_settled_by_load_order()
    {
        Add("a", "Mod A"); Add("b", "Mod B");
        Mod("a", "common/technology/one/tech.txt", "tech_one = { }");
        Mod("b", "common/technology/two/tech.txt", "tech_one = { }");
        var conflict = Assert.Single(Find("a", "b").Conflicts);
        Assert.Equal("Mod B", conflict.WinnerEntry.SourceName);
        Assert.Contains("file names match", conflict.Why);
    }

    [Fact]
    public void A_replaced_file_no_longer_counts_so_only_files_the_game_reads_conflict()
    {
        Add("a", "Mod A"); Add("b", "Mod B"); Add("c", "Mod C");
        Mod("a", "common/technology/x.txt", "tech_a = { }");
        Mod("b", "common/technology/x.txt", "tech_b = { }");   // replaces Mod A's file entirely
        Mod("c", "common/technology/y.txt", "tech_a = { }");

        var report = Find("a", "b", "c");
        Assert.Equal([ConflictKind.File], report.Conflicts.Select(c => c.Kind));   // the name tech_a is not a conflict: A's file is never read
    }

    [Fact]
    public void A_mod_redefining_a_game_name_conflicts_with_the_game()
    {
        Add("a", "Mod A");
        Game("common/technology/00_tech.txt", "tech_one = { }\ntech_two = { }\n");
        Mod("a", "common/technology/a.txt", "tech_one = { }");

        var conflict = Assert.Single(Find("a").Conflicts);
        Assert.Equal("tech_one", conflict.Name);
        Assert.Equal([ConflictEngine.GameName, "Mod A"], Entries(conflict));
        Assert.Equal("Mod A", conflict.WinnerEntry.SourceName);   // 'a.txt' sorts after '00_tech.txt'
        Assert.Equal(1, conflict.ModCount);
    }

    [Fact]
    public void In_a_first_wins_folder_a_game_file_named_00_beats_a_mod()
    {
        Add("a", "Mod A");
        Game("events/00_events.txt", "namespace = x\ncountry_event = { id = x.1 }\n");
        Mod("a", "events/mod_events.txt", "namespace = x\ncountry_event = { id = x.1 }\n");
        var conflict = Assert.Single(Find("a").Conflicts);
        Assert.Equal(("events", "x.1"), (conflict.Domain, conflict.Name));
        Assert.True(conflict.WinnerEntry.IsGame);
    }

    [Fact]
    public void Merged_folders_and_names_a_mod_repeats_inside_itself_are_not_conflicts()
    {
        Add("a", "Mod A"); Add("b", "Mod B");
        Mod("a", "common/on_actions/a.txt", "on_game_start = { }");
        Mod("b", "common/on_actions/b.txt", "on_game_start = { }");
        Mod("a", "common/defines/a.txt", "NGameplay = { }");
        Mod("b", "common/defines/b.txt", "NGameplay = { }");
        Mod("a", "common/technology/one.txt", "tech_x = { }");
        Mod("a", "common/technology/two.txt", "tech_x = { }");
        Assert.Empty(Find("a", "b").Conflicts);
    }

    [Fact]
    public void Localisation_keys_conflict_per_language_and_a_replace_folder_wins()
    {
        Add("a", "Mod A"); Add("b", "Mod B");
        Mod("a", "localisation/english/replace/a_l_english.yml", "l_english:\n key_x:0 \"from A\"\n");
        Mod("b", "localisation/english/b_l_english.yml", "l_english:\n key_x:0 \"from B\"\n");
        Mod("b", "localisation/german/b_l_german.yml", "l_german:\n key_x:0 \"von B\"\n");

        var conflict = Assert.Single(Find("a", "b").Conflicts);
        Assert.Equal((ConflictKind.Text, "localisation/l_english", "key_x"), (conflict.Kind, conflict.Domain, conflict.Name));
        Assert.Equal("Mod A", conflict.WinnerEntry.SourceName);
        Assert.Contains("replace folder", conflict.Why);
    }

    [Fact]
    public void English_text_conflicts_are_listed_before_other_languages()
    {
        Add("a", "Mod A"); Add("b", "Mod B");
        foreach (var language in new[] { "braz_por", "english" })
        {
            Mod("a", $"localisation/{language}/a_l_{language}.yml", $"l_{language}:\n key_x:0 \"A\"\n");
            Mod("b", $"localisation/{language}/b_l_{language}.yml", $"l_{language}:\n key_x:0 \"B\"\n");
        }
        Assert.Equal(["localisation/l_english", "localisation/l_braz_por"], Find("a", "b").Conflicts.Select(c => c.Domain));
    }

    [Fact]
    public void A_winner_that_asks_to_load_after_the_others_is_marked_intended()
    {
        Add("base", "Base Mod"); Add("patch", "Patch Mod", "Base Mod"); Add("other", "Other Mod");
        Mod("base", "common/technology/a.txt", "tech_x = { }");
        Mod("patch", "common/technology/b.txt", "tech_x = { }");
        Mod("other", "common/technology/c.txt", "tech_y = { }");

        Assert.True(Assert.Single(Find("base", "patch", "other").Conflicts).Intended);

        Mod("other", "common/technology/c.txt", "tech_x = { }");
        Assert.False(Assert.Single(Find("base", "patch", "other").Conflicts).Intended);   // Other Mod is not one it asks to follow
    }

    [Fact]
    public void Mods_that_are_not_indexed_are_reported_and_left_out()
    {
        Add("a", "Mod A");
        Mod("a", "common/technology/a.txt", "tech_x = { }");
        new Indexer(_store).Update(_installed, _game);
        var report = ConflictEngine.Find(_store, [new ConflictMod("a", "Mod A", []), new ConflictMod("new", "New Mod", [])], null);
        Assert.Equal(["New Mod"], report.NotIndexed);
        Assert.Empty(report.Conflicts);
    }
}
