using System.Text.Json.Nodes;
using Baker.App;
using Baker.Core;

internal static class SavedLoopApplyChecks
{
    internal static void Run(Action<bool, string> check)
    {
        TestTemp.Run(async dir =>
        {
            CheckSavedProjectPaths(check, dir);
            await CheckLivePublication(check, dir);
            await CheckApplicationContracts(check, dir);
            CheckTerminalSourceIdentity(check, dir);
        }).GetAwaiter().GetResult();
        var report = JsonNode.Parse("""
            {"schema_version":2,"artifact_kind":"hybrid_video_candidate","status":"candidate_generated",
             "source_start_frame":0,"plan":{"loop":{"status":"analytic_candidate_requires_seam_validation",
             "source_static":false,"candidates":[{"frames":1199}],"unresolved":[]}}}
            """)!.AsObject();
        check(AppJsonPresentation.CandidateCanApply(report), "a saved source-derived video remains applicable");
        report["groups"] = new JsonArray(new JsonObject { ["local_repair"] = new JsonObject { ["status"] = "observed_seam_pass" } });
        check(!AppJsonPresentation.CandidateCanApply(report), "a saved repaired candidate cannot bypass Apply");
        report.Remove("groups");
        report["plan"]!["loop"]!["status"] = "observational_candidate_requires_seam_validation";
        check(!AppJsonPresentation.CandidateCanApply(report), "a saved image-derived loop cannot bypass generation through Apply");
        report["plan"]!["loop"]!["status"] = "analytic_candidate_requires_seam_validation";
        report["source_start_frame"] = 120;
        check(!AppJsonPresentation.CandidateCanApply(report), "a saved nonzero capture origin cannot be applied");
        report.Remove("source_start_frame");
        check(!AppJsonPresentation.CandidateCanApply(report), "a saved report without an explicit source origin requires regeneration");
        var masked = JsonNode.Parse("""
            {"schema_version":2,"artifact_kind":"hybrid_video_candidate","status":"candidate_generated",
             "seam_policy":"analytic_period_with_residual_masking","source_start_frame":37,"crossfade_frames":24,
             "residual_masking":{"status":"residual_maskable","blocking_components":[]},
             "seam_residual":{"status":"observed_within_residual_limits","first_layer":{"passed":true}},
             "loop_crossfade":{"status":"applied","weight_verification":{"status":"verified_against_source_frames"}},
             "plan":{"loop":{"status":"analytic_candidate_requires_seam_validation","source_static":false,
             "candidates":[{"frames":1199}],"unresolved":[{"kind":"random_sprite"}]}}}
            """)!.AsObject();
        check(AppJsonPresentation.CandidateCanApply(masked), "a residual-masked candidate with an applied, weight-verified crossfade is applicable");

        static JsonObject Period(ulong frames, double seconds) => new()
        {
            ["status"] = "analytic_candidate_requires_seam_validation",
            ["candidates"] = new JsonArray(new JsonObject
                { ["frames"] = JsonValue.Create(frames), ["seconds"] = JsonValue.Create(seconds) }),
            ["unresolved"] = new JsonArray()
        };
        static JsonObject Cache(int owner, int prefix, int terminal, string image, ulong frames, double seconds) => new()
        {
            ["owner_layer_id"] = owner, ["prefix_effect_count"] = prefix, ["terminal_effect_id"] = terminal,
            ["source_image"] = image, ["fixed_user_properties"] = new JsonObject(), ["loop"] = Period(frames, seconds)
        };
        static JsonObject Group(int owner, ulong frames, double seconds) => new()
        {
            ["owner_layer_id"] = owner, ["status"] = "encoded", ["frames"] = JsonValue.Create(frames),
            ["period"] = Period(frames, seconds),
            ["encoded_loop_validation"] = new JsonObject { ["status"] = "observed_seam_pass" },
            ["hardware_decode"] = new JsonObject { ["all_adapters_passed"] = true }
        };
        var prefixes = new JsonObject
        {
            ["schema_version"] = 2, ["artifact_kind"] = "hybrid_video_candidate", ["status"] = "candidate_generated",
            ["source_start_frame"] = JsonValue.Create(0), ["seam_policy"] = "source_period_no_repair",
            ["official_playback"] = "not_verified", ["measured_gain"] = "not_verified",
            ["plan"] = new JsonObject { ["route"] = "effect_prefix", ["effect_prefix_caches"] = new JsonArray(
                Cache(55, 1, 4, "images/a.jpg", 120, 2), Cache(81, 2, 7, "images/b.jpg", 300, 5)) },
            ["groups"] = new JsonArray(Group(55, 120, 2), Group(81, 300, 5)),
            ["composition_validation"] = new JsonObject { ["status"] = "composition_pass" }
        };
        check(AppJsonPresentation.CandidateCanApply(prefixes), "independent verified effect-prefix caches can be applied without a global loop or measured gain");
        JsonObject ordinalCandidate = JsonNode.Parse(prefixes.ToJsonString())!.AsObject();
        JsonObject ordinalCache = ordinalCandidate["plan"]!["effect_prefix_caches"]![0]!.AsObject();
        ordinalCache["terminal_effect_id"] = null;
        ordinalCache["terminal_effect_ordinal"] = 0;
        check(AppJsonPresentation.CandidateCanApply(ordinalCandidate),
            "a JSON report with an ID-less terminal at prefix-1 remains applicable (contract only, no rendering proof)");
        foreach (var (field, value) in new (string, JsonNode?)[] {
            ("terminal_effect_id", JsonValue.Create(1.5)), ("terminal_effect_id", JsonValue.Create(-1)),
            ("terminal_effect_ordinal", JsonValue.Create(1)), ("terminal_effect_ordinal", JsonValue.Create(-1)),
            ("terminal_effect_ordinal", JsonValue.Create(0.5)), ("terminal_effect_ordinal", null),
            ("prefix_effect_count", JsonValue.Create(1.5)) })
        {
            JsonObject invalid = ordinalCandidate.DeepClone().AsObject();
            invalid["plan"]!["effect_prefix_caches"]![0]![field] = value?.DeepClone();
            check(!AppJsonPresentation.CandidateCanApply(invalid), "malformed terminal identity cannot enable Apply: " + field);
        }
        check(AppJsonPresentation.Number(JsonValue.Create(0)) == 0 &&
            AppJsonPresentation.Number(JsonValue.Create(uint.MaxValue)) == uint.MaxValue &&
            AppJsonPresentation.Number(JsonValue.Create(ulong.MaxValue)) == (double)ulong.MaxValue &&
            AppJsonPresentation.Number(JsonValue.Create(1.5f)) == 1.5 &&
            AppJsonPresentation.Number(JsonValue.Create(float.PositiveInfinity)) is null,
            "Number accepts in-memory integer/floating JsonValues and rejects non-finite values");
        prefixes["plan"]!["effect_prefix_caches"]![0]!["loop"]!["unresolved"]!.AsArray().Add(new JsonObject());
        check(!AppJsonPresentation.CandidateCanApply(prefixes), "effect-prefix with unresolved loop mechanism is rejected");
        prefixes["plan"]!["effect_prefix_caches"]![0]!["loop"]!["unresolved"]!.AsArray().Clear();
        prefixes["plan"]!["effect_prefix_caches"]![1]!["loop"]!["status"] = "observational_candidate_requires_seam_validation";
        check(!AppJsonPresentation.CandidateCanApply(prefixes), "effect-prefix with observational loop evidence is rejected");
        prefixes["plan"]!["effect_prefix_caches"]![1]!["loop"]!["status"] = "analytic_candidate_requires_seam_validation";
        prefixes["groups"]![0]!["local_repair"] = new JsonObject();
        check(!AppJsonPresentation.CandidateCanApply(prefixes), "repaired effect-prefix output is rejected");
    }

    private static async Task CheckApplicationContracts(Action<bool, string> check, string dir)
    {
        string project = Directory.CreateDirectory(Path.Combine(dir, "application-project")).FullName;
        File.WriteAllText(Path.Combine(project, "project.json"), """
            {"type":"scene","file":"scene.json","general":{"properties":{"canvaswidth":{"type":"slider","value":2560}}}}
            """);
        File.WriteAllText(Path.Combine(project, "scene.json"), """
            {"general":{"orthogonalprojection":{"width":{"user":"canvaswidth","value":1920},"height":1440}},"objects":[]}
            """);
        var livePlan = new JsonObject { ["source"] = "unavailable-original", ["snapshot_properties"] = new JsonObject { ["canvaswidth"] = 64 } };
        check(AppJsonPresentation.PreviewDimensions(livePlan, true, project, () => null) == (2560u, 1440u) && !livePlan.ContainsKey("settings"),
            "saved live preview reads the actual project canvas and property values without adding hybrid settings");
        check(AppJsonPresentation.PreviewDimensions(livePlan, true, project, () => (3840u, 2160u)) == (3840u, 2160u),
            "live preview uses the shared display resolution rule");
        var hybridPlan = new JsonObject { ["settings"] = PlanSettings.ToJson(new HybridAnalyzeRequest(2, "missing", "missing", "missing", 1280, 720)) };
        check(AppJsonPresentation.PreviewDimensions(hybridPlan, false, "missing", () => throw new InvalidOperationException()) == (1280u, 720u),
            "hybrid preview keeps its saved dimensions without consulting display or source");

        string recordsRoot = Path.Combine(dir, "application-records");
        string first = AppEnvironment.NewApplicationDirectory(project, recordsRoot);
        string second = AppEnvironment.NewApplicationDirectory(project, recordsRoot);
        check(first != second && AppEnvironment.OutputValid(first, project) && AppEnvironment.OutputValid(second, project) &&
            !Directory.Exists(first) && !Directory.Exists(second), "application record paths are fresh and outside the published project");
        bool rejectedInside = false;
        try { await new WallpaperController("not-started").ApplyAsync(new(1, project, "account", "Monitor0", Path.Combine(project, "apply-inside"))); }
        catch (IOException error) { rejectedInside = error.Message.Contains("inside the project", StringComparison.Ordinal); }
        check(rejectedInside && !Directory.Exists(Path.Combine(project, "apply-inside")), "the Core inside-project application guard remains active before any command");
        using var source = new ProjectSource(project);
        string beforeHash = await source.SourceHashAsync();
        Directory.CreateDirectory(first); Directory.CreateDirectory(second);
        string manifest0 = Path.Combine(first, "apply.json"), manifest1 = Path.Combine(second, "apply.json");
        File.WriteAllText(manifest0, "{\"profile\":\"account\",\"location\":\"Monitor0\",\"status\":\"prepared\"}");
        File.WriteAllText(manifest1, "{\"profile\":\"account\",\"location\":\"Monitor1\",\"status\":\"applied\"}");
        check(await source.SourceHashAsync() == beforeHash, "two target application records do not alter the project's source identity");
        var records = new List<(string Profile, string Location, string Manifest, bool Restored)> {
            ("account", "Monitor0", manifest0, false), ("account", "Monitor1", manifest1, false) };
        check(AppJsonPresentation.ApplicationManifest(records, "account", "Monitor0") == manifest0 &&
            AppJsonPresentation.ApplicationManifest(records, "account", "Monitor1") == manifest1 &&
            AppJsonPresentation.ApplicationManifest(records, "another-account", "Monitor0") is null &&
            AppJsonPresentation.ApplicationManifest(records, "account", "Monitor2") is null,
            "prepared recovery records block only their own profile and location, leaving other targets available");
        AppJsonPresentation.MarkApplicationRestored(records, manifest0);
        check(AppJsonPresentation.ApplicationManifest(records, "account", "Monitor0") is null &&
            AppJsonPresentation.ApplicationManifest(records, "account", "Monitor1") == manifest1 && File.Exists(manifest0),
            "restoring one target retains the other target's recovery point and the original record file");
        string reapplied = Path.Combine(dir, "apply-again.json");
        records.Add(("account", "Monitor0", reapplied, false));
        check(records.Count == 3 && records[0].Restored && AppJsonPresentation.ApplicationManifest(records, "account", "Monitor0") == reapplied,
            "reapplication retains the earlier restoration history");
    }

    private static void CheckTerminalSourceIdentity(Action<bool, string> check, string dir)
    {
        string project = Directory.CreateDirectory(Path.Combine(dir, "terminal-identity-project")).FullName;
        File.WriteAllText(Path.Combine(project, "project.json"), "{\"type\":\"scene\",\"file\":\"scene.json\"}");
        File.WriteAllText(Path.Combine(project, "model.json"), "{\"autosize\":true,\"material\":\"material.json\"}");
        File.WriteAllText(Path.Combine(project, "material.json"), "{\"passes\":[{\"shader\":\"genericimage2\",\"textures\":[\"base\"]}]}");
        File.WriteAllText(Path.Combine(project, "effect.json"), "{\"passes\":[{\"material\":\"effect-material.json\"}]}");
        File.WriteAllText(Path.Combine(project, "effect-material.json"), "{\"passes\":[{\"shader\":\"effect\"}]}");
        File.WriteAllText(Path.Combine(project, "scene.json"), """
            {"objects":[{"id":1,"image":"model.json","effects":[{"file":"effect.json","passes":[{}]}]}]}
            """);
        using var source = new ProjectSource(project);
        JsonObject scene = source.ReadJson(source.SceneResource);
        JsonObject cache = JsonNode.Parse("""
            {"owner_layer_id":1,"prefix_effect_count":1,"terminal_effect_id":null,"terminal_effect_ordinal":0,
             "source_image":"model.json","loop":{},"fixed_user_properties":{}}
            """)!.AsObject();
        EffectPrefixBakeService.ValidateSource(source, scene, cache);
        check(EffectPrefixCaptureTarget.HasValidTerminalIdentity(cache), "the same ID-less ordinal identity passes Core source validation against a disk-backed scene");
        cache["terminal_effect_id"] = 99;
        bool rejected = false;
        try { EffectPrefixBakeService.ValidateSource(source, scene, cache); }
        catch (InvalidDataException) { rejected = true; }
        check(rejected, "an invented terminal ID cannot match an ID-less authored effect");
    }

    private static void CheckSavedProjectPaths(Action<bool, string> check, string dir)
    {
        static JsonObject ReadReport(string output) => JsonNode.Parse(File.ReadAllText(Path.Combine(output, "bake.json")))!.AsObject();
        string root = Directory.CreateDirectory(Path.Combine(dir, "root")).FullName;
        var report = new JsonObject { ["schema_version"] = 1, ["artifact_kind"] = "live_scene_optimized",
            ["status"] = "optimized", ["fused_pairs"] = 1, ["project_path"] = root, ["source_sha256"] = "hash" };
        File.WriteAllText(Path.Combine(root, "bake.json"), report.ToJsonString());
        File.WriteAllText(Path.Combine(root, "project.json"), "{\"type\":\"scene\",\"file\":\"scene.json\"}");
        check(AppJsonPresentation.ResolveCompletedProject(ReadReport(root), root) == root,
            "live report loads the project beside bake.json");

        string legacy = Directory.CreateDirectory(Path.Combine(dir, "legacy")).FullName;
        string nested = Directory.CreateDirectory(Path.Combine(legacy, "project")).FullName;
        report["project_path"] = nested;
        File.WriteAllText(Path.Combine(legacy, "bake.json"), report.ToJsonString());
        File.Copy(Path.Combine(root, "project.json"), Path.Combine(nested, "project.json"));
        check(AppJsonPresentation.ResolveCompletedProject(ReadReport(legacy), legacy) == nested,
            "live report still loads the legacy project subfolder");

        string copied = Directory.CreateDirectory(Path.Combine(dir, "copied")).FullName;
        File.Copy(Path.Combine(legacy, "bake.json"), Path.Combine(copied, "bake.json"));
        File.Copy(Path.Combine(nested, "project.json"), Path.Combine(copied, "project.json"));
        check(AppJsonPresentation.ResolveCompletedProject(ReadReport(copied), copied) == copied,
            "a copied live result loads its local root even with a stale declared project path");

        string missing = Directory.CreateDirectory(Path.Combine(dir, "missing")).FullName;
        report["project_path"] = Path.Combine(missing, "project");
        File.WriteAllText(Path.Combine(missing, "bake.json"), report.ToJsonString());
        check(AppJsonPresentation.ResolveCompletedProject(ReadReport(missing), missing) is null,
            "live report without a local project cannot load");
        File.Copy(Path.Combine(root, "bake.json"), Path.Combine(missing, "bake.json"), overwrite: true);
        check(AppJsonPresentation.ResolveCompletedProject(ReadReport(missing), missing) is null,
            "live report cannot fall back to an existing external declared project");
        JsonObject hybrid = ReadReport(missing);
        hybrid["artifact_kind"] = "hybrid_video_candidate";
        check(AppJsonPresentation.ResolveCompletedProject(hybrid, missing) == root,
            "non-live report retains its existing declared-project fallback");
    }

    private static async Task CheckLivePublication(Action<bool, string> check, string dir)
    {
        string work = Directory.CreateDirectory(Path.Combine(dir, "publish-work")).FullName;
        string project = Path.Combine(dir, "published");
        File.WriteAllText(Path.Combine(work, "project.json"), "{\"type\":\"scene\",\"file\":\"scene.json\"}");
        File.WriteAllText(Path.Combine(work, "scene.json"), "{\"objects\":[]}");
        var report = new JsonObject { ["schema_version"] = 1, ["artifact_kind"] = "live_scene_optimized",
            ["status"] = "optimized", ["fused_pairs"] = 1, ["output"] = work, ["source_sha256"] = "hash" };
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        bool stopped = false;
        try { await AppEnvironment.PublishLiveSceneAsync(report, work, project, cancelled.Token); }
        catch (OperationCanceledException) { stopped = true; }
        check(stopped && !Directory.Exists(project) && File.Exists(Path.Combine(work, "project.json")),
            "cancelled live publication leaves the final folder absent and work intact");

        string failedWork = Directory.CreateDirectory(Path.Combine(dir, "failed-report-work")).FullName;
        string failedProject = Path.Combine(dir, "failed-report-final");
        File.Copy(Path.Combine(work, "project.json"), Path.Combine(failedWork, "project.json"));
        string movedWork = failedWork + "-moved";
        Directory.Move(failedWork, movedWork);
        bool writeFailed = false;
        try { await AppEnvironment.PublishLiveSceneAsync(report.DeepClone().AsObject(), failedWork, failedProject, CancellationToken.None); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { writeFailed = true; }
        check(writeFailed && !Directory.Exists(failedProject) && File.Exists(Path.Combine(movedWork, "project.json")),
            "report write failure leaves the final folder absent and work intact");

        string occupied = Directory.CreateDirectory(Path.Combine(dir, "occupied-final")).FullName;
        File.WriteAllText(Path.Combine(occupied, "project.json"), "{\"title\":\"existing project\"}");
        bool moveFailed = false;
        try { await AppEnvironment.PublishLiveSceneAsync(report, work, occupied, CancellationToken.None); }
        catch (IOException error) { moveFailed = error.Message.Contains(work, StringComparison.Ordinal); }
        check(moveFailed && File.ReadAllText(Path.Combine(occupied, "project.json")) == "{\"title\":\"existing project\"}" &&
            File.Exists(Path.Combine(work, "project.json")),
            "live publication refuses an existing final folder and reports retained work");

        await AppEnvironment.PublishLiveSceneAsync(report, work, project, CancellationToken.None);
        JsonObject saved = JsonNode.Parse(File.ReadAllText(report["publication_report_path"]!.GetValue<string>()))!.AsObject();
        check(!Directory.Exists(work) && File.Exists(Path.Combine(project, "project.json")) &&
            File.ReadAllText(Path.Combine(project, "scene.json")) == "{\"objects\":[]}" &&
            saved["output"]?.GetValue<string>() == project && saved["project_path"]?.GetValue<string>() == project &&
            AppJsonPresentation.ResolveCompletedProject(saved, project) == project && AppJsonPresentation.CandidateCanApply(saved),
            "successful live publication moves project, resources and a loadable final-root report together");
    }
}
