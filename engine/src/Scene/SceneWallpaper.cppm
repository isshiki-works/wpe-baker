module;
#include <vulkan/vulkan.h>

export module wescene.scene_wallpaper;
import rstd;

export import wescene.core;
export import wescene.json;
export import rstd.cppstd;
export import wescene.scene;
export import wescene.vulkan_render;
export import wescene.vulkan;
export import wescene.types;
export import wescene.utils;
export import wescene.pkg.parse;
export import owe.audio_response;

using namespace rstd::prelude;
using rstd::sync::Arc;

export namespace owe
{

using FirstFrameCallback             = std::function<void()>;
using AudioResponseDemandCallback    = Arc<dyn<rstd::Fn<void(bool)>>>;
using UserPropertyDiagnosticCallback = std::function<void(Vec<SceneUserPropertyDiagnostic>)>;
using RenderPassDiagnosticCallback =
    std::function<void(std::vector<vulkan::PreparedPassDiagnostic>)>;

// Publishes the effective wallpaper background color. The project scheme color
// takes precedence over `general.clearcolor` and runtime changes are emitted.
// Components are 0..=1 sRGB. Alpha is fixed at 1.0 by the host.
using ClearColorCallback = std::function<void(float r, float g, float b)>;

struct MediaStatus {
    uint32_t    state { 0 };
    std::string title;
    std::string artist;
    std::string album;
    std::string album_artist;
    std::string art_url;
    std::string previous_art_url;
};

struct SceneAudioClientIdentity {
    std::string application_name;
    std::string application_id;
    std::string stream_prefix;
    std::string component;
    std::string media_name;
    std::string media_role { "music" };
};

struct SceneWallpaperConfig {
    std::string                             source_pkg_path;
    std::string                             assets_dir;
    std::string                             cache_dir;
    std::shared_ptr<wpscene::SceneDocument> scene_document;
    rstd::json::Map                         user_properties;
    uint32_t                                fps { 30 };
    float                                   volume { 1.0f };
    float                                   volume_scale { 1.0f };
    bool                                    muted { false };
    FillMode                                fill_mode { FillMode::ASPECTCROP };
    float                                   speed { 1.0f };
    bool                                    graphviz { false };
    Option<u64>                             random_seed;
};

class SceneRuntimeController;

struct OfflineOptions {
    uint64_t seed { 0 };
    double epoch_ms { 946684800000.0 };
    uint32_t fps_num { 0 }; // both zero: use step(dt); otherwise exact rational FPS
    uint32_t fps_den { 0 };
    bool trace_scene { false };
    uint64_t readback_start { 0 };
    uint64_t readback_stride { 1 };
    std::optional<uint64_t> readback_phase;
    bool readsFrame(uint64_t index) const {
        if (index < readback_start || readback_stride == 0) return false;
        const uint64_t phase = (index - readback_start) % readback_stride;
        return phase == 0 || (readback_phase && phase == *readback_phase);
    }
    std::vector<OfflineVideoPlaybackRateOverride> video_rate_overrides;
};

enum class OfflineStepStatus { NotReady, Drawn, Failed };

struct OfflineFrameInput {
    double cursor_x { 0.5 };
    double cursor_y { 0.5 };
    bool cursor_in_window { false };
    uint32_t mouse_buttons_down { 0 };
    Option<MediaStatus> media;
    Option<audio::PcmWindow> pcm;
};

// Authored sound-layer mix for this output-frame interval. External response
// PCM is a separate input and is never copied into this soundtrack implicitly.
struct OfflineAudioFrame {
    uint64_t sample_start { 0 };
    uint32_t frame_count { 0 };
    uint32_t sample_rate { 48000 };
    uint32_t channels { 2 };
    std::vector<float> samples;
};

class SceneWallpaper : NoCopy {
public:
    SceneWallpaper();
    ~SceneWallpaper();
    bool init();
    bool inited() const;

    // Dedicated synchronous CPU-output mode; call on a fresh instance.
    // No realtime driver or sound device is started. Frame indices start at 0;
    // dt is fixed for the job. Prewarm uses these same steps and discards pixels.
    bool initOffline(SceneWallpaperConfig, RenderInitInfo, OfflineOptions = {});
    bool step(uint64_t frame_index, double dt, const OfflineFrameInput& = {});
    const CpuFrameResult& readback() const;
    OfflineStepStatus offlineStepStatus() const;
    const OfflineAudioFrame& audioReadback() const;
    std::string offlineError() const;
    std::vector<std::string> offlineDiagnostics() const;
    const std::vector<OfflineSourceScriptError>& offlineSourceScriptErrors() const;
    std::vector<OfflineDependency> offlineDependencies() const;
    uint64_t offlineIkChainSolves() const;
    std::string offlineSceneDescription() const;
    std::string offlineAnimationPeriods() const;
    std::string offlineProjection() const;
    std::string offlineVideoRateOverrides() const;

    void initVulkan(RenderInitInfo);

    void play();
    void play(uint32_t fade_ms);
    void pause();
    void pause(uint32_t fade_ms);
    void requestFrame();
    void mouseInput(double x, double y);
    // button: 0=left, 1=right, 2=middle (GLFW numbering). down=true on
    // press, false on release.
    void mouseButton(int button, bool down);
    void mouseEnter(bool in_window);

    void configure(SceneWallpaperConfig);
    void setFps(uint32_t);
    void setVolume(float);
    void setVolumeScale(float);
    void setVolumeScale(float, uint32_t fade_ms);
    void setMuted(bool);
    void setFillMode(FillMode);
    void setSpeed(float);
    void setMediaStatus(MediaStatus);
    void setAudioClientIdentity(SceneAudioClientIdentity);
    void setAudioResponseDemandCallback(AudioResponseDemandCallback);
    template<typename Callback>
    void setAudioResponseDemandCallback(Callback callback) {
        setAudioResponseDemandCallback(AudioResponseDemandCallback::make(rstd::move(callback)));
    }
    void setAudioResponseEnabled(bool);
    void setAudioPcmWindow(audio::PcmWindow window);
    void endAudioResponse();
    void setUserPropertyRaw(std::string_view, std::string);
    void setUserPropertyJson(std::string_view, Json);
    void setOnFirstFrame(FirstFrameCallback);
    void setOnUserPropertyDiagnostics(UserPropertyDiagnosticCallback);
    void requestPreparedPassDiagnostics(RenderPassDiagnosticCallback);

    // Install a callback for the effective wallpaper background color.
    // Set once before initVulkan.
    void setOnClearColor(ClearColorCallback);

    ExSwapchain* exSwapchain() const;

    int takeLastFrameSyncFd();

    bool getDrmRenderNode(uint32_t& out_major, uint32_t& out_minor) const;

    bool waitVulkanInited(uint32_t timeout_ms);

    VkInstance       vkInstance() const;
    VkPhysicalDevice vkPhysicalDevice() const;
    VkDevice         vkDevice() const;
    VkQueue          vkGraphicsQueue() const;
    uint32_t         vkGraphicsQueueFamily() const;

    void deviceUuid(uint8_t out[16]) const;
    void driverUuid(uint8_t out[16]) const;

private:
    bool m_inited { false };

private:
    friend class SceneRuntimeController;

    bool                                    m_offscreen { false };
    std::unique_ptr<SceneRuntimeController> m_runtime;
};

} // namespace owe
