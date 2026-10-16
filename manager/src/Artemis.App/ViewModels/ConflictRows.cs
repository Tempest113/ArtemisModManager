using Artemis.Core.Conflicts;
using Artemis.Core.Text;

namespace Artemis.App.ViewModels;

/// <summary>One side of a conflict, as the details pane lists it.</summary>
public sealed class ConflictEntryViewModel(ConflictEntry entry, bool winner)
{
    public string Name => entry.SourceName;
    public bool IsWinner => winner;
    public bool IsGame => entry.IsGame;
    public string State => winner ? Loc.T("vm.conflict_used") : Loc.T("vm.conflict_overridden");
    public string Location => entry.Line > 0 ? $"{entry.Path}:{entry.Line}" : entry.Path;
}

/// <summary>A conflict in the list: what it is, and in one line who wins.</summary>
public sealed class ConflictRowViewModel(Conflict conflict)
{
    public Conflict Conflict => conflict;
    public string Name => conflict.Name;
    public string Domain => conflict.Domain;
    public string KindText => conflict.Kind switch { ConflictKind.File => Loc.T("vm.kind_file"), ConflictKind.Definition => Loc.T("vm.kind_definition"), _ => Loc.T("vm.kind_text") };
    public string Why => conflict.Why;

    public string WhyHeading => conflict.Kind switch
    {
        ConflictKind.File => Loc.T("vm.why_file"),
        ConflictKind.Definition => Loc.T("vm.why_definition"),
        _ => Loc.T("vm.why_text"),
    };
    public bool Intended => conflict.Intended;

    public IReadOnlyList<ConflictEntryViewModel> Entries { get; } =
        conflict.Entries.Select((e, i) => new ConflictEntryViewModel(e, i == conflict.Winner)).ToList();

    /// <summary>"common/technology · Mod B over Mod A": the folder, then the winner and who it beats.</summary>
    public string Summary
    {
        get
        {
            var beaten = conflict.Entries.Where((_, i) => i != conflict.Winner).Select(e => e.SourceName).ToList();
            var names = beaten.Count <= 2 ? string.Join(Loc.T("vm.conflict_names_and"), beaten) : Loc.T("vm.conflict_names_more", ("first", beaten[0]), ("second", beaten[1]), ("count", beaten.Count - 2));
            return Loc.T("vm.conflict_summary", ("domain", conflict.Domain), ("winner", conflict.WinnerEntry.SourceName), ("names", names));
        }
    }

    public bool Matches(string search) =>
        conflict.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
        || conflict.Domain.Contains(search, StringComparison.OrdinalIgnoreCase)
        || conflict.Entries.Any(e => e.SourceName.Contains(search, StringComparison.OrdinalIgnoreCase) || e.Path.Contains(search, StringComparison.OrdinalIgnoreCase));
}
