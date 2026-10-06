module;
#include <atomic>
#include <cstdint>
#include <memory>

module wescene.scene;
import eigen;
import rstd;
import rstd.cppstd;

using namespace owe;
using namespace Eigen;

namespace
{
// Coarse invalidation keeps transform-only anchors and shared child membership
// out of the name cache's ownership rules. Stable scenes only load this epoch.
std::atomic_uint64_t scene_node_tree_epoch { 1 };
}

void SceneNode::MarkTreeChanged() {
    scene_node_tree_epoch.fetch_add(1, std::memory_order_relaxed);
}

Matrix4d SceneNode::GetLocalTrans() const {
    Affine3d trans = Affine3d::Identity();
    trans.prescale(m_scale.cast<double>());

    // m_rotation is in radians. Static scene.json `angles` are already radians;
    // the JS scripting API uses degrees and converts at the boundary (Script.cpp
    // NodeSetAngles / the transform actuator), so everything stored here is rad.
    trans.prerotate(AngleAxis<double>(m_rotation.x(), Vector3d::UnitX())); // x
    trans.prerotate(AngleAxis<double>(m_rotation.y(), Vector3d::UnitY())); // y
    trans.prerotate(AngleAxis<double>(m_rotation.z(), Vector3d::UnitZ())); // z

    trans.pretranslate(m_translate.cast<double>());

    return m_local_frame * trans.matrix();
}

void SceneNode::RotateObjectSpace(const Vector3f& rotation) {
    const Quaternionf current = AngleAxisf(m_rotation.z(), Vector3f::UnitZ()) *
                                AngleAxisf(m_rotation.y(), Vector3f::UnitY()) *
                                AngleAxisf(m_rotation.x(), Vector3f::UnitX());
    const Quaternionf local   = AngleAxisf(rotation.z(), Vector3f::UnitZ()) *
                                AngleAxisf(rotation.y(), Vector3f::UnitY()) *
                                AngleAxisf(rotation.x(), Vector3f::UnitX());
    const Vector3f    zyx     = (current * local).toRotationMatrix().canonicalEulerAngles(2, 1, 0);
    SetRotation({ zyx.z(), zyx.y(), zyx.x() });
}

void SceneNode::UpdateTrans() {
    if (! m_dirty) return;
    m_dirty = false;

    if (m_parent) {
        m_parent->UpdateTrans();
    }
    {
        Affine3d trans = Affine3d::Identity();
        if (m_parent) {
            trans *= m_parent->ModelTrans();
        }
        m_trans = (trans * GetLocalTrans()).matrix();
    }
}

void SceneNode::MarkTransDirty() {
    if (! m_dirty) {
        m_dirty = true;
        for (auto& child : m_children) {
            child->MarkTransDirty();
        }
        for (auto* anchor : m_transform_anchors) {
            if (anchor) anchor->MarkTransDirty();
        }
    }
}

auto SceneNode::ChildIndex(const SceneNode& child) const -> Option<usize> {
    for (usize index {}; index < m_children.len(); ++index) {
        if (m_children[index].as_ptr() == rstd::addressof(child)) return Some(index);
    }
    return None();
}

bool SceneNode::MoveChild(SceneNode& child, usize index) {
    if (index >= m_children.len()) return false;
    auto current = ChildIndex(child);
    if (current.is_none() || *current == index) return false;

    auto moving = rstd::move(m_children[*current]);
    if (*current < index) {
        for (auto cursor = *current; cursor < index; ++cursor)
            m_children[cursor] = rstd::move(m_children[cursor + usize(1)]);
    } else {
        for (auto cursor = *current; cursor > index; --cursor)
            m_children[cursor] = rstd::move(m_children[cursor - usize(1)]);
    }
    m_children[index] = rstd::move(moving);
    MarkTreeChanged();
    return true;
}

void SceneNode::ClearChildren() {
    if (m_children.is_empty()) return;
    for (auto& child : m_children) {
        if (child->m_parent == this) child->SetParentAnchor(nullptr);
    }
    m_children.clear();
    MarkTreeChanged();
}

SceneNode* SceneNode::FindByName(std::string_view name) {
    if (m_name == name) return this;
    if (m_parent == nullptr) {
        const auto epoch = u64(scene_node_tree_epoch.load(std::memory_order_relaxed));
        if (! m_name_index || m_name_index_epoch != epoch) {
            auto index = std::make_unique<Map<std::string, SceneNode*>>();
            auto visit = [&](auto& self, SceneNode& node) -> void {
                index->try_emplace(node.m_name, &node);
                for (const auto& child : node.m_children) self(self, *child);
            };
            visit(visit, *this);
            m_name_index = rstd::move(index);
            m_name_index_epoch = epoch;
        }
        const auto found = m_name_index->find(name);
        return found != m_name_index->end() ? found->second : nullptr;
    }
    for (auto& child : m_children) {
        if (auto* hit = child->FindByName(name)) return hit;
    }
    return nullptr;
}
