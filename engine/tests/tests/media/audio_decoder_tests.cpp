// 移植自 wavsen tests/stream_decoder_rate_tests.cpp。
// 引擎不用变速，AudioDecoder 没有 set_playback_rate；原来按播放速率检查帧数比例，
// 这里改成按源采样率检查（同采样率逐帧不变、44.1k→48k 重采样后含 drain 的尾巴）。

#include <gtest/gtest.h>

#include <cstdio>

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
