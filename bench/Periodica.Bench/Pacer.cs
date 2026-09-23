using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Periodica.Bench;

internal sealed record PaceOptions(string Renderer, string Source, string Assets, string Output, int Frames,
    double Fps = 60, int Warmup = 120, double Scale = 1, int Width = 3072, int Height = 1920);

/// <summary>
/// 让 wpe-render 按墙钟节拍出帧（移植自 research-realtime/x1-rerun-20260923/pacer.py）：渲染器把每帧 96×60 小图写进
/// stdout 管道，这里每 1/fps 秒读走一帧；管道满时渲染器阻塞在写入上，等同 vsync 限帧。模拟时间仍按帧号推进，画面不变。
/// Fps 为 0 表示不节流（满速参照，走同一条管道）。
/// </summary>
internal static class Pacer
{
    private const int SampleWidth = 96, SampleHeight = 60;

    internal static async Task<JsonObject> RunAsync(PaceOptions options, IProgress<string> progress, CancellationToken token)
    {
        if (options.Frames < 2 || options.Fps < 0) throw new ArgumentException("--frames must be at least 2 and --fps non-negative.");
        string output = Path.GetFullPath(options.Output);
        if (Directory.Exists(output) || File.Exists(output)) throw new IOException("--out must be a new directory.");
        var job = new JsonObject
        {
            ["schema_version"] = 1, ["source"] = options.Source, ["assets"] = options.Assets, ["output_dir"] = output,
            ["width"] = options.Width, ["height"] = options.Height, ["fps_num"] = 60, ["fps_den"] = 1,
            ["frames"] = options.Frames, ["warmup_frames"] = options.Warmup, ["seed"] = 17, ["epoch_ms"] = 946684800000L,
            ["raw_stdout"] = true, ["output_frame_stride"] = 1, ["trace_scene"] = false, ["gpu_timing"] = false,
            ["effect_render_scale"] = options.Scale, ["write_audio"] = false,
            ["output_sample_width"] = SampleWidth, ["output_sample_height"] = SampleHeight,
            ["input"] = new JsonObject { ["cursor_x"] = 0.5, ["cursor_y"] = 0.5, ["cursor_in_window"] = true }
        };
        string jobPath = output + ".job.json";
        await File.WriteAllTextAsync(jobPath, job.ToJsonString(), token);
        var start = new ProcessStartInfo(Path.GetFullPath(options.Renderer))
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
        };
        start.ArgumentList.Add("render"); start.ArgumentList.Add("--job"); start.ArgumentList.Add(jobPath);
        start.Environment["PATH"] = Path.GetDirectoryName(start.FileName) + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        long launched = Stopwatch.GetTimestamp();
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Renderer did not start.");
        await using var errorLog = File.Create(output + ".stderr.log");
        Task stderr = process.StandardError.BaseStream.CopyToAsync(errorLog, token);
        progress.Report($"renderer pid {process.Id}");
        Stream pipe = process.StandardOutput.BaseStream;
        byte[] frame = new byte[SampleWidth * SampleHeight * 4];
        var stamps = new long[options.Frames];
        long period = options.Fps > 0 ? (long)(Stopwatch.Frequency / options.Fps) : 0;
        long deadline = 0; int resets = 0;
        for (int index = 0; index < options.Frames; ++index)
        {
            if (period > 0)
            {
                long now = Stopwatch.GetTimestamp();
                if (index == 0) deadline = now;
                if (now < deadline) await Task.Delay(Stopwatch.GetElapsedTime(now, deadline), token);
            }
            await pipe.ReadExactlyAsync(frame, token);
            stamps[index] = Stopwatch.GetTimestamp();
            if (index == 0) progress.Report("FIRST");
            if (period > 0)
            {
                deadline += period;
                // 严重落后时重置节拍并计数，而不是连发追帧。
                if (stamps[index] > deadline + period) { ++resets; deadline = stamps[index]; }
            }
        }
        await process.WaitForExitAsync(token);
        await stderr;
        JsonObject summary = Summarize(stamps, options.Fps);
        summary["exit_code"] = process.ExitCode;
        summary["deadline_resets"] = resets;
        summary["startup_seconds"] = Stopwatch.GetElapsedTime(launched, stamps[0]).TotalSeconds;
        summary["scale"] = options.Scale;
        await File.WriteAllTextAsync(output + ".pace.json", summary.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), token);
        return summary;
    }

    /// <summary>帧到达时间戳 → 实际帧率与间隔分布；超预算按 1.5 倍名义间隔计。</summary>
    internal static JsonObject Summarize(IReadOnlyList<long> stamps, double fps)
    {
        double[] intervals = stamps.Zip(stamps.Skip(1), (a, b) => (b - a) * 1000.0 / Stopwatch.Frequency).ToArray();
        double span = (stamps[^1] - stamps[0]) / (double)Stopwatch.Frequency;
        double[] sorted = [.. intervals.Order()];
        return new JsonObject
        {
            ["fps_target"] = fps, ["frames"] = stamps.Count, ["span_seconds"] = span,
            ["achieved_fps"] = (stamps.Count - 1) / span,
            ["interval_ms_median"] = Quantile(sorted, 0.5), ["interval_ms_p01"] = Quantile(sorted, 0.01),
            ["interval_ms_p99"] = Quantile(sorted, 0.99), ["interval_ms_max"] = sorted[^1],
            ["over_budget_intervals"] = fps > 0 ? intervals.Count(value => value > 1000 / fps * 1.5) : null
        };
    }

    internal static double Quantile(IReadOnlyList<double> sorted, double p)
    {
        double index = (sorted.Count - 1) * p;
        int low = (int)Math.Floor(index), high = (int)Math.Ceiling(index);
        return sorted[low] + (sorted[high] - sorted[low]) * (index - low);
    }
}
