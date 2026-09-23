// SDR 辐射闭合判据 R1–R5 的 L0 表：Domain 的 SdrRadianceCriteria 逐条白名单与区间。
// L1 的 SdrRadianceClosureChecks 走真实文件与 plan 流程，这里只钉判据本身（每行一条输入 → 通过与否）。
using Xunit;

[Trait("Layer", "L0")]
public class SdrRadianceCriteriaTests
{
    // R1 着色器：只认六个内置 SDR 着色器，大小写敏感，缺失即否。
    [Theory]
    [InlineData("flat", true)]
    [InlineData("genericimage", true)]
    [InlineData("genericimage2", true)]
    [InlineData("genericimage3", true)]
    [InlineData("genericimage4", true)]
    [InlineData("solidlayer", true)]
    [InlineData("GenericImage3", false)]
    [InlineData("genericimage5", false)]
    [InlineData("workshop/123/effects/glow", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void R1Shader(string? shader, bool expected) => Assert.Equal(expected, SdrRadianceCriteria.IsBuiltInSdrShader(shader));

    // R1 combo：只有 VERSION，大小写不敏感（渲染器把 combo 键 toupper 成宏）。
    [Theory]
    [InlineData("VERSION", true)]
    [InlineData("version", true)]
    [InlineData("Version", true)]
    [InlineData("VERSIONS", false)]
    [InlineData("BLENDMODE", false)]
    [InlineData("", false)]
    public void R1Combo(string key, bool expected) => Assert.Equal(expected, SdrRadianceCriteria.IsRangePreservingCombo(key));

    // R2 pass 混合：normal/translucent 通过，additive 等与缺失不通过。
    [Theory]
    [InlineData("normal", true)]
    [InlineData("translucent", true)]
    [InlineData("additive", false)]
    [InlineData("Normal", false)]
    [InlineData(null, false)]
    public void R2Blend(string? blending, bool expected) => Assert.Equal(expected, SdrRadianceCriteria.IsRangePreservingBlend(blending));

    // R2 colorBlendMode：只有 0。
    [Theory]
    [InlineData(0.0, true)]
    [InlineData(1.0, false)]
    [InlineData(1e-12, false)]
    [InlineData(double.NaN, false)]
    public void R2ColorBlendMode(double mode, bool expected) => Assert.Equal(expected, SdrRadianceCriteria.IsNormalColorBlendMode(mode));

    // R3 视频解码像素格式：8 位 SDR 名单内通过，10 位与未列出的不通过。
    [Theory]
    [InlineData("yuv420p", true)]
    [InlineData("nv12", true)]
    [InlineData("0bgr", true)]
    [InlineData("yuva420p", true)]
    [InlineData("yuv420p10le", false)]
    [InlineData("p010le", false)]
    [InlineData("YUV420P", false)]
    [InlineData(null, false)]
    public void R3PixelFormat(string? format, bool expected) => Assert.Equal(expected, SdrRadianceCriteria.IsEightBitSdrPixelFormat(format));

    // R4 分量区间：[0,1] 含 1e-9 容差，非有限值一律不通过。
    [Theory]
    [InlineData(0.0, true)]
    [InlineData(1.0, true)]
    [InlineData(0.5, true)]
    [InlineData(-1e-9, true)]
    [InlineData(1 + 1e-9, true)]
    [InlineData(-2e-9, false)]
    [InlineData(1 + 2e-9, false)]
    [InlineData(1.5, false)]
    [InlineData(double.NaN, false)]
    [InlineData(double.PositiveInfinity, false)]
    public void R4Component(double component, bool expected) => Assert.Equal(expected, SdrRadianceCriteria.IsWithinUnitRange(component));

    // R4 字面量：空格分隔、不变文化；有一个记号不是数字即失败，已解析的留下。
    [Theory]
    [InlineData("1 0.5 0", true, new[] { 1.0, 0.5, 0.0 })]
    [InlineData("  0.25   1 ", true, new[] { 0.25, 1.0 })]
    [InlineData("1e-3", true, new[] { 1e-3 })]
    [InlineData("", true, new double[0])]
    [InlineData("1 abc 0", false, new[] { 1.0 })]
    [InlineData("0,5", false, new double[0])]
    public void R4Literal(string literal, bool ok, double[] expected)
    {
        var components = new List<double>();
        Assert.Equal(ok, SdrRadianceCriteria.TryParseComponents(literal, components));
        Assert.Equal(expected, components);
    }

    // R5 反馈：三个帧缓冲保留名是 Framebuffer，其它 _rt_ 前缀是 RenderTarget，其余不构成反馈。
    [Theory]
    [InlineData("_rt_default", TextureFeedback.Framebuffer)]
    [InlineData("_rt_FullFrameBuffer", TextureFeedback.Framebuffer)]
    [InlineData("_rt_Backbuffer", TextureFeedback.Framebuffer)]
    [InlineData("_rt_imageLayerComposite_1_a", TextureFeedback.RenderTarget)]
    [InlineData("_rt_fullframebuffer", TextureFeedback.RenderTarget)]
    [InlineData("clip", TextureFeedback.None)]
    [InlineData("rt_default", TextureFeedback.None)]
    public void R5Feedback(string texture, TextureFeedback expected) => Assert.Equal(expected, SdrRadianceCriteria.Feedback(texture));
}
