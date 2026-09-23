namespace Baker.Core;

/// <summary>
/// 单次分析的调用预算：固定部分 + 每个状态的部分（状态数取编排迄今展开过的最多状态数）。默认值由 <see cref="SearchSpace.Budget"/> 给出。
/// 每次调用记一格（阶段 | 交互 | 档位 | 布局 | 状态）：同一格记两次或总数超过上限，都说明编排自己出了错，抛内部错误；调用在记账之后才发出。
/// </summary>
internal sealed class CallBudget(int fixedCalls, int perState)
{
    private readonly HashSet<string> cells = new(StringComparer.Ordinal);

    internal int Limit(int states) => fixedCalls + perState * states;

    internal void Charge(string cell, int states)
    {
        if (!cells.Add(cell)) throw new InvalidOperationException("Internal error: analysis cell requested twice: " + cell);
        if (cells.Count > Limit(states))
            throw new InvalidOperationException($"Internal error: analysis call budget exceeded ({cells.Count} > {Limit(states)}) at {cell}.");
    }
}
