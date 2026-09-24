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
// 上游的线程随机数。离线作业不用它（见 Services::random），只剩不在离线作业里的调用方：
// 单测和待 R5 删除的实时路径。
using Random = effolkronium::random_thread_local;

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

// 一个离线作业的执行服务：随机数、时钟、诊断与依赖追踪。作业所有者（离线运行时）持有，
// 显式传给场景解析、脚本、粒子、Puppet、文本与纹理缓存。拿到空指针的调用方就是不在离线作业里。
struct Services {
    // 作业自己的引擎，所有随机数消费者共用；离线渲染的随机序列只由作业种子和消费顺序决定。
    effolkronium::random_local random;
    // 作业种子，以及每个对象自己的引擎（见 ObjectRandomScope）。
    uint64_t seed { 0 };
    std::unordered_map<std::int32_t, effolkronium::random_local> object_random;
    double epoch_ms { 946684800000.0 }; // 2000-01-01 UTC
    double elapsed { 0.0 };
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

// 区间随机数：离线作业用作业引擎，不在作业里用上游线程引擎。两边是同一个库的同一套分布实现，
// 所以离线作业的序列与原先"作用域把作业引擎换进线程引擎"时逐个相同。
template<typename T>
T RandomRange(Services* services, T from, T to) {
    return services != nullptr ? services->random.get(from, to) : Random::get(from, to);
}

// 作用域内把对象自己的引擎换进作业引擎的位置。它的种子只由作业种子和对象 id 决定，
// 所以增删、重排别的对象不会改变这个对象取到的随机数。不在离线作业里或 id 无效时什么都不做。
struct ObjectRandomScope {
    Services* services { nullptr };
    effolkronium::random_local* own { nullptr };
    ObjectRandomScope(Services* s, std::int32_t id) : services(s) {
        if (services == nullptr || id < 0) return;
        auto [it, fresh] = services->object_random.try_emplace(id);
        if (fresh) {
            std::seed_seq seq { uint32_t(services->seed), uint32_t(services->seed >> 32), uint32_t(id) };
            it->second.seed(seq);
        }
        own = &it->second;
        std::swap(services->random, *own);
    }
    ObjectRandomScope(const ObjectRandomScope&) = delete;
    ~ObjectRandomScope() { if (own != nullptr) std::swap(services->random, *own); }
};

} // namespace owe
