using System.Text.Json;
using System.Text.Json.Serialization;

namespace Artemis.Core.Playsets;

/// <summary>One line of the load order: a mod, or a separator (a named divider that only labels the mods below it).</summary>
public sealed class PlaysetMod
{
    public string Key { get; set; } = "";
    public bool Enabled { get; set; } = true;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Separator { get; set; }

    /// <summary>The mod's plugin part is switched off in this playset (the mod's scripts still load).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool PluginOff { get; set; }

    [JsonIgnore]
    public bool IsSeparator => Separator is not null;
}

public sealed class Playset
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New playset";
    public List<PlaysetMod> Mods { get; set; } = [];

    /// <summary>Plugin settings and feature switches chosen in this playset: mod key, then setting or feature id, then the value.</summary>
    public Dictionary<string, Dictionary<string, JsonElement>> PluginValues { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonIgnore]
    public IEnumerable<PlaysetMod> EnabledMods => Mods.Where(m => m.Enabled && !m.IsSeparator);

    public bool Contains(string key) => Mods.Any(m => string.Equals(m.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>Appends the mod at the end of the load order. Returns false if it was already there.</summary>
    public bool Add(string key)
    {
        if (Contains(key)) return false;
        Mods.Add(new PlaysetMod { Key = key });
        return true;
    }

    public bool Remove(string key) =>
        Mods.RemoveAll(m => string.Equals(m.Key, key, StringComparison.OrdinalIgnoreCase)) > 0;

    /// <summary>
    /// The list with the lines at <paramref name="indices"/> taken out and put back as one block, in their old order,
    /// starting at <paramref name="insertAt"/> of what is left (clamped). Works for one line or many.
    /// </summary>
    public static List<T> MoveBlock<T>(IReadOnlyList<T> items, IEnumerable<int> indices, int insertAt)
    {
        var picked = indices.Where(i => i >= 0 && i < items.Count).Distinct().Order().ToList();
        var block = picked.Select(i => items[i]).ToList();
        var rest = items.Where((_, i) => !picked.Contains(i)).ToList();
        rest.InsertRange(Math.Clamp(insertAt, 0, rest.Count), block);
        return rest;
    }

    public Playset Clone(string? newName = null) => new()
    {
        Name = newName ?? Name,
        Mods = Mods.Select(m => new PlaysetMod { Key = m.Key, Enabled = m.Enabled, Separator = m.Separator, PluginOff = m.PluginOff }).ToList(),
        PluginValues = PluginValues.ToDictionary(p => p.Key, p => new Dictionary<string, JsonElement>(p.Value), StringComparer.OrdinalIgnoreCase),
    };

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static Playset FromJson(string json) =>
        JsonSerializer.Deserialize<Playset>(json, JsonOptions) ?? throw new InvalidDataException("The playset file is empty.");

    /// <summary>A playset from a file someone shared: always a new playset (fresh id), with lines that couldn't mean anything dropped.</summary>
    public static Playset Import(string json)
    {
        var playset = FromJson(json);
        playset.Id = Guid.NewGuid().ToString("N");
        if (string.IsNullOrWhiteSpace(playset.Name)) playset.Name = "Imported playset";
        playset.Mods = (playset.Mods ?? []).Where(m => m is not null && (m.IsSeparator || !string.IsNullOrWhiteSpace(m.Key))).ToList();
        playset.PluginValues = playset.PluginValues is null ? new(StringComparer.OrdinalIgnoreCase) : new(playset.PluginValues, StringComparer.OrdinalIgnoreCase);
        return playset;
    }
}
