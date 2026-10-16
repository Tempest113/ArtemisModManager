using Artemis.Core.IO;
using System.Text.Json;
using Artemis.Core.Mods;
using Artemis.Core.Playsets;
using Artemis.Core.Plugins;

namespace Artemis.Core.Tests;

public class PluginManifestTests
{
    private const string Good = """
        {
          "schema": 1, "id": "com.example.demo", "name": "Demo", "version": "1.2.0", "apiVersion": 1,
          "files": ["orion/demo.dll"], "builds": ["v4.5.*"], "dependencies": ["com.example.core"], "keywords": ["demo_effect"],
          "stability": "stable",
          "features": [ { "id": "fast_build", "name": "Fast build", "default": true } ],
          "settings": [
            { "id": "speed", "type": "float", "name": "Speed", "default": 1.5, "min": 0.5, "max": 4 },
            { "id": "count", "type": "int", "default": 3, "min": 1, "max": 10 },
            { "id": "mode", "type": "enum", "options": [ "easy", { "value": "hard", "name": "Hard" } ], "default": "hard" },
            { "id": "label", "type": "string" }, { "id": "folder", "type": "path" }, { "id": "loud", "type": "bool" }
          ]
        }
        """;

    [Fact]
    public void A_complete_manifest_is_read_with_its_settings_and_features()
    {
        var info = PluginManifestReader.Parse(Good);
        var m = Assert.IsType<PluginManifest>(info.Manifest);
        Assert.Equal(("com.example.demo", "Demo", "1.2.0", 1, PluginTier.Stable, false), (m.Id, m.Name, m.Version, m.ApiVersion, m.Tier, m.Cosmetic));
        Assert.Equal(["orion/demo.dll"], m.Files);
        Assert.Equal(["fast_build"], m.Features.Select(f => f.Id));
        Assert.Equal([SettingType.Float, SettingType.Int, SettingType.Enum, SettingType.String, SettingType.Path, SettingType.Bool], m.Settings.Select(s => s.Type));
        Assert.Equal(1.5, m.Settings[0].Default);
        Assert.Equal(["easy", "hard"], m.Settings[2].Options.Select(o => o.Value));
        Assert.Equal("hard", m.Settings[2].Default);
        Assert.Equal(false, m.Settings[5].Default);
    }

    [Fact]
    public void Missing_pieces_get_safe_defaults_and_an_untiered_plugin_counts_as_fragile()
    {
        var m = PluginManifestReader.Parse("""{ "id": "a", "version": "1", "apiVersion": 1, "files": ["x.dll"] }""").Manifest!;
        Assert.Equal(("a", PluginTier.Fragile), (m.Name, m.Tier));
        Assert.Empty(m.Settings);
        Assert.Empty(m.Builds);
    }

    [Theory]
    [InlineData("""{ "version": "1", "apiVersion": 1, "files": ["x.dll"] }""", "id")]
    [InlineData("""{ "id": "a b", "version": "1", "apiVersion": 1, "files": ["x.dll"] }""", "id")]
    [InlineData("""{ "id": "a", "apiVersion": 1, "files": ["x.dll"] }""", "version")]
    [InlineData("""{ "id": "a", "version": "1", "files": ["x.dll"] }""", "apiVersion")]
    [InlineData("""{ "id": "a", "version": "1", "apiVersion": 1, "files": [] }""", "files")]
    [InlineData("""{ "id": "a", "version": "1", "apiVersion": 1, "files": ["../evil.dll"] }""", "evil.dll")]
    [InlineData("""{ "id": "a", "version": "1", "apiVersion": 1, "files": ["C:/evil.dll"] }""", "evil.dll")]
    [InlineData("""{ "id": "a", "version": "1", "apiVersion": 1, "files": ["a/../../b.dll"] }""", "b.dll")]
    [InlineData("""{ "id": "a", "version": "1", "apiVersion": 1, "files": ["data.txt"] }""", "data.txt")]
    [InlineData("""{ "schema": 2, "id": "a", "version": "1", "apiVersion": 1, "files": ["x.dll"] }""", "schema")]
    [InlineData("""{ "id": "a", "version": "1", "apiVersion": 1, "files": ["x.dll"], "stability": "wild" }""", "stability")]
    [InlineData("""{ "id": "a", "version": "1", "apiVersion": 1, "files": ["x.dll"], "settings": [ { "id": "s", "type": "int", "default": 99, "max": 5 } ] }""", "range")]
    [InlineData("""{ "id": "a", "version": "1", "apiVersion": 1, "files": ["x.dll"], "settings": [ { "id": "s", "type": "enum", "options": ["a"], "default": "b" } ] }""", "options")]
    [InlineData("""{ "id": "a", "version": "1", "apiVersion": 1, "files": ["x.dll"], "settings": [ { "id": "s", "type": "blob" } ] }""", "type")]
    [InlineData("""{ "id": "a", "version": "1", "apiVersion": 1, "files": ["x.dll"], "features": [ { "id": "s" } ], "settings": [ { "id": "s", "type": "bool" } ] }""", "two settings")]
    [InlineData("not json", "JSON")]
    [InlineData("[1]", "object")]
    public void A_manifest_with_something_wrong_is_refused_and_says_what(string json, string mentions)
    {
        var info = PluginManifestReader.Parse(json);
        Assert.Null(info.Manifest);
        Assert.Contains(mentions, info.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_scanner_attaches_the_plugin_part_to_a_mod_that_has_one()
    {
        using var t = new TempDir();
        t.File("mod/p.mod", "name=\"P\"\npath=\"mod/p\"\n");
        t.File("mod/p/modloader.json", Good);
        t.File("mod/plain.mod", "name=\"Plain\"\npath=\"mod/plain\"\n");
        t.Dir("mod/plain");
        t.File("mod/broken.mod", "name=\"Broken\"\npath=\"mod/broken\"\n");
        t.File("mod/broken/modloader.json", "{ nope");

        var mods = ModScanner.Scan(t.Path).Mods.ToDictionary(m => m.Key);
        Assert.Equal("com.example.demo", mods["p"].Plugin!.Manifest!.Id);
        Assert.Null(mods["plain"].Plugin);
        Assert.Null(mods["broken"].Plugin!.Manifest);
        Assert.NotNull(mods["broken"].Plugin!.Error);
    }
}

public class TrustStoreTests
{
    private static PluginManifest Manifest(params string[] files) =>
        PluginManifestReader.Parse($$"""{ "id": "a", "version": "1", "apiVersion": 1, "files": [{{string.Join(",", files.Select(f => $"\"{f}\""))}}] }""").Manifest!;

    [Fact]
    public void Hashes_cover_the_listed_files_and_never_read_outside_the_mod_folder()
    {
        using var t = new TempDir();
        var dir = t.Dir("mod");
        t.File("mod/a.dll", "one");
        t.File("outside.dll", "secret");
        var hashes = PluginHashes.Compute(dir, new PluginManifest("a", "a", "1", 1, ["a.dll", "missing.dll", "../outside.dll"], [], [], [], [], [], [], PluginTier.Stable, false));
        Assert.Equal(["a.dll"], hashes.Keys);
        Assert.Equal(64, hashes["a.dll"].Length);
        Assert.Equal(hashes["a.dll"], PluginHashes.Compute(dir, Manifest("a.dll"))["a.dll"]);
    }

    [Fact]
    public void Approval_is_pinned_to_the_files_and_a_change_needs_a_new_approval()
    {
        using var t = new TempDir();
        var store = new TrustStore(new AppPaths(t.Path));
        var manifest = Manifest("a.dll");
        Assert.Equal(PluginTrust.NotApproved, store.StateOf("m", manifest, new Dictionary<string, string> { ["a.dll"] = "h1" }));

        store.Approve("m", "Mod M", new Dictionary<string, string> { ["a.dll"] = "h1" }, autoApprove: false);
        Assert.Equal(PluginTrust.Approved, store.StateOf("m", manifest, new Dictionary<string, string> { ["a.dll"] = "h1" }));
        Assert.Equal(PluginTrust.Changed, store.StateOf("m", manifest, new Dictionary<string, string> { ["a.dll"] = "h2" }));
        Assert.Equal(PluginTrust.Changed, store.StateOf("m", manifest, new Dictionary<string, string>()));   // a file went missing
        Assert.Equal(PluginTrust.Changed, store.StateOf("m", Manifest("a.dll", "b.dll"), new Dictionary<string, string> { ["a.dll"] = "h1", ["b.dll"] = "x" }));

        var reloaded = new TrustStore(new AppPaths(t.Path));
        reloaded.Load();
        Assert.Equal(PluginTrust.Approved, reloaded.StateOf("m", manifest, new Dictionary<string, string> { ["a.dll"] = "h1" }));
        reloaded.Revoke("m");
        Assert.Equal(PluginTrust.NotApproved, reloaded.StateOf("m", manifest, new Dictionary<string, string> { ["a.dll"] = "h1" }));
    }

    [Fact]
    public void Following_updates_and_local_rebuilds_bring_the_stored_hash_along_only_for_those_mods()
    {
        using var t = new TempDir();
        var store = new TrustStore(new AppPaths(t.Path));
        var manifest = Manifest("a.dll");
        var old = new Dictionary<string, string> { ["a.dll"] = "h1" };
        var now = new Dictionary<string, string> { ["a.dll"] = "h2" };
        store.Approve("plain", "Plain", old, autoApprove: false);
        store.Approve("follow", "Follow", old, autoApprove: false);
        store.SetFollowUpdates("follow", true);
        store.Approve("local", "Local", old, autoApprove: true);

        Assert.Equal(PluginTrust.Changed, store.StateOf("plain", manifest, now));
        Assert.Equal(PluginTrust.Approved, store.StateOf("follow", manifest, now));
        Assert.Equal(PluginTrust.Approved, store.StateOf("local", manifest, now));   // its stored hash follows at the next launch
        var changed = store.RefreshFollowed([("plain", manifest, now), ("follow", manifest, now), ("local", manifest, now)]);
        Assert.Equal(["follow", "local"], changed.Order());
        Assert.Equal(PluginTrust.Changed, store.StateOf("plain", manifest, now));
        Assert.Equal("h2", store.Get("local")!.Files["a.dll"]);
        Assert.Empty(store.RefreshFollowed([("follow", manifest, now), ("local", manifest, now)]));   // nothing left to bring along
    }

    [Fact]
    public void A_damaged_trust_file_means_nothing_is_approved()
    {
        using var t = new TempDir();
        t.File("trust.json", "{ broken");
        var store = new TrustStore(new AppPaths(t.Path));
        store.Load();
        Assert.Empty(store.Entries);
    }
}

public class PluginExportTests
{
    private static ModInfo ModWithPlugin(string key, string manifestJson) =>
        new(key, key, ModSource.Local, "", "", null, "1", null, null, null, [], [], false, Plugin: PluginManifestReader.Parse(manifestJson));

    private const string Json = """
        { "id": "demo", "version": "1", "apiVersion": 1, "files": ["d.dll"],
          "features": [ { "id": "fast", "default": true } ],
          "settings": [ { "id": "speed", "type": "float", "default": 1.5, "min": 0.5, "max": 4 }, { "id": "mode", "type": "enum", "options": ["a", "b"] } ] }
        """;

    [Fact]
    public void Every_value_is_written_with_defaults_filled_in_and_stored_values_kept_in_range()
    {
        var installed = new Dictionary<string, ModInfo> { ["m"] = ModWithPlugin("m", Json), ["plain"] = new ModInfo("plain", "plain", ModSource.Local, "", "", null, "1", null, null, null, [], [], false) };
        var playset = new Playset
        {
            Id = "p1",
            Mods = [new() { Key = "m" }, new() { Key = "plain" }, new() { Key = "gone" }],
            PluginValues = { ["m"] = new() { ["speed"] = JsonDocument.Parse("99").RootElement.Clone(), ["mode"] = JsonDocument.Parse("\"zzz\"").RootElement.Clone() } },
        };

        using var doc = JsonDocument.Parse(PluginSettingsExport.Render(playset, installed, pluginsEnabled: true));
        var root = doc.RootElement;
        Assert.Equal("p1", root.GetProperty("playset").GetString());
        Assert.True(root.GetProperty("pluginsEnabled").GetBoolean());
        var mods = root.GetProperty("mods");
        Assert.Equal(["m"], mods.EnumerateObject().Select(p => p.Name));
        var m = mods.GetProperty("m");
        Assert.Equal("demo", m.GetProperty("id").GetString());
        Assert.True(m.GetProperty("enabled").GetBoolean());
        var values = m.GetProperty("values");
        Assert.Equal(4.0, values.GetProperty("speed").GetDouble());     // 99 is above the range: the top of it
        Assert.Equal("a", values.GetProperty("mode").GetString());      // "zzz" is not an option: the default
        Assert.True(values.GetProperty("fast").GetBoolean());
    }

    [Fact]
    public void A_plugin_counts_for_the_games_checksum_unless_its_manifest_claims_to_be_cosmetic()
    {
        const string cosmetic = """{ "id": "ui", "version": "1", "apiVersion": 1, "files": ["u.dll"], "cosmetic": true }""";
        var installed = new Dictionary<string, ModInfo> { ["loud"] = ModWithPlugin("loud", Json), ["quiet"] = ModWithPlugin("quiet", cosmetic) };
        var playset = new Playset { Mods = [new() { Key = "loud" }, new() { Key = "quiet" }] };
        using var doc = JsonDocument.Parse(PluginSettingsExport.Render(playset, installed, pluginsEnabled: true));
        var mods = doc.RootElement.GetProperty("mods");
        Assert.True(mods.GetProperty("loud").GetProperty("countsForChecksum").GetBoolean());
        Assert.False(mods.GetProperty("quiet").GetProperty("countsForChecksum").GetBoolean());
    }

    [Fact]
    public void A_plugin_switched_off_in_the_playset_and_a_disabled_mod_are_written_as_such_or_left_out()
    {
        var installed = new Dictionary<string, ModInfo> { ["m"] = ModWithPlugin("m", Json), ["n"] = ModWithPlugin("n", Json) };
        var playset = new Playset { Mods = [new() { Key = "m", PluginOff = true }, new() { Key = "n", Enabled = false }] };
        using var doc = JsonDocument.Parse(PluginSettingsExport.Render(playset, installed, pluginsEnabled: false));
        Assert.False(doc.RootElement.GetProperty("pluginsEnabled").GetBoolean());
        Assert.Equal(["m"], doc.RootElement.GetProperty("mods").EnumerateObject().Select(p => p.Name));
        Assert.False(doc.RootElement.GetProperty("mods").GetProperty("m").GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public void The_playset_file_keeps_plugin_choices_through_a_save_and_a_copy()
    {
        var playset = new Playset { Mods = [new() { Key = "m", PluginOff = true }] };
        playset.PluginValues["m"] = new() { ["speed"] = JsonDocument.Parse("2.5").RootElement.Clone() };
        var back = Playset.Import(playset.ToJson());
        Assert.True(back.Mods[0].PluginOff);
        Assert.Equal(2.5, back.PluginValues["m"]["speed"].GetDouble());
        Assert.Equal(2.5, playset.Clone().PluginValues["m"]["speed"].GetDouble());
        Assert.DoesNotContain("pluginOff", new Playset { Mods = [new() { Key = "x" }] }.ToJson());
    }
}

public class OrionFilesTests
{
    [Fact]
    public void The_last_status_and_a_loading_marker_are_read_and_a_missing_or_broken_file_is_nothing()
    {
        using var t = new TempDir();
        Assert.Null(OrionFiles.ReadStatus(t.Path));
        Assert.Null(OrionFiles.ReadMarker(t.Path));

        t.File("modloader/status.json", """
            { "version": 1, "orion": "0.1.0", "build": "v4.5.2", "startedUtc": "2026-10-15T12:00:00Z",
              "plugins": [ { "mod": "a", "id": "demo", "state": "loaded" },
                           { "mod": "b", "id": "other", "state": "skipped", "reason": "hashChanged", "detail": "d.dll changed" },
                           { "id": "no mod key" } ] }
            """);
        var report = OrionFiles.ReadStatus(t.Path)!;
        Assert.Equal(("0.1.0", "v4.5.2"), (report.Orion, report.Build));
        Assert.Equal(2, report.Plugins.Count);
        Assert.False(report.Plugins[0].IsProblem);
        Assert.True(report.Plugins[1].IsProblem);
        Assert.Equal("hashChanged", report.Plugins[1].Reason);
        Assert.Null(report.Honest); // an older Orion says nothing about honest mode

        t.File("modloader/status.json", """
            { "version": 1, "plugins": [], "honest": { "state": "mounted", "detail": "2 plugins count for the game's checksum and are in the fingerprint.", "plugins": 2 } }
            """);
        Assert.Equal(new HonestStatus("mounted", "2 plugins count for the game's checksum and are in the fingerprint.", 2), OrionFiles.ReadStatus(t.Path)!.Honest);
        t.File("modloader/status.json", """{ "version": 1, "plugins": [], "honest": { "state": "failed", "detail": "" } }""");
        Assert.Equal(new HonestStatus("failed", null, 0), OrionFiles.ReadStatus(t.Path)!.Honest);

        t.File("modloader/loading.json", """{ "mod": "b", "id": "other", "file": "orion/o.dll" }""");
        Assert.Equal(new LoadingMarker("b", "other", "orion/o.dll"), OrionFiles.ReadMarker(t.Path));
        OrionFiles.ClearMarker(t.Path);
        Assert.Null(OrionFiles.ReadMarker(t.Path));

        t.File("modloader/status.json", "garbage");
        Assert.Null(OrionFiles.ReadStatus(t.Path));
    }
}

public class InstallerTests
{
    private static string MakeZip(TempDir t, string name, params (string Path, string Text)[] files)
    {
        var path = Path.Combine(t.Path, name);
        using var zip = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create);
        foreach (var (p, text) in files)
        {
            using var w = new StreamWriter(zip.CreateEntry(p).Open());
            w.Write(text);
        }
        return path;
    }

    private const string Descriptor = "name=\"Fleet Trails\"\nversion=\"1.0\"\nsupported_version=\"v4.*\"\n";

    [Fact]
    public void A_mod_in_an_archive_is_installed_with_its_mod_file_whether_wrapped_in_a_folder_or_not()
    {
        using var t = new TempDir();
        var user = t.Dir("user");
        var wrapped = MakeZip(t, "wrapped.zip", ("Fleet Trails/descriptor.mod", Descriptor), ("Fleet Trails/common/a.txt", "x = { }"), ("Fleet Trails/gfx/", ""));
        var seen = ModInstaller.Inspect(wrapped, user);
        Assert.Equal(("Fleet Trails", 2, false), (seen.Name, seen.Files, seen.AlreadyInstalled));

        var result = ModInstaller.Install(wrapped, user);
        Assert.Equal("Fleet Trails", result.Key);
        Assert.True(File.Exists(Path.Combine(user, "mod", "Fleet Trails", "common", "a.txt")));
        var mod = Assert.Single(ModScanner.Scan(user).Mods);
        Assert.Equal(("Fleet Trails", "Fleet Trails", "v4.*"), (mod.Key, mod.Name, mod.SupportedVersion));
        Assert.False(mod.ContentMissing);
        Assert.True(ModInstaller.Inspect(wrapped, user).AlreadyInstalled);

        var flat = MakeZip(t, "flat.zip", ("descriptor.mod", "name=\"Flat One\"\n"), ("events/e.txt", "namespace = x"));
        Assert.Equal("Flat One", ModInstaller.Install(flat, user).Key);
    }

    [Fact]
    public void An_installed_mod_is_only_replaced_when_asked()
    {
        using var t = new TempDir();
        var user = t.Dir("user");
        var v1 = MakeZip(t, "v1.zip", ("descriptor.mod", Descriptor), ("common/old.txt", "x"));
        var v2 = MakeZip(t, "v2.zip", ("descriptor.mod", Descriptor), ("common/new.txt", "x"));
        ModInstaller.Install(v1, user);
        Assert.Throws<ArtemisException>(() => ModInstaller.Install(v2, user));
        ModInstaller.Install(v2, user, replace: true);
        Assert.False(File.Exists(Path.Combine(user, "mod", "Fleet Trails", "common", "old.txt")));
        Assert.True(File.Exists(Path.Combine(user, "mod", "Fleet Trails", "common", "new.txt")));
    }

    [Fact]
    public void An_archive_that_is_not_a_mod_or_tries_to_escape_is_refused_and_leaves_nothing_behind()
    {
        using var t = new TempDir();
        var user = t.Dir("user");
        Assert.Throws<ArtemisException>(() => ModInstaller.Install(MakeZip(t, "none.zip", ("readme.txt", "hi")), user));
        File.WriteAllText(Path.Combine(t.Path, "text.zip"), "not a zip at all");
        Assert.Contains("zip", Assert.Throws<ArtemisException>(() => ModInstaller.Install(Path.Combine(t.Path, "text.zip"), user)).Message);

        var sneaky = MakeZip(t, "sneaky.zip", ("descriptor.mod", Descriptor), ("../../escaped.txt", "bad"), ("common/ok.txt", "x"));
        ModInstaller.Install(sneaky, user);
        Assert.False(File.Exists(Path.Combine(t.Path, "escaped.txt")));
        Assert.False(File.Exists(Path.Combine(user, "escaped.txt")));
        Assert.True(File.Exists(Path.Combine(user, "mod", "Fleet Trails", "common", "ok.txt")));
    }

    [Fact]
    public void A_plugin_in_an_archive_is_reported_before_and_after_installing()
    {
        using var t = new TempDir();
        var user = t.Dir("user");
        var zip = MakeZip(t, "p.zip", ("descriptor.mod", "name=\"Plug\"\n"), ("orion/p.dll", "MZ"),
            ("modloader.json", """{ "id": "plug", "version": "1", "apiVersion": 1, "files": ["orion/p.dll"] }"""));
        var seen = ModInstaller.Inspect(zip, user);
        Assert.Equal(["orion/p.dll"], seen.Dlls);
        Assert.Equal("plug", seen.Plugin!.Manifest!.Id);
        Assert.Equal("plug", ModInstaller.Install(zip, user).Plugin!.Manifest!.Id);

        var loose = MakeZip(t, "l.zip", ("descriptor.mod", "name=\"Loose\"\n"), ("hook.dll", "MZ"));
        var looseSeen = ModInstaller.Inspect(loose, user);
        Assert.Equal(["hook.dll"], looseSeen.Dlls);
        Assert.Null(looseSeen.Plugin);
    }

    [Fact]
    public void Create_makes_a_mod_the_scanner_finds_with_an_optional_plugin_manifest_that_is_valid()
    {
        using var t = new TempDir();
        var plain = ModInstaller.Create(t.Path, "My New Mod", "v4.5.2", plugin: false);
        Assert.Null(plain.Plugin);
        var withPlugin = ModInstaller.Create(t.Path, "Plugin Mod", "v4.5.2", plugin: true);
        Assert.True(withPlugin.Plugin!.IsValid);
        Assert.Equal(["orion/mod.plugin-mod.dll"], withPlugin.Plugin.Manifest!.Files);
        Assert.Equal(["v4.*"], withPlugin.Plugin.Manifest.Builds);

        var mods = ModScanner.Scan(t.Path).Mods.ToDictionary(m => m.Key);
        Assert.Equal("v4.*", mods["My New Mod"].SupportedVersion);
        Assert.Equal(2, mods.Count);
        Assert.Throws<ArtemisException>(() => ModInstaller.Create(t.Path, "My New Mod", "v4.5.2", plugin: false));
        Assert.Throws<ArtemisException>(() => ModInstaller.Create(t.Path, "  ", "v4.5.2", plugin: false));
        Assert.Equal("ab", ModInstaller.SafeName("a:b"));
        Assert.Equal("What", ModInstaller.SafeName("What?*. "));
    }
}

public class StubCheckTests
{
    [Fact]
    public void A_winmm_dll_that_is_not_ours_is_reported_and_no_file_is_nothing()
    {
        using var t = new TempDir();
        Assert.Null(Artemis.Core.Game.StubCheck.Find(t.Path));
        t.File("winmm.dll", "not really a dll");
        var found = Artemis.Core.Game.StubCheck.Find(t.Path)!;
        Assert.EndsWith("winmm.dll", found.Path);
        Assert.StartsWith("foreign-winmm|", found.NoticeId);
    }
}
