using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Baker.Core;

public sealed partial class NativeRenderRunner
{
    /// <summary>Retains raw RGBA for static caches and comparisons; supports odd local texture extents.</summary>
    public async Task<JsonObject> RenderRawAsync(RenderRequest request, CancellationToken cancellationToken = default)
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
        TemporaryCaptureFiles.RequireFreeSpace(output, checked(expected + pcmBytes));
        Directory.CreateDirectory(output);
        var manifest = new JsonObject { ["schema_version"] = 1, ["artifact_kind"] = "raw_frames", ["status"] = "running",
            ["source_sha256"] = sourceHash, ["source_digest_scope"] = ProjectSource.DigestScope, ["request"] = JsonSerializer.SerializeToNode(request, JsonOptions) };
        string manifestPath = Path.Combine(output, "manifest.json");
        await WriteJsonAsync(manifestPath, manifest, cancellationToken);
        try
        {
            if (request.CaptureTarget?.ForceVisibleOwner == true &&
                !(await RunTextAsync(tools.Renderer, ["--version"], Path.Combine(output, "renderer-version.stderr.log"), cancellationToken))
                    .Contains("capture-force-visible-owner-v1", StringComparison.Ordinal))
                throw new InvalidDataException("Renderer does not support capturing a visibility-controlled effect owner.");
            string native = Path.Combine(output, "native");
            RenderJob job = RenderJob.From(request, source.SourcePath, native, rawStdout: false);
            string jobPath = Path.Combine(output, "renderer-job.json");
            await WriteJsonAsync(jobPath, JsonSerializer.SerializeToNode(job, JsonOptions)!, cancellationToken);
            try
            {
                _ = await RunTextAsync(tools.Renderer, ["render", "--job", jobPath], Path.Combine(output, "renderer.stderr.log"), cancellationToken);
            }
            catch (IOException error)
            {
                throw RendererFailure(Path.Combine(native, "result.json"), error.Message, error);
            }
            RenderResult result = RenderResult.Parse(await File.ReadAllTextAsync(Path.Combine(native, "result.json"), cancellationToken));
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
            if ((ulong)new FileInfo(Path.Combine(native, "frames.rgba")).Length != expected)
                throw new InvalidDataException("Raw output length does not match the frame contract.");
            if (sourceHash != await source.SourceHashAsync(cancellationToken)) throw new IOException("Source changed during raw rendering.");
            manifest["native_result"] = result.Json;
            manifest["status"] = "completed";
            manifest["rgba_path"] = Path.Combine(native, "frames.rgba");
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
}
