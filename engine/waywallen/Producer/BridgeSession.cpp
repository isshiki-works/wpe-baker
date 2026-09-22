module;

#include <cerrno>
#include <algorithm>
#include <fcntl.h>
#include <unistd.h>

module waywallen.bridge_session;

import rstd.cppstd;
import waywallen.bridge;

namespace ww_wescene
{

std::shared_ptr<BridgeSession> BridgeSession::Adopt(ww_pool_t* pool, int control_socket) {
    if (pool == nullptr || control_socket < 0) return {};
    int send_socket = ::fcntl(control_socket, F_DUPFD_CLOEXEC, 0);
    if (send_socket < 0) return {};
    return std::shared_ptr<BridgeSession>(new BridgeSession(pool, send_socket));
}

BridgeSession::~BridgeSession() {
    if (m_pool != nullptr) ww_bridge_pool_destroy(m_pool);
    if (m_send_socket >= 0) ::close(m_send_socket);
}

int BridgeSession::advertiseCaps(uint32_t width, uint32_t height, uint32_t mem_hints) {
    std::scoped_lock lock(m_send_mutex);
    return ww_bridge_pool_advertise_caps(m_pool, m_send_socket, width, height, mem_hints);
}

int BridgeSession::applyDirective(const ww_pool_directive_t& directive) {
    std::scoped_lock lock(m_send_mutex);
    return ww_bridge_pool_apply_directive(m_pool, m_send_socket, &directive);
}

int BridgeSession::getExtent(uint32_t& out_width, uint32_t& out_height) {
    return ww_bridge_pool_get_extent(m_pool, &out_width, &out_height);
}

int BridgeSession::tryAcquireAnyForRender(ww_pool_slot_acquire_result_t& out_result) {
    return ww_bridge_pool_try_acquire_any_for_render(m_pool, &out_result);
}

int BridgeSession::submitAcquiredSlot(const ww_pool_slot_identity_t& identity, int producer_sync_fd,
                                      ww_pool_slot_submit_result_t& out_result) {
    std::scoped_lock lock(m_send_mutex);
    return ww_bridge_pool_submit_acquired_slot(
        m_pool, m_send_socket, &identity, producer_sync_fd, &out_result);
}

int BridgeSession::tryRepublishLatest(ww_pool_republish_result_t& out_result) {
    std::scoped_lock lock(m_send_mutex);
    return ww_bridge_pool_try_republish_latest(m_pool, m_send_socket, &out_result);
}

int BridgeSession::waitRepublishLatest(ww_pool_cancel_fn cancel, void* userdata,
                                       ww_pool_republish_result_t& out_result) {
    std::scoped_lock lock(m_send_mutex);
    return ww_bridge_pool_wait_republish_latest(
        m_pool, m_send_socket, cancel, userdata, &out_result);
}

int BridgeSession::abortAcquiredSlot(const ww_pool_slot_identity_t& identity) {
    return ww_bridge_pool_abort_acquired_slot(m_pool, &identity);
}

int BridgeSession::sendBindFailed(const waywallen_bind_failure_t& failure) {
    std::scoped_lock lock(m_send_mutex);
    return ww_bridge_send_bind_failed(m_send_socket, &failure);
}

int BridgeSession::sendClearColor(float r, float g, float b, float a) {
    std::scoped_lock lock(m_send_mutex);
    return ww_bridge_send_report_state_clear_color(m_send_socket, r, g, b, a);
}

int BridgeSession::setEventSubscriptions(uint64_t revision, const std::vector<std::string>& kinds) {
    std::vector<char*> raw;
    raw.reserve(kinds.size());
    for (const auto& kind : kinds) raw.push_back(const_cast<char*>(kind.c_str()));
    const waywallen_event_subscription_t subscription {
        .revision = revision,
        .kinds = {
            .count = static_cast<uint32_t>(raw.size()),
            .data  = raw.empty() ? nullptr : raw.data(),
        },
    };
    std::scoped_lock lock(m_send_mutex);
    return ww_bridge_set_event_subscriptions(m_send_socket, &subscription);
}

bool BridgeSubscriptionController::replace(std::vector<std::string> kinds) {
    std::sort(kinds.begin(), kinds.end(), [](const auto& left, const auto& right) {
        return left.compare(right) < 0;
    });
    kinds.erase(std::unique(kinds.begin(), kinds.end()), kinds.end());
    std::scoped_lock lock(m_mutex);
    if (m_desired != kinds) {
        m_desired = std::move(kinds);
        m_dirty   = true;
    }
    if (! m_dirty) return true;
    return sendLocked();
}

bool BridgeSubscriptionController::set(std::string_view kind, bool enabled) {
    std::scoped_lock lock(m_mutex);
    auto             found = std::lower_bound(
        m_desired.begin(), m_desired.end(), kind, [](const auto& left, std::string_view right) {
            return left.compare(right) < 0;
        });
    const bool present = found != m_desired.end() && *found == kind;
    if (enabled == present) return ! m_dirty || sendLocked();
    if (enabled)
        m_desired.insert(found, std::string(kind));
    else
        m_desired.erase(found);
    m_dirty = true;
    return sendLocked();
}

bool BridgeSubscriptionController::sendLocked() {
    const uint64_t revision = m_next_revision++;
    if (m_session->setEventSubscriptions(revision, m_desired) != 0) return false;
    m_sent_revision = revision;
    m_dirty         = false;
    return true;
}

void BridgeSubscriptionController::applied(const waywallen_event_subscription_result_t& event) {
    std::scoped_lock lock(m_mutex);
    if (event.status != WAYWALLEN_EVENT_SUBSCRIPTION_STATUS_APPLIED ||
        event.revision < m_applied_revision || event.revision > m_sent_revision)
        return;
    m_applied_revision = event.revision;
    m_applied.clear();
    m_applied.reserve(event.kinds.count);
    for (uint32_t index = 0; index < event.kinds.count; ++index) {
        if (event.kinds.data[index]) m_applied.emplace_back(event.kinds.data[index]);
    }
    std::sort(m_applied.begin(), m_applied.end(), [](const auto& left, const auto& right) {
        return left.compare(right) < 0;
    });
}

bool BridgeSubscriptionController::acceptsAudio(uint64_t revision) const {
    std::scoped_lock lock(m_mutex);
    return revision == m_applied_revision &&
           std::any_of(m_applied.begin(), m_applied.end(), [](const auto& kind) {
               return kind == "audio";
           });
}

} // namespace ww_wescene
