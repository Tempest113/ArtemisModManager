using System.Collections.ObjectModel;
using Artemis.Core.Index;
using Artemis.Core.Mods;
using Artemis.Core.Plugins;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Artemis.App.ViewModels;

public sealed record DetailLine(string Label, string Value, bool Warn = false);

/// <summary>The details drawer and the dependency warnings on load order rows.</summary>
public sealed partial class MainViewModel
{
    private Dictionary<string, ModInfo> _installedByName = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, (int Index, bool Enabled)> _inPlayset = new(StringComparer.OrdinalIgnoreCase);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetails))]
    private ModRowBase? _detailsRow;

    [ObservableProperty] private bool _detailsOpen;
    [ObservableProperty] private bool _detailsHasWorkshop;
    [ObservableProperty] private bool _detailsHasPlugin;
    [ObservableProperty] private bool _detailsHasFolder;
    [ObservableProperty] private bool _detailsHasDependencies;
    [ObservableProperty] private bool _detailsCanChangePlayset;
    [ObservableProperty] private string _detailsPlaysetButtonText = "Add to playset";

    public ObservableCollection<DetailLine> DetailLines { get; } = [];
    public ObservableCollection<DependencyStatus> DependencyLines { get; } = [];

    public bool HasDetails => DetailsRow is not null;

    [ObservableProperty] private string _detailsNote = "";
    private bool _loadingNote;

    partial void OnDetailsNoteChanged(string value)
    {
        if (_loadingNote || DetailsRow?.Mod is not { } mod) return;
        if (string.IsNullOrWhiteSpace(value)) _settings.Notes.Remove(mod.Key);
        else _settings.Notes[mod.Key] = value;
        SaveSettings();
        RefreshMarks(mod.Key);
    }

    /// <summary>The drawer follows whichever mod was last selected, in either list.</summary>
    public void FollowSelection(ModRowBase row)
    {
        DetailsRow = row;
        IndexPlayset();
        RefreshDetails();
    }

    private void IndexPlayset()
    {
        _inPlayset = new Dictionary<string, (int, bool)>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < LoadOrder.Count; i++)
            if (!LoadOrder[i].IsSeparator) _inPlayset[LoadOrder[i].Key] = (i, LoadOrder[i].Enabled);
    }

    /// <summary>Flags load order rows that load above a mod their descriptor lists (an ordering hint, not a requirement), and brings the drawer up to date.</summary>
    private void CheckDependencies()
    {
        IndexPlayset();
        foreach (var row in LoadOrder)
        {
            if (row.IsSeparator || row.Mod is null || !row.Enabled)
            {
                row.DependencyProblem = null;
                continue;
            }
            var bad = DependencyCheck.For(row.Mod, _installedByName, _inPlayset, _inPlayset[row.Key].Index)
                .Where(d => !d.Ok).Select(d => $"Its descriptor asks to load after \"{d.Name}\", which is below it. Move it above.").ToList();
            row.DependencyProblem = bad.Count == 0 ? null : string.Join("\n", bad);
        }
        RefreshDetails();
    }

    private void RefreshDetails()
    {
        DetailLines.Clear();
        DependencyLines.Clear();
        if (DetailsRow is not { } row)
        {
            DetailsHasWorkshop = DetailsHasFolder = DetailsHasDependencies = DetailsCanChangePlayset = false;
            return;
        }

        var mod = row.Mod;
        _loadingNote = true;
        DetailsNote = row.Note ?? "";
        _loadingNote = false;
        var slot = _inPlayset.TryGetValue(row.Key, out var s) ? s : (Index: -1, Enabled: false);
        var inPlayset = slot.Index >= 0;

        DetailLines.Add(new DetailLine("Playset", inPlayset ? $"In the load order, #{LoadOrder.Take(slot.Index).Count(r => !r.IsSeparator) + 1}{(slot.Enabled ? "" : ", switched off")}" : "Not in this playset"));
        if (mod is null)
            DetailLines.Add(new DetailLine("Files", "This mod isn't installed.", Warn: true));
        else
        {
            if (row.Change != ModChange.None) DetailLines.Add(new DetailLine("Changed", row.Change == ModChange.New ? "New since you last launched" : "Updated since you last launched"));
            DetailLines.Add(new DetailLine("Source", mod.Source == ModSource.Workshop ? "Steam Workshop" : "Local mod"));
            if (!string.IsNullOrWhiteSpace(mod.Version)) DetailLines.Add(new DetailLine("Version", mod.Version));
            if (!string.IsNullOrWhiteSpace(mod.SupportedVersion))
                DetailLines.Add(new DetailLine("Made for", row.IsOutdated ? $"{mod.SupportedVersion}; the game is {GameVersion}" : mod.SupportedVersion, Warn: row.IsOutdated));
            if (mod.Modified > default(DateTime)) DetailLines.Add(new DetailLine("Added", mod.Modified.ToLocalTime().ToString("d MMM yyyy")));
            if (mod.Tags.Count > 0) DetailLines.Add(new DetailLine("Tags", string.Join(", ", mod.Tags)));
            if (mod.ContentMissing) DetailLines.Add(new DetailLine("Files", "The mod's files are missing.", Warn: true));
            if (mod.ContentDir is not null) DetailLines.Add(new DetailLine("Folder", mod.ContentDir));
            if (mod.Plugin is not null)
            {
                var mark = PluginMarkFor(mod)!.Value;
                DetailLines.Add(new DetailLine("Plugin", mod.Plugin.Manifest is { } pm ? $"{pm.Name} {pm.Version}, {(pm.Tier == PluginTier.Stable ? "stable" : "fragile")}: {mark.Tip}" : mark.Tip, Warn: mark.Warn));
            }
            if (_checksum is not null && _indexStats.ContainsKey((SourceKind.Mod, mod.Key)))
                DetailLines.Add(_checkedChanges.TryGetValue(mod.Key, out var changed)
                    ? new DetailLine("Achievements", $"Turned off: it adds or changes {changed.Count:N0} file{(changed.Count == 1 ? "" : "s")} the game's checksum covers, such as {changed[0]}", Warn: true)
                    : new DetailLine("Achievements", "Not affected"));
            if (_modConflicts.TryGetValue(mod.Key, out var overlaps))
                DetailLines.Add(new DetailLine("Conflicts", $"{overlaps.Total:N0} with other mods, this mod wins {overlaps.Wins:N0}; see the Conflicts page"));
            if (_indexStats.TryGetValue((SourceKind.Mod, mod.Key), out var indexed))
                DetailLines.Add(new DetailLine("Files", $"{indexed.Files:N0}; {indexed.Defs:N0} definitions and text keys"));
            DetailLines.Add(new DetailLine("ID", mod.Key));

            foreach (var d in DependencyCheck.For(mod, _installedByName, _inPlayset, slot.Index)) DependencyLines.Add(d);
        }

        DetailsHasPlugin = mod?.Plugin is not null;
        DetailsHasWorkshop = mod?.RemoteFileId is { Length: > 0 };
        DetailsHasFolder = mod?.ContentDir is not null;
        DetailsHasDependencies = DependencyLines.Count > 0;
        DetailsCanChangePlayset = mod is not null;
        DetailsPlaysetButtonText = inPlayset ? "Remove from playset" : "Add to playset";
    }

    [RelayCommand] private void ToggleDetails() => DetailsOpen = !DetailsOpen;

    [RelayCommand] private void CloseDetails() => DetailsOpen = false;

    [RelayCommand]
    private void OpenDetailsFolder()
    {
        if (DetailsRow?.Mod is { } mod) OpenFolderOf(mod);
    }

    [RelayCommand]
    private void OpenDetailsPlugin()
    {
        if (DetailsRow?.Mod is not { } mod) return;
        Page = AppPage.Plugins;
        SelectedPlugin = PluginRows.FirstOrDefault(r => string.Equals(r.Key, mod.Key, StringComparison.OrdinalIgnoreCase)) ?? SelectedPlugin;
    }

    [RelayCommand]
    private void OpenDetailsWorkshop()
    {
        if (DetailsRow?.Mod is { } mod) OpenOnWorkshop(mod);
    }

    [RelayCommand]
    private void ToggleDetailsPlayset()
    {
        if (DetailsRow?.Mod is not { } mod) return;
        SetInPlayset(mod, !IsInPlayset(mod.Key));
        if (_installedRows.TryGetValue(mod.Key, out var row)) row.Refresh();
    }
}
