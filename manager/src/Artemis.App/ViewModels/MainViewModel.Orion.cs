using Artemis.Core.Game;
using Artemis.Core.IO;
using Artemis.Core.Logging;
using Artemis.Core.Plugins;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Artemis.App.ViewModels;

/// <summary>
/// Installing and removing Orion, the part that loads plugins into the game (D14, D24, D50), and the one permission the user gives for a game
/// build Orion has no verified data for (D15). Both are on the Plugins page.
/// </summary>
public sealed partial class MainViewModel
{
    private OrionInstaller? _orion;
    private OrionBuilds? _builds;
    private string? _buildId;

    [ObservableProperty] private string _orionTitle = "";
    [ObservableProperty] private string _orionDetail = "";
    [ObservableProperty] private bool _orionNeedsAttention;
    [ObservableProperty] private string _installOrionText = "Install Orion…";
    [ObservableProperty] private bool _canInstallOrion;
    [ObservableProperty] private bool _canRemoveOrion;
    [ObservableProperty] private string _buildText = "";
    [ObservableProperty] private string _honestText = "";
    [ObservableProperty] private bool _honestNeedsAttention;
    [ObservableProperty] private bool _canAllowBuild;
    [ObservableProperty] private bool _canStopAllowing;

    private bool CanChangeOrion() => !GameRunning;

    /// <summary>Works out what to say about Orion and the game build, and which buttons make sense. Cheap: a few file checks.</summary>
    private void RefreshOrion()
    {
        if (_game is null || _orion is null || _builds is null) return;
        var status = _orion.Inspect(_game.GameDir);
        var available = status.AvailableVersion is not null;
        (OrionTitle, OrionDetail, OrionNeedsAttention) = status.State switch
        {
            OrionState.Installed => ($"Orion {status.InstalledVersion} is installed.", "It loads the plugins you approved when Stellaris starts. It does nothing in the Paradox Launcher.", false),
            OrionState.NotInstalled => ("Orion isn't installed.", "Plugins can't load until it is. Your approvals and settings are saved and apply once it is.", true),
            OrionState.NeedsRepair => ("Orion needs repair.", "Part of it is missing; Steam's \"verify files\" removes it. Artemis puts it back the next time you launch, or you can repair it now.", true),
            OrionState.Outdated => ($"A different Orion ({status.AvailableVersion}) came with this Artemis.", $"Installed: {status.InstalledVersion}. Artemis updates it the next time you launch, or you can update it now.", true),
            OrionState.ForeignStub => ("Another winmm.dll is in the Stellaris folder.", "Artemis never replaces a file it didn't put there. Move that file out of the folder to install Orion.", true),
            _ => ("Orion isn't included with this copy of Artemis.", "Plugins can't load. Your approvals and settings are saved.", true),
        };
        (InstallOrionText, CanInstallOrion) = status.State switch
        {
            OrionState.NotInstalled => ("Install Orion…", true),
            OrionState.NeedsRepair => ("Repair Orion", available),
            OrionState.Outdated => ("Update Orion", true),
            _ => ("Install Orion…", false),
        };
        CanRemoveOrion = status.State is OrionState.Installed or OrionState.NeedsRepair or OrionState.Outdated;

        _buildId = GameBuild.IdOf(_game.ExePath);
        var support = _buildId is null ? BuildSupport.Unknown : _builds.Check(_buildId);
        var shown = GameVersion.Length > 0 ? $"{_buildId} ({GameVersion})" : _buildId;
        BuildText = _buildId is null ? "Artemis couldn't read the game's build from stellaris.exe, so it can't say whether Orion supports it."
            : support switch
            {
                BuildSupport.Verified => $"Game build {shown}: verified. Orion can load plugins on it.",
                BuildSupport.AllowedByUser => $"Game build {shown}: you allowed plugins on this exact build. A game update asks again.",
                _ => $"Game build {shown}: not verified. Orion loads no plugins on a build it hasn't verified.",
            };
        CanAllowBuild = _buildId is not null && support == BuildSupport.Unknown && GameVersion.Length > 0;
        CanStopAllowing = support == BuildSupport.AllowedByUser;

        (HonestText, HonestNeedsAttention) = _lastStatus?.Honest switch
        {
            { State: "mounted" } h => ($"Last run: {h.Detail} The game's checksum shows them, so achievements and multiplayer checks notice them.", false),
            { State: "failed" } h => ($"Last run: Orion could not add the plugins to the game's checksum. {h.Detail}", true),
            { State: "pending" } => ("Last run: the game closed before Orion had added the plugins to its checksum.", true),
            _ => ("", false),
        };
    }

    [RelayCommand(CanExecute = nameof(CanChangeOrion))]
    private void InstallOrion()
    {
        if (_game is null || _orion is null) return;
        var first = _orion.Inspect(_game.GameDir).State == OrionState.NotInstalled;
        if (first && !_dialogs.Confirm(
                "Orion is the part that loads plugins (DLL mods) into Stellaris. Installing it puts one file, winmm.dll, in the Stellaris folder:\n\n"
                + $"   {_game.GameDir}\n\n"
                + "Windows loads that file every time Stellaris starts. It does nothing in the Paradox Launcher, and it loads no plugin you haven't approved. Plugins run with the same permissions as you.\n\n"
                + "You can remove it any time from this page. Steam's \"verify files\" deletes it; Artemis puts it back the next time you launch.", "Install")) return;
        Try(() =>
        {
            _orion.Install(_game.GameDir);
            _settings.OrionInstalled = true;
            SaveSettings();
            AppLog.Info(first ? "Installed Orion" : "Repaired or updated Orion");
            Notify(first ? "Orion is installed." : "Orion is up to date.");
        });
        RefreshOrion();
        RefreshNotices();
    }

    [RelayCommand(CanExecute = nameof(CanChangeOrion))]
    private void RemoveOrion()
    {
        if (_game is null || _orion is null) return;
        if (!_dialogs.Confirm("Remove Orion? Stellaris goes back to starting without anything of ours, and no plugin loads. Your approvals and settings are kept.", "Remove")) return;
        Try(() =>
        {
            _orion.Remove(_game.GameDir);
            _settings.OrionInstalled = false;
            SaveSettings();
            AppLog.Info("Removed Orion");
            Notify("Orion is removed.");
        });
        RefreshOrion();
        RefreshNotices();
    }

    [RelayCommand]
    private void AllowBuild()
    {
        if (_buildId is null || _builds is null) return;
        if (!_dialogs.Confirm(
                $"Orion has no verified data for this game build ({_buildId}, {GameVersion}), so it loads no plugins here.\n\n"
                + "If you allow it, plugins that only use what Orion hands them (their settings, logging) will work. A plugin that changes the game's own code is on its own on a build nobody has checked, and can crash the game.\n\n"
                + "This applies to this exact build only; after a game update Artemis asks again.", "Allow")) return;
        Try(() =>
        {
            _builds.Allow(_buildId, GameVersion);
            AppLog.Info($"Allowed plugins on the unverified game build {_buildId} ({GameVersion})");
        });
        RefreshOrion();
    }

    [RelayCommand]
    private void StopAllowingBuild()
    {
        if (_buildId is null || _builds is null) return;
        Try(() =>
        {
            _builds.StopAllowing(_buildId);
            AppLog.Info($"Took back the permission for plugins on the game build {_buildId}");
        });
        RefreshOrion();
    }

    /// <summary>
    /// Before the game starts: if the user installed Orion and something took part of it away (Steam's file check) or this Artemis carries a newer one,
    /// put it right. Failing here never stops the launch; the game then starts as vanilla.
    /// </summary>
    private void RepairOrionForLaunch()
    {
        if (_game is null || _orion is null || !_settings.OrionInstalled) return;
        var state = _orion.Inspect(_game.GameDir).State;
        if (state is not (OrionState.NeedsRepair or OrionState.Outdated)) return;
        try
        {
            _orion.Install(_game.GameDir);
            AppLog.Info(state == OrionState.NeedsRepair ? "Orion was put back before launching" : "Orion was updated before launching");
        }
        catch (ArtemisException ex) { AppLog.Warn("Orion couldn't be put back before launching: " + ex.Message); }
        RefreshOrion();
    }
}
