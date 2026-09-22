module;

module wescene.scene;
import eigen;
import rstd;
import rstd.cppstd;

using namespace owe;
using namespace Eigen;

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
    return true;
}

SceneNode* SceneNode::FindByName(std::string_view name) {
    if (m_name == name) return this;
    for (auto& child : m_children) {
        if (auto* hit = child->FindByName(name)) return hit;
    }
    return nullptr;
}
