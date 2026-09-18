using System.Globalization;
using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>
/// 视频外壳判据：判定"这份计划烘出来和原作在结构上是同一回事"。六条全部成立才算视频外壳——
/// 整层路线、周期分量全部来自 video 轨、没有未解析的时间机制、候选不需要调速、那段视频由唯一一层
/// 不透明整幅图层承载、被烘图层不含任何特效通道。任何一条不成立就说明存在可以被转进视频的逐帧计算，
/// 计划照常放行。
/// 六条全部是对计划与运行时观察里结构字段的布尔读取：不测量、不估算、不比阈值，也不写任何功耗结论。
/// </summary>
public static class VideoDominance
{
    /// <summary>判定成立：被烘内容与原作结构同构。</summary>
    public const string ShellStatus = "video_shell";

    /// <summary>判定不成立：有逐帧计算可以被转进视频。</summary>
    public const string NotShellStatus = "not_video_shell";

    /// <summary>判定成立但用户显式覆盖，证据原样留在计划里。</summary>
    public const string OverrideStatus = "override_accepted";

    /// <summary>特效前缀路线烘的正是特效通道本身，这条判据不适用。</summary>
    public const string EffectPrefixStatus = "not_applicable_to_effect_prefix";

    /// <summary>默认：判成视频外壳就写 blocker 拒绝。</summary>
    public const string RejectChoice = "reject";

    /// <summary>显式覆盖：仍然判定并记录，但不拦。</summary>
    public const string AllowChoice = "allow";

    /// <summary>判成视频外壳时追加进 plan.blockers 的那一条。</summary>
    public const string Blocker =
        "Baking cannot change how this wallpaper runs: every periodic component in this plan comes from one video track, " +
        "the baked layers carry no effect passes, and the result plays the same one video decode plus one full-screen blit " +
        "as the source. Nothing per-frame moves into the video. Bake it anyway with --video-shell allow if you want the " +
        "fixed loop or the repackaging for another reason.";

    /// <summary>同一条的中文原文；界面与命令行直接显示这一句，不翻译上面的英文。</summary>
    public const string BlockerZh =
        "烘焙改变不了这张壁纸的运行方式：计划里所有周期分量都来自同一段视频轨，被烘图层没有任何特效通道，" +
        "成品和原作一样是「一次视频解码 + 一次全屏贴图」，没有任何逐帧计算被转进视频。" +
        "若只是想要定长循环或者重新打包，用 --video-shell allow 显式覆盖。";

    private const string ScopeEn =
        "Structural identity between the source and the bake, read from the plan and the runtime observation alone. " +
        "No measurement, no estimate, no threshold.";

    private const string ScopeZh = "只读计划与运行时观察的结构字段，判的是「成品与原作同构」这一件事；不测量、不估算、不设阈值。";

    private const string OverrideReasonEn =
        "This plan is a video shell; baking was allowed explicitly with --video-shell allow and the evidence stays in the plan.";

    private const string OverrideReasonZh = "这份计划判定为视频外壳；用户已用 --video-shell allow 显式覆盖，判据证据原样留在计划里。";

    private const string EffectPrefixReasonEn =
        "Effect-prefix caching bakes the periodic effect passes themselves, so the video-shell test does not apply to this route.";

    private const string EffectPrefixReasonZh = "特效前缀路线烘的正是周期性的特效通道本身，视频外壳判据不适用于这条路线。";

    /// <summary>把命令行/界面上的两档名字校验一遍；非法值在这里就被挡下。</summary>
    public static string Choice(string value) => value is RejectChoice or AllowChoice ? value
        : throw new InvalidDataException("Video shell handling must be reject or allow.");

    /// <summary>
    /// 读计划与运行时观察，给出 video_dominant 记录。计划本身不在这里改动——追加 blocker 是调用方的事。
    /// </summary>
    public static JsonObject Evaluate(JsonObject plan, JsonObject runtime, string choice)
    {
        Choice(choice);
        var evidence = new JsonArray();
        string route = plan["route"]?.GetValue<string>() ?? "(absent)";
        evidence.Add("route=" + route);
        if (route != "whole_layer")
            return Record(EffectPrefixStatus, choice, evidence, EffectPrefixReasonEn, EffectPrefixReasonZh);
        var failure = Disqualify(plan, runtime, evidence);
        if (failure is not null) return Record(NotShellStatus, choice, evidence, failure.Value.En, failure.Value.Zh);
        return choice == AllowChoice
            ? Record(OverrideStatus, choice, evidence, OverrideReasonEn, OverrideReasonZh)
            : Record(ShellStatus, choice, evidence, Blocker, BlockerZh);
    }

    private static JsonObject Record(string status, string choice, JsonArray evidence, string reasonEnglish, string reasonChinese) =>
        new() { ["status"] = status, ["requested"] = choice, ["evidence"] = evidence,
            ["reason_en"] = reasonEnglish, ["reason_zh"] = reasonChinese, ["scope"] = ScopeEn, ["scope_zh"] = ScopeZh };

    /// <summary>
    /// 逐条核对判据二到六。返回第一条不成立的理由；返回 null 表示六条全部成立。证据按核对顺序累积，
    /// 不成立时也留下已经读到的那几条。
    /// </summary>
    private static (string En, string Zh)? Disqualify(JsonObject plan, JsonObject runtime, JsonArray evidence)
    {
        if (plan["loop"] is not JsonObject loop)
            return ("The plan carries no loop report.", "计划里没有循环报告。");
        JsonObject? candidate = loop["candidates"]?.AsArray().FirstOrDefault()?.AsObject();
        string[] components = (candidate?["components"] as JsonArray)?
            .OfType<JsonObject>().Select(component => component["id"]?.GetValue<string>()).OfType<string>().ToArray() ?? [];
        evidence.Add("periodic_components=" + Join(components));
        if (components.Length == 0)
            return ("The plan's selected candidate carries no periodic component, so there is no single video track to be a shell of.",
                "计划选中的候选没有任何周期分量，谈不上被一段视频轨包住。");
        string[] nonVideo = components.Where(id => !id.StartsWith("video/", StringComparison.Ordinal)).ToArray();
        evidence.Add("non_video_components=" + Join(nonVideo));
        if (nonVideo.Length > 0)
            return ("A shader clock or a non-video animation carries part of this period, so per-frame work exists that the bake can move into the video.",
                "这段周期里有着色器时钟或非视频动画分量，说明存在可以被烘进视频的逐帧计算。");
        int unresolved = (loop["unresolved"] as JsonArray)?.Count ?? -1;
        evidence.Add("unresolved_components=" + unresolved.ToString(CultureInfo.InvariantCulture));
        if (unresolved != 0)
            return ("The analytic loop parse left unresolved temporal mechanisms, so the clip is not provably the only thing moving.",
                "解析式循环分析还留着未解析的时间机制，无法证明动的只有那段视频，按保守处理不下结论。");
        int patches = (candidate!["patches"] as JsonArray)?.Count ?? 0;
        evidence.Add("retime_patches=" + patches.ToString(CultureInfo.InvariantCulture));
        if (patches > 0)
            return ("The selected candidate retimes authored rates to close, so the plan changes how the source is timed.",
                "选中的候选要靠改写作者速率才闭合，成品的时序与原作不同。");
        JsonArray? clips = loop["content_cadence"]?["clips"] as JsonArray;
        if (clips is not { Count: 1 } || clips[0] is not JsonObject clip ||
            HybridScenePlanner.Int(clip["owner_layer_id"]) is not int owner ||
            clip["component"]?.GetValue<string>() is not string clipComponent ||
            !components.Contains(clipComponent, StringComparer.Ordinal))
            return ("The plan does not name exactly one fixed-rate clip that owns the selected period.",
                "计划没有恰好指明一段承载选中周期的定速片源。");
        evidence.Add("clip_owner_layer=" + owner.ToString(CultureInfo.InvariantCulture));
        int[] componentOwners = components.Select(OwnerOf).Distinct().ToArray();
        evidence.Add("periodic_component_owners=" + Join(componentOwners.Select(id => id.ToString(CultureInfo.InvariantCulture))));
        if (componentOwners.Length != 1 || componentOwners[0] != owner)
            return ("More than one layer carries the periodic video components, so no single clip stands for the whole capture.",
                "周期性视频分量分布在不止一层上，没有哪一段片源能代表整幅画面。");
        JsonObject[] groups = (plan["video_groups"] as JsonArray)?.OfType<JsonObject>().ToArray() ?? [];
        JsonObject[] opaque = groups.Where(group => group["transparent"]?.GetValue<bool>() != true).ToArray();
        evidence.Add("opaque_groups=" + opaque.Length.ToString(CultureInfo.InvariantCulture) +
            " transparent_groups=" + (groups.Length - opaque.Length).ToString(CultureInfo.InvariantCulture));
        if (opaque.Length != 1)
            return ("The plan does not bake exactly one opaque group, so the clip is not the whole opaque picture.",
                "计划烘的不透明组不止一个（或一个都没有），那段片源撑不起整幅不透明画面。");
        int[] opaqueLayers = ((opaque[0]["layer_ids"] as JsonArray) ?? []).Select(HybridScenePlanner.Int).OfType<int>().ToArray();
        evidence.Add("opaque_group=" + (opaque[0]["id"]?.GetValue<string>() ?? "(unnamed)") +
            " layers=" + Join(opaqueLayers.Select(id => id.ToString(CultureInfo.InvariantCulture))));
        if (opaqueLayers.Length != 1 || opaqueLayers[0] != owner)
            return ("The opaque group bakes layers other than the clip layer, so other content draws inside the same pixels.",
                "不透明组里除了那层片源还有别的图层，同一片像素里还画着别的东西。");
        JsonObject? clipLayer = (plan["layers"] as JsonArray)?.OfType<JsonObject>()
            .FirstOrDefault(layer => HybridScenePlanner.Int(layer["id"]) == owner);
        double fraction = HybridScenePlanner.Numeric(clipLayer?["canvas_fraction"], double.NaN);
        double centreX = HybridScenePlanner.Numeric(clipLayer?["canvas_center_x"], double.NaN);
        double centreY = HybridScenePlanner.Numeric(clipLayer?["canvas_center_y"], double.NaN);
        evidence.Add("clip_layer_canvas_fraction=" + Text(fraction) + " centre=" + Text(centreX) + "," + Text(centreY));
        if (!(fraction >= 1) || Math.Abs(centreX - 0.5) > 1e-9 || Math.Abs(centreY - 0.5) > 1e-9)
            return ("The clip layer does not cover the whole centred canvas, so the wallpaper is not that clip played back.",
                "承载片源的图层没有铺满居中的整幅画布，这张壁纸不只是把那段视频播出来而已。");
        int[] baked = (plan["layers"] as JsonArray)?.OfType<JsonObject>()
            .Where(layer => layer["allocation"]?.GetValue<string>() == "video")
            .Select(layer => HybridScenePlanner.Int(layer["id"])).OfType<int>().ToArray() ?? [];
        evidence.Add("baked_layers=" + Join(baked.Select(id => id.ToString(CultureInfo.InvariantCulture))));
        if (runtime["runtime_layers"] is not JsonArray observed)
            return ("The runtime observation carries no layer table, so the baked layers' effect passes are unknown.",
                "运行时观察里没有图层表，无法得知被烘图层上有没有特效通道。");
        var bakedSet = baked.ToHashSet();
        int effectLayers = 0, effectPasses = 0;
        bool clipLayerObserved = false, effectEvidenceMissing = false;
        foreach (var entry in observed.OfType<JsonObject>())
        {
            if (HybridScenePlanner.Int(entry["owner"]) is not int ownerId || !bakedSet.Contains(ownerId)) continue;
            if (ownerId == owner) clipLayerObserved = true;
            if (entry["has_effect_layer"] is not JsonValue flag || !flag.TryGetValue<bool>(out bool hasEffectLayer))
            { effectEvidenceMissing = true; continue; }
            if (hasEffectLayer) ++effectLayers;
            effectPasses += (entry["materials"] as JsonArray)?.OfType<JsonObject>()
                .Count(material => material["role"]?.GetValue<string>() == "effect") ?? 0;
        }
        evidence.Add("baked_effect_layers=" + effectLayers.ToString(CultureInfo.InvariantCulture) +
            " baked_effect_passes=" + effectPasses.ToString(CultureInfo.InvariantCulture));
        if (effectEvidenceMissing || !clipLayerObserved)
            return ("The runtime observation does not record effect-layer metadata for every baked layer; analyze again with the current renderer.",
                "运行时观察没有完整记录被烘图层的特效层信息，换当前渲染器重新分析一次再说。");
        if (effectLayers > 0 || effectPasses > 0)
            return ("The baked layers carry effect passes, so the bake moves that per-frame shader work into the video.",
                "被烘图层上挂着特效通道，烘焙会把这些逐帧着色器计算转进视频。");
        return null;
    }

    /// <summary>分量 id 的第二段就是承载它的图层；解析不出来记 -1，落在"不止一层"那一侧。</summary>
    private static int OwnerOf(string component)
    {
        string[] parts = component.Split('/');
        return parts.Length > 1 && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int id) ? id : -1;
    }

    private static string Join(IEnumerable<string> values)
    {
        string text = string.Join(", ", values);
        return text.Length == 0 ? "(none)" : text;
    }

    private static string Text(double value) =>
        double.IsNaN(value) ? "(absent)" : value.ToString("0.######", CultureInfo.InvariantCulture);
}
