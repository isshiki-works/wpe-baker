using System.Globalization;
using System.Text.Json.Nodes;
using Baker.Core;

namespace Baker.App;

internal sealed record AppPropertyDefinition(string Key, JsonObject Definition);

/// <summary>Pure JSON projection used by the WPF layer and by file-backed wiring checks.</summary>
internal static class AppJsonPresentation
{
    public static HybridAnalyzeRequest ConfigureAnalysis(HybridAnalyzeRequest request, string preset, string interaction,
        bool custom, bool layoutExplicit) => request with
    {
        Preset = preset,
        LoopPreference = RetimeProfile.LoopPreferenceForPreset(preset),
        Interaction = interaction,
        ViewMode = interaction == "keep" ? "preserve" : "fixed_view",
        LiveOverlayPlacement = "foreground",
        CustomSettings = custom, LayoutExplicit = layoutExplicit
    };

    public static JsonObject? SuggestedSettings(JsonObject? plan)
    {
        if (plan?["suggested_change"]?["verified"]?.GetValue<bool>() != true ||
            plan["suggested_change"]?["settings"] is not JsonObject settings || settings.Count == 0) return null;
        foreach (var (key, value) in settings)
        {
            if (value is not JsonValue scalar || !scalar.TryGetValue<string>(out var text)) return null;
            if (key == "preset" ? !RetimeProfile.IsKnownPreset(text) : key != "interaction" || text is not ("keep" or "fixed" or "off")) return null;
        }
        return settings.DeepClone().AsObject();
    }

    public static string? CandidateProjectPath(JsonObject result) =>
        result["project_path"]?.GetValue<string>();

    public static bool CandidateCanApply(JsonObject result)
    {
        if (result["schema_version"]?.GetValue<int>() != 2 ||
            result["artifact_kind"]?.GetValue<string>() != "hybrid_video_candidate" ||
            !StaticOnlyBake.Finished(result["status"]?.GetValue<string>()) ||
            result["plan"] is not JsonObject plan)
            return false;
        // 残差掩盖的候选允许非零起点与非空 unresolved，但每一条都必须已被判定为可掩盖，
        // 并且接缝残差在第一层限内、固定窗口的交叉淡化已经应用（应用即已过权重核对与尾段 MD5，失败会直接抛出）。
        bool residualMasked = result["seam_policy"]?.GetValue<string>() == ResidualMasking.SeamPolicy;
        // 源周期路线的非零起点只能是起点偏移（第 0 帧孤立异常，顺延 1 帧，见 SourceStartOffset），并且记录要对得上。
        if (!residualMasked && Number(result["source_start_frame"]) != 0 && !SourceStartOffset.Allows(result)) return false;
        if (plan["route"]?.GetValue<string>() == "effect_prefix")
            return !residualMasked && EffectPrefixCandidateCanApply(result, plan);
        if (plan["loop"] is not JsonObject loop ||
            loop["status"]?.GetValue<string>() != "analytic_candidate_requires_seam_validation" ||
            loop["source_static"] is not JsonValue sourceStatic || !sourceStatic.TryGetValue<bool>(out _) ||
            loop["candidates"] is not JsonArray { Count: > 0 })
            return false;
        if (residualMasked)
        {
            if (result["residual_masking"]?["status"]?.GetValue<string>() != "residual_maskable" ||
                result["residual_masking"]?["blocking_components"] is not JsonArray { Count: 0 } ||
                result["seam_residual"]?["status"]?.GetValue<string>() != "observed_within_residual_limits" ||
                result["seam_residual"]?["first_layer"]?["passed"]?.GetValue<bool>() != true ||
                result["loop_crossfade"]?["status"]?.GetValue<string>() != "applied" ||
                Number(result["crossfade_frames"]) is not > 0 ||
                loop["unresolved"] is not JsonArray { Count: > 0 })
                return false;
            // 多组时每个含残差层的组都各自测第一层并淡化：组记录里带了读数的，淡化也必须已应用。
            if (result["groups"] is JsonArray residualGroups && residualGroups.OfType<JsonObject>().Any(group =>
                    group["seam_residual"] is JsonObject &&
                    (group["seam_residual"]?["first_layer"]?["passed"]?.GetValue<bool>() != true ||
                     group["loop_crossfade"]?["status"]?.GetValue<string>() != "applied")))
                return false;
        }
        else if (loop["unresolved"] is not JsonArray { Count: 0 }) return false;
        if (result["groups"] is JsonArray groups && groups.OfType<JsonObject>().Any(group => group["local_repair"] is not null))
            return false;
        return true;
    }

    private static bool EffectPrefixCandidateCanApply(JsonObject result, JsonObject plan)
    {
        if (result["seam_policy"]?.GetValue<string>() != "source_period_no_repair" ||
            plan["effect_prefix_caches"] is not JsonArray { Count: > 0 } caches ||
            result["groups"] is not JsonArray groups || groups.Count != caches.Count ||
            result["composition_validation"]?["status"]?.GetValue<string>() != "composition_pass")
            return false;
        foreach (JsonObject cache in caches.OfType<JsonObject>())
        {
            int? owner = cache["owner_layer_id"] is JsonValue ownerValue && ownerValue.TryGetValue<int>(out int id) ? id : null;
            if (owner is null || Number(cache["prefix_effect_count"]) is not > 0 ||
                Number(cache["terminal_effect_id"]) is null || cache["source_image"] is not JsonValue image ||
                !image.TryGetValue<string>(out _) || cache["fixed_user_properties"] is not JsonObject ||
                cache["loop"] is not JsonObject loop ||
                loop["status"]?.GetValue<string>() != "analytic_candidate_requires_seam_validation" ||
                loop["candidates"] is not JsonArray { Count: > 0 } ||
                loop["unresolved"] is not JsonArray { Count: 0 }) return false;
            JsonObject[] matches = groups.OfType<JsonObject>()
                .Where(group => Number(group["owner_layer_id"]) == owner).ToArray();
            if (matches.Length != 1) return false;
            JsonObject group = matches[0];
            JsonObject[] periods = group["period"] is JsonObject period ? [period] : [];
            JsonObject? groupCandidate = periods.FirstOrDefault()?["candidates"]?.AsArray().OfType<JsonObject>().FirstOrDefault();
            if (group["status"]?.GetValue<string>() != "encoded" ||
                Number(group["frames"]) is not > 0 || periods.Length != 1 ||
                periods[0]["status"]?.GetValue<string>() != "analytic_candidate_requires_seam_validation" ||
                periods[0]["candidates"] is not JsonArray { Count: > 0 } ||
                periods[0]["unresolved"] is not JsonArray { Count: 0 } ||
                Number(groupCandidate?["frames"]) != Number(group["frames"]) ||
                group["encoded_loop_validation"]?["status"]?.GetValue<string>() != "observed_seam_pass" ||
                group["hardware_decode"]?["all_adapters_passed"]?.GetValue<bool>() != true ||
                group["local_repair"] is not null) return false;
        }
        return true;
    }

    public static JsonObject LoadPropertyDefinitions(string sourcePath)
    {
        using var source = new ProjectSource(sourcePath);
        return LoadPropertyDefinitions(source);
    }

    public static JsonObject LoadPropertyDefinitions(ProjectSource source)
    {
        if (!source.Contains("project.json")) return new JsonObject();
        JsonObject project = source.ReadJson("project.json");
        return project["general"]?["properties"] is JsonObject properties
            ? properties.DeepClone().AsObject() : new JsonObject();
    }

    public static AppPropertyDefinition[] OrderedDefinitions(JsonObject definitions) => definitions
        .Where(pair => pair.Value is JsonObject)
        .Select(pair => new AppPropertyDefinition(pair.Key, pair.Value!.AsObject()))
        .OrderBy(property => Number(property.Definition["order"]) ?? 0)
        .ThenBy(property => property.Key, StringComparer.Ordinal)
        .ToArray();

    /// <summary>面板上显示的属性值：面板里的改动 → 已分析 plan 的快照 → WPE 设置（<paramref name="wpeValues"/>）→ 壁纸默认值。</summary>
    public static JsonObject ComposePropertyValues(JsonObject definitions, JsonObject? snapshot, JsonObject overrides, JsonObject? wpeValues = null)
    {
        var values = new JsonObject();
        foreach (AppPropertyDefinition property in OrderedDefinitions(definitions))
        {
            JsonNode? value = overrides[property.Key] ?? snapshot?[property.Key] ?? wpeValues?[property.Key] ?? property.Definition["value"];
            if (value is not null) values[property.Key] = value.DeepClone();
        }
        return values;
    }

    /// <summary>只有属性定义时拼出 Resolve / Merge 需要的最小 project.json。</summary>
    private static JsonObject ProjectOf(JsonObject definitions) =>
        new() { ["general"] = new JsonObject { ["properties"] = definitions.DeepClone() } };

    /// <summary>导入来源时读取用户在 Wallpaper Engine 里为它设的属性（只读 config.json）；<paramref name="wallpaperEngine"/> 是设置里的 WPE 路径，只作最后一个候选。</summary>
    public static WallpaperEngineProperties.Resolution ResolveWpeProperties(ProjectSource source, string? wallpaperEngine) =>
        WallpaperEngineProperties.Resolve(WallpaperEngineProperties.SourceWpe, source.DirectoryPath, source.SourcePath,
            ProjectOf(LoadPropertyDefinitions(source)), WallpaperEngineProperties.LocateConfig(wallpaperEngine));

    /// <summary>分析与保存方案用：WPE 值打底，面板里的改动覆盖在上；没有解析结果时原样用面板改动、不记来源。</summary>
    public static (JsonObject Properties, JsonObject? Origin) MergeWpeProperties(WallpaperEngineProperties.Resolution? resolution,
        JsonObject definitions, JsonObject overrides)
    {
        if (resolution is null) return (overrides.DeepClone().AsObject(), null);
        var (properties, origin) = WallpaperEngineProperties.Merge(resolution, ProjectOf(definitions), overrides);
        return (properties ?? new JsonObject(), origin);
    }

    /// <summary>面板上标"来自 WPE 设置"的属性：有 plan 时以 plan 记录的生效键为准，否则是 WPE 值里没被面板改动覆盖的键。</summary>
    public static IReadOnlySet<string> WpeMarkedKeys(JsonObject? plan, WallpaperEngineProperties.Resolution? resolution, JsonObject overrides) =>
        plan is not null ? WallpaperEngineProperties.AppliedKeys(plan)
            : (resolution?.Values ?? new JsonObject()).Select(pair => pair.Key).Where(key => !overrides.ContainsKey(key)).ToHashSet(StringComparer.Ordinal);

    /// <summary>属性面板顶部的来源说明；没有解析结果或明确用默认值时为空串。</summary>
    public static string PropertySourceNote(WallpaperEngineProperties.Resolution? resolution, bool english)
    {
        if (resolution is null || resolution.Source == WallpaperEngineProperties.SourceDefaults) return "";
        string language = english ? MessageCatalog.English : MessageCatalog.Chinese;
        if (resolution.Source == WallpaperEngineProperties.SourceUnavailable)
        {
            string code = resolution.Record["reason"] is JsonValue value && value.TryGetValue(out string? text) ? text : "";
            // 认不出的原因码不再把内部字段名摆到界面上，只说"原因不明"，细节留给报告文件。
            string reason = MessageCatalog.Find("properties.reason." + code) is null
                ? english ? "reason unknown" : "原因不明"
                : MessageCatalog.Get("properties.reason." + code, language);
            return english
                ? $"Could not read your Wallpaper Engine settings for this wallpaper ({reason}); starting from the wallpaper's own defaults."
                : $"没读到你在 Wallpaper Engine 里给这张壁纸做的设置（{reason}），从壁纸自带的默认值开始。";
        }
        int count = resolution.Values.Count;
        return count == 0
            ? english ? "No properties of this wallpaper were changed in Wallpaper Engine; starting from its defaults."
                : "这张壁纸在 Wallpaper Engine 里没改过属性，从默认值开始。"
            : english ? $"Prefilled {count} values from your Wallpaper Engine settings (marked \"from WPE settings\")."
                : $"已按你在 Wallpaper Engine 里的设置预填 {count} 项（标有“来自 WPE 设置”）。";
    }

    public static bool HasAudioEffectsChoice(JsonObject plan) =>
        plan["audio_effects_choice"] is JsonObject choice &&
        choice["status"]?.GetValue<string>() is "available" or "applied";

    public static string AudioEffectsChoiceSummary(JsonObject plan, bool english)
    {
        if (plan["audio_effects_choice"] is not JsonObject choice) return "";
        string status = choice["status"]?.GetValue<string>() ?? "";
        if (status is not ("available" or "applied")) return "";
        JsonArray? effects = (status == "applied" ? choice["omitted_effects"] : choice["available_effects"]) as JsonArray;
        string[] names = effects?.Select(EffectName).Where(name => name.Length > 0).Distinct().ToArray() ?? [];
        string listed = names.Length == 0 ? (english ? "identified audio effects" : "已识别的音频效果") : string.Join(", ", names);
        return status == "applied"
            ? (english ? $"Audio effects omitted: {listed}; music-reactive lighting stops." : $"已关闭音频效果：{listed}；灯光不再随音乐变化。")
            : (english ? $"Audio effects can be omitted: {listed}; this stops music-reactive lighting." : $"可关闭音频效果：{listed}；关闭后灯光不再随音乐变化。");
    }

    /// <summary>
    /// 图层清单一行的中性提示：只说位置与显隐方式，不按名字猜用途，也不暗示应当排除。
    /// plan 里的 suspected_overlay_reasons 字段名保持不变；名字匹配那一条不上界面。
    /// </summary>
    public static string LayerHints(JsonObject layer, bool english) => string.Join(english ? ", " : "、",
        ((layer["suspected_overlay_reasons"] as JsonArray)?.AsEnumerable() ?? [])
            .Select(reason => (reason is JsonValue value && value.TryGetValue<string>(out string? text) ? text : null) switch {
                "small_image_in_canvas_corner" => english ? "small layer in a corner" : "角落小图层",
                "timer_driven_visibility" => english ? "timer-driven visibility" : "定时显示图层",
                _ => "" })
            .Where(hint => hint.Length > 0).Distinct());

    public static string LoopSummary(JsonObject plan, JsonObject? bake, bool english)
    {
        if (plan["route"]?.GetValue<string>() == "effect_prefix")
            return EffectPrefixLoopSummary(plan, english);
        JsonObject? loop = plan["loop"] as JsonObject;
        if (loop?["status"]?.GetValue<string>() is "repairable_cut_candidate_requires_full_validation" or
            "observational_candidate_requires_seam_validation")
            return english ? "Legacy image-derived loop; re-analyze using source periods."
                : "旧版画面推断循环；请按源周期重新分析。";
        JsonObject? candidate = loop?["candidates"]?.AsArray().FirstOrDefault() as JsonObject;
        if (candidate is null) return "";
        string seconds = Number(candidate["seconds"])?.ToString("0.###", CultureInfo.InvariantCulture) ?? "?";
        string duration = english ? $"{seconds} seconds ({candidate["frames"]} frames)" : $"{seconds} 秒（{candidate["frames"]} 帧）";
        int unresolved = (loop?["unresolved"] as JsonArray)?.Count ?? 0;
        string pending = unresolved == 0 ? "" : english
            ? $"; {unresolved} unresolved mechanism(s), cannot generate a complete loop"
            : $"；另有 {unresolved} 项未解决机制，不能生成完整循环";
        return (english ? "source-period candidate " : "源周期候选 ") + duration + pending;
    }

    private static string EffectPrefixLoopSummary(JsonObject plan, bool english)
    {
        JsonObject[] caches = plan["effect_prefix_caches"]?.AsArray().OfType<JsonObject>().ToArray() ?? [];
        string[] periods = caches.Select(cache =>
        {
            JsonObject? loop = cache["loop"] as JsonObject;
            JsonObject? candidate = loop?["candidates"]?.AsArray().OfType<JsonObject>().FirstOrDefault();
            string owner = cache["owner_layer_id"]?.ToString() ?? "?";
            string prefix = cache["prefix_effect_count"]?.ToString() ?? "?";
            if (candidate is null) return english ? $"owner {owner}: period unresolved" : $"对象 {owner}：周期未解析";
            string seconds = Number(candidate["seconds"])?.ToString("0.###", CultureInfo.InvariantCulture) ?? "?";
            string frames = candidate["frames"]?.ToString() ?? "?";
            return english ? $"owner {owner}, first {prefix} effects: {seconds}s/{frames} frames; later effects stay live"
                : $"对象 {owner}，前 {prefix} 个效果：{seconds} 秒/{frames} 帧；后续效果保留实时运行";
        }).ToArray();
        return periods.Length == 0 ? (english ? "Effect-prefix route has no cache periods." : "特效前缀路线没有缓存周期。")
            : string.Join(english ? " | " : "；", periods);
    }

    public static string PhaseStartSummary(JsonObject bake, bool english)
    {
        if (Number(bake["source_start_frame"]) is not double start || start == 0)
            return english ? "Generated from the original start." : "从原始起点生成。";
        if (SourceStartOffset.Allows(bake))
            return english
                ? $"Analytic period kept; source frame 0 was an isolated start anomaly, so capture starts at source frame {start:0}."
                : $"周期仍来自解析；原作第 0 帧是孤立的起点异常帧，从源帧 {start:0} 开始录。";
        if (bake["seam_policy"]?.GetValue<string>() != ResidualMasking.SeamPolicy) return "";
        double crossfade = Number(bake["crossfade_frames"]) ?? 0;
        return english
            ? $"Analytic period kept; phase chosen at source frame {start:0} and the seam crossfaded over {crossfade:0} frames."
            : $"周期仍来自解析，起点相位选在源帧 {start:0}，接缝用 {crossfade:0} 帧交叉淡化。";
    }

    public static string SourceScriptErrorSummary(JsonObject evidence, bool english, bool includeFullCapture = false)
    {
        bool sourceKnown = TrySourceScriptErrors(evidence, "source_script_error_count", "source_script_errors", out JsonObject[] sourceErrors);
        JsonObject[] captureErrors = [];
        bool captureKnown = includeFullCapture && TrySourceScriptErrors(evidence,
            "full_capture_source_script_error_count", "full_capture_source_script_errors", out captureErrors);
        JsonObject[] allErrors = sourceErrors.Concat(captureErrors).GroupBy(FaultKey).Select(group => group.First()).ToArray();
        string sourceState = sourceKnown
            ? sourceErrors.Length == 0
                ? (english ? "Source observation recorded no script exceptions." : "来源观测未记录脚本异常。")
                : (english ? $"Source script exceptions: {sourceErrors.Length}; affected owners are retained live: {FaultOwners(sourceErrors)}."
                    : $"来源脚本异常：{sourceErrors.Length} 个；相关对象已保留为实时层：{FaultOwners(sourceErrors)}。")
            : (english ? "Source script-fault evidence is unknown." : "来源脚本异常证据未知。");
        if (!includeFullCapture) return sourceState;
        if (!captureKnown) return sourceState + (english ? " Full-capture script-fault evidence is unknown." : " 完整捕获脚本异常证据未知。");
        if (captureErrors.Length == 0) return sourceState;
        int additional = allErrors.Length - sourceErrors.GroupBy(FaultKey).Count();
        return sourceState + (english
            ? $" Full capture recorded {allErrors.Length} distinct script exception(s){(additional > 0 ? $" ({additional} additional)" : "")}; see bake.json for details."
            : $" 完整捕获记录了 {allErrors.Length} 个不同的脚本异常{(additional > 0 ? $"（其中新增 {additional} 个）" : "")}；详情见 bake.json。");
    }

    private static bool TrySourceScriptErrors(JsonObject evidence, string countKey, string errorsKey, out JsonObject[] errors)
    {
        errors = [];
        if (evidence[countKey] is not JsonValue count || !count.TryGetValue<int>(out int value) || value < 0 ||
            evidence[errorsKey] is not JsonArray items || items.Count != value || items.Any(item => item is not JsonObject)) return false;
        errors = items.OfType<JsonObject>().ToArray();
        return true;
    }

    private static string FaultKey(JsonObject error) => string.Join("|", error["binding_id"]?.ToJsonString() ?? "",
        error["owner_layer_id"]?.ToJsonString() ?? "", error["property"]?.ToJsonString() ?? "");

    private static string FaultOwners(IEnumerable<JsonObject> errors) => string.Join(", ", errors.Select(error =>
        error["owner_name"] is JsonValue name && name.TryGetValue<string>(out string? text) ? text :
        error["owner_layer_id"]?.ToString() ?? "unknown")
        .Distinct(StringComparer.Ordinal));

    private static string EffectName(JsonNode? effect)
    {
        if (effect is JsonValue value && value.TryGetValue<string>(out string? text)) return text;
        if (effect is not JsonObject item) return "";
        string? layer = item["layer_name"]?.GetValue<string>();
        string? effectName = item["name"]?.GetValue<string>();
        return layer is null ? effectName ?? "" : effectName is null ? layer : layer + ": " + effectName;
    }

    public static string HybridValidationSummary(JsonObject bake, bool english)
    {
        if (bake["plan"] is JsonObject prefixPlan && prefixPlan["route"]?.GetValue<string>() == "effect_prefix")
            return EffectPrefixValidationSummary(prefixPlan, bake, english);
        JsonObject[] groups = bake["groups"]?.AsArray().OfType<JsonObject>()
            .Where(group => group["storage"]?.GetValue<string>() == "video").ToArray() ?? [];
        string[] summaries = groups.Select(group =>
        {
            string id = group["id"]?.GetValue<string>() ?? "video";
            string seam = group["encoded_loop_validation"]?["status"]?.GetValue<string>() ?? "n/a";
            string repair;
            if (group["local_repair"] is JsonObject local)
            {
                string repairStatus = local["status"]?.GetValue<string>() ?? "n/a";
                double? coverage = Number(local["mask_coverage"] ?? local["mask"]?["affected_coverage_after_feather"]);
                double? seconds = Number(local["bridge_seconds"] ?? local["temporal"]?["bridge_seconds"]);
                string area = coverage is null ? "n/a" : (coverage.Value * 100).ToString("0.##", CultureInfo.InvariantCulture) + "%";
                string duration = seconds is null ? "n/a" : seconds.Value.ToString("0.###", CultureInfo.InvariantCulture) + "s";
                repair = english ? $"repair {repairStatus} {area}/{duration} (limit 0.5s)" :
                    $"局部修复 {repairStatus} {area}/{duration}（上限 0.5s）";
                if (local["error"] is JsonValue error && error.TryGetValue<string>(out string? errorText))
                    repair += $": {local["error_type"]?.GetValue<string>()}: {errorText}";
            }
            else repair = english ? "source-period encoding; no repair" : "源周期编码；未修补";
            string hardware = english ? "hardware decode not tested" : "硬解未测";
            if (group["hardware_decode"] is JsonObject decode)
            {
                string codec = decode["video_stream"]?["codec_name"]?.GetValue<string>() ?? "video";
                JsonObject[] adapters = decode["adapters"]?.AsArray().OfType<JsonObject>().ToArray() ?? [];
                int passed = adapters.Count(a => a["passed"]?.GetValue<bool>() == true);
                string[] failed = adapters.Where(a => a["passed"]?.GetValue<bool>() != true)
                    .Select(a => a["name"]?.GetValue<string>() ?? "unknown").ToArray();
                hardware = english ? $"{codec} hardware short check {passed}/{adapters.Length} adapters" :
                    $"{codec} 硬解短检 {passed}/{adapters.Length} 张显卡通过";
                if (failed.Length > 0) hardware += (english ? "; failed/unverified: " : "；失败/未确认：") + string.Join(", ", failed);
            }
            return english ? $"{id}: seam {seam}; {repair}; {hardware}" : $"{id}：接缝 {seam}；{repair}；{hardware}";
        }).ToArray();
        string body = summaries.Length == 0 ? (english ? "No encoded video groups." : "没有编码视频组。") : string.Join(" | ", summaries);
        if (bake["composition_validation"] is JsonObject composition)
        {
            string status = composition["status"]?.GetValue<string>() ?? "n/a";
            string rgb = Metric(composition["metrics"]?["rgb_mae_255"]);
            string tile = Metric(composition["worst_rgb_tile"]?["metrics"]?["rgb_mae_255"]);
            string alpha = Metric(composition["metrics"]?["alpha_mae_255"]);
            body = (english ? $"Short composition {status}: RGB/tile/alpha MAE {rgb}/{tile}/{alpha}" :
                $"短段合成 {status}：RGB/局部/Alpha MAE {rgb}/{tile}/{alpha}") + " | " + body;
        }
        // 旧版 bake.json（参照式接缝校验之前）里成品是"如实复现源片自身的切口"通过的，摘要第一句就要把这件事说清楚。
        // 新版不再写 source_discontinuity，这个分支只为读旧结果保留；判据类已删，状态字面量写在这里。
        if (bake["source_discontinuity"] is JsonObject sourceCut &&
            sourceCut["status"]?.GetValue<string>() == "candidate_matches_source_discontinuity")
            body = (english
                ? "The source material itself jumps at its loop point; this candidate reproduces that cut as-is and nothing was repaired."
                : "原作素材自身在循环点有可见跳变，成品如实复现，未做修复。") + " | " + body;
        if (bake["reason"] is JsonValue reason && reason.TryGetValue<string>(out string? reasonText) && !string.IsNullOrWhiteSpace(reasonText))
            body = reasonText + " | " + body;
        body += " | " + SourceScriptErrorSummary(bake, english, includeFullCapture: true);
        string loop = bake["plan"] is JsonObject savedPlan ? LoopSummary(savedPlan, bake, english) : "";
        if (loop.Length > 0) body += " | " + loop;
        string phase = PhaseStartSummary(bake, english);
        if (phase.Length > 0) body += " | " + phase;
        string target = HardwareDecodeConclusions(bake, english);
        if (target.Length > 0) body += " | " + target;
        return body + (english ? " | Official-player playback not measured." : " | 尚未进行官方播放器实播验证。");
    }

    private static string EffectPrefixValidationSummary(JsonObject plan, JsonObject bake, bool english)
    {
        string[] groups = bake["groups"]?.AsArray().OfType<JsonObject>().Select(group =>
        {
            string owner = group["owner_layer_id"]?.ToString() ?? "?";
            string frames = group["frames"]?.ToString() ?? "?";
            string seam = group["encoded_loop_validation"]?["status"]?.GetValue<string>() ?? "pending";
            string hardware = group["hardware_decode"]?["all_adapters_passed"]?.GetValue<bool>() switch
            {
                true => "passed", false => "failed", null => "pending"
            };
            JsonObject? loop = group["period"] as JsonObject;
            JsonObject? candidate = loop?["candidates"]?.AsArray().OfType<JsonObject>().FirstOrDefault();
            string seconds = Number(candidate?["seconds"])?.ToString("0.###", CultureInfo.InvariantCulture) ?? "?";
            return english ? $"owner {owner}: {seconds}s/{frames} frames, {group["status"]}; seam {seam}; hardware {hardware}"
                : $"对象 {owner}：{seconds} 秒/{frames} 帧，{group["status"]}；接缝 {seam}；硬解 {hardware}";
        }).ToArray() ?? [];
        string body = groups.Length == 0 ? (english ? "No effect-prefix groups." : "没有特效前缀组。") : string.Join(" | ", groups);
        string composition = bake["composition_validation"]?["status"]?.GetValue<string>() ?? "pending";
        body = (english ? $"Effect-prefix composition {composition}" : $"特效前缀合成 {composition}") + " | " + body;
        string loops = EffectPrefixLoopSummary(plan, english);
        string target = HardwareDecodeConclusions(bake, english);
        return body + " | " + loops + (target.Length > 0 ? " | " + target : "") + (english
            ? " | Official WPE playback and GPU power benefit are unverified."
            : " | 官方 WPE 实播和 GPU 功耗收益尚未验证。");
    }

    /// <summary>
    /// 硬解实测只在烘焙机上做过，结论文案由 Baker.Core 的 MessageCatalog 生成、写在每组的 hardware_decode.conclusion 里。
    /// 同一台机器上各组的结论多半一模一样，去重后整段只说一次。
    /// </summary>
    private static string HardwareDecodeConclusions(JsonObject bake, bool english) =>
        string.Join(" ", (bake["groups"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(group => group["hardware_decode"]?["conclusion"]?[english ? "en" : "zh"]?.GetValue<string>())
            .OfType<string>().Where(text => text.Length > 0).Distinct(StringComparer.Ordinal));

    public static double? Number(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue<double>(out double number) && double.IsFinite(number)) return number;
        if (value.TryGetValue<float>(out float single) && float.IsFinite(single)) return single;
        if (value.TryGetValue<decimal>(out decimal decimalNumber)) return (double)decimalNumber;
        if (value.TryGetValue<sbyte>(out sbyte signedByte)) return signedByte;
        if (value.TryGetValue<byte>(out byte unsignedByte)) return unsignedByte;
        if (value.TryGetValue<short>(out short smallInteger)) return smallInteger;
        if (value.TryGetValue<ushort>(out ushort unsignedSmallInteger)) return unsignedSmallInteger;
        if (value.TryGetValue<int>(out int integer)) return integer;
        if (value.TryGetValue<uint>(out uint unsignedInteger)) return unsignedInteger;
        if (value.TryGetValue<long>(out long largeInteger)) return largeInteger;
        if (value.TryGetValue<ulong>(out ulong unsignedLargeInteger)) return unsignedLargeInteger;
        return null;
    }

    private static string Metric(JsonNode? node) => Number(node) is double value
        ? value.ToString("0.###", CultureInfo.InvariantCulture) : "n/a";

    /// <summary>阻断原因按界面语言取：优先读 blockers_localized，缺失或没有对应语言时回退英文原文。</summary>
    public static string[] BlockerLines(JsonObject? plan, bool english)
    {
        if (plan?["blockers_localized"] is JsonArray localized && localized.Count > 0)
            return localized.OfType<JsonObject>().Select(item =>
                (english ? null : item["zh"]?.GetValue<string>()) ?? item["en"]?.GetValue<string>() ?? "")
                .Where(text => text.Length > 0).ToArray();
        return (plan?["blockers"] as JsonArray ?? []).Select(item => item?.GetValue<string>() ?? "")
            .Where(text => text.Length > 0).ToArray();
    }

    /// <summary>一行结论：plan 顶层 summary 的对应语言版本，没有就空串。</summary>
    public static string PlanVerdict(JsonObject? plan, bool english) =>
        plan?["summary"] is JsonObject summary
            ? (english ? null : summary["zh"]?.GetValue<string>()) ?? summary["en"]?.GetValue<string>() ?? "" : "";

    /// <summary>档位名的界面标签；plan 里没有档位（旧接口分析出来的）时回空串。</summary>
    public static string PresetLabel(string? preset, bool english) => preset switch
    {
        RetimeProfile.Efficiency => english ? "Efficiency" : "效率",
        RetimeProfile.Balanced => english ? "Balanced" : "平衡",
        RetimeProfile.Quality => english ? "Quality" : "质量",
        _ => ""
    };

    /// <summary>
    /// 结论区那一行档位信息：档位、观感改动预算、相位差（圈）、循环长度。
    /// 预算带来源（档位给的还是用户覆盖的）；质量档没有预算门槛，写"按改动最小求解"。
    /// 相位差只有摆动改频成立时才有，缺了就不列这一项，不拿 0 充数。
    /// </summary>
    public static string ProfileSummary(JsonObject? plan, bool english)
    {
        if (plan?["retime_profile"] is not JsonObject profile) return "";
        var parts = new List<string>();
        string preset = PresetLabel(profile["preset"]?.GetValue<string>(), english);
        if (preset.Length > 0) parts.Add((english ? "preset " : "档位 ") + preset);
        string manual = english ? " (manual)" : "（手动）";
        string budgetOrigin = profile["retime_budget_source"]?.GetValue<string>() == RetimeProfile.FromOverride ? manual : "";
        parts.Add((Number(profile["retime_budget_percent"]) is double budget
            ? (english ? "look budget " : "观感预算 ") + budget.ToString("0.###", CultureInfo.InvariantCulture) + "%"
            : english ? "smallest change" : "按改动最小求解") + budgetOrigin);
        JsonObject? candidate = plan["loop"]?["candidates"]?.AsArray().OfType<JsonObject>().FirstOrDefault();
        if (Number(candidate?["sway_retime"]?["phase_drift_cycles"]) is double drift)
            parts.Add((english ? "phase drift " : "相位差 ") + drift.ToString("0.###", CultureInfo.InvariantCulture) +
                (english ? " cycles" : " 圈"));
        if (Number(candidate?["seconds"]) is double seconds)
            parts.Add((english ? "loop " : "循环 ") + seconds.ToString("0.###", CultureInfo.InvariantCulture) +
                (english ? " s" : " 秒"));
        double maximum = Number(profile["loop_max_seconds"]) ?? 0;
        string maximumOrigin = profile["loop_max_seconds_source"]?.GetValue<string>() == RetimeProfile.FromOverride ? manual : "";
        if (maximum > 0) parts.Add((english ? "at most " : "上限 ") + maximum.ToString("0.###", CultureInfo.InvariantCulture) +
            (english ? " s" : " 秒") + maximumOrigin);
        return string.Join(" · ", parts);
    }

    /// <summary>
    /// 折叠的"详情"面板第一行：这次分析走的路线与它切出来的块数。技术口径原样保留，界面上只在详情里显示。
    /// </summary>
    public static string RouteSummary(JsonObject? plan, bool english)
    {
        if (plan is null) return "";
        int groups = Admission.GroupCount(plan), statics = Admission.StaticGroupCount(plan), live = plan["live_layer_ids"]?.AsArray().Count ?? 0;
        int prefixCaches = plan["effect_prefix_caches"]?.AsArray().Count ?? 0;
        return plan["route"]?.GetValue<string>() == "effect_prefix"
            ? english ? $"{prefixCaches} effect-prefix caches · {live} live objects"
                : $"特效前缀缓存 {prefixCaches} 组 · 实时对象 {live} 个"
            : english ? $"{groups} video groups · {statics} static caches · {live} live objects"
                : $"视频组 {groups} 个 · 静态缓存 {statics} 组 · 实时对象 {live} 个";
    }

    /// <summary>
    /// 折叠的"详情"面板第二行：循环、布局冲突、已移到前景／已简化文字特效的层、音频效果、源脚本异常与阻塞原因，
    /// 按原来的口径连成一段。这段是给看得懂的人读的，界面上只在详情里显示。
    /// </summary>
    public static string PlanNotes(JsonObject? plan, bool english)
    {
        if (plan is null) return "";
        string timing = LoopSummary(plan, null, english);
        string timingAdvice = timing.Length == 0 ? "" : (english ? "; " : "；") + timing;
        string[] blockerLines = BlockerLines(plan, english);
        string blockers = blockerLines.Length > 0 ? " · " + string.Join("; ", blockerLines) : "";
        string admissionStatus = plan["video_layout_admission"]?["status"]?.GetValue<string>() ?? "";
        string conflictAdvice = admissionStatus == "requires_user_choice"
            ? english ? " · Choose one: tick Allow layered video, or tick Move live widgets to foreground, then reanalyze."
                : " · 需要选择：在高级里勾上“允许把画面拆成几层做”，或勾上“把还在动的小东西放到最前面”，然后重新分析。"
            : admissionStatus == "full_frame_unreachable"
            ? english ? " · This scene has no full-frame layout: realtime drawing that cannot be moved stays in front of the video group; no setting or layered choice changes that."
                : " · 这个场景没有全幅布局：视频组被搬不走的实时绘制挡在前面，改设置或分层都不会改变这一点。" : "";
        var tradeoff = plan["occlusion_tradeoff"] as JsonObject;
        var promotedRoots = tradeoff?["promoted_roots"] as JsonArray;
        string promoted = tradeoff?["status"]?.GetValue<string>() == "applied" && promotedRoots is not null
            ? string.Join(", ", promotedRoots.Select(node => node is JsonObject root
                ? root["name"]?.GetValue<string>() ?? root["id"]?.ToString() ?? ""
                : node?.ToString() ?? "").Where(name => name.Length > 0))
            : "";
        string promotedAdvice = promoted.Length == 0 ? ""
            : english ? $" · Moved to foreground: {promoted}" : $" · 已移到前景：{promoted}";
        var textEffectsChoice = plan["text_effects_choice"] as JsonObject;
        var simplifiedLayers = textEffectsChoice?["simplified_layers"] as JsonArray;
        string simplified = textEffectsChoice?["status"]?.GetValue<string>() == "applied" && simplifiedLayers is not null
            ? string.Join(", ", simplifiedLayers.Select(node => node is JsonObject layer
                ? layer["name"]?.GetValue<string>() ?? layer["id"]?.ToString() ?? ""
                : node?.ToString() ?? "").Where(name => name.Length > 0))
            : "";
        string simplifiedAdvice = simplified.Length == 0 ? ""
            : english ? $" · Simplified text effects: {simplified}" : $" · 已简化文字特效：{simplified}";
        string audioEffects = AudioEffectsChoiceSummary(plan, english);
        string audioEffectsAdvice = audioEffects.Length == 0 ? "" : " · " + audioEffects;
        string scriptErrors = SourceScriptErrorSummary(plan, english);
        string scriptErrorsAdvice = scriptErrors.Length == 0 ? "" : " · " + scriptErrors;
        return (timingAdvice + conflictAdvice + promotedAdvice + simplifiedAdvice + audioEffectsAdvice +
            scriptErrorsAdvice + blockers).TrimStart(' ', '·', ';', '；').Trim();
    }

    /// <summary>
    /// 折叠的"技术细节"面板：纯数字表，一行一对（名称, 值），不组句子、不写段落。
    /// 循环长度、总调速、视频组数、实时图层数、输出分辨率与帧率原样取自 plan。
    /// </summary>
    public static (string Label, string Value)[] NumberRows(JsonObject? plan, bool english)
    {
        if (plan is null) return [];
        var rows = new List<(string, string)>();
        JsonObject? candidate = plan["loop"]?["candidates"]?.AsArray().OfType<JsonObject>().FirstOrDefault();
        double numerator = Number(plan["settings"]?["fps_numerator"]) ?? 0;
        double denominator = Number(plan["settings"]?["fps_denominator"]) ?? 0;
        double fps = denominator > 0 ? numerator / denominator : 0;
        string fpsText = fps <= 0 ? "" : (denominator == 1 ? fps.ToString("0", CultureInfo.InvariantCulture)
            : fps.ToString("0.##", CultureInfo.InvariantCulture)) + " fps";
        if (Number(candidate?["seconds"]) is double seconds && seconds > 0)
        {
            double? frames = Number(candidate?["frames"]);
            string value = seconds.ToString("0.###", CultureInfo.InvariantCulture) + (english ? " s" : " 秒");
            if (frames is > 0)
                value += " / " + frames.Value.ToString("0", CultureInfo.InvariantCulture) + (english ? " frames" : " 帧") +
                    (fpsText.Length > 0 ? " @" + fpsText : "");
            rows.Add((english ? "Loop length" : "循环长度", value));
        }
        if (Number(candidate?["total_retime_cost_percent"]) is double retime)
            rows.Add((english ? "Total retime" : "总调速", retime.ToString("0.###", CultureInfo.InvariantCulture) + "%"));
        int groups = Admission.GroupCount(plan), statics = Admission.StaticGroupCount(plan);
        rows.Add((english ? "Video groups" : "视频组数", groups.ToString(CultureInfo.InvariantCulture)));
        if (plan["route"]?.GetValue<string>() == "whole_layer")
            rows.Add((english ? "Static caches" : "静态缓存", statics.ToString(CultureInfo.InvariantCulture)));
        rows.Add((english ? "Live layers" : "实时图层数", (plan["live_layer_ids"]?.AsArray().Count ?? 0).ToString(CultureInfo.InvariantCulture)));
        if (plan["live_overlays_hoisted"] is JsonArray { Count: > 0 } overlays)
            rows.Add((english ? "Foreground widgets" : "置顶小组件数", overlays.Count.ToString(CultureInfo.InvariantCulture)));
        double width = Number(plan["output_resolution"]?["width"]) ?? 0, height = Number(plan["output_resolution"]?["height"]) ?? 0;
        if (width > 0 && height > 0)
            rows.Add((english ? "Output resolution and frame rate" : "输出分辨率与帧率",
                width.ToString("0", CultureInfo.InvariantCulture) + "×" + height.ToString("0", CultureInfo.InvariantCulture) +
                (fpsText.Length > 0 ? " @" + fpsText : "")));
        return [.. rows];
    }

    /// <summary>取舍清单的抬头：有方案时是"有 N 个方案…"，主体类壁纸是那句拒绝说明，其余为空串。</summary>
    public static string TradeoffHeader(JsonObject? plan, bool english) =>
        plan?[TradeoffOptions.Field] is JsonObject record &&
        record["status"]?.GetValue<string>() is "available" or "subject_only" or "dependency_blocked"
            ? (english ? record[MessageCatalog.English] : record[MessageCatalog.Chinese])?.GetValue<string>() ?? "" : "";

    /// <summary>
    /// 界面上一块取舍方案：标题（关掉什么）、按界面顺序排好的说明行、命令行写法，
    /// 以及"按此方案重新分析"要填回设置的那几项（属性关闭值、排除图层、要不要固定视角）。
    /// </summary>
    /// <param name="Kinds">这个方案要关掉的东西（视差、鼠标特效…），界面的勾选卡片按它出项。</param>
    /// <param name="ResidualLiveCount">关掉之后估计还会实时跑的层数，卡片底部那行小字用它。</param>
    /// <param name="ExpectedFullFrame">关掉之后预计整张都能录成视频。</param>
    internal sealed record AppTradeoffOption(string Title, string[] Lines, string Command,
        (string Key, JsonNode? OffValue)[] Properties, int[] ExcludeLayers, bool FixedView,
        string[] Kinds, int ResidualLiveCount, bool ExpectedFullFrame);

    /// <summary>
    /// plan 里的取舍清单摊成界面要显示的几块。只在 status=available 时出块：
    /// 主体类（subject_only）按结论文案原样显示，不出清单。
    /// 行的顺序按界面口径排：要关什么 → 怎么关（属性优先、命令行其次）→ 连带关掉什么 → 路线与残留 → 保留实时不省电。
    /// </summary>
    internal static AppTradeoffOption[] TradeoffOptionViews(JsonObject? plan, bool english)
    {
        if (plan?[TradeoffOptions.Field] is not JsonObject record ||
            record["status"]?.GetValue<string>() != "available") return [];
        string language = english ? MessageCatalog.English : MessageCatalog.Chinese;
        string[] order = ["lead", "how", "alternative", "collateral", "route", "residual", "retain_live", "bake_caveat"];
        return [.. (record["options"] as JsonArray ?? []).OfType<JsonObject>().Select(option =>
        {
            var parts = TradeoffOptions.OptionLines(option, language);
            string title = parts.FirstOrDefault(part => part.Field == "lead").Text ?? "";
            string[] lines = [.. order.Where(field => field != "lead")
                .SelectMany(field => parts.Where(part => part.Field == field).Select(part => part.Text))];
            var properties = (option["properties"] as JsonArray ?? []).OfType<JsonObject>()
                .Where(entry => entry["key"]?.GetValue<string>() is { Length: > 0 } &&
                    entry["status"]?.GetValue<string>() is "resolved" or "conditional" && entry.ContainsKey("off_value"))
                .Select(entry => (entry["key"]!.GetValue<string>(), entry["off_value"]?.DeepClone())).ToArray();
            int[] exclude = [.. (option["exclude_layers"] as JsonArray ?? []).Select(Number).OfType<double>().Select(id => (int)id)];
            string[] kinds = [.. (option["turn_off_kinds"] as JsonArray ?? [])
                .Select(node => node is JsonValue value && value.TryGetValue<string>(out string? kind) ? kind : null)
                .OfType<string>()];
            return new AppTradeoffOption(title, lines, option["command"]?.GetValue<string>() ?? "",
                properties, exclude, option["view_mode"]?.GetValue<string>() == "fixed_view", kinds,
                (int)(Number(option["estimated_residual_live_layers"]) ?? 0),
                option["expected_full_frame"] is JsonValue full && full.TryGetValue(out bool expected) && expected);
        })];
    }

}
