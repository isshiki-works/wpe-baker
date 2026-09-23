namespace Baker.Core;

/// <param name="Product">成品视频。</param>
/// <param name="LoopFrames">成品的总帧数，决定一遍顺序解还是按抽样帧取窗口（<see cref="QualityGate.UsesFrameWindows"/>）。</param>
/// <param name="Samples">抽样帧号，升序（<see cref="QualityGate.SampleFrames"/>）。</param>
/// <param name="WorkDirectory">日志与中间文件的目录；文件名都以 <paramref name="Stem"/> 开头。</param>
internal sealed record QualityRequest(string Product, ulong LoopFrames, IReadOnlyList<ulong> Samples, uint FpsNumerator,
    uint FpsDenominator, QualityReference Reference, string WorkDirectory, string Stem);

/// <summary>成品要对照的原作画面。</summary>
internal abstract record QualityReference;

/// <summary>
/// 无损 master 路线：master 是整张捕获、成品是裁剪后的画面，master 要走完成品编码用的同一条滤镜（输入标签 <c>[0:v]</c>、
/// 输出标签 <c>[packed]</c>）才有可比性。
/// </summary>
internal sealed record MasterQualityReference(string Video, string EncodeFilter) : QualityReference;

/// <summary>
/// GPU 直编路线：没有 master，参照是按抽样帧顺序排好的 rgb24 原始帧（尺寸 = 成品尺寸，透明组左 RGB 右 alpha），尚未转 BT.709。
/// 由 <paramref name="WriteRgb24"/> 写进比对方给的流；比对方可以让它与成品解码同时跑（参照要读原帧、缩放、排布局，8K 约 2 s）。
/// </summary>
internal sealed record FramesQualityReference(Func<Stream, CancellationToken, Task> WriteRgb24, int Width, int Height) : QualityReference;

/// <param name="DecodeScope"><see cref="QualityGate.SingleDecodeScope"/> 或 <see cref="QualityGate.FrameWindowsScope"/>，原样写进 bake.json。</param>
internal sealed record QualityReport(double? Ssim, double? Psnr, string DecodeScope);
