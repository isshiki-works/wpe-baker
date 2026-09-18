using System.Text.Json;
using System.Text.Json.Nodes;
using System.Reflection;
using Baker.Core;

string repository = Path.GetFullPath(args.Length == 1 ? args[0] : Environment.CurrentDirectory);
var passed = new List<string>();
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException("FAILED: " + name);
    passed.Add(name);
}
string Decision(JsonObject comparison) => HybridCompositionValidator.Evaluate(comparison)["status"]!.GetValue<string>();

JsonObject Sample(double globalRgb = 8, double globalAlpha = 1, params double[] tileRgb)
{
    const uint width = 128, height = 65;
    const ulong frames = HybridCompositionValidator.RequiredFrames;
    double[] values = tileRgb.Length == 0 ? [25, 2, 3, 4] : tileRgb;
    var tiles = new JsonArray();
    int index = 0;
    for (uint y = 0; y < height; y += HybridCompositionValidator.RequiredTileSize)
    for (uint x = 0; x < width; x += HybridCompositionValidator.RequiredTileSize)
    {
        uint tileWidth = Math.Min(HybridCompositionValidator.RequiredTileSize, width - x);
        uint tileHeight = Math.Min(HybridCompositionValidator.RequiredTileSize, height - y);
        tiles.Add(new JsonObject
        {
            ["x"] = x, ["y"] = y, ["width"] = tileWidth, ["height"] = tileHeight,
            ["metrics"] = new JsonObject
            {
                ["pixels"] = (ulong)tileWidth * tileHeight * frames,
                ["rgb_mae_255"] = values[index++], ["alpha_mae_255"] = globalAlpha
            }
        });
    }
    return new JsonObject
    {
        ["schema_version"] = 1, ["status"] = "compared", ["report_path"] = "comparison.json",
        ["request"] = new JsonObject
        {
            ["width"] = width, ["height"] = height, ["fps_numerator"] = 120U, ["fps_denominator"] = 1U,
            ["frames"] = frames, ["warmup_frames"] = 0UL,
            ["seed"] = 17UL, ["tile_size"] = HybridCompositionValidator.RequiredTileSize
        },
        ["frames_compared"] = frames,
        ["metrics"] = new JsonObject
        {
            ["pixels"] = (ulong)width * height * frames,
            ["rgb_mae_255"] = globalRgb, ["alpha_mae_255"] = globalAlpha
        },
        ["tiles"] = tiles
    };
}

Check(Decision(Sample()) == "composition_pass", "fixed thresholds are inclusive");
Check(Decision(Sample(8.0001)) == "composition_rejected", "global RGB limit rejects");
Check(Decision(Sample(1, 1.0001)) == "composition_rejected", "global alpha limit rejects");
Check(Decision(Sample(1, 0, 25.0001, 0, 0, 0)) == "composition_rejected", "every-tile RGB limit rejects");
JsonObject missingTile = Sample(1, 0);
missingTile["tiles"]!.AsArray().RemoveAt(0);
Check(Decision(missingTile) == "composition_rejected", "incomplete tile coverage rejects");
JsonObject missingMetric = Sample(1, 0);
missingMetric["metrics"]!.AsObject().Remove("alpha_mae_255");
Check(Decision(missingMetric) == "composition_rejected", "incomplete global metrics reject");
JsonObject badCount = Sample(1, 0);
badCount["tiles"]![0]!["metrics"]!["pixels"] = 1;
Check(Decision(badCount) == "composition_rejected", "incomplete tile pixels reject");

var corpus = new[]
{
    ("old case 14 gross regression rejects", "artifacts/generic-corpus-20260908-20260907T214338Z/14-3794488644/probe-validation/comparison.json", "composition_rejected"),
    ("old case 17 gross regression rejects", "artifacts/generic-corpus-20260908-20260907T214338Z/17-3795848596/probe-validation/comparison.json", "composition_rejected"),
    ("fixed case 14 probe passes", "artifacts/gray-composite-regression-1788821561404192600/14-3794488644/comparison/comparison.json", "composition_pass"),
    ("fixed case 17 probe passes", "artifacts/gray-composite-regression-1788821561404192600/17-3795848596/comparison/comparison.json", "composition_pass")
};
foreach (var (name, relative, expected) in corpus)
{
    string path = Path.Combine(repository, relative.Replace('/', Path.DirectorySeparatorChar));
    if (!File.Exists(path)) throw new FileNotFoundException("Required retained comparison is missing.", path);
    JsonObject comparison = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
    Check(Decision(comparison) == expected, name);
}

string referenceRoot = Path.Combine(Path.GetTempPath(), "hybrid-composition-reference-" + Guid.NewGuid().ToString("N"));
string referenceSource = Path.Combine(referenceRoot, "source");
Directory.CreateDirectory(referenceSource);
await File.WriteAllTextAsync(Path.Combine(referenceSource, "project.json"),
    "{\"type\":\"scene\",\"file\":\"scene.json\",\"general\":{\"properties\":{\"tone\":{\"value\":false}}}}");
await File.WriteAllTextAsync(Path.Combine(referenceSource, "scene.json"),
    "{\"general\":{\"camerashake\":true,\"cameraparallax\":true,\"preserved\":17},\"objects\":[]}");
MethodInfo createReference = typeof(HybridBakeService).GetMethod("CreateCompositionReferenceAsync",
    BindingFlags.NonPublic | BindingFlags.Static) ?? throw new MissingMethodException("Composition reference builder is missing.");
async Task CreateReferenceAsync(ProjectSource source, string destination, string viewMode)
{
    var task = (Task?)createReference.Invoke(null,
        [source, destination, new JsonObject { ["tone"] = true }, viewMode, CancellationToken.None]);
    await (task ?? throw new InvalidOperationException("Composition reference builder did not return a task."));
}
using (var source = new ProjectSource(referenceSource))
{
    string fixedReference = Path.Combine(referenceRoot, "fixed");
    await CreateReferenceAsync(source, fixedReference, "fixed_view");
    JsonObject fixedScene = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(fixedReference, "scene.json")))!.AsObject();
    JsonObject fixedProject = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(fixedReference, "project.json")))!.AsObject();
    Check(fixedScene["general"]!["camerashake"]!.GetValue<bool>() &&
        !fixedScene["general"]!["cameraparallax"]!.GetValue<bool>() &&
        fixedScene["general"]!["preserved"]!.GetValue<int>() == 17,
        "fixed-view reference preserves camera shake and live scene state while disabling parallax");
    Check(fixedProject["general"]!["properties"]!["tone"]!["value"]!.GetValue<bool>(),
        "comparison reference applies the selected property snapshot");
    string preserveReference = Path.Combine(referenceRoot, "preserve");
    await CreateReferenceAsync(source, preserveReference, "preserve");
    JsonObject preserveScene = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(preserveReference, "scene.json")))!.AsObject();
    Check(preserveScene["general"]!["camerashake"]!.GetValue<bool>() &&
        preserveScene["general"]!["cameraparallax"]!.GetValue<bool>(),
        "preserve reference retains original camera behavior");
    JsonObject unchanged = source.ReadJson(source.SceneResource);
    Check(unchanged["general"]!["camerashake"]!.GetValue<bool>() &&
        unchanged["general"]!["cameraparallax"]!.GetValue<bool>(), "reference construction does not mutate its source");
}

Console.WriteLine(JsonSerializer.Serialize(new { status = "passed", checks = passed.Count, passed },
    new JsonSerializerOptions { WriteIndented = true }));
