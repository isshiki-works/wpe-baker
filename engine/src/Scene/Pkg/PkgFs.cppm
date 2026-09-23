module;

export module wescene.pkg_fs;
import wescene.core;
import rstd;
import rstd.cppstd;

export import wescene.fs;

using ::alloc::collections::HashMap;
using ::alloc::string::String;
using namespace rstd::prelude;

export namespace owe::fs
{

class PkgMount;

class WPPkgFs {
public:
    WPPkgFs(const WPPkgFs&)                        = delete;
    auto operator=(const WPPkgFs&) -> WPPkgFs&     = delete;
    WPPkgFs(WPPkgFs&&) noexcept                    = default;
    auto operator=(WPPkgFs&&) noexcept -> WPPkgFs& = default;

    static auto open(Path pkg_path) -> Result<PkgMount>;

    auto open_read(Path path) const -> Result<ReadRange>;
    auto metadata(Path path) const -> Result<FileMetadata>;

private:
    struct PkgFile {
        std::uint64_t offset { 0 };
        std::uint64_t length { 0 };
    };

    WPPkgFs(ReadRange source, String version, HashMap<String, PkgFile> files)
        : m_source(rstd::move(source)),
          m_pkg_version(rstd::move(version)),
          m_files(rstd::move(files)) {}

    ReadRange                m_source;
    String                   m_pkg_version;
    HashMap<String, PkgFile> m_files;
};

class PkgMount {
public:
    PkgMount(const PkgMount&)                        = delete;
    auto operator=(const PkgMount&) -> PkgMount&     = delete;
    PkgMount(PkgMount&&) noexcept                    = default;
    auto operator=(PkgMount&&) noexcept -> PkgMount& = default;

    auto mount_handle() const -> MountHandle { return m_mount.clone(); }
    auto open_read(Path path) const -> Result<ReadRange> { return m_mount->open_read(path); }
    auto pkg_version_stamp() const noexcept -> rstd::ref<rstd::str> {
        return m_pkg_version.as_str();
    }

private:
    friend class WPPkgFs;

    PkgMount(MountHandle mount, String version)
        : m_mount(rstd::move(mount)), m_pkg_version(rstd::move(version)) {}

    MountHandle m_mount;
    String      m_pkg_version;
};

} // namespace owe::fs

namespace rstd
{

template<>
struct Impl<owe::fs::MountFs, owe::fs::WPPkgFs> : ImplBase<owe::fs::WPPkgFs> {
    auto open_read(owe::fs::Path path) const -> owe::io::Result<owe::io::ReadRange> {
        return this->self().open_read(path);
    }

    auto metadata(owe::fs::Path path) const -> owe::io::Result<owe::fs::FileMetadata> {
        return this->self().metadata(path);
    }
};

} // namespace rstd
