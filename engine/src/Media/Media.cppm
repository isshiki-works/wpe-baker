export module owe.media;

import rstd.cppstd;
import wescene.io;

// 引擎自己的媒体层（T5a：音频；视频在 T5b 迁入）。
// 语义逐项照搬 wavsen::audio 的离线路径：StreamDecoder → AudioDecoder，
// SoundStream → PcmSource，SoundManager 的离线混音 → OfflineMixer。
// libav 的头只进 .cpp，接口里不出现任何 libav 类型。
export namespace owe::media
{

// 交错 f32 PCM 的格式。默认值同 wavsen SoundManager 的离线格式（2 声道 48 kHz）。
struct PcmDesc {
    std::uint32_t channels { 2 };
    std::uint32_t sample_rate { 48000 };
};

// 可挂到混音器上的 PCM 源（原 wavsen SoundStream）。
class PcmSource {
public:
    PcmSource()                                    = default;
    virtual ~PcmSource()                           = default;
    PcmSource(const PcmSource&)                    = delete;
    auto operator=(const PcmSource&) -> PcmSource& = delete;

    // 往 dst 写至多 frames 帧交错 f32，返回实际帧数。
    virtual auto next_pcm(float* dst, std::uint32_t frames) -> std::uint32_t = 0;
    virtual void pass_desc(const PcmDesc&)                                   = 0;
    // 空串表示没有错误。
    virtual auto last_error() const -> std::string_view { return {}; }
};

// libav 解码 + swr 重采样到目标格式的交错 f32（原 wavsen StreamDecoder 的离线子集）。
// 输入经自定义 AVIOContext 从 RangeReader 读；不告诉 libav 总长（AVSEEK_SIZE 回 -1，同 wavsen）。
class AudioDecoder {
public:
    AudioDecoder();
    ~AudioDecoder();
    AudioDecoder(AudioDecoder&&) noexcept;
    auto operator=(AudioDecoder&&) noexcept -> AudioDecoder&;

    // 打开失败返回 false，原因见 last_error()。
    auto open(owe::io::RangeReader source, PcmDesc target) -> bool;

    // 拉 frames 帧；少于 frames 只发生在读完（已 drain 重采样器）或出错时。
    // 出错后错误锁存，之后一律返回 0。
    auto next_pcm(float* dst, std::uint32_t frames) -> std::uint32_t;

    auto last_error() const -> std::string_view;

private:
    struct Impl;
    std::unique_ptr<Impl> m_impl;
};

// 离线混音里的音量缩放渐变（原 wavsen detail::VolumeScaleRamp，逐语句照搬）。
class VolumeScaleRamp {
public:
    void redirect(float target, std::uint32_t sample_rate, std::uint32_t fade_ms);
    void apply(std::span<float> output, std::uint32_t channels, float volume);
    auto current() const -> float { return m_current; }

private:
    void advance();

    float         m_current { 1.0f };
    float         m_target { 1.0f };
    float         m_step {};
    std::uint32_t m_frames_left {};
};

// 同步离线混音器（原 wavsen SoundManager 的离线子集）：按挂载顺序逐源相加，再乘音量与渐变增益。
// 每次 mix 恰好消耗请求的时长；暂停时只输出静音、不拉源。
class OfflineMixer {
public:
    explicit OfflineMixer(PcmDesc desc = {});

    void mount(std::unique_ptr<PcmSource> source);
    void unmount_all();

    // 用混音结果覆盖 interleaved；失败返回 false，原因见 last_error()。
    auto mix(std::span<float> interleaved) -> bool;
    auto last_error() const -> const std::string& { return m_error; }

    void play() { m_playing = true; }
    void pause() { m_playing = false; }
    void set_volume(float value) { m_volume = value; }
    // 静音只作用于输出增益，不停解码时钟。
    void set_muted(bool muted) { m_muted = muted; }
    void set_volume_scale(float value, std::uint32_t fade_ms = 0);

private:
    PcmDesc                                 m_desc;
    std::vector<std::unique_ptr<PcmSource>> m_sources;
    std::vector<float>                      m_scratch;
    VolumeScaleRamp                         m_gain;
    std::string                             m_error;
    float                                   m_volume { 1.0f };
    bool                                    m_muted {};
    bool                                    m_playing {};
};

} // namespace owe::media
