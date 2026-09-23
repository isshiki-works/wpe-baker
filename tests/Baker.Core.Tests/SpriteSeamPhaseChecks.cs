using System.Text;
using System.Text.Json.Nodes;
using Baker.Core;

internal static class SpriteSeamPhaseChecks
{
    // 最小精灵 .tex：TEXV0005/TEXI0001 头、TEXB0003 单图单 mip、TEXS0003 帧表；badFrame 额外插一帧坏 imageId。
    internal static byte[] SpriteTex(float frameTime, int frames, bool badFrame = false) =>
        SpriteTex(Enumerable.Repeat(frameTime, frames).ToArray(), badFrame);

    // 同上，帧时长逐帧给出（坏帧插在第 1 位，不占 frameTimes 的位置）。
    internal static byte[] SpriteTex(float[] frameTimes, bool badFrame = false)
    {
        int frames = frameTimes.Length;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        void Stamp(string text) { writer.Write(Encoding.ASCII.GetBytes(text)); writer.Write((byte)0); }
        Stamp("TEXV0005"); Stamp("TEXI0001");
        writer.Write(0); writer.Write(4u); writer.Write(64); writer.Write(64); writer.Write(16); writer.Write(16); writer.Write(0);
        Stamp("TEXB0003"); writer.Write(1); writer.Write(-1);
        writer.Write(1); writer.Write(64); writer.Write(64); writer.Write(0); writer.Write(4); writer.Write(4); writer.Write(new byte[4]);
        Stamp("TEXS0003"); writer.Write(frames + (badFrame ? 1 : 0)); writer.Write(64); writer.Write(64);
        for (int i = 0, next = 0; i < frames + (badFrame ? 1 : 0); ++i)
        {
            writer.Write(badFrame && i == 1 ? 7 : 0); writer.Write(badFrame && i == 1 ? 9f : frameTimes[next++]);
            for (int j = 0; j < 6; ++j) writer.Write(0f);
        }
        writer.Flush();
        return stream.ToArray();
    }

    internal static void Run(Action<bool, string> check, string root)
    {
        float[] Table(float time, int count) => Enumerable.Repeat(time, count).ToArray();

        check(SpriteSeamPhase.TryReadFrameTimes(SpriteTex(0.07f, 16, badFrame: true), out float[] read) &&
            read.Length == 16 && read.All(x => BitConverter.SingleToInt32Bits(x) == BitConverter.SingleToInt32Bits(0.07f)),
            "sprite .tex frame table reads the renderer's float32 frame times and skips frames with an invalid image id");

        // 2325500626 形态：16×0.07，0.07f 偏大；P=336 帧 = 5 个十进制周期。第 P 帧退回上一精灵帧，整周期预热后逐帧闭合。
        var bonfire = SpriteSeamPhase.Select([Table(0.07f, 16)], 60, 1, 336);
        check(bonfire.AtOrigin == SpriteSeamPhase.Verdict.Mismatch && bonfire.AfterOnePeriod == SpriteSeamPhase.Verdict.Closed &&
            bonfire.WarmupFrames == 336,
            "float32-long sprite frame time (0.07) misses the seam from frame 0 by one sprite frame and closes after a one-period warmup");
        check(SpriteSeamPhase.Verify(Table(0.07f, 16), 60, 1, 336, 1008) == SpriteSeamPhase.Verdict.Closed,
            "one-period warmup also closes the longer 1008-frame candidate of the same sprite");

        // 3000562427 形态：24×0.1 与 48×0.1 两张帧表，P=288。
        var summer = SpriteSeamPhase.Select([Table(0.1f, 24), Table(0.1f, 48)], 60, 1, 288);
        check(summer.AtOrigin == SpriteSeamPhase.Verdict.Mismatch && summer.WarmupFrames == 288,
            "two float32-long sprite tables (0.1) share one deterministic one-period warmup");

        // 3726503096 形态：0.04f 偏小，起点 0 就闭合，不加预热。
        var shortTime = SpriteSeamPhase.Select([Table(0.04f, 12)], 60, 1, 432);
        check(shortTime.AtOrigin == SpriteSeamPhase.Verdict.Closed && shortTime.WarmupFrames == 0 && shortTime.AfterOnePeriod is null,
            "float32-short sprite frame time (0.04) closes from frame 0 and keeps the candidate unchanged");

        // 二进制精确时长：第 P 帧精确落在边界上，由渲染器 double 累加舍入决定，不下结论。
        check(SpriteSeamPhase.Verify(Table(0.25f, 4), 60, 1, 0, 60) == SpriteSeamPhase.Verdict.Undetermined,
            "an exactly representable sprite period puts later samples on a boundary and is reported undetermined, not closed");

        string sourceDirectory = Path.Combine(root, "sprite-seam-source");
        Directory.CreateDirectory(Path.Combine(sourceDirectory, "materials"));
        File.WriteAllText(Path.Combine(sourceDirectory, "project.json"), "{\"type\":\"scene\",\"file\":\"scene.json\"}");
        File.WriteAllText(Path.Combine(sourceDirectory, "scene.json"), "{\"objects\":[]}");
        File.WriteAllBytes(Path.Combine(sourceDirectory, "materials", "Long.tex"), SpriteTex(0.07f, 16));
        File.WriteAllBytes(Path.Combine(sourceDirectory, "materials", "Short.tex"), SpriteTex(0.04f, 12));
        using var source = new ProjectSource(sourceDirectory);
        JsonObject Analyze(string texture, double duration) => LoopAnalysis.Analyze(
            new JsonObject { ["objects"] = new JsonArray { new JsonObject { ["id"] = 1 } } }, source, null,
            new JsonObject { ["runtime_animation_periods"] = new JsonArray { new JsonObject {
                ["source_owner_layer_id"] = 1, ["mechanism"] = "sprite", ["track_name"] = texture, ["duration_seconds"] = duration,
                ["looping"] = true, ["event_driven"] = false, ["confidence"] = "high" } } }, [1], 60, 1).ToJson();

        JsonObject longPlan = Analyze("Long", 1.12);
        JsonObject longFirst = longPlan["candidates"]!.AsArray()[0]!.AsObject();
        check(longFirst["frames"]!.GetValue<ulong>() == 336 && longFirst["source_period_warmup_frames"]?.GetValue<ulong>() == 336 &&
            longPlan["candidates"]!.AsArray().OfType<JsonObject>().All(x => x["source_period_warmup_frames"]?.GetValue<ulong>() == x["frames"]!.GetValue<ulong>()),
            "loop analysis attaches a one-period warmup to every candidate of a float32-long sprite");

        JsonObject shortPlan = Analyze("Short", 0.48);
        check(shortPlan["candidates"]!.AsArray().Count > 0 &&
            shortPlan["candidates"]!.AsArray().OfType<JsonObject>().All(x => x["source_period_warmup_frames"] is null && x["sprite_seam_phase"] is null),
            "loop analysis leaves float32-short sprite candidates unchanged");

        JsonObject mismatchedTable = Analyze("Long", 1.2);
        check(mismatchedTable["candidates"]!.AsArray().OfType<JsonObject>().All(x => x["source_period_warmup_frames"] is null),
            "a texture whose frame-table sum disagrees with the runtime trace duration is not used for the seam phase");
    }
}
