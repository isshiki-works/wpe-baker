using System.Text.Json.Nodes;
using Baker.Core;

string root = Path.Combine(Path.GetTempPath(), "hybrid-loop-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var passed = new List<string>();
void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException("FAILED: " + name); passed.Add(name); }
void Write(string relative, string text)
{
    string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text);
}
Write("project.json", "{\"type\":\"scene\",\"file\":\"scene.json\"}");
Write("effects/sway.json", "{\"passes\":[{\"material\":\"materials/sway.json\"}]}");
Write("materials/sway.json", "{\"passes\":[{\"shader\":\"effects/auto_sway\"}]}");
Write("shaders/effects/auto_sway.vert", "uniform float g_Time; uniform float g_Speed; void main(){float thisMotionTime=g_Time*g_Speed; float x=sin(thisMotionTime*M_PI_2);}");
Write("shaders/effects/auto_sway.frag", "void main(){}");
Write("effects/scroll.json", "{\"passes\":[{\"material\":\"materials/scroll.json\"}]}");
Write("materials/scroll.json", "{\"passes\":[{\"shader\":\"effects/scroll\"}]}");
Write("shaders/effects/scroll.frag", "scroll = sign(scroll) * pow(vec2(g_ScrollX, g_ScrollY), CAST2(2.0));\nfrac((v_TexCoord + v_Scroll) * g_Scale)");
var objects = new JsonArray();
foreach (var (id, speed) in new[] { (1, .4), (2, .75), (3, .28), (4, .18000001), (5, .25), (6, .45) })
    objects.Add(new JsonObject {
        ["id"] = id,
        ["effects"] = new JsonArray {
            new JsonObject {
                ["file"] = "effects/sway.json",
                ["passes"] = new JsonArray { new JsonObject { ["constantshadervalues"] = new JsonObject { ["speed"] = speed } } }
            }
        }
    });
objects.Add(new JsonObject { ["id"] = 7, ["animationlayers"] = new JsonArray {
    new JsonObject { ["id"] = 70, ["name"] = "blink", ["rate"] = 1.0 }, new JsonObject { ["id"] = 71, ["name"] = "blink", ["rate"] = 1.0 } } });
var scene = new JsonObject { ["objects"] = objects };
Write("scene.json", scene.ToJsonString());
var runtime = new JsonObject { ["runtime_animation_periods"] = new JsonArray {
    new JsonObject { ["source_owner_layer_id"] = 7, ["mechanism"] = "puppet_bone", ["track_name"] = "blink", ["duration_seconds"] = 7.0,
        ["playback_rate"] = 1.0, ["looping"] = true, ["event_driven"] = false, ["confidence"] = "high" } } };
using var source = new ProjectSource(root);
JsonObject original = source.ReadJson(source.SceneResource);
JsonObject report = HybridLoopService.Analyze(original, source, null, runtime, Enumerable.Range(1, 7).ToArray(), 60, 1);
Check(report["retime_mode"]!.GetValue<string>() == "clip_retime_after_locked_search", "clips are retimed only after locked authored rates cannot fit");
JsonObject candidate = report["candidates"]!.AsArray().Single()!.AsObject();
Check(candidate["frames"]!.GetValue<ulong>() == 6000 && Math.Abs(candidate["seconds"]!.GetValue<double>() - 100) < 1e-12,
    "fixed analytic periods constrain this generic fixture to 100 seconds");
var animationPatches = candidate["patches"]!.AsArray().OfType<JsonObject>().Where(x => x["kind"]!.GetValue<string>() == "animation_rate").ToArray();
Check(animationPatches.Length == 2 && animationPatches.All(x => Math.Abs(x["new_value"]!.GetValue<double>() - .98) < 1e-12),
    "one runtime track yields both matching authored handles with the expected local rate");
Check(candidate["patches"]!.AsArray().OfType<JsonObject>().Any(x => x["kind"]!.GetValue<string>() == "shader_speed" &&
      Math.Abs(x["old_value"]!.GetValue<double>() - .18000001) < 1e-12 && Math.Abs(x["new_value"]!.GetValue<double>() - .18) < 1e-12),
    "float32 shader-speed normalization is an explicit capture patch rather than an unstated exact-period assumption");
var capture = original.DeepClone().AsObject();
HybridLoopService.ApplyPatches(capture, report);
var rates = capture["objects"]!.AsArray().OfType<JsonObject>().Single(x => x["id"]!.GetValue<int>() == 7)["animationlayers"]!.AsArray()
    .OfType<JsonObject>().Select(x => x["rate"]!.GetValue<double>()).ToArray();
Check(rates.All(x => Math.Abs(x - .98) < 1e-12), "apply patches changes only the temporary matching animation rates");
Check(report["visual_seam"]!.GetValue<string>() == "not_verified", "analytic candidate does not claim a visual seam");
var empty = HybridLoopService.Analyze(new JsonObject { ["objects"] = new JsonArray { new JsonObject { ["id"] = 99 } } }, source, null,
    new JsonObject(), [99], 60, 1);
Check(empty["status"]!.GetValue<string>() == "no_analytic_candidate" && empty["candidates"]!.AsArray().Count == 0,
    "scenes without parsed periodic components return an observational no-candidate report");
var mismatchedRuntime = runtime.DeepClone().AsObject();
mismatchedRuntime["runtime_animation_periods"]![0]!["playback_rate"] = .9;
var mismatched = HybridLoopService.Analyze(original, source, null, mismatchedRuntime, Enumerable.Range(1, 7).ToArray(), 60, 1);
Check(mismatched["unresolved"]!.AsArray().OfType<JsonObject>().Any(x => x["detail"]!.GetValue<string>().Contains("playback_rate", StringComparison.Ordinal)),
    "a runtime rate mismatch remains unresolved instead of using the source rate");
var fixedScene = new JsonObject { ["objects"] = new JsonArray {
    new JsonObject { ["id"] = 8 }, new JsonObject { ["id"] = 9 } } };
var fixedRuntime = new JsonObject { ["runtime_animation_periods"] = new JsonArray {
    new JsonObject { ["source_owner_layer_id"] = 8, ["mechanism"] = "puppet_bone", ["track_name"] = "anonymous-a", ["duration_seconds"] = 111.0,
        ["playback_rate"] = 1.0, ["looping"] = true, ["event_driven"] = false, ["confidence"] = "high" },
    new JsonObject { ["source_owner_layer_id"] = 9, ["mechanism"] = "puppet_bone", ["track_name"] = "anonymous-b", ["duration_seconds"] = 91.0,
        ["playback_rate"] = 1.0, ["looping"] = true, ["event_driven"] = false, ["confidence"] = "high" } } };
var fixedReport = HybridLoopService.Analyze(fixedScene, source, null, fixedRuntime, [8, 9], 60, 1);
Check(fixedReport["candidates"]!.AsArray().Count == 0 && fixedReport["fixed_frame_step"] is not null,
    "unpatchable 111/91-second runtime tracks remain fixed constraints instead of being ignored for a short loop");
var spriteScene = new JsonObject { ["objects"] = new JsonArray { new JsonObject { ["id"] = 10 } } };
var spriteRuntime = new JsonObject { ["runtime_animation_periods"] = new JsonArray {
    new JsonObject { ["source_owner_layer_id"] = 10, ["mechanism"] = "sprite", ["track_name"] = "sprite-loop", ["duration_seconds"] = .1,
        ["playback_rate"] = 1.0, ["looping"] = true, ["event_driven"] = false, ["confidence"] = "high" } } };
var spriteReport = HybridLoopService.Analyze(spriteScene, source, null, spriteRuntime, [10], 60, 1);
JsonObject spriteCandidate = spriteReport["candidates"]!.AsArray().First()!.AsObject();
Check(spriteCandidate["components"]!.AsArray().OfType<JsonObject>().Single(x => x["id"]!.GetValue<string>().Contains("sprite-loop", StringComparison.Ordinal))["speed_multiplier"]!.GetValue<double>() == 1 &&
      !spriteCandidate["patches"]!.AsArray().OfType<JsonObject>().Any(x => x["kind"]!.GetValue<string>() == "animation_rate"),
    "a sprite duration is a fixed period at rate one and never emits an animation-rate patch");
double newScrollSpeed = (double)typeof(HybridLoopService)
    .GetMethod("ShaderPatchValue", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
    .Invoke(null, [-.51, 1.0201, 2d])!;
Check(newScrollSpeed < 0 && Math.Abs(newScrollSpeed + .51 * Math.Sqrt(1.0201)) < 1e-12,
    "squared scroll speed patches preserve sign and apply the square-root motion multiplier");
var scrollValues = new JsonObject { ["speedx"] = -.51 };
var scrollPass = new JsonObject { ["constantshadervalues"] = scrollValues };
var scrollEffect = new JsonObject { ["passes"] = new JsonArray { scrollPass } };
var scrollOwner = new JsonObject { ["id"] = 11, ["effects"] = new JsonArray { scrollEffect } };
var scrollCapture = new JsonObject { ["objects"] = new JsonArray { scrollOwner } };
var scrollPatch = new JsonObject { ["kind"] = "shader_speed", ["owner_layer_id"] = 11, ["effect_index"] = 0, ["pass_index"] = 0,
    ["constant_key"] = "speedx", ["value_index"] = 0, ["animation_layer_id"] = null, ["old_value"] = -.51, ["new_value"] = newScrollSpeed };
var scrollReport = new JsonObject { ["candidates"] = new JsonArray { new JsonObject { ["patches"] = new JsonArray { scrollPatch } } } };
HybridLoopService.ApplyPatches(scrollCapture, scrollReport);
Check(Math.Abs(scrollCapture["objects"]!.AsArray()[0]!.AsObject()["effects"]!.AsArray()[0]!.AsObject()["passes"]!.AsArray()[0]!.AsObject()["constantshadervalues"]!["speedx"]!.GetValue<double>() - newScrollSpeed) < 1e-12,
    "scroll patch verifies its old value before changing the temporary capture scene");
if (args.Length == 3)
{
    // Real-case mode is intentionally CPU-only: <source> <plan.json> <output.json>.
    // The plan supplies the exact production selection and settings; its existing runtime
    // observation is replayed, never collected again here.
    using var actualSource = new ProjectSource(args[0]);
    JsonObject plan = JsonNode.Parse(File.ReadAllText(args[1]))!.AsObject();
    JsonObject settings = plan["settings"]!.AsObject();
    Check(Path.GetFullPath(plan["source"]!.GetValue<string>()).Equals(Path.GetFullPath(args[0]), StringComparison.OrdinalIgnoreCase),
        "real-case source argument belongs to the supplied plan");
    string runtimePath = plan["runtime_evidence"]!.GetValue<string>();
    Check(File.Exists(runtimePath), "real-case plan retains its runtime observation");
    int[] actualIds = plan["video_groups"]!.AsArray().OfType<JsonObject>()
        .SelectMany(group => group["layer_ids"]!.AsArray().Select(id => id!.GetValue<int>())).Distinct().ToArray();
    int[] birdIds = [186, 197, 195, 242, 191, 193];
    Check(birdIds.All(actualIds.Contains), "all six script-controlled bird layers remain in the baked selection");
    JsonObject actualScene = actualSource.ReadJson(actualSource.SceneResource);
    JsonObject actualRuntime = JsonNode.Parse(File.ReadAllText(runtimePath))!.AsObject();
    int initializationDependencyCount = actualRuntime["runtime_dependencies"]!.AsArray().OfType<JsonObject>()
        .Count(dependency => dependency["initialization"]?.GetValue<bool>() == true);
    Check(initializationDependencyCount > 0, "runtime observation retains initialization-phase script evidence");

    // Exercise the production property freeze, rather than a second implementation in the test.
    JsonObject captureScene = actualScene.DeepClone().AsObject();
    JsonObject properties = plan["snapshot_properties"]!.AsObject();
    typeof(HybridScenePlanner).GetMethod("FreezeTemporalProperties", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
        .Invoke(null, [captureScene, properties]);
    JsonObject actualReport = HybridLoopService.Analyze(captureScene, actualSource, settings["assets"]!.GetValue<string>(), actualRuntime, actualIds,
        settings["fps_numerator"]!.GetValue<uint>(), settings["fps_denominator"]!.GetValue<uint>(), settings["maximum_retime_percent"]!.GetValue<double>());
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[2]))!);
    File.WriteAllText(args[2], actualReport.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

    JsonObject actualCandidate = actualReport["candidates"]!.AsArray().OfType<JsonObject>().Single(candidate =>
        candidate["frames"]!.GetValue<ulong>() == 6000 && Math.Abs(candidate["seconds"]!.GetValue<double>() - 100) < 1e-12);
    Check(settings["fps_numerator"]!.GetValue<uint>() == 60 && settings["fps_denominator"]!.GetValue<uint>() == 1 &&
        settings["maximum_retime_percent"]!.GetValue<double>() == 2, "real-case call uses the plan's 60 FPS and two-percent retime limit");
    Check(actualCandidate["components"]!.AsArray().OfType<JsonObject>().All(component =>
        Math.Abs(component["speed_multiplier"]!.GetValue<double>() - 1) <= .02 + 1e-12), "every selected component retimes by at most two percent");
    Check(birdIds.All(id => actualReport["unresolved"]!.AsArray().OfType<JsonObject>().Any(item =>
        item["owner_layer_id"]?.GetValue<int>() == id && item["detail"]?.GetValue<string>().Contains("Script-controlled playback", StringComparison.Ordinal) == true)),
        "each selected bird is unresolved for script-controlled sprite playback, not treated as a fixed 1.68-second period");
    Check(!actualCandidate["components"]!.AsArray().OfType<JsonObject>().Any(component => birdIds.Any(id =>
        component["id"]!.GetValue<string>().StartsWith($"animation/{id}/", StringComparison.Ordinal))),
        "script-controlled bird durations do not constrain the candidate LCM");
    Check(initializationDependencyCount == actualRuntime["runtime_dependencies"]!.AsArray().OfType<JsonObject>()
        .Count(dependency => dependency["initialization"]?.GetValue<bool>() == true), "analysis does not delete initialization-phase runtime evidence");
}
Console.WriteLine($"passed {passed.Count}: {string.Join(", ", passed)}");
