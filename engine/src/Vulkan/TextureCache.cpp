module;
#include <new>

#include <cmath>
#include <limits>
#include <rstd/macro.hpp>

#include "vvk/macros.hpp"

module wescene.vulkan;
import wescene.core;
import rstd;
import rstd.log;
import rstd.cppstd;

import wescene.types;
import wescene.fs;
import wavsen.video;

using namespace owe;
using namespace owe::vulkan;
using namespace rstd::prelude;

namespace owe
{
namespace vulkan
{
VkFormat ToVkType(TextureFormat tf) {
    switch (tf) {
    case TextureFormat::BC1: return VK_FORMAT_BC1_RGBA_UNORM_BLOCK;
    case TextureFormat::BC2: return VK_FORMAT_BC2_UNORM_BLOCK;
    case TextureFormat::BC3: return VK_FORMAT_BC3_UNORM_BLOCK;
    case TextureFormat::R8: return VK_FORMAT_R8_UNORM;
    case TextureFormat::RG8: return VK_FORMAT_R8G8_UNORM;
    case TextureFormat::RGB8: return VK_FORMAT_R8G8B8_UNORM;
    case TextureFormat::RGBA8: return VK_FORMAT_R8G8B8A8_UNORM;
    case TextureFormat::D32F: return VK_FORMAT_D32_SFLOAT;
    default: rstd_assert(false); return VK_FORMAT_R8G8B8A8_UNORM;
    }
}

VkSamplerAddressMode ToVkType(owe::TextureWrap sam) {
    using namespace owe;
    switch (sam) {
    case TextureWrap::CLAMP_TO_EDGE: return VK_SAMPLER_ADDRESS_MODE_CLAMP_TO_EDGE;
    case TextureWrap::CLAMP_TO_BORDER: return VK_SAMPLER_ADDRESS_MODE_CLAMP_TO_BORDER;
    case TextureWrap::REPEAT:
    default: return VK_SAMPLER_ADDRESS_MODE_REPEAT;
    }
}
VkFilter ToVkType(owe::TextureFilter sam) {
    using namespace owe;
    switch (sam) {
    case TextureFilter::LINEAR: return VK_FILTER_LINEAR;
    case TextureFilter::NEAREST:
    default: return VK_FILTER_NEAREST;
    }
}
} // namespace vulkan
} // namespace owe

namespace
{
VkBorderColor ToVkBorderColor(TextureBorderColor color) {
    switch (color) {
    case TextureBorderColor::TransparentBlack: return VK_BORDER_COLOR_FLOAT_TRANSPARENT_BLACK;
    case TextureBorderColor::OpaqueBlack: return VK_BORDER_COLOR_INT_OPAQUE_BLACK;
    case TextureBorderColor::OpaqueWhite: return VK_BORDER_COLOR_FLOAT_OPAQUE_WHITE;
    }
    return VK_BORDER_COLOR_INT_OPAQUE_BLACK;
}

VkSamplerCreateInfo GenSamplerInfo(TextureKey key) {
    auto& sam = key.sample;

    VkSamplerCreateInfo sampler_info { .sType            = VK_STRUCTURE_TYPE_SAMPLER_CREATE_INFO,
                                       .pNext            = nullptr,
                                       .magFilter        = ToVkType(sam.magFilter),
                                       .minFilter        = (ToVkType(sam.minFilter)),
                                       .mipmapMode       = VK_SAMPLER_MIPMAP_MODE_LINEAR,
                                       .addressModeU     = (ToVkType(sam.wrapS)),
                                       .addressModeV     = (ToVkType(sam.wrapT)),
                                       .addressModeW     = (ToVkType(sam.wrapT)),
                                       .anisotropyEnable = (false),
                                       .maxAnisotropy    = (1.0f),
                                       .compareEnable    = sam.compare_enable,
                                       .compareOp        = ToVkType(sam.compare_op),
                                       .minLod           = (0.0f),
                                       .maxLod           = (1.0f),
                                       .borderColor      = ToVkBorderColor(sam.border_color),
                                       .unnormalizedCoordinates = (false) };
    return sampler_info;
}

VkResult TransImgLayout(const vvk::Queue& queue, vvk::CommandBuffer& cmd,
                        const ImageParameters& image, VkImageLayout layout) {
    VkResult result;
    do {
        result = cmd.Begin(VkCommandBufferBeginInfo {
            .sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO,
            .pNext = nullptr,
            .flags = VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT,
        });
        if (result != VK_SUCCESS) break;

        VkImageSubresourceRange subresourceRange {
            .aspectMask     = layout == VK_IMAGE_LAYOUT_DEPTH_STENCIL_ATTACHMENT_OPTIMAL
                                  ? VK_IMAGE_ASPECT_DEPTH_BIT
                                  : VK_IMAGE_ASPECT_COLOR_BIT,
            .baseMipLevel   = 0,
            .levelCount     = VK_REMAINING_MIP_LEVELS,
            .baseArrayLayer = 0,
            .layerCount     = VK_REMAINING_ARRAY_LAYERS,
        };
        const bool    depth_layout = layout == VK_IMAGE_LAYOUT_DEPTH_STENCIL_ATTACHMENT_OPTIMAL;
        VkAccessFlags dst_access   = depth_layout ? VK_ACCESS_DEPTH_STENCIL_ATTACHMENT_READ_BIT |
                                                        VK_ACCESS_DEPTH_STENCIL_ATTACHMENT_WRITE_BIT
                                                  : VK_ACCESS_MEMORY_READ_BIT;
        VkPipelineStageFlags dst_stage = depth_layout
                                             ? VK_PIPELINE_STAGE_EARLY_FRAGMENT_TESTS_BIT |
                                                   VK_PIPELINE_STAGE_LATE_FRAGMENT_TESTS_BIT
                                             : VK_PIPELINE_STAGE_FRAGMENT_SHADER_BIT;
        {
            VkImageMemoryBarrier out_bar {
                .sType            = VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER,
                .pNext            = nullptr,
                .srcAccessMask    = {},
                .dstAccessMask    = dst_access,
                .oldLayout        = VK_IMAGE_LAYOUT_UNDEFINED,
                .newLayout        = layout,
                .image            = image.handle,
                .subresourceRange = subresourceRange,
            };
            cmd.PipelineBarrier(
                VK_PIPELINE_STAGE_TOP_OF_PIPE_BIT, dst_stage, VK_DEPENDENCY_BY_REGION_BIT, out_bar);
        }
        result = cmd.End();
        if (result != VK_SUCCESS) break;

        VkSubmitInfo sub_info {
            .sType              = VK_STRUCTURE_TYPE_SUBMIT_INFO,
            .pNext              = nullptr,
            .commandBufferCount = 1,
            .pCommandBuffers    = cmd.address(),
        };
        result = queue.Submit(sub_info);
    } while (false);
    return result;
}

Option<vvk::DeviceMemory> AllocateMemory(const vvk::Device& device, vvk::PhysicalDevice gpu,
                                         VkMemoryRequirements reqs, VkMemoryPropertyFlags property,
                                         void* pNext = nullptr) {
    VkPhysicalDeviceMemoryProperties pros = gpu.GetMemoryProperties().memoryProperties;
    for (rstd::uint32_t i = 0; i < pros.memoryTypeCount; ++i) {
        if ((reqs.memoryTypeBits & (1u << i)) && (pros.memoryTypes[i].propertyFlags & property)) {
            VkMemoryAllocateInfo memory_allocate_info { .sType =
                                                            VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO,
                                                        .pNext           = pNext,
                                                        .allocationSize  = reqs.size,
                                                        .memoryTypeIndex = i };
            vvk::DeviceMemory    mem;
            VkResult             res = device.AllocateMemory(memory_allocate_info, mem);
            if (res == VK_SUCCESS) {
                return Some(rstd::move(mem));
            } else {
                VVK_CHECK(res);
                return None();
            }
        }
    }
    rstd_error("vulkan allocate memory failed, no memory match requires");
    return None();
}

// DRM fourcc codes we emit. We currently only render R8G8B8A8_UNORM and
// B8G8R8A8_UNORM into the ExSwapchain, so those are the only mappings.
// Vulkan component order X-Y-Z-W maps to the *first-byte-in-memory* ordering
// used by DRM fourccs ('AB24' = little-endian 'AB24' bytes = A, B, 2, 4 →
// DRM_FORMAT_ABGR8888).
static rstd::uint32_t VkFormatToDrmFourcc(VkFormat fmt) {
    switch (fmt) {
    case VK_FORMAT_R8G8B8A8_UNORM: return 0x34324241u; // DRM_FORMAT_ABGR8888
    case VK_FORMAT_B8G8R8A8_UNORM: return 0x34324152u; // DRM_FORMAT_ARGB8888
    default: return 0u;
    }
}

Option<ExImageParameters> CreateExImage(rstd::uint32_t width, rstd::uint32_t height,
                                        VkFormat format, VkImageTiling tiling,
                                        VkSamplerCreateInfo sampler_info, VkImageUsageFlags usage,
                                        const vvk::Device& device, const vvk::PhysicalDevice& gpu) {
    ExImageParameters image;
    do {
        // Iteration 1a: switch the external handle type from OPAQUE_FD to
        // real Linux DMA-BUF so the FD is importable outside this Vulkan
        // instance. The OPTIMAL code path would additionally use
        // VK_IMAGE_TILING_DRM_FORMAT_MODIFIER_EXT and pick a modifier from
        // a queried list; we keep LINEAR-only for now so that consumers
        // can mmap the buffer directly (the iteration 4 milestone).
        if (tiling != VK_IMAGE_TILING_LINEAR) {
            rstd_info("[ex-image] OPTIMAL tiling requested; downgrading to LINEAR "
                      "because the DRM-format-modifier path is not yet wired up");
            tiling = VK_IMAGE_TILING_LINEAR;
        }

        VkExternalMemoryImageCreateInfo ex_info {
            .sType       = VK_STRUCTURE_TYPE_EXTERNAL_MEMORY_IMAGE_CREATE_INFO,
            .pNext       = nullptr,
            .handleTypes = VK_EXTERNAL_MEMORY_HANDLE_TYPE_DMA_BUF_BIT_EXT,
        };
        VkExportMemoryAllocateInfo ex_mem_info {
            .sType       = VK_STRUCTURE_TYPE_EXPORT_MEMORY_ALLOCATE_INFO,
            .pNext       = nullptr,
            .handleTypes = VK_EXTERNAL_MEMORY_HANDLE_TYPE_DMA_BUF_BIT_EXT,
        };
        VkImageCreateInfo info {
            .sType                 = VK_STRUCTURE_TYPE_IMAGE_CREATE_INFO,
            .pNext                 = &ex_info,
            .imageType             = VK_IMAGE_TYPE_2D,
            .format                = format,
            .extent                = VkExtent3D { .width = width, .height = height, .depth = 1 },
            .mipLevels             = 1,
            .arrayLayers           = 1,
            .samples               = VK_SAMPLE_COUNT_1_BIT,
            .tiling                = tiling,
            .usage                 = usage,
            .sharingMode           = VK_SHARING_MODE_EXCLUSIVE,
            .queueFamilyIndexCount = 0,
            .initialLayout         = VK_IMAGE_LAYOUT_UNDEFINED,
        };
        image.extent = info.extent;

        VVK_CHECK_ACT(break, device.CreateImage(info, image.handle));

        image.mem_reqs = device.GetImageMemoryRequirements(*image.handle);

        if (auto opt = AllocateMemory(
                device, gpu, image.mem_reqs, VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT, &ex_mem_info);
            opt.is_some()) {
            image.mem = rstd::move(opt).unwrap();
        } else
            break;

        VVK_CHECK_ACT(break, image.handle.BindMemory(*image.mem, 0));
        {
            VkImageViewCreateInfo createinfo {
                .sType    = VK_STRUCTURE_TYPE_IMAGE_VIEW_CREATE_INFO,
                .pNext    = nullptr,
                .image    = *image.handle,
                .viewType = VK_IMAGE_VIEW_TYPE_2D,
                .format   = format,
                .subresourceRange =
                    VkImageSubresourceRange {
                        .aspectMask     = VK_IMAGE_ASPECT_COLOR_BIT,
                        .baseMipLevel   = 0,
                        .levelCount     = 1,
                        .baseArrayLayer = 0,
                        .layerCount     = 1,
                    },
            };
            VVK_CHECK_ACT(break, device.CreateImageView(createinfo, image.view));
        }
        VVK_CHECK_ACT(break, device.CreateSampler(sampler_info, image.sampler));
        VVK_CHECK_ACT(break, image.mem.GetMemoryFdKHR(&image.fd));

        // Populate the DRM metadata so LocalExSwapchain → ExHandle (and
        // eventually the waywallen-host process) can forward it to external
        // consumers without re-querying Vulkan.
        VkImageSubresource subres {
            .aspectMask = VK_IMAGE_ASPECT_COLOR_BIT,
            .mipLevel   = 0,
            .arrayLayer = 0,
        };
        VkSubresourceLayout layout = device.GetImageSubresourceLayout(*image.handle, subres);
        image.plane0_offset        = layout.offset;
        image.plane0_stride        = static_cast<rstd::uint32_t>(layout.rowPitch);
        image.drm_modifier         = 0; // DRM_FORMAT_MOD_LINEAR
        image.drm_fourcc           = VkFormatToDrmFourcc(format);

        return Some(rstd::move(image));

    } while (false);
    return None();
}

inline Option<VmaImageParameters>
CreateImage(const Device& device, VkExtent3D extent, rstd::uint32_t miplevel, VkFormat format,
            VkSamplerCreateInfo sampler_info, VkImageUsageFlags usage,
            VmaMemoryUsage        mem_usage = VMA_MEMORY_USAGE_GPU_ONLY,
            VkSampleCountFlagBits samples   = VK_SAMPLE_COUNT_1_BIT) {
    VmaImageParameters image;
    do {
        // Multisample images can't have mipmaps; force levelCount=1 and
        // restrict usage to color attachment (no transfer/sampled needed
        // since the resolved sibling carries the readable copy).
        if (samples != VK_SAMPLE_COUNT_1_BIT) {
            miplevel = 1;
            if ((usage & VK_IMAGE_USAGE_DEPTH_STENCIL_ATTACHMENT_BIT) == 0)
                usage = VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT;
        }
        VkImageCreateInfo info {
            .sType                 = VK_STRUCTURE_TYPE_IMAGE_CREATE_INFO,
            .pNext                 = nullptr,
            .imageType             = VK_IMAGE_TYPE_2D,
            .format                = format,
            .extent                = extent,
            .mipLevels             = miplevel,
            .arrayLayers           = 1,
            .samples               = samples,
            .tiling                = VK_IMAGE_TILING_OPTIMAL,
            .usage                 = usage,
            .sharingMode           = VK_SHARING_MODE_EXCLUSIVE,
            .queueFamilyIndexCount = 0,
            .initialLayout         = VK_IMAGE_LAYOUT_UNDEFINED,
        };
        image.extent = info.extent;
        VmaAllocationCreateInfo vma_info {};
        vma_info.usage = mem_usage;
        VVK_CHECK_ACT(break,
                      vvk::CreateImage(device.vma_allocator(), info, vma_info, image.handle));

        image.mipmap_level = miplevel;
        {
            const bool depth_usage = (usage & VK_IMAGE_USAGE_DEPTH_STENCIL_ATTACHMENT_BIT) != 0;
            VkImageViewCreateInfo createinfo {
                .sType    = VK_STRUCTURE_TYPE_IMAGE_VIEW_CREATE_INFO,
                .pNext    = nullptr,
                .image    = *image.handle,
                .viewType = VK_IMAGE_VIEW_TYPE_2D,
                .format   = format,
                .subresourceRange =
                    VkImageSubresourceRange {
                        .aspectMask =
                            depth_usage ? VK_IMAGE_ASPECT_DEPTH_BIT : VK_IMAGE_ASPECT_COLOR_BIT,
                        .baseMipLevel   = 0,
                        .levelCount     = miplevel,
                        .baseArrayLayer = 0,
                        .layerCount     = 1,
                    },
            };
            VVK_CHECK_ACT(break, device.handle().CreateImageView(createinfo, image.view));
        }
        VVK_CHECK_ACT(break, device.handle().CreateSampler(sampler_info, image.sampler));
        return Some(rstd::move(image));
    } while (false);
    /*
    if (result != vk::Result::eSuccess) {
        device.DestroyImageParameters(image);
    }
    */
    return None();
}

} // namespace

usize TextureKey::HashValue(const TextureKey& k) {
    std::size_t seed = 0;
    utils::hash_combine(seed, k.width.to_primitive());
    utils::hash_combine(seed, k.height.to_primitive());
    utils::hash_combine(seed, (int)k.usage);
    utils::hash_combine(seed, (int)k.format);
    utils::hash_combine(seed, (int)k.mipmap_level);

    utils::hash_combine(seed, (int)k.sample.wrapS);
    utils::hash_combine(seed, (int)k.sample.wrapT);
    utils::hash_combine(seed, (int)k.sample.magFilter);
    utils::hash_combine(seed, (int)k.sample.minFilter);
    utils::hash_combine(seed, k.sample.compare_enable);
    utils::hash_combine(seed, (int)k.sample.compare_op);
    utils::hash_combine(seed, (int)k.sample.border_color);
    utils::hash_combine(seed, (int)k.samples);
    return usize(seed);
}

Option<ExImageParameters> TextureCache::CreateExTex(u32 width, u32 height, VkFormat format,
                                                    VkImageTiling tiling) {
    VkSamplerCreateInfo sampler_info {
        .sType                   = VK_STRUCTURE_TYPE_SAMPLER_CREATE_INFO,
        .pNext                   = nullptr,
        .magFilter               = VK_FILTER_NEAREST,
        .minFilter               = VK_FILTER_NEAREST,
        .mipmapMode              = VK_SAMPLER_MIPMAP_MODE_LINEAR,
        .addressModeU            = VK_SAMPLER_ADDRESS_MODE_MIRRORED_REPEAT,
        .addressModeV            = VK_SAMPLER_ADDRESS_MODE_MIRRORED_REPEAT,
        .addressModeW            = VK_SAMPLER_ADDRESS_MODE_MIRRORED_REPEAT,
        .anisotropyEnable        = false,
        .maxAnisotropy           = 1.0f,
        .compareEnable           = false,
        .compareOp               = VK_COMPARE_OP_NEVER,
        .minLod                  = 0.0f,
        .maxLod                  = 1.0f,
        .borderColor             = VK_BORDER_COLOR_INT_OPAQUE_BLACK,
        .unnormalizedCoordinates = false,
    };

    auto opt = CreateExImage(width.to_primitive(),
                             height.to_primitive(),
                             format,
                             tiling,
                             sampler_info,
                             VK_IMAGE_USAGE_SAMPLED_BIT | VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT |
                                 VK_IMAGE_USAGE_TRANSFER_DST_BIT,
                             m_device.device(),
                             m_device.gpu());
    if (opt.is_some()) {
        AssignImageGeneration(*opt);
        const auto& eximg = *opt;

        if (! m_tex_cmd) allocateCmd();
        TransImgLayout(m_device.graphics_queue().handle,
                       m_tex_cmd,
                       ToImageParameters(eximg),
                       VK_IMAGE_LAYOUT_GENERAL);
        VVK_CHECK(m_device.handle().WaitIdle());
    }
    return opt;
}

Option<rstd::sync::Arc<TextureAllocation>>
TextureCache::AllocateImportedTexture(const Image&                                image,
                                      Option<rstd::sync::Arc<VideoPlaybackState>> playback) {
    if (image.header.type == ImageType::VIDEO) {
        return CreateVideoTex(image, rstd::move(playback));
    }

    ImageSlots img_slots;

    img_slots.slots.resize(image.slots.size());

    auto& sam = image.header.sample;

    for (std::size_t i = 0; i < image.slots.size(); ++i) {
        auto&       image_paras   = img_slots.slots[i];
        const auto& image_slot    = image.slots[i];
        auto        mipmap_levels = image_slot.mipmaps.size();

        // check data
        if (! image_slot) return rstd::None();
        VkSamplerCreateInfo sampler_info {
            .sType                   = VK_STRUCTURE_TYPE_SAMPLER_CREATE_INFO,
            .pNext                   = nullptr,
            .magFilter               = ToVkType(sam.magFilter),
            .minFilter               = (ToVkType(sam.minFilter)),
            .mipmapMode              = VK_SAMPLER_MIPMAP_MODE_LINEAR,
            .addressModeU            = (ToVkType(sam.wrapS)),
            .addressModeV            = (ToVkType(sam.wrapS)),
            .addressModeW            = (ToVkType(sam.wrapT)),
            .anisotropyEnable        = (false),
            .maxAnisotropy           = (1.0f),
            .compareEnable           = (false),
            .compareOp               = VK_COMPARE_OP_NEVER,
            .minLod                  = (0.0f),
            .maxLod                  = (float)mipmap_levels,
            .borderColor             = VK_BORDER_COLOR_INT_OPAQUE_BLACK,
            .unnormalizedCoordinates = (false),
        };
        VkFormat   format = ToVkType(image.header.format);
        VkExtent3D ext { static_cast<rstd::uint32_t>(image_slot.width),
                         static_cast<rstd::uint32_t>(image_slot.height),
                         1 };

        if (auto opt = CreateImage(m_device,
                                   ext,
                                   static_cast<rstd::uint32_t>(mipmap_levels),
                                   format,
                                   sampler_info,
                                   VK_IMAGE_USAGE_TRANSFER_DST_BIT | VK_IMAGE_USAGE_SAMPLED_BIT);
            opt.is_some()) {
            image_paras = rstd::move(opt).unwrap();
            AssignImageGeneration(image_paras);
        } else {
            return rstd::None();
        }
    }
    return rstd::Some(rstd::sync::Arc<TextureAllocation>::make(rstd::move(img_slots)));
}

void TextureCache::allocateCmd() {
    const auto& pool = m_device.cmd_pool();
    VVK_CHECK(pool.Allocate(usize(1), VK_COMMAND_BUFFER_LEVEL_PRIMARY, m_tex_cmds));
    m_tex_cmd = vvk::CommandBuffer(m_tex_cmds[usize()], m_device.handle().Dispatch());
}

Option<VmaImageParameters> TextureCache::CreateTex(TextureKey tex_key) {
    VmaImageParameters image_paras;
    do {
        VkSamplerCreateInfo sam_info = GenSamplerInfo(tex_key);
        VkFormat            format   = ToVkType(tex_key.format);
        VkExtent3D          ext { static_cast<rstd::uint32_t>(tex_key.width.to_primitive()),
                                  static_cast<rstd::uint32_t>(tex_key.height.to_primitive()),
                                  1 };
        const bool depth_usage = (tex_key.usage & VK_IMAGE_USAGE_DEPTH_STENCIL_ATTACHMENT_BIT) != 0;
        const auto properties  = m_device.gpu().GetFormatProperties(format);
        VkFormatFeatureFlags required_features {};
        if ((tex_key.usage & VK_IMAGE_USAGE_SAMPLED_BIT) != 0)
            required_features |= VK_FORMAT_FEATURE_SAMPLED_IMAGE_BIT;
        if ((tex_key.usage & VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT) != 0)
            required_features |= VK_FORMAT_FEATURE_COLOR_ATTACHMENT_BIT;
        if (depth_usage) required_features |= VK_FORMAT_FEATURE_DEPTH_STENCIL_ATTACHMENT_BIT;
        if ((properties.optimalTilingFeatures & required_features) != required_features) {
            rstd_error("texture format {} does not support requested usage {:#x}",
                       static_cast<int>(format),
                       tex_key.usage);
            break;
        }

        if (auto opt = CreateImage(m_device,
                                   ext,
                                   tex_key.mipmap_level,
                                   format,
                                   sam_info,
                                   tex_key.usage,
                                   VMA_MEMORY_USAGE_GPU_ONLY,
                                   tex_key.samples);
            opt.is_some()) {
            image_paras = rstd::move(opt).unwrap();
            AssignImageGeneration(image_paras);
        } else
            break;

        // Single-sample images settle in SHADER_READ_ONLY (sampled by other
        // passes). MSAA twin is never sampled — pre-transition to
        // COLOR_ATTACHMENT_OPTIMAL so the first render pass with LoadOp=LOAD
        // doesn't see UNDEFINED on a non-DONT_CARE attachment.
        if (! m_tex_cmd) allocateCmd();
        TransImgLayout(m_device.graphics_queue().handle,
                       m_tex_cmd,
                       ToImageParameters(image_paras),
                       depth_usage ? VK_IMAGE_LAYOUT_DEPTH_STENCIL_ATTACHMENT_OPTIMAL
                       : tex_key.samples == VK_SAMPLE_COUNT_1_BIT
                           ? VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL
                           : VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL);

        VVK_CHECK_ACT(break, m_device.handle().WaitIdle());
        return Some(rstd::move(image_paras));
    } while (false);
    return None();
}

Option<rstd::sync::Arc<TextureAllocation>> TextureCache::AllocateTexture(TextureKey key) {
    auto image = CreateTex(rstd::move(key));
    if (image.is_none()) return None();
    ImageSlots slots;
    slots.slots.push_back(rstd::move(image).unwrap());
    return Some(rstd::sync::Arc<TextureAllocation>::make(rstd::move(slots)));
}

/* ===========================================================================
 * Video-tex pipeline
 *
 * When TexImageParser detects an MP4 / WebM container inlined in a
 * .tex body (header.type == ImageType::VIDEO), it doesn't decompress
 * pixels. It stores a typed read range in ImageData instead. CreateTex
 * routes those Images here:
 *
 *   1. We allocate a stable RGBA8 VkImage at (w,h), cleared to black,
 *      and register it in m_tex_map under the Image's key so the rest
 *      of the renderer (descriptor sets, sprite-anim fallback, etc.)
 *      sees an ordinary single-slot texture.
 *   2. Each decoder trial creates an independent reader over that range.
 *   3. We spin up a wavsen::video::VideoDecoder via open_from_stream.
 *      When hwdec is enabled, a lazily-created wavsen Producer supplies
 *      the hwdevice; otherwise the decoder stays sw-only. On Linux the
 *      avformat probe is happy with ftyp/EBML at the head of the stream.
 *   4. Each render tick PumpVideoTextures advances PTS, pulls the
 *      correct frame view for the active decoder kind, and writes the
 *      stable RGBA8 VkImage through wavsen::video::YuvToRgba. Hardware
 *      decode uses OWE's own VkDevice via Producer::from_external so
 *      conversion and material sampling stay on the same device.
 *
 * ========================================================================= */

namespace
{

class RangeInputStream {
public:
    explicit RangeInputStream(owe::io::ReadRange source)
        : m_length(static_cast<rstd::int64_t>(source.len())),
          m_reader(std::move(source).into_reader()) {}

    int read(rstd::uint8_t* buffer, int size) {
        if (size <= 0) return 0;
        auto result = m_reader.read(buffer, static_cast<std::size_t>(size));
        return result.is_ok() ? static_cast<int>(*result) : -1;
    }

    rstd::int64_t seek(rstd::int64_t offset, int whence) {
        constexpr int AVSEEK_SIZE = 0x10000;
        if (whence == AVSEEK_SIZE) return m_length;
        owe::io::SeekFrom from;
        switch (whence) {
        case 0:
            if (offset < 0) return -1;
            from = owe::io::SeekFrom::from_start(static_cast<std::uint64_t>(offset));
            break;
        case 1: from = owe::io::SeekFrom::from_current(offset); break;
        case 2: from = owe::io::SeekFrom::from_end(offset); break;
        default: return -1;
        }
        auto result = m_reader.seek(from);
        return result.is_ok() ? static_cast<rstd::int64_t>(*result) : -1;
    }

private:
    rstd::int64_t        m_length { 0 };
    owe::io::RangeReader m_reader;
};

wavsen::video::HwAccel ParseHwdec(std::string_view value) {
    if (value == "vulkan") return wavsen::video::HwAccel::Vulkan;
    if (value == "vaapi") return wavsen::video::HwAccel::Vaapi;
    if (value == "none") return wavsen::video::HwAccel::None;
    return wavsen::video::HwAccel::Auto;
}

const char* HwdecLabel(wavsen::video::HwAccel h) {
    switch (h) {
    case wavsen::video::HwAccel::Auto: return "auto";
    case wavsen::video::HwAccel::Vulkan: return "vulkan";
    case wavsen::video::HwAccel::Vaapi: return "vaapi";
    case wavsen::video::HwAccel::None: return "none";
    }
    return "?";
}

const char* FrameKindLabel(wavsen::video::FrameKind k) {
    switch (k) {
    case wavsen::video::FrameKind::Sw: return "sw";
    case wavsen::video::FrameKind::VulkanShared: return "vulkan-shared";
    case wavsen::video::FrameKind::VaapiDrm: return "vaapi-drm";
    }
    return "?";
}

rstd::vec::Vec<const char*> ExtensionPtrs(std::span<const std::string> names) {
    auto out = rstd::vec::Vec<const char*>::with_capacity(usize(names.size()));
    for (const auto& name : names) out.push(name.c_str());
    return out;
}

rstd::vec::Vec<wavsen::video::QueueFamily> QueueFamiliesForFfmpeg(const Device& device) {
    auto props = device.gpu().GetQueueFamilyProperties();
    auto out   = rstd::vec::Vec<wavsen::video::QueueFamily>::with_capacity(props.len());
    for (rstd::uint32_t i = 0; i < props.len().to_primitive(); ++i) {
        out.push(wavsen::video::QueueFamily {
            .index      = u32(i),
            .flags      = props[usize(i)].queueFlags,
            .video_caps = u32(),
        });
    }
    return out;
}

wavsen::video::Producer::ExternalDeviceInfo
MakeExternalProducerInfo(const Device& device, rstd::uint32_t width, rstd::uint32_t height) {
    return wavsen::video::Producer::ExternalDeviceInfo {
        .instance                    = device.instance_handle(),
        .physical_device             = *device.gpu(),
        .device                      = *device.handle(),
        .queue                       = *device.graphics_queue().handle,
        .queue_family_index          = u32(device.graphics_queue().family_index),
        .queue_families              = QueueFamiliesForFfmpeg(device),
        .enabled_instance_extensions = ExtensionPtrs(device.enabled_instance_extensions()),
        .enabled_device_extensions   = ExtensionPtrs(device.enabled_device_extensions()),
        .api_version                 = u32(device.instance_api_version()),
        .width                       = u32(width),
        .height                      = u32(height),
    };
}

void CloseSyncFd(int fd) {
    CloseExternalFileDescriptor(fd);
}

} // anonymous namespace

struct TextureCache::VideoRegistry {
    TextureCache::VideoDecodeOptions      options;
    Option<Box<wavsen::video::Producer>>  producer;
    Option<Box<wavsen::video::YuvToRgba>> yuv;
    rstd::uint32_t                        yuv_max_width { 0 };
    rstd::uint32_t                        yuv_max_height { 0 };

    struct Runtime final : TextureAllocationRuntime {
        VideoRegistry*                              registry { nullptr };
        const Device*                               device { nullptr };
        String                                      key;
        rstd::uint32_t                              width { 0 };
        rstd::uint32_t                              height { 0 };
        ImageParameters                             target;
        Option<rstd::sync::Arc<VideoPlaybackState>> playback;
        Option<Box<wavsen::video::VideoDecoder>>    decoder;
        wavsen::video::Nv12Frame                    nv12_scratch;
        f64                                         pts_acc {};
        f64                                         last_pts { -1.0 };
        bool                                        have_frame { false };
        u64                                         applied_seek_sequence {};
        bool                                        offline_clock_initialized { false };
        double                                      offline_anchor_scene { 0.0 };
        double                                      offline_anchor_media { 0.0 };
        VideoPlaybackSnapshot                       offline_control;
        double                                      offline_cycle { -1.0 };
        bool                                        offline_drained { false };
        Option<wavsen::video::Nv12Frame>            offline_pending;

        void Pump(double dt_seconds) override;
    };
    Vec<std::weak_ptr<TextureAllocationRuntime>> runtimes;
    struct ObservedRuntime {
        VideoDecoderObservation observation;
        std::weak_ptr<TextureAllocationRuntime> runtime;
    };
    // Keep the union of actual successful opens, including allocations that later expire.
    // Multiple material uses of one shared allocation do not create more records.
    std::vector<ObservedRuntime> observed_runtimes;
    std::uint64_t next_instance_id { 1 };
    std::uint64_t peak_active_instances { 0 };

    void Observe(std::uint64_t tick) {
        std::uint64_t active = 0;
        for (auto& entry : observed_runtimes) {
            entry.observation.active = !entry.runtime.expired();
            if (entry.observation.active) {
                ++active;
                entry.observation.last_observed_active_tick = tick;
            } else if (!entry.observation.first_observed_inactive_tick.has_value()) {
                entry.observation.first_observed_inactive_tick = tick;
            }
        }
        peak_active_instances = std::max(peak_active_instances, active);
    }

    const wavsen::video::Producer* ensureProducer(const Device& device, rstd::uint32_t width,
                                                  rstd::uint32_t height) {
        if (producer.is_some()) return producer->get();
        auto r =
            wavsen::video::Producer::from_external(MakeExternalProducerInfo(device, width, height));
        if (r.is_err()) {
            rstd_warn(
                "CreateVideoTex: shared-device producer unavailable; falling back to sw decode: {}",
                std::move(r).unwrap_err().message);
            return nullptr;
        }
        producer = rstd::Some(std::move(r).unwrap());
        return producer->get();
    }

    wavsen::video::YuvToRgba* ensureYuv(const Device& device, rstd::uint32_t width,
                                        rstd::uint32_t height) {
        if (yuv.is_some() && width <= yuv_max_width && height <= yuv_max_height) return yuv->get();
        auto next_w = std::max(width, yuv_max_width);
        auto next_h = std::max(height, yuv_max_height);
        auto r      = wavsen::video::YuvToRgba::create(device.instance_handle(),
                                                       *device.gpu(),
                                                       *device.handle(),
                                                       u32(device.graphics_queue().family_index),
                                                       *device.graphics_queue().handle,
                                                       u32(next_w),
                                                       u32(next_h));
        if (r.is_err()) {
            rstd_error("CreateVideoTex: YuvToRgba create failed: {}",
                       std::move(r).unwrap_err().message);
            return nullptr;
        }
        yuv            = rstd::Some(rstd::move(r).unwrap());
        yuv_max_width  = next_w;
        yuv_max_height = next_h;
        return yuv->get();
    }
};

Option<rstd::sync::Arc<TextureAllocation>>
TextureCache::CreateVideoTex(const Image&                                image,
                             Option<rstd::sync::Arc<VideoPlaybackState>> playback) {
    if (image.slots.empty() || image.slots[0].mipmaps.empty()) return rstd::None();
    auto& mip = image.slots[0].mipmaps[0];
    if (mip.video_source.is_none() || mip.width <= 0 || mip.height <= 0) {
        rstd_error("CreateVideoTex: incomplete video-tex slot for {}", image.key);
        return rstd::None();
    }

    if (m_video_registry.is_none()) {
        m_video_registry                 = Some(Box<VideoRegistry>::make());
        m_video_registry->get()->options = m_video_decode_options;
    }
    auto* registry = m_video_registry->get();
    if (! m_tex_cmd) allocateCmd();

    auto video_source = (*mip.video_source).clone();

    VideoRegistry::Runtime runtime;
    runtime.registry = registry;
    runtime.device   = &m_device;
    runtime.key      = String::make(rstd::cppstd::as_str(image.key).unwrap());
    runtime.playback = rstd::move(playback);
    /* NV12 chroma is 4:2:0 → both dims even. */
    const auto source_width  = static_cast<rstd::uint32_t>(mip.width);
    const auto source_height = static_cast<rstd::uint32_t>(mip.height);
    runtime.width            = source_width | (source_width & 1u);
    runtime.height           = source_height | (source_height & 1u);
    if (runtime.width != source_width) runtime.width = source_width + 1u;
    if (runtime.height != source_height) {
        runtime.height = source_height + 1u;
    }

    /* 1) Allocate the stable RGBA8 target. */
    VkSamplerCreateInfo sampler_info {
        .sType                   = VK_STRUCTURE_TYPE_SAMPLER_CREATE_INFO,
        .pNext                   = nullptr,
        .magFilter               = ToVkType(image.header.sample.magFilter),
        .minFilter               = ToVkType(image.header.sample.minFilter),
        .mipmapMode              = VK_SAMPLER_MIPMAP_MODE_LINEAR,
        .addressModeU            = ToVkType(image.header.sample.wrapS),
        .addressModeV            = ToVkType(image.header.sample.wrapS),
        .addressModeW            = ToVkType(image.header.sample.wrapT),
        .anisotropyEnable        = false,
        .maxAnisotropy           = 1.0f,
        .compareEnable           = false,
        .compareOp               = VK_COMPARE_OP_NEVER,
        .minLod                  = 0.0f,
        .maxLod                  = 1.0f,
        .borderColor             = VK_BORDER_COLOR_INT_OPAQUE_BLACK,
        .unnormalizedCoordinates = false,
    };
    VkExtent3D ext { runtime.width, runtime.height, 1 };
    auto       img_opt = CreateImage(m_device,
                                     ext,
                                     /*miplevel=*/1u,
                                     VK_FORMAT_R8G8B8A8_UNORM,
                                     sampler_info,
                                     VK_IMAGE_USAGE_TRANSFER_DST_BIT | VK_IMAGE_USAGE_STORAGE_BIT |
                                         VK_IMAGE_USAGE_SAMPLED_BIT);
    if (! img_opt) {
        rstd_error("CreateVideoTex: VkImage allocation failed for {}", image.key);
        return rstd::None();
    }
    auto target_image = std::move(*img_opt);
    AssignImageGeneration(target_image);
    runtime.target = ToImageParameters(target_image);

    if (! registry->ensureYuv(m_device, runtime.width, runtime.height)) return None();

    /* 2) Initial layout: UNDEFINED → TRANSFER_DST → clear black →
     * SHADER_READ_ONLY. Mirrors the one-shot pattern used by the
     * existing TransImgLayout / CopyImageData helpers in this file. */
    {
        ImageParameters ip = runtime.target;
        VVK_CHECK(m_tex_cmd.Begin(VkCommandBufferBeginInfo {
            .sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO,
            .pNext = nullptr,
            .flags = VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT,
        }));
        VkImageSubresourceRange range {
            .aspectMask     = VK_IMAGE_ASPECT_COLOR_BIT,
            .baseMipLevel   = 0,
            .levelCount     = 1,
            .baseArrayLayer = 0,
            .layerCount     = 1,
        };
        VkImageMemoryBarrier to_xfer {
            .sType            = VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER,
            .srcAccessMask    = 0,
            .dstAccessMask    = VK_ACCESS_TRANSFER_WRITE_BIT,
            .oldLayout        = VK_IMAGE_LAYOUT_UNDEFINED,
            .newLayout        = VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL,
            .image            = ip.handle,
            .subresourceRange = range,
        };
        m_tex_cmd.PipelineBarrier(
            VK_PIPELINE_STAGE_TOP_OF_PIPE_BIT, VK_PIPELINE_STAGE_TRANSFER_BIT, 0, to_xfer);
        VkClearColorValue clear { .float32 = { 0.0f, 0.0f, 0.0f, 1.0f } };
        m_tex_cmd.ClearColorImage(ip.handle, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, &clear, range);
        VkImageMemoryBarrier to_shader {
            .sType            = VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER,
            .srcAccessMask    = VK_ACCESS_TRANSFER_WRITE_BIT,
            .dstAccessMask    = VK_ACCESS_SHADER_READ_BIT,
            .oldLayout        = VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL,
            .newLayout        = VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL,
            .image            = ip.handle,
            .subresourceRange = range,
        };
        m_tex_cmd.PipelineBarrier(
            VK_PIPELINE_STAGE_TRANSFER_BIT, VK_PIPELINE_STAGE_FRAGMENT_SHADER_BIT, 0, to_shader);
        VVK_CHECK(m_tex_cmd.End());
        VkSubmitInfo si {
            .sType              = VK_STRUCTURE_TYPE_SUBMIT_INFO,
            .commandBufferCount = 1,
            .pCommandBuffers    = m_tex_cmd.address(),
        };
        VVK_CHECK(m_device.graphics_queue().handle.Submit(si));
        VVK_CHECK(m_device.handle().WaitIdle());
    }

    /* 3) Open the decoder. Each backend trial gets its own range cursor. */
    auto factory =
        rstd::boxed::Box<dyn<FnMut<rstd::boxed::Box<dyn<wavsen::video::InputStream>>()>>>::make(
            [source =
                 rstd::move(video_source)]() -> rstd::boxed::Box<dyn<wavsen::video::InputStream>> {
                return rstd::boxed::Box<dyn<wavsen::video::InputStream>>::make(
                    RangeInputStream(source.clone()));
            });
    const auto              requested_hwdec = ParseHwdec(registry->options.hwdec);
    wavsen::video::OpenOpts opts {
        requested_hwdec,
        String::make(rstd::cppstd::as_str(registry->options.render_node).unwrap()),
    };
    const wavsen::video::Producer* producer = nullptr;
    if (requested_hwdec != wavsen::video::HwAccel::None) {
        producer = registry->ensureProducer(m_device, runtime.width, runtime.height);
        if (! producer) opts.hwaccel = wavsen::video::HwAccel::None;
    }
    auto dec_r = wavsen::video::VideoDecoder::open_from_stream(std::move(factory),
                                                               u32(runtime.width),
                                                               u32(runtime.height),
                                                               /*loop=*/true,
                                                               producer,
                                                               opts);
    if (dec_r.is_err()) {
        rstd_error("CreateVideoTex: open_from_stream failed for {}: {}",
                   image.key,
                   dec_r.unwrap_err().message);
        return None();
    }
    runtime.decoder = rstd::Some(std::move(dec_r).unwrap());
    if (runtime.playback.is_some()) {
        (*runtime.playback)->PublishTime((*runtime.playback)->CurrentTime(),
                                         (*runtime.decoder)->duration());
    }
    rstd_info("CreateVideoTex: {} hwdec={} decoder kind={}",
              image.key,
              HwdecLabel(requested_hwdec),
              FrameKindLabel((*runtime.decoder)->kind()));

    auto metadata = (*runtime.decoder)->stream_metadata();
    if (runtime.playback.is_some()) {
        (*runtime.playback)->PublishPeriodMetadata(VideoPlaybackPeriodMetadata {
            .duration_ticks = metadata.duration_ticks,
            .time_base_num  = metadata.time_base_num,
            .time_base_den  = metadata.time_base_den,
            .frame_count    = metadata.frame_count,
            .loops          = true, // open_from_stream above enables EOF seek-to-zero.
        });
    }
    VideoDecoderObservation observation;
    observation.resource_key = image.key;
    observation.instance_id = registry->next_instance_id++;
    observation.codec = rstd::cppstd::to_string(metadata.codec.as_str());
    if (metadata.coded_width.is_some()) observation.coded_width = metadata.coded_width->to_primitive();
    if (metadata.coded_height.is_some()) observation.coded_height = metadata.coded_height->to_primitive();
    observation.pixel_format = rstd::cppstd::to_string(metadata.pixel_format.as_str());
    if (metadata.fps_num.is_some()) observation.fps_num = metadata.fps_num->to_primitive();
    if (metadata.fps_den.is_some()) observation.fps_den = metadata.fps_den->to_primitive();
    observation.fps_source = rstd::cppstd::to_string(metadata.fps_source.as_str());
    observation.decoder_kind = FrameKindLabel((*runtime.decoder)->kind());
    observation.metadata_unknown = observation.codec.empty() || observation.codec == "unknown_codec" ||
        !observation.coded_width.has_value() || !observation.coded_height.has_value() ||
        observation.pixel_format.empty() || !observation.fps_num.has_value() || !observation.fps_den.has_value();
    observation.opened_at_tick = m_video_observation_tick;
    observation.last_observed_active_tick = m_video_observation_tick;

    ImageSlots img_slots {};
    img_slots.slots.resize(1);
    img_slots.slots[0] = std::move(target_image);
    std::shared_ptr<TextureAllocationRuntime> runtime_owner =
        std::make_shared<VideoRegistry::Runtime>(rstd::move(runtime));
    auto allocation = rstd::sync::Arc<TextureAllocation>::make(rstd::move(img_slots), runtime_owner);
    registry->runtimes.push(std::weak_ptr<TextureAllocationRuntime>(runtime_owner));
    registry->observed_runtimes.push_back(VideoRegistry::ObservedRuntime {
        .observation = std::move(observation), .runtime = runtime_owner,
    });
    registry->Observe(m_video_observation_tick);
    return Some(rstd::move(allocation));
}

void TextureCache::VideoRegistry::Runtime::Pump(double dt_seconds) {
    if (registry == nullptr || device == nullptr || decoder.is_none()) return;
    auto& s            = *this;
    auto  publish_time = [&] {
        if (s.playback.is_some()) {
            (*s.playback)->PublishTime(s.pts_acc, (*s.decoder)->duration());
        }
    };
    const bool offline = active_offline_execution != nullptr;
    if (!offline && s.playback.is_some()) {
        auto state = (*s.playback)->Snapshot();
        if (state.seek_sequence != s.applied_seek_sequence) {
            auto seeked             = (*s.decoder)->seek(state.seek_seconds);
            s.applied_seek_sequence = state.seek_sequence;
            if (seeked.is_err()) {
                rstd_error("PumpVideoTextures[{}]: seek: {}",
                           s.key.as_str(),
                           rstd::move(seeked).unwrap_err().message);
            } else {
                s.pts_acc    = state.seek_seconds;
                s.last_pts   = f64(-1.0);
                s.have_frame = false;
            }
        }
        if (! state.playing) {
            publish_time();
            return;
        }
        dt_seconds *= state.rate.to_primitive();
    }
    if (!offline) s.pts_acc += f64(dt_seconds);
    auto* yuv = registry->ensureYuv(*device, s.width, s.height);
    if (! yuv) {
        publish_time();
        return;
    }

    ImageParameters ip = s.target;

    const auto                             fkind = (*s.decoder)->kind();
    Option<wavsen::video::VkFrameLease>    vulkan_frame;
    Option<wavsen::video::VaapiFrameLease> vaapi_frame;

    bool got_new = false;
    if (offline) {
        auto fail = [&](const std::string& message) {
            active_offline_execution->diagnose(
                "video[" + rstd::cppstd::to_string(s.key.as_str()) + "]: " + message, true);
            rstd_error("PumpVideoTextures[{}]: {}", s.key.as_str(), message);
        };
        if (fkind != wavsen::video::FrameKind::Sw) {
            fail("offline timestamp selection requires the configured software decoder");
            return;
        }
        const double now = active_offline_execution->elapsed;
        const bool first = !s.offline_clock_initialized;
        auto control = s.playback.is_some() ? (*s.playback)->Snapshot() : VideoPlaybackSnapshot {};
        const bool seek_changed = control.seek_sequence != s.applied_seek_sequence;
        double media_time;
        if (s.playback.is_some()) {
            media_time = (*s.playback)->AdvanceOffline(f64(now)).to_primitive();
        } else {
            media_time = first ? control.seek_seconds.to_primitive() :
                s.offline_anchor_media + (s.offline_control.playing ?
                    (now - s.offline_anchor_scene) * s.offline_control.rate.to_primitive() : 0.0);
            if (seek_changed) media_time = control.seek_seconds.to_primitive();
            if (first || seek_changed || control.playing != s.offline_control.playing ||
                control.rate != s.offline_control.rate) {
                s.offline_anchor_scene = now;
                s.offline_anchor_media = media_time;
            }
            s.offline_control = control;
        }
        s.offline_clock_initialized = true;
        s.applied_seek_sequence = control.seek_sequence;
        const auto duration = (*s.decoder)->duration();
        if (duration.is_none() || !duration->is_finite() || *duration <= f64() ||
            !std::isfinite(media_time) || media_time < 0.0) {
            fail("offline video sampling requires a finite timestamp and positive video duration");
            return;
        }
        const double duration_s = duration->to_primitive();
        double quotient = media_time / duration_s;
        const double nearest = std::round(quotient);
        if (std::abs(quotient - nearest) <= 4.0 * std::numeric_limits<double>::epsilon() *
            std::max(1.0, std::abs(quotient))) quotient = nearest;
        const double cycle = std::floor(quotient);
        const double cycle_start = cycle * duration_s;
        s.pts_acc = f64(std::max(0.0, media_time - cycle_start));
        if (first || seek_changed || cycle != s.offline_cycle) {
            auto seeked = (*s.decoder)->seek(s.pts_acc);
            if (seeked.is_err()) {
                fail(rstd::cppstd::to_string(seeked.unwrap_err().message.as_str()));
                return;
            }
            s.offline_pending = None();
            s.offline_drained = false;
            s.offline_cycle = cycle;
            s.have_frame = false;
            s.last_pts = f64(-1.0);
        }
        // Retain one future frame as lookahead. Only a frame whose PTS has
        // arrived may replace the displayed image; output FPS does not change
        // the source's sampling rate. Offline catch-up must not drop work.
        while (!s.offline_drained) {
            if (s.offline_pending.is_none()) {
                wavsen::video::Nv12Frame candidate;
                auto pulled = (*s.decoder)->next_frame(candidate);
                if (pulled.is_err()) {
                    fail(rstd::cppstd::to_string(pulled.unwrap_err().message.as_str()));
                    return;
                }
                if (*pulled != wavsen::video::NextFrame::Ok) {
                    // The decoder's eager loop has consumed the next cycle's
                    // first frame. Seek again only when scene time wraps.
                    s.offline_drained = true;
                    break;
                }
                if (!candidate.pts_seconds.is_finite() || candidate.pts_seconds < f64()) {
                    fail("decoded video frame has no usable presentation timestamp");
                    return;
                }
                s.offline_pending = Some(rstd::move(candidate));
            }
            const double deadline = cycle_start + s.offline_pending->pts_seconds.to_primitive();
            const double tolerance = 4.0 * std::numeric_limits<double>::epsilon() *
                std::max(1.0, std::max(std::abs(deadline), std::abs(media_time)));
            if (deadline - media_time > tolerance) break;
            s.nv12_scratch = rstd::move(*s.offline_pending);
            s.offline_pending = None();
            s.last_pts = s.nv12_scratch.pts_seconds;
            got_new = true;
        }
    } else {
    /* Realtime pacing retains its bounded catch-up policy. */
    for (int i = 0; i < 4; ++i) {
        if (s.last_pts >= f64() && s.last_pts > s.pts_acc) break;

        rstd::Result<wavsen::video::NextFrame, wavsen::video::Error> r =
            rstd::Ok(wavsen::video::NextFrame::Ok);
        switch (fkind) {
        case wavsen::video::FrameKind::VulkanShared: {
            auto pulled = (*s.decoder)->next_vk_frame();
            if (pulled.is_err()) {
                r = Err(rstd::move(pulled).unwrap_err());
            } else {
                auto value = rstd::move(pulled).unwrap();
                r          = Ok(value.status);
                if (value.frame.is_some()) vulkan_frame = rstd::move(value.frame);
            }
            break;
        }
        case wavsen::video::FrameKind::VaapiDrm: {
            auto pulled = (*s.decoder)->next_vaapi_frame();
            if (pulled.is_err()) {
                r = Err(rstd::move(pulled).unwrap_err());
            } else {
                auto value  = rstd::move(pulled).unwrap();
                r           = Ok(value.status);
                vaapi_frame = rstd::move(value.frame);
            }
            break;
        }
        case wavsen::video::FrameKind::Sw: r = (*s.decoder)->next_frame(s.nv12_scratch); break;
        }
        if (r.is_err()) {
            rstd_error("PumpVideoTextures[{}]: decode {}: {}",
                       s.key.as_str(),
                       FrameKindLabel(fkind),
                       std::move(r).unwrap_err().message);
            break;
        }
        auto kind = r.unwrap();
        if (kind == wavsen::video::NextFrame::Eof) {
            s.pts_acc  = f64();
            s.last_pts = f64(-1.0);
            break;
        }
        const bool decoder_looped = kind == wavsen::video::NextFrame::Looped;
        f64        frame_pts { -1.0 };
        switch (fkind) {
        case wavsen::video::FrameKind::VulkanShared:
            if (vulkan_frame.is_none()) {
                rstd_error("PumpVideoTextures[{}]: Vulkan decode returned no frame lease",
                           s.key.as_str());
                publish_time();
                return;
            }
            frame_pts = vulkan_frame->info().pts_seconds;
            break;
        case wavsen::video::FrameKind::VaapiDrm:
            if (vaapi_frame.is_none()) {
                rstd_error("PumpVideoTextures[{}]: VAAPI decode returned no surface lease",
                           s.key.as_str());
                publish_time();
                return;
            }
            frame_pts = vaapi_frame->view().pts_seconds;
            break;
        case wavsen::video::FrameKind::Sw: frame_pts = s.nv12_scratch.pts_seconds; break;
        }
        if (decoder_looped) s.pts_acc = frame_pts.max(f64());
        s.last_pts = frame_pts;
        got_new    = true;
        if (decoder_looped) break;
    }
    }
    if (! got_new && s.have_frame) {
        publish_time();
        return;
    }
    if (! got_new) {
        publish_time();
        return;
    }

    u32 cs_id;
    u32 cr_id;
    switch (fkind) {
    case wavsen::video::FrameKind::VulkanShared:
        cs_id = vulkan_frame->info().colorspace;
        cr_id = vulkan_frame->info().color_range;
        break;
    case wavsen::video::FrameKind::VaapiDrm:
        cs_id = vaapi_frame->view().colorspace;
        cr_id = vaapi_frame->view().color_range;
        break;
    case wavsen::video::FrameKind::Sw:
        cs_id = s.nv12_scratch.colorspace;
        cr_id = s.nv12_scratch.color_range;
        break;
    }
    const auto color_matrix = wavsen::video::make_color_matrix(
        static_cast<wavsen::video::ColorSpace>(cs_id.to_primitive()),
        static_cast<wavsen::video::ColorRange>(cr_id.to_primitive()));

    rstd::Result<int, wavsen::video::Error> cv = rstd::Ok(-1);
    switch (fkind) {
    case wavsen::video::FrameKind::VulkanShared: {
        auto reserved = yuv->reserve({
            .target = {
                .image  = ip.handle,
                .view   = ip.view,
                .width  = u32(s.width),
                .height = u32(s.height),
                .kind   = wavsen::video::ConvertTarget::SampledLocal,
            },
        });
        if (reserved.is_err()) {
            cv = Err(rstd::move(reserved).unwrap_err());
            break;
        }
        auto reservation = rstd::move(reserved).unwrap();
        if (reservation.is_none()) {
            publish_time();
            return;
        }
        auto submitted = yuv->submit_av_vk_frame(
            rstd::move(*reservation), rstd::move(*vulkan_frame), color_matrix);
        if (submitted.is_err()) {
            cv = Err(rstd::move(submitted).unwrap_err());
        } else {
            cv = Ok(rstd::move(submitted).unwrap().sync_fd);
        }
        break;
    }
    case wavsen::video::FrameKind::VaapiDrm: {
        auto reserved = yuv->reserve({
            .target = {
                .image  = ip.handle,
                .view   = ip.view,
                .width  = u32(s.width),
                .height = u32(s.height),
                .kind   = wavsen::video::ConvertTarget::SampledLocal,
            },
        });
        if (reserved.is_err()) {
            cv = Err(rstd::move(reserved).unwrap_err());
            break;
        }
        auto reservation = rstd::move(reserved).unwrap();
        if (reservation.is_none()) {
            publish_time();
            return;
        }
        auto mapped = rstd::move(*vaapi_frame).into_drm();
        if (mapped.is_err()) {
            cv = Err(rstd::move(mapped).unwrap_err());
            break;
        }
        auto submitted = yuv->submit_drm_prime(
            rstd::move(*reservation), rstd::move(mapped).unwrap(), color_matrix);
        if (submitted.is_err()) {
            cv = Err(rstd::move(submitted).unwrap_err());
            break;
        }
        auto value = rstd::move(submitted).unwrap();
        if (value.is_none()) {
            publish_time();
            return;
        }
        cv = Ok(value->sync_fd);
        break;
    }
    case wavsen::video::FrameKind::Sw:
        cv = yuv->convert_nv12(ip.handle,
                               u32(s.width),
                               u32(s.height),
                               s.nv12_scratch.data.data(),
                               s.nv12_scratch.data.len(),
                               color_matrix,
                               wavsen::video::ConvertTarget::SampledLocal);
        break;
    }
    if (cv.is_err()) {
        rstd_error("PumpVideoTextures[{}]: yuv conversion {}: {}",
                   s.key.as_str(),
                   FrameKindLabel(fkind),
                   std::move(cv).unwrap_err().message);
        publish_time();
        return;
    }
    CloseSyncFd(std::move(cv).unwrap());
    s.have_frame = true;
    publish_time();
}

void TextureCache::PumpVideoTextures(double dt_seconds) {
    ++m_video_observation_tick;
    if (m_video_registry.is_none()) return;
    auto* registry = m_video_registry->get();
    registry->Observe(m_video_observation_tick);
    registry->runtimes.retain([](const std::weak_ptr<TextureAllocationRuntime>& runtime) {
        return ! runtime.expired();
    });
    for (const auto& weak : registry->runtimes) {
        auto runtime = weak.lock();
        if (runtime) runtime->Pump(dt_seconds);
    }
}

VideoDecoderInventory TextureCache::ObserveVideoDecoders() {
    VideoDecoderInventory inventory;
    inventory.observed = true;
    inventory.observed_through_tick = m_video_observation_tick;
    if (m_video_registry.is_none()) return inventory;
    auto* registry = m_video_registry->get();
    registry->Observe(m_video_observation_tick);
    inventory.peak_active_instances = registry->peak_active_instances;
    inventory.decoders.reserve(registry->observed_runtimes.size());
    for (const auto& entry : registry->observed_runtimes) inventory.decoders.push_back(entry.observation);
    return inventory;
}

bool TextureCache::UploadFontAtlasRegion(ref<TextureAllocation> texture, const rstd::uint8_t* atlas,
                                         rstd::uint32_t atlas_w, rstd::uint32_t x, rstd::uint32_t y,
                                         rstd::uint32_t w, rstd::uint32_t h) {
    if (w == 0 || h == 0) return true;
    auto view = texture->View();
    if (view.slots.empty()) return false;
    ImageParameters ip = view.getActive();

    // Tightly-packed staging buffer for the AABB. Allocating per-call keeps
    // this code path independent of the video-tex ring; atlas pumps are
    // small (a handful of glyphs per frame) so cost is negligible.
    const VkDeviceSize  bytes = static_cast<VkDeviceSize>(w) * h;
    VmaBufferParameters stage;
    if (! CreateStagingBuffer(m_device.vma_allocator(), bytes, stage)) return false;

    {
        void* v = nullptr;
        VVK_CHECK(stage.handle.MapMemory(&v));
        auto* dst = static_cast<rstd::uint8_t*>(v);
        for (rstd::uint32_t row = 0; row < h; ++row) {
            std::memcpy(dst + row * w, atlas + (y + row) * atlas_w + x, static_cast<size_t>(w));
        }
        stage.handle.UnMapMemory();
    }

    if (! m_tex_cmd) allocateCmd();
    VVK_CHECK(m_tex_cmd.Begin(VkCommandBufferBeginInfo {
        .sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO,
        .pNext = nullptr,
        .flags = VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT,
    }));
    VkImageSubresourceRange range {
        .aspectMask     = VK_IMAGE_ASPECT_COLOR_BIT,
        .baseMipLevel   = 0,
        .levelCount     = 1,
        .baseArrayLayer = 0,
        .layerCount     = 1,
    };
    VkImageMemoryBarrier to_xfer {
        .sType            = VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER,
        .srcAccessMask    = VK_ACCESS_SHADER_READ_BIT,
        .dstAccessMask    = VK_ACCESS_TRANSFER_WRITE_BIT,
        .oldLayout        = VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL,
        .newLayout        = VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL,
        .image            = ip.handle,
        .subresourceRange = range,
    };
    m_tex_cmd.PipelineBarrier(
        VK_PIPELINE_STAGE_FRAGMENT_SHADER_BIT, VK_PIPELINE_STAGE_TRANSFER_BIT, 0, to_xfer);
    VkBufferImageCopy region {};
    region.imageSubresource.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
    region.imageSubresource.layerCount = 1;
    region.imageOffset =
        VkOffset3D { static_cast<rstd::int32_t>(x), static_cast<rstd::int32_t>(y), 0 };
    region.imageExtent = VkExtent3D { w, h, 1 };
    m_tex_cmd.CopyBufferToImage(
        *stage.handle, ip.handle, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, region);
    VkImageMemoryBarrier to_shader {
        .sType            = VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER,
        .srcAccessMask    = VK_ACCESS_TRANSFER_WRITE_BIT,
        .dstAccessMask    = VK_ACCESS_SHADER_READ_BIT,
        .oldLayout        = VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL,
        .newLayout        = VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL,
        .image            = ip.handle,
        .subresourceRange = range,
    };
    m_tex_cmd.PipelineBarrier(
        VK_PIPELINE_STAGE_TRANSFER_BIT, VK_PIPELINE_STAGE_FRAGMENT_SHADER_BIT, 0, to_shader);
    VVK_CHECK(m_tex_cmd.End());
    VkSubmitInfo si {
        .sType              = VK_STRUCTURE_TYPE_SUBMIT_INFO,
        .commandBufferCount = 1,
        .pCommandBuffers    = m_tex_cmd.address(),
    };
    VVK_CHECK(m_device.graphics_queue().handle.Submit(si));
    VVK_CHECK(m_device.handle().WaitIdle());
    return true;
}

TextureCache::TextureCache(const Device& device): m_device(device) {}

TextureCache::~TextureCache() {
    if (m_video_registry.is_none() || m_video_registry->get()->yuv.is_none()) return;
    auto* yuv = m_video_registry->get()->yuv->get();
    if (auto drained = yuv->drain_submissions(u64(1'000'000'000)); drained.is_err()) {
        rstd_warn("TextureCache: conversion drain failed during shutdown: {}",
                  rstd::move(drained).unwrap_err().message);
        (void)m_device.handle().WaitIdle();
        (void)yuv->reclaim_submissions();
    }
}

u64 TextureCache::nextImageGeneration() { return m_next_image_generation++; }

void TextureCache::AssignImageGeneration(VmaImageParameters& image) {
    image.generation = nextImageGeneration();
}

void TextureCache::AssignImageGeneration(ExImageParameters& image) {
    image.generation = nextImageGeneration();
}

void TextureCache::SetVideoDecodeOptions(VideoDecodeOptions options) {
    m_video_decode_options = std::move(options);
    if (m_video_registry.is_some()) {
        auto* registry    = m_video_registry->get();
        registry->options = m_video_decode_options;
        registry->runtimes.retain(
            [](const std::weak_ptr<TextureAllocationRuntime>& runtime) {
                return ! runtime.expired();
            });
        if (registry->runtimes.is_empty()) (void)registry->producer.take();
    }
}

void TextureCache::Clear() {
    if (m_video_registry.is_some()) m_video_registry->get()->runtimes.clear();
}

void owe::vulkan::RecordGenerateMipmaps(vvk::CommandBuffer& cmd, const ImageParameters& image) {
    VkImageMemoryBarrier barrier {
        .sType               = VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER,
        .pNext               = nullptr,
        .srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
        .dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
        .image               = image.handle,
        .subresourceRange =
            VkImageSubresourceRange {
                .aspectMask     = VK_IMAGE_ASPECT_COLOR_BIT,
                .baseMipLevel   = 0,
                .levelCount     = 1,
                .baseArrayLayer = 0,
                .layerCount     = 1,
            },
    };
    /*
    cmd.PipelineBarrier(VK_PIPELINE_STAGE_TRANSFER_BIT,
                        VK_PIPELINE_STAGE_FRAGMENT_SHADER_BIT,
                        VK_DEPENDENCY_BY_REGION_BIT,
                        out_bar);
        */

    rstd::int32_t mipWidth  = static_cast<rstd::int32_t>(image.extent.width);
    rstd::int32_t mipHeight = static_cast<rstd::int32_t>(image.extent.height);

    for (unsigned i = 1; i < image.mipmap_level; i++) {
        barrier.subresourceRange.baseMipLevel = i - 1;
        barrier.oldLayout                     = i == 1 ? VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL
                                                       : VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL;

        barrier.newLayout     = VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL;
        barrier.srcAccessMask = i == 1 ? VK_ACCESS_SHADER_READ_BIT : VK_ACCESS_TRANSFER_WRITE_BIT;
        barrier.dstAccessMask = VK_ACCESS_TRANSFER_READ_BIT;

        VkPipelineStageFlags src_stage =
            i == 1 ? VK_PIPELINE_STAGE_FRAGMENT_SHADER_BIT : VK_PIPELINE_STAGE_TRANSFER_BIT;
        cmd.PipelineBarrier(
            src_stage, VK_PIPELINE_STAGE_TRANSFER_BIT, VK_DEPENDENCY_BY_REGION_BIT, barrier);

        barrier.subresourceRange.baseMipLevel = i;
        barrier.oldLayout                     = VK_IMAGE_LAYOUT_UNDEFINED;
        barrier.newLayout                     = VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL;
        barrier.srcAccessMask                 = 0;
        barrier.dstAccessMask                 = VK_ACCESS_TRANSFER_WRITE_BIT;

        cmd.PipelineBarrier(VK_PIPELINE_STAGE_TOP_OF_PIPE_BIT,
                            VK_PIPELINE_STAGE_TRANSFER_BIT,
                            VK_DEPENDENCY_BY_REGION_BIT,
                            barrier);

        VkImageBlit blit {
            .srcSubresource =
                VkImageSubresourceLayers {
                    .aspectMask     = VK_IMAGE_ASPECT_COLOR_BIT,
                    .mipLevel       = i - 1,
                    .baseArrayLayer = 0,
                    .layerCount     = 1,
                },
            .srcOffsets = { VkOffset3D { 0, 0, 0 }, VkOffset3D { mipWidth, mipHeight, 1 } },
            .dstOffsets = { VkOffset3D { 0, 0, 0 },
                            VkOffset3D { mipWidth > 1 ? mipWidth / 2 : 1,
                                         mipHeight > 1 ? mipHeight / 2 : 1,
                                         1 } },
        };
        blit.dstSubresource =
            VkImageSubresourceLayers {
                .aspectMask     = blit.srcSubresource.aspectMask,
                .mipLevel       = blit.srcSubresource.mipLevel + 1,
                .baseArrayLayer = 0,
                .layerCount     = 1,
            },

        cmd.BlitImage(image.handle,
                      VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL,
                      image.handle,
                      VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL,
                      blit,
                      VK_FILTER_LINEAR);

        barrier.subresourceRange.baseMipLevel = i - 1;
        barrier.oldLayout                     = VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL;
        barrier.newLayout                     = VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL;
        barrier.srcAccessMask                 = VK_ACCESS_TRANSFER_READ_BIT;
        barrier.dstAccessMask                 = VK_ACCESS_SHADER_READ_BIT;

        cmd.PipelineBarrier(VK_PIPELINE_STAGE_TRANSFER_BIT,
                            VK_PIPELINE_STAGE_FRAGMENT_SHADER_BIT,
                            VK_DEPENDENCY_BY_REGION_BIT,
                            barrier);

        if (mipWidth > 1) mipWidth /= 2;
        if (mipHeight > 1) mipHeight /= 2;
    }

    barrier.subresourceRange.baseMipLevel = image.mipmap_level - 1;
    barrier.oldLayout                     = VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL;
    barrier.newLayout                     = VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL;
    barrier.srcAccessMask                 = VK_ACCESS_TRANSFER_WRITE_BIT;
    barrier.dstAccessMask                 = VK_ACCESS_SHADER_READ_BIT;

    cmd.PipelineBarrier(VK_PIPELINE_STAGE_TRANSFER_BIT,
                        VK_PIPELINE_STAGE_FRAGMENT_SHADER_BIT,
                        VK_DEPENDENCY_BY_REGION_BIT,
                        barrier);
}
