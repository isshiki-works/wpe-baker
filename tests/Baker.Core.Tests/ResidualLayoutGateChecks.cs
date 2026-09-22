using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Baker.Core;

/// <summary>
/// 残差掩盖的布局前提（feat/particle-crossfade 改口径）：透明组与多组都能淡化——packed 图在 packed 域线性混合就是预乘空间的淡化，
/// 多组共享同一个起点。布局淡化不了的只剩"可掩盖分量所在的图层不在任何视频组里"，这时 analyze 写 blocker、bake 干净拒绝。
/// 全幅单组、分层多组、无未解析分量、布局本身已冲突、分量不可掩盖这几种形态都不写这条 blocker。
/// </summary>
internal static class ResidualLayoutGateChecks
{
    private const string RandomRestartScript = "'use strict';\nfunction randomNumber(min, max) { return Math.random() * (max - min) + min; }\n" +
        "export function update(value) { const ani = thisLayer.getTextureAnimation(); if (ani.getFrame() === ani.frameCount - 1) { " +
        "engine.setTimeout(() => { ani.stop(); engine.setTimeout(() => ani.play(), randomNumber(1, 5) * 1000); }, 100); } return value; }";

    internal static async Task RunAsync(Action<bool, string> check, string root)
    {
        var suitability = typeof(HybridScenePlanner).GetMethod("Suitability", BindingFlags.Static | BindingFlags.NonPublic)!;
        JsonObject Verdict(JsonObject plan) => (JsonObject)suitability.Invoke(null, [plan])!;

        // 可掩盖的随机重启精灵（层 3，作者根 30）+ analyze 追加的说明性条目。
        JsonObject RandomSprite() => new()
        {
            ["kind"] = "runtime_animation", ["owner_layer_id"] = 3, ["mechanism"] = "sprite", ["random_restart"] = true,
            ["track_name"] = "flip", ["detail"] = "Script playback restarts this sprite animation after a Math.random() delay."
        };
        JsonObject Informational() => new() { ["kind"] = ResidualMasking.AllocationFallbackKind, ["detail"] = "A smaller allocation note." };
        // groupLayers[i] 是第 i 组的 layer_ids；层 3 不在任何组里时就是"布局淡化不了"的形态。
        JsonObject Plan(string layout, bool[] groupClears, JsonArray unresolved, JsonObject? fallback = null, string? layoutConflict = null,
            int[][]? groupLayers = null)
        {
            var groups = new JsonArray(groupClears.Select((clear, index) => (JsonNode)new JsonObject
            {
                ["id"] = $"group-{index + 1}",
                ["layer_ids"] = new JsonArray([.. (groupLayers?[index] ?? [index == 0 ? 1 : 3]).Select(id => (JsonNode)JsonValue.Create(id))]),
                ["include_scene_clear"] = clear, ["transparent"] = !clear
            }).ToArray());
            var wholeLayer = new JsonObject { ["blockers"] = new JsonArray(), ["status"] = "unavailable" };
            if (layoutConflict is not null) wholeLayer["layout_conflict"] = layoutConflict;
            var plan = new JsonObject
            {
                ["route"] = "whole_layer", ["status"] = layoutConflict is null ? "requires_loop_analysis" : "requires_resolution",
                ["settings"] = new JsonObject { ["width"] = 1920, ["height"] = 1080, ["video_layout"] = layout, ["retain_live_root_ids"] = null },
                ["canvas_width"] = 1920, ["canvas_height"] = 1080,
                ["layers"] = new JsonArray(
                    new JsonObject { ["id"] = 1, ["root"] = 1, ["name"] = "background", ["canvas_fraction"] = 1.0 },
                    new JsonObject { ["id"] = 30, ["root"] = 30, ["name"] = "holder" },
                    new JsonObject { ["id"] = 3, ["root"] = 30, ["parent"] = 30, ["name"] = "sprite", ["canvas_fraction"] = 0.05 }),
                ["video_groups"] = groups,
                ["blockers"] = layoutConflict is null ? new JsonArray() : new JsonArray(layoutConflict),
                ["whole_layer"] = wholeLayer,
                ["loop"] = new JsonObject
                {
                    ["candidates"] = new JsonArray(new JsonObject { ["frames"] = 600 }), ["unresolved"] = unresolved
                }
            };
            if (fallback is not null) plan["loop_allocation_fallback"] = fallback;
            return plan;
        }
        var scene = new JsonObject { ["objects"] = new JsonArray(new JsonObject { ["id"] = 1 }, new JsonObject { ["id"] = 30 },
            new JsonObject { ["id"] = 3, ["parent"] = 30 }) };
        static JsonObject? NoResource(string _) => null;
        var candidateFound = new JsonObject { ["status"] = "candidate_found", ["retain_live_root_ids"] = new JsonArray(30) };

        // ---- layered 多组、残差层在透明组里 → 允许淡化，不写 blocker ----
        JsonObject layered = Plan("layered", [true, false], new JsonArray(RandomSprite(), Informational()), candidateFound.DeepClone().AsObject());
        string layeredBefore = layered.ToJsonString();
        JsonObject layeredClassification = ResidualMasking.Classify(layered, scene, NoResource);
        check(ResidualMasking.ApplyLayoutGate(layered, scene, NoResource) is null && layered.ToJsonString() == layeredBefore &&
            Verdict(layered)["verdict"]!.GetValue<string>() == "suitable",
            "layered groups whose residual layer sits in a transparent group need no layout blocker: packed crossfades are premultiplied crossfades");
        check(ResidualMasking.LayoutAllowsMasking(layered, layeredClassification) &&
            ResidualMasking.ResidualGroupIndexes(layered, layeredClassification).SequenceEqual([1]) &&
            ResidualMasking.UnplacedResidualOwners(layered, layeredClassification).Length == 0,
            "the residual group index points at the transparent group that contains the random sprite");
        JsonObject threeGroups = Plan("layered", [true, false, false], new JsonArray(RandomSprite()), groupLayers: [[1], [3], [3]]);
        check(ResidualMasking.ResidualGroupIndexes(threeGroups, ResidualMasking.Classify(threeGroups, scene, NoResource)).SequenceEqual([1, 2]) &&
            ResidualMasking.ApplyLayoutGate(threeGroups, scene, NoResource) is null,
            "every video group that contains a residual layer is listed, in plan order, and several transparent groups are allowed");

        // ---- full_frame 单个不透明组 + 需掩盖 → 不受影响 ----
        JsonObject fullFrame = Plan("full_frame", [true], new JsonArray(RandomSprite(), Informational()), candidateFound.DeepClone().AsObject(),
            groupLayers: [[1, 3]]);
        string fullFrameBefore = fullFrame.ToJsonString();
        check(ResidualMasking.LayoutAllowsMasking(fullFrame, ResidualMasking.Classify(fullFrame, scene, NoResource)) &&
            ResidualMasking.ApplyLayoutGate(fullFrame, scene, NoResource) is null &&
            fullFrame.ToJsonString() == fullFrameBefore && Verdict(fullFrame)["verdict"]!.GetValue<string>() == "suitable",
            "a single opaque full-frame group that needs residual masking is left untouched");

        // ---- 残差层不在任何视频组里 → 布局确实淡化不了，写 blocker ----
        JsonObject unplaced = Plan("layered", [true], new JsonArray(RandomSprite(), Informational()), candidateFound.DeepClone().AsObject(),
            groupLayers: [[1]]);
        JsonObject? gate = ResidualMasking.ApplyLayoutGate(unplaced, scene, NoResource);
        string blocker = gate?["reason"]?.GetValue<string>() ?? "";
        JsonObject localized = unplaced["blockers"]![0]!.AsObject();
        check(gate is not null && unplaced["blockers"] is JsonArray { Count: 1 } blockers && blockers[0]!["text"]!.GetValue<string>() == blocker &&
            PlanBlockers.Codes(unplaced["whole_layer"]!.AsObject()).Contains(BlockerCode.ResidualMaskingLayout) &&
            unplaced["status"]!.GetValue<string>() == "requires_resolution" &&
            unplaced["residual_layout_gate"]?["rule"]?.GetValue<string>() == "residual_masking_requires_residual_layers_in_video_groups",
            "a maskable residual layer that is in no video group gains an analyze blocker because no group can crossfade it");
        check(Verdict(unplaced)["verdict"]!.GetValue<string>() == "requires_user_choice",
            "the residual layout blocker turns suitability into a user choice");
        check(blocker.Contains("layer 3 \"sprite\" (random_sprite)", StringComparison.Ordinal) &&
            blocker.Contains("not in any video group", StringComparison.Ordinal) &&
            blocker.Contains("layered layout with 1 video group(s)", StringComparison.Ordinal) &&
            !blocker.Contains("--video-layout full_frame", StringComparison.Ordinal) &&
            blocker.Contains("--retain-live 30", StringComparison.Ordinal) &&
            blocker.Contains("already finds a loop", StringComparison.Ordinal) &&
            gate!["suggested_retain_live_root_ids"]!.AsArray().Select(node => node!.GetValue<int>()).SequenceEqual([30]) &&
            gate["retain_live_basis"]!.GetValue<string>() == "loop_allocation_fallback_candidate_found" &&
            gate["unresolved_components"]!.AsArray().Count == 1,
            "the blocker names only the unplaced component, the layout, and the re-analyzed --retain-live roots, without steering to full_frame");
        check(localized["key"]?.GetValue<string>() == "blocker.residual_masking_layout" &&
            localized["zh"]!.GetValue<string>().Contains("图层 3 \"sprite\"（random_sprite）", StringComparison.Ordinal) &&
            localized["zh"]!.GetValue<string>().Contains("不属于任何视频组", StringComparison.Ordinal) &&
            localized["zh"]!.GetValue<string>().Contains("--retain-live 30", StringComparison.Ordinal) &&
            !localized["zh"]!.GetValue<string>().Contains("layer 3", StringComparison.Ordinal),
            "the residual layout blocker localizes to chinese with chinese component and option wording");
        check(ResidualMasking.ApplyLayoutGate(unplaced, scene, NoResource) is not null && unplaced["blockers"]!.AsArray().Count == 1,
            "applying the residual layout gate twice does not duplicate the blocker");

        // 没有重查过的更小分配时，退回分量所在的作者根，并如实说明还没重新分析。
        JsonObject unverified = Plan("layered", [true], new JsonArray(RandomSprite()), groupLayers: [[1]]);
        JsonObject? unverifiedGate = ResidualMasking.ApplyLayoutGate(unverified, scene, NoResource);
        check(unverifiedGate?["retain_live_basis"]?.GetValue<string>() == "unresolved_owner_author_roots_not_reanalyzed" &&
            unverifiedGate["reason"]!.GetValue<string>().Contains("--retain-live 30 to keep", StringComparison.Ordinal) &&
            unverifiedGate["reason"]!.GetValue<string>().Contains("has not been re-analyzed", StringComparison.Ordinal) &&
            unverifiedGate["reason_zh"]!.GetValue<string>().Contains("还没有重新分析过", StringComparison.Ordinal),
            "without a re-analyzed allocation the blocker suggests the owners' author roots and says they are not re-analyzed");

        // ---- layered 多组 + 没有未解析时间机制 → 不受影响（说明性条目不算） ----
        foreach (JsonArray unresolved in new[] { new JsonArray(), new JsonArray(Informational()) })
        {
            JsonObject resolved = Plan("layered", [true, false], unresolved);
            string before = resolved.ToJsonString();
            check(ResidualMasking.ApplyLayoutGate(resolved, scene, NoResource) is null && resolved.ToJsonString() == before,
                $"layered groups without unresolved temporal mechanisms are left untouched ({unresolved.Count} informational item(s))");
        }

        // ---- 布局本身已冲突（全幅多组）→ 不再追加，保持原有那一条 blocker ----
        JsonObject conflicted = Plan("full_frame", [true, false], new JsonArray(RandomSprite()), layoutConflict: "Full-frame mode requires one opaque video group.",
            groupLayers: [[1], [2]]);
        string conflictedBefore = conflicted.ToJsonString();
        check(ResidualMasking.ApplyLayoutGate(conflicted, scene, NoResource) is null && conflicted.ToJsonString() == conflictedBefore,
            "a plan already blocked by its layout conflict keeps exactly that blocker");

        // ---- 分量本身不可掩盖 → 不是布局问题，不写这条 blocker ----
        JsonObject unmaskable = Plan("layered", [true], new JsonArray(new JsonObject
        {
            ["kind"] = "runtime_animation", ["owner_layer_id"] = 3, ["detail"] = "Runtime duration or source owner cannot be resolved exactly."
        }), groupLayers: [[1]]);
        check(ResidualMasking.ApplyLayoutGate(unmaskable, scene, NoResource) is null,
            "unmaskable residual components are not reported as a layout problem");

        await BakePassesLayoutAsync(check, root);
    }

    /// <summary>
    /// 以前"分层 + 透明组要靠掩盖"在 bake 侧被干净拒绝；现在透明组能淡化，同一份计划必须越过布局防御、进到合成校验。
    /// 工具路径是假的，合成探针一开渲染就失败——失败点证明它已经过了布局门，且没有写 candidate_rejected_residual_layout。
    /// </summary>
    private static async Task BakePassesLayoutAsync(Action<bool, string> check, string root)
    {
        string sourceDirectory = Path.Combine(root, "residual-layout-source");
        Directory.CreateDirectory(sourceDirectory);
        JsonObject Owner(int id, string? script) => new()
        {
            ["id"] = id, ["size"] = "100 100",
            ["visible"] = script is null ? (JsonNode)true : new JsonObject { ["value"] = true, ["script"] = script },
            ["animationlayers"] = new JsonArray(new JsonObject { ["id"] = 50, ["name"] = "sway", ["rate"] = 1 })
        };
        await File.WriteAllTextAsync(Path.Combine(sourceDirectory, "project.json"), "{\"type\":\"scene\",\"file\":\"scene.json\"}");
        await File.WriteAllTextAsync(Path.Combine(sourceDirectory, "scene.json"), new JsonObject
        {
            ["general"] = new JsonObject(), ["objects"] = new JsonArray(Owner(1, null), Owner(3, RandomRestartScript))
        }.ToJsonString());
        string runtimeEvidence = Path.Combine(root, "residual-layout-runtime.json");
        await File.WriteAllTextAsync(runtimeEvidence, new JsonObject
        {
            ["status"] = "complete", ["source_script_error_count"] = 0, ["source_script_errors"] = new JsonArray(),
            ["runtime_dependencies"] = new JsonArray(),
            ["runtime_animation_periods"] = new JsonArray(
                new JsonObject
                {
                    ["source_owner_layer_id"] = 1, ["mechanism"] = "puppet_bone", ["track_name"] = "sway", ["duration_seconds"] = 2,
                    ["looping"] = true, ["playback_mode"] = "loop", ["event_driven"] = false, ["confidence"] = "high", ["playback_rate"] = 1
                },
                new JsonObject
                {
                    ["source_owner_layer_id"] = 3, ["mechanism"] = "sprite", ["track_name"] = "flip", ["duration_seconds"] = 1.68,
                    ["looping"] = true, ["playback_mode"] = "loop", ["event_driven"] = false, ["confidence"] = "high"
                })
        }.ToJsonString());
        using var source = new ProjectSource(sourceDirectory);
        var settings = new HybridAnalyzeRequest(2, sourceDirectory, root, Path.Combine(root, "residual-layout-analysis"), 64, 32, 60, 1,
            VideoLayout: "layered");
        var plan = new JsonObject
        {
            ["schema_version"] = 2, ["kind"] = "hybrid_video", ["route"] = "whole_layer", ["source"] = sourceDirectory,
            ["source_sha256"] = await source.SourceHashAsync(),
            ["settings"] = JsonSerializer.SerializeToNode(settings, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }),
            // 保存的循环故意是空壳：bake 必须按源与运行时证据重新解析，得到"有候选 + 可掩盖的随机精灵"。
            ["loop"] = new JsonObject { ["status"] = "observed", ["candidates"] = new JsonArray(new JsonObject { ["frames"] = 600 }) },
            ["video_groups"] = new JsonArray(
                new JsonObject { ["id"] = "group-1", ["layer_ids"] = new JsonArray(1), ["include_scene_clear"] = true, ["transparent"] = false },
                new JsonObject { ["id"] = "group-2", ["layer_ids"] = new JsonArray(3), ["include_scene_clear"] = false, ["transparent"] = true }),
            ["layers"] = new JsonArray(new JsonObject { ["id"] = 1, ["root"] = 1, ["name"] = "background" },
                new JsonObject { ["id"] = 3, ["root"] = 3, ["name"] = "sprite" }),
            ["blockers"] = new JsonArray(), ["snapshot_properties"] = new JsonObject(), ["runtime_evidence"] = runtimeEvidence,
            ["source_script_error_evidence"] = new JsonObject { ["status"] = "available" },
            ["source_script_error_count"] = 0, ["source_script_errors"] = new JsonArray()
        };
        string output = Path.Combine(root, "residual-layout-bake");
        Exception? thrown = null;
        JsonObject? result = null;
        // KeepIntermediates：这条用例靠合成探针目录还在来证明计划走过了布局防御，所以关掉结束后的中间产物清理。
        try { result = await new HybridBakeService(new("not-started", "not-started", "not-started", []))
            .BakeAsync(new(2, plan, output, KeepIntermediates: true)); }
        catch (Exception error) { thrown = error; }
        JsonNode? saved = File.Exists(Path.Combine(output, "bake.json")) ? JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(output, "bake.json"))) : null;
        check(result?["status"]?.GetValue<string>() != ResidualMasking.LayoutRejectedStatus &&
            saved?["status"]?.GetValue<string>() != ResidualMasking.LayoutRejectedStatus &&
            Directory.Exists(output + ".composition-probe") && thrown is not null,
            "bake no longer rejects residual masking on a transparent group: the plan passes the layout defense and reaches the composition probe");
    }
}
