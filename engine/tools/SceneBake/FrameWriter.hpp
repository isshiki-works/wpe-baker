// SPDX-License-Identifier: MIT
#pragma once
// 帧像素写出线程：写第 N 帧（管道背压、磁盘）的同时渲染第 N+1 帧。
// 最多一帧在写；push 交出本帧缓冲、换回上一帧写完的缓冲，缓冲在渲染器与写线程之间轮换，
// 不再每帧分配。写出的字节与顺序和同步写完全相同，只是写失败要到下一次 push 或 finish 才抛出。
// threaded=false 时 push 在调用方线程上同步写（WPE_SERIAL_FRAMES 回退用）。
#include <condition_variable>
#include <cstdint>
#include <exception>
#include <functional>
#include <mutex>
#include <thread>
#include <utility>
#include <vector>

class FrameWriter {
public:
    using Write = std::function<void(const std::vector<std::uint8_t>&)>;

    FrameWriter(Write write, bool threaded): m_write(std::move(write)) {
        if (threaded) m_thread = std::thread([this] { run(); });
    }
    FrameWriter(const FrameWriter&)            = delete;
    FrameWriter& operator=(const FrameWriter&) = delete;
    // 出错退出时也等在写的那帧写完：消费方要么读走、要么关管道让写失败，不会一直卡住。
    ~FrameWriter() {
        {
            std::lock_guard lock(m_mutex);
            m_stop = true;
        }
        m_changed.notify_all();
        if (m_thread.joinable()) m_thread.join();
    }

    // 交出 pixels 去写；返回时 pixels 里是上一帧写完的缓冲（第一帧是空的）。
    void push(std::vector<std::uint8_t>& pixels) {
        if (! m_thread.joinable()) {
            m_write(pixels);
            return;
        }
        std::unique_lock lock(m_mutex);
        m_changed.wait(lock, [&] { return ! m_busy; });
        if (m_error) std::rethrow_exception(m_error);
        std::swap(pixels, m_frame);
        m_busy = true;
        m_changed.notify_all();
    }

    // 等最后一帧写完；任何一帧写失败都在这里抛出。
    void finish() {
        if (! m_thread.joinable()) return;
        std::unique_lock lock(m_mutex);
        m_changed.wait(lock, [&] { return ! m_busy; });
        if (m_error) std::rethrow_exception(m_error);
    }

private:
    void run() {
        std::unique_lock lock(m_mutex);
        for (;;) {
            m_changed.wait(lock, [&] { return m_busy || m_stop; });
            if (! m_busy) return;
            lock.unlock();
            std::exception_ptr error;
            try {
                m_write(m_frame);
            } catch (...) {
                error = std::current_exception();
            }
            lock.lock();
            // 失败后 push 先抛出、不再交新帧，后面的帧不会被写。
            if (error) m_error = error;
            m_busy = false;
            m_changed.notify_all();
        }
    }

    Write                     m_write;
    std::mutex                m_mutex;
    std::condition_variable   m_changed;
    std::vector<std::uint8_t> m_frame;
    std::exception_ptr        m_error;
    bool                      m_busy { false };
    bool                      m_stop { false };
    std::thread               m_thread;
};
