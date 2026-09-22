module;

#include "effolkronium/random.hpp"

export module wescene.core;
import rstd;
import rstd.cppstd;

using namespace rstd::prelude;

// NoCopyMove (global scope, matches the original NoCopyMove.hpp)
export struct NoCopy {
protected:
    NoCopy()  = default;
    ~NoCopy() = default;

    NoCopy(const NoCopy&)            = delete;
    NoCopy& operator=(const NoCopy&) = delete;
};

export struct NoMove {
protected:
    NoMove()  = default;
    ~NoMove() = default;

    NoMove(NoMove&&)            = delete;
    NoMove& operator=(NoMove&&) = delete;
};

export namespace owe
{

using rstd::i16;
using rstd::i32;
using rstd::i64;
using rstd::i8;
using rstd::isize;
using rstd::u16;
using rstd::u32;
using rstd::u64;
using rstd::u8;
using rstd::usize;

using idx = isize;

inline isize Ptr2Int(void* p) noexcept { return isize(reinterpret_cast<rstd::intptr_t>(p)); }

// StringHelper
constexpr bool sstart_with(std::string_view str, std::string_view start) {
    return str.size() >= start.size() && str.compare(0, start.size(), start, 0, start.size()) == 0;
}
constexpr bool send_with(std::string_view str, std::string_view end) {
    return str.size() >= end.size() &&
           str.compare(str.size() - end.size(), end.size(), end, 0, end.size()) == 0;
}
inline std::string_view sview_nullsafe(const char* const s) {
    return std::string_view(s != nullptr ? s : "");
}

// MapSet
template<class Key, class Value>
using Map = std::map<Key, Value, std::less<>>;

template<class Key>
using Set = std::set<Key, std::less<>>;

template<class Key, class Value, class KeyLike, class Allocator>
inline bool exists(const std::map<Key, Value, std::less<>, Allocator>& m,
                   const KeyLike&                                      key) noexcept {
    auto iter = m.find(key);
    return iter != m.end();
}

template<class Key, class KeyLike, class Allocator>
inline bool exists(const std::set<Key, std::less<>, Allocator>& m, const KeyLike& key) noexcept {
    auto iter = m.find(key);
    return iter != m.end();
}

// ArrayHelper
template<typename T, typename Tarray>
rstd::array<T, std::tuple_size<Tarray>::value> array_cast(const Tarray& array) noexcept {
    rstd::array<T, std::tuple_size<Tarray>::value> res;
    for (std::size_t index = 0; index < array.size(); ++index) {
        res[rstd::usize(index)] = rstd::as_cast<T>(array[index]);
    }
    return res;
}

template<typename S, typename TFunc, typename TR = std::invoke_result_t<TFunc, S>>
std::vector<TR> transform(std::span<const S> src, TFunc&& func) {
    std::vector<TR> dst(std::size(src));
    std::transform(std::begin(src), std::end(src), std::begin(dst), func);
    return dst;
}

template<typename T>
class spanone {
public:
    using value_type = T;
    using size_type  = usize;
    using reference  = T&;
    using pointer    = T*;

    constexpr spanone(reference value) noexcept: ptr { &value } {}
    constexpr pointer   data() const noexcept { return ptr; }
    constexpr size_type size() const noexcept { return usize(1); }
    constexpr reference operator[](usize index) const noexcept { return ptr[index]; }
    constexpr pointer   begin() const noexcept { return ptr; }
    constexpr pointer   end() const noexcept { return ptr + 1; }
    constexpr pointer   cbegin() const noexcept { return ptr; }
    constexpr pointer   cend() const noexcept { return ptr + 1; }

private:
    pointer ptr;
};

// Visitors
namespace visitor
{

template<class... Ts>
struct overload : Ts... {
    using Ts::operator()...;
};
template<class... Ts>
overload(Ts...) -> overload<Ts...>;

struct EqualVisitor {
    using result_type = bool;

    template<typename T, typename U>
    bool operator()(const T&, const U&) const {
        return false;
    }

    template<typename T>
    bool operator()(const T& v1, const T& v2) const {
        return v1 == v2;
    }
};

} // namespace visitor

// Random
using Random = effolkronium::random_thread_local;

// An offline job owns its RNG and clock. The scope restores the caller's RNG
// so two jobs stepped alternately on one thread do not affect one another.
struct OfflineDependency {
    std::int32_t owner { -1 }, target { -1 };
    std::string operation, property, binding;
    bool initialization { false };
};

// Stable trace contract consumed by the hybrid planner. owner_layer_id uses
// the same authored identity as OfflineDependency::owner.
struct OfflineSourceScriptError {
    std::uint64_t binding_id {};
    std::int32_t  owner_layer_id { -1 };
    std::string   owner_name;
    std::string   property;
    std::string   phase;
    std::string   script_sha;
    std::string   message;
    std::string   stack;
};

struct OfflineExecutionContext {
    Random::engine_type random { 0 };
    double epoch_ms { 946684800000.0 }; // 2000-01-01 UTC
    double elapsed { 0.0 };
    double delta { 0.0 };
    bool failed { false };
    bool trace_scene { false };
    uint64_t runtime_ik_chain_solves { 0 };
    std::vector<OfflineDependency> dependencies;
    std::unordered_set<std::string> dependency_keys;
    std::vector<OfflineSourceScriptError> source_script_errors;
    std::vector<std::string> diagnostics;

    void diagnose(std::string message, bool fatal = false) {
        failed = failed || fatal;
        if (std::find(diagnostics.begin(), diagnostics.end(), message) == diagnostics.end())
            diagnostics.push_back(std::move(message));
    }
    void trace(OfflineDependency value) {
        if (!trace_scene) return;
        if (dependencies.size() >= 10000) { diagnose("Runtime dependency trace reached its 10000-entry limit"); return; }
        std::string key = std::to_string(value.owner) + ':' + std::to_string(value.target) + ':' +
            value.operation + ':' + value.property + ':' + value.binding + ':' + (value.initialization ? '1' : '0');
        if (dependency_keys.insert(std::move(key)).second) dependencies.push_back(std::move(value));
    }
};

inline thread_local OfflineExecutionContext* active_offline_execution = nullptr;

class OfflineExecutionScope : NoCopy, NoMove {
public:
    explicit OfflineExecutionScope(OfflineExecutionContext& context)
        : m_context(context), m_previous(active_offline_execution), m_random(Random::get_engine()) {
        active_offline_execution = &context;
        Random::engine() = context.random;
    }
    ~OfflineExecutionScope() {
        m_context.random = Random::get_engine();
        Random::engine() = m_random;
        active_offline_execution = m_previous;
    }
private:
    OfflineExecutionContext& m_context;
    OfflineExecutionContext* m_previous;
    Random::engine_type m_random;
};

} // namespace owe
