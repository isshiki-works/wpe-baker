module;
#include <rstd/macro.hpp>

#include <random>
#include <chrono>
#include <cstdlib>

#include "JsonNlohmann.hpp"

module wescene.offline_session;
import wescene.types;
import wescene.utils;
import wescene.scene;

import eigen;
import owe.audio_response;
import owe.scene_audio_response;
import owe.user_property;
import rstd;
import rstd.log;
import rstd.cppstd;
import owe.media;
import wescene.fs;
import wescene.pkg.parse;
import wescene.pkg_fs;
import wescene.rgraph;
import wescene.resource;
import wescene.scene_user_property;
import wescene.script;
import wescene.vulkan_render;

#include "OfflineAnimationPeriods.h"

using namespace owe;
using namespace rstd::prelude;
using namespace rstd::literals;
using rstd::sync::Arc;

// ---- 一帧怎么推进 -------------------------------------------------------------
//
// OfflineSession::Impl::runFrame 按下面的固定顺序推进一帧。这个顺序就是拆分前
// SceneWallpaper 的 stepOffline + onDraw 的调用顺序，一步不差；输出逐字节依赖它，
// 不要调换、合并或跳过其中任何一步：
//
//   beginFrame  回收上一帧的像素缓冲 → 清空帧结果 → 写本帧指针/按键/媒体状态
//               → 分析本帧 PCM（没给就当静音）→ SceneRuntime::PrepareOfflineFrame
//   simulate    指针位置 → 脚本输入（frametime/runtime/time_of_day/画布/光标/按键边沿）
//               → 音频响应推进与频谱 → 节点字段动画 → 场景脚本 → 相机路径
//               → 材质着色器动画 → 变换更新器
//   draw        等上一帧 GPU 工作收尾（之后才能改网格/材质/纹理）→ 需要时重建渲染图
//               → SceneRuntime::BeforeRender → 刷新渲染目标/网格/材质 → 推进视频纹理
//               → 上传字形 → drawFrameCpu → 检查有没有漏准备的 pass
//   advance     SceneRuntime::AdvanceOffline（FrameAdvance 系统在本帧绘制之后才跑）
//
// 注意 draw 开头的 finishPendingFrame 排在 simulate 之后：上一帧的 GPU 工作可以和
// 本帧的脚本/音频并行，但任何 GPU 资源改动都必须在它之后。
//
// 时钟由 FrameClock 一次给全，simulate/draw/advance 只读它，不关心时钟从哪来。
// 离线作业用 offlineClock（固定 dt 或有理帧率 + 作业纪元）；实时驱动（将来的第二个
// 使用者）按墙钟和本地时间填同一个结构，复用 beginFrame/simulate/advance，draw 的
// 输出端换成呈现目标即可。

namespace owe
{
namespace
{

NJson MakeUserPropertyDescriptor(NJson value) {
    if (Find(value, "value") != nullptr) return value;
    NJson object    = NJson::object();
    object["value"] = std::move(value);
    return object;
}

NJson RawUserProperty(std::string_view value) { return MakeUserPropertyWirePatch(value); }

NJson InitialUserProperty(NJson value) {
    if (value.is_string()) return RawUserProperty(value.get_ref<const std::string&>());
    return MakeUserPropertyDescriptor(std::move(value));
}

bool SameSceneMaterialId(SceneMaterialId lhs, SceneMaterialId rhs) {
    return lhs.index == rhs.index && lhs.generation == rhs.generation;
}

void PushUniqueMaterialId(Vec<SceneMaterialId>& materials, SceneMaterialId id) {
    for (usize index {}; index < materials.len(); ++index) {
        if (SameSceneMaterialId(materials[index], id)) return;
    }
    materials.push(rstd::move(id));
}

vulkan::PassInvalidationFlags MaterialDirtyToPassInvalidationFlags(SceneMaterialDirtyFlags flags) {
    vulkan::PassInvalidationFlags out = vulkan::PassInvalidationNone;
    if ((flags & SceneMaterialDirtyResources) != 0) {
        out |= vulkan::ToPassInvalidationFlags(vulkan::PassInvalidation::Resources);
    }
    if ((flags & SceneMaterialDirtyPipeline) != 0) {
        out |= vulkan::ToPassInvalidationFlags(vulkan::PassInvalidation::Pipeline) |
               vulkan::ToPassInvalidationFlags(vulkan::PassInvalidation::Framebuffer);
    }
    return out;
}

NJson RuntimeTextureProperty(std::string value) {
    return NJson { { "type", "scenetexture" }, { "value", std::move(value) } };
}

owe::script::MediaStatus ToScriptMediaStatus(const MediaStatus& status) {
    return owe::script::MediaStatus { .state            = status.state,
                                      .title            = status.title,
                                      .artist           = status.artist,
                                      .album            = status.album,
                                      .album_artist     = status.album_artist,
                                      .art_url          = status.art_url,
                                      .previous_art_url = status.previous_art_url };
}

void MergeProjectUserProperties(const std::filesystem::path& project_dir, NJson& out) {
    const auto    project_path = project_dir / "project.json";
    std::ifstream is(project_path);
    if (! is) return;

    std::string source(std::istreambuf_iterator<char>(is), {});
    auto        parsed = ParseNJson(source, { .allow_comments = true });
    if (parsed.is_err()) {
        rstd_warn("Can't parse {}: {}", project_path.string(), parsed.unwrap_err());
        return;
    }
    auto        root    = parsed.unwrap();
    const auto* general = Find(root, "general");
    if (general == nullptr) return;
    const auto* properties = Find(*general, "properties");
    if (properties == nullptr || ! properties->is_object()) return;

    for (const auto& [raw_key, value] : properties->items()) {
        std::string key        = CanonicalSceneUserPropertyKey(raw_key);
        const auto* current    = Find(out, key);
        auto        descriptor = current != nullptr ? MergeUserPropertyDescriptor(value, *current)
                                                    : MakeUserPropertyDescriptor(value);
        out[key]               = std::move(descriptor);
    }
}

NJson NormalizeUserProperties(const NJson& input) {
    NJson out = NJson::object();
    for (const auto& [key, value] : input.items()) {
        std::string canonical = CanonicalSceneUserPropertyKey(key);
        if (key == canonical || Find(out, canonical) == nullptr)
            out[canonical] = InitialUserProperty(value);
    }
    return out;
}

} // namespace

struct OfflineSession::Impl {
    // 一帧的时钟，见文件顶部说明。
    struct FrameClock {
        uint64_t index { 0 };
        double   scaled_dt { 0.0 };    // 本帧时长 × 播放速度
        double   elapsed { 0.0 };      // 本帧场景时间（已乘播放速度）
        double   next_elapsed { 0.0 }; // 下一帧场景时间（已乘播放速度）
        float    time_of_day { 0.0f }; // 0..1，0 为午夜
    };

    // gpu_timing 打开时记的 CPU 侧分段时间：simulate 记开头和结尾，draw 接着记。
    struct FrameProfile {
        std::chrono::steady_clock::time_point scene_started;
        std::chrono::steady_clock::time_point scene_finished;
        std::optional<double>                 script_ms;
    };

    Impl();
    ~Impl();

    bool init(SessionConfig, RenderInitInfo, OfflineOptions);
    bool step(uint64_t, double, const OfflineFrameInput&);
    void setUserProperty(std::string_view, NJson);
    void invalidateFrame(uint64_t index, std::string message);
    std::string describeScene() const;
    std::string describeProjection() const;
    std::string animationPeriods() const { return m_scene ? DescribeOfflineAnimationPeriods(*m_scene) : "[]"; }
    std::string videoRateOverrides() const;

    double       frameTime(uint64_t index) const;
    FrameClock   offlineClock(uint64_t index) const;
    void         loadScene(const vulkan::DeviceCapabilities&);
    void         installScene(Box<Scene>, Arc<UniformRuntimeInput>);
    bool         hasScene() const { return m_scene && m_rg.is_some(); }
    bool         runFrame(const FrameClock&, const OfflineFrameInput&);
    bool         beginFrame(const FrameClock&, const OfflineFrameInput&);
    FrameProfile simulate(const FrameClock&);
    bool         draw(const FrameClock&, const FrameProfile&);
    void         advance(const FrameClock&);
    void         applyMediaStatus(const MediaStatus&);
    void rebuildRenderGraph(vulkan::RenderGraphResourceRetention retention, bool evict_meshes);
    void consumeDirtyEventsCoveredByGraphRebuild();
    void refreshPreparedRenderTargetDirtyEvents();
    void refreshPreparedMeshDirtyEvents();
    void refreshPreparedMaterialDirtyEvents();

    // 成员的声明顺序决定析构顺序：场景先于 VulkanRender、混音器与 Services 析构，
    // 与拆分前一致（场景里的脚本运行时持有 Services 指针，声音层挂在混音器上）。
    bool     m_started { false };
    bool     m_inited { false };
    bool     m_failed { false };
    uint64_t m_next_frame { 0 };
    double   m_dt { 0.0 };
    Services m_services;
    std::vector<OfflineVideoPlaybackRateOverrideResult> m_video_rate_overrides;
    std::string                                         m_error;
    OfflineAudioFrame                                   m_audio_frame;
    OfflineOptions                                      m_options;
    bool                                                m_profile { false };
    RenderLayerSelection                                m_layers;
    std::optional<RenderCaptureTarget>                  m_capture_target;

    SessionConfig m_config;
    NJson         m_user_properties;

    Box<owe::media::OfflineMixer> m_mixer;
    Box<vulkan::VulkanRender>     m_render;
    Option<Box<Scene>>               m_scene_owner;
    Scene*                           m_scene { nullptr };
    Option<Arc<UniformRuntimeInput>> m_uniform_input_owner;
    UniformRuntimeInput*             m_uniform_input { nullptr };
    // Identity snapshot owned by the compiled render graph.
    RenderSceneSnapshot            m_render_scene;
    Option<Box<rg::RenderGraph>>   m_rg;
    // 渲染图读上一帧像素（帧反馈）：预热也得真画。只增不减。
    // ponytail: 反馈在预热中途才出现时，之前跳过的帧不补画
    bool                           m_reads_previous_frame { false };
    // 覆盖度按输出段每一帧累计，要它时输出段每帧都画。
    bool                           m_raster_every_output_frame { false };
    audio::ResponseEngine          m_audio_response_engine;
    scene_audio::ResponseProcessor m_scene_audio_response;
    CpuFrameResult                 m_cpu_frame;
    OfflineStepStatus              m_step_status { OfflineStepStatus::NotReady };
    uint64_t                       m_step_index { 0 };

    // 本帧的指针与按键；按键边沿由相邻两帧的按下状态算出。
    array<float, 2> m_pointer { 0.5f, 0.5f };
    bool            m_cursor_in_window { false };
    uint32_t        m_buttons_down { 0 };
    uint32_t        m_buttons_pressed { 0 };
    uint32_t        m_buttons_released { 0 };
};

OfflineSession::Impl::Impl()
    : m_mixer(Box<owe::media::OfflineMixer>::make()), m_render(Box<vulkan::VulkanRender>::make()) {}

OfflineSession::Impl::~Impl() {
    m_render->destroy();
    rstd_info("render handler deleted");
}

void OfflineSession::Impl::invalidateFrame(uint64_t index, std::string message) {
    m_step_status = OfflineStepStatus::Failed;
    m_cpu_frame = CpuFrameResult {};
    m_cpu_frame.status = CpuFrameStatus::RenderError;
    m_cpu_frame.frame_index = index;
    m_cpu_frame.message = rstd::move(message);
}

double OfflineSession::Impl::frameTime(uint64_t index) const {
    return m_options.fps_num ? static_cast<double>(static_cast<long double>(index) *
        m_options.fps_den / m_options.fps_num) : double(index) * m_dt;
}

OfflineSession::Impl::FrameClock OfflineSession::Impl::offlineClock(uint64_t index) const {
    FrameClock clock;
    clock.index        = index;
    clock.scaled_dt    = m_dt * m_config.speed;
    clock.elapsed      = frameTime(index) * m_config.speed;
    clock.next_elapsed = frameTime(index + 1) * m_config.speed;
    double seconds = std::fmod(m_services.epoch_ms / 1000.0 + clock.elapsed, 86400.0);
    if (seconds < 0.0) seconds += 86400.0;
    clock.time_of_day = static_cast<float>(seconds / 86400.0);
    return clock;
}

std::string OfflineSession::Impl::videoRateOverrides() const {
    std::string out { "[" };
    for (std::size_t i = 0; i < m_video_rate_overrides.size(); ++i) {
        const auto& value = m_video_rate_overrides[i];
        if (i != 0) out += ',';
        out += "{\"owner_layer_id\":" + std::to_string(value.owner_layer_id);
        out += ",\"rate_numerator\":" + std::to_string(value.rate_numerator);
        out += ",\"rate_denominator\":" + std::to_string(value.rate_denominator);
        out += ",\"applied_control_count\":" + std::to_string(value.applied_control_count) + '}';
    }
    out += ']';
    return out;
}

// ---- 一帧 ---------------------------------------------------------------------

bool OfflineSession::Impl::runFrame(const FrameClock& clock, const OfflineFrameInput& input) {
    // 顺序见文件顶部说明。
    if (!beginFrame(clock, input)) return false;
    const FrameProfile profile = simulate(clock);
    if (!draw(clock, profile)) return false;
    advance(clock);
    return true;
}

bool OfflineSession::Impl::beginFrame(const FrameClock& clock, const OfflineFrameInput& input) {
    /* The consumer has written last frame's pixels out before stepping again.
     * Hand the buffer back to the renderer *before* the result is reset below
     * (the reset would otherwise free it), so this frame's readback reuses the
     * allocation instead of zero-filling a fresh full-frame buffer. */
    m_render->recycleCpuPixels(rstd::move(m_cpu_frame.pixels));
    m_cpu_frame = CpuFrameResult {};
    m_step_status = OfflineStepStatus::NotReady;
    m_step_index = clock.index;
    m_pointer = array<float, 2> { static_cast<float>(input.cursor_x), static_cast<float>(input.cursor_y) };
    m_cursor_in_window = input.cursor_in_window;
    const uint32_t previous = m_buttons_down;
    m_buttons_down = input.mouse_buttons_down;
    m_buttons_pressed = input.mouse_buttons_down & ~previous;
    m_buttons_released = previous & ~input.mouse_buttons_down;
    if (input.media.is_some()) applyMediaStatus(*input.media);
    if (input.pcm.is_some()) {
        audio::ResponseFrame response {};
        if (!m_audio_response_engine.analyze(*input.pcm, response)) {
            invalidateFrame(clock.index, "PCM snapshot rejected: expected 4096 stereo frames at 48000 Hz with a fresh nonzero sequence");
            return false;
        }
        m_scene_audio_response.submit(rstd::move(response));
    } else {
        // An omitted snapshot means deterministic silence, not stale live PCM.
        m_audio_response_engine.end();
        m_scene_audio_response.end();
    }
    m_scene->Runtime().PrepareOfflineFrame(u64(clock.index), f64(clock.elapsed), f64(clock.scaled_dt));
    return true;
}

OfflineSession::Impl::FrameProfile OfflineSession::Impl::simulate(const FrameClock& clock) {
    FrameProfile profile;
    if (m_profile) profile.scene_started = std::chrono::steady_clock::now();
    m_scene->SetPointerPosition(array<float, 2> { m_pointer[usize()], m_pointer[usize(1)] });
    if (m_uniform_input) m_uniform_input->SetPointerInput(m_pointer[usize()], m_pointer[usize(1)]);
    // Drive any per-Scene scenescripts before particle emission.
    // Scripts mutate SceneNode transforms (scale/origin/angles) so
    // they need to run before the matrix-derivation in the
    // uniform evaluation runs inside drawFrame after scripts have updated Scene state.
    // The runtime is a no-op when no ScriptScene is installed.
    owe::script::FrameInputs fi;
    fi.frametime   = static_cast<float>(clock.scaled_dt);
    fi.runtime     = m_scene->Runtime().Frame().elapsed.to_primitive();
    fi.time_of_day = clock.time_of_day;
    if (m_uniform_input) m_uniform_input->SetTimeOfDay(fi.time_of_day);
    auto ortho     = m_scene->Ortho();
    fi.canvas_w    = static_cast<float>(ortho[usize()].to_primitive());
    fi.canvas_h    = static_cast<float>(ortho[usize(1)].to_primitive());
    fi.screen_w    = fi.canvas_w;
    fi.screen_h    = fi.canvas_h;
    fi.cursor_x    = m_pointer[usize()];
    fi.cursor_y    = m_pointer[usize(1)];
    fi.cursor_in_window       = m_cursor_in_window;
    fi.mouse_buttons_down     = m_buttons_down;
    fi.mouse_buttons_pressed  = m_buttons_pressed;
    fi.mouse_buttons_released = m_buttons_released;
    (void)m_scene_audio_response.advance(fi.frametime, fi.audio);
    if (m_uniform_input) m_uniform_input->SetAudioSpectrum(fi.audio);
    m_scene->TickNodeFieldAnimations();
    const auto script_started = m_profile ? std::chrono::steady_clock::now() : std::chrono::steady_clock::time_point{};
    owe::script::TickSceneScripts(*m_scene, fi);
    if (m_profile) profile.script_ms = std::chrono::duration<double,std::milli>(std::chrono::steady_clock::now()-script_started).count();
    m_scene->TickCameraPaths(&m_services);
    m_scene->TickMaterialShaderAnimations();
    m_scene->TickTransformUpdaters();
    if (m_profile) profile.scene_finished = std::chrono::steady_clock::now();
    return profile;
}

bool OfflineSession::Impl::draw(const FrameClock& clock, const FrameProfile& profile) {
    // CPU scripts/audio for the next frame may run while the previous GPU
    // frame is in flight. Drain before any mesh/material/texture mutation.
    if (const auto error = m_render->finishPendingFrame(); !error.empty()) {
        invalidateFrame(clock.index, error);
        return false;
    }
    const auto pending_finished = m_profile ? std::chrono::steady_clock::now() : std::chrono::steady_clock::time_point{};
    if (m_scene->ConsumeRenderGraphDirty()) {
        rebuildRenderGraph(
            vulkan::RenderGraphResourceRetention::KeepSceneTextures, false);
    }
    m_scene->Runtime().BeforeRender();
    refreshPreparedRenderTargetDirtyEvents();
    refreshPreparedMeshDirtyEvents();
    refreshPreparedMaterialDirtyEvents();

    // 不读回的帧（预热、步长取样之间的帧）只模拟：下面的资源推进照做，跳过光栅、提交与读回；
    // 帧反馈场景、要覆盖度的输出段照常画。
    const bool reads = m_options.readsFrame(clock.index);
    const bool raster = reads || m_reads_previous_frame ||
        (m_raster_every_output_frame && clock.index >= m_options.readback_start);

    /* Advance video textures (no-op if none) before drawFrame so
     * the new RGBA frame is sampled by the same render pass. */
    m_render->pumpVideoTextures(clock.scaled_dt, &m_services, raster);

    /* Upload any glyph rects the actuators added this tick. Runs after
     * TickSceneScripts (which calls FontFace::Populate) and before
     * drawFrame so newly-rasterised glyphs are visible the same frame. */
    m_render->pumpFontAtlases(*m_scene);
    const auto resources_finished = m_profile ? std::chrono::steady_clock::now() : std::chrono::steady_clock::time_point{};

    if (m_services.failed) return false;
    m_cpu_frame = m_render->drawFrameCpu(*m_scene, reads, raster);
    if (m_profile) {
        m_cpu_frame.cpu_scene_ms = std::chrono::duration<double,std::milli>(profile.scene_finished-profile.scene_started).count();
        m_cpu_frame.cpu_script_ms = profile.script_ms;
        m_cpu_frame.cpu_pending_wait_ms = std::chrono::duration<double,std::milli>(pending_finished-profile.scene_finished).count();
        m_cpu_frame.cpu_resources_ms = std::chrono::duration<double,std::milli>(resources_finished-pending_finished).count();
    }
    m_cpu_frame.frame_index = clock.index;
    if (!m_cpu_frame.completed() && !m_cpu_frame.submitted()) return false;
    const auto check_started = m_profile ? std::chrono::steady_clock::now() : std::chrono::steady_clock::time_point{};
    if (const auto pass = m_render->firstUnpreparedPass()) {
        const std::string message = "Offline frame omitted an unprepared render pass: " + *pass;
        m_services.diagnose(message, true);
        invalidateFrame(clock.index, message);
        return false;
    }
    if (m_profile)
        m_cpu_frame.cpu_pass_check_ms = std::chrono::duration<double,std::milli>(std::chrono::steady_clock::now()-check_started).count();
    m_step_status = OfflineStepStatus::Drawn;
    return true;
}

void OfflineSession::Impl::advance(const FrameClock& clock) {
    m_scene->Runtime().AdvanceOffline(f64(clock.scaled_dt), f64(clock.next_elapsed));
}

// ---- 场景与渲染图 -------------------------------------------------------------

void OfflineSession::Impl::rebuildRenderGraph(vulkan::RenderGraphResourceRetention retention,
                                              bool                                 evict_meshes) {
    if (! m_scene || ! m_render->inited()) return;
    if (m_rg.is_some()) m_render->clearLastRenderGraph(retention);
    if (evict_meshes) m_render->evictUnusedMeshes();
    m_render->UpdateCameraFillMode(*m_scene, m_config.fill_mode);
    m_render->configureRenderTargets(*m_scene);
    m_render_scene = ExtractRenderSceneSnapshot(*m_scene);
    bool reads_previous_frame = false;
    m_rg = Some(sceneToRenderGraph(*m_scene, m_render_scene, &m_layers,
        m_capture_target.has_value() ? &*m_capture_target : nullptr, &reads_previous_frame));
    m_reads_previous_frame |= reads_previous_frame;

    if (m_config.graphviz) (*m_rg)->ToGraphviz("graph.dot"_str);
    m_render->compileRenderGraph(*m_scene, **m_rg, m_render_scene);
    consumeDirtyEventsCoveredByGraphRebuild();
    (void)m_scene->ConsumeRenderGraphDirty();
}

void OfflineSession::Impl::consumeDirtyEventsCoveredByGraphRebuild() {
    if (! m_scene) return;
    (void)m_scene->ConsumePreparedMaterialDirtyEvents();
    (void)m_scene->ConsumePreparedMeshDirtyEvents();
    (void)m_scene->ConsumePreparedRenderTargetDirtyEvents();
}

void OfflineSession::Impl::refreshPreparedRenderTargetDirtyEvents() {
    if (! m_scene || ! m_render->inited() || m_rg.is_none()) return;
    auto events = m_scene->ConsumePreparedRenderTargetDirtyEvents();
    if (events.is_empty()) return;

    m_render->refreshPreparedTextures(*m_scene, m_render_scene);
}

void OfflineSession::Impl::refreshPreparedMeshDirtyEvents() {
    if (! m_scene || ! m_render->inited() || m_rg.is_none()) return;
    auto events = m_scene->ConsumePreparedMeshDirtyEvents();
    if (events.is_empty()) return;

    bool requires_graph_rebuild = events.iter().any([](auto event) {
        return (event->flags & SceneMeshDirtyLayout) != 0;
    });
    if (requires_graph_rebuild) {
        rebuildRenderGraph(vulkan::RenderGraphResourceRetention::KeepSceneTextures, false);
        return;
    }

    for (const auto& event : events) {
        if ((event.flags & SceneMeshDirtyData) == 0) continue;
        m_render->refreshPreparedMesh(
            *m_scene,
            m_render_scene,
            event.mesh,
            vulkan::ToPassInvalidationFlags(vulkan::PassInvalidation::Resources));
    }
}

void OfflineSession::Impl::refreshPreparedMaterialDirtyEvents() {
    if (! m_scene || ! m_render->inited() || m_rg.is_none()) return;
    auto events = m_scene->ConsumePreparedMaterialDirtyEvents();
    if (events.is_empty()) return;

    bool requires_graph_rebuild = events.iter().any([](auto event) {
        return (event->flags & SceneMaterialDirtyGraph) != 0;
    });
    if (requires_graph_rebuild) {
        rebuildRenderGraph(vulkan::RenderGraphResourceRetention::KeepSceneTextures, false);
        return;
    }

    Vec<SceneMaterialId> texture_materials;
    for (const auto& event : events) {
        if ((event.flags & SceneMaterialDirtyTextureBindings) != 0) {
            PushUniqueMaterialId(texture_materials, event.material);
        }
    }
    if (! texture_materials.is_empty() &&
        ! m_render->refreshPreparedMaterialTextures(
            *m_scene, m_render_scene, texture_materials.as_slice())) {
        rebuildRenderGraph(vulkan::RenderGraphResourceRetention::KeepSceneTextures, false);
        return;
    }
    for (const auto& event : events) {
        auto flags = MaterialDirtyToPassInvalidationFlags(event.flags);
        if (flags == vulkan::PassInvalidationNone) continue;
        m_render->refreshPreparedMaterial(*m_scene, m_render_scene, event.material, flags);
    }
}

void OfflineSession::Impl::installScene(Box<Scene> scene, Arc<UniformRuntimeInput> uniform_input) {
    m_scene               = nullptr;
    m_uniform_input       = nullptr;
    m_scene_owner         = Some(rstd::move(scene));
    m_uniform_input_owner = Some(rstd::move(uniform_input));
    m_scene               = m_scene_owner->get();
    m_uniform_input       = m_uniform_input_owner->as_ptr().as_raw_ptr();
    m_scene_audio_response.end();
    rebuildRenderGraph(
        vulkan::RenderGraphResourceRetention::ReleaseSceneTextures, true);
}

void OfflineSession::Impl::setUserProperty(std::string_view key, NJson value) {
    const std::string property = CanonicalSceneUserPropertyKey(std::string(key));
    const auto*       current  = Find(m_user_properties, property);
    NJson             prop     = current != nullptr ? MergeUserPropertyDescriptor(*current, value)
                                                    : MakeUserPropertyDescriptor(std::move(value));
    m_user_properties[property] = prop;
    if (! m_scene) return;

    const SceneUserPropertyMutation mutation = SceneUserPropertyApplier::Apply(*m_scene, property, prop);
    if (mutation.graph_changed) {
        rebuildRenderGraph(
            vulkan::RenderGraphResourceRetention::KeepSceneTextures, false);
        return;
    }
    if (m_render->inited() && m_rg.is_some()) {
        refreshPreparedMaterialDirtyEvents();
    }
}

void OfflineSession::Impl::applyMediaStatus(const MediaStatus& status) {
    if (! m_scene) return;

    owe::script::SetSceneMediaStatus(*m_scene, ToScriptMediaStatus(status));

    (void)SceneUserPropertyApplier::ApplyTexture(
        *m_scene, "$mediaThumbnail", RuntimeTextureProperty(status.art_url));
    (void)SceneUserPropertyApplier::ApplyTexture(
        *m_scene, "$mediaPreviousThumbnail", RuntimeTextureProperty(status.previous_art_url));
    if (m_render->inited() && m_rg.is_some()) refreshPreparedMaterialDirtyEvents();
}

void OfflineSession::Impl::loadScene(const vulkan::DeviceCapabilities& capabilities) {
    if (m_config.source_pkg_path.empty() || m_config.assets_dir.empty()) return;

    rstd_info("loading scene: {}", m_config.source_pkg_path);

    m_mixer->unmount_all();

    // mount assets dir
    Box<fs::VFS> pVfs = Box<fs::VFS>::make();
    auto&        vfs  = *pVfs;
    if (! vfs.is_mounted("assets"_str)) {
        auto assets = fs::make_physical_fs(fs::ToPath(m_config.assets_dir));
        if (assets.is_err() ||
            vfs.mount("/assets"_str, rstd::move(assets).unwrap_unchecked(), "assets"_str)
                .is_err()) {
            rstd_error("Mount assets dir failed");
            return;
        }
    }
    std::filesystem::path pkgPath_fs { m_config.source_pkg_path };
    pkgPath_fs.replace_extension("pkg");
    std::string pkgPath  = pkgPath_fs.string();
    std::string pkgEntry = pkgPath_fs.filename().replace_extension("json").string();
    std::string pkgDir   = pkgPath_fs.parent_path().string();
    std::string scene_id = pkgPath_fs.parent_path().filename().string();
    MergeProjectUserProperties(pkgPath_fs.parent_path(), m_user_properties);

    // load pkgfile. Read pkg version stamp before move-mounting so we can
    // pass it to the scene parser; on fallback (loose dir) we have no
    // version info and use kSceneVersionUnknown.
    wpscene::SceneVersion pkg_v = wpscene::kSceneVersionUnknown;
    {
        auto wfs         = fs::WPPkgFs::open(fs::ToPath(pkgPath));
        bool pkg_mounted = false;
        if (wfs.is_ok()) {
            auto stamp  = wfs->pkg_version_stamp();
            pkg_v       = wpscene::ParsePkgVersionStamp(std::string_view(
                reinterpret_cast<const char*>(stamp.data()), stamp.size().to_primitive()));
            pkg_mounted = vfs.mount("/assets"_str, wfs->mount_handle()).is_ok();
        }
        if (! pkg_mounted) {
            rstd_info("load pkg file {} failed, fallback to use dir", pkgPath);
            pkg_v      = wpscene::kSceneVersionUnknown;
            auto loose = fs::make_physical_fs(fs::ToPath(pkgDir));
            if (loose.is_err() ||
                vfs.mount("/assets"_str, rstd::move(loose).unwrap_unchecked()).is_err()) {
                rstd_error("can't load pkg directory: {}", pkgDir);
                return;
            }
        }
    }
    Option<ParsedScene> parsed_scene;
    {
        const std::string base { "/assets/" };
        auto              scene_doc = m_config.scene_document;
        if (! scene_doc) {
            auto loaded = wpscene::LoadSceneDocumentFromVfs(vfs, base + pkgEntry, pkg_v);
            if (loaded) scene_doc = std::make_shared<wpscene::SceneDocument>(rstd::move(*loaded));
        }
        if (! scene_doc) {
            rstd_error("Not supported scene type");
            return;
        }
        Option<rstd::path::PathBuf> shader_cache_dir;
        if (! m_config.cache_dir.empty()) {
            shader_cache_dir =
                Some(rstd::path::PathBuf::from(rstd::cppstd::as_str(m_config.cache_dir).unwrap()));
            rstd_info("shader cache folder: {}", m_config.cache_dir);
        }
        SceneParser parser;
        auto        parsed = parser.Parse(
            rstd::cppstd::as_str(scene_id).unwrap(),
            rstd::ref<wpscene::SceneDocument>::from_raw_parts(scene_doc.get()),
            rstd::mut_ref<fs::VFS>::from_raw_parts(&vfs),
            rstd::mut_ref<owe::media::OfflineMixer>::from_raw_parts(m_mixer.get()),
            SceneParseOptions {
                .user_properties  = &m_user_properties,
                .shader_cache_dir = rstd::move(shader_cache_dir),
                .capabilities =
                    SceneParseCapabilities {
                        .directional_shadow = capabilities.directional_shadow(),
                        .max_geometry_output_vertices =
                            u32(capabilities.max_geometry_output_vertices),
                        .max_geometry_total_output_components =
                            u32(capabilities.max_geometry_total_output_components),
                    },
                // R4：解析一开始就要有 Services（BuildContext 里相机字段脚本就会建脚本运行时）。
                .services = &m_services,
                .hdr_scale = m_config.hdr_scale,
            });
        if (parsed.is_err()) {
            rstd_error("scene parse failed: {}", parsed.unwrap_err().message.as_str());
            return;
        }
        parsed_scene = Some(rstd::move(parsed).unwrap());
        auto& scene  = parsed_scene->scene;
        scene->InstallExtension(rstd::move(pVfs));
        (void)SceneUserPropertyApplier::ApplyAll(*scene, m_user_properties);
    }

    auto parsed = rstd::move(parsed_scene).unwrap();
    installScene(rstd::move(parsed.scene), rstd::move(parsed.runtime_input));
}

// ---- 会话 ---------------------------------------------------------------------

bool OfflineSession::Impl::init(SessionConfig config, RenderInitInfo info, OfflineOptions options) {
    if (m_started) { m_error = "Offline mode requires a fresh OfflineSession"; return false; }
    m_started = true;
    if (options.readback_stride == 0 ||
        (options.readback_phase && *options.readback_phase >= options.readback_stride)) {
        m_error = "Invalid offline readback stride or phase";
        return false;
    }
    if (!std::isfinite(options.epoch_ms) || !std::isfinite(config.speed) || config.speed <= 0.0f) {
        m_error = "Invalid offline epoch or playback speed";
        return false;
    }
    if ((options.fps_num == 0) != (options.fps_den == 0)) {
        m_error = "Offline rational FPS requires a nonzero numerator and denominator";
        return false;
    }
    m_options = options;
    m_raster_every_output_frame = info.collect_sampling_coverage && !info.sampling_coverage_sampled_only;
    m_profile = info.gpu_timing;
    m_layers = info.layer_selection;
    m_capture_target = info.capture_target;
    m_services.epoch_ms = options.epoch_ms;
    m_services.trace_scene = options.trace_scene;
    std::seed_seq seed { uint32_t(options.seed), uint32_t(options.seed >> 32) };
    m_services.random.seed(seed);
    m_services.seed = options.seed;
    m_services.object_random.clear();

    m_config = rstd::move(config);
    m_user_properties = NormalizeUserProperties(m_config.user_properties);
    m_mixer->set_volume(m_config.volume);
    m_mixer->set_volume_scale(m_config.volume_scale, 0);
    m_mixer->set_muted(m_config.muted);

    if (m_render->init(rstd::move(info)) && m_render->inited()) loadScene(m_render->deviceCapabilities());
    if (!m_render->inited()) m_error = "Offline Vulkan initialization failed";
    else if (!hasScene()) m_error = "Offline scene loading failed; see engine diagnostics";
    else if (m_services.failed) m_error = "Offline script initialization failed";
    else {
        auto applied = m_scene->ApplyOfflineVideoPlaybackRateOverrides(
            std::span<const OfflineVideoPlaybackRateOverride>(
                options.video_rate_overrides.data(), options.video_rate_overrides.size()));
        if (!applied.ok()) {
            m_error = applied.error;
            m_failed = true;
            return false;
        }
        m_video_rate_overrides = rstd::move(applied.applied);
        m_mixer->play(); m_inited = true; return true;
    }
    m_failed = true;
    return false;
}

bool OfflineSession::Impl::step(uint64_t index, double dt, const OfflineFrameInput& input) {
    m_audio_frame = OfflineAudioFrame {};
    if (!m_inited || m_failed) {
        if (m_error.empty()) m_error = "Offline renderer is not ready";
        invalidateFrame(index, m_error);
        return false;
    }
    if (index != m_next_frame || index == std::numeric_limits<uint64_t>::max() || !std::isfinite(dt) || dt <= 0.0 ||
        (m_next_frame != 0 && dt != m_dt) ||
        (m_options.fps_num && dt != double(m_options.fps_den) / m_options.fps_num) ||
        !std::isfinite(input.cursor_x) || !std::isfinite(input.cursor_y)) {
        m_error = "Expected the next sequential frame, a fixed positive dt, and finite cursor coordinates";
        invalidateFrame(index, m_error);
        return false;
    }
    m_dt = dt;
    const FrameClock clock = offlineClock(index);
    m_services.elapsed = clock.elapsed;
    if (!runFrame(clock, input) || m_services.failed) {
        // Scripts/particles may have mutated before a GPU error. Retrying the
        // same index is unsafe; require a fresh load and replay instead.
        m_failed = true;
        m_error = m_cpu_frame.message;
        if (m_services.failed && !m_services.diagnostics.empty())
            m_error = m_services.diagnostics.back();
        if (m_error.empty()) m_error = "Offline frame failed; reload and replay required";
        if (m_cpu_frame.completed() || m_cpu_frame.message.empty())
            invalidateFrame(index, m_error);
        return false;
    }
    // Compute cumulative sample boundaries rather than rounding dt * rate each
    // frame (e.g. 144 fps must alternate sample counts). Audio retains the
    // engine's independent playback clock when visual playback speed changes.
    auto boundary = [&](uint64_t frame) -> long double {
        if (m_options.fps_num) {
            // 64-bit index * 32-bit denominator * 48000 fits 128 bits.
            const unsigned __int128 numerator = static_cast<unsigned __int128>(frame) *
                m_options.fps_den * 48000;
            return static_cast<long double>(numerator / m_options.fps_num);
        }
        return std::floor(static_cast<long double>(frame) * dt * 48000.0L);
    };
    const long double start = boundary(index);
    const long double end = boundary(index + 1);
    if (!std::isfinite(end) || start < 0 || end < start ||
        end >= static_cast<long double>(std::numeric_limits<uint64_t>::max()) ||
        end - start > static_cast<long double>(std::numeric_limits<uint32_t>::max())) {
        m_error = "Offline audio sample interval exceeds supported integer range";
        m_failed = true;
        invalidateFrame(index, m_error);
        return false;
    }
    m_audio_frame.sample_start = static_cast<uint64_t>(start);
    m_audio_frame.frame_count = static_cast<uint32_t>(end - start);
    m_audio_frame.samples.resize(static_cast<size_t>(m_audio_frame.frame_count) * 2);
    const bool mixed = m_mixer->mix(m_audio_frame.samples);
    if (!mixed || m_services.failed) {
        m_error = !mixed ? m_mixer->last_error() :
            (m_services.diagnostics.empty() ? "Offline audio source failed" : m_services.diagnostics.back());
        m_failed = true;
        m_audio_frame = OfflineAudioFrame {};
        invalidateFrame(index, m_error);
        return false;
    }
    ++m_next_frame;
    return true;
}

// ---- 场景描述（trace_scene） -------------------------------------------------

std::string OfflineSession::Impl::describeProjection() const {
    if (!m_scene) return "null";
    auto active = m_scene->ActiveCamera();
    if (active.is_none()) return "null";

    std::string active_name;
    const auto names = m_scene->CameraNames();
    for (usize index {}; index < names.len(); ++index) {
        auto candidate = m_scene->Camera(names[index].as_str());
        if (candidate.is_some() && (*candidate).as_raw_ptr() == (*active).as_raw_ptr()) {
            active_name = rstd::cppstd::to_string(names[index].as_str());
            break;
        }
    }
    const auto position = (*active)->GetPosition();
    std::ostringstream out;
    out << "{\"frame_index\":" << m_step_index
        << ",\"active_camera_name\":"
        << (active_name.empty() ? "null" : Dump(NJson(active_name)))
        << ",\"active_camera_is_perspective\":"
        << ((*active)->IsPerspective() ? "true" : "false")
        << ",\"active_camera_position\":[" << position.x() << ',' << position.y() << ','
        << position.z() << ']'
        << ",\"active_camera_width\":" << (*active)->Width()
        << ",\"active_camera_height\":" << (*active)->Height();
    const auto ortho = m_scene->Ortho();
    out << ",\"scene_ortho\":[" << ortho[usize()].to_primitive() << ','
        << ortho[usize(1)].to_primitive() << ']'
        << ",\"viewport_scale\":" << m_scene->ViewportScale().to_primitive()
        << ",\"camera_parallax\":null}";
    return out.str();
}

std::string OfflineSession::Impl::describeScene() const {
    if (!m_scene) return "[]";
    std::ostringstream out;
    out << '[';
    bool first = true;
    std::function<void(SceneNode*, std::int32_t)> visit = [&](SceneNode* node, std::int32_t inherited) {
        if (node == nullptr) return;
        const auto identity = node->WallpaperIdentity();
        const auto generator = node->GeneratorIdentity();
        const std::int32_t owner = generator.is_some() ? generator->value.to_primitive() :
            (identity.is_some() ? identity->value.to_primitive() : inherited);
        auto effective_parallax =
            m_uniform_input ? m_uniform_input->EffectiveParallax(*node)
                            : None<UniformEffectiveParallax>();
        if (identity.is_some() || generator.is_some() ||
            (owner >= 0 && node->Mesh() != nullptr)) {
            if (!first) out << ',';
            first = false;
            out << "{\"id\":" << (identity.is_some() ? identity->value.to_primitive() : -1)
                << ",\"owner\":" << owner << ",\"parent\":" << inherited
                << ",\"name\":" << Dump(NJson(node->Name()))
                << ",\"visible\":" << (node->Visible() ? "true" : "false")
                << ",\"has_mesh\":" << (node->Mesh() != nullptr ? "true" : "false")
                << ",\"has_effect_layer\":" << (node->HasLayer() ? "true" : "false")
                << ",\"render_group\":" << (identity.is_some() && m_scene->RenderGroupCamera(*identity).is_some() ? "true" : "false")
                << ",\"effective_parallax_depth\":";
            if (effective_parallax.is_some()) {
                out << '[' << OfflineShortestNumber((*effective_parallax).depth[usize()]) << ','
                    << OfflineShortestNumber((*effective_parallax).depth[usize(1)]) << ']';
            } else {
                out << "null";
            }
            out << ",\"effective_parallax_source_id\":"
                << (effective_parallax.is_some()
                        ? std::to_string((*effective_parallax).source_object_id.to_primitive())
                        : std::string("null"))
                << ",\"materials\":[";
            bool first_material = true;
            auto append_materials = [&](SceneNode* render_node, std::string_view role) {
            if (auto* mesh = render_node->Mesh(); mesh != nullptr) for (const auto& material : mesh->MaterialSlots()) {
                if (!material) continue;
                if (!first_material) out << ',';
                first_material = false;
                std::vector<std::string> active_uniforms;
                bool                     uses_audio_spectrum { false };
                const bool uses_system_media_thumbnail =
                    m_scene->MaterialHasTextureUserBinding(*material, "$mediaThumbnail"_str) ||
                    m_scene->MaterialHasTextureUserBinding(*material, "$mediaPreviousThumbnail"_str);
                auto is_audio_spectrum = [](std::string_view name) {
                    return name == "g_AudioSpectrum16Left" ||
                           name == "g_AudioSpectrum16Right" ||
                           name == "g_AudioSpectrum32Left" ||
                           name == "g_AudioSpectrum32Right" ||
                           name == "g_AudioSpectrum64Left" ||
                           name == "g_AudioSpectrum64Right";
                };
                if (material->customShader.variant.is_some()) {
                    for (const auto& stage : material->customShader.variant->stages) {
                        for (const auto& [name, _] : stage.uniforms) {
                            bool seen { false };
                            for (const auto& existing : active_uniforms)
                                seen = seen || existing == name;
                            if (! seen) active_uniforms.push_back(name);
                            uses_audio_spectrum = uses_audio_spectrum || is_audio_spectrum(name);
                        }
                    }
                }
                out << "{\"shader\":" << Dump(NJson(material->customShader.shader ? material->customShader.shader->name : std::string()))
                    << ",\"role\":" << Dump(NJson(role))
                    << ",\"blend\":" << static_cast<int>(material->blenmode)
                    << ",\"uses_audio_spectrum\":" << (uses_audio_spectrum ? "true" : "false")
                    << ",\"uses_system_media_thumbnail\":"
                    << (uses_system_media_thumbnail ? "true" : "false")
                    << ",\"active_uniforms\":[";
                bool first_uniform = true;
                for (const auto& name : active_uniforms) {
                    if (! first_uniform) out << ',';
                    first_uniform = false;
                    out << Dump(NJson(name));
                }
                out << "],\"textures\":[";
                bool first_texture = true;
                for (const auto& texture : material->textures) {
                    if (!first_texture) out << ',';
                    first_texture = false;
                    out << Dump(NJson(texture));
                }
                out << "]}";
            }
            };
            append_materials(node, "source");
            if (node->HasLayer()) for (auto* effect : node->Layer()->ResolvedEffects())
                if (effect != nullptr) for (auto& effect_node : effect->nodes)
                    if (effect_node.sceneNode) append_materials(effect_node.sceneNode.as_ptr(), "effect");
            out << "]}";
        }
        for (const auto& child : node->GetChildren()) visit(child.as_ptr(), owner);
    };
    visit(m_scene->RootMut().as_raw_ptr(), -1);
    out << ']';
    return out.str();
}

} // namespace owe

// ---- 公开接口 -----------------------------------------------------------------

OfflineSession::OfflineSession(): m_impl(std::make_unique<Impl>()) {}

OfflineSession::~OfflineSession() = default;

bool OfflineSession::init(SessionConfig config, RenderInitInfo info, OfflineOptions options) {
    return m_impl->init(rstd::move(config), rstd::move(info), options);
}

bool OfflineSession::step(uint64_t index, double dt, const OfflineFrameInput& input) {
    return m_impl->step(index, dt, input);
}

void OfflineSession::setUserProperty(std::string_view key, NJson value) {
    m_impl->setUserProperty(key, rstd::move(value));
}

const CpuFrameResult& OfflineSession::readback() const { return m_impl->m_cpu_frame; }

OfflineStepStatus OfflineSession::stepStatus() const { return m_impl->m_step_status; }

const OfflineAudioFrame& OfflineSession::audioReadback() const { return m_impl->m_audio_frame; }

std::string OfflineSession::error() const { return m_impl->m_error; }

std::vector<std::string> OfflineSession::diagnostics() const { return m_impl->m_services.diagnostics; }

const std::vector<OfflineSourceScriptError>& OfflineSession::sourceScriptErrors() const {
    return m_impl->m_services.source_script_errors;
}

std::vector<OfflineDependency> OfflineSession::dependencies() const {
    return m_impl->m_services.dependencies;
}

uint64_t OfflineSession::ikChainSolves() const { return m_impl->m_services.runtime_ik_chain_solves; }

std::string OfflineSession::sceneDescription() const { return m_impl->describeScene(); }

std::string OfflineSession::animationPeriods() const { return m_impl->animationPeriods(); }

std::string OfflineSession::projection() const { return m_impl->describeProjection(); }

std::string OfflineSession::videoRateOverrides() const { return m_impl->videoRateOverrides(); }

void OfflineSession::deviceUuid(uint8_t out[16]) const { m_impl->m_render->deviceUuid(out); }
