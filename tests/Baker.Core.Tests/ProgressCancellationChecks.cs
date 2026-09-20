using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Baker.App;
using Baker.Core;

internal static class ProgressCancellationChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var estimate = new FrameProgressEstimate();
        check(estimate.Update(1, 101, 30).StageRemainingSeconds is null,
            "ETA waits for measured throughput after warmup");
        check(estimate.Update(11, 101, 31).StageRemainingSeconds is null,
            "ETA does not extrapolate the first second");
        var measured = estimate.Update(21, 101, 32);
        check(measured.StageRemainingSeconds == 8 && measured.StageElapsedSeconds == 32,
            "ETA excludes 30-second startup from frame rate but retains elapsed time");
        check(estimate.Update(101, 101, 40).StageRemainingSeconds == 0,
            "completed frame stream reports zero render time remaining");
        check(new FrameProgressEstimate().Update(1, 10, 100).StageRemainingSeconds is null,
            "new render does not inherit another group's throughput");
        string zh = ProgressPresentation.Timing(measured, 62, false);
        string en = ProgressPresentation.Timing(measured, 62, true);
        check(zh.Contains("已用时 1:02") && zh.Contains("预计剩余 0:08 / 共 0:40") && zh.Contains("收尾"),
            "Chinese timing distinguishes job elapsed from render-only ETA");
        check(en.Contains("This render") && en.Contains("finishing steps follow"),
            "English timing labels the estimate scope");
        check(!ProgressPresentation.Timing(new("finishing_encode", null, ""), 70, false).Contains("剩余"),
            "phase transition clears the render estimate");
        var stages = new List<RenderProgress>();
        using (new StageTiming(new ImmediateProgress(stages.Add)).Measure(StageTiming.SeamCheck)) { }
        check(stages.Single().Stage == StageTiming.SeamCheck && stages[0].Fraction is null,
            "existing timing scopes emit indeterminate stage progress");
    }

    internal static async Task RunNativeAsync(string output)
    {
        string root = Directory.GetCurrentDirectory();
        output = Path.GetFullPath(output);
        if (Directory.Exists(output)) throw new IOException("Native check output must be new.");
        Directory.CreateDirectory(output);
        var tools = new NativeTools(Path.Combine(root, "build/native-local22/bin/wpe-render.exe"),
            Path.Combine(root, ".deps/ffmpeg-encoder-gpl2/portable/ffmpeg.exe"),
            Path.Combine(root, ".deps/ffmpeg-encoder-gpl2/portable/ffprobe.exe"),
            [Path.Combine(root, ".tools/llvm-mingw-22/bin"), Path.Combine(root, ".deps/ffmpeg-lgpl21/prefix/bin")]);
        string fixture = Path.Combine(root, "tests/fixtures/native/shader-clock");
        var request = new RenderRequest(fixture, fixture, "", 128, 96, 30, 1, 1_000_000, WarmupFrames: 17);
        using var source = new ProjectSource(fixture);
        string before = await source.SourceHashAsync();
        var results = new JsonArray();
        foreach (bool samples in new[] { false, true })
        {
            string folder = Path.Combine(output, samples ? "cancel-samples" : "cancel-video");
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var children = new HashSet<int>();
            double? remaining = null;
            var stop = new Stopwatch();
            var progress = new ImmediateProgress(value =>
            {
                if (value.Stage != "rendering" || value.Fraction is not > 0 || stop.IsRunning) return;
                if (!samples && value.StageRemainingSeconds is null) return;
                remaining = value.StageRemainingSeconds;
                foreach (string exe in samples ? new[] { tools.Renderer } : new[] { tools.Renderer, tools.Ffmpeg })
                foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)))
                {
                    using (process)
                        if (!process.HasExited && string.Equals(process.MainModule?.FileName, Path.GetFullPath(exe), StringComparison.OrdinalIgnoreCase))
                            children.Add(process.Id);
                }
                stop.Start(); cancellation.Cancel();
            });
            bool cancelled = false;
            try
            {
                await new NativeRenderRunner(tools).RenderAsync(request with {
                    OutputDirectory = folder, FrameSamplesOnly = samples,
                    FrameSampleStride = samples ? 5u : 0u }, progress, cancellation.Token);
            }
            catch (OperationCanceledException) { cancelled = true; }
            stop.Stop();
            if (!cancelled || children.Count < (samples ? 1 : 2) || stop.Elapsed.TotalSeconds >= 5)
                throw new Exception($"Cancellation check failed: cancelled={cancelled}, children={children.Count}, seconds={stop.Elapsed.TotalSeconds:F3}.");
            foreach (int pid in children)
            {
                try { using var child = Process.GetProcessById(pid); if (!child.HasExited) throw new Exception($"Child {pid} survived cancellation."); }
                catch (ArgumentException) { }
            }
            var manifest = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(folder, "manifest.json")))!;
            if (manifest["status"]?.GetValue<string>() != "cancelled") throw new Exception("Wrong cancellation status.");
            results.Add(new JsonObject { ["route"] = samples ? "samples" : "video", ["child_count"] = children.Count,
                ["cancel_seconds"] = stop.Elapsed.TotalSeconds, ["render_eta_seconds"] = remaining, ["children_exited"] = true });
        }
        var values = new List<RenderProgress>();
        var completed = await new NativeRenderRunner(tools).RenderAsync(request with {
            OutputDirectory = Path.Combine(output, "retry"), Frames = 48 }, new ImmediateProgress(values.Add));
        if (completed["status"]?.GetValue<string>() != "completed" || !File.Exists(Path.Combine(output, "retry/preview.mp4")) ||
            !values.Any(value => value.Stage == "finishing_encode" && value.StageRemainingSeconds is null) ||
            !values.Any(value => value.Stage == "rendering" && value.Fraction == 1) || before != await source.SourceHashAsync())
            throw new Exception("Retry output/progress/source verification failed.");
        var report = new JsonObject { ["status"] = "passed", ["cancellation"] = results,
            ["retry_frames"] = 48, ["source_unchanged"] = true };
        await File.WriteAllTextAsync(Path.Combine(output, "report.json"), report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(report.ToJsonString());
    }

    private sealed class ImmediateProgress(Action<RenderProgress> action) : IProgress<RenderProgress>
    {
        public void Report(RenderProgress value) => action(value);
    }
}
