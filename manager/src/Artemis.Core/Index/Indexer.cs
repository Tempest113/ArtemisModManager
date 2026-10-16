using System.Diagnostics;
using System.IO.Compression;
using System.IO.Enumeration;
using System.Security.Cryptography;
using System.Text;
using Artemis.Core.Game;
using Artemis.Core.Mods;

namespace Artemis.Core.Index;

/// <summary><see cref="Busy"/> is false while mods are only being checked against the cache, and turns true once something is really being read.</summary>
public sealed record IndexProgress(int Done, int Total, bool Busy);

public sealed record IndexResult(int ModsIndexed, int ModsUnchanged, int ModsRemoved, bool GameIndexed, TimeSpan Elapsed);

public static class SourceKind
{
    public const string Mod = "mod";
    public const string Vanilla = "vanilla";
}

/// <summary>
/// Keeps the <see cref="IndexStore"/> in step with the disk. A mod is listed again on every update (listing is cheap: sizes and dates
/// only, no file is opened) and read again only when its listing changed. The game is indexed once per build.
/// </summary>
public sealed class Indexer(IndexStore store)
{
    private enum Kind { None, Script, Events, Localisation }

    private readonly record struct Listed(string Path, long Size, long MTime);

    /// <summary>Files larger than this are listed but not read: no script or localisation file comes close.</summary>
    private const long MaxReadBytes = 32 * 1024 * 1024;

    /// <summary>Names the game's current build: its version, and the exe's size and date in case the version text didn't change.</summary>
    public static string GameBuildKey(GameInstall game)
    {
        var exe = new FileInfo(game.ExePath);
        return $"{game.Settings.RawVersion}|{(exe.Exists ? exe.Length : 0)}|{(exe.Exists ? exe.LastWriteTimeUtc.Ticks : 0)}";
    }

    public IndexResult Update(IReadOnlyList<ModInfo> mods, GameInstall? game, IProgress<IndexProgress>? progress = null, CancellationToken ct = default)
    {
        var clock = Stopwatch.StartNew();
        var withFolder = mods.Where(m => (m.ContentDir is not null && Directory.Exists(m.ContentDir)) || (m.ArchivePath is not null && File.Exists(m.ArchivePath))).ToList();
        var gameKey = game is null ? null : GameBuildKey(game);
        var gameDue = gameKey is not null && store.Fingerprint(SourceKind.Vanilla, gameKey) is null;
        var total = withFolder.Count + (gameDue ? 1 : 0);
        int indexed = 0, unchanged = 0;

        foreach (var mod in withFolder)
        {
            ct.ThrowIfCancellationRequested();
            var folder = mod.ContentDir is not null && Directory.Exists(mod.ContentDir);
            List<Listed> listed;
            try { listed = folder ? List(mod.ContentDir!) : ListArchive(mod.ArchivePath!); }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException) { continue; } // a damaged or locked archive: leave the mod out
            var fingerprint = Fingerprint(listed);
            if (store.Fingerprint(SourceKind.Mod, mod.Key) == fingerprint) unchanged++;
            else
            {
                store.Replace(SourceKind.Mod, mod.Key, fingerprint, folder ? Read(mod.ContentDir!, listed, ct) : ReadArchive(mod.ArchivePath!, listed));
                indexed++;
            }
            progress?.Report(new IndexProgress(indexed + unchanged, total, indexed > 0 || gameDue));
        }
        var removed = store.Remove(SourceKind.Mod, withFolder.Select(m => m.Key).ToList());

        if (gameDue)
        {
            ct.ThrowIfCancellationRequested();
            store.Replace(SourceKind.Vanilla, gameKey!, "build", Read(game!.GameDir, List(game.GameDir), ct));
            progress?.Report(new IndexProgress(total, total, true));
        }
        if (gameKey is not null) store.Remove(SourceKind.Vanilla, [gameKey]); // older builds are no use any more
        return new IndexResult(indexed, unchanged, removed, gameDue, clock.Elapsed);
    }

    /// <summary>Every file under the folder with its size and date, by relative path with forward slashes. Reads no file.</summary>
    private static List<Listed> List(string root)
    {
        var rootLength = root.TrimEnd('\\', '/').Length + 1;
        var files = new FileSystemEnumerable<Listed>(root,
            (ref FileSystemEntry e) => new Listed(Relative(ref e, rootLength), e.Length, e.LastWriteTimeUtc.UtcTicks),
            new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 })
        {
            ShouldIncludePredicate = (ref FileSystemEntry e) => !e.IsDirectory,
        };
        var list = files.ToList();
        list.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return list;
    }

    /// <summary>The files of a .zip mod, listed like a folder's (the game reads an archive's root as the mod's root). Reads no file.</summary>
    private static List<Listed> ListArchive(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var list = zip.Entries.Where(e => e.Name.Length > 0).Select(e => new Listed(e.FullName.Replace('\\', '/'), e.Length, e.LastWriteTime.UtcTicks)).ToList();
        list.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return list;
    }

    /// <summary>Reads the text files of a .zip one after the other: an archive can't be read from several threads at once.</summary>
    private static IEnumerable<FileEntry> ReadArchive(string path, List<Listed> listed)
    {
        using var zip = ZipFile.OpenRead(path);
        foreach (var f in listed)
            yield return Parse(f, p =>
            {
                using var stream = (zip.GetEntry(p) ?? throw new IOException("The entry is gone.")).Open();
                using var memory = new MemoryStream();
                stream.CopyTo(memory);
                return memory.ToArray();
            });
    }

    private static string Relative(ref FileSystemEntry e, int rootLength)
    {
        if (e.Directory.Length < rootLength) return e.FileName.ToString();
        return string.Concat(e.Directory[rootLength..].ToString().Replace('\\', '/'), "/", e.FileName);
    }

    /// <summary>Changes whenever a file is added, removed, resized or re-dated, or the index format changes.</summary>
    private static string Fingerprint(List<Listed> listed)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        hash.AppendData(BitConverter.GetBytes(IndexStore.Version));
        foreach (var f in listed) hash.AppendData(Encoding.UTF8.GetBytes($"{f.Path}|{f.Size}|{f.MTime}\n"));
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    /// <summary>Opens the files that hold names and reads those; the rest stay as listed. Runs on all but one core, in no particular order.</summary>
    private static IEnumerable<FileEntry> Read(string root, List<Listed> listed, CancellationToken ct) =>
        listed.AsParallel().WithCancellation(ct).WithDegreeOfParallelism(Math.Max(1, Environment.ProcessorCount - 1)).Select(f => Parse(f, p => File.ReadAllBytes(Path.Combine(root, p))));

    private static FileEntry Parse(Listed f, Func<string, byte[]> read)
    {
        var (kind, domain) = Classify(f.Path);
        if (kind == Kind.None || f.Size > MaxReadBytes) return new FileEntry(f.Path, f.Size, f.MTime);
        try
        {
            var bytes = read(f.Path);
            if (kind != Kind.Localisation) return Entry(f, domain!, ScriptScanner.TopLevel(bytes, kind == Kind.Events));
            var (language, keys) = ScriptScanner.Localisation(bytes);
            return Entry(f, "localisation/" + (language.Length > 0 ? language : "unknown"), keys);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new FileEntry(f.Path, f.Size, f.MTime); // listed, but not readable right now
        }
    }

    private static FileEntry Entry(Listed f, string domain, List<Def> names) =>
        new(f.Path, f.Size, f.MTime, domain, NamePack.Pack(names), names.Count);

    /// <summary>What a file holds, and the folder it counts under: "common/technology" for anything below that folder.</summary>
    private static (Kind, string?) Classify(string path)
    {
        if (path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
        {
            if (path.StartsWith("events/", StringComparison.OrdinalIgnoreCase)) return (Kind.Events, "events");
            if (path.StartsWith("common/", StringComparison.OrdinalIgnoreCase))
            {
                var rest = path["common/".Length..];
                var slash = rest.IndexOf('/');
                return (Kind.Script, slash < 0 ? "common" : "common/" + rest[..slash].ToLowerInvariant());
            }
        }
        else if (path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
                 && (path.StartsWith("localisation/", StringComparison.OrdinalIgnoreCase) || path.StartsWith("localisation_synced/", StringComparison.OrdinalIgnoreCase)))
            return (Kind.Localisation, null);
        return (Kind.None, null);
    }
}
