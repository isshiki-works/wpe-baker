export module wescene.scene:uniform;
import :id;
import :runtime;
import eigen;
import rstd;
import rstd.cppstd;
import wescene.core;

using namespace rstd::prelude;
using rstd::collections::HashMap;

export namespace owe
{

using ShaderValueInter = rstd::array<float, 16>;

enum class UniformScalarType : rstd::uint8_t
{
    Float32,
};

enum class UniformValueKind : rstd::uint8_t
{
    Linear,
    Matrix,
};

enum class UniformMatrixStorage : rstd::uint8_t
{
    RowMajor,
    ColumnMajor,
};

struct UniformValueLayout {
    UniformScalarType    scalar { UniformScalarType::Float32 };
    UniformValueKind     kind { UniformValueKind::Linear };
    u32                  rows { u32(1) };
    u32                  columns { u32(1) };
    usize                array_count { usize(1) };
    UniformMatrixStorage matrix_storage { UniformMatrixStorage::ColumnMajor };

    static auto Linear(usize elements) -> UniformValueLayout {
        return { .columns = rstd::as_cast<u32>(elements) };
    }

    static auto Matrix(u32 rows, u32 columns, usize array_count, UniformMatrixStorage storage)
        -> UniformValueLayout {
        return {
            .kind           = UniformValueKind::Matrix,
            .rows           = rows,
            .columns        = columns,
            .array_count    = array_count,
            .matrix_storage = storage,
        };
    }

    usize MatrixElements() const {
        return usize(rows.to_primitive()) * usize(columns.to_primitive());
    }

    friend bool operator==(const UniformValueLayout&, const UniformValueLayout&) = default;
};

struct UniformValueView {
    const float*       data { nullptr };
    usize              size {};
    UniformValueLayout layout;
};

class ShaderValue {
public:
    using value_type = float;

    ShaderValue()  = default;
    ~ShaderValue() = default;

    ShaderValue(const ShaderValue& other) noexcept
        : m_dynamic(other.m_dynamic),
          m_value(other.m_value),
          m_dynamic_value(CloneDynamic(other.m_dynamic_value)),
          m_size(other.m_size),
          m_layout(other.m_layout) {}
    ShaderValue& operator=(const ShaderValue& other) noexcept {
        if (this == &other) return *this;
        m_dynamic       = other.m_dynamic;
        m_value         = other.m_value;
        m_dynamic_value = CloneDynamic(other.m_dynamic_value);
        m_size          = other.m_size;
        m_layout        = other.m_layout;
        return *this;
    }

    ShaderValue(const value_type& value) noexcept {
        fromSlice(slice<value_type>::from_raw_parts(rstd::addressof(value), usize(1)));
    }
    template<typename Range>
    ShaderValue(const Range& range) noexcept {
        const auto len = [&]() -> usize {
            if constexpr (requires { range.len(); })
                return range.len();
            else
                return usize(range.size());
        }();
        fromSlice(slice<value_type>::from_raw_parts(range.data(), len));
    }
    ShaderValue(const value_type* ptr, usize num) noexcept {
        fromSlice(slice<value_type>::from_raw_parts(ptr, num));
    }
    explicit ShaderValue(UniformValueView view) noexcept {
        fromSlice(slice<value_type>::from_raw_parts(view.data, view.size));
        m_layout = view.layout;
    }

    static ShaderValue fromMatrix(const Eigen::Ref<const Eigen::MatrixXf>& mat) {
        auto value     = ShaderValue(mat.data(), usize(mat.size()));
        value.m_layout = UniformValueLayout::Matrix(u32(static_cast<rstd::uint32_t>(mat.rows())),
                                                    u32(static_cast<rstd::uint32_t>(mat.cols())),
                                                    usize(1),
                                                    UniformMatrixStorage::ColumnMajor);
        return value;
    }
    static ShaderValue fromMatrix(const Eigen::Ref<const Eigen::MatrixXd>& mat) {
        Eigen::MatrixXf matf = mat.cast<float>();
        return fromMatrix(matf);
    }
    static ShaderValue fromMatrixArray(const value_type* ptr, u32 rows, u32 columns, usize count,
                                       UniformMatrixStorage storage) {
        auto value =
            ShaderValue(ptr, usize(rows.to_primitive()) * usize(columns.to_primitive()) * count);
        value.m_layout = UniformValueLayout::Matrix(rows, columns, count, storage);
        return value;
    }

    const auto& operator[](usize index) const { return value()[index]; }
    auto& operator[](usize index) { return m_dynamic ? m_dynamic_value[index] : m_value[index]; }

    auto  data() const noexcept { return value().as_raw_ptr(); }
    usize size() const noexcept { return m_size; }
    auto  View() const noexcept -> UniformValueView { return { data(), size(), m_layout }; }

    void setSize(usize size) noexcept { m_size = rstd::cmp::min(size, value().len()); }

private:
    static auto CloneDynamic(const Vec<value_type>& source) noexcept -> Vec<value_type> {
        auto result = Vec<value_type>::with_capacity(source.len());
        for (const auto value : source) result.push_back(value);
        return result;
    }

    void fromSlice(slice<value_type> values) noexcept;

    slice<value_type> value() const noexcept {
        if (m_dynamic) return m_dynamic_value.as_slice();
        return m_value.as_slice();
    }

    bool               m_dynamic { false };
    ShaderValueInter   m_value;
    Vec<value_type>    m_dynamic_value;
    usize              m_size {};
    UniformValueLayout m_layout;
};

using UniformValue   = ShaderValue;
using ShaderValues   = Map<std::string, ShaderValue>;
using ShaderValueMap = ShaderValues;

struct UniformOutputId {
    u32 value { u32::MAX };

    bool        Valid() const noexcept { return value != u32::MAX; }
    friend bool operator==(const UniformOutputId&, const UniformOutputId&) = default;
};

struct UniformSourceId {
    u32 index { u32::MAX };
    u32 generation { 0 };

    bool        Valid() const noexcept { return index != u32::MAX && generation != u32(); }
    friend bool operator==(const UniformSourceId&, const UniformSourceId&) = default;
};

struct UniformSourceAttachment {
    UniformSourceId source;
    i32             priority {};
};

enum class UniformBlockScope : rstd::uint8_t
{
    Shared,
    Local,
};

struct UniformBlockDefinition {
    u64                          identity {};
    String                       name;
    UniformBlockScope            scope { UniformBlockScope::Local };
    Vec<UniformSourceAttachment> sources;
};

struct UniformValueShape {
    UniformScalarType scalar { UniformScalarType::Float32 };
    UniformValueKind  kind { UniformValueKind::Linear };
    u32               min_elements {};
    u32               max_elements {};
    u32               rows { u32(1) };
    u32               columns { u32(1) };
    usize             min_array_count { usize(1) };
    usize             max_array_count { usize(1) };

    static auto Float(u32 elements) -> UniformValueShape {
        return { .min_elements = elements, .max_elements = elements };
    }
    static auto FloatRange(u32 min_elements, u32 max_elements) -> UniformValueShape {
        return { .min_elements = min_elements, .max_elements = max_elements };
    }
    static auto Matrix(u32 rows, u32 columns) -> UniformValueShape {
        return {
            .kind    = UniformValueKind::Matrix,
            .rows    = rows,
            .columns = columns,
        };
    }
    static auto MatrixArray(u32 rows, u32 columns, usize min_count, usize max_count)
        -> UniformValueShape {
        return {
            .kind            = UniformValueKind::Matrix,
            .rows            = rows,
            .columns         = columns,
            .min_array_count = min_count,
            .max_array_count = max_count,
        };
    }
};

struct UniformError {
    String message;
};

enum class SceneRenderViewKind
{
    Primary,
    Reflection,
};

struct UniformBindingSink {
    using Trait                  = UniformBindingSink;
    static constexpr bool direct = false;

    template<typename Self, typename = void>
    struct Api {
        using Trait = UniformBindingSink;

        auto Bind(UniformOutputId output, ref<str> shader_member, UniformValueShape shape = {})
            -> Result<bool, UniformError> {
            return rstd::trait_call<0>(this, output, shader_member, shape);
        }
    };

    template<typename T>
    using Funcs = TraitFuncs<&T::Bind>;
};

struct UniformValueSink {
    using Trait                  = UniformValueSink;
    static constexpr bool direct = false;

    template<typename Self, typename = void>
    struct Api {
        using Trait = UniformValueSink;

        bool Wants(UniformOutputId output) const { return rstd::trait_call<0>(this, output); }
        auto Write(UniformOutputId output, UniformValueView value) -> Result<empty, UniformError> {
            return rstd::trait_call<1>(this, output, value);
        }
    };

    template<typename T>
    using Funcs = TraitFuncs<&T::Wants, &T::Write>;
};

struct UniformTextureView {
    bool                  has_extent { false };
    rstd::array<float, 2> source_extent { 0.0f, 0.0f };
    rstd::array<float, 2> sample_extent { 0.0f, 0.0f };
    bool                  has_mipmap { false };
    float                 mipmap_level { 0.0f };
    bool                  has_transform { false };
    rstd::array<float, 4> rotation { 1.0f, 0.0f, 0.0f, 1.0f };
    rstd::array<float, 2> translation { 0.0f, 0.0f };
    u64                   revision { 1 };
};

struct UniformResourceView {
    using Trait                  = UniformResourceView;
    static constexpr bool direct = false;

    template<typename Self, typename = void>
    struct Api {
        using Trait = UniformResourceView;

        auto Texture(usize texture_index) const -> Option<UniformTextureView> {
            return rstd::trait_call<0>(this, texture_index);
        }
        auto Viewport() const -> rstd::array<float, 2> { return rstd::trait_call<1>(this); }
        auto TexelSize() const -> rstd::array<float, 2> { return rstd::trait_call<2>(this); }
    };

    template<typename T>
    using Funcs = TraitFuncs<&T::Texture, &T::Viewport, &T::TexelSize>;
};

struct UniformUpdateContext {
    using Trait                  = UniformUpdateContext;
    static constexpr bool direct = false;

    template<typename Self, typename = void>
    struct Api {
        using Trait = UniformUpdateContext;

        auto Frame() const -> ref<SceneFrame> { return rstd::trait_call<0>(this); }
        auto Resources() const -> ref<dyn<UniformResourceView>> {
            return rstd::trait_call<1>(this);
        }
        auto RenderView() const -> SceneRenderViewKind { return rstd::trait_call<2>(this); }
    };

    template<typename T>
    using Funcs = TraitFuncs<&T::Frame, &T::Resources, &T::RenderView>;
};

struct UniformBindingLease {
    using Trait                  = UniformBindingLease;
    static constexpr bool direct = false;

    template<typename Self, typename = void>
    struct Api {
        using Trait = UniformBindingLease;

        void KeepAlive() const { rstd::trait_call<0>(this); }
    };

    template<typename T>
    using Funcs = TraitFuncs<&T::KeepAlive>;
};

struct UniformSource {
    using Trait                  = UniformSource;
    static constexpr bool direct = false;

    template<typename Self, typename = void>
    struct Api {
        using Trait = UniformSource;

        auto Describe(mut_ref<dyn<UniformBindingSink>> sink) const -> Result<empty, UniformError> {
            return rstd::trait_call<0>(this, sink);
        }
        auto Version(ref<dyn<UniformUpdateContext>> context) const -> u64 {
            return rstd::trait_call<1>(this, context);
        }
        auto Evaluate(ref<dyn<UniformUpdateContext>> context,
                      mut_ref<dyn<UniformValueSink>> sink) const -> Result<empty, UniformError> {
            return rstd::trait_call<2>(this, context, sink);
        }
        auto AcquireBindingLease() const -> Option<Box<dyn<UniformBindingLease>>> {
            return rstd::trait_call<3>(this);
        }
    };

    template<typename T>
    using Funcs = TraitFuncs<&T::Describe, &T::Version, &T::Evaluate, &T::AcquireBindingLease>;
};

struct UniformSourceRegistrar {
    using Trait                  = UniformSourceRegistrar;
    static constexpr bool direct = false;

    template<typename Self, typename = void>
    struct Api {
        using Trait = UniformSourceRegistrar;

        auto Register(Box<dyn<UniformSource>> source) -> UniformSourceId {
            return rstd::trait_call<0>(this, rstd::move(source));
        }
    };

    template<typename T>
    using Funcs = TraitFuncs<&T::Register>;
};

struct UniformAttachmentWriter {
    using Trait                  = UniformAttachmentWriter;
    static constexpr bool direct = false;

    template<typename Self, typename = void>
    struct Api {
        using Trait = UniformAttachmentWriter;

        bool AttachGlobal(UniformSourceId source, i32 priority = i32()) {
            return rstd::trait_call<0>(this, source, priority);
        }
        bool AttachNode(SceneNodeId node, UniformSourceId source, i32 priority = i32()) {
            return rstd::trait_call<1>(this, node, source, priority);
        }
    };

    template<typename T>
    using Funcs = TraitFuncs<&T::AttachGlobal, &T::AttachNode>;
};

struct UniformSourceCatalog {
    using Trait                  = UniformSourceCatalog;
    static constexpr bool direct = false;

    template<typename Self, typename = void>
    struct Api {
        using Trait = UniformSourceCatalog;

        auto Resolve(UniformSourceId id) const -> Option<ref<dyn<UniformSource>>> {
            return rstd::trait_call<0>(this, id);
        }
    };

    template<typename T>
    using Funcs = TraitFuncs<&T::Resolve>;
};

struct UniformAttachmentCatalog {
    using Trait                  = UniformAttachmentCatalog;
    static constexpr bool direct = false;

    template<typename Self, typename = void>
    struct Api {
        using Trait = UniformAttachmentCatalog;

        auto GlobalSources() const -> slice<UniformSourceAttachment> {
            return rstd::trait_call<0>(this);
        }
        auto NodeSources(SceneNodeId node) const -> slice<UniformSourceAttachment> {
            return rstd::trait_call<1>(this, node);
        }
    };

    template<typename T>
    using Funcs = TraitFuncs<&T::GlobalSources, &T::NodeSources>;
};

class SceneUniformRegistry {
public:
    auto Register(Box<dyn<UniformSource>> source) -> UniformSourceId {
        auto id = UniformSourceId {
            .index      = rstd::as_cast<u32>(m_sources.len()),
            .generation = m_generation,
        };
        m_sources.push(rstd::move(source));
        return id;
    }

    template<typename T>
    auto RegisterSource(T source) -> UniformSourceId {
        return Register(Box<dyn<UniformSource>>::make(rstd::move(source)));
    }

    auto Resolve(UniformSourceId id) const -> Option<ref<dyn<UniformSource>>> {
        if (! id.Valid() || id.generation != m_generation ||
            rstd::as_cast<usize>(id.index) >= m_sources.len()) {
            return None();
        }
        return Some(m_sources[rstd::as_cast<usize>(id.index)].as_ref());
    }

    bool AttachGlobal(UniformSourceId source, i32 priority = i32()) {
        if (Resolve(source).is_none()) return false;
        return AttachUnique(m_global_sources, source, priority);
    }

    bool AttachNode(SceneNodeId node, UniformSourceId source, i32 priority = i32()) {
        if (! node.Valid() || Resolve(source).is_none()) return false;
        auto key         = IdKey(node);
        auto attachments = m_node_sources.get_mut(key);
        if (attachments.is_none()) {
            (void)m_node_sources.insert(key, Vec<UniformSourceAttachment>::make());
            attachments = m_node_sources.get_mut(key);
        }
        return AttachUnique(**attachments, source, priority);
    }

    auto GlobalSources() const -> slice<UniformSourceAttachment> {
        return m_global_sources.as_slice();
    }

    auto NodeSources(SceneNodeId node) const -> slice<UniformSourceAttachment> {
        auto found = m_node_sources.get(IdKey(node));
        return found.is_some() ? (**found).as_slice() : slice<UniformSourceAttachment> {};
    }

    bool RegisterBlock(UniformBlockDefinition definition) {
        if (definition.identity == u64() || definition.name.is_empty()) return false;
        for (const auto& attachment : definition.sources) {
            if (Resolve(attachment.source).is_none()) return false;
        }
        auto existing = m_blocks.get(definition.identity);
        if (existing.is_some()) {
            if ((**existing).name != definition.name || (**existing).scope != definition.scope ||
                (**existing).sources.len() != definition.sources.len()) {
                return false;
            }
            for (usize index {}; index < definition.sources.len(); ++index) {
                const auto& lhs = (**existing).sources[index];
                const auto& rhs = definition.sources[index];
                if (lhs.source != rhs.source || lhs.priority != rhs.priority) return false;
            }
            return true;
        }
        (void)m_blocks.insert(definition.identity, rstd::move(definition));
        return true;
    }

    auto ResolveBlock(u64 identity) const -> Option<ref<UniformBlockDefinition>> {
        return m_blocks.get(identity);
    }

    void Reset() {
        m_sources.clear();
        m_global_sources.clear();
        m_node_sources.clear();
        m_blocks.clear();
        ++m_generation;
        if (m_generation == u32()) ++m_generation;
    }

    usize Size() const noexcept { return m_sources.len(); }

private:
    static u64 IdKey(SceneNodeId id) {
        return (rstd::as_cast<u64>(id.generation) << u64(32)) | rstd::as_cast<u64>(id.index);
    }

    static bool AttachUnique(Vec<UniformSourceAttachment>& attachments, UniformSourceId source,
                             i32 priority) {
        for (auto& attachment : attachments) {
            if (attachment.source != source) continue;
            attachment.priority = priority;
            return true;
        }
        attachments.push(UniformSourceAttachment { .source = source, .priority = priority });
        return true;
    }

    Vec<Box<dyn<UniformSource>>>               m_sources;
    Vec<UniformSourceAttachment>               m_global_sources;
    HashMap<u64, Vec<UniformSourceAttachment>> m_node_sources;
    HashMap<u64, UniformBlockDefinition>       m_blocks;
    u32                                        m_generation { 1 };
};

} // namespace owe

export namespace rstd
{

template<>
struct Impl<fmt::Display, owe::UniformError> : ImplBase<owe::UniformError> {
    auto fmt(fmt::Formatter& formatter) const -> bool {
        return formatter.write_fmt(fmt::Arguments::make("{}", this->self().message));
    }
};

template<>
struct Impl<fmt::Debug, owe::UniformError> : ImplBase<owe::UniformError> {
    auto fmt(fmt::Formatter& formatter) const -> bool {
        return formatter.write_fmt(fmt::Arguments::make("UniformError({})", this->self().message));
    }
};

template<>
struct Impl<error::Error, owe::UniformError> : DefaultInImpl<error::Error, owe::UniformError> {};

} // namespace rstd

static_assert(rstd::Impled<owe::UniformError, rstd::error::Error>);
