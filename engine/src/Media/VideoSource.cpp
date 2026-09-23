module;
// make_unique 之前先包含 <new>，避开 clang 22 的 operator new 歧义。
#include <new>

extern "C" {
#include <libavcodec/avcodec.h>
#include <libavformat/avformat.h>
#include <libavutil/pixdesc.h>
#include <libswscale/swscale.h>
}

module owe.media;

import rstd.cppstd;
import wescene.io;

// 逐语句照搬 wavsen src/video/video_decoder.cpp 的软件解码路径（open_from_stream 不带 Producer 的分支、
// next_frame、seek、duration、stream_metadata）。硬件解码（Vulkan/VAAPI）不搬：离线渲染器固定软件解码。
// 对拍测试 video-parity-tests 逐帧比对两边的 NV12 字节。
namespace owe::media
{

namespace
{

// wavsen 视频路径的 avio 缓冲是 4 KiB（音频是 32 KiB），照旧。
constexpr int kAvioBuf = 4096;

auto av_err_str(int rc) -> std::string {
    char buf[AV_ERROR_MAX_STRING_SIZE] = {};
    av_strerror(rc, buf, sizeof(buf));
    return buf;
}

// 与 TextureCache 原来的 RangeInputStream + wavsen avio_read_shim 合起来的语义：
// 读错回 EIO、读到 0 字节回 EOF；AVSEEK_SIZE 回区间长度。
int avio_read_cb(void* opaque, std::uint8_t* buf, int size) {
    auto* source = static_cast<owe::io::RangeReader*>(opaque);
    auto  result = source->read(buf, static_cast<std::size_t>(size));
    if (result.is_err()) return AVERROR(EIO);
    if (*result == 0) return AVERROR_EOF;
    return static_cast<int>(*result);
}

std::int64_t avio_seek_cb(void* opaque, std::int64_t offset, int whence) {
    auto* source = static_cast<owe::io::RangeReader*>(opaque);
    if (whence == AVSEEK_SIZE) return static_cast<std::int64_t>(source->len());
    // avio 只以 SEEK_SET 回调（avio_seek 先把 SEEK_CUR 换算成绝对位置；AVSEEK_SIZE 有值就不走 SEEK_END）。
    if (whence != SEEK_SET) return -1;
    auto result = source->seek(owe::io::SeekFrom::from_start(static_cast<std::uint64_t>(offset)));
    return result.is_ok() ? static_cast<std::int64_t>(*result) : -1;
}

// FFmpeg 的色彩矩阵/范围 → Nv12Frame 的编号。未知一律按 BT.709 limited。
std::uint32_t map_colorspace(int cs) {
    switch (cs) {
    case AVCOL_SPC_BT709: return 0;
    case AVCOL_SPC_BT470BG: // PAL / BT.601 625
    case AVCOL_SPC_SMPTE170M: return 1;
    case AVCOL_SPC_BT2020_NCL: return 2;
    case AVCOL_SPC_BT2020_CL: return 2;
    default: return 0;
    }
}

int sws_source_matrix(int colorspace) {
    switch (colorspace) {
    case AVCOL_SPC_BT470BG:
    case AVCOL_SPC_SMPTE170M: return SWS_CS_ITU601;
    case AVCOL_SPC_BT2020_NCL:
    case AVCOL_SPC_BT2020_CL: return SWS_CS_BT2020;
    case AVCOL_SPC_FCC: return SWS_CS_FCC;
    case AVCOL_SPC_SMPTE240M: return SWS_CS_SMPTE240M;
    default: return SWS_CS_ITU709;
    }
}

// 不设 / 0 / 非法 → 0（libavcodec 按 CPU 数自动）；1 → 单线程；n → n 个（上限 64）。
int decode_thread_count_from_env() {
    const char* raw = std::getenv("WAVSEN_VIDEO_DECODE_THREADS");
    if (! raw || ! raw[0]) return 0;
    char* end   = nullptr;
    long  value = std::strtol(raw, &end, 10);
    if (end == raw || value < 0 || value > 64) return 0;
    return static_cast<int>(value);
}

} // namespace

struct VideoSource::Impl {
    explicit Impl(owe::io::RangeReader reader): source(std::move(reader)) {}
    Impl(const Impl&)                    = delete;
    auto operator=(const Impl&) -> Impl& = delete;

    ~Impl() {
        // 先关 avformat，它不再回调 avio 之后再释放 avio 与缓冲（libav 可能换过缓冲，释放当前的）。
        if (fmt) avformat_close_input(&fmt);
        if (avio) {
            av_freep(&avio->buffer);
            avio_context_free(&avio);
        }
        if (sws) sws_freeContext(sws);
        if (src_frame) av_frame_free(&src_frame);
        if (pkt) av_packet_free(&pkt);
        if (cctx) avcodec_free_context(&cctx);
    }

    auto fail(std::string message) -> bool {
        error = std::move(message);
        return false;
    }

    // 解码错误先交给 tail 判定是否可能在流末尾；不是就锁存。
    // 帧线程解码时错误要等后面几个包送进去才在 receive_frame 冒出来：离结尾不到"线程数"个包的中途错误
    // 会在冲洗阶段才出现，于是按末尾处理（THREADS=1 时照旧锁存）。
    auto defer_decode_error(const char* operation, int code) -> bool {
        return tail.defer(operation, code) || fail(std::string(operation) + ": " + av_err_str(code));
    }

    auto open(std::uint32_t width, std::uint32_t height) -> bool {
        if (width == 0 || height == 0) return fail("target dimensions must be non-zero");
        // 奇数宽高照原样解码（不补成偶数再缩放）：NV12 色度按向上取整打包，见 next_frame。
        target_width  = width;
        target_height = height;

        auto* buffer = static_cast<unsigned char*>(av_malloc(kAvioBuf));
        if (! buffer) return fail("av_malloc(avio buffer) failed");
        avio = avio_alloc_context(buffer, kAvioBuf, 0, &source, &avio_read_cb, nullptr, &avio_seek_cb);
        if (! avio) {
            av_free(buffer);
            return fail("avio_alloc_context failed");
        }
        fmt = avformat_alloc_context();
        if (! fmt) return fail("avformat_alloc_context failed");
        fmt->pb = avio;
        fmt->flags |= AVFMT_FLAG_CUSTOM_IO;
        if (int rc = avformat_open_input(&fmt, nullptr, nullptr, nullptr); rc < 0) {
            // 失败时 avformat_open_input 已释放上下文并置空 fmt；avio 留给析构释放。
            return fail("avformat_open_input(stream): " + av_err_str(rc));
        }
        if (int rc = avformat_find_stream_info(fmt, nullptr); rc < 0)
            return fail("avformat_find_stream_info: " + av_err_str(rc));

        video_idx = av_find_best_stream(fmt, AVMEDIA_TYPE_VIDEO, -1, -1, nullptr, 0);
        if (video_idx < 0) return fail("no video stream in file");
        AVStream*          st  = fmt->streams[video_idx];
        AVCodecParameters* par = st->codecpar;
        stream_tb              = st->time_base;

        // AV1 取到的是 libdav1d：FFmpeg 自带的 av1 解码器只做 hwaccel 分派，本构建注册顺序里 libdav1d 在前。
        const AVCodec* dec = avcodec_find_decoder(par->codec_id);
        if (! dec) return fail(std::string("no decoder for codec ") + avcodec_get_name(par->codec_id));
        cctx = avcodec_alloc_context3(dec);
        if (! cctx) return fail("avcodec_alloc_context3 failed");
        if (int rc = avcodec_parameters_to_context(cctx, par); rc < 0)
            return fail("avcodec_parameters_to_context: " + av_err_str(rc));
        cctx->pkt_timebase = st->time_base;
        // 帧/片线程对本引擎解的编码逐位一致，无条件打开（T5b-prep 实测两种设置输出相同）。
        cctx->thread_count = decode_thread_count_from_env();
        cctx->thread_type  = FF_THREAD_FRAME | FF_THREAD_SLICE;
        if (int rc = avcodec_open2(cctx, dec, nullptr); rc < 0) return fail("avcodec_open2: " + av_err_str(rc));

        pkt       = av_packet_alloc();
        src_frame = av_frame_alloc();
        if (! pkt || ! src_frame) return fail("av_packet_alloc / av_frame_alloc failed");
        return true;
    }

    auto ensure_sws(const AVFrame& frame) -> bool {
        const int  src_w   = frame.width;
        const int  src_h   = frame.height;
        const auto src_fmt = static_cast<AVPixelFormat>(frame.format);
        if (sws && sws_src_w == src_w && sws_src_h == src_h && sws_src_fmt == src_fmt &&
            sws_src_colorspace == frame.colorspace && sws_src_color_range == frame.color_range) {
            return true;
        }
        if (sws) sws_freeContext(sws);
        sws = sws_getContext(src_w,
                             src_h,
                             src_fmt,
                             static_cast<int>(target_width),
                             static_cast<int>(target_height),
                             AV_PIX_FMT_NV12,
                             SWS_BICUBIC,
                             nullptr,
                             nullptr,
                             nullptr);
        if (! sws) return fail(std::string("sws_getContext failed (src=") + av_get_pix_fmt_name(src_fmt) + ")");

        // 新上下文按像素格式给出默认范围（RGB、YUVJ、灰度）；帧上显式的 YUV 范围覆盖它。
        // RGB/调色板分量在 swscale 里总是全范围。
        int* default_source_coefficients = nullptr;
        int* default_target_coefficients = nullptr;
        int  source_range = 0, default_target_range = 0;
        int  brightness = 0, contrast = 0, saturation = 0;
        int  rc = sws_getColorspaceDetails(sws,
                                          &default_source_coefficients,
                                          &source_range,
                                          &default_target_coefficients,
                                          &default_target_range,
                                          &brightness,
                                          &contrast,
                                          &saturation);
        if (rc < 0) {
            sws_freeContext(sws);
            sws = nullptr;
            return fail("sws_getColorspaceDetails: " + av_err_str(rc));
        }
        const auto* desc = av_pix_fmt_desc_get(src_fmt);
        const bool  rgb  = desc && (desc->flags & (AV_PIX_FMT_FLAG_RGB | AV_PIX_FMT_FLAG_PAL));
        if (rgb || frame.color_range == AVCOL_RANGE_JPEG) source_range = 1;
        else if (frame.color_range == AVCOL_RANGE_MPEG) source_range = 0;

        // 支持的 YUV 矩阵与范围原样保留；RGB 变成 BT.709 NV12；不支持/未标注的矩阵按 BT.709。
        // 输出元数据描述这次转换，而不是原始 RGB 帧。
        const std::uint32_t output_colorspace = rgb ? 0 : map_colorspace(frame.colorspace);
        const int target_matrix = output_colorspace == 1 ? SWS_CS_ITU601 :
                                  output_colorspace == 2 ? SWS_CS_BT2020 : SWS_CS_ITU709;
        const int source_matrix = rgb ? SWS_CS_ITU709 : sws_source_matrix(frame.colorspace);
        const int target_range  = source_range;
        rc = sws_setColorspaceDetails(sws,
                                      sws_getCoefficients(source_matrix),
                                      source_range,
                                      sws_getCoefficients(target_matrix),
                                      target_range,
                                      0,
                                      1 << 16,
                                      1 << 16);
        if (rc < 0) {
            sws_freeContext(sws);
            sws = nullptr;
            return fail("sws_setColorspaceDetails: " + av_err_str(rc));
        }
        sws_src_w           = src_w;
        sws_src_h           = src_h;
        sws_src_fmt         = src_fmt;
        sws_src_colorspace  = frame.colorspace;
        sws_src_color_range = frame.color_range;
        sws_out_colorspace  = output_colorspace;
        sws_out_color_range = target_range == 1 ? 1u : 0u;
        return true;
    }

    void reset_after_seek() {
        avcodec_flush_buffers(cctx);
        av_packet_unref(pkt);
        av_frame_unref(src_frame);
        flushing = false;
        tail.reset();
    }

    auto next_frame(Nv12Frame& out) -> std::optional<NextFrame> {
        bool looped = false;

        // 输出缓冲按 NV12 尺寸定长（解码器生命周期内尺寸固定）。4:2:0 色度向上取整：
        // UV 平面 ceil(w/2) × ceil(h/2) 个 (U,V) 对，行距 2×ceil(w/2)（奇数宽高时 swscale 就写这么多）。
        const std::size_t uv_pitch = std::size_t((target_width + 1) / 2) * 2;
        const std::size_t want =
            std::size_t(target_width) * target_height + uv_pitch * ((target_height + 1) / 2);
        if (out.data.size() != want) out.data.resize(want, 0);

        while (true) {
            int rc = avcodec_receive_frame(cctx, src_frame);
            if (rc == 0) {
                tail.frame_decoded();
                const AVFrame& feed    = *src_frame;
                const auto     src_fmt = static_cast<AVPixelFormat>(feed.format);
                if (feed.width <= 0 || feed.height <= 0 || src_fmt == AV_PIX_FMT_NONE) {
                    fail("decoded frame has invalid dimensions/format");
                    return std::nullopt;
                }
                if (! ensure_sws(feed)) return std::nullopt;
                std::uint8_t* dst_planes[4]  = { out.data.data(),
                                                 out.data.data() + std::size_t(target_width) * target_height,
                                                 nullptr,
                                                 nullptr };
                int           dst_strides[4] = { static_cast<int>(target_width),
                                                 static_cast<int>(uv_pitch),
                                                 0,
                                                 0 };
                if (sws_scale(sws, feed.data, feed.linesize, 0, feed.height, dst_planes, dst_strides) <= 0) {
                    fail("sws_scale produced no rows");
                    return std::nullopt;
                }
                const std::int64_t pts =
                    feed.best_effort_timestamp != AV_NOPTS_VALUE ? feed.best_effort_timestamp : feed.pts;
                out.pts_seconds = pts == AV_NOPTS_VALUE ? -1.0 : static_cast<double>(pts) * av_q2d(stream_tb);
                out.colorspace  = sws_out_colorspace;
                out.color_range = sws_out_color_range;
                av_frame_unref(src_frame);
                return looped ? NextFrame::Looped : NextFrame::Ok;
            }
            if (rc == AVERROR_EOF) {
                if (av_seek_frame(fmt, -1, 0, AVSEEK_FLAG_BACKWARD) < 0) {
                    fail("loop seek-to-zero failed");
                    return std::nullopt;
                }
                reset_after_seek();
                looped = true;
                continue;
            }
            if (rc != AVERROR(EAGAIN)) {
                if (! defer_decode_error("avcodec_receive_frame", rc)) return std::nullopt;
                continue;
            }
            if (flushing) continue;

            rc = av_read_frame(fmt, pkt);
            if (rc == AVERROR_EOF) {
                avcodec_send_packet(cctx, nullptr);
                flushing = true;
                continue;
            }
            if (rc < 0) {
                fail("av_read_frame: " + av_err_str(rc));
                return std::nullopt;
            }
            if (pkt->stream_index != video_idx) {
                av_packet_unref(pkt);
                continue;
            }
            const char* operation {};
            int         deferred {};
            if (tail.take_mid_stream(operation, deferred)) {
                // 出错的包后面还有本流的包：错误在流中间，锁存。
                av_packet_unref(pkt);
                fail(std::string(operation) + ": " + av_err_str(deferred));
                return std::nullopt;
            }
            rc = avcodec_send_packet(cctx, pkt);
            av_packet_unref(pkt);
            if (rc < 0 && rc != AVERROR(EAGAIN) && ! defer_decode_error("avcodec_send_packet", rc))
                return std::nullopt;
        }
    }

    auto duration() const -> std::optional<double> {
        // 视频流自己的 tick 保留亚微秒级帧边界；容器时长按 AV_TIME_BASE 取整、还可能含更长的音频流，
        // 只在视频流没有时长时才用。
        const AVStream* stream = fmt->streams[video_idx];
        if (stream->duration > 0 && stream->duration != AV_NOPTS_VALUE)
            return static_cast<double>(stream->duration) * av_q2d(stream->time_base);
        if (fmt->duration > 0) return static_cast<double>(fmt->duration) / AV_TIME_BASE;
        return std::nullopt;
    }

    owe::io::RangeReader source;
    AVIOContext*         avio { nullptr };
    AVFormatContext*     fmt { nullptr };
    AVCodecContext*      cctx { nullptr };
    AVPacket*            pkt { nullptr };
    AVFrame*             src_frame { nullptr };
    SwsContext*          sws { nullptr };
    AVPixelFormat        sws_src_fmt { AV_PIX_FMT_NONE };
    int                  sws_src_w { 0 };
    int                  sws_src_h { 0 };
    int                  sws_src_colorspace { -1 };
    int                  sws_src_color_range { -1 };
    std::uint32_t        sws_out_colorspace {};
    std::uint32_t        sws_out_color_range {};
    int                  video_idx { -1 };
    AVRational           stream_tb { 0, 1 };
    bool                 flushing { false };
    detail::DecodeTailGate tail;
    std::uint32_t        target_width {};
    std::uint32_t        target_height {};
    std::string          error;
};

VideoSource::VideoSource()                                           = default;
VideoSource::~VideoSource()                                          = default;
VideoSource::VideoSource(VideoSource&&) noexcept                     = default;

auto VideoSource::open(owe::io::RangeReader source, std::uint32_t target_width,
                       std::uint32_t target_height) -> bool {
    m_impl = std::make_unique<Impl>(std::move(source));
    return m_impl->open(target_width, target_height);
}

auto VideoSource::next_frame(Nv12Frame& out) -> std::optional<NextFrame> { return m_impl->next_frame(out); }

auto VideoSource::seek(double seconds) -> bool {
    if (! std::isfinite(seconds) || seconds < 0.0)
        return m_impl->fail("seek time must be finite and non-negative");
    if (auto limit = duration()) seconds = std::min(seconds, *limit);
    const double time_base = av_q2d(m_impl->stream_tb);
    if (time_base <= 0.0) return m_impl->fail("video stream has an invalid time base");
    const auto timestamp = static_cast<std::int64_t>(seconds / time_base);
    const int  rc = av_seek_frame(m_impl->fmt, m_impl->video_idx, timestamp, AVSEEK_FLAG_BACKWARD);
    if (rc < 0) return m_impl->fail("av_seek_frame: " + av_err_str(rc));
    m_impl->reset_after_seek();
    return true;
}

auto VideoSource::duration() const -> std::optional<double> { return m_impl->duration(); }

auto VideoSource::stream_metadata() const -> VideoStreamMetadata {
    const AVStream*          stream     = m_impl->fmt->streams[m_impl->video_idx];
    const AVCodecParameters* parameters = stream->codecpar;
    VideoStreamMetadata      result;
    if (parameters->codec_id != AV_CODEC_ID_NONE) result.codec = avcodec_get_name(parameters->codec_id);
    if (parameters->width > 0) result.coded_width = static_cast<std::uint32_t>(parameters->width);
    if (parameters->height > 0) result.coded_height = static_cast<std::uint32_t>(parameters->height);
    if (parameters->format >= 0) {
        const char* pixel_format = av_get_pix_fmt_name(static_cast<AVPixelFormat>(parameters->format));
        if (pixel_format != nullptr) result.pixel_format = pixel_format;
    }
    // 声明的/平均帧率不能证明帧间隔恒定：保留有理数与来源，绝不拿采集帧率顶替。
    AVRational rate = stream->avg_frame_rate;
    if (rate.num > 0 && rate.den > 0) result.fps_source = "avg_frame_rate";
    else {
        rate = stream->r_frame_rate;
        if (rate.num > 0 && rate.den > 0) result.fps_source = "r_frame_rate";
    }
    if (rate.num > 0 && rate.den > 0) {
        result.fps_num = rate.num;
        result.fps_den = rate.den;
    }
    if (stream->duration > 0 && stream->duration != AV_NOPTS_VALUE && stream->time_base.num > 0 &&
        stream->time_base.den > 0) {
        result.duration_ticks = stream->duration;
        result.time_base_num  = stream->time_base.num;
        result.time_base_den  = stream->time_base.den;
    }
    if (stream->nb_frames > 0) result.frame_count = static_cast<std::uint64_t>(stream->nb_frames);
    return result;
}

auto VideoSource::decode_threads() const -> DecodeThreads {
    return { m_impl->cctx->thread_count, m_impl->cctx->active_thread_type };
}

auto VideoSource::last_error() const -> std::string_view { return m_impl->error; }

} // namespace owe::media
