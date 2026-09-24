namespace Baker.Core;

/// <summary>
/// 分析编排的显式搜索空间：交互策略 × 预设档 × 布局 × 状态。各维按数组顺序尝试，这个顺序就是运行目录下 1、2、3… 的编号顺序。
/// 状态维在运行中才知道有几个（母 plan 识别出昼夜选择器后按它的 states 展开），所以只记"哪些交互策略要展开"和"要不要逐状态导出"。
/// </summary>
/// <param name="Interactions">先是请求的交互策略，后面是它失败时依次验证的替代策略（keep → fixed → off）。</param>
/// <param name="Presets">从请求的档位往省电方向；给了观感改动预算（高级覆盖）时只试请求的那一档。</param>
/// <param name="Layouts">布局显式给定时只试它；否则先全幅、不行再分层。</param>
/// <param name="ExpandStates">请求没指定状态时，识别出的每个状态各规划一次、挑最好的（keep 不展开，见 <see cref="Expands"/>）。</param>
/// <param name="ExportStates">调用方要逐状态子 plan（CLI 的 --daytime-split on）：选定结果识别出状态时，每个状态按原请求再分析一次并落盘。</param>
internal sealed record SearchSpace(string[] Interactions, string[] Presets, string[] Layouts, bool ExpandStates, bool ExportStates)
{
    internal static readonly string[] Tiers = ["quality", "balanced", "efficiency"];

    internal static SearchSpace Of(HybridAnalyzeRequest request, bool exportStates)
    {
        string requested = request.Preset ?? "balanced", interaction = request.Interaction ?? "fixed";
        int start = Array.IndexOf(Tiers, requested);
        if (start < 0 && requested != RetimeProfile.Compatibility || interaction is not ("keep" or "fixed" or "off")) throw new InvalidDataException("Invalid preset or interaction policy.");
        // 兼容档只能手动选，只试它自己：不进自动回退链，默认运行的档位序列不变。
        string[] tiers = start < 0 ? [requested] : Tiers[start..];
        return new(interaction switch { "keep" => ["keep", "fixed", "off"], "fixed" => ["fixed", "off"], _ => ["off"] },
            request.RetimeBudgetPercent is null ? tiers : [tiers[0]],
            request.LayoutExplicit ? [request.VideoLayout] : ["full_frame", "layered"],
            request.DaytimeState is null, exportStates && request.DaytimeSplit);
    }

    /// <summary>该交互策略下是否按状态展开：keep 保留原交互，不拆状态。</summary>
    internal bool Expands(string interaction) => ExpandStates && interaction != "keep";

    /// <summary>
    /// 默认预算 = 现状最坏上限：每个交互策略把所有档位 × 布局试完（off 另加一轮布局做取舍测量），展开的策略每个状态再各试一遍档位 × 布局，
    /// 逐状态导出每个状态一次。
    /// </summary>
    internal CallBudget Budget()
    {
        int cells = Presets.Length * Layouts.Length;
        return new(Interactions.Length * cells + (Interactions.Contains("off") ? Layouts.Length : 0),
            Interactions.Count(Expands) * cells + (ExportStates ? 1 : 0));
    }
}
