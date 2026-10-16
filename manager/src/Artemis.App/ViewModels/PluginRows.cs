using System.Collections.ObjectModel;
using System.Globalization;
using Artemis.Core.Mods;
using Artemis.Core.Plugins;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Artemis.App.ViewModels;

public enum PluginState { Invalid, MissingFiles, WrongBuild, NotApproved, Changed, Approved }

/// <summary>One choice of an enum setting, shown as a button that is on when it is the chosen one.</summary>
public sealed class OptionViewModel(SettingViewModel setting, PluginOption option) : ObservableObject
{
    public string Name => option.Name;

    public bool IsSelected
    {
        get => setting.TextValue == option.Value;
        set { if (value) setting.TextValue = option.Value; }
    }

    public void Refresh() => OnPropertyChanged(nameof(IsSelected));
}

/// <summary>
/// One setting (or feature switch) of a plugin as a control: a switch, a box for a number or text, or a row of choices. A value that
/// doesn't fit (not a number, outside the range) is not saved and says why; the playset keeps the last good value.
/// </summary>
public sealed partial class SettingViewModel : ObservableObject
{
    private readonly PluginRowViewModel _row;
    private readonly PluginSetting _setting;
    private bool _bool;
    private string _text = "";
    private string _error = "";

    public SettingViewModel(PluginRowViewModel row, PluginSetting setting, System.Text.Json.JsonElement? stored)
    {
        (_row, _setting) = (row, setting);
        var value = PluginValue.For(setting, stored);
        if (value is bool b) _bool = b;
        else _text = Format(value);
        Options = setting.Options.Select(o => new OptionViewModel(this, o)).ToList();
    }

    public string Name => _setting.Name;
    public string Description => _setting.Description;
    public bool HasDescription => _setting.Description.Length > 0;
    public bool IsBool => _setting.Type == SettingType.Bool;
    public bool IsEnum => _setting.Type == SettingType.Enum;
    public bool IsText => !IsBool && !IsEnum;
    public IReadOnlyList<OptionViewModel> Options { get; }

    /// <summary>What a number may be, as a hint under the box; empty when anything goes.</summary>
    public string Hint => _setting.Type is SettingType.Int or SettingType.Float && (_setting.Min is not null || _setting.Max is not null)
        ? $"{(_setting.Type == SettingType.Int ? "A whole number" : "A number")} {(_setting.Min is { } lo ? $"from {Format(lo)}" : "up")}{(_setting.Max is { } hi ? $" to {Format(hi)}" : "")}"
        : "";

    public bool HasHint => Hint.Length > 0;

    public bool BoolValue
    {
        get => _bool;
        set
        {
            if (!SetProperty(ref _bool, value)) return;
            _row.Owner.SetPluginValue(_row.Key, _setting.Id, value);
        }
    }

    public string TextValue
    {
        get => _text;
        set
        {
            if (_text == value) return;
            if (!TryParse(value, out var parsed, out var problem))
            {
                Error = problem;
                OnPropertyChanged();   // the box shows what was typed until it is fixed or the page is left
                return;
            }
            Error = "";
            SetProperty(ref _text, value);
            foreach (var option in Options) option.Refresh();
            _row.Owner.SetPluginValue(_row.Key, _setting.Id, parsed);
        }
    }

    public string Error
    {
        get => _error;
        private set
        {
            if (SetProperty(ref _error, value)) OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => _error.Length > 0;

    [RelayCommand]
    private void Reset()
    {
        Error = "";
        if (IsBool) BoolValue = (bool)_setting.Default;
        else TextValue = Format(_setting.Default);
    }

    private bool TryParse(string text, out object value, out string problem)
    {
        problem = "";
        value = text;
        switch (_setting.Type)
        {
            case SettingType.Int:
                if (!long.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) { problem = $"Enter a whole number. {Hint}".Trim(); return false; }
                if (l < _setting.Min || l > _setting.Max) { problem = $"Out of range. {Hint}".Trim(); return false; }
                value = l;
                return true;
            case SettingType.Float:
                if (!double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) || !double.IsFinite(d)) { problem = $"Enter a number, with a dot for decimals. {Hint}".Trim(); return false; }
                if (d < _setting.Min || d > _setting.Max) { problem = $"Out of range. {Hint}".Trim(); return false; }
                value = d;
                return true;
            default:
                return true;
        }
    }

    private static string Format(object value) => value switch
    {
        double d => d.ToString("0.###############", CultureInfo.InvariantCulture),
        long l => l.ToString(CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };
}

/// <summary>A mod's plugin on the Plugins page: its state, what to do about it, and its settings for the current playset.</summary>
public sealed partial class PluginRowViewModel : ObservableObject
{
    public PluginRowViewModel(MainViewModel owner, ModInfo mod)
    {
        (Owner, Mod) = (owner, mod);
        Settings = [];
        Refresh();
    }

    public MainViewModel Owner { get; }
    public ModInfo Mod { get; }
    public string Key => Mod.Key;
    public PluginManifest? Manifest => Mod.Plugin?.Manifest;
    public string Name => Manifest?.Name ?? Mod.Name;
    public ObservableCollection<SettingViewModel> Settings { get; }

    public string Subtitle => Manifest is { } m ? $"{m.Id} · version {m.Version} · {(m.Tier == PluginTier.Stable ? "stable" : "fragile")}" : Mod.Name;

    public string TierText => Manifest is not { } m ? "" : m.Tier == PluginTier.Stable
        ? "Stable: adds script keywords or text commands, which survive game patches best."
        : "Fragile: hooks specific parts of the game, so it is checked again after every game patch.";

    public PluginState State => Owner.PluginStateOf(Mod.Key);

    public string StateText => State switch
    {
        PluginState.Approved => "Approved",
        PluginState.NotApproved => "Needs approval",
        PluginState.Changed => "Changed since approved",
        PluginState.WrongBuild => "Not for this game version",
        PluginState.MissingFiles => "File missing",
        _ => "Can't be used",
    };

    public string StateDetail => State switch
    {
        PluginState.Approved => "Its files are exactly the ones you approved.",
        PluginState.NotApproved => "Until you approve it, the game starts without this plugin. Approving lets it run code inside the game.",
        PluginState.Changed => "A file changed after you approved it, so the plugin is skipped until you approve it again.",
        PluginState.WrongBuild => $"It says it supports {string.Join(", ", Manifest?.Builds ?? [])}; the game is {Owner.GameVersion}. It is skipped.",
        PluginState.MissingFiles => "A file its modloader.json lists is not in the mod's folder.",
        _ => Mod.Plugin?.Error ?? "",
    };

    public bool HasManifest => Manifest is not null;
    public bool IsWarn => State != PluginState.Approved;
    public bool NeedsApproval => State is PluginState.NotApproved or PluginState.Changed;
    public bool IsApproved => Owner.IsPluginApproved(Mod.Key);
    public bool InPlayset => Owner.IsInPlayset(Mod.Key);
    public bool CanSetup => InPlayset && Manifest is not null;

    public bool FollowUpdates
    {
        get => Owner.PluginFollows(Mod.Key);
        set => Owner.SetPluginFollows(Mod.Key, value);
    }

    /// <summary>Whether this plugin runs in the current playset (the mod's scripts load either way).</summary>
    public bool OnHere
    {
        get => Owner.PluginOnHere(Mod.Key);
        set => Owner.SetPluginOnHere(Mod.Key, value);
    }

    public string Files => Manifest is null ? "" : string.Join("\n", Manifest.Files.Select(f => Owner.PluginHash(Mod.Key, f) is { } h ? $"{f}   SHA-256 {h[..16]}…" : $"{f}   (missing)"));

    public string LastRun => Owner.PluginLastRun(Mod.Key);
    public bool HasLastRun => LastRun.Length > 0;
    public bool HasSettings => Settings.Count > 0;

    public string SettingsNote => Manifest is null || Manifest.Settings.Count + Manifest.Features.Count == 0 ? "This plugin has no settings."
        : !InPlayset ? "Add the mod to the playset to change its settings; they are kept per playset."
        : "";

    public bool HasSettingsNote => SettingsNote.Length > 0;

    public void Refresh()
    {
        Settings.Clear();
        if (Manifest is { } m && InPlayset)
        {
            var stored = Owner.StoredPluginValues(Mod.Key);
            System.Text.Json.JsonElement? Get(string id) => stored is not null && stored.TryGetValue(id, out var v) ? v : null;
            foreach (var f in m.Features) Settings.Add(new SettingViewModel(this, new PluginSetting(f.Id, SettingType.Bool, f.Name, f.Description, f.Default, null, null, []), Get(f.Id)));
            foreach (var s in m.Settings) Settings.Add(new SettingViewModel(this, s, Get(s.Id)));
        }
        foreach (var p in new[]
        {
            nameof(State), nameof(StateText), nameof(StateDetail), nameof(IsWarn), nameof(NeedsApproval), nameof(IsApproved), nameof(InPlayset), nameof(CanSetup),
            nameof(FollowUpdates), nameof(OnHere), nameof(Files), nameof(LastRun), nameof(HasLastRun), nameof(HasSettings), nameof(SettingsNote), nameof(HasSettingsNote),
        }) OnPropertyChanged(p);
    }
}

/// <summary>A line of the notices above the lists: something to know, and maybe one thing to do about it. Dismissing is up to the owner of the list.</summary>
public sealed class NoticeViewModel
{
    public NoticeViewModel(string id, string text, string? actionText, Action? action, Action<NoticeViewModel> dismiss)
    {
        (Id, Text, ActionText) = (id, text, actionText ?? "");
        HasAction = actionText is not null && action is not null;
        ActionCommand = new RelayCommand(() => action?.Invoke());
        DismissCommand = new RelayCommand(() => dismiss(this));
    }

    public string Id { get; }
    public string Text { get; }
    public string ActionText { get; }
    public bool HasAction { get; }
    public IRelayCommand ActionCommand { get; }
    public IRelayCommand DismissCommand { get; }
}
