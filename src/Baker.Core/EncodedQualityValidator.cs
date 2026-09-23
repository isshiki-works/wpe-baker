namespace Baker.Core;

/// <summary>
/// C2.4a 起只剩转发器（登记在 runs/C2-plan/forwarders.txt，C3 删）：进程在 <see cref="FfmpegTool"/>，
/// 取帧在 <see cref="FrameAccess"/>，探流核对在 <see cref="VerifyEncoded"/>。留着只为本步不许改的调用方
/// （EmbeddedVideoBudget、EncodedLoopValidator 的非探流部分）。
/// </summary>
public static class EncodedQualityValidator
{
    internal static Task<List<byte[]>> DecodeExactFramesAsync(string file, NativeTools tools, ulong first, ulong count,
        int width, int height, uint numerator, uint denominator, string? filterAfter, CancellationToken token) =>
        FrameAccess.DecodeExactFramesAsync(file, new FfmpegTool(tools), first, count, width, height, numerator, denominator, filterAfter, token);

    internal static Task<byte[]> RunFfmpegBytesAsync(NativeTools tools, IEnumerable<string> arguments, long expectedBytes,
        CancellationToken token) => new FfmpegTool(tools).RunBytesAsync(arguments, expectedBytes, token);

    internal static Task<string> RunAsync(string exe, NativeTools tools, string[] args, CancellationToken token) =>
        new FfmpegTool(tools).RunCapturedAsync(exe, args, token);
}
