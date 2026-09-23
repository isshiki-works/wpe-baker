#include <owe/compat.hpp>
#include <owe/std.hpp>
#include <algorithm>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <vulkan/vulkan_core.h>

import wescene.resource;
import wescene.resource_registry;
import wescene.rgraph;
import wescene.scene;
import wescene.vulkan;
import wescene.vulkan_render;

using namespace rstd::literals;
using namespace rstd::prelude;
using namespace owe::vulkan;

namespace
{

using Color = std::array<std::uint8_t, 4>;

rstd::log::EnvLogger logger { "info"_str };

void PrintVulkanPreflight(bool validation) {
    const char* layer_path = std::getenv("VK_LAYER_PATH");
    std::fprintf(stderr,
                 "VULKAN_PRECHECK validation=%s VK_LAYER_PATH=%s\n",
                 validation ? "required" : "disabled",
                 layer_path == nullptr || *layer_path == '\0' ? "<unset>" : layer_path);

    std::uint32_t layer_count {};
    auto          result = vkEnumerateInstanceLayerProperties(&layer_count, nullptr);
    std::fprintf(stderr,
                 "VULKAN_PRECHECK vkEnumerateInstanceLayerProperties(count)=%d layers=%u\n",
                 int(result),
                 layer_count);
    if (result != VK_SUCCESS) return;

    std::vector<VkLayerProperties> layers(layer_count);
    result = vkEnumerateInstanceLayerProperties(&layer_count, layers.data());
    std::fprintf(stderr,
                 "VULKAN_PRECHECK vkEnumerateInstanceLayerProperties(list)=%d layers=%u\n",
                 int(result),
                 layer_count);
    if (result != VK_SUCCESS) return;

    bool found_validation { false };
    for (std::uint32_t index = 0; index < layer_count; ++index) {
        std::fprintf(stderr, "VULKAN_LAYER %s\n", layers[index].layerName);
        found_validation |=
            std::strcmp(layers[index].layerName, "VK_LAYER_KHRONOS_validation") == 0;
    }
    if (validation && ! found_validation) {
        std::fprintf(stderr,
                     "VULKAN_PRECHECK missing required VK_LAYER_KHRONOS_validation; install the "
                     "Khronos validation layers or set process VK_LAYER_PATH to the directory "
                     "containing VkLayer_khronos_validation.json\n");
    }
}

Color SeedColor(bool second_pair, unsigned frame) {
    return second_pair ? Color { 5, static_cast<std::uint8_t>(120 + frame), 30, 255 }
                       : Color { static_cast<std::uint8_t>(40 + frame), 10, 200, 255 };
}

class SeedPass final : public VulkanPass {
public:
    struct Desc {
        owe::resource::TextureUseHandle output;
        bool                            second_pair { false };
    };

    explicit SeedPass(Desc&& desc): m_output(desc.output), m_second_pair(desc.second_pair) {}

    PassResourceUses resourceUses() const override {
        PassResourceUses uses;
        uses.textures.push(owe::resource::TextureUseHandle(m_output));
        return uses;
    }

    bool prepareResourceStates(
        owe::resource_registry::TextureStatePreparer* states) override {
        auto before = states->Prepare(
            m_output, owe::resource_registry::TextureStateKind::TransferDestination, {}, true);
        auto after = states->Prepare(m_output, owe::resource_registry::TextureStateKind::Sampled);
        if (before.is_none() || after.is_none()) return false;
        m_before.Clear();
        m_after.Clear();
        m_before.Add(rstd::move(*before));
        m_after.Add(rstd::move(*after));
        return true;
    }

    void prepare(owe::Scene&, const Device& device, PassPrepareContext&) override {
        VkBufferCreateInfo info {
            .sType       = VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO,
            .size        = sizeof(Color),
            .usage       = VK_BUFFER_USAGE_TRANSFER_SRC_BIT,
            .sharingMode = VK_SHARING_MODE_EXCLUSIVE,
        };
        VmaAllocationCreateInfo allocation {};
        allocation.usage         = VMA_MEMORY_USAGE_CPU_TO_GPU;
        allocation.requiredFlags = VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT;
        if (vvk::CreateBuffer(device.vma_allocator(), info, allocation, m_upload) != VK_SUCCESS)
            return;
        m_device = &device;
        setPrepared();
    }

    bool update(PassUpdateContext&) override {
        void* mapped = nullptr;
        if (m_upload.MapMemory(&mapped) != VK_SUCCESS) return false;
        const auto color = SeedColor(m_second_pair, m_frame++);
        std::memcpy(mapped, color.data(), color.size());
        const auto result =
            vmaFlushAllocation(m_device->vma_allocator(), m_upload.Allocation(), 0, VK_WHOLE_SIZE);
        m_upload.UnMapMemory();
        return result == VK_SUCCESS;
    }

    void record(PassRecordContext& context) override {
        auto output = context.resources->Resolve(m_output);
        if (output.is_none()) return;
        auto& command = *context.command;
        m_before.Record(command);
        VkBufferImageCopy copy {
            .imageSubresource = { VK_IMAGE_ASPECT_COLOR_BIT, 0, 0, 1 },
            .imageExtent      = { 1, 1, 1 },
        };
        command.CopyBufferToImage(*m_upload,
                                  (**output).image.getActive().handle,
                                  VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL,
                                  copy);
        m_after.Record(command);
    }

    void destory(const Device&) override {
        m_upload = vvk::VmaBuffer {};
        m_before.Clear();
        m_after.Clear();
        setPrepared(false);
    }

private:
    owe::resource::TextureUseHandle              m_output;
    owe::resource_registry::PreparedBarrierBatch m_before;
    owe::resource_registry::PreparedBarrierBatch m_after;
    vvk::VmaBuffer                               m_upload;
    const Device*                                m_device { nullptr };
    bool                                         m_second_pair { false };
    unsigned                                     m_frame { 0 };
};

auto TextureDesc(owe::Scene& scene, std::string_view key) -> owe::rg::TextureDesc {
    auto name   = rstd::cppstd::as_str(key).unwrap();
    auto target = scene.RenderTarget(name);
    rstd_assert(target.is_some());
    return owe::rg::TextureDesc {
        .name    = String::make(name),
        .key     = String::make(name),
        .kind    = owe::rg::TextureKind::Temp,
        .request = Some(MakeRenderTargetTextureRequest(key, **target)),
    };
}

void AddSeed(owe::rg::RenderGraph& graph, owe::Scene& scene, std::string_view key,
             bool second_pair) {
    graph.addPass<SeedPass>(second_pair ? "seed-c"_str : "seed-a"_str,
                            owe::rg::PassNode::Type::Copy,
                            [&](owe::rg::RenderGraphBuilder& builder, SeedPass::Desc& desc) {
                                auto output = builder.createTexture(TextureDesc(scene, key), true);
                                builder.write(output);
                                desc.output      = builder.textureState(output)->use;
                                desc.second_pair = second_pair;
                            });
}

void AddCopy(owe::rg::RenderGraph& graph, owe::Scene& scene, std::string_view source,
             std::string_view destination, bool preserve_source) {
    graph.addPass<CopyPass>(
        "effect-swap-copy"_str,
        owe::rg::PassNode::Type::Copy,
        [&](owe::rg::RenderGraphBuilder& builder, CopyPass::Desc& desc) {
            auto input = builder.createTexture(TextureDesc(scene, source));
            if (preserve_source) builder.markVirtualWrite(input);
            auto output = builder.createTexture(TextureDesc(scene, destination), true);
            builder.read(input);
            builder.write(output);
            auto input_state  = builder.textureState(input);
            auto output_state = builder.textureState(output);
            rstd_assert(input_state.is_some() && output_state.is_some());
            desc.src         = std::string(source);
            desc.dst         = std::string(destination);
            desc.src_use     = Some(input_state->use);
            desc.dst_use     = Some(output_state->use);
            desc.src_request = Some(MakeRenderTargetTextureRequest(
                source, **scene.RenderTarget(rstd::cppstd::as_str(source).unwrap())));
            desc.dst_request = Some(MakeRenderTargetTextureRequest(
                destination, **scene.RenderTarget(rstd::cppstd::as_str(destination).unwrap())));
        });
}

void AddSwap(owe::rg::RenderGraph& graph, owe::Scene& scene, std::string_view left,
             std::string_view right, std::string_view scratch) {
    AddCopy(graph, scene, left, scratch, true);
    AddCopy(graph, scene, right, left, true);
    AddCopy(graph, scene, scratch, right, false);
}

bool IsRetainedAcrossFrames(const owe::resource::ResourcePlan& plan, std::string_view key) {
    auto name  = rstd::cppstd::as_str(key).unwrap();
    bool found = false;
    for (const auto& texture : plan.textures) {
        if (texture.request.name != name) continue;
        found = true;
        const auto preserve =
            owe::resource::TextureContentFlag(owe::resource::TextureContent::PreserveAcrossFrames);
        if (texture.request.lifetime != owe::resource::TextureLifetimeClass::Retained ||
            (texture.request.content & preserve) == u32()) {
            return false;
        }
    }
    return found;
}

bool RunCapture(std::string_view selected, bool validation) {
    constexpr std::array<std::string_view, 4> swapped {
        "_rt_swap_a", "_rt_swap_b", "_rt_swap_c", "_rt_swap_d"
    };
    constexpr std::array<std::string_view, 2> scratch { "_rt_swap_a_scratch",
                                                        "_rt_swap_c_scratch" };

    owe::RenderInitInfo init;
    init.output_mode        = owe::RenderOutputMode::CpuReadback;
    init.width              = 1;
    init.height             = 1;
    init.max_readback_bytes = 4;
    init.enable_valid_layer = validation;
    init.capture_target     = owe::RenderCaptureTarget {
        .runtime_render_target = std::string(selected),
        .texture_version       = -1,
    };

    VulkanRender renderer;
    if (! renderer.init(init)) {
        std::fprintf(stderr,
                     "FAIL: VulkanRender::init returned false for %.*s; preserve the preceding "
                     "rstd/Vulkan diagnostics\n",
                     int(selected.size()),
                     selected.data());
        return false;
    }

    owe::Scene scene;
    auto       register_target = [&](std::string_view key, bool initialize) {
        scene.RegisterRenderTarget(String::make(rstd::cppstd::as_str(key).unwrap()),
                                   owe::SceneRenderTarget { .width                  = i32(1),
                                                            .height                 = i32(1),
                                                            .allowReuse             = true,
                                                            .initialize_transparent = initialize });
    };
    register_target("_rt_default", false);
    for (auto key : swapped) register_target(key, key == "_rt_swap_b" || key == "_rt_swap_d");
    for (auto key : scratch) register_target(key, false);
    renderer.configureRenderTargets(scene);

    owe::rg::RenderGraph graph;
    AddSeed(graph, scene, "_rt_swap_a", false);
    AddSeed(graph, scene, "_rt_swap_c", true);
    AddSwap(graph, scene, "_rt_swap_a", "_rt_swap_b", "_rt_swap_a_scratch");
    AddSwap(graph, scene, "_rt_swap_c", "_rt_swap_d", "_rt_swap_c_scratch");

    auto plan = graph.resourcePlan();
    if (! IsRetainedAcrossFrames(plan, "_rt_swap_b") ||
        ! IsRetainedAcrossFrames(plan, "_rt_swap_d")) {
        std::fprintf(stderr, "FAIL: swap inputs are not retained across frames\n");
        renderer.destroy();
        return false;
    }

    renderer.compileRenderGraph(scene, graph);
    const bool second_pair  = selected == "_rt_swap_c" || selected == "_rt_swap_d";
    const bool captures_old = selected == "_rt_swap_a" || selected == "_rt_swap_c";
    bool       ok { true };
    for (unsigned frame_index = 0; frame_index < 4 && ok; ++frame_index) {
        auto  frame = renderer.drawFrameCpu(scene);
        Color expected {};
        if (! captures_old || frame_index > 0) {
            expected = SeedColor(second_pair, captures_old ? frame_index - 1 : frame_index);
        }
        if (! frame.completed() || frame.frame_index != frame_index || frame.width != 1 ||
            frame.height != 1 || frame.row_pitch != 4 || frame.pixels.size() != 4 ||
            frame.source_render_target != selected ||
            ! std::equal(expected.begin(), expected.end(), frame.pixels.begin())) {
            std::fprintf(stderr,
                         "FAIL: %.*s frame %u got [%u,%u,%u,%u], expected [%u,%u,%u,%u]: %s\n",
                         int(selected.size()),
                         selected.data(),
                         frame_index,
                         frame.pixels.size() > 0 ? unsigned(frame.pixels[0]) : 0,
                         frame.pixels.size() > 1 ? unsigned(frame.pixels[1]) : 0,
                         frame.pixels.size() > 2 ? unsigned(frame.pixels[2]) : 0,
                         frame.pixels.size() > 3 ? unsigned(frame.pixels[3]) : 0,
                         unsigned(expected[0]),
                         unsigned(expected[1]),
                         unsigned(expected[2]),
                         unsigned(expected[3]),
                         frame.message.c_str());
            ok = false;
        }
    }
    renderer.destroy();
    if (ok) {
        std::printf(
            "PASS: %.*s exact pixels across 4 frames\n", int(selected.size()), selected.data());
    }
    return ok;
}

} // namespace

int main(int argc, char** argv) {
    rstd::log::set_logger(logger);
    rstd::log::set_max_level(rstd::log::LevelFilter::Info);
    const bool validation = argc == 2 && std::strcmp(argv[1], "--validation") == 0;
    if (argc > 2 || (argc == 2 && ! validation)) {
        std::fprintf(stderr, "Usage: owe-effect-swap-gpu-fixture [--validation]\n");
        return 2;
    }
    PrintVulkanPreflight(validation);
    return RunCapture("_rt_swap_a", validation) && RunCapture("_rt_swap_b", validation) &&
                   RunCapture("_rt_swap_c", validation) && RunCapture("_rt_swap_d", validation)
               ? 0
               : 1;
}
