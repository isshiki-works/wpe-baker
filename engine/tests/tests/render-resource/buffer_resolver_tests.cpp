#include <gtest/gtest.h>

import rstd.cppstd;
import wescene.types;
import wescene.scene;
import wescene.vulkan;
import wescene.vulkan_render;
import wescene.resource_registry;

namespace
{

class BufferWriter final : public owe::resource::BufferContentWriter {
public:
    auto UpdateBuffer(owe::resource::BufferUseHandle, rstd::slice<rstd::u8>)
        -> rstd::Result<rstd::empty, owe::resource::ResourceError> override {
        ++update_count;
        if (fail_next) {
            fail_next = false;
            return rstd::Err(owe::resource::ResourceError {
                .kind = owe::resource::ResourceErrorKind::BackendFailure,
            });
        }
        return rstd::Ok(rstd::empty {});
    }

    rstd::u32 update_count {};
    bool      fail_next { false };
};

owe::SceneMesh::Submesh MakeSubmesh() {
    std::vector<owe::SceneVertexArray::SceneVertexAttribute> attrs {
        { .name = "a_Position", .type = owe::VertexType::FLOAT3 },
    };

    owe::SceneVertexArray vertices(attrs, rstd::usize(2));
    std::array<float, 6>  positions { 0.0f, 0.0f, 0.0f, 1.0f, 1.0f, 1.0f };
    (void)vertices.SetVertex(
        "a_Position",
        rstd::slice<float>::from_raw_parts(positions.data(), rstd::usize(positions.size())));

    owe::SceneIndexArray    indices(rstd::usize(3));
    std::array<uint32_t, 3> tri { 0, 1, 2 };
    indices.Assign(
        rstd::usize(),
        rstd::slice<rstd::uint32_t>::from_raw_parts(tri.data(), rstd::usize(tri.size())));

    owe::SceneMesh::Submesh submesh;
    submesh.vertex_arrays.push_back(std::move(vertices));
    submesh.index_arrays.push_back(std::move(indices));
    return submesh;
}

owe::vulkan::DrawBufferRefs MakeDynamicBuffers() {
    owe::vulkan::DrawBufferRefs buffers;
    buffers.dynamic = true;
    buffers.vertex_keys.push_back({});
    buffers.index_key = rstd::Some(owe::vulkan::DrawBufferKey {});
    buffers.vertices.push(
        owe::resource::BufferUseHandle { .index = rstd::u64(1), .generation = rstd::u64(1) });
    buffers.index = rstd::Some(
        owe::resource::BufferUseHandle { .index = rstd::u64(2), .generation = rstd::u64(1) });
    return buffers;
}

} // namespace

TEST(DrawBufferResourceName, UsesStableSceneDrawIdentity) {
    owe::SceneDrawItemId draw { .index = rstd::u32(7), .generation = rstd::u32(11) };

    auto vertex0 = owe::vulkan::BuildDrawBufferResourceName(
        draw, owe::vulkan::DrawBufferRole::Vertex, rstd::u32());
    auto vertex1 = owe::vulkan::BuildDrawBufferResourceName(
        draw, owe::vulkan::DrawBufferRole::Vertex, rstd::u32(1));
    auto index = owe::vulkan::BuildDrawBufferResourceName(draw, owe::vulkan::DrawBufferRole::Index);
    auto uniform =
        owe::vulkan::BuildDrawBufferResourceName(draw, owe::vulkan::DrawBufferRole::Uniform);

    EXPECT_EQ(rstd::cppstd::as_string_view(vertex0.as_str()), "draw:11:7:vertex:0");
    EXPECT_EQ(rstd::cppstd::as_string_view(vertex1.as_str()), "draw:11:7:vertex:1");
    EXPECT_EQ(rstd::cppstd::as_string_view(index.as_str()), "draw:11:7:index:0");
    EXPECT_EQ(rstd::cppstd::as_string_view(uniform.as_str()), "draw:11:7:uniform:0");
    EXPECT_TRUE(owe::vulkan::BuildDrawBufferResourceName({}, owe::vulkan::DrawBufferRole::Vertex)
                    .is_empty());
}

TEST(DrawBufferKey, BuildsStaticKeysFromRenderItemAndGeometryGeneration) {
    owe::SceneMesh mesh;
    mesh.Submeshes().push_back(MakeSubmesh());

    owe::RenderItemId render_item { .index = rstd::u32(7), .generation = rstd::u64(11) };
    owe::vulkan::DrawBufferRequest request { .render_item   = render_item,
                                             .mesh          = &mesh,
                                             .submesh_index = rstd::u32() };

    auto keys = owe::vulkan::BuildDrawBufferKeys(request, rstd::u64(99));
    ASSERT_EQ(keys.size(), 2u);

    const auto& vertex = mesh.Submeshes()[0].vertex_arrays[0];
    EXPECT_EQ(keys[0].render_item.index, render_item.index);
    EXPECT_EQ(keys[0].render_item.generation, render_item.generation);
    EXPECT_EQ(keys[0].role, owe::vulkan::DrawBufferRole::Vertex);
    EXPECT_EQ(keys[0].submesh_index, rstd::u32());
    EXPECT_EQ(keys[0].stream_index, rstd::u32());
    EXPECT_EQ(keys[0].data_generation, vertex.DataGeneration());
    EXPECT_EQ(keys[0].allocation_generation, rstd::u64());

    const auto& index = mesh.Submeshes()[0].index_arrays[0];
    EXPECT_EQ(keys[1].role, owe::vulkan::DrawBufferRole::Index);
    EXPECT_EQ(keys[1].data_generation, index.DataGeneration());
    EXPECT_EQ(keys[1].allocation_generation, rstd::u64());
}

TEST(DrawBufferKey, KeepsDynamicAllocationGenerationSeparateFromDataGeneration) {
    owe::SceneMesh mesh(true);
    mesh.Submeshes().push_back(MakeSubmesh());

    owe::RenderItemId render_item { .index = rstd::u32(3), .generation = rstd::u64(5) };
    owe::vulkan::DrawBufferRequest request { .render_item   = render_item,
                                             .mesh          = &mesh,
                                             .submesh_index = rstd::u32() };

    auto keys = owe::vulkan::BuildDrawBufferKeys(request, rstd::u64(77));
    ASSERT_EQ(keys.size(), 2u);
    EXPECT_EQ(keys[0].allocation_generation, rstd::u64(77));
    EXPECT_EQ(keys[1].allocation_generation, rstd::u64(77));
    EXPECT_EQ(keys[0].data_generation, mesh.Submeshes()[0].vertex_arrays[0].DataGeneration());
    EXPECT_EQ(keys[1].data_generation, mesh.Submeshes()[0].index_arrays[0].DataGeneration());
}

TEST(DrawBufferKey, ObservesCompletedVertexRewriteGeneration) {
    owe::SceneMesh mesh(true);
    mesh.Submeshes().push_back(MakeSubmesh());
    auto& vertices   = mesh.Submeshes()[0].vertex_arrays[0];
    auto  generation = vertices.DataGeneration();

    auto rewrite = vertices.RewriteVertices([](owe::SceneVertexWriter& writer) {
        auto vertex = writer.AppendZeroedVertex();
        ASSERT_TRUE(vertex.is_some());
        (*vertex)[rstd::usize()] = 3.0f;
    });
    ASSERT_FALSE(rewrite.overflowed);

    owe::vulkan::DrawBufferRequest request {
        .render_item   = { .index = rstd::u32(4), .generation = rstd::u64(6) },
        .mesh          = &mesh,
        .submesh_index = rstd::u32(),
    };
    auto keys = owe::vulkan::BuildDrawBufferKeys(request, rstd::u64(9));
    ASSERT_FALSE(keys.empty());
    EXPECT_EQ(vertices.DataGeneration(), generation + rstd::u64(1));
    EXPECT_EQ(keys[0].data_generation, vertices.DataGeneration());
    EXPECT_EQ(vertices.VertexCount(), rstd::usize(1));
}

TEST(DrawBufferKey, ReturnsEmptyForInvalidRequest) {
    owe::SceneMesh mesh;
    EXPECT_TRUE(owe::vulkan::BuildDrawBufferKeys({ .mesh = nullptr }, rstd::u64(1)).empty());
    EXPECT_TRUE(owe::vulkan::BuildDrawBufferKeys({ .mesh = &mesh, .submesh_index = rstd::u32(1) },
                                                 rstd::u64(1))
                    .empty());
}

TEST(DynamicDrawBuffer, PreparedCpuGenerationDoesNotProveUploadedContent) {
    owe::SceneMesh mesh(true);
    mesh.Submeshes().push_back(MakeSubmesh());
    const auto uploaded_generation = mesh.Submeshes()[0].vertex_arrays[0].DataGeneration();
    // Scripts change CPU geometry while warmup frames do not rasterize.
    std::array<float, 3> changed_position { 2.0f, 3.0f, 4.0f };
    ASSERT_TRUE(mesh.Submeshes()[0].vertex_arrays[0].SetVertexs(
        rstd::usize(), rstd::slice<float>::from_raw_parts(
                           changed_position.data(), rstd::usize(changed_position.size()))));
    (void)mesh.ConsumeDirtyFlags();
    std::array<owe::resource::BufferUseHandle, 2> uses {
        owe::resource::BufferUseHandle { .index = rstd::u64(1), .generation = rstd::u64(1) },
        owe::resource::BufferUseHandle { .index = rstd::u64(2), .generation = rstd::u64(1) },
    };
    owe::resource_registry::PreparedResourceTable table(rstd::u64(1));
    for (const auto& use : uses) {
        auto physical = rstd::sync::Arc<owe::resource_registry::BufferPhysical>::make(
            owe::vulkan::BufferAllocation {}, rstd::u64(1), rstd::u64(1));
        physical->source_generation = uploaded_generation;
        physical->submitted_generation = uploaded_generation;
        ASSERT_TRUE(table.Insert(owe::resource_registry::PreparedBufferUse {
            .use = use,
            .buffer = {
                .resource = { .index = use.index, .generation = use.generation },
                .physical = std::move(physical),
            },
        }));
    }
    owe::vulkan::DrawBufferRequest request {
        .mesh = &mesh,
        .buffer_uses = rstd::slice<owe::resource::BufferUseHandle>::from_raw_parts(
            uses.data(), rstd::usize(uses.size())),
    };
    owe::vulkan::RenderBufferResolver resolver(table);
    auto prepared = resolver.prepareDrawBuffers(request);
    ASSERT_TRUE(prepared.is_some());
    auto buffers = std::move(*prepared);
    EXPECT_EQ(buffers.vertex_keys[0].data_generation,
              mesh.Submeshes()[0].vertex_arrays[0].DataGeneration());
    EXPECT_EQ(buffers.vertex_sources[0], nullptr);
    EXPECT_EQ(buffers.index_source, nullptr);
    BufferWriter writer;
    EXPECT_TRUE(owe::vulkan::RenderBufferResolver::updateDynamicDrawBuffers(
        request, buffers, &writer));
    EXPECT_EQ(writer.update_count, rstd::u32(2));
    EXPECT_TRUE(owe::vulkan::RenderBufferResolver::updateDynamicDrawBuffers(
        request, buffers, &writer));
    EXPECT_EQ(writer.update_count, rstd::u32(2));

    // Re-preparing the same allocation invalidates upload knowledge too.
    auto refreshed = resolver.prepareDrawBuffers(request);
    ASSERT_TRUE(refreshed.is_some());
    EXPECT_TRUE(owe::vulkan::RenderBufferResolver::updateDynamicDrawBuffers(
        request, *refreshed, &writer));
    EXPECT_EQ(writer.update_count, rstd::u32(4));
}

TEST(DynamicDrawBuffer, UpdatesEveryViewOncePerChangedStream) {
    owe::SceneMesh mesh(true);
    mesh.Submeshes().push_back(MakeSubmesh());
    mesh.Submeshes()[0].index_arrays[0].SetRenderDataCount(rstd::usize(2));
    mesh.SetDirty();

    auto reflection_buffers = MakeDynamicBuffers();
    auto primary_buffers    = MakeDynamicBuffers();

    owe::vulkan::DrawBufferRequest request {
        .render_item   = { .index = rstd::u32(3), .generation = rstd::u64(5) },
        .mesh          = &mesh,
        .submesh_index = rstd::u32(),
    };
    BufferWriter writer;
    owe::resource::BufferContentWriter*         writer_trait = &writer;

    EXPECT_TRUE(owe::vulkan::RenderBufferResolver::updateDynamicDrawBuffers(
        request, reflection_buffers, writer_trait));
    EXPECT_TRUE(owe::vulkan::RenderBufferResolver::updateDynamicDrawBuffers(
        request, primary_buffers, writer_trait));
    EXPECT_EQ(reflection_buffers.draw_count, rstd::u32(2));
    EXPECT_EQ(primary_buffers.draw_count, rstd::u32(2));
    EXPECT_EQ(writer.update_count, rstd::u32(4));
    EXPECT_NE(mesh.DirtyFlags() & owe::SceneMeshDirtyData, owe::SceneMeshDirtyNone);

    // Repeated views and draw-count changes do not upload unchanged geometry.
    mesh.Submeshes()[0].index_arrays[0].SetRenderDataCount(rstd::usize(1));
    EXPECT_TRUE(owe::vulkan::RenderBufferResolver::updateDynamicDrawBuffers(
        request, reflection_buffers, writer_trait));
    EXPECT_TRUE(owe::vulkan::RenderBufferResolver::updateDynamicDrawBuffers(
        request, primary_buffers, writer_trait));
    EXPECT_EQ(writer.update_count, rstd::u32(4));
    EXPECT_EQ(primary_buffers.draw_count, rstd::u32(1));

    // One view may complete and clear mesh dirtiness before another updates.
    auto& vertices = mesh.Submeshes()[0].vertex_arrays[0];
    std::array<float, 3> changed_position { 2.0f, 3.0f, 4.0f };
    ASSERT_TRUE(vertices.SetVertexs(
        rstd::usize(), rstd::slice<float>::from_raw_parts(
                           changed_position.data(), rstd::usize(changed_position.size()))));
    EXPECT_TRUE(owe::vulkan::RenderBufferResolver::updateDynamicDrawBuffers(
        request, reflection_buffers, writer_trait));
    (void)mesh.ConsumeDirtyFlags(owe::SceneMeshDirtyData);
    EXPECT_TRUE(owe::vulkan::RenderBufferResolver::updateDynamicDrawBuffers(
        request, primary_buffers, writer_trait));
    EXPECT_EQ(writer.update_count, rstd::u32(6));

    std::array<uint32_t, 3> changed_indices { 2, 1, 0 };
    mesh.Submeshes()[0].index_arrays[0].Assign(
        rstd::usize(), rstd::slice<rstd::uint32_t>::from_raw_parts(
                           changed_indices.data(), rstd::usize(changed_indices.size())));
    EXPECT_TRUE(owe::vulkan::RenderBufferResolver::updateDynamicDrawBuffers(
        request, reflection_buffers, writer_trait));
    EXPECT_TRUE(owe::vulkan::RenderBufferResolver::updateDynamicDrawBuffers(
        request, primary_buffers, writer_trait));
    EXPECT_EQ(writer.update_count, rstd::u32(8));
}

TEST(DynamicDrawBuffer, UploadsReplacementGeometryAtTheSameGeneration) {
    owe::SceneMesh original(true), replacement(true);
    original.Submeshes().push_back(MakeSubmesh());
    replacement.Submeshes().push_back(MakeSubmesh());
    auto buffers = MakeDynamicBuffers();
    owe::vulkan::DrawBufferRequest request { .mesh = &original };
    BufferWriter writer;
    EXPECT_TRUE(owe::vulkan::RenderBufferResolver::updateDynamicDrawBuffers(
        request, buffers, &writer));
    EXPECT_EQ(writer.update_count, rstd::u32(2));

    ASSERT_EQ(original.Submeshes()[0].vertex_arrays[0].DataGeneration(),
              replacement.Submeshes()[0].vertex_arrays[0].DataGeneration());
    original.ChangeMeshDataFrom(replacement);
    EXPECT_TRUE(owe::vulkan::RenderBufferResolver::updateDynamicDrawBuffers(
        request, buffers, &writer));
    EXPECT_EQ(writer.update_count, rstd::u32(4));
    request.mesh = &replacement;
    EXPECT_TRUE(owe::vulkan::RenderBufferResolver::updateDynamicDrawBuffers(
        request, buffers, &writer));
    EXPECT_EQ(writer.update_count, rstd::u32(4));
}

TEST(DynamicDrawBuffer, FailedUploadDoesNotCommitTheSourceGeneration) {
    owe::SceneMesh mesh(true);
    mesh.Submeshes().push_back(MakeSubmesh());
    auto buffers = MakeDynamicBuffers();
    owe::vulkan::DrawBufferRequest request { .mesh = &mesh };
    BufferWriter writer;
    writer.fail_next = true;
    EXPECT_FALSE(owe::vulkan::RenderBufferResolver::updateDynamicDrawBuffers(
        request, buffers, &writer));
    EXPECT_EQ(buffers.vertex_keys[0].data_generation, rstd::u64());
    EXPECT_EQ(buffers.vertex_sources[0], nullptr);
    EXPECT_NE(mesh.DirtyFlags() & owe::SceneMeshDirtyLayout, owe::SceneMeshDirtyNone);
    EXPECT_TRUE(owe::vulkan::RenderBufferResolver::updateDynamicDrawBuffers(
        request, buffers, &writer));
    EXPECT_EQ(writer.update_count, rstd::u32(3));
}
