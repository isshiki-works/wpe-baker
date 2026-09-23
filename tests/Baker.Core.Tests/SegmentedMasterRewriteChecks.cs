using Baker.Core;

/// <summary>
/// 2026-09-16 的整机假死：为了在接缝处淡化 24 帧，代码把 4,047,247,799 字节、6024 帧的无损 master
/// 整片解码再整片重编码，内存和磁盘同时被吃光。这里把「大 master 只许分段改写」钉成硬约束。
/// </summary>
internal static class SegmentedMasterRewriteChecks
{
    internal static async Task RunAsync(Action<bool, string> check, string root)
    {
        string directory = Path.Combine(root, "segmented-master-rewrite");
        Directory.CreateDirectory(directory);
        string small = Path.Combine(directory, "small.mp4");
        File.WriteAllBytes(small, new byte[1024]);
        // 稀疏地撑到刚过闸门大小，不实际写 512 MiB 数据。
        string large = Path.Combine(directory, "large.mp4");
        using (var stream = new FileStream(large, FileMode.CreateNew, FileAccess.Write))
            stream.SetLength(MasterRewrite.SegmentedRewriteMinimumBytes + 1);
        try
        {
            static string? Rejected(string video, ulong total, ulong reencoded)
            {
                try { MasterRewrite.RequireSegmentedMasterRewrite(video, total, reencoded, "接缝交叉淡化"); }
                catch (InvalidOperationException error) { return error.Message; }
                return null;
            }

            check(Rejected(small, 6024, 6024) is null,
                "小于闸门的 master 不受分段约束，短片仍可整片重编码");
            check(Rejected(large, 6024, 250) is null,
                "大 master 上只重编码接缝所在的 GOP 是允许的");
            check(Rejected(large, 6024, MasterRewrite.MaximumRewriteReencodedFrames) is null,
                "分段上限本身是允许值，不是越界值");

            // 事故当时的实际形状：6024 帧的 master 上重编码 6000 帧。
            string? accident = Rejected(large, 6024, 6000);
            check(accident is not null,
                "事故当时的重编码规模（6024 帧里重编 6000 帧）被拒并指向分段路径");
            string? full = Rejected(large, 6024, 6024);
            check(full is not null,
                "大 master 上的全长重编码被单独报成全长重编码");
            check(Rejected(large, 6024, MasterRewrite.MaximumRewriteReencodedFrames + 1) is not null,
                "超过分段上限一帧也要拒，闸门不是估算");

            // 强制关键帧是分段改写的前提，帧号必须真的落在这次编码的序列里。
            string source = Path.Combine(directory, "source");
            Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(Path.Combine(source, "project.json"), "{\"type\":\"scene\",\"file\":\"scene.json\"}");
            await File.WriteAllTextAsync(Path.Combine(source, "scene.json"), "{\"objects\":[]}");
            string fakeTool = Path.Combine(source, "not-an-executable.exe");
            await File.WriteAllTextAsync(fakeTool, "must not be launched");
            var runner = new NativeRenderRunner(new NativeTools(fakeTool, fakeTool, fakeTool, []));
            async Task<bool> RejectsAsync(ulong? forced, ulong frames)
            {
                string output = Path.Combine(directory, $"forced-{forced?.ToString() ?? "none"}-{frames}");
                try
                {
                    await runner.RenderAsync(new(source, source, output, 2, 2, 60, 1, frames, ForceKeyFrameFrame: forced));
                }
                catch (ArgumentException)
                {
                    return !Directory.Exists(output);
                }
                catch (Exception) { return false; }
                return false;
            }
            check(await RejectsAsync(24, 24), "强制关键帧帧号不能等于总帧数");
            check(await RejectsAsync(0, 24), "强制关键帧帧号不能是第 0 帧，那一帧本来就是 IDR");
        }
        finally
        {
            try { File.Delete(large); } catch (IOException) { }
        }
    }
}
