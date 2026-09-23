// Per-version corpus tests.
//
// Each category walks the version stamps harvested by the corpus singleton,
// slices the matching entries, and asserts:
//   * the slice is non-empty (otherwise the parameter wouldn't have
//     been generated)
//   * every matching asset satisfies category-specific structural
//     invariants (sane dimensions, plausible bone counts, etc.)
//
// Adding a workshop with a previously-unseen version automatically extends
// the checked data without requiring an allow-list update.

#include <gtest/gtest.h>

#include <new> // wescene.json 的全局模块片段带进 <new>，这里显式包含，免得与隐式 operator new 冲突
#include "JsonNlohmann.hpp"

import rstd.cppstd;
import wescene.json;
import wescene.testing.corpus;

using namespace rstd::literals;
using owe::testing::Corpus;

namespace
{

// ----- parameter generators (see header note about static-local refs) ------

const std::vector<std::string>& AllPkgVersions() {
    static const auto v = [] {
        const auto& s = Corpus::instance().pkg_versions();
        return std::vector<std::string>(s.begin(), s.end());
    }();
    return v;
}
const std::vector<int>& AllTexvVersions() {
    static const auto v = [] {
        const auto& s = Corpus::instance().texv_versions();
        return std::vector<int>(s.begin(), s.end());
    }();
    return v;
}
const std::vector<int>& AllTexiVersions() {
    static const auto v = [] {
        const auto& s = Corpus::instance().texi_versions();
        return std::vector<int>(s.begin(), s.end());
    }();
    return v;
}
const std::vector<int>& AllTexbVersions() {
    static const auto v = [] {
        const auto& s = Corpus::instance().texb_versions();
        return std::vector<int>(s.begin(), s.end());
    }();
    return v;
}
const std::vector<int>& AllTexFormats() {
    static const auto v = [] {
        const auto& s = Corpus::instance().tex_formats();
        return std::vector<int>(s.begin(), s.end());
    }();
    return v;
}
const std::vector<int>& AllMdlvVersions() {
    static const auto v = [] {
        const auto& s = Corpus::instance().mdlv_versions();
        return std::vector<int>(s.begin(), s.end());
    }();
    return v;
}
const std::vector<int>& AllMdlsVersions() {
    static const auto v = [] {
        const auto& s = Corpus::instance().mdls_versions();
        return std::vector<int>(s.begin(), s.end());
    }();
    return v;
}
const std::vector<int>& AllMdlaVersions() {
    static const auto v = [] {
        const auto& s = Corpus::instance().mdla_versions();
        return std::vector<int>(s.begin(), s.end());
    }();
    return v;
}

// 原 rstd as_i64：有符号整数、或不超过 INT64_MAX 的无符号整数才有值。
auto JsonI64Or(const owe::NJson* value, rstd::int64_t default_value) -> rstd::int64_t {
    if (value == nullptr || ! value->is_number_integer()) return default_value;
    if (value->is_number_unsigned() &&
        value->get<std::uint64_t>() > std::uint64_t(std::numeric_limits<std::int64_t>::max()))
        return default_value;
    return value->get<std::int64_t>();
}

auto JsonBoolOr(const owe::NJson* value, bool default_value) -> bool {
    return value != nullptr && value->is_boolean() ? value->get<bool>() : default_value;
}

auto JsonStringOr(const owe::NJson* value, std::string_view default_value) -> std::string {
    return std::string(value != nullptr && value->is_string()
                           ? std::string_view(value->get_ref<const std::string&>())
                           : default_value);
}

} // namespace

// ============================================================================
// scene.pkg version
// ============================================================================

void CheckScenePkgVersion(const std::string& version) {
    auto slice = Corpus::instance().workshops_with_pkg(version);
    ASSERT_FALSE(slice.empty()) << "no workshops for " << version;

    for (const auto& ref : slice) {
        const auto& w = *ref.workshop;
        SCOPED_TRACE("workshop " + w.id);

        const auto* pkg = owe::Find(w.snapshot, "pkg");
        ASSERT_NE(pkg, nullptr);
        const auto* pkg_version = owe::Find(*pkg, "version");
        ASSERT_NE(pkg_version, nullptr);
        EXPECT_EQ(JsonStringOr(pkg_version, ""), version);
        const auto* file_count = owe::Find(*pkg, "file_count");
        ASSERT_NE(file_count, nullptr);
        EXPECT_GT(JsonI64Or(file_count, 0), 0);
        const auto* has_scene_json = owe::Find(*pkg, "has_scene_json");
        ASSERT_NE(has_scene_json, nullptr);
        EXPECT_TRUE(JsonBoolOr(has_scene_json, false));

        const auto* scene = owe::Find(w.snapshot, "scene");
        ASSERT_NE(scene, nullptr);
        const auto* parsed = owe::Find(*scene, "parsed");
        ASSERT_NE(parsed, nullptr);
        EXPECT_TRUE(JsonBoolOr(parsed, false))
            << "scene.json failed: " << JsonStringOr(owe::Find(*scene, "error"), "");
        if (JsonBoolOr(owe::Find(*scene, "is_ortho"), false)) {
            const auto* ortho = owe::Find(*scene, "ortho");
            ASSERT_NE(ortho, nullptr);
            const auto* width  = owe::Find(*ortho, "width");
            const auto* height = owe::Find(*ortho, "height");
            ASSERT_NE(width, nullptr);
            ASSERT_NE(height, nullptr);
            EXPECT_GT(JsonI64Or(width, 0), 0);
            EXPECT_GT(JsonI64Or(height, 0), 0);
        }
    }
}

TEST(ScenePkgVersionTest, AllWorkshopsParseAndExposeSaneScene) {
    for (const auto& version : AllPkgVersions()) CheckScenePkgVersion(version);
}

// ============================================================================
// .tex header version stamps
// ============================================================================

static void CheckTexInvariants(const Corpus::TexRef& ref) {
    const auto& w    = *ref.workshop;
    const auto& t    = *ref.tex;
    SCOPED_TRACE("workshop " + w.id + " tex " + JsonStringOr(owe::Find(t, "path"), ""));
    const auto* ok         = owe::Find(t, "ok");
    const auto* width      = owe::Find(t, "width");
    const auto* height     = owe::Find(t, "height");
    const auto* map_width  = owe::Find(t, "map_width");
    const auto* map_height = owe::Find(t, "map_height");
    const auto* count      = owe::Find(t, "count");
    ASSERT_TRUE(ok != nullptr && width != nullptr && height != nullptr && map_width != nullptr &&
                map_height != nullptr && count != nullptr);
    EXPECT_TRUE(JsonBoolOr(ok, false));
    EXPECT_GT(JsonI64Or(width, 0), 0);
    EXPECT_GT(JsonI64Or(height, 0), 0);
    EXPECT_GT(JsonI64Or(map_width, 0), 0);
    EXPECT_GT(JsonI64Or(map_height, 0), 0);
    EXPECT_GT(JsonI64Or(count, 0), 0);
}

void CheckTexvVersion(int version) {
    auto slice = Corpus::instance().textures_with_texv(version);
    ASSERT_FALSE(slice.empty());
    for (const auto& r : slice) {
        const auto* value = owe::Find(*r.tex, "texv");
        ASSERT_NE(value, nullptr);
        EXPECT_EQ(JsonI64Or(value, -1), version);
        CheckTexInvariants(r);
    }
}
void CheckTexiVersion(int version) {
    auto slice = Corpus::instance().textures_with_texi(version);
    ASSERT_FALSE(slice.empty());
    for (const auto& r : slice) {
        const auto* value = owe::Find(*r.tex, "texi");
        ASSERT_NE(value, nullptr);
        EXPECT_EQ(JsonI64Or(value, -1), version);
        CheckTexInvariants(r);
    }
}
void CheckTexbVersion(int version) {
    auto slice = Corpus::instance().textures_with_texb(version);
    ASSERT_FALSE(slice.empty());
    for (const auto& r : slice) {
        const auto* value = owe::Find(*r.tex, "texb");
        ASSERT_NE(value, nullptr);
        EXPECT_EQ(JsonI64Or(value, -1), version);
        CheckTexInvariants(r);
    }
}
void CheckTexFormat(int format) {
    auto slice = Corpus::instance().textures_with_format(format);
    ASSERT_FALSE(slice.empty());
    for (const auto& r : slice) {
        const auto* value = owe::Find(*r.tex, "format");
        ASSERT_NE(value, nullptr);
        EXPECT_EQ(JsonI64Or(value, -1), format);
        CheckTexInvariants(r);
    }
}

TEST(TextureTexvTest, AllInstancesParse) {
    for (int version : AllTexvVersions()) CheckTexvVersion(version);
}

TEST(TextureTexiTest, AllInstancesParse) {
    for (int version : AllTexiVersions()) CheckTexiVersion(version);
}

TEST(TextureTexbTest, AllInstancesParse) {
    for (int version : AllTexbVersions()) CheckTexbVersion(version);
}

TEST(TextureFormatTest, AllInstancesParse) {
    for (int format : AllTexFormats()) CheckTexFormat(format);
}

// ============================================================================
// .mdl header version stamps
// ============================================================================

static void CheckMdlInvariants(const Corpus::MdlRef& ref) {
    const auto& w    = *ref.workshop;
    const auto& m    = *ref.mdl;
    SCOPED_TRACE("workshop " + w.id + " mdl " + JsonStringOr(owe::Find(m, "path"), ""));
    // Failed parses are tolerated, but the version stamps must still be
    // readable.
    const auto* mdlv = owe::Find(m, "mdlv");
    const auto* mdls = owe::Find(m, "mdls");
    const auto* mdla = owe::Find(m, "mdla");
    const auto* ok   = owe::Find(m, "ok");
    ASSERT_TRUE(mdlv != nullptr && mdls != nullptr && mdla != nullptr && ok != nullptr);
    EXPECT_GE(JsonI64Or(mdlv, -1), 0);
    EXPECT_GE(JsonI64Or(mdls, -1), 0);
    EXPECT_GE(JsonI64Or(mdla, -1), 0);
    // 静态网格（不带 puppet）现在也解析成功，快照里没有 bone_tree；骨骼数只对 puppet 检查。
    if (JsonBoolOr(ok, false) && owe::Find(m, "bone_tree") != nullptr) {
        const auto* bones = owe::Find(m, "bones");
        ASSERT_NE(bones, nullptr);
        EXPECT_GT(JsonI64Or(bones, 0), 0);
    }
}

void CheckMdlvVersion(int version) {
    auto slice = Corpus::instance().mdls_with_mdlv(version);
    ASSERT_FALSE(slice.empty());
    for (const auto& r : slice) {
        const auto* value = owe::Find(*r.mdl, "mdlv");
        ASSERT_NE(value, nullptr);
        EXPECT_EQ(JsonI64Or(value, -1), version);
        CheckMdlInvariants(r);
    }
}
void CheckMdlsVersion(int version) {
    auto slice = Corpus::instance().mdls_with_mdls(version);
    ASSERT_FALSE(slice.empty());
    for (const auto& r : slice) {
        const auto* value = owe::Find(*r.mdl, "mdls");
        ASSERT_NE(value, nullptr);
        EXPECT_EQ(JsonI64Or(value, -1), version);
        CheckMdlInvariants(r);
    }
}
void CheckMdlaVersion(int version) {
    auto slice = Corpus::instance().mdls_with_mdla(version);
    ASSERT_FALSE(slice.empty());
    for (const auto& r : slice) {
        const auto* value = owe::Find(*r.mdl, "mdla");
        ASSERT_NE(value, nullptr);
        EXPECT_EQ(JsonI64Or(value, -1), version);
        CheckMdlInvariants(r);
    }
}

TEST(MdlMdlvTest, AllInstancesExposeStamps) {
    for (int version : AllMdlvVersions()) CheckMdlvVersion(version);
}

TEST(MdlMdlsTest, AllInstancesExposeStamps) {
    for (int version : AllMdlsVersions()) CheckMdlsVersion(version);
}

TEST(MdlMdlaTest, AllInstancesExposeStamps) {
    for (int version : AllMdlaVersions()) CheckMdlaVersion(version);
}

// ============================================================================
// Smoke: corpus must contain at least one workshop with at least one of
// each major asset class. Catches an empty workshop dir / pathing bug.
// ============================================================================

TEST(CorpusSmoke, NonEmpty) {
    const auto& c = Corpus::instance();
    EXPECT_FALSE(c.entries().empty());
    EXPECT_FALSE(c.pkg_versions().empty());
    EXPECT_FALSE(c.texv_versions().empty());
    EXPECT_FALSE(c.mdlv_versions().empty());
}
