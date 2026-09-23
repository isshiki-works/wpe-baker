module;

#include <rstd/macro.hpp>

#include "JsonNlohmann.hpp"

module wescene.json;
import rstd.cppstd;
import rstd.json;
import rstd.log;

using namespace rstd::prelude;
using namespace rstd::literals;
using rstd::mtp::is_arithmetic;
using rstd::mtp::same;

namespace owe
{

namespace
{

template<typename>
struct JsonArrayTarget {
    static constexpr bool enabled = false;
};

template<typename T>
struct JsonArrayTarget<std::vector<T>> {
    static constexpr bool enabled = true;
    static constexpr bool dynamic = true;
    using value_type              = T;
};

template<typename T>
struct JsonArrayTarget<Vec<T>> {
    static constexpr bool enabled = true;
    static constexpr bool dynamic = true;
    using value_type              = T;
};

template<typename T, std::size_t N>
struct JsonArrayTarget<std::array<T, N>> {
    static constexpr bool enabled = true;
    static constexpr bool dynamic = false;
    using value_type              = T;
};

template<typename T, rstd::size_t N>
struct JsonArrayTarget<rstd::array<T, N>> {
    static constexpr bool enabled = true;
    static constexpr bool dynamic = false;
    using value_type              = T;
};

struct WrongJsonType : std::exception {
    auto what() const noexcept -> const char* override { return "Wrong json value type"; }
};

struct WrongArraySize : std::exception {
    auto what() const noexcept -> const char* override { return "Wrong size of the array"; }
};

auto InitialJsonValue(const Json& json) -> const Json& {
    if (auto value = json.get("value"_str); value.is_some()) return **value;
    return json;
}

template<typename T>
auto ParseNumber(std::string_view value) -> T {
    std::string text { value };
    if constexpr (same<T, float>) {
        return std::stof(text);
    } else if constexpr (same<T, double>) {
        return std::stod(text);
    } else if constexpr (rstd::num::Float<T>) {
        return rstd::as_cast<T>(std::stod(text));
    } else if constexpr (rstd::num::SignedInteger<T>) {
        return rstd::as_cast<T>(std::stoll(text));
    } else if constexpr (rstd::num::UnsignedInteger<T>) {
        return rstd::as_cast<T>(std::stoull(text));
    } else if constexpr (std::is_signed_v<T>) {
        return static_cast<T>(std::stoll(text));
    } else {
        return static_cast<T>(std::stoull(text));
    }
}

template<typename T>
auto ConvertNumber(const Json& json) -> T {
    auto number = json.as_number();
    if (number.is_none()) throw WrongJsonType {};
    if ((*number)->is_f64()) return rstd::as_cast<T>(*(*number)->as_f64());
    if ((*number)->is_u64()) return rstd::as_cast<T>(*(*number)->as_u64());
    return rstd::as_cast<T>(*(*number)->as_i64());
}

template<typename T>
auto ConvertArray(std::string_view value, std::vector<T>& target) -> bool {
    std::vector<std::string_view> parts;
    while (true) {
        const auto delimiter = value.find(' ');
        if (delimiter == std::string_view::npos) {
            parts.push_back(value);
            break;
        }
        parts.push_back(value.substr(0, delimiter));
        value.remove_prefix(delimiter + 1);
    }
    if (target.size() < parts.size()) target.resize(parts.size());
    std::transform(parts.begin(), parts.end(), target.begin(), [](std::string_view part) {
        return ParseNumber<T>(part);
    });
    return true;
}

template<typename T>
auto ConvertArray(std::string_view value, Vec<T>& target) -> bool {
    target.clear();
    while (true) {
        const auto delimiter = value.find(' ');
        if (delimiter == std::string_view::npos) {
            target.push(ParseNumber<T>(value));
            break;
        }
        target.push(ParseNumber<T>(value.substr(0, delimiter)));
        value.remove_prefix(delimiter + 1);
    }
    return true;
}

template<typename T, std::size_t N>
auto ConvertArray(std::string_view value, std::array<T, N>& target) -> bool {
    std::array<std::string_view, N> parts;
    std::size_t                     count = 0;
    while (true) {
        const auto delimiter = value.find(' ');
        if (count == N) throw WrongArraySize {};
        if (delimiter == std::string_view::npos) {
            parts[count++] = value;
            break;
        }
        parts[count++] = value.substr(0, delimiter);
        value.remove_prefix(delimiter + 1);
    }
    if (count != N) throw WrongArraySize {};
    std::transform(parts.begin(), parts.end(), target.begin(), [](std::string_view part) {
        return ParseNumber<T>(part);
    });
    return true;
}

template<typename T, rstd::size_t N>
auto ConvertArray(std::string_view value, rstd::array<T, N>& target) -> bool {
    rstd::array<std::string_view, N> parts;
    usize                            count {};
    while (true) {
        const auto delimiter = value.find(' ');
        if (count == usize(N)) throw WrongArraySize {};
        if (delimiter == std::string_view::npos) {
            parts[count++] = value;
            break;
        }
        parts[count++] = value.substr(0, delimiter);
        value.remove_prefix(delimiter + 1);
    }
    if (count != usize(N)) throw WrongArraySize {};
    for (usize index {}; index < usize(N); ++index) target[index] = ParseNumber<T>(parts[index]);
    return true;
}

template<typename T>
auto ReadJsonValue(const Json& json, T& value) -> bool {
    const auto& input = InitialJsonValue(json);
    if constexpr (JsonArrayTarget<T>::enabled) {
        using Value = typename JsonArrayTarget<T>::value_type;
        if (input.is_number()) {
            if constexpr (JsonArrayTarget<T>::dynamic) {
                value.clear();
                value.push_back(ConvertNumber<Value>(input));
            } else {
                bool first = true;
                for (auto& item : value) {
                    item  = first ? ConvertNumber<Value>(input) : Value {};
                    first = false;
                }
            }
            return true;
        }
        if (auto array = input.as_array(); array.is_some()) {
            if constexpr (JsonArrayTarget<T>::dynamic) {
                value.clear();
                for (const auto& item : **array) value.push_back(ConvertNumber<Value>(item));
            } else {
                usize count {};
                for (auto& item : value) {
                    if (count >= (*array)->len()) throw WrongArraySize {};
                    item = ConvertNumber<Value>((**array)[count]);
                    ++count;
                }
                if (count != (*array)->len()) throw WrongArraySize {};
            }
            return true;
        }
        auto string = input.as_str();
        if (string.is_none()) throw WrongJsonType {};
        return ConvertArray(rstd::cppstd::as_string_view(*string), value);
    } else if constexpr (same<T, bool>) {
        auto boolean = input.as_bool();
        if (boolean.is_none()) throw WrongJsonType {};
        value = *boolean;
        return true;
    } else if constexpr (rstd::num::Numeric<T>) {
        auto boolean = input.as_bool();
        value        = boolean.is_some() ? rstd::as_cast<T>(static_cast<rstd::uint8_t>(*boolean))
                                         : ConvertNumber<T>(input);
        return true;
    } else if constexpr (is_arithmetic<T>) {
        auto boolean = input.as_bool();
        value        = boolean.is_some() ? static_cast<T>(*boolean) : ConvertNumber<T>(input);
        return true;
    } else if constexpr (same<T, std::string>) {
        auto string = input.as_str();
        if (string.is_none()) throw WrongJsonType {};
        value = rstd::cppstd::to_string(*string);
        return true;
    }
}

template<typename T>
auto ReadJsonValue(const Json& json, T& value, const char* name, std::source_location loc) -> bool {
    std::string name_info;
    if (name != nullptr) name_info = std::string("(key: ") + name + ")";
    try {
        return ReadJsonValue(json, value);
    } catch (const WrongJsonType& error) {
        rstd_info("{} {} at {} {}:{}\n{}",
                  std::string_view(error.what()),
                  name_info,
                  std::string_view(loc.function_name()),
                  std::string_view(loc.file_name()),
                  loc.line(),
                  Dump(json, usize(4)));
    } catch (const std::invalid_argument& error) {
        rstd_error("{} {} at {} {}:{}",
                   std::string_view(error.what()),
                   name_info,
                   std::string_view(loc.function_name()),
                   std::string_view(loc.file_name()),
                   loc.line());
    } catch (const std::out_of_range& error) {
        rstd_error("{} {} at {} {}:{}",
                   std::string_view(error.what()),
                   name_info,
                   std::string_view(loc.function_name()),
                   std::string_view(loc.file_name()),
                   loc.line());
    } catch (const WrongArraySize& error) {
        rstd_error("{} {} at {} {}:{}",
                   std::string_view(error.what()),
                   name_info,
                   std::string_view(loc.function_name()),
                   std::string_view(loc.file_name()),
                   loc.line());
    }
    return false;
}

} // namespace

template<typename T>
typename JsonTemplateTypeCheck<T>::type GetJsonValue(const Json& json, T& value,
                                                     std::source_location loc) {
    return ReadJsonValue(json, value, nullptr, loc);
}

template<typename T>
typename JsonTemplateTypeCheck<T>::type GetJsonValue(const Json& json, std::string_view name_view,
                                                     T& value, bool warn,
                                                     std::source_location loc) {
    auto member = json.get(rstd::cppstd::as_str(name_view).unwrap());
    if (member.is_none()) {
        if (warn)
            rstd_info("read json \"{}\" not a key at {}({}:{})",
                      name_view,
                      std::string_view(loc.function_name()),
                      std::string_view(loc.file_name()),
                      loc.line());
        return false;
    }
    if ((*member)->is_null()) {
        if (warn)
            rstd_info("read json \"{}\" is null at {}({}:{})",
                      name_view,
                      std::string_view(loc.function_name()),
                      std::string_view(loc.file_name()),
                      loc.line());
        return false;
    }
    std::string name { name_view };
    return ReadJsonValue(**member, value, name.c_str(), loc);
}

#define OWE_IMPL_GET_JSON(TYPE)                                    \
    template JsonTemplateTypeCheck<TYPE>::type GetJsonValue<TYPE>( \
        const Json&, TYPE&, std::source_location);                 \
    template JsonTemplateTypeCheck<TYPE>::type GetJsonValue<TYPE>( \
        const Json&, std::string_view, TYPE&, bool, std::source_location)

OWE_IMPL_GET_JSON(bool);
OWE_IMPL_GET_JSON(i32);
OWE_IMPL_GET_JSON(u32);
OWE_IMPL_GET_JSON(rstd::int32_t);
OWE_IMPL_GET_JSON(rstd::uint32_t);
OWE_IMPL_GET_JSON(float);
OWE_IMPL_GET_JSON(double);
OWE_IMPL_GET_JSON(std::string);
OWE_IMPL_GET_JSON(std::vector<float>);
OWE_IMPL_GET_JSON(std::vector<std::int32_t>);
OWE_IMPL_GET_JSON(std::vector<i32>);
OWE_IMPL_GET_JSON(std::vector<u32>);
OWE_IMPL_GET_JSON(Vec<float>);
OWE_IMPL_GET_JSON(Vec<std::int32_t>);
OWE_IMPL_GET_JSON(Vec<i32>);
OWE_IMPL_GET_JSON(Vec<u32>);

using IntArray3   = std::array<int, 3>;
using I32Array3   = std::array<i32, 3>;
using FloatArray2 = std::array<float, 2>;
using FloatArray3 = std::array<float, 3>;
OWE_IMPL_GET_JSON(IntArray3);
OWE_IMPL_GET_JSON(I32Array3);
OWE_IMPL_GET_JSON(FloatArray2);
OWE_IMPL_GET_JSON(FloatArray3);

using RstdIntArray3   = rstd::array<int, 3>;
using RstdFloatArray2 = rstd::array<float, 2>;
using RstdFloatArray3 = rstd::array<float, 3>;
OWE_IMPL_GET_JSON(RstdIntArray3);
OWE_IMPL_GET_JSON(RstdFloatArray2);
OWE_IMPL_GET_JSON(RstdFloatArray3);

#undef OWE_IMPL_GET_JSON

namespace
{

// 过渡桥：调用点仍按 rstd::json::Value 读取；调用点迁到 nlohmann 后删除。
auto ToRstd(const nljson::Value& value) -> Json {
    using Kind = nljson::Value::value_t;
    switch (value.type()) {
    case Kind::boolean: return rstd::into<Json>(value.get<bool>());
    case Kind::number_unsigned:
        return rstd::into<Json>(rstd::json::Number::from_u64(u64(value.get<std::uint64_t>())));
    case Kind::number_integer:
        return rstd::into<Json>(rstd::json::Number::from_i64(i64(value.get<std::int64_t>())));
    case Kind::number_float:
        return rstd::into<Json>(*rstd::json::Number::from_f64(f64(value.get<double>())));
    case Kind::string: return JsonFromStd(value.get_ref<const std::string&>());
    case Kind::array: {
        auto array = rstd::json::Array::make();
        for (const auto& item : value) array.push(ToRstd(item));
        return rstd::into<Json>(rstd::move(array));
    }
    case Kind::object: {
        auto object = rstd::json::Map::make();
        for (const auto& [key, item] : value.items())
            static_cast<void>(
                object.insert(String::make(rstd::cppstd::as_str(key).unwrap()), ToRstd(item)));
        return rstd::into<Json>(rstd::move(object));
    }
    default: return rstd::into<Json>(rstd::empty {});
    }
}

} // namespace

auto ParseJson(std::string_view source, JsonParseOptions options)
    -> rstd::Result<Json, JsonParseError> {
    nljson::Value parsed;
    std::string   error;
    if (! nljson::Parse(source,
                        { .allow_comments        = options.allow_comments,
                          .allow_trailing_commas = options.allow_trailing_commas },
                        parsed,
                        error))
        return Err(JsonParseError { String::make(rstd::cppstd::as_str(error).unwrap()) });
    return Ok(ToRstd(parsed));
}

auto ReadJsonFile(fs::VFS& vfs, fs::Path path, JsonParseOptions options)
    -> rstd::Result<Json, JsonFileError> {
    auto io_error = [](auto error) {
        return JsonFileError { JsonFileErrorKind::Io, rstd::format("{}", error) };
    };
    auto parse_error = [](auto error) {
        return JsonFileError { JsonFileErrorKind::Parse, rstd::format("{}", error) };
    };
    // Wallpaper Engine scene and asset metadata occasionally uses one trailing
    // comma. Keep ParseJson strict for non-resource callers; this boundary is
    // the explicit compatibility opt-in for VFS-backed WPE JSON only.
    options.allow_trailing_commas = true;
    auto content = rstd_try(fs::ReadFileContent(vfs, path), io_error);
    auto parsed  = rstd_try(ParseJson(content, options), parse_error);
    return Ok(rstd::move(parsed));
}

auto ReadAssetJsonFile(fs::VFS& vfs, std::string_view path, JsonParseOptions options)
    -> rstd::Result<Json, JsonFileError> {
    auto resolved = fs::ResolveAssetPath(path);
    if (resolved.is_err()) {
        auto error = rstd::move(resolved).unwrap_err_unchecked();
        return Err(JsonFileError { JsonFileErrorKind::Io, rstd::format("{}", error) });
    }
    return ReadJsonFile(vfs, resolved->as_path(), options);
}

auto Dump(const Json& value, Option<usize> indent) -> std::string {
    return rstd::cppstd::to_string(DumpString(value, indent));
}

auto DumpString(const Json& value, Option<usize> indent) -> String {
    auto options = rstd::json::FormatOptions {};
    if (indent) {
        options.pretty = true;
        options.indent = *indent;
    }
    return rstd::json::to_string(value, options);
}

} // namespace owe
