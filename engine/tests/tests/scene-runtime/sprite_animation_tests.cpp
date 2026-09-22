#include <rstd/test/gtest.hpp>

#include <cstdint>
#include <limits>
#include <stdexcept>

import rstd;
import rstd.cppstd;
import wescene.types;

namespace
{

owe::SpriteAnimation MakeRealSprite() {
    owe::SpriteAnimation animation;
    for (std::int32_t frame = 0; frame < 151; ++frame) {
        animation.AppendFrame(owe::SpriteFrame { .imageId = frame, .frametime = 0.03f });
    }
    return animation;
}

double RealSpritePeriod(const owe::SpriteAnimation& animation) {
    double period {};
    for (rstd::usize frame {}; frame < animation.numFrames(); ++frame) {
        period += static_cast<double>(animation.GetFrame(frame).frametime);
    }
    return period;
}

void AdvanceAtFps(owe::SpriteAnimation& animation, double total, double fps) {
    const double step = 1.0 / fps;
    double       elapsed {};
    while (elapsed + step < total) {
        (void)animation.GetAnimateFrame(step);
        elapsed += step;
    }
    (void)animation.GetAnimateFrame(total - elapsed);
}

} // namespace

TEST(SpriteAnimationClock, KeepsFrameZeroAndUsesExactFrameBoundaries) {
    owe::SpriteAnimation animation;
    animation.AppendFrame(owe::SpriteFrame { .imageId = 0, .frametime = 0.03f });
    animation.AppendFrame(owe::SpriteFrame { .imageId = 1, .frametime = 0.05f });
    animation.AppendFrame(owe::SpriteFrame { .imageId = 2, .frametime = 0.07f });

    const double first = static_cast<double>(animation.GetFrame(rstd::usize()).frametime);
    EXPECT_EQ(animation.GetAnimateFrame(0.0).imageId, 0);
    EXPECT_EQ(animation.GetAnimateFrame(first * 0.5).imageId, 0);
    EXPECT_EQ(animation.GetAnimateFrame(first * 0.5).imageId, 1);

    const double second = static_cast<double>(animation.GetFrame(rstd::usize(1)).frametime);
    EXPECT_EQ(animation.GetAnimateFrame(second).imageId, 2);
}

TEST(SpriteAnimationClock, PreservesOvershootAndBoundsLargeDeltas) {
    owe::SpriteAnimation animation;
    animation.AppendFrame(owe::SpriteFrame { .imageId = 0, .frametime = 0.03f });
    animation.AppendFrame(owe::SpriteFrame { .imageId = 1, .frametime = 0.05f });
    animation.AppendFrame(owe::SpriteFrame { .imageId = 2, .frametime = 0.07f });

    const double first  = static_cast<double>(animation.GetFrame(rstd::usize()).frametime);
    const double second = static_cast<double>(animation.GetFrame(rstd::usize(1)).frametime);
    const double third  = static_cast<double>(animation.GetFrame(rstd::usize(2)).frametime);
    const double period = first + second + third;

    EXPECT_EQ(animation.GetAnimateFrame(period * 1'000'000.0 + first + second + third * 0.5)
                  .imageId,
              2);
    EXPECT_EQ(animation.GetAnimateFrame(third * 0.5).imageId, 0);
}

TEST(SpriteAnimationClock, MatchesTexPeriodAcrossFrameRatePartitions) {
    auto one_step = MakeRealSprite();
    auto at_60    = MakeRealSprite();
    auto at_120   = MakeRealSprite();
    auto custom   = MakeRealSprite();

    const double period = RealSpritePeriod(one_step);
    const double frame  = static_cast<double>(one_step.GetFrame(rstd::usize()).frametime);
    EXPECT_NEAR(period, 4.53, 1e-6);
    EXPECT_EQ(one_step.GetAnimateFrame(period).imageId, 0);
    EXPECT_EQ(one_step.GetAnimateFrame(frame).imageId, 1);

    const double total = period * 35.0 + frame * 40.5;
    one_step            = MakeRealSprite();
    EXPECT_EQ(one_step.GetAnimateFrame(total).imageId, 40);
    AdvanceAtFps(at_60, total, 60.0);
    AdvanceAtFps(at_120, total, 120.0);
    AdvanceAtFps(custom, total, 73.25);
    EXPECT_EQ(at_60.CurrentFrameIndex(), one_step.CurrentFrameIndex());
    EXPECT_EQ(at_120.CurrentFrameIndex(), one_step.CurrentFrameIndex());
    EXPECT_EQ(custom.CurrentFrameIndex(), one_step.CurrentFrameIndex());
    EXPECT_EQ(at_60.GetAnimateFrame(frame * 0.6).imageId,
              one_step.GetAnimateFrame(frame * 0.6).imageId);
    EXPECT_EQ(at_120.GetAnimateFrame(frame * 0.6).imageId, one_step.GetCurFrame().imageId);
    EXPECT_EQ(custom.GetAnimateFrame(frame * 0.6).imageId, one_step.GetCurFrame().imageId);
}

TEST(SpriteAnimationClock, KeepsInvalidDurationFallbackBounded) {
    owe::SpriteAnimation empty;
    bool empty_rejected = false;
    try { (void)empty.GetAnimateFrame(0.0); }
    catch (const std::out_of_range&) { empty_rejected = true; }
    EXPECT_TRUE(empty_rejected);

    owe::SpriteAnimation animation;
    animation.AppendFrame(owe::SpriteFrame { .imageId = 0, .frametime = 0.0f });
    animation.AppendFrame(owe::SpriteFrame { .imageId = 1, .frametime = -1.0f });
    animation.AppendFrame(owe::SpriteFrame {
        .imageId = 2,
        .frametime = std::numeric_limits<float>::quiet_NaN(),
    });
    animation.AppendFrame(owe::SpriteFrame {
        .imageId = 3,
        .frametime = std::numeric_limits<float>::infinity(),
    });

    EXPECT_EQ(animation.GetAnimateFrame(-1.0).imageId, 0);
    EXPECT_EQ(animation.GetAnimateFrame(std::numeric_limits<double>::quiet_NaN()).imageId, 0);
    EXPECT_EQ(animation.GetAnimateFrame(std::numeric_limits<double>::infinity()).imageId, 0);
    EXPECT_EQ(animation.GetAnimateFrame(0.01).imageId, 1);
    EXPECT_EQ(animation.GetAnimateFrame(0.01).imageId, 2);
    EXPECT_EQ(animation.GetAnimateFrame(1e300).imageId, 2);

    owe::SpriteAnimation degenerate;
    degenerate.AppendFrame(owe::SpriteFrame { .imageId = 0, .frametime = 0.0f });
    degenerate.AppendFrame(owe::SpriteFrame { .imageId = 1, .frametime = -1.0f });
    EXPECT_EQ(degenerate.GetAnimateFrame(1e300).imageId, 1);
    EXPECT_EQ(degenerate.GetAnimateFrame(1e300).imageId, 0);
}
