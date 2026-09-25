using System.Text.Json;
using System.Text.Json.Nodes;
using Baker.Core;

/// <summary>
/// 摆动改频（feat/sway-retime）：振幅换算、逐项改频求解器与原型 per_term_best.py 六案对拍、shader 文本补丁
/// （含按 g_Speed 分支）、HybridLoopService 接入，以及开关关闭时 plan 不带任何改频痕迹。
/// </summary>
internal static class SwayRetimeChecks
{
    // stock foliagesway 的两段时钟语句与 uniform 注释（effects/foliagesway/shaders/effects/foliagesway.frag|.vert 原文摘录）。
    internal const string Fragment = """
        // [COMBO] {"material":"ui_editor_properties_mode","combo":"MODE","type":"options","default":0,"options":{"Vertex":1,"UV":0}}
        uniform float g_Speed; // {"material":"speeduv","label":"ui_editor_properties_speed","default":5,"range":[0.01, 20]}
        uniform float g_Power; // {"material":"power","label":"ui_editor_properties_power","default":1,"range":[0.01, 2]}
        uniform float g_Phase; // {"material":"phase","label":"ui_editor_properties_phase","default":0.5,"range":[0, 2]}
        uniform float g_Time;
        void main() {
        	float phase = (noise.g * M_PI * 2 + v_Params.x * 10 + v_Params.y * 5) * g_Phase;
        	vec4 sines = phase + g_Speed * g_Time * vec4(1, -0.16161616, 0.0083333, -0.00019841);
        	sines = sin(sines);
        	vec4 csines = 0.4 + phase + g_Speed * g_Time * vec4(-0.5, 0.041666666, -0.0013888889, 0.000024801587);
        	csines = sin(csines);
        }
        """;
    internal const string Vertex = """
        uniform float g_Time;
        uniform float g_Speed; // {"material":"speed","label":"ui_editor_properties_speed","default":1,"range":[0.01, 10]}
        uniform float g_Strength; // {"material":"strength","label":"ui_editor_properties_strength","default":0.4,"range":[0.01, 1]}
        uniform float g_Phase; // {"material":"phase","label":"ui_editor_properties_phase","default":0,"range":[0, 6.28]}
        uniform float g_Power; // {"material":"power","label":"ui_editor_properties_power","default":1,"range":[0.01, 2]}
        uniform vec2 g_DirectionWeights; // {"material":"directionweights","label":"ui_editor_properties_direction_weights","default":"1 0.2"}
        uniform float g_Ratio; // {"material":"ratio","label":"ui_editor_properties_ratio","default":0.3,"range":[0.01,10]}
        uniform float g_Direction; // {"material":"scrolldirection","label":"ui_editor_properties_direction","default":0,"range":[0,6.28],"direction":true}
        void main() {
        	vec4 sines = g_Phase + g_Speed * g_Time * vec4(1, -0.16161616, 0.0083333, -0.00019841);
        	sines = sin(sines);
        	vec4 csines = 0.4 + g_Phase + g_Speed * g_Time * vec4(-0.5, 0.041666666, -0.0013888889, 0.000024801587);
        	csines = sin(csines);
        }
        """;

    private sealed record ProtoLayer(double Width, double Height, double Scale, double Speed, double Strength, double Ratio, double Direction);
    private sealed record ProtoCase(string Id, ulong PFrames, double OutScale, ProtoLayer[] Layers,
        ulong K600, string Change600, ulong K3600, string Change3600);

    // D:\WPE-sway-proto\sway_cases.json 的六案实参。期望值（k 与可见项最大改动两位小数）取自独立参考 v2_ref.py：
    // 原型 per_term_best 的逐项口径加规则 v2（可见项 T < 60 s 只许整数圈；慢项在冻结与改频里取峰值速度偏差小的，≤ 0.1 px/s；
    // 1 帧护栏；按 (可见项最大改动, 慢项最大速度偏差, k) 取最优）。旧版期望（per_term_best_results.txt）按全部项最大改动取最优。
    private static readonly ProtoCase[] Cases =
    [
        new("3648434762", 103, 1.0, [new(1920, 1080, 1, 3.6500001, 0.33000001, 0.30000001, 0.017453292),
            new(1920, 1080, 1, 5.4099998, 0.2, 0.30000001, 2.0930777)], 242, "0.64", 1997, "0.04"),
        new("3652446458", 725, 0.46444, [new(1568, 1994, 1, 2.8199999, 0.40000001, 0.30000001, 0.0),
            new(884, 1085, 1, 5.0, 0.43000001, 0.30000001, 0.078655131), new(1138, 1190, 1, 2.51, 0.70999998, 0.88999999, 0.37462601)],
            40, "0.66", 292, "0.09"),
        new("3653991401", 3249, 0.5, [new(3840, 2160, 1, 1.5, 0.34999999, 0.30000001, 0.0)], 11, "0.14", 56, "0.01"),
        new("3660348531", 267, 0.5, [new(3840, 2160, 1, 2.32, 0.35, 0.3, 0.57895776)], 79, "0.15", 723, "0.00"),
        new("3750317749", 483, 0.5, [new(3840, 2160, 1.09437, 3.55, 0.2, 0.3, 0.0)], 53, "0.44", 438, "0.01"),
        new("3486806915", 6000, 0.88889, [new(739, 1088, 1, 5.0, 0.52999997, 0.30000001, 1.9986103),
            new(1872, 1760, 1, 2.0, 0.63, 0.30000001, -0.90372425), new(944, 647, 1, 5.0, 0.60000002, 0.30000001, -2.9296732),
            new(876, 688, 1, 5.0, 0.49000001, 3.02, -0.48429704)], 6, "0.53", 35, "0.04"),
    ];

    private static SwayModel Model(int owner, ProtoLayer layer, double[]? coefficients = null) =>
        new(owner, 0, 0, "effects/foliagesway", 0, "speeduv", (float)layer.Speed, coefficients ?? SwayModel.CanonicalCoefficients,
            layer.Strength, layer.Ratio, layer.Direction, 1, 1, 0.2, layer.Width, layer.Height, layer.Scale, layer.Scale);

    /// <param name="canvasFactor">输出画布相对原案的边长倍数（4 = 1080p 案放到 8K）：振幅按输出像素精确放大这么多倍。</param>
    private static SwayRetimeInput[] Inputs(ProtoCase item, double canvasFactor = 1) => item.Layers.Select((layer, index) =>
    {
        SwayModel model = Model(index + 1, layer);
        var amplitude = model.AmplitudeOutputPixels(item.OutScale * canvasFactor, item.OutScale * canvasFactor);
        return new SwayRetimeInput(model, amplitude?.X, amplitude?.Y);
    }).ToArray();

    internal static async Task RunAsync(Action<bool, string> check, string root)
    {
        EquationChecks(check);
        SolverParityChecks(check);
        CanvasScaleChecks(check);
        TextPatchChecks(check);
        await IntegrationChecks(check, root);
    }

    private static void EquationChecks(Action<bool, string> check)
    {
        // 设计 §1.2：3750317749 L17 A_x 0.788 / A_y 0.126；阿米娅 L407 A_x 2.050 / A_y 5.950（输出比例 0.889）。
        var flowers = Model(17, Cases[4].Layers[0]).AmplitudeOutputPixels(0.5, 0.5)!.Value;
        check(Math.Abs(flowers.X - 0.788) < 1e-3 && Math.Abs(flowers.Y - 0.126) < 1e-3,
            "sway amplitude: White flowers L17 converts strength²·0.005, aspect, direction and layer scale into 0.788 / 0.126 output px");
        var body = Model(407, Cases[5].Layers[0]).AmplitudeOutputPixels(0.88889, 0.88889)!.Value;
        check(Math.Abs(body.X - 2.050) < 1e-3 && Math.Abs(body.Y - 5.950) < 1e-3,
            "sway amplitude: Amiya L407 rotated by its direction gives 2.050 / 5.950 output px");
        var coat = Model(403, Cases[5].Layers[1]).AmplitudeOutputPixels(0.88889, 0.88889)!.Value;
        check(Math.Abs(coat.X - 7.231) < 1e-3 && Math.Abs(coat.Y - 7.031) < 1e-3,
            "sway amplitude: Amiya L403 coat gives 7.231 / 7.031 output px");
        var vertex = (Model(9, Cases[4].Layers[0]) with { Mode = 1, Strength = 0.5, DirectionWeightX = 1, DirectionWeightY = 0.2, ScaleX = 2, ScaleY = 2 })
            .AmplitudeOutputPixels(0.5, 0.5)!.Value;
        check(Math.Abs(vertex.X - 50) < 1e-9 && Math.Abs(vertex.Y - 10) < 1e-9,
            "sway amplitude: MODE 1 vertex sway is strength·100·directionweights through scale and output ratio");
        check((Model(9, Cases[4].Layers[0]) with { LayerWidth = null }).AmplitudeOutputPixels(0.5, 0.5) is null,
            "sway amplitude: an unreadable MODE 0 layer size reports no amplitude instead of inventing one");
        check(Math.Abs(SwayRecurrenceSolver.Drift(2, 1e-3, 600) - 2 * 2 * Math.Sin(0.3)) < 1e-12 &&
            Math.Abs(SwayRecurrenceSolver.Drift(2, 1, 600) - 4) < 1e-12,
            "sway drift bound: 2A·sin(min(ωΔt/2, π/2)) saturates at 2A");
        check(SwayRecurrenceSolver.MaximumMultiple(6000, 60, 1, 3600) == 36 && SwayRecurrenceSolver.MaximumMultiple(6000, 60, 1, 99) == 0 &&
            SwayRecurrenceSolver.MaximumMultiple(483, 60, 1, 600) == 74,
            "sway solver: k ranges over floor(Lmax / P)");
    }

    private static void SolverParityChecks(Action<bool, string> check)
    {
        foreach (ProtoCase item in Cases)
        {
            SwayRetimeInput[] inputs = Inputs(item);
            foreach (var (maximum, expectedK, expectedChange) in new[] { (600.0, item.K600, item.Change600), (3600.0, item.K3600, item.Change3600) })
            {
                SwayRetimeSolution solution = SwayRecurrenceSolver.Solve(inputs, item.PFrames, 60, 1, maximum)!;
                string change = solution.MaximumVisibleChangePercent.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
                check(solution.Multiple == expectedK && solution.Frames == expectedK * item.PFrames && change == expectedChange &&
                    Math.Abs(solution.Seconds - expectedK * item.PFrames / 60.0) < 1e-9,
                    $"sway solver parity {item.Id} Lmax {maximum:0}: k={expectedK}, L={expectedK * item.PFrames} frames, visible max change {expectedChange}% (got k={solution.Multiple}, {change}%)");
                // 精确闭合：非冻结项新系数在 L 内恰好走整数圈，冻结项系数写 0。
                bool exact = solution.Layers.All(layer => layer.Terms.All(term => term.Frozen
                    ? term.CoefficientNew == 0 && term.CyclesExact < 0.5
                    : Math.Abs(Math.Abs(term.CoefficientNew) * layer.Model.Speed * solution.Seconds / (2 * Math.PI) - term.Cycles) < 1e-9 &&
                      Math.Sign(term.CoefficientNew) == Math.Sign(term.CoefficientOld)));
                check(exact, $"sway solver {item.Id} Lmax {maximum:0}: every retimed term closes on whole cycles and every frozen term is zero");
                // 规则 v2：可见项不冻结；慢项取冻结（2πa/T）与改到最近整数圈（2πa·|n/L − 1/T|）里偏差小的，偏差 ≤ 0.1 px/s。
                bool v2 = solution.Layers.All(layer => layer.Terms.Where(term => term.CoefficientOld != 0).All(term =>
                {
                    double rate = 2 * Math.PI / term.OldPeriodSeconds, a = term.AmplitudePixels;
                    if (term.Visible) return !term.Frozen;
                    long nearest = (long)Math.Round(term.CyclesExact);
                    double freeze = a * rate, retime = nearest == 0 ? double.PositiveInfinity : a * Math.Abs(2 * Math.PI * nearest / solution.Seconds - rate);
                    double expected = Math.Min(freeze, retime);
                    return term.Frozen == freeze < retime && Math.Abs(term.SpeedDeviationPixelsPerSecond!.Value - expected) <= 1e-12 &&
                        expected <= SwayRecurrenceSolver.MaximumSlowSpeedDeviationPixelsPerSecond;
                }));
                check(v2, $"sway rule v2 {item.Id} Lmax {maximum:0}: visible terms never freeze and each slow term takes the smaller peak speed deviation, at most 0.1 px/s");
                // 分档改动：可见（周期 < 60 s）与慢项（≥ 60 s）各取非冻结项的最大 |δ|，两者较大者就是总的最大改动；慢项最大偏差就是逐项最大值。
                double Worst(Func<double, bool> period) => solution.Layers.SelectMany(layer => layer.Terms)
                    .Where(term => !term.Frozen && period(term.OldPeriodSeconds)).Select(term => Math.Abs(term.DeltaPercent)).DefaultIfEmpty(0).Max();
                check(solution.MaximumVisibleChangePercent == Worst(period => period < 60) &&
                    solution.MaximumEnvelopeChangePercent == Worst(period => period >= 60) &&
                    Math.Abs(Math.Max(solution.MaximumVisibleChangePercent, solution.MaximumEnvelopeChangePercent) - solution.MaximumChangePercent) < 1e-9 &&
                    solution.MaximumSlowSpeedDeviationPixelsPerSecond == solution.Layers.SelectMany(layer => layer.Terms)
                        .Where(term => term.CoefficientOld != 0 && !term.Visible).Max(term => term.SpeedDeviationPixelsPerSecond!.Value),
                    $"sway change tiers {item.Id} Lmax {maximum:0}: visible change and slow-term speed deviation maxima are split by the 60 s period line");
            }
        }
        // 冻结集合：阿米娅 3600 s 档（k = 35，3500 s）冻结四层的 1/5040 与 1/40320 里振幅×角速度比改频偏差更小的项。
        SwayRetimeSolution amiya3600 = SwayRecurrenceSolver.Solve(Inputs(Cases[5]), 6000, 60, 1, 3600)!;
        check(amiya3600.FrozenCount == 5 && amiya3600.FrozenTerms.All(frozen => frozen.Term.Name is "1/5040" or "1/40320") &&
            amiya3600.MaximumSlowSpeedDeviationPixelsPerSecond < 0.01,
            "sway rule v2: Amiya 3500 s freezes only 1/5040 and 1/40320 terms and keeps every slow-term speed deviation under 0.01 px/s");
        // 600 s 档（k = 6）：速度 5 三层的 1/720（905 s）改到 600 s 的偏差比冻结小，改频；速度 2 那层的 1/720（2262 s）冻结，共 9 项。
        SwayRetimeSolution amiya600 = SwayRecurrenceSolver.Solve(Inputs(Cases[5]), 6000, 60, 1, 600)!;
        check(amiya600.FrozenCount == 9 && amiya600.Layers.All(layer =>
                layer.Terms.Where(term => term.Frozen).Select(term => term.Name)
                    .SequenceEqual(layer.Model.Speed == 2 ? ["1/5040", "1/720", "1/40320"] : ["1/5040", "1/40320"])),
            "sway rule v2: Amiya 600 s retimes the 905 s 1/720 terms of the speed-5 layers because that deviates less than freezing, and freezes the 2262 s one");
        // 与样片代理实测过的 shader 系数一致（frag 34/36 行，10 位有效数字）：样片用的是 k = 25（2500 s），在该 k 上逐项取值。
        static bool Near(IEnumerable<double> actual, params double[] expected) =>
            actual.Zip(expected).All(pair => Math.Abs(pair.First - pair.Second) <= 2e-9 * Math.Max(1, Math.Abs(pair.Second)));
        SwayRetimeSolution amiya2500 = SwayRecurrenceSolver.Detail(Inputs(Cases[5]), 6000, 25, 60, 1);
        SwayRetimeLayer fast = amiya2500.Layers[0], slow = amiya2500.Layers[1];
        check(Near(fast.Terms.Select(term => term.CoefficientNew), 0.9997804461, -0.1618548535, 0.008545132018, 0.0,
                  -0.5001415505, 0.04172035044, -0.001507964474, 0.0) &&
              Near(slow.Terms.Select(term => term.CoefficientNew), 1.000283101, -0.1621061809, 0.00879645943, 0.0,
                  -0.5001415505, 0.04146902303, -0.001256637061, 0.0),
            "sway solver: Amiya 2500 s coefficients for speed 5 and speed 2 equal the shader the sample agent rendered and closed");
        SwayRetimeSolution flowers = SwayRecurrenceSolver.Solve(Inputs(Cases[4]), 483, 60, 1, 600)!;
        check(Near(flowers.Layers[0].Terms.Select(term => term.CoefficientNew), 0.9997624331, -0.1617872817, 0.008296783677, 0.0,
                -0.5019554125, 0.04148391839, 0.0, 0.0) && flowers.Frames == 25599,
            "sway solver: White flowers 600 s coefficients equal the sampled shader (L = 25599 frames)");
        // 合成两项：频率 1 与 1/24 rad/s（周期 6.3 s 可见、151 s 慢项，振幅 1 px），P = 1 s。暴力参考按规则 v2 逐 k 判定。
        double[] twoTerms = [1, 0, 0, 0, 0, 1.0 / 24, 0, 0];
        var synthetic = new SwayRetimeInput(Model(1, Cases[4].Layers[0], twoTerms) with { Speed = 1 }, 1, 1);
        // 这一条只对拍目标函数，循环下限（60 s / 3 圈）显式关掉，免得合成案被下限先挡掉。
        SwayRetimeSolution two = SwayRecurrenceSolver.Solve([synthetic], 60, 60, 1, 200, null, 0, 1)!;
        (double Change, double Deviation, ulong K) reference = (double.MaxValue, double.MaxValue, 0);
        for (ulong k = 1; k <= 200; ++k)
        {
            double x = k / (2 * Math.PI), y = k / 24.0 / (2 * Math.PI);
            if (Math.Round(x) == 0) continue;
            double change = Math.Abs(Math.Round(x) / x - 1);
            double deviation = Math.Min(1.0 / 24, Math.Round(y) == 0 ? double.PositiveInfinity : Math.Abs(2 * Math.PI * Math.Round(y) / k - 1.0 / 24));
            if (deviation > 0.1) continue;
            var key = (Math.Round(change, 6), Math.Round(deviation, 6), k);
            if (key.Item1 < reference.Change || key.Item1 == reference.Change && key.Item2 < reference.Deviation) reference = key;
        }
        check(two.Multiple == reference.K && two.Layers[0].Terms.Where(term => term.CoefficientOld == 0).All(term => term.CoefficientNew == 0 && term.Frozen) &&
            two.FrozenTerms.All(item => item.Term.CoefficientOld != 0),
            "sway solver: a synthetic two-term series picks the k a brute-force rule-v2 reference picks, and zero coefficients stay zero without counting as frozen");
        check(SwayRecurrenceSolver.Solve(Inputs(Cases[5]), 6000, 60, 1, 60) is null,
            "sway solver: a base period longer than the loop-length maximum has no solution");

        // 静止基础候选（P = 1 帧）+ 摆动：旧规则 k = 1 把 8 项全冻结（含 1～4 s 的主摆动）、改动 0，恒胜出，结论报"静止画面只需 1 帧"。
        foreach (int caseIndex in new[] { 0, 4, 5 })
        {
            SwayRetimeInput[] inputs = Inputs(Cases[caseIndex]);
            SwayRetimeSolution oldPick = SwayRecurrenceSolver.Detail(inputs, 1, 1, 60, 1);
            SwayRetimeSolution? still = SwayRecurrenceSolver.Solve(inputs, 1, 60, 1, 600);
            check(oldPick.FrozenCount == 8 * inputs.Length && oldPick.FrozenMinimumPeriodSeconds < 5 &&
                still is { Frames: > 1 } && still.FrozenTerms.All(frozen => !frozen.Term.Visible) &&
                still.Layers.All(layer => layer.Terms.Where(term => term.Name is "1" or "1/2").All(term => !term.Frozen)),
                $"sway static guard {Cases[caseIndex].Id}: a one-frame base candidate no longer freezes the visible sway into a still image");
        }
        // 1 帧护栏本身：每帧恰好走 1 圈的项在 L = 1 帧上改动为 0、不冻结，旧规则会产出 1 帧候选；现在跳到 2 帧，上限只容 1 帧时无解。
        var perFrame = new SwayRetimeInput(Model(1, Cases[4].Layers[0], [2 * Math.PI * 60, 0, 0, 0, 0, 0, 0, 0]) with { Speed = 1 }, 1, 1);
        check(SwayRecurrenceSolver.Solve([perFrame], 1, 60, 1, 1, null, 0, 1) is { Frames: 2, MaximumChangePercent: < 1e-9 } &&
            SwayRecurrenceSolver.Solve([perFrame], 1, 60, 1, 1.0 / 60, null, 0, 1) is null,
            "sway static guard: a candidate of one frame is never produced while any sway term still moves");
        // 慢项速度上限：一项 900 s、振幅 50 px 的慢项，冻结偏差 2π·50/900 = 0.349 px/s；L ≤ 600 s 时改到 1 圈的偏差也 ≥ 0.17 px/s，
        // 上限 600 s 无解；上限 3600 s 时 L = 900 s 正好整圈，偏差 0。振幅 1 px 时冻结偏差 0.007 px/s，300 s 内可以冻结。
        double slowRate = 2 * Math.PI / 900;
        SwayRetimeInput Slow(double amplitude) => new(Model(1, Cases[4].Layers[0], [0, 0, 0, 0, 0, 0, slowRate, 0]) with { Speed = 1 }, amplitude, amplitude);
        check(SwayRecurrenceSolver.Solve([Slow(50)], 60, 60, 1, 600) is null &&
            SwayRecurrenceSolver.Solve([Slow(50)], 60, 60, 1, 3600) is { Frames: 54000, FrozenCount: 0 } exact900 &&
            exact900.MaximumSlowSpeedDeviationPixelsPerSecond < 1e-12 &&
            SwayRecurrenceSolver.Solve([Slow(1)], 60, 60, 1, 300) is { FrozenCount: 1 } frozenSlow &&
            Math.Abs(frozenSlow.MaximumSlowSpeedDeviationPixelsPerSecond!.Value - slowRate) < 1e-12,
            "sway speed limit: a slow term whose freeze and retime deviations both exceed 0.1 px/s rules out every L, while a faint one may freeze");
        // 振幅读不到时慢项偏差无从判定：不给解，也不把它当 0。
        check(SwayRecurrenceSolver.Solve([new(Inputs(Cases[4])[0].Model, null, null)], 483, 60, 1, 600) is null,
            "sway speed limit: an unknown amplitude never passes as zero deviation");
        double[] tolerances = SwayRecurrenceSolver.SpeedTolerances([2.0, 2.0002, 5.0]);
        check(Math.Abs(tolerances[0] - 1e-4) < 1e-15 && Math.Abs(tolerances[1] - 1e-4) < 1e-12 && Math.Abs(tolerances[2] - 5e-4) < 1e-15 &&
            SwayRecurrenceSolver.SpeedTolerances([2.0, 2.0001])[0] < 6e-5,
            "sway shader branches: speed tolerance is relative 1e-4 and never reaches half the gap to a neighbouring speed");
    }

    /// <summary>
    /// fix-j：同一运动放到 4 倍边长的画布（1080p → 8K），振幅与速度偏差按输出像素精确 ×4；门限随短边 ×4 后，
    /// 六案在各上限与预算下解出的 L、冻结集合与 1080p 完全相同。不换算门限（旧口径）时至少一案改判，说明这些案确实压在门限附近。
    /// </summary>
    private static void CanvasScaleChecks(Action<bool, string> check)
    {
        double eightK = SwayRecurrenceSolver.SpeedLimitScale(7680, 4320);
        static string Frozen(SwayRetimeSolution? solution) => solution is null ? "none"
            : string.Join(",", solution.FrozenTerms.Select(item => item.Layer.Model.OwnerLayerId + ":" + item.Term.Index));
        bool same = true, changedWithoutScale = false;
        foreach (ProtoCase item in Cases)
        foreach (double maximum in new[] { 600.0, 3600.0 })
        foreach (double? budget in new double?[] { null, 3 })
        {
            SwayRetimeSolution? reference = SwayRecurrenceSolver.Solve(Inputs(item), item.PFrames, 60, 1, maximum, budget);
            SwayRetimeSolution? scaled = SwayRecurrenceSolver.Solve(Inputs(item, 4), item.PFrames, 60, 1, maximum, budget, speedLimitScale: eightK);
            SwayRetimeSolution? unscaled = SwayRecurrenceSolver.Solve(Inputs(item, 4), item.PFrames, 60, 1, maximum, budget);
            same &= reference?.Multiple == scaled?.Multiple && Frozen(reference) == Frozen(scaled);
            changedWithoutScale |= reference?.Multiple != unscaled?.Multiple;
        }
        check(same, "sway speed limits scale with the output short edge: the same motion on a 4x canvas solves to the same L and frozen set");
        check(changedWithoutScale, "sway speed limits scale: without scaling, at least one case changes its L on the 4x canvas (the check is not vacuous)");
    }

    private static void TextPatchChecks(Action<bool, string> check)
    {
        string text = Fragment + "\n" + Vertex;
        check(ShaderTextPatch.TryParseClockTerms(text, out ShaderTextPatch.ClockTerms clock) &&
            clock.Sines.Branches.Count == 0 && clock.Sines.Fallback.Concat(clock.CoSines.Fallback).SequenceEqual(SwayModel.CanonicalCoefficients),
            "shader text: the stock clock statements parse as the canonical eight coefficients");
        check(!ShaderTextPatch.TryParseClockTerms(Fragment, out _),
            "shader text: a fragment without its vertex stage does not have both clock statements twice");
        check(!ShaderTextPatch.TryParseClockTerms(Fragment.Replace("vec4(-0.5, 0.041666666", "vec4(-0.25, 0.041666666") + "\n" + Vertex, out _),
            "shader text: frag and vert stages with different coefficients are rejected");
        check(ShaderTextPatch.TryParseExpression("(g_Speed > 3.5 ? vec4(0.9997804461, -0.1618548535, 0.008545132018, 0.0) : vec4(1.000283101, -0.1621061809, 0.00879645943, 0.0))",
                out SwayCoefficientExpression threshold) &&
            threshold.Evaluate(5)[0] == 0.9997804461 && threshold.Evaluate(2)[0] == 1.000283101,
            "shader text: the sample agent's threshold branch form parses and selects by g_Speed");
        check(ShaderTextPatch.TryParseExpression("vec4(0, 0.0, -0., .5)", out SwayCoefficientExpression zeros) &&
            zeros.Fallback.SequenceEqual([0, 0, 0, 0.5]),
            "shader text: zero and already-rewritten literal forms parse");

        SwayRetimeSolution amiya = SwayRecurrenceSolver.Solve(Inputs(Cases[5]), 6000, 60, 1, 3600)!;
        ShaderTextPatch.SpeedRetime[] speeds = amiya.Layers.GroupBy(layer => layer.Model.Speed).OrderBy(group => group.Key)
            .Select(group => new ShaderTextPatch.SpeedRetime(group.Key, 1e-4 * Math.Max(1, group.Key),
                group.First().Terms.Select(term => term.CoefficientOld).ToArray(), group.First().Terms.Select(term => term.CoefficientNew).ToArray()))
            .ToArray();
        string frag = ShaderTextPatch.RewriteStage(Fragment, speeds, out int fragStatements);
        string vert = ShaderTextPatch.RewriteStage(Vertex, speeds, out int vertStatements);
        check(fragStatements == 2 && vertStatements == 2 &&
            frag.Contains("g_Speed * g_Time * (abs(g_Speed - 2.0) < 0.0002 ? vec4(", StringComparison.Ordinal) &&
            frag.Contains(": (abs(g_Speed - 5.0) < 0.0005 ? vec4(", StringComparison.Ordinal) &&
            frag.Contains(": vec4(1, -0.16161616, 0.0083333, -0.00019841)))", StringComparison.Ordinal),
            "shader text patch: layers with speed 2 and speed 5 on one shader get a g_Speed branch per speed, falling back to the original literal");
        check(ShaderTextPatch.TryParseClockTerms(frag + "\n" + vert, out ShaderTextPatch.ClockTerms patched) &&
            patched.Sines.Evaluate(5).Concat(patched.CoSines.Evaluate(5)).Zip(speeds[1].NewCoefficients).All(pair => Math.Abs(pair.First - pair.Second) <= 1e-13 * Math.Max(1, Math.Abs(pair.Second))) &&
            patched.Sines.Evaluate(2).Concat(patched.CoSines.Evaluate(2)).Zip(speeds[0].NewCoefficients).All(pair => Math.Abs(pair.First - pair.Second) <= 1e-13 * Math.Max(1, Math.Abs(pair.Second))) &&
            patched.Sines.Evaluate(3).Concat(patched.CoSines.Evaluate(3)).SequenceEqual(SwayModel.CanonicalCoefficients),
            "shader text patch: the rewritten stages evaluate to the new coefficients at each speed and to the original at any other speed");
        ShaderTextPatch.TryParseClockTerms(text, out ShaderTextPatch.ClockTerms original);
        check(patched.MaskedText == original.MaskedText,
            "shader text patch: every byte outside the two coefficient expressions per stage is unchanged");
        check(!System.Text.RegularExpressions.Regex.IsMatch(frag, @"\d[eE][-+]?\d"),
            "shader text patch: coefficient literals are plain decimals without exponents");
        bool threw = false;
        try { ShaderTextPatch.RewriteStage(Fragment.Replace("0.0083333", "0.0083334"), speeds, out _); }
        catch (InvalidDataException) { threw = true; }
        check(threw, "shader text patch: a shader whose coefficients changed since analysis is refused instead of patched");
        ShaderTextPatch.SpeedRetime[] again = speeds.Select(speed => speed with { OldCoefficients = speed.NewCoefficients }).ToArray();
        string twiceFrag = ShaderTextPatch.RewriteStage(frag, again, out _), twiceVert = ShaderTextPatch.RewriteStage(vert, again, out _);
        check(ShaderTextPatch.TryParseClockTerms(twiceFrag + "\n" + twiceVert, out ShaderTextPatch.ClockTerms nested) &&
            nested.Sines.Branches.Count == 4 && nested.Sines.Evaluate(3).Concat(nested.CoSines.Evaluate(3)).SequenceEqual(SwayModel.CanonicalCoefficients) &&
            nested.Sines.Evaluate(5).SequenceEqual(patched.Sines.Evaluate(5)),
            "shader text patch: re-patching an already patched stage verifies against its branch values and nests around it without losing the original fallback");
        check(ShaderTextPatch.GlslFloat(0) == "0.0" && ShaderTextPatch.GlslFloat(5) == "5.0" && ShaderTextPatch.GlslFloat(-0.5) == "-0.5" &&
            ShaderTextPatch.GlslFloat(0.000024801587) == "0.000024801587" && ShaderTextPatch.GlslFloat((float)3.55) == "3.54999995231628",
            "shader text patch: GLSL float literals always carry a decimal point and round-trip float32 speeds");
    }

    private static async Task IntegrationChecks(Action<bool, string> check, string root)
    {
        string sourceDirectory = Path.Combine(root, "sway-retime-source");
        void Write(string relative, string text)
        {
            string path = Path.Combine(sourceDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }
        Write("effects/foliagesway/effect.json", """{"passes":[{"material":"materials/effects/foliagesway.json"}]}""");
        Write("materials/effects/foliagesway.json", """{"passes":[{"shader":"effects/foliagesway"}]}""");
        Write("shaders/effects/foliagesway.frag", Fragment);
        Write("shaders/effects/foliagesway.vert", Vertex);
        Write("effects/filmgrain.json", """{"passes":[{"material":"materials/filmgrain.json"}]}""");
        Write("materials/filmgrain.json", """{"passes":[{"shader":"filmgrain"}]}""");
        Write("shaders/filmgrain.frag", "void main() { gl_FragColor = vec4(1.0); }");
        Write("shaders/filmgrain.vert", """
            uniform float g_Time;
            uniform float g_NoiseScale; // {"material":"scale","label":"ui_editor_properties_scale","default":10,"range":[0.0, 20.0]}
            void main() {
            	float t = frac(g_Time);
            	v_TexCoord = a_TexCoord.xyxy;
            	v_TexCoordNoise.xy = (a_TexCoord.xy + t) * g_NoiseScale;
            	v_TexCoordNoise.zw = (a_TexCoord.xy - t * 2.5) * g_NoiseScale * 0.52;
            	v_TexCoordNoise *= vec4(aspect, 1.0, aspect, 1.0);
            }
            """);
        JsonObject Sway(int id, double speed, double strength = 0.2) => new()
        {
            ["id"] = id, ["size"] = "3840 2160", ["scale"] = "1.09437 1.09437 1",
            ["effects"] = new JsonArray(new JsonObject { ["file"] = "effects/foliagesway/effect.json", ["passes"] = new JsonArray(
                new JsonObject { ["constantshadervalues"] = new JsonObject { ["speeduv"] = speed, ["strength"] = strength, ["ratio"] = 0.3 } }) })
        };
        JsonObject Grain(int id) => new()
        {
            ["id"] = id, ["effects"] = new JsonArray(new JsonObject { ["file"] = "effects/filmgrain.json", ["passes"] = new JsonArray(
                new JsonObject { ["constantshadervalues"] = new JsonObject { ["scale"] = 10.0 } }) })
        };
        Write("scene.json", new JsonObject { ["objects"] = new JsonArray(Grain(1), Sway(2, 3.55)) }.ToJsonString());
        using var source = new ProjectSource(sourceDirectory);
        var scene = new JsonObject { ["objects"] = new JsonArray(Grain(1), Sway(2, 3.55)) };
        var options = new SwayRetimeOptions(600, 0.5, 0.5);

        ShaderPeriodAnalysisResult analysis = ShaderPeriodAnalysis.Analyze(scene, source, null, [1, 2]);
        ShaderTemporalUnresolved sway = analysis.Unresolved.Single(item => item.OwnerLayerId == 2);
        check(sway.Mechanism == ShaderPeriodAnalysis.FoliageSwayMechanism && sway.Sway is { Mode: 0, SpeedKey: "speeduv" } model &&
            model.Speed == (float)3.55 && model.Coefficients.SequenceEqual(SwayModel.CanonicalCoefficients) &&
            model.LayerWidth == 3840 && model.ScaleX == 1.09437 && model.Strength == 0.2,
            "sway model: the analysis attaches the structured equation (float32 speed, coefficients, geometry) and keeps the old detail text");

        // 开关关闭：plan 里不出现任何改频字段，未解析项的字段集合与旧版一样。
        JsonObject off = LoopAnalysis.Analyze(scene.DeepClone().AsObject(), source, null, new JsonObject(), [1, 2], 60, 1).ToJson();
        JsonObject offSway = off["unresolved"]!.AsArray().OfType<JsonObject>().Single(item => item["owner_layer_id"]!.GetValue<int>() == 2);
        check(!off.ToJsonString().Contains("sway_retime", StringComparison.Ordinal) && !off.ToJsonString().Contains("sway_model", StringComparison.Ordinal) &&
            offSway.Select(pair => pair.Key).SequenceEqual(["kind", "owner_layer_id", "effect_index", "pass_index", "resource", "detail", "bounded_displacement", "mechanism"]) &&
            off["candidates"]!.AsArray().OfType<JsonObject>().All(candidate => candidate["frames"]!.GetValue<ulong>() <= 180 * 60),
            "sway retime off: the loop report carries no retime fields and the sway item keeps exactly the old keys");
        JsonObject offAgain = LoopAnalysis.Analyze(scene.DeepClone().AsObject(), source, null, new JsonObject(), [1, 2], 60, 1, 2,
            CommonLoopPreference.Balanced, null).ToJson();
        check(offAgain.ToJsonString() == off.ToJsonString(), "sway retime off: passing no options is byte-identical to the default call");

        // feat/retime-budget：质量档在档位上限（这里给 1200 s）与 600 s 下各求一次，
        // 取可见摆动改动更小的那次，并把两次读数记进 quality_ceiling_used。其余档位只求一次，记录不出现。
        JsonObject ForProfile(string preset, double? maximum = null) => HybridScenePlanner.AnalyzeLoopForProfile(() => scene.DeepClone().AsObject(),
            source, null, new JsonObject(), [1, 2],
            new HybridAnalyzeRequest(2, "s", "a", "o", 1920, 1080, 60, 1, SwayRetime: true, Preset: preset, LoopLengthMaximumSeconds: maximum),
            new JsonObject(), null);
        JsonObject quality = ForProfile(RetimeProfile.Quality, 1200), balancedLoop = ForProfile(RetimeProfile.Balanced);
        check(ForProfile(RetimeProfile.Quality)["quality_ceiling_used"] is null,
            "default quality uses the 600-second ceiling without an unnecessary second solve");
        JsonObject used = quality["quality_ceiling_used"]!.AsObject();
        JsonObject[] tried = used["tried"]!.AsArray().OfType<JsonObject>().ToArray();
        double Visible(JsonObject entry) => entry["max_change_visible_percent"]?.GetValue<double>() ?? double.PositiveInfinity;
        check(tried.Length == 2 && tried[0]["source"]!.GetValue<string>() == "preset" && tried[1]["source"]!.GetValue<string>() == "quality_comparison" &&
            tried[0]["ceiling_seconds"]!.GetValue<double>() == 1200 && tried[1]["ceiling_seconds"]!.GetValue<double>() == 600 &&
            used["seconds"]!.GetValue<double>() == quality["maximum_seconds"]!.GetValue<double>() &&
            Visible(tried[used["source"]!.GetValue<string>() == "preset" ? 0 : 1]) <= Visible(tried[used["source"]!.GetValue<string>() == "preset" ? 1 : 0]) &&
            balancedLoop["quality_ceiling_used"] is null,
            "quality ceiling: the quality preset solves under both ceilings, keeps the smaller visible change and records both readings; other presets solve once");

        JsonObject on = LoopAnalysis.Analyze(scene.DeepClone().AsObject(), source, null, new JsonObject(), [1, 2], 60, 1, 2,
            CommonLoopPreference.Balanced, options).ToJson();
        JsonObject[] candidates = on["candidates"]!.AsArray().OfType<JsonObject>().ToArray();
        JsonObject first = candidates[0];
        JsonObject retime = first["sway_retime"]!.AsObject();
        ulong frames = first["frames"]!.GetValue<ulong>();
        check(on["unresolved"]!.AsArray().Count == 0 && candidates.All(candidate => candidate["sway_retime"] is JsonObject) &&
            frames % 60 == 0 && frames <= 600 * 60 && retime["frames"]!.GetValue<ulong>() == frames &&
            first["components"]![0]!["cycles"]!.GetValue<ulong>() == frames / 60 &&
            on["sway_retime"]!["status"]!.GetValue<string>() == "applied" && on["status"]!.GetValue<string>() == "analytic_candidate_requires_seam_validation",
            "sway retime on: the sway item leaves unresolved, every candidate becomes L = kP with multiplied component cycles and a sway_retime record");
        var planAmplitude = sway.Sway!.AmplitudeOutputPixels(0.5, 0.5)!.Value;
        SwayRetimeSolution direct = SwayRecurrenceSolver.Solve([new(sway.Sway!, planAmplitude.X, planAmplitude.Y)], retime["base_frames"]!.GetValue<ulong>(), 60, 1, 600)!;
        check(retime["multiple"]!.GetValue<ulong>() == direct.Multiple &&
            retime["shaders"]![0]!["shader"]!.GetValue<string>() == "effects/foliagesway" &&
            retime["shaders"]![0]!["speeds"]![0]!["speed"]!.GetValue<double>() == (float)3.55 &&
            retime["layers"]![0]!["frozen"]!.AsArray().Count == retime["frozen_count"]!.GetValue<int>() &&
            retime["layers"]![0]!["deltas_percent"]!.AsArray().Count == 8,
            "sway retime record: L, multiple, per-layer deltas, frozen set and the per-shader speed table are written to the plan");

        JsonObject tooShort = LoopAnalysis.Analyze(scene.DeepClone().AsObject(), source, null, new JsonObject(), [1, 2], 60, 1, 2,
            CommonLoopPreference.Balanced, new SwayRetimeOptions(0.5, 0.5, 0.5)).ToJson();
        // --loop-max-seconds 同时是求解器上限：0.5 秒装不下 1 秒的颗粒周期，其余分量就没有基础候选，摆动项照旧留在未解析项里。
        check(tooShort["unresolved"]!.AsArray().Count == 1 && tooShort["sway_retime"]!["status"]!.GetValue<string>() == "no_base_candidate" &&
            tooShort["candidates"]!.AsArray().Count == 0 && tooShort["maximum_seconds"]!.GetValue<double>() == 0.5 &&
            tooShort["no_candidate_reason"]!["kind"]!.GetValue<string>() == "FixedPeriodExceedsCeiling" &&
            tooShort["no_candidate_reason"]!["ceiling_seconds"]!.GetValue<double>() == 0.5,
            "sway retime on with a loop length maximum shorter than every base period: the shared ceiling leaves no candidate and the sway item stays unresolved, with a bilingual reason");

        // 慢项速度上限：strength 1 时 1/120 项（212 s）振幅约 19.7 px，冻结偏差约 0.58 px/s；上限 100 s 内它走不满半圈只能冻结，
        // 每个 kP 都超限，整体按未解析处理。strength 0.2 时同一项冻结偏差只有 0.023 px/s，上限 100 s 照样给解。
        var strongScene = new JsonObject { ["objects"] = new JsonArray(Grain(1), Sway(2, 3.55, 1.0)) };
        JsonObject overLimit = LoopAnalysis.Analyze(strongScene, source, null, new JsonObject(), [1, 2], 60, 1, 2,
            CommonLoopPreference.Balanced, new SwayRetimeOptions(100, 0.5, 0.5)).ToJson();
        // faint 用 200 s 上限：同一张场景最慢的可见项是 42.5 s 的 1/24 项，100 s 内它只走得了 2 圈，先被圈数下限挡掉（见下一条）。
        JsonObject faint = LoopAnalysis.Analyze(scene.DeepClone().AsObject(), source, null, new JsonObject(), [1, 2], 60, 1, 2,
            CommonLoopPreference.Balanced, new SwayRetimeOptions(200, 0.5, 0.5)).ToJson();
        check(overLimit["unresolved"]!.AsArray().Count == 1 &&
            overLimit["sway_retime"]!["status"]!.GetValue<string>() == "no_multiple_meets_speed_limit" &&
            overLimit["sway_retime"]!["speed_limit_rejected_candidate_count"]!.GetValue<int>() > 0 &&
            overLimit["candidates"]!.AsArray().Count > 0 &&
            overLimit["candidates"]!.AsArray().OfType<JsonObject>().All(candidate => candidate["sway_retime"] is null) &&
            faint["sway_retime"]!["status"]!.GetValue<string>() == "applied",
            "sway speed limit: when every L = kP leaves a slow term above 0.1 px/s the sway item stays unresolved with a bilingual reason; a faint layer still resolves");

        // fix-j：同两张场景放到 4 倍边长的画布（输出/场景比 ×4，8K 对 1080p），门限随短边 ×4：faint 解出同一个 L，
        // 记录写生效门限 0.4 / 0.8；strong 仍超限，原因里的数字是生效门限 0.4 px/s。
        double eightK = SwayRecurrenceSolver.SpeedLimitScale(7680, 4320);
        JsonObject faint8K = LoopAnalysis.Analyze(scene.DeepClone().AsObject(), source, null, new JsonObject(), [1, 2], 60, 1, 2,
            CommonLoopPreference.Balanced, new SwayRetimeOptions(200, 2, 2, SpeedLimitScale: eightK)).ToJson();
        JsonObject overLimit8K = LoopAnalysis.Analyze(new JsonObject { ["objects"] = new JsonArray(Grain(1), Sway(2, 3.55, 1.0)) }, source, null,
            new JsonObject(), [1, 2], 60, 1, 2, CommonLoopPreference.Balanced, new SwayRetimeOptions(100, 2, 2, SpeedLimitScale: eightK)).ToJson();
        JsonObject faintRetime = faint["candidates"]![0]!["sway_retime"]!.AsObject(), faint8KRetime = faint8K["candidates"]![0]!["sway_retime"]!.AsObject();
        static double Limit(JsonObject record, string kind) => record[kind + "_speed_deviation_limit_pixels_per_second"]!.GetValue<double>();
        check(faint8K["sway_retime"]!["status"]!.GetValue<string>() == "applied" &&
            faint8KRetime["multiple"]!.GetValue<ulong>() == faintRetime["multiple"]!.GetValue<ulong>() &&
            faint8KRetime["frames"]!.GetValue<ulong>() == faintRetime["frames"]!.GetValue<ulong>() &&
            Limit(faintRetime, "slow") == 0.1 && Limit(faintRetime, "visible") == 0.2 &&
            Limit(faint8KRetime, "slow") == 0.4 && Limit(faint8KRetime, "visible") == 0.8 &&
            overLimit8K["sway_retime"]!["status"]!.GetValue<string>() == "no_multiple_meets_speed_limit",
            "sway speed limit on an 8K canvas: the same motion resolves to the same L, the record and the bilingual reason carry the scaled limits");
        // 预算诊断同一口径：预算卡死（0%）时，"不设预算能不能解"的重解也要按生效门限判，否则大画布上会把"预算太紧"误报成"速度超限"。
        // 用 64 倍边长的画布（振幅 ×64，精确）让旧口径的门限一定咬住：不换算时报速度超限，换算后与原画布同样报预算太紧。
        RetimeProfile zeroBudget = RetimeProfile.Resolve(RetimeProfile.Balanced, 0, 200, 2);
        string BudgetStatus(double outputPerScene, double speedLimitScale) => LoopAnalysis.Analyze(scene.DeepClone().AsObject(), source, null,
            new JsonObject(), [1, 2], 60, 1, 2, CommonLoopPreference.Balanced,
            new SwayRetimeOptions(200, outputPerScene, outputPerScene, Profile: zeroBudget, SpeedLimitScale: speedLimitScale)).ToJson()["sway_retime"]!["status"]!.GetValue<string>();
        check(BudgetStatus(0.5, 1) == "no_multiple_within_budget" && BudgetStatus(32, 64) == "no_multiple_within_budget" &&
            BudgetStatus(32, 1) == "no_multiple_meets_speed_limit",
            "sway budget diagnosis on a large canvas: the no-budget re-solve uses the same scaled speed limits, so a budget rejection is not reported as a speed-limit rejection");

        // 振幅读不到（没有 size）：慢项偏差无从判定，候选与未解析项原样保留，写明是哪一层。
        JsonObject sizeless = Sway(2, 3.55);
        sizeless.Remove("size");
        var sizelessScene = new JsonObject { ["objects"] = new JsonArray(Grain(1), sizeless) };
        bool sizeUnknown = ShaderPeriodAnalysis.Analyze(sizelessScene, source, null, [1, 2]).Unresolved.Single(item => item.OwnerLayerId == 2).Sway?.LayerWidth is null;
        JsonObject unknown = LoopAnalysis.Analyze(sizelessScene, source, null, new JsonObject(), [1, 2], 60, 1, 2, CommonLoopPreference.Balanced, options).ToJson();
        check(sizeUnknown && unknown["sway_retime"]!["status"]!.GetValue<string>() == "amplitude_unknown" &&
            unknown["unresolved"]!.AsArray().Count == 1 && unknown["candidates"]!.AsArray().OfType<JsonObject>().All(candidate => candidate["sway_retime"] is null),
            "sway speed limit: a layer whose amplitude cannot be converted to output pixels leaves the sway unresolved with its layer named");

        // 只有摆动：以 1 帧作 P，但不产出 1 帧静止候选（旧版这里 L = 1 帧、8 项全冻结，结论报"静止画面只需 1 帧"）。
        var swayOnlyScene = new JsonObject { ["objects"] = new JsonArray(Sway(2, 3.55)) };
        JsonObject swayOnly = LoopAnalysis.Analyze(swayOnlyScene, source, null, new JsonObject(), [2], 60, 1, 2, CommonLoopPreference.Balanced, options).ToJson();
        JsonObject swayOnlyRetime = swayOnly["candidates"]![0]!["sway_retime"]!.AsObject();
        check(swayOnly["candidates"]!.AsArray().Count == 1 && swayOnlyRetime["base_frames"]!.GetValue<ulong>() == 1 &&
            swayOnly["candidates"]![0]!["frames"]!.GetValue<ulong>() > 1 &&
            swayOnlyRetime["max_slow_speed_deviation_pixels_per_second"]!.GetValue<double>() <= 0.1 &&
            swayOnly["unresolved"]!.AsArray().Count == 0 && swayOnly["no_candidate_reason"] is null,
            "sway retime on a scene whose only clock is sway takes one frame as P but grows the loop instead of freezing the sway into one frame");

        var twoSpeedScene = new JsonObject { ["objects"] = new JsonArray(Grain(1), Sway(2, 5.0), Sway(3, 2.0), Sway(4, 5.0)) };
        JsonObject twoSpeeds = LoopAnalysis.Analyze(twoSpeedScene, source, null, new JsonObject(), [1, 2, 3, 4], 60, 1, 2, CommonLoopPreference.Balanced, options).ToJson();
        JsonArray speedTable = twoSpeeds["candidates"]![0]!["sway_retime"]!["shaders"]![0]!["speeds"]!.AsArray();
        check(speedTable.Count == 2 && speedTable[0]!["speed"]!.GetValue<double>() == 2 && speedTable[1]!["speed"]!.GetValue<double>() == 5 &&
            speedTable[1]!["owner_layer_ids"]!.AsArray().Select(node => node!.GetValue<int>()).SequenceEqual([2, 4]),
            "sway retime record: layers sharing a shader and speed share one table entry, different speeds get separate entries");

        // bake 写覆盖 shader：只写捕获项目副本，读回后每个速度的系数就是计划里的新系数。
        string capture = Path.Combine(root, "sway-retime-capture");
        Directory.CreateDirectory(capture);
        JsonArray patches = await ShaderTextPatch.WriteSwayRetimeAsync(capture, source, null, twoSpeeds, CancellationToken.None);
        string writtenFrag = await File.ReadAllTextAsync(Path.Combine(capture, "shaders", "effects", "foliagesway.frag"));
        string writtenVert = await File.ReadAllTextAsync(Path.Combine(capture, "shaders", "effects", "foliagesway.vert"));
        JsonArray newFive = speedTable[1]!["coefficients_new"]!.AsArray();
        check(patches.Count == 2 && patches.OfType<JsonObject>().All(item => item["statements_rewritten"]!.GetValue<int>() == 2) &&
            ShaderTextPatch.TryParseClockTerms(writtenFrag + "\n" + writtenVert, out ShaderTextPatch.ClockTerms written) &&
            written.Sines.Evaluate(5).Concat(written.CoSines.Evaluate(5)).Zip(newFive.Select(node => node!.GetValue<double>()))
                .All(pair => Math.Abs(pair.First - pair.Second) <= 1e-13 * Math.Max(1, Math.Abs(pair.Second))) &&
            File.ReadAllText(Path.Combine(sourceDirectory, "shaders", "effects", "foliagesway.frag")) == Fragment,
            "sway shader patch: the bake writes both stages into the capture copy only, with the planned coefficients per speed");
        check((await ShaderTextPatch.WriteSwayRetimeAsync(capture, source, null, off, CancellationToken.None)).Count == 0,
            "sway shader patch: a plan without sway_retime writes nothing");

        // 覆盖后的 shader 再分析：系数是改频后的值，同一 P 上改动为 0，L 整除原 L。
        string patchedSource = Path.Combine(root, "sway-retime-patched-source");
        foreach (string file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(patchedSource, Path.GetRelativePath(sourceDirectory, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        File.Copy(Path.Combine(capture, "shaders", "effects", "foliagesway.frag"), Path.Combine(patchedSource, "shaders", "effects", "foliagesway.frag"), true);
        File.Copy(Path.Combine(capture, "shaders", "effects", "foliagesway.vert"), Path.Combine(patchedSource, "shaders", "effects", "foliagesway.vert"), true);
        using var patchedProject = new ProjectSource(patchedSource);
        ShaderPeriodAnalysisResult reanalysis = ShaderPeriodAnalysis.Analyze(twoSpeedScene, patchedProject, null, [1, 2, 3, 4]);
        SwayModel[] reread = reanalysis.Unresolved.Where(item => item.Sway is not null).Select(item => item.Sway!).ToArray();
        ulong planned = twoSpeeds["candidates"]![0]!["frames"]!.GetValue<ulong>();
        SwayRetimeSolution again = SwayRecurrenceSolver.Solve(reread.Select(item => new SwayRetimeInput(item, 1, 1)).ToArray(),
            twoSpeeds["candidates"]![0]!["sway_retime"]!["base_frames"]!.GetValue<ulong>(), 60, 1, 600)!;
        check(reread.Length == 3 && !reread[0].Coefficients.SequenceEqual(SwayModel.CanonicalCoefficients) && again.MaximumChangePercent < 1e-4 && planned % again.Frames == 0,
            "sway model: a retimed shader parses back, needs no further change on the same base period, and closes on a divisor of the planned L");

        var (_, cycles, _, deviation, _) = SwayRetimeJson.SummaryNumbers(retime);
        check(deviation is not null && cycles is not null,
            "sway retime summary: the summary numbers exist when retime applied");
        // 字段写进 plan，数值与逐项记录一致；a7838d6 / 4b713f4 的旧记录没有这两个字段时从 layers[].terms 现算出同样的数。
        double TermWorst(Func<double, bool> period) => retime["layers"]!.AsArray().OfType<JsonObject>()
            .SelectMany(layer => layer["terms"]!.AsArray().OfType<JsonObject>())
            .Where(term => term["delta_percent"] is JsonValue && term["old_period_seconds"] is JsonValue old && period(old.GetValue<double>()))
            .Select(term => Math.Abs(term["delta_percent"]!.GetValue<double>())).DefaultIfEmpty(0).Max();
        JsonObject legacy = retime.DeepClone().AsObject();
        legacy.Remove("max_change_visible_percent");
        legacy.Remove("max_slow_speed_deviation_pixels_per_second");
        legacy.Remove("phase_drift_cycles");
        legacy.Remove("slowest_visible_cycles");
        foreach (JsonObject term in legacy["layers"]!.AsArray().OfType<JsonObject>().SelectMany(layer => layer["terms"]!.AsArray().OfType<JsonObject>()))
            term.Remove("speed_deviation_pixels_per_second");
        double plannedDeviation = retime["max_slow_speed_deviation_pixels_per_second"]!.GetValue<double>();
        check(retime["max_change_visible_percent"]!.GetValue<double>() == TermWorst(period => period < 60) &&
            retime["max_change_envelope_percent"]!.GetValue<double>() == TermWorst(period => period >= 60) &&
            retime["slow_speed_deviation_limit_pixels_per_second"]!.GetValue<double>() == 0.1 && retime["visible_period_threshold_seconds"]!.GetValue<double>() == 60 &&
            Math.Abs(SwayRetimeJson.RecordedSlowSpeedDeviation(legacy)!.Value - plannedDeviation) <= 1e-12 * Math.Max(1, plannedDeviation) &&
            SwayRetimeJson.SummaryNumbers(legacy) == SwayRetimeJson.SummaryNumbers(retime),
            "sway change tiers: the plan records the visible change, the slow-term speed deviation and the limit; old records recompute the same numbers from their terms");

        // settings：默认值不写进 plan，开启时写出并能读回。
        var jsonOptions = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        var request = new HybridAnalyzeRequest(2, "s", "a", "o");
        JsonObject defaults = JsonSerializer.SerializeToNode(request, jsonOptions)!.AsObject();
        JsonObject enabled = JsonSerializer.SerializeToNode(request with { SwayRetime = true, LoopLengthMaximumSeconds = 3600 }, jsonOptions)!.AsObject();
        HybridAnalyzeRequest? readBack = enabled.Deserialize<HybridAnalyzeRequest>(jsonOptions);
        check(!defaults.ContainsKey("sway_retime") && !defaults.ContainsKey("loop_length_maximum_seconds") &&
            enabled["sway_retime"]!.GetValue<bool>() && enabled["loop_length_maximum_seconds"]!.GetValue<double>() == 3600 &&
            readBack is { SwayRetime: true, LoopLengthMaximumSeconds: 3600 } &&
            defaults.Deserialize<HybridAnalyzeRequest>(jsonOptions) is { SwayRetime: false, LoopLengthMaximumSeconds: null },
            "sway retime settings: defaults are omitted from the plan settings, enabled values round-trip");
        check(MessageCatalog.Find("summary.sway_retime") is not null && MessageCatalog.Find("sway_retime.no_multiple_within_maximum") is not null,
            "sway retime copy: the conclusion and rejection texts are in the bilingual message table");

        JsonObject squeezedLoop = LoopAnalysis.Analyze(scene.DeepClone().AsObject(), source, null, new JsonObject(), [1, 2], 60, 1, 2,
            CommonLoopPreference.Balanced, options with { LoopLengthMaximumSeconds = 0.5 }).ToJson();
        // 求解器与改频共用同一个上限：0.5 秒上限下求解器先就没有基础候选，状态是 no_base_candidate。
        check(squeezedLoop["sway_retime"]!["status"]!.GetValue<string>() == "no_base_candidate",
            "sway retime: when the loop-length maximum leaves no base candidate the status is no_base_candidate");

        // analyze、布局降级与 bake 前刷新共用的参数构造：上限就是请求里的值（不按分辨率收紧），开关关闭时为 null。
        var optionsOf = typeof(HybridScenePlanner).GetMethod("SwayRetimeOptionsOf", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var nativeRequest = new HybridAnalyzeRequest(2, "s", "a", "o", Width: 3840, Height: 2160, FpsNumerator: 60, SwayRetime: true, LoopLengthMaximumSeconds: 3600);
        var nativeProjection = new JsonObject { ["visible_width"] = 3840, ["visible_height"] = 2160 };
        SwayRetimeOptions? Of(HybridAnalyzeRequest value, JsonArray? groups) => (SwayRetimeOptions?)optionsOf.Invoke(null, [value, nativeProjection, null]);
        SwayRetimeOptions opaqueOptions = Of(nativeRequest, null)!;
        SwayRetimeOptions unknownSize = Of(nativeRequest with { Width = 0, Height = 0 }, null)!;
        check(opaqueOptions.LoopLengthMaximumSeconds == 3600 && Of(nativeRequest with { LoopLengthMaximumSeconds = 300 }, null)!.LoopLengthMaximumSeconds == 300 &&
            unknownSize.LoopLengthMaximumSeconds == 3600 && Of(nativeRequest with { SwayRetime = false }, null) is null,
            "sway retime options: the loop-length maximum is the requested one at any output size, and sway off gives null");
        // fix-j：速度门限倍率取最终输出画布的短边：3840×2160 与竖屏 2160×3840 都是 2，尺寸未知按 1080p 口径取 1。
        check(opaqueOptions.SpeedLimitScale == 2 && Of(nativeRequest with { Width = 2160, Height = 3840 }, null)!.SpeedLimitScale == 2 &&
            unknownSize.SpeedLimitScale == 1,
            "sway speed limit scale: sway options take the short edge of the final output canvas, portrait or landscape");
    }
}
