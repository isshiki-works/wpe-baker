using System.Text.Json;
using System.Text.Json.Nodes;
using Baker.Core;

string root = Directory.GetCurrentDirectory();
string output = Path.GetFullPath(args.Single());
if (Directory.Exists(output)) throw new IOException("Output must be new.");
Directory.CreateDirectory(output);
JsonObject plan = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(root,
    "artifacts/cancel-resume-check-20260908/plan2.json")))!.AsObject();
string renderer = Path.Combine(output, "wpe-render.exe");
File.Copy(Path.Combine(root, "build/native-release22/bin/wpe-render.exe"), renderer);
var tools = new NativeTools(renderer,
    Path.Combine(root, ".deps/ffmpeg-encoder-gpl2/portable/ffmpeg.exe"),
    Path.Combine(root, ".deps/ffmpeg-encoder-gpl2/portable/ffprobe.exe"),
    [Path.Combine(root, ".tools/llvm-mingw-22/bin"), Path.Combine(root, ".deps/ffmpeg-lgpl21/prefix/bin")]);
using var source = new ProjectSource(plan["source"]!.GetValue<string>());
string before = await source.SourceHashAsync();
using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
bool sawActualFrame = false;
var progress = new ImmediateProgress(value =>
{
    if (value.Stage == "rendering" && value.Fraction is > 0)
    {
        sawActualFrame = true;
        cancellation.Cancel();
    }
});
string cancelledOutput = Path.Combine(output, "cancelled");
bool cancelled = false;
try
{
    await new HybridBakeService(tools).BakeAsync(new(2, plan, cancelledOutput, ProbeFrames: 120000),
        progress, cancellation.Token);
}
catch (OperationCanceledException) { cancelled = true; }
if (!cancelled || !sawActualFrame) throw new Exception("Cancellation did not occur after an actual frame.");
JsonObject partial = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(cancelledOutput, "bake.json")))!.AsObject();
if (partial["status"]?.GetValue<string>() != "cancelled" || partial["project_path"] is not null)
    throw new Exception("Cancelled generation exposed an applicable candidate.");
string retryOutput = Path.Combine(output, "retry");
JsonObject retry = await new HybridBakeService(tools).BakeAsync(new(2, plan, retryOutput, ProbeFrames: 48));
if (retry["status"]?.GetValue<string>() != "probe_generated" ||
    !File.Exists(Path.Combine(retry["project_path"]!.GetValue<string>(), "project.json")))
    throw new Exception("Retry did not produce the requested probe.");
string after = await source.SourceHashAsync();
if (before != after) throw new Exception("Source changed.");
var report = new JsonObject
{
    ["status"] = "passed", ["cancelled_after_actual_frame"] = sawActualFrame,
    ["cancelled_status"] = partial["status"]!.DeepClone(), ["cancelled_candidate_exposed"] = false,
    ["retry_status"] = retry["status"]!.DeepClone(), ["retry_frames"] = retry["frames"]!.DeepClone(),
    ["source_sha256_before"] = before, ["source_sha256_after"] = after,
    ["source_unchanged"] = true,
    ["recovery_behavior"] = "Existing product behavior: retain partial files and retry into a new folder; no in-place checkpoint continuation.",
    ["child_process_exit_verification"] = "Parent must check command lines for this unique output directory after completion."
};
await File.WriteAllTextAsync(Path.Combine(output, "report.json"), report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(report.ToJsonString());

sealed class ImmediateProgress(Action<RenderProgress> action) : IProgress<RenderProgress>
{
    public void Report(RenderProgress value) => action(value);
}
