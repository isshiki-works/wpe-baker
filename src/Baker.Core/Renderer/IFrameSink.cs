namespace Baker.Core;

/// <summary>
/// 原始帧接收器。<see cref="NativeRenderRunner.RenderRawAsync(RenderRequest, IFrameSink?, CancellationToken)"/> 给了它，
/// 渲染器就改走 stdout 出帧（raw_stdout），帧按序交给它、不再落 frames.rgba。为 C2.3c 的 P2（合成比较不落盘）预留。
/// </summary>
internal interface IFrameSink
{
    /// <summary>第 <paramref name="index"/> 帧（从 0 起、连续）的 RGBA8，长度正好 宽×高×4；调用返回后缓冲区会被复用。</summary>
    ValueTask WriteFrameAsync(ulong index, ReadOnlyMemory<byte> rgba, CancellationToken token);
}
