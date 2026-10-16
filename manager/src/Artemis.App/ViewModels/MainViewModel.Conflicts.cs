using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using System.Windows.Threading;
using Artemis.Core.Conflicts;
using Artemis.Core.Index;
using Artemis.Core.Logging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Artemis.App.ViewModels;

public enum ConflictFilter { All, File, Definition, Text }

/// <summary>The Conflicts page: where the enabled mods, or a mod and the game, provide the same thing, and which one wins.</summary>
public sealed partial class MainViewModel
{
    public BulkCollection<ConflictRowViewModel> Conflicts { get; } = [];
    public ICollectionView ConflictsView { get; private set; } = null!;

    private readonly DispatcherTimer _conflictTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool _conflictsRunning, _conflictsAgain, _conflictsKnown;
    private Dictionary<string, (int Total, int Wins)> _modConflicts = new(StringComparer.OrdinalIgnoreCase);

    [ObservableProperty] private ConflictRowViewModel? _selectedConflict;
    [ObservableProperty] private string _conflictsShownText = "";
    [ObservableProperty] private string _conflictsEmptyText = "";
    [ObservableProperty] private string _conflictsNote = "";

    /// <summary>The number of conflicts between mods, for the page's label in the left column; empty when there are none.</summary>
    [ObservableProperty] private string _conflictBadge = "";

    [ObservableProperty] private ConflictFilter _conflictFilter;
    /// <summary>On by default: hides conflicts where a single mod only replaces something of the game's.</summary>
    [ObservableProperty] private bool _excludeVanilla = true;
    [ObservableProperty] private string _conflictSearch = "";

    private void InitConflicts()
    {
        ConflictsView = CollectionViewSource.GetDefaultView(Conflicts);
        ConflictsView.Filter = o => MatchesConflict((ConflictRowViewModel)o);
        _conflictTimer.Tick += async (_, _) =>
        {
            _conflictTimer.Stop();
            await ComputeConflictsAsync();
        };
    }

    public bool HasConflicts(string key) => _modConflicts.ContainsKey(key);

    public bool IsConflictAll { get => ConflictFilter == ConflictFilter.All; set { if (value) ConflictFilter = ConflictFilter.All; } }
    public bool IsConflictFile { get => ConflictFilter == ConflictFilter.File; set { if (value) ConflictFilter = ConflictFilter.File; } }
    public bool IsConflictDefinition { get => ConflictFilter == ConflictFilter.Definition; set { if (value) ConflictFilter = ConflictFilter.Definition; } }
    public bool IsConflictText { get => ConflictFilter == ConflictFilter.Text; set { if (value) ConflictFilter = ConflictFilter.Text; } }

    partial void OnConflictFilterChanged(ConflictFilter value)
    {
        OnPropertyChanged(nameof(IsConflictAll));
        OnPropertyChanged(nameof(IsConflictFile));
        OnPropertyChanged(nameof(IsConflictDefinition));
        OnPropertyChanged(nameof(IsConflictText));
        RefreshConflictView();
    }

    partial void OnExcludeVanillaChanged(bool value) => RefreshConflictView();

    partial void OnConflictSearchChanged(string value) => RefreshConflictView();

    private bool MatchesConflict(ConflictRowViewModel row)
    {
        var c = row.Conflict;
        if (ExcludeVanilla && c.ModCount < 2) return false;
        if (ConflictFilter != ConflictFilter.All && c.Kind != (ConflictKind)(ConflictFilter - 1)) return false;
        var search = ConflictSearch.Trim();
        return search.Length == 0 || row.Matches(search);
    }

    private void RefreshConflictView()
    {
        ConflictsView.Refresh();
        var shown = ConflictsView.Cast<object>().Count();
        ConflictsShownText = Conflicts.Count == 0 ? "" : shown == 1 ? "1 conflict" : $"{shown:N0} conflicts";
        ConflictsEmptyText = shown > 0 || !_conflictsKnown ? ""
            : Conflicts.Count > 0 ? "No conflicts match this filter."
            : LoadOrder.Any(r => !r.IsSeparator && r.Enabled) ? "No conflicts between the enabled mods."
            : "Switch on mods in the load order to see where they overlap.";
        if (SelectedConflict is not null && !ConflictsView.Cast<object>().Contains(SelectedConflict)) SelectedConflict = null;
    }

    /// <summary>Called whenever the load order, a switch or the index changes: looks again shortly, once, however many changes come at once.</summary>
    public void RequestConflicts()
    {
        _conflictTimer.Stop();
        _conflictTimer.Start();
    }

    private async Task ComputeConflictsAsync()
    {
        if (_game is null || _indexing) return; // the end of indexing asks again
        if (_conflictsRunning)
        {
            _conflictsAgain = true;
            return;
        }
        _conflictsRunning = true;
        try
        {
            do
            {
                _conflictsAgain = false;
                var mods = LoadOrder.Where(r => !r.IsSeparator && r.Enabled && r.Mod is not null)
                    .Select(r => new ConflictMod(r.Mod!.Key, r.Mod.Name, r.Mod.Dependencies)).ToList();
                var game = _game;
                var report = await Task.Run(() => ConflictEngine.Find(_indexStore, mods, Indexer.GameBuildKey(game)));
                ShowConflictReport(report);
            }
            while (_conflictsAgain);
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException)
        {
            AppLog.Error("Conflicts couldn't be worked out", ex);
            ConflictsEmptyText = "Couldn't read the mod index.";
        }
        finally
        {
            _conflictsRunning = false;
        }
    }

    private void ShowConflictReport(ConflictReport report)
    {
        var selected = SelectedConflict?.Conflict;
        Conflicts.ReplaceAll(report.Conflicts.Select(c => new ConflictRowViewModel(c)));
        _conflictsKnown = true;
        ConflictsNote = report.NotIndexed.Count switch
        {
            0 => "",
            1 => "1 enabled mod isn't indexed yet, so it isn't checked.",
            var n => $"{n} enabled mods aren't indexed yet, so they aren't checked.",
        };

        var perMod = new Dictionary<string, (int Total, int Wins)>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in report.Conflicts.Where(c => c.ModCount >= 2))
            for (var i = 0; i < c.Entries.Count; i++)
            {
                if (c.Entries[i].IsGame) continue;
                var (total, wins) = perMod.GetValueOrDefault(c.Entries[i].SourceKey);
                perMod[c.Entries[i].SourceKey] = (total + 1, wins + (i == c.Winner ? 1 : 0));
            }
        _modConflicts = perMod;

        var between = report.Conflicts.Count(c => c.ModCount >= 2);
        ConflictBadge = between > 0 ? between.ToString("N0") : "";
        RefreshConflictView();
        if (Filter == InstalledFilter.WithConflicts) OnFilterChanged(Filter);
        if (selected is not null)
            SelectedConflict = Conflicts.FirstOrDefault(r => r.Conflict.Kind == selected.Kind && r.Conflict.Domain == selected.Domain && r.Conflict.Name == selected.Name);
        RefreshDetails();
    }
}
