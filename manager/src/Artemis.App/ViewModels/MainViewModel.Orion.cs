using Artemis.Core.Game;
using Artemis.Core.IO;
using Artemis.Core.Logging;
using Artemis.Core.Plugins;
using Artemis.Core.Text;
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
    [ObservableProperty] private string _installOrionText = Loc.T("plug.orion_install_button");
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
            OrionState.Installed => (Loc.T("plug.orion_installed_title", ("version", status.InstalledVersion)), Loc.T("plug.orion_installed_detail"), false),
            OrionState.NotInstalled => (Loc.T("plug.orion_missing_title"), Loc.T("plug.orion_missing_detail"), true),
            OrionState.NeedsRepair => (Loc.T("plug.orion_repair_title"), Loc.T("plug.orion_repair_detail"), true),
            OrionState.Outdated => (Loc.T("plug.orion_outdated_title", ("version", status.AvailableVersion)), Loc.T("plug.orion_outdated_detail", ("installed", status.InstalledVersion)), true),
            OrionState.ForeignStub => (Loc.T("plug.orion_stub_title"), Loc.T("plug.orion_stub_detail"), true),
            _ => (Loc.T("plug.orion_absent_title"), Loc.T("plug.orion_absent_detail"), true),
        };
        (InstallOrionText, CanInstallOrion) = status.State switch
        {
            OrionState.NotInstalled => (Loc.T("plug.orion_install_button"), true),
            OrionState.NeedsRepair => (Loc.T("plug.orion_repair_button"), available),
            OrionState.Outdated => (Loc.T("plug.orion_update_button"), true),
            _ => (Loc.T("plug.orion_install_button"), false),
        };
        CanRemoveOrion = status.State is OrionState.Installed or OrionState.NeedsRepair or OrionState.Outdated;

        _buildId = GameBuild.IdOf(_game.ExePath);
        var support = _buildId is null ? BuildSupport.Unknown : _builds.Check(_buildId);
        var shown = GameVersion.Length > 0 ? $"{_buildId} ({GameVersion})" : _buildId;
        BuildText = _buildId is null ? Loc.T("plug.build_unreadable")
            : support switch
            {
                BuildSupport.Verified => Loc.T("plug.build_verified", ("build", shown)),
                BuildSupport.AllowedByUser => Loc.T("plug.build_allowed", ("build", shown)),
                _ => Loc.T("plug.build_unverified", ("build", shown)),
            };
        CanAllowBuild = _buildId is not null && support == BuildSupport.Unknown && GameVersion.Length > 0;
        CanStopAllowing = support == BuildSupport.AllowedByUser;

        (HonestText, HonestNeedsAttention) = _lastStatus?.Honest switch
        {
            { State: "mounted" } h => (Loc.T("plug.honest_mounted", ("detail", h.Detail)), false),
            { State: "failed" } h => (Loc.T("plug.honest_failed", ("detail", h.Detail)), true),
            { State: "pending" } => (Loc.T("plug.honest_pending"), true),
            _ => ("", false),
        };
    }

    [RelayCommand(CanExecute = nameof(CanChangeOrion))]
    private void InstallOrion()
    {
        if (_game is null || _orion is null) return;
        var first = _orion.Inspect(_game.GameDir).State == OrionState.NotInstalled;
        if (first && !_dialogs.Confirm(
                Loc.T("plug.orion_install_confirm", ("dir", _game.GameDir)), Loc.T("plug.install"))) return;
        Try(() =>
        {
            _orion.Install(_game.GameDir);
            _settings.OrionInstalled = true;
            SaveSettings();
            AppLog.Info(first ? "Installed Orion" : "Repaired or updated Orion");
            Notify(Loc.T(first ? "plug.orion_is_installed" : "plug.orion_up_to_date"));
        });
        RefreshOrion();
        RefreshNotices();
    }

    [RelayCommand(CanExecute = nameof(CanChangeOrion))]
    private void RemoveOrion()
    {
        if (_game is null || _orion is null) return;
        if (!_dialogs.Confirm(Loc.T("plug.orion_remove_confirm"), Loc.T("plug.remove"))) return;
        Try(() =>
        {
            _orion.Remove(_game.GameDir);
            _settings.OrionInstalled = false;
            SaveSettings();
            AppLog.Info("Removed Orion");
            Notify(Loc.T("plug.orion_removed"));
        });
        RefreshOrion();
        RefreshNotices();
    }

    [RelayCommand]
    private void AllowBuild()
    {
        if (_buildId is null || _builds is null) return;
        if (!_dialogs.Confirm(
                Loc.T("plug.allow_build_confirm", ("build", _buildId), ("version", GameVersion)), Loc.T("plug.allow"))) return;
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
