using System.Text;

namespace Artemis.Core.Game;

/// <summary>What a <see cref="LogTail"/> found: the whole text to show in place of what is shown (<c>Replace</c>), or only the lines added since. Text is whole lines, each ending in a line break.</summary>
public sealed record LogChange(string Text, bool Replace);

/// <summary>
/// Follows one growing log file for the Logs page's live view. The first <see cref="Poll"/> returns the end of the file, later ones only what was
/// appended, so the view keeps its scroll position and selection. A file that was started over (a new game run) is returned whole again. A last line
/// that is still being written is held back until its line break arrives. Files are opened shared: the game may have its log open.
/// </summary>
/// <param name="joinEntries">The game's logs: a message that contains line breaks is joined into one line (<see cref="GameLogs.OneLinePerEntry"/>).</param>
public sealed class LogTail(string path, bool joinEntries)
{
    /// <summary>The most of a file shown at first, so a multi-megabyte log doesn't freeze the window.</summary>
    private const int MaxBytes = 256 * 1024, HeadBytes = 64;

    private long _shown = -1;   // bytes of the file already handed out; -1 before the first poll
    private byte[] _head = [];  // the file's first bytes then: a new run of the game starts with a different time stamp

    /// <summary>Null when nothing new has arrived. Throws <see cref="IOException"/> if the file can't be read.</summary>
    public LogChange? Poll()
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var length = stream.Length;
        var head = Read(stream, 0, (int)Math.Min(HeadBytes, length));
        var restart = _shown < 0 || length < _shown || !head.AsSpan().StartsWith(_head);
        if (!restart && length == _shown) return null;

        var from = restart ? Math.Max(0, length - MaxBytes) : _shown;
        var bytes = Read(stream, from, (int)(length - from));
        var end = Array.LastIndexOf(bytes, (byte)'\n') + 1;
        // A tail starts at a line start, and says it was cut.
        var start = restart && from > 0 ? Array.IndexOf(bytes, (byte)'\n') + 1 : 0;
        var text = end > start ? Encoding.UTF8.GetString(bytes, start, end - start) : "";
        if (restart)
        {
            _head = head;
            _shown = from + end;
            if (start > 0) text = "…\n" + text;
        }
        else if (end == 0) return null;
        else _shown += end;

        // shortcut: a line that continues an entry begun in an earlier poll is shown on its own line; the game writes an entry in one go, so it takes bad timing.
        if (joinEntries) text = GameLogs.OneLinePerEntry(text);
        if (text.Length > 0 && text[^1] != '\n') text += '\n';
        return new LogChange(text, restart);
    }

    private static byte[] Read(FileStream stream, long from, int count)
    {
        var buffer = new byte[count];
        stream.Seek(from, SeekOrigin.Begin);
        stream.ReadExactly(buffer);
        return buffer;
    }
}
