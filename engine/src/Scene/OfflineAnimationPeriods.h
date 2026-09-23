#pragma once

// This file is included by OfflineSession.cpp after its module imports.  Keep
// it header-only and do not add textual standard-library includes here: the
// offline-session translation unit already imports the scene, JSON, and C++
// standard-library module surfaces it needs.

namespace owe
{

inline std::string OfflineAnimationPeriodJsonString(std::string_view value) {
    return owe::Dump(owe::NJson(std::string(value)));
}

inline i32 OfflineAnimationPeriodOwner(const SceneNode& node) {
    const SceneNode* current = &node;
    while (current != nullptr) {
        auto generator = current->GeneratorIdentity();
        if (generator.is_some()) return generator->value;
        auto wallpaper = current->WallpaperIdentity();
        if (wallpaper.is_some()) return wallpaper->value;
        current = current->Parent();
    }
    return i32(-1);
}

inline long long OfflineAnimationPeriodGcd(long long lhs, long long rhs) {
    while (rhs != 0) {
        const long long remainder = lhs % rhs;
        lhs = rhs;
        rhs = remainder;
    }
    return lhs;
}

// Shortest decimal that parses back to exactly this value (std::to_string kept 6 decimals).
template <class Real>
inline std::string OfflineShortestNumber(Real value) {
    char buffer[64];
    const auto result = std::to_chars(buffer, buffer + sizeof(buffer), value);
    return std::string(buffer, result.ptr);
}

struct OfflinePeriodRational {
    long long numerator {};
    long long denominator { 1 };
};

// Exact value of a positive float/double: every finite binary float is n / 2^k.
inline std::optional<OfflinePeriodRational> OfflinePeriodExact(double value) {
    if (! std::isfinite(value) || value <= 0.0) return std::nullopt;
    int exponent {};
    long long numerator = static_cast<long long>(std::ldexp(std::frexp(value, &exponent), 53));
    exponent -= 53;
    while (exponent < 0 && (numerator & 1) == 0) {
        numerator >>= 1;
        ++exponent;
    }
    if (exponent < -62 || exponent > 9) return std::nullopt;
    if (exponent >= 0) return OfflinePeriodRational { numerator << exponent, 1 };
    return OfflinePeriodRational { numerator, 1LL << -exponent };
}

// 轨道/精灵周期的有理形式：取 float 精度区间内最简的分数。
//
// 为什么吸附：这两类周期的源数据是 float（精灵每帧 frametime、动画 fps），作者写的 0.1、29.97
// 存进 float 时已带舍入误差，渲染器按这些 float 精确算出的 double（如 24 帧 0.1f 之和 =
// 2.400000035762787）是"float 化之后"的值，并不比作者本意的 12/5 更可信。直接给 double 的精确
// 二进制分数（40265319/16777216）会让下游求公倍数时分母爆到 2^24 量级，周期失真。
//
// 为什么是这个区间：float 舍入的相对误差不超过 2^-24（半个 ulp）。精灵周期是若干正帧时长之和，
// 和的相对误差不超过各项里最大的那个；轨道周期 End/Fps 里只有 fps 一处 float 舍入（End 是整数，
// 除法按 double 做，尾差约 2^-53）。所以作者本意的值必在 v·(1±2^-24) 内。再放宽一倍取
// [v·(1−2^-23), v·(1+2^-23)]（2^-23 即 float 的机器 epsilon），给 double 求和/除法的尾差留余量，
// 同时区间仍窄到只会吸附 float 本身分辨不出的差别。
//
// 区间内任何有理数都与 double 真值同样可信，取分母最小（同分母取分子最小）的那个：用连分数逐项
// 下降，等价于在 Stern–Brocot 树上从根往下找第一个落入区间的结点。端点用 __int128 精确表示
// （v = n/2^k，n < 2^53），全程不做浮点运算。分子或分母超出 int64、或 v 不是有限正数时返回 none，
// 调用方不输出有理字段。duration_seconds 仍输出 double 真值，不吸附。
inline std::optional<OfflinePeriodRational> OfflinePeriodSimplest(double value) {
    const auto exact = OfflinePeriodExact(value);
    if (! exact) return std::nullopt;
    using Wide = __int128;
    constexpr Wide scale = Wide(1) << 23;
    Wide lo_num = Wide(exact->numerator) * (scale - 1);
    Wide lo_den = Wide(exact->denominator) * scale;
    Wide hi_num = Wide(exact->numerator) * (scale + 1);
    Wide hi_den = lo_den;
    // 收敛子 h/k；push 追加一项连分数部分商，溢出 int64 时失败。
    long long h { 1 }, h_prev {}, k {}, k_prev { 1 };
    auto push = [&](Wide term) {
        if (term > Wide(std::numeric_limits<long long>::max())) return false;
        const long long a = static_cast<long long>(term);
        long long next_h {}, next_k {};
        if (__builtin_mul_overflow(a, h, &next_h) || __builtin_add_overflow(next_h, h_prev, &next_h) ||
            __builtin_mul_overflow(a, k, &next_k) || __builtin_add_overflow(next_k, k_prev, &next_k))
            return false;
        h_prev = h;
        h = next_h;
        k_prev = k;
        k = next_k;
        return true;
    };
    for (;;) {
        const Wide whole = lo_num / lo_den;
        // 下端点本身是整数，或区间里含整数：取区间内最小的整数，结束。
        if (lo_num % lo_den == 0) {
            if (! push(whole)) return std::nullopt;
            break;
        }
        if ((whole + 1) * hi_den <= hi_num) {
            if (! push(whole + 1)) return std::nullopt;
            break;
        }
        // 两端整数部分相同：记下这一项，对小数部分取倒数，区间变为 [1/(hi−whole), 1/(lo−whole)]。
        if (! push(whole)) return std::nullopt;
        const Wide next_lo_num = hi_den, next_lo_den = hi_num - whole * hi_den;
        const Wide next_hi_num = lo_den, next_hi_den = lo_num - whole * lo_den;
        lo_num = next_lo_num;
        lo_den = next_lo_den;
        hi_num = next_hi_num;
        hi_den = next_hi_den;
    }
    return OfflinePeriodRational { h, k };
}

inline void AppendOfflineAnimationPeriod(std::string& out, bool& first, i32 owner,
                                         std::string_view mechanism,
                                         std::string_view track_name,
                                         double duration_seconds,
                                         std::string_view playback_rate,
                                         std::string_view looping,
                                         std::string_view event_driven,
                                         std::string_view confidence,
                                         std::string_view evidence,
                                         std::string_view event_marker_count = {},
                                         std::string_view playback_mode = {},
                                         std::string_view duration_numerator = {},
                                         std::string_view duration_denominator = {},
                                         std::string_view frame_count = {},
                                         std::string_view dynamic_controlled = {}) {
    if (! first) out += ',';
    first = false;
    out += "{\"source_owner_layer_id\":";
    out += std::to_string(owner.to_primitive());
    out += ",\"mechanism\":";
    out += OfflineAnimationPeriodJsonString(mechanism);
    out += ",\"track_name\":";
    out += track_name.empty() ? "null" : OfflineAnimationPeriodJsonString(track_name);
    out += ",\"duration_seconds\":";
    out += OfflineShortestNumber(duration_seconds);
    if (! duration_numerator.empty() && ! duration_denominator.empty()) {
        out += ",\"duration_numerator\":";
        out += duration_numerator;
        out += ",\"duration_denominator\":";
        out += duration_denominator;
    }
    if (! frame_count.empty()) {
        out += ",\"frame_count\":";
        out += frame_count;
    }
    if (! playback_rate.empty()) {
        out += ",\"playback_rate\":";
        out += playback_rate;
    }
    out += ",\"looping\":";
    out += looping;
    if (! playback_mode.empty()) {
        out += ",\"playback_mode\":";
        out += OfflineAnimationPeriodJsonString(playback_mode);
    }
    out += ",\"event_driven\":";
    out += event_driven;
    if (! dynamic_controlled.empty()) {
        out += ",\"dynamic_controlled\":";
        out += dynamic_controlled;
    }
    out += ",\"confidence\":";
    out += OfflineAnimationPeriodJsonString(confidence);
    out += ",\"source_evidence\":";
    out += OfflineAnimationPeriodJsonString(evidence);
    if (! event_marker_count.empty()) {
        out += ",\"event_marker_count\":";
        out += event_marker_count;
    }
    out += '}';
}

// Returns periods represented by public runtime state.  Puppet provenance is
// registered on SceneNode with the same playback handles used by rendering,
// so Scene itself does not need to depend on PuppetLayer.
inline std::string DescribeOfflineAnimationPeriods(Scene& scene) {
    std::string out { "[" };
    bool        first { true };
    std::vector<std::string> emitted;

    auto was_emitted = [&](i32 owner, std::string_view mechanism,
                           std::string_view track_name) {
        std::string key = std::to_string(owner.to_primitive());
        key += ':';
        key += mechanism;
        key += ':';
        key += track_name;
        for (const auto& existing : emitted) {
            if (existing == key) return true;
        }
        emitted.push_back(std::move(key));
        return false;
    };

    const auto nodes = scene.ResourceIndex().Nodes();
    for (usize node_index {}; node_index < nodes.len(); ++node_index) {
        auto* node = nodes[node_index];
        if (node == nullptr) continue;

        const i32 owner = OfflineAnimationPeriodOwner(*node);
        if (owner < i32()) continue;

        for (const auto& playback : node->AnimationPlaybacks()) {
            const double duration = playback->Duration();
            if (! rstd::f64(duration).is_finite() || duration <= 0.0) continue;

            const auto clip = playback->Clip();
            std::string mode = rstd::cppstd::to_string(clip->Mode());
            const bool repeats = clip->WrapLoop() || mode == "loop" || mode == "repeat" ||
                                 mode == "mirror";
            std::string playback_rate;
            const float rate = playback->Rate();
            if (rstd::f32(rate).is_finite() && rate > 0.0f)
                playback_rate = OfflineShortestNumber(rate);
            // Duration() = End / Fps（fps 是 float），有理字段取 float 精度区间内的最简分数。
            const auto simplest = OfflinePeriodSimplest(duration);
            const std::string duration_numerator =
                simplest ? std::to_string(simplest->numerator) : std::string {};
            const std::string duration_denominator =
                simplest ? std::to_string(simplest->denominator) : std::string {};

            AppendOfflineAnimationPeriod(
                out,
                first,
                owner,
                node->IsPuppetAnimation(*playback) ? "puppet_bone" : "authored_track",
                rstd::cppstd::to_string(playback->Name()),
                duration,
                playback_rate,
                repeats ? "true" : "false",
                "null",
                "high",
                "SceneAnimationPlayback::Duration/Rate and SceneAnimationClip mode/events; event markers do not imply event-driven playback",
                std::to_string(clip->Events().len().to_primitive()),
                mode,
                duration_numerator,
                duration_denominator);
        }

        const auto* mesh = node->Mesh();
        if (mesh == nullptr) continue;
        const auto& materials = mesh->MaterialSlots();
        for (const auto& material : materials) {
            if (! material) continue;
            for (const auto& texture_name : material->textures) {
                auto texture = scene.Texture(rstd::cppstd::as_str(texture_name).unwrap());
                if (texture.is_none()) continue;

                if ((**texture).isSprite) {
                    const auto frame_count = (**texture).spriteAnim.numFrames();
                    if (frame_count == usize()) continue;

                    double period {};
                    bool   valid { true };
                    for (usize frame_index {}; frame_index < frame_count; ++frame_index) {
                        const float frame_time =
                            (**texture).spriteAnim.GetFrame(frame_index).frametime;
                        if (! rstd::f32(frame_time).is_finite() || frame_time <= 0.0f) {
                            valid = false;
                            break;
                        }
                        period += static_cast<double>(frame_time);
                    }
                    if (! valid || ! rstd::f64(period).is_finite() || period <= 0.0) continue;
                    if (was_emitted(owner, "sprite", texture_name)) continue;
                    // 精灵周期 = float 帧时长之和，有理字段取 float 精度区间内的最简分数。
                    const auto simplest = OfflinePeriodSimplest(period);

                    AppendOfflineAnimationPeriod(
                        out,
                        first,
                        owner,
                        "sprite",
                        texture_name,
                        period,
                        {},
                        "true",
                        "false",
                        "high",
                        "SceneTexture.spriteAnim frame[].frametime; SpriteAnimation loops at its final frame",
                        {},
                        "loop",
                        simplest ? std::to_string(simplest->numerator) : std::string {},
                        simplest ? std::to_string(simplest->denominator) : std::string {});
                    continue;
                }

                if (! (**texture).isVideo) continue;
                auto control = scene.VideoControl(rstd::cppstd::as_str(texture_name).unwrap());
                if (control.is_none()) continue;
                auto duration = (*control)->Duration();
                if (duration.is_none() || ! duration->is_finite() || *duration <= rstd::f64())
                    continue;
                if (was_emitted(owner, "video", texture_name)) continue;

                const auto snapshot = (*control)->Snapshot();
                const auto metadata = (*control)->PeriodMetadata();
                std::string playback_rate;
                if (snapshot.rate.is_finite() && snapshot.rate > rstd::f64()) {
                    playback_rate = OfflineShortestNumber(snapshot.rate.to_primitive());
                }
                std::string duration_numerator;
                std::string duration_denominator;
                if (metadata.duration_ticks.is_some() && metadata.time_base_num.is_some() &&
                    metadata.time_base_den.is_some()) {
                    long long ticks = *metadata.duration_ticks;
                    long long numerator = metadata.time_base_num->to_primitive();
                    long long denominator = metadata.time_base_den->to_primitive();
                    const long long tick_denominator_gcd =
                        OfflineAnimationPeriodGcd(ticks, denominator);
                    ticks /= tick_denominator_gcd;
                    denominator /= tick_denominator_gcd;
                    const long long numerator_denominator_gcd =
                        OfflineAnimationPeriodGcd(numerator, denominator);
                    numerator /= numerator_denominator_gcd;
                    denominator /= numerator_denominator_gcd;
                    if (ticks <= std::numeric_limits<long long>::max() / numerator) {
                        duration_numerator = std::to_string(ticks * numerator);
                        duration_denominator = std::to_string(denominator);
                    }
                }
                const std::string frame_count = metadata.frame_count.is_some()
                    ? std::to_string(metadata.frame_count->to_primitive()) : std::string {};
                AppendOfflineAnimationPeriod(
                    out,
                    first,
                    owner,
                    "video",
                    texture_name,
                    duration->to_primitive(),
                    playback_rate,
                    metadata.loops ? "true" : "null",
                    "null",
                    "medium",
                    metadata.loops
                        ? "VideoDecoder opened with loop=true (EOF seek-to-zero); AVStream.duration/time_base is emitted only when present; dynamic script/event control requires source analysis"
                        : "VideoPlaybackState::Duration and Snapshot().rate; runtime decoder loop metadata unavailable; dynamic script/event control requires source analysis",
                    {},
                    metadata.loops ? std::string_view("loop") : std::string_view {},
                    duration_numerator,
                    duration_denominator,
                    frame_count,
                    "null");
            }
        }
    }

    out += ']';
    return out;
}

} // namespace owe
