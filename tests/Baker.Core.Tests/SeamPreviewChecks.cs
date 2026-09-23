using System.Globalization;
using System.Text.Json.Nodes;
using Baker.Core;

/// <summary>
/// 接缝预览：通过与被拒两种接缝结局都导出；参数只请求接缝两侧 2N 帧的时间段；导出失败不改烘焙结论。
/// 这里不启动 ffmpeg，只检查参数构造、结局判定与失败隔离。
/// </summary>
internal static class SeamPreviewChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        // ---- 默认仅拒绝导出；显式诊断也导出成功任务 ----
        string[] statuses = ["observed_seam_pass", "observed_seam_fail"];
        check(statuses.All(status => SeamPreview.ShouldExport(false, false, new JsonObject { ["status"] = status }, includePassed: true)),
            "seam preview: explicitly requested diagnostics include passed and rejected seams");
        check(!SeamPreview.ShouldExport(false, false, new JsonObject { ["status"] = "observed_seam_pass" }) &&
            SeamPreview.ShouldExport(false, false, new JsonObject { ["status"] = "observed_seam_fail" }),
            "seam preview: normal success does not generate an unused diagnostic video");
        check(!SeamPreview.ShouldExport(true, false, new JsonObject { ["status"] = "observed_seam_pass" }) &&
            !SeamPreview.ShouldExport(false, true, new JsonObject { ["status"] = "observed_seam_pass" }) &&
            !SeamPreview.ShouldExport(false, false, null),
            "seam preview: probes, static textures and groups without a seam verdict are not exported");
        check(SeamPreview.Outcome("observed_seam_pass") == SeamPreview.PassedOutcome &&
            SeamPreview.Outcome("observed_seam_fail") == SeamPreview.RejectedOutcome &&
            SeamPreview.Outcome(null) == SeamPreview.RejectedOutcome &&
            SeamPreview.Outcome("observed_seam_matches_source_discontinuity") == SeamPreview.RejectedOutcome,
            "seam preview: seam statuses map to passed / rejected; the retired matched-source-cut status counts as rejected");

        var outcomes = new List<string>();
        foreach (string outcome in new[] { SeamPreview.PassedOutcome, SeamPreview.RejectedOutcome })
        {
            var report = new JsonObject { ["status"] = "running" };
            JsonObject record = await SeamPreview.ExportOrWarnAsync(report, "group-1", outcome, _ =>
            {
                outcomes.Add(outcome);
                return Task.FromResult(new JsonObject { ["status"] = "exported", ["path"] = @"D:\out\group-1\seam-preview.mp4" });
            });
            var group = new JsonObject { ["id"] = "group-1" };
            SeamPreview.Attach(group, record);
            check(record["seam_outcome"]?.GetValue<string>() == outcome &&
                group["seam_preview"]?.GetValue<string>() == @"D:\out\group-1\seam-preview.mp4" &&
                report["status"]?.GetValue<string>() == "running" && report["warnings"] is null,
                $"seam preview: {outcome} outcome invokes the exporter and records the path on the group");
        }
        check(outcomes.Count == 2, "seam preview: the exporter ran once for each of the two outcomes");

        // ---- 导出失败不改烘焙结论 ----
        foreach (string bakeStatus in new[] { "candidate_rejected_seam", "candidate_generated" })
        {
            var report = new JsonObject { ["status"] = bakeStatus, ["loop_validation"] = "encoded_seam_failed" };
            JsonObject failed = await SeamPreview.ExportOrWarnAsync(report, "group-1", SeamPreview.RejectedOutcome,
                _ => throw new IOException("ffmpeg exited 1"));
            var group = new JsonObject { ["id"] = "group-1", ["status"] = "rejected_seam" };
            SeamPreview.Attach(group, failed);
            JsonObject? warning = (report["warnings"] as JsonArray)?.OfType<JsonObject>().SingleOrDefault();
            check(report["status"]?.GetValue<string>() == bakeStatus &&
                report["loop_validation"]?.GetValue<string>() == "encoded_seam_failed" &&
                failed["status"]?.GetValue<string>() == "failed" && group["seam_preview"] is null &&
                group["status"]?.GetValue<string>() == "rejected_seam" &&
                warning?["key"]?.GetValue<string>() == "warning.seam_preview_failed" &&
                warning["group_id"]?.GetValue<string>() == "group-1",
                $"seam preview: an export failure leaves bake status {bakeStatus} unchanged and only adds a warning");
        }
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            bool propagated = false;
            try
            {
                await SeamPreview.ExportOrWarnAsync(new JsonObject(), "group-1", SeamPreview.PassedOutcome,
                    token => { token.ThrowIfCancellationRequested(); return Task.FromResult(new JsonObject()); }, cancelled.Token);
            }
            catch (OperationCanceledException) { propagated = true; }
            check(propagated, "seam preview: user cancellation is not swallowed as a preview warning");
        }

        // ---- 参数只请求接缝两侧 2N 帧 ----
        const string video = @"D:\bake\group-1\encoded\cache.mp4", output = @"D:\bake\group-1\seam-preview.mp4";
        SeamPreviewPlan plan = SeamPreview.Plan(video, output, 864, 60, 1, 1920, 1080);
        string[] arguments = SeamPreview.FfmpegArguments(plan);
        check(plan.WindowFrames == 24 && plan.OutputFrames == 240,
            "seam preview: N defaults to the 0.4 s crossfade window (24 frames at 60 fps) and the output is 2N + 4x2N frames");
        var inputs = Inputs(arguments);
        var videoInputs = inputs.Where(input => input.Path == plan.VideoPath).ToArray();
        check(inputs.Count == 3 && videoInputs.Length == 2 && inputs[2].Path == plan.LabelPath,
            "seam preview: exactly two bounded reads of the video plus the label strip");
        check(videoInputs.All(input => input.Options.ContainsKey("-t")),
            "seam preview: every read of the video is limited with -t");
        check(arguments.Count(argument => argument == "-ss") == 1 && videoInputs[1].Options.ContainsKey("-ss") == false,
            "seam preview: only the tail input seeks; the head input starts at frame zero");
        double tailSeek = Frames(videoInputs[0].Options["-ss"], plan), tailRead = Frames(videoInputs[0].Options["-t"], plan);
        double headRead = Frames(videoInputs[1].Options["-t"], plan);
        check(Close(tailSeek, 864 - 24 - SeamPreview.SeekLeadFrames) && Close(tailRead, SeamPreview.SeekLeadFrames + 24 + 2) &&
            Close(headRead, 24 + 2),
            "seam preview: tail seeks to frame P-N-4 and reads N+6 frames; head reads N+2 frames");
        string graph = arguments[Array.IndexOf(arguments, "-filter_complex") + 1];
        (double start, double end) = SelectWindow(graph, plan);
        // 尾段窗口按半帧偏移取帧：seek 点 + [start, end) 里的整数帧恰好是 P-N..P-1。
        int first = (int)Math.Ceiling(tailSeek + start), last = (int)Math.Ceiling(tailSeek + end) - 1;
        check(first == 840 && last == 863 && start % 1 != 0 && end % 1 != 0,
            "seam preview: the tail window selects exactly loop frames P-N..P-1 with half-frame margins");
        check(graph.Contains("[1:v]trim=start_frame=0:end_frame=24,", StringComparison.Ordinal),
            "seam preview: the head keeps exactly loop frames 0..N-1");
        check(arguments[Array.IndexOf(arguments, "-frames:v") + 1] == "240" &&
            graph.Contains("fps=60/1,trim=end_frame=48[normal]", StringComparison.Ordinal) &&
            graph.Contains("setpts=4*(PTS-STARTPTS),fps=60/1", StringComparison.Ordinal) &&
            graph.Contains("trim=end_frame=192[slow]", StringComparison.Ordinal),
            "seam preview: one normal-speed pass of 2N frames then a 0.25x pass of 8N frames");
        check(!arguments.Any(argument => argument.Contains("master", StringComparison.OrdinalIgnoreCase)) &&
            arguments[^1] == output + ".partial.mp4",
            "seam preview: writes a partial file next to the group output, nothing else");

        // 帧数很大的无损 master：读取范围仍只随 N 走，与总长无关。
        SeamPreviewPlan huge = SeamPreview.Plan(@"D:\bake\group-1\master\preview.mp4", output, 6024, 60, 1, 3840, 2160);
        var hugeInputs = Inputs(SeamPreview.FfmpegArguments(huge)).Where(input => input.Path == huge.VideoPath).ToArray();
        double hugeRead = hugeInputs.Sum(input => Frames(input.Options["-t"], huge));
        check(Close(Frames(hugeInputs[0].Options["-ss"], huge), 6024 - 24 - 4) && hugeRead <= 2 * huge.WindowFrames + 8,
            "seam preview: a 6024-frame master is read for at most 2N+8 frames around the seam");

        // 分数帧率：秒数按分母换算。
        SeamPreviewPlan ntsc = SeamPreview.Plan(video, output, 1001, 60000, 1001, 1920, 1080);
        var ntscInputs = Inputs(SeamPreview.FfmpegArguments(ntsc)).Where(input => input.Path == ntsc.VideoPath).ToArray();
        check(ntsc.WindowFrames == 24 && Close(Frames(ntscInputs[0].Options["-ss"], ntsc), 1001 - 24 - 4),
            "seam preview: fractional frame rates convert frame indices with the denominator");

        // 短循环：N 不超过半个周期，seek 不越过第 0 帧。
        SeamPreviewPlan shortLoop = SeamPreview.Plan(video, output, 30, 60, 1, 256, 256);
        SeamPreviewPlan tiny = SeamPreview.Plan(video, output, 3, 60, 1, 256, 256);
        var tinyInputs = Inputs(SeamPreview.FfmpegArguments(tiny)).Where(input => input.Path == tiny.VideoPath).ToArray();
        check(shortLoop.WindowFrames == 15 && tiny.WindowFrames == 1 && tiny.SeekLeadFrames == 2 &&
            Close(Frames(tinyInputs[0].Options["-ss"], tiny), 0),
            "seam preview: short loops shrink N to half the period and never seek before frame zero");

        // ---- 帧号标签 ----
        check(SeamPreview.FrameAt(plan, 0) == (840UL, true, "1X") && SeamPreview.FrameAt(plan, 23) == (863UL, true, "1X") &&
            SeamPreview.FrameAt(plan, 24) == (0UL, false, "1X") && SeamPreview.FrameAt(plan, 47) == (23UL, false, "1X") &&
            SeamPreview.FrameAt(plan, 48) == (840UL, true, "0.25X") && SeamPreview.FrameAt(plan, 51) == (840UL, true, "0.25X") &&
            SeamPreview.FrameAt(plan, 48 + 4 * 24) == (0UL, false, "0.25X") && SeamPreview.FrameAt(plan, 239) == (23UL, false, "0.25X"),
            "seam preview: output frames map to loop frames P-N..P-1, 0..N-1, then the same at 0.25x");
        byte[] tailLabel = new byte[plan.LabelWidth * SeamPreview.LabelRows * 3], headLabel = new byte[tailLabel.Length];
        SeamPreview.RenderLabel(plan, 23, tailLabel);
        SeamPreview.RenderLabel(plan, 24, headLabel);
        check(tailLabel[0] > tailLabel[1] && headLabel[1] > headLabel[0] && tailLabel.Contains((byte)244) && !tailLabel.SequenceEqual(headLabel),
            "seam preview: the label strip turns from red to green exactly at the seam and carries drawn digits");
        using (var labels = new MemoryStream())
        {
            await SeamPreview.WriteLabelsAsync(shortLoop, labels);
            check(labels.Length == (long)shortLoop.LabelWidth * SeamPreview.LabelRows * 3 * shortLoop.OutputFrames,
                "seam preview: the label stream has one strip per output frame");
        }

        // ---- 尺寸 ----
        SeamPreviewPlan packed = SeamPreview.Plan(video, output, 864, 60, 1, 3840, 1080);
        SeamPreviewPlan portrait = SeamPreview.Plan(video, output, 864, 60, 1, 1080, 1920);
        check(plan.OutputWidth == 1920 && plan.OutputHeight == 1080 && packed.OutputWidth == 1920 && packed.OutputHeight == 540 &&
            portrait.OutputWidth == 608 && portrait.OutputHeight == 1080 && shortLoop.OutputWidth == 480 && shortLoop.OutputHeight == 480 &&
            new[] { plan, packed, portrait, shortLoop }.All(p => p.LabelHeight % 2 == 0 && p.LabelScale >= 1 &&
                p.LabelWidth * p.LabelScale >= p.OutputWidth),
            "seam preview: output fits 1920x1080, small sources are enlarged to 480 and dimensions stay even");
    }

    private sealed record Input(string Path, Dictionary<string, string> Options);

    /// <summary>把参数按 -i 切成输入，每个输入收集它前面的选项（从上一个 -i 之后开始）。</summary>
    private static List<Input> Inputs(string[] arguments)
    {
        var inputs = new List<Input>();
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int index = 0; index < arguments.Length; ++index)
        {
            string argument = arguments[index];
            if (argument == "-i") { inputs.Add(new(arguments[++index], options)); options = new(StringComparer.Ordinal); continue; }
            if (argument is "-ss" or "-t" or "-f" or "-framerate" or "-video_size" or "-pixel_format")
                options[argument] = arguments[++index];
            if (argument == "-filter_complex") break;
        }
        return inputs;
    }

    private static double Frames(string seconds, SeamPreviewPlan plan) =>
        double.Parse(seconds, CultureInfo.InvariantCulture) * plan.FpsNumerator / plan.FpsDenominator;

    private static bool Close(double actual, double expected) => Math.Abs(actual - expected) < 1e-4;

    private static (double Start, double End) SelectWindow(string graph, SeamPreviewPlan plan)
    {
        var match = System.Text.RegularExpressions.Regex.Match(graph, @"gte\(t\\,([0-9.]+)\)\*lt\(t\\,([0-9.]+)\)");
        if (!match.Success) return (double.NaN, double.NaN);
        return (Frames(match.Groups[1].Value, plan), Frames(match.Groups[2].Value, plan));
    }
}
