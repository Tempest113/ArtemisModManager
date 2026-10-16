using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Artemis.Core.Game;
using Artemis.Core.IO;
using Artemis.Core.Launch;
using Artemis.Core.Logging;
using Artemis.Core.Mods;
using Artemis.Core.Playsets;
using Artemis.Core.Plugins;
using Artemis.Core.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Artemis.App.ViewModels;

/// <summary>
/// The manager's half of the plugin features (D06, D16, D24): which mods carry a plugin, whether you approved it, per-playset switches and
/// settings, what Orion said last time, the notices, and creating or installing mods. Orion itself is not here: this writes the files it reads.
/// </summary>
public sealed partial class MainViewModel
{
    private TrustStore? _trustStore;
    private Dictionary<string, Dictionary<string, string>> _pluginHashes = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, PluginState> _pluginStates = new(StringComparer.OrdinalIgnoreCase);
    private PluginStatusReport? _lastStatus;

    private TrustStore Trust => _trustStore ??= LoadTrust();

    private TrustStore LoadTrust()
    {
        var store = new TrustStore(_paths);
        store.Load();
        return store;
    }

    public BulkCollection<PluginRowViewModel> PluginRows { get; } = [];
    public ObservableCollection<NoticeViewModel> Notices { get; } = [];

    [ObservableProperty] private PluginRowViewModel? _selectedPlugin;
    [ObservableProperty] private string _pluginsEmptyText = "";

    /// <summary>The master switch: off, no plugin loads whatever is approved.</summary>
    public bool PluginsEnabled
    {
        get => _settings.PluginsEnabled;
        set
        {
            if (_settings.PluginsEnabled == value) return;
            _settings.PluginsEnabled = value;
            SaveSettings();
            OnPropertyChanged();
            AppLog.Info(value ? "Plugins switched on" : "Plugins switched off");
            RefreshPluginStates();   // the trophies depend on it
        }
    }

    // ---- state of each plugin ----

    public PluginState PluginStateOf(string key) => _pluginStates.GetValueOrDefault(key, PluginState.Invalid);

    public bool IsPluginApproved(string key) => Trust.Get(key) is not null;

    public bool PluginFollows(string key) => Trust.Get(key)?.FollowUpdates == true;

    public string? PluginHash(string key, string file) => _pluginHashes.TryGetValue(key, out var h) && h.TryGetValue(file, out var v) ? v : null;

    public bool PluginOnHere(string key) => SelectedPlayset?.Mods.FirstOrDefault(m => string.Equals(m.Key, key, StringComparison.OrdinalIgnoreCase)) is { PluginOff: false };

    public IReadOnlyDictionary<string, JsonElement>? StoredPluginValues(string key) => SelectedPlayset?.PluginValues.GetValueOrDefault(key);

    /// <summary>What Orion said about this plugin the last time the game ran, in a sentence; empty when it said nothing.</summary>
    public string PluginLastRun(string key)
    {
        if (_lastStatus?.Plugins.FirstOrDefault(p => string.Equals(p.Mod, key, StringComparison.OrdinalIgnoreCase)) is not { } p) return "";
        var when = _lastStatus.StartedUtc is { } t ? $" ({t.ToLocalTime():d MMM HH:mm})" : "";
        // Orion's own sentence says more than the short reason does ("returned 3", which game build, which plugin it clashes with)
        return p.State == "loaded" ? Loc.T("plug.last_run_loaded", ("when", when))
            : p.Reason is null ? Loc.T("plug.last_run_state", ("state", p.State), ("when", when))
            : Loc.T("plug.last_run_state_reason", ("state", p.State), ("reason", ReasonText(p.Reason, p.Detail)), ("when", when));
    }

    private static string ReasonText(string reason, string? detail = null) => reason switch
    {
        "untrusted" => Loc.T("plug.reason_untrusted"),
        "hashChanged" => Loc.T("plug.reason_hash_changed"),
        "buildUnsupported" => Loc.T("plug.reason_build_unsupported"),
        "dependencyMissing" => Loc.T("plug.reason_dependency_missing"),
        "incompatible" => Loc.T("plug.reason_incompatible"),
        "fileMissing" => Loc.T("plug.reason_file_missing"),
        "apiUnsupported" => Loc.T("plug.reason_api_unsupported"),
        "disabled" => Loc.T("plug.reason_disabled"),
        "error" => string.IsNullOrWhiteSpace(detail) ? Loc.T("plug.reason_error") : detail,
        _ => reason,
    };

    private PluginState Evaluate(ModInfo mod)
    {
        if (mod.Plugin?.Manifest is not { } manifest) return PluginState.Invalid;
        if (!_pluginHashes.TryGetValue(mod.Key, out var hashes) || !manifest.Files.All(hashes.ContainsKey)) return PluginState.MissingFiles;
        if (manifest.Builds.Count > 0 && manifest.Builds.All(b => VersionMatcher.IsCompatible(b, GameVersion) == false)) return PluginState.WrongBuild;
        return Trust.StateOf(mod.Key, manifest, hashes) switch { PluginTrust.Approved => PluginState.Approved, PluginTrust.Changed => PluginState.Changed, _ => PluginState.NotApproved };
    }

    /// <summary>Hashes every plugin's files (off the window's thread) and rebuilds the Plugins page. After a scan, an install or an approval.</summary>
    private async Task RefreshPluginsAsync()
    {
        var mods = _installed.Values.Where(m => m.Plugin is not null).OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        _pluginHashes = await Task.Run(() => mods.Where(m => m.Plugin!.Manifest is not null && m.ContentDir is not null)
            .ToDictionary(m => m.Key, m => PluginHashes.Compute(m.ContentDir!, m.Plugin!.Manifest!), StringComparer.OrdinalIgnoreCase));
        var selected = SelectedPlugin?.Key;
        PluginRows.ReplaceAll(mods.Select(m => new PluginRowViewModel(this, m)));
        SelectedPlugin = PluginRows.FirstOrDefault(r => r.Key == selected) ?? PluginRows.FirstOrDefault();
        RefreshStatus();
    }

    /// <summary>Reads what Orion said last time and what is wrong in the game folder, and shows it. At start-up and whenever the window comes back to the front.</summary>
    public void RefreshStatus()
    {
        _lastStatus = _game is null ? null : OrionFiles.ReadStatus(_game.UserDir);
        RefreshPluginStates();
        RefreshNotices();
    }

    /// <summary>Works out every plugin's state again from what is known; cheap, so it runs on every playset change.</summary>
    private void RefreshPluginStates()
    {
        _pluginStates = _installed.Values.Where(m => m.Plugin is not null).ToDictionary(m => m.Key, Evaluate, StringComparer.OrdinalIgnoreCase);
        foreach (var row in PluginRows) row.Refresh();
        foreach (var row in _installedRows.Values) row.RefreshMarks();
        foreach (var row in LoadOrder) row.RefreshMarks();
        PluginsEmptyText = PluginRows.Count == 0 ? Loc.T("plug.plugins_empty") : "";
        RefreshOrion();
        if (Filter == InstalledFilter.WithPlugin) OnFilterChanged(Filter);
        RefreshDetails();
    }

    private bool PluginIsOff(string key) =>
        SelectedPlayset?.Mods.FirstOrDefault(m => string.Equals(m.Key, key, StringComparison.OrdinalIgnoreCase)) is { PluginOff: true };

    /// <summary>The small mark on a row: null for a mod with no plugin.</summary>
    public (string Text, bool Warn, string Tip)? PluginMarkFor(ModInfo mod)
    {
        if (mod.Plugin is null) return null;
        var state = PluginStateOf(mod.Key);
        if (PluginIsOff(mod.Key)) return (Loc.T("plug.mark_off"), false, Loc.T("plug.mark_off_tip"));
        var tip = state switch
        {
            PluginState.Approved => Loc.T("plug.mark_tip_approved"),
            PluginState.NotApproved => Loc.T("plug.mark_tip_not_approved"),
            PluginState.Changed => Loc.T("plug.mark_tip_changed"),
            PluginState.WrongBuild => Loc.T("plug.mark_tip_wrong_build"),
            PluginState.MissingFiles => Loc.T("plug.mark_tip_missing_files"),
            _ => Loc.T("plug.mark_tip_unusable", ("error", mod.Plugin.Error)),
        };
        return (Loc.T("plug.mark_plugin"), state != PluginState.Approved, tip);
    }

    public bool HasPlugin(string key) => _installed.TryGetValue(key, out var m) && m.Plugin is not null;

    // ---- approving, and what each choice changes ----

    /// <summary>Asks once, in plain words, whether this plugin may run code inside the game. Returns true if approved.</summary>
    public bool ApprovePlugin(ModInfo mod)
    {
        if (mod.Plugin?.Manifest is not { } manifest) return false;
        var hashes = mod.ContentDir is null ? [] : PluginHashes.Compute(mod.ContentDir, manifest);
        _pluginHashes[mod.Key] = hashes;
        if (!manifest.Files.All(hashes.ContainsKey))
        {
            ShowError(Loc.T("plug.approve_file_missing", ("name", mod.Name)));
            return false;
        }
        var files = string.Join("\n", manifest.Files.Select(f => $"   {f}   SHA-256 {hashes[f][..12]}…"));
        var changed = Trust.Get(mod.Key) is not null ? Loc.T("plug.approve_changed") : "";
        if (!_dialogs.Confirm(Loc.T("plug.approve_prompt", ("name", mod.Name), ("changed", changed), ("files", files)), Loc.T("plug.approve"))) return false;
        try { Trust.Approve(mod.Key, mod.Name, hashes, autoApprove: mod.Source == ModSource.Local); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ShowError(ex.Message, ex); return false; }
        AppLog.Info($"Approved the plugin of \"{mod.Name}\" ({string.Join(", ", manifest.Files)})");
        RefreshPluginStates();
        return true;
    }

    /// <summary>After mods are added or switched on: asks about each plugin that is not approved yet, one at a time. Later, so it never runs inside a click's own update.</summary>
    private void AskAboutNewPlugins(IEnumerable<ModInfo?> mods)
    {
        var due = mods.OfType<ModInfo>().Where(m => m.Plugin?.IsValid == true && PluginStateOf(m.Key) is PluginState.NotApproved or PluginState.Changed).DistinctBy(m => m.Key).ToList();
        if (due.Count == 0) return;
        Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            foreach (var mod in due.Where(m => PluginOnHere(m.Key) && IsInPlayset(m.Key))) ApprovePlugin(mod);
        }));
    }

    [RelayCommand]
    private void ApproveSelectedPlugin()
    {
        if (SelectedPlugin is { } p) ApprovePlugin(p.Mod);
    }

    [RelayCommand]
    private void RevokeSelectedPlugin()
    {
        if (SelectedPlugin is not { } p || !_dialogs.Confirm(Loc.T("plug.revoke_prompt", ("name", p.Name)), Loc.T("plug.take_back"))) return;
        Trust.Revoke(p.Key);
        AppLog.Info($"Took back the approval of the plugin of \"{p.Name}\"");
        RefreshPluginStates();
    }

    [RelayCommand]
    private void OpenSelectedPluginFolder()
    {
        if (SelectedPlugin?.Mod is { } mod) OpenFolderOf(mod);
    }

    public void SetPluginFollows(string key, bool follow)
    {
        Try(() => Trust.SetFollowUpdates(key, follow));
        RefreshPluginStates();
    }

    public void SetPluginOnHere(string key, bool on)
    {
        if (SelectedPlayset is not { } p || p.Mods.FirstOrDefault(m => string.Equals(m.Key, key, StringComparison.OrdinalIgnoreCase)) is not { } entry || entry.PluginOff == !on) return;
        entry.PluginOff = !on;
        Try(() => _store.Save(p));
        RefreshPluginStates();
        if (on && _installed.GetValueOrDefault(key) is { } mod) AskAboutNewPlugins([mod]);
    }

    /// <summary>Remembers a setting in the current playset. A value equal to the default is kept too, so a later change of the default doesn't move it.</summary>
    public void SetPluginValue(string key, string id, object value)
    {
        if (SelectedPlayset is not { } p) return;
        if (!p.PluginValues.TryGetValue(key, out var values)) p.PluginValues[key] = values = [];
        values[id] = JsonSerializer.SerializeToElement(value);
        Try(() => _store.Save(p));
    }

    [RelayCommand]
    private void ResetPluginSettings()
    {
        if (SelectedPlugin is not { } row || SelectedPlayset is not { } p) return;
        p.PluginValues.Remove(row.Key);
        Try(() => _store.Save(p));
        row.Refresh();
    }

    // ---- what Orion reads, written before the game starts ----

    /// <summary>Brings the approved hashes of local and "follow updates" mods up to date, then writes the plugin settings for the playset about to be played.</summary>
    private void ExportPluginFiles(Playset playset)
    {
        var plugins = _installed.Values.Where(m => m.Plugin?.Manifest is not null && m.ContentDir is not null).ToList();
        foreach (var mod in plugins) _pluginHashes[mod.Key] = PluginHashes.Compute(mod.ContentDir!, mod.Plugin!.Manifest!); // fresh: a plugin may have been rebuilt meanwhile
        var moved = Trust.RefreshFollowed(plugins.Select(m => (m.Key, m.Plugin!.Manifest!, (IReadOnlyDictionary<string, string>)_pluginHashes[m.Key])));
        foreach (var key in moved) AppLog.Info($"The approved files of \"{key}\" were brought up to date (local or follow-updates mod)");
        PluginSettingsExport.Write(_paths, playset, _installed, _settings.PluginsEnabled);
        RefreshPluginStates();
    }

    // ---- notices: things worth knowing when the window opens or comes back ----

    private void DismissNotice(NoticeViewModel notice)
    {
        Notices.Remove(notice);
        if (notice.Id.Length > 0 && !_settings.DismissedNotices.Contains(notice.Id))
        {
            _settings.DismissedNotices.Add(notice.Id);
            SaveSettings();
        }
    }

    /// <summary>Builds the list again: a foreign winmm.dll in the game folder, a plugin the game died loading, plugins that didn't load last time.</summary>
    public void RefreshNotices()
    {
        if (_game is null) return;
        var wanted = new List<NoticeViewModel>();

        if (StubCheck.Find(_game.GameDir) is { } stub && !_settings.DismissedNotices.Contains(stub.NoticeId))
            wanted.Add(new NoticeViewModel(stub.NoticeId,
                Loc.T("plug.notice_stub"),
                Loc.T("plug.show_folder"), () => Open(_game.GameDir), DismissNotice));

        if (!GameRunning && OrionFiles.ReadMarker(_game.UserDir) is { } marker)
        {
            var name = _installed.GetValueOrDefault(marker.Mod)?.Name ?? marker.Mod;
            wanted.Add(new NoticeViewModel("", Loc.T("plug.notice_marker", ("name", name)), Loc.T("plug.switch_plugin_off"),
                () =>
                {
                    SetPluginOnHere(marker.Mod, false);
                    OrionFiles.ClearMarker(_game.UserDir);
                    RefreshNotices();
                }, n => { OrionFiles.ClearMarker(_game.UserDir); Notices.Remove(n); }));
        }

        if (_lastStatus is { StartedUtc: { } started } status && status.Plugins.Where(p => p.IsProblem).ToList() is { Count: > 0 } problems)
        {
            var id = $"status|{started.Ticks}";
            if (!_settings.DismissedNotices.Contains(id))
                wanted.Add(new NoticeViewModel(id,
                    Loc.T(problems.Count == 1 ? "plug.notice_failed_one" : "plug.notice_failed_many", ("count", problems.Count),
                        ("list", string.Join("; ", problems.Take(3).Select(p => $"{_installed.GetValueOrDefault(p.Mod)?.Name ?? p.Mod} ({(p.Reason is null ? p.State : ReasonText(p.Reason, p.Detail))})"))),
                        ("end", problems.Count > 3 ? "…" : ".")),
                    Loc.T("plug.open_plugins"), () => Page = AppPage.Plugins, DismissNotice));
        }

        if (_lastStatus is { StartedUtc: { } at, Honest: { State: "failed" } honest } && !_settings.DismissedNotices.Contains($"honest|{at.Ticks}"))
            wanted.Add(new NoticeViewModel($"honest|{at.Ticks}",
                Loc.T("plug.notice_honest_failed", ("detail", honest.Detail)),
                Loc.T("plug.open_plugins"), () => Page = AppPage.Plugins, DismissNotice));

        if (LeftoverNotice() is { } leftover) wanted.Add(leftover);

        // keep the ones already shown (same id and text) so they don't flicker, replace the rest
        for (var i = Notices.Count - 1; i >= 0; i--) if (!wanted.Any(w => w.Id == Notices[i].Id && w.Text == Notices[i].Text)) Notices.RemoveAt(i);
        foreach (var w in wanted) if (!Notices.Any(n => n.Id == w.Id && n.Text == w.Text)) Notices.Add(w);
    }

    // ---- making and installing mods ----

    [RelayCommand]
    private async Task CreateMod()
    {
        if (_game is null) return;
        var answer = _dialogs.PromptWithOption(Loc.T("plug.create_mod_title"), Loc.T("plug.create_mod_default_name"), Loc.T("plug.create_mod_option"));
        if (answer is not { } a) return;
        try
        {
            var made = ModInstaller.Create(_game.UserDir, a.Name, GameVersion, a.Option);
            AppLog.Info($"Created the mod \"{made.Name}\" in {made.Folder}");
            await AfterModsChangedAsync(made.Key);
            Notify(Loc.T("plug.mod_created", ("name", made.Name)));
        }
        catch (Exception ex) when (ex is ArtemisException or IOException or UnauthorizedAccessException) { ShowError(ex.Message, ex); }
    }

    [RelayCommand]
    private async Task InstallFromArchive()
    {
        if (_game is null || _dialogs.PickOpenFile(Loc.T("plug.install_archive_title"), Loc.T("plug.install_archive_filter")) is not { } file) return;
        try
        {
            var seen = ModInstaller.Inspect(file, _game.UserDir);
            var text = Loc.T("plug.install_prompt", ("name", seen.Name), ("files", seen.Files.ToString("N0")));
            if (seen.Plugin is { IsValid: true } plugin)
                text += Loc.T("plug.install_has_plugin", ("files", string.Join(", ", plugin.Manifest!.Files)));
            else if (seen.Dlls.Count > 0)
                text += Loc.T("plug.install_has_dlls", ("dlls", string.Join(", ", seen.Dlls.Take(3))));
            if (seen.AlreadyInstalled) text += Loc.T("plug.install_replaces");
            if (!_dialogs.Confirm(text, Loc.T(seen.AlreadyInstalled ? "plug.replace" : "plug.install"))) return;

            var done = await Task.Run(() => ModInstaller.Install(file, _game.UserDir, replace: seen.AlreadyInstalled));
            AppLog.Info($"Installed \"{done.Name}\" from {Path.GetFileName(file)} ({done.Files:N0} files)");
            await AfterModsChangedAsync(done.Key);
            Notify(Loc.T("plug.mod_installed", ("name", done.Name)));
        }
        catch (Exception ex) when (ex is ArtemisException or IOException or UnauthorizedAccessException or InvalidDataException) { ShowError(ex.Message, ex); }
    }

    /// <summary>A mod was made or installed: scan again, show it, and index it.</summary>
    private async Task AfterModsChangedAsync(string key)
    {
        await ScanAsync();
        ShowPlayset();
        await RefreshPluginsAsync();
        _ = UpdateIndexAsync();
        if (_installedRows.TryGetValue(key, out var row))
        {
            InstalledSearch = "";
            FollowSelection(row);
            DetailsOpen = true;
        }
    }

    [RelayCommand]
    private void LaunchWithoutPlugins() => _ = LaunchGameAsync(resume: false, withoutPlugins: true);
}
