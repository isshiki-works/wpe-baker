// T5b 双实现对拍：wavsen::video::YuvToRgba 软件路径与 owe::media::Nv12ToRgba
// 吃同一份 NV12，在同一设备同一队列上各写一张 RGBA8，回读逐字节比较。
// **T5b 删 wavsen 时连同 CMake 里的 media-nv12-parity-tests 目标一起删。**
//
// 需要 GPU（Vulkan 1.1 + VK_KHR_timeline_semaphore，wavsen 的完成时间线要用）；没有就跳过。
// 输入只有 8 bit NV12：10 bit 源在解码器里经 swscale 转成 NV12 后才到转换器，这里不单列。

#include <gtest/gtest.h>
#include <vulkan/vulkan.h>

#include <cstdint>
#include <cstring>
#include <string>
#include <vector>

#include "nv12_to_rgba.spv.h" // wavsen 构建时内嵌的 SPIR-V

import rstd;
import rstd.cppstd;
import owe.media;
import wavsen.video;
import wescene.types;
import wescene.shader_compile;

using namespace rstd::prelude;

namespace
{

struct Gpu {
    VkInstance       instance { VK_NULL_HANDLE };
    VkPhysicalDevice phys { VK_NULL_HANDLE };
    VkDevice         device { VK_NULL_HANDLE };
    std::uint32_t    family { UINT32_MAX };
    VkQueue          queue { VK_NULL_HANDLE };
    VkCommandPool    pool { VK_NULL_HANDLE };
    VkCommandBuffer  cmd { VK_NULL_HANDLE };
    std::string      skip;

    Gpu() {
        const VkApplicationInfo app { .sType      = VK_STRUCTURE_TYPE_APPLICATION_INFO,
                                      .apiVersion = VK_API_VERSION_1_1 };
        const VkInstanceCreateInfo ici { .sType            = VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO,
                                         .pApplicationInfo = &app };
        if (vkCreateInstance(&ici, nullptr, &instance) != VK_SUCCESS) {
            skip = "vkCreateInstance failed";
            return;
        }
        std::uint32_t count = 0;
        vkEnumeratePhysicalDevices(instance, &count, nullptr);
        std::vector<VkPhysicalDevice> devices(count);
        vkEnumeratePhysicalDevices(instance, &count, devices.data());
        for (auto candidate : devices) {
            std::uint32_t qn = 0;
            vkGetPhysicalDeviceQueueFamilyProperties(candidate, &qn, nullptr);
            std::vector<VkQueueFamilyProperties> qs(qn);
            vkGetPhysicalDeviceQueueFamilyProperties(candidate, &qn, qs.data());
            for (std::uint32_t i = 0; i < qn; ++i) {
                if ((qs[i].queueFlags & VK_QUEUE_GRAPHICS_BIT) &&
                    (qs[i].queueFlags & VK_QUEUE_COMPUTE_BIT)) {
                    phys   = candidate;
                    family = i;
                    break;
                }
            }
            if (phys) break;
        }
        if (! phys) {
            skip = "no graphics+compute queue";
            return;
        }
        const float priority = 1.0f;
        const VkDeviceQueueCreateInfo qci { .sType            = VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO,
                                            .queueFamilyIndex = family,
                                            .queueCount       = 1,
                                            .pQueuePriorities = &priority };
        VkPhysicalDeviceTimelineSemaphoreFeaturesKHR timeline {
            .sType             = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_TIMELINE_SEMAPHORE_FEATURES_KHR,
            .timelineSemaphore = VK_TRUE,
        };
        const char*              ext = VK_KHR_TIMELINE_SEMAPHORE_EXTENSION_NAME;
        const VkDeviceCreateInfo dci { .sType                   = VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO,
                                       .pNext                   = &timeline,
                                       .queueCreateInfoCount    = 1,
                                       .pQueueCreateInfos       = &qci,
                                       .enabledExtensionCount   = 1,
                                       .ppEnabledExtensionNames = &ext };
        if (vkCreateDevice(phys, &dci, nullptr, &device) != VK_SUCCESS) {
            skip = "vkCreateDevice(timeline semaphore) failed";
            return;
        }
        vkGetDeviceQueue(device, family, 0, &queue);
        const VkCommandPoolCreateInfo pci { .sType = VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO,
                                            .flags = VK_COMMAND_POOL_CREATE_RESET_COMMAND_BUFFER_BIT,
                                            .queueFamilyIndex = family };
        vkCreateCommandPool(device, &pci, nullptr, &pool);
        const VkCommandBufferAllocateInfo cai { .sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO,
                                                .commandPool        = pool,
                                                .level              = VK_COMMAND_BUFFER_LEVEL_PRIMARY,
                                                .commandBufferCount = 1 };
        vkAllocateCommandBuffers(device, &cai, &cmd);
    }

    ~Gpu() {
        if (device) {
            vkDeviceWaitIdle(device);
            vkDestroyCommandPool(device, pool, nullptr);
            vkDestroyDevice(device, nullptr);
        }
        if (instance) vkDestroyInstance(instance, nullptr);
    }

    std::uint32_t MemoryType(std::uint32_t bits, VkMemoryPropertyFlags want) const {
        VkPhysicalDeviceMemoryProperties mp {};
        vkGetPhysicalDeviceMemoryProperties(phys, &mp);
        for (std::uint32_t i = 0; i < mp.memoryTypeCount; ++i) {
            if ((bits & (1u << i)) && (mp.memoryTypes[i].propertyFlags & want) == want) return i;
        }
        return UINT32_MAX;
    }

    template<typename F>
    void Submit(F&& record) {
        vkResetCommandBuffer(cmd, 0);
        const VkCommandBufferBeginInfo bi { .sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO,
                                            .flags = VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT };
        vkBeginCommandBuffer(cmd, &bi);
        record(cmd);
        vkEndCommandBuffer(cmd);
        const VkSubmitInfo si { .sType              = VK_STRUCTURE_TYPE_SUBMIT_INFO,
                                .commandBufferCount = 1,
                                .pCommandBuffers    = &cmd };
        vkQueueSubmit(queue, 1, &si, VK_NULL_HANDLE);
        vkQueueWaitIdle(queue);
    }
};

void ImageBarrier(VkCommandBuffer cmd, VkImage image, VkAccessFlags src_access,
                  VkAccessFlags dst_access, VkImageLayout from, VkImageLayout to,
                  VkPipelineStageFlags src_stage, VkPipelineStageFlags dst_stage) {
    const VkImageMemoryBarrier b { .sType               = VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER,
                                   .srcAccessMask       = src_access,
                                   .dstAccessMask       = dst_access,
                                   .oldLayout           = from,
                                   .newLayout           = to,
                                   .srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
                                   .dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
                                   .image               = image,
                                   .subresourceRange    = { VK_IMAGE_ASPECT_COLOR_BIT, 0, 1, 0, 1 } };
    vkCmdPipelineBarrier(cmd, src_stage, dst_stage, 0, 0, nullptr, 0, nullptr, 1, &b);
}

// 视频纹理目标：同 TextureCache::CreateVideoTex，RGBA8 清成不透明黑后停在 SHADER_READ_ONLY。
struct Target {
    Gpu&           gpu;
    std::uint32_t  w, h;
    VkImage        image { VK_NULL_HANDLE };
    VkDeviceMemory memory { VK_NULL_HANDLE };

    Target(Gpu& g, std::uint32_t width, std::uint32_t height): gpu(g), w(width), h(height) {
        const VkImageCreateInfo ici {
            .sType       = VK_STRUCTURE_TYPE_IMAGE_CREATE_INFO,
            .imageType   = VK_IMAGE_TYPE_2D,
            .format      = VK_FORMAT_R8G8B8A8_UNORM,
            .extent      = { w, h, 1 },
            .mipLevels   = 1,
            .arrayLayers = 1,
            .samples     = VK_SAMPLE_COUNT_1_BIT,
            .tiling      = VK_IMAGE_TILING_OPTIMAL,
            .usage = VK_IMAGE_USAGE_TRANSFER_DST_BIT | VK_IMAGE_USAGE_TRANSFER_SRC_BIT |
                     VK_IMAGE_USAGE_STORAGE_BIT | VK_IMAGE_USAGE_SAMPLED_BIT,
            .sharingMode   = VK_SHARING_MODE_EXCLUSIVE,
            .initialLayout = VK_IMAGE_LAYOUT_UNDEFINED,
        };
        vkCreateImage(gpu.device, &ici, nullptr, &image);
        VkMemoryRequirements mr {};
        vkGetImageMemoryRequirements(gpu.device, image, &mr);
        const VkMemoryAllocateInfo mai {
            .sType           = VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO,
            .allocationSize  = mr.size,
            .memoryTypeIndex = gpu.MemoryType(mr.memoryTypeBits, VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT),
        };
        vkAllocateMemory(gpu.device, &mai, nullptr, &memory);
        vkBindImageMemory(gpu.device, image, memory, 0);
        gpu.Submit([&](VkCommandBuffer cmd) {
            const VkImageSubresourceRange range { VK_IMAGE_ASPECT_COLOR_BIT, 0, 1, 0, 1 };
            ImageBarrier(cmd,
                         image,
                         0,
                         VK_ACCESS_TRANSFER_WRITE_BIT,
                         VK_IMAGE_LAYOUT_UNDEFINED,
                         VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL,
                         VK_PIPELINE_STAGE_TOP_OF_PIPE_BIT,
                         VK_PIPELINE_STAGE_TRANSFER_BIT);
            const VkClearColorValue black { .float32 = { 0.0f, 0.0f, 0.0f, 1.0f } };
            vkCmdClearColorImage(cmd, image, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, &black, 1, &range);
            ImageBarrier(cmd,
                         image,
                         VK_ACCESS_TRANSFER_WRITE_BIT,
                         VK_ACCESS_SHADER_READ_BIT,
                         VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL,
                         VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL,
                         VK_PIPELINE_STAGE_TRANSFER_BIT,
                         VK_PIPELINE_STAGE_FRAGMENT_SHADER_BIT);
        });
    }

    ~Target() {
        vkDeviceWaitIdle(gpu.device);
        vkDestroyImage(gpu.device, image, nullptr);
        vkFreeMemory(gpu.device, memory, nullptr);
    }

    std::vector<std::uint8_t> Read() {
        vkQueueWaitIdle(gpu.queue); // 等转换器的提交
        const VkDeviceSize       bytes = VkDeviceSize(w) * h * 4;
        const VkBufferCreateInfo bci { .sType = VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO,
                                       .size  = bytes,
                                       .usage = VK_BUFFER_USAGE_TRANSFER_DST_BIT };
        VkBuffer buffer = VK_NULL_HANDLE;
        vkCreateBuffer(gpu.device, &bci, nullptr, &buffer);
        VkMemoryRequirements mr {};
        vkGetBufferMemoryRequirements(gpu.device, buffer, &mr);
        const VkMemoryAllocateInfo mai {
            .sType           = VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO,
            .allocationSize  = mr.size,
            .memoryTypeIndex = gpu.MemoryType(mr.memoryTypeBits,
                                              VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT |
                                                  VK_MEMORY_PROPERTY_HOST_COHERENT_BIT),
        };
        VkDeviceMemory mem = VK_NULL_HANDLE;
        vkAllocateMemory(gpu.device, &mai, nullptr, &mem);
        vkBindBufferMemory(gpu.device, buffer, mem, 0);
        gpu.Submit([&](VkCommandBuffer cmd) {
            ImageBarrier(cmd,
                         image,
                         VK_ACCESS_SHADER_READ_BIT,
                         VK_ACCESS_TRANSFER_READ_BIT,
                         VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL,
                         VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL,
                         VK_PIPELINE_STAGE_FRAGMENT_SHADER_BIT,
                         VK_PIPELINE_STAGE_TRANSFER_BIT);
            const VkBufferImageCopy region { .imageSubresource = { VK_IMAGE_ASPECT_COLOR_BIT, 0, 0, 1 },
                                             .imageExtent      = { w, h, 1 } };
            vkCmdCopyImageToBuffer(
                cmd, image, VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL, buffer, 1, &region);
            ImageBarrier(cmd,
                         image,
                         VK_ACCESS_TRANSFER_READ_BIT,
                         VK_ACCESS_SHADER_READ_BIT,
                         VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL,
                         VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL,
                         VK_PIPELINE_STAGE_TRANSFER_BIT,
                         VK_PIPELINE_STAGE_FRAGMENT_SHADER_BIT);
        });
        void* mapped = nullptr;
        vkMapMemory(gpu.device, mem, 0, VK_WHOLE_SIZE, 0, &mapped);
        std::vector<std::uint8_t> out(static_cast<const std::uint8_t*>(mapped),
                                      static_cast<const std::uint8_t*>(mapped) + bytes);
        vkUnmapMemory(gpu.device, mem);
        vkDestroyBuffer(gpu.device, buffer, nullptr);
        vkFreeMemory(gpu.device, mem, nullptr);
        return out;
    }
};

// 确定性的 NV12：一半随机字节、一半平滑渐变，覆盖 0/255 两端（有限范围的越界值要被钳制）。
std::vector<std::uint8_t> MakeNv12(std::uint32_t w, std::uint32_t h, std::uint32_t seed) {
    std::vector<std::uint8_t> nv12(std::size_t(w) * h * 3 / 2);
    std::uint32_t             state = seed * 2654435761u + 1u;
    for (std::size_t i = 0; i < nv12.size(); ++i) {
        state = state * 1664525u + 1013904223u;
        const bool noise = ((i / 97) & 1) != 0;
        nv12[i] = noise ? std::uint8_t(state >> 24) : std::uint8_t((i * 7 + seed * 31) & 0xff);
    }
    return nv12;
}

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

TEST(Nv12ParityCpu, SpirvMatchesEmbedded) {
    const auto spirv = CompileNew();
    ASSERT_EQ(spirv.size() * sizeof(std::uint32_t), sizeof(nv12_to_rgba_spv));
    EXPECT_EQ(std::memcmp(spirv.data(), nv12_to_rgba_spv, sizeof(nv12_to_rgba_spv)), 0);
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

// 奇数目标尺寸：两边都拒绝，报同一句话（TextureCache 会把它记成渲染错误）。
TEST_F(Nv12Parity, OddTargetRejectedIdentically) {
    auto old_r = wavsen::video::YuvToRgba::create(
        gpu->instance, gpu->phys, gpu->device, u32(gpu->family), gpu->queue, u32(322), u32(182));
    ASSERT_TRUE(old_r.is_ok());
    auto        old_conv = std::move(old_r).unwrap();
    std::string error;
    auto new_conv = owe::media::Nv12ToRgba::Create(
        gpu->phys, gpu->device, gpu->family, gpu->queue, 322, 182, CompileNew(), error);
    ASSERT_NE(new_conv, nullptr) << error;
    Target     target(*gpu, 321, 181);
    const auto nv12 = MakeNv12(321, 181, 31);
    const auto m    = owe::media::MakeYuvColorMatrix(0, 0);
    auto       cv   = old_conv->convert_nv12(target.image,
                                     u32(321),
                                     u32(181),
                                     nv12.data(),
                                     usize(nv12.size()),
                                     wavsen::video::make_color_matrix(wavsen::video::ColorSpace::Bt709,
                                                                      wavsen::video::ColorRange::Limited),
                                     wavsen::video::ConvertTarget::SampledLocal);
    ASSERT_TRUE(cv.is_err());
    EXPECT_FALSE(new_conv->Convert(target.image, 321, 181, nv12, m));
    EXPECT_EQ(rstd::cppstd::to_string(cv.unwrap_err().message), new_conv->last_error());
}
