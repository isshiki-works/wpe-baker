module;
// 先让全局对齐分配声明可见，避免 Clang 合成第二个 operator new 重载（同 types.cppm）。
#include <new>
#include <rstd/macro.hpp>

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>

export module wescene.io;
import rstd;
import rstd.cppstd;

using ::alloc::vec::Vec;
using namespace rstd::prelude;

// 文件句柄与二进制读取（T2 Fs/IO 块：替换 rstd::io/fs）。
// 只实现现有调用点用到的操作；错误种类、Windows 路径转换与读语义照搬 rstd。
export namespace owe::io
{

// rstd::io::ErrorKind 中本仓库实际产生的子集，显示文字与 rstd 一致。
enum class ErrorKind : std::uint8_t
{
    NotFound,
    PermissionDenied,
    NotADirectory,
    InvalidInput,
    InvalidData,
    UnexpectedEof,
    Other,
    Uncategorized,
};

class Error {
public:
    static auto from_kind(ErrorKind kind) noexcept -> Error { return Error(kind, -1); }

    // Windows 错误码到种类：只保留行为依赖的 NotFound（VFS 叠加查找）与探针能测到的 PermissionDenied；
    // 其余码显示为 uncategorized（rstd 对 87/13/206 另有名字，只影响日志文字）。
    static auto from_os(DWORD code) noexcept -> Error {
        auto kind = ErrorKind::Uncategorized;
        switch (code) {
        case ERROR_FILE_NOT_FOUND:
        case ERROR_PATH_NOT_FOUND: kind = ErrorKind::NotFound; break;
        case ERROR_ACCESS_DENIED: kind = ErrorKind::PermissionDenied; break;
        default: break;
        }
        return Error(kind, static_cast<std::int32_t>(code));
    }

    static auto last_os_error() noexcept -> Error { return from_os(GetLastError()); }

    auto kind() const noexcept -> ErrorKind { return m_kind; }

    // rstd 的 Display：种类文字，OS 错误再加 " (os error N)"。
    auto to_string() const -> std::string {
        std::string text;
        switch (m_kind) {
        case ErrorKind::NotFound: text = "entity not found"; break;
        case ErrorKind::PermissionDenied: text = "permission denied"; break;
        case ErrorKind::NotADirectory: text = "not a directory"; break;
        case ErrorKind::InvalidInput: text = "invalid input parameter"; break;
        case ErrorKind::InvalidData: text = "invalid data"; break;
        case ErrorKind::UnexpectedEof: text = "unexpected end of file"; break;
        case ErrorKind::Other: text = "other error"; break;
        case ErrorKind::Uncategorized: text = "uncategorized error"; break;
        }
        if (m_os >= 0) text += " (os error " + std::to_string(m_os) + ")";
        return text;
    }

private:
    Error(ErrorKind kind, std::int32_t os) noexcept: m_kind(kind), m_os(os) {}

    ErrorKind    m_kind;
    std::int32_t m_os;
};

template<typename T>
using Result = rstd::Result<T, Error>;

// UTF-8 路径转宽字符，与 rstd sys/fs/windows.cpp path_wide 逐项一致：
// 空路径报 InvalidInput；
// 盘符或 UNC 绝对路径长度 >= 248 时加 \\?\ 或 \\?\UNC\ 前缀并把 / 换成 \。
auto to_wide(std::string_view path) -> Result<std::wstring> {
    if (path.empty()) {
        return Err(Error::from_kind(ErrorKind::InvalidInput));
    }
    // 输入恒为合法 UTF-8（ToPath 校验过），转换不会失败。
    auto         count    = static_cast<int>(path.size());
    auto         required = MultiByteToWideChar(CP_UTF8, 0, path.data(), count, nullptr, 0);
    std::wstring wide(static_cast<std::size_t>(required), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, path.data(), count, wide.data(), required);
    auto slash = [](wchar_t value) noexcept { return value == L'\\' || value == L'/'; };
    auto drive = wide.size() >= 3 &&
                 ((wide[0] >= L'A' && wide[0] <= L'Z') || (wide[0] >= L'a' && wide[0] <= L'z')) &&
                 wide[1] == L':' && slash(wide[2]);
    auto unc = wide.size() >= 3 && slash(wide[0]) && slash(wide[1]) && wide[2] != L'?';
    if ((drive || unc) && wide.size() >= 248) {
        std::wstring extended = unc ? L"\\\\?\\UNC\\" : L"\\\\?\\";
        for (auto index = std::size_t(unc ? 2 : 0); index < wide.size(); ++index) {
            extended.push_back(wide[index] == L'/' ? L'\\' : wide[index]);
        }
        return Ok(std::move(extended));
    }
    return Ok(std::move(wide));
}

struct FileInfo {
    std::uint64_t len {};
    bool          is_dir { false };
    bool          readonly { false };
};

auto file_info(HANDLE handle) -> Result<FileInfo> {
    BY_HANDLE_FILE_INFORMATION info {};
    if (! GetFileInformationByHandle(handle, &info)) return Err(Error::last_os_error());
    return Ok(FileInfo {
        .len      = (static_cast<std::uint64_t>(info.nFileSizeHigh) << 32) | info.nFileSizeLow,
        .is_dir   = (info.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0,
        .readonly = (info.dwFileAttributes & FILE_ATTRIBUTE_READONLY) != 0,
    });
}

// 按 rstd open_native 的参数打开：共享读/写/删除，OPEN_EXISTING。
auto open_handle(std::string_view path, DWORD access, DWORD flags) -> Result<HANDLE> {
    auto wide   = rstd_try(to_wide(path));
    auto handle = CreateFileW(wide.c_str(),
                              access,
                              FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                              nullptr,
                              OPEN_EXISTING,
                              flags,
                              nullptr);
    if (handle == INVALID_HANDLE_VALUE) return Err(Error::last_os_error());
    return Ok(handle);
}

// 按路径取元数据（跟随链接，可用于目录），同 rstd fs::metadata。
auto path_info(std::string_view path) -> Result<FileInfo> {
    auto handle = rstd_try(open_handle(path, FILE_READ_ATTRIBUTES, FILE_FLAG_BACKUP_SEMANTICS));
    auto info   = file_info(handle);
    CloseHandle(handle);
    return info;
}

// 同 rstd fs::canonicalize：GetFinalPathNameByHandleW(NORMALIZED|DOS)，
// 去掉 \\?\ 前缀，\\?\UNC\ 还原为 \\，转回严格 UTF-8。
auto canonicalize(std::string_view path) -> Result<std::string> {
    auto         handle = rstd_try(open_handle(path, FILE_READ_ATTRIBUTES, FILE_FLAG_BACKUP_SEMANTICS));
    std::wstring wide(32768, L'\0'); // NT 路径上限，一次给足
    auto         count = GetFinalPathNameByHandleW(
        handle, wide.data(), static_cast<DWORD>(wide.size()), FILE_NAME_NORMALIZED | VOLUME_NAME_DOS);
    auto error = count == 0 ? GetLastError() : DWORD(0);
    CloseHandle(handle);
    if (count == 0) return Err(Error::from_os(error));
    std::wstring_view view(wide.data(), count);
    std::string       prefix;
    if (view.starts_with(L"\\\\?\\UNC\\")) {
        view.remove_prefix(8);
        prefix = "\\\\";
    } else if (view.starts_with(L"\\\\?\\")) {
        view.remove_prefix(4);
    }
    auto size     = static_cast<int>(view.size());
    auto required = WideCharToMultiByte(
        CP_UTF8, WC_ERR_INVALID_CHARS, view.data(), size, nullptr, 0, nullptr, nullptr);
    if (required <= 0) return Err(Error::last_os_error());
    std::string text(static_cast<std::size_t>(required), '\0');
    WideCharToMultiByte(
        CP_UTF8, WC_ERR_INVALID_CHARS, view.data(), size, text.data(), required, nullptr, nullptr);
    return Ok(prefix + text);
}

// 按偏移读取的源；文件与测试里的内存源各实现一份。
class ReadAt {
public:
    virtual ~ReadAt() = default;
    virtual auto read_at(std::uint8_t* buffer, std::size_t size, std::uint64_t offset) const
        -> Result<std::size_t> = 0;
};

class File final : public ReadAt {
public:
    static auto open(std::string_view path) -> Result<std::shared_ptr<File>> {
        auto handle = rstd_try(open_handle(path, GENERIC_READ, FILE_ATTRIBUTE_NORMAL));
        return Ok(std::shared_ptr<File>(new File(handle)));
    }

    File(const File&)                    = delete;
    auto operator=(const File&) -> File& = delete;
    ~File() override { CloseHandle(m_handle); }

    auto info() const -> Result<FileInfo> { return file_info(m_handle); }

    // 同步句柄上的定位读（OVERLAPPED 偏移），ERROR_HANDLE_EOF 视为读到 0 字节（同 rstd）。
    // 调用方单次请求都小于 4 GiB（pkg 条目长度是 int32），不做 DWORD 截断保护。
    auto read_at(std::uint8_t* buffer, std::size_t size, std::uint64_t offset) const
        -> Result<std::size_t> override {
        OVERLAPPED overlapped {};
        overlapped.Offset     = static_cast<DWORD>(offset);
        overlapped.OffsetHigh = static_cast<DWORD>(offset >> 32);
        DWORD count {};
        auto  length = static_cast<DWORD>(size);
        if (! ReadFile(m_handle, buffer, length, &count, &overlapped)) {
            auto error = GetLastError();
            if (error == ERROR_HANDLE_EOF) return Ok(std::size_t {});
            return Err(Error::from_os(error));
        }
        return Ok(static_cast<std::size_t>(count));
    }

private:
    explicit File(HANDLE handle) noexcept: m_handle(handle) {}

    HANDLE m_handle;
};

struct SeekFrom {
    enum class Which
    {
        Start,
        Current,
        End,
    } which;
    std::uint64_t start {};
    std::int64_t  offset {};

    static auto from_start(std::uint64_t value) noexcept -> SeekFrom {
        return SeekFrom { .which = Which::Start, .start = value };
    }
    static auto from_current(std::int64_t value) noexcept -> SeekFrom {
        return SeekFrom { .which = Which::Current, .offset = value };
    }
    static auto from_end(std::int64_t value) noexcept -> SeekFrom {
        return SeekFrom { .which = Which::End, .offset = value };
    }
};

// base + offset 为负报 InvalidInput（上溢要文件长度近 2^63，不可达）。
auto add_offset(std::uint64_t base, std::int64_t offset) -> Result<std::uint64_t> {
    auto value = static_cast<__int128>(base) + offset;
    if (value < 0) {
        return Err(Error::from_kind(ErrorKind::InvalidInput));
    }
    return Ok(static_cast<std::uint64_t>(value));
}

// 共享源上的只读区间读取器；位置不能越过区间末尾（rstd RangeReader 语义）。
class RangeReader {
public:
    RangeReader(std::shared_ptr<const ReadAt> source, std::uint64_t offset, std::uint64_t len)
        : m_source(std::move(source)), m_offset(offset), m_len(len) {}

    auto read(std::uint8_t* buffer, std::size_t size) -> Result<std::size_t> {
        if (m_position == m_len || size == 0) return Ok(std::size_t {});
        auto requested = static_cast<std::size_t>(std::min<std::uint64_t>(size, m_len - m_position));
        auto count     = rstd_try(m_source->read_at(buffer, requested, m_offset + m_position));
        m_position += count;
        return Ok(count);
    }

    auto seek(SeekFrom from) -> Result<std::uint64_t> {
        auto next = from.start;
        if (from.which == SeekFrom::Which::Current) next = rstd_try(add_offset(m_position, from.offset));
        if (from.which == SeekFrom::Which::End) next = rstd_try(add_offset(m_len, from.offset));
        if (next > m_len) return Err(Error::from_kind(ErrorKind::InvalidInput));
        m_position = next;
        return Ok(next);
    }

    auto len() const noexcept -> std::uint64_t { return m_len; }

private:
    std::shared_ptr<const ReadAt> m_source;
    std::uint64_t                 m_offset {};
    std::uint64_t                 m_len {};
    std::uint64_t                 m_position {};
};

// 共享源上的 [offset, offset+len) 区间，可复制、可切子区间。
class ReadRange {
public:
    static auto make(std::shared_ptr<const ReadAt> source, std::uint64_t offset, std::uint64_t len)
        -> Result<ReadRange> {
        return Ok(ReadRange(std::move(source), offset, len));
    }

    auto clone() const -> ReadRange { return ReadRange(m_source, m_offset, m_len); }

    auto subrange(std::uint64_t offset, std::uint64_t len) const -> Result<ReadRange> {
        if (offset > m_len || len > m_len - offset) {
            return Err(Error::from_kind(ErrorKind::InvalidInput));
        }
        return Ok(ReadRange(m_source, m_offset + offset, len));
    }

    auto into_reader() && -> RangeReader { return RangeReader(std::move(m_source), m_offset, m_len); }

    auto len() const noexcept -> std::uint64_t { return m_len; }

private:
    ReadRange(std::shared_ptr<const ReadAt> source, std::uint64_t offset, std::uint64_t len)
        : m_source(std::move(source)), m_offset(offset), m_len(len) {}

    std::shared_ptr<const ReadAt> m_source;
    std::uint64_t                 m_offset {};
    std::uint64_t                 m_len {};
};

// 打开物理文件为整文件区间。
auto open_file_range(std::string_view path) -> Result<ReadRange> {
    auto file = rstd_try(File::open(path));
    auto info = rstd_try(file->info());
    return ReadRange::make(std::move(file), 0, info.len);
}

// 内存字节上的顺序读（rstd Cursor<Vec<u8>> 语义：可定位到末尾之后，之后读到 0 字节）。
class Cursor {
public:
    explicit Cursor(std::vector<std::uint8_t> bytes): m_bytes(std::move(bytes)) {}

    auto read(std::uint8_t* buffer, std::size_t size) -> Result<std::size_t> {
        auto position = m_position >= m_bytes.size() ? m_bytes.size() : std::size_t(m_position);
        auto count    = std::min(size, m_bytes.size() - position);
        if (count == 0) return Ok(std::size_t {});
        std::memcpy(buffer, m_bytes.data() + position, count);
        m_position += count;
        return Ok(count);
    }

    auto seek(SeekFrom from) -> Result<std::uint64_t> {
        auto base = from.which == SeekFrom::Which::End ? std::uint64_t(m_bytes.size()) : m_position;
        m_position = from.which == SeekFrom::Which::Start ? from.start
                                                          : rstd_try(add_offset(base, from.offset));
        return Ok(std::uint64_t(m_position));
    }

private:
    std::vector<std::uint8_t> m_bytes;
    std::uint64_t             m_position {};
};

// 区间读取加 8 KiB 缓冲（rstd BufReader 语义：缓冲空且请求 >= 容量时直读；
// 按当前位置定位要扣掉未读缓冲；定位成功才丢弃缓冲）。
class BufferedRange {
public:
    explicit BufferedRange(RangeReader reader): m_reader(std::move(reader)), m_buffer(8192) {}

    auto read(std::uint8_t* buffer, std::size_t size) -> Result<std::size_t> {
        if (size == 0) return Ok(std::size_t {});
        if (m_pos == m_filled && size >= m_buffer.size()) return m_reader.read(buffer, size);
        if (m_pos == m_filled) {
            m_filled = rstd_try(m_reader.read(m_buffer.data(), m_buffer.size()));
            m_pos    = 0;
            if (m_filled == 0) return Ok(std::size_t {});
        }
        auto count = std::min(size, m_filled - m_pos);
        std::memcpy(buffer, m_buffer.data() + m_pos, count);
        m_pos += count;
        return Ok(count);
    }

    auto seek(SeekFrom from) -> Result<std::uint64_t> {
        if (from.which == SeekFrom::Which::Current) {
            from.offset -= static_cast<std::int64_t>(m_filled - m_pos);
        }
        auto result = rstd_try(m_reader.seek(from));
        m_pos = m_filled = 0;
        return Ok(result);
    }

private:
    RangeReader               m_reader;
    std::vector<std::uint8_t> m_buffer;
    std::size_t               m_pos {};
    std::size_t               m_filled {};
};

class BinaryReader {
public:
    explicit BinaryReader(ReadRange range)
        : m_len(range.len()), m_source(BufferedRange(std::move(range).into_reader())) {}

    explicit BinaryReader(std::vector<u8>&& bytes)
        : m_len(bytes.size()),
          m_source(Cursor(std::vector<std::uint8_t>(reinterpret_cast<const std::uint8_t*>(bytes.data()),
                                                    reinterpret_cast<const std::uint8_t*>(bytes.data()) +
                                                        bytes.size()))) {}

    explicit BinaryReader(BinaryReader& source): BinaryReader(read_remaining(source)) {}

    BinaryReader(const BinaryReader&)                        = delete;
    auto operator=(const BinaryReader&) -> BinaryReader&     = delete;
    BinaryReader(BinaryReader&&) noexcept                    = default;
    auto operator=(BinaryReader&&) noexcept -> BinaryReader& = default;

    auto position() const noexcept -> u64 { return u64(m_position); }
    auto remaining() const noexcept -> u64 {
        return u64(m_position < m_len ? m_len - m_position : std::uint64_t {});
    }

    rstd::size_t Read(void* buffer, rstd::size_t size) {
        auto*        bytes = static_cast<std::uint8_t*>(buffer);
        rstd::size_t total {};
        while (total < size) {
            auto result = read(bytes + total, size - total);
            if (result.is_err()) break;
            auto count = *result;
            if (count == 0) break;
            total += count;
        }
        return total;
    }

    rstd::ptrdiff_t Tell() const noexcept { return static_cast<rstd::ptrdiff_t>(m_position); }

    bool SeekSet(rstd::ptrdiff_t offset) {
        return offset >= 0 && seek(SeekFrom::from_start(static_cast<std::uint64_t>(offset))).is_ok();
    }

    bool SeekCur(rstd::ptrdiff_t offset) { return seek(SeekFrom::from_current(offset)).is_ok(); }

    rstd::ptrdiff_t Size() const noexcept { return static_cast<rstd::ptrdiff_t>(m_len); }

    // 小端读取；整数读不满返回 0，浮点读不满保留已读字节（照原实现）。
    // 原实现的字节序切换无调用点，已删。
    float ReadFloat() {
        float value {};
        Read(&value, sizeof(value));
        return value;
    }

    rstd::int32_t  ReadInt32() { return read_value<rstd::int32_t>(); }
    rstd::uint32_t ReadUint32() { return read_value<rstd::uint32_t>(); }
    rstd::uint16_t ReadUint16() { return read_value<rstd::uint16_t>(); }
    rstd::uint8_t  ReadUint8() { return read_value<rstd::uint8_t>(); }

    auto read_all_string() -> Result<std::string> {
        std::string value(static_cast<std::size_t>(m_len - m_position), '\0');
        auto*       bytes = reinterpret_cast<std::uint8_t*>(value.data());
        auto        size  = value.size();
        while (size > 0) {
            auto count = rstd_try(read(bytes, size));
            if (count == 0) return Err(Error::from_kind(ErrorKind::UnexpectedEof));
            bytes += count;
            size -= count;
        }
        return Ok(std::move(value));
    }

    std::string ReadAllStr() {
        auto value = read_all_string();
        return value.is_ok() ? std::move(value).unwrap_unchecked() : std::string {};
    }

private:
    explicit BinaryReader(std::vector<std::uint8_t> bytes)
        : m_len(bytes.size()), m_source(Cursor(std::move(bytes))) {}

    static auto read_remaining(BinaryReader& source) -> std::vector<std::uint8_t> {
        std::vector<std::uint8_t> bytes(static_cast<std::size_t>(source.remaining().to_primitive()));
        bytes.resize(source.Read(bytes.data(), bytes.size()));
        return bytes;
    }

    auto read(std::uint8_t* buffer, std::size_t size) -> Result<std::size_t> {
        auto result = std::visit([&](auto& source) { return source.read(buffer, size); }, m_source);
        if (result.is_ok()) m_position += *result;
        return result;
    }

    auto seek(SeekFrom from) -> Result<std::uint64_t> {
        auto result = std::visit([&](auto& source) { return source.seek(from); }, m_source);
        if (result.is_ok()) m_position = *result;
        return result;
    }

    template<typename T>
    auto read_value() -> T {
        T value {};
        if (Read(&value, sizeof(value)) != sizeof(value)) return T {};
        return value;
    }

    std::uint64_t                         m_len {};
    std::uint64_t                         m_position {};
    std::variant<Cursor, BufferedRange>   m_source;
};

} // namespace owe::io

template<>
struct rstd::Impl<rstd::fmt::Display, owe::io::Error> : rstd::ImplBase<owe::io::Error> {
    auto fmt(rstd::fmt::Formatter& f) const -> bool {
        auto text = this->self().to_string();
        return f.write_raw(text.data(), text.size());
    }
};
