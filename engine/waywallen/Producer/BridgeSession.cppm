export module waywallen.bridge_session;

import rstd.cppstd;
export import waywallen.bridge;

export namespace ww_wescene
{

class BridgeSession final {
public:
    static std::shared_ptr<BridgeSession> Adopt(ww_pool_t* pool, int control_socket);

    ~BridgeSession();

    BridgeSession(const BridgeSession&)            = delete;
    BridgeSession& operator=(const BridgeSession&) = delete;

    bool valid() const noexcept { return m_pool != nullptr && m_send_socket >= 0; }

    int advertiseCaps(uint32_t width, uint32_t height, uint32_t mem_hints);
    int applyDirective(const ww_pool_directive_t& directive);
    int getExtent(uint32_t& out_width, uint32_t& out_height);
    int tryAcquireAnyForRender(ww_pool_slot_acquire_result_t& out_result);
    int submitAcquiredSlot(const ww_pool_slot_identity_t& identity, int producer_sync_fd,
                           ww_pool_slot_submit_result_t& out_result);
    int tryRepublishLatest(ww_pool_republish_result_t& out_result);
    int waitRepublishLatest(ww_pool_cancel_fn cancel, void* userdata,
                            ww_pool_republish_result_t& out_result);
    int abortAcquiredSlot(const ww_pool_slot_identity_t& identity);
    int sendBindFailed(const waywallen_bind_failure_t& failure);
    int sendClearColor(float r, float g, float b, float a);
    int setEventSubscriptions(uint64_t revision, const std::vector<std::string>& kinds);

private:
    BridgeSession(ww_pool_t* pool, int send_socket): m_pool(pool), m_send_socket(send_socket) {}

    ww_pool_t* m_pool { nullptr };
    int        m_send_socket { -1 };
    std::mutex m_send_mutex;
};

class BridgeSubscriptionController final {
public:
    explicit BridgeSubscriptionController(std::shared_ptr<BridgeSession> session)
        : m_session(std::move(session)) {}

    bool replace(std::vector<std::string> kinds);
    bool set(std::string_view kind, bool enabled);
    void applied(const waywallen_event_subscription_result_t& event);
    bool acceptsAudio(uint64_t revision) const;

private:
    bool sendLocked();

    std::shared_ptr<BridgeSession> m_session;
    mutable std::mutex             m_mutex;
    std::vector<std::string>       m_desired;
    uint64_t                       m_next_revision { 1 };
    uint64_t                       m_sent_revision { 0 };
    uint64_t                       m_applied_revision { 0 };
    std::vector<std::string>       m_applied;
    bool                           m_dirty { false };
};

} // namespace ww_wescene
