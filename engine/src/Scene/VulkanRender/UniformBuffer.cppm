export module wescene.vulkan_render:uniform_buffer;
import rstd;
import rstd.cppstd;
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

struct UniformBufferFrameContext {
    using Trait                  = UniformBufferFrameContext;
    static constexpr bool direct = false;

    template<typename Self, typename = void>
    struct Api {
        using Trait = UniformBufferFrameContext;

        auto Frame() const -> ref<SceneFrame> { return rstd::trait_call<0>(this); }
        auto Viewport() const -> rstd::array<float, 2> { return rstd::trait_call<1>(this); }
        auto TextureFrame(SceneDrawItemId draw, usize texture_index) const
            -> Option<SceneTextureFrameView> {
            return rstd::trait_call<2>(this, draw, texture_index);
        }
    };

    template<typename T>
    using Funcs = TraitFuncs<&T::Frame, &T::Viewport, &T::TextureFrame>;
};

class ProgramUniformFrameContext {
public:
    ProgramUniformFrameContext(const SceneFrame& frame, rstd::array<float, 2> viewport,
                               ref<dyn<SceneTextureAnimationView>> textures)
        : m_frame(ref<SceneFrame>::from_raw_parts(rstd::addressof(frame))),
          m_viewport(viewport),
          m_textures(textures) {}

    auto Frame() const -> ref<SceneFrame> { return m_frame; }
    auto Viewport() const -> rstd::array<float, 2> { return m_viewport; }
    auto TextureFrame(SceneDrawItemId draw, usize texture_index) const
        -> Option<SceneTextureFrameView>;

private:
    ref<SceneFrame>                             m_frame;
    rstd::array<float, 2>                       m_viewport;
    mutable ref<dyn<SceneTextureAnimationView>> m_textures;
};

struct UniformBufferUpdate {
    using Trait                  = UniformBufferUpdate;
    static constexpr bool direct = false;

    template<typename Self, typename = void>
    struct Api {
        using Trait = UniformBufferUpdate;

        auto Update(ref<dyn<UniformBufferFrameContext>>         context,
                    mut_ref<dyn<resource::BufferContentWriter>> buffers) const
            -> Result<empty, UniformBufferUpdateError> {
            return rstd::trait_call<0>(this, context, buffers);
        }
        auto Buffer() const -> resource::BufferUseHandle { return rstd::trait_call<1>(this); }
    };

    template<typename T>
    using Funcs = TraitFuncs<&T::Update, &T::Buffer>;
};

struct BoundUniformOutput {
    UniformOutputId output;
    usize           slot_index { 0 };
};

struct BoundUniformSource {
    ref<dyn<UniformSource>>               source;
    i32                                   priority {};
    Vec<BoundUniformOutput>               outputs;
    Option<Box<dyn<UniformBindingLease>>> lease;
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

struct UniformBindingPrepareContext {
    using Trait                  = UniformBindingPrepareContext;
    static constexpr bool direct = false;

    template<typename Self, typename = void>
    struct Api {
        using Trait = UniformBindingPrepareContext;

        auto ResolveDraw(SceneDrawItemId draw) const -> Option<UniformPrepareDraw> {
            return rstd::trait_call<0>(this, draw);
        }
        auto DrawItemFor(ref<SceneNode> node, u32 submesh_index) const -> Option<SceneDrawItemId> {
            return rstd::trait_call<1>(this, node, submesh_index);
        }
        auto GlobalSources() const -> slice<UniformSourceAttachment> {
            return rstd::trait_call<2>(this);
        }
        auto NodeSources(SceneNodeId node) const -> slice<UniformSourceAttachment> {
            return rstd::trait_call<3>(this, node);
        }
        auto ResolveSource(UniformSourceId source) const -> Option<ref<dyn<UniformSource>>> {
            return rstd::trait_call<4>(this, source);
        }
        auto ResolveBlock(u64 identity) const -> Option<ref<UniformBlockDefinition>> {
            return rstd::trait_call<5>(this, identity);
        }
    };

    template<typename T>
    using Funcs = TraitFuncs<&T::ResolveDraw, &T::DrawItemFor, &T::GlobalSources, &T::NodeSources,
                             &T::ResolveSource, &T::ResolveBlock>;
};

class SceneUniformBindingPrepareContext {
public:
    explicit SceneUniformBindingPrepareContext(Scene& scene)
        : m_scene(ref<Scene>::from_raw_parts(rstd::addressof(scene))) {}

    auto ResolveDraw(SceneDrawItemId) const -> Option<UniformPrepareDraw>;
    auto DrawItemFor(ref<SceneNode>, u32 submesh_index) const -> Option<SceneDrawItemId>;
    auto GlobalSources() const -> slice<UniformSourceAttachment>;
    auto NodeSources(SceneNodeId) const -> slice<UniformSourceAttachment>;
    auto ResolveSource(UniformSourceId) const -> Option<ref<dyn<UniformSource>>>;
    auto ResolveBlock(u64 identity) const -> Option<ref<UniformBlockDefinition>>;

private:
    mutable ref<Scene> m_scene;
};

class UniformBufferBinding {
public:
    UniformBufferBinding(SceneDrawItemId, resource::BufferUseHandle, UniformBufferLayout,
                         Vec<BoundUniformSource>, ShaderValues, ref<SceneMaterial>,
                         Vec<PreparedUniformTextureMetadata>, SceneRenderViewKind,
                         ShaderMatrixConvention, ShaderMatrixAbi);

    auto Update(ref<dyn<UniformBufferFrameContext>>,
                mut_ref<dyn<resource::BufferContentWriter>>) const
        -> Result<empty, UniformBufferUpdateError>;
    auto Buffer() const -> resource::BufferUseHandle { return m_buffer; }

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

class SharedUniformBufferBinding {
public:
    SharedUniformBufferBinding(resource::BufferUseHandle, UniformBufferLayout,
                               Vec<BoundUniformSource>, ShaderMatrixConvention, ShaderMatrixAbi);

    auto Update(ref<dyn<UniformBufferFrameContext>>,
                mut_ref<dyn<resource::BufferContentWriter>>) const
        -> Result<empty, UniformBufferUpdateError>;
    auto Buffer() const -> resource::BufferUseHandle { return m_buffer; }
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
    ref<dyn<UniformBindingPrepareContext>>, SceneDrawItemId, resource::BufferUseHandle,
    const resource::ShaderArtifactUniformBlock&, Vec<PreparedUniformTextureMetadata> textures = {},
    SceneRenderViewKind        render_view       = SceneRenderViewKind::Primary,
    ShaderMatrixConvention     matrix_convention = ShaderMatrixConvention::ColumnVector,
    ShaderMatrixAbi            matrix_abi        = ShaderMatrixAbi::NativeSpirv,
    Option<ref<SceneMaterial>> material_override = None<ref<SceneMaterial>>())
    -> Result<Box<dyn<UniformBufferUpdate>>, UniformBufferUpdateError>;

auto MakeSharedUniformBufferBinding(ref<dyn<UniformBindingPrepareContext>>,
                                    resource::BufferUseHandle,
                                    const resource::ShaderArtifactUniformBlock&,
                                    ShaderMatrixConvention, ShaderMatrixAbi)
    -> Result<Box<dyn<UniformBufferUpdate>>, UniformBufferUpdateError>;

} // namespace owe::vulkan

export namespace rstd
{

template<>
struct Impl<fmt::Display, owe::vulkan::UniformBufferUpdateError>
    : ImplBase<owe::vulkan::UniformBufferUpdateError> {
    auto fmt(fmt::Formatter& formatter) const -> bool {
        return formatter.write_fmt(fmt::Arguments::make("{}", this->self().message));
    }
};

template<>
struct Impl<fmt::Debug, owe::vulkan::UniformBufferUpdateError>
    : ImplBase<owe::vulkan::UniformBufferUpdateError> {
    auto fmt(fmt::Formatter& formatter) const -> bool {
        return formatter.write_fmt(
            fmt::Arguments::make("UniformBufferUpdateError({})", this->self().message));
    }
};

template<>
struct Impl<error::Error, owe::vulkan::UniformBufferUpdateError>
    : DefaultInImpl<error::Error, owe::vulkan::UniformBufferUpdateError> {};

} // namespace rstd

static_assert(rstd::Impled<owe::vulkan::UniformBufferUpdateError, rstd::error::Error>);
