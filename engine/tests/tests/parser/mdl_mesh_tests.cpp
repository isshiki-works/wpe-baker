#include <gtest/gtest.h>

#include <bit>
#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <filesystem>
#include <fstream>
#include <vector>

import eigen;
import rstd.cppstd;
import wescene.core;
import wescene.fs;
import wescene.pkg_fs;
import wescene.pkg.parse;
import wescene.scene;
import wescene.spec_names;

using namespace rstd::prelude;
using namespace rstd::literals;
using rstd::sync::Arc;

namespace
{

void AppendU32(std::vector<std::uint8_t>& bytes, std::uint32_t value) {
    for (unsigned shift = 0; shift < 32; shift += 8)
        bytes.push_back(static_cast<std::uint8_t>(value >> shift));
}

void AppendU16(std::vector<std::uint8_t>& bytes, std::uint16_t value) {
    bytes.push_back(static_cast<std::uint8_t>(value));
    bytes.push_back(static_cast<std::uint8_t>(value >> 8));
}

void AppendFloat(std::vector<std::uint8_t>& bytes, float value) {
    AppendU32(bytes, std::bit_cast<std::uint32_t>(value));
}

void AppendString(std::vector<std::uint8_t>& bytes, std::string_view text) {
    bytes.insert(bytes.end(), text.begin(), text.end());
    bytes.push_back(0);
}

std::vector<std::uint8_t> MdlPrefix(std::uint32_t meshes = 0) {
    std::vector<std::uint8_t> bytes;
    AppendString(bytes, "MDLV0023");
    AppendU32(bytes, 0);
    AppendU32(bytes, 1);
    AppendU32(bytes, meshes);
    return bytes;
}

std::size_t BeginMdlBlock(std::vector<std::uint8_t>& bytes, std::string_view tag) {
    AppendString(bytes, tag);
    auto offset = bytes.size();
    AppendU32(bytes, 0);
    return offset;
}

void FinishMdlBlock(std::vector<std::uint8_t>& bytes, std::size_t end_field) {
    auto end = static_cast<std::uint32_t>(bytes.size());
    for (unsigned i = 0; i < 4; ++i)
        bytes[end_field + i] = static_cast<std::uint8_t>(end >> (i * 8));
}

void AppendEmptyMdls(std::vector<std::uint8_t>& bytes, std::uint16_t extras = 0) {
    auto end_field = BeginMdlBlock(bytes, "MDLS0004");
    AppendU16(bytes, 0); // bones
    AppendU16(bytes, 0); // padding
    AppendU16(bytes, extras);
    bytes.insert(bytes.end(), 12, 0); // header fields and three absent metadata arrays
    FinishMdlBlock(bytes, end_field);
}

void AppendAffine(std::vector<std::uint8_t>& bytes, float x, float y) {
    for (float value : { 1.0f,
                         0.0f,
                         0.0f,
                         0.0f,
                         0.0f,
                         1.0f,
                         0.0f,
                         0.0f,
                         0.0f,
                         0.0f,
                         1.0f,
                         0.0f,
                         x,
                         y,
                         0.0f,
                         1.0f })
        AppendFloat(bytes, value);
}

void AppendBone(std::vector<std::uint8_t>& bytes, std::uint32_t parent, float x, float y) {
    AppendString(bytes, "");
    AppendU32(bytes, 3); // IK chain bone
    AppendU32(bytes, parent);
    AppendU32(bytes, 64);
    AppendAffine(bytes, x, y);
    AppendString(bytes, "{}");
}

void AppendBoneFrames(std::vector<std::uint8_t>& bytes, float x, float y) {
    for (unsigned frame = 0; frame < 2; ++frame) {
        AppendFloat(bytes, x);
        AppendFloat(bytes, y);
        AppendFloat(bytes, 0.0f);
        for (unsigned i = 0; i < 3; ++i) AppendFloat(bytes, 0.0f);
        for (unsigned i = 0; i < 3; ++i) AppendFloat(bytes, 1.0f);
    }
}

std::vector<std::uint8_t> MdlWithIkRig(float end_segment_length = 3.0f) {
    auto bytes    = MdlPrefix();
    auto mdls_end = BeginMdlBlock(bytes, "MDLS0004");
    AppendU16(bytes, 3);
    AppendU16(bytes, 0);
    AppendBone(bytes, owe::Puppet::NO_PARENT, 0.0f, 0.0f);
    AppendBone(bytes, 0, 2.0f, 0.0f);
    AppendBone(bytes, 1, 3.0f, 0.0f);

    AppendU16(bytes, 2); // one paired-controller set
    bytes.push_back(0);
    AppendU32(bytes, 2);
    AppendU32(bytes, 1);
    AppendAffine(bytes, 0.0f, 4.0f);
    bytes.push_back(0);
    AppendU32(bytes, 2);
    AppendU32(bytes, 0);
    AppendAffine(bytes, 5.0f, 0.0f);

    bytes.push_back(0);
    AppendU32(bytes, 0);
    AppendU16(bytes, 3);
    AppendFloat(bytes, 0.0f);
    AppendFloat(bytes, 2.0f);
    AppendFloat(bytes, end_segment_length);
    AppendU16(bytes, 1);
    AppendU32(bytes, 1);
    AppendFloat(bytes, 1.0f);
    AppendFloat(bytes, 0.0f);
    AppendFloat(bytes, 0.0f);
    AppendU16(bytes, 1);
    AppendU32(bytes, 2);
    AppendFloat(bytes, 1.0f);
    AppendFloat(bytes, 0.0f);
    AppendFloat(bytes, 0.0f);
    AppendU16(bytes, 0);

    AppendU16(bytes, 1);
    AppendU32(bytes, 0);
    AppendU32(bytes, 1);
    AppendU32(bytes, 1);
    AppendU16(bytes, 1);
    AppendU32(bytes, 0);
    AppendU16(bytes, 1);
    AppendU32(bytes, 2);
    AppendU32(bytes, 1);
    AppendFloat(bytes, 5.0f);
    AppendU32(bytes, 0);
    AppendU16(bytes, 3);
    AppendU32(bytes, 0);
    AppendU32(bytes, 1);
    AppendU32(bytes, 2);
    bytes.insert(bytes.end(), 3, 0); // absent metadata trailer tables
    FinishMdlBlock(bytes, mdls_end);

    auto mdla_end = BeginMdlBlock(bytes, "MDLA0003");
    AppendU32(bytes, 1);
    AppendU32(bytes, 1);
    AppendU32(bytes, 0);
    AppendString(bytes, "clip");
    AppendString(bytes, "loop");
    AppendFloat(bytes, 1.0f);
    AppendU32(bytes, 1); // length: two samples
    AppendU32(bytes, 0);
    AppendU32(bytes, 3);
    for (float x : { 0.0f, 2.0f, 3.0f }) {
        AppendU32(bytes, 0);
        AppendU32(bytes, 72);
        AppendBoneFrames(bytes, x, 0.0f);
    }
    AppendU32(bytes, 0); // trans_flag
    AppendU32(bytes, 72);
    AppendBoneFrames(bytes, 0.0f, 4.0f);
    AppendU32(bytes, 0); // next track separator
    AppendU32(bytes, 72);
    AppendBoneFrames(bytes, 5.0f, 0.0f);
    AppendU32(bytes, 0); // controller-track trailer
    bytes.push_back(0);  // no per-bone blend curves
    AppendU32(bytes, 0); // no animation events
    FinishMdlBlock(bytes, mdla_end);
    return bytes;
}

std::vector<std::uint8_t> MdlWithPlayMode(std::string_view mode, bool terminated) {
    auto bytes = MdlPrefix();
    AppendEmptyMdls(bytes);
    auto end_field = BeginMdlBlock(bytes, "MDLA0001");
    AppendU32(bytes, 1); // animations
    AppendU32(bytes, 1); // id
    AppendU32(bytes, 0);
    AppendString(bytes, "clip");
    bytes.insert(bytes.end(), mode.begin(), mode.end());
    if (terminated) {
        bytes.push_back(0);
        bytes.insert(bytes.end(), 20, 0); // fps、length、flags、骨骼轨数、事件数
    }
    FinishMdlBlock(bytes, end_field);
    if (! terminated) bytes.push_back(0); // must not be consumed beyond MDLA's boundary
    return bytes;
}

// MDLA0006，两条无骨骼动画：第一条的 flags 由参数给（0x400 位带源动画引用尾块），第二条普通。
std::vector<std::uint8_t> MdlaV6WithFirstFlags(std::uint32_t flags) {
    auto bytes = MdlPrefix();
    AppendEmptyMdls(bytes);
    auto end_field = BeginMdlBlock(bytes, "MDLA0006");
    AppendU32(bytes, 2); // animations
    struct Clip {
        std::uint32_t    id;
        std::string_view name;
        std::string_view mode;
        std::uint32_t    flags;
    };
    for (const Clip& clip : { Clip { 46, "Glance", "", flags }, Clip { 47, "clip", "loop", 0 } }) {
        AppendU32(bytes, clip.id);
        AppendU32(bytes, 0);
        AppendString(bytes, clip.name);
        AppendString(bytes, clip.mode);
        AppendFloat(bytes, 30.0f);
        AppendU32(bytes, 1); // length
        AppendU32(bytes, clip.flags);
        AppendU32(bytes, 0); // bone tracks
        AppendU32(bytes, 0); // trans_flag
        bytes.push_back(0);  // no per-bone blend curves
        bytes.push_back(0);  // no v4 events
        for (float v : { -1.0f, -1.0f, -1.0f, 1.0f, 1.0f, 1.0f }) AppendFloat(bytes, v); // AABB
        bytes.push_back(0); // no scalar curves
        if (clip.flags & 0x400) {
            AppendU32(bytes, 1); // 源动画序号
            AppendU16(bytes, 0);
            AppendU32(bytes, 1); // 帧数
            AppendU32(bytes, 0);
            AppendU32(bytes, 0xFFFFFFFFu);
        }
        AppendU32(bytes, 1); // events
        AppendU32(bytes, 0);
        AppendString(bytes, R"({"frame":0,"name":"e"})");
    }
    FinishMdlBlock(bytes, end_field);
    return bytes;
}

bool ParseMdlBytes(const std::vector<std::uint8_t>& bytes, owe::Services& context,
                   owe::Mdl* parsed_mdl = nullptr) {
    static unsigned serial = 0;
    auto root = std::filesystem::temp_directory_path() /
                ("owe-mdl-parser-errors-" + std::to_string(rstd::process::id().to_primitive()) +
                 "-" + std::to_string(++serial));
    if (! std::filesystem::create_directory(root)) {
        ADD_FAILURE() << "Could not create a new MDL fixture directory";
        return false;
    }
    auto file = root / "fixture.mdl";
    {
        std::ofstream output(file, std::ios::binary);
        output.write(reinterpret_cast<const char*>(bytes.data()),
                     static_cast<std::streamsize>(bytes.size()));
        EXPECT_TRUE(output.good());
    }
    bool parsed = false;
    {
        auto physical = owe::fs::make_physical_fs(owe::fs::ToPath(root.string()));
        if (physical.is_err()) {
            ADD_FAILURE() << "Could not mount the MDL fixture directory";
        } else {
            owe::fs::VFS vfs;
            auto mounted = vfs.mount("/assets"_str, rstd::move(physical).unwrap_unchecked());
            EXPECT_TRUE(mounted.is_ok());
            if (mounted.is_ok()) {
                owe::Mdl local_mdl;
                auto&    mdl = parsed_mdl == nullptr ? local_mdl : *parsed_mdl;
                parsed       = owe::MdlParser::Parse("fixture.mdl"_str, vfs, mdl, &context);
            }
        }
    }
    std::filesystem::remove(file);
    std::filesystem::remove(root);
    return parsed;
}

bool HasDiagnostic(const owe::Services& context, std::string_view text) {
    return std::any_of(
        context.diagnostics.begin(), context.diagnostics.end(), [&](const auto& message) {
            return message.find(text) != std::string::npos;
        });
}

std::uint32_t MaxMeshIndex(const owe::Mdl::Mesh& mesh) {
    std::uint32_t max_index = 0;
    for (const auto& tri : mesh.indices) {
        for (std::uint32_t idx : tri) max_index = std::max(max_index, idx);
    }
    return max_index;
}

std::uint32_t CountUvSeamTriangles(const owe::Mdl::Mesh& mesh) {
    std::uint32_t seam_triangles = 0;
    for (const auto& tri : mesh.indices) {
        float min_u = std::numeric_limits<float>::max();
        float max_u = std::numeric_limits<float>::lowest();
        for (std::uint32_t idx : tri) {
            min_u = std::min(min_u, mesh.texcoords[usize(idx)][usize(0)]);
            max_u = std::max(max_u, mesh.texcoords[usize(idx)][usize(0)]);
        }
        if (max_u - min_u > 0.5f) ++seam_triangles;
    }
    return seam_triangles;
}

// MDLA0001 单条动画，事件表条数由参数给；返回的 offset 是条数字段之后（第一条事件）的位置。
std::vector<std::uint8_t> MdlaV1WithEventCount(std::uint32_t count, std::size_t* events_offset) {
    auto bytes = MdlPrefix();
    AppendEmptyMdls(bytes);
    auto end_field = BeginMdlBlock(bytes, "MDLA0001");
    AppendU32(bytes, 1); // animations
    AppendU32(bytes, 1); // id
    AppendU32(bytes, 0);
    AppendString(bytes, "clip");
    AppendString(bytes, "loop");
    AppendFloat(bytes, 30.0f);
    AppendU32(bytes, 1); // length
    AppendU32(bytes, 0); // flags
    AppendU32(bytes, 0); // bone tracks
    AppendU32(bytes, count);
    *events_offset = bytes.size();
    AppendU32(bytes, 0);
    AppendString(bytes, "{}");
    FinishMdlBlock(bytes, end_field);
    return bytes;
}

// 每种块各一段的小夹具：网格（UV + 蒙皮 + parts + masks）、MDLS（1 根骨骼 + 偏移变换/骨骼序号表）、
// MDAT、MDLA0006（骨骼轨、主平移轨、混合曲线、v4 事件、标量曲线、事件表）、MDMP、MDLE。
std::vector<std::uint8_t> MdlWithAllBlocks() {
    auto bytes = MdlPrefix(1);
    AppendString(bytes, "materials/a.json");
    AppendU32(bytes, 0);                                                          // flag_a
    for (float v : { 0.0f, 0.0f, 0.0f, 1.0f, 1.0f, 0.0f }) AppendFloat(bytes, v); // AABB
    AppendU32(bytes, 0x8 | 0x00800000 | 0x01000000);                              // UV + 蒙皮
    AppendU32(bytes, 3 * 52);
    for (float x : { 0.0f, 1.0f, 0.0f }) {
        for (float v : { x, x > 0.0f ? 0.0f : 1.0f, 0.0f }) AppendFloat(bytes, v);
        for (unsigned i = 0; i < 4; ++i) AppendU32(bytes, 0); // blend indices
        for (float w : { 1.0f, 0.0f, 0.0f, 0.0f }) AppendFloat(bytes, w);
        AppendFloat(bytes, x);
        AppendFloat(bytes, 0.5f);
    }
    AppendU32(bytes, 6); // 一个 u16 三角形
    for (std::uint16_t i : { 0, 1, 2 }) AppendU16(bytes, i);
    bytes.push_back(0); // 无 part uv2
    bytes.push_back(1); // parts
    AppendU32(bytes, 16);
    for (std::uint32_t v : { 1u, 0u, 0u, 3u }) AppendU32(bytes, v);
    AppendU32(bytes, 1); // masks
    AppendU32(bytes, 0);
    AppendU32(bytes, 0);
    AppendString(bytes, "materials/m.json");
    AppendU32(bytes, 0);
    for (unsigned list = 0; list < 2; ++list) {
        AppendU32(bytes, 1);
        AppendU32(bytes, 0);
    }

    auto mdls_end = BeginMdlBlock(bytes, "MDLS0004");
    AppendU16(bytes, 1);
    AppendU16(bytes, 0);
    AppendBone(bytes, owe::Puppet::NO_PARENT, 0.0f, 0.0f);
    AppendU16(bytes, 0);             // extras
    bytes.insert(bytes.end(), 9, 0); // metadata header
    bytes.push_back(1);              // offset transforms
    for (unsigned i = 0; i < 3; ++i) AppendFloat(bytes, 0.0f);
    AppendAffine(bytes, 0.0f, 0.0f);
    bytes.push_back(1); // bone-index table
    AppendU32(bytes, 0);
    bytes.push_back(0); // no bone depths
    FinishMdlBlock(bytes, mdls_end);

    auto mdat_end = BeginMdlBlock(bytes, "MDAT0001");
    AppendU16(bytes, 1);
    AppendU16(bytes, 0);
    AppendString(bytes, "att");
    AppendAffine(bytes, 1.0f, 0.0f);
    FinishMdlBlock(bytes, mdat_end);

    auto mdla_end = BeginMdlBlock(bytes, "MDLA0006");
    AppendU32(bytes, 1); // animations
    AppendU32(bytes, 1); // id
    AppendU32(bytes, 0);
    AppendString(bytes, "clip");
    AppendString(bytes, "loop");
    AppendFloat(bytes, 30.0f);
    AppendU32(bytes, 1); // length
    AppendU32(bytes, 0); // flags
    AppendU32(bytes, 1); // bone tracks
    AppendU32(bytes, 0);
    AppendU32(bytes, 72);
    AppendBoneFrames(bytes, 0.0f, 0.0f);
    AppendU32(bytes, 0); // trans_flag
    AppendU32(bytes, 8); // AnimTransMain：length+1 个标量
    AppendFloat(bytes, 0.0f);
    AppendFloat(bytes, 1.0f);
    bytes.push_back(1); // per-bone blend curves
    AppendU32(bytes, 0);
    AppendU32(bytes, 8);
    AppendFloat(bytes, 1.0f);
    AppendFloat(bytes, 1.0f);
    bytes.push_back(1); // v4 events
    AppendU32(bytes, 1);
    AppendFloat(bytes, 0.0f);
    AppendU16(bytes, 2); // curves
    AppendU16(bytes, 0); // flags
    AppendU32(bytes, 8);
    AppendFloat(bytes, 0.0f);
    AppendFloat(bytes, 1.0f);
    AppendU16(bytes, 1);
    AppendU32(bytes, 8);
    AppendFloat(bytes, 1.0f);
    AppendFloat(bytes, 0.0f);
    for (float v : { -1.0f, -1.0f, -1.0f, 1.0f, 1.0f, 1.0f }) AppendFloat(bytes, v); // AABB
    bytes.push_back(1); // scalar curves
    AppendU32(bytes, 0);
    AppendU32(bytes, 4);
    AppendFloat(bytes, 0.5f);
    AppendU32(bytes, 1); // events
    AppendU32(bytes, 0);
    AppendString(bytes, "{}");
    FinishMdlBlock(bytes, mdla_end);

    auto mdmp_end = BeginMdlBlock(bytes, "MDMP0001");
    AppendU16(bytes, 1); // section data count
    AppendFloat(bytes, 0.0f);
    AppendU16(bytes, 0);
    AppendU16(bytes, 0);
    AppendU32(bytes, 1); // shape_id
    AppendU32(bytes, 0);
    AppendString(bytes, "m");
    AppendU32(bytes, 6);
    AppendU32(bytes, 0);
    for (std::uint16_t v : { 0, 1, 2 }) AppendU16(bytes, v);
    AppendU16(bytes, 0); // vertex trailer
    FinishMdlBlock(bytes, mdmp_end);

    auto mdle_end = BeginMdlBlock(bytes, "MDLE0001");
    AppendU32(bytes, 64);
    AppendAffine(bytes, 0.0f, 0.0f);
    FinishMdlBlock(bytes, mdle_end);
    bytes.push_back(0); // trailing_nul
    return bytes;
}

void WriteBytes(const std::filesystem::path& file, const std::vector<std::uint8_t>& bytes) {
    std::ofstream output(file, std::ios::binary | std::ios::trunc);
    output.write(reinterpret_cast<const char*>(bytes.data()),
                 static_cast<std::streamsize>(bytes.size()));
}

// 一个目录挂到 /assets 一次，反复解析其中的文件（模糊用例多，不为每个用例重建目录和 VFS）。
class MdlMount {
public:
    explicit MdlMount(const std::filesystem::path& root) {
        auto physical = owe::fs::make_physical_fs(owe::fs::ToPath(root.string()));
        if (physical.is_ok())
            m_ok = m_vfs.mount("/assets"_str, rstd::move(physical).unwrap_unchecked()).is_ok();
    }

    bool ok() const { return m_ok; }

    bool Parse(const std::string& name, owe::Services& context, owe::Mdl& mdl) {
        return owe::MdlParser::Parse(rstd::cppstd::as_str(name).unwrap(), m_vfs, mdl, &context);
    }

private:
    owe::fs::VFS m_vfs;
    bool         m_ok { false };
};

// 解析结果的内容摘要：全语料对照"正常文件解析行为不变"用，覆盖解析器写出的全部字段和 prepared()
// 的派生值。
class MdlDigest {
public:
    std::string Of(const owe::Mdl& mdl) {
        Add(mdl.header.mdlv, mdl.header.mdl_flag, mdl.header.skin_count, mdl.header.mesh_count);
        Add(mdl.mdls, mdl.mdla, mdl.mdle, mdl.mdmp);
        for (const auto& mesh : mdl.meshes) {
            Add(mesh.mat_json_files.len().to_primitive());
            for (const auto& name : mesh.mat_json_files) Text(name);
            Add(mesh.flag_a,
                mesh.has_flag_a2_one,
                mesh.flag,
                mesh.aabb_min,
                mesh.aabb_max,
                mesh.has_aabb);
            Each(mesh.positions);
            Each(mesh.normals);
            Each(mesh.tangents);
            Each(mesh.extra4);
            Each(mesh.blend_indices);
            Each(mesh.blend_weights);
            Each(mesh.texcoords);
            Each(mesh.texcoord2);
            Each(mesh.indices);
            Each(mesh.part_uv2);
            Each(mesh.part_uv2_pad);
            Add(mesh.parts.len().to_primitive());
            for (const auto& part : mesh.parts) Add(part.id, part.start, part.size);
            Add(mesh.masks.len().to_primitive());
            for (const auto& mask : mesh.masks) {
                Add(mask.leading_a);
                Text(mask.mat_json);
                Each(mask.part_ids_a);
                Each(mask.part_ids_b);
            }
        }
        Add(mdl.morph_sections.len().to_primitive());
        for (const auto& section : mdl.morph_sections) {
            Add(section.event_time, section.event_id, section.sections.len().to_primitive());
            for (const auto& data : section.sections) {
                Add(data.shape_id, data.hash);
                Text(data.tag);
                Each(data.vertices);
                Each(data.vertex_trailers);
                Each(data.trailer);
            }
        }
        Add(mdl.puppet.is_some());
        if (mdl.puppet.is_some()) Puppet(**mdl.puppet);
        char text[17];
        std::snprintf(text, sizeof(text), "%016llx", static_cast<unsigned long long>(m_hash));
        return text;
    }

private:
    void Bytes(const void* data, std::size_t size) {
        const auto* p = static_cast<const std::uint8_t*>(data);
        for (std::size_t i = 0; i < size; ++i) m_hash = (m_hash ^ p[i]) * 1099511628211ull;
    }
    template<typename... T>
    void Add(const T&... values) {
        (Bytes(&values, sizeof(values)), ...);
    }
    void Text(const String& text) {
        auto view = rstd::cppstd::as_string_view(text.as_str());
        Add(view.size());
        Bytes(view.data(), view.size());
    }
    template<typename T>
    void Each(const Vec<T>& values) {
        Add(values.len().to_primitive());
        for (const auto& value : values) Add(value);
    }
    void Matrix(const Eigen::Affine3f& m) { Bytes(m.matrix().data(), sizeof(float) * 16); }
    void Frames(const Vec<owe::Puppet::BoneFrame>& frames) {
        Add(frames.len().to_primitive());
        for (const auto& f : frames) {
            for (float v : f.position) Add(v);
            for (float v : f.angle) Add(v);
            for (float v : f.scale) Add(v);
        }
    }
    void Curves(const Vec<owe::Puppet::BoneFrameCurve>& curves) {
        Add(curves.len().to_primitive());
        for (const auto& c : curves) Each(c.values);
    }
    void Puppet(const owe::Puppet& puppet) {
        Add(puppet.bones.len().to_primitive());
        for (const auto& b : puppet.bones) {
            Text(b.name);
            Text(b.simulation_json);
            Add(b.sim_type, b.bind_parent, b.anim_parent, b.file_parent);
            Add(b.has_file_skin_pivot, b.has_file_world_bind);
            Matrix(b.local_bind);
            Matrix(b.world_bind);
            Matrix(b.file_world_bind);
            Bytes(b.file_skin_mat.data(), sizeof(float) * 16);
            for (float v : b.file_skin_pivot) Add(v);
            for (float v : b.vertex_centroid_offset) Add(v);
        }
        Add(puppet.attachments.len().to_primitive());
        for (const auto& a : puppet.attachments) {
            Add(a.bone_index);
            Text(a.name);
            Matrix(a.local_xform);
            Matrix(a.bind_xform);
        }
        Add(puppet.anims.len().to_primitive());
        for (const auto& anim : puppet.anims) {
            Add(anim.id, anim.unk_after_id, anim.fps, anim.length, anim.flags, anim.mode);
            Text(anim.name);
            Add(anim.bone_tracks.len().to_primitive(), anim.controller_tracks.len().to_primitive());
            for (const auto& t : anim.bone_tracks) {
                Add(t.bone_index, t.unk);
                Frames(t.frames);
            }
            for (const auto& t : anim.controller_tracks) {
                Add(t.bone_index);
                Frames(t.frames);
            }
            Add(anim.trans.is_some());
            if (anim.trans.is_some()) {
                Each((*anim.trans).extra_track);
                Each((*anim.trans).main_track);
                Add((*anim.trans).tail_tracks.len().to_primitive());
                for (const auto& t : (*anim.trans).tail_tracks) Each(t);
            }
            Curves(anim.blend_curves);
            Add(anim.v4_events.len().to_primitive());
            for (const auto& ev : anim.v4_events) {
                Add(ev.time, ev.flags, ev.curves.len().to_primitive());
                for (const auto& c : ev.curves) {
                    Add(c.id);
                    Each(c.values);
                }
            }
            Add(anim.aabb_min, anim.aabb_max, anim.has_aabb);
            Curves(anim.scalar_curves);
            Add(anim.events.len().to_primitive());
            for (const auto& ev : anim.events) {
                Add(ev.time_value);
                Text(ev.event_json);
            }
        }
        Add(puppet.ik_controllers.len().to_primitive(), puppet.ik_nodes.len().to_primitive());
        Add(puppet.ik_chains.len().to_primitive());
    }

    std::uint64_t m_hash { 1469598103934665603ull };
};

std::string JsonEscape(std::string_view text) {
    std::string out;
    for (char c : text) {
        if (c == '"' || c == '\\') out.push_back('\\');
        out.push_back(static_cast<unsigned char>(c) < 0x20 ? ' ' : c);
    }
    return out;
}

} // namespace

TEST(MdlParser, AcceptsKnownEmptyMdlsMetadata) {
    auto bytes = MdlPrefix();
    AppendEmptyMdls(bytes);
    owe::Services context;
    EXPECT_TRUE(ParseMdlBytes(bytes, context));
    EXPECT_FALSE(context.failed);
}

TEST(MdlParser, DistinguishesMissingFileFromParseFailure) {
    owe::fs::VFS vfs;
    owe::Mdl     mdl;
    bool         missing = false;
    EXPECT_FALSE(owe::MdlParser::Parse("missing-puppet.mdl"_str, vfs, mdl, nullptr, &missing));
    EXPECT_TRUE(missing);
}

TEST(MdlParser, RejectsTruncatedMdlsIkControllerTable) {
    auto bytes = MdlPrefix();
    AppendEmptyMdls(bytes, 8);
    owe::Services context;
    EXPECT_FALSE(ParseMdlBytes(bytes, context));
    EXPECT_TRUE(context.failed);
    EXPECT_TRUE(HasDiagnostic(context, "fixture.mdl"));
    EXPECT_TRUE(HasDiagnostic(context, "truncated MDLS IK controller table"));
}

TEST(MdlParser, ParsesSupportedIkRigAndControllerTracks) {
    auto                         bytes = MdlWithIkRig();
    owe::Services                context;
    owe::Mdl                     mdl;
    EXPECT_TRUE(ParseMdlBytes(bytes, context, &mdl));
    EXPECT_FALSE(context.failed);

    ASSERT_TRUE(mdl.puppet.is_some());
    const auto& puppet = **mdl.puppet;
    ASSERT_EQ(puppet.ik_controllers.len(), usize(2));
    EXPECT_EQ(puppet.ik_controllers[usize(0)].bone_index, 2u);
    EXPECT_EQ(puppet.ik_controllers[usize(0)].type, 1u);
    EXPECT_FLOAT_EQ(puppet.ik_controllers[usize(0)].bind_xform.translation().y(), 4.0f);
    EXPECT_EQ(puppet.ik_controllers[usize(1)].bone_index, 2u);
    EXPECT_EQ(puppet.ik_controllers[usize(1)].type, 0u);
    EXPECT_FLOAT_EQ(puppet.ik_controllers[usize(1)].bind_xform.translation().x(), 5.0f);

    ASSERT_EQ(puppet.ik_nodes.len(), usize(3));
    EXPECT_FLOAT_EQ(puppet.ik_nodes[usize(1)].length, 2.0f);
    EXPECT_FLOAT_EQ(puppet.ik_nodes[usize(2)].length, 3.0f);
    ASSERT_EQ(puppet.ik_nodes[usize(0)].children.len(), usize(1));
    EXPECT_EQ(puppet.ik_nodes[usize(0)].children[usize()].bone_index, 1u);
    ASSERT_EQ(puppet.ik_nodes[usize(1)].children.len(), usize(1));
    EXPECT_EQ(puppet.ik_nodes[usize(1)].children[usize()].bone_index, 2u);

    ASSERT_EQ(puppet.ik_chains.len(), usize(1));
    const auto& chain = puppet.ik_chains[usize()];
    EXPECT_EQ(chain.start_bone, 0u);
    EXPECT_EQ(chain.target_controller_index, 1u);
    EXPECT_EQ(chain.end_bone, 2u);
    EXPECT_FLOAT_EQ(chain.length, 5.0f);
    ASSERT_EQ(chain.bones.len(), usize(3));
    EXPECT_EQ(chain.bones[usize(0)], 0u);
    EXPECT_EQ(chain.bones[usize(1)], 1u);
    EXPECT_EQ(chain.bones[usize(2)], 2u);

    ASSERT_EQ(puppet.anims.len(), usize(1));
    const auto& animation = puppet.anims[usize()];
    EXPECT_TRUE(animation.trans.is_none());
    ASSERT_EQ(animation.controller_tracks.len(), usize(2));
    ASSERT_EQ(animation.controller_tracks[usize(0)].frames.len(), usize(2));
    EXPECT_FLOAT_EQ(animation.controller_tracks[usize(0)].frames[usize()].position.y(), 4.0f);
    ASSERT_EQ(animation.controller_tracks[usize(1)].frames.len(), usize(2));
    EXPECT_FLOAT_EQ(animation.controller_tracks[usize(1)].frames[usize()].position.x(), 5.0f);
}

TEST(MdlParser, RejectsNonFiniteIkBoneLength) {
    auto                         bytes = MdlWithIkRig(std::numeric_limits<float>::quiet_NaN());
    owe::Services                context;
    EXPECT_FALSE(ParseMdlBytes(bytes, context));
    EXPECT_TRUE(context.failed);
    EXPECT_TRUE(HasDiagnostic(context, "invalid MDLS IK bone length"));
}

TEST(MdlParser, RejectsInvalidUtf8MaterialName) {
    auto bytes = MdlPrefix(1);
    bytes.insert(bytes.end(), { 0xff, 0 });
    bytes.insert(bytes.end(), 12, 0); // 网格其余定长字段，让网格数先通过剩余字节核对
    owe::Services context;
    EXPECT_FALSE(ParseMdlBytes(bytes, context));
    EXPECT_TRUE(context.failed);
    EXPECT_TRUE(HasDiagnostic(context, "invalid UTF-8 in material name"));
}

TEST(MdlParser, BoundsBoneNameToMdlsBlock) {
    auto bytes     = MdlPrefix();
    auto end_field = BeginMdlBlock(bytes, "MDLS0004");
    AppendU16(bytes, 1);
    AppendU16(bytes, 0);
    bytes.insert(bytes.end(), 78, 'b'); // 一根骨骼的最小字节数，名字一直不结束
    FinishMdlBlock(bytes, end_field);
    bytes.push_back(0); // outside the declared MDLS block
    owe::Services context;
    EXPECT_FALSE(ParseMdlBytes(bytes, context));
    EXPECT_TRUE(context.failed);
    EXPECT_TRUE(HasDiagnostic(context, "unterminated bone name"));
    EXPECT_TRUE(HasDiagnostic(context, "offset=38, boundary=116"));
}

TEST(MdlParser, PropagatesInvalidUtf8PlayMode) {
    auto                         bytes = MdlWithPlayMode(std::string_view("\xff", 1), true);
    owe::Services                context;
    EXPECT_FALSE(ParseMdlBytes(bytes, context));
    EXPECT_TRUE(context.failed);
    EXPECT_TRUE(HasDiagnostic(context, "invalid UTF-8 in animation play_mode"));
}

TEST(MdlParser, BoundsPlayModeToMdlaBlock) {
    auto bytes =
        MdlWithPlayMode("loop_loop_loop_loop", false); // 名字 + play_mode 撑满一条动画的最小字节数
    owe::Services context;
    EXPECT_FALSE(ParseMdlBytes(bytes, context));
    EXPECT_TRUE(context.failed);
    EXPECT_TRUE(HasDiagnostic(context, "unterminated animation play_mode"));
}

// flags 0x401（语料里唯一出现的非零值）：读掉源动画引用尾块，下一条动画和事件表不跑偏。
// flags 0x2（未知位）：原样保留，不读尾块，不拒绝。
TEST(MdlParser, ReadsMdlaAnimationFlagsAndSourceReference) {
    for (std::uint32_t flags : { 0x401u, 0x2u }) {
        auto                         bytes = MdlaV6WithFirstFlags(flags);
        owe::Services                context;
        owe::Mdl                     mdl;
        EXPECT_TRUE(ParseMdlBytes(bytes, context, &mdl)) << flags;
        EXPECT_FALSE(context.failed) << flags;
        ASSERT_TRUE(mdl.puppet.is_some());
        const auto& anims = (**mdl.puppet).anims;
        ASSERT_EQ(anims.len(), usize(2)) << flags;
        EXPECT_EQ(anims[usize(0)].flags, flags);
        ASSERT_EQ(anims[usize(0)].events.len(), usize(1)) << flags;
        EXPECT_EQ(anims[usize(1)].id, 47) << flags;
        EXPECT_EQ(anims[usize(1)].name.as_str(), "clip"_str) << flags;
        EXPECT_EQ(anims[usize(1)].events.len(), usize(1)) << flags;
    }
}

TEST(MdlParser, RejectsUnsupportedPlayModeWithoutAssertion) {
    auto                         bytes = MdlWithPlayMode("unknown", true);
    owe::Services                context;
    EXPECT_FALSE(ParseMdlBytes(bytes, context));
    EXPECT_TRUE(context.failed);
    EXPECT_TRUE(HasDiagnostic(context, "unsupported animation play_mode"));
}

TEST(Puppet, ArcOwnedLayerExposesBorrowedTransforms) {
    auto              puppet = Arc<owe::Puppet>::make();
    owe::Puppet::Bone bone;
    bone.name = String::make("root"_str);
    puppet->bones.push(rstd::move(bone));
    puppet->prepared();

    owe::PuppetLayer layer(puppet.clone(), nullptr);
    layer.prepared(slice<owe::PuppetLayer::AnimationLayer> {});

    EXPECT_EQ(layer.boneIndex("root"_str), 1u);
    EXPECT_EQ(layer.boneIndex("missing"_str), 0u);
    EXPECT_TRUE(layer.boneTransform(0u, 0.0).is_none());
    auto transform = layer.boneTransform(1u, 0.0);
    ASSERT_TRUE(transform.is_some());
    EXPECT_TRUE(transform->matrix().isApprox(Eigen::Matrix4f::Identity()));
}

TEST(Puppet, SamplesTextureChannelBlendMapFromAnimationPlayback) {
    auto  puppet     = Arc<owe::Puppet>::make();
    auto& animation  = puppet->anims.emplace_back();
    animation.id     = 781;
    animation.name   = String::make("Arona Drool"_str);
    animation.mode   = owe::Puppet::PlayMode::Single;
    animation.fps    = 1.0f;
    animation.length = 2;
    auto& channels   = animation.trans.insert(owe::Puppet::AnimTrans {});
    channels.main_track.push(0.0f);
    channels.main_track.push(1.0f);
    channels.main_track.push(0.0f);
    auto& second = channels.tail_tracks.emplace_back();
    second.push(1.0f);
    second.push(0.5f);
    second.push(0.0f);
    puppet->prepared();

    owe::PuppetLayer                 layer(puppet.clone(), nullptr);
    owe::PuppetLayer::AnimationLayer authored {
        .id      = 781,
        .visible = true,
        .name    = String::make("Arona Drool"_str),
    };
    layer.prepared(slice<owe::PuppetLayer::AnimationLayer>::from_raw_parts(&authored, usize(1)));
    ASSERT_EQ(layer.AnimationPlaybacks().len(), usize(1));
    layer.AnimationPlaybacks()[usize()]->SetFrame(i32(1));
    layer.AnimationPlaybacks()[usize()]->Pause();

    const auto blend_map = layer.TextureChannelBlendMap(0.0);
    ASSERT_EQ(blend_map.len(), usize(4));
    EXPECT_FLOAT_EQ(blend_map[usize()], 1.0f);
    EXPECT_FLOAT_EQ(blend_map[usize(1)], 0.5f);
    EXPECT_FLOAT_EQ(blend_map[usize(2)], 0.0f);
    EXPECT_FLOAT_EQ(blend_map[usize(3)], 0.0f);
}

TEST(MdlMesh, KeepsPuppetPositionsInMdlLocalSpace) {
    owe::Mdl::Mesh source;
    source.positions.push(array<float, 3> { 244.0f, 349.5f, 0.0f });
    source.texcoords.push(array<float, 2> { 0.25f, 0.75f });
    source.indices.push(array<std::uint32_t, 3> { 0u, 0u, 0u });

    owe::SceneMesh::Submesh submesh;
    owe::MdlParser::GenMeshFromMdl(submesh, source);

    ASSERT_EQ(submesh.vertex_arrays.size(), 1u);
    const auto& vertices = submesh.vertex_arrays.front();
    ASSERT_NE(vertices.Data(), nullptr);
    EXPECT_FLOAT_EQ(vertices.Data()[0], 244.0f);
    EXPECT_FLOAT_EQ(vertices.Data()[1], 349.5f);
    EXPECT_FLOAT_EQ(vertices.Data()[2], 0.0f);
}

TEST(MdlMesh, FindsMeshByNormalizedMaterialReference) {
    owe::Mdl mdl;
    mdl.meshes.push(owe::Mdl::Mesh {});
    mdl.meshes.push(owe::Mdl::Mesh {});
    mdl.meshes[usize()].mat_json_files.push(String::make("materials/main.json"_str));
    mdl.meshes[usize(1)].mat_json_files.push(String::make("overlay"_str));

    auto main = owe::MdlParser::FindMeshByMaterial(mdl, "materials/main"_str);
    ASSERT_TRUE(main.is_some());
    EXPECT_EQ(*main, usize());

    auto overlay = owe::MdlParser::FindMeshByMaterial(mdl, "materials/overlay.json"_str);
    ASSERT_TRUE(overlay.is_some());
    EXPECT_EQ(*overlay, usize(1));
    EXPECT_TRUE(owe::MdlParser::FindMeshByMaterial(mdl, "missing"_str).is_none());
}

TEST(MdlMesh, Mdlv23LargeStaticMeshUsesUint32GlobalIndices) {
    const std::filesystem::path pkg_path =
        std::filesystem::path(WAYWALLEN_WORKSHOP_DIR) / "3557068717" / "scene.pkg";
    if (! std::filesystem::exists(pkg_path)) {
        GTEST_SKIP() << "workshop 3557068717 is not available";
    }

    owe::fs::VFS vfs;
    auto         assets_fs = owe::fs::make_physical_fs(owe::fs::ToPath(WAYWALLEN_ASSETS_DIR));
    if (assets_fs.is_ok()) {
        ASSERT_TRUE(vfs.mount("/assets"_str, std::move(assets_fs).unwrap_unchecked()).is_ok());
    }
    auto pkg_fs = owe::fs::WPPkgFs::open(owe::fs::ToPath(pkg_path.string()));
    ASSERT_TRUE(pkg_fs.is_ok());
    ASSERT_TRUE(vfs.mount("/assets"_str, pkg_fs->mount_handle()).is_ok());

    owe::Mdl mdl;
    ASSERT_TRUE(owe::MdlParser::Parse("models/球体01/球体01.mdl"_str, vfs, mdl, nullptr));
    ASSERT_FALSE(mdl.meshes.is_empty());

    const auto& mesh = mdl.meshes[usize()];
    ASSERT_EQ(mdl.header.mdlv, 23);
    ASSERT_EQ(mesh.positions.len(), usize(520192));
    ASSERT_EQ(mesh.texcoords.len(), mesh.positions.len());
    ASSERT_EQ(mesh.indices.len(), usize(260096));
    ASSERT_LT(MaxMeshIndex(mesh), mesh.positions.len().to_primitive());
    EXPECT_EQ(mesh.indices[usize(0)], (array<std::uint32_t, 3> { 0u, 1u, 2u }));
    EXPECT_EQ(mesh.indices[usize(1)], (array<std::uint32_t, 3> { 0u, 2u, 3u }));
    EXPECT_EQ(mesh.indices[mesh.indices.len() - usize(1)],
              (array<std::uint32_t, 3> { 520188u, 520190u, 520191u }));
    EXPECT_EQ(CountUvSeamTriangles(mesh), 0u);

    owe::SceneMesh::Submesh submesh;
    owe::MdlParser::GenMeshFromMdl(submesh, mesh);
    ASSERT_EQ(submesh.vertex_arrays.size(), 1u);
    ASSERT_EQ(submesh.index_arrays.size(), 1u);
    EXPECT_TRUE(submesh.draw_ranges.empty());

    const auto& index_array = submesh.index_arrays.front();
    ASSERT_EQ(index_array.DataCount(), rstd::usize(780288));
    EXPECT_EQ(index_array.Data()[0], 0u);
    EXPECT_EQ(index_array.Data()[1], 1u);
    EXPECT_EQ(index_array.Data()[2], 2u);
    EXPECT_EQ(index_array.Data()[780285], 520188u);
    EXPECT_EQ(index_array.Data()[780286], 520190u);
    EXPECT_EQ(index_array.Data()[780287], 520191u);
}

TEST(MdlMesh, Mdlv23ReadsPerMeshMaterialSkins) {
    const std::filesystem::path pkg_path =
        std::filesystem::path(WAYWALLEN_WORKSHOP_DIR) / "1979606285" / "scene.pkg";
    if (! std::filesystem::exists(pkg_path)) {
        GTEST_SKIP() << "workshop 1979606285 is not available";
    }

    owe::fs::VFS vfs;
    auto         assets_fs = owe::fs::make_physical_fs(owe::fs::ToPath(WAYWALLEN_ASSETS_DIR));
    if (assets_fs.is_ok()) {
        ASSERT_TRUE(vfs.mount("/assets"_str, std::move(assets_fs).unwrap_unchecked()).is_ok());
    }
    auto pkg_fs = owe::fs::WPPkgFs::open(owe::fs::ToPath(pkg_path.string()));
    ASSERT_TRUE(pkg_fs.is_ok());
    ASSERT_TRUE(vfs.mount("/assets"_str, pkg_fs->mount_handle()).is_ok());

    owe::Mdl mdl;
    ASSERT_TRUE(owe::MdlParser::Parse("models/prism/prism.mdl"_str, vfs, mdl, nullptr));
    ASSERT_EQ(mdl.header.mdlv, 23);
    ASSERT_EQ(mdl.header.skin_count, 2u);
    ASSERT_EQ(mdl.header.mesh_count, 1u);
    ASSERT_EQ(mdl.meshes.len(), usize(1));

    const auto& mesh = mdl.meshes[usize()];
    ASSERT_EQ(mesh.mat_json_files.len(), usize(2));
    EXPECT_EQ(mesh.mat_json_files[usize()].as_str(), "materials/prism/prism.json"_str);
    EXPECT_EQ(mesh.mat_json_files[usize(1)].as_str(), "materials/prism/prism_main.json"_str);
    EXPECT_EQ(mesh.positions.len(), usize(60));
    EXPECT_EQ(mesh.indices.len(), usize(32));
}

TEST(MdlPuppet, Mdlv23ReadsMultiCurveMorphEvents) {
    const std::filesystem::path pkg_path =
        std::filesystem::path(WAYWALLEN_WORKSHOP_DIR) / "3686252018" / "scene.pkg";
    if (! std::filesystem::exists(pkg_path)) {
        GTEST_SKIP() << "workshop 3686252018 is not available";
    }

    owe::fs::VFS vfs;
    auto         assets_fs = owe::fs::make_physical_fs(owe::fs::ToPath(WAYWALLEN_ASSETS_DIR));
    if (assets_fs.is_ok()) {
        ASSERT_TRUE(vfs.mount("/assets"_str, std::move(assets_fs).unwrap_unchecked()).is_ok());
    }
    auto pkg_fs = owe::fs::WPPkgFs::open(owe::fs::ToPath(pkg_path.string()));
    ASSERT_TRUE(pkg_fs.is_ok());
    ASSERT_TRUE(vfs.mount("/assets"_str, pkg_fs->mount_handle()).is_ok());

    owe::Mdl mdl;
    ASSERT_TRUE(owe::MdlParser::Parse("models/sheet_puppet.mdl"_str, vfs, mdl, nullptr));
    ASSERT_EQ(mdl.mdla, 6);
    ASSERT_TRUE(mdl.puppet.is_some());

    const auto& anims = (*mdl.puppet)->anims;
    ASSERT_EQ(anims.len(), usize(17));
    const auto& left_eye = anims[usize()];
    ASSERT_EQ(left_eye.name.as_str(), "Left eye"_str);
    ASSERT_EQ(left_eye.v4_events.len(), usize(1));

    const auto& event = left_eye.v4_events[usize()];
    EXPECT_EQ(event.flags, 0);
    ASSERT_EQ(event.curves.len(), usize(6));
    for (usize i {}; i < event.curves.len(); ++i) {
        const auto& curve = event.curves[i];
        EXPECT_EQ(curve.id, i.to_primitive());
        ASSERT_EQ(curve.values.len(), usize(211));
        EXPECT_FLOAT_EQ(curve.values[usize()], 1.0f);
    }

    ASSERT_EQ(mdl.morph_sections.len(), usize(1));
    EXPECT_FLOAT_EQ(mdl.morph_sections[usize()].event_time, event.time);
    EXPECT_EQ(mdl.morph_sections[usize()].sections.len(), event.curves.len());
}

TEST(MdlParser, ParsesFixtureWithEveryBlock) {
    owe::Services context;
    owe::Mdl      mdl;
    ASSERT_TRUE(ParseMdlBytes(MdlWithAllBlocks(), context, &mdl));
    EXPECT_FALSE(context.failed);
    ASSERT_EQ(mdl.meshes.len(), usize(1));
    const auto& mesh = mdl.meshes[usize()];
    EXPECT_EQ(mesh.positions.len(), usize(3));
    EXPECT_EQ(mesh.indices.len(), usize(1));
    EXPECT_EQ(mesh.parts.len(), usize(1));
    ASSERT_EQ(mesh.masks.len(), usize(1));
    EXPECT_EQ(mesh.masks[usize()].part_ids_b.len(), usize(1));
    ASSERT_TRUE(mdl.puppet.is_some());
    const auto& puppet = **mdl.puppet;
    EXPECT_EQ(puppet.bones.len(), usize(1));
    EXPECT_EQ(puppet.attachments.len(), usize(1));
    ASSERT_EQ(puppet.anims.len(), usize(1));
    const auto& anim = puppet.anims[usize()];
    EXPECT_EQ(anim.bone_tracks.len(), usize(1));
    EXPECT_TRUE(anim.trans.is_some());
    EXPECT_EQ(anim.blend_curves.len(), usize(1));
    ASSERT_EQ(anim.v4_events.len(), usize(1));
    EXPECT_EQ(anim.v4_events[usize()].curves.len(), usize(2));
    EXPECT_EQ(anim.scalar_curves.len(), usize(1));
    EXPECT_EQ(anim.events.len(), usize(1));
    ASSERT_EQ(mdl.morph_sections.len(), usize(1));
    EXPECT_EQ(mdl.morph_sections[usize()].sections.len(), usize(1));
    EXPECT_EQ(mdl.mdle, 1);
    EXPECT_TRUE(puppet.bones[usize()].has_file_world_bind);
}

// 错位读出的巨大计数（这里是事件表条数）在分配之前按块内剩余字节拒绝，报解析失败，诊断里带字段名和偏移。
// 修复前这里按计数直接 reserve，分配失败时 rstd panic，进程以 0xC0000409 退出。
TEST(MdlParser, RejectsRecordCountBeyondBlockWithFieldAndOffset) {
    std::size_t events_offset = 0;
    {
        owe::Services context;
        EXPECT_TRUE(ParseMdlBytes(MdlaV1WithEventCount(1, &events_offset), context));
        EXPECT_FALSE(context.failed);
    }
    owe::Services context;
    EXPECT_FALSE(ParseMdlBytes(MdlaV1WithEventCount(0xFFFFFFFFu, &events_offset), context));
    EXPECT_TRUE(context.failed);
    EXPECT_TRUE(HasDiagnostic(
        context, "truncated animation events (offset=" + std::to_string(events_offset) + ","));
}

// 三个夹具各截断到每一个长度，每个字节各做两种位翻转（最高位把计数变成巨大值，低位让读法错位）：
// 解析器必须正常返回（成功或解析失败），不许 panic 或分配失败崩溃。
TEST(MdlParser, SurvivesTruncationAndBitFlips) {
    const auto dir = std::filesystem::temp_directory_path() /
                     ("owe-mdl-mutants-" + std::to_string(rstd::process::id().to_primitive()));
    std::filesystem::create_directories(dir);
    const auto file = dir / "fixture.mdl";
    {
        MdlMount mount(dir);
        ASSERT_TRUE(mount.ok());
        const std::vector<std::uint8_t> fixtures[] = { MdlWithAllBlocks(),
                                                       MdlWithIkRig(),
                                                       MdlaV6WithFirstFlags(0x401) };
        for (const auto& original : fixtures) {
            std::size_t cases = 0, accepted = 0;
            auto        parse = [&](const std::vector<std::uint8_t>& bytes) {
                WriteBytes(file, bytes);
                owe::Services context;
                owe::Mdl      mdl;
                ++cases;
                accepted += mount.Parse("fixture.mdl", context, mdl);
            };
            for (std::size_t size = 0; size < original.size(); ++size)
                parse({ original.begin(), original.begin() + static_cast<std::ptrdiff_t>(size) });
            for (std::size_t i = 0; i < original.size(); ++i) {
                for (unsigned mask : { 0x80u, 1u << (i % 7) }) {
                    auto bytes = original;
                    bytes[i] ^= static_cast<std::uint8_t>(mask);
                    parse(bytes);
                }
            }
            EXPECT_EQ(cases, original.size() * 3);
            EXPECT_LT(accepted, cases);
        }
    }
    std::filesystem::remove(file);
    std::filesystem::remove(dir);
}

// 模糊测试与全语料对照的驱动，由 runs/fix-mdl-robust/fuzz_mdl.py 调用；不设变量时跳过。
// OWE_MDL_FUZZ_DIR 下的文件按 OWE_MDL_FUZZ_LIST（每行一个文件名）从第 OWE_MDL_FUZZ_START
// 行起逐个解析， 每个文件解析完追加一行 JSON 到 OWE_MDL_FUZZ_OUT
// 并刷新；进程若崩溃，脚本按已写行数定位崩溃的文件。
TEST(MdlParserFuzz, ParsesListedFiles) {
    const char* dir  = std::getenv("OWE_MDL_FUZZ_DIR");
    const char* list = std::getenv("OWE_MDL_FUZZ_LIST");
    const char* out  = std::getenv("OWE_MDL_FUZZ_OUT");
    if (dir == nullptr || list == nullptr || out == nullptr)
        GTEST_SKIP() << "OWE_MDL_FUZZ_DIR/LIST/OUT 未设置";
    const char*       start_text = std::getenv("OWE_MDL_FUZZ_START");
    const std::size_t start      = start_text ? std::strtoull(start_text, nullptr, 10) : 0;

    MdlMount mount(dir);
    ASSERT_TRUE(mount.ok());
    std::ifstream names(list);
    std::ofstream results(out, std::ios::app);
    std::string   name;
    for (std::size_t index = 0; std::getline(names, name); ++index) {
        if (index < start || name.empty()) continue;
        owe::Services context;
        bool          parsed = false;
        std::string   digest;
        const auto    begin = std::chrono::steady_clock::now();
        {
            // 结果在 Mdl 析构之后再写：析构也算在这个文件头上，卡在析构里时脚本不会记到下一个文件。
            owe::Mdl mdl;
            parsed = mount.Parse(name, context, mdl);
            if (parsed) digest = MdlDigest().Of(mdl);
        }
        const auto ms =
            std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - begin)
                .count();
        results << "{\"index\":" << index << ",\"file\":\"" << JsonEscape(name)
                << "\",\"ok\":" << (parsed ? "true" : "false") << ",\"digest\":\"" << digest
                << "\",\"ms\":" << ms << ",\"diag\":\""
                << JsonEscape(context.diagnostics.empty() ? std::string_view()
                                                          : context.diagnostics.front())
                << "\"}" << std::endl;
    }
}
