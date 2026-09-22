module;

export module wescene.timer:thread_timer;
import wescene.core;
import rstd.cppstd;

export namespace owe
{

class ThreadTimer : NoCopy, NoMove {
public:
    ThreadTimer(std::function<void()> callback);
    ~ThreadTimer();

    void Start();
    void Stop();

    bool Running() const;

    void SetInterval(std::chrono::microseconds);

private:
    std::function<void()> m_callback;

    std::mutex m_op_mutex;

    std::thread             m_timer_thread;
    std::mutex              m_cond_mutex;
    std::condition_variable m_condition;

    std::atomic<std::chrono::microseconds> m_interval;
    std::atomic<bool>                      m_running;
};

} // namespace owe
