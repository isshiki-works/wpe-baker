using System.Globalization;
using System.Text.Json.Nodes;
using Baker.Cli;
using Baker.Core;
using Xunit;

// C2.5a：CLI 选项表与界面映射同一出处。
// ① 选项表 → AnalyzeOptions → 工厂得到的请求，与改表前的 CLI 映射逐字段相等（覆盖集：每个选项的每个取值至少一次 + 常用组合）；
// ② 同一组选择，CLI 选项表与界面映射（AnalyzeOptions.ForDesktop）得到的请求逐字段相等；
// ③ 退出码表：未知选项、缺值、非法值、冲突组合都以异常结束（Program.cs 统一映射为退出码 1）。
[Trait("Layer", "L1")]
public class CliOptionTableTests
{
    private const string Source = "scene-dir", Assets = "assets-dir", Output = "analysis-dir";
    private static readonly JsonObject Properties = new() { ["speed"] = 2 };
    private static readonly JsonObject PropertiesOrigin = new() { ["mode"] = "defaults" };
    private static readonly JsonObject FrameRate = new() { ["source"] = "explicit", ["fps"] = 60 };

    private static HybridAnalyzeRequest Cli(params string[] options)
    {
        Dictionary<string, string> parsed = OptionTable.Parse(["analyze", Source, .. options]);
        return AnalyzeRequestFactory.Build(OptionTable.ReadAnalyze(parsed), Source, Assets, Output, Properties, PropertiesOrigin,
            60, (uint)OptionTable.Value("analyze", parsed, "--fps-den")!, FrameRate);
    }

    /// <summary>改表之前 Program.cs、RetimeProfile.ReadArguments、PresetCascade.IsCustom 的请求映射，原样抄来作差分对照。</summary>
    private static HybridAnalyzeRequest Legacy(params string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i += 2) options.Add(args[i], args[i + 1]);
        uint Number(string option, uint fallback) => options.TryGetValue(option, out string? value) ? uint.Parse(value, CultureInfo.InvariantCulture) : fallback;
        double? Real(string option) => options.TryGetValue(option, out string? value) ? double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture) : null;
        string preset = options.GetValueOrDefault("--preset", "balanced");
        bool custom = options.Keys.Any(option => option is
            "--properties" or "--properties-source" or "--width" or "--height" or "--fps" or "--fps-den" or
            "--video-layout" or "--live-overlays" or "--text-effects" or "--audio-effects" or
            "--exclude-layers" or "--retime-budget" or "--video-shell" or
            "--sway-retime" or "--loop-max-seconds" or
            "--retain-live" or "--daytime-split" or "--trace");
        return new HybridAnalyzeRequest(2, Source, Assets, Output,
            Number("--width", 0), Number("--height", 0), 60, Number("--fps-den", 1), Properties,
            MaximumRetimePercent: 2, AllowLocalSeamRepair: false,
            RuntimeTraceFile: options.GetValueOrDefault("--trace"),
            DeviceUuid: options.GetValueOrDefault("--device"),
            RetainLiveRootIds: options.TryGetValue("--retain-live", out string? keep) ? keep.Split(',').Select(int.Parse).ToArray() : null,
            VideoLayout: options.GetValueOrDefault("--video-layout", "full_frame"),
            LiveOverlayPlacement: options.GetValueOrDefault("--live-overlays", "foreground"),
            LiveTextEffects: options.GetValueOrDefault("--text-effects", "preserve"),
            AudioEffects: options.GetValueOrDefault("--audio-effects", "preserve"),
            ExcludedLayerIds: options.TryGetValue("--exclude-layers", out string? excludedLayers)
                ? excludedLayers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(int.Parse).ToArray() : null,
            LoopPreference: RetimeProfile.LoopPreferenceForPreset(preset),
            VideoShell: options.GetValueOrDefault("--video-shell", "reject"),
            SwayRetime: options.GetValueOrDefault("--sway-retime", "on") == "on",
            LoopLengthMaximumSeconds: Real("--loop-max-seconds"),
            PropertiesOrigin: PropertiesOrigin,
            FrameRateOrigin: FrameRate,
            Preset: preset,
            RetimeBudgetPercent: Real("--retime-budget"),
            DaytimeSplit: options.GetValueOrDefault("--daytime-split", "off") == "on",
            CustomSettings: custom, Interaction: options.GetValueOrDefault("--interaction", "fixed"),
            LayoutExplicit: options.ContainsKey("--video-layout"));
    }

    /// <summary>逐字段比较（含 JsonIgnore 的字段）：数组按元素、JSON 按内容，其余按值。返回不相等的字段名。</summary>
    private static string[] Differences(HybridAnalyzeRequest left, HybridAnalyzeRequest right) =>
        typeof(HybridAnalyzeRequest).GetProperties().Where(property => !((property.GetValue(left), property.GetValue(right)) switch
        {
            (null, null) => true,
            (int[] a, int[] b) => a.SequenceEqual(b),
            (JsonNode a, JsonNode b) => JsonNode.DeepEquals(a, b),
            var (a, b) => Equals(a, b)
        })).Select(property => property.Name).ToArray();

    /// <summary>没有枚举取值的 analyze 选项要试的值；有枚举的逐个取值由表生成。</summary>
    private static readonly Dictionary<string, string[][]> Samples = new(StringComparer.Ordinal)
    {
        ["--out"] = [["--out", "plan.json"]],
        ["--assets"] = [["--assets", "D:/assets"]],
        ["--tools"] = [["--tools", "tools.json"]],
        ["--properties"] = [["--properties", "props.json"]],
        ["--width"] = [["--width", "1920", "--height", "1080"], ["--width", "0", "--height", "0"]],
        ["--height"] = [["--height", "2160", "--width", "3840"]],
        ["--fps"] = [["--fps", "60"], ["--fps", "144"]],
        ["--fps-den"] = [["--fps", "60000", "--fps-den", "1001"], ["--fps", "30", "--fps-den", "1"]],
        ["--device"] = [["--device", "b22adf1f455b2bcc8bdbefd93eab85ee"]],
        ["--exclude-layers"] = [["--exclude-layers", "12,34"], ["--exclude-layers", "34, ,12,"], ["--exclude-layers", ""]],
        ["--retain-live"] = [["--retain-live", "5"], ["--retain-live", "7,5"]],
        ["--retime-budget"] = [["--retime-budget", "0"], ["--retime-budget", "4.5"], ["--retime-budget", "5"]],
        ["--loop-max-seconds"] = [["--loop-max-seconds", "1"], ["--loop-max-seconds", "900"], ["--loop-max-seconds", "3600"]],
        ["--trace"] = [["--trace", "trace.json"]],
    };

    private static readonly string[][] CommonCombinations =
    [
        // 黄金语料的固定参数。
        ["--width", "1920", "--height", "1080", "--fps", "60", "--fps-den", "1", "--preset", "balanced", "--interaction", "fixed",
            "--properties-source", "defaults", "--device", "b22adf1f455b2bcc8bdbefd93eab85ee"],
        ["--preset", "quality", "--interaction", "off", "--video-layout", "layered", "--audio-effects", "omit"],
        ["--preset", "efficiency", "--interaction", "keep", "--retime-budget", "4.5", "--loop-max-seconds", "900", "--sway-retime", "off"],
        ["--daytime-split", "on", "--video-shell", "allow", "--live-overlays", "preserve", "--text-effects", "simple", "--lang", "en"],
        ["--out", "plan.json", "--tools", "tools.json", "--assets", "D:/assets", "--preset", "quality", "--lang", "zh"],
        ["--exclude-layers", "3,4", "--retain-live", "9", "--trace", "t.json", "--properties", "p.json", "--properties-source", "wpe",
            "--width", "1280", "--height", "720", "--fps", "120", "--fps-den", "1", "--video-layout", "full_frame"],
    ];

    /// <summary>覆盖集：空参数、每个枚举选项的每个取值、每个非枚举选项的样例值、常用组合。</summary>
    public static IEnumerable<string[]> CoverageSet() =>
        new[] { Array.Empty<string>() }
            .Concat(OptionTable.Options.Where(option => option.Commands.Contains("analyze") && option.Choices is not null)
                .SelectMany(option => option.Choices!.Select(choice => new[] { option.Name, choice })))
            .Concat(Samples.Values.SelectMany(cases => cases))
            .Concat(CommonCombinations);

    [Fact]
    public void EveryAnalyzeOptionAndValueIsCovered()
    {
        string[][] coverage = CoverageSet().ToArray();
        foreach (CliOption option in OptionTable.Options.Where(option => option.Commands.Contains("analyze")))
        {
            Assert.True(coverage.Any(args => args.Contains(option.Name)), option.Name + " has no coverage case");
            foreach (string choice in option.Choices ?? [])
                Assert.True(coverage.Any(args => Array.IndexOf(args, option.Name) is int at && at >= 0 && args[at + 1] == choice),
                    option.Name + " " + choice + " has no coverage case");
        }
    }

    [Fact]
    public void OptionTableRequestEqualsPreviousCliMapping()
    {
        int cases = 0;
        foreach (string[] args in CoverageSet())
        {
            Assert.Empty(Differences(Cli(args), Legacy(args)));
            cases++;
        }
        Assert.True(cases >= 55, "coverage set shrank to " + cases);
    }

    [Fact]
    public void CliAndDesktopProduceTheSameRequest()
    {
        int cases = 0;
        foreach (string preset in new[] { RetimeProfile.Efficiency, RetimeProfile.Balanced, RetimeProfile.Quality })
        foreach (string interaction in new[] { "keep", "fixed", "off" })
        foreach (bool layered in new[] { false, true })
        foreach (bool omitAudio in new[] { false, true })
        foreach (bool exclude in new[] { false, true })
        foreach (double? budget in new double?[] { null, 4.5 })
        foreach (bool sway in new[] { true, false })
        foreach (string? device in new[] { null, "b22adf1f455b2bcc8bdbefd93eab85ee" })
        {
            // 界面：高级区任何一项偏离档位默认就记自定义（MainWindow.AdvancedIsCustom 的判据，本组选择覆盖到的部分）。
            bool custom = layered || omitAudio || exclude || budget is not null || !sway;
            HybridAnalyzeRequest desktop = AnalyzeRequestFactory.Build(
                AnalyzeOptions.ForDesktop(preset, interaction, true, layered, omitAudio, exclude ? new HashSet<int> { 34, 12 } : [],
                    budget, sway, custom, 0, 0, device),
                Source, Assets, Output, Properties, PropertiesOrigin, 60, 1, FrameRate);
            var args = new List<string> { "--preset", preset, "--interaction", interaction };
            if (layered) args.AddRange(["--video-layout", "layered"]);
            if (omitAudio) args.AddRange(["--audio-effects", "omit"]);
            if (exclude) args.AddRange(["--exclude-layers", "12,34"]);
            if (budget is double percent) args.AddRange(["--retime-budget", percent.ToString(CultureInfo.InvariantCulture)]);
            if (!sway) args.AddRange(["--sway-retime", "off"]);
            if (device is not null) args.AddRange(["--device", device]);
            Assert.Empty(Differences(Cli([.. args]), desktop));
            cases++;
        }
        Assert.Equal(576, cases);
    }

    [Fact]
    public void DesktopOnlyRetimeSwitchTurnsCommonRetimeOff()
    {
        HybridAnalyzeRequest on = AnalyzeRequestFactory.Build(new AnalyzeOptions(), Source, Assets, Output, null, null, 60, 1, FrameRate);
        HybridAnalyzeRequest off = AnalyzeRequestFactory.Build(new AnalyzeOptions { CommonRetime = false },
            Source, Assets, Output, null, null, 60, 1, FrameRate);
        Assert.Equal(2, on.MaximumRetimePercent);
        Assert.Equal(0, off.MaximumRetimePercent);
        Assert.Equal(new[] { "MaximumRetimePercent" }, Differences(on, off));
    }

    /// <summary>退出码表：参数 → 期望的异常类型。Program.cs 把这些异常一律映射为退出码 1。</summary>
    public static TheoryData<string[], Type> ExitCodeTable() => new()
    {
        // 未知选项、别的子命令的选项
        { ["analyze", "src", "--bogus", "x"], typeof(ArgumentException) },
        { ["bake", "plan.json", "--preset", "quality"], typeof(ArgumentException) },
        { ["export", "req.json", "--out", "x"], typeof(ArgumentException) },
        { ["nosuch", "x", "--out", "y"], typeof(ArgumentException) },
        // 缺值、重复、不以 -- 开头、缺位置参数
        { ["analyze", "src", "--preset"], typeof(ArgumentException) },
        { ["analyze", "src", "--preset", "quality", "--preset", "balanced"], typeof(ArgumentException) },
        { ["analyze", "src", "preset", "quality"], typeof(ArgumentException) },
        { ["analyze"], typeof(ArgumentException) },
        // 非法值
        { ["analyze", "src", "--preset", "fast"], typeof(ArgumentException) },
        { ["analyze", "src", "--interaction", "none"], typeof(ArgumentException) },
        { ["analyze", "src", "--sway-retime", "yes"], typeof(ArgumentException) },
        { ["analyze", "src", "--daytime-split", "true"], typeof(ArgumentException) },
        { ["analyze", "src", "--video-shell", "maybe"], typeof(ArgumentException) },
        { ["analyze", "src", "--video-layout", "tiled"], typeof(ArgumentException) },
        { ["analyze", "src", "--live-overlays", "top"], typeof(ArgumentException) },
        { ["analyze", "src", "--text-effects", "none"], typeof(ArgumentException) },
        { ["analyze", "src", "--audio-effects", "mute"], typeof(ArgumentException) },
        { ["analyze", "src", "--properties-source", "config"], typeof(ArgumentException) },
        { ["analyze", "src", "--lang", "fr"], typeof(ArgumentException) },
        { ["analyze", "src", "--retime-budget", "6"], typeof(ArgumentException) },
        { ["analyze", "src", "--retime-budget", "abc"], typeof(ArgumentException) },
        { ["analyze", "src", "--loop-max-seconds", "0"], typeof(ArgumentException) },
        { ["analyze", "src", "--loop-max-seconds", "3601"], typeof(ArgumentException) },
        { ["analyze", "src", "--width", "abc", "--height", "1080"], typeof(FormatException) },
        { ["analyze", "src", "--fps", "6O"], typeof(FormatException) },
        { ["analyze", "src", "--fps", "-1"], typeof(OverflowException) },
        { ["analyze", "src", "--exclude-layers", "a,b"], typeof(FormatException) },
        { ["analyze", "src", "--retain-live", "1,,2"], typeof(FormatException) },
        { ["bake", "plan.json", "--encoder", "x264"], typeof(ArgumentException) },
        { ["bake", "plan.json", "--effect-resolution", "half"], typeof(ArgumentException) },
        { ["bake", "plan.json", "--effect-render-scale", "0"], typeof(ArgumentException) },
        { ["bake", "plan.json", "--encode-slots", "65"], typeof(ArgumentException) },
        { ["bake", "plan.json", "--group-parallel", "0"], typeof(ArgumentException) },
        { ["bake", "plan.json", "--keep-intermediates", "yes"], typeof(ArgumentException) },
        { ["bake", "plan.json", "--lang", "de"], typeof(ArgumentException) },
        // 冲突组合
        { ["analyze", "src", "--fps-den", "1001"], typeof(ArgumentException) },
        { ["analyze", "src", "--width", "1920"], typeof(ArgumentException) },
        { ["analyze", "src", "--height", "1080"], typeof(ArgumentException) },
    };

    [Theory]
    [MemberData(nameof(ExitCodeTable))]
    public void InvalidArgumentsFail(string[] args, Type expected)
    {
        // 子命令走到取值校验那一步时读全部给出的选项：analyze 读成 AnalyzeOptions，其余子命令逐个按表读。
        void Run()
        {
            Dictionary<string, string> options = OptionTable.Parse(args);
            if (args[0] == "analyze") OptionTable.ReadAnalyze(options);
            else foreach (string name in options.Keys) OptionTable.Value(args[0], options, name);
        }
        Exception error = Assert.ThrowsAny<Exception>(Run);
        Assert.IsType(expected, error);
    }

    [Fact]
    public void EveryRowIsUsedByParsingOrHelp()
    {
        foreach (CliOption option in OptionTable.Options)
        foreach (string command in option.Commands)
        {
            Assert.Contains(OptionTable.Commands, spec => spec.Name == command);
            Assert.Same(option, OptionTable.Find(command, option.Name));
            // 默认值本身能过自己的校验。
            if (option.Default is not null) option.Read(option.Default);
        }
    }
}
