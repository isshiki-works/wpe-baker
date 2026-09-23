module;
#include <owe/compat.hpp>
#include <owe/std.hpp>
export module wescene.vulkan_render:uniform_buffer;
import wescene.resource;
import wescene.scene;
import wescene.types;

using namespace rstd::prelude;

export namespace owe::vulkan
{

struct UniformBufferUpdateError {
    String message;
};

struct UniformSlot {
    String              name;
    usize               offset { 0 };
    usize               size { 0 };
    usize               count { 1 };
    ShaderScalarKind    scalar_kind { ShaderScalarKind::Unknown };
    u32                 scalar_width {};
    u32                 vector_components { u32(1) };
    u32                 matrix_rows {};
    u32                 matrix_columns {};
    u32                 matrix_stride {};
    ShaderMatrixMajor   matrix_major { ShaderMatrixMajor::None };
    u32                 array_stride {};
    rstd::vec::Vec<u32> array_dimensions;

    usize LogicalFloatElements() const {
        if (scalar_kind == ShaderScalarKind::Unknown) return size / usize(sizeof(float));
        if (matrix_rows != u32() && matrix_columns != u32()) {
            return usize(matrix_rows.to_primitive()) * usize(matrix_columns.to_primitive()) * count;
        }
        return usize(vector_components.to_primitive()) * count;
    }
};

struct UniformBufferLayout {
    usize                       size { 0 };
    rstd::vec::Vec<UniformSlot> slots;
};

auto CompileUniformBufferLayout(const resource::ShaderArtifactUniformBlock&)
    -> Result<UniformBufferLayout, UniformBufferUpdateError>;

auto SerializeUniformValue(mut_ref<u8[]> destination, const UniformSlot&, UniformValueView,
                           ShaderMatrixConvention,
                           ShaderMatrixAbi matrix_abi = ShaderMatrixAbi::NativeSpirv)
    -> Result<empty, UniformBufferUpdateError>;

// uniform 缓冲更新时可见的帧、视口与纹理动画帧。
struct UniformBufferFrameContext {
    virtual ~UniformBufferFrameContext() = default;

    virtual auto Frame() const -> ref<SceneFrame>          = 0;
    virtual auto Viewport() const -> rstd::array<float, 2> = 0;
    virtual auto TextureFrame(SceneDrawItemId draw, usize texture_index) const
        -> Option<SceneTextureFrameView> = 0;
};

class ProgramUniformFrameContext final : public UniformBufferFrameContext {
public:
    ProgramUniformFrameContext(const SceneFrame& frame, rstd::array<float, 2> viewport,
                               const SceneTextureAnimationView* textures)
        : m_frame(ref<SceneFrame>::from_raw_parts(rstd::addressof(frame))),
          m_viewport(viewport),
          m_textures(textures) {}

    auto Frame() const -> ref<SceneFrame> override { return m_frame; }
    auto Viewport() const -> rstd::array<float, 2> override { return m_viewport; }
    auto TextureFrame(SceneDrawItemId draw, usize texture_index) const
        -> Option<SceneTextureFrameView> override;

private:
    ref<SceneFrame>                             m_frame;
    rstd::array<float, 2>                       m_viewport;
    const SceneTextureAnimationView* m_textures;
};

// 一个 uniform 缓冲的逐帧更新器。
struct UniformBufferUpdate {
    virtual ~UniformBufferUpdate() = default;

    virtual auto Update(const UniformBufferFrameContext*    context,
                        resource::BufferContentWriter* buffers) const
        -> Result<empty, UniformBufferUpdateError>          = 0;
    virtual auto Buffer() const -> resource::BufferUseHandle = 0;
};

struct BoundUniformOutput {
    UniformOutputId output;
    usize           slot_index { 0 };
};

struct BoundUniformSource {
    const UniformSource*                  source;
    i32                                   priority {};
    Vec<BoundUniformOutput>               outputs;
    Option<std::unique_ptr<UniformBindingLease>> lease;
    u64                                   version { 0 };
    bool                                  evaluated { false };
};

struct PreparedUniformTextureMetadata {
    bool                  available { false };
    rstd::array<float, 2> source_extent { 0.0f, 0.0f };
    rstd::array<float, 2> sample_extent { 0.0f, 0.0f };
    bool                  has_mipmap { false };
    float                 mipmap_level { 0.0f };
    u64                   revision { 1 };
};

struct UniformPrepareDraw {
    SceneDrawItemId    draw_item;
    SceneNodeId        node_id;
    ref<SceneNode>     node;
    ref<SceneMaterial> material;
};

// 建立 uniform 绑定时查询场景绘制项、源与块定义。
struct UniformBindingPrepareContext {
    virtual ~UniformBindingPrepareContext() = default;

    virtual auto ResolveDraw(SceneDrawItemId draw) const -> Option<UniformPrepareDraw>         = 0;
    virtual auto DrawItemFor(ref<SceneNode> node, u32 submesh_index) const
        -> Option<SceneDrawItemId>                                                            = 0;
    virtual auto GlobalSources() const -> slice<UniformSourceAttachment>                     = 0;
    virtual auto NodeSources(SceneNodeId node) const -> slice<UniformSourceAttachment>       = 0;
    virtual auto ResolveSource(UniformSourceId source) const -> Option<const UniformSource*> = 0;
    virtual auto ResolveBlock(u64 identity) const -> Option<ref<UniformBlockDefinition>>     = 0;
};

class SceneUniformBindingPrepareContext final : public UniformBindingPrepareContext {
public:
    explicit SceneUniformBindingPrepareContext(Scene& scene)
        : m_scene(ref<Scene>::from_raw_parts(rstd::addressof(scene))) {}

    auto ResolveDraw(SceneDrawItemId) const -> Option<UniformPrepareDraw> override;
    auto DrawItemFor(ref<SceneNode>, u32 submesh_index) const -> Option<SceneDrawItemId> override;
    auto GlobalSources() const -> slice<UniformSourceAttachment> override;
    auto NodeSources(SceneNodeId) const -> slice<UniformSourceAttachment> override;
    auto ResolveSource(UniformSourceId) const -> Option<const UniformSource*> override;
    auto ResolveBlock(u64 identity) const -> Option<ref<UniformBlockDefinition>> override;

private:
    mutable ref<Scene> m_scene;
};

class UniformBufferBinding final : public UniformBufferUpdate {
public:
    UniformBufferBinding(SceneDrawItemId, resource::BufferUseHandle, UniformBufferLayout,
                         Vec<BoundUniformSource>, ShaderValues, ref<SceneMaterial>,
                         Vec<PreparedUniformTextureMetadata>, SceneRenderViewKind,
                         ShaderMatrixConvention, ShaderMatrixAbi);

    auto Update(const UniformBufferFrameContext*,
                resource::BufferContentWriter*) const
        -> Result<empty, UniformBufferUpdateError> override;
    auto Buffer() const -> resource::BufferUseHandle override { return m_buffer; }

    auto WriteSlot(usize slot_index, UniformValueView value) const
        -> Result<bool, UniformBufferUpdateError>;
    auto WriteName(std::string_view, const UniformValue&) const
        -> Result<bool, UniformBufferUpdateError>;

private:
    SceneDrawItemId                     m_draw_item;
    resource::BufferUseHandle           m_buffer;
    UniformBufferLayout                 m_layout;
    mutable Vec<BoundUniformSource>     m_sources;
    mutable rstd::vec::Vec<u8>          m_data;
    mutable rstd::vec::Vec<u8>          m_base_data;
    ShaderValues                        m_defaults;
    ref<SceneMaterial>                  m_material;
    Vec<PreparedUniformTextureMetadata> m_textures;
    SceneRenderViewKind                 m_render_view { SceneRenderViewKind::Primary };
    ShaderMatrixConvention m_matrix_convention { ShaderMatrixConvention::ColumnVector };
    ShaderMatrixAbi        m_matrix_abi { ShaderMatrixAbi::NativeSpirv };
    mutable u64            m_material_version { 0 };
    mutable bool           m_uploaded { false };
};

class SharedUniformBufferBinding final : public UniformBufferUpdate {
public:
    SharedUniformBufferBinding(resource::BufferUseHandle, UniformBufferLayout,
                               Vec<BoundUniformSource>, ShaderMatrixConvention, ShaderMatrixAbi);

    auto Update(const UniformBufferFrameContext*,
                resource::BufferContentWriter*) const
        -> Result<empty, UniformBufferUpdateError> override;
    auto Buffer() const -> resource::BufferUseHandle override { return m_buffer; }
    auto WriteSlot(usize slot_index, UniformValueView value) const
        -> Result<bool, UniformBufferUpdateError>;

private:
    resource::BufferUseHandle       m_buffer;
    UniformBufferLayout             m_layout;
    mutable Vec<BoundUniformSource> m_sources;
    mutable rstd::vec::Vec<u8>      m_data;
    ShaderMatrixConvention          m_matrix_convention { ShaderMatrixConvention::ColumnVector };
    ShaderMatrixAbi                 m_matrix_abi { ShaderMatrixAbi::NativeSpirv };
    mutable bool                    m_uploaded { false };
};

auto MakeUniformBufferBinding(
    const UniformBindingPrepareContext*, SceneDrawItemId, resource::BufferUseHandle,
    const resource::ShaderArtifactUniformBlock&, Vec<PreparedUniformTextureMetadata> textures = {},
    SceneRenderViewKind        render_view       = SceneRenderViewKind::Primary,
    ShaderMatrixConvention     matrix_convention = ShaderMatrixConvention::ColumnVector,
    ShaderMatrixAbi            matrix_abi        = ShaderMatrixAbi::NativeSpirv,
    Option<ref<SceneMaterial>> material_override = None<ref<SceneMaterial>>())
    -> Result<std::unique_ptr<UniformBufferUpdate>, UniformBufferUpdateError>;

auto MakeSharedUniformBufferBinding(const UniformBindingPrepareContext*,
                                    resource::BufferUseHandle,
                                    const resource::ShaderArtifactUniformBlock&,
                                    ShaderMatrixConvention, ShaderMatrixAbi)
    -> Result<std::unique_ptr<UniformBufferUpdate>, UniformBufferUpdateError>;

} // namespace owe::vulkan

namespace owe::compat
{

template<>
struct Impl<fmt::Display, owe::vulkan::UniformBufferUpdateError>
    : ImplBase<owe::vulkan::UniformBufferUpdateError> {
    auto fmt(fmt::Formatter& formatter) const -> bool {
        return formatter.write_fmt(fmt::Arguments::make("{}", this->self().message));
    }
};

} // namespace rstd

