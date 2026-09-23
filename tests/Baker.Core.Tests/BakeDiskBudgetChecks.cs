using System.Text.Json.Nodes;
using Baker.Core;

/// <summary>
/// 开烘前的磁盘闸门：峰值预估的组成、空间不足时的拒绝记录，以及结束后中间产物的清理范围。
/// </summary>
internal static class BakeDiskBudgetChecks
{
    private static JsonObject Plan(ulong frames, bool unresolved) => new()
    {
        ["settings"] = new JsonObject { ["width"] = 1920, ["height"] = 1080 },
        ["video_groups"] = new JsonArray(
            new JsonObject { ["id"] = "group-1", ["transparent"] = false },
            new JsonObject { ["id"] = "group-2", ["transparent"] = true }),
        ["loop"] = new JsonObject
        {
            ["candidates"] = new JsonArray(new JsonObject { ["frames"] = frames }),
            ["unresolved"] = unresolved ? new JsonArray(new JsonObject { ["detail"] = "residual" }) : new JsonArray()
        }
    };

    internal static void Run(Action<bool, string> check, string root)
    {
        ArgumentNullException.ThrowIfNull(check);

        // ---- ① 峰值预估：同时在飞的 master + 全部成品 + 一份淡化副本 ----
        const ulong frames = 1000;
        double opaquePixels = EmbeddedVideoBudget.EncodedPixels(1920, 1080, false);
        double packedPixels = EmbeddedVideoBudget.EncodedPixels(1920, 1080, true);
        ulong opaqueMaster = (ulong)(frames * opaquePixels * 3 / BakeDiskBudget.MasterCompressionRatio);
        ulong packedMaster = (ulong)(frames * packedPixels * 3 / BakeDiskBudget.MasterCompressionRatio);
        ulong playback = (ulong)Math.Ceiling(frames * EmbeddedVideoBudget.ReferenceBytesPerFrame(opaquePixels) +
            frames * EmbeddedVideoBudget.ReferenceBytesPerFrame(packedPixels));
        BakeDiskBudget.Estimate residual = BakeDiskBudget.EstimatePeak(Plan(frames, unresolved: true), frames, 1);
        check(residual.Known && residual.Groups == 2 && residual.Frames == frames &&
            residual.MasterBytes == opaqueMaster + packedMaster &&
            residual.CrossfadeBytes == packedMaster &&
            Math.Abs((long)residual.PlaybackBytes - (long)playback) <= 2 &&
            residual.RequiredBytes == residual.PeakBytes + BakeDiskBudget.ReserveBytes,
            "the disk estimate adds the masters in flight, every playback video and one crossfade copy");

        // 没有未解析分量就没有淡化副本；组并行更高时同时在飞的 master 更多（这里已封顶到两个组）。
        BakeDiskBudget.Estimate plain = BakeDiskBudget.EstimatePeak(Plan(frames, unresolved: false), frames, 1);
        check(plain.CrossfadeBytes == 0 && plain.PeakBytes < residual.PeakBytes &&
            BakeDiskBudget.EstimatePeak(Plan(frames, unresolved: false), frames, 4).MasterBytes == plain.MasterBytes,
            "a source-period plan carries no crossfade copy and the masters in flight are capped by the group count");

        // 尺寸或视频组未知的计划不给预估，调用方也就不拦截。
        var unknown = new JsonObject { ["settings"] = new JsonObject { ["width"] = 0, ["height"] = 0 }, ["video_groups"] = new JsonArray() };
        check(!BakeDiskBudget.EstimatePeak(unknown, frames, 1).Known && BakeDiskBudget.Reject(unknown, frames, 1, root) is null,
            "an unknown output size or group list produces no estimate and no rejection");

        // ---- ② 空间不足时的拒绝记录带中英文案 ----
        JsonObject? rejection = BakeDiskBudget.Reject(Plan(50_000_000, unresolved: true), 50_000_000, 1, root);
        check(rejection?["status"]?.GetValue<string>() == BakeDiskBudget.RejectedBakeStatus &&
            rejection["reason_localized"]?["zh"]?.GetValue<string>() is string zh && zh.Contains("GiB", StringComparison.Ordinal) &&
            rejection["reason_localized"]?["en"]?.GetValue<string>() is string en && en.Contains("free", StringComparison.Ordinal) &&
            rejection["required_bytes"] is not null && rejection["available_bytes"] is not null &&
            rejection["estimate"]?["peak_bytes"] is not null,
            "a bake that cannot fit is rejected with the required and available space in both languages");

        // ---- ③ 结束后按 WorkLayout 登记表清理 ----
        string output = Path.Combine(root, "disk-budget-cleanup");
        string[] removed = [Path.Combine(output, "capture-source"), Path.Combine(output, "reference"),
            Path.Combine(output, "group-1", "master"), Path.Combine(output, "group-1", "master.gpu-unavailable"),
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
