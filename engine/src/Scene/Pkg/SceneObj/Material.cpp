module;

#include <rstd/macro.hpp>
#include "JsonNlohmann.hpp"

module wescene.pkg.scene_obj;
import rstd.log;
import rstd.cppstd;
import wescene.json;

using namespace owe::wpscene;
using namespace rstd::literals;

namespace
{

void LoadUserShaderValues(const owe::NJson&                             json,
                          std::unordered_map<std::string, std::string>& out) {
    auto values = owe::Find(json, "usershadervalues");
    if (values == nullptr || ! values->is_object()) return;
    for (const auto& [entry_key, entry_value] : values->items()) {
        if (entry_value.is_string()) out[entry_key] = entry_value.get_ref<const std::string&>();
    }
}

void MergeUserTextures(const std::vector<owe::NJson>& src, std::vector<owe::NJson>& dst) {
    if (src.size() > dst.size()) dst.resize(src.size());
    for (std::size_t i = 0; i < src.size(); ++i) {
        if (! src[i].is_null()) dst[i] = src[i];
    }
}

void LoadConstantShaderValue(std::string name, const owe::NJson& json,
                             std::unordered_map<std::string, std::vector<float>>& constant_values,
                             FieldBindings&                                       bindings) {
    std::vector<float> value;
    owe::GetJsonValue(json, value);
    constant_values[name] = std::move(value);
    if (! json.is_object()) return;

    (void)AbsorbFieldBinding(name, json, bindings);
}

} // namespace

auto owe::wpscene::Material::clone() const -> Material {
    Material clone;
    clone.blending                      = blending;
    clone.cullmode                      = cullmode;
    clone.shader                        = shader;
    clone.alphawriting                  = alphawriting;
    clone.depthtest                     = depthtest;
    clone.depthwrite                    = depthwrite;
    clone.textures                      = textures;
    clone.combos                        = combos;
    clone.constantshadervalues          = constantshadervalues;
    clone.user_shader_values            = user_shader_values;
    clone.use_puppet                    = use_puppet;
    clone.constantshadervalues_bindings = constantshadervalues_bindings.clone();
    MergeUserTextures(usertextures, clone.usertextures);
    return clone;
}

bool MaterialPassBindItem::FromJson(const owe::NJson& json) {
    owe::GetJsonValue(json, "name", name);
    owe::GetJsonValue(json, "index", index);
    return true;
}

void MaterialPass::Update(const MaterialPass& p) {
    std::size_t i = 0;
    for (const auto& el : p.textures) {
        if (p.textures.size() > textures.size()) textures.resize(p.textures.size());
        if (! el.empty()) {
            textures[i] = el;
        }
        ++i;
    }
    for (const auto& el : p.constantshadervalues) {
        constantshadervalues[el.first] = el.second;
    }
    constantshadervalues_bindings.Update(p.constantshadervalues_bindings);
    for (const auto& el : p.user_shader_values) {
        user_shader_values[el.first] = el.second;
    }
    MergeUserTextures(p.usertextures, usertextures);
    for (const auto& el : p.combos) {
        combos[el.first] = el.second;
    }
}

void Material::MergePass(const MaterialPass& p) {
    MergeBindingOverrides(p.textures, p.usertextures, p.combos);
    for (const auto& el : p.constantshadervalues) {
        constantshadervalues[el.first] = el.second;
    }
    constantshadervalues_bindings.Update(p.constantshadervalues_bindings);
    for (const auto& el : p.user_shader_values) {
        user_shader_values[el.first] = el.second;
    }
}

void Material::MergeBindingOverrides(const std::vector<std::string>&             textures,
                                     const std::vector<owe::NJson>&              usertextures,
                                     const std::unordered_map<std::string, i32>& combos) {
    if (textures.size() > this->textures.size()) this->textures.resize(textures.size());
    for (std::size_t i = 0; i < textures.size(); ++i) {
        if (! textures[i].empty()) this->textures[i] = textures[i];
    }
    MergeUserTextures(usertextures, this->usertextures);
    for (const auto& el : combos) {
        this->combos[el.first] = el.second;
    }
}

bool MaterialPass::FromJson(const owe::NJson& json) {
    owe::GetJsonValue(json, "id", id, false);
    if (auto values = owe::Find(json, "textures"); values != nullptr) {
        if (values->is_array()) {
            for (const auto& jT : *values) {
                std::string tex;
                if (! jT.is_null()) owe::GetJsonValue(jT, tex);
                textures.push_back(std::move(tex));
            }
        }
    }
    if (auto values = owe::Find(json, "usertextures"); values != nullptr) {
        if (values->is_array())
            for (const auto& jU : *values) usertextures.push_back(jU);
    }
    if (auto values = owe::Find(json, "constantshadervalues"); values != nullptr) {
        if (values->is_object())
            for (const auto& [entry_key, entry_value] : values->items())
                LoadConstantShaderValue(
                    entry_key, entry_value, constantshadervalues, constantshadervalues_bindings);
    }
    LoadUserShaderValues(json, user_shader_values);
    if (auto values = owe::Find(json, "combos"); values != nullptr) {
        if (values->is_object())
            for (const auto& [entry_key, entry_value] : values->items()) {
                i32 value { 0 };
                owe::GetJsonValue(entry_value, value);
                combos[entry_key] = value;
            }
    }
    owe::GetJsonValue(json, "target", target, false);
    if (auto values = owe::Find(json, "bind"); values != nullptr) {
        if (values->is_array()) {
            for (const auto& jB : *values) {
                MaterialPassBindItem bindItem;
                bindItem.FromJson(jB);
                bind.push_back(bindItem);
            }
        }
    }
    return true;
}

bool Material::FromJson(const owe::NJson& json) { return FromJson(json, kSceneVersionUnknown); }

bool Material::FromJson(const owe::NJson& json, SceneVersion /*v*/) {
    auto passes = owe::Find(json, "passes");
    if (passes == nullptr) {
        rstd_error("material no data");
        return false;
    }
    if (! passes->is_array() || passes->empty()) {
        rstd_error("material no data");
        return false;
    }
    const auto& jContent = (*passes)[0];
    if (owe::Find(jContent, "shader") == nullptr) {
        rstd_error("material no shader");
        return false;
    }
    owe::GetJsonValue(jContent, "blending", blending);
    owe::GetJsonValue(jContent, "cullmode", cullmode);
    owe::GetJsonValue(jContent, "alphawriting", alphawriting, false);
    owe::GetJsonValue(jContent, "depthtest", depthtest);
    owe::GetJsonValue(jContent, "depthwrite", depthwrite);
    owe::GetJsonValue(jContent, "shader", shader);
    if (auto values = owe::Find(jContent, "textures"); values != nullptr) {
        if (values->is_array()) {
            for (const auto& jT : *values) {
                std::string tex;
                if (! jT.is_null()) owe::GetJsonValue(jT, tex);
                textures.push_back(std::move(tex));
            }
        }
    }
    if (auto values = owe::Find(jContent, "usertextures"); values != nullptr) {
        if (values->is_array())
            for (const auto& jU : *values) usertextures.push_back(jU);
    }
    if (auto values = owe::Find(jContent, "constantshadervalues"); values != nullptr) {
        if (values->is_object())
            for (const auto& [entry_key, entry_value] : values->items())
                LoadConstantShaderValue(
                    entry_key, entry_value, constantshadervalues, constantshadervalues_bindings);
    }
    LoadUserShaderValues(jContent, user_shader_values);
    if (auto values = owe::Find(jContent, "combos"); values != nullptr) {
        if (values->is_object())
            for (const auto& [entry_key, entry_value] : values->items()) {
                i32 value { 0 };
                owe::GetJsonValue(entry_value, value);
                combos[entry_key] = value;
            }
    }
    return true;
}
