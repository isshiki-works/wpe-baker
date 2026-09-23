module;
// vector 扩容要 operator new；不先包含 <new>，clang 22 报 operator new 歧义（消融实测）。
#include <new>

module owe.media;

import rstd.cppstd;

// 逐语句照搬 wavsen src/audio/gain.cppm（VolumeScaleRamp）与 mixer.cpp 的离线混音（mix_pcm）。
namespace owe::media
{

namespace
{

auto clamped(float value) -> float {
    if (! std::isfinite(value) || value < 0.0f) return 0.0f;
    return std::min(value, 1.0f);
}

} // namespace

void VolumeScaleRamp::redirect(float target, std::uint32_t sample_rate, std::uint32_t fade_ms) {
    target = clamped(target);
    if (fade_ms == 0) {
        m_current     = target;
        m_target      = target;
        m_step        = 0.0f;
        m_frames_left = 0;
        return;
    }
    auto fade_frames = std::uint64_t(sample_rate) * fade_ms / 1000;
    fade_frames      = std::min<std::uint64_t>(std::max<std::uint64_t>(fade_frames, 1),
                                                  std::numeric_limits<std::uint32_t>::max());
    m_target         = target;
    m_frames_left    = static_cast<std::uint32_t>(fade_frames);
    m_step           = (m_target - m_current) / static_cast<float>(m_frames_left);
}

void VolumeScaleRamp::apply(std::span<float> output, std::uint32_t channels, float volume) {
    if (m_frames_left == 0) {
        const float gain = volume * m_current;
        for (auto& sample : output) sample *= gain;
        return;
    }
    const auto frames = output.size() / channels;
    for (std::size_t frame = 0; frame < frames; ++frame) {
        const float gain = volume * m_current;
        const auto  base = frame * channels;
        for (std::size_t channel = 0; channel < channels; ++channel) output[base + channel] *= gain;
        advance();
    }
}

void VolumeScaleRamp::advance() {
    if (m_frames_left == 0) return;
    --m_frames_left;
    m_current = m_frames_left == 0 ? m_target : m_current + m_step;
}

OfflineMixer::OfflineMixer(PcmDesc desc): m_desc(desc) {}

void OfflineMixer::mount(std::unique_ptr<PcmSource> source) {
    source->pass_desc(m_desc);
    m_sources.push_back(std::move(source));
}

void OfflineMixer::unmount_all() { m_sources.clear(); }

void OfflineMixer::set_volume_scale(float value, std::uint32_t fade_ms) {
    m_gain.redirect(value, m_desc.sample_rate, fade_ms);
}

auto OfflineMixer::mix(std::span<float> output) -> bool {
    const std::size_t channels = m_desc.channels;
    if (output.size() % channels != 0) {
        m_error = "offline mixer buffer is not an integral number of sample frames";
        return false;
    }
    const auto frames = static_cast<std::uint32_t>(output.size() / channels);
    std::fill(output.begin(), output.end(), 0.0f);
    if (! m_playing) return true;

    m_scratch.resize(output.size());
    for (auto& source : m_sources) {
        const auto produced = source->next_pcm(m_scratch.data(), frames);
        if (auto error = source->last_error(); ! error.empty()) {
            m_error = error;
            return false;
        }
        const auto count = std::size_t(produced) * channels;
        for (std::size_t index = 0; index < count; ++index) {
            const float sample = m_scratch[index];
            if (! std::isfinite(sample)) {
                m_error = "PCM source returned a non-finite sample";
                return false;
            }
            output[index] += sample;
        }
    }
    // 静音只作用于输出增益，不停解码时钟。
    m_gain.apply(output, m_desc.channels, m_muted ? 0.0f : m_volume);
    return true;
}

} // namespace owe::media
