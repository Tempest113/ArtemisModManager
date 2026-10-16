using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;
using Artemis.Core.Game;
using Artemis.Core.IO;
using Artemis.Core.Logging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Artemis.App.ViewModels;

public sealed record LogFile(string Name, string Path);

public enum AppPage { Mods, Conflicts, Plugins, Logs }

/// <summary>The Logs page: the game's own log files, newest content at the bottom.</summary>
public sealed partial class MainViewModel
{
    public ObservableCollection<LogFile> LogFiles { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowMods), nameof(ShowConflicts), nameof(ShowPlugins), nameof(ShowLogs))]
    private AppPage _page;

    [ObservableProperty] private LogFile? _selectedLog;
    [ObservableProperty] private string _logsEmptyText = "";

    public bool ShowMods { get => Page == AppPage.Mods; set { if (value) Page = AppPage.Mods; } }
    public bool ShowConflicts { get => Page == AppPage.Conflicts; set { if (value) Page = AppPage.Conflicts; } }
    public bool ShowPlugins { get => Page == AppPage.Plugins; set { if (value) Page = AppPage.Plugins; } }
    public bool ShowLogs { get => Page == AppPage.Logs; set { if (value) Page = AppPage.Logs; } }

    // The mouse's side buttons step through the pages last viewed, like the back and forward buttons of a browser.
    private readonly Stack<AppPage> _pagesBack = new(), _pagesForward = new();
    private bool _steppingPages;

    partial void OnPageChanging(AppPage oldValue, AppPage newValue)
    {
        if (_steppingPages || oldValue == newValue) return;
        _pagesBack.Push(oldValue);
        _pagesForward.Clear();
    }

    public void GoBack() => StepPage(_pagesBack, _pagesForward);

    public void GoForward() => StepPage(_pagesForward, _pagesBack);

    private void StepPage(Stack<AppPage> from, Stack<AppPage> to)
    {
        if (from.Count == 0) return;
        to.Push(Page);
        _steppingPages = true;
        Page = from.Pop();
        _steppingPages = false;
    }

    partial void OnPageChanged(AppPage value)
    {
        if (value == AppPage.Logs)
        {
            RefreshLogs();
            (_logTimer ??= NewLogTimer()).Start();
        }
        else _logTimer?.Stop();
        if (value == AppPage.Conflicts) RequestConflicts();
        if (value == AppPage.Plugins) RefreshStatus();
    }

    partial void OnSelectedLogChanged(LogFile? value) => StartTail();

    // The log on show is followed while the page is open: once a second the list of logs and the end of the chosen one are looked at (a stat call when
    // nothing changed), so lines the game writes appear as they come. shortcut: polling, not a file watcher, which is late on files that are only appended to.
    private DispatcherTimer? _logTimer;
    private LogTail? _tail;
    private string? _logError;

    /// <summary>The window shows what arrives: a <see cref="LogChange"/> either replaces the text or adds lines to its end.</summary>
    public event Action<LogChange>? LogChanged;

    private DispatcherTimer NewLogTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            ListLogs();
            PollLog();
        };
        return timer;
    }

    /// <summary>Reads the chosen log from its end again (a new <see cref="LogTail"/>), and lists the logs again.</summary>
    [RelayCommand]
    private void RefreshLogs()
    {
        ListLogs();
        StartTail();
    }

    private void StartTail()
    {
        _tail = SelectedLog is { } log ? new LogTail(log.Path, joinEntries: log.Path != AppLog.Path) : null;
        if (_tail is null) LogChanged?.Invoke(new LogChange("", Replace: true));
        else PollLog();
    }

    private void PollLog()
    {
        try
        {
            if (_tail?.Poll() is { } change) LogChanged?.Invoke(change);
            _logError = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var message = $"Couldn't read this log: {ex.Message}";
            if (message != _logError) LogChanged?.Invoke(new LogChange(message, Replace: true));
            _logError = message;
            if (SelectedLog is { } log) _tail = new LogTail(log.Path, joinEntries: log.Path != AppLog.Path); // read it whole once it can be read again
        }
    }

    /// <summary>
    /// Lists the logs again. The list is edited in place, so the chosen log keeps its place when another one appears or goes; if it is gone, the
    /// error log (or the first one) is chosen.
    /// </summary>
    private void ListLogs()
    {
        var files = new List<LogFile>();
        if (AppLog.Path is { } own && File.Exists(own)) files.Add(new LogFile("Artemis", own));
        if (_game is not null)
        {
            files.AddRange(GameLogs.List(_game.UserDir).Select(f => new LogFile(Path.GetFileName(f), f)));
            // Orion's own log and the log of each plugin (D17), when Orion has written any
            var orion = Core.Plugins.OrionFiles.FolderIn(_game.UserDir);
            if (Directory.Exists(orion))
                files.AddRange(Directory.EnumerateFiles(orion, "*.log", SearchOption.AllDirectories).Where(f => new FileInfo(f).Length > 0).Order()
                    .Select(f => new LogFile("Orion: " + Path.GetRelativePath(orion, f).Replace('\\', '/'), f)));
        }
        for (var i = 0; i < files.Count; i++)
        {
            if (i < LogFiles.Count && LogFiles[i] == files[i]) continue;
            var at = LogFiles.IndexOf(files[i]);
            if (at > i) LogFiles.Move(at, i);
            else LogFiles.Insert(i, files[i]);
        }
        while (LogFiles.Count > files.Count) LogFiles.RemoveAt(LogFiles.Count - 1);
        LogsEmptyText = files.Count == 0 ? "No logs yet." : "";
        if (SelectedLog is null || !files.Contains(SelectedLog)) SelectedLog = files.FirstOrDefault(f => f.Name == "error.log") ?? files.FirstOrDefault();
    }

    [RelayCommand]
    private void OpenLogFolder()
    {
        if (_game is not null) Open(Path.Combine(_game.UserDir, "logs"));
    }

    private bool CanCleanUpLogs() => !GameRunning;

    /// <summary>Sends the logs nothing needs any more to the Recycle Bin, after showing which and why (see <see cref="LogCleanup"/>).</summary>
    [RelayCommand(CanExecute = nameof(CanCleanUpLogs))]
    private async Task CleanUpLogs()
    {
        if (_game is null) return;
        var userDir = _game.UserDir;
        var plugins = _installed.Values.Where(m => m.Plugin is not null).Select(m => m.Key).ToList();
        var stale = LogCleanup.Find(userDir, plugins);
        if (stale.Count == 0) { Notify("Nothing to clean up: every log is from the game's latest run, or belongs to an installed plugin."); return; }
        var list = string.Join("\n", stale.Take(20).Select(s => $"   {Path.GetRelativePath(userDir, s.File)}   ({s.Bytes / 1024:N0} KB): {s.Reason}")) + (stale.Count > 20 ? $"\n   … and {stale.Count - 20} more" : "");
        if (!_dialogs.Confirm($"These logs are no longer needed:\n\n{list}\n\nSend them to the Recycle Bin? The game's latest logs, Orion's own log, Artemis's log and the logs of installed plugins stay where they are.", "Recycle")) return;
        var removed = await Task.Run(() => LogCleanup.Recycle(userDir, plugins, stale, RecycleBin.Send));
        AppLog.Info($"Sent {removed} of {stale.Count} old logs to the Recycle Bin: {string.Join(", ", stale.Take(10).Select(s => Path.GetFileName(s.File)))}");
        ListLogs();
        Notify(removed == stale.Count ? $"Sent {removed} old log{(removed == 1 ? "" : "s")} to the Recycle Bin." : $"Sent {removed} of {stale.Count} old logs to the Recycle Bin; the rest changed or are in use, so they stayed.");
    }
}
