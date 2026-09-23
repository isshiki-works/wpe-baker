module;

#include <rstd/macro.hpp>
module wescene.vulkan_render;
import rstd.log;
import rstd.cppstd;
import wescene.vulkan;
import wescene.scene;

using namespace owe::vulkan;
using namespace rstd::prelude;
using rstd::cppstd::as_str;

FinPass::FinPass(Desc&& desc): m_desc(std::move(desc)) {}
FinPass::~FinPass() {}

bool FinPass::setFrameSurface(
    owe::FrameSurfaceLease                                                lease,
    resource_registry::ExternalResourcePreparer* resources,
    const DeviceCapabilities& capabilities, rstd::uint32_t graphics_queue_family) {
    if (m_desc.external_use.is_none() || m_desc.result_use.is_none()) return false;
    auto prepared = resources->PrepareExternal(*m_desc.external_use,
                                               *m_desc.result_use,
                                               capabilities,
                                               rstd::move(lease),
                                               graphics_queue_family);
    if (prepared.is_err()) {
        auto error = rstd::move(prepared).unwrap_err_unchecked();
        rstd_error("prepare external frame failed: {}", error.message);
        return false;
    }
    return true;
}
bool FinPass::setResultRequest(rstd::Option<TextureRequest> request) {
    return SetTextureRequestIfChanged(m_desc.result_request, std::move(request));
}

void FinPass::setGraphSource(std::string name, rstd::Option<resource::TextureUseHandle> use) {
    m_desc.result = std::move(name);
    m_desc.graph_result_use = use;
}

void FinPass::resetResourceUses() {
    m_desc.result_use   = rstd::None();
    m_desc.external_use = rstd::None();
}

void FinPass::declareResources(ResourceDeclarationContext& context) {
    resetResourceUses();
    if (m_desc.graph_result_use.is_some()) {
        // Bind the graph version itself, not a second allocation by logical name.
        m_desc.result_use = m_desc.graph_result_use;
    } else if (m_desc.result_request.is_some()) {
        m_desc.result_use = rstd::Some(
            context.AddTexture(m_desc.result_request->clone(), resource::ResourceAccess::Read));
    }
    m_desc.external_use = rstd::Some(context.ReserveExternal());
}

PassResourceUses FinPass::resourceUses() const {
    PassResourceUses uses;
    if (m_desc.result_use.is_some()) {
        uses.textures.push(resource::TextureUseHandle(*m_desc.result_use));
    }
    if (m_desc.external_use.is_some()) {
        uses.externals.push(resource::ExternalUseHandle(*m_desc.external_use));
    }
    return uses;
}

std::vector<PassTextureRequestDiagnostic> FinPass::textureRequestDiagnostics() const {
    std::vector<PassTextureRequestDiagnostic> out;
    out.push_back(PassTextureRequestDiagnostic {
        .role    = "frame-result",
        .name    = std::string(m_desc.result),
        .use     = m_desc.result_use,
        .request = m_desc.result_request.is_some() ? rstd::Some(m_desc.result_request->clone())
                                                   : rstd::None<TextureRequest>(),
    });
    return out;
}

bool FinPass::prepareResourceStates(
    resource_registry::TextureStatePreparer* states) {
    m_desc.result_barrier.Clear();
    if (m_desc.result_use.is_none()) return false;
    auto barrier =
        states->Prepare(*m_desc.result_use, resource_registry::TextureStateKind::TransferSource);
    if (barrier.is_none()) return false;
    m_desc.result_barrier.Add(rstd::move(barrier).unwrap_unchecked());
    return true;
}

void FinPass::prepare(Scene& scene, const Device&, PassPrepareContext& context) {
    auto tex_name = std::string(m_desc.result);
    if (scene.RenderTarget(as_str(tex_name).unwrap()).is_none()) {
        rstd_error("FinPass: scene render target \"{}\" not found", tex_name);
        return;
    }
    if (m_desc.result_use.is_none()) return;
    if (context.resources->Resolve(*m_desc.result_use).is_none()) {
        rstd_error("FinPass: prepared texture \"{}\" unavailable", tex_name);
        return;
    }
    setPrepared();
}

void FinPass::record(PassRecordContext& context) {
    if (m_desc.result_use.is_none() || m_desc.external_use.is_none()) return;
    auto source   = context.resources->Resolve(*m_desc.result_use);
    auto external = context.resources->Resolve(*m_desc.external_use);
    if (source.is_none() || external.is_none()) return;
    const auto& result        = (**source).image.getActive();
    const auto& prepared      = (**external).frame;
    const auto& frame_surface = prepared.lease;
    const auto& present       = frame_surface.image;
    auto&       cmd           = *context.command;
    m_desc.result_barrier.Record(cmd);
    prepared.before_copy.Record(cmd);

    {
        // Result is R8G8B8A8_UNORM (screen RT), or RGBA16F under hdr_scale. We can
        // copy when present matches that and dimensions are identical; otherwise
        // fall back to blit which handles size/format mismatch (float -> UNORM clamps to [0,1]).
        const bool can_copy = result.extent.width == present.extent.width &&
                              result.extent.height == present.extent.height &&
                              frame_surface.format == VK_FORMAT_R8G8B8A8_UNORM &&
                              ! ((**source).request.definition.is_some() &&
                                 (*(**source).request.definition).format == owe::TextureFormat::RGBA16F);

        if (! m_path_logged) {
            rstd_info("FinPass: {}", can_copy ? "copy" : "blit");
            m_path_logged = true;
        }

        if (can_copy) {
            VkImageCopy region {
                .srcSubresource = { VK_IMAGE_ASPECT_COLOR_BIT, 0, 0, 1 },
                .srcOffset      = { 0, 0, 0 },
                .dstSubresource = { VK_IMAGE_ASPECT_COLOR_BIT, 0, 0, 1 },
                .dstOffset      = { 0, 0, 0 },
                .extent         = { result.extent.width, result.extent.height, 1 },
            };
            cmd.CopyImage(result.handle,
                          VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL,
                          present.handle,
                          VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL,
                          region);
        } else {
            VkImageBlit region {
                .srcSubresource = { VK_IMAGE_ASPECT_COLOR_BIT, 0, 0, 1 },
                .srcOffsets     = {
                    VkOffset3D { 0, 0, 0 },
                    VkOffset3D { static_cast<rstd::int32_t>(result.extent.width),
                                 static_cast<rstd::int32_t>(result.extent.height),
                                 1 },
                },
                .dstSubresource = { VK_IMAGE_ASPECT_COLOR_BIT, 0, 0, 1 },
                .dstOffsets     = {
                    VkOffset3D { 0, 0, 0 },
                    VkOffset3D { static_cast<rstd::int32_t>(present.extent.width),
                                 static_cast<rstd::int32_t>(present.extent.height),
                                 1 },
                },
            };
            cmd.BlitImage(result.handle,
                          VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL,
                          present.handle,
                          VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL,
                          region,
                          VK_FILTER_LINEAR);
        }
    }

    prepared.after_copy.Record(cmd);
}

void FinPass::destory(const Device&) {
    m_desc.result_barrier.Clear();
    setPrepared(false);
}
