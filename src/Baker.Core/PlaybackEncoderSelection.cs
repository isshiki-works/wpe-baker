using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Baker.Core;

/// <summary>
/// 播放版视频的编码器档位选择与回退；Vulkan 路径可直接生成成品，其他路径按需保留软件无损 master。
/// </summary>
public static partial class PlaybackEncoderSelection
{
    /// <summary>渲染器内经 NVENC SDK 编 AV1 的编码名；没有 FFmpeg/Vulkan 回退，开不了就由调用方降一档。</summary>
    public const string Av1Nvenc = "av1_nvenc";

    /// <summary>
    /// NVIDIA 上 GPU 直编的格式偏好（用户定 AV1 → HEVC → H.264，末档 H.264/HEVC 仍按宽度规则，不在这里）。只列 Media Foundation
    /// 能解的格式：WPE 经 MF 放视频，AV1、HEVC 靠商店扩展（9MVZQVXJBQ9V、9N4WGH0Z6VHQ）。显卡能否编 AV1 由渲染器开 NVENC 时实测。
    /// 非 NVIDIA 或查询失败返回空，维持原规则。
    /// </summary>
    public static string[] PreferredGpuCodecs(string? deviceUuid)
    {
        try
        {
            IReadOnlyList<VulkanDeviceInfo> devices = VulkanDevices.Enumerate();
            VulkanDeviceInfo? target = deviceUuid is null
                ? devices.FirstOrDefault(d => d.DeviceType == VulkanDeviceType.DiscreteGpu) ?? devices.FirstOrDefault()
                : devices.FirstOrDefault(d => string.Equals(d.DeviceUuid, deviceUuid, StringComparison.OrdinalIgnoreCase));
            if (target?.VendorId != 0x10DE) return [];
            // 子类型 FOURCC：'AV01'、'HEVC'。
            return [.. new[] { (Codec: Av1Nvenc, FourCc: 0x31305641u), (Codec: "hevc_vulkan", FourCc: 0x43564548u) }
                .Where(c => MediaFoundationDecoders(c.FourCc) > 0).Select(c => c.Codec)];
        }
        catch (Exception error) when (error is not OperationCanceledException) { return []; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MftTypeInfo { public Guid Major, Sub; }
    [DllImport("mfplat.dll")] private static extern int MFStartup(uint version, uint flags);
    [DllImport("mfplat.dll")] private static extern int MFShutdown();
    [DllImport("mfplat.dll")]
    private static extern int MFTEnumEx(Guid category, uint flags, ref MftTypeInfo input, IntPtr output, out IntPtr activates, out uint count);

    /// <summary>Media Foundation 里能解这个 FOURCC 的视频解码器个数（含商店扩展注册的）。</summary>
    private static int MediaFoundationDecoders(uint fourCc)
    {
        // MFMediaType_Video；视频子类型 GUID 是 FOURCC-0000-0010-8000-00AA00389B71；MFT_CATEGORY_VIDEO_DECODER。
        var input = new MftTypeInfo { Major = new("73646976-0000-0010-8000-00aa00389b71"),
            Sub = new(fourCc, 0, 0x10, 0x80, 0, 0, 0xaa, 0, 0x38, 0x9b, 0x71) };
        if (MFStartup(0x20070, 1) < 0) return 0;
        try
        {
            // SYNCMFT | ASYNCMFT | HARDWARE | LOCALMFT | SORTANDFILTER
            if (MFTEnumEx(new("d6c02d4b-6833-45b4-971a-05a4b04bab91"), 0x57, ref input, IntPtr.Zero, out IntPtr array, out uint count) < 0)
                return 0;
            for (int i = 0; i < count; ++i) Marshal.Release(Marshal.ReadIntPtr(array, i * IntPtr.Size));
            Marshal.FreeCoTaskMem(array);
            return (int)count;
        }
        finally { MFShutdown(); }
    }

    public const string Software = "software";
    public const string Mf = "mf";
    public const string Nvenc = "nvenc";
    public const string Qsv = "qsv";
    public const string Amf = "amf";
    public const string Auto = "auto";
    public const string Vulkan = "vulkan";

    /// <summary>CLI 与界面允许的取值，顺序即界面里的展示顺序。</summary>
    public static readonly string[] Choices = [Software, Auto, Vulkan, Mf, Nvenc, Qsv, Amf];

    /// <summary>
    /// auto 在渲染器支持整条 GPU 路线时先取 vulkan（见 NativeRenderRunner.ResolvePlaybackEncoderAsync）；
    /// 不支持时按这个顺序挑第一个可用的 ffmpeg 硬件档位。
    /// mf 排第一是因为它是 Windows 上唯一的厂商无关通道（Intel 落 QSV、AMD 落 VCE、NVIDIA 落 NVENC），
    /// 基准平台是核显，厂商专用档位只作附加。注意 mf 可用不等于硬件可用，见 ParseMfProbe。
    /// </summary>
    internal static readonly string[] AutoOrder = [Mf, Nvenc, Qsv, Amf];

    /// <summary>每个硬件档位都要求 HEVC 与 H.264 两条都在，避免按分辨率切换编码器时半路没有可用档位。</summary>
    public static string[] RequiredEncoders(string kind) => kind switch
    {
        Mf => ["hevc_mf", "h264_mf"],
        Nvenc => ["hevc_nvenc", "h264_nvenc"],
        Qsv => ["hevc_qsv", "h264_qsv"],
        Amf => ["hevc_amf", "h264_amf"],
        Vulkan => ["hevc_vulkan", "h264_vulkan"],
        _ => [],
    };

    /// <summary>把请求值归一化；null 与空串表示默认的 auto（本机 GPU 路线优先）。</summary>
    public static string Normalize(string? requested)
    {
        string value = (requested ?? Auto).Trim().ToLowerInvariant();
        if (value.Length == 0) return Auto;
        if (!Choices.Contains(value))
            throw new ArgumentException($"Playback encoder must be one of {string.Join(", ", Choices)}.");
        return value;
    }

    [GeneratedRegex(@"^\s*[VAS.][F.][S.][X.][B.][D.]\s+(?<name>[A-Za-z0-9_]+)\s", RegexOptions.Multiline)]
    private static partial Regex EncoderLine();

    /// <summary>解析 <c>ffmpeg -encoders</c> 的输出。只认编码器表格里的名字，不做任何猜测。</summary>
    public static HashSet<string> ParseEncoders(string text)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in EncoderLine().Matches(text ?? "")) names.Add(match.Groups["name"].Value);
        // 表头里的 "V....D = ..." 图例不会命中上面的格式，但名字列若出现保留字仍要排除。
        names.Remove("=");
        return names;
    }

    /// <summary>探测硬件 MFT 时试编的帧数：够走完 MFT 激活与类型协商即可，不测速度。</summary>
    public const int MfProbeFrames = 2;

    /// <summary>
    /// mf 档位的硬件探测命令。ffmpeg 没有“只问不编”的接口，只能真编几帧：
    /// <c>-hw_encoding true</c> 会把 MFT 枚举限制到 MFT_ENUM_FLAG_HARDWARE，落不到硬件就直接失败。
    /// 包内 ffmpeg 没有 lavfi，所以拿真实输入的头几帧当探针。
    /// </summary>
    public static string[] MfProbeArguments(string encoder, string input, string output) =>
        ["-hide_banner", "-loglevel", "verbose", "-nostdin", "-y", "-i", input,
            "-frames:v", MfProbeFrames.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-an", "-c:v", encoder, "-hw_encoding", "true", "-f", "mp4", output];

    [GeneratedRegex(@"MFT name: '(?<name>[^']*)'")]
    private static partial Regex MftNameLine();

    [GeneratedRegex(@"\((?<code>MF_E_[A-Z0-9_]+)\)")]
    private static partial Regex MfErrorCode();

    /// <summary>
    /// 解析硬件 MFT 探测结果。退出码为 0 才算硬件可用；失败时带回 MF_E_* 原因码，便于在 bake.json 里区分
    /// “这台机器没有硬件 MFT”与“ffmpeg 自身走不通硬件 MFT”。
    /// </summary>
    public static (bool Hardware, string? Mft, string? Failure) ParseMfProbe(int exitCode, string text)
    {
        string body = text ?? "";
        MatchCollection names = MftNameLine().Matches(body);
        // 探测成功时最后一条 MFT name 就是实际激活的硬件 MFT；失败时它只是枚举到却用不了的那一个，不当成结论。
        string? mft = names.Count == 0 ? null : names[^1].Groups["name"].Value;
        if (exitCode == 0) return (true, mft, null);
        Match error = MfErrorCode().Match(body);
        return (false, null, error.Success ? error.Groups["code"].Value : null);
    }

    /// <summary>硬件 MFT 不可用时写进 bake.json 的中文说明；软件 MFT 仍可用，不是回退软件档位的理由。</summary>
    public static string MfSoftwareOnlyNote(string? failure, string? attempted) =>
        "mf 落到微软自带的软件 H.264/HEVC MFT，没有走到厂商硬件 MFT" +
        (attempted is null ? "" : $"（枚举到 {attempted}）") +
        (failure is null ? "" : $"：{failure}") +
        "。ffmpeg 的 mfenc 只在编码第一帧 D3D11 画面时才发 MFT_MESSAGE_SET_D3D_MANAGER，" +
        "而 D3D-aware 的厂商 MFT 在更早的 SetOutputType 就要求已绑定 D3D manager，属上游实现顺序问题，改构建选项无效。";

    /// <summary>
    /// 决定实际使用的档位。硬件不可用时一律回退软件编码，并给出可写进 bake.json 的中文理由。
    /// </summary>
    public static (string Used, string? FallbackReason) Resolve(string? requested, IReadOnlySet<string> available)
    {
        string kind = Normalize(requested);
        if (kind == Software) return (Software, null);
        bool Usable(string candidate) => RequiredEncoders(candidate).All(available.Contains);
        if (kind == Auto)
        {
            foreach (string candidate in AutoOrder)
                if (Usable(candidate)) return (candidate, null);
            return (Software, "auto 未在当前 ffmpeg 中探测到可用的硬件编码器（已检查 " +
                string.Join("、", AutoOrder) + "），回退软件编码。");
        }
        if (Usable(kind)) return (kind, null);
        string[] missing = [.. RequiredEncoders(kind).Where(name => !available.Contains(name))];
        return (Software, $"请求的 {kind} 不可用：当前 ffmpeg 缺少 {string.Join("、", missing)}，回退软件编码。");
    }

    /// <summary>
    /// 汇总 bake.json 顶层的 playback_encoder 段：请求档位、实际档位、回退理由、编码总秒数与播放版总字节数。
    /// </summary>
    public static JsonObject Summarize(string? requested, IEnumerable<JsonObject> groups)
    {
        var encoded = groups.Select(group => (Group: group, Encode: group["playback_encode"] as JsonObject))
            .Where(pair => pair.Encode is not null).ToArray();
        string[] used = [.. encoded.Select(pair => pair.Encode!["encoder_used"]?.GetValue<string>())
            .OfType<string>().Distinct(StringComparer.Ordinal)];
        return new JsonObject
        {
            ["requested"] = Normalize(requested),
            // 同一次生成里多个视频组理论上档位一致；真出现不一致时标成 mixed，不掩盖。
            ["used"] = used.Length == 1 ? used[0] : used.Length == 0 ? null : "mixed",
            ["fallback_reason"] = encoded.Select(pair => pair.Encode!["encoder_fallback_reason"]?.GetValue<string>())
                .OfType<string>().FirstOrDefault(),
            ["encode_seconds_total"] = encoded.Any(pair => pair.Encode!["encode_seconds"] is null)
                ? null : JsonValue.Create(Math.Round(encoded.Sum(pair => pair.Encode!["encode_seconds"]!.GetValue<double>()), 3)),
            ["video_bytes_total"] = encoded.Sum(pair => pair.Group["video_bytes"]?.GetValue<long>() ?? 0),
        };
    }
}
