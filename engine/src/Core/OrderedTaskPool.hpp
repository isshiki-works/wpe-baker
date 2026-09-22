// 固定线程数的有序任务池（取代 rstd::thread 的 ThreadPool / BlockingTaskSet / BlockingTaskGroup）。
//
// 任务按提交顺序编号；Next() 严格按提交顺序交回结果，与各任务谁先做完无关。
// 所以调用方拿到结果、据此做后续工作（纹理上传、拼结果数组）的顺序每次运行都一样。
#pragma once

#include <condition_variable>
#include <cstddef>
#include <deque>
#include <functional>
#include <memory>
#include <mutex>
#include <optional>
#include <thread>
#include <utility>
#include <vector>

namespace owe
{

template<typename T>
class OrderedTaskPool {
public:
    explicit OrderedTaskPool(std::size_t workers) {
        for (std::size_t index = 0; index < workers; ++index)
            m_threads.emplace_back([this] { Work(); });
    }

    OrderedTaskPool(const OrderedTaskPool&)                    = delete;
    auto operator=(const OrderedTaskPool&) -> OrderedTaskPool& = delete;

    // 未开始的任务直接丢弃；正在做的任务做完后线程退出。
    ~OrderedTaskPool() {
        {
            std::lock_guard lock(m_mutex);
            m_stopping = true;
            m_queue.clear();
        }
        m_work.notify_all();
        for (auto& thread : m_threads) thread.join();
    }

    // 任务可以只可移动（捕获 rstd 的 Arc/String 等）；std::function 要求可复制，所以包一层 shared_ptr。
    template<typename F>
    void Submit(F&& task) {
        auto shared = std::make_shared<std::decay_t<F>>(std::forward<F>(task));
        {
            std::lock_guard lock(m_mutex);
            m_queue.emplace_back(m_submitted++, [shared]() -> T { return (*shared)(); });
            m_results.emplace_back();
        }
        m_work.notify_one();
    }

    // 已提交但还没被 Next() 取走的任务数（含排队中、执行中、已完成未取）。
    auto InFlight() -> std::size_t {
        std::lock_guard lock(m_mutex);
        return m_submitted - m_taken;
    }

    // 阻塞到下一个（按提交顺序）结果就绪。调用前必须至少有一个未取走的任务。
    auto Next() -> T {
        std::unique_lock lock(m_mutex);
        m_done.wait(lock, [this] { return m_results.front().has_value(); });
        T value = std::move(*m_results.front());
        m_results.pop_front();
        ++m_taken;
        return value;
    }

private:
    void Work() {
        std::unique_lock lock(m_mutex);
        for (;;) {
            m_work.wait(lock, [this] { return m_stopping || ! m_queue.empty(); });
            if (m_stopping) return;
            auto [id, task] = std::move(m_queue.front());
            m_queue.pop_front();
            lock.unlock();
            T value = task();
            lock.lock();
            // id 之前的结果可能已被取走，所以按 id - m_taken 定位。
            m_results[id - m_taken].emplace(std::move(value));
            m_done.notify_all();
        }
    }

    std::mutex                                           m_mutex;
    std::condition_variable                              m_work;
    std::condition_variable                              m_done;
    std::deque<std::pair<std::size_t, std::function<T()>>> m_queue;
    std::deque<std::optional<T>>                         m_results;
    std::size_t                                          m_submitted {};
    std::size_t                                          m_taken {};
    bool                                                 m_stopping { false };
    std::vector<std::thread>                             m_threads;
};

} // namespace owe
