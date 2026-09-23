#include <gtest/gtest.h>

#include <cmath>
#include <cstdint>
#include <string>
#include <vector>

#include <nlohmann/json.hpp>

#include "../../../src/Scene/VulkanRender/PassTiming.hpp"

// 逐 pass 计时的 jsonl 行（格式见 PassTiming.hpp）：区间归属、取整口径与字符串转义。

namespace
{

using owe::vulkan::pass_timing::FrameLine;
using owe::vulkan::pass_timing::Pass;

Pass Named(std::string name) {
    Pass pass;
    pass.name = std::move(name);
    return pass;
}

} // namespace

TEST(PassTimingLine, AttributesIntervalsToTheRecordedPasses) {
    std::vector<Pass> passes { Named("a"), Named("b"), Named("c"), Named("never-recorded") };
    passes[2] = Pass { .name = "c", .layer = 32, .role = "effect", .effect = "shine", .effect_index = 1,
                       .rt = "_rt_x", .width = 640, .height = 360 };
    // 上传，单独录制的 a，然后合并 scope 里 b、c 两次 draw 与 scope 结束（记在 c 名下）；第 4 个 pass 没录制。
    const std::vector<std::int64_t>  labels { -1, 0, 1, 2, 2 };
    const std::vector<std::uint64_t> ticks { 10, 20, 30, 40, 50 };
    const auto line = nlohmann::json::parse(FrameLine(7, passes, labels, ticks, 2.0));

    EXPECT_EQ(line["schema"], "pass-timing/1");
    EXPECT_EQ(line["frame"], 7);
    EXPECT_EQ(line["gpu_ns"], 300);
    EXPECT_EQ(line["uploads_ns"], 20);
    const auto& out = line["passes"];
    ASSERT_EQ(out.size(), 3u);
    EXPECT_EQ(out[0]["pass"], "a");
    EXPECT_EQ(out[0]["gpu_ns"], 40);
    EXPECT_EQ(out[1]["pass"], "b");
    EXPECT_EQ(out[1]["gpu_ns"], 60);
    EXPECT_EQ(out[2]["pass"], "c");
    EXPECT_EQ(out[2]["gpu_ns"], 180);
    EXPECT_EQ(out[2]["layer"], 32);
    EXPECT_EQ(out[2]["role"], "effect");
    EXPECT_EQ(out[2]["effect"], "shine");
    EXPECT_EQ(out[2]["effect_index"], 1);
    EXPECT_EQ(out[2]["rt"], "_rt_x");
    EXPECT_EQ(out[2]["w"], 640);
    EXPECT_EQ(out[2]["h"], 360);
    EXPECT_EQ(out[0]["layer"], -1);
    EXPECT_EQ(out[0]["effect_index"], -1);
}

TEST(PassTimingLine, PassTimesAddUpToTheFrameTimeWithFractionalPeriods) {
    // 52.083 ns 一刻（Arc B390）：逐区间各自取整会让合计偏离帧时间，按累计时刻取整不会。
    std::vector<Pass> passes;
    std::vector<std::int64_t>  labels { -1 };
    std::vector<std::uint64_t> ticks { 3 };
    for (int i = 0; i < 40; ++i) {
        passes.push_back(Named("p" + std::to_string(i)));
        labels.push_back(i);
        ticks.push_back(static_cast<std::uint64_t>(1 + i % 3));
    }
    std::uint64_t total_ticks = 0;
    for (auto t : ticks) total_ticks += t;
    const auto line = nlohmann::json::parse(FrameLine(0, passes, labels, ticks, 52.083));

    const auto gpu_ns = line["gpu_ns"].get<std::uint64_t>();
    EXPECT_EQ(gpu_ns, static_cast<std::uint64_t>(std::llround(static_cast<double>(total_ticks) * 52.083)));
    std::uint64_t sum = line["uploads_ns"].get<std::uint64_t>();
    for (const auto& pass : line["passes"]) sum += pass["gpu_ns"].get<std::uint64_t>();
    EXPECT_EQ(sum, gpu_ns);
}

TEST(PassTimingLine, EscapesNamesIntoValidJson) {
    const std::string name = "workshop/1/effects/\"q\"\\back\nline\x01";
    const std::string effect = "水波(全局强度)";
    std::vector<Pass> passes { Pass { .name = name, .role = "effect", .effect = effect, .effect_index = 0 } };
    const auto line = nlohmann::json::parse(FrameLine(0, passes, std::vector<std::int64_t> { 0 },
                                                      std::vector<std::uint64_t> { 1 }, 1.0));
    EXPECT_EQ(line["passes"][0]["pass"], name);
    EXPECT_EQ(line["passes"][0]["effect"], effect);
}
