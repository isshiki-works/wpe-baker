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

// 非法 UTF-8 照旧 abort（原实现 as_str(...).unwrap()）。
inline Path ToPath(std::string_view path) { return Path(rstd::cppstd::as_str(path).unwrap()); }

inline std::string ToStdString(Path path) { return std::string(path.as_str()); }

inline auto ResolveAssetPath(std::string_view path) -> Result<PathBuf> {
    return resolve_beneath(ToPath("/assets"), ToPath(path));
}

inline auto OpenBinary(VFS& vfs, Path path) -> Result<BinaryReader> {
    auto range = rstd_try(vfs.open_read(path));
    return Ok(BinaryReader(rstd::move(range)));
}

inline auto OpenBinary(VFS& vfs, std::string_view path) -> Result<BinaryReader> {
    return OpenBinary(vfs, ToPath(path));
}

inline auto OpenPhysicalBinary(std::string_view path) -> Result<BinaryReader> {
    auto range = rstd_try(owe::io::open_file_range(ToPath(path).as_str()));
    return Ok(BinaryReader(rstd::move(range)));
}

inline auto ReadFileContent(VFS& vfs, Path path) -> Result<std::string> {
    auto reader = rstd_try(OpenBinary(vfs, path));
    return reader.read_all_string();
}

inline auto ReadFileContent(VFS& vfs, std::string_view path) -> Result<std::string> {
    return ReadFileContent(vfs, ToPath(path));
}

} // namespace owe::fs
