module;
// make_unique 之前先包含 <new>，避开 clang 22 的 operator new 歧义。
#include <new>

extern "C" {
#include <libavcodec/avcodec.h>
#include <libavformat/avformat.h>
#include <libswresample/swresample.h>
}

module owe.media;

import rstd.cppstd;
import wescene.io;

// 逐语句照搬 wavsen src/audio/file.cpp 的离线路径（不含 seek、变速、retarget：引擎不用）。
// 对拍测试 media-parity-tests 逐字节比对两边输出。
namespace owe::media
{

namespace
{

constexpr std::size_t kAvioBuf = 32 * 1024;

int avio_read_cb(void* opaque, std::uint8_t* buf, int size) {
    auto* source = static_cast<owe::io::RangeReader*>(opaque);
    auto  result = source->read(buf, static_cast<std::size_t>(size));
    if (result.is_err()) return AVERROR(EIO);
    auto count = *result;
    if (count == 0) return AVERROR_EOF;
    return static_cast<int>(count);
}

std::int64_t avio_seek_cb(void* opaque, std::int64_t offset, int whence) {
    auto* source = static_cast<owe::io::RangeReader*>(opaque);
    if (whence == AVSEEK_SIZE) return -1;
    auto from = whence == SEEK_SET   ? owe::io::SeekFrom::from_start(static_cast<std::uint64_t>(offset))
                : whence == SEEK_CUR ? owe::io::SeekFrom::from_current(offset)
                                     : owe::io::SeekFrom::from_end(offset);
    auto result = source->seek(from);
    if (result.is_err()) return -1;
    return static_cast<std::int64_t>(*result);
}

} // namespace

struct AudioDecoder::Impl {
    explicit Impl(owe::io::RangeReader reader): source(std::move(reader)) {}
    Impl(const Impl&)                    = delete;
    auto operator=(const Impl&) -> Impl& = delete;

    ~Impl() {
        if (swr) swr_free(&swr);
        if (frame) av_frame_free(&frame);
        if (packet) av_packet_free(&packet);
        if (codec) avcodec_free_context(&codec);
        if (format) avformat_close_input(&format);
        if (avio) {
            av_freep(&avio->buffer);
            avio_context_free(&avio);
        }
    }

    auto open(PcmDesc desc) -> bool {
        target       = desc;
        auto* buffer = static_cast<std::uint8_t*>(av_malloc(kAvioBuf));
        if (! buffer) return fail_open("av_malloc avio buffer failed");
        avio = avio_alloc_context(buffer, kAvioBuf, 0, &source, &avio_read_cb, nullptr, &avio_seek_cb);
        if (! avio) {
            av_free(buffer);
            return fail_open("avio_alloc_context failed");
        }

        format     = avformat_alloc_context();
        format->pb = avio;
        format->flags |= AVFMT_FLAG_CUSTOM_IO;
        if (avformat_open_input(&format, nullptr, nullptr, nullptr) != 0) {
            format = nullptr; // 失败时 avformat_open_input 已释放上下文
            return fail_open("avformat_open_input failed");
        }
        if (avformat_find_stream_info(format, nullptr) < 0)
            return fail_open("avformat_find_stream_info failed");

        const AVCodec* decoder = nullptr;
        stream = av_find_best_stream(format, AVMEDIA_TYPE_AUDIO, -1, -1, &decoder, 0);
        if (stream < 0 || ! decoder) return fail_open("no audio stream / codec");

        codec = avcodec_alloc_context3(decoder);
        if (! codec) return fail_open("avcodec_alloc_context3 failed");
        if (avcodec_parameters_to_context(codec, format->streams[stream]->codecpar) < 0)
            return fail_open("avcodec_parameters_to_context failed");
        codec->pkt_timebase = format->streams[stream]->time_base;
        if (avcodec_open2(codec, decoder, nullptr) < 0) return fail_open("avcodec_open2 failed");

        packet = av_packet_alloc();
        frame  = av_frame_alloc();
        if (! packet || ! frame) return fail_open("alloc packet/frame failed");
        return build_resampler();
    }

    auto next_pcm(float* dst, std::uint32_t frames) -> std::uint32_t {
        if (! error.empty()) return 0;

        auto*      out      = reinterpret_cast<std::uint8_t*>(dst);
        const auto bps      = sizeof(float) * std::size_t(target.channels);
        auto       produced = std::uint32_t {};

        while (produced < frames) {
            // 先交出上一轮重采样剩下的帧。
            if (pending_frames > 0) {
                const auto take = std::min(frames - produced, pending_frames);
                std::memcpy(out + std::size_t(produced) * bps,
                            pending.data() + std::size_t(pending_offset) * bps,
                            std::size_t(take) * bps);
                produced += take;
                pending_offset += take;
                pending_frames -= take;
                continue;
            }

            if (! pull_decoded_frame()) {
                // 读完后把 swr 里缓着的尾巴冲出来（只冲一次）。
                if (error.empty() && eof && ! drained) {
                    drained            = true;
                    const int capacity = swr_get_out_samples(swr, 0);
                    if (capacity > 0) {
                        pending.resize(std::size_t(capacity) * bps, 0);
                        auto*     out_ptr   = pending.data();
                        const int converted = swr_convert(swr, &out_ptr, capacity, nullptr, 0);
                        if (converted < 0) {
                            fail("swr_convert(flush)", converted);
                            break;
                        }
                        pending_offset = 0;
                        pending_frames = static_cast<std::uint32_t>(converted);
                        if (converted > 0) continue;
                    }
                }
                break;
            }

            const std::int64_t max_out = swr_get_out_samples(swr, frame->nb_samples);
            if (max_out <= 0) continue;
            const auto need = static_cast<std::size_t>(max_out) * bps;
            if (pending.size() < need) pending.resize(need, 0);
            auto*     out_ptr   = pending.data();
            const int converted = swr_convert(swr,
                                              &out_ptr,
                                              static_cast<int>(max_out),
                                              const_cast<const std::uint8_t**>(frame->extended_data),
                                              frame->nb_samples);
            if (converted < 0) {
                fail("swr_convert", converted);
                break;
            }
            pending_offset = 0;
            pending_frames = static_cast<std::uint32_t>(converted);
        }
        return produced;
    }

    auto fail_open(const char* message) -> bool {
        error = message;
        return false;
    }

    void fail(const char* operation, int code) {
        char text[AV_ERROR_MAX_STRING_SIZE] {};
        av_strerror(code, text, sizeof(text));
        error = std::string(operation) + ": " + text;
    }

    // 取下一帧解码结果；读完或出错返回 false（eof / error 记在成员里）。
    auto pull_decoded_frame() -> bool {
        for (;;) {
            int code = avcodec_receive_frame(codec, frame);
            if (code == 0) return true;
            if (code == AVERROR(EAGAIN)) {
                code = av_read_frame(format, packet);
                if (code == AVERROR_EOF) {
                    avcodec_send_packet(codec, nullptr); // 进入冲洗
                    eof = true;
                    continue;
                }
                if (code < 0) {
                    fail("av_read_frame", code);
                    return false;
                }
                if (packet->stream_index != stream) {
                    av_packet_unref(packet);
                    continue;
                }
                const int sent = avcodec_send_packet(codec, packet);
                av_packet_unref(packet);
                if (sent < 0) {
                    fail("avcodec_send_packet", sent);
                    return false;
                }
                continue;
            }
            if (code == AVERROR_EOF) {
                eof = true;
                return false;
            }
            fail("avcodec_receive_frame", code);
            return false;
        }
    }

    auto build_resampler() -> bool {
        AVChannelLayout out_layout {};
        av_channel_layout_default(&out_layout, static_cast<int>(target.channels));
        AVChannelLayout in_layout {};
        if (codec->ch_layout.order != AV_CHANNEL_ORDER_UNSPEC)
            av_channel_layout_copy(&in_layout, &codec->ch_layout);
        else
            av_channel_layout_default(&in_layout, codec->ch_layout.nb_channels);

        // wavsen 按 sample_rate * 播放速率 + 0.5 取整；速率恒为 1，结果就是源采样率。
        const int allocated = swr_alloc_set_opts2(&swr,
                                                  &out_layout,
                                                  AV_SAMPLE_FMT_FLT,
                                                  static_cast<int>(target.sample_rate),
                                                  &in_layout,
                                                  codec->sample_fmt,
                                                  codec->sample_rate,
                                                  0,
                                                  nullptr);
        av_channel_layout_uninit(&out_layout);
        av_channel_layout_uninit(&in_layout);
        if (allocated < 0) return fail_open("swr_alloc_set_opts2 failed");
        if (swr_init(swr) < 0) return fail_open("swr_init failed");
        return true;
    }

    owe::io::RangeReader      source;
    PcmDesc                   target;
    AVIOContext*              avio {};
    AVFormatContext*          format {};
    AVCodecContext*           codec {};
    SwrContext*               swr {};
    AVPacket*                 packet {};
    AVFrame*                  frame {};
    int                       stream { -1 };
    std::vector<std::uint8_t> pending;
    std::uint32_t             pending_offset {};
    std::uint32_t             pending_frames {};
    bool                      eof {};
    bool                      drained {};
    std::string               error;
};

AudioDecoder::AudioDecoder()                                     = default;
AudioDecoder::~AudioDecoder()                                    = default;
AudioDecoder::AudioDecoder(AudioDecoder&&) noexcept              = default;
auto AudioDecoder::operator=(AudioDecoder&&) noexcept -> AudioDecoder& = default;

auto AudioDecoder::open(owe::io::RangeReader source, PcmDesc target) -> bool {
    m_impl = std::make_unique<Impl>(std::move(source));
    return m_impl->open(target);
}

auto AudioDecoder::next_pcm(float* dst, std::uint32_t frames) -> std::uint32_t {
    return m_impl->next_pcm(dst, frames);
}

auto AudioDecoder::last_error() const -> std::string_view { return m_impl->error; }

} // namespace owe::media
