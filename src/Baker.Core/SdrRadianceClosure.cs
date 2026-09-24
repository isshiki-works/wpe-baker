using System.Globalization;
using System.Text.Json.Nodes;
using Baker.Core.Analysis.EffectRange;

namespace Baker.Core;

/// <summary>
/// SDR 辐射闭合证明。场景的 hdr 标志本身不是拒绝理由；真正的拒绝理由是"被捕获组的输出可能超出 [0,1]
/// 而被 RGBA8 组捕获 clip"。当且仅当组内每个可见绘制层的材质来源（R1）、混合模式（R2）、纹理输入（R3）、
/// 颜色/亮度/alpha 标量（R4）与反馈路径（R5）全部落在可判定的 SDR 闭合集合内，才认定捕获无损。
/// 任何一条未知都判不通过——判据只回答"会不会被 clip"，不回答画面、循环或回放收益。
/// 白名单与数值区间在 Domain 的 <see cref="SdrRadianceCriteria"/>；这里逐层读 JSON 与资源、写 checks 与说明文字。
/// </summary>
public static class SdrRadianceClosure
{
    /// <summary>证明不成立时给用户的拒绝理由前缀；点名失败层与判据的说明会追加在后面。</summary>
    public const string HdrBlocker = "HDR intermediate compositing is not supported by the current RGBA8 group capture; " +
        "its radiance range must not be silently clipped into an SDR video.";

    private const string Scope = "The closure proof only states that this group's own output stays within [0,1], " +
        "so the RGBA8 group capture does not clip it. Capture is sampled before postprocessing, leaving bloom and tone mapping " +
        "to the original playback pipeline. It is not a proof of image, loop or playback benefit, and it does not remove the " +
        "1/255 quantization inherent to an 8-bit capture.";

    /// <summary>
    /// 对整份计划求值：hdr 未开启时判据不运行，开启时每个视频组都必须闭合才放行。
    /// <paramref name="project"/> 只用于文案：报出壁纸自带的 HDR 开关叫什么，传 null 时那半句略去。
    /// </summary>
    public static JsonObject Describe(JsonObject scene, JsonObject properties, JsonObject? trace, JsonArray groups,
        ProjectSource source, string? assets, bool hdrEnabled, JsonObject? project, out Blocker? blocker) =>
        Describe(scene, properties, trace, groups, source, assets, hdrEnabled, project, EffectRangeRules.Default, out blocker);

    /// <summary>同上，特效值域规则表可替换（测试用自带着色器的夹具表）。</summary>
    internal static JsonObject Describe(JsonObject scene, JsonObject properties, JsonObject? trace, JsonArray groups,
        ProjectSource source, string? assets, bool hdrEnabled, JsonObject? project, EffectRangeRules effectRules, out Blocker? blocker)
    {
        blocker = null;
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(properties);
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(source);
        var result = new JsonObject {
            ["hdr"] = hdrEnabled,
            ["status"] = "not_applicable",
            ["capture_point"] = "before_postprocessing",
            ["scope"] = Scope,
            ["groups"] = new JsonArray(),
            ["blocker"] = null };
        if (!hdrEnabled)
        {
            new Message("reason.sdr_scene_not_hdr").Write(result, "reason");
            return result;
        }
        var runtimeLayers = trace?["runtime_layers"] as JsonArray;
        var decoders = trace?["runtime_video_decoders"] as JsonArray;
        var failures = new List<string>();
        var failuresZh = new List<string>();
        bool closed = true;
        var evaluated = new JsonArray();
        foreach (var group in groups.OfType<JsonObject>())
        {
            JsonObject verdict = Evaluate(scene, properties, runtimeLayers, decoders, group, source, assets, effectRules);
            evaluated.Add(verdict);
            if (verdict["status"]?.GetValue<string>() == "closed") continue;
            closed = false;
            failures.AddRange(verdict["reasons"]!.AsArray().Select(reason => reason!.GetValue<string>()));
            failuresZh.AddRange(ChineseReasons(verdict));
        }
        result["groups"] = evaluated;
        result["status"] = closed ? "closed" : "open";
        if (closed) return result;
        string listed = string.Join(" ", failures.Take(4).Select(reason => reason + "."));
        if (failures.Count > 4) listed += $" (+{failures.Count - 4} more unproven layers.)";
        // 中文通道按同一顺序另拼一份，不把英文判据明细塞进中文结论。
        string listedZh = ChineseUnproven(failuresZh, failures.Count);
        // 明细单独留一份字段，文案由 PlanNarrative 按语言渲染；写进 blockers 的 legacy 英文与本判据自己的措辞逐字相同。
        result["unproven"] = listed;
        blocker = PlanNarrative.HdrRadianceOpen(scene, project, listed, listedZh);
        result["blocker"] = blocker.Text;
        return result;
    }

    /// <summary>中文版未通过明细：最多列四条，其余以"另有 N 处未通过"收尾。</summary>
    internal static string ChineseUnproven(IReadOnlyList<string> reasonsZh, int total)
    {
        string listed = string.Join("；", reasonsZh.Take(4)) + "。";
        if (total > 4) listed += $"（另有 {total - 4} 处未通过，明细见 plan.json 的 hdr_radiance_closure。）";
        return listed;
    }

    /// <summary>
    /// 从已求值的组结构（group_checks / per_layer）按 reasons 的同一顺序生成中文说明。
    /// 只读结构化字段，判据本身不变；英文 detail 认不出时退回按判据代号的通用中文，绝不把英文原文带进中文。
    /// </summary>
    internal static IEnumerable<string> ChineseReasons(JsonObject verdict)
    {
        string groupId = Text(verdict["group_id"]) ?? "group";
        foreach (var check in (verdict["group_checks"] as JsonArray ?? []).OfType<JsonObject>())
            if (Text(check["status"]) != "pass")
                yield return $"{groupId}：{ChineseDetail(Text(check["rule"]), Text(check["detail"]))}（{Text(check["rule"])}）";
        foreach (var layer in (verdict["per_layer"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (Text(layer["status"]) != "open") continue;
            string id = SceneGraph.Int(layer["layer_id"])?.ToString(CultureInfo.InvariantCulture) ?? "?";
            string label = Text(layer["layer_name"]) is string name ? $"{groupId} 的图层 {id} \"{name}\"" : $"{groupId} 的图层 {id}";
            foreach (var check in (layer["checks"] as JsonArray ?? []).OfType<JsonObject>().Where(check => Text(check["status"]) != "pass"))
                yield return $"{label}：{ChineseDetail(Text(check["rule"]), Text(check["detail"]))}（{Text(check["rule"])}）";
        }
    }

    /// <summary>英文判据明细 → 中文。模式与本文件里生成英文的措辞一一对应，引号里的资源名原样保留。</summary>
    private static readonly (System.Text.RegularExpressions.Regex Pattern, string Zh)[] ChineseDetails = [
        (Detail("^particle layers have no decidable output range$"), "粒子层的输出值域无法判定"),
        (Detail("^text layers have no decidable output range$"), "文字层的输出值域无法判定"),
        (Detail("^the layer carries authored effect layers$"), "这一层带有作者添加的特效"),
        (Detail("^the layer carries authored effect layers: effect (\"[^\"]*\") .*$"), "这一层的作者特效 $1 证明不了输出不超出 [0,1]"),
        (Detail("^the runtime trace has no observed material for this layer$"), "运行时观测里没有这一层的材质"),
        (Detail("^the renderer reported an effect layer on this layer$"), "渲染器报告这一层带有特效"),
        (Detail("^the runtime trace reported no material for this layer$"), "运行时观测报告这一层没有材质"),
        (Detail("^the runtime material consumes the audio spectrum$"), "运行时材质读取音频频谱"),
        (Detail("^the layer has no readable image model reference$"), "这一层没有可读的图像模型引用"),
        (Detail("^Layer is missing from the scene\\.$"), "被捕获的层不在场景里"),
        (Detail("^runtime shader (\"[^\"]*\") is not a built-in SDR shader$"), "运行时着色器 $1 不是内置 SDR 着色器"),
        (Detail("^runtime material role (\"[^\"]*\") is not a source material$"), "运行时材质角色 $1 不是源材质"),
        (Detail("^model (\"[^\"]*\") has no material reference$"), "模型 $1 没有材质引用"),
        (Detail("^material (\"[^\"]*\") declares no pass$"), "材质 $1 没有声明任何 pass"),
        (Detail("^material metadata for (\"[^\"]*\") could not be read: .*$"), "材质 $1 的元数据读不出来"),
        (Detail("^material shader (\"[^\"]*\") is not a built-in SDR shader$"), "材质着色器 $1 不是内置 SDR 着色器"),
        (Detail("^material combo (\"[^\"]*\") is not a known range-preserving combo$"), "材质 combo $1 不在已知不改变值域的 combo 之列"),
        (Detail("^pass blending (\"[^\"]*\") is not a range-preserving blend$"), "pass 混合模式 $1 可能抬高亮度上界"),
        (Detail("^colorBlendMode (.*) is not the normal \\(0\\) mode$"), "colorBlendMode $1 不是普通（0）模式"),
        (Detail("^the layer samples the current framebuffer through (\"[^\"]*\")$"), "这一层通过 $1 读取当前帧缓冲，形成反馈"),
        (Detail("^the layer samples a render target (\"[^\"]*\") whose range is not decidable$"), "这一层读取渲染目标 $1，值域无法判定"),
        (Detail("^texture (\"[^\"]*\") container could not be decided: .*$"), "纹理 $1 的容器格式无法判定"),
        (Detail("^texture (\"[^\"]*\") uses TEX format (.*), which is not a known 8-bit unsigned container$"), "纹理 $1 用的 TEX 格式 $2 不是已知的 8 位无符号格式"),
        (Detail("^video texture (\"[^\"]*\") has no observed decoder instance in the runtime trace$"), "视频纹理 $1 在运行时观测里没有解码实例"),
        (Detail("^video texture (\"[^\"]*\") reported unknown stream metadata$"), "视频纹理 $1 的流元数据未知"),
        (Detail("^video texture (\"[^\"]*\") decodes as (\"[^\"]*\"), which is not a known 8-bit SDR pixel format$"), "视频纹理 $1 解码为 $2，不是已知的 8 位 SDR 像素格式"),
        (Detail("^(?<label>scene clear color|color|brightness|alpha) is bound to a script; its value range cannot be decided$"), "${label} 由脚本驱动，取值范围无法判定"),
        (Detail("^(?<label>scene clear color|color|brightness|alpha) is bound to an animation; its value range cannot be decided$"), "${label} 由动画驱动，取值范围无法判定"),
        (Detail("^(?<label>scene clear color|color|brightness|alpha) is bound to an unresolved binding; its value range cannot be decided$"), "${label} 的绑定无法解析，取值范围无法判定"),
        (Detail("^(?<label>scene clear color|color|brightness|alpha) value .* is not a (?:decidable )?numeric literal$"), "${label} 的取值不是可判定的数字字面量"),
        (Detail("^(?<label>scene clear color|color|brightness|alpha) component (.*) is outside \\[0,1\\]$"), "${label} 的分量 $1 超出 [0,1]")];

    private static System.Text.RegularExpressions.Regex Detail(string pattern) =>
        new(pattern, System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>认不出的明细按判据代号给通用中文：R1 材质、R2 混合、R3 纹理、R4 标量、R5 反馈。</summary>
    internal static string ChineseDetail(string? rule, string? detail)
    {
        string text = detail ?? "";
        foreach (var (pattern, zh) in ChineseDetails)
        {
            var match = pattern.Match(text);
            if (!match.Success) continue;
            // 标量名只翻译捕获到的那一段，引号里的资源名不受影响。
            return match.Result(zh.Replace("${label}", ChineseLabel(match.Groups["label"].Value), StringComparison.Ordinal));
        }
        return rule switch {
            "R1" => "材质来源证明不了输出值域",
            "R2" => "混合方式证明不了不抬高亮度上界",
            "R3" => "纹理输入证明不了是 8 位 SDR",
            "R4" => "颜色、亮度或 alpha 的取值证明不了落在 [0,1] 内",
            "R5" => "读取了帧缓冲或渲染目标，形成反馈路径",
            _ => "判据未通过" };
    }

    private static string ChineseLabel(string label) => label switch {
        "scene clear color" => "场景清屏色", "color" => "颜色", "brightness" => "亮度", _ => label };

    /// <summary>对单个被捕获组求值：任一可见绘制层未通过，整组即判 open。</summary>
    public static JsonObject Evaluate(JsonObject scene, JsonObject properties, JsonArray? runtimeLayers,
        JsonArray? videoDecoders, JsonObject group, ProjectSource source, string? assets) =>
        Evaluate(scene, properties, runtimeLayers, videoDecoders, group, source, assets, EffectRangeRules.Default);

    /// <summary>同上，特效值域规则表可替换（测试用自带着色器的夹具表）。</summary>
    internal static JsonObject Evaluate(JsonObject scene, JsonObject properties, JsonArray? runtimeLayers,
        JsonArray? videoDecoders, JsonObject group, ProjectSource source, string? assets, EffectRangeRules effectRules)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(properties);
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(source);
        var objects = new Dictionary<int, JsonObject>();
        foreach (var obj in scene["objects"]?.AsArray().OfType<JsonObject>() ?? [])
            if (SceneGraph.Int(obj["id"]) is int id) objects[id] = obj;
        string groupId = Text(group["id"]) ?? "group";
        var reasons = new JsonArray();
        var perLayer = new JsonArray();
        var groupChecks = new JsonArray();
        bool closed = true;
        // 组捕获带上清屏色时，清屏色也是组输出的一部分。
        if (group["include_scene_clear"] is JsonValue clear && clear.TryGetValue<bool>(out bool included) && included)
        {
            bool ok = ScalarClosed(scene["general"]?["clearcolor"], properties, "scene clear color", out string clearDetail);
            groupChecks.Add(Check("R4", ok, clearDetail));
            if (!ok)
            {
                closed = false;
                reasons.Add($"{groupId} scene clear color: {clearDetail} (R4)");
            }
        }
        foreach (int id in Ids(group["layer_ids"]))
        {
            if (!objects.TryGetValue(id, out JsonObject? obj))
            {
                closed = false;
                reasons.Add($"{groupId} layer {id}: the captured layer is missing from the scene (R1)");
                perLayer.Add(new JsonObject { ["layer_id"] = id, ["status"] = "open",
                    ["checks"] = new JsonArray(Check("R1", false, "Layer is missing from the scene.")) });
                continue;
            }
            string name = Text(obj["name"]) ?? id.ToString(CultureInfo.InvariantCulture);
            // 明确解析为不可见、且没有任何动态绑定的层不参与绘制；其余一律参与判据。
            if (SceneGraph.Resolve(obj["visible"], properties) is JsonValue visible &&
                visible.TryGetValue<bool>(out bool shown) && !shown)
            {
                perLayer.Add(new JsonObject { ["layer_id"] = id, ["layer_name"] = name, ["status"] = "not_drawn",
                    ["checks"] = new JsonArray(Check("R0", true, "Layer visibility resolves to false.")) });
                continue;
            }
            if (!obj.ContainsKey("image") && !obj.ContainsKey("particle") && !obj.ContainsKey("text"))
            {
                perLayer.Add(new JsonObject { ["layer_id"] = id, ["layer_name"] = name, ["status"] = "not_drawn",
                    ["checks"] = new JsonArray(Check("R0", true, "Layer has no drawable image, particle or text.")) });
                continue;
            }
            // 字面值为空、又没有脚本能写进文字的文字层画不出任何像素（name 常是分隔线），不参与判据；与 Composer.Draws 同口径。
            if (!obj.ContainsKey("image") && !obj.ContainsKey("particle") && Composer.EmptyText(obj, properties, scene))
            {
                perLayer.Add(new JsonObject { ["layer_id"] = id, ["layer_name"] = name, ["status"] = "not_drawn",
                    ["checks"] = new JsonArray(Check("R0", true, "Text layer has an empty literal value and no script can reach it to write text.")) });
                continue;
            }
            var checks = new JsonArray();
            EvaluateLayer(obj, properties, runtimeLayers, videoDecoders, source, assets, effectRules, checks);
            bool layerClosed = checks.OfType<JsonObject>().All(check => check["status"]?.GetValue<string>() == "pass");
            perLayer.Add(new JsonObject { ["layer_id"] = id, ["layer_name"] = name,
                ["status"] = layerClosed ? "closed" : "open", ["checks"] = checks });
            if (layerClosed) continue;
            closed = false;
            foreach (var failed in checks.OfType<JsonObject>().Where(check => check["status"]?.GetValue<string>() != "pass"))
                reasons.Add($"{groupId} layer {id} \"{name}\": {Text(failed["detail"])} ({Text(failed["rule"])})");
        }
        return new JsonObject {
            ["group_id"] = groupId,
            ["status"] = closed ? "closed" : "open",
            ["reasons"] = reasons,
            ["group_checks"] = groupChecks,
            ["per_layer"] = perLayer };
    }

    /// <summary>逐层跑五条判据；每条都把通过/不通过与原因写进 checks。</summary>
    private static void EvaluateLayer(JsonObject obj, JsonObject properties, JsonArray? runtimeLayers,
        JsonArray? videoDecoders, ProjectSource source, string? assets, EffectRangeRules effectRules, JsonArray checks)
    {
        int id = SceneGraph.Int(obj["id"]) ?? -1;
        var textures = new List<string>();
        // R1 材质封闭：只接受内置 SDR 着色器，且没有作者特效层或运行时 effect 材质。
        if (obj.ContainsKey("particle") || obj.ContainsKey("text"))
        {
            checks.Add(Check("R1", false, obj.ContainsKey("particle")
                ? "particle layers have no decidable output range"
                : "text layers have no decidable output range"));
            return;
        }
        // 作者特效：可见的每一个都须被特效值域规则表证明为输入的凸组合采样，否则整层判未知。
        var provenEffectShaders = new HashSet<string>(StringComparer.Ordinal);
        foreach (var effect in (obj["effects"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (SceneGraph.Resolve(effect["visible"], properties) is JsonValue effectVisible &&
                effectVisible.TryGetValue<bool>(out bool effectShown) && !effectShown) continue;
            if (effectRules.IsRangeClosed(effect, properties, source, assets, provenEffectShaders, out string effectDetail)) continue;
            checks.Add(Check("R1", false, "the layer carries authored effect layers: " + effectDetail));
            return;
        }
        var observed = runtimeLayers?.OfType<JsonObject>()
            .Where(layer => SceneGraph.Int(layer["owner"]) == id).ToArray() ?? [];
        if (observed.Length == 0)
        {
            checks.Add(Check("R1", false, "the runtime trace has no observed material for this layer"));
            return;
        }
        foreach (var layer in observed)
        {
            if (layer["has_effect_layer"] is JsonValue effectFlag && effectFlag.TryGetValue<bool>(out bool hasEffect) && hasEffect &&
                provenEffectShaders.Count == 0)
            {
                checks.Add(Check("R1", false, "the renderer reported an effect layer on this layer"));
                return;
            }
            if (layer["materials"] is not JsonArray materials || materials.Count == 0)
            {
                checks.Add(Check("R1", false, "the runtime trace reported no material for this layer"));
                return;
            }
            foreach (var material in materials.OfType<JsonObject>())
            {
                string? shader = Text(material["shader"]);
                // 特效链上的材质：已证明的特效着色器，或把特效链合回层的内置 SDR 着色器。
                // 它们的纹理是特效链自己的中间缓冲与权重遮罩，值域已由规则表的证明覆盖，不再进 R3/R5。
                if (Text(material["role"]) == "effect" && provenEffectShaders.Count > 0 && shader is not null &&
                    (provenEffectShaders.Contains(shader) || SdrRadianceCriteria.IsBuiltInSdrShader(shader)))
                    continue;
                if (!SdrRadianceCriteria.IsBuiltInSdrShader(shader))
                {
                    checks.Add(Check("R1", false, $"runtime shader \"{shader ?? "unknown"}\" is not a built-in SDR shader"));
                    return;
                }
                if (Text(material["role"]) != "source")
                {
                    checks.Add(Check("R1", false, $"runtime material role \"{Text(material["role"]) ?? "unknown"}\" is not a source material"));
                    return;
                }
                if (material["uses_audio_spectrum"] is JsonValue audio && audio.TryGetValue<bool>(out bool spectrum) && spectrum)
                {
                    checks.Add(Check("R1", false, "the runtime material consumes the audio spectrum"));
                    return;
                }
                textures.AddRange(Strings(material["textures"]));
            }
        }
        // 材质定义：着色器、combos 与混合模式必须可读且落在白名单内。
        string? image = Text(SceneGraph.Resolve(obj["image"], properties));
        if (image is null)
        {
            checks.Add(Check("R1", false, "the layer has no readable image model reference"));
            return;
        }
        JsonArray passes;
        try
        {
            JsonObject model = SceneAnalyzer.ReadResourceJson(source, assets, image);
            string? materialResource = Text(model["material"]);
            if (materialResource is null)
            {
                checks.Add(Check("R1", false, $"model \"{image}\" has no material reference"));
                return;
            }
            JsonObject material = SceneAnalyzer.ReadResourceJson(source, assets, materialResource);
            if (material["passes"] is not JsonArray declared || declared.Count == 0)
            {
                checks.Add(Check("R1", false, $"material \"{materialResource}\" declares no pass"));
                return;
            }
            passes = declared;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or System.Text.Json.JsonException or InvalidOperationException)
        {
            checks.Add(Check("R1", false, $"material metadata for \"{image}\" could not be read: {error.Message}"));
            return;
        }
        foreach (var pass in passes.OfType<JsonObject>())
        {
            string? shader = Text(pass["shader"]);
            if (!SdrRadianceCriteria.IsBuiltInSdrShader(shader))
            {
                checks.Add(Check("R1", false, $"material shader \"{shader ?? "unknown"}\" is not a built-in SDR shader"));
                return;
            }
            if (pass["combos"] is JsonObject combos)
                foreach (var (key, _) in combos)
                    if (!SdrRadianceCriteria.IsRangePreservingCombo(key))
                    {
                        checks.Add(Check("R1", false, $"material combo \"{key}\" is not a known range-preserving combo"));
                        return;
                    }
            textures.AddRange(Strings(pass["textures"]));
        }
        checks.Add(Check("R1", true, provenEffectShaders.Count == 0
            ? "built-in SDR shaders only, no effect layer and no unknown combo"
            : "built-in SDR shaders only, effect layers are proven convex resamplings, no unknown combo"));
        // R2 混合封闭：alpha 凸组合，上界不升。
        foreach (var pass in passes.OfType<JsonObject>())
        {
            string? blending = Text(pass["blending"]);
            if (!SdrRadianceCriteria.IsRangePreservingBlend(blending))
            {
                checks.Add(Check("R2", false, $"pass blending \"{blending ?? "unset"}\" is not a range-preserving blend"));
                return;
            }
        }
        JsonNode? blendMode = SceneGraph.Resolve(Field(obj, "colorBlendMode"), properties);
        if (blendMode is not null && !(Number(blendMode, out double modeValue) && SdrRadianceCriteria.IsNormalColorBlendMode(modeValue)))
        {
            checks.Add(Check("R2", false, $"colorBlendMode {blendMode.ToJsonString()} is not the normal (0) mode"));
            return;
        }
        checks.Add(Check("R2", true, "normal or translucent blending with colorBlendMode 0"));
        // R3 输入封闭 / R5 无反馈路径。
        foreach (string texture in textures.Distinct(StringComparer.Ordinal))
        {
            TextureFeedback feedback = SdrRadianceCriteria.Feedback(texture);
            if (feedback == TextureFeedback.Framebuffer)
            {
                checks.Add(Check("R5", false, $"the layer samples the current framebuffer through \"{texture}\""));
                return;
            }
            if (feedback == TextureFeedback.RenderTarget)
            {
                checks.Add(Check("R5", false, $"the layer samples a render target \"{texture}\" whose range is not decidable"));
                return;
            }
        }
        checks.Add(Check("R5", true, "no render-target or framebuffer feedback input"));
        foreach (string texture in textures.Distinct(StringComparer.Ordinal))
        {
            if (!TextureClosed(texture, source, assets, videoDecoders, out string detail))
            {
                checks.Add(Check("R3", false, detail));
                return;
            }
        }
        checks.Add(Check("R3", true, textures.Count == 0 ? "the layer samples no texture"
            : "every texture is an 8-bit unsigned container or an 8-bit SDR video decode"));
        // R4 标量封闭。
        foreach (var (field, label) in new[] { ("color", "color"), ("brightness", "brightness"), ("alpha", "alpha") })
        {
            if (ScalarClosed(Field(obj, field), properties, label, out string detail)) continue;
            checks.Add(Check("R4", false, detail));
            return;
        }
        checks.Add(Check("R4", true, "color, brightness and alpha are literal values within [0,1]"));
    }

    /// <summary>纹理输入是否 8bit 无符号：.tex 容器格式，或视频包对应的解码像素格式。</summary>
    private static bool TextureClosed(string texture, ProjectSource source, string? assets, JsonArray? videoDecoders, out string detail)
    {
        string resource = "materials/" + texture + ".tex";
        if (!TextureContainer.TryReadHeader(source, assets, resource, out TextureContainer.TextureHeader header, out string reason))
        {
            detail = $"texture \"{texture}\" container could not be decided: {reason}";
            return false;
        }
        if (!header.IsVideo)
        {
            if (!TextureContainer.IsEightBitUnsignedFormat(header.Format))
            {
                detail = $"texture \"{texture}\" uses TEX format {header.Format}, which is not a known 8-bit unsigned container";
                return false;
            }
            detail = $"texture \"{texture}\" is an 8-bit unsigned container";
            return true;
        }
        var stream = videoDecoders?.OfType<JsonObject>().FirstOrDefault(item => Text(item["resource_key"]) == texture);
        if (stream is null)
        {
            detail = $"video texture \"{texture}\" has no observed decoder instance in the runtime trace";
            return false;
        }
        if (stream["metadata_unknown"] is not JsonValue known || !known.TryGetValue<bool>(out bool unknown) || unknown)
        {
            detail = $"video texture \"{texture}\" reported unknown stream metadata";
            return false;
        }
        string? pixelFormat = Text(stream["pixel_format"]);
        if (!SdrRadianceCriteria.IsEightBitSdrPixelFormat(pixelFormat))
        {
            detail = $"video texture \"{texture}\" decodes as \"{pixelFormat ?? "unknown"}\", which is not a known 8-bit SDR pixel format";
            return false;
        }
        detail = $"video texture \"{texture}\" decodes as 8-bit SDR \"{pixelFormat}\"";
        return true;
    }

    /// <summary>标量是否为字面量且落在 [0,1]；script 或 animation 绑定一律判未知。</summary>
    private static bool ScalarClosed(JsonNode? raw, JsonObject properties, string label, out string detail)
    {
        JsonNode? value = SceneGraph.Resolve(raw, properties);
        if (value is null)
        {
            detail = $"{label} is unset";
            return true;
        }
        if (value is JsonObject binding)
        {
            string kind = binding.ContainsKey("script") ? "a script" :
                binding.ContainsKey("animation") || binding.ContainsKey("animations") ? "an animation" : "an unresolved binding";
            detail = $"{label} is bound to {kind}; its value range cannot be decided";
            return false;
        }
        var components = new List<double>();
        if (Number(value, out double number)) components.Add(number);
        else if (value is JsonValue text && text.TryGetValue<string>(out string? literal) &&
            !SdrRadianceCriteria.TryParseComponents(literal, components))
        {
            detail = $"{label} value \"{literal}\" is not a numeric literal";
            return false;
        }
        if (components.Count == 0)
        {
            detail = $"{label} value {value.ToJsonString()} is not a decidable numeric literal";
            return false;
        }
        foreach (double component in components)
            if (!SdrRadianceCriteria.IsWithinUnitRange(component))
            {
                detail = $"{label} component {component.ToString("R", CultureInfo.InvariantCulture)} is outside [0,1]";
                return false;
            }
        detail = $"{label} is a literal within [0,1]";
        return true;
    }

    private static JsonObject Check(string rule, bool ok, string detail) =>
        new() { ["rule"] = rule, ["status"] = ok ? "pass" : "fail", ["detail"] = detail };

    /// <summary>场景 JSON 的键名大小写在不同作者之间并不统一，按大小写不敏感取字段。</summary>
    private static JsonNode? Field(JsonObject obj, string name)
    {
        foreach (var (key, value) in obj)
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase)) return value;
        return null;
    }

    /// <summary>数值可能来自 JSON 解析，也可能是内存里构造的 int/long/float 节点，逐一试取。</summary>
    private static bool Number(JsonNode? node, out double value)
    {
        value = 0;
        if (node is not JsonValue item) return false;
        if (item.TryGetValue(out double asDouble)) { value = asDouble; return true; }
        if (item.TryGetValue(out long asLong)) { value = asLong; return true; }
        if (item.TryGetValue(out int asInt)) { value = asInt; return true; }
        if (item.TryGetValue(out float asFloat)) { value = asFloat; return true; }
        if (item.TryGetValue(out decimal asDecimal)) { value = (double)asDecimal; return true; }
        return false;
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out string? text) ? text : null;

    private static IEnumerable<int> Ids(JsonNode? node) =>
        (node as JsonArray)?.Select(item => SceneGraph.Int(item)).OfType<int>() ?? [];

    private static IEnumerable<string> Strings(JsonNode? node) =>
        (node as JsonArray)?.Select(Text).OfType<string>().Where(text => text.Length > 0) ?? [];
}
