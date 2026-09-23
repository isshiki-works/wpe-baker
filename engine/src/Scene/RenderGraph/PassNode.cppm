module;
#include <owe/compat.hpp>
#include <owe/std.hpp>
export module wescene.rgraph:pass_node;

import :dependency_graph;
import :pass;

using namespace rstd::prelude;
using namespace rstd::literals;

export namespace owe::rg
{

struct PassNode {
    enum class Type
    {
        CustomShader,
        Copy,
        Virtual
    };

    NodeHandle handle;
    PassHandle pass;
    Type       type { Type::CustomShader };
    String     name { String::make("unknown pass"_str) };

    auto Handle() const noexcept -> NodeHandle { return handle; }
    auto ToGraphviz() const -> String;
};

} // namespace owe::rg
