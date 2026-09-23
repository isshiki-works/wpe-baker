using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static Baker.Core.SceneGraph;

namespace Baker.Core;

/// <summary>
/// 写 plan：按各阶段的结果组出 plan v3 的字段（字段顺序即 v3 的字节顺序），路线与裁定定稿之后挂来源记录、
/// 双语字段与一行结论，最后落盘。初判拒因只在 <see cref="Compose"/> 里由 <see cref="Verdict"/> 渲染一次。
/// </summary>
internal static class PlanWriter
{
    // 只用于标注可疑的广告/二维码/水印图层，从不自动剔除任何东西。
    private static readonly Regex OverlayVocabulary = new(
        @"\b(ads?|advert\w*|qr\w*|donate|donation|watermark|logo|signature|credit)\b|广告|二维码|捐赠|打赏|水印|署名|关注",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>组 plan（原 V 段）：只读各阶段结果，不做判定。</summary>
    internal static JsonObject Compose(HybridAnalyzeRequest request, ProjectSource source, string sourceHash, string output,
        OutputResolution.Choice resolution, JsonObject analysisDevice, JsonObject project, JsonObject properties, bool parallax,
        JsonObject projection, SceneGraph graph, RuntimeObservation observation, Liveness liveness, Allocation allocation, Composer composer,
        Verdict verdict, JsonObject loop, JsonArray effectPrefixCaches, DaytimeSplit.Detection? daytime)
    {
        var objects = graph.Objects;
        int[] sourceOrder = graph.SourceOrder;
        var (reasons, liveIds) = (liveness.Reasons, allocation.LiveIds);
        JsonArray dependencies = observation.Dependencies;
        bool effectPrefixRoute = effectPrefixCaches.Count > 0;
        double canvasWidth = projection["canvas_width"]!.GetValue<double>(), canvasHeight = projection["canvas_height"]!.GetValue<double>();
        var groupOfRoot = composer.Groups.OfType<JsonObject>().SelectMany(group => group["root_ids"]!.AsArray()
            .Select(root => (Root: root!.GetValue<int>(), Group: group))).ToDictionary(item => item.Root, item => item.Group);
        // 给用户看的图层清单：几何占比、可见性绑定与分配去向，外加只标注不改动的覆盖层嫌疑。
        double centerX = Numeric(projection["center_x"], canvasWidth / 2), centerY = Numeric(projection["center_y"], canvasHeight / 2);
        // 面积只由尺寸与整条缩放链决定，父链旋转不改变面积；位置需要完整的父变换，父链带旋转时位置写 null
        // 而不是假的 0.5。算不出的量一律写 null：写 0 会让下游把"未知"当成"零面积"（残差掩盖的 25% 精灵占比闸门
        // 就曾因此对旋转父链下的精灵失效）。
        (double? Fraction, double? X, double? Y) Placement(int id)
        {
            double area = canvasWidth * canvasHeight;
            if (!double.IsFinite(area) || area <= 0) return (null, null, null);
            double? fraction = null, x = null, y = null;
            try
            {
                var scale = HybridVideoProjection.Vector(Resolve(objects[id]["scale"], properties), (1, 1));
                (double X, double Y) chainScale = (1, 1);
                var seen = new HashSet<int> { id };
                int? ancestor = Int(objects[id]["parent"]);
                while (ancestor is int parentId && objects.TryGetValue(parentId, out JsonObject? parentObject) && seen.Add(parentId))
                {
                    var parentScale = HybridVideoProjection.Vector(Resolve(parentObject["scale"], properties), (1, 1));
                    chainScale = (chainScale.X * parentScale.X, chainScale.Y * parentScale.Y);
                    ancestor = Int(parentObject["parent"]);
                }
                if (objects[id]["size"] is not null)
                {
                    var size = HybridVideoProjection.Vector(Resolve(objects[id]["size"], properties), (0, 0));
                    double coverage = Math.Abs(size.X * scale.X * chainScale.X * size.Y * scale.Y * chainScale.Y) / area;
                    // 包围盒伸出画布的部分不可见，占比封顶 1。
                    if (double.IsFinite(coverage)) fraction = Math.Min(1, coverage);
                }
            }
            catch (InvalidDataException) { }
            try
            {
                var origin = HybridVideoProjection.Vector(Resolve(objects[id]["origin"], properties), (0, 0));
                JsonObject? inherited = Int(objects[id]["parent"]) is int parent && objects.ContainsKey(parent)
                    ? HybridVideoProjection.ParentTransform(objects, parent, properties) : null;
                var parentOrigin = HybridVideoProjection.Vector(inherited?["origin"], (0, 0));
                var parentScale = HybridVideoProjection.Vector(inherited?["scale"], (1, 1));
                double centreX = .5 + (parentOrigin.X + parentScale.X * origin.X - centerX) / canvasWidth;
                double centreY = .5 + (parentOrigin.Y + parentScale.Y * origin.Y - centerY) / canvasHeight;
                if (double.IsFinite(centreX) && double.IsFinite(centreY)) (x, y) = (centreX, centreY);
            }
            catch (InvalidDataException) { }
            return (fraction, x, y);
        }
        var compositeTargets = dependencies.OfType<JsonObject>().Where(d => d["property"]?.GetValue<string>() == "layerComposite")
            .Select(d => Int(d["target"])).OfType<int>().ToHashSet();
        string Kind(int id) => objects[id].ContainsKey("particle") ? "particle" : objects[id].ContainsKey("text") ? "text" :
            compositeTargets.Contains(id) ? "composite" : objects[id].ContainsKey("image") ? "image" : "other";
        string VisibleBinding(int id) => objects[id]["visible"] is not JsonObject binding ? "constant" :
            binding.ContainsKey("script") ? "script" :
            Animated(binding) ? "animation" :
            binding.ContainsKey("user") ? "user_property" : "constant";
        string Name(int id) => objects[id]["name"] is JsonValue value && value.TryGetValue<string>(out string? text) ? text : "";
        // 可取舍元素怎么关：这一层的显示开关绑在哪个壁纸属性上、当前值多少、给什么值算关。
        // 本层没绑就沿父链找最近一个绑了属性的祖先——关掉祖先会连带关掉这一层，对用户同样是"在 WPE 里关一个开关"。
        // 推不出来时写明原因（没绑属性 / project.json 没声明 / 属性不是开关），不猜。
        JsonObject VisibleProperty(int id)
        {
            int owner = id;
            (string Name, string? Condition)? bound = null;
            var visited = new HashSet<int>();
            for (int? cursor = id; cursor is int current && objects.ContainsKey(current) && visited.Add(current);
                cursor = Int(objects[current]["parent"]))
                if (PlanNarrative.BoundProperty(objects[current]["visible"]) is { } found) { (bound, owner) = (found, current); break; }
            if (bound is not { } binding) return new JsonObject { ["status"] = "not_bound", ["visible_binding"] = VisibleBinding(id) };
            string key = binding.Name;
            var definition = project["general"]?["properties"]?[key] as JsonObject;
            string type = definition?["type"] is JsonValue typeValue && typeValue.TryGetValue<string>(out string? typeText) ? typeText : "";
            JsonNode? value = properties.TryGetPropertyValue(key, out JsonNode? saved) ? saved?.DeepClone() : definition?["value"]?.DeepClone();
            var record = new JsonObject {
                ["key"] = key, ["label"] = definition?["text"]?.DeepClone(), ["type"] = definition is null ? null : type,
                ["declared"] = definition is not null, ["binding"] = owner == id ? "self" : "ancestor",
                ["bound_layer_id"] = owner, ["condition"] = binding.Condition, ["current_value"] = value };
            static string Scalar(JsonNode? node) => node is JsonValue text && text.TryGetValue<string>(out string? plain)
                ? plain : node?.ToJsonString() ?? "null";
            if (binding.Condition is string condition)
            {
                // 带 condition 的绑定：属性值等于 condition 时才显示，给它任何别的值都是关。
                JsonNode? other = (definition?["options"] as JsonArray)?.OfType<JsonObject>()
                    .Select(option => option["value"]).FirstOrDefault(option => Scalar(option) != condition);
                record["off_value"] = other?.DeepClone();
                record["status"] = "conditional";
                record["off_hint_zh"] = other is null ? $"把 \"{key}\" 设成任何不等于 \"{condition}\" 的值"
                    : $"把 \"{key}\" 设成 {other.ToJsonString()}";
                record["off_hint_en"] = other is null ? $"give \"{key}\" any value other than \"{condition}\""
                    : $"set \"{key}\" to {other.ToJsonString()}";
                return record;
            }
            if (definition is not null && type != "bool")
            {
                // 显示直接取属性值，而这个属性不是开关：关闭值推不出来，如实写明。
                record["status"] = "type_not_boolean";
                record["off_hint_zh"] = $"属性 \"{key}\" 是 {type} 类型，不是开关，关闭值要自己判断";
                record["off_hint_en"] = $"the \"{key}\" property is a {type}, not a switch; its off value must be judged manually";
                return record;
            }
            record["off_value"] = false;
            record["status"] = "resolved";
            record["off_hint_zh"] = $"关闭值 false，--properties 文件写 {{\"{key}\": false}}";
            record["off_hint_en"] = $"the off value is false; write {{\"{key}\": false}} in the --properties file";
            return record;
        }
        string[] OverlaySuspicion(int id, double? fraction, double? x, double? y)
        {
            var found = new List<string>();
            if (OverlayVocabulary.IsMatch(Name(id))) found.Add("name_matches_overlay_vocabulary");
            if (objects[id].ContainsKey("image") && fraction is > 0 and < .12 &&
                x is <= .2 or >= .8 && y is <= .2 or >= .8) found.Add("small_image_in_canvas_corner");
            if (objects[id]["visible"] is JsonObject visibility && visibility["script"] is JsonValue script &&
                script.TryGetValue<string>(out string? code) &&
                Regex.IsMatch(Liveness.CapabilityScanText(code), @"\b(setTimeout|setInterval|Date)\b")) found.Add("timer_driven_visibility");
            return found.ToArray();
        }
        var report = new JsonObject {
            ["schema_version"] = HybridPlanFormat.CurrentVersion, ["kind"] = "hybrid_video", ["route"] = effectPrefixRoute ? "effect_prefix" : "whole_layer",
            ["status"] = effectPrefixRoute || verdict.Blockers.Count == 0 ? "requires_loop_analysis" : "requires_resolution",
            ["source"] = source.SourcePath, ["source_sha256"] = sourceHash, ["source_digest_scope"] = ProjectSource.DigestScope,
            ["assets"] = Path.GetFullPath(request.Assets), ["analysis_directory"] = output,
            ["settings"] = PlanSettings.ToJson(request),
            // 档位与两个高级覆盖合成的生效值，每个值带来源（preset/override/default）；求解器与 bake 读的都是这一份。
            ["retime_profile"] = RetimeProfileJson.ToJson(RetimeProfileJson.Resolve(request), SwayRecurrenceSolver.SpeedLimitScale(request.Width, request.Height)),
            ["output_resolution"] = resolution.ToJson(),
            ["analysis_device"] = analysisDevice.DeepClone(),
            ["canvas_width"] = canvasWidth, ["canvas_height"] = canvasHeight,
            ["projection"] = projection,
            ["snapshot_properties"] = properties, ["has_parallax"] = parallax,
            ["video_groups"] = composer.Groups, ["composition"] = composer.Composition,
            ["live_layer_ids"] = JsonSerializer.SerializeToNode(sourceOrder.Where(allocation.LiveIds.Contains)),
            ["omitted_snapshot_layer_ids"] = JsonSerializer.SerializeToNode(sourceOrder.Where(allocation.OmittedIds.Contains)),
            ["excluded_layer_ids"] = JsonSerializer.SerializeToNode(allocation.ExcludedRoots),
            ["fixed_user_properties"] = JsonSerializer.SerializeToNode(allocation.FixedProperties),
            ["root_order"] = JsonSerializer.SerializeToNode(composer.RootOrder),
            ["source_root_order"] = JsonSerializer.SerializeToNode(graph.Roots),
            ["source_allocation_order"] = JsonSerializer.SerializeToNode(allocation.Order),
            ["occlusion_tradeoff"] = composer.OcclusionTradeoff,
            ["text_effects_choice"] = composer.TextEffectChoice,
            ["audio_effects_choice"] = observation.AudioEffectChoice,
            ["root_roles"] = new JsonArray(composer.RootOrder.Select((root, index) => (JsonNode)new JsonObject {
                ["root_id"] = root, ["source_order"] = index,
                ["role"] = allocation.OmittedIds.Contains(root) ? "omitted_snapshot" : allocation.LiveUnits.Contains(root) ? "live" :
                    groupOfRoot.ContainsKey(root) ? "video" : "inactive",
                ["video_group"] = groupOfRoot.GetValueOrDefault(root)?["id"]?.DeepClone(),
                ["parallax_depth"] = new JsonArray(allocation.Depths[root].X, allocation.Depths[root].Y) }).ToArray()),
            ["layers"] = new JsonArray(sourceOrder.Select(id => {
                var (fraction, x, y) = Placement(id);
                string[] suspicion = OverlaySuspicion(id, fraction, x, y);
                return (JsonNode)new JsonObject {
                    ["id"] = id, ["root"] = graph.RootOf[id], ["allocation_root"] = allocation.UnitOf[id],
                    ["parent"] = objects[id]["parent"]?.DeepClone(), ["source_order"] = Array.IndexOf(sourceOrder, id),
                    ["name"] = objects[id]["name"]?.DeepClone(),
                    ["kind"] = Kind(id), ["has_image"] = objects[id].ContainsKey("image"),
                    ["canvas_fraction"] = fraction is double coverage ? Math.Round(coverage, 6) : null,
                    ["canvas_center_x"] = x is double centreX ? Math.Round(centreX, 6) : null,
                    ["canvas_center_y"] = y is double centreY ? Math.Round(centreY, 6) : null,
                    ["quadrant"] = x is not double qx || y is not double qy ? "unknown"
                        : qx is > .4 and < .6 && qy is > .4 and < .6 ? "center"
                        : (qy >= .5 ? "top_" : "bottom_") + (qx < .5 ? "left" : "right"),
                    ["visible_binding"] = VisibleBinding(id),
                    ["allocation"] = allocation.ExcludedIds.Contains(id) ? "excluded" : allocation.OmittedIds.Contains(id) ? "omitted" :
                        liveIds.Contains(id) ? "live" : groupOfRoot.ContainsKey(allocation.UnitOf[id]) ? "video" : "inactive",
                    ["suspected_overlay"] = suspicion.Length > 0,
                    ["suspected_overlay_reasons"] = JsonSerializer.SerializeToNode(suspicion),
                    ["live"] = liveIds.Contains(id), ["reasons"] = JsonSerializer.SerializeToNode(reasons[id]),
                    // 取舍清单用的两个标签与关闭办法：只给实时层，其余层这三个字段为 null。
                    ["tradeoff_class"] = liveIds.Contains(id) ? TradeoffOptions.Classify(reasons[id], suspectedOverlay: suspicion.Length > 0).Class : null,
                    ["tradeoff_kinds"] = liveIds.Contains(id)
                        ? JsonSerializer.SerializeToNode(TradeoffOptions.Classify(reasons[id], suspectedOverlay: suspicion.Length > 0).Kinds) : null,
                    ["visible_property"] = liveIds.Contains(id) ? VisibleProperty(id) : null,
                    ["visible"] = composer.Visible(id), ["drawable"] = composer.Draws(id) };
            }).ToArray()),
            ["optional_realtime_roots"] = JsonSerializer.SerializeToNode(composer.OptionalForeground),
            ["hdr_radiance_closure"] = verdict.RadianceClosure,
            ["composition_policy"] = "Keep adjacent input-independent content together; optional live foreground must not split a video group or change draw order.",
            ["blockers"] = new JsonArray(), ["loop"] = loop,
            ["whole_layer"] = Routes.WholeLayer(verdict.Blockers, loop),
            ["effect_prefix_caches"] = effectPrefixCaches,
            ["encoding"] = new JsonObject { ["codec"] = "auto_h264_hevc", ["pixel_format"] = "yuv420p", ["crf"] = 16,
                ["color_space"] = "bt709_sdr", ["transparent_groups"] = "rgb_contribution_and_coverage_side_by_side",
                ["selection_basis"] = "H.264 within 4096 pixels and level 5.2 frame/rate limits; HEVC otherwise. Final cropped stream determines selection; target hardware decoding and playback still require verification." },
            ["runtime_evidence"] = Path.Combine(output, "runtime.json"),
            ["source_script_error_evidence"] = new JsonObject {
                ["status"] = verdict.ScriptFaultEvidence ? "available" : "not_available",
                ["reason"] = verdict.ScriptFaultEvidence ? null : Verdict.MissingScriptFaultEvidenceBlocker.Text },
            ["source_script_error_count"] = verdict.ScriptErrorCount is int recordedErrorCount ? recordedErrorCount : null,
            ["source_script_errors"] = verdict.ScriptFaultEvidence ? verdict.SourceScriptErrors.DeepClone() : null,
            ["official_playback"] = "not_verified", ["measured_gain"] = "not_verified" };
        // 初判拒因在这里渲染一次：整层路线写进 blockers（特效前缀路线为空），whole_layer 始终记整层的那一份。
        // 特效前缀路线的 HDR 闭合不在这里写：路线准入还可能改走前缀，定稿后统一按前缀捕获对象重求（Verdict.ApplyPrefixRadianceClosure），
        // 届时 hdr_radiance_closure 换成那次求值，这里的整层求值挪到 hdr_radiance_closure_initial。
        PlanBlockers.Set(report, effectPrefixRoute ? [] : verdict.Blockers);
        // 开关关着时不写这一段，plan 逐字不变。
        if (daytime is not null) report["daytime_split"] = daytime.ToJson();
        return report;
    }

    /// <summary>
    /// 落盘（原 X 段末尾）：来源记录插回固定位置，挂双语字段与一行结论，写 plan.json。
    /// 双语字段与一行结论只读已经定好的 blockers / loop / candidates，不参与任何判定。
    /// </summary>
    /// <param name="notes">loop.unresolved 各条的文案与点名图层（分析编排带来，渲染进 unresolved_localized 与一行结论）。</param>
    internal static async Task WriteAsync(JsonObject report, HybridAnalyzeRequest request, string output, UnresolvedNotes? notes,
        CancellationToken cancellationToken)
    {
        // 属性来源紧跟在 snapshot_properties 后面：没有来源记录的请求（测试、内部重分析）plan 不变。
        if (request.PropertiesOrigin is JsonObject propertiesOrigin) AttachPropertiesSource(report, propertiesOrigin);
        // 帧率来源紧跟在 output_resolution 后面：没有来源记录的请求（测试、内部重分析）plan 不变。
        if (request.FrameRateOrigin is JsonObject frameRateOrigin) AttachFrameRate(report, frameRateOrigin);
        PlanBlockers.PlaceLocalizedLast(report);
        if (report["whole_layer"] is JsonObject wholeLayer) PlanBlockers.PlaceLocalizedLast(wholeLayer);
        PlanNarrative.Attach(report, notes);
        await VideoSceneBuilder.WriteJsonAsync(Path.Combine(output, "plan.json"), report, cancellationToken);
    }

    /// <summary>把属性来源写成 plan 的 properties_source（wpe / defaults / wpe_unavailable）与 wpe_properties（原因、配置位置、生效键）。</summary>
    internal static void AttachPropertiesSource(JsonObject report, JsonObject origin)
    {
        var detail = origin.DeepClone().AsObject();
        JsonNode source = detail["source"]?.DeepClone() ?? WallpaperEngineProperties.SourceDefaults;
        detail.Remove("source");
        report.Remove("properties_source");
        report.Remove("wpe_properties");
        int at = report.IndexOf("snapshot_properties");
        if (at < 0) { report["properties_source"] = source; report["wpe_properties"] = detail; return; }
        report.Insert(at + 1, "properties_source", source);
        report.Insert(at + 2, "wpe_properties", detail);
    }

    /// <summary>把帧率来源写成 plan 的 frame_rate（explicit / auto、两条依据与选中值），位置紧挨 output_resolution。</summary>
    internal static void AttachFrameRate(JsonObject report, JsonObject origin)
    {
        var detail = origin.DeepClone().AsObject();
        report.Remove("frame_rate");
        int at = report.IndexOf("output_resolution");
        if (at < 0) report["frame_rate"] = detail;
        else report.Insert(at + 1, "frame_rate", detail);
    }
}
