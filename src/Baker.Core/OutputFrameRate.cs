using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>
/// 默认帧率的取法。显式给了 --fps 就照用（<see cref="Explicit"/>）。没给时取
/// min(用户在 Wallpaper Engine 设置里定的帧率上限, 本机主显示器刷新率)，再就近落到 30/60/120/144/165/240 标准档
/// （距离相同取低档：帧数少、烘得快）。两条依据缺任何一条都回退 60（不是 120）：只凭其中一边猜会把帧数翻倍，
/// 而 120 fps 成品在核显上播放实测反而更费电。取值与依据写进 plan 的 frame_rate。
/// </summary>
public static class OutputFrameRate
{
    public const string Explicit = "explicit";
    public const string Auto = "auto";
    /// <summary>读不到依据时的帧率。</summary>
    public const uint Fallback = 60;
    /// <summary>就近取的标准档，必须升序：取最近档时"严格更近才替换"靠这个顺序把并列让给低档。</summary>
    public static readonly uint[] Tiers = [30, 60, 120, 144, 165, 240];

    public const string Rule = "An explicit --fps wins. Otherwise the frame rate is min(the user's Wallpaper Engine FPS limit, " +
        "the primary display's refresh rate), snapped to the nearest of 30/60/120/144/165/240 (a tie takes the lower tier). " +
        "When either the Wallpaper Engine setting or the refresh rate cannot be read, 60 is used, never 120.";

    /// <summary>
    /// 一次取值的结果。<see cref="WpeLimit"/>/<see cref="DisplayRefreshHz"/> 只在真的读到时有值，
    /// <see cref="Fps"/> 是最终帧率分子（分母由调用方的 --fps-den 决定，自动取值恒为 1）。
    /// </summary>
    public sealed record Choice(uint Fps, string Source, uint? WpeLimit, uint? DisplayRefreshHz, string Reason, string? WpeLimitStatus)
    {
        public JsonObject ToJson() => new()
        {
            ["source"] = Source,
            ["wpe_fps_limit"] = WpeLimit,
            ["wpe_limit_status"] = WpeLimitStatus,
            ["display_refresh_hz"] = DisplayRefreshHz,
            ["chosen"] = Fps,
            ["reason"] = Reason,
            ["rule"] = Rule,
        };
    }

    public static bool IsKnownSource(string? source) => source is null or Explicit or Auto;

    /// <summary>用户显式指定的帧率：不读 Wallpaper Engine 设置，也不读显示器。</summary>
    public static Choice Requested(uint fps) => new(fps, Explicit, null, null, "requested_fps", null);

    /// <summary>
    /// 决定帧率分子。<paramref name="requestedFps"/> 为 0 表示未指定（CLI 没给 --fps）。
    /// 两个依据都用委托取，单测可注入固定值，也保证显式指定时一次 IO 都不做。
    /// </summary>
    public static Choice Choose(uint requestedFps, Func<WallpaperEngineProperties.FrameRateLimit> wpeLimit, Func<uint?> displayRefresh)
    {
        ArgumentNullException.ThrowIfNull(wpeLimit);
        ArgumentNullException.ThrowIfNull(displayRefresh);
        if (requestedFps != 0) return Requested(requestedFps);
        var limit = wpeLimit();
        uint? refresh = displayRefresh() is uint hz && hz > 1 && hz <= 10000 ? hz : null;
        // "连有没有上限都不知道"与"读到了、用户没设上限"是两回事：前者回退 60，后者由刷新率单独定。
        if (!limit.Known) return new(Fallback, Auto, null, refresh, "wpe_limit_unreadable", limit.Reason);
        if (refresh is not uint rate) return new(Fallback, Auto, limit.Fps, null, "display_refresh_unreadable", limit.Reason);
        uint target = limit.Fps is uint capped ? Math.Min(capped, rate) : rate;
        return new(NearestTier(target), Auto, limit.Fps, rate,
            limit.Fps is null ? "display_refresh" : "wpe_limit_and_display_refresh", limit.Reason);
    }

    /// <summary>就近取标准档；距离相同取低档（<see cref="Tiers"/> 升序 + 严格更近才替换）。</summary>
    public static uint NearestTier(uint target)
    {
        uint best = Tiers[0];
        foreach (uint tier in Tiers)
            if (Distance(tier) < Distance(best)) best = tier;
        return best;
        uint Distance(uint tier) => tier >= target ? tier - target : target - tier;
    }

    /// <summary>主显示器当前刷新率（Hz，取整）。GetDC(NULL) 的设备就是主显示器；0/1 是"硬件默认"，等于没读到。</summary>
    public static uint? PrimaryDisplayRefreshHz()
    {
        if (!OperatingSystem.IsWindows()) return null;
        nint dc = 0;
        try
        {
            dc = GetDC(0);
            if (dc == 0) return null;
            int hz = GetDeviceCaps(dc, VertRefresh);
            return hz > 1 ? (uint)hz : null;
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) { return null; }
        finally
        {
            if (dc != 0) _ = ReleaseDC(0, dc);
        }
    }

    private const int VertRefresh = 116;

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint window);
    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint window, nint deviceContext);
    [DllImport("gdi32.dll")]
    private static extern int GetDeviceCaps(nint deviceContext, int index);
}
