using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Artemis.Core.Game;
using Artemis.Core.IO;
using Artemis.Core.Launch;
using Artemis.Core.Playsets;

namespace Artemis.Core.Plugins;

public enum OrionState
{
    /// <summary>Not installed, and this copy of Artemis has no Orion files to install.</summary>
    NoPayload,
    NotInstalled,
    Installed,
    /// <summary>Part of it is missing: Steam's file check removes the stub, a clean-up can remove the core.</summary>
    NeedsRepair,
    /// <summary>Installed, but this copy of Artemis carries a different Orion.</summary>
    Outdated,
    /// <summary>The game folder has a winmm.dll that is not ours; it is never replaced.</summary>
    ForeignStub,
}

/// <param name="InstalledVersion">The core the pointer file names, or null.</param>
/// <param name="AvailableVersion">The Orion this copy of Artemis carries, or null if it carries none.</param>
public sealed record OrionStatus(OrionState State, string? InstalledVersion, string? AvailableVersion);

/// <summary>
/// Puts Orion in place (D14, D50): the stub as <c>winmm.dll</c> in the game folder and the core in a versioned folder under the app data,
/// named by a pointer file the stub reads. The stub is only ever written over nothing or over our own; anything else is left alone.
/// </summary>
public sealed partial class OrionInstaller(AppPaths paths, string payloadDir, Func<bool>? isRunning = null)
{
    public const string StubName = "winmm.dll";
    public const string CoreName = "orion.dll";

    private string PayloadStub => Path.Combine(payloadDir, StubName);
    private string PayloadCore => Path.Combine(payloadDir, CoreName);
    private bool HasPayload => File.Exists(PayloadStub) && File.Exists(PayloadCore);

    /// <summary>The folder next to Artemis.exe that carries the files to install.</summary>
    public static string DefaultPayloadDir => Path.Combine(AppContext.BaseDirectory, "orion");

    [GeneratedRegex("^[A-Za-z0-9._-]{1,40}$")]
    private static partial Regex VersionName();

    /// <summary>The payload's file version as a folder name: letters, digits, '.', '_' and '-', which is all the stub accepts.</summary>
    private string PayloadVersion
    {
        get
        {
            var v = new string((FileVersionInfo.GetVersionInfo(PayloadCore).FileVersion ?? "").Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-').ToArray());
            return v.Length is > 0 and <= 40 && v != "." && v != ".." ? v : "0";
        }
    }

    private string CorePath(string version) => Path.Combine(paths.OrionDir, version, CoreName);

    private string? InstalledVersion()
    {
        try
        {
            var version = File.ReadAllText(paths.OrionPointerFile).Trim();
            return VersionName().IsMatch(version) && version is not ("." or "..") && File.Exists(CorePath(version)) ? version : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public OrionStatus Inspect(string gameDir)
    {
        var available = HasPayload ? PayloadVersion : null;
        var installed = InstalledVersion();
        if (StubCheck.Find(gameDir) is not null) return new(OrionState.ForeignStub, installed, available);
        var stub = StubCheck.IsInstalled(gameDir);
        if (!stub && installed is null) return new(available is null ? OrionState.NoPayload : OrionState.NotInstalled, null, available);
        if (!stub || installed is null) return new(OrionState.NeedsRepair, installed, available);
        if (available is not null && (installed != available || !SameFile(PayloadStub, Path.Combine(gameDir, StubName)) || !SameFile(PayloadCore, CorePath(installed))))
            return new(OrionState.Outdated, installed, available);
        return new(OrionState.Installed, installed, available);
    }

    /// <summary>Installs, repairs or updates, whichever is due. Never replaces a winmm.dll that is not ours.</summary>
    public void Install(string gameDir)
    {
        if ((isRunning ?? LaunchService.GameIsRunning)()) throw new ArtemisException("Stellaris is running. Close it before installing or removing Orion.");
        if (!HasPayload) throw new ArtemisException("This copy of Artemis doesn't include Orion's files.");
        if (StubCheck.Find(gameDir) is { } foreign)
            throw new ArtemisException($"The Stellaris folder already has a winmm.dll that Artemis didn't put there. Artemis won't replace it. Move it out of the folder first: {foreign.Path}");
        try
        {
            var version = PayloadVersion;
            Directory.CreateDirectory(Path.Combine(paths.OrionDir, version));
            CopyOver(PayloadCore, CorePath(version));
            AtomicFile.WriteAllText(paths.OrionPointerFile, version + "\n"); // no BOM: the stub reads this as plain bytes
            try { CopyOver(PayloadStub, Path.Combine(gameDir, StubName)); }
            catch (UnauthorizedAccessException ex)
            {
                throw new ArtemisException("Windows won't let Artemis write to the Stellaris folder. If the game is installed under Program Files, run Artemis once as administrator to install Orion, or move the Steam library.", ex);
            }
            PruneOldCores(version);
        }
        catch (IOException ex) { throw new ArtemisException("Couldn't install Orion: " + ex.Message, ex); }
        catch (UnauthorizedAccessException ex) { throw new ArtemisException("Couldn't install Orion: " + ex.Message, ex); }
    }

    /// <summary>Takes out the stub (only if it is ours), the pointer, the core and the plugin copies. Build data the user allowed is kept.</summary>
    public void Remove(string gameDir)
    {
        if ((isRunning ?? LaunchService.GameIsRunning)()) throw new ArtemisException("Stellaris is running. Close it before installing or removing Orion.");
        try
        {
            if (StubCheck.IsInstalled(gameDir)) File.Delete(Path.Combine(gameDir, StubName));
            if (File.Exists(paths.OrionPointerFile)) File.Delete(paths.OrionPointerFile);
            PruneOldCores(keep: null);
            var cache = Path.Combine(paths.OrionDir, "cache");
            if (Directory.Exists(cache)) Directory.Delete(cache, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new ArtemisException("Couldn't remove Orion: " + ex.Message, ex); }
    }

    private void PruneOldCores(string? keep)
    {
        if (!Directory.Exists(paths.OrionDir)) return;
        foreach (var dir in Directory.EnumerateDirectories(paths.OrionDir))
        {
            var name = Path.GetFileName(dir);
            if (name == keep || name is "builds" or "cache" || !VersionName().IsMatch(name)) continue;
            try { Directory.Delete(dir, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Copies through a temp file next to the target, and leaves a target that is already identical untouched.</summary>
    private static void CopyOver(string source, string target)
    {
        if (SameFile(source, target)) return;
        var temp = target + ".artemis-tmp";
        File.Copy(source, temp, overwrite: true);
        File.Move(temp, target, overwrite: true);
    }

    private static bool SameFile(string a, string b)
    {
        try
        {
            var x = new FileInfo(a);
            var y = new FileInfo(b);
            return x.Exists && y.Exists && x.Length == y.Length && File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}

public enum BuildSupport { Unknown, AllowedByUser, Verified }

/// <summary>
/// The per-build data file Orion needs before it loads any plugin (D15, D50): <c>orion\builds\&lt;build id&gt;.json</c>. A file made by this class
/// is the user's own say-so for this exact build and holds no addresses; a file from the address tools would hold some and count as verified.
/// </summary>
public sealed partial class OrionBuilds(AppPaths paths)
{
    [GeneratedRegex("^[0-9a-f]{8}-[0-9a-f]{1,8}$")]
    private static partial Regex IdPattern();

    private string FileFor(string id) => IdPattern().IsMatch(id) ? Path.Combine(paths.OrionBuildsDir, id + ".json") : throw new ArtemisException("That isn't a game build id.");

    public BuildSupport Check(string id)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(FileFor(id)));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Text(root, "build") != id || Text(root, "game").Length == 0) return BuildSupport.Unknown;
            return Text(root, "source") == "user" ? BuildSupport.AllowedByUser : BuildSupport.Verified;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return BuildSupport.Unknown; }
    }

    /// <summary>Writes the user's permission for this exact build. A file that already counts as verified is left as it is.</summary>
    public void Allow(string id, string gameVersion)
    {
        if (string.IsNullOrWhiteSpace(gameVersion)) throw new ArtemisException("Artemis doesn't know which version of Stellaris this is, so it can't allow plugins on it.");
        if (Check(id) == BuildSupport.Verified) return;
        AtomicFile.WriteAllText(FileFor(id), JsonSerializer.Serialize(new { build = id, game = gameVersion, source = "user", addresses = new { } }));
    }

    /// <summary>Takes back the user's permission. Verified data is never deleted from here.</summary>
    public void StopAllowing(string id)
    {
        if (Check(id) == BuildSupport.AllowedByUser) File.Delete(FileFor(id));
    }

    private static string Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
