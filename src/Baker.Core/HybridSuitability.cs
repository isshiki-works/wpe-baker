using System.Globalization;
using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>
/// 把"这次分析到底能不能烘"讲成一句人话。纯函数：只读已经写完的 plan，不碰渲染器，也不改 plan 里任何既有字段。
/// 三条 not_suitable 规则一律以"这次没有产出任何循环候选、也没有可用的 effect_prefix 缓存"为前提，
/// 因此对任何能产出候选的壁纸零影响。判据只来自解析事实：依赖闭包后的视频组数，以及求解器返回的结构化空候选原因；
/// 不含 live 层占比、层总数、交织实时层数这类经验阈值——实测这些量在成品与被拒案之间完全重叠。
/// 裁定只改变"怎么说"，不改变"能不能烘"：blockers、route、status 一律原样保留，用户仍可自行决定继续。
/// </summary>
internal static class HybridSuitability
{
    /// <summary>能力缺口 blocker 的识别前缀。只用于分诊，不改写、不删除任何 blocker 原文。</summary>
    private const string HdrCapturePrefix = "HDR intermediate compositing is not supported";
    private const string PerspectiveCapturePrefix = "Perspective capture needs an explicit screen-space composition";

    internal static JsonObject Verdict(JsonObject plan)
    {
        var loop = plan["loop"] as JsonObject;
        var reason = loop?["no_candidate_reason"] as JsonObject;
        string? reasonKind = Text(reason?["kind"]);
        var layers = plan["layers"] as JsonArray;
        int candidates = (loop?["candidates"] as JsonArray)?.Count ?? 0;
        int prefixCaches = (plan["effect_prefix_caches"] as JsonArray)?.Count ?? 0;
        bool prefixRoute = Text(plan["route"]) == "effect_prefix";
        int groups = (plan["video_groups"] as JsonArray)?.Count ?? 0;
        // 未解机制数：字段缺了就当"不知道"（-1），不许把不知道当成零。
        int unresolved = (loop?["unresolved"] as JsonArray)?.Count ?? -1;
        int totalLayers = layers?.Count ?? 0;
        int videoLayers = layers?.OfType<JsonObject>().Count(layer => Text(layer["allocation"]) == "video") ?? 0;
        string[] blockers = (plan["blockers"] as JsonArray)?.Select(node => Text(node)).OfType<string>().ToArray() ?? [];
        string? layoutConflict = Text(plan["video_layout_admission"]?["reason"]) ?? Text(plan["whole_layer"]?["layout_conflict"]);
        bool hdr = blockers.Any(item => item.StartsWith(HdrCapturePrefix, StringComparison.Ordinal));
        bool perspective = blockers.Any(item => item.StartsWith(PerspectiveCapturePrefix, StringComparison.Ordinal));

        // 能力缺口与布局选择只进 notes：它们说的是工具或设置，不是这张壁纸的结论。
        var notes = new JsonArray();
        if (hdr) notes.Add("Capture capability gap: HDR intermediate compositing is outside the current RGBA8 group capture.");
        if (perspective) notes.Add("Capture capability gap: perspective projection needs an explicit screen-space composition.");
        if (layoutConflict is not null) notes.Add("Layout choice pending: " + layoutConflict);
        // 措辞只敢断言"没有已证明的时间机制"，所以把三项可核对的计数一并附上，用户可以自己复核是不是分析漏检。
        if (reason is not null)
            notes.Add($"Evidence counts on the layers bound for the video: {Count(reason["shader_component_count"])} shader period component(s), " +
                $"{Count(reason["runtime_period_count"])} traced runtime animation period(s), " +
                $"{Count(reason["runtime_clock_uniform_count"])} runtime clock uniform(s).");

        bool noCandidateAtAll = candidates == 0 && prefixCaches == 0 && !prefixRoute;

        // S1：依赖闭包之后连一个视频组都没有，没有任何东西可烘。
        if (noCandidateAtAll && groups == 0)
            return Build("not_suitable", "nothing_to_bake",
                "Nothing here can be pre-rendered: after dependency closure no visible layer is left for a video — every one of them reads the real clock, the mouse or a live script.",
                "这张壁纸没有可以预渲染的部分：依赖闭包之后没有任何可见图层能进视频——它们全都在读真实时钟、鼠标或实时脚本。", notes);

        // S3：不可调速分量的公共帧步长已经跨过求解器上限，上限内确定无解。
        if (noCandidateAtAll && reasonKind == nameof(CommonLoopNoCandidateKind.FixedPeriodExceedsCeiling))
        {
            string ceiling = Fmt(Number(reason?["ceiling_seconds"]));
            double periodSeconds = Number(reason?["fixed_period_seconds"]);
            double hours = periodSeconds / 3600;
            string periodEn = hours >= 1 ? Fmt(hours) + " hours" : Fmt(periodSeconds) + " seconds";
            string periodZh = hours >= 1 ? Fmt(hours) + " 小时" : Fmt(periodSeconds) + " 秒";
            string tracks = Count(reason?["runtime_period_count"]);
            string budget = Fmt(loop?["retime_budget_percent"] is JsonValue value && value.TryGetValue<double>(out double percent) ? percent : 2);
            return Build("not_suitable", "fixed_period_exceeds_loop_ceiling",
                $"The layers bound for the video carry {tracks} authored tracks whose lengths only line up again after {periodEn}; " +
                $"no loop within the {ceiling}-second ceiling can close this scene, and the ±{budget}% retime budget cannot bridge that gap.",
                $"要进视频的图层上有 {tracks} 条手工动画轨道，它们的时长要 {periodZh}才重新对齐；" +
                $"{ceiling} 秒上限内不存在能闭合的循环，±{budget}% 调速也补不上这个差距。", notes);
        }

        // S2：可烘集合非空，但集合上没有任何已证明的时间机制，而且证不出它是静态的——烘出来是一张静止画面。
        // 必须同时没有任何未解机制：只要还剩一条没解开，"没有运动"就只是分析没看懂，不能拿来下裁定。
        if (noCandidateAtAll && unresolved == 0 && reasonKind == nameof(CommonLoopNoCandidateKind.NoTemporalMechanism) &&
            loop?["source_static"] is JsonValue staticValue && staticValue.TryGetValue<bool>(out bool sourceStatic) && !sourceStatic)
            return videoLayers >= totalLayers
                ? Build("not_suitable", "no_temporal_mechanism_in_video",
                    $"All {totalLayers} layer(s) here can be pre-rendered but none of them changes over time, " +
                    "so the video would be a still image; baking would save nothing.",
                    $"这张壁纸的全部 {totalLayers} 层都能预渲染，但它们没有任何随时间变化的机制：" +
                    "烘出来的视频就是一张静止画面，烘焙省不下任何东西。", notes)
                : Build("not_suitable", "no_temporal_mechanism_in_video",
                    $"Only {videoLayers} of {totalLayers} layers can be pre-rendered and none of them changes over time, " +
                    "so the video would be a still image while everything that moves stays live; baking would save nothing.",
                    $"{totalLayers} 层里只有 {videoLayers} 层能预渲染，而这些层没有任何随时间变化的机制：" +
                    "烘出来的视频就是一张静止画面，会动的部分照样实时运行，烘焙省不下任何东西。", notes);

        // 能力缺口是工具的问题，不是壁纸的问题，单独一档。
        if (hdr || perspective)
            return Build("unsupported_capture", "capture_capability_gap",
                "This scene needs a capture path the tool does not have yet (HDR intermediate compositing and/or perspective projection); that is a gap in the tool, not a verdict on the wallpaper.",
                "这张壁纸需要工具目前还没有的采集能力（HDR 中间合成与/或透视投影）：这是工具的能力缺口，不是壁纸本身不行。", notes);

        if (blockers.Length > 0)
            return Build("requires_user_choice", "blockers_need_a_decision",
                $"Analysis left {blockers.Length} blocker(s) for you to resolve before baking; none of them says this wallpaper cannot be baked.",
                $"分析留下 {blockers.Length} 条需要你先决定的事项；它们都不代表这张壁纸不能烘。", notes);

        if (candidates > 0 || prefixCaches > 0)
            return Build("suitable", "loop_candidates_available",
                $"Analysis produced {(candidates > 0 ? candidates + " loop candidate(s)" : prefixCaches + " effect-prefix cache(s)")} and left no blockers.",
                $"分析产出了 {(candidates > 0 ? candidates + " 个循环候选" : prefixCaches + " 份 effect_prefix 缓存")}，没有遗留事项。", notes);

        // 既没候选也没可判定的不适合理由：说清楚是这次没解出来，不冒充壁纸不行。
        return unresolved > 0
            ? Build("requires_user_choice", "loop_not_established",
                $"Analysis established no loop: {unresolved} temporal mechanism(s) in this scene are still unexplained, so this run cannot say whether it closes; a different setting or a hand-picked period is needed before baking.",
                $"分析没能确立循环：这张壁纸上还有 {unresolved} 条没解开的时间机制，所以这一次无法断定它能不能闭合；要烘的话需要换设置重新分析，或者人工指定周期。", notes)
            : Build("requires_user_choice", "loop_not_established",
                "Analysis established no loop and reported no blocker, so this run cannot say the scene is unsuitable; a different setting or a hand-picked period is needed before baking.",
                "分析既没确立循环，也没有留下可判定的理由，所以这一次不能断言这张壁纸不适合：要烘的话需要换设置重新分析，或者人工指定周期。", notes);
    }

    private static JsonObject Build(string verdict, string rule, string reasonEnglish, string reasonChinese, JsonArray notes) => new()
    {
        ["verdict"] = verdict, ["rule"] = rule, ["reason_en"] = reasonEnglish, ["reason_zh"] = reasonChinese, ["notes"] = notes
    };

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out string? text) ? text : null;

    /// <summary>
    /// 读一个数：plan 可能是刚构造出来的对象（int/double 各自装箱），也可能是从磁盘解析回来的（JsonElement 支撑），
    /// 两条路径都要读得出来，所以逐个类型试，别只试 double。
    /// </summary>
    private static double Number(JsonNode? node)
    {
        if (node is not JsonValue value) return 0;
        if (value.TryGetValue(out double number)) return number;
        if (value.TryGetValue(out long integer)) return integer;
        if (value.TryGetValue(out int small)) return small;
        return value.TryGetValue(out decimal exact) ? (double)exact : 0;
    }

    private static string Count(JsonNode? node) => Fmt(Number(node));

    private static string Fmt(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
