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
};

st::Signature Analyze(const Case& c) {
    const auto dir = Assets() / "effects" / c.effect / "shaders" / "effects";
    if (! std::filesystem::exists(dir / (c.shader + ".frag"))) {
        ADD_FAILURE() << "missing " << (dir / (c.shader + ".frag")).string();
        return {};
    }
    owe::SceneShaderVariantDesc desc;
    desc.scene_id    = "effects/" + c.shader;
    desc.shader_name = "effects/" + c.shader;
    for (const auto& [k, v] : c.combos) desc.input_combos[k] = v;
    desc.stages.push_back(owe::SceneShaderVariantStage { .stage      = owe::ShaderType::VERTEX,
                                                         .source_key = "/assets/shaders/effects/" + c.shader + ".vert",
                                                         .source     = ReadText(dir / (c.shader + ".vert")) });
    desc.stages.push_back(owe::SceneShaderVariantStage { .stage      = owe::ShaderType::FRAGMENT,
                                                         .source_key = "/assets/shaders/effects/" + c.shader + ".frag",
                                                         .source     = ReadText(dir / (c.shader + ".frag")) });
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
