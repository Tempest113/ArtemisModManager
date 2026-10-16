using System.Windows.Media;
using Artemis.App.Services;
using Artemis.Core.Achievements;
using Artemis.Core.Game;
using Artemis.Core.Mods;
using Artemis.Core.Playsets;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Artemis.App.ViewModels;

/// <summary>What both lists show about a mod: name, picture, version, note, and whether it is new or updated.</summary>
public abstract class ModRowBase(MainViewModel owner, ModInfo? mod, string key, string gameVersion) : ObservableObject
{
    private ImageSource? _thumbnail;
    private bool _thumbnailRequested;

    protected MainViewModel Owner => owner;
    public ModInfo? Mod => mod;
    public DateTime Modified => mod?.Modified ?? default;
    public string Key => key;
    public virtual string Name => mod?.Name ?? key;
    public string Initials => Placeholder.Initials(Name);
    public Brush PlaceholderBrush => Placeholder.For(Name);
    public string SourceName => mod is null ? "Missing" : mod.Source == ModSource.Workshop ? "Workshop" : "Local";

    public string VersionText => mod?.SupportedVersion ?? "";
    public bool IsOutdated => mod is not null && VersionMatcher.IsCompatible(mod.SupportedVersion, gameVersion) == false;
    public string VersionTip => IsOutdated ? $"Made for {mod!.SupportedVersion}. The game is {gameVersion}." : VersionText;

    public bool HasPlugin => mod?.Plugin is not null;
    public bool HasConflicts => mod is not null && owner.HasConflicts(mod.Key);
    private (string Text, bool Warn, string Tip)? PluginMark => mod is null ? null : owner.PluginMarkFor(mod);
    public string PluginText => PluginMark?.Text ?? "";
    public bool PluginWarn => PluginMark?.Warn == true;
    public string PluginTip => PluginMark?.Tip ?? "";

    // Achievements: a trophy when the mod leaves them on, a broken one when it turns them off (files the checksum covers, or a plugin that counts), nothing until it has been looked at.
    private AchievementVerdict Achievement => mod is null ? new AchievementVerdict(AchievementState.Unknown, "") : owner.AchievementFor(mod);
    public bool AchievementOk => Achievement.State == AchievementState.On;
    public bool AchievementBroken => Achievement.State == AchievementState.Off;
    public string AchievementTip => Achievement.Tip;

    public void RefreshAchievement()
    {
        OnPropertyChanged(nameof(AchievementOk));
        OnPropertyChanged(nameof(AchievementBroken));
        OnPropertyChanged(nameof(AchievementTip));
    }

    public string? Note => mod is null ? null : owner.NoteFor(mod.Key);
    public bool HasNote => Note is not null;
    public ModChange Change => mod is null ? ModChange.None : owner.ChangeFor(mod);
    public string ChangeText => Change switch { ModChange.New => "New", ModChange.Updated => "Updated", _ => "" };

    public void RefreshMarks()
    {
        OnPropertyChanged(nameof(PluginText));
        OnPropertyChanged(nameof(PluginWarn));
        OnPropertyChanged(nameof(PluginTip));
        OnPropertyChanged(nameof(Note));
        OnPropertyChanged(nameof(HasNote));
        OnPropertyChanged(nameof(Change));
        OnPropertyChanged(nameof(ChangeText));
        RefreshAchievement();
    }

    public virtual bool HasProblem => mod is null || mod.ContentMissing;
    public virtual string ProblemText => mod is null ? "This mod isn't installed." : "The mod's files are missing.";

    /// <summary>Loaded on first use, which for a virtualized list means when the row first scrolls into view.</summary>
    public ImageSource? Thumbnail
    {
        get
        {
            if (!_thumbnailRequested)
            {
                _thumbnailRequested = true;
                _ = LoadThumbnailAsync();
            }
            return _thumbnail;
        }
    }

    private async Task LoadThumbnailAsync()
    {
        if (mod is null || await Task.Run(() => Thumbnails.PathFor(mod)) is not { } path) return;
        _thumbnail = await Thumbnails.LoadAsync(path);
        OnPropertyChanged(nameof(Thumbnail));
    }
}

/// <summary>A row in the Installed pane. The add button puts the mod in, or takes it out of, the active playset.</summary>
public sealed class InstalledRowViewModel(MainViewModel owner, ModInfo mod, string gameVersion) : ModRowBase(owner, mod, mod.Key, gameVersion)
{
    public bool InPlayset
    {
        get => Owner.IsInPlayset(Key);
        set
        {
            Owner.SetInPlayset(Mod!, value);
            OnPropertyChanged();
        }
    }

    public void Refresh() => OnPropertyChanged(nameof(InPlayset));
}

/// <summary>Where a dragged group will land relative to a row, shown as a line.</summary>
public enum DropMark { None, Above, Below }

/// <summary>A row in the Load order pane: a mod, or a separator (<see cref="IsSeparator"/>).</summary>
public sealed class LoadOrderRowViewModel(MainViewModel owner, PlaysetMod entry, ModInfo? mod, string gameVersion)
    : ModRowBase(owner, mod, entry.Key, gameVersion)
{
    private int _index;
    private bool _isMatch;
    private DropMark _drop;

    public PlaysetMod Entry => entry;

    private string? _dependencyProblem;

    /// <summary>What's wrong with this mod's dependencies in the current playset, if anything.</summary>
    public string? DependencyProblem
    {
        get => _dependencyProblem;
        set
        {
            if (!SetProperty(ref _dependencyProblem, value)) return;
            OnPropertyChanged(nameof(HasProblem));
            OnPropertyChanged(nameof(ProblemText));
        }
    }

    public override bool HasProblem => base.HasProblem || _dependencyProblem is not null;
    public override string ProblemText => base.HasProblem ? base.ProblemText : _dependencyProblem ?? "";
    public bool IsSeparator => entry.IsSeparator;
    public override string Name => entry.Separator ?? base.Name;

    public void RenameSeparator(string name)
    {
        entry.Separator = name;
        OnPropertyChanged(nameof(Name));
    }

    public int Index
    {
        get => _index;
        set
        {
            if (SetProperty(ref _index, value)) OnPropertyChanged(nameof(IndexText));
        }
    }

    /// <summary>The position as typed text: edit it and press Enter to move the mod there.</summary>
    public string IndexText
    {
        get => _index.ToString();
        set
        {
            if (int.TryParse(value, out var position)) Owner.MoveTo(this, position);
            OnPropertyChanged();
        }
    }

    public DropMark Drop
    {
        get => _drop;
        set => SetProperty(ref _drop, value);
    }

    public bool IsMatch
    {
        get => _isMatch;
        set => SetProperty(ref _isMatch, value);
    }

    private bool _isHidden;
    private int _groupSize;

    /// <summary>The row is in a group whose separator is collapsed, so the list doesn't show it.</summary>
    public bool IsHidden
    {
        get => _isHidden;
        set => SetProperty(ref _isHidden, value);
    }

    /// <summary>At a separator: how many mods are in its group.</summary>
    public int GroupSize
    {
        get => _groupSize;
        set => SetProperty(ref _groupSize, value);
    }

    /// <summary>At a separator: whether its group is folded away. Kept in the playset.</summary>
    public bool IsCollapsed
    {
        get => entry.Collapsed;
        set
        {
            if (entry.Collapsed == value) return;
            entry.Collapsed = value;
            OnPropertyChanged();
        }
    }

    public bool Enabled
    {
        get => entry.Enabled;
        set
        {
            if (entry.Enabled == value) return;
            SetEnabled(value);
            Owner.PlaysetChanged();
            if (value) Owner.PluginMaybeOn(Mod);
        }
    }

    /// <summary>Changes the switch without saving, for callers that change several rows and save once.</summary>
    public void SetEnabled(bool value)
    {
        entry.Enabled = value;
        OnPropertyChanged(nameof(Enabled));
    }
}
