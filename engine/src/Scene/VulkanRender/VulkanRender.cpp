module;
#include <rstd/macro.hpp>

#include "vvk/macros.hpp"

#include <cerrno>
#include <chrono>
#include <cmath>
#include <cstdlib>
#include <sstream>
#include <vulkan/vulkan.h>
#include "GpuVideoEncoder.hpp"

module wescene.vulkan_render;
import wescene.core;
import wescene.types;
import rstd.log;
import rstd.cppstd;
import wescene.resource_registry;
import wescene.vulkan;
import wescene.shader_compile;
import wescene.utils;
import wescene.scene;
import wescene.text;

import wescene.rgraph;

using namespace owe::vulkan;
using namespace rstd::prelude;
using namespace rstd::literals;

constexpr std::uint64_t        vk_wait_time { static_cast<std::uint64_t>(
    rstd::time::Duration::from_secs(u64(10)).as_nanos().to_primitive()) };
constexpr std::uint32_t        vk_upload_command_num { 3 };
constexpr std::uint32_t        vk_command_num { vk_upload_command_num + 1 };
constexpr VkPipelineStageFlags vk_upload_wait_stages { VK_PIPELINE_STAGE_TRANSFER_BIT |
                                                       VK_PIPELINE_STAGE_VERTEX_INPUT_BIT |
                                                       VK_PIPELINE_STAGE_VERTEX_SHADER_BIT |
                                                       VK_PIPELINE_STAGE_FRAGMENT_SHADER_BIT };

bool SameRenderItemId(owe::RenderItemId lhs, owe::RenderItemId rhs) {
    return lhs.index == rhs.index && lhs.generation == rhs.generation;
}

void PushUniqueRenderItem(Vec<owe::RenderItemId>& items, owe::RenderItemId id) {
    for (usize index {}; index < items.len(); ++index) {
        if (SameRenderItemId(items[index], id)) return;
    }
    items.push(rstd::move(id));
}

Vec<owe::RenderItemId> RenderItemsForMaterials(const owe::RenderSceneSnapshot& render_scene,
                                               slice<owe::SceneMaterialId>     materials) {
    Vec<owe::RenderItemId> render_items;
    for (usize material_index {}; material_index < materials.len(); ++material_index) {
        auto material       = materials[material_index];
        auto material_items = render_scene.renderItemsFor(material);
        for (usize index {}; index < material_items.len(); ++index) {
            PushUniqueRenderItem(render_items, material_items[index]);
        }
    }
    return render_items;
}

constexpr rstd::array<Extension, 4> base_inst_exts {
    Extension { false, VK_KHR_GET_PHYSICAL_DEVICE_PROPERTIES_2_EXTENSION_NAME },
    Extension { false, VK_KHR_EXTERNAL_MEMORY_CAPABILITIES_EXTENSION_NAME },
    Extension { false, VK_KHR_EXTERNAL_SEMAPHORE_CAPABILITIES_EXTENSION_NAME },
    Extension { false, VK_KHR_EXTERNAL_FENCE_CAPABILITIES_EXTENSION_NAME },
};
constexpr rstd::array<Extension, 5> base_device_exts {
    Extension { false, VK_EXT_MEMORY_BUDGET_EXTENSION_NAME },
    Extension { false, VK_KHR_PUSH_DESCRIPTOR_EXTENSION_NAME },
    Extension { false, VK_EXT_SHADER_VIEWPORT_INDEX_LAYER_EXTENSION_NAME },
    Extension { true, VK_KHR_TIMELINE_SEMAPHORE_EXTENSION_NAME },
    Extension { false, VK_KHR_SYNCHRONIZATION_2_EXTENSION_NAME },
};

void AppendVideoDeviceExtensions(std::vector<Extension>& device_exts) {
    device_exts.push_back({ false, VK_EXT_EXTERNAL_MEMORY_DMA_BUF_EXTENSION_NAME });
    device_exts.push_back({ false, VK_KHR_IMAGE_FORMAT_LIST_EXTENSION_NAME });
    device_exts.push_back({ false, VK_EXT_IMAGE_DRM_FORMAT_MODIFIER_EXTENSION_NAME });
    device_exts.push_back({ false, VK_EXT_QUEUE_FAMILY_FOREIGN_EXTENSION_NAME });
    device_exts.push_back({ false, VK_KHR_VIDEO_QUEUE_EXTENSION_NAME });
    device_exts.push_back({ false, VK_KHR_VIDEO_DECODE_QUEUE_EXTENSION_NAME });
    device_exts.push_back({ false, VK_KHR_VIDEO_DECODE_H264_EXTENSION_NAME });
    device_exts.push_back({ false, VK_KHR_VIDEO_DECODE_H265_EXTENSION_NAME });
    device_exts.push_back({ false, VK_KHR_VIDEO_DECODE_AV1_EXTENSION_NAME });
    device_exts.push_back({ false, VK_EXT_EXTERNAL_MEMORY_HOST_EXTENSION_NAME });
    device_exts.push_back({ false, VK_EXT_DESCRIPTOR_BUFFER_EXTENSION_NAME });
    device_exts.push_back({ false, VK_EXT_SHADER_OBJECT_EXTENSION_NAME });
}

void ReleaseCompletedRetiredResources(Device& device, RenderingResources& rr) {
    rr.resources.Collect(&device);
}

namespace {

constexpr std::uint32_t timestamp_query_count = 3;

constexpr std::uint64_t TimestampDelta(std::uint64_t begin, std::uint64_t end,
                                      std::uint32_t valid_bits) noexcept {
    const auto mask = valid_bits == 64 ? std::numeric_limits<std::uint64_t>::max()
                                      : (std::uint64_t(1) << valid_bits) - 1;
    return (end - begin) & mask;
}
static_assert(TimestampDelta((std::uint64_t(1) << 36) - 3, 2, 36) == 5);
static_assert(TimestampDelta(std::numeric_limits<std::uint64_t>::max() - 2, 2, 64) == 5);

class TimestampQueryPool {
public:
    TimestampQueryPool() = default;
    TimestampQueryPool(const TimestampQueryPool&) = delete;
    TimestampQueryPool& operator=(const TimestampQueryPool&) = delete;
    ~TimestampQueryPool() { reset(); }

    VkResult create(const vvk::Device& device) noexcept {
        reset();
        const VkQueryPoolCreateInfo info {
            .sType = VK_STRUCTURE_TYPE_QUERY_POOL_CREATE_INFO,
            .queryType = VK_QUERY_TYPE_TIMESTAMP,
            .queryCount = timestamp_query_count,
        };
        VkQueryPool pool = VK_NULL_HANDLE;
        const auto result = device.Dispatch().vkCreateQueryPool(*device, &info, nullptr, &pool);
        if (result == VK_SUCCESS) {
            m_pool = pool;
            m_device = *device;
            m_dispatch = &device.Dispatch();
        }
        return result;
    }

    void reset() noexcept {
        if (m_pool != VK_NULL_HANDLE) {
            // Only failure cleanup may need this wait. Normal frames use the
            // existing fence; their queries never introduce another GPU wait.
            if (m_in_flight) (void)m_dispatch->vkDeviceWaitIdle(m_device);
            m_dispatch->vkDestroyQueryPool(m_device, m_pool, nullptr);
        }
        m_pool = VK_NULL_HANDLE;
        m_device = VK_NULL_HANDLE;
        m_dispatch = nullptr;
        m_in_flight = false;
    }

    VkQueryPool get() const noexcept { return m_pool; }
    explicit operator bool() const noexcept { return m_pool != VK_NULL_HANDLE; }
    void submitted() noexcept { m_in_flight = m_pool != VK_NULL_HANDLE; }
    void completed() noexcept { m_in_flight = false; }

private:
    VkQueryPool m_pool { VK_NULL_HANDLE };
    VkDevice m_device { VK_NULL_HANDLE };
    const vvk::DeviceDispatch* m_dispatch { nullptr };
    bool m_in_flight { false };
};

struct CaptureBinding {
    std::string render_target;
    std::string producer_pass;
    std::uint64_t texture_version { 0 };
    owe::resource::TextureUseHandle texture_use;
    owe::rg::NodeHandle writer;
};

std::string ValidateCaptureSelector(const owe::RenderCaptureTarget& selector) {
    if (selector.texture_version < -1 || selector.owner_layer_id < -1 ||
        selector.authored_effect_id < -1 || selector.effect_ordinal < -1)
        return "capture selector IDs and texture version must be nonnegative or -1";
    if (selector.force_visible_owner && (selector.owner_layer_id < 0 ||
        !selector.runtime_render_target.empty()))
        return "force_visible_owner requires an authored owner capture selector";
    if (selector.effect_terminal) {
        if (!selector.runtime_render_target.empty() || !selector.local_fbo.empty() ||
            selector.owner_layer_id < 0 ||
            (selector.authored_effect_id < 0 && selector.effect_ordinal < 0) ||
            selector.texture_version != -1)
            return "terminal capture requires owner_layer_id, an effect ID or ordinal, and no local FBO/version";
    } else if (!selector.runtime_render_target.empty()) {
        if (selector.owner_layer_id != -1 || selector.authored_effect_id != -1 ||
            selector.effect_ordinal != -1 || !selector.local_fbo.empty())
            return "capture requires either a runtime target or an authored effect selector";
    } else if (selector.owner_layer_id < 0 || selector.local_fbo.empty() ||
               (selector.authored_effect_id < 0 && selector.effect_ordinal < 0)) {
        return "capture requires owner_layer_id, local_fbo and an effect ID or ordinal";
    }
    return {};
}

std::optional<CaptureBinding> ResolveCaptureBinding(
    owe::Scene& scene, owe::rg::RenderGraph& graph, const owe::RenderCaptureTarget& selector,
    std::string& error) {
    std::string key = selector.runtime_render_target;
    if (key.empty()) {
        Option<const owe::SceneImageEffect&> selected_effect;
        bool effect_found { false };
        if (!selector.effect_terminal && rstd::cppstd::as_str(selector.local_fbo).is_err()) {
            error = "capture local FBO name is not valid UTF-8";
            return std::nullopt;
        }
        for (auto* node : scene.ResourceIndex().Nodes()) {
            if (node == nullptr) continue;
            const auto owner = node->WallpaperIdentity();
            if (owner.is_none() || owner->value.to_primitive() != selector.owner_layer_id ||
                !node->HasLayer() || !node->Layer()) continue;
            if (selector.force_visible_owner && node->Parent() != scene.RootMut().as_raw_ptr()) {
                error = "force_visible_owner requires a flat top-level owner";
                return std::nullopt;
            }
            const auto& layer = node->Layer();
            for (usize index {}; index < layer->EffectCount(); ++index) {
                const auto& effect = layer->GetEffect(index);
                if (!effect ||
                    (selector.authored_effect_id >= 0 &&
                     effect->authored_id != selector.authored_effect_id) ||
                    (selector.effect_ordinal >= 0 &&
                     effect->authored_ordinal != selector.effect_ordinal)) continue;
                if (effect_found) {
                    error = "capture effect selector is ambiguous";
                    return std::nullopt;
                }
                effect_found = true;
                if (selector.effect_terminal) selected_effect = Some<const owe::SceneImageEffect&>(*effect);
                else key = rstd::cppstd::to_string(
                    scene.EffectResourceKey(effect->id,
                                            rstd::cppstd::as_str(selector.local_fbo).unwrap()).as_str());
            }
        }
        if (selector.effect_terminal && selected_effect.is_some()) {
            if (selected_effect->nodes.empty() ||
                selected_effect->nodes.back().output.kind != owe::SceneEffectTargetKind::LayerNext ||
                selected_effect->nodes.back().graph_pass_index.is_none()) {
                error = "terminal capture effect does not end in a graph-backed LayerNext pass";
                return std::nullopt;
            }
            const auto writer = owe::rg::NodeHandle {
                .index = usize(selected_effect->nodes.back().graph_pass_index->to_primitive())
            };
            auto written = graph.writtenTexture(writer);
            if (written.is_none()) {
                error = "terminal capture effect has no unique graph texture writer";
                return std::nullopt;
            }
            auto producer = graph.passState(written->writer);
            if (producer.is_none()) {
                error = "terminal capture graph writer is unavailable";
                return std::nullopt;
            }
            const auto terminal_name = written->texture.desc.key.as_str();
            auto target = scene.RenderTarget(terminal_name);
            if (target.is_none() || (**target).kind != owe::SceneRenderTargetKind::Color) {
                error = "terminal capture graph writer does not produce an RGBA8 color target";
                return std::nullopt;
            }
            return CaptureBinding {
                .render_target = rstd::cppstd::to_string(written->texture.desc.key.as_str()),
                .producer_pass = rstd::cppstd::to_string(producer->name.as_str()),
                .texture_version = written->texture.version.to_primitive(),
                .texture_use = written->texture.use,
                .writer = written->writer,
            };
        }
        if (key.empty()) {
            error = "capture owner/effect was not found in the parsed scene";
            return std::nullopt;
        }
    }
    auto name = rstd::cppstd::as_str(key);
    if (name.is_err()) {
        error = "capture runtime target is not valid UTF-8";
        return std::nullopt;
    }
    auto target = scene.RenderTarget(*name);
    if (target.is_none()) {
        error = "capture render target is not registered: " + key;
        return std::nullopt;
    }
    if ((**target).kind != owe::SceneRenderTargetKind::Color) {
        error = "capture currently supports RGBA8 color render targets only: " + key;
        return std::nullopt;
    }
    auto version = selector.texture_version >= 0
        ? Some(usize(static_cast<std::size_t>(selector.texture_version))) : None<usize>();
    auto written = graph.writtenTexture(*name, version);
    if (written.is_none()) {
        error = "capture target has no real graph writer for the requested version: " + key;
        return std::nullopt;
    }
    auto producer = graph.passState(written->writer);
    if (producer.is_none()) {
        error = "capture graph writer is unavailable: " + key;
        return std::nullopt;
    }
    return CaptureBinding {
        .render_target = std::move(key),
        .producer_pass = rstd::cppstd::to_string(producer->name.as_str()),
        .texture_version = written->texture.version.to_primitive(),
        .texture_use = written->texture.use,
        .writer = written->writer,
    };
}

} // namespace

struct VulkanRender::Impl {
    Impl()  = default;
    ~Impl() = default;

    bool init(RenderInitInfo);
    void destroy();

    void drawFrame(Scene&);
    CpuFrameResult drawFrameCpu(Scene&, bool read_pixels);
    std::string finishPendingFrame();
    void recycleCpuPixels(std::vector<std::uint8_t>&&);
    bool initCpuReadback(const RenderInitInfo&);
    bool initSamplePipeline();
    void initGpuTiming(const RenderInitInfo&);

    bool CreateRenderingResource(RenderingResources&);
    void DestroyRenderingResource(RenderingResources&);

    void clearLastRenderGraph(RenderGraphResourceRetention);
    void configureRenderTargets(Scene&);
    void compileRenderGraph(Scene&, rg::RenderGraph&);
    void compileRenderGraph(Scene&, rg::RenderGraph&, const RenderSceneSnapshot&);
    void refreshPreparedResources(Scene&);
    void refreshPreparedResources(Scene&, const RenderSceneSnapshot&);
    void refreshPreparedResources(Scene&, const RenderSceneSnapshot&,
                                  resource::ResourcePlanSections);
    void refreshPreparedTextures(Scene&, const RenderSceneSnapshot&);
    void invalidatePreparedRenderItems(slice<owe::RenderItemId>, PassInvalidationFlags);
    void refreshPreparedRenderItems(Scene&, const RenderSceneSnapshot&, slice<owe::RenderItemId>,
                                    PassInvalidationFlags);
    void refreshPreparedMaterial(Scene&, const RenderSceneSnapshot&, owe::SceneMaterialId,
                                 PassInvalidationFlags);
    bool refreshPreparedMaterialTextures(Scene&, const RenderSceneSnapshot&, owe::SceneMaterialId);
    bool refreshPreparedMaterialTextures(Scene&, const RenderSceneSnapshot&,
                                         slice<owe::SceneMaterialId>);
    void refreshPreparedMesh(Scene&, const RenderSceneSnapshot&, owe::SceneMeshId,
                             PassInvalidationFlags);
    std::vector<PreparedPassDiagnostic> preparedPassDiagnostics() const;
    void                                UpdateCameraFillMode(Scene&, owe::FillMode);
    bool ApplyOrthographicCaptureViewport(Scene&, std::string& error);

    bool                      initRes();
    rstd::Option<std::size_t> acquireUploadCommandSlot(RenderingResources&);
    bool                      commitPreparedUploads();
    bool prepareProgram(Scene&, const RenderSceneSnapshot&, resource::ResourcePlanSections);
    bool waitForPreparedUploads(RenderingResources&);
    void drawFrameSwapchain(Scene&);
    void drawFrameOffscreen(Scene&);
    bool onSwapchainReady(unsigned width, unsigned height);

    Instance     m_instance;
    Box<Device>  m_device { Box<Device>::make() };
    Box<PrePass> m_prepass { Box<PrePass>::make(PrePass::Desc {}) };
    Box<FinPass> m_finpass { Box<FinPass>::make(FinPass::Desc {}) };
    ReDrawCB     m_redraw_cb;

    ShaderReflectionCache      m_shader_reflection_cache;

    vvk::CommandBuffers             m_cmds;
    std::vector<vvk::CommandBuffer> m_upload_cmds;
    std::vector<std::uint64_t>      m_upload_cmd_values;
    std::size_t                     m_next_upload_cmd { 0 };
    vvk::CommandBuffer              m_render_cmd;

    bool m_with_surface { false };
    bool m_inited { false };
    bool m_cpu_readback { false };
    bool m_cpu_failed { false };
    VkFormat m_cpu_format { VK_FORMAT_R8G8B8A8_UNORM };
    std::uint64_t m_readback_timeout_ns { vk_wait_time };
    std::uint64_t m_cpu_frame_index { 0 };
    // Pixel buffer handed back by the previous frame's consumer; reused as-is
    // so the per-frame resize neither allocates nor zero-fills.
    std::vector<std::uint8_t> m_cpu_pixels_pool;
    VmaImageParameters m_cpu_image;
    VmaBufferParameters m_cpu_staging;
    std::uint32_t m_readback_width { 0 }, m_readback_height { 0 };
    bool m_sample_readback { false }, m_gpu_samples { false };
    bool m_sample_coverage_enabled { false };
    std::uint64_t m_sample_coverage_start {}, m_sample_coverage_frames {}, m_sample_coverage_observed {};
    VmaBufferParameters m_sample_coverage;
    vvk::ImageView m_sample_view;
    vvk::ShaderModule m_sample_shader;
    vvk::DescriptorSetLayout m_sample_descriptors;
    vvk::PipelineLayout m_sample_layout;
    vvk::Pipeline m_sample_pipeline;
    std::unique_ptr<GpuVideoEncoder> m_gpu_encoder;
    owe::resource::CompletionToken m_pending_cpu_submission;
    bool m_gpu_pipeline { false };
    std::optional<GpuEncodeOptions> m_encode_options;
    std::optional<RenderCaptureTarget> m_capture_target;
    std::optional<OrthographicCaptureViewport> m_orthographic_capture_viewport;
    bool m_orthographic_capture_viewport_rejected { false };
    std::optional<CaptureBinding> m_capture_binding;
    std::string m_capture_error;
    TimestampQueryPool m_timestamp_queries;
    bool m_gpu_timing_requested { false };
    bool m_cpu_timing_requested { false };
    double m_effect_render_scale { 1.0 };
    bool m_match_effect_resolution { false };
    bool m_effect_render_scale_reported { false };
    bool m_gpu_timing_supported { false };
    std::uint32_t m_timestamp_valid_bits { 0 };
    std::optional<double> m_timestamp_period_ns;
    VkResult m_gpu_timing_error_code { VK_SUCCESS };
    std::string m_gpu_timing_message;

    // MSAA sample count for the screen RT only. 1bit = disabled.
    // Resolved against device's framebufferColorSampleCounts in init().
    VkSampleCountFlagBits m_msaa_samples { VK_SAMPLE_COUNT_1_BIT };

    std::shared_ptr<ExSwapchain> m_ex_swapchain;
    RenderingResources           m_rendering_resources;
    u64                          m_next_surface_acquire_serial { 1 };

    // for VUID-vkQueueSubmit-pSignalSemaphores-00067
    std::vector<vvk::Semaphore> m_sem_swap_finish_per_image;

    RenderProgram m_program;
};

VulkanRender::VulkanRender(): pImpl(Box<Impl>::make()) {}
VulkanRender::~VulkanRender() {};

bool VulkanRender::inited() const { return pImpl->m_inited; }

int VulkanRender::takeLastFrameSyncFd() {
    return pImpl->m_ex_swapchain ? pImpl->m_ex_swapchain->takeLastFrameSyncFd() : -1;
}

bool VulkanRender::getDrmRenderNode(std::uint32_t& out_major, std::uint32_t& out_minor) const {
    if (! pImpl->m_inited) return false;
    VkPhysicalDeviceDrmPropertiesEXT drm {};
    drm.sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_DRM_PROPERTIES_EXT;
    VkPhysicalDeviceProperties2KHR props {};
    props.sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_PROPERTIES_2_KHR;
    props.pNext = &drm;
    pImpl->m_device->gpu().GetProperties2KHR(props);
    if (! drm.hasRender) return false;
    if (drm.renderMajor < 0 || drm.renderMinor < 0 ||
        static_cast<std::uint64_t>(drm.renderMajor) > UINT32_MAX ||
        static_cast<std::uint64_t>(drm.renderMinor) > UINT32_MAX) {
        return false;
    }
    out_major = static_cast<std::uint32_t>(drm.renderMajor);
    out_minor = static_cast<std::uint32_t>(drm.renderMinor);
    return true;
}

DeviceCapabilities VulkanRender::deviceCapabilities() const {
    if (! pImpl->m_inited) return {};
    return pImpl->m_device->capabilities();
}

VkInstance VulkanRender::vkInstance() const {
    if (! pImpl->m_inited) return VK_NULL_HANDLE;
    return *pImpl->m_instance.inst();
}

VkPhysicalDevice VulkanRender::vkPhysicalDevice() const {
    if (! pImpl->m_inited) return VK_NULL_HANDLE;
    return *pImpl->m_device->gpu();
}

VkDevice VulkanRender::vkDevice() const {
    if (! pImpl->m_inited) return VK_NULL_HANDLE;
    return *pImpl->m_device->handle();
}

VkQueue VulkanRender::vkGraphicsQueue() const {
    if (! pImpl->m_inited) return VK_NULL_HANDLE;
    return *pImpl->m_device->graphics_queue().handle;
}

std::uint32_t VulkanRender::vkGraphicsQueueFamily() const {
    if (! pImpl->m_inited) return 0;
    return pImpl->m_device->graphics_queue().family_index;
}

void VulkanRender::deviceUuid(std::uint8_t out[16]) const {
    std::memset(out, 0, 16);
    if (! pImpl->m_inited) return;
    VkPhysicalDeviceIDPropertiesKHR id {};
    id.sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_ID_PROPERTIES_KHR;
    VkPhysicalDeviceProperties2KHR props {};
    props.sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_PROPERTIES_2_KHR;
    props.pNext = &id;
    pImpl->m_device->gpu().GetProperties2KHR(props);
    std::memcpy(out, id.deviceUUID, 16);
}

void VulkanRender::pumpVideoTextures(double dt_seconds) {
    if (! pImpl->m_inited) return;
    pImpl->m_rendering_resources.resources.PumpVideoTextures(dt_seconds);
}

void VulkanRender::pumpFontAtlases(Scene& scene) {
    if (! pImpl->m_inited) return;
    auto* fc = owe::text::SceneFontCache(scene);
    if (fc == nullptr) return;
    for (auto* face : fc->Faces()) {
        if (face == nullptr) continue;
        auto rects = face->DirtyRects();
        if (rects.empty()) continue;
        // Coalesce all dirty rects into one AABB. Typical: ≤ a handful of
        // glyph slots per frame, so a single upload covering the union beats
        // submitting one copy per rect.
        std::uint32_t min_x = rects[0].x;
        std::uint32_t min_y = rects[0].y;
        std::uint32_t max_x = rects[0].x + rects[0].w;
        std::uint32_t max_y = rects[0].y + rects[0].h;
        for (auto& r : rects.subspan(1)) {
            if (r.x < min_x) min_x = r.x;
            if (r.y < min_y) min_y = r.y;
            const std::uint32_t rx2 = r.x + r.w;
            const std::uint32_t ry2 = r.y + r.h;
            if (rx2 > max_x) max_x = rx2;
            if (ry2 > max_y) max_y = ry2;
        }
        const auto fm     = face->Metrics();
        const auto pixels = face->AtlasPixels();
        (void)pImpl->m_rendering_resources.resources.UploadFontAtlasRegion(face->AtlasUrl(),
                                                                           pixels.data(),
                                                                           fm.atlas_w,
                                                                           min_x,
                                                                           min_y,
                                                                           max_x - min_x,
                                                                           max_y - min_y);
        // Clear regardless: if VkImage didn't exist yet, the pixels are
        // already in the CPU buffer that CreateTex aliases on its first
        // call. Re-uploading would just duplicate work.
        face->ClearDirtyRects();
    }
}

void VulkanRender::driverUuid(std::uint8_t out[16]) const {
    std::memset(out, 0, 16);
    if (! pImpl->m_inited) return;
    VkPhysicalDeviceIDPropertiesKHR id {};
    id.sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_ID_PROPERTIES_KHR;
    VkPhysicalDeviceProperties2KHR props {};
    props.sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_PROPERTIES_2_KHR;
    props.pNext = &id;
    pImpl->m_device->gpu().GetProperties2KHR(props);
    std::memcpy(out, id.driverUUID, 16);
}

bool VulkanRender::init(RenderInitInfo info) { return pImpl->init(rstd::move(info)); }
void VulkanRender::destroy() { pImpl->destroy(); }
void VulkanRender::drawFrame(Scene& scene) { pImpl->drawFrame(scene); };
owe::CpuFrameResult VulkanRender::drawFrameCpu(Scene& scene, bool read_pixels) {
    return pImpl->drawFrameCpu(scene, read_pixels);
}
void VulkanRender::recycleCpuPixels(std::vector<std::uint8_t>&& buffer) {
    pImpl->recycleCpuPixels(rstd::move(buffer));
}
void VulkanRender::clearLastRenderGraph(RenderGraphResourceRetention retention) {
    pImpl->clearLastRenderGraph(retention);
};
void VulkanRender::configureRenderTargets(Scene& scene) { pImpl->configureRenderTargets(scene); }
void VulkanRender::compileRenderGraph(Scene& scene, rg::RenderGraph& rg) {
    pImpl->compileRenderGraph(scene, rg);
}
void VulkanRender::compileRenderGraph(Scene& scene, rg::RenderGraph& rg,
                                      const RenderSceneSnapshot& render_scene) {
    pImpl->compileRenderGraph(scene, rg, render_scene);
}
void VulkanRender::refreshPreparedResources(Scene& scene) {
    pImpl->refreshPreparedResources(scene);
}
void VulkanRender::refreshPreparedResources(Scene& scene, const RenderSceneSnapshot& render_scene) {
    pImpl->refreshPreparedResources(scene, render_scene);
}
void VulkanRender::refreshPreparedTextures(Scene& scene, const RenderSceneSnapshot& render_scene) {
    pImpl->refreshPreparedTextures(scene, render_scene);
}
void VulkanRender::invalidatePreparedRenderItems(slice<owe::RenderItemId> render_items,
                                                 PassInvalidationFlags    flags) {
    pImpl->invalidatePreparedRenderItems(render_items, flags);
}
void VulkanRender::refreshPreparedRenderItems(Scene& scene, const RenderSceneSnapshot& render_scene,
                                              slice<owe::RenderItemId> render_items,
                                              PassInvalidationFlags    flags) {
    pImpl->refreshPreparedRenderItems(scene, render_scene, render_items, flags);
}
void VulkanRender::refreshPreparedMaterial(Scene& scene, const RenderSceneSnapshot& render_scene,
                                           owe::SceneMaterialId  material,
                                           PassInvalidationFlags flags) {
    pImpl->refreshPreparedMaterial(scene, render_scene, material, flags);
}
bool VulkanRender::refreshPreparedMaterialTextures(Scene&                     scene,
                                                   const RenderSceneSnapshot& render_scene,
                                                   owe::SceneMaterialId       material) {
    return pImpl->refreshPreparedMaterialTextures(scene, render_scene, material);
}
bool VulkanRender::refreshPreparedMaterialTextures(Scene&                      scene,
                                                   const RenderSceneSnapshot&  render_scene,
                                                   slice<owe::SceneMaterialId> materials) {
    return pImpl->refreshPreparedMaterialTextures(scene, render_scene, materials);
}
void VulkanRender::refreshPreparedMesh(Scene& scene, const RenderSceneSnapshot& render_scene,
                                       owe::SceneMeshId mesh, PassInvalidationFlags flags) {
    pImpl->refreshPreparedMesh(scene, render_scene, mesh, flags);
}
std::vector<PreparedPassDiagnostic> VulkanRender::preparedPassDiagnostics() const {
    return pImpl->preparedPassDiagnostics();
}
std::optional<std::string> VulkanRender::firstUnpreparedPass() const {
    return pImpl->m_program.firstUnpreparedPass();
}
std::string VulkanRender::finishPendingFrame() { return pImpl->finishPendingFrame(); }
void VulkanRender::evictUnusedMeshes() {
    if (pImpl->m_inited) pImpl->m_rendering_resources.resources.EvictUnusedBuffers();
};
void VulkanRender::UpdateCameraFillMode(Scene& scene, owe::FillMode fill) {
    pImpl->UpdateCameraFillMode(scene, fill);
};

bool VulkanRender::onSwapchainReady(unsigned width, unsigned height) {
    return pImpl->onSwapchainReady(width, height);
}

owe::ExSwapchain* VulkanRender::exSwapchain() const { return pImpl->m_ex_swapchain.get(); };

bool VulkanRender::Impl::init(RenderInitInfo info) {
    if (m_inited) return true;

    if (!std::isfinite(info.effect_render_scale) || info.effect_render_scale <= 0.0 ||
        info.effect_render_scale > 1.0) {
        rstd_error("effect_render_scale must be finite and in (0, 1]");
        return false;
    }
    m_effect_render_scale = info.effect_render_scale;
    if (info.match_effect_resolution && (info.effect_render_scale != 1.0 || info.capture_target)) {
        rstd_error("adaptive effect resolution requires scale 1 and a whole-scene capture");
        return false;
    }
    m_match_effect_resolution = info.match_effect_resolution;

    m_cpu_readback = info.output_mode == RenderOutputMode::CpuReadback;
    m_cpu_timing_requested = info.gpu_timing;
    m_gpu_pipeline = m_cpu_readback && (info.gpu_encode || info.collect_sampling_coverage) && !info.gpu_timing;
    m_prepass->setTransparentBackground(info.layer_selection.enabled &&
                                        info.layer_selection.transparent_background);
    if (info.orthographic_capture_viewport.has_value()) {
        const auto& viewport = *info.orthographic_capture_viewport;
        if (!m_cpu_readback || !std::isfinite(viewport.center_x) ||
            !std::isfinite(viewport.center_y) || !std::isfinite(viewport.width) ||
            !std::isfinite(viewport.height) || viewport.width <= 0.0 || viewport.height <= 0.0) {
            rstd_error("orthographic capture viewport requires CpuReadback and finite positive width/height");
            return false;
        }
        m_orthographic_capture_viewport = viewport;
    }
    if (info.capture_target.has_value()) {
        m_capture_error = ValidateCaptureSelector(*info.capture_target);
        if (!m_cpu_readback || !m_capture_error.empty()) {
            rstd_error("invalid CPU capture selector: {}", !m_cpu_readback
                ? "graph capture requires CpuReadback mode" : m_capture_error);
            return false;
        }
        m_capture_target = std::move(info.capture_target);
    }
    if (m_cpu_readback) {
        info.offscreen = true;
        info.video_hwdec = "none";
        if (info.ex_swapchain_factory || info.width == 0 || info.height == 0 ||
            info.cpu_format != VK_FORMAT_R8G8B8A8_UNORM || info.readback_timeout_ns == 0) {
            rstd_error("CPU readback requires nonzero dimensions/timeout, RGBA8, and no external swapchain");
            return false;
        }
        const std::uint64_t bytes = std::uint64_t(info.width) * info.height * 4;
        if (bytes > info.max_readback_bytes || bytes > std::numeric_limits<std::size_t>::max()) {
            rstd_error("CPU readback frame ({} bytes) exceeds configured budget ({})",
                       bytes, info.max_readback_bytes);
            return false;
        }
        m_cpu_format = info.cpu_format;
        m_readback_timeout_ns = info.readback_timeout_ns;
    }
#ifdef _WIN32
    if (! m_cpu_readback) {
        rstd_error("this Windows renderer requires CpuReadback output mode");
        return false;
    }
#endif

    m_redraw_cb = info.redraw_callback;
    VkExtent2D extent { info.width, info.height };
    if (! m_cpu_readback && extent.width * extent.height < 500 * 500) {
        rstd_error("too small swapchain image size: {}x{}", extent.width, extent.height);
    } else {
        rstd_info("set swapchain image size: {}x{}", extent.width, extent.height);
    }

    std::vector<Extension> inst_exts;
    std::vector<Extension> device_exts;
    if (m_cpu_readback) {
        inst_exts.push_back({ true, VK_KHR_GET_PHYSICAL_DEVICE_PROPERTIES_2_EXTENSION_NAME });
    } else {
        for (const auto& extension : base_inst_exts) inst_exts.push_back(extension);
        device_exts.push_back({ true, VK_KHR_EXTERNAL_MEMORY_FD_EXTENSION_NAME });
        device_exts.push_back({ true, VK_KHR_EXTERNAL_SEMAPHORE_FD_EXTENSION_NAME });
        device_exts.push_back({ false, VK_EXT_PHYSICAL_DEVICE_DRM_EXTENSION_NAME });
    }
    for (const auto& extension : base_device_exts) device_exts.push_back(extension);
    if (info.gpu_encode) {
        device_exts.push_back({ true, VK_KHR_VIDEO_QUEUE_EXTENSION_NAME });
        device_exts.push_back({ true, VK_KHR_VIDEO_ENCODE_QUEUE_EXTENSION_NAME });
        device_exts.push_back({ true, VK_KHR_VIDEO_MAINTENANCE_1_EXTENSION_NAME });
        device_exts.push_back({ true, info.gpu_encode->codec == "hevc_vulkan"
            ? VK_KHR_VIDEO_ENCODE_H265_EXTENSION_NAME : VK_KHR_VIDEO_ENCODE_H264_EXTENSION_NAME });
        device_exts.push_back({ true, VK_KHR_PUSH_DESCRIPTOR_EXTENSION_NAME });
        device_exts.push_back({ true, VK_KHR_SYNCHRONIZATION_2_EXTENSION_NAME });
    }
    if (info.video_hwdec != "none") {
        AppendVideoDeviceExtensions(device_exts);
    }

    if (! info.offscreen) {
        std::transform(info.surface_info.instanceExts.begin(),
                       info.surface_info.instanceExts.end(),
                       std::back_inserter(inst_exts),
                       [](const auto& s) {
                           return Extension { true, s.c_str() };
                       });
        device_exts.push_back({ true, VK_KHR_SWAPCHAIN_EXTENSION_NAME });
    } else if (! m_cpu_readback) {
        // Iteration 1a: offscreen FDs are real Linux DMA-BUFs so they can be
        // imported by arbitrary external consumers. These extensions are
        // strictly required on the offscreen path; if a driver lacks them
        // we fail fast in Device::CheckGPU.
        device_exts.push_back({ true, VK_EXT_EXTERNAL_MEMORY_DMA_BUF_EXTENSION_NAME });
        // Required by VK_EXT_image_drm_format_modifier on Vulkan 1.1 (promoted
        // to core in 1.2). Validation layer rejects the device otherwise.
        device_exts.push_back({ true, VK_KHR_IMAGE_FORMAT_LIST_EXTENSION_NAME });
        device_exts.push_back({ true, VK_EXT_IMAGE_DRM_FORMAT_MODIFIER_EXTENSION_NAME });
        device_exts.push_back({ true, VK_EXT_QUEUE_FAMILY_FOREIGN_EXTENSION_NAME });
    }

    std::vector<InstanceLayer> inst_layers;
    // valid layer
    if (info.enable_valid_layer) {
        inst_layers.push_back({ true, VALIDATION_LAYER_NAME });
        rstd_info("vulkan valid layer \"{}\" enabled", VALIDATION_LAYER_NAME);
    }

    const auto instance_api_version =
        info.video_hwdec == "none" && !info.gpu_encode ? WP_VULKAN_VERSION : VK_API_VERSION_1_3;
    {
        if (! Instance::Create(m_instance, inst_exts, inst_layers, instance_api_version)) {
            rstd_error("init vulkan failed");
            return false;
        }
        if (! info.offscreen) {
            VkSurfaceKHR surface;
            VVK_CHECK_ACT(
                {
                    rstd_error("create vulkan surface failed");
                    return false;
                },
                info.surface_info.createSurfaceOp(*m_instance.inst(), &surface));
            m_instance.setSurface(VkSurfaceKHR(surface));
            m_with_surface = true;
        }
        auto surface   = *m_instance.surface();
        auto check_gpu = [&device_exts, surface](const vvk::PhysicalDevice& gpu) {
            return Device::CheckGPU(gpu, device_exts, surface);
        };
        if (! m_instance.ChoosePhysicalDevice(check_gpu, info.uuid)) return false;
    }

    {
        if (! Device::Create(m_instance, device_exts, extent, *m_device)) {
            rstd_error("init vulkan device failed");
            return false;
        }
        if (! m_rendering_resources.resources.Initialize(*m_device)) {
            rstd_error("resource registry init failed");
            return false;
        }
        m_rendering_resources.resources.SetVideoDecodeOptions(TextureCache::VideoDecodeOptions {
            .hwdec       = info.video_hwdec,
            .render_node = info.video_render_node,
        });
    }

    {
        // Map requested integer to a bit; clamp down to highest supported bit
        // not exceeding the request, given device's framebufferColorSampleCounts.
        const std::uint32_t      requested = info.msaa_samples == 0 ? 1u : info.msaa_samples;
        const VkSampleCountFlags supported = m_device->limits().framebufferColorSampleCounts;
        VkSampleCountFlagBits    chosen    = VK_SAMPLE_COUNT_1_BIT;
        constexpr rstd::array<VkSampleCountFlagBits, 6> ladder {
            VK_SAMPLE_COUNT_64_BIT, VK_SAMPLE_COUNT_32_BIT, VK_SAMPLE_COUNT_16_BIT,
            VK_SAMPLE_COUNT_8_BIT,  VK_SAMPLE_COUNT_4_BIT,  VK_SAMPLE_COUNT_2_BIT,
        };
        for (auto bit : ladder) {
            if (static_cast<std::uint32_t>(bit) <= requested && (supported & bit)) {
                chosen = bit;
                break;
            }
        }
        m_msaa_samples = chosen;
        rstd_info(
            "msaa requested={} actual={}", requested, static_cast<std::uint32_t>(m_msaa_samples));
    }

    if (m_cpu_readback) {
        if (! initCpuReadback(info)) return false;
    } else if (info.offscreen) {
        if (info.ex_swapchain_factory) {
            RenderInitInfo::ExSwapchainHandles h {
                *m_instance.inst(),
                *m_device->gpu(),
                *m_device->handle(),
                *m_device->graphics_queue().handle,
                m_device->graphics_queue().family_index,
            };
            m_ex_swapchain = info.ex_swapchain_factory(h);
            if (! m_ex_swapchain) {
                rstd_error("ex_swapchain_factory returned null");
                return false;
            }
        } else {
            m_ex_swapchain = m_rendering_resources.resources.CreateLocalSwapchain(
                *m_device,
                extent.width,
                extent.height,
                (info.offscreen_tiling == TexTiling::OPTIMAL ? VK_IMAGE_TILING_OPTIMAL
                                                             : VK_IMAGE_TILING_LINEAR));
        }
        m_with_surface = false;
    }

    {
        if (! initRes()) return false;
    }

    // Allocate last: no later initialization failure can strand query objects.
    // Timestamp failure is diagnostic only; ordinary rendering remains usable.
    initGpuTiming(info);
    m_inited = true;
    return m_inited;
}

bool VulkanRender::Impl::initRes() {
    {
        auto& pool = m_device->cmd_pool();
        VVK_CHECK_BOOL_RE(
            pool.Allocate(usize(vk_command_num), VK_COMMAND_BUFFER_LEVEL_PRIMARY, m_cmds));
        m_upload_cmds.clear();
        m_upload_cmds.reserve(vk_upload_command_num);
        for (std::uint32_t i = 0; i < vk_upload_command_num; ++i) {
            m_upload_cmds.emplace_back(m_cmds[usize(i)], m_device->handle().Dispatch());
        }
        m_upload_cmd_values.assign(vk_upload_command_num, 0);
        m_next_upload_cmd = 0;
        m_render_cmd =
            vvk::CommandBuffer(m_cmds[usize(vk_upload_command_num)], m_device->handle().Dispatch());
    }
    if (! CreateRenderingResource(m_rendering_resources)) return false;

    return true;
}

bool VulkanRender::Impl::initCpuReadback(const RenderInitInfo& info) {
    m_encode_options = info.gpu_encode;
    const auto extent = m_device->out_extent();
    if ((info.sample_width == 0) != (info.sample_height == 0) ||
        info.sample_width > extent.width || info.sample_height > extent.height) {
        rstd_error("sample readback must fit inside the rendered image");
        return false;
    }
    m_sample_readback = info.sample_width != 0;
    m_readback_width = m_sample_readback ? info.sample_width : extent.width;
    m_readback_height = m_sample_readback ? info.sample_height : extent.height;
    if (extent.width > m_device->limits().maxImageDimension2D ||
        extent.height > m_device->limits().maxImageDimension2D) {
        rstd_error("CPU output extent exceeds maxImageDimension2D");
        return false;
    }
    const auto features = m_device->gpu().GetFormatProperties(info.cpu_format).optimalTilingFeatures;
    const auto queue_properties = m_device->gpu().GetQueueFamilyProperties();
    const auto cell_pixels = std::uint64_t((extent.width + m_readback_width - 1) / m_readback_width) *
        ((extent.height + m_readback_height - 1) / m_readback_height);
    const auto sample_bytes = std::uint64_t(m_readback_width) * m_readback_height * 4;
    m_gpu_samples = m_sample_readback &&
        (features & VK_FORMAT_FEATURE_STORAGE_IMAGE_BIT) &&
        (queue_properties[usize(m_device->graphics_queue().family_index)].queueFlags & VK_QUEUE_COMPUTE_BIT) &&
        sample_bytes <= m_device->limits().maxStorageBufferRange &&
        cell_pixels * 255 + cell_pixels / 2 <= std::numeric_limits<std::uint32_t>::max();
    m_sample_coverage_enabled = info.collect_sampling_coverage && m_gpu_samples && info.sampling_coverage_frames != 0;
    m_sample_coverage_start = info.sampling_coverage_start;
    m_sample_coverage_frames = info.sampling_coverage_frames;
    m_sample_coverage_observed = 0;
    constexpr VkFormatFeatureFlags required = VK_FORMAT_FEATURE_TRANSFER_SRC_BIT |
                                               VK_FORMAT_FEATURE_TRANSFER_DST_BIT |
                                               VK_FORMAT_FEATURE_BLIT_DST_BIT;
    if ((features & required) != required) {
        rstd_error("CPU output format does not support image transfers and blits");
        return false;
    }
    VkImageCreateInfo image_info {
        .sType         = VK_STRUCTURE_TYPE_IMAGE_CREATE_INFO,
        .imageType     = VK_IMAGE_TYPE_2D,
        .format        = info.cpu_format,
        .extent        = { extent.width, extent.height, 1 },
        .mipLevels     = 1,
        .arrayLayers   = 1,
        .samples       = VK_SAMPLE_COUNT_1_BIT,
        .tiling        = VK_IMAGE_TILING_OPTIMAL,
        .usage         = VK_IMAGE_USAGE_TRANSFER_SRC_BIT | VK_IMAGE_USAGE_TRANSFER_DST_BIT |
                         (m_gpu_samples || m_encode_options ? VK_IMAGE_USAGE_STORAGE_BIT : 0u),
        .sharingMode   = VK_SHARING_MODE_EXCLUSIVE,
        .initialLayout = VK_IMAGE_LAYOUT_UNDEFINED,
    };
    VmaAllocationCreateInfo image_allocation {};
    image_allocation.usage = VMA_MEMORY_USAGE_GPU_ONLY;
    VVK_CHECK_BOOL_RE(vvk::CreateImage(m_device->vma_allocator(), image_info,
                                       image_allocation, m_cpu_image.handle));
    m_cpu_image.extent = image_info.extent;
    m_cpu_image.generation = u64(1);

    if (m_encode_options) {
        if (m_sample_readback || !(features & VK_FORMAT_FEATURE_STORAGE_IMAGE_BIT)) return false;
        const auto& encode = *m_encode_options;
        try {
            m_gpu_encoder = std::make_unique<GpuVideoEncoder>(m_device->instance_handle(), *m_device->gpu(),
                *m_device->handle(), m_device->graphics_queue().family_index,
                m_device->enabled_instance_extensions(), m_device->enabled_device_extensions(),
                extent.width, extent.height, encode.packed_alpha, encode.fps_num, encode.fps_den,
                encode.qp, encode.codec, encode.path,
                GpuCaptureOptions { encode.collect_bounds, encode.bounds_include_rgb,
                    encode.encoded_frames ? encode.encoded_frames : encode.frames, encode.retain_frames,
                    encode.crossfade_frames, encode.crop_x, encode.crop_y, encode.crop_width, encode.crop_height,
                    encode.retain_loop_window, encode.resize_width, encode.resize_height });
        } catch (const std::exception& error) {
            rstd_error("GPU encode initialization: {}", error.what());
            return false;
        }
        return true;
    }

    // GPU sampling writes compact packed RGBA words directly to the readback buffer.
    // Unsupported devices keep the full copy and average from the mapped pixels.
    m_cpu_staging.req_size = m_gpu_samples ? sample_bytes : std::uint64_t(extent.width) * extent.height * 4;
    if (m_sample_coverage_enabled) m_cpu_staging.req_size += 32;
    VkBufferCreateInfo buffer_info {
        .sType       = VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO,
        .size        = m_cpu_staging.req_size,
        .usage       = m_gpu_samples ? VK_BUFFER_USAGE_STORAGE_BUFFER_BIT |
            (m_sample_coverage_enabled ? VK_BUFFER_USAGE_TRANSFER_DST_BIT : 0u) : VK_BUFFER_USAGE_TRANSFER_DST_BIT,
        .sharingMode = VK_SHARING_MODE_EXCLUSIVE,
    };
    VmaAllocationCreateInfo buffer_allocation {};
    buffer_allocation.usage = VMA_MEMORY_USAGE_GPU_TO_CPU;
    buffer_allocation.requiredFlags = VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT;
    buffer_allocation.preferredFlags = VK_MEMORY_PROPERTY_HOST_CACHED_BIT;
    VVK_CHECK_BOOL_RE(vvk::CreateBuffer(m_device->vma_allocator(), buffer_info,
                                        buffer_allocation, m_cpu_staging.handle));
    if (m_sample_coverage_enabled) {
        buffer_info.size = m_sample_coverage.req_size = 32;
        buffer_info.usage = VK_BUFFER_USAGE_STORAGE_BUFFER_BIT | VK_BUFFER_USAGE_TRANSFER_SRC_BIT | VK_BUFFER_USAGE_TRANSFER_DST_BIT;
        VmaAllocationCreateInfo statistics_allocation {};
        statistics_allocation.usage = VMA_MEMORY_USAGE_GPU_ONLY;
        VVK_CHECK_BOOL_RE(vvk::CreateBuffer(m_device->vma_allocator(),buffer_info,statistics_allocation,m_sample_coverage.handle));
    }
    return !m_gpu_samples || initSamplePipeline();
}

bool VulkanRender::Impl::initSamplePipeline() {
    const ShaderCompUnit unit { ShaderType::COMPUTE, R"glsl(#version 450
layout(local_size_x=8, local_size_y=8) in;
layout(binding=0, rgba8) readonly uniform image2D sourceImage;
layout(binding=1, std430) writeonly buffer Output { uint rgba[]; } samples;
layout(binding=2, std430) buffer Coverage { uint data[8]; } coverage;
layout(push_constant) uniform Dimensions { uvec2 sourceSize; uvec2 targetSize; uint flags; } dims;
shared uint groupBounds[8];
void main() {
    uvec2 p = gl_GlobalInvocationID.xy;
    bool inGrid=all(lessThan(p,dims.targetSize));
    bool collect=(dims.flags&2u)!=0u;
    if (collect) {
        if (gl_LocalInvocationIndex==0u) {
            groupBounds[0]=dims.sourceSize.x; groupBounds[1]=dims.sourceSize.y;
            groupBounds[2]=0u; groupBounds[3]=0u; groupBounds[4]=255u; groupBounds[5]=0u;
            groupBounds[6]=0u; groupBounds[7]=0u;
        }
        barrier();
    }
    uvec2 first = p * dims.sourceSize / dims.targetSize;
    uvec2 last = max(first + 1u, (p + 1u) * dims.sourceSize / dims.targetSize);
    uvec4 total = uvec4(0);
    uvec4 box=uvec4(dims.sourceSize,0u,0u);
    uint minA=255u,maxA=0u,content=0u;
    if (inGrid)
    for (uint y = first.y; y < last.y; ++y)
        for (uint x = first.x; x < last.x; ++x) {
            uvec4 value=uvec4(imageLoad(sourceImage,ivec2(x,y))*255.0+0.5);
            if ((dims.flags&1u)!=0u) total+=value;
            if (collect) {
                minA=min(minA,value.a); maxA=max(maxA,value.a);
                if (any(notEqual(value,uvec4(0)))) {
                    box.xy=min(box.xy,uvec2(x,y)); box.zw=max(box.zw,uvec2(x,y)); content=1u;
                }
            }
        }
    if (collect) {
        atomicMin(groupBounds[4],minA); atomicMax(groupBounds[5],maxA);
        if (content!=0u) {
            atomicMin(groupBounds[0],box.x); atomicMin(groupBounds[1],box.y);
            atomicMax(groupBounds[2],box.z); atomicMax(groupBounds[3],box.w); atomicOr(groupBounds[7],1u);
        }
        barrier();
        if (gl_LocalInvocationIndex==0u) {
            atomicMin(coverage.data[4],groupBounds[4]); atomicMax(coverage.data[5],groupBounds[5]);
            if (groupBounds[7]!=0u) {
                atomicMin(coverage.data[0],groupBounds[0]); atomicMin(coverage.data[1],groupBounds[1]);
                atomicMax(coverage.data[2],groupBounds[2]); atomicMax(coverage.data[3],groupBounds[3]); atomicOr(coverage.data[7],1u);
            }
        }
    }
    if (!inGrid || (dims.flags&1u)==0u) return;
    uint n = (last.x-first.x) * (last.y-first.y);
    uvec4 mean = (total + n/2u) / n;
    samples.rgba[p.y*dims.targetSize.x+p.x] = mean.r | (mean.g<<8u) | (mean.b<<16u) | (mean.a<<24u);
}
)glsl", "main" };
    std::vector<Uni_ShaderSpv> code;
    if (!CompileAndLinkShaderUnits(std::span(&unit, 1), ShaderCompOpt {}, code) || code.size() != 1) return false;
    const auto& words = code.front()->spirv;
    const auto& device = m_device->handle();
    VVK_CHECK_BOOL_RE(device.CreateShaderModule(VkShaderModuleCreateInfo {
        .sType = VK_STRUCTURE_TYPE_SHADER_MODULE_CREATE_INFO,
        .codeSize = words.size() * sizeof(unsigned int), .pCode = words.data() }, m_sample_shader));
    VVK_CHECK_BOOL_RE(device.CreateImageView(VkImageViewCreateInfo {
        .sType = VK_STRUCTURE_TYPE_IMAGE_VIEW_CREATE_INFO, .image = *m_cpu_image.handle,
        .viewType = VK_IMAGE_VIEW_TYPE_2D, .format = m_cpu_format,
        .subresourceRange = { VK_IMAGE_ASPECT_COLOR_BIT, 0, 1, 0, 1 } }, m_sample_view));
    const std::array<VkDescriptorSetLayoutBinding, 3> bindings {{
        { 0, VK_DESCRIPTOR_TYPE_STORAGE_IMAGE, 1, VK_SHADER_STAGE_COMPUTE_BIT, nullptr },
        { 1, VK_DESCRIPTOR_TYPE_STORAGE_BUFFER, 1, VK_SHADER_STAGE_COMPUTE_BIT, nullptr },
        { 2, VK_DESCRIPTOR_TYPE_STORAGE_BUFFER, 1, VK_SHADER_STAGE_COMPUTE_BIT, nullptr } }};
    VVK_CHECK_BOOL_RE(device.CreateDescriptorSetLayout(VkDescriptorSetLayoutCreateInfo {
        .sType = VK_STRUCTURE_TYPE_DESCRIPTOR_SET_LAYOUT_CREATE_INFO,
        .flags = VK_DESCRIPTOR_SET_LAYOUT_CREATE_PUSH_DESCRIPTOR_BIT_KHR,
        .bindingCount = 3, .pBindings = bindings.data() }, m_sample_descriptors));
    const VkDescriptorSetLayout descriptors = *m_sample_descriptors;
    const VkPushConstantRange constants { VK_SHADER_STAGE_COMPUTE_BIT, 0, 5 * sizeof(std::uint32_t) };
    VVK_CHECK_BOOL_RE(device.CreatePipelineLayout(VkPipelineLayoutCreateInfo {
        .sType = VK_STRUCTURE_TYPE_PIPELINE_LAYOUT_CREATE_INFO,
        .setLayoutCount = 1, .pSetLayouts = &descriptors,
        .pushConstantRangeCount = 1, .pPushConstantRanges = &constants }, m_sample_layout));
    VVK_CHECK_BOOL_RE(device.CreateComputePipeline(VkComputePipelineCreateInfo {
        .sType = VK_STRUCTURE_TYPE_COMPUTE_PIPELINE_CREATE_INFO,
        .stage = { .sType = VK_STRUCTURE_TYPE_PIPELINE_SHADER_STAGE_CREATE_INFO,
                   .stage = VK_SHADER_STAGE_COMPUTE_BIT, .module = *m_sample_shader, .pName = "main" },
        .layout = *m_sample_layout }, m_sample_pipeline));
    return true;
}

void VulkanRender::Impl::initGpuTiming(const RenderInitInfo& info) {
    m_gpu_timing_requested = info.gpu_timing;
    if (!m_gpu_timing_requested) return;
    auto unsupported = [&](const char* message) {
        m_gpu_timing_error_code = VK_ERROR_FEATURE_NOT_PRESENT;
        m_gpu_timing_message = message;
    };
    if (!m_cpu_readback) {
        unsupported("GPU timing requires CpuReadback mode");
        return;
    }
    const auto family = m_device->graphics_queue().family_index;
    const auto properties = m_device->gpu().GetQueueFamilyProperties();
    if (usize(family) >= properties.len()) {
        unsupported("graphics queue family properties are unavailable for GPU timing");
        return;
    }
    m_timestamp_valid_bits = properties[usize(family)].timestampValidBits;
    if (m_timestamp_valid_bits == 0 || m_timestamp_valid_bits > 64) {
        unsupported("graphics queue does not support usable timestamps");
        return;
    }
    const double period = m_device->limits().timestampPeriod;
    if (!(period > 0.0) || !std::isfinite(period)) {
        unsupported("GPU timestamp period is not finite and positive");
        return;
    }
    m_timestamp_period_ns = period;
    const auto& dispatch = m_device->handle().Dispatch();
    if (!dispatch.vkCreateQueryPool || !dispatch.vkDestroyQueryPool ||
        !dispatch.vkGetQueryPoolResults || !dispatch.vkCmdResetQueryPool ||
        !dispatch.vkCmdWriteTimestamp || !dispatch.vkDeviceWaitIdle) {
        unsupported("required GPU timestamp query entry points are unavailable");
        return;
    }
    m_gpu_timing_supported = true;
    m_gpu_timing_error_code = m_timestamp_queries.create(m_device->handle());
    if (m_gpu_timing_error_code != VK_SUCCESS) {
        m_gpu_timing_message = "create GPU timestamp query pool failed";
    }
}

void VulkanRender::Impl::destroy() {
    if (! m_inited) return;
    if (m_device->handle()) {
        VVK_CHECK(m_device->handle().WaitIdle());
        m_timestamp_queries.completed();
        m_timestamp_queries.reset();

        // res
        m_program.destroyPasses(*m_device);
        ReleaseCompletedRetiredResources(*m_device, m_rendering_resources);
        m_program.clear();
        m_rendering_resources.resources.Reset();
        m_gpu_encoder.reset();
        m_sample_pipeline = vvk::Pipeline {};
        m_sample_layout = vvk::PipelineLayout {};
        m_sample_descriptors = vvk::DescriptorSetLayout {};
        m_sample_shader = vvk::ShaderModule {};
        m_sample_view = vvk::ImageView {};
        m_cpu_staging.handle = vvk::VmaBuffer {};
        m_sample_coverage.handle = vvk::VmaBuffer {};
        m_cpu_image.handle = vvk::VmaImage {};

        m_device->Destroy();
    }
    m_instance.Destroy();
}

bool VulkanRender::Impl::CreateRenderingResource(RenderingResources& rr) {
    rr.command = m_render_cmd;
    VVK_CHECK_BOOL_RE(m_device->handle().CreateFence(
        VkFenceCreateInfo {
            .sType = VK_STRUCTURE_TYPE_FENCE_CREATE_INFO,
            .pNext = nullptr,
            .flags = VK_FENCE_CREATE_SIGNALED_BIT,
        },
        rr.fence_frame));

    rr.fence_frame.Reset();

    {
        VkSemaphoreTypeCreateInfo type_info {
            .sType         = VK_STRUCTURE_TYPE_SEMAPHORE_TYPE_CREATE_INFO,
            .pNext         = nullptr,
            .semaphoreType = VK_SEMAPHORE_TYPE_TIMELINE,
            .initialValue  = 0,
        };
        VkSemaphoreCreateInfo ci {
            .sType = VK_STRUCTURE_TYPE_SEMAPHORE_CREATE_INFO,
            .pNext = &type_info,
            .flags = 0,
        };
        VVK_CHECK_BOOL_RE(m_device->handle().CreateSemaphore(ci, rr.sem_upload));
    }

    if (m_with_surface) {
        VkSemaphoreCreateInfo ci { .sType = VK_STRUCTURE_TYPE_SEMAPHORE_CREATE_INFO,
                                   .pNext = nullptr };
        VVK_CHECK_BOOL_RE(m_device->handle().CreateSemaphore(ci, rr.sem_swap_wait_image));

        const std::size_t n_images = m_device->swapchain().images().size();
        m_sem_swap_finish_per_image.clear();
        m_sem_swap_finish_per_image.resize(n_images);
        for (auto& s : m_sem_swap_finish_per_image) {
            VVK_CHECK_BOOL_RE(m_device->handle().CreateSemaphore(ci, s));
        }
    }

    // Exportable SYNC_FD semaphore used by the waywallen-renderer host
    // to ship a dma_fence sync_file to display clients on each
    // FrameReady event. CPU readback uses its submission fence and never
    // creates an exportable semaphore.
    if (! m_cpu_readback) {
        VkExportSemaphoreCreateInfo export_info {
            .sType       = VK_STRUCTURE_TYPE_EXPORT_SEMAPHORE_CREATE_INFO,
            .pNext       = nullptr,
            .handleTypes = VK_EXTERNAL_SEMAPHORE_HANDLE_TYPE_SYNC_FD_BIT_KHR,
        };
        VkSemaphoreCreateInfo ci {
            .sType = VK_STRUCTURE_TYPE_SEMAPHORE_CREATE_INFO,
            .pNext = &export_info,
            .flags = 0,
        };
        VVK_CHECK_BOOL_RE(m_device->handle().CreateSemaphore(ci, rr.sem_export));
    }

    rr.shader_reflection_cache = rstd::Some(rstd::mut_ref<ShaderReflectionCache>::from_raw_parts(
        rstd::addressof(m_shader_reflection_cache)));
    return true;
}

void VulkanRender::Impl::DestroyRenderingResource(RenderingResources&) {}

rstd::Option<std::size_t> VulkanRender::Impl::acquireUploadCommandSlot(RenderingResources& rr) {
    if (m_upload_cmds.empty()) return rstd::None();
    const std::size_t slot = m_next_upload_cmd;
    m_next_upload_cmd      = (m_next_upload_cmd + 1) % m_upload_cmds.size();

    const std::uint64_t wait_value = m_upload_cmd_values[slot];
    if (wait_value != 0) {
        std::uint64_t counter = 0;
        VVK_CHECK_ACT(return rstd::None(), rr.sem_upload.GetCounter(&counter));
        if (counter < wait_value) {
            const auto result = rr.sem_upload.Wait(wait_value, vk_wait_time);
            if (result == VK_TIMEOUT) return rstd::None();
            VVK_CHECK_ACT(return rstd::None(), result);
        }
        rr.resources.CompleteUploadsThrough(u64(wait_value));
        m_upload_cmd_values[slot] = 0;
    }
    return rstd::Some<std::size_t>(slot);
}

bool VulkanRender::Impl::commitPreparedUploads() {
    if (! m_rendering_resources.resources.HasPendingUploads()) return true;
    auto slot = acquireUploadCommandSlot(m_rendering_resources);
    if (slot.is_none()) return false;
    auto committed =
        m_program.commitUploads(*m_device, m_rendering_resources, m_upload_cmds[*slot]);
    if (! committed.success) return false;
    if (committed.signal_value != u64()) {
        m_upload_cmd_values[*slot] = committed.signal_value.to_primitive();
    }
    return true;
}

bool VulkanRender::Impl::waitForPreparedUploads(RenderingResources& rr) {
    auto pending = rr.resources.PendingUpload();
    if (pending.is_none()) {
        return true;
    }
    std::uint64_t counter = 0;
    VVK_CHECK_ACT(return false, rr.sem_upload.GetCounter(&counter));
    if (counter < pending->value.to_primitive()) {
        const auto result = rr.sem_upload.Wait(pending->value.to_primitive(), vk_wait_time);
        if (result == VK_TIMEOUT) return false;
        VVK_CHECK_ACT(return false, result);
    }
    rr.resources.CompleteUploadsThrough(pending->value);
    return true;
}

void VulkanRender::Impl::drawFrame(Scene& scene) {
    if (! (m_inited && m_program.loaded)) return;
    if (m_cpu_readback) {
        rstd_error("CPU output requires drawFrameCpu to consume each completed frame");
        return;
    }

    if (m_instance.offscreen()) {
        drawFrameOffscreen(scene);
    } else {
        drawFrameSwapchain(scene);
    }

    if (m_redraw_cb) m_redraw_cb();
}

std::string VulkanRender::Impl::finishPendingFrame() {
    if (!m_pending_cpu_submission.Valid()) return {};
    try {
        if (m_gpu_encoder) m_gpu_encoder->waitConversion();
        auto& rr = m_rendering_resources;
        const auto result = rr.fence_frame.Wait(m_readback_timeout_ns);
        if (result != VK_SUCCESS)
            throw std::runtime_error("wait queued render frame: VkResult=" + std::to_string(result));
        if (rr.resources.CompleteSubmission(m_pending_cpu_submission).is_none())
            throw std::runtime_error("queued render resource completion was unavailable");
        m_pending_cpu_submission = {};
        m_timestamp_queries.completed();
        ReleaseCompletedRetiredResources(*m_device, rr);
        return {};
    } catch (const std::exception& error) {
        m_cpu_failed = true;
        return error.what();
    }
}

owe::CpuFrameResult VulkanRender::Impl::drawFrameCpu(Scene& scene, bool read_pixels) {
    const auto cpu_started = m_cpu_timing_requested ? std::chrono::steady_clock::now() : std::chrono::steady_clock::time_point{};
    if (m_gpu_encoder) read_pixels = false;
    CpuFrameResult frame;
    frame.gpu_scene_overlap = m_gpu_pipeline && (m_gpu_encoder || m_sample_coverage_enabled);
    frame.gpu_timing_requested = m_gpu_timing_requested;
    frame.gpu_timing_supported = m_gpu_timing_supported;
    frame.timestamp_valid_bits = m_timestamp_valid_bits;
    frame.timestamp_period_ns = m_timestamp_period_ns;
    frame.gpu_timing_error_code = m_gpu_timing_error_code;
    frame.gpu_timing_message = m_gpu_timing_message;
    frame.frame_index = m_cpu_frame_index;
    if (! m_cpu_readback) {
        frame.status = CpuFrameStatus::InvalidMode;
        frame.message = "renderer was not initialized for CPU readback";
        return frame;
    }
    if (m_cpu_failed) {
        frame.status = CpuFrameStatus::RenderError;
        frame.message = "CPU renderer failed previously; create a new renderer before retrying";
        return frame;
    }
    if (!m_capture_error.empty()) {
        frame.status = CpuFrameStatus::RenderError;
        frame.error_code = VK_ERROR_INITIALIZATION_FAILED;
        frame.message = m_capture_error;
        return frame;
    }
    std::string capture_viewport_error;
    if (!ApplyOrthographicCaptureViewport(scene, capture_viewport_error)) {
        frame.status = CpuFrameStatus::RenderError;
        frame.error_code = VK_ERROR_INITIALIZATION_FAILED;
        frame.message = std::move(capture_viewport_error);
        return frame;
    }
    if (! m_inited || ! m_program.loaded || ! m_finpass->prepared()) {
        frame.message = "render graph or final pass is not ready";
        return frame;
    }
    auto fail = [&](VkResult result, std::string operation) -> CpuFrameResult {
        m_cpu_failed = true;
        frame.status = result == VK_TIMEOUT ? CpuFrameStatus::Timeout :
                       result == VK_ERROR_DEVICE_LOST ? CpuFrameStatus::DeviceLost :
                                                       CpuFrameStatus::RenderError;
        frame.error_code = result;
        frame.message = std::move(operation);
        frame.pixels.clear();
        frame.gpu_total_ms.reset();
        frame.gpu_draw_ms.reset();
        return std::move(frame);
    };

    if (const auto error = finishPendingFrame(); !error.empty())
        return fail(VK_ERROR_INITIALIZATION_FAILED, error);

    const auto extent = m_device->out_extent();
    if (extent.width != m_cpu_image.extent.width || extent.height != m_cpu_image.extent.height) {
        return fail(VK_ERROR_INITIALIZATION_FAILED, "CPU output extent changed; create a new renderer");
    }
    if (m_cpu_frame_index == std::numeric_limits<std::uint64_t>::max()) {
        return fail(VK_ERROR_TOO_MANY_OBJECTS, "CPU frame serial overflow");
    }
    frame.width = m_readback_width;
    frame.height = m_readback_height;
    frame.row_pitch = m_readback_width * 4;
    frame.gpu_sampled = m_gpu_samples;
    frame.format = m_cpu_format;
    frame.compiled_scene_passes = m_program.compiledScenePassCount();
    if (m_capture_binding.has_value()) {
        frame.source_render_target = m_capture_binding->render_target;
        frame.source_pass = m_capture_binding->producer_pass;
        frame.source_texture_version = m_capture_binding->texture_version;
        auto name = rstd::cppstd::as_str(frame.source_render_target).unwrap();
        auto source = scene.RenderTarget(name);
        if (source.is_none()) return fail(VK_ERROR_INITIALIZATION_FAILED,
                                          "captured render target disappeared");
        frame.source_width = static_cast<std::uint32_t>((**source).PhysicalWidth().to_primitive());
        frame.source_height = static_cast<std::uint32_t>((**source).PhysicalHeight().to_primitive());
        if (m_capture_target->exact_extent &&
            (frame.source_width != extent.width || frame.source_height != extent.height)) {
            return fail(VK_ERROR_INITIALIZATION_FAILED,
                        "exact capture extent requires output " + std::to_string(frame.source_width) +
                            "x" + std::to_string(frame.source_height) +
                            "; selected source differs from the configured CPU output");
        }
    }
    // Take the recycled buffer: when it already holds a full frame the resize
    // below is a no-op, so no allocation and no zero fill precede the copy.
    if (read_pixels) {
        frame.pixels = rstd::move(m_cpu_pixels_pool);
        try {
            frame.pixels.resize(static_cast<std::size_t>(m_readback_width) * m_readback_height * 4);
        } catch (const std::bad_alloc&) {
            return fail(VK_ERROR_OUT_OF_HOST_MEMORY, "allocate CPU frame pixels");
        }
    }

    auto& rr = m_rendering_resources;
    auto pending_upload = rr.resources.PendingUpload();
    if (pending_upload.is_some()) {
        const auto result = rr.sem_upload.Wait(pending_upload->value.to_primitive(),
                                                m_readback_timeout_ns);
        if (result != VK_SUCCESS) return fail(result, "wait for texture uploads");
        rr.resources.CompleteUploadsThrough(pending_upload->value);
    }

    const auto queue_family = m_device->graphics_queue().family_index;
    owe::FrameSurfaceLease surface {
        .identity = { .owner_generation = u64(1), .image_index = u32(0),
                      .acquire_serial = u64(m_cpu_frame_index + 1) },
        .reuse = { .kind = owe::FrameSurfaceReuseKind::QueueOrdered },
        .image = ToImageParameters(m_cpu_image),
        .format = m_cpu_format,
        .initial_layout = VK_IMAGE_LAYOUT_UNDEFINED,
        .initial_queue_family = queue_family,
        .acquire = { .kind = owe::FrameSurfaceAcquireKind::QueueOrdered },
        .final_layout = VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL,
        .final_queue_family = queue_family,
        .discard_content = true,
    };
    auto external_preparer =
        rstd::dyn<resource_registry::ExternalResourcePreparer>::from_ref(rr.resources);
    if (! m_finpass->setFrameSurface(surface, external_preparer.as_mut_ref(),
                                     m_device->capabilities(), queue_family)) {
        return fail(VK_ERROR_INITIALIZATION_FAILED, "prepare CPU output image");
    }
    auto texture_frames = rstd::dyn<SceneTextureAnimationView>::from_ref(scene);
    if (! m_program.update(scene.Runtime().Frame(), extent, texture_frames.as_ref(), rr)) {
        return fail(VK_ERROR_INITIALIZATION_FAILED, "update render program");
    }
    auto result = rr.command.Reset();
    if (result != VK_SUCCESS) return fail(result, "reset CPU frame command buffer");
    result = rr.command.Begin(VkCommandBufferBeginInfo {
        .sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO,
        .flags = VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT,
    });
    if (result != VK_SUCCESS) return fail(result, "begin CPU frame command buffer");

    if (m_timestamp_queries) {
        rr.command.ResetQueryPool(m_timestamp_queries.get(), 0, timestamp_query_count);
        rr.command.WriteTimestamp(VK_PIPELINE_STAGE_TOP_OF_PIPE_BIT, m_timestamp_queries.get(), 0);
    }
    RecordedBufferUploads recorded_uploads;
    if (! m_program.record(rr, recorded_uploads)) {
        (void)rr.command.End();
        return fail(VK_ERROR_INITIALIZATION_FAILED, "record render program");
    }
    if (m_timestamp_queries)
        rr.command.WriteTimestamp(VK_PIPELINE_STAGE_BOTTOM_OF_PIPE_BIT, m_timestamp_queries.get(), 1);
    // FinPass writes this ordinary image. Make those transfer writes visible
    // to the copy, then make staging writes visible to the host after the fence.
    const bool coverage_frame=m_sample_coverage_enabled && m_cpu_frame_index>=m_sample_coverage_start &&
        m_cpu_frame_index-m_sample_coverage_start<m_sample_coverage_frames;
    const bool coverage_last=coverage_frame && m_cpu_frame_index-m_sample_coverage_start+1==m_sample_coverage_frames;
    const bool compact_readback=m_gpu_samples && (read_pixels || coverage_frame);
    VkImageMemoryBarrier to_readback {
        .sType = VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER,
        .srcAccessMask = VK_ACCESS_TRANSFER_WRITE_BIT,
        .dstAccessMask = compact_readback ? VK_ACCESS_SHADER_READ_BIT : VK_ACCESS_TRANSFER_READ_BIT,
        .oldLayout = VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL,
        .newLayout = compact_readback ? VK_IMAGE_LAYOUT_GENERAL : VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL,
        .srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
        .dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
        .image = *m_cpu_image.handle,
        .subresourceRange = { VK_IMAGE_ASPECT_COLOR_BIT, 0, 1, 0, 1 },
    };
    rr.command.PipelineBarrier(VK_PIPELINE_STAGE_TRANSFER_BIT,
                                compact_readback ? VK_PIPELINE_STAGE_COMPUTE_SHADER_BIT : VK_PIPELINE_STAGE_TRANSFER_BIT,
                                0, to_readback);
    VkBufferImageCopy region {
        .bufferOffset = 0,
        .bufferRowLength = 0,
        .bufferImageHeight = 0,
        .imageSubresource = { VK_IMAGE_ASPECT_COLOR_BIT, 0, 0, 1 },
        .imageOffset = { 0, 0, 0 },
        .imageExtent = { extent.width, extent.height, 1 },
    };
    // Simulation, draw calls and resource completion still run on every frame.
    // Sparse sampling skips only the device-to-host copy and CPU pixel materialization.
    if (compact_readback) {
        if (coverage_frame) {
            const bool first_coverage=m_cpu_frame_index==m_sample_coverage_start;
            if (first_coverage) {
                const std::array<std::uint32_t,8> empty {extent.width,extent.height,0,0,255,0,0,0};
                vkCmdUpdateBuffer(*rr.command,*m_sample_coverage.handle,0,sizeof(empty),empty.data());
            }
            VkBufferMemoryBarrier ready { .sType=VK_STRUCTURE_TYPE_BUFFER_MEMORY_BARRIER,
                .srcAccessMask=first_coverage ? VK_ACCESS_TRANSFER_WRITE_BIT : VK_ACCESS_SHADER_WRITE_BIT,
                .dstAccessMask=VK_ACCESS_SHADER_READ_BIT|VK_ACCESS_SHADER_WRITE_BIT,
                .srcQueueFamilyIndex=VK_QUEUE_FAMILY_IGNORED,.dstQueueFamilyIndex=VK_QUEUE_FAMILY_IGNORED,
                .buffer=*m_sample_coverage.handle,.offset=0,.size=32 };
            rr.command.PipelineBarrier(first_coverage ? VK_PIPELINE_STAGE_TRANSFER_BIT : VK_PIPELINE_STAGE_COMPUTE_SHADER_BIT,
                VK_PIPELINE_STAGE_COMPUTE_SHADER_BIT,0,ready);
        }
        rr.command.BindPipeline(VK_PIPELINE_BIND_POINT_COMPUTE, *m_sample_pipeline);
        const VkDescriptorImageInfo image { .imageView = *m_sample_view, .imageLayout = VK_IMAGE_LAYOUT_GENERAL };
        const VkDescriptorBufferInfo buffer { .buffer = *m_cpu_staging.handle, .offset = 0, .range = m_cpu_staging.req_size };
        const VkWriteDescriptorSet image_write {
            .sType = VK_STRUCTURE_TYPE_WRITE_DESCRIPTOR_SET, .dstBinding = 0,
            .descriptorCount = 1, .descriptorType = VK_DESCRIPTOR_TYPE_STORAGE_IMAGE, .pImageInfo = &image };
        const VkWriteDescriptorSet buffer_write {
            .sType = VK_STRUCTURE_TYPE_WRITE_DESCRIPTOR_SET, .dstBinding = 1,
            .descriptorCount = 1, .descriptorType = VK_DESCRIPTOR_TYPE_STORAGE_BUFFER, .pBufferInfo = &buffer };
        rr.command.PushDescriptorSetKHR(VK_PIPELINE_BIND_POINT_COMPUTE, *m_sample_layout, 0, image_write);
        rr.command.PushDescriptorSetKHR(VK_PIPELINE_BIND_POINT_COMPUTE, *m_sample_layout, 0, buffer_write);
        const VkDescriptorBufferInfo coverage_buffer { .buffer=m_sample_coverage_enabled ? *m_sample_coverage.handle : *m_cpu_staging.handle,
            .offset=0,.range=m_sample_coverage_enabled ? 32 : m_cpu_staging.req_size };
        const VkWriteDescriptorSet coverage_write { .sType=VK_STRUCTURE_TYPE_WRITE_DESCRIPTOR_SET,.dstBinding=2,
            .descriptorCount=1,.descriptorType=VK_DESCRIPTOR_TYPE_STORAGE_BUFFER,.pBufferInfo=&coverage_buffer };
        rr.command.PushDescriptorSetKHR(VK_PIPELINE_BIND_POINT_COMPUTE,*m_sample_layout,0,coverage_write);
        // Coverage-only frames need no thumbnail averages. Distribute their
        // pixels across the full grid instead of serially scanning large cells
        // in the few threads selected by a tiny thumbnail size.
        const auto grid_width = read_pixels ? m_readback_width : extent.width;
        const auto grid_height = read_pixels ? m_readback_height : extent.height;
        const std::array<std::uint32_t, 5> dimensions { extent.width, extent.height, grid_width, grid_height,
            (read_pixels ? 1u : 0u)|(coverage_frame ? 2u : 0u) };
        rr.command.PushConstants(*m_sample_layout, VK_SHADER_STAGE_COMPUTE_BIT, dimensions);
        rr.command.Dispatch((grid_width + 7) / 8, (grid_height + 7) / 8, 1);
        // Preserve FinPass's existing end-of-frame image layout contract.
        to_readback.srcAccessMask = VK_ACCESS_SHADER_READ_BIT;
        to_readback.dstAccessMask = VK_ACCESS_TRANSFER_READ_BIT;
        to_readback.oldLayout = VK_IMAGE_LAYOUT_GENERAL;
        to_readback.newLayout = VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL;
        rr.command.PipelineBarrier(VK_PIPELINE_STAGE_COMPUTE_SHADER_BIT, VK_PIPELINE_STAGE_TRANSFER_BIT, 0, to_readback);
    } else if (read_pixels)
        rr.command.CopyImageToBuffer(*m_cpu_image.handle, VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL,
                                      *m_cpu_staging.handle, region);
    if (coverage_last) {
        VkBufferMemoryBarrier ready { .sType=VK_STRUCTURE_TYPE_BUFFER_MEMORY_BARRIER,
            .srcAccessMask=VK_ACCESS_SHADER_WRITE_BIT,.dstAccessMask=VK_ACCESS_TRANSFER_READ_BIT,
            .srcQueueFamilyIndex=VK_QUEUE_FAMILY_IGNORED,.dstQueueFamilyIndex=VK_QUEUE_FAMILY_IGNORED,
            .buffer=*m_sample_coverage.handle,.offset=0,.size=32 };
        rr.command.PipelineBarrier(VK_PIPELINE_STAGE_COMPUTE_SHADER_BIT,VK_PIPELINE_STAGE_TRANSFER_BIT,0,ready);
        const VkBufferCopy copy { .srcOffset=0,.dstOffset=m_cpu_staging.req_size-32,.size=32 };
        m_device->handle().Dispatch().vkCmdCopyBuffer(*rr.command,*m_sample_coverage.handle,*m_cpu_staging.handle,1,&copy);
    }
    if (m_timestamp_queries)
        rr.command.WriteTimestamp(VK_PIPELINE_STAGE_BOTTOM_OF_PIPE_BIT, m_timestamp_queries.get(), 2);
    VkBufferMemoryBarrier to_host {
        .sType = VK_STRUCTURE_TYPE_BUFFER_MEMORY_BARRIER,
        .srcAccessMask = (compact_readback ? VK_ACCESS_SHADER_WRITE_BIT : VK_ACCESS_TRANSFER_WRITE_BIT) |
            (coverage_last ? VK_ACCESS_TRANSFER_WRITE_BIT : 0u),
        .dstAccessMask = VK_ACCESS_HOST_READ_BIT,
        .srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
        .dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
        .buffer = *m_cpu_staging.handle,
        .offset = 0,
        .size = m_cpu_staging.req_size,
    };
    if (read_pixels || coverage_last) rr.command.PipelineBarrier(
                                (compact_readback ? VK_PIPELINE_STAGE_COMPUTE_SHADER_BIT : VK_PIPELINE_STAGE_TRANSFER_BIT) |
                                    (coverage_last ? VK_PIPELINE_STAGE_TRANSFER_BIT : 0u),
                                VK_PIPELINE_STAGE_HOST_BIT,
                                0, to_host);
    result = rr.command.End();
    if (result != VK_SUCCESS) return fail(result, "end CPU frame command buffer");
    result = rr.fence_frame.Reset();
    if (result != VK_SUCCESS) return fail(result, "reset CPU frame fence");
    VkSubmitInfo submit {
        .sType = VK_STRUCTURE_TYPE_SUBMIT_INFO,
        .commandBufferCount = 1,
        .pCommandBuffers = rr.command.address(),
    };
    m_timestamp_queries.submitted();
    result = m_device->graphics_queue().handle.Submit(submit, *rr.fence_frame);
    if (result != VK_SUCCESS) return fail(result, "submit CPU frame");
    auto completion = rr.resources.BeginSubmission(rstd::move(recorded_uploads));
    if (!completion.Valid()) return fail(VK_ERROR_INITIALIZATION_FAILED, "track submitted frame resources");
    // Sparse-search frames still draw and reduce coverage. Only the frames
    // that return pixels or the final coverage result need completion here.
    const bool defer_completion = frame.gpu_scene_overlap && !read_pixels && !coverage_last &&
        (m_gpu_encoder || m_cpu_frame_index < m_sample_coverage_start + m_sample_coverage_frames);
    if (defer_completion) m_pending_cpu_submission = completion;
    const auto cpu_submitted = m_cpu_timing_requested ? std::chrono::steady_clock::now() : std::chrono::steady_clock::time_point{};
    if (m_cpu_timing_requested)
        frame.cpu_prepare_ms = std::chrono::duration<double,std::milli>(cpu_submitted-cpu_started).count();
    if (!defer_completion) result = rr.fence_frame.Wait(m_readback_timeout_ns);
    if (m_cpu_timing_requested)
        frame.cpu_render_wait_ms = std::chrono::duration<double,std::milli>(std::chrono::steady_clock::now()-cpu_submitted).count();
    // Do not reset/reuse buffers after a timeout: the submission can still be
    // in flight. The failed renderer retains its resources until destruction.
    if (result != VK_SUCCESS) return fail(result, "wait for CPU frame fence");
    if (!defer_completion) m_timestamp_queries.completed();
    if (m_timestamp_queries) {
        struct QueryValue { std::uint64_t ticks; std::uint64_t available; };
        std::array<QueryValue, timestamp_query_count> queries {};
        const auto query_result = m_device->handle().Dispatch().vkGetQueryPoolResults(
            *m_device->handle(), m_timestamp_queries.get(), 0, timestamp_query_count,
            sizeof(queries), queries.data(), sizeof(QueryValue),
            VK_QUERY_RESULT_64_BIT | VK_QUERY_RESULT_WITH_AVAILABILITY_BIT);
        frame.gpu_timing_error_code = query_result;
        if (query_result == VK_ERROR_DEVICE_LOST)
            return fail(query_result, "read GPU timestamp queries");
        if (query_result != VK_SUCCESS) {
            frame.gpu_timing_message = "GPU timestamp query results are unavailable after the frame fence";
        } else if (!queries[0].available || !queries[1].available || !queries[2].available) {
            frame.gpu_timing_error_code = VK_NOT_READY;
            frame.gpu_timing_message = "GPU timestamp query availability is incomplete after the frame fence";
        } else {
            const double milliseconds_per_tick = *m_timestamp_period_ns / 1'000'000.0;
            // Modulo subtraction handles a counter wrap. As with Vulkan
            // timestamp intervals generally, the span must be shorter than
            // one complete timestamp counter period.
            frame.gpu_total_ms = TimestampDelta(queries[0].ticks, queries[2].ticks,
                                                m_timestamp_valid_bits) * milliseconds_per_tick;
            frame.gpu_draw_ms = TimestampDelta(queries[0].ticks, queries[1].ticks,
                                               m_timestamp_valid_bits) * milliseconds_per_tick;
        }
    }
    if (!defer_completion && rr.resources.CompleteSubmission(completion).is_none()) {
        return fail(VK_ERROR_INITIALIZATION_FAILED, "track CPU frame resource completion");
    }
    if (!defer_completion) ReleaseCompletedRetiredResources(*m_device, rr);

    if (m_gpu_encoder && m_cpu_frame_index >= m_encode_options->first_frame) {
        const auto encode_started = m_cpu_timing_requested ? std::chrono::steady_clock::now() : std::chrono::steady_clock::time_point{};
        const auto index = m_cpu_frame_index - m_encode_options->first_frame;
        if (index < m_encode_options->frames) {
            try {
                m_gpu_encoder->encode(*m_cpu_image.handle, index, defer_completion);
                if (index + 1 == m_encode_options->frames) {
                    m_gpu_encoder->finish();
                    if (const auto error = finishPendingFrame(); !error.empty())
                        return fail(VK_ERROR_INITIALIZATION_FAILED, error);
                    frame.gpu_capture_metadata = m_gpu_encoder->captureMetadata();
                    frame.gpu_readback_frames = m_gpu_encoder->readbackFrames();
                }
            } catch (const std::exception& error) {
                return fail(VK_ERROR_INITIALIZATION_FAILED, error.what());
            }
        }
        if (m_cpu_timing_requested)
            frame.cpu_encode_ms = std::chrono::duration<double,std::milli>(std::chrono::steady_clock::now()-encode_started).count();
    }

    if (coverage_frame) ++m_sample_coverage_observed;
    if (read_pixels || coverage_last) {
        void* mapped = nullptr;
        result = m_cpu_staging.handle.MapMemory(&mapped);
        if (result != VK_SUCCESS) return fail(result, "map CPU staging buffer");
        result = vmaInvalidateAllocation(m_device->vma_allocator(),
                                          m_cpu_staging.handle.Allocation(), 0, VK_WHOLE_SIZE);
        if (result != VK_SUCCESS) {
            m_cpu_staging.handle.UnMapMemory();
            return fail(result, "invalidate CPU staging buffer");
        }
        if (read_pixels && m_sample_readback && !m_gpu_samples) {
            // Same integer box mean as the GPU kernel and Baker's existing CPU sampler.
            // This also covers devices without storage-image/compute support.
            const auto* rgba = static_cast<const std::uint8_t*>(mapped);
            for (std::uint32_t sy = 0; sy < m_readback_height; ++sy)
                for (std::uint32_t sx = 0; sx < m_readback_width; ++sx) {
                    const auto left = sx * extent.width / m_readback_width;
                    const auto right = std::max(left + 1, (sx + 1) * extent.width / m_readback_width);
                    const auto top = sy * extent.height / m_readback_height;
                    const auto bottom = std::max(top + 1, (sy + 1) * extent.height / m_readback_height);
                    std::array<std::uint64_t, 4> sum {};
                    for (auto y = top; y < bottom; ++y)
                        for (auto x = left; x < right; ++x)
                            for (unsigned channel = 0; channel < 4; ++channel)
                                sum[channel] += rgba[(y * extent.width + x) * 4 + channel];
                    const auto count = (right - left) * (bottom - top);
                    for (unsigned channel = 0; channel < 4; ++channel)
                        frame.pixels[(sy * m_readback_width + sx) * 4 + channel] =
                            static_cast<std::uint8_t>((sum[channel] + count / 2) / count);
                }
        } else if (read_pixels) std::memcpy(frame.pixels.data(), mapped, frame.pixels.size());
        if (coverage_last) {
            std::array<std::uint32_t,8> bounds;
            std::memcpy(bounds.data(),static_cast<const std::uint8_t*>(mapped)+m_cpu_staging.req_size-32,32);
            const bool content=bounds[7]!=0;
            std::ostringstream report;
            report << "{\"status\":\"complete\",\"first_simulation_frame\":" << m_sample_coverage_start
                << ",\"frames\":" << m_sample_coverage_observed << ",\"capture_width\":" << extent.width
                << ",\"capture_height\":" << extent.height << ",\"includes_rgb\":true,\"has_content\":" << (content ? "true" : "false")
                << ",\"x\":" << (content ? bounds[0] : 0) << ",\"y\":" << (content ? bounds[1] : 0)
                << ",\"width\":" << (content ? bounds[2]-bounds[0]+1 : 0) << ",\"height\":" << (content ? bounds[3]-bounds[1]+1 : 0)
                << ",\"minimum_alpha\":" << bounds[4] << ",\"maximum_alpha\":" << bounds[5]
                << ",\"basis\":\"Every full-resolution output frame reduced on GPU, including frames not selected for sampling.\"}";
            frame.sampling_coverage=report.str();
        }
        m_cpu_staging.handle.UnMapMemory();
    }
    frame.video_decoders = rr.resources.ObserveVideoDecoders();
    frame.status = m_pending_cpu_submission.Valid() ? CpuFrameStatus::Submitted : CpuFrameStatus::Completed;
    ++m_cpu_frame_index;
    return frame;
}

void VulkanRender::Impl::recycleCpuPixels(std::vector<std::uint8_t>&& buffer) {
    if (buffer.capacity() >= m_cpu_pixels_pool.capacity()) m_cpu_pixels_pool = rstd::move(buffer);
}

void VulkanRender::Impl::drawFrameSwapchain(Scene& scene) {
    static std::size_t resource_index = 0;

    RenderingResources& rr    = m_rendering_resources;
    resource_index            = (resource_index + 1) % 3;
    std::uint32_t image_index = 0;
    {
        VVK_CHECK_VOID_RE(m_device->handle().AcquireNextImageKHR(*m_device->swapchain().handle(),
                                                                 vk_wait_time,
                                                                 *rr.sem_swap_wait_image,
                                                                 {},
                                                                 &image_index));
    }
    const auto& image          = m_device->swapchain().images()[image_index];
    const u64   acquire_serial = m_next_surface_acquire_serial++;
    if (acquire_serial == u64()) {
        rstd_error("window frame surface acquire serial overflow");
        return;
    }
    owe::FrameSurfaceLease frame_surface {
        .identity             = { .owner_generation = u64(1),
                                  .image_index      = u32(image_index),
                                  .acquire_serial   = acquire_serial },
        .reuse                = { .kind = owe::FrameSurfaceReuseKind::PresentationAcquired },
        .image                = image,
        .format               = m_device->swapchain().format(),
        .initial_layout       = VK_IMAGE_LAYOUT_PRESENT_SRC_KHR,
        .initial_queue_family = m_device->graphics_queue().family_index,
        .acquire              = { .kind      = owe::FrameSurfaceAcquireKind::BinarySemaphore,
                                  .semaphore = *rr.sem_swap_wait_image },
        .final_layout         = VK_IMAGE_LAYOUT_PRESENT_SRC_KHR,
        .final_queue_family   = m_device->present_queue().family_index,
        .discard_content      = true,
    };
    auto external_preparer =
        rstd::dyn<resource_registry::ExternalResourcePreparer>::from_ref(rr.resources);
    if (! m_finpass->setFrameSurface(std::move(frame_surface),
                                     external_preparer.as_mut_ref(),
                                     m_device->capabilities(),
                                     m_device->graphics_queue().family_index)) {
        rstd_error("window frame surface lease rejected");
        return;
    }
    if (! waitForPreparedUploads(rr)) return;
    auto texture_frames = rstd::dyn<SceneTextureAnimationView>::from_ref(scene);
    if (! m_program.update(
            scene.Runtime().Frame(), m_device->out_extent(), texture_frames.as_ref(), rr))
        return;

    (void)rr.command.Begin(VkCommandBufferBeginInfo {
        .sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO,
        .pNext = nullptr,
        .flags = VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT,
    });
    RecordedBufferUploads recorded_uploads;
    if (! m_program.record(rr, recorded_uploads)) {
        (void)rr.command.End();
        return;
    }
    (void)rr.command.End();

    auto& sem_present_done = m_sem_swap_finish_per_image[image_index];

    // Swapchain image is only written via FinPass blit/copy (TRANSFER).
    // Waiting at COLOR_ATTACHMENT_OUTPUT lets the layout transition + transfer
    // race the presentation engine's read → sync-validation WRITE_AFTER_READ.
    auto                        pending_upload = rr.resources.PendingUpload();
    const bool                  wait_upload    = pending_upload.is_some();
    rstd::array<VkSemaphore, 2> wait_semaphores {
        *rr.sem_swap_wait_image,
        *rr.sem_upload,
    };
    rstd::array<VkPipelineStageFlags, 2> wait_stages {
        VK_PIPELINE_STAGE_TRANSFER_BIT,
        vk_upload_wait_stages,
    };
    rstd::array<std::uint64_t, 2> wait_values {
        std::uint64_t { 0 },
        wait_upload ? pending_upload->value.to_primitive() : std::uint64_t { 0 },
    };
    rstd::array<std::uint64_t, 1> signal_values { std::uint64_t { 0 } };
    VkTimelineSemaphoreSubmitInfo timeline_info {
        .sType                     = VK_STRUCTURE_TYPE_TIMELINE_SEMAPHORE_SUBMIT_INFO,
        .pNext                     = nullptr,
        .waitSemaphoreValueCount   = wait_upload ? 2u : 0u,
        .pWaitSemaphoreValues      = wait_upload ? wait_values.data() : nullptr,
        .signalSemaphoreValueCount = wait_upload ? 1u : 0u,
        .pSignalSemaphoreValues    = wait_upload ? signal_values.data() : nullptr,
    };
    VkSubmitInfo sub_info {
        .sType                = VK_STRUCTURE_TYPE_SUBMIT_INFO,
        .pNext                = wait_upload ? &timeline_info : nullptr,
        .waitSemaphoreCount   = wait_upload ? 2u : 1u,
        .pWaitSemaphores      = wait_semaphores.data(),
        .pWaitDstStageMask    = wait_stages.data(),
        .commandBufferCount   = 1,
        .pCommandBuffers      = rr.command.address(),
        .signalSemaphoreCount = 1,
        .pSignalSemaphores    = sem_present_done.address(),
    };

    VVK_CHECK_VOID_RE(m_device->present_queue().handle.Submit(sub_info, *rr.fence_frame));
    auto submission_completion = rr.resources.BeginSubmission(rstd::move(recorded_uploads));
    if (! submission_completion.Valid()) {
        rstd_error("track frame submission failed");
    }
    VkPresentInfoKHR present_info {
        .sType              = VK_STRUCTURE_TYPE_PRESENT_INFO_KHR,
        .pNext              = nullptr,
        .waitSemaphoreCount = 1,
        .pWaitSemaphores    = sem_present_done.address(),
        .swapchainCount     = 1,
        .pSwapchains        = m_device->swapchain().handle().address(),
        .pImageIndices      = &image_index,
    };
    VVK_CHECK_VOID_RE(m_device->present_queue().handle.Present(present_info));

    VVK_CHECK_VOID_RE(rr.fence_frame.Wait(vk_wait_time));
    if (submission_completion.Valid() &&
        rr.resources.CompleteSubmission(submission_completion).is_some()) {
        ReleaseCompletedRetiredResources(*m_device, rr);
    }
    if (pending_upload.is_some()) {
        rr.resources.CompleteUploadsThrough(pending_upload->value);
    }
    VVK_CHECK_VOID_RE(rr.fence_frame.Reset());
}
void VulkanRender::Impl::drawFrameOffscreen(Scene& scene) {
    if (! m_ex_swapchain) return;

    // Drain any pending bridge directive *before* committing to a slot.
    // Previous frame's GPU work has fenced at the tail of the last
    // drawFrameOffscreen, so the cmd pool is idle.
    m_ex_swapchain->poll();

    // Skip until both the swapchain has slots and the scene has loaded
    // (FinPass.prepare runs from compileRenderGraph). FinPass itself is
    // format-agnostic now — vkCmdBlitImage handles cross-format channel
    // mapping, no rebuild needed on renegotiation.
    if (! m_ex_swapchain->ready() || ! m_finpass->prepared()) {
        return;
    }

    RenderingResources& rr                    = m_rendering_resources;
    auto                frame_surface_acquire = m_ex_swapchain->acquireRenderTarget();
    if (! frame_surface_acquire.acquired()) {
        if (frame_surface_acquire.status == owe::FrameSurfaceAcquireStatus::ProtocolError) {
            rstd_error("offscreen frame surface acquisition failed: {}",
                       frame_surface_acquire.error_code);
        }
        return;
    }

    auto external_preparer =
        rstd::dyn<resource_registry::ExternalResourcePreparer>::from_ref(rr.resources);
    if (! m_finpass->setFrameSurface(frame_surface_acquire.lease,
                                     external_preparer.as_mut_ref(),
                                     m_device->capabilities(),
                                     m_device->graphics_queue().family_index)) {
        rstd_error("offscreen frame surface lease rejected");
        return;
    }
    if (! waitForPreparedUploads(rr)) return;
    auto texture_frames = rstd::dyn<SceneTextureAnimationView>::from_ref(scene);
    if (! m_program.update(
            scene.Runtime().Frame(), m_device->out_extent(), texture_frames.as_ref(), rr))
        return;

    (void)rr.command.Begin(VkCommandBufferBeginInfo {
        .sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO,
        .pNext = nullptr,
        .flags = VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT,
    });
    RecordedBufferUploads recorded_uploads;
    if (! m_program.record(rr, recorded_uploads)) {
        (void)rr.command.End();
        return;
    }

    (void)rr.command.End();

    auto                        pending_upload = rr.resources.PendingUpload();
    const bool                  wait_upload    = pending_upload.is_some();
    rstd::array<VkSemaphore, 1> wait_semaphores {
        *rr.sem_upload,
    };
    rstd::array<VkPipelineStageFlags, 1> wait_stages {
        vk_upload_wait_stages,
    };
    rstd::array<std::uint64_t, 1> wait_values {
        wait_upload ? pending_upload->value.to_primitive() : std::uint64_t { 0 },
    };
    rstd::array<std::uint64_t, 1> signal_values { std::uint64_t { 0 } };
    VkTimelineSemaphoreSubmitInfo timeline_info {
        .sType                     = VK_STRUCTURE_TYPE_TIMELINE_SEMAPHORE_SUBMIT_INFO,
        .pNext                     = nullptr,
        .waitSemaphoreValueCount   = wait_upload ? 1u : 0u,
        .pWaitSemaphoreValues      = wait_upload ? wait_values.data() : nullptr,
        .signalSemaphoreValueCount = wait_upload ? 1u : 0u,
        .pSignalSemaphoreValues    = wait_upload ? signal_values.data() : nullptr,
    };
    VkSubmitInfo sub_info {
        .sType                = VK_STRUCTURE_TYPE_SUBMIT_INFO,
        .pNext                = wait_upload ? &timeline_info : nullptr,
        .waitSemaphoreCount   = wait_upload ? 1u : 0u,
        .pWaitSemaphores      = wait_upload ? wait_semaphores.data() : nullptr,
        .pWaitDstStageMask    = wait_upload ? wait_stages.data() : nullptr,
        .commandBufferCount   = 1,
        .pCommandBuffers      = rr.command.address(),
        .signalSemaphoreCount = 1,
        .pSignalSemaphores    = rr.sem_export.address(),
    };
    VVK_CHECK_VOID_RE(m_device->graphics_queue().handle.Submit(sub_info, *rr.fence_frame));
    auto submission_completion = rr.resources.BeginSubmission(rstd::move(recorded_uploads));
    if (! submission_completion.Valid()) {
        rstd_error("track offscreen submission failed");
    }

    VVK_CHECK_VOID_RE(rr.fence_frame.Wait(vk_wait_time));
    if (submission_completion.Valid() &&
        rr.resources.CompleteSubmission(submission_completion).is_some()) {
        ReleaseCompletedRetiredResources(*m_device, rr);
    }
    if (pending_upload.is_some()) {
        rr.resources.CompleteUploadsThrough(pending_upload->value);
    }
    VVK_CHECK_VOID_RE(rr.fence_frame.Reset());

    // Export the signaled semaphore as a dma_fence sync_file fd and hand
    // it to the swapchain along with the slot. LocalExSwapchain stashes
    // it for the host's takeLastFrameSyncFd; BridgeExSwapchain forwards
    // it to ww_bridge_pool_submit_slot.
    //
    // Diagnostics: sync_fd export failure is silent in production but
    // the result is the consumer reading a buffer the producer hasn't
    // finished writing — exactly the "blank frame" symptom. We log the
    // first failure loudly and rate-limit subsequent ones.
    int sync_fd = -1;
    {
        VkSemaphoreGetFdInfoKHR gi {
            .sType      = VK_STRUCTURE_TYPE_SEMAPHORE_GET_FD_INFO_KHR,
            .pNext      = nullptr,
            .semaphore  = *rr.sem_export,
            .handleType = VK_EXTERNAL_SEMAPHORE_HANDLE_TYPE_SYNC_FD_BIT_KHR,
        };
        VkResult vr = m_device->handle().GetSemaphoreFdKHR(gi, &sync_fd);
        if (vr != VK_SUCCESS) {
            static std::atomic<std::uint64_t> n_failed { 0 };
            std::uint64_t                     k = n_failed.fetch_add(1, std::memory_order_relaxed);
            if (k == 0 || (k & (k - 1)) == 0) { // 1st, 2nd, 4th, 8th...
                rstd_error("VulkanRender: vkGetSemaphoreFdKHR failed (vr={}, count={})",
                           (int)vr,
                           (unsigned long long)(k + 1));
            }
            sync_fd = -1;
        } else if (sync_fd < 0) {
            // The driver returned VK_SUCCESS with fd=-1 — spec says this
            // means "the semaphore was unsignaled". Should not happen
            // because we just waited the fence; flag loudly.
            static std::atomic<std::uint64_t> n_unsig { 0 };
            std::uint64_t                     k = n_unsig.fetch_add(1, std::memory_order_relaxed);
            if (k == 0 || (k & (k - 1)) == 0) {
                rstd_error("VulkanRender: GetSemaphoreFdKHR returned fd=-1 "
                           "(semaphore not signaled? count={})",
                           (unsigned long long)(k + 1));
            }
        }
    }

    auto completion = frame_surface_acquire.completion.Submit(sync_fd);
    if (completion.status != owe::FrameSurfaceCompletionStatus::Submitted &&
        completion.status != owe::FrameSurfaceCompletionStatus::SessionLost) {
        rstd_error("offscreen frame surface completion failed: status={}, error={}",
                   static_cast<int>(completion.status),
                   completion.error_code);
    }
}

bool VulkanRender::Impl::onSwapchainReady(unsigned width, unsigned height) {
    if (! m_inited) return false;
    auto& cur            = m_device->out_extent();
    bool  extent_changed = (width != cur.width) || (height != cur.height);
    if (! extent_changed) {
        // Format-only changes flow through ExSwapchain::format() and
        // are handled by drawFrameOffscreen's head check.
        return false;
    }
    if (! waitForPreparedUploads(m_rendering_resources)) return false;
    m_device->set_out_extent(VkExtent2D { width, height });
    return true;
}

void VulkanRender::Impl::UpdateCameraFillMode(owe::Scene& scene, owe::FillMode fillmode) {
    using namespace owe;
    auto width  = m_device->out_extent().width;
    auto height = m_device->out_extent().height;

    if (width == 0) return;
    auto   projection_extent = scene.OrthographicProjectionExtent();
    double sw = projection_extent[usize()], sh = projection_extent[usize(1)];
    double fboAspect = width / (double)height, sAspect = sw / sh;
    auto   global      = scene.CameraMut("global"_str);
    auto   perspective = scene.CameraMut("global_perspective"_str);
    if (global.is_none() || perspective.is_none()) return;
    auto& gCam    = **global;
    auto& gPerCam = **perspective;
    if (m_orthographic_capture_viewport.has_value()) {
        std::string error;
        if (!ApplyOrthographicCaptureViewport(scene, error)) return;
        scene.CaptureCameraPathViewports();
        return;
    }
    // assum cam
    switch (fillmode) {
    case FillMode::STRETCH:
        gCam.SetWidth(sw);
        gCam.SetHeight(sh);
        gPerCam.SetAspect(sAspect);
        if (! gPerCam.IsLookAt())
            gPerCam.SetFov(algorism::CalculatePersperctiveFov(1000.0f, gCam.Height()));
        break;
    case FillMode::ASPECTFIT:
        if (fboAspect < sAspect) {
            // scale height
            gCam.SetWidth(sw);
            gCam.SetHeight(sw / fboAspect);
        } else {
            gCam.SetWidth(sh * fboAspect);
            gCam.SetHeight(sh);
        }
        gPerCam.SetAspect(fboAspect);
        if (! gPerCam.IsLookAt())
            gPerCam.SetFov(algorism::CalculatePersperctiveFov(1000.0f, gCam.Height()));
        break;
    case FillMode::ASPECTCROP:
    default:
        if (fboAspect > sAspect) {
            // scale height
            gCam.SetWidth(sw);
            gCam.SetHeight(sw / fboAspect);
        } else {
            gCam.SetWidth(sh * fboAspect);
            gCam.SetHeight(sh);
        }
        gPerCam.SetAspect(fboAspect);
        if (! gPerCam.IsLookAt())
            gPerCam.SetFov(algorism::CalculatePersperctiveFov(1000.0f, gCam.Height()));
        break;
    }
    gCam.Update();
    gPerCam.Update();
    scene.UpdateLinkedCamera("global"_str);
    scene.CaptureCameraPathViewports();
}

bool VulkanRender::Impl::ApplyOrthographicCaptureViewport(Scene& scene, std::string& error) {
    if (!m_orthographic_capture_viewport.has_value()) return true;

    auto active = scene.ActiveCamera();
    auto global = scene.CameraMut("global"_str);
    if (active.is_none() || global.is_none() || (*active)->IsPerspective() ||
        (*active).as_raw_ptr() != (*global).as_raw_ptr()) {
        error = "orthographic capture viewport rejected: the primary active camera is not orthographic global";
    } else {
        auto camera_node = (**global).GetAttachedNode();
        if (camera_node.is_none()) {
            error = "orthographic capture viewport rejected: global camera has no attached node";
        } else {
            const auto& viewport = *m_orthographic_capture_viewport;
            auto position = (*camera_node)->Translate();
            position.x() = static_cast<float>(viewport.center_x);
            position.y() = static_cast<float>(viewport.center_y);
            (*camera_node)->SetTranslate(position);
            (**global).SetWidth(viewport.width);
            (**global).SetHeight(viewport.height);
            (**global).Update();
            scene.UpdateLinkedCamera("global"_str);
            return true;
        }
    }

    if (!m_orthographic_capture_viewport_rejected) {
        rstd_error("{}", error);
        m_orthographic_capture_viewport_rejected = true;
    }
    return false;
}

void VulkanRender::Impl::clearLastRenderGraph(RenderGraphResourceRetention retention) {
    m_program.destroyPasses(*m_device);
    ReleaseCompletedRetiredResources(*m_device, m_rendering_resources);
    m_program.clear();
    m_rendering_resources.resources.ClearPreparedGraphics();
    if (retention == RenderGraphResourceRetention::ReleaseSceneTextures) {
        m_rendering_resources.resources.ClearTextures();
        m_shader_reflection_cache.Clear();
    } else {
        m_rendering_resources.resources.ClearTransientTextures();
    }
    m_rendering_resources.resources.EvictUnusedBuffers();
}

void VulkanRender::Impl::configureRenderTargets(Scene& scene) {
    if (! m_inited) return;
    const auto&      limits = m_device->limits();
    const VkExtent2D max_framebuffer_extent {
        std::min(limits.maxImageDimension2D, limits.maxFramebufferWidth),
        std::min(limits.maxImageDimension2D, limits.maxFramebufferHeight),
    };
    m_program.finalizeRenderTargetSizes(
        scene, m_device->out_extent(), max_framebuffer_extent, m_msaa_samples,
        m_effect_render_scale, !m_effect_render_scale_reported, m_match_effect_resolution);
    if (!scene.RenderTargetNames().is_empty()) m_effect_render_scale_reported = true;
}

void VulkanRender::Impl::compileRenderGraph(Scene& scene, rg::RenderGraph& rg) {
    auto render_scene = ExtractRenderSceneSnapshot(scene);
    compileRenderGraph(scene, rg, render_scene);
}

bool VulkanRender::Impl::prepareProgram(Scene& scene, const RenderSceneSnapshot& render_scene,
                                        resource::ResourcePlanSections sections) {
    auto status = m_program.beginPrepare(
        scene, *m_device, m_rendering_resources, render_scene, sections);
    while (status == RenderProgramPrepareStatus::BatchReady) {
        if (! commitPreparedUploads()) {
            m_program.abortPrepare(m_rendering_resources);
            return false;
        }
        status = m_program.continuePrepare(scene, *m_device, m_rendering_resources);
    }
    if (status == RenderProgramPrepareStatus::Failed) {
        m_program.abortPrepare(m_rendering_resources);
        return false;
    }

    {
        m_program.rebuildScopes();
    }
    if (! commitPreparedUploads()) {
        m_program.abortPrepare(m_rendering_resources);
        return false;
    }
    m_rendering_resources.resources.CommitPreparePlan();
    m_program.loaded = true;
    return true;
}

void VulkanRender::Impl::compileRenderGraph(Scene& scene, rg::RenderGraph& rg,
                                            const RenderSceneSnapshot& render_scene) {
    if (! m_inited) return;
    m_program.loaded     = false;
    m_capture_binding.reset();
    m_capture_error.clear();

    {
        if (! m_program.buildFromGraph(rg)) {
            rstd_error("compile render graph failed: dependency cycle");
            return;
        }
        Option<rg::NodeHandle> capture_after;
        if (m_capture_target.has_value()) {
            m_capture_binding = ResolveCaptureBinding(scene, rg, *m_capture_target, m_capture_error);
            if (!m_capture_binding.has_value()) {
                rstd_error("capture selection failed: {}", m_capture_error);
                m_program.clear();
                return;
            }
            m_finpass->setGraphSource(m_capture_binding->render_target,
                                       Some(m_capture_binding->texture_use));
            capture_after = Some(m_capture_binding->writer);
        } else {
            m_finpass->setGraphSource(rstd::cppstd::to_string(owe::SpecTex_Default), None());
        }
        if (!m_program.injectFramePasses(*m_prepass, *m_finpass, capture_after)) {
            m_capture_error = "capture writer is absent from the compiled pass order";
            rstd_error("{}", m_capture_error);
            m_program.clear();
            return;
        }
    }

    {
        configureRenderTargets(scene);
        m_program.finalizeFramePassRequests(scene);
        m_program.finalizeResourceRequests(scene);
    }
    (void)prepareProgram(scene, render_scene, resource::ResourcePlanAll);
};

void VulkanRender::Impl::refreshPreparedResources(Scene& scene) {
    auto render_scene = ExtractRenderSceneSnapshot(scene);
    refreshPreparedResources(scene, render_scene);
}

void VulkanRender::Impl::refreshPreparedResources(Scene&                     scene,
                                                  const RenderSceneSnapshot& render_scene) {
    refreshPreparedResources(scene, render_scene, resource::ResourcePlanAll);
}

void VulkanRender::Impl::refreshPreparedTextures(Scene&                     scene,
                                                 const RenderSceneSnapshot& render_scene) {
    refreshPreparedResources(scene, render_scene, resource::ResourcePlanTextures);
}

void VulkanRender::Impl::refreshPreparedResources(Scene&                         scene,
                                                  const RenderSceneSnapshot&     render_scene,
                                                  resource::ResourcePlanSections sections) {
    if (! m_inited || m_program.pass_records.is_empty()) return;

    configureRenderTargets(scene);
    m_program.finalizeFramePassRequests(scene);
    m_program.finalizeResourceRequests(scene);
    (void)prepareProgram(scene, render_scene, sections);
}

void VulkanRender::Impl::invalidatePreparedRenderItems(slice<owe::RenderItemId> render_items,
                                                       PassInvalidationFlags    flags) {
    if (! m_inited) return;
    m_program.invalidateRenderItems(render_items, flags);
}

void VulkanRender::Impl::refreshPreparedRenderItems(Scene&                     scene,
                                                    const RenderSceneSnapshot& render_scene,
                                                    slice<owe::RenderItemId>   render_items,
                                                    PassInvalidationFlags      flags) {
    invalidatePreparedRenderItems(render_items, flags);
    refreshPreparedResources(scene, render_scene);
}

void VulkanRender::Impl::refreshPreparedMaterial(Scene&                     scene,
                                                 const RenderSceneSnapshot& render_scene,
                                                 owe::SceneMaterialId       material,
                                                 PassInvalidationFlags      flags) {
    refreshPreparedRenderItems(scene, render_scene, render_scene.renderItemsFor(material), flags);
}

bool VulkanRender::Impl::refreshPreparedMaterialTextures(Scene&                     scene,
                                                         const RenderSceneSnapshot& render_scene,
                                                         owe::SceneMaterialId       material) {
    rstd::array<owe::SceneMaterialId, 1> materials { material };
    return refreshPreparedMaterialTextures(scene, render_scene, materials.as_slice());
}

bool VulkanRender::Impl::refreshPreparedMaterialTextures(Scene&                      scene,
                                                         const RenderSceneSnapshot&  render_scene,
                                                         slice<owe::SceneMaterialId> materials) {
    if (! m_inited || m_program.pass_records.is_empty()) return true;
    auto render_items = RenderItemsForMaterials(render_scene, materials);
    bool requires_graph_rebuild =
        m_program.refreshMaterialTextureBindings(render_scene, render_items.as_slice());
    if (requires_graph_rebuild) return false;
    refreshPreparedResources(scene, render_scene);
    return true;
}

void VulkanRender::Impl::refreshPreparedMesh(Scene& scene, const RenderSceneSnapshot& render_scene,
                                             owe::SceneMeshId mesh, PassInvalidationFlags flags) {
    refreshPreparedRenderItems(scene, render_scene, render_scene.renderItemsFor(mesh), flags);
}

std::vector<PreparedPassDiagnostic> VulkanRender::Impl::preparedPassDiagnostics() const {
    return m_program.diagnostics();
}
