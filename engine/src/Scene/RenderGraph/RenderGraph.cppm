module;
#include <owe/compat.hpp>
#include <owe/std.hpp>
export module wescene.rgraph:render_graph;
import cppstd;
import wescene.resource;

import :dependency_graph;
import :pass;
import :pass_node;
import :tex_node;

using namespace rstd::prelude;

export namespace owe::rg
{

class RenderGraph;

struct TextureNodeRef {
    NodeHandle handle;

    bool valid() const noexcept { return handle.valid(); }
};

enum class TextureKind
{
    Imported,
    Temp,
};

struct TextureDesc {
    String                                 name;
    String                                 key;
    TextureKind                            kind { TextureKind::Imported };
    rstd::Option<resource::TextureRequest> request;
    rstd::Option<String>                   allocation_family;
};

struct TextureNodeState {
    TextureNodeRef             ref;
    resource::TextureUseHandle use;
    TextureDesc                desc;
    usize                      version { 0 };
};

struct WrittenTextureState {
    TextureNodeState texture;
    NodeHandle       writer;
};

struct PassNodeState {
    NodeHandle     handle;
    PassHandle     pass;
    String         name;
    PassNode::Type type { PassNode::Type::CustomShader };
};

enum class RenderGraphOrderError
{
    Cycle,
};

struct RenderGraphBuilder {
    auto createTexture(const TextureDesc&, bool write = false) -> TextureNodeRef;
    void read(TextureNodeRef);
    void write(TextureNodeRef);
    auto textureState(TextureNodeRef) const -> rstd::Option<TextureNodeState>;
    auto workPassNode() const -> const PassNode&;
    void markSelfWrite(TextureNodeRef);
    void markVirtualWrite(TextureNodeRef);
    void reusePreviousAllocation(TextureNodeRef);

private:
    friend class RenderGraph;

    RenderGraphBuilder(RenderGraph&, NodeHandle);

    auto createTextureNode(const TextureDesc&, bool write) -> TextureNodeRef;
    void readTextureNode(TextureNodeRef);
    void writeTextureNode(TextureNodeRef);

    RenderGraph& m_rg;
    NodeHandle   m_passnode_wip;
};

class RenderGraph {
public:
    RenderGraph();

    auto getPass(PassHandle) -> rstd::Option<Pass&>;
    auto getPass(PassHandle) const -> rstd::Option<const Pass&>;
    auto passState(NodeHandle) const -> rstd::Option<PassNodeState>;
    auto textureState(TextureNodeRef) const -> rstd::Option<TextureNodeState>;
    // Versions include any graph-created virtual initial value. Only real
    // writers are eligible; an omitted version selects the latest real write.
    auto writtenTexture(ref<str> key, Option<usize> version = None()) const
        -> Option<WrittenTextureState>;
    auto writtenTexture(NodeHandle writer) const -> Option<WrittenTextureState>;
    auto readTexture(NodeHandle pass_node, TextureNodeRef texture) -> bool;

    auto topologicalOrder() const
        -> rstd::Result<rstd::vec::Vec<NodeHandle>, RenderGraphOrderError>;
    auto getLastReadTextures(rstd::slice<NodeHandle>) const
        -> rstd::vec::Vec<rstd::vec::Vec<TextureNodeState>>;
    auto resourcePlan() const -> resource::ResourcePlan;

    void ToGraphviz(rstd::ref<rstd::str> path) const;

    template<typename TPass, typename CB>
    auto addPass(rstd::ref<rstd::str> name, PassNode::Type type, CB&& callback) -> NodeHandle {
        using Desc = typename TPass::Desc;

        auto node_handle = m_dg.AddNode();
        auto pass_handle = PassHandle { .index = m_next_pass_index++ };
        (void)m_pass_nodes.insert(node_handle,
                                  PassNode {
                                      .handle = node_handle,
                                      .pass   = pass_handle,
                                      .type   = type,
                                      .name   = String::make(name),
                                  });

        RenderGraphBuilder builder(*this, node_handle);
        Desc               desc {};
        callback(builder, desc);

        std::unique_ptr<Pass> pass = std::make_unique<TPass>(std::move(desc));
        (void)m_passes.insert(pass_handle, std::move(pass));
        return node_handle;
    }

private:
    friend struct RenderGraphBuilder;

    using PassNodeMap   = rstd::collections::HashMap<NodeHandle, PassNode>;
    using TexNodeMap    = rstd::collections::HashMap<NodeHandle, TexNode>;
    using PassMap       = rstd::collections::HashMap<PassHandle, std::unique_ptr<Pass>>;
    using TextureKeyMap = rstd::collections::HashMap<String, NodeHandle>;

    auto getPassNode(NodeHandle) -> rstd::Option<PassNode&>;
    auto getPassNode(NodeHandle) const -> rstd::Option<const PassNode&>;
    auto getTexNode(NodeHandle) -> rstd::Option<TexNode&>;
    auto getTexNode(NodeHandle) const -> rstd::Option<const TexNode&>;
    auto createTextureNode(const TextureDesc&, bool write) -> TextureNodeRef;
    auto createNewTextureNode(const TextureDesc&) -> TextureNodeRef;
    void connectTextureRead(TextureNodeRef, NodeHandle pass_node);
    void connectTextureWrite(TextureNodeRef, NodeHandle pass_node);
    auto textureHasWriter(TextureNodeRef) const -> bool;
    void reusePreviousAllocation(TextureNodeRef);
    auto isPassNode(NodeHandle) const -> bool;
    auto isVirtualPassNode(NodeHandle) const -> bool;
    auto isRenderPassNode(NodeHandle) const -> bool;
    auto passWriteTarget(NodeHandle) const -> rstd::Option<String>;

    usize           m_next_pass_index { 0 };
    u64             m_resource_generation { 0 };
    DependencyGraph m_dg;
    PassNodeMap     m_pass_nodes;
    TexNodeMap      m_tex_nodes;
    PassMap         m_passes;
    TextureKeyMap   m_key_texnode;
};

} // namespace owe::rg
