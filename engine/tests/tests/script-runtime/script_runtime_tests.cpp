#include <owe/compat.hpp>
#include <owe/std.hpp>
#include <gtest/gtest.h>

#include <new> // wescene.json 的全局模块片段带进 <new>，这里显式包含，免得与隐式 operator new 冲突
#include "JsonNlohmann.hpp"

import eigen;
import wescene.json;
import wescene.pkg.parse;
import wescene.types;
import wescene.scene;
import wescene.script;
import wescene.core;

using namespace owe::script;
using namespace rstd::prelude;
using namespace rstd::literals;
using rstd::sync::Arc;

namespace
{

// Build a one-shot FieldScript whose update() returns a host-visible counter.
// The module body schedules timers/intervals that mutate that counter, so we
// can observe the JsRuntime's deferred-callback sweep through FieldScript's
// last_value().
FieldScript* MakeProbe(JsRuntime& rt, const char* sha, const char* src) {
    return rt.MakeFieldScript(src,
                              sha,
                              FieldKind::Scalar,
                              /*properties_config=*/owe::NJson::object(),
                              /*initial_value=*/owe::NJson(0),
                              /*node=*/nullptr);
}

double Tick(JsRuntime& rt, double runtime) {
    FrameInputs fi {};
    fi.runtime = float(runtime);
    rt.SetFrameInputs(fi);
    rt.TickAll();
    // last_value() of the last MakeFieldScript'd script — caller must own a
    // pointer, but for this test the call site walks via ForEach.
    return 0.0;
}

double LastScalar(FieldScript* fs) {
    EXPECT_TRUE(std::holds_alternative<ScalarValue>(fs->last_value()));
    if (! std::holds_alternative<ScalarValue>(fs->last_value())) return 0.0;
    return std::get<ScalarValue>(fs->last_value()).v;
}

struct ParticleControlState {
    std::array<float, 3> colorn { 1.0f, 1.0f, 1.0f };
    bool                 playing { true };
    int                  resets { 0 };
};

struct ParticleControlProbe final : owe::SceneParticleControl {
    explicit ParticleControlProbe(Arc<ParticleControlState> probe_state)
        : state(rstd::move(probe_state)) {}

    Arc<ParticleControlState> state;

    Vec<float> Get(ref<str> field) const override {
        Vec<float> out;
        if (field != "colorn"_str) return out;
        for (float value : state->colorn) out.push(float(value));
        return out;
    }
    void Apply(ref<str> field, slice<float> values) override {
        if (field != "colorn"_str || values.len() < usize(3)) return;
        state->colorn = { values[usize()], values[usize(1)], values[usize(2)] };
    }
    void Play() override {
        state->playing = true;
        state->resets++;
    }
    void Stop() override {
        state->playing = false;
        state->resets++;
    }
    void Pause() override { state->playing = false; }
    bool IsPlaying() const override { return state->playing; }
};

struct SoundControlState {
    float volume { 1.0f };
    bool  playing { true };
};

struct SoundControlProbe final : owe::SceneSoundControl {
    explicit SoundControlProbe(Arc<SoundControlState> probe_state): state(rstd::move(probe_state)) {}

    Arc<SoundControlState> state;

    void Play() override { state->playing = true; }
    void Stop() override { state->playing = false; }
    void Pause() override { state->playing = false; }
    bool IsPlaying() const override { return state->playing; }
    void SetVolume(float volume) override { state->volume = volume; }
};

} // namespace

TEST(OfflineClock, DateNowMatchesDateTimeClipAtFractionalFrameTimes) {
    owe::OfflineExecutionContext clock;
    owe::OfflineExecutionScope scope(clock);
    JsRuntime rt;
    auto* probe = MakeProbe(rt, "test/offline_date_integer", R"JS(
        export function update() {
            const now = Date.now();
            if (!Number.isInteger(now) || now !== new Date().getTime())
                throw new Error('Date.now disagrees with Date TimeClip');
            if (new Date().getTimezoneOffset() !== 0)
                throw new Error('Offline Date leaked the host timezone');
            return now;
        }
    )JS");
    ASSERT_NE(probe, nullptr);
    clock.elapsed = 1.0 / 120.0;
    rt.TickAll();
    EXPECT_FALSE(clock.failed);
    EXPECT_EQ(LastScalar(probe), 946684800008.0);
    clock.epoch_ms = -1000.5;
    rt.TickAll();
    EXPECT_FALSE(clock.failed);
    EXPECT_EQ(LastScalar(probe), -992.0);
}

TEST(OfflineClock, HundredMillisecondIntervalFiresThirdTimeAtFrame36Of120Fps) {
    // IEngine documents delay in milliseconds; EngineSetTimerImpl divides by
    // 1000. https://docs.wallpaperengine.io/en/scene/scenescript/reference/class/IEngine.html
    owe::OfflineExecutionContext clock;
    owe::OfflineExecutionScope scope(clock);
    JsRuntime rt;
    auto* probe = MakeProbe(rt, "test/offline_interval_boundary", R"JS(
        let count = 0;
        engine.setInterval(() => { ++count; }, 100);
        export function update() { return count; }
    )JS");
    ASSERT_NE(probe, nullptr);
    auto tick_frame = [&](unsigned frame) {
        FrameInputs inputs;
        inputs.runtime = static_cast<double>(frame) / 120.0;
        inputs.frametime = 1.0f / 120.0f;
        clock.elapsed = inputs.runtime;
        rt.SetFrameInputs(inputs); // Preserve double; legacy Tick() casts to float.
        rt.TickAll();
    };
    for (unsigned frame = 0; frame <= 35; ++frame) tick_frame(frame);
    EXPECT_EQ(LastScalar(probe), 2.0);
    tick_frame(36);
    EXPECT_EQ(LastScalar(probe), 3.0);
    for (unsigned frame = 37; frame <= 1200; ++frame) {
        tick_frame(frame);
        EXPECT_EQ(LastScalar(probe), static_cast<double>(frame / 12));
    }
    EXPECT_FALSE(clock.failed);
}

TEST(ScriptInitialization, UsesSceneOwnerOrderInsteadOfRegistrationOrder) {
    auto root = Arc<owe::SceneNode>::make();

    JsRuntime rt;
    auto*     consumer = rt.MakeFieldScript(
        R"JS(
            let seen = -1;
            export function init() { seen = shared.ready; }
            export function update() { return seen; }
        )JS",
        "test/init_order_consumer",
        FieldKind::Scalar,
        owe::NJson::object(),
        owe::NJson(0));
    auto* producer = rt.MakeFieldScript(
        R"JS(
            export function init() { shared.ready = 7; }
            export function update() { return 0; }
        )JS",
        "test/init_order_producer",
        FieldKind::Scalar,
        owe::NJson::object(),
        owe::NJson(0));
    ASSERT_NE(consumer, nullptr);
    ASSERT_NE(producer, nullptr);

    rt.SetInitializationOrder(*consumer, 1);
    rt.SetInitializationOrder(*producer, 0);
    rt.SetSceneRoot(root.as_ptr());
    rt.TickAll();

    EXPECT_EQ(LastScalar(consumer), 7.0);
}

TEST(ScriptValueCoercion, PreservesVec4InitialAndReturnValues) {
    JsRuntime rt;
    auto*     script = rt.MakeFieldScript(
        R"JS(
            export function update(value) { return value.add(new Vec4(1, 2, 3, 4)); }
        )JS",
        "test/vec4_value",
        FieldKind::Vec4,
        owe::NJson::object(),
        owe::NJson("0.5 1.5 2.5 3.5"));
    ASSERT_NE(script, nullptr);

    rt.TickAll();

    ASSERT_TRUE(std::holds_alternative<Vec4Value>(script->last_value()));
    const auto& value = std::get<Vec4Value>(script->last_value());
    EXPECT_DOUBLE_EQ(value.x, 1.5);
    EXPECT_DOUBLE_EQ(value.y, 3.5);
    EXPECT_DOUBLE_EQ(value.z, 5.5);
    EXPECT_DOUBLE_EQ(value.w, 7.5);
}

TEST(ScriptTimer, SetTimeoutFiresAfterDelay) {
    JsRuntime   rt;
    FrameInputs fi {};
    rt.SetFrameInputs(fi);
    auto* fs = MakeProbe(rt,
                         "test/timer_fires",
                         R"JS(
        let fired = 0;
        setTimeout(() => { fired++; }, 100);
        export function update() { return fired; }
    )JS");
    ASSERT_NE(fs, nullptr);

    Tick(rt, 0.05);
    ASSERT_TRUE(std::holds_alternative<ScalarValue>(fs->last_value()));
    EXPECT_EQ(std::get<ScalarValue>(fs->last_value()).v, 0.0);

    Tick(rt, 0.15);
    EXPECT_EQ(std::get<ScalarValue>(fs->last_value()).v, 1.0);

    Tick(rt, 0.30);
    EXPECT_EQ(std::get<ScalarValue>(fs->last_value()).v, 1.0);
}

TEST(ScriptTimer, SetIntervalRepeats) {
    JsRuntime   rt;
    FrameInputs fi {};
    rt.SetFrameInputs(fi);
    auto* fs = MakeProbe(rt,
                         "test/interval_repeats",
                         R"JS(
        let n = 0;
        setInterval(() => { n++; }, 100);
        export function update() { return n; }
    )JS");
    ASSERT_NE(fs, nullptr);

    Tick(rt, 0.25);
    EXPECT_EQ(std::get<ScalarValue>(fs->last_value()).v, 2.0);

    Tick(rt, 0.55);
    EXPECT_EQ(std::get<ScalarValue>(fs->last_value()).v, 5.0);
}

TEST(ScriptTimer, ClearTimeoutCancels) {
    JsRuntime   rt;
    FrameInputs fi {};
    rt.SetFrameInputs(fi);
    auto* fs = MakeProbe(rt,
                         "test/clear_cancels",
                         R"JS(
        let n = 0;
        let h = setTimeout(() => { n++; }, 100);
        clearTimeout(h);
        export function update() { return n; }
    )JS");
    ASSERT_NE(fs, nullptr);

    Tick(rt, 0.50);
    EXPECT_EQ(std::get<ScalarValue>(fs->last_value()).v, 0.0);
}

TEST(ScriptTimer, HandleSelfCallCancels) {
    // Corpus also calls the return value as a function to cancel (e.g.
    // `if (stopTimeout) stopTimeout()`). Both shapes must work.
    JsRuntime   rt;
    FrameInputs fi {};
    rt.SetFrameInputs(fi);
    auto* fs = MakeProbe(rt,
                         "test/handle_self_call",
                         R"JS(
        let n = 0;
        let h = setTimeout(() => { n++; }, 100);
        h();  // cancel by invoking handle
        export function update() { return n; }
    )JS");
    ASSERT_NE(fs, nullptr);

    Tick(rt, 0.50);
    EXPECT_EQ(std::get<ScalarValue>(fs->last_value()).v, 0.0);
}

TEST(ScriptCompat, RegExpLegacyCapturesSurviveTimerCallbacks) {
    JsRuntime   rt;
    FrameInputs fi {};
    rt.SetFrameInputs(fi);
    auto* fs = MakeProbe(rt,
                         "test/regexp_legacy_captures",
                         R"JS(
        let n = 0;
        setInterval(() => {
            if (/(H+)/.test('HH:mm')) n = RegExp.$1.length;
        }, 100);
        export function update() { return n; }
    )JS");
    ASSERT_NE(fs, nullptr);

    Tick(rt, 0.15);
    EXPECT_EQ(std::get<ScalarValue>(fs->last_value()).v, 2.0);
}

TEST(ScriptAudio, RegisterAudioBuffersUsesRequestedResolution) {
    JsRuntime   rt;
    FrameInputs fi {};
    for (std::size_t i = 0; i < 16; ++i) {
        fi.audio.bands16.left.data()[i]    = static_cast<float>(i);
        fi.audio.bands16.right.data()[i]   = static_cast<float>(200 + i);
        fi.audio.bands16.average.data()[i] = static_cast<float>(100 + i);
    }
    rt.SetFrameInputs(fi);

    auto* fs = MakeProbe(rt,
                         "test/audio_buffers_resample",
                         R"JS(
        let audio = engine.registerAudioBuffers(16);
        export function update() {
            return audio.left.length * 100000000
                + audio.right.length * 1000000
                + audio.average[15] * 10000
                + audio.left[15] * 100
                + audio.right[15];
        }
    )JS");
    ASSERT_NE(fs, nullptr);

    rt.TickAll();
    EXPECT_DOUBLE_EQ(LastScalar(fs),
                     16.0 * 100000000.0 + 16.0 * 1000000.0 + 115.0 * 10000.0 + 15.0 * 100.0 +
                         215.0);

    for (std::size_t i = 0; i < 16; ++i) {
        fi.audio.bands16.left.data()[i]    = static_cast<float>(100 + i);
        fi.audio.bands16.right.data()[i]   = static_cast<float>(300 + i);
        fi.audio.bands16.average.data()[i] = static_cast<float>(200 + i);
    }
    rt.SetFrameInputs(fi);
    rt.TickAll();
    EXPECT_DOUBLE_EQ(LastScalar(fs),
                     16.0 * 100000000.0 + 16.0 * 1000000.0 + 215.0 * 10000.0 + 115.0 * 100.0 +
                         315.0);
}

TEST(ScriptAudio, RegisterAudioBuffersAcceptsResolution64Constant) {
    JsRuntime   rt;
    FrameInputs fi {};
    rt.SetFrameInputs(fi);

    auto* fs = MakeProbe(rt,
                         "test/audio_buffers_resolution_64_constant",
                         R"JS(
        const audio = engine.registerAudioBuffers(engine.AUDIO_RESOLUTION_64);
        export function update() { return audio.average.length; }
    )JS");
    ASSERT_NE(fs, nullptr);

    rt.TickAll();
    EXPECT_DOUBLE_EQ(LastScalar(fs), 64.0);
}

TEST(ScriptAudio, RegisterAudioBuffersKeepsMixedResolutionsIndependent) {
    FrameInputs fi {};
    for (std::size_t i = 0; i < 64; ++i) {
        fi.audio.bands64.average.data()[i] = static_cast<float>(100 + i);
    }
    for (std::size_t i = 0; i < 16; ++i) {
        fi.audio.bands16.average.data()[i] = static_cast<float>(100 + i * 4 + 3);
    }

    {
        JsRuntime rt;
        rt.SetFrameInputs(fi);
        auto* low  = MakeProbe(rt,
                               "test/audio_buffers_mixed_low_first",
                               R"JS(
            const audio = engine.registerAudioBuffers(16);
            export function update() { return audio.average.length * 1000 + audio.average[15]; }
        )JS");
        auto* full = MakeProbe(rt,
                               "test/audio_buffers_mixed_full_second",
                               R"JS(
            const audio = engine.registerAudioBuffers(64);
            export function update() { return audio.average.length * 1000 + audio.average[63]; }
        )JS");
        ASSERT_NE(low, nullptr);
        ASSERT_NE(full, nullptr);

        rt.TickAll();
        EXPECT_DOUBLE_EQ(LastScalar(low), 16163.0);
        EXPECT_DOUBLE_EQ(LastScalar(full), 64163.0);

        for (std::size_t i = 0; i < 64; ++i) {
            fi.audio.bands64.average.data()[i] = static_cast<float>(200 + i);
        }
        for (std::size_t i = 0; i < 16; ++i) {
            fi.audio.bands16.average.data()[i] = static_cast<float>(200 + i * 4 + 3);
        }
        rt.SetFrameInputs(fi);
        rt.TickAll();
        EXPECT_DOUBLE_EQ(LastScalar(low), 16263.0);
        EXPECT_DOUBLE_EQ(LastScalar(full), 64263.0);
    }

    {
        JsRuntime rt;
        rt.SetFrameInputs(fi);
        auto* full = MakeProbe(rt,
                               "test/audio_buffers_mixed_full_first",
                               R"JS(
            const audio = engine.registerAudioBuffers(64);
            export function update() { return audio.average.length; }
        )JS");
        auto* low  = MakeProbe(rt,
                               "test/audio_buffers_mixed_low_second",
                               R"JS(
            const audio = engine.registerAudioBuffers(16);
            export function update() { return audio.average.length; }
        )JS");
        ASSERT_NE(full, nullptr);
        ASSERT_NE(low, nullptr);

        rt.TickAll();
        EXPECT_DOUBLE_EQ(LastScalar(full), 64.0);
        EXPECT_DOUBLE_EQ(LastScalar(low), 16.0);
    }
}

TEST(ScriptAudio, RegisterAudioBuffersHoldsOneRuntimeDemandLease) {
    auto              demand = rstd::sync::Arc<owe::AudioResponseDemand>::make();
    std::vector<bool> changes;
    demand->SetCallback([&changes](bool active) {
        changes.push_back(active);
    });
    {
        JsRuntime rt;
        rt.SetAudioResponseDemand(rstd::Some(demand.clone()));
        auto* fs = MakeProbe(rt,
                             "test/audio_demand",
                             R"JS(
            engine.registerAudioBuffers(64);
            engine.registerAudioBuffers(32);
            export function update() { return 1; }
        )JS");
        ASSERT_NE(fs, nullptr);
        EXPECT_TRUE(demand->Active());
    }
    EXPECT_FALSE(demand->Active());
    EXPECT_EQ(changes, (std::vector<bool> { false, true, false }));
}

// ---------------------------------------------------------------------------
// SceneNode wrapper surface

TEST(ScriptNodeSize, ParserSetSizeFlowsToScript) {
    owe::SceneNode node;
    node.SetSize({ 320.0f, 240.0f });

    JsRuntime   rt;
    FrameInputs fi {};
    rt.SetFrameInputs(fi);
    auto* fs = rt.MakeFieldScript(
        R"JS(
            export function update() { return thisLayer.size.x + thisLayer.size.y * 1000; }
        )JS",
        "test/node_size_real",
        FieldKind::Scalar,
        owe::NJson::object(),
        owe::NJson(0),
        &node);
    ASSERT_NE(fs, nullptr);

    rt.TickAll();
    EXPECT_EQ(std::get<ScalarValue>(fs->last_value()).v, 320.0 + 240.0 * 1000);
}

TEST(ScriptNodeParent, CursorCallbackParentChainTerminatesAtUnparentedNode) {
    auto root  = Arc<owe::SceneNode>::make();
    auto child = Arc<owe::SceneNode>::make();
    root->AppendChild(child.clone());
    child->SetTranslate({ 500.0f, 500.0f, 0.0f });
    child->SetSize({ 200.0f, 200.0f });

    JsRuntime   rt;
    FrameInputs fi {};
    fi.canvas_w               = 1920.0f;
    fi.canvas_h               = 1080.0f;
    fi.cursor_in_window       = true;
    fi.cursor_x               = 500.0f / fi.canvas_w;
    fi.cursor_y               = 1.0f - 500.0f / fi.canvas_h;
    fi.mouse_buttons_released = 1u << 0;
    rt.SetFrameInputs(fi);
    auto* fs = rt.MakeFieldScript(
        R"JS(
            let result = 0;
            export function cursorUp() {
                let layer = thisLayer;
                let depth = 0;
                while (typeof layer !== 'undefined' && layer != null && depth < 8) {
                    depth++;
                    layer = layer.getParent();
                }
                result = typeof layer === 'undefined' ? depth : -depth;
            }
            export function update() { return result; }
        )JS",
        "test/parent_chain_terminates",
        FieldKind::Scalar,
        owe::NJson::object(),
        owe::NJson(0),
        child.as_ptr());
    ASSERT_NE(fs, nullptr);

    rt.TickAll();
    EXPECT_EQ(LastScalar(fs), 2.0);
}

TEST(ScriptNodeParent, DefaultLayerParentIsUndefined) {
    JsRuntime   rt;
    FrameInputs fi {};
    rt.SetFrameInputs(fi);
    auto* fs = rt.MakeFieldScript(
        R"JS(
            export function update() {
                return typeof thisLayer.getParent() === 'undefined' ? 1 : 0;
            }
        )JS",
        "test/default_layer_parent",
        FieldKind::Scalar,
        owe::NJson::object(),
        owe::NJson(0));
    ASSERT_NE(fs, nullptr);

    rt.TickAll();
    EXPECT_EQ(LastScalar(fs), 1.0);
}

TEST(ScriptNodeSoftMutation, VisibleAndAlphaWrites) {
    owe::SceneNode node;
    JsRuntime      rt;
    FrameInputs    fi {};
    rt.SetFrameInputs(fi);
    auto* fs = rt.MakeFieldScript(
        R"JS(
            // Toggle alpha and visibility from script.
            thisLayer.alpha = 0.25;
            thisLayer.visible = false;
            export function update() {}
        )JS",
        "test/visible_alpha_writes",
        FieldKind::Scalar,
        owe::NJson::object(),
        owe::NJson(0),
        &node);
    ASSERT_NE(fs, nullptr);

    EXPECT_TRUE(node.IsAlphaOverridden());
    EXPECT_EQ(node.UserAlpha(), 0.25f);
    EXPECT_FALSE(node.Visible());
    EXPECT_EQ(node.EffectiveAlpha(), 0.0f); // hidden wins
}

TEST(ScriptNodeSoftMutation, VisibleWritesUseSceneVisibilityOwner) {
    owe::Scene scene;
    auto       node = Arc<owe::SceneNode>::make();
    node->ID()      = rstd::i32(17);
    scene.RegisterNode(*node, Some(owe::WallpaperLayerId { .value = rstd::i32(17) }));
    scene.RootMut()->AppendChild(node.clone());

    JsRuntime rt;
    rt.SetScene(&scene);
    auto* script = rt.MakeFieldScript(
        R"JS(
            export function init() { thisLayer.visible = false; }
            export function update() { return thisLayer.visible ? 1 : 0; }
        )JS",
        "test/scene_owned_visibility",
        FieldKind::Bool,
        owe::NJson::object(),
        owe::NJson(true),
        node.as_ptr());
    ASSERT_NE(script, nullptr);

    rt.SetSceneRoot(scene.RootMut().as_raw_ptr());
    rt.TickAll();

    EXPECT_FALSE(node->Visible());
    EXPECT_TRUE(scene.IsLayerVisibilityElidable(owe::WallpaperLayerId { .value = rstd::i32(17) }));
    EXPECT_TRUE(scene.ConsumeRenderGraphDirty());
}

TEST(ScriptNodeSoftMutation, VisibleTrueRestoresUserAlpha) {
    owe::SceneNode node;
    JsRuntime      rt;
    FrameInputs    fi {};
    rt.SetFrameInputs(fi);
    auto* fs = rt.MakeFieldScript(
        R"JS(
            thisLayer.alpha = 0.4;
            thisLayer.visible = false;
            thisLayer.visible = true;
            export function update() {}
        )JS",
        "test/visible_restore",
        FieldKind::Scalar,
        owe::NJson::object(),
        owe::NJson(0),
        &node);
    ASSERT_NE(fs, nullptr);

    EXPECT_TRUE(node.Visible());
    EXPECT_FLOAT_EQ(node.EffectiveAlpha(), 0.4f);
}

TEST(ScriptNodeSoftMutation, PerspectiveWritesNodeFlag) {
    owe::SceneNode node;
    JsRuntime      rt;
    FrameInputs    fi {};
    rt.SetFrameInputs(fi);
    auto* fs = rt.MakeFieldScript(
        R"JS(
            thisLayer.perspective = true;
            export function update() { return thisLayer.perspective ? 1 : 0; }
        )JS",
        "test/perspective_write",
        FieldKind::Scalar,
        owe::NJson::object(),
        owe::NJson(0),
        &node);
    ASSERT_NE(fs, nullptr);

    EXPECT_TRUE(node.Perspective());
    rt.TickAll();
    EXPECT_EQ(std::get<ScalarValue>(fs->last_value()).v, 1.0);
}

TEST(ScriptNodeSoftMutation, ImageAlignmentDispatchesRegisteredSetter) {
    owe::SceneNode  node;
    JsRuntime       rt;
    String          alignment;
    owe::SceneNode* target { nullptr };
    rt.RegisterImageAlignmentSetter(
        &node,
        "center"_str,
        std::make_shared<JsRuntime::ImageAlignmentSetter::element_type>([&](owe::SceneNode* node, ref<str> value) {
            target    = node;
            alignment = String::make(value);
        }));

    FrameInputs fi {};
    rt.SetFrameInputs(fi);
    auto* fs = rt.MakeFieldScript(
        R"JS(
            thisLayer.alignment = 'bottom';
            export function update() { return thisLayer.alignment === 'bottom' ? 1 : 0; }
        )JS",
        "test/image_alignment_write",
        FieldKind::Scalar,
        owe::NJson::object(),
        owe::NJson(0),
        &node);
    ASSERT_NE(fs, nullptr);

    EXPECT_EQ(alignment.as_str(), "bottom"_str);
    EXPECT_EQ(target, &node);
    rt.TickAll();
    EXPECT_EQ(LastScalar(fs), 1.0);
}

TEST(ScriptNodeSoftMutation, ParallaxDepthDispatchesRegisteredAccessors) {
    owe::SceneNode  node;
    JsRuntime       rt;
    Vec2Value       depth { .x = 1.0, .y = 1.0 };
    owe::SceneNode* target { nullptr };
    rt.SetNodeParallaxDepthAccessors(
        std::make_shared<JsRuntime::NodeParallaxDepthGetter::element_type>([&depth](owe::SceneNode*) -> Option<Vec2Value> {
            return Some(depth);
        }),
        std::make_shared<JsRuntime::NodeParallaxDepthSetter::element_type>(
            [&depth, &target](owe::SceneNode* node, Vec2Value value) {
                target = node;
                depth  = value;
            }));

    auto* fs = rt.MakeFieldScript(
        R"JS(
            thisLayer.parallaxDepth = new Vec2(0.25, 0.75);
            export function update() {
                return thisLayer.parallaxDepth.x + thisLayer.parallaxDepth.y;
            }
        )JS",
        "test/parallax_depth_write",
        FieldKind::Scalar,
        owe::NJson::object(),
        owe::NJson(0),
        &node);
    ASSERT_NE(fs, nullptr);

    EXPECT_EQ(target, &node);
    EXPECT_DOUBLE_EQ(depth.x, 0.25);
    EXPECT_DOUBLE_EQ(depth.y, 0.75);
    rt.TickAll();
    EXPECT_EQ(LastScalar(fs), 1.0);
}

TEST(ScriptNodeSoftMutation, RuntimeLayersKeepIndependentPendingParallaxDepth) {
    auto state  = Arc<owe::UniformSceneState>::make(Arc<owe::AudioResponseDemand>::make());
    auto first  = Arc<owe::SceneNode>::make();
    auto writer = Arc<owe::SceneNode>::make();
    auto second = Arc<owe::SceneNode>::make();
    state->RegisterNodeParallaxContract(
        *first, i32(-1), owe::wpscene::ParallaxDepthBinding { { 1.0f, 1.0f }, true });
    state->RegisterNodeParallaxContract(
        *writer, i32(-1), owe::wpscene::ParallaxDepthBinding { { 1.0f, 1.0f }, true });
    state->RegisterNodeParallaxContract(
        *second, i32(-2), owe::wpscene::ParallaxDepthBinding { { 1.0f, 1.0f }, true });
    EXPECT_TRUE(state->SetNodeParallaxDepth(*first, { 0.0f, 0.0f }));

    auto first_depth  = state->NodeParallaxDepth(*first);
    auto writer_depth = state->NodeParallaxDepth(*writer);
    auto second_depth = state->NodeParallaxDepth(*second);
    ASSERT_TRUE(first_depth.is_some());
    ASSERT_TRUE(writer_depth.is_some());
    ASSERT_TRUE(second_depth.is_some());
    EXPECT_FLOAT_EQ((*first_depth)[usize()], 0.0f);
    EXPECT_FLOAT_EQ((*first_depth)[usize(1)], 0.0f);
    EXPECT_FLOAT_EQ((*writer_depth)[usize()], 0.0f);
    EXPECT_FLOAT_EQ((*writer_depth)[usize(1)], 0.0f);
    EXPECT_FLOAT_EQ((*second_depth)[usize()], 1.0f);
    EXPECT_FLOAT_EQ((*second_depth)[usize(1)], 1.0f);

    auto camera =
        Arc<owe::SceneCamera>::make(owe::SceneCamera::MakeOrthographic(1920, 1080, -1.0, 1.0));
    auto cameras           = Arc<owe::UniformCameraResolver>::make(rstd::move(camera));
    auto first_state       = Arc<owe::UniformNodeState>::make(first.clone(), cameras.clone());
    first_state->object_id = i32(-1);
    state->SetNodeState({ .index = u32(1), .generation = u32(1) }, first_state.clone());
    EXPECT_FLOAT_EQ(first_state->parallax.depth[0], 0.0f);
    EXPECT_FLOAT_EQ(first_state->parallax.depth[1], 0.0f);
}

TEST(ScriptNodeSoftMutation, ImageAlignmentBindingClonesForDynamicLayer) {
    owe::SceneNode  source;
    owe::SceneNode  clone;
    JsRuntime       rt;
    String          alignment;
    owe::SceneNode* target { nullptr };
    rt.RegisterImageAlignmentSetter(
        &source,
        "center"_str,
        std::make_shared<JsRuntime::ImageAlignmentSetter::element_type>([&](owe::SceneNode* node, ref<str> value) {
            target    = node;
            alignment = String::make(value);
        }));
    rt.CloneImageAlignmentBinding(&source, &clone);

    auto* fs = rt.MakeFieldScript(
        R"JS(
            thisLayer.alignment = 'bottom';
            export function update() { return thisLayer.alignment === 'bottom' ? 1 : 0; }
        )JS",
        "test/cloned_image_alignment_write",
        FieldKind::Scalar,
        owe::NJson::object(),
        owe::NJson(0),
        &clone);
    ASSERT_NE(fs, nullptr);

    EXPECT_EQ(target, &clone);
    EXPECT_EQ(alignment.as_str(), "bottom"_str);
    rt.TickAll();
    EXPECT_EQ(LastScalar(fs), 1.0);
}

TEST(ScriptNodeSoftMutation, OriginDispatchesRegisteredAccessors) {
    owe::SceneNode node;
    JsRuntime      rt;
    Vec3Value      logical_origin { .x = 10.0, .y = 20.0, .z = 0.0 };
    rt.RegisterNodeOriginAccessors(
        &node,
        std::make_shared<JsRuntime::NodeOriginGetter::element_type>([&logical_origin]() {
            return logical_origin;
        }),
        std::make_shared<JsRuntime::NodeOriginSetter::element_type>([&node, &logical_origin](Vec3Value origin) {
            logical_origin = origin;
            node.SetTranslate({ static_cast<float>(origin.x + 50.0),
                                static_cast<float>(origin.y),
                                static_cast<float>(origin.z) });
        }));

    FrameInputs fi {};
    rt.SetFrameInputs(fi);
    auto* fs = rt.MakeFieldScript(
        R"JS(
            thisLayer.origin = new Vec3(42, 7, 3);
            export function update() {
                const origin = thisLayer.origin;
                return origin.x * 100 + origin.y * 10 + origin.z;
            }
        )JS",
        "test/node_origin_accessors",
        FieldKind::Scalar,
        owe::NJson::object(),
        owe::NJson(0),
        &node);
    ASSERT_NE(fs, nullptr);

    EXPECT_FLOAT_EQ(node.Translate().x(), 92.0f);
    EXPECT_FLOAT_EQ(node.Translate().y(), 7.0f);
    rt.TickAll();
    EXPECT_EQ(LastScalar(fs), 4273.0);
}

TEST(ScriptNodeActuator, AlphaFieldReturnWritesNodeAlpha) {
    auto node = rstd::sync::Arc<owe::SceneNode>::make();

    ScriptScene ss;
    auto*       fs = ss.runtime().MakeFieldScript(
        R"JS(
            export function update() { return 0.125; }
        )JS",
        "test/alpha_field_return",
        FieldKind::Scalar,
        owe::NJson::object(),
        owe::NJson(1.0),
        node.as_ptr());
    ASSERT_NE(fs, nullptr);
    ss.AddActuator({ fs, MakeNodeAlphaApply(node.clone()) });

    FrameInputs fi {};
    ss.Tick(fi);

    EXPECT_TRUE(node->IsAlphaOverridden());
    EXPECT_FLOAT_EQ(node->UserAlpha(), 0.125f);
    EXPECT_FLOAT_EQ(node->EffectiveAlpha(), 0.125f);
}

TEST(ScriptNodeActuator, ColorFieldReturnWritesNodeColor) {
    auto node = Arc<owe::SceneNode>::make();

    ScriptScene ss;
    auto*       fs = ss.runtime().MakeFieldScript(
        R"JS(
            export function update() { return new Vec3(0.2, 0.4, 0.6); }
        )JS",
        "test/color_field_return",
        FieldKind::Vec3,
        owe::NJson::object(),
        owe::NJson("1 1 1"),
        node.as_ptr());
    ASSERT_NE(fs, nullptr);
    ss.AddActuator({ fs, MakeNodeColorApply(node.clone()) });

    FrameInputs fi {};
    ss.Tick(fi);

    EXPECT_FLOAT_EQ(node->Color().x(), 0.2f);
    EXPECT_FLOAT_EQ(node->Color().y(), 0.4f);
    EXPECT_FLOAT_EQ(node->Color().z(), 0.6f);
}

TEST(SceneNodeRuntimeAlpha, AlphaSourceContributesOverride) {
    owe::SceneNode source;
    owe::SceneNode composite;
    composite.SetAlphaSource(&source);

    EXPECT_FALSE(composite.IsAlphaOverridden());
    EXPECT_FLOAT_EQ(composite.EffectiveAlpha(), 1.0f);

    source.SetUserAlpha(0.25f);
    EXPECT_TRUE(composite.IsAlphaOverridden());
    EXPECT_FLOAT_EQ(composite.EffectiveAlpha(), 0.25f);

    composite.SetUserAlpha(0.5f);
    EXPECT_FLOAT_EQ(composite.EffectiveAlpha(), 0.125f);

    source.SetVisible(false);
    EXPECT_FLOAT_EQ(composite.EffectiveAlpha(), 0.0f);
}

TEST(ScriptNodeSoftMutation, BrightnessAndColorWrites) {
    owe::SceneNode node;
    JsRuntime      rt;
    FrameInputs    fi {};
    rt.SetFrameInputs(fi);
    auto* fs = rt.MakeFieldScript(
        R"JS(
            thisLayer.brightness = 1.5;
            thisLayer.color = new Vec3(1, 0.5, 0);
            export function update() {}
        )JS",
        "test/brightness_color",
        FieldKind::Scalar,
        owe::NJson::object(),
        owe::NJson(0),
        &node);
    ASSERT_NE(fs, nullptr);

    EXPECT_TRUE(node.IsBrightnessOverridden());
    EXPECT_FLOAT_EQ(node.Brightness(), 1.5f);
    EXPECT_TRUE(node.IsColorOverridden());
    EXPECT_FLOAT_EQ(node.Color().x(), 1.0f);
    EXPECT_FLOAT_EQ(node.Color().y(), 0.5f);
    EXPECT_FLOAT_EQ(node.Color().z(), 0.0f);
}

TEST(ScriptNodeSoftMutation, NoWritesLeaveOverridesUnset) {
    owe::SceneNode node;
    JsRuntime      rt;
    FrameInputs    fi {};
    rt.SetFrameInputs(fi);
    auto* fs = rt.MakeFieldScript(
        R"JS(
            // Reads only; baked material values should stand.
            let a = thisLayer.alpha;
            let v = thisLayer.visible;
            export function update() {}
        )JS",
        "test/no_writes",
        FieldKind::Scalar,
        owe::NJson::object(),
        owe::NJson(0),
        &node);
    ASSERT_NE(fs, nullptr);

    EXPECT_FALSE(node.IsAlphaOverridden());
    EXPECT_FALSE(node.IsBrightnessOverridden());
    EXPECT_FALSE(node.IsColorOverridden());
}

// ---------------------------------------------------------------------------
// Cursor event dispatch

namespace
{
FrameInputs MakeFi(float canvas_w = 1920.0f, float canvas_h = 1080.0f) {
    FrameInputs fi {};
    fi.canvas_w = canvas_w;
    fi.canvas_h = canvas_h;
    return fi;
}
} // namespace

TEST(ScriptCursor, EnterLeaveAndMove) {
    // A 200×200 layer centered at (500, 500). Cursor at (500/1920, 500/1080)
    // sits inside; (100/1920, 100/1080) sits outside.
    owe::SceneNode node;
    node.SetTranslate({ 500.0f, 500.0f, 0.0f });
    node.SetSize({ 200.0f, 200.0f });

    JsRuntime rt;
    rt.SetFrameInputs(MakeFi());
    auto* fs = rt.MakeFieldScript(
        R"JS(
            let enters = 0, leaves = 0, moves = 0;
            export function cursorEnter() { enters++; }
            export function cursorLeave() { leaves++; }
            export function cursorMove()  { moves++;  }
            export function update() { return enters * 1000000 + leaves * 1000 + moves; }
        )JS",
        "test/cursor_enter_leave_move",
        FieldKind::Scalar,
        owe::NJson::object(),
        owe::NJson(0),
        &node);
    ASSERT_NE(fs, nullptr);

    // Outside: no enter, no move.
    auto fi             = MakeFi();
    fi.cursor_in_window = true;
    fi.cursor_x         = 100.0f / 1920.0f;
    fi.cursor_y         = 100.0f / 1080.0f;
    rt.SetFrameInputs(fi);
    rt.TickAll();
    EXPECT_EQ(std::get<ScalarValue>(fs->last_value()).v, 0.0);

    // Move inside: 1 enter + 1 move.
    fi.cursor_x = 500.0f / 1920.0f;
    fi.cursor_y = 500.0f / 1080.0f;
    rt.SetFrameInputs(fi);
    rt.TickAll();
    EXPECT_EQ(std::get<ScalarValue>(fs->last_value()).v, 1'000'001);

    // Still inside (no edge): +1 move.
    rt.SetFrameInputs(fi);
    rt.TickAll();
    EXPECT_EQ(std::get<ScalarValue>(fs->last_value()).v, 1'000'002);

    // Move outside: +1 leave (no move when outside).
    fi.cursor_x = 100.0f / 1920.0f;
    rt.SetFrameInputs(fi);
    rt.TickAll();
    EXPECT_EQ(std::get<ScalarValue>(fs->last_value()).v, 1'001'002);
}

TEST(ScriptCursor, InitiallyUndefinedExportRemainsLive) {
    owe::SceneNode node;
    node.SetTranslate({ 500.0f, 500.0f, 0.0f });
    node.SetSize({ 200.0f, 200.0f });
    JsRuntime rt;
    auto fi = MakeFi();
    fi.cursor_in_window = true;
    fi.cursor_x = 500.0f / 1920.0f;
    fi.cursor_y = 500.0f / 1080.0f;
    rt.SetFrameInputs(fi);
    auto* fs = rt.MakeFieldScript(
        R"JS(
            let moves = 0;
            export let cursorMove;
            export function update() {
                cursorMove = () => { moves++; };
                return moves;
            }
        )JS", "test/cursor_live_export", FieldKind::Scalar,
        owe::NJson::object(), owe::NJson(0), &node);
    ASSERT_NE(fs, nullptr);
    rt.TickAll();
    EXPECT_EQ(std::get<ScalarValue>(fs->last_value()).v, 0.0);
    rt.TickAll();
    EXPECT_EQ(std::get<ScalarValue>(fs->last_value()).v, 1.0);
}

TEST(ScriptCursor, ClickAndDownUpInside) {
    owe::SceneNode node;
    node.SetTranslate({ 500.0f, 500.0f, 0.0f });
    node.SetSize({ 200.0f, 200.0f });

    JsRuntime rt;
    rt.SetFrameInputs(MakeFi());
    auto* fs = rt.MakeFieldScript(
        R"JS(
            let down = 0, up = 0, click = 0, last_btn = -9;
            export function cursorDown(e)  { down++;  last_btn = e.button; }
            export function cursorUp(e)    { up++;    last_btn = e.button; }
            export function cursorClick(e) { click++; last_btn = e.button; }
            export function update() {
                return down * 10000 + up * 100 + click + (last_btn + 1) * 1000000;
            }
        )JS",
        "test/cursor_click",
        FieldKind::Scalar,
        owe::NJson::object(),
        owe::NJson(0),
        &node);
    ASSERT_NE(fs, nullptr);

    auto fi             = MakeFi();
    fi.cursor_in_window = true;
    fi.cursor_x         = 500.0f / 1920.0f;
    fi.cursor_y         = 500.0f / 1080.0f;
    // Press left button (bit 0) this frame.
    fi.mouse_buttons_pressed = 1u << 0;
    fi.mouse_buttons_down    = 1u << 0;
    rt.SetFrameInputs(fi);
    rt.TickAll();
    // 1 down, 1 click, last_btn = 0 → 1*1000000 + 1*10000 + 0*100 + 1 = 1010001
    EXPECT_EQ(std::get<ScalarValue>(fs->last_value()).v, 1'010'001);

    // Release this frame (no press): 1 up, last_btn=0.
    fi.mouse_buttons_pressed  = 0;
    fi.mouse_buttons_released = 1u << 0;
    fi.mouse_buttons_down     = 0;
    rt.SetFrameInputs(fi);
    rt.TickAll();
    EXPECT_EQ(std::get<ScalarValue>(fs->last_value()).v, 1'010'101);
}

TEST(ScriptCursor, ClickRestartsNamedAnimationLayer) {
    owe::SceneNode node;
    node.SetTranslate({ 500.0f, 500.0f, 0.0f });
    node.SetSize({ 200.0f, 200.0f });

    auto clip     = Arc<owe::SceneAnimationClip>::make(owe::SceneAnimationClipSpec {
        .name = String::make("Arona Drool"_str),
        .mode = String::make("single"_str),
        .fps  = 30.0f,
        .end  = i32(60),
    });
    auto playback = Arc<owe::SceneAnimationPlayback>::make(rstd::move(clip));
    node.RegisterAnimation(playback.clone());

    JsRuntime rt;
    rt.SetFrameInputs(MakeFi());
    auto* fs = rt.MakeFieldScript(
        R"JS(
            let animation;
            export function init(value) {
                animation = thisLayer.getAnimationLayer("Arona Drool");
                animation.pause();
                return value;
            }
            export function cursorClick() {
                animation.stop();
                animation.play();
            }
            export function update() {
                return animation.getFrame() + (animation.isPlaying() ? 1000 : 0);
            }
        )JS",
        "test/cursor_animation_layer",
        FieldKind::Scalar,
        owe::NJson::object(),
        owe::NJson(0),
        &node);
    ASSERT_NE(fs, nullptr);
    rt.SetSceneRoot(&node);
    EXPECT_FALSE(playback->IsPlaying());
    playback->SetFrame(i32(17));

    auto fi                  = MakeFi();
    fi.cursor_in_window      = true;
    fi.cursor_x              = 500.0f / 1920.0f;
    fi.cursor_y              = 500.0f / 1080.0f;
    fi.mouse_buttons_pressed = 1u << 0;
    rt.SetFrameInputs(fi);
    rt.TickAll();

    EXPECT_TRUE(playback->IsPlaying());
    EXPECT_EQ(playback->Frame(), i32());
    EXPECT_EQ(LastScalar(fs), 1000.0);
}

TEST(ScriptCursor, ClickOutsideIsIgnored) {
    owe::SceneNode node;
    node.SetTranslate({ 500.0f, 500.0f, 0.0f });
    node.SetSize({ 200.0f, 200.0f });

    JsRuntime rt;
    rt.SetFrameInputs(MakeFi());
    auto* fs = rt.MakeFieldScript(
        R"JS(
            let click = 0;
            export function cursorClick() { click++; }
            export function update() { return click; }
        )JS",
        "test/cursor_outside_click",
        FieldKind::Scalar,
        owe::NJson::object(),
        owe::NJson(0),
        &node);
    ASSERT_NE(fs, nullptr);

    auto fi                  = MakeFi();
    fi.cursor_in_window      = true;
    fi.cursor_x              = 100.0f / 1920.0f; // outside the AABB
    fi.cursor_y              = 100.0f / 1080.0f;
    fi.mouse_buttons_pressed = 1u << 0;
    rt.SetFrameInputs(fi);
    rt.TickAll();
    EXPECT_EQ(std::get<ScalarValue>(fs->last_value()).v, 0.0);
}

TEST(ScriptCursor, CursorOutOfWindowSuppressesEvents) {
    owe::SceneNode node;
    node.SetTranslate({ 500.0f, 500.0f, 0.0f });
    node.SetSize({ 200.0f, 200.0f });

    JsRuntime rt;
    rt.SetFrameInputs(MakeFi());
    auto* fs = rt.MakeFieldScript(
        R"JS(
            let n = 0;
            export function cursorEnter() { n++; }
            export function cursorMove()  { n++; }
            export function update() { return n; }
        )JS",
        "test/cursor_out_of_window",
        FieldKind::Scalar,
        owe::NJson::object(),
        owe::NJson(0),
        &node);
    ASSERT_NE(fs, nullptr);

    auto fi             = MakeFi();
    fi.cursor_in_window = false; // outside window: events suppressed
    fi.cursor_x         = 500.0f / 1920.0f;
    fi.cursor_y         = 500.0f / 1080.0f;
    rt.SetFrameInputs(fi);
    rt.TickAll();
    EXPECT_EQ(std::get<ScalarValue>(fs->last_value()).v, 0.0);
}

TEST(ScriptCursor, GlobalInputRefreshesFrameFields) {
    JsRuntime rt;
    rt.SetFrameInputs(MakeFi());
    auto* fs = rt.MakeFieldScript(
        R"JS(
            export function update() {
                return input.cursorScreenPosition.x +
                       input.cursorScreenPosition.y * 1000 +
                       (input.cursorLeftDown ? 1000000 : 0) +
                       input.mouseButtonsDown * 10000000;
            }
        )JS",
        "test/global_input_refresh",
        FieldKind::Scalar,
        owe::NJson::object(),
        owe::NJson(0));
    ASSERT_NE(fs, nullptr);

    auto fi               = MakeFi();
    fi.screen_w           = 800.0f;
    fi.screen_h           = 600.0f;
    fi.cursor_x           = 0.25f;
    fi.cursor_y           = 0.5f;
    fi.mouse_buttons_down = 1u << 0;
    fi.cursor_in_window   = true;
    rt.SetFrameInputs(fi);
    rt.TickAll();
    EXPECT_EQ(std::get<ScalarValue>(fs->last_value()).v, 11'300'200.0);

    fi.cursor_x           = 0.5f;
    fi.mouse_buttons_down = 0;
    rt.SetFrameInputs(fi);
    rt.TickAll();
    EXPECT_EQ(std::get<ScalarValue>(fs->last_value()).v, 300'400.0);
}

TEST(ScriptCursor, WorldPositionFlipsTopDownInputY) {
    JsRuntime rt;
    rt.SetFrameInputs(MakeFi());
    auto* fs = rt.MakeFieldScript(
        R"JS(
            export function update() {
                return new Vec3(
                    input.cursorWorldPosition.x,
                    input.cursorWorldPosition.y,
                    input.cursorScreenPosition.y);
            }
        )JS",
        "test/global_input_world_y",
        FieldKind::Vec3,
        owe::NJson::object(),
        owe::NJson("0.0 0.0 0.0"));
    ASSERT_NE(fs, nullptr);

    auto fi     = MakeFi();
    fi.screen_h = 600.0f;
    fi.cursor_x = 0.25f;
    fi.cursor_y = 0.25f;
    rt.SetFrameInputs(fi);
    rt.TickAll();

    ASSERT_TRUE(std::holds_alternative<Vec3Value>(fs->last_value()));
    const auto& v = std::get<Vec3Value>(fs->last_value());
    EXPECT_NEAR(v.x, 480.0, 0.001);
    EXPECT_NEAR(v.y, 810.0, 0.001);
    EXPECT_NEAR(v.z, 150.0, 0.001);
}

// ---------------------------------------------------------------------------
// Texture animation override

TEST(ScriptTexAnim, SetFramePinsAndStopsPlayback) {
    owe::SceneNode node;
    JsRuntime      rt;
    FrameInputs    fi {};
    rt.SetFrameInputs(fi);
    auto* fs = rt.MakeFieldScript(
        R"JS(
            let anim = thisLayer.getTextureAnimation();
            anim.setFrame(2);
            export function update() {
                return anim.getFrame() * 10 + (anim.isPlaying() ? 1 : 0);
            }
        )JS",
        "test/texanim_setframe",
        FieldKind::Scalar,
        owe::NJson::object(),
        owe::NJson(0),
        &node);
    ASSERT_NE(fs, nullptr);

    rt.TickAll();
    EXPECT_EQ(node.TexAnim().current_frame, 2);
    EXPECT_FALSE(node.TexAnim().playing);
    EXPECT_EQ(std::get<ScalarValue>(fs->last_value()).v, 20.0);
}

TEST(ScriptTexAnim, PlayResumesAutoAdvance) {
    owe::SceneNode node;
    node.TexAnim().current_frame = 5;
    node.TexAnim().playing       = false;

    JsRuntime   rt;
    FrameInputs fi {};
    rt.SetFrameInputs(fi);
    auto* fs = rt.MakeFieldScript(
        R"JS(
            thisLayer.getTextureAnimation().play();
            export function update() {}
        )JS",
        "test/texanim_play",
        FieldKind::Scalar,
        owe::NJson::object(),
        owe::NJson(0),
        &node);
    ASSERT_NE(fs, nullptr);

    rt.TickAll();
    EXPECT_EQ(node.TexAnim().current_frame, -1);
    EXPECT_TRUE(node.TexAnim().playing);
}

TEST(ScriptTexAnim, PauseFreezesAtCurrent) {
    owe::SceneNode node;
    JsRuntime      rt;
    FrameInputs    fi {};
    rt.SetFrameInputs(fi);
    auto* fs = rt.MakeFieldScript(
        R"JS(
            thisLayer.getTextureAnimation().pause();
            export function update() {}
        )JS",
        "test/texanim_pause",
        FieldKind::Scalar,
        owe::NJson::object(),
        owe::NJson(0),
        &node);
    ASSERT_NE(fs, nullptr);

    rt.TickAll();
    EXPECT_FALSE(node.TexAnim().playing);
    EXPECT_EQ(node.TexAnim().current_frame, -1); // pause keeps auto cursor
}

TEST(ScriptTexAnim, ReadsAndControlsBoundSpriteAnimation) {
    owe::Scene scene;
    auto       node = rstd::sync::Arc<owe::SceneNode>::make();
    node->SetGeneratorIdentity(Some(owe::WallpaperLayerId { .value = rstd::i32(711) }));
    auto       mesh = std::make_shared<owe::SceneMesh>();
    owe::SceneMaterial material;
    material.textures.push_back("tex/script-sprite");
    mesh->AddMaterial(rstd::move(material));
    mesh->Submeshes().push_back(owe::SceneMesh::Submesh { .material_slot = rstd::u32() });
    node->AddMesh(mesh);
    scene.RootMut()->AppendChild(node.clone());
    owe::SceneTexture texture { .url = "tex/script-sprite", .isSprite = true };
    texture.spriteAnim.AppendFrame(owe::SpriteFrame { .frametime = 0.1f });
    texture.spriteAnim.AppendFrame(owe::SpriteFrame { .frametime = 0.2f });
    texture.spriteAnim.AppendFrame(owe::SpriteFrame { .frametime = 0.3f });
    scene.RegisterTexture(String::make("tex/script-sprite"_str), rstd::move(texture));
    scene.RebuildResourceIndex();

    owe::OfflineExecutionContext offline;
    offline.trace_scene = true;
    owe::OfflineExecutionScope scope(offline);
    JsRuntime   rt;
    FrameInputs fi {};
    rt.SetFrameInputs(fi);
    auto* fs = rt.MakeFieldScript(
        R"JS(
            let anim;
            let initMetadata = false;
            export function init() {
                anim = thisLayer.getTextureAnimation();
                initMetadata = anim.frameCount === 3 && Math.abs(anim.duration - 0.6) < 0.001 &&
                    anim.rate === 1;
            }
            export function update() {
                anim.rate = 2;
                anim.pause();
                return (initMetadata ? 1 : -1) * 1000000 + anim.frameCount * 10000 +
                    anim.duration * 1000 + anim.rate * 100 +
                    anim.getFrame() * 10 + (anim.isPlaying() ? 1 : 0);
            }
        )JS",
        "test/texanim_bound_sprite",
        FieldKind::Scalar,
        owe::NJson::object(),
        owe::NJson(0),
        node.as_ptr());
    ASSERT_NE(fs, nullptr);

    rt.SetSceneRoot(scene.RootMut().as_raw_ptr());
    rt.TickAll();
    EXPECT_NEAR(std::get<ScalarValue>(fs->last_value()).v, 1'030'800.0, 0.01);
    auto* animations = node->TextureAnimationRegistry();
    ASSERT_NE(animations, nullptr);
    EXPECT_FALSE(animations->IsPlaying(*node));
    EXPECT_EQ(animations->Rate(*node), rstd::f64(2));
    EXPECT_TRUE(std::any_of(offline.dependencies.begin(),
                            offline.dependencies.end(),
                            [](const owe::OfflineDependency& dependency) {
                                return dependency.owner == 711 && dependency.target == 711 &&
                                       dependency.operation == "read" &&
                                       dependency.property == "textureAnimation" &&
                                       !dependency.initialization;
                            }));
    EXPECT_TRUE(std::any_of(offline.dependencies.begin(),
                            offline.dependencies.end(),
                            [](const owe::OfflineDependency& dependency) {
                                return dependency.owner == 711 && dependency.target == 711 &&
                                       dependency.operation == "write" &&
                                       dependency.property == "textureAnimation" &&
                                       !dependency.initialization;
                            }));
}

TEST(ScriptTexAnim, UnboundLayerFallsBackToJsStub) {
    // No node bound — getTextureAnimation() returns the JS-side stub from
    // the bootstrap, which silently accepts setFrame / play / etc.
    JsRuntime   rt;
    FrameInputs fi {};
    rt.SetFrameInputs(fi);
    auto* fs = rt.MakeFieldScript(
        R"JS(
            let a = thisLayer.getTextureAnimation();
            a.setFrame(7);
            export function update() { return a.getFrame(); }
        )JS",
        "test/texanim_unbound",
        FieldKind::Scalar,
        owe::NJson::object(),
        owe::NJson(0),
        nullptr);
    ASSERT_NE(fs, nullptr);

    rt.TickAll();
    // JS stub records the frame in a closure local; getFrame returns it.
    EXPECT_EQ(std::get<ScalarValue>(fs->last_value()).v, 7.0);
}

TEST(ScriptVideoTexture, ControlsStableNativePlaybackState) {
    owe::SceneNode node;
    auto           playback = Arc<owe::VideoPlaybackState>::make();
    playback->PublishTime(f64(2.5), Some(f64(10.0)));
    node.SetVideoControl(playback.clone());

    JsRuntime   rt;
    FrameInputs fi {};
    rt.SetFrameInputs(fi);
    auto* fs = rt.MakeFieldScript(
        R"JS(
            const video = thisLayer.getVideoTexture();
            video.pause();
            video.rate = 1.5;
            video.setCurrentTime(4);
            export function update() {
                return video.duration + video.getCurrentTime() + (video.isPlaying() ? 1 : 0);
            }
        )JS",
        "test/video_texture_control",
        FieldKind::Scalar,
        owe::NJson::object(),
        owe::NJson(0),
        &node);
    ASSERT_NE(fs, nullptr);

    rt.TickAll();
    auto state = playback->Snapshot();
    EXPECT_FALSE(state.playing);
    EXPECT_EQ(state.rate, f64(1.5));
    EXPECT_EQ(state.seek_sequence, u64(1));
    EXPECT_EQ(state.seek_seconds, f64(4.0));
    EXPECT_EQ(LastScalar(fs), 14.0);
}

TEST(ScriptVideoTexture, OfflineClockSurvivesRendererLifetime) {
    auto playback = Arc<owe::VideoPlaybackState>::make();

    EXPECT_EQ(playback->AdvanceOffline(f64(0.5)), f64());
    EXPECT_EQ(playback->AdvanceOffline(f64(1.0)), f64(0.5));
    // A replacement decoder observes the same scene-time clock rather than restarting at zero.
    EXPECT_EQ(playback->AdvanceOffline(f64(1.5)), f64(1.0));

    playback->Pause();
    EXPECT_EQ(playback->AdvanceOffline(f64(2.0)), f64(1.5));
    EXPECT_EQ(playback->AdvanceOffline(f64(2.5)), f64(1.5));
    playback->SetRate(f64(2.0));
    playback->Play();
    EXPECT_EQ(playback->AdvanceOffline(f64(3.0)), f64(1.5));
    EXPECT_EQ(playback->AdvanceOffline(f64(3.5)), f64(2.5));
    playback->Seek(f64(0.25));
    EXPECT_EQ(playback->AdvanceOffline(f64(4.0)), f64(0.25));
}

TEST(ScriptVideoTexture, HiddenOfflineControlsSyncClockAtMutation) {
    owe::OfflineExecutionContext offline;
    owe::OfflineExecutionScope   scope(offline);
    owe::SceneNode               node;
    auto                         playback = Arc<owe::VideoPlaybackState>::make();
    node.SetVideoControl(playback.clone());
    EXPECT_EQ(playback->AdvanceOffline(f64(0.5)), f64());

    JsRuntime   rt;
    FrameInputs fi {};
    rt.SetFrameInputs(fi);
    auto* fs = rt.MakeFieldScript(
        R"JS(
            const video = thisLayer.getVideoTexture();
            let phase = 0;
            export function update() {
                if (phase++ === 0) video.pause();
                else { video.rate = 2; video.play(); }
                return 0;
            }
        )JS",
        "test/video_texture_hidden_offline_controls",
        FieldKind::Scalar,
        owe::NJson::object(),
        owe::NJson(0),
        &node);
    ASSERT_NE(fs, nullptr);

    offline.elapsed = 1.0;
    rt.TickAll();
    EXPECT_EQ(playback->CurrentTime(), f64(0.5));
    offline.elapsed = 2.0;
    rt.TickAll();
    EXPECT_EQ(playback->AdvanceOffline(f64(2.5)), f64(1.5));
}

TEST(ScriptVideoTexture, HiddenOfflineGetterAdvancesAndWrapsWithoutDecoder) {
    owe::OfflineExecutionContext offline;
    owe::OfflineExecutionScope scope(offline);
    owe::SceneNode node;
    auto playback = Arc<owe::VideoPlaybackState>::make();
    node.SetVideoControl(playback.clone());
    playback->PublishTime(f64(), Some(f64(3.0)));
    EXPECT_EQ(playback->AdvanceOffline(f64(0.5)), f64());
    JsRuntime rt;
    FrameInputs fi {};
    rt.SetFrameInputs(fi);
    auto* fs = rt.MakeFieldScript(
        R"JS(export function update() { return thisLayer.getVideoTexture().getCurrentTime(); })JS",
        "test/video_texture_hidden_offline_getter", FieldKind::Scalar,
        owe::NJson::object(), owe::NJson(0), &node);
    ASSERT_NE(fs, nullptr);
    offline.elapsed = 2.0;
    rt.TickAll();
    EXPECT_EQ(std::get<ScalarValue>(fs->last_value()).v, 1.5);
    offline.elapsed = 4.0;
    rt.TickAll();
    EXPECT_EQ(std::get<ScalarValue>(fs->last_value()).v, 0.5);
    EXPECT_EQ(playback->AdvanceOffline(f64(4.5)), f64(4.0));
}

TEST(SceneNodeVisibility, CaptureForcePreservesAuthoredAlpha) {
    owe::SceneNode node;
    node.SetUserAlpha(0.4f);
    node.SetVisible(false);
    EXPECT_EQ(node.EffectiveAlpha(), 0.0f);
    node.SetCaptureForceVisibilityAlpha(true);
    EXPECT_EQ(node.EffectiveAlpha(), 0.4f);
    EXPECT_FALSE(node.Visible());
    node.SetCaptureForceVisibilityAlpha(false);
    EXPECT_EQ(node.EffectiveAlpha(), 0.0f);
    EXPECT_EQ(node.UserAlpha(), 0.4f);
}

TEST(ScriptVideoTexture, StillImageIsNullAndContainerIsOrdinaryTypeError) {
    owe::OfflineExecutionContext offline;
    offline.trace_scene = true;
    owe::OfflineExecutionScope   scope(offline);
    auto still = Arc<owe::SceneNode>::make();
    still->SetGeneratorIdentity(Some(owe::WallpaperLayerId { .value = rstd::i32(713) }));
    auto mesh = std::make_shared<owe::SceneMesh>();
    mesh->AddMaterial(owe::SceneMaterial {});
    mesh->Submeshes().push_back(owe::SceneMesh::Submesh { .material_slot = rstd::u32() });
    still->AddMesh(std::move(mesh));

    JsRuntime still_runtime;
    auto* still_script = still_runtime.MakeFieldScript(
        "const video = thisLayer.getVideoTexture(); export function update() { return video === null ? 1 : 0; }",
        "test/video_texture_still_image",
        FieldKind::Scalar,
        owe::NJson::object(),
        owe::NJson(0),
        still.as_ptr());
    ASSERT_NE(still_script, nullptr);
    auto* unguarded_still_script = still_runtime.MakeFieldScript(
        "export function init() { thisLayer.getVideoTexture().stop(); } export function update() { return 2; }",
        "test/video_texture_still_image_unguarded",
        FieldKind::Scalar,
        owe::NJson::object(),
        owe::NJson(0),
        still.as_ptr());
    ASSERT_NE(unguarded_still_script, nullptr);
    auto root = Arc<owe::SceneNode>::make();
    root->AppendChild(still.clone());
    still_runtime.SetSceneRoot(root.as_ptr());
    still_runtime.TickAll();
    EXPECT_EQ(LastScalar(still_script), 1.0);
    EXPECT_FALSE(offline.failed);
    ASSERT_EQ(offline.source_script_errors.size(), 1u);
    EXPECT_EQ(offline.source_script_errors.front().phase, "init");
    EXPECT_NE(offline.source_script_errors.front().message.find("null"), std::string::npos);
    EXPECT_TRUE(std::any_of(offline.dependencies.begin(),
                            offline.dependencies.end(),
                            [](const owe::OfflineDependency& dependency) {
                                return dependency.owner == 713 && dependency.target == 713 &&
                                       dependency.operation == "read" &&
                                       dependency.property == "videoTexture";
                            }));

    owe::OfflineExecutionContext container_offline;
    owe::OfflineExecutionScope   container_scope(container_offline);
    JsRuntime                     container_runtime;
    owe::SceneNode                container;
    container.SetGeneratorIdentity(Some(owe::WallpaperLayerId { .value = rstd::i32(714) }));
    auto* container_script = container_runtime.MakeFieldScript(
        "thisLayer.getVideoTexture();",
        "test/video_texture_container",
        FieldKind::Scalar,
        owe::NJson::object(),
        owe::NJson(0),
        &container);
    ASSERT_NE(container_script, nullptr);
    EXPECT_FALSE(container_offline.failed);
    ASSERT_EQ(container_offline.source_script_errors.size(), 1u);
    EXPECT_EQ(container_offline.source_script_errors.front().phase, "module");
    EXPECT_NE(container_offline.source_script_errors.front().message.find("TypeError"),
              std::string::npos);
}

// ---------------------------------------------------------------------------
// localStorage

namespace
{
std::string MakeTmpLsPath(const char* tag) {
    auto p = std::filesystem::temp_directory_path() / (std::string("owe_ls_") + tag + ".json");
    std::error_code ec;
    std::filesystem::remove(p, ec);
    const auto utf8 = p.u8string();
    return std::string(reinterpret_cast<const char*>(utf8.data()), utf8.size());
}
} // namespace

TEST(ScriptLocalStorage, InMemoryWithoutPersistencePath) {
    JsRuntime   rt;
    FrameInputs fi {};
    rt.SetFrameInputs(fi);
    auto* fs = rt.MakeFieldScript(
        R"JS(
            localStorage.set('k', 42);
            localStorage.set('o', { a: 1, b: 'two' });
            export function update() {
                let v = localStorage.get('k');
                let o = localStorage.get('o');
                return v + (o ? o.a + (o.b === 'two' ? 100 : 0) : 0);
            }
        )JS",
        "test/ls_inmemory",
        FieldKind::Scalar,
        owe::NJson::object(),
        owe::NJson(0),
        nullptr);
    ASSERT_NE(fs, nullptr);

    rt.TickAll();
    EXPECT_EQ(std::get<ScalarValue>(fs->last_value()).v, 42 + 1 + 100);
}

TEST(ScriptLocalStorage, RemoveDeletesKey) {
    JsRuntime   rt;
    FrameInputs fi {};
    rt.SetFrameInputs(fi);
    auto* fs = rt.MakeFieldScript(
        R"JS(
            localStorage.set('k', 9);
            localStorage.remove('k');
            export function update() {
                let v = localStorage.get('k');
                return v === undefined ? -1 : v;
            }
        )JS",
        "test/ls_remove",
        FieldKind::Scalar,
        owe::NJson::object(),
        owe::NJson(0),
        nullptr);
    ASSERT_NE(fs, nullptr);

    rt.TickAll();
    EXPECT_EQ(std::get<ScalarValue>(fs->last_value()).v, -1.0);
}

TEST(ScriptLocalStorage, PersistsAcrossRuntimes) {
    const std::string path = MakeTmpLsPath("persist");

    {
        JsRuntime rt;
        rt.SetPersistence(path);
        FrameInputs fi {};
        rt.SetFrameInputs(fi);
        auto* fs = rt.MakeFieldScript(
            R"JS(
                localStorage.set('count', 7);
                localStorage.set('label', 'hello');
                export function update() {}
            )JS",
            "test/ls_writer",
            FieldKind::Scalar,
            owe::NJson::object(),
            owe::NJson(0),
            nullptr);
        ASSERT_NE(fs, nullptr);
    }

    // Fresh runtime reading the same file should see the prior writes.
    {
        JsRuntime rt;
        rt.SetPersistence(path);
        FrameInputs fi {};
        rt.SetFrameInputs(fi);
        auto* fs = rt.MakeFieldScript(
            R"JS(
                export function update() {
                    let c = localStorage.get('count');
                    let l = localStorage.get('label');
                    return (c ?? -1) + (l === 'hello' ? 1000 : 0);
                }
            )JS",
            "test/ls_reader",
            FieldKind::Scalar,
            owe::NJson::object(),
            owe::NJson(0),
            nullptr);
        ASSERT_NE(fs, nullptr);
        rt.TickAll();
        EXPECT_EQ(std::get<ScalarValue>(fs->last_value()).v, 7 + 1000);
    }

    std::error_code ec;
    std::filesystem::remove(path, ec);
}

TEST(ScriptLocalStorage, ObjectRoundTrip) {
    const std::string path = MakeTmpLsPath("obj");

    {
        JsRuntime rt;
        rt.SetPersistence(path);
        FrameInputs fi {};
        rt.SetFrameInputs(fi);
        rt.MakeFieldScript(
            R"JS(
                localStorage.set('pos', { x: 10, y: 20 });
                export function update() {}
            )JS",
            "test/ls_obj_write",
            FieldKind::Scalar,
            owe::NJson::object(),
            owe::NJson(0),
            nullptr);
    }
    {
        JsRuntime rt;
        rt.SetPersistence(path);
        FrameInputs fi {};
        rt.SetFrameInputs(fi);
        auto* fs = rt.MakeFieldScript(
            R"JS(
                export function update() {
                    let p = localStorage.get('pos');
                    return (p && p.x === 10 && p.y === 20) ? 1 : 0;
                }
            )JS",
            "test/ls_obj_read",
            FieldKind::Scalar,
            owe::NJson::object(),
            owe::NJson(0),
            nullptr);
        rt.TickAll();
        EXPECT_EQ(std::get<ScalarValue>(fs->last_value()).v, 1.0);
    }
    std::error_code ec;
    std::filesystem::remove(path, ec);
}

TEST(ScriptNodeChildren, WalksSceneNodeChildren) {
    auto parent = rstd::sync::Arc<owe::SceneNode>::make();
    auto a      = rstd::sync::Arc<owe::SceneNode>::make();
    auto b      = rstd::sync::Arc<owe::SceneNode>::make();
    parent->AppendChild(a.clone());
    parent->AppendChild(b.clone());
    a->SetTranslate({ 10.0f, 0.0f, 0.0f });
    b->SetTranslate({ 20.0f, 0.0f, 0.0f });

    JsRuntime   rt;
    FrameInputs fi {};
    rt.SetFrameInputs(fi);
    auto* fs = rt.MakeFieldScript(
        R"JS(
            export function update() {
                const cs = thisLayer.getChildren();
                return cs.length * 1000 + (cs[0] ? cs[0].origin.x : 0)
                                       + (cs[1] ? cs[1].origin.x : 0);
            }
        )JS",
        "test/getChildren_walk",
        FieldKind::Scalar,
        owe::NJson::object(),
        owe::NJson(0),
        parent.as_ptr());
    ASSERT_NE(fs, nullptr);

    rt.TickAll();
    EXPECT_EQ(std::get<ScalarValue>(fs->last_value()).v, 2000 + 10 + 20);
}

TEST(ScriptLayerLookup, MissingLayerHandleResolvesLater) {
    auto root = rstd::sync::Arc<owe::SceneNode>::make();

    JsRuntime   rt;
    FrameInputs fi {};
    rt.SetFrameInputs(fi);
    auto* fs = rt.MakeFieldScript(
        R"JS(
            let late = thisLayer.getLayer("late-sound");
            export function init() {
                late.stop();
            }
            export function applyUserProperties(changed) {
                if (changed.go) late.play();
            }
            export function update() { return late.isPlaying() ? 1 : 0; }
        )JS",
        "test/lazy_layer_lookup",
        FieldKind::Scalar,
        owe::NJson::object(),
        owe::NJson(0),
        root.as_ptr());
    ASSERT_NE(fs, nullptr);

    auto late = rstd::sync::Arc<owe::SceneNode>::make(
        Eigen::Vector3f::Zero(), Eigen::Vector3f::Ones(), Eigen::Vector3f::Zero(), "late-sound");
    root->AppendChild(late.clone());
    rt.SetSceneRoot(root.as_ptr());
    rt.SetUserProperty("go", owe::ParseNJson(R"({"type":"bool","value":true})").unwrap());
    rt.TickAll();
    EXPECT_EQ(std::get<ScalarValue>(fs->last_value()).v, 1.0);
}

TEST(ScriptLayerLookup, GetEffectVisibleWritesSceneDirty) {
    owe::Scene scene;
    auto       root  = Box<owe::SceneNode>::make();
    auto       layer = rstd::sync::Arc<owe::SceneNode>::make(
        Eigen::Vector3f::Zero(), Eigen::Vector3f::Ones(), Eigen::Vector3f::Zero(), "audio-layer");
    root->AppendChild(layer.clone());
    auto root_pointer = root.get();
    scene.SetRoot(rstd::move(root));

    layer->SetCamera("audio-effect-camera");
    auto camera =
        Arc<owe::SceneCamera>::make(owe::SceneCamera::MakeOrthographic(256, 256, -1.0, 1.0));
    auto effect_layer = std::make_shared<owe::SceneNodeLayer>(
        layer.as_ptr(), 256.0f, 256.0f, "_rt_effect_composite_test");
    auto effect             = std::make_shared<owe::SceneImageEffect>();
    effect->name            = "audio-color";
    effect->runtime_visible = true;
    effect_layer->AddEffect(effect);
    layer->AttachLayer(effect_layer);
    scene.RegisterCamera(String::make("audio-effect-camera"_str), rstd::move(camera));

    JsRuntime   rt;
    FrameInputs fi {};
    rt.SetFrameInputs(fi);
    rt.SetScene(&scene);
    rt.SetSceneRoot(root_pointer);
    auto* fs = rt.MakeFieldScript(
        R"JS(
            export function update() {
                const effect = thisScene.getLayer("audio-layer").getEffect("audio-color");
                effect.visible = false;
                return effect.visible ? 1 : 0;
            }
        )JS",
        "test/layer_get_effect_visible",
        FieldKind::Scalar,
        owe::NJson::object(),
        owe::NJson(0),
        root_pointer);
    ASSERT_NE(fs, nullptr);

    rt.TickAll();
    EXPECT_EQ(std::get<ScalarValue>(fs->last_value()).v, 0.0);
    EXPECT_FALSE(effect->runtime_visible);
    EXPECT_TRUE(scene.ConsumeRenderGraphDirty());
    EXPECT_FALSE(scene.ConsumeRenderGraphDirty());
}

TEST(ScriptLayerLookup, EffectIndexAndMaterialWritesUseSceneMaterialOwner) {
    owe::Scene scene;
    auto       root  = Box<owe::SceneNode>::make();
    auto       layer = Arc<owe::SceneNode>::make(
        Eigen::Vector3f::Zero(), Eigen::Vector3f::Ones(), Eigen::Vector3f::Zero(), "color-layer");
    root->AppendChild(layer.clone());
    auto* root_pointer = root.get();
    scene.SetRoot(rstd::move(root));

    layer->SetCamera("color-effect-camera");
    auto camera =
        Arc<owe::SceneCamera>::make(owe::SceneCamera::MakeOrthographic(256, 256, -1.0, 1.0));
    auto effect_layer = std::make_shared<owe::SceneNodeLayer>(
        layer.as_ptr(), 256.0f, 256.0f, "_rt_effect_composite_color");
    auto effect                             = std::make_shared<owe::SceneImageEffect>();
    effect->name                            = "color";
    auto                        effect_node = Arc<owe::SceneNode>::make();
    auto                        mesh        = std::make_shared<owe::SceneMesh>();
    owe::SceneMaterial          material;
    owe::SceneShaderVariantDesc variant;
    variant.uniform_aliases["color"]       = "g_TintColor";
    variant.uniform_aliases["channelMask"] = "g_ChannelMask";
    material.customShader.variant          = Some(rstd::move(variant));
    material.customShader.constValues["g_TintColor"] =
        owe::ShaderValue(rstd::array<float, 3> { 1.0f, 0.0f, 0.0f });
    material.customShader.constValues["g_ChannelMask"] =
        owe::ShaderValue(rstd::array<float, 4> { 1.0f, 1.0f, 1.0f, 1.0f });
    mesh->AddMaterial(std::move(material));
    auto* effect_material = mesh->Material();
    effect_node->AddMesh(std::move(mesh));
    effect->nodes.push_back(owe::SceneImageEffectNode {
        .output    = owe::SceneEffectTarget::LayerNext(),
        .sceneNode = effect_node.clone(),
    });
    effect_layer->AddEffect(effect);
    layer->AttachLayer(effect_layer);
    scene.RegisterCamera(String::make("color-effect-camera"_str), rstd::move(camera));

    JsRuntime rt;
    rt.SetScene(&scene);
    rt.SetSceneRoot(root_pointer);
    auto  properties = owe::ParseNJson(R"({"color":"0.2 0.4 0.6"})").unwrap();
    auto* fs         = rt.MakeFieldScript(
        R"JS(
            export var scriptProperties = createScriptProperties()
                .addColor({ name: 'color', value: new Vec3(1, 0, 0) })
                .finish();
            export function update() {
                const effect = thisLayer.getEffect(0);
                effect.getMaterial(0).color = scriptProperties.color;
                effect.getMaterial(0).channelMask = new Vec4(0, 0.25, 0.5, 0.75);
                return thisLayer.getEffectCount() + (effect.name === "color" ? 1 : 0);
            }
        )JS",
        "test/layer_effect_material",
        FieldKind::Scalar,
        properties,
        owe::NJson(0),
        layer.as_ptr());
    ASSERT_NE(fs, nullptr);

    rt.TickAll();
    EXPECT_EQ(LastScalar(fs), 2.0);
    ASSERT_NE(effect_material, nullptr);
    auto color = effect_material->customShader.constValues.find("g_TintColor");
    ASSERT_NE(color, effect_material->customShader.constValues.end());
    ASSERT_EQ(color->second.size(), usize(3));
    EXPECT_FLOAT_EQ(color->second[usize()], 0.2f);
    EXPECT_FLOAT_EQ(color->second[usize(1)], 0.4f);
    EXPECT_FLOAT_EQ(color->second[usize(2)], 0.6f);
    auto channel_mask = effect_material->customShader.constValues.find("g_ChannelMask");
    ASSERT_NE(channel_mask, effect_material->customShader.constValues.end());
    ASSERT_EQ(channel_mask->second.size(), usize(4));
    EXPECT_FLOAT_EQ(channel_mask->second[usize()], 0.0f);
    EXPECT_FLOAT_EQ(channel_mask->second[usize(1)], 0.25f);
    EXPECT_FLOAT_EQ(channel_mask->second[usize(2)], 0.5f);
    EXPECT_FLOAT_EQ(channel_mask->second[usize(3)], 0.75f);
}

TEST(ScriptLayerLookup, MissingLayerReturnsNullAfterSceneRootIsReady) {
    auto root = rstd::sync::Arc<owe::SceneNode>::make();

    JsRuntime   rt;
    FrameInputs fi {};
    rt.SetFrameInputs(fi);
    rt.SetSceneRoot(root.as_ptr());
    auto* fs = rt.MakeFieldScript(
        R"JS(
            let result = -1;
            export function init() {
                const missing = thisScene.getLayer("__definitely_missing_layer__");
                result = missing === null && typeof missing === 'object' && !Boolean(missing) ? 1 : -1;
            }
            export function update() { return result; }
        )JS",
        "test/lazy_layer_default_transform",
        FieldKind::Scalar,
        owe::NJson::object(),
        owe::NJson(0),
        root.as_ptr());
    ASSERT_NE(fs, nullptr);

    rt.TickAll();
    EXPECT_EQ(std::get<ScalarValue>(fs->last_value()).v, 1.0);
}

TEST(ScriptWEMath, SmoothStepCamelCaseAndAliases) {
    // ~165 corpus callsites use camelCase smoothStep; lowercase exists too.
    JsRuntime   rt;
    FrameInputs fi {};
    rt.SetFrameInputs(fi);
    auto* fs = rt.MakeFieldScript(
        R"JS(
            import * as M from 'WEMath';
            export function update() {
                // smoothStep(0,1,0.5) → 0.5
                let a = M.smoothStep(0, 1, 0.5);
                let b = M.smoothstep(0, 1, 0.5);
                let c = M.deg2rad(180);   // ≈ π
                let d = M.rad2deg(Math.PI);  // 180
                let e = 180 * M.deg2rad;
                let f = Math.PI * M.rad2deg;
                return Math.round(a * 100) + Math.round(b * 100) * 100
                       + Math.round(c * 1000) * 10000   // π*1000 ≈ 3142
                       + Math.round(d) * 1000000000
                       + Math.round((e - c) * 1000000)
                       + Math.round((f - d) * 1000000);
            }
        )JS",
        "test/wemath_smoothstep",
        FieldKind::Scalar,
        owe::NJson::object(),
        owe::NJson(0),
        nullptr);
    ASSERT_NE(fs, nullptr);

    rt.TickAll();
    // expected: 50 + 50*100 + 3142*10000 + 180*1e9
    EXPECT_EQ(std::get<ScalarValue>(fs->last_value()).v,
              50.0 + 50.0 * 100 + 3142.0 * 10000 + 180.0 * 1e9);
}

TEST(ScriptModule, ImportedBindingInitializesTopLevelConstBeforeUpdate) {
    JsRuntime   rt;
    FrameInputs fi {};
    rt.SetFrameInputs(fi);
    auto* fs = rt.MakeFieldScript(
        R"JS(
            import * as WEColor from 'WEColor';
            const colors = {
                blue: WEColor.normalizeColor(new Vec3(110, 168, 255)),
            };
            export function update() {
                return WEColor.expandColor(colors.blue).divide(255);
            }
        )JS",
        "test/module_top_level_import_binding",
        FieldKind::Vec3,
        owe::NJson::object(),
        owe::NJson("0.0 0.0 0.0"),
        nullptr);
    ASSERT_NE(fs, nullptr);

    rt.TickAll();
    ASSERT_TRUE(std::holds_alternative<Vec3Value>(fs->last_value()));
    const auto& value = std::get<Vec3Value>(fs->last_value());
    EXPECT_NEAR(value.x, 110.0 / 255.0, 0.0001);
    EXPECT_NEAR(value.y, 168.0 / 255.0, 0.0001);
    EXPECT_NEAR(value.z, 1.0, 0.0001);
}

TEST(ScriptWEVector, VectorAngle2UsesDegrees) {
    JsRuntime   rt;
    FrameInputs fi {};
    rt.SetFrameInputs(fi);
    auto* fs = rt.MakeFieldScript(
        R"JS(
            import * as V from 'WEVector';
            export function update() {
                return new Vec3(
                    V.vectorAngle2(new Vec2(1, 0)),
                    V.vectorAngle2(new Vec2(0, 1)),
                    V.vectorAngle2(V.angleVector2(-135)));
            }
        )JS",
        "test/wevector_vector_angle2",
        FieldKind::Vec3,
        owe::NJson::object(),
        owe::NJson("0.0 0.0 0.0"),
        nullptr);
    ASSERT_NE(fs, nullptr);

    rt.TickAll();
    ASSERT_TRUE(std::holds_alternative<Vec3Value>(fs->last_value()));
    const auto& value = std::get<Vec3Value>(fs->last_value());
    EXPECT_NEAR(value.x, 0.0, 0.001);
    EXPECT_NEAR(value.y, 90.0, 0.001);
    EXPECT_NEAR(value.z, -135.0, 0.001);
}

TEST(ScriptVector, InstanceMixInterpolatesVectors) {
    JsRuntime   rt;
    FrameInputs fi {};
    rt.SetFrameInputs(fi);
    auto* fs = rt.MakeFieldScript(
        R"JS(
            export function update(value) {
                let a = new Vec3(1, 2, 3).mix(new Vec3(5, 6, 7), 0.25);
                let b = new Vec2(2, 6).mix(new Vec2(10, 14), 0.5);
                let c = new Vec3(2).mix(6, 0.25);
                return new Vec3(a.x + b.x, a.y + b.y, a.z + c.z);
            }
        )JS",
        "test/vector_mix",
        FieldKind::Vec3,
        owe::NJson::object(),
        owe::NJson("0.0 0.0 0.0"),
        nullptr);
    ASSERT_NE(fs, nullptr);

    rt.TickAll();
    ASSERT_TRUE(std::holds_alternative<Vec3Value>(fs->last_value()));
    const auto& v = std::get<Vec3Value>(fs->last_value());
    EXPECT_NEAR(v.x, 8.0, 0.001);
    EXPECT_NEAR(v.y, 13.0, 0.001);
    EXPECT_NEAR(v.z, 7.0, 0.001);
}

TEST(ScriptVector, Vec2ConstructorCopiesVectorComponents) {
    JsRuntime   rt;
    FrameInputs fi {};
    rt.SetFrameInputs(fi);
    auto* fs = rt.MakeFieldScript(
        R"JS(
            export function update(value) {
                let fromVec3 = new Vec2(new Vec3(100, 200, 300));
                let fromObject = new Vec2({ x: 3, y: 4, z: 5 });
                return new Vec3(fromVec3.x, fromVec3.y, fromObject.length());
            }
        )JS",
        "test/vector_vec2_copy_ctor",
        FieldKind::Vec3,
        owe::NJson::object(),
        owe::NJson("0.0 0.0 0.0"),
        nullptr);
    ASSERT_NE(fs, nullptr);

    rt.TickAll();
    ASSERT_TRUE(std::holds_alternative<Vec3Value>(fs->last_value()));
    const auto& v = std::get<Vec3Value>(fs->last_value());
    EXPECT_NEAR(v.x, 100.0, 0.001);
    EXPECT_NEAR(v.y, 200.0, 0.001);
    EXPECT_NEAR(v.z, 5.0, 0.001);
}

TEST(ScriptVector, LengthSqrMatchesWallpaperEngineVectors) {
    JsRuntime   rt;
    FrameInputs fi {};
    rt.SetFrameInputs(fi);
    auto* fs = rt.MakeFieldScript(
        R"JS(
            export function update(value) {
                return new Vec3(2, 3, 6).lengthSqr() + new Vec2(5, 12).lengthSqr();
            }
        )JS",
        "test/vector_length_sqr",
        FieldKind::Scalar,
        owe::NJson::object(),
        owe::NJson(0),
        nullptr);
    ASSERT_NE(fs, nullptr);

    rt.TickAll();
    EXPECT_NEAR(std::get<ScalarValue>(fs->last_value()).v, 218.0, 0.001);
}

TEST(ScriptVector, NormalizeReturnsUnitVectors) {
    JsRuntime   rt;
    FrameInputs fi {};
    rt.SetFrameInputs(fi);
    auto* fs = rt.MakeFieldScript(
        R"JS(
            export function update(value) {
                let a = new Vec3(3, 4, 0).normalize();
                let b = new Vec2(0, 5).normalize();
                let z = new Vec3(0, 0, 0).normalize();
                return new Vec3(a.x, a.y + b.y * 10, z.length());
            }
        )JS",
        "test/vector_normalize",
        FieldKind::Vec3,
        owe::NJson::object(),
        owe::NJson("0.0 0.0 0.0"),
        nullptr);
    ASSERT_NE(fs, nullptr);

    rt.TickAll();
    ASSERT_TRUE(std::holds_alternative<Vec3Value>(fs->last_value()));
    const auto& v = std::get<Vec3Value>(fs->last_value());
    EXPECT_NEAR(v.x, 0.6, 0.001);
    EXPECT_NEAR(v.y, 10.8, 0.001);
    EXPECT_NEAR(v.z, 0.0, 0.001);
}

TEST(ScriptVector, EngineCanvasSizeSupportsVectorMethods) {
    JsRuntime   rt;
    FrameInputs fi {};
    fi.canvas_w = 3840.0f;
    fi.canvas_h = 2160.0f;
    rt.SetFrameInputs(fi);
    auto* fs = rt.MakeFieldScript(
        R"JS(
            export function update() {
                const v = engine.canvasSize.divide(2);
                return v.x + v.y * 10000;
            }
        )JS",
        "test/canvas_size_vec2_methods",
        FieldKind::Scalar,
        owe::NJson::object(),
        owe::NJson(0),
        nullptr);
    ASSERT_NE(fs, nullptr);

    rt.TickAll();
    EXPECT_EQ(std::get<ScalarValue>(fs->last_value()).v, 1920.0 + 1080.0 * 10000);
}

TEST(ScriptScene, InitialLayerConfigPreservesAuthoredEffects) {
    owe::SceneNode node;
    JsRuntime      rt;
    FrameInputs    fi {};
    rt.SetFrameInputs(fi);
    rt.RegisterInitialLayerConfig(
        &node,
        owe::ParseNJson(
            R"({"name":"Brush","effects":[{"name":"Square"},{"name":"Glider"}]})")
            .unwrap());
    rt.SetSceneRoot(&node);
    auto* fs = rt.MakeFieldScript(
        R"JS(
            let seen = -1;
            export function init() {
                const cfg = thisScene.getInitialLayerConfig(thisLayer);
                seen = cfg.name === 'Brush' &&
                       cfg.effects[0].name === 'Square' &&
                       cfg.effects[1].name === 'Glider'
                    ? cfg.effects.length
                    : -1;
            }
            export function update() { return seen; }
        )JS",
        "test/initial_layer_config",
        FieldKind::Scalar,
        owe::NJson::object(),
        owe::NJson(0),
        &node);
    ASSERT_NE(fs, nullptr);

    rt.TickAll();
    EXPECT_EQ(std::get<ScalarValue>(fs->last_value()).v, 2.0);
}

TEST(ScriptScene, DestroyLayerHidesSceneNode) {
    auto root  = rstd::sync::Arc<owe::SceneNode>::make();
    auto child = rstd::sync::Arc<owe::SceneNode>::make(
        Eigen::Vector3f::Zero(), Eigen::Vector3f::Ones(), Eigen::Vector3f::Zero(), "coin");
    root->AppendChild(child.clone());

    JsRuntime   rt;
    FrameInputs fi {};
    rt.SetFrameInputs(fi);
    rt.SetSceneRoot(root.as_ptr());
    auto* fs = rt.MakeFieldScript(
        R"JS(
            let hidden = 0;
            export function init() {
                const coin = thisScene.getLayer("coin");
                thisScene.destroyLayer(coin);
                hidden = coin.visible ? 0 : 1;
            }
            export function update() { return hidden; }
        )JS",
        "test/destroy_layer_hides_node",
        FieldKind::Scalar,
        owe::NJson::object(),
        owe::NJson(0),
        root.as_ptr());
    ASSERT_NE(fs, nullptr);

    rt.TickAll();
    EXPECT_FALSE(child->Visible());
    EXPECT_EQ(std::get<ScalarValue>(fs->last_value()).v, 1.0);
}

TEST(ScriptScene, CameraTransformsRoundTripThroughSceneOwner) {
    owe::Scene scene;
    auto       camera = Arc<owe::SceneCamera>::make(
        owe::SceneCamera::MakePerspective(16.0 / 9.0, 0.01, 1000.0, 53.0));
    camera->SetLookAt({ 0.0, 0.0, 250.0 }, { 0.0, 1.0, 0.0 }, { 0.0, 1.0, 0.0 });
    scene.RegisterCamera(String::make("main"_str), rstd::move(camera));
    ASSERT_TRUE(scene.SetActiveCamera("main"_str));

    JsRuntime rt;
    rt.SetScene(&scene);
    auto* script = rt.MakeFieldScript(
        R"JS(
            export function init() {
                const camera = thisScene.getCameraTransforms();
                camera.eye = new Vec3(3, 4, 5);
                camera.center = new Vec3(0, 0, 0);
                thisScene.setCameraTransforms(camera);
            }
            export function update() {
                const camera = thisScene.getCameraTransforms();
                return camera.eye.x * 100 + camera.eye.y * 10 + camera.eye.z;
            }
        )JS",
        "test/camera_transforms_round_trip",
        FieldKind::Scalar,
        owe::NJson::object(),
        owe::NJson(0));
    ASSERT_NE(script, nullptr);

    rt.SetSceneRoot(scene.RootMut().as_raw_ptr());
    rt.TickAll();

    auto transforms = scene.ActiveCameraTransforms();
    ASSERT_TRUE(transforms.is_some());
    EXPECT_TRUE(transforms->eye.isApprox(Eigen::Vector3d { 3.0, 4.0, 5.0 }));
    EXPECT_TRUE(transforms->center.isApprox(Eigen::Vector3d::Zero()));
    EXPECT_TRUE(transforms->up.isApprox(Eigen::Vector3d::UnitY()));
    EXPECT_DOUBLE_EQ(LastScalar(script), 345.0);
}

TEST(ScriptScene, OrthographicCameraTransformsUseAttachedNodeCoordinates) {
    owe::Scene scene;
    auto       camera_node = Arc<owe::SceneNode>::make(Eigen::Vector3f { 1920.0f, 1080.0f, 0.0f },
                                                       Eigen::Vector3f::Ones(),
                                                       Eigen::Vector3f::Zero());
    auto       camera      = Arc<owe::SceneCamera>::make(
        owe::SceneCamera::MakeOrthographic(3840.0, 2160.0, -5000.0, 5000.0));
    camera->AttatchNode(camera_node.as_ptr());
    scene.RegisterCamera(String::make("main"_str), rstd::move(camera));
    ASSERT_TRUE(scene.SetActiveCamera("main"_str));

    JsRuntime rt;
    rt.SetScene(&scene);
    auto* script = rt.MakeFieldScript(
        R"JS(
            export function init() {
                const camera = thisScene.getCameraTransforms();
                camera.eye = new Vec3(10, 20, 0);
                camera.center = new Vec3(10, 20, -1);
                thisScene.setCameraTransforms(camera);
            }
            export function update() {
                const camera = thisScene.getCameraTransforms();
                return camera.eye.x * 100 + camera.eye.y;
            }
        )JS",
        "test/orthographic_camera_attached_coordinates",
        FieldKind::Scalar,
        owe::NJson::object(),
        owe::NJson(0));
    ASSERT_NE(script, nullptr);

    rt.SetSceneRoot(scene.RootMut().as_raw_ptr());
    rt.TickAll();

    auto active = scene.ActiveCamera();
    ASSERT_TRUE(active.is_some());
    auto world = (**active).Transforms();
    EXPECT_TRUE(world.eye.isApprox(Eigen::Vector3d { 1930.0, 1100.0, 0.0 }));
    EXPECT_TRUE(world.center.isApprox(Eigen::Vector3d { 1930.0, 1100.0, -1.0 }));
    EXPECT_TRUE(world.up.isApprox(Eigen::Vector3d::UnitY()));
    EXPECT_DOUBLE_EQ(LastScalar(script), 1020.0);
}

TEST(ScriptScene, CreateLayerRoutesConfigurationAndLayerCloneToFactory) {
    auto root  = Arc<owe::SceneNode>::make();
    auto owner = Arc<owe::SceneNode>::make(
        Eigen::Vector3f::Zero(), Eigen::Vector3f::Ones(), Eigen::Vector3f::Zero(), "owner");
    auto style = Arc<owe::SceneNode>::make(
        Eigen::Vector3f::Zero(), Eigen::Vector3f::Ones(), Eigen::Vector3f::Zero(), "Style1");
    root->AppendChild(owner.clone());
    root->AppendChild(style.clone());

    Vec<owe::NJson>           configs;
    Vec<Arc<owe::SceneNode>> created;
    JsRuntime                rt;
    rt.RegisterInitialLayerConfig(
        style.as_ptr(),
        owe::ParseNJson(R"({"name":"Style1","text":"template"})").unwrap());
    rt.SetLayerConfigFactory(std::make_shared<JsRuntime::LayerConfigFactory::element_type>(
        [&root, &configs, &created](owe::SceneNode*,
                                    owe::NJson config) -> Option<Arc<owe::SceneNode>> {
            auto node = Arc<owe::SceneNode>::make();
            root->AppendChild(node.clone());
            configs.push(owe::NJson(config));
            created.push(node.clone());
            return Some(rstd::move(node));
        }));
    auto* fs = rt.MakeFieldScript(
        R"JS(
            let result = 0;
            export function init() {
                const background = thisScene.createLayer({
                    color: new Vec3(0.1, 0.2, 0.3),
                    size: new Vec3(10, 20, 0).toString()
                });
                const text = thisScene.createLayer(thisScene.getLayer('Style1'));
                result = background.visible && text.visible ? 2 : -1;
            }
            export function update() { return result; }
        )JS",
        "test/create_layer_configuration",
        FieldKind::Scalar,
        owe::NJson::object(),
        owe::NJson(0),
        owner.as_ptr());
    ASSERT_NE(fs, nullptr);

    rt.SetSceneRoot(root.as_ptr());
    rt.ClearLayerFactory();
    rt.ClearLayerConfigFactory();
    rt.TickAll();

    ASSERT_EQ(configs.len(), usize(2));
    std::string color;
    std::string size;
    std::string text;
    EXPECT_TRUE(owe::GetJsonValue(configs[usize()], "color", color, false));
    EXPECT_TRUE(owe::GetJsonValue(configs[usize()], "size", size, false));
    EXPECT_TRUE(owe::GetJsonValue(configs[usize(1)], "text", text, false));
    EXPECT_EQ(color, "0.1 0.2 0.3");
    EXPECT_EQ(size, "10 20 0");
    EXPECT_EQ(text, "template");
    EXPECT_DOUBLE_EQ(LastScalar(fs), 2.0);
}

TEST(ScriptScene, CreatedLayersCanBeSortedBeforeAnExistingLayer) {
    owe::Scene scene;
    auto       ring = Arc<owe::SceneNode>::make(
        Eigen::Vector3f::Zero(), Eigen::Vector3f::Ones(), Eigen::Vector3f::Zero(), "ring");
    auto body = Arc<owe::SceneNode>::make(
        Eigen::Vector3f::Zero(), Eigen::Vector3f::Ones(), Eigen::Vector3f::Zero(), "body");
    scene.AttachRuntimeNode(*scene.RootMut(), ring.clone());
    scene.AttachRuntimeNode(*scene.RootMut(), body.clone());
    (void)scene.ConsumeRenderGraphDirty();

    Vec<Arc<owe::SceneNode>> created;
    JsRuntime                rt;
    rt.SetScene(&scene);
    rt.RegisterInitialLayerConfig(ring.as_ptr(), owe::ParseNJson(R"({})").unwrap());
    rt.RegisterInitialLayerConfig(body.as_ptr(), owe::ParseNJson(R"({})").unwrap());
    rt.SetLayerFactory(std::make_shared<JsRuntime::LayerFactory::element_type>(
        [&scene, &created](owe::SceneNode*, LayerAssetReference) -> Option<Arc<owe::SceneNode>> {
            auto node = Arc<owe::SceneNode>::make();
            scene.AttachRuntimeNode(*scene.RootMut(), node.clone());
            created.push(node.clone());
            return Some(rstd::move(node));
        }));
    auto* fs = rt.MakeFieldScript(
        R"JS(
            let result = -1;
            export function init() {
                const target = thisScene.getLayerIndex(thisLayer);
                const first = thisScene.createLayer('models/bar.json');
                thisScene.sortLayer(first, target);
                const second = thisScene.createLayer('models/bar.json');
                thisScene.sortLayer(second, target);
                result = thisScene.getLayerIndex(first) * 1000
                       + thisScene.getLayerIndex(second) * 100
                       + thisScene.getLayerIndex(thisLayer) * 10
                       + thisScene.getLayerIndex('body');
            }
            export function update() { return result; }
        )JS",
        "test/sort_created_layers",
        FieldKind::Scalar,
        owe::NJson::object(),
        owe::NJson(0),
        ring.as_ptr());
    ASSERT_NE(fs, nullptr);

    rt.SetSceneRoot(scene.RootMut().as_raw_ptr());
    rt.ClearLayerFactory();
    rt.TickAll();

    ASSERT_EQ(created.len(), usize(2));
    const auto& children = scene.Root()->GetChildren();
    ASSERT_EQ(children.len(), usize(4));
    EXPECT_EQ(children[usize()].as_ptr(), created[usize(1)].as_ptr());
    EXPECT_EQ(children[usize(1)].as_ptr(), created[usize()].as_ptr());
    EXPECT_EQ(children[usize(2)].as_ptr(), ring.as_ptr());
    EXPECT_EQ(children[usize(3)].as_ptr(), body.as_ptr());
    EXPECT_DOUBLE_EQ(LastScalar(fs), 1023.0);
    EXPECT_TRUE(scene.ConsumeRenderGraphDirty());
}

TEST(ScriptScene, PublicLayerQueriesUseAuthoredOrderAndTrackRuntimeLayers) {
    owe::Scene scene;
    auto       a = Arc<owe::SceneNode>::make(
        Eigen::Vector3f::Zero(), Eigen::Vector3f::Ones(), Eigen::Vector3f::Zero(), "A");
    auto one = Arc<owe::SceneNode>::make(
        Eigen::Vector3f::Zero(), Eigen::Vector3f::Ones(), Eigen::Vector3f::Zero(), "1");
    auto c = Arc<owe::SceneNode>::make(
        Eigen::Vector3f::Zero(), Eigen::Vector3f::Ones(), Eigen::Vector3f::Zero(), "C");
    auto hidden = Arc<owe::SceneNode>::make(
        Eigen::Vector3f::Zero(), Eigen::Vector3f::Ones(), Eigen::Vector3f::Zero(), "D");
    auto reporter = Arc<owe::SceneNode>::make(
        Eigen::Vector3f::Zero(), Eigen::Vector3f::Ones(), Eigen::Vector3f::Zero(), "reporter");
    auto internal = Arc<owe::SceneNode>::make(
        Eigen::Vector3f::Zero(), Eigen::Vector3f::Ones(), Eigen::Vector3f::Zero(), "internal");
    a->AppendChild(c.clone());
    hidden->SetVisible(false);
    a->SetGeneratorIdentity(Some(owe::WallpaperLayerId { .value = rstd::i32(37) }));
    one->SetGeneratorIdentity(Some(owe::WallpaperLayerId { .value = rstd::i32(9) }));
    reporter->SetGeneratorIdentity(Some(owe::WallpaperLayerId { .value = rstd::i32(13) }));
    scene.AttachRuntimeNode(*scene.RootMut(), a.clone());
    scene.AttachRuntimeNode(*scene.RootMut(), one.clone());
    scene.AttachRuntimeNode(*scene.RootMut(), hidden.clone());
    scene.AttachRuntimeNode(*scene.RootMut(), reporter.clone());
    scene.AttachRuntimeNode(*scene.RootMut(), internal.clone());

    owe::OfflineExecutionContext offline;
    offline.trace_scene = true;
    owe::OfflineExecutionScope scope(offline);
    JsRuntime                   rt;
    rt.SetScene(&scene);
    rt.RegisterInitialLayerConfig(
        a.as_ptr(), owe::ParseNJson(R"({"name":"A"})").unwrap());
    rt.RegisterInitialLayerConfig(one.as_ptr(), owe::ParseNJson(R"({})").unwrap());
    rt.RegisterInitialLayerConfig(c.as_ptr(), owe::ParseNJson(R"({})").unwrap());
    rt.RegisterInitialLayerConfig(hidden.as_ptr(), owe::ParseNJson(R"({})").unwrap());
    rt.RegisterInitialLayerConfig(reporter.as_ptr(), owe::ParseNJson(R"({})").unwrap());
    rt.SetLayerConfigFactory(std::make_shared<JsRuntime::LayerConfigFactory::element_type>(
        [&scene](owe::SceneNode*, owe::NJson config) -> Option<Arc<owe::SceneNode>> {
            auto node = Arc<owe::SceneNode>::make(Eigen::Vector3f::Zero(),
                                                   Eigen::Vector3f::Ones(),
                                                   Eigen::Vector3f::Zero(),
                                                   "Dynamic");
            bool visible = true;
            owe::GetJsonValue(config, "visible", visible, false);
            node->SetVisible(visible);
            scene.AttachRuntimeNode(*scene.RootMut(), node.clone());
            return Some(rstd::move(node));
        }));
    auto* fs = rt.MakeFieldScript(
        R"JS(
            let result = -1;
            export function init() {
                const initial = thisScene.getLayerCount() === 5 &&
                    thisScene.enumerateLayers().map(x => x.name).join(',') === 'A,1,C,D,reporter' &&
                    thisScene.getLayer(1).name === '1' && thisScene.getLayer('1').name === '1' &&
                    thisScene.getInitialLayerConfig(0).name === 'A' &&
                    thisScene.getLayerIndex(thisScene.getLayer('C')) === 2 &&
                    thisScene.getLayer(-1) === null && thisScene.getLayer(8) === null &&
                    thisScene.getLayer(0.5).name === 'A' && thisScene.getLayer(NaN).name === 'A';
                const sortedResult = thisScene.sortLayer('1', 2);
                const sorted = thisScene.enumerateLayers().map(x => x.name).join(',') ===
                    'A,C,1,D,reporter';
                const typedLookup = thisScene.getLayerIndex(1) === 1 &&
                    thisScene.getLayerIndex('1') === 2;
                const numericSort = thisScene.sortLayer(1, 1) === true;
                const dynamic = thisScene.createLayer({ name: 'Dynamic', visible: false });
                const appended = thisScene.getLayerCount() === 6 &&
                    thisScene.getLayerIndex(dynamic) === 5 && dynamic.visible === false;
                const destroyed = thisScene.destroyLayer(dynamic) === true;
                const removed = thisScene.getLayerCount() === 5 &&
                    thisScene.getLayerIndex(dynamic) === -1 && thisScene.destroyLayer(5) === false;
                const replacement = thisScene.createLayer({ name: 'Dynamic' });
                result = initial && sortedResult === true && sorted && typedLookup && numericSort && appended && destroyed && removed &&
                    thisScene.getLayerCount() === 6 && thisScene.getLayerIndex(replacement) === 5 ? 1 : -1;
            }
            export function update() { return result; }
        )JS",
        "test/public_layer_queries",
        FieldKind::Scalar,
        owe::NJson::object(),
        owe::NJson(0),
        reporter.as_ptr());
    ASSERT_NE(fs, nullptr);

    rt.SetSceneRoot(scene.RootMut().as_raw_ptr());
    rt.ClearLayerConfigFactory();
    rt.TickAll();

    EXPECT_DOUBLE_EQ(LastScalar(fs), 1.0);
    const auto& children = scene.Root()->GetChildren();
    ASSERT_EQ(children[usize()].as_ptr(), a.as_ptr());
    ASSERT_EQ(children[usize(1)].as_ptr(), one.as_ptr());
    ASSERT_EQ(children[usize(2)].as_ptr(), hidden.as_ptr());
    ASSERT_EQ(children[usize(3)].as_ptr(), reporter.as_ptr());
    EXPECT_FALSE(hidden->Visible());
    EXPECT_TRUE(std::any_of(offline.dependencies.begin(),
                            offline.dependencies.end(),
                            [](const owe::OfflineDependency& dependency) {
                                return dependency.owner == 13 && dependency.target == 9 &&
                                       dependency.operation == "lookup" &&
                                       dependency.property == "1";
                            }));
    EXPECT_TRUE(std::any_of(offline.dependencies.begin(),
                            offline.dependencies.end(),
                            [](const owe::OfflineDependency& dependency) {
                                return dependency.owner == 13 && dependency.target == 37 &&
                                       dependency.operation == "lookup" &&
                                       dependency.property == "0";
                            }));
    auto has_query = [&offline](const char* property) {
        return std::any_of(offline.dependencies.begin(),
                           offline.dependencies.end(),
                           [property](const owe::OfflineDependency& dependency) {
                               return dependency.operation == "query" &&
                                      dependency.property == property;
                           });
    };
    EXPECT_TRUE(has_query("layer_numeric_index"));
    EXPECT_TRUE(has_query("layer_count"));
    EXPECT_TRUE(has_query("layer_enumeration"));
    EXPECT_TRUE(has_query("layer_index"));
    EXPECT_TRUE(has_query("layer_order"));
    EXPECT_FALSE(std::any_of(offline.dependencies.begin(),
                             offline.dependencies.end(),
                             [](const owe::OfflineDependency& dependency) {
                                 return dependency.operation == "query" &&
                                        dependency.property == "layer_numeric_index" &&
                                        dependency.target == 9;
                             }));
}

TEST(ScriptScene, RegisteredAssetFactoryCreatesAndReusesDestroyedLayer) {
    auto                     root = Arc<owe::SceneNode>::make();
    Vec<Arc<owe::SceneNode>> created;

    JsRuntime rt;
    rt.SetLayerFactory(std::make_shared<JsRuntime::LayerFactory::element_type>(
        [&root, &created](owe::SceneNode*,
                          LayerAssetReference asset) -> Option<Arc<owe::SceneNode>> {
            if (asset.path != "models/prism.mdl"_str) return None();
            auto node = Arc<owe::SceneNode>::make();
            node->SetVisible(false);
            root->AppendChild(node.clone());
            created.push(node.clone());
            return Some(rstd::move(node));
        }));
    auto* fs = rt.MakeFieldScript(
        R"JS(
            const prism = engine.registerAsset('models/prism.mdl');
            let result = 0;
            export function init() {
                const layers = [];
                for (let i = 0; i < 12; ++i) {
                    const layer = thisScene.createLayer(prism);
                    layer.origin = new Vec3(i, 0, 0);
                    layers.push(layer);
                }
                thisScene.destroyLayer(layers[0]);
                const reused = thisScene.createLayer(prism);
                result = reused.origin.x === 0 ? 12 : -1;
            }
            export function update() { return result; }
        )JS",
        "test/registered_asset_factory",
        FieldKind::Scalar,
        owe::NJson::object(),
        owe::NJson(0),
        root.as_ptr());
    ASSERT_NE(fs, nullptr);
    ASSERT_EQ(fs->RegisteredAssets().len(), usize(1));

    rt.SetSceneRoot(root.as_ptr());
    rt.ClearLayerFactory();
    rt.TickAll();

    EXPECT_EQ(created.len(), usize(12));
    EXPECT_EQ(root->GetChildren().len(), usize(12));
    EXPECT_DOUBLE_EQ(LastScalar(fs), 12.0);
    EXPECT_TRUE(created[usize()]->Visible());
}

TEST(ScriptScene, DirectWorkshopAssetUsesLayerFactoryWithoutFixedCloneCapacity) {
    auto                     root = Arc<owe::SceneNode>::make();
    Vec<Arc<owe::SceneNode>> created;
    Vec<String>              paths;
    Vec<String>              workshop_ids;

    JsRuntime rt;
    rt.SetLayerFactory(std::make_shared<JsRuntime::LayerFactory::element_type>(
        [&root, &created, &paths, &workshop_ids](
            owe::SceneNode*, LayerAssetReference asset) -> Option<Arc<owe::SceneNode>> {
            paths.push(String::make(asset.path));
            if (asset.workshop_id.is_some()) workshop_ids.push(String::make(**asset.workshop_id));
            auto node = Arc<owe::SceneNode>::make();
            node->SetVisible(false);
            root->AppendChild(node.clone());
            created.push(node.clone());
            return Some(rstd::move(node));
        }));
    auto* fs = rt.MakeFieldScript(
        R"JS(
            export let __workshopId = '3365654061';
            let result = 0;
            export function init() {
                for (let i = 0; i < 127; ++i) {
                    const layer = thisScene.createLayer('models/cav_default_texture.json');
                    if (layer.visible) ++result;
                }
            }
            export function update() { return result; }
        )JS",
        "test/direct_workshop_asset_factory",
        FieldKind::Scalar,
        owe::NJson::object(),
        owe::NJson(0),
        root.as_ptr());
    ASSERT_NE(fs, nullptr);

    rt.SetSceneRoot(root.as_ptr());
    rt.ClearLayerFactory();
    rt.TickAll();

    ASSERT_EQ(created.len(), usize(127));
    ASSERT_EQ(paths.len(), usize(127));
    ASSERT_EQ(workshop_ids.len(), usize(127));
    EXPECT_EQ(paths[usize()], "models/cav_default_texture.json"_str);
    EXPECT_EQ(workshop_ids[usize()], "3365654061"_str);
    EXPECT_DOUBLE_EQ(LastScalar(fs), 127.0);
}

TEST(ScriptScene, ParticleInstanceAndPlaybackUseNodeCapability) {
    auto root  = Arc<owe::SceneNode>::make();
    auto layer = Arc<owe::SceneNode>::make();
    auto state = Arc<ParticleControlState>::make();
    layer->SetParticleControl(
        std::shared_ptr<owe::SceneParticleControl>(std::make_shared<ParticleControlProbe>(state.clone())));
    root->AppendChild(layer.clone());

    JsRuntime rt;
    auto*     fs = rt.MakeFieldScript(
        R"JS(
            export function init() {
                thisLayer.instance.colorn = new Vec3(0.2, 0.4, 0.8);
                thisLayer.stop();
                thisLayer.play();
            }
            export function update() {
                const color = thisLayer.instance.colorn;
                return color.x * 100 + color.y * 10 + color.z + (thisLayer.isPlaying() ? 1000 : 0);
            }
        )JS",
        "test/particle_instance_control",
        FieldKind::Scalar,
        owe::NJson::object(),
        owe::NJson(0),
        layer.as_ptr());
    ASSERT_NE(fs, nullptr);
    rt.SetSceneRoot(root.as_ptr());
    rt.TickAll();

    EXPECT_FLOAT_EQ(state->colorn[0], 0.2f);
    EXPECT_FLOAT_EQ(state->colorn[1], 0.4f);
    EXPECT_FLOAT_EQ(state->colorn[2], 0.8f);
    EXPECT_EQ(state->resets, 2);
    EXPECT_NEAR(LastScalar(fs), 1024.8, 1e-5);
}

TEST(SceneNodeSound, VisibilityStartsAndStopsTheSoundControl) {
    auto layer = Arc<owe::SceneNode>::make();
    auto state = Arc<SoundControlState>::make();
    layer->SetSoundControl(
        std::shared_ptr<owe::SceneSoundControl>(std::make_shared<SoundControlProbe>(state.clone())));
    ASSERT_TRUE(state->playing);

    layer->SetVisible(false);
    EXPECT_FALSE(state->playing);

    layer->SetVisible(true);
    EXPECT_TRUE(state->playing);
}

TEST(ScriptScene, SoundVolumeUsesSoundControl) {
    auto root  = Arc<owe::SceneNode>::make();
    auto layer = Arc<owe::SceneNode>::make();
    auto state = Arc<SoundControlState>::make();
    layer->SetSoundControl(
        std::shared_ptr<owe::SceneSoundControl>(std::make_shared<SoundControlProbe>(state.clone())));
    root->AppendChild(layer.clone());

    JsRuntime rt;
    auto*     fs = rt.MakeFieldScript(
        R"JS(
            export function init() {
                thisLayer.stop();
                thisLayer.volume = 0.25;
                thisLayer.play();
            }
            export function update() {
                return thisLayer.volume + (thisLayer.isPlaying() ? 1 : 0);
            }
        )JS",
        "test/sound_volume_control",
        FieldKind::Scalar,
        owe::NJson::object(),
        owe::NJson(0),
        layer.as_ptr());
    ASSERT_NE(fs, nullptr);
    rt.SetSceneRoot(root.as_ptr());
    rt.TickAll();

    EXPECT_FLOAT_EQ(state->volume, 0.25f);
    EXPECT_TRUE(state->playing);
    EXPECT_DOUBLE_EQ(LastScalar(fs), 1.25);
}

// ---------------------------------------------------------------------------
// Workshop 3327063360 repro: scripted-origin layer should land at canvas
// center when scriptProperties.{x,y} fall back to their declared 0.5.

TEST(ScriptUserProperty, UserPropertyOverridesFallback) {
    // ResolveConfigValue stores the {user, value} wrapper verbatim; the
    // bootstrap getter unwraps at access time. SetUserProperty in
    // between should win.
    JsRuntime rt;
    owe::NJson properties = owe::ParseNJson(R"({"x":{"user":"x1","value":0.5}})").unwrap();
    rt.SetUserProperty("x1",
                       owe::ParseNJson(R"({"type":"slider","value":-0.665})").unwrap());
    FrameInputs fi {};
    fi.canvas_w = 3840.0f;
    fi.canvas_h = 2160.0f;
    rt.SetFrameInputs(fi);
    auto* fs = rt.MakeFieldScript(
        R"JS(
            export var scriptProperties = createScriptProperties()
              .addSlider({ name: 'x', value: 0.5, min: 0, max: 1 })
              .finish();
            export function update() { return scriptProperties.x; }
        )JS",
        "test/user_prop_override",
        FieldKind::Scalar,
        properties,
        owe::NJson(0),
        nullptr);
    ASSERT_NE(fs, nullptr);

    rt.TickAll();
    // User value passes through verbatim — WE doesn't clamp, even when the
    // user's slider range (e.g. project.json [-1,1]) exceeds the script's
    // declared range. Workshop 3327063360 relies on this: x1=-0.665 fed
    // into `scriptProperties.x * canvasSize.x` produces a negative offset
    // that shifts the Clock cluster off the master-component origin.
    EXPECT_NEAR(std::get<ScalarValue>(fs->last_value()).v, -0.665, 1e-4);
}

TEST(ScriptUserProperty, FallbackWhenUserPropMissing) {
    JsRuntime rt;
    owe::NJson properties =
        owe::ParseNJson(R"({"x":{"user":"missing","value":0.5}})").unwrap();
    FrameInputs fi {};
    fi.canvas_w = 3840.0f;
    fi.canvas_h = 2160.0f;
    rt.SetFrameInputs(fi);
    auto* fs = rt.MakeFieldScript(
        R"JS(
            export var scriptProperties = createScriptProperties()
              .addSlider({ name: 'x', value: 0.5, min: 0, max: 1 })
              .finish();
            export function update() { return scriptProperties.x; }
        )JS",
        "test/user_prop_fallback",
        FieldKind::Scalar,
        properties,
        owe::NJson(0),
        nullptr);
    ASSERT_NE(fs, nullptr);

    rt.TickAll();
    EXPECT_NEAR(std::get<ScalarValue>(fs->last_value()).v, 0.5, 1e-4);
}

TEST(ScriptUserProperty, ApplyUserPropertiesReceivesUnwrappedValue) {
    JsRuntime   rt;
    FrameInputs fi {};
    rt.SetFrameInputs(fi);
    auto* fs = MakeProbe(rt,
                         "test/apply_user_properties_value",
                         R"JS(
        let seen = 0;
        export function applyUserProperties(changed) {
            if (changed.music === "5") seen = 1;
        }
        export function update() { return seen; }
    )JS");
    ASSERT_NE(fs, nullptr);

    rt.SetUserProperty("music",
                       owe::ParseNJson(R"({"type":"combo","value":"5"})").unwrap());
    rt.TickAll();
    EXPECT_EQ(std::get<ScalarValue>(fs->last_value()).v, 1.0);
}

TEST(ScriptUserProperty, DirectReadsReceiveUpdatedComboValue) {
    JsRuntime   rt;
    FrameInputs fi {};
    rt.SetFrameInputs(fi);
    auto* fs = MakeProbe(rt,
                         "test/direct_combo_user_property",
                         R"JS(
        export function update() {
            if (engine.userProperties.timeofday != 0) {
                return engine.userProperties.timeofday - 1;
            }
            return -1;
        }
    )JS");
    ASSERT_NE(fs, nullptr);

    rt.SetUserProperty("timeofday",
                       owe::ParseNJson(R"({"type":"combo","value":"1"})").unwrap());
    rt.TickAll();
    EXPECT_EQ(std::get<ScalarValue>(fs->last_value()).v, 0.0);

    rt.SetUserProperty("timeofday",
                       owe::ParseNJson(R"({"type":"combo","value":"2"})").unwrap());
    rt.TickAll();
    EXPECT_EQ(std::get<ScalarValue>(fs->last_value()).v, 1.0);
}

TEST(ScriptUserProperty, TextInputValueRemainsAString) {
    JsRuntime   rt;
    FrameInputs fi {};
    rt.SetFrameInputs(fi);
    auto* fs = MakeProbe(rt,
                         "test/apply_text_user_property",
                         R"JS(
        let seen = 0;
        export function applyUserProperties(changed) {
            if (changed.text === "true" && typeof changed.text === "string") seen = 1;
        }
        export function update() { return seen; }
    )JS");
    ASSERT_NE(fs, nullptr);

    rt.SetUserProperty("text",
                       owe::ParseNJson(R"({"type":"textinput","value":"true"})").unwrap());
    rt.TickAll();
    EXPECT_EQ(std::get<ScalarValue>(fs->last_value()).v, 1.0);
}

TEST(ScriptMedia, DispatchesPropertiesPlaybackAndThumbnailEvents) {
    JsRuntime   rt;
    FrameInputs fi {};
    rt.SetFrameInputs(fi);
    auto* fs = MakeProbe(rt,
                         "test/media_events",
                         R"JS(
        let props = 0, playback = 0, thumb = 0;
        export function mediaPropertiesChanged(event) {
            if (event.title === "Song" && event.artist === "Artist" &&
                event.album === "Album" && event.albumTitle === "Album" &&
                event.albumArtist === "Album Artist") {
                props = 1;
            }
        }
        export function mediaPlaybackChanged(event) {
            if (event.state === MediaPlaybackEvent.PLAYBACK_PLAYING) playback = 1;
        }
        export function mediaThumbnailChanged(event) {
            thisObject.visible = event.hasThumbnail;
            const colorDelta = event.textColor.subtract(event.tertiaryColor)
                .add(event.highContrastColor);
            if (event.hasThumbnail && event.thumbnail === "/tmp/cover.png" &&
                event.previousThumbnail === "/tmp/previous.png" &&
                event.primaryColor.x === 1 && event.secondaryColor.x === 0 &&
                event.tertiaryColor.x === 0 && colorDelta.lengthSqr() === 0) {
                thumb = 1;
            }
        }
        export function update() { return props && playback && thumb && thisObject.visible ? 1 : 0; }
    )JS");
    ASSERT_NE(fs, nullptr);

    rt.SetMediaStatus(MediaStatus { .state            = 1,
                                    .title            = "Song",
                                    .artist           = "Artist",
                                    .album            = "Album",
                                    .album_artist     = "Album Artist",
                                    .art_url          = "/tmp/cover.png",
                                    .previous_art_url = "/tmp/previous.png" });
    rt.TickAll();
    EXPECT_EQ(std::get<ScalarValue>(fs->last_value()).v, 1.0);
}

TEST(ScriptUserProperty, ScriptedOriginLandsAtCenter) {
    JsRuntime   rt;
    FrameInputs fi {};
    fi.canvas_w = 3840.0f;
    fi.canvas_h = 2160.0f;
    rt.SetFrameInputs(fi);

    owe::NJson properties =
        owe::ParseNJson(R"({"x":{"user":"x7","value":0.5},"y":{"user":"y8","value":0.5}})")
            .unwrap();

    auto* fs = rt.MakeFieldScript(
        R"JS(
            'use strict';
            export var scriptProperties = createScriptProperties()
              .addSlider({ name: 'x', label: 'X', value: 0.5, min: 0, max: 1 })
              .addSlider({ name: 'y', label: 'Y', value: 0.5, min: 0, max: 1 })
              .finish();
            export function update(value) {
                value.x = scriptProperties.x * engine.canvasSize.x;
                value.y = scriptProperties.y * engine.canvasSize.y;
                return value;
            }
        )JS",
        "test/workshop_3327_repro",
        FieldKind::Vec3,
        properties,
        owe::NJson("1315.0 1419.0 0.0"),
        nullptr);
    ASSERT_NE(fs, nullptr);

    rt.TickAll();
    ASSERT_TRUE(std::holds_alternative<Vec3Value>(fs->last_value()));
    const auto& v = std::get<Vec3Value>(fs->last_value());
    EXPECT_NEAR(v.x, 1920.0, 0.5);
    EXPECT_NEAR(v.y, 1080.0, 0.5);
}

TEST(SceneAnimationPlayback, SharedFieldBindingsAdvanceOnceAndPreserveEventOrder) {
    owe::Scene scene;
    auto       node = Arc<owe::SceneNode>::make();

    Vec<owe::SceneAnimationEvent> authored_events;
    authored_events.push(
        { .frame = i32(2), .order = usize(1), .name = String::make("second"_str) });
    authored_events.push({ .frame = i32(2), .order = usize(), .name = String::make("first"_str) });
    auto clip = Arc<owe::SceneAnimationClip>::make(owe::SceneAnimationClipSpec {
        .events = rstd::move(authored_events),
        .mode   = String::make("loop"_str),
        .fps    = 10.0f,
        .end    = i32(10),
    });

    auto playback = Arc<owe::SceneAnimationPlayback>::make(rstd::move(clip), true);
    node->BindFieldAnimation(String::make("origin"_str), playback.clone());
    node->BindFieldAnimation(String::make("alpha"_str), playback.clone());
    auto peer = Arc<owe::SceneNode>::make();
    peer->BindFieldAnimation(String::make("amount"_str), playback.clone());
    scene.RootMut()->AppendChild(node.clone());
    scene.RootMut()->AppendChild(peer.clone());

    scene.Runtime().Advance(rstd::f64(0.2));
    scene.TickNodeFieldAnimations();
    EXPECT_TRUE(scene.ConsumeAnimationEvents().is_empty());
    EXPECT_EQ(playback->Frame(), i32());

    playback->Play();
    scene.Runtime().Advance(rstd::f64(0.2));
    scene.TickNodeFieldAnimations();
    auto events = scene.ConsumeAnimationEvents();
    ASSERT_EQ(events.len(), usize(2));
    EXPECT_EQ(events[usize()].event.name, "first"_str);
    EXPECT_EQ(events[usize()].event.frame, i32(2));
    EXPECT_EQ(events[usize(1)].event.name, "second"_str);
    EXPECT_EQ(events[usize(1)].event.frame, i32(2));
    EXPECT_EQ(playback->Frame(), i32(2));

    playback->Pause();
    scene.Runtime().Advance(rstd::f64(0.3));
    scene.TickNodeFieldAnimations();
    EXPECT_EQ(playback->Frame(), i32(2));

    playback->SetFrame(i32(4));
    playback->SetRate(2.0f);
    playback->Play();
    scene.Runtime().Advance(rstd::f64(0.1));
    scene.TickNodeFieldAnimations();
    EXPECT_EQ(playback->Frame(), i32(6));

    playback->SetFrame(i32(10));
    scene.Runtime().Advance(rstd::f64(0.1));
    scene.TickNodeFieldAnimations();
    EXPECT_EQ(playback->Frame(), i32(2));
}

TEST(ScriptAnimation, BroadcastsMarkersAndControlsSharedPlayback) {
    owe::SceneNode layer;
    owe::SceneNode other_layer;

    auto clip     = Arc<owe::SceneAnimationClip>::make(owe::SceneAnimationClipSpec {
        .name = String::make("main"_str),
        .mode = String::make("loop"_str),
        .fps  = 6.0f,
        .end  = i32(12),
    });
    auto playback = Arc<owe::SceneAnimationPlayback>::make(rstd::move(clip));

    auto secondary_clip = Arc<owe::SceneAnimationClip>::make(owe::SceneAnimationClipSpec {
        .name = String::make("secondary"_str),
        .mode = String::make("loop"_str),
        .fps  = 8.0f,
        .end  = i32(16),
    });
    auto secondary      = Arc<owe::SceneAnimationPlayback>::make(rstd::move(secondary_clip));
    layer.BindFieldAnimation(String::make("scale"_str), secondary.clone());
    layer.BindFieldAnimation(String::make("origin"_str), playback.clone());

    JsRuntime rt;
    auto*     controller = rt.MakeFieldScript(
        R"JS(
            const animation = thisObject.getAnimation();
            const secondary = thisLayer.getAnimation("secondary");
            export function animationEvent(event, value) {
                return event.name === "beat" ? value + event.frame : -1000;
            }
            export function update(value) {
                animation.setFrame(4);
                animation.rate = 2;
                animation.pause();
                secondary.setFrame(3);
                secondary.pause();
                const metadata = animation.name === "main" && animation.fps === 6 &&
                    animation.frameCount === 12 && animation.duration === 2 &&
                    secondary.name === "secondary" ? 100 : 0;
                return value + animation.getFrame() + animation.rate + secondary.getFrame() + metadata;
            }
        )JS",
        "test/animation_controller",
        FieldKind::Scalar,
        owe::NJson::object(),
        owe::NJson(0),
        ScriptBindingContext::ForLayer(&layer, "origin"_str, Some(playback.clone())));
    auto* peer = rt.MakeFieldScript(
        R"JS(
            export function animationEvent(event) { return event.frame + 1; }
            export function update(value) { return value; }
        )JS",
        "test/animation_peer",
        FieldKind::Scalar,
        owe::NJson::object(),
        owe::NJson(0),
        &layer);
    auto* unrelated = rt.MakeFieldScript(
        R"JS(
            export function animationEvent() { return 99; }
            export function update(value) { return value; }
        )JS",
        "test/animation_unrelated",
        FieldKind::Scalar,
        owe::NJson::object(),
        owe::NJson(0),
        &other_layer);
    ASSERT_NE(controller, nullptr);
    ASSERT_NE(peer, nullptr);
    ASSERT_NE(unrelated, nullptr);

    Vec<owe::SceneAnimationEventDispatch> events;
    events.push(
        { .node  = &layer,
          .event = { .frame = i32(3), .order = usize(), .name = String::make("beat"_str) } });
    rt.TickAll(events.as_slice());

    EXPECT_EQ(LastScalar(controller), 112.0);
    EXPECT_EQ(LastScalar(peer), 4.0);
    EXPECT_EQ(LastScalar(unrelated), 0.0);
    EXPECT_EQ(playback->Frame(), i32(4));
    EXPECT_FLOAT_EQ(playback->Rate(), 2.0f);
    EXPECT_FALSE(playback->IsPlaying());
    EXPECT_EQ(secondary->Frame(), i32(3));
    EXPECT_FALSE(secondary->IsPlaying());
}

TEST(ScriptAnimation, SeparatesLayerAndCurrentPropertyLookup) {
    owe::SceneNode layer;
    auto           clip     = Arc<owe::SceneAnimationClip>::make(owe::SceneAnimationClipSpec {
        .name = String::make("layer-track"_str),
        .fps  = 12.0f,
        .end  = i32(24),
    });
    auto           playback = Arc<owe::SceneAnimationPlayback>::make(rstd::move(clip));
    layer.BindFieldAnimation(String::make("origin"_str), playback.clone());

    JsRuntime rt;
    auto*     unanimated = rt.MakeFieldScript(
        R"JS(
            const propertyAnimation = thisObject.getAnimation();
            const layerAnimation = thisLayer.getAnimation("layer-track");
            export function update() {
                return thisObject !== thisLayer && propertyAnimation === undefined &&
                    layerAnimation.frameCount === 24 ? 1 : 0;
            }
        )JS",
        "test/animation_property_scope",
        FieldKind::Scalar,
        owe::NJson::object(),
        owe::NJson(0),
        ScriptBindingContext::ForLayer(&layer, "alpha"_str));
    ASSERT_NE(unanimated, nullptr);

    owe::SceneMaterial material;
    auto*              material_property = rt.MakeFieldScript(
        R"JS(
            const animation = thisObject.getAnimation();
            export function update() {
                return thisObject !== thisLayer && animation.name === "layer-track" ? 1 : 0;
            }
        )JS",
        "test/animation_material_property_scope",
        FieldKind::Scalar,
        owe::NJson::object(),
        owe::NJson(0),
        ScriptBindingContext::ForMaterial(&layer, &material, "amount"_str, Some(playback.clone())));
    ASSERT_NE(material_property, nullptr);

    auto* scene_property = rt.MakeFieldScript(
        R"JS(
            const animation = thisObject.getAnimation();
            export function update() {
                return thisObject !== thisLayer && animation.name === "layer-track" ? 1 : 0;
            }
        )JS",
        "test/animation_scene_property_scope",
        FieldKind::Scalar,
        owe::NJson::object(),
        owe::NJson(0),
        ScriptBindingContext::ForLayer(nullptr, "camerashake"_str, Some(playback.clone())));
    ASSERT_NE(scene_property, nullptr);

    rt.TickAll();
    EXPECT_EQ(LastScalar(unanimated), 1.0);
    EXPECT_EQ(LastScalar(material_property), 1.0);
    EXPECT_EQ(LastScalar(scene_property), 1.0);
}

TEST(ScriptAnimation, TracesCachedPlaybackControlsAndInitialization) {
    for (const char* operation : { "play()", "pause()", "stop()", "setFrame(4)", "rate = 2" }) {
        owe::OfflineExecutionContext offline;
        offline.trace_scene = true;
        owe::OfflineExecutionScope scope(offline);
        owe::SceneNode layer;
        layer.SetGeneratorIdentity(Some(owe::WallpaperLayerId { .value = i32(711) }));
        auto clip = Arc<owe::SceneAnimationClip>::make(owe::SceneAnimationClipSpec {
            .name = String::make("controlled"_str), .mode = String::make("loop"_str),
            .fps = 6.0f, .end = i32(12),
        });
        auto playback = Arc<owe::SceneAnimationPlayback>::make(rstd::move(clip));
        JsRuntime rt;
        std::string code = "let animation; let ticks = 0; export function init(value) { "
                           "animation = thisObject.getAnimation(); animation.";
        code += operation;
        code += "; return value; } export function update(value) { if (ticks++ > 0) { animation.";
        code += operation;
        code += "; } return value; }";
        auto* script = rt.MakeFieldScript(
            code, "test/animation_control_trace", FieldKind::Scalar,
            owe::NJson::object(), owe::NJson(0),
            ScriptBindingContext::ForLayer(&layer, "origin"_str, Some(playback.clone())));
        ASSERT_NE(script, nullptr);
        auto has_write = [&](bool initialization) {
            return std::any_of(offline.dependencies.begin(), offline.dependencies.end(),
                               [initialization](const owe::OfflineDependency& dependency) {
                                   return dependency.owner == 711 && dependency.target == 711 &&
                                          dependency.operation == "write" &&
                                          dependency.property == "animation" &&
                                          dependency.initialization == initialization;
                               });
        };
        rt.SetSceneRoot(&layer); // Runs the deferred init before the first update.
        rt.TickAll();
        EXPECT_TRUE(has_write(true));
        EXPECT_FALSE(has_write(false));
        if (std::string_view(operation) == "setFrame(4)") {
            EXPECT_EQ(playback->Frame(), i32(4));
        }
        rt.TickAll();
        EXPECT_TRUE(has_write(false));
        EXPECT_FALSE(offline.failed);
    }
}

TEST(ScriptAnimation, LayerLookupUsesTargetFieldAndCallingBinding) {
    owe::OfflineExecutionContext offline;
    offline.trace_scene = true;
    owe::OfflineExecutionScope   scope(offline);
    auto target = Arc<owe::SceneNode>::make(Eigen::Vector3f::Zero(),
                                            Eigen::Vector3f::Ones(),
                                            Eigen::Vector3f::Zero(),
                                            "TimelineWhite");
    auto reporter = Arc<owe::SceneNode>::make(Eigen::Vector3f::Zero(),
                                               Eigen::Vector3f::Ones(),
                                               Eigen::Vector3f::Zero(),
                                               "Reporter");
    target->SetGeneratorIdentity(Some(owe::WallpaperLayerId { .value = i32(712) }));
    reporter->SetGeneratorIdentity(Some(owe::WallpaperLayerId { .value = i32(713) }));
    auto alpha_clip = Arc<owe::SceneAnimationClip>::make(owe::SceneAnimationClipSpec {
        .name = String::make("namedAlpha"_str), .fps = 1.0f, .end = i32(90),
    });
    auto origin_clip = Arc<owe::SceneAnimationClip>::make(owe::SceneAnimationClipSpec {
        .name = String::make("namedMove"_str), .fps = 1.0f, .end = i32(2),
    });
    auto alpha  = Arc<owe::SceneAnimationPlayback>::make(rstd::move(alpha_clip));
    auto origin = Arc<owe::SceneAnimationPlayback>::make(rstd::move(origin_clip));
    target->BindFieldAnimation(String::make("alpha"_str), alpha.clone());
    target->BindFieldAnimation(String::make("origin"_str), origin.clone());

    auto root = Arc<owe::SceneNode>::make();
    root->AppendChild(target.clone());
    root->AppendChild(reporter.clone());
    JsRuntime rt;
    auto* alpha_script = rt.MakeFieldScript(
        R"JS(export function update() {
            const layer = thisLayer.getAnimation();
            const object = thisObject.getAnimation();
            const byAlpha = thisLayer.getAnimation('alpha');
            const byOrigin = thisLayer.getAnimation('origin');
            return layer && object && byAlpha && byOrigin &&
                layer.duration === 90 && object.duration === 90 &&
                byAlpha.duration === 90 && byOrigin.duration === 2 &&
                thisLayer.getAnimation('Move') === undefined &&
                thisLayer.getAnimation('definitely-missing') === undefined ? 1 : 0;
        })JS",
        "test/animation_layer_alpha",
        FieldKind::Scalar,
        owe::NJson::object(),
        owe::NJson(0),
        ScriptBindingContext::ForLayer(target.as_ptr(), "alpha"_str, Some(alpha.clone())));
    auto* origin_script = rt.MakeFieldScript(
        R"JS(export function update() {
            return thisLayer.getAnimation().duration === 2 &&
                thisObject.getAnimation().duration === 2 &&
                thisLayer.getAnimation('alpha').duration === 90 &&
                thisLayer.getAnimation('origin').duration === 2 ? 1 : 0;
        })JS",
        "test/animation_layer_origin",
        FieldKind::Scalar,
        owe::NJson::object(),
        owe::NJson(0),
        ScriptBindingContext::ForLayer(target.as_ptr(), "origin"_str, Some(origin.clone())));
    auto* cross_owner_alpha = rt.MakeFieldScript(
        R"JS(let animation;
        export function init() {
            animation = thisScene.getLayer('TimelineWhite').getAnimation('alpha');
        }
        export function update() {
            animation.play();
            const target = thisScene.getLayer('TimelineWhite');
            return thisLayer.getAnimation() === undefined &&
                target.getAnimation().duration === 90 ? 1 : 0;
        })JS",
        "test/animation_layer_cross_owner_alpha",
        FieldKind::Scalar,
        owe::NJson::object(),
        owe::NJson(0),
        ScriptBindingContext::ForLayer(reporter.as_ptr(), "alpha"_str));
    auto* reporter_script = rt.MakeFieldScript(
        R"JS(export function update() {
            const target = thisScene.getLayer('TimelineWhite');
            return thisLayer.getAnimation() === undefined &&
                thisObject.getAnimation() === undefined &&
                thisLayer.getAnimation('alpha') === undefined &&
                target.getAnimation() === undefined ? 1 : 0;
        })JS",
        "test/animation_layer_cross_owner_text",
        FieldKind::Scalar,
        owe::NJson::object(),
        owe::NJson(0),
        ScriptBindingContext::ForLayer(reporter.as_ptr(), "text"_str));
    ASSERT_NE(alpha_script, nullptr);
    ASSERT_NE(origin_script, nullptr);
    ASSERT_NE(cross_owner_alpha, nullptr);
    ASSERT_NE(reporter_script, nullptr);
    rt.SetSceneRoot(root.as_ptr());
    rt.TickAll();
    EXPECT_EQ(LastScalar(alpha_script), 1.0);
    EXPECT_EQ(LastScalar(origin_script), 1.0);
    EXPECT_EQ(LastScalar(cross_owner_alpha), 1.0);
    EXPECT_EQ(LastScalar(reporter_script), 1.0);
    EXPECT_TRUE(std::any_of(offline.dependencies.begin(), offline.dependencies.end(),
                            [](const owe::OfflineDependency& dependency) {
                                return dependency.owner == 713 && dependency.target == 712 &&
                                       dependency.operation == "write" &&
                                       dependency.property == "animation" &&
                                       !dependency.initialization;
                            }));
    EXPECT_FALSE(offline.failed);
}

TEST(ScriptAnimation, TimerKeepsCurrentPropertyAnimation) {
    owe::SceneNode layer;
    auto           clip     = Arc<owe::SceneAnimationClip>::make(owe::SceneAnimationClipSpec {
        .name = String::make("timer-track"_str),
        .fps  = 10.0f,
        .end  = i32(20),
    });
    auto           playback = Arc<owe::SceneAnimationPlayback>::make(rstd::move(clip));

    JsRuntime rt;
    auto*     script = rt.MakeFieldScript(
        R"JS(
            let observed = 0;
            export function init(value) {
                setTimeout(() => {
                    const animation = thisObject.getAnimation();
                    observed = animation.name === "timer-track" ? animation.frameCount : -1;
                }, 10);
                return value;
            }
            export function update() { return observed; }
        )JS",
        "test/animation_timer_property_scope",
        FieldKind::Scalar,
        owe::NJson::object(),
        owe::NJson(0),
        ScriptBindingContext::ForLayer(&layer, "origin"_str, Some(rstd::move(playback))));
    ASSERT_NE(script, nullptr);
    rt.SetSceneRoot(&layer);

    FrameInputs inputs {};
    inputs.runtime = 0.02f;
    rt.SetFrameInputs(inputs);
    rt.TickAll();
    EXPECT_EQ(LastScalar(script), 20.0);
}

TEST(SceneNodeTrans, SetTranslateRecomputesModelTrans) {
    owe::SceneNode parent;
    parent.SetTranslate({ 100.0f, 200.0f, 0.0f });
    auto child = rstd::sync::Arc<owe::SceneNode>::make();
    child->SetTranslate({ 10.0f, 20.0f, 0.0f });
    parent.AppendChild(child.clone());

    child->UpdateTrans();
    Eigen::Matrix4d m1 = child->ModelTrans();
    EXPECT_DOUBLE_EQ(m1(0, 3), 110.0); // world x
    EXPECT_DOUBLE_EQ(m1(1, 3), 220.0); // world y

    // Mutate the parent and re-read the child without explicit dirty.
    parent.SetTranslate({ 500.0f, 600.0f, 0.0f });
    child->UpdateTrans();
    Eigen::Matrix4d m2 = child->ModelTrans();
    EXPECT_DOUBLE_EQ(m2(0, 3), 510.0);
    EXPECT_DOUBLE_EQ(m2(1, 3), 620.0);
}

TEST(SceneNodeTrans, SetScaleAndRotationMarkDirty) {
    owe::SceneNode n;
    n.UpdateTrans();
    // After first UpdateTrans the cache is clean.
    n.SetScale({ 2.0f, 2.0f, 1.0f });
    n.UpdateTrans();
    Eigen::Matrix4d m = n.ModelTrans();
    EXPECT_DOUBLE_EQ(m(0, 0), 2.0);
    EXPECT_DOUBLE_EQ(m(1, 1), 2.0);
}

TEST(ScriptNodeSize, UnsetFallsBackTo100x100) {
    owe::SceneNode node; // m_size defaults to (0,0)
    JsRuntime      rt;
    FrameInputs    fi {};
    rt.SetFrameInputs(fi);
    auto* fs = rt.MakeFieldScript(
        R"JS(
            export function update() { return thisLayer.size.x + thisLayer.size.y * 1000; }
        )JS",
        "test/node_size_unset",
        FieldKind::Scalar,
        owe::NJson::object(),
        owe::NJson(0),
        &node);
    ASSERT_NE(fs, nullptr);

    rt.TickAll();
    EXPECT_EQ(std::get<ScalarValue>(fs->last_value()).v, 100.0 + 100.0 * 1000);
}

TEST(ScriptTimer, ClearIntervalStops) {
    JsRuntime   rt;
    FrameInputs fi {};
    rt.SetFrameInputs(fi);
    auto* fs = MakeProbe(rt,
                         "test/clear_interval",
                         R"JS(
        let n = 0;
        let h = setInterval(() => { n++; }, 100);
        export function update() {
            if (n >= 2) clearInterval(h);
            return n;
        }
    )JS");
    ASSERT_NE(fs, nullptr);

    Tick(rt, 0.25); // fires at 0.1, 0.2 → n=2
    EXPECT_EQ(std::get<ScalarValue>(fs->last_value()).v, 2.0);

    Tick(rt, 1.50); // would have fired many more, but update cleared it
    EXPECT_EQ(std::get<ScalarValue>(fs->last_value()).v, 2.0);
}
