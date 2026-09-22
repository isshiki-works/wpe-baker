#include <algorithm>

#include <gtest/gtest.h>

import rstd;
import rstd.cppstd;
import wescene.fs;
import wescene.pkg_fs;

using namespace rstd::prelude;
using namespace rstd::literals;

namespace
{

auto FsError(owe::io::ErrorKind kind) -> owe::io::Error { return owe::io::Error::from_kind(kind); }

class MemorySource final : public owe::io::ReadAt {
public:
    explicit MemorySource(std::string data): m_data(std::move(data)) {}

    auto read_at(std::uint8_t* buffer, std::size_t size, std::uint64_t offset) const
        -> owe::io::Result<std::size_t> override {
        if (offset >= m_data.size()) return rstd::Ok(std::size_t {});
        auto count = std::min<std::size_t>(size, m_data.size() - offset);
        std::memcpy(buffer, m_data.data() + offset, count);
        return rstd::Ok(count);
    }

private:
    std::string m_data;
};

} // namespace

namespace
{

class TestMount {
public:
    explicit TestMount(std::unordered_map<std::string, std::string> files,
                       std::string                                  invalid_path = {})
        : m_files(std::move(files)), m_invalid_path(std::move(invalid_path)) {}

    auto open_read(owe::fs::Path path) const -> owe::io::Result<owe::fs::ReadRange> {
        auto key = owe::fs::ToStdString(path);
        if (key == m_invalid_path) {
            return rstd::Err(FsError(owe::io::ErrorKind::InvalidData));
        }
        auto file = m_files.find(key);
        if (file == m_files.end()) {
            return rstd::Err(FsError(owe::io::ErrorKind::NotFound));
        }
        return owe::fs::ReadRange::make(
            std::make_shared<MemorySource>(file->second), 0, file->second.size());
    }

    auto metadata(owe::fs::Path path) const -> owe::io::Result<owe::fs::FileMetadata> {
        auto key  = owe::fs::ToStdString(path);
        auto file = m_files.find(key);
        if (file == m_files.end()) {
            return rstd::Err(FsError(owe::io::ErrorKind::NotFound));
        }
        return rstd::Ok(owe::fs::FileMetadata {
            .len          = u64(file->second.size()),
            .is_file      = true,
            .is_directory = false,
            .readonly     = true,
        });
    }

private:
    std::unordered_map<std::string, std::string> m_files;
    std::string                                  m_invalid_path;
};

auto MakeMount(std::unordered_map<std::string, std::string> files, std::string invalid_path = {})
    -> owe::fs::MountHandle;

auto ReadText(owe::fs::ReadRange range) -> std::string {
    owe::fs::BinaryReader reader(std::move(range));
    return reader.ReadAllStr();
}

void WriteI32(std::ofstream& output, std::int32_t value) {
    output.write(reinterpret_cast<const char*>(&value), sizeof(value));
}

void WriteSized(std::ofstream& output, std::string_view value) {
    WriteI32(output, static_cast<std::int32_t>(value.size()));
    output.write(value.data(), static_cast<std::streamsize>(value.size()));
}

void WritePkg(const std::filesystem::path& path, std::int32_t body_length,
              std::string_view entry_path = "Materials/Foo.bin") {
    std::ofstream output(path, std::ios::binary);
    WriteSized(output, "PKGV0001");
    WriteI32(output, 1);
    WriteSized(output, entry_path);
    WriteI32(output, 0);
    WriteI32(output, body_length);
    output.write("hello", 5);
}

class TempDirectory {
public:
    TempDirectory() {
        auto suffix = std::chrono::steady_clock::now().time_since_epoch().count();
        path = std::filesystem::temp_directory_path() / ("owe-vfs-test-" + std::to_string(suffix));
        std::filesystem::create_directories(path);
    }

    ~TempDirectory() { std::filesystem::remove_all(path); }

    std::filesystem::path path;
};

} // namespace

template<>
struct rstd::Impl<owe::fs::MountFs, TestMount> : rstd::ImplBase<TestMount> {
    auto open_read(owe::fs::Path path) const -> owe::io::Result<owe::fs::ReadRange> {
        return this->self().open_read(path);
    }

    auto metadata(owe::fs::Path path) const -> owe::io::Result<owe::fs::FileMetadata> {
        return this->self().metadata(path);
    }
};

namespace
{

auto MakeMount(std::unordered_map<std::string, std::string> files, std::string invalid_path)
    -> owe::fs::MountHandle {
    return owe::fs::MountHandle::make(TestMount(std::move(files), std::move(invalid_path)));
}

TEST(Vfs, OverlayAndUnmountKeepOpenedRangeAlive) {
    owe::fs::VFS vfs;
    ASSERT_TRUE(vfs.mount("/assets"_str, MakeMount({ { "shared", "lower" } })).is_ok());
    auto upper = vfs.mount("/assets"_str, MakeMount({ { "shared", "upper" } }));
    ASSERT_TRUE(upper.is_ok());

    auto opened = vfs.open_read("/assets/shared"_str);
    ASSERT_TRUE(opened.is_ok());
    auto retained = std::move(opened).unwrap_unchecked();

    EXPECT_TRUE(vfs.unmount(*upper));
    auto visible = vfs.open_read("/assets/shared"_str);
    ASSERT_TRUE(visible.is_ok());
    EXPECT_EQ(ReadText(std::move(visible).unwrap_unchecked()), "lower");
    EXPECT_EQ(ReadText(std::move(retained)), "upper");
}

TEST(Vfs, BackendErrorsAreNotOverlayMisses) {
    owe::fs::VFS vfs;
    ASSERT_TRUE(vfs.mount("/assets"_str, MakeMount({ { "broken", "lower" } })).is_ok());
    ASSERT_TRUE(vfs.mount("/assets"_str, MakeMount({}, "broken")).is_ok());

    auto opened = vfs.open_read("/assets/broken"_str);
    ASSERT_TRUE(opened.is_err());
    EXPECT_TRUE(std::move(opened).unwrap_err_unchecked().kind() == owe::io::ErrorKind::InvalidData);
}

TEST(BinaryReader, CompletesReadsAcrossBufferedBoundary) {
    std::string bytes(8195, '\0');
    bytes[8191] = '\x78';
    bytes[8192] = '\x56';
    bytes[8193] = '\x34';
    bytes[8194] = '\x12';

    auto range = owe::io::ReadRange::make(std::make_shared<MemorySource>(std::move(bytes)), 0, 8195);
    ASSERT_TRUE(range.is_ok());
    owe::fs::BinaryReader reader(std::move(range).unwrap_unchecked());

    std::string prefix(8191, '\0');
    EXPECT_EQ(reader.Read(prefix.data(), prefix.size()), prefix.size());
    EXPECT_EQ(reader.ReadUint32(), 0x12345678u);
}

TEST(Vfs, PathsUseComponentBoundariesAndRejectTraversal) {
    owe::fs::VFS vfs;
    ASSERT_TRUE(vfs.mount("/asset"_str, MakeMount({ { "file", "value" } })).is_ok());

    EXPECT_TRUE(vfs.open_read("/assets/file"_str).is_err());
    auto invalid = vfs.open_read("/asset/../file"_str);
    ASSERT_TRUE(invalid.is_err());
    EXPECT_TRUE(std::move(invalid).unwrap_err_unchecked().kind() == owe::io::ErrorKind::InvalidInput);
    EXPECT_TRUE(vfs.mount("asset"_str, MakeMount({})).is_err());
}

TEST(PkgFs, ReusesHeaderAndRejectsInvalidEntryRanges) {
    TempDirectory temp;
    auto          valid_path = temp.path / "valid.pkg";
    WritePkg(valid_path, 5);

    auto pkg = owe::fs::WPPkgFs::open(owe::fs::ToPath(valid_path.string()));
    ASSERT_TRUE(pkg.is_ok());
    auto stamp = pkg->pkg_version_stamp();
    EXPECT_EQ(std::string(reinterpret_cast<const char*>(stamp.data()), stamp.len().to_primitive()),
              "PKGV0001");

    auto source = pkg->open_read(owe::fs::ToPath("/materials/foo.BIN"));
    ASSERT_TRUE(source.is_ok());
    EXPECT_EQ(ReadText(std::move(source).unwrap_unchecked()), "hello");

    auto invalid_path = temp.path / "invalid.pkg";
    WritePkg(invalid_path, 100);
    auto invalid = owe::fs::WPPkgFs::open(owe::fs::ToPath(invalid_path.string()));
    ASSERT_TRUE(invalid.is_err());
    EXPECT_TRUE(std::move(invalid).unwrap_err_unchecked().kind() == owe::io::ErrorKind::InvalidData);
}

TEST(PkgFs, PreservesUtf8WhileFoldingAsciiPathCase) {
    TempDirectory temp;
    auto          path = temp.path / "utf8.pkg";
    WritePkg(path, 5, "Materials/École/贴图.BIN");

    auto pkg = owe::fs::WPPkgFs::open(owe::fs::ToPath(path.string()));
    ASSERT_TRUE(pkg.is_ok());
    auto source = pkg->open_read(owe::fs::ToPath("/materials/École/贴图.bin"));
    ASSERT_TRUE(source.is_ok());
    EXPECT_EQ(ReadText(std::move(source).unwrap_unchecked()), "hello");
}

TEST(PkgFs, ResolvesAuthoredParentPathsInsideAssetRoot) {
    TempDirectory temp;
    auto          path = temp.path / "parent.pkg";
    WritePkg(path, 5, "../海景画/particles/snow.json");

    auto pkg = owe::fs::WPPkgFs::open(owe::fs::ToPath(path.string()));
    ASSERT_TRUE(pkg.is_ok());

    owe::fs::VFS vfs;
    ASSERT_TRUE(vfs.mount("/assets"_str, pkg->mount_handle()).is_ok());
    auto asset = owe::fs::ResolveAssetPath("../海景画/particles/snow.json");
    ASSERT_TRUE(asset.is_ok());
    auto asset_path = owe::fs::ToStdString(asset->as_path());
    std::replace(asset_path.begin(), asset_path.end(), '\\', '/');
    EXPECT_EQ(asset_path, "/assets/海景画/particles/snow.json");

    auto source = vfs.open_read(asset->as_path());
    ASSERT_TRUE(source.is_ok());
    EXPECT_EQ(ReadText(std::move(source).unwrap_unchecked()), "hello");
}

} // namespace
