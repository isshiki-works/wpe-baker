using System.Globalization;
using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>
/// 视频外壳判据：结构相同且已知源视频尺寸、帧率均无缩减空间时才拒绝。
/// 整层路线、周期分量全部来自 video 轨、没有未解析的时间机制、候选不需要调速、那段视频由唯一一层
/// 不透明整幅图层承载、被烘图层不含任何特效通道。任何一条不成立都不足以判为无工作量缩减，
/// 不在此追加拒绝理由。
/// 缺少源解码元数据时不拒绝；元数据比较不构成任何设备上的功耗结论。
/// 解码量比较与分量归属在 Domain 的 <see cref="WorkloadValue"/>；这里读 plan 与运行时观察、写记录与理由。
/// </summary>
public static class VideoDominance
{
    /// <summary>判定成立：被烘内容与原作结构同构。</summary>
    public const string ShellStatus = "video_shell";

    /// <summary>未证明结构与视频解码工作量都不会缩减；包含证据未知。</summary>
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
        "No workload reduction was identified: the plan preserves one video decode and one full-screen draw, " +
        "has no effect passes to cache, and does not reduce the source video's encoded pixel count or frame rate. " +
        "This is not a power measurement. Use --video-shell allow to generate a fixed loop or repackage it.";

    /// <summary>同一条的中文原文；界面与命令行直接显示这一句，不翻译上面的英文。</summary>
    public const string BlockerZh =
        "未识别到工作量缩减：方案仍需一次视频解码与一次全屏绘制，没有可缓存的特效，" +
        "也未降低源视频的编码像素数或帧率。此结论不是功耗实测；需要定长循环或重新打包时可用 --video-shell allow 显式覆盖。";

    private const string ScopeEn =
        "Structure and source encoded dimensions/frame rate from this runtime observation. " +
        "Missing metadata never proves low value; this is not a hardware or power measurement.";

    private const string ScopeZh = "比较结构与本次观察到的源视频编码尺寸、帧率；元数据缺失不代表低价值，结果不代表硬件或功耗实测。";

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
        JsonObject decodeWork = DecodeWork(plan, runtime);
        string decodeStatus = decodeWork["status"]!.GetValue<string>();
        JsonObject result = decodeStatus != WorkloadValue.DecodeNotReduced
            ? Record(NotShellStatus, choice, evidence,
                decodeStatus == WorkloadValue.DecodePotentialGain ? "Source video dimensions or frame rate can be reduced; decoding savings remain to be verified."
                    : "Source decoding metadata is incomplete or not directly comparable with the output; the absence of decoding savings has not been established.",
                decodeStatus == WorkloadValue.DecodePotentialGain ? "源视频尺寸或帧率可降低，解码收益仍待验证。" : "源视频解码元数据不完整或不能与输出直接比较，尚未证明没有解码收益。")
            : choice == AllowChoice
            ? Record(OverrideStatus, choice, evidence, OverrideReasonEn, OverrideReasonZh)
            : Record(ShellStatus, choice, evidence, Blocker, BlockerZh);
        result["decode_work"] = decodeWork;
        return result;
    }

    private static JsonObject DecodeWork(JsonObject plan, JsonObject runtime)
    {
        var result = new JsonObject { ["status"] = "unknown" };
        JsonObject workload = HybridVideoWorkload.Summarize(runtime);
        if (!HybridVideoWorkload.Complete(workload)) return result;
        int? owner = SceneGraph.Int(plan["loop"]?["content_cadence"]?["clips"]?[0]?["owner_layer_id"]);
        var textures = (runtime["runtime_layers"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(layer => SceneGraph.Int(layer["owner"]) == owner)
            .SelectMany(layer => (layer["materials"] as JsonArray ?? []).OfType<JsonObject>())
            .Where(material => material["role"]?.GetValue<string>() == "source")
            .SelectMany(material => (material["textures"] as JsonArray ?? []).Select(value => value?.GetValue<string>()))
            .OfType<string>().Where(name => name.Length > 0).ToHashSet(StringComparer.Ordinal);
        JsonObject[] streams = (runtime["runtime_video_decoders"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(stream => textures.Contains(stream["resource_key"]!.GetValue<string>())).ToArray();
        if (streams.Length != 1) return result;
        double sourceWidth = BakeValueAssessment.Number(streams[0]["coded_width"]);
        double sourceHeight = BakeValueAssessment.Number(streams[0]["coded_height"]);
        double sourceFps = BakeValueAssessment.Number(streams[0]["fps_num"]) / BakeValueAssessment.Number(streams[0]["fps_den"]);
        double width = BakeValueAssessment.Number(plan["output_resolution"]?["width"] ?? plan["settings"]?["width"]);
        double height = BakeValueAssessment.Number(plan["output_resolution"]?["height"] ?? plan["settings"]?["height"]);
        double numerator = BakeValueAssessment.Number(plan["settings"]?["fps_numerator"]);
        double denominator = BakeValueAssessment.Number(plan["settings"]?["fps_denominator"]);
        double fps = numerator / denominator;
        if (!WorkloadValue.IsComparableOutput(width, height, fps)) return result;
        result["source"] = streams[0].DeepClone();
        result["output_width"] = width; result["output_height"] = height; result["output_fps"] = fps;
        string? codec = plan["encoding"]?["codec"]?.GetValue<string>();
        if (codec == "auto_h264_hevc")
        {
            if (new[] { width, height, numerator, denominator }.Any(value =>
                !double.IsFinite(value) || value < 1 || value > uint.MaxValue || value != Math.Truncate(value))) return result;
            codec = PlaybackEncodeProfile.SelectPlaybackEncoder((uint)width, (uint)height, (uint)numerator, (uint)denominator) == "libx264"
                ? "h264" : "hevc";
        }
        string? pixelFormat = plan["encoding"]?["pixel_format"]?.GetValue<string>();
        result["output_codec"] = codec; result["output_pixel_format"] = pixelFormat;
        if (codec is not ("h264" or "hevc") || codec != streams[0]["codec"]?.GetValue<string>() ||
            string.IsNullOrWhiteSpace(pixelFormat) || pixelFormat != streams[0]["pixel_format"]?.GetValue<string>()) return result;
        result["status"] = WorkloadValue.DecodeWorkStatus(width, height, fps, sourceWidth, sourceHeight, sourceFps);
        return result;
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
            SceneGraph.Int(clip["owner_layer_id"]) is not int owner ||
            clip["component"]?.GetValue<string>() is not string clipComponent ||
            !components.Contains(clipComponent, StringComparer.Ordinal))
            return ("The plan does not name exactly one fixed-rate clip that owns the selected period.",
                "计划没有恰好指明一段承载选中周期的定速片源。");
        evidence.Add("clip_owner_layer=" + owner.ToString(CultureInfo.InvariantCulture));
        int[] componentOwners = components.Select(WorkloadValue.OwnerOf).Distinct().ToArray();
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
        int[] opaqueLayers = ((opaque[0]["layer_ids"] as JsonArray) ?? []).Select(SceneGraph.Int).OfType<int>().ToArray();
        evidence.Add("opaque_group=" + (opaque[0]["id"]?.GetValue<string>() ?? "(unnamed)") +
            " layers=" + Join(opaqueLayers.Select(id => id.ToString(CultureInfo.InvariantCulture))));
        if (opaqueLayers.Length != 1 || opaqueLayers[0] != owner)
            return ("The opaque group bakes layers other than the clip layer, so other content draws inside the same pixels.",
                "不透明组里除了那层片源还有别的图层，同一片像素里还画着别的东西。");
        JsonObject? clipLayer = (plan["layers"] as JsonArray)?.OfType<JsonObject>()
            .FirstOrDefault(layer => SceneGraph.Int(layer["id"]) == owner);
        double fraction = SceneGraph.Numeric(clipLayer?["canvas_fraction"], double.NaN);
        double centreX = SceneGraph.Numeric(clipLayer?["canvas_center_x"], double.NaN);
        double centreY = SceneGraph.Numeric(clipLayer?["canvas_center_y"], double.NaN);
        evidence.Add("clip_layer_canvas_fraction=" + Text(fraction) + " centre=" + Text(centreX) + "," + Text(centreY));
        if (!WorkloadValue.FillsCentredCanvas(fraction, centreX, centreY))
            return ("The clip layer does not cover the whole centred canvas, so the wallpaper is not that clip played back.",
                "承载片源的图层没有铺满居中的整幅画布，这张壁纸不只是把那段视频播出来而已。");
        int[] baked = (plan["layers"] as JsonArray)?.OfType<JsonObject>()
            .Where(layer => layer["allocation"]?.GetValue<string>() == "video")
            .Select(layer => SceneGraph.Int(layer["id"])).OfType<int>().ToArray() ?? [];
        evidence.Add("baked_layers=" + Join(baked.Select(id => id.ToString(CultureInfo.InvariantCulture))));
        if (runtime["runtime_layers"] is not JsonArray observed)
            return ("The runtime observation carries no layer table, so the baked layers' effect passes are unknown.",
                "运行时观察里没有图层表，无法得知被烘图层上有没有特效通道。");
        var bakedSet = baked.ToHashSet();
        int effectLayers = 0, effectPasses = 0;
        bool clipLayerObserved = false, effectEvidenceMissing = false;
        foreach (var entry in observed.OfType<JsonObject>())
        {
            if (SceneGraph.Int(entry["owner"]) is not int ownerId || !bakedSet.Contains(ownerId)) continue;
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

    private static string Join(IEnumerable<string> values)
    {
        string text = string.Join(", ", values);
        return text.Length == 0 ? "(none)" : text;
    }

    private static string Text(double value) =>
        double.IsNaN(value) ? "(absent)" : value.ToString("0.######", CultureInfo.InvariantCulture);
}
