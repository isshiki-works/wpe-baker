module;

#include <rstd/macro.hpp>

module wescene.scene_user_property;

import eigen;
import rstd;
import rstd.cppstd;
import rstd.log;
import wescene.fs;
import wescene.pkg.parse;
import wescene.script;
import wescene.pkg.spec_names;

using namespace rstd::prelude;
using namespace rstd::literals;
using rstd::cppstd::as_str;
using rstd::cppstd::as_string_view;
using rstd::cppstd::to_string;

namespace owe
{
namespace
{

constexpr std::string_view kSchemeColorKey          = "schemecolor";
constexpr std::string_view kWaywallenSchemeColorKey = "waywallen.scheme_color";

bool ParseFloatList(std::string_view source, std::vector<float>& out) {
    out.clear();
    std::size_t offset = 0;
    while (offset < source.size()) {
        while (offset < source.size() && (source[offset] == ' ' || source[offset] == '\t'))
            ++offset;
        if (offset >= source.size()) break;
        std::size_t start = offset;
        while (offset < source.size() && source[offset] != ' ' && source[offset] != '\t') ++offset;
        try {
            out.push_back(std::stof(std::string(source.substr(start, offset - start))));
        } catch (...) {
            return false;
        }
    }
    return ! out.empty();
}

struct UserPropertyCoerceResult {
    bool        ok { false };
    ShaderValue value;
    const char* skip_reason { nullptr };
};

UserPropertyCoerceResult CoerceUserPropertyValue(const Json& property) {
    UserPropertyCoerceResult result;

    std::string type;
    if (auto member = property.get("type"_str); member.is_some()) {
        auto string = (*member)->as_str();
        if (string.is_some()) type = rstd::cppstd::to_string(*string);
    }

    if (type == "combo") {
        result.skip_reason = "shader graph mutation is not a uniform update";
        return result;
    }
    if (type == "texture" || type == "replacetexture" || type == "file" || type == "textinput") {
        result.skip_reason = "non-uniform property type";
        return result;
    }

    auto        value = property.get("value"_str);
    const Json& raw   = value.is_some() ? **value : property;
    if (type == "color") {
        std::vector<float> values;
        auto               string = raw.as_str();
        if (string.is_some() && ParseFloatList(rstd::cppstd::as_string_view(*string), values) &&
            values.size() >= 3) {
            result.ok    = true;
            result.value = ShaderValue(std::span<const float>(values.data(), values.size()));
            return result;
        }
        result.skip_reason = "color value not a 'r g b[ a]' float string";
        return result;
    }

    if (raw.is_boolean()) {
        result.ok    = true;
        result.value = ShaderValue(*raw.as_bool() ? 1.0f : 0.0f);
        return result;
    }
    if (raw.is_number()) {
        auto number = raw.as_f64();
        if (number.is_some()) {
            const double native = number->to_primitive();
            result.ok           = native >= std::numeric_limits<float>::lowest() &&
                                  native <= std::numeric_limits<float>::max();
            if (result.ok) result.value = ShaderValue(static_cast<float>(native));
        }
        return result;
    }
    if (raw.is_string()) {
        std::vector<float> values;
        if (ParseFloatList(rstd::cppstd::as_string_view(*raw.as_str()), values)) {
            result.ok    = true;
            result.value = values.size() == 1
                               ? ShaderValue(values.front())
                               : ShaderValue(std::span<const float>(values.data(), values.size()));
            return result;
        }
        result.skip_reason = "string value isn't parseable as float list";
        return result;
    }
    result.skip_reason = "unsupported JSON value shape";
    return result;
}

bool IsShaderGraphUserProperty(const Json& property) {
    auto type = property.get("type"_str);
    if (type.is_none()) return false;
    auto string = (*type)->as_str();
    return string.is_some() && rstd::cppstd::as_string_view(*string) == "combo";
}

Option<array<float, 3>> ApplyClear(Scene& scene, const std::string& key, const Json& property) {
    auto user_key = scene.ClearColorUserKey();
    if (user_key.is_empty() || CanonicalSceneUserPropertyKey(as_string_view(user_key)) != key)
        return None();
    auto color = ResolveSceneUserPropertyColor(property);
    if (color.is_none()) return None();
    scene.SetClearColor(*color);
    return color;
}

void ApplyShaderUniforms(Scene& scene, const std::string& key, const Json& property) {
    auto bindings = scene.ShaderUserBindings(as_str(key).unwrap());
    if (bindings.is_empty()) return;
    if (IsShaderGraphUserProperty(property)) {
        rstd_warn("user property '{}' skipped: shader graph mutation is not a uniform update", key);
        return;
    }

    auto coerced = CoerceUserPropertyValue(property);
    if (! coerced.ok) {
        rstd_warn("user property '{}' skipped: {}",
                  key,
                  coerced.skip_reason ? coerced.skip_reason : "unknown");
        return;
    }
    for (usize index {}; index < bindings.len(); ++index) {
        const auto& binding = bindings[index];
        if (binding.material) {
            scene.SetMaterialShaderValue(
                *binding.material, binding.uniform.as_str(), coerced.value);
        }
    }
}

Option<String> ResolveTextureProperty(const Json& property) {
    if (property.is_string()) return Some(String::make(*property.as_str()));
    if (! property.is_object()) return None();

    std::string type;
    if (auto member = property.get("type"_str); member.is_some()) {
        auto string = (*member)->as_str();
        if (string.is_some()) type = rstd::cppstd::to_string(*string);
    }
    if (! type.empty() && type != "scenetexture" && type != "texture" && type != "replacetexture")
        return None();
    auto value = property.get("value"_str);
    if (value.is_none()) return None();
    auto string = (*value)->as_str();
    return string.is_some() ? Some(String::make(*string)) : None();
}

bool SameMaterialId(SceneMaterialId lhs, SceneMaterialId rhs) {
    return lhs.index == rhs.index && lhs.generation == rhs.generation;
}

void PushUniqueMaterial(Vec<SceneMaterialId>& materials, SceneMaterialId id) {
    for (usize index {}; index < materials.len(); ++index) {
        if (SameMaterialId(materials[index], id)) return;
    }
    materials.push(rstd::move(id));
}

Vec<SceneMaterialId> ApplyTextureProperty(Scene& scene, const std::string& key,
                                          const Json& property) {
    Vec<SceneMaterialId> changed;
    auto                 bindings = scene.MaterialTextureUserBindings(as_str(key).unwrap());
    if (bindings.is_empty()) return changed;

    auto texture = ResolveTextureProperty(property);
    if (texture.is_none()) return changed;
    for (usize binding_index {}; binding_index < bindings.len(); ++binding_index) {
        const auto& binding = bindings[binding_index];
        if (! binding.material) continue;
        auto next = texture->is_empty() ? binding.fallback.as_str() : texture->as_str();
        auto mutation =
            scene.SetMaterialTextureSlot(*binding.material, binding.slot, as_string_view(next));
        if (mutation.changed && mutation.material.is_some()) {
            PushUniqueMaterial(changed, *mutation.material);
        }
    }
    return changed;
}

Option<String> ResolveShaderComboValue(const Json&                          property,
                                       const Scene::ShaderComboUserBinding& binding) {
    auto        member = property.get("value"_str);
    const auto& value  = member.is_some() ? **member : property;
    if (value.is_null()) return Some(binding.fallback.clone());
    if (value.is_boolean()) return Some(String::make(*value.as_bool() ? "1"_str : "0"_str));
    if (value.is_number()) {
        auto number = value.as_f64();
        if (number.is_some()) {
            const double native = number->to_primitive();
            if (native >= std::numeric_limits<int>::min() &&
                native <= std::numeric_limits<int>::max())
                return Some(
                    String::make(as_str(std::to_string(static_cast<int>(native))).unwrap()));
        }
        return None();
    }
    if (! value.is_string()) return None();

    auto text = String::make(*value.as_str());
    if (text.is_empty()) return Some(binding.fallback.clone());
    auto option = binding.options.get(text.as_str());
    if (option.is_some()) return Some((*option)->clone());
    if (text == "true"_str) return Some(String::make("1"_str));
    if (text == "false"_str) return Some(String::make("0"_str));
    try {
        auto        native = to_string(text.as_str());
        std::size_t parsed = 0;
        int         number = std::stoi(native, &parsed);
        if (parsed == native.size())
            return Some(String::make(as_str(std::to_string(number)).unwrap()));
    } catch (...) {
    }
    return None();
}

void RecordShaderComboDiagnostic(Scene& scene, std::string key,
                                 SceneUserPropertyDiagnosticCode code, std::string material,
                                 std::string combo, std::string message) {
    scene.AddUserPropertyDiagnostic(SceneUserPropertyDiagnostic {
        .key      = String::make(rstd::cppstd::as_str(key).unwrap()),
        .code     = code,
        .material = String::make(rstd::cppstd::as_str(material).unwrap()),
        .combo    = String::make(rstd::cppstd::as_str(combo).unwrap()),
        .message  = String::make(rstd::cppstd::as_str(message).unwrap()),
    });
}

bool ApplyShaderCombos(Scene& scene, const std::string& key, const Json& property) {
    auto bindings = scene.ShaderComboUserBindings(as_str(key).unwrap());
    if (bindings.is_empty()) return false;
    scene.ClearUserPropertyDiagnostics(as_str(key).unwrap());

    ShaderCache* shader_cache = nullptr;
    if (auto cache = scene.ExtensionMut<Arc<ShaderCache>>(); cache.is_some()) {
        shader_cache = (**cache).as_ptr();
    }
    auto vfs = scene.ExtensionMut<fs::VFS>();
    if (vfs.is_none()) {
        rstd_warn("user property '{}' skipped: scene VFS is not available", key);
        RecordShaderComboDiagnostic(scene,
                                    key,
                                    SceneUserPropertyDiagnosticCode::SceneVfsUnavailable,
                                    {},
                                    {},
                                    "scene VFS is not available");
        return false;
    }

    bool graph_changed = false;
    for (usize binding_index {}; binding_index < bindings.len(); ++binding_index) {
        const auto& binding = bindings[binding_index];
        if (! binding.material) continue;
        auto next = ResolveShaderComboValue(property, binding);
        if (next.is_none()) {
            rstd_warn("user property '{}' skipped: combo '{}' value is unsupported",
                      key,
                      binding.combo.as_str());
            RecordShaderComboDiagnostic(
                scene,
                key,
                SceneUserPropertyDiagnosticCode::UnsupportedShaderComboValue,
                binding.material->name,
                to_string(binding.combo.as_str()),
                "shader combo value is unsupported");
            continue;
        }
        auto& material = *binding.material;
        if (material.customShader.variant.is_none()) {
            rstd_warn("user property '{}' skipped: material '{}' has no shader variant descriptor",
                      key,
                      material.name);
            RecordShaderComboDiagnostic(
                scene,
                key,
                SceneUserPropertyDiagnosticCode::MissingShaderVariantDescriptor,
                material.name,
                to_string(binding.combo.as_str()),
                "material has no shader variant descriptor");
            continue;
        }
        const auto& current_variant = *material.customShader.variant;
        auto        combo           = to_string(binding.combo.as_str());
        auto        next_value      = to_string(next->as_str());
        if (auto current = current_variant.resolved_combos.find(combo);
            current != current_variant.resolved_combos.end() && current->second == next_value)
            continue;

        auto compiled = ShaderParser::CompileSceneShaderVariant(
            current_variant, **vfs, { { combo, next_value } }, shader_cache);
        if (! compiled.ok || ! compiled.shader) {
            rstd_warn("user property '{}' skipped: shader combo '{}' compile failed: {}",
                      key,
                      binding.combo.as_str(),
                      compiled.error);
            RecordShaderComboDiagnostic(scene,
                                        key,
                                        SceneUserPropertyDiagnosticCode::ShaderComboCompileFailed,
                                        material.name,
                                        combo,
                                        compiled.error);
            continue;
        }
        auto mutation = scene.SetMaterialShaderVariant(material,
                                                       SceneShaderVariantMutation {
                                                           .shader  = rstd::move(compiled.shader),
                                                           .variant = rstd::move(compiled.variant),
                                                       });
        graph_changed |= mutation.changed && (material.DirtyFlags() & SceneMaterialDirtyGraph) != 0;
    }
    return graph_changed;
}

float CurrentImageAlpha(SceneNode& node) {
    return node.IsAlphaOverridden() ? node.EffectiveAlpha() : node.BaseAlpha();
}

Eigen::Vector3f CurrentImageColor(SceneNode& node) {
    return node.IsColorOverridden() ? node.Color() : node.BaseColor();
}

bool MaterialHasUniform(const SceneMaterial& material, ref<str> uniform_name) {
    const auto name = to_string(uniform_name);
    if (material.customShader.constValues.contains(name)) return true;
    if (material.customShader.shader &&
        material.customShader.shader->default_uniforms.contains(name))
        return true;
    return material.customShader.variant.is_some() &&
           material.customShader.variant->default_uniforms.contains(name);
}

void ApplyImageColor(Scene& scene, const std::string& key, const Json& property) {
    auto bindings = scene.ImageColorUserBindings(as_str(key).unwrap());
    if (bindings.is_empty()) return;
    auto coerced = CoerceUserPropertyValue(property);
    if (! coerced.ok || coerced.value.size() < usize(3)) return;

    Eigen::Vector3f color { coerced.value[usize()],
                            coerced.value[usize(1)],
                            coerced.value[usize(2)] };
    for (usize binding_index {}; binding_index < bindings.len(); ++binding_index) {
        const auto& binding = bindings[binding_index];
        SceneNode&  node    = *binding.node;
        node.SetColor(color);
        std::array<float, 3> color3 { color.x(), color.y(), color.z() };
        for (usize material_index {}; material_index < binding.materials.len(); ++material_index) {
            const auto& material = binding.materials[material_index];
            if (! material) continue;
            const bool  has_user_alpha = MaterialHasUniform(*material, G_USERALPHA);
            const float alpha = has_user_alpha ? node.BaseAlpha() : CurrentImageAlpha(node);
            std::array<float, 4> color4 { color.x(), color.y(), color.z(), alpha };
            if (MaterialHasUniform(*material, G_COLOR4))
                scene.SetMaterialShaderValue(*material, G_COLOR4, color4);
            if (MaterialHasUniform(*material, G_COLOR))
                scene.SetMaterialShaderValue(*material, G_COLOR, color3);
        }
    }
}

void ApplyImageAlpha(Scene& scene, const std::string& key, const Json& property) {
    auto bindings = scene.ImageAlphaUserBindings(as_str(key).unwrap());
    if (bindings.is_empty()) return;
    auto coerced = CoerceUserPropertyValue(property);
    if (! coerced.ok || coerced.value.size() < usize(1)) return;

    const float alpha = std::clamp(coerced.value[usize()], 0.0f, 1.0f);
    for (usize binding_index {}; binding_index < bindings.len(); ++binding_index) {
        const auto& binding = bindings[binding_index];
        SceneNode&  node    = *binding.node;
        node.SetUserAlpha(alpha);
        auto                 color = CurrentImageColor(node);
        std::array<float, 4> color4 { color.x(), color.y(), color.z(), alpha };
        for (usize material_index {}; material_index < binding.materials.len(); ++material_index) {
            const auto& material = binding.materials[material_index];
            if (! material) continue;
            const bool has_user_alpha = MaterialHasUniform(*material, G_USERALPHA);
            if (has_user_alpha) scene.SetMaterialShaderValue(*material, G_USERALPHA, alpha);
            if (MaterialHasUniform(*material, G_ALPHA))
                scene.SetMaterialShaderValue(*material, G_ALPHA, alpha);
            if (! has_user_alpha && MaterialHasUniform(*material, G_COLOR4))
                scene.SetMaterialShaderValue(*material, G_COLOR4, color4);
        }
    }
}

void ApplyParticles(Scene& scene, const std::string& key, const Json& property) {
    auto controls = scene.ParticleOverrideBindings(as_str(key).unwrap());
    if (controls.is_empty()) return;
    auto coerced = CoerceUserPropertyValue(property);
    if (! coerced.ok) return;
    auto values = slice<float>::from_raw_parts(coerced.value.data(), coerced.value.size());
    for (usize index {}; index < controls.len(); ++index) controls[index]->Apply(values);
}

void ApplySoundVolume(Scene& scene, const std::string& key, const Json& property) {
    auto controls = scene.SoundVolumeBindings(rstd::cppstd::as_str(key).unwrap());
    if (controls.is_empty()) return;
    auto coerced = CoerceUserPropertyValue(property);
    if (! coerced.ok || coerced.value.size() < usize(1)) return;
    const float volume = std::clamp(coerced.value[usize()], 0.0f, 1.0f);
    for (usize index {}; index < controls.len(); ++index) controls[index]->SetVolume(volume);
}

} // namespace

std::string CanonicalSceneUserPropertyKey(std::string_view key) {
    return key == kWaywallenSchemeColorKey ? std::string(kSchemeColorKey) : std::string(key);
}

Option<array<float, 3>> ResolveSceneUserPropertyColor(const Json& property) {
    auto coerced = CoerceUserPropertyValue(property);
    if (! coerced.ok || coerced.value.size() < usize(3)) return None();
    auto clamp01 = [](float value) {
        return std::clamp(value, 0.0f, 1.0f);
    };
    return Some(array<float, 3> { clamp01(coerced.value[usize()]),
                                  clamp01(coerced.value[usize(1)]),
                                  clamp01(coerced.value[usize(2)]) });
}

SceneUserPropertyMutation SceneUserPropertyApplier::Apply(Scene& scene, std::string_view raw_key,
                                                          const Json& property) {
    SceneUserPropertyMutation mutation;
    std::string               key = CanonicalSceneUserPropertyKey(raw_key);
    mutation.diagnostics_changed = ! scene.ShaderComboUserBindings(as_str(key).unwrap()).is_empty();

    script::SetSceneUserProperty(scene, key, property);
    mutation.clear_color = ApplyClear(scene, key, property);
    ApplyShaderUniforms(scene, key, property);
    mutation.texture_materials = ApplyTextureProperty(scene, key, property);
    mutation.graph_changed     = ApplyShaderCombos(scene, key, property);
    ApplyImageColor(scene, key, property);
    ApplyImageAlpha(scene, key, property);
    scene.ApplyUserTextBindings(as_str(key).unwrap(), property);
    ApplyParticles(scene, key, property);
    ApplySoundVolume(scene, key, property);
    scene.ApplyUserPropertyBindings(as_str(key).unwrap(), property);
    scene.ApplyUserCameraPathVisibilityBindings(key, property);
    mutation.graph_changed =
        scene.ApplyUserNodeVisibilityBindings(key, property) || mutation.graph_changed;
    mutation.graph_changed =
        scene.ApplyUserImageEffectVisibilityBindings(key, property) || mutation.graph_changed;
    return mutation;
}

SceneUserPropertyMutation SceneUserPropertyApplier::ApplyAll(Scene&                 scene,
                                                             const rstd::json::Map& properties) {
    SceneUserPropertyMutation result;
    properties.iter().for_each([&](auto entry) {
        auto [key, property] = entry;
        auto mutation        = Apply(scene, rstd::cppstd::as_string_view(key->as_str()), *property);
        result.graph_changed |= mutation.graph_changed;
        result.diagnostics_changed |= mutation.diagnostics_changed;
        if (mutation.clear_color.is_some()) result.clear_color = mutation.clear_color;
        for (auto material : mutation.texture_materials) {
            PushUniqueMaterial(result.texture_materials, material);
        }
    });
    return result;
}

Vec<SceneMaterialId> SceneUserPropertyApplier::ApplyTexture(Scene& scene, std::string_view raw_key,
                                                            const Json& property) {
    return ApplyTextureProperty(scene, CanonicalSceneUserPropertyKey(raw_key), property);
}

Vec<SceneUserPropertyDiagnostic> CollectSceneUserPropertyDiagnostics(const Scene&     scene,
                                                                     std::string_view raw_key) {
    Vec<SceneUserPropertyDiagnostic> out;
    const std::string                key         = CanonicalSceneUserPropertyKey(raw_key);
    auto                             diagnostics = scene.UserPropertyDiagnostics();
    for (usize index {}; index < diagnostics.len(); ++index) {
        const auto& diagnostic = diagnostics[index];
        if (diagnostic.key == rstd::cppstd::as_str(key).unwrap()) out.push(diagnostic.Clone());
    }
    return out;
}

} // namespace owe
