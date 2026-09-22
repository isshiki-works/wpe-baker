using System.Text.Json;
using System.Text.Json.Nodes;
using Baker.Core;

/// <summary>
/// 默认输出分辨率：画布取值与属性求值、按本机屏幕铺满缩放（缩放、封顶、宽高比不同时取大者）、透视退回、显式优先、
/// 大尺寸的 H.264→HEVC 选择，以及 plan 与一行结论里的记录。
/// </summary>
internal static class OutputResolutionChecks
{
    internal static async Task RunAsync(Action<bool, string> check, string root)
    {
        static JsonObject Scene(JsonNode? orthographic, params JsonObject[] objects)
        {
            var general = new JsonObject { ["clearenabled"] = true };
            if (orthographic is not null) general["orthogonalprojection"] = orthographic;
            return new JsonObject { ["general"] = general, ["objects"] = new JsonArray(objects.Select(o => (JsonNode)o).ToArray()) };
        }
        static JsonObject Ortho(JsonNode width, JsonNode height) => new() { ["width"] = width, ["height"] = height };
        static (uint, uint)? NoDisplayQuery() => throw new InvalidOperationException("an explicit size must not query the display");
        static (uint, uint)? NoDisplay() => null;
        static Func<(uint Width, uint Height)?> Screen(uint width, uint height) => () => (width, height);
        var none = new JsonObject();

        // ---- 画布取值（读不到屏幕时按画布原尺寸，便于单看取值） ----
        var canvas = OutputResolution.Choose(Scene(Ortho(3840, 2160)), none, 0, 0, null, NoDisplay);
        check(canvas is { Width: 3840, Height: 2160, Source: OutputResolution.SceneCanvas, CanvasWidth: 3840, CanvasHeight: 2160, FitScale: null } &&
            canvas.CanvasBasis == "orthogonalprojection" && canvas.DisplayWidth is null &&
            canvas.ToJson()["scene_canvas"]!.ToJsonString() == "[3840,2160]" && canvas.ToJson()["display"] is null,
            "without a readable display the unspecified size is the scene's orthogonalprojection canvas (3840x2160)");
        var odd = OutputResolution.Choose(Scene(Ortho(4095, 2891)), none, 0, 0, null, NoDisplay);
        check(odd is { Width: 4094, Height: 2890, Source: OutputResolution.SceneCanvas, CanvasWidth: 4095, CanvasHeight: 2891 },
            "an odd canvas is aligned down to even pixels for 4:2:0 and packed crops, keeping the authored values on record");
        var auto = OutputResolution.Choose(Scene(new JsonObject { ["auto"] = true },
            new JsonObject { ["id"] = 1, ["image"] = "models/a.json", ["size"] = "1280 720" },
            new JsonObject { ["id"] = 2, ["image"] = "models/b.json", ["size"] = "7680 4320" }), none, 0, 0, null, NoDisplay);
        check(auto is { Width: 7680, Height: 4320, Source: OutputResolution.SceneCanvas } && auto.CanvasBasis == "orthogonalprojection_auto_largest_image",
            "an auto orthographic projection uses its largest image as the canvas, the projection description's rule");

        // ---- 属性求值 ----
        var boundScene = Scene(Ortho(new JsonObject { ["user"] = "canvaswidth", ["value"] = 1920 },
            new JsonObject { ["user"] = new JsonObject { ["name"] = "canvasheight" }, ["value"] = 1080 }));
        var bound = OutputResolution.Choose(boundScene, new JsonObject { ["canvaswidth"] = 2560, ["canvasheight"] = 1440 }, 0, 0, null, NoDisplay);
        check(bound is { Width: 2560, Height: 1440, Source: OutputResolution.SceneCanvas },
            "a property-bound canvas width/height is evaluated with the snapshot user properties (both binding forms)");
        var unboundDefault = OutputResolution.Choose(boundScene, none, 0, 0, null, NoDisplay);
        check(unboundDefault is { Width: 1920, Height: 1080, Source: OutputResolution.SceneCanvas },
            "a property-bound canvas without a user value falls back to the binding's own value, still as the scene canvas");
        var boundProjection = HybridVideoProjection_Describe(boundScene, new JsonObject { ["canvaswidth"] = 2560, ["canvasheight"] = 1440 });
        check(boundProjection["canvas_width"]!.GetValue<double>() == 2560 && boundProjection["canvas_height"]!.GetValue<double>() == 1440,
            "the projection description reads the same evaluated canvas as the resolution choice");

        // ---- 按屏幕铺满：缩放、封顶、宽高比不同时取大者 ----
        var fit = OutputResolution.Choose(Scene(Ortho(3840, 2160)), none, 0, 0, null, Screen(2880, 1800));
        check(fit is { Width: 3200, Height: 1800, Source: OutputResolution.SceneCanvasFitDisplay, DisplayWidth: 2880, DisplayHeight: 1800 } &&
            Math.Abs(fit.FitScale!.Value - 1800d / 2160) < 1e-12 && !fit.CappedAtCanvas,
            "a 3840x2160 canvas on a 2880x1800 screen scales by max(0.75, 0.8333) to 3200x1800, covering the screen in both directions");
        var eightK = OutputResolution.Choose(Scene(Ortho(7680, 4320)), none, 0, 0, null, Screen(3840, 2160));
        check(eightK is { Width: 3840, Height: 2160, Source: OutputResolution.SceneCanvasFitDisplay, FitScale: 0.5 },
            "the 3669681034 8K canvas (7680x4320) on a 3840x2160 screen is baked at 3840x2160, not 8K");
        var leoFit = OutputResolution.Choose(Scene(Ortho(4096, 2892)), none, 0, 0, null, Screen(3840, 2160));
        check(leoFit is { Width: 3840, Height: 2710, FitScale: 0.9375 } && leoFit.Width >= 3840 && leoFit.Height >= 2160,
            "a canvas wider in aspect than the screen (4096x2892 on 3840x2160) takes the larger ratio 0.9375 so both sides still cover the screen");
        var tallFit = OutputResolution.Choose(Scene(Ortho(3840, 2160)), none, 0, 0, null, Screen(2560, 1600));
        check(tallFit is { Width: 2844, Height: 1600 } && Math.Abs(tallFit.FitScale!.Value - 1600d / 2160) < 1e-12 && tallFit.Width >= 2560,
            "a 16:9 canvas on a 16:10 screen takes the height ratio, the larger one, and crops nothing the screen shows");
        var capped = OutputResolution.Choose(Scene(Ortho(1920, 1080)), none, 0, 0, null, Screen(3840, 2160));
        check(capped is { Width: 1920, Height: 1080, Source: OutputResolution.SceneCanvasFitDisplay, FitScale: 1 } && capped.CappedAtCanvas &&
            capped.ToJson()["capped_at_canvas"]!.GetValue<bool>(),
            "a canvas smaller than the screen is capped at s=1 and never upscaled");
        var exact = OutputResolution.Choose(Scene(Ortho(3840, 2160)), none, 0, 0, null, Screen(3840, 2160));
        check(exact is { Width: 3840, Height: 2160, FitScale: 1 } && !exact.CappedAtCanvas,
            "a canvas equal to the screen keeps its size and is not reported as capped");

        // ---- 透视与缺失画布退回 ----
        int displayQueries = 0;
        var perspective = OutputResolution.Choose(Scene(null), none, 0, 0, null, () => { ++displayQueries; return (3072u, 1920u); });
        check(perspective is { Width: 3072, Height: 1920, Source: OutputResolution.Display, CanvasWidth: null, CanvasHeight: null,
            DisplayWidth: 3072, DisplayHeight: 1920, FitScale: null } && perspective.CanvasBasis == "perspective_no_orthogonalprojection" && displayQueries == 1,
            "a perspective scene (no orthogonalprojection) uses the primary display's physical resolution directly");
        var noDisplay = OutputResolution.Choose(Scene(null), none, 0, 0, null, NoDisplay);
        check(noDisplay is { Width: OutputResolution.FallbackWidth, Height: OutputResolution.FallbackHeight, Source: OutputResolution.Fallback } &&
            noDisplay.Width == 1920 && noDisplay.Height == 1080,
            "without a canvas and without a readable display the size falls back to 1920x1080");
        var invalid = OutputResolution.Choose(Scene(Ortho(0, 2160)), none, 0, 0, null, Screen(2560, 1600));
        var missing = OutputResolution.Choose(Scene(new JsonObject { ["height"] = 2160 }), none, 0, 0, null, Screen(2560, 1600));
        check(invalid is { Width: 2560, Height: 1600, Source: OutputResolution.Display } &&
            missing is { Width: 2560, Height: 1600, Source: OutputResolution.Display, CanvasWidth: null, CanvasHeight: 2160 },
            "a zero or missing canvas dimension is not a usable canvas and uses the display resolution");

        // ---- 显式参数优先 ----
        var explicitSize = OutputResolution.Choose(Scene(Ortho(3840, 2160)), none, 1920, 1080, null, NoDisplayQuery);
        check(explicitSize is { Width: 1920, Height: 1080, Source: OutputResolution.Explicit, CanvasWidth: 3840, CanvasHeight: 2160 },
            "explicit --width/--height win over the canvas without querying the display, and the canvas is still recorded");
        var kept = OutputResolution.Choose(Scene(Ortho(7680, 4320)), none, 3840, 2160, OutputResolution.SceneCanvasFitDisplay, NoDisplayQuery);
        check(kept is { Width: 3840, Height: 2160, Source: OutputResolution.SceneCanvasFitDisplay },
            "a re-analysis of resolved settings keeps the first choice's size and source even on another screen");
        check(OutputResolution.IsKnownSource(null) && OutputResolution.IsKnownSource("scene_canvas_fit_display") &&
            OutputResolution.IsKnownSource("display") && !OutputResolution.IsKnownSource("screen"),
            "resolution sources are limited to explicit, scene_canvas_fit_display, scene_canvas, display and fallback");
        var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        JsonObject serialized = JsonSerializer.SerializeToNode(new HybridAnalyzeRequest(2, "s", "a", "o", 3840, 2160,
            ResolutionSource: OutputResolution.SceneCanvasFitDisplay), jsonOptions)!.AsObject();
        HybridAnalyzeRequest roundTrip = serialized.Deserialize<HybridAnalyzeRequest>(jsonOptions)!;
        serialized.Remove("resolution_source");
        check(roundTrip is { Width: 3840, Height: 2160, ResolutionSource: OutputResolution.SceneCanvasFitDisplay } &&
            serialized.Deserialize<HybridAnalyzeRequest>(jsonOptions)!.ResolutionSource is null &&
            new HybridAnalyzeRequest(2, "s", "a", "o") is { Width: 0, Height: 0 },
            "plan.settings carries resolution_source, older settings without it still load, and the request default is unspecified (0x0)");
        bool halfRejected = false;
        try
        {
            await new HybridScenePlanner(new("not-started", "not-started", "not-started", [])).AnalyzeSingleAsync(
                new(2, Path.Combine(root, "missing-source"), root, Path.Combine(root, "half-size"), 0, 1080));
        }
        catch (InvalidDataException) { halfRejected = true; }
        check(halfRejected && !Directory.Exists(Path.Combine(root, "half-size")),
            "a request with only one of width/height is rejected before any output is created");

        // ---- 大尺寸的编码选择：超出 H.264 限制表即改 HEVC，打包透明超出 HEVC 宽度按既有规则拒绝 ----
        static HardwareDecodeDimensions.Plan Encode(uint width, uint height, bool packedAlpha, uint fps) =>
            HardwareDecodeDimensions.Evaluate(width, height, packedAlpha, fps, 1);
        check(Encode(3840, 2160, false, 60) is { Status: HardwareDecodeDimensions.PassStatus, SoftwareEncoder: "libx264", StoredWidth: 3840, StoredHeight: 2160 },
            "a 3840x2160 opaque output at 60 fps stays within the H.264 table (32400 macroblocks, 1944000 per second) and passes");
        check(Encode(3840, 2160, false, 120) is { Status: HardwareDecodeDimensions.PassStatus, SoftwareEncoder: "libx265" },
            "the same output at 120 fps exceeds the H.264 macroblock rate and switches to HEVC");
        check(Encode(eightK.Width, eightK.Height, true, 60) is { Status: HardwareDecodeDimensions.PassStatus, SoftwareEncoder: "libx265", StoredWidth: 7680, StoredHeight: 2160 },
            "the fitted 3669681034 output (3840x2160) with alpha packed side by side is 7680 wide, beyond H.264, and passes as HEVC");
        check(Encode(leoFit.Width, leoFit.Height, false, 60) is { Status: HardwareDecodeDimensions.PassStatus, SoftwareEncoder: "libx265" } &&
            Encode(leoFit.Width, leoFit.Height, true, 60) is { Status: HardwareDecodeDimensions.PassStatus, SoftwareEncoder: "libx265", StoredWidth: 7680 },
            "the fitted 3685247684 output (3840x2710) exceeds the H.264 frame-size limit and uses HEVC, opaque and packed");
        var fullEightK = Encode(auto.Width, auto.Height, true, 60);
        check(Encode(auto.Width, auto.Height, false, 60) is { Status: HardwareDecodeDimensions.PassStatus, SoftwareEncoder: "libx265" } &&
            fullEightK.Rejected && fullEightK.Violations.Any(v => v is { Measure: "width", Actual: 15360, Limit: 8192 }),
            "an unscaled 7680x4320 output passes opaque as HEVC, and packed alpha (15360 wide) is rejected by the existing HEVC width rule");

        // ---- plan 与一行结论 ----
        string source = Path.Combine(root, "canvas-resolution-source");
        Directory.CreateDirectory(source);
        var objects = new JsonArray(new JsonObject { ["id"] = 1, ["text"] = "still" });
        await File.WriteAllTextAsync(Path.Combine(source, "scene.json"), new JsonObject {
            ["general"] = new JsonObject { ["clearenabled"] = true, ["orthogonalprojection"] = new JsonObject {
                ["width"] = new JsonObject { ["user"] = "canvaswidth", ["value"] = 64 }, ["height"] = 32 } },
            ["objects"] = objects }.ToJsonString());
        await File.WriteAllTextAsync(Path.Combine(source, "project.json"), new JsonObject { ["type"] = "scene", ["file"] = "scene.json",
            ["general"] = new JsonObject { ["properties"] = new JsonObject {
                ["canvaswidth"] = new JsonObject { ["type"] = "slider", ["value"] = 128 } } } }.ToJsonString());
        string trace = Path.Combine(root, "canvas-resolution-trace.json");
        await File.WriteAllTextAsync(trace, new JsonObject {
            ["source"] = Path.Combine(source, "scene.json"), ["status"] = "complete",
            ["source_script_error_count"] = 0, ["source_script_errors"] = new JsonArray(), ["runtime_dependencies"] = new JsonArray(),
            ["runtime_layers"] = new JsonArray(new JsonObject { ["id"] = 1, ["owner"] = 1, ["visible"] = true, ["has_mesh"] = true,
                ["effective_parallax_depth"] = new JsonArray(0, 0),
                ["materials"] = new JsonArray(new JsonObject { ["uses_audio_spectrum"] = false, ["textures"] = new JsonArray() }) }) }.ToJsonString());
        async Task<JsonObject> AnalyzeAsync(string name, uint width, uint height) =>
            await new HybridScenePlanner(new("not-started", "not-started", "not-started", []), Screen(96, 16)).AnalyzeSingleAsync(
                new(2, source, root, Path.Combine(root, name), width, height, RuntimeTraceFile: trace));
        JsonObject planned = await AnalyzeAsync("canvas-resolution-default", 0, 0);
        check(planned["settings"]!["width"]!.GetValue<uint>() == 96 && planned["settings"]!["height"]!.GetValue<uint>() == 24 &&
            planned["settings"]!["resolution_source"]!.GetValue<string>() == OutputResolution.SceneCanvasFitDisplay &&
            planned["output_resolution"]!["source"]!.GetValue<string>() == OutputResolution.SceneCanvasFitDisplay &&
            planned["output_resolution"]!["display"]!.ToJsonString() == "[96,16]" && planned["canvas_width"]!.GetValue<double>() == 128,
            "analyze without a size fits the property-evaluated canvas (128x32) to the injected 96x16 screen as 96x24 (s = max(0.75, 0.5)) and records it in plan.settings");
        check(planned["summary"]!["zh"]!.GetValue<string>().EndsWith("输出分辨率 96×24：场景画布缩放至铺满本机屏幕（画布 128×32，屏幕 96×16）。", StringComparison.Ordinal) &&
            planned["summary"]!["en"]!.GetValue<string>().EndsWith(" Output resolution 96×24: the scene canvas scaled to cover this machine's screen (canvas 128×32, screen 96×16).", StringComparison.Ordinal),
            "the one-line conclusion states the fitted size, the canvas and the screen, in both languages");
        JsonObject requested = await AnalyzeAsync("canvas-resolution-explicit", 64, 32);
        check(requested["settings"]!["width"]!.GetValue<uint>() == 64 &&
            requested["settings"]!["resolution_source"]!.GetValue<string>() == OutputResolution.Explicit &&
            requested["summary"]!["zh"]!.GetValue<string>().EndsWith("输出分辨率取指定值 64×32（场景画布 128×32）。", StringComparison.Ordinal),
            "an explicit size is kept, recorded as explicit, and the conclusion names the differing scene canvas");
        static string Sentence(OutputResolution.Choice choice) =>
            PlanNarrative.Summarize(new JsonObject { ["blockers"] = new JsonArray(new Blocker(BlockerCode.PerspectiveNeedsScreenspace).ToNode()), ["output_resolution"] = choice.ToJson() })["zh"]!.GetValue<string>();
        check(Sentence(capped).EndsWith("输出分辨率 1920×1080，即场景画布原尺寸：画布不足以覆盖本机屏幕 3840×2160，不放大。", StringComparison.Ordinal) &&
            Sentence(canvas).EndsWith("本机屏幕分辨率不可读，输出分辨率取场景画布原尺寸 3840×2160。", StringComparison.Ordinal) &&
            Sentence(perspective).EndsWith("场景无可用正交画布（透视场景或画布缺失），输出分辨率取主显示器分辨率 3072×1920。", StringComparison.Ordinal),
            "capped, display-unavailable and perspective choices each get their own conclusion wording");
        var legacy = new JsonObject { ["blockers"] = new JsonArray(new Blocker(BlockerCode.PerspectiveNeedsScreenspace).ToNode()) };
        check(!PlanNarrative.Summarize(legacy)["zh"]!.GetValue<string>().Contains("烘焙。", StringComparison.Ordinal),
            "a plan without output_resolution keeps its conclusion unchanged");
    }

    // HybridVideoProjection 是内部类型：通过反射调用 Describe，确认投影读到的画布与分辨率取值一致。
    private static JsonObject HybridVideoProjection_Describe(JsonObject scene, JsonObject properties) =>
        (JsonObject)typeof(HybridScenePlanner).Assembly.GetType("Baker.Core.HybridVideoProjection")!
            .GetMethod("Describe", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, [scene, properties, 1920u, 1080u, null])!;
}
