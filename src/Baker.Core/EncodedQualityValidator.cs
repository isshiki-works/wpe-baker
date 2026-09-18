using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>读取已编码视频的 ffmpeg/ffprobe 底层工具：按帧号精确取帧、探流、跑出原始字节。只取数，不做任何裁决。</summary>
public static partial class EncodedQualityValidator
{
    /// <summary>只读容器头的视频流属性：尺寸、帧率、容器声明的帧数（mp4 取 stsz 的样本数）。不解码。</summary>
    internal static async Task<JsonObject> ProbeStreamAsync(string file, NativeTools tools, CancellationToken token)
    {
        if (!File.Exists(file)) throw new FileNotFoundException("Encoded video is missing.", file);
        if (!File.Exists(tools.Ffprobe)) throw new FileNotFoundException("Required native tool is missing.", tools.Ffprobe);
        string text = await RunAsync(tools.Ffprobe, tools, ["-v", "error",
            "-select_streams", "v:0", "-show_entries", "stream=width,height,avg_frame_rate,r_frame_rate,nb_frames",
            "-of", "json", file], token);
        return JsonNode.Parse(text)?["streams"]?.AsArray().SingleOrDefault()?.AsObject() ?? throw new InvalidDataException("ffprobe returned no unique video stream.");
    }

    /// <summary>容器头声明的帧数；没有或不是正数时返回 false，由调用方决定要不要回退全解码。</summary>
    internal static bool TryContainerFrameCount(JsonObject stream, out ulong count)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return ulong.TryParse(stream["nb_frames"]?.GetValue<string>(), NumberStyles.None, CultureInfo.InvariantCulture, out count) && count > 0;
    }

    /// <summary>ffprobe -count_frames 全解码计帧。只在容器头给不出帧数、或它与期望帧数不符时才跑。</summary>
    internal static async Task<ulong> CountFramesAsync(string file, NativeTools tools, CancellationToken token)
    {
        if (!File.Exists(file)) throw new FileNotFoundException("Encoded video is missing.", file);
        if (!File.Exists(tools.Ffprobe)) throw new FileNotFoundException("Required native tool is missing.", tools.Ffprobe);
        string text = await RunAsync(tools.Ffprobe, tools, ["-v", "error", "-threads", "4", "-count_frames",
            "-select_streams", "v:0", "-show_entries", "stream=nb_read_frames", "-of", "json", file], token);
        JsonObject stream = JsonNode.Parse(text)?["streams"]?.AsArray().SingleOrDefault()?.AsObject()
            ?? throw new InvalidDataException("ffprobe returned no unique video stream.");
        return ulong.TryParse(stream["nb_read_frames"]?.GetValue<string>(), NumberStyles.None, CultureInfo.InvariantCulture, out ulong count) && count > 0
            ? count : throw new InvalidDataException("ffprobe did not return a positive counted video frame total.");
    }
    internal static async IAsyncEnumerable<(ulong Index, byte[] Frame)> DecodeSelectedRgb24Async(string file,
        NativeTools tools, int width, int height, IReadOnlyList<ulong> indices, string? filterAfterSelection = null,
        string? stderrLogPath = null, [EnumeratorCancellation] CancellationToken token = default)
    {
        if (!File.Exists(file)) throw new FileNotFoundException("Encoded video is missing.", file);
        if (!File.Exists(tools.Ffmpeg)) throw new FileNotFoundException("Required native tool is missing.", tools.Ffmpeg);
        if (width <= 0 || height <= 0 || indices.Count == 0) throw new ArgumentException("Selected RGB decode requires positive dimensions and at least one frame index.");
        for (int i = 1; i < indices.Count; ++i)
            if (indices[i] <= indices[i - 1]) throw new ArgumentException("Selected frame indices must be strictly increasing.");
        var runs = ConsecutiveRuns(indices);
        if (runs.Count > 1 && (await TryReadPacketCfrAsync(file, tools, token)) is { } fps)
        {
            for (int run = 0; run < runs.Count; ++run)
            {
                (ulong first, int count) = runs[run];
                ulong wholeSeconds = checked((ulong)((UInt128)first * fps.Denominator / fps.Numerator));
                ulong anchor = wholeSeconds == 0 ? 0 : wholeSeconds - 1;
                ulong firstAtAnchor = checked((ulong)(((UInt128)anchor * fps.Numerator + fps.Denominator - 1) / fps.Denominator));
                ulong[] runIndices = Enumerable.Range(0, count).Select(offset => first + (ulong)offset).ToArray();
                await foreach (var selected in DecodeSelectedRunRgb24Async(file, tools, width, height, runIndices, firstAtAnchor,
                    anchor == 0 ? null : anchor, filterAfterSelection, stderrLogPath is null ? null : stderrLogPath + ".seek-" + run, token))
                    yield return selected;
            }
            yield break;
        }
        await foreach (var selected in DecodeSelectedRunRgb24Async(file, tools, width, height, indices, 0, null,
            filterAfterSelection, stderrLogPath, token)) yield return selected;
    }

    private static async IAsyncEnumerable<(ulong Index, byte[] Frame)> DecodeSelectedRunRgb24Async(string file,
        NativeTools tools, int width, int height, IReadOnlyList<ulong> indices, ulong firstInputIndex, ulong? seekSeconds,
        string? filterAfterSelection, string? stderrLogPath, [EnumeratorCancellation] CancellationToken token)
    {
        string selection = SelectionFilter(indices.Select(index => checked(index - firstInputIndex)).ToArray());
        string filter = string.IsNullOrWhiteSpace(filterAfterSelection) ? selection : selection + "," + filterAfterSelection;
        int bytes = checked(width * height * 3);
        var arguments = new List<string> { "-hide_banner", "-nostdin", "-v", "error", "-threads", "2" };
        if (seekSeconds is ulong seek)
        {
            arguments.Add("-ss"); arguments.Add(seek.ToString(CultureInfo.InvariantCulture)); arguments.Add("-accurate_seek");
        }
        arguments.AddRange(["-i", file, "-map", "0:v:0", "-vf", filter, "-fps_mode", "passthrough",
            "-frames:v", indices.Count.ToString(CultureInfo.InvariantCulture), "-an", "-sn", "-dn", "-f", "rawvideo", "-pix_fmt", "rgb24", "pipe:1"]);
        var info = StartInfo(tools.Ffmpeg, tools, arguments);
        using var process = new Process { StartInfo = info };
        if (!process.Start()) throw new IOException("Could not start ffmpeg selected-frame decode.");
        using var registration = token.Register(() => Stop(process));
        Task<string> stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        bool missing = false, extra = false, stderrSaved = false;
        try
        {
            foreach (ulong index in indices)
            {
                byte[]? frame = await ReadFrameAsync(process.StandardOutput.BaseStream, bytes, token);
                if (frame is null) { missing = true; break; }
                yield return (index, frame);
            }
            if (!missing) extra = await process.StandardOutput.BaseStream.ReadAsync(new byte[1], token) != 0;
            await Task.WhenAll(process.WaitForExitAsync(token), stderr);
            if (stderrLogPath is not null)
            {
                await File.WriteAllTextAsync(stderrLogPath, stderr.Result, CancellationToken.None);
                stderrSaved = true;
            }
            if (process.ExitCode != 0) throw new InvalidDataException($"ffmpeg selected-frame decode exited {process.ExitCode}: {Trim(stderr.Result)}");
            if (missing) throw new InvalidDataException("ffmpeg did not decode every requested exact frame index.");
            if (extra) throw new InvalidDataException("ffmpeg emitted more selected frames than requested.");
        }
        finally
        {
            Stop(process);
            if (stderrLogPath is not null && !stderrSaved)
            {
                string raw = await stderr;
                await File.WriteAllTextAsync(stderrLogPath, raw, CancellationToken.None);
            }
        }
    }

    /// <summary>
    /// 按帧号精确取第 <paramref name="first"/> 帧起的 <paramref name="count"/> 帧（<see cref="ExactFrameRange"/>），解成 RGB24。
    /// <paramref name="filterAfter"/> 接在取帧窗口之后（例如裁剪），输出尺寸按它之后的尺寸给。帧数不对直接抛异常。
    /// </summary>
    internal static async Task<List<byte[]>> DecodeExactFramesAsync(string file, NativeTools tools, ulong first, ulong count,
        int width, int height, uint numerator, uint denominator, string? filterAfter, CancellationToken token)
    {
        if (count is 0 or > 64 || width <= 0 || height <= 0) throw new ArgumentException("Exact frame decode needs 1..64 frames and positive dimensions.");
        ExactFrameRange.FfmpegInput input = ExactFrameRange.Build(file, first, count, numerator, denominator);
        string filter = string.IsNullOrWhiteSpace(filterAfter) ? input.Filter : input.Filter + "," + filterAfter;
        string[] arguments = ["-hide_banner", "-nostdin", "-v", "error", "-threads", "2", .. input.Arguments, "-map", "0:v:0",
            "-vf", filter, "-fps_mode", "passthrough", "-frames:v", count.ToString(CultureInfo.InvariantCulture),
            "-an", "-sn", "-dn", "-f", "rawvideo", "-pix_fmt", "rgb24", "pipe:1"];
        int frameBytes = checked(width * height * 3);
        byte[] data = await RunFfmpegBytesAsync(tools, arguments, checked(frameBytes * (long)count), token);
        var frames = new List<byte[]>((int)count);
        for (int index = 0; index < (int)count; ++index) frames.Add(data.AsSpan(index * frameBytes, frameBytes).ToArray());
        return frames;
    }

    /// <summary>跑一次输出原始字节到 stdout 的 ffmpeg；字节数必须正好等于 <paramref name="expectedBytes"/>。</summary>
    internal static async Task<byte[]> RunFfmpegBytesAsync(NativeTools tools, IEnumerable<string> arguments, long expectedBytes,
        CancellationToken token)
    {
        if (!File.Exists(tools.Ffmpeg)) throw new FileNotFoundException("Required native tool is missing.", tools.Ffmpeg);
        if (expectedBytes <= 0 || expectedBytes > Array.MaxLength) throw new ArgumentException("Raw ffmpeg output must fit one managed buffer.");
        using var process = new Process { StartInfo = StartInfo(tools.Ffmpeg, tools, arguments) };
        if (!process.Start()) throw new IOException("Could not start ffmpeg.");
        using var registration = token.Register(() => Stop(process));
        Task<string> stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            byte[] data = new byte[expectedBytes];
            int read = 0;
            while (read < data.Length)
            {
                int count = await process.StandardOutput.BaseStream.ReadAsync(data.AsMemory(read), token);
                if (count == 0) break;
                read += count;
            }
            bool extra = read == data.Length && await process.StandardOutput.BaseStream.ReadAsync(new byte[1], token) != 0;
            await Task.WhenAll(process.WaitForExitAsync(token), stderr);
            if (process.ExitCode != 0) throw new InvalidDataException($"ffmpeg exited {process.ExitCode}: {Trim(stderr.Result)}");
            if (read != data.Length || extra)
                throw new InvalidDataException($"ffmpeg produced {(extra ? "more than" : read.ToString(CultureInfo.InvariantCulture))} bytes; expected exactly {expectedBytes}.");
            return data;
        }
        finally { Stop(process); }
    }

    private static List<(ulong First, int Count)> ConsecutiveRuns(IReadOnlyList<ulong> indices)
    {
        var runs = new List<(ulong, int)>();
        for (int i = 0; i < indices.Count;)
        {
            ulong first = indices[i++], last = first;
            while (i < indices.Count && last != ulong.MaxValue && indices[i] == last + 1) last = indices[i++];
            runs.Add((first, checked((int)(last - first + 1))));
        }
        return runs;
    }

    private static async Task<(ulong Numerator, ulong Denominator)?> TryReadPacketCfrAsync(string file,
        NativeTools tools, CancellationToken token)
    {
        string text = await RunAsync(tools.Ffprobe, tools, ["-v", "error", "-select_streams", "v:0", "-show_entries",
            "stream=codec_name,time_base,field_order:packet=pts", "-show_packets", "-of", "json", file], token);
        JsonObject? root = JsonNode.Parse(text)?.AsObject();
        JsonObject? stream = root?["streams"]?.AsArray().SingleOrDefault()?.AsObject();
        if (stream?["codec_name"]?.GetValue<string>() is not ("h264" or "hevc") || stream?["field_order"]?.GetValue<string>() != "progressive" ||
            !TryRate(stream?["time_base"]?.GetValue<string>(), out ulong timeBaseNumerator, out ulong timeBaseDenominator)) return null;
        var pts = new List<long>();
        foreach (JsonObject packet in root?["packets"]?.AsArray().OfType<JsonObject>() ?? [])
            if (TryLong(packet["pts"], out long value)) pts.Add(value);
            else return null;
        if (pts.Count < 2) return null;
        pts.Sort();
        if (pts[0] != 0 || pts[1] <= 0) return null;
        long step = pts[1];
        for (int index = 2; index < pts.Count; ++index)
            if (pts[index] <= pts[index - 1] || pts[index] - pts[index - 1] != step) return null;
        UInt128 denominator = (UInt128)timeBaseNumerator * (ulong)step;
        if (denominator == 0 || denominator > ulong.MaxValue) return null;
        return (timeBaseDenominator, (ulong)denominator);

        static bool TryLong(JsonNode? node, out long value)
        {
            value = 0;
            if (node is not JsonValue number) return false;
            if (number.TryGetValue(out value)) return true;
            return number.TryGetValue<string>(out string? text) &&
                long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        static bool TryRate(string? text, out ulong numerator, out ulong denominator)
        {
            numerator = denominator = 0;
            string[] parts = text?.Split('/') ?? [];
            return parts.Length == 2 && ulong.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out numerator) &&
                ulong.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out denominator) && numerator != 0 && denominator != 0;
        }
    }

    private static string SelectionFilter(IReadOnlyList<ulong> indices)
    {
        var terms = new List<string>();
        for (int i = 0; i < indices.Count;)
        {
            ulong start = indices[i++], end = start;
            while (i < indices.Count && end != ulong.MaxValue && indices[i] == end + 1) end = indices[i++];
            terms.Add(start == end ? $"eq(n\\,{start})" : $"between(n\\,{start}\\,{end})");
        }
        return "select='" + string.Join('+', terms) + "'";
    }

    private static async Task<byte[]?> ReadFrameAsync(Stream input, int length, CancellationToken token)
    {
        byte[] frame = GC.AllocateUninitializedArray<byte>(length); int read = 0;
        while (read < frame.Length)
        {
            int count = await input.ReadAsync(frame.AsMemory(read), token);
            if (count == 0)
            {
                if (read == 0) return null;
                throw new InvalidDataException("ffmpeg selected RGB24 output ended in a partial frame.");
            }
            read += count;
        }
        return frame;
    }
    internal static async Task<string> RunAsync(string exe, NativeTools tools, string[] args, CancellationToken token) { var info = StartInfo(exe, tools, args); using var p = new Process { StartInfo = info }; if (!p.Start()) throw new IOException("Could not start ffprobe."); using var r = token.Register(() => Stop(p)); Task<string> o = p.StandardOutput.ReadToEndAsync(token), e = p.StandardError.ReadToEndAsync(token); try { await Task.WhenAll(p.WaitForExitAsync(token), o, e); if (p.ExitCode != 0) throw new InvalidDataException($"ffprobe failed: {Trim(e.Result)}"); return o.Result; } finally { Stop(p); } }
    private static ProcessStartInfo StartInfo(string exe, NativeTools tools, IEnumerable<string> args) { var i = new ProcessStartInfo(Path.GetFullPath(exe)) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true }; foreach (string a in args) i.ArgumentList.Add(a); i.Environment["PATH"] = string.Join(Path.PathSeparator, tools.RuntimeDirectories.Select(Path.GetFullPath)) + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"); return i; }
    private static void Stop(Process p) { try { if (!p.HasExited) p.Kill(true); } catch (InvalidOperationException) { } }
    private static string Trim(string s) => s.Length <= 2048 ? s.Trim() : s[..2048].Trim() + "…";
}
