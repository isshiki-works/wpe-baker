// T2-3 过渡期的 JSON 语义差分：同一输入分别交给 rstd::json 与 nlohmann（owe::nljson），
// 比较接受/拒绝与写出字节；另测 owe::ParseJson（nlohmann + 过渡桥）读回的 rstd 值。
// rstd 摘除时随 rstd.json 一起删。
//   json-semantic-diff files <清单> <输出.tsv>   清单每行一个 UTF-8 路径
//   json-semantic-diff floats <个数> <种子>       随机 double：写出与读回逐字节比较
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <random>
#include <sstream>
#include <string>

#include "JsonNlohmann.hpp"

import rstd;
import rstd.cppstd;
import rstd.json;
import wescene.json;

using namespace rstd::prelude;

namespace
{

struct Outcome {
    bool        ok { false };
    std::string compact;
    std::string pretty;
};

auto RstdSide(std::string_view text, bool comments, bool trailing, bool& utf8) -> Outcome {
    utf8 = rstd::cppstd::as_str(text).is_ok();
    if (! utf8) return {};
    auto parsed = rstd::json::from_str(rstd::cppstd::as_str(text).unwrap(),
                                       { .allow_comments = comments, .allow_trailing_commas = trailing });
    if (parsed.is_err()) return {};
    auto value  = parsed.unwrap();
    auto pretty = rstd::json::FormatOptions { .pretty = true, .indent = usize(4) };
    return { true,
             rstd::cppstd::to_string(rstd::json::to_string(value)),
             rstd::cppstd::to_string(rstd::json::to_string(value, pretty)) };
}

auto NlSide(std::string_view text, bool comments, bool trailing) -> Outcome {
    owe::nljson::Value value;
    std::string        error;
    if (! owe::nljson::Parse(text, { .allow_comments = comments, .allow_trailing_commas = trailing }, value, error))
        return {};
    return { true, owe::nljson::Dump(value), owe::nljson::Dump(value, 4) };
}

auto BridgeSide(std::string_view text, bool comments, bool trailing) -> Outcome {
    auto parsed = owe::ParseJson(text, { .allow_comments = comments, .allow_trailing_commas = trailing });
    if (parsed.is_err()) return {};
    auto value = parsed.unwrap();
    return { true, rstd::cppstd::to_string(rstd::json::to_string(value)), {} };
}

int Files(const char* list_path, const char* out_path) {
    std::ifstream list(list_path, std::ios::binary);
    std::ofstream out(out_path, std::ios::binary);
    out << "path\topts\tutf8\trstd_ok\tnl_ok\tbridge_ok\tsame_compact\tsame_pretty\tsame_bridge\n";
    std::string path;
    int         files = 0, mismatches = 0;
    while (std::getline(list, path)) {
        if (! path.empty() && path.back() == '\r') path.pop_back();
        if (path.empty()) continue;
        std::ifstream      input(std::filesystem::u8path(path), std::ios::binary);
        std::ostringstream buffer;
        buffer << input.rdbuf();
        const auto text = buffer.str();
        ++files;
        for (int opts = 0; opts < 4; ++opts) {
            const bool comments = opts & 1, trailing = opts & 2;
            bool       utf8     = true;
            auto       rstd_out = RstdSide(text, comments, trailing, utf8);
            auto       nl_out   = NlSide(text, comments, trailing);
            // rstd 的 ParseJson 在非 UTF-8 输入上 unwrap 中止；桥只在 UTF-8 输入上比
            auto bridge_out   = utf8 ? BridgeSide(text, comments, trailing) : Outcome {};
            bool same_compact = rstd_out.ok == nl_out.ok && rstd_out.compact == nl_out.compact;
            bool same_pretty  = rstd_out.ok == nl_out.ok && rstd_out.pretty == nl_out.pretty;
            bool same_bridge  = ! utf8 || (rstd_out.ok == bridge_out.ok && rstd_out.compact == bridge_out.compact);
            if (! (same_compact && same_pretty && same_bridge)) ++mismatches;
            out << path << '\t' << opts << '\t' << utf8 << '\t' << rstd_out.ok << '\t' << nl_out.ok << '\t'
                << bridge_out.ok << '\t' << same_compact << '\t' << same_pretty << '\t' << same_bridge << '\n';
        }
    }
    std::printf("files=%d mismatched_rows=%d\n", files, mismatches);
    return mismatches == 0 ? 0 : 1;
}

int Floats(long count, unsigned seed) {
    std::mt19937_64 random(seed);
    long            written = 0, parsed = 0;
    for (long i = 0; i < count; ++i) {
        double value;
        if (i % 2 == 0) {
            std::uint64_t bits = random();
            std::memcpy(&value, &bits, sizeof value);
        } else {
            // 贴近场景参数的量级：10^-8..10^20 的随机小数
            std::uniform_real_distribution<double> mantissa(-10.0, 10.0);
            std::uniform_int_distribution<int>     exponent(-8, 20);
            value = mantissa(random) * std::pow(10.0, exponent(random));
        }
        if (! std::isfinite(value)) continue;
        auto number   = *rstd::json::Number::from_f64(f64(value));
        auto expected = rstd::cppstd::to_string(rstd::json::to_string(rstd::into<rstd::json::Value>(number)));
        auto actual   = owe::nljson::FormatFloat(value);
        if (expected != actual) {
            if (++written <= 20) std::printf("format %a rstd=%s nl=%s\n", value, expected.c_str(), actual.c_str());
        }
        // 读回：%.17g 文本分别由两边解析再写出
        char text[64];
        std::snprintf(text, sizeof text, "%.17g", value);
        bool utf8   = true;
        auto rstd_r = RstdSide(text, false, false, utf8);
        auto nl_r   = NlSide(text, false, false);
        if (rstd_r.ok != nl_r.ok || rstd_r.compact != nl_r.compact) {
            if (++parsed <= 20) std::printf("parse %s rstd=%s nl=%s\n", text, rstd_r.compact.c_str(), nl_r.compact.c_str());
        }
    }
    std::printf("floats=%ld format_mismatch=%ld parse_mismatch=%ld\n", count, written, parsed);
    return written == 0 && parsed == 0 ? 0 : 1;
}

} // namespace

int main(int argc, char** argv) {
    if (argc == 4 && std::string_view(argv[1]) == "files") return Files(argv[2], argv[3]);
    if (argc == 4 && std::string_view(argv[1]) == "floats") return Floats(std::atol(argv[2]), unsigned(std::atol(argv[3])));
    std::fprintf(stderr, "usage: json-semantic-diff files <list> <out.tsv> | floats <count> <seed>\n");
    return 2;
}
