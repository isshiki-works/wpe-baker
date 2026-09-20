module;

export module wescene.vulkan_render;
import wescene.types;
import rstd;
import rstd.cppstd;
import wescene.load_bench;
import wescene.vulkan;
import wescene.scene;
import wescene.resource_registry;

import wescene.rgraph;

export import :vulkan_pass;
export import :program;
export import :pipeline_layout;
export import :shader_reflection_cache;
export import :uniform_buffer;
export import :resource;
export import :buffer_resolver;
export import :pass_common;
export import :copy_pass;
export import :custom_shader_pass;
export import :fin_pass;
export import :pre_pass;

using namespace rstd::prelude;

export namespace owe
{

using ReDrawCB = std::function<void()>;

struct VulkanSurfaceInfo {
    std::function<VkResult(VkInstance, VkSurfaceKHR*)> createSurfaceOp;
    std::vector<std::string>                           instanceExts;
};

enum class RenderOutputMode
{
    Legacy,
    CpuReadback,
};

enum class CpuFrameStatus
{
    Completed,
    Submitted,
    NotReady,
    InvalidMode,
    RenderError,
    Timeout,
    DeviceLost,
};

// Owns completed pixels or an explicit GPU submission receipt. Submitted frames
// contain no CPU pixels or completed GPU timing. No pixel view outlives a map.
struct CpuFrameResult {
    // Optional host-side spans, enabled by gpu_timing or WPE_RENDER_CPU_PROFILE. These are wall
    // times, not GPU timestamps, and distinguish preparation from waits.
    std::optional<double>    cpu_prepare_ms, cpu_render_wait_ms, cpu_encode_ms, cpu_pass_check_ms;
    std::optional<double>    cpu_scene_ms, cpu_resources_ms, cpu_script_ms, cpu_pending_wait_ms;
    CpuFrameStatus           status { CpuFrameStatus::NotReady };
    std::uint32_t            width { 0 };
    std::uint32_t            height { 0 };
    std::uint32_t            row_pitch { 0 };
    VkFormat                 format { VK_FORMAT_UNDEFINED };
    std::uint64_t            frame_index { 0 };
    // Populated for a selected graph capture; output dimensions remain above.
    std::string              source_render_target;
    std::string              source_pass;
    std::uint64_t            source_texture_version { 0 };
    std::uint32_t            source_width { 0 };
    std::uint32_t            source_height { 0 };
    // Prepared Graph passes, including scene copies; excludes frame/pre,
    // FinPass and CPU readback. This is not a count of individual VkDraw calls.
    std::uint32_t            compiled_scene_passes { 0 };
    vulkan::VideoDecoderInventory video_decoders;
    // Optional GPU batch span for offline strategy estimates: recorded
    // program (including FinPass) through readback copy completion, preserving
    // their actual overlap. CPU work and I/O are excluded.
    std::optional<double>    gpu_total_ms;
    // Same start as gpu_total_ms, ending after the recorded program
    // (including FinPass), before the CPU readback barrier and copy.
    std::optional<double>    gpu_draw_ms;
    bool                     gpu_timing_requested { false };
    bool                     gpu_timing_supported { false };
    std::uint32_t            timestamp_valid_bits { 0 };
    std::optional<double>    timestamp_period_ns;
    // Support is checked only when requested. Allocation/read failures keep
    // the measurement null and report their own status, without fake zeros.
    VkResult                 gpu_timing_error_code { VK_SUCCESS };
    std::string              gpu_timing_message;
    std::vector<std::uint8_t> pixels;
    bool                     gpu_sampled { false };
    bool                     gpu_scene_overlap { false };
    std::uint64_t            gpu_readback_frames { 0 };
    std::string              gpu_capture_metadata;
    std::string              sampling_coverage;
    VkResult                 error_code { VK_SUCCESS };
    std::string              message;

    bool completed() const noexcept { return status == CpuFrameStatus::Completed; }
    bool submitted() const noexcept { return status == CpuFrameStatus::Submitted; }
};

struct RenderCaptureTarget {
    // Select either an effect-local FBO by author identity, or a complete
    // runtime RT name. When both author ID and ordinal are supplied they must
    // identify the same effect. Ordinals refer to the original effects[] array.
    std::int32_t owner_layer_id { -1 };
    std::int32_t authored_effect_id { -1 };
    std::int32_t effect_ordinal { -1 };
    std::string local_fbo;
    std::string runtime_render_target;
    // Exact RenderGraph logical texture version; -1 selects the latest real
    // writer. Virtual initial values may consume version 0 and cannot be captured.
    std::int32_t texture_version { -1 };
    // Select the last pass of an authored effect only when it writes directly
    // to LayerNext. This is a graph provenance selector, not a target name.
    bool effect_terminal { false };
    // Refuse FinPass scaling: the offline output extent must equal the source.
    bool exact_extent { false };
    // Capture-only graph override for an explicitly selected authored owner.
    bool force_visible_owner { false };
};

struct RenderLayerSelection {
    bool enabled { false };
    std::vector<std::int32_t> include_layers;
    bool transparent_background { false };
    bool include_postprocessing { true };

    bool contains(std::int32_t id) const {
        return !enabled || id < 0 ||
            std::find(include_layers.begin(), include_layers.end(), id) != include_layers.end();
    }
};

// Offline-only primary orthographic camera override.  Coordinates are in the
// authored scene plane: the parser's normal global camera is centered at
// (orthographic_width / 2, orthographic_height / 2), so positive X/Y map
// directly to authored right/up world coordinates.  Width/height describe the
// world-space window, independently of RenderInitInfo's output pixel extent.
struct OrthographicCaptureViewport {
    double center_x { 0.0 };
    double center_y { 0.0 };
    double width { 0.0 };
    double height { 0.0 };
};

struct GpuEncodeOptions {
    std::string path;
    std::string codec { "h264_vulkan" };
    bool packed_alpha { false };
    int qp { 18 };
    std::uint32_t fps_num { 30 }, fps_den { 1 };
    std::uint64_t first_frame { 0 }, frames { 0 };
    std::uint64_t encoded_frames { 0 };
    bool collect_bounds { false }, bounds_include_rgb { false };
    std::vector<std::uint64_t> retain_frames;
    std::uint32_t crossfade_frames { 0 };
    std::uint32_t crop_x { 0 }, crop_y { 0 }, crop_width { 0 }, crop_height { 0 };
    bool retain_loop_window { false };
    std::uint32_t resize_width { 0 }, resize_height { 0 };
};

struct RenderInitInfo {
    bool enable_valid_layer { false };
    bool offscreen { false };

    // CpuReadback always creates a device without a window/surface or external
    // memory handles. The budget is configurable per job, not an image-size cap.
    RenderOutputMode output_mode { RenderOutputMode::Legacy };
    VkFormat         cpu_format { VK_FORMAT_R8G8B8A8_UNORM };
    std::uint64_t    max_readback_bytes { 256ull * 1024 * 1024 };
    std::uint64_t    readback_timeout_ns { 10'000'000'000ull };
    bool             gpu_timing { false };
    // Scale only explicitly eligible image-effect allocations, preserving authored layout.
    double           effect_render_scale { 1.0 };
    bool             match_effect_resolution { false };
    // Optional box-averaged RGBA readback. Rendering keeps the original output extent.
    std::uint32_t    sample_width { 0 };
    std::uint32_t    sample_height { 0 };
    bool             collect_sampling_coverage { false };
    std::uint64_t    sampling_coverage_start { 0 }, sampling_coverage_frames { 0 };
    std::optional<GpuEncodeOptions> gpu_encode;
    std::optional<RenderCaptureTarget> capture_target;
    std::optional<OrthographicCaptureViewport> orthographic_capture_viewport;
    RenderLayerSelection layer_selection;

    std::span<const rstd::uint8_t> uuid;
    TexTiling                      offscreen_tiling { TexTiling::OPTIMAL };
    /* When true, allocate the offscreen ExSwapchain images out of
     * HOST_VISIBLE && !DEVICE_LOCAL (true GTT) so the exported dmabuf
     * fds are importable by a foreign GPU (cross-GPU PRIME). Ignored
     * when offscreen == false. */
    bool              offscreen_host_visible { false };
    VulkanSurfaceInfo surface_info;

    std::uint16_t width { 1920 };
    std::uint16_t height { 1080 };
    std::string   video_hwdec { "auto" };
    std::string   video_render_node;
    // MSAA samples for the screen RT only. 1 disables. Clamped down to
    // device's framebufferColorSampleCounts at init.
    std::uint32_t msaa_samples { 1 };
    ReDrawCB      redraw_callback;

    /* When set AND `offscreen == true`, VulkanRender invokes this factory
     * after picking the GPU and creating the VkDevice, and adopts the
     * returned swapchain instead of allocating its own LocalExSwapchain.
     * Used by the waywallen-wescene host to construct a BridgeExSwapchain
     * around a ww_bridge_pool created with the just-picked device.
     * Ignored on the on-screen path. */
    struct ExSwapchainHandles {
        VkInstance       instance;
        VkPhysicalDevice physical_device;
        VkDevice         device;
        VkQueue          graphics_queue;
        rstd::uint32_t   graphics_queue_family;
    };
    std::function<std::unique_ptr<ExSwapchain>(const ExSwapchainHandles&)> ex_swapchain_factory;
};

Box<rg::RenderGraph> sceneToRenderGraph(Scene&);
Box<rg::RenderGraph> sceneToRenderGraph(Scene&, const RenderSceneSnapshot&, const RenderLayerSelection* = nullptr,
                                        const RenderCaptureTarget* = nullptr);

namespace vulkan
{

class FinPass;

enum class RenderGraphResourceRetention
{
    KeepSceneTextures,
    ReleaseSceneTextures,
};

class VulkanRender {
public:
    VulkanRender();
    ~VulkanRender();

    bool init(RenderInitInfo, SceneLoadBenchRecorderView load_bench = {});

    void destroy();

    void drawFrame(Scene&);

    // Synchronous backpressure: one submission, one fenced readback, no drops.
    // A timeout/device error poisons this renderer; destroy it before retrying.
    CpuFrameResult drawFrameCpu(Scene&, bool read_pixels = true);

    // Hand a consumed frame's pixel buffer back so the next drawFrameCpu
    // reuses its allocation. Without this every frame allocates and
    // zero-fills a full frame before the readback copy overwrites it.
    // Optional: skipping it only costs the allocation.
    void recycleCpuPixels(std::vector<std::uint8_t>&&);

    void clearLastRenderGraph(
        RenderGraphResourceRetention retention = RenderGraphResourceRetention::KeepSceneTextures);
    void configureRenderTargets(Scene&);
    void compileRenderGraph(Scene&, rg::RenderGraph&);
    void compileRenderGraph(Scene&, rg::RenderGraph&, const RenderSceneSnapshot&);
    void compileRenderGraph(Scene&, rg::RenderGraph&, const RenderSceneSnapshot&,
                            SceneLoadBenchRecorderView);
    void refreshPreparedResources(Scene&);
    void refreshPreparedResources(Scene&, const RenderSceneSnapshot&);
    void refreshPreparedTextures(Scene&, const RenderSceneSnapshot&);
    void invalidatePreparedRenderItems(slice<RenderItemId>, PassInvalidationFlags);
    void refreshPreparedRenderItems(Scene&, const RenderSceneSnapshot&, slice<RenderItemId>,
                                    PassInvalidationFlags);
    void refreshPreparedMaterial(Scene&, const RenderSceneSnapshot&, SceneMaterialId,
                                 PassInvalidationFlags);
    bool refreshPreparedMaterialTextures(Scene&, const RenderSceneSnapshot&, SceneMaterialId);
    bool refreshPreparedMaterialTextures(Scene&, const RenderSceneSnapshot&,
                                         slice<SceneMaterialId>);
    void refreshPreparedMesh(Scene&, const RenderSceneSnapshot&, SceneMeshId,
                             PassInvalidationFlags);
    std::vector<PreparedPassDiagnostic> preparedPassDiagnostics() const;
    std::optional<std::string> firstUnpreparedPass() const;
    // Finish queued GPU work before mutating resources it may still reference.
    std::string finishPendingFrame();
    // Free buffer generations no longer referenced by prepared work.
    void evictUnusedMeshes();
    void UpdateCameraFillMode(Scene&, owe::FillMode);

    bool onSwapchainReady(unsigned width, unsigned height);

    ExSwapchain* exSwapchain() const;
    bool         inited() const;

    int takeLastFrameSyncFd();

    bool               getDrmRenderNode(std::uint32_t& out_major, std::uint32_t& out_minor) const;
    DeviceCapabilities deviceCapabilities() const;

    /* Tick all registered video-tex decoders. No-op when no scene
     * texture has been recognised as a VIDEO container. Invoked from
     * SceneWallpaper's per-frame RenderDraw handler. */
    void pumpVideoTextures(double dt_seconds);

    /* For every FontFace in the Scene font-cache extension with non-empty DirtyRects,
     * coalesce to one AABB and vkCmdCopyBufferToImage into the face's
     * atlas VkImage. Skips faces whose VkImage hasn't been created yet
     * (CreateTex runs lazily on first material bind — those pixels reach
     * the GPU through the aliased Image::mip.data instead). Clears each
     * face's dirty_rects regardless. */
    void pumpFontAtlases(Scene& scene);

    VkInstance       vkInstance() const;
    VkPhysicalDevice vkPhysicalDevice() const;
    VkDevice         vkDevice() const;
    VkQueue          vkGraphicsQueue() const;
    std::uint32_t    vkGraphicsQueueFamily() const;

    void deviceUuid(std::uint8_t out[16]) const;
    void driverUuid(std::uint8_t out[16]) const;

private:
    struct Impl;
    Box<Impl> pImpl;
};

} // namespace vulkan
} // namespace owe
