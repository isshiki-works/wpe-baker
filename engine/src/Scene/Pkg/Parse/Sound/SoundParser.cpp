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

namespace
{

// wavsen 在 T5 前仍收 rstd::io::ReadSeekHandle；这里把 owe::io::RangeReader 包一层。
// wavsen 只看 is_err（读失败回 EIO、定位失败回 -1），所以错误一律报 Other。
struct WavsenRangeReader {
    owe::io::RangeReader reader;
};

auto WavsenError() -> rstd::io::error::Error {
    return rstd::io::error::Error::from_kind(
        rstd::io::error::ErrorKind { rstd::io::error::ErrorKind::Other });
}

} // namespace

template<>
struct rstd::Impl<rstd::io::Read, WavsenRangeReader> : rstd::ImplBase<WavsenRangeReader> {
    auto read(rstd::mut_ref<u8[]> buf) -> rstd::io::Result<usize> {
        auto result = this->self().reader.read(reinterpret_cast<std::uint8_t*>(buf.as_raw_ptr()),
                                               buf.len().to_primitive());
        if (result.is_err()) return rstd::Err(WavsenError());
        return rstd::Ok(usize(*result));
    }
};

template<>
struct rstd::Impl<rstd::io::Seek, WavsenRangeReader> : rstd::ImplBase<WavsenRangeReader> {
    auto seek(rstd::io::SeekFrom pos) -> rstd::io::Result<u64> {
        auto from = owe::io::SeekFrom::from_start(pos.start.to_primitive());
        if (pos.which == rstd::io::SeekFrom::Which::Current) {
            from = owe::io::SeekFrom::from_current(pos.offset.to_primitive());
        } else if (pos.which == rstd::io::SeekFrom::Which::End) {
            from = owe::io::SeekFrom::from_end(pos.offset.to_primitive());
        }
        auto result = this->self().reader.seek(from);
        if (result.is_err()) return rstd::Err(WavsenError());
        return rstd::Ok(u64(*result));
    }
};

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

class SoundControl final {
public:
    explicit SoundControl(Arc<SoundState> state): m_state(rstd::move(state)) {}

    void Play() {
        if (m_state->disabled) return;
        m_state->playing.store(true, Ordering::Release);
        m_state->play_seq.fetch_add(u32(1), Ordering::AcqRel);
    }
    void Stop() {
        if (m_state->disabled) return;
        m_state->playing.store(false, Ordering::Release);
        m_state->stop_seq.fetch_add(u32(1), Ordering::AcqRel);
    }
    void Pause() {
        if (! m_state->disabled) m_state->playing.store(false, Ordering::Release);
    }
    bool IsPlaying() const {
        return ! m_state->disabled && m_state->playing.load(Ordering::Acquire);
    }
    void SetVolume(float volume) {
        m_state->volume.store(f32(volume).clamp(f32(), f32(1.0f)), Ordering::Release);
    }

private:
    Arc<SoundState> m_state;
};

} // namespace

class SoundStream : public wavsen::audio::SoundStream {
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

    u64 next_pcm(void* pData, u32 frameCount) override {
        SyncControl();
        if (m_dead) return u64();
        if (! m_state->playing.load(Ordering::Acquire)) return u64();
        u64 frameReads {};
        size_t empty_sources = 0;
        while (frameReads < u64(frameCount.to_primitive()) && !m_dead) {
            if (!m_curActive) Switch();
            if (!m_curActive) break;
            const auto remaining = u32(frameCount.to_primitive() - frameReads.to_primitive());
            auto* output = static_cast<float*>(pData) + frameReads.to_primitive() * m_desc.channels.to_primitive();
            const auto count = m_curActive->next_pcm(output, remaining);
            if (auto failure = m_curActive->error(); failure.is_some()) {
                Fail(rstd::cppstd::to_string(*failure));
                break;
            }
            if (count > u64(remaining.to_primitive())) {
                Fail("sound decoder returned more sample frames than requested");
                break;
            }
            frameReads += count;
            if (count == u64()) ++empty_sources;
            else empty_sources = 0;
            if (count < u64(remaining.to_primitive())) {
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
        UpdateAudioAverage(pData, frameReads);
        {
            float*     pData_float = static_cast<float*>(pData);
            const auto num =
                usize(frameReads.to_primitive()) * usize(m_desc.channels.to_primitive());
            const auto volume = m_state->volume.load(Ordering::Acquire).to_primitive();
            for (usize i {}; i < num; ++i, ++pData_float) {
                (*pData_float) *= volume;
            }
        }
        return frameReads;
    };
    void pass_desc(const Desc& d) override { m_desc = d; }
    auto error() const -> Option<ref<str>> override {
        return m_error.is_empty() ? None() : Some(m_error.as_str());
    }

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
            auto handle = rstd::io::ReadSeekHandle::make(
                WavsenRangeReader { rstd::move(source).unwrap_unchecked().into_reader() });
            auto stream = wavsen::audio::make_stream(rstd::move(handle), m_desc);
            if (stream) {
                m_curActive = std::move(stream);
                return;
            }
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
        m_error = String::make(rstd::cppstd::as_str(message).unwrap());
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
        if (m_audio_average.is_none() || frameReads == u64() || m_desc.channels == u32()) return;

        const float* samples = static_cast<const float*>(pData);
        const auto total = usize(frameReads.to_primitive()) * usize(m_desc.channels.to_primitive());
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
    Desc            m_desc;
    Arc<SoundState> m_state;
    u32             m_curIndex {};
    u32             m_seenPlaySeq {};
    u32             m_seenStopSeq {};
    bool            m_dead { false };
    String          m_error;

    const std::vector<std::string>              m_soundPaths;
    std::unique_ptr<wavsen::audio::SoundStream> m_curActive;
    Option<Arc<SceneAudioAverage>>              m_audio_average;
};

Arc<dyn<SceneSoundControl>> SoundParser::Parse(const wpscene::SoundObject& obj, fs::VFS& vfs,
                                               wavsen::audio::SoundManager& sm, Scene* scene) {
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
    auto control = Arc<dyn<SceneSoundControl>>::make(SoundControl(state.clone()));
    if (! state->disabled) {
        auto ss = std::make_unique<SoundStream>(
            obj.sound, vfs, config, rstd::move(state), rstd::move(audio_average));
        sm.mount(std::move(ss));
    }
    return control;
}
