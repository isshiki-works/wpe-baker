module;

#include <rstd/macro.hpp>

export module wescene.fs;
export import wescene.io;
export import wescene.vfs;
import rstd;
import rstd.cppstd;

using namespace rstd::prelude;

export namespace owe::fs
{

using BinaryReader = owe::io::BinaryReader;
using BinaryWriter = owe::io::BinaryWriter;

inline Path ToPath(std::string_view path) { return Path(rstd::cppstd::as_str(path).unwrap()); }

inline std::string ToStdString(Path path) {
    return rstd::cppstd::to_string(path.as_os_str().to_str().unwrap());
}

inline auto ResolveAssetPath(std::string_view path) -> rstd::io::Result<rstd::path::PathBuf> {
    return resolve_beneath(ToPath("/assets"), ToPath(path));
}

inline auto OpenBinary(VFS& vfs, Path path) -> rstd::io::Result<BinaryReader> {
    auto range = rstd_try(vfs.open_read(path));
    return Ok(BinaryReader(rstd::move(range)));
}

inline auto OpenBinary(VFS& vfs, std::string_view path) -> rstd::io::Result<BinaryReader> {
    return OpenBinary(vfs, ToPath(path));
}

inline auto OpenBinaryWriter(VFS& vfs, std::string_view path, WriteOptions options)
    -> rstd::io::Result<BinaryWriter> {
    auto handle = rstd_try(vfs.open_write(ToPath(path), options));
    return Ok(BinaryWriter(rstd::move(handle)));
}

inline auto OpenPhysicalBinary(std::string_view path) -> rstd::io::Result<BinaryReader> {
    auto opened   = rstd_try(rstd::fs::File::open(ToPath(path)));
    auto metadata = rstd_try(opened.metadata());
    auto source   = rstd::io::SharedReadAt::make(rstd::move(opened));
    auto range    = rstd_try(rstd::io::ReadRange::make(rstd::move(source), u64(), metadata.len()));
    return Ok(BinaryReader(rstd::move(range)));
}

inline auto ReadFileContent(VFS& vfs, Path path) -> rstd::io::Result<std::string> {
    auto reader = rstd_try(OpenBinary(vfs, path));
    return reader.read_all_string();
}

inline auto ReadFileContent(VFS& vfs, std::string_view path) -> rstd::io::Result<std::string> {
    return ReadFileContent(vfs, ToPath(path));
}

} // namespace owe::fs
