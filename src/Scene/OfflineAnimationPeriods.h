#pragma once

// This file is included by SceneWallpaper.cpp after its module imports.  Keep
// it header-only and do not add textual standard-library includes here: the
// scene-wallpaper translation unit already imports the scene, JSON, and C++
// standard-library module surfaces it needs.

namespace owe
{

inline std::string OfflineAnimationPeriodJsonString(std::string_view value) {
    return owe::Dump(owe::JsonFromStd(value));
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
    out += std::to_string(duration_seconds);
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
                playback_rate = std::to_string(rate);

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
                mode);
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
                        "loop");
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
                    playback_rate = std::to_string(snapshot.rate.to_primitive());
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
