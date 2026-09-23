using System.Globalization;
using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>摆动改频（默认关）：把可建模的 uv_sway 未解析项变成改频后精确闭合的周期分量。从 HybridLoopService 原样搬出（C2.2c）。</summary>
internal static class SwayRetimeApplier
{
    /// <summary>
    /// 对每个候选 P 求 L = kP ≤ Lmax 上改动最小的逐项改频方案，没有解的候选丢掉；至少一个候选有解时，把这些摆动项从 unresolved 里移出，
    /// 候选帧数改成 L、分量圈数乘 k，并挂上 sway_retime 解（bake 按它写覆盖 shader）。全都没有解时候选与未解析项原样保留，只记原因。
    /// 除摆动之外没有任何时间机制时（求解器没有候选），以 1 帧为 P；求解器保证不会因此产出 1 帧静止候选（见 SwayRecurrenceSolver 护栏）。
    /// 返回 loop.sway_retime 记录。
    /// </summary>
    internal static JsonObject Apply(IReadOnlyList<ShaderTemporalUnresolved> shaderUnresolved, List<LoopUnresolved> unresolved,
        List<LoopCandidate> candidates, bool noOtherTemporalMechanism, uint fpsNumerator, uint fpsDenominator, SwayRetimeOptions options,
        float[][] spriteTables)
    {
        var record = new JsonObject { ["enabled"] = true, ["loop_length_maximum_seconds"] = options.LoopLengthMaximumSeconds,
            ["output_per_scene_x"] = options.OutputPerSceneX, ["output_per_scene_y"] = options.OutputPerSceneY,
            // 档位与预算：观感改动预算是求解参数，循环长度是求解结果。
            ["preset"] = options.Profile?.Preset, ["retime_budget_percent"] = options.BudgetPercent,
            ["retime_budget_source"] = options.Profile?.BudgetSource };
        // 循环长度上限按内嵌视频 2 GiB 收紧的记录；loop_length_maximum_seconds 已是收紧后的生效值。
        if (options.VideoLimit is EmbeddedVideoLoopLimit videoLimit) record["embedded_video_limit"] = videoLimit.ToJson();
        int[] swayIndexes = shaderUnresolved.Select((item, index) => (item, index))
            .Where(pair => pair.item.Mechanism == ShaderPeriodAnalysis.FoliageSwayMechanism).Select(pair => pair.index).ToArray();
        int[] modeled = swayIndexes.Where(index => shaderUnresolved[index].Sway is not null).ToArray();
        record["sway_items"] = swayIndexes.Length;
        record["modeled_items"] = modeled.Length;
        if (modeled.Length == 0)
        {
            record["status"] = swayIndexes.Length == 0 ? "no_sway_component" : "sway_not_modeled";
            return record;
        }
        SwayRetimeInput[] inputs = modeled.Select(index =>
        {
            SwayModel model = shaderUnresolved[index].Sway!;
            var amplitude = model.AmplitudeOutputPixels(options.OutputPerSceneX, options.OutputPerSceneY);
            return new SwayRetimeInput(model, amplitude?.X, amplitude?.Y);
        }).ToArray();
        // 振幅换算不到输出像素（读不到图层尺寸）时慢项的速度偏差无从判定：候选与未解析项原样保留，写明原因。
        int[] unknownAmplitude = inputs.Where(input => input.AmplitudeXPixels is null || input.AmplitudeYPixels is null)
            .Select(input => input.Model.OwnerLayerId).Distinct().ToArray();
        if (unknownAmplitude.Length > 0)
        {
            string layers = string.Join(", ", unknownAmplitude.Select(id => id.ToString(CultureInfo.InvariantCulture)));
            record["status"] = "amplitude_unknown";
            record["amplitude_unknown_layer_ids"] = new JsonArray(unknownAmplitude.Select(id => (JsonNode)JsonValue.Create(id)).ToArray());
            record["reason_zh"] = MessageCatalog.Get("sway_retime.amplitude_unknown", MessageCatalog.Chinese, layers);
            record["reason_en"] = MessageCatalog.Get("sway_retime.amplitude_unknown", MessageCatalog.English, layers);
            return record;
        }
        bool synthesized = false;
        if (candidates.Count == 0 && noOtherTemporalMechanism && modeled.Length == swayIndexes.Length)
        {
            candidates.Add(new LoopCandidate(1UL, (double)fpsDenominator / fpsNumerator, 0d, [], []));
            synthesized = true;
        }
        record["base_candidate_count"] = candidates.Count;
        var solutions = new SwayRetimeSolution?[candidates.Count];
        var spriteSelections = new SpriteSeamPhase.Selection?[candidates.Count];
        int spriteRejected = 0, limitRejected = 0, budgetRejected = 0;
        for (int index = 0; index < candidates.Count; ++index)
        {
            ulong baseFrames = candidates[index].Frames;
            solutions[index] = SwayRecurrenceSolver.Solve(inputs, baseFrames, fpsNumerator, fpsDenominator,
                options.LoopLengthMaximumSeconds, options.BudgetPercent, speedLimitScale: options.SpeedLimitScale);
            // 上限内取得到 kP、却没有一个 L 合规（可见项走不满整圈或速度偏差超限、慢项速度偏差超限、圈数不够、只剩 1 帧静止）：
            // 记下来，别当成"上限内没有 kP"。预算档再分一层：不设预算时有解，就是预算太紧卡的，原因要讲成预算。
            if (solutions[index] is null && SwayRecurrenceSolver.MaximumMultiple(baseFrames, fpsNumerator, fpsDenominator, options.LoopLengthMaximumSeconds) > 0)
            {
                ++limitRejected;
                if (options.BudgetPercent is not null &&
                    SwayRecurrenceSolver.Solve(inputs, baseFrames, fpsNumerator, fpsDenominator, options.LoopLengthMaximumSeconds,
                        speedLimitScale: options.SpeedLimitScale) is not null)
                    ++budgetRejected;
            }
            // 精灵 float32 接缝按 P 判过；L = kP 上漂移累积 k 倍，要在 L 上重新逐帧判定（与候选生成同一判据）。
            if (solutions[index] is SwayRetimeSolution solved && spriteTables.Length > 0)
            {
                var selection = SpriteSeamPhase.Select(spriteTables, fpsNumerator, fpsDenominator, solved.Frames);
                if (selection.AtOrigin == SpriteSeamPhase.Verdict.Mismatch && selection.WarmupFrames is null) { solutions[index] = null; ++spriteRejected; }
                else spriteSelections[index] = selection;
            }
        }
        if (spriteRejected > 0) record["sprite_float32_rejected_candidate_count"] = spriteRejected;
        if (limitRejected > 0) record["speed_limit_rejected_candidate_count"] = limitRejected;
        if (budgetRejected > 0) record["budget_rejected_candidate_count"] = budgetRejected;
        if (solutions.All(solution => solution is null))
        {
            if (synthesized) candidates.Clear();
            string status = budgetRejected > 0 ? "no_multiple_within_budget"
                : limitRejected > 0 ? "no_multiple_meets_speed_limit"
                : candidates.Count == 0 ? "no_base_candidate" : "no_multiple_within_maximum";
            string maximum = options.LoopLengthMaximumSeconds.ToString("0.###", CultureInfo.InvariantCulture);
            string limit = status == "no_multiple_within_budget"
                ? (options.BudgetPercent ?? 0).ToString("0.###", CultureInfo.InvariantCulture)
                : SwayRecurrenceSolver.SlowSpeedDeviationLimit(options.SpeedLimitScale).ToString("0.###", CultureInfo.InvariantCulture);
            // 上限是被内嵌视频大小收紧的，原因后面补一句说明收紧依据，否则用户看到的秒数和自己给的对不上。
            string limitZh = options.VideoLimit is { Applied: true } appliedLimit ? appliedLimit.Sentence(MessageCatalog.Chinese) : "";
            string limitEn = options.VideoLimit is { Applied: true } appliedLimitEn ? " " + appliedLimitEn.Sentence(MessageCatalog.English) : "";
            record["status"] = status;
            record["reason_zh"] = MessageCatalog.Get("sway_retime." + status, MessageCatalog.Chinese, maximum, limit) + limitZh;
            record["reason_en"] = MessageCatalog.Get("sway_retime." + status, MessageCatalog.English, maximum, limit) + limitEn;
            return record;
        }
        for (int index = candidates.Count - 1; index >= 0; --index)
        {
            if (solutions[index] is not SwayRetimeSolution solution) { candidates.RemoveAt(index); continue; }
            LoopCandidate candidate = candidates[index];
            // 其余分量在 P 上已闭合，L = kP 上各自多走 k 倍圈数，调速倍率与补丁不变。精灵接缝换成 L 上的判定
            // （有精灵帧表时每个有解候选都在上面重判过；没有帧表时原候选也没有判定）。
            candidates[index] = candidate with {
                Frames = solution.Frames, Seconds = solution.Seconds,
                Components = [.. candidate.Components.Select(component => component with { Cycles = checked(component.Cycles * solution.Multiple) })],
                SpriteSeam = spriteSelections[index],
                SwayRetime = new CandidateSwayRetime(solution, options.LoopLengthMaximumSeconds, fpsNumerator, fpsDenominator, options.Profile) };
        }
        // shader 未解析项在 unresolved 里排在最前、与 shaderUnresolved 同序；从后往前移出已建模的摆动项。
        foreach (int index in modeled.OrderDescending())
        {
            if (unresolved[index] is not ShaderLoopUnresolved item || !ReferenceEquals(item.Source, shaderUnresolved[index]))
                throw new InvalidOperationException("Sway unresolved items are out of order.");
            unresolved.RemoveAt(index);
        }
        record["status"] = "applied";
        record["resolved_items"] = new JsonArray(modeled.Select(index => (JsonNode)new JsonObject {
            ["owner_layer_id"] = shaderUnresolved[index].OwnerLayerId, ["effect_index"] = shaderUnresolved[index].EffectIndex,
            ["pass_index"] = shaderUnresolved[index].PassIndex, ["resource"] = shaderUnresolved[index].Resource }).ToArray());
        record["dropped_candidate_count"] = solutions.Count(solution => solution is null);
        return record;
    }
}
