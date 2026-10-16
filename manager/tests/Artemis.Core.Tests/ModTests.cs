using Artemis.Core.Game;
using Artemis.Core.Mods;
using Artemis.Core.Pdx;

namespace Artemis.Core.Tests;

public class PdxScriptTests
{
    [Fact]
    public void Reads_strings_lists_comments_and_bom()
    {
        var node = PdxScript.Parse("\uFEFF# comment\nname=\"My Mod\"\ntags={\n\t\"Gameplay\"\n\t\"UI\"\n}\nsupported_version=v4.*\n");
        Assert.Equal("My Mod", node.GetString("name"));
        Assert.Equal("v4.*", node.GetString("supported_version"));
        Assert.Equal(["Gameplay", "UI"], node.GetList("tags"));
    }

    [Fact]
    public void Keys_are_case_insensitive_and_missing_keys_are_null()
    {
        var node = PdxScript.Parse("Name=\"X\"");
        Assert.Equal("X", node.GetString("name"));
        Assert.Null(node.GetString("version"));
        Assert.Empty(node.GetList("tags"));
    }

    [Theory]
    [InlineData("name=\"unterminated")]
    [InlineData("} } name")]
    [InlineData("= = =")]
    [InlineData("tags={ \"a\" ")]
    public void Never_throws_on_broken_input(string text) => PdxScript.Parse(text);

    [Fact]
    public void Escaped_quotes_stay_in_the_value()
    {
        Assert.Equal("say \"hi\"", PdxScript.Parse("name=\"say \\\"hi\\\"\"").GetString("name"));
    }
}

public class ModScannerTests
{
    private static ModInfo ScanOne(TempDir t, string file) =>
        ModScanner.Scan(t.Path).Mods.Single(m => m.Key == file);

    [Fact]
    public void Remembers_when_the_outer_file_was_written()
    {
        using var t = new TempDir();
        t.File("mod/a.mod", "name=\"A\"\npath=\"mod/a\"\n");
        var when = new DateTime(2025, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(Path.Combine(t.Path, "mod", "a.mod"), when);
        Assert.Equal(when, ScanOne(t, "a").Modified);
    }

    [Fact]
    public void Reads_a_workshop_mod_from_its_outer_file()
    {
        using var t = new TempDir();
        t.Dir("workshop/content/281990/42");
        var content = t.Path.Replace('\\', '/') + "/workshop/content/281990/42";
        t.File("mod/ugc_42.mod",
            $"path=\"{content}\"\nname=\"Fleet Trails\"\nremote_file_id=\"42\"\nsupported_version=\"v4.*\"\ntags={{\n\"Gameplay\"\n}}\ndependencies={{\n\"Base Mod\"\n}}\n");

        var mod = ScanOne(t, "ugc_42");
        Assert.Equal("Fleet Trails", mod.Name);
        Assert.Equal(ModSource.Workshop, mod.Source);
        Assert.Equal("42", mod.RemoteFileId);
        Assert.Equal("v4.*", mod.SupportedVersion);
        Assert.Equal(["Gameplay"], mod.Tags);
        Assert.Equal(["Base Mod"], mod.Dependencies);
        Assert.False(mod.ContentMissing);
        Assert.Equal("mod/ugc_42.mod", mod.LoadEntry);
    }

    [Fact]
    public void A_mod_inside_the_mod_folder_is_local()
    {
        using var t = new TempDir();
        t.Dir("mod/Mine");
        t.File("mod/Mine.mod", "path=\"mod/Mine\"\nname=\"Mine\"\nsupported_version=\"v4.5.*\"\n");
        Assert.Equal(ModSource.Local, ScanOne(t, "Mine").Source);
    }

    [Fact]
    public void Missing_fields_come_from_the_inner_descriptor()
    {
        using var t = new TempDir();
        t.File("mod/Hand/descriptor.mod", "name=\"From Inner\"\nsupported_version=\"v4.4.*\"\nversion=\"2\"\n");
        t.File("mod/Hand.mod", "path=\"mod/Hand\"\n");
        var mod = ScanOne(t, "Hand");
        Assert.Equal("From Inner", mod.Name);
        Assert.Equal("v4.4.*", mod.SupportedVersion);
        Assert.Equal("2", mod.Version);
    }

    [Fact]
    public void A_nameless_mod_falls_back_to_its_file_name_and_missing_content_is_flagged()
    {
        using var t = new TempDir();
        t.File("mod/Ghost.mod", "path=\"mod/Nowhere\"\n");
        var mod = ScanOne(t, "Ghost");
        Assert.Equal("Ghost", mod.Name);
        Assert.True(mod.ContentMissing);
    }

    [Fact]
    public void Results_are_sorted_by_name_and_a_missing_mod_folder_is_empty()
    {
        using var t = new TempDir();
        Assert.Empty(ModScanner.Scan(t.Path).Mods);
        t.File("mod/b.mod", "name=\"beta\"\n");
        t.File("mod/a.mod", "name=\"Alpha\"\n");
        Assert.Equal(["Alpha", "beta"], ModScanner.Scan(t.Path).Mods.Select(m => m.Name));
    }
}

public class GameTests
{
    [Fact]
    public void Parses_steam_library_folders()
    {
        var vdf = "\"libraryfolders\"\n{\n\t\"0\"\n\t{\n\t\t\"path\"\t\t\"C:\\\\Program Files (x86)\\\\Steam\"\n\t}\n\t\"1\"\n\t{\n\t\t\"path\"\t\t\"D:\\\\SteamLibrary\"\n\t}\n}";
        Assert.Equal([@"C:\Program Files (x86)\Steam", @"D:\SteamLibrary"], GameLocator.ParseLibraryFolders(vdf));
    }

    [Fact]
    public void Opens_an_install_from_launcher_settings()
    {
        using var t = new TempDir();
        t.File("game/launcher-settings.json",
            "{ \"rawVersion\": \"v4.5.2\", \"exePath\": \"./stellaris.exe\", \"exeArgs\": [ \"-gdpr-compliant\" ], \"gameDataPath\": \"%USER_DOCUMENTS%/Paradox Interactive/Stellaris\", }");
        var game = GameInstall.Open(System.IO.Path.Combine(t.Path, "game"));
        Assert.Equal("v4.5.2", game.Settings.RawVersion);
        Assert.Equal(["-gdpr-compliant"], game.Settings.ExeArgs);
        Assert.EndsWith(System.IO.Path.Combine("Paradox Interactive", "Stellaris", "mod"), game.ModDir);
        Assert.EndsWith("stellaris.exe", game.ExePath);
    }
}
