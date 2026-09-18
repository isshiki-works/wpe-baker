namespace Baker.Core;

/// <summary>
/// foliagesway 一个 pass 的结构化方程（设计 design-sway-near-recurrence §1）。系数是这一层实际生效的 8 个
/// 频率系数：前 4 项进 x（sines），后 4 项进 y（csines），相位增量为 speed·c_i·t。系数从 shader 文本解析，
/// 已改写成按 g_Speed 分支的表达式时取这一层速度命中的那一支；0 表示该项已冻结。
/// </summary>
/// <param name="Shader">材质里的 shader 名（effects/foliagesway），覆盖文件写到 shaders/{Shader}.frag|.vert。</param>
/// <param name="Mode">0 = UV 摆动（片元，speeduv），1 = 顶点摆动（顶点，speed）。</param>
/// <param name="Speed">GPU 实际拿到的 float32 速度值（已按 float32 取整）。</param>
/// <param name="LayerWidth">图层像素宽（效果输入 rt 尺寸，g_Texture0Resolution.z）；读不到时为 null。</param>
/// <param name="ScaleX">图层自身与父链 scale 的乘积。</param>
public sealed record SwayModel(int OwnerLayerId, int EffectIndex, int PassIndex, string Shader, int Mode,
    string SpeedKey, double Speed, IReadOnlyList<double> Coefficients, double Strength, double Ratio, double Direction,
    double Power, double DirectionWeightX, double DirectionWeightY, double? LayerWidth, double? LayerHeight,
    double ScaleX, double ScaleY)
{
    /// <summary>stock foliagesway 的 8 个系数字面量（frag 34、36 行与 vert 54、56 行）。</summary>
    public static readonly double[] CanonicalCoefficients =
        [1, -0.16161616, 0.0083333, -0.00019841, -0.5, 0.041666666, -0.0013888889, 0.000024801587];

    /// <summary>8 项按最接近的简单分数命名，与设计文档、原型输出一致。</summary>
    public static readonly string[] TermNames = ["1", "16/99", "1/120", "1/5040", "1/2", "1/24", "1/720", "1/40320"];

    /// <summary>
    /// 每一项在输出像素上的振幅 (A_x, A_y)：前 4 项共用 A_x，后 4 项共用 A_y；mask 按最坏值 1。
    /// MODE 0：amp = strength²·0.005，aspect = texW/texH·ratio，(z, w) = rotateVec2((1/aspect, aspect), direction)，
    /// A_x = |z|·amp·texW·scaleX·outX，A_y = |w|·amp·texH·scaleY·outY。
    /// MODE 1：顶点位移 = Σsin·strength·100·weight·directionweights，weight ≤ 1，A_x = |strength·100·dw.x|·scaleX·outX。
    /// 图层尺寸读不到（MODE 0）时返回 null：振幅只用于冻结项漂移的次序与报告，不影响闭合。
    /// </summary>
    public (double X, double Y)? AmplitudeOutputPixels(double outputPerSceneX, double outputPerSceneY)
    {
        if (Mode == 1)
            return (Math.Abs(Strength * 100 * DirectionWeightX) * Math.Abs(ScaleX) * outputPerSceneX,
                Math.Abs(Strength * 100 * DirectionWeightY) * Math.Abs(ScaleY) * outputPerSceneY);
        if (LayerWidth is not double width || LayerHeight is not double height || width <= 0 || height <= 0) return null;
        double amp = Strength * Strength * 0.005;
        double aspect = width / height * Ratio;
        if (!double.IsFinite(aspect) || aspect == 0) return null;
        double z = 1 / aspect * Math.Cos(Direction) - aspect * Math.Sin(Direction);
        double w = 1 / aspect * Math.Sin(Direction) + aspect * Math.Cos(Direction);
        return (Math.Abs(z) * amp * width * Math.Abs(ScaleX) * outputPerSceneX,
            Math.Abs(w) * amp * height * Math.Abs(ScaleY) * outputPerSceneY);
    }

    /// <summary>系数是否正好是 stock 字面量（决定未解析说明沿用旧文案还是列出实际系数）。</summary>
    public bool HasCanonicalCoefficients => Coefficients.SequenceEqual(CanonicalCoefficients);
}

/// <summary>
/// 摆动改频的开关参数。输出比例 = 输出像素 / 场景可见单位，只影响振幅换算（冻结项漂移的次序与报告）。
/// LoopLengthMaximumSeconds 是生效上限；VideoLimit 非空时它已按内嵌视频 2 GiB 上限收紧过，记录用户原值与收紧依据。
/// </summary>
public sealed record SwayRetimeOptions(double LoopLengthMaximumSeconds, double OutputPerSceneX = 1, double OutputPerSceneY = 1,
    EmbeddedVideoLoopLimit? VideoLimit = null, RetimeProfile? Profile = null)
{
    /// <summary>档位给的观感改动预算（百分比）；null = 质量档或没选档，求解器取改动最小的解。</summary>
    public double? BudgetPercent => Profile?.BudgetPercent;

    /// <summary>
    /// 摆动改频的默认状态：三档都开（设计 `design-presets-simplify.md` §3——档位的观感预算管的就是摆动求解，
    /// 默认关等于默认没有档位）。CLI 的 `--sway-retime` 与界面高级区的勾选框都从这里取默认值，
    /// 要关掉就显式传 `--sway-retime off` 或取消勾选。
    /// </summary>
    public const bool OnByDefault = true;

    /// <summary>--loop-length-max 的默认值（秒）。</summary>
    public const double DefaultLoopLengthMaximumSeconds = 600;

    /// <summary>--loop-length-max 允许的上限（秒）。</summary>
    public const double MaximumLoopLengthSeconds = 3600;
}
