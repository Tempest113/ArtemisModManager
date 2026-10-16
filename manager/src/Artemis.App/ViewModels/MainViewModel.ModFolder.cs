using System.IO;
using Artemis.Core.Launch;
using Artemis.Core.Logging;
using Artemis.Core.Mods;
using Artemis.Core.IO;
using Artemis.Core.Text;
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
        string.Join(", ", mods.Take(max).Select(m => m.Name)) + (mods.Count > max ? Loc.T("plug.name_list_more", ("count", mods.Count - max)) : "");

    /// <summary>The notice for leftover .mod files, or null when there are none or the user has already put this set aside.</summary>
    private NoticeViewModel? LeftoverNotice()
    {
        var leftovers = LeftoverMods();
        if (leftovers.Count == 0 || _settings.DismissedNotices.Contains(LeftoverId(leftovers))) return null;
        return new NoticeViewModel(LeftoverId(leftovers),
            Loc.T(leftovers.Count == 1 ? "plug.leftover_notice_one" : "plug.leftover_notice_many", ("count", leftovers.Count), ("names", NameList(leftovers, 3))),
            Loc.T("plug.review"), () => _ = ReviewLeftoverMods(), DismissNotice);
    }

    [RelayCommand]
    private async Task ReviewLeftoverMods()
    {
        if (_game is null) return;
        var leftovers = LeftoverMods();
        if (leftovers.Count == 0) { Notify(Loc.T("plug.leftover_none")); return; }
        var list = string.Join("\n", leftovers.Take(20).Select(m => $"   {m.Name}   ({m.LoadEntry})")) + (leftovers.Count > 20 ? Loc.T("plug.list_more", ("count", leftovers.Count - 20)) : "");
        var workshop = leftovers.Any(m => m.Source == ModSource.Workshop)
            ? Loc.T("plug.leftover_workshop")
            : "";
        if (!_dialogs.Confirm(Loc.T("plug.leftover_confirm", ("list", list), ("workshop", workshop)), Loc.T("plug.recycle"))) return;
        try
        {
            var moved = await Task.Run(() => ModFolderHealth.RemoveDescriptors(_game.UserDir, leftovers, RecycleBin.Send));
            AppLog.Info($"Sent {moved} leftover .mod files to the Recycle Bin: {NameList(leftovers, 10)}");
            await ScanAsync();
            ShowPlayset();
            await RefreshPluginsAsync();
            _ = UpdateIndexAsync();
            Notify(moved == 1 ? Loc.T("plug.leftover_sent_one") : Loc.T("plug.leftover_sent_many", ("count", moved)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ShowError(ex.Message, ex); }
    }
}
