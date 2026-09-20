using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>烘焙各阶段的墙钟计时。只记录真的跑过的阶段；没跑过的阶段写 null，不写 0。</summary>
public sealed class StageTiming(IProgress<RenderProgress>? progress = null)
{
    public const string SourceCapture = "source_capture";
    public const string CompositionValidation = "composition_validation";
    public const string LoopStartSearch = "loop_start_search";
    public const string MasterRender = "master_render";
    public const string SeamCheck = "seam_check";
    public const string Crossfade = "crossfade";
    /// <summary>等成品编码槽位（--encode-slots）的时间；单独一项，不混进 encode_playback。</summary>
    public const string EncodeSlotWait = "encode_slot_wait";
    public const string EncodePlayback = "encode_playback";
    public const string HardwareDecodeCheck = "hardware_decode_check";
    public const string ProjectAssembly = "project_assembly";
    public const string Other = "other";

    /// <summary>互斥阶段：一个接一个发生，各项之和加上 other 等于 total_seconds。</summary>
    public static readonly string[] ExclusiveStages =
    [
        SourceCapture, CompositionValidation, LoopStartSearch, MasterRender, SeamCheck, Crossfade,
        EncodeSlotWait, EncodePlayback, HardwareDecodeCheck, ProjectAssembly
    ];

    /// <summary>master_render 内部与渲染重叠的分量，不参与求和。</summary>
    public const string Readback = "readback";
    public const string EncodeMaster = "encode_master";

    private readonly Dictionary<string, double> seconds = new(StringComparer.Ordinal);
    private readonly Stopwatch total = Stopwatch.StartNew();
    private readonly object gate = new();
    private double? readback, encodeMaster, rendererWall;
    private string? deviceUuid, deviceName;

    /// <summary>记录这次烘焙实际使用的 GPU；名称解析失败时只保留 UUID。</summary>
    public void SetDevice(string? uuid)
    {
        lock (gate)
        {
            if (uuid is null || string.Equals(uuid, deviceUuid, StringComparison.OrdinalIgnoreCase)) return;
            deviceUuid = uuid;
            try
            {
                deviceName = VulkanDevices.Enumerate()
                    .FirstOrDefault(device => string.Equals(device.DeviceUuid, uuid, StringComparison.OrdinalIgnoreCase))?.Name;
            }
            catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or
                InvalidOperationException or PlatformNotSupportedException or BadImageFormatException)
            {
                deviceName = null;
            }
        }
    }

    public void Add(string stage, double elapsedSeconds)
    {
        if (!double.IsFinite(elapsedSeconds) || elapsedSeconds < 0) return;
        lock (gate) seconds[stage] = seconds.GetValueOrDefault(stage) + elapsedSeconds;
    }

    /// <summary>用 using 包住一段代码，把它的墙钟累加到这个阶段。</summary>
    public Scope Measure(string stage)
    {
        progress?.Report(new(stage, null, StageLabel(stage, Messages.DefaultLanguage() == Messages.English)));
        return new(this, stage);
    }

    public readonly struct Scope : IDisposable
    {
        private readonly StageTiming owner;
        private readonly string stage;
        private readonly long started;
        internal Scope(StageTiming owner, string stage)
        {
            this.owner = owner; this.stage = stage; started = Stopwatch.GetTimestamp();
        }
        public void Dispose() => owner.Add(stage, Stopwatch.GetElapsedTime(started).TotalSeconds);
    }

    /// <summary>从一次 master 渲染的 manifest 里取回读与编码背压分量，并累加渲染器自报的墙钟。</summary>
    public void AddMasterBreakdown(JsonObject? manifest)
    {
        if (manifest is null) return;
        lock (gate)
        {
            Accumulate(ref readback, manifest["stream_timing"]?["readback_seconds"]);
            Accumulate(ref encodeMaster, manifest["stream_timing"]?["encode_seconds"]);
            Accumulate(ref rendererWall, manifest["native_result"]?["wall_seconds"]);
        }
    }

    private static void Accumulate(ref double? target, JsonNode? node)
    {
        if (node is not JsonValue value || !value.TryGetValue(out double number) || !double.IsFinite(number) || number < 0) return;
        target = (target ?? 0) + number;
    }

    public double TotalSeconds => total.Elapsed.TotalSeconds;

    private static JsonNode? Round(double? value) =>
        value is null || !double.IsFinite(value.Value) ? null : JsonValue.Create(Math.Round(value.Value, 3));

    /// <summary>把最新的分阶段计时写进报告顶层，帧数取报告自己记录的帧数。</summary>
    public void Stamp(JsonObject report) =>
        report["stage_timing"] = ToJson(report["frames"] is JsonValue frames &&
            ulong.TryParse(frames.ToJsonString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong count) ? count : 0);

    /// <summary>生成 bake.json 顶层的 stage_timing。</summary>
    public JsonObject ToJson(ulong frames)
    {
        lock (gate)
        {
            double totalSeconds = total.Elapsed.TotalSeconds;
            var stages = new JsonObject();
            double sum = 0;
            foreach (string stage in ExclusiveStages)
            {
                if (seconds.TryGetValue(stage, out double value)) { stages[stage] = Round(value); sum += value; }
                else stages[stage] = null;
            }
            stages[Other] = Round(Math.Max(0, totalSeconds - sum));
            double? render = seconds.TryGetValue(MasterRender, out double rendered) && rendered > 0 ? rendered : null;
            return new JsonObject
            {
                ["schema_version"] = 1,
                ["device"] = deviceName is null ? null : JsonValue.Create(deviceName),
                ["device_uuid"] = deviceUuid is null ? null : JsonValue.Create(deviceUuid),
                ["frames"] = frames,
                ["total_seconds"] = Round(totalSeconds),
                ["frames_per_second_render"] = render is null || frames == 0 ? null : Round(frames / render.Value),
                ["stages"] = stages,
                ["master_render_breakdown"] = new JsonObject
                {
                    [Readback] = Round(readback),
                    [EncodeMaster] = Round(encodeMaster),
                    ["renderer_wall_seconds"] = Round(rendererWall)
                },
                ["basis"] = "每一项都是本进程测得的墙钟秒。stages 的各项互斥，它们与 other 相加等于 total_seconds；" +
                    "没有发生过的阶段是 null，不是 0。master_render_breakdown 是 master_render 内部与渲染重叠的分量" +
                    "（readback 是等渲染器交出帧的时间，encode_master 是等待编码管道接收帧的背压时间，" +
                    "renderer_wall_seconds 是渲染器自己在 result.json 里报的墙钟），它们不参与求和。"
            };
        }
    }

    private static readonly (string Key, string Chinese, string English)[] Labels =
    [
        (SourceCapture, "捕获源准备", "capture source"), (CompositionValidation, "画面对照", "composition"),
        (LoopStartSearch, "起点搜索", "start search"), (MasterRender, "主渲染", "render"),
        (SeamCheck, "接缝校验", "seam check"), (Crossfade, "接缝淡化", "crossfade"),
        (EncodeSlotWait, "等编码槽位", "encode slot wait"), (EncodePlayback, "成品编码", "playback encode"),
        (HardwareDecodeCheck, "硬件解码检查", "hardware decode"), (ProjectAssembly, "项目组装", "assembly"),
        (Other, "其他", "other")
    ];

    public static string StageLabel(string stage, bool english) =>
        Labels.FirstOrDefault(label => label.Key == stage) is var label && label.Key is not null
            ? english ? label.English : label.Chinese : stage;

    private static double? Number(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out double number) && double.IsFinite(number) ? number : null;

    /// <summary>给 CLI 和 GUI 用的一行汇总；报告里没有 stage_timing 时返回 null。</summary>
    public static string? Summary(JsonNode? report, bool english = false)
    {
        if (report?["stage_timing"] is not JsonObject timing || timing["stages"] is not JsonObject stages) return null;
        double? totalSeconds = Number(timing["total_seconds"]);
        if (totalSeconds is null) return null;
        string Fixed(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);
        var parts = new List<string>();
        foreach (var (key, chinese, englishLabel) in Labels)
            if (Number(stages[key]) is double value && value > 0)
                parts.Add($"{(english ? englishLabel : chinese)} {Fixed(value)}");
        double? playbackEncode = Number(stages[EncodePlayback]);
        double? masterEncode = Number(timing["master_render_breakdown"]?[EncodeMaster]);
        string share = totalSeconds > 0 ? Fixed((playbackEncode ?? 0) / totalSeconds.Value * 100) : "0.0";
        string encoding = playbackEncode is null
            ? english ? "encoding not timed separately" : "编码未单独计时"
            : english ? $"playback encoding {Fixed(playbackEncode.Value)}s ({share}%)"
                : $"成品编码 {Fixed(playbackEncode.Value)} 秒（占 {share}%）";
        if (masterEncode is double writeWait)
            encoding += english ? $"; pipe write wait {Fixed(writeWait)}s (overlaps rendering)"
                : $"；管道写入等待 {Fixed(writeWait)} 秒（与渲染重叠）";
        string fps = Number(timing["frames_per_second_render"]) is double rate ? Fixed(rate) : "-";
        string device = timing["device"]?.GetValue<string>() ?? timing["device_uuid"]?.GetValue<string>() ?? "unknown";
        ulong frames = timing["frames"] is JsonValue count && count.TryGetValue(out ulong value2) ? value2 : 0;
        return english
            ? $"Stage timing (s): total {Fixed(totalSeconds.Value)} · {string.Join(" · ", parts)} · " +
              $"{frames} frames · {fps} rendered fps · {encoding} · device {device}"
            : $"分阶段耗时（秒）：总计 {Fixed(totalSeconds.Value)} · {string.Join(" · ", parts)} · " +
              $"共 {frames} 帧 · 渲染 {fps} 帧/秒 · {encoding} · 设备 {device}";
    }
}
