using System.Text.Json.Nodes;

namespace Periodica.Bench;

/// <summary>采样器"为什么没测到"的原因：键 + 英文原文。自用工具不做本地化，键留着方便脚本按键分类。</summary>
internal static class Reason
{
    internal static readonly IReadOnlyDictionary<string, string> Texts = new Dictionary<string, string>
    {
        ["reason.power_counters_missing"] =
            "This machine publishes no Energy Meter (RAPL) power counters, so wallpaper power cannot be measured here.",
        ["reason.presentmon_no_csv"] = "PresentMon produced no CSV; the target may have had no active presentation.",
        ["reason.presentmon_no_rows"] = "PresentMon CSV has no presentation rows.",
        ["reason.presentmon_columns_missing"] = "Required PresentMon v1 columns are unavailable.",
        ["reason.presentmon_swapchain_missing"] = "Requested SwapChainAddress was not present in the CSV.",
        ["reason.presentmon_no_intervals"] = "PresentMon CSV contained no valid presentation intervals.",
        ["reason.presentmon_incomplete"] = "PresentMon CSV contained incomplete or malformed swap-chain evidence.",
    };

    /// <summary>写 target["reason"]（英文原文）与 target["reason_key"]，返回 target。</summary>
    internal static JsonObject Write(string key, JsonObject target)
    {
        target["reason"] = Texts[key];
        target["reason_key"] = key;
        return target;
    }
}
