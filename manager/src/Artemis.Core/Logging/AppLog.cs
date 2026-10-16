namespace Artemis.Core.Logging;

/// <summary>
/// Artemis's own log: one text file with a line per notable event (start-up, scans, launches) and per problem, so "it did
/// something odd" can be answered afterwards. Writing never throws and never blocks the caller for long; if the file can't be
/// written the line is dropped. One process-wide log, started once at start-up.
/// </summary>
public static class AppLog
{
    /// <summary>When the file grows past this at start-up, it becomes the ".old" file (replacing the previous one) and a new one begins.</summary>
    public const long MaxBytes = 1024 * 1024;

    private static readonly object Gate = new();
    private static string? _path;

    public static string? Path => _path;

    public static void Start(string path)
    {
        lock (Gate)
        {
            _path = path;
            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                if (File.Exists(path) && new FileInfo(path).Length > MaxBytes) File.Move(path, path + ".old", overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        Info("Artemis started");
    }

    public static void Info(string message) => Write("info", message);

    public static void Warn(string message) => Write("warn", message);

    public static void Error(string message, Exception? exception = null) =>
        Write("error", exception is null ? message : $"{message}\n    {exception.ToString().ReplaceLineEndings("\n    ")}");

    private static void Write(string level, string message)
    {
        lock (Gate)
        {
            if (_path is null) return;
            try { File.AppendAllText(_path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level,-5} {message}\n"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
