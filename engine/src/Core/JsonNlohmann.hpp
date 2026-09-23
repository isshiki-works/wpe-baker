#pragma once
// nlohmann/json 之上的解析与写出，语义与字节对齐原 rstd::json（T2-3）。
// 解析：注释（nlohmann ignore_comments）与尾逗号（StripTrailingCommas）按选项放开；重复键后者覆盖前者（与 rstd 默认一致）；
//       "-0" 按浮点 -0.0 存（rstd 把负零整数交给浮点解析）；UTF-8 BOM 视为错误（rstd 不跳 BOM）；
//       容器嵌套到第 128 层报错（rstd remaining_depth=128；nlohmann 无上限，深嵌套会递归爆栈）。
// 写出：键按字节升序（std::map，与 rstd BTreeMap 同序）；浮点按 rstd 的 Rust Debug 规则
//       （最短往返，两个最短候选与真值等距时取绝对值大的；|x|<1e-4 或 ≥1e16 用科学计数，
//       指数前补 '+'；定点至少一位小数）。
#include <charconv>
#include <cmath>
#include <cstdlib>
#include <optional>
#include <string>
#include <string_view>

#include <nlohmann/json.hpp>

namespace owe::nljson
{

using Value = nlohmann::json;

struct Options {
    bool allow_comments { false };
    bool allow_trailing_commas { false };
};

// nlohmann 3.12.0 发布版没有 ignore_trailing_commas（只在 develop），这里先把
// “值后面、紧挨 ] 或 } 的那一个逗号”换成空格再交给 nlohmann；[,]、[1,,]、{"a":,} 保持报错。
inline auto StripTrailingCommas(std::string_view text, bool comments) -> std::string {
    std::string out(text);
    char        previous = 0; // 上一个有效字符（跳过空白、字符串内容与注释）
    auto skip_comment = [&](std::size_t& i) {
        if (! comments || text[i] != '/' || i + 1 >= text.size()) return false;
        if (text[i + 1] == '/') {
            i = text.find('\n', i);
            if (i == std::string_view::npos) i = text.size();
            return true;
        }
        if (text[i + 1] == '*') {
            const auto end = text.find("*/", i + 2);
            i              = end == std::string_view::npos ? text.size() : end + 2;
            return true;
        }
        return false;
    };
    for (std::size_t i = 0; i < text.size();) {
        const char c = text[i];
        if (c == '"') {
            for (++i; i < text.size() && text[i] != '"'; ++i)
                if (text[i] == '\\') ++i;
            ++i;
            previous = '"';
            continue;
        }
        if (skip_comment(i)) continue;
        if (c == ' ' || c == '\t' || c == '\n' || c == '\r') {
            ++i;
            continue;
        }
        if (c == ',' && previous != '[' && previous != '{' && previous != ',' && previous != ':') {
            std::size_t next = i + 1;
            while (next < text.size()) {
                const char n = text[next];
                if (n == ' ' || n == '\t' || n == '\n' || n == '\r') ++next;
                else if (! skip_comment(next)) break;
            }
            if (next < text.size() && (text[next] == ']' || text[next] == '}')) out[i] = ' ';
        }
        previous = c;
        ++i;
    }
    return out;
}

inline auto Parse(std::string_view text, Options options, Value& out, std::string& error) -> bool {
    if (text.starts_with("\xEF\xBB\xBF")) {
        error = "expected value at line 1 column 1";
        return false;
    }
    std::string stripped;
    if (options.allow_trailing_commas) {
        stripped = StripTrailingCommas(text, options.allow_comments);
        text     = stripped;
    }
    struct TooDeep {};
    auto adapt = [](int depth, Value::parse_event_t event, Value& parsed) {
        if ((event == Value::parse_event_t::object_start ||
             event == Value::parse_event_t::array_start) &&
            depth >= 127)
            throw TooDeep {};
        if (event == Value::parse_event_t::value &&
            parsed.type() == Value::value_t::number_integer && parsed.get<std::int64_t>() == 0)
            parsed = -0.0;
        return true;
    };
    try {
        out = Value::parse(text.begin(), text.end(), adapt, true, options.allow_comments);
        return true;
    } catch (const Value::exception& caught) {
        error = caught.what();
        return false;
    } catch (const TooDeep&) {
        error = "recursion limit exceeded";
        return false;
    }
}

inline auto FormatFloat(double value) -> std::string {
    if (value == 0.0) return std::signbit(value) ? "-0.0" : "0.0";
    char buffer[64];
    auto end = std::to_chars(buffer, buffer + sizeof(buffer), value, std::chars_format::scientific).ptr;
    std::string_view text(buffer, end - buffer);
    std::string      sign;
    if (text.front() == '-') {
        sign = "-";
        text.remove_prefix(1);
    }
    const auto  e        = text.find('e');
    const int   exponent = std::atoi(std::string(text.substr(e + 1)).c_str());
    std::string digits   = std::string(text.substr(0, 1)) +
                         std::string(e > 2 ? text.substr(2, e - 2) : std::string_view {});
    const double magnitude = std::fabs(value);
    if ((digits.back() - '0') % 2 == 0) {
        // to_chars 在等距时取偶数末位，rstd（Rust flt2dec）取大者：真值恰为 digits 后接 5 时改进一位。
        char exact[800];
        auto exact_end = std::to_chars(exact, exact + sizeof(exact), magnitude,
                                       std::chars_format::scientific, 770).ptr;
        const std::string_view full(exact, exact_end - exact);
        const auto             full_e = full.find('e');
        const std::string      all    = std::string(full.substr(0, 1)) + std::string(full.substr(2, full_e - 2));
        const auto             n      = digits.size();
        if (std::atoi(std::string(full.substr(full_e + 1)).c_str()) == exponent &&
            all.compare(0, n, digits) == 0 && all[n] == '5' &&
            all.find_first_not_of('0', n + 1) == std::string::npos) {
            std::string up = digits;
            ++up.back();
            const std::string candidate = up.substr(0, 1) + "." + up.substr(1) + "e" + std::to_string(exponent);
            double            back {};
            std::from_chars(candidate.data(), candidate.data() + candidate.size(), back);
            if (back == magnitude) digits = up;
        }
    }
    if (magnitude < 1e-4 || magnitude >= 1e16) {
        std::string out = sign + digits.substr(0, 1);
        if (digits.size() > 1) out += "." + digits.substr(1);
        return out + (exponent < 0 ? "e" : "e+") + std::to_string(exponent);
    }
    const int point = exponent + 1; // 小数点位于第 point 个数字之后
    if (point <= 0) return sign + "0." + std::string(-point, '0') + digits;
    if (point < static_cast<int>(digits.size()))
        return sign + digits.substr(0, point) + "." + digits.substr(point);
    return sign + digits + std::string(point - digits.size(), '0') + ".0";
}

inline void Write(std::string& out, const Value& value, std::optional<std::size_t> indent,
                  std::size_t depth = 0) {
    auto newline_indent = [&](std::size_t level) {
        if (! indent) return;
        out += '\n';
        out.append(level * *indent, ' ');
    };
    switch (value.type()) {
    case Value::value_t::number_float: out += FormatFloat(value.get<double>()); return;
    case Value::value_t::array: {
        out += '[';
        if (value.empty()) {
            out += ']';
            return;
        }
        bool first = true;
        for (const auto& item : value) {
            if (! first) out += ',';
            first = false;
            newline_indent(depth + 1);
            Write(out, item, indent, depth + 1);
        }
        newline_indent(depth);
        out += ']';
        return;
    }
    case Value::value_t::object: {
        out += '{';
        if (value.empty()) {
            out += '}';
            return;
        }
        bool first = true;
        for (const auto& [key, item] : value.items()) {
            if (! first) out += ',';
            first = false;
            newline_indent(depth + 1);
            out += Value(key).dump();
            out += indent ? ": " : ":";
            Write(out, item, indent, depth + 1);
        }
        newline_indent(depth);
        out += '}';
        return;
    }
    default: out += value.dump(); return; // null/bool/整数/字符串：nlohmann 与 rstd 字节相同
    }
}

inline auto Dump(const Value& value, std::optional<std::size_t> indent = std::nullopt) -> std::string {
    std::string out;
    Write(out, value, indent);
    return out;
}

} // namespace owe::nljson
