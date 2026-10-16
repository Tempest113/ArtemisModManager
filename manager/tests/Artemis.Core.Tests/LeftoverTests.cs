using Artemis.Core.Achievements;
using Artemis.Core.Game;
using Artemis.Core.Index;
using Artemis.Core.Mods;
using Artemis.Core.Playsets;

namespace Artemis.Core.Tests;

public class PlaysetFileTests
{
    [Fact]
    public void An_exported_playset_imports_as_a_new_playset_with_separators_and_switches_intact()
    {
        var original = new Playset
        {
            Name = "Main",
            Mods = [new PlaysetMod { Separator = "Gameplay" }, new PlaysetMod { Key = "a" }, new PlaysetMod { Key = "b", Enabled = false }],
        };
        var copy = Playset.Import(original.ToJson());

        Assert.NotEqual(original.Id, copy.Id);
        Assert.Equal("Main", copy.Name);
        Assert.Equal(["Gameplay", null, null], copy.Mods.Select(m => m.Separator));
        Assert.Equal(["", "a", "b"], copy.Mods.Select(m => m.Key));
        Assert.Equal([true, true, false], copy.Mods.Select(m => m.Enabled));
    }

    [Fact]
    public void A_hand_edited_file_with_missing_parts_still_imports()
    {
        var copy = Playset.Import("""{ "name": " ", "mods": [ { "key": "a" }, { "key": "" }, null ] }""");
        Assert.Equal("Imported playset", copy.Name);
        Assert.Equal(["a"], copy.Mods.Select(m => m.Key));
        Assert.Empty(Playset.Import("""{ "mods": null }""").Mods);
    }

    [Fact]
    public void Something_that_is_not_a_playset_is_refused()
    {
        Assert.ThrowsAny<Exception>(() => Playset.Import("not json"));
        Assert.Throws<InvalidDataException>(() => Playset.Import("null"));
    }
}

public class SeenModsTests
{
    private static ModInfo Mod(string key, long ticks = 1, string? version = "1.0") =>
        new(key, key, ModSource.Local, "", null, null, version, null, null, null, [], [], false, ContentModified: new DateTime(ticks, DateTimeKind.Utc));

    [Fact]
    public void Without_a_snapshot_nothing_counts_as_changed()
    {
        Assert.Equal(ModChange.None, SeenMods.Check(Mod("a"), null));
    }

    [Fact]
    public void New_updated_and_unchanged_mods_are_told_apart()
    {
        var seen = SeenMods.Snapshot([Mod("a"), Mod("b"), Mod("c")]);
        Assert.Equal(ModChange.None, SeenMods.Check(Mod("a"), seen));
        Assert.Equal(ModChange.Updated, SeenMods.Check(Mod("b", ticks: 2), seen));
        Assert.Equal(ModChange.Updated, SeenMods.Check(Mod("c", version: "1.1"), seen));
        Assert.Equal(ModChange.New, SeenMods.Check(Mod("d"), seen));
    }

    [Fact]
    public void The_scanner_dates_a_mod_by_its_folder_not_by_its_mod_file()
    {
        using var t = new TempDir();
        var folder = t.Dir("mod/a");
        t.File("mod/a.mod", "name=\"A\"\npath=\"mod/a\"\n");
        var when = new DateTime(2025, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        Directory.SetLastWriteTimeUtc(folder, when);
        Assert.Equal(when, ModScanner.Scan(t.Path).Mods.Single().ContentModified);
    }

    [Fact]
    public void Notes_and_the_snapshot_survive_a_restart()
    {
        using var t = new TempDir();
        var paths = new AppPaths(t.Path);
        new AppSettings { Notes = { ["a"] = "needs the patch" }, SeenMods = new() { ["a"] = "1|1.0" } }.Save(paths);
        var loaded = AppSettings.Load(paths);
        Assert.Equal("needs the patch", loaded.Notes["a"]);
        Assert.Equal("1|1.0", loaded.SeenMods!["a"]);
    }
}

public class GameLogsTests
{
    [Fact]
    public void Lists_only_non_empty_logs_with_the_error_log_first()
    {
        using var t = new TempDir();
        t.File("logs/system.log", "x");
        t.File("logs/game.log", "x");
        t.File("logs/error.log", "x");
        t.File("logs/empty.log", "");
        t.File("logs/notes.txt", "x");
        Assert.Equal(["error.log", "game.log", "system.log"], GameLogs.List(t.Path).Select(Path.GetFileName));
        Assert.Empty(GameLogs.List(Path.Combine(t.Path, "nowhere")));
    }
}

public class LogEntryTests
{
    [Fact]
    public void A_message_with_a_line_break_of_its_own_becomes_one_line()
    {
        var text = "[18:27:03][persistent.cpp:41]: Error: \"Unexpected token: size, near line: 6935\n\" in file: \"interface/x.gui\" near line: 6935\r\n"
                 + "[18:27:04][other.cpp:9]: Plain entry\n"
                 + "[18:27:05][other.cpp:9]: Trace\n    at something\n";
        Assert.Equal(
            "[18:27:03][persistent.cpp:41]: Error: \"Unexpected token: size, near line: 6935\" in file: \"interface/x.gui\" near line: 6935\n"
            + "[18:27:04][other.cpp:9]: Plain entry\n"
            + "[18:27:05][other.cpp:9]: Trace    at something",
            GameLogs.OneLinePerEntry(text));
    }

    [Fact]
    public void A_log_in_another_format_and_a_cut_tail_are_left_alone()
    {
        const string other = "2026-10-15 01:06:39 info  Artemis started\n    indented line\n";
        Assert.Equal(other, GameLogs.OneLinePerEntry(other));
        Assert.Equal("…\nrest of a cut line\n[00:00:01][a.cpp:1]: ok", GameLogs.OneLinePerEntry("…\nrest of a cut line\n[00:00:01][a.cpp:1]: ok"));
    }
}

public class AchievementTests
{
    // A made-up list in the format of the game's own, so the real one is never copied into the repo.
    private const string Sample = "directory \nname = common\nsub_directories = yes\nfile_extension = .txt\n\ndirectory\nname = events\nsub_directories = yes\nfile_extension = .txt\n\ndirectory\nname = map\nsub_directories = no\nfile_extension = .shader\n";

    [Fact]
    public void The_list_is_read_into_rules_and_broken_blocks_are_skipped()
    {
        Assert.Equal(
            [new ChecksumRule("common", true, ".txt"), new ChecksumRule("events", true, ".txt"), new ChecksumRule("map", false, ".shader")],
            ChecksumRules.Parse(Sample).Rules);
        Assert.Equal(["common"], ChecksumRules.Parse("junk\ndirectory\nname = common\nfile_extension = .txt\ndirectory\nname = lonely\n").Rules.Select(r => r.Directory));
        Assert.Empty(ChecksumRules.Parse("").Rules);
    }

    [Theory]
    [InlineData("common/technology/00_tech.txt", true)]
    [InlineData("Common/Deep/er/folder/x.TXT", true)]
    [InlineData("common/x.png", false)]
    [InlineData("commonality/x.txt", false)]
    [InlineData("events/e.txt", true)]
    [InlineData("gfx/x.txt", false)]
    [InlineData("localisation/english/x_l_english.yml", false)]
    [InlineData("map/x.shader", true)]
    [InlineData("map/sub/x.shader", false)]
    public void Only_the_listed_folders_and_file_types_count(string path, bool covered) =>
        Assert.Equal(covered, ChecksumRules.Parse(Sample).Matches(path));

    [Fact]
    public void A_game_without_the_list_gives_no_rules_and_mods_are_checked_from_the_index()
    {
        using var t = new TempDir();
        Assert.Null(ChecksumRules.Load(t.Path));
        t.File(ChecksumRules.FileName, Sample);
        var rules = ChecksumRules.Load(t.Path)!;

        var store = new IndexStore(Path.Combine(t.Path, "index.db"));
        store.Replace("mod", "changes", "x", [new FileEntry("gfx/a.dds", 1, 1), new FileEntry("common/b.txt", 1, 1), new FileEntry("events/a.txt", 1, 1)]);
        store.Replace("mod", "art", "x", [new FileEntry("gfx/a.dds", 1, 1), new FileEntry("localisation/english/a_l_english.yml", 1, 1)]);

        var changes = AchievementCheck.Scan(store, ["changes", "art", "unknown"], rules);
        Assert.Equal(["changes"], changes.Keys);
        Assert.Equal(["common/b.txt", "events/a.txt"], changes["changes"]);
    }
}
