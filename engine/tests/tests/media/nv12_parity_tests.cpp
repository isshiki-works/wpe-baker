// T5b 双实现对拍：wavsen::video::YuvToRgba 软件路径与 owe::media::Nv12ToRgba
// 吃同一份 NV12，在同一设备同一队列上各写一张 RGBA8，回读逐字节比较。
// **T5b 删 wavsen 时连同 CMake 里的 media-nv12-parity-tests 目标一起删。**
//
// 只比偶数尺寸：wavsen 拒绝奇数尺寸，引擎这边支持（见 nv12_tests.cpp）。
// 需要 GPU（Vulkan 1.1 + VK_KHR_timeline_semaphore，wavsen 的完成时间线要用）；没有就跳过。
// 输入只有 8 bit NV12：10 bit 源在解码器里经 swscale 转成 NV12 后才到转换器，这里不单列。

#include <gtest/gtest.h>
#include <vulkan/vulkan.h>

#include <cstdint>
#include <cstring>
#include <string>
#include <vector>

#include "nv12_gpu.hpp"

import rstd;
import rstd.cppstd;
import owe.media;
import wavsen.video;
import wescene.types;
import wescene.shader_compile;

using namespace rstd::prelude;

namespace
{

using namespace nv12_test;

std::vector<std::uint32_t> CompileNew() {
    const owe::vulkan::ShaderCompUnit unit {
        owe::ShaderType::COMPUTE, std::string(owe::media::Nv12ToRgba::ShaderSource()), "main"
    };
    std::vector<owe::vulkan::Uni_ShaderSpv> spv;
    if (! owe::vulkan::CompileAndLinkShaderUnits(
            std::span(&unit, 1),
            owe::vulkan::ShaderCompOpt { .target = owe::vulkan::VulkanTarget::Vulkan_1_0 },
            spv) ||
        spv.size() != 1)
        return {};
    return { spv.front()->spirv.begin(), spv.front()->spirv.end() };
}

struct Frame {
    std::uint32_t w, h, space, range, seed;
};

// 一对转换器（同一 max 尺寸）依次转一串帧，每帧新旧各写一张同尺寸目标，回读比较。
void RunPair(Gpu& gpu, std::uint32_t max_w, std::uint32_t max_h, const std::vector<Frame>& frames) {
    auto old_r = wavsen::video::YuvToRgba::create(
        gpu.instance, gpu.phys, gpu.device, u32(gpu.family), gpu.queue, u32(max_w), u32(max_h));
    ASSERT_TRUE(old_r.is_ok()) << rstd::cppstd::to_string(old_r.unwrap_err().message);
    auto old_conv = std::move(old_r).unwrap();

    const auto  spirv = CompileNew();
    std::string error;
    auto        new_conv =
        owe::media::Nv12ToRgba::Create(gpu.phys, gpu.device, gpu.family, gpu.queue, max_w, max_h, spirv, error);
    ASSERT_NE(new_conv, nullptr) << error;

    for (const auto& f : frames) {
        SCOPED_TRACE(testing::Message() << f.w << "x" << f.h << " space=" << f.space
                                        << " range=" << f.range << " seed=" << f.seed);
        const auto nv12 = MakeNv12(f.w, f.h, f.seed);
        Target     a(gpu, f.w, f.h);
        Target     b(gpu, f.w, f.h);

        const auto old_matrix =
            wavsen::video::make_color_matrix(static_cast<wavsen::video::ColorSpace>(f.space),
                                             static_cast<wavsen::video::ColorRange>(f.range));
        auto cv = old_conv->convert_nv12(a.image,
                                         u32(f.w),
                                         u32(f.h),
                                         nv12.data(),
                                         usize(nv12.size()),
                                         old_matrix,
                                         wavsen::video::ConvertTarget::SampledLocal);
        ASSERT_TRUE(cv.is_ok()) << rstd::cppstd::to_string(cv.unwrap_err().message);

        const auto new_matrix =
            owe::media::MakeYuvColorMatrix(f.space, f.range);
        ASSERT_TRUE(new_conv->Convert(b.image, f.w, f.h, nv12, new_matrix)) << new_conv->last_error();

        const auto pa = a.Read();
        const auto pb = b.Read();
        ASSERT_EQ(pa.size(), pb.size());
        std::size_t first = pa.size();
        std::size_t count = 0;
        for (std::size_t i = 0; i < pa.size(); ++i) {
            if (pa[i] != pb[i]) {
                if (first == pa.size()) first = i;
                ++count;
            }
        }
        EXPECT_EQ(count, 0u) << "first diff byte " << first << " pixel (" << (first / 4) % f.w
                             << ", " << (first / 4) / f.w << ")";
    }
}

class Nv12Parity : public testing::Test {
protected:
    static void SetUpTestSuite() { gpu = new Gpu(); }
    static void TearDownTestSuite() {
        delete gpu;
        gpu = nullptr;
    }
    void SetUp() override {
        if (! gpu->skip.empty()) GTEST_SKIP() << gpu->skip;
    }
    static Gpu* gpu;
};
Gpu* Nv12Parity::gpu = nullptr;

} // namespace

TEST(Nv12ParityCpu, ColorMatrixBitExact) {
    // 3 个已知色彩空间 + 未知值（回退 BT.709）× 2 个已知范围 + 未知值（回退有限范围）。
    for (std::uint32_t space : { 0u, 1u, 2u, 7u }) {
        for (std::uint32_t range : { 0u, 1u, 5u }) {
            const auto a = wavsen::video::make_color_matrix(
                static_cast<wavsen::video::ColorSpace>(space),
                static_cast<wavsen::video::ColorRange>(range));
            const auto b = owe::media::MakeYuvColorMatrix(space, range);
            static_assert(sizeof(a) == sizeof(b));
            EXPECT_EQ(std::memcmp(&a, &b, sizeof(a)), 0) << "space=" << space << " range=" << range;
        }
    }
}

// 最常见的形态：转换器的 max 就是这一路视频的尺寸；601/709/2020 × 有限/全范围逐一过。
TEST_F(Nv12Parity, ExactExtentAllMatrices) {
    std::vector<Frame> frames;
    std::uint32_t      seed = 1;
    for (std::uint32_t space : { 0u, 1u, 2u })
        for (std::uint32_t range : { 0u, 1u }) frames.push_back({ 320, 180, space, range, seed++ });
    RunPair(*gpu, 320, 180, frames);
}

// 多路视频共用一个转换器：max 大于本帧，先转大帧留下"脏"平面，再转小帧（色度钳制不能采到大帧残留）。
// 1280x686 → 色度高 343、4090x2300 → 色度宽 2045（奇数色度尺寸），外加 2x2、16x8、偶数非 8 倍数。
TEST_F(Nv12Parity, SharedMaxExtentMixedSizes) {
    RunPair(*gpu,
            4090,
            2300,
            {
                { 4090, 2300, 0, 0, 11 },
                { 1280, 686, 1, 1, 12 },
                { 320, 180, 0, 0, 13 },
                { 2, 2, 2, 0, 14 },
                { 16, 8, 1, 0, 15 },
                { 1918, 1078, 0, 1, 16 },
                { 1280, 686, 7, 5, 17 }, // 未知色彩空间与范围：回退 BT.709 有限范围
            });
}

// max 为奇数时两边都先补成偶数；帧尺寸取补齐后的上限。
TEST_F(Nv12Parity, OddMaxExtentRoundsUp) {
    RunPair(*gpu, 321, 181, { { 322, 182, 0, 0, 21 }, { 320, 180, 1, 1, 22 } });
}
