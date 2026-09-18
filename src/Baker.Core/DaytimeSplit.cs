using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Baker.Core;

/// <summary>
/// 昼夜/时段壁纸的状态拆分（feat/daytime-split，默认关）。
/// 用户模型：昼夜壁纸 = 几套传统壁纸的拼接，读时钟的脚本只切一组图层的可见性，不改画面内容。
/// 这里按脚本模式静态识别这样的"状态选择器"，并按脚本里的小时阈值枚举它的输出状态：
///   - 脚本挂在某层的 visible 绑定上，读 <c>new Date().getHours()</c>；
///   - 用字符串数组字面量声明几组图层名（<c>var nightLayers = ["night1", ...]</c>），运行时 <c>thisScene.getLayer</c> 取层；
///   - 分支形如 <c>if (hours &gt;= A &amp;&amp; hours &lt; B) { show(GROUP); }</c>，兜底 <c>else { show(GROUP); }</c>；
///   - 对图层的属性写只有 <c>.visible =</c>，不调用播放类方法。
/// 任何一条不满足就退回现有行为，原因写进 plan 的 daytime_split.fallback_reason。不按壁纸 id 特判。
/// </summary>
internal static class DaytimeSplit
{
    internal const string Recognized = "recognized", FallbackStatus = "fallback";

    private static readonly RegexOptions Options = RegexOptions.CultureInvariant;
    private static readonly Regex ClockRead = new(@"\bnew\s+Date\s*\(\s*\)|\bDate\s*\.\s*now\b|\btimeOfDay\b", Options);
    private static readonly Regex HoursRead = new(@"\bgetHours\s*\(\s*\)", Options);
    private static readonly Regex GroupArray = new(@"\b(?:var|let|const)\s+(\w+)\s*=\s*\[\s*((?:(?:""[^""]*""|'[^']*')\s*,?\s*)+)\]", Options);
    private static readonly Regex StringLiteral = new(@"""([^""]*)""|'([^']*)'", Options);
    private static readonly Regex HourBranch = new(@"\bif\s*\(\s*(\w+)\s*>=\s*(\w+)\s*&&\s*\1\s*<\s*(\w+)\s*\)\s*\{\s*(\w+)\s*\(\s*(\w+)\s*\)\s*;?\s*\}", Options);
    private static readonly Regex ElseBranch = new(@"\}\s*else\s*\{\s*(\w+)\s*\(\s*(\w+)\s*\)\s*;?\s*\}", Options);
    private static readonly Regex NumberAssignment = new(@"\b(\w+)\s*=\s*(\d{1,2})\b", Options);
    private static readonly Regex PropertyWrite = new(@"\.\s*(\w+)\s*=(?!=)", Options);
    private static readonly Regex MethodCall = new(@"\.\s*(play|pause|stop|seek|setText|setTexture|setEffect)\s*\(", Options);
    private static readonly Regex LiveInput = new(@"\binput\s*[.\[]|\bregisterAudioBuffers\s*\(|\bfunction\s+(cursor|media)\w*\s*\(", Options);

    /// <summary>一个状态：名字、覆盖的小时段（[起, 止) 可跨午夜拆成多段）、该状态下选择器置为可见的受控层。</summary>
    internal sealed record State(string Name, int[][] Hours, int[] VisibleLayerIds);

    /// <summary>识别结果：status=recognized 时 ControllerId、States、ControlledLayerIds 有效；否则 FallbackReason 说明退回原因。</summary>
    internal sealed record Detection(string Status, int? ControllerId, string? ControllerName, State[] States, int[] ControlledLayerIds,
        string? FallbackReason, string? ThresholdSource)
    {
        internal bool IsRecognized => Status == Recognized;

        internal State? StateNamed(string name) => States.FirstOrDefault(state => state.Name == name);

        internal JsonObject ToJson() => new()
        {
            ["status"] = Status,
            ["controller_layer_id"] = ControllerId,
            ["controller_name"] = ControllerName,
            ["threshold_source"] = ThresholdSource,
            ["controlled_layer_ids"] = JsonSerializer.SerializeToNode(ControlledLayerIds),
            ["states"] = new JsonArray(States.Select(state => (JsonNode)new JsonObject
            {
                ["name"] = state.Name,
                ["hours"] = JsonSerializer.SerializeToNode(state.Hours),
                ["visible_layer_ids"] = JsonSerializer.SerializeToNode(state.VisibleLayerIds)
            }).ToArray()),
            ["fallback_reason"] = FallbackReason
        };
    }

    private static Detection Fallback(string reason, int? controller = null, string? name = null) =>
        new(FallbackStatus, controller, name, [], [], reason, null);

    /// <summary>
    /// 在场景对象里找状态选择器。候选只看 visible 绑定上读时钟的脚本：文字时钟、按时段改着色器常量的脚本都不是状态选择器。
    /// <paramref name="dependencies"/> 是运行时观测到的对象访问：同名图层靠"选择器实际写过 visible 的那一个"消歧（getLayer 取的就是它）。
    /// </summary>
    internal static Detection Detect(IReadOnlyDictionary<int, JsonObject> objects, JsonArray? dependencies = null, JsonObject? properties = null)
    {
        var candidates = new List<(int Id, string Code)>();
        foreach (var (id, obj) in objects)
        {
            if (obj["visible"] is not JsonObject binding || binding["script"] is not JsonValue value ||
                !value.TryGetValue<string>(out string? text)) continue;
            string code = HybridScenePlanner.CapabilityScanText(text);
            if (ClockRead.IsMatch(code)) candidates.Add((id, code));
        }
        if (candidates.Count == 0) return Fallback("no_visibility_script_reads_clock");
        // 候选逐个试：识别失败的候选（按整点播语音之类）本来就该保持实时，不影响别的候选；
        // 恰好一个成功就用它，多个成功本轮不支持（几套状态集合相乘的组合先不做），都失败报第一个的原因。
        var recognized = new List<Detection>();
        Detection? first = null;
        foreach (var (id, code) in candidates)
        {
            Detection detection = Analyze(id, objects[id]["name"]?.GetValue<string>(), code, objects, dependencies, properties);
            if (detection.IsRecognized) recognized.Add(detection);
            else first ??= detection;
        }
        if (recognized.Count == 1) return recognized[0];
        if (recognized.Count > 1)
            return Fallback("multiple_state_selectors:" + string.Join(",", recognized.Select(d => d.ControllerId)), recognized[0].ControllerId, recognized[0].ControllerName);
        return first!;
    }

    private static Detection Analyze(int id, string? name, string code, IReadOnlyDictionary<int, JsonObject> objects, JsonArray? dependencies,
        JsonObject? properties)
    {
        // 选择器在观测里写过 visible 的目标：同名图层时 getLayer 取到的就是这一个。
        var writtenByController = (dependencies ?? []).OfType<JsonObject>()
            .Where(d => HybridScenePlanner.Int(d["owner"]) == id && d["operation"]?.GetValue<string>() == "write" &&
                d["property"]?.GetValue<string>() == "visible")
            .Select(d => HybridScenePlanner.Int(d["target"])).OfType<int>().ToHashSet();
        if (!HoursRead.IsMatch(code)) return Fallback("clock_value_not_hour_branches", id, name);
        if (LiveInput.IsMatch(code)) return Fallback("reads_other_live_input", id, name);
        if (MethodCall.Match(code) is { Success: true } call) return Fallback("calls_playback_method:" + call.Groups[1].Value, id, name);
        string[] writes = PropertyWrite.Matches(code).Select(m => m.Groups[1].Value).Distinct().ToArray();
        if (writes.Length == 0) return Fallback("writes_no_visibility", id, name);
        if (writes.Any(property => property != "visible")) return Fallback("writes_non_visibility:" + string.Join(",", writes.Where(p => p != "visible")), id, name);
        // 组：变量名 -> 图层名列表；名字按场景 name 精确对应到唯一图层。
        var groups = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (Match match in GroupArray.Matches(code))
            groups[match.Groups[1].Value] = StringLiteral.Matches(match.Groups[2].Value)
                .Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).ToArray();
        if (groups.Count == 0) return Fallback("no_layer_name_groups", id, name);
        var byName = objects.GroupBy(pair => pair.Value["name"]?.GetValue<string>() ?? "")
            .ToDictionary(g => g.Key, g => g.Select(pair => pair.Key).ToArray(), StringComparer.Ordinal);
        var groupIds = new Dictionary<string, int[]>(StringComparer.Ordinal);
        foreach (var (group, names) in groups)
        {
            var ids = new List<int>();
            foreach (string layerName in names)
            {
                if (!byName.TryGetValue(layerName, out int[]? matches) || matches.Length == 0) return Fallback("unknown_layer_name:" + layerName, id, name);
                if (matches.Length > 1)
                {
                    int[] written = matches.Where(writtenByController.Contains).ToArray();
                    if (written.Length != 1) return Fallback("ambiguous_layer_name:" + layerName, id, name);
                    ids.Add(written[0]);
                    continue;
                }
                ids.Add(matches[0]);
            }
            groupIds[group] = ids.ToArray();
        }
        // 阈值：数字字面量，或脚本里 `name = 4` 形式的默认值（用户属性可改的阈值按默认值枚举，来源记为 script_defaults）。
        var numbers = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Match match in NumberAssignment.Matches(code))
            numbers.TryAdd(match.Groups[1].Value, int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture));
        // 阈值变量与壁纸属性同名（模板里 applyUserProperties 把属性抄进同名变量）时，按当前快照属性的值枚举：
        // 用户在 Wallpaper Engine 里改过的时段要如实反映。属性值可能是数字也可能是数字字符串。
        bool usedDefaults = false, usedProperties = false;
        int? Threshold(string token)
        {
            if (int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out int literal)) return literal;
            if (properties?[token] is JsonValue property)
            {
                string text = property.TryGetValue<string>(out string? s) ? s : property.ToJsonString();
                if (int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int fromProperty))
                { usedProperties = true; return fromProperty; }
            }
            if (!numbers.TryGetValue(token, out int value)) return null;
            usedDefaults = true;
            return value;
        }
        var branches = new List<(int Low, int High, string Group)>();
        string? hourVariable = null;
        foreach (Match match in HourBranch.Matches(code))
        {
            hourVariable ??= match.Groups[1].Value;
            if (match.Groups[1].Value != hourVariable) return Fallback("mixed_branch_variables", id, name);
            if (Threshold(match.Groups[2].Value) is not int low) return Fallback("unresolved_threshold:" + match.Groups[2].Value, id, name);
            if (Threshold(match.Groups[3].Value) is not int high) return Fallback("unresolved_threshold:" + match.Groups[3].Value, id, name);
            if (!groupIds.ContainsKey(match.Groups[5].Value)) return Fallback("branch_shows_unknown_group:" + match.Groups[5].Value, id, name);
            branches.Add((low, high, match.Groups[5].Value));
        }
        if (branches.Count == 0) return Fallback("no_hour_branches", id, name);
        string? fallbackGroup = null;
        var elseMatch = ElseBranch.Match(code);
        if (elseMatch.Success)
        {
            if (!groupIds.ContainsKey(elseMatch.Groups[2].Value)) return Fallback("else_shows_unknown_group:" + elseMatch.Groups[2].Value, id, name);
            fallbackGroup = elseMatch.Groups[2].Value;
        }
        // 逐小时求值，按脚本分支顺序取第一个命中的组；相邻同组合并成一个状态，跨午夜的段并成同一状态。
        string?[] perHour = new string?[24];
        for (int hour = 0; hour < 24; ++hour)
            perHour[hour] = branches.FirstOrDefault(b => hour >= b.Low && hour < b.High) is { Group: not null } hit ? hit.Group : fallbackGroup;
        var segments = new List<(string? Group, int Start, int End)>();
        for (int hour = 0; hour < 24; ++hour)
        {
            if (segments.Count > 0 && segments[^1].Group == perHour[hour] && segments[^1].End == hour)
                segments[^1] = (perHour[hour], segments[^1].Start, hour + 1);
            else segments.Add((perHour[hour], hour, hour + 1));
        }
        var states = new List<State>();
        foreach (var segment in segments)
        {
            string stateName = StateName(segment.Group);
            int[] visible = segment.Group is null ? [] : groupIds[segment.Group];
            int index = states.FindIndex(state => state.Name == stateName);
            if (index >= 0) states[index] = states[index] with { Hours = [.. states[index].Hours, [segment.Start, segment.End]] };
            else states.Add(new State(stateName, [[segment.Start, segment.End]], visible));
        }
        if (states.Count < 2) return Fallback("single_state", id, name);
        int[] controlled = groupIds.Values.SelectMany(ids => ids).Distinct().OrderBy(layer => layer).ToArray();
        return new Detection(Recognized, id, name, states.ToArray(), controlled, null,
            usedProperties ? "user_properties" : usedDefaults ? "script_defaults" : "literals");
    }

    /// <summary>
    /// 录制副本按状态冻结：plan 带 daytime_state 时，选择器的 visible 绑定去掉脚本（录制时不再按真实时钟切层），
    /// 该状态的受控层 visible 写死为 true；其余受控层已由 omitted 机制从录制副本删除。只动录制副本，成品工程另从源解包，原脚本保留。
    /// </summary>
    internal static void ApplyState(JsonObject scene, JsonObject plan)
    {
        if (plan["settings"]?["daytime_state"] is not JsonValue stateValue || !stateValue.TryGetValue<string>(out string? stateName)) return;
        if (plan["daytime_split"] is not JsonObject split || split["status"]?.GetValue<string>() != Recognized) return;
        int controller = split["controller_layer_id"]!.GetValue<int>();
        var visible = (split["states"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(s => s["name"]?.GetValue<string>() == stateName)
            ?["visible_layer_ids"]?.AsArray().Select(n => n!.GetValue<int>()).ToHashSet()
            ?? throw new InvalidDataException("The plan's daytime state is not listed in its daytime_split section.");
        foreach (JsonObject obj in scene["objects"]!.AsArray().OfType<JsonObject>())
        {
            int id = obj["id"]!.GetValue<int>();
            if (id == controller && obj["visible"] is JsonObject binding) binding.Remove("script");
            if (visible.Contains(id)) obj["visible"] = true;
        }
    }

    private static string StateName(string? group)
    {
        if (group is null) return "none";
        string trimmed = Regex.Replace(group, @"(?i)layers?$", "");
        return (trimmed.Length == 0 ? group : trimmed).ToLowerInvariant();
    }
}
