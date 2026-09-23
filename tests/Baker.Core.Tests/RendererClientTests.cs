using System.Text.Json.Nodes;
using Baker.Core;
using Xunit;

// C2.4a：渲染器能力握手（解析 + 每个渲染器文件只握一次）、唯一进程封装 FfmpegTool 的失败/超时路径、
// 唯一探流核对 VerifyEncoded。假工具都是 .cmd 批处理，不需要真渲染器与 ffmpeg。

[Trait("Layer", "L0")]
public class RendererCapabilitiesTests
{
    [Fact]
    public void ParsesTextLineAndKeepsSparseFirstRule()
    {
        var caps = RendererCapabilities.Parse("wpe-render 0.1-dev upstream=b866 source=7ae9 features=sparse-readback-v1,gpu-samples-v1,gpu-encode-v1\r\n");
        Assert.Equal(["sparse-readback-v1", "gpu-samples-v1", "gpu-encode-v1"], caps.Features);
        Assert.True(caps.SparseReadback);
        Assert.True(caps.Has("gpu-encode-v1"));
        Assert.False(caps.Has("gpu-encode"));
        // 稀疏读回只认第一位：排在后面的旧渲染器不能被当成支持稀疏读回。
        Assert.False(RendererCapabilities.Parse("wpe-render x features=gpu-samples-v1,sparse-readback-v1").SparseReadback);
        Assert.Empty(RendererCapabilities.Parse("wpe-render 0.0 without features").Features);
    }

    [Fact]
    public void ParsesFutureJsonForm()
    {
        var caps = RendererCapabilities.Parse("""{"version":"0.2","job_schemas":[1,2],"features":["sparse-readback-v1","gpu-encode-v1"]}""");
        Assert.True(caps.SparseReadback);
        Assert.True(caps.Has("gpu-encode-v1"));
        Assert.Throws<InvalidDataException>(() => RendererCapabilities.Parse("""{"version":"0.2"}"""));
    }
}

[Trait("Layer", "L1")]
public class RendererClientTests
{
    private static string Cmd(string dir, string name, string body)
    {
        string path = Path.Combine(dir, name);
        File.WriteAllText(path, "@echo off\r\n" + body.ReplaceLineEndings("\r\n") + "\r\n");
        return path;
    }

    private static NativeTools Tools(string dir, string? renderer = null, string? ffmpeg = null, string? ffprobe = null) =>
        new(renderer ?? Path.Combine(dir, "none-render.cmd"), ffmpeg ?? Path.Combine(dir, "none-ffmpeg.cmd"),
            ffprobe ?? Path.Combine(dir, "none-ffprobe.cmd"), []);

    [Fact]
    public async Task HandshakeRunsOncePerRendererFile() => await TestTemp.Run(async dir =>
    {
        string counter = Path.Combine(dir, "count.txt");
        string renderer = Cmd(dir, "render.cmd", $"echo x>>\"{counter}\"\r\necho wpe-render t features=sparse-readback-v1,gpu-encode-v1");
        var client = new RendererClient(new FfmpegTool(Tools(dir, renderer)));
        var first = await client.CapabilitiesAsync(Path.Combine(dir, "a.log"), CancellationToken.None);
        var second = await new RendererClient(new FfmpegTool(Tools(dir, renderer))).CapabilitiesAsync(Path.Combine(dir, "b.log"), CancellationToken.None);
        Assert.Same(first, second);
        Assert.Single(File.ReadAllLines(counter));
        Assert.True(File.Exists(Path.Combine(dir, "a.log")));
        Assert.False(File.Exists(Path.Combine(dir, "b.log")));
        // 换了渲染器文件（长度变了）就重新握手。
        Cmd(dir, "render.cmd", $"echo x>>\"{counter}\"\r\necho wpe-render t features=gpu-encode-v1,gpu-capture-v1");
        var third = await client.CapabilitiesAsync(Path.Combine(dir, "c.log"), CancellationToken.None);
        Assert.Equal(2, File.ReadAllLines(counter).Length);
        Assert.True(third.Has("gpu-capture-v1"));
        Assert.False(third.SparseReadback);
    });

    [Fact]
    public async Task FailedHandshakeIsNotCached() => await TestTemp.Run(async dir =>
    {
        string counter = Path.Combine(dir, "count.txt");
        string renderer = Cmd(dir, "render.cmd", $"echo x>>\"{counter}\"\r\nexit /b 2");
        var client = new RendererClient(new FfmpegTool(Tools(dir, renderer)));
        await Assert.ThrowsAsync<IOException>(() => client.CapabilitiesAsync(Path.Combine(dir, "a.log"), CancellationToken.None));
        await Assert.ThrowsAsync<IOException>(() => client.CapabilitiesAsync(Path.Combine(dir, "b.log"), CancellationToken.None));
        Assert.Equal(2, File.ReadAllLines(counter).Length);
    });

    [Fact]
    public async Task ProcessFailureAndTimeoutPaths() => await TestTemp.Run(async dir =>
    {
        string failing = Cmd(dir, "fail.cmd", "echo out\r\necho boom 1>&2\r\nexit /b 3");
        string slow = Cmd(dir, "slow.cmd", "\"%SystemRoot%\\System32\\ping.exe\" -n 30 127.0.0.1 >nul");
        var tool = new FfmpegTool(Tools(dir, ffmpeg: Cmd(dir, "bytes.cmd", "echo ABCD")));
        await Assert.ThrowsAsync<IOException>(() => tool.RunTextAsync(failing, [], Path.Combine(dir, "fail.log"), CancellationToken.None));
        Assert.Contains("boom", File.ReadAllText(Path.Combine(dir, "fail.log")));
        await Assert.ThrowsAsync<InvalidDataException>(() => tool.RunCapturedAsync(failing, [], CancellationToken.None));
        Assert.Equal(3, await tool.RunToFilesAsync(failing, [], Path.Combine(dir, "o.txt"), Path.Combine(dir, "e.txt"), CancellationToken.None));
        Assert.Equal("out", File.ReadAllText(Path.Combine(dir, "o.txt")).Trim());
        // 超时就是调用方给的 token：到点杀进程树并以取消结束，不等 30 秒的 ping 自己结束。
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var started = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tool.RunToFilesAsync(slow, [], Path.Combine(dir, "so.txt"), Path.Combine(dir, "se.txt"), timeout.Token));
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(15), started.Elapsed.ToString());
        // 定长字节：echo ABCD 输出 6 字节（含回车换行），多了少了都拒绝。
        Assert.Equal("ABCD\r\n"u8.ToArray(), await tool.RunBytesAsync([], 6, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => tool.RunBytesAsync([], 4, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => tool.RunBytesAsync([], 8, CancellationToken.None));
    });

    private static string FakeProbe(string dir, string stream, string counted) => Cmd(dir, "ffprobe.cmd",
        "echo %* | \"%SystemRoot%\\System32\\findstr.exe\" /c:\"-count_frames\" >nul\r\n" +
        "if not errorlevel 1 (\r\n  echo {\"streams\":[{\"nb_read_frames\":\"" + counted + "\"}]}\r\n  exit /b 0\r\n)\r\n" +
        "echo {\"streams\":[" + stream + "]}");

    [Fact]
    public async Task VerifyEncodedTrustsHeaderThenFallsBackToDecode() => await TestTemp.Run(async dir =>
    {
        string video = Path.Combine(dir, "v.mp4");
        File.WriteAllBytes(video, [0]);
        const string header = "{\"width\":64,\"height\":48,\"avg_frame_rate\":\"60000/1001\",\"nb_frames\":\"10\",\"time_base\":\"1/60000\",\"duration_ts\":10010}";
        var tool = new FfmpegTool(Tools(dir, ffprobe: FakeProbe(dir, header, "12")));
        EncodedStream ok = await VerifyEncoded.ProbeAsync(tool, video, "stream=width", 10, Path.Combine(dir, "p.log"), CancellationToken.None);
        Assert.Equal(("container_header", 10UL, (string?)null), (ok.CountSource, ok.Frames, ok.FallbackReason));
        Assert.True(ok.Has(64, 48, 10));
        Assert.False(ok.Has(64, 47, 10));
        Assert.False(ok.Has(64, 48, 11));
        Assert.True(ok.RateIs(60000, 1001));
        Assert.False(ok.RateIs(60, 1));
        Assert.True(ok.DurationIs(10, 60000, 1001));
        Assert.False(ok.DurationIs(11, 60000, 1001));
        Assert.False(ok.DurationIs(10, 30000, 1001));
        Assert.Equal(64, ok.Probe["streams"]![0]!["width"]!.GetValue<int>());
        // 容器头帧数与期望不符：全解码计数，并写明回退原因。
        EncodedStream mismatch = await VerifyEncoded.ProbeAsync(tool, video, "stream=width", 11, null, CancellationToken.None, threads: false);
        Assert.Equal(("full_decode", 12UL), (mismatch.CountSource, mismatch.Frames));
        var missing = await VerifyEncoded.FrameCountAsync(tool, video, new JsonObject(), 10, CancellationToken.None);
        Assert.Equal(("full_decode", 12UL), (missing.Source, missing.Count));
    });
}
