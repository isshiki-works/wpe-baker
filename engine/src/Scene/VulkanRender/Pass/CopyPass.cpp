module;

#include <algorithm>
#include <cmath>
#include <limits>
#include <rstd/macro.hpp>

module wescene.vulkan_render;
import eigen;
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

namespace
{
bool EmptySnapshotConsumer(owe::SceneNode* node, u32 submesh_index) {
    if (node == nullptr || node->Mesh() == nullptr) return false;
    const auto& mesh = *node->Mesh();
    if (submesh_index.to_primitive() >= mesh.Submeshes().size()) return false;
    const auto& submesh = mesh.Submeshes()[submesh_index.to_primitive()];
    if (! submesh.index_arrays.empty()) {
        // Explicit ranges drive DrawIndexed independently of draw_count.
        if (! submesh.draw_ranges.empty()) return false;
        return submesh.index_arrays[0].RenderDataCount() == usize();
    }
    if (submesh.vertex_arrays.empty()) return false;
    // Dynamic updates use stream 0; static preparation sums all streams.
    if (mesh.Dynamic()) return submesh.vertex_arrays[0].VertexCount() == usize();
    for (const auto& vertices : submesh.vertex_arrays) {
        if (vertices.VertexCount() != usize()) return false;
    }
    return true;
}

VkRect2D SnapshotConsumerBounds(owe::SceneNode* node, u32 submesh_index, u32 instance_count,
                                VkExtent2D extent) {
    const VkRect2D full_rect { .offset = { 0, 0 }, .extent = extent };
    if (extent.width == 0 || extent.height == 0 || instance_count > u32(1) || node == nullptr ||
        ! node->HasLastPrimaryMvp() || node->Mesh() == nullptr) {
        return full_rect;
    }
    const auto& mesh = *node->Mesh();
    if (mesh.Primitive() == owe::MeshPrimitive::POINT) return full_rect;
    const auto submesh_idx = submesh_index.to_primitive();
    if (submesh_idx >= mesh.Submeshes().size()) return full_rect;

    const auto& submesh  = mesh.Submeshes()[submesh_idx];
    const auto  mat_slot = submesh.material_slot.to_primitive();
    if (mat_slot >= mesh.MaterialSlots().size() || ! mesh.MaterialSlots()[mat_slot]) {
        return full_rect;
    }
    const auto& mat = *mesh.MaterialSlots()[mat_slot];
    if (! owe::IsLayerCompositeShader(mat.name)) return full_rect;
    if (mat.customShader.variant.is_some() &&
        mat.customShader.variant->geometry_shader_enabled) {
        return full_rect;
    }

    const owe::SceneVertexArray* pos_array         = nullptr;
    std::size_t                  pos_offset_floats = 0;
    std::size_t                  pos_components    = 0;
    for (const auto& va : submesh.vertex_arrays) {
        std::size_t offset = 0;
        for (const auto& attr : va.Attributes()) {
            const auto attr_name = as_str(attr.name).unwrap();
            if (attr_name == owe::WE_IN_BLENDINDICES || attr_name == owe::WE_IN_BLENDWEIGHTS ||
                attr_name == owe::WE_IN_POSITIONVEC4 || attr_name == owe::WE_IN_POSITIONC1) {
                return full_rect;
            }
            if (attr_name == owe::WE_IN_POSITION && pos_array == nullptr) {
                pos_array         = &va;
                pos_offset_floats = offset;
                pos_components    = owe::SceneVertexArray::TypeCount(attr.type);
            }
            offset += owe::SceneVertexArray::RealAttributeSize(attr);
        }
    }
    if (pos_array == nullptr || pos_components < 2) return full_rect;

    const std::size_t vertex_count = pos_array->VertexCount().to_primitive();
    const std::size_t stride       = pos_array->OneSize().to_primitive();
    const float*      data         = pos_array->Data();
    if (vertex_count == 0 || stride == 0 || data == nullptr) return full_rect;

    const Eigen::Matrix4d& mvp    = node->LastPrimaryMvp();
    double                 min_px = std::numeric_limits<double>::infinity();
    double                 max_px = -std::numeric_limits<double>::infinity();
    double                 min_py = std::numeric_limits<double>::infinity();
    double                 max_py = -std::numeric_limits<double>::infinity();

    auto project_point = [&](double x, double y, double z) -> bool {
        const Eigen::Vector4d clip = mvp * Eigen::Vector4d(x, y, z, 1.0);
        if (! std::isfinite(clip.w()) || clip.w() <= 1e-5) return false;
        const double inv_w = 1.0 / clip.w();
        const double ndc_x = clip.x() * inv_w;
        const double ndc_y = clip.y() * inv_w;
        if (! std::isfinite(ndc_x) || ! std::isfinite(ndc_y)) return false;
        const double px = (ndc_x * 0.5 + 0.5) * static_cast<double>(extent.width);
        const double py = (0.5 - ndc_y * 0.5) * static_cast<double>(extent.height);
        min_px          = std::min(min_px, px);
        max_px          = std::max(max_px, px);
        min_py          = std::min(min_py, py);
        max_py          = std::max(max_py, py);
        return true;
    };

    if (vertex_count <= 16) {
        for (std::size_t i = 0; i < vertex_count; ++i) {
            const float* v = data + i * stride + pos_offset_floats;
            const double x = static_cast<double>(v[0]);
            const double y = static_cast<double>(v[1]);
            const double z = pos_components >= 3 ? static_cast<double>(v[2]) : 0.0;
            if (! project_point(x, y, z)) return full_rect;
        }
    } else {
        double min_x = std::numeric_limits<double>::infinity();
        double max_x = -std::numeric_limits<double>::infinity();
        double min_y = std::numeric_limits<double>::infinity();
        double max_y = -std::numeric_limits<double>::infinity();
        double min_z = std::numeric_limits<double>::infinity();
        double max_z = -std::numeric_limits<double>::infinity();
        for (std::size_t i = 0; i < vertex_count; ++i) {
            const float* v = data + i * stride + pos_offset_floats;
            const double x = static_cast<double>(v[0]);
            const double y = static_cast<double>(v[1]);
            const double z = pos_components >= 3 ? static_cast<double>(v[2]) : 0.0;
            min_x          = std::min(min_x, x);
            max_x          = std::max(max_x, x);
            min_y          = std::min(min_y, y);
            max_y          = std::max(max_y, y);
            min_z          = std::min(min_z, z);
            max_z          = std::max(max_z, z);
        }
        const double xs[2] = { min_x, max_x };
        const double ys[2] = { min_y, max_y };
        const double zs[2] = { min_z, max_z };
        for (double x : xs) {
            for (double y : ys) {
                for (double z : zs) {
                    if (! project_point(x, y, z)) return full_rect;
                }
            }
        }
    }

    if (! std::isfinite(min_px) || ! std::isfinite(max_px) || ! std::isfinite(min_py) ||
        ! std::isfinite(max_py)) {
        return full_rect;
    }

    constexpr double kMargin = 8.0;
    const double     w_d     = static_cast<double>(extent.width);
    const double     h_d     = static_cast<double>(extent.height);
    const double     x0_d    = std::floor(min_px - kMargin);
    const double     y0_d    = std::floor(min_py - kMargin);
    const double     x1_d    = std::ceil(max_px + kMargin);
    const double     y1_d    = std::ceil(max_py + kMargin);
    if (x1_d <= 0.0 || y1_d <= 0.0 || x0_d >= w_d || y0_d >= h_d) {
        return VkRect2D { .offset = { 0, 0 }, .extent = { 0, 0 } };
    }
    const std::int32_t x0 = static_cast<std::int32_t>(std::clamp(x0_d, 0.0, w_d));
    const std::int32_t y0 = static_cast<std::int32_t>(std::clamp(y0_d, 0.0, h_d));
    const std::int32_t x1 = static_cast<std::int32_t>(std::clamp(x1_d, 0.0, w_d));
    const std::int32_t y1 = static_cast<std::int32_t>(std::clamp(y1_d, 0.0, h_d));
    if (x0 >= x1 || y0 >= y1) {
        return VkRect2D { .offset = { 0, 0 }, .extent = { 0, 0 } };
    }
    return VkRect2D {
        .offset = { x0, y0 },
        .extent = { static_cast<std::uint32_t>(x1 - x0),
                    static_cast<std::uint32_t>(y1 - y0) },
    };
}
} // namespace

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
    resource_registry::TextureStatePreparer* states) {
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
    if (src.mipmap_level == 1 && dst.mipmap_level == 1 &&
        src.extent.width == dst.extent.width && src.extent.height == dst.extent.height &&
        m_desc.empty_draw_consumer != nullptr) {
        if (EmptySnapshotConsumer(m_desc.empty_draw_consumer, m_desc.empty_draw_submesh)) {
            // Keep the prepared image states even though no shader reads this snapshot.
            m_desc.after_barriers.Record(cmd);
            return;
        }
        const auto rect = SnapshotConsumerBounds(
            m_desc.empty_draw_consumer,
            m_desc.empty_draw_submesh,
            m_desc.empty_draw_instance_count,
            VkExtent2D { src.extent.width, src.extent.height });
        if (rect.extent.width == 0 || rect.extent.height == 0) {
            m_desc.after_barriers.Record(cmd);
            return;
        }
        copy.srcOffset = { rect.offset.x, rect.offset.y, 0 };
        copy.dstOffset = { rect.offset.x, rect.offset.y, 0 };
        copy.extent    = { rect.extent.width, rect.extent.height, 1 };
    }
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
PassTimingTarget CopyPass::timingTarget(const PreparedPassResources& resources) const {
    PassTimingTarget target { .output = m_desc.dst };
    if (m_desc.src_use.is_none()) return target;
    auto prepared_src = resources.Resolve(*m_desc.src_use);
    if (prepared_src.is_none()) return target;
    const auto& src = (**prepared_src).image.getActive();
    target.width    = src.extent.width;
    target.height   = src.extent.height;
    return target;
}
void CopyPass::destory(const Device&) {}
