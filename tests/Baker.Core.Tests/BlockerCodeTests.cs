using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Baker.Core;
using Xunit;

// C1.1：拒绝原因按编号走。L0 守住"能力缺口按编号分诊"（C0.2 消融 M12），L2 守住编号表与文案表一一对应。
[Trait("Layer", "L0")]
public class BlockerTriageTests
{
    private static JsonObject Verdict(JsonObject plan) => HybridSuitability.Verdict(plan);

    private static JsonObject Plan(params Blocker[] blockers)
    {
        var plan = new JsonObject
        {
            ["route"] = "whole_layer",
            ["video_groups"] = new JsonArray(new JsonObject { ["id"] = "group-1" }),
            ["layers"] = new JsonArray(),
            ["loop"] = new JsonObject { ["candidates"] = new JsonArray(new JsonObject()), ["unresolved"] = new JsonArray() },
        };
        PlanBlockers.Set(plan, blockers);
        return plan;
    }

    [Theory]
    [InlineData(BlockerCode.HdrRadianceOpen, "capture_capability_gap")]
    [InlineData(BlockerCode.HdrRadianceOpenProperty, "capture_capability_gap")]
    [InlineData(BlockerCode.PerspectiveNeedsScreenspace, "capture_capability_gap")]
    [InlineData(BlockerCode.CameraPathNeedsEnvelope, "blockers_need_a_decision")]
    public void CapabilityGapIsTriagedByCode(BlockerCode code, string rule)
    {
        int arity = BlockerCatalogTests.Arity(BlockerCodes.Key(code));
        var blocker = new Blocker(code, Enumerable.Repeat<object?>("x", arity).ToArray());
        JsonObject verdict = Verdict(Plan(blocker));
        Assert.Equal(rule, verdict["rule"]!.GetValue<string>());
    }

    /// <summary>效果前缀回退只在拒因全是"无独立组"（两种形态）时才试；混进任何别的拒因就不试。</summary>
    [Theory]
    [InlineData(new[] { BlockerCode.NoInputIndependentGroup }, false)]
    [InlineData(new[] { BlockerCode.NoInputIndependentGroupGeneric }, false)]
    [InlineData(new[] { BlockerCode.NoInputIndependentGroup, BlockerCode.NoInputIndependentGroupGeneric }, false)]
    [InlineData(new[] { BlockerCode.NoInputIndependentGroupGeneric, BlockerCode.CameraPathNeedsEnvelope }, true)]
    [InlineData(new[] { BlockerCode.HdrRadianceOpen }, true)]
    public void PrefixFallbackOnlyForNoIndependentGroup(BlockerCode[] codes, bool blocked)
    {
        // 初判拒因在内存里是 Blocker 列表（C2.2d2），前缀回退只看编号。
        Assert.Equal(blocked, Baker.Core.Verdict.PrefixSafetyBlockedBy(codes));
    }

    [Fact]
    public void TriageSurvivesFinishedPlan()
    {
        // plan 里的拒因只有 v3 一种形态：blockers 是英文原文，编号从同下标的 blockers_localized 取，不看句子。
        JsonObject plan = Plan(new Blocker(BlockerCode.HdrRadianceOpen, ["x"]));
        Assert.IsType<string>(plan["blockers"]![0]!.GetValue<string>());
        Assert.Equal("capture_capability_gap", Verdict(plan)["rule"]!.GetValue<string>());
    }
}

[Trait("Layer", "L2")]
public class BlockerCatalogTests
{
    private static readonly Regex Placeholder = new(@"\{(\d+)\}", RegexOptions.CultureInvariant);

    private static int[] Indices(string template) =>
        Placeholder.Matches(template).Select(match => int.Parse(match.Groups[1].Value)).Distinct().Order().ToArray();

    /// <summary>一个键的参数个数：中文、英文、v3 模板里用到的最大占位符 + 1（v3 专用的尾部参数也算）。</summary>
    internal static int Arity(string key)
    {
        MessageCatalog.Entry entry = MessageCatalog.Find(key)!;
        int[] all = Indices(entry.Zh).Concat(Indices(entry.En)).Concat(Indices(entry.LegacyTemplate)).ToArray();
        return all.Length == 0 ? 0 : all.Max() + 1;
    }

    [Fact]
    public void EveryCodeHasCompleteCatalogEntry()
    {
        foreach (BlockerCode code in Enum.GetValues<BlockerCode>())
        {
            string key = BlockerCodes.Key(code);
            MessageCatalog.Entry? entry = MessageCatalog.Find(key);
            Assert.True(entry is not null, $"{code} 缺文案键 {key}");
            Assert.False(string.IsNullOrWhiteSpace(entry!.Zh), $"{key} 中文模板为空");
            Assert.False(string.IsNullOrWhiteSpace(entry.En), $"{key} 英文模板为空");
            Assert.Equal(code, BlockerCodes.FromKey(key));
            int arity = Arity(key);
            // 参数个数对上时三种模板都能渲染出不含占位符的句子。
            JsonObject node = new Blocker(code, Enumerable.Range(0, arity).Select(i => (object?)("a" + i)).ToArray()).ToNode();
            foreach (string field in new[] { "zh", "en", "text" })
                Assert.DoesNotMatch(Placeholder, node[field]!.GetValue<string>());
        }
    }

    [Fact]
    public void EveryBlockerKeyInCatalogHasCode()
    {
        // 反方向：文案表里 blocker.* 键都要有编号，没有编号的就是死键。
        string[] orphans = MessageCatalog.Keys.Where(key => key.StartsWith("blocker.", StringComparison.Ordinal) && BlockerCodes.FromKey(key) is null).ToArray();
        Assert.Empty(orphans);
    }

    [Fact]
    public void EveryCodeIsProducedInSource()
    {
        // 编号表不养死编号：每个编号在产品代码里至少被构造一次。
        string root = Path.Combine(LocalTools.RepositoryRoot, "src");
        string source = string.Join("\n", Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.EndsWith("Blocker.cs", StringComparison.Ordinal)).Select(File.ReadAllText));
        string[] unused = Enum.GetNames<BlockerCode>().Where(name => !source.Contains("BlockerCode." + name, StringComparison.Ordinal)).ToArray();
        Assert.Empty(unused);
    }
}
