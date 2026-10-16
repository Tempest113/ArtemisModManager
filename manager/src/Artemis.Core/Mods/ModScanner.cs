using Artemis.Core.Pdx;

namespace Artemis.Core.Mods;

public sealed record ModScanResult(IReadOnlyList<ModInfo> Mods, IReadOnlyList<string> Warnings);

public static class ModScanner
{
    public static ModScanResult Scan(string userDir)
    {
        var mods = new List<ModInfo>();
        var warnings = new List<string>();
        var modDir = Path.Combine(userDir, "mod");
        if (!Directory.Exists(modDir)) return new ModScanResult(mods, warnings);

        foreach (var file in Directory.EnumerateFiles(modDir, "*.mod", SearchOption.TopDirectoryOnly))
        {
            try { mods.Add(ReadOuter(file, userDir)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }
        mods.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return new ModScanResult(mods, warnings);
    }

    public static ModInfo ReadOuter(string outerFile, string userDir)
    {
        var key = Path.GetFileNameWithoutExtension(outerFile);
        var outer = PdxScript.ParseFile(outerFile);
        var contentDir = ResolvePath(outer.GetString("path"), userDir);
        var archive = ResolvePath(outer.GetString("archive"), userDir);

        // The launcher copies the full descriptor into the outer file. Hand-made mods may only have part of it,
        // so the descriptor.mod inside the mod is read only when something the list needs is missing.
        PdxNode? inner = null;
        if (contentDir is not null && (outer.GetString("name") is null || outer.GetString("supported_version") is null))
        {
            var innerFile = Path.Combine(contentDir, "descriptor.mod");
            if (File.Exists(innerFile))
            {
                try { inner = PdxScript.ParseFile(innerFile); }
                catch (IOException) { }
            }
        }

        string? Str(string name) => outer.GetString(name) ?? inner?.GetString(name);
        List<string> Strings(string name)
        {
            var list = outer.GetList(name);
            return list.Count > 0 ? list : inner?.GetList(name) ?? [];
        }

        var name = Str("name");
        return new ModInfo(
            key,
            string.IsNullOrWhiteSpace(name) ? key : name,
            ClassifySource(key, contentDir ?? archive, Path.GetFullPath(Path.Combine(userDir, "mod"))),
            outerFile,
            contentDir,
            archive,
            Str("version"),
            Str("supported_version"),
            Str("remote_file_id"),
            Str("picture"),
            Strings("tags"),
            Strings("dependencies"),
            ContentMissing: !(contentDir is not null && Directory.Exists(contentDir))
                            && !(archive is not null && File.Exists(archive)),
            // The outer .mod file is written when a mod is subscribed to or created, so this reads as "when it was added".
            Modified: File.GetLastWriteTimeUtc(outerFile),
            // The Launcher rewrites every .mod file now and then, so only the mod's own folder says when its content changed.
            ContentModified: contentDir is not null && Directory.Exists(contentDir) ? Directory.GetLastWriteTimeUtc(contentDir) : default,
            Plugin: contentDir is not null && Directory.Exists(contentDir) ? Plugins.PluginManifestReader.Read(contentDir) : null);
    }

    private static string? ResolvePath(string? path, string userDir)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        path = path.Replace('/', Path.DirectorySeparatorChar);
        return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(userDir, path));
    }

    private static ModSource ClassifySource(string key, string? content, string userModDir)
    {
        if (content is not null)
        {
            var full = Path.GetFullPath(content);
            if (full.StartsWith(userModDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return ModSource.Local;
            if (full.Contains(Path.Combine("workshop", "content"), StringComparison.OrdinalIgnoreCase))
                return ModSource.Workshop;
        }
        return key.StartsWith("ugc_", StringComparison.OrdinalIgnoreCase) ? ModSource.Workshop : ModSource.Local;
    }
}
