using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Baker.Core;

/// <summary>原 Program.cs 默认路径里的顶层内联检查（包格式、发布目标、混合规划分配与依赖守卫），C0.2 逐行原样搬入。</summary>
internal static class ProgramInlineChecks
{
    internal static async Task RunAsync(Action<bool, string> check, string root)
    {
        var passed = new List<string>();
        void Check(bool condition, string name) { check(condition, name); passed.Add(name); }
// ---- 以下为 Program.cs 原文 ----
string rendererFailureOutput = Path.Combine(root, "renderer-failure-summary");
Directory.CreateDirectory(Path.Combine(rendererFailureOutput, "native"));
string rendererFailureResult = Path.Combine(rendererFailureOutput, "native", "result.json");
await File.WriteAllTextAsync(rendererFailureResult, """{"error":"renderer reported errors","diagnostics":["script warning"]}""");
string rendererFailureLog = Path.Combine(rendererFailureOutput, "renderer.stderr.log");
using var heldRendererLog = new FileStream(rendererFailureLog, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
using var heldRendererWriter = new StreamWriter(heldRendererLog) { AutoFlush = true };
await heldRendererWriter.WriteAsync("""
    [2026-09-13T01:05:07Z WARN] harmless warning
    [2026-09-13T01:05:08Z ERROR] parse puppet failed: models/1x1_puppet.mdl
    [2026-09-13T01:05:09Z ERROR] parse puppet failed: models/1x1_puppet.mdl
    [2026-09-13T01:05:10Z ERROR] material parse failed: materials/missing.json
    [2026-09-13T01:05:11Z INFO] literal ERROR in an ordinary message
    """);
await heldRendererWriter.FlushAsync();
await File.WriteAllTextAsync(Path.Combine(rendererFailureOutput, "encoder.stderr.log"), "[out#0/mp4] Error writing trailer: No space left on device\nConversion failed!\n");
var rendererFailureMethod = typeof(NativeRenderRunner).GetMethod("RendererFailure", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
var rendererFailure = (IOException)rendererFailureMethod.Invoke(null, new object?[] { rendererFailureResult, "renderer stopped", null })!;
Check(rendererFailure.Message.Contains("parse puppet failed: models/1x1_puppet.mdl", StringComparison.Ordinal) &&
    rendererFailure.Message.Contains("material parse failed: materials/missing.json", StringComparison.Ordinal) &&
    rendererFailure.Message.Contains("No space left on device", StringComparison.Ordinal) &&
    rendererFailure.Message.IndexOf("parse puppet failed", StringComparison.Ordinal) < rendererFailure.Message.IndexOf("script warning", StringComparison.Ordinal) &&
    !rendererFailure.Message.Contains("literal ERROR in an ordinary message", StringComparison.Ordinal) &&
    rendererFailure.Message.Split("parse puppet failed: models/1x1_puppet.mdl", StringSplitOptions.None).Length == 2,
    "renderer failure summary parses distinct timestamped stderr errors before JSON warnings");
void Reject(Action action, string name)
{
    try { action(); } catch (Exception error) when (error is InvalidDataException or EndOfStreamException or IOException) { passed.Add(name); return; }
    throw new InvalidOperationException("Accepted invalid input: " + name);
}
string MakePkg(string name, (string Path, byte[] Bytes)[] entries, int? offsetOverride = null, int version = 23)
{
    string dir = Path.Combine(root, name);
    Directory.CreateDirectory(dir);
    string path = Path.Combine(dir, "scene.pkg");
    using var output = new BinaryWriter(File.Create(path), Encoding.UTF8);
    void String(string text) { var bytes = Encoding.UTF8.GetBytes(text); output.Write(bytes.Length); output.Write(bytes); }
    String($"PKGV{version:D4}");
    output.Write(entries.Length);
    int offset = 0;
    foreach (var item in entries)
    {
        String(item.Path); output.Write(offsetOverride ?? offset); output.Write(item.Bytes.Length);
        offset += item.Bytes.Length;
    }
    foreach (var item in entries) output.Write(item.Bytes);
    return path;
}
byte[] scene = Encoding.UTF8.GetBytes("{\"objects\":[],\"general\":{}}");
byte[] binary = Enumerable.Range(0, 262_267).Select(i => (byte)(i * 197 + 11)).ToArray();
string valid = MakePkg("中文来源", [("scene.json", scene), ("materials/中文.bin", binary)]);
using (var source = new ProjectSource(valid))
{
    Check(source.PackageVersion == 23, "package version");
    Check(source.Read("MATERIALS/中文.bin").SequenceEqual(binary), "case-insensitive package resource bytes");
    Reject(() => source.Read("materials/中文.bin", 10), "metadata memory limit");
    string before = await source.SourceHashAsync();
    string destination = Path.Combine(root, "中文解包");
    await source.ExtractAsync(destination);
    Check(File.ReadAllBytes(Path.Combine(destination, "materials/中文.bin")).SequenceEqual(binary), "streamed extraction larger than buffer");
    Check(!File.Exists(Path.Combine(destination, "scene.pkg")), "editable project does not retain shadowing package");
    Check(before == await source.SourceHashAsync(), "source unchanged by extraction");
    using var extracted = new ProjectSource(destination);
    Check(extracted.ReadJson("scene.json")["objects"]!.AsArray().Count == 0, "extracted JSON readable");
    string first = await extracted.SourceHashAsync();
    File.AppendAllText(Path.Combine(destination, "materials/中文.bin"), "change");
    Check(first != await extracted.SourceHashAsync(), "directory hash covers dependent resource changes");
    try { await source.ExtractAsync(destination); throw new Exception("Overwrote existing destination"); }
    catch (IOException) { passed.Add("existing destination preserved"); }
    try { await source.ExtractAsync(Path.Combine(Path.GetDirectoryName(valid)!, "nested-output")); throw new Exception("Wrote inside source"); }
    catch (IOException) { passed.Add("source-contained destination rejected"); }
}
foreach (string name in new[] { "../outside", "/absolute", "C:/absolute", "a/../../escape", "a:stream", "CON", "x/NUL.txt", "a/./b", "trail. ", "a\\..\\b" })
{
    string path = MakePkg("bad-" + passed.Count, [(name, scene)]);
    Reject(() => { using var _ = new ProjectSource(path); }, "reject path " + name);
}
string version24 = MakePkg("version-24", [("scene.json", scene), ("materials/中文.bin", binary)], version: 24);
using (var source = new ProjectSource(version24))
{
    Check(source.PackageVersion == 24 && source.Read("materials/中文.bin").SequenceEqual(binary), "version 24 directory and payload read");
    string destination = Path.Combine(root, "version-24-extracted");
    await source.ExtractAsync(destination);
    Check(File.ReadAllBytes(Path.Combine(destination, "materials/中文.bin")).SequenceEqual(binary), "version 24 streamed extraction");
}
string bad24 = MakePkg("version-24-bad-range", [("scene.json", scene)], int.MaxValue, version: 24);
Reject(() => { using var _ = new ProjectSource(bad24); }, "version 24 retains payload bounds validation");
string identicalDuplicate = MakePkg("identical-duplicate", [("scene.json", scene), ("fonts/字形.bin", binary), ("fonts/字形.bin", binary)], version: 24);
using (var source = new ProjectSource(identicalDuplicate))
{
    Check(source.Read("fonts/字形.bin").SequenceEqual(binary), "identical duplicate resource retains exact bytes");
    string destination = Path.Combine(root, "identical-duplicate-extracted");
    await source.ExtractAsync(destination);
    Check(File.ReadAllBytes(Path.Combine(destination, "fonts/字形.bin")).SequenceEqual(binary), "identical duplicate extracts to one Windows path");
}
string duplicate = MakePkg("duplicate", [("a", scene), ("A", binary)]);
Reject(() => { using var _ = new ProjectSource(duplicate); }, "conflicting case-folded duplicate rejected");
byte[] changedBinary = binary.ToArray(); changedBinary[^1] ^= 1;
string sameSizeDuplicate = MakePkg("same-size-duplicate", [("a", binary), ("a", changedBinary)]);
Reject(() => { using var _ = new ProjectSource(sameSizeDuplicate); }, "same-sized conflicting duplicate rejected");
string noLoopSource = MakePkg("no-loop-source", [("scene.json", Encoding.UTF8.GetBytes("{\"objects\":[{\"id\":1},{\"id\":2}],\"general\":{}}"))]);
using (var source = new ProjectSource(noLoopSource))
{
    string nested = Path.Combine(source.DirectoryPath, "nested-hybrid");
    var captureSettings = new HybridAnalyzeRequest(2, noLoopSource, root, nested, 16, 8);
    string noLoopRuntimeEvidence = Path.Combine(root, "no-loop-runtime-evidence.json");
    await File.WriteAllTextAsync(noLoopRuntimeEvidence, new JsonObject {
        ["source"] = noLoopSource, ["status"] = "complete", ["source_script_error_count"] = 0,
        ["source_script_errors"] = new JsonArray(), ["runtime_dependencies"] = new JsonArray(),
        ["runtime_layers"] = new JsonArray(
            new JsonObject { ["id"] = 1, ["owner"] = 1, ["visible"] = true, ["has_mesh"] = true,
                ["effective_parallax_depth"] = new JsonArray(0, 0), ["materials"] = new JsonArray(new JsonObject {
                    ["uses_audio_spectrum"] = false, ["uses_system_media_thumbnail"] = false,
                    ["active_uniforms"] = new JsonArray(), ["textures"] = new JsonArray() }) },
            new JsonObject { ["id"] = 2, ["owner"] = 2, ["visible"] = true, ["has_mesh"] = true,
                ["effective_parallax_depth"] = new JsonArray(0, 0), ["materials"] = new JsonArray(new JsonObject {
                    ["uses_audio_spectrum"] = false, ["uses_system_media_thumbnail"] = false,
                    ["active_uniforms"] = new JsonArray(), ["textures"] = new JsonArray() }) }),
        ["runtime_animation_periods"] = new JsonArray(new JsonObject {
            ["source_owner_layer_id"] = 1, ["mechanism"] = "video", ["track_name"] = "unknown-video",
            ["looping"] = true, ["event_driven"] = false, ["playback_rate"] = 1 }) }.ToJsonString());
    var noLoopPlan = new JsonObject { ["schema_version"] = 2, ["kind"] = "hybrid_video", ["source"] = noLoopSource,
        ["source_sha256"] = await source.SourceHashAsync(), ["settings"] = JsonSerializer.SerializeToNode(captureSettings,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }),
        // The saved observed cut is deliberately stale: BakeAsync must derive the unresolved video again from source and trace.
        ["loop"] = new JsonObject { ["status"] = "observed", ["candidates"] = new JsonArray(new JsonObject { ["frames"] = 600 }) },
        ["video_groups"] = new JsonArray(new JsonObject { ["layer_ids"] = new JsonArray(1), ["include_scene_clear"] = true }),
        ["layers"] = new JsonArray(new JsonObject { ["id"] = 1, ["root"] = 1 }, new JsonObject { ["id"] = 2, ["root"] = 2 }),
        ["snapshot_properties"] = new JsonObject(), ["runtime_evidence"] = noLoopRuntimeEvidence,
        ["source_script_error_evidence"] = new JsonObject { ["status"] = "available" },
        ["source_script_error_count"] = 0, ["source_script_errors"] = new JsonArray() };
    try
    {
        await new HybridBakeService(new("not-started", "not-started", "not-started", [])).BakeAsync(new(2, noLoopPlan, nested));
        throw new InvalidOperationException("Accepted source-contained hybrid output.");
    }
    catch (IOException)
    {
        Check(!Directory.Exists(nested) && !Directory.Exists(nested + ".loop-analysis"), "hybrid source containment checked before loop-analysis writes");
    }
    string publishWork = Path.Combine(root, "publish-work");
    foreach (string blockedDestination in new[] { source.DirectoryPath, Path.Combine(source.DirectoryPath, "published"), publishWork })
    {
        try
        {
            await new HybridBakeService(new("not-started", "not-started", "not-started", []))
                .BakeAsync(new(2, noLoopPlan, publishWork, ProjectDirectory: blockedDestination));
            throw new InvalidOperationException("Accepted overlapping publication destination.");
        }
        catch (IOException) { }
    }
    Check(!Directory.Exists(publishWork), "publication destinations cannot overlap source or working trees");
    string existingProject = Path.Combine(root, "existing-published-project");
    Directory.CreateDirectory(existingProject);
    await File.WriteAllTextAsync(Path.Combine(existingProject, "keep.txt"), "unchanged");
    try
    {
        await new HybridBakeService(new("not-started", "not-started", "not-started", []))
            .BakeAsync(new(2, noLoopPlan, publishWork, ProjectDirectory: existingProject));
        throw new InvalidOperationException("Overwrote existing published project.");
    }
    catch (IOException) { }
    Check(File.ReadAllText(Path.Combine(existingProject, "keep.txt")) == "unchanged" && !Directory.Exists(publishWork),
        "existing wallpaper destination is preserved before starting work");
    string rejectedOutput = Path.Combine(root, "no-loop-result");
    string rejectedDestination = Path.Combine(root, "must-not-publish-rejection");
    JsonObject rejected = await new HybridBakeService(new("not-started", "not-started", "not-started", []))
        .BakeAsync(new(2, noLoopPlan, rejectedOutput, ProjectDirectory: rejectedDestination));
    Check(rejected["status"]?.GetValue<string>() == "candidate_rejected_no_loop" &&
        File.Exists(Path.Combine(rejectedOutput, "bake.json")) && !Directory.Exists(Path.Combine(rejectedOutput, "project")),
        "a stale observed loop is re-parsed as unresolved without indexing or rendering");
    Check(!Directory.Exists(rejectedDestination), "rejected candidates are not published into wallpaper storage");
    var fallbackPlan = noLoopPlan.DeepClone().AsObject();
    fallbackPlan["video_groups"]![0]!["layer_ids"] = new JsonArray(1, 2);
    fallbackPlan["loop"]!["unresolved"] = new JsonArray(new JsonObject { ["owner_layer_id"] = 1 });
    string fallbackOutput = Path.Combine(root, "fallback-missing-renderer");
    JsonObject stoppedBake = await new HybridBakeService(new("not-started", "not-started", "not-started", []))
        .BakeAsync(new(2, fallbackPlan, fallbackOutput));
    Check(stoppedBake["status"]!.GetValue<string>() == "candidate_rejected_no_loop" &&
        !File.Exists(Path.Combine(fallbackOutput, "before-loop-allocation.json")) &&
        !Directory.Exists(Path.Combine(fallbackOutput, "loop-allocation-analysis")),
        "a rejected bake returns its original reason without launching a renderer or a second allocation attempt");
}
string outside = MakePkg("offset", [("scene.json", scene)], int.MaxValue);
Reject(() => { using var _ = new ProjectSource(outside); }, "out-of-bounds payload rejected");
string truncated = MakePkg("truncated", [("scene.json", scene)]);
using (var file = new FileStream(truncated, FileMode.Open, FileAccess.Write)) file.SetLength(14);
Reject(() => { using var _ = new ProjectSource(truncated); }, "truncated directory rejected");
var report = await SceneAnalyzer.AnalyzeAsync(valid);
Check(report.Status == "analyzed_not_validated" && report.ObjectCount == 0, "analysis never implies playback or performance acceptance");
string overflowTexture = Path.Combine(root, "overflow.tex");
try
{
    await TextureContainer.WriteRgbaAsync(overflowTexture, 2147483648, 2147483648, ReadOnlyMemory<byte>.Empty);
    throw new Exception("Accepted overflowing texture extent");
}
catch (ArgumentException) { Check(!File.Exists(overflowTexture), "overflowing RGBA extent rejected before file creation"); }
string custom = Path.Combine(root, "custom-entry");
Directory.CreateDirectory(custom);
string otherScene = "{\"objects\":[{\"id\":1},{\"id\":7}]}";
await File.WriteAllTextAsync(Path.Combine(custom, "scene.json"), otherScene);
await File.WriteAllTextAsync(Path.Combine(custom, "project.json"), "{\"type\":\"scene\",\"file\":\"alternate.json\",\"title\":\"test\"}");
await File.WriteAllTextAsync(Path.Combine(custom, "alternate.json"), "{\"objects\":[{\"id\":1,\"effects\":[{\"id\":2,\"file\":\"unused.json\",\"passes\":[],\"visible\":true}]}]}");
using (var source = new ProjectSource(custom))
{
    Check(source.SceneResource == "alternate.json", "project descriptor selects custom scene entry");
    string fingerprint = await source.SourceHashAsync();
    string shaderCache = Path.Combine(custom, "shaders", "blobsSM40");
    Directory.CreateDirectory(shaderCache);
    await File.WriteAllTextAsync(Path.Combine(shaderCache, "runtime.bin"), "WPE playback cache");
    Check(fingerprint == await source.SourceHashAsync(), "ordinary WPE shader cache does not invalidate source plans");
    var analysis = await SceneAnalyzer.AnalyzeAsync(custom);
    Check(analysis.ObjectCount == 1, "analysis follows custom entry rather than adjacent scene.json");
}
// Different identities and a synthetic trace exercise planning without starting a renderer.
string groupingSource = Path.Combine(root, "grouping-source");
Directory.CreateDirectory(groupingSource);
var groupingObjects = new JsonArray(
    new JsonObject { ["id"] = 901, ["image"] = "models/util/fullscreenlayer.json" },
    new JsonObject { ["id"] = 902, ["visible"] = false },
    new JsonObject { ["id"] = 903, ["parent"] = 902, ["text"] = new JsonObject { ["script"] = "export function update() { return new Date(); }" } },
    new JsonObject { ["id"] = 904, ["text"] = "background" },
    new JsonObject { ["id"] = 905, ["particle"] = "random.json" },
    new JsonObject { ["id"] = 906, ["text"] = "foreground occluder" },
    new JsonObject { ["id"] = 907, ["particle"] = "random.json" },
    new JsonObject { ["id"] = 910, ["name"] = "visible date component" },
    new JsonObject { ["id"] = 911, ["parent"] = 910, ["visible"] = false,
        ["text"] = new JsonObject { ["script"] = "export function update() { return new Date(); }" } },
    new JsonObject { ["id"] = 912, ["parent"] = 910,
        ["text"] = new JsonObject { ["script"] = "export function update() { return new Date(); }" } },
    new JsonObject { ["id"] = 908, ["text"] = new JsonObject { ["script"] = "export function update() { return new Date(); }" } },
    new JsonObject { ["id"] = 909, ["particle"] = "pointer.json" });
await File.WriteAllTextAsync(Path.Combine(groupingSource, "scene.json"), new JsonObject {
    ["general"] = new JsonObject { ["orthogonalprojection"] = new JsonObject { ["width"] = 64, ["height"] = 32 }, ["clearenabled"] = true },
    ["objects"] = groupingObjects }.ToJsonString());
await File.WriteAllTextAsync(Path.Combine(groupingSource, "random.json"), "{\"emitter\":[{\"name\":\"sphererandom\"}]}");
await File.WriteAllTextAsync(Path.Combine(groupingSource, "pointer.json"), "{\"controlpoint\":[{\"flags\":1,\"id\":0}]}");
string groupingTrace = Path.Combine(root, "grouping-trace.json");
await File.WriteAllTextAsync(groupingTrace, new JsonObject {
    ["source"] = Path.Combine(groupingSource, "scene.json"), ["status"] = "complete",
    ["source_script_error_count"] = 0, ["source_script_errors"] = new JsonArray(),
    ["runtime_dependencies"] = new JsonArray(),
    ["runtime_layers"] = new JsonArray(groupingObjects.OfType<JsonObject>().Select(obj => (JsonNode)new JsonObject {
        ["id"] = obj["id"]!.DeepClone(), ["owner"] = obj["id"]!.DeepClone(),
        ["visible"] = obj["visible"]?.DeepClone() ?? JsonValue.Create(true),
        ["has_mesh"] = obj["id"]!.GetValue<int>() != 902,
        ["effective_parallax_depth"] = new JsonArray(0, 0),
        ["materials"] = new JsonArray(new JsonObject { ["uses_audio_spectrum"] = false,
            ["textures"] = obj["id"]!.GetValue<int>() == 901 ? new JsonArray("_rt_default") : new JsonArray() }) }).ToArray()) }.ToJsonString());
var groupingPlan = await new HybridScenePlanner(new("not-started", "not-started", "not-started", [])).AnalyzeSingleAsync(
    new(2, groupingSource, root, Path.Combine(root, "grouping-analysis"), 64, 32,
        RuntimeTraceFile: groupingTrace));
var grouped = groupingPlan["video_groups"]!.AsArray();
Check(grouped.Count == 1 && grouped[0]!["include_scene_clear"]!.GetValue<bool>() &&
    !grouped[0]!["transparent"]!.GetValue<bool>(),
    "identity framebuffer and hidden live hierarchy allow one opaque base group");
Check(grouped[0]!["root_ids"]!.AsArray().Select(n => n!.GetValue<int>()).SequenceEqual(new[] { 904, 905, 906, 907 }) &&
    groupingPlan["optional_realtime_roots"]!.AsArray().Select(n => n!.GetValue<int>()).SequenceEqual(new[] { 905, 906, 907 }),
    "particle and fixed text occluder are offered only as one foreground suffix, with no extra video group");
Check(groupingPlan["live_layer_ids"]!.AsArray().Any(n => n!.GetValue<int>() == 909),
    "native controlpoint mouse flag is retained as live interaction");
Check(groupingPlan["omitted_snapshot_layer_ids"]!.AsArray().Select(n => n!.GetValue<int>()).SequenceEqual(new[] { 902, 903, 911 }) &&
    groupingPlan["live_layer_ids"]!.AsArray().Any(n => n!.GetValue<int>() == 912) &&
    !groupingPlan["live_layer_ids"]!.AsArray().Any(n => n!.GetValue<int>() == 911),
    "fixed invisible self-contained root and child subtrees are omitted without removing the visible style");
Check(groupingPlan["settings"]!["video_layout"]!.GetValue<string>() == "full_frame" &&
    groupingPlan["video_layout_admission"]!["status"]!.GetValue<string>() == "planned_layout_allowed",
    "ordinary compatible settings keep the full-frame default without changing live layers");
Check(groupingPlan["schema_version"]!.GetValue<int>() == HybridPlanFormat.CurrentVersion,
    "newly analyzed subtree plans advertise version 3");
Check(groupingPlan["source_root_order"]!.AsArray().Select(node => node!.GetValue<int>()).SequenceEqual(
        new[] { 901, 902, 904, 905, 906, 907, 910, 908, 909 }) &&
    groupingPlan["root_order"]!.AsArray().Select(node => node!.GetValue<int>()).SequenceEqual(
        new[] { 901, 902, 903, 904, 905, 906, 907, 910, 908, 909 }) &&
    groupingPlan["root_roles"]!.AsArray().OfType<JsonObject>().Single(role => role["root_id"]!.GetValue<int>() == 903)
        ["role"]!.GetValue<string>() == "omitted_snapshot" &&
    groupingPlan["root_roles"]!.AsArray().OfType<JsonObject>().Single(role => role["root_id"]!.GetValue<int>() == 906)
        ["role"]!.GetValue<string>() == "video",
    "plan preserves author-root order and separately records split allocation order and roles");
var allocationMethod = typeof(HybridScenePlanner).GetMethod("ApplyAllocation",
    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
JsonObject Allocate(JsonObject plan, int[] roots, JsonArray dependencies) =>
    (JsonObject)allocationMethod.Invoke(null, new object[] { plan, roots, dependencies })!;
JsonObject foreground = Allocate(groupingPlan, [905], new JsonArray());
JsonObject foregroundGroup = foreground["video_groups"]!.AsArray().Single()!.AsObject();
int[] foregroundOrder = foreground["composition"]!.AsArray().OfType<JsonObject>()
    .Where(entry => entry["live_root"] is JsonValue value && value.GetValue<int>() is >= 905 and <= 907)
    .Select(entry => entry["live_root"]!.GetValue<int>()).ToArray();
Check(foregroundGroup["root_ids"]!.AsArray().Select(node => node!.GetValue<int>()).SequenceEqual(new[] { 904 }) &&
    foregroundOrder.SequenceEqual(new[] { 905, 906, 907 }) &&
    foreground["live_layer_ids"]!.AsArray().Any(node => node!.GetValue<int>() == 906),
    "foreground allocation retains the complete suffix including a static companion");
Check(foreground["live_layer_ids"]!.AsArray().Any(node => node!.GetValue<int>() == 908) &&
    foreground["live_layer_ids"]!.AsArray().Any(node => node!.GetValue<int>() == 909) &&
    JsonNode.DeepEquals(foregroundGroup["parallax_depth"], grouped[0]!["parallax_depth"]) &&
    foregroundGroup["include_scene_clear"]!.GetValue<bool>(),
    "foreground allocation preserves mandatory live roots, group depth and scene clear");
JsonObject dependentForeground = Allocate(groupingPlan, [905], new JsonArray(
    new JsonObject { ["owner"] = 905, ["target"] = 904, ["operation"] = "write", ["property"] = "visible", ["initialization"] = false }));
Check(dependentForeground["live_layer_ids"]!.AsArray().Any(node => node!.GetValue<int>() == 904) &&
    dependentForeground["layers"]!.AsArray().OfType<JsonObject>().Single(layer => layer["id"]!.GetValue<int>() == 904)
        ["reasons"]!.AsArray().Any(reason => reason!.GetValue<string>() == "required_by_foreground_dependency") &&
    dependentForeground["allocation"]!["status"]!.GetValue<string>() == "requires_resolution" &&
    dependentForeground["blockers"]!.AsArray().Count > 0,
    "new live controllers close cross-root writes and report loss of the opaque video");
try
{
    _ = Allocate(groupingPlan, [904], new JsonArray());
    throw new InvalidOperationException("Accepted a non-optional foreground allocation root.");
}
catch (System.Reflection.TargetInvocationException error) when (error.InnerException is InvalidDataException)
{
    passed.Add("invalid foreground allocation root rejected");
}
// Static author containers may share their transform without sharing one allocation decision.
string subtreeSource = Path.Combine(root, "subtree-source");
Directory.CreateDirectory(subtreeSource);
var subtreeObjects = new JsonArray(
    new JsonObject { ["id"] = 1200, ["name"] = "author container", ["origin"] = "31 17 0", ["scale"] = "2 3 1" },
    new JsonObject { ["id"] = 1201, ["parent"] = 1200, ["text"] = "body" },
    new JsonObject { ["id"] = 1203, ["parent"] = 1202, ["origin"] = "4 5 0", ["text"] = new JsonObject { ["script"] = "export function update() { return new Date(); }" } },
    new JsonObject { ["id"] = 1204, ["parent"] = 1200, ["text"] = "body foreground" },
    new JsonObject { ["id"] = 1202, ["parent"] = 1200, ["name"] = "clock container", ["origin"] = "3 4 2", ["scale"] = "0.5 -2 1", ["angles"] = "0 0 0" },
    new JsonObject { ["id"] = 1205, ["parent"] = 1202, ["text"] = new JsonObject { ["script"] = "export function update() { return Date.now(); }" } },
    new JsonObject { ["id"] = 1206, ["parent"] = 1201, ["text"] = "body child" },
    new JsonObject { ["id"] = 1207, ["parent"] = 1200, ["visible"] = false, ["text"] = "omitted fixed branch" });
async Task<JsonObject> PlanSubtrees(string name, JsonArray objects, int[]? retained = null, JsonArray? dependencies = null,
    string placement = "preserve", string layout = "full_frame")
{
    await File.WriteAllTextAsync(Path.Combine(subtreeSource, "scene.json"), new JsonObject {
        ["general"] = new JsonObject { ["orthogonalprojection"] = new JsonObject { ["width"] = 64, ["height"] = 32 }, ["clearenabled"] = true },
        ["objects"] = objects.DeepClone() }.ToJsonString());
    string tracePath = Path.Combine(root, name + "-trace.json");
    await File.WriteAllTextAsync(tracePath, new JsonObject {
        ["source"] = Path.Combine(subtreeSource, "scene.json"), ["status"] = "complete",
        ["source_script_error_count"] = 0, ["source_script_errors"] = new JsonArray(),
        ["runtime_dependencies"] = dependencies?.DeepClone() ?? new JsonArray(),
        ["runtime_layers"] = new JsonArray(objects.OfType<JsonObject>().Select(obj => (JsonNode)new JsonObject {
            ["id"] = obj["id"]!.DeepClone(), ["owner"] = obj["id"]!.DeepClone(),
            ["visible"] = obj["visible"]?.DeepClone() ?? JsonValue.Create(true), ["has_mesh"] = obj.ContainsKey("text"),
            ["effective_parallax_depth"] = new JsonArray(0, 0),
            ["materials"] = new JsonArray(new JsonObject { ["uses_audio_spectrum"] = false }) }).ToArray()) }.ToJsonString());
    return await new HybridScenePlanner(new("not-started", "not-started", "not-started", [])).AnalyzeSingleAsync(
        new(2, subtreeSource, root, Path.Combine(root, name), 64, 32, RuntimeTraceFile: tracePath,
            RetainLiveRootIds: retained, LiveOverlayPlacement: placement, VideoLayout: layout));
}
var subtreePlan = await PlanSubtrees("subtree-static", subtreeObjects);
Check(subtreePlan["video_groups"]!.AsArray().Single()!["layer_ids"]!.AsArray().Select(n => n!.GetValue<int>())
        .SequenceEqual(new[] { 1201, 1204, 1206 }) &&
    subtreePlan["live_layer_ids"]!.AsArray().Select(n => n!.GetValue<int>()).SequenceEqual(new[] { 1203, 1205 }),
    "static shared ancestors permit separate baked bodies and live child allocations");
Check(subtreePlan["source_root_order"]!.AsArray().Single()!.GetValue<int>() == 1200 &&
    subtreePlan["layers"]!.AsArray().OfType<JsonObject>().All(layer => layer["root"]!.GetValue<int>() == 1200) &&
    subtreePlan["root_order"]!.AsArray().Select(n => n!.GetValue<int>()).SequenceEqual(new[] { 1200, 1201, 1204, 1202, 1203, 1205, 1207 }) &&
    subtreePlan["layers"]!.AsArray().OfType<JsonObject>().Single(layer => layer["id"]!.GetValue<int>() == 1206)["allocation_root"]!.GetValue<int>() == 1201,
    "author roots remain facts while allocation order follows native sibling DFS and drawable subtrees remain whole");
var assembleMethod = typeof(HybridBakeService).GetMethod("AssembleObjects", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
var projectionType = typeof(HybridBakeService).Assembly.GetType("Baker.Core.HybridVideoProjection")!;
var attachMethod = projectionType.GetMethod("AttachToParent", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
JsonArray Assemble(JsonObject plan, JsonArray objects, JsonArray? dependencies = null)
{
    int replacementId = 1500;
    var replacements = new Dictionary<string, JsonObject>();
    foreach (var group in plan["video_groups"]!.AsArray().OfType<JsonObject>())
    {
        var replacement = new JsonObject { ["id"] = replacementId++, ["origin"] = "32 16 0", ["scale"] = "4 6 1", ["image"] = "world-video" };
        attachMethod.Invoke(null, new object[] { replacement, group });
        replacements.Add(group["id"]!.GetValue<string>(), replacement);
    }
    return (JsonArray)assembleMethod.Invoke(null, new object[] {
        objects.OfType<JsonObject>().ToDictionary(obj => obj["id"]!.GetValue<int>()), plan, replacements,
        dependencies ?? new JsonArray() })!;
}
JsonObject PublicLayerPlan(JsonArray composition, JsonArray live, JsonArray layers, JsonArray? groups = null) => new()
{
    ["layers"] = layers,
    ["live_layer_ids"] = live,
    ["omitted_snapshot_layer_ids"] = new JsonArray(),
    ["video_groups"] = groups ?? new JsonArray(),
    ["composition"] = composition
};
JsonObject PublicLayer(int id) => new() { ["id"] = id, ["root"] = id, ["allocation_root"] = id, ["drawable"] = true };
JsonObject LayerQuery(int owner, string property) => new() { ["owner"] = owner, ["target"] = -1,
    ["operation"] = "query", ["property"] = property, ["binding"] = "thisScene", ["initialization"] = false };
void RejectPublicLayerExport(Action action, string name)
{
    try { action(); }
    catch (System.Reflection.TargetInvocationException error)
        when (error.InnerException is InvalidDataException inner && inner.Message.Contains("public layer", StringComparison.Ordinal))
        { passed.Add(name); return; }
    throw new InvalidOperationException("Accepted public-layer-incompatible export: " + name);
}
var publicObjects = new JsonArray(
    new JsonObject { ["id"] = 1900, ["script"] = "export function update() {}" },
    new JsonObject { ["id"] = 1901, ["name"] = "visible" },
    new JsonObject { ["id"] = 1902, ["name"] = "hidden", ["visible"] = false });
var reorderedPublicPlan = PublicLayerPlan(new JsonArray(new JsonObject { ["live_root"] = 1901 },
    new JsonObject { ["live_root"] = 1900 }, new JsonObject { ["live_root"] = 1902 }), new JsonArray(1900, 1901, 1902),
    new JsonArray(PublicLayer(1900), PublicLayer(1901), PublicLayer(1902)));
Check(Assemble(reorderedPublicPlan, publicObjects, new JsonArray(LayerQuery(1900, "layer_count"))).Count == 3,
    "count-only numeric layer query allows same-count public-order changes");
RejectPublicLayerExport(() => Assemble(reorderedPublicPlan, publicObjects, new JsonArray(LayerQuery(1900, "layer_enumeration"))),
    "numeric public layer enumeration rejects same-count reordered export");
RejectPublicLayerExport(() => Assemble(reorderedPublicPlan, publicObjects, new JsonArray(LayerQuery(1900, "layer_index"))),
    "numeric public layer index rejects same-count reordered export");
var shellObjects = new JsonArray(
    new JsonObject { ["id"] = 1910, ["image"] = "baked-away", ["script"] = "export function update() {}" },
    new JsonObject { ["id"] = 1911, ["parent"] = 1910, ["text"] = "live" },
    new JsonObject { ["id"] = 1912, ["image"] = "baked" });
var shellPlan = PublicLayerPlan(new JsonArray(new JsonObject { ["live_root"] = 1911 }, new JsonObject { ["video_group"] = "shell-video" }),
    new JsonArray(1911), new JsonArray(PublicLayer(1910), PublicLayer(1911), PublicLayer(1912)),
    new JsonArray(new JsonObject { ["id"] = "shell-video", ["layer_ids"] = new JsonArray(1912) }));
RejectPublicLayerExport(() => Assemble(shellPlan, shellObjects, new JsonArray(LayerQuery(1910, "layer_order"))),
    "retained empty-shell script still guards numeric public layer order");
var deletedScriptPlan = PublicLayerPlan(new JsonArray(new JsonObject { ["live_root"] = 1901 }), new JsonArray(1901),
    new JsonArray(PublicLayer(1900), PublicLayer(1901), PublicLayer(1902)));
Check(Assemble(deletedScriptPlan, publicObjects, new JsonArray(LayerQuery(1900, "layer_numeric_index"))).Count == 1,
    "deleted script owner no longer blocks an unrelated public layer change");
var addedVideoPlan = PublicLayerPlan(new JsonArray(new JsonObject { ["live_root"] = 1900 }, new JsonObject { ["video_group"] = "added-video" }),
    new JsonArray(1900), new JsonArray(PublicLayer(1900)),
    new JsonArray(new JsonObject { ["id"] = "added-video", ["layer_ids"] = new JsonArray(999) }));
Check(Assemble(addedVideoPlan, new JsonArray(publicObjects[0]!.DeepClone()),
    new JsonArray(new JsonObject { ["owner"] = 1900, ["operation"] = "lookup", ["property"] = "name" })).Count == 2,
    "name lookup allows a public layer count change");
RejectPublicLayerExport(() => Assemble(addedVideoPlan, new JsonArray(publicObjects[0]!.DeepClone()),
    new JsonArray(LayerQuery(1900, "layer_count"))), "added video rejects a numeric public layer count query");
var mergeDependencies = typeof(HybridBakeService).GetMethod("MergeRuntimeDependencies", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
var mergedLateQueries = (JsonArray)mergeDependencies.Invoke(null, new object[] { new JsonArray(LayerQuery(1900, "lookup")),
    new JsonArray(LayerQuery(1900, "layer_enumeration"), LayerQuery(1900, "layer_enumeration")) })!;
Check(mergedLateQueries.Count == 2, "full-capture dependency merge preserves old dependencies and de-duplicates repeated late trace events");
RejectPublicLayerExport(() => Assemble(reorderedPublicPlan, publicObjects, mergedLateQueries),
    "late full-capture numeric layer query reaches the final public-layer guard");
var compareLookupMethod = typeof(CandidateValidation).GetMethod("CompareLookupBindings", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
JsonObject Lookup(int target, string binding = "origin") => new() { ["owner"] = 1900, ["target"] = target,
    ["operation"] = "lookup", ["property"] = "1", ["binding"] = binding, ["initialization"] = false };
JsonObject CompareLookups(JsonArray? a, JsonArray? b, JsonArray? final = null) => (JsonObject)compareLookupMethod.Invoke(null,
    new object?[] { publicObjects, final ?? publicObjects, a, b })!;
Check(CompareLookups(new JsonArray(Lookup(1901), Lookup(1901)), new JsonArray(Lookup(1901)))["status"]!.GetValue<string>() == "observed_lookups_match",
    "paired lookup comparison ignores repeat counts and preserves numeric-name ambiguity as a binding key");
Check(CompareLookups(new JsonArray(Lookup(1901)), new JsonArray(Lookup(1902)))["status"]!.GetValue<string>() == "rejected_lookup_bindings",
    "paired lookup comparison rejects a retained script binding to a different authored target");
Check(CompareLookups(new JsonArray(Lookup(1901)), new JsonArray())["status"]!.GetValue<string>() == "rejected_lookup_bindings" &&
    CompareLookups(new JsonArray(), new JsonArray(Lookup(1901)))["status"]!.GetValue<string>() == "rejected_lookup_bindings",
    "paired lookup comparison rejects missing or newly executed retained bindings");
Check(CompareLookups(new JsonArray(Lookup(1901)), new JsonArray(Lookup(1901, "visible")))["mismatches"]!.AsArray().Count == 2,
    "paired lookup comparison keeps script properties separate");
Check(CompareLookups(new JsonArray(Lookup(1901)), new JsonArray(), new JsonArray(publicObjects[1]!.DeepClone()))["status"]!.GetValue<string>() == "observed_lookups_match",
    "paired lookup comparison excludes removed script owners");
Check(CompareLookups(null, new JsonArray())["status"]!.GetValue<string>() == "not_available",
    "missing paired runtime trace is unavailable instead of a vacuous lookup pass");
Check(CompareLookups(new JsonArray(), new JsonArray(LayerQuery(1900, "layer_count")),
    new JsonArray(publicObjects[0]!.DeepClone()))["status"]!.GetValue<string>() == "rejected_public_layer_queries",
    "a public layer query first observed in paired rendering reaches the same export guard");
Check(HybridCompositionValidator.Evaluate(new JsonObject { ["lookup_binding_validation"] = CompareLookups(new JsonArray(Lookup(1901)), new JsonArray(Lookup(1902))) })
    ["failures"]!.AsArray().Any(node => node!.GetValue<string>().Contains("lookup bindings")),
    "composition rejection includes retained lookup binding drift");
var exportSafetyType = typeof(HybridBakeService).Assembly.GetType("Baker.Core.HybridExportSafety", throwOnError: true)!;
var publicQueryGuard = exportSafetyType.GetMethod("GuardPublicLayerQueries", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
bool SelfAnchoredGuardAllows(string sourceScript, string? candidateScript = null, int? sourceParent = null, int? candidateParent = null,
    string property = "layer_index", bool initialization = true, string binding = "visible", string scriptBinding = "visible")
{
    JsonObject SourceOwner() => new() { ["id"] = 2000, ["parent"] = sourceParent, [scriptBinding] = new JsonObject { ["script"] = sourceScript } };
    JsonObject CandidateOwner() => new() { ["id"] = 2000, ["parent"] = candidateParent ?? sourceParent,
        [scriptBinding] = new JsonObject { ["script"] = candidateScript ?? sourceScript } };
    try
    {
        publicQueryGuard.Invoke(null, new object[] { new[] { SourceOwner(), new JsonObject { ["id"] = 2001 } },
            new[] { new JsonObject { ["id"] = 2001 }, CandidateOwner() },
            new JsonArray(new JsonObject { ["owner"] = 2000, ["target"] = -1, ["operation"] = "query", ["property"] = property,
                ["binding"] = binding, ["initialization"] = initialization }) });
        return true;
    }
    catch (System.Reflection.TargetInvocationException error) when (error.InnerException is InvalidDataException) { return false; }
}
string selfAnchored = """
    export function update(value) {
      if (value) {
        let baseOrigin = thisLayer.origin;
        let style = { alignment: scriptProperties.barAlignmentdir, z: baseOrigin.x / 2 };
        bars[0] = style;
        bars.push(thisLayer);
      }
    }
    export function init() {
      let initialAnchor = thisScene.getLayerIndex(thisLayer);
      let bars = [];
      for (let i = 0; i < 2; ++i) {
        let createdBar = thisScene.createLayer('unrelated/path.json');
        createdBar.alignment = scriptProperties.barAlignmentdir;
        createdBar.parallaxDepth = scriptProperties.depth;
        thisScene.sortLayer(createdBar, initialAnchor);
        bars.push(createdBar);
      }
      for (let i = 0; i < bars.length; ++i) {
        let createdBar = bars[i];
        createdBar.opacity = 1;
      }
    }
    """;
var selfAnchoredMethod = exportSafetyType.GetMethod("IsSelfAnchoredInsert", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
Check((bool)selfAnchoredMethod.Invoke(null, new object[] { selfAnchored })!,
    "self-anchored scanner accepts the complete common two-function script shape");
Check(SelfAnchoredGuardAllows(selfAnchored),
    "self-anchored init insertion permits a different ID, variable names, and resource path");
Check(SelfAnchoredGuardAllows("// eval, Proxy, and thisScene.sortLayer are comments\n" +
    selfAnchored.Replace("unrelated/path.json", "eval thisScene.getLayerIndex", StringComparison.Ordinal)),
    "self-anchored scanner ignores comments and quoted resource text");
Check(!SelfAnchoredGuardAllows(selfAnchored.Replace("initialAnchor);", "initialAnchor + 1);", StringComparison.Ordinal)),
    "self-anchored exception rejects index arithmetic");
Check(!SelfAnchoredGuardAllows(selfAnchored.Replace("createdBar.parallaxDepth", "if (true) { createdBar.parallaxDepth", StringComparison.Ordinal)
    .Replace("scriptProperties.depth;", "scriptProperties.depth; }", StringComparison.Ordinal)),
    "self-anchored exception rejects branches");
Check(!SelfAnchoredGuardAllows(selfAnchored.Replace("thisScene.sortLayer", "let escaped = initialAnchor; thisScene.sortLayer", StringComparison.Ordinal)),
    "self-anchored exception rejects anchor escape");
Check(!SelfAnchoredGuardAllows(selfAnchored.Replace("thisScene.sortLayer", "initialAnchor = 0; thisScene.sortLayer", StringComparison.Ordinal)),
    "self-anchored exception rejects anchor overwrite");
Check(!SelfAnchoredGuardAllows(selfAnchored.Replace("createdBar, initialAnchor", "existingLayer, initialAnchor", StringComparison.Ordinal)),
    "self-anchored exception rejects sorting an existing layer");
Check(!SelfAnchoredGuardAllows(selfAnchored.Replace("createdBar.alignment", "createdBar = other; createdBar.alignment", StringComparison.Ordinal)),
    "self-anchored exception rejects created-layer rebinding");
Check(!SelfAnchoredGuardAllows(selfAnchored.Replace("thisScene.sortLayer", "queueMicrotask(() => thisScene.sortLayer", StringComparison.Ordinal)),
    "self-anchored exception rejects nested callback syntax");
Check(!SelfAnchoredGuardAllows(selfAnchored, property: "layer_order", initialization: false) &&
    !SelfAnchoredGuardAllows(selfAnchored, property: "layer_numeric_index") &&
    !SelfAnchoredGuardAllows(selfAnchored, binding: "origin", scriptBinding: "visible"),
    "self-anchored exception remains limited to its init binding and layer index/order queries");
Check(!SelfAnchoredGuardAllows(selfAnchored, candidateScript: selfAnchored + " ") &&
    !SelfAnchoredGuardAllows(selfAnchored, candidateParent: 9),
    "self-anchored exception requires unchanged root binding script text and parent");
var subtreeExport = Assemble(subtreePlan, subtreeObjects, new JsonArray(new JsonObject { ["owner"] = 1203, ["target"] = 1206, ["operation"] = "lookup" }));
Check(subtreeExport.OfType<JsonObject>().Select(obj => obj["id"]!.GetValue<int>()).SequenceEqual(new[] { 1200, 1500, 1202, 1203, 1205, 1201, 1206 }) &&
    JsonNode.DeepEquals(subtreeExport[0], subtreeObjects[0]) && JsonNode.DeepEquals(subtreeExport[2], subtreeObjects[4]) &&
    subtreeExport[3]!["parent"]!.GetValue<int>() == 1202 && subtreeExport[1]!["parent"]!.GetValue<int>() == 1200 &&
    subtreeExport[1]!["origin"]!.GetValue<string>() == "0.5 -0.3333333333333333 0" && subtreeExport[1]!["scale"]!.GetValue<string>() == "2 2 1" &&
    subtreeExport[5]!["text"] is null && subtreeExport[6]!["parent"]!.GetValue<int>() == 1201,
    "export keeps live ancestor identity and transforms, retains lookup parent chains, and compensates the video parent transform exactly once");
var retainedSubtree = await PlanSubtrees("subtree-retained", subtreeObjects, [1200]);
Check(retainedSubtree["live_layer_ids"]!.AsArray().Count == subtreeObjects.Count &&
    retainedSubtree["video_groups"]!.AsArray().Count == 0 && retainedSubtree["omitted_snapshot_layer_ids"]!.AsArray().Count == 0,
    "explicit author-root retention covers every split descendant including hidden branches");
var dynamicSubtree = subtreeObjects.DeepClone().AsArray();
dynamicSubtree[0]!["origin"] = new JsonObject { ["script"] = "export function update() { return engine.runtime; }" };
var dynamicSubtreePlan = await PlanSubtrees("subtree-dynamic", dynamicSubtree);
Check(dynamicSubtreePlan["root_order"]!.AsArray().Single()!.GetValue<int>() == 1200 && dynamicSubtreePlan["video_groups"]!.AsArray().Count == 0,
    "scripted shared ancestors keep the original protected hierarchy");
var writtenSubtreePlan = await PlanSubtrees("subtree-written", subtreeObjects, dependencies: new JsonArray(
    new JsonObject { ["owner"] = 1203, ["target"] = 1200, ["operation"] = "write", ["property"] = "origin", ["initialization"] = false }));
Check(writtenSubtreePlan["root_order"]!.AsArray().Single()!.GetValue<int>() == 1200,
    "runtime writes to an otherwise static ancestor prevent splitting");
var textureAnimationSubtreePlan = await PlanSubtrees("subtree-texture-animation", subtreeObjects, dependencies: new JsonArray(
    new JsonObject { ["owner"] = 1203, ["target"] = 1201, ["operation"] = "read", ["property"] = "textureAnimation", ["initialization"] = true }));
Check(textureAnimationSubtreePlan["live_layer_ids"]!.AsArray().Any(node => node!.GetValue<int>() == 1201) &&
    textureAnimationSubtreePlan["layers"]!.AsArray().OfType<JsonObject>().Single(layer => layer["id"]!.GetValue<int>() == 1201)
        ["reasons"]!.AsArray().Any(reason => reason!.GetValue<string>() == "live_runtime_resource_dependency"),
    "live textureAnimation reads retain the target resource for later script updates");
var linkedCompositeObjects = new JsonArray(
    new JsonObject { ["id"] = 1300, ["visible"] = false, ["image"] = "mask-a" },
    new JsonObject { ["id"] = 1301, ["visible"] = false, ["image"] = "mask-b" },
    new JsonObject { ["id"] = 1302, ["image"] = "compositor",
        ["origin"] = new JsonObject { ["script"] = "export function update() { return new Date(); }", ["value"] = "0 0 0" } });
await File.WriteAllTextAsync(Path.Combine(subtreeSource, "scene.json"), new JsonObject {
    ["general"] = new JsonObject { ["orthogonalprojection"] = new JsonObject { ["width"] = 64, ["height"] = 32 }, ["clearenabled"] = true },
    ["objects"] = linkedCompositeObjects.DeepClone() }.ToJsonString());
string linkedCompositeTrace = Path.Combine(root, "linked-composite-trace.json");
await File.WriteAllTextAsync(linkedCompositeTrace, new JsonObject {
    ["source"] = Path.Combine(subtreeSource, "scene.json"), ["status"] = "complete",
    ["source_script_error_count"] = 0, ["source_script_errors"] = new JsonArray(), ["runtime_dependencies"] = new JsonArray(),
    ["runtime_layers"] = new JsonArray(
        new JsonObject { ["id"] = 1300, ["owner"] = 1300, ["visible"] = false, ["has_mesh"] = true,
            ["effective_parallax_depth"] = new JsonArray(0, 0), ["materials"] = new JsonArray(new JsonObject { ["uses_audio_spectrum"] = false, ["textures"] = new JsonArray() }) },
        new JsonObject { ["id"] = 1301, ["owner"] = 1301, ["visible"] = false, ["has_mesh"] = true,
            ["effective_parallax_depth"] = new JsonArray(0, 0), ["materials"] = new JsonArray(new JsonObject { ["uses_audio_spectrum"] = false, ["textures"] = new JsonArray() }) },
        new JsonObject { ["id"] = 1302, ["owner"] = 1302, ["visible"] = true, ["has_mesh"] = true,
            ["effective_parallax_depth"] = new JsonArray(0, 0), ["materials"] = new JsonArray(new JsonObject { ["uses_audio_spectrum"] = false,
                ["textures"] = new JsonArray("_rt_link_1300", "_rt_link_1301", "_rt_link_missing", "_rt_link_9999") }) }) }.ToJsonString());
var linkedCompositePlan = await new HybridScenePlanner(new("not-started", "not-started", "not-started", [])).AnalyzeSingleAsync(
    new(2, subtreeSource, root, Path.Combine(root, "linked-composite"), 64, 32, RuntimeTraceFile: linkedCompositeTrace));
var linkedCompositeRuntime = JsonNode.Parse(await File.ReadAllTextAsync(linkedCompositePlan["runtime_evidence"]!.GetValue<string>()))!.AsObject();
var linkedCompositeDependencies = linkedCompositeRuntime["runtime_dependencies"]!.AsArray()
    .OfType<JsonObject>().Where(dependency => dependency["property"]?.GetValue<string>() == "layerComposite").ToArray();
Check(linkedCompositePlan["live_layer_ids"]!.AsArray().Select(node => node!.GetValue<int>()).Order().SequenceEqual(new[] { 1300, 1301, 1302 }) &&
    !linkedCompositePlan["omitted_snapshot_layer_ids"]!.AsArray().Any(node => node!.GetValue<int>() is 1300 or 1301) &&
    linkedCompositeDependencies.Select(dependency => (dependency["owner"]!.GetValue<int>(), dependency["target"]!.GetValue<int>()))
        .Order().SequenceEqual(new[] { (1302, 1300), (1302, 1301) }),
    "live linked composites retain hidden producers while malformed or unknown links create no producer");
var unknownSubtree = subtreeObjects.DeepClone().AsArray();
unknownSubtree[0]!["unknown_parent_behavior"] = true;
Check((await PlanSubtrees("subtree-unknown", unknownSubtree))["root_order"]!.AsArray().Single()!.GetValue<int>() == 1200,
    "unknown ancestor behavior remains protected");
foreach (var (field, value) in new[] { ("angles", "0 0 0.3"), ("scale", "1 0 1") })
{
    var unsupportedParent = subtreeObjects.DeepClone().AsArray();
    unsupportedParent[0]![field] = value;
    Check((await PlanSubtrees("subtree-protected-" + field, unsupportedParent))["root_order"]!.AsArray().Single()!.GetValue<int>() == 1200,
        "rotated or singular ancestor remains protected: " + field);
}
var parentTransformMethod = projectionType.GetMethod("ParentTransform", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
var nestedParentTransform = (JsonObject)parentTransformMethod.Invoke(null, new object[] {
    subtreeObjects.OfType<JsonObject>().ToDictionary(obj => obj["id"]!.GetValue<int>()), 1202, new JsonObject() })!;
var mappedLayer = new JsonObject { ["origin"] = "32 16 0", ["scale"] = "4 6 1", ["parallaxDepth"] = "0.4 0.2" };
attachMethod.Invoke(null, new object[] { mappedLayer, new JsonObject { ["parent_id"] = 1202, ["parent_transform"] = nestedParentTransform } });
double[] mappedOrigin = mappedLayer["origin"]!.GetValue<string>().Split(' ').Select(value => double.Parse(value, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
double[] mappedScale = mappedLayer["scale"]!.GetValue<string>().Split(' ').Select(value => double.Parse(value, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
Check(Math.Abs(37 + mappedOrigin[0] - 32) < 1e-10 && Math.Abs(29 - 6 * mappedOrigin[1] - 16) < 1e-10 &&
    Math.Abs(2 + mappedOrigin[2]) < 1e-10 && mappedScale.SequenceEqual(new[] { 4d, -1d, 1d }) &&
    mappedLayer["parallaxDepth"]!.GetValue<string>() == "0.4 0.2",
    "nested translation and nonuniform negative scale preserve world geometry and parallax anchor without double transform");
string capturedPixel = Path.Combine(root, "captured-parent-color.rgba");
await File.WriteAllBytesAsync(capturedPixel, new byte[] { 20, 40, 60, 255 });
string capturedColorProject = Path.Combine(root, "captured-parent-color");
await VideoSceneBuilder.WriteLayerAsync(capturedColorProject, "color", capturedPixel, 1, 1, 1600, 32, 16, 64, 32,
    rgbaFrame: true, capturedColor: true);
string capturedColorShader = await File.ReadAllTextAsync(Path.Combine(capturedColorProject, "shaders/wpe_baker_video/color.frag"));
Check(capturedColorShader.Contains("texSample2D") && !capturedColorShader.Contains("g_Color") && !capturedColorShader.Contains("g_Alpha"),
    "parented capture uses the existing direct-color shader so inherited alpha and tint are not applied twice");
var subtreeAllocationInput = subtreePlan.DeepClone().AsObject();
subtreeAllocationInput["optional_realtime_roots"] = new JsonArray(1204);
var subtreeAllocation = Allocate(subtreeAllocationInput, [1204], new JsonArray());
Check(subtreeAllocation["video_groups"]!.AsArray().Single()!["root_ids"]!.AsArray().Single()!.GetValue<int>() == 1201 &&
    subtreeAllocation["live_layer_ids"]!.AsArray().Select(n => n!.GetValue<int>()).ToHashSet().SetEquals(new[] { 1203, 1204, 1205 }) &&
    subtreeAllocation["omitted_snapshot_layer_ids"]!.AsArray().Single()!.GetValue<int>() == 1207,
    "foreground allocation uses child units while retaining snapshot omissions and unrelated baked siblings");
var subtreeDependencyAllocation = Allocate(subtreeAllocationInput, [1204], new JsonArray(
    new JsonObject { ["owner"] = 1204, ["target"] = 1206, ["operation"] = "write", ["property"] = "visible", ["initialization"] = false }));
Check(subtreeDependencyAllocation["live_layer_ids"]!.AsArray().Any(n => n!.GetValue<int>() == 1201) &&
    subtreeDependencyAllocation["video_groups"]!.AsArray().Count == 0,
    "cross-unit writes promote the target's complete protected subtree");
var textureAnimationAllocation = Allocate(subtreeAllocationInput, [1204], new JsonArray(
    new JsonObject { ["owner"] = 1204, ["target"] = 1206, ["operation"] = "read", ["property"] = "textureAnimation", ["initialization"] = true }));
Check(textureAnimationAllocation["live_layer_ids"]!.AsArray().Any(node => node!.GetValue<int>() == 1201) &&
    textureAnimationAllocation["video_groups"]!.AsArray().Count == 0,
    "foreground allocation retains textureAnimation targets used by live scripts");
var linkedCompositeAllocation = Allocate(subtreeAllocationInput, [1204], new JsonArray(
    new JsonObject { ["owner"] = 1204, ["target"] = 1206, ["operation"] = "read", ["property"] = "layerComposite", ["initialization"] = false }));
Check(linkedCompositeAllocation["live_layer_ids"]!.AsArray().Any(node => node!.GetValue<int>() == 1201) &&
    linkedCompositeAllocation["video_groups"]!.AsArray().Count == 0,
    "foreground allocation retains linked composite producers used by live layers");
var interleavedSubtree = new JsonArray(subtreeObjects.Select(n => n!.DeepClone()).ToArray());
interleavedSubtree.Insert(2, new JsonObject { ["id"] = 1208, ["parent"] = 1200,
    ["text"] = new JsonObject { ["script"] = "export function update() { return Date.now(); }" } });
var interleavedSubtreePlan = await PlanSubtrees("subtree-interleaved", interleavedSubtree, layout: "layered");
Check(interleavedSubtreePlan["video_groups"]!.AsArray().Count == 2 && interleavedSubtreePlan["blockers"]!.AsArray().Count == 0 &&
    Assemble(interleavedSubtreePlan, interleavedSubtree).OfType<JsonObject>().Select(obj => obj["id"]!.GetValue<int>())
        .SequenceEqual(new[] { 1200, 1500, 1208, 1501, 1202, 1203, 1205 }),
    "shared static parent keeps interleaved videos and live branches in source sibling order");
var differentParents = subtreeObjects.DeepClone().AsArray();
foreach (var obj in differentParents.OfType<JsonObject>().Where(obj => obj["id"]!.GetValue<int>() is 1203 or 1205)) obj["text"] = "fixed nested drawing";
var differentParentPlan = await PlanSubtrees("subtree-different-parents", differentParents, layout: "layered");
Check(differentParentPlan["video_groups"]!.AsArray().OfType<JsonObject>().Select(group => group["parent_id"]!.GetValue<int>())
        .SequenceEqual(new[] { 1200, 1202 }) && differentParentPlan["blockers"]!.AsArray().Count == 0 &&
    Assemble(differentParentPlan, differentParents).OfType<JsonObject>().Select(obj => obj["id"]!.GetValue<int>())
        .SequenceEqual(new[] { 1200, 1500, 1202, 1501 }),
    "adjacent baked branches with different parents keep separate groups and their nested sibling positions");
var foregroundSubtree = new JsonArray(subtreeObjects[0]!.DeepClone(), subtreeObjects[1]!.DeepClone(),
    subtreeObjects[4]!.DeepClone(), subtreeObjects[2]!.DeepClone(), subtreeObjects[5]!.DeepClone(),
    subtreeObjects[3]!.DeepClone(), subtreeObjects[6]!.DeepClone(), subtreeObjects[7]!.DeepClone());
var foregroundSubtreePlan = await PlanSubtrees("subtree-foreground", foregroundSubtree, placement: "foreground");
var placementMethod = typeof(HybridScenePlanner).GetMethod("ApplyOverlayPlacement", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
var foregroundSubtreeScene = new JsonObject { ["objects"] = foregroundSubtree.DeepClone() };
placementMethod.Invoke(null, new object[] { foregroundSubtreeScene, foregroundSubtreePlan });
Check(foregroundSubtreePlan["video_groups"]!.AsArray().Count == 1 && foregroundSubtreePlan["blockers"]!.AsArray().Count == 0 &&
    foregroundSubtreeScene["objects"]!.AsArray().Last()!["id"]!.GetValue<int>() == 1202 &&
    foregroundSubtreeScene["objects"]!.AsArray().OfType<JsonObject>().Single(obj => obj["id"]!.GetValue<int>() == 1203)["parent"]!.GetValue<int>() == 1202,
    "explicit foreground placement moves the complete static ancestor branch without changing author parents");

JsonObject interleavedPlan = groupingPlan.DeepClone().AsObject();
interleavedPlan["schema_version"] = 2; // This fixture deliberately uses the legacy layer/root representation.
interleavedPlan["video_groups"]!.AsArray().Add(new JsonObject { ["id"] = "group-2", ["include_scene_clear"] = false });
interleavedPlan["composition"] = new JsonArray(new JsonObject { ["video_group"] = "group-1" },
    new JsonObject { ["live_root"] = 908 }, new JsonObject { ["video_group"] = "group-2" });
interleavedPlan["layers"] = new JsonArray(
    new JsonObject { ["id"] = 908, ["root"] = 908, ["name"] = "Visible overlay", ["visible"] = true, ["drawable"] = true },
    new JsonObject { ["id"] = 909, ["root"] = 908, ["name"] = "Hidden controller", ["visible"] = false, ["drawable"] = true },
    new JsonObject { ["id"] = 910, ["root"] = 908, ["name"] = "music.mp3", ["visible"] = true, ["drawable"] = false });
interleavedPlan["blockers"] = new JsonArray();
interleavedPlan["source"] = Path.Combine(root, "must-not-be-opened.pkg");
try
{
    await new HybridBakeService(new("must-not-run", "must-not-run", "must-not-run", []))
        .BakeAsync(new(2, interleavedPlan, Path.Combine(root, "must-not-be-generated")));
    throw new InvalidOperationException("A layered plan silently bypassed full-frame mode.");
}
catch (InvalidDataException error) when (error.Message.Contains("Full-frame mode", StringComparison.Ordinal)) { }
Check(!Directory.Exists(Path.Combine(root, "must-not-be-generated")),
    "a legacy or edited multigroup plan is rejected before opening source files or starting generation");
var layoutMethod = typeof(HybridScenePlanner).GetMethod("FullFrameConflict",
    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
string conflictNames = (string)layoutMethod.Invoke(null, [interleavedPlan])!;
Check(conflictNames.Contains("Visible overlay", StringComparison.Ordinal) &&
    !conflictNames.Contains("Hidden controller", StringComparison.Ordinal) && !conflictNames.Contains("music.mp3", StringComparison.Ordinal),
    "full-frame conflict names only visible drawable interleaved layers");
interleavedPlan["settings"]!["video_layout"] = "layered";
Check(layoutMethod.Invoke(null, [interleavedPlan]) is null,
    "layered composition requires explicit selection and retains its existing order");
async Task<JsonObject> PlanGroupingVariantAsync(string script, JsonArray? extraDependencies = null)
{
    JsonObject sceneVariant = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(groupingSource, "scene.json")))!.AsObject();
    sceneVariant["objects"]!.AsArray().OfType<JsonObject>().Single(o => o["id"]!.GetValue<int>() == 903)["text"] =
        new JsonObject { ["script"] = script };
    await File.WriteAllTextAsync(Path.Combine(groupingSource, "scene.json"), sceneVariant.ToJsonString());
    JsonObject traceVariant = JsonNode.Parse(await File.ReadAllTextAsync(groupingTrace))!.AsObject();
    traceVariant["runtime_dependencies"] = extraDependencies ?? new JsonArray();
    await File.WriteAllTextAsync(groupingTrace, traceVariant.ToJsonString());
    return await new HybridScenePlanner(new("not-started", "not-started", "not-started", [])).AnalyzeSingleAsync(
        new(2, groupingSource, root, Path.Combine(root, "grouping-variant-" + Guid.NewGuid().ToString("N")), 64, 32,
            RuntimeTraceFile: groupingTrace));
}
foreach (string script in new[] {
    "export function update() { shared.value = new Date(); return ''; }",
    "export function update() { const url = 'https://example.test'; shared.value = new Date(); return url; }",
    "export function update() { const pattern = /https:\\/\\//; shared.value = new Date(); return pattern; }",
    "export function update() { return thisScene.getLayer('visible').text + new Date(); }",
    "export function update() { return engine.setTimeout(() => {}, 1) + new Date(); }" })
{
    JsonObject preserved = await PlanGroupingVariantAsync(script);
    var omitted = preserved["omitted_snapshot_layer_ids"]!.AsArray().Select(n => n!.GetValue<int>()).ToHashSet();
    Check(!omitted.Contains(902) && !omitted.Contains(903) &&
        preserved["live_layer_ids"]!.AsArray().Any(n => n!.GetValue<int>() == 903),
        "hidden hierarchy with shared state, object lookup, or unknown engine capability is preserved: " + script);
}
JsonObject crossRoot = await PlanGroupingVariantAsync("export function update() { return new Date(); }", new JsonArray(
    new JsonObject { ["owner"] = 908, ["target"] = 903, ["operation"] = "read", ["property"] = "text", ["initialization"] = false }));
Check(!crossRoot["omitted_snapshot_layer_ids"]!.AsArray().Any(id => id!.GetValue<int>() is 902 or 903),
    "observed cross-root dependency prevents hidden hierarchy removal");
JsonObject initializedCrossSubtree = await PlanGroupingVariantAsync("export function update() { return new Date(); }", new JsonArray(
    new JsonObject { ["owner"] = 912, ["target"] = 911, ["operation"] = "read", ["property"] = "text", ["initialization"] = true }));
Check(!initializedCrossSubtree["omitted_snapshot_layer_ids"]!.AsArray().Any(id => id!.GetValue<int>() == 911),
    "initialization reads across child-subtree boundaries preserve the hidden target");
JsonObject fontHostScene = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(groupingSource, "scene.json")))!.AsObject();
JsonArray fontHostObjects = fontHostScene["objects"]!.AsArray();
JsonObject fontHost = fontHostObjects.OfType<JsonObject>().Single(obj => obj["id"]!.GetValue<int>() == 902);
fontHost["visible"] = new JsonObject { ["value"] = false,
    ["script"] = "export function applyUserProperties(p) { thisScene.getLayer('label').font = p.font; }" };
foreach (JsonObject hidden in fontHostObjects.OfType<JsonObject>().Where(obj => obj["id"]!.GetValue<int>() is 902 or 903).ToArray())
{
    fontHostObjects.Remove(hidden); fontHostObjects.Add(hidden);
}
await File.WriteAllTextAsync(Path.Combine(groupingSource, "scene.json"), fontHostScene.ToJsonString());
JsonObject fontController = await PlanGroupingVariantAsync(
    "export function applyUserProperties(p) { thisScene.getLayer('label').font = p.font; }");
Check(fontController["live_layer_ids"]!.AsArray().Any(id => id!.GetValue<int>() == 902) &&
    fontController["composition"]!.AsArray().Any(item => item?["live_root"]?.GetValue<int>() == 902) &&
    fontController["video_groups"]!.AsArray().Count == 1 &&
    fontController["video_layout_admission"]!["status"]!.GetValue<string>() == "planned_layout_allowed",
    "a hidden font controller survives without creating an empty second video group or breaking the opaque base");

string overlaySource = Path.Combine(root, "overlay-source");
Directory.CreateDirectory(overlaySource);
var overlayObjects = new JsonArray(
    new JsonObject { ["id"] = 1000, ["name"] = "text overlay group", ["visible"] = true,
        ["origin"] = "10 20 0", ["scale"] = "2 2 1" },
    new JsonObject { ["id"] = 1001, ["parent"] = 1000, ["name"] = "clock label",
        ["text"] = new JsonObject { ["script"] = "export function update() { return new Date(); }" },
        ["effects"] = new JsonArray(new JsonObject { ["name"] = "fixed glow" }, new JsonObject { ["name"] = "fixed shadow" }) },
    new JsonObject { ["id"] = 1002, ["parent"] = 1000, ["visible"] = false, ["image"] = "hidden.png" },
    new JsonObject { ["id"] = 1006, ["parent"] = 1000, ["name"] = "protected label", ["text"] = "protected",
        ["effects"] = new JsonArray(new JsonObject { ["name"] = "animated glow", ["animation"] = new JsonObject { ["rate"] = 1 } }) },
    new JsonObject { ["id"] = 1003, ["name"] = "framebuffer input", ["image"] = "models/util/fullscreenlayer.json" },
    new JsonObject { ["id"] = 1004, ["name"] = "live image", ["image"] = "visible.png",
        ["origin"] = new JsonObject { ["script"] = "export function update(value) { new Date(); return value; }", ["value"] = "0 0 0" } },
    new JsonObject { ["id"] = 1005, ["name"] = "baked base", ["text"] = "base" });
await File.WriteAllTextAsync(Path.Combine(overlaySource, "scene.json"), new JsonObject {
    ["general"] = new JsonObject { ["orthogonalprojection"] = new JsonObject { ["width"] = 64, ["height"] = 32 }, ["clearenabled"] = true },
    ["objects"] = overlayObjects }.ToJsonString());
string overlayTrace = Path.Combine(root, "overlay-trace.json");
await File.WriteAllTextAsync(overlayTrace, new JsonObject {
    ["source"] = Path.Combine(overlaySource, "scene.json"), ["status"] = "complete",
    ["source_script_error_count"] = 0, ["source_script_errors"] = new JsonArray(),
    ["runtime_dependencies"] = new JsonArray(),
    ["runtime_layers"] = new JsonArray(overlayObjects.OfType<JsonObject>().Select(obj => (JsonNode)new JsonObject {
        ["id"] = obj["id"]!.DeepClone(), ["owner"] = obj["id"]!.DeepClone(),
        ["visible"] = obj["visible"]?.DeepClone() ?? JsonValue.Create(true),
        ["has_mesh"] = obj.ContainsKey("image") || obj.ContainsKey("text"),
        ["effective_parallax_depth"] = new JsonArray(0, 0),
        ["materials"] = new JsonArray(new JsonObject { ["uses_audio_spectrum"] = false,
            ["textures"] = obj["id"]!.GetValue<int>() == 1003 ? new JsonArray("_rt_default") : new JsonArray() }) }).ToArray()) }.ToJsonString());
async Task<JsonObject> AnalyzeOverlayAsync(string placement, string textEffects = "preserve") => await new HybridScenePlanner(
    new("not-started", "not-started", "not-started", [])).AnalyzeSingleAsync(new(2, overlaySource, root,
        Path.Combine(root, $"overlay-{placement}-{textEffects}"), 64, 32, RuntimeTraceFile: overlayTrace,
        LiveOverlayPlacement: placement, LiveTextEffects: textEffects));
JsonObject preservedOverlay = await AnalyzeOverlayAsync("preserve");
JsonObject foregroundOverlay = await AnalyzeOverlayAsync("foreground");
int[] promotedOverlayRoots = foregroundOverlay["occlusion_tradeoff"]!["promoted_roots"]!.AsArray()
    .Select(node => node!["root_id"]!.GetValue<int>()).ToArray();
Check(preservedOverlay["occlusion_tradeoff"]!["status"]!.GetValue<string>() == "available" &&
    preservedOverlay["root_order"]!.AsArray().Select(node => node!.GetValue<int>()).SequenceEqual(new[] { 1000, 1003, 1004, 1005 }) &&
    foregroundOverlay["occlusion_tradeoff"]!["status"]!.GetValue<string>() == "applied" &&
    foregroundOverlay["root_order"]!.AsArray().Last()!.GetValue<int>() == 1000,
    "preserve reports an available text overlay while foreground moves only its root order");
Check(promotedOverlayRoots.SequenceEqual(new[] { 1000 }) && !promotedOverlayRoots.Contains(1003) && !promotedOverlayRoots.Contains(1004),
    "framebuffer consumers and real visible image roots are not foreground-overlay candidates");
var snapshotOmissionMethod = typeof(HybridScenePlanner).GetMethod("ApplySnapshotOmissions",
    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
var overlayPlacementMethod = typeof(HybridScenePlanner).GetMethod("ApplyOverlayPlacement",
    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
JsonObject placedScene = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(overlaySource, "scene.json")))!.AsObject();
snapshotOmissionMethod.Invoke(null, new object[] { placedScene, foregroundOverlay });
overlayPlacementMethod.Invoke(null, new object[] { placedScene, foregroundOverlay });
JsonObject[] placedObjects = placedScene["objects"]!.AsArray().OfType<JsonObject>().ToArray();
JsonObject placedRoot = placedObjects.Single(obj => obj["id"]!.GetValue<int>() == 1000);
Check(placedObjects[^1]["id"]!.GetValue<int>() == 1000 && placedRoot["origin"]!.GetValue<string>() == "10 20 0" &&
    placedRoot["scale"]!.GetValue<string>() == "2 2 1" &&
    placedObjects.Single(obj => obj["id"]!.GetValue<int>() == 1001)["parent"]!.GetValue<int>() == 1000 &&
    placedObjects.All(obj => obj["id"]!.GetValue<int>() != 1002),
    "foreground placement preserves the root transform and child parent while omission removes only the hidden image");
var exportedOverlay = Assemble(foregroundOverlay, overlayObjects);
Check(exportedOverlay.OfType<JsonObject>().Select(obj => obj["id"]!.GetValue<int>()).SequenceEqual(new[] { 1003, 1004, 1500, 1000, 1001, 1006 }) &&
    JsonNode.DeepEquals(exportedOverlay[3], overlayObjects[0]) &&
    exportedOverlay[4]!["parent"]!.GetValue<int>() == 1000 && exportedOverlay[5]!["parent"]!.GetValue<int>() == 1000,
    "independent foreground overlay exports its complete author tree after the video, preserving native DFS order and static companions");

string overlaySourceBeforeTextChoice = await File.ReadAllTextAsync(Path.Combine(overlaySource, "scene.json"));
JsonObject simpleTextPlan = await AnalyzeOverlayAsync("preserve", "simple");
JsonObject simplifiedText = simpleTextPlan["text_effects_choice"]!["simplified_layers"]!.AsArray().Single()!.AsObject();
Check(preservedOverlay["text_effects_choice"]!["status"]!.GetValue<string>() == "available" &&
    preservedOverlay["text_effects_choice"]!["simplified_layers"]!.AsArray().Count == 0 &&
    simpleTextPlan["text_effects_choice"]!["status"]!.GetValue<string>() == "applied" &&
    simplifiedText["id"]!.GetValue<int>() == 1001 && simplifiedText["name"]!.GetValue<string>() == "clock label" &&
    simplifiedText["effect_count"]!.GetValue<int>() == 2 &&
    simpleTextPlan["text_effects_choice"]!["protected_layer_ids"]!.AsArray().Any(id => id!.GetValue<int>() == 1006),
    "text-effect choice reports the fixed live-text effects and protects an animated effect");
var textEffectMethod = typeof(HybridScenePlanner).GetMethod("ApplyTextEffectChoice",
    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
JsonObject preservedTextScene = JsonNode.Parse(overlaySourceBeforeTextChoice)!.AsObject();
textEffectMethod.Invoke(null, new object[] { preservedTextScene, preservedOverlay });
JsonObject simpleTextScene = JsonNode.Parse(overlaySourceBeforeTextChoice)!.AsObject();
textEffectMethod.Invoke(null, new object[] { simpleTextScene, simpleTextPlan });
JsonObject preservedLabel = preservedTextScene["objects"]!.AsArray().Single(node => node!["id"]!.GetValue<int>() == 1001)!.AsObject();
JsonObject simpleLabel = simpleTextScene["objects"]!.AsArray().Single(node => node!["id"]!.GetValue<int>() == 1001)!.AsObject();
JsonObject protectedLabel = simpleTextScene["objects"]!.AsArray().Single(node => node!["id"]!.GetValue<int>() == 1006)!.AsObject();
Check(preservedLabel["effects"]!.AsArray().Count == 2 && simpleLabel["effects"]!.AsArray().Count == 0 &&
    protectedLabel["effects"]!.AsArray().Count == 1 && simpleLabel["parent"]!.GetValue<int>() == 1000 &&
    simpleLabel["text"]!["script"]!.GetValue<string>() == preservedLabel["text"]!["script"]!.GetValue<string>() &&
    await File.ReadAllTextAsync(Path.Combine(overlaySource, "scene.json")) == overlaySourceBeforeTextChoice,
    "simple text effects remove only selected effects while preserving parent, script, protected effect, and source bytes");

var lateDependencyMethod = typeof(HybridBakeService).GetMethod("LateExternalDependencies",
    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
var lateSourceObjects = new[] {
    new JsonObject { ["id"] = 1700 },
    new JsonObject { ["id"] = 1701, ["parent"] = 1700 },
    new JsonObject { ["id"] = 1702, ["parent"] = 1701 },
    new JsonObject { ["id"] = 1703, ["parent"] = 1702 },
    new JsonObject { ["id"] = 1800 } }.ToDictionary(obj => obj["id"]!.GetValue<int>());
JsonObject LateEvent(int owner, int target, string operation, string property, bool initialization = false) => new() {
    ["owner"] = owner, ["target"] = target, ["operation"] = operation, ["property"] = property,
    ["initialization"] = initialization, ["frame"] = 240 };
JsonArray LateDependencies(params JsonObject[] dependencies) => (JsonArray)lateDependencyMethod.Invoke(null, new object[] {
    new JsonObject { ["native_result"] = new JsonObject { ["runtime_dependencies"] = new JsonArray(dependencies.Select(d => (JsonNode)d).ToArray()) } },
    new HashSet<int> { 1702, 1703 }, lateSourceObjects })!;
var lateAncestorWrite = LateEvent(1800, 1700, "write", "origin");
var lateAncestorDependencies = LateDependencies(lateAncestorWrite, LateEvent(1800, 1701, "write", "alpha"));
Check(lateAncestorDependencies.Count == 2 && lateAncestorDependencies.OfType<JsonObject>().All(dependency =>
        dependency["rejection_reason"]!.GetValue<string>() == "external_controller_write_to_retained_ancestor" &&
        dependency["frame"]!.GetValue<int>() == 240) && lateAncestorWrite["rejection_reason"] is null,
    "full capture rejects late external writes to immediate and transitive retained ancestors with unmodified trace evidence");
var lateBakedWrites = LateDependencies(LateEvent(1800, 1702, "write", "origin"), LateEvent(1800, 1800, "write", "origin"));
Check(lateBakedWrites.Count == 1 && lateBakedWrites[0]!["target"]!.GetValue<int>() == 1702 &&
    lateBakedWrites[0]!["rejection_reason"]!.GetValue<string>() == "external_controller_write_to_baked_layer",
    "full capture rejects external writes to baked targets without rejecting unrelated external writes");
var lateOutgoingWrites = LateDependencies(LateEvent(1702, 1700, "write", "origin"), LateEvent(1703, 1800, "write", "alpha"));
Check(lateOutgoingWrites.Count == 2 && lateOutgoingWrites[0]!["rejection_reason"]!.GetValue<string>() == "baked_controller_write_to_retained_ancestor" &&
    lateOutgoingWrites[1]!["rejection_reason"]!.GetValue<string>() == "baked_controller_write_to_external_layer",
    "full capture rejects baked controllers writing retained ancestors or known external objects instead of losing live updates");
Check(LateDependencies(LateEvent(1702, -1, "write", "origin"), LateEvent(1703, 9999, "write", "alpha")).Count == 0,
    "unresolved or runtime-created targets are not guessed to be known external source objects");
var lateInputs = LateDependencies(new[] { "wall_clock", "audio", "pointer", "media" }
    .Select(property => LateEvent(1702, -1, "input", property))
    .Concat(new[] { LateEvent(1700, -1, "input", "audio"), LateEvent(1800, -1, "input", "pointer") }).ToArray());
Check(lateInputs.Count == 5 && lateInputs.OfType<JsonObject>().Any(dependency =>
    dependency["rejection_reason"]!.GetValue<string>() == "external_input_to_retained_ancestor"),
    "full capture keeps all four external-input checks and also protects the complete ancestor chain");
Check(LateDependencies(LateEvent(1702, 1702, "write", "origin"), LateEvent(1703, 1702, "write", "alpha"),
    LateEvent(1702, -1, "time", "runtime"), LateEvent(1800, 1700, "write", "origin", initialization: true),
    LateEvent(1800, 1702, "write", "alpha", initialization: true), LateEvent(1800, 1702, "read", "origin", initialization: true),
    LateEvent(1702, 1700, "write", "origin", initialization: true), LateEvent(1703, 1800, "write", "alpha", initialization: true),
    LateEvent(1702, 1700, "read", "origin", initialization: true)).Count == 0,
    "full capture permits writes with both owner and target inside the group and preserves initialization-only read/write rules");
Check(LateDependencies(LateEvent(1702, -1, "input", "wall_clock", initialization: true)).Count == 1,
    "initialization does not exempt an external input from the existing capture guard");
var fallbackScene = JsonNode.Parse("""{"objects":[{"id":1,"visible":{"user":"shown","value":false},"scale":{"user":"size","value":"0.5 0.5 0.5"}}]}""")!.AsObject();
typeof(HybridBakeService).GetMethod("ApplyVisibilityFallbacks", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
    .Invoke(null, new object[] { fallbackScene, new JsonObject { ["shown"] = true, ["size"] = .7 } });
Check(fallbackScene["objects"]![0]!["visible"]!["value"]!.GetValue<bool>() &&
    fallbackScene["objects"]![0]!["scale"]!["value"]!.GetValue<string>() == "0.5 0.5 0.5",
    "export synchronizes visibility without replacing a vector fallback with its scalar slider");
string parallaxSource = Path.Combine(root, "shader-parallax-source");
Directory.CreateDirectory(parallaxSource);
await File.WriteAllTextAsync(Path.Combine(parallaxSource, "scene.json"), """
    {"general":{"cameraparallax":true,"orthogonalprojection":{"width":64,"height":32}},
    "objects":[{"id":1100,"name":"interactive background","image":"bg.json","parallaxDepth":"0 0"},
               {"id":1101,"name":"foreground","image":"fg.json"}]}
    """);
string parallaxTrace = Path.Combine(root, "shader-parallax-trace.json");
await File.WriteAllTextAsync(parallaxTrace, new JsonObject {
    ["status"]="complete", ["source"]=Path.Combine(parallaxSource,"scene.json"), ["runtime_dependencies"]=new JsonArray(),
    ["source_script_error_count"] = 0, ["source_script_errors"] = new JsonArray(),
    ["runtime_layers"]=new JsonArray(new[] {1100,1101}.Select(id=>(JsonNode)new JsonObject {
        ["id"]=id,["owner"]=id,["visible"]=true,["has_mesh"]=true,["effective_parallax_depth"]=new JsonArray(0,0),
        ["materials"]=new JsonArray(new JsonObject { ["uses_audio_spectrum"]=false,
            ["active_uniforms"]=id==1100 ? new JsonArray("g_ParallaxPosition") : new JsonArray() }) }).ToArray())
}.ToJsonString());
var parallaxPlanner = new HybridScenePlanner(new("not-started","not-started","not-started",[]));
JsonObject liveParallax = await parallaxPlanner.AnalyzeSingleAsync(new(2,parallaxSource,root,Path.Combine(root,"shader-parallax-live"),64,32,
    RuntimeTraceFile:parallaxTrace));
JsonObject fixedParallax = await parallaxPlanner.AnalyzeSingleAsync(new(2,parallaxSource,root,Path.Combine(root,"shader-parallax-fixed"),64,32,
    RuntimeTraceFile:parallaxTrace,ViewMode:"fixed_view"));
Check(liveParallax["live_layer_ids"]!.AsArray().Any(n=>n!.GetValue<int>()==1100) &&
    !fixedParallax["live_layer_ids"]!.AsArray().Any(n=>n!.GetValue<int>()==1100),
    "shader g_ParallaxPosition stays live even at zero model depth unless fixed view was explicitly selected");
    }
}
