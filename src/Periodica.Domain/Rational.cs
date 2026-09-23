using System.Numerics;

namespace Periodica.Domain;

/// <summary>A positive rational duration, used where an exact frame-grid calculation is required.</summary>
public readonly record struct CommonLoopRational
{
    public long Numerator { get; }
    public long Denominator { get; }

    public CommonLoopRational(long numerator, long denominator = 1)
    {
        if (numerator <= 0 || denominator <= 0) throw new ArgumentOutOfRangeException(nameof(numerator), "A duration must be positive.");
        long divisor = FrameGrid.GreatestCommonDivisor(numerator, denominator);
        Numerator = numerator / divisor;
        Denominator = denominator / divisor;
    }

    public double ToSeconds() => (double)Numerator / Denominator;
}

/// <summary>输出帧网格上的整数算术。全仓的最大公约数只在这里实现一次（泛型，long / ulong / UInt128 共用）。</summary>
public static class FrameGrid
{
    public static T GreatestCommonDivisor<T>(T left, T right) where T : IBinaryInteger<T>
    {
        while (right != T.Zero) (left, right) = (right, left % right);
        return left;
    }

    /// <summary>最小公倍数；超出 ulong 时抛 <see cref="OverflowException"/>。</summary>
    public static ulong LeastCommonMultiple(ulong left, ulong right)
    {
        UInt128 result = (UInt128)(left / GreatestCommonDivisor(left, right)) * right;
        if (result > ulong.MaxValue) throw new OverflowException();
        return (ulong)result;
    }

    /// <summary>frames 个输出帧的秒数 = frames × fps_den / fps_num（双精度，运算顺序固定）。</summary>
    public static double Seconds(ulong frames, uint fpsNumerator, uint fpsDenominator) =>
        (double)frames * fpsDenominator / fpsNumerator;
}
