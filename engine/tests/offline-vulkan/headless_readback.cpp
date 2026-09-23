#include <cmath>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <fstream>

#include <new> // wescene.json 的全局模块片段带进 <new>，这里显式包含，免得与隐式 operator new 冲突
#include "JsonNlohmann.hpp"

import rstd;
import rstd.cppstd;
import wescene.json;
import wescene.pkg.parse;
import wescene.scene;
import wescene.script;
import wescene.resource_registry;
import wescene.rgraph;
import wescene.vulkan;
import wescene.vulkan_render;
import wescene.offline_session;

using namespace rstd::prelude;
using namespace rstd::literals;
using namespace owe::vulkan;

namespace {

// Diagnostic runner using the public session API, not a new production job contract.
bool RunPropertyReplay(const char* path) {
    try {
        std::ifstream input(std::filesystem::u8path(path), std::ios::binary);
        const std::string text(std::istreambuf_iterator<char>(input), {});
        auto parsed = owe::ParseNJson(text);
        if (parsed.is_err()) throw std::runtime_error("invalid replay JSON");
        auto job = parsed.unwrap();
        auto field = [&](const char* name) -> const owe::NJson& {
            const auto* value = owe::Find(job, name);
            if (value == nullptr) throw std::runtime_error(std::string("missing replay field: ") + name);
            return *value;
        };
        auto string_field = [&](const char* name) {
            std::string value;
            if (!owe::GetJsonValue(field(name), value)) throw std::runtime_error(name);
            return value;
        };
        const auto output = std::filesystem::u8path(string_field("output_dir"));
        if (std::filesystem::exists(output)) throw std::runtime_error("replay output already exists");
        std::filesystem::create_directories(output);
        owe::SessionConfig config;
        config.source_pkg_path = string_field("source");
        config.assets_dir = string_field("assets");
        config.cache_dir = (output / "cache").string();
        const auto& properties = field("user_properties");
        if (!properties.is_object()) throw std::runtime_error("properties must be an object");
        config.user_properties = properties;
        owe::RenderInitInfo info;
        info.output_mode = owe::RenderOutputMode::CpuReadback;
        info.width = 640;
        info.height = 360;
        owe::OfflineOptions options;
        options.seed = 17;
        options.fps_num = 30;
        options.fps_den = 1;
        options.trace_scene = true;
        owe::OfflineSession wallpaper;
        if (!wallpaper.init(std::move(config), std::move(info), options))
            throw std::runtime_error(wallpaper.error());
        const auto& events = field("events");
        if (!events.is_array()) throw std::runtime_error("events must be an array");
        if (events.size() != 6) throw std::runtime_error("replay requires six bounded property events");
        std::ofstream raw(output / "frames.rgba", std::ios::binary);
        owe::OfflineFrameInput pointer;
        pointer.cursor_x = pointer.cursor_y = 0.5;
        pointer.cursor_in_window = true;
        for (uint64_t frame = 0; frame < 144; ++frame) {
            if (frame % 24 == 0) {
                const auto& event = events[frame / 24];
                if (!event.is_object()) throw std::runtime_error("event must be an object");
                for (const auto& [key, value] : event.items()) wallpaper.setUserProperty(key, value);
            }
            if (!wallpaper.step(frame, 1.0 / 30, pointer)) throw std::runtime_error(wallpaper.error());
            const auto& pixels = wallpaper.readback();
            if (!pixels.completed() || pixels.width != 640 || pixels.height != 360 || pixels.row_pitch != 640*4)
                throw std::runtime_error("unexpected replay frame");
            raw.write(reinterpret_cast<const char*>(pixels.pixels.data()), pixels.pixels.size());
            if (frame % 24 == 0 || frame % 24 == 23)
                std::ofstream(output / (std::to_string(frame) + ".scene.json")) << wallpaper.sceneDescription();
        }
        if (!raw) throw std::runtime_error("write replay frames failed");
        std::printf("PASS: one session, 144 frames, six property events; source_script_errors=%zu\n",
                    wallpaper.sourceScriptErrors().size());
        return true;
    } catch (const std::exception& error) {
        std::fprintf(stderr, "FAIL: property replay: %s\n", error.what());
        return false;
    }
}

unsigned Pattern(unsigned x, unsigned y, unsigned channel, unsigned frame) {
    return (x * 29 + y * 17 + channel * 41 + frame * 13) % 256;
}

bool RunAnimationLayerBinding() {
    auto puppet = rstd::sync::Arc<owe::Puppet>::make();
    for (const auto& spec : std::array<std::pair<rstd::int32_t, const char*>, 2> {
             std::pair { rstd::int32_t(701), "first" },
             std::pair { rstd::int32_t(702), "second" } }) {
        auto& animation  = puppet->anims.emplace_back();
        animation.id     = spec.first;
        animation.name   = String::make(rstd::cppstd::as_str(spec.second).unwrap());
        animation.mode   = owe::Puppet::PlayMode::Loop;
        animation.fps    = 10.0;
        animation.length = 20;
    }
    puppet->prepared();

    std::array<owe::PuppetLayer::AnimationLayer, 2> authored {
        owe::PuppetLayer::AnimationLayer { .id = 701, .layer_id = 9001 },
        owe::PuppetLayer::AnimationLayer { .id = 702, .layer_id = 9002 },
    };
    auto layers = rstd::sync::Arc<owe::PuppetLayer>::make(puppet.clone(), nullptr);
    layers->prepared(slice<owe::PuppetLayer::AnimationLayer>::from_raw_parts(authored.data(),
                                                                             usize(2)));
    auto first  = layers->AnimationPlayback(9001);
    auto second = layers->AnimationPlayback(9002);
    if (first.is_none() || second.is_none()) {
        std::fprintf(stderr, "FAIL: authored animation layer IDs did not resolve distinct playbacks\n");
        return false;
    }

    owe::SceneNode           owner;
    owe::script::ScriptScene scripts;
    auto&                    runtime = scripts.runtime();
    auto make_script = [&](const char* sha, const char* source, bool initial,
                           rstd::int32_t layer_id,
                           rstd::sync::Arc<owe::SceneAnimationPlayback> playback) {
        return runtime.MakeFieldScript(
            source,
            sha,
            owe::script::FieldKind::Bool,
            owe::NJson(),
            owe::NJson(initial),
            owe::script::ScriptBindingContext::ForAnimationLayer(
                &owner, layers.clone(), layer_id, "visible"_str, rstd::move(playback)));
    };
    auto* first_script = make_script(
        "test/puppet_layer_first",
        R"JS(export function init(value) {
            const animation = thisObject.getAnimation();
            animation.play(); animation.setFrame(animation.frameCount * 0.2);
            thisObject.blend = 0.2; return value;
        })JS",
        false,
        9001,
        (*first).clone());
    auto* second_script = make_script(
        "test/puppet_layer_second",
        R"JS(export function init(value) {
            const animation = thisObject.getAnimation();
            animation.play(); animation.setFrame(animation.frameCount * 0.75);
            thisObject.blend = 0.75; return value;
        })JS",
        false,
        9002,
        (*second).clone());
    if (first_script == nullptr || second_script == nullptr) {
        std::fprintf(stderr, "FAIL: animation-layer field scripts did not compile\n");
        return false;
    }
    scripts.AddActuator(
        { first_script,
          [layer = layers.as_ptr()](const owe::script::ScriptValue& value) {
              if (auto* visible = std::get_if<owe::script::BoolValue>(&value))
                  (void)layer->SetAnimationLayerVisible(9001, visible->v);
          } });
    owe::Scene user_scene;
    owe::SceneUserVisibilityBinding user_binding {
        .key = String::make("breathing"_str),
    };
    user_scene.RegisterUserPropertyBinding(
        user_binding.key.clone(),
        std::function<void(ref<owe::NJson>)>(
            [layer = layers.as_ptr(), binding = &user_binding](ref<owe::NJson> property) {
                auto visible = owe::ResolveSceneUserVisibilityBinding(*binding, *property);
                if (visible.is_some())
                    (void)layer->SetAnimationLayerVisible(9002, *visible);
            }));
    auto false_property = owe::NJson(false);
    const bool initial_false_applied =
        user_scene.ApplyUserPropertyBindings("breathing"_str, false_property);

    runtime.SetSceneRoot(&owner);
    scripts.Tick(owe::script::FrameInputs {});
    const bool initial_false_preserved = initial_false_applied &&
        layers->AnimationLayerVisible(9002).is_some() &&
        ! *layers->AnimationLayerVisible(9002) && owner.Visible();
    auto true_property = owe::NJson(true);
    const bool true_dispatched =
        user_scene.ApplyUserPropertyBindings("breathing"_str, true_property);
    scripts.Tick(owe::script::FrameInputs {});
    const bool true_applied = true_dispatched &&
        layers->AnimationLayerVisible(9002).is_some() &&
        *layers->AnimationLayerVisible(9002) && owner.Visible();
    const bool false_dispatched =
        user_scene.ApplyUserPropertyBindings("breathing"_str, false_property);
    scripts.Tick(owe::script::FrameInputs {});
    const bool false_applied = false_dispatched &&
        layers->AnimationLayerVisible(9002).is_some() &&
        ! *layers->AnimationLayerVisible(9002) && owner.Visible();

    const bool ok = initial_false_preserved && true_applied && false_applied &&
                    (*first)->Frame() == i32(4) &&
                    (*second)->Frame() == i32(15) &&
                    layers->AnimationLayerBlend(9001).is_some() &&
                    *layers->AnimationLayerBlend(9001) == 0.2 &&
                    layers->AnimationLayerBlend(9002).is_some() &&
                    *layers->AnimationLayerBlend(9002) == 0.75 &&
                    layers->AnimationLayerVisible(9001).is_some() &&
                    ! *layers->AnimationLayerVisible(9001) &&
                    layers->AnimationLayerVisible(9002).is_some() &&
                    ! *layers->AnimationLayerVisible(9002) && owner.Visible();
    if (! ok) {
        std::fprintf(stderr, "FAIL: exact animation-layer seek/write-back isolation\n");
        return false;
    }
    std::printf("PASS: exact animation-layer IDs isolate seek/visible/blend and user visibility toggles\n");
    return true;
}

// Feed a changing asymmetric image into the production FinPass/readback path.
// The fixture uses Vulkan transfers and has no window, asset or shader dependency.
class PatternPass final : public VulkanPass {
public:
    struct Desc {
        owe::resource::TextureUseHandle output;
        unsigned phase_offset { 0 };
        std::vector<VkImage>* observed_images { nullptr };
    };
    explicit PatternPass(Desc&& desc)
        : m_output(desc.output), m_observed_images(desc.observed_images), m_frame(desc.phase_offset) {}

    PassResourceUses resourceUses() const override {
        PassResourceUses uses;
        uses.textures.push(owe::resource::TextureUseHandle(m_output));
        return uses;
    }

    bool prepareResourceStates(
        owe::resource_registry::TextureStatePreparer* states) override {
        auto before = states->Prepare(m_output,
            owe::resource_registry::TextureStateKind::TransferDestination, {}, true);
        auto after = states->Prepare(m_output, owe::resource_registry::TextureStateKind::Sampled);
        if (before.is_none() || after.is_none()) return false;
        m_before.Clear();
        m_after.Clear();
        m_before.Add(rstd::move(*before));
        m_after.Add(rstd::move(*after));
        return true;
    }

    void prepare(owe::Scene&, const Device& device, PassPrepareContext& context) override {
        auto output = context.resources->Resolve(m_output);
        if (output.is_none()) return;
        m_extent = (**output).image.getActive().extent;
        if (m_observed_images) m_observed_images->push_back((**output).image.getActive().handle);
        m_device = &device;
        VkBufferCreateInfo info {
            .sType = VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO,
            .size = std::uint64_t(m_extent.width) * m_extent.height * 4,
            .usage = VK_BUFFER_USAGE_TRANSFER_SRC_BIT,
            .sharingMode = VK_SHARING_MODE_EXCLUSIVE,
        };
        VmaAllocationCreateInfo allocation {};
        allocation.usage = VMA_MEMORY_USAGE_CPU_TO_GPU;
        allocation.requiredFlags = VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT;
        if (vvk::CreateBuffer(device.vma_allocator(), info, allocation, m_upload) != VK_SUCCESS)
            return;
        setPrepared();
    }

    bool update(PassUpdateContext&) override {
        void* mapped = nullptr;
        if (m_upload.MapMemory(&mapped) != VK_SUCCESS) return false;
        auto* bytes = static_cast<std::uint8_t*>(mapped);
        for (unsigned y = 0; y < m_extent.height; ++y)
            for (unsigned x = 0; x < m_extent.width; ++x)
                for (unsigned c = 0; c < 4; ++c)
                    bytes[(y * m_extent.width + x) * 4 + c] =
                        static_cast<std::uint8_t>(Pattern(x, y, c, m_frame));
        const auto result = vmaFlushAllocation(m_device->vma_allocator(),
            m_upload.Allocation(), 0, VK_WHOLE_SIZE);
        m_upload.UnMapMemory();
        if (result != VK_SUCCESS) return false;
        ++m_frame;
        return true;
    }

    void record(PassRecordContext& context) override {
        auto output = context.resources->Resolve(m_output);
        if (output.is_none()) return;
        auto& command = *context.command;
        m_before.Record(command);
        VkBufferImageCopy copy {
            .imageSubresource = { VK_IMAGE_ASPECT_COLOR_BIT, 0, 0, 1 },
            .imageExtent = m_extent,
        };
        command.CopyBufferToImage(*m_upload, (**output).image.getActive().handle,
            VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, copy);
        m_after.Record(command);
    }

    void destory(const Device&) override {
        m_upload = vvk::VmaBuffer {};
        m_before.Clear();
        m_after.Clear();
        setPrepared(false);
    }

private:
    owe::resource::TextureUseHandle m_output;
    owe::resource_registry::PreparedBarrierBatch m_before, m_after;
    vvk::VmaBuffer m_upload;
    const Device* m_device { nullptr };
    VkExtent3D m_extent {};
    std::vector<VkImage>* m_observed_images { nullptr };
    unsigned m_frame { 0 };
};

double Expected(unsigned x, unsigned y, unsigned c, unsigned frame,
                unsigned source_width, unsigned source_height,
                unsigned width, unsigned height) {
    const double sx = std::clamp((x + 0.5) * source_width / width - 0.5,
                                 0.0, double(source_width - 1));
    const double sy = std::clamp((y + 0.5) * source_height / height - 0.5,
                                 0.0, double(source_height - 1));
    const auto x0 = static_cast<unsigned>(sx), y0 = static_cast<unsigned>(sy);
    const auto x1 = std::min(x0 + 1, source_width - 1);
    const auto y1 = std::min(y0 + 1, source_height - 1);
    const double fx = sx - x0, fy = sy - y0;
    const double a = Pattern(x0, y0, c, frame) * (1 - fx) + Pattern(x1, y0, c, frame) * fx;
    const double b = Pattern(x0, y1, c, frame) * (1 - fx) + Pattern(x1, y1, c, frame) * fx;
    return a * (1 - fy) + b * fy;
}

double BlitTolerance(unsigned width, unsigned height, unsigned frame, unsigned precision_bits) {
    double max_dx = 0.0, max_dy = 0.0;
    for (unsigned y = 0; y < height; ++y)
        for (unsigned x = 0; x < width; ++x)
            for (unsigned c = 0; c < 4; ++c) {
                const auto value = double(Pattern(x, y, c, frame));
                if (x + 1 < width)
                    max_dx = std::max(max_dx, std::abs(value - Pattern(x + 1, y, c, frame)));
                if (y + 1 < height)
                    max_dy = std::max(max_dy, std::abs(value - Pattern(x, y + 1, c, frame)));
            }
    // Vulkan blit coordinates/filter weights are quantized to the device's
    // subTexelPrecisionBits. Bound one coordinate quantum per axis using the
    // fixture's maximum slopes, plus one RGBA8 code for result quantization.
    // https://docs.vulkan.org/spec/latest/chapters/textures.html#textures-unnormalized-to-integer
    return 1.0 + (max_dx + max_dy) * std::ldexp(1.0, -static_cast<int>(precision_bits));
}

bool Run(unsigned source_width, unsigned source_height, unsigned width, unsigned height,
         bool validation, bool gpu_timing = false,
         std::vector<std::vector<std::uint8_t>>* recorded_frames = nullptr) {
    owe::Scene scene;
    owe::rg::RenderGraph graph;
    VulkanRender renderer;
    owe::RenderInitInfo init;
    init.output_mode = owe::RenderOutputMode::CpuReadback;
    init.width = static_cast<std::uint16_t>(width);
    init.height = static_cast<std::uint16_t>(height);
    init.enable_valid_layer = validation;
    init.gpu_timing = gpu_timing;
    init.max_readback_bytes = std::uint64_t(width) * height * 4;
    if (!renderer.init(init)) {
        std::fprintf(stderr, "FAIL: headless Vulkan initialization\n");
        return false;
    }
    const auto get_properties = reinterpret_cast<PFN_vkGetPhysicalDeviceProperties>(
        vkGetInstanceProcAddr(renderer.vkInstance(), "vkGetPhysicalDeviceProperties"));
    if (!get_properties) {
        std::fprintf(stderr, "FAIL: physical-device properties entry point unavailable\n");
        renderer.destroy();
        return false;
    }
    VkPhysicalDeviceProperties properties {};
    get_properties(renderer.vkPhysicalDevice(), &properties);
    const auto get_queue_properties = reinterpret_cast<PFN_vkGetPhysicalDeviceQueueFamilyProperties>(
        vkGetInstanceProcAddr(renderer.vkInstance(), "vkGetPhysicalDeviceQueueFamilyProperties"));
    if (!get_queue_properties) {
        std::fprintf(stderr, "FAIL: queue-family properties entry point unavailable\n");
        renderer.destroy();
        return false;
    }
    std::uint32_t queue_count = 0;
    get_queue_properties(renderer.vkPhysicalDevice(), &queue_count, nullptr);
    std::vector<VkQueueFamilyProperties> queue_properties(queue_count);
    get_queue_properties(renderer.vkPhysicalDevice(), &queue_count, queue_properties.data());
    const auto family = renderer.vkGraphicsQueueFamily();
    if (family >= queue_count) {
        std::fprintf(stderr, "FAIL: renderer returned an invalid graphics queue family\n");
        renderer.destroy();
        return false;
    }
    const auto timestamp_bits = queue_properties[family].timestampValidBits;
    const bool timestamp_support = timestamp_bits > 0 && timestamp_bits <= 64 &&
        properties.limits.timestampPeriod > 0 && std::isfinite(properties.limits.timestampPeriod);
    owe::SceneRenderTarget target { .width = i32(source_width), .height = i32(source_height) };
    scene.RegisterRenderTarget(String::make("_rt_default"_str), target);
    renderer.configureRenderTargets(scene);
    const auto& resolved_target = **scene.RenderTarget("_rt_default"_str);
    graph.addPass<PatternPass>("headless-pattern"_str, owe::rg::PassNode::Type::Copy,
        [&](owe::rg::RenderGraphBuilder& builder, PatternPass::Desc& desc) {
            auto texture = builder.createTexture(owe::rg::TextureDesc {
                .name = String::make("_rt_default"_str),
                .key = String::make("_rt_default"_str),
                .kind = owe::rg::TextureKind::Temp,
                .request = Some(MakeRenderTargetTextureRequest("_rt_default", resolved_target)),
            }, true);
            builder.write(texture);
            desc.output = builder.textureState(texture)->use;
        });
    renderer.compileRenderGraph(scene, graph);
    bool ok = true;
    double max_error = 0.0, max_tolerance = 0.0;
    double total_ms = 0.0;
    for (unsigned index = 0; index < 5 && ok; ++index) {
        const auto frame = renderer.drawFrameCpu(scene);
        if (!frame.completed() || frame.frame_index != index || frame.width != width ||
            frame.height != height || frame.row_pitch != width * 4 ||
            frame.format != VK_FORMAT_R8G8B8A8_UNORM || frame.pixels.size() != width * height * 4) {
            std::fprintf(stderr, "FAIL: frame %u status=%d VkResult=%d %s\n", index,
                int(frame.status), int(frame.error_code), frame.message.c_str());
            ok = false;
            break;
        }
        if (!gpu_timing) {
            if (frame.gpu_timing_requested || frame.gpu_timing_supported ||
                frame.gpu_total_ms.has_value() || frame.gpu_draw_ms.has_value() ||
                frame.timestamp_valid_bits != 0 || frame.timestamp_period_ns.has_value() ||
                frame.gpu_timing_error_code != VK_SUCCESS || !frame.gpu_timing_message.empty()) {
                std::fprintf(stderr, "FAIL: disabled GPU timing returned measurement metadata\n");
                ok = false;
                break;
            }
        } else if (timestamp_support) {
            if (!frame.gpu_timing_requested || !frame.gpu_timing_supported ||
                frame.timestamp_valid_bits != timestamp_bits || !frame.timestamp_period_ns.has_value() ||
                *frame.timestamp_period_ns != double(properties.limits.timestampPeriod) ||
                !frame.gpu_total_ms.has_value() || !std::isfinite(*frame.gpu_total_ms) ||
                *frame.gpu_total_ms < 0 ||
                !frame.gpu_draw_ms.has_value() || !std::isfinite(*frame.gpu_draw_ms) ||
                *frame.gpu_draw_ms < 0 || *frame.gpu_draw_ms > *frame.gpu_total_ms ||
                frame.gpu_timing_error_code != VK_SUCCESS || !frame.gpu_timing_message.empty()) {
                std::fprintf(stderr, "FAIL: requested GPU timestamps unavailable/invalid at frame %u: %s\n",
                             index, frame.gpu_timing_message.c_str());
                ok = false;
                break;
            }
            total_ms += *frame.gpu_total_ms;
        } else if (!frame.gpu_timing_requested || frame.gpu_timing_supported ||
                   frame.gpu_total_ms.has_value() || frame.gpu_draw_ms.has_value() ||
                   frame.gpu_timing_message.empty()) {
            std::fprintf(stderr, "FAIL: unsupported GPU timing did not return null plus a diagnostic\n");
            ok = false;
            break;
        }
        if (recorded_frames) recorded_frames->push_back(frame.pixels);
        const double tolerance = source_width == width && source_height == height ? 0.0 :
            BlitTolerance(source_width, source_height, index, properties.limits.subTexelPrecisionBits);
        max_tolerance = std::max(max_tolerance, tolerance);
        for (unsigned y = 0; y < height && ok; ++y)
            for (unsigned x = 0; x < width && ok; ++x)
                for (unsigned c = 0; c < 4; ++c) {
                    const auto actual = frame.pixels[y * frame.row_pitch + x * 4 + c];
                    const auto expected = Expected(x, y, c, index, source_width, source_height,
                                                   width, height);
                    const auto error = std::abs(double(actual) - expected);
                    max_error = std::max(max_error, error);
                    if (error > tolerance) {
                        std::fprintf(stderr,
                            "FAIL: frame %u pixel (%u,%u) channel %u: %u expected %.3f (tolerance %.3f)\n",
                            index, x, y, c, unsigned(actual), expected, tolerance);
                        ok = false;
                        break;
                    }
                }
    }
    renderer.destroy();
    if (ok) std::printf("PASS: %ux%u -> %ux%u, 5 completed RGBA8 frames, max error %.3f / %.3f, "
                         "subTexelPrecisionBits=%u, GPU=%s\n",
                         source_width, source_height, width, height, max_error, max_tolerance,
                         properties.limits.subTexelPrecisionBits, properties.deviceName);
    if (ok && gpu_timing && timestamp_support)
        std::printf("PASS: GPU timestamps for 5 frames: batch span mean %.9f ms, "
                    "timestampValidBits=%u, timestampPeriod=%.9f ns\n",
                    total_ms / 5, timestamp_bits, double(properties.limits.timestampPeriod));
    else if (ok && gpu_timing)
        std::printf("PASS: unsupported timestamp queue returned null measurements and a diagnostic\n");
    return ok;
}

bool RunTimingComparison(unsigned source_width, unsigned source_height, unsigned width,
                         unsigned height, bool validation) {
    std::vector<std::vector<std::uint8_t>> disabled, enabled;
    if (!Run(source_width, source_height, width, height, validation, false, &disabled) ||
        !Run(source_width, source_height, width, height, validation, true, &enabled)) return false;
    if (disabled != enabled) {
        std::fprintf(stderr, "FAIL: enabling GPU timestamps changed completed frame pixels\n");
        return false;
    }
    std::printf("PASS: timing disabled/enabled yields identical pixels for all 5 %ux%u frames\n", width, height);
    return true;
}

bool RunCapture(int version, bool authored_selector, bool invalid_selector, bool validation) {
    constexpr unsigned width = 7, height = 5;
    owe::Scene scene;
    owe::rg::RenderGraph graph;
    VulkanRender renderer;
    std::vector<VkImage> physical_images;

    auto node = rstd::sync::Arc<owe::SceneNode>::make();
    const auto owner = scene.RegisterNode(*node, Some(owe::WallpaperLayerId { .value = i32(20) }));
    auto layer = std::make_shared<owe::SceneNodeLayer>(node.as_ptr(), width, height, "_rt_owner");
    node->AttachLayer(layer);
    auto effect = std::make_shared<owe::SceneImageEffect>();
    effect->authored_id = 99;
    effect->authored_ordinal = 0;
    layer->AddEffect(effect);
    const auto effect_id = scene.RegisterEffect(owner, *layer, effect);
    scene.RootMut()->AppendChild(node.clone());
    scene.RebuildResourceIndex();
    const auto key = rstd::cppstd::to_string(scene.EffectResourceKey(effect_id, "_rt_capture"_str).as_str());

    owe::RenderInitInfo init;
    init.output_mode = owe::RenderOutputMode::CpuReadback;
    init.width = width;
    init.height = height;
    init.enable_valid_layer = validation;
    owe::RenderCaptureTarget selector;
    selector.texture_version = version;
    if (authored_selector) {
        selector.owner_layer_id = invalid_selector ? 999 : 20;
        selector.authored_effect_id = 99;
        selector.effect_ordinal = 0;
        selector.local_fbo = "_rt_capture";
    } else selector.runtime_render_target = key;
    init.capture_target = selector;
    if (!renderer.init(init)) {
        std::fprintf(stderr, "FAIL: capture fixture initialization\n");
        return false;
    }
    owe::SceneRenderTarget target { .width = i32(width), .height = i32(height), .allowReuse = true };
    scene.RegisterRenderTarget(String::make("_rt_default"_str), target);
    scene.RegisterRenderTarget(String::make(rstd::cppstd::as_str(key).unwrap()), target);
    renderer.configureRenderTargets(scene);
    const auto& resolved = **scene.RenderTarget(rstd::cppstd::as_str(key).unwrap());
    std::optional<owe::rg::TextureNodeRef> previous;
    for (unsigned stage = 0; stage < 3; ++stage) {
        const auto pass_name = "overwrite-" + std::to_string(stage);
        graph.addPass<PatternPass>(rstd::cppstd::as_str(pass_name).unwrap(), owe::rg::PassNode::Type::Copy,
            [&](owe::rg::RenderGraphBuilder& builder, PatternPass::Desc& desc) {
                // An explicit reader orders the writes; the allocation link
                // makes all three logical versions overwrite the same VkImage.
                if (previous.has_value()) builder.read(*previous);
                auto texture = builder.createTexture(owe::rg::TextureDesc {
                    .name = String::make(rstd::cppstd::as_str(key).unwrap()),
                    .key = String::make(rstd::cppstd::as_str(key).unwrap()),
                    .kind = owe::rg::TextureKind::Temp,
                    .request = Some(MakeRenderTargetTextureRequest(key, resolved)),
                    .allocation_family = Some(String::make(rstd::cppstd::as_str(key).unwrap())),
                }, true);
                if (previous.has_value()) builder.reusePreviousAllocation(texture);
                builder.write(texture);
                desc.output = builder.textureState(texture)->use;
                desc.phase_offset = stage * 11;
                desc.observed_images = &physical_images;
                previous = texture;
            });
    }
    renderer.compileRenderGraph(scene, graph);
    if (invalid_selector) {
        const auto frame = renderer.drawFrameCpu(scene);
        const bool rejected = !frame.completed() && frame.pixels.empty() && !frame.message.empty();
        renderer.destroy();
        std::printf("%s: invalid capture rejected without a fullscreen fallback\n", rejected ? "PASS" : "FAIL");
        return rejected;
    }
    if (physical_images.size() != 3 || physical_images[0] != physical_images[1] ||
        physical_images[0] != physical_images[2]) {
        std::fprintf(stderr, "FAIL: capture fixture did not share the physical output image\n");
        renderer.destroy();
        return false;
    }
    const auto selected = version < 0 ? 2u : static_cast<unsigned>(version);
    bool ok = true;
    for (unsigned index = 0; index < 5 && ok; ++index) {
        const auto frame = renderer.drawFrameCpu(scene);
        if (!frame.completed() || frame.frame_index != index || frame.source_render_target != key ||
            frame.source_texture_version != selected || frame.source_width != width ||
            frame.source_height != height || frame.compiled_scene_passes != 3 ||
            frame.source_pass != "overwrite-" + std::to_string(selected) ||
            frame.width != width || frame.height != height || frame.row_pitch != width * 4 ||
            frame.pixels.size() != width * height * 4) {
            std::fprintf(stderr, "FAIL: capture metadata/status at frame %u: %s\n", index, frame.message.c_str());
            ok = false;
            break;
        }
        for (unsigned y = 0; y < height && ok; ++y)
            for (unsigned x = 0; x < width && ok; ++x)
                for (unsigned c = 0; c < 4; ++c) {
                    const auto actual = frame.pixels[y * frame.row_pitch + x * 4 + c];
                    const auto expected = Pattern(x, y, c, index + selected * 11);
                    if (actual != expected) {
                        std::fprintf(stderr, "FAIL: selected version %u frame %u channel %u: %u expected %u\n",
                            selected, index, c, unsigned(actual), expected);
                        ok = false;
                        break;
                    }
                }
    }
    renderer.destroy();
    if (ok) std::printf("PASS: capture version %u (%s selector), 5 exact RGBA8 frames despite later alias writes\n",
                         selected, authored_selector ? "authored" : "runtime");
    return ok;
}

} // namespace

int main(int argc, char** argv) {
    if (argc == 3 && std::strcmp(argv[1], "--property-replay") == 0)
        return RunPropertyReplay(argv[2]) ? 0 : 1;
    if (argc == 2 && std::strcmp(argv[1], "--animation-layer-binding") == 0)
        return RunAnimationLayerBinding() ? 0 : 1;
    const bool validation = argc == 2 && std::strcmp(argv[1], "--validation") == 0;
    if (argc > 2 || (argc == 2 && !validation)) {
        std::fprintf(stderr,
                     "Usage: owe-offline-vulkan-test [--validation|--animation-layer-binding]\n");
        return 2;
    }
    // Odd strides expose row-padding assumptions. The second case exercises
    // the production linear blit when the authored RT and output sizes differ.
    return RunTimingComparison(7, 5, 7, 5, validation) && RunTimingComparison(7, 5, 13, 9, validation) &&
           RunCapture(0, false, false, validation) && RunCapture(1, true, false, validation) &&
           RunCapture(-1, false, false, validation) && RunCapture(-1, true, true, validation) &&
           RunCapture(42, false, true, validation) ? 0 : 1;
}
