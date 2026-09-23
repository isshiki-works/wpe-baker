using System.Globalization;
using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>
/// 摆动改频结果的 plan JSON 写出与读回。求解本身在 Domain 的 <see cref="SwayRecurrenceSolver"/>（不碰 JSON）；
/// </summary>
public static class SwayRetimeJson
{
    /// <summary>
    /// 候选上的 sway_retime 记录：L、最大改动、冻结集合、每层每项 δ，以及写 shader 覆盖需要的按 shader、按速度的系数表。
    /// 同一 shader 上 float32 速度相同的层系数必然相同（δ 只取决于 s 与 L），表里只列一次。
    /// </summary>
    public static JsonObject ToJson(SwayRetimeSolution solution, double maximumSeconds, uint fpsNumerator, uint fpsDenominator,
        RetimeProfile? profile = null)
    {
        var shaders = new JsonArray();
        foreach (var group in solution.Layers.GroupBy(layer => layer.Model.Shader, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var bySpeed = group.GroupBy(layer => layer.Model.Speed).OrderBy(speeds => speeds.Key).ToArray();
            double[] tolerances = SwayRecurrenceSolver.SpeedTolerances(bySpeed.Select(speeds => speeds.Key).ToArray());
            var speeds = new JsonArray();
            for (int index = 0; index < bySpeed.Length; ++index)
            {
                SwayRetimeLayer first = bySpeed[index].First();
                // 同速度的层若系数表不同（不同 shader 文本不会同名），说明解析出了矛盾，直接拒绝。
                if (bySpeed[index].Any(layer => !layer.Model.Coefficients.SequenceEqual(first.Model.Coefficients)))
                    throw new InvalidDataException("Sway layers sharing a shader and a speed disagree on their coefficients.");
                speeds.Add(new JsonObject
                {
                    ["speed"] = first.Model.Speed, ["tolerance"] = tolerances[index],
                    ["owner_layer_ids"] = new JsonArray(bySpeed[index].Select(layer => (JsonNode)JsonValue.Create(layer.Model.OwnerLayerId)).ToArray()),
                    ["coefficients_old"] = Numbers(first.Terms.Select(term => term.CoefficientOld)),
                    ["coefficients_new"] = Numbers(first.Terms.Select(term => term.CoefficientNew))
                });
            }
            shaders.Add(new JsonObject { ["shader"] = group.Key, ["speeds"] = speeds });
        }
        var worst = solution.MaximumChangeTerm;
        return new JsonObject
        {
            ["method"] = "per_term_integer_cycles_slow_terms_by_speed_deviation",
            ["loop_length_maximum_seconds"] = maximumSeconds,
            ["base_frames"] = solution.BaseFrames, ["multiple"] = solution.Multiple,
            ["frames"] = solution.Frames, ["seconds"] = solution.Seconds,
            ["fps_num"] = fpsNumerator, ["fps_den"] = fpsDenominator,
            ["visible_period_threshold_seconds"] = SwayRecurrenceSolver.VisiblePeriodSeconds,
            // 生效门限：1080p 口径的常量按输出短边换算（求解时的倍率随解记下）。
            ["slow_speed_deviation_limit_pixels_per_second"] = SwayRecurrenceSolver.SlowSpeedDeviationLimit(solution.SpeedLimitScale),
            ["visible_speed_deviation_limit_pixels_per_second"] = SwayRecurrenceSolver.VisibleSpeedDeviationLimit(solution.SpeedLimitScale),
            ["minimum_visible_cycles"] = SwayRecurrenceSolver.MinimumVisibleCycles,
            ["minimum_loop_seconds"] = SwayRecurrenceSolver.MinimumLoopSeconds,
            // 档位与预算：档位给观感改动预算，循环长度是求解结果；预算为 null 表示质量档（不设门槛，取改动最小）。
            ["preset"] = profile?.Preset,
            ["retime_budget_percent"] = profile?.BudgetPercent,
            ["retime_budget_source"] = profile?.BudgetSource,
            ["max_change_visible_percent"] = solution.MaximumVisibleChangePercent,
            // 观感排序量：相位差（圈）。百分比只是求解参数。
            ["phase_drift_cycles"] = solution.MaximumPhaseDriftCycles,
            ["slowest_visible_cycles"] = solution.SlowestVisibleCycles,
            ["max_visible_speed_deviation_pixels_per_second"] = solution.MaximumVisibleSpeedDeviationPixelsPerSecond,
            ["max_slow_speed_deviation_pixels_per_second"] = solution.MaximumSlowSpeedDeviationPixelsPerSecond,
            ["frozen_count"] = solution.FrozenCount,
            // 以下为次要记录：慢项的百分比改动、全部项的最大改动与冻结项漂移。
            ["max_change_envelope_percent"] = solution.MaximumEnvelopeChangePercent,
            ["max_change_percent"] = solution.MaximumChangePercent,
            ["max_change_term"] = worst is { } item ? new JsonObject
            {
                ["owner_layer_id"] = item.Layer.Model.OwnerLayerId, ["term"] = item.Term.Name,
                ["delta_percent"] = item.Term.DeltaPercent, ["old_period_seconds"] = item.Term.OldPeriodSeconds,
                ["new_period_seconds"] = item.Term.NewPeriodSeconds
            } : null,
            ["frozen_minimum_period_seconds"] = solution.FrozenMinimumPeriodSeconds,
            ["frozen_drift_10min_max_pixels"] = solution.FrozenDrift10MinutesPixels,
            ["residual_pixels"] = 0,
            ["layers"] = new JsonArray(solution.Layers.Select(layer => (JsonNode)new JsonObject
            {
                ["owner_layer_id"] = layer.Model.OwnerLayerId, ["effect_index"] = layer.Model.EffectIndex,
                ["pass_index"] = layer.Model.PassIndex, ["shader"] = layer.Model.Shader, ["mode"] = layer.Model.Mode,
                ["speed_key"] = layer.Model.SpeedKey, ["speed"] = layer.Model.Speed,
                ["strength"] = layer.Model.Strength, ["ratio"] = layer.Model.Ratio, ["direction"] = layer.Model.Direction,
                ["power"] = layer.Model.Power, ["layer_width"] = layer.Model.LayerWidth, ["layer_height"] = layer.Model.LayerHeight,
                ["scale_x"] = layer.Model.ScaleX, ["scale_y"] = layer.Model.ScaleY,
                ["amplitude_x_pixels"] = layer.AmplitudeXPixels, ["amplitude_y_pixels"] = layer.AmplitudeYPixels,
                ["deltas_percent"] = new JsonArray(layer.Terms.Select(term => (JsonNode?)(term.Frozen ? null : JsonValue.Create(term.DeltaPercent))).ToArray()),
                ["frozen"] = new JsonArray(layer.Terms.Where(term => term.Frozen && term.CoefficientOld != 0)
                    .Select(term => (JsonNode)JsonValue.Create(term.Name)).ToArray()),
                ["terms"] = new JsonArray(layer.Terms.Select(term => (JsonNode)new JsonObject
                {
                    ["term"] = term.Name, ["coefficient_old"] = term.CoefficientOld, ["coefficient_new"] = term.CoefficientNew,
                    ["cycles_exact"] = term.CyclesExact, ["cycles"] = term.Cycles, ["frozen"] = term.Frozen,
                    ["delta_percent"] = term.Frozen ? null : term.DeltaPercent,
                    ["old_period_seconds"] = double.IsFinite(term.OldPeriodSeconds) ? term.OldPeriodSeconds : null,
                    ["new_period_seconds"] = term.NewPeriodSeconds, ["amplitude_pixels"] = term.AmplitudePixels,
                    ["speed_deviation_pixels_per_second"] = term.CoefficientOld == 0 ? null : term.SpeedDeviationPixelsPerSecond,
                    ["frozen_drift_10min_pixels"] = term.FrozenDrift10MinutesPixels,
                    ["frozen_drift_60min_pixels"] = term.FrozenDrift60MinutesPixels
                }).ToArray())
            }).ToArray()),
            ["shaders"] = shaders
        };
    }

    /// <summary>
    /// 一行说明里用的数：一个循环内最坏相位差（圈，三位小数）、最慢可见项走的圈数（算不出时为 null）、
    /// 可见摆动项最大改动百分比（两位小数）、慢项最大峰值速度偏差（三位小数像素/秒，算不出时为 null）、冻结项个数。
    /// 记录里没有这些字段（a7838d6、4b713f4 与规则 v2 的旧 plan）时按 layers[].terms 现算，口径与求解器相同。
    /// </summary>
    public static (string PhaseDriftCycles, string? SlowestVisibleCycles, string VisibleChangePercent,
        string? SlowSpeedDeviation, int FrozenCount) SummaryNumbers(JsonObject retime)
    {
        ArgumentNullException.ThrowIfNull(retime);
        var terms = (retime["layers"] as JsonArray)?.OfType<JsonObject>()
            .SelectMany(layer => (layer["terms"] as JsonArray)?.OfType<JsonObject>() ?? []).ToArray() ?? [];
        double visible = retime.ContainsKey("max_change_visible_percent") ? Number(retime["max_change_visible_percent"]) ?? 0
            : terms.Where(term => Number(term["delta_percent"]) is not null && Number(term["old_period_seconds"]) < SwayRecurrenceSolver.VisiblePeriodSeconds)
                .Select(term => Math.Abs(Number(term["delta_percent"])!.Value)).DefaultIfEmpty(0).Max();
        double? deviation = retime.ContainsKey("max_slow_speed_deviation_pixels_per_second")
            ? Number(retime["max_slow_speed_deviation_pixels_per_second"]) : SwayRetimeJson.RecordedSlowSpeedDeviation(retime);
        int frozen = retime["frozen_count"]?.GetValue<int>() ?? 0;
        // 相位差 |n − x| 与可见项圈数 n 都能从逐项记录现算，旧 plan 也讲得出这两个数。
        var moving = terms.Where(term => term["frozen"] is JsonValue flag && flag.TryGetValue(out bool frozenTerm) && !frozenTerm
            && Number(term["coefficient_old"]) != 0).ToArray();
        double drift = retime.ContainsKey("phase_drift_cycles") ? Number(retime["phase_drift_cycles"]) ?? 0
            : moving.Where(term => Number(term["cycles_exact"]) is not null && Number(term["cycles"]) is not null)
                .Select(term => Math.Abs(Number(term["cycles"])!.Value - Number(term["cycles_exact"])!.Value)).DefaultIfEmpty(0).Max();
        double? cycles = retime.ContainsKey("slowest_visible_cycles") ? Number(retime["slowest_visible_cycles"])
            : moving.Where(term => Number(term["old_period_seconds"]) is double period && period < SwayRecurrenceSolver.VisiblePeriodSeconds)
                .Select(term => Number(term["cycles"])).Where(value => value is not null).DefaultIfEmpty(null).Min();
        return (drift.ToString("0.000", CultureInfo.InvariantCulture), cycles?.ToString("0", CultureInfo.InvariantCulture),
            visible.ToString("0.00", CultureInfo.InvariantCulture), deviation?.ToString("0.000", CultureInfo.InvariantCulture), frozen);
    }

    /// <summary>
    /// 按记录里的逐项振幅与新旧周期算慢项最大峰值速度偏差：冻结 2πa/T，改频 2πa·|1/T′ − 1/T|。
    /// 用于旧版记录（没有逐项偏差字段），也可以拿来复核按旧 plan 烘的成品。有慢项算不出时返回 null。
    /// </summary>
    public static double? RecordedSlowSpeedDeviation(JsonObject retime)
    {
        ArgumentNullException.ThrowIfNull(retime);
        double worst = 0;
        foreach (JsonObject term in (retime["layers"] as JsonArray)?.OfType<JsonObject>()
            .SelectMany(layer => (layer["terms"] as JsonArray)?.OfType<JsonObject>() ?? []) ?? [])
        {
            if (Number(term["old_period_seconds"]) is not double period || period < SwayRecurrenceSolver.VisiblePeriodSeconds || Number(term["coefficient_old"]) == 0) continue;
            if (Number(term["amplitude_pixels"]) is not double amplitude) return null;
            bool frozen = term["frozen"] is JsonValue flag && flag.TryGetValue(out bool isFrozen) && isFrozen;
            if (frozen) worst = Math.Max(worst, 2 * Math.PI * amplitude / period);
            else if (Number(term["new_period_seconds"]) is double updated) worst = Math.Max(worst, 2 * Math.PI * amplitude * Math.Abs(1 / updated - 1 / period));
            else return null;
        }
        return worst;
    }

    private static JsonArray Numbers(IEnumerable<double> values) =>
        new(values.Select(value => (JsonNode)JsonValue.Create(value)).ToArray());

    /// <summary>读数值：内存里刚建的 JsonValue 可能是 long（圈数这类整数字段），从磁盘读回的是 JsonElement，两种都要认。</summary>
    private static double? Number(JsonNode? node) => node is not JsonValue value ? null
        : value.TryGetValue(out double number) ? number
        : value.TryGetValue(out long integer) ? integer : null;
}
