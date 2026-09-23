namespace Periodica.Domain;

/// <summary>
/// 起点搜索里的一个候选（降采样样本上的读数）。<paramref name="WorstTile"/> 是 Δ_0 的最差瓦片；
/// <paramref name="StrideWorstTile"/> 是 Δ_stride 的最差瓦片，步长不在淡化窗口内或样本不够时为 null。
/// </summary>
public readonly record struct ResidualStartCandidate(ulong Start, double Global, double WorstTile, double? StrideWorstTile)
{
    /// <summary>排序键：样本上能看到的第一层量 max(M(Δ_0), M(Δ_stride))。</summary>
    public double SortKey => StrideWorstTile is double stride ? Math.Max(WorstTile, stride) : WorstTile;
}

/// <summary>
/// 起点搜索的评分（纯函数）：在锁定的解析周期内给每个采样相位打分，多个残差组逐相位合并。
/// 读样本帧、准入与排序（阈值在 ResidualMasking）、写记录都不在这里。
/// </summary>
public static class SeamMath
{
    /// <summary>
    /// <paramref name="wraps"/>[c] 是第 c 个采样相位的残差 Δ_0 = 样本[c + P/stride] − 样本[c]，按相位顺序排列。
    /// 第 c 个候选的起点是 c·stride；Δ_stride 就是下一个相位的 Δ_0，只在 stride 落在淡化窗口 [0, C) 内、
    /// 且下一个相位存在时才属于第一层的量。
    /// </summary>
    public static ResidualStartCandidate[] ScoreStarts(IReadOnlyList<LoopWrapResidual> wraps, uint stride, uint crossfadeFrames) =>
        [.. wraps.Select((wrap, c) => new ResidualStartCandidate((ulong)c * stride, wrap.GlobalRgbMae, wrap.WorstTileRgbMae,
            stride < crossfadeFrames && c + 1 < wraps.Count ? wraps[c + 1].WorstTileRgbMae : null))];

    /// <summary>
    /// 多个残差组共用一个起点：逐相位取各组整幅、瓦片、下一采样瓦片读数的最大值。
    /// 各组必须在同一相位网格上（调用方核对），准入与排序照旧作用在合并后的读数上。
    /// </summary>
    public static ResidualStartCandidate[] CombineGroups(IReadOnlyList<IReadOnlyList<ResidualStartCandidate>> groups) =>
        [.. Enumerable.Range(0, groups[0].Count).Select(index => new ResidualStartCandidate(groups[0][index].Start,
            groups.Max(group => group[index].Global),
            groups.Max(group => group[index].WorstTile),
            groups.Max(group => group[index].StrideWorstTile)))];
}
