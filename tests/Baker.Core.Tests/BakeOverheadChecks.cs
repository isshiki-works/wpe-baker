using Baker.Core;

/// <summary>C-PERF-BAKE：烘焙固定开销的几处提速，只查语义不变与记忆真的生效。</summary>
internal static class BakeOverheadChecks
{
    internal static async Task RunAsync(Action<bool, string> check, string root)
    {
        // P1-4：源树哈希按 (路径, 长度, mtime) 记忆。同长度改内容并还原 mtime 时，记忆版仍给旧值（证明中间检查只比指纹），
        // 完整版给新值并刷新记忆；长度一变记忆版也重算。把记忆去掉，第一条就失败。
        string project = Path.Combine(root, "project");
        Directory.CreateDirectory(project);
        await File.WriteAllTextAsync(Path.Combine(project, "project.json"), """{"type":"scene","file":"scene.json"}""");
        await File.WriteAllTextAsync(Path.Combine(project, "scene.json"), """{"objects":[]}""");
        string asset = Path.Combine(project, "video.mp4");
        await File.WriteAllBytesAsync(asset, [1, 2, 3, 4]);
        using var source = new ProjectSource(project);
        string first = await source.SourceHashAsync();
        DateTime stamp = File.GetLastWriteTimeUtc(asset);
        await File.WriteAllBytesAsync(asset, [9, 9, 9, 9]);
        File.SetLastWriteTimeUtc(asset, stamp);
        string reused = await source.SourceHashAsync(reuse: true);
        string full = await source.SourceHashAsync();
        check(reused == first && full != first && await source.SourceHashAsync(reuse: true) == full,
            "within a bake the source hash is reused while every file keeps its length and mtime; a full hash still reads the bytes");
        await File.WriteAllBytesAsync(asset, [9, 9, 9, 9, 9]);
        File.SetLastWriteTimeUtc(asset, stamp);
        check(await source.SourceHashAsync(reuse: true) != full, "a changed file length invalidates the remembered source hash");

        // 渲染器 exe 的哈希同样按 (长度, mtime) 记住；长度变了重算。
        string renderer = Path.Combine(root, "renderer.exe");
        await File.WriteAllBytesAsync(renderer, [1, 2, 3]);
        string exe = await NativeRenderRunner.RendererHashAsync(renderer, CancellationToken.None);
        DateTime exeStamp = File.GetLastWriteTimeUtc(renderer);
        await File.WriteAllBytesAsync(renderer, [3, 2, 1]);
        File.SetLastWriteTimeUtc(renderer, exeStamp);
        bool remembered = await NativeRenderRunner.RendererHashAsync(renderer, CancellationToken.None) == exe;
        await File.WriteAllBytesAsync(renderer, [3, 2, 1, 0]);
        check(remembered && await NativeRenderRunner.RendererHashAsync(renderer, CancellationToken.None) != exe,
            "the renderer executable hash is computed once per unchanged file and recomputed when it changes");

        // P2-1：中间副本里 1 MiB 以上的二进制松散文件走硬链接（原作就地改一个字节，副本跟着变）；.json、shaders/ 与小文件照旧拷贝，
        // 副本就地重写它们不会写回原作。不带 link 的解包全是拷贝。去掉硬链接，第一条就失败。
        byte[] large = new byte[(1 << 20) + 7];
        await File.WriteAllBytesAsync(Path.Combine(project, "video.mp4"), large);
        Directory.CreateDirectory(Path.Combine(project, "shaders"));
        await File.WriteAllBytesAsync(Path.Combine(project, "shaders", "big.frag"), large);
        await File.WriteAllBytesAsync(Path.Combine(project, "big.json"), large);
        await File.WriteAllBytesAsync(Path.Combine(project, "small.tex"), [5, 6, 7]);
        using var linked = new ProjectSource(project);
        string linkedCopy = Path.Combine(root, "linked"), plainCopy = Path.Combine(root, "plain");
        await linked.ExtractAsync(linkedCopy, link: true);
        await linked.ExtractAsync(plainCopy);
        foreach (string name in new[] { "video.mp4", "shaders/big.frag", "big.json", "small.tex" })
            await using (var stream = new FileStream(Path.Combine(project, name), FileMode.Open, FileAccess.Write)) stream.WriteByte(42);
        byte First(string copy, string name) => File.ReadAllBytes(Path.Combine(copy, name))[0];
        check(First(linkedCopy, "video.mp4") == 42 && First(plainCopy, "video.mp4") == 0,
            "a linked extraction hard-links large binary resources instead of copying them");
        // P2-5：慢分量预检一次渲染留第 0 与第 P 帧，靠"只出样本的渲染可以留样本帧"。请求校验放行它（走到找渲染器才失败），
        // 留非样本帧仍拒绝。改回"只出样本就不许留原帧"时第一条失败。
        var runner = new NativeRenderRunner(new(Path.Combine(root, "missing-renderer"), Path.Combine(root, "missing-ffmpeg"),
            Path.Combine(root, "missing-ffprobe"), []));
        var closure = new RenderRequest(project, root, Path.Combine(root, "closure"), 64, 64, 60, 1, 91, WarmupFrames: 30,
            FrameSamplesOnly: true, FrameSampleStride: 90, FrameSampleWidth: 1, RetainFrames: [0, 90]);
        async Task<Type?> Failure(RenderRequest request)
        {
            try { await runner.RenderAsync(request); return null; }
            catch (Exception error) { return error.GetType(); }
        }
        check(await Failure(closure) == typeof(FileNotFoundException) &&
            await Failure(closure with { RetainFrames = [0, 45] }) == typeof(ArgumentException),
            "a sample-only render may retain full frames only at sample indices, so frames 0 and P come from one render");

        check(First(linkedCopy, "shaders/big.frag") == 0 && First(linkedCopy, "big.json") == 0 && First(linkedCopy, "small.tex") == 5,
            "shaders, JSON and small files are still copied, so rewriting them in a work copy never reaches the source");
    }
}
