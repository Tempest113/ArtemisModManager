using Artemis.Core.Mods;

namespace Artemis.Core.Tests;

public class ModFolderHealthTests
{
    private static string Descriptor(string name) => $"name=\"{name}\"\nversion=\"1\"\nsupported_version=\"v4.*\"\n";

    [Fact]
    public void Mod_folders_without_a_mod_file_are_found_at_any_depth_and_registered_ones_are_not()
    {
        using var t = new TempDir();
        t.File("mod/plain/descriptor.mod", Descriptor("Plain"));
        t.File("mod/outer/inner/descriptor.mod", Descriptor("Inner"));
        t.File("mod/outer/inner/common/descriptor.mod", "a file inside the mod's own content, not another mod");
        t.File("mod/deep/a/b/c/descriptor.mod", Descriptor("Deep"));
        t.File("mod/registered/descriptor.mod", Descriptor("Registered"));
        t.File("mod/Registered.mod", "name=\"Registered\"\npath=\"mod/registered\"\n");
        t.File("mod/not a mod/readme.txt", "no descriptor here");

        var found = ModFolderHealth.FindUnregistered(t.Path, ModScanner.Scan(t.Path).Mods).Select(f => f.Relative).ToList();
        Assert.Equal(["deep/a/b/c", "outer/inner", "plain"], found);
    }

    [Fact]
    public void A_mod_file_is_made_for_each_with_a_path_to_its_folder_and_the_game_then_sees_it()
    {
        using var t = new TempDir();
        t.File("mod/outer/inner/descriptor.mod", Descriptor("Nested Mod"));
        t.File("mod/Nested Mod.mod", "name=\"Taken\"\npath=\"mod/elsewhere\"\n");   // a different mod already has this file name
        t.File("mod/plain/descriptor.mod", "name=\"Plain\"\npath=\"mod/somewhere/else\"\narchive=\"old.zip\"\n");

        var made = ModFolderHealth.CreateDescriptors(t.Path, ModFolderHealth.FindUnregistered(t.Path, ModScanner.Scan(t.Path).Mods));
        Assert.Equal(["Nested Mod 2", "Plain"], made.Select(m => m.Key).Order());
        Assert.Contains("path=\"mod/outer/inner\"", File.ReadAllText(Path.Combine(t.Path, "mod", "Nested Mod 2.mod")));
        Assert.Equal("name=\"Taken\"\npath=\"mod/elsewhere\"\n", File.ReadAllText(Path.Combine(t.Path, "mod", "Nested Mod.mod")));  // never replaced
        var plain = File.ReadAllText(Path.Combine(t.Path, "mod", "Plain.mod"));
        Assert.Contains("path=\"mod/plain\"", plain);
        Assert.DoesNotContain("somewhere", plain);   // the descriptor's own stale path and archive lines are dropped
        Assert.DoesNotContain("archive", plain);

        var scan = ModScanner.Scan(t.Path).Mods;
        var nested = scan.Single(m => m.Key == "Nested Mod 2");
        Assert.False(nested.ContentMissing);
        Assert.EndsWith(Path.Combine("mod", "outer", "inner"), nested.ContentDir);
        Assert.Empty(ModFolderHealth.FindUnregistered(t.Path, scan));   // and nothing is made a second time
    }

    [Fact]
    public void A_downloaded_workshop_item_without_a_ugc_file_gets_one_and_registered_ones_are_left_alone()
    {
        using var t = new TempDir();
        var workshop = Path.Combine(t.Path, "steam", "281990");
        t.File("steam/281990/111/descriptor.mod", Descriptor("New Portraits") + "path=\"stale\"\n");
        t.File("steam/281990/222/descriptor.mod", Descriptor("Known"));
        t.File("steam/281990/notes/descriptor.mod", Descriptor("Not an id"));
        t.File("mod/ugc_222.mod", "name=\"Known\"\nremote_file_id=\"222\"\npath=\"" + Path.Combine(workshop, "222").Replace('\\', '/') + "\"\n");

        var made = ModFolderHealth.RegisterWorkshop(t.Path, workshop, ModScanner.Scan(t.Path).Mods);
        var only = Assert.Single(made);
        Assert.Equal(("ugc_111", "New Portraits"), (only.Key, only.Name));
        var text = File.ReadAllText(Path.Combine(t.Path, "mod", "ugc_111.mod"));
        Assert.Contains("remote_file_id=\"111\"", text);
        Assert.DoesNotContain("stale", text);
        Assert.Equal(ModSource.Workshop, ModScanner.Scan(t.Path).Mods.Single(m => m.Key == "ugc_111").Source);
        Assert.Empty(ModFolderHealth.RegisterWorkshop(t.Path, workshop, ModScanner.Scan(t.Path).Mods));   // nothing is made a second time
    }

    [Fact]
    public void Leftover_mod_files_are_sent_away_and_a_mod_that_still_has_its_files_is_never_touched()
    {
        using var t = new TempDir();
        t.File("mod/Gone.mod", "name=\"Gone\"\npath=\"mod/gone\"\n");
        t.File("mod/GoneZip.mod", "name=\"Gone zip\"\narchive=\"mod/gone.zip\"\n");
        t.File("mod/NoPath.mod", "name=\"No path\"\n");
        t.File("mod/Here.mod", "name=\"Here\"\npath=\"mod/here\"\n");
        t.File("mod/here/descriptor.mod", Descriptor("Here"));
        t.File("mod/Back.mod", "name=\"Back\"\npath=\"mod/back\"\n");   // missing when scanned, its folder returns before the removal
        var scanned = ModScanner.Scan(t.Path).Mods;
        var missing = scanned.Where(m => m.ContentMissing).ToList();
        Assert.Equal(["Back", "Gone", "GoneZip", "NoPath"], missing.Select(m => m.Key).Order());
        t.File("mod/back/descriptor.mod", Descriptor("Back"));

        // the real remover is the Recycle Bin, so a recording one stands in
        var sent = new List<string>();
        var moved = ModFolderHealth.RemoveDescriptors(t.Path, scanned, sent.Add);   // even handed every mod, only a real leftover goes
        Assert.Equal(3, moved);
        Assert.Equal(["Gone.mod", "GoneZip.mod", "NoPath.mod"], sent.Select(Path.GetFileName).Order());
    }

    [Fact]
    public void A_mod_file_that_can_not_be_removed_is_not_counted()
    {
        using var t = new TempDir();
        t.File("mod/Gone.mod", "name=\"Gone\"\npath=\"mod/gone\"\n");
        t.File("mod/Gone2.mod", "name=\"Gone2\"\npath=\"mod/gone2\"\n");
        var scanned = ModScanner.Scan(t.Path).Mods;
        Assert.Equal(1, ModFolderHealth.RemoveDescriptors(t.Path, scanned, f => { if (f.EndsWith("Gone.mod")) throw new IOException("in use"); }));
    }

    [Fact]
    public void A_mod_file_outside_the_mod_folder_is_never_moved()
    {
        using var t = new TempDir();
        var stray = t.File("elsewhere/Stray.mod", "name=\"Stray\"\npath=\"mod/none\"\n");
        var info = ModScanner.ReadOuter(stray, t.Path);
        Assert.True(info.ContentMissing);
        var sent = new List<string>();
        Assert.Equal(0, ModFolderHealth.RemoveDescriptors(t.Path, [info], sent.Add));
        Assert.Empty(sent);
    }
}
