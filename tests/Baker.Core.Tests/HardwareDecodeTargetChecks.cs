using System.Reflection;
using System.Text.Json.Nodes;
using Baker.App;
using Baker.Core;

/// <summary>
/// 硬解实测的适用范围：烘焙机上验证过的适配器清单、核显/独显归类、厂商无关的核显尺寸提示与中英结论。
/// 这里只查记录与文案，不碰任何判据：判定字段（passed / all_adapters_passed）必须原样不动。
/// </summary>
internal static class HardwareDecodeTargetChecks
{
    private const ulong Gibibyte = 1UL << 30;
    private const ulong Mebibyte = 1UL << 20;

    private static readonly MethodInfo Apply = typeof(NativeRenderRunner)
        .GetMethod("ApplyTargetGuidance", BindingFlags.Static | BindingFlags.NonPublic)!;

    private static JsonObject Adapter(string name, uint vendorId, ulong dedicated, bool passed) => new()
    {
        ["adapter_index"] = 0u,
        ["name"] = name,
        ["vendor_id"] = vendorId,
        ["vendor"] = HardwareDecodeDimensions.VendorName(vendorId),
        ["dedicated_video_memory_bytes"] = dedicated,
        ["shared_system_memory_bytes"] = 8 * Gibibyte,
        ["adapter_class"] = HardwareDecodeDimensions.ClassifyAdapter(vendorId, dedicated, 8 * Gibibyte),
        ["status"] = passed ? "passed" : "failed",
        ["passed"] = passed,
    };

    private static JsonObject Probe(string? codec, int width, int height, params JsonObject[] adapters)
    {
        var report = new JsonObject
        {
            ["status"] = "completed",
            ["all_adapters_passed"] = adapters.Length > 0 && adapters.All(adapter => adapter["passed"]!.GetValue<bool>()),
            ["adapters"] = new JsonArray(adapters.Select(adapter => (JsonNode?)adapter).ToArray()),
            ["video_stream"] = codec is null ? null : new JsonObject
            {
                ["codec_name"] = codec, ["width"] = width, ["height"] = height,
                ["coded_width"] = width, ["coded_height"] = height,
            },
        };
        Apply.Invoke(null, [report]);
        return report;
    }

    private static string[] Keys(JsonObject report) =>
        [.. report["conclusion"]!["parts"]!.AsArray().Select(part => part!["key"]!.GetValue<string>())];

    internal static void Run(Action<bool, string> check)
    {
        // ---- 核显与独显的归类 ----
        check(HardwareDecodeDimensions.ClassifyAdapter(0x8086, 128 * Mebibyte, 8 * Gibibyte) == HardwareDecodeDimensions.IntegratedAdapter &&
            HardwareDecodeDimensions.ClassifyAdapter(0x1002, 512 * Mebibyte, 8 * Gibibyte) == HardwareDecodeDimensions.IntegratedAdapter &&
            HardwareDecodeDimensions.ClassifyAdapter(0x1002, 16 * Gibibyte, 8 * Gibibyte) == HardwareDecodeDimensions.DiscreteAdapter &&
            HardwareDecodeDimensions.ClassifyAdapter(0x8086, Gibibyte, 8 * Gibibyte) == HardwareDecodeDimensions.DiscreteAdapter &&
            HardwareDecodeDimensions.ClassifyAdapter(0x1414, 0, 0) == HardwareDecodeDimensions.UnknownAdapterClass,
            "adapters are split into integrated and discrete by dedicated video memory with a 1 GiB boundary");
        check(HardwareDecodeDimensions.ClassifyAdapter(0x10de, 0, 8 * Gibibyte) == HardwareDecodeDimensions.DiscreteAdapter &&
            HardwareDecodeDimensions.ClassifyAdapter(0x10de, 128 * Mebibyte, 8 * Gibibyte) == HardwareDecodeDimensions.DiscreteAdapter,
            "an NVIDIA adapter is never reported as integrated, however little dedicated memory it declares");
        check(HardwareDecodeDimensions.VendorName(0x10de) == "NVIDIA" && HardwareDecodeDimensions.VendorName(0x8086) == "Intel" &&
            HardwareDecodeDimensions.VendorName(0x1002) == "AMD" && HardwareDecodeDimensions.VendorName(0x1234) == "0x1234",
            "vendor ids map to readable names and unknown ids stay hexadecimal");

        // ---- 常见核显上限：数字与出处都在 HardwareDecodeDimensions 里 ----
        var h264 = HardwareDecodeDimensions.IntegratedH264;
        var hevc = HardwareDecodeDimensions.IntegratedHevc;
        check(h264.MaximumWidth == 4096 && h264.MaximumHeight == 4096 && h264.MaximumLumaSamples == 4096UL * 4096 &&
            hevc.MaximumWidth == 8192 && hevc.MaximumHeight == 4320 && hevc.MaximumLumaSamples == 7680UL * 4320 &&
            h264.Sources.Length > 0 && hevc.Sources.Length > 0 &&
            h264.Sources.Concat(hevc.Sources).All(source => source.Length > 20) &&
            h264.BasisZh.Length > 0 && h264.BasisEn.Length > 0 && hevc.BasisZh.Length > 0 && hevc.BasisEn.Length > 0,
            "the common integrated-GPU ceilings carry their numbers and a source for each codec");
        check(HardwareDecodeDimensions.CeilingFor("h264") == h264 && HardwareDecodeDimensions.CeilingFor("avc1") == h264 &&
            HardwareDecodeDimensions.CeilingFor("hevc") == hevc && HardwareDecodeDimensions.CeilingFor("h265") == hevc &&
            HardwareDecodeDimensions.CeilingFor("vp9") is null && HardwareDecodeDimensions.CeilingFor(null) is null,
            "ffprobe codec names map to a ceiling only for H.264 and HEVC");

        check(HardwareDecodeDimensions.HintFor("h264", 3840, 2160) is null &&
            HardwareDecodeDimensions.HintFor("h264", 4096, 4096) is null &&
            HardwareDecodeDimensions.HintFor("hevc", 8192, 3160) is null &&
            HardwareDecodeDimensions.HintFor("vp9", 8192, 8192) is null &&
            HardwareDecodeDimensions.HintFor("h264", 0, 0) is null,
            "sizes within the common integrated ceilings raise no hint, and neither does an unknown codec");
        var wide = HardwareDecodeDimensions.HintFor("h264", 8192, 2160);
        check(wide is not null && wide.Exceeded.Any(violation => violation.Measure == "width" && violation.Limit == 4096) &&
            wide.Exceeded.Any(violation => violation.Measure == "luma_samples"),
            "an H.264 bitstream wider than 4096 is flagged for integrated targets even though a discrete GPU may decode it");
        var tall = HardwareDecodeDimensions.HintFor("hevc", 8192, 4352);
        check(tall is not null && tall.Exceeded.Any(violation => violation.Measure == "height" && violation.Limit == 4320) &&
            tall.Exceeded.Any(violation => violation.Measure == "luma_samples"),
            "an HEVC bitstream taller than the 8K UHD ceiling is flagged for integrated targets");
        JsonObject hintJson = wide!.ToJson();
        check(hintJson["kind"]!.GetValue<string>() == HardwareDecodeDimensions.IntegratedCeilingHintKind &&
            hintJson["codec"]!.GetValue<string>() == "h264" &&
            hintJson["exceeded"]!.AsArray().Count == wide.Exceeded.Count &&
            hintJson["common_integrated_ceiling"]!["sources"]!.AsArray().Count == h264.Sources.Length,
            "the hint serialises its ceiling, its sources and an advisory-only scope");

        // ---- 结论：独显验证过、核显没验证过 ----
        JsonObject discreteOnly = Probe("h264", 3840, 2160, Adapter("NVIDIA GeForce RTX 5090 D v2", 0x10de, 32 * Gibibyte, passed: true));
        check(discreteOnly["target_caveat"]!.GetValue<string>() == HardwareDecodeDimensions.BakingMachineOnlyCaveat &&
            discreteOnly["verified_on"]!.AsArray().Count == 1 &&
            discreteOnly["verified_on"]![0]!["adapter_class"]!.GetValue<string>() == HardwareDecodeDimensions.DiscreteAdapter &&
            discreteOnly["verified_on"]![0]!["integrated"]!.GetValue<bool>() == false &&
            discreteOnly["verified_on"]![0]!["vendor"]!.GetValue<string>() == "NVIDIA" &&
            discreteOnly["verified_on"]![0]!["passed"]!.GetValue<bool>(),
            "the report lists which adapters of the baking machine took part, with vendor and integrated/discrete");
        check(Keys(discreteOnly).SequenceEqual(["hardware_decode.verified_on_baking_machine", "hardware_decode.no_integrated_verified"]),
            "a baking machine with no integrated GPU gets both the machine caveat and the stronger no-integrated warning");
        check(discreteOnly["all_adapters_passed"]!.GetValue<bool>() && discreteOnly["adapters"]![0]!["passed"]!.GetValue<bool>() &&
            discreteOnly["adapters"]![0]!["status"]!.GetValue<string>() == "passed" &&
            discreteOnly["target_hints"]!.AsArray().Count == 0,
            "writing the caveat changes no verdict field and raises no hint for an ordinary 4K bitstream");

        // ---- 结论：核显也参与了验证 ----
        JsonObject withIntegrated = Probe("h264", 3840, 2160,
            Adapter("NVIDIA GeForce RTX 5090 D v2", 0x10de, 32 * Gibibyte, passed: true),
            Adapter("AMD Radeon(TM) Graphics", 0x1002, 512 * Mebibyte, passed: true));
        check(Keys(withIntegrated).SequenceEqual(["hardware_decode.verified_on_baking_machine"]) &&
            withIntegrated["verified_on"]![1]!["integrated"]!.GetValue<bool>(),
            "when an integrated GPU took part the stronger warning is dropped and both adapters are named");

        // ---- 结论：尺寸越过常见核显上限 ----
        JsonObject oversized = Probe("h264", 8192, 2160,
            Adapter("AMD Radeon(TM) Graphics", 0x1002, 512 * Mebibyte, passed: true));
        check(oversized["target_hints"]!.AsArray().Count == 1 &&
            oversized["target_hints"]![0]!["kind"]!.GetValue<string>() == HardwareDecodeDimensions.IntegratedCeilingHintKind &&
            Keys(oversized).Contains("hardware_decode.beyond_integrated_ceiling"),
            "a bitstream beyond the common integrated ceiling adds a vendor-agnostic hint to the conclusion");
        check(oversized["all_adapters_passed"]!.GetValue<bool>() && oversized["status"]!.GetValue<string>() == "completed",
            "the ceiling hint is advisory: the probe's own verdict and status are untouched");

        // ---- 结论：没有适配器、或一张都没通过 ----
        JsonObject none = Probe("h264", 3840, 2160);
        check(Keys(none).SequenceEqual(["hardware_decode.no_adapters_on_baking_machine"]) &&
            none["verified_on"]!.AsArray().Count == 0,
            "a baking machine with no usable adapter says so instead of claiming a verified GPU");
        JsonObject failed = Probe("h264", 3840, 2160, Adapter("Intel(R) UHD Graphics", 0x8086, 128 * Mebibyte, passed: false));
        check(Keys(failed).SequenceEqual(["hardware_decode.none_passed_on_baking_machine"]) &&
            !failed["all_adapters_passed"]!.GetValue<bool>(),
            "when nothing passed the conclusion says so and still names the adapters that were tried");

        // ---- 文案表与本地化对象 ----
        string[] keys = ["hardware_decode.verified_on_baking_machine", "hardware_decode.none_passed_on_baking_machine",
            "hardware_decode.no_adapters_on_baking_machine", "hardware_decode.no_integrated_verified",
            "hardware_decode.beyond_integrated_ceiling"];
        check(keys.All(key => MessageCatalog.Find(key) is not null),
            "every hardware decode caveat has a zh and en wording in the shared message table");
        JsonObject localized = MessageCatalog.Localized("hardware_decode.no_integrated_verified", ["\"甲\""], ["\"A\""]);
        check(localized["key"]!.GetValue<string>() == "hardware_decode.no_integrated_verified" &&
            localized["params"]!.AsArray().Count == 1 &&
            MessageCatalog.Localized("hardware_decode.does_not_exist", [], [])["key"] is not null,
            "Localized renders both languages with their own arguments and does not throw on an unknown key");
    }
}
