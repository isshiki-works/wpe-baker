module;

#include "JsonNlohmann.hpp"

module wescene.pkg.parse;
import rstd;
import rstd.cppstd;
import wescene.json;

using namespace rstd::prelude;
using namespace rstd::literals;
using rstd::collections::HashMap;

namespace owe::wpscene
{

namespace
{

bool ReadString(const NJson& json, std::string_view key, String& output) {
    auto value = Find(json, key);
    if (value == nullptr || ! value->is_string()) return false;
    output = String::make(rstd::cppstd::as_str(value->get_ref<const std::string&>()).unwrap());
    return true;
}

void ReadMap(const NJson& json, std::string_view key, HashMap<String, i32>& output) {
    auto values = Find(json, key);
    if (values == nullptr || ! values->is_object()) return;
    for (const auto& [entry_key, entry_value] : values->items()) {
        int value {};
        owe::GetJsonValue(entry_value, value);
        (void)output.insert(String::make(rstd::cppstd::as_str(entry_key).unwrap()), i32(value));
    }
}

} // namespace

bool UniformTex::FromJson(const NJson& json) {
    (void)ReadString(json, "material", material);
    (void)ReadString(json, "label", label);
    (void)ReadString(json, "default", default_);
    (void)ReadString(json, "mode", mode);
    (void)ReadString(json, "combo", combo);
    if (auto values = Find(json, "components"); values != nullptr) {
        if (values->is_array()) {
            for (const auto& element : *values) {
                Component component;
                (void)ReadString(element, "label", component.label);
                (void)ReadString(element, "combo", component.combo);
                components.push(rstd::move(component));
            }
        }
    }
    owe::GetJsonValue(json, "requireany", requireany, false);
    ReadMap(json, "require", require);

    owe::GetJsonValue(json, "hidden", hidden, false);
    owe::GetJsonValue(json, "nonremovable", nonremovable, false);
    (void)ReadString(json, "group", group);
    owe::GetJsonValue(json, "linked", linked, false);
    (void)ReadString(json, "format", format);
    owe::GetJsonValue(json, "formatcombo", formatcombo, false);
    owe::GetJsonValue(json, "direction", direction, false);
    (void)ReadString(json, "conversion", conversion);
    int order_value {};
    owe::GetJsonValue(json, "order", order_value, false);
    order = i32(order_value);
    return true;
}

bool UniformVar::FromJson(const NJson& json, String uniform_name) {
    name    = rstd::move(uniform_name);
    is_user = name->starts_with("u_"_str);
    (void)ReadString(json, "material", material);
    (void)ReadString(json, "label", label);
    (void)ReadString(json, "group", group);
    (void)ReadString(json, "type", type);
    owe::GetJsonValue(json, "position", position, false);
    owe::GetJsonValue(json, "linked", linked, false);
    owe::GetJsonValue(json, "nobindings", nobindings, false);
    if (auto values = Find(json, "range"); values != nullptr) {
        if (values->is_array() && values->size() >= 2) {
            has_range = owe::GetJsonValue((*values)[0], range[usize()]) &&
                        owe::GetJsonValue((*values)[1], range[usize(1)]);
        }
    }
    return true;
}

bool Combo::FromJson(const NJson& json) {
    (void)ReadString(json, "material", material);
    (void)ReadString(json, "combo", combo);
    (void)ReadString(json, "type", type);
    int default_value {};
    owe::GetJsonValue(json, "default", default_value, false);
    default_ = i32(default_value);
    ReadMap(json, "options", options);
    ReadMap(json, "require", require);
    return true;
}

} // namespace owe::wpscene
