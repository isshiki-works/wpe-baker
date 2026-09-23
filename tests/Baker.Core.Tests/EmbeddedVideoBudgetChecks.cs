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

        // 大小表的三档分辨率：60 fps 下参考码率装得下的整秒数；120 fps 帧数不变、秒数减半（按帧计，偏保守）。
        (uint W, uint H, double At60, double At120)[] table = [(1920, 1080, 1175, 587), (3414, 1920, 633, 316), (3840, 2160, 558, 279)];
        check(table.All(row =>
                EmbeddedVideoBudget.WholeSeconds(EmbeddedVideoBudget.ReferenceMaximumFrames(row.W, row.H, false), 60, 1) == row.At60 &&
                EmbeddedVideoBudget.WholeSeconds(EmbeddedVideoBudget.ReferenceMaximumFrames(row.W, row.H, false), 120, 1) == row.At120) &&
            EmbeddedVideoBudget.WholeSeconds(EmbeddedVideoBudget.ReferenceMaximumFrames(1920, 1080, false), 60000, 1001) == 1177,
            "embedded video budget: the reference fits 1175/633/558 s at 60 fps and 587/316/279 s at 120 fps for 1080p, 3414x1920 and 4K");
        ulong fitFrames = EmbeddedVideoBudget.ReferenceMaximumFrames(3840, 2160, false);
        double perFrame = EmbeddedVideoBudget.ReferenceBytesPerFrame(3840d * 2160);
        check(fitFrames * perFrame <= EmbeddedVideoBudget.MaximumBytes && (fitFrames + 1) * perFrame > EmbeddedVideoBudget.MaximumBytes,
            "embedded video budget: the reference frame count is the largest that stays within the limit");

        // ---- 循环长度收紧 ----
        EmbeddedVideoLoopLimit amiya = EmbeddedVideoBudget.LoopLengthLimit(3600, 1920, 1080, false, 60, 1)!;
        EmbeddedVideoLoopLimit laptop = EmbeddedVideoBudget.LoopLengthLimit(600, 3414, 1920, false, 60, 1)!;
        EmbeddedVideoLoopLimit desktop = EmbeddedVideoBudget.LoopLengthLimit(600, 3840, 2160, false, 60, 1)!;
        EmbeddedVideoLoopLimit layered = EmbeddedVideoBudget.LoopLengthLimit(600, 1920, 1080, true, 60, 1)!;
        check(amiya is { Applied: true, EffectiveSeconds: 1175, RequestedSeconds: 3600 } &&
            laptop is { Applied: false, EffectiveSeconds: 600 } && desktop is { Applied: true, EffectiveSeconds: 558 } &&
            layered is { Applied: false, EncodedWidth: 3840, PackedAlpha: true } && layered.FitSeconds < amiya.FitSeconds &&
            EmbeddedVideoBudget.LoopLengthLimit(600, 0, 0, false, 60, 1) is null,
            "embedded video limit: 1080p at 3600 s drops to 1175 s, the laptop's 600 s default fits, a 4K desktop's 600 s drops to 558 s, and an unknown size is not limited");
        JsonObject record = EmbeddedVideoBudgetJson.ToJson(desktop);
        check(record["applied"]!.GetValue<bool>() && record["requested_loop_length_maximum_seconds"]!.GetValue<double>() == 600 &&
            record["fit_seconds"]!.GetValue<double>() == 558 && record["maximum_bytes"]!.GetValue<long>() == int.MaxValue &&
            record["encoded_width"]!.GetValue<uint>() == 3840 && record["fps_numerator"]!.GetValue<uint>() == 60,
            "embedded video limit: the plan record carries the requested and fitted seconds, the limit, the encoded size and its basis");
        check(EmbeddedVideoBudgetJson.Sentence(laptop, MessageCatalog.Chinese) == "" &&
            EmbeddedVideoBudgetJson.Sentence(EmbeddedVideoBudget.LoopLengthLimit(600, 1920, 1080, false, 60000, 1001)!, MessageCatalog.English) == "",
            "embedded video limit: the sentence names the resolution, frame rate, longest and requested seconds, and is empty when nothing was lowered");

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
        check(EmbeddedVideoBudget.RejectedBakeStatus == "candidate_rejected_embedded_video_size",
            "embedded video budget: pre-render and post-encode rejections share one bake status the app recognizes");
    }
}
