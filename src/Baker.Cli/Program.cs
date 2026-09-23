using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using Baker.Core;

var jsonOptions = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
async Task<NativeTools> ReadTools(string path, CancellationToken token)
{
    var value = JsonSerializer.Deserialize<NativeTools>(await File.ReadAllTextAsync(path, token), jsonOptions)
        ?? throw new InvalidDataException("Invalid native tools configuration.");
    string directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
    string Resolve(string file) => Path.GetFullPath(file, directory);
    return new(Resolve(value.Renderer), Resolve(value.Ffmpeg), Resolve(value.Ffprobe), value.RuntimeDirectories.Select(Resolve).ToArray());
}
// 界面语言：--lang 覆盖，默认跟随系统（zh-* → zh）。只影响文案，不影响任何判定。
string language = MessageCatalog.DefaultLanguage();
// 结论那一行是中文时，重定向到文件或管道必须是 UTF-8 无 BOM；直连控制台则保持控制台自己的代码页，
// 否则 936 控制台会把 UTF-8 字节显示成乱码。stdout 的 JSON 始终是转义过的 ASCII，不受影响。
if (Console.IsErrorRedirected || Console.IsOutputRedirected)
    Console.OutputEncoding = new System.Text.UTF8Encoding(false);
// 上一次分析留在 %TEMP%\WpeBaker 里的工作目录没人删过（中途取消的尤其），启动时顺手清掉过期的。
NativeEnvironment.PruneAnalysisScratch();
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
try
{
    // 每个子命令一段用法：顶层帮助由它们拼出，`<命令> --help` 只打印自己那一段，两处永远同步。
    // 骨架是英文；个别说明（如 bake 的 --encoder）按 --lang 出中文或英文，帮助路径里 --lang 放在哪都认。
    var usage = CliUsage.Sections(CliUsage.HelpLanguage(args, language));
    string[] primaryCommands = ["analyze", "bake", "export", "targets", "apply", "rollback"];
    string[] diagnosticCommands = ["inspect", "extract", "render", "validate",
        "decode-check", "devices", "pack-video", "pack-rgba"];
    // 子命令带 --help/-h 时只讲这个子命令，并以 0 退出；以前 --help 被当成壁纸路径，报"源不存在"。
    if (args.Length > 1 && usage.ContainsKey(args[0]) && args.Skip(1).Any(value => value is "--help" or "-h"))
    {
        Console.WriteLine(usage[args[0]]);
        Console.WriteLine($"Run `wpe-baker --help` for every command. Paths are relative to the working directory; tool paths to TOOLS.json.");
        return 0;
    }
    if (args.Length == 0 || args[0] is "--help" or "-h" or "help")
    {
        Console.WriteLine(string.Join(Environment.NewLine, primaryCommands.Select(name => usage[name])));
        Console.WriteLine();
        Console.WriteLine("Diagnostics:");
        Console.WriteLine(string.Join(Environment.NewLine, diagnosticCommands.Select(name => usage[name])));
        Console.WriteLine();
        Console.WriteLine("""
            Scene projects only. Analyze creates a version 3 video-plus-live plan.
            Analysis never judges whether baking is worth it; it records the analysis device and
            plans. Cost-probe stays available as an optional diagnostic measurement only.
            Analysis also considers independently periodic effect prefixes when whole-layer
            replacement is unsuitable; later effects remain live on their original objects.
            The plan lists every layer with its allocation, placement and visibility binding;
            nothing is excluded without --exclude-layers.
            Excluded layers and their subtrees are omitted exactly as if switched off in the
            source; they enter neither the video, the live scene nor a static texture.
            Live layers also carry a tradeoff class (tradeoff/subject/technical/derived), the kind
            of element they are and the wallpaper property their visibility is bound to, and the
            plan's tradeoff_options lists up to three ways to turn optional live elements off for
            a full-frame video: what disappears with them and how much stays live.
            Bake accepts a saved plan with --out, or a request containing its output directory.
            Legacy effect-cache and Video/Web compression plans are no longer supported.
            Apply and rollback affect only the location explicitly recorded in the request.
            Request paths are relative to the working directory; tool paths to TOOLS.json.
            """);
        return 0;
    }
    if (args[0] == "devices")
    {
        if (args.Length != 1) throw new ArgumentException("devices takes no arguments.");
        Console.WriteLine(JsonSerializer.Serialize(VulkanDevices.Enumerate(), jsonOptions));
        return 0;
    }
    var options = new Dictionary<string, string>(StringComparer.Ordinal);
    for (int i = 2; i < args.Length; i += 2)
    {
        if (i + 1 >= args.Length || !args[i].StartsWith("--", StringComparison.Ordinal) || !options.TryAdd(args[i], args[i + 1]))
            throw new ArgumentException("Options require a unique name and value.");
    }
    if (args.Length < 2) throw new ArgumentException("Source path is required.");
    string[] allowed = args[0] switch {
        "analyze" => ["--assets", "--out", "--tools", "--properties", "--properties-source", "--width", "--height", "--fps", "--fps-den", "--video-layout", "--live-overlays", "--text-effects", "--audio-effects", "--exclude-layers", "--preset", "--retime-budget", "--video-shell", "--sway-retime", "--loop-max-seconds", "--trace", "--retain-live", "--device", "--lang", "--daytime-split", "--interaction"],
        "inspect" => ["--assets", "--out"],
        "decode-check" => ["--tools", "--out"],
        "extract" => ["--out"], "bake" => ["--tools", "--out", "--encoder", "--encode-slots", "--group-parallel", "--keep-intermediates", "--effect-render-scale", "--effect-resolution", "--lang"], "render" or "validate" => ["--tools"],
        "targets" or "export" => [],
        "apply" or "rollback" => ["--wallpaper-engine"],
        "pack-video" or "pack-rgba" => ["--width", "--height", "--out"], _ => [] };
    foreach (var key in options.Keys)
        if (!allowed.Contains(key)) throw new ArgumentException($"Unknown option for {args[0]}: {key}");
    if (options.TryGetValue("--lang", out string? requestedLanguage))
    {
        if (requestedLanguage is not ("zh" or "en")) throw new ArgumentException("--lang must be zh or en.");
        language = requestedLanguage;
    }
    if (args[0] == "decode-check")
    {
        if (!options.TryGetValue("--out", out string? output)) throw new ArgumentException("--out is required.");
        var tools = options.TryGetValue("--tools", out string? path) ? await ReadTools(path, cancellation.Token) : NativeEnvironment.FindTools();
        Console.WriteLine((await new NativeRenderRunner(tools).ProbeHardwareDecodeAsync(args[1], output,
            cancellationToken: cancellation.Token)).ToJsonString(jsonOptions));
    }
    else if (args[0] == "analyze")
    {
        // 来源判定与 GUI 共用 SourceDiagnosis：空文件夹、随手拖进来的文件、视频/网页壁纸、预设包
        // 各有各的那一句，两处说法一致。legacy 英文写进 stderr 的 JSON，人话那一行按 --lang 出。
        if (SourceDiagnosis.Inspect(args[1], out string sourcePath) is { } rejection)
        {
            if (rejection.Kind == SourceDiagnosis.PresetKind)
            {
                Console.Error.WriteLine(JsonSerializer.Serialize(new { status = "not_applicable", kind = "preset",
                    dependency = rejection.Dependency, message = rejection.Text(MessageCatalog.English) }, jsonOptions));
                // 以前这里只吐一段英文 JSON 就退出，中文用户连一句人话都看不到。
                Console.Error.WriteLine(MessageCatalog.Get("summary.not_applicable", language, rejection.Text(language)));
                return 3;
            }
            throw rejection.Message.Error(text => new InvalidDataException(text));
        }
        using var source = new ProjectSource(sourcePath);
        NativeTools tools = options.TryGetValue("--tools", out string? toolPath) ? await ReadTools(toolPath, cancellation.Token) : NativeEnvironment.FindTools();
        string assets = options.TryGetValue("--assets", out string? suppliedAssets) ? Path.GetFullPath(suppliedAssets) : NativeEnvironment.FindAssets();
        if (!NativeEnvironment.AssetsValid(assets)) throw new Message("setup.assets_missing").Error(text => new DirectoryNotFoundException(text));
        uint Number(string option, uint fallback) => options.TryGetValue(option, out string? value) ? uint.Parse(value, System.Globalization.CultureInfo.InvariantCulture) : fallback;
        // 没给 --fps 时按 min(WPE 帧率上限, 主屏刷新率) 就近取标准档；--fps-den 是分子的配套，单独给没有意义。
        if (!options.ContainsKey("--fps") && options.ContainsKey("--fps-den"))
            throw new ArgumentException("--fps-den only applies together with --fps; omit both to take the frame rate from the Wallpaper Engine limit and the display refresh rate.");
        var frameRate = OutputFrameRate.Choose(Number("--fps", 0),
            () => WallpaperEngineProperties.ReadFrameRateLimit(WallpaperEngineProperties.LocateConfig(Path.GetDirectoryName(assets))),
            OutputFrameRate.PrimaryDisplayRefreshHz);
        // 档位给观感改动预算与长度上限兜底；--retime-budget / --loop-max-seconds 是高级覆盖。
        RetimeProfile.Arguments retimeArguments = RetimeProfile.ReadArguments(options);
        string interaction = options.GetValueOrDefault("--interaction", "fixed");
        if (interaction is not ("keep" or "fixed" or "off")) throw new ArgumentException("--interaction must be keep, fixed, or off.");
        string liveOverlayPlacement = options.GetValueOrDefault("--live-overlays", "foreground");
        if (liveOverlayPlacement is not ("preserve" or "foreground"))
            throw new ArgumentException("--live-overlays must be preserve or foreground.");
        string liveTextEffects = options.GetValueOrDefault("--text-effects", "preserve");
        if (liveTextEffects is not ("preserve" or "simple"))
            throw new ArgumentException("--text-effects must be preserve or simple.");
        string audioEffects = options.GetValueOrDefault("--audio-effects", "preserve");
        if (audioEffects is not ("preserve" or "omit"))
            throw new ArgumentException("--audio-effects must be preserve or omit.");
        // 循环取向跟着档位走。
        string loopPreference = RetimeProfile.LoopPreferenceForPreset(retimeArguments.Preset);
        string videoShell = options.GetValueOrDefault("--video-shell", VideoDominance.RejectChoice);
        if (videoShell is not (VideoDominance.RejectChoice or VideoDominance.AllowChoice))
            throw new ArgumentException("--video-shell must be reject or allow.");
        // feat/daytime-split：昼夜/时段壁纸的状态拆分，默认关；开着时识别到状态选择器就每个状态各出一份 plan。
        string daytimeSplit = options.GetValueOrDefault("--daytime-split", "off");
        if (daytimeSplit is not ("on" or "off")) throw new ArgumentException("--daytime-split must be on or off.");
        // 摆动改频默认开，与 GUI 一致（解析在 RetimeProfile.ReadArguments，默认值取 SwayRetimeOptions.OnByDefault）；
        // 循环时长上限（秒）由档位兜底（效率/平衡 600、质量 1200），--loop-max-seconds 可覆盖，与改频开关无关。
        double? loopLengthMaximum = retimeArguments.LoopMaximumSeconds;
        JsonObject? explicitProperties = options.TryGetValue("--properties", out string? propertiesPath)
            ? JsonNode.Parse(await File.ReadAllTextAsync(propertiesPath, cancellation.Token))?.AsObject()
                ?? throw new InvalidDataException("--properties must contain a JSON object.") : null;
        // 属性底值默认取用户在 Wallpaper Engine 里为这张壁纸设的值（只读 config.json），--properties 再覆盖在上；
        // 读不到就回退壁纸默认值，原因写进 plan 的 wpe_properties。
        string propertiesSource = options.GetValueOrDefault("--properties-source", WallpaperEngineProperties.SourceWpe);
        if (!WallpaperEngineProperties.IsKnownMode(propertiesSource))
            throw new ArgumentException("--properties-source must be wpe or defaults.");
        JsonObject sourceProject = source.Contains("project.json") ? source.ReadJson("project.json") : new JsonObject();
        var wpeProperties = WallpaperEngineProperties.Resolve(propertiesSource, source.DirectoryPath, source.SourcePath, sourceProject,
            propertiesSource == WallpaperEngineProperties.SourceWpe ? WallpaperEngineProperties.LocateConfig(Path.GetDirectoryName(assets)) : null);
        var (properties, propertiesOrigin) = WallpaperEngineProperties.Merge(wpeProperties, sourceProject, explicitProperties);
        // 宽高要么一起给，要么都不给：都不给时由分析按场景画布铺满本机屏幕取值，0 表示未指定。
        if (options.ContainsKey("--width") != options.ContainsKey("--height"))
            throw new ArgumentException("--width and --height must be given together; omit both to size the output from the scene canvas and this machine's primary display.");
        string analysisDirectory = options.TryGetValue("--out", out var planPath)
            ? Path.GetFullPath(planPath) + ".work" : Path.Combine(Path.GetTempPath(), "WpeBaker", "analysis-" + Guid.NewGuid().ToString("N"));
        var request = new HybridAnalyzeRequest(2, sourcePath, assets, analysisDirectory,
            Number("--width", 0), Number("--height", 0), frameRate.Fps, Number("--fps-den", 1), properties,
            // 质量档不设百分比预算（取改动最小的解），通用分量调速这时仍要一个上限，沿用旧默认 2%。
            MaximumRetimePercent: 2, AllowLocalSeamRepair: false,
            RuntimeTraceFile: options.GetValueOrDefault("--trace"),
            DeviceUuid: options.GetValueOrDefault("--device"),
            RetainLiveRootIds: options.TryGetValue("--retain-live", out string? keep) ? keep.Split(',').Select(int.Parse).ToArray() : null,
            VideoLayout: options.GetValueOrDefault("--video-layout", "full_frame"),
            LiveOverlayPlacement: liveOverlayPlacement,
            LiveTextEffects: liveTextEffects,
            AudioEffects: audioEffects,
            ExcludedLayerIds: options.TryGetValue("--exclude-layers", out string? excludedLayers)
                ? excludedLayers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(int.Parse).ToArray() : null,
            LoopPreference: loopPreference,
            VideoShell: videoShell,
            SwayRetime: retimeArguments.SwayRetime,
            LoopLengthMaximumSeconds: loopLengthMaximum,
            PropertiesOrigin: propertiesOrigin,
            FrameRateOrigin: frameRate.ToJson(),
            Preset: retimeArguments.Preset,
            RetimeBudgetPercent: retimeArguments.BudgetPercent,
            DaytimeSplit: daytimeSplit == "on",
            CustomSettings: PresetCascade.IsCustom(options.Keys), Interaction: interaction,
            LayoutExplicit: options.ContainsKey("--video-layout"));
        var progress = new Progress<RenderProgress>(p => Console.Error.WriteLine(JsonSerializer.Serialize(p, jsonOptions)));
        JsonObject report;
        int analyzeExitCode = 0;
        try { report = await new HybridScenePlanner(tools).AnalyzeAsync(request, progress, cancellation.Token); }
        catch (AnalysisToolLimitationException limitation)
        {
            // 渲染器读不了作品里的素材文件：照常写出结论报告，但用单独的退出码，不和"不适用"(1) 混在一起。
            report = limitation.Report;
            analyzeExitCode = AnalysisToolLimitation.ExitCode;
            Console.Error.WriteLine(JsonSerializer.Serialize(new { status = AnalysisToolLimitation.Verdict, exit_code = analyzeExitCode,
                files = report["tool_limitation"]?["files"]?.AsArray().Select(file => file?["file"]?.GetValue<string>()).ToArray() }, jsonOptions));
        }
        // 视频外壳的判定结果在 stderr 上再说一遍中英各一句；计划里 video_dominant 有完整证据，退出码不变。
        if (report["video_dominant"] is JsonObject dominance &&
            dominance["status"]?.GetValue<string>() is VideoDominance.ShellStatus or VideoDominance.OverrideStatus)
        {
            Console.Error.WriteLine(dominance["reason_en"]?.GetValue<string>());
            Console.Error.WriteLine(dominance["reason_zh"]?.GetValue<string>());
        }
        // 状态拆分识别成功：每个状态按"只有这套图层可见"再规划一次，子 plan 落在 <out>.state-<名字>.json，
        // 母 plan 的 daytime_split.states[] 记下各状态的 plan 路径、结论 key、阻断数、实时层数与视频组数。
        if (analyzeExitCode == 0 && request.DaytimeSplit && report["daytime_split"] is JsonObject split && split["status"]?.GetValue<string>() == "recognized")
        {
            foreach (JsonObject state in split["states"]!.AsArray().OfType<JsonObject>())
            {
                string stateName = state["name"]!.GetValue<string>();
                string stateDirectory = Path.Combine(analysisDirectory, "state-" + stateName);
                JsonObject statePlan = await new HybridScenePlanner(tools).AnalyzeSingleAsync(
                    request with { OutputDirectory = stateDirectory, DaytimeState = stateName }, progress, cancellation.Token);
                // 子 plan 不经预设级联，生成准入要在这里补上，否则它会说能生成、bake 第一步才拒。
                Admission.ApplyGenerationAdmission(statePlan);
                string statePlanPath = options.TryGetValue("--out", out var planOut) ? planOut + ".state-" + stateName + ".json"
                    : Path.Combine(stateDirectory, "plan.json");
                await using (var stateFile = new FileStream(statePlanPath, FileMode.CreateNew, FileAccess.Write))
                    await JsonSerializer.SerializeAsync(stateFile, statePlan, jsonOptions, cancellation.Token);
                state["plan"] = statePlanPath;
                state["status"] = statePlan["status"]?.DeepClone();
                state["summary_key"] = statePlan["summary"]?["key"]?.DeepClone();
                state["blocker_count"] = (statePlan["blockers"] as JsonArray)?.Count;
                state["live_layer_count"] = (statePlan["live_layer_ids"] as JsonArray)?.Count;
                state["video_group_count"] = (statePlan["video_groups"] as JsonArray)?.Count;
                Console.Error.WriteLine($"[daytime-split] state {stateName}: {statePlan["summary"]?[language]?.GetValue<string>() ?? statePlan["summary"]?["en"]?.GetValue<string>()}");
            }
        }
        string text = JsonSerializer.Serialize(report, jsonOptions);
        if (options.TryGetValue("--out", out var output))
        {
            await using var file = new FileStream(output, FileMode.Create, FileAccess.Write);
            await JsonSerializer.SerializeAsync(file, report, jsonOptions, cancellation.Token);
        }
        Console.WriteLine(text);
        // 用户唯一保证会读到的一行：分析结论走 stderr，stdout 保持纯 JSON。
        if (report["summary"] is JsonObject summary)
            Console.Error.WriteLine(summary[language]?.GetValue<string>() ?? summary["en"]?.GetValue<string>() ?? "");
        // 取舍清单跟在结论后面：每个方案一行，写明关什么、连带什么、关掉后还剩多少实时。
        foreach (string option in TradeoffOptions.Lines(report, language)) Console.Error.WriteLine(option);
        if (analyzeExitCode != 0) return analyzeExitCode;
    }
    else if (args[0] == "inspect")
    {
        object report = await SceneAnalyzer.AnalyzeAsync(args[1], options.GetValueOrDefault("--assets"), cancellation.Token);
        string text = JsonSerializer.Serialize(report, jsonOptions);
        if (options.TryGetValue("--out", out var output))
        {
            await using var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write);
            await JsonSerializer.SerializeAsync(file, report, jsonOptions, cancellation.Token);
        }
        Console.WriteLine(text);
    }
    else if (args[0] == "extract")
    {
        if (!options.TryGetValue("--out", out var output)) throw new ArgumentException("--out is required.");
        using var source = new ProjectSource(args[1]);
        string before = await source.SourceHashAsync(cancellation.Token);
        await source.ExtractAsync(output, cancellation.Token);
        string after = await source.SourceHashAsync(cancellation.Token);
        if (before != after) throw new IOException("Source changed during extraction; output is not validated.");
        Console.WriteLine(JsonSerializer.Serialize(new { status = "extracted", source_sha256 = before, output = Path.GetFullPath(output) }, jsonOptions));
    }
    else if (args[0] == "render")
    {
        if (!options.TryGetValue("--tools", out string? toolsPath)) throw new ArgumentException("--tools is required.");
        var request = JsonSerializer.Deserialize<RenderRequest>(await File.ReadAllTextAsync(args[1], cancellation.Token), jsonOptions)
            ?? throw new InvalidDataException("Invalid render request.");
        var tools = await ReadTools(toolsPath, cancellation.Token);
        var progress = new Progress<RenderProgress>(p => Console.Error.WriteLine(JsonSerializer.Serialize(p, jsonOptions)));
        var result = await new NativeRenderRunner(tools).RenderAsync(request, progress, cancellation.Token);
        Console.WriteLine(result.ToJsonString(jsonOptions));
    }
    else if (args[0] == "bake")
    {
        string text = await File.ReadAllTextAsync(args[1], cancellation.Token);
        JsonObject input = JsonNode.Parse(text)?.AsObject() ?? throw new InvalidDataException("Invalid bake plan or request.");
        if (input["kind"] is JsonValue inputKind && inputKind.TryGetValue(out string? kindText) && kindText == AnalysisToolLimitation.Kind)
            throw new InvalidDataException(MessageCatalog.RenderLegacy("cli.bake_tool_limitation_report"));
        if (input["kind"] is not null)
        {
            if (!options.TryGetValue("--out", out string? output)) throw new ArgumentException("Baking a plan directly requires --out NEW_DIRECTORY.");
            input = new JsonObject { ["schema_version"] = 2, ["plan"] = input, ["output_directory"] = Path.GetFullPath(output) };
            text = input.ToJsonString();
        }
        else if (options.ContainsKey("--out")) throw new ArgumentException("A bake request already specifies its output directory; --out is only for a direct plan.");
        if (input["schema_version"]?.GetValue<int>() != 2 || input["plan"]?["kind"]?.GetValue<string>() != "hybrid_video")
            throw new InvalidDataException("A version 2 Scene bake request with a supported plan is required. Legacy effect-cache and media plans were removed; analyze the Scene source again.");
        HybridPlanFormat.Validate(input["plan"]!.AsObject());
        // 播放版编码路径；支持的 Vulkan 路径直接生成成品，无需无损 master。
        if (options.TryGetValue("--encoder", out string? encoderChoice))
        {
            input["playback_encoder"] = PlaybackEncoderSelection.Normalize(encoderChoice);
            text = input.ToJsonString();
        }
        if (options.TryGetValue("--effect-resolution", out string? effectResolution))
        {
            if (effectResolution is not ("original" or "output"))
                throw new ArgumentException("--effect-resolution must be original or output.");
            input["match_effect_resolution"] = effectResolution == "output";
            text = input.ToJsonString();
        }
        if (options.TryGetValue("--effect-render-scale", out string? scaleChoice))
        {
            if (!double.TryParse(scaleChoice, NumberStyles.Float, CultureInfo.InvariantCulture, out double scale) ||
                !double.IsFinite(scale) || scale is <= 0 or > 1)
                throw new ArgumentException("--effect-render-scale must be in (0, 1]; default 1 preserves the original effect resolution.");
            input["effect_render_scale"] = scale;
            text = input.ToJsonString();
        }
        // 成品编码的跨进程槽位配额：0（默认）不限。多槽并行跑批时用它压住 ffmpeg 抢核，渲染阶段不受限制。
        if (options.TryGetValue("--encode-slots", out string? encodeSlotsChoice))
        {
            if (!int.TryParse(encodeSlotsChoice, NumberStyles.Integer, CultureInfo.InvariantCulture, out int encodeSlots) ||
                encodeSlots < 0 || encodeSlots > EncodeSlots.MaximumQuota)
                throw new ArgumentException($"--encode-slots must be an integer from 0 to {EncodeSlots.MaximumQuota}; 0 means no limit.");
            input["encode_slots"] = encodeSlots;
            text = input.ToJsonString();
        }
        // 单案内同时在飞的组主渲染数：1（默认）与逐组串行完全一致；组的判定、编码与写入始终按组序串行。
        if (options.TryGetValue("--group-parallel", out string? groupParallelChoice))
        {
            if (!int.TryParse(groupParallelChoice, NumberStyles.Integer, CultureInfo.InvariantCulture, out int groupParallel) ||
                groupParallel < 1 || groupParallel > 64)
                throw new ArgumentException("--group-parallel must be an integer from 1 to 64; 1 renders one group at a time.");
            input["group_parallel"] = groupParallel;
            text = input.ToJsonString();
        }
        // 开发用：保留中间产物（捕获副本、各组无损 master、合成探针与参照、分析刷新目录）。默认删，磁盘峰值按此设计。
        if (options.TryGetValue("--keep-intermediates", out string? keepIntermediates))
        {
            if (keepIntermediates is not ("true" or "false"))
                throw new ArgumentException("--keep-intermediates must be true or false.");
            input["keep_intermediates"] = keepIntermediates == "true";
            text = input.ToJsonString();
        }
        var tools = options.TryGetValue("--tools", out string? toolsPath) ? await ReadTools(toolsPath, cancellation.Token) : NativeEnvironment.FindTools();
        var progress = new Progress<RenderProgress>(p => Console.Error.WriteLine(JsonSerializer.Serialize(p, jsonOptions)));
        var request = JsonSerializer.Deserialize<HybridBakeRequest>(text, jsonOptions) ?? throw new InvalidDataException("Invalid hybrid bake request.");
        JsonObject baked = await new HybridBakeService(tools).BakeAsync(request, progress, cancellation.Token);
        Console.WriteLine(baked.ToJsonString(jsonOptions));
        // 分阶段耗时走 stderr，stdout 保持纯 JSON。
        if (StageTiming.Summary(baked) is string stageSummary) Console.Error.WriteLine(stageSummary);
        // 有双语拒绝理由时在 stderr 上中英各说一遍；stdout 仍是纯 JSON，拒绝的退出码仍为 0。
        if (baked["reason_localized"] is JsonObject reasonText)
        {
            Console.Error.WriteLine(reasonText["zh"]?.GetValue<string>());
            Console.Error.WriteLine(reasonText["en"]?.GetValue<string>());
        }
        // 摆动改频成立时说一句改了多少、冻结了什么（开关关闭的计划没有 sway_retime，这一行不出现）。
        if (baked["sway_retime"] is JsonObject swayRetime) Console.Error.WriteLine(PlanNarrative.SwayRetimeLine(swayRetime, language));
        // 结论行之后补一句接缝预览的路径：通过、被拒都有。
        foreach (string previewLine in SeamPreview.SummaryLines(baked, language)) Console.Error.WriteLine(previewLine);
    }
    else if (args[0] == "validate")
    {
        var tools = options.TryGetValue("--tools", out string? toolsPath)
            ? await ReadTools(toolsPath, cancellation.Token) : NativeEnvironment.FindTools();
        string text = await File.ReadAllTextAsync(args[1], cancellation.Token);
        var progress = new Progress<RenderProgress>(p => Console.Error.WriteLine(JsonSerializer.Serialize(p, jsonOptions)));
        var request = JsonSerializer.Deserialize<ValidationRequest>(text, jsonOptions) ?? throw new InvalidDataException("Invalid validation request.");
        Console.WriteLine((await new CandidateValidation(tools).ValidateAsync(request, progress, cancellation.Token)).ToJsonString(jsonOptions));
    }
    else if (args[0] == "targets")
    {
        var controller = new WallpaperController(args[1]);
        var targets = await controller.ReadCurrentTargetsAsync(cancellation.Token);
        var result = new List<object>();
        foreach (var target in targets)
        {
            var observed = await controller.ObserveAsync(target.Location, cancellation.Token);
            result.Add(new { target.Profile, target.Location, saved_file = target.CurrentFile,
                observed_file = observed.File, evidence = observed.Evidence, target.Assignment });
        }
        Console.WriteLine(JsonSerializer.Serialize(result, jsonOptions));
    }
    else if (args[0] is "apply" or "rollback")
    {
        if (!options.TryGetValue("--wallpaper-engine", out string? executable)) throw new ArgumentException("--wallpaper-engine is required.");
        var controller = new WallpaperController(executable);
        if (args[0] == "apply")
        {
            var request = JsonSerializer.Deserialize<WallpaperApplyRequest>(await File.ReadAllTextAsync(args[1], cancellation.Token), jsonOptions)
                ?? throw new InvalidDataException("Invalid application request.");
            Console.WriteLine((await controller.ApplyAsync(request, cancellation.Token)).ToJsonString(jsonOptions));
        }
        else Console.WriteLine((await controller.RollbackAsync(args[1], cancellation.Token)).ToJsonString(jsonOptions));
    }
    else if (args[0] == "export")
    {
        string text = await File.ReadAllTextAsync(args[1], cancellation.Token);
        if (JsonNode.Parse(text)?["project"] is null)
            throw new InvalidDataException("Export requires a generated project. Raw-video and effect-patch exports were removed.");
        var request = JsonSerializer.Deserialize<CandidateExportRequest>(text, jsonOptions) ?? throw new InvalidDataException("Invalid export request.");
        Console.WriteLine((await CandidateExporter.ExportAsync(request, cancellation.Token)).ToJsonString(jsonOptions));
    }
    else if (args[0] is "pack-video" or "pack-rgba")
    {
        if (!options.TryGetValue("--out", out string? output) ||
            !uint.TryParse(options.GetValueOrDefault("--width"), out uint width) ||
            !uint.TryParse(options.GetValueOrDefault("--height"), out uint height))
            throw new ArgumentException("--out, --width and --height are required.");
        if (args[0] == "pack-video") await TextureContainer.WriteVideoAsync(output, args[1], width, height, cancellation.Token);
        else await TextureContainer.WriteRgbaAsync(output, width, height, await File.ReadAllBytesAsync(args[1], cancellation.Token), cancellation.Token);
        Console.WriteLine(JsonSerializer.Serialize(new { status = "packaged_not_playback_validated", output = Path.GetFullPath(output), width, height }, jsonOptions));
    }
    else throw new ArgumentException($"Unknown command: {args[0]}");
    return 0;
}
catch (OperationCanceledException) { Console.Error.WriteLine("Cancelled; source is unchanged. Incomplete output is retained for inspection."); return 130; }
catch (Exception error)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(new { status = "failed", error_type = error.GetType().Name, message = error.Message }, jsonOptions));
    if (args.Length > 0 && args[0] == "analyze")
    {
        // 人话那一行按异常带着的文案（键 + 参数）出；没带文案的异常原样给英文消息。
        Console.Error.WriteLine(MessageCatalog.Get("summary.failed", language, Message.Of(error)?.In(language) ?? error.Message));
    }
    return 1;
}
