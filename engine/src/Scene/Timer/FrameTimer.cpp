module;
#include <owe/compat.hpp>
#include <owe/std.hpp>
#include <new>

module wescene.timer;
import wescene.core;

using namespace owe;
using micros = std::chrono::microseconds;
using namespace std::chrono;

FrameTimer::FrameTimer(std::function<void()> cb)
    : m_callback(cb), m_frame_busy_count(0), m_timer([this]() {
          microseconds wait_time = m_frametime.load();
          auto         ideatime  = m_ideatime.load();
          wait_time              = wait_time > ideatime ? wait_time / 2 : ideatime;
          m_timer.SetInterval(wait_time);

          if (m_callback && m_frame_busy_count <= 3) {
              m_frame_busy_count++;
              m_callback();
          }
      }) {
    SetRequiredFps(u16(15));
}

FrameTimer::~FrameTimer() {};

u16 FrameTimer::RequiredFps() const { return m_req_fps; }

double FrameTimer::FrameTime() const {
    return duration_cast<duration<double>>(m_frametime.load()).count();
}

double FrameTimer::TargetFrameTime() const {
    return duration_cast<duration<double>>(m_ideatime.load()).count();
}

void FrameTimer::UpdateFrametime() {
    m_frametime.store(
        std::accumulate(m_frametime_queue.begin(), m_frametime_queue.end(), microseconds(0)) /
        m_frametime_queue.size());
}

void FrameTimer::SetRequiredFps(u16 value) {
    m_req_fps             = value;
    microseconds ideatime = microseconds(1'000'000 / m_req_fps.to_primitive());
    m_ideatime            = ideatime;
    for (std::size_t i = 0; i < FrameTimer::FRAMETIME_QUEUE_SIZE; i++) {
        AddFrametime(ideatime);
    }
    UpdateFrametime();
}

void FrameTimer::AddFrametime(micros t) {
    m_frametime_queue.push_back(t);
    while (m_frametime_queue.size() > FrameTimer::FRAMETIME_QUEUE_SIZE) {
        m_frametime_queue.pop_front();
    }
}

void FrameTimer::FrameBegin() { m_clock = steady_clock::now(); }
void FrameTimer::FrameEnd() {
    auto now = steady_clock::now();
    AddFrametime(duration_cast<microseconds>(now - m_clock));
    UpdateFrametime();

    rstd::int32_t expected = m_frame_busy_count.load();
    while (expected > 0) {
        if (m_frame_busy_count.compare_exchange_weak(expected, expected - 1)) {
            break;
        }
    }
}

void FrameTimer::SetCallback(const std::function<void()>& cb) {
    if (! Running()) m_callback = cb;
}
void FrameTimer::Run() { m_timer.Start(); }
void FrameTimer::Stop() { m_timer.Stop(); }
bool FrameTimer::Running() const { return m_timer.Running(); }
