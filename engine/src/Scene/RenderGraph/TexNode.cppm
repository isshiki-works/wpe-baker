export module wescene.rgraph:tex_node;
import rstd;
import wescene.resource;
import :dependency_graph;

using namespace rstd::prelude;
using namespace rstd::literals;

export namespace owe::rg
{

struct TexNode {
    enum class TexType
    {
        Imported,
        Temp
    };

    NodeHandle                             handle;
    TexType                                type { TexType::Imported };
    String                                 key;
    String                                 name { String::make("unknown tex"_str) };
    rstd::Option<resource::TextureRequest> request;
    rstd::Option<String>                   allocation_family;
    rstd::Option<NodeHandle>               allocation_parent;
    usize                                  version { 0 };
    rstd::Option<NodeHandle>               previous;
    rstd::Option<NodeHandle>               next;
    rstd::Option<NodeHandle>               writer;
    rstd::vec::Vec<NodeHandle>             readers;

    auto Handle() const noexcept -> NodeHandle { return handle; }
    auto ToGraphviz() const -> String;
    auto ToGraphvizWithAllocation(rstd::Option<rstd::ref<rstd::str>> allocation_key) const
        -> String;
};

} // namespace owe::rg
