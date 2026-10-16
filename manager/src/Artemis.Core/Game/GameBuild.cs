namespace Artemis.Core.Game;

public static class GameBuild
{
    /// <summary>
    /// The game exe's build id as Orion computes it (D15, D50): the PE timestamp and the image size, such as "66f1a2b3-5a4000". Read from the file's
    /// headers, nothing else. Null when the file isn't a readable Windows program.
    /// </summary>
    public static string? IdOf(string exePath)
    {
        try
        {
            using var stream = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var head = new byte[4096];
            var n = stream.ReadAtLeast(head, 0x40, throwOnEndOfStream: false);
            if (n < 0x40 || head[0] != (byte)'M' || head[1] != (byte)'Z') return null;
            var pe = BitConverter.ToInt32(head, 0x3C);
            if (pe < 0 || pe + 24 + 60 > n || head[pe] != (byte)'P' || head[pe + 1] != (byte)'E' || head[pe + 2] != 0 || head[pe + 3] != 0) return null;
            return $"{BitConverter.ToUInt32(head, pe + 8):x8}-{BitConverter.ToUInt32(head, pe + 24 + 56):x}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}
