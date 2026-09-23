module;
#include <memory>
#include <new>

export module wescene.scene:runtime;
import rstd;

using namespace rstd::prelude;

export namespace owe
{

struct SceneFrame {
    u64 index { 0 };
    f64 elapsed { 0.0 };
    f64 delta { 0.0 };
    u64 revision { 1 };
};

// 每帧推进的场景运行时系统。
struct SceneRuntimeSystem {
    virtual ~SceneRuntimeSystem() = default;

    virtual void Update(ref<SceneFrame> frame) = 0;
};

enum class SceneRuntimeSchedule
{
    FrameAdvance,
    BeforeRender,
};

class SceneRuntime {
public:
    const SceneFrame& Frame() const noexcept { return m_frame; }

    // Sets the frame clock without running systems. Offline callers still run
    // BeforeRender and Advance in the normal order for every exported frame.
    void PrepareOfflineFrame(u64 index, f64 elapsed, f64 delta) {
        m_frame.index = index;
        m_frame.elapsed = elapsed;
        m_frame.delta = delta;
        ++m_frame.revision;
        if (m_frame.revision == u64()) m_frame.revision = u64(1);
    }

    void Register(std::unique_ptr<SceneRuntimeSystem> system,
                  SceneRuntimeSchedule         schedule = SceneRuntimeSchedule::FrameAdvance) {
        if (schedule == SceneRuntimeSchedule::BeforeRender) {
            m_before_render.push(rstd::move(system));
        } else {
            m_frame_advance.push(rstd::move(system));
        }
    }

    template<typename T>
    void RegisterSystem(T                    system,
                        SceneRuntimeSchedule schedule = SceneRuntimeSchedule::FrameAdvance) {
        Register(std::unique_ptr<SceneRuntimeSystem>(std::make_unique<T>(rstd::move(system))), schedule);
    }

    void Advance(f64 delta) {
        AdvanceOffline(delta, m_frame.elapsed + delta);
    }

    void AdvanceOffline(f64 delta, f64 next_elapsed) {
        m_frame.delta = delta;
        m_frame.elapsed = next_elapsed;
        ++m_frame.index;
        ++m_frame.revision;
        if (m_frame.revision == u64()) m_frame.revision = u64(1);

        UpdateSystems(m_frame_advance);
    }

    void BeforeRender() { UpdateSystems(m_before_render); }

private:
    void UpdateSystems(rstd::vec::Vec<std::unique_ptr<SceneRuntimeSystem>>& systems) {
        auto frame = ref<SceneFrame>::from_raw_parts(rstd::addressof(m_frame));
        for (auto& system : systems) system->Update(frame);
    }

    SceneFrame                                   m_frame;
    rstd::vec::Vec<std::unique_ptr<SceneRuntimeSystem>> m_frame_advance;
    rstd::vec::Vec<std::unique_ptr<SceneRuntimeSystem>> m_before_render;
};

} // namespace owe
