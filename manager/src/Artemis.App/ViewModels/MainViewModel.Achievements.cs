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
    private IReadOnlyList<string>? ChecksumChangesFor(string key) =>
        _checksum is null || !_indexStats.ContainsKey((SourceKind.Mod, key)) ? null : _checkedChanges.GetValueOrDefault(key) ?? [];

    /// <summary>Whether the mod's plugin would count toward the game's checksum (honest mode, D53): it isn't cosmetic, and plugins are on and not switched off for this mod.</summary>
    private bool PluginCounts(ModInfo mod) => _settings.PluginsEnabled && mod.Plugin?.Manifest is { Cosmetic: false } && !PluginIsOff(mod.Key);

    /// <summary>What the mod does to achievements, for the trophy beside it and the details drawer.</summary>
    public AchievementVerdict AchievementFor(ModInfo mod) => AchievementCheck.Judge(ChecksumChangesFor(mod.Key), PluginCounts(mod));

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
