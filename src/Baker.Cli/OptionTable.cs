using System.Globalization;
using Baker.Core;

namespace Baker.Cli;

/// <summary>选项表的一行：一个选项在一组子命令里的取值写法、校验、默认值与说明。</summary>
/// <param name="Value">帮助里的取值写法；有 <paramref name="Choices"/> 时由它生成（a|b|c）。</param>
/// <param name="Choices">枚举取值：按它校验，报错句也由它生成（"--x must be a, b, or c."）。</param>
/// <param name="Parse">取值解析（在枚举校验之后）；抛出的异常类型与报错句与改表之前逐字相同。null = 原样字符串。</param>
/// <param name="Default">没给这个选项时的取值；帮助里显示为 "(default …)"。</param>
/// <param name="Custom">analyze：给了这个选项，plan 记为自定义（custom_settings）。传输类选项与档位、交互不算。</param>
/// <param name="Required">帮助里不加方括号。缺失时的报错仍由子命令自己在原来的时机给出。</param>
/// <param name="HelpKey">按 --lang 出中文或英文的说明（MessageCatalog 键）。</param>
internal sealed record CliOption(string Name, string[] Commands, string? Value = null, string[]? Choices = null,
    Func<string, object>? Parse = null, string? Default = null, bool Custom = false, bool Required = false,
    string[]? Help = null, string? HelpKey = null)
{
    public object Read(string text)
    {
        if (Choices is not null && !Choices.Contains(text))
            throw new ArgumentException($"{Name} must be {(Choices.Length == 2 ? Choices[0] + " or " + Choices[1]
                : string.Join(", ", Choices[..^1]) + ", or " + Choices[^1])}.");
        return Parse is null ? text : Parse(text);
    }
}

/// <summary>子命令：名字、位置参数、是否诊断命令，以及跟在选项后面的整段说明。</summary>
internal sealed record CliCommand(string Name, string Operand, bool Diagnostic, params string[] Footer);

/// <summary>
/// CLI 的唯一选项表：哪些子命令认哪些选项、取值怎么校验、默认值、是否算自定义，以及帮助文本，全从这里生成。
/// 退出码、stdout 只放 JSON、人话走 stderr、重定向时 UTF-8 无 BOM 这些规则仍在 Program.cs，不因这张表改变。
/// </summary>
internal static class OptionTable
{
    private const string Analyze = "analyze", Bake = "bake";
    private static readonly AnalyzeOptions D = new();

    private static object UInt(string text) => uint.Parse(text, CultureInfo.InvariantCulture);

    public static readonly CliCommand[] Commands =
    [
        new(Analyze, "SOURCE", false,
            // fix/mdl-lenient-strings 加的退出码表，原样保留中英两行（两种 --lang 下输出相同）。
            "  Exit codes: 0 plan written; 1 not a Scene wallpaper, or analysis failed; 3 preset package;",
            "    4 tool limitation: the renderer cannot read an asset file (a 3D model or texture), a report with",
            "      summary.verdict=tool_limitation is still written to --out; 130 cancelled.",
            "  退出码：0 已写出 plan；1 不是 Scene 壁纸或分析失败；3 预设包；4 工具局限：渲染器读不了某个素材文件",
            "    （3D 模型或纹理），--out 仍写出 summary.verdict=tool_limitation 的结论；130 已取消。"),
        new(Bake, "PLAN.json|REQUEST.json", false,
            "  A saved PLAN.json needs --out NEW_DIRECTORY; a REQUEST.json already names its output directory."),
        new("export", "EXPORT_REQUEST.json", false),
        new("targets", "WALLPAPER_ENGINE.exe", false),
        new("apply", "REQUEST.json", false),
        new("rollback", "APPLY.json", false),
        new("inspect", "SOURCE", true),
        new("extract", "SOURCE", true),
        new("render", "REQUEST.json", true),
        new("validate", "REQUEST.json", true),
        new("decode-check", "VIDEO", true),
        new("devices", "", true),
        new("pack-video", "INPUT.mp4", true),
        new("pack-rgba", "INPUT.rgba", true),
    ];

    public static readonly CliOption[] Options =
    [
        // ---- analyze ----
        new("--out", [Analyze], "PLAN.json"),
        new("--assets", [Analyze, "inspect"], "DIR"),
        new("--tools", [Analyze, Bake, "decode-check", "validate"], "TOOLS.json"),
        new("--properties-source", [Analyze], Choices: [WallpaperEngineProperties.SourceWpe, WallpaperEngineProperties.SourceDefaults],
            Default: WallpaperEngineProperties.SourceWpe, Custom: true),
        new("--properties", [Analyze], "JSON_FILE_PATH", Custom: true),
        new("--interaction", [Analyze], Choices: ["keep", "fixed", "off"], Default: D.Interaction, Help:
        [
            "keep: preserve input; fixed: fixed view; off: also disable pointer effects and sampled costly full-screen audio effects.",
            "Clocks, dates and media text stay live. Unmeasured audio effects stay live."
        ]),
        new("--width", [Analyze], "W", Parse: UInt, Default: D.Width.ToString(CultureInfo.InvariantCulture), Custom: true,
            Help: ["Give --width and --height together, or neither to fit the scene canvas to the primary display."]),
        new("--height", [Analyze], "H", Parse: UInt, Default: D.Height.ToString(CultureInfo.InvariantCulture), Custom: true,
            Help: ["See --width."]),
        new("--fps", [Analyze], "N", Parse: UInt, Default: "0", Custom: true, Help:
        [
            "Without --fps: min(Wallpaper Engine's FPS limit, the primary display's refresh rate), snapped to the",
            "  nearest of 30/60/120/144/165/240; 60 when either cannot be read. --fps-den needs --fps.",
            "不给 --fps 时：取 min(Wallpaper Engine 的帧率上限, 主显示器刷新率) 就近落到 30/60/120/144/165/240，",
            "  任一读不到就用 60。--fps-den 要和 --fps 一起给。"
        ]),
        new("--fps-den", [Analyze], "D", Parse: UInt, Default: "1", Custom: true),
        new("--device", [Analyze], "VULKAN_UUID"),
        // 这几个开关名字比作用大，按 54 个候选场景的实测标注适用范围，免得用户拿它们当"关掉元素"用。
        new("--video-layout", [Analyze], Choices: ["full_frame", "layered"], Custom: true,
            Help: ["Without it: full_frame first, then layered (up to 4 videos) when full_frame cannot be generated."]),
        new("--live-overlays", [Analyze], Choices: ["preserve", "foreground"], Default: D.LiveOverlays, Custom: true, Help:
        [
            "Reorders independent live text/particle trees only; it never removes anything,",
            "and has no effect on image/model trees or framebuffer effects."
        ]),
        new("--text-effects", [Analyze], Choices: ["preserve", "simple"], Default: D.TextEffects, Custom: true, Help:
        [
            "simple drops fixed text effects only (no scripts, no observed input,",
            "no framebuffer/audio/pointer/parallax layers): 10 of 54 scanned scenes had any."
        ]),
        new("--audio-effects", [Analyze], Choices: ["preserve", "omit"], Default: D.AudioEffects, Custom: true, Help:
        [
            "omit drops fixed audio-reactive effects only: 10 of 54 scanned scenes had any;",
            "audio-driven scripts and shaders stay live."
        ]),
        new("--exclude-layers", [Analyze], "12,34", Custom: true,
            Parse: text => text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(int.Parse).ToArray(),
            Help:
            [
                "The only switch that really turns an element off; it omits each listed layer with its whole",
                "subtree, exactly as if switched off in the source. See tradeoff_options in the plan for which",
                "elements can be traded away and what disappears with them."
            ]),
        new("--retain-live", [Analyze], "12,34", Custom: true, Parse: text => text.Split(',').Select(int.Parse).ToArray(),
            Help: ["Keeps the listed layer trees live (a plan may suggest the exact list)."]),
        new("--preset", [Analyze], Choices: [RetimeProfile.Efficiency, RetimeProfile.Balanced, RetimeProfile.Quality, RetimeProfile.Compatibility], Default: D.Preset, Help:
        [
            "Automatic fallback: quality -> balanced -> efficiency.",
            "Presets change retiming only; interaction is independent. Failed settings may return a verified suggested_change.",
            "efficiency: 5% look budget, loop at most 600 s; balanced: 3%, 600 s; quality: smallest change, 1200 s.",
            "compatibility (experimental, manual only, never a fallback): 10% look budget, loop at most 1200 s, still capped by the 2 GiB",
            "embedded-video limit; seam, composition and quality gates unchanged. No scan data supports 10%; the look change is unverified.",
            "兼容档（实验性，只能手动选）：预算 10%、循环上限 1200 s；10% 没有扫描数据支持，观感变化未验证。",
            "档位给的是允许多大的观感改动，循环长度是求解结果；上限只是兜底。"
        ]),
        new("--retime-budget", [Analyze], "PERCENT", Custom: true, Parse: text =>
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) &&
            double.IsFinite(parsed) && parsed >= 0 && parsed <= RetimeProfile.MaximumBudgetPercent
                ? parsed : throw new ArgumentException("--retime-budget must be a percentage from 0 to 5."),
            Help: ["0..5, advanced override of the preset's look budget."]),
        new("--loop-max-seconds", [Analyze], "SECONDS", Custom: true, Parse: text =>
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) &&
            double.IsFinite(parsed) && parsed > 0 && parsed <= CommonLoopSolver.MaximumLoopLengthSeconds
                ? parsed : throw new ArgumentException("--loop-max-seconds must be a number of seconds above 0 and at most 3600."),
            Help: ["Advanced override of the preset's loop ceiling, at most 3600."]),
        new("--video-shell", [Analyze], Choices: [VideoDominance.RejectChoice, VideoDominance.AllowChoice], Default: D.VideoShell, Custom: true),
        new("--sway-retime", [Analyze], Choices: ["off", "on"], Parse: text => text == "on", Default: D.SwayRetime ? "on" : "off", Custom: true, Help:
        [
            "All three presets close swaying layers on whole cycles; the look budget above governs it.",
            "摆动改频三档都开（档位的观感预算管的就是它），只有显式 --sway-retime off 才关；界面高级区同一个默认。"
        ]),
        new("--daytime-split", [Analyze], Choices: ["on", "off"], Parse: text => text == "on", Default: D.DaytimeSplit ? "on" : "off", Custom: true,
            Help: ["Plans each recognized day/time state of the wallpaper separately."]),
        new("--trace", [Analyze], "FILE", Custom: true, Help: ["Diagnostic: writes the renderer's runtime trace to FILE."]),
        new("--lang", [Analyze, Bake], Choices: ["zh", "en"], Help: ["Default: the system interface language."]),
        // ---- bake ----
        new("--out", [Bake], "NEW_DIRECTORY", Help: ["Only with a PLAN.json."]),
        new("--encoder", [Bake], string.Join("|", PlaybackEncoderSelection.Choices), Parse: PlaybackEncoderSelection.Normalize,
            HelpKey: "cli.bake_encoder_help"),
        new("--encode-slots", [Bake], "N", Parse: text =>
            int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int slots) && slots >= 0 && slots <= EncodeSlots.MaximumQuota
                ? slots : throw new ArgumentException($"--encode-slots must be an integer from 0 to {EncodeSlots.MaximumQuota}; 0 means no limit.")),
        new("--group-parallel", [Bake], "N", Parse: text =>
            int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int groups) && groups >= 1 && groups <= 64
                ? groups : throw new ArgumentException("--group-parallel must be an integer from 1 to 64; 1 renders one group at a time."),
            HelpKey: "cli.bake_parallel_help"),
        new("--keep-intermediates", [Bake], Choices: ["true", "false"], Parse: text => text == "true",
            HelpKey: "cli.bake_keep_intermediates_help"),
        new("--effect-resolution", [Bake], Choices: ["original", "output"], Parse: text => text == "output", Default: "original",
            Help: ["output matches the output and layer dimensions (whole-layer baking only)."]),
        new("--effect-render-scale", [Bake], "SCALE", Parse: text =>
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double scale) && double.IsFinite(scale) && scale is > 0 and <= 1
                ? scale : throw new ArgumentException("--effect-render-scale must be in (0, 1]; default 1 preserves the original effect resolution."),
            Default: "1", Help: ["Greater than 0 and at most 1; a scale other than 1 cannot be combined with output (whole-layer baking only)."]),
        // ---- 其它子命令：缺失时的报错由子命令自己给 ----
        new("--out", ["inspect"], "REPORT.json"),
        new("--out", ["extract", "decode-check"], "NEW_DIRECTORY", Required: true),
        new("--tools", ["render"], "TOOLS.json", Required: true),
        new("--wallpaper-engine", ["apply", "rollback"], "EXE", Required: true),
        new("--width", ["pack-video", "pack-rgba"], "W", Required: true),
        new("--height", ["pack-video", "pack-rgba"], "H", Required: true),
        new("--out", ["pack-video", "pack-rgba"], "NEW.tex", Required: true),
    ];

    public static CliOption? Find(string command, string name) =>
        Options.FirstOrDefault(option => option.Name == name && option.Commands.Contains(command));

    /// <summary>
    /// 从第 3 个参数起成对读选项，只查名字：成对、以 -- 开头、不重复、这个子命令认得。取值在子命令走到那一步时才校验，
    /// 报错先后（例如来源判定、预设包退出码 3 先于选项取值）与改表之前一致。
    /// </summary>
    public static Dictionary<string, string> Parse(IReadOnlyList<string> args)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 2; i < args.Count; i += 2)
        {
            if (i + 1 >= args.Count || !args[i].StartsWith("--", StringComparison.Ordinal) || !options.TryAdd(args[i], args[i + 1]))
                throw new ArgumentException("Options require a unique name and value.");
        }
        if (args.Count < 2) throw new ArgumentException("Source path is required.");
        foreach (var key in options.Keys)
            if (Find(args[0], key) is null) throw new ArgumentException($"Unknown option for {args[0]}: {key}");
        return options;
    }

    /// <summary>选项的取值：给了就按表解析，没给就取表里的默认值（没有默认值为 null）。</summary>
    public static object? Value(string command, IReadOnlyDictionary<string, string> options, string name)
    {
        CliOption option = Find(command, name) ?? throw new ArgumentException($"Unknown option for {command}: {name}");
        return options.TryGetValue(name, out string? text) ? option.Read(text) : option.Default is string fallback ? option.Read(fallback) : null;
    }

    /// <summary>analyze 的选项 → <see cref="AnalyzeOptions"/>。先查两条跨选项规则，再按表的顺序逐行校验取值。</summary>
    public static AnalyzeOptions ReadAnalyze(IReadOnlyDictionary<string, string> options)
    {
        // 没给 --fps 时按 min(WPE 帧率上限, 主屏刷新率) 就近取标准档；--fps-den 是分子的配套，单独给没有意义。
        if (!options.ContainsKey("--fps") && options.ContainsKey("--fps-den"))
            throw new ArgumentException("--fps-den only applies together with --fps; omit both to take the frame rate from the Wallpaper Engine limit and the display refresh rate.");
        // 宽高要么一起给，要么都不给：都不给时由分析按场景画布铺满本机屏幕取值，0 表示未指定。
        if (options.ContainsKey("--width") != options.ContainsKey("--height"))
            throw new ArgumentException("--width and --height must be given together; omit both to size the output from the scene canvas and this machine's primary display.");
        var values = Options.Where(option => option.Commands.Contains(Analyze))
            .ToDictionary(option => option.Name, option => Value(Analyze, options, option.Name));
        return new AnalyzeOptions
        {
            Preset = (string)values["--preset"]!,
            Interaction = (string)values["--interaction"]!,
            RetimeBudgetPercent = (double?)values["--retime-budget"],
            LoopMaximumSeconds = (double?)values["--loop-max-seconds"],
            SwayRetime = (bool)values["--sway-retime"]!,
            VideoLayout = (string?)values["--video-layout"],
            LiveOverlays = (string)values["--live-overlays"]!,
            TextEffects = (string)values["--text-effects"]!,
            AudioEffects = (string)values["--audio-effects"]!,
            ExcludedLayerIds = (int[]?)values["--exclude-layers"],
            VideoShell = (string)values["--video-shell"]!,
            DaytimeSplit = (bool)values["--daytime-split"]!,
            Width = (uint)values["--width"]!,
            Height = (uint)values["--height"]!,
            DeviceUuid = (string?)values["--device"],
            RetainLiveRootIds = (int[]?)values["--retain-live"],
            TraceFile = (string?)values["--trace"],
            Custom = options.Keys.Any(name => Find(Analyze, name)?.Custom == true)
        };
    }

    /// <summary>一个子命令的用法段：命令行后面跟没有说明的选项，有说明或默认值的选项各占一行，最后是整段说明。</summary>
    public static string Usage(string command, string language)
    {
        CliCommand spec = Commands.Single(item => item.Name == command);
        CliOption[] rows = Options.Where(option => option.Commands.Contains(command)).ToArray();
        static string Token(CliOption option)
        {
            string text = option.Name + " " + (option.Choices is null ? option.Value : string.Join("|", option.Choices));
            return option.Required ? text : "[" + text + "]";
        }
        static bool Plain(CliOption option) => option.Help is null && option.HelpKey is null && option.Default is null;
        var lines = new List<string> { string.Join(" ", new[] { "wpe-baker", command, spec.Operand }
            .Concat(rows.Where(Plain).Select(Token)).Where(part => part.Length > 0)) };
        foreach (CliOption option in rows.Where(option => !Plain(option)))
        {
            lines.Add("  " + Token(option) + (option.Default is null ? "" : " (default " + option.Default + ")"));
            lines.AddRange((option.Help ?? []).Select(line => "    " + line));
            if (option.HelpKey is not null)
                lines.AddRange(MessageCatalog.Get(option.HelpKey, language).Split('\n').Select(line => "    " + line.TrimEnd('\r')));
        }
        lines.AddRange(spec.Footer);
        return string.Join(Environment.NewLine, lines);
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
