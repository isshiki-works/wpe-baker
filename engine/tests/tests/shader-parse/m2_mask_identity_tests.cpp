// M2 遮罩支撑区：源码规范化与"遮罩为 0 处恒等"结构识别的单测。
// 每种被接受的形态各有一个正例，以及破坏其中每条前提的反例。

#include <gtest/gtest.h>

#include <cstdint>
#include <optional>
#include <string>
#include <vector>

import wescene.pkg.parse;

namespace m2 = owe::m2;

namespace
{

std::optional<long> NoCombos(const std::string&) { return std::nullopt; }

std::vector<std::string> Active(const std::string& src, m2::ComboLookup combo = NoCombos) {
    auto t = m2::ActiveTokens(m2::Tokenize(src), combo);
    EXPECT_TRUE(t.has_value());
    return t ? *t : std::vector<std::string> {};
}

const std::string kVertScaleZW = R"(
#include "common.h"
uniform mat4 g_ModelViewProjectionMatrix;
uniform vec4 g_Texture1Resolution;
attribute vec3 a_Position;
attribute vec2 a_TexCoord;
varying vec4 v_TexCoord;
void main() {
	gl_Position = mul(vec4(a_Position, 1.0), g_ModelViewProjectionMatrix);
	v_TexCoord = a_TexCoord.xyxy;
	v_TexCoord.z *= g_Texture1Resolution.z / g_Texture1Resolution.x;
	v_TexCoord.w *= g_Texture1Resolution.w / g_Texture1Resolution.y;
}
)";

const std::string kVertVec2ZW = R"(
uniform mat4 g_ModelViewProjectionMatrix;
uniform vec4 g_Texture2Resolution;
attribute vec3 a_Position;
attribute vec2 a_TexCoord;
varying vec4 v_TexCoord;
void main() {
	gl_Position = mul(vec4(a_Position, 1.0), g_ModelViewProjectionMatrix);
	v_TexCoord = a_TexCoord.xyxy;
	v_TexCoord.zw = vec2(a_TexCoord.x * g_Texture2Resolution.z / g_Texture2Resolution.x,
		a_TexCoord.y * g_Texture2Resolution.w / g_Texture2Resolution.y);
}
)";

// Warp 形态（水波类）：坐标只按"积 × 遮罩"累加。
const std::string kWarpFrag = R"(
// [COMBO] {"material":"x","combo":"DUAL","type":"options","default":0}
varying vec4 v_TexCoord;
uniform sampler2D g_Texture0;
uniform sampler2D g_Texture1; // {"combo":"MASK"}
uniform float g_Time;
uniform float g_Exponent; // {"material":"exponent","default":1}
void main() {
#if MASK
	float mask = texSample2D(g_Texture1, v_TexCoord.zw).r;
#else
	float mask = 1.0;
#endif
	vec2 texCoord = v_TexCoord.xy;
	vec2 motion = texCoord;
	float val = pow(abs(sin(g_Time + motion.x)), g_Exponent);
#if DUAL == 1
	texCoord += val * vec2(0.01, 0.02) * mask;
#else
	texCoord += val * vec2(0.03, 0.0) * mask;
#endif
	gl_FragColor = texSample2D(g_Texture0, texCoord);
}
)";

// Mix 形态（脉冲类）：A = mix(S, E, M) 后 gl_FragColor = vec4(max(CAST3(0), A.rgb), A.a)。
const std::string kMixFrag = R"(
varying vec4 v_TexCoord;
uniform sampler2D g_Texture0;
uniform sampler2D g_Texture2;
uniform vec2 g_Bounds; // {"material":"bounds","default":"0 1"}
uniform float g_Time;
void main() {
	vec4 sample = texSample2D(g_Texture0, v_TexCoord.xy);
	vec4 albedo = sample;
	float pulse = smoothstep(g_Bounds.x, g_Bounds.y, sin(g_Time) * 0.5 + 0.5);
	albedo.rgb = albedo.rgb * pulse;
#if MASK
	float mask = texSample2D(g_Texture2, v_TexCoord.zw).r;
	albedo = mix(sample, albedo, mask);
#endif
	gl_FragColor = vec4(max(CAST3(0), albedo.rgb), albedo.a);
}
)";

std::optional<long> MaskOn(const std::string& name) {
    if (name == "MASK") return 1;
    return std::nullopt;
}

m2::Recognition Rec(const std::string& vert, const std::string& frag, m2::ComboLookup combo = MaskOn) {
    return m2::Recognize(Active(vert, combo), Active(frag, combo));
}

std::string Replace(std::string s, const std::string& from, const std::string& to) {
    auto pos = s.find(from);
    EXPECT_NE(pos, std::string::npos) << from;
    if (pos != std::string::npos) s.replace(pos, from.size(), to);
    return s;
}

} // namespace

TEST(M2Normalize, FormattingAndCommentsDoNotChangeHash) {
    auto read = [](const std::string&) -> std::optional<std::string> { return std::string("#define X 1\n"); };
    const std::string a = "void main() {\n\tgl_FragColor = vec4(1.0); // c\n}\n";
    const std::string b = "/* 注释 */ void  main( )\r\n{ gl_FragColor=vec4( 1.0 ) ;\r\n}";
    EXPECT_EQ(m2::NormalizedSourceHash("", a, read), m2::NormalizedSourceHash("", b, read));
    const std::string c = "void main() { gl_FragColor = vec4(0.0); }";
    EXPECT_NE(m2::NormalizedSourceHash("", a, read), m2::NormalizedSourceHash("", c, read));
    // 预处理行内空白合并，但指令内容有区别
    EXPECT_EQ(m2::Tokenize("#if  MASK // x\nA")[0], "#if MASK");
}

TEST(M2Normalize, IncludeContentParticipates) {
    const std::string frag = "#include \"common.h\"\nvoid main() { gl_FragColor = vec4(1.0); }";
    auto r1 = [](const std::string&) -> std::optional<std::string> { return std::string("float a;"); };
    auto r2 = [](const std::string&) -> std::optional<std::string> { return std::string("float b;"); };
    auto r3 = [](const std::string&) -> std::optional<std::string> { return std::nullopt; };
    EXPECT_NE(m2::NormalizedSourceHash("", frag, r1), m2::NormalizedSourceHash("", frag, r2));
    EXPECT_FALSE(m2::NormalizedSourceHash("", frag, r3).has_value());
}

TEST(M2Normalize, MatchesOfflineReference) {
    // 与 runs/M2/seg2/m2norm.py 的同一算法结果一致（白名单常量由它算出）。
    auto read = [](const std::string&) -> std::optional<std::string> { return std::string("vec2 f(vec2 v){return v;}\n"); };
    const std::string vert = "#include \"common.h\"\r\nvoid main() {\n  gl_Position = vec4(1.5e-3, 2, 0x1F, 1.0f);\n}\n";
    const std::string frag = "// [COMBO] {\"combo\":\"A\"}\n#if A == 1 // c\nuniform float g_X; // {\"default\":1}\n#endif\nvoid main() { gl_FragColor = vec4(g_X) * 0.5; }\n";
    EXPECT_EQ(m2::NormalizedSourceHash(vert, frag, read), std::optional<std::uint64_t>(0x2f9ff9570462b1f0ull));
}

TEST(M2Preprocess, EvaluatesSimpleConditions) {
    auto combo = [](const std::string& n) -> std::optional<long> {
        if (n == "A") return 1;
        if (n == "B") return 2;
        return std::nullopt;
    };
    auto t = m2::ActiveTokens(
        m2::Tokenize("#if A\na\n#else\nb\n#endif\n#if B == 2\nc\n#elif A\nd\n#endif\n#if !C\ne\n#endif\n#ifdef C\nf\n#endif"),
        combo);
    ASSERT_TRUE(t.has_value());
    EXPECT_EQ(*t, (std::vector<std::string> { "a", "c", "e" }));
    // "!=" 与 "!NAME" 都要能切词（曾因 "!=" 死循环耗尽内存）
    auto ne = m2::ActiveTokens(m2::Tokenize("#if B != 2\nx\n#endif\n#if A != 0\ny\n#endif\n#if !C\nz\n#endif"), combo);
    ASSERT_TRUE(ne.has_value());
    EXPECT_EQ(*ne, (std::vector<std::string> { "y", "z" }));
    EXPECT_FALSE(m2::ActiveTokens(m2::Tokenize("#if A && B\nx\n#endif"), combo).has_value());
    EXPECT_FALSE(m2::ActiveTokens(m2::Tokenize("#if A\nx\n"), combo).has_value());
}

TEST(M2Recognize, WarpAccepted) {
    auto r = Rec(kVertScaleZW, kWarpFrag);
    ASSERT_TRUE(r.ok) << r.reason;
    EXPECT_EQ(r.form, m2::Form::Warp);
    EXPECT_EQ(r.mask_slot, 1u);
    EXPECT_EQ(r.guards.positive, (std::vector<std::string> { "g_Exponent" }));
}

TEST(M2Recognize, WarpRejectsEachBrokenPremise) {
    // 遮罩不是整条积的因子
    EXPECT_FALSE(Rec(kVertScaleZW, Replace(kWarpFrag, "vec2(0.03, 0.0) * mask;", "vec2(0.03, 0.0) * mask + 0.001;")).ok);
    // 坐标有别的写入
    EXPECT_FALSE(Rec(kVertScaleZW, Replace(kWarpFrag, "vec2 motion = texCoord;", "vec2 motion = texCoord; texCoord.x = 0.5;")).ok);
    // 遮罩变量被改写
    EXPECT_FALSE(Rec(kVertScaleZW, Replace(kWarpFrag, "vec2 texCoord = v_TexCoord.xy;", "mask *= 2.0; vec2 texCoord = v_TexCoord.xy;")).ok);
    // 起点不是本像素坐标
    EXPECT_FALSE(Rec(kVertScaleZW, Replace(kWarpFrag, "vec2 texCoord = v_TexCoord.xy;", "vec2 texCoord = v_TexCoord.xy * 0.5;")).ok);
    // 最后采样的不是输入纹理
    EXPECT_FALSE(Rec(kVertScaleZW, Replace(kWarpFrag, "texSample2D(g_Texture0, texCoord)", "texSample2D(g_Texture1, texCoord)")).ok);
    // 有 discard
    EXPECT_FALSE(Rec(kVertScaleZW, Replace(kWarpFrag, "vec2 motion", "if (g_Time > 1.0) discard; vec2 motion")).ok);
    // 遮罩坐标不是 v_TexCoord.zw
    EXPECT_FALSE(Rec(kVertScaleZW, Replace(kWarpFrag, "texSample2D(g_Texture1, v_TexCoord.zw).r", "texSample2D(g_Texture1, v_TexCoord.xy).r")).ok);
    // pow 指数字面量 ≤ 0；可能出 NaN 的函数
    EXPECT_FALSE(Rec(kVertScaleZW, Replace(kWarpFrag, "g_Exponent)", "0.0)")).ok);
    EXPECT_FALSE(Rec(kVertScaleZW, Replace(kWarpFrag, "abs(sin(g_Time + motion.x))", "sqrt(motion.x)")).ok);
    // 顶点着色器改了 xy 或 zw 缩放不标准、或 gl_Position 不是标准 MVP
    EXPECT_FALSE(Rec(Replace(kVertScaleZW, "v_TexCoord = a_TexCoord.xyxy;", "v_TexCoord = a_TexCoord.xyxy; v_TexCoord.x += 0.1;"), kWarpFrag).ok);
    EXPECT_FALSE(Rec(Replace(kVertScaleZW, "g_Texture1Resolution.w / g_Texture1Resolution.y", "g_Texture1Resolution.w / g_Texture1Resolution.x"), kWarpFrag).ok);
    EXPECT_FALSE(Rec(Replace(kVertScaleZW, "vec4(a_Position, 1.0)", "vec4(a_Position * 2.0, 1.0)"), kWarpFrag).ok);
    // MASK 关时活动源码里 mask 是常量，不识别
    EXPECT_FALSE(Rec(kVertScaleZW, kWarpFrag, NoCombos).ok);
}

TEST(M2Recognize, MixAccepted) {
    auto r = Rec(kVertVec2ZW, kMixFrag);
    ASSERT_TRUE(r.ok) << r.reason;
    EXPECT_EQ(r.form, m2::Form::Mix);
    EXPECT_EQ(r.mask_slot, 2u);
    ASSERT_EQ(r.guards.distinct.size(), 1u);
    EXPECT_EQ(r.guards.distinct[0].first, "g_Bounds.x");
    // 直接写 gl_FragColor = mix(输入采样, E, M) 也接受
    const std::string direct = Replace(Replace(kMixFrag, "albedo = mix(sample, albedo, mask);", "gl_FragColor = mix(texSample2D(g_Texture0, v_TexCoord.xy), albedo, mask);"),
                                       "gl_FragColor = vec4(max(CAST3(0), albedo.rgb), albedo.a);", "");
    auto r2 = Rec(kVertVec2ZW, direct);
    EXPECT_TRUE(r2.ok) << r2.reason;
    // gl_FragColor = A 也接受
    auto r3 = Rec(kVertVec2ZW, Replace(kMixFrag, "vec4(max(CAST3(0), albedo.rgb), albedo.a)", "albedo"));
    EXPECT_TRUE(r3.ok) << r3.reason;
}

TEST(M2Recognize, MixRejectsEachBrokenPremise) {
    // 遮罩乘了系数（不是单独的遮罩变量）
    EXPECT_FALSE(Rec(kVertVec2ZW, Replace(kMixFrag, "mix(sample, albedo, mask)", "mix(sample, albedo, mask * 2.0)")).ok);
    // 第一参数不是输入采样
    EXPECT_FALSE(Rec(kVertVec2ZW, Replace(kMixFrag, "mix(sample, albedo, mask)", "mix(albedo, sample, mask)")).ok);
    // 输入采样被改写
    EXPECT_FALSE(Rec(kVertVec2ZW, Replace(kMixFrag, "vec4 albedo = sample;", "vec4 albedo = sample; sample.a = 1.0;")).ok);
    // S 不是本像素坐标采样
    EXPECT_FALSE(Rec(kVertVec2ZW, Replace(kMixFrag, "texSample2D(g_Texture0, v_TexCoord.xy);", "texSample2D(g_Texture0, v_TexCoord.xy * 0.5);")).ok);
    // mix 之后再改结果
    EXPECT_FALSE(Rec(kVertVec2ZW, Replace(kMixFrag, "#endif\n\tgl_FragColor", "#endif\n\talbedo.a = 1.0;\n\tgl_FragColor")).ok);
    // 最后一句外包了会改值的函数
    EXPECT_FALSE(Rec(kVertVec2ZW, Replace(kMixFrag, "max(CAST3(0), albedo.rgb)", "max(CAST3(0.5), albedo.rgb)")).ok);
    // 遮罩变量另作他用
    EXPECT_FALSE(Rec(kVertVec2ZW, Replace(kMixFrag, "albedo = mix(sample, albedo, mask);", "albedo.rgb *= mask; albedo = mix(sample, albedo, mask);")).ok);
    // 顶点着色器遮罩槽与片元不一致
    EXPECT_FALSE(Rec(kVertScaleZW, kMixFrag).ok);
    // 除以非分辨率 uniform：记为非零前提
    auto r = Rec(kVertVec2ZW, Replace(kMixFrag, "albedo.rgb * pulse;", "albedo.rgb * pulse / g_Time;"));
    ASSERT_TRUE(r.ok) << r.reason;
    EXPECT_EQ(r.guards.nonzero, (std::vector<std::string> { "g_Time" }));
}
