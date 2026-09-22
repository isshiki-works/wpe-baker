#pragma once

#include <Eigen/Dense>
#include <Eigen/Geometry>

#include <algorithm>
#include <cmath>

namespace owe::puppet_ik
{

// Positions and lengths use the same world-space units. Apply start_rotation
// about start to the whole chain, then joint_rotation about joint to the joint
// subtree.
struct TwoBoneSolution {
    Eigen::Vector3f    joint { Eigen::Vector3f::Zero() };
    Eigen::Vector3f    end { Eigen::Vector3f::Zero() };
    Eigen::Quaternionf start_rotation { Eigen::Quaternionf::Identity() };
    Eigen::Quaternionf joint_rotation { Eigen::Quaternionf::Identity() };
    bool               valid { false };
    bool               reached { false };
};

inline TwoBoneSolution SolveTwoBone(const Eigen::Vector3f& start, const Eigen::Vector3f& joint,
                                    const Eigen::Vector3f& end, const Eigen::Vector3f& target,
                                    float upper_length, float lower_length,
                                    const Eigen::Vector3f* pole = nullptr) {
    TwoBoneSolution result { .joint = joint, .end = end };
    constexpr float epsilon = 1e-6f;
    if (! start.allFinite() || ! joint.allFinite() || ! end.allFinite() || ! target.allFinite() ||
        (pole != nullptr && ! pole->allFinite()) || ! std::isfinite(upper_length) ||
        ! std::isfinite(lower_length) || upper_length <= epsilon || lower_length <= epsilon) {
        return result;
    }

    const Eigen::Vector3f current_upper        = joint - start;
    const Eigen::Vector3f current_lower        = end - joint;
    const float           current_upper_length = current_upper.norm();
    const float           current_lower_length = current_lower.norm();
    if (! std::isfinite(current_upper_length) || ! std::isfinite(current_lower_length) ||
        current_upper_length <= epsilon || current_lower_length <= epsilon) {
        return result;
    }

    const Eigen::Vector3f to_target       = target - start;
    const float           target_distance = to_target.norm();
    const float           minimum_reach   = std::abs(upper_length - lower_length);
    const float           maximum_reach   = upper_length + lower_length;
    if (! std::isfinite(target_distance) || ! std::isfinite(maximum_reach)) return result;
    const float solved_distance = std::clamp(target_distance, minimum_reach, maximum_reach);

    Eigen::Vector3f target_direction;
    if (target_distance > epsilon) {
        target_direction = to_target / target_distance;
    } else if ((end - start).norm() > epsilon) {
        target_direction = (end - start).normalized();
    } else {
        target_direction = current_upper.normalized();
    }

    if (solved_distance <= epsilon) {
        Eigen::Vector3f fold_direction = current_upper.normalized();
        if (pole != nullptr && (*pole - start).norm() > epsilon)
            fold_direction = (*pole - start).normalized();
        result.joint = start + fold_direction * upper_length;
        result.end   = start;
    } else {
        Eigen::Vector3f bend { Eigen::Vector3f::Zero() };
        if (pole != nullptr) {
            const Eigen::Vector3f to_pole = *pole - start;
            bend = to_pole - target_direction * to_pole.dot(target_direction);
        }
        if (bend.norm() <= epsilon)
            bend = current_upper - target_direction * current_upper.dot(target_direction);
        if (bend.norm() <= epsilon) {
            const Eigen::Vector3f plane_normal = current_upper.cross(current_lower);
            bend                               = target_direction.cross(plane_normal);
        }
        if (bend.norm() <= epsilon) bend = target_direction.unitOrthogonal();
        bend.normalize();

        const float along  = (upper_length * upper_length - lower_length * lower_length +
                              solved_distance * solved_distance) /
                             (2.0f * solved_distance);
        const float height = std::sqrt(std::max(0.0f, upper_length * upper_length - along * along));
        result.joint       = start + target_direction * along + bend * height;
        result.end         = start + target_direction * solved_distance;
    }

    result.start_rotation =
        Eigen::Quaternionf::FromTwoVectors(current_upper, result.joint - start).normalized();
    result.joint_rotation = Eigen::Quaternionf::FromTwoVectors(
                                result.start_rotation * current_lower, result.end - result.joint)
                                .normalized();
    result.valid          = result.joint.allFinite() && result.end.allFinite() &&
                            result.start_rotation.coeffs().allFinite() &&
                            result.joint_rotation.coeffs().allFinite();
    result.reached        = result.valid && target_distance >= minimum_reach - epsilon &&
                            target_distance <= maximum_reach + epsilon;
    return result;
}

} // namespace owe::puppet_ik
