#include <gtest/gtest.h>

#include <memory>

#include <new> // wescene.json 的全局模块片段带进 <new>，这里显式包含，免得与隐式 operator new 冲突
#include "JsonNlohmann.hpp"

import rstd;
import wescene.json;
import owe.user_property;
import wescene.scene;
import wescene.scene_user_property;

using namespace rstd::prelude;
using namespace rstd::literals;
using rstd::sync::Arc;

namespace
{

struct FakeParticleOverrideControl final : owe::SceneParticleOverrideControl {
    explicit FakeParticleOverrideControl(float* target): observed(target) {}

    float* observed;

    void Apply(slice<float> values) override {
        if (! values.is_empty()) *observed = values[usize()];
    }
};

} // namespace

TEST(SceneUserProperty, CanonicalizesHostSchemeColor) {
    EXPECT_EQ(owe::CanonicalSceneUserPropertyKey("waywallen.scheme_color"), "schemecolor");
    EXPECT_EQ(owe::CanonicalSceneUserPropertyKey("custom"), "custom");
}

TEST(SceneUserProperty, ResolvesClampedColorDescriptor) {
    auto property = owe::ParseNJson(R"({"type":"color","value":"-0.2 0.4 1.4"})").unwrap();
    auto color    = owe::ResolveSceneUserPropertyColor(property);

    ASSERT_TRUE(color.is_some());
    EXPECT_FLOAT_EQ((*color)[usize()], 0.0f);
    EXPECT_FLOAT_EQ((*color)[usize(1)], 0.4f);
    EXPECT_FLOAT_EQ((*color)[usize(2)], 1.0f);
}

TEST(SceneUserProperty, ResolvesResetWireValueFromExistingDescriptor) {
    auto schema = owe::ParseNJson(R"({"type":"color","value":"0.2 0.4 0.6"})").unwrap();
    auto overridden =
        owe::MergeUserPropertyDescriptor(schema, owe::MakeUserPropertyWirePatch("0.8 0.7 0.6"));
    auto reset =
        owe::MergeUserPropertyDescriptor(overridden, owe::MakeUserPropertyWirePatch("0.2 0.4 0.6"));
    auto color = owe::ResolveSceneUserPropertyColor(reset);

    ASSERT_TRUE(color.is_some());
    EXPECT_FLOAT_EQ((*color)[usize()], 0.2f);
    EXPECT_FLOAT_EQ((*color)[usize(1)], 0.4f);
    EXPECT_FLOAT_EQ((*color)[usize(2)], 0.6f);
}

TEST(SceneUserProperty, BatchUsesSinglePropertySemantics) {
    owe::Scene single;
    owe::Scene batch;
    single.SetClearColorUserKey(String::make("schemecolor"_str));
    batch.SetClearColorUserKey(String::make("schemecolor"_str));

    auto property = owe::ParseNJson(R"({"type":"color","value":"0.2 0.4 0.6"})").unwrap();
    auto properties =
        owe::ParseNJson(
            R"({"waywallen.scheme_color":{"type":"color","value":"0.2 0.4 0.6"}})")
            .unwrap();
    ASSERT_TRUE(properties.is_object());

    auto single_mutation =
        owe::SceneUserPropertyApplier::Apply(single, "waywallen.scheme_color", property);
    auto batch_mutation = owe::SceneUserPropertyApplier::ApplyAll(batch, properties);

    EXPECT_FALSE(single_mutation.graph_changed);
    EXPECT_FALSE(batch_mutation.graph_changed);
    ASSERT_TRUE(single_mutation.clear_color.is_some());
    ASSERT_TRUE(batch_mutation.clear_color.is_some());
    EXPECT_TRUE(single_mutation.texture_materials.is_empty());
    EXPECT_TRUE(batch_mutation.texture_materials.is_empty());
    auto single_color = single.ClearColor();
    auto batch_color  = batch.ClearColor();
    for (usize index {}; index < usize(3); ++index) {
        EXPECT_FLOAT_EQ(single_color[index], batch_color[index]);
    }
    EXPECT_FLOAT_EQ(batch_color[usize()], 0.2f);
    EXPECT_FLOAT_EQ(batch_color[usize(1)], 0.4f);
    EXPECT_FLOAT_EQ(batch_color[usize(2)], 0.6f);
}

TEST(SceneUserProperty, AppliesRegisteredShaderUniformBinding) {
    owe::Scene scene;
    auto       material = std::make_shared<owe::SceneMaterial>();
    scene.RegisterShaderUserBinding(
        String::make("brightness"_str), material, String::make("u_Brightness"_str));

    auto property = owe::ParseNJson(R"({"type":"slider","value":0.5})").unwrap();
    owe::SceneUserPropertyApplier::Apply(scene, "brightness", property);

    auto bindings = scene.ShaderUserBindings("brightness"_str);
    ASSERT_EQ(bindings.len(), usize(1));
    EXPECT_EQ(bindings[usize()].material, material);
    EXPECT_EQ(bindings[usize()].uniform, "u_Brightness"_str);
    EXPECT_TRUE(material->customShader.constValues.contains("u_Brightness"));
}

TEST(SceneUserProperty, ReportsRegisteredShaderComboWithoutVfs) {
    owe::Scene                         scene;
    auto                               material = std::make_shared<owe::SceneMaterial>();
    owe::Scene::ShaderComboUserBinding binding {
        .material = material,
        .combo    = String::make("QUALITY"_str),
        .fallback = String::make("0"_str),
    };
    (void)binding.options.insert(String::make("high"_str), String::make("2"_str));
    scene.RegisterShaderComboUserBinding(String::make("quality"_str), rstd::move(binding));

    auto property = owe::ParseNJson(R"({"type":"combo","value":"high"})").unwrap();
    auto mutation = owe::SceneUserPropertyApplier::Apply(scene, "quality", property);

    EXPECT_TRUE(mutation.diagnostics_changed);
    auto bindings = scene.ShaderComboUserBindings("quality"_str);
    ASSERT_EQ(bindings.len(), usize(1));
    EXPECT_EQ(bindings[usize()].combo, "QUALITY"_str);
    EXPECT_EQ(**bindings[usize()].options.get("high"_str), "2"_str);
    auto diagnostics = owe::CollectSceneUserPropertyDiagnostics(scene, "quality");
    ASSERT_EQ(diagnostics.len(), usize(1));
    EXPECT_EQ(diagnostics[usize()].code, owe::SceneUserPropertyDiagnosticCode::SceneVfsUnavailable);
}

TEST(SceneUserProperty, AppliesRegisteredMaterialTextureBinding) {
    owe::Scene scene;
    auto       material = std::make_shared<owe::SceneMaterial>();
    scene.RegisterTexture(String::make("tex/new"_str), owe::SceneTexture { .url = "tex/new" });
    material->textures.push_back("tex/old");
    scene.RegisterMaterialTextureUserBinding(String::make("cover"_str),
                                             owe::Scene::MaterialTextureUserBinding {
                                                 .material = material,
                                                 .slot     = rstd::u32(),
                                                 .fallback = String::make("tex/fallback"_str),
                                             });

    auto property = owe::ParseNJson(R"({"type":"texture","value":"tex/new"})").unwrap();
    (void)owe::SceneUserPropertyApplier::ApplyTexture(scene, "cover", property);

    auto bindings = scene.MaterialTextureUserBindings("cover"_str);
    ASSERT_EQ(bindings.len(), usize(1));
    EXPECT_EQ(bindings[usize()].fallback, "tex/fallback"_str);
    EXPECT_EQ(material->textures[0], "tex/new");
}

TEST(SceneUserProperty, AppliesRegisteredImageColorBinding) {
    owe::Scene scene;
    auto       node     = Arc<owe::SceneNode>::make();
    auto       material = std::make_shared<owe::SceneMaterial>();
    rstd::array<std::shared_ptr<owe::SceneMaterial>, 1> materials { material };
    scene.RegisterImageColorUserBinding(String::make("tint"_str), node, materials.as_slice());

    auto property = owe::ParseNJson(R"({"type":"color","value":"0.2 0.4 0.6"})").unwrap();
    (void)owe::SceneUserPropertyApplier::Apply(scene, "tint", property);

    auto bindings = scene.ImageColorUserBindings("tint"_str);
    ASSERT_EQ(bindings.len(), usize(1));
    ASSERT_EQ(bindings[usize()].materials.len(), usize(1));
    EXPECT_EQ(bindings[usize()].materials[usize()], material);
    EXPECT_FLOAT_EQ(node->Color().x(), 0.2f);
    EXPECT_FLOAT_EQ(node->Color().y(), 0.4f);
    EXPECT_FLOAT_EQ(node->Color().z(), 0.6f);
}

// A parsed object that never reaches the finalized scene (a spawn-template
// prototype, say) is released with the parse context, but its image bindings
// stay in the scene-wide index and are applied right after the parse. The
// index has to keep those targets alive on its own.
TEST(SceneUserProperty, KeepsImageColorBindingTargetsAliveAfterParse) {
    owe::Scene scene;
    {
        auto node     = Arc<owe::SceneNode>::make();
        auto material = std::make_shared<owe::SceneMaterial>();
        rstd::array<std::shared_ptr<owe::SceneMaterial>, 1> materials { material };
        scene.RegisterImageColorUserBinding(String::make("tint"_str), node, materials.as_slice());
    }

    auto property = owe::ParseNJson(R"({"type":"color","value":"0.2 0.4 0.6"})").unwrap();
    (void)owe::SceneUserPropertyApplier::Apply(scene, "tint", property);

    auto bindings = scene.ImageColorUserBindings("tint"_str);
    ASSERT_EQ(bindings.len(), usize(1));
    ASSERT_EQ(bindings[usize()].materials.len(), usize(1));
    EXPECT_FLOAT_EQ(bindings[usize()].node->Color().x(), 0.2f);
    EXPECT_FLOAT_EQ(bindings[usize()].node->Color().y(), 0.4f);
    EXPECT_FLOAT_EQ(bindings[usize()].node->Color().z(), 0.6f);
}

TEST(SceneUserProperty, KeepsImageAlphaBindingTargetsAliveAfterParse) {
    owe::Scene scene;
    {
        auto node     = Arc<owe::SceneNode>::make();
        auto material = std::make_shared<owe::SceneMaterial>();
        rstd::array<std::shared_ptr<owe::SceneMaterial>, 1> materials { material };
        scene.RegisterImageAlphaUserBinding(String::make("fade"_str), node, materials.as_slice());
    }

    auto property = owe::ParseNJson(R"({"type":"slider","value":0.25})").unwrap();
    (void)owe::SceneUserPropertyApplier::Apply(scene, "fade", property);

    auto bindings = scene.ImageAlphaUserBindings("fade"_str);
    ASSERT_EQ(bindings.len(), usize(1));
    EXPECT_FLOAT_EQ(bindings[usize()].node->UserAlpha(), 0.25f);
}

TEST(SceneUserProperty, AppliesRegisteredParticleOverrideBinding) {
    owe::Scene scene;
    float      observed = 0.0f;
    scene.RegisterParticleOverrideBinding(
        String::make("rate"_str),
        std::shared_ptr<owe::SceneParticleOverrideControl>(
            std::make_shared<FakeParticleOverrideControl>(&observed)));

    auto property = owe::ParseNJson(R"({"type":"slider","value":2.5})").unwrap();
    (void)owe::SceneUserPropertyApplier::Apply(scene, "rate", property);

    EXPECT_FLOAT_EQ(observed, 2.5f);
    EXPECT_EQ(scene.ParticleOverrideBindings("rate"_str).len(), usize(1));
}

// 可见性条件比较保持原 rstd 语义：数字种类不同（1 与 1.0）不相等，nlohmann 的 == 会把它们判成相等。
TEST(SceneUserVisibility, ConditionEqualityKeepsNumberKinds) {
    EXPECT_TRUE(owe::SceneJsonScalarEquals(owe::NJson(1u), owe::NJson(1u)));
    EXPECT_FALSE(owe::SceneJsonScalarEquals(owe::NJson(1u), owe::NJson(1.0)));
    EXPECT_FALSE(owe::SceneJsonScalarEquals(owe::ParseNJson("[1]").unwrap(), owe::ParseNJson("[1.0]").unwrap()));
    EXPECT_TRUE(owe::SceneJsonScalarEquals(owe::NJson(true), owe::NJson("1")));
    EXPECT_TRUE(owe::SceneJsonScalarEquals(owe::NJson("0"), owe::NJson(false)));

    owe::SceneUserVisibilityBinding binding { .key = String::make("variant"_str) };
    binding.condition     = std::make_shared<const owe::NJson>(2u);
    binding.has_condition = true;
    auto hit  = owe::ResolveSceneUserVisibilityBinding(binding, owe::ParseNJson(R"({"value":2})").unwrap());
    auto miss = owe::ResolveSceneUserVisibilityBinding(binding, owe::ParseNJson(R"({"value":2.0})").unwrap());
    ASSERT_TRUE(hit.is_some());
    ASSERT_TRUE(miss.is_some());
    EXPECT_TRUE(*hit);
    EXPECT_FALSE(*miss);
}
