using Artemis.Core.Game;

namespace Artemis.Core.Tests;

public class LogTailTests
{
    [Fact]
    public void The_first_poll_is_the_end_of_a_long_log_cut_at_a_line_start_and_a_short_one_whole()
    {
        using var t = new TempDir();
        var path = t.File("logs/error.log", string.Join("\n", Enumerable.Range(1, 30000).Select(i => $"line {i:D5}")) + "\n");
        var tail = new LogTail(path, joinEntries: false).Poll()!;
        Assert.True(tail.Replace);
        Assert.StartsWith("…\nline ", tail.Text);
        Assert.EndsWith("line 30000\n", tail.Text);
        Assert.InRange(tail.Text.Length, 200_000, 262_200);

        var small = t.File("logs/game.log", "a\nb\n");
        Assert.Equal(new LogChange("a\nb\n", true), new LogTail(small, false).Poll());
    }

    [Fact]
    public void Later_polls_return_only_the_new_lines_and_nothing_when_nothing_came()
    {
        using var t = new TempDir();
        var path = t.File("logs/error.log", "one\n");
        var tail = new LogTail(path, false);
        Assert.Equal(new LogChange("one\n", true), tail.Poll());
        Assert.Null(tail.Poll());

        File.AppendAllText(path, "two\nthree\n");
        Assert.Equal(new LogChange("two\nthree\n", false), tail.Poll());
        Assert.Null(tail.Poll());
    }

    [Fact]
    public void A_line_still_being_written_is_held_back_until_it_is_finished()
    {
        using var t = new TempDir();
        var path = t.File("logs/error.log", "one\ntw");
        var tail = new LogTail(path, false);
        Assert.Equal(new LogChange("one\n", true), tail.Poll());
        Assert.Null(tail.Poll());

        File.AppendAllText(path, "o\nthr");
        Assert.Equal(new LogChange("two\n", false), tail.Poll());
        File.AppendAllText(path, "ee\n");
        Assert.Equal(new LogChange("three\n", false), tail.Poll());
    }

    [Fact]
    public void A_log_started_over_is_returned_whole_again_even_when_it_is_already_longer_than_before()
    {
        using var t = new TempDir();
        var path = t.File("logs/error.log", "[10:00:00] first run\n");
        var tail = new LogTail(path, false);
        tail.Poll();

        File.WriteAllText(path, "[11:30:00] second run\n[11:30:01] more\n");
        Assert.Equal(new LogChange("[11:30:00] second run\n[11:30:01] more\n", true), tail.Poll());

        File.WriteAllText(path, "[12:00:00] x\n");
        Assert.Equal(new LogChange("[12:00:00] x\n", true), tail.Poll());
        File.WriteAllText(path, "");
        Assert.Equal(new LogChange("", true), tail.Poll());
    }

    [Fact]
    public void A_log_the_game_has_open_can_still_be_read()
    {
        using var t = new TempDir();
        var path = t.File("logs/game.log", "hello\n");
        using var game = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        Assert.Equal("hello\n", new LogTail(path, false).Poll()!.Text);
    }

    [Fact]
    public void A_missing_log_is_an_io_error_for_the_caller_to_report()
    {
        using var t = new TempDir();
        Assert.ThrowsAny<IOException>(() => new LogTail(Path.Combine(t.Path, "none.log"), false).Poll());
    }

    [Fact]
    public void Game_entries_with_line_breaks_of_their_own_are_joined_but_other_logs_are_left_as_they_are()
    {
        using var t = new TempDir();
        var text = "[18:27:03][a.cpp:1]: Error: \"x\n\" in file: y\n[18:27:04][a.cpp:2]: Plain\n";
        var path = t.File("logs/error.log", text);
        Assert.Equal("[18:27:03][a.cpp:1]: Error: \"x\" in file: y\n[18:27:04][a.cpp:2]: Plain\n", new LogTail(path, true).Poll()!.Text);
        Assert.Equal(text, new LogTail(path, false).Poll()!.Text);
    }
}

public class LogCleanupTests
{
    private static string[] Names(IEnumerable<StaleLog> stale, string root) => stale.Select(s => Path.GetRelativePath(root, s.File).Replace('\\', '/')).Order().ToArray();

    private static void Age(string path, int days) => File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-days));

    [Fact]
    public void A_game_log_is_a_leftover_only_when_it_is_much_older_than_the_games_newest_log()
    {
        using var t = new TempDir();
        t.File("logs/error.log", "x");
        t.File("logs/game.log", "x");
        Age(t.File("logs/system.log", "x"), 10);
        Age(t.File("logs/stellarstellaris-debug.log", "x"), 400);
        Age(t.File("logs/empty-and-old.log", ""), 400);
        Assert.Equal(["logs/stellarstellaris-debug.log"], Names(LogCleanup.Find(t.Path, []), t.Path));
    }

    [Fact]
    public void A_long_break_from_the_game_never_makes_its_last_logs_look_old()
    {
        using var t = new TempDir();
        Age(t.File("logs/error.log", "x"), 200);
        Age(t.File("logs/game.log", "x"), 200);
        Age(t.File("logs/setup.log", "x"), 205);
        Assert.Empty(LogCleanup.Find(t.Path, []));
    }

    [Fact]
    public void Orions_own_log_and_the_logs_of_installed_plugins_stay_but_other_plugin_logs_and_the_earlier_loaders_go()
    {
        using var t = new TempDir();
        t.File("modloader/orion.log", "x");
        t.File("modloader/modloader.log", "x");
        t.File("modloader/status.json", "{}");
        t.File("modloader/plugins/Kept Plugin.log", "x");
        t.File("modloader/plugins/a_b.log", "x"); // the log of a mod keyed "a:b"
        t.File("modloader/plugins/Orion Keyword Tests.log", "x");
        t.File("modloader/plugins/empty.log", "");
        var stale = LogCleanup.Find(t.Path, ["kept plugin", "a:b"]);
        Assert.Equal(["modloader/modloader.log", "modloader/plugins/Orion Keyword Tests.log"], Names(stale, t.Path));
        Assert.All(stale, s => Assert.True(s.Reason.Length > 0 && s.Bytes == 1));
    }

    [Fact]
    public void Nothing_is_found_when_there_are_no_log_folders()
    {
        using var t = new TempDir();
        Assert.Empty(LogCleanup.Find(t.Path, ["x"]));
    }

    [Fact]
    public void Recycling_removes_the_leftovers_and_leaves_one_that_stopped_being_a_leftover()
    {
        using var t = new TempDir();
        t.File("user/modloader/modloader.log", "old loader");
        t.File("user/modloader/plugins/Gone.log", "gone");
        t.File("user/modloader/plugins/Installed Later.log", "later");
        t.File("user/modloader/orion.log", "keep");
        var user = Path.Combine(t.Path, "user");
        var shown = LogCleanup.Find(user, []);
        Assert.Equal(3, shown.Count);

        // the plugin was installed between showing the list and the click; the real remover is the Recycle Bin, so a recording one stands in
        var sent = new List<string>();
        Assert.Equal(2, LogCleanup.Recycle(user, ["Installed Later"], shown, sent.Add));
        Assert.Equal(["modloader/modloader.log", "modloader/plugins/Gone.log"], sent.Select(f => Path.GetRelativePath(user, f).Replace('\\', '/')).Order());
    }

    [Fact]
    public void A_log_that_can_not_be_removed_stays_and_is_not_counted()
    {
        using var t = new TempDir();
        t.File("user/modloader/modloader.log", "old loader");
        t.File("user/modloader/plugins/Gone.log", "gone");
        var user = Path.Combine(t.Path, "user");
        var shown = LogCleanup.Find(user, []);
        Assert.Equal(1, LogCleanup.Recycle(user, [], shown, f => { if (f.EndsWith("Gone.log")) throw new IOException("in use"); }));
    }

    [Theory]
    [InlineData("Plain Name", "Plain Name")]
    [InlineData("a/b:c*d", "a_b_c_d")]
    [InlineData("ends with dot. ", "ends with dot")]
    [InlineData("...", "_")]
    [InlineData("", "_")]
    public void The_log_name_follows_Orions_rule(string key, string expected) => Assert.Equal(expected, LogCleanup.SafeFileName(key));
}
