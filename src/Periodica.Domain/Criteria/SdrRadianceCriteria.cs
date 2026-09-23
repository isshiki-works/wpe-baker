namespace Periodica.Domain;

/// <summary>纹理输入会不会把组输出反馈回自身（R5）。</summary>
public enum TextureFeedback
{
    /// <summary>普通纹理，不构成反馈。</summary>
    None,
    /// <summary>采样当前帧缓冲的保留纹理名：组输出直接反馈进自身。</summary>
    Framebuffer,
    /// <summary>其它 _rt_ 渲染目标：值域不可判。</summary>
    RenderTarget,
}

/// <summary>
/// SDR 辐射闭合证明的五条判据（R1 材质、R2 混合、R3 纹理输入、R4 标量、R5 反馈）里与 JSON、资源读取无关的部分：
/// 白名单与数值区间。逐层读场景/材质/运行时观测、写 checks 与说明文字在 Baker.Core 的 SdrRadianceClosure。
/// 任何一项认不出都判不通过——判据只回答"会不会被 RGBA8 组捕获 clip"。
/// </summary>
public static class SdrRadianceCriteria
{
    /// <summary>R1：内置 SDR 着色器，输出值域不超过输入值域，没有放大路径。工坊自定义着色器一律不在其中。</summary>
    private static readonly HashSet<string> SdrShaders = new(StringComparer.Ordinal)
        { "flat", "genericimage", "genericimage2", "genericimage3", "genericimage4", "solidlayer" };
    /// <summary>
    /// R1：已知不改变值域的 combo 键；出现任何其它键即判未知。键按大小写不敏感比较：渲染器
    /// （engine ShaderParser::PreShaderHeader）把每个 combo 键 toupper 后才写成 <c>#define</c>，
    /// 所以官方 materials/util/solidlayer_instance_*.json 里的小写 "version" 与 "VERSION" 是同一个宏。
    /// VERSION 在 genericimage/genericimage2 里只切换 g_Brightness·g_UserAlpha 与 g_Color4 两组标量乘法，
    /// genericimage3/4 不引用它；两条路径的值域都由 R4 的标量判据兜住。
    /// </summary>
    private static readonly HashSet<string> SafeCombos = new(StringComparer.OrdinalIgnoreCase) { "VERSION" };
    /// <summary>R2：alpha 凸组合，上界不升；additive 等一律不通过。</summary>
    private static readonly HashSet<string> SdrBlending = new(StringComparer.Ordinal) { "normal", "translucent" };
    /// <summary>R3：8bit 无符号解码格式；10bit/PQ/HLG 及任何未列出的格式都算未知。</summary>
    private static readonly HashSet<string> EightBitPixelFormats = new(StringComparer.Ordinal) {
        "yuv420p", "yuvj420p", "yuv422p", "yuvj422p", "yuv444p", "yuvj444p", "yuva420p",
        "nv12", "nv21", "gray", "rgb24", "bgr24", "rgba", "bgra", "argb", "abgr", "rgb0", "bgr0", "0rgb", "0bgr" };
    /// <summary>R5：采样当前帧缓冲的保留纹理名。</summary>
    private static readonly HashSet<string> FeedbackTextures = new(StringComparer.Ordinal)
        { "_rt_default", "_rt_FullFrameBuffer", "_rt_Backbuffer" };

    /// <summary>R4 的区间容差：分量落在 [−1e-9, 1 + 1e-9] 内算在 [0,1] 里。</summary>
    private const double UnitRangeTolerance = 1e-9;

    /// <summary>R1：运行时或材质定义里的着色器是不是内置 SDR 着色器（缺失即否）。</summary>
    public static bool IsBuiltInSdrShader(string? shader) => shader is not null && SdrShaders.Contains(shader);

    /// <summary>R1：材质 combo 键是否已知不改变值域。</summary>
    public static bool IsRangePreservingCombo(string key) => SafeCombos.Contains(key);

    /// <summary>R2：pass 混合模式是否为不抬高上界的凸组合（缺失即否）。</summary>
    public static bool IsRangePreservingBlend(string? blending) => blending is not null && SdrBlending.Contains(blending);

    /// <summary>R2：图层 colorBlendMode 只有普通模式 0 通过。</summary>
    public static bool IsNormalColorBlendMode(double mode) => mode == 0;

    /// <summary>R3：视频纹理解码出的像素格式是否为已知 8 位 SDR 格式（缺失即否）。</summary>
    public static bool IsEightBitSdrPixelFormat(string? pixelFormat) => pixelFormat is not null && EightBitPixelFormats.Contains(pixelFormat);

    /// <summary>R5：纹理名是否把帧缓冲或渲染目标接回本组。</summary>
    public static TextureFeedback Feedback(string texture) =>
        FeedbackTextures.Contains(texture) ? TextureFeedback.Framebuffer :
        texture.StartsWith("_rt_", StringComparison.Ordinal) ? TextureFeedback.RenderTarget : TextureFeedback.None;

    /// <summary>R4：一个分量是否有限且落在 [0,1]（含容差）。</summary>
    public static bool IsWithinUnitRange(double component) =>
        double.IsFinite(component) && component >= -UnitRangeTolerance && component <= 1 + UnitRangeTolerance;

    /// <summary>
    /// R4：把空格分隔的标量字面量（如 "1 0.5 0"）按不变文化解析成分量并追加进 <paramref name="components"/>。
    /// 有一个记号不是数字就返回 false，此时只留下已解析的那几个。
    /// </summary>
    public static bool TryParseComponents(string literal, List<double> components)
    {
        foreach (string token in literal.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!double.TryParse(token, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double component))
                return false;
            components.Add(component);
        }
        return true;
    }
}
