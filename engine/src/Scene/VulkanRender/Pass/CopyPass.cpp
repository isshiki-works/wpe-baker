module;

#include <rstd/macro.hpp>

module wescene.vulkan_render;
import wescene.spec_names;
import wescene.core;
import rstd;
import rstd.log;
import rstd.cppstd;
import wescene.vulkan;
import wescene.scene;

using namespace owe::vulkan;
using namespace rstd::prelude;
using rstd::cppstd::as_str;

CopyPass::CopyPass(Desc&& desc): m_desc(std::move(desc)) {}

CopyPass::~CopyPass() {};

PassInvalidationFlags CopyPass::finalizeResourceRequests(Scene& scene) {
    PassInvalidationFlags flags   = PassInvalidationNone;
    auto                  refresh = [&scene](std::string_view name) -> Option<TextureRequest> {
        auto text = as_str(name).unwrap();
        if (name.empty() || ! IsSpecTex(text)) return None();
        auto target = scene.RenderTarget(text);
        if (target.is_none()) return None();
        return Some(MakeRenderTargetTextureRequest(name, **target));
    };

    if (auto request = refresh(m_desc.src);
        request.is_some() && SetTextureRequestIfChanged(m_desc.src_request, std::move(request))) {
        flags |= ToPassInvalidationFlags(PassInvalidation::Resources);
    }
    auto dst_request = refresh(m_desc.dst);
    if (m_desc.dst_matches_src && m_desc.src_request.is_some()) {
        dst_request       = Some(m_desc.src_request->clone());
        dst_request->name = String::make(as_str(m_desc.dst).unwrap());
    }
    if (dst_request.is_some() &&
        SetTextureRequestIfChanged(m_desc.dst_request, std::move(dst_request))) {
        flags |= ToPassInvalidationFlags(PassInvalidation::Resources);
    }
    return flags;
}

PassResourceUses CopyPass::resourceUses() const {
    PassResourceUses uses;
    if (m_desc.src_use.is_some()) uses.textures.push(resource::TextureUseHandle(*m_desc.src_use));
    if (m_desc.dst_use.is_some()) uses.textures.push(resource::TextureUseHandle(*m_desc.dst_use));
    return uses;
}

std::vector<PassTextureRequestDiagnostic> CopyPass::textureRequestDiagnostics() const {
    std::vector<PassTextureRequestDiagnostic> out;
    out.reserve(2);
    out.push_back(PassTextureRequestDiagnostic {
        .role    = "copy-src",
        .name    = m_desc.src,
        .use     = m_desc.src_use,
        .request = m_desc.src_request.is_some() ? rstd::Some(m_desc.src_request->clone())
                                                : rstd::None<TextureRequest>(),
    });
    out.push_back(PassTextureRequestDiagnostic {
        .role    = "copy-dst",
        .name    = m_desc.dst,
        .use     = m_desc.dst_use,
        .request = m_desc.dst_request.is_some() ? rstd::Some(m_desc.dst_request->clone())
                                                : rstd::None<TextureRequest>(),
    });
    return out;
}

bool CopyPass::prepareResourceStates(
    rstd::mut_ref<rstd::dyn<resource_registry::TextureStatePreparer>> states) {
    m_desc.before_barriers.Clear();
    m_desc.after_barriers.Clear();
    if (m_desc.src_use.is_none() || m_desc.dst_use.is_none()) return false;

    auto range = resource_registry::TextureSubresourceRange {
        .level_count = u32(1),
        .layer_count = u32(1),
    };
    auto src_before = states->Prepare(
        *m_desc.src_use, resource_registry::TextureStateKind::TransferSource, range);
    auto dst_before = states->Prepare(
        *m_desc.dst_use, resource_registry::TextureStateKind::TransferDestination, range, true);
    auto src_after =
        states->Prepare(*m_desc.src_use, resource_registry::TextureStateKind::Sampled, range);
    auto dst_after =
        states->Prepare(*m_desc.dst_use, resource_registry::TextureStateKind::Sampled, range);
    if (src_before.is_none() || dst_before.is_none() || src_after.is_none() ||
        dst_after.is_none()) {
        return false;
    }
    m_desc.before_barriers.Add(rstd::move(src_before).unwrap_unchecked());
    m_desc.before_barriers.Add(rstd::move(dst_before).unwrap_unchecked());
    m_desc.after_barriers.Add(rstd::move(src_after).unwrap_unchecked());
    m_desc.after_barriers.Add(rstd::move(dst_after).unwrap_unchecked());
    return true;
}

void CopyPass::prepare(Scene&, const Device&, PassPrepareContext& context) {
    rstd::array<std::string, 2>                         textures { m_desc.src, m_desc.dst };
    rstd::array<Option<resource::TextureUseHandle>*, 2> texture_uses {
        &m_desc.src_use,
        &m_desc.dst_use,
    };
    for (usize i {}; i < textures.len(); i++) {
        auto& tex_name = textures[i];
        if (tex_name.empty()) continue;

        if (texture_uses[i]->is_none()) {
            rstd_error("copy texture {} has no resource use", tex_name);
            return;
        }
        auto prepared = context.resources->Resolve(**texture_uses[i]);
        if (prepared.is_none()) {
            rstd_error("prepared copy texture {} not found", tex_name);
            return;
        }
    }

    setPrepared();
};
void CopyPass::record(PassRecordContext& context) {
    if (m_desc.src_use.is_none() || m_desc.dst_use.is_none()) return;
    auto prepared_src = context.resources->Resolve(*m_desc.src_use);
    auto prepared_dst = context.resources->Resolve(*m_desc.dst_use);
    if (prepared_src.is_none() || prepared_dst.is_none()) return;
    auto& cmd = *context.command;
    auto& src = (**prepared_src).image.getActive();
    auto& dst = (**prepared_dst).image.getActive();

    if (! (src.handle && dst.handle)) {
        rstd_assert(src.handle && dst.handle);
        return;
    }

    VkImageSubresourceRange srang {
        .aspectMask     = VK_IMAGE_ASPECT_COLOR_BIT,
        .baseMipLevel   = 0,
        .levelCount     = 1,
        .baseArrayLayer = 0,
        .layerCount     = 1,

    };
    VkImageCopy copy {
        .srcSubresource =
            VkImageSubresourceLayers {
                .aspectMask     = srang.aspectMask,
                .mipLevel       = 0,
                .baseArrayLayer = 0,
                .layerCount     = 1,
            },
        .dstSubresource =
            VkImageSubresourceLayers {
                .aspectMask     = srang.aspectMask,
                .mipLevel       = 0,
                .baseArrayLayer = 0,
                .layerCount     = 1,
            },
        .extent = { src.extent.width, src.extent.height, 1 },
    };
    m_desc.before_barriers.Record(cmd);
    cmd.CopyImage(src.handle,
                  VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL,
                  dst.handle,
                  VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL,
                  copy);
    m_desc.after_barriers.Record(cmd);

    if (dst.mipmap_level > 1) {
        RecordGenerateMipmaps(cmd, dst);
    }
};
void CopyPass::destory(const Device&) {}
