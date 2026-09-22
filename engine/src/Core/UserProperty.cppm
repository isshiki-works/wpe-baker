export module owe.user_property;

import rstd.cppstd;
import wescene.json;

export namespace owe
{

Json MakeUserPropertyWirePatch(std::string_view value);
Json MergeUserPropertyDescriptor(const Json& schema, const Json& patch);

} // namespace owe
