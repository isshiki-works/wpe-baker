module;

#include "JsonNlohmann.hpp"

export module wescene.json;
export import rstd;
import rstd.cppstd;
import wescene.fs;

using namespace rstd::prelude;
using rstd::mtp::is_const;

export namespace owe
{

// nlohmann 值（键按字节序，与原 rstd BTreeMap 同序）；T2-3 起全仓唯一的 json 值类型。
// 用它的调用单元要在全局模块片段里 #include "JsonNlohmann.hpp"，才能调 nlohmann 的成员函数。
using NJson = nljson::Value;

enum class JsonFileErrorKind : rstd::uint8_t
{
    Io,
    Parse,
};

struct JsonFileError {
    JsonFileErrorKind kind;
    String            message;
};

// 解析走 nlohmann（见 JsonNlohmann.hpp）；重复键一律后者覆盖，原 reject_duplicate_keys 无调用方，已删。
struct JsonParseOptions {
    bool allow_comments { false };
    bool allow_trailing_commas { false };
};

struct JsonParseError {
    String message;
};

template<typename T>
struct JsonTemplateTypeCheck {
    using type = bool;
    static_assert(! is_const<T>, "GetJsonValue need a non const value");
};

// 读取：取 "value" 包装、数字/布尔互转、空格分隔数串、报错与日志文本与原 rstd 版逐项一致。
template<typename T>
typename JsonTemplateTypeCheck<T>::type
GetJsonValue(const NJson& json, T& value,
             std::source_location loc = std::source_location::current());

template<typename T>
typename JsonTemplateTypeCheck<T>::type
GetJsonValue(const NJson& json, std::string_view name, T& value, bool warn = true,
             std::source_location loc = std::source_location::current());

// 对象成员查找：不是对象或没有这个键返回 nullptr（对应 json.get("k"_str) 的 None）。
inline auto Find(const NJson& json, std::string_view key) -> const NJson* {
    const auto found = json.find(key);
    return found == json.end() ? nullptr : &*found;
}

auto ParseNJson(std::string_view source, JsonParseOptions options = {})
    -> rstd::Result<NJson, JsonParseError>;
auto ReadNJsonFile(fs::VFS& vfs, fs::Path path, JsonParseOptions options = {})
    -> rstd::Result<NJson, JsonFileError>;
inline auto ReadNJsonFile(fs::VFS& vfs, std::string_view path, JsonParseOptions options = {})
    -> rstd::Result<NJson, JsonFileError> {
    return ReadNJsonFile(vfs, fs::ToPath(path), options);
}
auto ReadAssetNJsonFile(fs::VFS& vfs, std::string_view path, JsonParseOptions options = {})
    -> rstd::Result<NJson, JsonFileError>;
auto Dump(const NJson& value, Option<usize> indent = None()) -> std::string;
inline auto Dump(const NJson& value, usize indent) -> std::string {
    return Dump(value, Some(indent));
}

} // namespace owe

export namespace rstd
{

template<>
struct Impl<fmt::Display, owe::JsonFileError> : ImplBase<owe::JsonFileError> {
    auto fmt(fmt::Formatter& formatter) const -> bool {
        return formatter.write_fmt(fmt::Arguments::make("{}", this->self().message));
    }
};

template<>
struct Impl<fmt::Display, owe::JsonParseError> : ImplBase<owe::JsonParseError> {
    auto fmt(fmt::Formatter& formatter) const -> bool {
        return formatter.write_fmt(fmt::Arguments::make("{}", this->self().message));
    }
};

} // namespace rstd

