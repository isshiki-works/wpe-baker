using System.Globalization;

namespace Periodica.Domain;

/// <summary>
/// "烘焙能省掉多少工作"的判据里与 plan JSON、资源读取无关的部分：视频外壳判据（Baker.Core 的 VideoDominance）的解码工作量比较
/// 与分量归属，以及烘焙价值（Baker.Core 的 BakeValueAssessment）的规则表。两者都只比较结构与编码尺寸/帧率，不是功耗结论；
/// 未知一律不当作低价值。
/// </summary>
public static class WorkloadValue
{
    /// <summary>源视频解码量可降低（输出像素数或帧率更小）。</summary>
    public const string DecodePotentialGain = "potential_gain";

    /// <summary>输出与源视频同尺寸同帧率（或更大），解码量不降。</summary>
    public const string DecodeNotReduced = "not_reduced";

    /// <summary>
    /// 输出尺寸与帧率能不能拿来比较：三者乘积有限且各自为正。缺值（NaN）、零或无穷都不比较，解码工作量记 unknown。
    /// </summary>
    public static bool IsComparableOutput(double width, double height, double fps) =>
        double.IsFinite(width * height * fps) && width > 0 && height > 0 && fps > 0;

    /// <summary>编码格式一致之后的解码量比较：输出像素数或帧率任一小于源视频即有降低空间。</summary>
    public static string DecodeWorkStatus(double width, double height, double fps, double sourceWidth, double sourceHeight, double sourceFps) =>
        width * height < sourceWidth * sourceHeight || fps < sourceFps ? DecodePotentialGain : DecodeNotReduced;

    /// <summary>周期分量 id（如 video/12/...）的第二段是承载它的图层；解析不出来记 −1，落在"不止一层"那一侧。</summary>
    public static int OwnerOf(string component)
    {
        string[] parts = component.Split('/');
        return parts.Length > 1 && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int id) ? id : -1;
    }

    /// <summary>
    /// 图层铺满居中的整幅画布：覆盖占比 ≥ 1，包围盒中心两轴都离画布中心不到 1e-9。视频外壳判据与烘焙价值共用这一条。
    /// 任一值缺失（NaN）都不成立：位置未知证明不了"只是把这一层画满"，与"未知一律不当作低价值"同一口径。
    /// </summary>
    public static bool FillsCentredCanvas(double fraction, double centreX, double centreY) =>
        fraction >= 1 && Math.Abs(centreX - .5) < 1e-9 && Math.Abs(centreY - .5) < 1e-9;

    /// <summary>静态纹理的图像尺寸是否大于输出（任一边）：大于时缩小后驻留与采样开销可能下降。</summary>
    public static bool TextureExceedsOutput(uint textureWidth, uint textureHeight, double width, double height) =>
        textureWidth > width || textureHeight > height;

    /// <summary>烘焙价值的一条结论：状态（low_value / potential_gain / unknown）、规则代号与中英理由。</summary>
    public sealed record Verdict(string Status, string Rule, string ReasonZh, string ReasonEn);

    /// <summary>烘焙价值结论的适用范围说明：与当前 GPU 快慢无关；潜在收益不是实测省电，未知不是低价值。</summary>
    public const string Scope = "Work removed by this plan, independent of current GPU speed. Potential gain is not measured power saving; unknown is not low value.";

    /// <summary>烘焙价值判为低价值时的状态。</summary>
    public const string LowValueStatus = "low_value";

    /// <summary>方案仍有未解决的问题：不能据此判断原作没有优化价值。</summary>
    public static readonly Verdict UnresolvedPlan = new("unknown", "unresolved_plan", "当前方案仍有未解决的问题，不能据此判断原作没有优化价值。",
        "The current plan has unresolved issues; that does not establish a lack of optimization value.");

    /// <summary>方案缓存了重复执行的特效前缀。</summary>
    public static readonly Verdict CachedEffectPrefix = new("potential_gain", "cached_effect_prefix", "方案可以缓存重复执行的特效；实际收益仍需原作与成品对照。",
        "The plan caches repeated effect work; actual savings still need a source/candidate comparison.");

    /// <summary>没有可用候选：不能据此断言原作负载低。</summary>
    public static readonly Verdict NoWorkingCandidate = new("unknown", "no_working_candidate", "当前未找到可用方案，不能据此断言原作负载低或没有优化价值。",
        "No usable candidate was found; that does not establish low load or lack of optimization value.");

    /// <summary>源视频的编码像素数或帧率可降低。</summary>
    public static readonly Verdict ReducedVideoDecode = new("potential_gain", "reduced_video_decode", "源视频的编码像素数或帧率可降低，可能减少解码开销；实际收益仍待对照。",
        "The source video's encoded pixel count or frame rate can fall, potentially reducing decoding work; actual savings still need comparison.");

    /// <summary>视频外壳（或显式覆盖）且解码量不降：没有已识别的缩减。</summary>
    public static readonly Verdict UnchangedVideoPlayback = new(LowValueStatus, "unchanged_video_playback", "当前方案没有已识别的特效、绘制或视频解码量缩减，通常无需重复烘焙。",
        "The plan removes no identified effect, draw or video decoding work; rebaking is usually unnecessary.");

    /// <summary>被烘图层上有逐帧特效通道。</summary>
    public static readonly Verdict CachedEffectPasses = new("potential_gain", "cached_effect_passes", "方案可以省去逐帧特效计算；画面静止也可能有缓存价值。",
        "The plan can remove per-frame effects; a still result can still have caching value.");

    /// <summary>静态纹理大于输出尺寸。</summary>
    public static readonly Verdict StaticTextureFootprint = new("potential_gain", "static_texture_footprint", "原静态纹理大于输出尺寸，可能降低纹理驻留或采样开销；不能仅因画面静止而排除。",
        "The static texture exceeds the output size; residency or sampling costs may fall, so still imagery is not automatically excluded.");

    /// <summary>原作只画一张不大于输出、铺满居中画布的无特效静态背景。</summary>
    public static readonly Verdict OneStillTextureUnchanged = new(LowValueStatus, "one_still_texture_unchanged", "原作已经只绘制一张无特效静态背景，纹理也不大于输出；生成后仍需同一次贴图，其他实时层不会因此省去。",
        "The source already draws one plain still background no larger than the output; generation retains that draw and does not remove the other live layers.");

    /// <summary>以上都不成立：尚未证明能省去多少计算。</summary>
    public static readonly Verdict NeedsWorkComparison = new("unknown", "needs_work_comparison", "尚未证明能省去多少计算，不按本机运行轻松或画面静止直接排除。",
        "Removed work has not been established; low load on this GPU or still imagery alone is not an exclusion.");
}
