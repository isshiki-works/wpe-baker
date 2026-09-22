using System.Text.Json;
using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>
/// plan.json 的 settings 字段就是分析请求 <see cref="HybridAnalyzeRequest"/>（v3 形态，蛇形命名）。
/// 写出与读回都走这里，字段默认值只在请求记录上定义一处，其余代码不再各自按键取值、各自兜底。
/// </summary>
public static class PlanSettings
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    /// <summary>写进 plan 的形态：追踪文件与两份来源记录只属于这一次运行，不写进 settings。</summary>
    public static JsonNode ToJson(HybridAnalyzeRequest request) =>
        JsonSerializer.SerializeToNode(request with { RuntimeTraceFile = null, PropertiesOrigin = null, FrameRateOrigin = null }, Json)!;

    /// <summary>从 plan 读回设置；缺失即报错，不补默认值。</summary>
    public static HybridAnalyzeRequest Of(JsonObject plan) => plan["settings"] is JsonObject settings
        ? settings.Deserialize<HybridAnalyzeRequest>(Json)!
        : throw new InvalidDataException("Plan settings are missing.");
}
