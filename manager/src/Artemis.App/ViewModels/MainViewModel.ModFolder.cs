using System.IO;
using Artemis.Core.Launch;
using Artemis.Core.Logging;
using Artemis.Core.Mods;
using Artemis.Core.IO;
using CommunityToolkit.Mvvm.Input;

namespace Artemis.App.ViewModels;

/// <summary>Keeping the mod folder and its .mod files in step (D55): mod folders without a .mod file get one, and .mod files whose mod is gone can be cleared away.</summary>
public sealed partial class MainViewModel
{
    /// <summary>Mods whose .mod file is still there but whose folder or archive is not.</summary>
    private List<ModInfo> LeftoverMods() => _installed.Values.Where(m => m.ContentMissing).OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    /// <summary>The id a "mods are missing" notice carries: it changes when the set of missing mods does, so a dismissed notice comes back only for new ones.</summary>
    private static string LeftoverId(IEnumerable<ModInfo> leftovers) => "missing|" + DlcLoad.Stamp(leftovers.Select(m => m.Key).Order(StringComparer.OrdinalIgnoreCase))[..16];

    private static string NameList(IReadOnlyList<ModInfo> mods, int max) =>
        string.Join(", ", mods.Take(max).Select(m => m.Name)) + (mods.Count > max ? $" and {mods.Count - max} more" : "");

    /// <summary>The notice for leftover .mod files, or null when there are none or the user has already put this set aside.</summary>
    private NoticeViewModel? LeftoverNotice()
    {
        var leftovers = LeftoverMods();
        if (leftovers.Count == 0 || _settings.DismissedNotices.Contains(LeftoverId(leftovers))) return null;
        return new NoticeViewModel(LeftoverId(leftovers),
            $"{(leftovers.Count == 1 ? "1 mod has" : $"{leftovers.Count} mods have")} a .mod file but no files any more (deleted or unsubscribed): {NameList(leftovers, 3)}.",
            "Review…", () => _ = ReviewLeftoverMods(), DismissNotice);
    }

    [RelayCommand]
    private async Task ReviewLeftoverMods()
    {
        if (_game is null) return;
        var leftovers = LeftoverMods();
        if (leftovers.Count == 0) { Notify("No mod in the mod folder is missing its files."); return; }
        var list = string.Join("\n", leftovers.Take(20).Select(m => $"   {m.Name}   ({m.LoadEntry})")) + (leftovers.Count > 20 ? $"\n   … and {leftovers.Count - 20} more" : "");
        var workshop = leftovers.Any(m => m.Source == ModSource.Workshop)
            ? "\n\nSome of these came from the Steam Workshop. If you unsubscribed on purpose, go ahead; if Steam is still downloading them or a drive is unplugged, wait."
            : "";
        if (!_dialogs.Confirm($"These mods have a .mod file in your mod folder, but their files are gone:\n\n{list}{workshop}\n\nSend the leftover .mod files to the Recycle Bin? Playsets keep their entries.", "Recycle")) return;
        try
        {
            var moved = await Task.Run(() => ModFolderHealth.RemoveDescriptors(_game.UserDir, leftovers, RecycleBin.Send));
            AppLog.Info($"Sent {moved} leftover .mod files to the Recycle Bin: {NameList(leftovers, 10)}");
            await ScanAsync();
            ShowPlayset();
            await RefreshPluginsAsync();
            _ = UpdateIndexAsync();
            Notify(moved == 1 ? "Sent 1 leftover .mod file to the Recycle Bin." : $"Sent {moved} leftover .mod files to the Recycle Bin.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ShowError(ex.Message, ex); }
    }
}
