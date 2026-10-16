using System.Security.Cryptography;
using System.Text.Json;
using Artemis.Core.IO;
using Artemis.Core.Mods;
using Artemis.Core.Playsets;

namespace Artemis.Core.Plugins;

public enum PluginTrust
{
    /// <summary>Never approved. Orion skips the plugin.</summary>
    NotApproved,

    /// <summary>Approved, and its files are the ones that were approved.</summary>
    Approved,

    /// <summary>Approved once, but a file has changed since. Orion skips it until it is approved again (D06).</summary>
    Changed,
}

/// <summary>One mod's approval: which files, as which SHA-256 hashes, and how the approval follows changes.</summary>
public sealed class TrustEntry
{
    public string Name { get; set; } = "";

    /// <summary>The plugin's files, as in its manifest, to the SHA-256 of each as lower-case hex.</summary>
    public Dictionary<string, string> Files { get; set; } = [];

    /// <summary>Opt-in per mod, off by default: a changed file is approved without asking again.</summary>
    public bool FollowUpdates { get; set; }

    /// <summary>For a mod in a local folder (not the Workshop's): its own rebuilds are approved without asking (D06).</summary>
    public bool AutoApprove { get; set; }

    public DateTime ApprovedUtc { get; set; }
}

public sealed class TrustFile
{
    public int Version { get; set; } = 1;
    public Dictionary<string, TrustEntry> Plugins { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public static class PluginHashes
{
    /// <summary>The SHA-256 of each file the manifest lists, as hex, keyed by the manifest's path. A missing file is not in the result.</summary>
    public static Dictionary<string, string> Compute(string contentDir, PluginManifest manifest)
    {
        var root = Path.GetFullPath(contentDir).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        var hashes = new Dictionary<string, string>();
        foreach (var file in manifest.Files)
        {
            var full = Path.GetFullPath(Path.Combine(contentDir, file));
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(full)) continue; // never read outside the mod's folder
            using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            hashes[file] = Convert.ToHexStringLower(SHA256.HashData(stream));
        }
        return hashes;
    }
}

/// <summary>
/// The approvals of plugins, in <c>trust.json</c> next to the playsets. Artemis writes it; Orion reads it and loads only a plugin whose
/// files still have the approved hashes. Trust is per mod, pinned to the exact files (D06).
/// </summary>
public sealed class TrustStore(AppPaths paths)
{
    private TrustFile _file = new();

    public IReadOnlyDictionary<string, TrustEntry> Entries => _file.Plugins;

    public void Load()
    {
        try { _file = JsonSerializer.Deserialize<TrustFile>(File.ReadAllText(paths.TrustFile), Playset.JsonOptions) ?? new(); }
        catch (Exception ex) when (ex is IOException or JsonException) { _file = new(); }
    }

    public void Save() => AtomicFile.WriteAllText(paths.TrustFile, JsonSerializer.Serialize(_file, Playset.JsonOptions));

    public TrustEntry? Get(string modKey) => _file.Plugins.GetValueOrDefault(modKey);

    /// <summary>Whether the files as they are now are the approved ones. A mod that follows updates, or a local one that approved its own rebuilds, counts as approved after a change (its stored hashes are brought along at the next launch).</summary>
    public PluginTrust StateOf(string modKey, PluginManifest manifest, IReadOnlyDictionary<string, string> hashes)
    {
        if (Get(modKey) is not { } entry) return PluginTrust.NotApproved;
        var same = manifest.Files.All(f => hashes.TryGetValue(f, out var h) && entry.Files.TryGetValue(f, out var approved) && approved == h);
        return same || ((entry.FollowUpdates || entry.AutoApprove) && manifest.Files.All(hashes.ContainsKey)) ? PluginTrust.Approved : PluginTrust.Changed;
    }

    public void Approve(string modKey, string name, IReadOnlyDictionary<string, string> hashes, bool autoApprove)
    {
        var old = Get(modKey);
        _file.Plugins[modKey] = new TrustEntry
        {
            Name = name, Files = new Dictionary<string, string>(hashes), AutoApprove = autoApprove,
            FollowUpdates = old?.FollowUpdates ?? false, ApprovedUtc = DateTime.UtcNow,
        };
        Save();
    }

    public void Revoke(string modKey)
    {
        if (_file.Plugins.Remove(modKey)) Save();
    }

    public void SetFollowUpdates(string modKey, bool follow)
    {
        if (Get(modKey) is not { } entry || entry.FollowUpdates == follow) return;
        entry.FollowUpdates = follow;
        Save();
    }

    /// <summary>
    /// Brings the stored hashes of mods that follow updates, or that are local and approved their own rebuilds, up to the files as they are.
    /// Called before a launch, so Orion sees hashes that match. Returns the keys that changed.
    /// </summary>
    public IReadOnlyList<string> RefreshFollowed(IEnumerable<(string Key, PluginManifest Manifest, IReadOnlyDictionary<string, string> Hashes)> mods)
    {
        var changed = new List<string>();
        foreach (var (key, manifest, hashes) in mods)
        {
            if (Get(key) is not { } entry || !(entry.FollowUpdates || entry.AutoApprove)) continue;
            if (!manifest.Files.All(hashes.ContainsKey)) continue; // a file is missing: nothing to approve
            var same = entry.Files.Count == hashes.Count && entry.Files.All(p => hashes.TryGetValue(p.Key, out var h) && h == p.Value);
            if (same) continue;
            entry.Files = new Dictionary<string, string>(hashes);
            changed.Add(key);
        }
        if (changed.Count > 0) Save();
        return changed;
    }
}
