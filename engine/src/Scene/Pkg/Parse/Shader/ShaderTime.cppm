module;

#include <algorithm>
#include <array>
#include <bit>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <compare>
#include <cstdio>
#include <functional>
#include <iterator>
#include <map>
#include <memory>
#include <initializer_list>
#include <numbers>
#include <numeric>
#include <optional>
#include <set>
#include <span>
#include <string>
#include <string_view>
#include <unordered_map>
#include <utility>
#include <vector>

export module wescene.pkg.parse:shader_time;

// 着色器时间签名：在 glslang 产出的 SPIR-V（combo/define 预处理后实际运行的代码）上做抽象解释，
// 把 g_Time 沿数据流传到输出，给出每个 pass 的周期（精确倍数）或不周期的证明（原因与出处）。不渲染、不搜周期。
// 抽象域逐分量：与时间无关（可带已知常数）；a·t + 周期部分（a 取可能系数的集合）；证明不周期。
// 规则：sin/cos/tan/fract/mod 作用在线性式上得周期 2π/a、π/a、1/a、m/a；周期值做任何运算仍是周期，
// 最后按可公度类取 LCM；线性式不经周期函数流到输出 = 漂移（系数 0 除外）；采样坐标线性滚动时 repeat 轴周期 1/|v|，
// clamp 轴过某时刻后静止（记为暂态）；与线性 t 比较（含 switch 按线性 t 选分支）看差的范围：有界则过 settle 时刻后固定
// （暂态，给出时刻上界），含 tan(线性 t) 的极点则永不固定、判不周期，范围说不清只报没推下去；
// 周期比为无理数时各类分别输出，交给 C# 的调速逻辑。
export namespace owe::shader_time
{

enum class Wrap
{
    Unknown,
    Repeat,
    Clamp,
};

struct UniformValue {
    enum class Kind
    {
        Unknown,  // 与时间无关、值未知
        Constant, // 材质常量或着色器默认值
        External, // 随时间变化但不在 g_Time 上（动画化的材质值等）
    };
    Kind               kind { Kind::Unknown };
    std::vector<float> values;
};

struct Inputs {
    std::function<UniformValue(std::string_view)>        uniform; // 按 uniform 成员名
    std::function<std::array<Wrap, 2>(std::string_view)> wrap;    // 按采样器变量名，S/T 两轴
};

struct Period {
    double    seconds { 0 };
    long long num { 0 }; // seconds = num/den（pi 时再乘 π）；num = 0：不是简单有理数、LCM 溢出或 π 次数不是 0/1，单独成类
    long long den { 0 };
    bool      pi { false };
};

// 调频旋钮：t 的系数里一个可改写的直接乘法因子（着色器浮点字面量或材质 uniform），
// 或顶点输出分量（varying 非空）：顶点程序换个时间再算一遍、只取这个分量，就只给经过它的项调频
struct Knob {
    std::string stage;   // vert | frag
    std::string uniform; // 空 = 字面量
    float       literal { 0 };
    bool        inverse { false }; // 作为除数出现
    std::string varying;
    int         component { -1 };
    auto        operator<=>(const Knob&) const = default;
};

// 逐项周期：seconds = num/den·π^pi；pi 空 = π 次数未知；num = 0：化不成有理数
struct Term {
    double             seconds { 0 };
    long long          num { 0 };
    long long          den { 0 };
    std::optional<int> pi;
    std::vector<Knob>  knobs;
};

struct Signature {
    std::string              kind { "static" }; // static | periodic | aperiodic
    std::vector<Period>      periods;           // 每个可公度类一个（类内已取 LCM）；多于一个 = 周期比无理
    std::vector<Term>        terms;             // 每个不同的 (周期, π 次数, 旋钮集合) 一项
    std::vector<std::string> reasons;           // 不周期的原因与出处
    std::vector<std::string> external;          // 随时间变化的外部输入（音频、指针、日时、动画化的材质值）
    bool                     transient { false }; // clamp 采样的滚动在某时刻后停住：稳态之前有一段不周期
    double                   settle { -1 };       // 与线性时间比较的分支在此时刻（秒，上界）之后固定；< 0：没有这种分支
};

Signature   Analyze(std::span<const std::vector<unsigned int>> stages, const Inputs& inputs);
std::string ToJson(const Signature& signature);

} // namespace owe::shader_time

namespace owe::shader_time
{
namespace
{

constexpr std::size_t kMaxRates   = 8;
constexpr std::size_t kMaxPeriods = 32;
constexpr std::size_t kMaxPasses  = 48;
constexpr std::size_t kMaxComps   = 256;
constexpr double      kPi         = std::numbers::pi;
constexpr double      kInf        = HUGE_VAL;

constexpr int         kNoPi       = 1 << 20; // π 次数未知

bool Near(double a, double b) { return a == b || std::abs(a - b) <= 1e-9 * std::max(std::abs(a), std::abs(b)); }
int  PiAdd(int a, int b) { return a == kNoPi || b == kNoPi ? kNoPi : a + b; }
int  PiNeg(int a) { return a == kNoPi ? kNoPi : -a; }

using Knobs = std::vector<Knob>; // 有序、去重
Knobs Union(Knobs a, const Knobs& b) {
    a.insert(a.end(), b.begin(), b.end());
    std::sort(a.begin(), a.end());
    a.erase(std::unique(a.begin(), a.end()), a.end());
    return a;
}
Knobs Common(const Knobs& a, const Knobs& b) {
    Knobs r;
    std::set_intersection(a.begin(), a.end(), b.begin(), b.end(), std::back_inserter(r));
    return r;
}

// 浮点字面量是否为 float32(k·π/n)（π 次数 +1）或 float32(k/(n·π))（−1），n ≤ 12、|k| ≤ 48 且已约分：
// 认出 M_PI、M_PI_2（WE 里是 2π）、M_PI_HALF、1/π 这类写法，返回精确值与次数
std::optional<std::pair<double, int>> PiLiteral(float f) {
    for (int n = 1; n <= 12; ++n)
        for (int k = -48; k <= 48; ++k) {
            if (k == 0 || std::gcd(k, n) != 1) continue;
            if (float(k * kPi / n) == f) return std::pair { k * kPi / n, 1 };
            if (float(k / (n * kPi)) == f) return std::pair { k / (n * kPi), -1 };
        }
    return std::nullopt;
}

// t 的一个系数：值、π 次数、直接乘法因子里可改写的来源
struct Rate {
    double v { 0 };
    int    pi { 0 };
    Knobs  knobs;
    bool   operator==(const Rate&) const = default;
};
// 同值合并：次数不同记未知，旋钮取交集（只留各路径都有的因子）
void AddUnique(std::vector<Rate>& v, const Rate& x) {
    for (Rate& y : v)
        if (Near(x.v, y.v)) {
            if (y.pi != x.pi) y.pi = kNoPi;
            y.knobs = Common(y.knobs, x.knobs);
            return;
        }
    v.push_back(x);
}
// x + sign·y：一边为 0 取另一边的次数与旋钮；否则次数不同即未知、旋钮取交集
Rate Sum(const Rate& x, const Rate& y, double sign) {
    if (x.v == 0) return Rate { sign * y.v, y.pi, y.knobs };
    if (y.v == 0) return x;
    // 抵消：mod 展开的 a − b·floor(a/b) 里系数 r − b·(r/b) 在双精度下常剩 1 ulp，那不是漂移
    if (Near(x.v, -sign * y.v)) return Rate {};
    return Rate { x.v + sign * y.v, x.pi == y.pi ? x.pi : kNoPi, Common(x.knobs, y.knobs) };
}

// 周期带上 π 次数（函数周期的次数 − 系数的次数）与系数的旋钮；归类只按次数
struct Per {
    double s { 0 };
    int    pi { 0 };
    Knobs  knobs;
    bool   operator==(const Per&) const = default;
};
void AddUnique(std::vector<Per>& v, const Per& x) {
    for (const Per& y : v)
        if (y.pi == x.pi && Near(x.s, y.s) && y.knobs == x.knobs) return;
    v.push_back(x);
}
// 周期个数上限按 (周期, 次数) 计，同一周期只是旋钮不同不多占
std::size_t Distinct(const std::vector<Per>& v) {
    std::size_t n = 0;
    for (std::size_t i = 0; i < v.size(); ++i)
        n += std::none_of(v.begin(), v.begin() + long(i), [&](const Per& y) { return y.pi == v[i].pi && Near(y.s, v[i].s); });
    return n;
}
void AddUnique(std::vector<std::string>& v, const std::string& x) {
    if (std::find(v.begin(), v.end(), x) == v.end()) v.push_back(x);
}

struct Comp {
    bool                     known { false }; // 只在与时间无关时成立
    double                   value { 0 };
    int                      pi { 0 };           // known 时：值的 π 次数
    Knobs                    knobs;              // known 时：值的直接乘法因子里可改写的来源
    std::vector<Rate>        rates { Rate {} };  // t 的可能系数（含 0）
    bool                     rate_unknown { false };
    // 含非时间部分（纹理坐标、非零常量、周期函数结果等）：再乘的因子会连带缩放静态部分，改它会变观感，不记旋钮
    bool                     mixed { true };
    std::vector<Per>         periods;
    std::vector<std::string> external;
    std::string              aperiodic;
    bool                     transient { false };
    // 去掉线性时间项后余下部分（常量、逐像素量、周期量）的取值范围；无穷 = 说不清
    double                   lo { -kInf };
    double                   hi { kInf };
    bool                     poles { false }; // 余下部分含 tan(线性时间) 这种每周期都趋于 ±∞ 的项，其余有界
    double                   settle { -1 };   // 与线性时间比较的分支在此时刻（秒）之后固定；< 0：没有

    bool operator==(const Comp&) const = default;
    bool Linear() const {
        return rate_unknown || std::any_of(rates.begin(), rates.end(), [](const Rate& r) { return r.v != 0; });
    }
    bool Timed() const { return Linear() || ! periods.empty() || ! external.empty() || ! aperiodic.empty() || settle >= 0; }
    bool Bounded() const { return std::isfinite(lo) && std::isfinite(hi); }
    Rate Value() const { return Rate { value, pi, knobs }; }
};
using Val   = std::vector<Comp>;
using State = std::map<std::uint32_t, Val>;

Comp Known(double v, int pi = 0) {
    Comp c;
    c.known = true;
    c.value = v;
    c.pi    = pi;
    c.mixed = v != 0;
    if (std::isfinite(v)) c.lo = c.hi = v;
    return c;
}

// 1/b：次数取反，旋钮翻成除数
Comp Inverse(const Comp& b) {
    Comp r = Known(1 / b.value, PiNeg(b.pi));
    for (Knob k : b.knobs) {
        k.inverse = ! k.inverse;
        r.knobs   = Union(r.knobs, { k });
    }
    return r;
}

void MergeTags(Comp& dst, const Comp& src) {
    for (const Per& p : src.periods) AddUnique(dst.periods, p);
    for (const auto& e : src.external) AddUnique(dst.external, e);
    if (dst.aperiodic.empty()) dst.aperiodic = src.aperiodic;
    dst.transient = dst.transient || src.transient;
    dst.settle    = std::max(dst.settle, src.settle);
}

void Normalize(Comp& c) {
    if (c.rates.size() > kMaxRates) {
        c.rate_unknown = true;
        c.rates        = { Rate {} };
    }
    if (c.periods.size() > kMaxPeriods && c.aperiodic.empty() && Distinct(c.periods) > kMaxPeriods)
        c.aperiodic = "too_many_periods";
}

Comp Join(const Comp& a, const Comp& b) {
    Comp r  = a;
    r.known = a.known && b.known && a.value == b.value;
    if (! r.known) {
        r.value = 0;
        r.pi    = 0;
        r.knobs.clear();
    } else {
        r.pi    = a.pi == b.pi ? a.pi : kNoPi;
        r.knobs = Common(a.knobs, b.knobs);
    }
    for (const Rate& x : b.rates) AddUnique(r.rates, x);
    r.rate_unknown = a.rate_unknown || b.rate_unknown;
    r.mixed        = a.mixed || b.mixed;
    // 范围只在两边相同时保留：循环里逐轮变宽的量一次就放弃，不动点照常收敛
    if (a.lo != b.lo || a.hi != b.hi) r.lo = -kInf, r.hi = kInf;
    r.poles = a.poles && b.poles;
    MergeTags(r, b);
    Normalize(r);
    return r;
}

Comp JoinAll(const Val& v) {
    if (v.empty()) return {};
    Comp r = v[0];
    for (std::size_t k = 1; k < v.size(); ++k) r = Join(r, v[k]);
    return r;
}

Val JoinVal(const Val& a, const Val& b) {
    if (a.size() != b.size()) return Val(std::max(a.size(), b.size()), Join(JoinAll(a), JoinAll(b)));
    Val r(a.size());
    for (std::size_t k = 0; k < a.size(); ++k) r[k] = Join(a[k], b[k]);
    return r;
}

State JoinState(const State& a, const State& b) {
    State r = a;
    for (const auto& [id, v] : b) {
        auto it = r.find(id);
        if (it == r.end())
            r.emplace(id, v);
        else
            it->second = JoinVal(it->second, v);
    }
    return r;
}

Val Fit(const Val& v, std::size_t size) {
    if (v.size() == size) return v;
    if (v.size() > size) return Val(v.begin(), v.begin() + long(size));
    return Val(size, JoinAll(v));
}

const Comp& At(const Val& v, std::size_t k) {
    static const Comp none;
    return v.empty() ? none : v[k < v.size() ? k : 0];
}

Comp Add(const Comp& a, const Comp& b, double sign) {
    Comp r;
    r.known = a.known && b.known;
    if (r.known) {
        const Rate s = Sum(a.Value(), b.Value(), sign);
        r.value      = s.v;
        r.pi         = s.pi;
        r.knobs      = s.knobs;
    }
    r.rates.clear();
    for (const Rate& x : a.rates)
        for (const Rate& y : b.rates) AddUnique(r.rates, Sum(x, y, sign));
    r.rate_unknown = a.rate_unknown || b.rate_unknown;
    r.mixed        = a.mixed || b.mixed;
    r.lo           = sign > 0 ? a.lo + b.lo : a.lo - b.hi;
    r.hi           = sign > 0 ? a.hi + b.hi : a.hi - b.lo;
    r.poles        = a.poles != b.poles && (a.poles ? b : a).Bounded();
    MergeTags(r, a);
    MergeTags(r, b);
    Normalize(r);
    return r;
}

// a·k，k 与时间无关
Comp Scale(const Comp& a, const Comp& k) {
    if (k.known && k.value == 0) return Known(0);
    Comp r = a;
    if (k.known) {
        // 只有纯时间量（如 g_Speed·g_Time）上的因子才是旋钮；(uv + t·s)·k 的 k 同时缩放纹理坐标，不记
        for (Rate& x : r.rates)
            x = Rate { x.v * k.value, PiAdd(x.pi, k.pi), r.mixed ? x.knobs : Union(x.knobs, k.knobs) };
        r.lo = a.lo * k.value, r.hi = a.hi * k.value;
        if (k.value < 0) std::swap(r.lo, r.hi);
        if (r.known) {
            r.value *= k.value;
            r.pi    = PiAdd(r.pi, k.pi);
            r.knobs = Union(r.knobs, k.knobs);
        }
    } else {
        r.known = false;
        r.value = 0;
        r.pi    = 0;
        r.knobs.clear();
        r.lo = -kInf, r.hi = kInf, r.poles = false;
        if (r.Linear()) {
            r.rate_unknown = true;
            r.rates        = { Rate {} };
        }
    }
    return r;
}

Comp Tagged(std::initializer_list<const Comp*> args, const std::string& reason) {
    Comp r;
    bool linear = false;
    for (const Comp* a : args) {
        MergeTags(r, *a);
        linear = linear || a->Linear();
    }
    if (linear && r.aperiodic.empty()) r.aperiodic = reason;
    Normalize(r);
    return r;
}

Comp Mul(const Comp& a, const Comp& b, const std::string& where) {
    if ((a.known && a.value == 0) || (b.known && b.value == 0)) return Known(0);
    if (! b.Timed()) return Scale(a, b);
    if (! a.Timed()) return Scale(b, a);
    return Tagged({ &a, &b }, "nonlinear_time" + where);
}

Comp Div(const Comp& a, const Comp& b, const std::string& where) {
    if (! b.Timed()) return b.known && b.value != 0 ? Scale(a, Inverse(b)) : Scale(a, Comp {});
    return Tagged({ &a, &b }, "nonlinear_time" + where);
}

// 周期为 q（π 次数 pi，自变量单位）的函数作用在 a 上：周期 q/|系数|，次数 pi − 系数的次数
Comp Periodic(const Comp& a, double q, int pi, const std::string& where) {
    Comp r;
    MergeTags(r, a);
    if (a.rate_unknown) {
        if (r.aperiodic.empty()) r.aperiodic = "time_rate_not_constant" + where;
    } else {
        for (const Rate& x : a.rates)
            if (x.v != 0) AddUnique(r.periods, Per { q / std::abs(x.v), PiAdd(pi, PiNeg(x.pi)), x.knobs });
    }
    Normalize(r);
    return r;
}

// 取整的余量 x − 取整(x)：周期 1/|系数| 的锯齿，取值在 [−1, 1]
Comp Saw(const Comp& a, const std::string& where) {
    Comp r = Periodic(a, 1, 0, where);
    r.lo = -1, r.hi = 1;
    return r;
}

// a 与 b 比较看差 d = a − b。d 不含线性时间：结果是周期量或常量的函数。d = r·t + q（各 r ≠ 0）：
// q ∈ [lo, hi] 时，r > 0 在 t > −lo/r 之后 d 恒正、r < 0 在 t > hi/|r| 之后恒负，比较从此固定（暂态），记 settle 上界，
// 之后照常按周期量求；q 含 tan(线性时间) 的极点、其余有界时 d 每个周期都变号且变号相位漂移，永不固定，是不周期的证明；
// q 的范围说不清（纹理、顶点属性、循环变量）只报没推下去
Comp Compare(const Comp& a, const Comp& b, const std::string& code, const std::string& where) {
    const Comp d = Add(a, b, -1);
    Comp       r;
    MergeTags(r, d);
    if (! d.Linear() || ! r.aperiodic.empty()) return r;
    const bool moving = std::none_of(d.rates.begin(), d.rates.end(), [](const Rate& x) { return x.v == 0; });
    if (d.rate_unknown)
        r.aperiodic = "time_rate_not_constant" + where;
    else if (d.poles && moving)
        r.aperiodic = "compare_with_unbounded_time: " + code + " against tan(linear time), whose poles flip it every period at a drifting phase" + where;
    else if (! d.Bounded())
        r.aperiodic = code + ": threshold range unknown" + where;
    else
        for (const Rate& x : d.rates)
            if (x.v != 0) r.settle = std::max(r.settle, std::max(0.0, (x.v > 0 ? -d.lo : -d.hi) / x.v));
    return r;
}

std::optional<double> FoldGlsl(std::uint32_t inst, const std::vector<double>& x) {
    auto at = [&](std::size_t k) { return k < x.size() ? x[k] : 0.0; };
    switch (inst) {
    case 1:
    case 2: return std::round(at(0));
    case 3: return std::trunc(at(0));
    case 4:
    case 5: return std::abs(at(0));
    case 6:
    case 7: return double((at(0) > 0) - (at(0) < 0));
    case 8: return std::floor(at(0));
    case 9: return std::ceil(at(0));
    case 10: return at(0) - std::floor(at(0));
    case 13: return std::sin(at(0));
    case 14: return std::cos(at(0));
    case 15: return std::tan(at(0));
    case 16: return std::asin(at(0));
    case 17: return std::acos(at(0));
    case 18: return std::atan(at(0));
    case 25: return std::atan2(at(0), at(1));
    case 26: return std::pow(at(0), at(1));
    case 27: return std::exp(at(0));
    case 28: return std::log(at(0));
    case 29: return std::exp2(at(0));
    case 30: return std::log2(at(0));
    case 31: return std::sqrt(at(0));
    case 32: return 1 / std::sqrt(at(0));
    case 37:
    case 38:
    case 39:
    case 79: return std::min(at(0), at(1));
    case 40:
    case 41:
    case 42:
    case 80: return std::max(at(0), at(1));
    case 43:
    case 44:
    case 45:
    case 81: return std::min(std::max(at(0), at(1)), at(2));
    case 48: return at(1) < at(0) ? 0.0 : 1.0;
    case 49: {
        const double t = std::clamp((at(2) - at(0)) / (at(1) - at(0)), 0.0, 1.0);
        return t * t * (3 - 2 * t);
    }
    default: return std::nullopt;
    }
}

std::optional<double> FoldCompare(std::uint32_t op, double x, double y) {
    switch (op) {
    case 164:
    case 170:
    case 180:
    case 181: return x == y;
    case 165:
    case 171:
    case 182:
    case 183: return x != y;
    case 172:
    case 173:
    case 186:
    case 187: return x > y;
    case 174:
    case 175:
    case 190:
    case 191: return x >= y;
    case 176:
    case 177:
    case 184:
    case 185: return x < y;
    case 178:
    case 179:
    case 188:
    case 189: return x <= y;
    default: return std::nullopt;
    }
}

struct Type {
    std::uint32_t              op { 0 };
    std::uint32_t              elem { 0 };
    std::uint32_t              count { 0 };
    std::uint32_t              storage { 0 };
    std::uint32_t              width { 32 };
    bool                       is_float { false };
    bool                       is_signed { false };
    bool                       summary { false }; // 数组过大：全部元素合成一个
    std::size_t                size { 1 };
    std::vector<std::uint32_t> members;
    std::vector<std::size_t>   offsets;
};

struct Ptr {
    std::uint32_t root { 0 };
    long          off { 0 }; // < 0：分量不定
    std::size_t   size { 1 };
    bool          weak { false };
    bool          block_root { false }; // uniform 块本身，下一个下标选成员
    std::string   uniform;              // 指向 uniform 成员时的成员名
};

struct Block {
    std::uint32_t            label { 0 };
    std::size_t              begin { 0 };
    std::size_t              end { 0 };
    std::vector<std::size_t> preds;
};

struct Function {
    std::string                                 name;
    std::vector<std::uint32_t>                  params;
    std::vector<Block>                          blocks;
    std::unordered_map<std::uint32_t, std::size_t> index;
};

struct Global {
    std::uint32_t type { 0 };
    std::uint32_t storage { 0 };
    std::uint32_t init { 0 };
};

struct Cond {
    Comp        value;
    bool        loop { false };
    std::string where;
};

struct RunResult {
    Val   ret;
    State exit;
    bool  has_exit { false };
};

std::string Str(const std::uint32_t* o, std::size_t n) {
    std::string s;
    for (std::size_t k = 0; k < n; ++k)
        for (int b = 0; b < 4; ++b) {
            const char c = char((o[k] >> (8 * b)) & 0xffu);
            if (c == 0) return s;
            s.push_back(c);
        }
    return s;
}

class Analyzer {
public:
    Analyzer(std::span<const unsigned int> words, const Inputs& inputs, std::map<std::pair<int, std::size_t>, Cond>& conds)
        : w_(words.begin(), words.end()), in_(inputs), conds_(conds) {}

    bool          Parse();
    std::uint32_t Model() const { return model_; }
    // 分析只覆盖顶点/几何/片元阶段、且靠 OpName 按名字取 uniform：看不到的情况报原因，不静默当成与时间无关
    std::string Unsupported() const {
        if ((model_ != 0 && model_ != 3 && model_ != 4) || side_effect_) return "unsupported_side_effect";
        return names_.empty() ? "names_stripped" : "";
    }
    void          Execute(int stage_index, const std::map<std::uint32_t, Val>& varyings_in,
                          std::map<std::uint32_t, Val>& varyings_out, std::vector<std::pair<Comp, std::string>>& outputs);

private:
    std::size_t Sz(std::uint32_t type) const {
        auto it = types_.find(type);
        return it == types_.end() ? 1 : it->second.size;
    }
    std::uint32_t Pointee(std::uint32_t ptr_type) const {
        auto it = types_.find(ptr_type);
        return it == types_.end() ? 0 : it->second.elem;
    }
    std::uint32_t TypeOf(std::uint32_t id) const {
        auto it = result_type_.find(id);
        return it == result_type_.end() ? 0 : it->second;
    }
    bool IsHandle(std::uint32_t type) const {
        auto it = types_.find(type);
        if (it == types_.end()) return false;
        if (it->second.op == 28 || it->second.op == 29) return IsHandle(it->second.elem);
        return it->second.op == 25 || it->second.op == 26 || it->second.op == 27;
    }
    Val V(std::uint32_t id) const {
        if (auto it = consts_.find(id); it != consts_.end()) return it->second;
        if (auto it = vals_.find(id); it != vals_.end()) return it->second;
        return Val(Sz(TypeOf(id)));
    }
    bool HasVal(std::uint32_t id) const { return consts_.count(id) || vals_.count(id); }
    void Set(std::uint32_t id, Val v) {
        auto& slot = vals_[id];
        if (! (slot == v)) {
            slot = std::move(v);
            if (changed_) *changed_ = true;
        }
    }
    std::optional<long> ConstIndex(std::uint32_t id) const {
        Val v = V(id);
        if (v.size() == 1 && v[0].known) return long(v[0].value);
        return std::nullopt;
    }
    Ptr P(std::uint32_t id) const {
        if (auto it = ptrs_.find(id); it != ptrs_.end()) return it->second;
        Ptr p;
        p.root = id;
        p.off  = -1;
        return p;
    }
    std::string W(std::uint32_t id) const {
        return " @" + stage_ + " " + fname_ + " %" + std::to_string(id);
    }
    // 旋钮所在的着色器文件后缀
    std::string Stage() const {
        return model_ == 0 ? "vert" : model_ == 4 ? "frag" : model_ == 3 ? "geom" : "stage" + std::to_string(model_);
    }

    std::uint32_t Walk(std::uint32_t type, const std::uint32_t* idx, std::size_t count, bool literal, long& off,
                       bool& weak) const;
    Val           Load(const Ptr& p, const State& S) const;
    void          Store(const Ptr& p, const Val& v, State& S) const;
    Val           Glsl(std::uint32_t inst, const std::vector<Val>& a, std::size_t size, const std::string& where) const;
    Val           Sample(const std::string& name, const Val& coord, const std::vector<Val>& extra, bool integral,
                         std::size_t size, const std::string& where) const;
    Val           GenericAll(const std::vector<Val>& a, std::size_t size, const std::string& op,
                             const std::string& where) const;
    RunResult     Run(std::uint32_t fid, const State& in, int depth);
    void          Exec(Function& f, std::size_t at, State& S, RunResult& res,
                       const std::vector<std::optional<State>>& out, int depth);

    std::vector<std::uint32_t>                                            w_;
    const Inputs&                                                         in_;
    std::map<std::pair<int, std::size_t>, Cond>&                          conds_;
    std::unordered_map<std::uint32_t, std::string>                        names_;
    bool                                                                  side_effect_ { false };
    std::set<std::uint32_t>                                               buffer_blocks_;
    std::unordered_map<std::uint32_t, std::map<std::uint32_t, std::string>> member_names_;
    std::unordered_map<std::uint32_t, std::uint32_t>                      location_;
    std::unordered_map<std::uint32_t, std::uint32_t>                      builtin_;
    std::unordered_map<std::uint32_t, std::map<std::uint32_t, std::uint32_t>> member_builtin_;
    std::unordered_map<std::uint32_t, Type>                               types_;
    std::unordered_map<std::uint32_t, Val>                                consts_;
    std::map<std::uint32_t, Global>                                       globals_;
    std::unordered_map<std::uint32_t, Function>                           functions_;
    std::set<std::uint32_t>                                               loop_merges_;
    std::unordered_map<std::uint32_t, std::uint32_t>                      result_type_;
    std::unordered_map<std::uint32_t, Val>                                vals_;
    std::unordered_map<std::uint32_t, Ptr>                                ptrs_;
    std::unordered_map<std::uint32_t, std::string>                        handles_;
    std::unordered_map<std::uint32_t, std::string>                        var_handles_;
    std::uint32_t                                                         glsl_ { 0 };
    std::uint32_t                                                         model_ { 0 };
    std::uint32_t                                                         entry_ { 0 };
    int                                                                   stage_index_ { 0 };
    std::string                                                           stage_;
    std::string                                                           fname_;
    bool*                                                                 changed_ { nullptr };
    bool                                                                  nonconverged_ { false };
};

bool Analyzer::Parse() {
    if (w_.size() < 5 || w_[0] != 0x07230203u) return false;
    Function* cur = nullptr;
    for (std::size_t i = 5; i < w_.size();) {
        const std::uint32_t len = w_[i] >> 16, op = w_[i] & 0xffffu;
        if (len == 0 || i + len > w_.size()) return false;
        const std::uint32_t* o = &w_[i + 1];
        const std::size_t    n = len - 1;
        // OpImageWrite、原子操作、存储缓冲变量（StorageBuffer，或旧写法 Uniform + BufferBlock）：有副作用的写入，抽象解释不建模
        if (op == 99 || (op >= 227 && op <= 242) ||
            (op == 59 && n > 2 && (o[2] == 12 || (o[2] == 2 && buffer_blocks_.contains(types_[o[0]].elem)))))
            side_effect_ = true;
        switch (op) {
        case 5: names_[o[0]] = Str(o + 1, n - 1); break;
        case 6: member_names_[o[0]][o[1]] = Str(o + 2, n - 2); break;
        case 11:
            if (Str(o + 1, n - 1) == "GLSL.std.450") glsl_ = o[0];
            break;
        case 15:
            if (entry_ == 0) {
                model_ = o[0];
                entry_ = o[1];
            }
            break;
        case 71:
            if (o[1] == 30 && n > 2) location_[o[0]] = o[2];
            if (o[1] == 11 && n > 2) builtin_[o[0]] = o[2];
            if (o[1] == 3) buffer_blocks_.insert(o[0]);
            break;
        case 72:
            if (o[2] == 11 && n > 3) member_builtin_[o[0]][o[1]] = o[3];
            break;
        case 19:
        case 33: types_[o[0]] = Type { .op = op, .size = 0 }; break;
        case 20:
        case 25:
        case 26:
        case 27: types_[o[0]] = Type { .op = op }; break;
        case 21: types_[o[0]] = Type { .op = op, .width = o[1], .is_signed = o[2] != 0 }; break;
        case 22: types_[o[0]] = Type { .op = op, .width = o[1], .is_float = true }; break;
        case 23:
        case 24: types_[o[0]] = Type { .op = op, .elem = o[1], .count = o[2], .size = o[2] * Sz(o[1]) }; break;
        case 28:
        case 29: {
            Type t { .op = op, .elem = o[1] };
            const long len_value = op == 28 ? ConstIndex(o[2]).value_or(0) : 0;
            t.count              = std::uint32_t(std::max(len_value, 0L));
            const std::size_t es = Sz(o[1]);
            t.summary            = op == 29 || t.count == 0 || t.count * es > kMaxComps;
            t.size               = t.summary ? es : t.count * es;
            types_[o[0]]         = t;
            break;
        }
        case 30: {
            Type t { .op = op, .size = 0 };
            for (std::size_t k = 1; k < n; ++k) {
                t.members.push_back(o[k]);
                t.offsets.push_back(t.size);
                t.size += Sz(o[k]);
            }
            types_[o[0]] = t;
            break;
        }
        case 32: types_[o[0]] = Type { .op = op, .elem = o[2], .storage = o[1] }; break;
        case 41:
        case 48:
            consts_[o[1]]      = Val { Known(1) };
            result_type_[o[1]] = o[0];
            break;
        case 42:
        case 49:
            consts_[o[1]]      = Val { Known(0) };
            result_type_[o[1]] = o[0];
            break;
        case 43:
        case 50: {
            const Type&   t    = types_[o[0]];
            std::uint64_t bits = n > 3 ? (std::uint64_t(o[3]) << 32) | o[2] : o[2];
            double        v    = 0;
            if (t.is_float)
                v = t.width == 64 ? std::bit_cast<double>(bits) : double(std::bit_cast<float>(o[2]));
            else if (t.width == 64)
                v = t.is_signed ? double(std::int64_t(bits)) : double(bits);
            else
                v = t.is_signed ? double(std::int32_t(o[2])) : double(o[2]);
            Comp c = Known(v);
            // 32 位浮点字面量是可改写的旋钮；π 字面量换成精确值并记次数
            if (t.is_float && t.width == 32 && v != 0) {
                c.knobs = { Knob { .stage = Stage(), .literal = float(v) } };
                if (auto p = PiLiteral(float(v))) {
                    c.value = p->first;
                    c.pi    = p->second;
                }
            }
            consts_[o[1]]      = Val { c };
            result_type_[o[1]] = o[0];
            break;
        }
        case 44:
        case 51: {
            Val v;
            for (std::size_t k = 2; k < n; ++k) {
                Val part = V(o[k]);
                v.insert(v.end(), part.begin(), part.end());
            }
            consts_[o[1]]      = Fit(v, Sz(o[0]));
            result_type_[o[1]] = o[0];
            break;
        }
        case 46:
            consts_[o[1]]      = Val(Sz(o[0]), Known(0));
            result_type_[o[1]] = o[0];
            break;
        case 1:
        case 52:
            if (! cur) {
                consts_[o[1]]      = Val(Sz(o[0]));
                result_type_[o[1]] = o[0];
            }
            break;
        case 59:
            if (! cur) {
                globals_[o[1]]     = Global { o[0], o[2], n > 3 ? o[3] : 0 };
                result_type_[o[1]] = o[0];
            }
            break;
        case 54:
            cur       = &functions_[o[1]];
            cur->name = names_.count(o[1]) ? names_[o[1]].substr(0, names_[o[1]].find('(')) : "%" + std::to_string(o[1]);
            break;
        case 55:
            if (cur) cur->params.push_back(o[1]);
            result_type_[o[1]] = o[0];
            break;
        case 56: cur = nullptr; break;
        case 246: loop_merges_.insert(o[0]); break;
        case 248:
            if (cur) cur->blocks.push_back(Block { .label = o[0], .begin = i, .end = i + len });
            break;
        default: break;
        }
        if (cur && ! cur->blocks.empty()) cur->blocks.back().end = i + len;
        i += len;
    }
    for (auto& [id, f] : functions_) {
        for (std::size_t b = 0; b < f.blocks.size(); ++b) f.index[f.blocks[b].label] = b;
        for (std::size_t b = 0; b < f.blocks.size(); ++b) {
            std::size_t last = f.blocks[b].begin;
            for (std::size_t i = last; i < f.blocks[b].end; i += std::max<std::uint32_t>(w_[i] >> 16, 1)) {
                const std::uint32_t op = w_[i] & 0xffffu;
                if (op != 56) last = i;
            }
            const std::uint32_t        op = w_[last] & 0xffffu;
            const std::uint32_t*       o  = &w_[last + 1];
            const std::size_t          n  = (w_[last] >> 16) - 1;
            std::vector<std::uint32_t> succ;
            if (op == 249) succ = { o[0] };
            if (op == 250) succ = { o[1], o[2] };
            if (op == 251) {
                succ.push_back(o[1]);
                for (std::size_t k = 3; k < n; k += 2) succ.push_back(o[k]);
            }
            for (std::uint32_t s : succ)
                if (auto it = f.index.find(s); it != f.index.end()) f.blocks[it->second].preds.push_back(b);
        }
    }
    return entry_ != 0 && functions_.count(entry_);
}

std::uint32_t Analyzer::Walk(std::uint32_t type, const std::uint32_t* idx, std::size_t count, bool literal, long& off,
                             bool& weak) const {
    for (std::size_t j = 0; j < count; ++j) {
        auto it = types_.find(type);
        if (it == types_.end()) {
            off = -1;
            return 0;
        }
        const Type&         t  = it->second;
        std::optional<long> ci = literal ? std::optional<long>(long(idx[j])) : ConstIndex(idx[j]);
        if (t.op == 30) {
            if (! ci || *ci < 0 || std::size_t(*ci) >= t.members.size()) {
                off = -1;
                return 0;
            }
            if (off >= 0) off += long(t.offsets[std::size_t(*ci)]);
            type = t.members[std::size_t(*ci)];
        } else if (t.op == 23 || t.op == 24 || t.op == 28 || t.op == 29) {
            if (t.summary)
                weak = true;
            else if (ci && off >= 0)
                off += *ci * long(Sz(t.elem));
            else
                off = -1;
            type = t.elem;
        } else {
            off = -1;
            return type;
        }
    }
    return type;
}

Val Analyzer::Load(const Ptr& p, const State& S) const {
    if (! p.uniform.empty()) {
        Val               r(p.size);
        const std::string& n = p.uniform;
        if (n == "g_Time") {
            for (auto& c : r) {
                c.rates = { Rate { 1.0 } };
                c.mixed = false;
                c.lo = c.hi = 0;
            }
            return r;
        }
        const bool dynamic = n.starts_with("g_AudioSpectrum") || n.starts_with("g_Pointer") ||
                             n == "g_ParallaxPosition" || n == "g_Daytime";
        UniformValue u = dynamic ? UniformValue { UniformValue::Kind::External, {} }
                                 : (in_.uniform ? in_.uniform(n) : UniformValue {});
        if (u.kind == UniformValue::Kind::External)
            for (auto& c : r) c.external = { n };
        else if (u.kind == UniformValue::Kind::Constant && p.off >= 0 && ! p.weak)
            for (std::size_t k = 0; k < p.size; ++k)
                if (std::size_t(p.off) + k < u.values.size()) {
                    r[k]       = Known(u.values[std::size_t(p.off) + k]);
                    r[k].knobs = { Knob { .stage = Stage(), .uniform = n } };
                }
        return r;
    }
    auto it = S.find(p.root);
    if (it == S.end()) return Val(p.size);
    const Val& v = it->second;
    if (p.off < 0 || std::size_t(p.off) + p.size > v.size()) return Val(p.size, JoinAll(v));
    return Val(v.begin() + p.off, v.begin() + p.off + long(p.size));
}

void Analyzer::Store(const Ptr& p, const Val& v, State& S) const {
    if (! p.uniform.empty() || p.block_root) return;
    auto& slot = S[p.root];
    if (slot.empty()) slot = Val(Sz(Pointee(TypeOf(p.root))));
    if (p.off < 0 || std::size_t(p.off) + p.size > slot.size()) {
        const Comp j = JoinAll(v);
        for (auto& c : slot) c = Join(c, j);
        return;
    }
    for (std::size_t k = 0; k < p.size && k < v.size(); ++k) {
        Comp& c = slot[std::size_t(p.off) + k];
        c       = p.weak ? Join(c, v[k]) : v[k];
    }
}

Val Analyzer::GenericAll(const std::vector<Val>& a, std::size_t size, const std::string& op,
                         const std::string& where) const {
    Comp r;
    bool linear = false;
    for (const Val& v : a)
        for (const Comp& c : v) {
            MergeTags(r, c);
            linear = linear || c.Linear();
        }
    if (linear && r.aperiodic.empty()) r.aperiodic = op + where;
    Normalize(r);
    return Val(size, r);
}

Val Analyzer::Glsl(std::uint32_t inst, const std::vector<Val>& a, std::size_t size, const std::string& where) const {
    auto arg = [&](std::size_t j, std::size_t k) -> const Comp& {
        static const Comp none;
        return j < a.size() ? At(a[j], k) : none;
    };
    switch (inst) {
    case 33:
    case 34:
    case 66:
    case 67:
    case 68:
    case 69:
    case 70:
    case 71:
    case 72: return GenericAll(a, size, "linear_time_through_glsl" + std::to_string(inst), where);
    default: break;
    }
    Val r(size);
    for (std::size_t k = 0; k < size; ++k) {
        const Comp& x = arg(0, k);
        switch (inst) {
        case 1:
        case 2:
        case 3:
        case 8:
        case 9:
            // Round、RoundEven、Trunc、Floor、Ceil：线性时间取整是阶梯 = 线性 + 周期 1/|系数| 的锯齿（再取模即周期）
            r[k] = x.known ? Known(*FoldGlsl(inst, { x.value })) : Add(x, Saw(x, where), -1);
            continue;
        case 10:
        case 13:
        case 14:
        case 15:
            r[k] = x.known ? Known(*FoldGlsl(inst, { x.value }), x.pi == 0 ? 0 : kNoPi)
                           : Periodic(x, inst == 10 ? 1.0 : inst == 15 ? kPi : 2 * kPi, inst == 10 ? 0 : 1, where);
            // fract ∈ [0, 1]、sin/cos ∈ [−1, 1]；tan 的自变量是各系数都非零的线性时间、余下有界时，每个周期都经过极点
            if (x.known) continue;
            if (inst != 15)
                r[k].lo = inst == 10 ? 0 : -1, r[k].hi = 1;
            else
                r[k].poles = ! x.rate_unknown && x.Bounded() &&
                             std::none_of(x.rates.begin(), x.rates.end(), [](const Rate& y) { return y.v == 0; });
            continue;
        case 11:
        case 12: r[k] = Scale(x, inst == 11 ? Known(kPi / 180, 1) : Known(180 / kPi, -1)); continue;
        case 50: r[k] = Add(Mul(x, arg(1, k), where), arg(2, k), 1); continue;
        case 46: {
            const Comp& t = arg(2, k);
            if (! t.Timed())
                r[k] = Add(Mul(x, Add(Known(1), t, -1), where), Mul(arg(1, k), t, where), 1);
            else
                r[k] = Tagged({ &x, &arg(1, k), &t }, "linear_time_through_mix" + where);
            continue;
        }
        default: break;
        }
        std::vector<double>       known;
        std::vector<const Comp*>  args;
        bool                      all_known = true, rational = true;
        for (std::size_t j = 0; j < a.size(); ++j) {
            args.push_back(&arg(j, k));
            all_known = all_known && arg(j, k).known;
            rational  = rational && arg(j, k).pi == 0;
            known.push_back(arg(j, k).value);
        }
        if (all_known)
            if (auto v = FoldGlsl(inst, known); v && std::isfinite(*v)) {
                r[k] = Known(*v, rational ? 0 : kNoPi);
                continue;
            }
        Comp c;
        bool linear = false;
        for (const Comp* p : args) {
            MergeTags(c, *p);
            linear = linear || p->Linear();
        }
        if (linear && c.aperiodic.empty()) c.aperiodic = "linear_time_through_glsl" + std::to_string(inst) + where;
        Normalize(c);
        r[k] = c;
    }
    return r;
}

Val Analyzer::Sample(const std::string& name, const Val& coord, const std::vector<Val>& extra, bool integral,
                     std::size_t size, const std::string& where) const {
    Comp r;
    for (const Val& e : extra)
        for (const Comp& c : e) {
            MergeTags(r, c);
            if (c.Linear() && r.aperiodic.empty()) r.aperiodic = "linear_time_through_sample_operand" + where;
        }
    std::array<Wrap, 2> wrap { Wrap::Unknown, Wrap::Unknown };
    if (in_.wrap) wrap = in_.wrap(name);
    for (std::size_t k = 0; k < coord.size(); ++k) {
        const Comp& c = coord[k];
        MergeTags(r, c);
        if (! c.Linear() || ! r.aperiodic.empty()) continue;
        if (integral || k >= 2)
            r.aperiodic = "linear_time_through_sample_coordinate:" + name + where;
        else if (c.rate_unknown)
            r.aperiodic = "scroll_rate_not_constant:" + name + where;
        else if (wrap[k] == Wrap::Repeat) {
            for (const Rate& x : c.rates)
                if (x.v != 0) AddUnique(r.periods, Per { 1 / std::abs(x.v), PiNeg(x.pi), x.knobs });
        } else if (wrap[k] == Wrap::Clamp)
            r.transient = true;
        else
            r.aperiodic = "sampler_wrap_unknown:" + name + where;
    }
    Normalize(r);
    return Val(size, r);
}

RunResult Analyzer::Run(std::uint32_t fid, const State& in, int depth) {
    RunResult res;
    auto      fit = functions_.find(fid);
    if (fit == functions_.end() || depth > 32) return res;
    Function&                         f = fit->second;
    std::vector<std::optional<State>> out(f.blocks.size());
    bool* const                       saved_changed = changed_;
    const std::string                 saved_name    = fname_;
    fname_                                          = f.name;
    for (std::size_t pass = 0;; ++pass) {
        bool changed = false;
        changed_     = &changed;
        res          = RunResult {};
        for (std::size_t b = 0; b < f.blocks.size(); ++b) {
            std::optional<State> S;
            if (b == 0) S = in;
            for (std::size_t p : f.blocks[b].preds)
                if (out[p]) S = S ? JoinState(*S, *out[p]) : *out[p];
            if (! S) continue;
            for (std::size_t i = f.blocks[b].begin; i < f.blocks[b].end; i += std::max<std::uint32_t>(w_[i] >> 16, 1))
                Exec(f, i, *S, res, out, depth);
            if (! out[b] || ! (*out[b] == *S)) {
                out[b]  = std::move(S);
                changed = true;
            }
        }
        if (! changed) break;
        if (pass + 1 >= kMaxPasses) {
            nonconverged_ = true;
            break;
        }
    }
    changed_ = saved_changed;
    fname_   = saved_name;
    return res;
}

void Analyzer::Exec(Function& f, std::size_t at, State& S, RunResult& res, const std::vector<std::optional<State>>& out,
                    int depth) {
    const std::uint32_t  op = w_[at] & 0xffffu;
    const std::uint32_t* o  = &w_[at + 1];
    const std::size_t    n  = (w_[at] >> 16) - 1;
    const bool           has_result = n >= 2 && types_.count(o[0]) && op != 248;
    if (has_result) result_type_[o[1]] = o[0];
    const std::uint32_t T  = has_result ? o[0] : 0;
    const std::uint32_t R  = has_result ? o[1] : 0;
    const std::size_t   sz = Sz(T);
    auto                map1 = [&](const Val& a, auto&& fn) {
        Val r(sz);
        for (std::size_t k = 0; k < sz; ++k) r[k] = fn(At(a, k));
        return r;
    };
    auto map2 = [&](const Val& a, const Val& b, auto&& fn) {
        Val r(sz);
        for (std::size_t k = 0; k < sz; ++k) r[k] = fn(At(a, k), At(b, k));
        return r;
    };
    auto fold_generic = [&](const std::string& name, auto&& fold) {
        std::vector<Val> a;
        for (std::size_t k = 2; k < n; ++k) a.push_back(V(o[k]));
        Val r(sz);
        for (std::size_t k = 0; k < sz; ++k) {
            bool                all_known = true;
            std::vector<double> x;
            Comp                c;
            bool                linear = false;
            for (const Val& v : a) {
                const Comp& ck = At(v, k);
                all_known      = all_known && ck.known;
                x.push_back(ck.value);
                MergeTags(c, ck);
                linear = linear || ck.Linear();
            }
            if (all_known)
                if (std::optional<double> v = fold(x); v && std::isfinite(*v)) {
                    r[k] = Known(*v);
                    continue;
                }
            if (linear && c.aperiodic.empty()) c.aperiodic = "linear_time_through_" + name + W(R);
            Normalize(c);
            r[k] = c;
        }
        Set(R, r);
    };
    switch (op) {
    case 1: Set(R, Val(sz)); break;
    case 59: {
        Ptr p;
        p.root   = R;
        p.size   = Sz(Pointee(T));
        ptrs_[R] = p;
        S[R]     = n > 3 ? Fit(V(o[3]), p.size) : Val(p.size);
        break;
    }
    case 61: {
        const Ptr p = P(o[2]);
        if (IsHandle(T)) {
            auto h      = var_handles_.find(p.root);
            handles_[R] = h != var_handles_.end() ? h->second : names_[p.root];
            break;
        }
        Set(R, Load(p, S));
        break;
    }
    case 62:
        if (auto h = handles_.find(o[1]); h != handles_.end())
            var_handles_[P(o[0]).root] = h->second;
        else
            Store(P(o[0]), V(o[1]), S);
        break;
    case 63: Store(P(o[0]), Load(P(o[1]), S), S); break;
    case 65:
    case 66: {
        Ptr                  p    = P(o[2]);
        std::uint32_t        type = Pointee(TypeOf(o[2]));
        const std::uint32_t* idx  = o + 3;
        std::size_t          cnt  = n - 3;
        if (p.block_root && cnt > 0) {
            const std::optional<long> ci = ConstIndex(idx[0]);
            const Type&               t  = types_[type];
            std::string               member;
            if (ci && member_names_.count(type) && member_names_[type].count(std::uint32_t(*ci)))
                member = member_names_[type][std::uint32_t(*ci)];
            p.block_root = false;
            p.uniform    = member.empty() ? "?" : member;
            p.off        = 0;
            type         = ci && std::size_t(*ci) < t.members.size() ? t.members[std::size_t(*ci)] : 0;
            ++idx;
            --cnt;
        }
        type     = Walk(type, idx, cnt, false, p.off, p.weak);
        p.size   = Sz(type);
        ptrs_[R] = p;
        break;
    }
    case 77: {
        const Val                 a  = V(o[2]);
        const std::optional<long> ci = ConstIndex(o[3]);
        Set(R, Val(sz, ci && *ci >= 0 && std::size_t(*ci) < a.size() ? a[std::size_t(*ci)] : JoinAll(a)));
        break;
    }
    case 78: {
        Val                       a  = V(o[2]);
        const Comp                x  = At(V(o[3]), 0);
        const std::optional<long> ci = ConstIndex(o[4]);
        if (ci && *ci >= 0 && std::size_t(*ci) < a.size())
            a[std::size_t(*ci)] = x;
        else
            for (auto& c : a) c = Join(c, x);
        Set(R, Fit(a, sz));
        break;
    }
    case 79: {
        Val a = V(o[2]), b = V(o[3]);
        a.insert(a.end(), b.begin(), b.end());
        Val r;
        for (std::size_t k = 4; k < n; ++k) r.push_back(o[k] < a.size() ? a[o[k]] : Comp {});
        Set(R, Fit(r, sz));
        break;
    }
    case 80: {
        Val r;
        for (std::size_t k = 2; k < n; ++k) {
            Val part = V(o[k]);
            r.insert(r.end(), part.begin(), part.end());
        }
        Set(R, Fit(r, sz));
        break;
    }
    case 81: {
        const Val a    = V(o[2]);
        long      off  = 0;
        bool      weak = false;
        const std::uint32_t type = Walk(TypeOf(o[2]), o + 3, n - 3, true, off, weak);
        const std::size_t   size = Sz(type);
        if (off < 0 || std::size_t(off) + size > a.size())
            Set(R, Val(sz, JoinAll(a)));
        else
            Set(R, Fit(Val(a.begin() + off, a.begin() + off + long(size)), sz));
        break;
    }
    case 82: {
        Val       a    = V(o[3]);
        const Val x    = V(o[2]);
        long      off  = 0;
        bool      weak = false;
        const std::uint32_t type = Walk(TypeOf(o[3]), o + 4, n - 4, true, off, weak);
        const std::size_t   size = Sz(type);
        if (off < 0 || std::size_t(off) + size > a.size()) {
            const Comp j = JoinAll(x);
            for (auto& c : a) c = Join(c, j);
        } else
            for (std::size_t k = 0; k < size; ++k) a[std::size_t(off) + k] = weak ? Join(a[std::size_t(off) + k], At(x, k)) : At(x, k);
        Set(R, Fit(a, sz));
        break;
    }
    case 83:
    case 400:
        if (auto h = handles_.find(o[2]); h != handles_.end())
            handles_[R] = h->second;
        else if (auto p = ptrs_.find(o[2]); p != ptrs_.end())
            ptrs_[R] = p->second;
        else
            Set(R, V(o[2]));
        break;
    case 84: {
        const Val     a    = V(o[2]);
        const Type&   t    = types_[T];
        const std::size_t cols = t.count, rows = cols ? sz / cols : 0;
        Val r(sz);
        for (std::size_t c = 0; c < cols; ++c)
            for (std::size_t i = 0; i < rows; ++i) r[c * rows + i] = At(a, i * cols + c);
        Set(R, r);
        break;
    }
    case 86:
    case 100:
        if (auto h = handles_.find(o[2]); h != handles_.end()) handles_[R] = h->second;
        break;
    case 87:
    case 88:
    case 89:
    case 90:
    case 91:
    case 92:
    case 93:
    case 94:
    case 95:
    case 96:
    case 97: {
        const std::string name  = handles_.count(o[2]) ? handles_[o[2]] : std::string("?");
        std::vector<Val>  extra;
        std::size_t       first = 5;
        if (op == 89 || op == 90 || op == 93 || op == 94 || op == 96 || op == 97) {
            if (n > 4) extra.push_back(V(o[4]));
            first = 6;
        }
        for (std::size_t k = first; k < n; ++k) extra.push_back(V(o[k]));
        Set(R, Sample(name, V(o[3]), extra, (op >= 91 && op <= 94) || op == 95, sz, W(R)));
        break;
    }
    case 109:
    case 110:
        // 取整：线性时间取整是阶梯 = 线性 − 周期 1/|系数| 的锯齿；再取模（精灵帧索引）就按周期算
        Set(R, map1(V(o[2]), [&](const Comp& a) { return a.known ? Known(std::trunc(a.value)) : Add(a, Saw(a, W(R)), -1); }));
        break;
    case 111:
    case 112:
    case 113:
    case 114:
    case 115: Set(R, Fit(V(o[2]), sz)); break;
    case 126:
    case 127: Set(R, map1(V(o[2]), [&](const Comp& a) { return Scale(a, Known(-1)); })); break;
    case 128:
    case 129: Set(R, map2(V(o[2]), V(o[3]), [&](const Comp& a, const Comp& b) { return Add(a, b, 1); })); break;
    case 130:
    case 131: Set(R, map2(V(o[2]), V(o[3]), [&](const Comp& a, const Comp& b) { return Add(a, b, -1); })); break;
    case 132:
    case 133:
    case 142:
    case 143:
        Set(R, map2(V(o[2]), V(o[3]), [&](const Comp& a, const Comp& b) { return Mul(a, b, W(R)); }));
        break;
    case 136: Set(R, map2(V(o[2]), V(o[3]), [&](const Comp& a, const Comp& b) { return Div(a, b, W(R)); })); break;
    case 134:
    case 135:
        // 整数除法：商取整，同 OpConvertFToS 的阶梯
        Set(R, map2(V(o[2]), V(o[3]), [&](const Comp& a, const Comp& b) {
                if (a.known && b.known && b.value != 0) return Known(std::trunc(a.value / b.value));
                const Comp q = Div(a, b, W(R));
                return Add(q, Saw(q, W(R)), -1);
            }));
        break;
    case 137:
    case 138:
    case 139:
    case 140:
    case 141:
        // 整数与浮点取模：模数与时间无关时按周期 |m|/系数；SMod、FMod 余数随除数符号
        Set(R, map2(V(o[2]), V(o[3]), [&](const Comp& a, const Comp& m) {
                if (a.known && m.known && m.value != 0)
                    return Known(op == 139 || op == 141 ? a.value - m.value * std::floor(a.value / m.value) : std::fmod(a.value, m.value));
                if (m.known && m.value != 0) return Periodic(a, std::abs(m.value), m.pi, W(R));
                return Tagged({ &a, &m }, "linear_time_through_mod" + W(R));
            }));
        break;
    case 144:
    case 145:
    case 146:
    case 147:
    case 148: {
        const Val a = V(o[2]), b = V(o[3]);
        Val       r(sz, Known(0));
        auto      mac = [&](Comp& acc, const Comp& x, const Comp& y) { acc = Add(acc, Mul(x, y, W(R)), 1); };
        if (op == 148)
            for (std::size_t k = 0; k < a.size(); ++k) mac(r[0], a[k], At(b, k));
        else if (op == 145) { // M·v：M 为 C 列 × R 行
            const std::size_t rows = sz, cols = b.size();
            for (std::size_t i = 0; i < rows; ++i)
                for (std::size_t j = 0; j < cols; ++j) mac(r[i], At(a, j * rows + i), b[j]);
        } else if (op == 144) { // v·M
            const std::size_t rows = a.size(), cols = sz;
            for (std::size_t j = 0; j < cols; ++j)
                for (std::size_t i = 0; i < rows; ++i) mac(r[j], a[i], At(b, j * rows + i));
        } else if (op == 146) {
            const std::size_t cols = std::max<std::size_t>(types_[T].count, 1), rows = sz / cols,
                              inner = rows ? a.size() / rows : 0;
            for (std::size_t c = 0; c < cols; ++c)
                for (std::size_t i = 0; i < rows; ++i)
                    for (std::size_t k = 0; k < inner; ++k) mac(r[c * rows + i], At(a, k * rows + i), At(b, c * inner + k));
        } else { // 外积
            const std::size_t rows = a.size();
            for (std::size_t c = 0; rows && c < sz / rows; ++c)
                for (std::size_t i = 0; i < rows; ++i) r[c * rows + i] = Mul(a[i], At(b, c), W(R));
        }
        Set(R, r);
        break;
    }
    case 164:
    case 165:
    case 170:
    case 171:
    case 172:
    case 173:
    case 174:
    case 175:
    case 176:
    case 177:
    case 178:
    case 179:
    case 180:
    case 181:
    case 182:
    case 183:
    case 184:
    case 185:
    case 186:
    case 187:
    case 188:
    case 189:
    case 190:
    case 191:
        Set(R, map2(V(o[2]), V(o[3]), [&](const Comp& a, const Comp& b) {
                if (a.known && b.known)
                    if (auto v = FoldCompare(op, a.value, b.value)) return Known(*v);
                return Compare(a, b, "compare_with_linear_time", W(R));
            }));
        break;
    case 166:
    case 167:
        fold_generic("logic", [op](const std::vector<double>& x) -> std::optional<double> {
            return op == 166 ? double((x[0] != 0) || (x[1] != 0)) : double((x[0] != 0) && (x[1] != 0));
        });
        break;
    case 168:
        fold_generic("not", [](const std::vector<double>& x) -> std::optional<double> { return double(x[0] == 0); });
        break;
    case 169: {
        const Val c = V(o[2]), x = V(o[3]), y = V(o[4]);
        Val       r(sz);
        for (std::size_t k = 0; k < sz; ++k) {
            const Comp& ck = At(c, k);
            if (ck.known)
                r[k] = ck.value != 0 ? At(x, k) : At(y, k);
            else if (! ck.Timed())
                r[k] = Join(At(x, k), At(y, k));
            else
                r[k] = Tagged({ &ck, &At(x, k), &At(y, k) }, "linear_time_through_select" + W(R));
        }
        Set(R, r);
        break;
    }
    case 207:
    case 208:
    case 209:
    case 210:
    case 211:
    case 212:
    case 213:
    case 214:
    case 215:
        // 屏幕空间导数：各像素同一系数时 t 项求导消失，否则剩下系数不定的线性项
        Set(R, map1(V(o[2]), [](const Comp& x) {
                if (x.known) return Known(0);
                Comp r  = x;
                r.known = false;
                r.value = 0;
                r.lo = -kInf, r.hi = kInf, r.poles = false;
                if (! x.rate_unknown && x.rates.size() <= 1)
                    r.rates = { Rate {} };
                else if (x.Linear()) {
                    r.rate_unknown = true;
                    r.rates        = { Rate {} };
                }
                return r;
            }));
        break;
    case 245: {
        Val  acc;
        bool any = false;
        for (std::size_t k = 2; k + 1 < n; k += 2) {
            auto bi = f.index.find(o[k + 1]);
            if (bi == f.index.end() || ! out[bi->second] || ! HasVal(o[k])) continue;
            const Val v = V(o[k]);
            acc         = any ? JoinVal(acc, v) : v;
            any         = true;
        }
        Set(R, any ? Fit(acc, sz) : Val(sz));
        break;
    }
    case 12: {
        std::vector<Val> a;
        for (std::size_t k = 4; k < n; ++k) a.push_back(V(o[k]));
        Set(R, o[2] == glsl_ ? Glsl(o[3], a, sz, W(R)) : GenericAll(a, sz, "analysis_not_converged:extinst", W(R)));
        break;
    }
    case 57: {
        auto fit = functions_.find(o[2]);
        if (fit == functions_.end()) {
            Set(R, Val(sz));
            break;
        }
        const Function& callee = fit->second;
        for (std::size_t k = 0; k + 3 < n && k < callee.params.size(); ++k) {
            const std::uint32_t arg = o[3 + k], par = callee.params[k];
            if (auto p = ptrs_.find(arg); p != ptrs_.end())
                ptrs_[par] = p->second;
            else if (auto h = handles_.find(arg); h != handles_.end())
                handles_[par] = h->second;
            else
                vals_[par] = V(arg);
        }
        RunResult rr = Run(o[2], S, depth + 1);
        if (rr.has_exit) S = std::move(rr.exit);
        Set(R, rr.ret.empty() ? Val(sz) : Fit(rr.ret, sz));
        break;
    }
    case 250:
    case 251: {
        Comp v = JoinAll(V(o[0]));
        // switch 按线性时间选分支：逐个与 case 值（常量）比较
        if (op == 251 && v.Linear() && n > 3) {
            Comp s = Compare(v, Known(double(std::int32_t(o[2]))), "branch_on_linear_time", W(o[0]));
            for (std::size_t k = 4; k + 1 < n; k += 2)
                s = Join(s, Compare(v, Known(double(std::int32_t(o[k]))), "branch_on_linear_time", W(o[0])));
            v = s;
        }
        Cond c { .value = std::move(v),
                 .loop  = op == 250 && (loop_merges_.count(o[1]) || loop_merges_.count(o[2])),
                 .where = W(o[0]) };
        auto key = std::make_pair(stage_index_, at);
        auto it  = conds_.find(key);
        if (it == conds_.end())
            conds_.emplace(key, std::move(c));
        else
            it->second.value = Join(it->second.value, c.value);
        break;
    }
    case 253:
    case 254:
        if (op == 254) {
            const Val v = V(o[0]);
            res.ret     = res.ret.empty() ? v : JoinVal(res.ret, v);
        }
        res.exit     = res.has_exit ? JoinState(res.exit, S) : S;
        res.has_exit = true;
        break;
    default:
        if (has_result && ! IsHandle(T)) {
            // 没单独建模的指令：与时间无关的输入给未知常量，带时间的输入报没推下去（C# 记未收敛，不当"不能"的证明）
            std::vector<Val> a;
            for (std::size_t k = 2; k < n; ++k)
                if (HasVal(o[k])) a.push_back(V(o[k]));
            Set(R, GenericAll(a, sz, "analysis_not_converged:op" + std::to_string(op), W(R)));
        }
        break;
    }
}

void Analyzer::Execute(int stage_index, const std::map<std::uint32_t, Val>& varyings_in,
                       std::map<std::uint32_t, Val>& varyings_out, std::vector<std::pair<Comp, std::string>>& outputs) {
    stage_index_ = stage_index;
    stage_       = model_ == 0 ? "vertex" : model_ == 3 ? "geometry" : model_ == 4 ? "fragment" : "stage" + std::to_string(model_);
    State S;
    for (const auto& [var, g] : globals_) {
        const std::uint32_t pointee = Pointee(g.type);
        Ptr                 p;
        p.root = var;
        p.size = Sz(pointee);
        if (g.storage == 0 || g.storage == 2 || g.storage == 9 || g.storage == 12) {
            if (! IsHandle(pointee)) {
                if (types_[pointee].op == 30)
                    p.block_root = true;
                else
                    p.uniform = names_[var];
            }
        } else if (g.storage == 1) {
            if (auto loc = location_.find(var); loc != location_.end()) {
                auto v = varyings_in.find(loc->second);
                S[var] = v != varyings_in.end() ? Fit(v->second, p.size) : Val(p.size);
            }
        } else {
            S[var] = g.init ? Fit(V(g.init), p.size) : Val(p.size);
        }
        ptrs_[var] = p;
    }
    RunResult   rr = Run(entry_, S, 0);
    const State E  = rr.has_exit ? rr.exit : S;
    for (const auto& [var, g] : globals_) {
        if (g.storage != 3) continue;
        auto it = E.find(var);
        if (it == E.end()) continue;
        const Val&         v    = it->second;
        const std::string  name = stage_ + " output " + (names_.count(var) ? names_[var] : "%" + std::to_string(var));
        if (model_ == 4) {
            for (const auto& c : v) outputs.emplace_back(c, name);
            continue;
        }
        if (auto b = builtin_.find(var); b != builtin_.end()) {
            if (b->second == 0)
                for (const auto& c : v) outputs.emplace_back(c, stage_ + " gl_Position");
            continue;
        }
        const std::uint32_t pointee = Pointee(g.type);
        if (auto mb = member_builtin_.find(pointee); mb != member_builtin_.end()) {
            const Type& t = types_[pointee];
            for (const auto& [m, bi] : mb->second)
                if (bi == 0 && m < t.members.size())
                    for (std::size_t k = 0; k < Sz(t.members[m]) && t.offsets[m] + k < v.size(); ++k)
                        outputs.emplace_back(v[t.offsets[m] + k], stage_ + " gl_Position");
            continue;
        }
        if (auto loc = location_.find(var); loc != location_.end()) {
            // 插值：各顶点系数不同，像素上的系数就不再是常量
            Val iv = v;
            for (std::size_t k = 0; model_ == 0 && names_.count(var) && k < iv.size(); ++k) {
                // HLSL 入口的输出名形如 @entryPointOutput.v_X：取源码里的 varying 名
                const Knobs axis { Knob { .stage = "vert", .varying = names_[var].substr(names_[var].rfind('.') + 1), .component = int(k) } };
                for (Rate& r : iv[k].rates) r.knobs = Union(r.knobs, axis);
                for (Per& p : iv[k].periods) p.knobs = Union(p.knobs, axis);
            }
            for (auto& c : iv)
                if (c.rates.size() > 1) {
                    c.rate_unknown = true;
                    c.rates        = { Rate {} };
                }
            varyings_out[loc->second] = iv;
        }
    }
    if (nonconverged_) {
        Comp c;
        c.aperiodic = "analysis_not_converged @" + stage_;
        outputs.emplace_back(c, stage_);
    }
}

struct Rat {
    long long p { 0 };
    long long q { 1 };
};

std::optional<Rat> Rationalize(double x) {
    long long h0 = 0, h1 = 1, k0 = 1, k1 = 0;
    double    v  = x;
    for (int it = 0; it < 40; ++it) {
        const double a = std::floor(v);
        if (a > 1e12) break;
        const long long ai = (long long)a;
        const long long h2 = ai * h1 + h0, k2 = ai * k1 + k0;
        if (k2 > 1000000 || h2 > 1000000000000LL) break;
        h0 = h1;
        h1 = h2;
        k0 = k1;
        k1 = k2;
        if (std::abs(x - double(h1) / double(k1)) <= 1e-6 * x) return Rat { h1, k1 };
        const double frac = v - a;
        if (frac < 1e-12) break;
        v = 1 / frac;
    }
    return std::nullopt;
}

// 周期按可公度类归并。类按来源的 π 次数定（函数周期的次数 − 系数的次数，π 字面量计入系数）。
// 类内以最短周期为基准，其余周期与它的比值化成有理数（分母 ≤ 10⁶）即可公度，组周期 = 基准 × 各比值的 LCM；
// 化不成的另起一组（周期比无理，交给调速）。次数不是 0/1（含未知）的类照样归并，num 记 0。
std::vector<Period> Classes(const std::vector<Per>& periods) {
    std::map<int, std::vector<double>> by_class;
    for (const Per& per : periods)
        if (per.s > 0 && std::isfinite(per.s)) by_class[per.pi].push_back(per.s);
    std::vector<Period> out;
    for (auto& [c, list] : by_class) {
        std::sort(list.begin(), list.end());
        struct Group {
            double    base;
            long long lcm { 1 };
            long long gcd { 0 };
            bool      overflow { false };
        };
        std::vector<Group> groups;
        for (double p : list) {
            bool merged = false;
            for (Group& g : groups) {
                const auto r = Rationalize(p / g.base);
                if (! r) continue;
                const __int128 l = (__int128)(g.lcm / std::gcd(g.lcm, r->p)) * r->p;
                g.overflow       = g.overflow || l > (__int128)1000000000000000LL;
                if (! g.overflow) g.lcm = (long long)l;
                g.gcd  = std::gcd(g.gcd, r->q);
                merged = true;
                break;
            }
            if (! merged) groups.push_back(Group { .base = p, .gcd = 1 });
        }
        for (const Group& g : groups) {
            const double seconds = g.overflow ? 1e300 : g.base * double(g.lcm) / double(g.gcd);
            const auto   r = g.overflow || (c != 0 && c != 1) ? std::nullopt : Rationalize(c ? seconds / kPi : seconds);
            out.push_back(Period { .seconds = seconds, .num = r ? r->p : 0, .den = r ? r->q : 0, .pi = c != 0 });
        }
    }
    return out;
}

std::string Quote(const std::string& s) {
    std::string r = "\"";
    for (char c : s) {
        if (c == '"' || c == '\\') r.push_back('\\');
        if (static_cast<unsigned char>(c) >= 0x20) r.push_back(c);
    }
    return r + "\"";
}

} // namespace

Signature Analyze(std::span<const std::vector<unsigned int>> stages, const Inputs& inputs) {
    Signature                                   sig;
    std::map<std::pair<int, std::size_t>, Cond> conds;
    std::vector<std::unique_ptr<Analyzer>>      list;
    for (const auto& words : stages) {
        auto a = std::make_unique<Analyzer>(std::span<const unsigned int>(words), inputs, conds);
        if (! a->Parse()) {
            sig.kind = "aperiodic";
            sig.reasons.push_back("spirv_unreadable");
            return sig;
        }
        if (const std::string why = a->Unsupported(); ! why.empty()) {
            sig.kind = "aperiodic";
            sig.reasons.push_back(why);
            return sig;
        }
        list.push_back(std::move(a));
    }
    std::stable_sort(list.begin(), list.end(), [](const auto& l, const auto& r) { return l->Model() < r->Model(); });
    std::map<std::uint32_t, Val>              varyings;
    std::vector<std::pair<Comp, std::string>> outputs;
    for (std::size_t i = 0; i < list.size(); ++i) {
        std::map<std::uint32_t, Val> next;
        list[i]->Execute(int(i), varyings, next, outputs);
        varyings = std::move(next);
    }
    Comp acc;
    for (const auto& [c, where] : outputs) {
        if (! c.aperiodic.empty())
            AddUnique(sig.reasons, c.aperiodic);
        else if (c.Linear())
            AddUnique(sig.reasons, std::string(c.rate_unknown ? "drift_rate_not_constant" : "drift") + " @" + where);
        MergeTags(acc, c);
    }
    for (const auto& [key, cond] : conds) {
        const Comp& c = cond.value;
        if (! c.aperiodic.empty())
            AddUnique(sig.reasons, c.aperiodic);
        else if (c.Linear())
            AddUnique(sig.reasons, (c.rate_unknown ? "time_rate_not_constant" : cond.loop ? "loop_count_time_dependent" : "branch_on_linear_time") + cond.where);
        else
            // 分支与循环次数只随周期量（或外部输入）变化：输出是这些量的确定函数，按它们的周期算
            MergeTags(acc, c);
    }
    sig.external  = acc.external;
    sig.transient = acc.transient;
    sig.settle    = acc.settle;
    sig.periods   = Classes(acc.periods);
    // 分量旋钮不参与分项：同 (周期, 次数, 其余旋钮) 的来源并成一项，每个来源都带分量旋钮时才留（取并集）
    std::vector<std::pair<Per, Knobs>> groups; // second 空 = 有来源不带分量旋钮
    for (const Per& p : acc.periods) {
        if (! (p.s > 0) || ! std::isfinite(p.s)) continue;
        Per   base { p.s, p.pi, {} };
        Knobs axes;
        for (const Knob& k : p.knobs) (k.varying.empty() ? base.knobs : axes).push_back(k);
        auto g = std::find_if(groups.begin(), groups.end(), [&](const auto& x) {
            return x.first.pi == base.pi && Near(x.first.s, base.s) && x.first.knobs == base.knobs;
        });
        if (g == groups.end())
            groups.emplace_back(base, axes);
        else
            g->second = g->second.empty() || axes.empty() ? Knobs {} : Union(g->second, axes);
    }
    for (const auto& [p, axes] : groups) {
        const auto r = p.pi == kNoPi ? std::nullopt : Rationalize(p.s / std::pow(kPi, p.pi));
        sig.terms.push_back(Term { .seconds = p.s,
                                   .num     = r ? r->p : 0,
                                   .den     = r ? r->q : 0,
                                   .pi      = p.pi == kNoPi ? std::nullopt : std::optional<int>(p.pi),
                                   .knobs   = Union(p.knobs, axes) });
    }
    sig.kind      = ! sig.reasons.empty() ? "aperiodic" : ! sig.periods.empty() ? "periodic" : "static";
    return sig;
}

std::string ToJson(const Signature& s) {
    char        buf[64];
    std::string r = "{\"kind\":" + Quote(s.kind) + ",\"periods\":[";
    for (std::size_t k = 0; k < s.periods.size(); ++k) {
        const Period& p = s.periods[k];
        std::snprintf(buf, sizeof(buf), "%.17g", p.seconds);
        r += (k ? ",{" : "{") + std::string("\"seconds\":") + buf + ",\"num\":" + std::to_string(p.num) +
             ",\"den\":" + std::to_string(p.den) + ",\"pi\":" + (p.pi ? "true" : "false") + "}";
    }
    r += "],\"terms\":[";
    for (std::size_t k = 0; k < s.terms.size(); ++k) {
        const Term& t = s.terms[k];
        std::snprintf(buf, sizeof(buf), "%.17g", t.seconds);
        r += (k ? ",{" : "{") + std::string("\"seconds\":") + buf + ",\"num\":" + std::to_string(t.num) +
             ",\"den\":" + std::to_string(t.den) + ",\"pi\":" + (t.pi ? std::to_string(*t.pi) : "null") + ",\"knobs\":[";
        for (std::size_t j = 0; j < t.knobs.size(); ++j) {
            const Knob& n = t.knobs[j];
            // 字面量按 float32 精确值转 double 输出，C# (float) 回去逐位相等
            std::snprintf(buf, sizeof(buf), "%.17g", double(n.literal));
            r += (j ? ",{" : "{") + std::string("\"stage\":") + Quote(n.stage) +
                 (! n.varying.empty() ? ",\"varying\":" + Quote(n.varying) + ",\"component\":" + std::to_string(n.component)
                  : n.uniform.empty() ? ",\"literal\":" + std::string(buf) : ",\"uniform\":" + Quote(n.uniform)) +
                 ",\"inverse\":" + (n.inverse ? "true" : "false") + "}";
        }
        r += "]}";
    }
    r += "],\"reasons\":[";
    for (std::size_t k = 0; k < s.reasons.size(); ++k) r += (k ? "," : "") + Quote(s.reasons[k]);
    r += "],\"external\":[";
    for (std::size_t k = 0; k < s.external.size(); ++k) r += (k ? "," : "") + Quote(s.external[k]);
    r += std::string("],\"transient\":") + (s.transient ? "true" : "false");
    if (s.settle >= 0) {
        std::snprintf(buf, sizeof(buf), "%.17g", s.settle);
        r += ",\"settle_seconds\":" + std::string(buf);
    }
    return r + "}";
}

} // namespace owe::shader_time
