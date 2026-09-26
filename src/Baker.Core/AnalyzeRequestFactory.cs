using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>
/// 分析请求的唯一出处：CLI 与界面都把用户选择填进 <see cref="AnalyzeOptions"/>，路径、已合并的属性和已定下的帧率
/// 由各自求出后一并交给这里。派生字段（循环取向、布局是否显式、通用调速上限）只在这里算一次。
/// </summary>
public static class AnalyzeRequestFactory
{
    public static HybridAnalyzeRequest Build(AnalyzeOptions options, string source, string assets, string outputDirectory,
        JsonObject? properties, JsonObject? propertiesOrigin, uint fpsNumerator, uint fpsDenominator, JsonObject frameRateOrigin,
        string? analysisCacheDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new(2, source, assets, outputDirectory, options.Width, options.Height, fpsNumerator, fpsDenominator, properties,
            // 质量档不设百分比预算（取改动最小的解），通用分量调速这时仍要一个上限，沿用旧默认 2%；界面关掉"调速"时为 0。
            MaximumRetimePercent: options.CommonRetime ? 2 : 0,
            RuntimeTraceFile: options.TraceFile,
            DeviceUuid: options.DeviceUuid,
            RetainLiveRootIds: options.RetainLiveRootIds,
            VideoLayout: options.VideoLayout ?? "full_frame",
            LiveOverlayPlacement: options.LiveOverlays,
            LiveTextEffects: options.TextEffects,
            AudioEffects: options.AudioEffects,
            ExcludedLayerIds: options.ExcludedLayerIds,
            // 循环取向跟着档位走。
            LoopPreference: RetimeProfile.LoopPreferenceForPreset(options.Preset),
            VideoShell: options.VideoShell,
            AllowNoBenefit: options.AllowNoBenefit,
            LoopLengthMaximumSeconds: options.LoopMaximumSeconds,
            PropertiesOrigin: propertiesOrigin,
            FrameRateOrigin: frameRateOrigin,
            Preset: options.Preset,
            RetimeBudgetPercent: options.RetimeBudgetPercent,
            DaytimeSplit: options.DaytimeSplit,
            CustomSettings: options.Custom,
            Interaction: options.Interaction,
            LayoutExplicit: options.VideoLayout is not null,
            AnalysisCacheDirectory: analysisCacheDirectory);
    }
}
