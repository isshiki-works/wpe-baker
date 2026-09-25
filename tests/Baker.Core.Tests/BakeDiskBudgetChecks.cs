using System.Text.Json.Nodes;
using Baker.Core;

/// <summary>
/// 开烘前的磁盘闸门：峰值预估的组成、空间不足时的拒绝记录，以及结束后中间产物的清理范围。
/// </summary>
internal static class BakeDiskBudgetChecks
{
    private static JsonObject Plan(ulong frames) => new()
    {
        ["settings"] = new JsonObject { ["width"] = 1920, ["height"] = 1080 },
        ["video_groups"] = new JsonArray(
            new JsonObject { ["id"] = "group-1", ["transparent"] = false },
            new JsonObject { ["id"] = "group-2", ["transparent"] = true }),
        ["loop"] = new JsonObject { ["candidates"] = new JsonArray(new JsonObject { ["frames"] = frames }) }
    };

    internal static void Run(Action<bool, string> check, string root)
    {
        ArgumentNullException.ThrowIfNull(check);

        // ---- ① 峰值预估：所有档位直编、不落 master；各组按自己录的帧数 ----
        const ulong frames = 1000;
        double opaquePixels = EmbeddedVideoBudget.EncodedPixels(1920, 1080, false);
        double packedPixels = EmbeddedVideoBudget.EncodedPixels(1920, 1080, true);
        ulong packedMaster = BakeDiskBudget.MasterBytes(frames, packedPixels);
        // 残差组拼接头段时多一份自己的成品；没有残差组就没有；group_frames 里的组（#131 雾组）只按自己录的帧数算成品。
        JsonObject periods = Plan(frames);
        periods["loop"]!["candidates"]![0]!["group_frames"] = new JsonObject { ["group-2"] = 100 };
        BakeDiskBudget.Estimate residual = BakeDiskBudget.EstimatePeak(periods, frames, [1]);
        double packedPlayback = 100 * EmbeddedVideoBudget.ReferenceBytesPerFrame(packedPixels);
        check(residual.Known && residual.Groups == 2 && residual.Frames == frames && residual.IntermediateBytes < packedMaster &&
            Math.Abs((long)residual.CrossfadeBytes - (long)Math.Ceiling(packedPlayback)) <= 1 &&
            Math.Abs((long)residual.PlaybackBytes - (long)Math.Ceiling(frames * EmbeddedVideoBudget.ReferenceBytesPerFrame(opaquePixels) +
                packedPlayback)) <= 2 &&
            residual.RequiredBytes == residual.PeakBytes + BakeDiskBudget.ReserveBytes &&
            BakeDiskBudget.EstimatePeak(periods, frames, []).CrossfadeBytes == 0,
            "no master is written, each group is estimated at its own recorded frame count and only residual groups add a spliced copy");

        // 起点搜索样本：3803167460 实测两个透明残差组（P = 21780，gcd 步长 4）各 9,634,775,040 字节，是整案峰值的全部。
        JsonObject search = Plan(21_780);
        search["settings"]!["fps_numerator"] = 60;
        search["settings"]!["fps_denominator"] = 1;
        check(BakeDiskBudget.EstimatePeak(search, 21_780, [1]).StartSearchBytes == 10_890UL * 512 * 288 * 3 * 2,
            "start-search thumbnails over two periods at stride gcd(P, 16) match the measured sample file");

        // 尺寸或视频组未知的计划不给预估，调用方也就不拦截。
        var unknown = new JsonObject { ["settings"] = new JsonObject { ["width"] = 0, ["height"] = 0 }, ["video_groups"] = new JsonArray() };
        check(!BakeDiskBudget.EstimatePeak(unknown, frames, []).Known && BakeDiskBudget.Reject(unknown, frames, [], root) is null,
            "an unknown output size or group list produces no estimate and no rejection");

        // ---- ② 空间不足时的拒绝记录带中英文案 ----
        JsonObject? rejection = BakeDiskBudget.Reject(Plan(50_000_000), 50_000_000, [1], root);
        check(rejection?["status"]?.GetValue<string>() == BakeDiskBudget.RejectedBakeStatus &&
            rejection["required_bytes"] is not null && rejection["available_bytes"] is not null &&
            rejection["estimate"]?["peak_bytes"] is not null,
            "a bake that cannot fit is rejected with the required and available space in both languages");

        // ---- ③ 结束后按 WorkLayout 登记表清理 ----
        string output = Path.Combine(root, "disk-budget-cleanup");
        string[] removed = [Path.Combine(output, "capture-source"), Path.Combine(output, "reference"),
            Path.Combine(output, "group-1", "master"), Path.Combine(output, "group-1", "master.gpu-unavailable"),
            Path.Combine(output, "group-1", "master.h264_vulkan-qp18"), Path.Combine(output, "group-1", "master.av1_nvenc-failed"),
            Path.Combine(output, "group-1", "capture-bounds"), Path.Combine(output, "group-1.start-search"),
            Path.Combine(output, "prefix-7", "capture-source"),
            output + ".composition-probe", output + ".composition-reference", output + ".analysis-refresh"];
        // 成品工程是原作解包，里面恰好有叫 master 的文件夹也不能动；编码成品、硬解日志、合成比对结果都留着。
        string[] kept = [Path.Combine(output, "project", "master"), Path.Combine(output, "group-1", "encoded"),
            Path.Combine(output, "group-1", "hardware-decode"), Path.Combine(output, "prefix-7", "encoded"),
            Path.Combine(output, "composition-validation"), output + ".composition-validation"];
        foreach (string directory in removed.Concat(kept))
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "content.bin"), "x");
        }
        File.WriteAllText(Path.Combine(output, "group-1", "seam-preview.mp4"), "x");
        File.WriteAllText(Path.Combine(output, "bake.json"), "{}");
        JsonArray? errors = new WorkLayout(output + Path.DirectorySeparatorChar).RemoveIntermediates(
            WorkLayout.KeepsCompositionProbe(new JsonObject { ["status"] = "candidate_generated" }));
        check(errors is null && removed.All(directory => !Directory.Exists(directory)) && kept.All(Directory.Exists) &&
            File.Exists(Path.Combine(output, "bake.json")) && File.Exists(Path.Combine(output, "group-1", "seam-preview.mp4")),
            "finishing a bake removes every registered intermediate but keeps the project, report, videos and diagnostics");

        // 合成校验被拒的报告靠 probe_paths 指路，这时探针与参照（含效果前缀路线的原作参照）保留。
        string probeOutput = Path.Combine(root, "disk-budget-probe-kept");
        Directory.CreateDirectory(Path.Combine(probeOutput, "capture-source"));
        Directory.CreateDirectory(Path.Combine(probeOutput, "reference"));
        Directory.CreateDirectory(probeOutput + ".composition-probe");
        Directory.CreateDirectory(probeOutput + ".composition-reference");
        new WorkLayout(probeOutput).RemoveIntermediates(WorkLayout.KeepsCompositionProbe(new JsonObject { ["probe_paths"] = new JsonObject() }));
        check(!Directory.Exists(Path.Combine(probeOutput, "capture-source")) && Directory.Exists(Path.Combine(probeOutput, "reference")) &&
            Directory.Exists(probeOutput + ".composition-probe") && Directory.Exists(probeOutput + ".composition-reference"),
            "a composition rejection keeps the probe and reference its report points at");
        check(!WorkLayout.KeepsCompositionProbe(null) &&
            WorkLayout.KeepsCompositionProbe(new JsonObject { ["status"] = "candidate_rejected_composition" }),
            "only composition rejections keep the probe");
    }
}
