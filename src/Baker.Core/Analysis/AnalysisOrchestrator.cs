using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Baker.Core;

/// <summary>
/// 逐状态子 plan 的去处（CLI 的 --daytime-split on）：<paramref name="PlanPath"/> 按状态名给落盘路径（新建，已存在即报错），
/// <paramref name="Analyzed"/> 在每个状态分析完后回调（CLI 用它在 stderr 上报一行结论）。
/// </summary>
public sealed record StateExport(Func<string, string> PlanPath, Action<string, JsonObject>? Analyzed = null);

/// <summary>
/// 分析编排：在 <see cref="SearchSpace"/>（交互 × 档位 × 布局 × 状态）里按固定顺序调用单次分析，挑第一个能生成的结果；
/// 请求的交互策略不行时验证替代策略并记成建议；最后把尝试经过写进既有的 preset_* 字段、落盘，按需逐状态导出子 plan。
/// 每次调用先在 <see cref="CallBudget"/> 记账，超出现状最坏上限抛内部错误。
/// </summary>
internal sealed class AnalysisOrchestrator
{
    // 与 CLI 写 plan 的序列化选项相同：逐状态子 plan 原先由 Program.cs 用这组选项写出。
    private static readonly JsonSerializerOptions StatePlanJson = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private readonly HybridAnalyzeRequest request;
    private readonly Func<HybridAnalyzeRequest, CancellationToken, Task<JsonObject>> analyze;
    private readonly SearchSpace space;
    private readonly CallBudget budget;
    private readonly NativeTools? tools;
    private readonly string run, cache;
    private readonly CancellationToken token;
    private readonly IProgress<RenderProgress>? progress;
    /// <summary>一次 RunAsync 里各编排器共用：退回重查的序号、逐组静态证明的记忆（子分析的记忆由 analyze 自己带着）。</summary>
    private readonly AnalysisMemo memo;
    private int attempt, states;
    private JsonArray costs = new();

    private AnalysisOrchestrator(HybridAnalyzeRequest request, Func<HybridAnalyzeRequest, CancellationToken, Task<JsonObject>> analyze,
        SearchSpace space, CallBudget budget, NativeTools? tools, string run, string cache, CancellationToken token, AnalysisMemo memo,
        IProgress<RenderProgress>? progress = null)
    {
        this.request = request;
        this.analyze = analyze;
        this.space = space;
        this.budget = budget;
        this.tools = tools;
        this.run = run;
        this.cache = cache;
        this.token = token;
        this.progress = progress;
        this.memo = memo;
    }

    /// <param name="budget">省略时用 <see cref="SearchSpace.Budget"/> 的现状最坏上限；测试传入别的预算验证超出时的内部错误。</param>
    /// <param name="progress">多轮退回重查时每试一种分组报一条阶段信息（retreating）。</param>
    /// <param name="memo">与 <paramref name="analyze"/> 共用的记忆（<see cref="HybridScenePlanner"/> 传入）；省略时新建一个。</param>
    internal static async Task<JsonObject> RunAsync(HybridAnalyzeRequest request,
        Func<HybridAnalyzeRequest, CancellationToken, Task<JsonObject>> analyze, CancellationToken token, NativeTools? tools = null,
        StateExport? export = null, CallBudget? budget = null, IProgress<RenderProgress>? progress = null, AnalysisMemo? memo = null)
    {
        memo ??= new();
        AnalysisTiming timing = AnalysisTiming.Begin();
        SearchSpace space = SearchSpace.Of(request, export is not null);
        string root = Path.GetFullPath(request.OutputDirectory);
        ProjectSource.EnsureNoReparsePoints(root);
        Directory.CreateDirectory(root);
        string run = Path.Combine(root, "run-" + Guid.NewGuid().ToString("N"));
        string assetsStamp = Directory.Exists(request.Assets) ? AnalysisCache.Key(Directory.EnumerateFiles(request.Assets, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal).Select(path => { var file = new FileInfo(path); return new { path, file.Length, file.LastWriteTimeUtc }; }).ToArray()) : "missing";
        string cache = Path.Combine(request.AnalysisCacheDirectory ?? Path.Combine(root, "cache"),
            AnalysisCache.Key(typeof(AnalysisOrchestrator).Module.ModuleVersionId, assetsStamp));
        var orchestrator = new AnalysisOrchestrator(request, analyze, space, budget ?? space.Budget(), tools, run, cache, token, memo, progress);
        JsonObject result = await orchestrator.SelectAsync();
        // 单次轨判实时（SingleShotLive）与"静止证明点名的层留实时"不在这里整套重跑：前者只在烘焙时由合成门拒绝入场切换触发，
        // 后者由分配回退在同一次分析里把重查新点名的所有者并进留实时（HybridLoopAllocation.ReplanUntilSettledAsync）。
        // 结果不行多半是预计不省电，多留实时、单次轨判实时只会烘得更少，救不回来。
        // 读帧缓冲规则放宽只在不让结果变差时采用：把放宽后没留实时的读取层按读帧缓冲留实时（LiveFramebufferReaderIds，实时判定阶段生效，
        // 不占用 --retain-live，分配回退照常能跑），从原请求的留实时集合整套再分析一次（放宽前的判定；不带放宽后方案自己的回退、
        // 退回留下的层，那些是放宽后的分配引出来的）。放宽后不能生成（例如读取层带进凑不出循环的分量），
        // 放宽后的方案只在能生成、且预计收益（省下的特效覆盖减视频路数）不少于、实时画布不大于放宽前时保留（放宽前不能生成的除外）；
        // 否则用放宽前的，两边都不能生成时结论也按放宽前。这一次能换回结果，不是白跑。
        if (await orchestrator.RelaxedFramebufferReadersAsync(result) is { Length: > 0 } relaxed)
        {
            var trial = new AnalysisOrchestrator(orchestrator.request with { RetainLiveRootIds = request.RetainLiveRootIds,
                    RetainLiveReasons = request.RetainLiveReasons, LiveFramebufferReaderIds = [.. (request.LiveFramebufferReaderIds ?? []).Union(relaxed).Order()] },
                analyze, space, space.Budget(), tools, Path.Combine(run, "framebuffer-live"), cache, token, memo, progress);
            JsonObject before;
            using (AnalysisTiming.Measure("g_framebuffer_live")) before = await trial.SelectAsync();
            bool relaxedHolds = Admission.Accepted(result) && (!Admission.Accepted(before) ||
                Margin(result) is double relaxedMargin && Margin(before) is double beforeMargin &&
                relaxedMargin >= beforeMargin && LiveCanvas(result) <= LiveCanvas(before) + 1e-9);
            if (!relaxedHolds) (orchestrator, result) = (trial, before);
        }
        // 含缓变分量的视频组先过闭合预检（SlowClosureProbe）：没闭合的把点名的慢分量层留实时、整套再分析，直到都闭合或不能生成；
        // 读数（漂移上界、闭合读数、渲染器墙钟）写进 plan 的 slow_closure_probe。闭合的照常判能，烘焙时接缝门照常复核。
        // 没闭合的层上有能改挂旋钮的慢分量时，留实时之前先改速或冻结它们重分析（RetimeSlowComponents），过速度实测才放行。
        // 振幅推不出的慢项超预算改速先实测速度差（SlowClosureProbe.SpeedAsync）：看得出就按逐项预算整套再分析（BudgetOnlyRetime，与没有这条放宽时
        // 同一结果），量不到（含渲染失败）同样不放开、但挡住循环的记未收敛（SpeedUnmeasured，见 AfterSpeedProbe）；读数写进 plan 的 slow_speed_probe。放行的改速照常过闭合预检与烘焙接缝门，阈值不动。
        // 烘焙预检（SlowClosureProbe）：按烘焙同一个主渲染请求，把烘焙门要看的帧在分析里渲出来、按烘焙的阈值判，没过的在这里留实时、整套再分析，
        // 直到都过或不能生成；烘焙不再为这些情况退回重烘。读数写进 plan：
        // 缓变分量闭合（slow_closure_probe；组按自身周期 P_g 录时就在 P_g 上判；没闭合 → 所有者层留实时）、
        // 含粒子的残差组（residual_probe；慢分量预检本来就跑的起点搜索证出烘焙第一层必拒 → 组里的粒子留实时，不另起渲染）、
        // 没证出静态的组数越过路数上限时的动态组（video_stream_probe；证出越线 → 省下渲染最少的越线几组留实时）。
        // 动态组判定是 main 没有的渲染，限时：只用到这里为止分析墙钟的一半（StreamBudgetFraction），超时没证出的组照旧没证出，不当拒因。
        // 过了的照常判能，烘焙时各道门照常复核，阈值都不动。这样留实时的层各带专门的原因码，读数在对应记录里（ProbeReplan）。
        // 预检的渲染记在 j_slow_closure，因预检改方案而整套再分析记在 k_probe_replan。
        var slowProbes = new JsonArray();
        var residualProbes = new JsonArray();
        var streamProbes = new JsonArray();
        var speedProbes = new JsonArray();
        int rounds = 0;
        TimeSpan streamBudget = timing.Elapsed * StreamBudgetFraction;
        while (Admission.Accepted(result) && tools is not null && request.RuntimeTraceFile is null)
        {
            using var slow = AnalysisTiming.Measure("j_slow_closure");
            if (await SlowClosureProbe.SpeedAsync(result, tools, Path.Combine(run, $"slow-speed-{speedProbes.Count}"), token) is JsonObject speed)
            {
                speedProbes.Add(speed);
                if (AfterSpeedProbe(orchestrator.request, speed["status"]!.GetValue<string>()) is HybridAnalyzeRequest retry)
                {
                    orchestrator = new AnalysisOrchestrator(retry, analyze, space,
                        space.Budget(), tools, Path.Combine(run, $"slow-speed-budget-{speedProbes.Count}"), cache, token, memo, progress);
                    result = await orchestrator.SelectAsync();
                    continue;
                }
            }
            JsonArray round = await SlowClosureProbe.RunAsync(result, tools, Path.Combine(run, $"bake-probe-{rounds}"), streamBudget, token);
            HybridAnalyzeRequest? next = ProbeReplan(result, round, orchestrator.request);
            foreach (JsonObject record in round.OfType<JsonObject>())
                (record["kind"]?.GetValue<string>() switch { "residual_start_search" => residualProbes, "video_streams" => streamProbes, _ => slowProbes })
                    .Add(record.DeepClone());
            streamBudget -= TimeSpan.FromSeconds(round.OfType<JsonObject>().Sum(record => record["stream_elapsed_seconds"]?.GetValue<double>() ?? 0));
            int[] slowOpen = [.. round.OfType<JsonObject>().Where(record =>
                (record["kind"]?.GetValue<string>() is null or "slow_closure") && !LoopClosureCheck.Allows(record["loop_closure"] as JsonObject))
                .SelectMany(record => (record["owner_layer_ids"] as JsonArray ?? []).Select(SceneGraph.Int)).OfType<int>()];
            if (slowOpen.Length > 0 && !orchestrator.request.RetimeSlowComponents && !orchestrator.request.BudgetOnlyRetime && !orchestrator.request.SpeedUnmeasured &&
                ((result["loop"]?["candidates"] as JsonArray)?.FirstOrDefault()?["slow_components"] as JsonArray ?? []).OfType<JsonObject>().Any(component =>
                    component["retimable"]?.GetValue<bool>() == true && SceneGraph.Int(component["owner_layer_id"]) is int owner && slowOpen.Contains(owner)))
            {
                var trial = new AnalysisOrchestrator(orchestrator.request with { RetimeSlowComponents = true }, analyze, space,
                    space.Budget(), tools, Path.Combine(run, $"slow-retime-{slowProbes.Count}"), cache, token, memo, progress);
                JsonObject retimed = await trial.SelectAsync();
                if (Admission.Accepted(retimed))
                {
                    (orchestrator, result) = (trial, retimed);
                    continue;
                }
            }
            if (next is null) break;
            orchestrator = new AnalysisOrchestrator(next, analyze, space, space.Budget(), tools, Path.Combine(run, $"probe-live-{++rounds}"),
                cache, token, memo, progress);
            using (AnalysisTiming.Measure("k_probe_replan")) result = await orchestrator.SelectAsync();
        }
        if (slowProbes.Count > 0) result["slow_closure_probe"] = slowProbes;
        if (residualProbes.Count > 0) result["residual_probe"] = residualProbes;
        if (streamProbes.Count > 0) result["video_stream_probe"] = streamProbes;
        // 证出的动态组仍越线（没有能再留实时的组）：按预计不省电拒（判据见 NoBenefit，只认证出的动态组）。
        if (Admission.Accepted(result) && NoBenefit.AnalysisConditions(result).Contains(NoBenefit.TooManyStreams))
            NoBenefit.Apply(result, orchestrator.request.AllowNoBenefit);
        if (speedProbes.Count > 0) result["slow_speed_probe"] = speedProbes;
        // 改方案后生成不了：裁定与结论按预检读数重算（HybridSuitability 判有证明的不能）。
        if (slowProbes.Count + residualProbes.Count + streamProbes.Count > 0 && !Admission.Accepted(result))
        {
            result["suitability"] = HybridSuitability.Verdict(result);
            PlanNarrative.Attach(result);
        }
        result["analysis_timing"] = timing.ToJson();
        string stagedPlan = Path.Combine(run, "selected-plan.json");
        await VideoSceneBuilder.WriteJsonAsync(stagedPlan, result, token);
        File.Move(stagedPlan, Path.Combine(root, "plan.json"), true);
        // 逐状态导出只改内存里的结果（母 plan 的 states[] 记子 plan 路径与结论），不回写上面落盘的 plan.json。
        if (space.ExportStates) await orchestrator.ExportStatesAsync(result, export!);
        return result;
    }

    /// <summary>慢分量闭合预检没闭合、所有者层因此留实时的原因码（不是用户要求的 retained_by_cost_trial）。</summary>
    internal const string SlowClosureNotClosed = "slow_closure_not_closed";
    /// <summary>起点搜索证出烘焙第一层必拒的残差组里的粒子（读数见 residual_probe）。</summary>
    internal const string ResidualRetainReason = "residual_seam_over_limit";
    /// <summary>证出的动态组超过路数上限、退回的组（读数见 video_stream_probe）。</summary>
    internal const string StreamRetainReason = "video_streams_over_limit";
    /// <summary>动态组判定（main 没有的渲染）可用的墙钟：到烘焙预检为止分析墙钟的这个比例，整张分析不超过原来的 1.5 倍。</summary>
    internal const double StreamBudgetFraction = 0.5;

    /// <summary>
    /// 烘焙预检（<see cref="SlowClosureProbe.RunAsync"/>）一轮读数之后的下一次分析请求；没有要再留实时的层时返回 null。
    /// 没闭合的缓变分量所有者层（<see cref="SlowClosureNotClosed"/>）、起点搜索证出第一层必拒的残差组里的粒子（<see cref="ResidualRetainReason"/>，
    /// 并上它们原来的未解析原因）、证出越线时点名退回的动态组的根（<see cref="StreamRetainReason"/>）留实时；
    /// 从选中方案实际留实时的层接着加（分配回退、逐组退回点名的层都在里面），plan 已带的原因跟着走。只加不减，层数有限，循环必然停下。
    /// 每条读数记录写 retained_live_layer_ids（这条读数让哪些层留实时）；留实时按分配单元整单元生效（<see cref="Allocation"/>，
    /// 整单元写这些原因码），同单元里没点名、原来也不实时的层（例如粒子挂在下面的全屏背景）记进同一条的 carried_layer_ids。
    /// </summary>
    internal static HybridAnalyzeRequest? ProbeReplan(JsonObject plan, JsonArray round, HybridAnalyzeRequest request)
    {
        static IEnumerable<int> Ids(JsonNode? node) => (node as JsonArray ?? []).Select(SceneGraph.Int).OfType<int>();
        int[] retained = [.. Ids(plan["settings"]?["retain_live_root_ids"])];
        HashSet<int> alreadyLive = [.. Ids(plan["live_layer_ids"])];
        var reasons = HybridLoopAllocation.RetainReasons(plan, []) ?? [];
        var unitOf = (plan["layers"] as JsonArray ?? []).OfType<JsonObject>().Where(layer => SceneGraph.Int(layer["id"]) is int)
            .DistinctBy(layer => SceneGraph.Int(layer["id"])).ToDictionary(layer => SceneGraph.Int(layer["id"])!.Value,
                layer => SceneGraph.Int(layer["allocation_root"] ?? layer["root"]) ?? SceneGraph.Int(layer["id"])!.Value);
        var live = new List<int>();
        void Retain(JsonObject record, IEnumerable<int> named, string reason)
        {
            int[] ids = [.. named.Except(retained).Except(live).Distinct()];
            foreach (int id in ids) reasons[id] = [.. reasons.GetValueOrDefault(id, []).Append(reason).Distinct()];
            record["retained_live_layer_ids"] = new JsonArray([.. ids.Select(id => (JsonNode)id)]);
            var units = ids.Where(unitOf.ContainsKey).Select(id => unitOf[id]).ToHashSet();
            record["carried_layer_ids"] = new JsonArray([.. unitOf.Where(pair => units.Contains(pair.Value) && !ids.Contains(pair.Key) &&
                !alreadyLive.Contains(pair.Key)).Select(pair => pair.Key).Order().Select(id => (JsonNode)id)]);
            live.AddRange(ids);
        }
        foreach (JsonObject record in round.OfType<JsonObject>())
            switch (record["kind"]?.GetValue<string>() ?? "slow_closure")
            {
                case "slow_closure" when !LoopClosureCheck.Allows(record["loop_closure"] as JsonObject):
                    Retain(record, Ids(record["owner_layer_ids"]), SlowClosureNotClosed);
                    break;
                case "residual_start_search" when record["status"]?.GetValue<string>() == "rejected_residual_above_limits":
                    int[] particles = [.. Ids(record["particle_layer_ids"])];
                    foreach (var (id, codes) in HybridLoopAllocation.RetainReasons(plan, particles) ?? [])
                        reasons[id] = [.. reasons.GetValueOrDefault(id, []).Concat(codes).Distinct()];
                    Retain(record, particles, ResidualRetainReason);
                    break;
                case "video_streams" when record["retreat_root_ids"] is JsonArray roots:
                    Retain(record, Ids(roots), StreamRetainReason);
                    break;
            }
        return live.Count == 0 ? null : request with { RetainLiveRootIds = [.. retained, .. live], RetainLiveReasons = reasons };
    }

    /// <summary>
    /// 速度实测之后要不要整套重分析：看得出（visible）按逐项预算（BudgetOnlyRetime），挡住循环的记不能；
    /// 量不到（not_measured：第 0 帧不同、没有纹理、渲染失败、所有者层不全在视频组、补丁没写进或没生效）同样不放开这些改速，
    /// 但挡住循环的记未收敛（SpeedUnmeasured）。放行或已经重分析过返回 null。
    /// </summary>
    internal static HybridAnalyzeRequest? AfterSpeedProbe(HybridAnalyzeRequest request, string status) => status switch
    {
        _ when request.BudgetOnlyRetime => null,
        "visible" => request with { BudgetOnlyRetime = true, SpeedUnmeasured = false },
        "not_measured" when !request.SpeedUnmeasured => request with { SpeedUnmeasured = true },
        _ => null
    };

    private async Task<JsonObject> SelectAsync()
    {
        string requested = request.Preset ?? "balanced", interaction = space.Interactions[0];
        var selected = await SolveAsync(interaction);
        JsonObject result = selected.Plan;
        JsonArray selectedCosts = costs.DeepClone().AsArray();
        bool alternativesTried = !Admission.Accepted(result);
        if (alternativesTried)
        {
            foreach (string mode in space.Interactions.Skip(1))
            {
                (JsonObject Plan, JsonArray Reasons) alternative;
                using (AnalysisTiming.Measure("b_interaction_alternative")) alternative = await SolveAsync(mode);
                if (!Admission.Accepted(alternative.Plan)) continue;
                await SuggestAsync(result, mode, alternative.Plan);
                break;
            }
            if (Admission.Bakeable(result) && Admission.GroupCount(result) > Admission.MaxVideoGroups(result))
            {
                PlanBlockers.Add(result, new Blocker(BlockerCode.TooManyVideoGroups, [Admission.MaxVideoGroups(result)]));
                result["status"] = "requires_resolution";
                result["preset_rejection_reason"] = "too_many_video_groups";
                result["suitability"] = HybridSuitability.Verdict(result);
                PlanNarrative.Attach(result);
            }
        }
        result = await RetreatAsync(result, interaction);
        // 只有普通图层的视频组省不下渲染，还给实时（加 --retain-live 重新分析）；重新分析能生成、预计不省电的条件（含省下多少算不出）没变多才采用。
        // --no-benefit allow 时照旧全烘，测功耗用。
        if (Admission.Accepted(result) && !request.AllowNoBenefit && await NoBenefit.PlainGroupRetainRootsAsync(result, token) is { Length: > 0 } plain)
        {
            using var plainGroups = AnalysisTiming.Measure("d_plain_groups_live");
            var (replanned, _) = await new AnalysisOrchestrator(request with { RetainLiveRootIds = plain }, analyze, space, space.Budget(), tools,
                Path.Combine(run, "plain-groups-live"), cache, token, memo).SolveAsync(interaction);
            // 任何 unknown 且没有可比较的特效覆盖都算一条：还给实时要证得出剩下的不更不省电，
            // 不能靠换成前缀等另一种 unknown 让普通组留实时（实时画布变大）。
            static int Unproven(JsonObject plan) => NoBenefit.AnalysisConditions(plan).Length +
                (plan[BakeValueAssessment.Field]?["status"]?.GetValue<string>() == "unknown" && NoBenefit.RemovedPassCoverage(plan) is null ? 1 : 0);
            if (Admission.Accepted(replanned) && Unproven(replanned) <= Unproven(result))
                result = replanned;
        }
        // 预计不省电的方案默认拒绝（判据与覆盖见 NoBenefit）；已经被别的原因拒掉的不重复写，只差采集能力的按假设能采集判。
        if (Admission.Accepted(result) || NoBenefit.CaptureOpenConditions(result) is not null) NoBenefit.Apply(result, request.AllowNoBenefit);
        DaytimeSplit.RejectFixedState(result, request.AllowNoBenefit);
        // 判"不省电"而有可关的交互项（指针、音频层）时也试关交互，与生成不了时同一条建议；试出能生成且省电才建议。不自动关，applied_tradeoffs 不变。
        // 请求的策略生成不了时上面已验证过替代策略，不再重试。
        if (result[NoBenefit.Field]?["status"]?.GetValue<string>() == NoBenefit.ExpectedStatus && !alternativesTried &&
            space.Interactions.Skip(1).Contains("off") && (result["layers"] as JsonArray ?? []).OfType<JsonObject>().Any(layer => !InteractionPolicy.Protected(layer) &&
                (layer["tradeoff_kinds"] as JsonArray ?? []).Any(kind => kind?.GetValue<string>() is "pointer" or "audio")))
        {
            using var offTrial = AnalysisTiming.Measure("e_interaction_off_trial");
            var (off, _) = await SolveAsync("off");
            if (Admission.Accepted(off) && await RetreatAsync(off, "off") is var retreated && Viable(retreated)) await SuggestAsync(result, "off", retreated);
        }
        // 尝试经过只写进既有的 preset_* / interaction_* 字段。
        result["preset_requested"] = requested;
        result["preset_applied"] = Admission.Accepted(result) ? result["settings"]?["preset"]?.DeepClone() ?? JsonValue.Create(requested) : JsonValue.Create("none");
        result["preset_fallback_reason"] = selected.Reasons.Count == 0 ? null : string.Join("; ", selected.Reasons.Select(n => n!.GetValue<string>()));
        result["interaction_requested"] = interaction;
        result["interaction_costs"] = selectedCosts;
        result["custom_settings"] = request.CustomSettings;
        result["live_overlays_hoisted"] = result["occlusion_tradeoff"]?["status"]?.GetValue<string>() == "applied"
            ? result["occlusion_tradeoff"]?["promoted_roots"]?.DeepClone() : new JsonArray();
        var omitted = (result["layers"] as JsonArray ?? []).OfType<JsonObject>().Where(l => l["allocation"]?.GetValue<string>() == "excluded").ToArray();
        var kinds = omitted.SelectMany(l => (l["tradeoff_kinds"] as JsonArray ?? []).Select(k => k!.GetValue<string>())).ToHashSet();
        foreach (JsonObject decision in selectedCosts.OfType<JsonObject>().Where(c => c["decision"]?.GetValue<string>() == "omit"))
            kinds.Add(decision["kind"]!.GetValue<string>());
        if (interaction != "keep" && result["has_parallax"]?.GetValue<bool>() == true) kinds.Add("parallax");
        result["applied_tradeoffs"] = new JsonObject { ["properties"] = new JsonObject(),
            ["excluded_layer_ids"] = result["settings"]?["excluded_layer_ids"]?.DeepClone() ?? new JsonArray(),
            ["turn_off_kinds"] = new JsonArray(kinds.Order().Select(k => (JsonNode)JsonValue.Create(k)).ToArray()),
            ["daytime_state"] = result["settings"]?["daytime_state"]?.DeepClone() };
        if (Admission.Accepted(result) && result["summary"] is JsonObject summary)
            foreach (string language in new[] { "zh", "en" })
                summary[language] = MessageCatalog.Get("preset.generated", language) + (kinds.Count == 0 ? "" : "\n" +
                    MessageCatalog.Get("preset.omitted", language, string.Join(language == "zh" ? "、" : ", ", kinds.Order().Select(k => TradeoffOptions.KindLabel(k, language))))) +
                    (result["settings"]?["daytime_state"] is JsonValue state ? "\n" + MessageCatalog.Get("preset.daytime", language, MessageCatalog.DaytimeStateLabel(state.GetValue<string>(), language)) : "");
        return result;
    }

    /// <summary>把验证过的替代交互策略记成一键建议（suggested_change），方案落盘供查看；不改 result 的判定。</summary>
    private async Task SuggestAsync(JsonObject result, string mode, JsonObject plan)
    {
        string path = Path.Combine(run, "suggested-" + mode + ".json");
        await VideoSceneBuilder.WriteJsonAsync(path, plan, token);
        string key = mode == "fixed" ? "interaction.suggest_fixed" : "interaction.suggest_off";
        result["suggested_change"] = new JsonObject { ["verified"] = true, ["plan_path"] = path,
            ["settings"] = new JsonObject { ["interaction"] = mode },
            ["zh"] = MessageCatalog.Get(key, "zh"), ["en"] = MessageCatalog.Get(key, "en") };
    }

    /// <summary>能生成且没有已证不省电条件，路数按没证出静态的组数（上界）算。
    /// 选中方案的上界越线时，编排收尾按渲染证据证出动态组（<see cref="SlowClosureProbe.StreamsAsync"/>），证出越线再退。</summary>
    private bool Viable(JsonObject plan) => Admission.Accepted(plan) && (request.AllowNoBenefit ||
        !DaytimeSplit.FixedState(plan) && NoBenefit.AnalysisConditions(plan).Length == 0 && !NoBenefit.TooManyVideoStreams(Admission.GroupCount(plan)));

    /// <summary>同口径的特效覆盖减视频路数；省下的覆盖没算出或不能生成就不可比较。</summary>
    internal static double? Margin(JsonObject plan) => Admission.Accepted(plan) && NoBenefit.RemovedPassCoverage(plan) is double removed
        ? removed - Admission.GroupCount(plan) * NoBenefit.MinPassCoveragePerStream : null;

    /// <summary>实时画布：留实时图层的画布占比之和（plan.layers[].canvas_fraction，缺值按 0）。</summary>
    private static double LiveCanvas(JsonObject plan) => (plan["layers"] as JsonArray ?? []).OfType<JsonObject>()
        .Where(layer => layer["allocation"]?.GetValue<string>() == "live").Sum(layer => SceneGraph.Numeric(layer["canvas_fraction"], 0));

    /// <summary>
    /// 读帧缓冲规则放宽涉及的读取层：之前画过网格、读当前帧缓冲，plan 里没按读帧缓冲留实时（reasons 里没有 reads_current_framebuffer），
    /// 也没被剔除或省略。进了视频、不绘制、或后来被回退与退回以别的原因留实时的都算：放宽前它们在实时判定阶段就留实时，
    /// 依赖它们的层也跟着留实时，分配从那里起就不同。没有运行时证据时为空。
    /// </summary>
    private async Task<int[]> RelaxedFramebufferReadersAsync(JsonObject plan)
    {
        if (plan["runtime_evidence"]?.GetValue<string>() is not string runtime || !File.Exists(runtime)) return [];
        var relaxed = (plan["layers"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(layer => layer["allocation"]?.GetValue<string>() is not ("excluded" or "omitted") &&
                !(layer["reasons"] as JsonArray ?? []).Any(reason => reason?.GetValue<string>() == "reads_current_framebuffer"))
            .Select(layer => SceneGraph.Int(layer["id"])).OfType<int>().ToHashSet();
        return [.. Liveness.FramebufferReads(JsonNode.Parse(await File.ReadAllTextAsync(runtime, token))?["runtime_layers"] as JsonArray ?? [])
            .Select(read => read.Reader).Where(relaxed.Contains).Distinct()];
    }

    /// <summary>
    /// 放进视频的层多了反而不行（整层无解、被阻断、预计视频成本高于省下的渲染）时，退回更多层留实时的方案：每轮把每个视频组各留一次实时
    /// 重新分析（连同 plan 已留的和分配回退已点名的层），能生成且预计省电的候选里取预计收益（省下的渲染减视频路数）最大的；一个都没有时，
    /// 从"能生成、只差省电或路数"且离可行的差距比这一轮起点小的候选里取差距最小的接着退。只加实时层、不减，证明不能循环的层照旧留实时。都不行时原样返回。
    /// 一轮里各组的重查互不依赖（各自的 retreat-N 目录与调用预算），同时跑至多 min(组数, <see cref="AnalysisMemo.RetreatParallelism"/>) 个；
    /// 序号与进度仍按组序发，全部跑完后按组序比较，选中的与逐个串行跑时相同。
    /// </summary>
    private async Task<JsonObject> RetreatAsync(JsonObject result, string interaction)
    {
        // 离可行的差距：只在特效覆盖有数值时比较；未算出覆盖或不能生成时没有可比较的差距。
        static double? Gap(JsonObject plan) => Margin(plan) is double margin
            ? Math.Max(-margin, Admission.GroupCount(plan) - NoBenefit.SavingProvenStreams) : null;
        static IEnumerable<int> Ids(JsonNode? node) => (node as JsonArray ?? []).Select(SceneGraph.Int).OfType<int>();
        if (Viable(result) || Admission.Accepted(result) && !NoBenefit.AnalysisConditions(result).Contains(NoBenefit.VideoCostOverSaving)) return result;
        // 只有每个候选必然仍命中的条件才叫改不了，这时直接不退：每个候选都是一整套重查（档位 × 状态 × 布局），结果不必和原方案同路。
        // 静态成品带实时层：被烘内容是静态的，多留实时剩下的仍是静态的子集。固定时段：只有请求本身指定了时段，候选才都固定在它上面；
        // 否则候选重查可以选中不分时段的母 plan。blocker、组数超限都不算：把挡住的那组留实时、或留实时后全幅布局变得可行
        // （一下并成一组），都可能解开（3463280673 在 main 上就是这样逐组退回救回的）。
        string[] conditions = NoBenefit.AnalysisConditions(result);
        if (conditions.Contains(NoBenefit.StaticWithLive) || request.DaytimeState is not null && DaytimeSplit.FixedState(result))
            return result;
        JsonObject current = result;
        while (current["video_groups"] is JsonArray { Count: > 1 } groups)
        {
            int[] kept = [.. Ids(current["settings"]?["retain_live_root_ids"]).Concat(Ids(current["loop_allocation_fallback"]?["retain_live_root_ids"]))];
            // 已留实时层的原因码跟着重查走（分配回退点名层的未解析原因、plan 已带的），被保留层不被盖成只剩 retained_by_cost_trial。
            var keptReasons = HybridLoopAllocation.RetainReasons(current, Ids(current["loop_allocation_fallback"]?["trigger_layer_ids"]));
            var plans = new Task<JsonObject>[groups.Count];
            using (var slots = new SemaphoreSlim(Math.Min(groups.Count, memo.RetreatParallelism)))
            {
                for (int index = 0; index < groups.Count; index++)
                {
                    await slots.WaitAsync();
                    // 每次重查都是一整次分析，耗时成倍增加：报一条阶段信息，命令行与界面不至于看起来卡住。
                    int tried = Interlocked.Increment(ref memo.Retreats);
                    progress?.Report(new("retreating", null, new Message("progress.trying_grouping", [tried])));
                    var trial = new AnalysisOrchestrator(request with { RetainLiveRootIds = [.. kept.Concat(Ids(groups[index]?["root_ids"])).Distinct()],
                        RetainLiveReasons = keptReasons,
                        // 请求里的 JSON 各给一份：解析出来的 JsonObject 首次访问才展开，不能几个线程同时读同一份。
                        UserProperties = request.UserProperties?.DeepClone().AsObject(), PropertiesOrigin = request.PropertiesOrigin?.DeepClone().AsObject(),
                        FrameRateOrigin = request.FrameRateOrigin?.DeepClone().AsObject() },
                        analyze, space, space.Budget(), tools, Path.Combine(run, $"retreat-{tried}"), cache, token, memo);
                    plans[index] = Task.Run(async () =>
                    {
                        try
                        {
                            using (AnalysisTiming.Measure("c_retreat")) return (await trial.SolveAsync(interaction)).Plan;
                        }
                        finally { slots.Release(); }
                    });
                }
                await Task.WhenAll(plans);
            }
            JsonObject? best = null, next = null;
            foreach (JsonObject plan in plans.Select(task => task.Result))
            {
                if (Viable(plan))
                {
                    if (best is null || Margin(plan) is double margin && Margin(best) is double bestMargin && margin > bestMargin)
                        best = plan;
                }
                else if (Gap(plan) is double gap && Gap(next ?? current) is double currentGap && gap < currentGap) next = plan;
            }
            if (best is not null) return best;
            if (next is null) break;
            current = next;
        }
        return result;
    }

    /// <summary>一个交互策略：档位由请求的那档往省电方向逐档试，第一个能生成的就用；每档失败的原因记进 reasons。</summary>
    private async Task<(JsonObject Plan, JsonArray Reasons)> SolveAsync(string interaction)
    {
        var reasons = new JsonArray();
        var current = request with { Interaction = interaction, ViewMode = interaction == "keep" ? "preserve" : "fixed_view",
            LiveOverlayPlacement = request.CustomSettings ? request.LiveOverlayPlacement : "foreground",
            DaytimeSplit = interaction != "keep" || request.DaytimeSplit };
        JsonObject? last = null;
        int[]? off = null;
        foreach (string preset in space.Presets)
        {
            current = current with { Preset = preset, LoopPreference = RetimeProfile.LoopPreferenceForPreset(preset) };
            if (interaction == "off" && off is null)
            {
                // 关交互：先按原图层分析一次，据此测出要剔除的指针内容与代价高的全屏音频层，之后各档都带着这份剔除。
                JsonObject original = await LayoutsAsync(current, "measure", null);
                using (AnalysisTiming.Measure("i_interaction_cost_trial"))
                    (off, costs) = await InteractionPolicy.ExclusionsAsync(original, current with { AnalysisCacheDirectory = cache }, tools,
                        Path.Combine(run, "interaction-cost"), token);
            }
            current = current with { ExcludedLayerIds = off is null ? request.ExcludedLayerIds :
                (request.ExcludedLayerIds ?? []).Concat(off).Distinct().Order().ToArray() };
            // 请求的那档之后的每一档都是档位回退触发的重查。
            using (preset == space.Presets[0] ? default : AnalysisTiming.Measure("a_preset_layout_fallback")) last = await StatesAsync(current);
            if (Admission.Accepted(last)) return (last, reasons);
            reasons.Add(preset + ": " + (Admission.GroupCount(last) > Admission.MaxVideoGroups(last) ? "too_many_video_groups" : last["summary"]?["key"]?.GetValue<string>()));
        }
        return (last!, reasons);
    }

    /// <summary>
    /// 母 plan 识别出状态时，每个状态各规划一次，取能生成且视频组最少的；都不行保留母 plan。
    /// 固定在单个时段的方案成品不随时刻切换，没有 --no-benefit allow 时必被固定时段拒因拒绝（<see cref="DaytimeSplit.RejectFixedState"/>）：
    /// 这时不展开。原来每个档位都要多跑 状态数 × 布局数 次整次分析（2955378002：11 个时段 × 2 个布局 × 2 档 = 48 次里的 44 次，
    /// 退回时每个候选再各来一遍），挑出来的状态方案还会顶替能生成的母 plan、随后被拒。
    /// </summary>
    private async Task<JsonObject> StatesAsync(HybridAnalyzeRequest candidate)
    {
        JsonObject plan = await LayoutsAsync(candidate, "search", null);
        if (!space.Expands(candidate.Interaction!) || !request.AllowNoBenefit || DaytimeSplit.RecognizedStates(plan) is not { } entries) return plan;
        states = Math.Max(states, entries.Length);
        JsonObject? best = Admission.Accepted(plan) ? plan : null;
        for (int index = 0; index < entries.Length; index++)
        {
            string name = entries[index]["name"]!.GetValue<string>();
            JsonObject statePlan = await LayoutsAsync(candidate with { DaytimeState = name }, "search", index + ":" + name);
            if (Admission.Accepted(statePlan) && (best is null || Admission.GroupCount(statePlan) < Admission.GroupCount(best))) best = statePlan;
        }
        return best ?? plan;
    }

    /// <summary>
    /// 布局按顺序试，第一个能生成的就用。例外：能生成的是特效前缀、而整层只被这个布局的冲突挡住（整层本身零阻断、有候选）时，
    /// 后面的布局下整层分组可能省得更多，也试一次，用 <see cref="LayeredSavesMoreAsync"/> 比，取省得多的。
    /// </summary>
    private async Task<JsonObject> LayoutsAsync(HybridAnalyzeRequest candidate, string phase, string? state)
    {
        JsonObject? plan = null;
        foreach (string layout in space.Layouts)
        {
            JsonObject tried;
            using (layout == space.Layouts[0] ? default : AnalysisTiming.Measure("a_preset_layout_fallback"))
                tried = await TryAsync(candidate with { VideoLayout = layout }, phase, state);
            if (plan is not null && Admission.Accepted(plan)) return await LayeredSavesMoreAsync(plan, tried) ? tried : plan;
            plan = tried;
            if (Admission.Accepted(plan) && !(phase == "search" && PrefixOverLayoutConflict(plan))) break;
        }
        return plan!;
    }

    /// <summary>特效前缀接手只是因为整层被布局冲突挡住：整层自己零阻断、有循环候选，冲突记在 whole_layer.layout_conflict。</summary>
    private static bool PrefixOverLayoutConflict(JsonObject plan) => plan["route"]?.GetValue<string>() == "effect_prefix" &&
        plan["whole_layer"] is JsonObject whole && whole["layout_conflict"] is JsonValue &&
        whole["blockers"] is JsonArray { Count: 0 } && whole["loop"]?["candidates"] is JsonArray { Count: > 0 };

    /// <summary>
    /// 换个布局的整层方案是否可证明比前缀多省特效渲染：整层的已知覆盖严格超过前缀所有者层全部特效的上界，
    /// 且新增视频路数不更多。前缀自身的覆盖没算出，不能拿 unknown 当零；也不能把仍实时的后缀当已缓存。
    /// </summary>
    private async Task<bool> LayeredSavesMoreAsync(JsonObject prefix, JsonObject whole)
    {
        if (!Viable(whole)) return false;
        if (!Viable(prefix)) return true;
        if (NoBenefit.RemovedPassCoverage(whole) is not double removed ||
            prefix["runtime_evidence"]?.GetValue<string>() is not string path || !File.Exists(path)) return false;
        var owners = (prefix["effect_prefix_caches"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(cache => SceneGraph.Int(cache["owner_layer_id"])).OfType<int>().ToHashSet();
        JsonObject[] observed = [.. (JsonNode.Parse(await File.ReadAllTextAsync(path, token))?["runtime_layers"] as JsonArray ?? [])
            .OfType<JsonObject>().Where(layer => SceneGraph.Int(layer["owner"]) is int owner && owners.Contains(owner))];
        if (owners.Count == 0 || owners.Any(owner => !observed.Any(layer => SceneGraph.Int(layer["owner"]) == owner && layer["materials"] is JsonArray)))
            return false;
        return WholeEffectWorkProvenGreater(whole, prefix, removed, BakeValueAssessment.PassCoverage(prefix, observed));
    }

    /// <summary>整层覆盖为两位小数记录，下界减半个末位；前缀的全部 owner 特效只是上界。</summary>
    internal static bool WholeEffectWorkProvenGreater(JsonObject whole, JsonObject prefix, double wholeCoverage,
        double prefixCoverageUpper) => double.IsFinite(wholeCoverage) && double.IsFinite(prefixCoverageUpper) &&
        Admission.GroupCount(whole) <= Admission.GroupCount(prefix) && wholeCoverage - 0.005 > prefixCoverageUpper;

    private async Task<JsonObject> TryAsync(HybridAnalyzeRequest candidate, string phase, string? state)
    {
        budget.Charge($"{phase}|{candidate.Interaction}|{candidate.Preset}|{candidate.VideoLayout}|{state ?? "-"}", states);
        return await AdoptAllocationAsync(await analyze(candidate with {
            OutputDirectory = Path.Combine(run, (++attempt).ToString()), AnalysisCacheDirectory = cache }, token), token, memo);
    }

    /// <summary>
    /// 逐状态子 plan：选定结果识别出状态时，每个状态按原请求（不经级联改写）再分析一次，补生成准入后落盘，
    /// 并在结果的 daytime_split.states[] 里记下子 plan 路径、结论 key、阻断数、实时层数与视频组数。
    /// </summary>
    private async Task ExportStatesAsync(JsonObject result, StateExport export)
    {
        if (DaytimeSplit.RecognizedStates(result) is not { } entries) return;
        states = Math.Max(states, entries.Length);
        for (int index = 0; index < entries.Length; index++)
        {
            JsonObject state = entries[index];
            string stateName = state["name"]!.GetValue<string>();
            budget.Charge($"export|-|-|-|{index}:{stateName}", states);
            JsonObject statePlan = await analyze(request with {
                OutputDirectory = Path.Combine(request.OutputDirectory, "state-" + stateName), DaytimeState = stateName }, token);
            // 子 plan 不经档位与布局的搜索，生成准入要在这里补上，否则它会说能生成、bake 第一步才拒。
            Admission.ApplyGenerationAdmission(statePlan);
            string statePlanPath = export.PlanPath(stateName);
            await using (var stateFile = new FileStream(statePlanPath, FileMode.CreateNew, FileAccess.Write))
                await JsonSerializer.SerializeAsync(stateFile, statePlan, StatePlanJson, token);
            state["plan"] = statePlanPath;
            state["status"] = statePlan["status"]?.DeepClone();
            state["summary_key"] = statePlan["summary"]?["key"]?.DeepClone();
            state["blocker_count"] = (statePlan["blockers"] as JsonArray)?.Count;
            state["live_layer_count"] = (statePlan["live_layer_ids"] as JsonArray)?.Count;
            state["video_group_count"] = (statePlan["video_groups"] as JsonArray)?.Count;
            export.Analyzed?.Invoke(stateName, statePlan);
        }
    }

    internal static async Task<JsonObject> AdoptAllocationAsync(JsonObject plan, CancellationToken token, AnalysisMemo? memo = null)
    {
        await VerifyStaticGroupBudgetAsync(plan, token, memo);
        Admission.ApplyGenerationAdmission(plan);
        if (Admission.Accepted(plan) || plan["loop_allocation_fallback"]?["status"]?.GetValue<string>() != "candidate_found" ||
            plan["analysis_directory"]?.GetValue<string>() is not string directory) return plan;
        string path = Path.Combine(directory, "loop-allocation-analysis", "plan.json");
        if (!File.Exists(path)) return plan;
        JsonObject child = JsonNode.Parse(await File.ReadAllTextAsync(path, token))!.AsObject();
        await VerifyStaticGroupBudgetAsync(child, token, memo);
        Admission.ApplyGenerationAdmission(child);
        if (!Admission.Accepted(child) || !JsonNode.DeepEquals(child["source_sha256"], plan["source_sha256"])) return plan;
        child["allocation_adopted"] = new JsonObject { ["plan_path"] = path,
            ["retain_live_root_ids"] = child["settings"]?["retain_live_root_ids"]?.DeepClone(),
            ["basis"] = plan["loop_allocation_fallback"]?["resolution_basis"]?.DeepClone() };
        return child;
    }

    /// <summary>整层方案逐组做源与运行时的静态证明（只用 CPU），已证静态的组不计视频路数（<see cref="Admission.GroupCount"/>）。
    /// 不论组数都证：路数上限、预计省电、退回的差距用同一口径，组数跨过上限时路数不跳变。
    /// 这是准入估计；烘焙仍录完整区间，声称静态却有变化的组整案拒绝。
    /// 每组的证明只取决于源、运行时证据的内容、素材目录、这组的图层与帧率（<see cref="LoopAnalysis.Analyze"/> 其余参数取默认值）：
    /// 有记忆时按这些做键，退回各轮与各布局里没变的组不再重证，源与运行时证据也只在要证时才解析。</summary>
    private static async Task VerifyStaticGroupBudgetAsync(JsonObject plan, CancellationToken token, AnalysisMemo? memo)
    {
        if (!Admission.Bakeable(plan) || plan["route"]?.GetValue<string>() != "whole_layer" ||
            plan["video_groups"] is not JsonArray groups ||
            plan["source"]?.GetValue<string>() is not string sourcePath ||
            plan["runtime_evidence"]?.GetValue<string>() is not string runtimePath || !File.Exists(runtimePath)) return;
        string sourceKey = plan["source_sha256"]?.GetValue<string>() ?? sourcePath;
        if (plan["static_group_budget"] is JsonObject prior &&
            prior["source"]?.GetValue<string>() == sourceKey && prior["runtime_evidence"]?.GetValue<string>() == runtimePath &&
            prior["status"]?.GetValue<string>() == "verified") return;
        foreach (JsonObject group in groups.OfType<JsonObject>())
        {
            group["static_verified"] = false;
            group.Remove("static_verification");
        }
        try
        {
            string runtimeText = await File.ReadAllTextAsync(runtimePath, token);
            string evidence = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(runtimeText)));
            JsonObject settings = plan["settings"]?.AsObject() ?? new JsonObject();
            uint fpsNumerator = settings["fps_numerator"]?.GetValue<uint>() ?? 120;
            uint fpsDenominator = settings["fps_denominator"]?.GetValue<uint>() ?? 1;
            string? assets = plan["assets"]?.GetValue<string>() ?? settings["assets"]?.GetValue<string>();
            ProjectSource? source = null;
            JsonObject? runtime = null, scene = null;
            try
            {
                foreach (JsonObject group in groups.OfType<JsonObject>())
                {
                    int[] layers = (group["layer_ids"] as JsonArray)?.Select(node => node!.GetValue<int>()).ToArray() ?? [];
                    if (layers.Length == 0) continue;
                    bool Prove()
                    {
                        source ??= new ProjectSource(sourcePath);
                        runtime ??= JsonNode.Parse(runtimeText)!.AsObject();
                        scene ??= source.ReadJson(source.SceneResource);
                        return LoopAnalysis.Analyze(scene.DeepClone().AsObject(), source, assets, runtime, layers,
                            fpsNumerator, fpsDenominator).ToJson()["source_static"]?.GetValue<bool>() == true;
                    }
                    bool proven = memo is null ? Prove() : await memo.StaticProofAsync(
                        "static-proof|" + AnalysisCache.Key(sourceKey, evidence, assets, layers, fpsNumerator, fpsDenominator), () => Task.FromResult(Prove()));
                    if (!proven) continue;
                    group["static_verified"] = true;
                    group["static_verification"] = new JsonObject { ["basis"] = "source_and_runtime_static_proof",
                        ["runtime_evidence"] = runtimePath, ["source"] = sourceKey };
                }
            }
            finally { source?.Dispose(); }
            plan["static_group_budget"] = new JsonObject { ["status"] = "verified", ["basis"] = "source_and_runtime_static_proof",
                ["source"] = sourceKey, ["runtime_evidence"] = runtimePath };
        }
        catch (Exception error) when (error is IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            foreach (JsonObject group in groups.OfType<JsonObject>())
            {
                group["static_verified"] = false;
                group.Remove("static_verification");
            }
            plan["static_group_budget"] = new JsonObject { ["status"] = "unavailable", ["basis"] = "source_and_runtime_static_proof",
                ["source"] = sourceKey, ["runtime_evidence"] = runtimePath, ["reason"] = error.Message };
        }
    }
}
