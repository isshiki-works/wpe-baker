#include <rstd/test/gtest.hpp>

import rstd;
import rstd.cppstd;
import wescene.resource;
import wescene.rgraph;
import wescene.scene;
import wescene.vulkan_render;

using namespace rstd::literals;
using namespace rstd::prelude;
using rstd::sync::Arc;

namespace
{

auto MakePassNode(std::string name) -> Arc<owe::SceneNode> {
    auto node = Arc<owe::SceneNode>::make();
    auto mesh = std::make_shared<owe::SceneMesh>();
    owe::SceneMaterial material;
    material.name = std::move(name);
    mesh->AddMaterial(std::move(material));
    mesh->Submeshes().push_back(owe::SceneMesh::Submesh {});
    node->AddMesh(std::move(mesh));
    return node;
}

void RegisterTarget(owe::Scene& scene, std::string_view name) {
    scene.RegisterRenderTarget(
        String::make(rstd::cppstd::as_str(name).unwrap()),
        owe::SceneRenderTarget { .width = i32(64), .height = i32(64), .allowReuse = true });
}

struct CopyEdge {
    std::string source;
    std::string destination;
};

bool HasEdge(const std::vector<CopyEdge>& edges, std::string_view source,
             std::string_view destination) {
    for (const auto& edge : edges) {
        if (edge.source == source && edge.destination == destination) return true;
    }
    return false;
}

} // namespace

TEST(EffectCommands, FlushesMultipleTailSwapsBidirectionally) {
    owe::Scene scene;
    for (auto name : { "_rt_default", "_rt_effect_commands", "_rt_swap_a", "_rt_swap_b",
                       "_rt_swap_c", "_rt_swap_d" }) {
        RegisterTarget(scene, name);
    }

    scene.RootMut()->AddMesh(MakePassNode("source")->MeshShared());
    auto layer = std::make_shared<owe::SceneNodeLayer>(
        scene.RootMut().as_raw_ptr(), 64.0f, 64.0f, "_rt_effect_commands");
    auto effect = std::make_shared<owe::SceneImageEffect>();
    effect->name = "two-tail-swaps";
    effect->nodes.push_back(owe::SceneImageEffectNode {
        .output    = owe::SceneEffectTarget::LayerNext(),
        .sceneNode = MakePassNode("effect-pass"),
    });
    effect->commands.push_back({ .cmd = owe::SceneImageEffect::CmdType::Swap,
                                 .dst = owe::SceneEffectTarget::Named("_rt_swap_b"),
                                 .src = owe::SceneEffectTarget::Named("_rt_swap_a"),
                                 .afterpos = i32(1) });
    effect->commands.push_back({ .cmd = owe::SceneImageEffect::CmdType::Swap,
                                 .dst = owe::SceneEffectTarget::Named("_rt_swap_d"),
                                 .src = owe::SceneEffectTarget::Named("_rt_swap_c"),
                                 .afterpos = i32(1) });
    layer->AddEffect(effect);
    scene.RootMut()->AttachLayer(layer);

    auto snapshot = owe::ExtractRenderSceneSnapshot(scene);
    auto graph    = owe::sceneToRenderGraph(scene, snapshot);
    auto ordered  = graph->topologicalOrder();
    ASSERT_TRUE(ordered.is_ok());

    std::vector<CopyEdge> edges;
    for (auto handle : ordered.unwrap_unchecked()) {
        auto state = graph->passState(handle);
        ASSERT_TRUE(state.is_some());
        if (state->type != owe::rg::PassNode::Type::Copy) continue;
        auto pass = graph->getPass(state->pass);
        ASSERT_TRUE(pass.is_some());
        auto diagnostics =
            static_cast<owe::vulkan::VulkanPass&>(*pass).textureRequestDiagnostics();
        ASSERT_EQ(diagnostics.size(), 2u);
        ASSERT_TRUE(diagnostics[0].use.is_some());
        ASSERT_TRUE(diagnostics[1].use.is_some());
        edges.push_back({ .source      = diagnostics[0].name,
                          .destination = diagnostics[1].name });
    }

    ASSERT_EQ(edges.size(), 6u);
    EXPECT_TRUE(HasEdge(edges, "_rt_swap_a", "_rt_swap_a_0_copy"));
    EXPECT_TRUE(HasEdge(edges, "_rt_swap_b", "_rt_swap_a"));
    EXPECT_TRUE(HasEdge(edges, "_rt_swap_a_0_copy", "_rt_swap_b"));
    EXPECT_TRUE(HasEdge(edges, "_rt_swap_c", "_rt_swap_c_0_copy"));
    EXPECT_TRUE(HasEdge(edges, "_rt_swap_d", "_rt_swap_c"));
    EXPECT_TRUE(HasEdge(edges, "_rt_swap_c_0_copy", "_rt_swap_d"));

    auto plan = graph->resourcePlan();
    for (auto target : { "_rt_swap_a", "_rt_swap_b", "_rt_swap_c", "_rt_swap_d" }) {
        bool retained { false };
        for (const auto& texture : plan.textures) {
            if (texture.request.name != rstd::cppstd::as_str(target).unwrap()) continue;
            retained = true;
            EXPECT_EQ(texture.request.lifetime,
                      owe::resource::TextureLifetimeClass::Retained);
            EXPECT_NE(texture.request.content &
                          owe::resource::TextureContentFlag(
                              owe::resource::TextureContent::PreserveAcrossFrames),
                      u32());
        }
        EXPECT_TRUE(retained);
    }
}
