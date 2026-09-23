module;
#include <owe/compat.hpp>
#include <owe/std.hpp>


#include "JsonNlohmann.hpp"

module wescene.json;

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

// GetJsonValue 用到的最小读取适配（原先 rstd 与 nlohmann 各一份，rstd 版已删）。
auto Member(const NJson& json, std::string_view key) -> const NJson* { return Find(json, key); }

auto IsNull(const NJson& json) -> bool { return json.is_null(); }
auto IsNumber(const NJson& json) -> bool { return json.is_number(); }

auto AsBool(const NJson& json) -> std::optional<bool> {
    return json.is_boolean() ? std::optional<bool>(json.get<bool>()) : std::nullopt;
}

auto AsString(const NJson& json) -> std::optional<std::string_view> {
    return json.is_string() ? std::optional<std::string_view>(json.get_ref<const std::string&>())
                            : std::nullopt;
}

// 回调 f(元素个数, 按下标取元素)；不是数组返回 false。
template<typename F>
auto WithArray(const NJson& json, F&& f) -> bool {
    if (! json.is_array()) return false;
    f(json.size(), [&](std::size_t index) -> const NJson& { return json[index]; });
    return true;
}

auto DumpForLog(const NJson& json) -> std::string { return nljson::Dump(json, 4); }

template<typename V>
auto InitialJsonValue(const V& json) -> const V& {
    if (auto value = Member(json, "value"); value != nullptr) return *value;
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

// nlohmann 的数字种类与原 rstd 一一对应：number_float↔f64，number_unsigned↔u64，number_integer↔i64。
template<typename T>
auto ConvertNumber(const NJson& json) -> T {
    if (! json.is_number()) throw WrongJsonType {};
    if (json.is_number_float()) return rstd::as_cast<T>(f64(json.get<double>()));
    if (json.is_number_unsigned()) return rstd::as_cast<T>(u64(json.get<std::uint64_t>()));
    return rstd::as_cast<T>(i64(json.get<std::int64_t>()));
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

template<typename V, typename T>
auto ReadJsonValue(const V& json, T& value) -> bool {
    const auto& input = InitialJsonValue(json);
    if constexpr (JsonArrayTarget<T>::enabled) {
        using Value = typename JsonArrayTarget<T>::value_type;
        if (IsNumber(input)) {
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
        const bool is_array = WithArray(input, [&](std::size_t len, auto at) {
            if constexpr (JsonArrayTarget<T>::dynamic) {
                value.clear();
                for (std::size_t index = 0; index < len; ++index)
                    value.push_back(ConvertNumber<Value>(at(index)));
            } else {
                std::size_t count {};
                for (auto& item : value) {
                    if (count >= len) throw WrongArraySize {};
                    item = ConvertNumber<Value>(at(count));
                    ++count;
                }
                if (count != len) throw WrongArraySize {};
            }
        });
        if (is_array) return true;
        auto string = AsString(input);
        if (! string) throw WrongJsonType {};
        return ConvertArray(*string, value);
    } else if constexpr (same<T, bool>) {
        auto boolean = AsBool(input);
        if (! boolean) throw WrongJsonType {};
        value = *boolean;
        return true;
    } else if constexpr (rstd::num::Numeric<T>) {
        auto boolean = AsBool(input);
        value        = boolean ? rstd::as_cast<T>(static_cast<rstd::uint8_t>(*boolean))
                               : ConvertNumber<T>(input);
        return true;
    } else if constexpr (is_arithmetic<T>) {
        auto boolean = AsBool(input);
        value        = boolean ? static_cast<T>(*boolean) : ConvertNumber<T>(input);
        return true;
    } else if constexpr (same<T, std::string>) {
        auto string = AsString(input);
        if (! string) throw WrongJsonType {};
        value = std::string(*string);
        return true;
    }
}

template<typename V, typename T>
auto ReadJsonValue(const V& json, T& value, const char* name, std::source_location loc) -> bool {
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
                  DumpForLog(json));
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

namespace
{

template<typename V, typename T>
auto ReadMember(const V& json, std::string_view name_view, T& value, bool warn,
                std::source_location loc) -> bool {
    auto member = Member(json, name_view);
    if (member == nullptr) {
        if (warn)
            rstd_info("read json \"{}\" not a key at {}({}:{})",
                      name_view,
                      std::string_view(loc.function_name()),
                      std::string_view(loc.file_name()),
                      loc.line());
        return false;
    }
    if (IsNull(*member)) {
        if (warn)
            rstd_info("read json \"{}\" is null at {}({}:{})",
                      name_view,
                      std::string_view(loc.function_name()),
                      std::string_view(loc.file_name()),
                      loc.line());
        return false;
    }
    std::string name { name_view };
    return ReadJsonValue(*member, value, name.c_str(), loc);
}

} // namespace

template<typename T>
typename JsonTemplateTypeCheck<T>::type GetJsonValue(const NJson& json, T& value,
                                                     std::source_location loc) {
    return ReadJsonValue(json, value, nullptr, loc);
}

template<typename T>
typename JsonTemplateTypeCheck<T>::type GetJsonValue(const NJson& json, std::string_view name_view,
                                                     T& value, bool warn,
                                                     std::source_location loc) {
    return ReadMember(json, name_view, value, warn, loc);
}

#define OWE_IMPL_GET_JSON(TYPE)                                    \
    template JsonTemplateTypeCheck<TYPE>::type GetJsonValue<TYPE>( \
        const NJson&, TYPE&, std::source_location);                \
    template JsonTemplateTypeCheck<TYPE>::type GetJsonValue<TYPE>( \
        const NJson&, std::string_view, TYPE&, bool, std::source_location)

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

auto ParseNJson(std::string_view source, JsonParseOptions options)
    -> rstd::Result<NJson, JsonParseError> {
    // 保持原行为：非 UTF-8 输入与原 rstd 路径一样在这里中止（T2 集成时随 as_str 校验取消一起登记）。
    static_cast<void>(rstd::cppstd::as_str(source).unwrap());
    nljson::Value parsed;
    std::string   error;
    if (! nljson::Parse(source,
                        { .allow_comments        = options.allow_comments,
                          .allow_trailing_commas = options.allow_trailing_commas },
                        parsed,
                        error)) {
        // nlohmann 的报错会原样带出读到的字节（可能是多字节字符的半截），不能直接当 str 用。
        if (rstd::cppstd::as_str(error).is_err()) error = "invalid JSON";
        return Err(JsonParseError { String::make(rstd::cppstd::as_str(error).unwrap()) });
    }
    return Ok(rstd::move(parsed));
}

auto ReadNJsonFile(fs::VFS& vfs, fs::Path path, JsonParseOptions options)
    -> rstd::Result<NJson, JsonFileError> {
    auto io_error = [](auto error) {
        return JsonFileError { JsonFileErrorKind::Io, rstd::format("{}", error) };
    };
    auto parse_error = [](auto error) {
        return JsonFileError { JsonFileErrorKind::Parse, rstd::format("{}", error) };
    };
    // Wallpaper Engine scene and asset metadata occasionally uses one trailing
    // comma. Keep ParseNJson strict for non-resource callers; this boundary is
    // the explicit compatibility opt-in for VFS-backed WPE JSON only.
    options.allow_trailing_commas = true;
    auto content = rstd_try(fs::ReadFileContent(vfs, path), io_error);
    auto parsed  = rstd_try(ParseNJson(content, options), parse_error);
    return Ok(rstd::move(parsed));
}

auto ReadAssetNJsonFile(fs::VFS& vfs, std::string_view path, JsonParseOptions options)
    -> rstd::Result<NJson, JsonFileError> {
    auto resolved = fs::ResolveAssetPath(path);
    if (resolved.is_err()) {
        auto error = rstd::move(resolved).unwrap_err_unchecked();
        return Err(JsonFileError { JsonFileErrorKind::Io, rstd::format("{}", error) });
    }
    return ReadNJsonFile(vfs, resolved->as_path(), options);
}

auto Dump(const NJson& value, Option<usize> indent) -> std::string {
    return indent ? nljson::Dump(value, (*indent).to_primitive()) : nljson::Dump(value);
}

} // namespace owe
