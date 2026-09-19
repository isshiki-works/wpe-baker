module;
#include <rstd/macro.hpp>

#include "vvk/macros.hpp"

#include <cerrno>
#include <cmath>
#include <vulkan/vulkan.h>

module wescene.vulkan_render;
import wescene.core;
import wescene.types;
import rstd.log;
import rstd.cppstd;
import wescene.load_bench;
import wescene.resource_registry;
import wescene.vulkan;
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
    auto memory = rstd::dyn<owe::vulkan::MemoryBudgetSource>::from_ref(device);
    rr.resources.Collect(memory.as_mut_ref());
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

    bool init(RenderInitInfo, SceneLoadBenchRecorderView);
    void destroy();

    void drawFrame(Scene&);
    CpuFrameResult drawFrameCpu(Scene&, bool read_pixels);
    void recycleCpuPixels(std::vector<std::uint8_t>&&);
    bool initCpuReadback(const RenderInitInfo&);
    void initGpuTiming(const RenderInitInfo&);

    bool CreateRenderingResource(RenderingResources&);
    void DestroyRenderingResource(RenderingResources&);

    void clearLastRenderGraph(RenderGraphResourceRetention);
    void configureRenderTargets(Scene&);
    void compileRenderGraph(Scene&, rg::RenderGraph&);
    void compileRenderGraph(Scene&, rg::RenderGraph&, const RenderSceneSnapshot&);
    void compileRenderGraph(Scene&, rg::RenderGraph&, const RenderSceneSnapshot&,
                            SceneLoadBenchRecorderView);
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
    bool                      commitPreparedUploads(SceneLoadBenchRecorderView load_bench = {});
    bool prepareProgram(Scene&, const RenderSceneSnapshot&, resource::ResourcePlanSections,
                        SceneLoadBenchRecorderView = {});
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
    SceneLoadBenchRecorderView m_pending_load_bench;

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
    std::optional<RenderCaptureTarget> m_capture_target;
    std::optional<OrthographicCaptureViewport> m_orthographic_capture_viewport;
    bool m_orthographic_capture_viewport_rejected { false };
    std::optional<CaptureBinding> m_capture_binding;
    std::string m_capture_error;
    TimestampQueryPool m_timestamp_queries;
    bool m_gpu_timing_requested { false };
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

bool VulkanRender::init(RenderInitInfo info, SceneLoadBenchRecorderView load_bench) {
    return pImpl->init(rstd::move(info), load_bench);
}
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
void VulkanRender::compileRenderGraph(Scene& scene, rg::RenderGraph& rg,
                                      const RenderSceneSnapshot& render_scene,
                                      SceneLoadBenchRecorderView load_bench) {
    pImpl->compileRenderGraph(scene, rg, render_scene, load_bench);
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

bool VulkanRender::Impl::init(RenderInitInfo info, SceneLoadBenchRecorderView load_bench) {
    if (m_inited) return true;

    m_cpu_readback = info.output_mode == RenderOutputMode::CpuReadback;
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
        info.video_hwdec == "none" ? WP_VULKAN_VERSION : VK_API_VERSION_1_3;
    {
        auto instance_span = SceneLoadSpan(load_bench, &SceneLoadProbeIds::vulkan_instance);
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
        auto device_span = SceneLoadSpan(load_bench, &SceneLoadProbeIds::vulkan_device);
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
        auto swapchain_span = SceneLoadSpan(load_bench, &SceneLoadProbeIds::vulkan_swapchain);
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
        auto resources_span = SceneLoadSpan(load_bench, &SceneLoadProbeIds::vulkan_resources);
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
    const auto extent = m_device->out_extent();
    if (extent.width > m_device->limits().maxImageDimension2D ||
        extent.height > m_device->limits().maxImageDimension2D) {
        rstd_error("CPU output extent exceeds maxImageDimension2D");
        return false;
    }
    const auto features = m_device->gpu().GetFormatProperties(info.cpu_format).optimalTilingFeatures;
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
        .usage         = VK_IMAGE_USAGE_TRANSFER_SRC_BIT | VK_IMAGE_USAGE_TRANSFER_DST_BIT,
        .sharingMode   = VK_SHARING_MODE_EXCLUSIVE,
        .initialLayout = VK_IMAGE_LAYOUT_UNDEFINED,
    };
    VmaAllocationCreateInfo image_allocation {};
    image_allocation.usage = VMA_MEMORY_USAGE_GPU_ONLY;
    VVK_CHECK_BOOL_RE(vvk::CreateImage(m_device->vma_allocator(), image_info,
                                       image_allocation, m_cpu_image.handle));
    m_cpu_image.extent = image_info.extent;
    m_cpu_image.generation = u64(1);

    // A transfer-only output image needs neither an image view nor a sampler.
    // bufferRowLength=0 in the copy gives width*4 bytes per row on every device.
    m_cpu_staging.req_size = std::uint64_t(extent.width) * extent.height * 4;
    VkBufferCreateInfo buffer_info {
        .sType       = VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO,
        .size        = m_cpu_staging.req_size,
        .usage       = VK_BUFFER_USAGE_TRANSFER_DST_BIT,
        .sharingMode = VK_SHARING_MODE_EXCLUSIVE,
    };
    VmaAllocationCreateInfo buffer_allocation {};
    buffer_allocation.usage = VMA_MEMORY_USAGE_GPU_TO_CPU;
    buffer_allocation.requiredFlags = VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT;
    buffer_allocation.preferredFlags = VK_MEMORY_PROPERTY_HOST_CACHED_BIT;
    VVK_CHECK_BOOL_RE(vvk::CreateBuffer(m_device->vma_allocator(), buffer_info,
                                        buffer_allocation, m_cpu_staging.handle));
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
        m_cpu_staging.handle = vvk::VmaBuffer {};
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

bool VulkanRender::Impl::commitPreparedUploads(SceneLoadBenchRecorderView load_bench) {
    auto upload_span = SceneLoadSpan(load_bench, &SceneLoadProbeIds::render_upload_submit);
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
    auto wait_span =
        SceneLoadSpan(m_pending_load_bench, &SceneLoadProbeIds::render_texture_upload_wait);
    auto pending = rr.resources.PendingUpload();
    if (pending.is_none()) {
        m_pending_load_bench = {};
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
    m_pending_load_bench = {};
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

owe::CpuFrameResult VulkanRender::Impl::drawFrameCpu(Scene& scene, bool read_pixels) {
    CpuFrameResult frame;
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

    const auto extent = m_device->out_extent();
    if (extent.width != m_cpu_image.extent.width || extent.height != m_cpu_image.extent.height) {
        return fail(VK_ERROR_INITIALIZATION_FAILED, "CPU output extent changed; create a new renderer");
    }
    if (m_cpu_frame_index == std::numeric_limits<std::uint64_t>::max()) {
        return fail(VK_ERROR_TOO_MANY_OBJECTS, "CPU frame serial overflow");
    }
    frame.width = extent.width;
    frame.height = extent.height;
    frame.row_pitch = extent.width * 4;
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
            frame.pixels.resize(static_cast<std::size_t>(m_cpu_staging.req_size));
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
    m_pending_load_bench = {};

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
    VkImageMemoryBarrier to_readback {
        .sType = VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER,
        .srcAccessMask = VK_ACCESS_TRANSFER_WRITE_BIT,
        .dstAccessMask = VK_ACCESS_TRANSFER_READ_BIT,
        .oldLayout = VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL,
        .newLayout = VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL,
        .srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
        .dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
        .image = *m_cpu_image.handle,
        .subresourceRange = { VK_IMAGE_ASPECT_COLOR_BIT, 0, 1, 0, 1 },
    };
    rr.command.PipelineBarrier(VK_PIPELINE_STAGE_TRANSFER_BIT, VK_PIPELINE_STAGE_TRANSFER_BIT,
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
    if (read_pixels)
        rr.command.CopyImageToBuffer(*m_cpu_image.handle, VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL,
                                      *m_cpu_staging.handle, region);
    if (m_timestamp_queries)
        rr.command.WriteTimestamp(VK_PIPELINE_STAGE_BOTTOM_OF_PIPE_BIT, m_timestamp_queries.get(), 2);
    VkBufferMemoryBarrier to_host {
        .sType = VK_STRUCTURE_TYPE_BUFFER_MEMORY_BARRIER,
        .srcAccessMask = VK_ACCESS_TRANSFER_WRITE_BIT,
        .dstAccessMask = VK_ACCESS_HOST_READ_BIT,
        .srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
        .dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
        .buffer = *m_cpu_staging.handle,
        .offset = 0,
        .size = m_cpu_staging.req_size,
    };
    rr.command.PipelineBarrier(VK_PIPELINE_STAGE_TRANSFER_BIT, VK_PIPELINE_STAGE_HOST_BIT,
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
    result = rr.fence_frame.Wait(m_readback_timeout_ns);
    // Do not reset/reuse buffers after a timeout: the submission can still be
    // in flight. The failed renderer retains its resources until destruction.
    if (result != VK_SUCCESS) return fail(result, "wait for CPU frame fence");
    m_timestamp_queries.completed();
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
    if (! completion.Valid() || rr.resources.CompleteSubmission(completion).is_none()) {
        return fail(VK_ERROR_INITIALIZATION_FAILED, "track CPU frame resource completion");
    }
    ReleaseCompletedRetiredResources(*m_device, rr);

    if (read_pixels) {
        void* mapped = nullptr;
        result = m_cpu_staging.handle.MapMemory(&mapped);
        if (result != VK_SUCCESS) return fail(result, "map CPU staging buffer");
        result = vmaInvalidateAllocation(m_device->vma_allocator(),
                                          m_cpu_staging.handle.Allocation(), 0, VK_WHOLE_SIZE);
        if (result != VK_SUCCESS) {
            m_cpu_staging.handle.UnMapMemory();
            return fail(result, "invalidate CPU staging buffer");
        }
        std::memcpy(frame.pixels.data(), mapped, frame.pixels.size());
        m_cpu_staging.handle.UnMapMemory();
    }
    frame.video_decoders = rr.resources.ObserveVideoDecoders();
    frame.status = CpuFrameStatus::Completed;
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
        scene, m_device->out_extent(), max_framebuffer_extent, m_msaa_samples);
}

void VulkanRender::Impl::compileRenderGraph(Scene& scene, rg::RenderGraph& rg) {
    auto render_scene = ExtractRenderSceneSnapshot(scene);
    compileRenderGraph(scene, rg, render_scene);
}

void VulkanRender::Impl::compileRenderGraph(Scene& scene, rg::RenderGraph& rg,
                                            const RenderSceneSnapshot& render_scene) {
    compileRenderGraph(scene, rg, render_scene, {});
}

bool VulkanRender::Impl::prepareProgram(Scene& scene, const RenderSceneSnapshot& render_scene,
                                        resource::ResourcePlanSections sections,
                                        SceneLoadBenchRecorderView     load_bench) {
    auto status = m_program.beginPrepare(
        scene, *m_device, m_rendering_resources, render_scene, sections, load_bench);
    while (status == RenderProgramPrepareStatus::BatchReady) {
        if (! commitPreparedUploads(load_bench)) {
            m_program.abortPrepare(m_rendering_resources);
            return false;
        }
        status = m_program.continuePrepare(scene, *m_device, m_rendering_resources, load_bench);
    }
    if (status == RenderProgramPrepareStatus::Failed) {
        m_program.abortPrepare(m_rendering_resources);
        return false;
    }

    {
        auto scopes_span = SceneLoadSpan(load_bench, &SceneLoadProbeIds::render_scopes);
        m_program.rebuildScopes();
    }
    if (! commitPreparedUploads(load_bench)) {
        m_program.abortPrepare(m_rendering_resources);
        return false;
    }
    m_rendering_resources.resources.CommitPreparePlan();
    m_program.loaded = true;
    return true;
}

void VulkanRender::Impl::compileRenderGraph(Scene& scene, rg::RenderGraph& rg,
                                            const RenderSceneSnapshot& render_scene,
                                            SceneLoadBenchRecorderView load_bench) {
    if (! m_inited) return;
    m_pending_load_bench = load_bench;
    auto compile_span    = SceneLoadSpan(load_bench, &SceneLoadProbeIds::render_graph_compile);
    m_program.loaded     = false;
    m_capture_binding.reset();
    m_capture_error.clear();

    {
        auto program_span = SceneLoadSpan(load_bench, &SceneLoadProbeIds::render_program_build);
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
        auto requests_span = SceneLoadSpan(load_bench, &SceneLoadProbeIds::render_requests);
        configureRenderTargets(scene);
        m_program.finalizeFramePassRequests(scene);
        m_program.finalizeResourceRequests(scene);
    }
    (void)prepareProgram(scene, render_scene, resource::ResourcePlanAll, load_bench);
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
