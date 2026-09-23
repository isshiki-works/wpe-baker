using System.Reflection;
using System.Text.Json.Nodes;
using Baker.Core;

internal static class TemporaryCaptureChecks
{
    internal static async Task RunAsync(Action<bool, string> check, string root)
    {
        string directory = Path.Combine(root, "temporary-capture");
        Directory.CreateDirectory(Path.Combine(directory, "native"));
        Directory.CreateDirectory(Path.Combine(directory, "project"));
        string raw = Path.Combine(directory, "native", "frames.rgba");
        string final = Path.Combine(directory, "project", "keep.tex");
        File.WriteAllText(raw, "scratch");
        File.WriteAllText(final, "final asset");
        File.WriteAllText(Path.Combine(directory, "manifest.json"), "{}");
        MethodInfo delete = typeof(ProjectSource).Assembly.GetType("Baker.Core.TemporaryCaptureFiles")!
            .GetMethod("Delete", BindingFlags.NonPublic | BindingFlags.Static)!;
        var report = new JsonObject();
        void Delete(params string[] paths) => delete.Invoke(null, [report, directory, paths]);
        Delete("native/frames.rgba");
        Delete("native/frames.rgba");
        check(!File.Exists(raw) && File.ReadAllText(final) == "final asset" &&
            File.Exists(Path.Combine(directory, "manifest.json")) && report.Count == 0,
            "owned scratch cleanup is repeatable and preserves final assets and reports");
        using (var locked = new FileStream(raw, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            Delete("native/frames.rgba");
            check(File.Exists(raw) && report["temporary_cleanup_errors"]!.AsArray().Count == 1,
                "a locked scratch file records the cleanup failure instead of hiding it");
        }
        Delete("native/frames.rgba");
        bool rejected = false;
        try { Delete("../keep.tex"); }
        catch (TargetInvocationException error) when (error.InnerException is InvalidDataException) { rejected = true; }
        check(rejected && File.Exists(final), "scratch cleanup rejects paths outside its owned directory");

        string source = Path.Combine(root, "scratch-check-source");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "project.json"), "{\"type\":\"scene\",\"file\":\"scene.json\"}");
        File.WriteAllText(Path.Combine(source, "scene.json"), "{\"objects\":[]}");
        string fakeTool = Path.Combine(source, "not-an-executable.exe");
        File.WriteAllText(fakeTool, "must not be launched");
        var tools = new NativeTools(fakeTool, fakeTool, fakeTool, []);
        string oversized = Path.Combine(root, "oversized-raw-not-created");
        bool noSpace = false;
        try { await new NativeRenderRunner(tools).RenderRawAsync(new(source, source, oversized, 1, 1, 60, 1, 1UL << 40)); }
        catch (IOException error) when (error.Message.Contains("Insufficient free space", StringComparison.Ordinal)) { noSpace = true; }
        check(noSpace && !Directory.Exists(oversized),
            "raw disk budget rejects oversized output before launching a renderer or creating capture files");

        // 成对比较的一边在渲染前就失败：报告记 failed 与那一边的原始错误，另一边被连带取消，不顶替它。
        string output = Path.Combine(root, "paired-failure");
        var progress = new InlineProgress(value =>
        {
            if (value.Stage == "rendering_pair") Directory.CreateDirectory(Path.Combine(output, "source"));
        });
        bool failedAsExpected = false;
        try
        {
            await new CandidateValidation(tools).ValidateAsync(new(1, source, source, source, output, 1, 1, Frames: 1), progress);
        }
        catch (IOException error) when (error.Message.Contains("Raw render output must be new", StringComparison.Ordinal))
        { failedAsExpected = true; }
        var comparison = JsonNode.Parse(File.ReadAllText(Path.Combine(output, "comparison.json")))!;
        check(failedAsExpected && comparison["status"]!.GetValue<string>() == "failed" &&
            comparison["error_type"]!.GetValue<string>() == nameof(IOException),
            "a paired comparison that fails on one side reports that side's original error");
    }

    private sealed class InlineProgress(Action<RenderProgress> report) : IProgress<RenderProgress>
    {
        public void Report(RenderProgress value) => report(value);
    }
}
