export module owe.media;

import rstd.cppstd;
import wescene.io;

// 引擎自己的媒体层（T5a：音频；T5b：视频解码）。
// 语义逐项照搬 wavsen::audio 的离线路径：StreamDecoder → AudioDecoder，
// SoundStream → PcmSource，SoundManager 的离线混音 → OfflineMixer；
// 视频照搬 wavsen::video::VideoDecoder 的软件解码路径 → VideoSource。
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
    virtual ~PcmSource() = default;

    // 往 dst 写至多 frames 帧交错 f32，返回实际帧数。
    virtual auto next_pcm(float* dst, std::uint32_t frames) -> std::uint32_t = 0;
    virtual void pass_desc(const PcmDesc&)                                   = 0;
    // 空串表示没有错误。
    virtual auto last_error() const -> std::string_view = 0;
};

// libav 解码 + swr 重采样到目标格式的交错 f32（原 wavsen StreamDecoder 的离线子集）。
// 输入经自定义 AVIOContext 从 RangeReader 读；不告诉 libav 总长（AVSEEK_SIZE 回 -1，同 wavsen）。
class AudioDecoder {
public:
    AudioDecoder();
    ~AudioDecoder();
    AudioDecoder(AudioDecoder&&) noexcept;
    auto operator=(AudioDecoder&&) noexcept -> AudioDecoder&;

    // 打开失败返回 false，原因见 last_error()。其余成员都只能在 open() 之后调用。
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
    void set_volume_scale(float value, std::uint32_t fade_ms);

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

// ---- 视频（T5b）：原 wavsen::video::VideoDecoder 的软件解码路径 ----

// 一帧 NV12（尺寸 = 解码器的目标尺寸）：Y 平面 w*h 字节，后接交错 UV 平面 w*h/2 字节。
struct Nv12Frame {
    std::vector<std::uint8_t> data;
    double                    pts_seconds { -1.0 };
    // 产出这帧的 swscale 所用矩阵与范围，编号同 wavsen：
    // colorspace 0 = BT.709、1 = BT.601、2 = BT.2020；color_range 0 = limited、1 = full。
    std::uint32_t colorspace {};
    std::uint32_t color_range {};
};

// 选中视频流的元数据（缩放、转 NV12 之前）。
struct VideoStreamMetadata {
    std::string                  codec;
    std::optional<std::uint32_t> coded_width;
    std::optional<std::uint32_t> coded_height;
    std::string                  pixel_format;
    std::optional<std::int32_t>  fps_num;
    std::optional<std::int32_t>  fps_den;
    std::string                  fps_source;
    // 流自身的时长，保留有理数形式。
    std::optional<std::int64_t>  duration_ticks;
    std::optional<std::int32_t>  time_base_num;
    std::optional<std::int32_t>  time_base_den;
    std::optional<std::uint64_t> frame_count;
};

// Looped：这一帧是读到结尾、回到开头之后解出的第一帧。
enum class NextFrame
{
    Ok,
    Looped,
};

// libav 实际采用的解码线程设置（只供日志）。
struct DecodeThreads {
    int count {};
    int type {};
};

// 软件解码 + swscale 到固定尺寸的 NV12。读到结尾自动回到开头（引擎的调用方都循环播放）。
// 解码线程数取环境变量 WAVSEN_VIDEO_DECODE_THREADS（不设/0/非法 = libavcodec 自动，1 = 单线程，
// n = n 个，上限 64），名字沿用 wavsen 以免改动部署脚本。
class VideoSource {
public:
    VideoSource();
    ~VideoSource();
    VideoSource(VideoSource&&) noexcept;

    // 目标尺寸为奇数时各加 1（NV12 色度减半）。打开失败返回 false，原因见 last_error()。
    // 其余成员都只能在 open() 成功之后调用。
    auto open(owe::io::RangeReader source, std::uint32_t target_width, std::uint32_t target_height)
        -> bool;

    // 失败返回 nullopt，原因见 last_error()；错误不锁存，下次调用照常再试。
    auto next_frame(Nv12Frame& out) -> std::optional<NextFrame>;
    // 跳到不超过 seconds 的关键帧（超出时长按时长算）；失败返回 false。
    auto seek(double seconds) -> bool;
    auto duration() const -> std::optional<double>;
    auto stream_metadata() const -> VideoStreamMetadata;
    auto decode_threads() const -> DecodeThreads;

    auto last_error() const -> std::string_view;

private:
    struct Impl;
    std::unique_ptr<Impl> m_impl;
};

} // namespace owe::media
