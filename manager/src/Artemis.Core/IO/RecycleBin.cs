using Microsoft.VisualBasic.FileIO;

namespace Artemis.Core.IO;

public static class RecycleBin
{
    /// <summary>Windows keeps the file in the Recycle Bin until the user empties it, so it can be restored from there. Throws <see cref="IOException"/> or <see cref="OperationCanceledException"/> if it can't be done (the file is open elsewhere).</summary>
    public static void Send(string file) => FileSystem.DeleteFile(file, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
}
