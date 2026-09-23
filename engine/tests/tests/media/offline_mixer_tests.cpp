// 移植自 wavsen tests/offline_audio_tests.cpp（去掉设备激活与 shutdown：OfflineMixer 没有这两项）。

#include <gtest/gtest.h>

import rstd.cppstd;
import owe.media;

namespace
{

class ConstantSource final : public owe::media::PcmSource {
public:
    explicit ConstantSource(float value): m_value(value) {}

    auto next_pcm(float* dst, std::uint32_t frames) -> std::uint32_t override {
        for (std::uint32_t index = 0; index < frames * m_channels; ++index) dst[index] = m_value;
        consumed += frames;
        return frames;
    }
    void pass_desc(const owe::media::PcmDesc& desc) override { m_channels = desc.channels; }
    auto last_error() const -> std::string_view override {
        return fail ? "synthetic decode failure" : std::string_view {};
    }

    std::uint64_t consumed {};
    bool          fail {};

private:
    float         m_value;
    std::uint32_t m_channels {};
};

} // namespace

TEST(OfflineMixer, MixesMountedSourcesWithPauseMuteAndErrors) {
    owe::media::OfflineMixer mixer;
    auto                     first    = std::make_unique<ConstantSource>(0.25f);
    auto*                    observed = first.get();
    mixer.mount(std::move(first));
    mixer.mount(std::make_unique<ConstantSource>(0.125f));

    std::array<float, 256> samples {};
    // 未 play：输出静音且不拉源。
    ASSERT_TRUE(mixer.mix(samples));
    EXPECT_EQ(observed->consumed, 0u);

    mixer.play();
    ASSERT_TRUE(mixer.mix(samples));
    for (float sample : samples) EXPECT_EQ(sample, 0.375f);

    // 静音只清增益，解码时钟照走。
    mixer.set_muted(true);
    ASSERT_TRUE(mixer.mix(samples));
    EXPECT_EQ(observed->consumed, 256u);
    for (float sample : samples) EXPECT_EQ(sample, 0.0f);

    mixer.pause();
    ASSERT_TRUE(mixer.mix(samples));
    EXPECT_EQ(observed->consumed, 256u);

    mixer.play();
    observed->fail = true;
    EXPECT_FALSE(mixer.mix(samples));
    EXPECT_EQ(mixer.last_error(), "synthetic decode failure");

    mixer.unmount_all();
    EXPECT_TRUE(mixer.mix(samples));

    std::array<float, 3> incomplete {};
    EXPECT_FALSE(mixer.mix(incomplete));
}
