using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Baker.Core;

/// <remarks>Width/Height 同为 0 表示未指定：分析时按 <see cref="OutputResolution"/> 取场景画布，结果与来源写回 plan.settings。</remarks>
public sealed record HybridAnalyzeRequest(int SchemaVersion, string Source, string Assets, string OutputDirectory,
    uint Width = 0, uint Height = 0, uint FpsNumerator = 120, uint FpsDenominator = 1,
    JsonObject? UserProperties = null, string ViewMode = "preserve", double MaximumRetimePercent = 2,
    bool AllowLocalSeamRepair = false, string? RuntimeTraceFile = null, string? DeviceUuid = null,
    int[]? RetainLiveRootIds = null, string VideoLayout = "full_frame",
    string LiveOverlayPlacement = "preserve", string LiveTextEffects = "preserve", string AudioEffects = "preserve",
    int[]? ExcludedLayerIds = null, string LoopPreference = "balanced", string VideoShell = "reject", string? ResolutionSource = null,
    // 摆动改频（默认关）与它的循环长度上限（秒，null = 600）。默认值不写进 plan 的 settings，开关关闭时 plan 与旧版逐字节相同。
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool SwayRetime = false,
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
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Postprocessing = null);

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
    /// 摆动改频参数：开关关闭时为 null（循环分析完全不走改频）。输出比例 = 输出像素 / 可见场景单位，只用于把摆动振幅
    /// 换到输出像素（冻结项漂移的次序与报告），与 bake 的捕获像素换算同一口径。
    /// 循环长度上限与通用求解器共用 <see cref="LoopLengthMaximumOf"/>（含内嵌视频 2 GiB 收紧），收紧记录挂在 VideoLimit 上。
    /// analyze、布局降级重算与 bake 前刷新都走这里，三处结果一致。
    /// </summary>
    /// <param name="ceilingOverride">质量档对比另一个上限时用的秒数（见 <see cref="RetimeProfile.QualityComparisonSeconds"/>）；其余情况为 null。</param>
    internal static SwayRetimeOptions? SwayRetimeOptionsOf(HybridAnalyzeRequest request, JsonObject projection, JsonArray? videoGroups,
        double? ceilingOverride = null)
    {
        if (!request.SwayRetime) return null;
        double visibleWidth = projection["visible_width"] is JsonValue width && width.TryGetValue(out double w) && w > 0 ? w : request.Width;
        double visibleHeight = projection["visible_height"] is JsonValue height && height.TryGetValue(out double h) && h > 0 ? h : request.Height;
        // 速度门限按最终输出画布（OutputResolution 定下的 request 宽高，与振幅换算同一个画布）的短边换算。
        return new(LoopLengthMaximumOf(request, videoGroups, ceilingOverride), request.Width / visibleWidth, request.Height / visibleHeight,
            EmbeddedVideoLimitOf(request, videoGroups, ceilingOverride), RetimeProfileJson.Resolve(request),
            SwayRecurrenceSolver.SpeedLimitScale(request.Width, request.Height));
    }

    /// <summary>
    /// 内嵌视频 2 GiB 对循环时长上限的收紧记录（EmbeddedVideoBudget）：按 --loop-max-seconds（未给时 600）、输出宽高与帧率，
    /// 有透明组时按 alpha 左右并排的双宽算。输出尺寸未知（0）时为 null，不收紧。
    /// </summary>
    internal static EmbeddedVideoLoopLimit? EmbeddedVideoLimitOf(HybridAnalyzeRequest request, JsonArray? videoGroups,
        double? ceilingOverride = null)
    {
        double requested = ceilingOverride ?? RetimeProfileJson.Resolve(request).LoopMaximumSeconds;
        bool packedAlpha = videoGroups?.OfType<JsonObject>().Any(group =>
            group["transparent"] is JsonValue transparent && transparent.TryGetValue(out bool value) && value) == true;
        return EmbeddedVideoBudget.LoopLengthLimit(requested, request.Width, request.Height, packedAlpha,
            request.FpsNumerator, request.FpsDenominator);
    }

    /// <summary>
    /// 循环时长上限（秒）：唯一的实际上限 = min(档位兜底或 --loop-max-seconds, 内嵌视频 2 GiB 在参考码率下装得下的秒数)。
    /// 与摆动改频开关无关：着色器、动画、视频与摆动分量全部在这一个上限下求解，摆动改频的 Lmax 也取这个值。
    /// 默认值不写进 plan.settings（字段为 null），bake 按同一规则还原，分析与烘焙用的上限一致。
    /// 特效前缀路线在规划时还不知道缓存尺寸，<paramref name="videoGroups"/> 传 null，按不透明整幅输出估算。
    /// </summary>
    internal static double LoopLengthMaximumOf(HybridAnalyzeRequest request, JsonArray? videoGroups, double? ceilingOverride = null) =>
        EmbeddedVideoLimitOf(request, videoGroups, ceilingOverride)?.EffectiveSeconds
        ?? ceilingOverride ?? RetimeProfileJson.Resolve(request).LoopMaximumSeconds;

    /// <summary>
    /// 循环分析的唯一入口：质量档在档位上限与 <see cref="RetimeProfile.QualityComparisonSeconds"/> 下各求一次，
    /// 取可见摆动改动更小的那次（规则见 <see cref="RetimeProfile.ChooseQualityCeiling"/>），并把两次读数写进选中结果的
    /// <c>quality_ceiling_used</c>；其余档位只求一次，行为与从前逐字节相同。
    /// analyze、布局降级重算与 bake 前刷新都走这里，三处结果一致——否则 bake 会按另一个上限重算出别的循环。
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
        (JsonObject Loop, UnresolvedNotes Notes) Solve(double? ceilingOverride)
        {
            JsonObject input = scene();
            // 缓存两段：plan 形态的 loop + unresolved 各条的文案与点名图层（UnresolvedNotes.Pack）。格式变了就换前缀，旧缓存不再命中。
            string key = "loop-v3-" + AnalysisCache.Key(input, runtime, bakedLayerIds, assets, projection, videoGroups,
                request.Width, request.Height, request.FpsNumerator, request.FpsDenominator, profile, request.SwayRetime, request.LoopPreference, ceilingOverride);
            return UnresolvedNotes.Unpack(AnalysisCache.Get(request.AnalysisCacheDirectory, key, () => UnresolvedNotes.Pack(LoopAnalysis.Analyze(
                input, source, assets, runtime, bakedLayerIds,
                request.FpsNumerator, request.FpsDenominator, profile.CommonRetimePercent, LoopPreferenceOf(request.LoopPreference),
                SwayRetimeOptionsOf(request, projection, videoGroups, ceilingOverride),
                LoopLengthMaximumOf(request, videoGroups, ceilingOverride), EmbeddedVideoLimitOf(request, videoGroups, ceilingOverride)))));
        }
        var atPreset = Solve(null);
        // 判定用的是这一案的生效上限（含内嵌视频 2 GiB 收紧），不是档位名义上限：4K 不透明组的生效上限只有 558 s，
        // 两个上限解出来是同一次求解，再跑一遍纯属白跑，还会写出"600 s 那侧循环更短所以胜出"的误导记录。
        double presetCeiling = LoopLengthMaximumOf(request, videoGroups);
        double comparisonCeiling = LoopLengthMaximumOf(request, videoGroups, RetimeProfile.QualityComparisonSeconds);
        if (!profile.ComparesQualityCeilings(presetCeiling) || Math.Abs(presetCeiling - comparisonCeiling) <= 1e-9) return atPreset;
        var atComparison = Solve(RetimeProfile.QualityComparisonSeconds);
        RetimeProfile.QualityCeilingReading presetReading = ReadCeiling(atPreset.Loop), comparisonReading = ReadCeiling(atComparison.Loop);
        RetimeProfile.QualityCeilingChoice choice = RetimeProfile.ChooseQualityCeiling(presetReading, comparisonReading);
        var chosen = choice.UsePresetCeiling ? atPreset : atComparison;
        chosen.Loop["quality_ceiling_used"] = new JsonObject
        {
            ["seconds"] = choice.UsePresetCeiling ? presetReading.CeilingSeconds : comparisonReading.CeilingSeconds,
            ["source"] = choice.UsePresetCeiling ? "preset" : "quality_comparison",
            ["reason"] = choice.Reason,
            ["tried"] = new JsonArray(CeilingJson(presetReading, "preset"), CeilingJson(comparisonReading, "quality_comparison"))
        };
        return chosen;
    }

    /// <summary>一次求解的对比读数：生效上限、选中候选的可见摆动改动与帧数（没有候选或没有摆动解时改动为 null）。</summary>
    private static RetimeProfile.QualityCeilingReading ReadCeiling(JsonObject loop)
    {
        JsonObject? candidate = (loop["candidates"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault();
        double? visible = candidate?["sway_retime"] is JsonObject sway && sway["max_change_visible_percent"] is JsonValue change &&
            change.TryGetValue(out double percent) ? percent : null;
        ulong? frames = candidate?["frames"] is JsonValue value && value.TryGetValue(out ulong count) ? count : null;
        double ceiling = loop["maximum_seconds"] is JsonValue seconds && seconds.TryGetValue(out double limit) ? limit : 0;
        return new(ceiling, visible, frames);
    }

    private static JsonNode CeilingJson(RetimeProfile.QualityCeilingReading reading, string source) => new JsonObject
    {
        ["source"] = source, ["ceiling_seconds"] = reading.CeilingSeconds,
        ["max_change_visible_percent"] = reading.VisibleChangePercent, ["frames"] = reading.Frames
    };

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

    /// <summary>门面：交给 <see cref="AnalysisOrchestrator"/> 在搜索空间里编排单次分析；<paramref name="states"/> 给了才逐状态导出子 plan。</summary>
    public async Task<JsonObject> AnalyzeAsync(HybridAnalyzeRequest request, IProgress<RenderProgress>? progress = null,
        CancellationToken cancellationToken = default, StateExport? states = null) =>
        await AnalysisOrchestrator.RunAsync(request, (candidate, token) => AnalyzeSingleAsync(candidate, progress, token), cancellationToken, tools, states);

    /// <summary>
    /// 单次分析的编排：观测 → 实时判定 → 分配 → 构图 → 初判（<see cref="Verdict"/>）→ 循环与特效前缀 → 组 plan（<see cref="PlanWriter"/>）
    /// → 路线与布局准入（<see cref="Routes"/>）→ 更小分配取证 → 收尾裁定 → 落盘。各阶段只按这个顺序各跑一次。
    /// </summary>
    public async Task<JsonObject> AnalyzeSingleAsync(HybridAnalyzeRequest request, IProgress<RenderProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
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
        string sourceHash = await source.SourceHashAsync(cancellationToken);
        if (request.AnalysisCacheDirectory is string cacheDirectory)
            request = request with { AnalysisCacheDirectory = Path.Combine(cacheDirectory, sourceHash) };
        var scene = AnalysisCache.Get(request.AnalysisCacheDirectory, "scene", () => source.ReadJson(source.SceneResource));
        var project = AnalysisCache.Get(request.AnalysisCacheDirectory, "project", () => source.Contains("project.json") ? source.ReadJson("project.json") : new JsonObject());
        var properties = SceneGraph.SnapshotProperties(project, request.UserProperties);
        // 未指定宽高时按场景画布铺满本机屏幕取尺寸；之后的探测、投影、plan.settings 与 bake 全部用这里定下的尺寸。
        OutputResolution.Choice resolution = OutputResolution.Choose(scene, properties, request.Width, request.Height,
            request.ResolutionSource, display ?? OutputResolution.PrimaryDisplay);
        request = request with { Width = resolution.Width, Height = resolution.Height, ResolutionSource = resolution.Source };
        var graph = new SceneGraph(scene);
        Directory.CreateDirectory(output);
        progress?.Report(new("analyzing", null, "Observing real script inputs, object accesses and scene hierarchy."));
        RuntimeObservation observation = await RuntimeObservation.ObserveAsync(request, source, sourceHash, scene, project, properties,
            graph, output, new NativeRuntimeObserver(tools), progress, cancellationToken);
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
        var liveness = Liveness.Analyze(request, source, graph, observation, scriptFaults.Errors, parallax, daytimeSelector,
            FrozenDaytimeVideoRead, DaytimeVisibilityWrite);
        var projection = HybridVideoProjection.Describe(scene, properties, request.Width, request.Height, observation.Trace["runtime_projection"] as JsonObject);
        var allocation = Allocation.Plan(graph, observation, liveness, request, properties, parallax, FrozenDaytimeVideoRead, DaytimeVisibilityWrite);
        var composer = new Composer(request, source, scene, properties, graph, observation, liveness, allocation, parallax,
            daytimeControlled, daytimeVisible);
        // A later parallax/occlusion group can stay live in its original place. Compare that
        // complete suffix before concluding that the user needs multiple transparent videos.
        // 初判（原 R 段）：拒因在内存里按 Blocker 持有，写 plan 时渲染一次。
        Verdict verdict = Verdict.Initial(request, source, scene, project, properties, graph, observation, liveness, composer, projection, scriptFaults);
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
        // 三处回退都可能要前缀缓存，同一个终端捕获点只问一次渲染器。
        var captureProbes = new PrefixCaptureProbes(tools, request, source, scene, properties, output);
        async Task<JsonArray> PrefixCachesAsync()
        {
            // 只看整层初判拒因：路线与布局准入追加的 blockers 不影响前缀回退（与改写前读同一份局部数组的结果相同）。
            // HDR 闭合拒因不挡：它是按整层初始分配求的，前缀采纳后按前缀捕获对象重求（Verdict.ApplyPrefixRadianceClosure）。
            if (verdict.PrefixSafetyBlocked) return new JsonArray();
            var proposed = EffectPrefixPlanner.Propose(scene, source, request.Assets, observation.Trace, properties, request, projection);
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
                accepted.Add(cache.DeepClone());
                settled.Add(ownerId);
            }
            return accepted;
        }
        JsonArray effectPrefixCaches = Routes.WholeLoopComplete(loop) ? new JsonArray() : await PrefixCachesAsync();
        // 只记录这次分析实际用的是哪台设备；要不要烘是用户的事，不在这里裁决。
        JsonObject analysisDevice = DescribeAnalysisDevice(EnumerateDevicesOrNull(), request.DeviceUuid);
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
        // 探测过的前缀捕获点全部留档。被拒的原因只在整层循环本来就有未解机制时并进 loop.unresolved：这时前缀是
        // 整层循环的回退，拒绝原因正好说明回退为什么没走成。条目不带 owner_layer_id，免得分配回退把它当成要保留实时的
        // 未解层；原本没有未解项的循环也不凭空添一条，免得改变整层裁定。
        if (captureProbes.Recorded.Count > 0)
        {
            report["effect_prefix_capture_probes"] = new JsonArray(captureProbes.Recorded.Select(probe => (JsonNode)probe.DeepClone()).ToArray());
            if (report["loop"]?["unresolved"] is JsonArray { Count: > 0 })
                foreach (JsonObject probe in captureProbes.Recorded.Where(probe =>
                    probe["status"]?.GetValue<string>() != EffectPrefixCaptureTarget.LayerTargetStatus))
                    Verdict.AddLoopUnresolved(report, "effect_prefix_capture_target", probe["reason"]!.GetValue<string>(), loopNotes,
                        probe["reason_localized"] as JsonObject);
        }
        // README 的承诺：解析周期不完整时，把未解决机制与粒子所在的完整作者子树保留实时，再重查一次周期与构图。
        // 这条路以前只在 bake 阶段跑，analyze 既没走也没记录，用户拿到的就是一个没有任何理由的 unavailable。
        JsonObject residualScene = source.ReadJson(source.SceneResource);
        Func<string, JsonObject?> residualResources = ResidualMasking.ResourceReader(source, request.Assets);
        await RecordLoopAllocationFallbackAsync(report, scene, request, output, residualScene, residualResources, progress, cancellationToken);
        // 收尾裁定（原 X 段）：残差布局闸门 → 求解器空候选 → 可追溯不变量 → 视频外壳 → 硬解预检 → 公共查询冲突 → suitability。
        Verdict.Conclude(report, request, source, graph, observation, projection, effectPrefixRoute, residualScene, residualResources);
        await PlanWriter.WriteAsync(report, request, output, loopNotes, cancellationToken);
        if (sourceHash != await source.SourceHashAsync(cancellationToken)) throw new IOException("Source changed during analysis.");
        return report;
    }

    /// <summary>
    /// 整层路线在"除 HDR 闭合外零 blocker 却求不出循环"时，真的试一次更小的烘焙分配，并把走了什么、结果如何写进 plan。
    /// HDR 闭合是按当前分配求的：更小分配把未解机制所在的子树留实时，重查时 Verdict.Initial 对新分配重求，所以 HDR 拒因不挡这一步；
    /// 重查的 HDR 结论记在 replanned_hdr_radiance_closure_status，重查仍不闭合就不算找到。
    /// 这里只取证，不替换用户没有要求的分配：真要改分配由 bake 的同名回退或显式 --retain-live 执行。
    /// </summary>
    private async Task RecordLoopAllocationFallbackAsync(JsonObject report, JsonObject scene, HybridAnalyzeRequest request,
        string output, JsonObject sourceScene, Func<string, JsonObject?> readResource,
        IProgress<RenderProgress>? progress, CancellationToken cancellationToken)
    {
        // 重查请求自己带着 retain_live_root_ids，绝不递归第二层。
        if ((request.RetainLiveRootIds ?? []).Length != 0 || report["route"]?.GetValue<string>() != "whole_layer" ||
            report["whole_layer"]?["status"]?.GetValue<string>() != "unavailable" ||
            report["blockers"] is not JsonArray initialBlockers || !PlanBlockers.Codes(report).All(Verdict.IsRadianceCode)) return;
        // 只剩 HDR 拒因而循环本身完整时，没有要留实时的未解机制：不试，也不往完整的循环里补"没试"的说明。
        if (initialBlockers.Count > 0 && report["loop"] is JsonObject wholeLoop && Routes.WholeLoopComplete(wholeLoop)) return;
        JsonObject evidence = HybridLoopAllocation.Explain(report, scene);
        report["loop_allocation_fallback"] = evidence;
        if (evidence["status"]?.GetValue<string>() != "proposed")
        {
            Verdict.AddLoopUnresolved(report, "loop_allocation_fallback",
                "A smaller bake allocation was not attempted: " + (evidence["reason"]?.GetValue<string>() ?? "no reason recorded."));
            return;
        }
        int[] retained = evidence["retain_live_root_ids"]!.AsArray().Select(node => node!.GetValue<int>()).ToArray();
        string retainedText = string.Join(",", retained);
        string analysisOutput = Path.Combine(output, "loop-allocation-analysis");
        evidence["analysis_plan_path"] = Path.Combine(analysisOutput, "plan.json");
        progress?.Report(new("retaining_nonlooping_layers", null,
            "Keeping unresolved effects and particles live, then checking one smaller bake allocation."));
        try
        {
            JsonObject replanned = await AnalyzeSingleAsync(request with {
                OutputDirectory = analysisOutput, RuntimeTraceFile = null, RetainLiveRootIds = retained }, progress, cancellationToken);
            var replannedLoop = replanned["loop"]!.AsObject();
            // 留下的未解析项全部可由残差掩盖时也算找到：bake 会走残差掩盖路线（例如留实时水面之后剩下的平稳随机雨）。
            (bool resolved, string basis, JsonObject? residual) = HybridLoopAllocation.ReplannedResolution(replanned, sourceScene, readResource);
            // 重查按更小分配重求了 HDR 闭合（走前缀路线时是前缀捕获对象那次）。前缀路线的 blockers 只剩 HDR 拒因时
            // ReplannedResolution 仍按路线判"找到"，这里按重查的 HDR 结论改判，免得编排层采纳一份还被 HDR 挡着的分配。
            bool replannedRadianceOpen = PlanBlockers.Codes(replanned).Any(Verdict.IsRadianceCode);
            if (resolved && replannedRadianceOpen) (resolved, basis) = (false, "hdr_radiance_open");
            evidence["status"] = resolved ? "candidate_found" : "still_unavailable";
            evidence["resolution_basis"] = basis;
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
            Verdict.AddLoopUnresolved(report, "loop_allocation_fallback", resolved
                ? $"No loop covers every baked layer, but a smaller bake allocation does: keeping author roots {retainedText} live leaves content that resolves. Re-run analyze with --retain-live {retainedText} to plan that allocation."
                : replannedRadianceOpen
                    ? $"No loop covers every baked layer, and the smaller bake allocation that keeps author roots {retainedText} live is still blocked by the HDR radiance closure of the content it captures."
                    : $"No loop covers every baked layer, and the smaller bake allocation that keeps author roots {retainedText} live establishes none either.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            evidence["status"] = "failed";
            evidence["error_type"] = error.GetType().Name;
            evidence["error"] = error.Message;
            Verdict.AddLoopUnresolved(report, "loop_allocation_fallback",
                $"No loop covers every baked layer, and the smaller bake allocation keeping author roots {retainedText} live could not be analyzed: {error.Message}");
        }
    }

}
