// 移植自 wavsen tests/audio_gain_tests.cpp。

#include <gtest/gtest.h>

import rstd.cppstd;
import owe.media;

namespace
{

// 一块 1024 帧立体声，全 1.0 过一遍渐变后每个样本都应落在 [minimum, maximum]。
bool ApplyAndCheck(owe::media::VolumeScaleRamp& ramp, float minimum, float maximum) {
    std::array<float, 1024 * 2> samples;
    samples.fill(1.0f);
    ramp.apply(samples, 2, 1.0f);
    for (const float sample : samples) {
        if (! std::isfinite(sample) || sample < minimum || sample > maximum) return false;
    }
    return true;
}

} // namespace

TEST(VolumeScaleRamp, UnmuteStopsAtTargetInsideChunk) {
    owe::media::VolumeScaleRamp ramp;
    ramp.redirect(0.0f, 48000, 0);
    ramp.redirect(1.0f, 48000, 500);
    for (int index = 0; index < 30; ++index) ASSERT_TRUE(ApplyAndCheck(ramp, 0.0f, 1.0f));
    EXPECT_EQ(ramp.current(), 1.0f);
}

TEST(VolumeScaleRamp, MuteStopsAtTargetInsideChunk) {
    owe::media::VolumeScaleRamp ramp;
    ramp.redirect(0.0f, 48000, 500);
    for (int index = 0; index < 30; ++index) ASSERT_TRUE(ApplyAndCheck(ramp, 0.0f, 1.0f));
    EXPECT_EQ(ramp.current(), 0.0f);
}

TEST(VolumeScaleRamp, RepeatedRedirectionStaysBounded) {
    owe::media::VolumeScaleRamp ramp;
    for (int index = 0; index < 20; ++index) {
        ramp.redirect(index % 2 == 0 ? 0.0f : 1.0f, 48000, 500);
        for (int chunk = 0; chunk < 30; ++chunk) ASSERT_TRUE(ApplyAndCheck(ramp, 0.0f, 1.0f));
    }
}
