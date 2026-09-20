using System.IO;
using System.Text.Json.Nodes;
using Baker.Core;

namespace Baker.App;

internal sealed record SettingsPreset(string SourceSha256, PresetSettings Settings, JsonObject Properties)
{
    internal const string Kind = "wpe_baker_settings_preset";

    public static JsonObject Create(string sourcePath, string sourceSha256, PresetSettings settings, JsonObject properties) => new()
    {
        ["schema_version"] = 1,
        ["kind"] = Kind,
        ["source"] = new JsonObject
        {
            ["path"] = sourcePath,
            ["sha256"] = sourceSha256,
            ["digest_scope"] = ProjectSource.DigestScope
        },
        ["settings"] = settings.ToJson(),
        ["properties"] = properties.DeepClone()
    };

    public static SettingsPreset Parse(JsonObject document, JsonObject definitions)
    {
        if (document["schema_version"]?.GetValue<int>() != 1 || document["kind"]?.GetValue<string>() != Kind)
            throw new InvalidDataException("Unsupported settings preset.");
        JsonObject source = document["source"]?.AsObject() ?? throw new InvalidDataException("Preset source identity is missing.");
        _ = RequiredString(source, "path");
        string hash = RequiredString(source, "sha256");
        if (hash.Length != 64 || !hash.All(Uri.IsHexDigit) || source["digest_scope"]?.GetValue<string>() != ProjectSource.DigestScope)
            throw new InvalidDataException("Preset source identity is invalid.");
        JsonObject settings = document["settings"]?.AsObject() ?? throw new InvalidDataException("Preset settings are missing.");
        JsonObject properties = document["properties"]?.AsObject() ?? throw new InvalidDataException("Preset properties are missing.");
        ValidateProperties(properties, definitions);
        return new SettingsPreset(hash, PresetSettings.Parse(settings), properties.DeepClone().AsObject());
    }

    private static void ValidateProperties(JsonObject properties, JsonObject definitions)
    {
        foreach (var (key, value) in properties)
        {
            JsonObject definition = definitions[key]?.AsObject() ?? throw new InvalidDataException($"Preset property is not available: {key}.");
            string type = RequiredString(definition, "type");
            if (value is null) throw new InvalidDataException($"Preset property has no value: {key}.");
            if (type == "bool" && value is JsonValue boolean && boolean.TryGetValue<bool>(out _)) continue;
            if (type == "slider" && Number(value) is double number &&
                number >= (Number(definition["min"]) ?? 0) && number <= (Number(definition["max"]) ?? 100)) continue;
            if (type == "combo" && definition["options"] is JsonArray options &&
                options.OfType<JsonObject>().Any(option => JsonNode.DeepEquals(option["value"], value))) continue;
            if (type == "textinput" && value is JsonValue text && text.TryGetValue<string>(out _)) continue;
            if (type == "color" && value is JsonValue color && color.TryGetValue<string>(out string? rgb) && IsRgb(rgb)) continue;
            throw new InvalidDataException($"Preset property value is invalid: {key}.");
        }
    }

    private static string RequiredString(JsonObject objectValue, string key) => objectValue[key] is JsonValue value &&
        value.TryGetValue<string>(out string? text) && !string.IsNullOrWhiteSpace(text) ? text : throw new InvalidDataException($"Preset {key} is missing.");

    private static double? Number(JsonNode? node) => node is JsonValue value && value.TryGetValue<double>(out double number) && double.IsFinite(number) ? number : null;

    private static bool IsRgb(string? text)
    {
        string[] parts = text?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries) ?? [];
        return parts.Length == 3 && parts.All(part => double.TryParse(part, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double value) && double.IsFinite(value) && value >= 0 && value <= 1);
    }
}

internal sealed record PresetSettings(uint Width, uint Height, string Fps, string? DeviceUuid, bool Retime, bool FixedView,
    bool LayeredVideo, bool ForegroundLive, bool SimpleTextEffects, bool AudioEffects, string LoopPreference = "balanced",
    string Interaction = "fixed", bool Compatibility = false, string PlaybackEncoder = "software", bool MatchEffectResolution = false)
{
    public JsonObject ToJson() => new()
    {
        ["width"] = Width, ["height"] = Height, ["fps"] = Fps, ["device_uuid"] = DeviceUuid,
        ["retime"] = Retime, ["fixed_view"] = FixedView, ["layered_video"] = LayeredVideo,
        ["foreground_live"] = ForegroundLive, ["simple_text_effects"] = SimpleTextEffects,
        ["audio_effects"] = AudioEffects, ["loop_preference"] = LoopPreference,
        ["interaction"] = Interaction, ["compatibility"] = Compatibility,
        ["playback_encoder"] = PlaybackEncoder, ["match_effect_resolution"] = MatchEffectResolution
    };

    public static PresetSettings Parse(JsonObject settings)
    {
        // 0×0 表示按场景画布取值；只有一边为 0 仍是无效预设。
        uint width = DimensionUInt(settings, "width"), height = DimensionUInt(settings, "height");
        if ((width == 0) != (height == 0)) throw new InvalidDataException("Preset width and height must both be positive or both 0.");
        string fps = RequiredString(settings, "fps");
        if (!AppEnvironment.TryFrameRate(fps, out _, out _)) throw new InvalidDataException("Preset frame rate is invalid.");
        if (!settings.ContainsKey("device_uuid")) throw new InvalidDataException("Preset device_uuid is invalid.");
        string? device = settings["device_uuid"] is null ? null : RequiredString(settings, "device_uuid");
        // Legacy presets may carry seam_repair. It is deliberately parsed only for format compatibility:
        // newly analyzed plans always disable repair.
        if (settings.ContainsKey("seam_repair")) _ = RequiredBool(settings, "seam_repair");
        // 循环取向是后加的字段：老预设没有它，按默认的平衡档读。
        string loopPreference = settings.ContainsKey("loop_preference") ? RequiredString(settings, "loop_preference") : "balanced";
        if (loopPreference is not ("performance" or "balanced" or "quality"))
            throw new InvalidDataException("Preset loop_preference is invalid.");
        string interaction = settings.ContainsKey("interaction") ? RequiredString(settings, "interaction")
            : RequiredBool(settings, "fixed_view") ? "fixed" : "keep";
        if (interaction is not ("keep" or "fixed" or "off")) throw new InvalidDataException("Preset interaction is invalid.");
        return new(width, height, fps, device, RequiredBool(settings, "retime"), RequiredBool(settings, "fixed_view"),
            RequiredBool(settings, "layered_video"), RequiredBool(settings, "foreground_live"), RequiredBool(settings, "simple_text_effects"),
            RequiredBool(settings, "audio_effects"), loopPreference, interaction,
            settings.ContainsKey("compatibility") && RequiredBool(settings, "compatibility"),
            PlaybackEncoderSelection.Normalize(settings.ContainsKey("playback_encoder") ? RequiredString(settings, "playback_encoder") : null),
            settings.ContainsKey("match_effect_resolution") && RequiredBool(settings, "match_effect_resolution"));
    }

    private static uint DimensionUInt(JsonObject objectValue, string key) => objectValue[key] is JsonValue value &&
        value.TryGetValue<uint>(out uint number) ? number : throw new InvalidDataException($"Preset {key} is invalid.");
    private static bool RequiredBool(JsonObject objectValue, string key) => objectValue[key] is JsonValue value &&
        value.TryGetValue<bool>(out bool result) ? result : throw new InvalidDataException($"Preset {key} is invalid.");
    private static string RequiredString(JsonObject objectValue, string key) => objectValue[key] is JsonValue value &&
        value.TryGetValue<string>(out string? text) && !string.IsNullOrWhiteSpace(text) ? text : throw new InvalidDataException($"Preset {key} is invalid.");
}
