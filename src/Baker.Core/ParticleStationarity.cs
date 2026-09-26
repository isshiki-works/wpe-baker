using System.Globalization;
using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>
/// 粒子系统"平稳随机、可淡化替换"判据（design-particle-crossfade.md §2.2 的 C1–C9）与预热时长（§2.3），
/// 按本项目原生渲染器（engine ce30024）的粒子语义加严：湍流共享场、封顶替换、instanceoverride 的时间缩放。
/// <para>
/// 全部条件满足时，粒子层是率恒定、标记 i.i.d.、无外部输入的 M/G/∞ 型过程：预热之后任一时刻的画面分布不随时间变，
/// 相隔超过寿命上界的两帧互不相关，接缝处可以交叉淡化替换成统计等价的另一段。任一条不满足，这层就不适用淡化替换，
/// 照旧留实时（不是阻断整案）。
/// </para>
/// <para>
/// 判据只从粒子定义、材质、场景对象与运行时依赖确定性读出，不看画面。说不清的一律判不满足：引擎缺省值未核的字段
/// （寿命、maxcount）、位含义未核的控制点 flags、嵌套的子系统。
/// 每条不满足都写进 failed_conditions，写明是哪一条、哪个节点、什么值。
/// </para>
/// <para>
/// 渲染器语义（ParticleParser.cpp / ParticleRuntime.cpp / ParticleEmitter.cpp / SceneParticleObjectParser.cpp）：
/// instanceoverride.rate 是整个子系统的时间缩放（Advance(frame_time × rate)），count 乘在发射率上，lifetime 乘在寿命上；
/// turbulentvelocityrandom 持有一个跨粒子共享、沿 CurlNoise 流线随出生次数推进的采样点；turbulence 算子是随子系统时间
/// 沿 x 平移的共享确定性场；槽位（maxcount）满时发射器变成"死一个补一个"。
/// </para>
/// <para>
/// 槽位常满且寿命确定的层不是平稳过程而是周期平稳过程：给了输出帧时钟时按 <see cref="ParticleCappedReplacement"/> 推出整数帧的
/// 替换周期并锁定（Result.Lock），循环长度必须是它的整数倍；推不准时照旧判不满足。
/// </para>
/// </summary>
internal static class ParticleStationarity
{
    // 核过的节点种类、渲染器缺省值与数值判据在 Domain 的 ParticleCriteria；这里只做逐节点读 JSON 与记录。

    /// <summary>
    /// 一条不满足：condition 是 C1–C9（或 definition / warmup），code 是稳定代号，node 指出哪个节点，value 是原始值。
    /// Cannot = 已证明循环上限内不会重复（不是"说不清"），这一层的结论就是"不能"。
    /// </summary>
    internal sealed record Failure(string Condition, string Code, string Node, string? Value, bool Cannot = false);

    /// <summary>
    /// 输出帧时钟：bake 的离线步长就是 fps_den / fps_num（渲染器强制），封顶替换的周期按它逐帧推。
    /// <paramref name="LoopCeilingSeconds"/> 是本次分析实际用的循环时长上限（= --loop-max-seconds，含内嵌视频收紧）：
    /// 锁定周期超过它就算不出可用循环，维持拒绝。不给时按 --loop-max-seconds 的默认值。
    /// </summary>
    internal readonly record struct FrameClock(uint FpsNumerator, uint FpsDenominator,
        double LoopCeilingSeconds = CommonLoopSolver.DefaultLoopLengthMaximumSeconds);

    /// <summary>
    /// 封顶 + 确定寿命的粒子层按周期锁定：计数状态从 CycleStartFrame 起严格以 PeriodFrames 帧为周期，画面是按该周期的统计平稳过程。
    /// ComponentId 是交给循环求解器的锁定分量名；Evidence 是推导快照；ContinuousWarmupSeconds 是不锁定时的连续公式预热（降级时恢复它）。
    /// </summary>
    internal sealed record CyclostationaryLock(string ComponentId, ulong PeriodFrames, ulong CycleStartFrame, uint FpsNumerator,
        uint FpsDenominator, JsonObject Evidence, double? ContinuousWarmupSeconds)
    {
        /// <summary>这一分量的最小周期（帧，PeriodFrames 的约数）；同层几把锁合并时取它们的最小公倍数。</summary>
        internal ulong MinimalPeriodFrames { get; init; } = PeriodFrames;

        /// <summary>周期的精确秒数 = PeriodFrames × fps_den / fps_num。</summary>
        internal CommonLoopRational PeriodSeconds => new(checked((long)PeriodFrames * FpsDenominator), FpsNumerator);

        internal JsonObject ToJson() => new()
        {
            ["component"] = ComponentId,
            ["period_frames"] = PeriodFrames,
            ["period_seconds"] = PeriodSeconds.ToSeconds(),
            ["fps_num"] = FpsNumerator,
            ["fps_den"] = FpsDenominator,
            ["cycle_start_frame"] = CycleStartFrame,
            ["evidence"] = Evidence.DeepClone(),
            ["basis"] = "maxcount-capped deterministic lifetime: the renderer's per-frame count dynamics (float lifetime decrement, " +
                "emitter timer clamp) replayed from source values; the count state at frame k equals the state at k + period_frames " +
                "for every k >= cycle_start_frame, so the loop length is locked to a multiple of period_frames and both sides of the " +
                "seam sit in the same phase"
        };
    }

    /// <summary>
    /// 判定结果。WarmupSeconds / LifetimeMaxSeconds / EmitIntervalSeconds 都是真实秒（已除以 instanceoverride.rate 的时间缩放）。
    /// Capped 表示槽位常满（发射被 maxcount 封顶），WarmupGenerations 是封顶下随机寿命把替换时刻打散所需的代数（不封顶为 1）。
    /// Lock 非空表示这层是按周期锁定的周期平稳过程（只在 Stationary 时出现），预热换成进入周期态的帧数。
    /// </summary>
    internal sealed record Result(bool Stationary, IReadOnlyList<Failure> Failures, double? WarmupSeconds, double? LifetimeMaxSeconds,
        double? EmitIntervalSeconds = null, bool Capped = false, int WarmupGenerations = 1, CyclostationaryLock? Lock = null)
    {
        /// <summary>写进 plan.loop.unresolved[].particle_stationarity 的结构化字段，下游只读这里，不匹配文案。</summary>
        internal JsonObject ToJson()
        {
            var json = new JsonObject
            {
                ["stationary"] = Stationary,
                ["failed_conditions"] = new JsonArray(Failures.Select(failure =>
                {
                    var entry = new JsonObject { ["condition"] = failure.Condition, ["code"] = failure.Code, ["node"] = failure.Node, ["value"] = failure.Value };
                    if (failure.Cannot) entry["cannot"] = true;
                    return (JsonNode)entry;
                }).ToArray()),
                ["warmup_seconds"] = WarmupSeconds,
                ["lifetime_max_seconds"] = LifetimeMaxSeconds,
                // 只记录不裁决：发射节拍（真实秒）。节拍是确定性的，1/rate 远大于淡化窗口时粒子年龄分布是周期 1/rate 的梳状，见报告。
                ["emit_interval_seconds"] = EmitIntervalSeconds,
                ["capped"] = Capped,
                ["warmup_generations"] = WarmupGenerations,
                ["basis"] = "design-particle-crossfade §2.2 C1–C9 + fix/particle-turbulence (shared turbulence field, capped replacement, " +
                    "instanceoverride.rate as time scale); warmup = starttime + (lifetime_max × lifetime override × generations + " +
                    "max(maxperiodicdelay + maxperiodicduration)) / rate override; a capped deterministic lifetime is locked to its " +
                    "replacement period and warms up to its cycle start frame"
            };
            if (Lock is not null) json["cyclostationary_lock"] = Lock.ToJson();
            // 任一条已证明上限内不会重复，这一层就是"不能"（ResidualMasking 据此收敛），其余条件说不说得清都不改变结论。
            if (Failures.Any(failure => failure.Cannot))
            {
                json["loop_convergence"] = "cannot";
                json["reason_key"] = ResidualMasking.NeverRepeatsReasonKey;
            }
            return json;
        }

        /// <summary>给用户读的一行代号清单，例如 "C4 controlpoint_follows_cursor, C5 child_systems_not_recursed"。</summary>
        internal string FailureSummary() => string.Join(", ", Failures.Select(failure => failure.Condition + " " + failure.Code).Distinct(StringComparer.Ordinal));

        /// <summary>
        /// 锁定周期与其余分量在循环上限内没有公共循环：退回拒绝（这层留实时），预热恢复连续公式。<paramref name="evidence"/> 记求解器的结论。
        /// </summary>
        internal Result WithoutLock(string code, JsonObject evidence)
        {
            if (Lock is null) throw new InvalidOperationException("只有锁定周期的粒子层才能退回拒绝。");
            JsonObject snapshot = Lock.Evidence.DeepClone().AsObject();
            snapshot["period_frames"] = Lock.PeriodFrames;
            foreach (var (key, value) in evidence) snapshot[key] = value?.DeepClone();
            return this with
            {
                Stationary = false, Failures = [.. Failures, new Failure("C2", code, "maxcount", snapshot.ToJsonString())],
                WarmupSeconds = Lock.ContinuousWarmupSeconds, Lock = null
            };
        }
    }

    /// <summary>
    /// 判定一个粒子对象。<paramref name="objects"/> 用来找祖先链（脚本或关键帧移动祖先等于移动发射器）；
    /// <paramref name="runtime"/> 是运行时观测，读 runtime_dependencies；<paramref name="readResource"/> 读粒子定义与材质，读不到返回 null。
    /// <paramref name="clock"/> 是输出帧时钟：给出时，封顶 + 确定寿命的层按渲染器离散推进推出替换周期并锁定；不给时这类层照旧拒绝。
    /// <paramref name="childType"/> 只供 <see cref="CheckEventChild"/> 递归用（子系统的 type）。
    /// </summary>
    internal static Result Evaluate(JsonObject owner, IReadOnlyDictionary<int, JsonObject> objects, JsonObject? runtime,
        Func<string, JsonObject?> readResource, FrameClock? clock = null, string? childType = null)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(readResource);
        var failures = new List<Failure>();
        void Fail(string condition, string code, string node, JsonNode? value = null) =>
            failures.Add(new(condition, code, node, value?.ToJsonString()));
        void Cannot(string condition, string code, string node, JsonNode? value) =>
            failures.Add(new(condition, code, node, value?.ToJsonString(), Cannot: true));

        JsonObject? definition = owner["particle"] switch
        {
            JsonObject inline => inline,
            JsonValue path when path.TryGetValue(out string? resource) && !string.IsNullOrWhiteSpace(resource) => readResource(resource),
            _ => null
        };
        if (definition is null)
        {
            Fail("definition", "particle_definition_unavailable", "particle", owner["particle"]);
            return new(false, failures, null, null);
        }
        JsonObject overrides = owner["instanceoverride"] as JsonObject ?? [];
        // eventspawn / eventdeath 每个事件一个新实例，发射计时与槽位从实例创建起算：爆发、零发射率、有限时长、间歇、实例内封顶都只相对实例。
        bool perInstance = childType is "eventspawn" or "eventdeath";

        // ---- 实例覆盖倍率：rate 是子系统的时间缩放，count 乘在发射率上，lifetime 乘在寿命上（渲染器语义，见类注释）----
        double rateScale = 1, countScale = 1, lifetimeScale = 1;
        if (overrides["rate"] is JsonNode rateNode && !TryNonNegative(rateNode, out rateScale))
        { Fail("warmup", "rate_override_unreadable", "instanceoverride.rate", rateNode); rateScale = 1; }
        if (rateScale == 0)
        {
            // 时间缩放为 0 的子系统根本不推进：既不是平稳随机，也不是静态证明的对象。
            Fail("C1", "rate_override_zero", "instanceoverride.rate", overrides["rate"]);
            rateScale = 1;
        }
        if (overrides["count"] is JsonNode countNode && !TryNonNegative(countNode, out countScale))
        { Fail("warmup", "count_override_unreadable", "instanceoverride.count", countNode); countScale = 1; }
        if (overrides["lifetime"] is JsonNode scaleNode && !TryNonNegative(scaleNode, out lifetimeScale))
        { Fail("warmup", "lifetime_override_unreadable", "instanceoverride.lifetime", scaleNode); lifetimeScale = 1; }

        // ---- C1 发射器：率恒定、无爆发、无音频、不限时；间歇发射必须带随机 ----
        double periodicWarmup = 0;
        // 全部发射器合计的发射率，按子系统时间计（每系统秒），已含 count 倍率。
        double emissionRate = 0;
        // 各发射器里最短的发射间隔（子系统秒）：短于寿命下界时系统在第一次持续发射之后不会再空，爆发只在开场发一次。
        double shortestInterval = double.PositiveInfinity;
        JsonObject[] emitters = Entries(definition["emitter"]);
        // 渲染器 ResolveEmitCount：instantaneous 只在系统里一个粒子都没有时发一批——开场一次；发射率为 0 时每次死光再发一批（更新过程）。
        bool burst = false;
        if (emitters.Length == 0) Fail("C1", "emitter_missing", "emitter", definition["emitter"]);
        for (int index = 0; index < emitters.Length; ++index)
        {
            JsonObject emitter = emitters[index];
            string node = $"emitter[{index}]";
            if (!ParticleCriteria.Emitters.Contains(Name(emitter))) Fail("C1", "emitter_kind", node, emitter["name"]);
            bool bursts = !perInstance && emitter["instantaneous"] is JsonNode instant && !IsZero(instant);
            burst |= bursts;
            if (!TryScalar(emitter["rate"], ParticleCriteria.DefaultEmitterRate, out double rate) || rate < 0 || (rate == 0 && !perInstance && !bursts))
                Fail("C1", "emitter_rate_not_constant_positive", node + ".rate", emitter["rate"]);
            else
            {
                emissionRate += rate * countScale;
                if (rate * countScale > 0) shortestInterval = Math.Min(shortestInterval, 1 / (rate * countScale));
            }
            if (!perInstance && emitter["duration"] is JsonNode duration && !IsZero(duration)) Fail("C1", "emitter_finite_duration", node + ".duration", duration);
            if (ParticleInputAnalysis.AudioDriven(emitter)) Fail("C1", "emitter_audio_input", node + ".audioprocessingmode", emitter["audioprocessingmode"]);
            if (!perInstance && ParticleCriteria.PeriodicKeys.Any(emitter.ContainsKey))
            {
                var values = new double[ParticleCriteria.PeriodicKeys.Length];
                bool readable = true;
                for (int key = 0; key < ParticleCriteria.PeriodicKeys.Length; ++key)
                    readable &= TryNonNegative(emitter[ParticleCriteria.PeriodicKeys[key]], out values[key]);
                // 四个字段缺一个就要靠未核的缺省值，随机与否、间歇上界都说不清。
                if (!readable) Fail("C1", "emitter_periodic_incomplete", node + ".periodic", PeriodicSnapshot(emitter));
                else
                {
                    // 延迟与持续时长都是确定值时，开关本身是一个解析周期（T = delay + duration），不属于随机分量，这里不放行。
                    if (values[0] == values[1] && values[2] == values[3])
                        Fail("C1", "emitter_periodic_deterministic", node + ".periodic", PeriodicSnapshot(emitter));
                    periodicWarmup = Math.Max(periodicWarmup, values[1] + values[3]);
                }
            }
        }

        // ---- C2 初始化器：集合受限，寿命上界必须显式给出；湍流初始化器的噪声不得进入出生速度 ----
        double? lifetimeMax = null, lifetimeMin = null;
        bool lifetimeSeen = false;
        JsonObject[] initializers = Entries(definition["initializer"]);
        for (int index = 0; index < initializers.Length; ++index)
        {
            JsonObject initializer = initializers[index];
            string node = $"initializer[{index}]";
            string name = Name(initializer);
            if (!ParticleCriteria.Initializers.Contains(name)) Fail("C2", "initializer_kind", node, initializer["name"]);
            if (ParticleInputAnalysis.AudioDriven(initializer)) Fail("C2", "initializer_audio_input", node + ".audioprocessingmode", initializer["audioprocessingmode"]);
            if (name == "turbulentvelocityrandom") CheckTurbulentVelocity(initializer, node, shortestInterval > 10, Fail, Cannot);
            if (name != "lifetimerandom") continue;
            lifetimeSeen = true;
            // 缺 min 或 max 时引擎用缺省值，缺省值没有核过，寿命上界就说不清。
            if (initializer["min"] is null || initializer["max"] is null)
            {
                Fail("C2", "lifetime_default_unverified", node);
                continue;
            }
            if (!TryConstant(initializer["min"], out double[] low) || !TryConstant(initializer["max"], out double[] high) ||
                low.Concat(high).Any(value => value < 0))
            {
                Fail("C2", "lifetime_unbounded", node, initializer["max"]);
                continue;
            }
            lifetimeMax = Math.Max(lifetimeMax ?? 0, low.Concat(high).Max());
            lifetimeMin = Math.Min(lifetimeMin ?? double.PositiveInfinity, low.Concat(high).Min());
        }
        if (!lifetimeSeen) Fail("C2", "lifetime_default_unverified", "initializer");

        // ---- C2 封顶：槽位（maxcount）常满时发射变成"死一个补一个"的替换过程 ----
        // 渲染器 ParticleEmitter.cpp：槽位满时发射计时器被夹到一个发射间隔，槽位一空出同一帧就补。寿命是确定值时每个槽位
        // 按同一节拍整批替换，计数状态严格周期、画面是按该周期的统计平稳过程：给了帧时钟就按渲染器离散推进推出周期（整数帧）
        // 并锁定，交给循环求解器；推不准（参数读不成渲染器的值、周期超上限、状态不回归）或没有帧时钟时照旧拒绝。
        // 寿命随机时替换时刻逐代打散（第 k 代的展宽是 k × (L_max − L_min)），要经过 ⌈L_max / (L_max − L_min)⌉ 代才混合，预热按代数计。
        bool capped = false;
        int generations = 1;
        CyclostationaryLock? cycle = null;
        if (perInstance) { } // 槽位只在实例内部，从实例创建起算，不影响平稳性
        else if (definition["maxcount"] is null) Fail("C2", "maxcount_default_unverified", "maxcount");
        else if (!TryNonNegative(definition["maxcount"], out double maxCount)) Fail("C2", "maxcount_unreadable", "maxcount", definition["maxcount"]);
        else if (lifetimeMax is double authored && lifetimeMin is double authoredMin && emissionRate > 0)
        {
            double occupancy = emissionRate * authored * lifetimeScale;
            capped = occupancy >= maxCount;
            if (capped && authoredMin == authored)
            {
                var snapshot = new JsonObject
                {
                    ["period_seconds"] = ParticleCriteria.Round(authored * lifetimeScale / rateScale),
                    ["duty"] = ParticleCriteria.Round(Math.Min(1, maxCount / occupancy)),
                    ["maxcount"] = maxCount, ["occupancy"] = ParticleCriteria.Round(occupancy)
                };
                if (clock is not FrameClock frame) Fail("C2", "lifetime_capped_deterministic", "maxcount", snapshot);
                else
                {
                    var outcome = DeriveCappedReplacement(definition, overrides, emitters, initializers, frame);
                    // 连续公式的周期只是参照；锁定用的是离散推进推出的整数帧。
                    snapshot["period_seconds_continuous"] = snapshot["period_seconds"]!.DeepClone();
                    snapshot.Remove("period_seconds");
                    foreach (var (key, value) in outcome.Evidence) snapshot[key] = value?.DeepClone();
                    if (outcome.PeriodFrames is ulong periodFrames && outcome.CycleStartFrame is ulong cycleStart)
                        cycle = new(Component(owner, periodFrames), periodFrames, cycleStart, frame.FpsNumerator, frame.FpsDenominator, snapshot, null)
                            { MinimalPeriodFrames = outcome.MinimalPeriodFrames ?? periodFrames };
                    else Fail("C2", outcome.FailureCode!, "maxcount", snapshot);
                }
            }
            else if (capped) generations = ParticleCriteria.WarmupGenerations(authored, authoredMin);
        }
        // 爆发（渲染器 ResolveEmitCount）：系统一个粒子都没有时发一批。最短发射间隔短于寿命下界时，系统在第一次持续发射后不再空，
        // 爆发只在开场一次、预热（≥ 寿命上界）内死光；封顶 + 确定寿命的推导已把爆发算进计数动力学。否则每次死光都再发一批：
        // 寿命随机时发射时刻逐代打散（代数同封顶）；寿命确定时计数状态是确定性的，按同一套离散推进推出周期并锁定。
        // eventfollow 子系统的实例在父粒子出生时建立、只在跟着活的父粒子时发射，爆发是父粒子链上的年龄函数，与父层一同平稳。
        bool refires = burst && !capped && !(shortestInterval < lifetimeMin * lifetimeScale);
        if (refires && childType != "eventfollow" && lifetimeMin is double burstLifetime && burstLifetime == lifetimeMax)
        {
            if (clock is not FrameClock frame) Fail("C1", "emitter_burst", "emitter", definition["emitter"]);
            else
            {
                var outcome = DeriveCappedReplacement(definition, overrides, emitters, initializers, frame);
                if (outcome.PeriodFrames is ulong periodFrames && outcome.CycleStartFrame is ulong cycleStart)
                    cycle = new(Component(owner, periodFrames), periodFrames, cycleStart, frame.FpsNumerator, frame.FpsDenominator, outcome.Evidence, null)
                        { MinimalPeriodFrames = outcome.MinimalPeriodFrames ?? periodFrames };
                else Fail("C1", outcome.FailureCode!, "emitter", outcome.Evidence);
            }
        }
        else if (refires && lifetimeMax is double refireMax && lifetimeMin is double refireMin && refireMin < refireMax)
            generations = Math.Max(generations, ParticleCriteria.WarmupGenerations(refireMax, refireMin));

        // ---- C3 算子：年龄函数或静止的确定性场；turbulence 的场不得随时间推进 ----
        JsonObject[] operators = Entries(definition["operator"]);
        var fields = new List<(string Node, JsonObject Field)>();
        for (int index = 0; index < operators.Length; ++index)
        {
            JsonObject item = operators[index];
            string node = $"operator[{index}]";
            string name = Name(item);
            if (ParticleInputAnalysis.AudioDriven(item)) Fail("C3", "operator_audio_input", node + ".audioprocessingmode", item["audioprocessingmode"]);
            // oscillate*（渲染器 ParticleParser.cpp FrequencyValue）的时间是粒子年龄 LifetimePassed = 初始寿命 − 剩余寿命，频率、幅度、
            // 相位在粒子第一次更新时各抽一次（槽位复用时属性重置）：摆动只是年龄与每粒子标记的函数，频率与相位退化也不跟全局时钟同步。
            if (ParticleCriteria.OscillateOperators.Contains(name)) continue;
            if (!ParticleCriteria.Operators.Contains(name)) Fail("C3", "operator_kind", node, item["name"]);
            if (name == "turbulence" && CheckTurbulenceOperator(item, node, Fail) is JsonObject field) fields.Add((node, field));
        }

        // ---- C4 控制点：flags 必须为 0 或缺省 ----
        JsonObject[] controlPoints = Entries(definition["controlpoint"]);
        for (int index = 0; index < controlPoints.Length; ++index)
        {
            JsonNode? flags = controlPoints[index]["flags"];
            if (flags is null || IsZero(flags)) continue;
            // flags 第 1 位实测是跟随鼠标（外部输入）；2、16 等其它位的含义没有核过，同样判不满足。
            bool cursor = TryConstant(flags, out double[] bits) && bits.Length == 1 && bits[0] == Math.Floor(bits[0]) &&
                ((long)bits[0] & 1) != 0;
            Fail("C4", cursor ? "controlpoint_follows_cursor" : "controlpoint_flags_unverified", $"controlpoint[{index}].flags", flags);
        }

        // ---- C5 子系统：事件子系统逐个递归判定（CheckEventChild）；非数组与嵌套的没有核过 ----
        double childTail = 0;
        var childLocks = new List<CyclostationaryLock>();
        // 按父发射率给同时在世的子实例一个上界：活着的父粒子（出生于最近 L_max 内）+ 最近 tail 内释放的（父粒子出生于最近 tail + L_max 内）；
        // 每个发射器按计时器等间隔发射，任意 w 真实秒内至多 ⌊w × 率⌋ + 1 个。有爆发时不给（爆发数另算）。
        double Births(double window) => Math.Floor(window * emissionRate * rateScale) + emitters.Length;
        double? ParentInstances(double tail) => burst || emitters.Length == 0 || lifetimeMax is not double parentMax ? null
            : Births(parentMax * lifetimeScale / rateScale) + Births(tail + parentMax * lifetimeScale / rateScale);
        if (definition["children"] is JsonNode children && !(children is JsonArray { Count: 0 }))
        {
            if (childType is not null || children is not JsonArray list || list.Any(entry => entry is not JsonObject))
                Fail("C5", "child_systems_not_recursed", "children", children);
            else
                for (int index = 0; index < list.Count; ++index)
                    childTail = Math.Max(childTail, CheckEventChild(list[index]!.AsObject(), $"children[{index}]", failures, definition,
                        overrides, lifetimeMin * lifetimeScale / rateScale, objects, runtime, readResource, clock, childLocks, ParentInstances));
        }

        // ---- C6 材质：genericparticle 且不读帧缓冲 ----
        CheckMaterial(definition, readResource, Fail);

        // ---- C7 脚本：对象及祖先不能被脚本逐帧驱动或写入 ----
        int[] chain = Chain(owner, objects);
        if (runtime?["runtime_dependencies"] is not JsonArray dependencies)
            Fail("C7", "script_evidence_unavailable", "runtime_dependencies");
        else
            foreach (JsonObject dependency in dependencies.OfType<JsonObject>())
            {
                bool initialization = dependency["initialization"] is JsonValue flag && flag.TryGetValue(out bool once) && once;
                string operation = Text(dependency["operation"]);
                int? scriptOwner = SceneGraph.Int(dependency["owner"]);
                int? target = SceneGraph.Int(dependency["target"]);
                string summary = $"{operation}:{Text(dependency["property"])}";
                // 挂在本对象或祖先上的脚本：逐帧运行，或读外部输入（音频、鼠标等，初始化时注册也算）。
                if (scriptOwner is int host && chain.Contains(host) && (!initialization || operation == "input"))
                    Cannot("C7", "script_drives_object", $"runtime_dependencies[owner={host}]", JsonValue.Create(summary));
                else if (target is int written && chain.Contains(written) && operation == "write" && !initialization)
                    Cannot("C7", "script_writes_object", $"runtime_dependencies[target={written}]", JsonValue.Create(summary));
            }

        // ---- C8 覆盖与对象属性：常数、静态属性绑定，或周期已由渲染器动画轨道证明的关键帧 ----
        // 作者脚本驱动的值（覆盖或属性）：分析没有脚本周期的推导（运行时观测只记录读时钟与外部输入），周期给不出，判不能。C7 同理。
        foreach ((string key, JsonNode? value) in overrides)
            if (key == "id" || IsConstantBinding(value)) continue;
            else if (value is JsonObject { } scripted && scripted["script"] is not null) Cannot("C8", "override_script_driven", "instanceoverride." + key, value);
            else Fail("C8", "override_not_constant", "instanceoverride." + key, value);
        var tracks = new List<(string Node, int Id, JsonObject Binding)>();
        foreach (int id in chain)
        {
            JsonObject item = objects.TryGetValue(id, out JsonObject? found) ? found : owner;
            foreach (string key in ParticleCriteria.AncestorMotionKeys)
                if (item[key] is not JsonObject binding || IsConstantBinding(binding)) continue;
                else if (binding["script"] is not null) Cannot("C8", "property_script_driven", $"object[{id}].{key}", binding);
                // 关键帧：发射器随动画轨道周期运动，粒子层是按轨道周期的周期平稳过程，锁到轨道周期（见下方锁定）。
                else if (binding["animation"] is JsonObject) tracks.Add(($"object[{id}].{key}", id, binding));
                else Fail("C8", "property_animated", $"object[{id}].{key}", binding);
        }

        // ---- C9 渲染器与精灵帧 ----
        JsonObject[] renderers = Entries(definition["renderer"]);
        if (renderers.Length == 0)
        {
            if (!definition.ContainsKey("renderer") || definition["renderer"] is JsonArray { Count: 0 })
                renderers = [new JsonObject { ["name"] = "sprite" }];
            else Fail("C9", "renderer_definition_invalid", "renderer", definition["renderer"]);
        }
        for (int index = 0; index < renderers.Length; ++index)
            if (!ParticleCriteria.Renderers.Contains(Name(renderers[index]))) Fail("C9", "renderer_kind", $"renderer[{index}]", renderers[index]["name"]);
        if (definition["animationmode"] is JsonNode mode && !(mode is JsonValue modeValue && modeValue.TryGetValue(out string? modeText) &&
            modeText is not null && ParticleCriteria.AnimationModes.Contains(modeText)))
            Fail("C9", "animation_mode_unverified", "animationmode", mode);

        // ---- 预热：starttime + (寿命上界 × 覆盖倍率 × 代数 + 间歇发射上界) / 时间缩放 ----
        // 寿命、间歇与替换代数都以子系统时间计，除以 instanceoverride.rate 才是 master 要跳过的真实秒数。
        double? warmup = null, lifetime = null;
        double start = 0;
        // 负的 starttime（实测有 -2）在引擎里怎么起算没有核过，读不出与负值都算说不清，预热时长就给不出上界。
        if (definition["starttime"] is JsonNode startNode && !TryNonNegative(startNode, out start))
            Fail("warmup", "starttime_unverified", "starttime", startNode);
        if (lifetimeMax is double authoredLifetime)
        {
            // 事件子系统的实例在父粒子死后还要走完子寿命：相关跨度与预热各加尾长（CheckEventChild）。
            lifetime = ParticleCriteria.Round(authoredLifetime * lifetimeScale / rateScale + childTail);
            warmup = ParticleCriteria.WarmupSeconds(start, authoredLifetime, lifetimeScale, generations, periodicWarmup, rateScale) + childTail;
        }
        // 同层的几把锁（封顶/爆发的计数周期、湍流场周期、关键帧轨道周期、static 子系统的锁）合成一把：周期取各自最小周期的最小公倍数，
        // 起点取最晚的。公倍数超过循环上限就是上限内不会同时回到同一相位，判"不能"并写出各分量。
        void Lock(CyclostationaryLock next, FrameClock frame)
        {
            if (cycle is null) { cycle = next; return; }
            ulong period = Lcm(cycle.MinimalPeriodFrames, next.MinimalPeriodFrames);
            var evidence = new JsonObject { ["locks"] = new JsonArray(cycle.ToJson(), next.ToJson()), ["lcm_frames"] = period };
            if (period > ParticleCriteria.MaximumPeriodFrames(frame.LoopCeilingSeconds, frame.FpsNumerator, frame.FpsDenominator))
                Cannot("C2", "particle_locks_exceed_ceiling", "cyclostationary_lock", evidence);
            else cycle = new(Component(owner, period), period, Math.Max(cycle.CycleStartFrame, next.CycleStartFrame), frame.FpsNumerator,
                frame.FpsDenominator, evidence, null);
        }
        foreach (CyclostationaryLock child in childLocks) Lock(child, clock!.Value);
        // C3 turbulence 共享场以 256 / (2 × scale × timescale) 系统秒为周期平移（÷ rate 覆盖是真实秒），粒子是按场周期的周期平稳过程：
        // 周期超过循环上限就是上限内不会重复，判"不能"并写出周期；否则循环长度锁到场周期的整数倍（与封顶锁定同一机制，起点在预热之后）。
        double ceiling = clock?.LoopCeilingSeconds ?? CommonLoopSolver.DefaultLoopLengthMaximumSeconds;
        foreach ((string node, JsonObject field) in fields)
        {
            double? period = field["field_period_system_seconds"]?.GetValue<double>() / rateScale;
            if (period is double real) field["field_period_seconds"] = ParticleCriteria.Round(real);
            if (period > ceiling) failures.Add(new("C3", "turbulence_shared_field", node, field.ToJsonString(), Cannot: true));
            else if (period is double locked && clock is FrameClock frame && warmup is double settled)
            {
                ulong frames = (ulong)Math.Round(locked * frame.FpsNumerator / frame.FpsDenominator);
                ulong settledFrame = (ulong)Math.Ceiling((settled - childTail) * frame.FpsNumerator / frame.FpsDenominator);
                Lock(new($"particle_field/{SceneGraph.Int(owner["id"])?.ToString(CultureInfo.InvariantCulture) ?? "?"}/{frames}", frames, settledFrame,
                    frame.FpsNumerator, frame.FpsDenominator, field, null), frame);
            }
            else failures.Add(new("C3", "turbulence_shared_field", node, field.ToJsonString()));
        }
        // C8 关键帧：轨道周期取渲染器给的有理时长（runtime_animation_periods 的 authored_track，循环、非事件驱动、高置信、速率 1），
        // 折成整数帧 = 时长 × fps 化成最简分数后的分子；同一对象几条轨道取最小公倍数。
        foreach ((string node, int id, JsonObject binding) in tracks)
            if (clock is FrameClock frame && warmup is double settled && TrackFrames(runtime, id, frame) is (ulong frames, JsonArray traces))
                Lock(new($"particle_track/{id}/{frames}", frames,
                    (ulong)Math.Ceiling((settled - childTail) * frame.FpsNumerator / frame.FpsDenominator), frame.FpsNumerator, frame.FpsDenominator,
                    new JsonObject { ["object"] = id, ["traces"] = traces }, null), frame);
            else Fail("C8", "property_animated", node, binding);
        double? emitInterval = emissionRate > 0 ? ParticleCriteria.Round(1 / (emissionRate * rateScale)) : null;
        // 锁定周期只在其余条件全部满足时成立；预热换成进入周期态的帧（starttime 预跑已在推导里），按微秒向上取，
        // 保证 ⌈预热秒 × fps⌉ 不少于起点帧。连续公式的值留着，退回拒绝时恢复。
        if (failures.Count == 0 && cycle is not null)
        {
            cycle = cycle with { ContinuousWarmupSeconds = warmup };
            warmup = ParticleCriteria.CycleWarmupSeconds(cycle.CycleStartFrame, cycle.FpsNumerator, cycle.FpsDenominator) + childTail;
        }
        else cycle = null;
        return new(failures.Count == 0, failures, warmup, lifetime, emitInterval, capped, generations, cycle);
    }

    /// <summary>
    /// 从源值按渲染器的类型转换组出封顶替换的推导输入：maxcount（u32，上限 20000）、发射率 × count 覆盖（float）、one_per_frame 位、
    /// lifetimerandom × lifetime 覆盖（float）、rate 覆盖（float）、starttime（float）；粒子顶层 flags 16 / 32 让 count / lifetime 覆盖失效。
    /// 任一值不是 JSON 数字（或 {user, value: 数字} 绑定），渲染器读到的值就说不清，判算不准。
    /// </summary>
    private static (ulong? PeriodFrames, ulong? CycleStartFrame, string? FailureCode, JsonObject Evidence, ulong? MinimalPeriodFrames) DeriveCappedReplacement(
        JsonObject definition, JsonObject overrides, JsonObject[] emitters, JsonObject[] initializers, FrameClock clock)
    {
        var evidence = new JsonObject();
        (ulong?, ulong?, string?, JsonObject, ulong?) Unverified(string node)
        {
            evidence["unverified_node"] = node;
            return (null, null, "lifetime_capped_parameters_unverified", evidence, null);
        }
        if (!TryAuthoredInteger(definition["maxcount"], 0, out long maxCount) || maxCount < 0 || maxCount > uint.MaxValue) return Unverified("maxcount");
        if (!TryAuthoredInteger(definition["flags"], 0, out long flags) || flags < 0 || flags > uint.MaxValue) return Unverified("flags");
        bool countOverrideDisabled = (flags & 16) != 0, lifetimeOverrideDisabled = (flags & 32) != 0;
        if (!TryAuthoredFloat(overrides["count"], 1f, out float countScale)) return Unverified("instanceoverride.count");
        if (!TryAuthoredFloat(overrides["lifetime"], 1f, out float lifetimeScale)) return Unverified("instanceoverride.lifetime");
        if (!TryAuthoredFloat(overrides["rate"], 1f, out float rateScale)) return Unverified("instanceoverride.rate");
        if (!TryAuthoredFloat(definition["starttime"], 0f, out float startTime)) return Unverified("starttime");
        if (countOverrideDisabled) countScale = 1f;
        if (lifetimeOverrideDisabled) lifetimeScale = 1f;

        var sources = new List<ParticleCappedReplacement.Emitter>();
        for (int index = 0; index < emitters.Length; ++index)
        {
            if (!TryAuthoredFloat(emitters[index]["rate"], ParticleCriteria.DefaultEmitterRate, out float rate)) return Unverified($"emitter[{index}].rate");
            if (!TryAuthoredInteger(emitters[index]["flags"], 0, out long emitterFlags) || emitterFlags < 0 || emitterFlags > uint.MaxValue)
                return Unverified($"emitter[{index}].flags");
            if (!TryAuthoredInteger(emitters[index]["instantaneous"], 0, out long burst) || burst < 0 || burst > uint.MaxValue)
                return Unverified($"emitter[{index}].instantaneous");
            // SceneParticleObjectParser.LoadEmitter：rate *= count 覆盖（float × float）；Emitter::FlagEnum::one_per_frame 是第 1 位（值 2）；
            // instantaneous 原样读成 u32，不乘覆盖。
            sources.Add(new(rate * countScale, (emitterFlags & 2) != 0, (uint)burst));
        }
        // 初始化器按顺序执行，后一个 lifetimerandom 覆盖前一个；min = max 时 lerp 精确落在 min 上（Utils.cppm: a + t × (b − a)）。
        JsonObject? lifetimeNode = initializers.LastOrDefault(node => Name(node) == "lifetimerandom");
        if (lifetimeNode is null || !TryAuthoredFloat(lifetimeNode["min"], 0f, out float authoredLifetime) ||
            !TryAuthoredFloat(lifetimeNode["max"], 0f, out float authoredMaximum) || authoredLifetime != authoredMaximum)
            return Unverified("initializer.lifetimerandom");
        // OverrideSpawnProgram：lifetimes[index] *= modifiers.Lifetime()（只在有 instanceoverride 时挂上；没有时覆盖值恒为 1）。
        float lifetime = authoredLifetime * lifetimeScale;
        evidence["maxcount_effective"] = Math.Min(maxCount, 20000);
        evidence["emit_rates_float"] = new JsonArray([.. sources.Select(source => (JsonNode)JsonValue.Create(source.Speed))]);
        ulong maximumFrames = ParticleCriteria.MaximumPeriodFrames(clock.LoopCeilingSeconds, clock.FpsNumerator, clock.FpsDenominator);
        ParticleCappedReplacement.Outcome outcome = ParticleCappedReplacement.Derive(new((uint)Math.Min(maxCount, 20000), sources, lifetime,
            rateScale, startTime, clock.FpsNumerator, clock.FpsDenominator), maximumFrames);
        // 推导取证按推到的步骤依次写入（键序、数值类型与旧版逐字节相同）。
        evidence["subsystem_step_seconds"] = outcome.SubsystemStepSeconds;
        evidence["authored_lifetime_float"] = outcome.AuthoredLifetime;
        if (outcome.MaximumPeriodFrames is ulong ceiling) evidence["maximum_period_frames"] = ceiling;
        if (outcome.ReplacementFrames is ulong replacement) evidence["period_frames"] = replacement;
        if (outcome.StartTimePrerunSteps is ulong prerun) evidence["starttime_prerun_steps"] = prerun;
        if (outcome.CycleStartFrame is ulong start) evidence["cycle_start_frame"] = (long)start;
        if (outcome.SimulatedSteps is long steps) evidence["simulated_steps"] = steps;
        return (outcome.PeriodFrames, outcome.CycleStartFrame, outcome.FailureCode, evidence, outcome.MinimalPeriodFrames);
    }

    private static string Component(JsonObject owner, ulong periodFrames) =>
        $"particle_cycle/{SceneGraph.Int(owner["id"])?.ToString(CultureInfo.InvariantCulture) ?? "?"}/{periodFrames}";

    private static ulong Gcd(ulong a, ulong b)
    {
        while (b != 0) (a, b) = (b, a % b);
        return a;
    }

    private static ulong Lcm(ulong a, ulong b) => checked(a / Gcd(a, b) * b);

    /// <summary>
    /// 对象 <paramref name="id"/> 的关键帧轨道周期折成的整数帧（几条轨道取最小公倍数）与所用的轨道记录；
    /// 没有轨道，或任一条不是循环、非事件驱动、高置信、速率 1、带有理时长，返回 null。
    /// </summary>
    private static (ulong Frames, JsonArray Traces)? TrackFrames(JsonObject? runtime, int id, FrameClock clock)
    {
        JsonObject[] traces = (runtime?["runtime_animation_periods"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(trace => SceneGraph.Int(trace["source_owner_layer_id"]) == id && Text(trace["mechanism"]) == "authored_track").ToArray();
        ulong frames = 1;
        foreach (JsonObject trace in traces)
        {
            if (trace["looping"] is not JsonValue looping || !looping.TryGetValue(out bool loops) || !loops ||
                trace["event_driven"] is JsonValue driven && driven.TryGetValue(out bool events) && events ||
                Text(trace["confidence"]) != "high" || !TryAuthoredNumber(trace["playback_rate"] ?? JsonValue.Create(1), out double rate) || rate != 1 ||
                !TryAuthoredInteger(trace["duration_numerator"], 0, out long numerator) || numerator <= 0 ||
                !TryAuthoredInteger(trace["duration_denominator"], 0, out long denominator) || denominator <= 0)
                return null;
            // 时长 n/d 秒 × fps_num/fps_den 化成最简分数 p/q：p 帧是轨道周期的整数倍里最短的整数帧。
            ulong top = checked((ulong)numerator * clock.FpsNumerator), bottom = checked((ulong)denominator * clock.FpsDenominator);
            frames = Lcm(frames, top / Gcd(top, bottom));
        }
        return traces.Length == 0 ? null : (frames, new JsonArray([.. traces.Select(trace => (JsonNode)trace.DeepClone())]));
    }

    /// <summary>JSON 数字或 {user, value: 数字} 绑定，按渲染器转成 float；缺省时取渲染器缺省值；字符串与向量说不清。</summary>
    private static bool TryAuthoredFloat(JsonNode? node, float fallback, out float value)
    {
        value = fallback;
        if (node is null) return true;
        if (!TryAuthoredNumber(node, out double number)) return false;
        value = (float)number;
        return float.IsFinite(value);
    }

    /// <summary>JSON 整数（或 {user, value: 整数} 绑定）；缺省时取 <paramref name="fallback"/>。</summary>
    private static bool TryAuthoredInteger(JsonNode? node, long fallback, out long value)
    {
        value = fallback;
        if (node is null) return true;
        if (!TryAuthoredNumber(node, out double number) || number != Math.Floor(number) || Math.Abs(number) > long.MaxValue / 2.0) return false;
        value = (long)number;
        return true;
    }

    private static bool TryAuthoredNumber(JsonNode node, out double number)
    {
        number = 0;
        if (node is JsonObject binding)
        {
            if (!IsConstantBinding(binding) || binding["value"] is not JsonNode inner) return false;
            node = inner;
        }
        if (node is not JsonValue value) return false;
        if (value.TryGetValue(out double real)) number = real;
        else if (value.TryGetValue(out long integer)) number = integer;
        else if (value.TryGetValue(out int small)) number = small;
        else if (value.TryGetValue(out float single)) number = single;
        else return false;
        return double.IsFinite(number);
    }

    /// <summary>
    /// 一个子系统（渲染器 ParticleObject.cpp / SceneParticleObjectParser.cpp / ParticleRuntime.cpp）。只核了事件类型：eventspawn / eventfollow
    /// 在父粒子出生、eventdeath 在父粒子死亡时取一个实例（按 probability 独立抽签）；实例的发射计时、槽位、starttime 预跑都从实例创建起算，
    /// 父粒子死后实例停发（InstanceCanEmit），余下粒子走完子寿命即回收。每个实例因此是父粒子标记、自身随机与事件后时间的函数，
    /// 寿命有界：父层平稳时整体仍平稳，相关跨度与预热各多一段尾长（返回值，真实秒；不满足时返回 0）。
    /// 前提是实例上限（children[].maxcount，缺省 20）永不触顶，否则哪些事件拿到实例取决于历史：每个父槽位相邻两次出生或死亡至少隔
    /// 父寿命下界，同时在世的实例 ≤ min(父 maxcount, 20000) × (2 + ⌊尾长 / 父寿命下界⌋)。子定义按同一套条件递归判，
    /// 只相对实例的几条（爆发、零发射率、有限时长、间歇、实例内封顶）不判。eventfollow 例外：父粒子死亡的同一帧槽位被补上时实例不释放、
    /// 接着跟新粒子（ProcessChildEvents），发射计时跨代延续，所以按常驻系统的全套条件判。static 子系统是另一个常驻系统，同样按全套条件判。
    /// </summary>
    private static double CheckEventChild(JsonObject child, string node, List<Failure> failures, JsonObject definition, JsonObject overrides,
        double? parentLifetimeMin, IReadOnlyDictionary<int, JsonObject> objects, JsonObject? runtime, Func<string, JsonObject?> readResource,
        FrameClock? clock, List<CyclostationaryLock> locks, Func<double, double?> parentInstances)
    {
        // 渲染器 ParseSpawnType：type 缺省或不认识的字样都是 static。
        string type = Text(child["type"]) is "eventspawn" or "eventfollow" or "eventdeath" ? Text(child["type"]) : "static";
        // static 子系统与父系统同时建立、共用子系统时钟：它的锁（封顶/爆发/场/轨道）按同一时钟并进父层。事件子系统的时钟从实例创建起算，不给帧时钟。
        Result result = Evaluate(new JsonObject { ["particle"] = child["name"]?.DeepClone(), ["instanceoverride"] = overrides.DeepClone() },
            objects, runtime, readResource, type == "static" ? clock : null, childType: type);
        if (result.Lock is not null) locks.Add(result.Lock);
        failures.AddRange(result.Failures.Select(failure => failure with { Node = node + "." + failure.Node }));
        // 子定义的预热（≥ 子寿命上界，eventfollow 的常驻实例还含 starttime 与间歇）当作尾长：相关跨度按它从宽计。
        if (!result.Stationary || result.WarmupSeconds is not double tail) return 0;
        // static 是挂在父节点上的另一个常驻系统，只有一个实例，按全套条件判过了；它的预热并进父系统，没有实例上限的问题。
        if (type == "static") return tail;
        double cap = 20;
        bool capReadable = child["maxcount"] is null || TryNonNegative(child["maxcount"], out cap);
        double? instances = TryNonNegative(definition["maxcount"], out double parentCount) && parentLifetimeMin is > 0
            ? Math.Min(parentCount, 20000) * (2 + Math.Floor(tail / parentLifetimeMin.Value)) : null;
        if (parentInstances(tail) is double byRate) instances = Math.Min(instances ?? double.PositiveInfinity, byRate);
        if (capReadable && instances <= cap) return tail;
        failures.Add(new("C5", "child_instance_cap_binds", node + ".maxcount",
            new JsonObject { ["cap"] = child["maxcount"]?.DeepClone(), ["instances_bound"] = instances }.ToJsonString()));
        return 0;
    }

    /// <summary>
    /// turbulentvelocityrandom（渲染器 TurbulentVelocityRandomProgram）：初始化器持有一个跨粒子共享的采样点 position（解析时在 [0,10]³ 随机取），
    /// 每次出生先抽本粒子的 speed 与 phase，发射间隔 &gt; 10 s 时 position.x += speed，再沿 CurlNoise((position + normal·phase)) 的投影方向
    /// 推进 ⌈(1/rate)/0.01⌉ 步 × 0.005 × timescale，用最后一步的方向定出生方向（绕 normal 转 atan2 × max(0, scale × 0.5) + offset）。
    /// 采样点不读时钟，只随出生推进，每一步都掺进本次出生独立抽取的 phase（间隔 &gt; 10 s 时还有 speed）：出生方向由独立同分布的随机量驱动，
    /// 分布不随时间变，按平稳随机走淡化替换，接缝掩盖由接缝门验证（主线程 9/26 裁定）。phase 与 speed 都是定值时，采样点是出生次数的确定函数
    /// ——一个随时间演化的共享风向，没有解析周期（CurlNoise 流线上的归一化步进），判不能。噪声不进速度（角度增益 0、速度区间 0）
    /// 或采样点不动（timescale = 0）时出生方向只由每粒子的 phase 决定，同样平稳。
    /// </summary>
    private static void CheckTurbulentVelocity(JsonObject initializer, string node, bool slowEmitters,
        Action<string, string, string, JsonNode?> fail, Action<string, string, string, JsonNode?> cannot)
    {
        if (!TryScalar(initializer["scale"], 1, out double scale) || !TryScalar(initializer["timescale"], 1, out double timescale) ||
            !TryScalar(initializer["speedmin"], 100, out double speedMin) || !TryScalar(initializer["speedmax"], 250, out double speedMax) ||
            !TryScalar(initializer["phasemin"], 0, out double phaseMin) || !TryScalar(initializer["phasemax"], 0.1, out double phaseMax))
        {
            fail("C2", "turbulent_velocity_unreadable", node, initializer);
            return;
        }
        bool inert = Math.Max(0, scale * 0.5) == 0 || (speedMin == 0 && speedMax == 0) || timescale == 0;
        bool driven = phaseMin != phaseMax || slowEmitters && speedMin != speedMax;
        if (inert || driven) return;
        cannot("C2", "turbulent_velocity_shared_field", node, new JsonObject
        {
            ["scale"] = scale, ["timescale"] = timescale, ["speedmin"] = speedMin, ["speedmax"] = speedMax, ["phasemin"] = phaseMin, ["phasemax"] = phaseMax
        });
    }

    /// <summary>
    /// turbulence 算子（渲染器 TurbulenceOperator）：力 = speed × normalize(CurlNoise((p + (phase + timescale × t) · e_x) × 2 × scale))，
    /// t 是子系统累计时间，phase 与 speed 在解析时抽一次、全系统共享。场随时间沿 x 平移，Perlin 排列表周期 256，
    /// 所以这是周期 256 / (2 × scale × timescale) 系统秒的共享确定性分量，不是平稳噪声场。timescale = 0 时场静止，
    /// 每个粒子的轨迹只是自身标记的确定性函数；速度区间为 0 或 mask 全 0 时算子不起作用（返回 null）。起作用时返回参数快照，
    /// 由调用方按场周期与循环上限裁定（锁定或不能）。
    /// </summary>
    private static JsonObject? CheckTurbulenceOperator(JsonObject item, string node, Action<string, string, string, JsonNode?> fail)
    {
        double[] mask = item["mask"] is null ? [1, 1, 0] : ParticleInputAnalysis.Numbers(item["mask"]);
        if (!TryScalar(item["timescale"], 20, out double timescale) || !TryScalar(item["scale"], 0.01, out double scale) ||
            !TryScalar(item["speedmin"], 500, out double speedMin) || !TryScalar(item["speedmax"], 1000, out double speedMax) || mask.Length == 0)
        {
            fail("C3", "turbulence_unreadable", node, item);
            return null;
        }
        bool inert = (speedMin == 0 && speedMax == 0) || mask.All(component => component == 0) || timescale == 0;
        if (inert) return null;
        double? period = ParticleCriteria.TurbulenceFieldPeriodSeconds(scale, timescale);
        return new JsonObject
        {
            ["timescale"] = timescale, ["scale"] = scale, ["speedmin"] = speedMin, ["speedmax"] = speedMax,
            ["field_period_system_seconds"] = period
        };
    }

    private static void CheckMaterial(JsonObject definition, Func<string, JsonObject?> readResource, Action<string, string, string, JsonNode?> fail)
    {
        JsonObject? material = definition["material"] is JsonValue path && path.TryGetValue(out string? resource) && !string.IsNullOrWhiteSpace(resource)
            ? readResource(resource) : null;
        JsonObject[] passes = Entries(material?["passes"]);
        if (passes.Length == 0)
        {
            fail("C6", "material_unreadable", "material", definition["material"]);
            return;
        }
        for (int index = 0; index < passes.Length; ++index)
        {
            JsonObject pass = passes[index];
            string node = $"material.passes[{index}]";
            string shader = Text(pass["shader"]);
            if (!shader.StartsWith("genericparticle", StringComparison.OrdinalIgnoreCase))
            {
                fail("C6", "shader_not_generic_particle", node + ".shader", pass["shader"]);
                continue;
            }
            // genericparticle 的 REFRACT 组合采样 _rt_FullFrameBuffer：画面取决于底下的实时层，不是粒子自身的随机过程。
            bool refract = shader.Contains("refract", StringComparison.OrdinalIgnoreCase) ||
                (pass["combos"] as JsonObject ?? []).Any(pair => string.Equals(pair.Key, "REFRACT", StringComparison.OrdinalIgnoreCase) &&
                    (pair.Value is null || !IsZero(pair.Value))) ||
                (pass["textures"] as JsonArray ?? []).Any(texture => Text(texture).StartsWith("_rt_", StringComparison.OrdinalIgnoreCase));
            if (refract) fail("C6", "shader_reads_framebuffer", node, pass["combos"] ?? pass["shader"]);
        }
    }

    /// <summary>对象自身加父链上的全部祖先（父链断在场景外或成环时停下）。</summary>
    private static int[] Chain(JsonObject owner, IReadOnlyDictionary<int, JsonObject> objects)
    {
        var chain = new List<int>();
        JsonObject? current = owner;
        while (current is not null && SceneGraph.Int(current["id"]) is int id && !chain.Contains(id))
        {
            chain.Add(id);
            current = SceneGraph.Int(current["parent"]) is int parent && objects.TryGetValue(parent, out JsonObject? next) ? next : null;
        }
        return [.. chain];
    }

    private static JsonObject[] Entries(JsonNode? node) => (node as JsonArray ?? []).OfType<JsonObject>().ToArray();

    private static string Name(JsonObject node) => Text(node["name"]);

    private static string Text(JsonNode? node) => node is JsonValue value && value.TryGetValue(out string? text) && text is not null ? text : "";

    private static JsonObject PeriodicSnapshot(JsonObject emitter) =>
        new(ParticleCriteria.PeriodicKeys.Select(key => KeyValuePair.Create(key, emitter[key]?.DeepClone())));

    /// <summary>
    /// 常数：数字、数字串或 "x y z" 向量串，或 {"user":…, "value":…} 静态属性绑定（壁纸属性在播放中不变）。
    /// 带 script / animation 的绑定不是常数。
    /// </summary>
    private static bool TryConstant(JsonNode? node, out double[] values)
    {
        values = [];
        if (node is JsonObject binding)
        {
            if (!IsConstantBinding(binding)) return false;
            node = binding["value"];
        }
        values = ParticleInputAnalysis.Numbers(node);
        return values.Length > 0 && values.All(double.IsFinite);
    }

    /// <summary>单个非负常数。</summary>
    private static bool TryNonNegative(JsonNode? node, out double value)
    {
        value = 0;
        if (!TryConstant(node, out double[] values) || values.Length != 1 || values[0] < 0) return false;
        value = values[0];
        return true;
    }

    /// <summary>单个标量：缺省时取渲染器的缺省值，写了就必须是单个有限常数。</summary>
    private static bool TryScalar(JsonNode? node, double fallback, out double value)
    {
        value = fallback;
        if (node is null) return true;
        if (!TryConstant(node, out double[] values) || values.Length != 1) return false;
        value = values[0];
        return true;
    }

    /// <summary>字面值，或只带 user / value 两个键的属性绑定。</summary>
    private static bool IsConstantBinding(JsonNode? node) => node switch
    {
        JsonValue => true,
        // {"script": null} 是编辑器留下的空脚本槽，不驱动任何东西。
        JsonObject binding => binding.All(pair => pair.Key is "user" or "value" || pair.Key == "script" && pair.Value is null) &&
            binding["value"] is not (JsonObject or JsonArray),
        _ => false
    };

    private static bool IsZero(JsonNode node) =>
        node is JsonValue value && value.TryGetValue(out bool flag) ? !flag : TryConstant(node, out double[] values) && values.All(item => item == 0);

}
