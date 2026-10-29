module wescene.vulkan_render;
import wescene.core;
import rstd;
import rstd.cppstd;
import wescene.vulkan;
import wescene.scene;

using namespace rstd::prelude;

namespace owe::vulkan
{

namespace
{

auto bytesOf(const float* data, usize size) -> rstd::slice<rstd::u8> {
    return rstd::slice<rstd::u8>::from_raw_parts(reinterpret_cast<const rstd::byte*>(data), size);
}

auto bytesOf(const rstd::uint32_t* data, usize size) -> rstd::slice<u8> {
    return rstd::slice<rstd::u8>::from_raw_parts(reinterpret_cast<const rstd::byte*>(data), size);
}

u64 next_dynamic_allocation_generation() {
    static std::atomic_uint64_t next { 1 };
    return u64(next.fetch_add(1, std::memory_order_relaxed));
}

u64 resolve_allocation_generation(const DrawBufferRequest& request, bool dynamic) {
    if (! dynamic) return u64();
    if (request.dynamic_allocation_generation != u64())
        return request.dynamic_allocation_generation;
    return next_dynamic_allocation_generation();
}

} // namespace

String BuildDrawBufferResourceName(SceneDrawItemId draw_item, DrawBufferRole role,
                                   u32 stream_index) {
    if (! draw_item.Valid()) return {};
    const char* role_name = "uniform";
    if (role == DrawBufferRole::Vertex) role_name = "vertex";
    if (role == DrawBufferRole::Index) role_name = "index";
    return rstd::format(
        "draw:{}:{}:{}:{}", draw_item.generation, draw_item.index, role_name, stream_index);
}

RenderBufferResolver::RenderBufferResolver(
    const resource_registry::PreparedResourceTable& resources)
    : m_resources(rstd::ref<resource_registry::PreparedResourceTable>::from_raw_parts(
          rstd::addressof(resources))) {}

std::vector<DrawBufferKey> BuildDrawBufferKeys(const DrawBufferRequest& request,
                                               u64                      allocation_generation) {
    std::vector<DrawBufferKey> keys;
    if (request.mesh == nullptr) return keys;
    const SceneMesh&  mesh          = *request.mesh;
    const std::size_t submesh_index = request.submesh_index.to_primitive();
    if (submesh_index >= mesh.Submeshes().size()) return keys;

    const auto& submesh = mesh.Submeshes()[submesh_index];
    const bool  has_filter = ! request.active_vertex_streams.is_empty();
    const std::size_t stream_count =
        has_filter ? request.active_vertex_streams.len().to_primitive()
                   : submesh.vertex_arrays.size();
    keys.reserve(stream_count + (submesh.index_arrays.empty() ? 0u : 1u));
    const u64 resolved_allocation = mesh.Dynamic() ? allocation_generation : u64();

    for (std::size_t slot = 0; slot < stream_count; ++slot) {
        const std::size_t stream_idx =
            has_filter ? request.active_vertex_streams[usize(slot)].to_primitive() : slot;
        if (stream_idx >= submesh.vertex_arrays.size()) continue;
        const auto& vertex = submesh.vertex_arrays[stream_idx];
        keys.push_back(DrawBufferKey {
            .render_item           = request.render_item,
            .role                  = DrawBufferRole::Vertex,
            .submesh_index         = request.submesh_index,
            .stream_index          = u32(static_cast<rstd::uint32_t>(stream_idx)),
            .data_generation       = vertex.DataGeneration(),
            .allocation_generation = resolved_allocation });
    }

    if (! submesh.index_arrays.empty()) {
        const auto& index = submesh.index_arrays[0];
        keys.push_back(DrawBufferKey { .render_item           = request.render_item,
                                       .role                  = DrawBufferRole::Index,
                                       .submesh_index         = request.submesh_index,
                                       .stream_index          = u32(),
                                       .data_generation       = index.DataGeneration(),
                                       .allocation_generation = resolved_allocation });
    }

    return keys;
}

Option<DrawBufferRefs> RenderBufferResolver::prepareDrawBuffers(const DrawBufferRequest& request) {
    if (request.mesh == nullptr) return None();
    SceneMesh&        mesh                 = *request.mesh;
    const auto        submesh_index        = request.submesh_index;
    const std::size_t native_submesh_index = submesh_index.to_primitive();
    if (mesh.Submeshes().empty() || native_submesh_index >= mesh.Submeshes().size()) return None();

    const auto& submesh    = mesh.Submeshes()[native_submesh_index];
    const bool  has_filter = ! request.active_vertex_streams.is_empty();
    const std::size_t stream_count =
        has_filter ? request.active_vertex_streams.len().to_primitive()
                   : submesh.vertex_arrays.size();

    DrawBufferRefs out;
    out.render_item           = request.render_item;
    out.dynamic               = mesh.Dynamic();
    out.allocation_generation = resolve_allocation_generation(request, out.dynamic);
    auto keys                 = BuildDrawBufferKeys(request, out.allocation_generation);
    out.vertex_keys.reserve(stream_count);
    out.vertex_sources.reserve(stream_count);

    for (std::size_t slot = 0; slot < stream_count; slot++) {
        const std::size_t stream_idx =
            has_filter ? request.active_vertex_streams[usize(slot)].to_primitive() : slot;
        if (stream_idx >= submesh.vertex_arrays.size()) return None();
        const auto& vertex = submesh.vertex_arrays[stream_idx];
        out.vertex_keys.push_back(keys[slot]);
        // Preparing a reused dynamic allocation does not upload its contents.
        // The first update must queue the current CPU data before it can be skipped.
        out.vertex_sources.push_back(nullptr);
        if (slot == 0) {
            out.draw_count = rstd::as_cast<u32>(vertex.DataSize() / vertex.OneSize());
        }

        if (slot >= request.buffer_uses.len().to_primitive()) return None();
        auto prepared = m_resources->Resolve(request.buffer_uses[usize(slot)]);
        if (prepared.is_none()) return None();
        out.vertices.push(resource::BufferUseHandle(request.buffer_uses[usize(slot)]));
    }

    if (! submesh.index_arrays.empty()) {
        const auto& index = submesh.index_arrays[0];
        out.draw_count    = rstd::as_cast<u32>(index.RenderDataCount());
        out.index_key     = Some<DrawBufferKey>(keys.back());

        auto use_index = stream_count;
        if (use_index >= request.buffer_uses.len().to_primitive()) return None();
        auto prepared = m_resources->Resolve(request.buffer_uses[usize(use_index)]);
        if (prepared.is_none()) return None();
        out.index = Some(resource::BufferUseHandle(request.buffer_uses[usize(use_index)]));
    }

    return Some(rstd::move(out));
}

bool RenderBufferResolver::updateDynamicDrawBuffers(
    const DrawBufferRequest& request, DrawBufferRefs& buffers,
    resource::BufferContentWriter* writer) {
    if (! buffers.dynamic) return true;
    if (request.mesh == nullptr) return false;
    SceneMesh&        mesh                 = *request.mesh;
    const auto        submesh_index        = request.submesh_index;
    const std::size_t native_submesh_index = submesh_index.to_primitive();
    if (native_submesh_index >= mesh.Submeshes().size()) return true;

    const auto& submesh    = mesh.Submeshes()[native_submesh_index];
    const bool  has_filter = ! request.active_vertex_streams.is_empty();
    const std::size_t stream_count =
        has_filter ? request.active_vertex_streams.len().to_primitive()
                   : submesh.vertex_arrays.size();
    if (! submesh.index_arrays.empty()) {
        buffers.draw_count = rstd::as_cast<u32>(submesh.index_arrays[0].RenderDataCount());
    } else if (! submesh.vertex_arrays.empty()) {
        buffers.draw_count = rstd::as_cast<u32>(submesh.vertex_arrays[0].VertexCount());
    } else {
        buffers.draw_count = u32();
    }
    auto require_reprepare = [&mesh] {
        mesh.SetLayoutDirty();
        return false;
    };
    if (buffers.vertices.len().to_primitive() != stream_count)
        return require_reprepare();
    if (buffers.vertex_keys.size() != stream_count) return require_reprepare();
    if (buffers.index.is_some() != ! submesh.index_arrays.empty()) return require_reprepare();
    if (buffers.vertex_sources.size() != stream_count)
        buffers.vertex_sources.assign(stream_count, nullptr);

    for (std::size_t slot = 0; slot < stream_count; slot++) {
        const std::size_t stream_idx =
            has_filter ? request.active_vertex_streams[usize(slot)].to_primitive() : slot;
        if (stream_idx >= submesh.vertex_arrays.size()) return require_reprepare();
        const auto& vertex = submesh.vertex_arrays[stream_idx];
        const auto generation = vertex.DataGeneration();
        if (buffers.vertex_sources[slot] == &vertex &&
            buffers.vertex_keys[slot].data_generation == generation) continue;
        auto updated = writer->UpdateBuffer(buffers.vertices[usize(slot)],
                                            bytesOf(vertex.Data(), vertex.DataSizeOf()));
        if (updated.is_err()) return require_reprepare();
        buffers.vertex_keys[slot].data_generation = generation;
        buffers.vertex_sources[slot] = &vertex;
    }

    if (! submesh.index_arrays.empty()) {
        const auto& index = submesh.index_arrays[0];
        const auto generation = index.DataGeneration();
        if (buffers.index_source != &index || buffers.index_key.is_none() ||
            buffers.index_key->data_generation != generation) {
            auto updated =
                writer->UpdateBuffer(*buffers.index, bytesOf(index.Data(), index.DataSizeOf()));
            if (updated.is_err()) return require_reprepare();
            if (buffers.index_key.is_some()) buffers.index_key->data_generation = generation;
            buffers.index_source = &index;
        }
    }

    return true;
}

} // namespace owe::vulkan
