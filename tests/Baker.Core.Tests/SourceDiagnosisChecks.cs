using System.Text.Json.Nodes;
using Baker.Core;

/// <summary>
/// 首次使用会撞到的来源判定：空文件夹、随手拖进来的文件、视频/网页壁纸、预设包、只有 scene.pkg 的 Scene。
/// 全部用真文件，不打桩。每条都同时查"分类对不对"和"给的那句话是不是中文/是不是点到了名"。
/// </summary>
internal static class SourceDiagnosisChecks
{
    public static void Run(Action<bool, string> Check, string root)
    {
        string home = Path.Combine(root, "source-diagnosis");
        Directory.CreateDirectory(home);

        string New(string name)
        {
            string path = Path.Combine(home, name);
            Directory.CreateDirectory(path);
            return path;
        }

        // ---- 路径根本不存在 ----
        var missing = SourceDiagnosis.Inspect(Path.Combine(home, "no-such-folder"));
        Check(missing is { Kind: "missing" }, "a path that does not exist is reported as missing");
        Check(missing!.Text(MessageCatalog.Chinese).Contains("no-such-folder", StringComparison.Ordinal),
            "the missing-path message is chinese and names the path");

        // ---- 空文件夹：以前一律报 "Wallpaper source does not exist."，用户明明选中了它 ----
        string empty = New("empty");
        var emptyResult = SourceDiagnosis.Inspect(empty);
        Check(emptyResult is { Kind: "empty_folder" }, "a folder with no wallpaper entry is reported separately from a missing path");

        // ---- 拖进来一个不相干的文件 ----
        string stray = Path.Combine(home, "clip.mp4");
        File.WriteAllBytes(stray, [0, 1, 2]);
        var strayResult = SourceDiagnosis.Inspect(stray);
        Check(strayResult is { Kind: "not_wallpaper_file" }, "an unrelated file is rejected as not a wallpaper source");
        Check(strayResult!.Text(MessageCatalog.Chinese).Contains("clip.mp4", StringComparison.Ordinal),
            "the stray-file message names the file that was dropped");

        // ---- 视频壁纸 / 网页壁纸：各说各的，并点名 type 与内容文件 ----
        foreach (var (kind, entry, key) in new[] { ("video", "clip.mp4", "source.video_wallpaper"), ("web", "index.html", "source.web_wallpaper") })
        {
            string folder = New(kind);
            File.WriteAllText(Path.Combine(folder, "project.json"), $$"""{"type":"{{kind}}","file":"{{entry}}"}""");
            File.WriteAllText(Path.Combine(folder, entry), "unchanged");
            var result = SourceDiagnosis.Inspect(folder);
            Check(result is not null && result.Kind == kind && result.Key == key && result.Unsupported,
                kind + " wallpaper is classified by its own kind as an unsupported source");
            string legacy = result!.Message.Text;
            Check(legacy.Contains("type=" + kind, StringComparison.Ordinal) && legacy.Contains(entry, StringComparison.Ordinal),
                kind + " legacy english names the type and the content file");
        }

        // ---- project.json 没写 file 时不该留下半句"内容是" ----
        string noFile = New("video-no-file");
        File.WriteAllText(Path.Combine(noFile, "project.json"), """{"type":"video"}""");
        var noFileResult = SourceDiagnosis.Inspect(noFile);
        Check(noFileResult is { Kind: "video" },
            "a video wallpaper without a file entry omits the content clause in both languages");

        // ---- 预设包：单独的分类与 dependency，指路到它依赖的作品 ----
        string preset = New("preset");
        File.WriteAllText(Path.Combine(preset, "project.json"), """{"dependency":"3172471800","alignment":"center"}""");
        var presetResult = SourceDiagnosis.Inspect(preset);
        Check(presetResult is { Kind: SourceDiagnosis.PresetKind, Dependency: "3172471800", Unsupported: true },
            "a preset package is classified apart from video and web and keeps its dependency");
        Check(presetResult!.Text(MessageCatalog.Chinese).Contains(@"431960\3172471800", StringComparison.Ordinal) &&
            presetResult.Text(MessageCatalog.English).Contains(@"431960\3172471800", StringComparison.Ordinal),
            "both languages point at the wallpaper the preset depends on");

        // ---- 别的 type ----
        string other = New("other");
        File.WriteAllText(Path.Combine(other, "project.json"), """{"type":"application","file":"a.exe"}""");
        Check(SourceDiagnosis.Inspect(other) is { Kind: "other_type", Key: "source.not_scene_project", Unsupported: true },
            "an unknown project type falls back to the generic not-a-scene rejection");

        // ---- Scene 缺 objects ----
        string broken = New("broken-scene");
        File.WriteAllText(Path.Combine(broken, "project.json"), """{"type":"scene","file":"scene.json"}""");
        File.WriteAllText(Path.Combine(broken, "scene.json"), """{"camera":{}}""");
        Check(SourceDiagnosis.Inspect(broken) is { Kind: "no_objects", Unsupported: false },
            "a scene json without an objects array is rejected on its own terms, as an error rather than an unsupported source");

        // ---- 能用的 Scene：返回 null，并把路径规范化 ----
        string scene = New("scene");
        File.WriteAllText(Path.Combine(scene, "project.json"), """{"type":"scene","file":"scene.json"}""");
        File.WriteAllText(Path.Combine(scene, "scene.json"), """{"objects":[]}""");
        Check(SourceDiagnosis.Inspect(scene, out string resolvedScene) is null && resolvedScene == Path.GetFullPath(scene),
            "a usable scene folder is accepted and its path is returned fully qualified");
        Check(SourceDiagnosis.Inspect(Path.Combine(scene, "scene.json")) is null, "the scene json inside a scene folder is accepted directly");

        // ---- 只放了 scene.pkg 的作品：用户手里的 scene.json 路径也要认（以前 CLI 报"来源不存在"）----
        string packaged = New("packaged");
        WritePackage(Path.Combine(packaged, "scene.pkg"), """{"objects":[]}""");
        Check(SourceDiagnosis.Inspect(packaged, out string resolvedPackage) is null &&
            resolvedPackage == Path.GetFullPath(packaged),
            "a folder that ships only scene.pkg is accepted and stays a folder path");
        Check(SourceDiagnosis.Inspect(Path.Combine(packaged, "scene.json"), out string swapped) is null &&
            swapped.EndsWith("scene.pkg", StringComparison.OrdinalIgnoreCase),
            "a scene.json path is answered by the scene.pkg beside it instead of 'source does not exist'");
        using (var opened = new ProjectSource(Path.Combine(packaged, "scene.json")))
            Check(opened.Kind == "scene" && opened.SourcePath.EndsWith("scene.pkg", StringComparison.OrdinalIgnoreCase),
                "ProjectSource itself also falls back from scene.json to the scene.pkg beside it");

        // ---- 拖放接受范围：存在即收，由导入给理由 ----
        Check(SourceDiagnosis.LooksLikeSource(scene) && !SourceDiagnosis.LooksLikeSource(empty) &&
            !SourceDiagnosis.LooksLikeSource(stray) && !SourceDiagnosis.LooksLikeSource(""),
            "LooksLikeSource only accepts paths shaped like a wallpaper source");

        // ---- 自检文案：缺件时点名缺的是哪一个、在哪个路径 ----
        string bare = New("no-tools");
        string toolsConfig = Path.Combine(bare, "tools.json");
        File.WriteAllText(toolsConfig, """{"renderer":"renderer/wpe-render.exe","ffmpeg":"encoder/ffmpeg.exe","ffprobe":"encoder/ffprobe.exe"}""");
        bool named = false, translated = false;
        try { _ = NativeEnvironmentProbe(toolsConfig); }
        catch (FileNotFoundException error)
        {
            named = error.Message.Contains("wpe-render.exe", StringComparison.Ordinal) &&
                error.Message.Contains(bare, StringComparison.Ordinal);
            // 界面显示前要能换成中文：Core 抛英文原文，异常带着键与参数，GUI 按它重新渲染。
            JsonObject localized = Message.Of(error)!.Localized();
            translated = localized["key"]?.GetValue<string>() == "setup.tool_file_missing" &&
                localized[MessageCatalog.Chinese]?.GetValue<string>() is string zh &&
                zh.Contains(bare, StringComparison.Ordinal);
        }
        Check(named, "a missing bundled tool is reported by name and full path, not just 'Required tool is missing'");
        Check(translated, "the missing-tool error carries its message key, which renders chinese with the same tool name and path");

        // ---- 临时目录清理：过期的删掉，新的留着（在测试自己的目录里跑，不动本机 %TEMP%）----
        string scratch = New("scratch");
        string stale = Path.Combine(scratch, "analysis-" + Guid.NewGuid().ToString("N"));
        string fresh = Path.Combine(scratch, "analysis-" + Guid.NewGuid().ToString("N"));
        string unrelated = Path.Combine(scratch, "keep-me");
        Directory.CreateDirectory(stale);
        Directory.CreateDirectory(fresh);
        Directory.CreateDirectory(unrelated);
        File.WriteAllText(Path.Combine(stale, "trace.json"), "{}");
        Directory.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-30));
        Directory.SetLastWriteTimeUtc(unrelated, DateTime.UtcNow.AddDays(-30));
        NativeEnvironment.PruneAnalysisScratch(scratch, 7);
        Check(!Directory.Exists(stale) && Directory.Exists(fresh) && Directory.Exists(unrelated),
            "startup pruning removes stale analysis scratch directories, keeps recent ones and never touches other folders");
    }

    /// <summary>在一个只有 tools.json 的目录里解析工具，用来观察缺件报错。</summary>
    private static NativeTools NativeEnvironmentProbe(string toolsConfig)
    {
        string previous = Environment.CurrentDirectory;
        Environment.CurrentDirectory = Path.GetDirectoryName(toolsConfig)!;
        try { return NativeEnvironment.FindTools(); }
        finally { Environment.CurrentDirectory = previous; }
    }

    /// <summary>最小的 PKGV 容器，只放一个 scene.json 条目。</summary>
    private static void WritePackage(string path, string sceneJson)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        void WriteString(string value)
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(value);
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }
        WriteString("PKGV0017");
        writer.Write(1);
        byte[] payload = System.Text.Encoding.UTF8.GetBytes(sceneJson);
        WriteString("scene.json");
        writer.Write(0);
        writer.Write(payload.Length);
        writer.Write(payload);
    }
}
