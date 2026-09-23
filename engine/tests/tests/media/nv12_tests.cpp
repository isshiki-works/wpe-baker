// owe::media::Nv12ToRgba 的 GPU 单测：奇数宽高按 4:2:0 色度向上取整处理。
// 需要 GPU；没有就跳过。
//
// 1) 奇数尺寸 = 偶数扩展后裁剪：奇数 W×H 的帧与"补一列/一行、色度平面原样"的偶数帧，
//    色度样本数相同（ceil(W/2) = (W+1)/2），同一转换器转出的左上 W×H 必须逐字节相同。
//    偶数路径与 wavsen 逐字节相同（media-nv12-parity-tests），这条把奇数路径钉在它上面。
// 2) 与 ffmpeg 交叉核对（设 OWE_NV12_XCHECK_DIR 才跑）：同一视频由 ffmpeg 解成 NV12，再由 ffmpeg 的
//    swscale 转成 RGB24 作参考；奇数尺寸与偶数裁剪各自对参考的误差要同一量级，最后一列/行不许比偶数帧的边更差。
//    奇数帧的参考不能让 swscale 直接在奇数尺寸上转：它的缩放路径把 ceil(W/2) 个色度样本摊满 W 个像素
//    （比例 161/321 而不是 1/2），自己与"补成偶数再裁剪"就差到 43 级。所以参考取后者：Y 补一列一行、
//    UV 平面原样（色度样本与 4:2:0 几何都不变），按偶数尺寸转换后裁回 W×H。

#include <gtest/gtest.h>
#include <vulkan/vulkan.h>

#include <algorithm>
#include <cstdint>
#include <cstdlib>
#include <fstream>
#include <string>
#include <vector>

#include "nv12_gpu.hpp"

import rstd.cppstd;
import owe.media;
import wescene.types;
import wescene.shader_compile;

namespace
{

using namespace nv12_test;

std::vector<std::uint32_t> Compile() {
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

class Nv12 : public testing::Test {
protected:
    static void SetUpTestSuite() { gpu = new Gpu(); }
    static void TearDownTestSuite() {
        delete gpu;
        gpu = nullptr;
    }
    void SetUp() override {
        if (! gpu->skip.empty()) GTEST_SKIP() << gpu->skip;
    }

    std::unique_ptr<owe::media::Nv12ToRgba> Make(std::uint32_t max_w, std::uint32_t max_h) {
        std::string error;
        auto        conv = owe::media::Nv12ToRgba::Create(
            gpu->phys, gpu->device, gpu->family, gpu->queue, max_w, max_h, Compile(), error);
        EXPECT_NE(conv, nullptr) << error;
        return conv;
    }

    // 转一帧到同尺寸的新目标图像并回读 RGBA。
    std::vector<std::uint8_t> Run(owe::media::Nv12ToRgba& conv, std::uint32_t w, std::uint32_t h,
                                  std::span<const std::uint8_t> nv12, std::uint32_t colorspace,
                                  std::uint32_t range) {
        Target target(*gpu, w, h);
        EXPECT_TRUE(conv.Convert(target.image, w, h, nv12, owe::media::MakeYuvColorMatrix(colorspace, range)))
            << conv.last_error();
        return target.Read();
    }

    static Gpu* gpu;
};
Gpu* Nv12::gpu = nullptr;

// 奇数帧的 Y 每行补一个字节、奇数高再补一整行，UV 平面原样：得到同色度样本数的偶数帧。
std::vector<std::uint8_t> EvenExtension(const std::vector<std::uint8_t>& odd, std::uint32_t w,
                                        std::uint32_t h) {
    const std::uint32_t ew = (w + 1) & ~1u, eh = (h + 1) & ~1u;
    std::vector<std::uint8_t> even;
    even.reserve(Nv12Size(ew, eh));
    for (std::uint32_t y = 0; y < eh; ++y) {
        const std::uint32_t src = std::min(y, h - 1);
        even.insert(even.end(), odd.begin() + std::size_t(src) * w, odd.begin() + std::size_t(src + 1) * w);
        for (std::uint32_t x = w; x < ew; ++x) even.push_back(std::uint8_t(37 * x + 11 * y));
    }
    even.insert(even.end(), odd.begin() + std::size_t(w) * h, odd.end());
    return even;
}

} // namespace

TEST_F(Nv12, OddExtentMatchesEvenExtensionCrop) {
    struct Case {
        std::uint32_t w, h, max_w, max_h, colorspace, range;
    };
    const Case cases[] = {
        { 321, 181, 321, 181, 0, 0 },    // 转换器 max 就是这路视频（补成 322×182）
        { 321, 181, 1280, 720, 1, 1 },   // 多路共用、max 大于本帧
        { 1, 1, 1280, 720, 0, 1 },       // 最小：一个像素一个色度样本
        { 3, 2, 1280, 720, 2, 0 },       // 只有宽奇数
        { 2, 3, 1280, 720, 0, 0 },       // 只有高奇数
        { 7, 5, 1280, 720, 1, 0 },
        { 1279, 685, 1280, 720, 0, 0 },  // 接近 max
        { 320, 181, 1280, 720, 2, 1 },
    };
    std::uint32_t seed = 100;
    for (const auto& c : cases) {
        SCOPED_TRACE(testing::Message() << c.w << "x" << c.h << " max " << c.max_w << "x" << c.max_h);
        auto conv = Make(c.max_w, c.max_h);
        ASSERT_NE(conv, nullptr);
        const auto odd  = MakeNv12(c.w, c.h, seed++);
        const auto even = EvenExtension(odd, c.w, c.h);
        const std::uint32_t ew = (c.w + 1) & ~1u, eh = (c.h + 1) & ~1u;
        ASSERT_EQ(even.size(), Nv12Size(ew, eh));
        const auto a = Run(*conv, c.w, c.h, odd, c.colorspace, c.range);
        const auto b = Run(*conv, ew, eh, even, c.colorspace, c.range);
        std::size_t diffs = 0;
        for (std::uint32_t y = 0; y < c.h; ++y) {
            for (std::uint32_t x = 0; x < c.w * 4; ++x) {
                if (a[std::size_t(y) * c.w * 4 + x] != b[std::size_t(y) * ew * 4 + x]) ++diffs;
            }
        }
        EXPECT_EQ(diffs, 0u);
    }
}

TEST_F(Nv12, OddExtentRejectsEvenLayoutSize) {
    // 奇数尺寸按 W*H*3/2（旧的整数截断写法）给的数据不是合法 NV12，必须拒绝。
    auto conv = Make(322, 182);
    ASSERT_NE(conv, nullptr);
    Target                    target(*gpu, 321, 181);
    std::vector<std::uint8_t> truncated(std::size_t(321) * 181 * 3 / 2);
    EXPECT_FALSE(conv->Convert(target.image, 321, 181, truncated, owe::media::MakeYuvColorMatrix(0, 0)));
    EXPECT_EQ(conv->last_error(), "convert_nv12: nv12_size mismatch (expected NV12 layout)");
}

// 目录里要有 xcheck.txt（一行：奇数宽 高 偶数宽 高 色彩空间编号 范围编号 帧数）与
// odd.nv12 / odd.rgb / even.nv12 / even.rgb（rawvideo，逐帧紧密排列；odd.rgb 按上面的补偶数再裁剪做）。
// 结果写 xcheck-result.json。
TEST_F(Nv12, FfmpegCrossCheck) {
    const char* dir = std::getenv("OWE_NV12_XCHECK_DIR");
    if (dir == nullptr) GTEST_SKIP() << "OWE_NV12_XCHECK_DIR 未设置";
    const std::string root(dir);
    std::uint32_t     ow, oh, ew, eh, colorspace, range, frames;
    std::ifstream(root + "/xcheck.txt") >> ow >> oh >> ew >> eh >> colorspace >> range >> frames;
    auto read_all = [&](const std::string& name) {
        std::ifstream f(root + "/" + name, std::ios::binary);
        return std::vector<std::uint8_t>(std::istreambuf_iterator<char>(f), {});
    };
    const auto odd_nv12 = read_all("odd.nv12"), odd_rgb = read_all("odd.rgb");
    const auto even_nv12 = read_all("even.nv12"), even_rgb = read_all("even.rgb");
    ASSERT_EQ(odd_nv12.size(), Nv12Size(ow, oh) * frames);
    ASSERT_EQ(even_nv12.size(), Nv12Size(ew, eh) * frames);
    ASSERT_EQ(odd_rgb.size(), std::size_t(ow) * oh * 3 * frames);
    ASSERT_EQ(even_rgb.size(), std::size_t(ew) * eh * 3 * frames);

    // 分区统计 |GPU - ffmpeg|：全部、最后一列、最后一行（逐通道，单位 1/255）。
    struct Stats {
        std::uint32_t max {};
        double        sum {};
        std::size_t   n {};
        void          Add(int d) {
            max = std::max<std::uint32_t>(max, std::uint32_t(d));
            sum += d;
            ++n;
        }
        double Mean() const { return n ? sum / double(n) : 0.0; }
    };
    struct Region {
        Stats all, last_col, last_row;
    };
    auto measure = [&](std::uint32_t w, std::uint32_t h, const std::vector<std::uint8_t>& nv12,
                       const std::vector<std::uint8_t>& rgb) {
        Region region;
        auto   conv = Make(w, h);
        for (std::uint32_t f = 0; f < frames; ++f) {
            const auto px = Run(*conv, w, h, std::span(nv12).subspan(Nv12Size(w, h) * f, Nv12Size(w, h)), colorspace, range);
            const auto* ref = rgb.data() + std::size_t(w) * h * 3 * f;
            for (std::uint32_t y = 0; y < h; ++y) {
                for (std::uint32_t x = 0; x < w; ++x) {
                    for (int c = 0; c < 3; ++c) {
                        const int d = std::abs(int(px[(std::size_t(y) * w + x) * 4 + c]) -
                                               int(ref[(std::size_t(y) * w + x) * 3 + c]));
                        region.all.Add(d);
                        if (x == w - 1) region.last_col.Add(d);
                        if (y == h - 1) region.last_row.Add(d);
                    }
                }
            }
        }
        return region;
    };
    const Region odd  = measure(ow, oh, odd_nv12, odd_rgb);
    const Region even = measure(ew, eh, even_nv12, even_rgb);

    std::ofstream out(root + "/xcheck-result.json");
    auto          dump = [&](const char* name, const Region& r) {
        auto s = [&](const char* k, const Stats& st) {
            out << "  \"" << name << "_" << k << "\": {\"max\": " << st.max << ", \"mean\": " << st.Mean() << "},\n";
        };
        s("all", r.all);
        s("last_col", r.last_col);
        s("last_row", r.last_row);
    };
    out << "{\n";
    dump("odd", odd);
    dump("even", even);
    out << "  \"frames\": " << frames << "\n}\n";

    // 判据：偶数路径与 main/wavsen 逐字节相同，是已接受的基准；奇数路径对同一个独立解码器的误差
    // 不得超出它的量级（最大值至多多 1 级、均值至多多 0.5 级），最后一列/行的均值不比偶数帧的边多 1 级以上。
    EXPECT_LE(odd.all.max, even.all.max + 1);
    EXPECT_LE(odd.all.Mean(), even.all.Mean() + 0.5);
    EXPECT_LE(odd.last_col.Mean(), even.last_col.Mean() + 1.0);
    EXPECT_LE(odd.last_row.Mean(), even.last_row.Mean() + 1.0);
}
