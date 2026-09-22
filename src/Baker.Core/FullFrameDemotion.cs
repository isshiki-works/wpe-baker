using System.Text.Json;
using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>全幅准入的"单不透明组可达性"判定与尾组降级。</summary>
/// <remarks>
/// 唯一允许的自动补救：把第一个不透明底组之后的全部视频 root 整体退回实时，保持它们在原位、
/// 原父子变换、原绘制顺序、原视差深度。不重排图层、不改遮挡、不放宽任何接缝或周期阈值、不产生透明组。
/// 判定全部在克隆上完成，任何一条采纳条件不成立就维持原拒绝，并把理由写进 plan。
/// </remarks>
internal static class FullFrameDemotion
{
    /// <summary>降级生效后的布局准入状态。</summary>
    internal const string DemotedAdmissionStatus = "planned_layout_allowed_after_demotion";

    private const string Scope = "Whole video roots only: the demoted roots keep their position, author parents, draw order and parallax depth. " +
        "Layout permission is not proof of image correctness, looping, hardware decoding or playback benefit.";

    // ApplyAllocation 末尾会调用 FullFrameConflict，而冲突文案又要问"能否整体退回"，
    // 两者会互相调用。探测期间禁止再次探测，把递归深度钉死在一层。
    [ThreadStatic] private static bool probing;

    /// <summary>分配一旦改变，之前记录的准入结论不再成立。</summary>
    internal static void ResetAdmissionRecords(JsonObject plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        plan.Remove("full_frame_retention");
        plan.Remove("layout_admission_demotion");
    }

    /// <summary>纯判定：首组含 scene clear 且其后的视频 root 能整根退回实时时返回这些 root，否则返回 null。</summary>
    internal static int[]? FullFrameSingleGroupRetention(JsonObject plan) => FullFrameSingleGroupRetention(plan, null);

    /// <summary>同上，带上运行时依赖以便闭包判定；只在克隆上运行，不改动传入的 plan。</summary>
    internal static int[]? FullFrameSingleGroupRetention(JsonObject plan, JsonArray? runtimeDependencies)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (probing || Layout(plan) != "full_frame") return null;
        probing = true;
        try { return Retain(plan, runtimeDependencies ?? new JsonArray(), out int[] demoted, out _) is null ? null : demoted; }
        finally { probing = false; }
    }

    /// <summary>把可达性判定写成 plan 字段；analyze 不自动套用它，值不值得由用户在目标机器上自己比。</summary>
    internal static JsonObject Describe(JsonObject plan, JsonArray runtimeDependencies)
    {
        ArgumentNullException.ThrowIfNull(plan);
        int[]? retained = FullFrameSingleGroupRetention(plan, runtimeDependencies);
        var layers = Layers(plan);
        return new JsonObject {
            ["status"] = retained is null ? "unavailable" : "available",
            ["root_ids"] = JsonSerializer.SerializeToNode(retained ?? []),
            // root_ids 是分配根，可能是作者根拆开后的子单元；--retain-live 只收源作者根。这里给出照抄就能跑的完整列表，
            // 换算会连带退回别的视频单元（多半是不透明底组）时为 null：那不是同一个分配，不能当建议。
            ["retain_live_root_ids"] = retained is null ? null : RetainLiveCommandRoots(plan, retained) is int[] command
                ? JsonSerializer.SerializeToNode(command) : null,
            ["names"] = JsonSerializer.SerializeToNode(retained is null ? [] : VisibleNames(layers, retained)),
            ["canvas_fractions"] = new JsonArray((retained ?? []).Select(root => (JsonNode?)(
                layers.Where(layer => AllocationRoot(layer) == root && VisibleDrawable(layer)).Select(Fraction).OfType<double>().ToArray() is { Length: > 0 } found
                    ? JsonValue.Create(Math.Round(found.Sum(), 6)) : null)).ToArray()),
            ["remaining_group"] = (plan["video_groups"] as JsonArray)?.FirstOrDefault()?["id"]?.DeepClone(),
            ["reason"] = retained is null
                ? "No suffix of whole video roots leaves a single opaque group that carries the scene clear."
                : "Re-running analyze with --retain-live for these roots leaves one opaque video group; analyze does not apply it on its own." };
    }

    /// <summary>尝试尾组降级。采纳条件全部满足才返回新的 plan，否则返回 null 并在 record 里写明理由。</summary>
    internal static JsonObject? TryDemote(JsonObject plan, JsonArray runtimeDependencies, out JsonObject record)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(runtimeDependencies);
        record = new JsonObject { ["status"] = "not_applied", ["scope"] = Scope };
        if (probing || Layout(plan) != "full_frame")
        {
            record["reason"] = "Tail-group demotion only applies to a full-frame layout.";
            return null;
        }
        JsonObject? demotedPlan;
        int[] demoted;
        string? rejection;
        probing = true;
        try { demotedPlan = Retain(plan, runtimeDependencies, out demoted, out rejection); }
        finally { probing = false; }
        var layers = Layers(plan);
        record["blocking_live_root_ids"] = JsonSerializer.SerializeToNode(BlockingLiveRoots(plan, layers));
        if (demotedPlan is null)
        {
            record["reason"] = "Demoting the video roots after the opaque base group does not leave exactly one opaque group that carries the scene clear, " +
                "or its dependency closure would reach further roots.";
            record["allocation_reason"] = rejection;
            return null;
        }
        var demotedSet = demoted.ToHashSet();
        var demotedLayers = layers.Where(layer => AllocationRoot(layer) is int root && demotedSet.Contains(root) && VisibleDrawable(layer)).ToArray();
        record["demoted_root_ids"] = JsonSerializer.SerializeToNode(demoted);
        record["demoted_layer_ids"] = JsonSerializer.SerializeToNode(demotedLayers.Select(layer => HybridScenePlanner.Int(layer["id"])).OfType<int>());
        record["demoted_layer_names"] = JsonSerializer.SerializeToNode(VisibleNames(layers, demoted));
        // canvas_fraction 是包围盒上界，不是不透明覆盖：它只能支持"视频仍承担画面主体"的同类量比较，
        // 不能用于任何正确性判断，更不能当作遮挡证明。
        if (demotedLayers.Any(layer => Fraction(layer) is null))
        {
            record["reason"] = "A demoted visible drawable layer has an unknown canvas fraction, so the comparison against the retained group is not decidable.";
            return null;
        }
        double sum = demotedLayers.Sum(layer => Fraction(layer)!.Value);
        var keptLayerIds = Ids((demotedPlan["video_groups"] as JsonArray)?.FirstOrDefault()?["layer_ids"]).ToHashSet();
        double[] keptFractions = layers.Where(layer => HybridScenePlanner.Int(layer["id"]) is int id && keptLayerIds.Contains(id) && VisibleDrawable(layer))
            .Select(Fraction).OfType<double>().ToArray();
        record["demoted_canvas_fraction_sum"] = Math.Round(sum, 6);
        record["kept_group_canvas_fraction_max"] = keptFractions.Length == 0 ? null : Math.Round(keptFractions.Max(), 6);
        if (keptFractions.Length == 0 || sum >= keptFractions.Max())
        {
            record["reason"] = "The demoted content does not stay below the retained group's largest visible drawable canvas fraction, " +
                "so the video would no longer carry the image; the original rejection stands.";
            return null;
        }
        // 绘制顺序不变性：退回后 composition 里真正绘制的 live root 必须仍按原始分配顺序出现。
        // 不可见的 live root 不画一个像素，分组循环本来就把它们提前登记，其位置与绘制顺序无关。
        // 前景提升已经重排过顺序时这条不成立，此时拒绝降级而不是叠加两次改动。
        int[] order = Ids(plan["source_allocation_order"]) is { Length: > 0 } recorded ? recorded : Ids(plan["root_order"]);
        int[] positions = (demotedPlan["composition"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
            .Select(entry => HybridScenePlanner.Int(entry["live_root"])).OfType<int>()
            .Where(root => layers.Any(layer => AllocationRoot(layer) == root && VisibleDrawable(layer)))
            .Select(root => Array.IndexOf(order, root)).ToArray();
        if (positions.Any(position => position < 0) || positions.Zip(positions.Skip(1)).Any(pair => pair.First >= pair.Second))
        {
            record["reason"] = "The demoted composition would not keep visible drawable live roots in their recorded source allocation order; draw order must not change.";
            return null;
        }
        record["status"] = "applied";
        record["kept_group_id"] = (demotedPlan["video_groups"] as JsonArray)?.FirstOrDefault()?["id"]?.DeepClone();
        record["reason"] = $"The {demoted.Length} video root(s) above the opaque base group stay realtime in place; draw order and occlusion are unchanged.";
        return demotedPlan;
    }

    /// <summary>
    /// 全幅冲突文案里"这个场景实际可行的做法"那一串：中英各一份，只列本场景真的可行的解法。
    /// 文案的分类与双语由 <see cref="PlanNarrative.FullFrameConflict"/> 的文案表负责，这里只出选项。
    /// </summary>
    internal static (string Zh, string En) ConflictOptions(JsonObject plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var chinese = new List<string>();
        var english = new List<string>();
        if (plan["occlusion_tradeoff"]?["promoted_roots"] is JsonArray { Count: > 0 })
        {
            chinese.Add("把独立的实时小组件提到前景后重新分析（这会改变遮挡关系）");
            english.Add("choose independent live overlays in the foreground and analyze again, which changes occlusion");
        }
        if (RetentionRoots(plan) is { Length: > 0 } retention && RetainLiveCommandRoots(plan, retention) is { Length: > 0 } roots)
        {
            string ids = string.Join(",", roots);
            string[] visibleNames = VisibleSourceNames(Layers(plan), roots);
            string named = Listed(" (", visibleNames, ")");
            // 中文通道单独拼接：顿号分隔、超出以"等 N 个"收尾，不混进英文的 and N more / root。
            string namedZh = ListedChinese(visibleNames);
            chinese.Add($"用 --retain-live {ids} 重新分析，让这些图层{namedZh}保持实时，这样只剩一组不透明视频，它们当前的实时开销不变");
            english.Add($"re-run analyze with --retain-live {ids} to keep those roots realtime" + named +
                ", which leaves one opaque video group and keeps their current realtime cost");
        }
        chinese.Add("改壁纸自身的设置");
        english.Add("change the scene settings");
        chinese.Add("用 --video-layout layered 显式选择分层视频（播放开销需要另行验证）");
        english.Add("explicitly choose layered video with --video-layout layered, which requires separate playback-cost validation");
        return (string.Join("；", chinese), string.Join("; ", english));
    }

    /// <summary>可以靠 --retain-live 留住的 root：取证记下的优先，没有取证时按单组保留推断。</summary>
    private static int[]? RetentionRoots(JsonObject plan) =>
        plan["full_frame_retention"] is JsonObject recorded
            ? recorded["status"]?.GetValue<string>() == "available" ? Ids(recorded["root_ids"]) : null
            : FullFrameSingleGroupRetention(plan);

    /// <summary>
    /// 照抄就能跑的 --retain-live 列表：本次已保留的根（--retain-live 每次整体替换，必须带上）+ 保留做法换算成的源作者根。
    /// 分配根是作者根拆开后的子单元时换成它的作者根；如果这棵作者根下还有别的单元在视频里、又不在保留做法里，
    /// 整棵保留实时会把它们一起退回实时（实测常常就是不透明底组，重跑后一组视频都不剩），这就不是同一个分配，返回 null。
    /// </summary>
    internal static int[]? RetainLiveCommandRoots(JsonObject plan, IReadOnlyCollection<int> allocationRoots)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(allocationRoots);
        var layers = Layers(plan);
        var sourceOf = new Dictionary<int, int>();
        foreach (var layer in layers)
            if (HybridScenePlanner.Int(layer["id"]) is int id && SourceRoot(layer) is int source) sourceOf.TryAdd(id, source);
        var sources = new List<int>();
        foreach (int unit in allocationRoots)
        {
            if (!sourceOf.TryGetValue(unit, out int source)) return null;
            if (!sources.Contains(source)) sources.Add(source);
        }
        var retention = allocationRoots.ToHashSet();
        var videoRoots = (plan["video_groups"] as JsonArray ?? []).OfType<JsonObject>().SelectMany(group => Ids(group["root_ids"])).ToHashSet();
        bool pullsOtherVideo = layers.Any(layer => SourceRoot(layer) is int source && sources.Contains(source) &&
            AllocationRoot(layer) is int unit && videoRoots.Contains(unit) && !retention.Contains(unit));
        return pullsOtherVideo ? null : Ids(plan["settings"]?["retain_live_root_ids"]).Concat(sources).Distinct().ToArray();
    }

    private static string Listed(string prefix, string[] names, string suffix = ".")
    {
        const int limit = 5;
        if (names.Length == 0) return "";
        return prefix + string.Join(", ", names.Take(limit)) + (names.Length > limit ? $" and {names.Length - limit} more" : "") + suffix;
    }

    /// <summary>中文版层名列表：（甲、乙、丙等 7 个），最多列五个；没有层名时为空。</summary>
    internal static string ListedChinese(string[] names)
    {
        const int limit = 5;
        if (names.Length == 0) return "";
        return "（" + string.Join("、", names.Take(limit)) + (names.Length > limit ? $"等 {names.Length} 个" : "") + "）";
    }

    private static JsonObject? Retain(JsonObject plan, JsonArray runtimeDependencies, out int[] demoted, out string? rejection)
    {
        demoted = [];
        rejection = null;
        if (plan["video_groups"] is not JsonArray groups || groups.Count < 2) return null;
        if (groups[0] is not JsonObject first || first["include_scene_clear"]?.GetValue<bool>() != true ||
            first["transparent"]?.GetValue<bool>() != false) return null;
        if (groups[1] is not JsonObject second || Ids(second["root_ids"]) is not { Length: > 0 } tail) return null;
        JsonObject result;
        try { result = HybridScenePlanner.ApplyAllocation(plan, [tail[0]], runtimeDependencies); }
        catch (InvalidDataException error) { rejection = error.Message; return null; }
        rejection = result["allocation"]?["reason"]?.GetValue<string>();
        if (result["allocation"]?["status"]?.GetValue<string>() != "applied") return null;
        // 依赖闭包一旦拉进额外的 root，退回的就不再是"底组之上的那一段"，维持拒绝。
        if (Ids(result["allocation"]?["dependency_live_root_ids"]).Length > 0) return null;
        if (result["video_groups"] is not JsonArray kept || kept.Count != 1 || kept[0] is not JsonObject keptGroup ||
            keptGroup["include_scene_clear"]?.GetValue<bool>() != true || keptGroup["transparent"]?.GetValue<bool>() != false) return null;
        demoted = Ids(result["allocation"]?["foreground_live_root_ids"]);
        return demoted.Length == 0 ? null : result;
    }

    private static int[] BlockingLiveRoots(JsonObject plan, JsonObject[] layers) =>
        (plan["composition"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
            .SkipWhile(entry => entry["video_group"] is null).Reverse().SkipWhile(entry => entry["video_group"] is null).Reverse()
            .Select(entry => HybridScenePlanner.Int(entry["live_root"])).OfType<int>()
            .Where(root => layers.Any(layer => AllocationRoot(layer) == root && VisibleDrawable(layer))).ToArray();

    private static string[] VisibleNames(JsonObject[] layers, int[] roots) => roots
        .SelectMany(root => layers.Where(layer => AllocationRoot(layer) == root && VisibleDrawable(layer)).Select(Name))
        .Where(name => name.Length > 0).Distinct().ToArray();

    /// <summary>按源作者根列可见层名：--retain-live 保留的是整棵作者根。</summary>
    private static string[] VisibleSourceNames(JsonObject[] layers, int[] roots) => roots
        .SelectMany(root => layers.Where(layer => SourceRoot(layer) == root && VisibleDrawable(layer)).Select(Name))
        .Where(name => name.Length > 0).Distinct().ToArray();

    private static int? SourceRoot(JsonObject layer) => HybridScenePlanner.Int(layer["root"] ?? layer["allocation_root"]);

    private static string Layout(JsonObject plan) => PlanSettings.Of(plan).VideoLayout;

    private static JsonObject[] Layers(JsonObject plan) => (plan["layers"] as JsonArray)?.OfType<JsonObject>().ToArray() ?? [];

    private static int[] Ids(JsonNode? node) => node is JsonArray array
        ? array.Select(HybridScenePlanner.Int).OfType<int>().ToArray() : [];

    private static int? AllocationRoot(JsonObject layer) => HybridScenePlanner.Int(layer["allocation_root"] ?? layer["root"]);

    private static bool VisibleDrawable(JsonObject layer) =>
        layer["visible"] is JsonValue visible && visible.TryGetValue(out bool shown) && shown &&
        layer["drawable"] is JsonValue drawable && drawable.TryGetValue(out bool draws) && draws;

    private static double? Fraction(JsonObject layer) =>
        layer["canvas_fraction"] is JsonValue value && value.TryGetValue(out double number) && double.IsFinite(number) ? number : null;

    private static string Name(JsonObject layer) =>
        layer["name"] is JsonValue value && value.TryGetValue(out string? text) && !string.IsNullOrWhiteSpace(text)
            ? text : HybridScenePlanner.Int(layer["id"])?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
}
