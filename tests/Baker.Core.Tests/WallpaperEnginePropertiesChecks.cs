using System.Text.Json;
using System.Text.Json.Nodes;
using Baker.App;
using Baker.Core;

/// <summary>属性默认沿用 Wallpaper Engine 设置：路径规范化匹配、类型校验、覆盖优先级、找不到 config 的回退与 plan 接线。</summary>
internal static class WallpaperEnginePropertiesChecks
{
    private static readonly string[] Tester = ["Tester"];

    internal static async Task RunAsync(Action<bool, string> check, string root)
    {
        string install = Path.Combine(root, "wpe-props-install");
        string wallpaper = Path.Combine(root, "wpe-props-workshop", "431960", "3486806915");
        Directory.CreateDirectory(install);
        Directory.CreateDirectory(wallpaper);
        JsonObject project = Project();
        await File.WriteAllTextAsync(Path.Combine(wallpaper, "project.json"), project.ToJsonString());
        string sceneEntry = Path.Combine(wallpaper, "scene.json");

        // ---- 路径规范化匹配：正斜杠、大小写都和本地路径不同，照样认出来；别的目录的记录不串 ----
        string entryKey = wallpaper.Replace('\\', '/').ToUpperInvariant() + "/scene.json";
        string config = Path.Combine(install, "config.json");
        await File.WriteAllTextAsync(config, new JsonObject {
            ["?installdirectory"] = install.Replace('\\', '/'),
            ["Tester"] = new JsonObject {
                ["general"] = Selected(("Monitor0", entryKey)),
                ["wproperties"] = new JsonObject {
                    [Path.Combine(root, "wpe-props-workshop", "431960", "999").Replace('\\', '/') + "/scene.pkg"] =
                        Monitors(("Monitor0", new JsonObject { ["sponsor"] = true, ["volume"] = 1 })),
                    [entryKey] = Monitors(("Monitor0", new JsonObject {
                        ["sponsor"] = false, ["volume"] = 0, ["mode"] = 2, ["tint"] = "2 0 0", ["caption"] = "custom",
                        ["heading"] = "x", ["alignment"] = 4, ["rain"] = "no" }))
                } }
        }.ToJsonString());
        var location = new WallpaperEngineProperties.ConfigLocation(config, "steam_library");
        var resolved = WallpaperEngineProperties.Resolve("wpe", wallpaper, sceneEntry, project, location, Tester);
        check(resolved.Source == "wpe" && resolved.Record["reason"] is null &&
            Text(resolved.Record["entry"]) == entryKey && Text(resolved.Record["location"]) == "Monitor0" &&
            Text(resolved.Record["profile"]) == "Tester" && Text(resolved.Record["profile_match"]) == "current_user" &&
            Text(resolved.Record["config"]) == config && Text(resolved.Record["config_origin"]) == "steam_library",
            "wproperties keys match the source folder after slash and case normalization, not a neighbouring wallpaper");
        check(WallpaperEngineProperties.NormalizePath("C:/Foo/Bar/") == WallpaperEngineProperties.NormalizePath(@"C:\Foo\Bar") &&
            string.Equals(WallpaperEngineProperties.NormalizePath("c:/foo/bar/scene.pkg"), WallpaperEngineProperties.NormalizePath(@"C:\FOO\BAR\scene.pkg"),
                StringComparison.OrdinalIgnoreCase),
            "path normalization unifies separators and trailing separators; comparison ignores case");

        string projectKeyConfig = Path.Combine(root, "wpe-props-project-key.json");
        await File.WriteAllTextAsync(projectKeyConfig, new JsonObject { ["Tester"] = new JsonObject { ["wproperties"] = new JsonObject {
            [(wallpaper + @"\").ToLowerInvariant() + "project.json"] = new JsonObject { ["sponsor"] = false } } } }.ToJsonString());
        var projectKeyed = WallpaperEngineProperties.Resolve("wpe", wallpaper, sceneEntry, project, new(projectKeyConfig, "configured_path"), Tester);
        check(projectKeyed.Source == "wpe" && projectKeyed.Record["location"] is null &&
            projectKeyed.Values["sponsor"] is JsonValue flat && !flat.GetValue<bool>(),
            "a project.json key with backslashes and lower case also matches, and a flat (pre-monitor) entry is read directly");

        // ---- 类型校验：按 project.json 的类型收下或忽略，并记录原因 ----
        string Reason(string key) => resolved.Record["ignored"]!.AsArray().OfType<JsonObject>()
            .Single(item => Text(item["key"]) == key)["reason"]!.GetValue<string>();
        check(resolved.Values.Select(pair => pair.Key).Order(StringComparer.Ordinal).SequenceEqual(["caption", "mode", "sponsor", "volume"]) &&
            !resolved.Values["sponsor"]!.GetValue<bool>() && resolved.Values["volume"]!.GetValue<double>() == 0 &&
            resolved.Values["mode"]!.GetValueKind() == JsonValueKind.String && resolved.Values["mode"]!.GetValue<string>() == "2" &&
            resolved.Values["caption"]!.GetValue<string>() == "custom",
            "valid bool, slider, combo and text values are taken; a numeric combo value is normalized to the option's own value");
        check(Reason("tint") == "invalid_color" && Reason("heading") == "unsupported_type:text" &&
            Reason("alignment") == "not_a_project_property" && Reason("rain") == "invalid_bool" &&
            resolved.Record["ignored"]!.AsArray().Count == 4,
            "invalid colors, label-only types, WPE-only settings and wrongly typed switches are ignored with a recorded reason");
        JsonObject slider = project["general"]!["properties"]!["volume"]!.AsObject();
        JsonObject combo = project["general"]!["properties"]!["mode"]!.AsObject();
        check(WallpaperEngineProperties.Validate(slider, JsonValue.Create(1.5), out _) == "out_of_range" &&
            WallpaperEngineProperties.Validate(slider, JsonValue.Create("0.5"), out _) == "invalid_number" &&
            WallpaperEngineProperties.Validate(combo, JsonValue.Create("9"), out _) == "not_an_option" &&
            WallpaperEngineProperties.Validate(slider, JsonValue.Create(0.25), out JsonNode? kept) is null && kept!.GetValue<double>() == .25,
            "sliders outside min/max, numbers stored as text and unknown combo options are rejected");

        // ---- 覆盖优先级：默认值 < WPE 值 < 显式属性 ----
        var (merged, origin) = WallpaperEngineProperties.Merge(resolved, project, new JsonObject { ["volume"] = 0.5, ["rain"] = false });
        JsonObject snapshot = Snapshot(project, merged);
        check(!snapshot["sponsor"]!.GetValue<bool>() && snapshot["volume"]!.GetValue<double>() == .5 && snapshot["mode"]!.GetValue<string>() == "2" &&
            !snapshot["rain"]!.GetValue<bool>() && snapshot["tint"]!.GetValue<string>() == "1 1 1" && snapshot["caption"]!.GetValue<string>() == "custom",
            "explicit properties override WPE values, which override project defaults");
        check(Text(origin["source"]) == "wpe" && Names(origin["overridden_keys"]).SequenceEqual(["volume"]) &&
            Names(origin["applied_keys"]).Order(StringComparer.Ordinal).SequenceEqual(["caption", "mode", "sponsor"]) &&
            Names(origin["differs_from_default"]).Order(StringComparer.Ordinal).SequenceEqual(["caption", "mode", "sponsor"]),
            "the origin record lists applied, overridden and non-default WPE keys");
        var defaults = WallpaperEngineProperties.Resolve("defaults", wallpaper, sceneEntry, project, location, Tester);
        var (explicitOnly, defaultsOrigin) = WallpaperEngineProperties.Merge(defaults, project, new JsonObject { ["volume"] = new JsonObject { ["value"] = 0.3 } });
        check(defaults.Source == "defaults" && Text(defaultsOrigin["source"]) == "defaults" && Text(defaultsOrigin["reason"]) == "requested_defaults" &&
            defaultsOrigin["applied_keys"] is null && explicitOnly!.Count == 1 &&
            Snapshot(project, explicitOnly)["volume"]!.GetValue<double>() == .3 && Snapshot(project, explicitOnly)["sponsor"]!.GetValue<bool>(),
            "properties-source defaults ignores WPE values and still applies explicit properties");
        var (sameDefaults, _) = WallpaperEngineProperties.Merge(
            WallpaperEngineProperties.Resolve("wpe", wallpaper, sceneEntry, project, location, Tester) with {
                Values = new JsonObject { ["volume"] = 0.8 } }, project, null);
        check(sameDefaults!.Count == 1, "a WPE value equal to the default is still passed on");

        // ---- 回退：找不到 / 读不了 config、用户歧义、没有记录 ----
        var missing = WallpaperEngineProperties.Resolve("wpe", wallpaper, sceneEntry, project, null, Tester);
        var (missingProperties, missingOrigin) = WallpaperEngineProperties.Merge(missing, project, null);
        check(missing.Source == "wpe_unavailable" && Text(missing.Record["reason"]) == "config_not_found" && missing.Values.Count == 0 &&
            missingProperties is null && Text(missingOrigin["source"]) == "wpe_unavailable",
            "without a config.json the analysis falls back to defaults and passes no user properties");
        string broken = Path.Combine(root, "wpe-props-broken.json");
        await File.WriteAllTextAsync(broken, "{ not json");
        var unreadable = WallpaperEngineProperties.Resolve("wpe", wallpaper, sceneEntry, project, new(broken, "running_wallpaper_engine"), Tester);
        check(unreadable.Source == "wpe_unavailable" && Text(unreadable.Record["reason"]) == "config_unreadable" &&
            Text(unreadable.Record["config"]) == broken && unreadable.Record["detail"] is not null,
            "an unreadable config.json falls back to defaults and records why");
        string users = Path.Combine(root, "wpe-props-users.json");
        await File.WriteAllTextAsync(users, new JsonObject {
            ["Alice"] = new JsonObject { ["wproperties"] = new JsonObject { [entryKey] = new JsonObject { ["sponsor"] = false } } },
            ["Bob"] = new JsonObject { ["wproperties"] = new JsonObject { [entryKey] = new JsonObject { ["volume"] = 0.1 } } } }.ToJsonString());
        var ambiguous = WallpaperEngineProperties.Resolve("wpe", wallpaper, sceneEntry, project, new(users, "steam_library"), ["Carol"]);
        var chosen = WallpaperEngineProperties.Resolve("wpe", wallpaper, sceneEntry, project, new(users, "steam_library"), ["Carol", "bob"]);
        var single = WallpaperEngineProperties.Resolve("wpe", wallpaper, sceneEntry, project, new(projectKeyConfig, "steam_library"), ["Carol"]);
        check(ambiguous.Source == "wpe_unavailable" && Text(ambiguous.Record["reason"]) == "profile_ambiguous" && ambiguous.Values.Count == 0 &&
            chosen.Source == "wpe" && Text(chosen.Record["profile"]) == "Bob" && chosen.Values.Count == 1 &&
            single.Source == "wpe" && Text(single.Record["profile_match"]) == "only_profile",
            "several users fall back unless one is the current user; a single user is used as is");
        string noEntry = Path.Combine(root, "wpe-props-no-entry.json");
        await File.WriteAllTextAsync(noEntry, new JsonObject { ["Tester"] = new JsonObject { ["general"] = new JsonObject() } }.ToJsonString());
        var unsaved = WallpaperEngineProperties.Resolve("wpe", wallpaper, sceneEntry, project, new(noEntry, "steam_library"), Tester);
        var (unsavedProperties, unsavedOrigin) = WallpaperEngineProperties.Merge(unsaved, project, null);
        check(unsaved.Source == "wpe" && Text(unsaved.Record["reason"]) == "no_saved_properties" && unsavedProperties is null &&
            Names(unsavedOrigin["differs_from_default"]).Length == 0,
            "a wallpaper without saved WPE properties keeps the default snapshot and passes no user properties");

        // ---- 多屏：优先正在放这张壁纸的屏幕；设置不同又分不出来就回退 ----
        string monitors = Path.Combine(root, "wpe-props-monitors.json");
        JsonObject MultiMonitor(string? showing) => new() { ["Tester"] = new JsonObject {
            ["general"] = showing is null ? new JsonObject() : Selected((showing, entryKey)),
            ["wproperties"] = new JsonObject { [entryKey] = Monitors(
                ("Monitor0", new JsonObject { ["sponsor"] = true }), ("Monitor1", new JsonObject { ["sponsor"] = false })) } } };
        await File.WriteAllTextAsync(monitors, MultiMonitor("Monitor1").ToJsonString());
        var shown = WallpaperEngineProperties.Resolve("wpe", wallpaper, sceneEntry, project, new(monitors, "steam_library"), Tester);
        await File.WriteAllTextAsync(monitors, MultiMonitor(null).ToJsonString());
        var unclear = WallpaperEngineProperties.Resolve("wpe", wallpaper, sceneEntry, project, new(monitors, "steam_library"), Tester);
        check(Text(shown.Record["location"]) == "Monitor1" && !shown.Values["sponsor"]!.GetValue<bool>() &&
            unclear.Source == "wpe_unavailable" && Text(unclear.Record["reason"]) == "location_ambiguous",
            "per-monitor settings prefer the screen showing the wallpaper and fall back when screens disagree");

        // ---- 定位顺序：运行中 → Steam 库 → 设置里的路径 ----
        string empty = Path.Combine(root, "wpe-props-empty");
        Directory.CreateDirectory(empty);
        Directory.CreateDirectory(Path.Combine(install, "assets"));
        await File.WriteAllTextAsync(Path.Combine(install, "wallpaper64.exe"), "");
        var located = WallpaperEngineProperties.LocateConfig([(empty, "running_wallpaper_engine"), (null, "steam_library"), (install, "configured_path")]);
        check(located is { Origin: "configured_path" } && located.Path == config &&
            WallpaperEngineProperties.LocateConfig([(install, "running_wallpaper_engine"), (install, "configured_path")]) is { Origin: "running_wallpaper_engine" } &&
            WallpaperEngineProperties.LocateConfig([(empty, "steam_library")]) is null &&
            WallpaperEngineProperties.ConfiguredDirectory(Path.Combine(install, "wallpaper64.exe")) == install &&
            WallpaperEngineProperties.ConfiguredDirectory(Path.Combine(install, "assets")) == install,
            "config.json is located in order, skipping folders without one; an executable or assets path resolves to the install folder");

        // ---- plan 接线与结论句 ----
        string traceSource = Path.Combine(root, "wpe-props-plan-source");
        Directory.CreateDirectory(traceSource);
        var objects = new JsonArray(new JsonObject { ["id"] = 10, ["name"] = "背景", ["image"] = "models/background.json",
            ["size"] = "64 32", ["origin"] = "32 16 0" });
        string scenePath = Path.Combine(traceSource, "scene.json");
        await File.WriteAllTextAsync(scenePath, new JsonObject {
            ["general"] = new JsonObject { ["orthogonalprojection"] = new JsonObject { ["width"] = 64, ["height"] = 32 }, ["clearenabled"] = true },
            ["objects"] = objects }.ToJsonString());
        await File.WriteAllTextAsync(Path.Combine(traceSource, "project.json"), project.ToJsonString());
        string tracePath = Path.Combine(root, "wpe-props-trace.json");
        await File.WriteAllTextAsync(tracePath, new JsonObject {
            ["source"] = scenePath, ["status"] = "complete", ["runtime_dependencies"] = new JsonArray(),
            ["source_script_error_count"] = 0, ["source_script_errors"] = new JsonArray(),
            ["runtime_layers"] = new JsonArray(new JsonObject { ["id"] = 10, ["owner"] = 10, ["visible"] = true, ["has_mesh"] = true,
                ["effective_parallax_depth"] = new JsonArray(0, 0),
                ["materials"] = new JsonArray(new JsonObject { ["uses_audio_spectrum"] = false, ["textures"] = new JsonArray() }) }) }.ToJsonString());
        async Task<JsonObject> PlanAsync(string name, JsonObject? properties, JsonObject? propertiesOrigin) =>
            await new HybridScenePlanner(new("not-started", "not-started", "not-started", [])).AnalyzeSingleAsync(
                new(2, traceSource, root, Path.Combine(root, name), 64, 32, UserProperties: properties,
                    RuntimeTraceFile: tracePath, VideoLayout: "layered", PropertiesOrigin: propertiesOrigin));
        JsonObject withWpe = await PlanAsync("wpe-props-plan-wpe", merged, origin);
        JsonObject plain = await PlanAsync("wpe-props-plan-plain", null, null);
        string[] keys = withWpe.Select(pair => pair.Key).ToArray();
        int at = Array.IndexOf(keys, "snapshot_properties");
        check(Text(withWpe["properties_source"]) == "wpe" && at >= 0 && keys[at + 1] == "properties_source" && keys[at + 2] == "wpe_properties" &&
            withWpe["wpe_properties"]!["source"] is null && Names(withWpe["wpe_properties"]!["applied_keys"]).Length == 3 &&
            !withWpe["settings"]!.AsObject().ContainsKey("properties_origin") &&
            !withWpe["snapshot_properties"]!["sponsor"]!.GetValue<bool>() && withWpe["snapshot_properties"]!["volume"]!.GetValue<double>() == .5,
            "the plan records properties_source and wpe_properties after the snapshot, never inside settings");
        check(!plain.ContainsKey("properties_source") && !plain.ContainsKey("wpe_properties") &&
            !JsonSerializer.SerializeToNode(new HybridAnalyzeRequest(2, "s", "a", "o"))!.AsObject().ContainsKey("PropertiesOrigin"),
            "requests without an origin record produce plans without the new fields");

        // ---- GUI 投影：预填、标注、说明 ----
        JsonObject definitions = project["general"]!["properties"]!.AsObject();
        var overrides = new JsonObject { ["volume"] = 0.4 };
        JsonObject shownValues = AppJsonPresentation.ComposePropertyValues(definitions, null, overrides, resolved.Values);
        JsonObject analyzedValues = AppJsonPresentation.ComposePropertyValues(definitions, new JsonObject { ["sponsor"] = true }, new JsonObject(), resolved.Values);
        var (guiProperties, guiOrigin) = AppJsonPresentation.MergeWpeProperties(resolved, definitions, overrides);
        var (noResolution, noOrigin) = AppJsonPresentation.MergeWpeProperties(null, definitions, overrides);
        check(shownValues["volume"]!.GetValue<double>() == .4 && !shownValues["sponsor"]!.GetValue<bool>() &&
            shownValues["mode"]!.GetValue<string>() == "2" && shownValues["rain"]!.GetValue<bool>() &&
            analyzedValues["sponsor"]!.GetValue<bool>() &&
            guiProperties["volume"]!.GetValue<double>() == .4 && !guiProperties["sponsor"]!.GetValue<bool>() && guiOrigin is not null &&
            noResolution.Count == 1 && noOrigin is null,
            "the property panel prefills WPE values under panel edits and above defaults, and analysis merges them the same way");
        check(AppJsonPresentation.WpeMarkedKeys(null, resolved, overrides).Order(StringComparer.Ordinal).SequenceEqual(["caption", "mode", "sponsor"]) &&
            AppJsonPresentation.WpeMarkedKeys(withWpe, resolved, new JsonObject()).Order(StringComparer.Ordinal).SequenceEqual(["caption", "mode", "sponsor"]) &&
            AppJsonPresentation.PropertySourceNote(defaults, false) == "",
            "panel marks follow the plan's applied keys, or unedited WPE values before analysis, with a note on the source");
    }

    private static JsonObject Project() => new()
    {
        ["type"] = "scene", ["file"] = "scene.json",
        ["general"] = new JsonObject { ["properties"] = new JsonObject {
            ["sponsor"] = new JsonObject { ["type"] = "bool", ["value"] = true },
            ["volume"] = new JsonObject { ["type"] = "slider", ["min"] = 0, ["max"] = 1, ["value"] = 0.8 },
            ["mode"] = new JsonObject { ["type"] = "combo", ["value"] = "1", ["options"] = new JsonArray(
                new JsonObject { ["label"] = "one", ["value"] = "1" }, new JsonObject { ["label"] = "two", ["value"] = "2" }) },
            ["tint"] = new JsonObject { ["type"] = "color", ["value"] = "1 1 1" },
            ["caption"] = new JsonObject { ["type"] = "textinput", ["value"] = "hello" },
            ["heading"] = new JsonObject { ["type"] = "text", ["value"] = "" },
            ["rain"] = new JsonObject { ["type"] = "bool", ["value"] = true } } }
    };

    private static JsonObject Selected(params (string Location, string File)[] items)
    {
        var selected = new JsonObject();
        foreach (var (location, file) in items) selected[location] = new JsonObject { ["file"] = file };
        return new JsonObject { ["wallpaperconfig"] = new JsonObject { ["selectedwallpapers"] = selected } };
    }

    private static JsonObject Monitors(params (string Location, JsonObject Values)[] items)
    {
        var entry = new JsonObject();
        foreach (var (location, values) in items) entry[location] = values;
        return entry;
    }

    private static JsonObject Snapshot(JsonObject project, JsonObject? properties) =>
        (JsonObject)typeof(SceneGraph).GetMethod("SnapshotProperties", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, [project, properties])!;

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    private static string[] Names(JsonNode? node) => (node as JsonArray ?? []).Select(item => item!.GetValue<string>()).ToArray();
}
