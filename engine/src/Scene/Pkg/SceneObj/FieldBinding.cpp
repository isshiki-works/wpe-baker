module;

#include "JsonNlohmann.hpp"

module wescene.pkg.scene_obj;
import rstd;
import rstd.cppstd;

using namespace rstd::literals;
using rstd::sync::atomic::Atomic;
using rstd::sync::atomic::Ordering;

namespace
{
Atomic<u64> next_field_binding_identity { u64(1) };
}

namespace owe::wpscene
{

bool ParseAnimKeyframeTangent(const owe::NJson& json, AnimKeyframeTangent& out) {
    if (! json.is_object()) return false;
    out.enabled = true;
    owe::GetJsonValue(json, "enabled", out.enabled, false);
    owe::GetJsonValue(json, "x", out.x, false);
    owe::GetJsonValue(json, "y", out.y, false);
    owe::GetJsonValue(json, "magic", out.magic, false);
    return true;
}

bool ParseAnimKeyframe(const owe::NJson& json, AnimKeyframe& out) {
    if (! json.is_object()) return false;
    owe::GetJsonValue(json, "frame", out.frame, false);
    owe::GetJsonValue(json, "value", out.value, false);
    owe::GetJsonValue(json, "step", out.step, false);
    owe::GetJsonValue(json, "lockangle", out.lockangle, false);
    owe::GetJsonValue(json, "locklength", out.locklength, false);
    if (auto front = owe::Find(json, "front"); front != nullptr)
        ParseAnimKeyframeTangent(*front, out.front);
    if (auto back = owe::Find(json, "back"); back != nullptr)
        ParseAnimKeyframeTangent(*back, out.back);
    return true;
}

bool ParseAnimAxis(const owe::NJson& json, std::vector<AnimKeyframe>& out) {
    if (! json.is_array()) return false;
    out.reserve(json.size());
    for (const auto& jK : json) {
        AnimKeyframe k;
        if (ParseAnimKeyframe(jK, k)) out.push_back(std::move(k));
    }
    return true;
}

bool ParseAnimEvent(const owe::NJson& json, AnimEvent& out) {
    if (! json.is_object()) return false;
    owe::GetJsonValue(json, "frame", out.frame, false);
    owe::GetJsonValue(json, "name", out.name, false);
    return ! out.name.empty();
}

bool ParseAnimOptions(const owe::NJson& json, AnimOptions& out) {
    if (! json.is_object()) return false;
    owe::GetJsonValue(json, "fps", out.fps, false);
    owe::GetJsonValue(json, "length", out.length, false);
    owe::GetJsonValue(json, "mode", out.mode, false);
    owe::GetJsonValue(json, "name", out.name, false);
    owe::GetJsonValue(json, "startpaused", out.startpaused, false);
    owe::GetJsonValue(json, "wraploop", out.wraploop, false);
    if (auto value = owe::Find(json, "smoothing"); value != nullptr) out.smoothing = *value;
    if (auto value = owe::Find(json, "parent"); value != nullptr) {
        std::string key;
        if (owe::GetJsonValue(*value, "key", key, false) && ! key.empty())
            out.parent = Some(String::make(rstd::cppstd::as_str(key).unwrap()));
    }
    if (auto value = owe::Find(json, "children"); value != nullptr) {
        if (value->is_array()) {
            for (const auto& entry : *value) {
                std::string key;
                if (owe::GetJsonValue(entry, "key", key, false) && ! key.empty())
                    out.children.push(String::make(rstd::cppstd::as_str(key).unwrap()));
            }
        }
    }
    if (auto value = owe::Find(json, "events"); value != nullptr) {
        if (value->is_array()) {
            for (const auto& entry : *value) {
                AnimEvent event;
                if (ParseAnimEvent(entry, event)) out.events.push_back(std::move(event));
            }
        }
    }
    return true;
}

auto AnimOptions::clone() const -> AnimOptions {
    AnimOptions result;
    result.fps         = fps;
    result.length      = length;
    result.mode        = mode;
    result.name        = name;
    result.startpaused = startpaused;
    result.wraploop    = wraploop;
    result.smoothing   = smoothing;
    result.parent      = parent.is_some() ? Some(parent->clone()) : None();
    for (const auto& child : children) result.children.push(child.clone());
    result.events = events;
    return result;
}

bool ParseAnimCurve(const owe::NJson& json, AnimCurve& out) {
    if (! json.is_object()) return false;
    if (auto value = owe::Find(json, "c0"); value != nullptr) ParseAnimAxis(*value, out.c0);
    if (auto value = owe::Find(json, "c1"); value != nullptr) ParseAnimAxis(*value, out.c1);
    if (auto value = owe::Find(json, "c2"); value != nullptr) ParseAnimAxis(*value, out.c2);
    if (auto value = owe::Find(json, "options"); value != nullptr)
        ParseAnimOptions(*value, out.options);
    owe::GetJsonValue(json, "relative", out.relative, false);
    return true;
}

auto AnimCurve::clone() const -> AnimCurve {
    AnimCurve result;
    result.c0       = c0;
    result.c1       = c1;
    result.c2       = c2;
    result.options  = options.clone();
    result.relative = relative;
    return result;
}

auto ScriptBinding::clone() const -> ScriptBinding {
    return ScriptBinding {
        .source        = source,
        .initial_value = initial_value,
    };
}

auto FieldBindingSpec::clone() const -> FieldBindingSpec {
    return FieldBindingSpec {
        .identity  = identity,
        .field     = field.clone(),
        .animation = animation.is_some() ? Some(animation->clone()) : None(),
        .script_properties =
            script_properties.is_some() ? Some(owe::NJson(*script_properties)) : None(),
        .script = script.is_some() ? Some(script->clone()) : None(),
        .user   = user.is_some() ? Some(user->clone()) : None(),
    };
}

auto FieldBindingSpec::ScriptProperties() const noexcept -> const owe::NJson& {
    static const owe::NJson empty;
    return script_properties.is_some() ? *script_properties : empty;
}

auto FieldBindings::Get(ref<str> field) const noexcept -> Option<ref<FieldBindingSpec>> {
    for (const auto& binding : entries) {
        if (binding.field == field)
            return Some(ref<FieldBindingSpec>::from_raw_parts(rstd::addressof(binding)));
    }
    return None();
}

auto FieldBindings::GetMut(ref<str> field) noexcept -> Option<mut_ref<FieldBindingSpec>> {
    for (auto& binding : entries) {
        if (binding.field == field)
            return Some(mut_ref<FieldBindingSpec>::from_raw_parts(rstd::addressof(binding)));
    }
    return None();
}

auto FieldBindings::Ensure(ref<str> field) -> mut_ref<FieldBindingSpec> {
    auto binding = GetMut(field);
    if (binding.is_some()) return *binding;
    entries.push(FieldBindingSpec {
        .identity = next_field_binding_identity.fetch_add(u64(1), Ordering::Relaxed),
        .field    = String::make(field),
    });
    return mut_ref<FieldBindingSpec>::from_raw_parts(
        rstd::addressof(entries[entries.len() - usize(1)]));
}

bool FieldBindings::HasAnimation(ref<str> field) const noexcept {
    auto binding = Get(field);
    return binding.is_some() && (**binding).animation.is_some();
}

bool FieldBindings::HasScript(ref<str> field) const noexcept {
    auto binding = Get(field);
    return binding.is_some() && (**binding).script.is_some();
}

auto FieldBindings::clone() const -> FieldBindings {
    FieldBindings result;
    result.entries.reserve(entries.len());
    for (const auto& binding : entries) result.entries.push(binding.clone());
    return result;
}

void FieldBindings::Update(const FieldBindings& other) {
    for (const auto& binding : other.entries) *Ensure(binding.field.as_str()) = binding.clone();
}

std::size_t AbsorbFieldBinding(std::string_view field, const owe::NJson& field_value,
                               FieldBindings& out) {
    if (! field_value.is_object()) return 0;
    std::size_t count = 0;
    if (auto animation = owe::Find(field_value, "animation"); animation != nullptr) {
        AnimCurve curve;
        if (ParseAnimCurve(*animation, curve)) {
            out.Ensure(rstd::cppstd::as_str(field).unwrap())->animation = Some(rstd::move(curve));
            ++count;
        }
    }
    if (auto properties = owe::Find(field_value, "scriptproperties"); properties != nullptr) {
        out.Ensure(rstd::cppstd::as_str(field).unwrap())->script_properties =
            Some(owe::NJson(*properties));
        ++count;
    }
    if (auto user = owe::Find(field_value, "user"); user != nullptr) {
        if (user->is_string()) {
            out.Ensure(rstd::cppstd::as_str(field).unwrap())->user = Some(
                String::make(rstd::cppstd::as_str(user->get_ref<const std::string&>()).unwrap()));
            ++count;
        }
    }
    auto script = owe::Find(field_value, "script");
    if (script != nullptr && script->is_string()) {
        ScriptBinding binding;
        binding.source = script->get_ref<const std::string&>();
        if (auto value = owe::Find(field_value, "value"); value != nullptr)
            binding.initial_value = *value;
        out.Ensure(rstd::cppstd::as_str(field).unwrap())->script = Some(rstd::move(binding));
        ++count;
    }
    return count;
}

std::size_t AbsorbAllFieldBindings(const owe::NJson& obj_json, FieldBindings& out) {
    if (! obj_json.is_object()) return 0;
    std::size_t n = 0;
    for (const auto& [field, field_value] : obj_json.items())
        n += AbsorbFieldBinding(field, field_value, out);
    return n;
}

} // namespace owe::wpscene
