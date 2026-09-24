using System.Text.Json.Nodes;

namespace Baker.Core;

/// 两条结构/证据判据，与任何单张壁纸无关。
/// 规则一：事件触发的一次性动画轨不得进入循环视频组；加载即播的按入场处理（IntroSeconds）。
/// 规则二：唯一视频组被不可搬动的实时绘制挡在后面时，full_frame 布局不可达。
internal static class SingleShotAllocation
{
    internal const string LiveReason = "single_shot_animation";
    internal const string PrecedingRootsField = "preceding_visible_live_roots";

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    private static bool? Flag(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out bool flag) ? flag : null;

    private static int? Number(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out int id) ? id : null;

    /// 运行时证据里 confidence=high、looping=false、playback_mode=single 的 authored 轨（视频轨有自己的循环与时长判据，不算）。
    internal static bool IsSingleShot(JsonObject track) =>
        !string.Equals(Text(track["mechanism"]), "video", StringComparison.OrdinalIgnoreCase) &&
        Flag(track["looping"]) == false &&
        string.Equals(Text(track["playback_mode"]), "single", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Text(track["confidence"]), "high", StringComparison.OrdinalIgnoreCase);

    /// 事件触发的单次动画轨（播放时刻不定）所属层判为实时。场景加载即播的单次轨不在此列：
    /// 视频按入场结束后的定格态录制，入场那几秒成品显示原作图层（SceneAssembler.ApplyIntro）。
    /// <paramref name="loadPlayed"/>（settings.single_shot_live，入场切换做不成时的退回）：加载即播的也判实时，即旧行为。
    internal static IEnumerable<int> LiveOwners(JsonObject runtime, bool loadPlayed) =>
        (runtime["runtime_animation_periods"]?.AsArray() ?? []).OfType<JsonObject>()
            .Where(track => IsSingleShot(track) && (loadPlayed || Flag(track["event_driven"]) == true))
            .Select(track => Number(track["source_owner_layer_id"])).OfType<int>();

    /// 入场秒数：相机入场（projection.camera_intro）与进了视频组的图层加载即播单次轨取最大；没有为 0。
    internal static double IntroSeconds(JsonObject plan, JsonObject runtime) =>
        Math.Max(IntroTrackSeconds(plan, runtime), SceneGraph.Numeric(plan["projection"]?["camera_intro"]?["seconds"], 0));

    /// 进了视频组的图层加载即播单次轨的最长时长（时长 / 速率）；没有为 0。
    internal static double IntroTrackSeconds(JsonObject plan, JsonObject runtime)
    {
        var baked = (plan["video_groups"] as JsonArray ?? []).OfType<JsonObject>()
            .SelectMany(group => (group["layer_ids"] as JsonArray ?? []).Select(Number)).OfType<int>().ToHashSet();
        return (runtime["runtime_animation_periods"]?.AsArray() ?? []).OfType<JsonObject>()
            .Where(track => IsSingleShot(track) && Flag(track["event_driven"]) != true &&
                Number(track["source_owner_layer_id"]) is int owner && baked.Contains(owner))
            .Select(track => SceneGraph.Numeric(track["duration_seconds"], 0) /
                SceneGraph.Numeric(track["playback_rate"] ?? track["current_rate"], 1))
            .Where(double.IsFinite).DefaultIfEmpty(0).Max();
    }

    /// 唯一视频组不承担场景清屏、且决定这一点的那批前置可见绘制 live 根没有一个属于可提前景集合
    /// （occlusion_tradeoff.promoted_roots）时，返回这批阻挡根；否则返回空，表示这不是不可达情形。
    internal static int[] UnreachableBlockingRoots(JsonObject plan)
    {
        if (plan["video_groups"]?.AsArray() is not JsonArray groups || groups.Count != 1 ||
            groups[0] is not JsonObject group || Flag(group["include_scene_clear"]) != false) return [];
        int[] blocking = (group[PrecedingRootsField]?.AsArray() ?? []).Select(node => Number(node)).OfType<int>().ToArray();
        if (blocking.Length == 0) return [];
        var movable = (plan["occlusion_tradeoff"]?["promoted_roots"]?.AsArray() ?? []).OfType<JsonObject>()
            .Select(entry => Number(entry["root_id"])).OfType<int>().ToHashSet();
        return blocking.Any(movable.Contains) ? [] : blocking;
    }

    /// 陈述哪些实时绘制挡在视频组之前、为什么搬不走，并给出这个场景真正可执行的解锁路径：
    /// 反事实实测里这一形态 4/4 都能靠关掉挡路的可取舍元素进整幅，所以不再说"改设置也没用"。
    internal static Blocker UnreachableReason(JsonObject plan, int[] blocking)
    {
        var layers = (plan["layers"]?.AsArray() ?? []).OfType<JsonObject>().ToArray();
        string Describe(int root)
        {
            var owned = layers.Where(layer => Number(layer["allocation_root"] ?? layer["root"]) == root).ToArray();
            string name = owned.Where(layer => Flag(layer["visible"]) == true && Flag(layer["drawable"]) == true)
                .Select(layer => Text(layer["name"])).FirstOrDefault(text => !string.IsNullOrWhiteSpace(text))
                ?? Text(owned.FirstOrDefault()?["name"]) ?? root.ToString();
            string[] reasons = owned.SelectMany(layer => (layer["reasons"]?.AsArray() ?? []).Select(node => Text(node)))
                .OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
            return reasons.Length == 0 ? name : $"{name} ({string.Join(", ", reasons)})";
        }
        // 文案入 MessageCatalog 表（key: blocker.fullframe_unreachable），legacy 英文逐字不变，中文与新英文在 blockers_localized 里给出。
        string listed = string.Join("; ", blocking.Select(Describe));
        var (zh, en) = UnlockPath(layers, blocking);
        return new Blocker(BlockerCode.FullframeUnreachable, [listed, en], [listed, zh]);
    }

    /// 挡路的这批根里哪些是可取舍元素、怎么关（中英各一句）。全是视差时只要固定视角，不必排除图层。
    private static (string Zh, string En) UnlockPath(JsonObject[] layers, int[] blocking)
    {
        var owned = layers.Where(layer => Number(layer["allocation_root"] ?? layer["root"]) is int root && blocking.Contains(root)).ToArray();
        string[] kinds = [.. owned.Where(layer => Text(layer["tradeoff_class"]) == TradeoffOptions.Tradeoff)
            .SelectMany(layer => (layer["tradeoff_kinds"]?.AsArray() ?? []).Select(node => Text(node)).OfType<string>())
            .Distinct(StringComparer.Ordinal)];
        bool parallax = kinds.Contains("parallax", StringComparer.Ordinal);
        bool parallaxOnly = parallax && kinds.Length == 1;
        string ids = string.Join(",", blocking.Order());
        string command = parallaxOnly ? "--interaction fixed"
            : string.Join(" ", new[] { "--exclude-layers " + ids, parallax ? "--interaction fixed" : null }.OfType<string>());
        if (kinds.Length == 0)
            return ($"用 {command} 关掉它们后重新分析", $"turn them off with {command} and analyze again");
        return ($"关掉{TradeoffOptions.KindList(kinds, MessageCatalog.Chinese)}：{command}",
            $"turn off {TradeoffOptions.KindList(kinds, MessageCatalog.English)}: {command}");
    }
}
