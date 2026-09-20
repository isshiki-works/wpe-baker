namespace Baker.Core;

/// <summary>One render's measured frame throughput, excluding startup and warmup from the rate.</summary>
internal sealed class FrameProgressEstimate
{
    private double? firstFrameSeconds;
    private ulong firstFrame;

    internal RenderProgress Update(ulong completed, ulong total, double elapsedSeconds)
    {
        if (total == 0 || completed == 0 || completed > total || !double.IsFinite(elapsedSeconds) || elapsedSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(completed));
        if (firstFrameSeconds is null) { firstFrameSeconds = elapsedSeconds; firstFrame = completed; }
        double measuredSeconds = elapsedSeconds - firstFrameSeconds.Value;
        double? remaining = completed == total ? 0
            : measuredSeconds >= 2 && completed > firstFrame
                ? measuredSeconds / (completed - firstFrame) * (total - completed) : null;
        return new("rendering", (double)completed / total, $"{completed} / {total} frames",
            elapsedSeconds, remaining);
    }
}
