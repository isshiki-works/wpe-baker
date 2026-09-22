namespace Baker.Core;

/// <summary>
/// CLI 各子命令的用法段。骨架一律是英文（与 analyze --help 同一口径），只有个别说明走 <see cref="MessageCatalog"/>
/// 按 --lang 出中文或英文；顶层帮助与 `&lt;命令&gt; --help` 都从这里取，两处永远同步。
/// </summary>
public static class CliUsage
{
    /// <summary>按语言生成全部用法段。</summary>
    public static Dictionary<string, string> Sections(string language)
    {
        string encoderNote = string.Join(Environment.NewLine,
            MessageCatalog.Get("cli.bake_encoder_help", language).Split('\n').Select(line => "  " + line.TrimEnd('\r')));
        // 并行相关的两个开关同样按 --lang 出中文或英文，骨架保持英文。
        string parallelNote = string.Join(Environment.NewLine,
            MessageCatalog.Get("cli.bake_parallel_help", language).Split('\n').Select(line => "  " + line.TrimEnd('\r')));
        // 中间产物保留开关同样按 --lang 出中文或英文，骨架保持英文。
        string keepNote = string.Join(Environment.NewLine,
            MessageCatalog.Get("cli.bake_keep_intermediates_help", language).Split('\n').Select(line => "  " + line.TrimEnd('\r')));
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["analyze"] = string.Join(Environment.NewLine,
                "wpe-baker analyze SOURCE [--out PLAN.json] [--assets DIR] [--tools TOOLS.json]",
                "  [--properties-source wpe|defaults (default wpe)] [--properties JSON_FILE_PATH]",
                "  [--keep-live on|off (default off)]  on preserves the legacy analysis path.",
                "  [--interaction keep|fixed|off (default fixed)]",
                "    keep: preserve input; fixed: fixed view; off: also disable pointer effects and sampled costly full-screen audio effects.",
                "    Clocks, dates and media text stay live. Unmeasured audio effects stay live.",
                "  [--width W --height H --fps N --fps-den D] [--device VULKAN_UUID]",
                "    Without --fps: min(Wallpaper Engine's FPS limit, the primary display's refresh rate), snapped to the",
                "      nearest of 30/60/120/144/165/240; 60 when either cannot be read. --fps-den needs --fps.",
                "    不给 --fps 时：取 min(Wallpaper Engine 的帧率上限, 主显示器刷新率) 就近落到 30/60/120/144/165/240，",
                "      任一读不到就用 60。--fps-den 要和 --fps 一起给。",
                "  [--view-mode preserve|fixed_view] [--video-layout full_frame|layered]",
                // 这三个开关名字比作用大，按 54 个候选场景的实测标注适用范围，免得用户拿它们当"关掉元素"用。
                "  [--live-overlays preserve|foreground (default foreground)]  reorders independent live text/particle trees only;",
                "    it never removes anything, and has no effect on image/model trees or framebuffer effects",
                "  [--text-effects preserve|simple]  drops fixed text effects only (no scripts, no observed input,",
                "    no framebuffer/audio/pointer/parallax layers): 10 of 54 scanned scenes had any",
                "  [--audio-effects preserve|omit]  drops fixed audio-reactive effects only: 10 of 54 scanned scenes",
                "    had any; audio-driven scripts and shaders stay live",
                "  [--exclude-layers 12,34]  the only switch that really turns an element off; it omits each listed",
                "    layer with its whole subtree, exactly as if switched off in the source. See tradeoff_options",
                "    in the plan for which elements can be traded away and what disappears with them",
                "  [--preset efficiency|balanced|quality (default balanced)]",
                "    Automatic fallback: quality -> balanced -> efficiency; layered layouts allow up to 4 videos.",
                "    Presets change retiming only; interaction is independent. Failed settings may return a verified suggested_change.",
                "  [--measure-source on|off (default off)]",
                "    efficiency: 5% look budget, loop at most 600 s; balanced: 3%, 600 s; quality: smallest change, 1200 s.",
                "    档位给的是允许多大的观感改动，循环长度是求解结果；上限只是兜底。",
                "  [--retime-budget PERCENT 0..5 (advanced override; alias --max-retime)]",
                "  [--loop-max-seconds SECONDS (advanced override, at most 3600; alias --loop-length-max)]",
                "  [--loop-preference performance|balanced|quality]  the old name for --preset:",
                "    performance = efficiency, balanced = balanced, quality = quality. Giving both names for different",
                "    presets is an error; there is no separate loop preference any more.",
                "    --loop-preference 是 --preset 的旧名，两者指向不同档位时直接报错；循环取向不再是单独的开关。",
                "  [--video-shell reject|allow]",
                "  [--sway-retime on|off (default on)]",
                "  [--daytime-split on|off (default off)]",
                "    all three presets close swaying layers on whole cycles; the look budget above governs it.",
                "    摆动改频三档都开（档位的观感预算管的就是它），只有显式 --sway-retime off 才关；界面高级区同一个默认。",
                "  [--lang zh|en]",
                // fix/mdl-lenient-strings 加的退出码表，原样保留中英两行（两种 --lang 下输出相同）。
                "  Exit codes: 0 plan written; 1 not a Scene wallpaper, or analysis failed; 3 preset package;",
                "    4 tool limitation: the renderer cannot read an asset file (a 3D model or texture), a report with",
                "      summary.verdict=tool_limitation is still written to --out; 130 cancelled.",
                "  退出码：0 已写出 plan；1 不是 Scene 壁纸或分析失败；3 预设包；4 工具局限：渲染器读不了某个素材文件",
                "    （3D 模型或纹理），--out 仍写出 summary.verdict=tool_limitation 的结论；130 已取消。"),
            ["bake"] = string.Join(Environment.NewLine,
                "wpe-baker bake PLAN.json --out NEW_DIRECTORY [--tools TOOLS.json]",
                "  [--encoder software|auto|vulkan|nvenc|qsv|amf] [--encode-slots N] [--group-parallel N] [--lang zh|en]",
                "  [--effect-resolution original|output] (default original; output matches the output and layer dimensions)",
                "  [--effect-render-scale 0..1] (greater than 0; default 1; cannot combine a non-1 scale with output)",
                "wpe-baker bake REQUEST.json [--tools TOOLS.json] [--encoder software|auto|vulkan|nvenc|qsv|amf]",
                "  [--encode-slots N] [--group-parallel N] [--keep-intermediates true|false] [--lang zh|en]",
                "  [--effect-resolution original|output] [--effect-render-scale 0..1] (whole-layer baking only)",
                encoderNote, parallelNote, keepNote),
            ["export"] = "wpe-baker export EXPORT_REQUEST.json",
            ["targets"] = "wpe-baker targets WALLPAPER_ENGINE.exe",
            ["apply"] = "wpe-baker apply REQUEST.json --wallpaper-engine EXE",
            ["rollback"] = "wpe-baker rollback APPLY.json --wallpaper-engine EXE",
            ["inspect"] = "wpe-baker inspect SOURCE [--assets DIR] [--out REPORT.json]",
            ["extract"] = "wpe-baker extract SOURCE --out NEW_DIRECTORY",
            ["render"] = "wpe-baker render REQUEST.json --tools TOOLS.json",
            ["validate"] = "wpe-baker validate REQUEST.json [--tools TOOLS.json]",
            ["measure-official"] = string.Join(Environment.NewLine,
                "wpe-baker measure-official REQUEST.json",
                "  Request fields: schema_version, process_id, label, output_directory, seconds, target_fps,",
                "    present_mon_path, expected_process_path, swap_chain_address, track_display, nvidia_gpu_uuid.",
                "  Before baking, measure the source itself: source_project + wallpaper_engine_executable play that",
                "    source in Wallpaper Engine, wait settle_seconds, sample it and restore the previous wallpaper;",
                "    process_id may then be 0. Optional location names one assigned monitor.",
                "  Power comes from the Windows Energy Meter (RAPL) counters. Only a graphics domain (PP1/GT)",
                "    measures the wallpaper itself; a machine without one reports unsupported_platform and no verdict.",
                "  Source-only watts describe this device; verdict.worth_baking stays null until savings are established."),
            ["decode-check"] = "wpe-baker decode-check VIDEO --out NEW_DIRECTORY [--tools TOOLS.json]",
            ["devices"] = "wpe-baker devices",
            ["pack-video"] = "wpe-baker pack-video INPUT.mp4 --width W --height H --out NEW.tex",
            ["pack-rgba"] = "wpe-baker pack-rgba INPUT.rgba --width W --height H --out NEW.tex",
        };
    }

    /// <summary>
    /// 帮助输出的语言：参数里出现合法的 `--lang zh|en` 就用它，否则用 <paramref name="fallback"/>（系统界面语言）。
    /// 帮助路径不走常规的成对选项解析，所以 --lang 放在 --help 前后都认。
    /// </summary>
    public static string HelpLanguage(IReadOnlyList<string> args, string fallback)
    {
        ArgumentNullException.ThrowIfNull(args);
        for (int i = 0; i + 1 < args.Count; ++i)
            if (args[i] == "--lang" && args[i + 1] is "zh" or "en") return args[i + 1];
        return fallback;
    }
}
