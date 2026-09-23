namespace Baker.Core.Analysis.ShaderClock;

/// <summary>
/// 一条规则对一个 pass 的裁定：产出的周期分量与未解析项。两者都为空表示已证明这个 pass 没有运动
/// （停住的时钟、零振幅、关闭的分支），它不约束循环。规则不认领时处理器返回 null，不产出裁定。
/// </summary>
internal sealed record ShaderVerdict(IReadOnlyList<ShaderPeriodComponent> Components, IReadOnlyList<ShaderTemporalUnresolved> Unresolved)
{
    public static ShaderVerdict NoMotion { get; } = new([], []);

    public static ShaderVerdict Of(ShaderTemporalUnresolved unresolved) => new([], [unresolved]);

    public static ShaderVerdict Of(params ShaderPeriodComponent[] components) => new(components, []);
}
