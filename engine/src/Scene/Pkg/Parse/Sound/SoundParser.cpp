module;
#include <new>

module wescene.pkg.parse;
import wescene.core;
import wescene.scene;
import rstd.cppstd;
import rstd.log;
import rstd;

using namespace rstd::prelude;
using namespace owe;
using rstd::sync::Arc;
using rstd::sync::atomic::Atomic;
using rstd::sync::atomic::Ordering;

enum class PlaybackMode
{
    Random,
    Loop,
    Single
};

static PlaybackMode ToPlaybackMode(std::string_view s) {
    if (s == "loop")
        return PlaybackMode::Loop;
    else if (s == "random")
        return PlaybackMode::Random;
    else if (s == "single")
        return PlaybackMode::Single;
    return PlaybackMode::Loop;
};

namespace
{

struct SoundState {
    Atomic<bool> playing { false };
    Atomic<f32>  volume { f32(1.0f) };
    Atomic<u32>  play_seq {};
    Atomic<u32>  stop_seq {};
    bool         disabled { false };
};

class SoundControl final : public SceneSoundControl {
public:
    explicit SoundControl(Arc<SoundState> state): m_state(rstd::move(state)) {}

    void Play() override {
        if (m_state->disabled) return;
        m_state->playing.store(true, Ordering::Release);
        m_state->play_seq.fetch_add(u32(1), Ordering::AcqRel);
    }
    void Stop() override {
        if (m_state->disabled) return;
        m_state->playing.store(false, Ordering::Release);
        m_state->stop_seq.fetch_add(u32(1), Ordering::AcqRel);
    }
    void Pause() override {
        if (! m_state->disabled) m_state->playing.store(false, Ordering::Release);
    }
    bool IsPlaying() const override {
        return ! m_state->disabled && m_state->playing.load(Ordering::Acquire);
    }
    void SetVolume(float volume) override {
        m_state->volume.store(f32(volume).clamp(f32(), f32(1.0f)), Ordering::Release);
    }

private:
    Arc<SoundState> m_state;
};

} // namespace

class SoundStream : public owe::media::PcmSource {
public:
    struct Config {
        f32          maxtime { 10.0f };
        f32          mintime {};
        f32          volume { 1.0f };
        PlaybackMode mode { PlaybackMode::Loop };
    };
    SoundStream(const std::vector<std::string>& paths, fs::VFS& vfs, Config c,
                Arc<SoundState> state, Option<Arc<SceneAudioAverage>> audio_average)
        : vfs(vfs),
          m_config(c),
          m_state(rstd::move(state)),
          m_soundPaths(paths),
          m_audio_average(rstd::move(audio_average)) {};
    virtual ~SoundStream() = default;

    std::uint32_t next_pcm(float* pData, std::uint32_t frameCount) override {
        SyncControl();
        if (m_dead) return 0;
        if (! m_state->playing.load(Ordering::Acquire)) return 0;
        std::uint32_t frameReads {};
        size_t empty_sources = 0;
        while (frameReads < frameCount && !m_dead) {
            if (!m_curActive) Switch();
            if (!m_curActive) break;
            const auto remaining = frameCount - frameReads;
            auto* output = pData + std::size_t(frameReads) * m_desc.channels;
            const auto count = m_curActive->next_pcm(output, remaining);
            if (auto failure = m_curActive->last_error(); ! failure.empty()) {
                Fail(std::string(failure));
                break;
            }
            if (count > remaining) {
                Fail("sound decoder returned more sample frames than requested");
                break;
            }
            frameReads += count;
            if (count == 0) ++empty_sources;
            else empty_sources = 0;
            if (count < remaining) {
                m_curActive.reset();
                if (m_config.mode == PlaybackMode::Single) {
                    m_state->playing.store(false, Ordering::Release);
                    break;
                }
                if (empty_sources > m_soundPaths.size()) {
                    Fail("sound playlist produced no decoded sample frames");
                    break;
                }
            }
        }
        UpdateAudioAverage(pData, u64(std::uint64_t(frameReads)));
        {
            float*     pData_float = pData;
            const auto num = usize(std::size_t(frameReads)) * usize(std::size_t(m_desc.channels));
            const auto volume = m_state->volume.load(Ordering::Acquire).to_primitive();
            for (usize i {}; i < num; ++i, ++pData_float) {
                (*pData_float) *= volume;
            }
        }
        return frameReads;
    };
    void pass_desc(const owe::media::PcmDesc& d) override { m_desc = d; }
    auto last_error() const -> std::string_view override { return m_error; }

    // Walk paths until one opens. If all fail, disable the stream so the
    // audio callback stops re-trying every tick (which spammed FFmpeg's
    // demuxer-probe errors at audio-callback rate).
    void Switch() {
        m_curActive.reset();
        const auto n = rstd::as_cast<u32>(usize(m_soundPaths.size()));
        if (n == u32()) {
            Fail("sound layer has no audio asset paths");
            return;
        }
        const u32 base = SelectStartIndex(n);
        for (u32 tried {}; tried < n; ++tried) {
            const std::string& path   = m_soundPaths[((base + tried) % n).to_primitive()];
            auto               source = vfs.open_read(fs::ToPath("/assets/" + path));
            if (source.is_err()) continue;
            owe::media::AudioDecoder decoder;
            if (decoder.open(rstd::move(source).unwrap_unchecked().into_reader(), m_desc)) {
                m_curActive = std::move(decoder);
                return;
            }
            rstd::log::error("SoundStream: {}: {}", path, std::string(decoder.last_error()));
        }
        m_dead = true;
        m_state->playing.store(false, Ordering::Release);
        Fail("all sound-layer audio assets failed to open");
    }
    u32 SelectStartIndex(u32 n) {
        if (n == u32()) return u32();
        if (m_config.mode == PlaybackMode::Random) {
            return u32(Random::get<uint32_t>(0, (n - u32(1)).to_primitive()));
        }
        if (m_config.mode == PlaybackMode::Single) {
            return u32(Random::get<uint32_t>(0, (n - u32(1)).to_primitive()));
        }
        u32 idx    = m_curIndex;
        m_curIndex = (m_curIndex + u32(1)) % n;
        return idx;
    }

private:
    void Fail(std::string message) {
        m_dead = true;
        m_error = message;
        if (active_offline_execution) active_offline_execution->diagnose("sound layer: " + message, true);
        rstd::log::error("SoundStream: {}", m_error);
    }

    void SyncControl() {
        const u32 stop_seq = m_state->stop_seq.load(Ordering::Acquire);
        if (stop_seq != m_seenStopSeq) {
            m_seenStopSeq = stop_seq;
            m_curActive.reset();
            m_dead = false;
            m_error.clear();
        }
        const u32 play_seq = m_state->play_seq.load(Ordering::Acquire);
        if (play_seq != m_seenPlaySeq) {
            m_seenPlaySeq = play_seq;
            m_curActive.reset();
            m_dead = false;
            m_error.clear();
        }
    }

    void UpdateAudioAverage(const void* pData, u64 frameReads) {
        if (m_audio_average.is_none() || frameReads == u64() || m_desc.channels == 0) return;

        const float* samples = static_cast<const float*>(pData);
        const auto total = usize(frameReads.to_primitive()) * usize(std::size_t(m_desc.channels));
        if (total == usize()) return;

        const usize bin_count = (*m_audio_average)->Len();
        for (usize bin {}; bin < bin_count; ++bin) {
            const auto begin = bin * total / bin_count;
            const auto end   = (bin + usize(1)) * total / bin_count;
            if (end <= begin) continue;

            float sum = 0.0f;
            for (usize i = begin; i < end; ++i) sum += std::abs(samples[i.to_primitive()]);
            float level =
                std::clamp(sum / static_cast<float>((end - begin).to_primitive()), 0.0f, 1.0f);

            const float old = (*m_audio_average)->Load(bin).to_primitive();
            (*m_audio_average)->Store(bin, f32(std::max(old * 0.75f, level)));
        }
    }

    fs::VFS&        vfs;
    Config          m_config;
    owe::media::PcmDesc m_desc;
    Arc<SoundState> m_state;
    u32             m_curIndex {};
    u32             m_seenPlaySeq {};
    u32             m_seenStopSeq {};
    bool            m_dead { false };
    std::string     m_error;

    const std::vector<std::string>          m_soundPaths;
    std::optional<owe::media::AudioDecoder> m_curActive;
    Option<Arc<SceneAudioAverage>>          m_audio_average;
};

std::shared_ptr<SceneSoundControl> SoundParser::Parse(const wpscene::SoundObject& obj, fs::VFS& vfs,
                                               owe::media::OfflineMixer& sm, Scene* scene) {
    SoundStream::Config config { .maxtime = f32(obj.maxtime),
                                 .mintime = f32(obj.mintime),
                                 .volume  = f32(obj.volume).clamp(f32(), f32(1.0f)),
                                 .mode    = ToPlaybackMode(obj.playbackmode) };

    Option<Arc<SceneAudioAverage>> audio_average = None();
    if (scene != nullptr) audio_average = Some(scene->AudioAverageHandle());
    auto state = Arc<SoundState>::make();
    // The node's sound control is attached after its visibility is applied, so
    // a layer that starts hidden has to be silenced here or it would play until
    // something toggles it.
    state->disabled = obj.sound.empty();
    state->playing.store(! state->disabled && obj.visible && ! obj.startsilent, Ordering::Release);
    state->volume.store(config.volume, Ordering::Release);
    auto control = std::shared_ptr<SceneSoundControl>(std::make_shared<SoundControl>(state.clone()));
    if (! state->disabled) {
        auto ss = std::make_unique<SoundStream>(
            obj.sound, vfs, config, rstd::move(state), rstd::move(audio_average));
        sm.mount(std::move(ss));
    }
    return control;
}
