using System.Text;
using Artemis.Core.Pdx;

namespace Artemis.Core.Mods;

/// <summary>A .mod file made for a mod folder that had none: its name (the file stem) and the folder it points at.</summary>
public sealed record NewDescriptor(string Key, string Name, string Folder);

/// <summary>
/// Keeping the mod folder and its .mod files in step, as Irony does (D55). The game only sees a mod through a .mod file in <c>mod/</c>, so a mod folder
/// without one is invisible, and a .mod file whose folder or archive is gone is a leftover.
/// </summary>
public static class ModFolderHealth
{
    private const int MaxDepth = 6;

    private static string Norm(string path) => Path.GetFullPath(path).TrimEnd('\\', '/');

    /// <summary>
    /// Folders anywhere under <c>mod/</c> (not only directly in it: <c>outer/inner/descriptor.mod</c> counts) that hold a descriptor.mod but that no .mod file
    /// points at. A folder with a descriptor.mod is a mod's root, so the search does not go on inside it. Returns the folder and its path relative to <c>mod/</c>.
    /// </summary>
    public static IReadOnlyList<(string Folder, string Relative)> FindUnregistered(string userDir, IEnumerable<ModInfo> known)
    {
        var modDir = Path.Combine(userDir, "mod");
        var found = new List<(string, string)>();
        if (!Directory.Exists(modDir)) return found;
        var taken = known.Where(m => m.ContentDir is not null).Select(m => Norm(m.ContentDir!)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        void Walk(string dir, int depth)
        {
            List<string> subs;
            try { subs = Directory.EnumerateDirectories(dir).Order(StringComparer.OrdinalIgnoreCase).ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
            foreach (var sub in subs)
            {
                if (File.Exists(Path.Combine(sub, "descriptor.mod")))
                {
                    if (!taken.Contains(Norm(sub))) found.Add((sub, Path.GetRelativePath(modDir, sub).Replace('\\', '/')));
                }
                else if (depth < MaxDepth) Walk(sub, depth + 1);
            }
        }
        Walk(modDir, 1);
        return found;
    }

    /// <summary>
    /// Writes a .mod file in <c>mod/</c> for each folder: the folder's descriptor.mod with a <c>path</c> line pointing at the folder, named after the mod. Never
    /// replaces a file that is there (a free name is found instead).
    /// </summary>
    public static IReadOnlyList<NewDescriptor> CreateDescriptors(string userDir, IEnumerable<(string Folder, string Relative)> folders)
    {
        var modDir = Path.Combine(userDir, "mod");
        var made = new List<NewDescriptor>();
        foreach (var (folder, relative) in folders)
        {
            string text;
            try { text = File.ReadAllText(Path.Combine(folder, "descriptor.mod")); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            string? name = null;
            try { name = PdxScript.Parse(text).GetString("name"); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { }
            var stem = ModInstaller.SafeName(string.IsNullOrWhiteSpace(name) ? Path.GetFileName(folder) : name);
            if (stem.Length == 0) stem = "mod";
            var key = stem;
            for (var i = 2; File.Exists(Path.Combine(modDir, key + ".mod")); i++) key = $"{stem} {i}";
            try { File.WriteAllText(Path.Combine(modDir, key + ".mod"), ModInstaller.OuterFile(text, relative), new UTF8Encoding(false)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            made.Add(new NewDescriptor(key, string.IsNullOrWhiteSpace(name) ? key : name, folder));
        }
        return made;
    }

    /// <summary>
    /// Sends the .mod files of mods whose folder or archive is gone to <paramref name="remove"/> (the Recycle Bin, <see cref="IO.RecycleBin.Send"/>), so nothing is lost for
    /// good. Each file is looked at again first, and one whose files have come back, or that is not directly in <c>mod/</c>, is left alone. Returns how many went.
    /// </summary>
    public static int RemoveDescriptors(string userDir, IEnumerable<ModInfo> mods, Action<string> remove)
    {
        var modDir = Norm(Path.Combine(userDir, "mod"));
        var moved = 0;
        foreach (var mod in mods)
        {
            var file = Path.GetFullPath(mod.OuterFile);
            if (!file.EndsWith(".mod", StringComparison.OrdinalIgnoreCase) || !File.Exists(file)
                || !string.Equals(Norm(Path.GetDirectoryName(file)!), modDir, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                if (!ModScanner.ReadOuter(file, userDir).ContentMissing) continue;
                remove(file);
                moved++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException) { }
        }
        return moved;
    }
}
