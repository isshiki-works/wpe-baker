#include <owe/compat.hpp>
#include <owe/std.hpp>
#include <gtest/gtest.h>

#include <bit>
#include <filesystem>
#include <fstream>
#include <vector>

import eigen;
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
    if (terminated) bytes.push_back(0);
    FinishMdlBlock(bytes, end_field);
    if (! terminated) bytes.push_back(0); // must not be consumed beyond MDLA's boundary
    return bytes;
}

bool ParseMdlBytes(const std::vector<std::uint8_t>& bytes, owe::OfflineExecutionContext& context,
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
                owe::OfflineExecutionScope scope(context);
                owe::Mdl                   local_mdl;
                auto&                      mdl = parsed_mdl == nullptr ? local_mdl : *parsed_mdl;
                parsed                         = owe::MdlParser::Parse("fixture.mdl"_str, vfs, mdl);
            }
        }
    }
    std::filesystem::remove(file);
    std::filesystem::remove(root);
    return parsed;
}

bool HasDiagnostic(const owe::OfflineExecutionContext& context, std::string_view text) {
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

} // namespace

TEST(MdlParser, AcceptsKnownEmptyMdlsMetadata) {
    auto bytes = MdlPrefix();
    AppendEmptyMdls(bytes);
    owe::OfflineExecutionContext context;
    EXPECT_TRUE(ParseMdlBytes(bytes, context));
    EXPECT_FALSE(context.failed);
}

TEST(MdlParser, DistinguishesMissingFileFromParseFailure) {
    owe::fs::VFS vfs;
    owe::Mdl     mdl;
    bool         missing = false;
    EXPECT_FALSE(owe::MdlParser::Parse("missing-puppet.mdl"_str, vfs, mdl, &missing));
    EXPECT_TRUE(missing);
}

TEST(MdlParser, RejectsTruncatedMdlsIkControllerTable) {
    auto bytes = MdlPrefix();
    AppendEmptyMdls(bytes, 8);
    owe::OfflineExecutionContext context;
    EXPECT_FALSE(ParseMdlBytes(bytes, context));
    EXPECT_TRUE(context.failed);
    EXPECT_TRUE(HasDiagnostic(context, "fixture.mdl"));
    EXPECT_TRUE(HasDiagnostic(context, "truncated MDLS IK controller table"));
}

TEST(MdlParser, ParsesSupportedIkRigAndControllerTracks) {
    auto                         bytes = MdlWithIkRig();
    owe::OfflineExecutionContext context;
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
    owe::OfflineExecutionContext context;
    EXPECT_FALSE(ParseMdlBytes(bytes, context));
    EXPECT_TRUE(context.failed);
    EXPECT_TRUE(HasDiagnostic(context, "invalid MDLS IK bone length"));
}

TEST(MdlParser, RejectsInvalidUtf8MaterialName) {
    auto bytes = MdlPrefix(1);
    bytes.insert(bytes.end(), { 0xff, 0 });
    owe::OfflineExecutionContext context;
    EXPECT_FALSE(ParseMdlBytes(bytes, context));
    EXPECT_TRUE(context.failed);
    EXPECT_TRUE(HasDiagnostic(context, "invalid UTF-8 in material name"));
}

TEST(MdlParser, BoundsBoneNameToMdlsBlock) {
    auto bytes     = MdlPrefix();
    auto end_field = BeginMdlBlock(bytes, "MDLS0004");
    AppendU16(bytes, 1);
    AppendU16(bytes, 0);
    bytes.insert(bytes.end(), { 'b', 'o', 'n', 'e' });
    FinishMdlBlock(bytes, end_field);
    bytes.push_back(0); // outside the declared MDLS block
    owe::OfflineExecutionContext context;
    EXPECT_FALSE(ParseMdlBytes(bytes, context));
    EXPECT_TRUE(context.failed);
    EXPECT_TRUE(HasDiagnostic(context, "unterminated bone name"));
    EXPECT_TRUE(HasDiagnostic(context, "offset=38, boundary=42"));
}

TEST(MdlParser, PropagatesInvalidUtf8PlayMode) {
    auto                         bytes = MdlWithPlayMode(std::string_view("\xff", 1), true);
    owe::OfflineExecutionContext context;
    EXPECT_FALSE(ParseMdlBytes(bytes, context));
    EXPECT_TRUE(context.failed);
    EXPECT_TRUE(HasDiagnostic(context, "invalid UTF-8 in animation play_mode"));
}

TEST(MdlParser, BoundsPlayModeToMdlaBlock) {
    auto                         bytes = MdlWithPlayMode("loop", false);
    owe::OfflineExecutionContext context;
    EXPECT_FALSE(ParseMdlBytes(bytes, context));
    EXPECT_TRUE(context.failed);
    EXPECT_TRUE(HasDiagnostic(context, "unterminated animation play_mode"));
}

TEST(MdlParser, RejectsUnsupportedPlayModeWithoutAssertion) {
    auto                         bytes = MdlWithPlayMode("unknown", true);
    owe::OfflineExecutionContext context;
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

    owe::PuppetLayer layer(puppet.clone());
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

    owe::PuppetLayer                 layer(puppet.clone());
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
    ASSERT_TRUE(owe::MdlParser::Parse("models/球体01/球体01.mdl"_str, vfs, mdl));
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
    ASSERT_TRUE(owe::MdlParser::Parse("models/prism/prism.mdl"_str, vfs, mdl));
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
    ASSERT_TRUE(owe::MdlParser::Parse("models/sheet_puppet.mdl"_str, vfs, mdl));
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
