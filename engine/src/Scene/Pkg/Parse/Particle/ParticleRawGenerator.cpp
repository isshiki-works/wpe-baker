module;

#include <rstd/macro.hpp>

module wescene.pkg.parse;

import eigen;
import rstd;
import rstd.cppstd;
import rstd.log;
import wescene.core;
import wescene.particle;
import wescene.particle.program;
import wescene.scene;
import wescene.pkg.spec_names;

using namespace rstd::prelude;
using namespace owe;

namespace
{

struct GOption {
    bool thick_format { false };
};

struct AttrSlot {
    usize offset {};
    bool  enabled { false };
};

struct PointVertexLayout {
    AttrSlot position;
    AttrSlot texcoord;
    AttrSlot color;
    AttrSlot velocity;
};

struct RopeVertexLayout {
    AttrSlot position;
    AttrSlot endpoint;
    AttrSlot previous_point;
    AttrSlot next_point;
    AttrSlot color_end;
    AttrSlot color;
};

struct ExtractParticle {
    particle::ParticleSlot             slot;
    const particle::ParticleSlotState& state;
    const Eigen::Vector3f&             position;
    const Eigen::Vector3f&             velocity;
    const Eigen::Vector3f&             rotation;
    const Eigen::Vector3f&             color;
    const float&                       alpha;
    const float&                       size;
    const float&                       lifetime;
    const float&                       random;
    const float&                       initial_lifetime;
};

struct ExtractInstance {
    slice<particle::ParticleSlotState> states;
    slice<Eigen::Vector3f>             positions;
    slice<Eigen::Vector3f>             velocities;
    slice<Eigen::Vector3f>             rotations;
    slice<Eigen::Vector3f>             colors;
    slice<float>                       alphas;
    slice<float>                       sizes;
    slice<float>                       lifetimes;
    slice<float>                       randoms;
    slice<float>                       initial_lifetimes;
    slice<particle::ParticleSlot>      slots;
    Option<ref<TrailHistoryAttribute>> trail;
    usize                              instance_index {};

    auto Particle(particle::ParticleSlot slot) const -> ExtractParticle {
        auto index = slot.index;
        return {
            .slot             = slot,
            .state            = states[index],
            .position         = positions[index],
            .velocity         = velocities[index],
            .rotation         = rotations[index],
            .color            = colors[index],
            .alpha            = alphas[index],
            .size             = sizes[index],
            .lifetime         = lifetimes[index],
            .random           = randoms[index],
            .initial_lifetime = initial_lifetimes[index],
        };
    }
};

auto FindAttrSlot(
    const owe::Map<std::string, SceneVertexArray::SceneVertexAttributeOffset>& attributes,
    ref<str> name) noexcept -> AttrSlot {
    auto found = attributes.find(rstd::cppstd::as_string_view(name));
    if (found == attributes.end()) return {};
    return {
        .offset  = found->second.offset / usize(sizeof(float)),
        .enabled = true,
    };
}

auto ResolvePointVertexLayout(const SceneVertexArray& vertices) -> PointVertexLayout {
    const auto attributes = vertices.GetAttrOffsetMap();
    return {
        .position = FindAttrSlot(attributes, WE_IN_POSITION),
        .texcoord = FindAttrSlot(attributes, WE_IN_TEXCOORDVEC4),
        .color    = FindAttrSlot(attributes, WE_IN_COLOR),
        .velocity = FindAttrSlot(attributes, WE_IN_TEXCOORDVEC4C1),
    };
}

auto ResolveRopeVertexLayout(const SceneVertexArray& vertices, GOption option) -> RopeVertexLayout {
    const auto attributes = vertices.GetAttrOffsetMap();
    return {
        .position       = FindAttrSlot(attributes, WE_IN_POSITIONVEC4),
        .endpoint       = FindAttrSlot(attributes, WE_IN_TEXCOORDVEC4),
        .previous_point = FindAttrSlot(attributes, WE_IN_TEXCOORDVEC4C1),
        .next_point     = FindAttrSlot(
            attributes, option.thick_format ? WE_IN_TEXCOORDVEC4C2 : WE_IN_TEXCOORDVEC3C2),
        .color_end = FindAttrSlot(attributes, WE_IN_TEXCOORDVEC4C3),
        .color     = FindAttrSlot(attributes, WE_IN_COLOR),
    };
}

void Write3(mut_ref<float[]> data, AttrSlot slot, float x, float y, float z) noexcept {
    if (! slot.enabled) return;
    data[slot.offset]            = x;
    data[slot.offset + usize(1)] = y;
    data[slot.offset + usize(2)] = z;
}

void Write3(mut_ref<float[]> data, AttrSlot slot, const Eigen::Vector3f& source) noexcept {
    Write3(data, slot, source[0], source[1], source[2]);
}

void Write4(mut_ref<float[]> data, AttrSlot slot, float x, float y, float z, float w) noexcept {
    if (! slot.enabled) return;
    Write3(data, slot, x, y, z);
    data[slot.offset + usize(3)] = w;
}

void Write4(mut_ref<float[]> data, AttrSlot slot, const Eigen::Vector3f& source, float w) noexcept {
    Write4(data, slot, source[0], source[1], source[2], w);
}

void WriteColor(mut_ref<float[]> data, AttrSlot slot, const ExtractParticle& value) noexcept {
    Write4(data, slot, value.color[0], value.color[1], value.color[2], value.alpha);
}

auto AnimationLifetime(const ExtractParticle& value, ParticleAnimationSpec animation) noexcept
    -> float {
    if (value.lifetime <= 0.0f) return 0.0f;
    switch (animation.mode) {
    case ParticleAnimationMode::RANDOMONE:
        return std::clamp(value.random, 0.0f, std::nextafter(1.0f, 0.0f));
    case ParticleAnimationMode::SEQUENCE:
        if (value.initial_lifetime == 0.0f) return 0.0f;
        return (1.0f - (value.lifetime / value.initial_lifetime)) * animation.sequence_multiplier;
    }
    return 0.0f;
}

void GenParticlePointData(slice<ExtractInstance> instances, const ParticleSubSystem& subsystem,
                          GOption option, const PointVertexLayout& layout,
                          SceneVertexWriter& writer) noexcept {
    for (const auto& instance : instances) {
        if (subsystem.InstanceState(instance.instance_index).no_live_particle) continue;
        for (auto slot : instance.slots) {
            auto value = instance.Particle(slot);
            if (value.lifetime <= 0.0f) continue;

            auto render_position =
                subsystem.RenderPosition(instance.instance_index, value.position);
            auto lifetime    = AnimationLifetime(value, subsystem.AnimationSpec());
            auto destination = writer.AppendZeroedVertex();
            if (destination.is_none()) return;
            auto data = *destination;
            Write3(
                data, layout.position, render_position[0], render_position[1], render_position[2]);
            Write4(data,
                   layout.texcoord,
                   value.rotation[0],
                   value.rotation[1],
                   value.rotation[2],
                   value.size * 0.5f);
            Write4(data, layout.color, value.color[0], value.color[1], value.color[2], value.alpha);
            if (option.thick_format) {
                Write4(data,
                       layout.velocity,
                       value.velocity[0],
                       value.velocity[1],
                       value.velocity[2],
                       lifetime);
            }
        }
    }
}

void GenRopeParticleData(slice<ExtractInstance> instances, const ParticleSubSystem& subsystem,
                         GOption option, const RopeVertexLayout& layout,
                         SceneVertexWriter& writer) {
    for (const auto& instance : instances) {
        if (subsystem.InstanceState(instance.instance_index).no_live_particle) continue;
        std::vector<usize> slots;
        slots.reserve(instance.slots.len().to_primitive());
        for (auto slot : instance.slots) {
            if (instance.lifetimes[slot.index] > 0.0f) slots.push_back(slot.index);
        }
        std::sort(slots.begin(), slots.end(), [&](usize lhs, usize rhs) {
            return instance.states[lhs].spawn_sequence < instance.states[rhs].spawn_sequence;
        });
        if (slots.size() < 2) continue;

        auto particle = [&](std::size_t index) {
            return instance.Particle(particle::ParticleSlot { slots[index] });
        };
        auto render_position = [&](const ExtractParticle& value) {
            return subsystem.RenderPosition(instance.instance_index, value.position);
        };

        auto emit_group = [&](std::size_t begin, std::size_t end) -> bool {
            if (end - begin < 2) return true;

            auto  newest      = particle(end - 1);
            auto  before      = particle(end - 2);
            auto  newest_age  = newest.initial_lifetime - newest.lifetime;
            auto  before_age  = before.initial_lifetime - before.lifetime;
            auto  emit_period = before_age - newest_age;
            float sequence_offset {};
            if (emit_period > 1e-6f)
                sequence_offset = -std::clamp(newest_age / emit_period, 0.0f, 1.0f);

            auto segment_count = end - begin - 1;
            for (std::size_t index = begin; index < end - 1; ++index) {
                auto previous_index = index == begin ? begin : index - 1;
                auto next_index     = index + 1;
                auto after_index    = std::min(index + 2, end - 1);
                auto current        = particle(index);
                auto previous       = particle(previous_index);
                auto next           = particle(next_index);
                auto after          = particle(after_index);

                auto destination = writer.AppendZeroedVertex();
                if (destination.is_none()) return false;
                auto data = *destination;
                Write4(data, layout.position, render_position(current), current.size * 0.5f);
                Write4(data,
                       layout.endpoint,
                       render_position(next),
                       static_cast<float>(segment_count));
                Write4(data,
                       layout.previous_point,
                       render_position(previous),
                       static_cast<float>(index - begin) + sequence_offset);
                if (option.thick_format)
                    Write4(data, layout.next_point, render_position(after), next.size * 0.5f);
                else
                    Write3(data, layout.next_point, render_position(after));
                WriteColor(data, layout.color_end, next);
                WriteColor(data, layout.color, current);
            }
            return true;
        };

        auto sequence_count = subsystem.RopeSequenceCount();
        if (sequence_count.is_none()) {
            if (! emit_group(0, slots.size())) return;
            continue;
        }

        auto count = rstd::as_cast<u64>(rstd::cmp::max(*sequence_count, u32(2)));
        auto group = [&](std::size_t index) {
            return instance.states[slots[index]].spawn_sequence / count;
        };
        std::size_t begin {};
        while (begin < slots.size()) {
            auto        sequence_group = group(begin);
            std::size_t end            = begin + 1;
            while (end < slots.size() && group(end) == sequence_group) ++end;
            if (! emit_group(begin, end)) return;
            begin = end;
        }
    }
}

void GenRopeTrailSegments(const ExtractParticle& value, const ParticleSubSystem& subsystem,
                          usize instance_index, const TrailHistoryAttribute& trails, GOption option,
                          const RopeVertexLayout& layout, SceneVertexWriter& writer) {
    auto state = trails.State(value.slot);
    if (state.len == usize()) return;

    auto size         = value.size * 0.5f;
    auto trail_length = static_cast<float>(state.sample_count.to_primitive());

    auto point = [&](usize point_index) -> Eigen::Vector3f {
        if (point_index == usize()) return subsystem.RenderPosition(instance_index, value.position);
        return subsystem.RenderPosition(instance_index,
                                        trails.At(value.slot, state.len - point_index));
    };

    for (usize sample_index {}; sample_index < state.len; ++sample_index) {
        auto previous       = point(sample_index);
        auto current        = point(sample_index + usize(1));
        auto start_control  = point(sample_index == usize() ? usize() : sample_index - usize(1));
        auto end_control    = point(rstd::cmp::min(sample_index + usize(2), state.len));
        auto trail_position = static_cast<float>(sample_index.to_primitive());
        auto destination    = writer.AppendZeroedVertex();
        if (destination.is_none()) return;
        auto data = *destination;
        Write4(data, layout.position, previous, size);
        Write4(data, layout.endpoint, current, trail_length);
        Write4(data, layout.previous_point, start_control, trail_position);
        if (option.thick_format)
            Write4(data, layout.next_point, end_control, size);
        else
            Write3(data, layout.next_point, end_control);
        WriteColor(data, layout.color_end, value);
        WriteColor(data, layout.color, value);
    }
}

void GenRopeTrailData(slice<ExtractInstance> instances, const ParticleSubSystem& subsystem,
                      GOption option, const RopeVertexLayout& layout, SceneVertexWriter& writer) {
    for (const auto& instance : instances) {
        if (subsystem.InstanceState(instance.instance_index).no_live_particle) continue;
        if (instance.trail.is_none()) continue;
        auto trails = *instance.trail;
        for (auto slot : instance.slots) {
            auto value = instance.Particle(slot);
            if (value.lifetime <= 0.0f) continue;
            GenRopeTrailSegments(
                value, subsystem, instance.instance_index, *trails, option, layout, writer);
            if (writer.Overflowed()) return;
        }
    }
}

} // namespace

void ParticleRawGenerator::Compile(particle::ParticleViewCompiler& compiler) {
    auto attributes = m_subsystem->Attributes();
    compiler.ReadBase(attributes.position);
    m_velocity         = compiler.Read(attributes.velocity);
    m_rotation         = compiler.Read(attributes.rotation);
    m_color            = compiler.Read(attributes.color);
    m_alpha            = compiler.Read(attributes.alpha);
    m_size             = compiler.Read(attributes.size);
    m_lifetime         = compiler.Read(attributes.lifetime);
    m_random           = compiler.Read(attributes.random);
    m_initial_lifetime = compiler.Read(attributes.initial_lifetime);
    auto trail_key     = m_subsystem->TrailKey();
    if (trail_key.is_some()) m_trail = compiler.ReadObject(*trail_key);
}

void ParticleRawGenerator::Extract(particle::ParticleExtractContext& context) {
    auto instances = rstd::vec::Vec<ExtractInstance>::with_capacity(context.instances.len());
    for (const auto& instance : context.instances) {
        Option<ref<TrailHistoryAttribute>> trail = None();
        if (m_trail.Valid()) trail = Some(instance.view.ReadObject(m_trail));
        instances.emplace_back(ExtractInstance {
            .states            = instance.view.States(),
            .positions         = instance.view.Positions(),
            .velocities        = instance.view.Read(m_velocity),
            .rotations         = instance.view.Read(m_rotation),
            .colors            = instance.view.Read(m_color),
            .alphas            = instance.view.Read(m_alpha),
            .sizes             = instance.view.Read(m_size),
            .lifetimes         = instance.view.Read(m_lifetime),
            .randoms           = instance.view.Read(m_random),
            .initial_lifetimes = instance.view.Read(m_initial_lifetime),
            .slots             = instance.slots,
            .trail             = trail,
            .instance_index    = instance.instance_index,
        });
    }

    auto&   mesh     = m_subsystem->Mesh();
    auto&   vertices = mesh.GetVertexArray(usize());
    GOption option {
        .thick_format = vertices.GetOption(rstd::cppstd::as_string_view(WE_CB_THICK_FORMAT)),
    };

    auto rope       = vertices.GetOption(rstd::cppstd::as_string_view(WE_PRENDER_ROPE));
    auto rope_trail = vertices.GetOption(rstd::cppstd::as_string_view(WE_PRENDER_ROPE_TRAIL));
    auto result     = vertices.RewriteVertices([&](SceneVertexWriter& writer) {
        if (rope || rope_trail) {
            auto layout = ResolveRopeVertexLayout(vertices, option);
            if (rope) {
                GenRopeParticleData(instances.as_slice(), *m_subsystem, option, layout, writer);
            } else {
                GenRopeTrailData(instances.as_slice(), *m_subsystem, option, layout, writer);
            }
            return;
        }

        auto layout = ResolvePointVertexLayout(vertices);
        GenParticlePointData(instances.as_slice(), *m_subsystem, option, layout, writer);
    });
    if (result.overflowed) {
        rstd_error("particle vertex capacity exceeded: written={}, capacity={}",
                   result.vertex_count,
                   result.capacity);
    }
}
