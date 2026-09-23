namespace Baker.Core;

/// <summary>
/// C2.4a 起只剩转发器（登记在 runs/C2-plan/forwarders.txt，C3 删）：进程在 <see cref="FfmpegTool"/>，
/// 取帧在 <see cref="FrameAccess"/>，探流核对在 <see cref="VerifyEncoded"/>。C2.4b 起只剩 EmbeddedVideoBudgetJson 一个调用方。
/// </summary>
public static class EncodedQualityValidator
{
    internal static Task<string> RunAsync(string exe, NativeTools tools, string[] args, CancellationToken token) =>
        new FfmpegTool(tools).RunCapturedAsync(exe, args, token);
}
