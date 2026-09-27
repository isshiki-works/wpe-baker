using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>
/// 分析的分阶段计时（plan.json 顶层的 analysis_timing）：单次子分析按 A1–A15 分段，编排层按重查路径 (a)–(j) 记次数与墙钟秒。
/// 一次 <see cref="AnalysisOrchestrator.RunAsync"/> 一份，经 AsyncLocal 流到子分析；不在编排里（单测直接调子分析）时什么都不记。只计时，不改任何判定。
/// </summary>
internal sealed class AnalysisTiming
{
    /// <summary>单次子分析的阶段（编号与 perf-review 的阶段表一致）。</summary>
    internal static readonly string[] Stages =
    [
        "A1_open_source", "A2_source_hash", "A3_read_scene", "A4_resolution_graph", "A5_runtime_observation",
        "A6_liveness_projection", "A7_allocation", "A8_compose_verdict", "A9_loop_analysis", "A10_effect_prefix",
        "A11_enumerate_devices", "A12_compose_routes", "A13_reread_scene", "A14_loop_allocation_fallback", "A15_conclude_write"
    ];

    /// <summary>触发整套重分析的路径。</summary>
    internal static readonly string[] Paths =
    [
        "a_preset_layout_fallback", "b_interaction_alternative", "c_retreat", "d_plain_groups_live", "e_interaction_off_trial",
        "g_static_live", "h_loop_allocation_replan", "i_interaction_cost_trial", "j_slow_closure"
    ];

    private static readonly AsyncLocal<AnalysisTiming?> current = new();
    private readonly Dictionary<string, (int Count, double Seconds)> entries = new(StringComparer.Ordinal);
    private readonly Stopwatch total = Stopwatch.StartNew();
    private int subAnalyses;

    /// <summary>从这里往下（含 await 之后）的子分析与路径都记进新的一份。</summary>
    internal static AnalysisTiming Begin() => current.Value = new();

    private void Add(string key, double seconds)
    {
        lock (entries)
        {
            var (count, sum) = entries.GetValueOrDefault(key);
            entries[key] = (count + 1, sum + seconds);
        }
    }

    /// <summary>一次子分析的分段计时：每段结束处 <see cref="Lap.Mark"/> 一次，记上一处打点到这里的墙钟。</summary>
    internal static Lap StartSubAnalysis()
    {
        AnalysisTiming? timing = current.Value;
        if (timing is not null) Interlocked.Increment(ref timing.subAnalyses);
        return new(timing);
    }

    internal sealed class Lap(AnalysisTiming? timing)
    {
        private long last = Stopwatch.GetTimestamp();

        internal void Mark(string stage)
        {
            long now = Stopwatch.GetTimestamp();
            timing?.Add(stage, Stopwatch.GetElapsedTime(last, now).TotalSeconds);
            last = now;
        }
    }

    /// <summary>用 using 包住一条重查路径触发的那段工作。</summary>
    internal static Scope Measure(string path) => new(current.Value, path);

    internal readonly struct Scope(AnalysisTiming? timing, string path) : IDisposable
    {
        private readonly long started = Stopwatch.GetTimestamp();
        public void Dispose() => timing?.Add(path, Stopwatch.GetElapsedTime(started).TotalSeconds);
    }

    internal JsonObject ToJson()
    {
        lock (entries)
        {
            JsonObject Table(string[] keys)
            {
                var table = new JsonObject();
                foreach (string key in keys)
                    table[key] = entries.TryGetValue(key, out var entry)
                        ? new JsonObject { ["count"] = entry.Count, ["seconds"] = Math.Round(entry.Seconds, 3) } : null;
                return table;
            }
            return new JsonObject
            {
                ["schema_version"] = 1,
                ["total_seconds"] = Math.Round(total.Elapsed.TotalSeconds, 3),
                ["sub_analyses"] = subAnalyses,
                ["stages"] = Table(Stages),
                ["paths"] = Table(Paths),
                ["basis"] = "每一项都是本进程测得的墙钟秒，count 是发生次数；没有发生过的项是 null。stages 是所有子分析（含 A14 递归的那些）逐段累加，" +
                    "A14 的秒数包含它递归的整次子分析；退回轮内并行的子分析各自计时后相加。所以 stages 各项相加会大于 total_seconds。" +
                    "有记忆（AnalysisMemo）时 A2 只在本次 analyze 第一次真算哈希，其余是取记忆，收尾核对在 analysis_timing 写出之后。" +
                    "paths 是各条重查路径触发的那段工作的墙钟，路径之间可以嵌套（退回里的档位回退同时计入 a 与 c），不能相加；c 是并行候选各自墙钟之和。"
            };
        }
    }
}
