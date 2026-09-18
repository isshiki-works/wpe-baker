using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Baker.Core;

/// <summary>
/// 原生渲染器读不了作品里的某个素材文件（3D 模型、纹理），运行时观测起不来时的结构化结论。
/// 这是工具解析能力的缺口，不是对壁纸能否烘焙的判断：CLI 用单独的退出码报告，与"不适用"分开。
/// 识别只认原生渲染器固定的报错格式，认不出的渲染器失败照旧按分析失败处理。
/// </summary>
public static class AnalysisToolLimitation
{
    public const string Verdict = "tool_limitation";
    /// <summary>报告的 kind；不是 hybrid_video，bake 与 HybridPlanFormat 都不会把它当烘焙方案。</summary>
    public const string Kind = "analysis_tool_limitation";
    /// <summary>CLI 退出码表：0 正常、1 不适用或分析失败、3 预设包、4 工具局限、130 取消。</summary>
    public const int ExitCode = 4;

    /// <summary>一个读不了的素材文件。Problem 是规范化短码（invalid_utf8 等），Detail 是渲染器原话。</summary>
    public sealed record Finding(string AssetKind, string File, string? Field, string? Problem, string Detail,
        long? Offset, long? Boundary);

    // engine/src/Scene/Pkg/Parse/Puppet/MdlParser.cpp 的 ParseFailure：
    // "MDL parse failed: <path>: <reason> (offset=N, boundary=N)"
    private static readonly Regex MdlParse = new(
        @"^MDL parse failed: (?<file>.+?): (?<reason>.+?)(?: \(offset=(?<offset>-?\d+), boundary=(?<boundary>-?\d+)\))?$",
        RegexOptions.CultureInvariant);
    // ParseFailure 的 reason 固定写成"<问题> <字段>"。
    private static readonly Regex MdlReason = new(
        @"^(?<problem>invalid UTF-8 in|unterminated|truncated|unsupported|invalid boundary for|non-finite) (?<field>.+)$",
        RegexOptions.CultureInvariant);
    // 模型解析失败后外层再报一次，只有文件名。
    private static readonly Regex ModelParse = new(@"^parse (?:model|puppet) failed: (?<file>.+)$", RegexOptions.CultureInvariant);
    // 纹理解码被拒："prepare resource plan failed: texture <name> has an invalid <field>"
    private static readonly Regex TextureInvalid = new(
        @"^prepare resource plan failed: texture (?<file>.+?) has an invalid (?<field>[A-Za-z_ ]+)$", RegexOptions.CultureInvariant);

    private static string? ProblemCode(string phrase) => phrase switch
    {
        "invalid UTF-8 in" => "invalid_utf8",
        "unterminated" => "unterminated",
        "truncated" => "truncated",
        "unsupported" => "unsupported",
        "invalid boundary for" => "invalid_boundary",
        "non-finite" => "non_finite",
        _ => null
    };

    /// <summary>
    /// 从渲染器失败信息（RendererFailure 汇总的 result.json error/diagnostics 与 stderr ERROR 行）里挑出素材解析失败。
    /// 同一文件出现多次（stderr 与 diagnostics 各一次、外层 parse model failed 再一次）只留最详细的一条。
    /// </summary>
    public static IReadOnlyList<Finding> Classify(string? rendererMessage)
    {
        var findings = new List<Finding>();
        foreach (string raw in (rendererMessage ?? "").Split('\n'))
        {
            string line = raw.Trim();
            Finding? found = null;
            if (MdlParse.Match(line) is { Success: true } mdl)
            {
                string reason = mdl.Groups["reason"].Value;
                Match detail = MdlReason.Match(reason);
                found = new("model", mdl.Groups["file"].Value, detail.Success ? detail.Groups["field"].Value : null,
                    detail.Success ? ProblemCode(detail.Groups["problem"].Value) : null, reason,
                    Long(mdl.Groups["offset"]), Long(mdl.Groups["boundary"]));
            }
            else if (ModelParse.Match(line) is { Success: true } model)
                found = new("model", model.Groups["file"].Value, null, null, line, null, null);
            else if (TextureInvalid.Match(line) is { Success: true } texture)
                found = new("texture", texture.Groups["file"].Value, texture.Groups["field"].Value, "invalid",
                    "has an invalid " + texture.Groups["field"].Value, null, null);
            if (found is null) continue;
            int existing = findings.FindIndex(item => item.AssetKind == found.AssetKind &&
                string.Equals(Slash(item.File), Slash(found.File), StringComparison.OrdinalIgnoreCase));
            if (existing < 0) findings.Add(found);
            else if (findings[existing].Field is null && found.Field is not null) findings[existing] = found;
        }
        return findings;
    }

    /// <summary>生成写进 --out 的结论报告：blockers 逐文件点名，summary.verdict 为 tool_limitation。</summary>
    public static JsonObject BuildReport(IReadOnlyList<Finding> findings, JsonObject? scene, string source, string sourceSha256,
        string evidenceDirectory, string rendererMessage)
    {
        if (findings.Count == 0) throw new ArgumentException("A tool limitation needs at least one unreadable asset.", nameof(findings));
        var blockers = new JsonArray();
        var files = new JsonArray();
        foreach (Finding finding in findings)
        {
            JsonArray layers = LayersUsing(scene, finding);
            string layerIds = string.Join(", ", layers.OfType<JsonObject>()
                .Select(layer => $"{layer["id"]} \"{Messages.EscapeName(layer["name"]?.GetValue<string>())}\""));
            string layersZh = layerIds.Length == 0 ? "" : Messages.Get("asset_layers.used_by", Messages.Chinese, layerIds);
            string layersEn = layerIds.Length == 0 ? "" : Messages.Get("asset_layers.used_by", Messages.English, layerIds);
            string blocker;
            if (finding.AssetKind == "texture")
                blocker = Messages.Emit("blocker.tool_unsupported_texture", finding.File, finding.Field);
            else if (finding.Field is not null && finding.Problem is not null && finding.Offset is long offset)
            {
                string offsetText = offset.ToString(CultureInfo.InvariantCulture);
                blocker = Messages.EmitBilingual("blocker.tool_unsupported_model_field",
                    [finding.File, finding.Field, Messages.Get("asset_problem." + finding.Problem, Messages.Chinese), offsetText, layersZh],
                    [finding.File, finding.Field, Messages.Get("asset_problem." + finding.Problem, Messages.English), offsetText, layersEn]);
            }
            else
                blocker = Messages.EmitBilingual("blocker.tool_unsupported_model",
                    [finding.File, layersZh, finding.Detail], [finding.File, layersEn, finding.Detail]);
            blockers.Add(blocker);
            files.Add(new JsonObject
            {
                ["kind"] = finding.AssetKind, ["file"] = finding.File, ["field"] = finding.Field, ["problem"] = finding.Problem,
                ["renderer_detail"] = finding.Detail, ["offset"] = finding.Offset, ["boundary"] = finding.Boundary, ["layers"] = layers
            });
        }
        JsonArray localized = Messages.LocalizeAll(blockers);
        string firstZh = localized[0]?["zh"]?.GetValue<string>() ?? "", firstEn = localized[0]?["en"]?.GetValue<string>() ?? "";
        return new JsonObject
        {
            ["schema_version"] = 1,
            ["kind"] = Kind,
            ["status"] = Verdict,
            ["source"] = source,
            ["source_sha256"] = sourceSha256,
            ["tool_limitation"] = new JsonObject
            {
                ["stage"] = "runtime_probe",
                ["component"] = "native_renderer",
                ["files"] = files,
                ["renderer_message"] = rendererMessage,
                ["evidence_directory"] = evidenceDirectory
            },
            ["blockers"] = blockers,
            ["blockers_localized"] = localized,
            ["loop"] = new JsonObject { ["candidates"] = new JsonArray(), ["unresolved"] = new JsonArray(), ["unresolved_localized"] = new JsonArray() },
            ["summary"] = new JsonObject
            {
                ["verdict"] = Verdict, ["key"] = "summary.tool_limitation",
                ["zh"] = Messages.Get("summary.tool_limitation", Messages.Chinese, firstZh, findings.Count),
                ["en"] = Messages.Get("summary.tool_limitation", Messages.English, firstEn, findings.Count)
            }
        };
    }

    /// <summary>场景里直接引用这个模型文件的图层；纹理经材质间接引用，不追。</summary>
    private static JsonArray LayersUsing(JsonObject? scene, Finding finding)
    {
        var layers = new JsonArray();
        if (finding.AssetKind != "model" || scene?["objects"] is not JsonArray objects) return layers;
        foreach (JsonObject item in objects.OfType<JsonObject>())
        {
            JsonNode? model = item["model"] is JsonObject bound ? bound["value"] : item["model"];
            if (model is not JsonValue value || !value.TryGetValue(out string? path) ||
                !string.Equals(Slash(path), Slash(finding.File), StringComparison.OrdinalIgnoreCase)) continue;
            layers.Add(new JsonObject { ["id"] = item["id"]?.DeepClone(), ["name"] = item["name"]?.DeepClone() });
        }
        return layers;
    }

    private static string Slash(string path) => path.Replace('\\', '/').Trim();

    private static long? Long(Group group) =>
        group.Success && long.TryParse(group.Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long value) ? value : null;
}

/// <summary>分析因工具局限停止；<see cref="Report"/> 是可以直接写进 --out 的结构化结论。</summary>
public sealed class AnalysisToolLimitationException(JsonObject report, Exception? inner = null)
    : IOException(report["summary"]?["en"]?.GetValue<string>() ?? "Tool limitation.", inner)
{
    public JsonObject Report { get; } = report;
}
