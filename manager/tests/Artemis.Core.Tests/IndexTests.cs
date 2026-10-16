using System.IO.Compression;
using System.Text;
using Artemis.Core.Game;
using Artemis.Core.Index;
using Artemis.Core.Mods;
using Microsoft.Data.Sqlite;

namespace Artemis.Core.Tests;

public class ScriptScannerTests
{
    private static List<Def> Top(string text, bool events = false) => ScriptScanner.TopLevel(Encoding.UTF8.GetBytes(text), events);

    [Fact]
    public void Finds_top_level_names_and_skips_what_is_inside_blocks_strings_and_comments()
    {
        var defs = Top("""
            # a comment with { a brace
            @base_cost = 100
            tech_one = {
                cost = @base_cost
                prerequisites = { "tech_hidden" }
                name = "a } brace in text"
            }
            "tech_two" = { }
            bare_word
            tech_three = value # trailing
            """);
        Assert.Equal(["@base_cost", "tech_one", "tech_two", "tech_three"], defs.Select(d => d.Name));
        Assert.Equal([2, 3, 8, 10], defs.Select(d => d.Line));
    }

    private static List<Def> TopIn(string domain, string text) => ScriptScanner.TopLevel(Encoding.UTF8.GetBytes(text), false, domain);

    [Fact]
    public void An_inline_script_statement_is_never_a_name_in_any_folder()
    {
        var text = """
            inline_script = { script = buildings/gen_a PARAM = 1 }
            inline_script = "buildings/gen_b"
            real_building = { cost = 1 }
            """;
        Assert.Equal(["real_building"], TopIn("common/buildings", text).Select(d => d.Name));
        Assert.Equal(["real_building"], Top(text).Select(d => d.Name));
    }

    [Fact]
    public void A_string_that_runs_over_several_lines_does_not_upset_the_braces_after_it()
    {
        // the game's own buildings do this: an inline script's parameter is a block of script inside quotes
        var defs = TopIn("common/buildings", "building_a = {\n\tinline_script = {\n\t\tscript = x\n\t\tTRIGGER = \"owner? = {\n\t\t\tflag = yes\n\t\t}\"\n\t\tAMOUNT = @n\n\t}\n\tcost = 1\n}\nbuilding_b = { }\n");
        Assert.Equal(["building_a", "building_b"], defs.Select(d => d.Name));
        Assert.Equal([1, 11], defs.Select(d => d.Line));
        Assert.Equal(["a", "b"], Top("a = \"one\ntwo\" b = 1").Select(d => d.Name));
    }

    [Fact]
    public void A_wrapper_block_is_named_by_its_key_or_name_field_not_by_its_word()
    {
        var defs = TopIn("common/component_templates", """
            weapon_component_template = { size = small  key = "LASER_1"  nested = { key = "inner" } }
            utility_component_template = { KEY = "SHIELD_1" }
            utility_component_template = { inline_script = { script = x } }
            ordinary_name = { }
            """);
        Assert.Equal(["LASER_1", "SHIELD_1", "ordinary_name"], defs.Select(d => d.Name));
        Assert.Equal([1, 2, 4], defs.Select(d => d.Line));
        Assert.Equal(["GetFoo"], TopIn("common/scripted_loc", "defined_text = { name = GetFoo text = { } }").Select(d => d.Name));
    }

    [Fact]
    public void Random_names_are_named_by_key_for_part_lists_and_formats_are_left_out()
    {
        var defs = TopIn("common/random_names", "empire_name_format = { x = 1 }  federation_name_parts_list = { key = fed_parts }  war_name_format = { }");
        Assert.Equal(["fed_parts"], defs.Select(d => d.Name));
    }

    [Fact]
    public void A_scripted_variable_is_a_name_only_in_its_own_folder_because_elsewhere_it_belongs_to_its_file()
    {
        const string text = "@distance = 5\nthing = { }";
        Assert.Equal(["@distance", "thing"], TopIn("common/scripted_variables", text).Select(d => d.Name));
        Assert.Equal(["thing"], TopIn("common/solar_system_initializers", text).Select(d => d.Name));
        Assert.Equal(["@distance", "thing"], Top(text).Select(d => d.Name));
    }

    [Fact]
    public void A_byte_order_mark_and_stray_braces_do_no_harm()
    {
        var defs = ScriptScanner.TopLevel([0xEF, 0xBB, 0xBF, .. "} a = { } } b = 1"u8.ToArray()]);
        Assert.Equal(["a", "b"], defs.Select(d => d.Name));
    }

    [Fact]
    public void Events_are_named_by_their_id_and_the_namespace_is_not_one()
    {
        var defs = Top("""
            namespace = my
            country_event = { id = my.1  title = x  option = { id = inner } }
            planet_event = { title = y id = "my.2" }
            not_an_ev = { id = other }
            """, events: true);
        Assert.Equal(["my.1", "my.2"], defs.Select(d => d.Name));
    }

    [Fact]
    public void Localisation_gives_the_language_and_every_key_with_text()
    {
        var (language, keys) = ScriptScanner.Localisation(Encoding.UTF8.GetBytes(
            "﻿l_english:\n # comment\n key_a:0 \"Text A\"\n key_b: \"Text B\"\n no_text:0 \n\n key_c:12 \"x: y\"\r\n"));
        Assert.Equal("l_english", language);
        Assert.Equal(["key_a", "key_b", "key_c"], keys.Select(k => k.Name));
        Assert.Equal([3, 4, 7], keys.Select(k => k.Line));
    }

    [Theory]
    [InlineData("a = {")]
    [InlineData("= = = {")]
    [InlineData("\"unterminated = { x")]
    [InlineData("a = \"open")]
    [InlineData("")]
    public void Never_throws_on_broken_input(string text)
    {
        Top(text);
        Top(text, events: true);
        ScriptScanner.Localisation(Encoding.UTF8.GetBytes(text));
    }
}

public class IndexTests
{
    private static ModInfo Mod(string key, string dir) =>
        new(key, key, ModSource.Local, "", dir, null, "1.0", null, null, null, [], [], false);

    private static GameInstall Game(string dir, string version = "4.5.2") => new()
    {
        GameDir = dir,
        UserDir = dir,
        Settings = new LauncherSettings(version, version, "", "./stellaris.exe", [], ""),
    };

    private static long Count(IndexStore store, string sql)
    {
        using var db = store.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        return (long)cmd.ExecuteScalar()!;
    }

    private static (TempDir Tmp, IndexStore Store, Indexer Indexer, string A, string B, string Game) Setup()
    {
        var t = new TempDir();
        var a = t.Dir("mods/a");
        var b = t.Dir("mods/b");
        t.File("mods/a/common/technology/a.txt", "tech_one = { cost = 1 }\ntech_two = { }\n");
        t.File("mods/a/localisation/english/a_l_english.yml", "l_english:\n a_key:0 \"A\"\n");
        t.File("mods/a/gfx/big.dds", "not text");
        t.File("mods/b/events/b.txt", "namespace = b\ncountry_event = { id = b.1 }\n");
        t.File("game/common/technology/00_tech.txt", "tech_one = { }\n");
        t.File("game/stellaris.exe", "exe");
        var store = new IndexStore(Path.Combine(t.Path, "cache", "index.db"));
        return (t, store, new Indexer(store), a, b, Path.Combine(t.Path, "game"));
    }

    [Fact]
    public void Indexes_every_mod_and_the_game_with_files_names_and_localisation_keys()
    {
        var (t, store, indexer, a, b, game) = Setup();
        using var _ = t;
        var result = indexer.Update([Mod("a", a), Mod("b", b)], Game(game));

        Assert.Equal(2, result.ModsIndexed);
        Assert.True(result.GameIndexed);
        var stats = store.AllStats();
        Assert.Equal(new SourceStats(3, 2, 3), stats[("mod", "a")]);
        Assert.Equal(new SourceStats(1, 1, 1), stats[("mod", "b")]);
        Assert.Equal(new SourceStats(2, 1, 1), stats[("vanilla", Indexer.GameBuildKey(Game(game)))]);
        Assert.Equal(1, Count(store, "SELECT COUNT(*) FROM file WHERE path = 'GFX/BIG.DDS'")); // paths compare without case
        Assert.Equal(
            [("common/technology", "common/technology/a.txt", "tech_one", 1), ("common/technology", "common/technology/a.txt", "tech_two", 2), ("localisation/l_english", "localisation/english/a_l_english.yml", "a_key", 2)],
            store.Names("mod", "a").Select(n => (n.Domain, n.Path, n.Name, n.Line)).Order());
        Assert.Equal([("events", "events/b.txt", "b.1", 2)], store.Names("mod", "b").Select(n => (n.Domain, n.Path, n.Name, n.Line)));
    }

    [Fact]
    public void A_mod_that_is_a_zip_is_indexed_like_a_folder_and_a_damaged_zip_is_left_out()
    {
        var (t, store, indexer, a, b, game) = Setup();
        using var _ = t;
        var zipPath = Path.Combine(t.Path, "zipped.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(zip.CreateEntry("common/technology/z.txt").Open())) w.Write("tech_zip = { }");
            using (var w = new StreamWriter(zip.CreateEntry("gfx/pic.dds").Open())) w.Write("x");
        }
        File.WriteAllText(Path.Combine(t.Path, "broken.zip"), "not a zip");
        var zipped = new ModInfo("zipped", "zipped", ModSource.Local, "", null, zipPath, "1.0", null, null, null, [], [], false);
        var broken = new ModInfo("broken", "broken", ModSource.Local, "", null, Path.Combine(t.Path, "broken.zip"), "1.0", null, null, null, [], [], false);

        var first = indexer.Update([zipped, broken, Mod("a", a)], null);
        Assert.Equal(2, first.ModsIndexed);
        Assert.Equal(["tech_zip"], store.Names("mod", "zipped").Select(n => n.Name));
        Assert.Equal(new SourceStats(2, 1, 1), store.AllStats()[("mod", "zipped")]);
        Assert.DoesNotContain(("mod", "broken"), store.AllStats().Keys);
        Assert.Equal(2, indexer.Update([zipped, Mod("a", a)], null).ModsUnchanged);
    }

    [Fact]
    public void A_second_update_with_nothing_changed_reads_nothing()
    {
        var (t, store, indexer, a, b, game) = Setup();
        using var _ = t;
        indexer.Update([Mod("a", a), Mod("b", b)], Game(game));
        var again = indexer.Update([Mod("a", a), Mod("b", b)], Game(game));

        Assert.Equal(0, again.ModsIndexed);
        Assert.Equal(2, again.ModsUnchanged);
        Assert.False(again.GameIndexed);
    }

    [Fact]
    public void Only_the_mod_that_changed_is_read_again_and_its_old_names_are_gone()
    {
        var (t, store, indexer, a, b, game) = Setup();
        using var _ = t;
        indexer.Update([Mod("a", a), Mod("b", b)], Game(game));

        File.WriteAllText(Path.Combine(a, "common", "technology", "a.txt"), "tech_new = { }\n");
        var again = indexer.Update([Mod("a", a), Mod("b", b)], Game(game));

        Assert.Equal(1, again.ModsIndexed);
        Assert.Equal(1, again.ModsUnchanged);
        Assert.Equal(["a_key", "tech_new"], store.Names("mod", "a").Select(n => n.Name).Order());
        Assert.Equal(["b.1"], store.Names("mod", "b").Select(n => n.Name));
    }

    [Fact]
    public void A_mod_that_is_no_longer_installed_is_forgotten_with_its_rows()
    {
        var (t, store, indexer, a, b, game) = Setup();
        using var _ = t;
        indexer.Update([Mod("a", a), Mod("b", b)], Game(game));
        var again = indexer.Update([Mod("b", b)], Game(game));

        Assert.Equal(1, again.ModsRemoved);
        Assert.DoesNotContain(("mod", "a"), store.AllStats().Keys);
        Assert.Equal(0, Count(store, "SELECT COUNT(*) FROM file WHERE source NOT IN (SELECT id FROM source)"));
        Assert.Empty(store.Names("mod", "a"));
    }

    [Fact]
    public void A_new_game_build_is_indexed_and_the_old_one_dropped()
    {
        var (t, store, indexer, a, b, game) = Setup();
        using var _ = t;
        indexer.Update([Mod("a", a)], Game(game, "4.5.2"));
        var again = indexer.Update([Mod("a", a)], Game(game, "4.5.3"));

        Assert.True(again.GameIndexed);
        Assert.Equal(["4.5.3"], store.AllStats().Keys.Where(k => k.Kind == "vanilla").Select(k => k.Key.Split('|')[0]));
    }

    [Fact]
    public void A_mod_with_no_folder_is_skipped_and_cancelling_stops_the_update()
    {
        var (t, store, indexer, a, b, game) = Setup();
        using var _ = t;
        var result = indexer.Update([Mod("gone", Path.Combine(t.Path, "nowhere")), Mod("a", a)], null);
        Assert.Equal(1, result.ModsIndexed);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => indexer.Update([Mod("b", b)], null, ct: cancelled.Token));
        Assert.DoesNotContain(("mod", "b"), store.AllStats().Keys);
    }

    [Fact]
    public void A_damaged_cache_file_is_replaced_and_an_old_version_is_dropped()
    {
        var (t, store, indexer, a, b, game) = Setup();
        using var _ = t;
        indexer.Update([Mod("a", a)], null);

        SqliteConnection.ClearAllPools();
        File.WriteAllText(store.Path, "this is not a database, just text that is long enough to look like one");
        File.Delete(store.Path + "-wal");
        File.Delete(store.Path + "-shm");
        Assert.Equal(1, indexer.Update([Mod("a", a)], null).ModsIndexed);

        using (var db = store.Open())
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "PRAGMA user_version = 0";
            cmd.ExecuteNonQuery();
        }
        Assert.Empty(store.AllStats());
        Assert.Equal(1, indexer.Update([Mod("a", a)], null).ModsIndexed);
    }
}

public class NamePackTests
{
    [Fact]
    public void Names_survive_packing_including_odd_characters()
    {
        var defs = new List<Def> { new("tech_one", 1), new("@var", 22), new("a b", 3), new("tab	here", 4), new("ünïcode_名前", 5) };
        Assert.Equal([new Def("tech_one", 1), new Def("@var", 22), new Def("a b", 3), new Def("tab here", 4), new Def("ünïcode_名前", 5)], NamePack.Unpack(NamePack.Pack(defs)));
        Assert.Empty(NamePack.Unpack(NamePack.Pack([])));
    }
}
