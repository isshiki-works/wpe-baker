using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Baker.Core;

/// <summary>Finds one source-side, analytically closed effect prefix per safe image owner.</summary>
internal static class EffectPrefixPlanner
{
    /// <param name="request">这次分析的完整请求：档位预算、圈数下限、可见项第二闸与质量档双上限都由它经
    /// <see cref="HybridScenePlanner.AnalyzeLoopForProfile"/> 生效，前缀路线与整层路线同一口径。</param>
    /// <param name="projection">投影记录，只用来换算摆动振幅的输出比例；bake 侧从 plan["projection"] 取同一份。</param>
    /// <param name="mayBeVisible">分析侧传 <see cref="Composer.MayBeVisible"/>：运行中不可能可见的层 WPE 不画，不提前缀、不探测
    /// （以前对当前天气下隐藏的层也探测，探测报错把整张拒掉）。bake 侧复核描述时不传，提案是分析侧的超集。</param>
    internal static JsonArray Propose(JsonObject originalScene, ProjectSource source, string assets, JsonObject runtime,
        JsonObject snapshotProperties, HybridAnalyzeRequest request, JsonObject projection, Func<int, bool>? mayBeVisible = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        RetimeProfile profile = RetimeProfileJson.Resolve(request);
        if (request.FpsNumerator == 0 || request.FpsDenominator == 0 || !double.IsFinite(profile.CommonRetimePercent) ||
            profile.CommonRetimePercent < 0 || profile.CommonRetimePercent > RetimeProfile.MaximumCommonRetimePercent)
            throw new ArgumentException("Use positive rational FPS and a retime limit from zero to ten percent.");
        var proposals = new JsonArray();
        foreach (JsonObject owner in originalScene["objects"]?.AsArray().OfType<JsonObject>() ?? [])
        {
            if (!EligibleOwner(owner, source, runtime, out bool retainedPuppetAnimation)) continue;
            int ownerId = owner["id"]!.GetValue<int>();
            if (mayBeVisible?.Invoke(ownerId) == false || !VisibilityControllersProven(originalScene, runtime, ownerId)) continue;
            JsonArray effects = owner["effects"]!.AsArray();
            var closed = new List<JsonObject>();
            // 同层各前缀共用一份"每个效果用到哪些材质 shader"的索引：在第一次分析前缀时建，后面的前缀只切片不重读。
            string[][]? effectShaders = null;
            for (int count = 1; count <= effects.Count; ++count)
            {
                if (effects[count - 1] is not JsonObject effect || !SafeEffect(effect, source, retainedPuppetAnimation)) break;
                if (effect["id"] is null) continue;
                effectShaders ??= EffectShaderIndex(originalScene, source, assets, ownerId);
                JsonObject loop = AnalyzeIndexedPrefix(originalScene, source, assets, runtime, snapshotProperties,
                    ownerId, count, request, projection, effectShaders);
                if (loop["unresolved"] is JsonArray { Count: > 0 } || loop["candidates"] is not JsonArray { Count: > 0 } candidates ||
                    candidates[0]?["components"] is not JsonArray { Count: > 0 }) continue;
                var cache = new JsonObject {
                    ["owner_layer_id"] = ownerId, ["prefix_effect_count"] = count,
                    ["terminal_effect_id"] = effect["id"]!.DeepClone(), ["source_image"] = owner["image"]!.DeepClone(),
                    ["loop"] = loop, ["fixed_user_properties"] = PrefixProperties(effects.Take(count), snapshotProperties),
                    // 这个前缀循环是在哪一档下求出来的：与 plan 顶层的 retime_profile 同一份值，
                    // 单看一条缓存记录就能知道 preset 与 retime_budget_percent；phase_drift_cycles 在 loop 的 sway_retime 里，
                    // 质量档两个上限的取舍在 loop 的 quality_ceiling_used 里。
                    ["retime_profile"] = RetimeProfileJson.ToJson(profile, SwayRecurrenceSolver.SpeedLimitScale(request.Width, request.Height)),
                    ["retained_puppet_animation"] = retainedPuppetAnimation,
                    ["prefix_capture_scope"] = retainedPuppetAnimation ? "pre_puppet_authored_effect_terminal" : "flat_authored_effect_terminal"
                };
                if ((runtime["runtime_dependencies"] as JsonArray ?? []).OfType<JsonObject>().Any(dependency =>
                    dependency["initialization"]?.GetValue<bool>() != true &&
                    IsExternalVisibilityDependency(dependency, ownerId) && dependency["operation"]?.GetValue<string>() == "write"))
                    cache["preserve_external_visibility"] = true;
                closed.Add(cache);
            }
            // 同一层的可闭合前缀按长到短全部提出来：最长的那个仍是首选，但它的终端捕获点可能落在共用缓冲上
            // （调用方探测后拒绝），那时短一级的前缀还能用，整层不必因此退回实时。
            for (int index = closed.Count - 1; index >= 0; --index) proposals.Add(closed[index]);
        }
        return proposals;
    }

    /// <summary>
    /// 一个前缀的循环分析。走 <see cref="HybridScenePlanner.AnalyzeLoopForProfile"/> 这一个入口，
    /// 与 analyze 的整层路线、bake 前刷新同一口径：档位预算、圈数下限、可见项第二闸与质量档双上限取优都在这里生效。
    /// 缓存尺寸在规划时还不知道，videoGroups 传 null，长度上限按不透明整幅输出估算（与 <see cref="HybridScenePlanner.LoopLengthMaximumOf"/> 的约定一致）。
    /// </summary>
    /// <param name="effectShaders">该层每个效果（按 effects 里的对象顺序）用到的材质 shader；null 时当场建（bake 侧单个前缀复算）。</param>
    internal static JsonObject AnalyzeIndexedPrefix(JsonObject originalScene, ProjectSource source, string assets, JsonObject runtime,
        JsonObject snapshot, int ownerId, int prefixCount, HybridAnalyzeRequest request, JsonObject projection,
        IReadOnlyList<string[]>? effectShaders)
    {
        ArgumentNullException.ThrowIfNull(request);
        // 证据要跟着分析范围走：被截掉的效果不在这次前缀里，它们的运行时材质也就没有对应的方程裁定，
        // 留在证据里只会以「未建模时钟」的名义反过来否掉这个前缀。留在前缀里的同名 shader 不受影响。
        JsonObject authoredOwner = originalScene["objects"]!.AsArray().OfType<JsonObject>()
            .Single(item => item["id"]!.GetValue<int>() == ownerId);
        effectShaders ??= EffectShaderIndex(originalScene, source, assets, ownerId);
        var kept = new HashSet<string>(effectShaders.Take(prefixCount).SelectMany(shaders => shaders), StringComparer.Ordinal);
        var dropped = new HashSet<string>(effectShaders.Skip(prefixCount).SelectMany(shaders => shaders), StringComparer.Ordinal);
        dropped.ExceptWith(kept);
        JsonObject analysisRuntime = runtime.DeepClone().AsObject();
        RemoveDroppedEffectMaterials(analysisRuntime, ownerId, dropped);
        // These operations control whether the retained owner is shown, not the
        // independently captured pixels. Keep the original trace for safety checks.
        if (analysisRuntime["runtime_dependencies"] is JsonArray dependencies)
            foreach (JsonNode? dependency in dependencies.ToArray())
                if (dependency is JsonObject value && IsExternalVisibilityDependency(value, ownerId))
                    dependencies.Remove(dependency);
        if (authoredOwner["animationlayers"] is JsonArray && analysisRuntime["runtime_animation_periods"] is JsonArray periods)
            foreach (JsonNode? period in periods.ToArray())
                if (period is JsonObject value && SceneGraph.Int(value["source_owner_layer_id"]) == ownerId &&
                    value["mechanism"] is JsonValue mechanism && mechanism.TryGetValue<string>(out string? kind) &&
                    kind == "puppet_bone") periods.Remove(period);
        // 质量档要在两个上限下各求一次，求解会往场景副本上写：每次求解都重新裁一份前缀场景，不共用。
        JsonObject PrefixScene()
        {
            JsonObject prefixScene = originalScene.DeepClone().AsObject();
            PlanTransforms.FreezeTemporalProperties(prefixScene, snapshot);
            JsonObject owner = prefixScene["objects"]!.AsArray().OfType<JsonObject>()
                .Single(item => item["id"]!.GetValue<int>() == ownerId);
            JsonArray prefixEffects = owner["effects"]!.AsArray();
            while (prefixEffects.Count > prefixCount) prefixEffects.RemoveAt(prefixEffects.Count - 1);
            if (owner["animationlayers"] is JsonArray) owner.Remove("animationlayers");
            return prefixScene;
        }
        // 前缀循环只进缓存描述（bake 侧逐字节比对），不出 unresolved_localized。
        return HybridScenePlanner.AnalyzeLoopForProfile(PrefixScene, source, assets, analysisRuntime, [ownerId],
            request, projection, videoGroups: null);
    }

    /// <summary>该层 effects 里每个对象效果用到的材质 shader（与 <see cref="ShaderPeriodAnalysis.EffectMaterialShaders"/> 同序）。</summary>
    private static string[][] EffectShaderIndex(JsonObject originalScene, ProjectSource source, string assets, int ownerId) =>
        [.. originalScene["objects"]!.AsArray().OfType<JsonObject>().Single(item => item["id"]!.GetValue<int>() == ownerId)["effects"]!
            .AsArray().OfType<JsonObject>().Select(effect => ShaderPeriodAnalysis.EffectMaterialShaders(source, assets, effect).ToArray())];

    /// <summary>把该层运行时材质里属于被截掉效果的条目去掉，其余（作者材质与前缀内效果）原样保留。</summary>
    private static void RemoveDroppedEffectMaterials(JsonObject runtime, int ownerId, IReadOnlySet<string> droppedShaders)
    {
        if (droppedShaders.Count == 0 || runtime["runtime_layers"] is not JsonArray layers) return;
        foreach (JsonObject layer in layers.OfType<JsonObject>())
        {
            if (SceneGraph.Int(layer["owner"]) != ownerId || layer["materials"] is not JsonArray materials) continue;
            foreach (JsonNode? node in materials.ToArray())
                if (node is JsonObject material && material["shader"] is JsonValue shader &&
                    shader.TryGetValue(out string? name) && name is not null && droppedShaders.Contains(name))
                    materials.Remove(node);
        }
    }

    private static bool EligibleOwner(JsonObject owner, ProjectSource source, JsonObject runtime, out bool retainedPuppetAnimation)
    {
        retainedPuppetAnimation = false;
        if (owner["id"] is not JsonValue id || !id.TryGetValue<int>(out int ownerId) || owner["parent"] is not null ||
            owner["image"] is not JsonValue image || !image.TryGetValue<string>(out string? imageResource) ||
            string.IsNullOrWhiteSpace(imageResource) || owner["effects"] is not JsonArray { Count: > 0 } ||
            HasUnsafeOwnerDynamic(owner) || owner.ContainsKey("particle") || owner.ContainsKey("puppet") ||
            HasRuntimeInput(runtime, ownerId) || !source.Contains(imageResource)) return false;
        JsonObject model;
        try { model = source.ReadJson(imageResource); }
        catch (Exception error) when (error is IOException or InvalidDataException) { return false; }
        if (HasDynamic(model) || model["material"]?.GetValue<string>() is not string materialResource || !source.Contains(materialResource)) return false;
        if (owner["animationlayers"] is JsonArray && !ValidPuppetResource(model, source)) return false;
        retainedPuppetAnimation = owner["animationlayers"] is JsonArray;
        JsonObject material;
        try { material = source.ReadJson(materialResource); }
        catch (Exception error) when (error is IOException or InvalidDataException) { return false; }
        return !HasDynamic(material) && material["passes"] is JsonArray { Count: 1 } passes && passes[0] is JsonObject pass &&
            (pass["shader"]?.GetValue<string>() is "genericimage3" or "genericimage4") && pass["textures"] is JsonArray { Count: 1 } textures &&
            textures[0]?.GetValue<string>() is string texture && !texture.StartsWith("_rt_", StringComparison.Ordinal);
    }

    private static bool SafeEffect(JsonObject effect, ProjectSource source, bool prePuppet)
    {
        if (HasDynamic(effect) || effect["visible"] is JsonValue visible && visible.TryGetValue<bool>(out bool shown) && !shown ||
            effect["file"]?.GetValue<string>() is not string file || !source.Contains(file)) return false;
        try
        {
            JsonObject definition = source.ReadJson(file);
            if (HasDynamic(definition) || definition["passes"] is not JsonArray passes) return false;
            foreach (JsonObject pass in passes.OfType<JsonObject>())
            {
                if (pass["material"]?.GetValue<string>() is not string materialFile || !source.Contains(materialFile)) return false;
                JsonObject material = source.ReadJson(materialFile);
                if (HasDynamic(material) || prePuppet && !NoPuppet(material) || material["passes"] is not JsonArray materialPasses || materialPasses.OfType<JsonObject>()
                    .SelectMany(item => item["textures"]?.AsArray().OfType<JsonValue>() ?? [])
                    .Any(texture => texture.TryGetValue<string>(out string? name) && name.StartsWith("_rt_", StringComparison.Ordinal))) return false;
            }
            return true;
        }
        catch (Exception error) when (error is IOException or InvalidDataException) { return false; }
    }

    private static bool ValidPuppetResource(JsonObject model, ProjectSource source)
    {
        if (model["puppet"]?.GetValue<string>() is not string puppet || string.IsNullOrWhiteSpace(puppet) || !source.Contains(puppet)) return false;
        try { return source.ReadPrefix(puppet, 1).Length == 1; }
        catch (Exception error) when (error is IOException or InvalidDataException) { return false; }
    }

    private static bool NoPuppet(JsonObject material) => SceneAnalyzer.Walk(material).OfType<JsonObject>().All(node =>
        node["use_puppet"] is null || node["use_puppet"] is JsonValue value && value.TryGetValue<bool>(out bool usePuppet) && !usePuppet);

    private static bool HasUnsafeOwnerDynamic(JsonObject owner)
    {
        if (owner["animationlayers"] is JsonArray layers && layers.Any(node => node is not JsonObject layer ||
            layer["animation"] is not JsonValue animation || !animation.TryGetValue<int>(out _) ||
            layer.ContainsKey("script") || layer.ContainsKey("animations") ||
            SceneAnalyzer.Walk(layer).OfType<JsonObject>().Any(value => !ReferenceEquals(value, layer) &&
                (value.ContainsKey("script") || value.ContainsKey("animation") || value.ContainsKey("animations"))))) return true;
        JsonObject withoutPuppetLayers = owner.DeepClone().AsObject();
        withoutPuppetLayers.Remove("animationlayers");
        return HasDynamic(withoutPuppetLayers);
    }

    private static bool IsExternalVisibilityDependency(JsonObject dependency, int ownerId) =>
        SceneGraph.Int(dependency["owner"]) is int caller && caller >= 0 && caller != ownerId &&
        SceneGraph.Int(dependency["target"]) == ownerId &&
        (dependency["operation"]?.GetValue<string>() == "lookup" ||
         dependency["property"]?.GetValue<string>() == "visible" &&
         dependency["operation"]?.GetValue<string>() is "read" or "write");

    private static bool VisibilityControllersProven(JsonObject scene, JsonObject runtime, int ownerId)
    {
        // An observed lookup/visible write is not proof that a later callback cannot
        // change pixels. Only admit a small, inspectable script subset; unknown
        // syntax, computed members and other host APIs retain the existing guard.
        var callers = (runtime["runtime_dependencies"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(d => IsExternalVisibilityDependency(d, ownerId))
            .Select(d => SceneGraph.Int(d["owner"])!.Value).Distinct();
        foreach (int caller in callers)
        {
            var controller = (scene["objects"] as JsonArray ?? []).OfType<JsonObject>()
                .FirstOrDefault(node => SceneGraph.Int(node["id"]) == caller);
            if (controller is null) return false;
            string[] scripts = SceneAnalyzer.Walk(controller).OfType<JsonObject>()
                .Select(node => node["script"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null)
                .OfType<string>().ToArray();
            if (scripts.Length == 0 || scripts.Any(script => !VisibilityScriptProven(script))) return false;
        }
        return true;
    }

    private static bool VisibilityScriptProven(string script)
    {
        string code = Regex.Replace(script, @"//[^\r\n\u2028\u2029]*|/\*[\s\S]*?\*/|""(?:\\.|[^""\\])*""|'(?:\\.|[^'\\])*'", " ");
        if (Regex.IsMatch(code, @"[^A-Za-z0-9_$\s.,;:(){}+\-*=!<>?%&|^~]") ||
            Regex.IsMatch(code, @"\b(?:eval|Function|Proxy|Reflect|globalThis|window|import|with|delete|require|Object)\b")) return false;
        var members = new HashSet<string>(StringComparer.Ordinal) { "visible", "getLayer", "frametime", "runtime",
            "getHours", "getMinutes", "getSeconds", "now", "sin", "cos", "random", "floor", "ceil", "round", "min", "max", "abs", "PI" };
        if (Regex.Matches(code, @"\.\s*([A-Za-z_$][A-Za-z0-9_$]*)").Any(match => !members.Contains(match.Groups[1].Value))) return false;
        var calls = Regex.Matches(code, @"\bfunction\s+([A-Za-z_$][A-Za-z0-9_$]*)\s*\(")
            .Select(match => match.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        calls.UnionWith(["if", "while", "for", "switch", "catch", "return", "function", "Date", "Number", "Boolean", "Error", "setTimeout", "setInterval", "clearTimeout", "clearInterval"]);
        return Regex.Matches(code, @"(?<![A-Za-z0-9_$.])([A-Za-z_$][A-Za-z0-9_$]*)\s*\(")
            .All(match => calls.Contains(match.Groups[1].Value));
    }

    private static bool HasRuntimeInput(JsonObject runtime, int ownerId)
    {
        if (runtime["runtime_dependencies"] is JsonArray dependencies && dependencies.OfType<JsonObject>().Any(dependency =>
            dependency["initialization"]?.GetValue<bool>() != true &&
            (SceneGraph.Int(dependency["owner"]) == ownerId || SceneGraph.Int(dependency["target"]) == ownerId) &&
            !IsExternalVisibilityDependency(dependency, ownerId))) return true;
        return runtime["runtime_layers"] is JsonArray layers && layers.OfType<JsonObject>().Where(layer => SceneGraph.Int(layer["owner"]) == ownerId)
            .SelectMany(layer => layer["materials"]?.AsArray().OfType<JsonObject>() ?? []).Any(material =>
                material["uses_audio_spectrum"]?.GetValue<bool>() == true || material["uses_system_media_thumbnail"]?.GetValue<bool>() == true ||
                material["active_uniforms"]?.AsArray().Any(uniform => uniform?.GetValue<string>() is "g_PointerPosition" or "g_PointerPositionLast" or "g_ParallaxPosition") == true);
    }

    private static JsonObject PrefixProperties(IEnumerable<JsonNode?> effects, JsonObject snapshot)
    {
        var result = new JsonObject();
        foreach (JsonObject node in effects.SelectMany(effect => SceneAnalyzer.Walk(effect).OfType<JsonObject>()))
            if ((node["user"] is JsonObject binding ? binding["name"] : node["user"]) is JsonValue user &&
                user.TryGetValue<string>(out string? key) && snapshot[key] is JsonNode value)
                result[key] = value.DeepClone();
        return result;
    }

    private static bool HasDynamic(JsonNode node) => SceneAnalyzer.Walk(node).OfType<JsonObject>()
        .Any(value => value.ContainsKey("script") || value.ContainsKey("animation") || value.ContainsKey("animations"));
}
