export module wescene.json;
export import rstd;
import rstd.cppstd;
export import rstd.json;
import wescene.fs;

using namespace rstd::prelude;
using rstd::mtp::is_const;

export namespace owe
{

using Json = rstd::json::Value;

enum class JsonFileErrorKind : rstd::uint8_t
{
    Io,
    Parse,
};

struct JsonFileError {
    JsonFileErrorKind kind;
    String            message;
};

template<typename T>
struct JsonTemplateTypeCheck {
    using type = bool;
    static_assert(! is_const<T>, "GetJsonValue need a non const value");
};

template<typename T>
typename JsonTemplateTypeCheck<T>::type
GetJsonValue(const Json& json, T& value,
             std::source_location loc = std::source_location::current());

template<typename T>
typename JsonTemplateTypeCheck<T>::type
GetJsonValue(const Json& json, std::string_view name, T& value, bool warn = true,
             std::source_location loc = std::source_location::current());

auto ParseJson(std::string_view source, rstd::json::ParseOptions options = {})
    -> rstd::json::ParseResult;
auto ReadJsonFile(fs::VFS& vfs, fs::Path path, rstd::json::ParseOptions options = {})
    -> rstd::Result<Json, JsonFileError>;
inline auto ReadJsonFile(fs::VFS& vfs, std::string_view path, rstd::json::ParseOptions options = {})
    -> rstd::Result<Json, JsonFileError> {
    return ReadJsonFile(vfs, fs::ToPath(path), options);
}
auto ReadAssetJsonFile(fs::VFS& vfs, std::string_view path, rstd::json::ParseOptions options = {})
    -> rstd::Result<Json, JsonFileError>;
auto Dump(const Json& value, Option<usize> indent = None()) -> std::string;
auto DumpString(const Json& value, Option<usize> indent = None()) -> String;

inline auto Dump(const Json& value, usize indent) -> std::string {
    return Dump(value, Some(indent));
}

inline auto Dump(const Json& value, rstd::size_t indent) -> std::string {
    return Dump(value, usize(indent));
}

inline auto JsonFromStd(std::string_view value) -> Json {
    return rstd::into<Json>(::alloc::string::String::make(rstd::cppstd::as_str(value).unwrap()));
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
struct Impl<fmt::Debug, owe::JsonFileError> : ImplBase<owe::JsonFileError> {
    auto fmt(fmt::Formatter& formatter) const -> bool {
        return formatter.write_fmt(fmt::Arguments::make("JsonFileError(kind={}, message={})",
                                                        static_cast<int>(this->self().kind),
                                                        this->self().message));
    }
};

template<>
struct Impl<error::Error, owe::JsonFileError> : DefaultInImpl<error::Error, owe::JsonFileError> {};

} // namespace rstd

static_assert(rstd::Impled<owe::JsonFileError, rstd::error::Error>);
