using System.Text.Json.Nodes;
using Baker.App;

internal static class SavedLoopApplyChecks
{
    internal static void Run(Action<bool, string> check)
    {
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
        string prefixSummary = AppJsonPresentation.LoopSummary(prefixes["plan"]!.AsObject(), null, true);
        check(prefixSummary.Contains("owner 55") && prefixSummary.Contains("owner 81") && prefixSummary.Contains("later effects stay live") &&
            !prefixSummary.Contains("complete loop"), "effect-prefix summary lists each period and retained live suffix");
        string validationSummary = AppJsonPresentation.HybridValidationSummary(prefixes, true);
        check(validationSummary.Contains("Official WPE playback and GPU power benefit are unverified"), "effect-prefix summary does not claim playback or power benefit");
        prefixes["plan"]!["effect_prefix_caches"]![0]!["loop"]!["unresolved"]!.AsArray().Add(new JsonObject());
        check(!AppJsonPresentation.CandidateCanApply(prefixes), "effect-prefix with unresolved loop mechanism is rejected");
        prefixes["plan"]!["effect_prefix_caches"]![0]!["loop"]!["unresolved"]!.AsArray().Clear();
        prefixes["plan"]!["effect_prefix_caches"]![1]!["loop"]!["status"] = "observational_candidate_requires_seam_validation";
        check(!AppJsonPresentation.CandidateCanApply(prefixes), "effect-prefix with observational loop evidence is rejected");
        prefixes["plan"]!["effect_prefix_caches"]![1]!["loop"]!["status"] = "analytic_candidate_requires_seam_validation";
        prefixes["groups"]![0]!["local_repair"] = new JsonObject();
        check(!AppJsonPresentation.CandidateCanApply(prefixes), "repaired effect-prefix output is rejected");
    }
}
