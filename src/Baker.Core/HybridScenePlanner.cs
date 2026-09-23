using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

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
    [property: JsonIgnore] bool LayoutExplicit = false);

/// <summary>Plans video replacement from source hierarchy and observed input dependencies.</summary>
/// <param name="display">未指定宽高时用来铺满的屏幕尺寸；省略时读本机主显示器物理分辨率，测试可注入固定值。</param>
public sealed class HybridScenePlanner(NativeTools tools, Func<(uint Width, uint Height)?>? display = null)
{
    // 只用于标注可疑的广告/二维码/水印图层，从不自动剔除任何东西。
    private static readonly Regex OverlayVocabulary = new(
        @"\b(ads?|advert\w*|qr\w*|donate|donation|watermark|logo|signature|credit)\b|广告|二维码|捐赠|打赏|水印|署名|关注",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    internal static readonly Blocker MissingScriptFaultEvidenceBlocker = new(BlockerCode.MissingScriptFaultEvidence);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

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
        return new(LoopLengthMaximumOf(request, videoGroups, ceilingOverride), request.Width / visibleWidth, request.Height / visibleHeight,
            EmbeddedVideoLimitOf(request, videoGroups, ceilingOverride), RetimeProfile.Resolve(request));
    }

    /// <summary>
    /// 内嵌视频 2 GiB 对循环时长上限的收紧记录（EmbeddedVideoBudget）：按 --loop-max-seconds（未给时 600）、输出宽高与帧率，
    /// 有透明组时按 alpha 左右并排的双宽算。输出尺寸未知（0）时为 null，不收紧。
    /// </summary>
    internal static EmbeddedVideoLoopLimit? EmbeddedVideoLimitOf(HybridAnalyzeRequest request, JsonArray? videoGroups,
        double? ceilingOverride = null)
    {
        double requested = ceilingOverride ?? RetimeProfile.Resolve(request).LoopMaximumSeconds;
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
        ?? ceilingOverride ?? RetimeProfile.Resolve(request).LoopMaximumSeconds;

    /// <summary>
    /// 循环分析的唯一入口：质量档在档位上限与 <see cref="RetimeProfile.QualityComparisonSeconds"/> 下各求一次，
    /// 取可见摆动改动更小的那次（规则见 <see cref="RetimeProfile.ChooseQualityCeiling"/>），并把两次读数写进选中结果的
    /// <c>quality_ceiling_used</c>；其余档位只求一次，行为与从前逐字节相同。
    /// analyze、布局降级重算与 bake 前刷新都走这里，三处结果一致——否则 bake 会按另一个上限重算出别的循环。
    /// </summary>
    /// <param name="scene">每次求解取一份新的、已冻结时间属性的场景副本（求解会写候选与未解析项，不能共用）。</param>
    public static JsonObject AnalyzeLoopForProfile(Func<JsonObject> scene, ProjectSource source, string? assets, JsonObject runtime,
        int[] bakedLayerIds, HybridAnalyzeRequest request, JsonObject projection, JsonArray? videoGroups)
    {
        RetimeProfile profile = RetimeProfile.Resolve(request);
        JsonObject Solve(double? ceilingOverride)
        {
            JsonObject input = scene();
            // 缓存内容带 unresolved 的文案键（detail_localized），格式变了就换前缀，旧缓存不再命中。
            string key = "loop-v2-" + AnalysisCache.Key(input, runtime, bakedLayerIds, assets, projection, videoGroups,
                request.Width, request.Height, request.FpsNumerator, request.FpsDenominator, profile, request.SwayRetime, request.LoopPreference, ceilingOverride);
            return AnalysisCache.Get(request.AnalysisCacheDirectory, key, () => HybridLoopService.Analyze(input, source, assets, runtime, bakedLayerIds,
                request.FpsNumerator, request.FpsDenominator, profile.CommonRetimePercent, LoopPreferenceOf(request.LoopPreference),
                SwayRetimeOptionsOf(request, projection, videoGroups, ceilingOverride),
                LoopLengthMaximumOf(request, videoGroups, ceilingOverride), EmbeddedVideoLimitOf(request, videoGroups, ceilingOverride)));
        }
        JsonObject atPreset = Solve(null);
        // 判定用的是这一案的生效上限（含内嵌视频 2 GiB 收紧），不是档位名义上限：4K 不透明组的生效上限只有 558 s，
        // 两个上限解出来是同一次求解，再跑一遍纯属白跑，还会写出"600 s 那侧循环更短所以胜出"的误导记录。
        double presetCeiling = LoopLengthMaximumOf(request, videoGroups);
        double comparisonCeiling = LoopLengthMaximumOf(request, videoGroups, RetimeProfile.QualityComparisonSeconds);
        if (!profile.ComparesQualityCeilings(presetCeiling) || Math.Abs(presetCeiling - comparisonCeiling) <= 1e-9) return atPreset;
        JsonObject atComparison = Solve(RetimeProfile.QualityComparisonSeconds);
        RetimeProfile.QualityCeilingReading presetReading = ReadCeiling(atPreset), comparisonReading = ReadCeiling(atComparison);
        RetimeProfile.QualityCeilingChoice choice = RetimeProfile.ChooseQualityCeiling(presetReading, comparisonReading);
        JsonObject chosen = choice.UsePresetCeiling ? atPreset : atComparison;
        chosen["quality_ceiling_used"] = new JsonObject
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

    public async Task<JsonObject> AnalyzeAsync(HybridAnalyzeRequest request, IProgress<RenderProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        await PresetCascade.AnalyzeAsync(request, (candidate, token) => AnalyzeSingleAsync(candidate, progress, token), cancellationToken, tools);

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
                loopLengthMaximum > SwayRetimeOptions.MaximumLoopLengthSeconds))
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
        var properties = SnapshotProperties(project, request.UserProperties);
        // 未指定宽高时按场景画布铺满本机屏幕取尺寸；之后的探测、投影、plan.settings 与 bake 全部用这里定下的尺寸。
        OutputResolution.Choice resolution = OutputResolution.Choose(scene, properties, request.Width, request.Height,
            request.ResolutionSource, display ?? OutputResolution.PrimaryDisplay);
        request = request with { Width = resolution.Width, Height = resolution.Height, ResolutionSource = resolution.Source };
        var graph = new SceneGraph(scene);
        var objects = graph.Objects;
        var sourceOrder = graph.SourceOrder;
        var rootOf = graph.RootOf;
        var roots = graph.Roots;
        Directory.CreateDirectory(output);
        progress?.Report(new("analyzing", null, "Observing real script inputs, object accesses and scene hierarchy."));
        RuntimeObservation observation = await RuntimeObservation.ObserveAsync(request, source, sourceHash, scene, project, properties,
            graph, output, new NativeRuntimeObserver(tools), progress, cancellationToken);
        JsonObject trace = observation.Trace, audioEffectChoice = observation.AudioEffectChoice;
        JsonArray dependencies = observation.Dependencies, runtimeLayers = observation.RuntimeLayers;
        // feat/daytime-split：开关开着才识别状态选择器；识别失败只记原因，判定照旧。同名图层靠观测到的可见性写消歧。
        DaytimeSplit.Detection? daytime = request.DaytimeSplit ? DaytimeSplit.Detect(objects, dependencies, properties) : null;
        if (request.DaytimeState is not null && daytime?.IsRecognized != true)
            throw new InvalidDataException("A daytime state was requested but no daytime state selector was recognized.");
        DaytimeSplit.State? daytimeState = request.DaytimeState is null ? null
            : daytime!.StateNamed(request.DaytimeState) ?? throw new InvalidDataException("Unknown daytime state: " + request.DaytimeState);
        int? daytimeSelector = daytimeState is null ? null : daytime!.ControllerId;
        HashSet<int> daytimeControlled = daytimeState is null ? new HashSet<int>() : daytime!.ControlledLayerIds.ToHashSet();
        HashSet<int> daytimeVisible = daytimeState is null ? new HashSet<int>() : daytimeState.VisibleLayerIds.ToHashSet();
        // 选择器对受控层的可见性写不再把目标连坐成实时：这就是状态拆分要摘掉的那条链。
        bool DaytimeVisibilityWrite(JsonObject dependency) => daytimeSelector is int selector &&
            Int(dependency["owner"]) == selector && dependency["property"]?.GetValue<string>() == "visible" &&
            Int(dependency["target"]) is int target && daytimeControlled.Contains(target);
        // 完整匹配的视频选择器在所选状态里已冻结。只摘掉该 visible 脚本原有的视频读取，
        // 不能因选择器所在图层还承担实时后处理，就把受控视频重新连坐为实时。
        bool FrozenDaytimeVideoRead(JsonObject dependency) => daytimeSelector is int selector && daytime!.ControlsVideoPlayback &&
            Int(dependency["owner"]) == selector && dependency["binding"]?.GetValue<string>() == "visible" &&
            dependency["operation"]?.GetValue<string>() == "read" && dependency["property"]?.GetValue<string>() == "videoTexture" &&
            Int(dependency["target"]) is int target && daytimeControlled.Contains(target);
        var (scriptErrorEvidenceAvailable, scriptErrorCount, sourceScriptErrors) = observation.ScriptFaults(request.RuntimeTraceFile is not null);
        var live = new HashSet<int>();
        bool parallax = Resolve(scene["general"]?["cameraparallax"], properties)?.ToJsonString() == "true";
        var reasons = objects.Keys.ToDictionary(id => id, _ => new HashSet<string>());
        void Live(int id, string reason)
        {
            if (!objects.ContainsKey(id)) return;
            live.Add(id); reasons[id].Add(reason);
        }
        foreach (var error in sourceScriptErrors.OfType<JsonObject>())
        {
            if (Int(error["owner_layer_id"]) is not int owner || !objects.ContainsKey(owner))
                throw new InvalidDataException("A source script fault lacks a known authored owner; its allocation cannot be inferred safely.");
            Live(owner, "source_script_error");
        }
        // 一次性动画轨的所属层必须实时绘制：循环视频会让只播一次的动画每个周期重播，
        // 与原作播完即定格的画面不一致。判据与 HybridLoopService 读的是同一份轨道证据。
        foreach (int owner in SingleShotAllocation.LiveOwners(trace)) Live(owner, SingleShotAllocation.LiveReason);
        foreach (var dependency in dependencies.OfType<JsonObject>())
        {
            int owner = dependency["owner"]!.GetValue<int>();
            string operation = dependency["operation"]!.GetValue<string>();
            if (operation == "input" && !(owner == daytimeSelector && dependency["property"]?.GetValue<string>() == "wall_clock"))
                Live(owner, "observed_" + dependency["property"]!.GetValue<string>());
            if (operation == "write" && dependency["initialization"]?.GetValue<bool>() != true &&
                dependency["property"]?.GetValue<string>() == "parallaxDepth" && request.ViewMode == "preserve")
                Live(dependency["target"]!.GetValue<int>(), "runtime_parallax_depth_change");
            if (operation == "time" && dependency["property"]?.GetValue<string>() == "frametime" &&
                objects.TryGetValue(owner, out var item) && item.ContainsKey("text")) Live(owner, "live_frame_status");
        }
        // These materials consume the already-composited scene. Their input must remain the
        // current video/live composition; their upstream drawing need not remain expensive.
        foreach (var layer in runtimeLayers.OfType<JsonObject>())
            if (layer["materials"] is JsonArray materials && materials.OfType<JsonObject>().Any(material =>
                material["textures"] is JsonArray textures && textures.Any(texture => texture?.GetValue<string>() is
                    "_rt_default" or "_rt_FullFrameBuffer")))
                Live(layer["owner"]!.GetValue<int>(), "reads_current_framebuffer");
        foreach (var layer in runtimeLayers.OfType<JsonObject>())
            if (layer["materials"] is JsonArray materials)
            {
                if (materials.OfType<JsonObject>().Any(m => m["uses_audio_spectrum"] is null))
                    throw new InvalidDataException("Runtime observation predates active shader input metadata; collect a fresh trace.");
                if (materials.OfType<JsonObject>().Any(m => m["uses_audio_spectrum"]?.GetValue<bool>() == true))
                    Live(layer["owner"]!.GetValue<int>(), "active_shader_audio_spectrum");
                if (materials.OfType<JsonObject>().Any(m => m["active_uniforms"] is JsonArray uniforms &&
                    uniforms.Any(u => u?.GetValue<string>() is "g_PointerPosition" or "g_PointerPositionLast")))
                    Live(layer["owner"]!.GetValue<int>(), "active_shader_pointer_input");
                if (parallax && request.ViewMode == "preserve" && materials.OfType<JsonObject>().Any(m =>
                    m["active_uniforms"] is JsonArray uniforms && uniforms.Any(u => u?.GetValue<string>() == "g_ParallaxPosition")))
                    Live(layer["owner"]!.GetValue<int>(), "active_shader_parallax_input");
            }
        foreach (var (id, obj) in objects)
        {
            if (obj.ContainsKey("sound")) Live(id, "soundtrack");
            if (obj.ContainsKey("camera")) Live(id, "scene_camera");
            foreach (var binding in SceneAnalyzer.Walk(obj).OfType<JsonObject>())
            {
                if (binding["script"] is not JsonValue value || !value.TryGetValue<string>(out string? text)) continue;
                string code = CapabilityScanText(text);
                if (Regex.IsMatch(code, @"\bnew\s+Date\b|\bDate\s*\.\s*now\b|\btimeOfDay\b") && id != daytimeSelector) Live(id, "wall_clock_api");
                if (Regex.IsMatch(code, @"\bregisterAudioBuffers\s*\(")) Live(id, "audio_api");
                if (Regex.IsMatch(code, @"\binput\s*[.\[]|\bfunction\s+cursor\w*\s*\(")) Live(id, "pointer_api");
                if (Regex.IsMatch(code, @"\bfunction\s+media\w*\s*\(")) Live(id, "media_api");
            }
            // Particle cursor linkage is a native input path rather than a SceneScript call.
            if (SceneAnalyzer.Walk(obj).OfType<JsonObject>().Any(n => n["name"]?.GetValue<string>() == "link_mouse" ||
                n["function"]?.GetValue<string>() == "link_mouse")) Live(id, "particle_pointer_input");
            if (obj["particle"] is JsonValue particle && particle.TryGetValue<string>(out string? resource))
            {
                var definition = SceneAnalyzer.ReadResourceJson(source, request.Assets, resource);
                if (definition.ToJsonString().Contains("link_mouse", StringComparison.OrdinalIgnoreCase) ||
                    definition["controlpoint"] is JsonArray points && points.OfType<JsonObject>()
                        .Any(point => (Int(point["flags"]) & 1) == 1)) Live(id, "particle_pointer_input");
                // 粒子的音频响应与着色器音频频谱同级：外部实时输入没有源可证明的周期，这样的层不进烘焙。
                if (ParticleInputAnalysis.HasAudioInput(definition, obj)) Live(id, "particle_audio_input");
            }
        }
        // Runtime writes by a live controller make their targets live. Reads of a live mutable
        // target make the consuming animation live too. Initialization-only transforms stay snapshots.
        bool changed;
        do
        {
            int before = live.Count;
            foreach (var dependency in dependencies.OfType<JsonObject>())
            {
                if (FrozenDaytimeVideoRead(dependency)) continue;
                int owner = dependency["owner"]!.GetValue<int>(), target = dependency["target"]!.GetValue<int>();
                string operation = dependency["operation"]!.GetValue<string>();
                bool initialization = dependency["initialization"]?.GetValue<bool>() == true;
                if (operation == "write" && live.Contains(owner) && !DaytimeVisibilityWrite(dependency)) Live(target, "written_by_live_controller");
                if (operation == "read" && !initialization && live.Contains(target)) Live(owner, "reads_live_object");
                if (operation == "read" && live.Contains(owner) && dependency["property"]?.GetValue<string>() is "boneTransform" or "animation" or "effect" or "videoTexture" or "textureAnimation" or "layerComposite")
                    Live(target, "live_runtime_resource_dependency");
            }
            changed = live.Count != before;
        } while (changed);
        var scripts = objects.ToDictionary(pair => pair.Key, pair => SceneAnalyzer.Walk(pair.Value).OfType<JsonObject>()
            .Where(n => n["script"] is JsonValue).Select(n => CapabilityScanText(n["script"]!.GetValue<string>())).ToArray());
        int[] authorRoots = roots;
        bool PotentialVisibility(int id) => (Resolve(objects[id]["visible"], properties)?.ToJsonString() != "false" ||
            objects[id]["visible"] is JsonObject binding && (binding.ContainsKey("script") || binding.ContainsKey("animation") || binding.ContainsKey("animations"))) &&
            (Int(objects[id]["parent"]) is not int parent || !objects.ContainsKey(parent) || PotentialVisibility(parent));
        bool PotentialDrawing(int id) => PotentialVisibility(id) &&
            (objects[id].ContainsKey("image") || objects[id].ContainsKey("text") || objects[id].ContainsKey("particle") || objects[id].ContainsKey("model") ||
                runtimeLayers.OfType<JsonObject>().Any(layer => Int(layer["owner"]) == id && layer["has_mesh"]?.GetValue<bool>() == true));
        // Preserve existing independent overlay components that can move across later author roots.
        // Splitting their fixed text companions into a video would change that explicit foreground choice.
        var independentOverlays = authorRoots.Where(root => sourceOrder.Any(id => rootOf[id] == root && live.Contains(id)) &&
            sourceOrder.Any(id => rootOf[id] == root && PotentialDrawing(id)) &&
            authorRoots.Skip(Array.IndexOf(authorRoots, root) + 1).Any(later =>
                !sourceOrder.Any(id => rootOf[id] == later && live.Contains(id)) &&
                sourceOrder.Any(id => rootOf[id] == later && PotentialDrawing(id))) &&
            sourceOrder.Where(id => rootOf[id] == root).All(id => objects[id]["disablepropagation"]?.ToJsonString() != "true" &&
                (!PotentialDrawing(id) || objects[id].ContainsKey("text") || objects[id].ContainsKey("particle")) &&
                !reasons[id].Contains("reads_current_framebuffer")) &&
            !dependencies.OfType<JsonObject>().Any(d => d["operation"]?.GetValue<string>() is "read" or "write" &&
                ((Int(d["owner"]) is int owner && rootOf.GetValueOrDefault(owner, -1) == root) !=
                 (Int(d["target"]) is int target && rootOf.GetValueOrDefault(target, -1) == root)))).ToHashSet();
        // A structural node can share its fixed parent transform between independently allocated
        // children. Unknown state, drawing, scripts and observed writes keep its whole subtree intact.
        var structuralFields = new HashSet<string>(["id", "name", "parent", "origin", "angles", "scale", "visible",
            "alpha", "color", "solid", "disablepropagation", "parallaxDepth"], StringComparer.Ordinal);
        bool unresolvedObjectAccess = dependencies.OfType<JsonObject>().Any(d =>
            d["operation"]?.GetValue<string>() is "lookup" or "read" or "write" &&
            (Int(d["target"]) is not int target || !objects.ContainsKey(target)));
        bool StaticStructure(int id) => objects[id].All(pair => structuralFields.Contains(pair.Key)) &&
            scripts[id].Length == 0 && !live.Contains(id) && !unresolvedObjectAccess && !independentOverlays.Contains(id) &&
            HybridVideoProjection.SupportsStaticParent(objects[id], properties) &&
            (!parallax || request.ViewMode != "preserve" || objects[id]["parallaxDepth"] is null ||
                HybridVideoProjection.Vector(Resolve(objects[id]["parallaxDepth"], properties), (0, 0)) == (0d, 0d)) &&
            !SceneAnalyzer.Walk(objects[id]).OfType<JsonObject>().Any(node =>
                node.ContainsKey("animation") || node.ContainsKey("animations")) &&
            runtimeLayers.OfType<JsonObject>().Any(layer => Int(layer["owner"]) == id && layer["has_mesh"]?.GetValue<bool>() == false) &&
            !runtimeLayers.OfType<JsonObject>().Any(layer => Int(layer["owner"]) == id && layer["has_mesh"]?.GetValue<bool>() != false) &&
            !dependencies.OfType<JsonObject>().Any(d => Int(d["target"]) == id && d["operation"]?.GetValue<string>() == "write");
        var allocationOf = new Dictionary<int, int>();
        void Assign(int id, int unit)
        {
            allocationOf[id] = unit;
            bool split = id == unit && StaticStructure(id);
            foreach (int child in sourceOrder.Where(child => Int(objects[child]["parent"]) == id))
                Assign(child, split ? child : unit);
        }
        foreach (int root in authorRoots) Assign(root, root);
        // Depth-first source sibling order is the author's draw order, even when declarations interleave.
        var allocationOrder = new List<int>();
        void Order(int id)
        {
            if (allocationOf[id] == id) allocationOrder.Add(id);
            foreach (int child in sourceOrder.Where(child => Int(objects[child]["parent"]) == id)) Order(child);
        }
        foreach (int root in authorRoots) Order(root);
        roots = allocationOrder.ToArray();
        var projection = HybridVideoProjection.Describe(scene, properties, request.Width, request.Height, trace["runtime_projection"] as JsonObject);
        var rootDepths = new Dictionary<int, (double X, double Y)>();
        foreach (int root in roots)
        {
            var draws = runtimeLayers.OfType<JsonObject>().Where(n => Int(n["owner"]) is int owner &&
                allocationOf.GetValueOrDefault(owner, -1) == root && n["has_mesh"]?.GetValue<bool>() == true && n["visible"]?.GetValue<bool>() != false).ToArray();
            var depths = draws.Where(n => n["effective_parallax_depth"] is JsonArray)
                .Select(n => HybridVideoProjection.Vector(n["effective_parallax_depth"], (0, 0))).Distinct().ToArray();
            rootDepths[root] = depths.FirstOrDefault();
            if (parallax && request.ViewMode == "preserve" && depths.Length > 1) Live(root, "mixed_parallax_depth_hierarchy");
            if (parallax && request.ViewMode == "preserve" && draws.Any(n => n["effective_parallax_depth"] is not JsonArray)) Live(root, "unresolved_runtime_parallax_depth");
            if (parallax && request.ViewMode == "preserve" && sourceOrder.Any(id => allocationOf[id] == root &&
                objects[id]["parallaxDepth"] is JsonObject binding && binding.ContainsKey("script"))) Live(root, "animated_parallax_depth");
        }
        foreach (int root in request.RetainLiveRootIds ?? [])
        {
            if (!rootOf.TryGetValue(root, out int actualRoot) || actualRoot != root) throw new InvalidDataException("A requested live root is not a source root.");
            foreach (int id in sourceOrder.Where(id => rootOf[id] == root)) Live(id, "retained_by_cost_trial");
        }
        // Hidden script hosts can initialize fonts or other live layers without drawing a pixel.
        // Preserve those controllers instead of turning them into empty video groups.
        foreach (int root in roots)
        {
            JsonNode? visibility = Resolve(objects[root]["visible"], properties);
            if (visibility is JsonObject binding) visibility = binding["value"];
            if (visibility?.ToJsonString() == "false" && sourceOrder.Any(id => allocationOf[id] == root && scripts[id].Length > 0))
                Live(root, "hidden_script_controller");
        }
        var liveRoots = live.Select(id => allocationOf[id]).ToHashSet();
        // A protected subtree can make additional controllers live; close their cross-unit accesses too.
        do
        {
            int before = liveRoots.Count;
            foreach (var dependency in dependencies.OfType<JsonObject>())
            {
                if (FrozenDaytimeVideoRead(dependency)) continue;
                int owner = dependency["owner"]!.GetValue<int>(), target = dependency["target"]!.GetValue<int>();
                bool ownerLive = allocationOf.TryGetValue(owner, out int ownerUnit) && liveRoots.Contains(ownerUnit);
                bool targetLive = allocationOf.TryGetValue(target, out int targetUnit) && liveRoots.Contains(targetUnit);
                string? operation = dependency["operation"]?.GetValue<string>();
                if (operation == "write" && ownerLive && objects.ContainsKey(target) && !DaytimeVisibilityWrite(dependency))
                { Live(target, "written_by_live_controller"); liveRoots.Add(allocationOf[target]); }
                if (operation == "read" && dependency["initialization"]?.GetValue<bool>() != true && targetLive && objects.ContainsKey(owner))
                { Live(owner, "reads_live_object"); liveRoots.Add(allocationOf[owner]); }
                if (operation == "read" && ownerLive && objects.ContainsKey(target) &&
                    dependency["property"]?.GetValue<string>() is "boneTransform" or "animation" or "effect" or "videoTexture" or "textureAnimation" or "layerComposite")
                { Live(target, "live_runtime_resource_dependency"); liveRoots.Add(allocationOf[target]); }
            }
            changed = liveRoots.Count != before;
        } while (changed);
        // ponytail: leave dynamic object lookup/shared controllers alone. This small conservative
        // check only removes fixed-disabled, self-contained branches; it is not a JS optimizer.
        bool dynamicLookup = scripts.Values.SelectMany(s => s).Any(code => Regex.IsMatch(code,
            @"\b(thisScene|getLayer|getParent|setParent|globalThis|eval|Function|Reflect|Proxy|import)\b|\.\s*(parent|children)\b"));
        bool Within(int id, int ancestor)
        {
            while (objects.TryGetValue(id, out var item))
            {
                if (id == ancestor) return true;
                if (Int(item["parent"]) is not int parent || !objects.ContainsKey(parent)) return false;
                id = parent;
            }
            return false;
        }
        bool SafeHiddenSubtree(int id, HashSet<int> subtree) => !dynamicLookup &&
            !(request.RetainLiveRootIds ?? []).Contains(rootOf[id]) &&
            Resolve(objects[id]["visible"], properties)?.ToJsonString() == "false" &&
            !(objects[id]["visible"] is JsonObject visibility && (visibility.ContainsKey("script") || visibility.ContainsKey("animation") || visibility.ContainsKey("animations"))) &&
            scripts[id].All(code => !Regex.IsMatch(code, @"\bthisLayer\s*\.\s*visible\b")) &&
            subtree.All(layer => objects[layer]["disablepropagation"]?.ToJsonString() != "true" &&
                !objects[layer].ContainsKey("sound") && !objects[layer].ContainsKey("camera") &&
                scripts[layer].All(code => !Regex.IsMatch(code, @"\b(shared|thisObject|this)\b|\.\s*(constructor|__proto__|prototype)\b|\bengine\b(?!\s*\.\s*(canvasSize|frametime|runtime|registerAudioBuffers|AUDIO_RESOLUTION_\d+|userProperties)\b)|\bthisLayer\b(?!\s*\.\s*(text|origin|scale|angles|alpha|color|visible)\b)"))) &&
            !dependencies.OfType<JsonObject>().Any(dependency =>
            {
                string? operation = dependency["operation"]?.GetValue<string>();
                int? owner = Int(dependency["owner"]), target = Int(dependency["target"]);
                return operation == "write" && target == id && dependency["property"]?.GetValue<string>() == "visible" ||
                    operation is "lookup" or "read" or "write" &&
                    ((owner is int ownerId && subtree.Contains(ownerId)) != (target is int targetId && subtree.Contains(targetId)));
            });
        var omittedSubtreeRoots = new List<int>();
        var omittedIds = new HashSet<int>();
        foreach (int id in sourceOrder)
        {
            if (omittedIds.Contains(id)) continue;
            var subtree = sourceOrder.Where(layer => Within(layer, id)).ToHashSet();
            if (!SafeHiddenSubtree(id, subtree)) continue;
            omittedSubtreeRoots.Add(id);
            omittedIds.UnionWith(subtree);
        }
        // 用户显式剔除的图层及其子树，等同于在原作里把它关掉：不进视频、不留实时、不进静态纹理。
        // 复用既有的 omitted 机制，原作文件不变。
        int[] excludedRoots = (request.ExcludedLayerIds ?? []).Distinct().OrderBy(id => id).ToArray();
        if (excludedRoots.Any(id => !objects.ContainsKey(id)))
            throw new InvalidDataException("An excluded layer id is not present in this scene.");
        var excludedIds = sourceOrder.Where(id => excludedRoots.Any(root => Within(id, root))).ToHashSet();
        omittedIds.UnionWith(excludedIds);
        liveRoots.ExceptWith(omittedIds.Where(id => allocationOf[id] == id));
        var fixedProperties = omittedSubtreeRoots.Select(id => (objects[id]["visible"] as JsonObject)?["user"])
            .Select(user => user is JsonValue value ? value.GetValue<string>() : user?["name"]?.GetValue<string>())
            .OfType<string>().Distinct().ToArray();
        // Each protected subtree stays intact; fixed structural ancestors are restored during export.
        var liveIds = sourceOrder.Where(id => liveRoots.Contains(allocationOf[id]) && !omittedIds.Contains(id)).ToHashSet();
        foreach (int id in liveIds.Where(id => !live.Contains(id))) reasons[id].Add("shares_live_hierarchy");
        var observed = runtimeLayers.OfType<JsonObject>().Where(n => Int(n["id"]) is int id && objects.ContainsKey(id))
            .GroupBy(n => n["id"]!.GetValue<int>()).ToDictionary(g => g.Key, g => g.First());
        // 状态拆分：不属于当前状态的受控层按"保留但隐藏"处理（不进视频、不实时、不删——选择器脚本在成品里仍要
        // getLayer 找到它们）；属于当前状态的受控层视为可见（探测在真实时刻跑，观测里它们多半是隐藏的）。
        var daytimeHidden = daytimeControlled.Except(daytimeVisible).ToHashSet();
        bool LocallyVisible(int id)
        {
            if (omittedIds.Contains(id) || daytimeHidden.Contains(id)) return false;
            if (daytimeVisible.Contains(id)) return true;
            if (observed.TryGetValue(id, out var state) && state["visible"]?.GetValue<bool>() == false) return false;
            return Resolve(objects[id]["visible"], properties)?.ToJsonString() != "false";
        }
        bool Visible(int id) => LocallyVisible(id) &&
            (Int(objects[id]["parent"]) is not int parent || !objects.ContainsKey(parent) || Visible(parent));
        bool Draws(int id) => !omittedIds.Contains(id) && (objects[id].ContainsKey("image") || objects[id].ContainsKey("text") ||
            objects[id].ContainsKey("particle") || objects[id].ContainsKey("model") ||
            observed.GetValueOrDefault(id)?["has_mesh"]?.GetValue<bool>() == true);
        bool MayBeVisible(int id) => !omittedIds.Contains(id) && !daytimeHidden.Contains(id) && (LocallyVisible(id) ||
            objects[id]["visible"] is JsonObject visibility && (visibility.ContainsKey("script") || visibility.ContainsKey("animation") || visibility.ContainsKey("animations")) ||
            dependencies.OfType<JsonObject>().Any(d => Int(d["target"]) == id && d["operation"]?.GetValue<string>() == "write" &&
                d["property"]?.GetValue<string>() == "visible")) &&
            (Int(objects[id]["parent"]) is not int parent || !objects.ContainsKey(parent) || MayBeVisible(parent));
        // The stock fullscreen passthrough copies the framebuffer onto itself. Keep its scripts,
        // but it neither separates drawing groups nor prevents capturing the scene clear color.
        bool IdentityFramebuffer(int id) => objects[id]["image"]?.GetValue<string>() == "models/util/fullscreenlayer.json" &&
            objects[id]["effects"] is not JsonArray { Count: > 0 } &&
            !source.Contains("models/util/fullscreenlayer.json") && !source.Contains("materials/util/fullscreenlayer.json") &&
            !source.Contains("shaders/passthrough.frag") && !source.Contains("shaders/passthrough.vert");
        bool Contributes(int id) => Draws(id) && !IdentityFramebuffer(id);
        int[] textWithEffects = sourceOrder.Where(id => liveIds.Contains(id) && MayBeVisible(id) &&
            objects[id].ContainsKey("text") && objects[id]["effects"] is JsonArray { Count: > 0 }).ToArray();
        int[] simpleText = textWithEffects.Where(id => !dynamicLookup &&
            !SceneAnalyzer.Walk(objects[id]["effects"]!).OfType<JsonObject>().Any(n =>
                n.ContainsKey("script") || n.ContainsKey("animation") || n.ContainsKey("animations")) &&
            !reasons[id].Overlaps(["reads_current_framebuffer", "active_shader_audio_spectrum", "active_shader_pointer_input", "active_shader_parallax_input"]) &&
            !dependencies.OfType<JsonObject>().Any(d => Int(d["target"]) == id && d["property"]?.GetValue<string>() == "effect") &&
            !scripts.Where(pair => liveIds.Contains(pair.Key)).SelectMany(pair => pair.Value)
                .Any(code => Regex.IsMatch(code, @"\b(getEffect|getEffects|findEffect)\b|\.\s*effects\b"))).ToArray();
        var textEffectChoice = new JsonObject {
            ["selection"] = request.LiveTextEffects,
            ["status"] = request.LiveTextEffects == "simple" && simpleText.Length > 0 ? "applied" : simpleText.Length > 0 ? "available" : "not_needed",
            ["scope"] = "Optional appearance tradeoff: omit fixed text effects while preserving text scripts and parent transforms. Script-accessed and observed input/background-dependent effects remain intact.",
            ["simplified_layers"] = new JsonArray((request.LiveTextEffects == "simple" ? simpleText : []).Select(id => (JsonNode)new JsonObject {
                ["id"] = id, ["name"] = objects[id]["name"]?.DeepClone(), ["effect_count"] = objects[id]["effects"]!.AsArray().Count }).ToArray()),
            ["available_layer_ids"] = JsonSerializer.SerializeToNode(simpleText),
            ["protected_layer_ids"] = JsonSerializer.SerializeToNode(textWithEffects.Except(simpleText)) };
        int[] sourceRootOrder = roots;
        int lastBakedDraw = Array.FindLastIndex(roots, root => !liveRoots.Contains(root) &&
            sourceOrder.Any(id => allocationOf[id] == root && MayBeVisible(id) && Contributes(id)));
        // ponytail: explicit foreground placement only supports independent text/particle trees.
        // A nested overlay cannot cross any external drawable tree, including another live tree:
        // retaining its author parent would pull it back inside that parent's DFS position.
        // Image/model trees and framebuffer effects keep their order and can still block full-frame mode.
        int[] overlayRoots = roots.Take(Math.Max(0, lastBakedDraw)).Where(root => liveRoots.Contains(root) &&
            (rootOf[root] == root || !roots.Skip(Array.IndexOf(roots, root) + 1).Any(later =>
                rootOf[later] != rootOf[root] &&
                sourceOrder.Any(id => allocationOf[id] == later && MayBeVisible(id) && Contributes(id)))) &&
            sourceOrder.Any(id => allocationOf[id] == root && MayBeVisible(id) && Contributes(id)) &&
            !dependencies.OfType<JsonObject>().Any(d => d["operation"]?.GetValue<string>() is "read" or "write" &&
                ((Int(d["owner"]) is int owner && allocationOf.GetValueOrDefault(owner, -1) == root) !=
                 (Int(d["target"]) is int target && allocationOf.GetValueOrDefault(target, -1) == root))) &&
            sourceOrder.Where(id => allocationOf[id] == root && !omittedIds.Contains(id)).All(id =>
                objects[id]["disablepropagation"]?.ToJsonString() != "true" &&
                (!MayBeVisible(id) || !Contributes(id) || objects[id].ContainsKey("text") || objects[id].ContainsKey("particle")) &&
                !reasons[id].Contains("reads_current_framebuffer"))).ToArray();
        bool moveOverlays = request.LiveOverlayPlacement == "foreground" && overlayRoots.Length > 0;
        if (moveOverlays) roots = roots.Except(overlayRoots).Concat(overlayRoots).ToArray();
        var occlusionTradeoff = new Message("reason.foreground_occlusion").Write(new JsonObject {
            ["status"] = moveOverlays ? "applied" : overlayRoots.Length > 0 ? "available" : "not_needed",
            ["selection"] = request.LiveOverlayPlacement,
            ["promoted_roots"] = new JsonArray(overlayRoots.Select(root => (JsonNode)new JsonObject {
                ["root_id"] = root, ["name"] = objects[root]["name"]?.DeepClone(),
                ["layer_names"] = JsonSerializer.SerializeToNode(sourceOrder.Where(id => allocationOf[id] == root && MayBeVisible(id) && Contributes(id))
                    .Select(id => objects[id]["name"]?.GetValue<string>() ?? id.ToString())),
                ["crossed_root_ids"] = JsonSerializer.SerializeToNode(sourceRootOrder.Skip(Array.IndexOf(sourceRootOrder, root) + 1).Except(overlayRoots))
            }).ToArray()) }, "reason");
        var groups = new JsonArray();
        var current = new List<int>();
        var rootSequence = new JsonArray();
        int? ParentOf(int root) => Int(objects[root]["parent"]) is int parent && objects.ContainsKey(parent) ? parent : null;
        // 一个根是否自己可见且有可见的绘制后代。它既决定视频组能不能承担场景清屏，
        // 也是 full_frame 冲突里真正的阻挡者判据。
        bool VisibleDrawingRoot(int root) => MayBeVisible(root) &&
            sourceOrder.Any(layer => allocationOf[layer] == root && MayBeVisible(layer) && Contributes(layer));
        void Flush()
        {
            if (current.Count == 0) return;
            var ids = sourceOrder.Where(id => current.Contains(allocationOf[id]) && !omittedIds.Contains(id)).ToArray();
            string groupId = $"group-{groups.Count + 1}";
            bool includeClear = groups.Count == 0 && Resolve(scene["general"]?["clearenabled"], properties)?.ToJsonString() != "false" &&
                !(scene["general"]?["clearcolor"] is JsonObject clear && clear.ContainsKey("script")) &&
                rootSequence.OfType<JsonObject>().All(entry => entry["live_root"] is JsonValue prior &&
                    !(MayBeVisible(prior.GetValue<int>()) && sourceOrder.Any(layer =>
                        allocationOf[layer] == prior.GetValue<int>() && MayBeVisible(layer) && Contributes(layer))));
            var group = new JsonObject { ["id"] = groupId, ["root_ids"] = JsonSerializer.SerializeToNode(current),
                ["layer_ids"] = JsonSerializer.SerializeToNode(ids), ["transparent"] = !includeClear,
                ["parent_id"] = ParentOf(current[0]),
                ["parent_transform"] = ParentOf(current[0]) is int parent ? HybridVideoProjection.ParentTransform(objects, parent, properties) : null,
                ["include_scene_clear"] = includeClear,
                ["parallax_depth"] = new JsonArray(rootDepths[current[0]].X, rootDepths[current[0]].Y),
                ["capture_space"] = "scene", ["static_verified"] = false };
            // 先于这一组绘制的可见 live 根。只在存在这样的根时记录，其余场景的 plan 不受影响。
            int[] precedingVisibleLiveRoots = rootSequence.OfType<JsonObject>()
                .Select(entry => Int(entry["live_root"])).OfType<int>().Where(VisibleDrawingRoot).ToArray();
            if (precedingVisibleLiveRoots.Length > 0)
                group[SingleShotAllocation.PrecedingRootsField] = JsonSerializer.SerializeToNode(precedingVisibleLiveRoots);
            groups.Add(group);
            rootSequence.Add(new JsonObject { ["video_group"] = groupId });
            current.Clear();
        }
        foreach (int root in roots)
        {
            var descendants = sourceOrder.Where(id => allocationOf[id] == root).ToArray();
            bool visibleDrawing = MayBeVisible(root) && descendants.Any(id => MayBeVisible(id) && Contributes(id));
            if (!visibleDrawing)
            {
                // A hidden leaf sharing the pending video's parent cannot introduce
                // another drawable subtree. Keep its controller without splitting
                // adjacent videos; other ancestors still require a flush for DFS order.
                if (liveRoots.Contains(root))
                {
                    if (current.Count > 0 && (ParentOf(current[0]) != ParentOf(root) ||
                        sourceOrder.Any(id => Int(objects[id]["parent"]) == root))) Flush();
                    rootSequence.Add(new JsonObject { ["live_root"] = root });
                }
                continue;
            }
            if (liveRoots.Contains(root))
            {
                Flush(); rootSequence.Add(new JsonObject { ["live_root"] = root });
            }
            else
            {
                if (current.Count > 0 && ParentOf(current[0]) != ParentOf(root)) Flush();
                if (parallax && request.ViewMode == "preserve" && current.Count > 0 && rootDepths[current[0]] != rootDepths[root]) Flush();
                current.Add(root);
            }
        }
        Flush();
        var optionalRoots = roots.Where(root => !liveRoots.Contains(root) &&
            sourceOrder.Any(id => allocationOf[id] == root && !omittedIds.Contains(id) && (objects[id].ContainsKey("particle") ||
                SceneAnalyzer.Walk(objects[id]).OfType<JsonObject>().Any(n => n["script"] is JsonValue script &&
                    script.TryGetValue<string>(out string? code) && Regex.IsMatch(code, @"\bMath\s*\.\s*random\s*\("))))).ToHashSet();
        // Fixed foreground text can accompany a cheap particle without introducing another video.
        // It is measured too, and is only retained when the complete suffix includes a random root.
        var optionalCompanions = roots.Where(root => !liveRoots.Contains(root) && !omittedIds.Contains(root) && objects[root].ContainsKey("text") &&
            scripts[root].Length == 0 && sourceOrder.Count(id => allocationOf[id] == root) == 1 &&
            !SceneAnalyzer.Walk(objects[root]).OfType<JsonObject>().Any(n => n.ContainsKey("animation") ||
                n.ContainsKey("animations") || n["effects"] is JsonArray { Count: > 0 })).ToHashSet();
        var eligibleForeground = optionalRoots.Concat(optionalCompanions).ToHashSet();
        // ponytail: only peel optional foreground roots off each video group. Retaining an
        // interior root splits the video and can double decode area; a layer timing cannot justify it.
        var optionalForeground = groups.OfType<JsonObject>().SelectMany((group, index) => {
            int[] suffix = group["root_ids"]!.AsArray().Skip(index == 0 ? 1 : 0).Select(n => n!.GetValue<int>())
                .Reverse().TakeWhile(eligibleForeground.Contains).Reverse().ToArray();
            return suffix.Any(optionalRoots.Contains) ? suffix : [];
        }).Concat(request.VideoLayout == "full_frame"
            ? groups.OfType<JsonObject>().Skip(1).Select(group => group["root_ids"]![0]!.GetValue<int>())
            : []).Distinct().OrderBy(root => Array.IndexOf(roots, root)).ToArray();
        // A later parallax/occlusion group can stay live in its original place. Compare that
        // complete suffix before concluding that the user needs multiple transparent videos.
        var blockers = new JsonArray();
        if (!scriptErrorEvidenceAvailable) blockers.Add(MissingScriptFaultEvidenceBlocker.ToNode());
        if (groups.Count == 0) blockers.Add(PlanNarrative.NoInputIndependentGroup(objects, reasons).ToNode());
        // hdr 标志本身不是拒绝理由；拒绝理由是被捕获组的输出可能超出 [0,1] 而被 RGBA8 捕获 clip。
        // 文案走 i18n：判据给出未通过的明细，legacy 英文逐字不变，中文另报壁纸自带的 HDR 开关。
        JsonObject radianceClosure = SdrRadianceClosure.Describe(scene, properties, trace, groups, source, request.Assets,
            Resolve(scene["general"]?["hdr"], properties)?.ToJsonString() == "true", project, out Blocker? radianceBlocker);
        if (radianceBlocker is not null) blockers.Add(radianceBlocker.ToNode());
        if (projection["status"]?.GetValue<string>() != "orthographic") blockers.Add(new Blocker(BlockerCode.PerspectiveNeedsScreenspace).ToNode());
        foreach (var camera in objects.Values.Where(obj => obj.ContainsKey("camera")))
        {
            if (camera["path"] is JsonValue path && path.TryGetValue<string>(out string? file) &&
                SceneAnalyzer.ReadResourceJson(source, request.Assets, file)["paths"] is JsonArray { Count: > 0 })
                blockers.Add(new Blocker(BlockerCode.CameraPathNeedsEnvelope).ToNode());
            if (trace["runtime_projection"] is not JsonObject) blockers.Add(new Blocker(BlockerCode.RuntimeProjectionRequired).ToNode());
        }
        double canvasWidth = projection["canvas_width"]!.GetValue<double>(), canvasHeight = projection["canvas_height"]!.GetValue<double>();
        JsonObject LoopScene()
        {
            var copy = scene.DeepClone().AsObject();
            FreezeTemporalProperties(copy, properties);
            DaytimeSplit.ApplyState(copy, daytime, daytimeState, forAnalysis: true);
            return copy;
        }
        var loop = AnalyzeLoopForProfile(LoopScene, source, request.Assets, trace,
            groups.OfType<JsonObject>().SelectMany(g => g["layer_ids"]!.AsArray().Select(n => n!.GetValue<int>())).ToArray(),
            request, projection, groups);
        AnnotateLoopCandidates(loop);
        bool WholeLoopComplete(JsonObject value) => value["unresolved"] is JsonArray { Count: 0 } && value["candidates"] is JsonArray { Count: > 0 };
        // 三处回退都可能要前缀缓存，同一个终端捕获点只问一次渲染器。
        var captureProbes = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        async Task<JsonObject?> PrefixCaptureTargetAsync(JsonObject cache)
        {
            // 离线 trace 是开发者输入，没有渲染器可问；bake 的元数据探测会做同一裁定。
            if (request.RuntimeTraceFile is not null) return null;
            int owner = cache["owner_layer_id"]!.GetValue<int>(), terminal = cache["terminal_effect_id"]!.GetValue<int>();
            bool forceVisibleOwner = cache["preserve_external_visibility"]?.GetValue<bool>() == true;
            string key = owner.ToString(CultureInfo.InvariantCulture) + ":" + terminal.ToString(CultureInfo.InvariantCulture);
            if (forceVisibleOwner) key += ":visible-control";
            if (captureProbes.TryGetValue(key, out JsonObject? known)) return known;
            string persistentKey = "capture-" + AnalysisCache.Key(source.SourcePath, properties, request.Assets, tools,
                File.Exists(tools.Renderer) ? File.GetLastWriteTimeUtc(tools.Renderer).Ticks : 0,
                request.FpsNumerator, request.FpsDenominator, request.DeviceUuid, key);
            if (AnalysisCache.Read(request.AnalysisCacheDirectory, persistentKey) is JsonObject cachedProbe)
                return captureProbes[key] = cachedProbe;
            string? name = EffectPrefixCaptureTarget.LayerName(scene, owner);
            string probeOutput = Path.Combine(output, $"effect-prefix-capture-probe-{owner}-{terminal}" + (forceVisibleOwner ? "-visible" : ""));
            JsonObject observed = new(), verdict;
            try
            {
                // 与 bake 的元数据探测同一个捕获选择：原始源、快照属性、1 帧，只看渲染器实际从哪个目标取帧。
                var raw = await new NativeRenderRunner(tools).RenderRawAsync(new(source.SourcePath, request.Assets, probeOutput, 64, 64,
                    request.FpsNumerator, request.FpsDenominator, 1, Seed: 17,
                    CaptureTarget: new RenderCaptureSelection(owner, terminal, EffectTerminal: true, ExactExtent: false,
                        ForceVisibleOwner: forceVisibleOwner ? true : null),
                    UserProperties: properties, DeviceUuid: request.DeviceUuid, TraceScene: true), cancellationToken);
                observed = raw["native_result"]!.AsObject();
                verdict = EffectPrefixCaptureTarget.Evaluate(observed, owner, terminal, name);
            }
            catch (Exception error) when (error is IOException or InvalidDataException)
            {
                verdict = EffectPrefixCaptureTarget.ProbeFailed(owner, terminal, name, error.Message);
            }
            finally
            {
                TemporaryCaptureFiles.Delete(observed, probeOutput, "native/frames.rgba", "native/frames.rgba.partial",
                    "native/audio.f32le", "native/audio.f32le.partial");
            }
            verdict["probe_output"] = probeOutput;
            captureProbes[key] = verdict;
            if (verdict["status"]?.GetValue<string>() != EffectPrefixCaptureTarget.ProbeFailedStatus)
                AnalysisCache.Write(request.AnalysisCacheDirectory, persistentKey, verdict);
            return verdict;
        }
        async Task<JsonArray> PrefixCachesAsync()
        {
            if (PrefixSafetyBlocked(blockers)) return new JsonArray();
            var proposed = EffectPrefixPlanner.Propose(scene, source, request.Assets, trace, properties, request, projection);
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
                if (await PrefixCaptureTargetAsync(cache) is { } probe &&
                    probe["status"]?.GetValue<string>() != EffectPrefixCaptureTarget.LayerTargetStatus) continue;
                accepted.Add(cache.DeepClone());
                settled.Add(ownerId);
            }
            return accepted;
        }
        JsonArray effectPrefixCaches = WholeLoopComplete(loop) ? new JsonArray() : await PrefixCachesAsync();
        bool effectPrefixRoute = effectPrefixCaches.Count > 0;
        var groupOfRoot = groups.OfType<JsonObject>().SelectMany(group => group["root_ids"]!.AsArray()
            .Select(root => (Root: root!.GetValue<int>(), Group: group))).ToDictionary(item => item.Root, item => item.Group);
        // 给用户看的图层清单：几何占比、可见性绑定与分配去向，外加只标注不改动的覆盖层嫌疑。
        double centerX = Numeric(projection["center_x"], canvasWidth / 2), centerY = Numeric(projection["center_y"], canvasHeight / 2);
        // 面积只由尺寸与整条缩放链决定，父链旋转不改变面积；位置需要完整的父变换，父链带旋转时位置写 null
        // 而不是假的 0.5。算不出的量一律写 null：写 0 会让下游把"未知"当成"零面积"（残差掩盖的 25% 精灵占比闸门
        // 就曾因此对旋转父链下的精灵失效）。
        (double? Fraction, double? X, double? Y) Placement(int id)
        {
            double area = canvasWidth * canvasHeight;
            if (!double.IsFinite(area) || area <= 0) return (null, null, null);
            double? fraction = null, x = null, y = null;
            try
            {
                var scale = HybridVideoProjection.Vector(Resolve(objects[id]["scale"], properties), (1, 1));
                (double X, double Y) chainScale = (1, 1);
                var seen = new HashSet<int> { id };
                int? ancestor = Int(objects[id]["parent"]);
                while (ancestor is int parentId && objects.TryGetValue(parentId, out JsonObject? parentObject) && seen.Add(parentId))
                {
                    var parentScale = HybridVideoProjection.Vector(Resolve(parentObject["scale"], properties), (1, 1));
                    chainScale = (chainScale.X * parentScale.X, chainScale.Y * parentScale.Y);
                    ancestor = Int(parentObject["parent"]);
                }
                if (objects[id]["size"] is not null)
                {
                    var size = HybridVideoProjection.Vector(Resolve(objects[id]["size"], properties), (0, 0));
                    double coverage = Math.Abs(size.X * scale.X * chainScale.X * size.Y * scale.Y * chainScale.Y) / area;
                    // 包围盒伸出画布的部分不可见，占比封顶 1。
                    if (double.IsFinite(coverage)) fraction = Math.Min(1, coverage);
                }
            }
            catch (InvalidDataException) { }
            try
            {
                var origin = HybridVideoProjection.Vector(Resolve(objects[id]["origin"], properties), (0, 0));
                JsonObject? inherited = Int(objects[id]["parent"]) is int parent && objects.ContainsKey(parent)
                    ? HybridVideoProjection.ParentTransform(objects, parent, properties) : null;
                var parentOrigin = HybridVideoProjection.Vector(inherited?["origin"], (0, 0));
                var parentScale = HybridVideoProjection.Vector(inherited?["scale"], (1, 1));
                double centreX = .5 + (parentOrigin.X + parentScale.X * origin.X - centerX) / canvasWidth;
                double centreY = .5 + (parentOrigin.Y + parentScale.Y * origin.Y - centerY) / canvasHeight;
                if (double.IsFinite(centreX) && double.IsFinite(centreY)) (x, y) = (centreX, centreY);
            }
            catch (InvalidDataException) { }
            return (fraction, x, y);
        }
        var compositeTargets = dependencies.OfType<JsonObject>().Where(d => d["property"]?.GetValue<string>() == "layerComposite")
            .Select(d => Int(d["target"])).OfType<int>().ToHashSet();
        string Kind(int id) => objects[id].ContainsKey("particle") ? "particle" : objects[id].ContainsKey("text") ? "text" :
            compositeTargets.Contains(id) ? "composite" : objects[id].ContainsKey("image") ? "image" : "other";
        string VisibleBinding(int id) => objects[id]["visible"] is not JsonObject binding ? "constant" :
            binding.ContainsKey("script") ? "script" :
            binding.ContainsKey("animation") || binding.ContainsKey("animations") ? "animation" :
            binding.ContainsKey("user") ? "user_property" : "constant";
        string Name(int id) => objects[id]["name"] is JsonValue value && value.TryGetValue<string>(out string? text) ? text : "";
        // 可取舍元素怎么关：这一层的显示开关绑在哪个壁纸属性上、当前值多少、给什么值算关。
        // 本层没绑就沿父链找最近一个绑了属性的祖先——关掉祖先会连带关掉这一层，对用户同样是"在 WPE 里关一个开关"。
        // 推不出来时写明原因（没绑属性 / project.json 没声明 / 属性不是开关），不猜。
        JsonObject VisibleProperty(int id)
        {
            int owner = id;
            (string Name, string? Condition)? bound = null;
            var visited = new HashSet<int>();
            for (int? cursor = id; cursor is int current && objects.ContainsKey(current) && visited.Add(current);
                cursor = Int(objects[current]["parent"]))
                if (PlanNarrative.BoundProperty(objects[current]["visible"]) is { } found) { (bound, owner) = (found, current); break; }
            if (bound is not { } binding) return new JsonObject { ["status"] = "not_bound", ["visible_binding"] = VisibleBinding(id) };
            string key = binding.Name;
            var definition = project["general"]?["properties"]?[key] as JsonObject;
            string type = definition?["type"] is JsonValue typeValue && typeValue.TryGetValue<string>(out string? typeText) ? typeText : "";
            JsonNode? value = properties.TryGetPropertyValue(key, out JsonNode? saved) ? saved?.DeepClone() : definition?["value"]?.DeepClone();
            var record = new JsonObject {
                ["key"] = key, ["label"] = definition?["text"]?.DeepClone(), ["type"] = definition is null ? null : type,
                ["declared"] = definition is not null, ["binding"] = owner == id ? "self" : "ancestor",
                ["bound_layer_id"] = owner, ["condition"] = binding.Condition, ["current_value"] = value };
            static string Scalar(JsonNode? node) => node is JsonValue text && text.TryGetValue<string>(out string? plain)
                ? plain : node?.ToJsonString() ?? "null";
            if (binding.Condition is string condition)
            {
                // 带 condition 的绑定：属性值等于 condition 时才显示，给它任何别的值都是关。
                JsonNode? other = (definition?["options"] as JsonArray)?.OfType<JsonObject>()
                    .Select(option => option["value"]).FirstOrDefault(option => Scalar(option) != condition);
                record["off_value"] = other?.DeepClone();
                record["status"] = "conditional";
                record["off_hint_zh"] = other is null ? $"把 \"{key}\" 设成任何不等于 \"{condition}\" 的值"
                    : $"把 \"{key}\" 设成 {other.ToJsonString()}";
                record["off_hint_en"] = other is null ? $"give \"{key}\" any value other than \"{condition}\""
                    : $"set \"{key}\" to {other.ToJsonString()}";
                return record;
            }
            if (definition is not null && type != "bool")
            {
                // 显示直接取属性值，而这个属性不是开关：关闭值推不出来，如实写明。
                record["status"] = "type_not_boolean";
                record["off_hint_zh"] = $"属性 \"{key}\" 是 {type} 类型，不是开关，关闭值要自己判断";
                record["off_hint_en"] = $"the \"{key}\" property is a {type}, not a switch; its off value must be judged manually";
                return record;
            }
            record["off_value"] = false;
            record["status"] = "resolved";
            record["off_hint_zh"] = $"关闭值 false，--properties 文件写 {{\"{key}\": false}}";
            record["off_hint_en"] = $"the off value is false; write {{\"{key}\": false}} in the --properties file";
            return record;
        }
        string[] OverlaySuspicion(int id, double? fraction, double? x, double? y)
        {
            var found = new List<string>();
            if (OverlayVocabulary.IsMatch(Name(id))) found.Add("name_matches_overlay_vocabulary");
            if (objects[id].ContainsKey("image") && fraction is > 0 and < .12 &&
                x is <= .2 or >= .8 && y is <= .2 or >= .8) found.Add("small_image_in_canvas_corner");
            if (objects[id]["visible"] is JsonObject visibility && visibility["script"] is JsonValue script &&
                script.TryGetValue<string>(out string? code) &&
                Regex.IsMatch(CapabilityScanText(code), @"\b(setTimeout|setInterval|Date)\b")) found.Add("timer_driven_visibility");
            return found.ToArray();
        }
        // 只记录这次分析实际用的是哪台设备；要不要烘是用户的事，不在这里裁决。
        JsonObject analysisDevice = DescribeAnalysisDevice(EnumerateDevicesOrNull(), request.DeviceUuid);
        var report = new JsonObject {
            ["schema_version"] = HybridPlanFormat.CurrentVersion, ["kind"] = "hybrid_video", ["route"] = effectPrefixRoute ? "effect_prefix" : "whole_layer",
            ["status"] = effectPrefixRoute || blockers.Count == 0 ? "requires_loop_analysis" : "requires_resolution",
            ["source"] = source.SourcePath, ["source_sha256"] = sourceHash, ["source_digest_scope"] = ProjectSource.DigestScope,
            ["assets"] = Path.GetFullPath(request.Assets), ["analysis_directory"] = output,
            ["settings"] = PlanSettings.ToJson(request),
            // 档位与两个高级覆盖合成的生效值，每个值带来源（preset/override/default）；求解器与 bake 读的都是这一份。
            ["retime_profile"] = RetimeProfile.Resolve(request).ToJson(),
            ["output_resolution"] = resolution.ToJson(),
            ["analysis_device"] = analysisDevice.DeepClone(),
            ["canvas_width"] = canvasWidth, ["canvas_height"] = canvasHeight,
            ["projection"] = projection,
            ["snapshot_properties"] = properties, ["has_parallax"] = parallax,
            ["video_groups"] = groups, ["composition"] = rootSequence,
            ["live_layer_ids"] = JsonSerializer.SerializeToNode(sourceOrder.Where(liveIds.Contains)),
            ["omitted_snapshot_layer_ids"] = JsonSerializer.SerializeToNode(sourceOrder.Where(omittedIds.Contains)),
            ["excluded_layer_ids"] = JsonSerializer.SerializeToNode(excludedRoots),
            ["fixed_user_properties"] = JsonSerializer.SerializeToNode(fixedProperties),
            ["root_order"] = JsonSerializer.SerializeToNode(roots),
            ["source_root_order"] = JsonSerializer.SerializeToNode(authorRoots),
            ["source_allocation_order"] = JsonSerializer.SerializeToNode(sourceRootOrder),
            ["occlusion_tradeoff"] = occlusionTradeoff,
            ["text_effects_choice"] = textEffectChoice,
            ["audio_effects_choice"] = audioEffectChoice,
            ["root_roles"] = new JsonArray(roots.Select((root, index) => (JsonNode)new JsonObject {
                ["root_id"] = root, ["source_order"] = index,
                ["role"] = omittedIds.Contains(root) ? "omitted_snapshot" : liveRoots.Contains(root) ? "live" :
                    groupOfRoot.ContainsKey(root) ? "video" : "inactive",
                ["video_group"] = groupOfRoot.GetValueOrDefault(root)?["id"]?.DeepClone(),
                ["parallax_depth"] = new JsonArray(rootDepths[root].X, rootDepths[root].Y) }).ToArray()),
            ["layers"] = new JsonArray(sourceOrder.Select(id => {
                var (fraction, x, y) = Placement(id);
                string[] suspicion = OverlaySuspicion(id, fraction, x, y);
                return (JsonNode)new JsonObject {
                    ["id"] = id, ["root"] = rootOf[id], ["allocation_root"] = allocationOf[id],
                    ["parent"] = objects[id]["parent"]?.DeepClone(), ["source_order"] = Array.IndexOf(sourceOrder, id),
                    ["name"] = objects[id]["name"]?.DeepClone(),
                    ["kind"] = Kind(id), ["has_image"] = objects[id].ContainsKey("image"),
                    ["canvas_fraction"] = fraction is double coverage ? Math.Round(coverage, 6) : null,
                    ["canvas_center_x"] = x is double centreX ? Math.Round(centreX, 6) : null,
                    ["canvas_center_y"] = y is double centreY ? Math.Round(centreY, 6) : null,
                    ["quadrant"] = x is not double qx || y is not double qy ? "unknown"
                        : qx is > .4 and < .6 && qy is > .4 and < .6 ? "center"
                        : (qy >= .5 ? "top_" : "bottom_") + (qx < .5 ? "left" : "right"),
                    ["visible_binding"] = VisibleBinding(id),
                    ["allocation"] = excludedIds.Contains(id) ? "excluded" : omittedIds.Contains(id) ? "omitted" :
                        liveIds.Contains(id) ? "live" : groupOfRoot.ContainsKey(allocationOf[id]) ? "video" : "inactive",
                    ["suspected_overlay"] = suspicion.Length > 0,
                    ["suspected_overlay_reasons"] = JsonSerializer.SerializeToNode(suspicion),
                    ["live"] = liveIds.Contains(id), ["reasons"] = JsonSerializer.SerializeToNode(reasons[id]),
                    // 取舍清单用的两个标签与关闭办法：只给实时层，其余层这三个字段为 null。
                    ["tradeoff_class"] = liveIds.Contains(id) ? TradeoffOptions.Classify(reasons[id], suspectedOverlay: suspicion.Length > 0).Class : null,
                    ["tradeoff_kinds"] = liveIds.Contains(id)
                        ? JsonSerializer.SerializeToNode(TradeoffOptions.Classify(reasons[id], suspectedOverlay: suspicion.Length > 0).Kinds) : null,
                    ["visible_property"] = liveIds.Contains(id) ? VisibleProperty(id) : null,
                    ["visible"] = Visible(id), ["drawable"] = Draws(id) };
            }).ToArray()),
            ["optional_realtime_roots"] = JsonSerializer.SerializeToNode(optionalForeground),
            ["hdr_radiance_closure"] = radianceClosure,
            ["composition_policy"] = "Keep adjacent input-independent content together; optional live foreground must not split a video group or change draw order.",
            ["blockers"] = effectPrefixRoute ? new JsonArray() : blockers, ["loop"] = loop,
            ["whole_layer"] = new JsonObject { ["blockers"] = blockers.DeepClone(), ["loop"] = loop.DeepClone(),
                ["status"] = WholeLoopComplete(loop) && blockers.Count == 0 ? "available" : "unavailable" },
            ["effect_prefix_caches"] = effectPrefixCaches,
            ["encoding"] = new JsonObject { ["codec"] = "auto_h264_hevc", ["pixel_format"] = "yuv420p", ["crf"] = 16,
                ["color_space"] = "bt709_sdr", ["transparent_groups"] = "rgb_contribution_and_coverage_side_by_side",
                ["selection_basis"] = "H.264 within 4096 pixels and level 5.2 frame/rate limits; HEVC otherwise. Final cropped stream determines selection; target hardware decoding and playback still require verification." },
            ["runtime_evidence"] = Path.Combine(output, "runtime.json"),
            ["source_script_error_evidence"] = new JsonObject {
                ["status"] = scriptErrorEvidenceAvailable ? "available" : "not_available",
                ["reason"] = scriptErrorEvidenceAvailable ? null : MissingScriptFaultEvidenceBlocker.Text },
            ["source_script_error_count"] = scriptErrorCount is int recordedErrorCount ? recordedErrorCount : null,
            ["source_script_errors"] = scriptErrorEvidenceAvailable ? sourceScriptErrors.DeepClone() : null,
            ["official_playback"] = "not_verified", ["measured_gain"] = "not_verified" };
        // 开关关着时不写这一段，plan 逐字不变。
        if (daytime is not null) report["daytime_split"] = daytime.ToJson();
        JsonObject wholeLayer = report["whole_layer"]!.AsObject();
        if (!effectPrefixRoute) wholeLayer["blockers"] = report["blockers"]!.DeepClone();
        wholeLayer["loop"] = report["loop"]!.DeepClone();
        wholeLayer["status"] = wholeLayer["blockers"]!.AsArray().Count == 0 &&
            WholeLoopComplete(wholeLayer["loop"]!.AsObject()) ? "available" : "unavailable";
        if (!effectPrefixRoute && report["status"]?.GetValue<string>() == "requires_resolution")
        {
            JsonArray fallback = await PrefixCachesAsync();
            if (fallback.Count > 0)
            {
                effectPrefixRoute = true;
                report["route"] = "effect_prefix";
                report["effect_prefix_caches"] = fallback;
                report["blockers"] = new JsonArray();
                report["status"] = "requires_loop_analysis";
            }
        }
        // 全幅准入：先把"单不透明组可达性"写进 plan，冲突文案才能给出本场景真正可执行的指令。
        if (request.VideoLayout == "full_frame" && !effectPrefixRoute && groups.Count > 1)
            report["full_frame_retention"] = FullFrameDemotion.Describe(report, dependencies);
        // 一个视频组都没有时无从谈布局：blockers 已经说明依赖闭包之后没有输入无关的可视组，再追加一条
        // "改设置或选分层后重新分析"只会把用户引向无效操作。分配路径仍用这条冲突报告不透明视频的丢失。
        Blocker? layoutConflict = (groups.Count == 0 ? null : FullFrameConflict(report)) ?? CompositionHierarchyConflict(report);
        bool layoutDemoted = false;
        // 尾组降级：把底组之上的视频 root 整体退回实时，是全幅被拒时唯一允许的自动补救。
        if (layoutConflict is not null && !effectPrefixRoute && request.VideoLayout == "full_frame" && groups.Count > 1)
        {
            JsonObject? demotedPlan = FullFrameDemotion.TryDemote(report, dependencies, out JsonObject demotion);
            if (demotedPlan is null) report["layout_admission_demotion"] = demotion;
            else
            {
                demotedPlan["layout_admission_demotion"] = demotion;
                if (report["full_frame_retention"]?.DeepClone() is JsonObject retention)
                {
                    retention["status"] = "applied";
                    demotedPlan["full_frame_retention"] = retention;
                }
                // 视频组少了被退回的层，循环必须按新的组重算，否则 plan 的周期仍引用已退出视频的分量。
                JsonObject DemotedLoopScene()
                {
                    var copy = scene.DeepClone().AsObject();
                    FreezeTemporalProperties(copy, properties);
                    DaytimeSplit.ApplyState(copy, daytime, daytimeState, forAnalysis: true);
                    return copy;
                }
                demotedPlan["loop"] = AnalyzeLoopForProfile(DemotedLoopScene, source, request.Assets, trace,
                    demotedPlan["video_groups"]!.AsArray().OfType<JsonObject>()
                        .SelectMany(group => group["layer_ids"]!.AsArray().Select(node => node!.GetValue<int>())).ToArray(),
                    request, demotedPlan["projection"] as JsonObject ?? new JsonObject(), demotedPlan["video_groups"] as JsonArray);
                AnnotateLoopCandidates(demotedPlan["loop"]!.AsObject());
                JsonObject demotedWholeLayer = demotedPlan["whole_layer"]!.AsObject();
                demotedWholeLayer.Remove("layout_conflict");
                demotedWholeLayer["blockers"] = demotedPlan["blockers"]!.DeepClone();
                demotedWholeLayer["loop"] = demotedPlan["loop"]!.DeepClone();
                demotedWholeLayer["status"] = demotedWholeLayer["blockers"]!.AsArray().Count == 0 &&
                    WholeLoopComplete(demotedWholeLayer["loop"]!.AsObject()) ? "available" : "unavailable";
                report = demotedPlan;
                layoutConflict = null;
                layoutDemoted = true;
            }
        }
        if (layoutConflict is not null && !effectPrefixRoute)
        {
            JsonArray fallback = await PrefixCachesAsync();
            if (fallback.Count > 0)
            {
                effectPrefixRoute = true;
                report["route"] = "effect_prefix";
                report["effect_prefix_caches"] = fallback;
                report["blockers"] = new JsonArray();
                report["status"] = "requires_loop_analysis";
            }
        }
        if (layoutConflict is not null) report["whole_layer"]!.AsObject()["layout_conflict"] = layoutConflict.Text;
        report["video_layout_admission"] = new JsonObject {
            ["requested"] = request.VideoLayout,
            ["status"] = effectPrefixRoute ? "not_applicable_to_effect_prefix" :
                groups.Count == 0 ? "not_applicable_no_video_group" :
                layoutConflict is not null ? LayoutConflictStatus(report) :
                layoutDemoted ? FullFrameDemotion.DemotedAdmissionStatus : "planned_layout_allowed",
            ["reason"] = layoutConflict?.Text ?? (layoutDemoted ? report["layout_admission_demotion"]?["reason"]?.GetValue<string>() : null),
            ["scope"] = effectPrefixRoute ? "Effect-prefix caching retains the authored layer and suffix; it does not reorder whole-layer groups." :
                "Layout permission is not proof of image correctness, looping, hardware decoding or playback benefit." };
        if (layoutConflict is not null && !effectPrefixRoute)
        {
            var finalBlockers = report["blockers"]!.AsArray();
            PlanBlockers.Add(finalBlockers, layoutConflict);
            report["status"] = "requires_resolution";
        }
        // 探测过的前缀捕获点全部留档。被拒的原因只在整层循环本来就有未解机制时并进 loop.unresolved：这时前缀是
        // 整层循环的回退，拒绝原因正好说明回退为什么没走成。条目不带 owner_layer_id，免得分配回退把它当成要保留实时的
        // 未解层；原本没有未解项的循环也不凭空添一条，免得改变整层裁定。
        if (captureProbes.Count > 0)
        {
            report["effect_prefix_capture_probes"] = new JsonArray(captureProbes.Values.Select(probe => (JsonNode)probe.DeepClone()).ToArray());
            if (report["loop"]?["unresolved"] is JsonArray { Count: > 0 })
                foreach (JsonObject probe in captureProbes.Values.Where(probe =>
                    probe["status"]?.GetValue<string>() != EffectPrefixCaptureTarget.LayerTargetStatus))
                    AddLoopUnresolved(report, "effect_prefix_capture_target", probe["reason"]!.GetValue<string>(), probe["reason_localized"]);
        }
        // README 的承诺：解析周期不完整时，把未解决机制与粒子所在的完整作者子树保留实时，再重查一次周期与构图。
        // 这条路以前只在 bake 阶段跑，analyze 既没走也没记录，用户拿到的就是一个没有任何理由的 unavailable。
        JsonObject residualScene = source.ReadJson(source.SceneResource);
        Func<string, JsonObject?> residualResources = ResidualMasking.ResourceReader(source, request.Assets);
        await RecordLoopAllocationFallbackAsync(report, scene, request, output, residualScene, residualResources, progress, cancellationToken);
        // 残差掩盖要在可掩盖分量所在的视频组里淡化（透明组、多组都可以）：分量不在任何视频组里时，在这里写 blocker，不留到 bake 才拒。
        // 放在更小分配取证之后，blocker 才能给出重查过的 --retain-live id；判定读原始源场景，与 bake 第一步同一个 Admission.Evaluate。
        Admission.ApplyResidualLayoutGate(report, residualScene, residualResources);
        RecordSolverNoCandidateBlocker(report);
        RequireTraceableRejection(report);
        // 视频外壳判据放在最后：前面两处 effect_prefix 回退与布局裁决都已经定稿，这里只读结构、只追加，
        // 不改 route、不改分组，免得新加的 blocker 反过来把计划改道。
        JsonObject videoDominance = VideoDominance.Evaluate(report, trace, request.VideoShell);
        report["video_dominant"] = videoDominance;
        report[BakeValueAssessment.Field] = BakeValueAssessment.Evaluate(report, trace, source, request.Assets);
        if (videoDominance["status"]?.GetValue<string>() == VideoDominance.ShellStatus)
        {
            PlanBlockers.Add(report["blockers"]!.AsArray(), new Blocker(BlockerCode.VideoShell));
            report["status"] = "requires_resolution";
        }
        // 特效前缀缓存的编码尺寸在 analyze 阶段就能按源纹理算出来：越过硬件解码上限的提前写 unresolved 提示。
        // 只追加这一个字段，不改 effect_prefix_caches（bake 会逐字比对它），也不改 route 与裁决。
        if (report["effect_prefix_caches"] is JsonArray { Count: > 0 })
            report["effect_prefix_hardware_decode_preflight"] = HardwareDecodeDimensions.PredictEffectPrefixCaches(report, source,
                request.Assets, request, report["projection"] as JsonObject ?? projection);
        // These queries are already present in the analysis trace. Use the exporter's
        // existing assembly rule before promising a whole-layer bake, without rendering again.
        if (!effectPrefixRoute && dependencies.OfType<JsonObject>().Any(item =>
                item["operation"]?.GetValue<string>() == "query" &&
                item["property"]?.GetValue<string>()?.StartsWith("layer_", StringComparison.Ordinal) == true) &&
            CompositionHierarchyConflict(report, objects, dependencies) is Blocker publicQueryConflict)
        {
            PlanBlockers.Add(report["blockers"]!.AsArray(), publicQueryConflict);
            PlanBlockers.Add(report["whole_layer"]!["blockers"]!.AsArray(), publicQueryConflict);
            report["whole_layer"]!["status"] = "unavailable";
            report["status"] = "requires_resolution";
        }
        report["suitability"] = Suitability(report);
        // 属性来源紧跟在 snapshot_properties 后面：没有来源记录的请求（测试、内部重分析）plan 不变。
        if (request.PropertiesOrigin is JsonObject propertiesOrigin) AttachPropertiesSource(report, propertiesOrigin);
        // 帧率来源紧跟在 output_resolution 后面：没有来源记录的请求（测试、内部重分析）plan 不变。
        if (request.FrameRateOrigin is JsonObject frameRateOrigin) AttachFrameRate(report, frameRateOrigin);
        // 双语字段与一行结论只读已经定好的 blockers / loop / candidates，不参与任何判定。
        PlanNarrative.Attach(report);
        await VideoSceneBuilder.WriteJsonAsync(Path.Combine(output, "plan.json"), report, cancellationToken);
        if (sourceHash != await source.SourceHashAsync(cancellationToken)) throw new IOException("Source changed during analysis.");
        return report;
    }

    /// <summary>
    /// 整层路线在"零 blocker 却求不出循环"时，真的试一次更小的烘焙分配，并把走了什么、结果如何写进 plan。
    /// 这里只取证，不替换用户没有要求的分配：真要改分配由 bake 的同名回退或显式 --retain-live 执行。
    /// </summary>
    private async Task RecordLoopAllocationFallbackAsync(JsonObject report, JsonObject scene, HybridAnalyzeRequest request,
        string output, JsonObject sourceScene, Func<string, JsonObject?> readResource,
        IProgress<RenderProgress>? progress, CancellationToken cancellationToken)
    {
        // 重查请求自己带着 retain_live_root_ids，绝不递归第二层。
        if ((request.RetainLiveRootIds ?? []).Length != 0 || report["route"]?.GetValue<string>() != "whole_layer" ||
            report["whole_layer"]?["status"]?.GetValue<string>() != "unavailable" ||
            report["blockers"] is not JsonArray { Count: 0 }) return;
        JsonObject evidence = HybridLoopAllocation.Explain(report, scene);
        report["loop_allocation_fallback"] = evidence;
        if (evidence["status"]?.GetValue<string>() != "proposed")
        {
            AddLoopUnresolved(report, "loop_allocation_fallback",
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
            evidence["replanned_unresolved"] = replannedLoop["unresolved"]!.DeepClone();
            // 重查只被全幅布局挡住时，把冲突给出的保留做法按完整 --retain-live 列表记下来，结论行才能给出照做就能用的参数。
            if (!resolved && HybridLoopAllocation.ReplannedRetainLiveSuggestion(replanned, retained) is JsonObject suggestion)
                evidence["replanned_retain_live_suggestion"] = suggestion;
            AddLoopUnresolved(report, "loop_allocation_fallback", resolved
                ? $"No loop covers every baked layer, but a smaller bake allocation does: keeping author roots {retainedText} live leaves content that resolves. Re-run analyze with --retain-live {retainedText} to plan that allocation."
                : $"No loop covers every baked layer, and the smaller bake allocation that keeps author roots {retainedText} live establishes none either.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            evidence["status"] = "failed";
            evidence["error_type"] = error.GetType().Name;
            evidence["error"] = error.Message;
            AddLoopUnresolved(report, "loop_allocation_fallback",
                $"No loop covers every baked layer, and the smaller bake allocation keeping author roots {retainedText} live could not be analyzed: {error.Message}");
        }
    }

    /// <summary>plan 里的 loop 与 whole_layer.loop 是两份独立副本，追加理由时必须同时写。</summary>
    private static void AddLoopUnresolved(JsonObject report, string kind, string detail, JsonNode? localized = null)
    {
        // localized 是 detail 的 {key, zh, en, params}（例如捕获点探测的理由）；只有英文原文的理由不带。
        var entry = new JsonObject { ["kind"] = kind, ["detail"] = detail };
        if (localized is not null) entry[PlanNarrative.DetailLocalized] = localized.DeepClone();
        foreach (JsonNode? node in new JsonNode?[] { report["loop"], report["whole_layer"]?["loop"] })
            if (node is JsonObject loop && loop["unresolved"] is JsonArray unresolved &&
                !unresolved.Any(item => JsonNode.DeepEquals(item, entry)))
                unresolved.Add(entry.DeepClone());
    }

    /// <summary>
    /// 整层不可用、没有阻断、也没有任何未解析机制，但求解器给出了结构化的空候选原因（公共步长上没有闭合帧、不可调速分量的周期超上限、
    /// 单段视频调速落不到整数帧）：这就是分析结论，写成 blocker 讲给用户，而不是留给下面的不变量当内部错误抛出。
    /// 典型路径是 --retain-live（包括补充分析的重查）把所有未解析机制的所有者留成实时，剩下的分量各有周期却凑不出公共循环。
    /// 没有结构化原因、或原因是"没有时间机制"（那条路由静态证明负责记录）时不处理，仍由不变量兜底。
    /// </summary>
    internal static void RecordSolverNoCandidateBlocker(JsonObject report)
    {
        if (report["route"]?.GetValue<string>() != "whole_layer" || report["whole_layer"] is not JsonObject wholeLayer ||
            wholeLayer["status"]?.GetValue<string>() != "unavailable" || wholeLayer["blockers"] is JsonArray { Count: > 0 } ||
            wholeLayer["loop"] is not JsonObject loop || loop["candidates"] is JsonArray { Count: > 0 } ||
            (loop["unresolved"] as JsonArray ?? []).OfType<JsonObject>().Any(item => item["kind"]?.GetValue<string>() != ResidualMasking.AllocationFallbackKind) ||
            loop["no_candidate_reason"] is not JsonObject reason) return;
        // 内存里刚写的 plan 是 int/double 各自装箱，从磁盘读回的是 JsonElement：两种都要读得出来。
        static double Number(JsonNode? node) => node is not JsonValue value ? 0
            : value.TryGetValue(out double number) ? number : value.TryGetValue(out long integer) ? integer : value.TryGetValue(out int small) ? small : 0;
        static string Seconds(JsonNode? node) => Number(node).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        string ceiling = Seconds(reason["ceiling_seconds"]), period = Seconds(reason["fixed_period_seconds"]);
        int shaders = (int)Number(reason["shader_component_count"]), tracks = (int)Number(reason["runtime_period_count"]);
        Blocker? blocker = reason["kind"]?.GetValue<string>() switch
        {
            nameof(CommonLoopNoCandidateKind.NoFrameOnFixedStepSatisfiesComponents) => new Blocker(BlockerCode.LoopNoCommonFrame, [shaders, tracks, period, ceiling]),
            nameof(CommonLoopNoCandidateKind.FixedPeriodExceedsCeiling) => new Blocker(BlockerCode.LoopFixedPeriodExceedsCeiling, [tracks, period, ceiling]),
            nameof(CommonLoopNoCandidateKind.NoExactVideoRetimeFrame) => new Blocker(BlockerCode.LoopNoExactVideoRetime),
            _ => null
        };
        if (blocker is null) return;
        foreach (JsonNode? node in new[] { report["blockers"], wholeLayer["blockers"] })
            if (node is JsonArray blockers) PlanBlockers.Add(blockers, blocker);
        report["status"] = "requires_resolution";
    }

    /// <summary>
    /// 通用不变量：unavailable 一定要留下可追溯的理由。blockers、loop.unresolved 与求解器的结构化空候选原因
    /// （loop.no_candidate_reason，HybridSuitability 据此裁定，RecordSolverNoCandidateBlocker 会把它写成 blocker）
    /// 同时为空的 unavailable 是状态机漏写，属于内部错误——它对用户表现为"退出码 0、零产出、零解释"，比抛出异常更难处理。
    /// 把粒子层留实时之后剩下的层常常只有一个空候选原因（手工轨道公共周期超上限、着色器分量没有公共帧），
    /// 这不是漏写，是已经说清楚的拒绝。
    /// </summary>
    internal static void RequireTraceableRejection(JsonObject report)
    {
        // 结构化的空候选原因算可追溯（RecordSolverNoCandidateBlocker 会把它写成 blocker），但"没有时间机制"不算：
        // 那条路由由静态证明负责记录理由，缺了就是状态机漏写。
        static bool Traceable(JsonNode? loop) => loop?["no_candidate_reason"] is JsonObject reason &&
            reason["kind"]?.GetValue<string>() != nameof(CommonLoopNoCandidateKind.NoTemporalMechanism);
        if (report["whole_layer"] is not JsonObject wholeLayer || wholeLayer["status"]?.GetValue<string>() != "unavailable" ||
            wholeLayer["blockers"] is JsonArray { Count: > 0 } || wholeLayer["loop"]?["unresolved"] is JsonArray { Count: > 0 } ||
            Traceable(wholeLayer["loop"]) || Traceable(report["loop"]))
            return;
        // 特效前缀路线自带可烘方案，整层不可用不是这次分析的结局。
        if (report["route"]?.GetValue<string>() == "effect_prefix" && report["effect_prefix_caches"] is JsonArray { Count: > 0 }) return;
        string groups = string.Join("; ", (report["video_groups"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(group => $"{group["id"]?.GetValue<string>() ?? "?"}=[{string.Join(",", (group["layer_ids"] as JsonArray ?? []).Select(id => id?.ToJsonString()))}]"));
        throw new InvalidOperationException("Internal error: whole-layer analysis ended as unavailable without a single blocker or " +
            $"unresolved loop mechanism. route={report["route"]?.GetValue<string>()}, loop.status={report["loop"]?["status"]?.GetValue<string>()}, " +
            $"loop.candidates={(report["loop"]?["candidates"] as JsonArray)?.Count}, baked groups: {(groups.Length == 0 ? "(none)" : groups)}.");
    }
    /// <summary>纯函数裁定：只读 plan，给出 verdict/rule/reason_en/reason_zh/notes，不改任何既有字段。</summary>
    internal static JsonObject Suitability(JsonObject plan) => HybridSuitability.Verdict(plan);

    internal static int Id(JsonObject obj) => obj["id"]!.GetValue<int>();
    internal static int? Int(JsonNode? node) => node is JsonValue value && value.TryGetValue<int>(out int n) ? n : null;
    internal static double Numeric(JsonNode? node, double fallback) => node is JsonValue value && value.TryGetValue<double>(out double n) ? n : fallback;
    /// <summary>把属性来源写成 plan 的 properties_source（wpe / defaults / wpe_unavailable）与 wpe_properties（原因、配置位置、生效键）。</summary>
    internal static void AttachPropertiesSource(JsonObject report, JsonObject origin)
    {
        var detail = origin.DeepClone().AsObject();
        JsonNode source = detail["source"]?.DeepClone() ?? WallpaperEngineProperties.SourceDefaults;
        detail.Remove("source");
        report.Remove("properties_source");
        report.Remove("wpe_properties");
        int at = report.IndexOf("snapshot_properties");
        if (at < 0) { report["properties_source"] = source; report["wpe_properties"] = detail; return; }
        report.Insert(at + 1, "properties_source", source);
        report.Insert(at + 2, "wpe_properties", detail);
    }

    /// <summary>把帧率来源写成 plan 的 frame_rate（explicit / auto、两条依据与选中值），位置紧挨 output_resolution。</summary>
    internal static void AttachFrameRate(JsonObject report, JsonObject origin)
    {
        var detail = origin.DeepClone().AsObject();
        report.Remove("frame_rate");
        int at = report.IndexOf("output_resolution");
        if (at < 0) report["frame_rate"] = detail;
        else report.Insert(at + 1, "frame_rate", detail);
    }

    internal static JsonObject SnapshotProperties(JsonObject project, JsonObject? overrides)
    {
        var output = new JsonObject();
        if (project["general"]?["properties"] is JsonObject properties)
            foreach (var (key, value) in properties) output[key] = (value is JsonObject entry ? entry["value"] : value)?.DeepClone();
        if (overrides is not null)
            foreach (var (key, value) in overrides) output[key] = (value is JsonObject entry && entry.ContainsKey("value") ? entry["value"] : value)?.DeepClone();
        return output;
    }

    internal static JsonNode? Resolve(JsonNode? value, JsonObject properties)
    {
        if (value is not JsonObject binding || binding["user"] is not { } user) return value?.DeepClone();
        string? name = user is JsonValue text && text.TryGetValue<string>(out string? key) ? key : user["name"]?.GetValue<string>();
        if (name is null || !properties.TryGetPropertyValue(name, out var selected)) return binding["value"]?.DeepClone();
        if (user is JsonObject condition && condition.ContainsKey("condition"))
        {
            static string Scalar(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out string? s) ? s : node?.ToJsonString() ?? "null";
            return JsonValue.Create(Scalar(selected) == Scalar(condition["condition"]));
        }
        return selected?.DeepClone();
    }

    /// <summary>
    /// 按当前源码与运行时证据重新求解 plan 的循环，覆盖 plan["loop"]：bake 不接受计划里存着的周期与起点。
    /// （原先住在 HybridCostProbe 里，成本探针删掉后搬到这里；它本来就只调 planner 与 HybridLoopService。）
    /// 走 <see cref="AnalyzeLoopForProfile"/> 这一个入口，与 analyze、布局降级重算同一口径——
    /// 否则质量档的双上限取优只在 analyze 侧生效，bake 会按另一个上限重算出别的循环。
    /// </summary>
    /// <summary>
    /// 效果前缀回退只救"没有与输入无关的可烘组"这一种拒因（含通用形态）；blockers 里还有别的拒因时不试前缀。
    /// </summary>
    internal static bool PrefixSafetyBlocked(JsonArray blockers) => PlanBlockers.Codes(blockers)
        .Any(code => code is not (BlockerCode.NoInputIndependentGroup or BlockerCode.NoInputIndependentGroupGeneric));

    internal static void RefreshLoop(JsonObject plan, ProjectSource source, JsonObject runtime, HybridAnalyzeRequest settings)
    {
        // 每次求解都重新读一份场景：质量档要在两个上限下各求一次，求解会往场景副本上写，不能共用同一份。
        JsonObject LoopScene()
        {
            var scene = source.ReadJson(source.SceneResource);
            ApplyAudioEffectChoice(scene, plan);
            FreezeTemporalProperties(scene, plan["snapshot_properties"]!.AsObject());
            DaytimeSplit.ApplyState(scene, plan, forAnalysis: true);
            return scene;
        }
        plan["loop"] = AnalyzeLoopForProfile(LoopScene, source, settings.Assets, runtime,
            plan["video_groups"]!.AsArray().OfType<JsonObject>().SelectMany(g => g["layer_ids"]!.AsArray().Select(n => n!.GetValue<int>())).ToArray(),
            settings, plan["projection"] as JsonObject ?? new JsonObject(), plan["video_groups"] as JsonArray);
        AnnotateLoopCandidates(plan["loop"]!.AsObject());
        // bake 侧重算的 loop 直接进 bake.json 的 plan 副本，不再 Attach，临时字段当场去掉。
        PlanNarrative.StripTransient(plan["loop"]);
    }

    /// <summary>Returns a plan clone with one selected foreground suffix retained as whole live roots.</summary>
    internal static JsonObject ApplyAllocation(JsonObject plan, IReadOnlyCollection<int> extraLiveRoots,
        JsonArray runtimeDependencies)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(extraLiveRoots);
        ArgumentNullException.ThrowIfNull(runtimeDependencies);
        HybridPlanFormat.Validate(plan);
        var result = plan.DeepClone().AsObject();
        FullFrameDemotion.ResetAdmissionRecords(result);
        var layers = result["layers"]?.AsArray().OfType<JsonObject>().ToArray()
            ?? throw new InvalidDataException("Hybrid plan layers are missing.");
        var layerRoot = new Dictionary<int, int>();
        var rootLayers = new Dictionary<int, List<int>>();
        foreach (var layer in layers)
        {
            int id = Int(layer["id"]) ?? throw new InvalidDataException("Hybrid plan layer id is invalid.");
            int root = Int(layer["allocation_root"] ?? layer["root"]) ?? throw new InvalidDataException("Hybrid plan layer root is invalid.");
            if (!layerRoot.TryAdd(id, root)) throw new InvalidDataException("Hybrid plan contains duplicate layer ids.");
            if (!rootLayers.TryGetValue(root, out var ids)) rootLayers[root] = ids = [];
            ids.Add(id);
        }
        var omittedLayerIds = result["omitted_snapshot_layer_ids"]?.AsArray()
            .Select(node => node?.GetValue<int>() ?? throw new InvalidDataException("Omitted snapshot layer id is invalid."))
            .ToHashSet() ?? [];
        if (omittedLayerIds.Any(id => !layerRoot.ContainsKey(id)))
            throw new InvalidDataException("Omitted snapshot layer is missing from the plan layers.");
        int[] rootOrder = result["root_order"] is JsonArray recordedOrder
            ? recordedOrder.Select(node => node?.GetValue<int>() ?? throw new InvalidDataException("Hybrid root order is invalid.")).ToArray()
            : layers.Where(layer => Int(layer["id"]) == Int(layer["allocation_root"] ?? layer["root"]))
                .Select(layer => layer["id"]!.GetValue<int>()).ToArray();
        if (rootOrder.Distinct().Count() != rootOrder.Length || rootOrder.Any(root => !rootLayers.ContainsKey(root)) ||
            rootLayers.Keys.Any(root => !rootOrder.Contains(root)))
            throw new InvalidDataException("Hybrid root order does not match the plan layers.");
        var rootIndex = rootOrder.Select((root, index) => (root, index)).ToDictionary(item => item.root, item => item.index);
        var groups = result["video_groups"]?.AsArray().OfType<JsonObject>().ToArray()
            ?? throw new InvalidDataException("Hybrid video groups are missing.");
        var groupRoots = new Dictionary<string, int[]>();
        var videoRoots = new HashSet<int>();
        foreach (var group in groups)
        {
            string id = group["id"]?.GetValue<string>() ?? throw new InvalidDataException("Hybrid video group id is missing.");
            int[] roots = group["root_ids"]?.AsArray().Select(node => node?.GetValue<int>()
                ?? throw new InvalidDataException("Hybrid video group root is invalid.")).ToArray()
                ?? throw new InvalidDataException("Hybrid video group roots are missing.");
            if (!groupRoots.TryAdd(id, roots) || roots.Any(root => !rootIndex.ContainsKey(root) || !videoRoots.Add(root)))
                throw new InvalidDataException("Hybrid video group roots are missing, duplicated, or unknown.");
        }
        var optional = result["optional_realtime_roots"]?.AsArray().Select(node => node?.GetValue<int>()
            ?? throw new InvalidDataException("Optional realtime root is invalid.")).ToHashSet() ?? [];
        int[] requested = extraLiveRoots.Distinct().ToArray();
        if (requested.Length != extraLiveRoots.Count || requested.Any(root => !optional.Contains(root) || !videoRoots.Contains(root)))
            throw new InvalidDataException("Foreground allocation roots must be distinct optional realtime video roots.");

        var existingLiveRoots = layers.Where(layer => layer["live"]?.GetValue<bool>() == true)
            .Select(layer => layerRoot[layer["id"]!.GetValue<int>()]).ToHashSet();
        if (result["composition"] is not JsonArray originalComposition)
            throw new InvalidDataException("Hybrid composition is missing.");
        foreach (var entry in originalComposition.OfType<JsonObject>())
            if (Int(entry["live_root"]) is int root)
            {
                if (!rootIndex.ContainsKey(root)) throw new InvalidDataException("Hybrid composition contains an unknown live root.");
                existingLiveRoots.Add(root);
            }
        int? suffixStart = requested.Length == 0 ? null : requested.Min(root => rootIndex[root]);
        var suffixRoots = suffixStart is int start
            ? rootOrder.Skip(start).Where(videoRoots.Contains).ToHashSet()
            : [];
        var liveRoots = existingLiveRoots.Concat(suffixRoots).ToHashSet();
        var dependencyRoots = new HashSet<int>();
        bool Promote(int layerId)
        {
            if (!layerRoot.TryGetValue(layerId, out int root) || !liveRoots.Add(root)) return false;
            dependencyRoots.Add(root);
            return true;
        }
        bool changed;
        do
        {
            changed = false;
            foreach (var dependency in runtimeDependencies.OfType<JsonObject>())
            {
                if (Int(dependency["owner"]) is not int owner || Int(dependency["target"]) is not int target) continue;
                string? operation = dependency["operation"]?.GetValue<string>();
                bool initialization = dependency["initialization"]?.GetValue<bool>() == true;
                bool ownerLive = layerRoot.TryGetValue(owner, out int ownerRoot) && liveRoots.Contains(ownerRoot);
                bool targetLive = layerRoot.TryGetValue(target, out int targetRoot) && liveRoots.Contains(targetRoot);
                if (operation == "write" && ownerLive) changed |= Promote(target);
                if (operation == "read" && !initialization && targetLive) changed |= Promote(owner);
                if (operation == "read" && ownerLive && dependency["property"]?.GetValue<string>() is
                    "boneTransform" or "animation" or "effect" or "videoTexture" or "textureAnimation" or "layerComposite") changed |= Promote(target);
            }
        } while (changed);

        var allocation = new JsonObject {
            ["status"] = "applied", ["requested_live_root_ids"] = JsonSerializer.SerializeToNode(requested),
            ["suffix_start_root_id"] = suffixStart is int suffixIndex ? JsonValue.Create(rootOrder[suffixIndex]) : null,
            ["foreground_live_root_ids"] = JsonSerializer.SerializeToNode(rootOrder.Where(suffixRoots.Contains)),
            ["dependency_live_root_ids"] = JsonSerializer.SerializeToNode(rootOrder.Where(dependencyRoots.Contains)),
            ["scope"] = "Whole allocation subtrees only; author parents, existing group order, parallax depths and scene-clear ownership are preserved."
        };
        result["allocation"] = allocation;
        JsonObject RejectAllocation(Blocker blocker, string status = "requires_user_choice")
        {
            string reason = blocker.Text;
            allocation["status"] = "requires_resolution";
            allocation["reason"] = reason;
            var blockers = result["blockers"] as JsonArray ?? new JsonArray();
            result["blockers"] = blockers;
            PlanBlockers.Add(blockers, blocker);
            result["status"] = "requires_resolution";
            result["video_layout_admission"] = new JsonObject { ["requested"] = PlanSettings.Of(result).VideoLayout,
                ["status"] = status, ["reason"] = reason,
                ["scope"] = "The allocation did not reorder roots, split a video group or change parallax settings." };
            return result;
        }
        foreach (var group in groups)
        {
            bool removed = false;
            foreach (int root in groupRoots[group["id"]!.GetValue<string>()])
            {
                if (liveRoots.Contains(root)) removed = true;
                else if (removed)
                    return RejectAllocation(new Blocker(BlockerCode.ForegroundSplitsVideoGroup));
            }
        }
        if (dependencyRoots.Any(root => !videoRoots.Contains(root) && !existingLiveRoots.Contains(root)))
            return RejectAllocation(new Blocker(BlockerCode.ForegroundOutsideComposition));

        var remainingGroups = new HashSet<string>(StringComparer.Ordinal);
        var promotedByGroup = new Dictionary<string, int[]>(StringComparer.Ordinal);
        var rebuiltGroups = new JsonArray();
        foreach (var group in groups)
        {
            string id = group["id"]!.GetValue<string>();
            int[] promoted = groupRoots[id].Where(liveRoots.Contains).ToArray();
            int[] remaining = groupRoots[id].Where(root => !liveRoots.Contains(root)).ToArray();
            promotedByGroup[id] = promoted;
            if (remaining.Length == 0) continue;
            var rebuilt = group.DeepClone().AsObject();
            rebuilt["root_ids"] = JsonSerializer.SerializeToNode(remaining);
            rebuilt["layer_ids"] = new JsonArray(group["layer_ids"]!.AsArray()
                .Where(node => !omittedLayerIds.Contains(node!.GetValue<int>()) &&
                    !liveRoots.Contains(layerRoot[node!.GetValue<int>()])).Select(node => node!.DeepClone()).ToArray());
            rebuiltGroups.Add(rebuilt);
            remainingGroups.Add(id);
        }
        result["video_groups"] = rebuiltGroups;
        var rebuiltComposition = new JsonArray();
        var emittedLive = new HashSet<int>();
        foreach (var entry in originalComposition.OfType<JsonObject>())
        {
            if (entry["video_group"] is JsonValue groupValue)
            {
                string id = groupValue.GetValue<string>();
                if (!groupRoots.ContainsKey(id)) throw new InvalidDataException("Hybrid composition references an unknown video group.");
                if (remainingGroups.Contains(id)) rebuiltComposition.Add(entry.DeepClone());
                foreach (int root in promotedByGroup[id])
                    if (emittedLive.Add(root)) rebuiltComposition.Add(new JsonObject { ["live_root"] = root });
            }
            else if (Int(entry["live_root"]) is int root && emittedLive.Add(root)) rebuiltComposition.Add(entry.DeepClone());
        }
        result["composition"] = rebuiltComposition;
        result["live_layer_ids"] = JsonSerializer.SerializeToNode(layers.Select(layer => layer["id"]!.GetValue<int>())
            .Where(id => !omittedLayerIds.Contains(id) && liveRoots.Contains(layerRoot[id])));
        foreach (var layer in layers)
        {
            int id = layer["id"]!.GetValue<int>();
            if (omittedLayerIds.Contains(id)) { layer["live"] = false; continue; }
            int root = layerRoot[id];
            if (!liveRoots.Contains(root)) continue;
            layer["live"] = true;
            if (layer["allocation"] is not null) layer["allocation"] = "live";
            var reasons = layer["reasons"] as JsonArray ?? new JsonArray();
            layer["reasons"] = reasons;
            string reason = dependencyRoots.Contains(root) ? "required_by_foreground_dependency" :
                suffixRoots.Contains(root) ? "retained_as_foreground_suffix" : "";
            if (reason.Length > 0 && !reasons.Any(node => node?.GetValue<string>() == reason)) reasons.Add(reason);
        }
        var groupOfRoot = rebuiltGroups.OfType<JsonObject>().SelectMany(group => group["root_ids"]!.AsArray()
            .Select(root => (Root: root!.GetValue<int>(), Group: group["id"]!.GetValue<string>())))
            .ToDictionary(item => item.Root, item => item.Group);
        var previousRoles = result["root_roles"]?.AsArray().OfType<JsonObject>()
            .Where(role => Int(role["root_id"]) is int).ToDictionary(role => role["root_id"]!.GetValue<int>()) ?? [];
        result["root_order"] = JsonSerializer.SerializeToNode(rootOrder);
        result["root_roles"] = new JsonArray(rootOrder.Select((root, index) => {
            var role = previousRoles.GetValueOrDefault(root)?.DeepClone().AsObject() ?? new JsonObject();
            role["root_id"] = root; role["source_order"] = index;
            role["role"] = dependencyRoots.Contains(root) ? "dependency_live" : suffixRoots.Contains(root) ? "foreground_live" :
                existingLiveRoots.Contains(root) ? "live" : groupOfRoot.ContainsKey(root) ? "video" :
                role["role"]?.GetValue<string>() == "omitted_snapshot" ? "omitted_snapshot" : "inactive";
            role["video_group"] = groupOfRoot.TryGetValue(root, out string? group) ? group : null;
            return (JsonNode)role;
        }).ToArray());

        Blocker? layoutConflict = FullFrameConflict(result) ?? CompositionHierarchyConflict(result);
        if (layoutConflict is not null) return RejectAllocation(layoutConflict, LayoutConflictStatus(result));
        result["video_layout_admission"] = new JsonObject { ["requested"] = PlanSettings.Of(result).VideoLayout,
            ["status"] = "planned_layout_allowed", ["reason"] = null,
            ["scope"] = "Layout permission is not proof of image correctness, looping, hardware decoding or playback benefit." };
        return result;
    }

    // full_frame 的唯一视频组排在搬不走的实时绘制之后时，这不是用户能选出来的布局，而是不可达。
    private static string LayoutConflictStatus(JsonObject plan) =>
        SingleShotAllocation.UnreachableBlockingRoots(plan).Length > 0 ? "full_frame_unreachable" : "requires_user_choice";

    internal static Blocker? FullFrameConflict(JsonObject plan)
    {
        string layout = PlanSettings.Of(plan).VideoLayout;
        if (layout == "layered") return null;
        if (layout != "full_frame") throw new InvalidDataException("Unknown video layout; use full_frame or layered.");
        JsonArray groups = plan["video_groups"]?.AsArray() ?? throw new InvalidDataException("Video groups are missing.");
        if (groups.Count == 1 && groups[0]?["include_scene_clear"]?.GetValue<bool>() == true) return null;
        // 唯一一组被不可搬动的实时绘制挡在后面时，这个布局对该场景不可达：报出阻挡者，不提建议。
        if (SingleShotAllocation.UnreachableBlockingRoots(plan) is { Length: > 0 } blocking)
            return SingleShotAllocation.UnreachableReason(plan, blocking);
        var composition = (plan["composition"]?.AsArray() ?? []).OfType<JsonObject>().ToArray();
        string[] LiveNames(IEnumerable<JsonObject> entries) => entries
            .Where(item => item["live_root"] is not null).Select(item => item["live_root"]!.GetValue<int>())
            .SelectMany(id => plan["layers"]?.AsArray().OfType<JsonObject>().Where(layer => Int(layer["allocation_root"] ?? layer["root"]) == id &&
                layer["visible"] is JsonValue visible && visible.TryGetValue<bool>(out bool shown) && shown &&
                layer["drawable"] is JsonValue drawable && drawable.TryGetValue<bool>(out bool draws) && draws)
                .Select(layer => layer["name"]?.GetValue<string>()) ?? [])
            .Where(name => !string.IsNullOrWhiteSpace(name)).Cast<string>().Distinct().ToArray();
        string[] names = LiveNames(composition
            .SkipWhile(item => item["video_group"] is null).Reverse().SkipWhile(item => item["video_group"] is null).Reverse());
        // 排在第一组视频之前先画的可见实时层：只进文案，用来点名"挡在前面"的是谁。
        string[] leading = composition.Any(item => item["video_group"] is not null)
            ? LiveNames(composition.TakeWhile(item => item["video_group"] is null)) : [];
        // 文案两边意图取并集：分类与双语来自文案表，本场景真正可执行的选项来自全幅降级的取证。
        return PlanNarrative.FullFrameConflict(groups, names, FullFrameDemotion.ConflictOptions(plan), leading);
    }

    internal static Blocker? CompositionHierarchyConflict(JsonObject plan,
        IReadOnlyDictionary<int, JsonObject>? sourceObjects = null, JsonArray? dependencies = null)
    {
        HybridPlanFormat.Validate(plan);
        JsonArray layers = plan["layers"]!.AsArray();
        var objects = sourceObjects ?? layers.OfType<JsonObject>().ToDictionary(Id, layer => new JsonObject {
            ["id"] = layer["id"]!.DeepClone(), ["parent"] = layer["parent"]?.DeepClone() });
        int nextId = checked(objects.Keys.Max() + 1);
        var replacements = plan["video_groups"]!.AsArray().OfType<JsonObject>().ToDictionary(
            group => group["id"]!.GetValue<string>(), group => new JsonObject { ["id"] = nextId++, ["parent"] = group["parent_id"]?.DeepClone() });
        try { _ = HybridBakeService.AssembleAllocationObjects(objects, plan, replacements, dependencies ?? new JsonArray()); return null; }
        catch (InvalidDataException error) when (Blocker.Of(error) is Blocker blocker) { return blocker; }
    }

    internal static void FreezeTemporalProperties(JsonObject scene, JsonObject properties)
    {
        void Freeze(JsonObject container, string key)
        {
            if (container[key] is JsonObject binding && binding.ContainsKey("user") &&
                !binding.ContainsKey("script") && !binding.ContainsKey("animation") && !binding.ContainsKey("animations"))
                container[key] = Resolve(binding, properties);
        }
        foreach (var obj in scene["objects"]!.AsArray().OfType<JsonObject>())
        {
            foreach (var effect in obj["effects"]?.AsArray().OfType<JsonObject>() ?? [])
            {
                Freeze(effect, "visible");
                foreach (var pass in effect["passes"]?.AsArray().OfType<JsonObject>() ?? [])
                    foreach (string field in new[] { "constantshadervalues", "combos" })
                        if (pass[field] is JsonObject values)
                            foreach (string key in values.Select(pair => pair.Key).ToArray()) Freeze(values, key);
            }
            foreach (var clip in obj["animationlayers"]?.AsArray().OfType<JsonObject>() ?? []) Freeze(clip, "rate");
        }
    }

    internal static string CapabilityScanText(string code)
    {
        // Keep template expressions conservative. Quotes and regex literals must protect embedded
        // comment markers, otherwise a URL can hide the remaining live/shared API calls on its line.
        if (code.Contains('`')) return code;
        return Regex.Replace(code,
            "\"(?:\\\\.|[^\"\\\\])*\"|'(?:\\\\.|[^'\\\\])*'|(?<comment>/\\*[\\s\\S]*?\\*/|//[^\\r\\n]*)|/(?:\\\\.|[^/\\\\\\r\\n])+/[a-z]*",
            match => match.Groups["comment"].Success ? "" : match.Value);
    }

    internal static void ApplySnapshotOmissions(JsonObject scene, JsonObject plan)
    {
        if (plan["omitted_snapshot_layer_ids"] is not JsonArray { Count: > 0 } omittedIds) return;
        var omitted = omittedIds.Select(n => n!.GetValue<int>()).ToHashSet();
        var objects = scene["objects"]!.AsArray();
        for (int i = objects.Count - 1; i >= 0; --i)
            if (omitted.Contains(objects[i]!["id"]!.GetValue<int>())) objects.RemoveAt(i);
    }

    internal static void ApplyOverlayPlacement(JsonObject scene, JsonObject plan)
    {
        if (plan["occlusion_tradeoff"]?["status"]?.GetValue<string>() != "applied") return;
        var promoted = plan["occlusion_tradeoff"]!["promoted_roots"]!.AsArray()
            .Select(n => n!["root_id"]!.GetValue<int>()).ToHashSet();
        var objects = scene["objects"]!.AsArray().OfType<JsonObject>().ToArray();
        var byId = objects.ToDictionary(Id);
        var layers = plan["layers"]!.AsArray().OfType<JsonObject>().ToArray();
        bool Within(int id, int ancestor)
        {
            while (byId.TryGetValue(id, out var obj))
            {
                if (id == ancestor) return true;
                if (Int(obj["parent"]) is not int parent || !byId.ContainsKey(parent)) return false;
                id = parent;
            }
            return false;
        }
        // Lift a promoted child declaration to its wholly promoted structural branch. Merely moving
        // the child to the end of the JSON cannot cross a sibling of its ancestor in the native tree.
        var placement = new HashSet<int>();
        foreach (int selected in promoted)
        {
            int id = selected;
            while (Int(byId[id]["parent"]) is int parent && byId.ContainsKey(parent) &&
                layers.Where(layer => Within(Id(layer), parent) && layer["drawable"]?.GetValue<bool>() == true)
                    .All(layer => promoted.Contains(Int(layer["allocation_root"] ?? layer["root"])!.Value))) id = parent;
            placement.Add(id);
        }
        scene["objects"] = new JsonArray(objects.Where(o => !placement.Contains(Id(o)))
            .Concat(objects.Where(o => placement.Contains(Id(o)))).Select(o => (JsonNode)o.DeepClone()).ToArray());
    }

    internal static void ApplyTextEffectChoice(JsonObject scene, JsonObject plan)
    {
        if (plan["text_effects_choice"]?["status"]?.GetValue<string>() != "applied") return;
        var selected = plan["text_effects_choice"]!["simplified_layers"]!.AsArray().Select(n => n!["id"]!.GetValue<int>()).ToHashSet();
        foreach (var obj in scene["objects"]!.AsArray().OfType<JsonObject>().Where(obj => selected.Contains(Id(obj))))
        {
            if (!obj.ContainsKey("text")) throw new InvalidDataException("Text-effect choice refers to a non-text layer.");
            obj["effects"] = new JsonArray();
        }
    }

    internal static JsonObject DescribeAudioEffectChoice(JsonObject scene, ProjectSource source, string? assets,
        JsonObject properties, JsonObject runtime, string selection)
    {
        if (selection is not "preserve" and not "omit") throw new InvalidDataException("Unknown audio-effect choice.");
        var available = new JsonArray(); var protectedEffects = new JsonArray();
        var objects = scene["objects"]!.AsArray().OfType<JsonObject>().ToArray();
        bool scriptAccess = objects.SelectMany(obj => SceneAnalyzer.Walk(obj).OfType<JsonObject>())
            .Where(n => n["script"] is JsonValue).Select(n => CapabilityScanText(n["script"]!.GetValue<string>()))
            .Any(code => Regex.IsMatch(code, @"\b(getEffect|getEffects|findEffect|eval|Function|Reflect|Proxy|import)\b|\.\s*effects\b|\[\s*['""]effects['""]\s*\]"));
        var dependencies = runtime["runtime_dependencies"]!.AsArray().OfType<JsonObject>().ToArray();
        foreach (var obj in objects)
        {
            int id = Id(obj);
            var observed = runtime["runtime_layers"]!.AsArray().OfType<JsonObject>().Where(n => Int(n["owner"]) == id).ToArray();
            var audio = observed.SelectMany(n => n["materials"]?.AsArray().OfType<JsonObject>() ?? [])
                .Where(m => m["uses_audio_spectrum"]?.GetValue<bool>() == true).ToArray();
            if (audio.Length == 0) continue;
            JsonObject Entry(int index, JsonObject? effect, string? reason = null) => new() {
                ["layer_id"] = id, ["layer_name"] = obj["name"]?.DeepClone() ?? JsonValue.Create("Layer"),
                ["effect_index"] = index, ["effect_id"] = effect?["id"]?.DeepClone(), ["file"] = effect?["file"]?.DeepClone(),
                ["name"] = effect?["name"] is JsonValue name && name.TryGetValue<string>(out string? text) && !string.IsNullOrWhiteSpace(text)
                    ? text : effect?["file"]?.GetValue<string>() ?? "Source audio material",
                ["reason"] = reason };
            if (audio.Any(m => m["role"]?.GetValue<string>() != "effect"))
                protectedEffects.Add(Entry(-1, null, "intrinsic_audio_material"));
            if (!audio.Any(m => m["role"]?.GetValue<string>() == "effect")) continue;
            var effects = obj["effects"]?.AsArray().OfType<JsonObject>().ToArray() ?? [];
            var materials = observed.SelectMany(n => n["materials"]?.AsArray().OfType<JsonObject>() ?? [])
                .Where(m => m["role"]?.GetValue<string>() == "effect").ToArray();
            bool mapped = observed.Length == 1 && effects.Length > 0 && effects.Length == materials.Length;
            // ponytail: map only fixed-visible single-pass chains. Stable native effect identities
            // are needed before supporting hidden, multipass or expanded render-node chains.
            for (int index = 0; mapped && index < effects.Length; ++index)
            {
                var effect = effects[index];
                if (effect["visible"] is JsonObject visibility && SceneAnalyzer.Walk(visibility).OfType<JsonObject>()
                        .Any(n => n.ContainsKey("script") || n.ContainsKey("animation") || n.ContainsKey("animations")) ||
                    Resolve(effect["visible"], properties)?.ToJsonString() is string shown && shown != "true" ||
                    effect["passes"] is JsonArray { Count: > 1 })
                { mapped = false; break; }
                try
                {
                    string file = effect["file"]?.GetValue<string>() ?? "";
                    JsonObject definition = SceneAnalyzer.ReadResourceJson(source, assets, file);
                    if (definition["passes"] is not JsonArray { Count: 1 } passes || passes[0]?["material"] is not JsonValue materialFile)
                    { mapped = false; break; }
                    JsonObject material = SceneAnalyzer.ReadResourceJson(source, assets, materialFile.GetValue<string>());
                    mapped = material["passes"] is JsonArray { Count: 1 } materialPasses &&
                        materialPasses[0]?["shader"]?.GetValue<string>() == materials[index]["shader"]?.GetValue<string>();
                }
                catch (Exception error) when (error is IOException or InvalidDataException or JsonException)
                { mapped = false; }
            }
            if (!mapped)
            { protectedEffects.Add(Entry(-1, null, "audio_effect_mapping_unavailable")); continue; }
            for (int index = 0; index < effects.Length; ++index)
            {
                if (materials[index]["uses_audio_spectrum"]?.GetValue<bool>() != true) continue;
                var effect = effects[index];
                string? reason = scriptAccess || dependencies.Any(d => Int(d["target"]) == id && d["property"]?.GetValue<string>() == "effect")
                    ? "script_accessed_effect" : SceneAnalyzer.Walk(effect).OfType<JsonObject>()
                        .Any(n => n.ContainsKey("script") || n.ContainsKey("animation") || n.ContainsKey("animations"))
                    ? "scripted_or_animated_effect" : materials[index]["active_uniforms"] is JsonArray uniforms &&
                        uniforms.Any(n => n?.GetValue<string>() is "g_PointerPosition" or "g_PointerPositionLast" or "g_ParallaxPosition")
                    ? "other_live_effect_input" : materials[index]["textures"] is JsonArray textures &&
                        textures.Any(n => n?.GetValue<string>() is "_rt_default" or "_rt_FullFrameBuffer")
                    ? "framebuffer_effect" : null;
                if (reason is not null) protectedEffects.Add(Entry(index, effect, reason));
                else available.Add(Entry(index, effect));
            }
        }
        return new JsonObject {
            ["selection"] = selection,
            ["status"] = selection == "omit" && available.Count > 0 ? "applied" : available.Count > 0 ? "available" :
                protectedEffects.Count > 0 ? "protected" : "not_needed",
            ["scope"] = "Explicit appearance tradeoff: remove only the listed fixed single-pass audio effects. Their lighting or other appearance changes no longer respond to music. Other effects, audio scripts, intrinsic audio materials, clocks and pointer interaction remain intact.",
            ["available_effects"] = available, ["omitted_effects"] = selection == "omit" ? available.DeepClone() : new JsonArray(),
            ["protected_effects"] = protectedEffects };
    }

    internal static void ApplyAudioEffectChoice(JsonObject scene, JsonObject plan)
    {
        if (plan["audio_effects_choice"]?["status"]?.GetValue<string>() != "applied") return;
        var objects = scene["objects"]!.AsArray().OfType<JsonObject>().ToDictionary(Id);
        foreach (var layer in plan["audio_effects_choice"]!["omitted_effects"]!.AsArray().OfType<JsonObject>()
            .GroupBy(item => item["layer_id"]!.GetValue<int>()))
        {
            if (!objects.TryGetValue(layer.Key, out var obj) || obj["effects"] is not JsonArray effects)
                throw new InvalidDataException("Audio-effect choice refers to a missing effect layer.");
            foreach (var item in layer.OrderByDescending(item => item["effect_index"]!.GetValue<int>()))
            {
                int index = item["effect_index"]!.GetValue<int>();
                if (index < 0 || index >= effects.Count || effects[index] is not JsonObject effect ||
                    !JsonNode.DeepEquals(effect["id"], item["effect_id"]) || !JsonNode.DeepEquals(effect["file"], item["file"]))
                    throw new InvalidDataException("Audio-effect choice no longer matches its source effect.");
                effects.RemoveAt(index);
            }
        }
    }

}
