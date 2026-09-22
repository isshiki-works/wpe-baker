module;

#include <rstd/macro.hpp>

module wescene.vulkan_render;
import rstd;
import rstd.cppstd;
import rstd.log;
import wescene.resource;
import wescene.scene;

using namespace rstd::prelude;
using namespace rstd::literals;

namespace owe::vulkan
{

auto ProgramUniformFrameContext::TextureFrame(SceneDrawItemId draw, usize texture_index) const
    -> Option<SceneTextureFrameView> {
    return m_textures->TextureFrame(draw, texture_index);
}

auto SceneUniformBindingPrepareContext::ResolveDraw(SceneDrawItemId id) const
    -> Option<UniformPrepareDraw> {
    auto draw = m_scene->ResourceIndex().resolve(id);
    if (draw.is_none() || draw->node == nullptr || draw->material == nullptr) return None();
    auto node_id = m_scene->ResourceIndex().nodeId(*draw->node);
    if (node_id.is_none()) return None();
    return Some(UniformPrepareDraw {
        .draw_item = id,
        .node_id   = *node_id,
        .node      = ref<SceneNode>::from_raw_parts(draw->node),
        .material  = ref<SceneMaterial>::from_raw_parts(draw->material),
    });
}

auto SceneUniformBindingPrepareContext::DrawItemFor(ref<SceneNode> node, u32 submesh_index) const
    -> Option<SceneDrawItemId> {
    auto node_id = m_scene->ResourceIndex().nodeId(*node);
    if (node_id.is_none()) return None();
    return m_scene->ResourceIndex().drawItemFor(*node_id, submesh_index);
}

auto SceneUniformBindingPrepareContext::GlobalSources() const -> slice<UniformSourceAttachment> {
    return m_scene->GlobalSources();
}

auto SceneUniformBindingPrepareContext::NodeSources(SceneNodeId node) const
    -> slice<UniformSourceAttachment> {
    return m_scene->NodeSources(node);
}

auto SceneUniformBindingPrepareContext::ResolveSource(UniformSourceId source) const
    -> Option<ref<dyn<UniformSource>>> {
    return m_scene->Resolve(source);
}

auto SceneUniformBindingPrepareContext::ResolveBlock(u64 identity) const
    -> Option<ref<UniformBlockDefinition>> {
    return m_scene->ResolveUniformBlock(identity);
}

namespace
{

struct MatrixCoordinate {
    u32 row;
    u32 column;
};

MatrixCoordinate SceneMatrixCoordinate(u32 row, u32 column, ShaderMatrixConvention convention,
                                       ShaderMatrixAbi matrix_abi) {
    const auto shader_row    = matrix_abi == ShaderMatrixAbi::Hlsl ? column : row;
    const auto shader_column = matrix_abi == ShaderMatrixAbi::Hlsl ? row : column;
    return convention == ShaderMatrixConvention::RowVector
               ? MatrixCoordinate { shader_column, shader_row }
               : MatrixCoordinate { shader_row, shader_column };
}

} // namespace

namespace detail
{

class SourceBindingCompiler {
public:
    SourceBindingCompiler(const UniformBufferLayout& layout, BoundUniformSource& source,
                          ShaderMatrixConvention convention, ShaderMatrixAbi matrix_abi)
        : m_layout(layout), m_source(source), m_convention(convention), m_matrix_abi(matrix_abi) {}

    auto Bind(UniformOutputId output, ref<str> shader_member, UniformValueShape shape)
        -> Result<bool, UniformError> {
        bool matched = false;
        for (usize index {}; index < m_layout.slots.len(); ++index) {
            const auto& slot = m_layout.slots[index];
            if (slot.name.as_str() != shader_member) continue;
            if (slot.size % usize(sizeof(float)) != usize()) {
                return Err(UniformError {
                    .message =
                        rstd::format("uniform {} has non-float size {}", shader_member, slot.size),
                });
            }
            if (slot.scalar_kind != ShaderScalarKind::Unknown &&
                (shape.scalar != UniformScalarType::Float32 ||
                 slot.scalar_kind != ShaderScalarKind::Float || slot.scalar_width != u32(32))) {
                return Err(UniformError {
                    .message =
                        rstd::format("uniform {} scalar type does not match source", shader_member),
                });
            }
            const bool matrix = slot.matrix_rows != u32() && slot.matrix_columns != u32();
            if (matrix != (shape.kind == UniformValueKind::Matrix)) {
                return Err(UniformError {
                    .message = rstd::format("uniform {} value kind does not match reflection",
                                            shader_member),
                });
            }
            if (matrix) {
                const auto last = SceneMatrixCoordinate(slot.matrix_rows - u32(1),
                                                        slot.matrix_columns - u32(1),
                                                        m_convention,
                                                        m_matrix_abi);
                if (shape.rows <= last.row || shape.columns <= last.column ||
                    slot.count < shape.min_array_count || slot.count > shape.max_array_count) {
                    return Err(UniformError {
                        .message = rstd::format(
                            "uniform {} matrix shape mismatch: reflected {}x{}[{}], source "
                            "accepts at least {}x{}[{}..{}]",
                            shader_member,
                            slot.matrix_rows,
                            slot.matrix_columns,
                            slot.count,
                            shape.rows,
                            shape.columns,
                            shape.min_array_count,
                            shape.max_array_count),
                    });
                }
            } else {
                const auto elements =
                    u32(static_cast<rstd::uint32_t>(slot.LogicalFloatElements().to_primitive()));
                if ((shape.min_elements != u32() && elements < shape.min_elements) ||
                    (shape.max_elements != u32() && elements > shape.max_elements)) {
                    return Err(UniformError {
                        .message = rstd::format("uniform {} shape mismatch: reflected {} floats, "
                                                "source expects {}..{}",
                                                shader_member,
                                                elements,
                                                shape.min_elements,
                                                shape.max_elements),
                    });
                }
            }
            bool exists = false;
            for (const auto& binding : m_source.outputs) {
                if (binding.output == output && binding.slot_index == index) {
                    exists = true;
                    break;
                }
            }
            if (! exists) {
                m_source.outputs.push(BoundUniformOutput {
                    .output     = output,
                    .slot_index = index,
                });
            }
            matched = true;
        }
        return Ok(matched);
    }

private:
    const UniformBufferLayout& m_layout;
    BoundUniformSource&        m_source;
    ShaderMatrixConvention     m_convention;
    ShaderMatrixAbi            m_matrix_abi;
};

template<typename Binding>
class SourceValueWriter {
public:
    SourceValueWriter(const Binding& binding, const BoundUniformSource& source)
        : m_binding(binding), m_source(source) {}

    bool Wants(UniformOutputId output) const {
        for (const auto& binding : m_source.outputs) {
            if (binding.output == output) return true;
        }
        return false;
    }

    auto Write(UniformOutputId output, UniformValueView value) -> Result<empty, UniformError> {
        bool wrote = false;
        for (const auto& binding : m_source.outputs) {
            if (binding.output != output) continue;
            auto result = m_binding.WriteSlot(binding.slot_index, value);
            if (result.is_err()) {
                return Err(UniformError {
                    .message = rstd::move(result).unwrap_err_unchecked().message,
                });
            }
            wrote = rstd::move(result).unwrap_unchecked() || wrote;
        }
        if (! wrote) {
            return Err(UniformError {
                .message = String::make("uniform output is not bound"_str),
            });
        }
        return Ok(empty {});
    }

private:
    const Binding&            m_binding;
    const BoundUniformSource& m_source;
};

class ResourceSnapshot {
public:
    ResourceSnapshot(ref<dyn<UniformBufferFrameContext>> frame, SceneDrawItemId draw,
                     slice<PreparedUniformTextureMetadata> textures)
        : m_frame(frame), m_draw(draw), m_textures(textures) {}

    auto Texture(usize index) const -> Option<UniformTextureView> {
        UniformTextureView view;
        bool               available = false;
        if (index < m_textures.len() && m_textures[index].available) {
            const auto& prepared = m_textures[index];
            view.has_extent      = true;
            view.source_extent   = prepared.source_extent;
            view.sample_extent   = prepared.sample_extent;
            view.has_mipmap      = prepared.has_mipmap;
            view.mipmap_level    = prepared.mipmap_level;
            view.revision        = prepared.revision;
            available            = true;
        }
        auto frame = m_frame->TextureFrame(m_draw, index);
        if (frame.is_some()) {
            view.has_transform = true;
            view.rotation      = frame->rotation;
            view.translation   = frame->translation;
            auto revision      = view.revision.to_primitive();
            revision ^= frame->revision.to_primitive() + 0x9e3779b97f4a7c15ULL + (revision << 6U) +
                        (revision >> 2U);
            view.revision = u64(revision);
            if (view.revision == u64()) view.revision = u64(1);
            available = true;
        }
        return available ? Some(view) : None<UniformTextureView>();
    }
    auto Viewport() const -> rstd::array<float, 2> { return m_frame->Viewport(); }
    auto TexelSize() const -> rstd::array<float, 2> {
        const auto viewport = m_frame->Viewport();
        return { viewport[usize(0)] > 0.0f ? 1.0f / viewport[usize(0)] : 0.0f,
                 viewport[usize(1)] > 0.0f ? 1.0f / viewport[usize(1)] : 0.0f };
    }

private:
    mutable ref<dyn<UniformBufferFrameContext>> m_frame;
    SceneDrawItemId                             m_draw;
    slice<PreparedUniformTextureMetadata>       m_textures;
};

class UpdateContext {
public:
    UpdateContext(ref<SceneFrame> frame, const ResourceSnapshot& resources,
                  SceneRenderViewKind render_view)
        : m_frame(frame),
          m_resources(dyn<UniformResourceView>::from_ref(resources)),
          m_render_view(render_view) {}
    UpdateContext(ref<SceneFrame> frame, ref<dyn<UniformResourceView>> resources,
                  SceneRenderViewKind render_view)
        : m_frame(frame), m_resources(resources), m_render_view(render_view) {}

    auto Frame() const -> ref<SceneFrame> { return m_frame; }
    auto Resources() const -> ref<dyn<UniformResourceView>> { return m_resources; }
    auto RenderView() const -> SceneRenderViewKind { return m_render_view; }

private:
    ref<SceneFrame>               m_frame;
    ref<dyn<UniformResourceView>> m_resources;
    SceneRenderViewKind           m_render_view;
};

class EmptyResourceView {
public:
    auto Texture(usize) const -> Option<UniformTextureView> { return None(); }
    auto Viewport() const -> rstd::array<float, 2> { return { 0.0f, 0.0f }; }
    auto TexelSize() const -> rstd::array<float, 2> { return { 0.0f, 0.0f }; }
};

} // namespace detail

} // namespace owe::vulkan

namespace owe::vulkan
{

namespace
{

bool WriteFloat(mut_ref<u8[]> destination, usize offset, float value) {
    if (offset > destination.len() || usize(sizeof(float)) > destination.len() - offset)
        return false;
    auto bytes =
        slice<u8>::from_raw_parts(reinterpret_cast<const byte*>(&value), usize(sizeof(float)));
    for (usize index {}; index < bytes.len(); ++index) destination[offset + index] = bytes[index];
    return true;
}

float MatrixValue(UniformValueView value, usize matrix, u32 row, u32 column) {
    const auto rows     = usize(value.layout.rows.to_primitive());
    const auto columns  = usize(value.layout.columns.to_primitive());
    const auto base     = matrix * rows * columns;
    const auto row_i    = usize(row.to_primitive());
    const auto column_i = usize(column.to_primitive());
    const auto index    = value.layout.matrix_storage == UniformMatrixStorage::ColumnMajor
                              ? base + column_i * rows + row_i
                              : base + row_i * columns + column_i;
    return value.data[index.to_primitive()];
}

auto SerializeMatrixValue(mut_ref<u8[]> destination, const UniformSlot& slot,
                          UniformValueView value, ShaderMatrixConvention convention,
                          ShaderMatrixAbi matrix_abi) -> Result<empty, UniformBufferUpdateError> {
    if (slot.scalar_kind != ShaderScalarKind::Float || slot.scalar_width != u32(32)) {
        return Err(UniformBufferUpdateError {
            .message = rstd::format("uniform {} matrix scalar is not float32", slot.name.as_str()),
        });
    }
    if (slot.matrix_rows == u32() || slot.matrix_columns == u32() ||
        slot.matrix_major == ShaderMatrixMajor::None || slot.matrix_stride == u32()) {
        return Err(UniformBufferUpdateError {
            .message =
                rstd::format("uniform {} has incomplete matrix reflection", slot.name.as_str()),
        });
    }

    const auto source_rows    = value.layout.rows;
    const auto source_columns = value.layout.columns;
    const auto last           = SceneMatrixCoordinate(
        slot.matrix_rows - u32(1), slot.matrix_columns - u32(1), convention, matrix_abi);
    const bool source_fits     = source_rows > last.row && source_columns > last.column &&
                                 value.layout.array_count >= slot.count;
    const auto source_elements = value.layout.MatrixElements() * value.layout.array_count;
    if (! source_fits || value.size < source_elements) {
        return Err(UniformBufferUpdateError {
            .message = rstd::format("uniform {} matrix shape {}x{}[{}] cannot fill {}x{}[{}]",
                                    slot.name.as_str(),
                                    source_rows,
                                    source_columns,
                                    value.layout.array_count,
                                    slot.matrix_rows,
                                    slot.matrix_columns,
                                    slot.count),
        });
    }

    const auto scalar_size = usize(sizeof(float));
    const auto stride      = usize(slot.matrix_stride.to_primitive());
    const auto major_count = slot.matrix_major == ShaderMatrixMajor::Row
                                 ? usize(slot.matrix_rows.to_primitive())
                                 : usize(slot.matrix_columns.to_primitive());
    const auto minor_count = slot.matrix_major == ShaderMatrixMajor::Row
                                 ? usize(slot.matrix_columns.to_primitive())
                                 : usize(slot.matrix_rows.to_primitive());
    if (stride < minor_count * scalar_size) {
        return Err(UniformBufferUpdateError {
            .message = rstd::format(
                "uniform {} matrix stride {} is too small", slot.name.as_str(), slot.matrix_stride),
        });
    }
    const auto matrix_size = major_count * stride;
    const auto array_stride =
        slot.count > usize(1) ? usize(slot.array_stride.to_primitive()) : matrix_size;
    if (slot.count > usize(1) && array_stride < matrix_size) {
        return Err(UniformBufferUpdateError {
            .message = rstd::format(
                "uniform {} array stride {} is too small", slot.name.as_str(), slot.array_stride),
        });
    }

    const auto write_count = slot.count;
    for (usize matrix {}; matrix < write_count; ++matrix) {
        const auto matrix_base = slot.offset + matrix * array_stride;
        for (u32 row {}; row < slot.matrix_rows; ++row) {
            for (u32 column {}; column < slot.matrix_columns; ++column) {
                const auto source = SceneMatrixCoordinate(row, column, convention, matrix_abi);
                const auto target_offset = slot.matrix_major == ShaderMatrixMajor::Row
                                               ? matrix_base + usize(row.to_primitive()) * stride +
                                                     usize(column.to_primitive()) * scalar_size
                                               : matrix_base +
                                                     usize(column.to_primitive()) * stride +
                                                     usize(row.to_primitive()) * scalar_size;
                if (! WriteFloat(destination,
                                 target_offset,
                                 MatrixValue(value, matrix, source.row, source.column))) {
                    return Err(UniformBufferUpdateError {
                        .message = rstd::format("uniform {} matrix write exceeds block",
                                                slot.name.as_str()),
                    });
                }
            }
        }
    }
    return Ok(empty {});
}

auto SerializeLinearValue(mut_ref<u8[]> destination, const UniformSlot& slot,
                          UniformValueView value) -> Result<empty, UniformBufferUpdateError> {
    const auto scalar_size = usize(sizeof(float));
    if (slot.scalar_kind != ShaderScalarKind::Unknown &&
        (slot.scalar_kind != ShaderScalarKind::Float || slot.scalar_width != u32(32))) {
        return Err(UniformBufferUpdateError {
            .message = rstd::format("uniform {} scalar is not float32", slot.name.as_str()),
        });
    }
    if (slot.scalar_kind != ShaderScalarKind::Unknown && value.size < slot.LogicalFloatElements()) {
        return Err(UniformBufferUpdateError {
            .message = rstd::format("uniform {} value has {} floats, expected at least {}",
                                    slot.name.as_str(),
                                    value.size,
                                    slot.LogicalFloatElements()),
        });
    }

    if (slot.scalar_kind == ShaderScalarKind::Unknown || slot.array_stride == u32() ||
        slot.count <= usize(1)) {
        const auto count = rstd::cmp::min(value.size, slot.size / scalar_size);
        for (usize index {}; index < count; ++index) {
            if (! WriteFloat(destination,
                             slot.offset + index * scalar_size,
                             value.data[index.to_primitive()])) {
                return Err(UniformBufferUpdateError {
                    .message = rstd::format("uniform {} write exceeds block", slot.name.as_str()),
                });
            }
        }
        return Ok(empty {});
    }

    const auto components   = usize(slot.vector_components.to_primitive());
    const auto write_count  = rstd::cmp::min(slot.count, value.size / components);
    const auto array_stride = usize(slot.array_stride.to_primitive());
    if (array_stride < components * scalar_size) {
        return Err(UniformBufferUpdateError {
            .message = rstd::format(
                "uniform {} array stride {} is too small", slot.name.as_str(), slot.array_stride),
        });
    }
    for (usize element {}; element < write_count; ++element) {
        for (usize component {}; component < components; ++component) {
            const auto source = element * components + component;
            if (! WriteFloat(destination,
                             slot.offset + element * array_stride + component * scalar_size,
                             value.data[source.to_primitive()])) {
                return Err(UniformBufferUpdateError {
                    .message =
                        rstd::format("uniform {} array write exceeds block", slot.name.as_str()),
                });
            }
        }
    }
    return Ok(empty {});
}

} // namespace

auto SerializeUniformValue(mut_ref<u8[]> destination, const UniformSlot& slot,
                           UniformValueView value, ShaderMatrixConvention convention,
                           ShaderMatrixAbi matrix_abi) -> Result<empty, UniformBufferUpdateError> {
    if (value.data == nullptr) {
        return Err(UniformBufferUpdateError {
            .message = rstd::format("uniform {} value is empty", slot.name.as_str()),
        });
    }
    if (slot.offset > destination.len() || slot.size > destination.len() - slot.offset) {
        return Err(UniformBufferUpdateError {
            .message =
                rstd::format("uniform {} lies outside destination block", slot.name.as_str()),
        });
    }
    const bool reflected_matrix = slot.matrix_rows != u32() && slot.matrix_columns != u32();
    const bool value_matrix     = value.layout.kind == UniformValueKind::Matrix;
    if (reflected_matrix != value_matrix) {
        return Err(UniformBufferUpdateError {
            .message =
                rstd::format("uniform {} value kind does not match reflection", slot.name.as_str()),
        });
    }
    for (usize index {}; index < slot.size; ++index) destination[slot.offset + index] = u8();
    if (value_matrix) {
        return SerializeMatrixValue(destination, slot, value, convention, matrix_abi);
    }
    return SerializeLinearValue(destination, slot, value);
}

auto CompileUniformBufferLayout(const resource::ShaderArtifactUniformBlock& block)
    -> Result<UniformBufferLayout, UniformBufferUpdateError> {
    UniformBufferLayout layout { .size = block.size };
    layout.slots.reserve(block.members.len());

    for (const auto& member : block.members) {
        const auto offset = rstd::as_cast<usize>(member.offset);
        if (offset > block.size || member.size > block.size - offset) {
            return Err(UniformBufferUpdateError {
                .message = rstd::format(
                    "uniform {} lies outside block {}", member.name.as_str(), block.name.as_str()),
            });
        }
        auto dimensions = rstd::vec::Vec<u32>::with_capacity(member.array_dimensions.len());
        for (auto dimension : member.array_dimensions) dimensions.push(u32(dimension));
        layout.slots.push(UniformSlot {
            .name              = member.name.clone(),
            .offset            = offset,
            .size              = member.size,
            .count             = member.count,
            .scalar_kind       = member.scalar_kind,
            .scalar_width      = member.scalar_width,
            .vector_components = member.vector_components,
            .matrix_rows       = member.matrix_rows,
            .matrix_columns    = member.matrix_columns,
            .matrix_stride     = member.matrix_stride,
            .matrix_major      = member.matrix_major,
            .array_stride      = member.array_stride,
            .array_dimensions  = rstd::move(dimensions),
        });
    }

    auto order = rstd::vec::Vec<usize>::with_capacity(layout.slots.len());
    for (usize index {}; index < layout.slots.len(); ++index) {
        order.push(usize(index.to_primitive()));
    }
    rstd::slice_::sort_unstable_by(order.as_mut_slice().as_mut_ref(), [&](usize lhs, usize rhs) {
        return layout.slots[lhs].offset < layout.slots[rhs].offset;
    });
    for (usize index { 1 }; index < order.len(); ++index) {
        const auto& previous = layout.slots[order[index - usize(1)]];
        const auto& current  = layout.slots[order[index]];
        if (current.offset < previous.offset + previous.size) {
            rstd_warn("uniform block {} overlaps {} and {}",
                      block.name.as_str(),
                      previous.name.as_str(),
                      current.name.as_str());
        }
    }
    return Ok(rstd::move(layout));
}

UniformBufferBinding::UniformBufferBinding(
    SceneDrawItemId draw_item, resource::BufferUseHandle buffer, UniformBufferLayout layout,
    Vec<BoundUniformSource> sources, ShaderValues defaults, ref<SceneMaterial> material,
    Vec<PreparedUniformTextureMetadata> textures, SceneRenderViewKind render_view,
    ShaderMatrixConvention matrix_convention, ShaderMatrixAbi matrix_abi)
    : m_draw_item(draw_item),
      m_buffer(buffer),
      m_layout(rstd::move(layout)),
      m_sources(rstd::move(sources)),
      m_data(rstd::vec::Vec<u8>::with_capacity(m_layout.size)),
      m_base_data(rstd::vec::Vec<u8>::with_capacity(m_layout.size)),
      m_defaults(rstd::move(defaults)),
      m_material(material),
      m_textures(rstd::move(textures)),
      m_render_view(render_view),
      m_matrix_convention(matrix_convention),
      m_matrix_abi(matrix_abi) {
    m_data.resize(m_layout.size, u8(0));
    m_base_data.resize(m_layout.size, u8(0));
}

auto UniformBufferBinding::WriteSlot(usize slot_index, UniformValueView value) const
    -> Result<bool, UniformBufferUpdateError> {
    if (slot_index >= m_layout.slots.len()) return Ok(false);
    auto serialized = SerializeUniformValue(m_data.as_mut_slice().as_mut_ref(),
                                            m_layout.slots[slot_index],
                                            value,
                                            m_matrix_convention,
                                            m_matrix_abi);
    if (serialized.is_err()) return Err(rstd::move(serialized).unwrap_err_unchecked());
    return Ok(true);
}

auto UniformBufferBinding::WriteName(std::string_view name, const UniformValue& value) const
    -> Result<bool, UniformBufferUpdateError> {
    for (usize index {}; index < m_layout.slots.len(); ++index) {
        if (rstd::cppstd::as_string_view(m_layout.slots[index].name.as_str()) != name) continue;
        return WriteSlot(index, value.View());
    }
    return Ok(false);
}

auto UniformBufferBinding::Update(ref<dyn<UniformBufferFrameContext>>         frame_context,
                                  mut_ref<dyn<resource::BufferContentWriter>> buffers) const
    -> Result<empty, UniformBufferUpdateError> {
    auto&      material = *m_material;
    const bool force    = ! m_uploaded || m_material_version != material.customShader.value_version;
    if (force) {
        for (usize index {}; index < m_data.len(); ++index) m_data[index] = u8();
        for (const auto& [name, value] : m_defaults) {
            auto written = WriteName(name, value);
            if (written.is_err()) return Err(rstd::move(written).unwrap_err_unchecked());
        }
        for (const auto& [name, value] : material.customShader.constValues) {
            auto written = WriteName(name, value);
            if (written.is_err()) return Err(rstd::move(written).unwrap_err_unchecked());
        }
        m_base_data = m_data.clone();
    }

    detail::ResourceSnapshot resources(frame_context, m_draw_item, m_textures.as_slice());

    detail::UpdateContext context_impl(frame_context->Frame(), resources, m_render_view);
    auto                  context = dyn<UniformUpdateContext>::from_ref(context_impl);

    auto versions = Vec<u64>::with_capacity(m_sources.len());
    bool changed  = force;
    for (auto& bound : m_sources) {
        u64 version = bound.source->Version(context.as_ref());
        versions.push(rstd::move(version));
        changed = changed || ! bound.evaluated || bound.version != version;
    }
    if (! changed) return Ok(empty {});

    rstd::slice_::copy_from_slice(m_data.as_mut_slice().as_mut_ref(), m_base_data.as_slice());
    usize source_index {};
    for (auto& bound : m_sources) {
        detail::SourceValueWriter writer_impl(*this, bound);
        auto                      writer = dyn<UniformValueSink>::from_ref(writer_impl);
        auto evaluated = bound.source->Evaluate(context.as_ref(), writer.as_mut_ref());
        if (evaluated.is_err()) {
            return Err(UniformBufferUpdateError {
                .message = rstd::move(evaluated).unwrap_err_unchecked().message,
            });
        }
        bound.version   = versions[source_index++];
        bound.evaluated = true;
    }

    auto updated = buffers->UpdateBuffer(m_buffer, m_data.as_slice());
    if (updated.is_err()) {
        auto error = rstd::move(updated).unwrap_err_unchecked();
        return Err(UniformBufferUpdateError { .message = rstd::move(error.message) });
    }
    m_material_version = material.customShader.value_version;
    m_uploaded         = true;
    return Ok(empty {});
}

SharedUniformBufferBinding::SharedUniformBufferBinding(resource::BufferUseHandle buffer,
                                                       UniformBufferLayout       layout,
                                                       Vec<BoundUniformSource>   sources,
                                                       ShaderMatrixConvention    matrix_convention,
                                                       ShaderMatrixAbi           matrix_abi)
    : m_buffer(buffer),
      m_layout(rstd::move(layout)),
      m_sources(rstd::move(sources)),
      m_data(rstd::vec::Vec<u8>::with_capacity(m_layout.size)),
      m_matrix_convention(matrix_convention),
      m_matrix_abi(matrix_abi) {
    m_data.resize(m_layout.size, u8());
}

auto SharedUniformBufferBinding::WriteSlot(usize slot_index, UniformValueView value) const
    -> Result<bool, UniformBufferUpdateError> {
    if (slot_index >= m_layout.slots.len()) return Ok(false);
    auto serialized = SerializeUniformValue(m_data.as_mut_slice().as_mut_ref(),
                                            m_layout.slots[slot_index],
                                            value,
                                            m_matrix_convention,
                                            m_matrix_abi);
    if (serialized.is_err()) return Err(rstd::move(serialized).unwrap_err_unchecked());
    return Ok(true);
}

auto SharedUniformBufferBinding::Update(ref<dyn<UniformBufferFrameContext>>         frame_context,
                                        mut_ref<dyn<resource::BufferContentWriter>> buffers) const
    -> Result<empty, UniformBufferUpdateError> {
    detail::EmptyResourceView resources_impl;
    auto                      resources = dyn<UniformResourceView>::from_ref(resources_impl);
    detail::UpdateContext     context_impl(
        frame_context->Frame(), resources.as_ref(), SceneRenderViewKind::Primary);
    auto context = dyn<UniformUpdateContext>::from_ref(context_impl);

    auto versions = Vec<u64>::with_capacity(m_sources.len());
    bool changed  = ! m_uploaded;
    for (auto& bound : m_sources) {
        auto version = bound.source->Version(context.as_ref());
        versions.push(rstd::move(version));
        changed = changed || ! bound.evaluated || bound.version != version;
    }
    if (! changed) return Ok(empty {});

    for (usize index {}; index < m_data.len(); ++index) m_data[index] = u8();
    usize source_index {};
    for (auto& bound : m_sources) {
        detail::SourceValueWriter writer_impl(*this, bound);
        auto                      writer = dyn<UniformValueSink>::from_ref(writer_impl);
        auto evaluated = bound.source->Evaluate(context.as_ref(), writer.as_mut_ref());
        if (evaluated.is_err()) {
            return Err(UniformBufferUpdateError {
                .message = rstd::move(evaluated).unwrap_err_unchecked().message,
            });
        }
        bound.version   = versions[source_index++];
        bound.evaluated = true;
    }
    auto updated = buffers->UpdateBuffer(m_buffer, m_data.as_slice());
    if (updated.is_err()) {
        return Err(UniformBufferUpdateError {
            .message = rstd::move(updated).unwrap_err_unchecked().message,
        });
    }
    m_uploaded = true;
    return Ok(empty {});
}

auto MakeSharedUniformBufferBinding(ref<dyn<UniformBindingPrepareContext>>      prepare,
                                    resource::BufferUseHandle                   buffer,
                                    const resource::ShaderArtifactUniformBlock& block,
                                    ShaderMatrixConvention                      matrix_convention,
                                    ShaderMatrixAbi                             matrix_abi)
    -> Result<Box<dyn<UniformBufferUpdate>>, UniformBufferUpdateError> {
    auto definition = prepare->ResolveBlock(block.identity);
    if (definition.is_none() || (**definition).scope != UniformBlockScope::Shared) {
        return Err(UniformBufferUpdateError {
            .message = String::make("shared uniform block definition is unavailable"_str),
        });
    }
    auto layout_result = CompileUniformBufferLayout(block);
    if (layout_result.is_err()) return Err(rstd::move(layout_result).unwrap_err_unchecked());
    auto layout  = rstd::move(layout_result).unwrap_unchecked();
    auto sources = Vec<BoundUniformSource>::make();
    for (const auto& attachment : (**definition).sources) {
        auto source = prepare->ResolveSource(attachment.source);
        if (source.is_none()) {
            return Err(UniformBufferUpdateError {
                .message = String::make("shared uniform source is unavailable"_str),
            });
        }
        BoundUniformSource bound {
            .source   = *source,
            .priority = attachment.priority,
        };
        detail::SourceBindingCompiler compiler_impl(layout, bound, matrix_convention, matrix_abi);
        auto                          compiler  = dyn<UniformBindingSink>::from_ref(compiler_impl);
        auto                          described = bound.source->Describe(compiler.as_mut_ref());
        if (described.is_err()) {
            return Err(UniformBufferUpdateError {
                .message = rstd::move(described).unwrap_err_unchecked().message,
            });
        }
        if (bound.outputs.is_empty()) continue;
        bound.lease = bound.source->AcquireBindingLease();
        sources.push(rstd::move(bound));
    }
    SharedUniformBufferBinding binding(
        buffer, rstd::move(layout), rstd::move(sources), matrix_convention, matrix_abi);
    return Ok(Box<dyn<UniformBufferUpdate>>::make(rstd::move(binding)));
}

auto MakeUniformBufferBinding(ref<dyn<UniformBindingPrepareContext>> prepare,
                              SceneDrawItemId draw_item, resource::BufferUseHandle buffer,
                              const resource::ShaderArtifactUniformBlock& block,
                              Vec<PreparedUniformTextureMetadata>         textures,
                              SceneRenderViewKind                         render_view,
                              ShaderMatrixConvention matrix_convention, ShaderMatrixAbi matrix_abi,
                              Option<ref<SceneMaterial>> material_override)
    -> Result<Box<dyn<UniformBufferUpdate>>, UniformBufferUpdateError> {
    auto draw = prepare->ResolveDraw(draw_item);
    if (draw.is_none()) {
        return Err(UniformBufferUpdateError {
            .message = String::make("uniform binding scene data is unavailable"_str),
        });
    }
    auto material = material_override.unwrap_or(draw->material);
    if (! material->customShader.shader) {
        return Err(UniformBufferUpdateError {
            .message = String::make("uniform binding shader metadata is unavailable"_str),
        });
    }

    auto layout_result = CompileUniformBufferLayout(block);
    if (layout_result.is_err()) {
        return Err(rstd::move(layout_result).unwrap_err_unchecked());
    }
    auto layout = rstd::move(layout_result).unwrap_unchecked();

    struct RankedSource {
        UniformSourceId source;
        i32             priority {};
    };
    auto ranked = Vec<RankedSource>::make();
    auto append = [&](slice<UniformSourceAttachment> attachments) {
        for (usize index {}; index < attachments.len(); ++index) {
            const auto&   attachment = attachments[index];
            Option<usize> found;
            for (usize candidate {}; candidate < ranked.len(); ++candidate) {
                if (ranked[candidate].source == attachment.source) {
                    found = Some(candidate);
                    break;
                }
            }
            if (found.is_none()) {
                ranked.push(RankedSource { attachment.source, attachment.priority });
            } else {
                ranked[*found].priority = attachment.priority;
            }
        }
    };
    append(prepare->GlobalSources());
    append(prepare->NodeSources(draw->node_id));
    rstd::slice_::sort_unstable_by(ranked.as_mut_slice().as_mut_ref(),
                                   [](const auto& lhs, const auto& rhs) {
                                       if (lhs.priority != rhs.priority)
                                           return lhs.priority < rhs.priority;
                                       if (lhs.source.generation != rhs.source.generation)
                                           return lhs.source.generation < rhs.source.generation;
                                       return lhs.source.index < rhs.source.index;
                                   });

    auto sources = Vec<BoundUniformSource>::with_capacity(ranked.len());
    rstd::collections::HashMap<usize, usize> slot_sources;
    usize                                    source_ordinal { 0 };
    for (const auto& candidate : ranked) {
        auto source = prepare->ResolveSource(candidate.source);
        if (source.is_none()) {
            return Err(UniformBufferUpdateError {
                .message = String::make("scene uniform source is unavailable"_str),
            });
        }
        BoundUniformSource bound {
            .source   = *source,
            .priority = candidate.priority,
        };
        detail::SourceBindingCompiler compiler_impl(layout, bound, matrix_convention, matrix_abi);
        auto                          compiler  = dyn<UniformBindingSink>::from_ref(compiler_impl);
        auto                          described = bound.source->Describe(compiler.as_mut_ref());
        if (described.is_err()) {
            return Err(UniformBufferUpdateError {
                .message = rstd::move(described).unwrap_err_unchecked().message,
            });
        }
        if (bound.outputs.is_empty()) continue;
        bound.lease = bound.source->AcquireBindingLease();
        for (const auto& output : bound.outputs) {
            auto previous = slot_sources.get(output.slot_index);
            if (previous.is_none()) {
                (void)slot_sources.insert(output.slot_index, source_ordinal);
            } else if (**previous != source_ordinal) {
                rstd_warn("uniform slot {} has multiple sources {} and {}",
                          layout.slots[output.slot_index].name.as_str(),
                          **previous,
                          source_ordinal);
            }
        }
        sources.push(rstd::move(bound));
        ++source_ordinal;
    }

    const auto&          shader = *material->customShader.shader;
    UniformBufferBinding binding(draw->draw_item,
                                 buffer,
                                 rstd::move(layout),
                                 rstd::move(sources),
                                 shader.default_uniforms,
                                 material,
                                 rstd::move(textures),
                                 render_view,
                                 matrix_convention,
                                 matrix_abi);
    return Ok(Box<dyn<UniformBufferUpdate>>::make(rstd::move(binding)));
}

} // namespace owe::vulkan
