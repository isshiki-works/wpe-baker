namespace Baker.Core;

/// <summary>Frame-rate numerator and denominator retained without decimal rounding.</summary>
public sealed record RationalFrameRate(uint Numerator, uint Denominator)
{
    public double FramesPerSecond => (double)Numerator / Denominator;
}
