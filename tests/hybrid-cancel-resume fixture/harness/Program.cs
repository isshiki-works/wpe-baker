using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Baker.Core;

if (args.Length != 5) throw new ArgumentException("Usage: harness PLAN TOOLS SOURCE CANCEL_OUT RETRY_OUT");
string planPath = Path.GetFullPath(args[0]);
string toolsPath = Path.GetFullPath(args[1]);
string sourcePath = Path.GetFullPath(args[2]);
string cancelOut = Path.GetFullPath(args[3]);
string retryOut = Path.GetFullPath(args[4]);
var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
var toolsJson = JsonNode.Parse(await File.ReadAllTextAsync(toolsPath))!.Deserialize<NativeTools>(options)!
    with { Renderer = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(toolsPath)!, JsonNode.Parse(await File.ReadAllTextAsync(toolsPath))!["renderer"]!.GetValue<string>())) };
// Resolve all tool paths relative to the tools file, matching BakeCLI.
var rawTools = JsonNode.Parse(await File.ReadAllTextAsync(toolsPath))!.AsObject();
string Resolve(string key) => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(toolsPath)!, rawTools[key]!.GetValue<string>()));
toolsJson = new NativeTools(Resolve("renderer"), Resolve("ffmpeg"), Resolve("ffprobe"),
    rawTools["runtime_directories"]!.AsArray().Select(n => ResolveValue(n!.GetValue<string>())).ToArray());
string ResolveValue(string p) => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(toolsPath)!, p));
var plan = JsonNode.Parse(await File.ReadAllTextAsync(planPath))!.AsObject();
string sourceBefore = await new ProjectSource(sourcePath).SourceHashAsync();
var requestCancel = new HybridBakeRequest(2, plan, cancelOut, ProbeFrames: 2,
    DeviceUuid: "b22adf1f455b2bcc8bdbefd93eab85ee");
var cancel = new JsonObject { ["output"] = cancelOut, ["status"] = "not_started" };
try
{
    using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
    await new HybridBakeService(toolsJson).BakeAsync(requestCancel, cancellationToken: cts.Token);
    cancel["status"] = "unexpected_completed";
}
catch (OperationCanceledException ex)
{
    cancel["status"] = "cancelled"; cancel["exception"] = ex.GetType().Name; cancel["message"] = ex.Message;
}
catch (Exception ex)
{
    cancel["status"] = "failed"; cancel["exception"] = ex.GetType().Name; cancel["message"] = ex.Message;
}
cancel["output_exists"] = Directory.Exists(cancelOut);
cancel["partial_report_exists"] = File.Exists(Path.Combine(cancelOut, "bake.json"));
cancel["source_hash_after_cancel"] = await new ProjectSource(sourcePath).SourceHashAsync();

var retryRequest = new HybridBakeRequest(2, plan, retryOut, ProbeFrames: 2,
    DeviceUuid: "b22adf1f455b2bcc8bdbefd93eab85ee");
JsonObject retry;
try { retry = await new HybridBakeService(toolsJson).BakeAsync(retryRequest); }
catch (Exception ex) { retry = new JsonObject { ["status"] = "exception", ["exception"] = ex.GetType().Name, ["message"] = ex.Message }; }
retry["output"] = retryOut;
retry["source_hash_after_retry"] = await new ProjectSource(sourcePath).SourceHashAsync();
var result = new JsonObject { ["source"] = sourcePath, ["source_hash_before"] = sourceBefore,
    ["cancel"] = cancel, ["retry"] = retry };
await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(cancelOut)!, "harness-result.json"), result.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(result.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
