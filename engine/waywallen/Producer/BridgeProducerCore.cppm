module;

export module waywallen.bridge_producer_core;

import rstd.cppstd;
import vvk;
export import waywallen.bridge;
export import waywallen.bridge_session;

export namespace ww_wescene
{

// Snapshot fired by `setOnReadyChanged` whenever applyDirective resolves
// a new slot pool. Mirrors `owe::ExSwapchainReadyEvent` shape but
// kept Vulkan-native and free of any wescene types so non-wescene
// producers can consume it.
struct BridgeReadyEvent {
    bool     ready;
    uint32_t width;
    uint32_t height;
    VkFormat format;
};

enum class BridgeSlotAcquireStatus
{
    ReadyUnused,
    ReadyReleased,
    Busy,
    NotReady,
    SessionLost,
    Error,
};

struct BridgeSlotIdentity {
    uint64_t bind_generation { 0 };
    uint32_t slot_index { 0 };
    uint64_t previous_release_point { 0 };
    uint64_t acquire_serial { 0 };

    bool valid() const noexcept { return bind_generation != 0 && acquire_serial != 0; }
    bool operator==(const BridgeSlotIdentity&) const = default;
};

struct BridgeSlotAcquireResult {
    BridgeSlotAcquireStatus status { BridgeSlotAcquireStatus::NotReady };
    BridgeSlotIdentity      identity;
    VkImage                 image { VK_NULL_HANDLE };
    uint32_t                width { 0 };
    uint32_t                height { 0 };
    int32_t                 error_code { 0 };

    bool acquired() const noexcept {
        return (status == BridgeSlotAcquireStatus::ReadyUnused ||
                status == BridgeSlotAcquireStatus::ReadyReleased) &&
               identity.valid() && image != VK_NULL_HANDLE && width != 0 && height != 0;
    }
};

enum class BridgeSlotCompletionStatus
{
    Submitted,
    Aborted,
    NotPending,
    StaleIdentity,
    SessionLost,
    ProtocolError,
};

struct BridgeSlotCompletionResult {
    BridgeSlotCompletionStatus status { BridgeSlotCompletionStatus::NotPending };
    BridgeSlotIdentity         identity;
    int32_t                    error_code { 0 };

    bool completed() const noexcept {
        return status == BridgeSlotCompletionStatus::Submitted ||
               status == BridgeSlotCompletionStatus::Aborted;
    }
};

class BridgeProducerCore {
public:
    static constexpr uint32_t kMaxSlots = 8; // matches bridge cap

    explicit BridgeProducerCore(std::shared_ptr<BridgeSession> session);
    ~BridgeProducerCore();

    BridgeProducerCore(const BridgeProducerCore&)            = delete;
    BridgeProducerCore& operator=(const BridgeProducerCore&) = delete;

    // Stash a directive received from the daemon. Safe to call from any
    // thread — only the producer-thread `drainPendingDirective()` ever
    // reads the stash. If a directive was already pending, it is
    // dropped (the newer directive supersedes it; the daemon's older
    // negotiation is implicitly abandoned).
    void queueDirective(const ww_pool_directive_t& directive);

    // Thread-safe level-triggered request. The producer thread either
    // republishes the latest content or lets its next submit satisfy it.
    bool requestFrame();

    bool hasPendingFrameRequest() const {
        return m_frame_request_revision.load(std::memory_order_acquire) !=
               m_served_frame_request_revision.load(std::memory_order_acquire);
    }

    // Producer-thread-only. True means the caller should skip producing
    // another frame for the request that was just satisfied.
    bool republishRequestedFrame(bool wait_for_release = false);

    void cancelFrameWait() { m_cancel_frame_wait.store(true, std::memory_order_release); }

    // True iff a queued directive is waiting to be applied.
    bool hasPendingDirective() const { return m_pending_valid.load(std::memory_order_acquire); }

    // One-shot callback fired from the producer thread the first time a
    // directive is successfully applied. Setting after the first
    // negotiate is a no-op.
    void setOnFirstNegotiated(std::function<void()> cb) {
        std::lock_guard<std::mutex> lk(m_cb_mu);
        m_on_first_negotiated = std::move(cb);
    }

    // Fired from the producer thread inside drainPendingDirective after
    // every successful apply. Coexists with setOnFirstNegotiated; both
    // fire in undefined order.
    void setOnReadyChanged(std::function<void(const BridgeReadyEvent&)> cb) {
        std::lock_guard<std::mutex> lk(m_cb_mu);
        m_on_ready_changed = std::move(cb);
    }

    // Producer-thread-only. Drain any pending directive so `format()` /
    // `ready()` reflect the new state before the caller decides whether
    // to acquire a slot.
    void drainPendingDirective();

    // Producer-thread-only. Busy/error outcomes do not expose a writable
    // image and do not create a pending acquisition.
    BridgeSlotAcquireResult acquireSlot();

    // Compatibility façade for producers not yet consuming typed outcomes.
    bool acquireSlot(VkImage* out_image, uint32_t* out_width = nullptr,
                     uint32_t* out_height = nullptr);

    // Producer-thread-only. Forwards `producer_sync_fd` to
    // `ww_bridge_pool_submit_slot`. Bridge takes ownership of the fd
    // and closes it. Pass -1 only on shutdown.
    BridgeSlotCompletionResult submitSlot(const BridgeSlotIdentity& identity, int producer_sync_fd);
    BridgeSlotCompletionResult abortSlot(const BridgeSlotIdentity& identity);

    // Compatibility façade paired with the pointer-based acquire overload.
    void submitSlot(int producer_sync_fd);

    // Geometry / readiness accessors — readable from any thread, but
    // they can only update on the producer thread, so the caller is
    // responsible for any happens-before they need (typical pattern:
    // call from inside the producer thread right after
    // drainPendingDirective).
    bool ready() const {
        return ! m_session_lost && m_slot_count > 0 && m_export_format != VK_FORMAT_UNDEFINED;
    }
    uint32_t width() const { return m_width; }
    uint32_t height() const { return m_height; }
    VkFormat format() const { return m_export_format; }
    uint32_t fourcc() const { return m_fourcc; }

private:
    void       markSessionLost();
    static int cancelRepublishWait(void* userdata);

    // Producer-thread-only. Runs the bridge's apply_directive,
    // publishes new geometry/format on success. Returns:
    //   0   success — m_slot_count == directive.count
    //  <0   dry-run failure (bridge already emitted bind_failed); caller
    //       leaves m_slot_count = 0 and waits for the next directive
    //  >0   hard system error (logged); m_slot_count = 0
    int applyDirective(const ww_pool_directive_t& directive);

    std::shared_ptr<BridgeSession> m_session;

    VkFormat m_export_format { VK_FORMAT_UNDEFINED };

    std::atomic<bool>   m_pending_valid { false };
    std::mutex          m_pending_mu;
    ww_pool_directive_t m_pending_directive {};

    std::mutex                                   m_cb_mu;
    std::function<void()>                        m_on_first_negotiated;
    bool                                         m_first_negotiated_done { false };
    std::function<void(const BridgeReadyEvent&)> m_on_ready_changed;

    uint32_t              m_slot_count { 0 };
    bool                  m_have_pending { false };
    bool                  m_session_lost { false };
    BridgeSlotIdentity    m_pending_identity;
    uint32_t              m_width { 0 };
    uint32_t              m_height { 0 };
    uint32_t              m_fourcc { 0 };
    std::atomic<bool>     m_cancel_frame_wait { false };
    std::atomic<uint64_t> m_frame_request_revision { 0 };
    std::atomic<uint64_t> m_served_frame_request_revision { 0 };
};

} // namespace ww_wescene
