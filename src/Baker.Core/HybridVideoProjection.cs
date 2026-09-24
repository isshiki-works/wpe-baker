using System.Globalization;
using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>Maps a fixed orthographic capture to source-world video geometry.</summary>
internal static class HybridVideoProjection
{
    internal static bool SupportsStaticParent(JsonObject obj, JsonObject properties)
    {
        try
        {
            var angles = Vector3(SceneGraph.Resolve(obj["angles"], properties), (0, 0, 0));
            var scale = Vector3(SceneGraph.Resolve(obj["scale"], properties), (1, 1, 1));
            _ = Vector3(SceneGraph.Resolve(obj["origin"], properties), (0, 0, 0));
            return angles == (0d, 0d, 0d) && Math.Abs(scale.X) > 1e-9 && Math.Abs(scale.Y) > 1e-9 && Math.Abs(scale.Z) > 1e-9;
        }
        catch (InvalidDataException) { return false; }
    }

    internal static JsonObject ParentTransform(IReadOnlyDictionary<int, JsonObject> objects, int parent, JsonObject properties)
    {
        var chain = new Stack<JsonObject>();
        var seen = new HashSet<int>();
        int id = parent;
        while (objects.TryGetValue(id, out var obj))
        {
            if (!seen.Add(id) || !SupportsStaticParent(obj, properties))
                throw new InvalidDataException("Video parent mapping requires a static axis-aligned, non-singular parent chain.");
            chain.Push(obj);
            if (SceneGraph.Int(obj["parent"]) is not int next || !objects.ContainsKey(next)) break;
            id = next;
        }
        (double X, double Y, double Z) origin = (0, 0, 0), scale = (1, 1, 1);
        foreach (var obj in chain)
        {
            var localOrigin = Vector3(SceneGraph.Resolve(obj["origin"], properties), (0, 0, 0));
            var localScale = Vector3(SceneGraph.Resolve(obj["scale"], properties), (1, 1, 1));
            origin = (origin.X + scale.X * localOrigin.X, origin.Y + scale.Y * localOrigin.Y, origin.Z + scale.Z * localOrigin.Z);
            scale = (scale.X * localScale.X, scale.Y * localScale.Y, scale.Z * localScale.Z);
        }
        if (new[] { origin.X, origin.Y, origin.Z, scale.X, scale.Y, scale.Z }.Any(value => !double.IsFinite(value)) ||
            Math.Abs(scale.X) <= 1e-9 || Math.Abs(scale.Y) <= 1e-9 || Math.Abs(scale.Z) <= 1e-9)
            throw new InvalidDataException("Composed video parent transform is non-finite or singular.");
        return new JsonObject { ["origin"] = new JsonArray(origin.X, origin.Y, origin.Z), ["scale"] = new JsonArray(scale.X, scale.Y, scale.Z) };
    }

    internal static void AttachToParent(JsonObject layer, JsonObject group)
    {
        if (SceneGraph.Int(group["parent_id"]) is not int parent) return;
        var transform = group["parent_transform"]?.AsObject() ?? throw new InvalidDataException("Video parent transform is missing.");
        var origin = Vector3(transform["origin"], (0, 0, 0));
        var scale = Vector3(transform["scale"], (1, 1, 1));
        var worldOrigin = Vector3(layer["origin"], (0, 0, 0));
        var worldScale = Vector3(layer["scale"], (1, 1, 1));
        if (Math.Abs(scale.X) <= 1e-9 || Math.Abs(scale.Y) <= 1e-9 || Math.Abs(scale.Z) <= 1e-9)
            throw new InvalidDataException("Video parent transform is singular.");
        layer["origin"] = FormattableString.Invariant($"{(worldOrigin.X - origin.X) / scale.X:R} {(worldOrigin.Y - origin.Y) / scale.Y:R} {(worldOrigin.Z - origin.Z) / scale.Z:R}");
        layer["scale"] = FormattableString.Invariant($"{worldScale.X / scale.X:R} {worldScale.Y / scale.Y:R} {worldScale.Z / scale.Z:R}");
        layer["parent"] = parent;
    }

    internal readonly record struct CaptureViewportExtent(double Width, double Height);

    /// <summary>Returns the world-space extent needed to capture one group without parallax edge exposure.</summary>
    internal static CaptureViewportExtent CaptureViewportForGroup(JsonObject projection, JsonObject group, bool preserveParallax)
    {
        double visibleWidth = projection["visible_width"]!.GetValue<double>(), visibleHeight = projection["visible_height"]!.GetValue<double>();
        if (!preserveParallax) return new(visibleWidth, visibleHeight);
        double depthX = group["parallax_depth"]![0]!.GetValue<double>(), depthY = group["parallax_depth"]![1]!.GetValue<double>();
        double amount = projection["parallax_amount"]!.GetValue<double>(), mouse = projection["parallax_mouse_influence"]!.GetValue<double>();
        double extraX = projection["canvas_width"]!.GetValue<double>() * Math.Abs(mouse * depthX * amount);
        double extraY = projection["canvas_height"]!.GetValue<double>() * Math.Abs(mouse * depthY * amount);
        return new(visibleWidth + extraX, visibleHeight + extraY);
    }

    /// <summary>Returns the shared extent covering every group that can appear in a composited seam check.</summary>
    internal static CaptureViewportExtent MaximumCaptureViewport(JsonObject projection, IEnumerable<JsonObject> groups, bool preserveParallax)
    {
        CaptureViewportExtent? result = null;
        foreach (JsonObject group in groups)
        {
            CaptureViewportExtent viewport = CaptureViewportForGroup(projection, group, preserveParallax);
            result = result is null ? viewport : new(Math.Max(result.Value.Width, viewport.Width), Math.Max(result.Value.Height, viewport.Height));
        }
        return result ?? throw new InvalidDataException("A seam capture requires at least one video group.");
    }

    private static (double X, double Y, double Z) Vector3(JsonNode? node, (double X, double Y, double Z) fallback)
    {
        if (node is JsonObject binding) node = binding["value"];
        if (node is null) return fallback;
        double[] values;
        if (node is JsonArray array) values = array.Select(value => value!.GetValue<double>()).ToArray();
        else if (node is JsonValue value && value.TryGetValue<string>(out string? text))
        {
            var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            values = new double[words.Length];
            for (int i = 0; i < words.Length; i++)
                if (!double.TryParse(words[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]))
                    throw new InvalidDataException("Invalid parent transform vector.");
        }
        else throw new InvalidDataException("Invalid parent transform vector.");
        if (values.Length != 3 || values.Any(value => !double.IsFinite(value))) throw new InvalidDataException("Invalid parent transform vector.");
        return (values[0], values[1], values[2]);
    }

    internal const string CanvasBasisOrthographic = "orthogonalprojection";
    internal const string CanvasBasisAutoLargestImage = "orthogonalprojection_auto_largest_image";
    internal const string CanvasBasisPerspective = "perspective_no_orthogonalprojection";

    /// <summary>
    /// 场景作者画布：orthogonalprojection 的 width/height 按用户属性求值；auto 投影取面积最大的图片尺寸。
    /// 没有 orthogonalprojection 的是透视场景，没有作者画布；缺哪一边哪一边就是 null，由调用方决定退回值。
    /// </summary>
    internal static (double? Width, double? Height, string Basis) AuthoredCanvas(JsonObject scene, JsonObject properties)
    {
        var ortho = scene["general"]?["orthogonalprojection"];
        if (ortho is null) return (null, null, CanvasBasisPerspective);
        if (ortho["auto"]?.GetValue<bool>() == true && scene["objects"] is JsonArray objects)
        {
            var largest = objects.OfType<JsonObject>().Where(o => o.ContainsKey("image"))
                .Select(o => Vector(SceneGraph.Resolve(o["size"], properties), (0, 0)))
                .OrderByDescending(size => size.X * size.Y).FirstOrDefault();
            if (largest.X > 0 && largest.Y > 0) return (largest.X, largest.Y, CanvasBasisAutoLargestImage);
        }
        double? Dimension(JsonNode? node)
        {
            JsonNode? value = SceneGraph.Resolve(node, properties);
            if (value is JsonObject binding) value = binding["value"];
            // 解析出来的数字按 double 读得到；代码里构造的节点保留写入时的整数类型，按整数再读一次。
            if (value is not JsonValue number) return null;
            if (number.TryGetValue(out double real)) return real;
            if (number.TryGetValue(out int integer)) return integer;
            if (number.TryGetValue(out uint unsigned)) return unsigned;
            return number.TryGetValue(out long wide) ? wide : null;
        }
        return (Dimension(ortho["width"]), Dimension(ortho["height"]), CanvasBasisOrthographic);
    }

    internal static JsonObject Describe(JsonObject scene, JsonObject properties, uint width, uint height, JsonObject? runtime = null)
    {
        var general = scene["general"];
        var ortho = general?["orthogonalprojection"];
        var authored = AuthoredCanvas(scene, properties);
        double cw = authored.Width ?? 1920, ch = authored.Height ?? 1080;
        double zoom = SceneGraph.Numeric(SceneGraph.Resolve(general?["zoom"], properties), 1);
        if (zoom <= 0 || !double.IsFinite(zoom) || cw <= 0 || ch <= 0) throw new InvalidDataException("Invalid orthographic projection extent.");
        double scale = Math.Max(width / (cw / zoom), height / (ch / zoom));
        var report = new JsonObject { ["status"] = ortho is null ? "perspective" : "orthographic",
            ["canvas_width"] = cw, ["canvas_height"] = ch, ["center_x"] = cw / 2, ["center_y"] = ch / 2,
            ["visible_width"] = width / scale, ["visible_height"] = height / scale,
            ["parallax_amount"] = SceneGraph.Numeric(SceneGraph.Resolve(general?["cameraparallaxamount"], properties), .5),
            ["parallax_mouse_influence"] = SceneGraph.Numeric(SceneGraph.Resolve(general?["cameraparallaxmouseinfluence"], properties), 0) };
        if (runtime is not null)
        {
            var canvas = Vector(runtime["scene_ortho"], (cw, ch));
            var center = Vector(runtime["active_camera_position"], (cw / 2, ch / 2));
            report["canvas_width"] = canvas.X; report["canvas_height"] = canvas.Y;
            report["center_x"] = center.X; report["center_y"] = center.Y;
            report["visible_width"] = runtime["active_camera_width"]?.DeepClone();
            report["visible_height"] = runtime["active_camera_height"]?.DeepClone();
            report["status"] = runtime["active_camera_is_perspective"]?.GetValue<bool>() == true ? "perspective" : "orthographic";
            report["runtime_evidence"] = runtime.DeepClone();
            // 相机带单次播放的入场动画时，分析时刻的观测停在入场途中：取景改按入场结束后的静止态（之后仍循环的通道取全程范围的并集）。
            if (CameraIntro(scene, properties) is var (seconds, settled, minX, maxX, minY, maxY))
            {
                double still = Math.Max(width / (canvas.X / settled), height / (canvas.Y / settled));
                double w = width / still + maxX - minX, h = height / still + maxY - minY, fit = Math.Min(width / w, height / h);
                report["center_x"] = canvas.X / 2 + (minX + maxX) / 2; report["center_y"] = canvas.Y / 2 + (minY + maxY) / 2;
                report["visible_width"] = width / fit; report["visible_height"] = height / fit;
                report["camera_intro"] = new JsonObject { ["seconds"] = seconds,
                    ["tradeoff"] = "取景按相机入场动画结束后的静止态；入场那几秒成品显示原作图层，结束后切到视频（bake.json 的 intro_live）。" +
                        "切换做不成、退回旧行为（settings.single_shot_live）时，入场那几秒是放大的视频，会发糊。" };
            }
        }
        return report;
    }

    /// <summary>
    /// 场景相机（general.zoom、相机对象的 origin/zoom）带单次播放动画时：入场秒数、之后的最小缩放和相机偏移范围。
    /// 单次动画取末关键帧，循环动画取全部关键帧，没动画取当前值。没有单次动画返回 null（取景照旧用运行时观测）。
    /// </summary>
    static (double Seconds, double Zoom, double MinX, double MaxX, double MinY, double MaxY)? CameraIntro(JsonObject scene, JsonObject properties)
    {
        double seconds = 0;
        double[][] Values(JsonNode? node, double[] still)
        {
            if (SceneGraph.Resolve(node, properties) is not JsonObject { } holder || holder["animation"] is not JsonObject animation)
                return still.Select(v => new[] { v }).ToArray();
            bool single = animation["options"]?["mode"]?.GetValue<string>() == "single";
            if (single) seconds = Math.Max(seconds, SceneGraph.Numeric(animation["options"]?["length"], 0) / SceneGraph.Numeric(animation["options"]?["fps"], 30));
            return still.Select((v, i) => animation[$"c{i}"] is JsonArray { Count: > 0 } keys
                ? (single ? keys.TakeLast(1) : keys).Select(key => key!["value"]!.GetValue<double>()).ToArray() : [v]).ToArray();
        }
        double Scalar(JsonNode? node) => SceneGraph.Numeric(SceneGraph.Resolve(node, properties), 1);
        double zoom = Values(scene["general"]?["zoom"], [Scalar(scene["general"]?["zoom"])])[0].Min();
        var camera = (scene["objects"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(obj => obj.ContainsKey("camera"));
        zoom *= Values(camera?["zoom"], [Scalar(camera?["zoom"])])[0].Min();
        var still = Vector(SceneGraph.Resolve(camera?["origin"], properties), (0, 0));
        var origin = Values(camera?["origin"], [still.X, still.Y]);
        return seconds > 0 && zoom > 0 ? (seconds, zoom, origin[0].Min(), origin[0].Max(), origin[1].Min(), origin[1].Max()) : null;
    }

    internal static (double X, double Y) Vector(JsonNode? node, (double X, double Y) fallback)
    {
        if (node is JsonObject value) node = value["value"];
        if (node is JsonArray a && a.Count >= 2) return (a[0]!.GetValue<double>(), a[1]!.GetValue<double>());
        if (node is not JsonValue v || !v.TryGetValue<string>(out string? text)) return fallback;
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return words.Length >= 2 && double.TryParse(words[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double x) &&
            double.TryParse(words[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double y) && double.IsFinite(x) && double.IsFinite(y)
            ? (x, y) : throw new InvalidDataException("Invalid parallax/projection vector.");
    }
}
