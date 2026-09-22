// T2a 差分用例：本文件被 side_rstd.cpp 与 side_compat.cpp 各包含一次，源码完全相同。
// 每个用例返回结果串；会 abort 的用例由 main 以子进程运行，比较退出码与 stderr。
using namespace rstd::prelude;
using namespace rstd::literals;
using rstd::string::String;
using rstd::vec::Vec;

namespace
{
float const NaNf = std::numeric_limits<float>::quiet_NaN();
float const INFf = std::numeric_limits<float>::infinity();

// ── 整数强类型 ──
std::string int_wrap() {
    Out o;
    o("u32_add", (u32(7) + u32(5)).to_primitive());
    o("u32_sub_wrap", (u32(0) - u32(1)).to_primitive());
    o("u8_add_wrap", (u8(200) + u8(100)).to_primitive());
    o("u16_mul_wrap", (u16(65535) * u16(65535)).to_primitive());
    o("i32_max_add", (i32::MAX + i32(1)).to_primitive());
    o("i8_min_sub", (i8(-128) - i8(1)).to_primitive());
    o("i32_neg_min", (-i32::MIN).to_primitive());
    o("u64_max_mul", (u64::MAX * u64(2)).to_primitive());
    o("usize_sub_wrap", (usize(1) - usize(2)).to_primitive());
    o("i64_mul_wrap", (i64::MAX * i64(3)).to_primitive());
    auto x = u32(5);
    x += u32(3);
    x *= u32(2);
    x -= u32(1);
    ++x;
    o("compound", x.to_primitive());
    o("u16max", u16::MAX.to_primitive())("i16min", i16::MIN.to_primitive());
    o("isize_min", isize::MIN.to_primitive())("u32_lt", u32(1) < u32(2))("i32_eq", i32(-1) == i32(-1));
    o("bitops", ((u32(0xF0) & u32(0x3C)) | (u32(1) ^ u32(3))).to_primitive())("not", (~u8(0x0F)).to_primitive());
    return o.s;
}

std::string int_shift_div() {
    Out o;
    o("shl_mask", (u32(1) << u64(33)).to_primitive());
    o("shl_u8", (u8(0x81) << u64(9)).to_primitive());
    o("shr_neg", (i32(-8) >> u64(1)).to_primitive());
    o("shr_mask", (u64(1024) >> u64(66)).to_primitive());
    o("div_neg", (i32(-7) / i32(2)).to_primitive())("rem_neg", (i32(-7) % i32(2)).to_primitive());
    o("div_u", (u32(7) / u32(2)).to_primitive())("rem_u", (u32(7) % u32(2)).to_primitive());
    return o.s;
}

std::string int_methods() {
    Out o;
    o("csub_none", u32(3).checked_sub(u32(5)).is_none());
    o("csub_some", u32(5).checked_sub(u32(3)).unwrap().to_primitive());
    o("csub_i32_ovf", i32::MIN.checked_sub(i32(1)).is_none());
    o("cmul_ovf", u32(0x10000).checked_mul(u32(0x10000)).is_none());
    o("cmul_ok", usize(6).checked_mul(usize(7)).unwrap().to_primitive());
    o("min", u32(3).min(u32(9)).to_primitive())("max", i32(-3).max(i32(-9)).to_primitive());
    o("swap_bytes", u32(0x11223344).swap_bytes().to_primitive());
    o("ctor_trunc_u8", u8(300).to_primitive())("ctor_trunc_i8", i8(200).to_primitive())("ctor_neg_u32", u32(-1).to_primitive());
    auto y = u32(1);
    o("ptr", *y.as_ptr());
    *y.as_mut_ptr() = 77;
    o("mut_ptr", y.to_primitive());
    return o.s;
}

// ── 浮点强类型 ──
std::string float_methods() {
    Out o;
    o("finite_inf", f32(INFf).is_finite())("finite_nan", f32(NaNf).is_finite())("finite_1", f32(1.0f).is_finite());
    o("sqrt_neg", f32(-1.0f).sqrt().is_finite())("sqrt_2", f32(2.0f).sqrt().to_primitive())("sqrt_2d", f64(2.0).sqrt().to_primitive());
    o("round_25", f32(2.5f).round().to_primitive())("round_m25", f32(-2.5f).round().to_primitive())("round_05d", f64(0.49999999999999994).round().to_primitive());
    o("floor_m05", f32(-0.5f).floor().to_primitive())("floor_d", f64(-1e-300).floor().to_primitive());
    o("abs_m0", f32(-0.0f).abs().to_primitive())("abs_m3", f64(-3.25).abs().to_primitive());
    o("min_nan", f32(NaNf).min(f32(1.0f)).to_primitive())("max_nan", f32(1.0f).max(f32(NaNf)).to_primitive());
    o("min_z", f32(0.0f).min(f32(-0.0f)).to_primitive())("max_z", f32(-0.0f).max(f32(0.0f)).to_primitive());
    o("clamp_lo", f32(-5.0f).clamp(f32(0.0f), f32(1.0f)).to_primitive())("clamp_nan", f32(NaNf).clamp(f32(0.0f), f32(1.0f)).is_finite());
    o("sin", f32(0.7f).sin().to_primitive())("cos", f32(0.7f).cos().to_primitive())("sin_d", f64(1e10).sin().to_primitive());
    o("atan2", f32(1.0f).atan2(f32(-2.0f)).to_primitive())("atan2_d", f64(-0.0).atan2(f64(-1.0)).to_primitive());
    o("pi", f32::consts::PI.to_primitive())("tau", f32::consts::TAU.to_primitive())("pi_d", f64::consts::PI.to_primitive());
    o("add", (f32(0.1f) + f32(0.2f)).to_primitive())("div0", (f64(1.0) / f64(0.0)).to_primitive())("neg", (-f32(0.0f)).to_primitive());
    o("lt_nan", f32(NaNf) < f32(1.0f))("eq_nan", f32(NaNf) == f32(NaNf));
    return o.s;
}

// ── as_cast ──
std::string cast_float_to_int() {
    Out o;
    float const fs[] = { NaNf, INFf, -INFf, 1e10f, -1e10f, 2147483647.0f, -2147483648.0f, 2147483520.0f,
                         -2147483904.0f, -0.9f, 0.99f, 4294967296.0f, 4294967040.0f, -1.0f, 1e-45f, 255.5f };
    for (float f : fs) {
        o("u32", rstd::as_cast<u32>(f).to_primitive())("i32", rstd::as_cast<i32>(f).to_primitive());
        o("usize", rstd::as_cast<usize>(f).to_primitive())("u64", rstd::as_cast<u64>(f).to_primitive());
        o("u8", rstd::as_cast<u8>(f32(f)).to_primitive())("i8", rstd::as_cast<i8>(f).to_primitive());
    }
    double const ds[] = { 18446744073709551616.0, 18446744073709549568.0, 9223372036854775808.0, -9223372036854775808.0,
                          -9223372036854777856.0, 4294967295.9, 4294967296.0, -0.0, 1e300, -1e-300 };
    for (double d : ds) {
        o("u64", rstd::as_cast<u64>(d).to_primitive())("i64", rstd::as_cast<i64>(d).to_primitive());
        o("u32", rstd::as_cast<u32>(f64(d)).to_primitive())("i32", rstd::as_cast<i32>(d).to_primitive());
        o("raw_u64", rstd::as_cast<std::uint64_t>(d))("raw_uns", rstd::as_cast<unsigned>(d));
    }
    return o.s;
}

enum class Tag : int { A = 5, B = -3 };

std::string cast_int_and_float() {
    Out o;
    o("u8_from_u32", rstd::as_cast<u8>(u32(300)).to_primitive())("i8_from_u32", rstd::as_cast<i8>(u32(200)).to_primitive());
    o("u32_from_i32", rstd::as_cast<u32>(i32(-1)).to_primitive())("i32_from_u64", rstd::as_cast<i32>(u64(0x1'0000'0005ull)).to_primitive());
    o("usize_from_i32", rstd::as_cast<usize>(i32(-1)).to_primitive())("u64_from_int", rstd::as_cast<u64>(-2).to_primitive());
    o("u32_from_enum", rstd::as_cast<u32>(Tag::B).to_primitive())("i32_from_enum", rstd::as_cast<i32>(Tag::A).to_primitive());
    o("f_from_u64max", rstd::as_cast<float>(u64::MAX))("d_from_i64min", rstd::as_cast<double>(i64::MIN));
    o("f_from_u32", rstd::as_cast<float>(u32(16777217)))("f_from_usize", rstd::as_cast<float>(usize(123456789)));
    o("f_from_big_d", rstd::as_cast<float>(1e300))("d_from_f32", rstd::as_cast<double>(f32(0.1f)));
    o("f32w_from_i32", rstd::as_cast<f32>(i32(-7)).to_primitive())("f_from_f64w", rstd::as_cast<float>(f64(1.0 / 3.0)));
    return o.s;
}

// ── cmp::max/min（NaN、±0、相等时返回哪一个）──
struct Keyed {
    int key;
    int id;
    auto operator>(Keyed const& r) const -> bool { return key > r.key; }
    auto operator<(Keyed const& r) const -> bool { return key < r.key; }
};
std::string cmp_max_min() {
    Out o;
    o("max_nan_1", rstd::cmp::max(NaNf, 1.0f))("max_1_nan", rstd::cmp::max(1.0f, NaNf));
    o("min_nan_1", rstd::cmp::min(NaNf, 1.0f))("min_1_nan", rstd::cmp::min(1.0f, NaNf));
    o("max_pz_nz", rstd::cmp::max(0.0f, -0.0f))("min_pz_nz", rstd::cmp::min(0.0f, -0.0f));
    o("max_eq_id", rstd::cmp::max(Keyed { 1, 10 }, Keyed { 1, 20 }).id)("min_eq_id", rstd::cmp::min(Keyed { 1, 10 }, Keyed { 1, 20 }).id);
    o("max_u32", rstd::cmp::max(u32(3), u32(9)).to_primitive())("min_i32", rstd::cmp::min(i32(-3), i32(9)).to_primitive());
    return o.s;
}

// ── Option ──
std::string option_basic() {
    Out o;
    auto a = Some(5);
    o("some", a.is_some())("none", a.is_none())("unwrap", a.unwrap())("deref", *a);
    Option<int> n = rstd::None();
    o("n_none", n.is_none())("unwrap_or", n.unwrap_or(7))("unwrap_or_some", a.unwrap_or(7));
    auto t = a.take();
    o("take_old", t.unwrap())("take_left", a.is_none());
    o("map", Some(21).map([](int v) { return v * 2; }).unwrap())("map_none", rstd::None<int>().map([](int v) { return v; }).is_none());
    Option<int> ins = rstd::None();
    int& r = ins.insert(9);
    r += 1;
    o("insert", *ins);
    auto so = Some(String::make("abc"_str));
    auto ar = so.as_ref();
    so->push_str("d"_str);
    o("as_ref_sees", rstd::cppstd::to_string(*ar));
    auto oa = Some(rstd::sync::Arc<int>::make(4));
    auto oc = oa.clone();
    o("clone_shares", oa->strong_count().to_primitive())("clone_val", **oc);
    o("unwrap_unchecked", Some(11).unwrap_unchecked());
    o("bool", static_cast<bool>(Some(1)))("bool_none", static_cast<bool>(rstd::None<int>()));
    return o.s;
}

std::string option_some_ref() {
    Out o;
    int x = 1;
    auto ox = Some(x); // 平凡可复制：按值
    x = 2;
    o("triv_copy", *ox);
    String s = String::make("a"_str);
    auto os = Some(s); // 非平凡左值：Option<String&>
    s.push_str("b"_str);
    o("ref_sees_update", rstd::cppstd::to_string(*os));
    o("ref_is_ptr_size", sizeof(os) == sizeof(void*));
    o("rvalue_owned", rstd::cppstd::to_string(Some(String::make("z"_str)).unwrap()));
    return o.s;
}
// 登记项：HashMap::get 返回的 Option<ref<V>> 在 rstd 是 16 字节，compat 是指针大小（只差 sizeof，不进 GPU/缓存布局）。
std::string option_ref_size() {
    Out o;
    rstd::collections::HashMap<u32, u32> m = rstd::collections::HashMap<u32, u32>::make();
    o("opt_ref_size", sizeof(decltype(m.get(u32(1)))));
    return o.s;
}

std::string option_transpose() {
    Out o;
    Option<Result<int, int>> a = Some(Result<int, int>(Ok(1)));
    Option<Result<int, int>> b = Some(Result<int, int>(Err(2)));
    Option<Result<int, int>> c = rstd::None();
    auto ta = a.transpose();
    auto tb = b.transpose();
    auto tc = c.transpose();
    o("a_ok", ta.is_ok())("a_val", ta.unwrap().unwrap());
    o("b_err", tb.is_err())("b_val", tb.unwrap_err());
    o("c_ok", tc.is_ok())("c_none", tc.unwrap().is_none());
    return o.s;
}

// ── Result 与 rstd_try ──
auto res_inner(int x) -> Result<int, int> {
    if (x < 0) return Err(x);
    return Ok(x * 10);
}
auto res_outer(int x) -> Result<int, long long> {
    auto v = rstd_try(res_inner(x));
    return Ok(v + 1);
}
auto opt_inner(int x) -> Option<int> {
    if (x < 0) return rstd::None();
    return Some(x);
}
auto opt_outer(int x) -> Option<int> {
    auto v = rstd_try(opt_inner(x));
    return Some(v + 1);
}

std::string result_basic() {
    Out o;
    Result<int, String> r = Ok(5);
    o("ok", r.is_ok())("err", r.is_err())("unwrap", r.unwrap());
    Result<int, String> e = Err(String::make("bad"_str));
    o("e_err", e.is_err())("unwrap_or", e.unwrap_or(3));
    Result<int, String> e2 = Err(String::make("bad2"_str));
    o("unwrap_err", rstd::cppstd::to_string(e2.unwrap_err()));
    Result<int, int> m = Ok(4);
    o("map", m.map([](int v) { return v * 3; }).unwrap());
    Result<int, String> k = Ok(8);
    o("ok_opt", k.ok().unwrap());
    Result<int, String> k2 = Err(String::make("x"_str));
    o("ok_none", k2.ok().is_none());
    Result<int, int> u = Ok(6);
    o("unchecked", u.unwrap_unchecked());
    Result<int, int> ue = Err(-6);
    o("err_unchecked", ue.unwrap_err_unchecked());
    return o.s;
}

std::string result_try() {
    Out o;
    o("ok", res_outer(4).unwrap())("err", res_outer(-3).unwrap_err());
    o("opt_ok", opt_outer(1).unwrap())("opt_none", opt_outer(-1).is_none());
    return o.s;
}

// ── format ──
std::string format_ints_strings() {
    Out o;
    o("ints", rstd::cppstd::to_string(rstd::format("{} {} {:X} {}", u32(255), i32(-5), u32(255), 42)));
    o("u64max", rstd::cppstd::to_string(rstd::format("{}", u64::MAX)));
    o("strs", rstd::cppstd::to_string(rstd::format("[{}|{}]", "ab"_str, String::make("cd"_str))));
    // 引擎日志里实际出现的格式说明：{:X}、{:04X}、{:#x}（包装类型与原生整数各一组）
    o("w_hex", rstd::cppstd::to_string(rstd::format("{:X}|{:04X}|{:#x}|{:x}", u32(255), u32(255), u32(255), u64(255))));
    o("r_hex", rstd::cppstd::to_string(rstd::format("{:X}|{:04X}|{:#x}", 255u, 255, std::uint64_t(255))));
    return o.s;
}
std::string format_floats() {
    Out o;
    o("f32", rstd::cppstd::to_string(rstd::format("{} {} {} {}", f32(0.1f), f32(1.0f), f32(-0.0f), f32(1.5f))));
    o("f32_big", rstd::cppstd::to_string(rstd::format("{} {}", f32(1e20f), f32(1e-7f))));
    o("f32_special", rstd::cppstd::to_string(rstd::format("{} {} {}", f32(NaNf), f32(INFf), f32(-INFf))));
    o("f64", rstd::cppstd::to_string(rstd::format("{} {} {}", f64(0.1), f64(123456789.0), f64(1.0 / 3.0))));
    return o.s;
}

// ── Vec ──
std::string vec_basic() {
    Out o;
    auto v = Vec<int>::make();
    o("empty", v.is_empty())("len0", v.len().to_primitive());
    v.push(1);
    v.push(2);
    v.push(3);
    o("len", v.len().to_primitive())("idx1", v[usize(1)]);
    v[usize(0)] = 10;
    o("idx0", v[usize(0)])("slice_len", v.as_slice().len().to_primitive())("as_ptr", v.as_ptr() == &v[usize(0)]);
    auto p = v.pop();
    o("pop", p.unwrap())("len_after_pop", v.len().to_primitive());
    auto e = Vec<int>::make();
    o("pop_empty", e.pop().is_none());
    auto w = Vec<int>::with_capacity(usize(16));
    o("cap_empty", w.is_empty());
    auto c = v.clone();
    c.push(99);
    o("clone_indep", v.len().to_primitive())("clone_len", c.len().to_primitive());
    auto vs = Vec<String>::make();
    vs.push(String::make("q"_str));
    auto vc = vs.clone();
    vs[usize(0)].push_str("r"_str);
    o("clone_deep", rstd::cppstd::to_string(vc[usize(0)]));
    return o.s;
}

// ── String / str / cppstd 桥 ──
std::string string_basic() {
    Out o;
    auto s = String::make("abc"_str);
    o("len", s.len().to_primitive())("empty", s.is_empty())("new_empty", String::make().is_empty());
    s.push_str("de"_str);
    o("push_str", rstd::cppstd::to_string(s))("as_str_len", s.as_str().len().to_primitive());
    auto c = s.clone();
    s.push_str("!"_str);
    o("clone", rstd::cppstd::to_string(c));
    auto u = rstd::cppstd::as_str("h\xc3\xa9llo");
    o("as_str_ok", u.is_ok())("as_str_len", u.unwrap().len().to_primitive());
    o("to_string", rstd::cppstd::to_string("xyz"_str))("as_sv", rstd::cppstd::as_string_view("pq"_str));
    o("lit_len", ("12345"_str).len().to_primitive())("lit_empty", (""_str).is_empty());
    return o.s;
}
// 登记项：非法 UTF-8。rstd 返回 Err，compat 不校验返回 Ok（预期差异）。
std::string as_str_invalid_utf8() {
    Out o;
    o("is_err", rstd::cppstd::as_str("\xff\xfe").is_err());
    return o.s;
}

// ── 集合 ──
std::string hashmap_basic() {
    Out o;
    auto m = rstd::collections::HashMap<u64, usize>::make();
    o("empty", m.is_empty());
    o("ins_new", m.insert(u64(1), usize(10)).is_none());
    o("ins_old", m.insert(u64(1), usize(11)).unwrap().to_primitive());
    m.insert(u64(2), usize(20));
    m.insert(u64(3), usize(30));
    o("len", m.len().to_primitive())("get", (*m.get(u64(1)).unwrap()).to_primitive())("get_none", m.get(u64(9)).is_none());
    o("contains", m.contains_key(u64(2)))("contains_no", m.contains_key(u64(7)));
    auto gm = m.get_mut(u64(3));
    *gm.unwrap() = usize(33);
    o("get_mut", (*m.get(u64(3)).unwrap()).to_primitive());
    o("remove", m.remove(u64(2)).unwrap().to_primitive())("remove_none", m.remove(u64(2)).is_none())("len2", m.len().to_primitive());
    // 遍历顺序不比较（rstd 每次运行随机，compat 固定）；只比较与顺序无关的和。
    std::uint64_t sk = 0, sv = 0;
    for (auto [k, v] : m.iter()) {
        sk += (*k).to_primitive();
        sv += (*v).to_primitive();
    }
    o("sum_k", sk)("sum_v", sv);
    return o.s;
}

std::string set_and_btree() {
    Out o;
    auto s = rstd::collections::HashSet<u32>::make();
    o("ins", s.insert(u32(5)))("ins_dup", s.insert(u32(5)))("contains", s.contains(u32(5)))("len", s.len().to_primitive());
    o("remove", s.remove(u32(5)))("remove_again", s.remove(u32(5)))("empty", s.is_empty());
    auto b = rstd::collections::BTreeMap<u32, int>::make();
    b.insert(u32(30), 3);
    b.insert(u32(10), 1);
    b.insert(u32(20), 2);
    o("b_old", b.insert(u32(20), 22).unwrap())("b_len", b.len().to_primitive());
    std::string order;
    for (auto [k, v] : b.iter()) order += std::to_string((*k).to_primitive()) + ":" + std::to_string(*v) + ",";
    o("b_order", order)("b_get", *b.get(u32(10)).unwrap())("b_remove", b.remove(u32(30)).unwrap());
    return o.s;
}

// ── Arc / Weak / Box ──
std::string arc_box() {
    Out o;
    auto a = rstd::sync::Arc<int>::make(5);
    o("deref", *a)("count1", a.strong_count().to_primitive());
    auto b = a.clone();
    o("count2", a.strong_count().to_primitive())("same", &*a == &*b);
    Option<rstd::sync::Arc<int>> holder = Some(rstd::sync::Arc<int>::make(7));
    auto w = holder->downgrade();
    o("upgrade", *w.upgrade())("upgrade_alive", static_cast<bool>(w.upgrade()));
    holder = rstd::None();
    o("upgrade_dead", ! static_cast<bool>(w.upgrade()));
    auto bx = rstd::boxed::Box<int>::make(3);
    o("box", *bx);
    return o.s;
}

// ── atomic / Mutex ──
struct Guarded {
    int v;
};
std::string atomic_mutex() {
    Out o;
    using rstd::sync::atomic::Ordering;
    rstd::sync::atomic::Atomic<u64> a { u64(1) };
    o("fetch_add_old", a.fetch_add(u64(2), Ordering::Relaxed).to_primitive())("load", a.load(Ordering::Acquire).to_primitive());
    a.store(u64(7), Ordering::Release);
    o("exchange_old", a.exchange(u64(9), Ordering::AcqRel).to_primitive())("load2", a.load(Ordering::Relaxed).to_primitive());
    rstd::sync::atomic::Atomic<bool> flag { true };
    o("bool", flag.load(Ordering::Acquire));
    rstd::sync::atomic::Atomic<f64> d { f64(1.5) };
    d.store(f64(2.5), Ordering::Release);
    o("f64", d.load(Ordering::Acquire).to_primitive());
    rstd::sync::atomic::Atomic<u32> z {};
    o("default_zero", z.load(Ordering::Relaxed).to_primitive());
    rstd::sync::Mutex<Guarded> m { Guarded { 5 } };
    {
        auto g = m.lock().unwrap_unchecked();
        g->v += 1;
    }
    o("mutex", m.lock().unwrap_unchecked()->v);
    return o.s;
}

// ── 日志（子进程里比较 stderr，时间戳归一化）──
std::string log_levels() {
    rstd_error("before logger {}", 0);
    static rstd::log::EnvLogger logger { "info"_str };
    rstd::log::set_logger(logger);
    rstd::log::set_max_level(rstd::log::LevelFilter::Info);
    rstd_error("e {}", 1);
    rstd_warn("w {} {}", u32(2), "s"_str);
    rstd_info("i {}", i32(-3));
    rstd_debug("d {}", 4);
    rstd::log::set_max_level(rstd::log::LevelFilter::Warn);
    rstd_info("filtered {}", 5);
    rstd_warn("still {}", 6);
    int evaluated = 0;
    rstd_info("lazy {}", ++evaluated);
    return std::format("evaluated={};", evaluated);
}

// ── 会 abort 的用例 ──
std::string die_option_unwrap() { Option<int> n = rstd::None(); return std::to_string(n.unwrap()); }
std::string die_option_deref() { Option<int> n = rstd::None(); return std::to_string(*n); }
std::string die_result_unwrap() { Result<int, int> r = Err(3); return std::to_string(r.unwrap()); }
std::string die_result_unwrap_err() { Result<int, int> r = Ok(3); return std::to_string(r.unwrap_err()); }
std::string die_div_zero() { volatile int z = 0; return std::to_string((u32(1) / u32(z)).to_primitive()); }
std::string die_div_overflow() { volatile int m = -1; return std::to_string((i32::MIN / i32(m)).to_primitive()); }
std::string die_rem_zero() { volatile int z = 0; return std::to_string((i64(5) % i64(z)).to_primitive()); }
std::string die_float_clamp() { return std::format("{}", f32(1.0f).clamp(f32(2.0f), f32(1.0f)).to_primitive()); }
std::string die_panic_fmt() { rstd::panic("value {} and {}", 42, "x"); }
std::string die_assert() { volatile int a = 1; rstd_assert(a == 2); return "unreachable"; }
std::string die_vec_oob() { auto v = Vec<int>::make(); v.push(1); return std::to_string(v[usize(5)]); }
} // namespace

auto cases() -> std::vector<DiffCase> const& {
    static std::vector<DiffCase> const table {
        { "int_wrap", int_wrap },
        { "int_shift_div", int_shift_div },
        { "int_methods", int_methods },
        { "float_methods", float_methods },
        { "cast_float_to_int", cast_float_to_int },
        { "cast_int_and_float", cast_int_and_float },
        { "cmp_max_min", cmp_max_min },
        { "option_basic", option_basic },
        { "option_some_ref", option_some_ref },
        { "option_ref_size", option_ref_size },
        { "option_transpose", option_transpose },
        { "result_basic", result_basic },
        { "result_try", result_try },
        { "format_ints_strings", format_ints_strings },
        { "format_floats", format_floats },
        { "vec_basic", vec_basic },
        { "string_basic", string_basic },
        { "as_str_invalid_utf8", as_str_invalid_utf8 },
        { "hashmap_basic", hashmap_basic },
        { "set_and_btree", set_and_btree },
        { "arc_box", arc_box },
        { "atomic_mutex", atomic_mutex },
        { "log_levels", log_levels },
        { "die_option_unwrap", die_option_unwrap },
        { "die_option_deref", die_option_deref },
        { "die_result_unwrap", die_result_unwrap },
        { "die_result_unwrap_err", die_result_unwrap_err },
        { "die_div_zero", die_div_zero },
        { "die_div_overflow", die_div_overflow },
        { "die_rem_zero", die_rem_zero },
        { "die_float_clamp", die_float_clamp },
        { "die_panic_fmt", die_panic_fmt },
        { "die_assert", die_assert },
        { "die_vec_oob", die_vec_oob },
    };
    return table;
}
