using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>
/// 输出分辨率的取法。显式给了宽高就照用。没给时取本机主显示器物理分辨率（允许放大）：WPE 播放原作时按屏幕分辨率实时渲染，
/// 画布由渲染器按 WPE 的铺放方式（默认铺满居中裁切）映射到输出上。读不到显示器时退回场景作者画布
/// （general.orthogonalprojection，按用户属性求值，auto 画布取面积最大的图片）；两者都拿不到才用 1920×1080。
/// 自动取得的尺寸对齐到偶数，4:2:0 编码与打包裁剪都要求偶数边。
/// </summary>
public static class OutputResolution
{
    public const string Explicit = "explicit";
    public const string SceneCanvasFitDisplay = "scene_canvas_fit_display";
    public const string SceneCanvas = "scene_canvas";
    public const string Display = "display";
    public const string Fallback = "fallback";
    public const uint FallbackWidth = 1920, FallbackHeight = 1080;

    public const string Rule = "Explicit --width/--height win. Otherwise the primary display's current physical resolution is used, upscaling allowed " +
        "(Wallpaper Engine renders the original at screen resolution; the renderer maps the canvas onto it the way Wallpaper Engine does). " +
        "Without a readable display the scene's general.orthogonalprojection canvas after user properties is used (an auto projection uses its " +
        "largest image); 1920x1080 only when neither is available. Automatic sizes are aligned to even pixels.";

    /// <summary>一次取值的结果。Canvas* 是作者画布原值（透视或缺失时为 null），Display* 只在真的读到显示器时有值。</summary>
    public sealed record Choice(uint Width, uint Height, string Source, double? CanvasWidth, double? CanvasHeight, string CanvasBasis,
        uint? DisplayWidth = null, uint? DisplayHeight = null)
    {
        public JsonObject ToJson() => new()
        {
            ["width"] = Width, ["height"] = Height, ["source"] = Source,
            ["scene_canvas"] = CanvasWidth is double width && CanvasHeight is double height ? new JsonArray(width, height) : null,
            ["canvas_basis"] = CanvasBasis,
            ["display"] = DisplayWidth is uint displayWidth && DisplayHeight is uint displayHeight ? new JsonArray(displayWidth, displayHeight) : null,
            ["rule"] = Rule,
        };
    }

    // SceneCanvasFitDisplay 只剩旧 plan.settings 里的来源（旧规则按画布铺满屏幕且不放大），重新分析时照旧接受。
    public static bool IsKnownSource(string? source) => source is null or Explicit or SceneCanvasFitDisplay or SceneCanvas or Display or Fallback;

    /// <summary>
    /// 决定输出宽高。<paramref name="requestedWidth"/>/<paramref name="requestedHeight"/> 同为 0 表示未指定；
    /// 显式尺寸沿用 <paramref name="requestedSource"/>（重新分析时保留第一次取值的来源），没有就记 explicit。
    /// </summary>
    public static Choice Choose(JsonObject scene, JsonObject properties, uint requestedWidth, uint requestedHeight, string? requestedSource,
        Func<(uint Width, uint Height)?> display)
    {
        var (canvasWidth, canvasHeight, basis) = HybridVideoProjection.AuthoredCanvas(scene, properties);
        if (requestedWidth != 0 && requestedHeight != 0)
            return new(requestedWidth, requestedHeight, requestedSource ?? Explicit, canvasWidth, canvasHeight, basis);
        if (display() is { } screen && Even(screen.Width) is uint displayWidth && Even(screen.Height) is uint displayHeight)
            return new(displayWidth, displayHeight, Display, canvasWidth, canvasHeight, basis, screen.Width, screen.Height);
        if (Even(canvasWidth) is uint width && Even(canvasHeight) is uint height)
            return new(width, height, SceneCanvas, canvasWidth, canvasHeight, basis);
        return new(FallbackWidth, FallbackHeight, Fallback, canvasWidth, canvasHeight, basis);
    }

    // 渲染器接受的边长上限是 ushort（NativeRenderRunner.Raw 的既有校验），超出的画布不当作可用画布。
    private static uint? Even(double? value) =>
        value is double number && double.IsFinite(number) && number >= 2 && number <= ushort.MaxValue ? (uint)Math.Round(number) & ~1u : null;

    /// <summary>主显示器当前物理分辨率。临时切到按显示器感知 DPI 的线程上下文读取，读完恢复；切不过去就不给值，免得拿到缩放后的逻辑尺寸。</summary>
    public static (uint Width, uint Height)? PrimaryDisplay()
    {
        if (!OperatingSystem.IsWindows()) return null;
        nint previous = 0;
        try
        {
            previous = SetThreadDpiAwarenessContext(new nint(-4));
            int width = GetSystemMetrics(0), height = GetSystemMetrics(1);
            return previous != 0 && width > 0 && height > 0 ? ((uint)width, (uint)height) : null;
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) { return null; }
        finally
        {
            if (previous != 0) _ = SetThreadDpiAwarenessContext(previous);
        }
    }

    [DllImport("user32.dll")]
    private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
