// VideoSource 的流末尾解码错误判定（与 AudioDecoder 共用 Media.cppm 的 detail::DecodeTailGate）：
// 最后一个包坏了，解到最后一个完整帧后照常回到开头循环，每轮帧数与 `ffmpeg -i x -f framecrc -`
// （ffmpeg 8.1.2，与引擎同一版 libavcodec）一致；错误在流中间照旧报错，停在出错处。
// 解码线程数两种设置（不设 = libavcodec 自动、WAVSEN_VIDEO_DECODE_THREADS=1）各跑一遍。
// 夹具在 tests/media/fixtures，都由 lavfi testsrc2（64x48、10 fps）生成：
//   1.6 s -c:v libx264 -g 16 -bf 0 -movflags +faststart
//     → 截断到 3262 字节（最后一个包 50 字节只剩 20）= tail-cut.mp4（ffmpeg：15 帧，Invalid NAL unit size）
//   1.6 s -c:v libvpx-vp9 -g 16 -b:v 100k
//     → 最后一个包的帧数据开头 4 字节清零 = tail-corrupt.webm（ffmpeg：15 帧，Failed to read frame header）
//   4.8 s 同上（-g 48）→ 第 9 个包（pts 0.8 s）的帧数据开头 4 字节清零 = mid-corrupt.webm
//     （出错处离结尾 39 个包，多于自动线程数，两种线程设置下都在读到后面的包时才判定）

#include <gtest/gtest.h>

#include <cstdlib>

import rstd.cppstd;
import owe.media;
import wescene.io;

namespace
{

auto Fixture(const char* name) -> std::string { return std::string(OWE_MEDIA_FIXTURE_DIR) + "/" + name; }

auto Open(owe::media::VideoSource& source, const char* name) -> bool {
    auto range = owe::io::open_file_range(Fixture(name));
    if (range.is_err()) return false;
    return source.open(std::move(*range).into_reader(), 64, 48);
}

struct Cycle {
    std::uint32_t frames {};
    bool          looped {};
    std::string   error;
};

// 连续取帧，直到回到开头（Looped 那一帧属于下一轮，不计入）或出错。
auto RunCycle(owe::media::VideoSource& source) -> Cycle {
    Cycle                 cycle;
    owe::media::Nv12Frame frame;
    while (cycle.frames < 1000) {
        const auto next = source.next_frame(frame);
        if (! next) {
            cycle.error = std::string(source.last_error());
            break;
        }
        if (*next == owe::media::NextFrame::Looped) {
            cycle.looped = true;
            break;
        }
        ++cycle.frames;
    }
    return cycle;
}

// 参数是 WAVSEN_VIDEO_DECODE_THREADS 的值（空串 = 不设）；VideoSource::open 时读取。
class VideoSourceTail : public ::testing::TestWithParam<const char*> {
protected:
    void SetUp() override { _putenv_s("WAVSEN_VIDEO_DECODE_THREADS", GetParam()); }
    void TearDown() override { _putenv_s("WAVSEN_VIDEO_DECODE_THREADS", ""); }

    // 第一轮 frames 帧后循环；第二轮从 Looped 那一帧之后再解 frames - 1 帧又循环（回到开头后暂缓的错误要作废）。
    static void ExpectLoops(const char* name, std::uint32_t frames) {
        owe::media::VideoSource source;
        ASSERT_TRUE(Open(source, name)) << source.last_error();
        const auto first = RunCycle(source);
        EXPECT_EQ(first.error, "");
        EXPECT_TRUE(first.looped);
        EXPECT_EQ(first.frames, frames);
        const auto second = RunCycle(source);
        EXPECT_EQ(second.error, "");
        EXPECT_TRUE(second.looped);
        EXPECT_EQ(second.frames, frames - 1);
    }
};

} // namespace

// 下载不完整的 mp4：最后一个包只剩开头，h264 报 Invalid NAL unit size。
TEST_P(VideoSourceTail, TruncatedH264LoopsAfterLastWholeFrame) { ExpectLoops("tail-cut.mp4", 15); }

// 最后一帧的帧头坏了，vp9 报 Invalid data。
TEST_P(VideoSourceTail, CorruptLastVp9FrameLoops) { ExpectLoops("tail-corrupt.webm", 15); }

// 出错的包后面还有包：不是流末尾，照旧报错，且停在出错处（前 8 帧照常输出）。
TEST_P(VideoSourceTail, MidStreamErrorStillFails) {
    owe::media::VideoSource source;
    ASSERT_TRUE(Open(source, "mid-corrupt.webm")) << source.last_error();
    const auto cycle = RunCycle(source);
    EXPECT_FALSE(cycle.looped);
    EXPECT_NE(cycle.error.find("Invalid data"), std::string::npos) << cycle.error;
    EXPECT_EQ(cycle.frames, 8u);
}

INSTANTIATE_TEST_SUITE_P(Threads, VideoSourceTail, ::testing::Values("", "1"));
