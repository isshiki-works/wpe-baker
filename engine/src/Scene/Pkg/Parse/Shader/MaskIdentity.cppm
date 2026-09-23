module;

#include <algorithm>
#include <cstdint>
#include <initializer_list>
#include <tuple>
#include <utility>
#include <cstdlib>
#include <functional>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

export module wescene.pkg.parse:mask_identity;

// M2 遮罩支撑区：着色器源码的规范化与"遮罩为 0 处恒等"结构识别（纯文本、无引擎依赖，便于单测）。
// 方案与论证见 runs/M2/design.json 与 runs/M2/seg2/design-ext.json。
export namespace owe::m2
{

// ---------- 规范化 ----------
// 去掉 \r、注释与空白差异，得到 token 序列。预处理行整行一个 token（'#'+指令词以单空格连接）。
// 注解注释（// {...}、// [COMBO]）一并去掉：注解里的默认值与 combo 默认值由调用方从实际载入的
// 着色器信息（ShaderInfo 的 svs/combos/alias）取有效值核对，不靠源码比对。
inline bool IsIdStart(char c) { return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '_'; }
inline bool IsDigit(char c) { return c >= '0' && c <= '9'; }
inline bool IsIdCont(char c) { return IsIdStart(c) || IsDigit(c); }

inline std::vector<std::string> Tokenize(std::string_view src) {
    std::string s;
    s.reserve(src.size());
    for (char c : src)
        if (c != '\r') s.push_back(c);
    std::vector<std::string> t;
    const std::size_t        n = s.size();
    std::size_t              i = 0;
    bool                     line_start = true;
    auto                     starts     = [&](std::size_t at, const char* p) {
        return s.compare(at, std::char_traits<char>::length(p), p) == 0;
    };
    while (i < n) {
        const char c = s[i];
        if (c == '\n') {
            line_start = true;
            i++;
            continue;
        }
        if (c == ' ' || c == '\t' || c == '\f' || c == '\v') {
            i++;
            continue;
        }
        if (starts(i, "//")) {
            const auto j = s.find('\n', i);
            i            = j == std::string::npos ? n : j;
            continue;
        }
        if (starts(i, "/*")) {
            const auto j = s.find("*/", i + 2);
            i            = j == std::string::npos ? n : j + 2;
            continue;
        }
        if (c == '#' && line_start) {
            std::string line;
            i++;
            while (i < n) {
                if (s[i] == '\\' && i + 1 < n && s[i + 1] == '\n') {
                    i += 2;
                    continue;
                }
                if (s[i] == '\n') break;
                line.push_back(s[i++]);
            }
            // 行内注释去掉，空白合并
            std::string body;
            for (std::size_t k = 0; k < line.size();) {
                if (line.compare(k, 2, "//") == 0) break;
                if (line.compare(k, 2, "/*") == 0) {
                    const auto e = line.find("*/", k + 2);
                    k            = e == std::string::npos ? line.size() : e + 2;
                    body.push_back(' ');
                    continue;
                }
                body.push_back(line[k++]);
            }
            std::string tok = "#";
            bool        first = true;
            for (std::size_t k = 0; k < body.size();) {
                while (k < body.size() && (body[k] == ' ' || body[k] == '\t' || body[k] == '\f' ||
                                           body[k] == '\v'))
                    k++;
                if (k >= body.size()) break;
                const std::size_t b = k;
                while (k < body.size() && ! (body[k] == ' ' || body[k] == '\t' ||
                                             body[k] == '\f' || body[k] == '\v'))
                    k++;
                if (! first) tok.push_back(' ');
                tok.append(body, b, k - b);
                first = false;
            }
            t.push_back(std::move(tok));
            continue;
        }
        line_start = false;
        if (IsIdStart(c)) {
            std::size_t j = i + 1;
            while (j < n && IsIdCont(s[j])) j++;
            t.emplace_back(s, i, j - i);
            i = j;
            continue;
        }
        if (IsDigit(c) || (c == '.' && i + 1 < n && IsDigit(s[i + 1]))) {
            std::size_t j   = i + 1;
            const bool  hex = s.compare(i, 2, "0x") == 0 || s.compare(i, 2, "0X") == 0;
            while (j < n) {
                if (IsIdCont(s[j]) || s[j] == '.')
                    j++;
                else if ((s[j] == '+' || s[j] == '-') && (s[j - 1] == 'e' || s[j - 1] == 'E') &&
                         ! hex)
                    j++;
                else
                    break;
            }
            t.emplace_back(s, i, j - i);
            i = j;
            continue;
        }
        static constexpr const char* kOps2[] = { "+=", "-=", "*=", "/=", "==", "!=", "<=",
                                                 ">=", "&&", "||", "++", "--", "<<", ">>" };
        bool                         two     = false;
        for (const char* op : kOps2) {
            if (starts(i, op)) {
                t.emplace_back(op);
                i += 2;
                two = true;
                break;
            }
        }
        if (two) continue;
        t.emplace_back(1, c);
        i++;
    }
    return t;
}

inline std::uint64_t Fnv1a(std::uint64_t h, std::string_view bytes) {
    for (unsigned char c : bytes) h = (h ^ c) * 0x100000001b3ull;
    return h;
}

// 规范化哈希：vert、frag、两者直接 #include 的头文件（按名字排序去重）的 token 序列。
// 读头文件失败返回空。
inline std::optional<std::uint64_t>
NormalizedSourceHash(std::string_view vert, std::string_view frag,
                     const std::function<std::optional<std::string>(const std::string&)>& read_include) {
    const auto               tv = Tokenize(vert);
    const auto               tf = Tokenize(frag);
    std::vector<std::string> includes;
    for (const auto* part : { &tv, &tf }) {
        for (const auto& tok : *part) {
            constexpr std::string_view kInc = "#include \"";
            if (tok.size() > kInc.size() + 1 && tok.compare(0, kInc.size(), kInc) == 0 &&
                tok.back() == '"')
                includes.push_back(tok.substr(kInc.size(), tok.size() - kInc.size() - 1));
        }
    }
    std::sort(includes.begin(), includes.end());
    includes.erase(std::unique(includes.begin(), includes.end()), includes.end());
    std::uint64_t h    = 0xcbf29ce484222325ull;
    auto          feed = [&](const std::vector<std::string>& toks) {
        for (const auto& tok : toks) {
            h = Fnv1a(h, tok);
            h = Fnv1a(h, std::string_view("\0", 1));
        }
        h = Fnv1a(h, "\x01");
    };
    feed(tv);
    feed(tf);
    for (const auto& inc : includes) {
        auto text = read_include(inc);
        if (! text) return std::nullopt;
        feed(Tokenize(*text));
    }
    return h;
}

// ---------- 按 combo 求值的简单预处理 ----------
// 只认 #if/#elif 的 NAME、!NAME、NAME ==/!=/>/</>=/<= 整数、#ifdef/#ifndef、#else、#endif；
// 其它条件形式返回空（调用方全画）。未定义的名字按 C 预处理语义取 0。
// 活动区里的非条件指令（#include 等）原样保留在输出里。
using ComboLookup = std::function<std::optional<long>(const std::string&)>;

inline std::optional<bool> EvalCondition(const std::string& expr, const ComboLookup& combo) {
    // 按空格切词；"!NAME" 拆成 "!" 与 "NAME"（"!=" 不拆）
    std::vector<std::string> w;
    for (std::size_t k = 0; k < expr.size();) {
        while (k < expr.size() && expr[k] == ' ') k++;
        if (k >= expr.size()) break;
        const std::size_t b = k;
        while (k < expr.size() && expr[k] != ' ') k++;
        std::string word(expr, b, k - b);
        if (word.size() > 1 && word[0] == '!' && word[1] != '=') {
            w.emplace_back("!");
            word.erase(0, 1);
        }
        w.push_back(std::move(word));
    }
    auto value = [&](const std::string& name) -> std::optional<long> {
        if (name.empty() || ! IsIdStart(name[0])) return std::nullopt;
        for (char ch : name)
            if (! IsIdCont(ch)) return std::nullopt;
        auto v = combo(name);
        return v ? *v : 0;
    };
    auto integer = [](const std::string& text) -> std::optional<long> {
        if (text.empty()) return std::nullopt;
        char* end = nullptr;
        long  v   = std::strtol(text.c_str(), &end, 10);
        if (end == nullptr || *end != '\0') return std::nullopt;
        return v;
    };
    if (w.size() == 1) {
        if (auto lit = integer(w[0])) return *lit != 0;
        auto v = value(w[0]);
        if (! v) return std::nullopt;
        return *v != 0;
    }
    if (w.size() == 2 && w[0] == "!") {
        auto v = value(w[1]);
        if (! v) return std::nullopt;
        return *v == 0;
    }
    if (w.size() == 3) {
        auto a = value(w[0]);
        auto b = integer(w[2]);
        if (! a || ! b) return std::nullopt;
        if (w[1] == "==") return *a == *b;
        if (w[1] == "!=") return *a != *b;
        if (w[1] == ">") return *a > *b;
        if (w[1] == "<") return *a < *b;
        if (w[1] == ">=") return *a >= *b;
        if (w[1] == "<=") return *a <= *b;
    }
    return std::nullopt;
}

inline std::optional<std::vector<std::string>> ActiveTokens(const std::vector<std::string>& toks,
                                                           const ComboLookup& combo) {
    struct Frame {
        bool parent_active;
        bool taken;  // 本组已有分支为真
        bool active; // 当前分支活动
    };
    std::vector<Frame>       stack;
    std::vector<std::string> out;
    auto                     cur_active = [&] { return stack.empty() || stack.back().active; };
    auto                     starts     = [](const std::string& t, std::string_view p) {
        return t.size() >= p.size() && t.compare(0, p.size(), p) == 0 &&
               (t.size() == p.size() || t[p.size()] == ' ');
    };
    for (const auto& tok : toks) {
        if (tok.empty() || tok[0] != '#') {
            if (cur_active()) out.push_back(tok);
            continue;
        }
        if (starts(tok, "#ifdef") || starts(tok, "#ifndef")) {
            const bool        neg  = starts(tok, "#ifndef");
            const std::string name = tok.substr(neg ? 8 : 7);
            if (name.empty() || name.find(' ') != std::string::npos) return std::nullopt;
            const bool defined = combo(name).has_value();
            const bool parent  = cur_active();
            const bool cond    = neg ? ! defined : defined;
            stack.push_back({ parent, cond, parent && cond });
        } else if (starts(tok, "#if")) {
            auto cond = EvalCondition(tok.size() > 4 ? tok.substr(4) : std::string(), combo);
            if (! cond) return std::nullopt;
            const bool parent = cur_active();
            stack.push_back({ parent, *cond, parent && *cond });
        } else if (starts(tok, "#elif")) {
            if (stack.empty()) return std::nullopt;
            auto cond = EvalCondition(tok.size() > 6 ? tok.substr(6) : std::string(), combo);
            if (! cond) return std::nullopt;
            auto& f  = stack.back();
            f.active = f.parent_active && ! f.taken && *cond;
            f.taken  = f.taken || *cond;
        } else if (tok == "#else") {
            if (stack.empty()) return std::nullopt;
            auto& f  = stack.back();
            f.active = f.parent_active && ! f.taken;
            f.taken  = true;
        } else if (tok == "#endif") {
            if (stack.empty()) return std::nullopt;
            stack.pop_back();
        } else if (cur_active()) {
            out.push_back(tok);
        }
    }
    if (! stack.empty()) return std::nullopt;
    return out;
}

// ---------- 结构识别 ----------
enum class Form { Warp, Mix };

// 数值有限性前提：调用方按 uniform 有效值（材质常量或注解默认值、且无动画/脚本/用户属性绑定）核对。
struct Guards {
    std::vector<std::string>                              positive; // pow 指数：> 0
    std::vector<std::string>                              nonzero;  // 除数：各分量 ≠ 0
    std::vector<std::pair<std::string, std::string>>      distinct; // smoothstep 两端：不相等
};

struct Recognition {
    bool        ok { false };
    Form        form { Form::Warp };
    std::size_t mask_slot { 0 };
    Guards      guards;
    std::string reason;
};

struct Stmt {
    std::vector<std::string> t;
    int                      depth; // 相对 main 函数体：1 = 顶层
};

// 截出 main 函数体并按 ';'、'{'、'}' 切语句（圆括号内不切）。找不到返回空。
inline std::optional<std::vector<Stmt>> MainStatements(const std::vector<std::string>& toks) {
    std::size_t start = std::string::npos;
    for (std::size_t i = 0; i + 4 < toks.size(); i++) {
        if (toks[i] == "void" && toks[i + 1] == "main" && toks[i + 2] == "(" &&
            ((toks[i + 3] == ")" && toks[i + 4] == "{") ||
             (i + 5 < toks.size() && toks[i + 3] == "void" && toks[i + 4] == ")" &&
              toks[i + 5] == "{"))) {
            if (start != std::string::npos) return std::nullopt; // 两个 main
            start = toks[i + 3] == ")" ? i + 5 : i + 6;
        }
    }
    if (start == std::string::npos) return std::nullopt;
    std::vector<Stmt> out;
    Stmt              cur { {}, 1 };
    int               depth = 1, paren = 0;
    for (std::size_t i = start; i < toks.size(); i++) {
        const auto& tk = toks[i];
        if (tk == "(") paren++;
        if (tk == ")") paren--;
        if (paren == 0 && (tk == ";" || tk == "{" || tk == "}")) {
            if (! cur.t.empty()) out.push_back(cur);
            cur.t.clear();
            if (tk == "{") depth++;
            if (tk == "}") {
                depth--;
                if (depth == 0) return out;
            }
            cur.depth = depth;
            continue;
        }
        if (cur.t.empty()) cur.depth = depth;
        cur.t.push_back(tk);
    }
    return std::nullopt;
}

inline bool IsIdent(const std::string& t) {
    if (t.empty() || ! IsIdStart(t[0])) return false;
    for (char c : t)
        if (! IsIdCont(c)) return false;
    return true;
}

inline bool IsNumber(const std::string& t) { return ! t.empty() && (IsDigit(t[0]) || t[0] == '.'); }

inline std::optional<double> NumberValue(const std::string& t) {
    std::string s = t;
    while (! s.empty() && (s.back() == 'f' || s.back() == 'F' || s.back() == 'u' || s.back() == 'U' ||
                           s.back() == 'h' || s.back() == 'H'))
        s.pop_back();
    if (s.empty()) return std::nullopt;
    char*  end = nullptr;
    double v   = std::strtod(s.c_str(), &end);
    if (end == nullptr || *end != '\0') return std::nullopt;
    return v;
}

// 按顶层（括号深度 0）分隔符切分
inline std::vector<std::vector<std::string>> SplitTop(const std::vector<std::string>& t, std::size_t b,
                                                      std::size_t e, const std::string& sep) {
    std::vector<std::vector<std::string>> parts(1);
    int                                   d = 0;
    for (std::size_t i = b; i < e; i++) {
        if (t[i] == "(" || t[i] == "[") d++;
        if (t[i] == ")" || t[i] == "]") d--;
        if (d == 0 && t[i] == sep) {
            parts.emplace_back();
            continue;
        }
        parts.back().push_back(t[i]);
    }
    return parts;
}

inline bool Eq(const std::vector<std::string>& a, std::initializer_list<const char*> b) {
    if (a.size() != b.size()) return false;
    std::size_t i = 0;
    for (const char* x : b)
        if (a[i++] != x) return false;
    return true;
}

// "g_TextureK" → K（K ≥ 1）
inline std::optional<std::size_t> TextureSlot(const std::string& t, std::string_view suffix = {}) {
    constexpr std::string_view p = "g_Texture";
    if (t.size() <= p.size() + suffix.size() || t.compare(0, p.size(), p) != 0) return std::nullopt;
    if (! suffix.empty() && t.compare(t.size() - suffix.size(), suffix.size(), suffix) != 0)
        return std::nullopt;
    const std::string digits = t.substr(p.size(), t.size() - p.size() - suffix.size());
    if (digits.empty() || digits.size() > 2) return std::nullopt;
    for (char c : digits)
        if (! IsDigit(c)) return std::nullopt;
    return std::size_t(std::stoul(digits));
}

// 简单操作数：字面量、标识符、标识符.分量
inline std::optional<std::string> SimpleOperand(const std::vector<std::string>& t) {
    if (t.size() == 1 && (IsNumber(t[0]) || IsIdent(t[0]))) return t[0];
    if (t.size() == 3 && IsIdent(t[0]) && t[1] == "." && IsIdent(t[2])) return t[0] + "." + t[2];
    return std::nullopt;
}

// 收集整段活动源码里的数值前提；遇到无法判定有限性的写法返回原因。
inline std::optional<std::string> CollectGuards(const std::vector<std::string>& t, Guards& g) {
    static constexpr const char* kReject[] = { "sqrt",  "inversesqrt", "log",   "log2", "asin",
                                               "acos",  "atan",        "tan",   "normalize",
                                               "mod",   "fmod",        "inverse", "exp", "exp2",
                                               "ldexp", "cosh",        "sinh",  "tanh" };
    for (std::size_t i = 0; i < t.size(); i++) {
        for (const char* r : kReject)
            if (t[i] == r && i + 1 < t.size() && t[i + 1] == "(")
                return std::string("用到可能产生 Inf/NaN 的函数 ") + r;
        if (t[i] == "%") return std::string("用到 % 运算");
        if ((t[i] == "pow" || t[i] == "smoothstep") && i + 1 < t.size() && t[i + 1] == "(") {
            int         d = 0;
            std::size_t e = i + 1;
            for (; e < t.size(); e++) {
                if (t[e] == "(") d++;
                if (t[e] == ")" && --d == 0) break;
            }
            if (e >= t.size()) return std::string("括号不配对");
            auto args = SplitTop(t, i + 2, e, ",");
            if (t[i] == "pow") {
                if (args.size() != 2) return std::string("pow 参数个数不对");
                auto b = SimpleOperand(args[1]);
                if (! b) return std::string("pow 指数不是简单操作数");
                if (IsNumber(*b)) {
                    auto v = NumberValue(*b);
                    if (! v || ! (*v > 0.0)) return std::string("pow 指数字面量 ≤ 0");
                } else {
                    g.positive.push_back(*b);
                }
            } else {
                if (args.size() != 3) return std::string("smoothstep 参数个数不对");
                auto a = SimpleOperand(args[0]);
                auto b = SimpleOperand(args[1]);
                if (! a || ! b) return std::string("smoothstep 端点不是简单操作数");
                g.distinct.emplace_back(*a, *b);
            }
        }
        if ((t[i] == "/" || t[i] == "/=") && i + 1 < t.size()) {
            std::vector<std::string> rhs { t[i + 1] };
            if (i + 3 < t.size() && t[i + 2] == "." && IsIdent(t[i + 3])) {
                rhs.push_back(t[i + 2]);
                rhs.push_back(t[i + 3]);
            }
            const std::size_t next = i + 1 + rhs.size();
            if (next < t.size() && (t[next] == "(" || t[next] == "[" || t[next] == "."))
                return std::string("除数不是简单操作数");
            auto op = SimpleOperand(rhs);
            if (! op) return std::string("除数不是简单操作数");
            if (IsNumber(*op)) {
                auto v = NumberValue(*op);
                if (! v || *v == 0.0) return std::string("除数字面量为 0");
            } else if (! TextureSlot(rhs[0], "Resolution").has_value()) {
                g.nonzero.push_back(*op);
            }
        }
    }
    return std::nullopt;
}

inline bool IsAssignOp(const std::string& t) {
    return t == "=" || t == "+=" || t == "-=" || t == "*=" || t == "/=" || t == "++" || t == "--";
}

// 语句里标识符 name 出现的次数
inline std::size_t Count(const std::vector<std::string>& t, const std::string& name) {
    std::size_t n = 0;
    for (const auto& x : t) n += x == name;
    return n;
}

// "float M = texSample2D ( g_TextureK , v_TexCoord . zw ) . r" → (M, K)
inline std::optional<std::pair<std::string, std::size_t>> MaskDecl(const Stmt& s) {
    const auto& t = s.t;
    if (t.size() != 13 || t[0] != "float" || ! IsIdent(t[1]) || t[2] != "=" || t[3] != "texSample2D" ||
        t[4] != "(" || t[6] != "," || t[7] != "v_TexCoord" || t[8] != "." || t[9] != "zw" ||
        t[10] != ")" || t[11] != "." || t[12] != "r")
        return std::nullopt;
    auto k = TextureSlot(t[5]);
    if (! k || *k == 0) return std::nullopt;
    return std::make_pair(t[1], *k);
}

inline bool IsInputSample(const std::vector<std::string>& t) {
    return Eq(t, { "texSample2D", "(", "g_Texture0", ",", "v_TexCoord", ".", "xy", ")" });
}

// 顶点着色器：gl_Position 走标准 MVP，v_TexCoord = a_TexCoord.xyxy，zw 按遮罩槽 K 的分辨率缩放；
// v_TexCoord 与 gl_Position 没有别的写入。
inline std::optional<std::string> CheckVertex(const std::vector<std::string>& vert, std::size_t slot) {
    auto stmts = MainStatements(vert);
    if (! stmts) return std::string("顶点着色器找不到 main");
    const std::string R = "g_Texture" + std::to_string(slot) + "Resolution";
    bool              pos = false, base = false, z = false, w = false, zw = false;
    for (const auto& s : *stmts) {
        const auto& t = s.t;
        if (Count(t, "gl_Position")) {
            if (pos || s.depth != 1 ||
                ! (Eq(t, { "gl_Position", "=", "mul", "(", "vec4", "(", "a_Position", ",", "1.0", ")",
                           ",", "g_ModelViewProjectionMatrix", ")" }) ||
                   Eq(t, { "gl_Position", "=", "mul", "(", "vec4", "(", "a_Position", ",", "1", ")",
                           ",", "g_ModelViewProjectionMatrix", ")" })))
                return std::string("gl_Position 不是标准 MVP 或有多处写入");
            pos = true;
            continue;
        }
        if (! Count(t, "v_TexCoord")) continue;
        if (s.depth != 1) return std::string("v_TexCoord 在嵌套块里写入");
        if (Eq(t, { "v_TexCoord", "=", "a_TexCoord", ".", "xyxy" }) && ! base) {
            base = true;
        } else if (t.size() == 11 && t[0] == "v_TexCoord" && t[1] == "." && t[2] == "z" && t[3] == "*=" &&
                   t[4] == R && t[5] == "." && t[6] == "z" && t[7] == "/" && t[8] == R && t[9] == "." &&
                   t[10] == "x" && ! z && base) {
            z = true;
        } else if (t.size() == 11 && t[0] == "v_TexCoord" && t[1] == "." && t[2] == "w" && t[3] == "*=" &&
                   t[4] == R && t[5] == "." && t[6] == "w" && t[7] == "/" && t[8] == R && t[9] == "." &&
                   t[10] == "y" && ! w && base) {
            w = true;
        } else {
            std::vector<std::string> want = { "v_TexCoord", ".", "zw", "=", "vec2", "(", "a_TexCoord", ".",
                                              "x", "*", R, ".", "z", "/", R, ".", "x", ",", "a_TexCoord",
                                              ".", "y", "*", R, ".", "w", "/", R, ".", "y", ")" };
            if (t == want && ! zw && base) {
                zw = true;
            } else {
                return std::string("v_TexCoord 有不认识的写法");
            }
        }
    }
    if (! pos) return std::string("没有 gl_Position 写入");
    if (! base || ! ((z && w && ! zw) || (zw && ! z && ! w))) return std::string("v_TexCoord 遮罩坐标不是标准缩放");
    return std::nullopt;
}

// 识别两种形态（每种都有单测）：
//  Warp：vec2 T = v_TexCoord.xy；其余对 T 的写入只有 "T += a*b*…*M"（M 是遮罩采样且是整条积的最后一个因子）；
//        最后一句 gl_FragColor = texSample2D(g_Texture0, T)。M=0 时每次加 ±0，T 逐位不变。
//  Mix： gl_FragColor = mix(S, E, M)，或 A = mix(S, E, M) 紧接 gl_FragColor = A /
//        gl_FragColor = vec4(max(CAST3(0), A.rgb), A.a)；S = texSample2D(g_Texture0, v_TexCoord.xy)。
//        M=0 且 E 有限时 mix 结果 = S；UNORM 采样 ≥ 0，max(0,·) 不改变。
// 共同要求：遮罩 M = texSample2D(g_TextureK, v_TexCoord.zw).r 只声明一次、只在上述位置使用；
// main 里没有 discard/return；gl_FragColor 只在最后一句写；没有 #define；顶点着色器通过 CheckVertex。
inline Recognition Recognize(const std::vector<std::string>& vert, const std::vector<std::string>& frag) {
    Recognition r;
    auto        fail = [&](std::string why) {
        r.ok     = false;
        r.reason = std::move(why);
        return r;
    };
    for (const auto* part : { &vert, &frag })
        for (const auto& tk : *part)
            if (tk.compare(0, 7, "#define") == 0 || tk.compare(0, 6, "#undef") == 0)
                return fail("活动源码里有 #define/#undef");
    auto stmts_opt = MainStatements(frag);
    if (! stmts_opt || stmts_opt->empty()) return fail("片元着色器找不到 main");
    const auto& st = *stmts_opt;
    for (const auto& s : st)
        for (const auto& tk : s.t)
            if (tk == "discard" || tk == "return") return fail("main 里有 discard/return");
    std::size_t fc = 0;
    for (const auto& tk : frag) fc += tk == "gl_FragColor";
    const auto& last = st.back();
    if (fc != 1 || last.depth != 1 || last.t.size() < 3 || last.t[0] != "gl_FragColor" || last.t[1] != "=")
        return fail("gl_FragColor 不是只在 main 最后一句写一次");
    const std::vector<std::string> rhs(last.t.begin() + 2, last.t.end());

    // 遮罩变量
    auto mask_of = [&](const std::string& m) -> std::optional<std::size_t> {
        std::optional<std::size_t> slot;
        for (const auto& s : st) {
            if (auto d = MaskDecl(s); d && d->first == m) {
                if (slot || s.depth != 1) return std::nullopt;
                slot = d->second;
            }
        }
        return slot;
    };

    std::string M;
    // ---- Warp ----
    if (rhs.size() == 6 && rhs[0] == "texSample2D" && rhs[1] == "(" && rhs[2] == "g_Texture0" &&
        rhs[3] == "," && IsIdent(rhs[4]) && rhs[5] == ")") {
        const std::string T    = rhs[4];
        bool              decl = false;
        std::size_t       adds = 0;
        for (std::size_t i = 0; i + 1 < st.size(); i++) {
            const auto& t = st[i].t;
            if (! Count(t, T)) continue;
            if (Eq(t, { "vec2", T.c_str(), "=", "v_TexCoord", ".", "xy" })) {
                if (decl || st[i].depth != 1 || adds) return fail("T 声明不唯一或位置不对");
                decl = true;
                continue;
            }
            if (t.size() >= 4 && t[0] == T && t[1] == "+=" && Count(t, T) == 1) {
                auto factors = SplitTop(t, 2, t.size(), "*");
                for (const auto& f : factors)
                    if (f.empty()) return fail("T += 表达式为空因子");
                // 顶层只能有 '*'：检查每个因子里没有顶层的其它二元运算符
                for (const auto& f : factors) {
                    int d = 0;
                    for (std::size_t k = 0; k < f.size(); k++) {
                        if (f[k] == "(" || f[k] == "[") d++;
                        if (f[k] == ")" || f[k] == "]") d--;
                        if (d == 0 && (f[k] == "+" || f[k] == "-" || f[k] == "/" || f[k] == "?" ||
                                       f[k] == ":" || f[k] == "," || f[k] == "<" || f[k] == ">" ||
                                       f[k] == "==" || f[k] == "!=" || f[k] == "&&" || f[k] == "||" ||
                                       f[k] == "<=" || f[k] == ">=" || f[k] == "=" || IsAssignOp(f[k])))
                            return fail("T += 表达式顶层不是纯乘积");
                    }
                }
                if (factors.size() < 2 || factors.back().size() != 1 || ! IsIdent(factors.back()[0]))
                    return fail("T += 的最后因子不是遮罩变量");
                const std::string m = factors.back()[0];
                if (! M.empty() && M != m) return fail("T += 用了不同的遮罩变量");
                M = m;
                if (! decl) return fail("T 未声明先累加");
                adds++;
                continue;
            }
            // 拷贝读：X = T 或 TYPE X = T
            if ((t.size() == 3 && IsIdent(t[0]) && t[0] != T && t[1] == "=" && t[2] == T) ||
                (t.size() == 4 && IsIdent(t[0]) && IsIdent(t[1]) && t[1] != T && t[2] == "=" &&
                 t[3] == T))
                continue;
            return fail("T 有不认识的用法");
        }
        if (! decl || adds == 0) return fail("没有 T 声明或 T += 遮罩项");
        // 遮罩变量只允许出现在声明和 T += 的最后因子
        auto slot = mask_of(M);
        if (! slot) return fail("遮罩变量不是唯一的 texSample2D(g_TextureK, v_TexCoord.zw).r 声明");
        for (std::size_t i = 0; i + 1 < st.size(); i++) {
            const auto& t = st[i].t;
            if (! Count(t, M)) continue;
            if (MaskDecl(st[i]) && MaskDecl(st[i])->first == M) continue;
            if (t[0] == rhs[4] && t[1] == "+=" && Count(t, M) == 1 && t.back() == M) continue;
            return fail("遮罩变量有别的用法");
        }
        r.form      = Form::Warp;
        r.mask_slot = *slot;
    } else {
        // ---- Mix ----
        auto parse_mix = [&](const std::vector<std::string>& e)
            -> std::optional<std::tuple<std::vector<std::string>, std::vector<std::string>, std::string>> {
            if (e.size() < 8 || e[0] != "mix" || e[1] != "(" || e.back() != ")") return std::nullopt;
            int d = 0;
            for (std::size_t k = 1; k < e.size(); k++) {
                if (e[k] == "(") d++;
                if (e[k] == ")" && --d == 0 && k != e.size() - 1) return std::nullopt;
            }
            auto args = SplitTop(e, 2, e.size() - 1, ",");
            if (args.size() != 3 || args[2].size() != 1 || ! IsIdent(args[2][0])) return std::nullopt;
            return std::make_tuple(args[0], args[1], args[2][0]);
        };
        std::optional<std::tuple<std::vector<std::string>, std::vector<std::string>, std::string>> mx;
        std::string                                                                            A;
        std::size_t                                                                            mix_idx = 0;
        if (auto direct = parse_mix(rhs)) {
            mx      = direct;
            mix_idx = st.size() - 1;
        } else {
            if (rhs.size() == 1 && IsIdent(rhs[0])) {
                A = rhs[0];
            } else if (rhs.size() == 18 && rhs[0] == "vec4" && rhs[1] == "(" && rhs[2] == "max" &&
                       rhs[3] == "(" && rhs[4] == "CAST3" && rhs[5] == "(" &&
                       (rhs[6] == "0" || rhs[6] == "0.0") && rhs[7] == ")" && rhs[8] == "," &&
                       IsIdent(rhs[9]) && rhs[10] == "." && rhs[11] == "rgb" && rhs[12] == ")" &&
                       rhs[13] == "," && rhs[14] == rhs[9] && rhs[15] == "." && rhs[16] == "a" &&
                       rhs[17] == ")") {
                A = rhs[9];
            } else {
                return fail("最后一句不是认识的形态");
            }
            if (st.size() < 2) return fail("缺少 mix 语句");
            const auto& prev = st[st.size() - 2];
            if (prev.depth != 1 || prev.t.size() < 3 || prev.t[0] != A || prev.t[1] != "=")
                return fail("gl_FragColor 前一句不是 A = mix(...)");
            mx = parse_mix(std::vector<std::string>(prev.t.begin() + 2, prev.t.end()));
            if (! mx) return fail("gl_FragColor 前一句不是 A = mix(S, E, M)");
            mix_idx = st.size() - 2;
        }
        const auto& [s_arg, e_arg, m] = *mx;
        M                               = m;
        std::string S;
        if (! IsInputSample(s_arg)) {
            if (s_arg.size() != 1 || ! IsIdent(s_arg[0])) return fail("mix 第一参数不是输入采样");
            S = s_arg[0];
            bool decl = false;
            for (std::size_t i = 0; i < st.size(); i++) {
                const auto& t = st[i].t;
                if (! Count(t, S)) continue;
                if (t.size() == 11 && t[0] == "vec4" && t[1] == S && t[2] == "=" &&
                    IsInputSample(std::vector<std::string>(t.begin() + 3, t.end()))) {
                    if (decl || st[i].depth != 1 || i > mix_idx) return fail("S 声明不唯一或位置不对");
                    decl = true;
                    continue;
                }
                if (i == mix_idx && Count(t, S) == 1) continue;
                if ((t.size() == 3 && IsIdent(t[0]) && t[0] != S && t[1] == "=" && t[2] == S) ||
                    (t.size() == 4 && IsIdent(t[0]) && IsIdent(t[1]) && t[1] != S && t[2] == "=" &&
                     t[3] == S))
                    continue;
                return fail("S 有不认识的用法");
            }
            if (! decl) return fail("S 没有声明为输入采样");
        }
        auto slot = mask_of(M);
        if (! slot) return fail("遮罩变量不是唯一的 texSample2D(g_TextureK, v_TexCoord.zw).r 声明");
        for (std::size_t i = 0; i < st.size(); i++) {
            const auto& t = st[i].t;
            if (! Count(t, M)) continue;
            if (MaskDecl(st[i]) && MaskDecl(st[i])->first == M) continue;
            if (i == mix_idx && Count(t, M) == 1) continue;
            return fail("遮罩变量有别的用法");
        }
        // A = mix(...) 与最后一句相邻，其间没有别的写入；E 可以读 A（如 mix(sample, albedo, mask)）。
        r.form      = Form::Mix;
        r.mask_slot = *slot;
    }
    if (auto why = CheckVertex(vert, r.mask_slot)) return fail(*why);
    if (auto why = CollectGuards(frag, r.guards)) return fail(*why);
    if (auto why = CollectGuards(vert, r.guards)) return fail(*why);
    r.ok = true;
    return r;
}

} // namespace owe::m2
