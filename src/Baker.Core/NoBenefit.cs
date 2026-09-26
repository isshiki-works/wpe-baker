using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>
/// 按组取舍，烘了也不省电的方案默认拒绝（只有命令行 --no-benefit allow 能覆盖，调试、测功耗用）。
/// 每个候选组（整层路线的视频组、前缀路线的每个缓存）算 净收益 = 省下的渲染 R_g − 视频成本 C_g，
/// 只烘净收益大于 <see cref="GroupMarginW"/> 的组，其余还给实时；全作品净收益之和不到 <see cref="MinSavingW"/> 就判"预计不省电"并写出主因。
/// 另一条判据：成品固定在单个时段。瓦数是笔记本（Arc B390，3072×1920@60，封装功耗）口径的预测，只用来挑组，是否省电以实测为准。
/// </summary>
public static class NoBenefit
{
    public const string RejectChoice = "reject";
    public const string AllowChoice = "allow";

    /// <summary>plan 顶层字段：{ policy, status, conditions[], cause }，命中判据时才写。界面读 status。</summary>
    public const string Field = "no_benefit";
    /// <summary>plan 顶层字段：逐组的 render_w / video_w / net_w 与按净收益该烘还是该留实时（decision），量得到渲染耗时才写。</summary>
    public const string CostField = "group_costs";

    public const string ExpectedStatus = "expected_no_benefit";
    public const string OverrideStatus = "override_accepted";
    public const string RejectionReason = "no_benefit_expected";

    public const string FixedDaytime = "fixed_daytime_state";
    public const string NetBelowThreshold = "net_saving_below_threshold";

    // 标定（runs/COSTALLOC/calibration.json）：本渲染器 3072×1920 逐帧绘制耗时对笔记本原作封装功耗（减空闲），14 张线性拟合
    // 斜率 2.13 W/ms、截距 1.6 W；WPE 在这个分辨率下的底座约 6 W。视频成本：每路 CPU+非核心 0.66 W（SALVAGE），
    // 每个视频层核显 0.46 W（VLAYOUT，低负载），解码与采样 0.5 mW/(Mpx/s)；后两项随原作负载（原作功耗 ÷ 底座，至少 1）放大，
    // 对上 3257043844 高负载下撤 3 组实测每组 2.36 W、3776778760 低负载每组约 1.1 W。
    internal const uint ReferenceWidth = 3072, ReferenceHeight = 1920;
    private const double WattsPerDrawMs = 2.13, OriginalInterceptW = 1.6, WpeFloorW = 6.0;
    private const double StreamW = 0.66, VideoLayerW = 0.46, DecodeWPerMpxS = 0.0005;
    /// <summary>单组净收益要超过它才烘（预测误差的余量）。</summary>
    public const double GroupMarginW = 0.3;
    /// <summary>全作品净收益之和不到它就判预计不省电（台账判据 2σ ≤ 1 W 同一量级）。</summary>
    public const double MinSavingW = 1.0;

    public const string CauseNearIdle = "original_near_idle";
    public const string CauseLiveLayers = "cost_in_live_layers";
    public const string CauseStreams = "video_streams_cost";
    public const string CausePixelRate = "video_pixel_rate_cost";

    /// <summary>
    /// 分析收尾的按组取舍：逐组量渲染耗时算净收益；整层路线把不划算的组经 <paramref name="replan"/>（加 --retain-live 重新分析）还给实时，
    /// 前缀路线直接去掉不划算的缓存（bake 只核对缓存是提案的子集）。至少留一组；重新分析不成立就用原方案。量不到耗时（没有渲染器、
    /// 设备不支持计时、源自带视频的方案）时原样返回，不写成本记录。
    /// </summary>
    internal static async Task<JsonObject> SelectGroupsAsync(JsonObject plan, HybridAnalyzeRequest request, NativeTools? tools, string output,
        string cacheDirectory, Func<int[], Task<(JsonObject Plan, JsonArray Reasons)>> replan, CancellationToken token)
    {
        if (await CostsAsync(plan, request, tools, output, cacheDirectory, token) is not JsonObject costs) return plan;
        JsonObject[] groups = [.. costs["groups"]!.AsArray().OfType<JsonObject>()];
        string[] drop = [.. groups.Where(g => !(g["net_w"]!.GetValue<double>() > GroupMarginW)).Select(g => g["id"]!.GetValue<string>())];
        if (drop.Length > 0 && drop.Length < groups.Length)
        {
            if (plan["route"]?.GetValue<string>() == "effect_prefix")
            {
                var caches = plan["effect_prefix_caches"]!.AsArray();
                foreach (JsonNode? cache in caches.ToArray())
                    if (drop.Contains(PrefixId(cache!.AsObject()))) caches.Remove(cache);
                if (plan[BakeValueAssessment.Field]?["evidence"] is JsonObject evidence && evidence.ContainsKey("prefix_count"))
                    evidence["prefix_count"] = caches.Count;
                plan[CostField] = costs;
                plan["suitability"] = HybridSuitability.Verdict(plan);
                PlanNarrative.Attach(plan);
                return plan;
            }
            int[] roots = [.. (plan["video_groups"] as JsonArray ?? []).OfType<JsonObject>()
                .Where(g => drop.Contains(g["id"]?.GetValue<string>()))
                .SelectMany(g => (g["root_ids"] as JsonArray ?? []).Select(SceneGraph.Int)).OfType<int>()];
            if (FullFrameDemotion.RetainLiveCommandRoots(plan, roots) is { Length: > 0 } retain)
            {
                var (replanned, _) = await replan(retain);
                if (Admission.Accepted(replanned) &&
                    await CostsAsync(replanned, request, tools, output, cacheDirectory, token) is JsonObject recosted)
                {
                    recosted["returned_live_groups"] = new JsonArray([.. groups.Where(g => drop.Contains(g["id"]!.GetValue<string>()))
                        .Select(g => (JsonNode)g.DeepClone())]);
                    replanned[CostField] = recosted;
                    return replanned;
                }
            }
        }
        plan[CostField] = costs;
        return plan;
    }

    private static string PrefixId(JsonObject cache) => "prefix-" + cache["owner_layer_id"]!.GetValue<int>();

    /// <summary>逐组成本。渲染耗时在参照分辨率下量：全部图层一次，再每组隐藏一次，差值换成瓦数。</summary>
    private static async Task<JsonObject?> CostsAsync(JsonObject plan, HybridAnalyzeRequest request, NativeTools? tools, string output,
        string cacheDirectory, CancellationToken token)
    {
        if (tools is null || plan["video_dominant"]?["decode_work"] is not null) return null;
        var layers = (plan["layers"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(l => SceneGraph.Int(l["id"]) is int && l["allocation"]?.GetValue<string>() != "excluded")
            .DistinctBy(l => SceneGraph.Int(l["id"])).ToDictionary(l => SceneGraph.Int(l["id"])!.Value);
        double Fraction(int id) => Math.Min(1, SceneGraph.Numeric(layers.GetValueOrDefault(id)?["canvas_fraction"], 1));
        HybridAnalyzeRequest settings = PlanSettings.Of(plan);
        double outputMpxS = (double)settings.Width * settings.Height * settings.FpsNumerator / settings.FpsDenominator / 1e6;
        bool still = plan["loop"]?["candidates"] is JsonArray { Count: > 0 } loops && BakeValueAssessment.Number(loops[0]?["frames"]) <= 1;
        // (id, 隐藏的图层, 占该层渲染的比例, 编码像素占输出的比例, 是否静态纹理)
        var candidates = new List<(string Id, int[] Hidden, double Share, double Area, bool Static)>();
        if (plan["route"]?.GetValue<string>() == "effect_prefix")
        {
            JsonObject? runtime = plan["runtime_evidence"]?.GetValue<string>() is string path && File.Exists(path)
                ? JsonNode.Parse(await File.ReadAllTextAsync(path, token))?.AsObject() : null;
            foreach (JsonObject cache in (plan["effect_prefix_caches"] as JsonArray ?? []).OfType<JsonObject>())
            {
                int owner = cache["owner_layer_id"]!.GetValue<int>();
                // 前缀只占这一层特效链的一段：隐藏整层量到的耗时按特效个数折算（左右打包 alpha，像素翻倍）。
                int effects = (runtime?["runtime_layers"] as JsonArray ?? []).OfType<JsonObject>().Where(l => SceneGraph.Int(l["owner"]) == owner)
                    .Sum(l => (l["materials"] as JsonArray ?? []).OfType<JsonObject>().Count(m => m["role"]?.GetValue<string>() == "effect"));
                double count = cache["prefix_effect_count"]!.GetValue<int>();
                candidates.Add((PrefixId(cache), [owner], effects > count ? count / effects : 1, 2 * Fraction(owner), false));
            }
        }
        else
            foreach (JsonObject group in (plan["video_groups"] as JsonArray ?? []).OfType<JsonObject>())
            {
                int[] ids = [.. (group["layer_ids"] as JsonArray ?? []).Select(SceneGraph.Int).OfType<int>()];
                bool transparent = group["transparent"]?.GetValue<bool>() == true;
                int[] hidden = [.. layers.Keys.Where(id => ids.Any(g => SceneGraph.Within(layers, id, g)))];
                candidates.Add((group["id"]!.GetValue<string>(), hidden, 1,
                    transparent ? 2 * Math.Min(1, ids.Sum(Fraction)) : 1, still || group["static_verified"]?.GetValue<bool>() == true));
            }
        if (candidates.Count == 0 || await DrawMsAsync(plan, request, tools, output, cacheDirectory, layers.Keys, token) is not double all)
            return null;
        double original = OriginalInterceptW + WattsPerDrawMs * all, load = Math.Max(1, original / WpeFloorW);
        var rows = new JsonArray();
        double net = 0, render = 0, streams = 0, pixels = 0;
        foreach (var (id, hidden, share, area, isStatic) in candidates)
        {
            if (await DrawMsAsync(plan, request, tools, output, cacheDirectory, layers.Keys.Except(hidden), token) is not double without)
                return null;
            double renderW = Math.Max(0, all - without) * share * WattsPerDrawMs;
            double streamW = isStatic ? 0 : StreamW + load * VideoLayerW, pixelW = isStatic ? 0 : load * DecodeWPerMpxS * area * outputMpxS;
            double groupNet = renderW - streamW - pixelW;
            rows.Add(new JsonObject { ["id"] = id, ["render_w"] = Math.Round(renderW, 2), ["video_w"] = Math.Round(streamW + pixelW, 2),
                ["net_w"] = Math.Round(groupNet, 2), ["decision"] = groupNet > GroupMarginW ? "bake" : "live" });
            net += groupNet; render += renderW; streams += streamW; pixels += pixelW;
        }
        return new JsonObject {
            ["basis"] = "laptop_arc_b390_3072x1920_pkg_prediction", ["draw_ms"] = Math.Round(all, 3),
            ["original_w"] = Math.Round(original, 2), ["load_factor"] = Math.Round(load, 2), ["net_w"] = Math.Round(net, 2),
            ["cause"] = original - WpeFloorW < MinSavingW ? CauseNearIdle : render < MinSavingW ? CauseLiveLayers
                : pixels > streams ? CausePixelRate : CauseStreams,
            ["groups"] = rows };
    }

    /// <summary>只画 <paramref name="include"/> 这些图层时的逐帧绘制耗时中位数（毫秒）；设备不支持计时返回 null。读数按源与图层集缓存。</summary>
    private static async Task<double?> DrawMsAsync(JsonObject plan, HybridAnalyzeRequest request, NativeTools tools, string output,
        string cacheDirectory, IEnumerable<int> include, CancellationToken token)
    {
        int[] ids = [.. include.Order()];
        string key = "group-cost-" + AnalysisCache.Key(plan["source_sha256"], plan["snapshot_properties"], tools,
            File.Exists(tools.Renderer) ? File.GetLastWriteTimeUtc(tools.Renderer).Ticks : 0, request.DeviceUuid, ids);
        if (AnalysisCache.Read(cacheDirectory, key)?["draw_ms"] is JsonValue cached) return cached.GetValue<double>();
        string directory = Path.Combine(output, key[^16..]);
        const ulong frames = 60;
        JsonObject raw = new();
        try
        {
            raw = await new NativeRenderRunner(tools).RenderRawAsync(new(request.Source, request.Assets, directory,
                ReferenceWidth, ReferenceHeight, 60, 1, frames, WarmupFrames: 30, Seed: 17,
                UserProperties: plan["snapshot_properties"]?.AsObject(), DeviceUuid: request.DeviceUuid, GpuTiming: true,
                LayerSelection: new(ids), FrameSampleStride: (uint)frames, FrameSampleWidth: 16, FrameSamplesOnly: true), token);
            if (raw["native_result"]?["gpu_timing"]?["supported"]?.GetValue<bool>() != true) return null;
            string perFrame = Path.Combine(directory, "native", "frames.jsonl");
            double[] draws = File.Exists(perFrame)
                ? [.. File.ReadLines(perFrame).Where(line => line.Length > 0).Select(line => JsonNode.Parse(line)?["gpu_draw_ms"])
                    .OfType<JsonValue>().Select(v => v.GetValue<double>()).Order()]
                : [];
            double? ms = draws.Length > 0 ? draws[draws.Length / 2] : BakeValueAssessment.Number(raw["native_result"]?["gpu_timing"]?["draw_ms"]);
            if (ms is not double value || !double.IsFinite(value)) return null;
            AnalysisCache.Write(cacheDirectory, key, new JsonObject { ["draw_ms"] = value });
            return value;
        }
        catch (Exception error) when (error is IOException or InvalidDataException) { return null; }
        finally
        {
            TemporaryCaptureFiles.Delete(raw["native_result"] as JsonObject ?? new(), directory,
                "native/frames.rgba", "native/frames.rgba.partial", "native/audio.f32le", "native/audio.f32le.partial");
        }
    }

    /// <summary>分析时就能判的条件：固定在单个时段；按组取舍后全作品净收益之和不到 <see cref="MinSavingW"/>。</summary>
    public static string[] AnalysisConditions(JsonObject plan)
    {
        var hits = new List<string>();
        if (plan["settings"]?["daytime_state"] is JsonValue)
            hits.Add(FixedDaytime);
        if (plan[CostField]?["net_w"] is JsonValue net && net.GetValue<double>() < MinSavingW)
            hits.Add(NetBelowThreshold);
        return hits.ToArray();
    }

    /// <summary>分析收尾：记下判定；命中且没有覆盖时写 blocker 拒绝（与 TooManyVideoGroups 同一写法）。</summary>
    public static void Apply(JsonObject plan, bool allowed)
    {
        string[] conditions = AnalysisConditions(plan);
        if (conditions.Length == 0) return;   // 没命中的方案不写记录
        plan[Field] = new JsonObject {
            ["policy"] = allowed ? AllowChoice : RejectChoice, ["status"] = allowed ? OverrideStatus : ExpectedStatus,
            ["conditions"] = new JsonArray(conditions.Select(c => (JsonNode)JsonValue.Create(c)).ToArray()),
            ["cause"] = conditions.Contains(NetBelowThreshold) ? plan[CostField]?["cause"]?.DeepClone() : null };
        if (allowed) return;
        PlanBlockers.Add(plan, new Blocker(BlockerCode.NoBenefitExpected, [Describe(plan, english: true)], [Describe(plan, english: false)]));
        plan["status"] = "requires_resolution";
        plan["preset_rejection_reason"] = RejectionReason;
        plan["suitability"] = HybridSuitability.Verdict(plan);
        PlanNarrative.Attach(plan);
    }

    /// <summary>plan 里记下的命中条件，讲成一句话（界面结论区用）。</summary>
    public static string Describe(JsonObject plan, bool english)
    {
        string? cause = plan[Field]?["cause"]?.GetValue<string>();
        double net = BakeValueAssessment.Number(plan[CostField]?["net_w"], 0);
        return string.Join(english ? "; " : "；", (plan[Field]?["conditions"] as JsonArray ?? []).Select(c => c!.GetValue<string>()).Select(c => (c, english) switch
        {
            (FixedDaytime, false) => "成品固定在一个时段，不随时刻切换",
            (FixedDaytime, true) => "the result is fixed to one time of day and does not follow the clock",
            (NetBelowThreshold, false) => $"预计省电 {net:0.0} W，不到 {MinSavingW:0} W（主因：" + cause switch
            {
                CauseNearIdle => "原壁纸本身接近空载",
                CauseLiveLayers => "耗电的图层必须保持实时",
                CausePixelRate => "视频像素率太高",
                _ => "视频路数太多"
            } + "）",
            (NetBelowThreshold, true) => $"the estimated saving is {net:0.0} W, below {MinSavingW:0} W (main cause: " + cause switch
            {
                CauseNearIdle => "the original wallpaper is already close to idle",
                CauseLiveLayers => "the expensive layers have to stay live",
                CausePixelRate => "the video pixel rate is too high",
                _ => "too many video streams"
            } + ")",
            _ => c
        }));
    }
}
