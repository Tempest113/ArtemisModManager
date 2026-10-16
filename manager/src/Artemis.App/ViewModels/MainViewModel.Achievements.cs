using System.IO;
using Artemis.Core.Achievements;
using Artemis.Core.Index;
using Artemis.Core.Logging;
using Artemis.Core.Mods;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Artemis.App.ViewModels;

/// <summary>Whether each mod leaves achievements on (a trophy in both lists), from the game's own list of the files its checksum covers.</summary>
public sealed partial class MainViewModel
{
    private ChecksumRules? _checksum;
    private Dictionary<string, IReadOnlyList<string>> _checkedChanges = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The files of the mod that the game's checksum covers: none means achievement-compatible. Null until the mod has been looked at (or the game has no list).</summary>
    public IReadOnlyList<string>? ChecksumChangesFor(string key) =>
        _checksum is null || !_indexStats.ContainsKey((SourceKind.Mod, key)) ? null : _checkedChanges.GetValueOrDefault(key) ?? [];

    private void LoadChecksumRules()
    {
        try
        {
            _checksum = ChecksumRules.Load(_game!.GameDir);
            if (_checksum is null) AppLog.Warn($"The game has no {ChecksumRules.FileName}, so achievements can't be checked");
        }
        catch (IOException ex)
        {
            _checksum = null;
            AppLog.Error($"Couldn't read {ChecksumRules.FileName}", ex);
        }
    }

    /// <summary>Looks at the files of every indexed mod; called whenever the index has been brought up to date.</summary>
    private async Task CheckAchievementsAsync(IReadOnlyList<ModInfo> mods)
    {
        if (_checksum is not { } rules) return;
        var indexed = mods.Select(m => m.Key).Where(k => _indexStats.ContainsKey((SourceKind.Mod, k))).ToList();
        _checkedChanges = await Task.Run(() => AchievementCheck.Scan(_indexStore, indexed, rules));
        // Rows made since read their state when they appear; the ones on show look again.
        foreach (var row in _installedRows.Values) row.RefreshAchievement();
        foreach (var row in LoadOrder) row.RefreshAchievement();
    }
}
