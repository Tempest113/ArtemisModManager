namespace Artemis.Core.Mods;

/// <summary>One mod a descriptor lists, and where it stands relative to this mod.</summary>
public sealed record DependencyStatus(string Name, string Text, bool Ok);

public static class DependencyCheck
{
    /// <summary>
    /// The mods this mod's descriptor lists under <c>dependencies</c>. That list only asks the game to load those mods
    /// <em>before</em> this one; it does not mean this mod needs them to work (and Workshop "required items" are no more
    /// reliable). So the only thing worth flagging is the order: a listed mod that is switched on and loads after this one.
    /// A listed mod that isn't installed, isn't in the playset or is switched off is reported but is never a problem.
    /// <paramref name="position"/> is this mod's place in the load order, or -1 when it isn't in the playset.
    /// </summary>
    public static List<DependencyStatus> For(
        ModInfo mod,
        IReadOnlyDictionary<string, ModInfo> installedByName,
        IReadOnlyDictionary<string, (int Index, bool Enabled)> inPlayset,
        int position)
    {
        var result = new List<DependencyStatus>();
        foreach (var name in mod.Dependencies)
        {
            if (!installedByName.TryGetValue(name, out var listed))
                result.Add(new DependencyStatus(name, "Not installed", Ok: true));
            else if (!inPlayset.TryGetValue(listed.Key, out var slot))
                result.Add(new DependencyStatus(name, "Not in this playset", Ok: true));
            else if (!slot.Enabled)
                result.Add(new DependencyStatus(name, "Switched off", Ok: true));
            else if (position >= 0 && slot.Index > position)
                result.Add(new DependencyStatus(name, "Loads below this mod; the descriptor asks for it above", Ok: false));
            else
                result.Add(new DependencyStatus(name, "In this playset", Ok: true));
        }
        return result;
    }
}
