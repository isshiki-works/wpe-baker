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
import owe.media;

using namespace owe;
using namespace owe::vulkan;
using namespace rstd::prelude;

// ---- R4 过渡 ------------------------------------------------------------------
// 视频段（TextureCache::VideoRegistry 与 PumpVideoTextures）R4 不动，仍按旧名
// active_offline_execution 读取离线作业。注入的 Services 只在一次 PumpVideoTextures
// 调用期间挂到这个文件内指针上；视频段改为直接接收 Services* 后删掉指针和这个包装。
namespace owe::vulkan
{
namespace
{
Services* active_offline_execution = nullptr;
bool      active_offline_raster = true;
}

void TextureCache::PumpVideoTextures(double dt_seconds, Services* services, bool raster) {
    active_offline_execution = services;
    active_offline_raster = raster;
    PumpVideoTextures(dt_seconds);
    active_offline_execution = nullptr;
    active_offline_raster = true;
}
} // namespace owe::vulkan

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
    case TextureFormat::RGBA16F: return VK_FORMAT_R16G16B16A16_SFLOAT;
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
 *   2. We open an owe::media::VideoSource (software decode) over that
 *      range. The hardware decode pipeline was not ported: hwdec≠none
 *      decodes in software too (the offline renderer forces none).
 *   3. Each render tick PumpVideoTextures advances PTS, pulls the NV12
 *      frame due, and writes the stable RGBA8 VkImage through
 *      owe::media::Nv12ToRgba.
 *
 * ========================================================================= */

namespace
{

template<typename T, typename U>
auto ToOption(const std::optional<U>& value) -> Option<T> {
    if (! value) return None();
    return Some(T(*value));
}

} // anonymous namespace

struct TextureCache::VideoRegistry {
    TextureCache::VideoDecodeOptions        options;
    std::unique_ptr<owe::media::Nv12ToRgba> yuv;
    rstd::uint32_t                          yuv_max_width { 0 };
    rstd::uint32_t                          yuv_max_height { 0 };

    struct Runtime final : TextureAllocationRuntime {
        VideoRegistry*                              registry { nullptr };
        const Device*                               device { nullptr };
        String                                      key;
        rstd::uint32_t                              width { 0 };
        rstd::uint32_t                              height { 0 };
        ImageParameters                             target;
        Option<rstd::sync::Arc<VideoPlaybackState>> playback;
        owe::media::VideoSource                     decoder;
        owe::media::Nv12Frame                       nv12_scratch;
        f64                                         pts_acc {};
        f64                                         last_pts { -1.0 };
        bool                                        convert_pending { false }; // nv12_scratch 里有还没写进纹理的新帧
        u64                                         applied_seek_sequence {};
        bool                                        offline_clock_initialized { false };
        double                                      offline_anchor_scene { 0.0 };
        double                                      offline_anchor_media { 0.0 };
        VideoPlaybackSnapshot                       offline_control;
        double                                      offline_cycle { -1.0 };
        bool                                        offline_drained { false };
        Option<owe::media::Nv12Frame>               offline_pending;

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

    // 按见过的最大视频尺寸建转换器，更大的视频来了就重建（建失败时保留旧的）。
    owe::media::Nv12ToRgba* ensureYuv(const Device& device, rstd::uint32_t width,
                                      rstd::uint32_t height) {
        if (yuv && width <= yuv_max_width && height <= yuv_max_height) return yuv.get();
        auto next_w = std::max(width, yuv_max_width);
        auto next_h = std::max(height, yuv_max_height);
        // 目标 Vulkan 1.0，与 wavsen 构建时内嵌的 SPIR-V 同版本。
        const ShaderCompUnit unit {
            ShaderType::COMPUTE, std::string(owe::media::Nv12ToRgba::ShaderSource()), "main"
        };
        std::vector<Uni_ShaderSpv>              spv;
        std::string                             error = "nv12_to_rgba shader compile failed";
        std::unique_ptr<owe::media::Nv12ToRgba> created;
        if (CompileAndLinkShaderUnits(
                std::span(&unit, 1), ShaderCompOpt { .target = VulkanTarget::Vulkan_1_0 }, spv) &&
            spv.size() == 1) {
            created = owe::media::Nv12ToRgba::Create(*device.gpu(),
                                                     *device.handle(),
                                                     device.graphics_queue().family_index,
                                                     *device.graphics_queue().handle,
                                                     next_w,
                                                     next_h,
                                                     spv.front()->spirv,
                                                     error);
        }
        if (! created) {
            rstd_error("CreateVideoTex: YuvToRgba create failed: {}", error);
            return nullptr;
        }
        yuv            = std::move(created);
        yuv_max_width  = next_w;
        yuv_max_height = next_h;
        return yuv.get();
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
    // 目标图像取纹理头尺寸；奇数宽高由解码与转换按 4:2:0 色度向上取整处理。
    runtime.width  = static_cast<rstd::uint32_t>(mip.width);
    runtime.height = static_cast<rstd::uint32_t>(mip.height);

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

    /* 3) Open the software decoder (it loops: EOF seeks back to zero). */
    if (! runtime.decoder.open(std::move(video_source).into_reader(), runtime.width, runtime.height)) {
        rstd_error("CreateVideoTex: video open failed for {}: {}", image.key, runtime.decoder.last_error());
        return None();
    }
    const auto threads = runtime.decoder.decode_threads();
    rstd_info("VideoDecoder: sw decode threads={} type={}.", threads.count, threads.type);
    if (runtime.playback.is_some()) {
        (*runtime.playback)->PublishTime((*runtime.playback)->CurrentTime(),
                                         ToOption<f64>(runtime.decoder.duration()));
    }
    rstd_info("CreateVideoTex: {} hwdec={} decoder kind=sw", image.key, registry->options.hwdec);

    auto metadata = runtime.decoder.stream_metadata();
    if (runtime.playback.is_some()) {
        (*runtime.playback)->PublishPeriodMetadata(VideoPlaybackPeriodMetadata {
            .duration_ticks = ToOption<rstd::int64_t>(metadata.duration_ticks),
            .time_base_num  = ToOption<i32>(metadata.time_base_num),
            .time_base_den  = ToOption<i32>(metadata.time_base_den),
            .frame_count    = ToOption<u64>(metadata.frame_count),
            .loops          = true, // VideoSource always seeks back to zero at EOF.
        });
    }
    VideoDecoderObservation observation;
    observation.resource_key = image.key;
    observation.instance_id = registry->next_instance_id++;
    observation.codec = metadata.codec;
    observation.coded_width = metadata.coded_width;
    observation.coded_height = metadata.coded_height;
    observation.pixel_format = metadata.pixel_format;
    observation.fps_num = metadata.fps_num;
    observation.fps_den = metadata.fps_den;
    observation.fps_source = metadata.fps_source;
    observation.decoder_kind = "sw";
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
    if (registry == nullptr || device == nullptr) return;
    auto& s            = *this;
    auto  publish_time = [&] {
        if (s.playback.is_some()) {
            (*s.playback)->PublishTime(s.pts_acc, ToOption<f64>(s.decoder.duration()));
        }
    };
    const bool offline = active_offline_execution != nullptr;
    if (!offline && s.playback.is_some()) {
        auto state = (*s.playback)->Snapshot();
        if (state.seek_sequence != s.applied_seek_sequence) {
            const bool seeked       = s.decoder.seek(state.seek_seconds.to_primitive());
            s.applied_seek_sequence = state.seek_sequence;
            if (! seeked) {
                rstd_error("PumpVideoTextures[{}]: seek: {}", s.key.as_str(), s.decoder.last_error());
            } else {
                s.pts_acc    = state.seek_seconds;
                s.last_pts   = f64(-1.0);
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

    bool got_new = false;
    if (offline) {
        auto fail = [&](const std::string& message) {
            active_offline_execution->diagnose(
                "video[" + rstd::cppstd::to_string(s.key.as_str()) + "]: " + message, true);
            rstd_error("PumpVideoTextures[{}]: {}", s.key.as_str(), message);
        };
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
        const auto duration = s.decoder.duration();
        if (!duration || !std::isfinite(*duration) || *duration <= 0.0 ||
            !std::isfinite(media_time) || media_time < 0.0) {
            fail("offline video sampling requires a finite timestamp and positive video duration");
            return;
        }
        const double duration_s = *duration;
        double quotient = media_time / duration_s;
        const double nearest = std::round(quotient);
        if (std::abs(quotient - nearest) <= 4.0 * std::numeric_limits<double>::epsilon() *
            std::max(1.0, std::abs(quotient))) quotient = nearest;
        const double cycle = std::floor(quotient);
        const double cycle_start = cycle * duration_s;
        s.pts_acc = f64(std::max(0.0, media_time - cycle_start));
        if (first || seek_changed || cycle != s.offline_cycle) {
            if (! s.decoder.seek(s.pts_acc.to_primitive())) {
                fail(std::string(s.decoder.last_error()));
                return;
            }
            s.offline_pending = None();
            s.offline_drained = false;
            s.offline_cycle = cycle;
            s.last_pts = f64(-1.0);
        }
        // Retain one future frame as lookahead. Only a frame whose PTS has
        // arrived may replace the displayed image; output FPS does not change
        // the source's sampling rate. Offline catch-up must not drop work.
        while (!s.offline_drained) {
            if (s.offline_pending.is_none()) {
                owe::media::Nv12Frame candidate;
                auto pulled = s.decoder.next_frame(candidate);
                if (!pulled) {
                    fail(std::string(s.decoder.last_error()));
                    return;
                }
                if (*pulled != owe::media::NextFrame::Ok) {
                    // The decoder's eager loop has consumed the next cycle's
                    // first frame. Seek again only when scene time wraps.
                    s.offline_drained = true;
                    break;
                }
                if (!std::isfinite(candidate.pts_seconds) || candidate.pts_seconds < 0.0) {
                    fail("decoded video frame has no usable presentation timestamp");
                    return;
                }
                s.offline_pending = Some(rstd::move(candidate));
            }
            const double deadline = cycle_start + s.offline_pending->pts_seconds;
            const double tolerance = 4.0 * std::numeric_limits<double>::epsilon() *
                std::max(1.0, std::max(std::abs(deadline), std::abs(media_time)));
            if (deadline - media_time > tolerance) break;
            s.nv12_scratch = rstd::move(*s.offline_pending);
            s.offline_pending = None();
            s.last_pts = f64(s.nv12_scratch.pts_seconds);
            got_new = true;
        }
    } else {
    /* Realtime pacing retains its bounded catch-up policy. */
    for (int i = 0; i < 4; ++i) {
        if (s.last_pts >= f64() && s.last_pts > s.pts_acc) break;

        auto pulled = s.decoder.next_frame(s.nv12_scratch);
        if (!pulled) {
            rstd_error("PumpVideoTextures[{}]: decode sw: {}", s.key.as_str(), s.decoder.last_error());
            break;
        }
        const bool decoder_looped = *pulled == owe::media::NextFrame::Looped;
        const f64  frame_pts(s.nv12_scratch.pts_seconds);
        if (decoder_looped) s.pts_acc = frame_pts.max(f64());
        s.last_pts = frame_pts;
        got_new    = true;
        if (decoder_looped) break;
    }
    }
    if (got_new) s.convert_pending = true;
    // 离线不光栅的帧（预热、取样步之间）不写纹理：最新解出的帧留在 nv12_scratch，到要画的那帧再转，
    // 画出来的纹理与每帧都转相同。4K 视频每帧的上传与转换比解码还贵（2903412088 起点搜索 16 帧只画 1 帧）。
    if (! s.convert_pending || (offline && ! active_offline_raster)) {
        publish_time();
        return;
    }

    if (! yuv->Convert(ip.handle,
                       s.width,
                       s.height,
                       s.nv12_scratch.data,
                       owe::media::MakeYuvColorMatrix(s.nv12_scratch.colorspace,
                                                      s.nv12_scratch.color_range))) {
        rstd_error("PumpVideoTextures[{}]: yuv conversion sw: {}", s.key.as_str(), yuv->last_error());
        publish_time();
        return;
    }
    s.convert_pending = false;
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

// 转换器析构时自己等设备空闲（原 wavsen 的 drain_submissions 对软件帧本就是空操作）。
TextureCache::~TextureCache() = default;

u64 TextureCache::nextImageGeneration() { return m_next_image_generation++; }

void TextureCache::AssignImageGeneration(VmaImageParameters& image) {
    image.generation = nextImageGeneration();
}

void TextureCache::SetVideoDecodeOptions(VideoDecodeOptions options) {
    m_video_decode_options = std::move(options);
    if (m_video_registry.is_some()) m_video_registry->get()->options = m_video_decode_options;
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
