module;

export module wescene.timer:frame_timer;
import wescene.core;
import rstd.cppstd;

import :thread_timer;

export namespace owe
{

class FrameTimer : NoCopy, NoMove {
    constexpr static std::size_t FRAMETIME_QUEUE_SIZE { 5 };

public:
    FrameTimer(std::function<void()> callback = {});
    ~FrameTimer();

    void SetCallback(const std::function<void()>&);

    void Run();
    void Stop();

    u16    RequiredFps() const;
    bool   Running() const;
    double FrameTime() const;
    double TargetFrameTime() const;

    void SetRequiredFps(u16);

    void FrameBegin();
    void FrameEnd();

private:
    void AddFrametime(std::chrono::microseconds);
    void UpdateFrametime();

    std::function<void()>                 m_callback;
    std::deque<std::chrono::microseconds> m_frametime_queue;

    u16                                    m_req_fps;
    std::atomic<std::chrono::microseconds> m_frametime;
    std::atomic<std::chrono::microseconds> m_ideatime;
    std::atomic<rstd::int32_t>             m_frame_busy_count;

    ThreadTimer m_timer;

    std::chrono::time_point<std::chrono::steady_clock> m_clock;
};

} // namespace owe
