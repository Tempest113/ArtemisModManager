using Artemis.Core.Conflicts;

namespace Artemis.App.ViewModels;

/// <summary>One side of a conflict, as the details pane lists it.</summary>
public sealed class ConflictEntryViewModel(ConflictEntry entry, bool winner)
{
    public string Name => entry.SourceName;
    public bool IsWinner => winner;
    public bool IsGame => entry.IsGame;
    public string State => winner ? "Used" : "Overridden";
    public string Location => entry.Line > 0 ? $"{entry.Path}:{entry.Line}" : entry.Path;
}

/// <summary>A conflict in the list: what it is, and in one line who wins.</summary>
public sealed class ConflictRowViewModel(Conflict conflict)
{
    public Conflict Conflict => conflict;
    public string Name => conflict.Name;
    public string Domain => conflict.Domain;
    public string KindText => conflict.Kind switch { ConflictKind.File => "File", ConflictKind.Definition => "Definition", _ => "Text" };
    public string Why => conflict.Why;

    public string WhyHeading => conflict.Kind switch
    {
        ConflictKind.File => "Why this file takes priority",
        ConflictKind.Definition => "Why this definition takes priority",
        _ => "Why this text takes priority",
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
            var names = beaten.Count <= 2 ? string.Join(" and ", beaten) : $"{beaten[0]}, {beaten[1]} and {beaten.Count - 2} more";
            return $"{conflict.Domain} · {conflict.WinnerEntry.SourceName} over {names}";
        }
    }

    public bool Matches(string search) =>
        conflict.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
        || conflict.Domain.Contains(search, StringComparison.OrdinalIgnoreCase)
        || conflict.Entries.Any(e => e.SourceName.Contains(search, StringComparison.OrdinalIgnoreCase) || e.Path.Contains(search, StringComparison.OrdinalIgnoreCase));
}
