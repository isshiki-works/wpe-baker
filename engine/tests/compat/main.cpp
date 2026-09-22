// T2a 差分测试驱动：对每个用例分别以子进程运行 rstd 侧与 compat 侧，比较退出码、stdout 与归一化后的 stderr。
//   compat-difftest [过滤子串]         运行全部（或名字含过滤串的）用例，原始输出落盘 D:/Periodica/runs/T2a/difftest/
//   compat-difftest --run <side> <名>  子进程模式：运行单个用例，结果写 stdout
#include "difftest.hpp"

#include <filesystem>
#include <fstream>
#include <regex>
#include <set>
#include <sstream>
#include <stdlib.h>
#if defined(_WIN32)
#include <windows.h>
#endif

namespace side_rstd
{
auto cases() -> std::vector<DiffCase> const&;
}
namespace side_compat
{
auto cases() -> std::vector<DiffCase> const&;
}

namespace
{
// 预期差异（登记项）：rstd 校验 UTF-8，compat 不校验；HashMap::get 的 Option<ref<V>> 在 compat 是指针大小、rstd 不是（只差 sizeof）。
std::set<std::string> const EXPECTED_DIFF { "as_str_invalid_utf8", "option_ref_size" };

auto find_case(std::vector<DiffCase> const& table, std::string_view name) -> DiffCase const* {
    for (auto const& c : table)
        if (name == c.name) return &c;
    return nullptr;
}

auto read_file(std::filesystem::path const& p) -> std::string {
    std::ifstream in(p, std::ios::binary);
    std::stringstream ss;
    ss << in.rdbuf();
    return ss.str();
}

// panic 位置：落在 cases.inc 的（调用点位置）逐字比较，落在库内的只比较"在库内"。时间戳归一化。
auto normalize(std::string s) -> std::string {
    static std::regex const loc(R"(panicked at ([^\n]*):(\d+):(\d+):)");
    std::string out;
    auto begin = std::sregex_iterator(s.begin(), s.end(), loc);
    std::size_t last = 0;
    for (auto it = begin; it != std::sregex_iterator(); ++it) {
        auto const& m = *it;
        out += s.substr(last, m.position() - last);
        std::string file = m[1].str();
        if (file.ends_with("cases.inc")) out += "panicked at cases.inc:" + m[2].str() + ":" + m[3].str() + ":";
        else out += "panicked at <LIB>:";
        last = m.position() + m.length();
    }
    out += s.substr(last);
    static std::regex const ts(R"(\[\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ )");
    return std::regex_replace(out, ts, "[TS ");
}

auto child(char const* side, char const* name) -> int {
#if defined(_WIN32)
    SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX);
    _set_abort_behavior(0, _WRITE_ABORT_MSG | _CALL_REPORTFAULT);
#endif
    auto const& table = std::string_view(side) == "rstd" ? side_rstd::cases() : side_compat::cases();
    auto const* c = find_case(table, name);
    if (c == nullptr) return 90;
    auto r = c->run();
    std::fwrite(r.data(), 1, r.size(), stdout);
    std::fflush(stdout);
    return 0;
}
} // namespace

auto main(int argc, char** argv) -> int {
    if (argc == 4 && std::string_view(argv[1]) == "--run") return child(argv[2], argv[3]);
    std::string filter = argc >= 2 ? argv[1] : "";
    std::filesystem::path const dir = "D:/Periodica/runs/T2a/difftest";
    std::filesystem::create_directories(dir);
    std::string exe = std::filesystem::absolute(argv[0]).make_preferred().string();

    int fails = 0, expected = 0, passes = 0;
    std::ofstream summary(dir / "summary.txt", std::ios::binary);
    for (auto const& c : side_rstd::cases()) {
        if (! filter.empty() && std::string_view(c.name).find(filter) == std::string_view::npos) continue;
        if (find_case(side_compat::cases(), c.name) == nullptr) {
            std::printf("MISSING %s\n", c.name);
            ++fails;
            continue;
        }
        struct Run {
            int rc;
            std::string out, err;
        } runs[2];
        char const* sides[2] = { "rstd", "compat" };
        for (int i = 0; i < 2; ++i) {
            auto base = (dir / (std::string(c.name) + "." + sides[i])).make_preferred().string();
            auto cmd  = exe + " --run " + sides[i] + " " + c.name + " > " + base + ".out 2> " + base + ".err";
            runs[i].rc  = std::system(cmd.c_str());
            runs[i].out = read_file(base + ".out");
            runs[i].err = normalize(read_file(base + ".err"));
        }
        bool same = runs[0].rc == runs[1].rc && runs[0].out == runs[1].out && runs[0].err == runs[1].err;
        char const* verdict = same ? "PASS" : EXPECTED_DIFF.contains(c.name) ? "EXPECTED-DIFF" : "DIFF";
        (same ? passes : EXPECTED_DIFF.contains(c.name) ? expected : fails) += 1;
        auto line = std::format("{} {} rc={}/{}\n", verdict, c.name, runs[0].rc, runs[1].rc);
        if (! same) {
            if (runs[0].out != runs[1].out) line += std::format("  out rstd  : {}\n  out compat: {}\n", runs[0].out, runs[1].out);
            if (runs[0].err != runs[1].err) line += std::format("  err rstd  : {}\n  err compat: {}\n", runs[0].err, runs[1].err);
        }
        std::fputs(line.c_str(), stdout);
        summary << line;
    }
    auto tail = std::format("pass={} expected_diff={} fail={}\n", passes, expected, fails);
    std::fputs(tail.c_str(), stdout);
    summary << tail;
    return fails == 0 ? 0 : 1;
}
