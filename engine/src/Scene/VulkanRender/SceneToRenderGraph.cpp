module;

#include <rstd/macro.hpp>
#include <cstdint>
#include <cstring>
#include <string>

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
    // X3 M1：离线捕获（图层/效果/FBO 抓取）会直接读中间 RT，区域反推不建模这类读取，整场景不裁。
    bool capture { false };
    // X3 M1：效果 pass → （输出中需保留的 UV 矩形，逐帧 guard）
    std::unordered_map<const SceneImageEffectNode*,
                       std::shared_ptr<const std::function<Option<std::array<double, 4>>()>>>
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
    Rect Union(const Rect& o) const {
        return { std::min(u0, o.u0), std::min(v0, o.v0), std::max(u1, o.u1), std::max(v1, o.v1) };
    }
};

// 某个 RT（某一版本）在本帧需要保留的区域：无人读 / 矩形 / 整幅。
struct Region {
    enum class Kind
    {
        Nothing,
        Some,
        Full
    };
    Kind kind { Kind::Nothing };
    Rect r {};
    void Add(const Rect& x) {
        if (kind == Kind::Full) return;
        if (! (std::isfinite(x.u0) && std::isfinite(x.v0) && std::isfinite(x.u1) && std::isfinite(x.v1))) {
            kind = Kind::Full;
            return;
        }
        r    = kind == Kind::Nothing ? x : r.Union(x);
        kind = Kind::Some;
    }
    void AddFull() { kind = Kind::Full; }
};

// 采样坐标相对本片元 UV 的最大偏移（UV 单位）；extra 是只在效果支撑区内才会读到的额外采样
// 范围（UV，已含位移），与"片元 ⊕ 偏移"取并集。
struct Displacement {
    double       du { 0.0 }, dv { 0.0 };
    Option<Rect> extra;
};

// 读取效果参数；记录用到的参数，运行时 guard 逐帧核对它们没被脚本/用户属性改动。
struct ParamSnapshot {
    SceneMaterial*     material;
    std::string        name;
    std::vector<float> values;
};

// g_Texture0 所读 RT 的尺寸（像素）。min_* 取逻辑/物理较小者，aspect_* 取两者宽高比的范围。
struct InputExtent {
    double min_w { 1.0 }, min_h { 1.0 }, aspect_lo { 1.0 }, aspect_hi { 1.0 };
};

class EffectParams {
public:
    EffectParams(SceneMaterial& material, InputExtent input, std::vector<ParamSnapshot>& used,
                 bool skinned_final)
        : m_material(material), m_input(input), m_used(used), m_skinned_final(skinned_final) {}

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
        for (float f : out)
            if (! std::isfinite(f)) return None();
        m_used.push_back(ParamSnapshot { &m_material, name, out });
        return Some(rstd::move(out));
    }
    Option<double> Scalar(const char* name) const {
        auto v = Get(name);
        if (v.is_none() || v->empty()) return None();
        return Some(static_cast<double>((*v)[0]));
    }
    Option<std::array<double, 2>> Vec2(const char* name) const {
        auto v = Get(name);
        if (v.is_none() || v->size() < 2) return None();
        return Some(std::array<double, 2> { (*v)[0], (*v)[1] });
    }
    // 着色器实际编译用的组合值；没解析到时按注解默认值 dflt（由规则按源码填写）。
    int Combo(const char* name, int dflt = 0) const {
        auto& variant = m_material.customShader.variant;
        if (variant.is_none()) return -1;
        auto it = variant->resolved_combos.find(name);
        if (it == variant->resolved_combos.end()) return dflt;
        return std::atoi(it->second.c_str());
    }
    bool               HasVariant() const { return m_material.customShader.variant.is_some(); }
    const InputExtent& Input() const { return m_input; }
    bool               SkinnedFinal() const { return m_skinned_final; }

private:
    SceneMaterial&              m_material;
    InputExtent                 m_input;
    std::vector<ParamSnapshot>& m_used;
    bool                        m_skinned_final;
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
    return Some(Displacement { d, d, None() });
}

// genericimage2/3/4（图层自身材质，常作最终合成 pass）：v_TexCoord.xy = a_TexCoord，片元只在
// v_TexCoord.xy 采 g_Texture0。SPRITESHEET 改纹理坐标，MORPHING 改位置，PRELIGHTING/LIGHTING
// 换矩阵路径，一律当未知。SKINNING 只在最终 pass 走逐帧 CPU 蒙皮包围盒时接受：三份着色器
// 的蒙皮都是 localPos = Σ w_k·(a_Position·g_Bones[i_k])，与 CPU 端 SkinnedFinalRegion 同式。
Option<Displacement> GenericImage(const EffectParams& p) {
    if (! p.HasVariant()) return None();
    for (const char* combo : { "SPRITESHEET", "MORPHING", "PRELIGHTING", "LIGHTING" })
        if (p.Combo(combo) != 0) return None();
    if (p.Combo("SKINNING") != 0 && ! p.SkinnedFinal()) return None();
    return Some(Displacement {});
}

// twirl（效果目录 effects/twirl，芙莉莲工程自带版本）：
//   t = [ELLIPTICAL: Rot(g_Axis)·] ((p − c)·(aspect, 1))，[ELLIPTICAL: t.x *= g_Ratio]；
//   feather = smoothstep(size + fea + 1e-5, size − fea, |t|)：|t| ≥ size + fea + 1e-5 时为 0，
//   此时 mix(v_TexCoord, ·, 0) 原样返回片元 UV（逐像素）。支撑区内 t 只被旋转（anim/噪声都只
//   改角度），|t| 不变，反变换回 UV 后 |Δx| ≤ R·k/aspect、|Δy| ≤ R·k（k = ELLIPTICAL ?
//   max(1, 1/ratio) : 1）。REPEAT 时 frac 可把越界坐标折到对边，mix 结果在片元与折回点的
//   连线上，所以越界的轴按 [0,1] 整轴算。MASK 分支另采 v_TexCoord.xy（逐像素）。
Option<Displacement> Twirl(const EffectParams& p) {
    if (! p.HasVariant()) return None();
    auto center = p.Vec2("g_SpinCenter");
    auto size = p.Scalar("g_Size"), feather = p.Scalar("g_Feather");
    if (center.is_none() || size.is_none() || feather.is_none() || *size <= 0.0 ||
        *feather < 0.0)
        return None();
    double k = 1.0;
    if (p.Combo("ELLIPTICAL", 1) != 0) {
        auto ratio = p.Scalar("g_Ratio");
        if (ratio.is_none() || *ratio <= 0.0) return None();
        k = std::max(1.0, 1.0 / *ratio);
    }
    const double R  = *size + *feather + 1e-5;
    const double rx = R * k / (p.Input().aspect_lo * 0.99), ry = R * k;
    Rect box { (*center)[0] - rx, (*center)[1] - ry, (*center)[0] + rx, (*center)[1] + ry };
    if (p.Combo("REPEAT", 1) != 0) {
        if (box.u0 < 0.0 || box.u1 > 1.0) box.u0 = std::min(box.u0, 0.0), box.u1 = std::max(box.u1, 1.0);
        if (box.v0 < 0.0 || box.v1 > 1.0) box.v0 = std::min(box.v0, 0.0), box.v1 = std::max(box.v1, 1.0);
    }
    return Some(Displacement { 0.0, 0.0, Some(box) });
}

// auto_sway（工坊 3235948233，AA_VERSION 2）：片元依次绕节点中心 c_k（g_SpinCenter{k}，x 乘
// aspect）旋转 θ_k = motionRadian_k·g_Strength·w_k。
//   motionRadian_k = sin(·)[^exp] + sin(wind_k + π/2) + sin(g_GlobalWindOffset)
//                    − (sin(·)[^exp]·g_Inertia + sin(wind_{k+1} + π/2))  [NOISE 各加 ±noiseamount]
//   ⇒ |motionRadian_k| ≤ 1 + |sin(wind_k+π/2)| + |sin(offset)| + |inertia| + |sin(wind_{k+1}+π/2)|
//   w_k = weight·(1 − weight·u_Damping)·autoMask·mask，weight/autoMask/mask ∈ [0,1]
//   ⇒ |w_k| ≤ (0 ≤ d ≤ 0.5 ? 1 − d : 1 + |d|)
//   绕 c 转 θ 的位移 = 2r·sin(|θ|/2) ≤ r·|θ|，r 取 c 到纹理四角（aspect 空间）最远距离加上前面
//   各节点已产生的位移。权重的支撑区（autoMask 条带）是沿节点方向的无限长楔形，不设上界，
//   所以只能按整幅纹理取 r。
Option<Displacement> AutoSway(const EffectParams& p) {
    if (! p.HasVariant() || p.Combo("AA_VERSION", 2) != 2) return None();
    const int n = p.Combo("NODE_COUNT", 2);
    if (n < 2 || n > 11) return None();
    auto strength = p.Scalar("g_Strength"), inertia = p.Scalar("g_Inertia"),
         offset = p.Scalar("g_GlobalWindOffset"), damping = p.Scalar("u_Damping");
    if (strength.is_none() || inertia.is_none() || offset.is_none() || damping.is_none())
        return None();
    if (p.Combo("EXPONENT", 0) != 0) {
        auto e = p.Scalar("g_Exponent");
        if (e.is_none() || *e <= 0.0) return None();
    }
    double noise = 0.0;
    if (p.Combo("NOISE", 0) != 0) {
        auto amount = p.Scalar("g_NoiseAmount");
        if (amount.is_none()) return None();
        noise = 2.0 * std::abs(*amount);
    }
    const double d  = *damping;
    const double wd = (d >= 0.0 && d <= 0.5) ? 1.0 - d : 1.0 + std::abs(d);
    const double a  = p.Input().aspect_hi * 1.01;
    constexpr double kHalfPi = 1.5707963267948966;
    auto wind = [&](int k) -> Option<double> {
        if (k > 11) return Some(static_cast<double>(kHalfPi)); // 节点 11 的"下一风向"写死 M_PI_D2
        return p.Scalar(("g_WindDirection" + std::to_string(k)).c_str());
    };
    double total = 0.0;
    for (int k = 2; k <= n; ++k) {
        auto w0 = wind(k), w1 = wind(k + 1);
        auto c = p.Vec2(("g_SpinCenter" + std::to_string(k)).c_str());
        if (w0.is_none() || w1.is_none() || c.is_none()) return None();
        const double motion = 1.0 + std::abs(std::sin(*w0 + kHalfPi)) + std::abs(std::sin(*offset)) +
                              std::abs(*inertia) + std::abs(std::sin(*w1 + kHalfPi)) + noise;
        const double theta = std::abs(*strength) * motion * wd;
        const double cx = (*c)[0] * a, cy = (*c)[1];
        double       r  = 0.0;
        for (double x : { 0.0, a })
            for (double y : { 0.0, 1.0 }) r = std::max(r, std::hypot(x - cx, y - cy));
        total += (r + total) * theta;
    }
    return Some(Displacement { total / (p.Input().aspect_lo * 0.99), total, None() });
}

// shine_cast（芙莉莲工程自带）：沿 d = rotate((0,0.5), ·)·(1, ratio) 方向在 [p, p + d·g_Length]
// 上取样（EDGES 2..5 每个方向 |rotate(...)| = 0.5），ratio = g_Texture0Resolution.x/.y。
Option<Displacement> ShineCast(const EffectParams& p) {
    if (! p.HasVariant()) return None();
    auto length = p.Scalar("g_Length");
    if (length.is_none()) return None();
    const double l = std::abs(*length);
    return Some(Displacement { 0.5 * l, 0.5 * l * p.Input().aspect_hi * 1.01, None() });
}

// shine_gaussian：blur13a 最远 5.2063·d，blur7a 最远 3·d；d = g_Scale / g_Texture0Resolution.zw，
// VERTICAL 只沿 y，否则只沿 x。KERNEL 2（blur3a）未推导。
Option<Displacement> ShineGaussian(const EffectParams& p) {
    if (! p.HasVariant()) return None();
    const int    kernel = p.Combo("KERNEL", 0);
    const double reach  = kernel == 0 ? 5.2062900776825969 : kernel == 1 ? 3.0 : -1.0;
    if (reach < 0.0) return None();
    auto scale = p.Vec2("g_Scale");
    if (scale.is_none()) return None();
    if (p.Combo("VERTICAL", 0) != 0)
        return Some(Displacement { 0.0, reach * std::abs((*scale)[1]) / p.Input().min_h, None() });
    return Some(Displacement { reach * std::abs((*scale)[0]) / p.Input().min_w, 0.0, None() });
}

// shine_combine：g_Texture0（光线）在 v_TexCoord.zw、g_Texture1（previous）在 v_TexCoord.xy，
// 都等于 a_TexCoord。COPYBG 会读屏幕，按未知处理。
Option<Displacement> ShineCombine(const EffectParams& p) {
    if (! p.HasVariant() || p.Combo("COPYBG", 0) != 0) return None();
    return Some(Displacement {});
}

// waterflow：采样点 = v_TexCoord.xy + flowMask·g_FlowAmp·0.1·(cycles − 0.5)，flowMask =
//   (rg − 0.498)·2 ∈ [−0.996, 1.004]，cycles − 0.5 ∈ [−0.5, 0.5) ⇒ |Δ| ≤ 0.0502·|amp|（两轴）。
Option<Displacement> WaterFlow(const EffectParams& p) {
    if (! p.HasVariant()) return None();
    auto amp = p.Scalar("g_FlowAmp");
    if (amp.is_none()) return None();
    const double d = 1.004 * 0.1 * 0.5 * std::abs(*amp);
    return Some(Displacement { d, d, None() });
}

// 位移上界表：按着色器名 + 源码指纹（.vert 与 .frag 文件内容的 FNV-1a 64）查。指纹不符（工程自带
// 改过的同名着色器、WPE 资源版本变化）就当未知。表外一律视为无界；不设"未知效果保守常数"——
// 自定义着色器可以缩放/镜像/环绕采样，任何常数都证明不了。rt_slots 是除 g_Texture0 外还允许读
// 本层 RT（合成 RT 或本效果 FBO）的槽位掩码，这些槽位一律逐像素采样。
struct Rule {
    std::string_view shader;
    std::uint64_t    source_fnv;
    BoundFn          bound;
    std::uint32_t    rt_slots { 0 };
};
constexpr Rule kDisplacementTable[] = {
    { "effects/waterwaves", 1665525461160322644ull, WaterWaves },
    { "effects/pulse", 4691102007919585790ull, Pointwise },
    { "genericimage3", 8045191383678370494ull, GenericImage },
    { "genericimage2", 4186575975355154278ull, GenericImage },
    { "genericimage4", 13446820757646335182ull, GenericImage },
    { "effects/twirl", 15515623398863031119ull, Twirl },
    { "workshop/3235948233/effects/auto_sway", 3454525798839054591ull, AutoSway },
    { "effects/shine_downsample2", 17177365509716486298ull, Pointwise },
    { "effects/shine_cast", 17347959800840427808ull, ShineCast },
    { "effects/shine_gaussian", 94899090392042740ull, ShineGaussian },
    { "effects/shine_combine", 9097062468333062290ull, ShineCombine, 1u << 1 },
    { "effects/waterflow", 8434346930136608297ull, WaterFlow },
    { "effects/shimmer", 1518327559064762644ull, Pointwise },
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
                // GetAttrOffsetMap 的 offset 是字节，stride（OneSize）是 float 个数。
                const float* p = vtx + pos->second.offset.to_primitive() / sizeof(float);
                const float* t = vtx + uv->second.offset.to_primitive() / sizeof(float);
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

// ---- 蒙皮最终 pass：每帧 CPU 蒙皮 → 屏幕内三角形 → UV 包围盒 ----
//
// 顶点 = Σ_k w_k·(B_k·p)，B_k 是 Puppet::lastFrame()（本帧 uniform 更新时 genFrame 写入、上传给
// g_Bones 的同一组 float 矩阵；Program::update 先更新全部 uniform 再 record，所以录制时已是本帧值）。
// 片元 UV 是所在三角形顶点 UV 的重心组合，只要三角形与屏幕（NDC 放大包络 + 余量）有交，就把
// 三角形∩屏幕的多边形按三角形自身的仿射关系映射回 UV；与屏幕无交的三角形没有片元。
// 余量：CPU 用 double、GPU 用 float，蒙皮误差 ≲ 16 项累加 × 2^-24 × |坐标|（4K 画布约 0.03 px），
// 光栅化顶点吸附 1/256 px，片元中心规则只在像素中心落在边上时起作用。取 NDC 2e-3（512 宽约
// 0.5 px，1920 宽约 1.9 px），远大于上述误差之和；UV 侧再外扩合成 RT 的 0.5 像素盖住属性插值误差。
constexpr double kSkinMarginNdc = 2e-3;
constexpr double kSkinMarginUvPx = 0.5;

struct SkinnedMesh {
    std::vector<float>         pos;     // xyz
    std::vector<float>         uv;      // uv
    std::vector<std::uint32_t> bone;    // 4 个索引
    std::vector<float>         weight;  // 4 个权重
    std::vector<std::uint32_t> tris;    // 三角形顶点下标
};

Option<SkinnedMesh> ExtractSkinnedMesh(const SceneMesh& mesh) {
    if (mesh.Primitive() != MeshPrimitive::TRIANGLE) return None();
    if (mesh.Submeshes().size() != 1) return None();
    const auto& sm = mesh.Submeshes()[0];
    if (sm.vertex_arrays.size() != 1 || sm.index_arrays.size() != 1) return None();
    const auto& array   = sm.vertex_arrays[0];
    auto        offsets = array.GetAttrOffsetMap();
    auto pos = offsets.find(std::string(as_string_view(WE_IN_POSITION)));
    auto uv  = offsets.find(std::string(as_string_view(WE_IN_TEXCOORD)));
    auto bi  = offsets.find(std::string(as_string_view(WE_IN_BLENDINDICES)));
    auto bw  = offsets.find(std::string(as_string_view(WE_IN_BLENDWEIGHTS)));
    if (pos == offsets.end() || uv == offsets.end() || bi == offsets.end() || bw == offsets.end())
        return None();
    const float* data   = array.Data();
    const usize  stride = array.OneSize();
    if (data == nullptr) return None();
    SkinnedMesh out;
    const std::size_t count = array.VertexCount().to_primitive();
    for (std::size_t i = 0; i < count; ++i) {
        const float* vtx = data + (usize(i) * stride).to_primitive();
        // GetAttrOffsetMap 的 offset 是字节，stride（OneSize）是 float 个数。
        const float* p = vtx + pos->second.offset.to_primitive() / sizeof(float);
        const float* t = vtx + uv->second.offset.to_primitive() / sizeof(float);
        const float* w = vtx + bw->second.offset.to_primitive() / sizeof(float);
        std::uint32_t idx[4];
        std::memcpy(idx, vtx + bi->second.offset.to_primitive() / sizeof(float), sizeof(idx));
        out.pos.insert(out.pos.end(), { p[0], p[1], p[2] });
        out.uv.insert(out.uv.end(), { t[0], t[1] });
        out.bone.insert(out.bone.end(), idx, idx + 4);
        out.weight.insert(out.weight.end(), w, w + 4);
    }
    const auto& ia = sm.index_arrays[0];
    const std::size_t n = ia.DataCount().to_primitive();
    if (ia.Data() == nullptr || n % 3 != 0 || n == 0) return None();
    // 全部索引都当三角形（draw_ranges 只画其中一部分，多算只会让区域变大）。
    out.tris.assign(ia.Data(), ia.Data() + n);
    for (auto v : out.tris)
        if (v >= count) return None();
    return Some(rstd::move(out));
}

// lim = NDC 矩形 {x0, y0, x1, y1}（已含包络与余量）。返回 None 即本帧无法给出区域（整幅绘制）。
Option<Rect> SkinnedFinalRegion(const SkinnedMesh& m, const Vec<Eigen::Affine3f>& bones,
                                const Eigen::Matrix4d& mvp, const double lim[4]) {
    const std::size_t count = m.pos.size() / 3;
    std::vector<double> ndc(count * 2);
    for (std::size_t i = 0; i < count; ++i) {
        Eigen::Vector3d s(0.0, 0.0, 0.0);
        const Eigen::Vector3d p(m.pos[i * 3], m.pos[i * 3 + 1], m.pos[i * 3 + 2]);
        for (int k = 0; k < 4; ++k) {
            const double w = m.weight[i * 4 + k];
            if (w == 0.0) continue; // 0·有限值 = 0；索引可能越界的零权重槽不读
            const std::uint32_t b = m.bone[i * 4 + k];
            if (b >= bones.len().to_primitive()) return None();
            const auto& bm = bones[usize(b)].matrix();
            Eigen::Matrix4d bd = bm.cast<double>();
            s += w * (bd.topLeftCorner<3, 3>() * p + bd.topRightCorner<3, 1>());
        }
        const Eigen::Vector4d clip = mvp * Eigen::Vector4d(s.x(), s.y(), s.z(), 1.0);
        if (std::abs(clip.w() - 1.0) > 1e-9 || ! std::isfinite(clip.x()) || ! std::isfinite(clip.y()))
            return None();
        ndc[i * 2] = clip.x(), ndc[i * 2 + 1] = clip.y();
    }
    Region r;
    std::vector<std::array<double, 2>> poly, next;
    for (std::size_t t = 0; t + 2 < m.tris.size(); t += 3) {
        const std::uint32_t id[3] = { m.tris[t], m.tris[t + 1], m.tris[t + 2] };
        double bx0 = 1e300, by0 = 1e300, bx1 = -1e300, by1 = -1e300;
        Rect   tuv { 1e300, 1e300, -1e300, -1e300 };
        for (auto v : id) {
            bx0 = std::min(bx0, ndc[v * 2]), bx1 = std::max(bx1, ndc[v * 2]);
            by0 = std::min(by0, ndc[v * 2 + 1]), by1 = std::max(by1, ndc[v * 2 + 1]);
            tuv = tuv.Union({ m.uv[v * 2], m.uv[v * 2 + 1], m.uv[v * 2], m.uv[v * 2 + 1] });
        }
        if (bx1 < lim[0] || bx0 > lim[2] || by1 < lim[1] || by0 > lim[3]) continue;
        if (bx0 >= lim[0] && bx1 <= lim[2] && by0 >= lim[1] && by1 <= lim[3]) {
            r.Add(tuv);
            continue;
        }
        const double ax = ndc[id[0] * 2], ay = ndc[id[0] * 2 + 1];
        const double ex = ndc[id[1] * 2] - ax, ey = ndc[id[1] * 2 + 1] - ay;
        const double fx = ndc[id[2] * 2] - ax, fy = ndc[id[2] * 2 + 1] - ay;
        const double det = ex * fy - ey * fx;
        const double scale = std::max({ std::abs(ex), std::abs(ey), std::abs(fx), std::abs(fy), 1e-300 });
        if (! (std::abs(det) > 1e-9 * scale * scale)) {
            r.Add(tuv); // 近退化三角形：取整个 UV 三角形
            continue;
        }
        poly = { { ax, ay }, { ndc[id[1] * 2], ndc[id[1] * 2 + 1] }, { ndc[id[2] * 2], ndc[id[2] * 2 + 1] } };
        for (int edge = 0; edge < 4 && ! poly.empty(); ++edge) {
            const int  axis = edge % 2;
            const bool low  = edge < 2;
            auto inside = [&](const auto& q) { return low ? q[axis] >= lim[edge] : q[axis] <= lim[edge]; };
            next.clear();
            for (std::size_t i = 0; i < poly.size(); ++i) {
                const auto& a = poly[i];
                const auto& b = poly[(i + 1) % poly.size()];
                if (inside(a)) next.push_back(a);
                if (inside(a) != inside(b)) {
                    const double s = (lim[edge] - a[axis]) / (b[axis] - a[axis]);
                    next.push_back({ a[0] + s * (b[0] - a[0]), a[1] + s * (b[1] - a[1]) });
                }
            }
            std::swap(poly, next);
        }
        if (poly.empty()) continue;
        const double tu = m.uv[id[0] * 2], tv = m.uv[id[0] * 2 + 1];
        const double du1 = m.uv[id[1] * 2] - tu, dv1 = m.uv[id[1] * 2 + 1] - tv;
        const double du2 = m.uv[id[2] * 2] - tu, dv2 = m.uv[id[2] * 2 + 1] - tv;
        Rect c { 1e300, 1e300, -1e300, -1e300 };
        for (const auto& q : poly) {
            const double qx = q[0] - ax, qy = q[1] - ay;
            const double l1 = (qx * fy - qy * fx) / det, l2 = (ex * qy - ey * qx) / det;
            const double u = tu + l1 * du1 + l2 * du2, v = tv + l1 * dv1 + l2 * dv2;
            c = c.Union({ u, v, u, v });
        }
        // 真实 UV 在三角形 UV 凸包内，截到三角形 UV 包围盒上只去掉数值误差。
        c = { std::max(c.u0, tuv.u0), std::max(c.v0, tuv.v0), std::min(c.u1, tuv.u1), std::min(c.v1, tuv.v1) };
        if (c.u1 < c.u0 || c.v1 < c.v0) c = tuv;
        r.Add(c);
    }
    if (r.kind == Region::Kind::Full) return None();
    if (r.kind == Region::Kind::Nothing) return Some(Rect { 0.0, 0.0, 0.0, 0.0 });
    return Some(r.r);
}

// ---- 逐 pass 反推 ----
struct ReadRule {
    std::string          rt;
    Option<Displacement> disp; // None = 无界
};
struct PassRule {
    std::string           target;   // 写入的 RT；最终 pass 为空（屏幕）
    double                margin_u; // 本 pass 输出 2 像素（UV）
    double                margin_v;
    std::vector<ReadRule> reads;
};
struct FrameRegions {
    std::vector<Region> out; // 与 passes 对齐；最终 pass 不用
    Region              source;
};

void Sample(Region& dst, const Rect& r, double mu, double mv, const Option<Displacement>& d) {
    if (d.is_none()) {
        dst.AddFull();
        return;
    }
    dst.Add(r.Grow(mu + d->du, mv + d->dv));
    if (d->extra.is_some()) dst.Add(*d->extra);
}

// 从最终 pass 往前做一遍"活跃区域"反推：pass 写 T 时，T 的旧版本不再被后面读，need[T] 清空，
// 再把本 pass 各槽位的采样区并进被读 RT 的 need。某步无界只让它上游整幅，下游仍可裁。
FrameRegions Propagate(const std::vector<PassRule>& passes, const std::string& composite,
                       const Rect& final_uv) {
    std::unordered_map<std::string, Region> need;
    FrameRegions                            fr;
    fr.out.resize(passes.size());
    for (const auto& rd : passes.back().reads) Sample(need[rd.rt], final_uv, 0.0, 0.0, rd.disp);
    fr.out.back().AddFull();
    for (std::size_t i = passes.size() - 1; i-- > 0;) {
        const auto& p = passes[i];
        Region      o = need[p.target];
        need[p.target] = Region {};
        fr.out[i]      = o;
        if (o.kind == Region::Kind::Nothing) continue;
        for (const auto& rd : p.reads) {
            if (o.kind == Region::Kind::Full) need[rd.rt].AddFull();
            else Sample(need[rd.rt], o.r, p.margin_u, p.margin_v, rd.disp);
        }
    }
    fr.source = need[composite];
    return fr;
}

// 一层的共享状态：guard + 最终区域（静态，或按骨骼矩阵逐帧重算并缓存）+ 各 pass 区域。
struct LayerState {
    std::function<bool()>  guard;
    std::vector<PassRule>  passes;
    std::string            composite;
    double                 uv_margin_u { 0.0 }, uv_margin_v { 0.0 };
    // 静态
    Option<FrameRegions>   fixed;
    // 蒙皮
    Option<SkinnedMesh>    skinned;
    const PuppetLayer*     puppet { nullptr };
    Eigen::Matrix4d        mvp;
    double                 lim[4] {};
    std::vector<float>     cache_key;
    bool                   cache_ok { false };
    Option<FrameRegions>   cache;
    // 统计（每层每帧一次，第一个带区域的 pass 录制时计）：各 pass 保留面积比例累计，每 60 帧打一行日志。
    std::uint64_t          frames { 0 }, full_frames { 0 };
    std::vector<double>    kept_sum;
    double                 src_kept_sum { 0.0 };
    Rect                   last_final {};

    const FrameRegions* Get() {
        if (! guard()) return nullptr;
        if (fixed.is_some()) return &*fixed;
        const auto& bones = puppet->puppet().lastFrame();
        const std::size_t nb = bones.len().to_primitive();
        if (cache_ok && cache_key.size() == nb * 16 &&
            std::memcmp(cache_key.data(), bones[usize()].data(), nb * 16 * sizeof(float)) == 0)
            return cache.is_some() ? &*cache : nullptr;
        cache_key.resize(nb * 16);
        if (nb > 0) std::memcpy(cache_key.data(), bones[usize()].data(), nb * 16 * sizeof(float));
        cache_ok = true;
        auto uv  = SkinnedFinalRegion(*skinned, bones, mvp, lim);
        if (uv.is_none()) {
            cache = None();
            return nullptr;
        }
        last_final = uv->Grow(uv_margin_u, uv_margin_v);
        cache = Some(Propagate(passes, composite, last_final));
        return &*cache;
    }

    static double Frac(const Region& r) {
        if (r.kind == Region::Kind::Full) return 1.0;
        if (r.kind == Region::Kind::Nothing) return 0.0;
        const double w = std::clamp(r.r.u1, 0.0, 1.0) - std::clamp(r.r.u0, 0.0, 1.0);
        const double h = std::clamp(r.r.v1, 0.0, 1.0) - std::clamp(r.r.v0, 0.0, 1.0);
        return std::max(0.0, w) * std::max(0.0, h);
    }
    void Account(const FrameRegions* fr, bool has_source) {
        ++frames;
        kept_sum.resize(passes.size() - 1, 0.0);
        if (fr == nullptr) ++full_frames;
        for (std::size_t i = 0; i + 1 < passes.size(); ++i) kept_sum[i] += fr ? Frac(fr->out[i]) : 1.0;
        src_kept_sum += fr && has_source ? Frac(fr->source) : 1.0;
        if (frames % 60 != 0) return;
        std::string line = rstd::cppstd::to_string(rstd::format("src={}", src_kept_sum / frames).as_str());
        for (std::size_t i = 0; i < kept_sum.size(); ++i)
            line += rstd::cppstd::to_string(rstd::format(" p{}={}", i, kept_sum[i] / frames).as_str());
        rstd_info("region clip stats {}: frames={} full_frames={} last_final_uv=[{},{},{},{}] avg_kept: {}", composite,
                  frames, full_frames, last_final.u0, last_final.v0, last_final.u1, last_final.v1, line);
    }
};

using RegionFn = std::shared_ptr<const std::function<Option<std::array<double, 4>>()>>;

RegionFn MakeRegionFn(std::shared_ptr<LayerState> state, std::size_t index, bool source,
                      bool accounts, bool has_source) {
    return std::make_shared<const std::function<Option<std::array<double, 4>>()>>(
        [state, index, source, accounts, has_source]() -> Option<std::array<double, 4>> {
            const FrameRegions* fr = state->Get();
            if (accounts) state->Account(fr, has_source);
            if (fr == nullptr) return None();
            const Region& r = source ? fr->source : fr->out[index];
            if (r.kind == Region::Kind::Full) return None();
            if (r.kind == Region::Kind::Nothing)
                return Some(std::array<double, 4> { 0.0, 0.0, 0.0, 0.0 });
            return Some(std::array<double, 4> { r.r.u0, r.r.v0, r.r.u1, r.r.v1 });
        });
}

struct LayerPlan {
    RegionFn                                               source_region;
    std::unordered_map<const SceneImageEffectNode*, RegionFn> effect_regions;
};

bool Near(const Eigen::Matrix4d& a, const Eigen::Matrix4d& b) { return a == b; }

Option<InputExtent> ExtentOf(Scene& scene, const std::string& name) {
    auto rt = scene.RenderTarget(as_str(name).unwrap());
    if (rt.is_none()) return None();
    const auto& t = **rt;
    if (t.has_mipmap || t.sample_count > 1 || t.bind.enable || t.preserve_on_write) return None();
    const double w = rstd::as_cast<double>(t.width), h = rstd::as_cast<double>(t.height);
    if (! (w > 0.0 && h > 0.0)) return None();
    const double pw = t.physical_width > i32() ? rstd::as_cast<double>(t.physical_width) : w;
    const double ph = t.physical_height > i32() ? rstd::as_cast<double>(t.physical_height) : h;
    InputExtent e;
    e.min_w     = std::max(1.0, std::min(w, pw));
    e.min_h     = std::max(1.0, std::min(h, ph));
    e.aspect_lo = std::min(w / h, pw / ph);
    e.aspect_hi = std::max(w / h, pw / ph);
    return Some(e);
}

// 返回 None 即整层不裁；reason 写日志。
Option<LayerPlan> PlanLayer(SceneNodeLayer& layer, Scene& scene, ExtraInfo& extra,
                            std::string& reason) {
    auto fail = [&](std::string why) {
        reason = rstd::move(why);
        return None<LayerPlan>();
    };
    if (extra.selection != nullptr &&
        (extra.selection->enabled || extra.selection->transparent_background))
        return fail("layer selection");
    if (layer.FinalResolveEffect() || layer.PublishedEffect() || layer.VisibleResolveEffect())
        return fail("resolve/published effect");
    if (! layer.PrefillNodes().empty()) return fail("prefill nodes");
    if (extra.capture) return fail("offline capture target");
    auto state_ext = scene.ExtensionMut<Arc<UniformSceneState>>();
    if (state_ext.is_none()) return fail("no uniform state");
    // 扩展由场景持有，生命周期覆盖渲染图；guard 里存裸指针以便 std::function 可拷贝。
    UniformSceneState* uniform_state = (**state_ext).as_ptr();

    const std::string composite(layer.CompositeTarget());
    auto              comp_extent = ExtentOf(scene, composite);
    if (comp_extent.is_none()) return fail("composite rt kind");

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

    // 最终 pass：静态正交投影、非透视/反射、不在世界空间；蒙皮时走逐帧 CPU 包围盒。
    SceneNode* final_scene_node = final_node->sceneNode.as_ptr();
    for (auto* p = final_scene_node; p != nullptr; p = p->Parent())
        if (p->Perspective() || (p->Reflected() && scene.PlanarReflectionEnabled()))
            return fail("perspective/reflected final");
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
    if (mesh->Material()->customShader.variant.is_none()) return fail("final variant");
    bool skinned = false;
    {
        const auto& combos = mesh->Material()->customShader.variant->resolved_combos;
        if (auto it = combos.find("MORPHING"); it != combos.end() && it->second != "0")
            return fail("morphing final mesh");
        if (auto it = combos.find("SKINNING"); it != combos.end() && it->second != "0") skinned = true;
    }
    const PuppetLayer* puppet = nullptr;
    if (skinned) {
        auto layers = scene.ExtensionMut<PuppetNodeLayers>();
        if (layers.is_none()) return fail("no puppet registry");
        auto found = (**layers).by_node.get(static_cast<const SceneNode*>(final_scene_node));
        if (found.is_none()) return fail("skinned final without puppet layer");
        puppet = (**found).as_ptr();
        // 同一 Puppet 被多个木偶层共用时 lastFrame 可能是别的层算的，不裁。
        std::size_t sharing = 0;
        (**layers).by_node.iter().for_each([&](auto entry) {
            auto [node_ref, layer_ref] = entry;
            if (&(*layer_ref)->puppet() == &puppet->puppet() && (*layer_ref).as_ptr() != puppet) ++sharing;
        });
        if (sharing > 0) return fail("puppet shared by several layers");
    }

    // 逐 pass 规则。
    std::vector<ParamSnapshot> used_params;
    std::vector<PassRule>      passes;
    std::string                chain_log;
    for (std::size_t i = 0; i < nodes.size(); ++i) {
        auto*      n        = nodes[i];
        const bool is_final = i + 1 == nodes.size();
        SceneNode* node     = n->sceneNode.as_ptr();
        if (node->Mesh() == nullptr || node->Mesh()->Submeshes().size() != 1 ||
            node->Mesh()->Material() == nullptr)
            return fail("effect mesh");
        auto& material = *node->Mesh()->Material();
        if (! material.customShader.shader) return fail("no shader");
        PassRule pr;
        InputExtent out_extent = *comp_extent;
        if (! is_final) {
            pr.target = std::string(ResolveEffectTarget(layer, layer.ResolvedTarget(*n)));
            auto ext  = ExtentOf(scene, pr.target);
            if (ext.is_none()) return fail("effect target kind " + pr.target);
            out_extent = *ext;
            // 读回旧内容（混合/保留）的 pass 依赖物理 RT 的历史内容（可能来自上一帧被裁的 pass），整层不裁。
            if (LoadsPreviousAttachment(material.blenmode)) return fail("effect pass loads previous attachment");
        }
        pr.margin_u = 2.0 / out_extent.min_w;
        pr.margin_v = 2.0 / out_extent.min_h;
        if (material.textures.empty()) return fail("no g_Texture0");
        const std::string& in0 = material.textures[0];
        auto in_extent = ExtentOf(scene, in0);
        std::uint64_t fnv  = 0;
        const Rule*   rule = nullptr;
        {
            auto hash = ShaderSourceFnv(scene, material.customShader.shader->name);
            fnv       = hash.is_some() ? *hash : 0;
            for (const auto& r : kDisplacementTable)
                if (r.shader == material.customShader.shader->name && hash.is_some() && *hash == r.source_fnv)
                    rule = &r;
        }
        Option<Displacement> disp;
        if (rule != nullptr && in_extent.is_some()) {
            EffectParams params(material, *in_extent, used_params, is_final && skinned);
            disp = rule->bound(params);
        }
        chain_log += rstd::cppstd::to_string(rstd::format(" [{} {} fnv={} {}]", i,
                                                          material.customShader.shader->name, fnv,
                                                          rule == nullptr ? "unknown" : disp.is_none() ? "unbounded" : "ok").as_str());
        if (is_final && disp.is_none()) return fail("final pass not bounded:" + chain_log);
        for (std::size_t s = 0; s < material.textures.size(); ++s) {
            const auto& key = material.textures[s];
            if (key.empty()) continue;
            const bool is_rt = key == composite || IsSpecTex(as_str(key).unwrap()) ||
                               scene.RenderTarget(as_str(key).unwrap()).is_some();
            if (! is_rt) continue;
            // 本层以外的 RT（屏幕、别层结果）不受裁剪影响，但读屏幕的 pass 结果依赖本层之外，保守起见不裁。
            if (key != composite && ExtentOf(scene, key).is_none())
                return fail("reads foreign render target " + key);
            if (s == 0) pr.reads.push_back({ key, disp });
            else if (rule != nullptr && disp.is_some() && ((rule->rt_slots >> s) & 1u))
                pr.reads.push_back({ key, Some(Displacement {}) });
            else pr.reads.push_back({ key, None() });
        }
        passes.push_back(rstd::move(pr));
    }

    final_scene_node->UpdateTrans();
    const Eigen::Matrix4d model = final_scene_node->ModelTrans() *
                                  final_scene_node->GeometryTransform() * mesh->GeometryTransform();
    const Eigen::Matrix4d view_projection = camera->GetViewProjectionMatrix();
    std::vector<u64> mesh_generations;
    for (const auto& sm : mesh->Submeshes()) {
        for (const auto& va : sm.vertex_arrays) mesh_generations.push_back(va.DataGeneration());
        for (const auto& ia : sm.index_arrays) mesh_generations.push_back(ia.DataGeneration());
    }

    // 视差包络：|shift| ≤ (|节点 − 相机| + 0.5·ortho·|mouse_influence|)·|depth|·amount（指针在 [0,1]）；
    // 运行时直接用同一函数算本帧真实偏移核对，不依赖这里的推导。
    const auto ortho      = uniform_state->Ortho();
    const auto parallax   = uniform_state->CameraParallax();
    double     parallax_x = 0.0, parallax_y = 0.0;
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

    auto state        = std::make_shared<LayerState>();
    state->passes     = passes;
    state->composite  = composite;
    std::string final_desc;
    if (skinned) {
        state->skinned = ExtractSkinnedMesh(*mesh);
        if (state->skinned.is_none()) return fail("skinned final mesh layout");
        state->puppet      = puppet;
        state->mvp         = view_projection * model;
        state->lim[0]      = -1.0 - grow_x - kSkinMarginNdc;
        state->lim[1]      = -1.0 - grow_y - kSkinMarginNdc;
        state->lim[2]      = 1.0 + grow_x + kSkinMarginNdc;
        state->lim[3]      = 1.0 + grow_y + kSkinMarginNdc;
        state->uv_margin_u = kSkinMarginUvPx / comp_extent->min_w;
        state->uv_margin_v = kSkinMarginUvPx / comp_extent->min_h;
        final_desc = rstd::cppstd::to_string(rstd::format("skinned verts={} tris={} bones={}",
                                                          state->skinned->pos.size() / 3,
                                                          state->skinned->tris.size() / 3,
                                                          puppet->puppet().lastFrame().len()).as_str());
    } else {
        auto final_region = FinalSampledRegion(*mesh, view_projection * model, grow_x, grow_y);
        if (final_region.is_none()) return fail("final mesh region");
        state->fixed = Some(Propagate(passes, composite, *final_region));
        final_desc   = rstd::cppstd::to_string(rstd::format("static final_uv=[{},{},{},{}]",
                                                            final_region->u0, final_region->v0,
                                                            final_region->u1, final_region->v1).as_str());
    }

    // 运行时 guard：本帧最终 pass 的模型/相机/网格/参数与规划时一致，且真实视差偏移在包络内。
    const float parallax_amount = parallax.amount, parallax_influence = parallax.mouse_influence;
    const bool  parallax_enable = parallax.enable;
    state->guard = [=, used = rstd::move(used_params)]() -> bool {
        final_scene_node->UpdateTrans();
        const Eigen::Matrix4d now = final_scene_node->ModelTrans() *
                                    final_scene_node->GeometryTransform() * mesh->GeometryTransform();
        if (! Near(now, model)) return false;
        if (! Near(camera->GetViewProjectionMatrix(), view_projection)) return false;
        std::size_t g = 0;
        for (const auto& sm : mesh->Submeshes()) {
            for (const auto& va : sm.vertex_arrays)
                if (g >= mesh_generations.size() || va.DataGeneration() != mesh_generations[g++])
                    return false;
            for (const auto& ia : sm.index_arrays)
                if (g >= mesh_generations.size() || ia.DataGeneration() != mesh_generations[g++])
                    return false;
        }
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
    };

    LayerPlan plan;
    const bool has_source = layer.RequiresSourceDraw();
    if (has_source) plan.source_region = MakeRegionFn(state, 0, true, true, true);
    for (std::size_t i = 0; i + 1 < nodes.size(); ++i)
        plan.effect_regions[nodes[i]] = MakeRegionFn(state, i, false, ! has_source && i == 0, has_source);

    // 规划日志：静态层直接给出各 pass 保留比例；蒙皮层按首帧（规划时的骨骼）估一次。
    const FrameRegions* fr = state->Get();
    std::string         kept;
    auto frac = [](const Region& r) {
        if (r.kind == Region::Kind::Full) return 1.0;
        if (r.kind == Region::Kind::Nothing) return 0.0;
        const double w = std::clamp(r.r.u1, 0.0, 1.0) - std::clamp(r.r.u0, 0.0, 1.0);
        const double h = std::clamp(r.r.v1, 0.0, 1.0) - std::clamp(r.r.v0, 0.0, 1.0);
        return std::max(0.0, w) * std::max(0.0, h);
    };
    if (fr != nullptr) {
        if (skinned)
            kept = rstd::cppstd::to_string(rstd::format(" final_uv=[{},{},{},{}]", state->last_final.u0,
                                                        state->last_final.v0, state->last_final.u1,
                                                        state->last_final.v1).as_str());
        kept += rstd::cppstd::to_string(rstd::format(" src={}", frac(fr->source)).as_str());
        for (std::size_t i = 0; i + 1 < nodes.size(); ++i)
            kept += rstd::cppstd::to_string(rstd::format(" p{}={}", i, frac(fr->out[i])).as_str());
    } else {
        kept = " (guard/region unavailable at plan time)";
    }
    rstd_info("region clip {}: {} envelope_world=({},{}) kept_fraction:{} chain:{}", composite,
              final_desc, ex, ey, kept, chain_log);
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

    std::shared_ptr<const std::function<Option<std::array<double, 4>>()>> source_region;
    if (imgeff != nullptr && ! defer_effect && render_view == SceneRenderViewKind::Primary &&
        imgeff->HasRenderEffects()) {
        std::string reason;
        if (auto plan = region_clip::PlanLayer(*imgeff, scene, extra, reason); plan.is_some()) {
            source_region = plan->source_region;
            for (auto& [n, r] : plan->effect_regions) extra.region_clip[n] = r;
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
             source_region = submesh.output_override.empty() ? source_region : nullptr,
             &scene,
             &extra](rg::RenderGraphBuilder& builder, vulkan::CustomShaderPass::Desc& pdesc) {
                if (effect_node != nullptr) {
                    if (auto it = extra.region_clip.find(effect_node); it != extra.region_clip.end())
                        pdesc.region = it->second;
                } else if (source_region) {
                    pdesc.region = source_region;
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
    extra.capture = capture_target != nullptr;

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
