using System.Text.Json.Nodes;
using Baker.App;

internal static class SavedLoopApplyChecks
{
    internal static void Run(Action<bool, string> check)
    {
        TestTemp.Run(async dir =>
        {
            CheckSavedProjectPaths(check, dir);
            await CheckLivePublication(check, dir);
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
        Directory.CreateDirectory(Path.Combine(failedWork, "bake.json"));
        bool writeFailed = false;
        try { await AppEnvironment.PublishLiveSceneAsync(report.DeepClone().AsObject(), failedWork, failedProject, CancellationToken.None); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { writeFailed = true; }
        check(writeFailed && !Directory.Exists(failedProject) && File.Exists(Path.Combine(failedWork, "project.json")),
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
        JsonObject saved = JsonNode.Parse(File.ReadAllText(Path.Combine(project, "bake.json")))!.AsObject();
        check(!Directory.Exists(work) && File.Exists(Path.Combine(project, "project.json")) &&
            File.ReadAllText(Path.Combine(project, "scene.json")) == "{\"objects\":[]}" &&
            saved["output"]?.GetValue<string>() == project && saved["project_path"]?.GetValue<string>() == project &&
            AppJsonPresentation.ResolveCompletedProject(saved, project) == project && AppJsonPresentation.CandidateCanApply(saved),
            "successful live publication moves project, resources and a loadable final-root report together");
    }
}
