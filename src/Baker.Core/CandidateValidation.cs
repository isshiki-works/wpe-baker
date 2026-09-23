using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Baker.Core;

public sealed record ValidationRequest(int SchemaVersion, string Source, string Candidate, string Assets,
    string OutputDirectory, uint Width, uint Height, uint FpsNumerator = 60, uint FpsDenominator = 1,
    ulong Frames = 24, ulong WarmupFrames = 0, ulong Seed = 0, string? DeviceUuid = null,
    JsonObject? UserProperties = null, JsonObject? Input = null, JsonArray? InputTimeline = null, uint TileSize = 64);

/// <summary>Measures sampled offline RGBA differences; never certifies visual equivalence.</summary>
public sealed class CandidateValidation(NativeTools tools)
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    /// <summary>CLI <c>validate</c> 的入口：返回的就是写进 comparison.json 的报告。</summary>
    public async Task<JsonObject> ValidateAsync(ValidationRequest request, IProgress<RenderProgress>? progress = null,
        CancellationToken cancellationToken = default) => (await CompareAsync(request, progress, cancellationToken)).Report;

    /// <summary>
    /// 原作与候选各起一个渲染器，两条 stdout 锁步逐帧比较，帧不落盘；
    /// 每帧按 <see cref="ValidationRequest.TileSize"/> 见方的瓦片做整数累加（<see cref="PairedFrameComparer"/>）。
    /// </summary>
    internal async Task<PairedComparison> CompareAsync(ValidationRequest request, IProgress<RenderProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (request.SchemaVersion != 1 || request.Frames == 0 || request.FpsNumerator == 0 ||
            request.FpsDenominator == 0 || request.TileSize == 0 || request.TileSize > ushort.MaxValue)
            throw new ArgumentException("Validation requires schema version 1 and positive frame, FPS and tile values.");
        FrameByteCount(request.Width, request.Height);
        string output = PrepareOutput(request.Source, request.Candidate, request.OutputDirectory);
        string reportPath = Path.Combine(output, "comparison.json");
        string framesPath = Path.Combine(output, "frame-comparison.jsonl");
        var report = new JsonObject
        {
            ["schema_version"] = 1, ["status"] = "running", ["report_path"] = reportPath,
            ["request"] = JsonSerializer.SerializeToNode(request, JsonOptions),
            ["scope"] = "Sampled full-frame RGBA8 from the same offline renderer and inputs; RGB includes transparent pixels.",
            ["automatic_visual_certification"] = false, ["official_playback_verified"] = false,
            ["frames_report_path"] = framesPath
        };
        await WriteReportAsync(reportPath, report, cancellationToken);
        bool validationFailed = false;
        try
        {
            var runner = new NativeRenderRunner(tools);
            RenderRequest RenderSide(string source, string label) => new(source, request.Assets, Path.Combine(output, label),
                request.Width, request.Height, request.FpsNumerator, request.FpsDenominator, request.Frames,
                request.WarmupFrames, request.Seed, DeviceUuid: request.DeviceUuid, UserProperties: request.UserProperties,
                Input: request.Input, InputTimeline: request.InputTimeline, TraceScene: true);
            var comparer = new PairedFrameComparer(checked((int)request.Width), checked((int)request.Height), checked((int)request.TileSize));
            await using var frameReport = new StreamWriter(new FileStream(framesPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.Read, 128 * 1024, true), new UTF8Encoding(false));
            progress?.Report(new("rendering_pair", 0, "Rendering the source and the candidate with the same sampled inputs; frames are compared as they arrive."));
            async ValueTask CompareFrameAsync(ulong frame, ReadOnlyMemory<byte> a, ReadOnlyMemory<byte> b, CancellationToken token)
            {
                PixelErrors frameErrors = comparer.Add(a, b, token);
                var line = new JsonObject { ["frame"] = frame, ["metrics"] = frameErrors.ToJson() };
                await frameReport.WriteLineAsync(line.ToJsonString().AsMemory(), token);
                progress?.Report(new("comparing", (double)(frame + 1) / request.Frames, $"Compared {frame + 1} / {request.Frames} frames."));
            }
            (JsonObject original, JsonObject candidate) = await RenderLockstepAsync(
                (sink, token) => runner.RenderRawAsync(RenderSide(request.Source, "source"), sink, token),
                (sink, token) => runner.RenderRawAsync(RenderSide(request.Candidate, "candidate"), sink, token),
                CompareFrameAsync, cancellationToken);
            report["source_sha256"] = original["source_sha256"]!.DeepClone();
            report["candidate_sha256"] = candidate["source_sha256"]!.DeepClone();
            report["source_native_result"] = original["native_result"]!.DeepClone();
            report["candidate_native_result"] = candidate["native_result"]!.DeepClone();
            report["source_compiled_scene_passes"] = original["native_result"]!["compiled_scene_passes"]!.DeepClone();
            report["candidate_compiled_scene_passes"] = candidate["native_result"]!["compiled_scene_passes"]!.DeepClone();
            using var sourceView = new ProjectSource(request.Source);
            using var candidateView = new ProjectSource(request.Candidate);
            if (await sourceView.SourceHashAsync(cancellationToken) != original["source_sha256"]!.GetValue<string>() ||
                await candidateView.SourceHashAsync(cancellationToken) != candidate["source_sha256"]!.GetValue<string>())
                throw new IOException("Source or candidate changed during the paired comparison.");
            report["object_preservation"] = ObjectPreservation(sourceView, candidateView);
            report["lookup_binding_validation"] = CompareLookupBindings(
                sourceView.ReadJson(sourceView.SceneResource)["objects"]!.AsArray(),
                candidateView.ReadJson(candidateView.SceneResource)["objects"]!.AsArray(),
                original["native_result"]?["runtime_dependencies"] as JsonArray,
                candidate["native_result"]?["runtime_dependencies"] as JsonArray);
            report["frames_compared"] = request.Frames;
            report["metrics"] = comparer.Total.ToJson();
            report["tiles"] = new JsonArray([.. comparer.Tiles.Select(tile => (JsonNode)tile.ToJson())]);
            report["worst_rgb_tile"] = report["tiles"]![comparer.WorstRgbTile]!.DeepClone();
            report["byte_identical_samples"] = comparer.Total.MaxRgb == 0 && comparer.Total.MaxAlpha == 0;
            report["status"] = "compared";
            await WriteReportAsync(reportPath, report, cancellationToken);
            return new PairedComparison(report, comparer.Total, comparer.Tiles);
        }
        catch (Exception error)
        {
            validationFailed = true;
            report["status"] = cancellationToken.IsCancellationRequested ? "cancelled" : "failed";
            report["error_type"] = error.GetType().Name;
            report["error"] = error.Message;
            await WriteReportAsync(reportPath, report, CancellationToken.None);
            throw;
        }
        finally
        {
            // 帧不落盘；渲染器照旧写出的 PCM 在这里删掉。
            TemporaryCaptureFiles.Delete(report, output, "source/native/audio.f32le", "source/native/audio.f32le.partial",
                "candidate/native/audio.f32le", "candidate/native/audio.f32le.partial");
            if (report.ContainsKey("temporary_cleanup_errors"))
                try { await WriteReportAsync(reportPath, report, CancellationToken.None); }
                catch when (validationFailed) { }
        }
    }

    /// <summary>
    /// 两个渲染器同时跑，stdout 锁步：两边都交出第 i 帧时由后到的一边调用 <paramref name="compare"/>，先到的一边等它比完
    /// 才把缓冲区还给自己的渲染器，所以比较时两块缓冲区都有效，也不用复制。任何一边失败（含比较失败）就取消另一边；
    /// 被连带取消的一边结束为 Canceled、不带异常，所以抛出的是失败那边的原始错误。
    /// </summary>
    internal static async Task<(JsonObject Source, JsonObject Candidate)> RenderLockstepAsync(
        Func<Lockstep.Side, CancellationToken, Task<JsonObject>> source, Func<Lockstep.Side, CancellationToken, Task<JsonObject>> candidate,
        Func<ulong, ReadOnlyMemory<byte>, ReadOnlyMemory<byte>, CancellationToken, ValueTask> compare, CancellationToken token)
    {
        using var pair = CancellationTokenSource.CreateLinkedTokenSource(token);
        var lockstep = new Lockstep(compare);
        async Task<JsonObject> Side(Func<Lockstep.Side, CancellationToken, Task<JsonObject>> render, Lockstep.Side sink)
        {
            try { return await render(sink, pair.Token); }
            catch { await pair.CancelAsync(); throw; }
        }
        Task<JsonObject> a = Side(source, lockstep.Source), b = Side(candidate, lockstep.Candidate);
        await Task.WhenAll(a, b);
        return (a.Result, b.Result);
    }

    /// <summary>两个接收器共用的汇合点：同一时刻最多一边在等（它交出的帧没比完之前，它的渲染器交不出下一帧）。</summary>
    internal sealed class Lockstep
    {
        private readonly Func<ulong, ReadOnlyMemory<byte>, ReadOnlyMemory<byte>, CancellationToken, ValueTask> compare;
        private readonly Lock gate = new();
        private (ReadOnlyMemory<byte> Frame, TaskCompletionSource Done)? waiting;

        internal Lockstep(Func<ulong, ReadOnlyMemory<byte>, ReadOnlyMemory<byte>, CancellationToken, ValueTask> compare)
        {
            this.compare = compare;
            Source = new Side(this, isSource: true);
            Candidate = new Side(this, isSource: false);
        }

        internal Lockstep.Side Source { get; }
        internal Lockstep.Side Candidate { get; }

        private async ValueTask ArriveAsync(bool isSource, ulong index, ReadOnlyMemory<byte> frame, CancellationToken token)
        {
            (ReadOnlyMemory<byte> Frame, TaskCompletionSource Done)? first;
            var mine = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (gate)
            {
                first = waiting;
                waiting = first is null ? (frame, mine) : null;
            }
            if (first is not { } other)
            {
                await mine.Task.WaitAsync(token);
                return;
            }
            // 比较失败时这里直接抛出，等着的一边随整对取消退出。
            await (isSource ? compare(index, frame, other.Frame, token) : compare(index, other.Frame, frame, token));
            other.Done.SetResult();
        }

        internal sealed class Side(Lockstep owner, bool isSource)
        {
            public ValueTask WriteFrameAsync(ulong index, ReadOnlyMemory<byte> rgba, CancellationToken token) =>
                owner.ArriveAsync(isSource, index, rgba, token);
        }
    }

    internal static JsonObject CompareLookupBindings(JsonArray sourceObjects, JsonArray candidateObjects,
        JsonArray? sourceDependencies, JsonArray? candidateDependencies)
    {
        var mismatches = new JsonArray();
        var report = new JsonObject
        {
            ["status"] = "not_available", ["mismatches"] = mismatches,
            ["scope"] = "Lookup target sets of retained authored script owners in this paired sample only; unexecuted branches and dynamic-object identity are not certified."
        };
        if (sourceDependencies is null || candidateDependencies is null) return report;
        try
        {
            SceneAssembler.GuardPublicLayerQueries(sourceObjects.OfType<JsonObject>(), candidateObjects.OfType<JsonObject>(),
                SceneAssembler.MergeRuntimeDependencies(sourceDependencies, candidateDependencies));
        }
        catch (InvalidDataException error)
        {
            report["status"] = "rejected_public_layer_queries";
            report["reason"] = error.Message;
            return report;
        }
        var sourceIds = sourceObjects.OfType<JsonObject>().Where(obj => Scripts(obj).Count > 0)
            .Select(SceneGraph.Id).ToHashSet();
        var retained = candidateObjects.OfType<JsonObject>()
            .Where(obj => sourceIds.Contains(SceneGraph.Id(obj)) && Scripts(obj).Count > 0)
            .Select(SceneGraph.Id).ToHashSet();
        Dictionary<(int Owner, string Binding, bool Initialization, string Property), HashSet<int>> Lookups(JsonArray dependencies)
        {
            var result = new Dictionary<(int, string, bool, string), HashSet<int>>();
            foreach (var dependency in dependencies.OfType<JsonObject>())
            {
                if (dependency["operation"]?.GetValue<string>() != "lookup" ||
                    SceneGraph.Int(dependency["owner"]) is not int owner || !retained.Contains(owner)) continue;
                var key = (owner, dependency["binding"]?.GetValue<string>() ?? "",
                    dependency["initialization"]?.GetValue<bool>() == true, dependency["property"]?.GetValue<string>() ?? "");
                if (!result.TryGetValue(key, out var targets)) result.Add(key, targets = []);
                targets.Add(SceneGraph.Int(dependency["target"]) ?? -1);
            }
            return result;
        }
        var original = Lookups(sourceDependencies);
        var candidate = Lookups(candidateDependencies);
        foreach (var key in original.Keys.Union(candidate.Keys).OrderBy(key => key.Owner)
            .ThenBy(key => key.Binding, StringComparer.Ordinal).ThenBy(key => key.Initialization)
            .ThenBy(key => key.Property, StringComparer.Ordinal))
        {
            var a = original.GetValueOrDefault(key) ?? [];
            var b = candidate.GetValueOrDefault(key) ?? [];
            if (a.SetEquals(b)) continue;
            mismatches.Add(new JsonObject
            {
                ["owner"] = key.Owner, ["binding"] = key.Binding, ["initialization"] = key.Initialization,
                ["property"] = key.Property, ["source_targets"] = JsonSerializer.SerializeToNode(a.Order()),
                ["candidate_targets"] = JsonSerializer.SerializeToNode(b.Order())
            });
        }
        report["retained_script_owner_count"] = retained.Count;
        report["source_lookup_binding_count"] = original.Count;
        report["candidate_lookup_binding_count"] = candidate.Count;
        report["status"] = mismatches.Count == 0 ? "observed_lookups_match" : "rejected_lookup_bindings";
        return report;
    }

    internal static int FrameByteCount(uint width, uint height)
    {
        ulong bytes = (ulong)width * height * 4;
        if (width == 0 || height == 0 || width > ushort.MaxValue || height > ushort.MaxValue || bytes > 256ul * 1024 * 1024)
            throw new ArgumentException("RGBA extent must be positive and fit the native 256 MiB frame budget.");
        return checked((int)bytes);
    }

    internal static string PrepareOutput(string original, string candidate, string requested)
    {
        string output = Path.GetFullPath(requested);
        ProjectSource.EnsureNoReparsePoints(output);
        if (Directory.Exists(output) || File.Exists(output)) throw new IOException("Paired output must be a new directory.");
        foreach (string input in new[] { original, candidate })
        {
            using var source = new ProjectSource(input);
            if (source.Kind != "scene") throw new InvalidDataException("Paired rendering requires scene projects.");
            string prefix = Path.TrimEndingDirectorySeparator(source.DirectoryPath) + Path.DirectorySeparatorChar;
            if (output.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Paired output cannot be inside either input project.");
        }
        Directory.CreateDirectory(output);
        return output;
    }

    internal static Task WriteReportAsync(string path, JsonObject report, CancellationToken token) =>
        File.WriteAllTextAsync(path, report.ToJsonString(JsonOptions), token);

    private static JsonObject ObjectPreservation(ProjectSource source, ProjectSource candidate)
    {
        JsonObject aScene = source.ReadJson(source.SceneResource), bScene = candidate.ReadJson(candidate.SceneResource);
        JsonArray aObjects = aScene["objects"]?.AsArray() ?? throw new InvalidDataException("Source objects are missing.");
        JsonArray bObjects = bScene["objects"]?.AsArray() ?? throw new InvalidDataException("Candidate objects are missing.");
        int[] aIds = aObjects.Select(x => x?["id"]?.GetValue<int>() ?? -1).ToArray();
        int[] bIds = bObjects.Select(x => x?["id"]?.GetValue<int>() ?? -1).ToArray();
        var rows = new JsonArray();
        for (int i = 0; i < aIds.Length; ++i)
        {
            int[] matches = Enumerable.Range(0, bIds.Length).Where(j => bIds[j] == aIds[i]).ToArray();
            var row = new JsonObject { ["id"] = aIds[i], ["source_order"] = i,
                ["candidate_order"] = matches.Length == 1 ? JsonValue.Create(matches[0]) : null,
                ["state"] = matches.Length == 1 ? "present" : matches.Length == 0 ? "missing" : "ambiguous_id" };
            Dictionary<string, string> aScripts = Scripts(aObjects[i]);
            row["source_script_count"] = aScripts.Count;
            if (matches.Length == 1)
            {
                Dictionary<string, string> bScripts = Scripts(bObjects[matches[0]]);
                row["candidate_script_count"] = bScripts.Count;
                row["script_texts_equal"] = aScripts.Values.Order(StringComparer.Ordinal).SequenceEqual(bScripts.Values.Order(StringComparer.Ordinal));
                row["script_binding_paths_equal"] = aScripts.OrderBy(x => x.Key, StringComparer.Ordinal).SequenceEqual(bScripts.OrderBy(x => x.Key, StringComparer.Ordinal));
            }
            rows.Add(row);
        }
        Dictionary<string, string> allA = Scripts(aScene), allB = Scripts(bScene);
        return new JsonObject
        {
            ["scope"] = "Authored scene JSON IDs, order and script text/binding paths; runtime script behavior is not certified.",
            ["source_order"] = JsonSerializer.SerializeToNode(aIds), ["candidate_order"] = JsonSerializer.SerializeToNode(bIds),
            ["original_order_preserved"] = bIds.Where(aIds.Contains).SequenceEqual(aIds), ["objects"] = rows,
            ["source_script_count"] = allA.Count, ["candidate_script_count"] = allB.Count,
            ["script_texts_equal"] = allA.Values.Order(StringComparer.Ordinal).SequenceEqual(allB.Values.Order(StringComparer.Ordinal))
        };
    }

    private static Dictionary<string, string> Scripts(JsonNode? node)
    {
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        void Walk(JsonNode? current, string path)
        {
            if (current is JsonObject obj)
                foreach (var pair in obj)
                {
                    string child = path + "/" + pair.Key.Replace("~", "~0").Replace("/", "~1");
                    if (pair.Key == "script" && pair.Value is not null) found.Add(child, pair.Value.ToJsonString());
                    Walk(pair.Value, child);
                }
            else if (current is JsonArray array)
                for (int i = 0; i < array.Count; ++i) Walk(array[i], path + "/" + i);
        }
        Walk(node, "");
        return found;
    }
}

/// <summary>成对比较的类型化结果：<see cref="Report"/> 就是 comparison.json；合成门（<see cref="CompositionGate"/>）读 <see cref="Total"/> 与 <see cref="Tiles"/> 判定。</summary>
internal sealed record PairedComparison(JsonObject Report, PixelErrors Total, PairedTile[] Tiles);

/// <summary>一个瓦片的位置、尺寸与跨帧累计误差。</summary>
internal sealed record PairedTile(int X, int Y, int Width, int Height, PixelErrors Errors)
{
    internal JsonObject ToJson() => new() { ["x"] = X, ["y"] = Y, ["width"] = Width, ["height"] = Height, ["metrics"] = Errors.ToJson() };
}

/// <summary>
/// 逐像素 |a−b| 的整数累计：RGB 三通道合计、alpha 单列。均值等派生量只在写出与判定时由整数和算出，
/// 与原来逐像素 double 累加的结果逐位相同（double 累加整数在 2^53 以内是精确的，48 帧 8K 的平方和约 3e14）。
/// </summary>
internal sealed class PixelErrors
{
    internal ulong Pixels, DifferentRgb, DifferentAlpha, RgbSum, RgbSquared, AlphaSum;
    internal int MaxRgb, MaxAlpha;

    /// <summary>RGB 平均绝对误差（0–255 标度），写出与合成门判定用同一个表达式。</summary>
    internal double RgbMae => RgbSum / (Pixels * 3.0);
    internal double AlphaMae => (double)AlphaSum / Pixels;

    internal void Merge(PixelErrors other)
    {
        Pixels += other.Pixels; DifferentRgb += other.DifferentRgb; DifferentAlpha += other.DifferentAlpha;
        RgbSum += other.RgbSum; RgbSquared += other.RgbSquared; AlphaSum += other.AlphaSum;
        MaxRgb = Math.Max(MaxRgb, other.MaxRgb); MaxAlpha = Math.Max(MaxAlpha, other.MaxAlpha);
    }

    internal JsonObject ToJson() => new()
    {
        ["pixels"] = Pixels, ["rgb_mae_255"] = RgbMae,
        ["rgb_rmse_255"] = Math.Sqrt(RgbSquared / (Pixels * 3.0)), ["rgb_max_abs_255"] = (double)MaxRgb,
        ["alpha_mae_255"] = AlphaMae, ["alpha_max_abs_255"] = (double)MaxAlpha,
        ["differing_rgb_pixel_fraction"] = (double)DifferentRgb / Pixels,
        ["differing_alpha_pixel_fraction"] = (double)DifferentAlpha / Pixels
    };
}

/// <summary>
/// 成对帧比较：一帧按瓦片行并行，瓦片内 AVX2 每次 8 个像素（CPU 不支持或不足 8 个像素时逐像素），全部整数累加。
/// 瓦片跨帧累计，帧合计由瓦片相加，最后的整体合计由帧相加。
/// </summary>
internal sealed class PairedFrameComparer
{
    private readonly int width, height, rows, columns;

    internal PairedFrameComparer(int width, int height, int edge)
    {
        this.width = width; this.height = height;
        columns = (width + edge - 1) / edge; rows = (height + edge - 1) / edge;
        Tiles = new PairedTile[checked(columns * rows)];
        for (int i = 0; i < Tiles.Length; ++i)
        {
            int x = i % columns * edge, y = i / columns * edge;
            Tiles[i] = new(x, y, Math.Min(edge, width - x), Math.Min(edge, height - y), new PixelErrors());
        }
    }

    internal PairedTile[] Tiles { get; }
    internal PixelErrors Total { get; } = new();

    /// <summary>RGB 平均误差最大的瓦片（并列取第一个）。</summary>
    internal int WorstRgbTile => Enumerable.Range(0, Tiles.Length).MaxBy(i => (double)Tiles[i].Errors.RgbSum / Tiles[i].Errors.Pixels);

    /// <summary>加一帧（两块 宽×高×4 的 RGBA8），返回这一帧的合计。</summary>
    internal PixelErrors Add(ReadOnlyMemory<byte> a, ReadOnlyMemory<byte> b, CancellationToken token, bool vectorized = true)
    {
        // 内核用不检查边界的向量读取；长度不对直接拒绝，不读越界内存。
        if (a.Length != width * height * 4 || b.Length != a.Length) throw new ArgumentException("Paired frames must both be width x height RGBA8.");
        vectorized &= Avx2.IsSupported;
        var frameTiles = new PixelErrors[Tiles.Length];
        Parallel.For(0, rows, new ParallelOptions { CancellationToken = token }, row =>
        {
            for (int column = 0, i = row * columns; column < columns; ++column, ++i)
                frameTiles[i] = Tile(a.Span, b.Span, width, Tiles[i], vectorized);
        });
        var frame = new PixelErrors();
        for (int i = 0; i < Tiles.Length; ++i)
        {
            frame.Merge(frameTiles[i]);
            Tiles[i].Errors.Merge(frameTiles[i]);
        }
        Total.Merge(frame);
        return frame;
    }

    private static PixelErrors Tile(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, int stride, PairedTile tile, bool vectorized)
    {
        var errors = new PixelErrors { Pixels = (ulong)tile.Width * (ulong)tile.Height };
        int vectorWidth = vectorized ? tile.Width & ~7 : 0;
        if (vectorWidth > 0) AddVectorized(a, b, stride, tile, vectorWidth, errors);
        for (int y = tile.Y; y < tile.Y + tile.Height; ++y)
            for (int x = tile.X + vectorWidth; x < tile.X + tile.Width; ++x)
            {
                int offset = (y * stride + x) * 4;
                int dr = Math.Abs(a[offset] - b[offset]), dg = Math.Abs(a[offset + 1] - b[offset + 1]);
                int db = Math.Abs(a[offset + 2] - b[offset + 2]), da = Math.Abs(a[offset + 3] - b[offset + 3]);
                errors.RgbSum += (ulong)(dr + dg + db);
                errors.RgbSquared += (ulong)(dr * dr + dg * dg + db * db);
                errors.AlphaSum += (ulong)da;
                int maximum = Math.Max(dr, Math.Max(dg, db));
                errors.MaxRgb = Math.Max(errors.MaxRgb, maximum); errors.MaxAlpha = Math.Max(errors.MaxAlpha, da);
                if (maximum != 0) ++errors.DifferentRgb;
                if (da != 0) ++errors.DifferentAlpha;
            }
        return errors;
    }

    /// <summary>
    /// 每行前 <paramref name="vectorWidth"/> 个像素（8 的倍数）：|a−b| 用无符号字节 max−min，RGB 与 alpha 按掩码分开；
    /// 和用 SAD 累到 64 位；平方和用 pmaddwd 累到 32 位、每行并入 64 位（一行最多 8191 次，每道每次不超过 4×255²，不溢出 uint）。
    /// </summary>
    private static void AddVectorized(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, int stride, PairedTile tile, int vectorWidth, PixelErrors errors)
    {
        Vector256<byte> rgbMask = Vector256.Create(0x00FFFFFFu).AsByte(), alphaMask = Vector256.Create(0xFF000000u).AsByte();
        Vector256<ulong> rgbSum = default, alphaSum = default;
        Vector256<byte> maxRgb = default, maxAlpha = default;
        ref byte left = ref MemoryMarshal.GetReference(a), right = ref MemoryMarshal.GetReference(b);
        for (int y = tile.Y; y < tile.Y + tile.Height; ++y)
        {
            Vector256<uint> squares = default, sameRgb = default, sameAlpha = default;
            nuint offset = (nuint)((y * stride + tile.X) * 4), end = offset + (nuint)(vectorWidth * 4);
            for (; offset < end; offset += 32)
            {
                Vector256<byte> va = Vector256.LoadUnsafe(ref left, offset), vb = Vector256.LoadUnsafe(ref right, offset);
                Vector256<byte> difference = Avx2.Subtract(Avx2.Max(va, vb), Avx2.Min(va, vb));
                Vector256<byte> rgb = Avx2.And(difference, rgbMask), alpha = Avx2.And(difference, alphaMask);
                rgbSum = Avx2.Add(rgbSum, Avx2.SumAbsoluteDifferences(rgb, Vector256<byte>.Zero).AsUInt64());
                alphaSum = Avx2.Add(alphaSum, Avx2.SumAbsoluteDifferences(alpha, Vector256<byte>.Zero).AsUInt64());
                Vector256<short> low = Avx2.UnpackLow(rgb, Vector256<byte>.Zero).AsInt16(), high = Avx2.UnpackHigh(rgb, Vector256<byte>.Zero).AsInt16();
                squares = Avx2.Add(squares, Avx2.Add(Avx2.MultiplyAddAdjacent(low, low), Avx2.MultiplyAddAdjacent(high, high)).AsUInt32());
                maxRgb = Avx2.Max(maxRgb, rgb); maxAlpha = Avx2.Max(maxAlpha, alpha);
                // 相等时比较结果是全 1（−1），减掉即计数加一。
                sameRgb = Avx2.Subtract(sameRgb, Avx2.CompareEqual(rgb.AsUInt32(), Vector256<uint>.Zero));
                sameAlpha = Avx2.Subtract(sameAlpha, Avx2.CompareEqual(alpha.AsUInt32(), Vector256<uint>.Zero));
            }
            (Vector256<ulong> lower, Vector256<ulong> upper) = Vector256.Widen(squares);
            errors.RgbSquared += Vector256.Sum(lower + upper);
            errors.DifferentRgb += (ulong)vectorWidth - Vector256.Sum(sameRgb);
            errors.DifferentAlpha += (ulong)vectorWidth - Vector256.Sum(sameAlpha);
        }
        errors.RgbSum += Vector256.Sum(rgbSum);
        errors.AlphaSum += Vector256.Sum(alphaSum);
        for (int i = 0; i < Vector256<byte>.Count; ++i)
        {
            errors.MaxRgb = Math.Max(errors.MaxRgb, maxRgb.GetElement(i));
            errors.MaxAlpha = Math.Max(errors.MaxAlpha, maxAlpha.GetElement(i));
        }
    }
}
