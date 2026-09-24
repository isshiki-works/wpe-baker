using System.Globalization;
using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>
/// 播放版视频的硬件解码尺寸预检。所有数字集中在 <see cref="H264"/> / <see cref="Hevc"/> 两行表里并注明出处；
/// 补边与越限的算术在 Domain 的 DecodeDimensions。规划只做两件事：过小（或奇数）时把编码画布居中补边到下限，回放按原矩形取样，显示不变；
/// 补边后仍越过上限就列出越限项，由调用方在编码之前干净拒绝。编码后的 D3D11VA 实测照旧执行，预检不替代它。
/// <para>
/// 另外收着"目标播放机"这一侧的数字（<see cref="IntegratedH264"/> / <see cref="IntegratedHevc"/> 与
/// <see cref="ClassifyAdapter"/>）：烘焙机上的 D3D11VA 实测只代表烘焙机自己的显卡，播放机常常是核显。
/// 这部分只产生提示文案与结果字段，不参与任何判定，也不改变任何判据的强度。
/// </para>
/// </summary>
public static class HardwareDecodeDimensions
{
    public const string PassStatus = "pass";
    public const string PaddedStatus = "padded";
    public const string RejectedStatus = "rejected";

    /// <summary>一种编解码的尺寸限制。宏块两项只用于 H.264 的编码器选择（超出即改用 HEVC），不直接产生拒绝。</summary>
    public sealed record Limits(string Codec, string DisplayName, uint MinimumWidth, uint MinimumHeight,
        uint MaximumWidth, uint MaximumHeight, ulong? MaximumLumaSamples, ulong? MaximumMacroblocks,
        ulong? MaximumMacroblocksPerSecond, string BasisZh, string BasisEn, string[] Sources);

    // 取最严者：公开文档里各厂商下限取最大、上限取最小；本机实测比公开值更严的，按实测收紧并写明。
    public static readonly Limits H264 = new("h264", "H.264",
        MinimumWidth: 48, MinimumHeight: 64, MaximumWidth: 4096, MaximumHeight: 4096,
        MaximumLumaSamples: null, MaximumMacroblocks: 36864, MaximumMacroblocksPerSecond: 2073600,
        BasisZh: "NVDEC/Microsoft H.264 最小 48×48，本机 NVIDIA D3D11VA 实测高 48 失败故取 64；NVDEC/Intel H.264 最大 4096×4096",
        BasisEn: "NVDEC/Microsoft H.264 minimum 48×48, height raised to 64 after NVIDIA D3D11VA failed at 48 here; NVDEC/Intel H.264 maximum 4096×4096",
        Sources: [
            "NVIDIA Video Codec SDK 13.0, NVDEC Video Decoder API Programming Guide, decoder capability table: H.264 minimum 48x16; maximum 4096x4096 (Maxwell to Ada), 8192x8192 on Blackwell. https://docs.nvidia.com/video-technologies/video-codec-sdk/13.0/nvdec-video-decoder-api-prog-guide/index.html",
            "Microsoft Learn, Media Foundation H.264 Video Decoder, Format Constraints: minimum resolution 48x48, maximum 4096x2304 (DXVA guaranteed only to 1920x1088). https://learn.microsoft.com/windows/win32/medfound/h-264-video-decoder",
            "Intel media-driver docs/media_features.md: AVC hardware decode 4k (defined there as 4096x4096) on every listed platform. https://github.com/intel/media-driver/blob/master/docs/media_features.md",
            "ITU-T H.264 Table A-1 / FFmpeg libavcodec/h264_levels.c, level 5.2: MaxFS 36864 macroblocks, MaxMBPS 2073600 (existing encoder-selection rule).",
            "Local D3D11VA probe 2026-09-17 (same ffmpeg arguments as NativeRenderRunner.Hardware): NVIDIA GeForce RTX 5090 D v2 failed 3840x6, 3840x48, 4096x48, 48x48, 32x64 and passed 3840x50, 48x64, 64x58; AMD Radeon integrated graphics passed every size down to 16x16.",
            "AMD AMF Video Decode API documents no numeric resolution range; no stricter public AMD figure was found."]);

    public static readonly Limits Hevc = new("hevc", "HEVC",
        MinimumWidth: 144, MinimumHeight: 144, MaximumWidth: 8192, MaximumHeight: 8192,
        MaximumLumaSamples: 35651584, MaximumMacroblocks: null, MaximumMacroblocksPerSecond: null,
        BasisZh: "NVDEC HEVC 最小 144×144、最大 8192×8192；H.265 Level 6.2 最大亮度样本 35651584",
        BasisEn: "NVDEC HEVC minimum 144×144, maximum 8192×8192; H.265 Level 6.2 maximum luma picture size 35651584",
        Sources: [
            "NVIDIA Video Codec SDK 13.0, NVDEC Video Decoder API Programming Guide, decoder capability table: HEVC minimum 144x144; maximum 8192x8192 from Pascal (GP10x) through Blackwell. https://docs.nvidia.com/video-technologies/video-codec-sdk/13.0/nvdec-video-decoder-api-prog-guide/index.html",
            "Microsoft Learn, Media Foundation H.265/HEVC Video Decoder, Format Constraints: minimum resolution 48x48. https://learn.microsoft.com/windows/win32/medfound/h-265---hevc-video-decoder",
            "Intel media-driver docs/media_features.md: HEVC 8-bit hardware decode 8k on SKL/KBL/ICL/TGL (16k only on newer platforms). https://github.com/intel/media-driver/blob/master/docs/media_features.md",
            "ITU-T H.265 Table A.8: MaxLumaPs 35651584 for levels 6, 6.1 and 6.2.",
            "Local D3D11VA probe 2026-09-17: NVIDIA GeForce RTX 5090 D v2 failed 128x128 and 8192x64, passed 144x144, 144x136, 136x144, 8192x144 and 8192x3160; both NVIDIA and AMD Radeon integrated graphics failed 10216x3160 and passed 8192x3160."]);

    public static Limits For(string softwareEncoder) => softwareEncoder == "libx265" ? Hevc : H264;

    /// <summary>ITU-T H.265 表 A.8 的 MaxLumaSr（每秒亮度样本数）：Level 6 是 8K30 量级，6.1 是 8K60 量级。</summary>
    public const ulong HevcLevel6LumaSamplesPerSecond = 1_069_547_520, HevcLevel61LumaSamplesPerSecond = 2_139_095_040;

    /// <summary>
    /// 透明打包的方向：左右并排的总宽越过 HEVC 宽度上限时改为上下并排（上半 RGB、下半 alpha）。只看每半幅宽度，
    /// 编码端与解码端（着色器、接缝门）各自算出同一个结果，不另存字段。
    /// </summary>
    public static bool StackedVertically(bool packedAlpha, long halfWidth) => packedAlpha && halfWidth * 2 > Hevc.MaximumWidth;

    /// <summary>
    /// 内容尺寸越过 HEVC 硬解上限（宽、高、亮度样本；透明按两半幅算）就等比缩小到上限内，回放按图层原尺寸放大，
    /// 观感交给编码后的画质门与合成门判。透明时左右、上下两种并排各算一次，取保留像素多的（打平取左右并排）；
    /// 上下并排后的总高越过常见核显的 HEVC 高度上限（<see cref="IntegratedHevc"/>）时不用它，免得显示器接核显的用户被硬解检查拒绝。
    /// 没越限原样返回（输入须为偶数，与 Fit 一致）。
    /// </summary>
    public static (uint Width, uint Height) FitCeiling(uint width, uint height, bool packedAlpha)
    {
        uint halves = packedAlpha ? 2u : 1u;
        double luma = Math.Sqrt(Hevc.MaximumLumaSamples!.Value / ((double)halves * width * height));
        (uint Width, uint Height) Box(uint maximumWidth, uint maximumHeight) => EffectPrefixBakeService.Fit(width, height,
            (uint)Math.Min(maximumWidth, width * luma), (uint)Math.Min(maximumHeight, height * luma));
        var side = Box(Hevc.MaximumWidth / halves, Hevc.MaximumHeight);
        if (!packedAlpha) return side;
        var stacked = Box(Hevc.MaximumWidth, Hevc.MaximumHeight / 2);
        return stacked.Height * 2UL <= IntegratedHevc.MaximumHeight && (ulong)stacked.Width * stacked.Height > (ulong)side.Width * side.Height ? stacked : side;
    }

    // ---------------------------------------------------------------------------------------
    // 目标播放机一侧。硬解只能在真正跑它的那台机器上验，烘焙机（常是独显）验过不等于播放机（常是核显）能放。
    // 下面这些数字只用来写提示文案与结果字段，一条判据都不参与。
    // ---------------------------------------------------------------------------------------

    /// <summary>硬解实测的适用范围：只在烘焙机上验证过。</summary>
    public const string BakingMachineOnlyCaveat = "verified_on_baking_machine_only";

    /// <summary>提示种类：码流尺寸越过常见核显的硬解上限。</summary>
    public const string IntegratedCeilingHintKind = "beyond_common_integrated_decode_ceiling";

    public const string IntegratedAdapter = "integrated";
    public const string DiscreteAdapter = "discrete";
    public const string UnknownAdapterClass = "unknown";

    /// <summary>PCI-SIG 厂商号到厂商名，只作结果里的可读标签；不认识的按 0x 十六进制原样写出。</summary>
    public static string VendorName(uint vendorId) => vendorId switch
    {
        0x1002 or 0x1022 => "AMD",
        0x10de => "NVIDIA",
        0x8086 => "Intel",
        0x1414 => "Microsoft",
        0x13b5 => "ARM",
        0x5143 => "Qualcomm",
        _ => "0x" + vendorId.ToString("x4", CultureInfo.InvariantCulture),
    };

    /// <summary>
    /// 核显与独显的分界：DXGI 的 DXGI_ADAPTER_DESC1 没有这一位，只能按专用显存量分。核显的 DedicatedVideoMemory
    /// 是从系统内存里划的一小块（Intel 核显常见 128 MiB，AMD APU 常见 512 MiB），独显是整块板载显存，
    /// 取 1 GiB 为界两边都留足余量。
    /// </summary>
    public const ulong IntegratedDedicatedMemoryCeilingBytes = 1UL << 30;

    /// <summary>
    /// 按厂商号与显存量判断是核显还是独显。NVIDIA 在 Windows 桌面上没有核显产品，显存再小也按独显算：
    /// 宁可把独显算成独显（提示更强），也不能把独显误报成"核显已经验证过"。
    /// </summary>
    public static string ClassifyAdapter(uint vendorId, ulong dedicatedVideoMemoryBytes, ulong sharedSystemMemoryBytes) =>
        vendorId == 0x10de || dedicatedVideoMemoryBytes >= IntegratedDedicatedMemoryCeilingBytes ? DiscreteAdapter :
        dedicatedVideoMemoryBytes > 0 || sharedSystemMemoryBytes > 0 ? IntegratedAdapter : UnknownAdapterClass;

    /// <summary>常见核显的硬解上限。比 <see cref="Limits"/> 保守，只用于提示，不产生拒绝。</summary>
    public sealed record IntegratedCeiling(string Codec, string DisplayName, uint MaximumWidth, uint MaximumHeight,
        ulong MaximumLumaSamples, string BasisZh, string BasisEn, string[] Sources);

    public static readonly IntegratedCeiling IntegratedH264 = new("h264", "H.264",
        MaximumWidth: 4096, MaximumHeight: 4096, MaximumLumaSamples: 4096UL * 4096,
        BasisZh: "Intel 核显 AVC 硬解上限 4096×4096，Microsoft Media Foundation H.264 解码器上限 4096×2304；独显要宽得多（NVIDIA Blackwell 到 8192×8192），所以在独显上过了不代表核显能过",
        BasisEn: "Intel integrated AVC decode tops out at 4096×4096 and the Media Foundation H.264 decoder at 4096×2304, while discrete parts go much wider (NVIDIA Blackwell to 8192×8192), so passing on a discrete GPU says nothing about an integrated one",
        Sources: [
            "Intel media-driver docs/media_features.md: AVC hardware decode 4k (defined there as 4096x4096) on every listed platform. https://github.com/intel/media-driver/blob/master/docs/media_features.md",
            "Microsoft Learn, Media Foundation H.264 Video Decoder, Format Constraints: maximum 4096x2304, DXVA guaranteed only to 1920x1088. https://learn.microsoft.com/windows/win32/medfound/h-264-video-decoder",
            "NVIDIA Video Codec SDK 13.0 NVDEC capability table: H.264 up to 4096x4096 (Maxwell to Ada) and 8192x8192 on Blackwell, i.e. discrete parts exceed the integrated ceiling. https://docs.nvidia.com/video-technologies/video-codec-sdk/13.0/nvdec-video-decoder-api-prog-guide/index.html"]);

    public static readonly IntegratedCeiling IntegratedHevc = new("hevc", "HEVC",
        MaximumWidth: 8192, MaximumHeight: 4320, MaximumLumaSamples: 7680UL * 4320,
        BasisZh: "Intel 核显 HEVC 8-bit 硬解在 SKL–TGL 上是 8k，厂商宣传的 8K 指 7680×4320；本机 AMD 核显只实测到 8192×3160。故宽取 8192、高取 4320、亮度样本数取 8K UHD 的 33177600，三者任一越过就提示",
        BasisEn: "Intel integrated HEVC 8-bit decode is listed as 8k on SKL-TGL and the advertised 8K means 7680×4320; the AMD integrated adapter here was only measured to 8192×3160. The ceiling is therefore width 8192, height 4320 and 33177600 luma samples (8K UHD), and exceeding any of the three raises the hint",
        Sources: [
            "Intel media-driver docs/media_features.md: HEVC 8-bit hardware decode 8k on SKL/KBL/ICL/TGL (16k only on newer platforms). https://github.com/intel/media-driver/blob/master/docs/media_features.md",
            "ITU-T H.265 Table A.8: MaxLumaPs 35651584 for levels 6, 6.1 and 6.2; 8K UHD 7680x4320 is 33177600 luma samples.",
            "AMD AMF Video Decode API documents no numeric resolution range; local D3D11VA probe 2026-09-17 measured AMD Radeon integrated graphics passing 8192x3160 and failing 10216x3160."]);

    /// <summary>按 ffprobe 的 codec_name 取常见核显上限；不是 H.264/HEVC 就没有可提示的数字。</summary>
    public static IntegratedCeiling? CeilingFor(string? codecName) => (codecName ?? "").ToLowerInvariant() switch
    {
        "h264" or "avc" or "avc1" or "h.264" => IntegratedH264,
        "hevc" or "h265" or "h.265" or "hev1" or "hvc1" => IntegratedHevc,
        _ => null,
    };

    /// <summary>一条目标播放机提示：这段码流的尺寸越过了常见核显的硬解上限。</summary>
    public sealed record TargetHint(IntegratedCeiling Ceiling, uint Width, uint Height, IReadOnlyList<DecodeViolation> Exceeded)
    {
        public JsonObject ToJson() => new()
        {
            ["kind"] = IntegratedCeilingHintKind,
            ["codec"] = Ceiling.Codec,
            ["bitstream_extent"] = new JsonArray(Width, Height),
            ["common_integrated_ceiling"] = new JsonObject
            {
                ["maximum_width"] = Ceiling.MaximumWidth,
                ["maximum_height"] = Ceiling.MaximumHeight,
                ["maximum_luma_samples"] = Ceiling.MaximumLumaSamples,
                ["sources"] = new JsonArray(Ceiling.Sources.Select(source => (JsonNode?)JsonValue.Create(source)).ToArray()),
            },
            ["exceeded"] = new JsonArray(Exceeded.Select(violation => (JsonNode?)new JsonObject
            {
                ["measure"] = violation.Measure, ["actual"] = violation.Actual, ["limit"] = violation.Limit,
            }).ToArray()),
            ["scope"] = "Advisory only, from published integrated-GPU decode ceilings. It is vendor-agnostic, changes no acceptance criterion and does not replace running the probe on the playback machine.",
        };

        public string ExceededText(string language) => HardwareDecodeDimensions.ViolationText(Exceeded, language);
        public string CeilingText() => Extent(Ceiling.MaximumWidth, Ceiling.MaximumHeight);
    }

    /// <summary>码流尺寸对常见核显上限的提示；没有越限（或不是认识的编解码）返回 null。</summary>
    public static TargetHint? HintFor(string? codecName, uint width, uint height)
    {
        if (CeilingFor(codecName) is not { } ceiling || width == 0 || height == 0) return null;
        var exceeded = DecodeDimensions.Violations(width, height, ceiling.MaximumWidth, ceiling.MaximumHeight, ceiling.MaximumLumaSamples);
        return exceeded.Count == 0 ? null : new TargetHint(ceiling, width, height, exceeded);
    }

    /// <summary>越限项列成一句，中英各一套。</summary>
    public static string ViolationText(IEnumerable<DecodeViolation> violations, string language) =>
        string.Join(NormalizedChinese(language) ? "、" : ", ", violations.Select(violation => NormalizedChinese(language)
            ? violation.Measure switch { "width" => "宽", "height" => "高", _ => "亮度样本数" } +
              $" {violation.Actual.ToString(CultureInfo.InvariantCulture)} > {violation.Limit.ToString(CultureInfo.InvariantCulture)}"
            : violation.Measure switch { "width" => "width", "height" => "height", _ => "luma samples" } +
              $" {violation.Actual.ToString(CultureInfo.InvariantCulture)} > {violation.Limit.ToString(CultureInfo.InvariantCulture)}"));

    private static bool NormalizedChinese(string language) => MessageCatalog.NormalizeLanguage(language) == MessageCatalog.Chinese;

    /// <summary>一次预检的结果。Content 是每半幅的逻辑内容，Padded 是每半幅补边后的画布，Stored 是实际编码尺寸。</summary>
    public sealed record Plan(string Status, string SoftwareEncoder, bool PackedAlpha, uint ContentWidth, uint ContentHeight,
        uint PaddedWidth, uint PaddedHeight, uint OffsetX, uint OffsetY, IReadOnlyList<DecodeViolation> Violations)
    {
        public Limits Limits => For(SoftwareEncoder);
        public bool Vertical => StackedVertically(PackedAlpha, PaddedWidth);
        public uint StoredWidth => checked(PaddedWidth * (PackedAlpha && !Vertical ? 2u : 1u));
        public uint StoredHeight => checked(PaddedHeight * (Vertical ? 2u : 1u));
        public bool Padded => PaddedWidth != ContentWidth || PaddedHeight != ContentHeight;
        /// <summary>编码后每秒亮度样本数（按实际编码尺寸 × 帧率）。</summary>
        public double LumaSamplesPerSecond { get; init; }
        public bool Rejected => Status == RejectedStatus;

        public JsonObject ToJson()
        {
            JsonObject json = new() {
                ["status"] = Status,
                ["codec"] = Limits.Codec,
                ["software_encoder"] = SoftwareEncoder,
                ["pixel_packing"] = PackedAlpha ? Vertical ? "rgba_top_bottom" : "rgba_side_by_side" : "rgb",
                ["content_extent"] = new JsonArray(ContentWidth, ContentHeight),
                ["padded_extent"] = new JsonArray(PaddedWidth, PaddedHeight),
                ["encoded_extent"] = new JsonArray(StoredWidth, StoredHeight),
                ["padding"] = !Padded ? null : new JsonObject
                {
                    ["left"] = OffsetX, ["top"] = OffsetY,
                    ["right"] = PaddedWidth - ContentWidth - OffsetX, ["bottom"] = PaddedHeight - ContentHeight - OffsetY,
                    ["applies_to"] = PackedAlpha ? "each half of the side-by-side RGB/alpha frame" : "the RGB frame",
                    ["fill"] = "transparent black (RGB 0, alpha 0)",
                    ["playback_sampling"] = "UV is remapped to the original content rectangle and clamped half a texel inside it, so the displayed rectangle does not change.",
                },
                ["limits"] = new JsonObject
                {
                    ["codec"] = Limits.Codec, ["minimum_width"] = Limits.MinimumWidth, ["minimum_height"] = Limits.MinimumHeight,
                    ["maximum_width"] = Limits.MaximumWidth, ["maximum_height"] = Limits.MaximumHeight,
                    ["maximum_luma_samples"] = Limits.MaximumLumaSamples, ["alignment"] = DecodeDimensions.ChromaAlignment,
                    ["sources"] = new JsonArray(Limits.Sources.Select(source => (JsonNode?)JsonValue.Create(source)).ToArray()),
                },
                ["violations"] = new JsonArray(Violations.Select(violation => (JsonNode?)new JsonObject
                {
                    ["measure"] = violation.Measure, ["actual"] = violation.Actual, ["limit"] = violation.Limit,
                }).ToArray()),
                ["scope"] = "Dimension preflight from published decoder limits and local D3D11VA measurements, applied before encoding. It does not replace the actual hardware decode probe of the encoded file.",
            };
            // 只提示不拒绝：亮度样本率越过 HEVC Level 6（8K30 量级）就要 Level 6.1 以上的硬解，较老的硬件可能跟不上实时。
            if (Limits == Hevc && LumaSamplesPerSecond > HevcLevel6LumaSamplesPerSecond)
                json["luma_rate_hint"] = new JsonObject {
                    ["luma_samples_per_second"] = Math.Round(LumaSamplesPerSecond),
                    ["hevc_level_needed"] = LumaSamplesPerSecond > HevcLevel61LumaSamplesPerSecond ? "6.2" : "6.1",
                    ["scope"] = "Advisory only: the luma sample rate exceeds HEVC Level 6 (ITU-T H.265 Table A.8 MaxLumaSr), so playback needs a Level 6.1+ hardware decoder; older decoders may not keep up in real time. Not a rejection." };
            return json;
        }

        public string ViolationText(string language) => HardwareDecodeDimensions.ViolationText(Violations, language);

        public string PackingText(string language) => MessageCatalog.NormalizeLanguage(language) == MessageCatalog.Chinese
            ? Limits.DisplayName + (PackedAlpha ? Vertical ? "，透明通道上下并排" : "，透明通道左右并排" : "，不透明 RGB")
            : Limits.DisplayName + (PackedAlpha ? Vertical ? ", alpha packed top and bottom" : ", alpha packed side by side" : ", opaque RGB");
    }

    public static string Extent(uint width, uint height) =>
        width.ToString(CultureInfo.InvariantCulture) + "×" + height.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// 规划一段播放版视频的编码画布。<paramref name="contentWidth"/> 是每半幅（不透明时即整幅）的内容宽度。
    /// 编码器按补边后的实际编码尺寸选择；补边只增不减，编码器最多从 H.264 切到 HEVC 一次。
    /// </summary>
    public static Plan Evaluate(uint contentWidth, uint contentHeight, bool packedAlpha, uint fpsNumerator, uint fpsDenominator)
    {
        if (contentWidth == 0 || contentHeight == 0) throw new ArgumentException("Hardware decode preflight requires a positive content extent.");
        if (fpsNumerator == 0 || fpsDenominator == 0) throw new ArgumentException("Hardware decode preflight requires a positive rational frame rate.");
        uint halves = packedAlpha ? 2u : 1u;
        (uint Width, uint Height) Stored(uint w, uint h) => StackedVertically(packedAlpha, w) ? (w, checked(h * 2)) : (checked(w * halves), h);
        (uint width, uint offsetX) = DecodeDimensions.Grow(contentWidth, contentWidth);
        (uint height, uint offsetY) = DecodeDimensions.Grow(contentHeight, contentHeight);
        (uint storedWidth, uint storedHeight) = Stored(width, height);
        string encoder = PlaybackEncodeProfile.SelectPlaybackEncoder(storedWidth, storedHeight, fpsNumerator, fpsDenominator);
        for (int pass = 0; ; ++pass)
        {
            Limits limits = For(encoder);
            (width, offsetX) = DecodeDimensions.Grow(contentWidth, (limits.MinimumWidth + halves - 1) / halves);
            (height, offsetY) = DecodeDimensions.Grow(contentHeight, limits.MinimumHeight);
            (storedWidth, storedHeight) = Stored(width, height);
            string next = PlaybackEncodeProfile.SelectPlaybackEncoder(storedWidth, storedHeight, fpsNumerator, fpsDenominator);
            if (next == encoder) break;
            if (pass >= 2) throw new InvalidOperationException("Hardware decode preflight did not converge on one encoder.");
            encoder = next;
        }
        Limits chosen = For(encoder);
        var violations = DecodeDimensions.Violations(storedWidth, storedHeight, chosen.MaximumWidth, chosen.MaximumHeight, chosen.MaximumLumaSamples);
        string status = violations.Count > 0 ? RejectedStatus : width != contentWidth || height != contentHeight ? PaddedStatus : PassStatus;
        return new(status, encoder, packedAlpha, contentWidth, contentHeight, width, height, offsetX, offsetY, violations)
            { LumaSamplesPerSecond = (double)storedWidth * storedHeight * fpsNumerator / fpsDenominator };
    }


    /// <summary>
    /// 硬件解码下限对裁剪区的要求：在捕获范围内对称扩大裁剪区（只会多带进透明/已渲染像素，图层几何随裁剪区同步），
    /// 捕获本身不够大时保留原区，由预检记录越限。
    /// </summary>
    public static CacheRegion GrowRegion(CacheRegion region, bool packedAlpha, uint fpsNumerator, uint fpsDenominator)
    {
        region.Validate();
        Plan plan = Evaluate((uint)region.Width, (uint)region.Height, packedAlpha, fpsNumerator, fpsDenominator);
        if (!plan.Padded) return region;
        (int x, int width) = DecodeDimensions.Expand(region.X, region.Width, (int)plan.PaddedWidth, region.CaptureWidth);
        (int y, int height) = DecodeDimensions.Expand(region.Y, region.Height, (int)plan.PaddedHeight, region.CaptureHeight);
        var grown = region with { X = x, Y = y, Width = width, Height = height };
        grown.Validate();
        return grown;
    }

    /// <summary>
    /// analyze 阶段按源纹理的图像尺寸预估特效前缀缓存的编码尺寸。透明与否要到烘焙时看首帧才知道，所以两种打包都算；
    /// 只有越限（不论透明与否，或仅在透明时）才写 unresolved 提示，补边只作记录。
    /// </summary>
    internal static JsonArray PredictEffectPrefixCaches(JsonObject report, ProjectSource source, string? assets,
        HybridAnalyzeRequest request, JsonObject projection)
    {
        var predictions = new JsonArray();
        static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue(out string? text) ? text : null;
        foreach (JsonObject cache in (report["effect_prefix_caches"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (cache["owner_layer_id"] is not JsonValue ownerValue || !ownerValue.TryGetValue(out int owner)) continue;
            var entry = new JsonObject { ["owner_layer_id"] = owner, ["source_image"] = cache["source_image"]?.DeepClone(),
                ["basis"] = "Source texture image extent with the bake's own fit rule; the bake re-plans from the actual capture extent before encoding." };
            predictions.Add(entry);
            try
            {
                // 缓存能进 plan 就已过 ValidateSource：单个 genericimage 通道、单张非帧缓冲纹理。这里仍逐级判空，读不到只记 not_predicted。
                if (Text(cache["source_image"]) is not string image || Text(source.ReadJson(image)["material"]) is not string materialResource ||
                    source.ReadJson(materialResource)["passes"] is not JsonArray { Count: > 0 } passes ||
                    passes[0]?["textures"] is not JsonArray { Count: > 0 } textures || Text(textures[0]) is not string texture)
                {
                    entry["status"] = "not_predicted"; new Message("hardware_decode.owner_unreadable").Write(entry, "reason"); continue;
                }
                JsonObject model = source.ReadJson(image);
                string resource = "materials/" + texture + ".tex";
                if (!TextureContainer.TryReadImageExtent(source, assets, resource, out uint sourceWidth, out uint sourceHeight, out string reason))
                {
                    entry["status"] = "not_predicted"; entry["reason"] = reason; continue;
                }
                bool puppet = model.ContainsKey("puppet");
                (uint encodeWidth, uint encodeHeight) = puppet
                    ? EffectPrefixBakeService.FitAtlas(sourceWidth, sourceHeight, request.Width, request.Height,
                        projection["visible_width"]?.GetValue<double>() ?? 0, projection["visible_height"]?.GetValue<double>() ?? 0)
                    : EffectPrefixBakeService.Fit(sourceWidth, sourceHeight, request.Width, request.Height);
                (uint opaqueWidth, uint opaqueHeight) = FitCeiling(encodeWidth, encodeHeight, false);
                (uint packedWidth, uint packedHeight) = FitCeiling(encodeWidth, encodeHeight, true);
                Plan opaque = Evaluate(opaqueWidth, opaqueHeight, false, request.FpsNumerator, request.FpsDenominator);
                Plan transparent = Evaluate(packedWidth, packedHeight, true, request.FpsNumerator, request.FpsDenominator);
                entry["texture"] = resource;
                entry["predicted_source_extent"] = new JsonArray(sourceWidth, sourceHeight);
                entry["sampling_basis"] = puppet ? "source_atlas_at_projected_canvas_density" : "source_image_fits_output";
                entry["opaque"] = opaque.ToJson();
                entry["transparent"] = transparent.ToJson();
                entry["status"] = opaque.Padded || transparent.Padded ? "predicted_padding" : "predicted_pass";
            }
            catch (Exception error) when (error is InvalidDataException or IOException or System.Text.Json.JsonException or
                InvalidOperationException or FormatException or ArgumentException or UnauthorizedAccessException)
            {
                entry["status"] = "not_predicted";
                entry["reason"] = error.Message;
            }
        }
        return predictions;
    }
}
