using System.Reflection;
using System.Text.Json.Nodes;
using Baker.Core;

/// <summary>
/// 原生渲染器读不了素材文件时的工具局限结论。输入是渲染器真实落盘的两份证据（native/result.json 与
/// renderer.stderr.log，格式照 3737268876 的实际输出），经生产代码 RendererFailure 汇总后再分类。
/// </summary>
internal static class AnalysisToolLimitationChecks
{
    private const string MdlLine = "MDL parse failed: models/link_adult/link_adult.mdl: invalid UTF-8 in animation play_mode (offset=380009, boundary=463671)";

    public static async Task RunAsync(Action<bool, string> Check, string root)
    {
        string output = Path.Combine(root, "tool-limitation-probe");
        Directory.CreateDirectory(Path.Combine(output, "native"));
        string resultPath = Path.Combine(output, "native", "result.json");
        await File.WriteAllTextAsync(resultPath, new JsonObject
        {
            ["schema_version"] = 1, ["status"] = "failed", ["written_frames"] = 0, ["renderer_error_count"] = 3,
            ["error"] = "Offline script initialization failed",
            ["diagnostics"] = new JsonArray("Scene scripts execute once per simulated frame; frame-count-based scripts can change behavior when FPS changes", MdlLine)
        }.ToJsonString());
        await File.WriteAllTextAsync(Path.Combine(output, "renderer.stderr.log"),
            "[2026-09-17T06:18:42Z WARN] shader compile note\n" +
            "[2026-09-17T06:18:42Z ERROR] " + MdlLine + "\n" +
            "[2026-09-17T06:18:42Z ERROR] parse model failed: models/link_adult/link_adult.mdl\n" +
            "[2026-09-17T06:18:42Z INFO] literal ERROR in an ordinary message\n" +
            "[2026-09-17T06:18:42Z ERROR] prepare resource plan failed: texture models/link_child/childlink_01 has an invalid mipmap\n");
        var rendererFailure = typeof(NativeRenderRunner).GetMethod("RendererFailure", BindingFlags.Static | BindingFlags.NonPublic)!;
        var failure = (IOException)rendererFailure.Invoke(null, new object?[] { resultPath, "wpe-render.exe exited 1; see " + output, null })!;

        // ---- 分类：只认渲染器固定的素材解析报错，同一文件只留最详细的一条 ----
        var findings = AnalysisToolLimitation.Classify(failure.Message);
        Check(findings.Count == 2, "renderer failure with a model and a texture parse error yields exactly two unreadable assets");
        var model = findings[0];
        Check(model.AssetKind == "model" && model.File == "models/link_adult/link_adult.mdl" &&
            model.Field == "animation play_mode" && model.Problem == "invalid_utf8" &&
            model.Offset == 380009 && model.Boundary == 463671,
            "the MDL parse failure is split into file, field, problem, offset and boundary");
        Check(findings[1] is { AssetKind: "texture", File: "models/link_child/childlink_01", Field: "mipmap" },
            "the texture decode rejection names the texture and the rejected data");

        var reversed = AnalysisToolLimitation.Classify("parse model failed: models/link_adult/link_adult.mdl\n" + MdlLine);
        Check(reversed.Count == 1 && reversed[0].Field == "animation play_mode" && reversed[0].Offset == 380009,
            "the detailed MDL line replaces the bare parse-model line whichever comes first");

        Check(AnalysisToolLimitation.Classify(
                "wpe-render.exe exited 1; see x\nOffline script initialization failed\nmaterial parse failed: materials/missing.json\n" +
                "[out#0/mp4] Error writing trailer: No space left on device\nRenderer exited 1, encoder exited 0; original stderr logs are retained.").Count == 0 &&
            AnalysisToolLimitation.Classify(null).Count == 0,
            "renderer failures that are not asset parse errors are not reported as a tool limitation");

        var unknownReason = AnalysisToolLimitation.Classify("MDL parse failed: models/a.mdl: invalid MDLA end_offset (offset=12, boundary=8)");
        Check(unknownReason.Count == 1 && unknownReason[0].Field is null && unknownReason[0].Problem is null &&
            unknownReason[0].Detail == "invalid MDLA end_offset" && unknownReason[0].Offset == 12,
            "an MDL reason outside the fixed problem wording keeps the renderer's own words");

        // ---- 结论报告 ----
        var scene = new JsonObject
        {
            ["objects"] = new JsonArray(
                new JsonObject { ["id"] = 654, ["name"] = "Link" },
                new JsonObject { ["id"] = 536, ["name"] = "link_adult", ["model"] = "models/link_adult/link_adult.mdl", ["parent"] = 654 },
                new JsonObject { ["id"] = 571, ["name"] = "link_child", ["model"] = "models\\link_child\\link_child.mdl" })
        };
        JsonObject report = AnalysisToolLimitation.BuildReport(findings, scene, "D:/src/scene.pkg", "abc", output, failure.Message);
        Check(report["summary"]?["verdict"]?.GetValue<string>() == AnalysisToolLimitation.Verdict &&
            report["status"]?.GetValue<string>() == "tool_limitation" && report["kind"]?.GetValue<string>() != "hybrid_video",
            "the report's verdict is tool_limitation and it is not a hybrid_video plan");
        Check(report["loop"]?["candidates"] is JsonArray { Count: 0 } && report["blockers"] is JsonArray { Count: 2 },
            "the report has no candidate and one blocker per unreadable asset");

        JsonArray files = report["tool_limitation"]!["files"]!.AsArray();
        Check(files[0]!["layers"] is JsonArray { Count: 1 } layers && layers[0]!["id"]!.GetValue<int>() == 536 &&
            files[1]!["layers"] is JsonArray { Count: 0 },
            "only the layer that references the unreadable model file is named");

        string modelZh = report["blockers_localized"]![0]!["zh"]!.GetValue<string>();
        string modelEn = report["blockers_localized"]![0]!["en"]!.GetValue<string>();
        Check(modelZh.Contains("3D 模型文件", StringComparison.Ordinal) && modelZh.Contains("models/link_adult/link_adult.mdl", StringComparison.Ordinal) &&
            modelZh.Contains("animation play_mode", StringComparison.Ordinal) && modelZh.Contains("不是合法的 UTF-8", StringComparison.Ordinal) &&
            modelZh.Contains("380009", StringComparison.Ordinal) && modelZh.Contains("图层 536 \"link_adult\"", StringComparison.Ordinal) &&
            !modelZh.Contains("used by", StringComparison.Ordinal),
            "the chinese model blocker names the file, field, problem, offset and layer in chinese");
        Check(modelEn.Contains("3D model file", StringComparison.Ordinal) && modelEn.Contains("models/link_adult/link_adult.mdl", StringComparison.Ordinal) &&
            modelEn.Contains("animation play_mode", StringComparison.Ordinal) && modelEn.Contains("referenced by layer 536", StringComparison.Ordinal) &&
            !modelEn.Contains("图层", StringComparison.Ordinal) &&
            report["blockers"]![0]!.GetValue<string>().StartsWith("The tool cannot read this 3D model file yet:", StringComparison.Ordinal) &&
            Messages.Localize(report["blockers"]![0]!.GetValue<string>())["en"]!.GetValue<string>() == modelEn,
            "the english model blocker is the legacy blocker line and carries the same facts");
        Check(report["blockers_localized"]![1]!["zh"]!.GetValue<string>().Contains("纹理文件", StringComparison.Ordinal) &&
            report["blockers_localized"]![1]!["en"]!.GetValue<string>().Contains("mipmap", StringComparison.Ordinal),
            "the texture blocker is localized too");
        Check(report["summary"]!["zh"]!.GetValue<string>().Contains("工具局限", StringComparison.Ordinal) &&
            report["summary"]!["zh"]!.GetValue<string>().Contains("共 2 个素材文件", StringComparison.Ordinal) &&
            report["summary"]!["zh"]!.GetValue<string>().Contains("非对壁纸可生成性的判定", StringComparison.Ordinal) &&
            report["summary"]!["en"]!.GetValue<string>().StartsWith("Tool limitation, not analyzed:", StringComparison.Ordinal),
            "the one-line summary says it is a tool limitation, counts the files and is not a verdict on the wallpaper");

        JsonObject generic = AnalysisToolLimitation.BuildReport(unknownReason, null, "s", "h", output, "m");
        Check(generic["blockers_localized"]![0]!["zh"]!.GetValue<string>().Contains("invalid MDLA end_offset", StringComparison.Ordinal),
            "a model failure without a known field quotes the renderer's words");

        try { HybridPlanFormat.Validate(report); Check(false, "a tool limitation report is rejected as a bake plan"); }
        catch (InvalidDataException) { Check(true, "a tool limitation report is rejected as a bake plan"); }

        var exception = new AnalysisToolLimitationException(report, failure);
        Check(ReferenceEquals(exception.Report, report) && exception.Message == report["summary"]!["en"]!.GetValue<string>() &&
            exception.InnerException == failure,
            "the limitation exception carries the report and keeps the renderer failure as its cause");
        int[] existingExitCodes = [0, 1, 3, 130];
        Check(!existingExitCodes.Contains(AnalysisToolLimitation.ExitCode),"the tool limitation exit code is distinct from success, not applicable, preset and cancel");
    }
}
