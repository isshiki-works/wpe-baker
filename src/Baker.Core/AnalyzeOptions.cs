namespace Baker.Core;

/// <summary>
/// 一次分析里用户能选的全部东西，带类型。CLI 由选项表（Baker.Cli/OptionTable）填，界面由控件填（<see cref="ForDesktop"/>），
/// 再统一交给 <see cref="AnalyzeRequestFactory"/> 生成请求。默认值只写在这里：CLI 帮助里的 "(default …)" 也读 new AnalyzeOptions()。
/// </summary>
public sealed record AnalyzeOptions
{
    public string Preset { get; init; } = RetimeProfile.Balanced;
    public string Interaction { get; init; } = "fixed";
    /// <summary>观感改动预算的高级覆盖（百分比）；null = 跟档位。</summary>
    public double? RetimeBudgetPercent { get; init; }
    /// <summary>循环长度上限的高级覆盖（秒）；null = 跟档位。</summary>
    public double? LoopMaximumSeconds { get; init; }
    public bool SwayRetime { get; init; } = SwayRetimeOptions.OnByDefault;
    /// <summary>界面的"调速"勾选框；CLI 没有对应选项，恒为 true。关掉时通用分量调速上限为 0。</summary>
    public bool CommonRetime { get; init; } = true;
    /// <summary>null = 自动（先整幅，不行再分层）；给了值就只按这个布局分析。</summary>
    public string? VideoLayout { get; init; }
    public string LiveOverlays { get; init; } = "foreground";
    public string TextEffects { get; init; } = "preserve";
    public string AudioEffects { get; init; } = "preserve";
    public int[]? ExcludedLayerIds { get; init; }
    public string VideoShell { get; init; } = VideoDominance.RejectChoice;
    public bool DaytimeSplit { get; init; }
    /// <summary>0×0 = 未指定，分析时取本机主显示器分辨率。</summary>
    public uint Width { get; init; }
    public uint Height { get; init; }
    public string? DeviceUuid { get; init; }
    public int[]? RetainLiveRootIds { get; init; }
    public string? TraceFile { get; init; }
    /// <summary>plan 的 custom_settings：CLI 按选项表的"自定义"列算，界面按高级区是否改过算。</summary>
    public bool Custom { get; init; }

    /// <summary>界面控件 → 选项。界面没有的开关（时段拆分、视频外壳、保留实时层等）保持默认。</summary>
    public static AnalyzeOptions ForDesktop(string preset, string interaction, bool commonRetime, bool layered, bool omitAudio,
        IReadOnlyCollection<int> excludedLayerIds, double? retimeBudgetOverride, bool swayRetime, bool custom,
        uint width, uint height, string? deviceUuid) => new()
    {
        Preset = preset, Interaction = interaction, CommonRetime = commonRetime,
        VideoLayout = layered ? "layered" : null,
        AudioEffects = omitAudio ? "omit" : "preserve",
        ExcludedLayerIds = excludedLayerIds.Count == 0 ? null : excludedLayerIds.Order().ToArray(),
        RetimeBudgetPercent = retimeBudgetOverride, SwayRetime = swayRetime, Custom = custom,
        Width = width, Height = height, DeviceUuid = deviceUuid
    };
}
