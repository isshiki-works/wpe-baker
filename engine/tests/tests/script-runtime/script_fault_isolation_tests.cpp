#include <gtest/gtest.h>

import eigen;
import rstd;
import rstd.cppstd;
import wescene.core;
import wescene.json;
import wescene.scene;
import wescene.script;
import wescene.testing.json_builder;

using namespace rstd::prelude;
using namespace rstd::literals;
using rstd::sync::Arc;
using namespace owe::script;

TEST(ScriptFaultIsolation, UpdateFaultKeepsLastValueAndOtherBindingsRunning) {
    owe::OfflineExecutionContext offline;
    owe::OfflineExecutionScope   scope(offline);
    JsRuntime                    runtime;

    auto faulty_owner = Arc<owe::SceneNode>::make(Eigen::Vector3f::Zero(),
                                                   Eigen::Vector3f::Ones(),
                                                   Eigen::Vector3f::Zero(),
                                                   "FAULTY_A");
    faulty_owner->SetGeneratorIdentity(
        Some(owe::WallpaperLayerId { .value = rstd::i32(701) }));
    auto healthy_owner = Arc<owe::SceneNode>::make(Eigen::Vector3f::Zero(),
                                                    Eigen::Vector3f::Ones(),
                                                    Eigen::Vector3f::Zero(),
                                                    "HEALTHY_B");
    healthy_owner->SetGeneratorIdentity(
        Some(owe::WallpaperLayerId { .value = rstd::i32(702) }));

    auto* faulty = runtime.MakeFieldScript(
        R"JS(
            let frame = 0;
            export function update(value) {
                frame++;
                if (frame === 1) return 'FIRST';
                if (frame === 2) throw new TypeError('WPE_NATIVE_UPDATE_FAULT_TOKEN');
                return 'RECOVERED_PREVIOUS=' + String(value);
            }
        )JS",
        "test/update_fault_isolation",
        FieldKind::String,
        owe::MakeObject(),
        owe::IntoJson("INITIAL"),
        ScriptBindingContext::ForLayer(faulty_owner.as_ptr(), "text"_str));
    auto* healthy = runtime.MakeFieldScript(
        R"JS(
            let frame = 0;
            export function update() { return ++frame; }
        )JS",
        "test/update_fault_healthy_peer",
        FieldKind::Scalar,
        owe::MakeObject(),
        owe::IntoJson(0),
        ScriptBindingContext::ForLayer(healthy_owner.as_ptr(), "text"_str));
    ASSERT_NE(faulty, nullptr);
    ASSERT_NE(healthy, nullptr);

    for (int frame = 0; frame < 10; ++frame) runtime.TickAll();

    ASSERT_TRUE(std::holds_alternative<StringValue>(faulty->last_value()));
    EXPECT_EQ(std::get<StringValue>(faulty->last_value()).s, "FIRST");
    ASSERT_TRUE(std::holds_alternative<ScalarValue>(healthy->last_value()));
    EXPECT_DOUBLE_EQ(std::get<ScalarValue>(healthy->last_value()).v, 10.0);
    EXPECT_FALSE(offline.failed);
    ASSERT_EQ(offline.source_script_errors.size(), 1u);

    const auto& error = offline.source_script_errors.front();
    EXPECT_EQ(error.binding_id, 0u);
    EXPECT_EQ(error.owner_layer_id, 701);
    EXPECT_EQ(error.owner_name, "FAULTY_A");
    EXPECT_EQ(error.property, "text");
    EXPECT_EQ(error.phase, "update");
    EXPECT_EQ(error.script_sha, "test/update_fault_isolation");
    EXPECT_NE(error.message.find("TypeError: WPE_NATIVE_UPDATE_FAULT_TOKEN"), std::string::npos);
    EXPECT_NE(error.stack.find("test/update_fault_isolation"), std::string::npos);
    bool found_fault_diagnostic = false;
    for (const auto& diagnostic : offline.diagnostics)
        found_fault_diagnostic = found_fault_diagnostic ||
                                 diagnostic.find("WPE_NATIVE_UPDATE_FAULT_TOKEN") !=
                                     std::string::npos;
    EXPECT_TRUE(found_fault_diagnostic);

    runtime.TickAll();
    EXPECT_EQ(std::get<StringValue>(faulty->last_value()).s, "FIRST");
    EXPECT_DOUBLE_EQ(std::get<ScalarValue>(healthy->last_value()).v, 11.0);
    EXPECT_EQ(offline.source_script_errors.size(), 1u);
    EXPECT_FALSE(offline.failed);
}

TEST(ScriptFaultIsolation, InitFaultRecordsAndStillRunsUpdates) {
    owe::OfflineExecutionContext offline;
    owe::OfflineExecutionScope   scope(offline);
    JsRuntime                    runtime;
    auto root = Arc<owe::SceneNode>::make();
    auto faulty = Arc<owe::SceneNode>::make(Eigen::Vector3f::Zero(),
                                            Eigen::Vector3f::Ones(),
                                            Eigen::Vector3f::Zero(),
                                            "FAULTY_INIT");
    auto healthy = Arc<owe::SceneNode>::make(Eigen::Vector3f::Zero(),
                                             Eigen::Vector3f::Ones(),
                                             Eigen::Vector3f::Zero(),
                                             "HEALTHY_INIT");
    faulty->SetGeneratorIdentity(Some(owe::WallpaperLayerId { .value = rstd::i32(701) }));
    healthy->SetGeneratorIdentity(Some(owe::WallpaperLayerId { .value = rstd::i32(702) }));
    root->AppendChild(faulty.clone());
    root->AppendChild(healthy.clone());

    auto* failed = runtime.MakeFieldScript(
        R"JS(
            export function init() { throw new TypeError('WPE_NATIVE_INIT_FAULT_TOKEN'); }
            export function update() { return 0.25; }
        )JS",
        "test/init_fault_isolation",
        FieldKind::Scalar,
        owe::MakeObject(),
        owe::IntoJson(0),
        ScriptBindingContext::ForLayer(faulty.as_ptr(), "alpha"_str));
    auto* peer = runtime.MakeFieldScript(
        R"JS(
            let frames = 0;
            export function init() {}
            export function update() { return ++frames; }
        )JS",
        "test/init_fault_healthy_peer",
        FieldKind::Scalar,
        owe::MakeObject(),
        owe::IntoJson(0),
        ScriptBindingContext::ForLayer(healthy.as_ptr(), "text"_str));
    ASSERT_NE(failed, nullptr);
    ASSERT_NE(peer, nullptr);

    runtime.SetSceneRoot(root.as_ptr());
    runtime.TickAll();

    EXPECT_DOUBLE_EQ(std::get<ScalarValue>(failed->last_value()).v, 0.25);
    EXPECT_DOUBLE_EQ(std::get<ScalarValue>(peer->last_value()).v, 1.0);
    EXPECT_FALSE(offline.failed);
    ASSERT_EQ(offline.source_script_errors.size(), 1u);
    const auto& error = offline.source_script_errors.front();
    EXPECT_EQ(error.owner_layer_id, 701);
    EXPECT_EQ(error.property, "alpha");
    EXPECT_EQ(error.phase, "init");
    EXPECT_EQ(error.script_sha, "test/init_fault_isolation");
    EXPECT_NE(error.message.find("WPE_NATIVE_INIT_FAULT_TOKEN"), std::string::npos);
    EXPECT_NE(error.stack.find("test/init_fault_isolation"), std::string::npos);

    runtime.SetSceneRoot(root.as_ptr());
    runtime.TickAll();
    EXPECT_DOUBLE_EQ(std::get<ScalarValue>(failed->last_value()).v, 0.25);
    EXPECT_DOUBLE_EQ(std::get<ScalarValue>(peer->last_value()).v, 2.0);
    EXPECT_EQ(offline.source_script_errors.size(), 1u);
}

TEST(ScriptFaultIsolation, ModuleFaultKeepsPeerRunningAndPreservesUnsupportedFatal) {
    owe::OfflineExecutionContext offline;
    owe::OfflineExecutionScope   scope(offline);
    JsRuntime                    runtime;
    auto root = Arc<owe::SceneNode>::make();
    auto faulty = Arc<owe::SceneNode>::make(Eigen::Vector3f::Zero(),
                                            Eigen::Vector3f::Ones(),
                                            Eigen::Vector3f::Zero(),
                                            "FAULTY_MODULE");
    auto healthy = Arc<owe::SceneNode>::make(Eigen::Vector3f::Zero(),
                                             Eigen::Vector3f::Ones(),
                                             Eigen::Vector3f::Zero(),
                                             "HEALTHY_MODULE");
    faulty->SetGeneratorIdentity(Some(owe::WallpaperLayerId { .value = rstd::i32(701) }));
    healthy->SetGeneratorIdentity(Some(owe::WallpaperLayerId { .value = rstd::i32(702) }));
    root->AppendChild(faulty.clone());
    root->AppendChild(healthy.clone());

    auto* failed = runtime.MakeFieldScript(
        "const x = scene.nonexistent; export function update() { return 1; }",
        "test/module_fault_isolation",
        FieldKind::Scalar,
        owe::MakeObject(),
        owe::IntoJson(0.75),
        ScriptBindingContext::ForLayer(faulty.as_ptr(), "alpha"_str));
    ASSERT_NE(failed, nullptr);
    auto* peer = runtime.MakeFieldScript(
        "export function update() { return 1; }",
        "test/module_fault_healthy_peer",
        FieldKind::Scalar,
        owe::MakeObject(),
        owe::IntoJson(0),
        ScriptBindingContext::ForLayer(healthy.as_ptr(), "text"_str));
    ASSERT_NE(peer, nullptr);
    runtime.SetSceneRoot(root.as_ptr());
    runtime.TickAll();

    ASSERT_TRUE(std::holds_alternative<ScalarValue>(failed->last_value()));
    EXPECT_DOUBLE_EQ(std::get<ScalarValue>(failed->last_value()).v, 0.75);
    EXPECT_DOUBLE_EQ(std::get<ScalarValue>(peer->last_value()).v, 1.0);
    EXPECT_FALSE(offline.failed);
    ASSERT_EQ(offline.source_script_errors.size(), 1u);
    EXPECT_EQ(offline.source_script_errors.front().phase, "module");
    EXPECT_EQ(offline.source_script_errors.front().owner_layer_id, 701);
    EXPECT_NE(offline.source_script_errors.front().message.find("scene is not defined"),
              std::string::npos);

    owe::OfflineExecutionContext unsupported_offline;
    owe::OfflineExecutionScope   unsupported_scope(unsupported_offline);
    JsRuntime                    unsupported_runtime;
    EXPECT_NE(unsupported_runtime.MakeFieldScript(
                  "engine.notImplementedOffline();",
                  "test/module_unsupported_fatal",
                  FieldKind::Scalar,
                  owe::MakeObject(),
                  owe::IntoJson(0.75),
                  ScriptBindingContext::ForLayer(faulty.as_ptr(), "alpha"_str)),
              nullptr);
    EXPECT_TRUE(unsupported_offline.failed);
}

TEST(ScriptFaultIsolation, CompileFaultPreservesInitialValueAndPeer) {
    owe::OfflineExecutionContext offline;
    owe::OfflineExecutionScope   scope(offline);
    JsRuntime                    runtime;
    auto root = Arc<owe::SceneNode>::make();
    auto faulty = Arc<owe::SceneNode>::make(Eigen::Vector3f::Zero(),
                                            Eigen::Vector3f::Ones(),
                                            Eigen::Vector3f::Zero(),
                                            "FAULTY_COMPILE");
    auto healthy = Arc<owe::SceneNode>::make(Eigen::Vector3f::Zero(),
                                             Eigen::Vector3f::Ones(),
                                             Eigen::Vector3f::Zero(),
                                             "HEALTHY_COMPILE");
    faulty->SetGeneratorIdentity(Some(owe::WallpaperLayerId { .value = rstd::i32(701) }));
    healthy->SetGeneratorIdentity(Some(owe::WallpaperLayerId { .value = rstd::i32(702) }));
    root->AppendChild(faulty.clone());
    root->AppendChild(healthy.clone());

    auto* failed = runtime.MakeFieldScript(
        "export function update( {",
        "test/compile_fault_isolation",
        FieldKind::Scalar,
        owe::MakeObject(),
        owe::IntoJson(0.75),
        ScriptBindingContext::ForLayer(faulty.as_ptr(), "alpha"_str));
    auto* peer = runtime.MakeFieldScript(
        "export function update() { return 1; }",
        "test/compile_fault_healthy_peer",
        FieldKind::Scalar,
        owe::MakeObject(),
        owe::IntoJson(0),
        ScriptBindingContext::ForLayer(healthy.as_ptr(), "text"_str));
    ASSERT_NE(failed, nullptr);
    ASSERT_NE(peer, nullptr);
    runtime.SetSceneRoot(root.as_ptr());
    runtime.TickAll();

    ASSERT_TRUE(std::holds_alternative<ScalarValue>(failed->last_value()));
    EXPECT_DOUBLE_EQ(std::get<ScalarValue>(failed->last_value()).v, 0.75);
    EXPECT_DOUBLE_EQ(std::get<ScalarValue>(peer->last_value()).v, 1.0);
    EXPECT_FALSE(offline.failed);
    ASSERT_EQ(offline.source_script_errors.size(), 1u);
    EXPECT_EQ(offline.source_script_errors.front().phase, "compile");
    EXPECT_EQ(offline.source_script_errors.front().owner_layer_id, 701);
    EXPECT_NE(offline.source_script_errors.front().message.find("SyntaxError"), std::string::npos);
}
