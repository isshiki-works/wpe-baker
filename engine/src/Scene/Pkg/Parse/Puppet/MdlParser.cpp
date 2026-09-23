module;
#include <cmath>
#include <rstd/macro.hpp>

module wescene.pkg.parse;
import wescene.pkg.spec_names;
import wescene.core;
import wescene.types;
import rstd.log;
import rstd.cppstd;
import wescene.scene;
import wescene.pkg_asset_version;

using namespace owe;
using namespace rstd::prelude;
using namespace rstd::literals;
using rstd::sync::Arc;

namespace
{

bool ParseFailure(std::string_view path, Services* services, std::string reason,
                  rstd::ptrdiff_t offset, rstd::ptrdiff_t boundary) {
    std::string message = "MDL parse failed: " + std::string(path) + ": " + reason +
                          " (offset=" + std::to_string(offset) +
                          ", boundary=" + std::to_string(boundary) + ")";
    rstd_error("{}", message);
    if (services) services->diagnose(message, true);
    return false;
}

bool ReadOwnedString(fs::BinaryReader& reader, String& out, std::string_view path,
                     Services* services, std::string_view field, uint32_t end_offset = 0) {
    const auto start    = reader.Tell();
    const auto boundary = end_offset ? static_cast<rstd::ptrdiff_t>(end_offset) : reader.Size();
    if (start < 0 || boundary < start || boundary > reader.Size())
        return ParseFailure(
            path, services, "invalid boundary for " + std::string(field), start, boundary);
    std::string value;
    while (reader.Tell() < boundary) {
        char current = 0;
        if (reader.Read(&current, 1) != 1)
            return ParseFailure(path, services, "truncated " + std::string(field), start, boundary);
        if (current == '\0') {
            auto text = rstd::cppstd::as_str(value);
            if (text.is_err())
                return ParseFailure(
                    path, services, "invalid UTF-8 in " + std::string(field), start, boundary);
            out = String::make(rstd::move(text).unwrap_unchecked());
            return true;
        }
        value.push_back(current);
    }
    return ParseFailure(path, services, "unterminated " + std::string(field), start, boundary);
}

bool CheckBlockEnd(fs::BinaryReader& reader, uint32_t end_offset, std::string_view path,
                   Services* services, std::string_view block) {
    if (end_offset && (static_cast<rstd::ptrdiff_t>(end_offset) < reader.Tell() ||
                       static_cast<rstd::ptrdiff_t>(end_offset) > reader.Size()))
        return ParseFailure(path,
                            services,
                            "invalid " + std::string(block) + " end_offset",
                            reader.Tell(),
                            static_cast<rstd::ptrdiff_t>(end_offset));
    return true;
}

// end_offset 为 0 表示没有块边界（网格段在所有块之前），以文件末尾为界，与 ReadOwnedString 一致。
bool RequireBytes(fs::BinaryReader& reader, uint64_t byte_count, uint32_t end_offset,
                  std::string_view path, Services* services, std::string_view field) {
    const auto start    = reader.Tell();
    const auto boundary = end_offset ? static_cast<rstd::ptrdiff_t>(end_offset) : reader.Size();
    if (start < 0 || boundary < start || byte_count > static_cast<uint64_t>(boundary - start))
        return ParseFailure(path, services, "truncated " + std::string(field), start, boundary);
    return true;
}

// 文件里读出的计数只能经这里变成容器长度：先按块内剩余字节核对 count 条记录（每条至少
// record_bytes 字节）装得下，装不下就是截断或读错位，返回解析失败（带偏移与字段名），不分配。
// 以前直接按计数 reserve，错位读出的巨大计数会让分配失败、进程以 0xC0000409 崩溃。
template<typename T>
bool ResizeCounted(fs::BinaryReader& reader, Vec<T>& values, uint64_t count, uint64_t record_bytes,
                   uint32_t end_offset, std::string_view path, Services* services,
                   std::string_view field) {
    if (! RequireBytes(reader, count * record_bytes, end_offset, path, services, field))
        return false;
    values.clear();
    values.reserve(usize(count));
    for (uint64_t i {}; i < count; ++i) values.emplace_back();
    return true;
}

// MDAT/MDLA/MDMP/MDLE 标签 "MDxx0006" 里的版本号。不以数字开头就是读错位，按解析失败返回
// （以前用 std::stoi，非数字会抛异常直接终止进程）。
bool ParseTagVersion(std::string_view tag, int32_t& version, rstd::ptrdiff_t offset,
                     std::string_view path, Services* services) {
    const auto digits = tag.substr(4, 4);
    if (std::from_chars(digits.data(), digits.data() + digits.size(), version).ec != std::errc())
        return ParseFailure(path, services, "invalid block version tag", offset, offset);
    return true;
}

// MDAT/MDLA/MDLE 挂在 MDLS 建的 puppet 上；没有 MDLS 就出现这些块是读错位，按解析失败返回。
bool RequirePuppet(const Mdl& mdl, std::string_view block, rstd::ptrdiff_t offset,
                   std::string_view path, Services* services) {
    if (mdl.puppet.is_some()) return true;
    return ParseFailure(path, services, std::string(block) + " block without MDLS", offset, offset);
}

// Vertex layout bits (mirror imhex/mdl.hexpat MdlFlagBit).
constexpr uint32_t MDL_FLAG_NORMAL      = 0x00000002;
constexpr uint32_t MDL_FLAG_TANGENT     = 0x00000004;
constexpr uint32_t MDL_FLAG_UV          = 0x00000008;
constexpr uint32_t MDL_FLAG_UV2         = 0x00000020;
constexpr uint32_t MDL_FLAG_EXTRA4      = 0x00010000;
constexpr uint32_t MDL_FLAG_SKIN_BLEND  = 0x00800000;
constexpr uint32_t MDL_FLAG_SKIN_WEIGHT = 0x01000000;

constexpr uint32_t singile_indices_u16          = 2 * 3;
constexpr uint32_t singile_indices_u32          = 4 * 3;
constexpr uint32_t singile_bone_frame           = 4 * 9;
constexpr uint32_t mdls_offset_trans_entry_size = (3 + 16) * 4;

// Compute per-vertex byte stride from a layout flag bitset. Position is
// always emitted (12 bytes), other attributes are gated by their bits.
// UV2 implies a regular UV slot in addition to the UV2 slot.
uint32_t compute_vertex_stride(uint32_t flag) {
    uint32_t s = 12;
    if (flag & MDL_FLAG_NORMAL) s += 12;
    if (flag & MDL_FLAG_TANGENT) s += 16;
    if (flag & MDL_FLAG_EXTRA4) s += 4;
    if (flag & MDL_FLAG_SKIN_BLEND) s += 16;
    if (flag & MDL_FLAG_SKIN_WEIGHT) s += 16;
    if (flag & (MDL_FLAG_UV | MDL_FLAG_UV2)) s += 8;
    if (flag & MDL_FLAG_UV2) s += 8;
    return s;
}

// Peek the next 4 bytes; restore cursor before returning. Used to detect
// optional MDLS/MDAT/MDLA/MDMP/MDLE block headers without consuming them.
bool peek_block_magic(fs::BinaryReader& f, std::string_view expect4) {
    if (expect4.size() != 4) return false;
    auto save = f.Tell();
    if (save + 4 > f.Size()) return false;
    char buf[4] = { 0 };
    f.Read(buf, 4);
    bool ok = (std::memcmp(buf, expect4.data(), 4) == 0);
    f.SeekSet(save);
    return ok;
}

bool peek_uint8_at(fs::BinaryReader& f, rstd::ptrdiff_t off, uint8_t& out) {
    if (off < 0 || off + 1 > f.Size()) return false;
    auto save = f.Tell();
    f.SeekSet(off);
    out = f.ReadUint8();
    f.SeekSet(save);
    return true;
}

bool peek_uint32_at(fs::BinaryReader& f, rstd::ptrdiff_t off, uint32_t& out) {
    if (off < 0 || off + 4 > f.Size()) return false;
    auto save = f.Tell();
    f.SeekSet(off);
    out = f.ReadUint32();
    f.SeekSet(save);
    return true;
}

bool is_anim_trans_main_size(uint32_t byte_size, int32_t length) {
    if (length < 0 || byte_size == 0 || byte_size % 4 != 0) return false;
    auto samples = static_cast<uint64_t>(length) + 1;
    return byte_size == samples * singile_bone_frame || byte_size == samples * 4;
}

bool is_controller_track_size(uint32_t byte_size, int32_t length) {
    return length >= 0 && byte_size == (static_cast<uint64_t>(length) + 1) * singile_bone_frame;
}

bool next_is_anim_trans_main(fs::BinaryReader& f, int32_t length) {
    uint32_t byte_size = 0;
    return peek_uint32_at(f, f.Tell(), byte_size) && is_anim_trans_main_size(byte_size, length);
}

bool next_after_zero_is_anim_trans_main(fs::BinaryReader& f, int32_t length) {
    auto     off  = f.Tell();
    uint32_t zero = 0;
    if (! peek_uint32_at(f, off, zero) || zero != 0) return false;
    uint32_t byte_size = 0;
    return peek_uint32_at(f, off + 4, byte_size) && is_anim_trans_main_size(byte_size, length);
}

// MDLA 动画头里 length 之后的 u32 flags。语料（D:/WPE-regress-src 342 个 mdl、505 条动画）
// 只出现过 0 和 0x401。0x400 决定事件表前有没有源动画引用尾块；0x001 只与 0x400 同时出现，
// 含义未知。其余位和其余组合照原样保留在 anim.flags 里，打一条 info 日志，不改变读法。
constexpr uint32_t kAnimFlagSourceRef = 0x400;
constexpr uint32_t kAnimFlagsKnown    = 0x401;

bool next_is_anim_bone_curves(fs::BinaryReader& f) {
    auto    off        = f.Tell();
    uint8_t has_curves = 0;
    if (! peek_uint8_at(f, off, has_curves)) return false;
    if (! has_curves) return true;

    uint32_t zero_a = 0;
    if (! peek_uint32_at(f, off + 1, zero_a) || zero_a != 0) return false;
    uint32_t byte_size = 0;
    return peek_uint32_at(f, off + 5, byte_size) && byte_size % 4 == 0;
}

bool next_is_anim_record_padding(fs::BinaryReader& f, uint32_t end_offset) {
    auto off = f.Tell();
    if (end_offset == 0 || off + 12 > static_cast<rstd::ptrdiff_t>(end_offset)) return false;
    uint32_t zero = 0;
    if (! peek_uint32_at(f, off, zero) || zero != 0) return false;
    uint32_t next_id = 0;
    if (! peek_uint32_at(f, off + 4, next_id) || next_id == 0 || next_id > 100000) {
        return false;
    }
    uint32_t next_unk_after_id = 0;
    return peek_uint32_at(f, off + 8, next_unk_after_id) && next_unk_after_id == 0;
}

rstd::ptrdiff_t mdls_v2_indexed_trailer_start(uint32_t end_offset, uint16_t bones_num) {
    auto trailer_size = 1ull + static_cast<uint64_t>(bones_num) * mdls_offset_trans_entry_size +
                        1ull + static_cast<uint64_t>(bones_num) * 4ull;
    if (end_offset < trailer_size) return -1;
    return static_cast<rstd::ptrdiff_t>(end_offset - trailer_size);
}

bool is_mdls_v2_indexed_trailer(fs::BinaryReader& f, rstd::ptrdiff_t start, uint32_t end_offset,
                                uint16_t bones_num) {
    if (start < 0 || start >= static_cast<rstd::ptrdiff_t>(end_offset)) return false;
    uint8_t has_offset_trans = 0;
    if (! peek_uint8_at(f, start, has_offset_trans) || has_offset_trans != 1) return false;

    auto has_index_off = start + 1 +
                         static_cast<rstd::ptrdiff_t>(bones_num) *
                             static_cast<rstd::ptrdiff_t>(mdls_offset_trans_entry_size);
    if (has_index_off >= static_cast<rstd::ptrdiff_t>(end_offset)) return false;
    uint8_t has_index = 0;
    return peek_uint8_at(f, has_index_off, has_index) && has_index == 1;
}

bool ParseMasks(fs::BinaryReader& f, Mdl::Mesh& mesh, std::string_view path, Services* services);

bool UsesUint32Indices(const MdlHeader& header, uint32_t vertex_num) {
    return header.mdlv >= 23 && vertex_num > std::numeric_limits<uint16_t>::max();
}

// hexpat Mesh<MdlV, TopFlag, SinglePuppet, SkinCount>:
//   CStr mat_json[SkinCount] + u32 flag_a + (if flag_a==2: u32) + (if MdlV>=17: aabb)
//   + (if MdlV>14: u32 mesh_flag) + u32 vertex_size + Vertex[]
//   + u32 indices_size + Triangle[] + (if MdlV>=21: Parts) + (if MdlV>21: Masks)
bool ParseMesh(fs::BinaryReader& f, const MdlHeader& header, Mdl::Mesh& mesh, std::string_view path,
               Services* services) {
    if (! ResizeCounted(
            f, mesh.mat_json_files, header.skin_count, 1, 0, path, services, "mesh material names"))
        return false;
    for (auto& material : mesh.mat_json_files)
        if (! ReadOwnedString(f, material, path, services, "material name")) return false;
    mesh.flag_a = f.ReadUint32();
    if (mesh.flag_a == 2) {
        mesh.has_flag_a2_one = (f.ReadUint32() == 1);
    }

    if (header.mdlv >= 17) {
        for (auto& v : mesh.aabb_min) v = f.ReadFloat();
        for (auto& v : mesh.aabb_max) v = f.ReadFloat();
        mesh.has_aabb = true;
    }

    uint32_t mesh_flag = (header.mdlv > 14) ? f.ReadUint32() : header.mdl_flag;
    mesh.flag          = mesh_flag;

    uint32_t vertex_size = f.ReadUint32();
    uint32_t stride      = compute_vertex_stride(mesh_flag);
    if (stride == 0 || vertex_size % stride != 0) {
        rstd_error("unsupport mdl vertex size {} (flag=0x{:X} stride={}) in {}",
                   vertex_size,
                   mesh_flag,
                   stride,
                   std::string(path));
        return false;
    }

    uint32_t vertex_num = vertex_size / stride;
    auto     attribute  = [&](auto& values, bool present) {
        return ! present ||
               ResizeCounted(f, values, vertex_num, stride, 0, path, services, "mesh vertices");
    };
    if (! attribute(mesh.positions, true) ||
        ! attribute(mesh.normals, mesh_flag & MDL_FLAG_NORMAL) ||
        ! attribute(mesh.tangents, mesh_flag & MDL_FLAG_TANGENT) ||
        ! attribute(mesh.extra4, mesh_flag & MDL_FLAG_EXTRA4) ||
        ! attribute(mesh.blend_indices, mesh_flag & MDL_FLAG_SKIN_BLEND) ||
        ! attribute(mesh.blend_weights, mesh_flag & MDL_FLAG_SKIN_WEIGHT) ||
        ! attribute(mesh.texcoords, mesh_flag & (MDL_FLAG_UV | MDL_FLAG_UV2)) ||
        ! attribute(mesh.texcoord2, mesh_flag & MDL_FLAG_UV2))
        return false;

    for (uint32_t i = 0; i < vertex_num; ++i) {
        const usize index(i);
        for (auto& v : mesh.positions[index]) v = f.ReadFloat();
        if (mesh_flag & MDL_FLAG_NORMAL) {
            for (auto& v : mesh.normals[index]) v = f.ReadFloat();
        }
        if (mesh_flag & MDL_FLAG_TANGENT) {
            for (auto& v : mesh.tangents[index]) v = f.ReadFloat();
        }
        if (mesh_flag & MDL_FLAG_EXTRA4) {
            for (auto& v : mesh.extra4[index]) v = f.ReadUint8();
        }
        if (mesh_flag & MDL_FLAG_SKIN_BLEND) {
            for (auto& v : mesh.blend_indices[index]) v = f.ReadUint32();
        }
        if (mesh_flag & MDL_FLAG_SKIN_WEIGHT) {
            for (auto& v : mesh.blend_weights[index]) v = f.ReadFloat();
        }
        if (mesh_flag & (MDL_FLAG_UV | MDL_FLAG_UV2)) {
            for (auto& v : mesh.texcoords[index]) v = f.ReadFloat();
        }
        if (mesh_flag & MDL_FLAG_UV2) {
            for (auto& v : mesh.texcoord2[index]) v = f.ReadFloat();
        }
    }

    uint32_t       indices_size    = f.ReadUint32();
    const bool     use_u32_indices = UsesUint32Indices(header, vertex_num);
    const uint32_t index_stride    = use_u32_indices ? singile_indices_u32 : singile_indices_u16;
    if (indices_size % index_stride != 0) {
        rstd_error("unsupport mdl indices size {} (stride={}) in {}",
                   indices_size,
                   index_stride,
                   std::string(path));
        return false;
    }
    uint32_t indices_num = indices_size / index_stride;
    if (! ResizeCounted(
            f, mesh.indices, indices_num, index_stride, 0, path, services, "mesh indices"))
        return false;
    for (auto& id : mesh.indices) {
        for (auto& v : id) v = use_u32_indices ? f.ReadUint32() : f.ReadUint16();
    }

    // V21+ Parts sub-block (hexpat Parts<MdlV>): optional uv2 region followed
    // by an optional part draw-range list.
    if (header.mdlv >= 21) {
        uint8_t unk_a = f.ReadUint8();
        if (unk_a == 1) {
            uint8_t unk_b = f.ReadUint8();
            if (unk_b) {
                uint16_t unk_c = f.ReadUint16();
                if (unk_c != 0) {
                    rstd_info("mdlv{} parts unk_c expected 0, got {}", header.mdlv, unk_c);
                }
                (void)f.ReadUint8(); // vert_section_marker
                uint32_t payload_size = f.ReadUint32();
                if (payload_size != 12u * vertex_num) {
                    rstd_error("mdlv{} extras payload size {} != 12*{}",
                               header.mdlv,
                               payload_size,
                               vertex_num);
                    return false;
                }
                if (! ResizeCounted(
                        f, mesh.part_uv2, vertex_num, 12, 0, path, services, "mesh part uv2"))
                    return false;
                mesh.part_uv2_pad.clear();
                for (auto& uv : mesh.part_uv2) {
                    uv[usize(0)] = f.ReadFloat();
                    uv[usize(1)] = f.ReadFloat();
                    mesh.part_uv2_pad.push(f.ReadUint32());
                }
            }
        } else if (unk_a != 0) {
            rstd_error("mdlv{} parts unhandled unk_a={}", header.mdlv, unk_a);
            return false;
        }
        uint8_t has_parts = f.ReadUint8();
        if (has_parts) {
            uint32_t parts_bytes = f.ReadUint32();
            if (parts_bytes % 16 != 0) {
                rstd_error("mdlv{} parts byte count {} not %% 16", header.mdlv, parts_bytes);
                return false;
            }
            uint32_t parts_num = parts_bytes / 16;
            if (! ResizeCounted(f, mesh.parts, parts_num, 16, 0, path, services, "mesh parts"))
                return false;
            for (auto& part : mesh.parts) {
                part.id = f.ReadUint32();
                (void)f.ReadUint32(); // reserved 0
                part.start = f.ReadUint32();
                part.size  = f.ReadUint32();
            }
        }
        if (header.mdlv > 21) {
            if (! ParseMasks(f, mesh, path, services)) return false;
        }
    }
    return true;
}

bool ParseIkRig(fs::BinaryReader& f, Mdl& mdl, uint16_t controller_count, uint16_t bones_num,
                uint32_t end_offset, std::string_view path, Services* services) {
    auto& puppet = **mdl.puppet;

    constexpr uint64_t controller_size = 1 + 4 + 4 + 16 * 4;
    if (! ResizeCounted(f,
                        puppet.ik_controllers,
                        controller_count,
                        controller_size,
                        end_offset,
                        path,
                        services,
                        "MDLS IK controller table"))
        return false;
    for (auto& controller : puppet.ik_controllers) {
        const auto record_offset = f.Tell();
        uint8_t    reserved      = f.ReadUint8();
        controller.bone_index    = f.ReadUint32();
        controller.type          = f.ReadUint32();
        controller.bind_xform    = Eigen::Affine3f::Identity();
        for (auto col : controller.bind_xform.matrix().colwise()) {
            for (auto& value : col) {
                value = f.ReadFloat();
                if (! std::isfinite(value))
                    return ParseFailure(path,
                                        services,
                                        "non-finite MDLS IK controller transform",
                                        record_offset,
                                        end_offset);
            }
        }
        if (reserved != 0 || controller.bone_index >= bones_num || controller.type > 1)
            return ParseFailure(
                path, services, "unsupported MDLS IK controller record", record_offset, end_offset);
        const auto& matrix = controller.bind_xform.matrix();
        if (matrix(3, 0) != 0.0f || matrix(3, 1) != 0.0f || matrix(3, 2) != 0.0f ||
            matrix(3, 3) != 1.0f)
            return ParseFailure(path,
                                services,
                                "non-affine MDLS IK controller transform",
                                record_offset,
                                end_offset);
    }

    if (! RequireBytes(f, 7, end_offset, path, services, "MDLS IK graph header")) return false;
    uint8_t  reserved_a   = f.ReadUint8();
    uint32_t reserved_b   = f.ReadUint32();
    uint16_t length_count = f.ReadUint16();
    if (reserved_a != 0 || reserved_b != 0 || length_count != bones_num)
        return ParseFailure(
            path, services, "unsupported MDLS IK graph header", f.Tell() - 7, end_offset);

    if (! ResizeCounted(f,
                        puppet.ik_nodes,
                        length_count,
                        4,
                        end_offset,
                        path,
                        services,
                        "MDLS IK bone lengths"))
        return false;
    for (auto& node : puppet.ik_nodes) {
        const auto length_offset = f.Tell();
        node.length              = f.ReadFloat();
        if (! std::isfinite(node.length) || node.length < 0.0f)
            return ParseFailure(
                path, services, "invalid MDLS IK bone length", length_offset, end_offset);
    }

    for (uint32_t parent = 0; parent < bones_num; ++parent) {
        if (! RequireBytes(f, 2, end_offset, path, services, "MDLS IK child count")) return false;
        uint16_t child_count = f.ReadUint16();
        auto& children = puppet.ik_nodes[usize(parent)].children;
        if (! ResizeCounted(
                f, children, child_count, 16, end_offset, path, services, "MDLS IK child table"))
            return false;
        for (auto& child : children) {
            const auto child_offset = f.Tell();
            child.bone_index        = f.ReadUint32();
            for (auto& value : child.bind_direction) value = f.ReadFloat();
            if (child.bone_index >= bones_num ||
                puppet.bones[usize(child.bone_index)].file_parent != parent)
                return ParseFailure(
                    path, services, "invalid MDLS IK child edge", child_offset, end_offset);
            const float norm_squared = child.bind_direction.squaredNorm();
            if (! std::isfinite(norm_squared) || norm_squared < 0.99f || norm_squared > 1.01f)
                return ParseFailure(
                    path, services, "non-unit MDLS IK child direction", child_offset, end_offset);
        }
    }

    if (! RequireBytes(f, 2, end_offset, path, services, "MDLS IK chain count")) return false;
    uint16_t chain_count = f.ReadUint16();
    if (uint32_t(chain_count) * 2 != controller_count)
        return ParseFailure(
            path, services, "MDLS IK controller/chain count mismatch", f.Tell() - 2, end_offset);
    puppet.ik_chains.clear();
    for (uint16_t chain_index = 0; chain_index < chain_count; ++chain_index) {
        auto&      chain        = puppet.ik_chains.emplace_back();
        const auto chain_offset = f.Tell();
        if (! RequireBytes(f, 38, end_offset, path, services, "MDLS IK chain")) return false;
        chain.start_bone              = f.ReadUint32();
        uint32_t one_a                = f.ReadUint32();
        chain.target_controller_index = f.ReadUint32();
        uint16_t one_b                = f.ReadUint16();
        uint32_t repeated_start       = f.ReadUint32();
        uint16_t one_c                = f.ReadUint16();
        chain.end_bone                = f.ReadUint32();
        uint32_t one_d                = f.ReadUint32();
        chain.length                  = f.ReadFloat();
        uint32_t zero                 = f.ReadUint32();
        uint16_t path_count           = f.ReadUint16();
        if (one_a != 1 || one_b != 1 || repeated_start != chain.start_bone || one_c != 1 ||
            one_d != 1 || zero != 0 || path_count != 3 || ! std::isfinite(chain.length) ||
            chain.length <= 0.0f)
            return ParseFailure(
                path, services, "unsupported MDLS IK chain record", chain_offset, end_offset);
        if (! ResizeCounted(
                f, chain.bones, path_count, 4, end_offset, path, services, "MDLS IK chain path"))
            return false;
        for (auto& bone_index : chain.bones) bone_index = f.ReadUint32();
        if (chain.start_bone >= bones_num || chain.end_bone >= bones_num ||
            chain.bones[usize(1)] >= bones_num || chain.bones[usize(0)] != chain.start_bone ||
            chain.bones[usize(2)] != chain.end_bone ||
            puppet.bones[usize(chain.bones[usize(1)])].file_parent != chain.bones[usize(0)] ||
            puppet.bones[usize(chain.bones[usize(2)])].file_parent != chain.bones[usize(1)])
            return ParseFailure(
                path, services, "invalid MDLS IK chain path", chain_offset, end_offset);
        const float derived_length = puppet.ik_nodes[usize(chain.bones[usize(1)])].length +
                                     puppet.ik_nodes[usize(chain.bones[usize(2)])].length;
        if (puppet.ik_nodes[usize(chain.bones[usize(1)])].length <= 0.0f ||
            puppet.ik_nodes[usize(chain.bones[usize(2)])].length <= 0.0f ||
            ! std::isfinite(derived_length) || chain.length < derived_length - 0.01f ||
            chain.length > derived_length + 0.01f)
            return ParseFailure(
                path, services, "MDLS IK chain length mismatch", chain_offset, end_offset);
        if (chain.target_controller_index >= controller_count) {
            return ParseFailure(
                path, services, "invalid MDLS IK target controller", chain_offset, end_offset);
        }
        const auto& target = puppet.ik_controllers[usize(chain.target_controller_index)];
        if (target.type != 0 || target.bone_index != chain.end_bone)
            return ParseFailure(
                path, services, "invalid MDLS IK target controller", chain_offset, end_offset);
        uint32_t paired_count = 0;
        for (const auto& controller : puppet.ik_controllers) {
            if (controller.type == 1 && controller.bone_index == chain.end_bone) ++paired_count;
        }
        if (paired_count != 1)
            return ParseFailure(
                path, services, "unsupported MDLS IK paired controller", chain_offset, end_offset);
    }
    return true;
}

bool ParseMDLS(fs::BinaryReader& f, Mdl& mdl, std::string_view path, Services* services) {
    mdl.mdls = ReadMdlVersion(f);

    uint32_t end_offset = f.ReadUint32();
    if (! CheckBlockEnd(f, end_offset, path, services, "MDLS")) return false;

    uint16_t bones_num = f.ReadUint16();
    f.ReadUint16(); // zero pad

    mdl.puppet  = Some(Arc<Puppet>::make());
    auto& bones = (*mdl.puppet)->bones;

    // 每根骨骼至少：名字 1 + sim_type 4 + parent 4 + size 4 + 矩阵 64 + 模拟 JSON 1。
    if (! ResizeCounted(f, bones, bones_num, 78, end_offset, path, services, "MDLS bones"))
        return false;
    for (unsigned i = 0; i < bones_num; ++i) {
        auto& bone = bones[usize(i)];
        if (! ReadOwnedString(f, bone.name, path, services, "bone name", end_offset)) return false;
        bone.sim_type = f.ReadInt32();

        uint32_t file_parent = f.ReadUint32();
        if (file_parent >= i && file_parent != Puppet::NO_PARENT) {
            rstd_info("mdl bone[{}] forward parent {} in {}; treating as root",
                      i,
                      file_parent,
                      std::string(path));
            file_parent = Puppet::NO_PARENT;
        }
        bone.bind_parent = file_parent;
        bone.anim_parent = file_parent;
        bone.file_parent = file_parent;

        uint32_t size = f.ReadUint32();
        if (size != 64) {
            rstd_error("mdl unsupport bones size: {}", size);
            return false;
        }
        for (auto row : bone.local_bind.matrix().colwise()) {
            for (auto& x : row) x = f.ReadFloat();
        }
        if (! ReadOwnedString(
                f, bone.simulation_json, path, services, "bone simulation JSON", end_offset))
            return false;
    }

    if (mdl.mdls > 1) {
        const auto extras_offset = f.Tell();
        if (! RequireBytes(f, 2, end_offset, path, services, "MDLS extras count")) return false;
        uint16_t extras_count = f.ReadUint16();

        if (mdl.mdls == 2) {
            if (extras_count != 0 && extras_count != 5)
                return ParseFailure(path,
                                    services,
                                    "unsupported MDLS extras: version=2, extras_flag=" +
                                        std::to_string(extras_count),
                                    extras_offset,
                                    end_offset);
            uint8_t has_world_binds = f.ReadUint8();
            if (has_world_binds) {
                if (! RequireBytes(f,
                                   uint64_t(bones_num) * 64,
                                   end_offset,
                                   path,
                                   services,
                                   "MDLS world binds"))
                    return false;
                // Per-bone world-bind mat4 inline (mdls v2 only).
                for (unsigned i = 0; i < bones_num; ++i)
                    for (unsigned j = 0; j < 16; ++j) f.ReadFloat();
            }
            uint8_t pad[8];
            f.Read(pad, sizeof(pad));
            if (extras_count == 5) {
                auto trailer_start = mdls_v2_indexed_trailer_start(end_offset, bones_num);
                if (trailer_start >= f.Tell() &&
                    is_mdls_v2_indexed_trailer(f, trailer_start, end_offset, bones_num)) {
                    f.SeekSet(trailer_start);
                } else {
                    rstd_info("MDLSv2 extras_flag 5 did not match indexed trailer in {}",
                              std::string(path));
                }
            }
        } else if (extras_count != 0) {
            if (mdl.header.mdlv != 23 || mdl.mdls != 4)
                return ParseFailure(path,
                                    services,
                                    "unsupported MDL IK controller schema: mdlv=" +
                                        std::to_string(mdl.header.mdlv) +
                                        ", mdls=" + std::to_string(mdl.mdls) +
                                        ", controller_count=" + std::to_string(extras_count),
                                    extras_offset,
                                    end_offset);
            if (! ParseIkRig(f, mdl, extras_count, bones_num, end_offset, path, services))
                return false;
        } else {
            if (! RequireBytes(f, 9, end_offset, path, services, "MDLS metadata header"))
                return false;
            uint8_t zero_b = f.ReadUint8();
            if (zero_b != 0) {
                rstd_info("MDLSv{} zero_b expected 0, got {}", mdl.mdls, zero_b);
            }
            uint32_t pair0 = f.ReadUint32();
            uint32_t pair1 = f.ReadUint32();
            (void)pair0;
            (void)pair1;
        }

        if (static_cast<uint32_t>(f.Tell()) < end_offset) {
            if (! RequireBytes(f, 1, end_offset, path, services, "MDLS offset-transform flag"))
                return false;
            uint8_t has_offset_trans = f.ReadUint8();
            if (has_offset_trans) {
                if (! RequireBytes(f,
                                   uint64_t(bones_num) * mdls_offset_trans_entry_size,
                                   end_offset,
                                   path,
                                   services,
                                   "MDLS offset transforms"))
                    return false;
                for (unsigned i = 0; i < bones_num; ++i) {
                    auto& b               = (*mdl.puppet)->bones[usize(i)];
                    b.has_file_skin_pivot = true;
                    b.file_skin_pivot.x() = f.ReadFloat();
                    b.file_skin_pivot.y() = f.ReadFloat();
                    b.file_skin_pivot.z() = f.ReadFloat();
                    for (auto col : b.file_skin_mat.colwise()) {
                        for (auto& v : col) v = f.ReadFloat();
                    }
                }
            }

            if (! RequireBytes(f, 1, end_offset, path, services, "MDLS bone-index flag"))
                return false;
            uint8_t has_index = f.ReadUint8();
            if (has_index) {
                if (! RequireBytes(f,
                                   uint64_t(bones_num) * 4,
                                   end_offset,
                                   path,
                                   services,
                                   "MDLS bone-index table"))
                    return false;
                for (unsigned i = 0; i < bones_num; ++i) f.ReadUint32();
            }

            if (mdl.mdls >= 3) {
                if (! RequireBytes(f, 1, end_offset, path, services, "MDLS bone-depth flag"))
                    return false;
                uint8_t has_depth = f.ReadUint8();
                if (has_depth) {
                    if (! RequireBytes(f,
                                       uint64_t(bones_num) * 4,
                                       end_offset,
                                       path,
                                       services,
                                       "MDLS bone-depth table"))
                        return false;
                    for (unsigned i = 0; i < bones_num; ++i) (void)f.ReadUint32();
                }
            }
        }
    }

    // Honour the block's declared end so partial IK / unknown trailer can't
    // poison subsequent MDxx scans.
    if (end_offset > 0 && static_cast<uint32_t>(f.Tell()) != end_offset) {
        rstd_info("MDLS body ended at 0x{:X} but end_offset=0x{:X} ({})",
                  static_cast<uint32_t>(f.Tell()),
                  end_offset,
                  std::string(path));
        f.SeekSet(end_offset);
    }
    return true;
}

bool ParseMDAT(fs::BinaryReader& f, Mdl& mdl, std::string_view path, Services* services) {
    if (! RequirePuppet(mdl, "MDAT", f.Tell(), path, services)) return false;
    uint32_t end_offset = f.ReadUint32();
    if (! CheckBlockEnd(f, end_offset, path, services, "MDAT")) return false;
    uint32_t num_attachments = f.ReadUint16();
    auto&    attachments     = (*mdl.puppet)->attachments;
    // 每个挂点至少：骨骼序号 2 + 名字 1 + 矩阵 64。
    if (! ResizeCounted(
            f, attachments, num_attachments, 67, end_offset, path, services, "MDAT attachments"))
        return false;
    for (auto& att : attachments) {
        att.bone_index = f.ReadUint16();
        if (! ReadOwnedString(f, att.name, path, services, "attachment name", end_offset))
            return false;
        // 64-byte payload = column-major 4x4 affine in the anchored bone's
        // local space (linear 3x3 in cols 0-2, translation in col 3).
        att.local_xform = Eigen::Affine3f::Identity();
        for (auto col : att.local_xform.matrix().colwise()) {
            for (auto& v : col) v = f.ReadFloat();
        }
    }
    if (end_offset > 0 && static_cast<uint32_t>(f.Tell()) != end_offset) {
        f.SeekSet(end_offset);
    }
    return true;
}

// hexpat AnimBoneCurves: u8 has_curves; if(has_curves) BoneFrameCurve[bone_count].
// Each BoneFrameCurve = u32 zero + u32 byte_size + float[byte_size/4].
bool ParseAnimBoneCurves(fs::BinaryReader& f, Vec<Puppet::BoneFrameCurve>& out, uint32_t bone_count,
                         uint32_t end_offset, std::string_view path, Services* services) {
    uint8_t has_curves = f.ReadUint8();
    if (! has_curves) return true;
    if (! ResizeCounted(f, out, bone_count, 8, end_offset, path, services, "MDLA bone curves"))
        return false;
    for (auto& curve : out) {
        uint32_t zero_a = f.ReadUint32();
        if (zero_a != 0) {
            rstd_info("BoneFrameCurve zero_a expected 0, got {}", zero_a);
        }
        uint32_t byte_size = f.ReadUint32();
        if (byte_size % 4 != 0) {
            rstd_error("BoneFrameCurve byte_size {} not %% 4", byte_size);
            return false;
        }
        if (! ResizeCounted(f,
                            curve.values,
                            byte_size / 4,
                            4,
                            end_offset,
                            path,
                            services,
                            "MDLA bone curve values"))
            return false;
        for (auto& v : curve.values) v = f.ReadFloat();
    }
    return true;
}

bool ParseAnimTransMainTrack(fs::BinaryReader& f, Vec<float>& out, int32_t length,
                             uint32_t end_offset, std::string_view path, Services* services) {
    uint32_t byte_size = f.ReadUint32();
    if (! is_anim_trans_main_size(byte_size, length)) {
        rstd_error("AnimTransMain byte_size {} does not match animation length {} in {}",
                   byte_size,
                   length,
                   std::string(path));
        return false;
    }
    if (! ResizeCounted(
            f, out, byte_size / 4, 4, end_offset, path, services, "MDLA translation track"))
        return false;
    for (auto& v : out) v = f.ReadFloat();
    return true;
}

bool ParseControllerTrack(fs::BinaryReader& f, Puppet::BoneTrack& out, uint32_t index,
                          int32_t length, uint32_t end_offset, std::string_view path,
                          Services* services) {
    if (! RequireBytes(f, 4, end_offset, path, services, "MDLA IK controller track size"))
        return false;
    uint32_t byte_size = f.ReadUint32();
    if (! is_controller_track_size(byte_size, length)) {
        rstd_error("IK controller track byte_size {} does not match animation length {} in {}",
                   byte_size,
                   length,
                   std::string(path));
        return false;
    }
    out.bone_index = index;
    if (! ResizeCounted(f,
                        out.frames,
                        byte_size / singile_bone_frame,
                        singile_bone_frame,
                        end_offset,
                        path,
                        services,
                        "MDLA IK controller track"))
        return false;
    for (auto& frame : out.frames) {
        const auto frame_offset = f.Tell();
        for (auto& value : frame.position) value = f.ReadFloat();
        for (auto& value : frame.angle) value = f.ReadFloat();
        for (auto& value : frame.scale) value = f.ReadFloat();
        if (! std::isfinite(frame.position.x()) || ! std::isfinite(frame.position.y()) ||
            ! std::isfinite(frame.position.z()) || ! std::isfinite(frame.angle.x()) ||
            ! std::isfinite(frame.angle.y()) || ! std::isfinite(frame.angle.z()) ||
            ! std::isfinite(frame.scale.x()) || ! std::isfinite(frame.scale.y()) ||
            ! std::isfinite(frame.scale.z()))
            return ParseFailure(
                path, services, "non-finite MDLA IK controller frame", frame_offset, end_offset);
    }
    return true;
}

bool ParseAnimation(fs::BinaryReader& f, Puppet::Animation& anim, int mdla_ver,
                    uint32_t mdla_end_offset, uint32_t controller_count, std::string_view path,
                    Services* services) {
    anim.id           = f.ReadInt32();
    anim.unk_after_id = f.ReadUint32();

    if (! ReadOwnedString(f, anim.name, path, services, "animation name", mdla_end_offset))
        return false;
    if (anim.name.is_empty() &&
        ! ReadOwnedString(f, anim.name, path, services, "animation name", mdla_end_offset))
        return false;

    const auto mode_offset = f.Tell();
    String     play_mode;
    if (! ReadOwnedString(f, play_mode, path, services, "animation play_mode", mdla_end_offset))
        return false;
    auto mode = play_mode.as_str();
    if (mode == "loop"_str || mode.is_empty())
        anim.mode = Puppet::PlayMode::Loop;
    else if (mode == "mirror"_str)
        anim.mode = Puppet::PlayMode::Mirror;
    else if (mode == "single"_str)
        anim.mode = Puppet::PlayMode::Single;
    else
        return ParseFailure(
            path, services, "unsupported animation play_mode", mode_offset, mdla_end_offset);
    anim.fps    = f.ReadFloat();
    anim.length = f.ReadInt32();
    anim.flags  = f.ReadUint32();
    if (anim.flags != 0 && anim.flags != kAnimFlagsKnown) {
        rstd_info("Animation {} flags 0x{:X} differs from the observed 0x{:X} ({})",
                  anim.name,
                  anim.flags,
                  kAnimFlagsKnown,
                  std::string(path));
    }

    uint32_t b_num = f.ReadUint32();
    if (! ResizeCounted(
            f, anim.bone_tracks, b_num, 8, mdla_end_offset, path, services, "MDLA bone tracks"))
        return false;
    for (uint32_t ti = 0; ti < b_num; ++ti) {
        auto& track        = anim.bone_tracks[usize(ti)];
        track.bone_index   = ti; // dense: slot i animates bone i
        track.unk          = f.ReadInt32();
        uint32_t byte_size = f.ReadUint32();
        if (byte_size % singile_bone_frame != 0) {
            rstd_error("wrong bone frame size {} in {}", byte_size, std::string(path));
            return false;
        }
        uint32_t num = byte_size / singile_bone_frame;
        if (! ResizeCounted(f,
                            track.frames,
                            num,
                            singile_bone_frame,
                            mdla_end_offset,
                            path,
                            services,
                            "MDLA bone track frames"))
            return false;
        for (auto& frame : track.frames) {
            for (auto& v : frame.position) v = f.ReadFloat();
            for (auto& v : frame.angle) v = f.ReadFloat();
            for (auto& v : frame.scale) v = f.ReadFloat();
        }
    }

    if (mdla_ver >= 3) {
        uint32_t trans_flag = f.ReadUint32();
        if (trans_flag == 1) {
            if (controller_count != 0)
                return ParseFailure(path,
                                    services,
                                    "unsupported MDLA IK controller track layout",
                                    f.Tell() - 4,
                                    mdla_end_offset);
            auto&    tr         = anim.trans.insert(Puppet::AnimTrans {});
            uint32_t extra_size = f.ReadUint32();
            if (extra_size > 0) {
                if (extra_size % 4 != 0) {
                    rstd_error("UnkAnimTrans extra_size {} not %% 4", extra_size);
                    return false;
                }
                if (! ResizeCounted(f,
                                    tr.extra_track,
                                    extra_size / 4,
                                    4,
                                    mdla_end_offset,
                                    path,
                                    services,
                                    "MDLA translation extra track"))
                    return false;
                for (auto& v : tr.extra_track) v = f.ReadFloat();
                uint32_t extra_zero = f.ReadUint32();
                if (extra_zero != 0) {
                    rstd_info("UnkAnimTrans extra_zero expected 0, got {}", extra_zero);
                }
            }
            uint32_t main_size = f.ReadUint32();
            if (main_size % 4 != 0) {
                rstd_error("UnkAnimTrans main_size {} not %% 4", main_size);
                return false;
            }
            if (! ResizeCounted(f,
                                tr.main_track,
                                main_size / 4,
                                4,
                                mdla_end_offset,
                                path,
                                services,
                                "MDLA translation track"))
                return false;
            for (auto& v : tr.main_track) v = f.ReadFloat();
            if (extra_size > 0) {
                uint32_t trail_zero = f.ReadUint32();
                if (trail_zero != 0) {
                    rstd_info("UnkAnimTrans trail_zero expected 0, got {}", trail_zero);
                }
            }
        } else if (trans_flag == 0) {
            if (next_is_anim_trans_main(f, anim.length)) {
                bool first = true;
                do {
                    if (! first) {
                        uint32_t trail_zero = f.ReadUint32();
                        if (trail_zero != 0) {
                            rstd_info("AnimTransMain trail_zero expected 0, got {}", trail_zero);
                        }
                    }
                    uint32_t byte_size = 0;
                    if (! peek_uint32_at(f, f.Tell(), byte_size)) return false;
                    if (controller_count != 0 && is_controller_track_size(byte_size, anim.length)) {
                        const uint32_t index =
                            static_cast<uint32_t>(anim.controller_tracks.len().to_primitive());
                        auto& track = anim.controller_tracks.emplace_back();
                        if (! ParseControllerTrack(
                                f, track, index, anim.length, mdla_end_offset, path, services))
                            return false;
                    } else {
                        Vec<float>* track = nullptr;
                        if (anim.trans.is_none()) {
                            track =
                                std::addressof(anim.trans.insert(Puppet::AnimTrans {}).main_track);
                        } else {
                            track = std::addressof((*anim.trans).tail_tracks.emplace_back());
                        }
                        if (! ParseAnimTransMainTrack(
                                f, *track, anim.length, mdla_end_offset, path, services))
                            return false;
                    }
                    first = false;
                } while (next_after_zero_is_anim_trans_main(f, anim.length));
            }
        } else {
            rstd_error("Animation {} trans_flag expected 0/1, got {} in {}",
                       anim.name,
                       trans_flag,
                       std::string(path));
            return false;
        }
        if (anim.controller_tracks.len() != usize(controller_count))
            return ParseFailure(path,
                                services,
                                "MDLA IK controller track count mismatch: expected=" +
                                    std::to_string(controller_count) + ", actual=" +
                                    std::to_string(anim.controller_tracks.len().to_primitive()),
                                f.Tell(),
                                mdla_end_offset);
        if (controller_count != 0) {
            if (! RequireBytes(f, 4, mdla_end_offset, path, services, "MDLA IK controller trailer"))
                return false;
            const auto trailer_offset = f.Tell();
            if (f.ReadUint32() != 0)
                return ParseFailure(path,
                                    services,
                                    "unsupported MDLA IK controller trailer",
                                    trailer_offset,
                                    mdla_end_offset);
        }
        if (! ParseAnimBoneCurves(f, anim.blend_curves, b_num, mdla_end_offset, path, services))
            return false;
    }

    if (mdla_ver >= 4) {
        uint8_t has_v4_events = f.ReadUint8();
        if (has_v4_events == 1) {
            uint32_t v4_count = f.ReadUint32();
            // 每个事件至少：时间 4 + 曲线数 2 + flags 2 + 首条曲线长度 4。
            if (! ResizeCounted(f,
                                anim.v4_events,
                                v4_count,
                                12,
                                mdla_end_offset,
                                path,
                                services,
                                "MDLA v4 events"))
                return false;
            for (auto& ev : anim.v4_events) {
                ev.time              = f.ReadFloat();
                uint16_t curve_count = f.ReadUint16();
                ev.flags             = f.ReadUint16();
                if (curve_count == 0) {
                    rstd_error("AnimV4Event curve_count is zero in {}", std::string(path));
                    return false;
                }
                if (! ResizeCounted(f,
                                    ev.curves,
                                    curve_count,
                                    4,
                                    mdla_end_offset,
                                    path,
                                    services,
                                    "MDLA v4 event curves"))
                    return false;
                for (uint16_t curve_index = 0; curve_index < curve_count; ++curve_index) {
                    auto& curve = ev.curves[usize(curve_index)];
                    curve.id    = curve_index == 0 ? 0 : f.ReadUint16();
                    uint32_t bs = f.ReadUint32();
                    if (bs % 4 != 0) {
                        rstd_error("AnimV4Curve byte_size {} not %% 4", bs);
                        return false;
                    }
                    if (! ResizeCounted(f,
                                        curve.values,
                                        bs / 4,
                                        4,
                                        mdla_end_offset,
                                        path,
                                        services,
                                        "MDLA v4 event curve values"))
                        return false;
                    for (auto& v : curve.values) v = f.ReadFloat();
                }
            }
        } else if (has_v4_events != 0) {
            rstd_info("Animation has_v4_events expected 0/1, got {}", has_v4_events);
        }
    }

    if (mdla_ver >= 5) {
        for (auto& v : anim.aabb_min) v = f.ReadFloat();
        for (auto& v : anim.aabb_max) v = f.ReadFloat();
        anim.has_aabb = true;
    }

    if (mdla_ver == 6) {
        if (next_is_anim_bone_curves(f)) {
            if (! ParseAnimBoneCurves(
                    f, anim.scalar_curves, b_num, mdla_end_offset, path, services))
                return false;
        }
    }

    // flags 位 0x400：事件表之前多一段 18 字节的源动画引用尾块
    // {u32 源动画序号, u16 0, u32 帧数, u32 0, i32 -1}。语料里 6 条样本全是 MDLA0006、
    // play_mode 为空、源序号 < anim_num、帧数 == length；读过即可，渲染不用这段。
    if (anim.flags & kAnimFlagSourceRef) {
        if (! RequireBytes(f, 18, mdla_end_offset, path, services, "MDLA animation source reference"))
            return false;
        f.ReadUint32(); // 源动画序号
        const uint16_t pad = f.ReadUint16();
        f.ReadUint32(); // 帧数
        const uint32_t zero      = f.ReadUint32();
        const int32_t  minus_one = f.ReadInt32();
        if (pad != 0 || zero != 0 || minus_one != -1)
            rstd_info("Animation {} source reference {}/{}/{} differs from observed 0/0/-1 ({})",
                      anim.name,
                      pad,
                      zero,
                      minus_one,
                      std::string(path));
    }

    // Trailing event list — present on every animation regardless of mdla
    // version. Pre-mdla>=3 anims start here directly.
    uint32_t event_count = f.ReadUint32();
    // 每条事件至少：时间 4 + JSON 1。
    if (! ResizeCounted(
            f, anim.events, event_count, 5, mdla_end_offset, path, services, "animation events"))
        return false;
    for (auto& ev : anim.events) {
        ev.time_value = f.ReadUint32();
        if (! ReadOwnedString(
                f, ev.event_json, path, services, "animation event JSON", mdla_end_offset))
            return false;
    }
    if (next_is_anim_record_padding(f, mdla_end_offset)) {
        uint32_t record_padding_zero = f.ReadUint32();
        if (record_padding_zero != 0) {
            rstd_info("Animation {} record_padding_zero expected 0, got {}",
                      anim.name,
                      record_padding_zero);
        }
    }
    return true;
}

bool ParseMDLA(fs::BinaryReader& f, Mdl& mdl, std::string_view tag, std::string_view path,
               Services* services) {
    if (! ParseTagVersion(tag, mdl.mdla, f.Tell() - 9, path, services)) return false;
    if (mdl.mdla == 0) return true;
    if (! RequirePuppet(mdl, "MDLA", f.Tell(), path, services)) return false;

    uint32_t end_offset = f.ReadUint32();
    if (! CheckBlockEnd(f, end_offset, path, services, "MDLA")) return false;

    uint32_t anim_num = f.ReadUint32();
    auto&    anims    = (*mdl.puppet)->anims;
    // 每条动画至少：id 4 + 4 + 名字 1 + play_mode 1 + fps 4 + length 4 + flags 4 + 骨骼轨数 4 +
    // 事件数 4。
    if (! ResizeCounted(f, anims, anim_num, 30, end_offset, path, services, "MDLA animations"))
        return false;
    for (auto& anim : anims) {
        if (! ParseAnimation(
                f,
                anim,
                mdl.mdla,
                end_offset,
                static_cast<uint32_t>((*mdl.puppet)->ik_controllers.len().to_primitive()),
                path,
                services))
            return false;
    }

    if (end_offset > 0 && static_cast<uint32_t>(f.Tell()) + 4 == end_offset) {
        uint32_t final_padding_zero = f.ReadUint32();
        if (final_padding_zero != 0) {
            rstd_info("MDLA final_padding_zero expected 0, got {} ({})",
                      final_padding_zero,
                      std::string(path));
        }
    }
    if (end_offset > 0 && static_cast<uint32_t>(f.Tell()) != end_offset) {
        rstd_info("MDLA body ended at 0x{:X} but end_offset=0x{:X} ({})",
                  static_cast<uint32_t>(f.Tell()),
                  end_offset,
                  std::string(path));
        f.SeekSet(end_offset);
    }
    return true;
}

bool ParseMasks(fs::BinaryReader& f, Mdl::Mesh& mesh, std::string_view path, Services* services) {
    uint32_t mask_count = f.ReadUint32();
    // 每个遮罩至少：4 + 4 + 材质名 1 + 4 + 两个表长 4 + 4。
    if (! ResizeCounted(f, mesh.masks, mask_count, 21, 0, path, services, "mesh masks"))
        return false;
    for (auto& m : mesh.masks) {
        m.leading_a     = f.ReadUint32();
        uint32_t zero_a = f.ReadUint32();
        if (zero_a != 0) rstd_info("MaskBlock zero_a expected 0, got {}", zero_a);
        if (! ReadOwnedString(f, m.mat_json, path, services, "mask material name")) return false;
        uint32_t zero_pad = f.ReadUint32();
        if (zero_pad != 0) rstd_info("MaskBlock zero_pad expected 0, got {}", zero_pad);
        uint32_t a_count = f.ReadUint32();
        if (! ResizeCounted(f, m.part_ids_a, a_count, 4, 0, path, services, "mesh mask part ids"))
            return false;
        for (auto& v : m.part_ids_a) v = f.ReadUint32();
        uint32_t b_count = f.ReadUint32();
        if (! ResizeCounted(f, m.part_ids_b, b_count, 4, 0, path, services, "mesh mask part ids"))
            return false;
        for (auto& v : m.part_ids_b) v = f.ReadUint32();
    }
    return true;
}

bool ParseMDMP(fs::BinaryReader& f, Mdl& mdl, std::string_view tag, std::string_view path,
               Services* services) {
    if (! ParseTagVersion(tag, mdl.mdmp, f.Tell() - 9, path, services)) return false;
    uint32_t end_offset = f.ReadUint32();
    if (! CheckBlockEnd(f, end_offset, path, services, "MDMP")) return false;
    while (f.Tell() < end_offset) {
        auto&    sec    = mdl.morph_sections.emplace_back();
        uint16_t count  = f.ReadUint16();
        sec.event_time  = f.ReadFloat();
        sec.event_id    = f.ReadUint16();
        uint16_t zero_a = f.ReadUint16();
        if (zero_a != 0) {
            rstd_info("MDMPSection zero_a expected 0, got {}", zero_a);
        }
        // 每段至少：shape_id 4 + 4 + 标签 1 + 长度 4 + hash 4。
        if (! ResizeCounted(
                f, sec.sections, count, 17, end_offset, path, services, "MDMP section data"))
            return false;
        for (auto& sd : sec.sections) {
            sd.shape_id      = f.ReadUint32();
            uint32_t sd_zero = f.ReadUint32();
            if (sd_zero != 0) {
                rstd_info("MDMPSectionData zero_a expected 0, got {}", sd_zero);
            }
            if (! ReadOwnedString(f, sd.tag, path, services, "morph section tag", end_offset))
                return false;
            uint32_t length = f.ReadUint32();
            sd.hash         = f.ReadUint32();
            if (length % 6 != 0) {
                rstd_error("MDMPSectionData length {} not %% 6", length);
                return false;
            }
            uint32_t vcount = length / 6;
            if (! ResizeCounted(
                    f, sd.vertices, vcount, 6, end_offset, path, services, "MDMP vertices"))
                return false;
            for (auto& v : sd.vertices) {
                for (auto& x : v) x = f.ReadUint16();
            }
            if (sd.shape_id == 0) {
                if (! ResizeCounted(
                        f, sd.trailer, length, 1, end_offset, path, services, "MDMP trailer"))
                    return false;
                for (auto& b : sd.trailer) b = f.ReadUint8();
            } else {
                if (! ResizeCounted(f,
                                    sd.vertex_trailers,
                                    vcount,
                                    2,
                                    end_offset,
                                    path,
                                    services,
                                    "MDMP vertex trailers"))
                    return false;
                for (auto& v : sd.vertex_trailers) v = f.ReadUint16();
            }
        }
    }
    if (end_offset > 0 && static_cast<uint32_t>(f.Tell()) != end_offset) {
        rstd_info("MDMP body ended at 0x{:X} but end_offset=0x{:X} ({})",
                  static_cast<uint32_t>(f.Tell()),
                  end_offset,
                  std::string(path));
        f.SeekSet(end_offset);
    }
    return true;
}

bool ParseMDLE(fs::BinaryReader& f, Mdl& mdl, std::string_view tag, std::string_view path,
               Services* services) {
    if (! ParseTagVersion(tag, mdl.mdle, f.Tell() - 9, path, services)) return false;
    if (! RequirePuppet(mdl, "MDLE", f.Tell(), path, services)) return false;
    uint32_t end_offset = f.ReadUint32();
    if (! CheckBlockEnd(f, end_offset, path, services, "MDLE")) return false;
    uint32_t     payload_bytes = f.ReadUint32();
    const size_t nbones        = (*mdl.puppet)->bones.len().to_primitive();
    const size_t expected      = nbones * 64;
    if (payload_bytes != expected) {
        rstd_error("MDLE payload_bytes {} != bones_num*64 {}", payload_bytes, expected);
        return false;
    }
    if (! RequireBytes(f, payload_bytes, end_offset, path, services, "MDLE world binds"))
        return false;
    for (auto& bone : (*mdl.puppet)->bones) {
        bone.file_world_bind = Eigen::Affine3f::Identity();
        for (auto col : bone.file_world_bind.matrix().colwise()) {
            for (auto& v : col) v = f.ReadFloat();
        }
        bone.has_file_world_bind = true;
    }
    if (end_offset > 0 && static_cast<uint32_t>(f.Tell()) != end_offset) {
        f.SeekSet(end_offset);
    }
    return true;
}

// MDLS v3+ vertex-centroid offsets per bone. MDLV21 puppets need the bind
// chain flattened and the centroid_offset bracketed around scale/rotation
// in genFrame (bones are world-anchored, sprite lives at bind.t + vco), while
// animation still follows the file parent chain via parent skin deltas.
// MDLV22+ keeps file_parent intact for chain LBS; `vertex_centroid_offset`
// is still computed but not consumed by the chain path.
void ApplyMDLS3CentroidPivot(Mdl& mdl) {
    if (mdl.meshes.is_empty()) return;
    if ((*mdl.puppet)->world_anchored_bones) {
        for (auto& b : (*mdl.puppet)->bones) {
            b.bind_parent = Puppet::NO_PARENT;
            b.anim_parent = b.file_parent;
        }
    }
    const size_t                 nbones = (*mdl.puppet)->bones.len().to_primitive();
    std::vector<Eigen::Vector3d> sum_pos(nbones, Eigen::Vector3d::Zero());
    std::vector<double>          sum_w(nbones, 0.0);
    auto                         v_to_e = [](const array<float, 3>& p) {
        return Eigen::Vector3d { p[usize(0)], p[usize(1)], p[usize(2)] };
    };

    // Multi-mesh puppets (mesh_count > 1) may distribute skin data across
    // sub-meshes; accumulate centroid contributions from every mesh that has
    // bone indices. Meshes that only carry SKIN_BLEND (no SKIN_WEIGHT) follow
    // the WE 1-bone rigid convention: implicit weight 1.0 on slot 0.
    auto contribute = [&](const Mdl::Mesh& m) {
        if (m.blend_indices.is_empty()) return;
        const bool has_w  = ! m.blend_weights.is_empty();
        auto       weight = [&](size_t vi, int k) -> float {
            if (! has_w) return k == 0 ? 1.0f : 0.0f;
            return m.blend_weights[usize(vi)][usize(static_cast<size_t>(k))];
        };
        if (! m.indices.is_empty()) {
            for (const auto& tri : m.indices) {
                if (tri[usize(0)] >= m.positions.len().to_primitive() ||
                    tri[usize(1)] >= m.positions.len().to_primitive() ||
                    tri[usize(2)] >= m.positions.len().to_primitive())
                    continue;
                Eigen::Vector3d p0           = v_to_e(m.positions[usize(tri[usize(0)])]);
                Eigen::Vector3d p1           = v_to_e(m.positions[usize(tri[usize(1)])]);
                Eigen::Vector3d p2           = v_to_e(m.positions[usize(tri[usize(2)])]);
                Eigen::Vector3d centroid_tri = (p0 + p1 + p2) / 3.0;
                double          area         = 0.5 * (p1 - p0).cross(p2 - p0).norm();
                if (area <= 0.0) continue;
                const int slots = has_w ? 4 : 1;
                for (int corner = 0; corner < 3; ++corner) {
                    uint32_t vi = tri[usize(static_cast<size_t>(corner))];
                    for (int slot = 0; slot < slots; ++slot) {
                        float    w  = weight(vi, slot);
                        uint32_t bi = m.blend_indices[usize(vi)][usize(static_cast<size_t>(slot))];
                        if (w > 0.0f && bi < nbones) {
                            double tri_w = (area / 3.0) * static_cast<double>(w);
                            sum_pos[bi] += centroid_tri * tri_w;
                            sum_w[bi] += tri_w;
                        }
                    }
                }
            }
        } else {
            const int slots = has_w ? 4 : 1;
            for (size_t vi = 0; vi < m.positions.len().to_primitive(); ++vi) {
                Eigen::Vector3d p = v_to_e(m.positions[usize(vi)]);
                for (int k = 0; k < slots; ++k) {
                    float    w  = weight(vi, k);
                    uint32_t bi = m.blend_indices[usize(vi)][usize(static_cast<size_t>(k))];
                    if (w > 0.0f && bi < nbones) {
                        sum_pos[bi] += p * (double)w;
                        sum_w[bi] += (double)w;
                    }
                }
            }
        }
    };
    for (const auto& m : mdl.meshes) contribute(m);

    for (size_t i = 0; i < nbones; ++i) {
        if (sum_w[i] > 0.0) {
            Eigen::Vector3f centroid = (sum_pos[i] / sum_w[i]).cast<float>();
            (*mdl.puppet)->bones[usize(i)].vertex_centroid_offset =
                centroid - (*mdl.puppet)->bones[usize(i)].local_bind.translation();
        }
    }
}

// hexpat Header: VersionTag mdlv + u32 mdl_flag + u32 skin_count + u32 mesh_count.
bool ReadHeaderFromStream(fs::BinaryReader& f, MdlHeader& h, std::string_view path_for_log) {
    h.mdlv       = ReadMdlVersion(f);
    h.mdl_flag   = f.ReadUint32();
    h.skin_count = f.ReadUint32();
    h.mesh_count = f.ReadUint32();
    if (h.skin_count == 0) {
        rstd_error("mdl '{}' header has no material skins", std::string(path_for_log));
        return false;
    }
    return true;
}

std::string ResolveMdlMaterialPath(std::string_view ref) {
    std::string path(ref);
    if (! path.ends_with(".json")) path += ".json";
    if (path.starts_with("materials/")) return "/assets/" + path;
    return "/assets/materials/" + path;
}

} // namespace

bool MdlParser::ParseHeader(ref<str> path, fs::VFS& vfs, MdlHeader& h) {
    auto path_view = rstd::cppstd::as_string_view(path);
    auto pfile     = fs::OpenBinary(vfs, "/assets/" + std::string(path_view));
    if (pfile.is_err()) return false;
    auto f = rstd::move(pfile).unwrap_unchecked();
    return ReadHeaderFromStream(f, h, path_view);
}

bool MdlParser::Parse(ref<str> path, fs::VFS& vfs, Mdl& mdl, Services* services, bool* missing) {
    if (missing != nullptr) *missing = false;
    auto str_path = std::string(rstd::cppstd::as_string_view(path));
    auto pfile    = fs::OpenBinary(vfs, "/assets/" + str_path);
    if (pfile.is_err()) {
        auto error = rstd::move(pfile).unwrap_err_unchecked();
        if (missing != nullptr) {
            *missing = error.kind() == owe::io::ErrorKind::NotFound;
        }
        return false;
    }
    auto f = rstd::move(pfile).unwrap_unchecked();

    if (! ReadHeaderFromStream(f, mdl.header, str_path)) return false;

    // 每个网格至少：flag_a 4 + 顶点段长度 4 + 索引段长度 4。
    if (! ResizeCounted(f, mdl.meshes, mdl.header.mesh_count, 12, 0, str_path, services, "meshes"))
        return false;
    for (auto& m : mdl.meshes) {
        if (! ParseMesh(f, mdl.header, m, str_path, services)) return false;
    }

    // Consume the 9-byte VersionTag for blocks whose body parser expects to
    // start at `end_offset`. MDLS reads its tag internally via ReadMdlVersion.
    auto consume_tag = [&]() -> std::string {
        char buf[9] { 0 };
        f.Read(buf, 9);
        return std::string(buf, 8);
    };

    if (peek_block_magic(f, "MDLS")) {
        if (! ParseMDLS(f, mdl, str_path, services)) return false;
    }
    if (peek_block_magic(f, "MDAT")) {
        (void)consume_tag();
        if (! ParseMDAT(f, mdl, str_path, services)) return false;
    }
    if (peek_block_magic(f, "MDLA")) {
        std::string tag = consume_tag();
        if (! ParseMDLA(f, mdl, tag, str_path, services)) return false;
    }
    if (peek_block_magic(f, "MDMP")) {
        std::string tag = consume_tag();
        if (! ParseMDMP(f, mdl, tag, str_path, services)) return false;
    }
    if (peek_block_magic(f, "MDLE")) {
        std::string tag = consume_tag();
        if (! ParseMDLE(f, mdl, tag, str_path, services)) return false;
    }

    // hexpat Body: u8 trailing_nul (mdlv>=14). mdlv==13 file end is padded
    // with zeros until EOF.
    if (mdl.header.mdlv >= 14 && f.Tell() < f.Size()) {
        uint8_t trailing_nul = f.ReadUint8();
        if (trailing_nul != 0) {
            rstd_info("mdlv{} trailing_nul expected 0, got {}", mdl.header.mdlv, trailing_nul);
        }
    } else if (mdl.header.mdlv == 13) {
        while (f.Tell() < f.Size()) {
            auto    save = f.Tell();
            uint8_t b    = f.ReadUint8();
            if (b != 0) {
                f.SeekSet(save);
                break;
            }
        }
    }

    if (mdl.puppet.is_some()) {
        (*mdl.puppet)->world_anchored_bones = (mdl.header.mdlv == 21);
    }

    if (mdl.mdls >= 3) ApplyMDLS3CentroidPivot(mdl);

    if (mdl.puppet.is_some()) (*mdl.puppet)->prepared();

    rstd_info("read puppet: mdlv: {}, nmdls: {}, mdla: {}, mdle: {}, bones: {}, anims: {}",
              mdl.header.mdlv,
              mdl.mdls,
              mdl.mdla,
              mdl.mdle,
              mdl.puppet.is_some() ? (*mdl.puppet)->bones.len() : usize(),
              mdl.puppet.is_some() ? (*mdl.puppet)->anims.len() : usize());
    return true;
}

Option<wpscene::Material> MdlParser::ParseMaterial(ref<str> material_ref, fs::VFS& vfs) {
    const auto path   = ResolveMdlMaterialPath(rstd::cppstd::as_string_view(material_ref));
    auto       parsed = owe::ReadNJsonFile(vfs, path, { .allow_comments = true });
    if (parsed.is_err()) {
        auto error = rstd::move(parsed).unwrap_err_unchecked();
        rstd_error("load mdl material '{}' failed: {}", path, error.message.as_str());
        return None();
    }
    auto json = rstd::move(parsed).unwrap_unchecked();

    wpscene::Material material;
    material.blending   = "disabled";
    material.depthtest  = "enabled";
    material.depthwrite = "enabled";
    material.cullmode   = "back";
    if (! material.FromJson(json)) {
        rstd_error("parse mdl material '{}' failed", path);
        return None();
    }
    return Some(rstd::move(material));
}

Option<usize> MdlParser::FindMeshByMaterial(const Mdl& mdl, ref<str> material_ref) {
    const auto wanted = ResolveMdlMaterialPath(rstd::cppstd::as_string_view(material_ref));
    for (usize mesh_index {}; mesh_index < mdl.meshes.len(); ++mesh_index) {
        for (const auto& candidate : mdl.meshes[mesh_index].mat_json_files) {
            if (ResolveMdlMaterialPath(rstd::cppstd::as_string_view(candidate.as_str())) == wanted)
                return Some(mesh_index);
        }
    }
    return None();
}

void MdlParser::GenMeshFromMdl(SceneMesh::Submesh& submesh, const Mdl::Mesh& src,
                               array<float, 2> texcoord_scale, array<float, 3> position_offset) {
    const size_t vert_num = src.positions.len().to_primitive();
    if (vert_num == 0) return;
    if (! src.part_uv2.is_empty() || ! src.parts.is_empty()) {
        // V21+ part meshes already store primary UVs in backing-texture space.
        texcoord_scale = { 1.0f, 1.0f };
    }

    // Build the attribute list in a stable order. Skinning attrs come early so
    // a puppet vertex layout matches what WE shaders historically expect.
    std::vector<VertexAttrSpec>                      specs;
    std::vector<std::function<void(size_t, float*)>> packers;

    // Position is always present (the parser would have failed otherwise).
    specs.push_back(VAttr::Position);
    packers.push_back([&src, position_offset](size_t i, float* dst) {
        dst[0] = src.positions[usize(i)][usize(0)] + position_offset[usize(0)];
        dst[1] = src.positions[usize(i)][usize(1)] + position_offset[usize(1)];
        dst[2] = src.positions[usize(i)][usize(2)] + position_offset[usize(2)];
    });
    if (! src.normals.is_empty()) {
        specs.push_back(VAttr::Normal);
        packers.push_back([&src](size_t i, float* dst) {
            std::memcpy(dst, src.normals[usize(i)].data(), sizeof(src.normals[usize(i)]));
        });
    }
    if (! src.tangents.is_empty()) {
        specs.push_back(VAttr::Tangent4);
        packers.push_back([&src](size_t i, float* dst) {
            std::memcpy(dst, src.tangents[usize(i)].data(), sizeof(src.tangents[usize(i)]));
        });
    }
    if (! src.blend_indices.is_empty()) {
        specs.push_back(VAttr::BlendIndices);
        packers.push_back([&src](size_t i, float* dst) {
            std::memcpy(
                dst, src.blend_indices[usize(i)].data(), sizeof(src.blend_indices[usize(i)]));
        });
        // SKIN_BLEND without SKIN_WEIGHT is the WE 1-bone rigid convention;
        // emit synthetic [1,0,0,0] so the SKINNING shader path always has
        // valid weights to read.
        specs.push_back(VAttr::BlendWeights);
        const bool has_w = ! src.blend_weights.is_empty();
        packers.push_back([&src, has_w](size_t i, float* dst) {
            if (has_w) {
                std::memcpy(
                    dst, src.blend_weights[usize(i)].data(), sizeof(src.blend_weights[usize(i)]));
            } else {
                dst[0] = 1.0f;
                dst[1] = 0.0f;
                dst[2] = 0.0f;
                dst[3] = 0.0f;
            }
        });
    }
    if (! src.texcoords.is_empty()) {
        specs.push_back(VAttr::TexCoord);
        packers.push_back([&src, texcoord_scale](size_t i, float* dst) {
            dst[0] = src.texcoords[usize(i)][usize(0)] * texcoord_scale[usize(0)];
            dst[1] = src.texcoords[usize(i)][usize(1)] * texcoord_scale[usize(1)];
        });
    }
    const auto* uv2 = ! src.part_uv2.is_empty()    ? &src.part_uv2
                      : ! src.texcoord2.is_empty() ? &src.texcoord2
                                                   : nullptr;
    if (! src.texcoords.is_empty() && uv2 != nullptr && uv2->len() == usize(vert_num)) {
        specs.push_back(VAttr::TexCoordVec4);
        packers.push_back([&src, uv2, texcoord_scale](size_t i, float* dst) {
            dst[0] = src.texcoords[usize(i)][usize(0)] * texcoord_scale[usize(0)];
            dst[1] = src.texcoords[usize(i)][usize(1)] * texcoord_scale[usize(1)];
            dst[2] = (*uv2)[usize(i)][usize(0)];
            dst[3] = (*uv2)[usize(i)][usize(1)];
        });
    }

    auto             attrs = MakeAttrSet(specs);
    SceneVertexArray vertex(attrs, usize(vert_num));

    size_t stride_floats = 0;
    for (auto& a : attrs) stride_floats += SceneVertexArray::RealAttributeSize(a);
    std::vector<float> one_vert(stride_floats);

    for (size_t i = 0; i < vert_num; ++i) {
        size_t offset = 0;
        for (size_t k = 0; k < packers.size(); ++k) {
            packers[k](i, one_vert.data() + offset);
            offset += SceneVertexArray::RealAttributeSize(attrs[k]);
        }
        vertex.SetVertexs(
            usize(i), rstd::slice<float>::from_raw_parts(one_vert.data(), usize(one_vert.size())));
    }

    std::vector<uint32_t> indices;
    indices.reserve(src.indices.len().to_primitive() * 3);
    for (const auto& tri : src.indices) {
        for (uint32_t v : tri) indices.push_back(v);
    }

    submesh.vertex_arrays.emplace_back(std::move(vertex));
    submesh.index_arrays.emplace_back(
        SceneIndexArray(slice<uint32_t>::from_raw_parts(indices.data(), usize(indices.size()))));

    // V21 parts[] enumerates index sub-ranges in artist-chosen z-order. We
    // issue one DrawIndexed per range so each "part" is drawn as a separate
    // primitive batch, which lets later parts overdraw earlier ones (eyelid
    // covering pupil at peak blink) and leaves headroom for per-part state.
    if (! src.parts.is_empty()) {
        submesh.draw_ranges.reserve(src.parts.len().to_primitive());
        for (const auto& p : src.parts) {
            if (p.size == 0) continue;
            submesh.draw_ranges.push_back({ u32(p.start), u32(p.size) });
        }
    }
}

void MdlParser::GenMaskSubmeshFromMdl(SceneMesh::Submesh& submesh, const Mdl::Mesh& src,
                                      slice<uint32_t> clip_part_indices,
                                      array<float, 2> texcoord_scale) {
    GenMeshFromMdl(submesh, src, texcoord_scale);
    // `clip_part_indices` are positions in src.parts[] (0-based), not `part.id`.
    std::vector<SceneMesh::DrawRange> ranges;
    for (usize i {}; i < clip_part_indices.len(); ++i) {
        const uint32_t idx = clip_part_indices[i];
        if (idx >= src.parts.len().to_primitive()) continue;
        const auto& p = src.parts[usize(idx)];
        if (p.size == 0) continue;
        ranges.push_back({ u32(p.start), u32(p.size) });
    }
    submesh.draw_ranges = std::move(ranges);
}

void MdlParser::AddPuppetShaderInfo(ShaderInfo& info, const Mdl& mdl) {
    info.combos[rstd::cppstd::to_string(WE_CB_SKINNING)] = "1";
    info.combos[rstd::cppstd::to_string(WE_CB_BONECOUNT)] =
        std::to_string((*mdl.puppet)->bones.len().to_primitive());
}

void MdlParser::AddPuppetMatInfo(wpscene::Material& mat, const Mdl& mdl) {
    mat.combos[rstd::cppstd::to_string(WE_CB_SKINNING)] = i32(1);
    mat.combos[rstd::cppstd::to_string(WE_CB_BONECOUNT)] =
        rstd::as_cast<i32>((*mdl.puppet)->bones.len());
    mat.use_puppet = true;
}
