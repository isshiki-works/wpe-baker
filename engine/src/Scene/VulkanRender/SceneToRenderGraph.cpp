module;

#include <rstd/macro.hpp>

module wescene.vulkan_render;
import wescene.spec_names;
import wescene.core;
import rstd.log;
import rstd.cppstd;
import eigen;
import wescene.vulkan;
import wescene.scene;

import wescene.rgraph;
import wescene.pkg.parse;
import wescene.fs;

using namespace owe;
using namespace rstd::literals;
using namespace rstd::prelude;
using rstd::collections::BTreeSet;
using rstd::collections::HashMap;
using rstd::collections::HashSet;
using rstd::cppstd::as_str;
using rstd::cppstd::as_string_view;

namespace
{
auto StdString(const String& value) -> std::string {
    return rstd::cppstd::to_string(value.as_str());
}

auto CloneTextureDesc(const rg::TextureDesc& desc) -> rg::TextureDesc {
    return rg::TextureDesc {
        .name = desc.name.clone(),
        .key  = desc.key.clone(),
        .kind = desc.kind,
        .request =
            desc.request.is_some() ? Some(desc.request->clone()) : None<resource::TextureRequest>(),
        .allocation_family = desc.allocation_family.is_some()
                                 ? Some(desc.allocation_family->clone())
                                 : None<String>(),
    };
}
} // namespace

namespace owe::rg
{

void doCopy(RenderGraphBuilder& builder, vulkan::CopyPass::Desc& desc, TextureNodeRef in,
            TextureNodeRef out) {
    builder.read(in);
    builder.write(out);

    auto in_state  = builder.textureState(in);
    auto out_state = builder.textureState(out);
    rstd_assert(in_state.is_some() && out_state.is_some());
    if (in_state.is_none() || out_state.is_none()) return;
    desc.src     = StdString(in_state->desc.key);
    desc.dst     = StdString(out_state->desc.key);
    desc.src_use = Some(in_state->use);
    desc.dst_use = Some(out_state->use);
}
} // namespace owe::rg

struct ExtraInfo;

static rg::TextureDesc MakeTextureDescBase(std::string_view key) {
    auto name = as_str(key).unwrap();
    return rg::TextureDesc {
        .name = String::make(name),
        .key  = String::make(name),
        .kind = IsSpecTex(name) ? rg::TextureKind::Temp : rg::TextureKind::Imported,
    };
}

struct ExtraInfo {
    rg::RenderGraph*           rgraph { nullptr };
    Scene*                     scene { nullptr };
    Set<std::string>           depth_initialized_outputs {};
    HashSet<String>            transient_texture_families;
    Option<rg::TextureNodeRef> mip_framebuffer_history;
    const RenderSceneSnapshot* render_scene { nullptr };
    const RenderLayerSelection* selection { nullptr };
    // X3 M1：效果 pass → （输出中需保留的 UV 矩形，逐帧 guard）
    std::unordered_map<const SceneImageEffectNode*,
                       std::pair<std::array<double, 4>, std::shared_ptr<const std::function<bool()>>>>
        region_clip;
};

static Option<vulkan::TextureRequest> BuildGraphTextureRequest(ExtraInfo&       extra,
                                                               std::string_view key) {
    if (key.empty()) return None();
    auto name = as_str(key).unwrap();
    if (! IsSpecTex(name)) {
        Option<RenderTextureDescId> texture;
        if (extra.render_scene != nullptr) texture = extra.render_scene->textureDescId(name);
        return Some(vulkan::MakeImportedTextureRequest(key, texture));
    }

    if (extra.render_scene != nullptr) {
        if (auto desc_id = extra.render_scene->renderTargetDescId(name)) {
            if (auto* desc = extra.render_scene->renderTargetDesc(*desc_id)) {
                return Some(vulkan::MakeRenderTargetTextureRequest(key, desc->desc));
            }
        }
    }

    if (extra.scene != nullptr) {
        auto target = extra.scene->RenderTarget(name);
        if (target.is_some()) {
            return Some(vulkan::MakeRenderTargetTextureRequest(key, **target));
        }
    }

    return None();
}

static rg::TextureDesc MakeTextureDesc(ExtraInfo& extra, std::string_view key) {
    auto desc    = MakeTextureDescBase(key);
    desc.request = BuildGraphTextureRequest(extra, key);
    auto name    = as_str(key).unwrap();
    if (extra.transient_texture_families.contains(name)) {
        desc.allocation_family = Some(String::make(name));
    }
    return desc;
}

static std::string_view ResolveEffectTarget(const SceneNodeLayer&    layer,
                                            const SceneEffectTarget& target) {
    if (target.kind == SceneEffectTargetKind::Named && ! target.key.empty()) return target.key;
    return layer.CompositeTarget();
}

static bool LoadsPreviousAttachment(BlendMode mode) {
    return mode == BlendMode::Translucent || mode == BlendMode::Additive ||
           mode == BlendMode::AlphaToCoverage;
}

static void FillCopyTextureRequests(ExtraInfo& extra, vulkan::CopyPass::Desc& desc) {
    desc.src_request = BuildGraphTextureRequest(extra, desc.src);
    desc.dst_request = BuildGraphTextureRequest(extra, desc.dst);
}

static rg::TextureNodeRef AddCopyPass(ExtraInfo& extra, rg::TextureDesc in,
                                      Option<rg::TextureDesc> out_desc,
                                      bool preserve_source_across_frames) {
    rg::TextureNodeRef copy {};
    extra.rgraph->addPass<vulkan::CopyPass>(
        "copy"_str,
        rg::PassNode::Type::Copy,
        [&copy,
         in = std::move(in),
         out_desc = std::move(out_desc),
         preserve_source_across_frames,
         &extra](rg::RenderGraphBuilder& builder, vulkan::CopyPass::Desc& pdesc) {
            auto input = builder.createTexture(in);
            if (preserve_source_across_frames) builder.markVirtualWrite(input);

            auto state = builder.textureState(input);
            rstd_assert(state.is_some());
            if (state.is_none()) return;
            auto desc =
                out_desc.is_some() ? CloneTextureDesc(*out_desc) : CloneTextureDesc(state->desc);
            if (out_desc.is_none()) {
                auto suffix = rstd::format("_{}_copy", state->version);
                desc.key.push_str(suffix.as_str());
                desc.name.push_str(suffix.as_str());
            }
            copy = builder.createTexture(desc, true);
            rg::doCopy(builder, pdesc, input, copy);
            FillCopyTextureRequests(extra, pdesc);
            pdesc.dst_matches_src = out_desc.is_none();
            if (pdesc.dst_matches_src && pdesc.src_request.is_some()) {
                pdesc.dst_request       = Some(pdesc.src_request->clone());
                pdesc.dst_request->name = desc.key.clone();
            }
        });
    return copy;
}

static void AddCopyPass(ExtraInfo& extra, rg::TextureDesc in, rg::TextureDesc out) {
    (void)AddCopyPass(extra,
                      std::move(in),
                      Some<rg::TextureDesc>(std::move(out)),
                      false);
}

static rg::TextureNodeRef AddCopyPass(ExtraInfo& extra, rg::TextureNodeRef in,
                                      Option<rg::TextureDesc> out_desc = None()) {
    rg::TextureNodeRef copy {};
    extra.rgraph->addPass<vulkan::CopyPass>(
        "copy"_str,
        rg::PassNode::Type::Copy,
        [&copy, in, out_desc = std::move(out_desc), &extra](rg::RenderGraphBuilder& builder,
                                                            vulkan::CopyPass::Desc& pdesc) {
            auto state = builder.textureState(in);
            rstd_assert(state.is_some());
            if (state.is_none()) return;
            auto desc =
                out_desc.is_some() ? CloneTextureDesc(*out_desc) : CloneTextureDesc(state->desc);
            if (out_desc.is_none()) {
                auto suffix = rstd::format("_{}_copy", state->version);
                desc.key.push_str(suffix.as_str());
                desc.name.push_str(suffix.as_str());
            }
            copy = builder.createTexture(desc, true);
            rg::doCopy(builder, pdesc, in, copy);
            FillCopyTextureRequests(extra, pdesc);
            pdesc.dst_matches_src = out_desc.is_none();
            if (pdesc.dst_matches_src && pdesc.src_request.is_some()) {
                pdesc.dst_request       = Some(pdesc.src_request->clone());
                pdesc.dst_request->name = desc.key.clone();
            }
        });
    return copy;
}

static rg::TextureNodeRef AddMipFramebufferHistory(ExtraInfo&              extra,
                                                   rg::RenderGraphBuilder& builder) {
    if (extra.mip_framebuffer_history.is_some()) {
        return *extra.mip_framebuffer_history;
    }

    auto history_desc = MakeTextureDesc(extra, as_string_view(WE_MIP_MAPPED_FRAME_BUFFER));
    history_desc.kind = rg::TextureKind::Temp;
    auto history      = builder.createTexture(history_desc);
    builder.markVirtualWrite(history);
    extra.mip_framebuffer_history = Some<rg::TextureNodeRef>(history);
    return history;
}

static void StoreMipFramebufferHistory(ExtraInfo& extra) {
    if (extra.mip_framebuffer_history.is_none()) return;

    auto history_desc = MakeTextureDesc(extra, as_string_view(WE_MIP_MAPPED_FRAME_BUFFER));
    history_desc.kind = rg::TextureKind::Temp;
    AddCopyPass(
        extra, MakeTextureDesc(extra, as_string_view(SpecTex_Default)), rstd::move(history_desc));
}

static void AddMaterialTextureReads(SceneMaterial& material, std::string_view pass_output,
                                    ExtraInfo& extra, rg::RenderGraphBuilder& builder,
                                    vulkan::CustomShaderPass::Desc& pdesc,
                                    bool                            reuses_previous_output) {
    auto snapshots = HashMap<String, rg::TextureNodeRef>::make();
    for (std::size_t index = 0; index < material.textures.size(); ++index) {
        rstd_assert(index < material.texture_sources.len().to_primitive());
        if (index >= material.texture_sources.len().to_primitive()) {
            pdesc.texture_bindings.emplace_back();
            continue;
        }
        const auto&                source = material.texture_sources[usize(index)];
        Option<rg::TextureNodeRef> input;
        std::string                binding_key;
        if (source.kind == SceneMaterialTextureSourceKind::Empty) {
            pdesc.texture_bindings.emplace_back();
            continue;
        }
        if (source.kind == SceneMaterialTextureSourceKind::UnsupportedSpecial) {
            rstd_error("material '{}' references unsupported scene texture '{}'",
                       material.name,
                       source.key);
            pdesc.texture_bindings.emplace_back();
            continue;
        }
        if (source.kind == SceneMaterialTextureSourceKind::LayerOutput) {
            auto* link = extra.render_scene != nullptr && source.wallpaper_layer >= i32()
                             ? extra.render_scene->linkSource(WallpaperLayerId {
                                   .value = source.wallpaper_layer,
                               })
                             : nullptr;
            if (link == nullptr || source.layer.is_none() || link->scene_node != *source.layer ||
                extra.render_scene->renderTargetDesc(link->render_target) == nullptr) {
                rstd_error("material '{}' has unresolved linked layer {}",
                           material.name,
                           source.wallpaper_layer);
                pdesc.texture_bindings.emplace_back();
                continue;
            }
            binding_key = StdString(link->render_target_key);
            input       = Some(builder.createTexture(MakeTextureDesc(extra, binding_key)));
            builder.markVirtualWrite(*input);
        } else {
            binding_key = StdString(source.key);
            auto name   = as_str(binding_key).unwrap();
            if (source.kind == SceneMaterialTextureSourceKind::MipMappedFramebuffer) {
                input = Some(AddMipFramebufferHistory(extra, builder));
            } else {
                input = Some(builder.createTexture(MakeTextureDesc(extra, binding_key)));
            }
            if (IsSpecTex(name) && ! name.starts_with(WE_MIP_MAPPED_FRAME_BUFFER)) {
                builder.markVirtualWrite(*input);
            }
        }

        if (binding_key == pass_output && reuses_previous_output) {
            auto key      = as_str(binding_key).unwrap();
            auto snapshot = snapshots.get(key);
            if (snapshot.is_some()) {
                input = Some<rg::TextureNodeRef>(rg::TextureNodeRef {
                    .handle = (**snapshot).handle,
                });
            } else {
                builder.markSelfWrite(*input);
                input = Some(AddCopyPass(extra, *input));
                (void)snapshots.insert(String::make(key), *input);
            }
        }
        builder.read(*input);
        auto sampled_state = builder.textureState(*input);
        rstd_assert(sampled_state.is_some());
        if (sampled_state.is_none()) {
            pdesc.texture_bindings.emplace_back();
            continue;
        }
        auto sampled_key = StdString(sampled_state->desc.key);
        pdesc.texture_bindings.emplace_back(vulkan::TextureBindingRequest {
            .name    = String::make(as_str(sampled_key).unwrap()),
            .use     = Some(sampled_state->use),
            .request = BuildGraphTextureRequest(extra, sampled_key),
        });
    }
}

// ---- X3 M1：按最终采样区域反推裁剪（逐位等价，只少算不会被采样的像素） ----
//
// 区域一律用图层合成 RT 的归一化 UV 表示（u 向右、v 与显存行同向）；执行时按各输出的
// 实际像素尺寸换算成 scissor（CustomShaderPass::Record），半尺寸/自适应 FBO 自动适配。
// 从最终合成往前：P_final = 最终 pass 在屏幕内片元的 UV（含视差/抖动包络）⊕ 最终效果位移；
// 每个前级输出只需覆盖后级片元的采样区 ⊕ 后级位移，逐级膨胀。任何一步给不出上界就整层不裁。
namespace region_clip
{
struct Rect {
    double u0, v0, u1, v1;
    Rect Grow(double du, double dv) const { return { u0 - du, v0 - dv, u1 + du, v1 + dv }; }
};

// 采样坐标相对本片元 UV 的最大偏移（UV 单位）。
struct Displacement {
    double du { 0.0 }, dv { 0.0 };
};

// 读取效果参数；记录用到的参数，运行时 guard 逐帧核对它们没被脚本/用户属性改动。
struct ParamSnapshot {
    SceneMaterial*     material;
    std::string        name;
    std::vector<float> values;
};

class EffectParams {
public:
    EffectParams(SceneMaterial& material, double aspect, double min_width, double min_height,
                 std::vector<ParamSnapshot>& used)
        : m_material(material), m_aspect(aspect), m_min_width(min_width),
          m_min_height(min_height), m_used(used) {}

    // 常量优先，其次着色器注解默认值；被动画驱动的参数视为未知。
    Option<std::vector<float>> Get(const char* name) const {
        auto& shader = m_material.customShader;
        if (shader.valueAnimations.get(as_str(name).unwrap()).is_some()) return None();
        const ShaderValue* value = nullptr;
        if (auto it = shader.constValues.find(name); it != shader.constValues.end())
            value = &it->second;
        else if (shader.variant.is_some()) {
            auto& defaults = shader.variant->default_uniforms;
            if (auto it = defaults.find(name); it != defaults.end()) value = &it->second;
        }
        if (value == nullptr) return None();
        std::vector<float> out;
        for (usize i {}; i < value->size(); ++i) out.push_back((*value)[i]);
        m_used.push_back(ParamSnapshot { &m_material, name, out });
        return Some(rstd::move(out));
    }
    Option<double> Scalar(const char* name) const {
        auto v = Get(name);
        if (v.is_none() || v->empty() || ! std::isfinite((*v)[0])) return None();
        return Some(static_cast<double>((*v)[0]));
    }
    int Combo(const char* name) const {
        auto& variant = m_material.customShader.variant;
        if (variant.is_none()) return -1;
        auto it = variant->resolved_combos.find(name);
        if (it == variant->resolved_combos.end()) return 0;
        return std::atoi(it->second.c_str());
    }
    bool   HasVariant() const { return m_material.customShader.variant.is_some(); }
    double Aspect() const { return m_aspect; }          // 输入宽/高
    double MinWidth() const { return m_min_width; }     // 输入逻辑/物理宽的较小者（像素）
    double MinHeight() const { return m_min_height; }

private:
    SceneMaterial&              m_material;
    double                      m_aspect, m_min_width, m_min_height;
    std::vector<ParamSnapshot>& m_used;
};

using BoundFn = Option<Displacement> (*)(const EffectParams&);

// 逐像素效果：g_Texture0 只在 v_TexCoord.xy 采样（顶点着色器 v_TexCoord = a_TexCoord）。
Option<Displacement> Pointwise(const EffectParams&) { return Some(Displacement {}); }

// 内置 waterwaves（assets/effects/waterwaves）：texCoord += pow(|sin|, g_Exponent)
//   ·[pow(|sin|, g_Exponent2)]·方向单位向量·g_Strength²·mask（UV 单位，mask ∈ [0,1]）。
Option<Displacement> WaterWaves(const EffectParams& p) {
    if (! p.HasVariant()) return None();
    auto strength = p.Scalar("g_Strength"), exponent = p.Scalar("g_Exponent");
    if (strength.is_none() || exponent.is_none() || *exponent <= 0.0) return None();
    if (p.Combo("DUALWAVES") != 0) {
        auto exponent2 = p.Scalar("g_Exponent2");
        if (exponent2.is_none() || *exponent2 <= 0.0) return None();
    }
    const double d = *strength * *strength;
    return Some(Displacement { d, d });
}

// 位移上界表：按着色器名 + 源码指纹（.vert 与 .frag 文件内容的 FNV-1a 64）查。指纹不符（工程自带
// 改过的同名着色器、WPE 资源版本变化）就当未知。表外一律视为无界、整层不裁；不设"未知效果
// 保守常数"——自定义着色器可以缩放/镜像/环绕采样，任何常数都证明不了。
struct Rule {
    std::string_view shader;
    std::uint64_t    source_fnv;
    BoundFn          bound;
};
constexpr Rule kDisplacementTable[] = {
    { "effects/waterwaves", 1665525461160322644ull, WaterWaves },
    { "effects/pulse", 4691102007919585790ull, Pointwise },
};

std::uint64_t Fnv1a(std::uint64_t h, std::string_view bytes) {
    for (unsigned char c : bytes) h = (h ^ c) * 1099511628211ull;
    return h;
}

Option<std::uint64_t> ShaderSourceFnv(Scene& scene, const std::string& shader) {
    auto vfs = scene.ExtensionMut<fs::VFS>();
    if (vfs.is_none()) return None();
    std::uint64_t h = 14695981039346656037ull;
    for (const char* ext : { ".vert", ".frag" }) {
        auto content = fs::ReadFileContent(**vfs, "/assets/shaders/" + shader + ext);
        if (content.is_err()) return None();
        h = Fnv1a(h, *content);
        h = Fnv1a(h, "\n");
    }
    return Some(h);
}

BoundFn FindRule(Scene& scene, const std::string& shader, std::uint64_t& fnv) {
    auto hash = ShaderSourceFnv(scene, shader);
    fnv       = hash.is_some() ? *hash : 0;
    for (const auto& rule : kDisplacementTable)
        if (rule.shader == shader && hash.is_some() && *hash == rule.source_fnv) return rule.bound;
    return nullptr;
}

// 最终 pass 在屏幕（NDC 放大 grow_x/grow_y）内片元的 UV 包围盒。网格 UV 是位置的全局仿射函数时
// （卡片），用凸包裁剪后反映射；否则退回全部顶点 UV 包围盒（三角形内插 UV 不出顶点包围盒）。
Option<Rect> FinalSampledRegion(const SceneMesh& mesh, const Eigen::Matrix4d& mvp, double grow_x,
                                double grow_y) {
    std::vector<std::array<double, 4>> points; // ndc x, ndc y, u, v
    for (const auto& submesh : mesh.Submeshes()) {
        for (const auto& array : submesh.vertex_arrays) {
            auto offsets = array.GetAttrOffsetMap();
            auto pos     = offsets.find(std::string(as_string_view(WE_IN_POSITION)));
            auto uv      = offsets.find(std::string(as_string_view(WE_IN_TEXCOORD)));
            if (pos == offsets.end() || uv == offsets.end()) return None();
            const float* data   = array.Data();
            const usize  stride = array.OneSize();
            if (data == nullptr) continue;
            for (usize i {}; i < array.VertexCount(); ++i) {
                const float*          vtx = data + (i * stride).to_primitive();
                const float*          p   = vtx + pos->second.offset.to_primitive();
                const float*          t   = vtx + uv->second.offset.to_primitive();
                const Eigen::Vector4d clip =
                    mvp * Eigen::Vector4d(p[0], p[1], p[2], 1.0);
                if (std::abs(clip.w() - 1.0) > 1e-9) return None(); // 只接受正交
                points.push_back({ clip.x(), clip.y(), t[0], t[1] });
            }
        }
    }
    if (points.size() < 3) return None();
    for (const auto& q : points)
        for (double c : q)
            if (! std::isfinite(c)) return None();

    auto bbox_all = [&]() {
        Rect r { points[0][2], points[0][3], points[0][2], points[0][3] };
        for (const auto& q : points)
            r = { std::min(r.u0, q[2]), std::min(r.v0, q[3]), std::max(r.u1, q[2]),
                  std::max(r.v1, q[3]) };
        return r;
    };
    // 拟合 uv = A·[x y 1]；取面积最大的三点求解。
    std::size_t i0 = 0, i1 = 1, i2 = 2;
    double      best = 0.0;
    for (std::size_t a = 0; a < points.size(); ++a)
        for (std::size_t b = a + 1; b < points.size(); ++b)
            for (std::size_t c = b + 1; c < points.size() && points.size() <= 64; ++c) {
                const double area = std::abs((points[b][0] - points[a][0]) * (points[c][1] - points[a][1]) -
                                             (points[c][0] - points[a][0]) * (points[b][1] - points[a][1]));
                if (area > best) best = area, i0 = a, i1 = b, i2 = c;
            }
    if (! (best > 1e-12)) return Some(bbox_all());
    Eigen::Matrix3d m;
    m << points[i0][0], points[i0][1], 1.0, points[i1][0], points[i1][1], 1.0, points[i2][0],
        points[i2][1], 1.0;
    Eigen::Vector3d cu(points[i0][2], points[i1][2], points[i2][2]);
    Eigen::Vector3d cv(points[i0][3], points[i1][3], points[i2][3]);
    const Eigen::Vector3d au = m.fullPivLu().solve(cu), av = m.fullPivLu().solve(cv);
    for (const auto& q : points) {
        const double u = au.dot(Eigen::Vector3d(q[0], q[1], 1.0));
        const double v = av.dot(Eigen::Vector3d(q[0], q[1], 1.0));
        if (std::abs(u - q[2]) > 1e-6 || std::abs(v - q[3]) > 1e-6) return Some(bbox_all());
    }
    // 屏幕凸包（单调链）∩ 放大后的 NDC 矩形。
    std::vector<std::array<double, 2>> pts;
    for (const auto& q : points) pts.push_back({ q[0], q[1] });
    std::sort(pts.begin(), pts.end());
    pts.erase(std::unique(pts.begin(), pts.end()), pts.end());
    auto cross = [](const auto& o, const auto& a, const auto& b) {
        return (a[0] - o[0]) * (b[1] - o[1]) - (a[1] - o[1]) * (b[0] - o[0]);
    };
    std::vector<std::array<double, 2>> hull(2 * pts.size());
    std::size_t                        k = 0;
    for (std::size_t i = 0; i < pts.size(); ++i) {
        while (k >= 2 && cross(hull[k - 2], hull[k - 1], pts[i]) <= 0) --k;
        hull[k++] = pts[i];
    }
    for (std::size_t i = pts.size() - 1, t = k + 1; i > 0; --i) {
        while (k >= t && cross(hull[k - 2], hull[k - 1], pts[i - 1]) <= 0) --k;
        hull[k++] = pts[i - 1];
    }
    hull.resize(k > 0 ? k - 1 : 0);
    const double lim[4] = { -1.0 - grow_x, -1.0 - grow_y, 1.0 + grow_x, 1.0 + grow_y };
    for (int edge = 0; edge < 4 && ! hull.empty(); ++edge) {
        const int    axis = edge % 2;
        const bool   low  = edge < 2;
        auto inside = [&](const auto& q) { return low ? q[axis] >= lim[edge] : q[axis] <= lim[edge]; };
        std::vector<std::array<double, 2>> out;
        for (std::size_t i = 0; i < hull.size(); ++i) {
            const auto& a = hull[i];
            const auto& b = hull[(i + 1) % hull.size()];
            if (inside(a)) out.push_back(a);
            if (inside(a) != inside(b)) {
                const double t = (lim[edge] - a[axis]) / (b[axis] - a[axis]);
                out.push_back({ a[0] + t * (b[0] - a[0]), a[1] + t * (b[1] - a[1]) });
            }
        }
        hull = rstd::move(out);
    }
    if (hull.empty()) return Some(Rect { 0.0, 0.0, 0.0, 0.0 });
    Rect r { 1e300, 1e300, -1e300, -1e300 };
    for (const auto& q : hull) {
        const double u = au.dot(Eigen::Vector3d(q[0], q[1], 1.0));
        const double v = av.dot(Eigen::Vector3d(q[0], q[1], 1.0));
        r = { std::min(r.u0, u), std::min(r.v0, v), std::max(r.u1, u), std::max(r.v1, v) };
    }
    return Some(r);
}

struct LayerPlan {
    Option<std::array<double, 4>>                          source_region;
    std::unordered_map<const SceneImageEffectNode*, std::array<double, 4>> effect_regions;
    std::shared_ptr<const std::function<bool()>>           guard;
};

bool Near(const Eigen::Matrix4d& a, const Eigen::Matrix4d& b) { return a == b; }

// 返回 None 即整层不裁；reason 写日志。
Option<LayerPlan> PlanLayer(SceneNodeLayer& layer, Scene& scene, ExtraInfo& extra,
                            std::string& reason) {
    auto fail = [&](const char* why) {
        reason = why;
        return None<LayerPlan>();
    };
    if (extra.selection != nullptr &&
        (extra.selection->enabled || extra.selection->transparent_background))
        return fail("layer selection");
    if (layer.FinalResolveEffect() || layer.PublishedEffect() || layer.VisibleResolveEffect())
        return fail("resolve/published effect");
    if (! layer.PrefillNodes().empty()) return fail("prefill nodes");
    auto state_ext = scene.ExtensionMut<Arc<UniformSceneState>>();
    if (state_ext.is_none()) return fail("no uniform state");
    // 扩展由场景持有，生命周期覆盖渲染图；guard 里存裸指针以便 std::function 可拷贝。
    UniformSceneState* uniform_state = (**state_ext).as_ptr();

    const std::string composite(layer.CompositeTarget());
    auto              composite_rt = scene.RenderTarget(as_str(composite).unwrap());
    if (composite_rt.is_none()) return fail("no composite rt");
    const auto& crt = **composite_rt;
    if (crt.has_mipmap || crt.sample_count > 1 || crt.bind.enable || crt.preserve_on_write)
        return fail("composite rt kind");
    const double width  = rstd::as_cast<double>(crt.width);
    const double height = rstd::as_cast<double>(crt.height);
    const double min_w  = std::max(1.0, std::min(width, crt.physical_width > i32() ? rstd::as_cast<double>(crt.physical_width) : width));
    const double min_h  = std::max(1.0, std::min(height, crt.physical_height > i32() ? rstd::as_cast<double>(crt.physical_height) : height));

    // 依序收集效果 pass；v0 只接受单 pass、写合成 RT、g_Texture0 读合成 RT 的效果。
    std::vector<SceneImageEffectNode*> nodes;
    for (auto* eff : layer.ResolvedEffects()) {
        if (eff == nullptr) continue;
        if (! eff->commands.empty()) return fail("effect copy/swap commands");
        for (auto& n : eff->nodes) nodes.push_back(&n);
    }
    if (nodes.empty()) return fail("no effect pass");
    SceneImageEffectNode* final_node = nodes.back();
    {
        auto t = layer.ResolvedTarget(*final_node);
        if (t.kind != SceneEffectTargetKind::Named || t.key != std::string(as_string_view(SpecTex_Default)))
            return fail("final target not screen");
    }
    for (std::size_t i = 0; i + 1 < nodes.size(); ++i) {
        if (ResolveEffectTarget(layer, layer.ResolvedTarget(*nodes[i])) != composite)
            return fail("effect writes named fbo");
    }

    std::vector<ParamSnapshot> used_params;
    std::vector<Displacement>  displacement;
    for (auto* n : nodes) {
        SceneNode* node = n->sceneNode.as_ptr();
        if (node->Mesh() == nullptr || node->Mesh()->Submeshes().size() != 1 ||
            node->Mesh()->Material() == nullptr)
            return fail("effect mesh");
        auto& material = *node->Mesh()->Material();
        if (! material.customShader.shader) return fail("no shader");
        if (material.textures.empty() || material.textures[0] != composite)
            return fail("g_Texture0 not previous");
        for (std::size_t s = 1; s < material.textures.size(); ++s) {
            const auto& key = material.textures[s];
            if (key == composite || (! key.empty() && IsSpecTex(as_str(key).unwrap())))
                return fail("reads render target in extra slot");
        }
        std::uint64_t fnv  = 0;
        auto          rule = FindRule(scene, material.customShader.shader->name, fnv);
        if (rule == nullptr) {
            reason = rstd::cppstd::to_string(rstd::format("unknown effect {} fnv={}",
                                                            material.customShader.shader->name, fnv).as_str());
            return None();
        }
        EffectParams params(material, width / height, min_w, min_h, used_params);
        auto         bound = rule(params);
        if (bound.is_none()) {
            reason = "unbounded params " + material.customShader.shader->name;
            return None();
        }
        displacement.push_back(*bound);
    }

    // 最终 pass：静态正交投影、非透视/反射、不在世界空间。
    SceneNode* final_scene_node = final_node->sceneNode.as_ptr();
    if (final_scene_node->Perspective() ||
        (final_scene_node->Reflected() && scene.PlanarReflectionEnabled()))
        return fail("perspective/reflected final");
    for (auto* p = final_scene_node; p != nullptr; p = p->Parent())
        if (p->Perspective() || (p->Reflected() && scene.PlanarReflectionEnabled()))
            return fail("perspective/reflected parent");
    if (! final_scene_node->Camera().empty() && final_scene_node->Camera() != "global")
        return fail("final camera");
    const auto* node_state = uniform_state->FindNodeState(final_scene_node);
    if (node_state == nullptr || node_state->vertices_in_world_space)
        return fail("final uniform state");
    auto camera_ref = scene.CameraMut("global"_str);
    if (camera_ref.is_none() || (**camera_ref).IsPerspective()) return fail("camera");
    SceneCamera* camera = (*camera_ref).as_raw_ptr();
    auto*        mesh   = final_scene_node->Mesh();
    if (mesh == nullptr || mesh->Material() == nullptr) return fail("final mesh");
    // 最终 pass 的顶点着色器若做蒙皮，网格位置随骨骼动画变，静态投影不成立。
    if (mesh->Material()->customShader.variant.is_none()) return fail("final variant");
    {
        const auto& combos = mesh->Material()->customShader.variant->resolved_combos;
        for (const auto& key : { "SKINNING", "BONECOUNT", "MORPHING" }) {
            auto it = combos.find(key);
            if (it != combos.end() && it->second != "0") return fail("skinned final mesh");
        }
    }

    final_scene_node->UpdateTrans();
    const Eigen::Matrix4d model = final_scene_node->ModelTrans() *
                                  final_scene_node->GeometryTransform() * mesh->GeometryTransform();
    const Eigen::Matrix4d view_projection = camera->GetViewProjectionMatrix();
    std::vector<u64> mesh_generations;
    for (const auto& sm : mesh->Submeshes())
        for (const auto& va : sm.vertex_arrays) mesh_generations.push_back(va.DataGeneration());

    // 视差包络：|shift| ≤ (|节点 − 相机| + 0.5·ortho·|mouse_influence|)·|depth|·amount（指针在 [0,1]）；
    // 运行时直接用同一函数算本帧真实偏移核对，不依赖这里的推导。
    const auto   ortho    = uniform_state->Ortho();
    const auto   parallax = uniform_state->CameraParallax();
    double       parallax_x = 0.0, parallax_y = 0.0;
    if (parallax.enable) {
        auto effective = uniform_state->EffectiveParallax(*final_scene_node);
        if (effective.is_some()) {
            const Eigen::Vector3d cam = camera->GetPosition();
            const double dx = std::abs(model(0, 3) - cam.x()) + 0.5 * ortho[usize(0)] * std::abs(parallax.mouse_influence);
            const double dy = std::abs(model(1, 3) - cam.y()) + 0.5 * ortho[usize(1)] * std::abs(parallax.mouse_influence);
            parallax_x = dx * std::abs(effective->depth[usize(0)]) * std::abs(parallax.amount) * 1.01 + 1.0;
            parallax_y = dy * std::abs(effective->depth[usize(1)]) * std::abs(parallax.amount) * 1.01 + 1.0;
        }
    }
    // 抖动包络：ShakeOffset 各分量 ≤ 1.37·(1 + 7·grow)，再乘 amplitude·min(ortho)·0.01。
    const auto shake   = uniform_state->CameraShake();
    double     shake_w = 0.0;
    if (shake.enable && shake.amplitude > 0.0f && shake.speed > 0.0f) {
        const double r    = std::clamp(static_cast<double>(shake.roughness), 0.0, 2.0);
        const double over = std::clamp(r - 1.0, 0.0, 1.0);
        shake_w = 1.5 * (1.0 + 7.0 * over * over) * shake.amplitude *
                  std::min(ortho[usize(0)], ortho[usize(1)]) * 0.01;
    }
    const double ex = parallax_x + shake_w, ey = parallax_y + shake_w;
    const double grow_x = std::abs(view_projection(0, 0)) * ex + std::abs(view_projection(0, 1)) * ey;
    const double grow_y = std::abs(view_projection(1, 0)) * ex + std::abs(view_projection(1, 1)) * ey;

    auto final_region = FinalSampledRegion(*mesh, view_projection * model, grow_x, grow_y);
    if (final_region.is_none()) return fail("final mesh region");

    // 反推：P = 最终片元 UV ⊕ 最终位移；每级输出需覆盖 P（执行时再外扩 1 像素 + 取整），
    // 该级片元的采样区 = P 外扩 2 像素 ⊕ 该级位移。
    LayerPlan  plan;
    const double margin_u = 2.0 / min_w, margin_v = 2.0 / min_h;
    Rect         need     = final_region->Grow(displacement.back().du, displacement.back().dv);
    for (std::size_t i = nodes.size() - 1; i-- > 0;) {
        plan.effect_regions[nodes[i]] = { need.u0, need.v0, need.u1, need.v1 };
        need = need.Grow(margin_u + displacement[i].du, margin_v + displacement[i].dv);
    }
    plan.source_region = Some(std::array<double, 4> { need.u0, need.v0, need.u1, need.v1 });

    // 运行时 guard：本帧最终 pass 的模型/相机/网格/参数与规划时一致，且真实视差偏移在包络内。
    const float parallax_amount = parallax.amount, parallax_influence = parallax.mouse_influence;
    const bool  parallax_enable = parallax.enable;
    auto        guard = std::make_shared<const std::function<bool()>>(
        [=, used = rstd::move(used_params)]() -> bool {
            final_scene_node->UpdateTrans();
            const Eigen::Matrix4d now = final_scene_node->ModelTrans() *
                                        final_scene_node->GeometryTransform() * mesh->GeometryTransform();
            if (! Near(now, model)) return false;
            if (! Near(camera->GetViewProjectionMatrix(), view_projection)) return false;
            std::size_t g = 0;
            for (const auto& sm : mesh->Submeshes())
                for (const auto& va : sm.vertex_arrays)
                    if (g >= mesh_generations.size() || va.DataGeneration() != mesh_generations[g++])
                        return false;
            if (g != mesh_generations.size()) return false;
            const auto& cp = uniform_state->CameraParallax();
            if (cp.enable != parallax_enable || cp.amount != parallax_amount ||
                cp.mouse_influence != parallax_influence)
                return false;
            if (cp.enable) {
                const auto* st = uniform_state->FindNodeState(final_scene_node);
                if (st == nullptr) return false;
                const auto off = uniform_state->ComputeParallaxOffset(*st, *camera, SceneRenderViewKind::Primary);
                if (! (std::abs(off[usize(0)]) <= parallax_x && std::abs(off[usize(1)]) <= parallax_y))
                    return false;
            }
            const auto& cs = uniform_state->CameraShake();
            if (cs.enable != shake.enable || cs.amplitude != shake.amplitude ||
                cs.roughness != shake.roughness || (cs.speed > 0.0f) != (shake.speed > 0.0f))
                return false;
            const auto o = uniform_state->Ortho();
            if (o[usize(0)] != ortho[usize(0)] || o[usize(1)] != ortho[usize(1)]) return false;
            for (const auto& p : used) {
                auto& shader = p.material->customShader;
                if (shader.valueAnimations.get(as_str(p.name).unwrap()).is_some()) return false;
                const ShaderValue* value = nullptr;
                if (auto it = shader.constValues.find(p.name); it != shader.constValues.end())
                    value = &it->second;
                else if (shader.variant.is_some()) {
                    auto it = shader.variant->default_uniforms.find(p.name);
                    if (it != shader.variant->default_uniforms.end()) value = &it->second;
                }
                if (value == nullptr || value->size() != usize(p.values.size())) return false;
                for (usize i {}; i < value->size(); ++i)
                    if ((*value)[i] != p.values[i.to_primitive()]) return false;
            }
            return true;
        });
    plan.guard = guard;

    double full = 0.0, kept = 0.0;
    auto   account = [&](const std::array<double, 4>& r) {
        const double w = std::clamp(r[2], 0.0, 1.0) - std::clamp(r[0], 0.0, 1.0);
        const double h = std::clamp(r[3], 0.0, 1.0) - std::clamp(r[1], 0.0, 1.0);
        full += 1.0;
        kept += std::max(0.0, w) * std::max(0.0, h);
    };
    account(*plan.source_region);
    for (std::size_t i = 0; i + 1 < nodes.size(); ++i) account(plan.effect_regions[nodes[i]]);
    rstd_info("region clip {}: passes={} final_uv=[{},{},{},{}] source_uv=[{},{},{},{}] clipped_fraction={} envelope_world=({},{})",
              composite, full, final_region->u0, final_region->v0, final_region->u1,
              final_region->v1, need.u0, need.v0, need.u1, need.v1, 1.0 - kept / full, ex, ey);
    return Some(rstd::move(plan));
}
} // namespace region_clip

static SceneNodeLayer* ToGraphPass(SceneNode* node, std::string_view output, ExtraInfo& extra,
                                   bool                defer_effect = false,
                                   SceneRenderViewKind render_view  = SceneRenderViewKind::Primary,
                                   SceneImageEffectNode* effect_node = nullptr);

static void LoadGraphEffects(SceneNodeLayer* effs, ExtraInfo& extra) {
    for (auto* eff : effs->ResolvedEffects()) {
        if (eff == nullptr) continue;
        auto cmdItor = eff->commands.begin();
        auto cmdEnd  = eff->commands.end();
        int  nodePos = 0;
        auto emit_commands = [&]() {
            while (cmdItor != cmdEnd && nodePos == cmdItor->afterpos.to_primitive()) {
                auto source = ResolveEffectTarget(*effs, cmdItor->src);
                auto target = ResolveEffectTarget(*effs, cmdItor->dst);
                switch (cmdItor->cmd) {
                case SceneImageEffect::CmdType::Copy:
                    (void)AddCopyPass(extra,
                                      MakeTextureDesc(extra, source),
                                      Some(MakeTextureDesc(extra, target)),
                                      true);
                    break;
                case SceneImageEffect::CmdType::Swap: {
                    auto temporary = AddCopyPass(extra,
                                                 MakeTextureDesc(extra, source),
                                                 None<rg::TextureDesc>(),
                                                 true);
                    (void)AddCopyPass(extra,
                                      MakeTextureDesc(extra, target),
                                      Some(MakeTextureDesc(extra, source)),
                                      true);
                    (void)AddCopyPass(
                        extra, temporary, Some(MakeTextureDesc(extra, target)));
                    break;
                }
                }
                ++cmdItor;
            }
        };
        for (auto& n : eff->nodes) {
            emit_commands();
            auto target = effs->ResolvedTarget(n);
            n.graph_pass_index = None();
            ToGraphPass(n.sceneNode.as_ptr(), ResolveEffectTarget(*effs, target), extra,
                        false, SceneRenderViewKind::Primary, &n);
            nodePos++;
        }
        emit_commands();
        if (cmdItor != cmdEnd) {
            rstd_error("effect '{}' command afterpos {} exceeds pass count {}",
                       eff->name,
                       cmdItor->afterpos,
                       nodePos);
        }
    }
}

static SceneNodeLayer* ToGraphPass(SceneNode* node, std::string_view output, ExtraInfo& extra,
                                   bool defer_effect, SceneRenderViewKind render_view,
                                   SceneImageEffectNode* effect_node) {
    auto& rgraph = *extra.rgraph;
    auto& scene  = *extra.scene;

    if (node->Mesh() == nullptr) return nullptr;
    auto* mesh = node->Mesh();
    if (mesh->Submeshes().empty()) return nullptr;
    const auto& slots = mesh->MaterialSlots();

    SceneNodeLayer* imgeff = nullptr;
    if (node->HasLayer()) {
        auto*      effect       = node->Layer().get();
        const bool intermediate = effect->RequiresIntermediateTarget();
        effect->ConfigureSourceDraw(intermediate);
        if (intermediate) {
            imgeff = effect;
            imgeff->ResolveEffect(*scene.DefaultEffectMesh(), "effect");
            output = imgeff->CompositeTarget();
            (void)extra.transient_texture_families.insert(String::make(as_str(output).unwrap()));
        }
    }
    if (imgeff != nullptr) {
        for (auto& prefill : imgeff->PrefillNodes()) {
            auto prefill_output = ResolveEffectTarget(*imgeff, prefill.output);
            ToGraphPass(prefill.sceneNode.as_ptr(), prefill_output, extra);
        }
    }

    Option<std::array<double, 4>>                source_region;
    std::shared_ptr<const std::function<bool()>> source_guard;
    if (imgeff != nullptr && ! defer_effect && render_view == SceneRenderViewKind::Primary &&
        imgeff->HasRenderEffects()) {
        std::string reason;
        if (auto plan = region_clip::PlanLayer(*imgeff, scene, extra, reason); plan.is_some()) {
            source_region = plan->source_region;
            source_guard  = plan->guard;
            for (auto& [n, r] : plan->effect_regions) extra.region_clip[n] = { r, plan->guard };
        } else {
            rstd_info("region clip {}: skipped ({})", imgeff->CompositeTarget(), reason);
        }
    }

    const bool draw_source = imgeff == nullptr || imgeff->RequiresSourceDraw();
    for (std::size_t smi = 0; draw_source && smi < mesh->Submeshes().size(); smi++) {
        const auto& submesh       = mesh->Submeshes()[smi];
        const auto  material_slot = submesh.material_slot.to_primitive();
        if (material_slot >= slots.size() || ! slots[material_slot]) continue;
        SceneMaterial* material = slots[material_slot].get();
        scene.ResolveMaterialTextureSources(*material);
        std::shared_ptr<SceneMaterial> material_override;
        if (imgeff != nullptr && submesh.output_override.empty() && ! submesh.preserve_output) {
            auto source_blend = imgeff->IntermediateSourceBlend();
            if (source_blend.is_some()) {
                material_override           = std::make_shared<SceneMaterial>(*material);
                material_override->blenmode = *source_blend;
            }
        }
        std::string passName = material->name;
        // Per-submesh output override (clipping-mask submeshes write into a
        // shared RT that the main puppet pass samples via g_Texture8).
        std::string_view pass_output =
            submesh.output_override.empty() ? output : std::string_view(submesh.output_override);

        rgraph.addPass<vulkan::CustomShaderPass>(
            rstd::cppstd::as_str(passName).unwrap(),
            rg::PassNode::Type::CustomShader,
            [material,
             node,
             smi,
             pass_output,
             material_override,
             preserve_output = submesh.preserve_output,
             render_view,
             effect_node,
             source_region = submesh.output_override.empty() ? source_region : None(),
             source_guard,
             &scene,
             &extra](rg::RenderGraphBuilder& builder, vulkan::CustomShaderPass::Desc& pdesc) {
                if (effect_node != nullptr) {
                    if (auto it = extra.region_clip.find(effect_node); it != extra.region_clip.end()) {
                        pdesc.region_uv    = Some(it->second.first);
                        pdesc.region_guard = it->second.second;
                    }
                } else if (source_region.is_some()) {
                    pdesc.region_uv    = source_region;
                    pdesc.region_guard = source_guard;
                }
                const auto& pass        = builder.workPassNode();
                if (effect_node != nullptr)
                    effect_node->graph_pass_index = Some(u64(pass.handle.index.to_primitive()));
                pdesc.node              = Some(rstd::mut_ref<SceneNode>::from_raw_parts(node));
                pdesc.submesh_index     = u32(static_cast<rstd::uint32_t>(smi));
                pdesc.graph_pass_index  = pass.pass.index;
                pdesc.render_view       = render_view;
                pdesc.material_override = material_override;
                if (auto node_id = scene.ResourceIndex().nodeId(*node)) {
                    if (auto draw_item = scene.ResourceIndex().drawItemFor(
                            *node_id, u32(static_cast<rstd::uint32_t>(smi)))) {
                        pdesc.draw_item = *draw_item;
                        if (extra.render_scene != nullptr) {
                            if (auto render_item = extra.render_scene->renderItemFor(*draw_item)) {
                                pdesc.render_item = *render_item;
                            }
                        }
                    }
                }
                std::string pass_output_s(pass_output);
                auto        output_rt = scene.RenderTarget(as_str(pass_output_s).unwrap());
                rstd_assert(output_rt.is_some());
                if (output_rt.is_none()) return;
                const auto& output_target = **output_rt;
                const auto* pass_material = material_override ? material_override.get() : material;
                const bool  reuses_previous_output =
                    ! (output_target.force_clear && ! preserve_output) &&
                    (output_target.preserve_on_write || preserve_output ||
                     LoadsPreviousAttachment(pass_material->blenmode));

                pdesc.output = pass_output_s;
                AddMaterialTextureReads(
                    *material, pass_output, extra, builder, pdesc, reuses_previous_output);

                auto output_node =
                    builder.createTexture(MakeTextureDesc(extra, pass_output_s), true);
                auto output_state = builder.textureState(output_node);
                rstd_assert(output_state.is_some());
                if (output_state.is_none()) return;
                const bool first_output_write = output_state->version == usize();
                if (! first_output_write && reuses_previous_output) {
                    builder.reusePreviousAllocation(output_node);
                }
                pdesc.output_use     = Some(output_state->use);
                pdesc.output_request = BuildGraphTextureRequest(extra, pass_output_s);
                pdesc.samples        = vulkan::TextureSampleCount(output_target.sample_count);
                if (pdesc.samples != VK_SAMPLE_COUNT_1_BIT) {
                    auto twin_name            = vulkan::MsaaTwinName(pass_output_s, pdesc.samples);
                    pdesc.output_msaa_request = Some(
                        vulkan::MakeMsaaTextureRequest(twin_name, output_target, pdesc.samples));
                    auto msaa_node = builder.createTexture(
                        rg::TextureDesc {
                            .name    = String::make(rstd::cppstd::as_str(twin_name).unwrap()),
                            .key     = String::make(rstd::cppstd::as_str(twin_name).unwrap()),
                            .kind    = rg::TextureKind::Temp,
                            .request = Some(pdesc.output_msaa_request->clone()),
                        },
                        true);
                    auto msaa_state = builder.textureState(msaa_node);
                    if (msaa_state) pdesc.output_msaa_use = Some(msaa_state->use);
                }
                pdesc.transparent_clear = first_output_write && (output_target.clear_on_first_write ||
                    (output_target.bind.screen && extra.selection != nullptr && extra.selection->transparent_background));
                pdesc.capture_composition = output_target.bind.screen && extra.selection != nullptr && extra.selection->transparent_background;
                pdesc.clear_output =
                    ! preserve_output &&
                    ((first_output_write && output_target.bind.screen) || pdesc.transparent_clear);
                pdesc.preserve_output = output_state->version > usize() &&
                                        (output_target.preserve_on_write || preserve_output);
                const bool uses_depth =
                    output_target.withDepth && vulkan::UsesDepthAttachment(*pass_material);
                pdesc.has_depth_attachment = uses_depth;
                if (uses_depth) {
                    auto depth_name = pass_output_s + "::depth";
                    pdesc.depth_request =
                        Some(vulkan::MakeDepthTextureRequest(depth_name, output_target));
                    auto depth_node = builder.createTexture(
                        rg::TextureDesc {
                            .name    = String::make(rstd::cppstd::as_str(depth_name).unwrap()),
                            .key     = String::make(rstd::cppstd::as_str(depth_name).unwrap()),
                            .kind    = rg::TextureKind::Temp,
                            .request = Some(pdesc.depth_request->clone()),
                        },
                        true);
                    auto depth_state = builder.textureState(depth_node);
                    if (depth_state) pdesc.depth_use = Some(depth_state->use);
                }
                pdesc.clear_depth =
                    uses_depth && (pdesc.clear_output || output_target.force_clear ||
                                   extra.depth_initialized_outputs.count(pass_output_s) == 0);
                if (uses_depth) {
                    extra.depth_initialized_outputs.insert(pass_output_s);
                } else if (pdesc.clear_output || output_target.force_clear) {
                    extra.depth_initialized_outputs.erase(pass_output_s);
                }
                builder.write(output_node);
            });
    }

    if (! defer_effect && imgeff != nullptr && imgeff->HasRenderEffects())
        LoadGraphEffects(imgeff, extra);
    return imgeff;
}

// Bottom-up collect: identify SceneNode subtrees whose every node can be
// elided without losing a link source. Visibility-hidden ancestors also hide
// anonymous/generated descendants such as particle children, so the skip set
// is keyed by node pointer instead of WE layer id.
static bool CollectEmitSkipSubtrees(SceneNode* node, Scene& scene, const BTreeSet<i32>& linked_ids,
                                    Set<const SceneNode*>& out_skip,
                                    i32                    force_visible_owner,
                                    bool                   visibility_hidden_ancestor = false,
                                    bool                   force_visible_ancestor = false) {
    const auto wallpaper   = node->WallpaperIdentity();
    const auto link_source = scene.ResolveLayerLinkSource(*node);
    const i32 layer_id = link_source.is_some() ? link_source->value
                                               : (wallpaper.is_some() ? wallpaper->value : i32(-1));
    const bool linked  = link_source.is_some() && linked_ids.contains(link_source->value);
    const bool forced = (force_visible_ancestor && wallpaper.is_none()) ||
        (wallpaper.is_some() && wallpaper->value == force_visible_owner);
    // Refresh all nodes, including skipped ones, so a later ordinary graph
    // cannot retain a previous capture-only alpha override.
    node->SetCaptureForceVisibilityAlpha(forced);
    const bool visibility_hidden_self =
        (! node->Visible() || (layer_id >= i32() && scene.IsLayerVisibilityElidable(
                                                        WallpaperLayerId { .value = layer_id }))) &&
        ! linked && ! forced;
    const bool visibility_hidden = visibility_hidden_ancestor || visibility_hidden_self;

    bool all_children_skippable = true;
    for (auto& c : node->GetChildren()) {
        if (! CollectEmitSkipSubtrees(c.as_ptr(), scene, linked_ids, out_skip, force_visible_owner,
                                      visibility_hidden, forced))
            all_children_skippable = false;
    }
    const bool self_skippable =
        ! linked && ! forced &&
        (visibility_hidden ||
         (layer_id >= i32() && scene.IsLayerElidable(WallpaperLayerId { .value = layer_id })));
    if (self_skippable && all_children_skippable) {
        out_skip.insert(node);
        return true;
    }
    return false;
}

static bool ShouldSkipNoRuntimeEffect(SceneNode* node, Scene& scene) {
    (void)scene;
    if (node == nullptr || ! node->HasLayer()) return false;
    const auto& effect_layer = node->Layer();
    return effect_layer && effect_layer->SkipWhenNoRuntimeEffect() &&
           ! effect_layer->PublishesOutput() && effect_layer->EffectCount() > usize() &&
           ! effect_layer->HasRuntimeVisibleEffect();
}

static void ConfigureNestedOutput(SceneNode* node, std::string_view output,
                                  std::string_view inherited_camera) {
    if (! inherited_camera.empty() && node->Camera().empty()) {
        node->SetCamera(std::string(inherited_camera));
    }
    if (! node->HasLayer()) return;
    auto& effect_layer = node->Layer();
    if (! effect_layer) return;
    auto output_text = as_str(output).unwrap();
    if (output_text != SpecTex_Default &&
        as_str(effect_layer->FinalTarget()).unwrap() == SpecTex_Default) {
        effect_layer->SetFinalTarget(std::string(output));
    }
    if (effect_layer->FinalTarget() == output) {
        effect_layer->SetFinalCamera(std::string(inherited_camera));
    }
}

static void EmitSceneNode(SceneNode* node, std::string_view inherited_output,
                          std::string_view inherited_camera, ExtraInfo& extra,
                          const Set<const SceneNode*>& emit_skip_subtrees,
                          const BTreeSet<i32>&         linked_ids, i32 force_visible_owner,
                          std::int32_t inherited_owner = -1) {
    if (node == nullptr || emit_skip_subtrees.count(node) != 0) return;

    auto&      scene       = *extra.scene;
    const auto wallpaper   = node->WallpaperIdentity();
    const auto link_source = scene.ResolveLayerLinkSource(*node);
    const i32 layer_id = link_source.is_some() ? link_source->value
                                               : (wallpaper.is_some() ? wallpaper->value : i32(-1));
    const auto generator = node->GeneratorIdentity();
    const std::int32_t capture_owner = generator.is_some() ? generator->value.to_primitive() :
        (wallpaper.is_some() ? wallpaper->value.to_primitive() : inherited_owner);
    const bool selected = extra.selection == nullptr || extra.selection->contains(capture_owner);
    const bool forced = force_visible_owner >= i32() && capture_owner == force_visible_owner.to_primitive();
    const bool       elidable = !forced &&
        (!selected || scene.IsLayerElidable(WallpaperLayerId { .value = layer_id }));
    const bool       linked   = link_source.is_some() && linked_ids.contains(link_source->value);
    bool             emit     = true;
    std::string      link_output;
    std::string_view node_output = inherited_output;

    if (! linked && ! forced && ShouldSkipNoRuntimeEffect(node, scene)) emit = false;
    if (elidable) {
        if (! linked && ! forced) {
            emit = false;
        } else {
            auto* source_record = extra.render_scene->linkSource(*link_source);
            if (source_record == nullptr) {
                rstd_error("link render target for layer {} not found in snapshot", layer_id);
                emit = false;
            } else {
                link_output = rstd::cppstd::to_string(source_record->render_target_key);
                node_output = link_output;
                if (node->HasLayer() && ! node->Layer()->PublishesOutput()) {
                    node->Layer()->SetFinalTarget(link_output);
                    node->Layer()->SetFinalLocal(true);
                }
            }
        }
    }

    auto group_camera =
        wallpaper.is_some() ? scene.RenderGroupCamera(*wallpaper) : None<ref<str>>();
    if (emit && group_camera) {
        ConfigureNestedOutput(node, node_output, inherited_camera);
        auto* effect_layer = ToGraphPass(node, node_output, extra, true);
        if (effect_layer == nullptr) {
            rstd_error("render group layer {} has no effect target", wallpaper->value);
        }
        const std::string_view child_output =
            effect_layer == nullptr ? node_output
                                    : std::string_view(effect_layer->CompositeTarget());
        for (auto& child : node->GetChildren()) {
            EmitSceneNode(child.as_ptr(),
                          child_output,
                          rstd::cppstd::as_string_view(*group_camera),
                          extra,
                          emit_skip_subtrees,
                          linked_ids, force_visible_owner, capture_owner);
        }
        if (effect_layer != nullptr && effect_layer->HasRenderEffects()) {
            LoadGraphEffects(effect_layer, extra);
        }
        return;
    }

    if (emit) {
        ConfigureNestedOutput(node, node_output, inherited_camera);
        ToGraphPass(node, node_output, extra);
    }
    for (auto& child : node->GetChildren()) {
        EmitSceneNode(child.as_ptr(),
                      inherited_output,
                      inherited_camera,
                      extra,
                      emit_skip_subtrees,
                      linked_ids, force_visible_owner, capture_owner);
    }
}

static bool SamplesPlanarReflection(SceneNode& node) {
    auto* mesh = node.Mesh();
    if (mesh == nullptr) return false;
    for (const auto& material : mesh->MaterialSlots()) {
        if (! material) continue;
        for (const auto& texture : material->textures) {
            if (as_str(texture).unwrap().starts_with(WE_REFLECTION_PREFIX)) return true;
        }
    }
    return false;
}

static void EmitPlanarReflectionNode(SceneNode* node, ExtraInfo& extra,
                                     const Set<const SceneNode*>& emit_skip_subtrees) {
    if (node == nullptr || emit_skip_subtrees.count(node) != 0) return;
    if (node->Reflected() && ! SamplesPlanarReflection(*node)) {
        ToGraphPass(node,
                    as_string_view(WE_REFLECTION_PREFIX),
                    extra,
                    true,
                    SceneRenderViewKind::Reflection);
    }
    for (auto& child : node->GetChildren()) {
        EmitPlanarReflectionNode(child.as_ptr(), extra, emit_skip_subtrees);
    }
}

static void EmitShadowPasses(ExtraInfo& extra) {
    if (extra.render_scene == nullptr) return;
    auto definitions = extra.render_scene->ShadowDefinitions();
    if (definitions.is_empty()) return;
    const auto& definition = definitions[usize()];
    const auto  target     = StdString(definition.target);
    bool        first      = true;

    for (const auto& caster : extra.render_scene->ShadowCasters()) {
        const auto* item = extra.render_scene->renderItem(caster.render_item);
        if (item == nullptr || ! caster.material) continue;
        auto draw = extra.scene->ResourceIndex().resolve(item->scene_draw_item);
        if (draw.is_none() || draw->node == nullptr || draw->mesh == nullptr) continue;
        extra.scene->ResolveMaterialTextureSources(*caster.material);

        extra.rgraph->addPass<vulkan::CustomShaderPass>(
            caster.material->name.empty() ? "shadow"_str : as_str(caster.material->name).unwrap(),
            rg::PassNode::Type::CustomShader,
            [&,
             item,
             draw,
             material       = caster.material,
             instance_count = caster.instance_count,
             clear_depth    = first,
             target](rg::RenderGraphBuilder& builder, vulkan::CustomShaderPass::Desc& pdesc) {
                const auto& pass        = builder.workPassNode();
                pdesc.node              = Some(mut_ref<SceneNode>::from_raw_parts(draw->node));
                pdesc.draw_item         = item->scene_draw_item;
                pdesc.render_item       = item->id;
                pdesc.submesh_index     = item->submesh_index;
                pdesc.graph_pass_index  = pass.pass.index;
                pdesc.material_override = material;
                pdesc.depth_only        = true;
                pdesc.instance_count    = instance_count;
                pdesc.output            = target;
                pdesc.clear_depth       = clear_depth;

                pdesc.viewports.reserve(definition.viewports.len());
                pdesc.scissors.reserve(definition.viewports.len());
                for (const auto& viewport : definition.viewports) {
                    pdesc.viewports.push(VkViewport {
                        .x        = viewport.x.to_primitive(),
                        .y        = viewport.y.to_primitive(),
                        .width    = viewport.width.to_primitive(),
                        .height   = viewport.height.to_primitive(),
                        .minDepth = 0.0f,
                        .maxDepth = 1.0f,
                    });
                    pdesc.scissors.push(VkRect2D {
                        .offset = { viewport.scissor_x.to_primitive(),
                                    viewport.scissor_y.to_primitive() },
                        .extent = { viewport.scissor_width.to_primitive(),
                                    viewport.scissor_height.to_primitive() },
                    });
                }

                AddMaterialTextureReads(*material, target, extra, builder, pdesc, true);
                auto atlas = builder.createTexture(MakeTextureDesc(extra, target), true);
                auto state = builder.textureState(atlas);
                if (state.is_none()) return;
                pdesc.depth_use     = Some(state->use);
                pdesc.depth_request = BuildGraphTextureRequest(extra, target);
                builder.write(atlas);
            });
        first = false;
    }
}

Box<rg::RenderGraph> owe::sceneToRenderGraph(Scene&                     scene,
                                             const RenderSceneSnapshot& render_scene,
                                             const RenderLayerSelection* selection,
                                             const RenderCaptureTarget* capture_target) {
    auto      rgraph = Box<rg::RenderGraph>::make();
    ExtraInfo extra { .rgraph = rgraph.get(), .scene = &scene, .render_scene = &render_scene, .selection = selection };

    // The snapshot owns link-consumer discovery; graph build only consumes the
    // resulting source ids.
    const auto& linked_ids = render_scene.LinkedLayerIds();
    const i32 force_visible_owner = capture_target != nullptr && capture_target->force_visible_owner
                                        ? i32(capture_target->owner_layer_id) : i32(-1);

    // Skip subtrees the parser tagged as elidable (user-hidden, or no-effect
    // identity passthrough layers) when nothing in the subtree links anything.
    // Most corpora have ~25x more elidable layers than link-referenced ones;
    // the skip set lets the emit walk short-circuit without mutating the tree.
    Set<const SceneNode*> emit_skip_subtrees;
    CollectEmitSkipSubtrees(scene.RootMut().as_raw_ptr(), scene, linked_ids, emit_skip_subtrees,
                            force_visible_owner);

    EmitShadowPasses(extra);

    if (scene.PlanarReflectionEnabled()) {
        EmitPlanarReflectionNode(scene.RootMut().as_raw_ptr(), extra, emit_skip_subtrees);
    }

    EmitSceneNode(scene.RootMut().as_raw_ptr(),
                  as_string_view(SpecTex_Default),
                  {},
                  extra,
                  emit_skip_subtrees,
                  linked_ids, force_visible_owner);

    // Emit global post-process passes after the main scene-graph traversal.
    // Each step is either a CustomShaderPass (built on the synthetic node's
    // mesh+material) or a CopyPass (RT-to-RT blit).
    auto post_processes = scene.PostProcesses();
    for (usize index {}; index < post_processes.len() &&
        (selection == nullptr || selection->include_postprocessing); ++index) {
        const auto& pp = post_processes[index];
        for (auto& step : pp->steps) {
            if (step.is_Pass()) {
                auto&            sp     = step.as_Pass().value;
                std::string_view target = sp.output.empty() ? as_string_view(SpecTex_Default)
                                                            : std::string_view(sp.output);
                ToGraphPass(sp.node.as_ptr(), target, extra);
            } else {
                auto& cp = step.as_Copy().value;
                AddCopyPass(extra, MakeTextureDesc(extra, cp.src), MakeTextureDesc(extra, cp.dst));
            }
        }
    }

    StoreMipFramebufferHistory(extra);

    scene.RebuildResourceIndex();
    return rgraph;
}

Box<rg::RenderGraph> owe::sceneToRenderGraph(Scene& scene) {
    auto render_scene = ExtractRenderSceneSnapshot(scene);
    return sceneToRenderGraph(scene, render_scene);
}
