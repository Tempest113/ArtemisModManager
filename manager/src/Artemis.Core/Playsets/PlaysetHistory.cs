namespace Artemis.Core.Playsets;

/// <summary>Undo and redo for one playset's load order. It remembers each state it is told about; it never watches the playset.</summary>
public sealed class PlaysetHistory
{
    private const int Limit = 100;

    private readonly record struct Line(string Key, bool Enabled, string? Separator);

    private readonly List<Line[]> _undo = [];
    private readonly List<Line[]> _redo = [];
    private Line[] _current = [];

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>Starts over from the playset as it is now (first load, switching playsets).</summary>
    public void Reset(Playset playset)
    {
        _undo.Clear();
        _redo.Clear();
        _current = Snapshot(playset);
    }

    /// <summary>Call after the playset changed. A change made after an undo discards what could have been redone.</summary>
    public void Record(Playset playset)
    {
        var now = Snapshot(playset);
        if (now.SequenceEqual(_current)) return;
        _undo.Add(_current);
        if (_undo.Count > Limit) _undo.RemoveAt(0);
        _redo.Clear();
        _current = now;
    }

    public bool Undo(Playset playset) => Step(playset, _undo, _redo);

    public bool Redo(Playset playset) => Step(playset, _redo, _undo);

    private bool Step(Playset playset, List<Line[]> from, List<Line[]> to)
    {
        if (from.Count == 0) return false;
        to.Add(_current);
        _current = from[^1];
        from.RemoveAt(from.Count - 1);
        playset.Mods.Clear();
        playset.Mods.AddRange(_current.Select(l => new PlaysetMod { Key = l.Key, Enabled = l.Enabled, Separator = l.Separator }));
        return true;
    }

    private static Line[] Snapshot(Playset playset) =>
        playset.Mods.Select(m => new Line(m.Key, m.Enabled, m.Separator)).ToArray();
}
