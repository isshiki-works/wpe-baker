using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>接缝预览的一次导出计划：输入视频、循环长度、每侧帧数与输出尺寸都在这里定死，参数构造只读它。</summary>
public sealed record SeamPreviewPlan(string VideoPath, string OutputPath, string LabelPath, ulong LoopFrames, uint WindowFrames,
    uint FpsNumerator, uint FpsDenominator, int SourceWidth, int SourceHeight, int OutputWidth, int OutputHeight,
    int LabelScale, int LabelWidth)
{
    /// <summary>尾段第一帧在循环里的帧号：P - N。</summary>
    public ulong TailStartFrame => LoopFrames - WindowFrames;

    /// <summary>尾段输入 -ss 往前多留的整帧数，最多 <see cref="SeamPreview.SeekLeadFrames"/>，不越过第 0 帧。</summary>
    public ulong SeekLeadFrames => Math.Min(SeamPreview.SeekLeadFrames, TailStartFrame);

    /// <summary>预览总帧数：原速 2N 帧 + 0.25 倍速 4×2N 帧。</summary>
    public int OutputFrames => checked((int)WindowFrames * 2 * (1 + SeamPreview.SlowFactor));

    /// <summary>标签条缩放后的高度，恒为偶数。</summary>
    public int LabelHeight => SeamPreview.LabelRows * LabelScale;
}

/// <summary>
/// 接缝预览：循环末尾 N 帧接开头 N 帧，先原速一遍、再 0.25 倍速一遍，顶部标签条写帧号，
/// 末尾段红底、开头段绿底，颜色切换的那一刻就是接缝。拒绝时自动导出；成功任务仅在显式
/// 请求诊断时导出到组目录下的 <c>seam-preview.mp4</c>。
/// <para>
/// 只按时间段 seek 解码这 2N 帧：尾段输入 -ss 到第 P-N-M 帧、-t 只读 M+N+2 帧，开头段 -t 只读 N+2 帧，
/// 绝不整片解码，也不把 master 读进内存。导出失败只记 warning，不改变烘焙结论。
/// </para>
/// </summary>
public static class SeamPreview
{
    public const string FileName = "seam-preview.mp4";

    /// <summary>慢放倍数：每帧重复 4 次，即 0.25 倍速。</summary>
    public const int SlowFactor = 4;

    /// <summary>尾段 -ss 落在第 P-N 帧之前这么多个整帧边界上，真正取哪几帧由按时间戳的半帧窗口决定。</summary>
    public const int SeekLeadFrames = 4;

    /// <summary>标签条原生行高：3 行上边距 + 7 行字形 + 2 行下边距。</summary>
    public const int LabelRows = 12;

    /// <summary>预览画面（不含标签条）的外框。大于外框的缩小，远小于外框的放大到最长边 480。</summary>
    public const int MaximumWidth = 1920, MaximumHeight = 1080, MinimumLongEdge = 480;

    public const string PassedOutcome = "passed";
    public const string RejectedOutcome = "rejected";

    /// <summary>每侧帧数 N：与接缝淡化窗口一致（0.4 秒，60fps 下 24 帧），短循环不超过半个周期。</summary>
    public static uint WindowFrames(ulong loopFrames, uint fpsNumerator, uint fpsDenominator) =>
        (uint)Math.Min(ResidualMasking.CrossfadeFrames(fpsNumerator, fpsDenominator), loopFrames / 2);

    /// <summary>
    /// 这个组要不要导出预览：只要接缝校验给出了状态（通过或被拒）就导出；
    /// 探针渲染没有做接缝校验，静态贴图没有接缝，这两种不导。
    /// </summary>
    public static bool ShouldExport(bool probe, bool isStatic, [NotNullWhen(true)] JsonObject? seam, bool includePassed = false) =>
        !probe && !isStatic && seam?["status"] is JsonValue status && status.TryGetValue(out string? text) &&
        !string.IsNullOrEmpty(text) && (text != "observed_seam_pass" || includePassed);

    /// <summary>接缝校验状态归成两种结局。原作自身的切口不再单列：它在参照步进 f[P] − f[P−1] 里，闭合就通过。</summary>
    public static string Outcome(string? seamStatus) => seamStatus == "observed_seam_pass" ? PassedOutcome : RejectedOutcome;

    public static SeamPreviewPlan Plan(string videoPath, string outputPath, ulong loopFrames, uint fpsNumerator,
        uint fpsDenominator, int sourceWidth, int sourceHeight)
    {
        if (fpsNumerator == 0 || fpsDenominator == 0) throw new ArgumentException("接缝预览需要正的有理帧率。");
        if (loopFrames < 2) throw new ArgumentException("接缝预览需要至少 2 帧的循环。");
        if (sourceWidth <= 0 || sourceHeight <= 0) throw new ArgumentException("接缝预览需要正的视频尺寸。");
        uint window = WindowFrames(loopFrames, fpsNumerator, fpsDenominator);
        double scale = Math.Min((double)MaximumWidth / sourceWidth, (double)MaximumHeight / sourceHeight);
        if (scale > 1) scale = Math.Min(scale, Math.Max(1, (double)MinimumLongEdge / Math.Max(sourceWidth, sourceHeight)));
        int width = Math.Max(2, (int)Math.Round(sourceWidth * scale / 2) * 2);
        int height = Math.Max(2, (int)Math.Round(sourceHeight * scale / 2) * 2);
        string output = Path.GetFullPath(outputPath);
        // 标签条缩放：画面高 240 像素一档、至少 2 倍，同时保证最长的那行字放得下。
        int columns = LongestLabel(loopFrames) * GlyphAdvance + 2 * GlyphAdvance;
        int labelScale = Math.Max(1, Math.Min(Math.Max(2, height / 240), Math.Min(width / columns, 8)));
        int labelWidth = (width + labelScale - 1) / labelScale;
        return new(Path.GetFullPath(videoPath), output, output + ".labels.rgb", loopFrames, window, fpsNumerator, fpsDenominator,
            sourceWidth, sourceHeight, width, height, labelScale, labelWidth);
    }

    private static string Seconds(double frames, SeamPreviewPlan plan) =>
        (frames * plan.FpsDenominator / plan.FpsNumerator).ToString("F9", CultureInfo.InvariantCulture);

    private static string Integer(ulong value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// ffmpeg 参数。两个视频输入都用 -t 限定读取长度：尾段 -ss 到整帧边界 P-N-M，再用按时间戳的半帧窗口
    /// [M-0.5, M+N-0.5) 取出第 P-N..P-1 帧——不依赖 -ss 在帧边界上保留哪一帧；开头段按帧序号取第 0..N-1 帧。
    /// 输出 -frames:v 恰为 10N。
    /// </summary>
    public static string[] FfmpegArguments(SeamPreviewPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ulong n = plan.WindowFrames, lead = plan.SeekLeadFrames;
        string fps = $"{plan.FpsNumerator}/{plan.FpsDenominator}";
        string tailWindow = $"select='gte(t\\,{Seconds(lead - 0.5, plan)})*lt(t\\,{Seconds(lead + n - 0.5, plan)})'";
        string toPreview = $"scale={plan.OutputWidth}:{plan.OutputHeight}:flags=bicubic:out_color_matrix=bt709:out_range=limited,format=yuv420p";
        string graph =
            $"[0:v]{tailWindow},setpts=PTS-STARTPTS[tail];" +
            $"[1:v]trim=start_frame=0:end_frame={Integer(n)},setpts=PTS-STARTPTS[head];" +
            $"[tail][head]concat=n=2:v=1:a=0,{toPreview},split=2[normalin][slowin];" +
            $"[normalin]fps={fps},trim=end_frame={Integer(2 * n)}[normal];" +
            $"[slowin]setpts={SlowFactor}*(PTS-STARTPTS),fps={fps},tpad=stop={SlowFactor}:stop_mode=clone," +
            $"trim=end_frame={Integer(2 * n * SlowFactor)}[slow];" +
            "[normal][slow]concat=n=2:v=1:a=0[body];" +
            $"[2:v]scale=iw*{plan.LabelScale}:ih*{plan.LabelScale}:flags=neighbor:out_color_matrix=bt709:out_range=limited," +
            $"format=yuv420p,crop={plan.OutputWidth}:ih:0:0[label];" +
            "[label][body]vstack=inputs=2[out]";
        return
        [
            "-hide_banner", "-nostdin", "-n",
            "-ss", Seconds(plan.TailStartFrame - lead, plan), "-t", Seconds(lead + n + 2, plan), "-i", plan.VideoPath,
            "-t", Seconds(n + 2, plan), "-i", plan.VideoPath,
            "-f", "rawvideo", "-pixel_format", "rgb24", "-video_size", $"{plan.LabelWidth}x{LabelRows}", "-framerate", fps,
            "-i", plan.LabelPath,
            "-filter_complex", graph, "-map", "[out]", "-an",
            "-frames:v", plan.OutputFrames.ToString(CultureInfo.InvariantCulture),
            "-c:v", "libx264", "-preset", "fast", "-crf", "18", "-pix_fmt", "yuv420p",
            "-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709", "-color_range", "tv",
            "-video_track_timescale", plan.FpsNumerator.ToString(CultureInfo.InvariantCulture),
            "-movflags", "+faststart", plan.OutputPath + ".partial.mp4"
        ];
    }

    /// <summary>第 <paramref name="outputFrame"/> 帧对应的循环帧号、是否属于末尾段、播放倍速文字。</summary>
    public static (ulong LoopFrame, bool Tail, string Speed) FrameAt(SeamPreviewPlan plan, int outputFrame)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (outputFrame < 0 || outputFrame >= plan.OutputFrames) throw new ArgumentOutOfRangeException(nameof(outputFrame));
        int pair = checked((int)plan.WindowFrames * 2);
        bool slow = outputFrame >= pair;
        int index = slow ? (outputFrame - pair) / SlowFactor : outputFrame;
        bool tail = index < plan.WindowFrames;
        ulong frame = tail ? plan.TailStartFrame + (ulong)index : (ulong)index - plan.WindowFrames;
        return (frame, tail, slow ? "0.25X" : "1X");
    }

    /// <summary>标签文字，例如 "0.25X  END    1199/1200"。只用内置点阵字库里的字符。</summary>
    public static string LabelText(SeamPreviewPlan plan, int outputFrame)
    {
        var (frame, tail, speed) = FrameAt(plan, outputFrame);
        return $"{speed,-5}  {(tail ? "END" : "START"),-5}  {Integer(frame)}/{Integer(plan.LoopFrames)}";
    }

    private static int LongestLabel(ulong loopFrames) => $"0.25X  START  {Integer(loopFrames)}/{Integer(loopFrames)}".Length;

    /// <summary>按帧顺序写出标签条原始 RGB24 数据，逐帧写盘，不整份放进内存。</summary>
    public static async Task WriteLabelsAsync(SeamPreviewPlan plan, Stream destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(destination);
        byte[] frame = new byte[checked(plan.LabelWidth * LabelRows * 3)];
        for (int index = 0; index < plan.OutputFrames; ++index)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RenderLabel(plan, index, frame);
            await destination.WriteAsync(frame, cancellationToken);
        }
    }

    /// <summary>画一帧标签条：末尾段暗红底、开头段暗绿底，白色 5×7 点阵字。</summary>
    public static void RenderLabel(SeamPreviewPlan plan, int outputFrame, Span<byte> destination)
    {
        int width = plan.LabelWidth, rows = LabelRows;
        if (destination.Length != width * rows * 3) throw new ArgumentException("标签帧缓冲区大小与计划不一致。");
        bool tail = FrameAt(plan, outputFrame).Tail;
        (byte r, byte g, byte b) background = tail ? ((byte)120, (byte)28, (byte)28) : ((byte)24, (byte)96, (byte)40);
        for (int pixel = 0; pixel < width * rows; ++pixel)
        {
            destination[pixel * 3] = background.r;
            destination[pixel * 3 + 1] = background.g;
            destination[pixel * 3 + 2] = background.b;
        }
        string text = LabelText(plan, outputFrame);
        int x = GlyphAdvance;
        foreach (char character in text)
        {
            if (x + GlyphWidth > width) break;
            if (Glyphs.TryGetValue(character, out string[]? glyph))
                for (int row = 0; row < GlyphHeight; ++row)
                    for (int column = 0; column < GlyphWidth; ++column)
                        if (glyph[row][column] == '#')
                        {
                            int offset = ((row + 3) * width + x + column) * 3;
                            destination[offset] = 244; destination[offset + 1] = 244; destination[offset + 2] = 244;
                        }
            x += GlyphAdvance;
        }
    }

    /// <summary>
    /// 包一层导出：任何结局都调用 <paramref name="export"/>；失败（取消除外）只往 report.warnings 记一条、
    /// 返回 status=failed 的记录，绝不改 report.status，也不抛异常。
    /// </summary>
    public static async Task<JsonObject> ExportOrWarnAsync(JsonObject report, string groupId, string outcome,
        Func<CancellationToken, Task<JsonObject>> export, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(export);
        try
        {
            JsonObject record = await export(cancellationToken);
            record["seam_outcome"] = outcome;
            return record;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            if (report["warnings"] is not JsonArray warnings) report["warnings"] = warnings = new JsonArray();
            warnings.Add(new JsonObject
            {
                ["key"] = "warning.seam_preview_failed",
                ["group_id"] = groupId,
                ["zh"] = MessageCatalog.Get("warning.seam_preview_failed", MessageCatalog.Chinese, groupId, error.Message),
                ["en"] = MessageCatalog.Get("warning.seam_preview_failed", MessageCatalog.English, groupId, error.Message),
                ["error_type"] = error.GetType().Name,
                ["error"] = error.Message
            });
            return new JsonObject
            {
                ["status"] = "failed", ["path"] = null, ["seam_outcome"] = outcome,
                ["error_type"] = error.GetType().Name, ["error"] = error.Message
            };
        }
    }

    /// <summary>把导出记录挂到组记录上：seam_preview 是路径（失败或不适用为 null），seam_preview_export 是完整记录。</summary>
    public static void Attach(JsonObject group, JsonObject? record)
    {
        ArgumentNullException.ThrowIfNull(group);
        group["seam_preview"] = record?["status"]?.GetValue<string>() == "exported" ? record["path"]?.DeepClone() : null;
        group["seam_preview_export"] = record?.DeepClone();
    }

    /// <summary>bake 结束时 stderr 结论行之后的补充行：每个导出成功的组一句"接缝预览：路径"。</summary>
    public static IEnumerable<string> SummaryLines(JsonObject report, string language)
    {
        ArgumentNullException.ThrowIfNull(report);
        foreach (JsonObject group in (report["groups"] as JsonArray ?? []).OfType<JsonObject>())
            if (group["seam_preview"] is JsonValue value && value.TryGetValue(out string? path) && !string.IsNullOrEmpty(path))
                yield return MessageCatalog.Get("summary.seam_preview", language, path);
    }

    private const int GlyphWidth = 5, GlyphHeight = 7, GlyphAdvance = 6;

    private static readonly Dictionary<char, string[]> Glyphs = new()
    {
        ['0'] = [".###.", "#...#", "#..##", "#.#.#", "##..#", "#...#", ".###."],
        ['1'] = ["..#..", ".##..", "..#..", "..#..", "..#..", "..#..", ".###."],
        ['2'] = [".###.", "#...#", "....#", "...#.", "..#..", ".#...", "#####"],
        ['3'] = ["#####", "...#.", "..#..", "...#.", "....#", "#...#", ".###."],
        ['4'] = ["...#.", "..##.", ".#.#.", "#..#.", "#####", "...#.", "...#."],
        ['5'] = ["#####", "#....", "####.", "....#", "....#", "#...#", ".###."],
        ['6'] = ["..##.", ".#...", "#....", "####.", "#...#", "#...#", ".###."],
        ['7'] = ["#####", "....#", "...#.", "..#..", ".#...", ".#...", ".#..."],
        ['8'] = [".###.", "#...#", "#...#", ".###.", "#...#", "#...#", ".###."],
        ['9'] = [".###.", "#...#", "#...#", ".####", "....#", "...#.", ".##.."],
        ['.'] = [".....", ".....", ".....", ".....", ".....", ".##..", ".##.."],
        ['/'] = [".....", "....#", "...#.", "..#..", ".#...", "#....", "....."],
        ['X'] = ["#...#", "#...#", ".#.#.", "..#..", ".#.#.", "#...#", "#...#"],
        ['E'] = ["#####", "#....", "#....", "####.", "#....", "#....", "#####"],
        ['N'] = ["#...#", "#...#", "##..#", "#.#.#", "#..##", "#...#", "#...#"],
        ['D'] = ["####.", "#...#", "#...#", "#...#", "#...#", "#...#", "####."],
        ['S'] = [".####", "#....", "#....", ".###.", "....#", "....#", "####."],
        ['T'] = ["#####", "..#..", "..#..", "..#..", "..#..", "..#..", "..#.."],
        ['A'] = [".###.", "#...#", "#...#", "#####", "#...#", "#...#", "#...#"],
        ['R'] = ["####.", "#...#", "#...#", "####.", "#.#..", "#..#.", "#...#"],
    };
}
