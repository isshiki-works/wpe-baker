using System.Text.Json;
using System.Text.Json.Nodes;

namespace Periodica.Bench;

/// <param name="Order">段序，由 A（原作）与 B（成品）组成，如 ABBA；只测原作时为 A。</param>
/// <param name="IdleSeconds">大于 0 时在首尾各加一段空闲基线 I（WPE 暂停），功耗按两段空闲均值扣除。</param>
internal sealed record AbbaOptions(string WallpaperEngine, string Original, string? Baked, string Output,
    string Order = "ABBA", int Monitor = 0, int Seconds = 45, int SettleSeconds = 20, double Fps = 60,
    int IdleSeconds = 0, string? PresentMon = null);

/// <summary>
/// 官方 WPE 播放功耗的 A/B/B/A 编排：记下该显示器当前壁纸，逐段打开原作/成品（或暂停作空闲基线）、等稳定、
/// 调 <see cref="OfficialPerformanceSampler"/> 采样，最后无论成败都把原壁纸打开回去并继续播放。
/// 判定口径同 2026-09-18 的 A/B/B/A 实测：核显域中位功耗降幅 ≥30% 算省电，升幅 >5% 算更费，其余持平。
/// </summary>
internal static class Abba
{
    internal const double SavedPercent = 30, WorsePercent = 5;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>实际执行的段序：有空闲基线时首尾各包一段 I。</summary>
    internal static string Phases(string order, int idleSeconds)
    {
        if (order.Length == 0 || order.Any(kind => kind is not ('A' or 'B')))
            throw new ArgumentException("--order may contain only A and B.");
        return idleSeconds > 0 ? "I" + order + "I" : order;
    }

    internal static async Task<JsonObject> RunAsync(AbbaOptions options, IProgress<string> progress, CancellationToken token)
    {
        string phases = Phases(options.Order, options.IdleSeconds);
        if (phases.Contains('B') && options.Baked is null) throw new ArgumentException("--order uses B but --baked is missing.");
        string output = Path.GetFullPath(options.Output);
        if (Directory.Exists(output) || File.Exists(output)) throw new IOException("--out must be a new directory.");
        var wpe = new WpeControl(options.WallpaperEngine);
        string previous = await wpe.GetWallpaperAsync(options.Monitor, token);
        if (previous.Length == 0)
            throw new InvalidDataException($"Wallpaper Engine reports no wallpaper on monitor {options.Monitor}; nothing to restore to.");
        Directory.CreateDirectory(output);
        var segments = new JsonArray();
        var run = new JsonObject
        {
            ["schema_version"] = 1, ["order"] = phases, ["monitor"] = options.Monitor, ["previous_wallpaper"] = previous,
            ["seconds"] = options.Seconds, ["settle_seconds"] = options.SettleSeconds, ["target_fps"] = options.Fps,
            ["idle_seconds"] = options.IdleSeconds, ["original"] = options.Original, ["baked"] = options.Baked,
            ["segments"] = segments
        };
        try
        {
            for (int index = 0; index < phases.Length; ++index)
            {
                char kind = phases[index];
                string label = $"{index:00}-{kind}";
                var playback = new JsonObject { ["segment"] = label, ["kind"] = kind.ToString() };
                if (kind == 'I')
                {
                    progress.Report($"{label}: pausing Wallpaper Engine for the idle baseline.");
                    await wpe.PauseAsync(token);
                }
                else
                {
                    string project = kind == 'A' ? options.Original : options.Baked!;
                    playback["project"] = project;
                    progress.Report($"{label}: opening {project}.");
                    await wpe.OpenAsync(project, options.Monitor, token);
                    await wpe.PlayAsync(token);
                }
                await Task.Delay(TimeSpan.FromSeconds(options.SettleSeconds), token);
                JsonObject report = await OfficialPerformanceSampler.SampleAsync(new(1, wpe.ProcessId(), label,
                    Path.Combine(output, label), kind == 'I' ? options.IdleSeconds : options.Seconds, options.Fps,
                    options.PresentMon, ExpectedProcessPath: wpe.Executable), progress, playback, token);
                segments.Add(Segment(label, kind, report));
            }
        }
        finally
        {
            // 还原永远要做，采样失败或取消也一样；还原本身失败只记录，不掩盖原始异常。
            try
            {
                await wpe.OpenAsync(previous, options.Monitor, CancellationToken.None);
                await wpe.PlayAsync(CancellationToken.None);
                run["restored"] = true;
            }
            catch (Exception error) { run["restored"] = false; run["restore_error"] = error.ToString(); }
            run["summary"] = Summarize(segments);
            await File.WriteAllTextAsync(Path.Combine(output, "abba.json"), run.ToJsonString(JsonOptions), CancellationToken.None);
        }
        return run;
    }

    internal static JsonObject Segment(string label, char kind, JsonObject report)
    {
        JsonNode? power = report["platform_power"];
        return new JsonObject
        {
            ["label"] = label, ["kind"] = kind.ToString(), ["status"] = report["status"]?.DeepClone(),
            ["power_status"] = power?["status"]?.DeepClone(),
            ["igpu_watts"] = Median(power?["igpu_domain_watts"]), ["package_watts"] = Median(power?["package_watts"]),
            ["presentmon_status"] = report["presentmon"]?["status"]?.DeepClone(),
            ["report_path"] = report["report_path"]?.DeepClone()
        };
    }

    /// <summary>按段种求核显域与封装的均值，扣空闲基线，给出 A 对 B 的结论。任一段缺读数就不给结论。</summary>
    internal static JsonObject Summarize(JsonArray segments)
    {
        double? Mean(char kind, string field)
        {
            var values = segments.Where(item => item?["kind"]?.GetValue<string>() == kind.ToString())
                .Select(item => item?[field] is JsonValue value && value.TryGetValue(out double watts) ? watts : (double?)null).ToArray();
            return values.Length == 0 || values.Any(value => value is null) ? null : values.Average(value => value!.Value);
        }
        double? idle = Mean('I', "igpu_watts"), original = Mean('A', "igpu_watts"), baked = Mean('B', "igpu_watts");
        var summary = new JsonObject
        {
            ["metric"] = "platform_power.igpu_domain_watts.median, mean over segments of the same kind",
            ["idle_igpu_watts"] = idle, ["original_igpu_watts"] = original, ["baked_igpu_watts"] = baked,
            ["idle_package_watts"] = Mean('I', "package_watts"), ["original_package_watts"] = Mean('A', "package_watts"),
            ["baked_package_watts"] = Mean('B', "package_watts")
        };
        summary["gain"] = Gain(original, baked, idle);
        return summary;
    }

    /// <summary>原作对成品的核显域变化；给了空闲基线就先扣掉再比（比的是壁纸自身的增量）。</summary>
    internal static JsonObject Gain(double? original, double? baked, double? idle)
    {
        double? a = original - (idle ?? 0), b = baked - (idle ?? 0);
        if (a is not > 0 || b is null)
            return new JsonObject { ["status"] = "not_measured", ["verdict"] = "unavailable" };
        double change = (b.Value - a.Value) / a.Value * 100;
        return new JsonObject
        {
            ["status"] = "measured", ["baseline_subtracted"] = idle is not null,
            ["original_watts"] = a, ["baked_watts"] = b, ["change_percent"] = change,
            ["verdict"] = change <= -SavedPercent ? "saved" : change > WorsePercent ? "worse" : "same"
        };
    }

    private static double? Median(JsonNode? stats) =>
        stats?["median"] is JsonValue value && value.TryGetValue(out double median) && double.IsFinite(median) ? median : null;
}
