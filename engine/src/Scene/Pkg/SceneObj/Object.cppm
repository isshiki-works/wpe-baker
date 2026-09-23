module;
#include <owe/compat.hpp>
#include <owe/std.hpp>

export module wescene.pkg.scene_obj:object;
import wescene.fs;
import wescene.json;
import :scene_document;
import :image_object;
import :light_object;
import :misc_object;
import :particle_object;
import :sound_object;

using namespace rstd::prelude;

export namespace owe::wpscene
{

struct ContainerObject {
    bool FromJson(const owe::NJson&);

    i32                  id { 0 };
    std::string          name;
    std::array<float, 3> origin { 0.0f, 0.0f, 0.0f };
    std::array<float, 3> scale { 1.0f, 1.0f, 1.0f };
    std::array<float, 3> angles { 0.0f, 0.0f, 0.0f };
    ParallaxDepthBinding parallax;
    bool                 visible { true };
    bool                 solid { false };
    bool                 disable_propagation { false };
    u32                  parent { 0 };
    std::string          attachment;
    Vec<i32>             dependencies;
    owe::NJson           instance;
    VisibleUserBinding   visible_user;
    FieldBindings        field_bindings;
};

// 场景对象：各类对象的 std::variant。
using SceneObject = std::variant<ContainerObject, ImageObject, ShapeObject, ParticleObject,
                                 SoundObject, LightObject, TextObject, ModelObject, CameraObject>;

Vec<SceneObject> DecodeSceneObjects(ref<SceneDocument>, mut_ref<fs::VFS>);

} // namespace owe::wpscene
