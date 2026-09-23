using System.Text.Json.Nodes;
using static Baker.Core.SceneGraph;

namespace Baker.Core;

/// <summary>
/// 布局准入：全幅"单不透明组"可达性取证 → 布局冲突判定 → 尾组降级（退回尾部视频根，按新分组重求循环）
/// → 写 video_layout_admission 与冲突 blocker。冲突与降级本身仍由 FullFrameConflict / CompositionHierarchyConflict /
/// <see cref="FullFrameDemotion"/> 判定，这里只把它们在分析里的先后与落盘收成一处。
/// </summary>
internal sealed class LayoutAdmission
{
    /// <summary>布局按请求准入。</summary>
    internal const string AllowedStatus = "planned_layout_allowed";
    /// <summary>尾组降级生效后的布局准入状态。</summary>
    internal const string DemotedStatus = "planned_layout_allowed_after_demotion";
    private const string WholeLayerScope = "Layout permission is not proof of image correctness, looping, hardware decoding or playback benefit.";
    private const string EffectPrefixScope = "Effect-prefix caching retains the authored layer and suffix; it does not reorder whole-layer groups.";

    private readonly string requested;
    private readonly int groupCount;

    /// <summary>准入之后的 plan：尾组降级生效时是降级后的新 plan，否则就是传入的那一份。</summary>
    internal JsonObject Plan { get; }
    /// <summary>降级之后仍然成立的布局冲突；null 表示准入。</summary>
    internal Blocker? Conflict { get; }
    /// <summary>冲突靠尾组降级化解了。</summary>
    internal bool Demoted { get; }

    private LayoutAdmission(JsonObject plan, Blocker? conflict, bool demoted, string requested, int groupCount) =>
        (Plan, Conflict, Demoted, this.requested, this.groupCount) = (plan, conflict, demoted, requested, groupCount);

    /// <param name="plan">整层路线已定的 plan；取证字段直接写在它上面。</param>
    /// <param name="requested">请求的布局（full_frame / layered）。</param>
    /// <param name="effectPrefix">已改走特效前缀：不取证、不降级。</param>
    /// <param name="groupCount">构图阶段给出的视频组数。</param>
    /// <param name="resolveLoop">按降级后 plan 的视频组重求循环（含候选标注）；只在降级采纳时调用一次。</param>
    internal static LayoutAdmission Evaluate(JsonObject plan, string requested, bool effectPrefix, int groupCount,
        JsonArray dependencies, Func<JsonObject, JsonObject> resolveLoop)
    {
        bool demotable = requested == "full_frame" && !effectPrefix && groupCount > 1;
        // 先把"单不透明组可达性"写进 plan，冲突文案才能给出本场景真正可执行的指令。
        if (demotable) plan["full_frame_retention"] = FullFrameDemotion.Describe(plan, dependencies);
        // 一个视频组都没有时无从谈布局：blockers 已经说明依赖闭包之后没有输入无关的可视组，再追加一条
        // "改设置或选分层后重新分析"只会把用户引向无效操作。分配路径仍用这条冲突报告不透明视频的丢失。
        Blocker? conflict = (groupCount == 0 ? null : FullFrameConflict(plan)) ?? CompositionHierarchyConflict(plan);
        if (conflict is null || !demotable) return new(plan, conflict, false, requested, groupCount);
        // 尾组降级：把底组之上的视频 root 整体退回实时，是全幅被拒时唯一允许的自动补救。
        JsonObject? demoted = FullFrameDemotion.TryDemote(plan, dependencies, out JsonObject demotion);
        if (demoted is null)
        {
            plan["layout_admission_demotion"] = demotion;
            return new(plan, conflict, false, requested, groupCount);
        }
        demoted["layout_admission_demotion"] = demotion;
        if (plan["full_frame_retention"]?.DeepClone() is JsonObject retention)
        {
            retention["status"] = "applied";
            demoted["full_frame_retention"] = retention;
        }
        // 视频组少了被退回的层，循环必须按新的组重算，否则 plan 的周期仍引用已退出视频的分量。
        demoted["loop"] = resolveLoop(demoted);
        Routes.RefreshWholeLayer(demoted);
        return new(demoted, null, true, requested, groupCount);
    }

    /// <summary>写准入记录；冲突仍在且没改走特效前缀时并进 blockers，plan 回到 requires_resolution。</summary>
    internal void Record(bool effectPrefix)
    {
        if (Conflict is not null) Plan["whole_layer"]!.AsObject()["layout_conflict"] = Conflict.Text;
        Plan["video_layout_admission"] = AdmissionRecord(requested,
            effectPrefix ? "not_applicable_to_effect_prefix" :
                groupCount == 0 ? "not_applicable_no_video_group" :
                Conflict is not null ? ConflictStatus(Plan) :
                Demoted ? DemotedStatus : AllowedStatus,
            Conflict?.Text ?? (Demoted ? Plan["layout_admission_demotion"]?["reason"]?.GetValue<string>() : null),
            effectPrefix ? EffectPrefixScope : WholeLayerScope);
        if (Conflict is null || effectPrefix) return;
        PlanBlockers.Add(Plan["blockers"]!.AsArray(), Conflict);
        Plan["status"] = "requires_resolution";
    }

    /// <summary>
    /// 冲突的准入状态：full_frame 的唯一视频组排在搬不走的实时绘制之后时，这不是用户能选出来的布局，而是不可达。
    /// 分析（<see cref="Record"/>）与 bake 侧前景分配（<see cref="PlanTransforms.ApplyAllocation"/>）共用这一份。
    /// </summary>
    internal static string ConflictStatus(JsonObject plan) =>
        SingleShotAllocation.UnreachableBlockingRoots(plan).Length > 0 ? "full_frame_unreachable" : "requires_user_choice";

    /// <summary>video_layout_admission 记录（四个字段、固定顺序）；分析的准入与 bake 侧前景分配都经这里写。</summary>
    internal static JsonObject AdmissionRecord(string requested, string status, string? reason, string scope) =>
        new() { ["requested"] = requested, ["status"] = status, ["reason"] = reason, ["scope"] = scope };

    /// <summary>布局准入通过时的记录（分配与分析同一句 scope）。</summary>
    internal static JsonObject AllowedRecord(string requested) => AdmissionRecord(requested, AllowedStatus, null, WholeLayerScope);

    internal static Blocker? FullFrameConflict(JsonObject plan)
    {
        string layout = PlanSettings.Of(plan).VideoLayout;
        if (layout == "layered") return null;
        if (layout != "full_frame") throw new InvalidDataException("Unknown video layout; use full_frame or layered.");
        JsonArray groups = plan["video_groups"]?.AsArray() ?? throw new InvalidDataException("Video groups are missing.");
        if (groups.Count == 1 && groups[0]?["include_scene_clear"]?.GetValue<bool>() == true) return null;
        // 唯一一组被不可搬动的实时绘制挡在后面时，这个布局对该场景不可达：报出阻挡者，不提建议。
        if (SingleShotAllocation.UnreachableBlockingRoots(plan) is { Length: > 0 } blocking)
            return SingleShotAllocation.UnreachableReason(plan, blocking);
        var composition = (plan["composition"]?.AsArray() ?? []).OfType<JsonObject>().ToArray();
        string[] LiveNames(IEnumerable<JsonObject> entries) => entries
            .Where(item => item["live_root"] is not null).Select(item => item["live_root"]!.GetValue<int>())
            .SelectMany(id => plan["layers"]?.AsArray().OfType<JsonObject>().Where(layer => Int(layer["allocation_root"] ?? layer["root"]) == id &&
                layer["visible"] is JsonValue visible && visible.TryGetValue<bool>(out bool shown) && shown &&
                layer["drawable"] is JsonValue drawable && drawable.TryGetValue<bool>(out bool draws) && draws)
                .Select(layer => layer["name"]?.GetValue<string>()) ?? [])
            .Where(name => !string.IsNullOrWhiteSpace(name)).Cast<string>().Distinct().ToArray();
        string[] names = LiveNames(composition
            .SkipWhile(item => item["video_group"] is null).Reverse().SkipWhile(item => item["video_group"] is null).Reverse());
        // 排在第一组视频之前先画的可见实时层：只进文案，用来点名"挡在前面"的是谁。
        string[] leading = composition.Any(item => item["video_group"] is not null)
            ? LiveNames(composition.TakeWhile(item => item["video_group"] is null)) : [];
        // 文案两边意图取并集：分类与双语来自文案表，本场景真正可执行的选项来自全幅降级的取证。
        return PlanNarrative.FullFrameConflict(groups, names, FullFrameDemotion.ConflictOptions(plan), leading);
    }

    internal static Blocker? CompositionHierarchyConflict(JsonObject plan,
        IReadOnlyDictionary<int, JsonObject>? sourceObjects = null, JsonArray? dependencies = null)
    {
        HybridPlanFormat.Validate(plan);
        JsonArray layers = plan["layers"]!.AsArray();
        var objects = sourceObjects ?? layers.OfType<JsonObject>().ToDictionary(Id, layer => new JsonObject {
            ["id"] = layer["id"]!.DeepClone(), ["parent"] = layer["parent"]?.DeepClone() });
        int nextId = checked(objects.Keys.Max() + 1);
        var replacements = plan["video_groups"]!.AsArray().OfType<JsonObject>().ToDictionary(
            group => group["id"]!.GetValue<string>(), group => new JsonObject { ["id"] = nextId++, ["parent"] = group["parent_id"]?.DeepClone() });
        try { _ = HybridBakeService.AssembleAllocationObjects(objects, plan, replacements, dependencies ?? new JsonArray()); return null; }
        catch (InvalidDataException error) when (Blocker.Of(error) is Blocker blocker) { return blocker; }
    }
}
