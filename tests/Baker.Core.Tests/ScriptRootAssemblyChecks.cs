using System.Reflection;
using System.Text.Json.Nodes;
using Baker.Core;

/// <summary>
/// 装配候选工程时保留原作里"不绘制但带脚本"的根对象（脚本可能经 shared 给保留的实时脚本提供函数），
/// 无脚本的 inactive 对象照旧丢弃；合成比对里候选脚本报错多于原作时直接拒绝，相等时放行。
/// </summary>
internal static class ScriptRootAssemblyChecks
{
    private static readonly string[] DrawKeys = ["image", "model", "text", "particle", "sound", "effects", "puppet"];

    internal static void Run(Action<bool, string> check)
    {
        var assemble = typeof(SceneAssembler).GetMethod("AssembleObjects", BindingFlags.Static | BindingFlags.NonPublic)!;
        JsonArray Assemble(JsonArray objects, JsonObject plan, Dictionary<string, JsonObject> replacements) =>
            (JsonArray)assemble.Invoke(null, [objects.OfType<JsonObject>().ToDictionary(obj => obj["id"]!.GetValue<int>()),
                plan, replacements, new JsonArray()])!;
        JsonObject Layer(int id, string allocation, bool drawable, int? parent = null) => new()
        {
            ["id"] = id, ["root"] = parent ?? id, ["allocation_root"] = id, ["parent"] = parent,
            ["allocation"] = allocation, ["drawable"] = drawable, ["live"] = allocation == "live"
        };
        int[] Ids(JsonArray objects) => objects.OfType<JsonObject>().Select(obj => obj["id"]!.GetValue<int>()).ToArray();

        // ---- 源顺序：视频层、脚本库在它后面、实时层、无脚本 inactive、带脚本但绘制的 inactive、对象级 script ----
        var objects = new JsonArray(
            new JsonObject { ["id"] = 11, ["image"] = "materials/background.json" },
            new JsonObject
            {
                ["id"] = 10, ["name"] = "script library",
                ["visible"] = new JsonObject { ["script"] = "shared.startAt = function(layer) { layer.play(); };\nexport function update(value) { return value; }", ["value"] = true },
                ["effects"] = new JsonArray(new JsonObject { ["file"] = "effects/tint/effect.json" }),
                ["sound"] = new JsonArray("sounds/unused.mp3")
            },
            new JsonObject
            {
                ["id"] = 12, ["text"] = new JsonObject { ["value"] = "live" },
                ["visible"] = new JsonObject { ["script"] = "export function init(value) { shared.startAt(thisLayer); return value; }", ["value"] = true }
            },
            new JsonObject { ["id"] = 13, ["name"] = "plain inactive", ["visible"] = true },
            new JsonObject
            {
                ["id"] = 14, ["image"] = "materials/hidden.json", ["visible"] = false,
                ["origin"] = new JsonObject { ["script"] = "export function update(value) { return value; }", ["value"] = "0 0 0" }
            },
            new JsonObject { ["id"] = 15, ["script"] = "shared.counter = 0;" });
        var plan = new JsonObject
        {
            ["layers"] = new JsonArray(Layer(11, "video", true), Layer(10, "inactive", false), Layer(12, "live", true),
                Layer(13, "inactive", false), Layer(14, "inactive", true), Layer(15, "inactive", false)),
            ["live_layer_ids"] = new JsonArray(12),
            ["omitted_snapshot_layer_ids"] = new JsonArray(),
            ["video_groups"] = new JsonArray(new JsonObject { ["id"] = "group-1", ["layer_ids"] = new JsonArray(11) }),
            ["composition"] = new JsonArray(new JsonObject { ["video_group"] = "group-1" }, new JsonObject { ["live_root"] = 12 })
        };
        var replacements = new Dictionary<string, JsonObject> { ["group-1"] = new JsonObject { ["id"] = 100, ["image"] = "wpe_baker_video/group-1.json" } };
        JsonArray assembled = Assemble(objects, plan, replacements);
        check(Ids(assembled).SequenceEqual(new[] { 10, 15, 100, 12 }),
            "inactive non-drawing script roots are retained first in source order ahead of video and live layers");
        var library = assembled.OfType<JsonObject>().Single(obj => obj["id"]!.GetValue<int>() == 10);
        check(DrawKeys.All(key => !library.ContainsKey(key)) &&
            library["visible"]?["script"]?.GetValue<string>()?.Contains("shared.startAt", StringComparison.Ordinal) == true,
            "a retained script root keeps its script bindings and loses every draw key");
        check(!Ids(assembled).Contains(13), "an inactive object without scripts is still dropped");
        check(!Ids(assembled).Contains(14), "a drawable inactive object is not retained by the script-root rule");
        check(objects.OfType<JsonObject>().Single(obj => obj["id"]!.GetValue<int>() == 10).ContainsKey("effects"),
            "script-root retention does not mutate the source objects");

        // ---- 脚本根下挂着实时层时仍走父级保留路径、留在原位置，不被提到最前打乱视频/实时顺序 ----
        var parentedObjects = new JsonArray(
            new JsonObject { ["id"] = 30, ["image"] = "materials/background.json" },
            new JsonObject { ["id"] = 31, ["visible"] = new JsonObject { ["script"] = "shared.value = 1;", ["value"] = true } },
            new JsonObject { ["id"] = 32, ["parent"] = 31, ["text"] = new JsonObject { ["value"] = "live" } });
        var parentedPlan = new JsonObject
        {
            ["layers"] = new JsonArray(Layer(30, "video", true), Layer(31, "inactive", false), Layer(32, "live", true, 31)),
            ["live_layer_ids"] = new JsonArray(32),
            ["omitted_snapshot_layer_ids"] = new JsonArray(),
            ["video_groups"] = new JsonArray(new JsonObject { ["id"] = "group-1", ["layer_ids"] = new JsonArray(30) }),
            ["composition"] = new JsonArray(new JsonObject { ["video_group"] = "group-1" }, new JsonObject { ["live_root"] = 32 })
        };
        JsonArray parented = Assemble(parentedObjects, parentedPlan,
            new Dictionary<string, JsonObject> { ["group-1"] = new JsonObject { ["id"] = 100, ["image"] = "wpe_baker_video/group-1.json" } });
        check(Ids(parented).SequenceEqual(new[] { 100, 31, 32 }),
            "a script root that already parents a live layer keeps its source position behind the video");

        // ---- 脚本报错门 ----
        JsonObject Error(int owner, string name, string message) => new()
        {
            ["binding_id"] = 0, ["owner_layer_id"] = owner, ["owner_name"] = name, ["property"] = "visible",
            ["phase"] = "init", ["script_sha"] = "0", ["message"] = message, ["stack"] = "    at init (scripts/0-0.js:1:1)\n"
        };
        PairedComparison Comparison(JsonArray? sourceErrors, JsonArray? candidateErrors)
        {
            JsonObject Native(JsonArray? errors) => errors is null ? new JsonObject { ["status"] = "rendered" }
                : new JsonObject { ["status"] = "rendered", ["source_script_error_count"] = errors.Count, ["source_script_errors"] = errors };
            // 像素指标都在限值内（RGB 平均误差 1），只看脚本报错门。
            var errors = new PixelErrors { Pixels = 1, RgbSum = 3 };
            return PairedComparisonTests.Comparison(errors, [errors],
                new JsonObject { ["source_native_result"] = Native(sourceErrors), ["candidate_native_result"] = Native(candidateErrors) });
        }
        JsonObject rejected = CompositionGate.Evaluate(Comparison(new JsonArray(),
            new JsonArray(Error(2242, "N", "TypeError: not a function"), Error(2242, "N", "TypeError: not a function"), Error(224, "十字架", "TypeError: not a function"))));
        check(rejected["status"]!.GetValue<string>() == CandidateScriptErrorGate.RejectedCompositionStatus &&
            rejected["script_error_validation"]!["status"]!.GetValue<string>() == CandidateScriptErrorGate.RejectedValidationStatus &&
            rejected["metrics_judged"]!.GetValue<bool>() == false,
            "more candidate script errors than the original reject the composition even when pixel metrics would pass");
        var shared = Error(99, "nv", "ReferenceError: x is not defined");
        JsonObject equal = CompositionGate.Evaluate(Comparison(new JsonArray(shared.DeepClone()), new JsonArray(shared.DeepClone())));
        check(equal["status"]!.GetValue<string>() == "composition_pass" &&
            equal["script_error_validation"]!["status"]!.GetValue<string>() == "script_errors_not_increased",
            "equal script error counts pass the script error gate");
        JsonObject listedOnlyAdded = CandidateScriptErrorGate.Evaluate(Comparison(new JsonArray(shared.DeepClone()),
            new JsonArray(shared.DeepClone(), Error(2242, "N", "TypeError: not a function"))).Report);
        check(listedOnlyAdded["added_script_error_count"]!.GetValue<int>() == 1 &&
            listedOnlyAdded["listed_script_errors"]!.AsArray().Single()!["owner_layer_id"]!.GetValue<int>() == 2242,
            "the script error reason lists the errors the candidate added, not ones the original already had");
        JsonObject unavailable = CompositionGate.Evaluate(Comparison(null, new JsonArray(shared.DeepClone())));
        check(unavailable["status"]!.GetValue<string>() == "composition_pass" &&
            unavailable["script_error_validation"]!["status"]!.GetValue<string>() == "not_available",
            "a missing script error count is recorded as unavailable and left to the remaining composition checks");
    }
}
