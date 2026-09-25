using System.Text.Json;
using System.Text.Json.Nodes;

namespace Baker.Core;

internal static class HybridLoopAllocation
{
    // Called only after loop search fails; the caller owns the single retry and all admission gates.
    internal static JsonObject? Propose(JsonObject plan, JsonObject scene) =>
        Explain(plan, scene) is JsonObject result && result["status"]?.GetValue<string>() == "proposed" ? result : null;

    private static JsonObject NotApplicable(string key) =>
        new Message(key).Write(new JsonObject { ["schema_version"] = 1, ["status"] = "not_applicable" }, "reason");

    /// <summary>
    /// 与 <see cref="Propose"/> 同一判定，但提议不成立时也给出原因，
    /// 让 analyze 能把"为什么没走这条回退"写进 plan，而不是静默跳过。
    /// </summary>
    internal static JsonObject Explain(JsonObject plan, JsonObject scene)
    {
        var baked = (plan["video_groups"] as JsonArray ?? []).OfType<JsonObject>()
            .SelectMany(group => ReadIds(group["layer_ids"])).ToHashSet();
        if (baked.Count == 0) return NotApplicable("reason.allocation_nothing_baked");
        var objects = (scene["objects"]?.AsArray()
            ?? throw new InvalidDataException("Loop allocation requires source objects."))
            .OfType<JsonObject>().ToDictionary(SceneGraph.Id);
        if (baked.Any(id => !objects.ContainsKey(id)))
            throw new InvalidDataException("A baked loop layer is absent from the source scene.");

        int AuthorRoot(int id)
        {
            var seen = new HashSet<int>();
            while (SceneGraph.Int(objects[id]["parent"]) is int parent && objects.ContainsKey(parent))
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
                if (SceneGraph.Int(unresolved["owner_layer_id"] ?? unresolved["source_owner_layer_id"]) is not int owner ||
                    !baked.Contains(owner)) continue;
                if (unresolved["particle_stationarity"] is JsonObject verdict && objects[owner].ContainsKey("particle"))
                    particleStationary[owner] = particleStationary.GetValueOrDefault(owner, true) &&
                        verdict["stationary"] is JsonValue flag && flag.TryGetValue(out bool stationary) && stationary;
                else triggers.Add(owner);
            }
        triggers.UnionWith(baked.Where(id => objects[id].ContainsKey("particle") && !particleStationary.GetValueOrDefault(id)));
        // 各分量有周期证明却凑不出上限内公共循环时，循环分析点名的并不进的所有者层同样留实时（LoopAnalysis.NoCommonLoopOwners）。
        triggers.UnionWith(ReadIds(plan["loop"]?["no_candidate_reason"]?["retain_live_owner_layer_ids"]).Where(baked.Contains));

        var added = triggers.Select(id => rootOf[id]).Where(root => !retained.Contains(root)).ToHashSet();
        if (added.Count == 0) return NotApplicable("reason.allocation_no_trigger");
        retained.UnionWith(added);
        // 加载即播、一次淡到全透明的图层（alpha 单次轨末帧值 0）入场后就看不见；视频从入场结束后录
        // （SingleShotAllocation.IntroSeconds），它不算剩下的可烘内容。
        static bool FadesOutForGood(JsonObject obj) => obj["alpha"] is JsonObject alpha && alpha["animation"] is JsonObject track &&
            track["options"] is JsonObject options && options["mode"] is JsonValue mode && mode.TryGetValue(out string? text) && text == "single" &&
            (track["c0"] as JsonArray ?? []).OfType<JsonObject>().MaxBy(key => SceneGraph.Numeric(key["frame"], 0)) is JsonObject last &&
            SceneGraph.Numeric(last["value"], 1) == 0;
        int[] remaining = objects.Keys.Where(id => baked.Contains(id) && !retained.Contains(rootOf[id]) && !FadesOutForGood(objects[id])).ToArray();
        if (remaining.Length == 0) return NotApplicable("reason.allocation_nothing_left");

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
        // 与分析、bake 同一个准入判定；说明性条目以外没有未解析项时不走残差掩盖。
        AdmissionVerdict verdict = Admission.Evaluate(replanned, sourceScene, readResource);
        if (verdict.Residual?["status"]?.GetValue<string>() == "no_residual") return (false, "unavailable", null);
        bool admitted = verdict.Rejection == AdmissionRejection.None;
        return (admitted, admitted ? "residual_maskable" : "unavailable", verdict.Residual);
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

    /// <summary>
    /// 触发留实时的原因码，供重查写进层的 reasons：<paramref name="owners"/> 各层在 plan 循环分析里的未解析项
    /// （每条取 particle_nonperiodic_reason / mechanism / key / kind 中第一个有的），被"凑不出公共循环"点名的记 no_candidate_reason.kind，
    /// 再并上 plan 设置里已带的（上一轮留实时的原因）。都没有时 null。
    /// </summary>
    internal static Dictionary<int, string[]>? RetainReasons(JsonObject plan, IEnumerable<int> owners)
    {
        var wanted = owners.ToHashSet();
        var reasons = plan["settings"]?["retain_live_reasons"]?.Deserialize<Dictionary<int, string[]>>()
            ?.ToDictionary(pair => pair.Key, pair => pair.Value.ToList()) ?? [];
        void Add(int id, JsonNode? code)
        {
            if (!wanted.Contains(id) || code is not JsonValue value || !value.TryGetValue(out string? text)) return;
            var list = reasons.TryGetValue(id, out var found) ? found : reasons[id] = [];
            if (!list.Contains(text)) list.Add(text);
        }
        foreach (var report in SceneAnalyzer.Walk(plan["loop"]).OfType<JsonObject>())
            foreach (var item in (report["unresolved"] as JsonArray ?? []).OfType<JsonObject>())
                if (SceneGraph.Int(item["owner_layer_id"] ?? item["source_owner_layer_id"]) is int owner)
                    Add(owner, item["particle_nonperiodic_reason"] ?? item["mechanism"] ?? item["key"] ?? item["kind"]);
        foreach (int owner in ReadIds(plan["loop"]?["no_candidate_reason"]?["retain_live_owner_layer_ids"]))
            Add(owner, plan["loop"]!["no_candidate_reason"]!["kind"]);
        return reasons.Count == 0 ? null : reasons.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());
    }

    private static IEnumerable<int> ReadIds(JsonNode? node) => (node?.AsArray() ?? []).Select(value =>
        SceneGraph.Int(value) ?? throw new InvalidDataException("A loop allocation layer id is invalid."));
}
