export module wescene.offline_session;
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

export namespace owe
{

struct MediaStatus {
    uint32_t    state { 0 };
    std::string title;
    std::string artist;
    std::string album;
    std::string album_artist;
    std::string art_url;
    std::string previous_art_url;
};

// 加载一个场景要的配置。与帧怎么驱动（离线固定步长还是实时墙钟）无关。
struct SessionConfig {
    std::string                             source_pkg_path;
    std::string                             assets_dir;
    std::string                             cache_dir;
    std::shared_ptr<wpscene::SceneDocument> scene_document;
    NJson                                   user_properties;
    float                                   volume { 1.0f };
    float                                   volume_scale { 1.0f };
    bool                                    muted { false };
    FillMode                                fill_mode { FillMode::ASPECTCROP };
    float                                   speed { 1.0f };
    bool                                    graphviz { false };
};

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

// 一次场景渲染会话：加载一个场景，按帧推进并同步取回像素与音频。
// 一个实例只加载一次；不起线程、不开音频设备，全部调用在调用方线程上同步完成。
// 帧怎么推进见 OfflineSession.cpp 顶部的说明（simulate/draw 的固定顺序，以及实时驱动怎么接）。
class OfflineSession : NoCopy {
public:
    OfflineSession();
    ~OfflineSession();

    // 加载场景并准备第一帧。帧号从 0 起，dt 在整个作业里固定；预热帧同样走 step，只是不取像素。
    bool init(SessionConfig, RenderInitInfo, OfflineOptions = {});
    bool step(uint64_t frame_index, double dt, const OfflineFrameInput& = {});
    // 帧与帧之间改用户属性（与作者在属性面板上改值同一条路径）。
    void setUserProperty(std::string_view key, NJson value);

    const CpuFrameResult& readback() const;
    OfflineStepStatus stepStatus() const;
    const OfflineAudioFrame& audioReadback() const;
    std::string error() const;
    std::vector<std::string> diagnostics() const;
    const std::vector<OfflineSourceScriptError>& sourceScriptErrors() const;
    std::vector<OfflineDependency> dependencies() const;
    uint64_t ikChainSolves() const;
    std::string sceneDescription() const;
    std::string animationPeriods() const;
    std::string projection() const;
    std::string videoRateOverrides() const;
    void deviceUuid(uint8_t out[16]) const;

private:
    struct Impl;
    std::unique_ptr<Impl> m_impl;
};

} // namespace owe
