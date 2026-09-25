using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>
/// 开烘前的磁盘闸门（rc11 bughunt 第 1 条）：单案实测中间产物峰值 47.8 GB，而
/// <see cref="TemporaryCaptureFiles.RequireFreeSpace"/> 只留 1 GiB 余量，塞满盘之后才以 IOException 失败，
/// 界面侧连这一层都没有。这里按计划预估峰值，空间不够就在任何渲染开始前干净拒绝，并说清差多少、在哪个盘。
/// </summary>
public static class BakeDiskBudget
{
    /// <summary>
    /// 无损 master 的经验压缩比（原始 RGB 字节 ÷ master 字节）。rc11 的实测样本都在 5 以上，
    /// 取 5 当下界：无损压缩率不可预知，预估只能当下界，不能当唯一防线（渲染中的周期复查仍在）。
    /// </summary>
    public const double MasterCompressionRatio = 5;

    /// <summary>预估之外还要留出的空闲空间：系统页面文件、成品另存与同机其它任务都在同一个盘上。</summary>
    public const ulong ReserveBytes = 10UL * 1024 * 1024 * 1024;

    /// <summary>磁盘空间不足时的 bake 状态。不在界面的"已完成"白名单里，不会被当成结果展示。</summary>
    public const string RejectedBakeStatus = "candidate_rejected_disk_space";

    /// <summary>直编组留在盘上的 RGBA 原帧：画质抽样 9 帧、闭合参照 3 帧与首帧。</summary>
    private const ulong RetainedFrames = QualityGate.MinimumSamples + 4;

    /// <summary>
    /// 一次烘焙的中间产物峰值预估；各组按自己录的帧数 P_g（plan 候选的 group_frames）。
    /// 所有档位都边渲染边编码成品（GPU 管线或 CPU 管道），不落无损 master；只剩上下并排的超宽透明组、渲染器量不出覆盖度时
    /// 退回 master，那一份开写前另有同口径的空间检查（<see cref="NativeRenderRunner"/>）。
    /// <para><see cref="IntermediateBytes"/>：各组留的十几帧原帧，残差组另有淡化窗口两侧与混合好的头段。</para>
    /// <para><see cref="StartSearchBytes"/>：残差组起点搜索的缩略图样本，两条路线都有，留到烘完。周期不是 16 的倍数时步长变小、
    /// 样本成倍增加（3803167460 两个残差组各 9.6 GB，是整案峰值的全部）。</para>
    /// <para><see cref="PlaybackBytes"/>：全部组的成品视频，按内嵌视频的参考码率估，要留到装配完成。</para>
    /// <para><see cref="CrossfadeBytes"/>：残差组头段与主体段拷包拼接时，最大那一组的成品多出的一份副本。</para>
    /// </summary>
    public readonly record struct Estimate(ulong Frames, int Groups, ulong IntermediateBytes, ulong StartSearchBytes, ulong PlaybackBytes,
        ulong CrossfadeBytes)
    {
        /// <summary>预估峰值（字节）。</summary>
        public ulong PeakBytes => IntermediateBytes + StartSearchBytes + PlaybackBytes + CrossfadeBytes;

        /// <summary>峰值加固定余量：低于这个数就不开烘。</summary>
        public ulong RequiredBytes => PeakBytes + ReserveBytes;

        /// <summary>预估是否成立：帧数、输出尺寸与视频组都已知时才有意义。</summary>
        public bool Known => Frames > 0 && Groups > 0 && PeakBytes > 0;

        public JsonObject ToJson() => new()
        {
            ["route"] = "direct",
            ["frames"] = Frames,
            ["groups"] = Groups,
            ["intermediate_bytes"] = IntermediateBytes,
            ["start_search_bytes"] = StartSearchBytes,
            ["playback_bytes"] = PlaybackBytes,
            ["crossfade_copy_bytes"] = CrossfadeBytes,
            ["peak_bytes"] = PeakBytes,
            ["reserve_bytes"] = ReserveBytes,
            ["required_bytes"] = RequiredBytes,
            ["master_compression_ratio"] = MasterCompressionRatio,
            ["basis"] = "Each group records its own frame count (group_frames, else the loop length). Every group's playback video at the " +
                "embedded-video reference bitrate, plus the residual groups' start-search samples (512-wide RGB thumbnails every stride frames over " +
                "P_g + min(P_g, P_min + crossfade), colour and alpha for transparent groups), plus 13 retained RGBA frames per group " +
                "(plus the crossfade window on both sides and the blended head for residual groups), plus one spliced copy of the largest " +
                "residual group's playback video. No lossless master is written."
        };
    }

    /// <summary>一份无损 master 的预估字节：帧数 × 编码像素 × 3 字节 ÷ 压缩比。</summary>
    public static ulong MasterBytes(ulong frames, double encodedPixels) => Bytes(frames * encodedPixels * 3 / MasterCompressionRatio);

    /// <summary>
    /// 按计划预估中间产物峰值。<paramref name="residualGroups"/> 是残差组下标；原帧量小，全部组都算。
    /// 尺寸或帧数未知时返回一个 <see cref="Estimate.Known"/> 为 false 的预估，调用方不做拦截。
    /// </summary>
    public static Estimate EstimatePeak(JsonObject plan, ulong frames, IReadOnlyCollection<int> residualGroups)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(residualGroups);
        JsonObject? settings = plan["settings"] as JsonObject;
        uint width = Unsigned(settings?["width"]), height = Unsigned(settings?["height"]);
        JsonObject[] groups = (plan["video_groups"] as JsonArray)?.OfType<JsonObject>().ToArray() ?? [];
        if (frames == 0 || width == 0 || height == 0 || groups.Length == 0) return new(frames, groups.Length, 0, 0, 0, 0);
        JsonObject? groupFrames = (plan["loop"]?["candidates"] as JsonArray)?.FirstOrDefault()?["group_frames"] as JsonObject;
        uint fpsNumerator = Unsigned(settings?["fps_numerator"]), fpsDenominator = Unsigned(settings?["fps_denominator"]);
        uint crossfadeFrames = fpsNumerator > 0 && fpsDenominator > 0 ? ResidualMasking.CrossfadeFrames(fpsNumerator, fpsDenominator) : 0;
        var recordedFrames = new ulong[groups.Length];
        var transparentGroups = new bool[groups.Length];
        ulong intermediate = 0;
        double playback = 0, crossfade = 0;
        for (int i = 0; i < groups.Length; ++i)
        {
            bool transparent = transparentGroups[i] = groups[i]["transparent"] is JsonValue value && value.TryGetValue(out bool packed) && packed;
            bool residual = residualGroups.Contains(i);
            ulong recorded = recordedFrames[i] = Unsigned(groupFrames?[groups[i]["id"]?.GetValue<string>() ?? ""]) is > 0 and var own ? own : frames;
            double pixels = EmbeddedVideoBudget.EncodedPixels(width, height, transparent);
            double groupPlayback = recorded * EmbeddedVideoBudget.ReferenceBytesPerFrame(pixels);
            playback += groupPlayback;
            intermediate += (ulong)width * height * 4 * (RetainedFrames + (residual ? 3UL * crossfadeFrames : 0));
            if (residual) crossfade = Math.Max(crossfade, groupPlayback);
        }
        // 起点搜索的窗口与步长照 LoopStartSelector.SearchAsync：步长 gcd(各残差组 P_g, 16)，窗口 P_g + min(P_g, P_min + 淡化取整到步长)。
        double search = 0;
        if (residualGroups.Count > 0)
        {
            ulong[] periods = [.. residualGroups.Select(i => recordedFrames[i])];
            ulong stride = ResidualMasking.StartSearchStride(periods.Aggregate(0UL, (gcd, period) => (ulong)BigInteger.GreatestCommonDivisor(gcd, period)));
            ulong tail = periods.Min() + (crossfadeFrames + stride - 1) / stride * stride;
            double sampleWidth = Math.Round(ResidualMasking.StartSearchSampleWidth * SwayRecurrenceSolver.SpeedLimitScale(width, height));
            double sampleBytes = sampleWidth * Math.Max(1, Math.Round(height * sampleWidth / width)) * 3;
            foreach (int i in residualGroups)
                search += (recordedFrames[i] + Math.Min(recordedFrames[i], tail) + stride - 1) / stride * sampleBytes *
                    (transparentGroups[i] ? 2 : 1);
        }
        return new(frames, groups.Length, intermediate, Bytes(search), Bytes(playback), Bytes(crossfade));
    }

    /// <summary>
    /// 输出目录所在卷的空闲空间够不够开烘。够用、或者读不出空闲空间（UNC、长路径前缀）时返回 null，不拦截；
    /// 不够时返回写进 bake.json 的拒绝记录，中英文各一句写明需要多少、现有多少、在哪个盘。
    /// </summary>
    public static JsonObject? Reject(JsonObject plan, ulong frames, IReadOnlyCollection<int> residualGroups, string outputDirectory)
    {
        Estimate estimate = EstimatePeak(plan, frames, residualGroups);
        if (!estimate.Known) return null;
        if (FreeSpace(outputDirectory) is not (string volume, long available)) return null;
        if (available >= 0 && (ulong)available >= estimate.RequiredBytes) return null;
        var rejection = new JsonObject
        {
            ["status"] = RejectedBakeStatus,
            ["volume"] = volume,
            ["available_bytes"] = available,
            ["required_bytes"] = estimate.RequiredBytes,
            ["estimate"] = estimate.ToJson(),
        };
        new Message("bake.insufficient_disk_space", [Gibibytes(estimate.PeakBytes), Gibibytes(ReserveBytes),
            Gibibytes(estimate.RequiredBytes), volume, Gibibytes((ulong)Math.Max(available, 0))]).Write(rejection, "reason");
        return rejection;
    }

    /// <summary>输出目录所在卷与它的空闲字节数；卷不是盘符形式（UNC、<c>\\?\</c> 前缀）或读不到时返回 null。</summary>
    internal static (string Volume, long Available)? FreeSpace(string path)
    {
        try
        {
            string volume = Path.GetPathRoot(Path.GetFullPath(path)) ?? "";
            if (volume.Length == 0) return null;
            return (volume, new DriveInfo(volume).AvailableFreeSpace);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>读一个非负整数。计划可能来自文件（JsonElement）也可能是内存里构造的值，两种都要读得出。</summary>
    private static uint Unsigned(JsonNode? node)
    {
        if (node is not JsonValue value) return 0;
        if (value.TryGetValue(out int signed)) return signed <= 0 ? 0 : (uint)signed;
        if (value.TryGetValue(out uint number)) return number;
        if (value.TryGetValue(out double real) && double.IsFinite(real)) return real <= 0 ? 0 : (uint)Math.Min(real, uint.MaxValue);
        return 0;
    }

    private static ulong Bytes(double value) =>
        !double.IsFinite(value) || value <= 0 ? 0 : value >= ulong.MaxValue ? ulong.MaxValue : (ulong)Math.Ceiling(value);

    private static string Gibibytes(ulong bytes) =>
        ((double)bytes / (1L << 30)).ToString("0.0", CultureInfo.InvariantCulture);
}
