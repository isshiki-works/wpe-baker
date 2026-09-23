using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>
/// 拒绝原因编号。每个编号对应文案表里的一个键（默认 "blocker." + 蛇形名，个别历史键见 <see cref="BlockerCodes.Key"/>）。
/// 程序只按编号判断；文案只在写 plan 时由 <see cref="MessageCatalog"/> 渲染。
/// </summary>
public enum BlockerCode
{
    BakeAllocation,
    MissingScriptFaultEvidence,
    NoInputIndependentGroup,
    NoInputIndependentGroupGeneric,
    HdrRadianceOpen,
    HdrRadianceOpenProperty,
    PerspectiveNeedsScreenspace,
    CameraPathNeedsEnvelope,
    RuntimeProjectionRequired,
    FullframeNeedsOpaqueGroup,
    FullframeNeedsOpaqueGroupNoLive,
    FullframeSingleTransparentGroup,
    FullframeSingleTransparentGroupLive,
    FullframeNoVideoGroup,
    FullframeNeedsOpaqueGroupOptions,
    FullframeSplitGroupsOptions,
    FullframeSingleTransparentGroupOptions,
    FullframeNoVideoGroupOptions,
    FullframeUnreachable,
    ForegroundSplitsVideoGroup,
    ForegroundOutsideComposition,
    HierarchyChangesDrawOrder,
    OmittedSnapshotDependency,
    ReplacementParentMismatch,
    PublicLayerQuery,
    VideoShell,
    ResidualMaskingLayout,
    LoopNoCommonFrame,
    LoopFixedPeriodExceedsCeiling,
    LoopNoExactVideoRetime,
    ToolUnsupportedModelField,
    ToolUnsupportedModel,
    ToolUnsupportedTexture,
    TooManyVideoGroups,
}

/// <summary>
/// 一条拒绝原因：编号 + 参数。<see cref="Args"/> 是英文（plan v3 与 en）参数；嵌进句子里的那半句本身分语言时
/// 另给 <see cref="ZhArgs"/>，为 null 表示两种语言共用一套。
/// </summary>
public sealed record Blocker(BlockerCode Code, object?[] Args, object?[]? ZhArgs = null)
{
    public Blocker(BlockerCode code) : this(code, []) { }

    public string Key => BlockerCodes.Key(Code);

    /// <summary>plan v3 的 blockers 字段写的英文原文（C3 切 plan v4 时随 Legacy 模板一起删）。</summary>
    public string Text => MessageCatalog.RenderLegacy(Key, Args);

    /// <summary>plan v3 的 blockers_localized 条目：{key, zh, en, params}。</summary>
    public JsonObject Localized() => MessageCatalog.Localized(Key, ZhArgs ?? Args, Args);

    /// <summary>
    /// 独立节点 {key, zh, en, params, text}：给不经 plan 的报告（工具局限）先攒再用 <see cref="PlanBlockers.Finish"/> 拆，
    /// 以及测试比对用。plan 里的拒因不用它，直接经 <see cref="PlanBlockers"/> 写成 v3 两个字段。
    /// </summary>
    public JsonObject ToNode()
    {
        JsonObject node = Localized();
        node["text"] = Text;
        return node;
    }

    /// <summary>带编号抛出：消息仍是 v3 英文原文，捕获方用 <see cref="Of"/> 取回编号，不拿异常文本当 blocker。</summary>
    public InvalidDataException ToException()
    {
        var error = new Message(Key, Args, ZhArgs).Error(text => new InvalidDataException(text));
        error.Data[nameof(Blocker)] = this;
        return error;
    }

    public static Blocker? Of(Exception error) => error.Data[nameof(Blocker)] as Blocker;
}

public static class BlockerCodes
{
    private static readonly Dictionary<BlockerCode, string> Special = new()
    {
        [BlockerCode.TooManyVideoGroups] = "preset.too_many_video_groups",
    };

    private static readonly Dictionary<string, BlockerCode> ByKey =
        Enum.GetValues<BlockerCode>().ToDictionary(Key, code => code, StringComparer.Ordinal);

    /// <summary>编号 → 文案表键。</summary>
    public static string Key(BlockerCode code) =>
        Special.TryGetValue(code, out string? key) ? key : "blocker." + Snake(code.ToString());

    /// <summary>文案表键 → 编号；不是 blocker 键时返回 null。</summary>
    public static BlockerCode? FromKey(string? key) => key is not null && ByKey.TryGetValue(key, out var code) ? code : null;

    private static string Snake(string name) =>
        string.Concat(name.Select((c, i) => char.IsUpper(c) ? (i > 0 ? "_" : "") + char.ToLowerInvariant(c) : c.ToString()));
}

/// <summary>
/// plan 里拒因的读写。plan JSON 只有一种形态（plan v3）：<c>blockers</c> 是英文原文数组，<c>blockers_localized</c> 是同下标的
/// {key, zh, en, params}，追加时两边一起写、编号只从 blockers_localized 读。分析中的初判拒因在内存里是 <see cref="Blocker"/> 列表
/// （<see cref="Verdict"/>），组 plan 时由 <see cref="Set"/> 渲染一次；v4 删字符串数组与 whole_layer 副本（C3）。
/// </summary>
public static class PlanBlockers
{
    private const string Texts = "blockers", Localized = "blockers_localized";

    /// <summary>owner 上每一条拒因的编号，按同下标的 blockers_localized 读（它是 plan 自带的结构化字段，不是拿句子反查）。</summary>
    public static IEnumerable<BlockerCode> Codes(JsonObject? owner) =>
        Codes(owner?[Texts] as JsonArray, owner?[Localized] as JsonArray);

    /// <summary>同上，直接给两个同下标数组。</summary>
    public static IEnumerable<BlockerCode> Codes(JsonArray? items, JsonArray? localized)
    {
        if (items is null) yield break;
        for (int i = 0; i < items.Count; ++i)
        {
            string? key = localized is not null && i < localized.Count ? localized[i]?["key"]?.GetValue<string>() : null;
            yield return BlockerCodes.FromKey(key) ?? throw UnnumberedPlan();
        }
    }

    /// <summary>
    /// 拒因没有编号：plan 是 C1.1a 之前分析出来的（v3 字段齐全，blockers_localized 的 key 为 null）。
    /// 与读到旧版本号一样按旧版 plan 处理，提示重新分析（决策 1：旧 plan 不迁移）。
    /// </summary>
    private static InvalidDataException UnnumberedPlan() =>
        new Message("plan.legacy_unnumbered_blocker").Error(text => new InvalidDataException(text));

    /// <summary>把 owner 的拒因整体换成 <paramref name="blockers"/>（两个字段已存在时原位替换，不存在时追加在末尾）。</summary>
    public static void Set(JsonObject owner, IEnumerable<Blocker> blockers)
    {
        var texts = new JsonArray();
        var localized = new JsonArray();
        foreach (Blocker blocker in blockers)
        {
            texts.Add(blocker.Text);
            localized.Add(blocker.Localized());
        }
        owner[Texts] = texts;
        owner[Localized] = localized;
    }

    /// <summary>把 <paramref name="from"/> 的拒因各拷一份给 <paramref name="to"/>（whole_layer 副本按 plan 当前拒因重写时用）。</summary>
    public static void CopyTo(JsonObject from, JsonObject to)
    {
        to[Texts] = from[Texts]?.DeepClone() ?? new JsonArray();
        to[Localized] = from[Localized]?.DeepClone() ?? new JsonArray();
    }

    /// <summary>追加一条；同编号同参数的已在 owner 里就不重复加。字段缺失时补上（追加在末尾）。</summary>
    public static void Add(JsonObject owner, Blocker blocker)
    {
        if (owner[Texts] is not JsonArray texts) owner[Texts] = texts = new JsonArray();
        if (owner[Localized] is not JsonArray localized) owner[Localized] = localized = new JsonArray();
        string text = blocker.Text;
        JsonObject entry = blocker.Localized();
        for (int i = 0; i < texts.Count; ++i)
            if (texts[i] is JsonValue value && value.TryGetValue(out string? existing) && existing == text &&
                i < localized.Count && JsonNode.DeepEquals(localized[i], entry)) return;
        texts.Add(text);
        localized.Add(entry);
    }

    /// <summary>
    /// 写 plan 时把 blockers_localized 挪到 owner 末尾：v3 的字节顺序里它排在分析期间写下的所有字段之后
    /// （C2.2d2 之前由写 plan 前的 Finish 追加）。只由 <see cref="PlanWriter"/> 在挂双语字段之前调一次。
    /// </summary>
    internal static void PlaceLocalizedLast(JsonObject owner)
    {
        if (owner[Localized] is not JsonArray localized) return;
        owner.Remove(Localized);
        owner[Localized] = localized;
    }

    /// <summary>
    /// 在 plan 之外用 <see cref="Blocker.ToNode"/> 节点攒出的拒因数组（工具局限报告）拆成 v3 两个字段。
    /// plan 本身从不持有节点，所以这里只认节点。
    /// </summary>
    public static void Finish(JsonObject owner)
    {
        var texts = new JsonArray();
        var localized = new JsonArray();
        foreach (JsonNode? item in owner[Texts] as JsonArray ?? [])
        {
            var entry = (item as JsonObject ?? throw UnnumberedPlan()).DeepClone().AsObject();
            texts.Add(entry["text"]!.GetValue<string>());
            entry.Remove("text");
            localized.Add(entry);
        }
        owner[Texts] = texts;
        owner[Localized] = localized;
    }
}
