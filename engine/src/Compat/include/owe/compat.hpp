#pragma once
// owe::compat：rstd 核心类型的标准 C++23 垫片（T2a）。
// 原则：只实现引擎（src/tests/tools）实际调用到的 rstd API，语义照抄 rstd：
//   - 整数是强类型（explicit 构造、同类型才能运算），+ - * 回绕（等同 rstd Release 构建），
//     / % 除零和 MIN/-1 仍 panic，移位量是 U64 且按位宽取模；
//   - Option::unwrap / Result::unwrap / 解引用空值 = 打印 "thread 'main' panicked at ..." 后 abort；
//   - as_cast 浮点→整数：NaN→0、越界饱和（rstd 用 long double，这里用 double，对 float/double 输入逐位等价）；
//   - cmp::max/min 是 a > b ? a : b / a < b ? a : b（NaN 与相等时的返回值与 std::max 不同）。
// 与 rstd 的已知差异（登记项）：
//   - HashMap/HashSet 用 std::unordered_map/set：没有每次运行随机种子，同一构建遍历顺序固定，但与 rstd 的顺序不同；
//   - cppstd::as_str 不再校验 UTF-8（rstd 对非法 UTF-8 返回 Err，调用点 .unwrap() 后 abort）；
//   - 内部断言（Option 解引用、Vec 下标）的 panic 位置行指向本头文件，而不是 rstd 源码。
// 本头文件不依赖 rstd；集成时由各模块单元的全局模块片段 #include，并启用下面的 rstd 名字别名。

#include <array>
#include <atomic>
#include <bit>
#include <charconv>
#include <cmath>
#include <compare>
#include <concepts>
#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <ctime>
#include <expected>
#include <format>
#include <functional>
#include <limits>
#include <map>
#include <memory>
#include <mutex>
#include <optional>
#include <ranges>
#include <set>
#include <source_location>
#include <span>
#include <string>
#include <string_view>
#include <tuple>
#include <type_traits>
#include <unordered_map>
#include <unordered_set>
#include <utility>
#include <vector>
#if defined(_WIN32)
#include <io.h>
// rstd 的 EnvLogger 直接写 stderr 句柄（不经 CRT 文本模式，换行是 LF）；只声明用到的两个 API，不引 windows.h。
struct _OVERLAPPED;
extern "C" __declspec(dllimport) void* __stdcall GetStdHandle(unsigned long);
extern "C" __declspec(dllimport) int __stdcall WriteFile(void*, void const*, unsigned long, unsigned long*, _OVERLAPPED*);
#else
#include <unistd.h>
#endif

namespace owe::compat
{
// ── std 转出与原生整数别名（rstd::move/addressof/uint32_t 等本来就是 std 的别名）──
using std::addressof;
using std::forward;
using std::move;
using std::swap;
using std::exchange;
using std::int16_t;
using std::int32_t;
using std::int64_t;
using std::int8_t;
using std::intptr_t;
using std::ptrdiff_t;
using std::size_t;
using std::uint16_t;
using std::uint32_t;
using std::uint64_t;
using std::uint8_t;
using std::uintptr_t;
using byte = std::byte;

struct empty {
    friend constexpr bool operator==(empty, empty) noexcept { return true; }
};

// ── panic / assert / unreachable ──
namespace detail
{
[[noreturn]] inline void panic_at(std::source_location loc, std::string_view msg) {
    std::fprintf(stderr,
                 "thread 'main' panicked at %s:%u:%u:\n",
                 loc.file_name(),
                 static_cast<unsigned>(loc.line()),
                 static_cast<unsigned>(loc.column()));
    std::fwrite(msg.data(), 1, msg.size(), stderr);
    std::fputc('\n', stderr);
    std::fflush(stderr);
    std::abort();
}
} // namespace detail

namespace detail
{
// rstd 的格式化器忽略 "{:…}" 里的格式说明（实测 {:X}、{:04X}、{:#x} 都输出十进制），这里把每个占位符改写成 "{}"。
inline auto strip_specs(std::string_view f) -> std::string {
    std::string out;
    out.reserve(f.size());
    for (size_t i = 0; i < f.size(); ++i) {
        char const c = f[i];
        if ((c == '{' || c == '}') && i + 1 < f.size() && f[i + 1] == c) {
            out += c;
            out += c;
            ++i;
        } else if (c == '{') {
            out += "{}";
            i = f.find('}', i);
        } else {
            out += c;
        }
    }
    return out;
}
template<typename... Args>
auto rstd_format(std::format_string<Args...> fmt, Args&&... args) -> std::string {
    return std::vformat(strip_specs(fmt.get()), std::make_format_args(args...));
}
} // namespace detail

// rstd::panic 是可 CTAD 的结构体：rstd::panic("…{}", x) 或 rstd::panic { "…" }。
template<typename... Args>
struct panic {
    [[noreturn]] panic(std::format_string<Args...> fmt, Args&&... args,
                       std::source_location loc = std::source_location::current()) {
        detail::panic_at(loc, detail::rstd_format<Args...>(fmt, std::forward<Args>(args)...));
    }
};
template<typename... Args>
panic(std::format_string<Args...>, Args&&...) -> panic<Args...>;

[[noreturn]] inline void unreachable() { std::unreachable(); }

// ── Option ──
template<typename T>
class Option;

// 无类型的 None()，可隐式转成任意 Option<T>。
struct Unknown {
    template<typename T>
    constexpr operator Option<T>() const noexcept {
        return Option<T> {};
    }
};

template<typename T>
struct ref;
template<typename T>
struct mut_ref;
struct str;

namespace detail
{
template<typename T>
concept HasClone = requires(T const& v) {
    { v.clone() } -> std::convertible_to<T>;
};

template<typename T>
constexpr auto clone_value(T const& v) -> T {
    if constexpr (HasClone<T>) return v.clone();
    else return v;
}

template<typename T>
struct is_option : std::false_type {};
template<typename T>
struct is_option<Option<T>> : std::true_type {};

inline constexpr char UNWRAP_NONE[] = "called `Option::unwrap()` on a `None` value";

// Option 的共用方法；D 提供 is_some() 与 value_move()（按 unwrap 语义取出）。
template<typename D, typename V>
struct OptionOps {
    constexpr auto d() noexcept -> D& { return static_cast<D&>(*this); }
    constexpr auto d() const noexcept -> D const& { return static_cast<D const&>(*this); }

    constexpr auto is_none() const noexcept -> bool { return ! d().is_some(); }

    auto unwrap(std::source_location loc = std::source_location::current()) -> V {
        if (d().is_some()) return d().value_move();
        panic_at(loc, UNWRAP_NONE);
    }

    constexpr auto unwrap_unchecked() -> V {
        if (! d().is_some()) std::unreachable();
        return d().value_move();
    }

    template<typename U>
    auto unwrap_or(U&& default_value) -> V {
        if (d().is_some()) return d().value_move();
        return std::forward<U>(default_value);
    }

    constexpr auto take() -> D { return std::exchange(d(), D {}); }

    template<typename F, typename U = std::invoke_result_t<F, V>>
    constexpr auto map(F&& f) -> Option<U> {
        if (d().is_some()) return Option<U>(std::in_place, std::forward<F>(f)(d().value_move()));
        return Option<U> {};
    }
};
} // namespace detail

// Option<T>（T 不是引用）：派生自 std::optional<T>，与 std::optional 双向隐式转换；
// 解引用带检查（rstd 在所有构建里都检查），unwrap 失败 abort 而不是抛 bad_optional_access。
template<typename T>
class Option : public std::optional<T>, public detail::OptionOps<Option<T>, T> {
    using Base = std::optional<T>;

public:
    using value_type = T;
    using Base::Base;
    constexpr Option() noexcept = default;
    constexpr Option(Base const& o): Base(o) {}
    constexpr Option(Base&& o): Base(std::move(o)) {}

    constexpr auto is_some() const noexcept -> bool { return this->has_value(); }
    constexpr auto value_move() -> T { return std::move(**static_cast<Base*>(this)); }

    constexpr auto as_ref() const -> Option<T const&> {
        if (is_some()) return Option<T const&>(**this);
        return {};
    }

    template<typename U>
    constexpr auto insert(U&& value) -> T& {
        this->emplace(std::forward<U>(value));
        return **this;
    }

    auto clone() const -> Option {
        if (is_some()) return Option(std::in_place, detail::clone_value(**this));
        return {};
    }

    // Option<Result<T, E>> → Result<Option<T>, E>
    constexpr auto transpose()
        requires requires { typename T::error_type; };

    constexpr auto operator*() const& -> T const& {
        if (! is_some()) detail::panic_at(std::source_location::current(), "this->is_some()");
        return Base::operator*();
    }
    constexpr auto operator*() & -> T& {
        if (! is_some()) detail::panic_at(std::source_location::current(), "this->is_some()");
        return Base::operator*();
    }
    constexpr auto operator->() const -> T const* {
        if (! is_some()) detail::panic_at(std::source_location::current(), "this->is_some()");
        return Base::operator->();
    }
    constexpr auto operator->() -> T* {
        if (! is_some()) detail::panic_at(std::source_location::current(), "this->is_some()");
        return Base::operator->();
    }

    friend constexpr auto operator==(Option const& a, Option const& b) -> bool {
        if (a.is_some()) return b.is_some() && *static_cast<Base const&>(a) == *static_cast<Base const&>(b);
        return ! b.is_some();
    }
};

// Option<T&>：rstd 由 Some(左值) 产生，存指针，指针大小。
template<typename T>
class Option<T&> : public detail::OptionOps<Option<T&>, T&> {
    T* p_ = nullptr;

public:
    using value_type = T&;
    constexpr Option() noexcept = default;
    constexpr explicit Option(T& v) noexcept: p_(std::addressof(v)) {}
    constexpr Option(std::in_place_t, T& v) noexcept: p_(std::addressof(v)) {}

    constexpr auto is_some() const noexcept -> bool { return p_ != nullptr; }
    constexpr auto value_move() noexcept -> T& { return *p_; }
    constexpr explicit operator bool() const noexcept { return is_some(); }

    constexpr auto operator*() const -> T& {
        if (! is_some()) detail::panic_at(std::source_location::current(), "this->is_some()");
        return *p_;
    }
    constexpr auto operator->() const -> T* {
        if (! is_some()) detail::panic_at(std::source_location::current(), "this->is_some()");
        return p_;
    }
    auto clone() const -> Option { return *this; }
};

// Option<ref<T>> / Option<mut_ref<T>>：rstd 用零值利基存储，这里同样是一个指针大小。
template<typename R, typename P>
class OptionPtrRef : public detail::OptionOps<Option<R>, R> {
protected:
    P* p_ = nullptr;

public:
    using value_type = R;
    constexpr OptionPtrRef() noexcept = default;
    constexpr explicit OptionPtrRef(R r) noexcept: p_(r.p) {}
    constexpr OptionPtrRef(std::in_place_t, R r) noexcept: p_(r.p) {}

    constexpr auto is_some() const noexcept -> bool { return p_ != nullptr; }
    constexpr auto value_move() const noexcept -> R { return R::from_raw(p_); }
    constexpr explicit operator bool() const noexcept { return is_some(); }

    constexpr auto operator*() const -> R {
        if (! is_some()) detail::panic_at(std::source_location::current(), "this->is_some()");
        return R::from_raw(p_);
    }
    constexpr auto operator->() const -> P* {
        if (! is_some()) detail::panic_at(std::source_location::current(), "this->is_some()");
        return p_;
    }
    auto clone() const -> Option<R> { return static_cast<Option<R> const&>(*this); }
};

// 切片与 ref<str> 是胖指针，不走单指针利基存储，用主模板（std::optional）。
template<typename T>
    requires(! std::is_array_v<T> && ! std::same_as<T, str>)
class Option<ref<T>> : public OptionPtrRef<ref<T>, T const> {
public:
    using OptionPtrRef<ref<T>, T const>::OptionPtrRef;
};
template<typename T>
    requires(! std::is_array_v<T>)
class Option<mut_ref<T>> : public OptionPtrRef<mut_ref<T>, T> {
public:
    using OptionPtrRef<mut_ref<T>, T>::OptionPtrRef;
};

// Some(x)：可平凡复制的值按值存；否则左值得到 Option<T&>、右值得到 Option<T>（照抄 rstd）。
template<typename U = void, typename T>
constexpr auto Some(T&& val) {
    if constexpr (std::is_void_v<U>) {
        using V = std::remove_cvref_t<T>;
        if constexpr (std::is_trivially_copyable_v<V>) {
            return Option<V>(std::in_place, val);
        } else {
            return Option<T>(std::in_place, std::forward<T>(val));
        }
    } else {
        return Option<U>(std::in_place, std::forward<T>(val));
    }
}

template<typename U = void>
constexpr auto None() {
    if constexpr (std::is_void_v<U>) return Unknown {};
    else return Option<U> {};
}

// ── Result ──
template<typename T, typename E>
class Result;

template<typename T>
struct OkValue {
    T value;
    template<typename U, typename E>
    constexpr operator Result<U, E>() && {
        return Result<U, E>(std::in_place, std::move(value));
    }
};
template<typename E>
struct ErrValue {
    E error;
    template<typename U, typename E2>
    constexpr operator Result<U, E2>() && {
        return Result<U, E2>(std::unexpect, std::move(error));
    }
};

template<typename T = void, typename V>
constexpr auto Ok(V&& v) {
    if constexpr (std::is_void_v<T>) return OkValue<std::remove_cvref_t<V>> { std::forward<V>(v) };
    else return OkValue<T> { T(std::forward<V>(v)) };
}
template<typename E = void, typename V>
constexpr auto Err(V&& v) {
    if constexpr (std::is_void_v<E>) return ErrValue<std::remove_cvref_t<V>> { std::forward<V>(v) };
    else return ErrValue<E> { E(std::forward<V>(v)) };
}

// Result<T, E>：派生自 std::expected<T, E>；unwrap 系列失败 abort。
template<typename T, typename E>
class Result : public std::expected<T, E> {
    using Base = std::expected<T, E>;

public:
    using value_type = T;
    using error_type = E;
    using Base::Base;
    constexpr Result() = default;
    constexpr Result(Base const& b): Base(b) {}
    constexpr Result(Base&& b): Base(std::move(b)) {}

    constexpr auto is_ok() const noexcept -> bool { return this->has_value(); }
    constexpr auto is_err() const noexcept -> bool { return ! this->has_value(); }

    auto unwrap(std::source_location loc = std::source_location::current()) -> T {
        if (is_ok()) return std::move(**static_cast<Base*>(this));
        detail::panic_at(loc, "called `Result::unwrap()` on an `Err` value");
    }
    auto unwrap_err(std::source_location loc = std::source_location::current()) -> E {
        if (is_err()) return std::move(this->error());
        detail::panic_at(loc, "called `Result::unwrap_err()` on an `Ok` value");
    }
    auto unwrap_unchecked() -> T {
        if (! is_ok()) std::unreachable();
        return std::move(**static_cast<Base*>(this));
    }
    auto unwrap_err_unchecked() -> E {
        if (! is_err()) std::unreachable();
        return std::move(this->error());
    }
    auto unwrap_or(T&& def) -> T {
        if (is_ok()) return std::move(**static_cast<Base*>(this));
        return std::move(def);
    }
    auto ok() -> Option<T> {
        if (is_ok()) return Option<T>(std::in_place, std::move(**static_cast<Base*>(this)));
        return {};
    }
    template<typename F, typename U = std::invoke_result_t<F, T>>
    auto map(F&& op) -> Result<U, E> {
        if (is_ok()) return Result<U, E>(std::in_place, std::forward<F>(op)(std::move(**static_cast<Base*>(this))));
        return Result<U, E>(std::unexpect, std::move(this->error()));
    }

    constexpr auto operator->() const -> T const* {
        if (! is_ok()) detail::panic_at(std::source_location::current(), "is_ok()");
        return Base::operator->();
    }
    constexpr auto operator->() -> T* {
        if (! is_ok()) detail::panic_at(std::source_location::current(), "is_ok()");
        return Base::operator->();
    }
};

template<typename T>
constexpr auto Option<T>::transpose()
    requires requires { typename T::error_type; }
{
    using U = typename T::value_type;
    using E = typename T::error_type;
    if (! is_some()) return Result<Option<U>, E>(std::in_place);
    auto& r = **this;
    if (r.is_ok()) return Result<Option<U>, E>(std::in_place, Option<U>(std::in_place, r.unwrap_unchecked()));
    return Result<Option<U>, E>(std::unexpect, r.unwrap_err_unchecked());
}

// ── rstd_try 支持 ──
namespace detail
{
template<typename R>
constexpr auto try_ok(R const& r) -> bool {
    if constexpr (is_option<std::remove_cvref_t<R>>::value) return r.is_some();
    else return r.is_ok();
}
template<typename R>
constexpr decltype(auto) try_value(R&& r) {
    return std::forward<R>(r).unwrap_unchecked();
}
template<typename R>
constexpr auto try_residual(R&& r) {
    if constexpr (is_option<std::remove_cvref_t<R>>::value) return Unknown {};
    else return ErrValue<typename std::remove_cvref_t<R>::error_type> { std::forward<R>(r).unwrap_err_unchecked() };
}
} // namespace detail

// ── 整数强类型 ──
struct U64;
struct U32;

namespace detail
{
template<typename P>
using wide_unsigned_t =
    std::conditional_t<(sizeof(P) < sizeof(unsigned)), unsigned, std::make_unsigned_t<P>>;

template<typename P>
constexpr auto wrap_add(P a, P b) noexcept -> P {
    using W = wide_unsigned_t<P>;
    return static_cast<P>(static_cast<W>(static_cast<std::make_unsigned_t<P>>(a)) +
                          static_cast<W>(static_cast<std::make_unsigned_t<P>>(b)));
}
template<typename P>
constexpr auto wrap_sub(P a, P b) noexcept -> P {
    using W = wide_unsigned_t<P>;
    return static_cast<P>(static_cast<W>(static_cast<std::make_unsigned_t<P>>(a)) -
                          static_cast<W>(static_cast<std::make_unsigned_t<P>>(b)));
}
template<typename P>
constexpr auto wrap_mul(P a, P b) noexcept -> P {
    using W = wide_unsigned_t<P>;
    return static_cast<P>(static_cast<W>(static_cast<std::make_unsigned_t<P>>(a)) *
                          static_cast<W>(static_cast<std::make_unsigned_t<P>>(b)));
}
} // namespace detail

template<typename Derived, typename P>
class Integer {
    P value_ {};

public:
    using primitive_type               = P;
    using Self                         = Derived;
    static constexpr bool     IS_SIGNED = std::is_signed_v<P>;
    static constexpr uint32_t BIT_WIDTH = sizeof(P) * 8;

protected:
    constexpr Integer() noexcept = default;
    explicit constexpr Integer(P v) noexcept: value_(v) {}

public:
    [[nodiscard]] constexpr auto to_primitive() const noexcept -> P { return value_; }
    [[nodiscard]] constexpr auto as_ptr() const& noexcept -> P const* { return &value_; }
    [[nodiscard]] constexpr auto as_mut_ptr() & noexcept -> P* { return &value_; }
    auto as_ptr() const&& -> P const* = delete; // 与 rstd 相同：不许取临时量的地址
    auto as_mut_ptr() && -> P*        = delete;

    [[nodiscard]] constexpr auto min(Self rhs) const noexcept -> Self {
        return value_ <= rhs.value_ ? Self(value_) : rhs;
    }
    [[nodiscard]] constexpr auto max(Self rhs) const noexcept -> Self {
        return value_ >= rhs.value_ ? Self(value_) : rhs;
    }
    [[nodiscard]] constexpr auto checked_sub(Self rhs) const noexcept -> Option<Self> {
        P out {};
        if (__builtin_sub_overflow(value_, rhs.value_, &out)) return {};
        return Option<Self>(std::in_place, Self(out));
    }
    [[nodiscard]] constexpr auto checked_mul(Self rhs) const noexcept -> Option<Self> {
        P out {};
        if (__builtin_mul_overflow(value_, rhs.value_, &out)) return {};
        return Option<Self>(std::in_place, Self(out));
    }
    [[nodiscard]] constexpr auto swap_bytes() const noexcept -> Self {
        return Self(static_cast<P>(std::byteswap(static_cast<std::make_unsigned_t<P>>(value_))));
    }

    friend constexpr auto operator==(Self a, Self b) noexcept -> bool { return a.value_ == b.value_; }
    friend constexpr auto operator<=>(Self a, Self b) noexcept -> std::strong_ordering {
        return a.value_ <=> b.value_;
    }
    friend constexpr auto operator+(Self a, Self b) noexcept -> Self { return Self(detail::wrap_add(a.value_, b.value_)); }
    friend constexpr auto operator-(Self a, Self b) noexcept -> Self { return Self(detail::wrap_sub(a.value_, b.value_)); }
    friend constexpr auto operator*(Self a, Self b) noexcept -> Self { return Self(detail::wrap_mul(a.value_, b.value_)); }
    friend constexpr auto operator/(Self a, Self b) -> Self {
        if (b.value_ == 0) detail::panic_at(std::source_location::current(), "attempt to divide by zero");
        if constexpr (IS_SIGNED) {
            if (a.value_ == std::numeric_limits<P>::min() && b.value_ == P(-1))
                detail::panic_at(std::source_location::current(), "attempt to perform integer arithmetic with overflow");
        }
        return Self(static_cast<P>(a.value_ / b.value_));
    }
    friend constexpr auto operator%(Self a, Self b) -> Self {
        if (b.value_ == 0) detail::panic_at(std::source_location::current(), "attempt to divide by zero");
        if constexpr (IS_SIGNED) {
            if (a.value_ == std::numeric_limits<P>::min() && b.value_ == P(-1))
                detail::panic_at(std::source_location::current(), "attempt to perform integer arithmetic with overflow");
        }
        return Self(static_cast<P>(a.value_ % b.value_));
    }
    friend constexpr auto operator-(Self v) noexcept -> Self { return Self(detail::wrap_sub(P(0), v.value_)); }
    friend constexpr auto operator~(Self v) noexcept -> Self { return Self(static_cast<P>(~v.value_)); }
    friend constexpr auto operator&(Self a, Self b) noexcept -> Self { return Self(static_cast<P>(a.value_ & b.value_)); }
    friend constexpr auto operator|(Self a, Self b) noexcept -> Self { return Self(static_cast<P>(a.value_ | b.value_)); }
    friend constexpr auto operator^(Self a, Self b) noexcept -> Self { return Self(static_cast<P>(a.value_ ^ b.value_)); }
    // 移位量必须是 U64（与 rstd 相同），按位宽取模（rstd Release 的 overflowing_shl/shr 语义）。
    template<std::same_as<U64> Shift>
    friend constexpr auto operator<<(Self a, Shift s) noexcept -> Self {
        auto n = static_cast<unsigned>(s.to_primitive() % BIT_WIDTH);
        return Self(static_cast<P>(static_cast<std::make_unsigned_t<P>>(a.value_) << n));
    }
    template<std::same_as<U64> Shift>
    friend constexpr auto operator>>(Self a, Shift s) noexcept -> Self {
        auto n = static_cast<unsigned>(s.to_primitive() % BIT_WIDTH);
        return Self(static_cast<P>(a.value_ >> n));
    }
    constexpr auto operator++() & noexcept -> Self& { return self_() = self_() + Self(P(1)); }
    constexpr auto operator++(int) & noexcept -> Self { auto old = self_(); ++*this; return old; }
    constexpr auto operator--() & noexcept -> Self& { return self_() = self_() - Self(P(1)); }
    constexpr auto operator--(int) & noexcept -> Self { auto old = self_(); --*this; return old; }
    constexpr auto operator+=(Self r) noexcept -> Self& { return self_() = self_() + r; }
    constexpr auto operator-=(Self r) noexcept -> Self& { return self_() = self_() - r; }
    constexpr auto operator*=(Self r) noexcept -> Self& { return self_() = self_() * r; }
    constexpr auto operator/=(Self r) -> Self& { return self_() = self_() / r; }
    constexpr auto operator%=(Self r) -> Self& { return self_() = self_() % r; }
    constexpr auto operator&=(Self r) noexcept -> Self& { return self_() = self_() & r; }
    constexpr auto operator|=(Self r) noexcept -> Self& { return self_() = self_() | r; }
    constexpr auto operator^=(Self r) noexcept -> Self& { return self_() = self_() ^ r; }
    template<std::same_as<U64> Shift>
    constexpr auto operator<<=(Shift s) noexcept -> Self& { return self_() = self_() << s; }
    template<std::same_as<U64> Shift>
    constexpr auto operator>>=(Shift s) noexcept -> Self& { return self_() = self_() >> s; }

private:
    constexpr auto self_() noexcept -> Self& { return static_cast<Self&>(*this); }
};

#define OWE_COMPAT_INTEGER(NAME, P)                                                     \
    struct NAME final : Integer<NAME, P> {                                              \
        constexpr NAME() noexcept = default;                                            \
        explicit constexpr NAME(P v) noexcept: Integer(v) {}                            \
        template<typename S>                                                            \
            requires std::is_integral_v<S> && (! std::is_same_v<std::remove_cv_t<S>, bool>) \
        explicit constexpr NAME(S v) noexcept: Integer(static_cast<P>(v)) {}            \
        static const NAME MIN;                                                          \
        static const NAME MAX;                                                          \
    };
OWE_COMPAT_INTEGER(U8, uint8_t)
OWE_COMPAT_INTEGER(U16, uint16_t)
OWE_COMPAT_INTEGER(U32, uint32_t)
OWE_COMPAT_INTEGER(U64, uint64_t)
OWE_COMPAT_INTEGER(Usize, size_t)
OWE_COMPAT_INTEGER(I8, int8_t)
OWE_COMPAT_INTEGER(I16, int16_t)
OWE_COMPAT_INTEGER(I32, int32_t)
OWE_COMPAT_INTEGER(I64, int64_t)
OWE_COMPAT_INTEGER(Isize, ptrdiff_t)
#undef OWE_COMPAT_INTEGER

#define OWE_COMPAT_INTEGER_CONSTS(NAME, P)                                     \
    inline constexpr NAME NAME::MIN { std::numeric_limits<P>::min() };         \
    inline constexpr NAME NAME::MAX { std::numeric_limits<P>::max() };
OWE_COMPAT_INTEGER_CONSTS(U8, uint8_t)
OWE_COMPAT_INTEGER_CONSTS(U16, uint16_t)
OWE_COMPAT_INTEGER_CONSTS(U32, uint32_t)
OWE_COMPAT_INTEGER_CONSTS(U64, uint64_t)
OWE_COMPAT_INTEGER_CONSTS(Usize, size_t)
OWE_COMPAT_INTEGER_CONSTS(I8, int8_t)
OWE_COMPAT_INTEGER_CONSTS(I16, int16_t)
OWE_COMPAT_INTEGER_CONSTS(I32, int32_t)
OWE_COMPAT_INTEGER_CONSTS(I64, int64_t)
OWE_COMPAT_INTEGER_CONSTS(Isize, ptrdiff_t)
#undef OWE_COMPAT_INTEGER_CONSTS

// ── 浮点强类型（方法照抄 rstd 用的 builtin：round/floor/sqrt 走 double，sin/cos/atan2 按宽度选 f 版本）──
template<typename Derived, typename P>
class Floating {
    P value_ {};

public:
    using primitive_type = P;
    using Self           = Derived;

protected:
    constexpr Floating() noexcept = default;
    explicit constexpr Floating(P v) noexcept: value_(v) {}

public:
    [[nodiscard]] constexpr auto to_primitive() const noexcept -> P { return value_; }
    constexpr auto is_finite() const noexcept -> bool { return std::isfinite(value_); }
    constexpr auto abs() const noexcept -> Self { return Self(static_cast<P>(std::fabs(static_cast<double>(value_)))); }
    constexpr auto min(Self r) const noexcept -> Self { return Self(static_cast<P>(std::fmin(double(value_), double(r.value_)))); }
    constexpr auto max(Self r) const noexcept -> Self { return Self(static_cast<P>(std::fmax(double(value_), double(r.value_)))); }
    auto clamp(Self lo, Self hi) const -> Self {
        if (std::isnan(lo.value_) || std::isnan(hi.value_) || lo.value_ > hi.value_)
            detail::panic_at(std::source_location::current(), "min > max, or either was NaN");
        return value_ < lo.value_ ? lo : value_ > hi.value_ ? hi : Self(value_);
    }
    auto floor() const noexcept -> Self { return Self(static_cast<P>(std::floor(double(value_)))); }
    auto round() const noexcept -> Self { return Self(static_cast<P>(std::round(double(value_)))); }
    auto sqrt() const noexcept -> Self { return Self(static_cast<P>(std::sqrt(double(value_)))); }
    auto sin() const noexcept -> Self { return Self(std::sin(value_)); }
    auto cos() const noexcept -> Self { return Self(std::cos(value_)); }
    auto atan2(Self o) const noexcept -> Self { return Self(std::atan2(value_, o.value_)); }

    friend constexpr auto operator==(Self a, Self b) noexcept -> bool { return a.value_ == b.value_; }
    friend constexpr auto operator<=>(Self a, Self b) noexcept -> std::partial_ordering { return a.value_ <=> b.value_; }
    friend constexpr auto operator+(Self a, Self b) noexcept -> Self { return Self(a.value_ + b.value_); }
    friend constexpr auto operator-(Self a, Self b) noexcept -> Self { return Self(a.value_ - b.value_); }
    friend constexpr auto operator*(Self a, Self b) noexcept -> Self { return Self(a.value_ * b.value_); }
    friend constexpr auto operator/(Self a, Self b) noexcept -> Self { return Self(a.value_ / b.value_); }
    friend constexpr auto operator-(Self v) noexcept -> Self { return Self(-v.value_); }
    constexpr auto operator+=(Self r) noexcept -> Self& { return self_() = self_() + r; }
    constexpr auto operator-=(Self r) noexcept -> Self& { return self_() = self_() - r; }
    constexpr auto operator*=(Self r) noexcept -> Self& { return self_() = self_() * r; }
    constexpr auto operator/=(Self r) noexcept -> Self& { return self_() = self_() / r; }

private:
    constexpr auto self_() noexcept -> Self& { return static_cast<Self&>(*this); }
};

#define OWE_COMPAT_FLOAT(NAME, P)                                    \
    struct NAME final : Floating<NAME, P> {                          \
        constexpr NAME() noexcept = default;                         \
        explicit constexpr NAME(P v) noexcept: Floating(v) {}        \
        struct consts {                                              \
            static constexpr P PI_  = static_cast<P>(3.14159265358979323846264338327950288L); \
            static constexpr P TAU_ = static_cast<P>(6.28318530717958647692528676655900577L); \
            static const NAME  PI;                                   \
            static const NAME  TAU;                                  \
        };                                                           \
    };                                                               \
    inline constexpr NAME NAME::consts::PI { NAME::consts::PI_ };    \
    inline constexpr NAME NAME::consts::TAU { NAME::consts::TAU_ };
OWE_COMPAT_FLOAT(F32, float)
OWE_COMPAT_FLOAT(F64, double)
#undef OWE_COMPAT_FLOAT

using u8    = U8;
using u16   = U16;
using u32   = U32;
using u64   = U64;
using usize = Usize;
using i8    = I8;
using i16   = I16;
using i32   = I32;
using i64   = I64;
using isize = Isize;
using f32   = F32;
using f64   = F64;

namespace detail
{
template<typename T>
concept IntegerWrapper = requires { typename T::primitive_type; } &&
                         std::derived_from<T, Integer<T, typename T::primitive_type>>;
template<typename T>
concept FloatWrapper = requires { typename T::primitive_type; } &&
                       std::derived_from<T, Floating<T, typename T::primitive_type>>;

template<typename T>
constexpr auto prim(T v) noexcept {
    if constexpr (IntegerWrapper<T> || FloatWrapper<T>) return v.to_primitive();
    else if constexpr (std::is_enum_v<T>) return static_cast<std::underlying_type_t<T>>(v);
    else return v;
}
template<typename T>
using prim_t = decltype(prim(std::declval<T>()));

template<typename To, typename P>
constexpr auto wrap_as(P v) noexcept -> To {
    if constexpr (IntegerWrapper<To> || FloatWrapper<To>) return To(static_cast<typename To::primitive_type>(v));
    else return static_cast<To>(v);
}
} // namespace detail

// ── as_cast：Rust `as` 语义 ──
template<typename To, typename From>
constexpr auto as_cast(From&& from) noexcept -> To {
    using F  = std::remove_cvref_t<From>;
    using FP = detail::prim_t<F>;
    using TP = detail::prim_t<To>;
    FP const v = detail::prim(from);
    if constexpr (std::is_floating_point_v<TP>) {
        return detail::wrap_as<To>(static_cast<TP>(v));
    } else if constexpr (std::is_floating_point_v<FP>) {
        // 照抄 lossy_float_to_integer：NaN→0，越界饱和，其余向零截断。
        double const raw   = static_cast<double>(v);
        constexpr int digits = std::numeric_limits<TP>::digits;
        double upper = 1.0;
        for (int k = 0; k < digits; ++k) upper *= 2.0; // 2^digits，精确
        if (raw != raw) return detail::wrap_as<To>(TP(0));
        if constexpr (std::is_signed_v<TP>) {
            if (raw <= -upper) return detail::wrap_as<To>(std::numeric_limits<TP>::min());
            if (raw >= upper) return detail::wrap_as<To>(std::numeric_limits<TP>::max());
        } else {
            if (raw <= 0.0) return detail::wrap_as<To>(TP(0));
            if (raw >= upper) return detail::wrap_as<To>(std::numeric_limits<TP>::max());
        }
        return detail::wrap_as<To>(static_cast<TP>(raw)); // 区间内 static_cast 即向零截断
    } else {
        // 整数间：取模截断（C++20 起 static_cast 即两补码回绕）。
        return detail::wrap_as<To>(static_cast<TP>(v));
    }
}

// ── cmp：rstd 语义（不是 std::max/min）──
namespace cmp
{
template<typename T>
constexpr auto max(T v1, T v2) noexcept -> T { return v1 > v2 ? v1 : v2; }
template<typename T>
constexpr auto min(T v1, T v2) noexcept -> T { return v1 < v2 ? v1 : v2; }
} // namespace cmp

// ── 指针、引用与切片（rstd 的 ptr/mut_ptr、ref/mut_ref、ref<T[]>/mut_ref<T[]> 语义）──
template<typename T>
struct ptr;
template<typename T>
struct mut_ptr;

template<typename T>
struct ref {
    T const* p = nullptr;
    constexpr ref() noexcept = default;
    constexpr ref(T const& v) noexcept: p(std::addressof(v)) {}
    static constexpr auto from_raw(T const* raw) noexcept -> ref { ref r; r.p = raw; return r; }
    static constexpr auto from_raw_parts(T const* raw) noexcept -> ref { return from_raw(raw); }
    constexpr auto get() const noexcept -> T const& { return *p; }
    constexpr auto operator*() const noexcept -> T const& { return *p; }
    constexpr auto operator->() const noexcept -> T const* { return p; }
    constexpr auto as_raw_ptr() const noexcept -> T const* { return p; }
    constexpr auto as_ptr() const noexcept -> ptr<T>;
    constexpr auto deref() const noexcept -> T const& { return *p; }
    // rstd：ref 之间按地址比较，与元素比较时按值。
    friend constexpr auto operator==(ref a, ref b) noexcept -> bool { return a.p == b.p; }
    friend constexpr auto operator<=>(ref a, ref b) noexcept { return a.p <=> b.p; }
};
template<typename T>
struct mut_ref {
    T* p = nullptr;
    constexpr mut_ref() noexcept = default;
    constexpr mut_ref(T& v) noexcept: p(std::addressof(v)) {}
    static constexpr auto from_raw(T* raw) noexcept -> mut_ref { mut_ref r; r.p = raw; return r; }
    static constexpr auto from_raw_parts(T* raw) noexcept -> mut_ref { return from_raw(raw); }
    constexpr auto get() const noexcept -> T& { return *p; }
    constexpr auto get_mut() const noexcept -> T& { return *p; }
    constexpr auto operator*() const noexcept -> T& { return *p; }
    constexpr auto operator->() const noexcept -> T* { return p; }
    constexpr auto as_raw_ptr() const noexcept -> T* { return p; }
    constexpr auto as_ref() const noexcept -> ref<T> { return ref<T>::from_raw(p); }
    constexpr operator ref<T>() const noexcept { return ref<T>::from_raw(p); }
    constexpr auto as_ptr() const noexcept -> ptr<T>;
    constexpr auto as_mut_ptr() const noexcept -> mut_ptr<T>;
    constexpr auto deref() const noexcept -> T& { return *p; }
    constexpr auto deref_mut() const noexcept -> T& { return *p; }
    friend constexpr auto operator==(mut_ref a, mut_ref b) noexcept -> bool { return a.p == b.p; }
    friend constexpr auto operator<=>(mut_ref a, mut_ref b) noexcept { return a.p <=> b.p; }
};

// 原始指针包装：可隐式退化为裸指针，另带 rstd 的 add/get/as_ref/as_raw_ptr。
template<typename T>
struct ptr {
    T const* p = nullptr;
    static constexpr auto from_raw_parts(T const* raw) noexcept -> ptr { return ptr { raw }; }
    constexpr auto as_raw_ptr() const noexcept -> T const* { return p; }
    constexpr operator T const*() const noexcept { return p; }
    constexpr auto is_null() const noexcept -> bool { return p == nullptr; }
    constexpr auto add(usize n) const noexcept -> ptr;
    constexpr auto get() const noexcept -> T const& { return *p; }
    constexpr auto as_ref() const noexcept -> ref<T> { return ref<T>::from_raw(p); }
    template<typename U>
    auto cast() const noexcept -> ptr<U> { return ptr<U> { reinterpret_cast<U const*>(p) }; }
};
template<typename T>
struct mut_ptr {
    T* p = nullptr;
    static constexpr auto from_raw_parts(T* raw) noexcept -> mut_ptr { return mut_ptr { raw }; }
    constexpr auto as_raw_ptr() const noexcept -> T* { return p; }
    constexpr operator T*() const noexcept { return p; }
    constexpr operator ptr<T>() const noexcept { return ptr<T> { p }; }
    constexpr auto is_null() const noexcept -> bool { return p == nullptr; }
    constexpr auto add(usize n) const noexcept -> mut_ptr;
    constexpr auto get() const noexcept -> T& { return *p; }
    constexpr auto as_ref() const noexcept -> ref<T> { return ref<T>::from_raw(p); }
    constexpr auto as_mut_ref() const noexcept -> mut_ref<T> { return mut_ref<T>::from_raw(p); }
    template<typename U>
    auto cast() const noexcept -> mut_ptr<U> { return mut_ptr<U> { reinterpret_cast<U*>(p) }; }
};

// 切片：ref<T[]>（只读）/ mut_ref<T[]>（可写），下标不查越界（与 rstd 的 element_at 相同），== 逐元素比较。
template<typename T>
struct ref<T[]> {
    T const* p = nullptr;
    size_t   n = 0;
    using value_type = T;
    constexpr ref() noexcept = default;
    constexpr ref(std::span<T const> s) noexcept: p(s.data()), n(s.size()) {}
    template<size_t N>
    constexpr ref(T const (&a)[N]) noexcept: p(a), n(N) {}
    static constexpr auto from_raw_parts(T const* data, usize len) noexcept -> ref {
        ref r;
        r.p = data;
        r.n = len.to_primitive();
        return r;
    }
    constexpr auto len() const noexcept -> usize { return usize(n); }
    constexpr auto is_empty() const noexcept -> bool { return n == 0; }
    constexpr auto size() const noexcept -> size_t { return n; }
    constexpr auto empty() const noexcept -> bool { return n == 0; }
    constexpr auto data() const noexcept -> T const* { return p; }
    constexpr auto begin() const noexcept -> T const* { return p; }
    constexpr auto end() const noexcept -> T const* { return p + n; }
    constexpr auto operator[](usize i) const noexcept -> T const& { return p[i.to_primitive()]; }
    constexpr auto as_raw_ptr() const noexcept -> T const* { return p; }
    constexpr auto as_ptr() const noexcept -> ptr<T> { return ptr<T> { p }; }
    constexpr auto deref() const noexcept -> ref { return *this; }
    constexpr operator std::span<T const>() const noexcept { return { p, n }; }
    friend constexpr auto operator==(ref a, ref b) noexcept(noexcept(std::declval<T const&>() == std::declval<T const&>())) -> bool
        requires requires(T const& x) { x == x; }
    {
        if (a.n != b.n) return false;
        for (size_t i = 0; i < a.n; ++i)
            if (! (a.p[i] == b.p[i])) return false;
        return true;
    }
};
template<typename T>
struct mut_ref<T[]> {
    T*     p = nullptr;
    size_t n = 0;
    using value_type = T;
    constexpr mut_ref() noexcept = default;
    constexpr mut_ref(std::span<T> s) noexcept: p(s.data()), n(s.size()) {}
    template<size_t N>
    constexpr mut_ref(T (&a)[N]) noexcept: p(a), n(N) {}
    static constexpr auto from_raw_parts(T* data, usize len) noexcept -> mut_ref {
        mut_ref r;
        r.p = data;
        r.n = len.to_primitive();
        return r;
    }
    constexpr auto len() const noexcept -> usize { return usize(n); }
    constexpr auto is_empty() const noexcept -> bool { return n == 0; }
    constexpr auto size() const noexcept -> size_t { return n; }
    constexpr auto empty() const noexcept -> bool { return n == 0; }
    constexpr auto data() const noexcept -> T* { return p; }
    constexpr auto begin() const noexcept -> T* { return p; }
    constexpr auto end() const noexcept -> T* { return p + n; }
    constexpr auto operator[](usize i) const noexcept -> T& { return p[i.to_primitive()]; }
    constexpr auto as_raw_ptr() const noexcept -> T* { return p; }
    constexpr auto as_ptr() const noexcept -> ptr<T> { return ptr<T> { p }; }
    constexpr auto as_mut_ptr() const noexcept -> mut_ptr<T> { return mut_ptr<T> { p }; }
    constexpr auto as_ref() const noexcept -> ref<T[]> { return ref<T[]>::from_raw_parts(p, usize(n)); }
    constexpr operator ref<T[]>() const noexcept { return as_ref(); }
    constexpr operator std::span<T>() const noexcept { return { p, n }; }
    constexpr auto deref() const noexcept -> ref<T[]> { return as_ref(); }
    constexpr auto deref_mut() const noexcept -> mut_ref { return *this; }
};
template<typename T>
using slice = ref<T[]>;

template<typename T>
constexpr auto ref<T>::as_ptr() const noexcept -> ptr<T> { return ptr<T> { p }; }
template<typename T>
constexpr auto mut_ref<T>::as_ptr() const noexcept -> ptr<T> { return ptr<T> { p }; }
template<typename T>
constexpr auto mut_ref<T>::as_mut_ptr() const noexcept -> mut_ptr<T> { return mut_ptr<T> { p }; }
template<typename T>
constexpr auto ptr<T>::add(usize k) const noexcept -> ptr { return ptr { p + k.to_primitive() }; }
template<typename T>
constexpr auto mut_ptr<T>::add(usize k) const noexcept -> mut_ptr { return mut_ptr { p + k.to_primitive() }; }
namespace detail
{
// rstd 的切片本身没有 get/first/last，array 与 Vec 有；共用这两个帮助函数。
template<typename T>
constexpr auto slice_get(T const* p, size_t n, usize i) noexcept -> Option<ref<T>> {
    if (i.to_primitive() >= n) return {};
    return Option<ref<T>>(ref<T>::from_raw(p + i.to_primitive()));
}
template<typename T>
constexpr auto slice_get_mut(T* p, size_t n, usize i) noexcept -> Option<mut_ref<T>> {
    if (i.to_primitive() >= n) return {};
    return Option<mut_ref<T>>(mut_ref<T>::from_raw(p + i.to_primitive()));
}
} // namespace detail

struct str {};

// ref<str>：UTF-8 字节串视图，派生自 std::string_view。
template<>
struct ref<str> : std::string_view {
    using std::string_view::string_view;
    constexpr ref(std::string_view s) noexcept: std::string_view(s) {}
    constexpr auto len() const noexcept -> usize { return usize(std::string_view::size()); }
    constexpr auto is_empty() const noexcept -> bool { return std::string_view::empty(); }
    // rstd：size()/find() 等用 usize，查找失败返回 None。
    constexpr auto size() const noexcept -> usize { return usize(std::string_view::size()); }
    constexpr auto view() const noexcept -> std::string_view { return *this; }
    // rstd：按字节下标取 u8，不查越界。
    constexpr auto operator[](usize i) const noexcept -> u8 { return u8(static_cast<std::uint8_t>(view()[i.to_primitive()])); }
    constexpr auto find(ref<str> pat) const noexcept -> Option<usize> {
        auto i = view().find(pat.view());
        if (i == std::string_view::npos) return {};
        return Option<usize>(std::in_place, usize(i));
    }
    constexpr auto get(usize a, usize b) const noexcept -> Option<ref<str>> {
        if (a.to_primitive() > b.to_primitive() || b.to_primitive() > view().size()) return {};
        return Option<ref<str>>(std::in_place, view().substr(a.to_primitive(), b.to_primitive() - a.to_primitive()));
    }
    constexpr auto strip_prefix(ref<str> pat) const noexcept -> Option<ref<str>> {
        if (! view().starts_with(pat.view())) return {};
        return Option<ref<str>>(std::in_place, view().substr(pat.view().size()));
    }
    constexpr auto split_at(usize i) const noexcept -> std::tuple<ref<str>, ref<str>> {
        return { view().substr(0, i.to_primitive()), view().substr(i.to_primitive()) };
    }
    constexpr auto split_once(ref<str> pat) const noexcept -> Option<std::tuple<ref<str>, ref<str>>> {
        auto i = view().find(pat.view());
        if (i == std::string_view::npos) return {};
        return Option<std::tuple<ref<str>, ref<str>>>(std::in_place, view().substr(0, i), view().substr(i + pat.view().size()));
    }
    constexpr auto trim_ascii() const noexcept -> ref<str> {
        auto v = view();
        auto ws = [](char c) { return c == 0x20 || c == 0x09 || c == 0x0a || c == 0x0d || c == 0x0c; };
        while (! v.empty() && ws(v.front())) v.remove_prefix(1);
        while (! v.empty() && ws(v.back())) v.remove_suffix(1);
        return v;
    }
    auto as_bytes() const noexcept -> ref<u8[]>;
};
inline auto ref<str>::as_bytes() const noexcept -> ref<u8[]> {
    return ref<u8[]>::from_raw_parts(reinterpret_cast<u8 const*>(data()), len());
}

// array：std::array + rstd 的 usize 下标（越界 panic）、len/as_slice/get/first/last；
// 变参构造按直接初始化（与 rstd 相同，允许 array<f32, 3> { 1.0f, 2.0f, 3.0f }）。
template<typename T, size_t N>
struct array : std::array<T, N> {
    using base = std::array<T, N>;
    constexpr array() = default;
    template<typename... Us>
        requires(N > 0 && sizeof...(Us) == N && (std::constructible_from<T, Us &&> && ...))
    constexpr array(Us&&... v): base { { T(std::forward<Us>(v))... } } {}
    constexpr auto len() const noexcept -> usize { return usize(N); }
    constexpr auto is_empty() const noexcept -> bool { return N == 0; }
    constexpr auto at(usize i) -> T& {
        if (i.to_primitive() >= N) detail::panic_at(std::source_location::current(), "array index out of bounds");
        return base::operator[](i.to_primitive());
    }
    constexpr auto at(usize i) const -> T const& {
        if (i.to_primitive() >= N) detail::panic_at(std::source_location::current(), "array index out of bounds");
        return base::operator[](i.to_primitive());
    }
    constexpr auto operator[](usize i) -> T& { return at(i); }
    constexpr auto operator[](usize i) const -> T const& { return at(i); }
    constexpr auto as_ptr() const noexcept -> ptr<T> { return ptr<T> { base::data() }; }
    constexpr auto as_mut_ptr() noexcept -> mut_ptr<T> { return mut_ptr<T> { base::data() }; }
    constexpr auto as_slice() const noexcept -> ref<T[]> { return ref<T[]>::from_raw_parts(base::data(), usize(N)); }
    constexpr auto as_mut_slice() noexcept -> mut_ref<T[]> { return mut_ref<T[]>::from_raw_parts(base::data(), usize(N)); }
    constexpr auto deref() const noexcept -> ref<T[]> { return as_slice(); }
    constexpr auto deref_mut() noexcept -> mut_ref<T[]> { return as_mut_slice(); }
    constexpr auto get(usize i) const noexcept -> Option<ref<T>> { return detail::slice_get(base::data(), N, i); }
    constexpr auto get_mut(usize i) noexcept -> Option<mut_ref<T>> { return detail::slice_get_mut(base::data(), N, i); }
    constexpr auto first() const noexcept -> Option<ref<T>> { return get(usize(0)); }
    constexpr auto last() const noexcept -> Option<ref<T>> {
        if constexpr (N == 0) return {};
        else return get(usize(N - 1));
    }
    constexpr auto clone() const -> array { return *this; }
};

// ── String / format ──
struct String : std::string {
    using std::string::basic_string;
    String() = default;
    String(std::string s): std::string(std::move(s)) {}
    static auto make() -> String { return {}; }
    static auto make(ref<str> s) -> String { return String(std::string(s)); }
    auto as_str() const noexcept -> ref<str> { return ref<str>(std::string_view(*this)); }
    auto len() const noexcept -> usize { return usize(std::string::size()); }
    auto is_empty() const noexcept -> bool { return std::string::empty(); }
    auto capacity() const noexcept -> usize { return usize(std::string::capacity()); }
    void push_str(ref<str> s) { std::string::append(s.data(), s.view().size()); }
    void push(char c) { std::string::push_back(c); }
    auto size() const noexcept -> usize { return usize(std::string::size()); }
    void reserve(usize n) { std::string::reserve(std::string::size() + n.to_primitive()); }
    auto find(ref<str> pat) const noexcept -> Option<usize> { return as_str().find(pat); }
    auto get(usize a, usize b) const noexcept -> Option<ref<str>> { return as_str().get(a, b); }
    auto as_bytes() const noexcept -> ref<u8[]> { return as_str().as_bytes(); }
    auto clone() const -> String { return *this; }
};

template<typename... Args>
auto format(std::format_string<Args...> fmt, Args&&... args) -> String {
    return String(detail::rstd_format<Args...>(fmt, std::forward<Args>(args)...));
}

namespace cppstd
{
struct Utf8Error {};
// 登记：rstd 在这里做 UTF-8 校验，非法输入返回 Err（调用点 .unwrap() 即 abort）；compat 不再校验，一律 Ok。
inline auto as_str(std::string_view value) noexcept -> Result<ref<str>, Utf8Error> {
    return Result<ref<str>, Utf8Error>(std::in_place, value);
}
inline auto to_string(ref<str> value) -> std::string { return std::string(value); }
inline auto to_string(String const& value) -> std::string { return value; }
inline auto as_string_view(ref<str> value) noexcept -> std::string_view { return value; }
} // namespace cppstd

// ── Vec ──
template<typename T>
struct Vec : std::vector<T> {
    using std::vector<T>::vector;
    Vec() = default;
    static auto make() -> Vec { return {}; }
    auto capacity() const noexcept -> usize { return usize(std::vector<T>::capacity()); }
    // rstd：reserve(additional) 按“再多容纳 additional 个”计。
    void reserve(usize additional) { std::vector<T>::reserve(this->size() + additional.to_primitive()); }
    void extend_from_slice(T const* data, usize n) {
        this->reserve(usize(n));
        for (size_t i = 0; i < n.to_primitive(); ++i) this->push_back(detail::clone_value(data[i]));
    }
    static auto with_capacity(usize n) -> Vec {
        Vec v;
        static_cast<std::vector<T>&>(v).reserve(n.to_primitive());
        return v;
    }
    auto len() const noexcept -> usize { return usize(this->size()); }
    auto is_empty() const noexcept -> bool { return this->empty(); }
    void push(T&& v) { this->emplace_back(std::move(v)); }
    auto pop() -> Option<T> {
        if (this->empty()) return {};
        Option<T> out(std::in_place, std::move(this->back()));
        this->pop_back();
        return out;
    }
    auto operator[](usize i) -> T& {
        if (i.to_primitive() >= this->size()) detail::panic_at(std::source_location::current(), "Vec index out of bounds");
        return std::vector<T>::operator[](i.to_primitive());
    }
    auto operator[](usize i) const -> T const& {
        if (i.to_primitive() >= this->size()) detail::panic_at(std::source_location::current(), "Vec index out of bounds");
        return std::vector<T>::operator[](i.to_primitive());
    }
    auto as_slice() const noexcept -> ref<T[]> { return ref<T[]>::from_raw_parts(this->data(), usize(this->size())); }
    auto as_mut_slice() noexcept -> mut_ref<T[]> { return mut_ref<T[]>::from_raw_parts(this->data(), usize(this->size())); }
    auto deref() const noexcept -> ref<T[]> { return as_slice(); }
    auto deref_mut() noexcept -> mut_ref<T[]> { return as_mut_slice(); }
    auto as_ptr() const noexcept -> ptr<T> { return ptr<T> { this->data() }; }
    auto as_mut_ptr() noexcept -> mut_ptr<T> { return mut_ptr<T> { this->data() }; }
    auto get(usize i) const noexcept -> Option<ref<T>> { return detail::slice_get(this->data(), this->size(), i); }
    auto get_mut(usize i) noexcept -> Option<mut_ref<T>> { return detail::slice_get_mut(this->data(), this->size(), i); }
    auto first() const noexcept -> Option<ref<T>> { return get(usize(0)); }
    auto last() const noexcept -> Option<ref<T>> { return this->empty() ? Option<ref<T>>() : get(usize(this->size() - 1)); }
    void truncate(usize n) {
        if (n.to_primitive() < this->size()) this->erase(this->begin() + static_cast<std::ptrdiff_t>(n.to_primitive()), this->end());
    }
    template<typename F>
    void retain(F&& keep) {
        std::erase_if(static_cast<std::vector<T>&>(*this), [&](T const& v) { return ! keep(v); });
    }
    void extend_from_slice(ref<T[]> s) {
        std::vector<T>::reserve(this->size() + s.size());
        for (auto const& v : s) this->push_back(detail::clone_value(v));
    }
    void push(T const& v)
        requires std::is_trivially_copyable_v<T>
    {
        this->push_back(v);
    }
    void clear() noexcept { std::vector<T>::clear(); }
    auto clone() const -> Vec {
        Vec out;
        static_cast<std::vector<T>&>(out).reserve(this->size());
        for (auto const& v : *this) out.push_back(detail::clone_value(v));
        return out;
    }
};

// ── Arc / Weak / Box ──
template<typename T>
struct Weak;

template<typename T>
struct Arc : std::shared_ptr<T> {
    using std::shared_ptr<T>::shared_ptr;
    Arc(std::shared_ptr<T> p) noexcept: std::shared_ptr<T>(std::move(p)) {}
    template<typename... A>
    static auto make(A&&... a) -> Arc { return Arc(std::make_shared<T>(std::forward<A>(a)...)); }
    auto clone() const noexcept -> Arc { return *this; }
    auto strong_count() const noexcept -> usize { return usize(static_cast<size_t>(this->use_count())); }
    auto downgrade() const noexcept -> Weak<T>;
    auto as_ptr() const noexcept -> ptr<T> { return ptr<T> { this->get() }; }
};
template<typename T>
struct Weak : std::weak_ptr<T> {
    using std::weak_ptr<T>::weak_ptr;
    // rstd 的 upgrade 返回可空的 Arc（explicit operator bool 判活），不是 Option。
    auto upgrade() const noexcept -> Arc<T> { return Arc<T>(this->lock()); }
};
template<typename T>
auto Arc<T>::downgrade() const noexcept -> Weak<T> { return Weak<T>(static_cast<std::shared_ptr<T> const&>(*this)); }

template<typename T>
struct Box : std::unique_ptr<T> {
    using std::unique_ptr<T>::unique_ptr;
    template<typename... A>
    static auto make(A&&... a) -> Box { return Box(new T(std::forward<A>(a)...)); }
};

// ── 集合 ──
namespace detail
{
// String 键支持用 ref<str>/string_view 异构查找。
template<typename K>
struct MapHash : std::hash<K> {};
template<>
struct MapHash<String> {
    using is_transparent = void;
    auto operator()(std::string_view s) const noexcept -> size_t { return std::hash<std::string_view> {}(s); }
};
template<typename K>
using MapEq = std::conditional_t<std::is_same_v<K, String>, std::equal_to<>, std::equal_to<K>>;
template<typename K>
using MapLess = std::conditional_t<std::is_same_v<K, String>, std::less<>, std::less<K>>;

// HashMap 与 BTreeMap 的共用 rstd 接口，Base 为 std::unordered_map 或 std::map。
template<typename Base, typename K, typename V>
struct MapOps : Base {
    using Base::Base;
    auto len() const noexcept -> usize { return usize(Base::size()); }
    auto is_empty() const noexcept -> bool { return Base::empty(); }
    auto insert(K key, V value) -> Option<V> {
        auto [it, inserted] = Base::try_emplace(std::move(key), std::move(value));
        if (inserted) return {};
        // try_emplace 未插入时不会移走 value
        return Option<V>(std::in_place, std::exchange(it->second, std::move(value)));
    }
    template<typename Q>
    auto get(Q const& key) const -> Option<ref<V>> {
        auto it = Base::find(key);
        if (it == Base::end()) return {};
        return Option<ref<V>>(ref<V>(it->second));
    }
    template<typename Q>
    auto get_mut(Q const& key) -> Option<mut_ref<V>> {
        auto it = Base::find(key);
        if (it == Base::end()) return {};
        return Option<mut_ref<V>>(mut_ref<V>(it->second));
    }
    template<typename Q>
    auto contains_key(Q const& key) const -> bool { return Base::find(key) != Base::end(); }
    template<typename Q>
    auto remove(Q const& key) -> Option<V> {
        auto it = Base::find(key);
        if (it == Base::end()) return {};
        Option<V> out(std::in_place, std::move(it->second));
        Base::erase(it);
        return out;
    }
    // 迭代项与 rstd 相同：tuple<ref<K>, ref<V>>；顺序由底层容器决定（见文件头登记）。
    auto iter() const {
        return static_cast<Base const&>(*this) | std::views::transform([](auto const& kv) {
                   return std::tuple<ref<K>, ref<V>>(ref<K>(kv.first), ref<V>(kv.second));
               });
    }
    auto values() const {
        return static_cast<Base const&>(*this) | std::views::transform([](auto const& kv) { return ref<V>(kv.second); });
    }
};
} // namespace detail

namespace collections
{
template<typename K, typename V>
struct HashMap : detail::MapOps<std::unordered_map<K, V, detail::MapHash<K>, detail::MapEq<K>>, K, V> {
    using detail::MapOps<std::unordered_map<K, V, detail::MapHash<K>, detail::MapEq<K>>, K, V>::MapOps;
    static auto make() -> HashMap { return {}; }
    static auto with_capacity(usize n) -> HashMap {
        HashMap m;
        m.reserve(n.to_primitive());
        return m;
    }
};
template<typename K, typename V>
struct BTreeMap : detail::MapOps<std::map<K, V, detail::MapLess<K>>, K, V> {
    using detail::MapOps<std::map<K, V, detail::MapLess<K>>, K, V>::MapOps;
    static auto make() -> BTreeMap { return {}; }
};

template<typename Base, typename K>
struct SetOps : Base {
    using Base::Base;
    auto len() const noexcept -> usize { return usize(Base::size()); }
    auto is_empty() const noexcept -> bool { return Base::empty(); }
    auto insert(K value) -> bool { return Base::insert(std::move(value)).second; }
    template<typename Q>
    auto contains(Q const& v) const -> bool { return Base::find(v) != Base::end(); }
    template<typename Q>
    auto remove(Q const& v) -> bool {
        auto it = Base::find(v);
        if (it == Base::end()) return false;
        Base::erase(it);
        return true;
    }
    auto iter() const {
        return static_cast<Base const&>(*this) | std::views::transform([](K const& k) { return ref<K>(k); });
    }
};
template<typename K>
struct HashSet : SetOps<std::unordered_set<K, detail::MapHash<K>, detail::MapEq<K>>, K> {
    using SetOps<std::unordered_set<K, detail::MapHash<K>, detail::MapEq<K>>, K>::SetOps;
    static auto make() -> HashSet { return {}; }
};
template<typename K>
struct BTreeSet : SetOps<std::set<K, detail::MapLess<K>>, K> {
    using SetOps<std::set<K, detail::MapLess<K>>, K>::SetOps;
    static auto make() -> BTreeSet { return {}; }
};
} // namespace collections

// ── sync ──
namespace sync
{
template<typename T>
using Arc = compat::Arc<T>;
template<typename T>
using Weak = compat::Weak<T>;

namespace atomic
{
enum class Ordering { Relaxed, Release, Acquire, AcqRel, SeqCst };
constexpr auto to_std(Ordering o) noexcept -> std::memory_order {
    switch (o) {
    case Ordering::Relaxed: return std::memory_order_relaxed;
    case Ordering::Release: return std::memory_order_release;
    case Ordering::Acquire: return std::memory_order_acquire;
    case Ordering::AcqRel: return std::memory_order_acq_rel;
    case Ordering::SeqCst: return std::memory_order_seq_cst;
    }
    return std::memory_order_seq_cst;
}
template<typename T>
struct Atomic {
    using P = detail::prim_t<T>;
    std::atomic<P> v;
    constexpr Atomic() noexcept: v(P {}) {}
    constexpr Atomic(T init) noexcept: v(detail::prim(init)) {}
    auto load(Ordering o) const noexcept -> T { return T(v.load(to_std(o))); }
    void store(T x, Ordering o) noexcept { v.store(detail::prim(x), to_std(o)); }
    auto exchange(T x, Ordering o) noexcept -> T { return T(v.exchange(detail::prim(x), to_std(o))); }
    auto fetch_add(T x, Ordering o) noexcept -> T { return T(v.fetch_add(detail::prim(x), to_std(o))); }
};
} // namespace atomic

template<typename T>
struct MutexGuard {
    std::unique_lock<std::mutex> lock;
    T*                           value;
    auto operator*() const noexcept -> T& { return *value; }
    auto operator->() const noexcept -> T* { return value; }
};
template<typename T>
struct Mutex {
    mutable std::mutex m;
    mutable T          value;
    explicit Mutex(T v): value(std::move(v)) {}
    // rstd 的 lock() 返回 Result<MutexGuard<T>, empty>（中毒语义），这里永不失败。
    auto lock() const -> Result<MutexGuard<T>, empty> {
        return Result<MutexGuard<T>, empty>(std::in_place, MutexGuard<T> { std::unique_lock<std::mutex>(m), &value });
    }
};
} // namespace sync

namespace vec
{
template<typename T>
using Vec = compat::Vec<T>;
}
namespace string
{
using String = compat::String;
}
namespace boxed
{
template<typename T>
using Box = compat::Box<T>;
}

// ── 日志：rstd::log 的最小实现（默认无 logger、级别 Off，宏什么也不打印，与 rstd 相同）──
namespace log
{
enum class Level : size_t { Error = 1, Warn, Info, Debug, Trace };
enum class LevelFilter : size_t { Off = 0, Error, Warn, Info, Debug, Trace };
constexpr auto operator<=>(Level a, LevelFilter b) noexcept { return static_cast<size_t>(a) <=> static_cast<size_t>(b); }
constexpr auto operator==(Level a, LevelFilter b) noexcept -> bool { return static_cast<size_t>(a) == static_cast<size_t>(b); }

struct Metadata {
    Level    level;
    ref<str> target;
};
struct Record {
    Metadata         metadata;
    std::string_view message;
    auto lvl() const noexcept -> Level { return metadata.level; }
    auto target() const noexcept -> ref<str> { return metadata.target; }
};
// rstd 的 Impl<log::Log, T> 改成虚基类（T2 的 trait/dyn 重写沿用此形状）。
struct Log {
    virtual ~Log()                                       = default;
    virtual auto enabled(Metadata const& m) const -> bool = 0;
    virtual void log(Record const& r) const               = 0;
    virtual void flush() const                            = 0;
};

namespace detail
{
inline std::atomic<size_t>     g_max_level { 0 };
inline std::atomic<Log const*> g_logger { nullptr };
} // namespace detail

inline auto max_level() noexcept -> LevelFilter { return static_cast<LevelFilter>(detail::g_max_level.load(std::memory_order_relaxed)); }
inline void set_max_level(LevelFilter l) noexcept { detail::g_max_level.store(static_cast<size_t>(l), std::memory_order_relaxed); }
inline auto set_logger(Log const& logger) noexcept -> bool {
    Log const* expected = nullptr;
    return detail::g_logger.compare_exchange_strong(expected, &logger, std::memory_order_acq_rel);
}
inline auto log_enabled(Level level, ref<str> target) noexcept -> bool {
    if (level > max_level()) return false;
    auto* l = detail::g_logger.load(std::memory_order_acquire);
    return l != nullptr && l->enabled(Metadata { level, target });
}
template<typename... Args>
void emit(Level level, std::format_string<Args...> fmt, Args&&... args) {
    if (level > max_level()) return;
    auto* l = detail::g_logger.load(std::memory_order_acquire);
    if (l == nullptr) return;
    auto msg = compat::detail::rstd_format<Args...>(fmt, std::forward<Args>(args)...);
    l->log(Record { Metadata { level, ref<str>() }, msg });
}

// EnvLogger：输出 "[<UTC RFC3339> LEVEL] 消息"，照抄 rstd 的格式；过滤器只支持级别名（引擎只用 "info" 与 RSTD_LOG）。
struct EnvLogger : Log {
    LevelFilter default_level = LevelFilter::Error;
    bool        color         = false;

    static auto parse_level(std::string_view s, LevelFilter& out) noexcept -> bool {
        auto eq = [&](std::string_view w) {
            if (s.size() != w.size()) return false;
            for (size_t i = 0; i < s.size(); ++i)
                if ((s[i] | 0x20) != w[i]) return false;
            return true;
        };
        if (eq("off")) out = LevelFilter::Off;
        else if (eq("error")) out = LevelFilter::Error;
        else if (eq("warn")) out = LevelFilter::Warn;
        else if (eq("info")) out = LevelFilter::Info;
        else if (eq("debug")) out = LevelFilter::Debug;
        else if (eq("trace")) out = LevelFilter::Trace;
        else return false;
        return true;
    }
    EnvLogger() noexcept {
        if (char const* v = std::getenv("RSTD_LOG")) parse_level(v, default_level);
        std::string_view style = std::getenv("RSTD_LOG_STYLE") ? std::getenv("RSTD_LOG_STYLE") : "";
#if defined(_WIN32)
        bool tty = _isatty(_fileno(stderr)) != 0;
#else
        bool tty = isatty(fileno(stderr)) != 0;
#endif
        color = style == "always" || (style != "never" && tty);
    }
    explicit EnvLogger(ref<str> filters) noexcept { parse_level(filters, default_level); }

    auto enabled(Metadata const& m) const -> bool override { return m.level <= default_level; }
    void flush() const override {}
    void log(Record const& r) const override {
        if (! enabled(r.metadata)) return;
        static constexpr std::string_view names[] = { "?????", "ERROR", "WARN ", "INFO ", "DEBUG", "TRACE" };
        static constexpr std::string_view colors[] = { "", "\x1b[31m", "\x1b[33m", "\x1b[32m", "\x1b[34m", "\x1b[36m" };
        auto        idx = static_cast<size_t>(r.lvl());
        std::time_t now = std::time(nullptr);
        std::tm     tm {};
#if defined(_WIN32)
        gmtime_s(&tm, &now);
#else
        gmtime_r(&now, &tm);
#endif
        char ts[21];
        std::strftime(ts, sizeof ts, "%Y-%m-%dT%H:%M:%SZ", &tm);
        std::string line = std::format("[{} {}{}{}", ts, color ? colors[idx] : "", names[idx], color ? "\x1b[0m" : "");
        if (! r.target().empty()) line += std::format(" {}", std::string_view(r.target()));
        line += std::format("] {}\n", r.message);
#if defined(_WIN32)
        unsigned long written = 0;
        WriteFile(GetStdHandle(static_cast<unsigned long>(-12)), line.data(), static_cast<unsigned long>(line.size()), &written, nullptr);
#else
        (void)! ::write(2, line.data(), line.size());
#endif
    }
};
} // namespace log

// ── mtp：rstd 的类型谓词（引擎只用这几个）──
namespace mtp
{
template<typename T>
inline constexpr bool is_const = std::is_const_v<T>;
template<typename T>
inline constexpr bool is_ptr = std::is_pointer_v<T>;
template<typename T>
inline constexpr bool is_arithmetic = std::is_arithmetic_v<T>;
template<typename T>
inline constexpr bool triv_copy = std::is_trivially_copyable_v<T>;
template<typename A, typename B>
concept same = std::same_as<A, B>;
template<typename T, typename... Us>
concept any = (std::same_as<T, Us> || ...);
} // namespace mtp

// ── char_：UTF-8 解码（照抄 rstd::char_::decode_utf8，非法序列返回 U+FFFD 与宽度 1）──
namespace char_
{
inline constexpr char32_t REPLACEMENT = 0xFFFD;
constexpr auto decode_utf8(char const* p, usize len) noexcept -> std::tuple<char32_t, usize> {
    auto const available = len.to_primitive();
    if (available == 0) return { REPLACEMENT, usize() };
    auto const b0 = static_cast<std::uint8_t>(p[0]);
    if (b0 <= 0x7F) return { static_cast<char32_t>(b0), usize(1) };
    size_t   seq_len;
    char32_t cp;
    if ((b0 & 0xE0) == 0xC0) {
        seq_len = 2;
        cp      = b0 & 0x1F;
    } else if ((b0 & 0xF0) == 0xE0) {
        seq_len = 3;
        cp      = b0 & 0x0F;
    } else if ((b0 & 0xF8) == 0xF0) {
        seq_len = 4;
        cp      = b0 & 0x07;
    } else {
        return { REPLACEMENT, usize(1) };
    }
    if (seq_len > available) return { REPLACEMENT, usize(1) };
    for (size_t i = 1; i < seq_len; ++i) {
        auto const b = static_cast<std::uint8_t>(p[i]);
        if ((b & 0xC0) != 0x80) return { REPLACEMENT, usize(1) };
        cp = (cp << 6) | (b & 0x3F);
    }
    if (seq_len == 2 && cp < 0x80) return { REPLACEMENT, usize(1) };
    if (seq_len == 3 && cp < 0x800) return { REPLACEMENT, usize(1) };
    if (seq_len == 4 && cp < 0x10000) return { REPLACEMENT, usize(1) };
    if (cp >= 0xD800 && cp <= 0xDFFF) return { REPLACEMENT, usize(1) };
    if (cp > 0x10FFFF) return { REPLACEMENT, usize(1) };
    return { cp, usize(seq_len) };
}
} // namespace char_

// ── time：Instant/Duration（单调时钟），只覆盖引擎用到的成员 ──
namespace time
{
struct Duration {
    std::chrono::nanoseconds d {};
    static auto from_millis(u64 ms) noexcept -> Duration { return { std::chrono::milliseconds(ms.to_primitive()) }; }
    static auto from_secs_f64(f64 s) noexcept -> Duration {
        return { std::chrono::duration_cast<std::chrono::nanoseconds>(std::chrono::duration<double>(s.to_primitive())) };
    }
    auto as_secs_f64() const noexcept -> f64 { return f64(std::chrono::duration<double>(d).count()); }
    auto as_secs_f32() const noexcept -> f32 { return f32(static_cast<float>(std::chrono::duration<double>(d).count())); }
    auto as_millis() const noexcept -> u64 { return u64(static_cast<std::uint64_t>(std::chrono::duration_cast<std::chrono::milliseconds>(d).count())); }
    auto as_nanos() const noexcept -> u64 { return u64(static_cast<std::uint64_t>(d.count())); }
    friend auto operator<=>(Duration, Duration) = default;
};
struct Instant {
    std::chrono::steady_clock::time_point t {};
    static auto now() noexcept -> Instant { return { std::chrono::steady_clock::now() }; }
    auto elapsed() const noexcept -> Duration { return { now().t - t }; }
    friend auto operator-(Instant a, Instant b) noexcept -> Duration { return { a.t - b.t }; }
    friend auto operator+(Instant a, Duration b) noexcept -> Instant { return { a.t + std::chrono::duration_cast<std::chrono::steady_clock::duration>(b.d) }; }
    friend auto operator<=>(Instant, Instant) = default;
};
inline constexpr u64 NANOS_PER_SEC = u64(1000000000ull);
} // namespace time

// ── 字面量 ──
namespace literals
{
consteval auto operator""_str(char const* s, size_t n) -> ref<str> { return ref<str>(std::string_view(s, n)); }
} // namespace literals

// ── prelude：rstd::prelude 里被引擎非限定使用的名字 ──
namespace prelude
{
using compat::array;
using compat::as_cast;
using compat::Err;
using compat::f32;
using compat::f64;
using compat::i16;
using compat::i32;
using compat::i64;
using compat::i8;
using compat::isize;
using compat::mut_ref;
using compat::None;
using compat::Ok;
using compat::Option;
using compat::ref;
using compat::Result;
using compat::slice;
using compat::Some;
using compat::str;
using compat::u16;
using compat::u32;
using compat::u64;
using compat::u8;
using compat::usize;
using compat::Vec;
using compat::String;
using compat::Box;
using compat::Arc;
} // namespace prelude
} // namespace owe::compat

// ── std::formatter / std::hash：让强类型可以进 format 与 unordered 容器 ──
namespace owe::compat::detail
{
// Rust 浮点 Display：最短往返的数字按位置记数法展开（不用指数），NaN→"NaN"，±inf→"inf"/"-inf"。
template<typename P>
auto rust_float_display(P v) -> std::string {
    if (std::isnan(v)) return "NaN";
    if (std::isinf(v)) return v < 0 ? "-inf" : "inf";
    char buf[64];
    auto end = std::to_chars(buf, buf + sizeof buf, v, std::chars_format::scientific).ptr;
    std::string_view s(buf, static_cast<size_t>(end - buf));
    std::string out;
    if (s.front() == '-') {
        out += '-';
        s.remove_prefix(1);
    }
    auto const epos = s.find('e');
    std::string digits;
    for (char c : s.substr(0, epos))
        if (c != '.') digits += c;
    int const point = std::stoi(std::string(s.substr(epos + 1))) + 1;
    if (point <= 0) out += "0." + std::string(static_cast<size_t>(-point), '0') + digits;
    else if (point >= static_cast<int>(digits.size())) out += digits + std::string(static_cast<size_t>(point) - digits.size(), '0');
    else out += digits.substr(0, static_cast<size_t>(point)) + "." + digits.substr(static_cast<size_t>(point));
    return out;
}
} // namespace owe::compat::detail

template<typename T, typename C>
    requires owe::compat::detail::IntegerWrapper<T>
struct std::formatter<T, C> : std::formatter<typename T::primitive_type, C> {
    auto format(T const& v, auto& ctx) const {
        return std::formatter<typename T::primitive_type, C>::format(v.to_primitive(), ctx);
    }
};
// 格式说明已被 strip_specs 去掉，浮点一律按 Rust Display 输出。
template<typename T, typename C>
    requires owe::compat::detail::FloatWrapper<T>
struct std::formatter<T, C> {
    constexpr auto parse(auto& ctx) { return ctx.begin(); }
    auto format(T const& v, auto& ctx) const {
        auto s = owe::compat::detail::rust_float_display(v.to_primitive());
        return std::ranges::copy(s, ctx.out()).out;
    }
};
template<typename C>
struct std::formatter<owe::compat::String, C> : std::formatter<std::string_view, C> {};
template<typename C>
struct std::formatter<owe::compat::ref<owe::compat::str>, C> : std::formatter<std::string_view, C> {};
template<typename T>
    requires owe::compat::detail::IntegerWrapper<T>
struct std::hash<T> {
    auto operator()(T v) const noexcept -> std::size_t { return std::hash<typename T::primitive_type> {}(v.to_primitive()); }
};
template<>
struct std::hash<owe::compat::String> : std::hash<std::string> {};

// ── trait 残留：Impl<hash::Hash, T> / Impl<fmt::Display, T>（T2-6：保持引擎写法，T3 改成 std::hash / std::formatter 特化）──
namespace owe::compat
{
struct Copy {};
template<typename T>
struct ImplBase {
    T const* self_ = nullptr;
    constexpr auto self() const noexcept -> T const& { return *self_; }
};
template<typename Trait, typename T>
struct Impl;

namespace hash
{
struct Hash {};
struct Hasher {};
// 只决定 unordered 容器的分桶，不进输出；FNV-1a 64 位。
struct DefaultHasher {
    std::uint64_t h = 14695981039346656037ull;
    constexpr void write_u64(std::uint64_t v) noexcept {
        for (int i = 0; i < 8; ++i) {
            h ^= (v >> (8 * i)) & 0xffu;
            h *= 1099511628211ull;
        }
    }
    constexpr auto finish() const noexcept -> std::uint64_t { return h; }
};
template<typename T, typename H>
constexpr void hash_into(T const& v, H& state) noexcept;
} // namespace hash

template<typename T, typename Trait>
concept Impled = (std::same_as<Trait, hash::Hasher> && requires(T& h, std::uint64_t v) { h.write_u64(v); }) ||
                 (std::same_as<Trait, Copy> && std::is_trivially_copyable_v<T>) ||
                 requires { sizeof(Impl<Trait, T>); };

namespace hash
{
template<typename T, typename H>
constexpr void hash_into(T const& v, H& state) noexcept {
    if constexpr (requires(Impl<Hash, T> i) { i.hash(state); }) {
        Impl<Hash, T> i;
        i.self_ = std::addressof(v);
        i.hash(state);
    } else if constexpr (detail::IntegerWrapper<T>) {
        state.write_u64(static_cast<std::uint64_t>(v.to_primitive()));
    } else if constexpr (std::is_enum_v<T>) {
        state.write_u64(static_cast<std::uint64_t>(std::to_underlying(v)));
    } else if constexpr (std::is_integral_v<T>) {
        state.write_u64(static_cast<std::uint64_t>(v));
    } else if constexpr (std::is_pointer_v<T>) {
        state.write_u64(static_cast<std::uint64_t>(reinterpret_cast<std::uintptr_t>(v)));
    } else if constexpr (std::is_convertible_v<T const&, std::string_view>) {
        state.write_u64(std::hash<std::string_view> {}(std::string_view(v)));
    } else {
        state.write_u64(std::hash<T> {}(v));
    }
}
} // namespace hash

namespace fmt
{
struct Display {};
struct Arguments {
    std::string text;
    template<typename... A>
    static auto make(std::format_string<A...> f, A&&... a) -> Arguments {
        return Arguments { detail::rstd_format<A...>(f, std::forward<A>(a)...) };
    }
};
struct Formatter {
    std::string* out;
    auto write_raw(char const* p, size_t n) -> bool {
        out->append(p, n);
        return true;
    }
    auto write_str(std::string_view s) -> bool {
        out->append(s);
        return true;
    }
    auto write_fmt(Arguments const& a) -> bool {
        out->append(a.text);
        return true;
    }
};
} // namespace fmt
} // namespace owe::compat

template<typename T>
    requires requires(owe::compat::Impl<owe::compat::hash::Hash, T> i, owe::compat::hash::DefaultHasher& h) { i.hash(h); }
struct std::hash<T> {
    auto operator()(T const& v) const noexcept -> std::size_t {
        owe::compat::hash::DefaultHasher h;
        owe::compat::hash::hash_into(v, h);
        return static_cast<std::size_t>(h.finish());
    }
};
template<typename T, typename C>
    requires requires(owe::compat::Impl<owe::compat::fmt::Display, T> i, owe::compat::fmt::Formatter& f) { i.fmt(f); }
struct std::formatter<T, C> {
    constexpr auto parse(auto& ctx) { return ctx.begin(); }
    auto format(T const& v, auto& ctx) const {
        std::string s;
        owe::compat::Impl<owe::compat::fmt::Display, T> i;
        i.self_ = std::addressof(v);
        owe::compat::fmt::Formatter f { &s };
        i.fmt(f);
        return std::ranges::copy(s, ctx.out()).out;
    }
};

// ── 宏：与 rstd/macro.hpp 同名 ──
#define OWE_COMPAT_TRY_1(EXPR)                                                                    \
    __extension__({                                                                               \
        auto&& owe_try_r_ = (EXPR);                                                               \
        if (! ::owe::compat::detail::try_ok(owe_try_r_))                                          \
            return ::owe::compat::detail::try_residual(std::move(owe_try_r_));                    \
        ::owe::compat::detail::try_value(std::move(owe_try_r_));                                  \
    })
#define OWE_COMPAT_ASSERT(EXP)                                                                     \
    ((EXP) ? (void)0 : ::owe::compat::detail::panic_at(std::source_location::current(), #EXP))

#define OWE_COMPAT_LOG(LEVEL, ...)                                                                 \
    do {                                                                                           \
        if (::owe::compat::log::log_enabled(LEVEL, ::owe::compat::ref<::owe::compat::str>())) {    \
            ::owe::compat::log::emit(LEVEL, __VA_ARGS__);                                          \
        }                                                                                          \
    } while (0)

#ifndef OWE_COMPAT_NO_RSTD_NAMES
namespace rstd = owe::compat;
#define rstd_try(EXPR) OWE_COMPAT_TRY_1(EXPR)
#define rstd_assert(EXP) OWE_COMPAT_ASSERT(EXP)
#define rstd_error(...) OWE_COMPAT_LOG(::owe::compat::log::Level::Error, __VA_ARGS__)
#define rstd_warn(...) OWE_COMPAT_LOG(::owe::compat::log::Level::Warn, __VA_ARGS__)
#define rstd_info(...) OWE_COMPAT_LOG(::owe::compat::log::Level::Info, __VA_ARGS__)
#ifdef NDEBUG
#define rstd_debug(...) ((void)0)
#else
#define rstd_debug(...) OWE_COMPAT_LOG(::owe::compat::log::Level::Debug, __VA_ARGS__)
#endif
#endif
