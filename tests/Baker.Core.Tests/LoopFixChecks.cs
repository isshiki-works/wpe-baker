using System.Text.Json.Nodes;
using Baker.Core;

/// <summary>
/// 发布前审查里循环/残差/接缝条目的反例检查：H2（随机重启判定按轨道而非 owner）、M1（起点搜索步长取 gcd(P,16)）、
/// M2（搜索窗固定 2P）、M8（面积未知不当零）、以及源周期路线的候选回退。夹具都是合成的最小场景。
/// </summary>
internal static class LoopFixChecks
{
    internal static void Run(Action<bool, string> check, string root)
    {
        string sourceDirectory = Path.Combine(root, "loop-fix-source");
        Directory.CreateDirectory(sourceDirectory);
        File.WriteAllText(Path.Combine(sourceDirectory, "project.json"), "{\"type\":\"scene\",\"file\":\"scene.json\"}");
        File.WriteAllText(Path.Combine(sourceDirectory, "scene.json"), "{\"objects\":[]}");
        using var source = new ProjectSource(sourceDirectory);

        // ---- H2 反例 A：同一 owner 两条轨道——精灵 flip 由脚本随机重启，骨骼 sway 被脚本持续写入。
        const string randomRestartScript = "'use strict';\nfunction randomNumber(min, max) { return Math.random() * (max - min) + min; }\n" +
            "export function update(value) { const ani = thisLayer.getTextureAnimation(); if (ani.getFrame() === ani.frameCount - 1) { " +
            "engine.setTimeout(() => { ani.stop(); engine.setTimeout(() => ani.play(), randomNumber(1, 5) * 1000); }, 100); } return value; }";
        JsonObject Owner(int id, string script) => new()
        {
            ["id"] = id, ["size"] = "100 100",
            ["visible"] = new JsonObject { ["value"] = true, ["script"] = script },
            ["animationlayers"] = new JsonArray(new JsonObject { ["id"] = 50, ["name"] = "sway", ["rate"] = 1 })
        };
        JsonObject SpriteTrace(int owner, string track) => new()
        {
            ["source_owner_layer_id"] = owner, ["mechanism"] = "sprite", ["track_name"] = track, ["duration_seconds"] = 1.68,
            ["looping"] = true, ["playback_mode"] = "loop", ["event_driven"] = false, ["confidence"] = "high"
        };
        JsonObject BoneTrace(int owner, string track) => new()
        {
            ["source_owner_layer_id"] = owner, ["mechanism"] = "puppet_bone", ["track_name"] = track, ["duration_seconds"] = 2,
            ["looping"] = true, ["playback_mode"] = "loop", ["event_driven"] = false, ["confidence"] = "high", ["playback_rate"] = 1
        };
        JsonObject Write(int owner, string property) => new()
        { ["owner"] = owner, ["target"] = owner, ["operation"] = "write", ["property"] = property, ["initialization"] = false };
        JsonObject Analyze(JsonObject owner, JsonArray traces, JsonArray dependencies) => LoopAnalysis.Analyze(
            new JsonObject { ["objects"] = new JsonArray { owner } }, source, null,
            new JsonObject { ["runtime_animation_periods"] = traces, ["runtime_dependencies"] = dependencies },
            [owner["id"]!.GetValue<int>()], 60, 1).ToJson();

        JsonObject dual = Analyze(Owner(5, randomRestartScript), new JsonArray { SpriteTrace(5, "flip"), BoneTrace(5, "sway") },
            new JsonArray { Write(5, "animation") });
        JsonObject[] dualUnresolved = dual["unresolved"]!.AsArray().OfType<JsonObject>().ToArray();
        JsonObject? flip = dualUnresolved.SingleOrDefault(x => x["track_name"]?.GetValue<string>() == "flip");
        JsonObject? sway = dualUnresolved.SingleOrDefault(x => x["track_name"]?.GetValue<string>() == "sway");
        check(flip?["random_restart"]?.GetValue<bool>() == true && flip["mechanism"]?.GetValue<string>() == "sprite" &&
            sway?["random_restart"]?.GetValue<bool>() == false && sway["mechanism"]?.GetValue<string>() == "puppet_bone",
            "a script-controlled bone track on the same owner never borrows the sprite's random-restart proof");

        JsonObject Plan(int owner, JsonArray unresolved, double? fraction = 0.05) => new()
        {
            ["settings"] = new JsonObject { ["width"] = 1920, ["height"] = 1080 },
            ["canvas_width"] = 1920, ["canvas_height"] = 1080,
            ["layers"] = new JsonArray(new JsonObject { ["id"] = owner, ["name"] = "owner", ["canvas_fraction"] = fraction }),
            ["loop"] = new JsonObject { ["candidates"] = new JsonArray(new JsonObject { ["frames"] = 600 }), ["unresolved"] = unresolved }
        };
        JsonObject classified = ResidualMasking.Classify(Plan(5, new JsonArray(flip!.DeepClone(), sway!.DeepClone())),
            new JsonObject { ["objects"] = new JsonArray { Owner(5, randomRestartScript) } }, _ => null);
        check(classified["status"]?.GetValue<string>() == "rejected" &&
            classified["blocking_components"]!.AsArray().OfType<JsonObject>().Single()["source_detail"]?.GetValue<string>()?
                .Contains("Script-controlled", StringComparison.Ordinal) == true &&
            classified["residual_layers"]!.AsArray().OfType<JsonObject>().Single()["classification"]?.GetValue<string>() == "random_sprite",
            "residual masking reads the structured random_restart field: the bone track blocks while the random sprite stays maskable");

        // ---- H2 反例 B：脚本只读精灵帧，Math.random 在别处做颜色；唯一轨道是被脚本写入的属性动画。
        const string colourScript = "export function update(value) { thisLayer.getTextureAnimation().getFrame(); " +
            "thisLayer.color = new Vec3(Math.random(), 0, 0); engine.setTimeout(() => {}, 10); return value; }";
        JsonObject colour = Analyze(Owner(6, colourScript), new JsonArray { BoneTrace(6, "sway") }, new JsonArray { Write(6, "animation") });
        JsonObject colourItem = colour["unresolved"]!.AsArray().OfType<JsonObject>().Single();
        JsonObject colourClassified = ResidualMasking.Classify(Plan(6, new JsonArray(colourItem.DeepClone())),
            new JsonObject { ["objects"] = new JsonArray { Owner(6, colourScript) } }, _ => null);
        check(colourItem["random_restart"]?.GetValue<bool>() == false && colourClassified["status"]?.GetValue<string>() == "rejected" &&
            colourClassified["blocking_components"]!.AsArray().OfType<JsonObject>().Single()["classification"]?.GetValue<string>() == "unrecognized",
            "Math.random used for colour elsewhere in the script does not turn a controlled animation track into a maskable random sprite");
        // 精灵轨道、脚本只读帧且 Math.random 做颜色：外部写入让它成为脚本控制，但不是随机重启。
        JsonObject readOnlySprite = Analyze(Owner(6, colourScript), new JsonArray { SpriteTrace(6, "flip") },
            new JsonArray { Write(6, "textureAnimation") });
        JsonObject readOnlyItem = readOnlySprite["unresolved"]!.AsArray().OfType<JsonObject>().Single();
        check(readOnlyItem["mechanism"]?.GetValue<string>() == "sprite" && readOnlyItem["random_restart"]?.GetValue<bool>() == false,
            "a sprite whose script never controls playback is script-controlled, not proven random, even with Math.random in the file");
        // 真随机精灵脚本 + 无外部写入：靠脚本自身判出随机重启。
        JsonObject selfRestart = Analyze(Owner(7, randomRestartScript), new JsonArray { SpriteTrace(7, "flip") }, new JsonArray());
        check(selfRestart["unresolved"]!.AsArray().OfType<JsonObject>().Single()["random_restart"]?.GetValue<bool>() == true,
            "a sprite whose own script stops and replays it after a Math.random timer is proven random");
        // 脚本指名另一条纹理动画：不算这条轨道的证据。
        string otherTrackScript = randomRestartScript.Replace("getTextureAnimation()", "getTextureAnimation('other')", StringComparison.Ordinal);
        JsonObject otherTrack = Analyze(Owner(8, otherTrackScript), new JsonArray { SpriteTrace(8, "flip") }, new JsonArray());
        check(otherTrack["unresolved"]!.AsArray().OfType<JsonObject>().Single()["random_restart"]?.GetValue<bool>() == false,
            "a random-restart script that names a different texture animation is not evidence for this track");

        // ---- M1：采样步长取 gcd(P, 16)。
        check(ResidualMasking.StartSearchStride(6000) == 16 && ResidualMasking.StartSearchStride(120) == 8 &&
            ResidualMasking.StartSearchStride(1800) == 8 && ResidualMasking.StartSearchStride(121) == 1 &&
            ResidualMasking.StartSearchStride(96) == 16 && ResidualMasking.StartSearchStride(100) == 4 &&
            120 % ResidualMasking.StartSearchStride(120) == 0 && 1800 % ResidualMasking.StartSearchStride(1800) == 0,
            "the start-search stride is gcd(period, 16) so 120-frame and 1800-frame analytic periods align their candidates without rejection");

        // ---- M2：搜索窗固定 2P，没有 3P 重试。
        check(ResidualMasking.SearchWindowPeriods == 2 &&
            typeof(ResidualMasking).GetField("MaximumSearchWindowPeriods") is null &&
            typeof(ResidualMasking).GetField("DefaultSearchWindowPeriods") is null,
            "the start-search window is a single fixed constant of two periods; the 3P retry and its upper bound are gone");

        // ---- 候选回退（纯函数）：第一个候选接缝失败、第二个通过 → 两条记录；三个都失败 → 拒绝且 reason 含三次指标。
        JsonArray Candidates() => new(
            new JsonObject { ["frames"] = 1920, ["seconds"] = 32.0, ["total_retime_cost_percent"] = 13.4543 },
            new JsonObject { ["frames"] = 8671, ["seconds"] = 144.52, ["total_retime_cost_percent"] = 0.5096 },
            new JsonObject { ["frames"] = 8670, ["seconds"] = 144.5, ["total_retime_cost_percent"] = 0.5506 },
            new JsonObject { ["frames"] = 9000, ["seconds"] = 150.0, ["total_retime_cost_percent"] = 0.6 });
        JsonObject Summary(double worst, int x, int y) => new() { ["global"] = 0.1, ["worst"] = worst, ["worst_x"] = x, ["worst_y"] = y, ["median"] = 0 };
        JsonObject Seam(bool pass) => new()
        {
            ["status"] = pass ? "observed_seam_pass" : "observed_seam_fail",
            ["failures"] = pass ? new JsonArray() : new JsonArray("loop_not_closed"),
            ["actual"] = new JsonObject { ["decoded_frame_count"] = 1920 },
            ["expected"] = new JsonObject { ["frames"] = 1920 },
            ["loop_closure"] = new JsonObject
            {
                ["status"] = pass ? LoopClosureCheck.ClosedStatus : LoopClosureCheck.NotClosedStatus,
                ["limit_tile_mae_255"] = LoopClosureCheck.MaximumTileMae255,
                ["rgb"] = new JsonObject { ["tile_64"] = Summary(pass ? 0 : 3.0752, 1184, 160) }
            },
            ["reference_seam"] = new JsonObject
            {
                ["rgb"] = new JsonObject { ["tile_64"] = new JsonObject {
                    ["difference_map"] = Summary(pass ? 0.41 : 3.5063, 1184, 160), ["reference_step"] = Summary(12.97, 640, 320),
                    ["encoding_noise_seam"] = Summary(0.41, 1184, 160) } }
            }
        };
        JsonArray Groups(JsonObject seam) => new(new JsonObject { ["id"] = "group-1", ["status"] = "encoded", ["encoded_loop_validation"] = seam });

        JsonArray candidates = Candidates();
        var attempts = new JsonArray();
        attempts.Add(LoopCandidateFallback.Attempt(0, candidates[0]!.AsObject(), "rejected_seam", Groups(Seam(false))));
        check(LoopCandidateFallback.CanRetry(0, candidates.Count), "a failed first candidate with more candidates behind it can fall back");
        LoopCandidateFallback.Promote(candidates, 1);
        check(candidates[0]!["frames"]!.GetValue<int>() == 8671 && candidates[1]!["frames"]!.GetValue<int>() == 1920 &&
            candidates[2]!["frames"]!.GetValue<int>() == 8670 && candidates[3]!["frames"]!.GetValue<int>() == 9000,
            "promoting the next candidate moves it to the front and keeps the rest in their original relative order");
        attempts.Add(LoopCandidateFallback.Attempt(1, candidates[0]!.AsObject(), "encoded", Groups(Seam(true))));
        JsonObject first = attempts[0]!.AsObject(), second = attempts[1]!.AsObject();
        JsonObject firstSeam = first["seams"]!.AsArray().Single()!.AsObject();
        check(attempts.Count == 2 && first["status"]?.GetValue<string>() == "rejected_seam" && first["frames"]?.GetValue<int>() == 1920 &&
            firstSeam["closure_status"]?.GetValue<string>() == LoopClosureCheck.NotClosedStatus &&
            firstSeam["closure_worst_tile_mae"]?.GetValue<double>() == 3.0752 && firstSeam["closure_worst_x"]?.GetValue<int>() == 1184 &&
            firstSeam["failures"]!.AsArray().Single()!.GetValue<string>() == "loop_not_closed" &&
            second["status"]?.GetValue<string>() == "encoded" && second["frames"]?.GetValue<int>() == 8671 &&
            second["seams"]!.AsArray().Single()!["failures"]!.AsArray().Count == 0 &&
            second["seams"]!.AsArray().Single()!["reference_step_worst"]!.GetValue<double>() == 12.97,
            "a first candidate rejected at the seam and a second one passing leave two attempt records with their closure and reference readings");
        JsonObject selected = LoopCandidateFallback.Selected(1, candidates[0]!.AsObject());
        check(selected["attempt_index"]?.GetValue<int>() == 1 && selected["frames"]?.GetValue<int>() == 8671 &&
            selected["basis"]?.GetValue<string>()?.Contains("no threshold was relaxed", StringComparison.Ordinal) == true,
            "the selected candidate record names its original position and that nothing was relaxed");

        JsonArray exhausted = Candidates();
        var all = new JsonArray();
        for (int attempt = 0; attempt < LoopCandidateFallback.MaximumAttempts; ++attempt)
        {
            if (attempt > 0) LoopCandidateFallback.Promote(exhausted, attempt);
            all.Add(LoopCandidateFallback.Attempt(attempt, exhausted[0]!.AsObject(), "rejected_seam", Groups(Seam(false))));
        }
        string summary = LoopCandidateFallback.Summary(all);
        check(!LoopCandidateFallback.CanRetry(2, exhausted.Count) && all.Count == 3 &&
            all[2]!["frames"]!.GetValue<int>() == 8670 &&
            summary.Contains("#0 1920 frames", StringComparison.Ordinal) && summary.Contains("#1 8671 frames", StringComparison.Ordinal) &&
            summary.Contains("#2 8670 frames", StringComparison.Ordinal) && summary.Contains("loop_not_closed", StringComparison.Ordinal) &&
            summary.Contains("closure worst tile 3.0752/1 at (1184, 160)", StringComparison.Ordinal) &&
            summary.Contains("reference difference 3.5063", StringComparison.Ordinal) && summary.Contains("13.454%", StringComparison.Ordinal),
            "three failed candidates exhaust the fallback and the rejection reason lists every attempt with its closure and reference readings");
        check(!LoopCandidateFallback.CanRetry(0, 1) && LoopCandidateFallback.MaximumAttempts == 3,
            "a plan with a single analytic candidate has nothing to fall back to");
    }
}
