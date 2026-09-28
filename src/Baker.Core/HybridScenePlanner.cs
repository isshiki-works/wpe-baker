using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Baker.Core;

/// <remarks>Width/Height 同为 0 表示未指定：分析时按 <see cref="OutputResolution"/> 取主显示器分辨率，结果与来源写回 plan.settings。</remarks>
public sealed record HybridAnalyzeRequest(int SchemaVersion, string Source, string Assets, string OutputDirectory,
    uint Width = 0, uint Height = 0, uint FpsNumerator = 120, uint FpsDenominator = 1,
    JsonObject? UserProperties = null, string ViewMode = "preserve", double MaximumRetimePercent = 2,
    bool AllowLocalSeamRepair = false, string? RuntimeTraceFile = null, string? DeviceUuid = null,
    int[]? RetainLiveRootIds = null, string VideoLayout = "full_frame",
    string LiveOverlayPlacement = "preserve", string LiveTextEffects = "preserve", string AudioEffects = "preserve",
    int[]? ExcludedLayerIds = null, string LoopPreference = "balanced", string VideoShell = "reject", string? ResolutionSource = null,
    // 显式允许生成预计不省电的方案（--no-benefit allow）。默认 false 不写进 settings；true 时随 settings 走，烘焙期间重分析也保持。
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool AllowNoBenefit = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? LoopLengthMaximumSeconds = null,
    // 属性来源记录（WallpaperEngineProperties.Merge 的 Origin）：只抄进 plan 的 properties_source / wpe_properties，不写进 settings。
    // WPE 值本身已并进 UserProperties，所以 bake 期间按 settings 重新分析时不会再去读 config.json，结果确定。
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonObject? PropertiesOrigin = null,
    // 帧率来源记录（OutputFrameRate.Choice.ToJson()）：只抄进 plan 的 frame_rate，不写进 settings。
    // 帧率本身已经定死在 FpsNumerator 上，所以 bake 期间按 settings 重新分析时不会再去读 config.json 或显示器。
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonObject? FrameRateOrigin = null,
    // 三档预设（efficiency|balanced|quality）：档位给观感改动预算与长度上限兜底，循环长度是求解结果。
    // null = 调用方没选档（旧 plan、旧接口），按"不设预算 + 600 s 上限 + MaximumRetimePercent"的旧行为走，plan 逐字节不变。
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Preset = null,
    // 高级覆盖：观感改动预算（百分比，同时是通用分量调速预算）。给了就压过档位，plan 的 profile 里标 override。
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? RetimeBudgetPercent = null,
    // feat/daytime-split：昼夜/时段壁纸的状态拆分（默认关，关闭时 plan 逐字不变）。DaytimeState 给定时按该状态规划：
    // 选择器脚本不再算实时控制器，它切换的受控层里不属于该状态的按剔除处理、属于该状态的视为可见。
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool DaytimeSplit = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DaytimeState = null,
    [property: JsonIgnore] bool CustomSettings = false,
    [property: JsonIgnore] string? AnalysisCacheDirectory = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Interaction = null,
    [property: JsonIgnore] bool LayoutExplicit = false,
    // WPE 设置里的后处理画质档（config.json general.user.postprocessing）。只有它是 ultra/displayhdr 且场景 hdr、bloom 都开时，
    // 官方走浮点 HDR 管线；plan 的 settings 只在这种场景里记它，其余 plan 逐字不变。
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Postprocessing = null,
    // 自动留实时（分配回退、慢分量预检、残差粒子重试）时触发留实时的原因码：图层 id → 原来的未解析原因（HybridLoopAllocation.RetainReasons）。
    // 重查时所在单元写这些原因，不写 retained_by_cost_trial（那条只给用户 --retain-live 与退回轮）；没有时不写进 settings，plan 逐字不变。
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Dictionary<int, string[]>? RetainLiveReasons = null,
    // 入场切换的退回（旧行为）：加载即播的单次轨所属层也判实时，bake 不做入场切换。分析引出新 blocker 或合成门拒绝切换时自动打开；
    // 默认关时不写进 settings，plan 逐字不变。
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool SingleShotLive = false,
    // 组周期的退回：含这些图层的视频组不按自身周期缩短，录全局 L 帧（LoopAnalysis.GroupPeriods）。只在请求显式给出时生效：
    // 烘焙的自身周期退回已删（没有缓变分量的组按解析周期精确闭合，有的由分析的慢分量预检在 P_g 上判）；默认空时不写进 settings，plan 逐字不变。
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int[]? FullLoopLayerIds = null,
    // 慢项实测的退回：振幅推不出的慢项一律按逐项预算（不放开改速，见 LoopAnalysis）。分析收尾的速度实测（SlowClosureProbe.SpeedAsync）
    // 没放行（看得出、量不到或渲染失败）时自动打开，随 settings 走，烘焙前刷新循环与分析同一口径；默认关时不写进 settings，plan 逐字不变。
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool BudgetOnlyRetime = false,
    // 按读帧缓冲留实时的读取层（放宽前的判定）：之前全是静态内容的读取层默认不留实时，分析编排发现放宽让结果变差时点名这些层重查。
    // 在实时判定阶段生效（与 --retain-live 不同，不占用分配回退）；随 settings 走，默认空时不写进 settings，plan 逐字不变。
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int[]? LiveFramebufferReaderIds = null,
    // 慢分量改速：带可用旋钮的慢分量（loop.candidates[].slow_components[].retimable）改挂旋钮改速或冻结，放不放行由速度实测。
    // 慢分量闭合预检没过时自动打开，随 settings 走；BudgetOnlyRetime 打开后不再生效。默认关时不写进 settings，plan 逐字不变。
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool RetimeSlowComponents = false,
    // 速度实测量不到（not_measured）后的重分析：求解同 BudgetOnlyRetime（不放开要实测的改速与冻结），但这些项挡住循环时记未收敛、不记不能。
    // 只由编排层在测速量不到时打开，随 settings 走；默认关时不写进 settings，plan 逐字不变。
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool SpeedUnmeasured = false);

/// <summary>Plans video replacement from source hierarchy and observed input dependencies.</summary>
/// <param name="display">未指定宽高时用来铺满的屏幕尺寸；省略时读本机主显示器物理分辨率，测试可注入固定值。</param>
public sealed class HybridScenePlanner(NativeTools tools, Func<(uint Width, uint Height)?>? display = null)
{

    /// <summary>把命令行/界面上的三档名字翻成求解器的择优倾向；非法值在请求校验里已被挡下。</summary>
    public static CommonLoopPreference LoopPreferenceOf(string value) => value switch
    {
        "performance" => CommonLoopPreference.Performance,
        "balanced" => CommonLoopPreference.Balanced,
        "quality" => CommonLoopPreference.Quality,
        _ => throw new InvalidDataException("Loop preference must be performance, balanced, or quality.")
    };

    /// <summary>
    /// 循环时长上限（秒）：档位兜底或 --loop-max-seconds。不按内嵌视频 2 GiB 收紧：分析时只有参考码率（现有成品里最高的那张）可估，
    /// 对一般场景高估一个数量级；成品大小由 bake 按这个场景自己的试编码外推、编码后按实际字节判（<see cref="EmbeddedVideoBudget"/>）。
    /// 默认值不写进 plan.settings（字段为 null），bake 按同一规则还原，分析与烘焙用的上限一致。
    /// </summary>
    internal static double LoopLengthMaximumOf(HybridAnalyzeRequest request) =>
        RetimeProfileJson.Resolve(request).LoopMaximumSeconds;

    /// <summary>
    /// 循环分析的唯一入口。analyze、布局降级重算与 bake 前刷新都走这里，三处结果一致。
    /// </summary>
    /// <param name="scene">每次求解取一份新的、已冻结时间属性的场景副本（求解会写候选与未解析项，不能共用）。</param>
    public static JsonObject AnalyzeLoopForProfile(Func<JsonObject> scene, ProjectSource source, string? assets, JsonObject runtime,
        int[] bakedLayerIds, HybridAnalyzeRequest request, JsonObject projection, JsonArray? videoGroups) =>
        AnalyzeLoopWithNotes(scene, source, assets, runtime, bakedLayerIds, request, projection, videoGroups).Loop;

    /// <summary>
    /// 同 <see cref="AnalyzeLoopForProfile"/>，另给出 loop.unresolved 各条的文案与点名图层（<see cref="UnresolvedNotes"/>，不进 plan）：
    /// analyze 把它带到写 plan；前缀缓存与 bake 前刷新只要 plan 形态的 loop。
    /// </summary>
    internal static (JsonObject Loop, UnresolvedNotes Notes) AnalyzeLoopWithNotes(Func<JsonObject> scene, ProjectSource source, string? assets,
        JsonObject runtime, int[] bakedLayerIds, HybridAnalyzeRequest request, JsonObject projection, JsonArray? videoGroups)
    {
        RetimeProfile profile = RetimeProfileJson.Resolve(request);
        (JsonObject Loop, UnresolvedNotes Notes) Solve()
        {
            JsonObject input = scene();
            // 缓存两段：plan 形态的 loop + unresolved 各条的文案与点名图层（UnresolvedNotes.Pack）。格式变了就换前缀，旧缓存不再命中。
            // 键只放 LoopAnalysis 真读的：视频组只读 id 与 layer_ids（组的位置、尺寸随布局变），投影与输出宽高根本不传进去。
            // 这样同一组图层换了布局或别的组变了，也能命中。
            JsonArray? groupLayers = videoGroups is null ? null : new JsonArray([.. videoGroups.OfType<JsonObject>().Select(group =>
                (JsonNode)new JsonObject { ["id"] = group["id"]?.DeepClone(), ["layer_ids"] = group["layer_ids"]?.DeepClone() })]);
            string key = "loop-v15-" + AnalysisCache.Key(input, runtime, bakedLayerIds, assets, groupLayers,
                request.FpsNumerator, request.FpsDenominator, profile, request.LoopPreference,
                request.FullLoopLayerIds, request.BudgetOnlyRetime, request.RetimeSlowComponents, request.SpeedUnmeasured);
            double ceiling = LoopLengthMaximumOf(request);
            LoopReport Analyze(JsonObject scene, IReadOnlyCollection<ulong>? steps) => LoopAnalysis.Analyze(
                scene, source, assets, runtime, bakedLayerIds,
                request.FpsNumerator, request.FpsDenominator, profile.CommonRetimePercent, LoopPreferenceOf(request.LoopPreference),
                ceiling, videoGroups, steps, request.FullLoopLayerIds,
                // 逐层"不能"按档位回退链能走到的最大预算证明（SearchSpace：没给 --retime-budget 时一直退到效率档）
                request.RetimeBudgetPercent is null && request.Preset is not null ? Math.Max(profile.CommonRetimePercent, RetimeProfile.MaximumBudgetPercent) : null,
                request.BudgetOnlyRetime, request.RetimeSlowComponents, request.SpeedUnmeasured);
            JsonObject packed = AnalysisCache.Get(request.AnalysisCacheDirectory, key, () =>
            {
                LoopReport loop = Analyze(input, null);
                // 档位上限（600 s）内凑不出公共循环：判"周期超上限"之前，按兼容档的上限（1200 s）、同一预算再求一次，有解才用
                // （视频变长，画面不变）。用户给了 --loop-max-seconds 时不动；plan 的 loop.maximum_seconds 记实际用的上限。
                double wider = RetimeProfile.PresetLoopMaximumSeconds(RetimeProfile.Compatibility);
                if (loop.Candidates.Count == 0 && profile.LoopMaximumSource == RetimeProfile.FromPreset && ceiling < wider &&
                    loop.NoCandidateReason?.Reason.Kind is CommonLoopNoCandidateKind.NoFrameOnFixedStepSatisfiesComponents or CommonLoopNoCandidateKind.FixedPeriodExceedsCeiling)
                {
                    (double narrow, ceiling) = (ceiling, wider);
                    if (Analyze(scene(), null) is { Candidates.Count: > 0 } relaxed) loop = relaxed;
                    else ceiling = narrow;
                }
                // 与别的组共用时钟的组，自身周期不整除 L 时给 L 加"是它的倍数"的约束重解一次；
                // 只在重解后候选与未解析项都不变差时采用，否则保持原解（这些组录 L 帧）。
                if (loop.GroupClockSteps.Count > 0 && Analyze(scene(), loop.GroupClockSteps) is { Candidates.Count: > 0 } stepped &&
                    stepped.Unresolved.Count == loop.Unresolved.Count)
                    loop = stepped;
                return UnresolvedNotes.Pack(loop);
            });
            // 每条未解析项记下能否被接缝淡化掩盖（与生成准入 Admission 同一判定 ResidualMasking）：循环完整不完整
            // （Routes.WholeLoopComplete）按它判，已证随机的精灵与平稳随机粒子不再让整层路线记成没解完。拆包前写，文案记录与条目逐字对得上。
            Func<string, JsonObject?> resources = ResidualMasking.ResourceReader(source, assets);
            foreach (JsonObject item in (packed["loop"]?["unresolved"] as JsonArray ?? []).OfType<JsonObject>())
                item["maskable"] = ResidualMasking.Maskable(item, input, resources);
            return UnresolvedNotes.Unpack(packed);
        }
        return Solve();
    }

    /// <summary>给候选表补上名次与计划选中项：候选表首项就是 bake 会用的那个。</summary>
    internal static void AnnotateLoopCandidates(JsonObject loop)
    {
        if (loop["candidates"] is not JsonArray items) return;
        LoopCandidateFallback.PrioritizeShortest(items);
        for (int index = 0; index < items.Count; ++index)
            if (items[index] is JsonObject candidate) candidate["rank"] = index + 1;
        loop["selected_candidate_index"] = items.Count == 0 ? null : JsonValue.Create(0);
    }

    /// <summary>Vulkan 设备类型到设备类别；只做溯源记录，不参与任何准入判断。</summary>
    public static string DeviceClassOf(VulkanDeviceType deviceType) => deviceType switch
    {
        VulkanDeviceType.IntegratedGpu => "integrated",
        VulkanDeviceType.DiscreteGpu => "discrete",
        _ => "other"
    };

    /// <summary>记录本次分析实际使用的设备；未指定或枚举不到时类别为 unknown。</summary>
    public static JsonObject DescribeAnalysisDevice(IReadOnlyList<VulkanDeviceInfo>? devices, string? requestedUuid)
    {
        VulkanDeviceInfo? device = requestedUuid is null || devices is null ? null
            : devices.FirstOrDefault(item => string.Equals(item.DeviceUuid, requestedUuid, StringComparison.OrdinalIgnoreCase));
        return new JsonObject
        {
            ["name"] = device?.Name,
            ["device_uuid"] = device?.DeviceUuid ?? requestedUuid,
            ["device_type"] = device is null ? null : (uint)device.DeviceType,
            ["device_class"] = device is null ? "unknown" : DeviceClassOf(device.DeviceType),
            ["identification"] = device is not null ? "vulkan_enumeration"
                : requestedUuid is null ? "no_device_selected" : "device_not_enumerated"
        };
    }

    // 枚举失败（无 Vulkan 加载器、驱动异常）按"未识别"处理，只影响溯源字段，不打断分析。
    private static IReadOnlyList<VulkanDeviceInfo>? EnumerateDevicesOrNull()
    {
        try { return VulkanDevices.Enumerate(); }
        catch (Exception) { return null; }
    }

    /// <summary>门面：交给 <see cref="AnalysisOrchestrator"/> 在搜索空间里编排单次分析；<paramref name="states"/> 给了才逐状态导出子 plan。
    /// 一次调用内各次子分析共用一个 <see cref="AnalysisMemo"/>，源是否被改过在最后核对一次。</summary>
    public async Task<JsonObject> AnalyzeAsync(HybridAnalyzeRequest request, IProgress<RenderProgress>? progress = null,
        CancellationToken cancellationToken = default, StateExport? states = null)
    {
        var memo = new AnalysisMemo();
        JsonObject result = await AnalysisOrchestrator.RunAsync(request, (candidate, token) => AnalyzeSingleAsync(candidate, memo, progress, token),
            cancellationToken, tools, states, progress: progress, memo: memo);
        await memo.VerifySourcesAsync(cancellationToken);
        return result;
    }

    /// <summary>
    /// 单次分析的编排：观测 → 实时判定 → 分配 → 构图 → 初判（<see cref="Verdict"/>）→ 循环与特效前缀 → 组 plan（<see cref="PlanWriter"/>）
    /// → 路线与布局准入（<see cref="Routes"/>）→ 更小分配取证 → 收尾裁定 → 落盘。各阶段只按这个顺序各跑一次。
    /// </summary>
    public Task<JsonObject> AnalyzeSingleAsync(HybridAnalyzeRequest request, IProgress<RenderProgress>? progress = null,
        CancellationToken cancellationToken = default) => AnalyzeSingleAsync(request, null, progress, cancellationToken);

    /// <param name="memo">一次 analyze 内共用的记忆（<see cref="AnalysisMemo"/>）；null 时每步照常各算各的（烘焙前刷新、单次调用）。</param>
    internal async Task<JsonObject> AnalyzeSingleAsync(HybridAnalyzeRequest request, AnalysisMemo? memo, IProgress<RenderProgress>? progress,
        CancellationToken cancellationToken)
    {
        AnalysisTiming.Lap timing = AnalysisTiming.StartSubAnalysis();
        if (request.SchemaVersion != 2 || (request.Width == 0) != (request.Height == 0) || !OutputResolution.IsKnownSource(request.ResolutionSource) ||
            request.FpsNumerator == 0 || request.FpsDenominator == 0 ||
            request.MaximumRetimePercent < 0 || request.MaximumRetimePercent > RetimeProfile.MaximumBudgetPercent ||
            request.Preset is string preset && !RetimeProfile.IsKnownPreset(preset) ||
            request.RetimeBudgetPercent is double budget && (!double.IsFinite(budget) || budget < 0 || budget > RetimeProfile.MaximumBudgetPercent) ||
            request.ViewMode is not "preserve" and not "fixed_view" ||
            request.VideoLayout is not "full_frame" and not "layered" || request.LiveOverlayPlacement is not "preserve" and not "foreground" ||
            request.LiveTextEffects is not "preserve" and not "simple" || request.AudioEffects is not "preserve" and not "omit" ||
            request.LoopPreference is not "performance" and not "balanced" and not "quality" ||
            request.VideoShell is not VideoDominance.RejectChoice and not VideoDominance.AllowChoice ||
            request.LoopLengthMaximumSeconds is double loopLengthMaximum && (!double.IsFinite(loopLengthMaximum) || loopLengthMaximum <= 0 ||
                loopLengthMaximum > CommonLoopSolver.MaximumLoopLengthSeconds))
            throw new InvalidDataException("Invalid version 2 scene analysis settings.");
        string output = Path.GetFullPath(request.OutputDirectory);
        ProjectSource.EnsureNoReparsePoints(output);
        if (Directory.Exists(output) || File.Exists(output)) throw new IOException("Analysis output must be new.");
        using var source = new ProjectSource(request.Source);
        if (source.Kind != "scene") throw new InvalidDataException("Hybrid scene planning requires a Scene project.");
        timing.Mark("A1_open_source");
        string sourceHash = memo is null ? await source.SourceHashAsync(cancellationToken) : await memo.SourceHashAsync(source, cancellationToken);
        timing.Mark("A2_source_hash");
        // 只随源变的 JSON：有记忆时整次 analyze 只解析一次，每次拿一份副本。
        JsonObject SourceJson(string name, Func<JsonObject> read) => memo is null ? read() : memo.Json(name + "|" + sourceHash, read);
        if (request.AnalysisCacheDirectory is string cacheDirectory)
            request = request with { AnalysisCacheDirectory = Path.Combine(cacheDirectory, sourceHash) };
        var scene = SourceJson("scene", () => AnalysisCache.Get(request.AnalysisCacheDirectory, "scene", () => source.ReadJson(source.SceneResource)));
        var project = SourceJson("project", () => AnalysisCache.Get(request.AnalysisCacheDirectory, "project",
            () => source.Contains("project.json") ? source.ReadJson("project.json") : new JsonObject()));
        timing.Mark("A3_read_scene");
        var properties = SceneGraph.SnapshotProperties(project, request.UserProperties);
        // 未指定宽高时取本机主显示器分辨率；之后的探测、投影、plan.settings 与 bake 全部用这里定下的尺寸。
        OutputResolution.Choice resolution = OutputResolution.Choose(scene, properties, request.Width, request.Height,
            request.ResolutionSource, display ?? OutputResolution.PrimaryDisplay);
        request = request with { Width = resolution.Width, Height = resolution.Height, ResolutionSource = resolution.Source };
        var graph = new SceneGraph(scene);
        Directory.CreateDirectory(output);
        timing.Mark("A4_resolution_graph");
        progress?.Report(new("analyzing", null, new Message("progress.observing_scene")));
        RuntimeObservation observation = await RuntimeObservation.ObserveAsync(request, source, sourceHash, scene, project, properties,
            graph, output, new NativeRuntimeObserver(tools), progress, cancellationToken, memo);
        timing.Mark("A5_runtime_observation");
        // feat/daytime-split：开关开着才识别状态选择器；识别失败只记原因，判定照旧。同名图层靠观测到的可见性写消歧。
        DaytimeSplit.Detection? daytime = request.DaytimeSplit ? DaytimeSplit.Detect(graph.Objects, observation.Dependencies, properties) : null;
        if (request.DaytimeState is not null && daytime?.IsRecognized != true)
            throw new InvalidDataException("A daytime state was requested but no daytime state selector was recognized.");
        DaytimeSplit.State? daytimeState = request.DaytimeState is null ? null
            : daytime!.StateNamed(request.DaytimeState) ?? throw new InvalidDataException("Unknown daytime state: " + request.DaytimeState);
        // 时段常量型状态没有选择器层：状态内那些绑定就是常数，判定与合成直接看冻结后的场景（观测已在同样冻结的副本上跑）。
        if (daytimeState is not null && daytime!.ControllerId is null) DaytimeSplit.ApplyState(scene, daytime, daytimeState);
        int? daytimeSelector = daytimeState is null ? null : daytime!.ControllerId;
        HashSet<int> daytimeControlled = daytimeState is null ? new HashSet<int>() : daytime!.ControlledLayerIds.ToHashSet();
        HashSet<int> daytimeVisible = daytimeState is null ? new HashSet<int>() : daytimeState.VisibleLayerIds.ToHashSet();
        // 选择器对受控层的可见性写不再把目标连坐成实时：这就是状态拆分要摘掉的那条链。
        bool DaytimeVisibilityWrite(JsonObject dependency) => daytimeSelector is int selector &&
            SceneGraph.Int(dependency["owner"]) == selector && dependency["property"]?.GetValue<string>() == "visible" &&
            SceneGraph.Int(dependency["target"]) is int target && daytimeControlled.Contains(target);
        // 完整匹配的视频选择器在所选状态里已冻结。只摘掉该 visible 脚本原有的视频读取，
        // 不能因选择器所在图层还承担实时后处理，就把受控视频重新连坐为实时。
        bool FrozenDaytimeVideoRead(JsonObject dependency) => daytimeSelector is int selector && daytime!.ControlsVideoPlayback &&
            SceneGraph.Int(dependency["owner"]) == selector && dependency["binding"]?.GetValue<string>() == "visible" &&
            dependency["operation"]?.GetValue<string>() == "read" && dependency["property"]?.GetValue<string>() == "videoTexture" &&
            SceneGraph.Int(dependency["target"]) is int target && daytimeControlled.Contains(target);
        var scriptFaults = observation.ScriptFaults(request.RuntimeTraceFile is not null);
        bool parallax = SceneGraph.Resolve(scene["general"]?["cameraparallax"], properties)?.ToJsonString() == "true";
        var projection = HybridVideoProjection.Describe(scene, properties, request.Width, request.Height, observation.Trace["runtime_projection"] as JsonObject);
        // 读帧缓冲层没和之前的内容同在第一组时留实时重判；每轮只增不减，最多读帧缓冲层个数轮。计时按段累加，重判各轮都记进同一段。
        var retainedReaders = new HashSet<int>(request.LiveFramebufferReaderIds ?? []);
        Liveness liveness; Allocation allocation; Composer composer;
        do
        {
            int[] readers = [.. retainedReaders.Order()];
            Liveness AnalyzeLiveness() => Liveness.Analyze(request, source, graph, observation, scriptFaults.Errors, parallax, daytimeSelector,
                FrozenDaytimeVideoRead, DaytimeVisibilityWrite, readers.ToHashSet());
            // 实时判定只取决于源、观测与到这里为止读过的请求字段（见 LivenessKey），退回、档位、布局、留实时集合各轮都相同；
            // 构图阶段要求重判的读帧缓冲层并进键（没有时键与原来相同）。
            liveness = memo is null ? AnalyzeLiveness() : memo.Liveness(LivenessKey(request, sourceHash) +
                (readers.Length > 0 ? "|framebuffer-readers:" + string.Join(",", readers) : ""), graph, AnalyzeLiveness);
            timing.Mark("A6_liveness_projection");
            allocation = Allocation.Plan(graph, observation, liveness, request, properties, parallax, projection, FrozenDaytimeVideoRead, DaytimeVisibilityWrite);
            timing.Mark("A7_allocation");
            composer = new Composer(request, source, scene, properties, graph, observation, liveness, allocation, parallax,
                daytimeControlled, daytimeVisible);
        } while (composer.UnsettledFramebufferReaders.Length > 0 && retainedReaders.Add(composer.UnsettledFramebufferReaders[0]));
        // A later parallax/occlusion group can stay live in its original place. Compare that
        // complete suffix before concluding that the user needs multiple transparent videos.
        // 初判（原 R 段）：拒因在内存里按 Blocker 持有，写 plan 时渲染一次。
        Verdict verdict = Verdict.Initial(request, source, scene, project, properties, graph, observation, liveness, composer, projection, scriptFaults);
        timing.Mark("A8_compose_verdict");
        JsonObject LoopScene()
        {
            var copy = scene.DeepClone().AsObject();
            PlanTransforms.FreezeTemporalProperties(copy, properties);
            DaytimeSplit.ApplyState(copy, daytime, daytimeState, forAnalysis: true);
            return copy;
        }
        int[] BakedLayerIds(JsonArray groups) =>
            groups.OfType<JsonObject>().SelectMany(group => group["layer_ids"]!.AsArray().Select(node => node!.GetValue<int>())).ToArray();
        var (loop, loopNotes) = AnalyzeLoopWithNotes(LoopScene, source, request.Assets, observation.Trace, BakedLayerIds(composer.Groups),
            request, projection, composer.Groups);
        AnnotateLoopCandidates(loop);
        timing.Mark("A9_loop_analysis");
        // 三处回退都可能要前缀缓存，同一个终端捕获点只问一次渲染器。
        var captureProbes = new PrefixCaptureProbes(tools, request, source, scene, properties, output, memo);
        async Task<JsonArray> PrefixCachesAsync()
        {
            // 只看整层初判拒因：路线与布局准入追加的 blockers 不影响前缀回退（与改写前读同一份局部数组的结果相同）。
            // HDR 闭合拒因不挡：它是按整层初始分配求的，前缀采纳后按前缀捕获对象重求（Verdict.ApplyPrefixRadianceClosure）。
            if (verdict.PrefixSafetyBlocked) return new JsonArray();
            var proposed = EffectPrefixPlanner.Propose(scene, source, request.Assets, observation.Trace, properties, request, projection,
                composer.MayBeVisible);
            var accepted = new JsonArray();
            // 提案按层分组、层内由长到短。每层只取第一个捕获点可用的前缀：最长的那个被拒时退一级，
            // 整层不因此退回实时（摆动改频进来以后，更长的前缀更容易把终端落在共用缓冲上）。
            var settled = new HashSet<int>();
            foreach (JsonObject cache in proposed.OfType<JsonObject>())
            {
                int ownerId = cache["owner_layer_id"]!.GetValue<int>();
                if (settled.Contains(ownerId)) continue;
                try { EffectPrefixBakeService.ValidateSource(source, scene, cache); }
                catch (InvalidDataException) { continue; }
                // 捕获点落在共用缓冲上的前缀录到的是整幅场景，这个候选不生成。
                if (await captureProbes.TargetAsync(cache, cancellationToken) is { } probe &&
                    probe["status"]?.GetValue<string>() != EffectPrefixCaptureTarget.LayerTargetStatus) continue;
                // 完整区间复核（与烘焙同一份捕获副本、同一判据）：短观测看不到的晚到依赖推翻这条前缀时不采用，退一级再试，
                // 分析结论与烘焙一致，不再判"能"而烘焙 candidate_rejected_late_dependency。
                if (await captureProbes.CompleteCaptureAsync(cache, observation.Trace, projection, cancellationToken) is { } complete &&
                    complete["status"]?.GetValue<string>() != PrefixCaptureProbes.CompleteCapturePassedStatus) continue;
                accepted.Add(cache.DeepClone());
                settled.Add(ownerId);
            }
            return accepted;
        }
        JsonArray effectPrefixCaches = Routes.WholeLoopComplete(loop) ? new JsonArray() : await PrefixCachesAsync();
        timing.Mark("A10_effect_prefix");
        // 只记录这次分析实际用的是哪台设备；要不要烘是用户的事，不在这里裁决。
        JsonObject analysisDevice = DescribeAnalysisDevice(EnumerateDevicesOrNull(), request.DeviceUuid);
        timing.Mark("A11_enumerate_devices");
        JsonObject report = PlanWriter.Compose(request, source, sourceHash, output, resolution, analysisDevice, project, properties, parallax,
            projection, graph, observation, liveness, allocation, composer, verdict, loop, effectPrefixCaches, daytime);
        bool effectPrefixRoute = effectPrefixCaches.Count > 0;
        // W 段（C2.2d1）：路线与布局准入交给 Routes / LayoutAdmission（整层 → 特效前缀 → 全幅准入与尾组降级 → 仍被挡再试前缀）。
        // 尾组降级按新分组重求循环，用的是与整层同一个 LoopScene。
        (report, effectPrefixRoute) = await Routes.SettleAsync(report, effectPrefixRoute, request.VideoLayout, composer.Groups.Count,
            observation.Dependencies, PrefixCachesAsync, demoted =>
            {
                // 降级后的 plan 一定采用这份重算的 loop（LayoutAdmission.Evaluate），文案随之换成它的。
                (JsonObject demotedLoop, loopNotes) = AnalyzeLoopWithNotes(LoopScene, source, request.Assets, observation.Trace,
                    BakedLayerIds(demoted["video_groups"]!.AsArray()),
                    request, demoted["projection"] as JsonObject ?? new JsonObject(), demoted["video_groups"] as JsonArray);
                AnnotateLoopCandidates(demotedLoop);
                return demotedLoop;
            });
        // 前缀路线不管是初判就走的、还是路线准入里改走的，blockers 都被清空过：这里按前缀实际捕获的对象重求 HDR 闭合，
        // 不成立时拒因写回。路线到这里已定稿，后面的更小分配取证只看整层路线，不会再改道。
        if (effectPrefixRoute)
            Verdict.ApplyPrefixRadianceClosure(report, scene, properties, observation.Trace, source, request.Assets, project);
        // 探测过的前缀捕获点全部留档（被拒的原因就在这份记录里，不再并进 loop.unresolved：那里只放时间机制）。
        if (captureProbes.Recorded.Count > 0)
            report["effect_prefix_capture_probes"] = new JsonArray(captureProbes.Recorded.Select(probe => (JsonNode)probe.DeepClone()).ToArray());
        // README 的承诺：解析周期不完整时，把未解决机制与粒子所在的完整作者子树保留实时，再重查一次周期与构图。
        // 这条路以前只在 bake 阶段跑，analyze 既没走也没记录，用户拿到的就是一个没有任何理由的 unavailable。
        timing.Mark("A12_compose_routes");
        JsonObject residualScene = SourceJson("source-scene", () => source.ReadJson(source.SceneResource));
        Func<string, JsonObject?> residualResources = ResidualMasking.ResourceReader(source, request.Assets);
        timing.Mark("A13_reread_scene");
        await RecordLoopAllocationFallbackAsync(report, scene, request, output, residualScene, residualResources, memo, progress, cancellationToken);
        timing.Mark("A14_loop_allocation_fallback");
        // 收尾裁定（原 X 段）：残差布局闸门 → 求解器空候选 → 可追溯不变量 → 视频外壳 → 硬解预检 → 公共查询冲突 → suitability。
        Verdict.Conclude(report, request, source, graph, observation, projection, effectPrefixRoute, residualScene, residualResources);
        await PlanWriter.WriteAsync(report, request, output, loopNotes, cancellationToken);
        timing.Mark("A15_conclude_write");
        // 有记忆时改在整次 analyze 结束时核对一次（AnalysisMemo.VerifySourcesAsync）。
        if (memo is null)
        {
            if (sourceHash != await source.SourceHashAsync(cancellationToken)) throw new IOException("Source changed during analysis.");
            timing.Mark("A2_source_hash");
        }
        return report;
    }

    /// <summary>
    /// 实时判定（<see cref="Liveness.Analyze"/>）的记忆键。它的输入在分配之前就全部定下，都只随下面这些变：
    /// 源（sourceHash；scene、project 只随源变）、属性（UserProperties）、分辨率（已解析的 Width/Height/ResolutionSource，本机显示器同一个 planner 不变）、
    /// 观测（ObservationKey 的各项，Interaction 决定 gpuTiming，DeviceUuid、RuntimeTraceFile，音频效果取舍 AudioEffects，时段 DaytimeSplit/DaytimeState），
    /// 以及 Liveness 自己读的 SingleShotLive、ViewMode、Assets。
    /// 置空的这几项在 Liveness 之前一处都没读到（分配、构图、循环与收尾才用），退回、档位、布局、留实时集合各轮之间变的正是它们；
    /// 其余字段原样进键：以后在这之前多读了哪个字段，键只会更细，不会错用别的请求的结果。
    /// </summary>
    internal static string LivenessKey(HybridAnalyzeRequest request, string sourceHash) => "liveness|" + AnalysisCache.Key(sourceHash, request with {
        OutputDirectory = "", AnalysisCacheDirectory = null, RetainLiveRootIds = null, RetainLiveReasons = null, ExcludedLayerIds = null,
        Preset = null, LoopPreference = "", VideoLayout = "", LiveOverlayPlacement = "", BudgetOnlyRetime = false,
        RetimeSlowComponents = false, SpeedUnmeasured = false });

    /// <summary>
    /// 整层路线在"除 HDR 闭合外零 blocker 却求不出循环"时，真的试一次更小的烘焙分配，并把走了什么、结果如何写进 plan。
    /// HDR 闭合是按当前分配求的：更小分配把未解机制所在的子树留实时，重查时 Verdict.Initial 对新分配重求，所以 HDR 拒因不挡这一步；
    /// 重查的 HDR 结论记在 replanned_hdr_radiance_closure_status，重查仍不闭合就不算找到。
    /// 这里只取证，不替换用户没有要求的分配：真要改分配由 bake 的同名回退或显式 --retain-live 执行。
    /// </summary>
    private async Task RecordLoopAllocationFallbackAsync(JsonObject report, JsonObject scene, HybridAnalyzeRequest request,
        string output, JsonObject sourceScene, Func<string, JsonObject?> readResource, AnalysisMemo? memo,
        IProgress<RenderProgress>? progress, CancellationToken cancellationToken)
    {
        // 重查请求自己带着 retain_live_root_ids，绝不递归第二层。
        if ((request.RetainLiveRootIds ?? []).Length != 0 || report["route"]?.GetValue<string>() != "whole_layer" ||
            report["whole_layer"]?["status"]?.GetValue<string>() != "unavailable" ||
            report["blockers"] is not JsonArray initialBlockers ||
            !PlanBlockers.Codes(report).All(Verdict.IsCaptureGap)) return;
        // 只剩 HDR 拒因而循环本身完整时，没有要留实时的未解机制：不试，也不往完整的循环里补"没试"的说明。
        if (initialBlockers.Count > 0 && report["loop"] is JsonObject wholeLoop && Routes.WholeLoopComplete(wholeLoop)) return;
        JsonObject evidence = HybridLoopAllocation.Explain(report, scene);
        report["loop_allocation_fallback"] = evidence;
        // 取证只记在 loop_allocation_fallback 字段里，不再往 loop.unresolved 塞说明条目（那里只放时间机制）。
        if (evidence["status"]?.GetValue<string>() != "proposed") return;
        string analysisOutput = Path.Combine(output, "loop-allocation-analysis");
        evidence["analysis_plan_path"] = Path.Combine(analysisOutput, "plan.json");
        progress?.Report(new("retaining_nonlooping_layers", null, new Message("progress.retaining_nonlooping_layers")));
        int[] retained = [];
        string retainedText = string.Join(",", evidence["retain_live_root_ids"]!.AsArray().Select(node => node!.GetValue<int>()));
        try
        {
            // 重查仍证不出循环而又点名了新的所有者时并进留实时再查（拆分放开后第一轮看不到的单元、静止证明点名的层），
            // 前几轮挪到 -1、-2…，最终结果仍在 analysis_plan_path（编排层按它采纳）。
            // 这里的缓存目录已按源哈希拼过一层（AnalyzeSingleAsync 开头）：递归时交回基础目录，子分析再拼一次正好落回同一个目录，
            // 父分析已有的观测、循环分析与捕获探针缓存都能命中（原先落到 <sha>/<sha>，每次重查都冷算一遍、多起一次观测渲染）。
            int archived = 0;
            (JsonObject replanned, bool resolved, string basis, JsonObject? residual) = await HybridLoopAllocation.ReplanUntilSettledAsync(report, scene, evidence,
                async (ids, reasons) =>
                {
                    (retained, retainedText) = (ids, string.Join(",", ids));
                    using (AnalysisTiming.Measure("h_loop_allocation_replan"))
                        return await AnalyzeSingleAsync(request with { OutputDirectory = analysisOutput, RuntimeTraceFile = null,
                            AnalysisCacheDirectory = Path.GetDirectoryName(request.AnalysisCacheDirectory),
                            RetainLiveRootIds = ids, RetainLiveReasons = reasons }, memo, progress, cancellationToken);
                },
                replanned => HybridLoopAllocation.ReplannedResolution(replanned, sourceScene, readResource),
                () =>
                {
                    string moved = analysisOutput + "-" + ++archived;
                    Directory.Move(analysisOutput, moved);
                    return Path.Combine(moved, "plan.json");
                });
            var replannedLoop = replanned["loop"]!.AsObject();
            // 重查按更小分配重求了 HDR 闭合（走前缀路线时是前缀捕获对象那次）。前缀路线的 blockers 只剩 HDR 拒因时
            // ReplannedResolution 仍按路线判"找到"，这里按重查的 HDR 结论改判，免得编排层采纳一份还被 HDR 挡着的分配。
            bool replannedRadianceOpen = PlanBlockers.Codes(replanned).Any(Verdict.IsRadianceCode);
            if (resolved && replannedRadianceOpen) (resolved, basis) = (false, "hdr_radiance_open");
            // 透视同理：剩下的内容循环成立、只差透视捕获时记 perspective_capture_open，不能说成证不出循环。
            bool replannedPerspective = PlanBlockers.Codes(replanned).Contains(BlockerCode.PerspectiveNeedsScreenspace);
            if (resolved && replannedPerspective) (resolved, basis) = (false, "perspective_capture_open");
            evidence["status"] = resolved ? "candidate_found" : "still_unavailable";
            evidence["resolution_basis"] = basis;
            // 只差采集能力时，按"假设能采集"对这份更小分配做的省电预判（编排层据此判不省电或标给排期）。
            if (basis is "perspective_capture_open" or "hdr_radiance_open" && NoBenefit.CaptureOpenConditions(replanned) is string[] ifCaptured)
                evidence[NoBenefit.ReplannedConditionsField] = new JsonArray([.. ifCaptured.Select(c => (JsonNode)JsonValue.Create(c))]);
            if (residual is not null)
                evidence["replanned_residual_masking"] = new JsonObject
                {
                    ["status"] = residual["status"]?.DeepClone(),
                    ["residual_layer_ids"] = new JsonArray([.. ResidualMasking.ResidualOwners(residual).Select(id => (JsonNode)JsonValue.Create(id))]),
                    ["blocking_layer_ids"] = new JsonArray([.. (residual["blocking_components"] as JsonArray ?? []).OfType<JsonObject>()
                        .Select(item => item["owner_layer_id"]?.DeepClone())]),
                    ["max_warmup_seconds"] = residual["max_warmup_seconds"]?.DeepClone()
                };
            evidence["replanned_route"] = replanned["route"]!.DeepClone();
            evidence["replanned_status"] = replanned["status"]!.DeepClone();
            evidence["replanned_whole_layer_status"] = replanned["whole_layer"]?["status"]?.DeepClone();
            evidence["replanned_loop_status"] = replannedLoop["status"]!.DeepClone();
            evidence["replanned_candidate_count"] = replannedLoop["candidates"]!.AsArray().Count;
            evidence["replanned_video_group_count"] = (replanned["video_groups"] as JsonArray)?.Count;
            evidence["replanned_effect_prefix_cache_count"] = (replanned["effect_prefix_caches"] as JsonArray)?.Count;
            evidence["replanned_blockers"] = replanned["blockers"]!.DeepClone();
            // 重查 plan 的 HDR 闭合结论（最终路线那次求值）。hdr 未开启时判据不运行（not_applicable），不写这个字段，plan 逐字不变。
            if (replanned["hdr_radiance_closure"]?["status"] is JsonValue radianceStatus &&
                radianceStatus.TryGetValue(out string? radiance) && radiance != "not_applicable")
                evidence["replanned_hdr_radiance_closure_status"] = radiance;
            evidence["replanned_unresolved"] = replannedLoop["unresolved"]!.DeepClone();
            // 重查只被全幅布局挡住时，把冲突给出的保留做法按完整 --retain-live 列表记下来，结论行才能给出照做就能用的参数。
            if (!resolved && HybridLoopAllocation.ReplannedRetainLiveSuggestion(replanned, retained) is JsonObject suggestion)
                evidence["replanned_retain_live_suggestion"] = suggestion;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            evidence["status"] = "failed";
            evidence["error_type"] = error.GetType().Name;
            evidence["error"] = error.Message;
        }
    }

}
