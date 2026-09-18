using System.Buffers.Binary;
using System.Reflection;
using System.Text.Json.Nodes;
using Baker.Core;

/// <summary>
/// 帧扫描向量化内核（FrameScan）与逐像素参考实现的逐位对照：覆盖范围、alpha 极值、首个非不透明像素索引，
/// 在随机帧、全透明、全不透明、四角单像素、并排打包宽帧、行宽不足一个向量等形状上，且并行度 1/2/4/5 结果全同。
/// 参考实现是 perf/all 303cf8a 上 FrameBounds.Add 与不透明扫描的逐字符副本。不需要渲染器也不需要 GPU。
/// </summary>
internal static class FrameScanChecks
{
    private readonly record struct Bounds(int MinX, int MaxX, int MinY, int MaxY, byte MinAlpha, byte MaxAlpha);

    private static Bounds ReferenceBounds(ReadOnlySpan<byte> rgba, int width, int height, bool includeRgb)
    {
        int minX = width, minY = height, maxX = -1, maxY = -1;
        byte minAlpha = 255, maxAlpha = 0;
        int position = 0;
        for (int y = 0; y < height; ++y)
            for (int x = 0; x < width; ++x, position += 4)
            {
                uint pixel = BinaryPrimitives.ReadUInt32LittleEndian(rgba.Slice(position, 4));
                byte alpha = (byte)(pixel >> 24);
                minAlpha = Math.Min(minAlpha, alpha); maxAlpha = Math.Max(maxAlpha, alpha);
                if (includeRgb ? pixel == 0 : alpha == 0) continue;
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
            }
        return new(minX, maxX, minY, maxY, minAlpha, maxAlpha);
    }

    private static int ReferenceFirstNonOpaque(byte[] rgba)
    {
        for (int alphaIndex = 3; alphaIndex < rgba.Length; alphaIndex += 4)
            if (rgba[alphaIndex] != byte.MaxValue) return alphaIndex;
        return -1;
    }

    internal static void Run(Action<bool, string> check)
    {
        Type scan = typeof(NativeRenderRunner).Assembly.GetType("Baker.Core.FrameScan", throwOnError: true)!;
        MethodInfo boundsMethod = scan.GetMethod("Bounds", BindingFlags.Static | BindingFlags.NonPublic,
            [typeof(ReadOnlyMemory<byte>), typeof(int), typeof(int), typeof(bool), typeof(int)])!;
        MethodInfo opaqueMethod = scan.GetMethod("FirstNonOpaqueAlphaIndex", BindingFlags.Static | BindingFlags.NonPublic,
            [typeof(ReadOnlyMemory<byte>), typeof(int)])!;
        int defaultThreads = (int)scan.GetProperty("Threads", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        check(defaultThreads >= 1 && defaultThreads <= 4, "frame scan default parallelism stays within 1..4 threads");

        Bounds CoreBounds(byte[] rgba, int width, int height, bool includeRgb, int threads)
        {
            object result = boundsMethod.Invoke(null, [new ReadOnlyMemory<byte>(rgba), width, height, includeRgb, threads])!;
            Type type = result.GetType();
            int Int(string name) => (int)type.GetProperty(name)!.GetValue(result)!;
            byte Byte(string name) => (byte)type.GetProperty(name)!.GetValue(result)!;
            return new(Int("MinX"), Int("MaxX"), Int("MinY"), Int("MaxY"), Byte("MinAlpha"), Byte("MaxAlpha"));
        }
        int CoreOpaque(byte[] rgba, int threads) => (int)opaqueMethod.Invoke(null, [new ReadOnlyMemory<byte>(rgba), threads])!;

        var rng = new Random(20260918);
        int[] threadCounts = [1, 2, 4, 5];
        (int Width, int Height)[] sizes = [(1, 1), (3, 1), (5, 4), (7, 3), (8, 1), (9, 5), (33, 17), (64, 2), (1920, 1080), (3840, 1080)];

        byte[] Zero(int w, int h) => new byte[w * h * 4];
        byte[] Random(int w, int h) { byte[] frame = Zero(w, h); rng.NextBytes(frame); return frame; }
        byte[] OpaqueRandom(int w, int h) { byte[] frame = Random(w, h); for (int i = 3; i < frame.Length; i += 4) frame[i] = 255; return frame; }
        byte[] AlphaZeroRandom(int w, int h) { byte[] frame = Random(w, h); for (int i = 3; i < frame.Length; i += 4) frame[i] = 0; return frame; }
        byte[] Corner(int w, int h, int x, int y, byte r, byte a) { byte[] frame = Zero(w, h); int p = (y * w + x) * 4; frame[p] = r; frame[p + 3] = a; return frame; }
        byte[] Centered(int w, int h)
        {
            byte[] frame = Zero(w, h);
            for (int y = h / 5; y < Math.Max(h / 5 + 1, h * 4 / 5); ++y)
                for (int x = w / 5; x < Math.Max(w / 5 + 1, w * 4 / 5); ++x)
                {
                    int p = (y * w + x) * 4;
                    frame[p] = (byte)rng.Next(256); frame[p + 1] = (byte)rng.Next(256); frame[p + 2] = (byte)rng.Next(256); frame[p + 3] = (byte)rng.Next(1, 256);
                }
            return frame;
        }
        byte[] Sparse(int w, int h)
        {
            byte[] frame = Zero(w, h);
            int pixels = w * h;
            for (int i = 0; i < Math.Max(1, pixels / 100); ++i)
            {
                int p = rng.Next(pixels) * 4;
                switch (rng.Next(3))
                {
                    case 0: frame[p + 3] = (byte)rng.Next(1, 256); break;          // 只有 alpha
                    case 1: frame[p + rng.Next(3)] = (byte)rng.Next(1, 256); break; // 只有颜色
                    default: rng.NextBytes(frame.AsSpan(p, 4)); break;
                }
            }
            return frame;
        }
        byte[] Diagonal(int w, int h)
        {
            // 每行的第一个非零像素逐行左移、最后一个逐行右移：逼两段式列收缩在每一行都工作。
            byte[] frame = Zero(w, h);
            for (int y = 0; y < h; ++y)
            {
                int left = Math.Max(0, w - 1 - y), right = Math.Min(w - 1, y);
                frame[(y * w + left) * 4 + 3] = 7; frame[(y * w + right) * 4 + 3] = 9;
            }
            return frame;
        }

        var mismatches = new List<string>();
        int compared = 0;
        foreach (var (w, h) in sizes)
        {
            var frames = new List<(string Name, byte[] Frame)>
            {
                ("zero", Zero(w, h)), ("random", Random(w, h)), ("opaque", OpaqueRandom(w, h)), ("alpha0", AlphaZeroRandom(w, h)),
                ("centered", Centered(w, h)), ("sparse", Sparse(w, h)), ("diagonal", Diagonal(w, h)),
                ("corner-tl-alpha", Corner(w, h, 0, 0, 0, 1)), ("corner-tr-rgb", Corner(w, h, w - 1, 0, 1, 0)),
                ("corner-bl-rgb", Corner(w, h, 0, h - 1, 3, 0)), ("corner-br-alpha", Corner(w, h, w - 1, h - 1, 0, 200)),
            };
            foreach (var (name, frame) in frames)
                foreach (bool includeRgb in new[] { true, false })
                {
                    Bounds expected = ReferenceBounds(frame, w, h, includeRgb);
                    foreach (int threads in threadCounts)
                    {
                        Bounds actual = CoreBounds(frame, w, h, includeRgb, threads);
                        ++compared;
                        if (actual != expected) mismatches.Add($"{w}x{h} {name} rgb={includeRgb} t={threads}: {actual} != {expected}");
                    }
                }
        }
        check(mismatches.Count == 0 && compared > 800,
            "vectorized frame bounds match the per-pixel reference bit for bit across shapes, modes and thread counts" +
            (mismatches.Count == 0 ? "" : ": " + string.Join("; ", mismatches.Take(5))));

        var opaqueMismatches = new List<string>();
        int opaqueCompared = 0;
        foreach (var (w, h) in sizes)
        {
            int pixels = w * h;
            var frames = new List<(string Name, byte[] Frame)> { ("all-255", OpaqueRandom(w, h)), ("zero", Zero(w, h)), ("random", Random(w, h)) };
            foreach (int pixel in new[] { 0, pixels - 1, Math.Min(pixels - 1, 7), Math.Min(pixels - 1, 8), Math.Min(pixels - 1, 9), rng.Next(pixels) })
                foreach (byte alpha in new byte[] { 0, 254 })
                {
                    byte[] frame = OpaqueRandom(w, h);
                    frame[pixel * 4 + 3] = alpha;
                    frames.Add(($"pixel{pixel}-alpha{alpha}", frame));
                }
            byte[] twoPixels = OpaqueRandom(w, h);
            twoPixels[(pixels - 1) * 4 + 3] = 1; twoPixels[(pixels / 2) * 4 + 3] = 2;
            frames.Add(("two-pixels", twoPixels));
            foreach (var (name, frame) in frames)
            {
                int expected = ReferenceFirstNonOpaque(frame);
                foreach (int threads in threadCounts)
                {
                    int actual = CoreOpaque(frame, threads);
                    ++opaqueCompared;
                    if (actual != expected) opaqueMismatches.Add($"{w}x{h} {name} t={threads}: {actual} != {expected}");
                }
            }
        }
        check(opaqueMismatches.Count == 0 && opaqueCompared > 500,
            "vectorized first non-opaque pixel index matches the per-pixel reference across shapes and thread counts" +
            (opaqueMismatches.Count == 0 ? "" : ": " + string.Join("; ", opaqueMismatches.Take(5))));

        // FrameBounds 包装：多帧并集与逐帧参考的并集相同，ToJson 字段照旧。
        Type frameBounds = typeof(NativeRenderRunner).GetNestedType("FrameBounds", BindingFlags.NonPublic)!;
        MethodInfo add = frameBounds.GetMethod("Add")!, toJson = frameBounds.GetMethod("ToJson")!;
        foreach (bool includeRgb in new[] { true, false })
        {
            const int w = 41, h = 23;
            object union = Activator.CreateInstance(frameBounds, w, h)!;
            int minX = w, minY = h, maxX = -1, maxY = -1; byte minAlpha = 255, maxAlpha = 0;
            foreach (byte[] frame in new[] { Sparse(w, h), Corner(w, h, 3, 20, 0, 5), Zero(w, h), Corner(w, h, 39, 1, 8, 0) })
            {
                add.Invoke(union, [new ReadOnlyMemory<byte>(frame), includeRgb]);
                Bounds reference = ReferenceBounds(frame, w, h, includeRgb);
                minX = Math.Min(minX, reference.MinX); maxX = Math.Max(maxX, reference.MaxX);
                minY = Math.Min(minY, reference.MinY); maxY = Math.Max(maxY, reference.MaxY);
                minAlpha = Math.Min(minAlpha, reference.MinAlpha); maxAlpha = Math.Max(maxAlpha, reference.MaxAlpha);
            }
            var json = (JsonObject)toJson.Invoke(union, [includeRgb, "first.rgba", true])!;
            check(json["has_content"]?.GetValue<bool>() == (maxX >= 0) && json["x"]?.GetValue<int>() == minX && json["y"]?.GetValue<int>() == minY &&
                json["width"]?.GetValue<int>() == maxX - minX + 1 && json["height"]?.GetValue<int>() == maxY - minY + 1 &&
                json["minimum_alpha"]?.GetValue<int>() == minAlpha && json["maximum_alpha"]?.GetValue<int>() == maxAlpha &&
                json["includes_rgb"]?.GetValue<bool>() == includeRgb && json["pixel_identical_in_generated_interval"]?.GetValue<bool>() == true,
                $"frame bounds union over several frames matches the per-pixel reference union (includes_rgb={includeRgb})");
        }
    }
}
