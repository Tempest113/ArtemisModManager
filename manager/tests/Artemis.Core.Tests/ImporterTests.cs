using Artemis.Core.Playsets;
using Microsoft.Data.Sqlite;

namespace Artemis.Core.Tests;

public class ImporterTests
{
    [Fact]
    public void Irony_collections_come_from_the_newest_database_and_skip_other_games()
    {
        using var t = new TempDir();
        File.WriteAllText(Path.Combine(t.Path, "Database_1.9.json"), """[{"Name":"ModCollection","Value":[{"Game":"Stellaris","Name":"Old","Mods":["mod/old.mod"]}]}]""");
        File.WriteAllText(Path.Combine(t.Path, "Database_1.10.json"), """
            [{"Name":"AppState","Value":{}},
             {"Name":"ModCollection","Value":[
               {"Game":"Stellaris","Name":"Main","Mods":["mod/ugc_1.mod","mod/My Mod.mod"]},
               {"Game":"Hearts of Iron IV","Name":"Other","Mods":["mod/x.mod"]},
               {"Game":"Stellaris","Name":"","Mods":[]}]}]
            """);

        var found = PlaysetImporters.FromIrony(t.Path);

        Assert.Equal(["Main", "Unnamed"], found.Select(p => p.Name));
        Assert.Equal(["ugc_1", "My Mod"], found[0].Mods.Select(m => m.Key));
        Assert.All(found[0].Mods, m => Assert.True(m.Enabled));
    }

    [Fact]
    public void Irony_files_we_cannot_read_import_nothing()
    {
        using var t = new TempDir();
        Assert.Empty(PlaysetImporters.FromIrony(Path.Combine(t.Path, "missing")));
        File.WriteAllText(Path.Combine(t.Path, "Database_1.0.json"), "not json");
        Assert.Empty(PlaysetImporters.FromIrony(t.Path));
    }

    [Fact]
    public void Launcher_playsets_keep_order_and_enabled_flags_and_skip_removed_ones()
    {
        using var t = new TempDir();
        var file = Path.Combine(t.Path, "launcher-v2.sqlite");
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Pooling = false }.ToString()))
        {
            db.Open();
            var cmd = db.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE playsets (id TEXT, name TEXT, isActive INTEGER, isRemoved INTEGER);
                CREATE TABLE mods (id TEXT, gameRegistryId TEXT);
                CREATE TABLE playsets_mods (playsetId TEXT, modId TEXT, enabled INTEGER, position INTEGER);
                INSERT INTO playsets VALUES ('p1','Main',1,0), ('p2','Gone',0,1);
                INSERT INTO mods VALUES ('m1','mod/ugc_1.mod'), ('m2','mod/ugc_2.mod'), ('m3',NULL);
                INSERT INTO playsets_mods VALUES ('p1','m2',0,2), ('p1','m1',1,1), ('p1','m3',1,3), ('p2','m1',1,1);
                """;
            cmd.ExecuteNonQuery();
        }

        var found = Assert.Single(PlaysetImporters.FromLauncher(file));

        Assert.Equal("Main", found.Name);
        Assert.Equal(["ugc_1", "ugc_2"], found.Mods.Select(m => m.Key));
        Assert.Equal([true, false], found.Mods.Select(m => m.Enabled));
        Assert.Empty(PlaysetImporters.FromLauncher(Path.Combine(t.Path, "nope.sqlite")));
    }
}
