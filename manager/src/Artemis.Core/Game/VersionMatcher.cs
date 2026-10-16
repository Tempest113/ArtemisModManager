namespace Artemis.Core.Game;

public static class VersionMatcher
{
    /// <summary>
    /// Does a mod's supported_version (for example "v4.5.*" or "v4.*") cover the game version ("v4.5.2")?
    /// Returns null when the mod gives no usable version, so the UI can stay quiet instead of guessing.
    /// </summary>
    public static bool? IsCompatible(string? supported, string gameVersion)
    {
        var want = Parts(supported);
        var have = Parts(gameVersion);
        if (want.Length == 0 || have.Length == 0) return null;

        for (var i = 0; i < want.Length; i++)
        {
            if (want[i] == "*") continue;
            if (i >= have.Length || !string.Equals(want[i], have[i], StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    private static string[] Parts(string? version)
    {
        var v = (version ?? "").Trim().Trim('"').TrimStart('v', 'V');
        return v.Length == 0 ? [] : v.Split('.');
    }
}
