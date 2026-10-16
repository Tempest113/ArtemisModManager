using System.IO.Compression;
using System.Text;
using Artemis.Core.IO;
using Artemis.Core.Pdx;
using Artemis.Core.Plugins;

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
        if (!target.StartsWith(modDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new ArtemisException("That mod's name can't be used as a folder name.");
        if (Exists(userDir, folder))
        {
            if (!replace) throw new ArtemisException($"A mod called \"{folder}\" is already installed.");
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            if (File.Exists(outer)) File.Delete(outer);
        }

        var entries = Content(zip, prefix).ToList();
        long total = 0;
        try
        {
            Directory.CreateDirectory(target);
            foreach (var entry in entries)
            {
                total += entry.Length;
                if (total > MaxBytes) throw new ArtemisException("This archive is far larger than any mod; it was not installed.");
                var dest = Path.GetFullPath(Path.Combine(target, entry.Name.Replace('/', Path.DirectorySeparatorChar)));
                if (!dest.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new ArtemisException("This archive tries to write outside the mod's folder; it was not installed.");
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                entry.ExtractToFile(dest, overwrite: true);
            }
            File.WriteAllText(outer, OuterFile(descriptor, folder), new UTF8Encoding(false));
        }
        catch
        {
            // Don't leave half a mod behind.
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            if (File.Exists(outer)) File.Delete(outer);
            throw;
        }
        return new InstallResult(folder, name, target, entries.Count, PluginManifestReader.Read(target));
    }

    /// <summary>A new, empty script mod: its folder, a descriptor, and the .mod file. With <paramref name="plugin"/>, also a starting <c>modloader.json</c>.</summary>
    public static InstallResult Create(string userDir, string name, string gameVersion, bool plugin)
    {
        name = name.Trim();
        var folder = SafeName(name);
        if (name.Length == 0 || folder.Length == 0) throw new ArtemisException("A mod needs a name.");
        if (Exists(userDir, folder)) throw new ArtemisException($"A mod called \"{folder}\" already exists.");
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
            throw new ArtemisException(ex is InvalidDataException ? "That isn't a .zip archive (7z and rar files aren't supported yet)." : $"The archive couldn't be opened: {ex.Message}");
        }
    }

    /// <summary>The folder inside the archive that holds descriptor.mod (the archive's root, or the one folder it is wrapped in) and the descriptor's text.</summary>
    private static (string Prefix, string Text) FindDescriptor(ZipArchive zip)
    {
        var best = zip.Entries.Where(e => e.Name.Equals("descriptor.mod", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.FullName.Count(c => c == '/')).FirstOrDefault()
            ?? throw new ArtemisException("This archive has no descriptor.mod, so it doesn't look like a Stellaris mod.");
        if (best.FullName.Count(c => c == '/') > 1) throw new ArtemisException("This archive's descriptor.mod is buried too deep to be a Stellaris mod.");
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
            if (++count > MaxFiles) throw new ArtemisException("This archive holds far more files than any mod; it was not installed.");
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
    public static string OuterFile(string descriptor, string folder)
    {
        var lines = descriptor.Replace("\r\n", "\n").Split('\n').Where(l => !l.TrimStart().StartsWith("path", StringComparison.OrdinalIgnoreCase) && !l.TrimStart().StartsWith("archive", StringComparison.OrdinalIgnoreCase));
        return string.Join('\n', lines).TrimEnd() + $"\npath=\"mod/{folder}\"\n";
    }
}
