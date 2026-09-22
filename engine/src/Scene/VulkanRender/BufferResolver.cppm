module;

export module wescene.vulkan_render:buffer_resolver;
import wescene.core;
import rstd;
import rstd.cppstd;
import wescene.resource_registry;
import wescene.vulkan;
import wescene.scene;

using namespace rstd::prelude;

export namespace owe::vulkan
{

enum class DrawBufferRole
{
    Uniform,
    Vertex,
    Index,
};

struct DrawBufferKey {
    RenderItemId   render_item;
    DrawBufferRole role { DrawBufferRole::Vertex };
    u32            submesh_index { 0 };
    u32            stream_index { 0 };
    u64            data_generation { 0 };
    u64            allocation_generation { 0 };
};

struct DrawBufferRefs {
    RenderItemId render_item;
    u64          allocation_generation { 0 };
    bool         dynamic { false };
    u32          draw_count { 0 };

    std::vector<DrawBufferKey> vertex_keys;
    Option<DrawBufferKey>      index_key;

    Vec<resource::BufferUseHandle>    vertices;
    Option<resource::BufferUseHandle> index;

    DrawBufferRefs()                                     = default;
    DrawBufferRefs(const DrawBufferRefs&)                = delete;
    DrawBufferRefs& operator=(const DrawBufferRefs&)     = delete;
    DrawBufferRefs(DrawBufferRefs&&) noexcept            = default;
    DrawBufferRefs& operator=(DrawBufferRefs&&) noexcept = default;

    bool hasIndex() const { return index.is_some(); }
};

struct DrawBufferRequest {
    RenderItemId                           render_item;
    SceneMesh*                             mesh { nullptr };
    u32                                    submesh_index { 0 };
    u64                                    dynamic_allocation_generation { 0 };
    rstd::slice<resource::BufferUseHandle> buffer_uses;
};

String BuildDrawBufferResourceName(SceneDrawItemId, DrawBufferRole, u32 stream_index = u32());
std::vector<DrawBufferKey> BuildDrawBufferKeys(const DrawBufferRequest&,
                                               u64 allocation_generation = u64());

class RenderBufferResolver {
public:
    explicit RenderBufferResolver(const resource_registry::PreparedResourceTable&);

    Option<DrawBufferRefs> prepareDrawBuffers(const DrawBufferRequest&);
    static bool updateDynamicDrawBuffers(const DrawBufferRequest&, DrawBufferRefs&,
                                         rstd::mut_ref<rstd::dyn<resource::BufferContentWriter>>);

private:
    rstd::ref<resource_registry::PreparedResourceTable> m_resources;
};

} // namespace owe::vulkan
