module;
#include <owe/compat.hpp>
#include <owe/std.hpp>
export module owe.user_property;

import wescene.json;

export namespace owe
{

NJson MakeUserPropertyWirePatch(std::string_view value);
NJson MergeUserPropertyDescriptor(const NJson& schema, const NJson& patch);

} // namespace owe
