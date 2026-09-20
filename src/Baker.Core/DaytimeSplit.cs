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
///   - 对图层的属性写只有 <c>.visible =</c>；播放控制仅支持完整匹配的按索引切视频模板。
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
    // 先去掉注释及字符串以外的空白，再匹配整个模板。只允许选中项 play、其余 pause，
    // 不能把 play/pause 从上面的通用拒绝规则中删掉，也不能只检查几个局部片段。
    private static readonly Regex IndexedVideoSelector = new("""
        \A (?:'use\x20strict'|"use\x20strict");?
        (?:var|let|const)(?<layers>\w+)=(?<array>\[(?:"[^"\\]*"|'[^'\\]*')(?:,(?:"[^"\\]*"|'[^'\\]*'))*\]);
        (?:var|let|const)(?<manual>\w+)=(?<manualDefault>false|true|\d{1,2});
        (?:var|let|const)(?<enabled>\w+)=(?<enabledDefault>false|true);
        (?:var|let|const)(?<defaults>\w+=\d{1,2}(?:,\w+=\d{1,2})*);
        exportfunctioninit\(\)\{
            \k<layers>=\k<layers>\.map\((?<mapper>\w+)=>thisScene\.getLayer\(\k<mapper>\)\);
        \}
        (?:var|let|const)(?<select>\w+)=function\((?<number>\w+)\)\{
            \k<layers>\.forEach\(\((?<video>\w+),(?<index>\w+)\)=>\{
                if\(\k<index>===\k<number>\)\{
                    \k<video>\.getVideoTexture\(\)\.play\(\);\k<video>\.visible=true;
                \}else\{
                    \k<video>\.getVideoTexture\(\)\.pause\(\);\k<video>\.visible=false;
                \}
            \}\);
        \};?
        exportfunctionupdate\(\)\{
            (?:var|let|const)(?<date>\w+)=newDate\(\);
            (?:var|let|const)(?<hour>\w+)=\k<date>\.getHours\(\);
            if\(\k<enabled>\)\{(?<branches>.+?)\}
            if\(\k<manual>&&!\k<enabled>\)\{
                for\(let(?<loop>\w+)=0;\k<loop><\k<layers>\.length;\k<loop>\+\+\)\{
                    if\(\k<loop>==\k<manual>\)\k<select>\(\k<loop>\);
                \}
            \}
        \}
        exportfunctionapplyUserProperties\((?<properties>\w+)\)\{(?<bindings>.+)\}
        \z
        """, Options | RegexOptions.IgnorePatternWhitespace);

    /// <summary>一个状态：名字、覆盖的小时段（[起, 止) 可跨午夜拆成多段）、该状态下选择器置为可见的受控层。</summary>
    internal sealed record State(string Name, int[][] Hours, int[] VisibleLayerIds);

    internal sealed record VideoSelection(string ModeProperty, string ManualProperty, string[] PropertyKeys, int[] LayerIds);

    /// <summary>识别结果：status=recognized 时 ControllerId、States、ControlledLayerIds 有效；否则 FallbackReason 说明退回原因。</summary>
    internal sealed record Detection(string Status, int? ControllerId, string? ControllerName, State[] States, int[] ControlledLayerIds,
        string? FallbackReason, string? ThresholdSource, bool ControlsVideoPlayback = false, VideoSelection? Selection = null)
    {
        internal bool IsRecognized => Status == Recognized;

        internal State? StateNamed(string name) => States.FirstOrDefault(state => state.Name == name);

        internal JsonObject ToJson() => new()
        {
            ["status"] = Status,
            ["controller_layer_id"] = ControllerId,
            ["controller_name"] = ControllerName,
            ["threshold_source"] = ThresholdSource,
            ["controls_video_playback"] = ControlsVideoPlayback,
            ["video_selection"] = Selection is null ? null : new JsonObject {
                ["mode_property"] = Selection.ModeProperty, ["manual_property"] = Selection.ManualProperty,
                ["property_keys"] = JsonSerializer.SerializeToNode(Selection.PropertyKeys),
                ["layer_ids"] = JsonSerializer.SerializeToNode(Selection.LayerIds) },
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
        JsonObject? properties, bool controlsVideoPlayback = false)
    {
        // 选择器在观测里写过 visible 的目标：同名图层时 getLayer 取到的就是这一个。
        var writtenByController = (dependencies ?? []).OfType<JsonObject>()
            .Where(d => HybridScenePlanner.Int(d["owner"]) == id && d["operation"]?.GetValue<string>() == "write" &&
                d["property"]?.GetValue<string>() == "visible")
            .Select(d => HybridScenePlanner.Int(d["target"])).OfType<int>().ToHashSet();
        if (!HoursRead.IsMatch(code)) return Fallback("clock_value_not_hour_branches", id, name);
        if (LiveInput.IsMatch(code)) return Fallback("reads_other_live_input", id, name);
        if (MethodCall.Match(code) is { Success: true } call)
            return AnalyzeIndexedVideoSelector(id, name, code, objects, dependencies, properties)
                ?? Fallback("calls_playback_method:" + call.Groups[1].Value, id, name);
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
        if (states.Count < 2 && !controlsVideoPlayback) return Fallback("single_state", id, name);
        int[] controlled = groupIds.Values.SelectMany(ids => ids).Distinct().OrderBy(layer => layer).ToArray();
        return new Detection(Recognized, id, name, states.ToArray(), controlled, null,
            usedProperties ? "user_properties" : usedDefaults ? "script_defaults" : "literals", controlsVideoPlayback);
    }

    private static Detection? AnalyzeIndexedVideoSelector(int id, string? name, string code,
        IReadOnlyDictionary<int, JsonObject> objects, JsonArray? dependencies, JsonObject? properties)
    {
        string compact = Regex.Replace(code, "\"(?:\\\\.|[^\"\\\\])*\"|'(?:\\\\.|[^'\\\\])*'|\\s+",
            match => char.IsWhiteSpace(match.Value[0]) ? "" : match.Value);
        Match template = IndexedVideoSelector.Match(compact);
        if (!template.Success) return null;
        Detection Reject(string reason) => Fallback(reason, id, name);
        string Part(string key) => template.Groups[key].Value;
        if (SceneAnalyzer.Walk(objects[id]).OfType<JsonObject>().Count(node => node.ContainsKey("script")) != 1)
            return Reject("multiple_controller_scripts");
        Match[] assignments = NumberAssignment.Matches(Part("defaults")).ToArray();
        if (assignments.Select(match => match.Groups[1].Value).Distinct().Count() != assignments.Length)
            return Reject("overlapping_selector_variables");
        var defaults = assignments
            .ToDictionary(match => match.Groups[1].Value, match => (JsonNode)JsonValue.Create(int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture))!);
        if (defaults.ContainsKey(Part("manual")) || defaults.ContainsKey(Part("enabled")) || Part("manual") == Part("enabled"))
            return Reject("overlapping_selector_variables");
        defaults.Add(Part("manual"), JsonNode.Parse(Part("manualDefault"))!);
        defaults.Add(Part("enabled"), JsonNode.Parse(Part("enabledDefault"))!);
        string[] globals = [Part("layers"), Part("select"), .. defaults.Keys];
        if (globals.Distinct(StringComparer.Ordinal).Count() != globals.Length ||
            new[] { "date", "hour", "properties", "number", "video", "index", "loop", "mapper" }.Any(key => globals.Contains(Part(key))) ||
            Part("date") == Part("hour") || Part("video") == Part("index") ||
            Part("video") == Part("number") || Part("index") == Part("number"))
            return Reject("overlapping_selector_variables");

        string propertyParameter = Regex.Escape(Part("properties"));
        var bindingPattern = new Regex("if\\(" + propertyParameter + @"\.hasOwnProperty\((?<quote>['""])(?<key>\w+)\k<quote>\)\)\{(?<variable>\w+)=" + propertyParameter + @"\.\k<key>;\}", Options);
        Match[] bindings = bindingPattern.Matches(Part("bindings")).ToArray();
        if (bindings.Sum(match => match.Length) != Part("bindings").Length || bindings.Length != defaults.Count ||
            bindings.Select(match => match.Groups["variable"].Value).Distinct().Count() != defaults.Count ||
            bindings.Any(match => !defaults.ContainsKey(match.Groups["variable"].Value)))
            return Reject("unsupported_selector_properties");
        bool usedProperties = false;
        foreach (Match binding in bindings)
            if (properties?[binding.Groups["key"].Value] is { } value)
            {
                defaults[binding.Groups["variable"].Value] = value;
                usedProperties = true;
            }
        if (defaults[Part("enabled")] is not JsonValue enabledValue || !enabledValue.TryGetValue<bool>(out bool enabled))
            return Reject("non_boolean_time_mode");

        Match[] layerLiterals = StringLiteral.Matches(Part("array")).ToArray();
        string[] stateNames = layerLiterals.Select(match => (match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value).ToLowerInvariant()).ToArray();
        if (stateNames.Any(string.IsNullOrWhiteSpace) || stateNames.Distinct().Count() != stateNames.Length)
            return Reject("ambiguous_video_state_names");
        string Group(int index) => "video" + index.ToString(CultureInfo.InvariantCulture) + "Layers";
        // 已完整验证的模板归一化到现有组解析器；这里只生成数据，不执行源脚本。
        string normalized = "new Date().getHours(); layer.visible=true;\n" + string.Join("\n",
            layerLiterals.Select((literal, index) => "var " + Group(index) + "=[" + literal.Value + "];").ToArray());
        int? Integer(JsonNode value)
        {
            string text = value is JsonValue scalar && scalar.TryGetValue<string>(out string? textValue) ? textValue : value.ToJsonString();
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ? parsed : null;
        }
        if (!enabled)
        {
            JsonNode selection = defaults[Part("manual")];
            if (Integer(selection) is not int selected || selected < 0 || selected >= layerLiterals.Length)
                return Reject("unresolved_manual_selection");
            // JS 的字符串 "0" 为真，数字 0 为假；假值下模板不会调用选择函数。
            if (selected == 0 && selection.GetValueKind() != JsonValueKind.String)
                return Reject("inactive_manual_selection");
            normalized += $"if(h>=0&&h<24){{show({Group(selected)});}}";
        }
        else
        {
            string hour = Regex.Escape(Part("hour")), select = Regex.Escape(Part("select"));
            string branch = @"if\(" + hour + @"(?<lowOp>>=?)(?<low>\w+)&&" + hour + @"(?<highOp><=?)(?<high>\w+)\)\{" + select + @"\((?<index>\d{1,2})\);\}";
            Match chain = Regex.Match(Part("branches"), @"\A" + branch + "(?:else" + branch + @")*else\{" + select + @"\((?<fallback>\d{1,2})\);\}\z", Options);
            if (!chain.Success) return Reject("unsupported_video_hour_branches");
            int? Threshold(string token) => int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out int literal) ? literal
                : defaults.TryGetValue(token, out JsonNode? value) ? Integer(value) : null;
            for (int i = 0; i < chain.Groups["index"].Captures.Count; ++i)
            {
                int index = int.Parse(chain.Groups["index"].Captures[i].Value, CultureInfo.InvariantCulture);
                if (index >= layerLiterals.Length) return Reject("video_index_out_of_range");
                if (Threshold(chain.Groups["low"].Captures[i].Value) is not int low ||
                    Threshold(chain.Groups["high"].Captures[i].Value) is not int high || low is < 0 or > 24 || high is < 0 or > 24)
                    return Reject("unresolved_video_hour_threshold");
                if (chain.Groups["lowOp"].Captures[i].Value == ">") ++low;
                if (chain.Groups["highOp"].Captures[i].Value == "<=") ++high;
                normalized += $"{(i == 0 ? "" : "else ")}if(h>={low}&&h<{high}){{show({Group(index)});}}";
            }
            int fallback = int.Parse(chain.Groups["fallback"].Value, CultureInfo.InvariantCulture);
            if (fallback >= layerLiterals.Length) return Reject("video_index_out_of_range");
            normalized += $"else{{show({Group(fallback)});}}";
        }
        Detection result = Analyze(id, name, normalized, objects, dependencies, null, controlsVideoPlayback: true);
        if (!result.IsRecognized) return result;
        if (result.ControlledLayerIds.Length != layerLiterals.Length || result.ControlledLayerIds.Contains(id))
            return Reject("repeated_or_self_video_target");
        if (result.ControlledLayerIds.Any(target => objects[target]["visible"] is JsonObject visible &&
            (visible.ContainsKey("script") || visible.ContainsKey("animation") || visible.ContainsKey("animations"))))
            return Reject("controlled_visibility_has_script_or_animation");
        var namesByGroup = Enumerable.Range(0, stateNames.Length).ToDictionary(index => StateName(Group(index)), index => stateNames[index]);
        int[] indexedIds = layerLiterals.Select(literal => objects.Single(pair => result.ControlledLayerIds.Contains(pair.Key) &&
            pair.Value["name"]?.GetValue<string>() == (literal.Groups[1].Success ? literal.Groups[1].Value : literal.Groups[2].Value)).Key).ToArray();
        return result with {
            States = result.States.Select(state => state with { Name = namesByGroup[state.Name] }).ToArray(),
            ThresholdSource = usedProperties ? "user_properties" : "script_defaults",
            Selection = new VideoSelection(
                bindings.Single(binding => binding.Groups["variable"].Value == Part("enabled")).Groups["key"].Value,
                bindings.Single(binding => binding.Groups["variable"].Value == Part("manual")).Groups["key"].Value,
                bindings.Select(binding => binding.Groups["key"].Value).Distinct().ToArray(), indexedIds) };
    }

    internal sealed record DynamicExport(Detection Detection, State State, Dictionary<string, int> ReplacementTargets)
    {
        internal string[] PropertyKeys => Detection.Selection!.PropertyKeys;

        internal JsonObject ComparisonProperties(JsonObject snapshot)
        {
            var properties = snapshot.DeepClone().AsObject();
            properties[Detection.Selection!.ModeProperty] = false;
            // 字符串 "0" 必须保持为真，才能在模板的手动分支选择第一项。
            properties[Detection.Selection.ManualProperty] = Array.IndexOf(Detection.Selection.LayerIds, State.VisibleLayerIds.Single())
                .ToString(CultureInfo.InvariantCulture);
            return properties;
        }

        internal void BindReplacement(JsonObject replacement, JsonObject original, bool isStatic)
        {
            if (isStatic) throw new InvalidDataException("A dynamic daytime replacement must remain a playable video texture.");
            if (HybridScenePlanner.Int(replacement["parent"]) != HybridScenePlanner.Int(original["parent"]))
                throw new InvalidDataException("A dynamic daytime replacement must preserve its source parent.");
            replacement["id"] = original["id"]!.DeepClone();
            replacement["name"] = original["name"]!.DeepClone();
            replacement["visible"] = original["visible"]?.DeepClone() ?? JsonValue.Create(true);
        }
    }

    /// <summary>
    /// 最小动态装配：每组只替换一个受控叶子视频，原 ID/名称/父级和其余原对象全部保留。
    /// 控制脚本无需改写，未替换时段（包括手动第五项）继续使用源视频；不接受互斥图层混组。
    /// </summary>
    internal static DynamicExport? PrepareDynamicExport(IReadOnlyDictionary<int, JsonObject> objects, JsonObject plan, JsonArray dependencies)
    {
        if (plan["settings"]?["daytime_state"] is not JsonValue selected || !selected.TryGetValue<string>(out string? stateName) || stateName is null ||
            plan["daytime_split"]?["controls_video_playback"]?.GetValue<bool>() != true) return null;
        Detection detection = Detect(objects, dependencies, plan["snapshot_properties"] as JsonObject);
        if (!detection.IsRecognized || !detection.ControlsVideoPlayback || detection.Selection is null ||
            detection.StateNamed(stateName) is not State state || state.VisibleLayerIds.Length != 1)
            throw new InvalidDataException("Dynamic daytime export requires one unambiguous selected video in a fully recognized selector.");
        if ((plan["excluded_layer_ids"] as JsonArray)?.Count > 0 || (plan["omitted_snapshot_layer_ids"] as JsonArray)?.Count > 0 ||
            plan["occlusion_tradeoff"]?["status"]?.GetValue<string>() == "applied")
            throw new InvalidDataException("Dynamic daytime export cannot restore omitted layers or change the original draw order.");
        var targets = new Dictionary<string, int>(StringComparer.Ordinal);
        var seen = new HashSet<int>();
        foreach (JsonObject group in (plan["video_groups"] as JsonArray ?? []).OfType<JsonObject>())
        {
            int[] ids = (group["layer_ids"] as JsonArray ?? []).Select(node => node!.GetValue<int>()).ToArray();
            if (ids.Length != 1 || !state.VisibleLayerIds.Contains(ids[0]) || !seen.Add(ids[0]))
                throw new InvalidDataException("Dynamic daytime export requires one selected controlled source per video group; mixed or repeated groups are not supported.");
            int target = ids[0];
            if (!objects.TryGetValue(target, out JsonObject? original) || original["image"] is null ||
                objects.Values.Any(obj => HybridScenePlanner.Int(obj["parent"]) == target) ||
                HybridScenePlanner.Int(group["parent_id"]) != HybridScenePlanner.Int(original["parent"]))
                throw new InvalidDataException("Dynamic daytime export requires a leaf image layer with its original parent.");
            if (SceneAnalyzer.Walk(original).OfType<JsonObject>().Any(node => node.ContainsKey("script") ||
                !ReferenceEquals(node, original["visible"]) && PlanNarrative.BoundProperty(node) is { } property &&
                detection.Selection.PropertyKeys.Contains(property.Name, StringComparer.Ordinal)))
                throw new InvalidDataException("The replacement's drawing or scripts also depend on the preserved daytime controls.");
            foreach (JsonObject dependency in dependencies.OfType<JsonObject>().Where(dependency => HybridScenePlanner.Int(dependency["target"]) == target &&
                dependency["operation"]?.GetValue<string>() is "read" or "write"))
            {
                string? operation = dependency["operation"]?.GetValue<string>(), property = dependency["property"]?.GetValue<string>();
                bool selectorAccess = HybridScenePlanner.Int(dependency["owner"]) == detection.ControllerId &&
                    dependency["binding"]?.GetValue<string>() == "visible" &&
                    (operation == "read" && property == "videoTexture" || operation == "write" && property == "visible");
                bool captureInitialization = HybridScenePlanner.Int(dependency["owner"]) == target &&
                    dependency["initialization"]?.GetValue<bool>() == true && operation == "read" && property == "videoTexture";
                if (!selectorAccess && !captureInitialization)
                    throw new InvalidDataException("Another script accesses the daytime replacement's source fields or resources.");
            }
            targets.Add(group["id"]!.GetValue<string>(), target);
        }
        if (targets.Count == 0) throw new InvalidDataException("Dynamic daytime export has no mapped video replacement.");
        return new DynamicExport(detection, state, targets);
    }

    internal static JsonArray AssembleDynamic(IReadOnlyDictionary<int, JsonObject> originals, DynamicExport export,
        IReadOnlyDictionary<string, JsonObject> replacements)
    {
        if (replacements.Count != export.ReplacementTargets.Count || export.ReplacementTargets.Keys.Any(key => !replacements.ContainsKey(key)))
            throw new InvalidDataException("The dynamic daytime replacement map is incomplete.");
        var bySource = export.ReplacementTargets.ToDictionary(pair => pair.Value, pair => replacements[pair.Key]);
        var result = new JsonArray();
        foreach (var (id, original) in originals)
        {
            JsonObject obj = bySource.TryGetValue(id, out JsonObject? replacement) ? replacement.DeepClone().AsObject() : original.DeepClone().AsObject();
            if (replacement is not null)
            {
                if (obj["id"]?.GetValue<int>() != id || obj["name"]?.GetValue<string>() != original["name"]?.GetValue<string>() ||
                    HybridScenePlanner.Int(obj["parent"]) != HybridScenePlanner.Int(original["parent"]))
                    throw new InvalidDataException("The daytime replacement lost its source identity or parent.");
                obj["visible"] = original["visible"]?.DeepClone() ?? JsonValue.Create(true);
            }
            result.Add(obj);
        }
        return result;
    }

    /// <summary>
    /// 视频解码器只为实际激活的层提供完整元数据。可静态证明的新模板先在观测副本上选择状态，
    /// 旧模板或需要运行时消歧的同名层返回 null，继续原有观测路径，不猜目标也不增加探测次数。
    /// </summary>
    internal static JsonObject? PrepareVideoObservation(JsonObject scene, JsonObject properties, string stateName)
    {
        Detection detection = Detect(scene["objects"]!.AsArray().OfType<JsonObject>().ToDictionary(obj => obj["id"]!.GetValue<int>()),
            properties: properties);
        if (!detection.IsRecognized || !detection.ControlsVideoPlayback || detection.StateNamed(stateName) is not State state) return null;
        JsonObject copy = scene.DeepClone().AsObject();
        HybridScenePlanner.FreezeTemporalProperties(copy, properties);
        ApplyState(copy, detection, state);
        return copy;
    }

    /// <summary>
    /// 录制副本按状态冻结：plan 带 daytime_state 时，选择器的 visible 绑定去掉脚本（录制时不再按真实时钟切层），
    /// 受控层 visible 按状态固定；视频模板保留仅启动/暂停当前图层的一次性初始化。
    /// 只动录制副本；成品工程仍需单独处理源控制器与生成视频之间的播放控制关系。
    /// </summary>
    internal static void ApplyState(JsonObject scene, JsonObject plan, bool forAnalysis = false)
    {
        if (plan["settings"]?["daytime_state"] is not JsonValue stateValue || !stateValue.TryGetValue<string>(out string? stateName)) return;
        if (plan["daytime_split"] is not JsonObject split || split["status"]?.GetValue<string>() != Recognized) return;
        int controller = split["controller_layer_id"]!.GetValue<int>();
        var controlled = (split["controlled_layer_ids"] as JsonArray ?? []).Select(node => node!.GetValue<int>()).ToHashSet();
        bool playback = split["controls_video_playback"]?.GetValue<bool>() == true;
        var visible = (split["states"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(s => s["name"]?.GetValue<string>() == stateName)
            ?["visible_layer_ids"]?.AsArray().Select(n => n!.GetValue<int>()).ToHashSet()
            ?? throw new InvalidDataException("The plan's daytime state is not listed in its daytime_split section.");
        ApplyState(scene, controller, controlled, visible, playback && !forAnalysis);
    }

    internal static void ApplyState(JsonObject scene, Detection? detection, State? state, bool forAnalysis = false)
    {
        if (detection?.IsRecognized != true || state is null) return;
        ApplyState(scene, detection.ControllerId!.Value, detection.ControlledLayerIds.ToHashSet(), state.VisibleLayerIds.ToHashSet(),
            detection.ControlsVideoPlayback && !forAnalysis);
    }

    private static void ApplyState(JsonObject scene, int controller, HashSet<int> controlled, HashSet<int> visible, bool playback)
    {
        // 分析视图不需要仅为启动捕获而生成的 init play/pause；保留它会被通用视频控制判据误当成动态控制。
        // 源里其他播放脚本和运行时控制证据不变，仍由既有判据处理。
        foreach (JsonObject obj in scene["objects"]!.AsArray().OfType<JsonObject>())
        {
            int id = obj["id"]!.GetValue<int>();
            if (id == controller && obj["visible"] is JsonObject binding) binding.Remove("script");
            if (!controlled.Contains(id)) continue;
            bool selected = visible.Contains(id);
            obj["visible"] = playback ? new JsonObject {
                ["value"] = selected,
                ["script"] = "export function init() { thisLayer.getVideoTexture()." + (selected ? "play" : "pause") + "(); }"
            } : JsonValue.Create(selected);
        }
    }

    private static string StateName(string? group)
    {
        if (group is null) return "none";
        string trimmed = Regex.Replace(group, @"(?i)layers?$", "");
        return (trimmed.Length == 0 ? group : trimmed).ToLowerInvariant();
    }
}
