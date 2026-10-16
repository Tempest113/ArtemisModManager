using Artemis.Core.Game;
using Artemis.Core.IO;
using Artemis.Core.Launch;
using Artemis.Core.Mods;
using Artemis.Core.Playsets;

namespace Artemis.Core.Tests;

public class PlaysetStoreTests
{
    [Fact]
    public void Saves_lists_and_deletes()
    {
        using var t = new TempDir();
        var store = new PlaysetStore(new AppPaths(t.Path));
        var playset = new Playset { Name = "Main", Mods = [new PlaysetMod { Key = "a" }, new PlaysetMod { Key = "b", Enabled = false }] };
        store.Save(playset);

        var loaded = Assert.Single(store.List().Playsets);
        Assert.Equal(playset.Id, loaded.Id);
        Assert.Equal(["a", "b"], loaded.Mods.Select(m => m.Key));
        Assert.Equal(["a"], loaded.EnabledMods.Select(m => m.Key));

        store.Delete(playset.Id);
        Assert.Empty(store.List().Playsets);
    }

    [Fact]
    public void A_corrupt_file_is_reported_and_does_not_hide_the_others()
    {
        using var t = new TempDir();
        var paths = new AppPaths(t.Path);
        var store = new PlaysetStore(paths);
        store.Save(new Playset { Name = "Good" });
        t.File("playsets/broken.json", "{ not json");

        var result = store.List();
        Assert.Equal(["Good"], result.Playsets.Select(p => p.Name));
        Assert.Single(result.Warnings);
    }

    [Fact]
    public void The_file_name_is_the_id_and_unsafe_ids_are_refused()
    {
        using var t = new TempDir();
        var store = new PlaysetStore(new AppPaths(t.Path));
        t.File("playsets/real.json", "{ \"id\": \"..\\\\evil\", \"name\": \"X\" }");
        Assert.Equal("real", Assert.Single(store.List().Playsets).Id);
        Assert.Throws<ArtemisException>(() => store.Save(new Playset { Id = @"..\evil" }));
    }

    [Fact]
    public void Settings_round_trip_and_a_bad_file_gives_defaults()
    {
        using var t = new TempDir();
        var paths = new AppPaths(t.Path);
        new AppSettings { ActivePlaysetId = "p1", GameDir = @"D:\Games\Stellaris" }.Save(paths);
        Assert.Equal("p1", AppSettings.Load(paths).ActivePlaysetId);

        t.File("settings.json", "garbage");
        Assert.Null(AppSettings.Load(paths).ActivePlaysetId);
    }
}

public class SettingsTests
{
    [Fact]
    public void Layout_survives_a_restart_and_old_files_still_load()
    {
        using var t = new TempDir();
        var paths = new AppPaths(t.Path);
        new AppSettings
        {
            ActivePlaysetId = "x", LibrarySplit = 0.4, LibraryCollapsed = true,
            Window = new WindowPlacement { Left = 10, Top = 20, Width = 1000, Height = 700, Maximized = true },
        }.Save(paths);

        var loaded = AppSettings.Load(paths);
        Assert.Equal(0.4, loaded.LibrarySplit);
        Assert.True(loaded.LibraryCollapsed);
        Assert.Equal((10, 20, 1000, 700, true), (loaded.Window!.Left, loaded.Window.Top, loaded.Window.Width, loaded.Window.Height, loaded.Window.Maximized));

        File.WriteAllText(paths.SettingsFile, """{"activePlaysetId":"y"}""");
        var old = AppSettings.Load(paths);
        Assert.Equal("y", old.ActivePlaysetId);
        Assert.Null(old.Window);
        Assert.Null(old.LibrarySplit);
    }
}

public class PlaysetEditTests
{
    private static Playset Of(params string[] keys) => new() { Mods = keys.Select(k => new PlaysetMod { Key = k }).ToList() };

    [Fact]
    public void Add_appends_once_and_ignores_case()
    {
        var p = Of("a");
        Assert.True(p.Add("b"));
        Assert.False(p.Add("A"));
        Assert.Equal(["a", "b"], p.Mods.Select(m => m.Key));
        Assert.True(p.Contains("B"));
    }

    [Fact]
    public void Remove_deletes_by_key()
    {
        var p = Of("a", "b");
        Assert.True(p.Remove("B"));
        Assert.False(p.Remove("zzz"));
        Assert.Equal(["a"], p.Mods.Select(m => m.Key));
    }

    [Theory]
    [InlineData("0", 2, "b,c,a,d")]        // one line later
    [InlineData("2", 0, "c,a,b,d")]        // one line to the front
    [InlineData("1", 1, "a,b,c,d")]        // already there
    [InlineData("0", 99, "b,c,d,a")]       // past the end is the end
    [InlineData("0,2", 1, "b,a,c,d")]      // a block keeps its inner order
    [InlineData("3,1", 0, "b,d,a,c")]      // however the indices are listed
    [InlineData("-1,7", 1, "a,b,c,d")]     // out of range is ignored
    public void MoveBlock_moves_lines_together(string picked, int insertAt, string expected)
    {
        var moved = Playset.MoveBlock(["a", "b", "c", "d"], picked.Split(',').Select(int.Parse), insertAt);
        Assert.Equal(expected, string.Join(',', moved));
    }

    [Fact]
    public void Clone_copies_the_mods_but_gets_a_new_id()
    {
        var p = Of("a");
        p.Mods[0].Enabled = false;
        var copy = p.Clone("Copy");
        Assert.NotEqual(p.Id, copy.Id);
        Assert.Equal("Copy", copy.Name);
        Assert.False(copy.Mods[0].Enabled);
        copy.Add("b");
        Assert.Single(p.Mods);
    }

    [Fact]
    public void Separators_label_the_list_but_never_count_as_mods()
    {
        var p = Of("a", "b");
        p.Mods.Insert(1, new PlaysetMod { Separator = "Fixes" });

        var loaded = Playset.FromJson(p.ToJson());
        Assert.Equal("Fixes", loaded.Mods[1].Separator);
        Assert.Null(loaded.Mods[0].Separator);
        Assert.Equal(["a", "b"], loaded.EnabledMods.Select(m => m.Key));
        Assert.Equal("Fixes", p.Clone().Mods[1].Separator);
        Assert.DoesNotContain("separator", Of("a").ToJson());
    }
}

public class DlcLoadTests
{
    [Fact]
    public void Stamp_changes_with_the_list_and_its_order_only()
    {
        var a = DlcLoad.Stamp(["mod/a.mod", "mod/b.mod"]);
        Assert.Equal(a, DlcLoad.Stamp(["mod/a.mod", "mod/b.mod"]));
        Assert.NotEqual(a, DlcLoad.Stamp(["mod/b.mod", "mod/a.mod"]));
        Assert.NotEqual(a, DlcLoad.Stamp(["mod/a.mod"]));
        Assert.NotEqual(DlcLoad.Stamp(["mod/a.mod"]), DlcLoad.Stamp(["mod/a.mod", "mod/b.mod"]));
    }

    private static ModInfo Mod(string key) =>
        new(key, key, ModSource.Local, "", null, null, null, null, null, null, [], [], false);

    [Fact]
    public void Renders_the_compact_format_the_game_uses()
    {
        var text = DlcLoad.Render(new DlcLoad.State(["mod/a.mod", "mod/No Pirate.48 Event.mod"], ["dlc001"]));
        Assert.Equal("{\"disabled_dlcs\":[\"dlc001\"],\"enabled_mods\":[\"mod/a.mod\",\"mod/No Pirate.48 Event.mod\"]}", text);
    }

    [Fact]
    public void Reads_what_it_renders_and_tolerates_missing_arrays()
    {
        using var t = new TempDir();
        var path = t.File("dlc_load.json", DlcLoad.Render(new DlcLoad.State(["mod/a.mod"], ["x"])));
        var state = DlcLoad.Read(path);
        Assert.Equal(["mod/a.mod"], state.EnabledMods);
        Assert.Equal(["x"], state.DisabledDlcs);

        var sparse = DlcLoad.Read(t.File("sparse.json", "{}"));
        Assert.Empty(sparse.EnabledMods);
    }

    [Fact]
    public void Entries_keep_order_skip_disabled_uninstalled_and_repeated_mods()
    {
        var installed = new[] { "a", "b", "c" }.ToDictionary(k => k, Mod, StringComparer.OrdinalIgnoreCase);
        var playset = new Playset
        {
            Mods = [new() { Key = "c" }, new() { Key = "a" }, new() { Key = "b", Enabled = false },
                    new() { Key = "gone" }, new() { Key = "C" }],
        };
        Assert.Equal(["mod/c.mod", "mod/a.mod"], DlcLoad.EntriesFor(playset, installed));
    }

    [Fact]
    public void Imports_entries_as_playset_mods()
    {
        var mods = DlcLoad.ToPlaysetMods(["mod/ugc_1.mod", "mod/My Mod.mod"]);
        Assert.Equal(["ugc_1", "My Mod"], mods.Select(m => m.Key));
        Assert.All(mods, m => Assert.True(m.Enabled));
    }
}

public class LaunchServiceTests
{
    private static (GameInstall game, AppPaths paths) Setup(TempDir t)
    {
        t.File("game/launcher-settings.json", "{ \"exePath\": \"./stellaris.exe\" }");
        var game = GameInstall.Open(System.IO.Path.Combine(t.Path, "game"), System.IO.Path.Combine(t.Path, "user"));
        return (game, new AppPaths(System.IO.Path.Combine(t.Path, "app")));
    }

    private static readonly IReadOnlyDictionary<string, ModInfo> Installed = new Dictionary<string, ModInfo>
    {
        ["a"] = new("a", "a", ModSource.Local, "", null, null, null, null, null, null, [], [], false),
    };

    [Fact]
    public void Apply_backs_up_the_original_once_and_keeps_disabled_dlcs()
    {
        using var t = new TempDir();
        var (game, paths) = Setup(t);
        t.File("user/dlc_load.json", "{\"disabled_dlcs\":[\"dlc9\"],\"enabled_mods\":[\"mod/old.mod\"]}");
        var service = new LaunchService(game, paths, () => false);
        var playset = new Playset { Mods = [new() { Key = "a" }, new() { Key = "missing" }] };

        var first = service.Apply(playset, Installed);
        Assert.Equal(1, first.EnabledMods);
        Assert.Equal(1, first.SkippedMods);
        var written = DlcLoad.Read(game.DlcLoadPath);
        Assert.Equal(["mod/a.mod"], written.EnabledMods);
        Assert.Equal(["dlc9"], written.DisabledDlcs);

        service.Apply(playset, Installed); // second apply must not replace the backup with our own output
        Assert.True(service.RestoreOriginal());
        Assert.Equal(["mod/old.mod"], DlcLoad.Read(game.DlcLoadPath).EnabledMods);
    }

    [Fact]
    public void Apply_works_without_an_existing_dlc_load_and_restore_reports_no_backup()
    {
        using var t = new TempDir();
        var (game, paths) = Setup(t);
        var service = new LaunchService(game, paths, () => false);
        Assert.False(service.RestoreOriginal());
        service.Apply(new Playset { Mods = [new() { Key = "a" }] }, Installed);
        Assert.Equal(["mod/a.mod"], DlcLoad.Read(game.DlcLoadPath).EnabledMods);
    }

    [Fact]
    public void Refuses_to_apply_or_launch_while_the_game_runs()
    {
        using var t = new TempDir();
        var (game, paths) = Setup(t);
        var service = new LaunchService(game, paths, () => true);
        Assert.Throws<ArtemisException>(() => service.Apply(new Playset(), Installed));
        Assert.Throws<ArtemisException>(() => service.Launch());
        Assert.Equal(game.Settings.ExeArgs, service.Arguments(continueLastSave: false));
        Assert.Equal([.. game.Settings.ExeArgs, "--continuelastsave"], service.Arguments(continueLastSave: true));

        var start = service.StartInfo(continueLastSave: true);
        Assert.Equal(game.ExePath, start.FileName);
        Assert.Equal(game.GameDir, start.WorkingDirectory);
        Assert.Equal("--continuelastsave", start.ArgumentList[^1]);
        Assert.All(new[] { "SteamAppId", "SteamGameId", "SteamOverlayGameId" }, name => Assert.Equal("281990", start.Environment[name]));
        Assert.Equal(paths.Root, start.Environment["ARTEMIS_DATA_DIR"]);
        Assert.False(start.Environment.ContainsKey("ORION_PLUGINS") && start.Environment["ORION_PLUGINS"] == "off");
        Assert.Equal("off", service.StartInfo(continueLastSave: false, withoutPlugins: true).Environment["ORION_PLUGINS"]);
        Assert.False(File.Exists(game.DlcLoadPath));
    }
}

public class LastSaveTests
{
    [Fact]
    public void The_save_to_continue_is_read_from_the_games_own_note()
    {
        using var t = new TempDir();
        Assert.Null(LaunchService.FindLastSave(t.Path));
        t.File("continue_game.json", "{\n\t\"title\":\t\"save games/empire_1/2335.08.15\",\n\t\"desc\":\t\"Shazarak Matrix 2\",\n\t\"date\":\t\"2335.08.15\"\n}");
        Assert.Equal(new LastSave("Shazarak Matrix 2", "2335.08.15"), LaunchService.FindLastSave(t.Path));

        t.File("continue_game.json", "{ \"title\": \"save games/x/1\" }");
        Assert.Equal(new LastSave("save games/x/1", ""), LaunchService.FindLastSave(t.Path));
        t.File("continue_game.json", "not json");
        Assert.Null(LaunchService.FindLastSave(t.Path));
    }
}
