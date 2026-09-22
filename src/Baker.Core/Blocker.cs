using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>
/// 拒绝原因编号。每个编号对应文案表里的一个键（默认 "blocker." + 蛇形名，个别历史键见 <see cref="BlockerCodes.Key"/>）。
/// 程序只按编号判断；文案只在写 plan 时由 <see cref="Messages"/> 渲染。
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
    public string Text => Messages.RenderLegacy(Key, Args);

    /// <summary>
    /// 分析过程中放进 plan 的 blockers 数组的节点：{key, zh, en, params, text}。
    /// 写 plan 时 <see cref="PlanBlockers.Finish"/> 把 text 拆回 blockers、其余进 blockers_localized。
    /// </summary>
    public JsonObject ToNode()
    {
        JsonObject node = Messages.Localized(Key, ZhArgs ?? Args, Args);
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

/// <summary>plan 里 blockers 数组的读写：分析中是 <see cref="Blocker.ToNode"/> 节点，写 plan 时拆成 v3 字段。</summary>
public static class PlanBlockers
{
    /// <summary>
    /// 数组里每一条的编号。分析中的节点直接读 key；已经 <see cref="Finish"/> 过的英文原文按下标读同级的
    /// blockers_localized（它是 plan 自带的结构化字段，不是拿句子反查）。
    /// </summary>
    public static IEnumerable<BlockerCode> Codes(JsonObject? owner) =>
        Codes(owner?["blockers"] as JsonArray, owner?["blockers_localized"] as JsonArray);

    /// <summary>同上，直接给数组（分析中的局部 blockers 没有 blockers_localized）。</summary>
    public static IEnumerable<BlockerCode> Codes(JsonArray? items, JsonArray? localized = null)
    {
        if (items is null) yield break;
        for (int i = 0; i < items.Count; ++i)
        {
            string? key = items[i] is JsonObject node ? node["key"]?.GetValue<string>()
                : localized is not null && i < localized.Count ? localized[i]?["key"]?.GetValue<string>() : null;
            yield return BlockerCodes.FromKey(key) ?? throw new InvalidOperationException("blocker 没有编号：" + items[i]?.ToJsonString());
        }
    }

    /// <summary>追加一条；同编号同参数的已在数组里就不重复加。</summary>
    public static void Add(JsonArray items, Blocker blocker)
    {
        JsonObject node = blocker.ToNode();
        if (!items.Any(item => JsonNode.DeepEquals(item, node))) items.Add(node);
    }

    /// <summary>
    /// 把 owner.blockers 写成 plan v3 形态：blockers 为英文原文数组，blockers_localized 为 {key, zh, en, params}。
    /// 已经写过的条目（英文原文）沿用同下标的 blockers_localized，所以可以反复调用。
    /// </summary>
    public static void Finish(JsonObject owner)
    {
        var items = owner["blockers"] as JsonArray;
        var previous = owner["blockers_localized"] as JsonArray;
        var texts = new JsonArray();
        var localized = new JsonArray();
        for (int i = 0; i < (items?.Count ?? 0); ++i)
        {
            if (items![i] is JsonObject node)
            {
                var entry = node.DeepClone().AsObject();
                texts.Add(entry["text"]!.GetValue<string>());
                entry.Remove("text");
                localized.Add(entry);
            }
            else if (previous is not null && i < previous.Count && previous[i]?["key"] is not null)
            {
                texts.Add(items[i]!.DeepClone());
                localized.Add(previous[i]!.DeepClone());
            }
            else throw new InvalidOperationException("blocker 没有编号：" + items[i]?.ToJsonString());
        }
        if (items is not null) owner["blockers"] = texts;
        owner["blockers_localized"] = localized;
    }
}
