using System.Reflection;
using System.Text.Json.Nodes;
using Baker.Core;

internal static class HybridCaptureViewportChecks
{
    internal static void Run(Action<bool, string> check)
    {
        Type projection = typeof(HybridBakeService).Assembly.GetType("Baker.Core.HybridVideoProjection")!;
        MethodInfo single = projection.GetMethod("CaptureViewportForGroup", BindingFlags.Static | BindingFlags.NonPublic)!;
        MethodInfo maximum = projection.GetMethod("MaximumCaptureViewport", BindingFlags.Static | BindingFlags.NonPublic)!;
        var source = new JsonObject { ["canvas_width"] = 200d, ["canvas_height"] = 100d, ["visible_width"] = 80d, ["visible_height"] = 40d,
            ["parallax_amount"] = .5, ["parallax_mouse_influence"] = .2 };
        JsonObject Group(double x, double y) => new() { ["parallax_depth"] = new JsonArray(x, y) };
        (double Width, double Height) Extent(object value)
        {
            Type type = value.GetType();
            return ((double)type.GetProperty("Width")!.GetValue(value)!, (double)type.GetProperty("Height")!.GetValue(value)!);
        }
        var expanded = Extent(single.Invoke(null, [source, Group(2, -3), true])!);
        check(expanded == (120, 70), "preserved parallax expands a group viewport by its absolute depth");
        var zero = Extent(single.Invoke(null, [source, Group(0, 0), true])!);
        var fixedView = Extent(single.Invoke(null, [source, Group(2, -3), false])!);
        check(zero == (80, 40) && fixedView == (80, 40), "zero-depth and fixed-view groups retain the visible viewport");
        var union = Extent(maximum.Invoke(null, [source, new[] { Group(2, 0), Group(0, -4) }, true])!);
        check(union == (120, 80), "the natural seam viewport covers the largest overscan required by every group");
    }
}
