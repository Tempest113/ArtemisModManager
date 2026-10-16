using System.Diagnostics;

namespace Artemis.Core.Game;

/// <summary>A <c>winmm.dll</c> in the game folder that Artemis did not put there.</summary>
public sealed record ForeignStub(string Path, long Size, DateTime ModifiedUtc, string? Company)
{
    /// <summary>Changes when the file does, so a notice the user dismissed comes back only for a different file.</summary>
    public string NoticeId => $"foreign-winmm|{Size}|{ModifiedUtc.Ticks}";
}

/// <summary>
/// The game loads <c>winmm.dll</c> from its own folder before the system one, which is how Orion will get in (D14), and how an old experiment's
/// loader once stopped the Paradox Launcher from working. Orion's stub says "Artemis" as the company in its version resource; any other
/// <c>winmm.dll</c> there is somebody else's (or an old one), and is only ever reported, never touched.
/// </summary>
public static class StubCheck
{
    /// <summary>True when the game folder has Orion's own stub (company "Artemis" in its version resource).</summary>
    public static bool IsInstalled(string gameDir)
    {
        var file = Path.Combine(gameDir, "winmm.dll");
        try { return File.Exists(file) && FileVersionInfo.GetVersionInfo(file).CompanyName?.StartsWith("Artemis", StringComparison.OrdinalIgnoreCase) == true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    public static ForeignStub? Find(string gameDir)
    {
        var file = Path.Combine(gameDir, "winmm.dll");
        try
        {
            if (!File.Exists(file)) return null;
            var company = FileVersionInfo.GetVersionInfo(file).CompanyName;
            if (company?.StartsWith("Artemis", StringComparison.OrdinalIgnoreCase) == true) return null;
            var info = new FileInfo(file);
            return new ForeignStub(file, info.Length, info.LastWriteTimeUtc, company);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}
