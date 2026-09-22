#pragma once
// T2a 差分测试的公共部分：两侧（rstd / compat）各自导出同名用例表，main 逐例比较。
#include <bit>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <format>
#include <limits>
#include <string>
#include <string_view>
#include <type_traits>
#include <vector>

struct DiffCase {
    char const* name;
    std::string (*run)();
};

// 把结果拼成 "键=值;"：浮点用十六进制表示（逐位比较），整数十进制。
struct Out {
    std::string s;
    template<typename T>
    auto operator()(char const* key, T const& v) -> Out& {
        s += key;
        s += '=';
        if constexpr (std::is_same_v<T, bool>) s += v ? "true" : "false";
        else if constexpr (std::is_floating_point_v<T>) s += std::format("{:a}", v);
        else if constexpr (std::is_integral_v<T>) s += std::to_string(v);
        else s += std::string_view(v);
        s += ';';
        return *this;
    }
};
