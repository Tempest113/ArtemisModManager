using System.IO.Compression;
using System.Text;
using Artemis.Core.IO;
using Artemis.Core.Pdx;
using Artemis.Core.Plugins;
using Artemis.Core.Text;

namespace Artemis.Core.Mods;

/// <summary>What an archive holds, before anything is written: enough to ask the user a sensible question.</summary>
public sealed record ArchiveInspection(string Name, string Folder, int Files, IReadOnlyList<string> Dlls, PluginInfo? Plugin, bool AlreadyInstalled);

public sealed record InstallResult(string Key, string Name, string Folder, int Files, PluginInfo? Plugin);

/// <summary>Creates and installs mods in the game's user mod folder (<c>Documents\...\Stellaris\mod</c>): a folder and the <c>.mod</c> file that points at it.</summary>
public static class ModInstaller
{
    /// <summary>Bounds against an archive that is not what it says: more files or more bytes than any mod has.</summary>
    public const int MaxFiles = 200_000;
    public const long MaxBytes = 16L * 1024 * 1024 * 1024;

    /// <summary>Looks inside a .zip without extracting. Throws <see cref="ArtemisException"/> with a plain message if it is not a Stellaris mod.</summary>
    public static ArchiveInspection Inspect(string archivePath, string userDir)
    {
        using var zip = Open(archivePath);
        var (prefix, descriptor) = FindDescriptor(zip);
        var entries = Content(zip, prefix).ToList();
        var name = DescriptorName(descriptor) ?? Path.GetFileNameWithoutExtension(archivePath);
        var folder = SafeName(name);
        var dlls = entries.Where(e => e.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)).Select(e => e.Name).ToList();
        PluginInfo? plugin = null;
        if (entries.FirstOrDefault(e => e.Name.Equals(PluginManifestReader.FileName, StringComparison.OrdinalIgnoreCase)) is { } manifest)
        {
            using var reader = new StreamReader(manifest.Open());
            plugin = PluginManifestReader.Parse(reader.ReadToEnd());
        }
        return new ArchiveInspection(name, folder, entries.Count, dlls, plugin, Exists(userDir, folder));
    }

    /// <summary>Extracts the mod into its own folder under the mod folder and writes its .mod file. With <paramref name="replace"/>, an installed mod of the same name is removed first.</summary>
    public static InstallResult Install(string archivePath, string userDir, bool replace = false)
    {
        using var zip = Open(archivePath);
        var (prefix, descriptor) = FindDescriptor(zip);
        var name = DescriptorName(descriptor) ?? Path.GetFileNameWithoutExtension(archivePath);
        var folder = SafeName(name);
        var modDir = Path.GetFullPath(Path.Combine(userDir, "mod"));
        var target = Path.GetFullPath(Path.Combine(modDir, folder));
        var outer = Path.Combine(modDir, folder + ".mod");
        if (!target.StartsWith(modDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new ArtemisException(Loc.T("core.mod_name_unusable"));
        if (Exists(userDir, folder) && !replace) throw new ArtemisException(Loc.T("core.mod_exists", ("folder", folder)));

        // The new copy is unpacked beside the mod folder first (same volume, so the last step is a rename) and the installed one only steps
        // aside once that worked: a failed install leaves the old mod exactly as it was, and never half a new one.
        var entries = Content(zip, prefix).ToList();
        var holding = Path.Combine(Path.GetFullPath(userDir), StagingFolder, Guid.NewGuid().ToString("N")[..8]);
        var staging = Path.Combine(holding, "new");
        var oldDir = Path.Combine(holding, "old");
        var oldOuter = Path.Combine(holding, "old.mod");
        bool movedDir = false, movedOuter = false, placed = false;
        try
        {
            Directory.CreateDirectory(staging);
            long total = 0;
            foreach (var entry in entries)
            {
                total += entry.Length;
                if (total > MaxBytes) throw new ArtemisException(Loc.T("core.archive_too_big"));
                var dest = Path.GetFullPath(Path.Combine(staging, entry.Name.Replace('/', Path.DirectorySeparatorChar)));
                if (!dest.StartsWith(staging + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new ArtemisException(Loc.T("core.archive_escapes"));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                entry.ExtractToFile(dest, overwrite: true);
            }

            Directory.CreateDirectory(modDir);
            if (Directory.Exists(target)) { Directory.Move(target, oldDir); movedDir = true; }
            if (File.Exists(outer)) { File.Move(outer, oldOuter); movedOuter = true; }
            Directory.Move(staging, target);
            placed = true;
            File.WriteAllText(outer, OuterFile(descriptor, folder), new UTF8Encoding(false));
        }
        catch
        {
            try
            {
                if (placed) { Directory.Delete(target, recursive: true); if (File.Exists(outer)) File.Delete(outer); }
                if (movedDir) Directory.Move(oldDir, target);
                if (movedOuter) File.Move(oldOuter, outer);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { holding = ""; } // the old mod is still in the holding folder; leave it there for the user
            throw;
        }
        finally
        {
            if (holding.Length > 0) TryDelete(holding);
            TryDeleteIfEmpty(Path.Combine(Path.GetFullPath(userDir), StagingFolder));
        }
        return new InstallResult(folder, name, target, entries.Count, PluginManifestReader.Read(target));
    }

    /// <summary>Where a mod is unpacked before it replaces anything. It sits in the user folder, not in <c>mod</c>, so the game and the mod scan never see it.</summary>
    private const string StagingFolder = "artemis-staging";

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void TryDeleteIfEmpty(string dir)
    {
        try { if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>A new, empty script mod: its folder, a descriptor, and the .mod file. With <paramref name="plugin"/>, also a starting <c>modloader.json</c>.</summary>
    public static InstallResult Create(string userDir, string name, string gameVersion, bool plugin)
    {
        name = name.Trim();
        var folder = SafeName(name);
        if (name.Length == 0 || folder.Length == 0) throw new ArtemisException(Loc.T("core.mod_needs_name"));
        if (Exists(userDir, folder)) throw new ArtemisException(Loc.T("core.mod_already_exists", ("folder", folder)));
        var modDir = Path.Combine(userDir, "mod");
        var target = Path.Combine(modDir, folder);
        var supported = gameVersion.Split('.') is [var major, ..] && major.TrimStart('v').Length > 0 ? $"v{major.TrimStart('v')}.*" : "v4.*";

        var descriptor = $"name=\"{name.Replace("\"", "'")}\"\nversion=\"0.1.0\"\nsupported_version=\"{supported}\"\ntags={{\n\t\"Gameplay\"\n}}\n";
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "descriptor.mod"), descriptor, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(modDir, folder + ".mod"), OuterFile(descriptor, folder), new UTF8Encoding(false));
        if (plugin)
        {
            var id = "mod." + new string(folder.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
            Directory.CreateDirectory(Path.Combine(target, "orion"));
            File.WriteAllText(Path.Combine(target, PluginManifestReader.FileName), $$"""
                {
                  "schema": 1,
                  "id": "{{id}}",
                  "name": "{{name.Replace("\"", "'")}}",
                  "version": "0.1.0",
                  "apiVersion": 1,
                  "files": [ "orion/{{id}}.dll" ],
                  "builds": [ "{{supported}}" ],
                  "stability": "fragile",
                  "features": [],
                  "settings": []
                }
                """, new UTF8Encoding(false));
        }
        return new InstallResult(folder, name, target, plugin ? 2 : 1, PluginManifestReader.Read(target));
    }

    /// <summary>Letters, digits, spaces and a few marks; anything else, and trailing dots and spaces, are dropped so the name is safe as a folder and file name on Windows.</summary>
    public static string SafeName(string name)
    {
        var clean = new string(name.Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or '.' or '(' or ')' or '[' or ']' or '\'' or '+' or '&').ToArray()).Trim().TrimEnd('.');
        return clean.Length > 80 ? clean[..80].TrimEnd() : clean;
    }

    private static bool Exists(string userDir, string folder) =>
        Directory.Exists(Path.Combine(userDir, "mod", folder)) || File.Exists(Path.Combine(userDir, "mod", folder + ".mod"));

    private static ZipArchive Open(string path)
    {
        try { return ZipFile.OpenRead(path); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            throw new ArtemisException(ex is InvalidDataException ? Loc.T("core.archive_not_zip") : Loc.T("core.archive_open_failed", ("reason", ex.Message)));
        }
    }

    /// <summary>The folder inside the archive that holds descriptor.mod (the archive's root, or the one folder it is wrapped in) and the descriptor's text.</summary>
    private static (string Prefix, string Text) FindDescriptor(ZipArchive zip)
    {
        var best = zip.Entries.Where(e => e.Name.Equals("descriptor.mod", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.FullName.Count(c => c == '/')).FirstOrDefault()
            ?? throw new ArtemisException(Loc.T("core.archive_no_descriptor"));
        if (best.FullName.Count(c => c == '/') > 1) throw new ArtemisException(Loc.T("core.archive_descriptor_deep"));
        using var reader = new StreamReader(best.Open(), Encoding.UTF8);
        return (best.FullName[..^best.Name.Length], reader.ReadToEnd());
    }

    /// <summary>The files under the prefix, as entries whose <c>Name</c> is changed to the path relative to it. Folders and anything that tries to leave the folder are left out.</summary>
    private static IEnumerable<Entry> Content(ZipArchive zip, string prefix)
    {
        var count = 0;
        foreach (var e in zip.Entries)
        {
            if (e.Name.Length == 0 || !e.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var relative = e.FullName[prefix.Length..].Replace('\\', '/');
            if (relative.Split('/').Any(p => p is "" or "." or "..") || Path.IsPathRooted(relative) || relative.Contains(':')) continue;
            if (++count > MaxFiles) throw new ArtemisException(Loc.T("core.archive_too_many_files"));
            yield return new Entry(e, relative);
        }
    }

    private sealed record Entry(ZipArchiveEntry Source, string Name)
    {
        public long Length => Source.Length;
        public Stream Open() => Source.Open();
        public void ExtractToFile(string destination, bool overwrite) => Source.ExtractToFile(destination, overwrite);
    }

    private static string? DescriptorName(string descriptor) => PdxScript.Parse(descriptor).GetString("name") is { Length: > 0 } n && SafeName(n).Length > 0 ? n : null;

    /// <summary>The .mod file the game reads: the descriptor, and the line that says where the mod's files are.</summary>
    public static string OuterFile(string descriptor, string folder) => WithPath(descriptor, $"mod/{folder}");

    /// <summary>The descriptor with its own <c>path</c> and <c>archive</c> lines replaced by a single <c>path</c> line.</summary>
    public static string WithPath(string descriptor, string path)
    {
        var lines = descriptor.Replace("\r\n", "\n").Split('\n').Where(l => !l.TrimStart().StartsWith("path", StringComparison.OrdinalIgnoreCase) && !l.TrimStart().StartsWith("archive", StringComparison.OrdinalIgnoreCase));
        return string.Join('\n', lines).TrimEnd() + $"\npath=\"{path}\"\n";
    }
}
