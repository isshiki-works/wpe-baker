using System.Globalization;
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

    /// <summary>GPU 直编组渲染器留在盘上的 RGBA 原帧上限：画质抽样与闭合参照最多 32 帧（NativeRenderRunner 的上限）加首帧。</summary>
    private const ulong GpuRetainedFrames = 33;

    /// <summary>
    /// 一次烘焙的中间产物峰值预估，按实际路线算；各组按自己录的帧数 P_g（plan 候选的 group_frames）。
    /// <para><see cref="IntermediateBytes"/>：同时在飞的组的中间文件。GPU 直编不落 master，只有渲染器留的几十帧原帧；
    /// 软件档的透明组与残差组落无损 master。</para>
    /// <para><see cref="PlaybackBytes"/>：全部组的成品视频，按内嵌视频的参考码率估，要留到装配完成。</para>
    /// <para><see cref="CrossfadeBytes"/>：软件档残差组在接缝处淡化改写 master 时的那一份副本（GPU 路线在渲染器里淡化，没有）。</para>
    /// </summary>
    public readonly record struct Estimate(ulong Frames, int Groups, bool Gpu, ulong IntermediateBytes, ulong PlaybackBytes, ulong CrossfadeBytes)
    {
        /// <summary>预估峰值（字节）。</summary>
        public ulong PeakBytes => IntermediateBytes + PlaybackBytes + CrossfadeBytes;

        /// <summary>峰值加固定余量：低于这个数就不开烘。</summary>
        public ulong RequiredBytes => PeakBytes + ReserveBytes;

        /// <summary>预估是否成立：帧数、输出尺寸与视频组都已知时才有意义。</summary>
        public bool Known => Frames > 0 && Groups > 0 && PeakBytes > 0;

        public JsonObject ToJson() => new()
        {
            ["route"] = Gpu ? "gpu_direct" : "lossless_master",
            ["frames"] = Frames,
            ["groups"] = Groups,
            ["intermediate_bytes"] = IntermediateBytes,
            ["playback_bytes"] = PlaybackBytes,
            ["crossfade_copy_bytes"] = CrossfadeBytes,
            ["peak_bytes"] = PeakBytes,
            ["reserve_bytes"] = ReserveBytes,
            ["required_bytes"] = RequiredBytes,
            ["master_compression_ratio"] = MasterCompressionRatio,
            ["basis"] = "Each group records its own frame count (group_frames, else the loop length). Every group's playback video at the " +
                "embedded-video reference bitrate, plus the intermediates of the groups in flight: on the GPU route up to 33 retained RGBA frames " +
                "(plus the crossfade window on both sides for residual groups); on the software route a lossless master (frames x encoded pixels x 3 " +
                "bytes / 5) for transparent and residual groups, plus one crossfade copy of the largest residual master."
        };
    }

    /// <summary>一份无损 master 的预估字节：帧数 × 编码像素 × 3 字节 ÷ 压缩比。</summary>
    public static ulong MasterBytes(ulong frames, double encodedPixels) => Bytes(frames * encodedPixels * 3 / MasterCompressionRatio);

    /// <summary>
    /// 按计划与路线预估中间产物峰值。<paramref name="gpu"/> 为 GPU 直编路线；<paramref name="residualGroups"/> 是残差组下标。
    /// 软件档同时在飞的组按 groupParallel + 1 份算（当前组的 master 要等它自己编完才删）；GPU 路线的原帧量小，全部组都算。
    /// 尺寸或帧数未知时返回一个 <see cref="Estimate.Known"/> 为 false 的预估，调用方不做拦截。
    /// </summary>
    public static Estimate EstimatePeak(JsonObject plan, ulong frames, int groupParallel, bool gpu, IReadOnlyCollection<int> residualGroups)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(residualGroups);
        JsonObject? settings = plan["settings"] as JsonObject;
        uint width = Unsigned(settings?["width"]), height = Unsigned(settings?["height"]);
        JsonObject[] groups = (plan["video_groups"] as JsonArray)?.OfType<JsonObject>().ToArray() ?? [];
        if (frames == 0 || width == 0 || height == 0 || groups.Length == 0) return new(frames, groups.Length, gpu, 0, 0, 0);
        JsonObject? groupFrames = (plan["loop"]?["candidates"] as JsonArray)?.FirstOrDefault()?["group_frames"] as JsonObject;
        uint fpsNumerator = Unsigned(settings?["fps_numerator"]), fpsDenominator = Unsigned(settings?["fps_denominator"]);
        ulong fadeWindow = fpsNumerator > 0 && fpsDenominator > 0 ? 2UL * ResidualMasking.CrossfadeFrames(fpsNumerator, fpsDenominator) : 0;
        var intermediates = new List<ulong>();
        double playback = 0;
        ulong crossfade = 0;
        for (int i = 0; i < groups.Length; ++i)
        {
            bool transparent = groups[i]["transparent"] is JsonValue value && value.TryGetValue(out bool packed) && packed;
            bool residual = residualGroups.Contains(i);
            ulong recorded = Unsigned(groupFrames?[groups[i]["id"]?.GetValue<string>() ?? ""]) is > 0 and var own ? own : frames;
            double pixels = EmbeddedVideoBudget.EncodedPixels(width, height, transparent);
            playback += recorded * EmbeddedVideoBudget.ReferenceBytesPerFrame(pixels);
            ulong master = HybridBakeService.AllowsDirectPlayback(!transparent, residual, 0) ? 0 : MasterBytes(recorded, pixels);
            intermediates.Add(gpu ? (ulong)width * height * 4 * (GpuRetainedFrames + (residual ? fadeWindow : 0)) : master);
            if (!gpu && residual) crossfade = Math.Max(crossfade, master);
        }
        int inFlight = gpu ? groups.Length : Math.Clamp(groupParallel + 1, 1, groups.Length);
        ulong intermediate = intermediates.OrderDescending().Take(inFlight).Aggregate(0UL, (sum, bytes) => sum + bytes);
        return new(frames, groups.Length, gpu, intermediate, Bytes(playback), crossfade);
    }

    /// <summary>
    /// 输出目录所在卷的空闲空间够不够开烘。够用、或者读不出空闲空间（UNC、长路径前缀）时返回 null，不拦截；
    /// 不够时返回写进 bake.json 的拒绝记录，中英文各一句写明需要多少、现有多少、在哪个盘。
    /// </summary>
    public static JsonObject? Reject(JsonObject plan, ulong frames, int groupParallel, bool gpu, IReadOnlyCollection<int> residualGroups,
        string outputDirectory)
    {
        Estimate estimate = EstimatePeak(plan, frames, groupParallel, gpu, residualGroups);
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
