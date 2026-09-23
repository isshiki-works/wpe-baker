using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace Baker.Core;

/// <summary>
/// 读出用户在 Wallpaper Engine 里给某张壁纸设的属性值（config.json → &lt;用户&gt;.wproperties），作为分析的属性底值。
/// 只读：从不写 config.json，也不碰正在运行的 Wallpaper Engine。读不到时回退壁纸默认值，并把原因写进 plan。
/// </summary>
public static class WallpaperEngineProperties
{
    public const string SourceWpe = "wpe";
    public const string SourceDefaults = "defaults";
    public const string SourceUnavailable = "wpe_unavailable";

    /// <summary>找到的 config.json 与它是怎么找到的（running_wallpaper_engine / steam_library / configured_path）。</summary>
    public sealed record ConfigLocation(string Path, string Origin);

    /// <summary>
    /// 解析结果。<see cref="Values"/> 是通过类型校验、以 project.json 属性名为键的 WPE 值（可能为空）；
    /// <see cref="Record"/> 是写进 plan 的溯源（source、reason、config、profile、entry、location、ignored）。
    /// </summary>
    public sealed record Resolution(string Source, JsonObject Values, JsonObject Record);

    private static readonly JsonDocumentOptions ConfigJsonOptions = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    public static bool IsKnownMode(string? mode) => mode is SourceWpe or SourceDefaults;

    /// <summary>
    /// 定位 config.json：先看当前会话里正在运行的 wallpaper64/32 所在目录，再按注册表与 Steam 库推导安装目录，
    /// 最后才用调用方给的路径（GUI 设置里的 WPE 程序、CLI 的 assets 上一级）。都找不到返回 null。
    /// </summary>
    public static ConfigLocation? LocateConfig(string? configuredPath)
    {
        string running = Safe(NativeEnvironment.RunningWallpaperDirectory);
        string installed = Safe(NativeEnvironment.InstalledWallpaperDirectory);
        return LocateConfig([(running, "running_wallpaper_engine"), (installed, "steam_library"),
            (ConfiguredDirectory(configuredPath), "configured_path")]);

        // 进程、注册表或 libraryfolders.vdf 读不了只当"这一处没找到"，不让分析因此失败。
        static string Safe(Func<string> lookup)
        {
            try { return lookup(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException
                or InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException or NotSupportedException) { return ""; }
        }
    }

    /// <summary>按给定顺序取第一个含 config.json 的目录；拆出来便于测试顺序。</summary>
    public static ConfigLocation? LocateConfig(IEnumerable<(string? Directory, string Origin)> candidates)
    {
        foreach (var (directory, origin) in candidates)
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            try
            {
                string file = Path.Combine(Path.GetFullPath(directory), "config.json");
                if (File.Exists(file)) return new(file, origin);
            }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { }
        }
        return null;
    }

    /// <summary>调用方给的路径可以是 wallpaper64.exe、安装目录、assets 目录或 config.json 本身，统一换成安装目录。</summary>
    public static string? ConfiguredDirectory(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured)) return null;
        try
        {
            string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configured.Trim()));
            if (File.Exists(full)) return Path.GetDirectoryName(full);
            if (Directory.Exists(full) && Path.GetFileName(full).Equals("assets", StringComparison.OrdinalIgnoreCase)
                && !File.Exists(Path.Combine(full, "config.json"))) return Path.GetDirectoryName(full);
            return full;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    /// <summary>路径规范化：正反斜杠统一、展开成绝对路径、去掉结尾分隔符；比较时再忽略大小写。</summary>
    public static string NormalizePath(string path)
    {
        string unified = path.Trim().Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        try { unified = Path.GetFullPath(unified); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { }
        return Path.TrimEndingDirectorySeparator(unified);
    }

    private static bool SamePath(string first, string second) =>
        string.Equals(NormalizePath(first), NormalizePath(second), StringComparison.OrdinalIgnoreCase);

    /// <summary>本机"当前用户"的候选名：Windows 用户名（Wallpaper Engine 实际按它分键）与 Steam 自动登录账户名。</summary>
    public static IReadOnlyList<string> CurrentUserNames()
    {
        var names = new List<string> { Environment.UserName };
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var steam = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                if (steam?.GetValue("AutoLoginUser") is string login && !string.IsNullOrWhiteSpace(login)) names.Add(login);
            }
            catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        }
        return names;
    }

    /// <summary>
    /// 按模式解析属性底值。<paramref name="sourceDirectory"/> 是壁纸目录，<paramref name="sourceEntry"/> 是实际读取的场景入口
    /// （scene.pkg / scene.json / project.json），只在同目录有多条记录时用来挑。
    /// </summary>
    public static Resolution Resolve(string mode, string sourceDirectory, string sourceEntry, JsonObject project,
        ConfigLocation? location, IReadOnlyList<string>? userNames = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (!IsKnownMode(mode)) throw new ArgumentException("Properties source must be wpe or defaults.", nameof(mode));
        if (mode == SourceDefaults) return new(SourceDefaults, new JsonObject(), Record(SourceDefaults, "requested_defaults"));
        if (location is null) return Unavailable("config_not_found");

        var (account, profileName, profileMatch, accountReason, accountDetail) = ReadAccount(location, userNames);
        if (account is null) return Unavailable(accountReason, location, detail: accountDetail);

        JsonObject record = Record(SourceWpe, null, location);
        record["profile"] = profileName;
        record["profile_match"] = profileMatch;
        if (account["wproperties"] is not JsonObject saved) return NoSaved(record);

        // 键是 Wallpaper Engine 播放的那个文件（scene.pkg、project.json、视频文件……），按所在目录与来源匹配。
        var entries = saved.Where(pair => pair.Value is JsonObject && EntryDirectoryMatches(pair.Key, sourceDirectory)).ToArray();
        if (entries.Length == 0) return NoSaved(record);
        if (entries.Length > 1)
        {
            string projectFile = Path.Combine(sourceDirectory, "project.json");
            var exact = entries.Where(pair => SamePath(pair.Key, sourceEntry)).ToArray();
            if (exact.Length == 0) exact = entries.Where(pair => SamePath(pair.Key, projectFile)).ToArray();
            if (exact.Length == 1) entries = exact;
            else if (!entries.Skip(1).All(pair => JsonNode.DeepEquals(pair.Value, entries[0].Value)))
                return Unavailable("entry_ambiguous", location, profileName, detail: string.Join(", ", entries.Select(pair => pair.Key)));
        }
        string entryKey = entries[0].Key;
        var entry = entries[0].Value!.AsObject();
        record["entry"] = entryKey;

        // 新版按屏幕（MonitorN）再分一层；老格式直接是属性表。
        JsonObject values;
        string? locationKey = null;
        if (entry.Count > 0 && entry.All(pair => pair.Value is JsonObject))
        {
            var locations = entry.Select(pair => pair.Key).ToArray();
            if (locations.Length > 1)
            {
                // 优先正在放这张壁纸的那块屏幕；仍有多块时，设置完全相同才取，否则分不清用户看到的是哪一套。
                var selected = account["general"]?["wallpaperconfig"]?["selectedwallpapers"] as JsonObject;
                var showing = locations.Where(key => selected?[key]?["file"] is JsonValue file && file.TryGetValue(out string? path) &&
                    !string.IsNullOrWhiteSpace(path) && EntryDirectoryMatches(path, sourceDirectory)).ToArray();
                if (showing.Length > 0) locations = showing;
            }
            if (locations.Length > 1 && !locations.Skip(1).All(key => JsonNode.DeepEquals(entry[key], entry[locations[0]])))
                return Unavailable("location_ambiguous", location, profileName, entryKey, string.Join(", ", locations));
            locationKey = locations.OrderBy(key => key, StringComparer.Ordinal).First();
            values = entry[locationKey]!.AsObject();
        }
        else values = entry;
        record["location"] = locationKey;

        var definitions = project["general"]?["properties"] as JsonObject ?? new JsonObject();
        var accepted = new JsonObject();
        var ignored = new JsonArray();
        foreach (var (key, value) in values)
        {
            string? definitionKey = definitions.ContainsKey(key) ? key
                : definitions.Where(pair => pair.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Select(pair => pair.Key).ToArray() is [string single] ? single : null;
            string? reason;
            JsonNode? normalized = null;
            if (definitionKey is null || definitions[definitionKey] is not JsonObject definition) reason = "not_a_project_property";
            else reason = Validate(definition, value, out normalized);
            if (reason is null) accepted[definitionKey!] = normalized;
            else ignored.Add(new JsonObject { ["key"] = key, ["reason"] = reason, ["value"] = value?.DeepClone() });
        }
        record["ignored"] = ignored;
        return new(SourceWpe, accepted, record);

        static Resolution NoSaved(JsonObject record)
        {
            // 读到了 Wallpaper Engine 的设置，只是这张壁纸没改过任何属性：与默认值相同，来源仍记为 wpe。
            record["reason"] = "no_saved_properties";
            record["ignored"] = new JsonArray();
            return new(SourceWpe, new JsonObject(), record);
        }
        static Resolution Unavailable(string reason, ConfigLocation? location = null, string? profile = null, string? entry = null, string? detail = null)
        {
            JsonObject record = Record(SourceUnavailable, reason, location);
            if (profile is not null) record["profile"] = profile;
            if (entry is not null) record["entry"] = entry;
            if (detail is not null) record["detail"] = detail;
            return new(SourceUnavailable, new JsonObject(), record);
        }
    }

    /// <summary>
    /// 读 config.json 并挑出"当前用户"那一份账户设置：用户名对上取它，只有一个用户时取它，否则分不清。
    /// 读不到时 Account 为 null，Reason 是 <see cref="Resolve"/> 与 <see cref="ReadFrameRateLimit"/> 共用的原因码。
    /// </summary>
    private static (JsonObject? Account, string Profile, string Match, string Reason, string? Detail) ReadAccount(
        ConfigLocation location, IReadOnlyList<string>? userNames)
    {
        JsonObject config;
        try
        {
            using var stream = new FileStream(location.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            config = JsonNode.Parse(stream, documentOptions: ConfigJsonOptions) as JsonObject
                ?? throw new InvalidDataException("config.json is not a JSON object.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return (null, "", "", "config_unreadable", error.Message);
        }

        // 顶层 "?installdirectory" 这类不是用户；其余每个对象值是一个用户。
        var profiles = config.Where(pair => pair.Value is JsonObject && !pair.Key.StartsWith('?')).Select(pair => pair.Key).ToArray();
        if (profiles.Length == 0) return (null, "", "", "no_profile", null);
        string[] names = (userNames ?? CurrentUserNames()).Where(name => !string.IsNullOrWhiteSpace(name)).ToArray();
        string[] matched = profiles.Where(profile => names.Any(name => profile.Equals(name, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (matched.Length == 1) return (config[matched[0]]!.AsObject(), matched[0], "current_user", "", null);
        if (matched.Length == 0 && profiles.Length == 1) return (config[profiles[0]]!.AsObject(), profiles[0], "only_profile", "", null);
        return (null, "", "", "profile_ambiguous", string.Join(", ", profiles));
    }

    /// <summary>读到设置时的原因码：有上限 / 用户没设上限。</summary>
    public const string LimitFound = "wpe_fps_limit", LimitNone = "no_fps_limit";

    /// <summary>Wallpaper Engine 设置里"不限帧率"的判定边界：超出 [1, 1000] 的存值都当没有上限。</summary>
    public const uint MaximumFrameRateLimit = 1000;

    /// <summary>
    /// 用户在 Wallpaper Engine 设置里定的帧率上限。<see cref="Known"/> 为 false 表示连"有没有上限"都没读到；
    /// 为 true 而 <see cref="Fps"/> 为 null 表示读到了、用户没设上限。<see cref="Reason"/> 是写进 plan 的溯源。
    /// </summary>
    public sealed record FrameRateLimit(bool Known, uint? Fps, string Reason);

    /// <summary>
    /// 读 config.json 的 general.user.fps。只读：与属性读取共用定位与用户挑选，读不到就如实说明原因，
    /// 由 <see cref="OutputFrameRate"/> 决定怎么回退。
    /// </summary>
    public static FrameRateLimit ReadFrameRateLimit(ConfigLocation? location, IReadOnlyList<string>? userNames = null)
    {
        if (location is null) return new(false, null, "config_not_found");
        var (account, _, _, reason, _) = ReadAccount(location, userNames);
        if (account is null) return new(false, null, reason);
        if (account["general"]?["user"]?["fps"] is not JsonValue value || value.GetValueKind() != JsonValueKind.Number ||
            !double.TryParse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double fps) || !double.IsFinite(fps))
            return new(false, null, "fps_not_set");
        return fps >= 1 && fps <= MaximumFrameRateLimit ? new(true, (uint)Math.Round(fps), LimitFound) : new(true, null, LimitNone);
    }

    /// <summary>读 config.json 的 general.user.postprocessing（disabled/enabled/ultra/displayhdr）；读不到为 null。</summary>
    public static string? ReadPostprocessing(ConfigLocation? location) =>
        location is not null && ReadAccount(location, null).Item1?["general"]?["user"]?["postprocessing"] is JsonValue value &&
        value.TryGetValue(out string? text) ? text : null;

    private static JsonObject Record(string source, string? reason, ConfigLocation? location = null)
    {
        var record = new JsonObject { ["source"] = source, ["reason"] = reason };
        if (location is not null)
        {
            record["config"] = location.Path;
            record["config_origin"] = location.Origin;
        }
        return record;
    }

    private static bool EntryDirectoryMatches(string key, string sourceDirectory)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;
        string normalized = NormalizePath(key);
        string? directory = Path.GetDirectoryName(normalized);
        return (directory is not null && SamePath(directory, sourceDirectory)) || SamePath(normalized, sourceDirectory);
    }

    /// <summary>
    /// 按 project.json 的属性类型校验一个 WPE 值，通过时给出规范化后的值（组合框取选项本身的值）。
    /// 规则与"载入方案"的属性校验一致；返回 null 表示有效，否则是忽略原因。
    /// </summary>
    public static string? Validate(JsonObject definition, JsonNode? value, out JsonNode? normalized)
    {
        normalized = null;
        string type = definition["type"] is JsonValue typeValue && typeValue.TryGetValue(out string? text) ? text : "";
        if (value is null) return "missing_value";
        switch (type)
        {
            case "bool":
                if (value is JsonValue boolean && boolean.TryGetValue(out bool _)) { normalized = value.DeepClone(); return null; }
                return "invalid_bool";
            case "slider":
                if (Number(value) is not double number) return "invalid_number";
                if (number < (Number(definition["min"]) ?? 0) || number > (Number(definition["max"]) ?? 100)) return "out_of_range";
                normalized = value.DeepClone();
                return null;
            case "combo":
                if (definition["options"] is not JsonArray options) return "combo_without_options";
                string wanted = Scalar(value);
                var option = options.OfType<JsonObject>().FirstOrDefault(item => item["value"] is JsonNode optionValue &&
                    (JsonNode.DeepEquals(optionValue, value) || Scalar(optionValue) == wanted));
                if (option is null) return "not_an_option";
                normalized = option["value"]!.DeepClone();
                return null;
            case "textinput":
                if (value is JsonValue textValue && textValue.TryGetValue(out string? _)) { normalized = value.DeepClone(); return null; }
                return "invalid_text";
            case "color":
                if (value is JsonValue colorValue && colorValue.TryGetValue(out string? rgb) && IsRgb(rgb)) { normalized = value.DeepClone(); return null; }
                return "invalid_color";
            default:
                return "unsupported_type:" + (type.Length == 0 ? "unknown" : type);
        }
    }

    /// <summary>
    /// WPE 值打底、显式属性（CLI 的 --properties、GUI 面板里的改动）覆盖在上。返回交给分析的 UserProperties
    /// （两边都没有时为 null，plan 的 settings 与改动前一致）与写进 plan 的来源记录。
    /// </summary>
    public static (JsonObject? UserProperties, JsonObject Origin) Merge(Resolution resolution, JsonObject project, JsonObject? explicitProperties)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        ArgumentNullException.ThrowIfNull(project);
        var merged = new JsonObject();
        foreach (var (key, value) in resolution.Values) merged[key] = value?.DeepClone();
        var overridden = new List<string>();
        if (explicitProperties is not null)
            foreach (var (key, value) in explicitProperties)
            {
                if (resolution.Values.ContainsKey(key)) overridden.Add(key);
                merged[key] = value?.DeepClone();
            }
        JsonObject origin = resolution.Record.DeepClone().AsObject();
        if (resolution.Source == SourceWpe)
        {
            var definitions = project["general"]?["properties"] as JsonObject;
            string[] applied = resolution.Values.Select(pair => pair.Key).Where(key => !overridden.Contains(key)).ToArray();
            string[] differs = applied.Where(key => !SameValue(DefaultOf(definitions?[key]), resolution.Values[key])).ToArray();
            origin["applied_keys"] = new JsonArray([.. applied.Select(key => (JsonNode)JsonValue.Create(key))]);
            origin["differs_from_default"] = new JsonArray([.. differs.Select(key => (JsonNode)JsonValue.Create(key))]);
            origin["overridden_keys"] = new JsonArray([.. overridden.Select(key => (JsonNode)JsonValue.Create(key))]);
        }
        return (explicitProperties is null && merged.Count == 0 ? null : merged, origin);
    }

    /// <summary>plan 里"与默认不同"的项数；没有来源记录或不是 wpe 时为 null。</summary>
    public static int? DifferingCount(JsonObject plan) =>
        plan["properties_source"] is JsonValue source && source.TryGetValue(out string? text) && text == SourceWpe &&
        plan["wpe_properties"]?["differs_from_default"] is JsonArray differs ? differs.Count : null;

    /// <summary>plan 里实际生效（没被显式属性覆盖）的 WPE 属性名。</summary>
    public static IReadOnlySet<string> AppliedKeys(JsonObject? plan) =>
        (plan?["wpe_properties"]?["applied_keys"] as JsonArray ?? []).Select(node => node is JsonValue value && value.TryGetValue(out string? key) ? key : null)
            .OfType<string>().ToHashSet(StringComparer.Ordinal);

    private static JsonNode? DefaultOf(JsonNode? definition) => definition is JsonObject entry ? entry["value"] : definition;

    private static bool SameValue(JsonNode? first, JsonNode? second)
    {
        if (Number(first) is double a && Number(second) is double b) return a == b;
        return JsonNode.DeepEquals(first, second) || first is JsonValue && second is JsonValue && Scalar(first) == Scalar(second);
    }

    // 按 JSON 文本取数：解析出来的值与代码里构造的 int/double 值走同一条路（后者 TryGetValue<double> 会失败）。
    private static double? Number(JsonNode? node) => node is JsonValue value && value.GetValueKind() == JsonValueKind.Number &&
        double.TryParse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double number) && double.IsFinite(number) ? number : null;

    private static string Scalar(JsonNode? node) => node is JsonValue value && value.TryGetValue(out string? text) ? text : node?.ToJsonString() ?? "null";

    private static bool IsRgb(string? text)
    {
        string[] parts = text?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries) ?? [];
        return parts.Length == 3 && parts.All(part => double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out double channel) &&
            double.IsFinite(channel) && channel >= 0 && channel <= 1);
    }
}
