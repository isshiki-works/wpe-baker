using System.Text.Json.Nodes;
using Baker.Core;

/// <summary>效果前缀终端捕获点的归属裁定：只认这一层自己的渲染目标，落到共用缓冲就不生成前缀并给出点名理由。</summary>
internal static class EffectPrefixCaptureTargetChecks
{
    // 结构取自真实探测：3757825891 的 watermark 层唯一效果 scroll 直接画进 _rt_default，
    // 3465215190 的层在 waterwaves 之后还有渲染器补的合成 pass，终端写进自己的 layer composite。
    private static JsonObject Probe(string? renderTarget, int owner, params string[] effectTextures)
    {
        var materials = new JsonArray(new JsonObject { ["shader"] = "genericimage4", ["role"] = "source",
            ["textures"] = new JsonArray("watermark", "") });
        foreach (string texture in effectTextures)
            materials.Add(new JsonObject { ["shader"] = "effects/scroll", ["role"] = "effect", ["textures"] = new JsonArray(texture) });
        return new JsonObject
        {
            ["capture_source"] = renderTarget is null ? null : new JsonObject { ["render_target"] = renderTarget, ["pass"] = "effects/scroll",
                ["texture_version"] = 36, ["width"] = 64, ["height"] = 64 },
            ["runtime_layers"] = new JsonArray(
                new JsonObject { ["id"] = owner, ["owner"] = owner, ["materials"] = materials },
                new JsonObject { ["id"] = 170, ["owner"] = 170, ["materials"] = new JsonArray(new JsonObject {
                    ["shader"] = "effects/waterripple", ["role"] = "effect", ["textures"] = new JsonArray("_rt_node_50_layer_composite") }) })
        };
    }

    internal static void Run(Action<bool, string> check)
    {
        JsonObject sceneWide = EffectPrefixCaptureTarget.Evaluate(Probe("_rt_default", 176, "_rt_node_56_layer_composite"), 176, 185, "watermark");
        string reason = sceneWide["reason"]?.GetValue<string>() ?? "";
        JsonObject localized = sceneWide["reason_localized"]!.AsObject();
        check(sceneWide["status"]?.GetValue<string>() == EffectPrefixCaptureTarget.RejectedStatus &&
            sceneWide["render_target"]?.GetValue<string>() == "_rt_default" &&
            sceneWide["layer_targets"]!.AsArray().Select(node => node!.GetValue<string>()).SequenceEqual(["_rt_node_56_layer_composite"]),
            "a terminal capture from the scene framebuffer is rejected and records the layer's own targets");
        check(reason.Contains("\"watermark\" (id 176)", StringComparison.Ordinal) && reason.Contains("terminal effect 185", StringComparison.Ordinal) &&
            reason.Contains("_rt_default", StringComparison.Ordinal) && reason.Contains("_rt_node_56_layer_composite", StringComparison.Ordinal),
            "the capture-target rejection names the layer, the terminal effect, the capture point and the layer target");
        check(localized["key"]?.GetValue<string>() == "effect_prefix.capture_not_layer_target" &&
            localized["zh"]!.GetValue<string>().Contains("不生成图层 \"watermark\"（id 176）的效果前缀缓存", StringComparison.Ordinal) &&
            localized["zh"]!.GetValue<string>().Contains("_rt_default", StringComparison.Ordinal) &&
            localized["en"]!.GetValue<string>().Contains("\"watermark\" (id 176)", StringComparison.Ordinal) &&
            localized["en"]!.GetValue<string>().Contains("terminal effect 185 is captured from _rt_default", StringComparison.Ordinal) &&
            localized["en"]!.GetValue<string>().Contains("_rt_node_56_layer_composite", StringComparison.Ordinal),
            "the capture-target rejection carries bilingual text from the messages table");

        JsonObject own = EffectPrefixCaptureTarget.Evaluate(Probe("_rt_node_39_layer_composite", 223, "_rt_node_39_layer_composite", "_rt_node_39_layer_composite"), 223, 224, "线4");
        check(own["status"]?.GetValue<string>() == EffectPrefixCaptureTarget.LayerTargetStatus && own["reason"] is null &&
            own["render_target"]?.GetValue<string>() == "_rt_node_39_layer_composite",
            "a terminal capture from the layer's own composite is accepted without a rejection reason");

        JsonObject foreign = EffectPrefixCaptureTarget.Evaluate(Probe("_rt_node_50_layer_composite", 176, "_rt_node_56_layer_composite"), 176, 185, "watermark");
        check(foreign["status"]?.GetValue<string>() == EffectPrefixCaptureTarget.RejectedStatus,
            "a terminal capture from another layer's composite is not the prefix owner's target");

        JsonObject sharedRead = EffectPrefixCaptureTarget.Evaluate(Probe("_rt_FullFrameBuffer", 176, "_rt_FullFrameBuffer", "_rt_node_56_layer_composite"), 176, 185, "watermark");
        check(sharedRead["status"]?.GetValue<string>() == EffectPrefixCaptureTarget.RejectedStatus &&
            !sharedRead["layer_targets"]!.AsArray().Any(node => node!.GetValue<string>() == "_rt_FullFrameBuffer"),
            "a scene-wide buffer never counts as the layer's own target even when the layer's effect reads it");

        JsonObject missing = EffectPrefixCaptureTarget.Evaluate(Probe(null, 176, "_rt_node_56_layer_composite"), 176, 185, "watermark");
        check(missing["status"]?.GetValue<string>() == EffectPrefixCaptureTarget.RejectedStatus &&
            missing["reason"]!.GetValue<string>().Contains("(none)", StringComparison.Ordinal),
            "a probe without a confirmed capture point cannot prove the layer's own target");

        JsonObject failed = EffectPrefixCaptureTarget.ProbeFailed(176, 185, "watermark", "terminal capture effect has no unique graph texture writer");
        check(failed["status"]?.GetValue<string>() == EffectPrefixCaptureTarget.ProbeFailedStatus &&
            failed["reason_localized"]?["key"]?.GetValue<string>() == "effect_prefix.capture_probe_failed" &&
            failed["reason_localized"]!["zh"]!.GetValue<string>().Contains("no unique graph texture writer", StringComparison.Ordinal),
            "a failed capture probe does not admit the prefix and keeps the renderer's message");

        var scene = new JsonObject { ["objects"] = new JsonArray(new JsonObject { ["id"] = 176, ["name"] = "watermark" }, new JsonObject { ["id"] = 17 }) };
        check(EffectPrefixCaptureTarget.LayerName(scene, 176) == "watermark" && EffectPrefixCaptureTarget.LayerName(scene, 17) is null &&
            EffectPrefixCaptureTarget.LayerName(scene, 999) is null, "layer names come from the authored scene and stay null when absent");
    }
}
