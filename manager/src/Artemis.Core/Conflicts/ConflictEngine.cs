using System.Diagnostics;
using Artemis.Core.Index;
using Artemis.Core.Text;

namespace Artemis.Core.Conflicts;

public enum ConflictKind { File, Definition, Text }

/// <summary>One side of a conflict: who provides the thing, and in which file and line.</summary>
public sealed record ConflictEntry(string SourceKey, string SourceName, bool IsGame, string Path, int Line);

/// <summary>
/// Two or more sources (mods, or a mod and the game) providing the same thing. <see cref="Entries"/> run in load order, the
/// game first; <see cref="Winner"/> is the entry the game uses and <see cref="Why"/> says why, in a sentence.
/// <see cref="Intended"/> is true when the winner's descriptor asks to load after every mod it overrides, which usually
/// means the override is on purpose (a hint from the author, not a promise).
/// </summary>
public sealed record Conflict(ConflictKind Kind, string Domain, string Name, MergeRule Rule, IReadOnlyList<ConflictEntry> Entries, int Winner, string Why, bool Intended)
{
    public ConflictEntry WinnerEntry => Entries[Winner];
    public int ModCount => Entries.Count(e => !e.IsGame);
    public bool InvolvesGame => Entries.Any(e => e.IsGame);
}

/// <summary>A mod of the playset as the engine needs it. <see cref="Dependencies"/> are the names its descriptor asks to load after.</summary>
public sealed record ConflictMod(string Key, string Name, IReadOnlyList<string> Dependencies);

public sealed record ConflictReport(IReadOnlyList<Conflict> Conflicts, IReadOnlyList<string> NotIndexed, TimeSpan Elapsed);

/// <summary>
/// Finds what the enabled mods of a playset (and the game) provide twice, from the index alone: no mod file is opened.
/// Three kinds: whole files with the same path, definitions with the same name in the same folder, and localisation keys.
/// </summary>
public static class ConflictEngine
{
    public const string GameName = "Stellaris";

    private sealed record Source(string Kind, string Key, string Name, bool IsGame, IReadOnlyList<string> Dependencies);

    private readonly record struct Hit(int Source, string Path, int Line);

    /// <param name="mods">The enabled mods in load order, the one loaded first at the top.</param>
    /// <param name="gameKey">The game's build key (<see cref="Indexer.GameBuildKey"/>), or null to leave the game out.</param>
    public static ConflictReport Find(IndexStore store, IReadOnlyList<ConflictMod> mods, string? gameKey, CancellationToken ct = default)
    {
        var clock = Stopwatch.StartNew();
        var sources = new List<Source>();
        var notIndexed = new List<string>();
        if (gameKey is not null && store.Fingerprint(SourceKind.Vanilla, gameKey) is not null)
            sources.Add(new Source(SourceKind.Vanilla, gameKey, GameName, true, []));
        foreach (var mod in mods)
        {
            if (store.Fingerprint(SourceKind.Mod, mod.Key) is null) notIndexed.Add(mod.Name);
            else sources.Add(new Source(SourceKind.Mod, mod.Key, mod.Name, false, mod.Dependencies));
        }

        // Who provides each path, lowest priority first. A path two sources provide is used from the last one only.
        var owners = new Dictionary<string, List<Hit>>(StringComparer.OrdinalIgnoreCase);
        for (var s = 0; s < sources.Count; s++)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var path in store.Paths(sources[s].Kind, sources[s].Key))
            {
                if (!GameFiles.IsLoadedByGame(path)) continue;
                if (!owners.TryGetValue(path, out var list)) owners[path] = list = [];
                list.Add(new Hit(s, path, 0));
            }
        }

        var conflicts = new List<Conflict>();
        foreach (var (path, list) in owners)
        {
            if (list.Count < 2) continue;
            var winner = list.Count - 1;
            conflicts.Add(new Conflict(ConflictKind.File, GameFiles.Domain(path), path, MergeRule.LastWins, list.Select(h => Entry(sources, h)).ToList(), winner,
                list.Any(h => sources[h.Source].IsGame)
                    ? Loc.T("core.conflict_file_over_game")
                    : Loc.T("core.conflict_file_same_path"),
                IsIntended(sources, list.Select(h => h.Source).ToList(), list[winner].Source)));
        }

        // Names, from the files the game really reads (the last provider of each path). Mods first, then only those of the game's names a mod also uses.
        var named = new Dictionary<(string Domain, string Name), List<Hit>>();
        for (var s = 0; s < sources.Count; s++)
        {
            if (sources[s].IsGame) continue;
            ct.ThrowIfCancellationRequested();
            var source = s;
            string? lastPath = null;
            var effective = false;
            store.VisitNames(sources[s].Kind, sources[s].Key, ConflictRules.IsDefinitionFolder, (path, domain, name, line) =>
            {
                if (!ReferenceEquals(path, lastPath)) (lastPath, effective) = (path, owners.TryGetValue(path, out var list) && list[^1].Source == source);
                if (!effective) return;
                var key = (domain, name.ToString());
                if (!named.TryGetValue(key, out var hits)) named[key] = hits = [];
                hits.Add(new Hit(source, path, line));
            });
        }
        if (sources.Count > 0 && sources[0].IsGame)
        {
            // The game has far more names than any mod list uses, so a name is only turned into a string when some mod also defines it.
            var modNames = new Dictionary<string, HashSet<string>>();
            foreach (var (domain, name) in named.Keys)
            {
                if (!modNames.TryGetValue(domain, out var set)) modNames[domain] = set = [];
                set.Add(name);
            }
            string? lastPath = null, lastDomain = null;
            var effective = false;
            HashSet<string>.AlternateLookup<ReadOnlySpan<char>> known = default;
            store.VisitNames(sources[0].Kind, sources[0].Key, modNames.ContainsKey, (path, domain, name, line) =>
            {
                if (!ReferenceEquals(domain, lastDomain)) (lastDomain, known) = (domain, modNames[domain].GetAlternateLookup<ReadOnlySpan<char>>());
                if (!known.Contains(name)) return;
                if (!ReferenceEquals(path, lastPath)) (lastPath, effective) = (path, owners.TryGetValue(path, out var list) && list[^1].Source == 0);
                if (effective) named[(domain, name.ToString())].Add(new Hit(0, path, line));
            });
        }
        foreach (var ((domain, name), hits) in named)
            if (hits.Select(h => h.Source).Distinct().Count() > 1) conflicts.Add(Definition(sources, domain, name, hits));

        conflicts.Sort((a, b) =>
        {
            var c = a.Kind.CompareTo(b.Kind);
            if (c == 0) c = IsEnglish(b).CompareTo(IsEnglish(a)); // English text first: it is what most people read
            if (c == 0) c = string.Compare(a.Domain, b.Domain, StringComparison.OrdinalIgnoreCase);
            return c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.Ordinal);
        });
        return new ConflictReport(conflicts, notIndexed, clock.Elapsed);
    }

    private static bool IsEnglish(Conflict c) => c.Domain == "localisation/l_english";

    private static Conflict Definition(List<Source> sources, string domain, string name, List<Hit> hits)
    {
        var text = domain.StartsWith("localisation/", StringComparison.Ordinal);
        var rule = text ? MergeRule.LastWins : ConflictRules.For(domain);
        // One entry per source: the one that wins inside that source. Then the winner among the sources.
        var picks = hits.GroupBy(h => h.Source).Select(g => Pick(g, rule, text)).OrderBy(h => h.Source).ToList();
        var winner = Pick(picks, rule, text);
        return new Conflict(text ? ConflictKind.Text : ConflictKind.Definition, domain, name, rule, picks.Select(h => Entry(sources, h)).ToList(),
            picks.IndexOf(winner), Why(domain, rule, text, picks, winner), IsIntended(sources, picks.Select(h => h.Source).ToList(), winner.Source));
    }

    private static string FileName(string path) => path[(path.LastIndexOf('/') + 1)..];

    private static bool IsReplace(Hit h) => h.Path.Contains("/replace/", StringComparison.OrdinalIgnoreCase);

    /// <summary>Which hit the game uses: by file name in the folder's direction, with text in a replace folder first, and the load order settling ties.</summary>
    private static Hit Pick(IEnumerable<Hit> hits, MergeRule rule, bool text)
    {
        var candidates = hits.ToList();
        if (text && candidates.Any(IsReplace)) candidates = candidates.Where(IsReplace).ToList();
        var ordered = candidates.OrderBy(h => FileName(h.Path).ToLowerInvariant(), StringComparer.Ordinal).ThenBy(h => h.Source).ThenBy(h => h.Path, StringComparer.Ordinal).ToList();
        return rule == MergeRule.FirstWins ? ordered[0] : ordered[^1];
    }

    private static string Why(string domain, MergeRule rule, bool text, List<Hit> picks, Hit winner)
    {
        if (text && IsReplace(winner) && picks.Any(p => !IsReplace(p))) return Loc.T("core.conflict_replace_folder");
        var first = rule == MergeRule.FirstWins;
        var why = Loc.T(first ? "core.conflict_first_wins" : "core.conflict_last_wins", ("label", Label(domain, text)));
        var key = FileName(winner.Path).ToLowerInvariant();
        if (picks.Any(p => p.Source != winner.Source && FileName(p.Path).ToLowerInvariant() == key)) why += " " + Loc.T("core.conflict_names_match");
        return rule == MergeRule.Unreliable ? why + " " + Loc.T("core.conflict_unreliable") : why;
    }

    /// <summary>"Technology definitions" for common/technology; "Events" for events; "Localisation keys" for text.</summary>
    private static string Label(string domain, bool text)
    {
        if (text) return Loc.T("core.conflict_label_text");
        if (domain == "events") return Loc.T("core.conflict_label_events");
        var folder = domain[(domain.LastIndexOf('/') + 1)..].Replace('_', ' ');
        return Loc.T("core.conflict_label_definitions", ("folder", $"{char.ToUpperInvariant(folder[0])}{folder[1..]}"));
    }

    /// <summary>True when the winning mod's descriptor asks to load after every other mod involved, directly or through other mods.</summary>
    private static bool IsIntended(List<Source> sources, List<int> involved, int winner)
    {
        if (sources[winner].IsGame) return false;
        var losers = involved.Where(s => s != winner && !sources[s].IsGame).ToList();
        if (losers.Count == 0) return false;

        var byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < sources.Count; i++) if (!sources[i].IsGame) byName.TryAdd(sources[i].Name.Trim(), i);
        bool DependsOn(int from, int target, HashSet<int> seen) =>
            seen.Add(from) && sources[from].Dependencies.Any(d => byName.TryGetValue(d.Trim(), out var j) && (j == target || DependsOn(j, target, seen)));
        return losers.All(l => DependsOn(winner, l, []));
    }

    private static ConflictEntry Entry(List<Source> sources, Hit h) =>
        new(sources[h.Source].Key, sources[h.Source].Name, sources[h.Source].IsGame, h.Path, h.Line);
}
