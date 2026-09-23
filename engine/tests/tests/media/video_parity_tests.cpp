// T5b 双实现对拍：wavsen 旧视频解码（open_from_stream 不带 Producer，即引擎用的软件解码）与
// owe::media::VideoSource 解同一批视频，逐帧比较 NV12 字节、PTS、色彩编号、回绕状态，
// 以及打开结果、时长、流元数据、错误文案。
// **T5b 删 wavsen 时连同 CMake 里的 video-parity-tests 目标一起删。**
//
// 夹具目录由环境变量 OWE_VIDEO_PARITY_DIR 指定（视频有版权，不进仓库）；目录里的 manifest.tsv
// 每行"文件名 纹理宽 纹理高"，宽高即引擎传给解码器的目标尺寸。没设变量时整组跳过。
// 解码线程数两边都读 WAVSEN_VIDEO_DECODE_THREADS，设与不设各跑一遍。

#include <gtest/gtest.h>

#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <fstream>
#include <string>
#include <vector>

import rstd;
import rstd.cppstd;
import owe.media;
import wavsen.video;
import wescene.io;

using namespace rstd::prelude;

namespace
{

// 与 TextureCache.cpp 切换前的 RangeInputStream 逐字一致。
class RangeInputStream {
public:
    explicit RangeInputStream(owe::io::ReadRange source)
        : m_length(static_cast<rstd::int64_t>(source.len())),
          m_reader(std::move(source).into_reader()) {}

    int read(rstd::uint8_t* buffer, int size) {
        if (size <= 0) return 0;
        auto result = m_reader.read(buffer, static_cast<std::size_t>(size));
        return result.is_ok() ? static_cast<int>(*result) : -1;
    }

    rstd::int64_t seek(rstd::int64_t offset, int whence) {
        constexpr int AVSEEK_SIZE = 0x10000;
        if (whence == AVSEEK_SIZE) return m_length;
        owe::io::SeekFrom from;
        switch (whence) {
        case 0:
            if (offset < 0) return -1;
            from = owe::io::SeekFrom::from_start(static_cast<std::uint64_t>(offset));
            break;
        case 1: from = owe::io::SeekFrom::from_current(offset); break;
        case 2: from = owe::io::SeekFrom::from_end(offset); break;
        default: return -1;
        }
        auto result = m_reader.seek(from);
        return result.is_ok() ? static_cast<rstd::int64_t>(*result) : -1;
    }

private:
    rstd::int64_t        m_length { 0 };
    owe::io::RangeReader m_reader;
};

struct Fixture {
    std::string   file;
    std::uint32_t width {};
    std::uint32_t height {};
};

auto Fixtures(std::string& dir) -> std::vector<Fixture> {
    std::vector<Fixture> out;
    const char*          env = std::getenv("OWE_VIDEO_PARITY_DIR");
    if (env == nullptr) return out;
    dir = env;
    std::ifstream in(dir + "/manifest.tsv");
    Fixture       f;
    while (in >> f.file >> f.width >> f.height) out.push_back(f);
    return out;
}

// 一次拉帧的结果：-1 出错，0 普通帧，1 回绕后的第一帧。
struct Pull {
    int                       status {};
    std::string               error;
    std::vector<std::uint8_t> data;
    std::uint32_t             width {}, height {};
    double                    pts {};
    std::uint32_t             colorspace {}, color_range {};
};

struct Pair {
    Box<wavsen::video::VideoDecoder> old_decoder;
    owe::media::VideoSource          new_decoder;
    wavsen::video::Nv12Frame         old_reused;
    owe::media::Nv12Frame            new_reused;

    auto pull_old(bool reuse) -> Pull {
        wavsen::video::Nv12Frame fresh;
        auto&                    frame = reuse ? old_reused : fresh;
        auto                     r     = old_decoder->next_frame(frame);
        Pull                     p;
        if (r.is_err()) {
            p.status = -1;
            p.error  = rstd::cppstd::to_string(r.unwrap_err().message.as_str());
            return p;
        }
        EXPECT_NE(*r, wavsen::video::NextFrame::Eof);
        p.status      = *r == wavsen::video::NextFrame::Looped ? 1 : 0;
        p.data.assign(frame.data.data(), frame.data.data() + frame.data.len().to_primitive());
        p.width       = frame.width.to_primitive();
        p.height      = frame.height.to_primitive();
        p.pts         = frame.pts_seconds.to_primitive();
        p.colorspace  = frame.colorspace.to_primitive();
        p.color_range = frame.color_range.to_primitive();
        return p;
    }

    auto pull_new(bool reuse) -> Pull {
        owe::media::Nv12Frame fresh;
        auto&                 frame = reuse ? new_reused : fresh;
        auto                  r     = new_decoder.next_frame(frame);
        Pull                  p;
        if (! r) {
            p.status = -1;
            p.error  = std::string(new_decoder.last_error());
            return p;
        }
        p.status      = *r == owe::media::NextFrame::Looped ? 1 : 0;
        p.data        = frame.data;
        p.width       = frame.width;
        p.height      = frame.height;
        p.pts         = frame.pts_seconds;
        p.colorspace  = frame.colorspace;
        p.color_range = frame.color_range;
        return p;
    }

    int frames {}, loops {}, errors {};

    // 两边各拉一帧并比较；返回状态（-1/0/1）。
    auto step(const std::string& where, bool reuse) -> int {
        auto a = pull_old(reuse);
        auto b = pull_new(reuse);
        ++(a.status < 0 ? errors : frames);
        if (a.status == 1) ++loops;
        EXPECT_EQ(a.status, b.status) << where;
        EXPECT_EQ(a.error, b.error) << where;
        EXPECT_EQ(a.width, b.width) << where;
        EXPECT_EQ(a.height, b.height) << where;
        EXPECT_EQ(std::memcmp(&a.pts, &b.pts, sizeof(double)), 0) << where << " pts " << a.pts << " vs " << b.pts;
        EXPECT_EQ(a.colorspace, b.colorspace) << where;
        EXPECT_EQ(a.color_range, b.color_range) << where;
        EXPECT_EQ(a.data.size(), b.data.size()) << where;
        if (a.data.size() == b.data.size() && a.data != b.data) {
            std::size_t i = 0;
            while (a.data[i] == b.data[i]) ++i;
            ADD_FAILURE() << where << " NV12 first mismatch at byte " << i;
        }
        return a.status;
    }

    void seek(const std::string& where, double seconds) {
        auto        a       = old_decoder->seek(f64(seconds));
        const bool  b       = new_decoder.seek(seconds);
        std::string a_error = a.is_err() ? rstd::cppstd::to_string(a.unwrap_err().message.as_str()) : "";
        EXPECT_EQ(a.is_ok(), b) << where;
        EXPECT_EQ(a_error, b ? std::string() : std::string(new_decoder.last_error())) << where;
    }
};

template<typename T, typename U>
void ExpectSameOption(const Option<T>& a, const std::optional<U>& b, const char* what) {
    EXPECT_EQ(a.is_some(), b.has_value()) << what;
    if (a.is_some() && b.has_value()) EXPECT_EQ((*a).to_primitive(), *b) << what;
}

} // namespace

TEST(VideoParity, DecodeSeekLoopMetadata) {
    std::string dir;
    const auto  fixtures = Fixtures(dir);
    if (fixtures.empty()) GTEST_SKIP() << "OWE_VIDEO_PARITY_DIR not set";

    for (const auto& fx : fixtures) {
        SCOPED_TRACE(fx.file + " " + std::to_string(fx.width) + "x" + std::to_string(fx.height));
        auto range = owe::io::open_file_range(dir + "/" + fx.file);
        ASSERT_TRUE(range.is_ok());
        auto source = std::move(*range);

        auto factory = Box<dyn<FnMut<Box<dyn<wavsen::video::InputStream>>()>>>::make(
            [source = source.clone()]() -> Box<dyn<wavsen::video::InputStream>> {
                return Box<dyn<wavsen::video::InputStream>>::make(RangeInputStream(source.clone()));
            });
        auto old_open = wavsen::video::VideoDecoder::open_from_stream(
            std::move(factory), u32(fx.width), u32(fx.height), true, nullptr,
            wavsen::video::OpenOpts { wavsen::video::HwAccel::None, String {} });
        owe::media::VideoSource new_decoder;
        const bool              new_ok = new_decoder.open(source.clone().into_reader(), fx.width, fx.height);
        ASSERT_EQ(old_open.is_ok(), new_ok);
        if (! new_ok) {
            EXPECT_EQ(rstd::cppstd::to_string(old_open.unwrap_err().message.as_str()), new_decoder.last_error());
            std::printf("parity %s %ux%u: open failed: %s\n", fx.file.c_str(), fx.width, fx.height,
                        std::string(new_decoder.last_error()).c_str());
            continue;
        }
        Pair pair { std::move(old_open).unwrap(), std::move(new_decoder), {}, {} };

        // 时长与元数据（runtime_video_decoders、PublishPeriodMetadata 都从这里来）。
        const auto old_duration = pair.old_decoder->duration();
        const auto new_duration = pair.new_decoder.duration();
        ASSERT_EQ(old_duration.is_some(), new_duration.has_value());
        const double duration = new_duration.value_or(0.0);
        if (new_duration) {
            const double old_value = (*old_duration).to_primitive();
            EXPECT_EQ(std::memcmp(&duration, &old_value, sizeof(double)), 0);
        }
        const auto om = pair.old_decoder->stream_metadata();
        const auto nm = pair.new_decoder.stream_metadata();
        EXPECT_EQ(rstd::cppstd::to_string(om.codec.as_str()), nm.codec);
        EXPECT_EQ(rstd::cppstd::to_string(om.pixel_format.as_str()), nm.pixel_format);
        EXPECT_EQ(rstd::cppstd::to_string(om.fps_source.as_str()), nm.fps_source);
        ExpectSameOption(om.coded_width, nm.coded_width, "coded_width");
        ExpectSameOption(om.coded_height, nm.coded_height, "coded_height");
        ExpectSameOption(om.fps_num, nm.fps_num, "fps_num");
        ExpectSameOption(om.fps_den, nm.fps_den, "fps_den");
        EXPECT_EQ(om.duration_ticks.is_some(), nm.duration_ticks.has_value());
        if (nm.duration_ticks) EXPECT_EQ(*om.duration_ticks, *nm.duration_ticks);
        ExpectSameOption(om.time_base_num, nm.time_base_num, "time_base_num");
        ExpectSameOption(om.time_base_den, nm.time_base_den, "time_base_den");
        ExpectSameOption(om.frame_count, nm.frame_count, "frame_count");

        // 1) 从头顺序拉（复用缓冲 = 实时路径的用法）。
        for (int i = 0; i < 45; ++i) pair.step("head#" + std::to_string(i), true);
        // 2) 跳到末尾前 0.4 s，拉过回绕点（每帧新缓冲 = 离线路径的用法），回绕后再拉 10 帧。
        pair.seek("seek-tail", std::max(0.0, duration - 0.4));
        for (int i = 0, after = -1; i < 400 && after < 10; ++i) {
            const int s = pair.step("tail#" + std::to_string(i), false);
            if (s == 1 && after < 0) after = 0;
            if (after >= 0) ++after;
        }
        // 3) 中点、超出时长（按时长夹住）、非法时间。
        pair.seek("seek-mid", duration / 2);
        for (int i = 0; i < 10; ++i) pair.step("mid#" + std::to_string(i), false);
        pair.seek("seek-past-end", duration + 1000.0);
        for (int i = 0; i < 10; ++i) pair.step("past#" + std::to_string(i), false);
        pair.seek("seek-negative", -1.0);
        pair.seek("seek-nan", std::nan(""));
        for (int i = 0; i < 3; ++i) pair.step("after-bad-seek#" + std::to_string(i), false);
        std::printf("parity %s %ux%u: %s %s duration=%.6f frames=%d loops=%d errors=%d\n", fx.file.c_str(),
                    fx.width, fx.height, nm.codec.c_str(), nm.pixel_format.c_str(), duration, pair.frames, pair.loops,
                    pair.errors);
    }
}
