#include <gtest/gtest.h>

import rstd.cppstd;
import wescene.types;
import wescene.scene;
import wescene.vulkan_render;

namespace
{

class BufferWriter {
public:
    auto UpdateBuffer(owe::resource::BufferUseHandle, rstd::slice<rstd::u8>)
        -> rstd::Result<rstd::empty, owe::resource::ResourceError> {
        ++update_count;
        return rstd::Ok(rstd::empty {});
    }

    rstd::u32 update_count {};
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

TEST(DynamicDrawBuffer, UpdatesEveryViewBeforeDirtyDataIsConsumed) {
    owe::SceneMesh mesh(true);
    mesh.Submeshes().push_back(MakeSubmesh());
    mesh.Submeshes()[0].index_arrays[0].SetRenderDataCount(rstd::usize(2));
    mesh.SetDirty();

    auto make_buffers = [] {
        owe::vulkan::DrawBufferRefs buffers;
        buffers.dynamic = true;
        buffers.vertex_keys.push_back({});
        buffers.index_key = rstd::Some(owe::vulkan::DrawBufferKey {});
        buffers.vertices.push(
            owe::resource::BufferUseHandle { .index = rstd::u64(1), .generation = rstd::u64(1) });
        buffers.index = rstd::Some(
            owe::resource::BufferUseHandle { .index = rstd::u64(2), .generation = rstd::u64(1) });
        return buffers;
    };
    auto reflection_buffers = make_buffers();
    auto primary_buffers    = make_buffers();

    owe::vulkan::DrawBufferRequest request {
        .render_item   = { .index = rstd::u32(3), .generation = rstd::u64(5) },
        .mesh          = &mesh,
        .submesh_index = rstd::u32(),
    };
    BufferWriter writer;
    auto         writer_trait = rstd::dyn<owe::resource::BufferContentWriter>::from_ref(writer);

    EXPECT_TRUE(owe::vulkan::RenderBufferResolver::updateDynamicDrawBuffers(
        request, reflection_buffers, writer_trait.as_mut_ref()));
    EXPECT_TRUE(owe::vulkan::RenderBufferResolver::updateDynamicDrawBuffers(
        request, primary_buffers, writer_trait.as_mut_ref()));
    EXPECT_EQ(reflection_buffers.draw_count, rstd::u32(2));
    EXPECT_EQ(primary_buffers.draw_count, rstd::u32(2));
    EXPECT_EQ(writer.update_count, rstd::u32(4));
    EXPECT_NE(mesh.DirtyFlags() & owe::SceneMeshDirtyData, owe::SceneMeshDirtyNone);
}
