module;
#include <owe/compat.hpp>
#include <owe/std.hpp>

#include "JsonNlohmann.hpp"

module owe.user_property;


using namespace rstd::prelude;

namespace owe
{

namespace
{

NJson MakeDescriptor(NJson value) {
    NJson object   = NJson::object();
    object["value"] = std::move(value);
    return object;
}

std::string DescriptorType(const NJson& descriptor) {
    const auto* type = Find(descriptor, "type");
    return type != nullptr && type->is_string() ? type->get<std::string>() : std::string();
}

NJson ParseWireValue(const NJson& schema, const NJson& value) {
    if (! value.is_string()) return value;
    const auto type = DescriptorType(schema);
    if (type.empty() || type == "textinput") return value;

    auto parsed = ParseNJson(value.get_ref<const std::string&>(), { .allow_comments = true });
    return parsed.is_ok() ? parsed.unwrap() : value;
}

} // namespace

NJson MakeUserPropertyWirePatch(std::string_view value) {
    return MakeDescriptor(NJson(std::string(value)));
}

NJson MergeUserPropertyDescriptor(const NJson& schema, const NJson& patch) {
    const NJson* value = &patch;
    if (const auto* member = Find(patch, "value"); member != nullptr) value = member;
    const bool typed_patch = Find(patch, "type") != nullptr;

    NJson descriptor   = schema.is_object() ? schema : MakeDescriptor(*value);
    NJson merged_value = typed_patch ? *value : ParseWireValue(schema, *value);
    descriptor["value"] = std::move(merged_value);
    return descriptor;
}

} // namespace owe
