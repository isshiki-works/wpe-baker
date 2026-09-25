using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>
/// 一案烘焙的工作目录：输出根（去掉结尾分隔符的绝对路径）、报告位置，以及结束后要删的中间目录登记表。
/// 清理只删登记表里的名字，不按"除了什么都删"推断，成品工程、bake.json、编码成品、接缝预览、硬解探测日志与
/// 合成比对结果都不在表里。
/// </summary>
internal sealed class WorkLayout(string outputDirectory)
{
    private const string CaptureSourceName = "capture-source";
    private const string ReferenceName = "reference";
    private const string StartSearchSuffix = ".start-search";

    /// <summary>输出根旁的同级目录：旧计划的分析刷新；合成探针与参照（合成被拒时保留，报告的 probe_paths 指向它们）。</summary>
    private static readonly (string Suffix, bool CompositionProbe)[] Siblings =
        [(".composition-probe", true), (".composition-reference", true), (".analysis-refresh", false)];

    /// <summary>输出根下：捕获副本；效果前缀路线的原作参照工程（合成被拒时保留）。</summary>
    private static readonly (string Name, bool CompositionProbe)[] RootDirectories =
        [(CaptureSourceName, false), (ReferenceName, true)];

    /// <summary>输出根下的工程副本（成品、参照、捕获副本）：都是原作解包，里面可能恰好有同名文件夹，不按组目录清理。</summary>
    private static readonly string[] ProjectCopies = ["project", ReferenceName, CaptureSourceName];

    /// <summary>
    /// 每个组 / 前缀目录下：主渲染、覆盖度预通道、前缀的捕获副本；另有重渲前挪开的 master.*
    /// （gpu-unavailable、hardware-failed、coverage-miss、{codec}-qp{qp}、{codec}-failed），按前缀一并删。
    /// </summary>
    private static readonly string[] ChildDirectories = ["master", "capture-bounds", CaptureSourceName];

    internal string Output { get; } = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputDirectory));
    internal string Report => Path.Combine(Output, "bake.json");
    internal string CaptureSource => Path.Combine(Output, CaptureSourceName);
    internal string AnalysisRefresh => Output + ".analysis-refresh";

    /// <summary>合成被拒（含脚本报错门）时探针、参照要留给报告指路。</summary>
    internal static bool KeepsCompositionProbe(JsonObject? report) =>
        report?["probe_paths"] is JsonObject ||
        report?["status"]?.GetValue<string>() is "candidate_rejected_composition" or CandidateScriptErrorGate.RejectedBakeStatus;

    /// <summary>按登记表删中间目录；删不掉的记进返回的错误表（null = 全部删掉或本来就没有）。</summary>
    internal JsonArray? RemoveIntermediates(bool keepCompositionProbe)
    {
        JsonArray? errors = null;
        void Remove(string directory)
        {
            try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                (errors ??= []).Add(new JsonObject { ["path"] = directory, ["error"] = error.Message });
            }
        }
        foreach (var (suffix, probe) in Siblings)
            if (!(probe && keepCompositionProbe)) Remove(Output + suffix);
        if (!Directory.Exists(Output)) return errors;
        foreach (var (name, probe) in RootDirectories)
            if (!(probe && keepCompositionProbe)) Remove(Path.Combine(Output, name));
        string[] children;
        try { children = Directory.GetDirectories(Output); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return errors; }
        foreach (string child in children)
        {
            string name = Path.GetFileName(child);
            if (ProjectCopies.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            if (name.EndsWith(StartSearchSuffix, StringComparison.OrdinalIgnoreCase)) { Remove(child); continue; }
            foreach (string intermediate in ChildDirectories) Remove(Path.Combine(child, intermediate));
            try { foreach (string moved in Directory.GetDirectories(child, "master.*")) Remove(moved); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        return errors;
    }
}
