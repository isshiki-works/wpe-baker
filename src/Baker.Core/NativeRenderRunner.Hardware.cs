using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace Baker.Core;

public sealed partial class NativeRenderRunner
{
    /// <summary>
    /// Checks this bitstream on the adapters WPE plays on: those driving a display; with no display attached, the bake's
    /// render device; failing that, every hardware adapter. Does not certify sustained FPS or WPE playback.
    /// </summary>
    public async Task<JsonObject> ProbeHardwareDecodeAsync(string videoFile, string outputNewDirectory,
        ulong frames = 5, CancellationToken cancellationToken = default, string? renderDeviceUuid = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("D3D11VA probing requires Windows.");
        if (frames == 0 || frames > long.MaxValue) throw new ArgumentOutOfRangeException(nameof(frames));
        videoFile = Path.GetFullPath(videoFile);
        if (!File.Exists(videoFile)) throw new FileNotFoundException("Video is missing.", videoFile);
        string output = Path.GetFullPath(outputNewDirectory);
        ProjectSource.EnsureNoReparsePoints(output);
        if (File.Exists(output) || Directory.Exists(output)) throw new IOException("Hardware probe output must be a new directory.");
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(output);
        string reportPath = Path.Combine(output, "hardware-decode.json");
        var adapters = new JsonArray();
        var skippedSoftware = new JsonArray();
        var skippedNotPlayback = new JsonArray();
        var report = new JsonObject {
            ["schema_version"] = 1, ["status"] = "running", ["video_path"] = videoFile,
            ["ffmpeg_path"] = Path.GetFullPath(tools.Ffmpeg), ["ffprobe_path"] = Path.GetFullPath(tools.Ffprobe),
            ["frames_requested"] = frames, ["adapter_timeout_seconds"] = 30,
            ["scope"] = "Actual bitstream, short D3D11VA decode with mandatory hardware frames and hwdownload, on this baking machine's playback adapters only (see playback_adapter_basis); no sustained FPS, full-file integrity, playback-machine claim or official WPE playback claim.",
            ["all_adapters_passed"] = false, ["adapters"] = adapters,
            // 这次实测只代表烘焙机自己的显卡；verified_on / target_hints / conclusion 由 ApplyTargetGuidance 在收尾时填。
            ["target_caveat"] = HardwareDecodeDimensions.BakingMachineOnlyCaveat,
            ["verified_on"] = new JsonArray(), ["target_hints"] = new JsonArray(), ["conclusion"] = null,
            ["skipped_software_adapters"] = skippedSoftware, ["skipped_non_playback_adapters"] = skippedNotPlayback, ["report_path"] = reportPath
        };
        await WriteJsonAsync(reportPath, report, cancellationToken);
        try
        {
            foreach (var adapter in EnumerateDecodeAdapters(skippedSoftware)) adapters.Add(adapter);
            try
            {
                var devices = VulkanDevices.Enumerate();
                foreach (var adapter in adapters.OfType<JsonObject>())
                {
                    string luid = adapter["windows_luid"]!.GetValue<string>();
                    var matches = devices.Where(d => string.Equals(d.WindowsLuid, luid, StringComparison.OrdinalIgnoreCase)).ToArray();
                    // A vendor/device ID cannot distinguish two installed cards of the same model.
                    if (matches.Length == 1)
                    {
                        adapter["device_uuid"] = matches[0].DeviceUuid;
                        adapter["vulkan_match"] = "windows_luid";
                    }
                    else adapter["vulkan_match"] = matches.Length == 0 ? "unmatched" : "ambiguous_luid";
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                report["vulkan_enumeration_error_type"] = error.GetType().Name;
                report["vulkan_enumeration_error"] = error.Message;
            }
            // WPE 在驱动显示器的那块卡上解码播放：只验这些卡，没接显示器的核显解不了不影响播放。
            // 一块都没接显示器（显示器休眠断开、远程会话、Session 0）时退到本次烘焙的渲染卡；也认不出就照旧验全部。
            JsonObject[] all = [.. adapters.OfType<JsonObject>()], playback = [.. all.Where(a => a["drives_display"]!.GetValue<bool>())];
            report["playback_adapter_basis"] = playback.Length > 0 ? "drives_display" : "all_adapters";
            if (playback.Length == 0 && renderDeviceUuid is not null &&
                all.Where(a => string.Equals(a["device_uuid"]?.GetValue<string>(), renderDeviceUuid, StringComparison.OrdinalIgnoreCase)).ToArray() is [var render])
            {
                playback = [render];
                report["playback_adapter_basis"] = "render_device";
            }
            if (playback.Length > 0)
                foreach (var idle in all.Except(playback))
                {
                    idle["status"] = "skipped_not_playback";
                    adapters.Remove(idle);
                    skippedNotPlayback.Add(idle);
                }

            string probeLog = Path.Combine(output, "ffprobe.stderr.log");
            report["ffprobe_stderr_log_path"] = probeLog;
            using var inspectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            inspectTimeout.CancelAfter(TimeSpan.FromSeconds(30));
            string metadata = await ff.RunTextAsync(tools.Ffprobe,
                ["-v", "error", "-select_streams", "v:0", "-show_streams", "-of", "json", videoFile], probeLog, inspectTimeout.Token);
            var streams = JsonNode.Parse(metadata)?["streams"]?.AsArray();
            if (streams is null || streams.Count != 1) throw new InvalidDataException("Expected a first video stream.");
            report["video_stream"] = streams[0]!.DeepClone();
            string? pixelFormat = streams[0]?["pix_fmt"]?.GetValue<string>();
            string? downloadFormat = pixelFormat switch {
                "yuv420p" or "yuvj420p" or "nv12" => "nv12",
                "yuv420p10le" or "p010le" => "p010le",
                _ => null
            };
            report["download_pixel_format"] = downloadFormat;
            report["production_8bit_path"] = downloadFormat == "nv12";

            foreach (var adapter in adapters.OfType<JsonObject>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (downloadFormat is null)
                {
                    adapter["status"] = "not_tested";
                    adapter["error_type"] = "UnsupportedProbePixelFormat";
                    adapter["error"] = $"This probe only covers 8-bit NV12 and 10-bit P010 4:2:0 hardware surfaces; input pixel format is {pixelFormat ?? "unknown"}.";
                    continue;
                }
                string index = adapter["adapter_index"]!.GetValue<uint>().ToString(CultureInfo.InvariantCulture);
                string stdoutPath = Path.Combine(output, $"adapter-{index}.stdout.log");
                string stderrPath = Path.Combine(output, $"adapter-{index}.stderr.log");
                string[] arguments = ["-hide_banner", "-nostdin", "-v", "verbose", "-xerror",
                    "-init_hw_device", $"d3d11va=target:{index}", "-hwaccel", "d3d11va", "-hwaccel_device", "target",
                    "-hwaccel_output_format", "d3d11", "-i", videoFile, "-map", "0:v:0", "-frames:v", frames.ToString(CultureInfo.InvariantCulture),
                    "-vf", $"hwdownload,format={downloadFormat}", "-an", "-sn", "-dn", "-c:v", "rawvideo",
                    "-progress", "pipe:1", "-nostats", "-f", "null", "-"];
                adapter["arguments"] = new JsonArray(arguments.Select(a => (JsonNode?)JsonValue.Create(a)).ToArray());
                adapter["stdout_log_path"] = stdoutPath;
                adapter["stderr_log_path"] = stderrPath;
                adapter["status"] = "running";
                await WriteJsonAsync(reportPath, report, cancellationToken);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                try
                {
                    int exitCode = await ff.RunToFilesAsync(tools.Ffmpeg, arguments, stdoutPath, stderrPath, timeout.Token);
                    adapter["exit_code"] = exitCode;
                    string progressText = await File.ReadAllTextAsync(stdoutPath, CancellationToken.None);
                    ulong? decoded = null;
                    string? progress = null;
                    foreach (string line in progressText.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (line.StartsWith("frame=", StringComparison.Ordinal))
                            decoded = ulong.TryParse(line.AsSpan(6).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out ulong count) ? count : null;
                        else if (line.StartsWith("progress=", StringComparison.Ordinal)) progress = line[9..];
                    }
                    adapter["frames_decoded"] = decoded;
                    adapter["progress"] = progress;
                    bool passed = exitCode == 0 && decoded == frames && progress == "end";
                    adapter["passed"] = passed;
                    adapter["status"] = passed ? "passed" : "failed";
                    if (!passed)
                    {
                        adapter["error_type"] = exitCode != 0 ? "FfmpegProcessFailure" : "IncompleteHardwareDecode";
                        adapter["error"] = exitCode != 0 ? $"FFmpeg exited {exitCode}; original diagnostic is in stderr." :
                            $"Expected final frame={frames} and progress=end; got frame={decoded?.ToString(CultureInfo.InvariantCulture) ?? "missing"}, progress={progress ?? "missing"}.";
                    }
                }
                catch (Exception error)
                {
                    adapter["status"] = cancellationToken.IsCancellationRequested ? "cancelled" : timeout.IsCancellationRequested ? "timed_out" : "failed";
                    adapter["error_type"] = error.GetType().Name;
                    adapter["error"] = error.Message;
                    if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException("Hardware decode probe cancelled.", error, cancellationToken);
                }
                finally
                {
                    if (File.Exists(stderrPath)) adapter["stderr"] = await File.ReadAllTextAsync(stderrPath, CancellationToken.None);
                    await WriteJsonAsync(reportPath, report, CancellationToken.None);
                }
            }
            report["status"] = adapters.Count == 0 ? "no_hardware_adapters" : downloadFormat is null ? "not_tested" : "completed";
            report["all_adapters_passed"] = adapters.Count > 0 && adapters.All(a => a?["passed"]?.GetValue<bool>() == true);
            ApplyTargetGuidance(report);
            await WriteJsonAsync(reportPath, report, cancellationToken);
            return report;
        }
        catch (Exception error)
        {
            report["status"] = cancellationToken.IsCancellationRequested ? "cancelled" : "failed";
            report["error_type"] = error.GetType().Name;
            report["error"] = error.Message;
            string probeLog = Path.Combine(output, "ffprobe.stderr.log");
            if (File.Exists(probeLog)) report["ffprobe_stderr"] = await File.ReadAllTextAsync(probeLog, CancellationToken.None);
            ApplyTargetGuidance(report);
            await WriteJsonAsync(reportPath, report, CancellationToken.None);
            throw;
        }
    }

    /// <summary>
    /// 把"这次只在烘焙机上验过"写进结果：参与验证的适配器清单（名称、厂商、核显还是独显）、
    /// 厂商无关的核显尺寸提示，以及一段中英结论。只读已有字段、只写文案，不改任何判定，也不抛异常。
    /// </summary>
    private static void ApplyTargetGuidance(JsonObject report)
    {
        JsonObject[] adapters = (report["adapters"] as JsonArray ?? []).OfType<JsonObject>().ToArray();
        static string Name(JsonObject adapter) => adapter["name"]?.GetValue<string>() ?? "unknown adapter";
        static bool Passed(JsonObject adapter) => adapter["passed"]?.GetValue<bool>() == true;
        static string Class(JsonObject adapter) =>
            adapter["adapter_class"]?.GetValue<string>() ?? HardwareDecodeDimensions.UnknownAdapterClass;

        report["verified_on"] = new JsonArray(adapters.Select(adapter => (JsonNode?)new JsonObject
        {
            ["name"] = Name(adapter),
            ["vendor"] = adapter["vendor"]?.DeepClone(),
            ["adapter_class"] = Class(adapter),
            ["integrated"] = Class(adapter) == HardwareDecodeDimensions.IntegratedAdapter,
            ["status"] = adapter["status"]?.DeepClone(),
            ["passed"] = Passed(adapter),
        }).ToArray());
        report["target_caveat"] = HardwareDecodeDimensions.BakingMachineOnlyCaveat;

        JsonObject[] passed = [.. adapters.Where(Passed)];
        var parts = new JsonArray();
        void Add(string key, object?[] chineseArgs, object?[] englishArgs) =>
            parts.Add(new Message(key, englishArgs, chineseArgs).Localized());

        if (adapters.Length == 0) Add("hardware_decode.no_adapters_on_baking_machine", [], []);
        else if (passed.Length == 0)
        {
            object?[] all = [MessageCatalog.NameList(adapters.Select(Name))];
            Add("hardware_decode.none_passed_on_baking_machine", all, all);
        }
        else
        {
            object?[] names = [MessageCatalog.NameList(passed.Select(Name))];
            Add("hardware_decode.verified_on_baking_machine", names, names);
        }
        // 烘焙机上一张核显都没有时提示更强：核显完全没被验证过，而播放机多半就是核显。
        if (adapters.Length > 0 && !adapters.Any(adapter => Class(adapter) == HardwareDecodeDimensions.IntegratedAdapter))
        {
            object?[] discrete = [MessageCatalog.NameList(adapters.Select(Name))];
            Add("hardware_decode.no_integrated_verified", discrete, discrete);
        }

        var hints = new JsonArray();
        JsonObject? stream = report["video_stream"] as JsonObject;
        uint width = Dimension(stream, "coded_width", "width"), height = Dimension(stream, "coded_height", "height");
        if (HardwareDecodeDimensions.HintFor(stream?["codec_name"]?.GetValue<string>(), width, height) is { } hint)
        {
            hints.Add(hint.ToJson());
            string extent = HardwareDecodeDimensions.Extent(width, height);
            Add("hardware_decode.beyond_integrated_ceiling",
                [hint.Ceiling.DisplayName, extent, hint.ExceededText(MessageCatalog.Chinese), hint.CeilingText(), hint.Ceiling.BasisZh],
                [hint.Ceiling.DisplayName, extent, hint.ExceededText(MessageCatalog.English), hint.CeilingText(), hint.Ceiling.BasisEn]);
        }
        report["target_hints"] = hints;
        static string Text(JsonNode? part, string language) => part?[language]?.GetValue<string>() ?? "";
        report["conclusion"] = new JsonObject
        {
            // 中文句子自带句号，直接相接；英文按句子间空格相接。
            ["zh"] = string.Concat(parts.Select(part => Text(part, MessageCatalog.Chinese))),
            ["en"] = string.Join(" ", parts.Select(part => Text(part, MessageCatalog.English))),
            ["parts"] = parts,
        };
    }

    /// <summary>从 ffprobe 的流信息里取第一个有效的正整数尺寸；读不到按 0 处理（不产生提示）。</summary>
    private static uint Dimension(JsonObject? stream, params string[] keys)
    {
        foreach (string key in keys)
            if (stream?[key] is JsonValue value && value.TryGetValue(out int number) && number > 0) return (uint)number;
        return 0;
    }

    private static List<JsonObject> EnumerateDecodeAdapters(JsonArray skippedSoftware)
    {
        Guid iid = new("770aae78-f26f-4dba-a829-253c83d1b387");
        Marshal.ThrowExceptionForHR(CreateDXGIFactory1(in iid, out nint factory));
        if (factory == 0) throw new InvalidOperationException("CreateDXGIFactory1 returned a null factory.");
        try
        {
            var vtable = Marshal.PtrToStructure<DxgiFactory1Vtable>(Marshal.ReadIntPtr(factory));
            var enumerate = Marshal.GetDelegateForFunctionPointer<EnumAdapters1Delegate>(vtable.EnumAdapters1);
            var results = new List<JsonObject>();
            for (uint index = 0; ; index++)
            {
                int result = enumerate(factory, index, out nint adapter);
                if (result == unchecked((int)0x887A0002)) break; // DXGI_ERROR_NOT_FOUND
                Marshal.ThrowExceptionForHR(result);
                if (adapter == 0) throw new InvalidOperationException("EnumAdapters1 returned a null adapter.");
                try
                {
                    var table = Marshal.PtrToStructure<DxgiAdapter1Vtable>(Marshal.ReadIntPtr(adapter));
                    var getDescription = Marshal.GetDelegateForFunctionPointer<GetDesc1Delegate>(table.GetDesc1);
                    Marshal.ThrowExceptionForHR(getDescription(adapter, out var description));
                    int outputResult = Marshal.GetDelegateForFunctionPointer<EnumOutputsDelegate>(table.EnumOutputs)(adapter, 0, out nint output);
                    if (output != 0) Marshal.Release(output);
                    byte[] luid = [.. BitConverter.GetBytes(description.AdapterLuid.LowPart), .. BitConverter.GetBytes(description.AdapterLuid.HighPart)];
                    // 核显还是独显要写进结果：用户在播放机（多半是核显）上播，烘焙机上验过的未必是同一类硬件。
                    ulong dedicated = description.DedicatedVideoMemory, shared = description.SharedSystemMemory;
                    var identity = new JsonObject {
                        ["adapter_index"] = index, ["name"] = description.Description,
                        ["windows_luid"] = Convert.ToHexStringLower(luid), ["vendor_id"] = description.VendorId,
                        ["vendor"] = HardwareDecodeDimensions.VendorName(description.VendorId),
                        ["device_id"] = description.DeviceId, ["dxgi_flags"] = description.Flags,
                        ["dedicated_video_memory_bytes"] = dedicated, ["shared_system_memory_bytes"] = shared,
                        ["adapter_class"] = HardwareDecodeDimensions.ClassifyAdapter(description.VendorId, dedicated, shared),
                        ["device_uuid"] = null, ["vulkan_match"] = "not_available",
                        ["drives_display"] = outputResult >= 0,
                        ["status"] = "pending", ["passed"] = false
                    };
                    // Microsoft's DXGI overview defines 1414:008c as Basic Render Driver.
                    // It can appear without SOFTWARE in GetDesc1; retain the observed flags.
                    // https://learn.microsoft.com/windows/win32/direct3ddxgi/d3d10-graphics-programming-guide-dxgi
                    bool softwareFlag = (description.Flags & 2) != 0;
                    if (softwareFlag || (description.VendorId == 0x1414 && description.DeviceId == 0x008c))
                    {
                        identity["status"] = "skipped_software";
                        identity["skip_reason"] = softwareFlag ? "DXGI_ADAPTER_FLAG_SOFTWARE" : "Microsoft Basic Render Driver software adapter ID";
                        skippedSoftware.Add(identity);
                    }
                    else results.Add(identity);
                }
                finally { Marshal.Release(adapter); }
            }
            return results;
        }
        finally { Marshal.Release(factory); }
    }

    [DllImport("dxgi.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int CreateDXGIFactory1(in Guid iid, out nint factory);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int EnumAdapters1Delegate(nint factory, uint index, out nint adapter);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int GetDesc1Delegate(nint adapter, out DxgiAdapterDescription1 description);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int EnumOutputsDelegate(nint adapter, uint index, out nint output);

    // Native field order from the bundled llvm-mingw-22/include/dxgi.h:
    // IDXGIFactory1Vtbl, IDXGIAdapter1Vtbl and DXGI_ADAPTER_DESC1. SIZE_T is nuint.
    [StructLayout(LayoutKind.Sequential)]
    private struct DxgiFactory1Vtable
    {
        public nint QueryInterface, AddRef, Release, SetPrivateData, SetPrivateDataInterface, GetPrivateData, GetParent;
        public nint EnumAdapters, MakeWindowAssociation, GetWindowAssociation, CreateSwapChain, CreateSoftwareAdapter;
        public nint EnumAdapters1, IsCurrent;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct DxgiAdapter1Vtable
    {
        public nint QueryInterface, AddRef, Release, SetPrivateData, SetPrivateDataInterface, GetPrivateData, GetParent;
        public nint EnumOutputs, GetDesc, CheckInterfaceSupport, GetDesc1;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct DxgiLuid { public uint LowPart; public int HighPart; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DxgiAdapterDescription1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId, DeviceId, SubSysId, Revision;
        public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public DxgiLuid AdapterLuid;
        public uint Flags;
    }
}
