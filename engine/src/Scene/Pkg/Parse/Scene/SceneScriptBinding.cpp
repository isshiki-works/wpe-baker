module;

#include <rstd/enum.hpp>

#include "JsonNlohmann.hpp"

module wescene.pkg.parse;
import :scene_context;
import eigen;
import wescene.pkg.spec_names;
import wescene.core;
import wescene.types;
import rstd;
import rstd.log;
import rstd.cppstd;
import wescene.utils;
import wescene.scene;
import wescene.text;
import wescene.script;

using namespace rstd::prelude;
using namespace rstd::literals;
using rstd::collections::HashMap;
using rstd::collections::HashSet;
using rstd::cppstd::as_str;
using rstd::cppstd::as_string_view;
using rstd::slice_::sort_unstable_by;
using rstd::sync::Arc;
using namespace owe;
using namespace Eigen;

namespace owe
{

auto LoadJsonFile(fs::VFS& vfs, const std::string& path) -> Option<NJson> {
    auto parsed = owe::ReadNJsonFile(vfs, path);
    if (parsed.is_err()) {
        auto error = rstd::move(parsed).unwrap_err_unchecked();
        rstd_error("Can't load json {}: {}", path, error.message.as_str());
        return None();
    }
    return Some(rstd::move(parsed).unwrap_unchecked());
}

template<typename T>
struct CopyableArcHold {
    Arc<T> value;

    explicit CopyableArcHold(Arc<T> owner): value(rstd::move(owner)) {}
    CopyableArcHold(const CopyableArcHold& other): value(other.value.clone()) {}
    CopyableArcHold(CopyableArcHold&&) noexcept            = default;
    CopyableArcHold& operator=(CopyableArcHold&&) noexcept = default;
    CopyableArcHold& operator=(const CopyableArcHold&)     = delete;
};

bool SourceWritesLayerText(std::string_view src) {
    const bool writes_text = src.find(".text") != std::string_view::npos ||
                             src.find("[\"text\"]") != std::string_view::npos ||
                             src.find("['text']") != std::string_view::npos;
    if (! writes_text) return false;
    return src.find("getLayer") != std::string_view::npos;
}

bool FieldBindingsWriteLayerText(const wpscene::FieldBindings& fb) {
    for (const auto& binding : fb.Entries()) {
        if (binding.script.is_some() && SourceWritesLayerText(binding.script->source)) return true;
    }
    return false;
}

const wpscene::FieldBindings& SceneObjectFieldBindings(const SceneObjectVar& object) {
    RSTD_MATCH(object) {
        RSTD_CASE(Container, value) { return value.field_bindings; }
        RSTD_CASE(Image, value) { return value.field_bindings; }
        RSTD_CASE(Shape, value) { return value.field_bindings; }
        RSTD_CASE(Particle, value) { return value.field_bindings; }
        RSTD_CASE(Sound, value) { return value.field_bindings; }
        RSTD_CASE(Light, value) { return value.field_bindings; }
        RSTD_CASE(Text, value) { return value.field_bindings; }
        RSTD_CASE(Model, value) { return value.field_bindings; }
        RSTD_CASE(Camera, value) { return value.field_bindings; }
    }
    rstd::unreachable();
}

bool SceneWritesLayerText(slice<SceneObjectVar> scene_objs) {
    for (usize index {}; index < scene_objs.len(); ++index) {
        if (FieldBindingsWriteLayerText(SceneObjectFieldBindings(scene_objs[index]))) return true;
    }
    return false;
}

bool SceneHasScripts(slice<SceneObjectVar> scene_objs) {
    for (usize index {}; index < scene_objs.len(); ++index) {
        for (const auto& binding : SceneObjectFieldBindings(scene_objs[index]).Entries()) {
            if (binding.script.is_some()) return true;
        }
    }
    return false;
}

bool AppendLayerCompositePassthroughEffect(fs::VFS& vfs, wpscene::ImageObject& image) {
    wpscene::Material material;
    auto              json = LoadJsonFile(vfs, "/assets/materials/util/effectpassthrough.json");
    if (! json || ! material.FromJson(*json)) {
        rstd_error("parse effectpassthrough.json failed for '{}'", image.name);
        return false;
    }

    wpscene::ImageEffect effect;
    effect.name    = "linked layer composite";
    effect.visible = true;
    effect.materials.push_back(std::move(material));
    image.effects.push_back(std::move(effect));
    return true;
}

Arc<PuppetLayer> MakePuppetLayer(Arc<Puppet>                            puppet,
                                 std::span<PuppetLayer::AnimationLayer> layers) {
    auto out = Arc<PuppetLayer>::make(rstd::move(puppet));
    out->prepared(
        slice<PuppetLayer::AnimationLayer>::from_raw_parts(layers.data(), usize(layers.size())));
    return out;
}

void RegisterPuppetLayer(SceneParseContext& context, SceneNode* node, Arc<PuppetLayer> layer) {
    if (! node) return;
    for (const auto& playback : layer->AnimationPlaybacks())
        node->RegisterPuppetAnimation(playback.clone());
    (void)context.puppet_layers->by_node.insert(node, rstd::move(layer));
}

Option<Arc<PuppetLayer>> LookupPuppetLayer(const Arc<PuppetLayerRegistry>& layers,
                                           SceneNode*                      node) {
    if (! node) return None();
    if (auto layer = layers->by_node.get(node); layer.is_some()) return Some((**layer).clone());
    if (auto fallback = layers->fallback_by_node.get(node); fallback.is_some()) {
        return Some((**fallback).clone());
    }
    return None();
}

SceneNode* RootOf(SceneNode* node) {
    if (! node) return nullptr;
    while (node->Parent()) node = node->Parent();
    return node;
}

void MarkHiddenLinkSource(SceneParseContext& context, i32 id) {
    if (context.hidden_link_source_ids.contains(i32(id)))
        context.scene->MarkLayerVisibilityElidable(WallpaperLayerId { .value = i32(id) });
}

SceneUserVisibilityBinding
ToSceneUserVisibilityBinding(const wpscene::VisibleUserBinding& binding) {
    SceneUserVisibilityBinding out;
    out.key           = String::make(rstd::cppstd::as_str(binding.name).unwrap());
    out.condition     = std::make_shared<const NJson>(binding.condition);
    out.has_condition = binding.has_condition;
    return out;
}

array<float, 2> Texture0UvScale(const SceneMaterial& material, bool nopadding) {
    if (nopadding) return { 1.0f, 1.0f };
    auto it = material.customShader.constValues.find(WE_GLTEX_RESOLUTION_NAMES[usize()]);
    if (it == material.customShader.constValues.end()) return { 1.0f, 1.0f };
    const auto& r = it->second;
    if (r.size() < usize(4) || r[usize(0)] == 0.0f || r[usize(1)] == 0.0f) {
        return { 1.0f, 1.0f };
    }
    return { r[usize(2)] / r[usize(0)], r[usize(3)] / r[usize(1)] };
}

void InstallImageAlignmentBinding(script::JsRuntime& runtime, SceneNode* node, ref<str> alignment,
                                  const SceneParseContext::ImageAlignmentSetter& setter) {
    runtime.RegisterImageAlignmentSetter(node, alignment, setter.clone());
}

void RegisterImageAlignmentBinding(SceneParseContext& context, SceneNode* node, ref<str> alignment,
                                   SceneParseContext::ImageAlignmentSetter setter) {
    if (context.script_scene.is_some()) {
        InstallImageAlignmentBinding((*context.script_scene)->runtime(), node, alignment, setter);
    }
    context.image_alignment_bindings.push(SceneParseContext::ImageAlignmentBinding {
        .node      = node,
        .alignment = String::make(alignment),
        .setter    = rstd::move(setter),
    });
}

Option<Arc<PuppetLayer>> FindPuppetLayerWithBone(const Arc<PuppetLayerRegistry>& layers,
                                                 SceneNode* node, std::string_view name,
                                                 std::uint32_t& index) {
    if (! node) return None();
    if (auto layer = layers->by_node.get(node); layer.is_some()) {
        index = (**layer)->boneIndex(rstd::cppstd::as_str(name).unwrap());
        if (index != 0) return Some((**layer).clone());
    }
    for (auto& child : node->GetChildren()) {
        auto hit = FindPuppetLayerWithBone(layers, child.as_ptr(), name, index);
        if (hit.is_some()) return hit;
    }
    return None();
}

script::ScriptScene& EnsureScriptScene(SceneParseContext& context) {
    if (context.installed_script_scene != nullptr) return *context.installed_script_scene;
    if (context.script_scene.is_none()) {
        context.script_scene =
            Some(Box<script::ScriptScene>::make(Some(context.audio_response_demand.clone())));
        auto layers = CopyableArcHold(context.puppet_layers.clone());
        (*context.script_scene)
            ->runtime()
            .SetBoneResolvers(
                [layers](SceneNode* node, std::string_view name) -> std::uint32_t {
                    auto          layer = LookupPuppetLayer(layers.value, node);
                    std::uint32_t index =
                        layer.is_some() ? (*layer)->boneIndex(rstd::cppstd::as_str(name).unwrap())
                                        : 0;
                    if (index != 0) return index;

                    if (auto fallback =
                            FindPuppetLayerWithBone(layers.value, RootOf(node), name, index);
                        fallback.is_some()) {
                        (void)layers.value->fallback_by_node.insert(node, rstd::move(*fallback));
                        return index;
                    }
                    return 0;
                },
                [layers](SceneNode*    node,
                         std::uint32_t index,
                         double        time) -> Option<script::BoneTranslation> {
                    auto layer = LookupPuppetLayer(layers.value, node);
                    if (layer.is_none()) return None();
                    auto bone = (*layer)->boneTransform(index, time);
                    if (bone.is_none()) return None();

                    node->UpdateTrans();
                    Eigen::Affine3f world = Eigen::Affine3f::Identity();
                    world.matrix()        = node->ModelTrans().cast<float>();
                    Eigen::Vector3f t     = (world * *bone).translation();
                    return Some(script::BoneTranslation { t.x(), t.y(), t.z() });
                });
        if (context.user_properties != nullptr)
            for (const auto& [key, value] : context.user_properties->items())
                (*context.script_scene)->runtime().SetUserProperty(key, value);
        for (const auto& binding : context.image_alignment_bindings) {
            InstallImageAlignmentBinding((*context.script_scene)->runtime(),
                                         binding.node,
                                         binding.alignment.as_str(),
                                         binding.setter);
        }
    }
    return **context.script_scene;
}

void SetScriptInitializationOrder(SceneParseContext& context, script::FieldScript& script,
                                  const SceneNode* node) {
    if (node == nullptr) return;
    auto order = context.script_initialization_orders.get(node->ID());
    if (order.is_none()) return;
    EnsureScriptScene(context).runtime().SetInitializationOrder(script, **order);
}

void TrackRegisteredAssets(SceneParseContext& context, script::FieldScript* script) {
    if (script && ! script->RegisteredAssets().is_empty())
        context.registered_asset_scripts.push(rstd::move(script));
}

Option<float> ScriptValueAsFloat(const script::ScriptValue& value) {
    if (auto* p = std::get_if<script::ScalarValue>(&value)) return Some(static_cast<float>(p->v));
    if (auto* p = std::get_if<script::BoolValue>(&value)) return Some(p->v ? 1.0f : 0.0f);
    if (auto* p = std::get_if<script::Vec2Value>(&value)) return Some(static_cast<float>(p->x));
    if (auto* p = std::get_if<script::Vec3Value>(&value)) return Some(static_cast<float>(p->x));
    return None();
}

Option<array<float, 2>> ScriptValueAsVec2(const script::ScriptValue& value) {
    auto* vector = std::get_if<script::Vec2Value>(&value);
    if (vector == nullptr) return None();
    return Some(array<float, 2> {
        static_cast<float>(vector->x),
        static_cast<float>(vector->y),
    });
}

Option<Vector3f> ScriptValueAsVec3(const script::ScriptValue& value, const Vector3f& current) {
    Vector3f next = current;
    if (auto* p = std::get_if<script::Vec3Value>(&value)) {
        next = Vector3f { static_cast<float>(p->x),
                          static_cast<float>(p->y),
                          static_cast<float>(p->z) };
    } else if (auto* p = std::get_if<script::Vec2Value>(&value)) {
        next = Vector3f { static_cast<float>(p->x), static_cast<float>(p->y), current.z() };
    } else if (auto* p = std::get_if<script::ScalarValue>(&value)) {
        next.x() = static_cast<float>(p->v);
    } else
        return None();
    return Some(next);
}

bool IsFractionSliderProperty(const SceneParseContext& context, const NJson& binding) {
    if (context.user_properties == nullptr || ! binding.is_object()) return false;
    const auto* user = Find(binding, "user");
    if (user == nullptr || ! user->is_string()) return false;
    const auto* prop = Find(*context.user_properties, user->get_ref<const std::string&>());
    if (prop == nullptr || ! prop->is_object()) return false;
    const auto* type = Find(*prop, "type");
    if (type == nullptr || ! type->is_string() || type->get_ref<const std::string&>() != "slider")
        return false;
    const auto* fraction = Find(*prop, "fraction");
    return fraction != nullptr && fraction->is_boolean() && fraction->get<bool>();
}

NJson ScriptPropertiesForField(const SceneParseContext& context, std::string_view field,
                               const NJson& properties, const wpscene::ScriptBinding& binding) {
    NJson props = properties;
    if (field != "scale" || binding.source.find("/10000") == std::string::npos ||
        ! props.is_object())
        return props;

    for (auto& [key, item] : props.items()) {
        if (IsFractionSliderProperty(context, item)) item["__scriptValueScale"] = 50.0;
    }
    return props;
}

NJson ScriptInitialValueForField(std::string_view field, const NJson& value) {
    if (field != "angles") return value;

    constexpr float kRadToDeg = 180.0f / rstd::f32::consts::PI.to_primitive();
    auto in_float_range = [](double number) {
        return number >= std::numeric_limits<float>::lowest() &&
               number <= std::numeric_limits<float>::max();
    };
    if (value.is_null()) return NJson();
    if (value.is_number()) {
        const double number = value.get<double>();
        return in_float_range(number) ? NJson(double(static_cast<float>(number) * kRadToDeg))
                                      : NJson();
    }

    if (value.is_object()) {
        NJson out = value;
        for (const char* axis : { "x", "y", "z" }) {
            auto member = out.find(axis);
            if (member == out.end() || ! member->is_number()) continue;
            const double number = member->get<double>();
            if (in_float_range(number)) *member = double(static_cast<float>(number) * kRadToDeg);
        }
        return out;
    }

    Vec<float> values;
    if (owe::GetJsonValue(value, values) && ! values.is_empty()) {
        NJson out = NJson::array();
        for (float axis : values) out.push_back(double(axis * kRadToDeg));
        return out;
    }

    return value;
}

} // namespace owe

namespace owe
{

namespace
{

Option<SceneUserVisibilityBinding> AnimationLayerVisibleUserBinding(const NJson& visible) {
    if (! visible.is_object()) return None();
    const auto* user = Find(visible, "user");
    if (user == nullptr) return None();

    auto to_string = [](const NJson& text) {
        return String::make(rstd::cppstd::as_str(text.get_ref<const std::string&>()).unwrap());
    };
    SceneUserVisibilityBinding binding;
    if (user->is_string()) {
        binding.key = to_string(*user);
    } else if (user->is_object()) {
        if (const auto* name = Find(*user, "name"); name != nullptr && name->is_string())
            binding.key = to_string(*name);
        if (const auto* condition = Find(*user, "condition"); condition != nullptr) {
            binding.condition     = std::make_shared<const NJson>(*condition);
            binding.has_condition = true;
        }
    }
    return binding.empty() ? None() : Some(rstd::move(binding));
}

} // namespace

void WireFieldScripts(SceneParseContext& context, const Arc<SceneNode>& node_sp,
                      const wpscene::FieldBindings&                   fb,
                      std::function<void(const script::ScriptValue&)> origin_apply,
                      std::function<void(const script::ScriptValue&)> scale_apply) {
    SceneNode* node = node_sp.as_ptr();

    auto parallax_binding = fb.Get("parallaxDepth"_str);
    if (parallax_binding.is_some() && (**parallax_binding).user.is_some() && node->ID() != i32() &&
        ! context.parallax_depth_user_binding_ids.contains(node->ID())) {
        context.parallax_depth_user_binding_ids.insert(node->ID());
        auto state = CopyableArcHold(context.uniform_state.clone());
        context.scene->RegisterUserPropertyBinding(
            (**parallax_binding).user->clone(),
            Box<dyn<FnMut<void(ref<NJson>)>>>::make(
                [state, object_id = node->ID()](ref<NJson> property) mutable {
                    (void)state.value->ApplyObjectParallaxDepth(object_id, *property);
                }));
    }
    auto& ss = EnsureScriptScene(context);
    auto& rt = ss.runtime();

    for (const auto& binding : fb.Entries()) {
        if (binding.script.is_none()) continue;
        const auto&                 sb    = *binding.script;
        auto                        field = rstd::cppstd::as_string_view(binding.field.as_str());
        script::NodeTransformTarget tgt   = script::NodeTransformTarget::Translate;
        script::FieldKind           kind;
        bool                        has_actuator = true;
        bool                        is_alpha     = false;
        bool                        is_color     = false;
        bool                        is_volume    = false;
        bool                        is_parallax  = false;
        bool                        is_visible   = false;
        if (field == "origin") {
            tgt  = script::NodeTransformTarget::Translate;
            kind = script::FieldKind::Vec3;
        } else if (field == "scale") {
            tgt  = script::NodeTransformTarget::Scale;
            kind = script::FieldKind::Vec3;
        } else if (field == "angles") {
            tgt  = script::NodeTransformTarget::Rotation;
            kind = script::FieldKind::Vec3;
        } else if (field == "visible") {
            kind       = script::FieldKind::Bool;
            is_visible = true;
        } else if (field == "alpha") {
            kind     = script::FieldKind::Scalar;
            is_alpha = true;
        } else if (field == "color") {
            kind     = script::FieldKind::Vec3;
            is_color = true;
        } else if (field == "volume") {
            kind      = script::FieldKind::Scalar;
            is_volume = true;
        } else if (field == "parallaxDepth") {
            kind        = script::FieldKind::Vec2;
            is_parallax = true;
        } else {
            // text/rate/intensity/... are wired elsewhere or not yet supported.
            continue;
        }
        std::string sha = utils::genSha1(std::span<const char>(sb.source));
        auto props =
            ScriptPropertiesForField(context, field, binding.ScriptProperties(), sb);
        auto initial_value = ScriptInitialValueForField(field, sb.initial_value);
        Option<Arc<SceneAnimationPlayback>> animation;
        if (binding.animation.is_some())
            animation = Some(ResolveAnimationTrack(context, binding).playback.clone());
        auto* fs = rt.MakeFieldScript(sb.source,
                                      sha,
                                      kind,
                                      props,
                                      initial_value,
                                      script::ScriptBindingContext::ForLayer(
                                          node, binding.field.as_str(), rstd::move(animation)));
        if (! fs) continue;
        SetScriptInitializationOrder(context, *fs, node);
        TrackRegisteredAssets(context, fs);
        if (! has_actuator) continue;
        if (is_alpha)
            ss.AddActuator({ fs, script::MakeNodeAlphaApply(node_sp.clone()) });
        else if (is_visible)
            ss.AddActuator({
                fs,
                [scene = context.scene.get(), hold = CopyableArcHold(node_sp.clone())](const script::ScriptValue& value) {
                    if (scene == nullptr) return;
                    if (auto* visible = std::get_if<script::BoolValue>(&value))
                        (void)scene->SetNodeVisible(*hold.value, visible->v);
                },
            });
        else if (is_color)
            ss.AddActuator({ fs, script::MakeNodeColorApply(node_sp.clone()) });
        else if (is_volume)
            ss.AddActuator({ fs, script::MakeNodeVolumeApply(node_sp.clone()) });
        else if (is_parallax) {
            auto state = CopyableArcHold(context.uniform_state.clone());
            ss.AddActuator(
                { fs, [state, object_id = node->ID()](const script::ScriptValue& value) mutable {
                     auto depth = ScriptValueAsVec2(value);
                     if (depth.is_some())
                         (void)state.value->SetObjectParallaxDepth(object_id, *depth);
                 } });
        } else if (field == "origin" && origin_apply)
            ss.AddActuator({ fs, origin_apply });
        else if (field == "scale" && scale_apply)
            ss.AddActuator({ fs, scale_apply });
        else
            ss.AddActuator({ fs, script::MakeNodeTransformApply(node_sp.clone(), tgt) });
    }
}

void WirePuppetAnimationLayerScripts(SceneParseContext& context,
                                     const Arc<SceneNode>& owner,
                                     const Arc<PuppetLayer>& puppet_layer,
                                     std::span<PuppetLayer::AnimationLayer> authored_layers) {
    for (const auto& authored : authored_layers) {
        if (authored.visible_binding.is_none() || ! authored.visible_binding->is_object()) continue;

        auto user_binding = AnimationLayerVisibleUserBinding(*authored.visible_binding);
        const bool user_controls_visibility = user_binding.is_some();
        if (user_binding.is_some()) {
            if (context.user_properties != nullptr) {
                const auto* property = Find(*context.user_properties,
                                            rstd::cppstd::as_string_view(user_binding->key.as_str()));
                if (property != nullptr) {
                    auto visible = ResolveSceneUserVisibilityBinding(*user_binding, *property);
                    if (visible.is_some())
                        (void)puppet_layer->SetAnimationLayerVisible(authored.layer_id, *visible);
                }
            }
            auto hold = CopyableArcHold(puppet_layer.clone());
            context.scene->RegisterUserPropertyBinding(
                user_binding->key.clone(),
                Box<dyn<FnMut<void(ref<NJson>)>>>::make(
                    [hold, layer_id = authored.layer_id,
                     binding = rstd::move(*user_binding)](ref<NJson> property) mutable {
                        auto visible = ResolveSceneUserVisibilityBinding(binding, *property);
                        if (visible.is_some())
                            (void)hold.value->SetAnimationLayerVisible(layer_id, *visible);
                    }));
        }

        wpscene::FieldBindings fields;
        (void)wpscene::AbsorbFieldBinding("visible", *authored.visible_binding, fields);
        auto binding = fields.Get("visible"_str);
        if (binding.is_none() || (**binding).script.is_none()) continue;

        auto playback = puppet_layer->AnimationPlayback(authored.layer_id);
        if (playback.is_none()) {
            rstd_error("animation layer {} on '{}' has no playback; its visible script cannot be bound",
                       authored.layer_id,
                       owner->Name());
            continue;
        }

        const auto& script_binding = *(**binding).script;
        auto&       scripts        = EnsureScriptScene(context);
        auto&       runtime        = scripts.runtime();
        NJson initial_value = script_binding.initial_value;
        if (user_controls_visibility) {
            auto visible = puppet_layer->AnimationLayerVisible(authored.layer_id);
            if (visible.is_some()) initial_value = bool(*visible);
        }
        std::string sha = utils::genSha1(std::span<const char>(script_binding.source));
        auto* field_script = runtime.MakeFieldScript(
            script_binding.source,
            sha,
            script::FieldKind::Bool,
            (**binding).ScriptProperties(),
            initial_value,
            script::ScriptBindingContext::ForAnimationLayer(owner.as_ptr(),
                                                             puppet_layer.clone(),
                                                             authored.layer_id,
                                                             "visible"_str,
                                                             rstd::move(*playback)));
        if (field_script == nullptr) continue;
        SetScriptInitializationOrder(context, *field_script, owner.as_ptr());
        TrackRegisteredAssets(context, field_script);

        // A top-level `visible.user` binding owns the selected runtime value.
        // Its setter above updates the PuppetLayer directly; replaying an
        // init-only script's cached return every frame would undo later user
        // property changes. Scripts without a user binding keep the normal
        // visibility return actuator.
        if (user_controls_visibility) continue;

        auto hold = CopyableArcHold(puppet_layer.clone());
        scripts.AddActuator(
            { field_script,
              [hold, layer_id = authored.layer_id](const script::ScriptValue& value) mutable {
                  auto visible = ScriptValueAsFloat(value);
                  if (visible.is_some())
                      (void)hold.value->SetAnimationLayerVisible(layer_id, *visible >= 0.5f);
              } });
    }
}

// An effect's `visible` can be driven by a script, the same way a layer's
// can. The effect is already registered with the scene, so the actuator
// only has to flip its runtime visibility; the render graph rebuild is
// handled by Scene::SetImageEffectRuntimeVisible.
void WireImageEffectVisibilityScript(SceneParseContext& context, SceneNode* node,
                                     const wpscene::ImageEffect& effect, SceneEffectId effect_id) {
    const auto* binding = effect.visible_binding();
    if (binding == nullptr || binding->script.is_none() || ! effect_id.Valid()) return;
    const auto& sb = *binding->script;

    auto&                               ss  = EnsureScriptScene(context);
    auto&                               rt  = ss.runtime();
    std::string                         sha = utils::genSha1(std::span<const char>(sb.source));
    Option<Arc<SceneAnimationPlayback>> animation;
    if (binding->animation.is_some()) {
        auto track = ResolveAnimationTrack(context, *binding);
        node->RegisterAnimation(track.playback.clone());
        animation = Some(rstd::move(track.playback));
    }
    auto* fs =
        rt.MakeFieldScript(sb.source,
                           sha,
                           script::FieldKind::Bool,
                           binding->ScriptProperties(),
                           sb.initial_value,
                           script::ScriptBindingContext::ForEffect(
                               node, { .id = effect_id }, "visible"_str, rstd::move(animation)));
    if (! fs) return;
    SetScriptInitializationOrder(context, *fs, node);
    TrackRegisteredAssets(context, fs);

    auto* scene = context.scene.get();
    ss.AddActuator({ fs, [scene, effect_id](const script::ScriptValue& value) {
                        auto flag = ScriptValueAsFloat(value);
                        if (! flag) return;
                        (void)scene->SetImageEffectRuntimeVisible({ .id = effect_id },
                                                                  *flag >= 0.5f);
                    } });
}

void WireCameraShakeScripts(SceneParseContext& context, const wpscene::FieldBindings& fb) {
    auto& ss = EnsureScriptScene(context);
    auto& rt = ss.runtime();

    for (const auto& binding : fb.Entries()) {
        if (binding.script.is_none()) continue;
        const auto&       sb    = *binding.script;
        auto              field = rstd::cppstd::as_string_view(binding.field.as_str());
        script::FieldKind kind  = script::FieldKind::Scalar;
        if (field == "camerashake") {
            kind = script::FieldKind::Bool;
        } else if (field != "camerashakeamplitude" && field != "camerashakespeed" &&
                   field != "camerashakeroughness") {
            continue;
        }

        std::string                         sha = utils::genSha1(std::span<const char>(sb.source));
        Option<Arc<SceneAnimationPlayback>> animation;
        if (binding.animation.is_some()) {
            auto track = ResolveAnimationTrack(context, binding);
            if (context.global_camera_node.is_some())
                (**context.global_camera_node).RegisterAnimation(track.playback.clone());
            animation = Some(rstd::move(track.playback));
        }
        auto* fs = rt.MakeFieldScript(sb.source,
                                      sha,
                                      kind,
                                      binding.ScriptProperties(),
                                      sb.initial_value,
                                      script::ScriptBindingContext::ForLayer(
                                          nullptr, binding.field.as_str(), rstd::move(animation)));
        if (! fs) continue;
        TrackRegisteredAssets(context, fs);

        auto state = mut_ref<UniformSceneState>::from_raw_parts(context.uniform_state.as_ptr());
        auto field_name = rstd::cppstd::to_string(binding.field.as_str());
        ss.AddActuator({ fs, [state, field_name](const script::ScriptValue& value) mutable {
                            auto scalar = ScriptValueAsFloat(value);
                            if (! scalar) return;
                            auto& shake = state->CameraShake();
                            if (field_name == "camerashake")
                                shake.enable = *scalar >= 0.5f;
                            else if (field_name == "camerashakeamplitude")
                                shake.amplitude = *scalar;
                            else if (field_name == "camerashakespeed")
                                shake.speed = *scalar;
                            else if (field_name == "camerashakeroughness")
                                shake.roughness = *scalar;
                        } });
    }
}

void WireCameraFieldScripts(SceneParseContext& context, const Arc<SceneNode>& node_sp,
                            const Arc<SceneCamera>& camera, const Arc<SceneCameraPath>& camera_path,
                            const wpscene::FieldBindings& fb, const Vector3f& translate_bias,
                            const Vector3f& rotation_bias) {
    SceneNode* node = node_sp.as_ptr();
    auto&      ss   = EnsureScriptScene(context);
    auto&      rt   = ss.runtime();

    for (const auto& binding : fb.Entries()) {
        if (binding.script.is_none()) continue;
        const auto&       sb    = *binding.script;
        auto              field = rstd::cppstd::as_string_view(binding.field.as_str());
        script::FieldKind kind  = script::FieldKind::Vec3;
        if (field == "visible") {
            kind = script::FieldKind::Bool;
        } else if (field != "origin" && field != "angles") {
            continue;
        }

        std::string sha           = utils::genSha1(std::span<const char>(sb.source));
        auto        initial_value = ScriptInitialValueForField(field, sb.initial_value);
        auto*       fs            = rt.MakeFieldScript(
            sb.source,
            sha,
            kind,
            binding.ScriptProperties(),
            initial_value,
            script::ScriptBindingContext::ForLayer(
                node, binding.field.as_str(), node->FieldAnimation(binding.field.as_str())));
        if (! fs) continue;
        SetScriptInitializationOrder(context, *fs, node);
        TrackRegisteredAssets(context, fs);

        if (field == "origin") {
            auto path         = CopyableArcHold(camera_path.clone());
            auto camera_owner = CopyableArcHold(camera.clone());
            ss.AddActuator(
                { fs, [node, camera_owner, path, translate_bias](const script::ScriptValue& value) {
                     Vector3f current = path.value->origin_base;
                     auto     next    = ScriptValueAsVec3(value, current);
                     if (next) {
                         path.value->origin_base = *next;
                         node->SetTranslate(translate_bias + *next);
                         camera_owner.value->Update();
                     }
                 } });
        } else if (field == "angles") {
            auto path         = CopyableArcHold(camera_path.clone());
            auto camera_owner = CopyableArcHold(camera.clone());
            ss.AddActuator(
                { fs, [node, camera_owner, path, rotation_bias](const script::ScriptValue& value) {
                     constexpr float kRadToDeg = 180.0f / rstd::f32::consts::PI.to_primitive();
                     constexpr float kDegToRad = rstd::f32::consts::PI.to_primitive() / 180.0f;
                     Vector3f        current   = path.value->rotation_base;
                     current *= kRadToDeg;
                     auto next = ScriptValueAsVec3(value, current);
                     if (next) {
                         path.value->rotation_base = *next * kDegToRad;
                         node->SetRotation(rotation_bias + *next * kDegToRad);
                         camera_owner.value->Update();
                     }
                 } });
        }
    }
}

} // namespace owe
