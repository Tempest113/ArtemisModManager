using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using Artemis.Core.Game;
using Artemis.Core.IO;
using Artemis.App.Services;
using Artemis.Core.Index;
using Artemis.Core.Launch;
using Artemis.Core.Logging;
using Artemis.Core.Mods;
using Artemis.Core.Playsets;
using Artemis.Core.Plugins;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Artemis.App.ViewModels;

/// <summary>Everything the main window needs that isn't a window: dialogs go through this so the view model stays testable.</summary>
public interface IDialogs
{
    string? PickFolder(string title);
    void Error(string message);
    string? Prompt(string title, string initial);
    bool Confirm(string message, string yes = "Yes");
    (string Name, bool Option)? PromptWithOption(string title, string initial, string optionText);
    string? PickOpenFile(string title, string filter);
    string? PickSaveFile(string title, string fileName, string filter);
    IReadOnlyList<ExternalPlayset>? PickPlaysets(string title, string body, string ok, IReadOnlyList<ExternalPlayset> found, Func<ExternalPlayset, bool> preselect);
}

public enum GroupMode { All, Source }

public enum InstalledFilter { All, NotInPlayset, InPlayset, Outdated, Missing, NewOrUpdated, WithConflicts, WithPlugin }

public enum InstalledSort { Name, Newest }

public sealed partial class MainViewModel : ObservableObject
{
    private readonly AppPaths _paths;
    private readonly PlaysetStore _store;
    private readonly IDialogs _dialogs;
    private readonly AppSettings _settings;
    private readonly PlaysetHistory _history = new();
    private readonly IndexStore _indexStore;
    private readonly Indexer _indexer;
    private Dictionary<(string Kind, string Key), SourceStats> _indexStats = [];
    private bool _indexing, _indexAgain;
    private readonly Dictionary<string, InstalledRowViewModel> _installedRows = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, ModInfo> _installed = new(StringComparer.OrdinalIgnoreCase);
    private List<LoadOrderRowViewModel> _matches = [];
    private int _matchIndex = -1;
    private GameInstall? _game;
    private LaunchService? _launcher;

    private readonly string? _userDirOverride, _orionPayloadDir;

    public MainViewModel(AppPaths paths, IDialogs dialogs, string? userDirOverride = null, string? orionPayloadDir = null)
    {
        _userDirOverride = userDirOverride;
        _orionPayloadDir = orionPayloadDir;
        _paths = paths;
        _store = new PlaysetStore(paths);
        _indexStore = new IndexStore(paths.IndexFile);
        _indexer = new Indexer(_indexStore);
        _dialogs = dialogs;
        _settings = AppSettings.Load(paths);

        InstalledView = CollectionViewSource.GetDefaultView(Installed);
        InstalledView.Filter = o => MatchesInstalled((InstalledRowViewModel)o);
        ApplyGrouping();
        InitConflicts();
    }

    public AppSettings Settings => _settings;

    public void SaveSettings() => Try(() => _settings.Save(_paths));

    public ObservableCollection<Playset> Playsets { get; } = [];
    public BulkCollection<InstalledRowViewModel> Installed { get; } = [];
    public BulkCollection<LoadOrderRowViewModel> LoadOrder { get; } = [];
    public ICollectionView InstalledView { get; }
    public string GameVersion => _game?.Settings.RawVersion ?? "";
    public bool HasGameVersion => GameVersion.Length > 0;
    private bool _hasScanned;

    /// <summary>What to say in the Installed list when it shows no rows; empty when it shows some.</summary>
    public string InstalledEmptyText =>
        IsScanning && Installed.Count == 0 ? "Scanning mods…"
        : !_hasScanned ? ""
        : Installed.Count == 0 ? "No mods found. Subscribe to mods on the Steam Workshop, or put them in the mod folder (the folder button, top right)."
        : InstalledView.IsEmpty ? (InstalledSearch.Trim().Length > 0 ? $"No installed mods match \"{InstalledSearch.Trim()}\"." : "No installed mods match this filter.")
        : "";

    public string SelectedPlaysetName => SelectedPlayset?.Name ?? "";

    /// <summary>Set by the view whenever the selection in the Installed list changes.</summary>
    public IReadOnlyList<InstalledRowViewModel> SelectedInstalled { get; private set; } = [];
    public event Action? ClearInstalledSelectionRequested;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedPlaysetName))]
    private Playset? _selectedPlayset;
    [ObservableProperty] private LoadOrderRowViewModel? _selectedRow;
    [ObservableProperty] private string _installedSearch = "";
    [ObservableProperty] private string _findText = "";
    [ObservableProperty] private string _findStatus = "";
    [ObservableProperty] private string _message = "";

    /// <summary>What the background index is doing, while it is really reading something; empty otherwise.</summary>
    [ObservableProperty] private string _indexStatus = "";
    [ObservableProperty] private bool _messageHasUndo;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstalledEmptyText), nameof(InstalledCountText))]
    [NotifyCanExecuteChangedFor(nameof(RescanCommand))]
    private bool _isScanning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LaunchText))]
    [NotifyCanExecuteChangedFor(nameof(LaunchCommand), nameof(ResumeCommand), nameof(InstallOrionCommand), nameof(RemoveOrionCommand), nameof(CleanUpLogsCommand))]
    private bool _gameRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResumeTip))]
    [NotifyCanExecuteChangedFor(nameof(ResumeCommand))]
    private LastSave? _lastSave;

    public string ResumeTip => LastSave is { } s ? $"Start the game and continue {s.Name}, {s.Date}" : "There is no save to continue yet";

    [ObservableProperty] private bool _hasDrift;
    [ObservableProperty] private string _driftText = "";

    public string InstalledCountText => IsScanning ? "scanning…" : Installed.Count.ToString();
    public string LaunchText => GameRunning ? "Running" : "Launch";
    [ObservableProperty] private bool _libraryCollapsed;
    [ObservableProperty] private bool _hasMultiSelection;
    [ObservableProperty] private int _selectedInstalledCount;
    [ObservableProperty] private GroupMode _groupMode = GroupMode.All;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilterActive))]
    private InstalledFilter _filter;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilterActive))]
    private InstalledSort _sort;

    /// <summary>True when the Installed list is narrowed or ordered differently from the default.</summary>
    public bool FilterActive => Filter != InstalledFilter.All || Sort != InstalledSort.Name;

    partial void OnFilterChanged(InstalledFilter value)
    {
        InstalledView.Refresh();
        OnPropertyChanged(nameof(InstalledEmptyText));
    }

    partial void OnSortChanged(InstalledSort value) => ApplyGrouping();

    /// <summary>Rows come and go from the "in playset" filters as the playset changes. Deferred, because this runs from inside a click.</summary>
    private void RefreshMembershipFilter()
    {
        if (Filter is not (InstalledFilter.NotInPlayset or InstalledFilter.InPlayset)) return;
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            InstalledView.Refresh();
            OnPropertyChanged(nameof(InstalledEmptyText));
        });
    }

    public bool IsGroupAll { get => GroupMode == GroupMode.All; set { if (value) GroupMode = GroupMode.All; } }
    public bool IsGroupSource { get => GroupMode == GroupMode.Source; set { if (value) GroupMode = GroupMode.Source; } }

    partial void OnGroupModeChanged(GroupMode value)
    {
        OnPropertyChanged(nameof(IsGroupAll));
        OnPropertyChanged(nameof(IsGroupSource));
        ApplyGrouping();
    }

    partial void OnInstalledSearchChanged(string value)
    {
        InstalledView.Refresh();
        OnPropertyChanged(nameof(InstalledEmptyText));
    }
    partial void OnFindTextChanged(string value)
    {
        RefreshFind();
        if (_matches.Count > 0) GoToMatch(0);
    }

    partial void OnSelectedPlaysetChanged(Playset? value)
    {
        ShowPlayset();
        if (value is not null) _history.Reset(value);
        _settings.ActivePlaysetId = value?.Id;
        Try(() => _settings.Save(_paths));
    }

    // ---- start-up and scanning ----

    public async Task InitializeAsync()
    {
        var gameDir = _settings.GameDir is { } saved && File.Exists(Path.Combine(saved, "stellaris.exe"))
            ? saved
            : GameLocator.FindGameDir();
        if (gameDir is null)
        {
            gameDir = _dialogs.PickFolder("Select your Stellaris folder (the one containing stellaris.exe)");
            if (gameDir is null || !File.Exists(Path.Combine(gameDir, "stellaris.exe")))
            {
                Notify("Stellaris wasn't found. Restart Artemis to choose its folder.");
                return;
            }
            _settings.GameDir = gameDir;
            Try(() => _settings.Save(_paths));
        }

        try { _game = GameInstall.Open(gameDir, _userDirOverride); }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            ShowError($"Couldn't read the game's launcher settings: {ex.Message}", ex);
            return;
        }
        _launcher = new LaunchService(_game, _paths);
        _orion = new OrionInstaller(_paths, _orionPayloadDir ?? OrionInstaller.DefaultPayloadDir);
        _builds = new OrionBuilds(_paths);
        LastSave = LaunchService.FindLastSave(_game.UserDir);
        AppLog.Info($"Stellaris {GameVersion} at {gameDir}; mod folder {_game.ModDir}");
        LoadChecksumRules();
        OnPropertyChanged(nameof(GameVersion));
        OnPropertyChanged(nameof(HasGameVersion));

        Timing.Mark("start-up begins");
        var scan = await ScanAsync();
        Timing.Mark("mod list filled");
        AppLog.Info($"Found {scan.Mods.Count} mods" + (scan.Warnings.Count > 0 ? $", {scan.Warnings.Count} unreadable" : ""));
        var list = await Task.Run(_store.List);
        foreach (var p in list.Playsets) Playsets.Add(p);
        if (Playsets.Count == 0) await FirstRunAsync(scan.Mods.Count);

        SelectedPlayset = Playsets.FirstOrDefault(p => p.Id == _settings.ActivePlaysetId) ?? Playsets[0];
        var running = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        running.Tick += (_, _) => GameRunning = LaunchService.GameIsRunning();
        running.Start();
        GameRunning = LaunchService.GameIsRunning();
        CheckDrift();
        Timing.Mark("playset shown");
        _ = RefreshPluginsAsync();
        // The window is up and the lists are filled; the index can take its time after that.
        _ = Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () => await UpdateIndexAsync()));
        if (scan.Warnings.Count > 0) ShowError("Some mods couldn't be read:\n" + string.Join("\n", scan.Warnings.Take(5)));
    }

    /// <summary>
    /// Brings the index cache up to date in the background: only mods whose files changed are read again, and the game only when
    /// its build changed. A failure here never stops the manager: the index is an extra, not something the lists depend on.
    /// </summary>
    private async Task UpdateIndexAsync()
    {
        if (_game is null) return;
        if (_indexing)
        {
            _indexAgain = true;
            return;
        }
        _indexing = true;
        try
        {
            do
            {
                _indexAgain = false;
                var mods = _installed.Values.ToList();
                var game = _game;
                var progress = new Progress<IndexProgress>(p => IndexStatus = p.Busy ? $"Indexing mods… {p.Done} of {p.Total}" : "");
                Timing.Mark("index update begins");
                var result = await Task.Run(() => _indexer.Update(mods, game, progress));
                Timing.Mark($"index update done: {result.ModsIndexed} mods read, {result.ModsUnchanged} unchanged, game {(result.GameIndexed ? "read" : "cached")}");
                AppLog.Info($"Index updated in {result.Elapsed.TotalSeconds:0.0} s: {result.ModsIndexed} mods read, {result.ModsUnchanged} unchanged, {result.ModsRemoved} removed, game {(result.GameIndexed ? "read" : "cached")}");
                _indexStats = await Task.Run(_indexStore.AllStats);
                await CheckAchievementsAsync(mods);
                IndexStatus = "";
                RefreshDetails();
                RequestConflicts();
            }
            while (_indexAgain);
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException)
        {
            AppLog.Error("The mod index couldn't be updated", ex);
            IndexStatus = "Couldn't update the mod index.";
        }
        finally
        {
            _indexing = false;
        }
    }

    private async Task<ModScanResult> ScanAsync()
    {
        IsScanning = true;
        try { return await ScanCoreAsync(); }
        finally { IsScanning = false; }
    }

    private async Task<ModScanResult> ScanCoreAsync()
    {
        var scan = await Task.Run(() => ModScanner.Scan(_game!.UserDir));
        // The game only sees a mod through a .mod file, so a mod folder that has none (at any depth) gets one, as Irony does (D55).
        var adopted = await Task.Run(() => ModFolderHealth.CreateDescriptors(_game!.UserDir, ModFolderHealth.FindUnregistered(_game.UserDir, scan.Mods)));
        if (adopted.Count > 0)
        {
            scan = await Task.Run(() => ModScanner.Scan(_game!.UserDir));
            AppLog.Info($"Made .mod files for {adopted.Count} mod folders that had none: {string.Join(", ", adopted.Select(a => a.Key))}");
            Notify(adopted.Count == 1 ? $"Found a mod folder with no .mod file and made one: {adopted[0].Name}." : $"Found {adopted.Count} mod folders with no .mod file and made them: {string.Join(", ", adopted.Take(3).Select(a => a.Name))}{(adopted.Count > 3 ? "…" : "")}.");
        }
        _installed = scan.Mods.ToDictionary(m => m.Key, StringComparer.OrdinalIgnoreCase);
        if (_settings.SeenMods is null)
        {
            // First start: everything installed now counts as already seen, so only later arrivals get a marker.
            _settings.SeenMods = SeenMods.Snapshot(scan.Mods);
            SaveSettings();
        }
        _installedByName = scan.Mods.GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        _installedRows.Clear();
        var rows = new List<InstalledRowViewModel>(scan.Mods.Count);
        foreach (var mod in scan.Mods)
        {
            var row = new InstalledRowViewModel(this, mod, GameVersion);
            _installedRows[mod.Key] = row;
            rows.Add(row);
        }
        Installed.ReplaceAll(rows);
        _hasScanned = true;
        OnPropertyChanged(nameof(InstalledEmptyText));
        return scan;
    }

    [RelayCommand(CanExecute = nameof(CanRescan))]
    private async Task Rescan()
    {
        if (_game is null) return;
        var scan = await ScanAsync();
        ShowPlayset();
        Notify($"Found {scan.Mods.Count} mods.");
        _ = UpdateIndexAsync();
        _ = RefreshPluginsAsync();
    }

    private const string CurrentListSource = "Game";

    /// <summary>Everything worth offering as a starting point: the game's own list, the Launcher's playsets, Irony's collections.</summary>
    private async Task<IReadOnlyList<ExternalPlayset>> FindImportableAsync()
    {
        var game = _game!;
        return await Task.Run(() =>
        {
            var found = new List<ExternalPlayset>();
            try
            {
                if (File.Exists(game.DlcLoadPath) && DlcLoad.ToPlaysetMods(DlcLoad.Read(game.DlcLoadPath).EnabledMods) is { Count: > 0 } current)
                    found.Add(new ExternalPlayset(CurrentListSource, "Current mod list", current));
            }
            catch (Exception ex) when (ex is IOException or JsonException) { }
            try { found.AddRange(PlaysetImporters.FromLauncher(PlaysetImporters.LauncherFile(game.UserDir))); }
            catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException) { }
            found.AddRange(PlaysetImporters.FromIrony(PlaysetImporters.IronyFolder()));
            // Irony also writes its collections into the Launcher's database, so the same playset can turn up twice.
            return (IReadOnlyList<ExternalPlayset>)found.Where(p => p.Mods.Count > 0).DistinctBy(p => p.Name + "|" + string.Join(",", p.Mods.Select(m => m.Key))).ToList();
        });
    }

    /// <summary>First start: offer to begin from the game's list or another tool's playsets; otherwise begin empty.</summary>
    private async Task FirstRunAsync(int installedCount)
    {
        var found = await FindImportableAsync();
        // Scripts and tests set this so no dialog waits for a click; they get the game's own list, as before.
        var quiet = Environment.GetEnvironmentVariable("ARTEMIS_NO_WELCOME") == "1";
        var picked = found.Count == 0 ? null
            : quiet ? found.Where(p => p.Source == CurrentListSource).ToList()
            : _dialogs.PickPlaysets("Welcome to Artemis",
                $"Stellaris {GameVersion} was found with {installedCount} installed mods. Choose the playsets to start with; you can import more later from the playset menu. Nothing in your game changes until you press Launch.",
                "Import", found, p => p.Source == CurrentListSource);
        if (picked is { Count: > 0 }) AddImported(picked);
        if (Playsets.Count == 0) AddAndSelect(new Playset { Name = "My playset" });
    }

    [RelayCommand]
    private async Task ImportPlaysets()
    {
        if (_game is null) return;
        var found = await FindImportableAsync();
        if (found.Count == 0)
        {
            Notify("No playsets found to import.");
            return;
        }
        if (_dialogs.PickPlaysets("Import playsets", "Tick the playsets to copy into Artemis. The other tools' files are only read, never changed.", "Import", found, _ => false) is { Count: > 0 } picked)
            AddImported(picked);
    }

    private void AddImported(IEnumerable<ExternalPlayset> picked)
    {
        Playset? first = null;
        foreach (var external in picked)
        {
            var name = external.Name;
            if (Playsets.Any(p => p.Name == name)) name = $"{external.Name} ({external.Source})";
            for (var n = 2; Playsets.Any(p => p.Name == name); n++) name = $"{external.Name} ({external.Source} {n})";
            var playset = new Playset { Name = name, Mods = external.Mods.Select(m => new PlaysetMod { Key = m.Key, Enabled = m.Enabled }).ToList() };
            Try(() => _store.Save(playset));
            Playsets.Add(playset);
            first ??= playset;
        }
        if (first is not null) SelectedPlayset = first;
    }

    private bool CanRescan() => !IsScanning;

    private void ShowPlayset()
    {
        var rows = new List<LoadOrderRowViewModel>();
        if (SelectedPlayset is { } p)
            foreach (var entry in p.Mods)
            {
                rows.Add(new LoadOrderRowViewModel(this, entry, _installed.GetValueOrDefault(entry.Key), GameVersion));
            }
        LoadOrder.ReplaceAll(rows);
        Renumber();
        foreach (var row in Installed) row.Refresh();
        RefreshMembershipFilter();
        RequestConflicts();
    }

    /// <summary>Positions count mods only; separators sit between them without a number.</summary>
    private void Renumber()
    {
        var position = 0;
        foreach (var row in LoadOrder) row.Index = row.IsSeparator ? 0 : ++position;
        RefreshFind();
        CheckDependencies();
    }

    // ---- notes and "new or updated" markers ----

    public string? NoteFor(string key) => _settings.Notes.GetValueOrDefault(key);

    public ModChange ChangeFor(ModInfo mod) => SeenMods.Check(mod, _settings.SeenMods);

    private void RefreshMarks(string key)
    {
        if (_installedRows.TryGetValue(key, out var installedRow)) installedRow.RefreshMarks();
        foreach (var row in LoadOrder.Where(r => string.Equals(r.Key, key, StringComparison.OrdinalIgnoreCase))) row.RefreshMarks();
    }

    /// <summary>A launch (or a saved load order) counts as having looked at every mod: the markers clear.</summary>
    private void MarkSeen()
    {
        _settings.SeenMods = SeenMods.Snapshot(_installed.Values);
        SaveSettings();
        foreach (var row in _installedRows.Values) row.RefreshMarks();
        foreach (var row in LoadOrder) row.RefreshMarks();
        if (Filter == InstalledFilter.NewOrUpdated) OnFilterChanged(Filter);
    }

    // ---- the Installed list: grouping, search, selection ----

    private void ApplyGrouping()
    {
        using (InstalledView.DeferRefresh())
        {
            InstalledView.GroupDescriptions.Clear();
            InstalledView.SortDescriptions.Clear();
            switch (GroupMode)
            {
                case GroupMode.Source:
                    InstalledView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ModRowBase.SourceName)));
                    InstalledView.SortDescriptions.Add(new SortDescription(nameof(ModRowBase.SourceName), ListSortDirection.Ascending));
                    break;
            }
            if (Sort == InstalledSort.Newest) InstalledView.SortDescriptions.Add(new SortDescription(nameof(ModRowBase.Modified), ListSortDirection.Descending));
            InstalledView.SortDescriptions.Add(new SortDescription(nameof(ModRowBase.Name), ListSortDirection.Ascending));
        }
    }

    private bool MatchesInstalled(InstalledRowViewModel row)
    {
        var s = InstalledSearch.Trim();
        var matchesSearch = s.Length == 0
            || row.Name.Contains(s, StringComparison.OrdinalIgnoreCase)
            || row.Key.Contains(s, StringComparison.OrdinalIgnoreCase);
        return matchesSearch && Filter switch
        {
            InstalledFilter.NotInPlayset => !row.InPlayset,
            InstalledFilter.InPlayset => row.InPlayset,
            InstalledFilter.Outdated => row.IsOutdated,
            InstalledFilter.Missing => row.HasProblem,
            InstalledFilter.NewOrUpdated => row.Change != ModChange.None,
            InstalledFilter.WithConflicts => row.HasConflicts,
            InstalledFilter.WithPlugin => row.HasPlugin,
            _ => true,
        };
    }

    public void SetInstalledSelection(IEnumerable<InstalledRowViewModel> rows)
    {
        SelectedInstalled = rows.ToList();
        SelectedInstalledCount = SelectedInstalled.Count;
        HasMultiSelection = SelectedInstalled.Count > 1;
    }

    [RelayCommand] private void ClearInstalledSelection() => ClearInstalledSelectionRequested?.Invoke();

    [RelayCommand] private void ToggleLibrary() => LibraryCollapsed = !LibraryCollapsed;

    // ---- the playset ----

    public bool IsInPlayset(string key) => SelectedPlayset?.Contains(key) == true;

    public void SetInPlayset(ModInfo mod, bool on)
    {
        if (on) AddMod(mod);
        else RemoveMod(mod.Key);
        Renumber();
        PlaysetChanged();
        RefreshMembershipFilter();
        if (on) AskAboutNewPlugins([mod]);
    }

    private void AddMod(ModInfo mod)
    {
        if (SelectedPlayset is not { } p || !p.Add(mod.Key)) return;
        LoadOrder.Add(new LoadOrderRowViewModel(this, p.Mods[^1], mod, GameVersion));
    }

    private void RemoveMod(string key)
    {
        if (SelectedPlayset is not { } p || !p.Remove(key)) return;
        if (LoadOrder.FirstOrDefault(r => string.Equals(r.Key, key, StringComparison.OrdinalIgnoreCase)) is { } row) LoadOrder.Remove(row);
        if (_installedRows.TryGetValue(key, out var installedRow)) installedRow.Refresh();
    }

    [RelayCommand]
    private void AddSelectedToPlayset()
    {
        foreach (var row in SelectedInstalled) AddMod(row.Mod!);
        Renumber();
        PlaysetChanged();
        foreach (var row in SelectedInstalled) row.Refresh();
        RefreshMembershipFilter();
        AskAboutNewPlugins(SelectedInstalled.Select(r => r.Mod));
    }

    /// <summary>Takes mods and separators out of the playset (a separator's mods stay).</summary>
    public void RemoveRows(IEnumerable<LoadOrderRowViewModel> rows)
    {
        if (SelectedPlayset is not { } p) return;
        foreach (var row in rows.ToList())
        {
            var index = LoadOrder.IndexOf(row);
            if (index < 0) continue;
            p.Mods.RemoveAt(index);
            LoadOrder.RemoveAt(index);
            if (_installedRows.TryGetValue(row.Key, out var installedRow)) installedRow.Refresh();
        }
        Renumber();
        PlaysetChanged();
        RefreshMembershipFilter();
        var mods = rows.Count(r => !r.IsSeparator);
        if (mods > 0) Notify(mods == 1 ? "Removed 1 mod." : $"Removed {mods} mods.", undo: true);
    }

    /// <summary>Moves a row to where mod number <paramref name="position"/> is now (clamped). Separators aren't counted.</summary>
    public void MoveTo(LoadOrderRowViewModel row, int position)
    {
        var mods = LoadOrder.Where(r => !r.IsSeparator).ToList();
        if (mods.Count > 0) MoveRows([row], LoadOrder.IndexOf(mods[Math.Clamp(position, 1, mods.Count) - 1]));
    }

    /// <summary>Moves rows together, keeping their order, so the first lands at <paramref name="insertAt"/> of the list left without them.</summary>
    public void MoveRows(IReadOnlyList<LoadOrderRowViewModel> rows, int insertAt)
    {
        if (SelectedPlayset is not { } p || rows.Count == 0) return;
        var order = Playset.MoveBlock(LoadOrder, rows.Select(LoadOrder.IndexOf), insertAt);
        if (order.SequenceEqual(LoadOrder)) return;
        for (var i = 0; i < order.Count; i++)
            if (!ReferenceEquals(LoadOrder[i], order[i])) LoadOrder.Move(LoadOrder.IndexOf(order[i]), i);
        p.Mods.Clear();
        p.Mods.AddRange(order.Select(r => r.Entry));
        Renumber();
        PlaysetChanged();
        if (rows.Count == 1) SelectedRow = rows[0];
    }

    /// <summary>One step up or down for the whole group.</summary>
    public void MoveRowsBy(IReadOnlyList<LoadOrderRowViewModel> rows, int delta) =>
        MoveRows(rows, Math.Max(0, rows.Min(LoadOrder.IndexOf) + delta));

    /// <summary>Switches the selected mods all on, or all off if they already are.</summary>
    public void ToggleEnabled(IEnumerable<LoadOrderRowViewModel> rows)
    {
        var mods = rows.Where(r => !r.IsSeparator).ToList();
        if (mods.Count == 0) return;
        var on = !mods.All(r => r.Enabled);
        foreach (var row in mods) row.SetEnabled(on);
        PlaysetChanged();
        if (on) AskAboutNewPlugins(mods.Select(r => r.Mod));
    }

    /// <summary>A mod was switched on from its own switch.</summary>
    public void PluginMaybeOn(ModInfo? mod) => AskAboutNewPlugins([mod]);

    /// <summary>Drop: dragging up puts the rows just above the target, dragging down just below it.</summary>
    public void MoveRowsTo(IReadOnlyList<LoadOrderRowViewModel> rows, LoadOrderRowViewModel target)
    {
        if (rows.Contains(target)) return;
        var up = rows.Min(LoadOrder.IndexOf) > LoadOrder.IndexOf(target);
        MoveRows(rows, LoadOrder.Where(r => !rows.Contains(r)).ToList().IndexOf(target) + (up ? 0 : 1));
    }

    public void PlaysetChanged()
    {
        if (SelectedPlayset is not { } p) return;
        _history.Record(p);
        Try(() => _store.Save(p));
        CheckDependencies();
        RequestConflicts();
        RefreshPluginStates();
    }

    [RelayCommand]
    private void Undo() => StepHistory(_history.Undo);

    [RelayCommand]
    private void Redo() => StepHistory(_history.Redo);

    private void StepHistory(Func<Playset, bool> step)
    {
        if (SelectedPlayset is not { } p || !step(p)) return;
        ShowPlayset();
        Try(() => _store.Save(p));
    }

    // ---- separators: named dividers in the load order, purely a visual aid ----

    public void AddSeparator(string name, int index)
    {
        if (SelectedPlayset is not { } p) return;
        index = Math.Clamp(index, 0, LoadOrder.Count);
        var entry = new PlaysetMod { Separator = name };
        p.Mods.Insert(index, entry);
        var row = new LoadOrderRowViewModel(this, entry, null, GameVersion);
        LoadOrder.Insert(index, row);
        Renumber();
        PlaysetChanged();
        SelectedRow = row;
    }

    public void AddCustomSeparator(int index)
    {
        if (_dialogs.Prompt("New separator", "") is { } name) AddSeparator(name, index);
    }

    public void RenameSeparator(LoadOrderRowViewModel row)
    {
        if (_dialogs.Prompt("Rename separator", row.Name) is not { } name) return;
        row.RenameSeparator(name);
        PlaysetChanged();
    }

    // ---- find in the load order: matches are marked, rows are never hidden ----

    private void RefreshFind()
    {
        foreach (var r in LoadOrder) r.IsMatch = false;
        var s = FindText.Trim();
        _matches = s.Length == 0 ? [] : LoadOrder.Where(r => r.Name.Contains(s, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var m in _matches) m.IsMatch = true;
        _matchIndex = SelectedRow is null ? -1 : _matches.IndexOf(SelectedRow);
        FindStatus = s.Length == 0 ? "" : _matches.Count == 0 ? "No match" : _matchIndex >= 0 ? $"{_matchIndex + 1} of {_matches.Count}" : $"{_matches.Count} found";
    }

    private void GoToMatch(int index)
    {
        _matchIndex = index;
        SelectedRow = _matches[index];
        FindStatus = $"{index + 1} of {_matches.Count}";
    }

    [RelayCommand] private void FindNext() { if (_matches.Count > 0) GoToMatch((_matchIndex + 1) % _matches.Count); }

    [RelayCommand] private void FindPrevious() { if (_matches.Count > 0) GoToMatch((_matchIndex - 1 + _matches.Count) % _matches.Count); }

    // ---- playsets ----

    [RelayCommand]
    private void NewPlayset()
    {
        if (_dialogs.Prompt("New playset", "New playset") is not { } name) return;
        AddAndSelect(new Playset { Name = name });
    }

    [RelayCommand]
    private void DuplicatePlayset()
    {
        if (SelectedPlayset is { } p) AddAndSelect(p.Clone(p.Name + " (copy)"));
    }

    [RelayCommand]
    private void ExportPlayset()
    {
        if (SelectedPlayset is not { } p) return;
        var file = string.Concat(p.Name.Where(c => !Path.GetInvalidFileNameChars().Contains(c)));
        if (_dialogs.PickSaveFile("Export playset", file + ".json", "Playset (*.json)|*.json") is not { } path) return;
        Try(() =>
        {
            AtomicFile.WriteAllText(path, p.ToJson());
            Notify($"Exported \"{p.Name}\".");
        });
    }

    [RelayCommand]
    private void ImportPlaysetFile()
    {
        if (_dialogs.PickOpenFile("Import playset", "Playset (*.json)|*.json") is not { } path) return;
        try
        {
            var playset = Playset.Import(File.ReadAllText(path));
            var name = playset.Name;
            for (var n = 2; Playsets.Any(p => p.Name == name); n++) name = $"{playset.Name} ({n})";
            playset.Name = name;
            AddAndSelect(playset);
            var mods = playset.Mods.Where(m => !m.IsSeparator).ToList();
            var missing = mods.Count(m => !_installed.ContainsKey(m.Key));
            Notify($"Imported \"{name}\": {mods.Count} mods" + (missing > 0 ? $", {missing} not installed." : "."));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            ShowError($"That file isn't a playset Artemis can read: {ex.Message}", ex);
        }
    }

    private void AddAndSelect(Playset playset)
    {
        Try(() => _store.Save(playset));
        Playsets.Add(playset);
        SelectedPlayset = playset;
    }

    [RelayCommand]
    private void RenamePlayset()
    {
        if (SelectedPlayset is not { } p || _dialogs.Prompt("Rename playset", p.Name) is not { } name) return;
        p.Name = name;
        Try(() => _store.Save(p));
        OnPropertyChanged(nameof(SelectedPlaysetName));
    }

    [RelayCommand]
    private void DeletePlayset()
    {
        if (SelectedPlayset is not { } p || !_dialogs.Confirm($"Delete the playset \"{p.Name}\"? The mods themselves aren't touched.")) return;
        Try(() => _store.Delete(p.Id));
        Playsets.Remove(p);
        if (Playsets.Count == 0) Playsets.Add(new Playset { Name = "Default" });
        SelectedPlayset = Playsets[0];
    }

    // ---- launching ----

    private bool CanLaunch() => !GameRunning;

    private bool CanResume() => !GameRunning && LastSave is not null;

    /// <summary>Saves the load order, then starts the game the way Steam counts as playing it (see <see cref="LaunchService.StartInfo"/>).</summary>
    [RelayCommand(CanExecute = nameof(CanLaunch))]
    private Task Launch() => LaunchGameAsync(resume: false);

    /// <summary>One start with no plugins at all (D24), whatever is approved; the next start is as usual.</summary>

    /// <summary>Like Launch, and the game loads the newest save at once.</summary>
    [RelayCommand(CanExecute = nameof(CanResume))]
    private Task Resume() => LaunchGameAsync(resume: true);

    private bool _launching;

    private async Task LaunchGameAsync(bool resume, bool withoutPlugins = false)
    {
        if (_launcher is null || SelectedPlayset is not { } p || _launching) return;
        _launching = true;
        try
        {
            var result = _launcher.Apply(p, _installed);
            RememberGameList();
            MarkSeen();
            ExportPluginFiles(p);
            RepairOrionForLaunch();
            // Steam must be up for the game to count as played; starting it can take a while, so not on the window's thread.
            var steam = await Task.Run(() => LaunchService.EnsureSteam(TimeSpan.FromSeconds(40)));
            if (!steam) AppLog.Warn("Steam did not start in time; starting the game anyway");
            _launcher.Launch(resume, withoutPlugins);
            var skipped = result.SkippedMods > 0 ? $" ({result.SkippedMods} skipped: not installed)" : "";
            AppLog.Info($"Started Stellaris{(resume ? " to continue the last save" : "")}{(withoutPlugins ? ", without plugins," : "")} with {result.EnabledMods} mods from the playset \"{p.Name}\"{skipped}");
            Notify($"Launched with {result.EnabledMods} mods{skipped}" + (resume ? "; the game was asked to continue the last save." : ".") + (withoutPlugins ? " No plugins this time." : "") + (steam ? "" : " Steam didn't start, so the game may not count as played."));
        }
        catch (Exception ex) when (ex is ArtemisException or IOException or UnauthorizedAccessException or Win32Exception)
        {
            ShowError(ex.Message, ex);
        }
        finally
        {
            _launching = false;
        }
    }

    [RelayCommand]
    private void ApplyOnly()
    {
        if (_launcher is null || SelectedPlayset is not { } p) return;
        try
        {
            var result = _launcher.Apply(p, _installed);
            RememberGameList();
            MarkSeen();
            ExportPluginFiles(p);
            RepairOrionForLaunch();
            AppLog.Info($"Saved the playset \"{p.Name}\" ({result.EnabledMods} mods) to dlc_load.json without launching");
            Notify($"Saved the load order ({result.EnabledMods} mods). Start the game from Steam or the Launcher.");
        }
        catch (Exception ex) when (ex is ArtemisException or IOException or UnauthorizedAccessException) { ShowError(ex.Message, ex); }
    }

    [RelayCommand]
    private void RestoreOriginal()
    {
        if (_launcher is null) return;
        try
        {
            var restored = _launcher.RestoreOriginal();
            AppLog.Info(restored ? "Restored the original dlc_load.json" : "Restore asked for, but there is no saved original dlc_load.json");
            RememberGameList();
            Notify(restored
                ? "Restored the original dlc_load.json."
                : "There's no saved original to restore yet. Artemis saves one the first time you launch.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ShowError(ex.Message, ex); }
    }

    // ---- another tool changed dlc_load.json: whoever writes last wins, so say so instead of silently losing their list ----

    private ExternalPlayset? _drift;

    private List<string>? ReadGameList()
    {
        try { return _game is not null && File.Exists(_game.DlcLoadPath) ? DlcLoad.Read(_game.DlcLoadPath).EnabledMods.ToList() : null; }
        catch (Exception ex) when (ex is IOException or JsonException) { return null; }
    }

    /// <summary>Note the file as it is now, as what Artemis expects to find next time.</summary>
    private void RememberGameList()
    {
        if (ReadGameList() is not { } list) return;
        _settings.GameListStamp = DlcLoad.Stamp(list);
        SaveSettings();
    }

    /// <summary>Called when the window comes back to the front, and at start-up.</summary>
    public void CheckDrift()
    {
        if (_game is not null)
        {
            LastSave = LaunchService.FindLastSave(_game.UserDir); // a game played meanwhile has a newer save
            RefreshStatus();
        }
        if (HasDrift || ReadGameList() is not { } list) return;
        var stamp = DlcLoad.Stamp(list);
        if (_settings.GameListStamp is null)
        {
            RememberGameList();
            return;
        }
        if (stamp == _settings.GameListStamp) return;
        AppLog.Info($"dlc_load.json was changed outside Artemis ({list.Count} mods)");
        _drift = new ExternalPlayset(CurrentListSource, $"Game list, {DateTime.Now:d MMM HH:mm}", DlcLoad.ToPlaysetMods(list));
        DriftText = $"The game's mod list ({list.Count} mods) was changed outside Artemis, probably by the Paradox Launcher or Irony. Launching from Artemis replaces it with this playset.";
        HasDrift = true;
    }

    [RelayCommand]
    private void ImportDrift()
    {
        if (_drift is not null) AddImported([_drift]);
        IgnoreDrift();
    }

    [RelayCommand]
    private void IgnoreDrift()
    {
        RememberGameList();
        _drift = null;
        HasDrift = false;
    }

    // ---- small helpers ----

    [RelayCommand]
    private void OpenModFolder()
    {
        if (_game is not null) Open(_game.ModDir);
    }

    public void OpenFolderOf(ModInfo mod)
    {
        if (mod.ContentDir is not null) Open(mod.ContentDir);
    }

    public void OpenOnWorkshop(ModInfo mod)
    {
        if (mod.RemoteFileId is { Length: > 0 } id) Open($"steam://url/CommunityFilePage/{id}");
    }

    private void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is Win32Exception or IOException) { ShowError($"Couldn't open {target}: {ex.Message}", ex); }
    }

    private async void Notify(string text, bool undo = false)
    {
        Message = text;
        MessageHasUndo = undo;
        await Task.Delay(6000);
        if (Message != text) return;
        Message = "";
        MessageHasUndo = false;
    }

    private void Try(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArtemisException)
        {
            ShowError(ex.Message, ex);
        }
    }

    /// <summary>Tells the user and writes it to the Artemis log.</summary>
    private void ShowError(string message, Exception? exception = null)
    {
        AppLog.Error(message, exception);
        _dialogs.Error(message);
    }
}
