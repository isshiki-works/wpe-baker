using System.Text.Json;
using System.Text.Json.Nodes;

namespace Baker.Core;

public sealed partial class NativeRenderRunner
{
    /// <summary>Retains raw RGBA for static caches and comparisons; supports odd local texture extents.</summary>
    public Task<JsonObject> RenderRawAsync(RenderRequest request, CancellationToken cancellationToken = default) =>
        RenderRawAsync(request, frameSink: null, cancellationToken);

    /// <summary>
    /// 同上；给了 <paramref name="frameSink"/> 时渲染器从 stdout 逐帧交出、不落 frames.rgba（manifest 不写 rgba_path），
    /// 其余回执核对不变。null 时与上面的重载逐字节相同。
    /// </summary>
    internal async Task<JsonObject> RenderRawAsync(RenderRequest request, IFrameSink? frameSink, CancellationToken cancellationToken)
    {
        if (request.SchemaVersion != 1) throw new InvalidDataException("Unsupported render request version.");
        if (request.Width == 0 || request.Height == 0 || request.Width > ushort.MaxValue || request.Height > ushort.MaxValue ||
            request.FpsNumerator == 0 || request.FpsDenominator == 0 || request.Frames == 0)
            throw new ArgumentException("Invalid raw render extent, frame count or rational FPS.");
        if ((ulong)request.Width * request.Height * 4 > 256ul * 1024 * 1024)
            throw new ArgumentException("Raw frame exceeds the 256 MiB readback budget.");
        if (request.OrthographicCaptureViewport is { } viewport &&
            (!double.IsFinite(viewport.CenterX) || !double.IsFinite(viewport.CenterY) ||
             !double.IsFinite(viewport.Width) || !double.IsFinite(viewport.Height) ||
             viewport.Width <= 0 || viewport.Height <= 0))
            throw new ArgumentException("Orthographic capture viewport requires finite coordinates and positive dimensions.");
        if (!Directory.Exists(request.Assets)) throw new DirectoryNotFoundException(request.Assets);
        if (!File.Exists(tools.Renderer)) throw new FileNotFoundException("Native renderer missing.", tools.Renderer);
        using var source = new ProjectSource(request.Source);
        if (source.Kind != "scene") throw new InvalidDataException("Raw rendering requires a scene project.");
        string output = Path.GetFullPath(request.OutputDirectory);
        ProjectSource.EnsureNoReparsePoints(output);
        if (Directory.Exists(output) || File.Exists(output)) throw new IOException("Raw render output must be new.");
        if (output.StartsWith(source.DirectoryPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Raw render output cannot be inside its source project.");
        string sourceHash = await source.SourceHashAsync(cancellationToken);
        ulong expected = checked((ulong)request.Width * request.Height * 4 * request.Frames);
        ulong pcmBytes = checked((ulong)(((UInt128)request.Frames * request.FpsDenominator * 384000 +
            request.FpsNumerator - 1) / request.FpsNumerator));
        // 交给接收器的帧不落盘，只需给渲染器可能写的 PCM 留余量。
        TemporaryCaptureFiles.RequireFreeSpace(output, frameSink is null ? checked(expected + pcmBytes) : pcmBytes);
        Directory.CreateDirectory(output);
        var manifest = new JsonObject { ["schema_version"] = 1, ["artifact_kind"] = "raw_frames", ["status"] = "running",
            ["source_sha256"] = sourceHash, ["source_digest_scope"] = ProjectSource.DigestScope, ["request"] = JsonSerializer.SerializeToNode(request, JsonOptions) };
        string manifestPath = Path.Combine(output, "manifest.json");
        await WriteJsonAsync(manifestPath, manifest, cancellationToken);
        try
        {
            if (request.CaptureTarget?.ForceVisibleOwner == true &&
                !(await client.CapabilitiesAsync(Path.Combine(output, "renderer-version.stderr.log"), cancellationToken))
                    .Has("capture-force-visible-owner-v1"))
                throw new InvalidDataException("Renderer does not support capturing a visibility-controlled effect owner.");
            string native = Path.Combine(output, "native");
            RenderJob job = RenderJob.From(request, source.SourcePath, native, rawStdout: frameSink is not null);
            string jobPath = Path.Combine(output, "renderer-job.json");
            await WriteJsonAsync(jobPath, JsonSerializer.SerializeToNode(job, JsonOptions)!, cancellationToken);
            try
            {
                if (frameSink is null)
                    await client.RenderAsync(jobPath, Path.Combine(output, "renderer.stderr.log"), cancellationToken);
                else await RenderToSinkAsync(jobPath, Path.Combine(output, "renderer.stderr.log"),
                    checked((int)((ulong)request.Width * request.Height * 4)), request.Frames, frameSink, cancellationToken);
            }
            catch (IOException error)
            {
                throw RendererFailure(Path.Combine(native, "result.json"), error.Message, error);
            }
            RenderResult result = await RendererClient.ReadResultAsync(native, cancellationToken);
            if (!result.Confirms(request.Frames))
                throw new InvalidDataException("Native raw render did not confirm every requested frame.");
            ConfirmVideoRateOverrides(request, result);
            if (request.CaptureTarget is not null && string.IsNullOrWhiteSpace(result.CaptureSource?.RenderTarget))
                throw new InvalidDataException("Renderer did not confirm its actual capture target.");
            if (request.OrthographicCaptureViewport is not null && !JsonNode.DeepEquals(result.OrthographicCaptureViewport, JsonSerializer.SerializeToNode(request.OrthographicCaptureViewport, JsonOptions)))
                throw new InvalidDataException("Renderer did not confirm the requested orthographic capture viewport.");
            if (request.LayerSelection is not null && !JsonNode.DeepEquals(result.LayerSelection, JsonSerializer.SerializeToNode(request.LayerSelection, JsonOptions)))
                throw new InvalidDataException("Renderer did not confirm its layer selection.");
            if (request.TraceScene && (result.RuntimeLayers is null || result.RuntimeDependencies is null))
                throw new InvalidDataException("Renderer did not return requested runtime scene information.");
            if (request.DeviceUuid is not null && !request.DeviceUuid.Equals(result.DeviceUuid, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Renderer did not use the requested GPU.");
            if (frameSink is null && (ulong)new FileInfo(Path.Combine(native, "frames.rgba")).Length != expected)
                throw new InvalidDataException("Raw output length does not match the frame contract.");
            if (sourceHash != await source.SourceHashAsync(cancellationToken)) throw new IOException("Source changed during raw rendering.");
            manifest["native_result"] = result.Json;
            manifest["status"] = "completed";
            if (frameSink is null) manifest["rgba_path"] = Path.Combine(native, "frames.rgba");
            await WriteJsonAsync(manifestPath, manifest, cancellationToken);
            return manifest;
        }
        catch (Exception error)
        {
            bool cancelled = cancellationToken.IsCancellationRequested || error is OperationCanceledException;
            manifest["status"] = cancelled ? "cancelled" : "failed";
            manifest["error_type"] = error.GetType().Name;
            manifest["error"] = error.Message;
            await WriteJsonAsync(manifestPath, manifest, CancellationToken.None);
            if (cancelled && error is not OperationCanceledException) throw new OperationCanceledException("Raw rendering cancelled.", error, cancellationToken);
            throw;
        }
    }

    /// <summary>渲染器 stdout 上的整帧 RGBA 按序交给接收器；帧数必须正好等于请求，多一个字节、少半帧都算失败（IOException，走渲染器失败归因）。</summary>
    private async Task RenderToSinkAsync(string jobPath, string logPath, int frameBytes, ulong frames, IFrameSink sink,
        CancellationToken token)
    {
        await using var log = new FileStream(logPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        await using NativeProcess run = client.StartRender(jobPath, token);
        Task stderr = run.Process.StandardError.BaseStream.CopyToAsync(log, CancellationToken.None);
        Stream stdout = run.Process.StandardOutput.BaseStream;
        byte[] frame = new byte[frameBytes];
        try
        {
            for (ulong index = 0; index < frames; ++index)
            {
                int read = await stdout.ReadAtLeastAsync(frame, frame.Length, throwOnEndOfStream: false, token);
                if (read != frame.Length) throw new IOException($"Renderer stdout ended after {index} of {frames} raw frames.");
                await sink.WriteFrameAsync(index, frame, token);
            }
            if (await stdout.ReadAsync(new byte[1], token) != 0) throw new IOException("Renderer emitted more raw frames than requested.");
            await Task.WhenAll(run.Process.WaitForExitAsync(token), stderr);
            if (run.Process.ExitCode != 0) throw new IOException($"Renderer exited {run.Process.ExitCode}; see {logPath}");
        }
        catch
        {
            run.Stop();
            try { await stderr; } catch { }
            throw;
        }
    }
}
