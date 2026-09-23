module;
// 会分配内存的模块单元先包含 <new>，避开 clang 22 的 operator new 歧义。
#include <new>

#include <vulkan/vulkan.h>

module owe.media;

import rstd.cppstd;
import wescene.vk;

// 逐项照搬 wavsen src/video/yuv_to_rgba.cpp 的软件路径（init + convert_nv12_）：
// 采样器、平面格式、上传拷贝、屏障、推常量、分组数都不变，偶数尺寸像素与 wavsen 逐字节相同。
// 与 wavsen 不同的只有奇数宽高：wavsen 直接拒绝，这里按 4:2:0 色度向上取整处理。
// 去掉的只有本路径用不到的东西：完成时间线信号量（软件帧的调用方只看栅栏，
// drain_submissions 对软件帧本就是空操作）、BridgeForeign 的导出信号量、硬解帧的上下文池。
namespace owe::media
{

namespace
{

// 与着色器里的 PC 块逐字段对应（std430 推常量，按 16 字节对齐）。
struct alignas(16) ShaderPushConstants {
    std::uint32_t dst_w;
    std::uint32_t dst_h;
    std::uint32_t software_src_w;
    std::uint32_t software_src_h;
    float         m_r[4];
    float         m_g[4];
    float         m_b[4];
    float         offset[4];
};
static_assert(sizeof(ShaderPushConstants) == 80, "PC size mismatch with shader");

// 源码取自 wavsen shaders/nv12_to_rgba.comp，只改了色度钳制上界（奇数宽高按 ceil(dst/2) 个色度样本）；
// 偶数尺寸时新旧上界的 float 值逐位相同。
constexpr std::string_view kShaderSource = R"glsl(#version 450

// NV12 -> RGBA8. The YUV->RGB matrix and offset are pushed per frame so the
// same pipeline handles BT.601 / BT.709 / BT.2020 and limited / full range.
// ycbcr = vec3(Y, Cb, Cr) sampled in [0..1]; add `offset` (negative), then dot
// against the three matrix rows. Limited-range scaling is folded into the
// matrix on the CPU side.

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(set = 0, binding = 0) uniform sampler2D y_tex;
layout(set = 0, binding = 1) uniform sampler2D uv_tex;
layout(set = 0, binding = 2, rgba8) uniform writeonly image2D dst;

layout(push_constant) uniform PC {
    uvec2 dst_size;       // 8 bytes
    uvec2 software_src_size; // shared upload extent; zero for hardware frame views
    vec4  m_r;            // {y, cb, cr, _} scaling Y/Cb/Cr -> R
    vec4  m_g;            // {y, cb, cr, _} -> G
    vec4  m_b;            // {y, cb, cr, _} -> B
    vec4  offset;         // {y_off, cb_off, cr_off, _} subtract before matmul
} pc;

void main() {
    uvec2 px = gl_GlobalInvocationID.xy;
    if (px.x >= pc.dst_size.x || px.y >= pc.dst_size.y) return;

    vec2 uv = (vec2(px) + 0.5) / vec2(pc.dst_size);
    float Y;
    vec2 CC;
    if (pc.software_src_size.x != 0u) {
        // Software frames occupy the upper-left part of shared max-size images.
        // Normalize by the allocation, then clamp chroma filtering to this frame
        // so smaller videos cannot sample padding left by another video.
        uv = (vec2(px) + 0.5) / vec2(pc.software_src_size);
        vec2 chroma_size = vec2(pc.software_src_size) * 0.5;
        vec2 chroma_min = vec2(0.5) / chroma_size;
        vec2 chroma_max = (vec2((pc.dst_size + 1u) / 2u) - 0.5) / chroma_size;
        Y = texture(y_tex, uv).r;
        CC = texture(uv_tex, clamp(uv, chroma_min, chroma_max)).rg;
    } else {
        Y = texture(y_tex, uv).r;
        CC = texture(uv_tex, uv).rg;
    }

    vec3 ycbcr = vec3(Y, CC.r, CC.g) + pc.offset.xyz;
    vec3 rgb   = vec3(dot(ycbcr, pc.m_r.xyz),
                      dot(ycbcr, pc.m_g.xyz),
                      dot(ycbcr, pc.m_b.xyz));
    imageStore(dst, ivec2(px), vec4(clamp(rgb, 0.0, 1.0), 1.0));
}
)glsl";

auto VkError(std::string_view operation, VkResult result) -> std::string {
    return std::string(operation) + ": " + owe::vk::ToString(result);
}

auto PickMemoryType(VkPhysicalDevice phys, std::uint32_t mask, VkMemoryPropertyFlags want)
    -> std::uint32_t {
    VkPhysicalDeviceMemoryProperties mp {};
    vkGetPhysicalDeviceMemoryProperties(phys, &mp);
    for (std::uint32_t i = 0; i < mp.memoryTypeCount; ++i) {
        if ((mask & (1u << i)) && (mp.memoryTypes[i].propertyFlags & want) == want) return i;
    }
    return UINT32_MAX;
}

void Barrier(VkCommandBuffer cmd, VkImage image, VkAccessFlags src_access, VkAccessFlags dst_access,
             VkImageLayout old_layout, VkImageLayout new_layout, VkPipelineStageFlags src_stage,
             VkPipelineStageFlags dst_stage) {
    const VkImageMemoryBarrier barrier {
        .sType               = VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER,
        .srcAccessMask       = src_access,
        .dstAccessMask       = dst_access,
        .oldLayout           = old_layout,
        .newLayout           = new_layout,
        .srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
        .dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
        .image               = image,
        .subresourceRange    = { VK_IMAGE_ASPECT_COLOR_BIT, 0, 1, 0, 1 },
    };
    vkCmdPipelineBarrier(cmd, src_stage, dst_stage, 0, 0, nullptr, 0, nullptr, 1, &barrier);
}

auto CreateView(VkDevice device, VkImage image, VkFormat format,
                owe::vk::Unique<VkImageView>& out) -> VkResult {
    const VkImageViewCreateInfo info {
        .sType            = VK_STRUCTURE_TYPE_IMAGE_VIEW_CREATE_INFO,
        .image            = image,
        .viewType         = VK_IMAGE_VIEW_TYPE_2D,
        .format           = format,
        .components       = { VK_COMPONENT_SWIZZLE_IDENTITY, VK_COMPONENT_SWIZZLE_IDENTITY,
                              VK_COMPONENT_SWIZZLE_IDENTITY, VK_COMPONENT_SWIZZLE_IDENTITY },
        .subresourceRange = { VK_IMAGE_ASPECT_COLOR_BIT, 0, 1, 0, 1 },
    };
    VkImageView view = VK_NULL_HANDLE;
    const auto  r    = vkCreateImageView(device, &info, nullptr, &view);
    if (r == VK_SUCCESS) out = owe::vk::Unique<VkImageView>(device, view);
    return r;
}

void WriteImageDescriptor(VkDevice device, VkDescriptorSet set, std::uint32_t binding,
                          VkDescriptorType type, const VkDescriptorImageInfo& info) {
    const VkWriteDescriptorSet write {
        .sType           = VK_STRUCTURE_TYPE_WRITE_DESCRIPTOR_SET,
        .dstSet          = set,
        .dstBinding      = binding,
        .descriptorCount = 1,
        .descriptorType  = type,
        .pImageInfo      = &info,
    };
    vkUpdateDescriptorSets(device, 1, &write, 0, nullptr);
}

} // namespace

auto MakeYuvColorMatrix(std::uint32_t colorspace, std::uint32_t range) -> YuvColorMatrix {
    // 系数与 wavsen make_color_matrix 逐个相同（ITU-R 亮度/色度权重；BT.2020 按非恒定亮度）。
    struct Weights {
        float r_cr, g_cb, g_cr, b_cb;
    };
    const Weights w = colorspace == 1   ? Weights { 1.402f, -0.34414f, -0.71414f, 1.772f }
                      : colorspace == 2 ? Weights { 1.4746f, -0.16455f, -0.57135f, 1.8814f }
                                        : Weights { 1.5748f, -0.18732f, -0.46812f, 1.85563f };
    // 全范围 y、c 缩放为 1；有限范围 y×255/219、c×255/224 折进系数（与 wavsen 同样是单次 float 乘法）。
    const bool  full = range == 1;
    const float ys   = full ? 1.0f : 255.0f / 219.0f;
    auto        c    = [&](float k) { return full ? k : k * (255.0f / 224.0f); };
    return YuvColorMatrix {
        .m_r    = { ys, 0.0f, c(w.r_cr) },
        .m_g    = { ys, c(w.g_cb), c(w.g_cr) },
        .m_b    = { ys, c(w.b_cb), 0.0f },
        .offset = { full ? 0.0f : -16.0f / 255.0f, -128.0f / 255.0f, -128.0f / 255.0f },
    };
}

auto Nv12ToRgba::ShaderSource() -> std::string_view { return kShaderSource; }

auto Nv12ToRgba::Create(VkPhysicalDevice phys, VkDevice device, std::uint32_t queue_family,
                        VkQueue queue, std::uint32_t max_w, std::uint32_t max_h,
                        std::span<const std::uint32_t> spirv, std::string& error)
    -> std::unique_ptr<Nv12ToRgba> {
    if (max_w == 0 || max_h == 0) {
        error = "YuvToRgba: max_w/max_h must be non-zero";
        return nullptr;
    }
    // NV12 色度是 4:2:0，平面宽高补成偶数。
    if (max_w % 2 != 0) ++max_w;
    if (max_h % 2 != 0) ++max_h;
    std::unique_ptr<Nv12ToRgba> self(new Nv12ToRgba());
    self->m_device       = device;
    self->m_queue        = queue;
    self->m_max_w        = max_w;
    self->m_max_h        = max_h;
    if (! self->Init(phys, queue_family, spirv)) {
        error = std::move(self->m_error);
        return nullptr;
    }
    return self;
}

Nv12ToRgba::~Nv12ToRgba() {
    // 照 wavsen 等整个设备空闲：上一次转换与之后引用目标图像的渲染都结束后才释放。
    if (m_device != VK_NULL_HANDLE) (void)vkDeviceWaitIdle(m_device);
}

auto Nv12ToRgba::Fail(std::string message) -> bool {
    m_error = std::move(message);
    return false;
}

auto Nv12ToRgba::Init(VkPhysicalDevice phys, std::uint32_t queue_family,
                      std::span<const std::uint32_t> spirv) -> bool {
    const VkDevice device = m_device;

    // 采样器：线性、边缘钳制。
    {
        const VkSamplerCreateInfo info {
            .sType        = VK_STRUCTURE_TYPE_SAMPLER_CREATE_INFO,
            .magFilter    = VK_FILTER_LINEAR,
            .minFilter    = VK_FILTER_LINEAR,
            .mipmapMode   = VK_SAMPLER_MIPMAP_MODE_NEAREST,
            .addressModeU = VK_SAMPLER_ADDRESS_MODE_CLAMP_TO_EDGE,
            .addressModeV = VK_SAMPLER_ADDRESS_MODE_CLAMP_TO_EDGE,
            .addressModeW = VK_SAMPLER_ADDRESS_MODE_CLAMP_TO_EDGE,
            .maxLod       = 0.0f,
        };
        VkSampler sampler = VK_NULL_HANDLE;
        if (auto r = vkCreateSampler(device, &info, nullptr, &sampler); r != VK_SUCCESS)
            return Fail(VkError("vkCreateSampler", r));
        m_sampler = owe::vk::Unique<VkSampler>(device, sampler);
    }

    // Y 平面 R8（max_w × max_h）与 UV 平面 R8G8（各减半），设备本地内存。
    auto plane = [&](VkFormat format, std::uint32_t w, std::uint32_t h,
                     owe::vk::Unique<VkImage>& image, owe::vk::Unique<VkDeviceMemory>& memory,
                     owe::vk::Unique<VkImageView>& view) -> bool {
        const VkImageCreateInfo info {
            .sType         = VK_STRUCTURE_TYPE_IMAGE_CREATE_INFO,
            .imageType     = VK_IMAGE_TYPE_2D,
            .format        = format,
            .extent        = { w, h, 1 },
            .mipLevels     = 1,
            .arrayLayers   = 1,
            .samples       = VK_SAMPLE_COUNT_1_BIT,
            .tiling        = VK_IMAGE_TILING_OPTIMAL,
            .usage         = VK_IMAGE_USAGE_TRANSFER_DST_BIT | VK_IMAGE_USAGE_SAMPLED_BIT,
            .sharingMode   = VK_SHARING_MODE_EXCLUSIVE,
            .initialLayout = VK_IMAGE_LAYOUT_UNDEFINED,
        };
        VkImage handle = VK_NULL_HANDLE;
        if (auto r = vkCreateImage(device, &info, nullptr, &handle); r != VK_SUCCESS)
            return Fail(VkError("vkCreateImage", r));
        image = owe::vk::Unique<VkImage>(device, handle);
        VkMemoryRequirements mr {};
        vkGetImageMemoryRequirements(device, handle, &mr);
        const auto type =
            PickMemoryType(phys, mr.memoryTypeBits, VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT);
        if (type == UINT32_MAX) return Fail("no DEVICE_LOCAL memory type for plane image");
        const VkMemoryAllocateInfo alloc {
            .sType           = VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO,
            .allocationSize  = mr.size,
            .memoryTypeIndex = type,
        };
        VkDeviceMemory mem = VK_NULL_HANDLE;
        if (auto r = vkAllocateMemory(device, &alloc, nullptr, &mem); r != VK_SUCCESS)
            return Fail(VkError("vkAllocateMemory(plane)", r));
        memory = owe::vk::Unique<VkDeviceMemory>(device, mem);
        if (auto r = vkBindImageMemory(device, handle, mem, 0); r != VK_SUCCESS)
            return Fail(VkError("vkBindImageMemory(plane)", r));
        if (auto r = CreateView(device, handle, format, view); r != VK_SUCCESS)
            return Fail(VkError("vkCreateImageView", r));
        return true;
    };
    if (! plane(VK_FORMAT_R8_UNORM, m_max_w, m_max_h, m_y_image, m_y_memory, m_y_view))
        return false;
    if (! plane(VK_FORMAT_R8G8_UNORM, m_max_w / 2, m_max_h / 2, m_uv_image, m_uv_memory, m_uv_view))
        return false;

    // 暂存缓冲：一整帧 NV12，主机可见且一致，常驻映射。
    {
        const VkDeviceSize size = VkDeviceSize(m_max_w) * m_max_h * 3 / 2;
        const VkBufferCreateInfo info {
            .sType       = VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO,
            .size        = size,
            .usage       = VK_BUFFER_USAGE_TRANSFER_SRC_BIT,
            .sharingMode = VK_SHARING_MODE_EXCLUSIVE,
        };
        VkBuffer buffer = VK_NULL_HANDLE;
        if (auto r = vkCreateBuffer(device, &info, nullptr, &buffer); r != VK_SUCCESS)
            return Fail(VkError("vkCreateBuffer(stage)", r));
        m_staging = owe::vk::Unique<VkBuffer>(device, buffer);
        VkMemoryRequirements mr {};
        vkGetBufferMemoryRequirements(device, buffer, &mr);
        const auto type = PickMemoryType(phys,
                                         mr.memoryTypeBits,
                                         VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT |
                                             VK_MEMORY_PROPERTY_HOST_COHERENT_BIT);
        if (type == UINT32_MAX) return Fail("no HOST_VISIBLE|COHERENT memory type for staging");
        const VkMemoryAllocateInfo alloc {
            .sType           = VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO,
            .allocationSize  = mr.size,
            .memoryTypeIndex = type,
        };
        VkDeviceMemory mem = VK_NULL_HANDLE;
        if (auto r = vkAllocateMemory(device, &alloc, nullptr, &mem); r != VK_SUCCESS)
            return Fail(VkError("vkAllocateMemory(stage)", r));
        m_staging_memory = owe::vk::Unique<VkDeviceMemory>(device, mem);
        if (auto r = vkBindBufferMemory(device, buffer, mem, 0); r != VK_SUCCESS)
            return Fail(VkError("vkBindBufferMemory(stage)", r));
        void* mapped = nullptr;
        if (auto r = vkMapMemory(device, mem, 0, VK_WHOLE_SIZE, 0, &mapped); r != VK_SUCCESS)
            return Fail(VkError("vkMapMemory(stage)", r));
        m_staging_map = static_cast<std::uint8_t*>(mapped);
    }

    // 着色器模块。
    {
        const VkShaderModuleCreateInfo info {
            .sType    = VK_STRUCTURE_TYPE_SHADER_MODULE_CREATE_INFO,
            .codeSize = spirv.size_bytes(),
            .pCode    = spirv.data(),
        };
        VkShaderModule module = VK_NULL_HANDLE;
        if (auto r = vkCreateShaderModule(device, &info, nullptr, &module); r != VK_SUCCESS)
            return Fail(VkError("vkCreateShaderModule", r));
        m_shader = owe::vk::Unique<VkShaderModule>(device, module);
    }

    // 描述符布局：0/1 采样 Y/UV，2 写目标。
    {
        const VkDescriptorSetLayoutBinding bindings[3] {
            { 0, VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER, 1, VK_SHADER_STAGE_COMPUTE_BIT, nullptr },
            { 1, VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER, 1, VK_SHADER_STAGE_COMPUTE_BIT, nullptr },
            { 2, VK_DESCRIPTOR_TYPE_STORAGE_IMAGE, 1, VK_SHADER_STAGE_COMPUTE_BIT, nullptr },
        };
        const VkDescriptorSetLayoutCreateInfo info {
            .sType        = VK_STRUCTURE_TYPE_DESCRIPTOR_SET_LAYOUT_CREATE_INFO,
            .bindingCount = 3,
            .pBindings    = bindings,
        };
        VkDescriptorSetLayout layout = VK_NULL_HANDLE;
        if (auto r = vkCreateDescriptorSetLayout(device, &info, nullptr, &layout); r != VK_SUCCESS)
            return Fail(VkError("vkCreateDescriptorSetLayout", r));
        m_set_layout = owe::vk::Unique<VkDescriptorSetLayout>(device, layout);
    }

    // 管线布局（推常量：目标尺寸 + 颜色矩阵）与计算管线。
    {
        const VkPushConstantRange range { VK_SHADER_STAGE_COMPUTE_BIT, 0,
                                          sizeof(ShaderPushConstants) };
        const VkDescriptorSetLayout set_layout = *m_set_layout;
        const VkPipelineLayoutCreateInfo info {
            .sType                  = VK_STRUCTURE_TYPE_PIPELINE_LAYOUT_CREATE_INFO,
            .setLayoutCount         = 1,
            .pSetLayouts            = &set_layout,
            .pushConstantRangeCount = 1,
            .pPushConstantRanges    = &range,
        };
        VkPipelineLayout layout = VK_NULL_HANDLE;
        if (auto r = vkCreatePipelineLayout(device, &info, nullptr, &layout); r != VK_SUCCESS)
            return Fail(VkError("vkCreatePipelineLayout", r));
        m_pipeline_layout = owe::vk::Unique<VkPipelineLayout>(device, layout);

        const VkComputePipelineCreateInfo pipeline_info {
            .sType  = VK_STRUCTURE_TYPE_COMPUTE_PIPELINE_CREATE_INFO,
            .stage  = { .sType  = VK_STRUCTURE_TYPE_PIPELINE_SHADER_STAGE_CREATE_INFO,
                        .stage  = VK_SHADER_STAGE_COMPUTE_BIT,
                        .module = *m_shader,
                        .pName  = "main" },
            .layout = layout,
        };
        VkPipeline pipeline = VK_NULL_HANDLE;
        if (auto r = vkCreateComputePipelines(
                device, VK_NULL_HANDLE, 1, &pipeline_info, nullptr, &pipeline);
            r != VK_SUCCESS)
            return Fail(VkError("vkCreateComputePipelines", r));
        m_pipeline = owe::vk::Unique<VkPipeline>(device, pipeline);
    }

    // 描述符池（一套）与集合；0/1 两个采样绑定固定不变，这里一次写好。
    {
        const VkDescriptorPoolSize sizes[2] {
            { VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER, 2 },
            { VK_DESCRIPTOR_TYPE_STORAGE_IMAGE, 1 },
        };
        auto arena = owe::vk::DescriptorArenaGeneration::Create(device, 1, sizes);
        if (! arena.created()) return Fail(VkError("vkCreateDescriptorPool", arena.api_result));
        auto allocation = owe::vk::DescriptorArenaGeneration::Allocate(arena.arena, *m_set_layout);
        if (! allocation.allocated())
            return Fail(VkError("vkAllocateDescriptorSets", allocation.api_result));
        m_set = std::move(allocation.lease);
        WriteImageDescriptor(device,
                             m_set.handle,
                             0,
                             VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER,
                             { *m_sampler, *m_y_view, VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL });
        WriteImageDescriptor(device,
                             m_set.handle,
                             1,
                             VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER,
                             { *m_sampler, *m_uv_view, VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL });
    }

    // 命令池 + 一个命令缓冲（随池释放）+ 每次提交的栅栏。
    {
        const VkCommandPoolCreateInfo info {
            .sType            = VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO,
            .flags            = VK_COMMAND_POOL_CREATE_RESET_COMMAND_BUFFER_BIT,
            .queueFamilyIndex = queue_family,
        };
        VkCommandPool pool = VK_NULL_HANDLE;
        if (auto r = vkCreateCommandPool(device, &info, nullptr, &pool); r != VK_SUCCESS)
            return Fail(VkError("vkCreateCommandPool", r));
        m_command_pool = owe::vk::Unique<VkCommandPool>(device, pool);
        const VkCommandBufferAllocateInfo alloc {
            .sType              = VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO,
            .commandPool        = pool,
            .level              = VK_COMMAND_BUFFER_LEVEL_PRIMARY,
            .commandBufferCount = 1,
        };
        if (auto r = vkAllocateCommandBuffers(device, &alloc, &m_command); r != VK_SUCCESS)
            return Fail(VkError("vkAllocateCommandBuffers", r));
        const VkFenceCreateInfo fence_info { .sType = VK_STRUCTURE_TYPE_FENCE_CREATE_INFO };
        VkFence fence = VK_NULL_HANDLE;
        if (auto r = vkCreateFence(device, &fence_info, nullptr, &fence); r != VK_SUCCESS)
            return Fail(VkError("vkCreateFence", r));
        m_fence = owe::vk::Unique<VkFence>(device, fence);
    }
    return true;
}

auto Nv12ToRgba::Convert(VkImage dst, std::uint32_t width, std::uint32_t height,
                         std::span<const std::uint8_t> nv12, const YuvColorMatrix& matrix) -> bool {
    m_error.clear();
    if (dst == VK_NULL_HANDLE) return Fail("convert_nv12: dst VkImage null");
    if (width == 0 || height == 0) return Fail("convert_nv12: dst_w/h zero");
    if (width > m_max_w || height > m_max_h)
        return Fail("convert_nv12: dst exceeds configured max extent");
    // 4:2:0 色度向上取整：奇数宽（高）时最后一列（行）像素独占一个色度样本。
    const std::uint32_t chroma_w = (width + 1) / 2;
    const std::uint32_t chroma_h = (height + 1) / 2;
    if (nv12.size() != std::size_t(width) * height + std::size_t(chroma_w) * chroma_h * 2)
        return Fail("convert_nv12: nv12_size mismatch (expected NV12 layout)");

    // 暂存缓冲、平面图像和命令缓冲都只有一份：先等上一次提交完成。
    if (m_fence_pending) {
        const VkFence fence = *m_fence;
        if (auto r = vkWaitForFences(m_device, 1, &fence, VK_TRUE, 1'000'000'000ull);
            r != VK_SUCCESS)
            return Fail(VkError("vkWaitForFences", r));
        if (auto r = vkResetFences(m_device, 1, &fence); r != VK_SUCCESS)
            return Fail(VkError("vkResetFences", r));
        m_fence_pending = false;
    }

    // UV 平面在暂存区里放到 4 字节对齐处：R8G8 的拷贝要求 bufferOffset 是 2 的倍数，
    // 宽高都是奇数时 W*H 是奇数。偶数宽高时 W*H 本就是 4 的倍数，布局不变。
    const std::size_t y_bytes   = std::size_t(width) * height;
    const std::size_t uv_offset = (y_bytes + 3) & ~std::size_t(3);
    std::memcpy(m_staging_map, nv12.data(), y_bytes);
    std::memcpy(m_staging_map + uv_offset, nv12.data() + y_bytes, nv12.size() - y_bytes);

    // 目标视图每次新建（调用方可能换目标图像），留到下一次等过栅栏后再销毁。
    owe::vk::Unique<VkImageView> dst_view;
    if (auto r = CreateView(m_device, dst, VK_FORMAT_R8G8B8A8_UNORM, dst_view); r != VK_SUCCESS)
        return Fail(VkError("vkCreateImageView(dst)", r));
    WriteImageDescriptor(
        m_device, m_set.handle, 2, VK_DESCRIPTOR_TYPE_STORAGE_IMAGE,
        { VK_NULL_HANDLE, *dst_view, VK_IMAGE_LAYOUT_GENERAL });

    if (auto r = vkResetCommandBuffer(m_command, 0); r != VK_SUCCESS)
        return Fail(VkError("vkResetCommandBuffer", r));
    const VkCommandBufferBeginInfo begin {
        .sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO,
        .flags = VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT,
    };
    if (auto r = vkBeginCommandBuffer(m_command, &begin); r != VK_SUCCESS)
        return Fail(VkError("vkBeginCommandBuffer", r));

    const VkImage y_image  = *m_y_image;
    const VkImage uv_image = *m_uv_image;
    for (VkImage plane : { y_image, uv_image }) {
        Barrier(m_command,
                plane,
                0,
                VK_ACCESS_TRANSFER_WRITE_BIT,
                VK_IMAGE_LAYOUT_UNDEFINED,
                VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL,
                VK_PIPELINE_STAGE_TOP_OF_PIPE_BIT,
                VK_PIPELINE_STAGE_TRANSFER_BIT);
    }
    // Y 在暂存区 [0, W*H)，UV 从 uv_offset 起；两块都按紧密排列拷进平面左上角。
    auto copy = [&](VkDeviceSize offset, VkImage plane, std::uint32_t w, std::uint32_t h) {
        const VkBufferImageCopy region {
            .bufferOffset     = offset,
            .imageSubresource = { VK_IMAGE_ASPECT_COLOR_BIT, 0, 0, 1 },
            .imageExtent      = { w, h, 1 },
        };
        vkCmdCopyBufferToImage(
            m_command, *m_staging, plane, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, 1, &region);
    };
    copy(0, y_image, width, height);
    copy(uv_offset, uv_image, chroma_w, chroma_h);
    for (VkImage plane : { y_image, uv_image }) {
        Barrier(m_command,
                plane,
                VK_ACCESS_TRANSFER_WRITE_BIT,
                VK_ACCESS_SHADER_READ_BIT,
                VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL,
                VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL,
                VK_PIPELINE_STAGE_TRANSFER_BIT,
                VK_PIPELINE_STAGE_COMPUTE_SHADER_BIT);
    }
    // 目标：片元着色器读 → 计算着色器写。
    Barrier(m_command,
            dst,
            VK_ACCESS_SHADER_READ_BIT,
            VK_ACCESS_SHADER_WRITE_BIT,
            VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL,
            VK_IMAGE_LAYOUT_GENERAL,
            VK_PIPELINE_STAGE_FRAGMENT_SHADER_BIT,
            VK_PIPELINE_STAGE_COMPUTE_SHADER_BIT);

    vkCmdBindPipeline(m_command, VK_PIPELINE_BIND_POINT_COMPUTE, *m_pipeline);
    vkCmdBindDescriptorSets(m_command,
                            VK_PIPELINE_BIND_POINT_COMPUTE,
                            *m_pipeline_layout,
                            0,
                            1,
                            &m_set.handle,
                            0,
                            nullptr);
    ShaderPushConstants pc {};
    pc.dst_w          = width;
    pc.dst_h          = height;
    pc.software_src_w = m_max_w;
    pc.software_src_h = m_max_h;
    for (int i = 0; i < 3; ++i) {
        pc.m_r[i]    = matrix.m_r[i];
        pc.m_g[i]    = matrix.m_g[i];
        pc.m_b[i]    = matrix.m_b[i];
        pc.offset[i] = matrix.offset[i];
    }
    vkCmdPushConstants(
        m_command, *m_pipeline_layout, VK_SHADER_STAGE_COMPUTE_BIT, 0, sizeof(pc), &pc);
    vkCmdDispatch(m_command, (width + 7) / 8, (height + 7) / 8, 1);

    // 目标：计算着色器写 → 片元着色器读。
    Barrier(m_command,
            dst,
            VK_ACCESS_SHADER_WRITE_BIT,
            VK_ACCESS_SHADER_READ_BIT,
            VK_IMAGE_LAYOUT_GENERAL,
            VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL,
            VK_PIPELINE_STAGE_COMPUTE_SHADER_BIT,
            VK_PIPELINE_STAGE_FRAGMENT_SHADER_BIT);

    if (auto r = vkEndCommandBuffer(m_command); r != VK_SUCCESS)
        return Fail(VkError("vkEndCommandBuffer", r));
    const VkSubmitInfo submit {
        .sType              = VK_STRUCTURE_TYPE_SUBMIT_INFO,
        .commandBufferCount = 1,
        .pCommandBuffers    = &m_command,
    };
    if (auto r = vkQueueSubmit(m_queue, 1, &submit, *m_fence); r != VK_SUCCESS)
        return Fail(VkError("vkQueueSubmit", r));
    m_fence_pending = true;
    m_target_view   = std::move(dst_view);
    return true;
}

} // namespace owe::media
