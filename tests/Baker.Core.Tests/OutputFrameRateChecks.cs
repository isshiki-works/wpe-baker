using System.Text.Json.Nodes;
using Baker.Core;

/// <summary>
/// 默认帧率：显式优先、min(WPE 帧率上限, 主屏刷新率) 就近取标准档、依据缺失回退 60（不是 120）、
/// config.json 里 general.user.fps 的读取，以及 plan 的 frame_rate 记录。
/// </summary>
internal static class OutputFrameRateChecks
{
    internal static Task RunAsync(Action<bool, string> check, string root)
    {
        static Func<WallpaperEngineProperties.FrameRateLimit> Limit(uint fps) => () => new(true, fps, WallpaperEngineProperties.LimitFound);
        static Func<WallpaperEngineProperties.FrameRateLimit> NoLimit() => () => new(true, null, WallpaperEngineProperties.LimitNone);
        static Func<WallpaperEngineProperties.FrameRateLimit> Unreadable(string reason) => () => new(false, null, reason);
        static Func<WallpaperEngineProperties.FrameRateLimit> NoLimitQuery() =>
            () => throw new InvalidOperationException("an explicit --fps must not read Wallpaper Engine settings");
        static Func<uint?> Refresh(uint hz) => () => hz;
        static Func<uint?> NoRefresh() => () => null;
        static Func<uint?> NoRefreshQuery() => () => throw new InvalidOperationException("an explicit --fps must not read the display");

        // ---- 显式优先 ----
        var explicitFps = OutputFrameRate.Choose(120, NoLimitQuery(), NoRefreshQuery());
        check(explicitFps is { Fps: 120, Source: OutputFrameRate.Explicit, WpeLimit: null, DisplayRefreshHz: null } &&
            explicitFps.ToJson()["chosen"]!.GetValue<uint>() == 120 && explicitFps.ToJson()["source"]!.GetValue<string>() == "explicit",
            "an explicit --fps is used unchanged and reads neither the Wallpaper Engine setting nor the display");
        check(OutputFrameRate.Choose(97, NoLimitQuery(), NoRefreshQuery()).Fps == 97,
            "an explicit --fps is never snapped to a standard tier");

        // ---- min(上限, 刷新率) 就近取档 ----
        var machine = OutputFrameRate.Choose(0, Limit(63), Refresh(240));
        check(machine is { Fps: 60, Source: OutputFrameRate.Auto, WpeLimit: 63, DisplayRefreshHz: 240 },
            "a Wallpaper Engine limit of 63 on a 240 Hz display gives min=63, nearest tier 60 (this machine's shape)");
        check(OutputFrameRate.Choose(0, Limit(144), Refresh(144)).Fps == 144,
            "144 under a 144 Hz display stays 144, a standard tier of its own");
        check(OutputFrameRate.Choose(0, NoLimit(), Refresh(240)) is { Fps: 240, Source: OutputFrameRate.Auto, WpeLimit: null, DisplayRefreshHz: 240 },
            "no Wallpaper Engine limit on a 240 Hz display gives 240: the refresh rate alone decides");
        check(OutputFrameRate.Choose(0, Limit(240), Refresh(60)).Fps == 60,
            "a 240 limit on a 60 Hz display takes the display side of the min");
        check(OutputFrameRate.Choose(0, Limit(60), Refresh(165)).Fps == 60,
            "a 60 limit on a 165 Hz display takes the Wallpaper Engine side of the min");
        check(OutputFrameRate.Choose(0, NoLimit(), Refresh(165)).Fps == 165, "165 Hz is a standard tier and survives the snap");
        check(OutputFrameRate.Choose(0, NoLimit(), Refresh(59)).Fps == 60, "59 Hz (59.94 reported as an integer) snaps to 60");
        check(OutputFrameRate.Choose(0, NoLimit(), Refresh(75)).Fps == 60, "75 Hz is nearer 60 than 120");
        check(OutputFrameRate.Choose(0, NoLimit(), Refresh(90)).Fps == 60, "90 Hz is a tie between 60 and 120 and takes the lower tier");
        check(OutputFrameRate.Choose(0, NoLimit(), Refresh(143)).Fps == 144, "143 Hz snaps up to the 144 tier");
        check(OutputFrameRate.Choose(0, NoLimit(), Refresh(360)).Fps == 240, "a rate above every tier is capped at 240");
        check(OutputFrameRate.Choose(0, Limit(24), Refresh(240)).Fps == 30, "a limit below every tier snaps up to 30, the lowest tier");
        check(OutputFrameRate.NearestTier(120) == 120 && OutputFrameRate.NearestTier(45) == 30 && OutputFrameRate.NearestTier(150) == 144 &&
            OutputFrameRate.NearestTier(155) == 165 && OutputFrameRate.NearestTier(1) == 30,
            "nearest tier: exact tiers keep themselves, 45 ties down to 30, 150 takes 144 and 155 takes 165");

        // ---- 依据缺失一律回退 60 ----
        var noConfig = OutputFrameRate.Choose(0, Unreadable("config_not_found"), Refresh(240));
        check(noConfig is { Fps: OutputFrameRate.Fallback, Source: OutputFrameRate.Auto, WpeLimit: null, DisplayRefreshHz: 240 } &&
            noConfig.Reason == "wpe_limit_unreadable" && noConfig.WpeLimitStatus == "config_not_found",
            "an unreadable Wallpaper Engine setting falls back to 60 even on a 240 Hz display, and records why");
        var noDisplay = OutputFrameRate.Choose(0, Limit(240), NoRefresh());
        check(noDisplay is { Fps: OutputFrameRate.Fallback, DisplayRefreshHz: null, WpeLimit: 240 } && noDisplay.Reason == "display_refresh_unreadable",
            "an unreadable refresh rate falls back to 60 even with a 240 limit on record");
        check(OutputFrameRate.Choose(0, Unreadable("profile_ambiguous"), NoRefresh()).Fps == OutputFrameRate.Fallback &&
            OutputFrameRate.Choose(0, NoLimit(), NoRefresh()).Fps == OutputFrameRate.Fallback,
            "neither basis readable also falls back to 60");
        check(OutputFrameRate.Choose(0, NoLimit(), () => 1).Fps == OutputFrameRate.Fallback &&
            OutputFrameRate.Choose(0, NoLimit(), () => 0).Fps == OutputFrameRate.Fallback,
            "GetDeviceCaps reporting 0 or 1 Hz means 'hardware default', not a real rate, so it falls back to 60");
        check(OutputFrameRate.Fallback == 60 && OutputFrameRate.Tiers.SequenceEqual<uint>([30, 60, 120, 144, 165, 240]),
            "the fallback is 60 (not the old hard-coded 120) and the tiers are 30/60/120/144/165/240 in ascending order");

        // ---- config.json 的 general.user.fps ----
        string directory = Path.Combine(root, "wpe-frame-rate-limit");
        Directory.CreateDirectory(directory);
        string configPath = Path.Combine(directory, "config.json");
        WallpaperEngineProperties.FrameRateLimit Read(JsonObject config, params string[] names)
        {
            File.WriteAllText(configPath, config.ToJsonString());
            return WallpaperEngineProperties.ReadFrameRateLimit(new(configPath, "steam_library"), names);
        }
        static JsonObject Account(JsonNode? fps) => new()
        {
            ["?installdirectory"] = "D:/games/wallpaper_engine",
            ["Alya"] = new JsonObject { ["general"] = new JsonObject { ["user"] = fps is null ? new JsonObject() : new JsonObject { ["fps"] = fps } } },
        };
        check(Read(Account(63), "Alya") is { Known: true, Fps: 63, Reason: WallpaperEngineProperties.LimitFound },
            "general.user.fps is the Wallpaper Engine frame rate limit and is read as a number");
        check(Read(Account(null), "Alya") is { Known: false, Reason: "fps_not_set" },
            "a profile without general.user.fps is not known, so the caller falls back rather than guessing");
        check(Read(Account(0), "Alya") is { Known: true, Fps: null, Reason: WallpaperEngineProperties.LimitNone } &&
            Read(Account(100000), "Alya") is { Known: true, Fps: null, Reason: WallpaperEngineProperties.LimitNone },
            "a non-positive or absurd stored value means the user set no limit: known, but with no cap");
        check(Read(Account("60"), "Alya") is { Known: false, Reason: "fps_not_set" }, "a non-numeric stored value is not a limit");
        check(Read(new JsonObject { ["Alya"] = new JsonObject { ["general"] = new JsonObject() }, ["Other"] = new JsonObject() }, "Nobody")
            is { Known: false, Reason: "profile_ambiguous" }, "two profiles and no name match is ambiguous, the same rule the property read uses");
        check(Read(new JsonObject { ["Only"] = new JsonObject { ["general"] = new JsonObject { ["user"] = new JsonObject { ["fps"] = 30 } } } }, "Nobody")
            is { Known: true, Fps: 30 }, "a single profile is used even when no name matches, matching the property read");
        check(WallpaperEngineProperties.ReadFrameRateLimit(null) is { Known: false, Reason: "config_not_found" },
            "no config.json at all is reported as config_not_found");
        File.WriteAllText(configPath, "{ not json");
        check(WallpaperEngineProperties.ReadFrameRateLimit(new(configPath, "steam_library"), ["Alya"]) is { Known: false, Reason: "config_unreadable" },
            "a corrupt config.json is reported as config_unreadable, never as a frame rate");

        // ---- plan 记录 ----
        var plan = new JsonObject { ["settings"] = new JsonObject(), ["output_resolution"] = new JsonObject(), ["projection"] = new JsonObject() };
        typeof(PlanWriter).GetMethod("AttachFrameRate", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, [plan, machine.ToJson()]);
        check(plan.Select(pair => pair.Key).ToArray() is ["settings", "output_resolution", "frame_rate", "projection"],
            "the frame rate record sits directly after output_resolution in the plan");
        var recorded = plan["frame_rate"]!.AsObject();
        check(recorded["source"]!.GetValue<string>() == "auto" && recorded["wpe_fps_limit"]!.GetValue<uint>() == 63 &&
            recorded["display_refresh_hz"]!.GetValue<uint>() == 240 && recorded["chosen"]!.GetValue<uint>() == 60 &&
            recorded["rule"]!.GetValue<string>().Length > 0,
            "the plan records fps_source auto with wpe_fps_limit, display_refresh_hz, chosen and the rule");
        check(!System.Text.Json.JsonSerializer.SerializeToNode(new HybridAnalyzeRequest(2, "s", "a", "o"))!.AsObject().ContainsKey("FrameRateOrigin"),
            "a request without a frame rate record does not write the origin into plan settings");
        return Task.CompletedTask;
    }
}
