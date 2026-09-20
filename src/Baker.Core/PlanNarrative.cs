using System.Globalization;
using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>
/// 把已经算好的分析结果讲成人话：为文案准备参数、给 plan 挂上 <c>blockers_localized</c> /
/// <c>unresolved_localized</c> / <c>summary</c>。这里不做任何判定，只读 plan 已有的字段。
/// </summary>
public static class PlanNarrative
{
    public const string Bakeable = "bakeable";
    public const string Blocked = "blocked";
    public const string NotApplicable = "not_applicable";
    public const string Unknown = "unknown";

    /// <summary>无独立组时只描述依赖分析的结果，并列出相关证据；单层输入不能证明整幅画面的构成。</summary>
    public static string NoInputIndependentGroup(IReadOnlyDictionary<int, JsonObject> objects,
        IReadOnlyDictionary<int, HashSet<string>> reasons)
    {
        (string Reason, string Mechanism)[] known = [
            ("wall_clock_api", "wall-clock"),
            ("observed_wall_clock", "wall-clock"),
            ("active_shader_audio_spectrum", "audio-spectrum"),
            ("active_shader_pointer_input", "pointer-input"),
            ("active_shader_parallax_input", "parallax-input"),
            ("reads_current_framebuffer", "framebuffer-feedback")];
        var evidence = new List<string>();
        foreach (var (id, layerReasons) in reasons.OrderBy(entry => entry.Key))
        {
            string[] mechanisms = known.Where(item => layerReasons.Contains(item.Reason)).Select(item => item.Mechanism).Distinct().ToArray();
            if (mechanisms.Length > 0)
                evidence.Add("\"" + Messages.EscapeName(objects.GetValueOrDefault(id)?["name"]?.GetValue<string>()) + "\" (" + string.Join(", ", mechanisms) + ")");
        }
        return evidence.Count == 0 ? Messages.Emit("blocker.no_input_independent_group_generic")
            : Messages.Emit("blocker.no_input_independent_group", string.Join("; ", evidence));
    }

    /// <summary>
    /// 文案③：SDR 辐射闭合证明不成立时的拒绝理由。<paramref name="unproven"/> 是
    /// <see cref="SdrRadianceClosure"/> 点名的失败层与判据（英文，下游脚本按它对账），
    /// 同时把壁纸自带的 HDR 属性指出来——关掉它就可能过关。
    /// </summary>
    /// <remarks><paramref name="unprovenZh"/> 是同一份明细的中文版；不给时中文通道沿用英文明细（旧调用方）。</remarks>
    public static string HdrRadianceOpen(JsonObject scene, JsonObject? project, string unproven, string? unprovenZh = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        // 只有 general.hdr 本身绑定了属性，关掉那个属性才会让 hdr 变成 false、判据不再运行。
        // 名字里带 "hdr" 的其它属性（bloomrequiredforhdr、hdrstrength……）绑的是 bloom 或别的字段，
        // 关掉后 hdr 仍是 true、判据照样不通过（3695791724 实测），所以不按名字猜开关。
        var binding = BoundProperty(scene["general"]?["hdr"]);
        string chinese = unprovenZh ?? unproven;
        if (binding is null)
            return Messages.EmitBilingual("blocker.hdr_radiance_open", [chinese], [unproven]);
        string name = Messages.EscapeName(binding.Value.Name);
        string label = project?["general"]?["properties"]?[binding.Value.Name]?["text"]?.GetValue<string>() is string text
            && !string.IsNullOrWhiteSpace(text) ? "\"" + Messages.EscapeName(text) + " / " + name + "\"" : "\"" + name + "\"";
        // 关闭值：无 condition 的绑定直接取属性值当 hdr，false 即关；带 condition 的绑定只在属性值等于
        // condition 时 hdr 为 true，给它任何别的值都是关。
        string offZh, offEn;
        if (binding.Value.Condition is string condition)
        {
            string quotedCondition = "\"" + Messages.EscapeName(condition) + "\"";
            offZh = $"只要 \"{name}\" 不等于 {quotedCondition} 就是关，--properties 文件里给它另一个值";
            offEn = $"it is off whenever \"{name}\" is not {quotedCondition}; give it another value in the --properties file";
        }
        else
        {
            string json = "{\"" + name + "\": false}";
            offZh = $"关闭值 false，--properties 文件写 {json}";
            offEn = $"the off value is false; write {json} in the --properties file";
        }
        return Messages.EmitBilingual("blocker.hdr_radiance_open_property", [chinese, label, offZh], [unproven, label, offEn]);
    }

    /// <summary>
    /// 属性绑定写成 {"user": "名字"} 或 {"user": {"name": "名字", "condition": "值"}} 两种形态；
    /// 返回属性名与（可选的）condition，不是绑定时返回 null。
    /// </summary>
    internal static (string Name, string? Condition)? BoundProperty(JsonNode? node)
    {
        if (node is not JsonObject binding || binding["user"] is not { } user) return null;
        if (user is JsonValue text && text.TryGetValue<string>(out string? name)) return (name, null);
        if (user["name"]?.GetValue<string>() is not string named) return null;
        string? condition = user["condition"] is JsonValue value
            ? (value.TryGetValue<string>(out string? literal) ? literal : value.ToJsonString()) : null;
        return (named, condition);
    }

    /// <summary>
    /// 文案①：全屏模式拿不到不透明底。legacy 英文逐字不变，双语版另说清楚 N=1 透明组这一真实原因。
    /// <paramref name="leadingNames"/> 是排在第一组视频之前先画的可见实时层，只用于 N=1 时点名挡在前面的层。
    /// </summary>
    public static string FullFrameConflict(JsonArray groups, IReadOnlyList<string> interleavedNames,
        (string Zh, string En)? options = null, IReadOnlyList<string>? leadingNames = null)
    {
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(interleavedNames);
        int count = groups.Count, live = interleavedNames.Count;
        // legacy 专用中段：层名不截断，逐字复现历史输出。
        string legacySegment = live == 0 ? "" : " Interleaved realtime layers: " + string.Join(", ", interleavedNames) + ".";
        string names = Messages.NameList(interleavedNames);
        // 调用方给了本场景可执行的选项时走这一条：分类与双语仍来自文案表，选项由全幅降级的取证提供。
        if (options is { } choices)
        {
            // 这一形态的 legacy 中段沿用全幅降级的写法：层名最多列五个，其余以 and N more 收尾。
            const int limit = 5;
            string legacyListed = live == 0 ? "" : " Interleaved realtime layers: " +
                string.Join(", ", interleavedNames.Take(limit)) + (live > limit ? $" and {live - limit} more" : "") + ".";
            // 按实际状态分支，不把"透明背景"写成与"切成 N 块"并列的备选。
            string optionsKey;
            string phraseZh, phraseEn;
            if (count == 0)
            {
                optionsKey = "blocker.fullframe_no_video_group_options";
                phraseZh = phraseEn = "";
            }
            else if (count == 1)
            {
                optionsKey = "blocker.fullframe_single_transparent_group_options";
                string[] leading = (leadingNames ?? []).Where(name => !string.IsNullOrWhiteSpace(name)).Distinct().ToArray();
                string leadingList = Messages.NameList(leading);
                phraseZh = leading.Length == 0 ? "" : $"：它前面还有实时图层 {leadingList} 先画，视频若带上清屏就会把它们盖掉";
                phraseEn = leading.Length == 0 ? "" : $": realtime layers {leadingList} draw before it, and a video carrying the scene clear would paint over them";
            }
            else if (live > 0)
            {
                optionsKey = "blocker.fullframe_needs_opaque_group_options";
                phraseZh = $"{names}（共 {live} 个）。";
                phraseEn = $" {names} ({live} in total).";
            }
            else
            {
                optionsKey = "blocker.fullframe_split_groups_options";
                phraseZh = phraseEn = "";
            }
            return Messages.EmitBilingual(optionsKey,
                [count, phraseZh, choices.Zh, legacyListed, choices.En],
                [count, phraseEn, choices.En, legacyListed, choices.En]);
        }
        bool singleTransparent = count == 1 && groups[0]?["transparent"]?.GetValue<bool>() == true;
        string key = count == 0 ? "blocker.fullframe_no_video_group"
            : singleTransparent ? live > 0 ? "blocker.fullframe_single_transparent_group_live" : "blocker.fullframe_single_transparent_group"
            : live > 0 ? "blocker.fullframe_needs_opaque_group" : "blocker.fullframe_needs_opaque_group_no_live";
        return Messages.Emit(key, count, live, names, legacySegment);
    }

    /// <summary>给 plan 挂上双语字段与一行结论。现有英文字段一律不动。</summary>
    public static void Attach(JsonObject report)
    {
        report["blockers_localized"] = Messages.LocalizeAll(report["blockers"] as JsonArray);
        if (report["loop"] is JsonObject loop) loop["unresolved_localized"] = LocalizeUnresolved(loop["unresolved"] as JsonArray);
        if (report["whole_layer"] is JsonObject whole)
        {
            whole["blockers_localized"] = Messages.LocalizeAll(whole["blockers"] as JsonArray);
            if (whole["loop"] is JsonObject wholeLoop)
                wholeLoop["unresolved_localized"] = LocalizeUnresolved(wholeLoop["unresolved"] as JsonArray);
        }
        foreach (JsonObject preflight in (report["effect_prefix_hardware_decode_preflight"] as JsonArray ?? []).OfType<JsonObject>())
            preflight["unresolved_localized"] = LocalizeUnresolved(preflight["unresolved"] as JsonArray);
        report["summary"] = Summarize(report);
        // 取舍清单要读 blockers_localized 与 summary.key，所以排在它们之后；它只读 plan，不改任何判定。
        TradeoffOptions.Attach(report);
        if (report["summary"] is JsonObject line && line["verdict"]?.GetValue<string>() != "not_suitable" &&
            TradeoffOptions.SummarySentence(report) is { } tradeoff)
        {
            line["zh"] = line["zh"]?.GetValue<string>() + tradeoff.Zh;
            line["en"] = line["en"]?.GetValue<string>() + " " + tradeoff.En;
        }
        // 保留实时把会动的全留实时、只烘一帧背景时，先说清这条路不省电，并指向分层视频（协调者 2026-09-18）。
        if (report["summary"] is JsonObject staticLine &&
            staticLine["key"]?.GetValue<string>()?.StartsWith("summary.bakeable_static", StringComparison.Ordinal) == true)
        {
            staticLine["zh"] = staticLine["zh"]?.GetValue<string>() + Messages.Get("summary.static_only_route", Messages.Chinese);
            staticLine["en"] = staticLine["en"]?.GetValue<string>() + " " + Messages.Get("summary.static_only_route", Messages.English);
            staticLine["prefer_layered"] = true;
        }
        // 效果前缀路线的省电提醒：只把效果链前缀烘成视频、图层本身仍然实时跑，省电幅度和"被烘走的那段
        // 占多少工作量"直接挂钩（雷欧 −73%，但 2026-09-18 的 5 个重型 effect_prefix 案只有 +0.3% ~ −15%）。
        // 只在能烘的结论后面补，拒绝类结论不加。
        if (report["route"]?.GetValue<string>() == "effect_prefix" && report["summary"] is JsonObject prefixLine &&
            prefixLine["key"]?.GetValue<string>()?.StartsWith("summary.bakeable", StringComparison.Ordinal) == true)
        {
            prefixLine["zh"] = prefixLine["zh"]?.GetValue<string>() + Messages.Get("summary.effect_prefix_limited_saving", Messages.Chinese);
            prefixLine["en"] = prefixLine["en"]?.GetValue<string>() + " " + Messages.Get("summary.effect_prefix_limited_saving", Messages.English);
        }
    }

    private static JsonArray LocalizeUnresolved(JsonArray? unresolved) =>
        new((unresolved ?? []).OfType<JsonObject>().Select(item => {
            var localized = Messages.Localize(item["detail"]?.GetValue<string>());
            if (item["kind"] is JsonNode kind) localized["kind"] = kind.DeepClone();
            if (item["owner_layer_id"] is JsonNode owner) localized["owner_layer_id"] = owner.DeepClone();
            if (item["track_name"] is JsonNode track) localized["track_name"] = track.DeepClone();
            return (JsonNode)localized;
        }).ToArray());

    /// <summary>
    /// 一行结论：verdict 加中英文各一句。这是用户唯一保证会读到的输出。
    /// verdict 以 plan 里的 suitability 裁定为准（它读同一份 plan，但判据更细：S1/S2/S3 与能力缺口分档）；
    /// plan 还没有 suitability 时回退到这里按 blockers/candidates 得出的粗分类。
    /// </summary>
    public static JsonObject Summarize(JsonObject report)
    {
        JsonObject summary = Narrate(report);
        if (report["suitability"]?["verdict"] is JsonValue verdictValue &&
            verdictValue.TryGetValue(out string? verdict) && !string.IsNullOrWhiteSpace(verdict))
            summary["verdict"] = verdict;
        // 输出分辨率与来源接在结论后面：只有新版 analyze 写了 output_resolution 的 plan 才有这半句。
        if (ResolutionSentence(report) is { } sentence)
        {
            summary["zh"] = summary["zh"]?.GetValue<string>() + sentence.Zh;
            summary["en"] = summary["en"]?.GetValue<string>() + " " + sentence.En;
        }
        // 属性来源再接一句：按 WPE 设置且确有不同于默认的项、或想读 WPE 却没读到时才说；与默认一致时结论逐字不变。
        if (PropertiesSentence(report) is { } properties)
        {
            summary["zh"] = summary["zh"]?.GetValue<string>() + properties.Zh;
            summary["en"] = summary["en"]?.GetValue<string>() + " " + properties.En;
        }
        return summary;
    }

    /// <summary>"按你在 Wallpaper Engine 里的属性设置烘焙（N 项与默认不同）。"或读不到时的回退说明；其余情况返回 null。</summary>
    internal static (string Zh, string En)? PropertiesSentence(JsonObject report)
    {
        if (report["properties_source"] is not JsonValue sourceValue || !sourceValue.TryGetValue(out string? source)) return null;
        if (source == WallpaperEngineProperties.SourceWpe)
        {
            int differing = WallpaperEngineProperties.DifferingCount(report) ?? 0;
            return differing == 0 ? null
                : (Messages.Get("summary.properties_from_wpe", Messages.Chinese, differing), Messages.Get("summary.properties_from_wpe", Messages.English, differing));
        }
        if (source != WallpaperEngineProperties.SourceUnavailable) return null;
        string reason = report["wpe_properties"]?["reason"] is JsonValue reasonValue && reasonValue.TryGetValue(out string? code) ? code : "";
        string key = Messages.Find("properties.reason." + reason) is null ? "properties.reason.unknown" : "properties.reason." + reason;
        return (Messages.Get("summary.properties_wpe_unavailable", Messages.Chinese, Messages.Get(key, Messages.Chinese)),
            Messages.Get("summary.properties_wpe_unavailable", Messages.English, Messages.Get(key, Messages.English)));
    }

    /// <summary>"按场景画布铺满本机屏幕所需的 3200×1800 烘焙（画布 3840×2160，屏幕 2880×1800）。"一类的半句；plan 没有 output_resolution 时返回 null。</summary>
    internal static (string Zh, string En)? ResolutionSentence(JsonObject report)
    {
        if (report["output_resolution"] is not JsonObject resolution || Number(resolution["width"]) is not double width ||
            Number(resolution["height"]) is not double height || resolution["source"] is not JsonValue sourceValue ||
            !sourceValue.TryGetValue(out string? source)) return null;
        string extent = Extent(width, height);
        string? canvas = resolution["scene_canvas"] is JsonArray { Count: 2 } pair && Number(pair[0]) is double canvasWidth &&
            Number(pair[1]) is double canvasHeight ? Extent(canvasWidth, canvasHeight) : null;
        string? display = resolution["display"] is JsonArray { Count: 2 } screen && Number(screen[0]) is double displayWidth &&
            Number(screen[1]) is double displayHeight ? Extent(displayWidth, displayHeight) : null;
        bool capped = resolution["capped_at_canvas"] is JsonValue cappedValue && cappedValue.TryGetValue(out bool isCapped) && isCapped;
        string key = source switch
        {
            OutputResolution.SceneCanvasFitDisplay => capped ? "summary.resolution_canvas_fit_display_capped" : "summary.resolution_canvas_fit_display",
            OutputResolution.SceneCanvas => "summary.resolution_scene_canvas",
            OutputResolution.Display => "summary.resolution_display",
            OutputResolution.Fallback => "summary.resolution_fallback",
            _ => canvas is not null && canvas != extent ? "summary.resolution_explicit_canvas" : "summary.resolution_explicit"
        };
        object?[] args = [extent, canvas, display];
        return (Messages.Get(key, Messages.Chinese, args), Messages.Get(key, Messages.English, args));
        static string Extent(double width, double height) =>
            width.ToString("0.###", CultureInfo.InvariantCulture) + "×" + height.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static JsonObject Narrate(JsonObject report)
    {
        if (report["suitability"] is JsonObject suitability &&
            suitability["rule"]?.GetValue<string>() == "no_independent_content_after_reallocation")
            return Bilingual(Blocked, "summary.not_suitable_current",
                [suitability["reason_zh"]!.GetValue<string>()], [suitability["reason_en"]!.GetValue<string>()]);
        var blockers = report["blockers"] as JsonArray ?? [];
        if (blockers.Count > 0)
        {
            JsonObject first = Messages.Localize(blockers[0]?.GetValue<string>());
            return Bilingual(Blocked, "summary.blocked",
                [first["zh"]?.GetValue<string>() ?? "", blockers.Count], [first["en"]?.GetValue<string>() ?? "", blockers.Count]);
        }
        if (FirstCandidate(report) is JsonObject candidate) return Bakeable_(report, candidate);
        if (LoopUnresolved(report) is JsonObject specific) return specific;
        if (FirstReason(report) is not string reason) return Verdict(Unknown, "summary.unknown_no_reason");
        JsonObject localized = Messages.Localize(reason);
        return Bilingual(Unknown, "summary.unknown_with_reason",
            [localized["zh"]?.GetValue<string>() ?? reason], [localized["en"]?.GetValue<string>() ?? reason]);
    }

    private static JsonObject Bakeable_(JsonObject report, JsonObject candidate)
    {
        string liveNames = Messages.NameList(LiveLayerNames(report));
        bool hasLive = liveNames.Length > 0;
        double frames = Number(candidate["frames"]) ?? 0;
        JsonObject summary;
        if (frames == 1)
            summary = hasLive ? Verdict(Bakeable, "summary.bakeable_static", liveNames) : Verdict(Bakeable, "summary.bakeable_static_no_live");
        else
        {
            double seconds = Number(candidate["seconds"]) ?? 0;
            double retime = Number(candidate["total_retime_cost_percent"]) ?? 0;
            string fps = FramesPerSecond(report);
            object?[] shared = [seconds.ToString("0.0", CultureInfo.InvariantCulture), frames.ToString("0", CultureInfo.InvariantCulture), fps,
                retime.ToString("0.00", CultureInfo.InvariantCulture), liveNames];
            summary = hasLive ? Verdict(Bakeable, "summary.bakeable", shared) : Verdict(Bakeable, "summary.bakeable_no_live", shared);
        }
        // 摆动改频成立时结论行补一句改了多少、冻结了什么；开关关闭的计划没有这个字段，结论逐字不变。
        // 静止候选也补：护栏之后它只可能是摆动项全部是慢项、冻结后速度偏差都在上限内，冻结了什么得说出来。
        if (candidate["sway_retime"] is JsonObject swayRetime)
            foreach (string language in new[] { Messages.Chinese, Messages.English })
            {
                summary[language] = summary[language]!.GetValue<string>() + (language == Messages.Chinese ? "" : " ") + SwayRetimeLine(swayRetime, language);
            }
        // 循环长度不是任何周期定出来的（只有平稳随机粒子）：结论行说明按几秒循环、接缝靠交叉淡化。
        if (Text(candidate["loop_length_source"]) == "stationary_particle_default")
        {
            string length = (Number(candidate["seconds"]) ?? 0).ToString("0.###", CultureInfo.InvariantCulture);
            foreach (string language in new[] { Messages.Chinese, Messages.English })
                summary[language] = summary[language]!.GetValue<string>() + (language == Messages.Chinese ? "" : " ") +
                    Messages.Get("summary.particle_default_loop", language, length);
        }
        // 循环长度上限被内嵌视频 2 GiB 收紧时说明"该分辨率下最长约 x 秒"。上限对所有周期分量共用，所以不论改频开关，
        // 只要选中的是整层循环候选就说；循环记录上没有时回退读改频记录（旧 plan 只在那里记）。
        if (ReferenceEquals(candidate.Parent, report["loop"]?["candidates"]) &&
            (report["loop"]?["embedded_video_limit"] ?? report["loop"]?["sway_retime"]?["embedded_video_limit"]) is JsonObject limitRecord)
            foreach (string language in new[] { Messages.Chinese, Messages.English })
                if (EmbeddedVideoLimitLine(limitRecord, language) is string limit)
                    summary[language] = summary[language]!.GetValue<string>() + (language == Messages.Chinese ? "" : " ") + limit;
        return summary;
    }

    /// <summary>plan 里 embedded_video_limit 记录收紧过（applied）时的说明句；没收紧或字段不全时为 null。</summary>
    public static string? EmbeddedVideoLimitLine(JsonObject? limit, string language)
    {
        if (limit?["applied"] is not JsonValue applied || !applied.TryGetValue(out bool yes) || !yes) return null;
        if (Number(limit["requested_loop_length_maximum_seconds"]) is not double requested || Number(limit["fit_seconds"]) is not double fit ||
            Number(limit["encoded_width"]) is not double width || Number(limit["encoded_height"]) is not double height) return null;
        return Messages.Get("summary.embedded_video_limit", language, width.ToString("0", CultureInfo.InvariantCulture),
            height.ToString("0", CultureInfo.InvariantCulture), FramesPerSecond(new JsonObject { ["settings"] = new JsonObject {
                ["fps_numerator"] = limit["fps_numerator"]?.DeepClone(), ["fps_denominator"] = limit["fps_denominator"]?.DeepClone() } }),
            fit.ToString("0", CultureInfo.InvariantCulture), requested.ToString("0.###", CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// "一个循环内相位最多偏 x 圈、最慢可见项走 n 圈（改频 y%，预算 z%）；慢项峰值速度偏差最大 w 像素/秒，其中冻结 m 项"一句
    /// （结论行与 bake 的 stderr 共用）。相位差才是观感排序量，百分比只是求解参数。
    /// </summary>
    public static string SwayRetimeLine(JsonObject retime, string language)
    {
        ArgumentNullException.ThrowIfNull(retime);
        var (drift, cycles, visible, deviation, frozen) = SwayRecurrenceSolver.SummaryNumbers(retime);
        string seconds = Number(retime["seconds"]) is double length ? length.ToString("0.##", CultureInfo.InvariantCulture) : "?";
        // 预算是求解参数：质量档与旧 plan 没有预算，讲"按改动最小求解"。
        string budget = Number(retime["retime_budget_percent"]) is double percent
            ? Messages.Get("summary.sway_budget", language, percent.ToString("0.###", CultureInfo.InvariantCulture))
            : Messages.Get("summary.sway_budget_minimized", language);
        return Messages.Get("summary.sway_retime", language, drift, cycles ?? "?", visible, budget, deviation ?? "?", frozen, seconds);
    }

    private static JsonObject Verdict(string verdict, string key, params object?[] args) => Bilingual(verdict, key, args, args);

    /// <summary>中英各带一套参数：嵌进结论里的那半句本身就是分语言的。</summary>
    private static JsonObject Bilingual(string verdict, string key, object?[] chineseArgs, object?[] englishArgs) => new() {
        ["verdict"] = verdict, ["key"] = key,
        ["zh"] = Messages.Get(key, Messages.Chinese, chineseArgs), ["en"] = Messages.Get(key, Messages.English, englishArgs) };

    /// <summary>整幅候选优先，其次特效前缀候选。</summary>
    private static JsonObject? FirstCandidate(JsonObject report)
    {
        if ((report["loop"]?["candidates"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault() is { } whole) return whole;
        return (report["effect_prefix_caches"] as JsonArray)?.OfType<JsonObject>()
            .Select(cache => (cache["loop"]?["candidates"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault())
            .FirstOrDefault(candidate => candidate is not null);
    }

    /// <summary>补充分析"更小的烘焙分配"写进 loop.unresolved 的那条记录；它是出路说明，不是时间机制。</summary>
    private const string AllocationFallbackKind = "loop_allocation_fallback";

    /// <summary>
    /// 没有候选、没有阻断、但 loop.unresolved 里有具体时间机制：说清几处、首条是什么、补充分析给了什么出路。
    /// 满足平稳随机判据的粒子项不算"证明不了周期"，不计数、不当首条，只另起半句说明。
    /// 只读 plan 已有字段（loop.unresolved、loop.no_candidate_reason、loop_allocation_fallback、layers），不改任何判定。
    /// </summary>
    private static JsonObject? LoopUnresolved(JsonObject report)
    {
        JsonObject[] items = (report["loop"]?["unresolved"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(item => Text(item["kind"]) != AllocationFallbackKind).ToArray();
        if (items.Length == 0) return null;
        var names = LayerNames(report);
        JsonObject[] mechanisms = items.Where(item => !StationaryParticle(item)).ToArray();
        // 摆动改频开着、摆动项全部建了模型时，摆动项只是在等其余分量先解出候选，本身能被改频接住；
        // 首条让给真正挡路的机制。只调整首条的挑选顺序，处数与判定不变。
        if (report["loop"]?["sway_retime"] is JsonObject swayRetime && swayRetime["enabled"] is JsonValue enabled &&
            enabled.TryGetValue(out bool swayOn) && swayOn && Number(swayRetime["sway_items"]) is double swayItems && swayItems > 0 &&
            Number(swayRetime["modeled_items"]) == swayItems)
            mechanisms = [.. mechanisms.OrderBy(item => Text(item["mechanism"]) == ShaderPeriodAnalysis.FoliageSwayMechanism ? 1 : 0)];
        // 同一粒子层可能有多条精灵轨道项，按层计数。
        string[] stationaryLayers = items.Where(StationaryParticle)
            .Select(item => Number(item["owner_layer_id"]) is double id ? names.GetValueOrDefault((int)id) ?? ((int)id).ToString(CultureInfo.InvariantCulture) : "?")
            .Distinct().ToArray();
        var fallback = report["loop_allocation_fallback"] as JsonObject;
        string? status = Text(fallback?["status"]);
        var (nextZh, nextEn) = NextStep(fallback, status);
        if (mechanisms.Length == 0)
        {
            string reasonKey = Text(report["loop"]?["no_candidate_reason"]?["kind"]) == nameof(CommonLoopNoCandidateKind.NoTemporalMechanism)
                ? "summary.stationary_only_no_period_source" : "summary.stationary_only_no_candidate";
            string listed = Messages.NameList(stationaryLayers);
            return Bilingual(Unknown, "summary.loop_unresolved_stationary_only",
                [stationaryLayers.Length, listed, Messages.Get(reasonKey, Messages.Chinese), nextZh],
                [stationaryLayers.Length, listed, Messages.Get(reasonKey, Messages.English), nextEn]);
        }
        var (firstZh, firstEn) = DescribeUnresolved(mechanisms[0], names, Number(report["loop"]?["maximum_seconds"]));
        // 首条说明嵌在括号里，去掉它自带的句末标点。
        firstZh = firstZh.TrimEnd('。', ' ');
        firstEn = firstEn.TrimEnd('.', ' ');
        object?[] note = [stationaryLayers.Length, Messages.NameList(stationaryLayers)];
        string noteZh = stationaryLayers.Length == 0 ? "" : Messages.Get("summary.stationary_particles_note", Messages.Chinese, note);
        string noteEn = stationaryLayers.Length == 0 ? "" : Messages.Get("summary.stationary_particles_note", Messages.English, note) + " ";
        string LayerList(IEnumerable<int> ids) => Messages.NameList(ids.Select(id => names.GetValueOrDefault(id) ?? id.ToString(CultureInfo.InvariantCulture)));
        int[] retained = Ids(fallback?["retain_live_root_ids"]);
        if (status == "candidate_found" && retained.Length > 0)
        {
            string ids = string.Join(",", retained);
            return Bilingual(Unknown, "summary.loop_unresolved_retain_live",
                [mechanisms.Length, firstZh, LayerList(retained), ids, noteZh], [mechanisms.Length, firstEn, LayerList(retained), ids, noteEn]);
        }
        // 重查只被全幅布局挡住、冲突给出了再保留哪些根：照它给完整的 --retain-live 列表，并如实说明这个分配还没重查过。
        if (status == "still_unavailable" && retained.Length > 0 && fallback?["replanned_retain_live_suggestion"] is JsonObject suggestion &&
            Ids(suggestion["retain_live_root_ids"]) is { Length: > 0 } suggested && Ids(suggestion["added_live_root_ids"]) is { Length: > 0 } added)
        {
            string ids = string.Join(",", suggested);
            return Bilingual(Unknown, "summary.loop_unresolved_retain_live_full_frame",
                [mechanisms.Length, firstZh, LayerList(retained), LayerList(added), ids, noteZh],
                [mechanisms.Length, firstEn, LayerList(retained), LayerList(added), ids, noteEn]);
        }
        return Bilingual(Unknown, "summary.loop_unresolved",
            [mechanisms.Length, firstZh, noteZh + nextZh], [mechanisms.Length, firstEn, noteEn + nextEn]);
    }

    /// <summary>这一条是满足平稳随机判据的粒子项：它没有周期，但接缝可以淡化替换，不是"证明不了周期"的机制。</summary>
    private static bool StationaryParticle(JsonObject item) =>
        item["particle_stationarity"]?["stationary"] is JsonValue flag && flag.TryGetValue(out bool stationary) && stationary;

    /// <summary>按补充分析的结局选下一步说明（中英各一句）。</summary>
    private static (string Zh, string En) NextStep(JsonObject? fallback, string? status)
    {
        (string key, object?[] args) = status switch {
            "still_unavailable" when fallback?["replanned_blockers"] is JsonArray { Count: > 0 } blockers =>
                ("summary.next_still_unavailable_blocked", new object?[] { blockers.Count, "loop_allocation_fallback.replanned_blockers" }),
            "still_unavailable" => ("summary.next_still_unavailable", Array.Empty<object?>()),
            "not_applicable" => ("summary.next_not_applicable", Array.Empty<object?>()),
            "failed" => ("summary.next_failed", new object?[] { "loop_allocation_fallback" }),
            _ => ("summary.next_see_unresolved", new object?[] { "loop.unresolved_localized" }) };
        return (Messages.Get(key, Messages.Chinese, args), Messages.Get(key, Messages.English, args));
    }

    private static int[] Ids(JsonNode? node) =>
        (node as JsonArray ?? []).Select(Number).OfType<double>().Select(id => (int)id).ToArray();

    /// <summary>
    /// 一条未解析机制的一句话说明。文案表认得的直接用双语文案；认不得的（着色器周期分析、静止证明等只有英文明细的）
    /// 按 kind 与结构化字段给中文说明，英文明细原样留给英文通道与 plan.json，不进中文。
    /// </summary>
    internal static (string Zh, string En) DescribeUnresolved(JsonObject item, IReadOnlyDictionary<int, string> names, double? ceilingSeconds = null)
    {
        string detail = Text(item["detail"]) ?? "";
        JsonObject localized = Messages.Localize(detail);
        if (localized["key"] is not null)
            return (localized["zh"]?.GetValue<string>() ?? detail, localized["en"]?.GetValue<string>() ?? detail);
        string kind = Text(item["kind"]) ?? "";
        string? owner = Number(item["owner_layer_id"]) is double id
            ? "\"" + Messages.EscapeName(names.GetValueOrDefault((int)id) ?? ((int)id).ToString(CultureInfo.InvariantCulture)) + "\"" : null;
        string effect = EffectName(Text(item["resource"]));
        string where = owner is null ? "" : effect.Length > 0 ? $"图层 {owner} 上的 {effect} 特效" : $"图层 {owner}";
        string zh = kind switch {
            "NonPeriodicOrDriftingMechanism" => (where.Length > 0 ? where : "一个着色器特效") + "在循环时长上限" +
                (ceilingSeconds is double ceiling ? $"（{ceiling.ToString("0.###", CultureInfo.InvariantCulture)} 秒）" : "") + "内回不到起点",
            "UnsupportedShaderMechanism" => (where.Length > 0 ? where : "一个着色器特效") + "不符合任何已验证的周期公式",
            "ResourceUnavailable" => (where.Length > 0 ? where : "一个着色器特效") + "读不到着色器源码",
            "MissingOrInvalidSpeed" => (where.Length > 0 ? where : "一个着色器特效") + "的速度参数缺失或不合法",
            "source_static" => SourceStaticChinese(detail),
            "runtime_animation" when Text(item["particle_nonperiodic_reason"]) is string particle =>
                (owner is null ? "一个粒子系统" : $"图层 {owner} 的粒子系统") + "证明不了周期" + ParticleReasonChinese(particle),
            "runtime_animation" => (owner is null ? "一条运行时动画" : $"图层 {owner} 的运行时动画") + "证明不了周期",
            "runtime_video" => (owner is null ? "一段视频" : $"图层 {owner} 的视频") + "证明不了周期",
            "runtime_material" => (owner is null ? "一个运行时材质" : $"图层 {owner} 的运行时材质") + "用到了未建模的时间变量",
            "solver" or "search_budget" => "各分量的公共周期没有在求解范围内找到",
            _ => "有一处时间机制证明不了周期，英文明细见 plan.json 的 loop.unresolved" };
        return (zh, detail);
    }

    /// <summary>ParticleInputAnalysis 记下的粒子非周期原因代号 → 中文括注；认不出的代号不加括注。</summary>
    private static string ParticleReasonChinese(string reason) => reason switch {
        "particle_definition_unavailable" => "（读不到粒子定义，排除不了随机源）",
        "particle_audio_input" => "（响应实时音频）",
        "particle_nonperiodic_turbulent_velocity" => "（受湍流噪声场驱动）",
        "particle_nonperiodic_random_frame" => "（精灵动画随机起帧）",
        "particle_nonperiodic_random_initializer" => "（每个粒子的初始值随机抽取）",
        "particle_nonperiodic_emitter_extent" => "（出生位置随机抽取）",
        "particle_effective_period_not_modelled" => "（有效周期尚未建模）",
        _ => "" };

    /// <summary>静止证明失败的明细以 Baked layer N "名字" 开头；认不出就给通用说明。</summary>
    private static string SourceStaticChinese(string detail)
    {
        var match = System.Text.RegularExpressions.Regex.Match(detail, "^Baked layer \\d+ \"(?<name>[^\"]*)\"",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        string layer = match.Success ? $"被烘的图层 \"{Messages.EscapeName(match.Groups["name"].Value)}\" " : "有一个被烘的图层";
        return detail.Contains("contains a particle system", StringComparison.Ordinal)
            ? layer + "含有粒子系统，既证明不了循环也证明不了静止"
            : layer + "证明不了是静止画面";
    }

    /// <summary>"shaders/effects/lightshafts.frag + shaders/effects/lightshafts.vert" → "lightshafts"。</summary>
    private static string EffectName(string? resource)
    {
        if (string.IsNullOrWhiteSpace(resource)) return "";
        string first = resource.Split([" + ", " / "], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
        return Messages.EscapeName(Path.GetFileNameWithoutExtension(first), 24);
    }

    private static Dictionary<int, string> LayerNames(JsonObject report)
    {
        var names = new Dictionary<int, string>();
        foreach (var layer in (report["layers"] as JsonArray ?? []).OfType<JsonObject>())
            if (Number(layer["id"]) is double id && Text(layer["name"]) is string name && !string.IsNullOrWhiteSpace(name))
                names.TryAdd((int)id, name);
        return names;
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    /// <summary>unknown 时把 plan 里任何一条现成的理由带出来，没有就说内部未给出理由。</summary>
    private static string? FirstReason(JsonObject report)
    {
        string?[] candidates = [
            (report["loop"]?["unresolved"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault()?["detail"]?.GetValue<string>(),
            report["whole_layer"]?["layout_conflict"]?.GetValue<string>(),
            report["video_layout_admission"]?["reason"]?.GetValue<string>(),
            report["allocation"]?["reason"]?.GetValue<string>(),
            report["source_script_error_evidence"]?["reason"]?.GetValue<string>()];
        return candidates.FirstOrDefault(text => !string.IsNullOrWhiteSpace(text));
    }

    private static string[] LiveLayerNames(JsonObject report)
    {
        var names = new Dictionary<double, string?>();
        foreach (var layer in (report["layers"] as JsonArray ?? []).OfType<JsonObject>())
            if (Number(layer["id"]) is double id) names.TryAdd(id, layer["name"]?.GetValue<string>());
        return (report["live_layer_ids"] as JsonArray ?? []).Select(id => Number(id))
            .Where(id => id is not null).Select(id => names.GetValueOrDefault(id!.Value))
            .Where(name => !string.IsNullOrWhiteSpace(name)).Cast<string>().ToArray();
    }

    /// <summary>JsonNode 的数字类型是写进去时定的；这里一律按宽松读法取，读不到就是 null。</summary>
    private static double? Number(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue(out double number) && double.IsFinite(number)) return number;
        if (value.TryGetValue(out long integer)) return integer;
        if (value.TryGetValue(out ulong unsigned)) return unsigned;
        if (value.TryGetValue(out int small)) return small;
        if (value.TryGetValue(out uint pixels)) return pixels;
        return null;
    }

    private static string FramesPerSecond(JsonObject report)
    {
        double numerator = Number(report["settings"]?["fps_numerator"]) ?? 0;
        double denominator = Number(report["settings"]?["fps_denominator"]) ?? 0;
        if (numerator <= 0 || denominator <= 0) return "?";
        double fps = numerator / denominator;
        return fps == Math.Floor(fps) ? fps.ToString("0", CultureInfo.InvariantCulture) : fps.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
