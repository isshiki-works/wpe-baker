module;

#include <rstd/macro.hpp>

module wescene.pkg_fs;
import wescene.core;
import rstd;
import rstd.log;
import rstd.cppstd;

import wescene.fs;

using ::alloc::collections::HashMap;
using ::alloc::string::String;
using namespace owe;
using namespace owe::fs;
using namespace rstd::prelude;
using namespace rstd::literals;

namespace
{

auto FsError(owe::io::ErrorKind kind) -> owe::io::Error { return owe::io::Error::from_kind(kind); }

Option<std::string> ReadSizedString(BinaryReader& file, usize max_len) {
    auto signed_len = file.ReadInt32();
    if (signed_len < 0) return None();

    auto len = usize(static_cast<rstd::size_t>(signed_len));
    if (len > max_len) return None();
    std::string result(len.to_primitive(), '\0');
    if (file.Read(result.data(), len.to_primitive()) != len.to_primitive()) return None();
    return Some(rstd::move(result));
}

bool IsPkgVersionStamp(std::string_view stamp) {
    constexpr std::string_view prefix = "PKGV";
    return stamp.size() > prefix.size() && stamp.substr(0, prefix.size()) == prefix;
}

// 条目键：在 "/" 下解析（".." 不越出根），组件用 / 连接，只折叠 ASCII 大小写。
auto LookupKey(Path path) -> owe::fs::Result<String> {
    auto        normalized = rstd_try(resolve_beneath("/"_str, path));
    std::string key        = "/";
    auto        components = normalized.as_path().components();
    while (auto component = components.next()) {
        if (! component->is_normal()) continue;
        if (key.size() > 1) key.push_back('/');
        key += component->text;
    }
    for (auto& c : key) {
        if (c >= 'A' && c <= 'Z') c = static_cast<char>(c - 'A' + 'a');
    }
    return Ok(String::make(rstd::cppstd::as_str(key).unwrap()));
}

} // namespace

auto WPPkgFs::open(Path pkg_path) -> Result<PkgMount> {
    auto pkg_source = rstd_try(owe::io::open_file_range(pkg_path.as_str()));
    auto pkg        = BinaryReader(pkg_source.clone());

    auto version = ReadSizedString(pkg, usize(64));
    if (! version || ! IsPkgVersionStamp(*version)) {
        return Err(FsError(owe::io::ErrorKind::InvalidData));
    }
    rstd_info("pkg version: {}", *version);

    struct PendingFile {
        String        path;
        std::uint64_t offset;
        std::uint64_t length;
    };
    auto files = ::alloc::vec::Vec<PendingFile>::make();

    auto entry_count = pkg.ReadInt32();
    if (entry_count < 0) {
        return Err(FsError(owe::io::ErrorKind::InvalidData));
    }
    files.reserve(usize(entry_count));
    for (rstd::int32_t i = 0; i < entry_count; ++i) {
        auto path = ReadSizedString(pkg, usize(4096));
        if (! path) return Err(FsError(owe::io::ErrorKind::InvalidData));
        auto key = LookupKey(ToPath(*path));
        if (key.is_err()) return Err(rstd::move(key).unwrap_err_unchecked());

        auto offset = pkg.ReadInt32();
        auto length = pkg.ReadInt32();
        if (offset < 0 || length < 0) {
            return Err(FsError(owe::io::ErrorKind::InvalidData));
        }
        files.push(PendingFile { .path   = rstd::move(key).unwrap_unchecked(),
                                 .offset = static_cast<std::uint64_t>(offset),
                                 .length = static_cast<std::uint64_t>(length) });
    }

    auto header_size = pkg.position().to_primitive();
    auto entries     = HashMap<String, PkgFile>::with_capacity(files.len());
    for (auto& file : files) {
        if (file.offset > ~std::uint64_t(0) - header_size) {
            return Err(FsError(owe::io::ErrorKind::InvalidData));
        }
        auto absolute = header_size + file.offset;
        if (absolute > pkg_source.len() || file.length > pkg_source.len() - absolute) {
            return Err(FsError(owe::io::ErrorKind::InvalidData));
        }
        entries.insert(rstd::move(file.path),
                       PkgFile { .offset = absolute, .length = file.length });
    }

    auto version_string = String::make(rstd::cppstd::as_str(*version).unwrap());
    auto mount_version  = version_string.clone();
    auto mount          = MountHandle::make(
        WPPkgFs(rstd::move(pkg_source), rstd::move(version_string), rstd::move(entries)));
    return rstd::Ok(PkgMount(rstd::move(mount), rstd::move(mount_version)));
}

auto WPPkgFs::open_read(Path path) const -> Result<ReadRange> {
    auto key  = rstd_try(LookupKey(path));
    auto file = m_files.get(key);
    if (file.is_none()) return Err(FsError(owe::io::ErrorKind::NotFound));
    return m_source.subrange((*file)->offset, (*file)->length);
}

auto WPPkgFs::metadata(Path path) const -> Result<FileMetadata> {
    auto key  = rstd_try(LookupKey(path));
    auto file = m_files.get(key);
    if (file.is_none()) return Err(FsError(owe::io::ErrorKind::NotFound));
    return Ok(FileMetadata {
        .len = u64((*file)->length), .is_file = true, .is_directory = false, .readonly = true });
}
