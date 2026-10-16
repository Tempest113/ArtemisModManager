using System.Text;

namespace Artemis.Core.IO;

/// <summary>User-facing failure: the message is written to be shown as is.</summary>
public sealed class ArtemisException(string message, Exception? inner = null) : Exception(message, inner);

public static class AtomicFile
{
    /// <summary>Writes UTF-8 without BOM through a temp file, so a crash never leaves a half-written file.</summary>
    public static void WriteAllText(string path, string text)
    {
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, "." + Path.GetFileName(path) + ".tmp");
        File.WriteAllText(tmp, text, new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }
}
