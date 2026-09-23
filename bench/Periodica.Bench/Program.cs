using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Periodica.Bench;

// periodica-bench：Periodica 的自用测量工具（官方 WPE 播放功耗、A/B/B/A、渲染器节拍）。产品不带它。
const string Usage = """
    periodica-bench sample REQUEST.json
      Read-only sampling of one running process (PresentMon cadence, PDH GPU engines/memory, RAPL power,
      nvidia-smi board power, CPU and working set). Request fields: schema_version, process_id, label,
      output_directory, seconds, target_fps, present_mon_path, nvidia_gpu_uuid, expected_process_path,
      swap_chain_address, track_display.
    periodica-bench abba --wpe EXE --original PROJECT [--baked PROJECT] --out NEW_DIR
        [--order ABBA] [--monitor 0] [--seconds 45] [--settle 20] [--fps 60] [--idle 0] [--present-mon EXE]
      Plays each segment in the official Wallpaper Engine, samples it, restores the previous wallpaper.
      --order A measures the original alone. --idle N adds paused idle segments first and last and
      subtracts their mean iGPU power before comparing.
    periodica-bench pace --renderer EXE --source SCENE --assets DIR --out NEW_PATH --frames N
        [--fps 60] [--warmup 120] [--scale 1] [--width 3072] [--height 1920]
      Paces wpe-render through its raw stdout pipe at a wall-clock frame rate (--fps 0: unpaced).
    periodica-bench selftest
    """;

var json = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
var progress = new Progress<string>(line => Console.Error.WriteLine(line));
try
{
    if (args.Length == 0 || args[0] is "--help" or "-h" or "help") { Console.WriteLine(Usage); return 0; }
    if (args[0] == "selftest") return SelfTest.Run();
    if (args[0] == "sample" && args.Length == 2)
    {
        var request = JsonSerializer.Deserialize<OfficialPerformanceRequest>(await File.ReadAllTextAsync(args[1]), json)
            ?? throw new InvalidDataException("Invalid sampler request.");
        Console.WriteLine((await OfficialPerformanceSampler.SampleAsync(request, progress, null, cancellation.Token)).ToJsonString(json));
        return 0;
    }
    var options = Options(args);
    string Need(string name) => options.Remove(name, out string? value) ? value : throw new ArgumentException(name + " is required.");
    string? Opt(string name) => options.Remove(name, out string? value) ? value : null;
    int Int(string name, int fallback) => Opt(name) is string text ? int.Parse(text, CultureInfo.InvariantCulture) : fallback;
    double Real(string name, double fallback) => Opt(name) is string text ? double.Parse(text, CultureInfo.InvariantCulture) : fallback;
    JsonObject result;
    if (args[0] == "abba")
    {
        string? baked = Opt("--baked");
        var abba = new AbbaOptions(Need("--wpe"), Need("--original"), baked, Need("--out"), Opt("--order") ?? (baked is null ? "A" : "ABBA"),
            Int("--monitor", 0), Int("--seconds", 45), Int("--settle", 20), Real("--fps", 60), Int("--idle", 0), Opt("--present-mon"));
        Unknown(options);
        result = await Abba.RunAsync(abba, progress, cancellation.Token);
    }
    else if (args[0] == "pace")
    {
        var pace = new PaceOptions(Need("--renderer"), Need("--source"), Need("--assets"), Need("--out"), Int("--frames", 0),
            Real("--fps", 60), Int("--warmup", 120), Real("--scale", 1), Int("--width", 3072), Int("--height", 1920));
        Unknown(options);
        result = await Pacer.RunAsync(pace, progress, cancellation.Token);
    }
    else throw new ArgumentException("Unknown command. Run periodica-bench --help.");
    Console.WriteLine(result.ToJsonString(json));
    return 0;
}
catch (Exception error) when (error is ArgumentException or FormatException or IOException or InvalidDataException or JsonException)
{
    Console.Error.WriteLine(error.Message);
    return 2;
}

static Dictionary<string, string> Options(string[] args)
{
    var options = new Dictionary<string, string>(StringComparer.Ordinal);
    for (int i = 1; i < args.Length; i += 2)
        if (i + 1 >= args.Length || !args[i].StartsWith("--", StringComparison.Ordinal) || !options.TryAdd(args[i], args[i + 1]))
            throw new ArgumentException("Options require a unique name and value.");
    return options;
}

static void Unknown(Dictionary<string, string> left)
{
    if (left.Count > 0) throw new ArgumentException("Unknown option: " + string.Join(", ", left.Keys));
}
