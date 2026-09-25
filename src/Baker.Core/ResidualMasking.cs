using System.Globalization;
using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>残差掩盖第一层的全分辨率判定结果（见 <see cref="ResidualMasking.FirstLayer"/>）。</summary>
/// <param name="Passed">max_k 最差瓦片 ≤ 48/255 且 Δ_0 整幅 ≤ 2/255。</param>
/// <param name="MaximumWorstTileRgbMae">淡化窗口内所有 Δ_k 的最差瓦片 RGB MAE 的最大值。</param>
/// <param name="MaximumWorstTileFrame">取到最大值的 k。</param>
/// <param name="HardCutGlobalRgbMae">Δ_0 的整幅 RGB MAE。</param>
public readonly record struct ResidualFirstLayer(bool Passed, double MaximumWorstTileRgbMae, int MaximumWorstTileFrame,
    int WorstTileX, int WorstTileY, double HardCutGlobalRgbMae);

/// <summary>
/// 解析周期 + 残差掩盖规则。周期只能来自解析（CommonLoopSolver 候选）；未解析的时间分量必须逐条
/// 拿出"数学上随机"的证明，才允许在接缝处用固定窗口的整帧交叉淡化掩盖其残差：脚本随机重启的精灵、满足平稳随机判据
/// 的粒子系统。位移类分量（foliagesway 这类 UV/顶点抖动）即使幅度有界也不可掩盖——人眼定标判定淡化后位置跳变明显，
/// 只能解析闭合或保留实时。拿不出证明的，整个候选被拒绝并报出具体是哪一层、哪个机制、缺什么。
/// 随机精灵与粒子的画布占比只记录、不裁决：层包围盒对带透明边的精灵图集高估几十倍，作预过滤没有意义，
/// 真正把关的是全分辨率 master 上的第一层接缝判据（淡化窗口内的残差 Δ_k），淡化之后另有一道实现自检。
/// 这里只做判据与度量，不做任何相似度求周期、不做局部接缝修复。
/// </summary>
public static class ResidualMasking
{
    /// <summary>成品 bake.json / plan 中记录的接缝策略名。</summary>
    public const string SeamPolicy = "analytic_period_with_residual_masking";

    /// <summary>
    /// 第一层的面积型上限：硬切残差 Δ_0 = f[P] − f[0] 的整幅 RGB MAE，0..255 通道尺度。瓦片上限管不住
    /// "每块都不大、但整屏都在"的重影，所以另设这一项。它和 <see cref="MaximumResidualTileRgbMae255"/>
    /// 是残差掩盖仅有的两个需要人眼定标的数。
    /// </summary>
    public const double MaximumSeamRgbMae255 = 2.0;

    /// <summary>
    /// 第一层（残差掩盖唯一的裁决）：淡化窗口内每个 k∈[0,C) 的残差 Δ_k = f[P+k] − f[k] 取 64px 最差瓦片
    /// RGB MAE，最大值不得超过这个数，0..255 通道尺度。淡化之后观众能看到的只剩三样，全部由 |Δ_k| 决定：
    /// 重影峰值 min(w,1−w)·|Δ_k| ≤ 24/255；接缝那一步比原作多出 |Δ_0|/(C+1) ≤ 1.92/255；淡化段每一步多出
    /// ≤ |Δ|/(C+1) + ½·|Δ_k − Δ_{k−1}|（后一项是残差的帧间变化率）。三者都与场景普通帧间步进的快慢无关，
    /// 所以不再和普通步进 p95 取小。需要人眼定标。
    /// </summary>
    public const double MaximumResidualTileRgbMae255 = 48.0;

    /// <summary>
    /// 淡化实现自检的取整余量，0..255 通道尺度。自检比的是成品淡化段每一步相对原作参照步进多出的量与由 Δ_k
    /// 精确推导的上界；两边在实数域逐像素满足恒等式，只差淡化输出的 8 位取整（blend 表达式把浮点结果截断写回整数，
    /// 每帧误差在 [−1, 0] 级内，相邻两帧之差不超过 1 级，瓦片平均也不超过 1 级；平坦区实测能顶到 0.996）。
    /// 判定在整数域上做、含边界。这不是用户可调判据：超出就是淡化实现错了。
    /// </summary>
    public const double CrossfadeSelfCheckRounding255 = 1.0;

    /// <summary>
    /// 起点回退：全分辨率第一层在某个起点上被拒时，按起点排序依次换下一个候选重渲 master 再测，
    /// 最多尝试这么多个起点（含第一个）。降采样排序键只看得到 k=0 与 k=stride，看不到淡化窗口后段的单帧尖峰，
    /// 所以排序只决定尝试顺序，放行与否只看全分辨率读数。这是尝试次数，不是阈值。
    /// </summary>
    public const int MaximumStartAttempts = 8;

    /// <summary>
    /// 起点搜索的窗口上限，2 个解析周期。候选起点只由周期与采样步长决定（共 P/stride 个），每个候选比较的是
    /// (s, s+P)，全部落在前 2P 帧里；再宽的窗口不会引入任何新候选，只是白渲染帧。
    /// </summary>
    public const int SearchWindowPeriods = 2;

    /// <summary>随机精灵"两端同一静止状态"的替代判据：该层可定位区域内的残差上限。</summary>
    public const double MaximumSpriteRestRgbMae255 = 1.0;

    /// <summary>接缝残差评估使用的瓦片边长（1080p 短边下；实际按输出短边等比缩放，见 GroupRenderScheduler.TileScale）。</summary>
    public const int SeamTileSize = 64;

    /// <summary>接缝整帧交叉淡化窗口，固定 0.4 秒。</summary>
    public const double CrossfadeSeconds = 0.4;

    /// <summary>起点搜索的采样步长上限（源帧）与样本宽度（像素，1080p 短边下，同上缩放）。实际步长见 <see cref="StartSearchStride"/>。</summary>
    public const uint StartSearchSampleStride = 16;
    public const uint StartSearchSampleWidth = 512;

    /// <summary>
    /// 起点搜索实际使用的采样步长：解析周期帧数与步长上限的最大公约数，保证候选起点 s 与 s+P 都落在样本网格上。
    /// 周期不是 16 的倍数时（120 fps 下 1 秒 = 120 帧、60 fps 下 1800 帧）步长缩到 8、4、2 或 1，只会多出候选，
    /// 不会因为对齐问题拒绝。
    /// </summary>
    public static uint StartSearchStride(ulong periodFrames)
    {
        if (periodFrames == 0) throw new ArgumentException("解析周期帧数必须为正。");
        ulong a = periodFrames, b = StartSearchSampleStride;
        while (b != 0) (a, b) = (b, a % b);
        return (uint)a;
    }

    /// <summary>
    /// foliagesway 的 UV 位移幅度：着色器把八项正弦和乘以 amp = strength^2 * 0.005 写进纹理坐标，
    /// 其中四项 sines 与四项 csines 分别构成两个坐标轴的和，单轴峰值上界取四项各自幅度之和。
    /// </summary>
    public const double FoliageSwayAmplitudeFactor = 0.005;
    public const int FoliageSwaySineTerms = 4;

    public static double FoliageSwayPeakOffsetFraction(double strength) =>
        strength * strength * FoliageSwayAmplitudeFactor * FoliageSwaySineTerms;

    /// <summary>0.4 秒对应的整帧数，向上取整到至少一帧。</summary>
    public static uint CrossfadeFrames(uint fpsNumerator, uint fpsDenominator)
    {
        if (fpsNumerator == 0 || fpsDenominator == 0) throw new ArgumentException("交叉淡化需要正的有理帧率。");
        double frames = CrossfadeSeconds * fpsNumerator / fpsDenominator;
        return (uint)Math.Max(1, Math.Round(frames, MidpointRounding.AwayFromZero));
    }

    /// <summary>阈值常量的机器可读副本，写进 plan 与 bake.json 供核对。</summary>
    public static JsonObject Thresholds() => new()
    {
        ["seam_rgb_mae_255"] = MaximumSeamRgbMae255,
        ["residual_worst_tile_rgb_mae_255"] = MaximumResidualTileRgbMae255,
        ["seam_tile_size"] = SeamTileSize,
        ["first_layer"] = "淡化窗口内每个 k∈[0,C) 的残差 Δ_k = f[P+k] − f[k] 取 64px 最差瓦片 RGB MAE，最大值 ≤ 48/255；" +
            "硬切残差 Δ_0 的整幅 RGB MAE ≤ 2/255。这是残差掩盖唯一的裁决，与场景普通帧间步进无关。",
        ["crossfade_self_check"] = "淡化实现自检，不是判据：成品淡化段每一步相对原作参照步进多出的量，逐瓦片不得超过由 Δ_k " +
            "推导的上界加 1/255 取整余量；超出说明淡化实现错了，按内部错误抛出。",
        ["crossfade_self_check_rounding_255"] = CrossfadeSelfCheckRounding255,
        ["sprite_rest_region_rgb_mae_255"] = MaximumSpriteRestRgbMae255,
        ["crossfade_seconds"] = CrossfadeSeconds
    };

    /// <summary>
    /// 第一层判定。<paramref name="residuals"/> 是淡化窗口内按 k 排列的 Δ_k（第 0 项是硬切残差）：
    /// 对所有 k 取最差瓦片的最大值（位移型残差在窗口里可能变大，不能只看 k=0），整幅只看 Δ_0。
    /// </summary>
    public static ResidualFirstLayer FirstLayer(IReadOnlyList<LoopWrapResidual> residuals)
    {
        ArgumentNullException.ThrowIfNull(residuals);
        if (residuals.Count == 0) throw new ArgumentException("第一层至少需要硬切残差 Δ_0。", nameof(residuals));
        int peak = 0;
        for (int k = 1; k < residuals.Count; ++k)
            if (residuals[k].WorstTileRgbMae > residuals[peak].WorstTileRgbMae) peak = k;
        LoopWrapResidual worst = residuals[peak];
        double global = residuals[0].GlobalRgbMae;
        return new(worst.WorstTileRgbMae <= MaximumResidualTileRgbMae255 && global <= MaximumSeamRgbMae255,
            worst.WorstTileRgbMae, peak, worst.WorstTileX, worst.WorstTileY, global);
    }

    /// <summary>
    /// 起点搜索的准入：样本上整幅 Δ_0 ≤ 2/255。排序键（降采样估算的瓦片量）只用于排序、不做准入——
    /// 它看不到淡化窗口后段的单帧尖峰，拿它挡候选会把全分辨率能过的起点排除在外。
    /// </summary>
    public static bool StartCandidateAdmitted(ResidualStartCandidate candidate) =>
        candidate.Global <= MaximumSeamRgbMae255;

    /// <summary>起点候选排序：先准入的，再按排序键 max(Δ_0, Δ_stride) 从小到大，再按整幅与起点帧打破平局。</summary>
    public static ResidualStartCandidate[] OrderStartCandidates(IEnumerable<ResidualStartCandidate> candidates) =>
        [.. candidates.OrderBy(x => StartCandidateAdmitted(x) ? 0 : 1).ThenBy(x => x.SortKey)
            .ThenBy(x => x.Global).ThenBy(x => x.Start)];

    /// <summary>plan.loop.unresolved 里 analyze 追加的"更小分配"取证条目：它是说明，不是时间机制，不参与掩盖判定。</summary>
    public const string AllocationFallbackKind = "loop_allocation_fallback";

    /// <summary>bake.json 里残差掩盖因布局被拒时的状态。</summary>
    public const string LayoutRejectedStatus = "candidate_rejected_residual_layout";

    /// <summary>
    /// 残差掩盖的布局前提：每个可掩盖残差层都落在某个视频组里——它所在的组才能多渲一个淡化窗口、在接缝处淡化。
    /// 组数与透明与否都不限：透明组 packed 图左半是预乘色 C = α·c（加性粒子只有 C），右半是覆盖度 α，合成到任意背景 B 的
    /// out = C + (1−α)·B 对 (C, α) 线性，所以在 packed 域逐通道线性混合就是预乘空间里正确的淡化（design-particle-crossfade §4），
    /// 与不透明组同一个 blend 表达式；多组共享同一个起点帧，组间相位关系与原作一致。
    /// analyze 的前移判定与 bake 的防御共用这一条，保证两边口径一致。
    /// </summary>
    public static bool LayoutAllowsMasking(JsonObject plan, JsonObject classification) =>
        UnplacedResidualOwners(plan, classification).Length == 0;

    /// <summary>可掩盖残差层里不属于任何视频组的 owner：布局确实无法替它们淡化。</summary>
    public static int[] UnplacedResidualOwners(JsonObject plan, JsonObject classification)
    {
        ArgumentNullException.ThrowIfNull(plan);
        HashSet<int> baked = (plan["video_groups"] as JsonArray ?? []).OfType<JsonObject>()
            .SelectMany(group => (group["layer_ids"] as JsonArray ?? []).Select(Id).OfType<int>()).ToHashSet();
        return [.. ResidualOwners(classification).Where(owner => !baked.Contains(owner))];
    }

    /// <summary>分类结果里可掩盖残差层的 owner（去重，按出现顺序）。</summary>
    public static int[] ResidualOwners(JsonObject classification)
    {
        ArgumentNullException.ThrowIfNull(classification);
        return [.. (classification["residual_layers"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(layer => Id(layer["owner_layer_id"]) ?? -1).Distinct()];
    }

    /// <summary>
    /// 含可掩盖残差层的视频组下标（plan.video_groups 顺序）。起点搜索在第一个上做、起点共享给所有组；
    /// 这些组的 master 多渲一个淡化窗口，各自测第一层并淡化。
    /// </summary>
    public static int[] ResidualGroupIndexes(JsonObject plan, JsonObject classification)
    {
        ArgumentNullException.ThrowIfNull(plan);
        HashSet<int> owners = [.. ResidualOwners(classification)];
        JsonObject[] groups = [.. (plan["video_groups"] as JsonArray ?? []).OfType<JsonObject>()];
        return [.. Enumerable.Range(0, groups.Length).Where(index =>
            (groups[index]["layer_ids"] as JsonArray ?? []).Select(Id).OfType<int>().Any(owners.Contains))];
    }

    /// <summary>
    /// 起点搜索与 master 渲染的预热帧数：所有可掩盖粒子层 warmup_seconds 的最大值乘帧率，向上取整到整帧。
    /// 平稳随机过程要跑过预热（starttime + 寿命上界 + 间歇发射上界）才进入与时间无关的分布；没有粒子层时为 0。
    /// 用 decimal 乘，免得 0.795 × 60 这类值在浮点里多出一帧。
    /// </summary>
    public static ulong WarmupFrames(JsonObject classification, uint fpsNumerator, uint fpsDenominator)
    {
        ArgumentNullException.ThrowIfNull(classification);
        if (fpsNumerator == 0 || fpsDenominator == 0) throw new ArgumentException("预热帧数需要正的有理帧率。");
        decimal seconds = (classification["residual_layers"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(layer => Number(layer["warmup_seconds"])).OfType<double>()
            .Select(value => (decimal)Math.Max(0, value)).DefaultIfEmpty(0m).Max();
        return (ulong)decimal.Ceiling(seconds * fpsNumerator / fpsDenominator);
    }

    /// <summary>
    /// 锁定周期的可掩盖粒子层（封顶 + 确定寿命）要求循环长度是替换周期的整数倍、帧率与锁定时一致，否则接缝两侧不同相位。
    /// 全部满足返回 null；否则返回第一条不满足的取证（带中英理由）。analyze 的求解器已经保证这一点，这里是 bake 选定循环后的防御。
    /// </summary>
    public static JsonObject? LockedCycleMismatch(JsonObject classification, ulong frames, uint fpsNumerator, uint fpsDenominator)
    {
        ArgumentNullException.ThrowIfNull(classification);
        foreach (JsonObject layer in (classification["residual_layers"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (layer["cyclostationary_lock"] is not JsonObject cycle) continue;
            static ulong Whole(JsonNode? node, ulong maximum) =>
                Number(node) is double value && value >= 0 && value <= maximum && value == Math.Floor(value) ? (ulong)value : 0;
            ulong period = Whole(cycle["period_frames"], 1UL << 52);
            uint lockedNumerator = (uint)Whole(cycle["fps_num"], uint.MaxValue);
            uint lockedDenominator = (uint)Whole(cycle["fps_den"], uint.MaxValue);
            // 帧率按有理数比较（60/1 与 120/2 相同）。
            bool sameRate = lockedNumerator > 0 && lockedDenominator > 0 &&
                (ulong)lockedNumerator * fpsDenominator == (ulong)fpsNumerator * lockedDenominator;
            if (period > 0 && sameRate && frames > 0 && frames % period == 0) continue;
            string owner = Id(layer["owner_layer_id"])?.ToString(CultureInfo.InvariantCulture) ?? "?";
            object[] args = [owner, period, frames, Rate(fpsNumerator, fpsDenominator), Rate(lockedNumerator, lockedDenominator)];
            return new JsonObject
            {
                ["owner_layer_id"] = layer["owner_layer_id"]?.DeepClone(), ["period_frames"] = period, ["loop_frames"] = frames,
                ["fps_num"] = fpsNumerator, ["fps_den"] = fpsDenominator, ["locked_fps_num"] = lockedNumerator, ["locked_fps_den"] = lockedDenominator,
                ["reason"] = MessageCatalog.Get("residual.particle_cycle_mismatch", MessageCatalog.Chinese, args),
                ["reason_en"] = MessageCatalog.Get("residual.particle_cycle_mismatch", MessageCatalog.English, args)
            };
        }
        return null;
        static string Rate(uint numerator, uint denominator) => denominator == 1
            ? numerator.ToString(CultureInfo.InvariantCulture)
            : $"{numerator.ToString(CultureInfo.InvariantCulture)}/{denominator.ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>
    /// 布局不允许掩盖时的取证与双语理由：点名不在任何视频组里的可掩盖分量、说明没有组能替它们淡化、给出本场景可执行的出路。
    /// --retain-live 的具体 id 优先取 analyze 已经重查过的更小分配（loop_allocation_fallback 为 candidate_found），
    /// 否则退回这些分量所在的作者根，并如实说明还没重新分析过。
    /// </summary>
    public static JsonObject LayoutRejection(JsonObject plan, JsonObject classification) =>
        LayoutRejection(plan, classification, out _);

    internal static JsonObject LayoutRejection(JsonObject plan, JsonObject classification, out Blocker blocker)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(classification);
        string layout = PlanSettings.Of(plan).VideoLayout;
        JsonObject[] groups = (plan["video_groups"] as JsonArray ?? []).OfType<JsonObject>().ToArray();
        int transparentGroups = groups.Count(group => !Flag(group["include_scene_clear"]));
        var layers = (plan["layers"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(layer => Id(layer["id"]) is not null).GroupBy(layer => Id(layer["id"])!.Value).ToDictionary(g => g.Key, g => g.First());
        // 只列布局确实淡化不了的那几项：不在任何视频组里的残差层。
        HashSet<int> unplaced = [.. UnplacedResidualOwners(plan, classification)];
        JsonObject[] residual = (classification["residual_layers"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(item => unplaced.Contains(Id(item["owner_layer_id"]) ?? -1)).ToArray();

        var components = new JsonArray();
        var ownerRoots = new List<int>();
        foreach (JsonObject item in residual)
        {
            int? owner = Id(item["owner_layer_id"]);
            int? root = owner is int ownerId && layers.TryGetValue(ownerId, out JsonObject? layer) ? Id(layer["root"]) ?? ownerId : owner;
            if (root is int rootId && !ownerRoots.Contains(rootId)) ownerRoots.Add(rootId);
            components.Add(new JsonObject
            {
                ["owner_layer_id"] = owner, ["layer_name"] = item["layer_name"]?.DeepClone(),
                ["unresolved_kind"] = item["unresolved_kind"]?.DeepClone(), ["classification"] = item["classification"]?.DeepClone(),
                ["author_root_id"] = root
            });
        }
        string ComponentList(bool chinese)
        {
            const int maximum = 5;
            string[] parts = residual.Take(maximum).Select(item =>
            {
                string owner = Id(item["owner_layer_id"]) is int id ? id.ToString(CultureInfo.InvariantCulture) : "?";
                string name = Text(item["layer_name"]) is { Length: > 0 } named ? " \"" + MessageCatalog.EscapeName(named) + "\"" : "";
                string label = Text(item["classification"]) is { Length: > 0 } kind ? kind : Text(item["unresolved_kind"]);
                return chinese ? $"图层 {owner}{name}（{label}）" : $"layer {owner}{name} ({label})";
            }).ToArray();
            string list = string.Join(chinese ? "、" : ", ", parts);
            if (residual.Length > maximum) list += chinese ? $" 等共 {residual.Length} 项" : $" and {residual.Length - maximum} more";
            return list;
        }

        // 已重查过的更小分配优先；它的 id 列表本身就包含了用户已经要求保留的根。
        JsonObject? fallback = plan["loop_allocation_fallback"] as JsonObject;
        int[] verified = Text(fallback?["status"]) == "candidate_found"
            ? (fallback!["retain_live_root_ids"] as JsonArray ?? []).Select(Id).OfType<int>().ToArray() : [];
        int[] retain = verified.Length > 0 ? verified
            : (plan["settings"]?["retain_live_root_ids"] as JsonArray ?? []).Select(Id).OfType<int>().Concat(ownerRoots).Distinct().ToArray();
        string retainText = string.Join(",", retain.Select(id => id.ToString(CultureInfo.InvariantCulture)));

        var optionsZh = new List<string>();
        var optionsEn = new List<string>();
        if (retain.Length == 0)
        {
            optionsZh.Add("重新分析，确认这些图层的分配");
            optionsEn.Add("re-run analyze to settle where these layers are allocated");
        }
        else
        {
            optionsZh.Add(verified.Length > 0
                ? $"加 --retain-live {retainText} 重新分析，让这些分量所在的作者根整棵保持实时；按这个分配重新分析过，已经找到覆盖其余内容的循环"
                : $"加 --retain-live {retainText} 重新分析，让这些分量所在的作者根整棵保持实时（这个分配还没有重新分析过）");
            optionsEn.Add(verified.Length > 0
                ? $"re-run analyze with --retain-live {retainText} to keep the author roots that own these components live; a re-analysis with that allocation already finds a loop for the remaining content"
                : $"re-run analyze with --retain-live {retainText} to keep the author roots that own these components live (that allocation has not been re-analyzed yet)");
        }
        string groupCount = groups.Length.ToString(CultureInfo.InvariantCulture);
        object?[] zh = [layout, groupCount, transparentGroups > 0 ? $"（其中 {transparentGroups} 个是透明组）" : "",
            ComponentList(chinese: true), string.Join("；或者", optionsZh)];
        object?[] en = [layout, groupCount, transparentGroups > 0 ? $" ({transparentGroups} transparent)" : "",
            ComponentList(chinese: false), string.Join("; or ", optionsEn)];
        blocker = new Blocker(BlockerCode.ResidualMaskingLayout, en, zh);
        string reason = blocker.Text;
        return new JsonObject
        {
            ["schema_version"] = 1,
            ["status"] = "blocked",
            ["rule"] = "residual_masking_requires_residual_layers_in_video_groups",
            ["video_layout"] = layout,
            ["video_group_count"] = groups.Length,
            ["transparent_group_count"] = transparentGroups,
            ["unresolved_components"] = components,
            ["suggested_retain_live_root_ids"] = new JsonArray(retain.Select(id => (JsonNode)JsonValue.Create(id)).ToArray()),
            ["retain_live_basis"] = retain.Length == 0 ? null
                : verified.Length > 0 ? "loop_allocation_fallback_candidate_found" : "unresolved_owner_author_roots_not_reanalyzed",
            ["reason"] = reason,
            ["reason_zh"] = MessageCatalog.Get("blocker.residual_masking_layout", MessageCatalog.Chinese, zh),
            ["reason_en"] = MessageCatalog.Get("blocker.residual_masking_layout", MessageCatalog.English, en)
        };
    }

    /// <summary>
    /// 残差判定读取工程内 JSON 资源（粒子预设等）的统一入口：先查源工程，再查 WPE assets；读不到返回 null。
    /// analyze 的前移判定与 bake 用同一个读取口径。
    /// </summary>
    public static Func<string, JsonObject?> ResourceReader(ProjectSource source, string? assets)
    {
        ArgumentNullException.ThrowIfNull(source);
        return resource =>
        {
            try
            {
                if (source.Contains(resource)) return source.ReadJson(resource);
                if (string.IsNullOrEmpty(assets)) return null;
                string asset = Path.Combine(assets, resource.Replace('/', Path.DirectorySeparatorChar));
                return File.Exists(asset) ? JsonNode.Parse(File.ReadAllText(asset))?.AsObject() : null;
            }
            catch (Exception error) when (error is IOException or InvalidDataException or System.Text.Json.JsonException) { return null; }
        };
    }

    /// <summary>
    /// 对 plan.loop.unresolved 的每一项判定"残差可掩盖"或"不可掩盖"。
    /// <paramref name="readResource"/> 读取工程内的 JSON 资源（粒子预设等），读不到返回 null。准入统一走 <see cref="Admission.Evaluate(JsonObject, JsonObject, Func{string, JsonObject?})"/>。
    /// </summary>
    public static JsonObject Classify(JsonObject plan, JsonObject scene, Func<string, JsonObject?> readResource)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(readResource);
        var maskable = new JsonArray();
        var blocking = new JsonArray();
        var result = new JsonObject
        {
            ["schema_version"] = 1,
            ["policy"] = SeamPolicy,
            ["scope"] = "每一条未解析分量都必须先被证明是随机的（脚本随机重启的精灵、满足平稳随机判据的粒子系统），才允许被接缝交叉淡化掩盖。" +
                "位移类分量淡化后位置跳变可见，即使幅度有界也不可掩盖；识别不了的机制、判据不通过的粒子一律阻断。" +
                "随机精灵与粒子的画布占比只记录不裁决，把关的是全分辨率的第一层残差判据。",
            ["thresholds"] = Thresholds(),
            ["residual_layers"] = maskable,
            ["blocking_components"] = blocking
        };
        var unresolved = plan["loop"]?["unresolved"] as JsonArray ?? [];
        if (unresolved.Count == 0)
        {
            result["status"] = "no_residual";
            result["sprite_canvas_coverage_total"] = 0d;
            result["sprite_canvas_coverage_unknown_layers"] = 0;
            return result;
        }
        var layers = (plan["layers"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(layer => Id(layer["id"]) is not null).ToDictionary(layer => Id(layer["id"])!.Value);
        var objects = (scene["objects"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(node => Id(node["id"]) is not null).ToDictionary(node => Id(node["id"])!.Value, node => node);
        // 两套尺度不能混：位移的像素估计按输出像素（settings.width/height），面积占比按场景单位画布
        // （projection 的 canvas_width/height）——后者才是 plan.layers[].canvas_fraction 的分母，粒子的 sizerandom 也是场景单位。
        double outputWidth = Number(plan["settings"]?["width"]) ?? 0;
        double outputHeight = Number(plan["settings"]?["height"]) ?? 0;
        double sceneWidth = Number(plan["canvas_width"]) ?? 0;
        double sceneHeight = Number(plan["canvas_height"]) ?? 0;
        // 拒绝理由复述这份计划实际用的循环时长上限（= --loop-max-seconds）；旧计划没记就按默认值讲。
        double loopCeiling = Number(plan["loop"]?["maximum_seconds"]) ?? CommonLoopSolver.DefaultMaximumSeconds;
        result["output_width"] = outputWidth;
        result["output_height"] = outputHeight;
        result["canvas_width"] = sceneWidth;
        result["canvas_height"] = sceneHeight;
        // 精灵/粒子的画布占比合计只是信息：层包围盒对带透明边的精灵图集高估几十倍，用它当门卫没有意义，
        // 真正把关的是全分辨率 master 上的第一层残差判据。算不出的层计数写进 unknown_layers，同样不裁决。
        double spriteCoverage = 0;
        int unknownCoverageLayers = 0;
        foreach (JsonObject item in unresolved.OfType<JsonObject>())
        {
            JsonObject verdict = Evaluate(item, layers, objects, readResource, outputWidth, sceneWidth, sceneHeight, loopCeiling);
            if (verdict["maskable"]?.GetValue<bool>() == true)
            {
                maskable.Add(verdict);
                if (Number(verdict["canvas_fraction"]) is double fraction) spriteCoverage += fraction;
                else ++unknownCoverageLayers;
            }
            else blocking.Add(verdict);
        }
        // 平稳随机粒子要跑过预热才进入与时间无关的分布：起点搜索与 master 渲染取所有可掩盖粒子层预热的最大值。
        result["max_warmup_seconds"] = maskable.OfType<JsonObject>().Select(verdict => Number(verdict["warmup_seconds"]))
            .OfType<double>().DefaultIfEmpty(0).Max();
        result["sprite_canvas_coverage_total"] = Math.Round(spriteCoverage, 6);
        result["sprite_canvas_coverage_unknown_layers"] = unknownCoverageLayers;
        result["sprite_canvas_coverage_basis"] = "随机精灵与粒子层的包围盒画布占比合计，只记录不裁决；掩盖是否可接受由第一层残差判据在全分辨率 master 上实测决定。";
        result["status"] = blocking.Count == 0 ? "residual_maskable" : "rejected";
        if (blocking.Count > 0)
            result["reason"] = MessageCatalog.Get("residual.not_maskable_items", MessageCatalog.Chinese, string.Join("；", blocking.OfType<JsonObject>()
                .Select(node => node["reason"]?.GetValue<string>() ?? MessageCatalog.Get("residual.reason_missing", MessageCatalog.Chinese))));
        return result;
    }

    private static JsonObject Evaluate(JsonObject item, IReadOnlyDictionary<int, JsonObject> layers,
        IReadOnlyDictionary<int, JsonObject> objects, Func<string, JsonObject?> readResource,
        double outputWidth, double sceneWidth, double sceneHeight, double loopCeiling)
    {
        string kind = item["kind"]?.GetValue<string>() ?? "";
        string ceiling = loopCeiling.ToString("0.###", CultureInfo.InvariantCulture);
        string detail = item["detail"]?.GetValue<string>() ?? "";
        string resource = item["resource"]?.GetValue<string>() ?? "";
        int? owner = Id(item["owner_layer_id"]);
        var verdict = new JsonObject
        {
            ["owner_layer_id"] = owner is int id ? id : null,
            ["layer_name"] = owner is int named && layers.TryGetValue(named, out JsonObject? layer) ? layer["name"]?.DeepClone() : null,
            ["unresolved_kind"] = kind,
            ["resource"] = resource.Length > 0 ? resource : null,
            ["source_detail"] = detail
        };
        // 面积算不出（size 缺失、父链几何解析失败）时 plan 里是 null；这里绝不把 null 当 0，否则 25% 占比闸门对这些层失效。
        double? canvasFraction = owner is int fractionOwner && layers.TryGetValue(fractionOwner, out JsonObject? fractionLayer)
            ? Number(fractionLayer["canvas_fraction"]) : null;
        verdict["canvas_fraction"] = canvasFraction;

        // 机制知识来自 ShaderPeriodAnalysis 写下的结构化字段，不按资源名匹配字样——同一套方程换个文件名，判定必须一致。
        // 声明了幅度上界的位移机制单独分类（记下估算的峰值偏移），但一律不可掩盖。
        string mechanism = Text(item["mechanism"]);
        if (kind == "NonPeriodicOrDriftingMechanism" && Flag(item["bounded_displacement"]))
            return BoundedDisplacementVerdict(verdict, item, mechanism, objects, outputWidth);

        if (kind == "runtime_animation" && owner is int spriteOwner)
        {
            JsonObject? spriteObject = objects.GetValueOrDefault(spriteOwner);
            if (spriteObject?["particle"] is JsonNode particle)
                return ParticleVerdict(verdict, item, particle, readResource, sceneWidth, sceneHeight);
            // 随机重启的证明来自 HybridLoopService 写下的结构化字段（同一段脚本里取到该精灵动画、做播放控制、
            // 用 Math.random 喂定时器），不从文案里匹配字样；非精灵轨道即便被脚本控制也拿不到这个字段。
            if (Flag(item["random_restart"]) && mechanism == "sprite")
            {
                verdict["classification"] = "random_sprite";
                verdict["mechanism"] = "script_random_restart";
                verdict["proof"] = "该层的精灵动画由脚本在 Math.random() 延迟后重启，任何采集长度下都没有周期；" +
                    "证明来自源场景脚本，不是识别失败。";
                // 面积（包围盒占比）只记录：算不出时 canvas_fraction 保持 null，同样不裁决；掩盖幅度由第一层残差判据实测。
                verdict["amplitude_basis"] = "first_layer_seam_residual_check";
                verdict["canvas_fraction_basis"] = canvasFraction is double
                    ? "层包围盒占画布的比例，只记录不裁决。"
                    : "size 缺失或父链几何无法解析，占比未知，只记录不裁决。";
                verdict["maskable"] = true;
                return verdict;
            }
            verdict["classification"] = "unrecognized";
            verdict["maskable"] = false;
            verdict["reason"] = MessageCatalog.Get("residual.sprite_unrecognized", MessageCatalog.Chinese, spriteOwner, detail);
            return verdict;
        }

        // 「已被方程证明非周期」与「识别不了」是两回事，理由必须分开写：前者改参数也救不回来，后者是分析
        // 没认出机制。两类都不可掩盖，措辞不能互相冒充。
        bool provenNonPeriodic = kind == "NonPeriodicOrDriftingMechanism";
        if (mechanism.Length > 0) verdict["mechanism"] = mechanism;
        verdict["classification"] = provenNonPeriodic ? "proven_nonperiodic_unbounded" : "unrecognized";
        verdict["maskable"] = false;
        string layerPrefix = owner is int unknownOwner ? MessageCatalog.Get("residual.layer_prefix", MessageCatalog.Chinese, unknownOwner) : "";
        verdict["reason"] = provenNonPeriodic
            ? MessageCatalog.Get("residual.proven_nonperiodic_unbounded", MessageCatalog.Chinese, layerPrefix, kind, ceiling, detail)
            : MessageCatalog.Get("residual.unrecognized_unbounded", MessageCatalog.Chinese, layerPrefix, kind, detail);
        if (mechanism == ShaderPeriodAnalysis.LightShaftDriftMechanism) AddLinearDriftGuidance(verdict, item, objects, loopCeiling);
        return verdict;
    }

    /// <summary>
    /// 线性漂移（lightshafts 一族）的用户指引：数字全部由该 pass 的速率常量与着色器方程算出，不写壁纸名，
    /// 也不针对任何一张壁纸。读不出速率常量时只给不带数字的一句，不编数。
    /// </summary>
    private static void AddLinearDriftGuidance(JsonObject verdict, JsonObject item, IReadOnlyDictionary<int, JsonObject> objects,
        double loopCeiling)
    {
        string ceiling = loopCeiling.ToString("0.###", CultureInfo.InvariantCulture);
        JsonObject? pass = AuthoredPass(item, objects);
        double? speed = pass is null ? null : Scalar(pass, "rayspeed");
        if (speed is not double value || !double.IsFinite(value) || value == 0)
        {
            verdict["user_guidance_zh"] = "这一层的特效让噪声贴图沿四条轴匀速漂移，偏移随时间线性增长、不回头，" +
                $"在 {ceiling} 秒的循环上限内没有可用循环；调速只会等比缩放这四条速率，救不回来。" +
                "请在 Wallpaper Engine 里关掉这一层后重新烘焙，或接受该层保持实时渲染。";
            verdict["user_guidance_en"] = "This layer's effect drifts noise textures along four axes at a constant rate, " +
                $"so the offset grows without ever returning and no loop exists within the {ceiling}-second ceiling; " +
                "retiming scales all four rates equally and cannot recover it. Switch that layer off before baking, or keep it rendering live.";
            return;
        }
        double rate = Math.Abs(value);
        double fastest = 1 / (ShaderPeriodAnalysis.LightShaftDriftRates.Max() * rate);
        double slowest = 1 / (ShaderPeriodAnalysis.LightShaftDriftRates.Min() * rate);
        double joint = ShaderPeriodAnalysis.LightShaftJointRepeatUnits / rate;
        verdict["axis_rates_per_second"] = new JsonArray(ShaderPeriodAnalysis.LightShaftDriftRates
            .Select(coefficient => (JsonNode)JsonValue.Create(rate * coefficient)).ToArray());
        verdict["fastest_axis_seconds"] = Math.Round(fastest, 3);
        verdict["slowest_axis_seconds"] = Math.Round(slowest, 3);
        verdict["joint_return_seconds"] = joint;
        verdict["loop_ceiling_seconds"] = loopCeiling;
        string fast = fastest.ToString("0.#", CultureInfo.InvariantCulture);
        string slow = slowest.ToString("0.#", CultureInfo.InvariantCulture);
        string together = joint.ToString("0.###E+00", CultureInfo.InvariantCulture);
        verdict["user_guidance_zh"] = $"这一层的光轴效果让两张噪声贴图沿四条轴漂移，单轴回归周期是 {fast} 秒到 {slow} 秒，" +
            $"四条轴合到一起要 {together} 秒，在 {ceiling} 秒的循环上限内不存在任何可用循环；" +
            "调速只会等比缩放这四条速率，救不回来。请在 Wallpaper Engine 里关掉这一层后重新烘焙，或接受该层保持实时渲染。";
        verdict["user_guidance_en"] = "This layer's light-shaft effect drifts two noise textures at four rates; the axes repeat only every " +
            $"{fast} s to {slow} s and close together only after {together} s, so no loop exists within the " +
            $"{ceiling}-second ceiling, and retiming scales all four rates equally. " +
            "Switch that layer off before baking, or keep it rendering live.";
    }

    /// <summary>未解析分量指向的作者 pass（effects[effect_index].passes[pass_index]），取不到返回 null。</summary>
    private static JsonObject? AuthoredPass(JsonObject item, IReadOnlyDictionary<int, JsonObject> objects)
    {
        int? owner = Id(item["owner_layer_id"]);
        int effectIndex = Id(item["effect_index"]) ?? -1, passIndex = Id(item["pass_index"]) ?? -1;
        return owner is int ownerId && objects.TryGetValue(ownerId, out JsonObject? ownerObject)
            ? (ownerObject["effects"] as JsonArray)?.ElementAtOrDefault(effectIndex)?["passes"]?.AsArray()?.ElementAtOrDefault(passIndex) as JsonObject
            : null;
    }

    /// <summary>
    /// 声明了幅度上界的位移机制（foliagesway 这类着色器 UV/顶点抖动）。一律不可掩盖：残差是同一个元素在接缝两侧的位置不同，
    /// 交叉淡化只会把位置跳变变成 0.4 秒的半透明双影，人眼定标（3648434762、3652446458、3750317749 的并排视频，max_k 12.8～24.4）
    /// 判定明显。能算出峰值偏移时照样记下（uv_sway 一族：八项正弦和乘以 strength²·0.005），只作取证，不再参与裁决。
    /// </summary>
    private static JsonObject BoundedDisplacementVerdict(JsonObject verdict, JsonObject item, string mechanism,
        IReadOnlyDictionary<int, JsonObject> objects, double canvasWidth)
    {
        verdict["classification"] = "displacement";
        string named = mechanism.Length > 0 ? mechanism : ShaderPeriodAnalysis.FoliageSwayMechanism;
        verdict["mechanism"] = named;
        int? owner = Id(item["owner_layer_id"]);
        verdict["maskable"] = false;
        string layer = owner is int ownerId ? ownerId.ToString(CultureInfo.InvariantCulture) : "?";
        verdict["reason"] = MessageCatalog.Get("residual.displacement_not_maskable", MessageCatalog.Chinese, layer, named);
        verdict["reason_en"] = MessageCatalog.Get("residual.displacement_not_maskable", MessageCatalog.English, layer, named);
        if (named != ShaderPeriodAnalysis.FoliageSwayMechanism) return verdict;
        JsonObject? pass = AuthoredPass(item, objects);
        double? strength = pass is null ? null : Scalar(pass, "strength");
        if (strength is not double value || !double.IsFinite(value)) return verdict;
        double fraction = FoliageSwayPeakOffsetFraction(Math.Abs(value));
        verdict["strength"] = value;
        verdict["peak_offset_uv"] = Math.Round(fraction, 8);
        verdict["peak_offset_pixels"] = Math.Round(fraction * canvasWidth, 3);
        verdict["peak_offset_basis"] = "strength² × 0.005 × 4 × 输出宽度，只记录不裁决。";
        return verdict;
    }

    /// <summary>
    /// 粒子层：读 HybridLoopService 写下的 particle_stationarity（C1–C9 平稳随机判据的结构化结论），不按名字里有没有 random 判断。
    /// 判据通过才可掩盖，并把预热时长带出来给起点搜索与 master 渲染；判据不通过、或旧版计划没有这个字段，一律不可掩盖。
    /// 粒子定义读得到时照样记画布占比，只记录不裁决。
    /// </summary>
    private static JsonObject ParticleVerdict(JsonObject verdict, JsonObject item, JsonNode particle,
        Func<string, JsonObject?> readResource, double canvasWidth, double canvasHeight)
    {
        verdict["classification"] = "random_particle";
        verdict["mechanism"] = "particle_system";
        string layer = Id(item["owner_layer_id"]) is int ownerId ? ownerId.ToString(CultureInfo.InvariantCulture) : "?";
        JsonObject? definition = particle as JsonObject;
        if (definition is null && particle is JsonValue value && value.TryGetValue<string>(out string? path))
        {
            verdict["particle_definition"] = path;
            definition = readResource(path);
        }
        if (item["particle_stationarity"] is not JsonObject stationarity ||
            stationarity["stationary"] is not JsonValue flag || !flag.TryGetValue(out bool stationary))
        {
            verdict["maskable"] = false;
            verdict["reason"] = MessageCatalog.Get("residual.particle_verdict_missing", MessageCatalog.Chinese, layer);
            verdict["reason_en"] = MessageCatalog.Get("residual.particle_verdict_missing", MessageCatalog.English, layer);
            return verdict;
        }
        verdict["particle_stationarity"] = stationarity.DeepClone();
        if (!stationary)
        {
            string codes = string.Join(", ", (stationarity["failed_conditions"] as JsonArray ?? []).OfType<JsonObject>()
                .Select(failure => (Text(failure["condition"]) + " " + Text(failure["code"])).Trim()).Where(code => code.Length > 0).Distinct());
            verdict["maskable"] = false;
            verdict["reason"] = MessageCatalog.Get("residual.particle_not_stationary", MessageCatalog.Chinese, layer, codes);
            verdict["reason_en"] = MessageCatalog.Get("residual.particle_not_stationary", MessageCatalog.English, layer, codes);
            return verdict;
        }
        if (Number(stationarity["warmup_seconds"]) is not double warmup || warmup < 0)
        {
            verdict["maskable"] = false;
            verdict["reason"] = MessageCatalog.Get("residual.particle_warmup_missing", MessageCatalog.Chinese, layer);
            verdict["reason_en"] = MessageCatalog.Get("residual.particle_warmup_missing", MessageCatalog.English, layer);
            return verdict;
        }
        verdict["warmup_seconds"] = warmup;
        verdict["lifetime_max_seconds"] = stationarity["lifetime_max_seconds"]?.DeepClone();
        if (stationarity["cyclostationary_lock"] is JsonObject cycle)
        {
            // 封顶 + 确定寿命：周期平稳，只在循环长度是替换周期的整数倍时成立；bake 用 LockedCycleMismatch 核对选中的循环。
            string period = Id(cycle["period_frames"])?.ToString(CultureInfo.InvariantCulture) ?? "?";
            verdict["cyclostationary_lock"] = cycle.DeepClone();
            verdict["proof"] = MessageCatalog.Get("residual.particle_cyclostationary_proof", MessageCatalog.Chinese, period);
            verdict["proof_en"] = MessageCatalog.Get("residual.particle_cyclostationary_proof", MessageCatalog.English, period);
        }
        else
        {
            verdict["proof"] = MessageCatalog.Get("residual.particle_stationary_proof", MessageCatalog.Chinese);
            verdict["proof_en"] = MessageCatalog.Get("residual.particle_stationary_proof", MessageCatalog.English);
        }
        verdict["amplitude_basis"] = "first_layer_seam_residual_check";
        verdict["canvas_fraction_basis"] = definition is null
            ? "粒子定义读不到，占比未知，只记录不裁决。"
            : "单个精灵最大尺寸占画布的面积比乘以最大粒子数（封顶 1），只记录不裁决。";
        verdict["canvas_fraction"] = definition is null ? null : Math.Round(ParticleCoverage(definition, canvasWidth, canvasHeight), 6);
        verdict["maskable"] = true;
        return verdict;
    }

    /// <summary>粒子层的画布占比：单个精灵的最大尺寸占画布的面积比乘以同时存在的最大粒子数，封顶 1。</summary>
    private static double ParticleCoverage(JsonObject definition, double canvasWidth, double canvasHeight)
    {
        if (canvasWidth <= 0 || canvasHeight <= 0) return 1;
        double size = 0;
        foreach (JsonObject initializer in (definition["initializer"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (initializer["name"]?.GetValue<string>() is not string name ||
                !name.Contains("size", StringComparison.OrdinalIgnoreCase)) continue;
            size = Math.Max(size, Math.Max(Scalar(initializer, "max") ?? 0, Scalar(initializer, "min") ?? 0));
        }
        if (size <= 0) return 1;
        double count = Scalar(definition, "maxcount") ?? 0;
        if (count <= 0) return 1;
        return Math.Min(1, size * size / (canvasWidth * canvasHeight) * count);
    }

    private static double? Scalar(JsonObject node, string key)
    {
        JsonNode? value = key is "max" or "min" or "maxcount" ? node[key] : node["constantshadervalues"]?[key];
        if (value is JsonArray array) value = array.Count == 1 ? array[0] : null;
        return Number(value);
    }

    private static bool Flag(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out bool flag) && flag;

    /// <summary>字符串字段的安全读取：类型不对当成缺失，绝不抛。</summary>
    private static string Text(JsonNode? node) => node is JsonValue value && value.TryGetValue(out string? text) && text is not null ? text : "";

    private static int? Id(JsonNode? node) => Number(node) is double value && value == Math.Floor(value) &&
        value is >= int.MinValue and <= int.MaxValue ? (int)value : null;

    /// <summary>内存里构造的 JsonValue 只认原始类型，磁盘上的 JsonElement 才会自动转换；两种都要读得出来。</summary>
    private static double? Number(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        double number;
        if (value.TryGetValue(out number)) { }
        else if (value.TryGetValue(out int integer)) number = integer;
        else if (value.TryGetValue(out long wide)) number = wide;
        else if (value.TryGetValue(out uint unsigned)) number = unsigned;
        else if (value.TryGetValue(out ulong unsignedWide)) number = unsignedWide;
        else if (value.TryGetValue(out float single)) number = single;
        else if (value.TryGetValue(out decimal exact)) number = (double)exact;
        else return null;
        return double.IsFinite(number) ? number : null;
    }
}
