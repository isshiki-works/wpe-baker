using System.Text.Json.Nodes;
using Baker.Core;
using Microsoft.Win32;
using Xunit;

/// <summary>
/// 只对官方源码全文成立的三处指纹（水波官方子句、auto sway 官方子句、shine 四方向与 shine 路径上的备用时钟禁止），
/// 合成片段测不到，这里直接读本机官方源码：WE 安装目录（WPE_ASSETS，缺省取 Steam 注册表的 SteamPath）与
/// Steam 工坊目录（WPE_WORKSHOP_CONTENT，缺省同上）。源码只在测试临时目录里拷一份，不进仓库；目录缺失就跳过。
/// </summary>
internal static class StockShaderClockChecks
{
    private static readonly string? SteamRoot = OperatingSystem.IsWindows()
        ? (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string)?.Replace('/', Path.DirectorySeparatorChar)
        : null;

    internal static readonly string Assets = Environment.GetEnvironmentVariable("WPE_ASSETS") is { Length: > 0 } assets ? assets
        : Path.Combine(SteamRoot ?? "", "steamapps", "common", "wallpaper_engine", "assets");

    internal static readonly string Workshop = Environment.GetEnvironmentVariable("WPE_WORKSHOP_CONTENT") is { Length: > 0 } workshop ? workshop
        : Path.Combine(SteamRoot ?? "", "steamapps", "workshop", "content", "431960");

    /// <summary>带官方 auto sway 着色器（thisMotionTime 那一族）的工坊作品，取第一张在本机的。</summary>
    private static readonly string[] AutoSwayWorks = ["3486806915", "3492627662", "3516174947"];

    internal static string MissingAssets => "缺本机 Wallpaper Engine 官方资源目录：" + Assets + "（用 WPE_ASSETS 指定 wallpaper_engine/assets）";

    internal static string? AutoSwayWork => AutoSwayWorks.Select(id => Path.Combine(Workshop, id)).FirstOrDefault(Directory.Exists);

    internal static string MissingAutoSway => "本机工坊目录没有带官方 auto sway 着色器的作品（" + string.Join("/", AutoSwayWorks) + "）：" +
        Workshop + "（用 WPE_WORKSHOP_CONTENT 指定 workshop/content/431960）";

    /// <summary>把官方效果目录（去掉 preview）拷进一个临时工程，可选地给某个 shader 末尾追加一行。clouds_256 与实际一样从 WE 资源目录读。</summary>
    private static ProjectSource StockEffect(string root, string effect, JsonArray passes, string? appendShader = null, string? appendLine = null)
    {
        string target = Path.Combine(root, "stock-" + effect + (appendShader is null ? "" : "-" + Path.GetFileName(appendShader)));
        string origin = Path.Combine(Assets, "effects", effect);
        foreach (string file in Directory.EnumerateFiles(origin, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(origin, file);
            if (relative.StartsWith("preview", StringComparison.OrdinalIgnoreCase)) continue;
            string destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }
        if (appendShader is not null) File.AppendAllText(Path.Combine(target, appendShader.Replace('/', Path.DirectorySeparatorChar)), "\n" + appendLine + "\n");
        var scene = new JsonObject { ["objects"] = new JsonArray(new JsonObject { ["id"] = 1,
            ["effects"] = new JsonArray(new JsonObject { ["file"] = "effect.json", ["passes"] = passes }) }) };
        File.WriteAllText(Path.Combine(target, "project.json"), "{\"type\":\"scene\",\"file\":\"scene.json\"}");
        File.WriteAllText(Path.Combine(target, "scene.json"), scene.ToJsonString());
        return new ProjectSource(target);
    }

    private static ShaderPeriodAnalysisResult Analyze(ProjectSource source) =>
        ShaderPeriodAnalysis.Analyze(source.ReadJson(source.SceneResource), source, Assets, [1]);

    internal static void WaterWaves(Action<bool, string> check, string root)
    {
        using ProjectSource source = StockEffect(root, "waterwaves", [new JsonObject { ["constantshadervalues"] = new JsonObject { ["speed"] = 1.5 } }]);
        ShaderPeriodAnalysisResult analysis = Analyze(source);
        check(analysis.Unresolved.Count == 0 && analysis.Components.Count == 1 &&
            analysis.Components[0].Patch.ConstantKey == "speed" && analysis.Components[0].Component.AllowRetime &&
            Math.Abs(analysis.Components[0].Component.BasePeriod!.Seconds - 2 * Math.PI / 1.5) < 1e-12,
            "the stock waterwaves source (perspective step and texture time offset included) is the water-wave rule's official clause");
    }

    internal static void Shine(Action<bool, string> check, string root)
    {
        JsonArray Passes() => [new JsonObject { ["constantshadervalues"] = new JsonObject { ["noisespeed"] = 0.2, ["noisescale"] = 5.0 } },
            new JsonObject { ["constantshadervalues"] = new JsonObject { ["speed"] = 0.3 } }];
        using (ProjectSource source = StockEffect(root, "shine", Passes()))
        {
            ShaderPeriodAnalysisResult analysis = Analyze(source);
            ShaderPeriodComponent? ray = analysis.Components.SingleOrDefault(item => item.Component.Id.EndsWith("/ray-speed", StringComparison.Ordinal));
            ShaderPeriodComponent? noise = analysis.Components.SingleOrDefault(item => item.Component.Id.EndsWith("/noise-speed", StringComparison.Ordinal));
            check(ray is not null && noise is not null &&
                Math.Abs(ray.Component.BasePeriod!.Seconds - Math.PI / (2 * 0.3)) < 1e-12 && ray.Patch.ConstantKey == "speed" && ray.Patch.PassIndex == 1 &&
                Math.Abs(noise.Component.BasePeriod!.Seconds - 2 / (0.2 * 5)) < 1e-12 && noise.Patch.ConstantKey == "noisespeed",
                "the stock shine cast is the verified four-direction variant, so a moving cast yields π/(2·speed) beside the repeat-noise period");
        }
        // shine 的两个 pass 不经主循环的备用时钟预筛：官方源码多一处 g_Frametime，只有指纹上的备用时钟禁止能拒掉。
        foreach (string shader in new[] { "shaders/effects/shine_downsample2.vert", "shaders/effects/shine_cast.vert" })
        {
            using ProjectSource source = StockEffect(root, "shine", Passes(), shader, "float extraClock = g_Frametime;");
            ShaderPeriodAnalysisResult analysis = Analyze(source);
            check(analysis.Components.Count == 0 && analysis.Unresolved.Count == 1 &&
                analysis.Unresolved[0].Kind == ShaderTemporalUnresolvedKind.UnsupportedShaderMechanism &&
                analysis.Unresolved[0].Detail == "Shine source, supported cast variant, or canonical repeat noise texture is not proven.",
                $"an extra g_Frametime use in the stock {Path.GetFileName(shader)} keeps shine from claiming a period");
        }
    }

    internal static void AutoSway(Action<bool, string> check, string work)
    {
        using var source = new ProjectSource(work);
        JsonObject scene = SceneAnalyzer.ReadResourceJson(source, Directory.Exists(Assets) ? Assets : null, source.SceneResource);
        int[] ids = [.. (scene["objects"]?.AsArray() ?? []).OfType<JsonObject>()
            .Select(item => item["id"] is JsonValue value && value.TryGetValue(out int id) ? id : int.MinValue).Where(id => id != int.MinValue)];
        ShaderPeriodAnalysisResult analysis = ShaderPeriodAnalysis.Analyze(scene, source, Directory.Exists(Assets) ? Assets : null, ids);
        check(analysis.Components.Any(item => !item.Component.AllowRetime && item.Patch.ConstantKey == "speed" &&
                item.Component.BasePeriod!.ExactSeconds is not null),
            "the stock workshop auto-sway source (two stages, 32 thisMotionTime uses) is the auto-sway rule's official clause");
    }
}

[Trait("Layer", "L3"), Collection("L3 本机工具")]
public class StockShaderClockTests
{
    [Fact]
    public async Task WaterWaves()
    {
        Assert.SkipUnless(Directory.Exists(StockShaderClockChecks.Assets), StockShaderClockChecks.MissingAssets);
        await TestTemp.Run(dir => StockShaderClockChecks.WaterWaves(Assert.True, dir));
    }

    [Fact]
    public async Task Shine()
    {
        Assert.SkipUnless(Directory.Exists(StockShaderClockChecks.Assets), StockShaderClockChecks.MissingAssets);
        await TestTemp.Run(dir => StockShaderClockChecks.Shine(Assert.True, dir));
    }

    [Fact]
    public void AutoSway()
    {
        Assert.SkipUnless(StockShaderClockChecks.AutoSwayWork is not null, StockShaderClockChecks.MissingAutoSway);
        StockShaderClockChecks.AutoSway(Assert.True, StockShaderClockChecks.AutoSwayWork!);
    }
}
