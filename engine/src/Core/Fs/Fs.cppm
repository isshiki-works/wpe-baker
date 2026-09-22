module;
#include <rstd/macro.hpp>

export module wescene.vfs;
import rstd;
import rstd.cppstd;
export import wescene.io;

using ::alloc::string::String;
using ::alloc::sync::Arc;
using ::alloc::vec::Vec;
using namespace rstd::prelude;
using namespace rstd::literals;

export namespace owe::fs
{

using owe::io::Error;
using owe::io::ErrorKind;
using owe::io::ReadRange;
template<typename T>
using Result = owe::io::Result<T>;

// 路径的词法规则照搬 rstd path（Windows 版）：/ 与 \ 都是分隔符；"X:" 后跟分隔符或开头的分隔符
// 算作根（盘符本身不单列为组件）；"." 一律丢弃（rstd 只在开头单列 CurDir，使用方都跳过它）；拼接用 \。
// 虚拟路径（/assets/...）与物理路径共用这套规则，ToStdString 暴露的就是这个字符串。
constexpr bool is_sep(char c) noexcept { return c == '/' || c == '\\'; }

struct Component {
    enum class Kind
    {
        RootDir,
        ParentDir,
        Normal,
    } kind;
    std::string_view text;

    bool is_root_dir() const noexcept { return kind == Kind::RootDir; }
    bool is_parent_dir() const noexcept { return kind == Kind::ParentDir; }
    bool is_normal() const noexcept { return kind == Kind::Normal; }

    friend bool operator==(Component lhs, Component rhs) noexcept {
        return lhs.kind == rhs.kind && (lhs.kind != Kind::Normal || lhs.text == rhs.text);
    }
};

class Path;

class Components {
public:
    explicit Components(std::string_view text) noexcept: m_text(text) {
        auto root = root_len(text);
        if (root != 0) {
            m_root_pending = true;
            m_pos          = root;
        }
    }

    auto next() noexcept -> Option<Component> {
        if (m_root_pending) {
            m_root_pending = false;
            return Some(Component { Component::Kind::RootDir, {} });
        }
        while (true) {
            m_pos = skip_seps(m_pos);
            if (m_pos >= m_text.size()) return None();
            auto start = m_pos;
            while (m_pos < m_text.size() && ! is_sep(m_text[m_pos])) ++m_pos;
            auto part = m_text.substr(start, m_pos - start);
            if (part == ".") continue;
            if (part == "..") return Some(Component { Component::Kind::ParentDir, part });
            return Some(Component { Component::Kind::Normal, part });
        }
    }

    // 余下未迭代部分（与 rstd Components::as_path 相同，跳过开头分隔符）。
    auto as_path() const noexcept -> Path;

private:
    static auto root_len(std::string_view text) noexcept -> std::size_t {
        if (text.size() >= 3 && text[1] == ':' && is_sep(text[2])) return 3;
        return ! text.empty() && is_sep(text[0]) ? 1 : 0;
    }

    auto skip_seps(std::size_t pos) const noexcept -> std::size_t {
        while (pos < m_text.size() && is_sep(m_text[pos])) ++pos;
        return pos;
    }

    std::string_view m_text;
    std::size_t      m_pos {};
    bool             m_root_pending { false };
};

// 借用的路径视图。内容恒为合法 UTF-8：只能从 rstd str、PathBuf 或 ToPath（校验 UTF-8）得到。
class Path {
public:
    Path() = default;
    Path(rstd::ref<rstd::str> text) noexcept: m_text(rstd::cppstd::as_string_view(text)) {}

    static auto from_utf8(std::string_view text) noexcept -> Path { return Path(text); }

    auto as_str() const noexcept -> std::string_view { return m_text; }
    auto components() const noexcept -> Components { return Components(m_text); }

    // 以 base 为组件前缀时返回余下部分。
    auto strip_prefix(Path base) const noexcept -> Option<Path> {
        auto iter   = components();
        auto prefix = base.components();
        while (auto expected = prefix.next()) {
            auto value = iter.next();
            if (value.is_none() || ! (*value == *expected)) return None();
        }
        return Some(iter.as_path());
    }

    bool starts_with(Path base) const noexcept { return strip_prefix(base).is_some(); }

    // 去掉最后一个组件（同 rstd Path::parent）；根或单组件返回 None。
    auto parent() const noexcept -> Option<Path> {
        auto end = m_text.size();
        while (end > 0 && is_sep(m_text[end - 1])) --end;
        if (end == 0) return None();
        while (end > 0 && ! is_sep(m_text[end - 1])) --end;
        if (end == 0) return None();
        while (end > 1 && is_sep(m_text[end - 1])) --end;
        return Some(Path(m_text.substr(0, end)));
    }

private:
    explicit Path(std::string_view text) noexcept: m_text(text) {}

    std::string_view m_text;
};

auto Components::as_path() const noexcept -> Path {
    auto start = m_root_pending ? 0 : skip_seps(m_pos);
    return Path::from_utf8(m_text.substr(start));
}

class PathBuf {
public:
    PathBuf() = default;
    explicit PathBuf(Path path): m_text(path.as_str()) {}

    // 追加一个相对路径（调用点只传单个组件或规范化后的相对路径），缺分隔符时补 \。
    void push(Path component) {
        if (! m_text.empty() && ! is_sep(m_text.back())) m_text.push_back('\\');
        m_text += component.as_str();
    }

    bool pop() {
        auto parent = as_path().parent();
        if (parent.is_none()) return false;
        m_text.resize(parent->as_str().size());
        return true;
    }

    auto join(Path component) const -> PathBuf {
        auto result = *this;
        result.push(component);
        return result;
    }

    auto as_path() const noexcept [[clang::lifetimebound]] -> Path {
        return Path::from_utf8(m_text);
    }

private:
    std::string m_text;
};

using MountHandle = Arc<rstd::dyn<struct MountFs>>;

struct FileMetadata {
    u64  len {};
    bool is_file { false };
    bool is_directory { false };
    bool readonly { false };
};

struct MountId {
    u64 value {};

    friend bool operator==(MountId lhs, MountId rhs) noexcept { return lhs.value == rhs.value; }
};

struct MountFs {
    using Trait                  = MountFs;
    static constexpr bool direct = false;

    template<typename Self, typename Delegate = void>
    struct Api {
        using Trait = MountFs;

        auto open_read(Path path) const -> Result<ReadRange> {
            return rstd::trait_call<0>(this, path);
        }

        auto metadata(Path path) const -> Result<FileMetadata> {
            return rstd::trait_call<1>(this, path);
        }
    };

    template<typename T>
    using Funcs = rstd::TraitFuncs<&T::open_read, &T::metadata>;
};

namespace detail
{

auto error(ErrorKind kind) -> Error { return Error::from_kind(kind); }

auto normalize(Path path, bool rooted) -> Result<PathBuf> {
    auto components = path.components();
    auto output     = PathBuf();

    if (rooted) {
        auto first = components.next();
        if (first.is_none() || ! first->is_root_dir()) {
            return Err(error(ErrorKind::InvalidInput));
        }
        output = PathBuf("/"_str);
    }

    while (auto component = components.next()) {
        if (component->is_parent_dir() || component->is_root_dir()) {
            return Err(error(ErrorKind::InvalidInput));
        }
        if (! component->is_normal()) continue;
        output.push(Path::from_utf8(component->text));
    }
    return Ok(rstd::move(output));
}

auto normalize_global(Path path) -> Result<PathBuf> { return normalize(path, true); }

auto normalize_relative(Path path) -> Result<PathBuf> { return normalize(path, false); }

bool is_mount_point(Path path) {
    auto components = path.components();
    auto root       = components.next();
    return root.is_some() && root->is_root_dir() && components.next().is_some();
}

bool is_not_found(const Error& error) { return error.kind() == ErrorKind::NotFound; }

} // namespace detail

auto resolve_beneath(Path root, Path path) -> Result<PathBuf> {
    auto output     = rstd_try(detail::normalize_global(root));
    auto components = path.components();
    usize depth {};

    while (auto component = components.next()) {
        if (component->is_parent_dir()) {
            if (depth != usize()) {
                output.pop();
                --depth;
            }
            continue;
        }
        if (! component->is_normal()) continue;
        output.push(Path::from_utf8(component->text));
        ++depth;
    }
    return Ok(rstd::move(output));
}

class PhysicalFs {
public:
    PhysicalFs(const PhysicalFs&)                        = delete;
    auto operator=(const PhysicalFs&) -> PhysicalFs&     = delete;
    PhysicalFs(PhysicalFs&&) noexcept                    = default;
    auto operator=(PhysicalFs&&) noexcept -> PhysicalFs& = default;

    static auto make(Path root) -> Result<PhysicalFs> {
        auto info = rstd_try(owe::io::path_info(root.as_str()));
        if (! info.is_dir) return Err(detail::error(ErrorKind::NotADirectory));
        return Ok(PhysicalFs(rstd_try(owe::io::canonicalize(root.as_str()))));
    }

    auto open_read(Path path) const -> Result<ReadRange> {
        auto resolved = rstd_try(resolve_existing(path));
        auto opened   = rstd_try(owe::io::File::open(resolved));
        auto info     = rstd_try(opened->info());
        return ReadRange::make(rstd::move(opened), 0, info.len);
    }

    auto metadata(Path path) const -> Result<FileMetadata> {
        auto resolved = rstd_try(resolve_existing(path));
        auto value    = rstd_try(owe::io::path_info(resolved));
        return Ok(FileMetadata { .len          = u64(value.len),
                                 .is_file      = ! value.is_dir,
                                 .is_directory = value.is_dir,
                                 .readonly     = value.readonly });
    }

private:
    explicit PhysicalFs(std::string root): m_root(PathBuf(Path::from_utf8(root))) {}

    // 规范化后必须仍在根目录之下（组件前缀比较），否则 PermissionDenied。
    auto resolve_existing(Path path) const -> Result<std::string> {
        auto local     = rstd_try(detail::normalize_relative(path));
        auto full      = m_root.join(local.as_path());
        auto canonical = rstd_try(owe::io::canonicalize(full.as_path().as_str()));
        if (! Path::from_utf8(canonical).starts_with(m_root.as_path())) {
            return Err(detail::error(ErrorKind::PermissionDenied));
        }
        return Ok(rstd::move(canonical));
    }

    PathBuf m_root;
};

class VFS {
public:
    VFS(): m_state(State { .mounts = Vec<MountedFs>::make(), .next_id = u64(1) }) {}

    VFS(const VFS&)                    = delete;
    auto operator=(const VFS&) -> VFS& = delete;

    auto mount(Path mount_point, MountHandle fs, rstd::ref<rstd::str> name = {})
        -> Result<MountId> {
        auto normalized = rstd_try(detail::normalize_global(mount_point));
        if (! detail::is_mount_point(normalized.as_path())) {
            return Err(detail::error(ErrorKind::InvalidInput));
        }

        auto state = m_state.lock().unwrap_unchecked();
        if (state->next_id == u64::MAX) {
            return Err(detail::error(ErrorKind::Other));
        }
        auto id = MountId { state->next_id++ };
        state->mounts.push(MountedFs { .id          = id,
                                       .name        = String::make(name),
                                       .mount_point = rstd::move(normalized),
                                       .fs          = rstd::move(fs) });
        return Ok(id);
    }

    bool unmount(MountId id) {
        auto state = m_state.lock().unwrap_unchecked();
        for (usize i = state->mounts.len(); i > usize(); --i) {
            if (state->mounts[i - usize(1)].id == id) {
                state->mounts.remove(i - usize(1));
                return true;
            }
        }
        return false;
    }

    bool is_mounted(rstd::ref<rstd::str> name) const {
        auto state = m_state.lock().unwrap_unchecked();
        for (usize i {}; i < state->mounts.len(); ++i) {
            if (state->mounts[i].name == name) return true;
        }
        return false;
    }

    auto open_read(Path path) const -> Result<ReadRange> {
        auto normalized = rstd_try(detail::normalize_global(path));
        auto mounts     = snapshot();
        for (usize i = mounts.len(); i > usize(); --i) {
            auto& mount = mounts[i - usize(1)];
            auto  local = normalized.as_path().strip_prefix(mount.mount_point.as_path());
            if (local.is_none()) continue;
            auto opened = mount.fs->open_read(*local);
            if (opened.is_ok()) return opened;
            auto error = rstd::move(opened).unwrap_err_unchecked();
            if (! detail::is_not_found(error)) return Err(rstd::move(error));
        }
        return Err(detail::error(ErrorKind::NotFound));
    }

    auto metadata(Path path) const -> Result<FileMetadata> {
        auto normalized = rstd_try(detail::normalize_global(path));
        auto mounts     = snapshot();
        for (usize i = mounts.len(); i > usize(); --i) {
            auto& mount = mounts[i - usize(1)];
            auto  local = normalized.as_path().strip_prefix(mount.mount_point.as_path());
            if (local.is_none()) continue;
            auto result = mount.fs->metadata(*local);
            if (result.is_ok()) return result;
            auto error = rstd::move(result).unwrap_err_unchecked();
            if (! detail::is_not_found(error)) return Err(rstd::move(error));
        }
        return Err(detail::error(ErrorKind::NotFound));
    }

private:
    struct MountedFs {
        MountId     id;
        String      name;
        PathBuf     mount_point;
        MountHandle fs;

        auto clone() const -> MountedFs {
            return MountedFs { .id = id, .name = name.clone(), .mount_point = mount_point, .fs = fs.clone() };
        }
    };

    struct State {
        Vec<MountedFs> mounts;
        u64            next_id;
    };

    auto snapshot() const -> Vec<MountedFs> {
        auto state  = m_state.lock().unwrap_unchecked();
        auto result = Vec<MountedFs>::with_capacity(state->mounts.len());
        for (usize i {}; i < state->mounts.len(); ++i) {
            result.push(state->mounts[i].clone());
        }
        return result;
    }

    rstd::sync::Mutex<State> m_state;
};

} // namespace owe::fs

namespace rstd
{

template<>
struct Impl<owe::fs::MountFs, owe::fs::PhysicalFs> : ImplBase<owe::fs::PhysicalFs> {
    auto open_read(owe::fs::Path path) const -> owe::io::Result<owe::io::ReadRange> {
        return this->self().open_read(path);
    }

    auto metadata(owe::fs::Path path) const -> owe::io::Result<owe::fs::FileMetadata> {
        return this->self().metadata(path);
    }
};

} // namespace rstd

export namespace owe::fs
{

auto make_physical_fs(Path root) -> Result<MountHandle> {
    auto fs = PhysicalFs::make(root);
    if (fs.is_err()) return Err(rstd::move(fs).unwrap_err_unchecked());
    return Ok(MountHandle::make(rstd::move(fs).unwrap_unchecked()));
}

} // namespace owe::fs
