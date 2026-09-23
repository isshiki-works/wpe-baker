export module owe.user_property;

import rstd.cppstd;
import wescene.json;

export namespace owe
{

NJson MakeUserPropertyWirePatch(std::string_view value);
NJson MergeUserPropertyDescriptor(const NJson& schema, const NJson& patch);

} // namespace owe
