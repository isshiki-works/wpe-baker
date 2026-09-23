// 移植自 wavsen tests/stream_decoder_rate_tests.cpp。
// 引擎不用变速，AudioDecoder 没有 set_playback_rate；原来按播放速率检查帧数比例，
// 这里改成按源采样率检查（同采样率逐帧不变、44.1k→48k 重采样后含 drain 的尾巴）。
//
// 后半是流末尾解码错误的判定（AudioDecoder.cpp 的 defer_decode_error）：截断文件解到最后一个完整帧后
// 正常结束，帧数与 `ffmpeg -i x -f f32le`（ffmpeg 8.1.2，与引擎同一版 libavcodec）一致；
// 错误在文件中间、或一帧都没解出就出错，仍然报错。夹具在 tests/media/fixtures，都由 lavfi 正弦波生成：
//   ffmpeg -f lavfi -i sine=frequency=440:sample_rate=16000:duration=1 -ac 1 -c:a flac -sample_fmt s16
//     再去掉 8 KB 的 PADDING 元数据块 → 截断到 4424 字节 = tail-cut.flac（ffmpeg：11520 帧，1 次 Decoding error）
//                                   → 从第 2949 字节起清零 16 字节 = mid-corrupt.flac（ffmpeg：错一帧后继续解）
//   同一正弦 -c:a libmp3lame -b:a 32k → 截断到 3538 字节 = tail-cut.mp3（ffmpeg：12143 帧，1 次 Error submitting packet）

#include <gtest/gtest.h>

#include <cstdio>
#include <cstdlib>

import rstd.cppstd;
import owe.media;
import wescene.io;

namespace
{

void PushU16(std::vector<std::uint8_t>& out, std::uint16_t value) {
    out.push_back(std::uint8_t(value & 0xffu));
    out.push_back(std::uint8_t(value >> 8u));
}

void PushU32(std::vector<std::uint8_t>& out, std::uint32_t value) {
    PushU16(out, std::uint16_t(value & 0xffffu));
    PushU16(out, std::uint16_t(value >> 16u));
}

void PushTag(std::vector<std::uint8_t>& out, const char* tag) { out.insert(out.end(), tag, tag + 4); }

// 1 秒单声道 s16 静音 WAV。
auto PcmWav(std::uint32_t sample_rate) -> std::vector<std::uint8_t> {
    const std::uint32_t       data_size = sample_rate * 2;
    std::vector<std::uint8_t> bytes;
    PushTag(bytes, "RIFF");
    PushU32(bytes, 36 + data_size);
    PushTag(bytes, "WAVE");
    PushTag(bytes, "fmt ");
    PushU32(bytes, 16);
    PushU16(bytes, 1);
    PushU16(bytes, 1);
    PushU32(bytes, sample_rate);
    PushU32(bytes, sample_rate * 2);
    PushU16(bytes, 2);
    PushU16(bytes, 16);
    PushTag(bytes, "data");
    PushU32(bytes, data_size);
    bytes.resize(bytes.size() + data_size, 0);
    return bytes;
}

// 48 kHz 双声道 s16 WAV：frames 个完整帧后再跟 tail_bytes 个零字节（半帧），data 块长度按多 100 帧声明，
// 模拟下载/打包时被截断的文件。样本是确定的锯齿，左右声道不同。
auto TruncatedWav(std::uint32_t frames, std::uint32_t tail_bytes) -> std::vector<std::uint8_t> {
    const std::uint32_t       claimed = (frames + 100) * 4;
    std::vector<std::uint8_t> bytes;
    PushTag(bytes, "RIFF");
    PushU32(bytes, 36 + claimed);
    PushTag(bytes, "WAVE");
    PushTag(bytes, "fmt ");
    PushU32(bytes, 16);
    PushU16(bytes, 1);
    PushU16(bytes, 2);
    PushU32(bytes, 48000);
    PushU32(bytes, 48000 * 4);
    PushU16(bytes, 4);
    PushU16(bytes, 16);
    PushTag(bytes, "data");
    PushU32(bytes, claimed);
    for (std::uint32_t index = 0; index < frames; ++index) {
        const auto left = std::uint16_t((index * 37u) % 65536u - 32768u);
        PushU16(bytes, left);
        PushU16(bytes, std::uint16_t(~left));
    }
    bytes.resize(bytes.size() + tail_bytes, 0);
    return bytes;
}

auto WriteTemp(const std::string& name, const std::vector<std::uint8_t>& bytes) -> std::string {
    const auto  path = ::testing::TempDir() + name;
    std::FILE*  file = std::fopen(path.c_str(), "wb");
    EXPECT_NE(file, nullptr);
    std::fwrite(bytes.data(), 1, bytes.size(), file);
    std::fclose(file);
    return path;
}

auto OpenDecoder(owe::media::AudioDecoder& decoder, const std::string& path) -> bool {
    auto range = owe::io::open_file_range(path);
    if (range.is_err()) return false;
    return decoder.open(std::move(*range).into_reader(), owe::media::PcmDesc { 2, 48000 });
}

struct Decoded {
    bool          opened {};
    std::uint64_t frames {};
    std::string   error;
};

// 按引擎 SoundStream 的用法拉块，第一次短读即停。
auto DecodeFile(const std::string& path, owe::media::PcmDesc desc) -> Decoded {
    Decoded                  out;
    owe::media::AudioDecoder decoder;
    auto                     range = owe::io::open_file_range(path);
    if (range.is_err()) return out;
    out.opened = decoder.open(std::move(*range).into_reader(), desc);
    if (! out.opened) return out;
    std::vector<float> samples(std::size_t(1024) * desc.channels);
    for (;;) {
        const auto count = decoder.next_pcm(samples.data(), 1024);
        out.frames += count;
        if (count < 1024) break;
    }
    out.error = std::string(decoder.last_error());
    return out;
}

auto Fixture(const char* name) -> std::string { return std::string(OWE_MEDIA_FIXTURE_DIR) + "/" + name; }

auto DecodedFrames(std::uint32_t source_rate) -> std::uint64_t {
    owe::media::AudioDecoder decoder;
    const auto path = WriteTemp("owe-media-" + std::to_string(source_rate) + ".wav", PcmWav(source_rate));
    if (! OpenDecoder(decoder, path)) return 0;
    std::array<float, 2048> samples {};
    std::uint64_t           total {};
    for (int iteration = 0; iteration < 200; ++iteration) {
        const auto count = decoder.next_pcm(samples.data(), 1024);
        total += count;
        if (count < 1024) break;
    }
    EXPECT_TRUE(decoder.last_error().empty());
    return total;
}

} // namespace

TEST(AudioDecoder, SameRateKeepsEveryFrame) { EXPECT_EQ(DecodedFrames(48000), 48000u); }

// 1 秒 44.1k 重采样到 48k 恰好 48000 帧；少了 EOF 时的 drain 就会缺重采样滤波器里缓着的尾巴。
TEST(AudioDecoder, ResamplesAndDrainsTail) { EXPECT_EQ(DecodedFrames(44100), 48000u); }

TEST(AudioDecoder, RejectsNonAudioInput) {
    owe::media::AudioDecoder decoder;
    const auto path = WriteTemp("owe-media-garbage.bin", std::vector<std::uint8_t>(4096, 0x5a));
    EXPECT_FALSE(OpenDecoder(decoder, path));
    EXPECT_FALSE(decoder.last_error().empty());
}

// 截断在半帧：最后一个包解完整帧后剩 2 字节，avcodec_receive_frame 报 Invalid data。ffmpeg 输出 1000 帧。
TEST(AudioDecoderTail, TruncatedWavEndsAtLastWholeFrame) {
    const auto path = WriteTemp("owe-media-tail.wav", TruncatedWav(1000, 2));
    const auto got  = DecodeFile(path, { 2, 48000 });
    ASSERT_TRUE(got.opened);
    EXPECT_EQ(got.error, "");
    EXPECT_EQ(got.frames, 1000u);
}

// 最后一帧只剩半截（残差不全），单线程解码器在 avcodec_send_packet 报错。
TEST(AudioDecoderTail, TruncatedFlacEndsAtLastWholeFrame) {
    const auto got = DecodeFile(Fixture("tail-cut.flac"), { 1, 16000 });
    ASSERT_TRUE(got.opened);
    EXPECT_EQ(got.error, "");
    EXPECT_EQ(got.frames, 11520u);
}

// 最后一个包只剩下一帧的开头几个字节，avcodec_send_packet 报错。
TEST(AudioDecoderTail, TruncatedMp3EndsAtLastWholeFrame) {
    const auto got = DecodeFile(Fixture("tail-cut.mp3"), { 1, 16000 });
    ASSERT_TRUE(got.opened);
    EXPECT_EQ(got.error, "");
    EXPECT_EQ(got.frames, 12143u);
}

// 出错的包后面还有包：不是流末尾，照旧锁存，且停在出错处。
TEST(AudioDecoderTail, MidStreamErrorStillFails) {
    const auto got = DecodeFile(Fixture("mid-corrupt.flac"), { 1, 16000 });
    ASSERT_TRUE(got.opened);
    EXPECT_NE(got.error.find("Invalid data"), std::string::npos) << got.error;
    EXPECT_LT(got.frames, 14848u);
}

// 一帧都没解出就出错（data 只有半帧）：文件整个不可解，仍然报错。ffmpeg 对它也以 69 退出。
TEST(AudioDecoderTail, ErrorBeforeAnyFrameStillFails) {
    const auto path = WriteTemp("owe-media-half-frame.wav", TruncatedWav(0, 2));
    const auto got  = DecodeFile(path, { 2, 48000 });
    ASSERT_TRUE(got.opened);
    EXPECT_NE(got.error.find("Invalid data"), std::string::npos) << got.error;
    EXPECT_EQ(got.frames, 0u);
}

// 语料文件（有版权，不进仓库）：OWE_MEDIA_FFMPEG_FRAMES 指向一张表，每行 `路径<TAB>声道<TAB>采样率<TAB>ffmpeg 帧数`，
// 按源格式解码，要求不报错且帧数与 ffmpeg 命令行一致。没设变量时跳过。
TEST(AudioDecoderTail, CorpusFilesMatchFfmpegFrameCounts) {
    const char* list = std::getenv("OWE_MEDIA_FFMPEG_FRAMES");
    if (list == nullptr) GTEST_SKIP() << "OWE_MEDIA_FFMPEG_FRAMES 未设置";
    std::FILE* file = std::fopen(list, "rb");
    ASSERT_NE(file, nullptr) << list;
    char line[4096];
    int  checked = 0;
    while (std::fgets(line, sizeof(line), file) != nullptr) {
        std::string              text(line);
        std::vector<std::string> fields;
        std::size_t              begin = 0;
        for (;;) {
            const auto tab = text.find('\t', begin);
            fields.push_back(text.substr(begin, tab == std::string::npos ? std::string::npos : tab - begin));
            if (tab == std::string::npos) break;
            begin = tab + 1;
        }
        if (fields.size() != 4) continue;
        const auto channels = std::uint32_t(std::stoul(fields[1]));
        const auto rate     = std::uint32_t(std::stoul(fields[2]));
        const auto expected = std::stoull(fields[3]);
        const auto got      = DecodeFile(fields[0], { channels, rate });
        std::printf("TAIL %s frames=%llu ffmpeg=%llu error=\"%s\"\n",
                    fields[0].c_str(),
                    static_cast<unsigned long long>(got.frames),
                    static_cast<unsigned long long>(expected),
                    got.error.c_str());
        EXPECT_TRUE(got.opened) << fields[0];
        EXPECT_EQ(got.error, "") << fields[0];
        EXPECT_EQ(got.frames, expected) << fields[0];
        ++checked;
    }
    std::fclose(file);
    EXPECT_GT(checked, 0);
}
