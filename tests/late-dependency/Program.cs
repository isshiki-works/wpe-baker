using System.Text.Json;
using System.Text.Json.Nodes;
using Baker.Core;

if (args.Length is < 2 or > 3)
    throw new ArgumentException("Usage: late-dependency <new-output-directory> <Wallpaper Engine assets> [device UUID]");

string repository = Directory.GetCurrentDirectory();
string output = Path.GetFullPath(args[0]);
string assets = Path.GetFullPath(args[1]);
string? device = args.Length == 3 ? args[2] : null;
if (Directory.Exists(output) || File.Exists(output)) throw new IOException("Test output must be new.");
if (!Directory.Exists(assets)) throw new DirectoryNotFoundException(assets);
Directory.CreateDirectory(output);

string fixture = Path.Combine(output, "fixture-source");
CopyTree(Path.Combine(repository, "tests", "fixtures", "native", "date-clock"), fixture);
JsonObject scene = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(fixture, "scene.json")))!.AsObject();
scene["objects"]!.AsArray()[0]!["name"] = "delayed-external-input";
scene["objects"]!.AsArray()[0]!["alpha"] = new JsonObject
{
    ["value"] = 1.0,
    ["script"] = """
        let lateWallClock = 0;
        export function init(value) {
            const permittedRuntime = engine.runtime;
            const permittedResolution = engine.canvasSize.x;
            const permittedRandom = Math.random();
            engine.setTimeout(() => { lateWallClock = globalThis['Date']['now'](); }, 600);
            return value;
        }
        export function update(value) {
            return lateWallClock === 0 ? 1.0 : 0.25 + 0.5 * (lateWallClock % 2);
        }
        """
};
await File.WriteAllTextAsync(Path.Combine(fixture, "scene.json"), scene.ToJsonString(new() { WriteIndented = true }));

string renderer = Path.Combine(repository, "build", "native-release22", "bin", "wpe-render.exe");
string encoder = Path.Combine(repository, ".deps", "ffmpeg-encoder-gpl2", "portable");
var tools = new NativeTools(renderer, Path.Combine(encoder, "ffmpeg.exe"), Path.Combine(encoder, "ffprobe.exe"),
    [Path.Combine(repository, ".tools", "llvm-mingw-22", "bin"),
     Path.Combine(repository, ".deps", "ffmpeg-lgpl21", "prefix", "bin")]);
var analyze = new HybridAnalyzeRequest(2, fixture, assets, Path.Combine(output, "analysis"), 64, 32, 120, 1,
    DeviceUuid: device);
JsonObject plan = await new HybridScenePlanner(tools).AnalyzeAsync(analyze);
JsonArray analyzedDependencies = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(output, "analysis", "runtime.json")))!["runtime_dependencies"]!.AsArray();
Check(!analyzedDependencies.OfType<JsonObject>().Any(IsExternalInput),
    "The 48-frame analysis unexpectedly reached the 600 ms timer.");
Check(plan["video_groups"]!.AsArray().OfType<JsonObject>().Any(group =>
    group["layer_ids"]!.AsArray().Any(id => id!.GetValue<int>() == 1)),
    "The bounded analysis did not leave the delayed owner in a video group.");

plan["loop"] = new JsonObject
{
    ["schema_version"] = 1,
    ["status"] = "synthetic_candidate_requires_seam_validation",
    ["candidates"] = new JsonArray(new JsonObject
    {
        ["frames"] = 120ul,
        ["seconds"] = 1.0,
        ["patches"] = new JsonArray(),
        ["components"] = new JsonArray()
    }),
    ["unresolved"] = new JsonArray(),
    ["evidence"] = new JsonArray(),
    ["visual_seam"] = "not_verified",
    ["encoded_loop"] = "not_verified"
};
JsonObject bake = await new HybridBakeService(tools).BakeAsync(new(2, plan, Path.Combine(output, "bake"), DeviceUuid: device));
JsonObject validation = bake["late_dependency_validation"]?.AsObject()
    ?? throw new InvalidDataException("Late dependency rejection record is missing.");
JsonObject[] rejectedDependencies = validation["dependencies"]!.AsArray().OfType<JsonObject>().ToArray();
Check(bake["status"]?.GetValue<string>() == "candidate_rejected_late_dependency",
    "The full capture did not reject the late external input.");
Check(rejectedDependencies.Length == 1 && rejectedDependencies[0]["owner"]?.GetValue<int>() == 1 &&
    rejectedDependencies[0]["operation"]?.GetValue<string>() == "input" &&
    rejectedDependencies[0]["property"]?.GetValue<string>() == "wall_clock",
    "The rejection did not isolate the delayed wall-clock dependency.");
Check(bake["groups"]!.AsArray()[0]!["status"]?.GetValue<string>() == "rejected_late_external_input" &&
    bake["project_path"] is null && !Directory.Exists(Path.Combine(output, "bake", "group-1", "encoded")),
    "The rejected group advanced to an applicable encoded replacement.");
JsonObject fullResult = JsonNode.Parse(await File.ReadAllTextAsync(
    Path.Combine(output, "bake", "group-1", "master", "native", "result.json")))!.AsObject();
JsonArray fullDependencies = fullResult["runtime_dependencies"]?.AsArray()
    ?? throw new InvalidDataException("Full group capture omitted runtime dependency metadata.");
Check(fullDependencies.OfType<JsonObject>().Any(IsExternalInput) &&
    fullDependencies.OfType<JsonObject>().Any(d => d["operation"]?.GetValue<string>() == "time") &&
    fullDependencies.OfType<JsonObject>().Any(d => d["operation"]?.GetValue<string>() == "random"),
    "The full trace did not contain the expected late, simulation-time, and deterministic-random observations.");

var report = new JsonObject
{
    ["status"] = "passed",
    ["analysis_frames"] = 48,
    ["late_timer_milliseconds"] = 600,
    ["full_capture_frames"] = 120,
    ["analysis_external_inputs"] = analyzedDependencies.OfType<JsonObject>().Count(IsExternalInput),
    ["full_external_inputs"] = fullDependencies.OfType<JsonObject>().Count(IsExternalInput),
    ["rejected_dependencies"] = validation["dependencies"]!.DeepClone(),
    ["ignored_dependency_operations_present"] = new JsonArray("time", "random"),
    ["bake_report"] = Path.Combine(output, "bake", "bake.json")
};
await File.WriteAllTextAsync(Path.Combine(output, "report.json"), report.ToJsonString(new() { WriteIndented = true }));
Console.WriteLine(report.ToJsonString(new() { WriteIndented = true }));

static bool IsExternalInput(JsonObject dependency) =>
    dependency["operation"]?.GetValue<string>() == "input" &&
    dependency["property"]?.GetValue<string>() is "wall_clock" or "audio" or "pointer" or "media";

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void CopyTree(string source, string destination)
{
    Directory.CreateDirectory(destination);
    foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
    foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
    {
        string target = Path.Combine(destination, Path.GetRelativePath(source, file));
        File.Copy(file, target);
    }
}
