using System.Text.Json;
using System.Text.RegularExpressions;
using Artemis.Core.Text;

namespace Artemis.Core.Plugins;

public enum PluginTier { Stable, Fragile }

public enum SettingType { Bool, Int, Float, Enum, String, Path }

public sealed record PluginOption(string Value, string Name);

public sealed record PluginFeature(string Id, string Name, string Description, bool Default);

/// <summary>A setting a plugin declares. <see cref="Default"/> is a bool, a long, a double or a string, whichever the type needs.</summary>
public sealed record PluginSetting(string Id, SettingType Type, string Name, string Description, object Default, double? Min, double? Max, IReadOnlyList<PluginOption> Options);

/// <summary>
/// What a mod's <c>modloader.json</c> says about its plugin (D16). The manager reads it without loading the DLL: it is what the
/// trust prompt, the badges and the settings pages are made from.
/// </summary>
public sealed record PluginManifest(
    string Id, string Name, string Version, int ApiVersion,
    IReadOnlyList<string> Files, IReadOnlyList<string> Builds,
    IReadOnlyList<string> Dependencies, IReadOnlyList<string> Incompatible, IReadOnlyList<string> Keywords,
    IReadOnlyList<PluginFeature> Features, IReadOnlyList<PluginSetting> Settings,
    PluginTier Tier, bool Cosmetic);

/// <summary>A mod's plugin part: its manifest, or the reason the manifest can't be used.</summary>
public sealed record PluginInfo(PluginManifest? Manifest, string? Error)
{
    public bool IsValid => Manifest is not null;
}

public static partial class PluginManifestReader
{
    public const string FileName = "modloader.json";
    public const int SchemaVersion = 1;

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")]
    private static partial Regex IdPattern();

    /// <summary>The plugin part of the mod folder, or null if it has no <c>modloader.json</c>.</summary>
    public static PluginInfo? Read(string contentDir)
    {
        var file = Path.Combine(contentDir, FileName);
        if (!File.Exists(file)) return null;
        try { return Parse(File.ReadAllText(file)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new PluginInfo(null, Loc.T("core.manifest_unreadable", ("file", FileName), ("reason", ex.Message))); }
    }

    /// <summary>Checks the text field by field; every problem found is reported, the first thing wrong is what a short message shows.</summary>
    public static PluginInfo Parse(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }); }
        catch (JsonException ex) { return new PluginInfo(null, Loc.T("core.manifest_bad_json", ("file", FileName), ("reason", ex.Message))); }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new PluginInfo(null, Loc.T("core.manifest_not_object", ("file", FileName)));
            var errors = new List<string>();

            string? Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            List<string> Strings(string name)
            {
                if (!root.TryGetProperty(name, out var v)) return [];
                if (v.ValueKind != JsonValueKind.Array || v.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String)) { errors.Add(Loc.T("core.manifest_list_of_text", ("name", name))); return []; }
                return v.EnumerateArray().Select(x => x.GetString()!.Trim()).Where(x => x.Length > 0).ToList();
            }

            if (root.TryGetProperty("schema", out var schema) && !(schema.ValueKind == JsonValueKind.Number && schema.TryGetInt32(out var s) && s == SchemaVersion))
                errors.Add(Loc.T("core.manifest_schema", ("version", SchemaVersion)));

            var id = Text(root, "id")?.Trim() ?? "";
            if (!IdPattern().IsMatch(id)) errors.Add(Loc.T("core.manifest_id_invalid"));
            var version = Text(root, "version")?.Trim() ?? "";
            if (version.Length == 0) errors.Add(Loc.T("core.manifest_version_missing"));
            var apiVersion = root.TryGetProperty("apiVersion", out var api) && api.ValueKind == JsonValueKind.Number && api.TryGetInt32(out var a) && a >= 1 ? a : 0;
            if (apiVersion == 0) errors.Add(Loc.T("core.manifest_api_version"));

            var files = Strings("files").Select(f => f.Replace('\\', '/')).ToList();
            if (files.Count == 0) errors.Add(Loc.T("core.manifest_files_empty"));
            foreach (var f in files)
                if (!IsSafeDllPath(f)) errors.Add(Loc.T("core.manifest_file_unsafe", ("file", f)));

            var tierText = Text(root, "stability") ?? Text(root, "tier");
            var tier = tierText?.ToLowerInvariant() switch { null or "fragile" => PluginTier.Fragile, "stable" => PluginTier.Stable, _ => (PluginTier?)null };
            if (tier is null) errors.Add(Loc.T("core.manifest_stability"));

            var features = new List<PluginFeature>();
            if (root.TryGetProperty("features", out var fe))
            {
                if (fe.ValueKind != JsonValueKind.Array) errors.Add(Loc.T("core.manifest_features_not_list"));
                else foreach (var f in fe.EnumerateArray()) features.Add(ReadFeature(f, errors));
            }

            var settings = new List<PluginSetting>();
            if (root.TryGetProperty("settings", out var se))
            {
                if (se.ValueKind != JsonValueKind.Array) errors.Add(Loc.T("core.manifest_settings_not_list"));
                else foreach (var s2 in se.EnumerateArray()) if (ReadSetting(s2, errors) is { } setting) settings.Add(setting);
            }
            var ids = features.Select(f => f.Id).Concat(settings.Select(x => x.Id)).ToList();
            foreach (var dup in ids.GroupBy(i => i, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1)) errors.Add(Loc.T("core.manifest_duplicate_id", ("id", dup.Key)));

            if (errors.Count > 0) return new PluginInfo(null, string.Join(" ", errors));
            var cosmetic = root.TryGetProperty("cosmetic", out var c) && c.ValueKind == JsonValueKind.True;
            return new PluginInfo(new PluginManifest(id, Text(root, "name")?.Trim() is { Length: > 0 } n ? n : id, version, apiVersion, files, Strings("builds"),
                Strings("dependencies"), Strings("incompatible"), Strings("keywords"), features, settings, tier!.Value, cosmetic), null);
        }
    }

    /// <summary>A path inside the mod folder, written with forward slashes, that names a .dll. Nothing that climbs out of the folder.</summary>
    public static bool IsSafeDllPath(string path) =>
        path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && !Path.IsPathRooted(path) && !path.StartsWith('/')
        && !path.Contains(':') && !path.Split('/').Any(part => part is "" or "." or "..");

    private static PluginFeature ReadFeature(JsonElement e, List<string> errors)
    {
        var id = e.ValueKind == JsonValueKind.Object && e.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString()! : "";
        if (!IdPattern().IsMatch(id)) { errors.Add(Loc.T("core.manifest_feature_id")); return new PluginFeature("", "", "", false); }
        return new PluginFeature(id, Str(e, "name") is { Length: > 0 } name ? name : id, Str(e, "description"), e.TryGetProperty("default", out var d) && d.ValueKind == JsonValueKind.True);
    }

    private static PluginSetting? ReadSetting(JsonElement e, List<string> errors)
    {
        var id = e.ValueKind == JsonValueKind.Object && e.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString()! : "";
        if (!IdPattern().IsMatch(id)) { errors.Add(Loc.T("core.manifest_setting_id")); return null; }
        var type = Str(e, "type").ToLowerInvariant() switch
        {
            "bool" => SettingType.Bool, "int" => SettingType.Int, "float" => SettingType.Float,
            "enum" => SettingType.Enum, "string" => SettingType.String, "path" => SettingType.Path, _ => (SettingType?)null,
        };
        if (type is null) { errors.Add(Loc.T("core.manifest_setting_type", ("id", id))); return null; }

        var options = new List<PluginOption>();
        if (type == SettingType.Enum)
        {
            if (e.TryGetProperty("options", out var o) && o.ValueKind == JsonValueKind.Array)
                foreach (var item in o.EnumerateArray())
                {
                    var value = item.ValueKind == JsonValueKind.String ? item.GetString()! : item.ValueKind == JsonValueKind.Object ? Str(item, "value") : "";
                    if (value.Length > 0) options.Add(new PluginOption(value, item.ValueKind == JsonValueKind.Object && Str(item, "name") is { Length: > 0 } n ? n : value));
                }
            if (options.Count == 0) { errors.Add(Loc.T("core.manifest_setting_options", ("id", id))); return null; }
        }

        double? min = e.TryGetProperty("min", out var mn) && mn.ValueKind == JsonValueKind.Number ? mn.GetDouble() : null;
        double? max = e.TryGetProperty("max", out var mx) && mx.ValueKind == JsonValueKind.Number ? mx.GetDouble() : null;
        if (min > max) { errors.Add(Loc.T("core.manifest_setting_min_max", ("id", id))); return null; }

        object def;
        var hasDefault = e.TryGetProperty("default", out var d);
        switch (type)
        {
            case SettingType.Bool: def = hasDefault && d.ValueKind == JsonValueKind.True; break;
            case SettingType.Int: def = hasDefault && d.ValueKind == JsonValueKind.Number && d.TryGetInt64(out var l) ? l : 0L; break;
            case SettingType.Float: def = hasDefault && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : 0.0; break;
            case SettingType.Enum: def = hasDefault && d.ValueKind == JsonValueKind.String ? d.GetString()! : options[0].Value; break;
            default: def = hasDefault && d.ValueKind == JsonValueKind.String ? d.GetString()! : ""; break;
        }
        if (type == SettingType.Enum && options.All(o => o.Value != (string)def)) { errors.Add(Loc.T("core.manifest_setting_default_option", ("id", id))); return null; }
        if (def is long or double && (Convert.ToDouble(def) < min || Convert.ToDouble(def) > max)) { errors.Add(Loc.T("core.manifest_setting_default_range", ("id", id))); return null; }
        return new PluginSetting(id, type.Value, Str(e, "name") is { Length: > 0 } name ? name : id, Str(e, "description"), def, min, max, options);
    }

    private static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
}
