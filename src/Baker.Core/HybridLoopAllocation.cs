using System.Text.Json;
using System.Text.Json.Nodes;

namespace Baker.Core;

internal static class HybridLoopAllocation
{
    // Called only after loop search fails; the caller owns the single retry and all admission gates.
    internal static JsonObject? Propose(JsonObject plan, JsonObject scene) =>
        Explain(plan, scene) is JsonObject result && result["status"]?.GetValue<string>() == "proposed" ? result : null;

    private static JsonObject NotApplicable(string reason) =>
        new() { ["schema_version"] = 1, ["status"] = "not_applicable", ["reason"] = reason };

    /// <summary>
    /// 与 <see cref="Propose"/> 同一判定，但提议不成立时也给出原因，
    /// 让 analyze 能把"为什么没走这条回退"写进 plan，而不是静默跳过。
    /// </summary>
    internal static JsonObject Explain(JsonObject plan, JsonObject scene)
    {
        var baked = (plan["video_groups"] as JsonArray ?? []).OfType<JsonObject>()
            .SelectMany(group => ReadIds(group["layer_ids"])).ToHashSet();
        if (baked.Count == 0) return NotApplicable("No layer is allocated to video, so there is no smaller bake allocation left to try.");
        var objects = (scene["objects"]?.AsArray()
            ?? throw new InvalidDataException("Loop allocation requires source objects."))
            .OfType<JsonObject>().ToDictionary(HybridScenePlanner.Id);
        if (baked.Any(id => !objects.ContainsKey(id)))
            throw new InvalidDataException("A baked loop layer is absent from the source scene.");

        int AuthorRoot(int id)
        {
            var seen = new HashSet<int>();
            while (HybridScenePlanner.Int(objects[id]["parent"]) is int parent && objects.ContainsKey(parent))
            {
                if (!seen.Add(id)) throw new InvalidDataException("Scene parent cycle.");
                id = parent;
            }
            return id;
        }
        // RetainLiveRootIds accepts author roots, even when the current plan split allocation units.
        var rootOf = objects.Keys.ToDictionary(id => id, AuthorRoot);
        var retained = ReadIds(plan["settings"]?["retain_live_root_ids"]).ToHashSet();
        if (retained.Any(id => !rootOf.TryGetValue(id, out int root) || root != id))
            throw new InvalidDataException("A retained loop root is not a source author root.");

        // 触发器 = 没通过平稳随机判据的粒子层 + 其它未解析机制的所有者。判据结论由 HybridLoopService 写在
        // loop.unresolved[].particle_stationarity 里：通过判据的粒子层没有周期但可以淡化替换，留实时救不了循环，不再触发。
        // 同一层只要有一条判不通过、或拿不到判据字段（旧版 plan），就照旧当触发器（保守）。
        var particleStationary = new Dictionary<int, bool>();
        var triggers = new HashSet<int>();
        foreach (var report in SceneAnalyzer.Walk(plan["loop"]).OfType<JsonObject>())
            foreach (var unresolved in (report["unresolved"] as JsonArray ?? []).OfType<JsonObject>())
            {
                if (HybridScenePlanner.Int(unresolved["owner_layer_id"] ?? unresolved["source_owner_layer_id"]) is not int owner ||
                    !baked.Contains(owner)) continue;
                if (unresolved["particle_stationarity"] is JsonObject verdict && objects[owner].ContainsKey("particle"))
                    particleStationary[owner] = particleStationary.GetValueOrDefault(owner, true) &&
                        verdict["stationary"] is JsonValue flag && flag.TryGetValue(out bool stationary) && stationary;
                else triggers.Add(owner);
            }
        triggers.UnionWith(baked.Where(id => objects[id].ContainsKey("particle") && !particleStationary.GetValueOrDefault(id)));

        var added = triggers.Select(id => rootOf[id]).Where(root => !retained.Contains(root)).ToHashSet();
        if (added.Count == 0) return NotApplicable(
            "No baked layer is a particle system that fails the stationary-random criteria or owns an unresolved loop mechanism, so retaining whole author subtrees would keep the same allocation.");
        retained.UnionWith(added);
        int[] remaining = objects.Keys.Where(id => baked.Contains(id) && !retained.Contains(rootOf[id])).ToArray();
        if (remaining.Length == 0) return NotApplicable(
            "Retaining the author subtrees of every unresolved or particle layer leaves no bakeable content, so a smaller allocation cannot help.");

        return new JsonObject {
            ["schema_version"] = 1,
            ["status"] = "proposed",
            ["retain_live_root_ids"] = JsonSerializer.SerializeToNode(objects.Keys.Where(retained.Contains)),
            ["added_live_root_ids"] = JsonSerializer.SerializeToNode(objects.Keys.Where(added.Contains)),
            ["trigger_layer_ids"] = JsonSerializer.SerializeToNode(objects.Keys.Where(triggers.Contains)),
            ["remaining_baked_layer_ids"] = JsonSerializer.SerializeToNode(remaining),
            ["scope"] = "Retain whole author subtrees containing unresolved loop owners or selected particles that fail the stationary-random criteria; replan the remaining bake and rerun all loop, composition and cost checks."
        };
    }

    /// <summary>
    /// analyze 侧回退取证：按更小分配重新分析出的 plan 算不算"找到了循环"。整层可用、改走 effect_prefix 照旧算；
    /// 另外，整层路线下零 blocker、有解析候选、留下的未解析项（说明性条目除外）全部可由残差掩盖时也算——bake 会走残差掩盖路线，
    /// 只是 whole_layer 因为留着未解析项而写 unavailable（例如留实时 Sea 之后剩下的平稳随机雨）。
    /// 返回 (是否可用, 依据, 残差分类)；依据是 whole_layer_available / effect_prefix / residual_maskable / unavailable。
    /// </summary>
    internal static (bool Resolved, string Basis, JsonObject? Classification) ReplannedResolution(JsonObject replanned,
        JsonObject sourceScene, Func<string, JsonObject?> readResource)
    {
        ArgumentNullException.ThrowIfNull(replanned);
        if (replanned["whole_layer"]?["status"]?.GetValue<string>() == "available") return (true, "whole_layer_available", null);
        if (replanned["route"]?.GetValue<string>() == "effect_prefix") return (true, "effect_prefix", null);
        if (replanned["route"]?.GetValue<string>() != "whole_layer" || replanned["blockers"] is not JsonArray { Count: 0 } ||
            replanned["loop"]?["candidates"] is not JsonArray { Count: > 0 }) return (false, "unavailable", null);
        JsonNode[] mechanisms = (replanned["loop"]?["unresolved"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(item => item["kind"]?.GetValue<string>() != ResidualMasking.AllocationFallbackKind)
            .Select(item => item.DeepClone()).ToArray();
        if (mechanisms.Length == 0) return (false, "unavailable", null);
        // 与 ResidualMasking.LayoutGate 同一份最小副本：说明性条目不当成识别不了的机制。
        var probe = new JsonObject
        {
            ["settings"] = replanned["settings"]?.DeepClone(), ["canvas_width"] = replanned["canvas_width"]?.DeepClone(),
            ["canvas_height"] = replanned["canvas_height"]?.DeepClone(), ["layers"] = replanned["layers"]?.DeepClone(),
            ["video_groups"] = replanned["video_groups"]?.DeepClone(),
            ["loop"] = new JsonObject { ["unresolved"] = new JsonArray(mechanisms) }
        };
        JsonObject classification = ResidualMasking.Classify(probe, sourceScene, readResource);
        bool maskable = classification["status"]?.GetValue<string>() == "residual_maskable" &&
            ResidualMasking.LayoutAllowsMasking(probe, classification);
        return (maskable, maskable ? "residual_maskable" : "unavailable", classification);
    }

    /// <summary>
    /// 重查没找到循环、但唯一挡住它的是全幅布局冲突，且冲突本身给出了"再把哪些根保持实时就只剩一组不透明视频"时，
    /// 记下照着做该传的完整 --retain-live 列表（本次已保留的根在前）。只取证、不重查：这个分配有没有循环要等用户重新分析。
    /// 重查还有别的阻断、或冲突没有可执行的保留做法时返回 null。
    /// </summary>
    internal static JsonObject? ReplannedRetainLiveSuggestion(JsonObject replanned, IReadOnlyList<int> retained)
    {
        ArgumentNullException.ThrowIfNull(replanned);
        ArgumentNullException.ThrowIfNull(retained);
        if (replanned["blockers"] is not JsonArray { Count: 1 } blockers || blockers[0] is not JsonValue blocker ||
            !blocker.TryGetValue(out string? blockerText) ||
            replanned["whole_layer"]?["layout_conflict"] is not JsonValue conflict ||
            !conflict.TryGetValue(out string? conflictText) || blockerText != conflictText ||
            replanned["full_frame_retention"] is not JsonObject retention ||
            retention["status"]?.GetValue<string>() != "available") return null;
        // 用记录里换算好的源作者根（--retain-live 只收源根）；换算不成立（会连带退回别的视频单元）时记录为 null，不给建议。
        if (retention["retain_live_root_ids"] is not JsonArray command) return null;
        int[] added = ReadIds(command).Where(id => !retained.Contains(id)).Distinct().ToArray();
        if (added.Length == 0) return null;
        return new JsonObject
        {
            ["basis"] = "full_frame_retention",
            ["retain_live_root_ids"] = JsonSerializer.SerializeToNode(retained.Concat(added).ToArray()),
            ["added_live_root_ids"] = JsonSerializer.SerializeToNode(added),
            ["scope"] = "Taken from the re-analysis's own full-frame conflict options; this allocation has not been re-analyzed."
        };
    }

    private static IEnumerable<int> ReadIds(JsonNode? node) => (node?.AsArray() ?? []).Select(value =>
        HybridScenePlanner.Int(value) ?? throw new InvalidDataException("A loop allocation layer id is invalid."));
}
