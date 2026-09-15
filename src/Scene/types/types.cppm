module;

// Make the global aligned allocation declarations visible before importing
// standard-library templates, so Clang does not synthesize a second overload.
#include <new>

export module wescene.types;
import wescene.core;
import rstd;
import rstd.cppstd;

export namespace owe
{

// ---------- enums + TextureSample (was Type.hpp) ---------------------------

enum class ImageType
{
    UNKNOWN = -1,
    BMP     = 0,
    ICO     = 1,
    JPEG    = 2,
    JNG     = 3,
    KOALA   = 4,
    LBM     = 5,
    MNG     = 6,
    PBM     = 7,
    PBMRAW  = 8,
    PCD     = 9,
    PCX     = 10,
    PGM     = 11,
    PGMRAW  = 12,
    PNG     = 13,
    PPM     = 14,
    PPMRAW  = 15,
    RAS     = 16,
    TARGA   = 17,
    TIFF    = 18,
    WBMP    = 19,
    PSD     = 20,
    CUT     = 21,
    XBM     = 22,
    XPM     = 23,
    DDS     = 24,
    GIF     = 25,
    HDR     = 26,
    FAXG3   = 27,
    SGI     = 28,
    EXR     = 29,
    J2K     = 30,
    JP2     = 31,
    PFM     = 32,
    PICT    = 33,
    RAW     = 34,
    // Wallpaper Engine "scene format" wallpapers may inline an MP4/WebM
    // container as a .tex body. We extend ImageType past FreeImage's
    // range (which stops at RAW=34) so the value can flow through the
    // existing ImageHeader::type slot without colliding.
    VIDEO = 100,
};
std::string ToString(const ImageType&);

enum class TextureFormat
{
    BC1,
    BC2,
    BC3,
    RGB8,
    RGBA8,
    RG8,
    R8,
    D32F
};
std::string ToString(const TextureFormat&);

enum class BlendMode
{
    Disable,
    Translucent,
    Additive,
    AlphaToCoverage,
    Normal
};

enum class CullMode
{
    None,
    Front,
    Back
};

enum class ShaderType
{
    VERTEX,
    GEOMETRY,
    FRAGMENT
};

enum class ShaderScalarKind
{
    Unknown,
    Float,
    SignedInteger,
    UnsignedInteger,
    Boolean,
};

enum class ShaderMatrixMajor
{
    None,
    Row,
    Column,
};

enum class ShaderMatrixConvention
{
    ColumnVector,
    RowVector,
};

enum class ShaderMatrixAbi
{
    NativeSpirv,
    Hlsl,
};

enum class TextureType
{
    IMG_2D,
};

enum class MeshPrimitive
{
    POINT,
    TRIANGLE
};

enum class FillMode
{
    STRETCH,
    ASPECTFIT,
    ASPECTCROP
};

enum class TextureWrap
{
    CLAMP_TO_EDGE,
    CLAMP_TO_BORDER,
    REPEAT
};

enum class TextureFilter
{
    LINEAR,
    NEAREST
};

enum class CompareOp
{
    Never,
    Less,
    LessEqual,
    Greater,
    GreaterEqual,
    Equal,
    NotEqual,
    Always,
};

enum class TextureBorderColor
{
    TransparentBlack,
    OpaqueBlack,
    OpaqueWhite,
};

struct TextureSample {
    TextureWrap        wrapS { TextureWrap::REPEAT };
    TextureWrap        wrapT { TextureWrap::REPEAT };
    TextureFilter      magFilter { TextureFilter::NEAREST };
    TextureFilter      minFilter { TextureFilter::NEAREST };
    bool               compare_enable { false };
    CompareOp          compare_op { CompareOp::Never };
    TextureBorderColor border_color { TextureBorderColor::OpaqueBlack };
};

struct VideoPlaybackSnapshot {
    bool      playing { true };
    rstd::f64 rate { 1.0 };
    rstd::u64 seek_sequence {};
    rstd::f64 seek_seconds {};
};

struct VideoPlaybackPeriodMetadata {
    rstd::Option<rstd::int64_t> duration_ticks;
    rstd::Option<rstd::i32> time_base_num;
    rstd::Option<rstd::i32> time_base_den;
    rstd::Option<rstd::u64> frame_count;
    bool loops { false };
};

class VideoPlaybackState {
public:
    VideoPlaybackState()                                             = default;
    VideoPlaybackState(const VideoPlaybackState&)                    = delete;
    VideoPlaybackState(VideoPlaybackState&&)                         = delete;
    auto operator=(const VideoPlaybackState&) -> VideoPlaybackState& = delete;
    auto operator=(VideoPlaybackState&&) -> VideoPlaybackState&      = delete;

    void Play() { m_playing.store(true, rstd::sync::atomic::Ordering::Release); }
    void Pause() { m_playing.store(false, rstd::sync::atomic::Ordering::Release); }
    void Stop() {
        Pause();
        Seek(rstd::f64());
    }
    void Seek(rstd::f64 seconds) {
        if (! seconds.is_finite() || seconds < rstd::f64()) seconds = rstd::f64();
        m_current_time.store(seconds, rstd::sync::atomic::Ordering::Release);
        m_seek_seconds.store(seconds, rstd::sync::atomic::Ordering::Release);
        m_seek_sequence.fetch_add(rstd::u64(1), rstd::sync::atomic::Ordering::AcqRel);
    }
    void SetRate(rstd::f64 rate) {
        if (! rate.is_finite() || rate <= rstd::f64()) return;
        m_rate.store(rate, rstd::sync::atomic::Ordering::Release);
    }

    auto Snapshot() const -> VideoPlaybackSnapshot {
        return VideoPlaybackSnapshot {
            .playing       = m_playing.load(rstd::sync::atomic::Ordering::Acquire),
            .rate          = m_rate.load(rstd::sync::atomic::Ordering::Acquire),
            .seek_sequence = m_seek_sequence.load(rstd::sync::atomic::Ordering::Acquire),
            .seek_seconds  = m_seek_seconds.load(rstd::sync::atomic::Ordering::Acquire),
        };
    }

    void PublishTime(rstd::f64 current, rstd::Option<rstd::f64> duration) {
        m_current_time.store(current, rstd::sync::atomic::Ordering::Release);
        m_duration.store(duration.unwrap_or(rstd::f64(-1.0)),
                         rstd::sync::atomic::Ordering::Release);
    }
    bool BeginDurationProbe() {
        return ! m_duration_probe_attempted.exchange(true, rstd::sync::atomic::Ordering::AcqRel);
    }
    void PublishDuration(rstd::Option<rstd::f64> duration) {
        m_duration.store(duration.unwrap_or(rstd::f64(-1.0)),
                         rstd::sync::atomic::Ordering::Release);
    }
    void PublishPeriodMetadata(const VideoPlaybackPeriodMetadata& metadata) {
        rstd::int64_t duration_ticks(-1);
        rstd::i32     time_base_num;
        rstd::i32     time_base_den;
        rstd::u64     frame_count;
        if (metadata.duration_ticks.is_some())
            duration_ticks = *metadata.duration_ticks;
        if (metadata.time_base_num.is_some())
            time_base_num = *metadata.time_base_num;
        if (metadata.time_base_den.is_some())
            time_base_den = *metadata.time_base_den;
        if (metadata.frame_count.is_some())
            frame_count = *metadata.frame_count;
        m_duration_ticks.store(duration_ticks, rstd::sync::atomic::Ordering::Release);
        m_time_base_num.store(time_base_num, rstd::sync::atomic::Ordering::Release);
        m_time_base_den.store(time_base_den, rstd::sync::atomic::Ordering::Release);
        m_frame_count.store(frame_count, rstd::sync::atomic::Ordering::Release);
        m_loops.store(metadata.loops, rstd::sync::atomic::Ordering::Release);
    }
    auto CurrentTime() const -> rstd::f64 {
        return m_current_time.load(rstd::sync::atomic::Ordering::Acquire);
    }
    auto Duration() const -> rstd::Option<rstd::f64> {
        auto value = m_duration.load(rstd::sync::atomic::Ordering::Acquire);
        return value >= rstd::f64() ? rstd::Some(value) : rstd::None<rstd::f64>();
    }
    auto PeriodMetadata() const -> VideoPlaybackPeriodMetadata {
        auto ticks = m_duration_ticks.load(rstd::sync::atomic::Ordering::Acquire);
        auto num = m_time_base_num.load(rstd::sync::atomic::Ordering::Acquire);
        auto den = m_time_base_den.load(rstd::sync::atomic::Ordering::Acquire);
        auto frames = m_frame_count.load(rstd::sync::atomic::Ordering::Acquire);
        VideoPlaybackPeriodMetadata metadata {
            .loops = m_loops.load(rstd::sync::atomic::Ordering::Acquire),
        };
        if (ticks > rstd::int64_t())
            metadata.duration_ticks = rstd::Some(ticks);
        if (num > rstd::i32())
            metadata.time_base_num = rstd::Some(num);
        if (den > rstd::i32())
            metadata.time_base_den = rstd::Some(den);
        if (frames > rstd::u64())
            metadata.frame_count = rstd::Some(frames);
        return metadata;
    }

private:
    rstd::sync::atomic::Atomic<bool>      m_playing { true };
    rstd::sync::atomic::Atomic<rstd::f64> m_rate { rstd::f64(1.0) };
    rstd::sync::atomic::Atomic<rstd::u64> m_seek_sequence {};
    rstd::sync::atomic::Atomic<rstd::f64> m_seek_seconds {};
    rstd::sync::atomic::Atomic<rstd::f64> m_current_time {};
    rstd::sync::atomic::Atomic<rstd::f64> m_duration { rstd::f64(-1.0) };
    rstd::sync::atomic::Atomic<bool>      m_duration_probe_attempted { false };
    rstd::sync::atomic::Atomic<bool>      m_loops { false };
    rstd::sync::atomic::Atomic<rstd::int64_t> m_duration_ticks { rstd::int64_t(-1) };
    rstd::sync::atomic::Atomic<rstd::i32> m_time_base_num {};
    rstd::sync::atomic::Atomic<rstd::i32> m_time_base_den {};
    rstd::sync::atomic::Atomic<rstd::u64> m_frame_count {};
};

enum class VertexType
{
    FLOAT1,
    FLOAT2,
    FLOAT3,
    FLOAT4,
    UINT1,
    UINT2,
    UINT3,
    UINT4
};

// ---------- BitFlags<EnumT> (was in Utils.cppm) ---------------------------

template<typename EnumT>
class BitFlags {
    static_assert(std::is_enum_v<EnumT>, "Flags can only be specialized for enum types");

    using UnderlyingT = typename std::make_unsigned_t<typename std::underlying_type_t<EnumT>>;

public:
    constexpr BitFlags() noexcept: bits_(0u) {}
    constexpr BitFlags(UnderlyingT val) noexcept: bits_(val) {}

    BitFlags& set(EnumT e, bool value = true) noexcept {
        bits_.set(underlying(e), value);
        return *this;
    }
    BitFlags& reset(EnumT e) noexcept {
        set(e, false);
        return *this;
    }
    BitFlags& reset() noexcept {
        bits_.reset();
        return *this;
    }
    [[nodiscard]] bool                  all() const noexcept { return bits_.all(); }
    [[nodiscard]] bool                  any() const noexcept { return bits_.any(); }
    [[nodiscard]] bool                  none() const noexcept { return bits_.none(); }
    [[nodiscard]] constexpr std::size_t size() const noexcept { return bits_.size(); }
    [[nodiscard]] std::size_t           count() const noexcept { return bits_.count(); }
    constexpr bool                      operator[](EnumT e) const { return bits_[underlying(e)]; }
    constexpr bool                      operator[](UnderlyingT t) const { return bits_[t]; }
    auto                                to_string() const { return bits_.to_string(); }

private:
    static constexpr UnderlyingT         underlying(EnumT e) { return static_cast<UnderlyingT>(e); }
    std::bitset<sizeof(UnderlyingT) * 8> bits_;
};

// ---------- SpriteAnimation (was SpriteAnimation.hpp) ---------------------

struct SpriteFrame {
    std::int32_t imageId { 0 };
    float        frametime { 0 };
    float        x { 0 };
    float        y { 0 };
    float        width { 1 };
    float        height { 1 };
    float        rate { 1 }; // real h / w

    std::array<float, 2> xAxis { 1, 0 };
    std::array<float, 2> yAxis { 0, 1 };
};

class SpriteAnimation {
public:
    const auto& GetAnimateFrame(double newtime) {
        const auto& current = m_frames.at(m_curFrame);
        if (! std::isfinite(newtime) || newtime <= 0.0) {
            return current;
        }
        // Invalid frame times do not define a period. Preserve the bounded
        // legacy one-frame-per-call fallback instead of inventing a duration.
        if (m_hasInvalidDuration || m_period <= 0.0 || ! std::isfinite(m_period)) {
            if ((m_remainTime -= newtime) <= 0.0) {
                SwitchToNext();
                m_remainTime = static_cast<double>(m_frames.at(m_curFrame).frametime);
            }
            return m_frames.at(m_curFrame);
        }

        double      delta       = std::fmod(newtime, m_period);
        std::size_t transitions {};
        while (transitions <= m_frames.size()) {
            if (m_remainTime > 0.0 && std::isfinite(m_remainTime)) {
                if (delta < m_remainTime) {
                    m_remainTime -= delta;
                    return m_frames.at(m_curFrame);
                }
                delta -= m_remainTime;
            }
            SwitchToNext();
            ++transitions;
            m_remainTime = static_cast<double>(m_frames.at(m_curFrame).frametime);
        }
        return m_frames.at(m_curFrame);
    }
    const auto& GetCurFrame() const { return m_frames.at(m_curFrame); }
    const auto& GetAnimateFrameSingleStep(double delta) {
        const auto& current = m_frames.at(m_curFrame);
        if (! std::isfinite(delta) || delta == 0.0) return current;
        if (m_hasInvalidDuration || m_period <= 0.0 || ! std::isfinite(m_period)) {
            m_remainTime -= std::abs(delta);
            if (m_remainTime <= 0.0) {
                if (delta > 0.0)
                    SwitchToNext();
                else
                    SwitchToPrevious();
                m_remainTime = static_cast<double>(m_frames.at(m_curFrame).frametime);
            }
            return m_frames.at(m_curFrame);
        }
        if (delta > 0.0) {
            m_remainTime -= delta;
            if (m_remainTime <= 0.0) {
                SwitchToNext();
                m_remainTime = static_cast<double>(m_frames.at(m_curFrame).frametime);
            }
        } else {
            const auto duration = static_cast<double>(m_frames.at(m_curFrame).frametime);
            m_remainTime += -delta;
            if (m_remainTime >= duration) {
                SwitchToPrevious();
                m_remainTime = 0.0;
            }
        }
        return m_frames.at(m_curFrame);
    }
    void AppendFrame(const SpriteFrame& frame) {
        m_frames.push_back(frame);
        const double duration = static_cast<double>(frame.frametime);
        if (! std::isfinite(duration) || duration <= 0.0)
            m_hasInvalidDuration = true;
        else
            m_period += duration;
        if (m_frames.size() == 1) m_remainTime = duration;
    }
    // Read a specific frame without advancing the internal cursor. Used by
    // the script-driven setFrame() override path.
    const SpriteFrame& GetFrame(usize i) const { return m_frames.at(i.to_primitive()); }

    usize numFrames() const { return usize(m_frames.size()); }
    usize CurrentFrameIndex() const { return usize(m_curFrame); }
    double Duration() const { return m_hasInvalidDuration ? 0.0 : m_period; }
    void SetCurrentFrame(usize i) {
        if (m_frames.empty()) return;
        m_curFrame    = i.to_primitive() % m_frames.size();
        m_remainTime = static_cast<double>(m_frames.at(m_curFrame).frametime);
    }

private:
    void SwitchToNext() {
        if (m_curFrame + 1 >= m_frames.size())
            m_curFrame = 0;
        else
            m_curFrame++;
    }
    void SwitchToPrevious() {
        if (m_curFrame == 0)
            m_curFrame = m_frames.size() - 1;
        else
            --m_curFrame;
    }
    std::size_t m_curFrame { 0 };
    double      m_remainTime { 0 };
    double      m_period { 0 };
    bool        m_hasInvalidDuration { false };

    std::vector<SpriteFrame> m_frames;
};

// ---------- Image (was Image.hpp) -----------------------------------------

union ImageExtra {
    int32_t val { 0 };
    char    str[125];
};

using ImageDataPtr = std::unique_ptr<uint8_t, std::function<void(uint8_t*)>>;

struct ImageData {
    std::int32_t                      width { 0 };
    std::int32_t                      height { 0 };
    isize                             size { 0 };
    ImageDataPtr                      data {};
    rstd::Option<rstd::io::ReadRange> video_source;
    ImageData() = default;
};

struct ImageHeader {
    std::int32_t width { 0 };
    std::int32_t height { 0 };
    std::int32_t mapWidth { 0 };
    std::int32_t mapHeight { 0 };

    bool mipmap_larger { false };
    bool mipmap_pow2 { false };

    ImageType     type { ImageType::UNKNOWN };
    TextureFormat format { TextureFormat::RGBA8 };
    std::int32_t  count { 0 };

    bool          isSprite { false };
    TextureSample sample;

    SpriteAnimation                             spriteAnim;
    std::unordered_map<std::string, ImageExtra> extraHeader;
};

struct Image : NoCopy, NoMove {
    struct Slot {
        std::int32_t width { 0 };
        std::int32_t height { 0 };

        std::vector<ImageData> mipmaps;

        explicit operator bool() const { return width != 0 && height != 0 && ! mipmaps.empty(); }
    };
    ImageHeader       header;
    std::vector<Slot> slots;
    std::string       key;
};

} // namespace owe

// Small OS utility — dlopen/dlsym wrapper. Lives here so wescene-vulkan-runtime
// can reach it without dragging wescene-base in. hash_combine is co-located
// for the same reason (TextureCache key hashing).
export namespace utils
{

template<typename T>
inline void hash_combine(std::size_t& seed, const T& val) {
    seed ^= std::hash<T>()(val) + 0x9e3779b9 + (seed << 6) + (seed >> 2);
}
template<typename T>
inline void hash_combine_fast(std::size_t& seed, const T& val) {
    seed ^= std::hash<T>()(val) << 1u;
}

class DynamicLibrary : NoCopy {
public:
    DynamicLibrary();
    ~DynamicLibrary();

    DynamicLibrary(const char* filename);

    DynamicLibrary(DynamicLibrary&& o) noexcept;
    DynamicLibrary& operator=(DynamicLibrary&& o) noexcept;

    bool IsOpen() const;
    bool Open(const char* filename);
    void Close();

    void* GetSymbolAddr(const char* name) const;

    template<typename T>
    bool GetSymbol(const char* name, T& pfunc) const {
        pfunc = reinterpret_cast<T>(GetSymbolAddr(name));
        return pfunc != nullptr;
    }

private:
    void* handle { nullptr };
};

} // namespace utils
