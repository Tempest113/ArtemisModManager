using System.Diagnostics;
using System.Text.Json;
using Artemis.Core.Game;
using Artemis.Core.IO;
using Artemis.Core.Playsets;
using Artemis.Core.Plugins;

namespace Artemis.Core.Tests;

public sealed class OrionInstallerTests : IDisposable
{
    private readonly TempDir _t = new();
    private readonly string _game, _payload;
    private readonly AppPaths _paths;
    private bool _running;

    public OrionInstallerTests()
    {
        _game = _t.Dir("game");
        _payload = _t.Dir("payload");
        _paths = new AppPaths(_t.Dir("data"));
        // Any program file whose version resource says "Artemis" can stand in for the stub (the test project sets that company); another one for the core.
        File.Copy(typeof(OrionInstallerTests).Assembly.Location, Path.Combine(_payload, "winmm.dll"));
        File.Copy(typeof(AppPaths).Assembly.Location, Path.Combine(_payload, "orion.dll"));
    }

    public void Dispose() => _t.Dispose();

    private OrionInstaller Installer(string? payload = null) => new(_paths, payload ?? _payload, () => _running);

    private string Version => FileVersionInfo.GetVersionInfo(Path.Combine(_payload, "orion.dll")).FileVersion!;

    [Fact]
    public void Without_the_files_to_install_there_is_nothing_to_offer()
    {
        var status = Installer(_t.Dir("empty")).Inspect(_game);
        Assert.Equal(OrionState.NoPayload, status.State);
        Assert.Null(status.AvailableVersion);
        Assert.Throws<ArtemisException>(() => Installer(_t.Dir("empty")).Install(_game));
    }

    [Fact]
    public void Install_puts_the_stub_the_core_and_the_pointer_in_place_and_is_harmless_to_repeat()
    {
        var installer = Installer();
        Assert.Equal(OrionState.NotInstalled, installer.Inspect(_game).State);
        installer.Install(_game);
        installer.Install(_game);

        Assert.True(StubCheck.IsInstalled(_game));
        Assert.True(File.Exists(Path.Combine(_paths.OrionDir, Version, "orion.dll")));
        var pointer = File.ReadAllBytes(_paths.OrionPointerFile);
        Assert.NotEqual(0xEF, pointer[0]); // no byte-order mark: the stub reads the file as plain bytes
        Assert.Equal(Version, System.Text.Encoding.ASCII.GetString(pointer).Trim());
        var status = installer.Inspect(_game);
        Assert.Equal(OrionState.Installed, status.State);
        Assert.Equal(Version, status.InstalledVersion);
    }

    [Fact]
    public void A_winmm_dll_that_is_not_ours_is_never_replaced()
    {
        var foreign = _t.File("game/winmm.dll", "somebody else's");
        var installer = Installer();
        Assert.Equal(OrionState.ForeignStub, installer.Inspect(_game).State);
        var ex = Assert.Throws<ArtemisException>(() => installer.Install(_game));
        Assert.Contains("won't replace", ex.Message);
        Assert.Equal("somebody else's", File.ReadAllText(foreign));
        Assert.False(File.Exists(_paths.OrionPointerFile));

        installer.Remove(_game);
        Assert.Equal("somebody else's", File.ReadAllText(foreign)); // removing Orion never deletes a file Artemis didn't put there
    }

    [Fact]
    public void Nothing_changes_while_the_game_is_running()
    {
        _running = true;
        Assert.Throws<ArtemisException>(() => Installer().Install(_game));
        Assert.False(File.Exists(Path.Combine(_game, "winmm.dll")));
        _running = false;
        Installer().Install(_game);
        _running = true;
        Assert.Throws<ArtemisException>(() => Installer().Remove(_game));
        Assert.True(StubCheck.IsInstalled(_game));
    }

    [Fact]
    public void Remove_takes_out_the_stub_the_core_and_the_copies_but_keeps_the_builds_the_user_allowed()
    {
        var installer = Installer();
        installer.Install(_game);
        _t.File("data/orion/cache/abc.dll", "a plugin copy");
        new OrionBuilds(_paths).Allow("66f1a2b3-5a4000", "v4.5.2");

        installer.Remove(_game);
        Assert.False(File.Exists(Path.Combine(_game, "winmm.dll")));
        Assert.False(File.Exists(_paths.OrionPointerFile));
        Assert.False(Directory.Exists(Path.Combine(_paths.OrionDir, Version)));
        Assert.False(Directory.Exists(Path.Combine(_paths.OrionDir, "cache")));
        Assert.Equal(BuildSupport.AllowedByUser, new OrionBuilds(_paths).Check("66f1a2b3-5a4000"));
        Assert.Equal(OrionState.NotInstalled, installer.Inspect(_game).State);
        installer.Remove(_game); // and again, with nothing left, is fine
    }

    [Fact]
    public void A_stub_that_steams_file_check_removed_is_seen_and_put_back()
    {
        var installer = Installer();
        installer.Install(_game);
        File.Delete(Path.Combine(_game, "winmm.dll"));
        Assert.Equal(OrionState.NeedsRepair, installer.Inspect(_game).State);
        installer.Install(_game);
        Assert.Equal(OrionState.Installed, installer.Inspect(_game).State);
    }

    [Fact]
    public void A_different_orion_in_this_copy_of_artemis_is_an_update_and_old_core_folders_go()
    {
        var installer = Installer();
        installer.Install(_game);
        _t.File("data/orion/0.0.9/orion.dll", "an older core");
        _t.File("data/orion/cache/abc.dll", "a plugin copy");
        File.AppendAllText(Path.Combine(_payload, "orion.dll"), "x"); // same version number, new build: still different
        Assert.Equal(OrionState.Outdated, installer.Inspect(_game).State);

        installer.Install(_game);
        Assert.Equal(OrionState.Installed, installer.Inspect(_game).State);
        Assert.False(Directory.Exists(Path.Combine(_paths.OrionDir, "0.0.9")));
        Assert.True(File.Exists(Path.Combine(_paths.OrionDir, "cache", "abc.dll"))); // plugin copies are not touched by an update
    }
}

public class GameBuildTests
{
    private static byte[] Pe(uint stamp, uint size)
    {
        var d = new byte[0x200];
        d[0] = (byte)'M'; d[1] = (byte)'Z';
        BitConverter.GetBytes(0x80).CopyTo(d, 0x3C);
        d[0x80] = (byte)'P'; d[0x81] = (byte)'E';
        BitConverter.GetBytes(stamp).CopyTo(d, 0x80 + 8);
        BitConverter.GetBytes(size).CopyTo(d, 0x80 + 24 + 56);
        return d;
    }

    [Fact]
    public void The_build_id_is_the_pe_timestamp_and_image_size_in_hex()
    {
        using var t = new TempDir();
        var exe = Path.Combine(t.Path, "stellaris.exe");
        File.WriteAllBytes(exe, Pe(0x66F1A2B3, 0x5A4000));
        Assert.Equal("66f1a2b3-5a4000", GameBuild.IdOf(exe));
        File.WriteAllBytes(exe, Pe(0x00000042, 0x1000));
        Assert.Equal("00000042-1000", GameBuild.IdOf(exe));
    }

    [Fact]
    public void Anything_that_is_not_a_program_has_no_build_id()
    {
        using var t = new TempDir();
        Assert.Null(GameBuild.IdOf(t.File("a.exe", "just text")));
        Assert.Null(GameBuild.IdOf(Path.Combine(t.Path, "missing.exe")));
        var broken = Pe(1, 1);
        broken[0x80] = 0;
        File.WriteAllBytes(Path.Combine(t.Path, "b.exe"), broken);
        Assert.Null(GameBuild.IdOf(Path.Combine(t.Path, "b.exe")));
    }
}

public class OrionBuildsTests
{
    private const string Id = "66f1a2b3-5a4000";

    [Fact]
    public void Allowing_a_build_writes_the_file_orion_reads_and_taking_it_back_removes_it()
    {
        using var t = new TempDir();
        var paths = new AppPaths(t.Path);
        var builds = new OrionBuilds(paths);
        Assert.Equal(BuildSupport.Unknown, builds.Check(Id));

        builds.Allow(Id, "v4.5.2");
        Assert.Equal(BuildSupport.AllowedByUser, builds.Check(Id));
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(paths.OrionBuildsDir, Id + ".json")));
        Assert.Equal(Id, doc.RootElement.GetProperty("build").GetString());
        Assert.Equal("v4.5.2", doc.RootElement.GetProperty("game").GetString());
        Assert.Equal(JsonValueKind.Object, doc.RootElement.GetProperty("addresses").ValueKind); // Orion's key for addresses; none here

        builds.StopAllowing(Id);
        Assert.Equal(BuildSupport.Unknown, builds.Check(Id));
    }

    [Fact]
    public void Verified_data_is_never_overwritten_or_deleted_from_here()
    {
        using var t = new TempDir();
        var paths = new AppPaths(t.Path);
        var file = t.File($"orion/builds/{Id}.json", $$"""{ "build": "{{Id}}", "game": "v4.5.2", "addresses": { "x": 1 } }""");
        var builds = new OrionBuilds(paths);
        Assert.Equal(BuildSupport.Verified, builds.Check(Id));
        builds.Allow(Id, "v9.9.9");
        builds.StopAllowing(Id);
        Assert.Contains("\"x\": 1", File.ReadAllText(file));
    }

    [Fact]
    public void A_file_for_another_build_a_missing_version_and_a_bad_id_are_refused()
    {
        using var t = new TempDir();
        var paths = new AppPaths(t.Path);
        t.File($"orion/builds/{Id}.json", """{ "build": "00000000-1000", "game": "v4.5.2" }""");
        var builds = new OrionBuilds(paths);
        Assert.Equal(BuildSupport.Unknown, builds.Check(Id));
        Assert.Throws<ArtemisException>(() => builds.Allow("66f1a2b3-5a4001", "  "));
        Assert.Throws<ArtemisException>(() => builds.Allow("..\\evil", "v4"));
    }
}
