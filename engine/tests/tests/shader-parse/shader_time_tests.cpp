// 着色器时间签名（Pkg/Parse/Shader/ShaderTime.cppm）：用 WPE 自带特效源码编成实际运行的 SPIR-V，
// 断言解析出的周期与方程库（src/Baker.Core/Analysis/ShaderClock/clock-rules.json、ShaderPeriodAnalysis.cs）的结论一致；
// 另给方程库认不出的 clouds、godrays_downsample2、shine_cast 的签名。找不到 WPE assets（WPE_ASSETS_DIR）时跳过。

#include <gtest/gtest.h>

#include <cmath>
#include <cstdlib>
#include <filesystem>
#include <fstream>
#include <map>
#include <numbers>
#include <sstream>

import rstd.cppstd;
import rstd;
import wescene.fs;
import wescene.pkg.parse;
import wescene.scene;
import wescene.types;

using namespace rstd::literals;
namespace st = owe::shader_time;

namespace
{

std::filesystem::path Assets() {
    const char* env = std::getenv("WPE_ASSETS_DIR");
    return env ? env : "D:/Apps/Steam/steamapps/common/wallpaper_engine/assets";
}

std::string ReadText(const std::filesystem::path& path) {
    std::ifstream     in(path, std::ios::binary);
    std::stringstream s;
    s << in.rdbuf();
    return s.str();
}

struct Case {
    std::string                               effect; // assets/effects/<effect>/shaders/effects/<shader>.{vert,frag}
    std::string                               shader;
    std::map<std::string, std::string>        combos;
    std::map<std::string, std::vector<float>> values; // 材质常量，其余取着色器默认值
    std::string                               vert, frag; // 非空时用这段源码，不读 assets
};

st::Signature Analyze(const Case& c) {
    const auto dir = Assets() / "effects" / c.effect / "shaders" / "effects";
    if (c.frag.empty() && ! std::filesystem::exists(dir / (c.shader + ".frag"))) {
        ADD_FAILURE() << "missing " << (dir / (c.shader + ".frag")).string();
        return {};
    }
    owe::SceneShaderVariantDesc desc;
    desc.scene_id    = "effects/" + c.shader;
    desc.shader_name = "effects/" + c.shader;
    for (const auto& [k, v] : c.combos) desc.input_combos[k] = v;
    desc.stages.push_back(owe::SceneShaderVariantStage { .stage      = owe::ShaderType::VERTEX,
                                                         .source_key = "/assets/shaders/effects/" + c.shader + ".vert",
                                                         .source     = c.frag.empty() ? ReadText(dir / (c.shader + ".vert")) : c.vert });
    desc.stages.push_back(owe::SceneShaderVariantStage { .stage      = owe::ShaderType::FRAGMENT,
                                                         .source_key = "/assets/shaders/effects/" + c.shader + ".frag",
                                                         .source     = c.frag.empty() ? ReadText(dir / (c.shader + ".frag")) : c.frag });
    owe::fs::VFS vfs;
    auto         mount = owe::fs::make_physical_fs(owe::fs::ToPath(Assets().string()));
    EXPECT_TRUE(mount.is_ok());
    if (mount.is_err()) return {};
    (void)vfs.mount("/assets"_str, rstd::move(mount).unwrap_unchecked(), "assets"_str);
    auto compiled = owe::ShaderParser::CompileSceneShaderVariant(desc, vfs);
    EXPECT_TRUE(compiled.ok && compiled.shader) << c.shader;
    if (! compiled.ok || ! compiled.shader) return {};
    st::Inputs in;
    in.uniform = [&](std::string_view name) -> st::UniformValue {
        if (auto it = c.values.find(std::string(name)); it != c.values.end())
            return { st::UniformValue::Kind::Constant, it->second };
        const auto& defaults = compiled.shader->default_uniforms;
        if (auto it = defaults.find(name); it != defaults.end())
            return { st::UniformValue::Kind::Constant,
                     std::vector<float>(it->second.data(), it->second.data() + it->second.size().to_primitive()) };
        return {};
    };
    in.wrap = [](std::string_view) { return std::array { st::Wrap::Repeat, st::Wrap::Repeat }; };
    auto sig = st::Analyze(compiled.shader->codes, in);
    std::cout << c.shader << ": " << st::ToJson(sig) << '\n';
    return sig;
}

void ExpectPeriod(const st::Signature& sig, double seconds) {
    EXPECT_EQ(sig.kind, "periodic") << st::ToJson(sig);
    ASSERT_EQ(sig.periods.size(), 1u) << st::ToJson(sig);
    EXPECT_NEAR(sig.periods[0].seconds, seconds, seconds * 1e-6) << st::ToJson(sig);
    EXPECT_NE(sig.periods[0].num, 0) << st::ToJson(sig);
}

constexpr double kTau = 2 * std::numbers::pi;

class ShaderTime : public ::testing::Test {
protected:
    void SetUp() override {
        if (! std::filesystem::exists(Assets() / "shaders" / "common.h")) GTEST_SKIP() << "no WPE assets";
    }
};

} // namespace

// 方程库 shake：2π/|speed|
TEST_F(ShaderTime, ShakeMatchesEquation) {
    ExpectPeriod(Analyze({ "shake", "shake", { { "NOISE", "0" } }, { { "g_Speed", { 2.5f } } } }), kTau / 2.5);
}

// 方程库 shake 的 NOISE 分支判不周期（多个时间系数）：签名给不出上限内的周期
TEST_F(ShaderTime, ShakeNoiseHasNoPeriodWithinCeiling) {
    auto sig = Analyze({ "shake", "shake", { { "NOISE", "1" } }, { { "g_Speed", { 1.0f } } } });
    for (const auto& p : sig.periods) EXPECT_TRUE(sig.periods.size() > 1 || p.seconds > 3600) << st::ToJson(sig);
    EXPECT_FALSE(sig.periods.empty() && sig.kind != "aperiodic") << st::ToJson(sig);
}

// 方程库 spin：2π/|speed|
TEST_F(ShaderTime, SpinMatchesEquation) {
    ExpectPeriod(Analyze({ "spin", "spin", { { "NOISE", "0" } }, { { "g_Speed", { 0.5f } } } }), kTau / 0.5);
}

// 方程库 pulse（noiseamount 0）：2π/|speed|
TEST_F(ShaderTime, PulseMatchesEquation) {
    ExpectPeriod(Analyze({ "pulse", "pulse", { { "AUDIOPROCESSING", "0" } },
                           { { "g_PulseSpeed", { 3.0f } }, { "g_NoiseAmount", { 0.0f } } } }),
                 kTau / 3);
}

// 方程库 fire：10/|speed|（slot 2 按 repeat）
TEST_F(ShaderTime, FireMatchesEquation) {
    ExpectPeriod(Analyze({ "fire", "fire", {}, { { "g_FlowSpeed", { 0.5f } } } }), 10 / 0.5);
}

// 方程库 water_ripple（scrollspeed 0）：1/(a²·|scale|·gcd(W/H, |ratio|))，1920×1080、a = 0.15、scale 1、ratio 1 → 400 s
TEST_F(ShaderTime, WaterRippleMatchesEquation) {
    const std::vector<float> resolution { 1920, 1080, 1920, 1080 };
    ExpectPeriod(Analyze({ "waterripple", "waterripple", {},
                           { { "g_AnimationSpeed", { 0.15f } }, { "g_Scale", { 1.0f } }, { "g_ScrollSpeed", { 0.0f } },
                             { "g_Ratio", { 1.0f } }, { "g_Texture0Resolution", resolution } } }),
                 400);
}

// 方程库认不出的三种：给出签名（周期由各自的滚动/旋转速率决定）
TEST_F(ShaderTime, CloudsScrollPeriod) {
    // 两层云按 repeat 滚动（默认 speed 0.01/-0.02、scale 1.3/0.5，x 轴再乘 16/9）：
    // 四条轴周期 1125/26、1000/13、100、225/4，LCM 9000 s。不给 g_Texture0Resolution 时系数不定，判不周期。
    ExpectPeriod(Analyze({ "clouds", "clouds", {}, { { "g_Texture0Resolution", { 1920, 1080, 1920, 1080 } } } }), 9000);
}

TEST_F(ShaderTime, GodraysNoiseScrollPeriod) {
    // 噪声两次查表：速率 noisespeed·noisescale 与其一半，默认 0.15·3 → 周期 20/9 与 40/9，LCM 40/9
    ExpectPeriod(Analyze({ "godrays", "godrays_downsample2", { { "NOISE", "1" } }, {} }), 40.0 / 9);
}

TEST_F(ShaderTime, ShineCastRotationPeriod) {
    ExpectPeriod(Analyze({ "shine", "shine_cast", {}, { { "g_Speed", { 0.2f } } } }), kTau / 0.2);
}

// 精灵帧索引 int(t·2) % 5 与 mod(floor(t·2), 5)：取整是阶梯，再取模按周期 5/2 s（不是"线性时间经过取整"的不周期）
TEST_F(ShaderTime, IntegerModFrameIndexPeriod) {
    const std::string vert = "attribute vec3 a_Position;\nvoid main() { gl_Position = vec4(a_Position, 1.0); }\n";
    for (const char* index : { "float(int(g_Time * 2.0) % 5)", "mod(floor(g_Time * 2.0), 5.0)" }) {
        Case c { "", "imod_frame", {}, {} };
        c.vert = vert;
        c.frag = std::string("uniform float g_Time;\nvoid main() { gl_FragColor = vec4(") + index + " / 5.0, 0.0, 0.0, 1.0); }\n";
        ExpectPeriod(Analyze(c), 2.5);
    }
}

// 旧写法的存储缓冲（Uniform + BufferBlock）同 StorageBuffer 按副作用写入报原因；普通 uniform 块（Block）不算
TEST(ShaderTimeSpirv, BufferBlockIsSideEffect) {
    auto module = [](unsigned int decoration) {
        return std::vector<std::vector<unsigned int>> { {
            0x07230203u, 0x00010000u, 0, 9, 0,
            (2u << 16) | 17, 1,                    // OpCapability Shader
            (3u << 16) | 14, 0, 1,                 // OpMemoryModel Logical GLSL450
            (5u << 16) | 15, 4, 1, 0x6E69616Du, 0, // OpEntryPoint Fragment %1 "main"
            (4u << 16) | 5, 1, 0x6E69616Du, 0,     // OpName %1 "main"
            (3u << 16) | 71, 4, decoration,        // OpDecorate %4 Block(2) / BufferBlock(3)
            (2u << 16) | 19, 2,                    // %2 = OpTypeVoid
            (3u << 16) | 33, 3, 2,                 // %3 = OpTypeFunction %2
            (3u << 16) | 22, 5, 32,                // %5 = OpTypeFloat 32
            (3u << 16) | 30, 4, 5,                 // %4 = OpTypeStruct %5
            (4u << 16) | 32, 6, 2, 4,              // %6 = OpTypePointer Uniform %4
            (4u << 16) | 59, 6, 7, 2,              // %7 = OpVariable %6 Uniform
            (5u << 16) | 54, 2, 1, 0, 3,           // %1 = OpFunction %2 None %3
            (2u << 16) | 248, 8,                   // %8 = OpLabel
            (1u << 16) | 253,                      // OpReturn
            (1u << 16) | 56 } };                   // OpFunctionEnd
    };
    const auto buffer = st::Analyze(module(3), {});
    EXPECT_EQ((buffer.reasons.empty() ? std::string() : buffer.reasons[0]), "unsupported_side_effect") << st::ToJson(buffer);
    EXPECT_EQ(st::Analyze(module(2), {}).kind, "static");
}
