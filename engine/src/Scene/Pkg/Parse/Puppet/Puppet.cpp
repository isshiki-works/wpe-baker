module;
#include <owe/compat.hpp>
#include <owe/std.hpp>

#include "TwoBoneIk.hpp"

#include <string>

module wescene.pkg.puppet;
import eigen;
import wescene.core;
import wescene.scene;

using namespace owe;
using namespace Eigen;
using namespace rstd::prelude;
using namespace rstd::literals;
using rstd::sync::Arc;

static void DiagnosePuppetIk(const char* reason) {
    if (active_offline_execution != nullptr)
        active_offline_execution->diagnose("puppet IK runtime failed: " + std::string(reason),
                                           true);
}

static double SampleBoneCurve(const Vec<Puppet::BoneFrameCurve>& curves, usize bone_index,
                              const Puppet::Animation::InterpolationInfo& info) {
    if (bone_index >= curves.len()) return 1.0;
    const auto& values = curves[bone_index].values;
    if (values.is_empty()) return 1.0;

    auto sample = [&](usize frame) {
        const auto i = frame < values.len() ? frame : values.len() - usize(1);
        return static_cast<double>(values[i]);
    };
    const double a = sample(info.frame_a);
    const double b = sample(info.frame_b);
    return a * (1.0 - info.t) + b * info.t;
}

static bool IsTextureChannelTrack(const Puppet::Animation& animation, const Vec<float>& values) {
    if (animation.length < 0) return false;
    return values.len() == usize(static_cast<size_t>(animation.length) + 1);
}

static float SampleTextureChannelTrack(const Vec<float>&                           values,
                                       const Puppet::Animation::InterpolationInfo& info) {
    if (values.is_empty()) return 0.0f;
    const auto sample = [&](usize frame) {
        const auto index = frame < values.len() ? frame : values.len() - usize(1);
        return values[index];
    };
    return sample(info.frame_a) * static_cast<float>(1.0 - info.t) +
           sample(info.frame_b) * static_cast<float>(info.t);
}

static usize TextureChannelTrackCount(const Puppet::Animation& animation) {
    if (animation.trans.is_none()) return usize();
    const auto& trans = *animation.trans;
    usize       count = IsTextureChannelTrack(animation, trans.main_track) ? usize(1) : usize();
    for (const auto& track : trans.tail_tracks) {
        if (IsTextureChannelTrack(animation, track)) ++count;
    }
    return count;
}

static double LayerBoneBlend(const Puppet::Animation& anim, usize bone_index,
                             const Puppet::Animation::InterpolationInfo& info, double layer_blend) {
    double blend = layer_blend * SampleBoneCurve(anim.blend_curves, bone_index, info);
    blend *= SampleBoneCurve(anim.scalar_curves, bone_index, info);
    return std::max(0.0, blend);
}

static bool HasAuthoredTrack(const Puppet::BoneTrack& track) {
    constexpr float eps      = 1e-6f;
    auto            non_zero = [](const Eigen::Vector3f& v) {
        return v.cwiseAbs().maxCoeff() > eps;
    };
    auto non_default_scale = [](const Eigen::Vector3f& v) {
        const bool zero = v.cwiseAbs().maxCoeff() <= eps;
        const bool one  = (v - Eigen::Vector3f::Ones()).cwiseAbs().maxCoeff() <= eps;
        return ! zero && ! one;
    };
    for (const auto& frame : track.frames) {
        if (non_zero(frame.position) || non_zero(frame.angle) || non_default_scale(frame.scale))
            return true;
    }
    return false;
}

struct BindLinear {
    Quaterniond rotation;
    Vector3f    scale;
};

static Quaterniond ToQuaternion(Vector3f euler) {
    const rstd::array<Vector3d, 3> axis { Vector3d::UnitX(), Vector3d::UnitY(), Vector3d::UnitZ() };
    return AngleAxis<double>(euler.z(), axis[usize(2)]) *
           AngleAxis<double>(euler.y(), axis[usize(1)]) *
           AngleAxis<double>(euler.x(), axis[usize(0)]);
};

static BindLinear DecomposeBindLinear(const Matrix3f& linear) {
    Matrix3f rot = linear;
    Vector3f scale { rot.col(0).norm(), rot.col(1).norm(), rot.col(2).norm() };
    for (int i = 0; i < 3; ++i) {
        if (scale[i] > 0.000001f) {
            rot.col(i) /= scale[i];
        } else {
            rot.col(i).setZero();
            rot(i, i) = 1.0f;
            scale[i]  = 1.0f;
        }
    }
    if (rot.determinant() < 0.0f) {
        scale.x() = -scale.x();
        rot.col(0) *= -1.0f;
    }

    Quaterniond q { rot.cast<double>() };
    q.normalize();
    return { q, scale };
}

void Puppet::prepared() {
    for (usize i {}; i < bones.len(); ++i) {
        auto& b = bones[i];
        rstd_assert(b.bind_parent < i.to_primitive() || b.noBindParent());
        // vco bracket only applies to world-anchored puppets (MDLV21): each bone
        // is its own sprite root, and anim pivots around vertex_centroid_offset.
        // Chain LBS (MDLV22+) keeps strict parent.world_bind * local_bind.
        if (b.noBindParent()) {
            b.world_bind = b.local_bind;
            if (world_anchored_bones) {
                b.world_bind.pretranslate(b.vertex_centroid_offset);
            }
        } else {
            b.world_bind = bones[usize(b.bind_parent)].world_bind * b.local_bind;
        }
        b.inv_bind = b.world_bind.inverse();
    }
    for (auto& attachment : attachments) {
        attachment.bind_xform = attachment.local_xform;
        if (usize(attachment.bone_index) >= bones.len()) continue;

        Vec<uint32_t> chain;
        uint32_t      bone_index = attachment.bone_index;
        while (bone_index != NO_PARENT && usize(bone_index) < bones.len()) {
            chain.push(uint32_t(bone_index));
            bone_index = bones[usize(bone_index)].file_parent;
        }

        Eigen::Affine3f bone_bind = Eigen::Affine3f::Identity();
        for (usize i = chain.len(); i > usize();) {
            --i;
            bone_bind = bone_bind * bones[usize(chain[i])].local_bind;
        }
        attachment.bind_xform = bone_bind * attachment.local_xform;
    }
    for (auto& anim : anims) {
        anim.frame_time = 1.0f / anim.fps;
        anim.max_time   = static_cast<double>(anim.length) / anim.fps;
        for (auto& t : anim.bone_tracks) {
            for (auto& f : t.frames) {
                f.quaternion = ToQuaternion(f.angle);
            }
        }
    }

    m_final_affines.clear();
    m_final_affines.reserve(bones.len());
    for (usize i {}; i < bones.len(); ++i) {
        m_final_affines.emplace_back(Affine3f::Identity());
    }
}

Option<usize> Puppet::attachmentIndex(ref<str> name) const noexcept {
    for (usize i {}; i < attachments.len(); ++i) {
        if (attachments[i].name == name) return Some(i);
    }
    return None();
}

Option<Eigen::Affine3f> Puppet::attachmentBindTransform(usize index) const noexcept {
    if (index >= attachments.len()) return None();
    Eigen::Affine3f transform = attachments[index].bind_xform;
    return Some(rstd::move(transform));
}

slice<Eigen::Affine3f> Puppet::genFrame(PuppetLayer& puppet_layer, double time) noexcept {
    puppet_layer.updateInterpolation(time);

    // TRS skinning is required: WE puppets animate scale (e.g. blink uses
    // frame.scale.y → ~0). A pure-translation g_Bones would shift the
    // whole sprite as a unit; intra-sprite compression needs non-identity
    // linear so vertices within the sprite get differential treatment.
    // Standard LBS: per-bone local affine = T(pos) · R(quat) · Diag(scale).
    // Chained through parent's anim transform, then M_skin = A_world · inv_bind.
    // WE anim convention: frame[0] is the replacement anchor pose for a bone.
    // MDLA blend curves decide which dense bone-track slots are active for each
    // animation layer; inactive bones keep bind pose instead of being diluted by
    // unrelated replacement layers.
    for (usize i {}; i < m_final_affines.len(); ++i) {
        const auto& bone   = bones[i];
        auto&       affine = m_final_affines[i];

        rstd_assert(bone.anim_parent < i.to_primitive() || bone.noAnimParent());
        Affine3f parent = Affine3f::Identity();
        if (! bone.noAnimParent()) {
            parent = m_final_affines[usize(bone.anim_parent)];
            if (world_anchored_bones) {
                // MDLV21 child bind poses are already puppet-local; inherit
                // only the parent's animated delta to avoid double transforms.
                parent = parent * bones[usize(bone.anim_parent)].inv_bind.matrix();
            }
        }

        const Puppet::BoneFrame* replace_base_frame { nullptr };
        for (const auto& layer : puppet_layer.m_layers) {
            if (layer.anim == nullptr || ! layer.anim_layer.visible || layer.anim_layer.additive)
                continue;
            if (i >= layer.anim->bone_tracks.len()) continue;
            const auto& track = layer.anim->bone_tracks[i];
            if (! HasAuthoredTrack(track)) continue;
            const double blend =
                LayerBoneBlend(*layer.anim, i, layer.interp_info, layer.anim_layer.blend);
            if (blend <= 0.0) continue;
            replace_base_frame = std::addressof(track.frames[usize()]);
            break;
        }

        // Bind state. vco is a fixed render-time pivot offset for root sprite
        // bones (matches world_bind's pretranslate in prepared()) and is added
        // after layer deltas so the replacement anchor stays in puppet space.
        const BindLinear bind_linear = DecomposeBindLinear(bone.local_bind.linear());

        Vector3f trans { replace_base_frame != nullptr ? replace_base_frame->position
                                                       : bone.local_bind.translation() };
        Vector3f scale { replace_base_frame != nullptr ? replace_base_frame->scale
                                                       : bind_linear.scale };
        // quat absorbs the anchor rotation directly. Each layer multiplies in its
        // frame delta from frame[0], whose delta is identity.
        Quaterniond       quat { replace_base_frame != nullptr ? replace_base_frame->quaternion
                                                               : bind_linear.rotation };
        const Quaterniond ident { Quaterniond::Identity() };

        for (auto& layer : puppet_layer.m_layers) {
            auto& alayer = layer.anim_layer;
            if (layer.anim == nullptr || ! alayer.visible) continue;
            if (i >= layer.anim->bone_tracks.len()) continue;

            auto& info  = layer.interp_info;
            auto& track = layer.anim->bone_tracks[i];
            if (! HasAuthoredTrack(track)) continue;
            auto& frame_base = track.frames[usize()];
            auto& frame_a    = track.frames[info.frame_a];
            auto& frame_b    = track.frames[info.frame_b];

            double t     = info.t;
            double one_t = 1.0 - info.t;
            double blend = LayerBoneBlend(*layer.anim, i, info, alayer.blend);
            if (blend <= 0.0) continue;

            auto frame_a_quat_delta = frame_a.quaternion * frame_base.quaternion.conjugate();
            auto frame_b_quat_delta = frame_b.quaternion * frame_base.quaternion.conjugate();
            auto pos_a_delta        = frame_a.position - frame_base.position;
            auto pos_b_delta        = frame_b.position - frame_base.position;
            auto scale_a_delta      = frame_a.scale - frame_base.scale;
            auto scale_b_delta      = frame_b.scale - frame_base.scale;

            quat *= frame_a_quat_delta.slerp(t, frame_b_quat_delta).slerp(1.0 - blend, ident);
            if (alayer.additive) {
                trans += blend * (pos_a_delta * one_t + pos_b_delta * t);
                scale += blend * (scale_a_delta * one_t + scale_b_delta * t);
            } else {
                trans += blend * (pos_a_delta * one_t + pos_b_delta * t);
                scale += blend * (scale_a_delta * one_t + scale_b_delta * t);
            }
        }
        if (bone.noBindParent() && world_anchored_bones) {
            trans += bone.vertex_centroid_offset;
        }
        affine = Affine3f::Identity();
        affine.pretranslate(trans);
        affine.rotate(quat.cast<float>());
        affine.scale(scale);
        affine = parent * affine;
    }

    struct ControllerSample {
        Vector3f position;
        bool     valid { true };
        bool     active { false };
    };
    auto sample_controller_position = [&](usize controller_index) {
        const auto&              controller = ik_controllers[controller_index];
        const Puppet::BoneFrame* replace_base_frame { nullptr };
        ControllerSample         sample { .position = controller.bind_xform.translation() };
        for (const auto& layer : puppet_layer.m_layers) {
            if (layer.anim == nullptr || ! layer.anim_layer.visible) continue;
            const double blend = std::max(0.0, layer.anim_layer.blend);
            if (blend <= 0.0) continue;
            sample.active = true;
            if (controller_index >= layer.anim->controller_tracks.len()) {
                sample.valid = false;
                return sample;
            }
            const auto& track = layer.anim->controller_tracks[controller_index];
            const auto& info  = layer.interp_info;
            if (track.frames.is_empty() || info.frame_a >= track.frames.len() ||
                info.frame_b >= track.frames.len()) {
                sample.valid = false;
                return sample;
            }
            if (! layer.anim_layer.additive && replace_base_frame == nullptr)
                replace_base_frame = std::addressof(track.frames[usize()]);
        }

        if (! sample.active) return sample;
        if (replace_base_frame != nullptr) sample.position = replace_base_frame->position;
        for (const auto& layer : puppet_layer.m_layers) {
            if (layer.anim == nullptr || ! layer.anim_layer.visible) continue;
            const double blend = std::max(0.0, layer.anim_layer.blend);
            if (blend <= 0.0) continue;
            const auto& track = layer.anim->controller_tracks[controller_index];
            const auto& info  = layer.interp_info;
            const auto& base  = track.frames[usize()].position;
            const auto& a     = track.frames[info.frame_a].position;
            const auto& b     = track.frames[info.frame_b].position;
            sample.position +=
                static_cast<float>(blend) * ((a - base) * static_cast<float>(1.0 - info.t) +
                                             (b - base) * static_cast<float>(info.t));
        }
        sample.valid = sample.position.allFinite();
        return sample;
    };

    auto is_descendant = [&](usize bone_index, usize ancestor) {
        for (usize remaining = bones.len(); remaining > usize(); --remaining) {
            if (bone_index == ancestor) return true;
            const uint32_t parent = bones[bone_index].anim_parent;
            if (parent == NO_PARENT || usize(parent) >= bones.len()) return false;
            bone_index = usize(parent);
        }
        return false;
    };
    auto rotate_subtree = [&](usize root, const Vector3f& pivot, const Quaternionf& rotation) {
        Affine3f correction = Affine3f::Identity();
        correction.translate(pivot);
        correction.rotate(rotation);
        correction.translate(-pivot);
        for (usize i {}; i < m_final_affines.len(); ++i) {
            if (is_descendant(i, root)) m_final_affines[i] = correction * m_final_affines[i];
        }
    };

    for (const auto& chain : ik_chains) {
        if (chain.bones.len() != usize(3) ||
            usize(chain.target_controller_index) >= ik_controllers.len()) {
            DiagnosePuppetIk("invalid three-node chain schema");
            continue;
        }
        const usize start_index(chain.bones[usize()]);
        const usize joint_index(chain.bones[usize(1)]);
        const usize end_index(chain.bones[usize(2)]);
        if (start_index >= bones.len() || joint_index >= bones.len() || end_index >= bones.len() ||
            joint_index >= ik_nodes.len() || end_index >= ik_nodes.len() ||
            bones[joint_index].anim_parent != start_index.to_primitive() ||
            bones[end_index].anim_parent != joint_index.to_primitive()) {
            DiagnosePuppetIk("invalid chain hierarchy");
            continue;
        }

        const usize target_index(chain.target_controller_index);
        const auto& target_controller = ik_controllers[target_index];
        if (target_controller.type != 0 || usize(target_controller.bone_index) != end_index) {
            DiagnosePuppetIk("invalid target controller");
            continue;
        }

        usize pole_index {};
        bool  has_pole = false;
        for (usize i {}; i < ik_controllers.len(); ++i) {
            if (ik_controllers[i].type == 1 &&
                ik_controllers[i].bone_index == target_controller.bone_index) {
                pole_index = i;
                has_pole   = true;
                break;
            }
        }
        if (! has_pole || ik_nodes[joint_index].length <= 0.0f ||
            ik_nodes[end_index].length <= 0.0f || ! std::isfinite(ik_nodes[joint_index].length) ||
            ! std::isfinite(ik_nodes[end_index].length)) {
            DiagnosePuppetIk("missing pole controller or invalid bind length");
            continue;
        }

        // Controller positions are absolute model-space replacement poses. The paired type-1
        // controller is a pole point, so all solver inputs share this post-animation world space.
        const Vector3f start  = m_final_affines[start_index].translation();
        const Vector3f joint  = m_final_affines[joint_index].translation();
        const Vector3f end    = m_final_affines[end_index].translation();
        const auto     target = sample_controller_position(target_index);
        if (! target.active) continue;
        const auto pole = sample_controller_position(pole_index);
        if (! target.valid || ! pole.valid || ! pole.active) {
            DiagnosePuppetIk("invalid animated controller track");
            continue;
        }
        // IkNode lengths describe the bind pose. Preserve parent/animation scaling by solving with
        // the current world-space segment lengths after the positive metadata guard above.
        const float upper_length = (joint - start).norm();
        const float lower_length = (end - joint).norm();
        const auto  solution     = puppet_ik::SolveTwoBone(start,
                                                           joint,
                                                           end,
                                                           target.position,
                                                           upper_length,
                                                           lower_length,
                                                           std::addressof(pole.position));
        if (! solution.valid) {
            DiagnosePuppetIk("non-finite or degenerate animated pose");
            continue;
        }

        rotate_subtree(start_index, start, solution.start_rotation);
        rotate_subtree(joint_index, solution.joint, solution.joint_rotation);
        if (active_offline_execution != nullptr)
            ++active_offline_execution->runtime_ik_chain_solves;
    }

    for (usize i {}; i < m_final_affines.len(); ++i) {
        m_final_affines[i] *= bones[i].inv_bind.matrix();
    }
    return m_final_affines.as_slice();
}

static constexpr void genInterpolationInfo(Puppet::Animation::InterpolationInfo& info, double& cur,
                                           usize length, double frame_time, double max_time) {
    cur          = std::fmod(cur, max_time);
    double _rate = cur / frame_time;

    // `length` is the number of intervals; the track stores `length + 1`
    // frame samples (frame[0]..frame[length], where frame[length] closes
    // the loop). frame_b = frame_a + 1 is always in-range.
    info.frame_a = usize(static_cast<size_t>(_rate)) % length;
    info.frame_b = info.frame_a + usize(1);
    info.t       = _rate - static_cast<double>(info.frame_a.to_primitive());
}

static constexpr void genSingleInterpolationInfo(Puppet::Animation::InterpolationInfo& info,
                                                 double& cur, usize length, double frame_time,
                                                 double max_time) {
    if (length == usize() || frame_time <= 0.0) {
        cur          = 0.0;
        info.frame_a = usize();
        info.frame_b = usize();
        info.t       = 0.0;
        return;
    }

    cur          = std::clamp(cur, 0.0, max_time);
    double rate  = cur / frame_time;
    auto   frame = usize(static_cast<size_t>(rate));
    if (frame >= length) {
        info.frame_a = length - usize(1);
        info.frame_b = length;
        info.t       = 1.0;
        return;
    }

    info.frame_a = frame;
    info.frame_b = frame + usize(1);
    info.t       = rate - static_cast<double>(frame.to_primitive());
}

Puppet::Animation::InterpolationInfo
Puppet::Animation::getInterpolationInfo(double* cur_time) const {
    InterpolationInfo _info;
    auto&             _cur_time = *cur_time;

    if (mode == PlayMode::Loop) {
        const auto frame_count = usize(static_cast<size_t>(length));
        genInterpolationInfo(_info, _cur_time, frame_count, frame_time, max_time);
    } else if (mode == PlayMode::Single) {
        const auto frame_count = usize(static_cast<size_t>(length));
        genSingleInterpolationInfo(_info, _cur_time, frame_count, frame_time, max_time);
    } else if (mode == PlayMode::Mirror) {
        // Frames 0..length stored; mirror cycle is 0,1,..,length,length-1,..,0
        // (2*length intervals). Map any f in [0, 2*length] back into [0, length].
        const auto frame_count = usize(static_cast<size_t>(length));
        const auto _get_frame  = [frame_count](usize frame) {
            return frame <= frame_count ? frame : (frame_count * usize(2) - frame);
        };
        genInterpolationInfo(_info, _cur_time, frame_count * usize(2), frame_time, max_time * 2.0);
        _info.frame_a = _get_frame(_info.frame_a);
        _info.frame_b = _get_frame(_info.frame_b);
    }

    return _info;
}

auto PuppetLayer::AnimationLayer::Clone() const -> AnimationLayer {
    return AnimationLayer {
        .id        = id,
        .rate      = rate,
        .blend     = blend,
        .visible   = visible,
        .cur_time  = cur_time,
        .visible_binding =
            visible_binding.is_some() ? Some(NJson(*visible_binding)) : None(),
        .visible_can_change = visible_can_change,
        .layer_id  = layer_id,
        .name      = name.clone(),
        .additive  = additive,
        .blendin   = blendin,
        .blendout  = blendout,
        .blendtime = blendtime,
    };
}

static ref<str> PuppetAnimationMode(Puppet::PlayMode mode) {
    switch (mode) {
    case Puppet::PlayMode::Loop: return "loop"_str;
    case Puppet::PlayMode::Mirror: return "mirror"_str;
    case Puppet::PlayMode::Single: return "single"_str;
    }
    rstd::unreachable();
}

void PuppetLayer::prepared(slice<AnimationLayer> alayers) {
    m_layers.clear();
    m_layers.reserve(alayers.len());
    for (usize i {}; i < alayers.len(); ++i) m_layers.emplace_back();
    m_playbacks.clear();
    m_playbacks.reserve(alayers.len());
    m_texture_channel_blend_map.clear();

    usize texture_channel_count {};
    for (const auto& animation : m_puppet->anims) {
        texture_channel_count =
            rstd::cmp::max(texture_channel_count, TextureChannelTrackCount(animation));
    }
    const usize blend_map_size = ((texture_channel_count + usize(3)) / usize(4)) * usize(4);
    m_texture_channel_blend_map.reserve(blend_map_size);
    for (usize index {}; index < blend_map_size; ++index) m_texture_channel_blend_map.push(0.0f);

    const auto&           anims         = m_puppet->anims;
    const AnimationLayer* additive_base = nullptr;
    bool                  has_replace   = false;
    auto                  exists        = [&](const auto& layer) {
        for (const auto& anim : anims) {
            if (anim.id == layer.id) return true;
        }
        return false;
    };
    for (usize i {}; i < alayers.len(); ++i) {
        const auto& layer = alayers[i];
        if ((! layer.visible && ! layer.visible_can_change) || ! exists(layer)) continue;
        if (layer.additive) {
            if (! has_replace && additive_base == nullptr && layer.blend > 0.0) {
                additive_base = std::addressof(layer);
            }
            continue;
        }
        has_replace   = true;
        additive_base = nullptr;
    }

    for (usize i = alayers.len(); i != usize();) {
        --i;
        const auto&              layer     = alayers[i];
        auto                     out_layer = layer.Clone();
        const Puppet::Animation* matched   = nullptr;
        for (const auto& anim : anims) {
            if (layer.id == anim.id) {
                matched = rstd::addressof(anim);
                break;
            }
        }
        const bool ok = matched != nullptr && (layer.visible || layer.visible_can_change);

        if (ok && rstd::addressof(layer) == additive_base) {
            // Additive-only stacks still need one absolute frame[0]
            // pose; otherwise authored puppet pieces stay scattered.
            out_layer.additive = false;
        }

        Option<Arc<SceneAnimationPlayback>> playback;
        if (ok) {
            auto clip  = Arc<SceneAnimationClip>::make(SceneAnimationClipSpec {
                .name =
                    ! out_layer.name.is_empty() ? out_layer.name.clone() : matched->name.clone(),
                .mode = String::make(PuppetAnimationMode(matched->mode)),
                .fps  = static_cast<float>(matched->fps),
                .end  = i32(matched->length),
            });
            auto value = Arc<SceneAnimationPlayback>::make(rstd::move(clip), out_layer.rate <= 0.0);
            if (out_layer.rate > 0.0) value->SetRate(static_cast<float>(out_layer.rate));
            playback = Some(rstd::move(value));
        }

        m_layers[i] = Layer {
            .anim_layer = rstd::move(out_layer),
            .anim       = ok ? matched : nullptr,
            .playback   = rstd::move(playback),
        };
    }
    for (const auto& layer : m_layers) {
        if (layer.playback.is_some()) m_playbacks.push((*layer.playback).clone());
    }
}

slice<Eigen::Affine3f> PuppetLayer::genFrame(double time) noexcept {
    return m_puppet->genFrame(*this, time);
}

uint32_t PuppetLayer::boneIndex(ref<str> name) const noexcept {
    for (usize i {}; i < m_puppet->bones.len(); ++i) {
        if (m_puppet->bones[i].name == name) {
            return static_cast<uint32_t>(i.to_primitive()) + 1;
        }
    }
    return 0;
}

Option<Eigen::Affine3f> PuppetLayer::boneTransform(uint32_t index, double time) noexcept {
    if (index == 0) return None();
    const usize zero_based(index - 1);
    if (zero_based >= m_puppet->bones.len()) return None();
    auto frame = genFrame(time);
    if (zero_based >= frame.len()) return None();
    Eigen::Affine3f transform = frame[zero_based] * m_puppet->bones[zero_based].world_bind;
    return Some(rstd::move(transform));
}

Option<Eigen::Affine3f> PuppetLayer::attachmentTransform(usize index, double time) noexcept {
    if (index >= m_puppet->attachments.len()) return None();
    const auto& attachment = m_puppet->attachments[index];
    const usize bone_index(attachment.bone_index);
    if (bone_index >= m_puppet->bones.len()) return None();

    auto frame = genFrame(time);
    if (bone_index >= frame.len()) return None();
    Eigen::Affine3f transform = frame[bone_index] * attachment.bind_xform;
    return Some(rstd::move(transform));
}

auto PuppetLayer::AnimationPlaybacks() const noexcept -> slice<Arc<SceneAnimationPlayback>> {
    return m_playbacks.as_slice();
}

auto PuppetLayer::AnimationPlayback(rstd::int32_t layer_id) const noexcept
    -> Option<Arc<SceneAnimationPlayback>> {
    for (const auto& layer : m_layers) {
        if (layer.anim_layer.layer_id == layer_id && layer.playback.is_some())
            return Some((*layer.playback).clone());
    }
    return None();
}

auto PuppetLayer::AnimationLayerVisible(rstd::int32_t layer_id) const noexcept -> Option<bool> {
    for (const auto& layer : m_layers) {
        if (layer.anim_layer.layer_id == layer_id) return Some(bool(layer.anim_layer.visible));
    }
    return None();
}

bool PuppetLayer::SetAnimationLayerVisible(rstd::int32_t layer_id, bool visible) noexcept {
    for (auto& layer : m_layers) {
        if (layer.anim_layer.layer_id != layer_id) continue;
        layer.anim_layer.visible = visible;
        return true;
    }
    return false;
}

auto PuppetLayer::AnimationLayerBlend(rstd::int32_t layer_id) const noexcept -> Option<double> {
    for (const auto& layer : m_layers) {
        if (layer.anim_layer.layer_id == layer_id) return Some(double(layer.anim_layer.blend));
    }
    return None();
}

bool PuppetLayer::SetAnimationLayerBlend(rstd::int32_t layer_id, double blend) noexcept {
    for (auto& layer : m_layers) {
        if (layer.anim_layer.layer_id != layer_id) continue;
        layer.anim_layer.blend = blend;
        return true;
    }
    return false;
}

auto PuppetLayer::TextureChannelBlendMap(double time) noexcept -> slice<float> {
    updateInterpolation(time);
    for (auto& value : m_texture_channel_blend_map) value = 0.0f;

    for (const auto& layer : m_layers) {
        if (layer.anim == nullptr || ! layer.anim_layer.visible || layer.anim->trans.is_none())
            continue;

        const auto& animation = *layer.anim;
        const auto& trans     = *animation.trans;
        usize       channel {};
        auto        apply = [&](const Vec<float>& track) {
            if (! IsTextureChannelTrack(animation, track)) return;
            if (channel >= m_texture_channel_blend_map.len()) return;

            const float sample = SampleTextureChannelTrack(track, layer.interp_info);
            const float blend  = static_cast<float>(std::max(0.0, layer.anim_layer.blend));
            auto&       value  = m_texture_channel_blend_map[channel];
            if (layer.anim_layer.additive) {
                value += sample * blend;
            } else {
                const float weight = std::min(blend, 1.0f);
                value              = value * (1.0f - weight) + sample * weight;
            }
            ++channel;
        };

        apply(trans.main_track);
        for (const auto& track : trans.tail_tracks) apply(track);
    }
    return m_texture_channel_blend_map.as_slice();
}

void PuppetLayer::updateInterpolation(double) noexcept {
    for (auto& layer : m_layers) {
        if (layer) {
            double position   = layer.playback.is_some() ? (*layer.playback)->PositionSeconds()
                                                         : layer.anim_layer.cur_time;
            layer.interp_info = layer.anim->getInterpolationInfo(&position);
        }
    }
}

PuppetLayer::PuppetLayer(Arc<Puppet> pup): m_puppet(rstd::move(pup)) {}
PuppetLayer::~PuppetLayer() = default;
