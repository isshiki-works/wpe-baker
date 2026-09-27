#include <gtest/gtest.h>

#include <filesystem>
#include <fstream>

import rstd;
import rstd.cppstd;
import wescene.fs;
import wescene.pkg.parse;
import wescene.scene;
import wescene.types;
import wescene.json;

using namespace rstd::prelude;
using namespace rstd::literals;
using rstd::sync::Arc;

namespace
{

class TrackingImageParser final : public owe::IImageParser {
public:
    auto Parse(ref<str> name) const -> Result<Arc<owe::Image>, owe::ImageParseError> override {
        auto current  = m_active.fetch_add(1) + 1;
        auto observed = m_peak.load();
        while (current > observed && ! m_peak.compare_exchange_weak(observed, current)) {
        }

        std::this_thread::sleep_for(std::chrono::milliseconds(10));
        auto image = Arc<owe::Image>::make();
        image->key = rstd::cppstd::to_string(name);
        m_active.fetch_sub(1);
        return Ok(rstd::move(image));
    }

    auto ParseHeader(ref<str>) const -> Result<owe::ImageHeader, owe::ImageParseError> override {
        return Ok(owe::ImageHeader {});
    }

    auto ParseMany(slice<String> names) const
        -> Vec<Result<Arc<owe::Image>, owe::ImageParseError>> override {
        return owe::ParseImages(this, names);
    }

    int peak() const { return m_peak.load(); }

private:
    mutable std::atomic<int> m_active { 0 };
    mutable std::atomic<int> m_peak { 0 };
};

class MixedImageParser final : public owe::IImageParser {
public:
    auto Parse(ref<str> name) const -> Result<Arc<owe::Image>, owe::ImageParseError> override {
        if (rstd::cppstd::as_string_view(name) == "bad") {
            return Err(owe::ImageParseError {
                .kind    = owe::ImageParseErrorKind::DecodeFailed,
                .message = String::make("bad image"_str),
            });
        }
        auto image = Arc<owe::Image>::make();
        image->key = rstd::cppstd::to_string(name);
        return Ok(rstd::move(image));
    }

    auto ParseHeader(ref<str>) const -> Result<owe::ImageHeader, owe::ImageParseError> override {
        return Ok(owe::ImageHeader {});
    }

    auto ParseMany(slice<String> names) const
        -> Vec<Result<Arc<owe::Image>, owe::ImageParseError>> override {
        return owe::ParseImages(this, names);
    }
};

void WriteU32(std::ofstream& output, std::uint32_t value) {
    output.write(reinterpret_cast<const char*>(&value), sizeof(value));
}

// TEXB0004 变体贴图的最小合成夹具。
struct TexBytes {
    std::string data;
    void        u32(std::uint32_t value) { data.append(reinterpret_cast<const char*>(&value), 4); }
    void        f32(float value) { data.append(reinterpret_cast<const char*>(&value), 4); }
    void        cstr(std::string_view value) {
        data.append(value);
        data.push_back('\0');
    }
    // 补丁条目：{ 官方不读的 u32, id, x, y, w, h, FreeImage 格式, size, 字节 }
    void patch(std::uint32_t id, std::uint32_t x, std::uint32_t y, std::uint32_t w, std::uint32_t h,
               const std::string& body) {
        for (std::uint32_t v : { 1u, id, x, y, w, h, 13u, std::uint32_t(body.size()) }) u32(v);
        data += body;
    }
};

// 只有一段字面量的 LZ4 块（合法：LZ4 块的最后一段本来就是字面量）。
std::string Lz4Literals(const std::string& raw) {
    std::string out;
    std::size_t n = raw.size();
    if (n < 15) {
        out.push_back(static_cast<char>(n << 4));
    } else {
        out.push_back(static_cast<char>(0xF0));
        for (n -= 15; n >= 255; n -= 255) out.push_back(static_cast<char>(255));
        out.push_back(static_cast<char>(n));
    }
    return out + raw;
}

// TEXV/TEXI 头 + TEXB0004 头（image_type=-1）+ 条件表。
TexBytes TexVariantHeader(std::uint32_t format, std::uint32_t flags, std::uint32_t size,
                          const std::vector<std::tuple<std::uint32_t, std::uint32_t, std::uint32_t,
                                                       std::string>>& conditions) {
    TexBytes tex;
    tex.cstr("TEXV0005");
    tex.cstr("TEXI0001");
    for (std::uint32_t v : { format, flags, size, size, size, size, 0u }) tex.u32(v);
    tex.cstr("TEXB0004");
    tex.u32(1);          // count
    tex.u32(0xFFFFFFFF); // image_type = -1
    tex.u32(static_cast<std::uint32_t>(conditions.size()));
    for (const auto& [group, id, cflags, json] : conditions) {
        tex.u32(group);
        tex.u32(id);
        tex.u32(cflags);
        tex.cstr(json);
    }
    return tex;
}

struct TexFixtureDir {
    std::filesystem::path root;
    owe::fs::VFS          vfs;

    explicit TexFixtureDir(std::string_view tag) {
        root = std::filesystem::temp_directory_path() /
               (std::string("owe-image-parser-") + std::string(tag) + "-" +
                std::to_string(rstd::process::id().to_primitive()));
        std::filesystem::remove_all(root);
        std::filesystem::create_directories(root / "materials");
    }
    ~TexFixtureDir() { std::filesystem::remove_all(root); }

    void Write(std::string_view name, const std::string& bytes) {
        std::ofstream output(root / "materials" / (std::string(name) + ".tex"), std::ios::binary);
        output.write(bytes.data(), static_cast<std::streamsize>(bytes.size()));
    }
    bool Mount() {
        auto physical = owe::fs::make_physical_fs(owe::fs::ToPath(root.string()));
        return physical.is_ok() &&
               vfs.mount("/assets"_str, rstd::move(physical).unwrap_unchecked()).is_ok();
    }
};

std::shared_ptr<const owe::NJson> UserProperties(std::string_view json) {
    return std::make_shared<const owe::NJson>(owe::ParseNJson(json).unwrap());
}

std::string MipBytes(const owe::ImageData& mip) {
    return std::string(reinterpret_cast<const char*>(mip.data.get()),
                       static_cast<std::size_t>(mip.size.to_primitive()));
}

TEST(ImageParser, BatchPreservesOrderAndBoundsConcurrency) {
    TrackingImageParser parser;
    Vec<String>         names;
    for (const char* name : { "0", "1", "2", "3", "4", "5", "6", "7" })
        names.push(String::make(rstd::cppstd::as_str(name).unwrap()));

    auto images = owe::ParseImages(&parser, names.as_slice());

    ASSERT_EQ(images.len(), names.len());
    for (usize index {}; index < names.len(); ++index) {
        ASSERT_TRUE(images[index].is_ok());
        auto image = rstd::move(images[index]).unwrap_unchecked();
        EXPECT_EQ(image->key, rstd::cppstd::to_string(names[index].as_str()));
    }
    EXPECT_GT(parser.peak(), 1);
    EXPECT_LE(parser.peak(), 4);
}

TEST(ImageParser, TextureHeaderExposesFourthPackedComponent) {
    auto root = std::filesystem::temp_directory_path() /
                ("owe-image-parser-" + std::to_string(rstd::process::id().to_primitive()));
    std::filesystem::remove_all(root);
    std::filesystem::create_directories(root / "materials");
    {
        std::ofstream output(root / "materials" / "mask.tex", std::ios::binary);
        output.write("TEXV0005", 9);
        output.write("TEXI0001", 9);
        WriteU32(output, 4);
        WriteU32(output, 1u << 23);
        WriteU32(output, 1);
        WriteU32(output, 1);
        WriteU32(output, 1);
        WriteU32(output, 1);
        WriteU32(output, 0);
        output.write("TEXB0001", 9);
        WriteU32(output, 1);
        WriteU32(output, 1);
        WriteU32(output, 1);
        WriteU32(output, 1);
    }

    auto physical = owe::fs::make_physical_fs(owe::fs::ToPath(root.string()));
    ASSERT_TRUE(physical.is_ok());
    owe::fs::VFS vfs;
    ASSERT_TRUE(vfs.mount("/assets"_str, rstd::move(physical).unwrap_unchecked()).is_ok());

    owe::TexImageParser parser(&vfs);
    auto                parsed = parser.ParseHeader("mask"_str);
    ASSERT_TRUE(parsed.is_ok());
    auto header = rstd::move(parsed).unwrap_unchecked();
    EXPECT_EQ(header.extraHeader.at("compo1").val, 0);
    EXPECT_EQ(header.extraHeader.at("compo2").val, 0);
    EXPECT_EQ(header.extraHeader.at("compo3").val, 0);
    EXPECT_EQ(header.extraHeader.at("compo4").val, 1);

    std::filesystem::remove_all(root);
}

TEST(ImageParser, SceneBatchPreservesRuntimeParserAndErrorPositions) {
    owe::Scene scene;
    scene.SetImageParser(std::make_unique<MixedImageParser>(MixedImageParser {}));
    auto runtime = Arc<owe::Image>::make();
    runtime->key = "runtime-value";
    scene.RegisterRuntimeImage(String::make("runtime"_str), runtime.clone());

    Vec<String> names;
    for (const char* name : { "first", "runtime", "bad", "last" }) {
        names.push(String::make(rstd::cppstd::as_str(name).unwrap()));
    }
    auto images = scene.ParseImages(names.as_slice());

    ASSERT_EQ(images.len(), names.len());
    ASSERT_TRUE(images[usize(0)].is_ok());
    EXPECT_EQ((*images[usize(0)])->key, "first");
    ASSERT_TRUE(images[usize(1)].is_ok());
    EXPECT_TRUE(Arc<owe::Image>::ptr_eq(*images[usize(1)], runtime));
    ASSERT_TRUE(images[usize(2)].is_err());
    EXPECT_EQ(images[usize(2)].unwrap_err_unchecked().kind, owe::ImageParseErrorKind::DecodeFailed);
    ASSERT_TRUE(images[usize(3)].is_ok());
    EXPECT_EQ((*images[usize(3)])->key, "last");
}

// TEXB0004 变体：条件表 + 每个 mip 后的补丁块。BC3 8x8（4 个块）+ 4x4 两级 mip。
// 条件：id1/id2 同组、都看 color=="1"（组内只取第一条）；id3 看 bool 属性 flag；
// id4 也看 flag，但 flags=1（alpha 混合，未实现，保留基础图）。
TEST(ImageParser, TextureVariantPatchesFollowUserProperties) {
    TexFixtureDir dir("variant");
    auto          tex = TexVariantHeader(4, 0, 8,
                                         {
                                    { 1, 1, 0, R"({"condition":{"condition":"1","name":"color"}})" },
                                    { 1, 2, 0, R"({"condition":{"condition":"1","name":"color"}})" },
                                    { 2, 3, 0, R"({"condition":"flag"})" },
                                    { 3, 4, 1, R"({"condition":"flag"})" },
                                });
    // mip_count=2；mip0：8x8 不压缩，64 字节 0
    for (std::uint32_t v : { 2u, 8u, 8u, 0u, 64u, 64u }) tex.u32(v);
    tex.data += std::string(64, '\0');
    tex.u32(1); // 组数
    tex.u32(4); // 条数
    tex.patch(1, 4, 0, 4, 8, std::string(16, '\x11') + std::string(16, '\x12')); // 块 1、3
    tex.patch(2, 0, 0, 8, 8, std::string(64, '\x22'));
    tex.patch(3, 0, 4, 4, 4, std::string(16, '\x33')); // 块 2
    tex.patch(4, 0, 0, 4, 4, std::string(16, '\x44')); // 块 0，不贴
    // mip1：4x4 LZ4，补丁也是 LZ4
    const auto base1 = Lz4Literals(std::string(16, '\0'));
    for (std::uint32_t v : { 4u, 4u, 1u, 16u, std::uint32_t(base1.size()) }) tex.u32(v);
    tex.data += base1;
    tex.u32(1);
    tex.u32(1);
    tex.patch(3, 0, 0, 4, 4, Lz4Literals(std::string(16, '\x55')));
    dir.Write("variant", tex.data);
    ASSERT_TRUE(dir.Mount());

    const auto zero16 = std::string(16, '\0');
    {
        owe::TexImageParser parser(&dir.vfs,
                                   UserProperties(R"({"color":{"value":1},"flag":{"value":true}})"));
        auto parsed = parser.Parse("variant"_str);
        ASSERT_TRUE(parsed.is_ok());
        auto image = rstd::move(parsed).unwrap_unchecked();
        ASSERT_EQ(image->slots.size(), 1u);
        ASSERT_EQ(image->slots[0].mipmaps.size(), 2u);
        EXPECT_EQ(MipBytes(image->slots[0].mipmaps[0]),
                  zero16 + std::string(16, '\x11') + std::string(16, '\x33') +
                      std::string(16, '\x12'));
        EXPECT_EQ(MipBytes(image->slots[0].mipmaps[1]), std::string(16, '\x55'));

        auto header = parser.ParseHeader("variant"_str);
        ASSERT_TRUE(header.is_ok());
        EXPECT_TRUE((*header).mipmap_pow2);
    }
    for (auto props : { UserProperties(R"({"color":{"value":0},"flag":{"value":false}})"),
                        std::shared_ptr<const owe::NJson> {} }) {
        owe::TexImageParser parser(&dir.vfs, props);
        auto                parsed = parser.Parse("variant"_str);
        ASSERT_TRUE(parsed.is_ok());
        auto image = rstd::move(parsed).unwrap_unchecked();
        EXPECT_EQ(MipBytes(image->slots[0].mipmaps[0]), std::string(64, '\0'));
        EXPECT_EQ(MipBytes(image->slots[0].mipmaps[1]), zero16);
    }
}

// 精灵贴图带变体时，TEXS 段在最后一个 mip 的补丁块之后；R8 补丁按行贴到 (2, 1)。
TEST(ImageParser, SpriteTextureVariantBlocks) {
    TexFixtureDir dir("variant-sprite");
    auto          tex = TexVariantHeader(9, 1u << 2, 4, { { 1, 1, 0, R"({"condition":"flag"})" } });
    for (std::uint32_t v : { 1u, 4u, 4u, 0u, 16u, 16u }) tex.u32(v);
    tex.data += std::string(16, '\0');
    tex.u32(1);
    tex.u32(1);
    tex.patch(1, 2, 1, 2, 2, "\x7f\x7e\x7d\x7c");
    tex.cstr("TEXS0003");
    for (std::uint32_t v : { 1u, 4u, 4u, 0u }) tex.u32(v); // frame_count, atlas w/h, image_id
    for (float v : { 1.0f, 0.0f, 0.0f, 4.0f, 0.0f, 0.0f, 4.0f }) tex.f32(v);
    dir.Write("sprite", tex.data);
    ASSERT_TRUE(dir.Mount());

    owe::TexImageParser parser(&dir.vfs, UserProperties(R"({"flag":{"value":true}})"));
    auto                header = parser.ParseHeader("sprite"_str);
    ASSERT_TRUE(header.is_ok());
    EXPECT_EQ((*header).spriteAnim.numFrames(), usize(1));

    auto parsed = parser.Parse("sprite"_str);
    ASSERT_TRUE(parsed.is_ok());
    auto expected = std::string(16, '\0');
    expected.replace(6, 2, "\x7f\x7e");
    expected.replace(10, 2, "\x7d\x7c");
    EXPECT_EQ(MipBytes(rstd::move(parsed).unwrap_unchecked()->slots[0].mipmaps[0]), expected);
}

// 损坏的贴图按解析失败处理，不越界、不按声明值做超大分配。
TEST(ImageParser, CorruptTextureSizesAreRejected) {
    TexFixtureDir            dir("corrupt");
    std::vector<std::string> cases;
    { // RGBA8 8x8 只有 16 字节
        auto tex = TexVariantHeader(0, 0, 8, {});
        for (std::uint32_t v : { 1u, 8u, 8u, 0u, 16u, 16u }) tex.u32(v);
        tex.data += std::string(16, '\0');
        cases.push_back(tex.data);
    }
    { // BC3 宽 2^30、高 2^30+4 只有 16 字节；命中的补丁在最后两块行，旧的偏移乘法会回绕
        auto tex = TexVariantHeader(4, 0, 8, { { 1, 1, 0, R"({"condition":"flag"})" } });
        for (std::uint32_t v : { 1u, 1u << 30, (1u << 30) + 4, 0u, 16u, 16u }) tex.u32(v);
        tex.data += std::string(16, '\0');
        tex.u32(1);
        tex.u32(1);
        tex.patch(1, 0, (1u << 30) - 4, 4, 8, std::string(32, '\x11'));
        cases.push_back(tex.data);
    }
    { // LZ4：16 字节声明解压成 2 GiB
        auto tex = TexVariantHeader(0, 0, 8, {});
        for (std::uint32_t v : { 1u, 8u, 8u, 1u, 0x7fffffffu, 16u }) tex.u32(v);
        tex.data += std::string(16, '\0');
        cases.push_back(tex.data);
    }
    { // 声明 2 GiB 字节，文件里只有 16
        auto tex = TexVariantHeader(0, 0, 8, {});
        for (std::uint32_t v : { 1u, 8u, 8u, 0u, 0u, 0x7fffffffu }) tex.u32(v);
        tex.data += std::string(16, '\0');
        cases.push_back(tex.data);
    }
    { // mip 数 2^31-1
        auto tex = TexVariantHeader(0, 0, 8, {});
        tex.u32(0x7fffffffu);
        cases.push_back(tex.data);
    }
    { // RGBA8 4x4 的第 1 级声明成 4x4，GPU 上这一级只有 2x2
        auto tex = TexVariantHeader(0, 0, 4, {});
        tex.u32(2);
        for (std::uint32_t v : { 4u, 4u, 0u, 0u, 64u }) tex.u32(v);
        tex.data += std::string(64, '\0');
        for (std::uint32_t v : { 4u, 4u, 0u, 0u, 64u }) tex.u32(v);
        tex.data += std::string(64, '\0');
        cases.push_back(tex.data);
    }
    { // DXT1 16x16 原始第 0 级 + 内嵌 PNG 的第 1 级：PNG 把整张贴图改成 RGBA8，第 0 级的 128 字节只够 BC1
        static const char kPng8x8[] =
            "\x89\x50\x4e\x47\x0d\x0a\x1a\x0a\x00\x00\x00\x0d\x49\x48\x44\x52\x00\x00\x00\x08\x00\x00"
            "\x00\x08\x08\x06\x00\x00\x00\xc4\x0f\xbe\x8b\x00\x00\x00\x12\x49\x44\x41\x54\x78\x9c\x63"
            "\xf8\xcf\xc0\xf0\x1f\x1f\x66\x18\x19\x0a\x00\xc2\xd7\x7f\x81\x2f\x71\xe0\x01\x00\x00\x00"
            "\x00\x49\x45\x4e\x44\xae\x42\x60\x82";
        const std::string png(kPng8x8, sizeof(kPng8x8) - 1);
        auto              tex = TexVariantHeader(7, 0, 16, {});
        tex.u32(2);
        for (std::uint32_t v : { 16u, 16u, 0u, 0u, 128u }) tex.u32(v);
        tex.data += std::string(128, '\x11');
        for (std::uint32_t v : { 8u, 8u, 0u, 0u, std::uint32_t(png.size()) }) tex.u32(v);
        tex.data += png;
        cases.push_back(tex.data);
    }
    { // RGBA8 1x1 带第 2 级：GPU 上 1x1 只有一级
        auto tex = TexVariantHeader(0, 0, 1, {});
        tex.u32(2);
        for (int i = 0; i < 2; ++i) {
            for (std::uint32_t v : { 1u, 1u, 0u, 0u, 4u }) tex.u32(v);
            tex.data += std::string(4, '\0');
        }
        cases.push_back(tex.data);
    }
    for (std::size_t i = 0; i < cases.size(); ++i) dir.Write("corrupt" + std::to_string(i), cases[i]);
    ASSERT_TRUE(dir.Mount());

    owe::TexImageParser parser(&dir.vfs, UserProperties(R"({"flag":{"value":true}})"));
    for (std::size_t i = 0; i < cases.size(); ++i) {
        const auto name   = "corrupt" + std::to_string(i);
        auto       parsed = parser.Parse(rstd::cppstd::as_str(name).unwrap());
        ASSERT_TRUE(parsed.is_err()) << name;
        EXPECT_TRUE(rstd::move(parsed).unwrap_err().kind == owe::ImageParseErrorKind::InvalidData)
            << name;
    }
}

// 精灵贴图的头解析（ParseHeader）与 Parse 一样按剩余字节限住声明的图数、mip 数、帧数。
TEST(ImageParser, CorruptSpriteHeaderCountsAreRejected) {
    TexFixtureDir            dir("corrupt-sprite-header");
    std::vector<std::string> cases;
    { // 图数 2^31-1：旧代码先按这个数建 imageDatas（约 51 GB）
        auto                tex   = TexVariantHeader(0, 1u << 2, 4, {});
        const std::uint32_t count = 0x7fffffffu;
        tex.data.replace(55, 4, reinterpret_cast<const char*>(&count), 4); // TEXB0004 后的 count
        cases.push_back(tex.data);
    }
    { // 一张 4x4 图，TEXS0002 声明 2^31-1 帧后直接结束：旧代码读出 imageId=0 一直追加
        auto tex = TexVariantHeader(0, 1u << 2, 4, {});
        for (std::uint32_t v : { 1u, 4u, 4u, 0u, 0u, 0u }) tex.u32(v);
        tex.cstr("TEXS0002");
        tex.u32(0x7fffffffu);
        cases.push_back(tex.data);
    }
    { // mip 数 2^31-1：旧代码过了文件尾仍逐个空转
        auto tex = TexVariantHeader(0, 1u << 2, 4, {});
        tex.u32(0x7fffffffu);
        cases.push_back(tex.data);
    }
    for (std::size_t i = 0; i < cases.size(); ++i) dir.Write("sprite" + std::to_string(i), cases[i]);
    ASSERT_TRUE(dir.Mount());

    owe::TexImageParser parser(&dir.vfs);
    for (std::size_t i = 0; i < cases.size(); ++i) {
        const auto name   = "sprite" + std::to_string(i);
        auto       header = parser.ParseHeader(rstd::cppstd::as_str(name).unwrap());
        ASSERT_TRUE(header.is_err()) << name;
        EXPECT_TRUE(rstd::move(header).unwrap_err().kind == owe::ImageParseErrorKind::InvalidData)
            << name;
    }
}

// 按 GPU 取整规则（向下、至少 1）减半的完整 mip 链照常解析，非 2 的幂也一样。
TEST(ImageParser, MipChainWithinGpuLevelsParses) {
    TexFixtureDir dir("mipchain");
    auto          tex = TexVariantHeader(0, 0, 8, {});
    tex.u32(3);
    const std::pair<std::uint32_t, std::uint32_t> levels[] = { { 3, 5 }, { 1, 2 }, { 1, 1 } };
    for (auto [w, h] : levels) {
        for (std::uint32_t v : { w, h, 0u, 0u, w * h * 4 }) tex.u32(v);
        tex.data += std::string(w * h * 4, '\0');
    }
    dir.Write("chain", tex.data);
    ASSERT_TRUE(dir.Mount());

    owe::TexImageParser parser(&dir.vfs);
    auto                parsed = parser.Parse("chain"_str);
    ASSERT_TRUE(parsed.is_ok());
    EXPECT_EQ(rstd::move(parsed).unwrap_unchecked()->slots[0].mipmaps.size(), 3u);
}

// 贴图名只在挂载的包和资产目录里找，不按本机绝对路径读文件。
TEST(ImageParser, AbsoluteTextureNamesStayInsideMounts) {
    TexFixtureDir dir("absolute");
    ASSERT_TRUE(dir.Mount());
    const auto outside = std::filesystem::current_path() / "owe-absolute-texture.png";
    std::ofstream(outside, std::ios::binary) << "not a png";
    const auto name = "/" + outside.relative_path().generic_string();

    owe::TexImageParser parser(&dir.vfs);
    auto                parsed = parser.Parse(rstd::cppstd::as_str(name).unwrap());
    std::filesystem::remove(outside);
    ASSERT_TRUE(parsed.is_err());
    EXPECT_TRUE(rstd::move(parsed).unwrap_err().kind == owe::ImageParseErrorKind::MissingContent);
}

} // namespace
