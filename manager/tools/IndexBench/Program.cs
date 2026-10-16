using System.Diagnostics;
using Artemis.Core.Game;
using Artemis.Core.Index;
using Artemis.Core.Launch;
using Artemis.Core.Mods;

// usage: IndexBench <cache folder> [--keep]   Reads the real Documents mod folder and game folder; writes only into the cache folder.
var cache = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "artemis-bench");
var dbFile = Path.Combine(cache, "index.db");
if (!args.Contains("--keep") && Directory.Exists(cache)) Directory.Delete(cache, recursive: true);

var gameDir = GameLocator.FindGameDir() ?? throw new InvalidOperationException("Stellaris was not found.");
var game = GameInstall.Open(gameDir);
var clock = Stopwatch.StartNew();
var mods = ModScanner.Scan(game.UserDir).Mods;
Console.WriteLine($"scan of {mods.Count} .mod files: {clock.ElapsedMilliseconds} ms");

var store = new IndexStore(dbFile);
var indexer = new Indexer(store);
for (var run = 1; run <= 2; run++)
{
    var result = indexer.Update(mods, game);
    Console.WriteLine($"update {run}: {result.Elapsed.TotalSeconds:0.00} s  mods indexed {result.ModsIndexed}, unchanged {result.ModsUnchanged}, removed {result.ModsRemoved}, game {(result.GameIndexed ? "indexed" : "cached")}");
}

var stats = store.AllStats();
Console.WriteLine($"rows: {stats.Values.Sum(s => s.Files):N0} files, {stats.Values.Sum(s => s.Parsed):N0} read, {stats.Values.Sum(s => s.Defs):N0} names");
Console.WriteLine($"mods only: {stats.Where(s => s.Key.Kind == "mod").Sum(s => s.Value.Files):N0} files, {stats.Where(s => s.Key.Kind == "mod").Sum(s => s.Value.Defs):N0} names");
foreach (var ext in new[] { "", "-wal" })
    if (File.Exists(dbFile + ext)) Console.WriteLine($"{Path.GetFileName(dbFile + ext)}: {new FileInfo(dbFile + ext).Length / 1048576.0:0.0} MB");
Console.WriteLine($"peak memory: {Process.GetCurrentProcess().PeakWorkingSet64 / 1048576} MB");

// A look at what was read, to spot-check the scanner: the folders with the most names in the game, and how many names mods share with it.
var gameKey = Indexer.GameBuildKey(game);
var vanilla = store.Names(SourceKind.Vanilla, gameKey);
foreach (var g in vanilla.Where(n => !n.Domain.StartsWith("localisation")).GroupBy(n => n.Domain).OrderByDescending(g => g.Count()).Take(8))
    Console.WriteLine($"  game {g.Key}: {g.Count():N0} names in {g.Select(n => n.Path).Distinct().Count():N0} files");
var gameNames = vanilla.Select(n => (n.Domain, n.Name)).ToHashSet();
var shared = mods.Sum(m => store.Names(SourceKind.Mod, m.Key).Count(n => gameNames.Contains((n.Domain, n.Name))));
Console.WriteLine($"names that mods define again from the game: {shared:N0}");

// Conflicts for the mods the game's own list (dlc_load.json) has switched on, in that order.
var byKey = mods.ToDictionary(m => m.Key, StringComparer.OrdinalIgnoreCase);
var order = (File.Exists(game.DlcLoadPath) ? DlcLoad.ToPlaysetMods(DlcLoad.Read(game.DlcLoadPath).EnabledMods) : [])
    .Where(m => m.Enabled && byKey.ContainsKey(m.Key)).Select(m => byKey[m.Key])
    .Select(m => new Artemis.Core.Conflicts.ConflictMod(m.Key, m.Name, m.Dependencies)).ToList();
Artemis.Core.Conflicts.ConflictReport report = null!;
for (var run = 1; run <= 3; run++)
{
    var t = Stopwatch.StartNew();
    report = Artemis.Core.Conflicts.ConflictEngine.Find(store, order, gameKey);
    Console.WriteLine($"conflicts {run}: {t.Elapsed.TotalSeconds:0.00} s for {order.Count} enabled mods");
}
var all = report.Conflicts;
Console.WriteLine($"{all.Count:N0} conflicts: {all.Count(c => c.ModCount >= 2):N0} between mods, {all.Count(c => c.ModCount == 1 && c.InvolvesGame):N0} over the game; intended {all.Count(c => c.Intended):N0}");
foreach (var g in all.GroupBy(c => (c.Kind, Scope: c.ModCount >= 2 ? "mods" : "game")).OrderBy(g => g.Key))
    Console.WriteLine($"  {g.Key.Kind} {g.Key.Scope}: {g.Count():N0}");
foreach (var c in all.Where(c => c.ModCount >= 2 && !c.Intended && c.Kind == Artemis.Core.Conflicts.ConflictKind.Definition).Take(4))
    Console.WriteLine($"  e.g. {c.Domain} {c.Name}: {string.Join(" < ", c.Entries.Select(e => e.SourceName))}; winner {c.WinnerEntry.SourceName}. {c.Why}");
