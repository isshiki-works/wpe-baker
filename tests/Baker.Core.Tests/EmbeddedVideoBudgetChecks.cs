using System.Text.Json.Nodes;
using Baker.Core;

/// <summary>
/// 内嵌视频 2 GiB 上限（fix/embedded-video-size）：参考码率、循环长度收紧、试编码外推与拒绝文案。
/// 数字取自现有成品的实测（3572877776 两个分辨率、阿米娅 2500 s 的试编码与成品），不渲染、不调 ffprobe。
/// </summary>
internal static class EmbeddedVideoBudgetChecks
{
    public static void Run(Action<bool, string> check)
    {
        ArgumentNullException.ThrowIfNull(check);

        // ---- 上限与参考码率 ----
        check(EmbeddedVideoBudget.MaximumBytes == int.MaxValue && EmbeddedVideoBudget.MaximumBytes == (1L << 31) - 1,
            "embedded video budget: the limit is 2^31 - 1 bytes (WPE played 2,104,622,403 bytes and did not display 2,922,466,521)");
        check(Math.Abs(EmbeddedVideoBudget.ReferenceBytesPerFrame(1920d * 1080) - 279_409_688d / 9180) < 1e-6 &&
            Math.Abs(EmbeddedVideoBudget.ReferenceBytesPerFrame(3414d * 1920) - 518_554_375d / 9180) < 1e-6 &&
            Math.Abs(EmbeddedVideoBudget.ReferencePixelExponent - 0.5373) < 1e-3,
            "embedded video budget: the reference bitrate passes through both measured sizes of 3572877776 with a pixel exponent of about 0.537");
        check(EmbeddedVideoBudget.EncodedPixels(1920, 1080, true) == 2 * EmbeddedVideoBudget.EncodedPixels(1920, 1080, false) &&
            EmbeddedVideoBudget.ReferenceBytesPerFrame(3840d * 2160) > EmbeddedVideoBudget.ReferenceBytesPerFrame(3414d * 1920),
            "embedded video budget: transparent groups double the encoded width and more pixels never lower the reference bytes per frame");

        double perFrame = EmbeddedVideoBudget.ReferenceBytesPerFrame(3840d * 2160);
        // 硬件档位预判：参考码率 × 1.555 刚好不超的帧数仍用硬件，多一帧就改软件。
        ulong hardwareFrames = (ulong)Math.Floor(EmbeddedVideoBudget.MaximumBytes / (perFrame * 1.555));
        ulong hevcFrames = (ulong)Math.Floor(EmbeddedVideoBudget.MaximumBytes / (perFrame * 1.12));
        check(!EmbeddedVideoBudget.HardwareOverBudget(hardwareFrames, 3840, 2160, false, hevc: false) &&
            EmbeddedVideoBudget.HardwareOverBudget(hardwareFrames + 1, 3840, 2160, false, hevc: false) &&
            !EmbeddedVideoBudget.HardwareOverBudget(hevcFrames, 3840, 2160, false, hevc: true) &&
            EmbeddedVideoBudget.HardwareOverBudget(hevcFrames + 1, 3840, 2160, false, hevc: true),
            "embedded video budget: hardware falls back to software exactly when reference bytes x 1.555 (H.264) or x 1.12 (HEVC) exceed the limit");

        // ---- 试编码外推 ----
        EmbeddedVideoBudget.VideoPacket[] Packets(long key, long inter, int count) =>
            [new(key, true), .. Enumerable.Repeat(new EmbeddedVideoBudget.VideoPacket(inter, false), count - 1)];
        EmbeddedVideoBudget.VideoPacket[] simple = Packets(100_000, 1_000, 48);
        check(EmbeddedVideoBudget.ExtrapolateBytes(simple, 250) == 100_000 + 249 * 1_000 &&
            EmbeddedVideoBudget.ExtrapolateBytes(simple, 251) == 2 * 100_000 + 249 * 1_000 &&
            EmbeddedVideoBudget.ExtrapolateBytes(simple, 48) == 100_000 + 47 * 1_000 && EmbeddedVideoBudget.ExtrapolateBytes(simple, 0) == 0 &&
            EmbeddedVideoBudget.ExtrapolateBytes([new(5_000, true), new(7_000, true)], 10) == 70_000 &&
            EmbeddedVideoBudget.ExtrapolateBytes([new(9_000, false), new(3_000, false)], 250) == 9_000 + 249 * 6_000,
            "trial extrapolation: one largest key frame per 250 frames plus the mean non-key packet; all-key or keyless trials fall back sensibly");
        bool threw = false;
        try { EmbeddedVideoBudget.ExtrapolateBytes([], 10); } catch (InvalidDataException) { threw = true; }
        check(threw, "trial extrapolation: an empty trial is rejected instead of extrapolating nothing");
        ulong simpleMaximum = EmbeddedVideoBudget.ExtrapolatedMaximumFrames(simple);
        check(EmbeddedVideoBudget.ExtrapolateBytes(simple, simpleMaximum) <= EmbeddedVideoBudget.MaximumBytes &&
            EmbeddedVideoBudget.ExtrapolateBytes(simple, simpleMaximum + 1) > EmbeddedVideoBudget.MaximumBytes,
            "trial extrapolation: the maximum frame count is exactly the last one that stays within the limit");

        // 阿米娅 1080p 2500 s（150000 帧）：试编码 48 帧里关键帧 128,190 字节、其余平均 14,565 字节；成品实测 2,922,466,521 字节。
        // 3572877776 3414×1920（9180 帧）：关键帧 1,352,302、其余平均 44,688；成品 518,554,375 字节。
        EmbeddedVideoBudget.VideoPacket[] amiyaTrial = Packets(128_190, 14_565, 48);
        EmbeddedVideoBudget.VideoPacket[] clockTrial = Packets(1_352_302, 44_688, 48);
        JsonObject over = EmbeddedVideoBudgetJson.EvaluateProbe([new("group-1", false, 1920, 1080, 48, 814_173, amiyaTrial)], 150_000, 60, 1);
        JsonObject within = EmbeddedVideoBudgetJson.EvaluateProbe([new("group-1", false, 3414, 1920, 48, 3_454_128, clockTrial)], 9_180, 60, 1);
        JsonObject overGroup = over["groups"]![0]!.AsObject();
        double amiyaPredicted = overGroup["predicted_bytes"]!.GetValue<long>();
        check(over["status"]!.GetValue<string>() == "predicted_over_limit" && overGroup["over_limit"]!.GetValue<bool>() &&
            amiyaPredicted > EmbeddedVideoBudget.MaximumBytes && amiyaPredicted < 2_922_466_521 &&
            overGroup["maximum_seconds"]!.GetValue<double>() == 2382 && overGroup["key_frame_bytes"]!.GetValue<long>() == 128_190 &&
            within["status"]!.GetValue<string>() == "within_limit" && within["reason"] is null &&
            within["groups"]![0]!["predicted_bytes"]!.GetValue<long>() < 518_554_375,
            "trial extrapolation: Amiya's 2500 s bake is rejected before rendering while 3572877776's 9180 frames pass, both extrapolations below the real size");
        JsonObject overLocalized = over["reason_localized"]!.AsObject();
        check(overLocalized["key"]!.GetValue<string>() == "bake.embedded_video_size_predicted" &&
            over["reason_localized"]?["key"]?.GetValue<string>() == "bake.embedded_video_size_predicted",
            "trial extrapolation: the pre-render rejection names the group, length, estimated size and the longest loop at this bitrate in both languages");
        JsonObject mixed = EmbeddedVideoBudgetJson.EvaluateProbe([new("group-1", false, 1920, 1080, 48, 1, simple),
            new("group-2", true, 3840, 1080, 48, 814_173, amiyaTrial)], 150_000, 60, 1);
        JsonObject none = EmbeddedVideoBudgetJson.EvaluateProbe([], 150_000, 60, 1, new Message("bake.probe_all_static"));
        check(mixed["status"]!.GetValue<string>() == "predicted_over_limit" && mixed["groups"]!.AsArray().Count == 2 &&
            !mixed["groups"]![0]!["over_limit"]!.GetValue<bool>() && mixed["groups"]![1]!["over_limit"]!.GetValue<bool>() &&
            none["status"]!.GetValue<string>() == "not_estimated" &&
            none["reason_localized"]?["key"]?.GetValue<string>() == "bake.probe_all_static",
            "trial extrapolation: any over-limit group rejects and is named; with no encoded trial the estimate is recorded as not_estimated without rejecting");

        // ---- 编码后兜底的文案 ----
        JsonObject encoded = EmbeddedVideoBudgetJson.EncodedRejection("group-1", 2_922_466_521, 150_000, 60, 1).Localized();
        check(encoded["key"]!.GetValue<string>() == "bake.embedded_video_size_rejected",
            "encoded size check: Amiya's real 2.72 GiB video is rejected with the longest loop its actual bitrate allows");
        // 被拒报告里读出"最长能做多少秒"：超限自动重烘（HybridBakeService.BakeAsync）拿它当循环上限
        double? predictedCap = EmbeddedVideoBudgetJson.MaximumSeconds(new JsonObject {
            ["status"] = EmbeddedVideoBudget.RejectedBakeStatus, ["reason_localized"] = mixed["reason_localized"]!.DeepClone() });
        double? encodedCap = EmbeddedVideoBudgetJson.MaximumSeconds(new JsonObject {
            ["status"] = EmbeddedVideoBudget.RejectedBakeStatus, ["reason_localized"] = encoded.DeepClone() });
        check(predictedCap == mixed["groups"]![1]!["maximum_seconds"]!.GetValue<double>() && encodedCap == 1837 &&
            EmbeddedVideoBudgetJson.MaximumSeconds(new JsonObject { ["status"] = "candidate_generated", ["reason_localized"] = encoded.DeepClone() }) is null,
            "over-limit retry: the longest loop the rejected bitrate allows is read from either rejection message, and only for this rejection status");
        check(EmbeddedVideoBudget.RejectedBakeStatus == "candidate_rejected_embedded_video_size",
            "embedded video budget: pre-render and post-encode rejections share one bake status the app recognizes");
    }
}
