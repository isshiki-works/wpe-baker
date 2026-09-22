#include <gtest/gtest.h>

#include "../../../src/Scene/Pkg/Parse/Puppet/TwoBoneIk.hpp"

#include <array>
#include <string>

import rstd;
import wescene.core;
import wescene.pkg.puppet;

namespace
{

using owe::puppet_ik::SolveTwoBone;
using namespace rstd::prelude;
using rstd::sync::Arc;

void ExpectReconstructs(const Eigen::Vector3f& start, const Eigen::Vector3f& old_joint,
                        const Eigen::Vector3f&                 old_end,
                        const owe::puppet_ik::TwoBoneSolution& solution) {
    const Eigen::Vector3f rotated_joint = start + solution.start_rotation * (old_joint - start);
    const Eigen::Vector3f rotated_end =
        rotated_joint + solution.joint_rotation * (solution.start_rotation * (old_end - old_joint));
    EXPECT_TRUE(rotated_joint.isApprox(solution.joint, 1e-5f));
    EXPECT_TRUE(rotated_end.isApprox(solution.end, 1e-5f));
    EXPECT_NEAR(solution.start_rotation.norm(), 1.0f, 1e-6f);
    EXPECT_NEAR(solution.joint_rotation.norm(), 1.0f, 1e-6f);
}

auto MakeIkPuppet(const Eigen::Vector3f& target, const Eigen::Vector3f& pole) -> Arc<owe::Puppet> {
    auto puppet = Arc<owe::Puppet>::make();

    auto& start = puppet->bones.emplace_back();
    start.local_bind.translate(Eigen::Vector3f { 10.0f, -5.0f, 2.0f });
    start.local_bind.rotate(Eigen::AngleAxisf(0.7f, Eigen::Vector3f::UnitZ()));

    auto& joint       = puppet->bones.emplace_back();
    joint.bind_parent = joint.anim_parent = joint.file_parent = 0;
    joint.local_bind.translate(Eigen::Vector3f { 3.0f, 0.0f, 0.0f });

    auto& end       = puppet->bones.emplace_back();
    end.bind_parent = end.anim_parent = end.file_parent = 1;
    end.local_bind.translate(Eigen::Vector3f { 3.0f, 0.0f, 0.0f });

    puppet->ik_nodes.emplace_back();
    puppet->ik_nodes.emplace_back().length = 3.0f;
    puppet->ik_nodes.emplace_back().length = 3.0f;

    auto& pole_controller      = puppet->ik_controllers.emplace_back();
    pole_controller.bone_index = 2;
    pole_controller.type       = 1;
    pole_controller.bind_xform.translate(pole);
    auto& target_controller      = puppet->ik_controllers.emplace_back();
    target_controller.bone_index = 2;
    target_controller.type       = 0;
    target_controller.bind_xform.translate(target);

    auto& chain                   = puppet->ik_chains.emplace_back();
    chain.start_bone              = 0;
    chain.end_bone                = 2;
    chain.target_controller_index = 1;
    chain.length                  = 6.0f;
    chain.bones.push(0);
    chain.bones.push(1);
    chain.bones.push(2);
    return puppet;
}

void AddControllerAnimation(owe::Puppet& puppet, const Eigen::Vector3f& bind_target,
                            const Eigen::Vector3f& bind_pole, const Eigen::Vector3f& target,
                            const Eigen::Vector3f& pole) {
    auto& animation  = puppet.anims.emplace_back();
    animation.id     = 7;
    animation.fps    = 1.0;
    animation.length = 1;
    animation.mode   = owe::Puppet::PlayMode::Single;
    for (const auto& positions :
         { std::array { bind_pole, pole }, std::array { bind_target, target } }) {
        auto& track = animation.controller_tracks.emplace_back();
        for (const auto& position : positions) {
            track.frames.push(owe::Puppet::BoneFrame {
                .position = position,
                .angle    = Eigen::Vector3f::Zero(),
                .scale    = Eigen::Vector3f::Ones(),
            });
        }
    }
    animation.blend_curves.emplace_back();
    animation.blend_curves.emplace_back();
    auto& end_bone_curve = animation.blend_curves.emplace_back();
    end_bone_curve.values.push(0.0f);
    end_bone_curve.values.push(0.0f);
}

} // namespace

TEST(PuppetTwoBoneIk, ReachesTargetAndPreservesBoneLengths) {
    const Eigen::Vector3f start { 0.0f, 0.0f, 0.0f };
    const Eigen::Vector3f joint { 3.0f, 0.0f, 0.0f };
    const Eigen::Vector3f end { 3.0f, 4.0f, 0.0f };
    const Eigen::Vector3f target { 1.0f, 4.0f, 0.0f };

    const auto solution = SolveTwoBone(start, joint, end, target, 3.0f, 4.0f);

    ASSERT_TRUE(solution.valid);
    EXPECT_TRUE(solution.reached);
    EXPECT_TRUE(solution.end.isApprox(target, 1e-5f));
    EXPECT_NEAR((solution.joint - start).norm(), 3.0f, 1e-5f);
    EXPECT_NEAR((solution.end - solution.joint).norm(), 4.0f, 1e-5f);
    ExpectReconstructs(start, joint, end, solution);
}

TEST(PuppetTwoBoneIk, ClampsTargetsOutsideMaximumReach) {
    const Eigen::Vector3f start { 0.0f, 0.0f, 0.0f };
    const Eigen::Vector3f joint { 3.0f, 0.0f, 0.0f };
    const Eigen::Vector3f end { 7.0f, 0.0f, 0.0f };

    const auto solution =
        SolveTwoBone(start, joint, end, Eigen::Vector3f { 20.0f, 0.0f, 0.0f }, 3.0f, 4.0f);

    ASSERT_TRUE(solution.valid);
    EXPECT_FALSE(solution.reached);
    EXPECT_TRUE(solution.joint.isApprox(Eigen::Vector3f { 3.0f, 0.0f, 0.0f }, 1e-5f));
    EXPECT_TRUE(solution.end.isApprox(Eigen::Vector3f { 7.0f, 0.0f, 0.0f }, 1e-5f));
    ExpectReconstructs(start, joint, end, solution);
}

TEST(PuppetTwoBoneIk, ClampsTargetsInsideMinimumReach) {
    const Eigen::Vector3f start { 0.0f, 0.0f, 0.0f };
    const Eigen::Vector3f joint { 4.0f, 0.0f, 0.0f };
    const Eigen::Vector3f end { 3.0f, 0.0f, 0.0f };

    const auto solution =
        SolveTwoBone(start, joint, end, Eigen::Vector3f { 1.0f, 0.0f, 0.0f }, 4.0f, 1.0f);

    ASSERT_TRUE(solution.valid);
    EXPECT_FALSE(solution.reached);
    EXPECT_TRUE(solution.end.isApprox(Eigen::Vector3f { 3.0f, 0.0f, 0.0f }, 1e-5f));
    ExpectReconstructs(start, joint, end, solution);
}

TEST(PuppetTwoBoneIk, ChoosesStableBendForCollinearPose) {
    const Eigen::Vector3f start { 0.0f, 0.0f, 0.0f };
    const Eigen::Vector3f joint { 3.0f, 0.0f, 0.0f };
    const Eigen::Vector3f end { 6.0f, 0.0f, 0.0f };
    const Eigen::Vector3f target { 4.0f, 0.0f, 0.0f };

    const auto solution = SolveTwoBone(start, joint, end, target, 3.0f, 3.0f);

    ASSERT_TRUE(solution.valid);
    EXPECT_TRUE(solution.reached);
    EXPECT_TRUE(solution.end.isApprox(target, 1e-5f));
    EXPECT_GT((solution.joint - target.normalized() * solution.joint.x()).norm(), 1.0f);
    ExpectReconstructs(start, joint, end, solution);
}

TEST(PuppetTwoBoneIk, BendsTowardPolePoint) {
    const Eigen::Vector3f start { 0.0f, 0.0f, 0.0f };
    const Eigen::Vector3f joint { 3.0f, 0.0f, 0.0f };
    const Eigen::Vector3f end { 6.0f, 0.0f, 0.0f };
    const Eigen::Vector3f target { 4.0f, 0.0f, 0.0f };
    const Eigen::Vector3f pole { 0.0f, 10.0f, 0.0f };

    const auto solution = SolveTwoBone(start, joint, end, target, 3.0f, 3.0f, &pole);

    ASSERT_TRUE(solution.valid);
    EXPECT_GT(solution.joint.y(), 0.0f);
    ExpectReconstructs(start, joint, end, solution);
}

TEST(PuppetTwoBoneIk, FoldsEqualBonesOntoStart) {
    const Eigen::Vector3f start { 1.0f, 2.0f, 0.0f };
    const Eigen::Vector3f joint { 3.0f, 2.0f, 0.0f };
    const Eigen::Vector3f end { 5.0f, 2.0f, 0.0f };

    const auto solution = SolveTwoBone(start, joint, end, start, 2.0f, 2.0f);

    ASSERT_TRUE(solution.valid);
    EXPECT_TRUE(solution.reached);
    EXPECT_TRUE(solution.end.isApprox(start, 1e-5f));
    EXPECT_NEAR((solution.joint - start).norm(), 2.0f, 1e-5f);
    ExpectReconstructs(start, joint, end, solution);
}

TEST(PuppetTwoBoneIk, RejectsDegenerateCurrentSegmentWithoutNan) {
    const Eigen::Vector3f start { 0.0f, 0.0f, 0.0f };
    const Eigen::Vector3f end { 2.0f, 0.0f, 0.0f };

    const auto solution = SolveTwoBone(start, start, end, end, 1.0f, 1.0f);

    EXPECT_FALSE(solution.valid);
    EXPECT_FALSE(solution.reached);
    EXPECT_TRUE(solution.joint.isApprox(start));
    EXPECT_TRUE(solution.end.isApprox(end));
    EXPECT_TRUE(solution.start_rotation.coeffs().allFinite());
    EXPECT_TRUE(solution.joint_rotation.coeffs().allFinite());
}

TEST(PuppetTwoBoneIk, SolvesWorldPoseUnderParentTransform) {
    Eigen::Affine3f parent = Eigen::Affine3f::Identity();
    parent.translate(Eigen::Vector3f { 10.0f, -5.0f, 2.0f });
    parent.rotate(Eigen::AngleAxisf(0.7f, Eigen::Vector3f::UnitZ()));
    parent.scale(2.0f);

    const Eigen::Vector3f start  = parent * Eigen::Vector3f { 0.0f, 0.0f, 0.0f };
    const Eigen::Vector3f joint  = parent * Eigen::Vector3f { 2.0f, 0.0f, 0.0f };
    const Eigen::Vector3f end    = parent * Eigen::Vector3f { 3.0f, 0.0f, 0.0f };
    const Eigen::Vector3f target = parent * Eigen::Vector3f { 1.0f, 2.0f, 0.0f };

    const auto solution = SolveTwoBone(start, joint, end, target, 4.0f, 2.0f);

    ASSERT_TRUE(solution.valid);
    EXPECT_TRUE(solution.reached);
    EXPECT_TRUE(solution.end.isApprox(target, 1e-5f));
    ExpectReconstructs(start, joint, end, solution);
}

TEST(PuppetTwoBoneIkRuntime, LeavesPoseAloneWithoutActiveControllerLayer) {
    const Eigen::Vector3f start { 10.0f, -5.0f, 2.0f };
    const Eigen::Vector3f target = start + Eigen::Vector3f { 4.0f, 0.0f, 0.0f };
    auto puppet = MakeIkPuppet(target, start + Eigen::Vector3f { 0.0f, 5.0f, 0.0f });
    puppet->prepared();
    const Eigen::Vector3f bind_end = puppet->bones[usize(2)].world_bind.translation();
    owe::PuppetLayer      layer(puppet.clone());
    layer.prepared(slice<owe::PuppetLayer::AnimationLayer> {});

    const auto            skin     = layer.genFrame(0.0);
    const Eigen::Affine3f end_pose = skin[usize(2)] * puppet->bones[usize(2)].world_bind;

    EXPECT_TRUE(end_pose.translation().isApprox(bind_end, 1e-5f));
    EXPECT_FALSE(end_pose.translation().isApprox(target, 1e-3f));
}

TEST(PuppetTwoBoneIkRuntime, SamplesControllerReplacementIndependentOfBoneCurve) {
    const Eigen::Vector3f start { 10.0f, -5.0f, 2.0f };
    const Eigen::Vector3f bind_target = start + Eigen::Vector3f { 4.0f, 0.0f, 0.0f };
    const Eigen::Vector3f bind_pole   = start + Eigen::Vector3f { 0.0f, 5.0f, 0.0f };
    const Eigen::Vector3f target      = start + Eigen::Vector3f { 0.0f, 4.0f, 0.0f };
    const Eigen::Vector3f pole        = start + Eigen::Vector3f { -5.0f, 0.0f, 0.0f };
    auto                  puppet      = MakeIkPuppet(bind_target, bind_pole);
    AddControllerAnimation(*puppet, bind_target, bind_pole, target, pole);
    puppet->prepared();

    owe::PuppetLayer                 layer(puppet.clone());
    owe::PuppetLayer::AnimationLayer authored { .id = 7 };
    layer.prepared(slice<owe::PuppetLayer::AnimationLayer>::from_raw_parts(&authored, usize(1)));
    layer.AnimationPlaybacks()[usize()]->SetFrame(rstd::i32(1));
    layer.AnimationPlaybacks()[usize()]->Pause();

    owe::OfflineExecutionContext context;
    owe::OfflineExecutionScope   scope(context);
    const auto            skin       = layer.genFrame(0.0);
    const Eigen::Affine3f joint_pose = skin[usize(1)] * puppet->bones[usize(1)].world_bind;
    const Eigen::Affine3f end_pose   = skin[usize(2)] * puppet->bones[usize(2)].world_bind;

    EXPECT_EQ(context.runtime_ik_chain_solves, 1u);
    EXPECT_TRUE(end_pose.translation().isApprox(target, 1e-5f));
    EXPECT_LT(joint_pose.translation().x(), start.x());
}

TEST(PuppetTwoBoneIkRuntime, ZeroControllerWeightLeavesPoseAlone) {
    const Eigen::Vector3f start { 10.0f, -5.0f, 2.0f };
    const Eigen::Vector3f bind_target = start + Eigen::Vector3f { 4.0f, 0.0f, 0.0f };
    const Eigen::Vector3f bind_pole   = start + Eigen::Vector3f { 0.0f, 5.0f, 0.0f };
    auto                  puppet      = MakeIkPuppet(bind_target, bind_pole);
    AddControllerAnimation(*puppet,
                           bind_target,
                           bind_pole,
                           start + Eigen::Vector3f { 0.0f, 4.0f, 0.0f },
                           start + Eigen::Vector3f { -5.0f, 0.0f, 0.0f });
    puppet->prepared();
    const Eigen::Vector3f bind_end = puppet->bones[usize(2)].world_bind.translation();

    owe::PuppetLayer                 layer(puppet.clone());
    owe::PuppetLayer::AnimationLayer authored { .id = 7, .blend = 0.0 };
    layer.prepared(slice<owe::PuppetLayer::AnimationLayer>::from_raw_parts(&authored, usize(1)));
    layer.AnimationPlaybacks()[usize()]->SetFrame(rstd::i32(1));
    layer.AnimationPlaybacks()[usize()]->Pause();

    const auto            skin     = layer.genFrame(0.0);
    const Eigen::Affine3f end_pose = skin[usize(2)] * puppet->bones[usize(2)].world_bind;
    EXPECT_TRUE(end_pose.translation().isApprox(bind_end, 1e-5f));
}

TEST(PuppetTwoBoneIkRuntime, ReportsDegenerateAnimatedPoseOffline) {
    const Eigen::Vector3f start { 10.0f, -5.0f, 2.0f };
    const Eigen::Vector3f bind_target = start + Eigen::Vector3f { 4.0f, 0.0f, 0.0f };
    const Eigen::Vector3f bind_pole   = start + Eigen::Vector3f { 0.0f, 5.0f, 0.0f };
    auto                  puppet      = MakeIkPuppet(bind_target, bind_pole);
    AddControllerAnimation(*puppet, bind_target, bind_pole, bind_target, bind_pole);
    puppet->bones[usize(1)].local_bind.translation().setZero();
    puppet->prepared();

    owe::PuppetLayer                 layer(puppet.clone());
    owe::PuppetLayer::AnimationLayer authored { .id = 7 };
    layer.prepared(slice<owe::PuppetLayer::AnimationLayer>::from_raw_parts(&authored, usize(1)));
    owe::OfflineExecutionContext context;
    {
        owe::OfflineExecutionScope scope(context);
        (void)layer.genFrame(0.0);
    }

    ASSERT_TRUE(context.failed);
    EXPECT_EQ(context.runtime_ik_chain_solves, 0u);
    ASSERT_FALSE(context.diagnostics.empty());
    EXPECT_NE(context.diagnostics.back().find("degenerate animated pose"), std::string::npos);
}
