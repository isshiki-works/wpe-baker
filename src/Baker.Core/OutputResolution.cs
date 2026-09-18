using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>
/// 输出分辨率的取法。显式给了宽高就照用。没给时以场景作者画布（general.orthogonalprojection，按用户属性求值，
/// auto 画布取面积最大的图片，与投影描述同一取法）按本机主显示器物理分辨率铺满缩放：
/// s = min(1, max(屏宽/画布宽, 屏高/画布高))，输出 = 画布×s——WPE 播放原作时按屏幕分辨率渲染，比屏幕大的画布出片没有意义，
/// 比屏幕小的画布也不放大。读不到显示器时退回画布原尺寸；透视场景没有作者画布，画布缺失或非法时直接用屏幕物理分辨率；
/// 两者都拿不到才用 1920×1080。自动取得的尺寸对齐到偶数，4:2:0 编码与打包裁剪都要求偶数边。
/// </summary>
public static class OutputResolution
{
    public const string Explicit = "explicit";
    public const string SceneCanvasFitDisplay = "scene_canvas_fit_display";
    public const string SceneCanvas = "scene_canvas";
    public const string Display = "display";
    public const string Fallback = "fallback";
    public const uint FallbackWidth = 1920, FallbackHeight = 1080;

    public const string Rule = "Explicit --width/--height win. Otherwise the scene's general.orthogonalprojection canvas after user properties " +
        "(an auto projection uses its largest image, the same rule as the projection description) is scaled to cover the primary display's current " +
        "physical resolution without upscaling: s = min(1, max(display width / canvas width, display height / canvas height)), size = canvas x s. " +
        "Without a readable display the canvas size is used as is; a perspective scene or a missing/invalid canvas uses the display resolution; " +
        "1920x1080 only when neither is available. Automatic sizes are aligned to even pixels.";

    /// <summary>
    /// 一次取值的结果。Canvas* 是作者画布原值（透视或缺失时为 null），Display* 只在真的读到显示器时有值，
    /// FitScale 是铺满屏幕的缩放系数 s（只在画布按屏幕缩放时有值）。
    /// </summary>
    public sealed record Choice(uint Width, uint Height, string Source, double? CanvasWidth, double? CanvasHeight, string CanvasBasis,
        uint? DisplayWidth = null, uint? DisplayHeight = null, double? FitScale = null)
    {
        /// <summary>画布比铺满屏幕所需的还小、按 s=1 封顶没有放大。</summary>
        public bool CappedAtCanvas => FitScale is double scale && CanvasWidth is double width && CanvasHeight is double height &&
            DisplayWidth is uint displayWidth && DisplayHeight is uint displayHeight &&
            scale >= 1 && Math.Max(displayWidth / width, displayHeight / height) > 1;

        public JsonObject ToJson() => new()
        {
            ["width"] = Width, ["height"] = Height, ["source"] = Source,
            ["scene_canvas"] = CanvasWidth is double width && CanvasHeight is double height ? new JsonArray(width, height) : null,
            ["canvas_basis"] = CanvasBasis,
            ["display"] = DisplayWidth is uint displayWidth && DisplayHeight is uint displayHeight ? new JsonArray(displayWidth, displayHeight) : null,
            ["fit_scale"] = FitScale,
            ["capped_at_canvas"] = FitScale is null ? null : CappedAtCanvas,
            ["rule"] = Rule,
        };
    }

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
        (uint Width, uint Height)? screen = display() is { } read && read.Width > 0 && read.Height > 0 ? read : null;
        bool canvasUsable = Even(canvasWidth) is not null && Even(canvasHeight) is not null;
        if (canvasUsable && screen is { } fitTo)
        {
            // 铺满（cover）取两个比值里大的那个，这样两边都不小于屏幕；再以 1 封顶，画布比屏幕小时不放大。
            double scale = Math.Min(1, Math.Max(fitTo.Width / canvasWidth!.Value, fitTo.Height / canvasHeight!.Value));
            return new(Even(canvasWidth.Value * scale) ?? 2, Even(canvasHeight.Value * scale) ?? 2, SceneCanvasFitDisplay,
                canvasWidth, canvasHeight, basis, fitTo.Width, fitTo.Height, scale);
        }
        if (canvasUsable)
            return new(Even(canvasWidth)!.Value, Even(canvasHeight)!.Value, SceneCanvas, canvasWidth, canvasHeight, basis);
        if (screen is { } direct && Even(direct.Width) is uint displayWidth && Even(direct.Height) is uint displayHeight)
            return new(displayWidth, displayHeight, Display, canvasWidth, canvasHeight, basis, direct.Width, direct.Height);
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
