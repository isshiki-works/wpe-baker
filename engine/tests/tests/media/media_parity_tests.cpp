// T5a 双实现对拍：wavsen 旧音频路径与 owe::media 新路径解同一批文件，PCM 逐字节比较。
// **T5b 删 wavsen 时连同 CMake 里的 media-parity-tests 目标一起删。**
//
// 夹具目录由环境变量 OWE_MEDIA_PARITY_DIR 指定（语料音频有版权，不进仓库）；
// T5a 用的是 D:/Periodica/runs/T5a/fixtures（格式清单见同目录 manifest.json）。
// 没设变量时整组跳过。

#include <gtest/gtest.h>

#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <filesystem>

import rstd;
import rstd.cppstd;
import owe.media;
import wavsen.audio;
import wescene.io;

using namespace rstd::prelude;

namespace
{

// 与 SoundParser.cpp 切换前的桥逐字一致：wavsen 收 rstd::io::ReadSeekHandle。
struct ParityRangeReader {
    owe::io::RangeReader reader;
};

auto ParityError() -> rstd::io::error::Error {
    return rstd::io::error::Error::from_kind(
        rstd::io::error::ErrorKind { rstd::io::error::ErrorKind::Other });
}

} // namespace

template<>
struct rstd::Impl<rstd::io::Read, ParityRangeReader> : rstd::ImplBase<ParityRangeReader> {
    auto read(rstd::mut_ref<u8[]> buf) -> rstd::io::Result<usize> {
        auto result = this->self().reader.read(reinterpret_cast<std::uint8_t*>(buf.as_raw_ptr()),
                                               buf.len().to_primitive());
        if (result.is_err()) return rstd::Err(ParityError());
        return rstd::Ok(usize(*result));
    }
};

template<>
struct rstd::Impl<rstd::io::Seek, ParityRangeReader> : rstd::ImplBase<ParityRangeReader> {
    auto seek(rstd::io::SeekFrom pos) -> rstd::io::Result<u64> {
        auto from = owe::io::SeekFrom::from_start(pos.start.to_primitive());
        if (pos.which == rstd::io::SeekFrom::Which::Current) {
            from = owe::io::SeekFrom::from_current(pos.offset.to_primitive());
        } else if (pos.which == rstd::io::SeekFrom::Which::End) {
            from = owe::io::SeekFrom::from_end(pos.offset.to_primitive());
        }
        auto result = this->self().reader.seek(from);
        if (result.is_err()) return rstd::Err(ParityError());
        return rstd::Ok(u64(*result));
    }
};

namespace
{

constexpr std::uint32_t kChannels   = 2;
constexpr std::uint32_t kSampleRate = 48000;

// 拉取块大小轮换：60/30/144 fps 的每帧样本数、单帧、大块。
constexpr std::uint32_t kChunks[] = { 800, 1600, 333, 334, 1, 4096, 800 };

auto Fixtures() -> std::vector<std::filesystem::path> {
    std::vector<std::filesystem::path> files;
    const char*                        dir = std::getenv("OWE_MEDIA_PARITY_DIR");
    if (dir == nullptr) return files;
    for (const auto& entry : std::filesystem::directory_iterator(dir)) {
        if (entry.is_regular_file() && entry.path().filename() != "manifest.json")
            files.push_back(entry.path());
    }
    std::sort(files.begin(), files.end());
    return files;
}

auto OpenReader(const std::filesystem::path& path) -> owe::io::RangeReader {
    auto range = owe::io::open_file_range(path.string());
    if (range.is_err()) {
        ADD_FAILURE() << "open " << path.string();
        std::abort();
    }
    return std::move(*range).into_reader();
}

auto OldStream(const std::filesystem::path& path) -> std::unique_ptr<wavsen::audio::SoundStream> {
    auto handle = rstd::io::ReadSeekHandle::make(ParityRangeReader { OpenReader(path) });
    return wavsen::audio::make_stream(rstd::move(handle),
                                      wavsen::audio::SoundStream::Desc { u32(kChannels), u32(kSampleRate) });
}

// 两边同步按块拉（引擎 SoundStream 的用法：第一次短读或出错后就不再用这个解码器），逐块比较；
// 不留整段 PCM，长文件（语料里有 1 小时的 mp3）也不占内存。
struct Lockstep {
    bool          opened_old {};
    bool          opened_new {};
    std::string   error_old;
    std::string   error_new;
    std::uint64_t frames_old {};
    std::uint64_t frames_new {};
    bool          same_bytes { true }; // 两边都输出了的帧逐字节相同
};

auto DecodeBoth(const std::filesystem::path& path) -> Lockstep {
    Lockstep                 out;
    auto                     old_stream = OldStream(path);
    owe::media::AudioDecoder decoder;
    out.opened_old = old_stream != nullptr;
    out.opened_new = decoder.open(OpenReader(path), owe::media::PcmDesc { kChannels, kSampleRate });
    if (! out.opened_old || ! out.opened_new) return out;

    std::vector<float> old_pcm;
    std::vector<float> new_pcm;
    for (std::size_t index = 0;; ++index) {
        const auto chunk = kChunks[index % std::size(kChunks)];
        old_pcm.assign(std::size_t(chunk) * kChannels, 0.0f);
        new_pcm.assign(std::size_t(chunk) * kChannels, 0.0f);
        const auto old_got = old_stream->next_pcm(old_pcm.data(), u32(chunk)).to_primitive();
        const auto new_got = decoder.next_pcm(new_pcm.data(), chunk);
        out.frames_old += old_got;
        out.frames_new += new_got;
        const auto common = std::min<std::uint64_t>(old_got, new_got);
        if (std::memcmp(old_pcm.data(), new_pcm.data(), std::size_t(common) * kChannels * sizeof(float)) != 0) {
            out.same_bytes = false;
            break;
        }
        if (auto error = old_stream->error(); error.is_some()) out.error_old = rstd::cppstd::to_string(*error);
        out.error_new = std::string(decoder.last_error());
        if (! out.error_old.empty() || ! out.error_new.empty() || new_got < chunk) break;
    }
    return out;
}

// 有意的不同：wavsen 在流末尾锁存解码错误，新实现正常读完；wavsen 报错前两边输出逐字节相同，
// 新实现多出的只是 wavsen 丢掉的最后几帧。
auto TailOnlyDifference(const Lockstep& r) -> bool {
    return r.opened_old && r.opened_new && ! r.error_old.empty() && r.error_new.empty() && r.same_bytes &&
           r.frames_new >= r.frames_old;
}

auto SameBytes(const std::vector<float>& a, const std::vector<float>& b) -> bool {
    return a.size() == b.size() && std::memcmp(a.data(), b.data(), a.size() * sizeof(float)) == 0;
}

// 新路径挂到混音器上的源：直接包 AudioDecoder（wavsen make_stream 的 DecoderStream 对应物）。
class DecoderSource final : public owe::media::PcmSource {
public:
    explicit DecoderSource(owe::media::AudioDecoder decoder): m_decoder(std::move(decoder)) {}
    auto next_pcm(float* dst, std::uint32_t frames) -> std::uint32_t override {
        return m_decoder.next_pcm(dst, frames);
    }
    void pass_desc(const owe::media::PcmDesc&) override {}
    auto last_error() const -> std::string_view override { return m_decoder.last_error(); }

private:
    owe::media::AudioDecoder m_decoder;
};

// 混音操作序列：两边照同一脚本走，逐次比较混音结果。
enum class Op
{
    Mix,
    Pause,
    Play,
    Mute,
    Unmute,
    Fade,
};

constexpr Op kScript[] = {
    Op::Mix, Op::Mix,  Op::Fade, Op::Mix,    Op::Mix, Op::Mix, Op::Mute, Op::Mix, Op::Unmute,
    Op::Mix, Op::Pause, Op::Mix, Op::Play,   Op::Mix, Op::Fade, Op::Mix, Op::Mix,
};

} // namespace

TEST(MediaParity, DecoderMatchesWavsenByteForByte) {
    const auto files = Fixtures();
    if (files.empty()) GTEST_SKIP() << "OWE_MEDIA_PARITY_DIR 未设置或为空";
    for (const auto& file : files) {
        const auto r    = DecodeBoth(file);
        const bool same = r.opened_old == r.opened_new && r.error_old == r.error_new &&
                          r.frames_old == r.frames_new && r.same_bytes;
        const bool tail = TailOnlyDifference(r);
        std::printf("PARITY decode %s opened=%d frames=%llu/%llu error=\"%s\"/\"%s\" %s\n",
                    file.filename().string().c_str(),
                    int(r.opened_new),
                    static_cast<unsigned long long>(r.frames_old),
                    static_cast<unsigned long long>(r.frames_new),
                    r.error_old.c_str(),
                    r.error_new.c_str(),
                    same ? "same" : tail ? "tail" : "DIFF");
        EXPECT_TRUE(same || tail) << file.string() << " opened " << r.opened_old << "/" << r.opened_new
                                  << " frames " << r.frames_old << "/" << r.frames_new << " error \""
                                  << r.error_old << "\" / \"" << r.error_new << "\"";
    }
}

// 每个文件与下一个文件同挂一台混音器（覆盖多源相加、音量、渐变、静音、暂停）。
TEST(MediaParity, MixerMatchesWavsenByteForByte) {
    const auto files = Fixtures();
    if (files.empty()) GTEST_SKIP() << "OWE_MEDIA_PARITY_DIR 未设置或为空";
    for (std::size_t index = 0; index < files.size(); ++index) {
        const auto& first  = files[index];
        const auto& second = files[(index + 1) % files.size()];

        wavsen::audio::SoundManager old_mixer;
        ASSERT_TRUE(old_mixer.configure_offline({ u32(kChannels), u32(kSampleRate) }).is_ok());
        old_mixer.set_volume(f32(0.8f));
        old_mixer.set_volume_scale(f32(0.6f), u32());
        owe::media::OfflineMixer new_mixer(owe::media::PcmDesc { kChannels, kSampleRate });
        new_mixer.set_volume(0.8f);
        new_mixer.set_volume_scale(0.6f, 0);
        for (const auto& path : { first, second }) {
            if (auto stream = OldStream(path)) old_mixer.mount(std::move(stream));
            owe::media::AudioDecoder decoder;
            if (decoder.open(OpenReader(path), owe::media::PcmDesc { kChannels, kSampleRate }))
                new_mixer.mount(std::make_unique<DecoderSource>(std::move(decoder)));
        }
        old_mixer.play();
        new_mixer.play();

        std::vector<float> old_pcm(std::size_t(800) * kChannels);
        std::vector<float> new_pcm(old_pcm.size());
        bool               same  = true;
        float              scale = 0.25f;
        for (const auto op : kScript) {
            switch (op) {
            case Op::Mix: {
                auto       old_ok = old_mixer.mix_pcm(
                    rstd::mut_ref<float[]>::from_raw_parts(old_pcm.data(), usize(old_pcm.size())));
                const bool new_ok = new_mixer.mix(new_pcm);
                same              = same && old_ok.is_ok() == new_ok && SameBytes(old_pcm, new_pcm);
                if (old_ok.is_err())
                    same = same && rstd::cppstd::to_string(old_ok.unwrap_err().as_str()) ==
                                       new_mixer.last_error();
                break;
            }
            case Op::Pause:
                old_mixer.pause();
                new_mixer.pause();
                break;
            case Op::Play:
                old_mixer.play();
                new_mixer.play();
                break;
            case Op::Mute:
                old_mixer.set_muted(true);
                new_mixer.set_muted(true);
                break;
            case Op::Unmute:
                old_mixer.set_muted(false);
                new_mixer.set_muted(false);
                break;
            case Op::Fade:
                old_mixer.set_volume_scale(f32(scale), u32(12));
                new_mixer.set_volume_scale(scale, 12);
                scale = 1.0f;
                break;
            }
        }
        std::printf("PARITY mix %s + %s %s\n",
                    first.filename().string().c_str(),
                    second.filename().string().c_str(),
                    same ? "same" : "DIFF");
        EXPECT_TRUE(same) << first.string() << " + " << second.string();
    }
}
