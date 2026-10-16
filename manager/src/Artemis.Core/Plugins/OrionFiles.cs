using System.Text.Json;
using Artemis.Core.IO;
using Artemis.Core.Mods;
using Artemis.Core.Playsets;

namespace Artemis.Core.Plugins;

/// <summary>One plugin's outcome in the last game session, as Orion wrote it.</summary>
public sealed record PluginStatusEntry(string Mod, string Id, string State, string? Reason, string? Detail)
{
    /// <summary>"loaded", "skipped" or "failed"; anything else Orion writes is shown as it is.</summary>
    public bool IsProblem => State is "skipped" or "failed";
}

/// <summary>Orion's honest mode (D22, D53): whether the plugins that change how the game plays were added to the game's checksum. State is "notNeeded", "pending", "mounted" or "failed".</summary>
public sealed record HonestStatus(string State, string? Detail, int Plugins);

public sealed record PluginStatusReport(string? Orion, string? Build, DateTime? StartedUtc, IReadOnlyList<PluginStatusEntry> Plugins, HonestStatus? Honest = null);

/// <summary>Orion writes this before it loads each plugin and removes it when start-up is over. If it is still there and the game is not running, the game died while loading that plugin (D24).</summary>
public sealed record LoadingMarker(string Mod, string Id, string File);

/// <summary>Reads what Orion leaves in the game's data folder (<c>modloader/status.json</c>, <c>modloader/loading.json</c>). Never throws: no file, or one we can't read, is "nothing".</summary>
public static class OrionFiles
{
    public static string FolderIn(string userDir) => Path.Combine(userDir, "modloader");

    public static PluginStatusReport? ReadStatus(string userDir)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(FolderIn(userDir), "status.json")), Options);
            var root = doc.RootElement;
            var plugins = new List<PluginStatusEntry>();
            if (root.TryGetProperty("plugins", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var p in list.EnumerateArray())
                    if (p.ValueKind == JsonValueKind.Object && Text(p, "mod") is { Length: > 0 } mod)
                        plugins.Add(new PluginStatusEntry(mod, Text(p, "id"), Text(p, "state"), NullIfEmpty(Text(p, "reason")), NullIfEmpty(Text(p, "detail"))));
            DateTime? started = DateTime.TryParse(Text(root, "startedUtc"), null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var t) ? t : null;
            HonestStatus? honest = root.TryGetProperty("honest", out var h) && h.ValueKind == JsonValueKind.Object && Text(h, "state") is { Length: > 0 } state
                ? new HonestStatus(state, NullIfEmpty(Text(h, "detail")), h.TryGetProperty("plugins", out var n) && n.ValueKind == JsonValueKind.Number && n.TryGetInt32(out var count) ? count : 0)
                : null;
            return new PluginStatusReport(NullIfEmpty(Text(root, "orion")), NullIfEmpty(Text(root, "build")), started, plugins, honest);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException) { return null; }
    }

    public static LoadingMarker? ReadMarker(string userDir)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(FolderIn(userDir), "loading.json")), Options);
            return Text(doc.RootElement, "mod") is { Length: > 0 } mod ? new LoadingMarker(mod, Text(doc.RootElement, "id"), Text(doc.RootElement, "file")) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException) { return null; }
    }

    public static void ClearMarker(string userDir)
    {
        try { File.Delete(Path.Combine(FolderIn(userDir), "loading.json")); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static readonly JsonDocumentOptions Options = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    private static string Text(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;
}

/// <summary>The value a setting has, whatever the playset file holds: a stored value that doesn't fit the setting's type or range gives the default or the nearest allowed value.</summary>
public static class PluginValue
{
    public static object For(PluginSetting setting, JsonElement? stored)
    {
        if (stored is not { } e) return setting.Default;
        switch (setting.Type)
        {
            case SettingType.Bool:
                return e.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => setting.Default };
            case SettingType.Int:
                return e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out var l) ? Clamp(setting, l) : setting.Default;
            case SettingType.Float:
                return e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out var d) && double.IsFinite(d) ? Clamp(setting, d) : setting.Default;
            case SettingType.Enum:
                return e.ValueKind == JsonValueKind.String && setting.Options.Any(o => o.Value == e.GetString()) ? e.GetString()! : setting.Default;
            default:
                return e.ValueKind == JsonValueKind.String ? e.GetString()! : setting.Default;
        }
    }

    public static bool Feature(PluginFeature feature, JsonElement? stored) =>
        stored is { ValueKind: JsonValueKind.True or JsonValueKind.False } e ? e.ValueKind == JsonValueKind.True : feature.Default;

    private static long Clamp(PluginSetting s, long v) => (long)Math.Clamp(v, s.Min ?? long.MinValue, s.Max ?? long.MaxValue);

    private static double Clamp(PluginSetting s, double v) => Math.Clamp(v, s.Min ?? double.MinValue, s.Max ?? double.MaxValue);
}

/// <summary>Writes <c>plugin-settings.json</c>: for the playset about to be played, which plugins are switched on and the value of every setting and feature, defaults filled in so Orion needs no schema.</summary>
public static class PluginSettingsExport
{
    public static string Render(Playset playset, IReadOnlyDictionary<string, ModInfo> installed, bool pluginsEnabled)
    {
        var mods = new SortedDictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in playset.EnabledMods)
        {
            if (!installed.TryGetValue(entry.Key, out var mod) || mod.Plugin?.Manifest is not { } manifest) continue;
            var stored = playset.PluginValues.GetValueOrDefault(entry.Key);
            JsonElement? Stored(string id) => stored is not null && stored.TryGetValue(id, out var v) ? v : null;
            var values = new SortedDictionary<string, object>(StringComparer.Ordinal);
            foreach (var f in manifest.Features) values[f.Id] = PluginValue.Feature(f, Stored(f.Id));
            foreach (var s in manifest.Settings) values[s.Id] = PluginValue.For(s, Stored(s.Id));
            // countsForChecksum: whether Orion adds this plugin to the game's checksum (honest mode, D22, D53). Today the manifest's "cosmetic" claim decides.
            mods[entry.Key] = new { id = manifest.Id, enabled = !entry.PluginOff, countsForChecksum = !manifest.Cosmetic, values };
        }
        return JsonSerializer.Serialize(new { version = 1, playset = playset.Id, pluginsEnabled, mods }, Playset.JsonOptions);
    }

    public static void Write(AppPaths paths, Playset playset, IReadOnlyDictionary<string, ModInfo> installed, bool pluginsEnabled) =>
        AtomicFile.WriteAllText(paths.PluginSettingsFile, Render(playset, installed, pluginsEnabled));
}
