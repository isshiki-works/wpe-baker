using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Baker.Core;

/// <summary>
/// 脚本（SceneScript）的时间签名，结论域与着色器侧（engine ShaderTime.cppm）一致：静态、周期 P（可带暂态，settle 是上界）、
/// 不能（附证明：读外部输入，线性漂移直达几何属性）、未收敛（附原因码）。"推不下去"只记未收敛，不当证明。
/// 前端：仓库里没有 JS 解析器依赖，引擎的 QuickJS（quickjs-ng）不暴露 AST、也不支持运算符重载，所以这里是最小的 JS 子集解析器；
/// 认不出的语法记未收敛 script_syntax_unsupported。
/// 抽象解释：engine.runtime 是线性时间 t，frametime 是常数，脚本属性取烘焙值，init 只在 t=0 跑一次。每个数值取下面一种形态：
/// 常数、a·t+b、floor(a·t+b)、周期集合、外部输入、随机、说不清（原因码）。sin/cos/tan、取模、x−floor(x) 把线性时间变成周期；
/// 与线性时间比较（min/max/clamp 同理）过交点后固定，交点记进 settle；按周期条件分支的两边按周期合并。
/// 跨帧状态（update 的 value、模块级变量、本层属性）影响输出的，按官方实际行为逐帧精确模拟（写回属性按单精度存），
/// 状态回到出现过的值就是周期（帧），暂态段记 settle；上限内不闭合记未收敛。
/// </summary>
internal static class ScriptTime
{
    internal enum Outcome { Static, Periodic, Cannot, Unconverged }

    /// <summary>
    /// PeriodSeconds：按 engine.runtime 的周期；Retimable 时捕获把这段脚本读到的 runtime 乘倍率调速（<see cref="Retime"/>）。
    /// PeriodFrames：跨帧状态的精确周期（帧），不调速。Settle：秒，此后才进入静态或周期段（上界）。
    /// </summary>
    internal sealed record Verdict(Outcome Outcome, double Settle = 0, double? PeriodSeconds = null, ulong? PeriodFrames = null,
        bool Retimable = false, string Code = "", string Detail = "");

    /// <summary>被烘图层上的一段脚本。Pointer 是相对图层对象的 JSON 指针（场景里的绑定，捕获时可改写）；模型材质里的为 null。</summary>
    internal sealed record Binding(int OwnerLayerId, string Name, string? Pointer, JsonObject Node, JsonObject Owner);

    internal static IEnumerable<Binding> Bindings(JsonObject scene, ProjectSource source, string? assets, IReadOnlyCollection<int> baked)
    {
        foreach (JsonObject layer in (scene["objects"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (SceneGraph.Int(layer["id"]) is not int id || !baked.Contains(id)) continue;
            foreach (Binding binding in Of(layer, id)) yield return binding;
            if (layer["model"] is not JsonValue model || !model.TryGetValue(out string? mdl)) continue;
            // 模型的材质脚本：.mdl 里按明文记着材质 json 的路径
            string text;
            try { text = Encoding.Latin1.GetString(source.Read(mdl)); } catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException) { continue; }
            foreach (string material in Regex.Matches(text, @"materials/[\x21-\x7e]+?\.json").Select(m => m.Value).Distinct())
                foreach (var (pointer, node) in Walk(SceneAnalyzer.ReadResourceJson(source, assets, material), ""))
                    yield return new(id, pointer[(pointer.LastIndexOf('/') + 1)..], null, node, layer);
        }
    }

    /// <summary>图层对象上场景里的脚本绑定（不含模型材质）。</summary>
    internal static IEnumerable<Binding> Of(JsonObject layer, int id) =>
        Walk(layer, "").Select(w => new Binding(id, w.Pointer[(w.Pointer.LastIndexOf('/') + 1)..], w.Pointer, w.Node, layer));

    private static IEnumerable<(string Pointer, JsonObject Node)> Walk(JsonNode? node, string path)
    {
        if (node is JsonObject obj)
        {
            if (obj["script"] is JsonValue) { yield return (path, obj); yield break; }
            foreach (var (key, child) in obj)
                foreach (var item in Walk(child, path + "/" + key.Replace("~", "~0").Replace("/", "~1"))) yield return item;
        }
        else if (node is JsonArray array)
            for (int i = 0; i < array.Count; ++i)
                foreach (var item in Walk(array[i], path + "/" + i.ToString(CultureInfo.InvariantCulture))) yield return item;
    }

    /// <summary>调速：这段脚本读到的 engine.runtime 乘 k，f(t) 变成 f(k·t)，周期与 settle 同除 k。只用于 Retimable 的脚本（runtime 都经字面的 engine.runtime 读）。</summary>
    internal static string Retime(string code, double k) =>
        Regex.Replace(code, @"\bengine\s*\.\s*runtime\b", "(engine.runtime*" + k.ToString("R", CultureInfo.InvariantCulture) + ")");

    internal static Verdict Analyze(Binding binding, uint fpsNumerator, uint fpsDenominator, ulong frameCap)
    {
        if (binding.Node["script"] is not JsonValue value || !value.TryGetValue(out string? code))
            return new(Outcome.Unconverged, Code: "script_source_unreadable");
        X program;
        try { program = new Parser(code).Program(); }
        catch (Exception e) when (e is not OutOfMemoryException) { return new(Outcome.Unconverged, Code: "script_syntax_unsupported", Detail: e.Message); }
        double frametime = (double)fpsDenominator / fpsNumerator;
        var run = new Interp(program, binding, frametime, collect: false);
        try
        {
            run.Setup();
            if (run.Root.Vars.GetValueOrDefault("update") is not Fn update) return new(Outcome.Static);
            State s0 = run.Capture(), before = s0;
            run.Frame(update, 's');
            // 上一帧里变了的状态打上标记再跑一帧：标记流到输出、分支或留在状态里 = 输出依赖跨帧状态。
            // 前几帧里就稳定下来的（只在首帧初始化的开关之类）仍是时间的纯函数，这几帧记进 settle。
            for (int frame = 0; frame < 3; ++frame)
            {
                State now = run.Capture();
                run.Load(now, before);
                var outputs = run.Frame(update, 's');
                if (!run.StateCond && !outputs.Any(o => Tainted(o.Value)) && !run.Capture().Parts.Any(Tainted))
                    return run.Classify(outputs, frame == 0 ? 0 : (frame + 2) * frametime);
                before = now;
            }
            return run.Simulate(update, s0, frameCap);
        }
        catch (Bail bail) { return new(bail.Proof ? Outcome.Cannot : Outcome.Unconverged, Code: bail.Code, Detail: bail.Detail); }
        // 作者脚本是任意代码：解释器自身没料到的情况一律记未收敛，不让分析中断
        catch (Exception e) when (e is not OutOfMemoryException) { return new(Outcome.Unconverged, Code: "script_analysis_error", Detail: e.GetType().Name); }
    }

    /// <summary>
    /// getLayer 取到的图层名：参数按数据流求值（脚本属性取烘焙值、局部变量、数组元素、map/forEach 回调都跟得到），
    /// 各回调按两边分支都走的方式跑一遍。有一处求不出具体名字、或脚本认不出，返回 null，调用方退回旧规则。
    /// </summary>
    internal static string[]? LayerNames(JsonObject node, JsonObject owner)
    {
        if (node["script"] is not JsonValue value || !value.TryGetValue(out string? code)) return null;
        try
        {
            X program = new Parser(code).Program();
            var run = new Interp(program, new(-1, "", null, node, owner), 1.0 / 30, collect: true);
            run.Setup();
            foreach (X fn in All(program).Where(x => x.Op is "fn" or "fdecl"))
                if (!run.Executed.Contains(fn)) run.Call(new Fn(fn, run.Root), null, [.. fn.K[0]!.K.Select(_ => (V)new N('o', Why: "event_argument"))]);
            return run.Unresolved ? null : [.. run.Names.Distinct()];
        }
        catch (Exception e) when (e is not OutOfMemoryException) { return null; }
    }

    private static IEnumerable<X> All(X x) => x.K.OfType<X>().SelectMany(All).Prepend(x);

    // ---------------- 前端：词法 + 递归下降 ----------------

    private sealed class SyntaxError(string message) : Exception(message);

    private sealed class X(string op, string? name = null, double num = 0, params X?[] kids)
    {
        public readonly string Op = op; public readonly string? Name = name; public readonly double Num = num; public readonly X?[] K = kids;
    }

    private sealed record Tok(char K, string S, double N, bool Nl);

    private static readonly string[] Puncts = [">>>=", "...", "===", "!==", "**=", ">>>", "<<=", ">>=", "&&=", "||=", "??=", "=>", "==", "!=", "<=", ">=",
        "&&", "||", "??", "?.", "++", "--", "+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=", "**", "<<", ">>",
        "{", "}", "(", ")", "[", "]", ";", ",", "<", ">", "+", "-", "*", "/", "%", "&", "|", "^", "!", "~", "?", ":", "=", ".", "@", "#"];

    private static List<Tok> Lex(string code)
    {
        var list = new List<Tok>();
        int i = 0;
        bool nl = false;
        while (true)
        {
            while (i < code.Length)
            {
                char c = code[i];
                if (c == '\n') { nl = true; ++i; }
                else if (char.IsWhiteSpace(c) || c == '﻿') ++i;
                else if (c == '/' && i + 1 < code.Length && code[i + 1] == '/') while (i < code.Length && code[i] != '\n') ++i;
                else if (c == '/' && i + 1 < code.Length && code[i + 1] == '*')
                {
                    int end = code.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    if (end < 0) throw new SyntaxError("unterminated comment");
                    nl |= code.AsSpan(i, end - i).Contains('\n');
                    i = end + 2;
                }
                else break;
            }
            if (i >= code.Length) { list.Add(new('e', "", 0, true)); return list; }
            char ch = code[i];
            int start = i;
            if (char.IsAsciiDigit(ch) || ch == '.' && i + 1 < code.Length && char.IsAsciiDigit(code[i + 1]))
            {
                if (ch == '0' && i + 1 < code.Length && (code[i + 1] | 0x20) == 'x')
                {
                    i += 2;
                    while (i < code.Length && char.IsAsciiHexDigit(code[i])) ++i;
                    list.Add(new('n', "", Convert.ToInt64(code[(start + 2)..i], 16), nl));
                }
                else
                {
                    while (i < code.Length && (char.IsAsciiDigit(code[i]) || code[i] == '.')) ++i;
                    if (i < code.Length && (code[i] | 0x20) == 'e')
                    {
                        ++i;
                        if (i < code.Length && code[i] is '+' or '-') ++i;
                        while (i < code.Length && char.IsAsciiDigit(code[i])) ++i;
                    }
                    if (!double.TryParse(code[start..i], NumberStyles.Float, CultureInfo.InvariantCulture, out double number)) throw new SyntaxError("bad number");
                    list.Add(new('n', "", number, nl));
                }
            }
            else if (char.IsLetter(ch) || ch is '_' or '$')
            {
                while (i < code.Length && (char.IsLetterOrDigit(code[i]) || code[i] is '_' or '$')) ++i;
                list.Add(new('i', code[start..i], 0, nl));
            }
            else if (ch is '"' or '\'')
            {
                var text = new StringBuilder();
                ++i;
                while (true)
                {
                    if (i >= code.Length || code[i] == '\n') throw new SyntaxError("unterminated string");
                    char d = code[i++];
                    if (d == ch) break;
                    if (d == '\\') text.Append(Escape(code, ref i)); else text.Append(d);
                }
                list.Add(new('s', text.ToString(), 0, nl));
            }
            else if (ch == '`')
            {
                // 模板串原样收下（${} 按花括号配对），由语法分析再切
                int depth = 0;
                ++i;
                while (true)
                {
                    if (i >= code.Length) throw new SyntaxError("unterminated template");
                    char d = code[i];
                    if (d == '\\') { i += 2; continue; }
                    if (depth == 0 && d == '`') break;
                    if (d == '$' && i + 1 < code.Length && code[i + 1] == '{') { ++depth; i += 2; continue; }
                    if (depth > 0 && d == '{') ++depth;
                    if (depth > 0 && d == '}') --depth;
                    ++i;
                }
                list.Add(new('t', code[(start + 1)..i], 0, nl));
                ++i;
            }
            else
            {
                string p = Puncts.FirstOrDefault(p => string.CompareOrdinal(code, i, p, 0, p.Length) == 0) ?? throw new SyntaxError($"unexpected '{ch}'");
                i += p.Length;
                list.Add(new('p', p, 0, nl));
            }
            nl = false;
        }
    }

    private static string Escape(string code, ref int i)
    {
        if (i >= code.Length) throw new SyntaxError("bad escape");
        char e = code[i++];
        switch (e)
        {
            case 'n': return "\n";
            case 't': return "\t";
            case 'r': return "\r";
            case 'b': return "\b";
            case 'f': return "\f";
            case 'v': return "\v";
            case '0': return "\0";
            case '\n': return "";
            case 'x' when i + 2 <= code.Length: i += 2; return ((char)Convert.ToInt32(code[(i - 2)..i], 16)).ToString();
            case 'u' when i + 4 <= code.Length && code[i] != '{': i += 4; return ((char)Convert.ToInt32(code[(i - 4)..i], 16)).ToString();
            case 'u': throw new SyntaxError("unicode escape");
            default: return e.ToString();
        }
    }

    private sealed class Parser
    {
        private readonly List<Tok> t;
        private int p;
        public Parser(string code) => t = Lex(code);

        private bool Is(string s) => t[p].K is 'p' or 'i' && t[p].S == s;
        private bool Eat(string s) { if (!Is(s)) return false; ++p; return true; }
        private void Need(string s) { if (!Eat(s)) throw new SyntaxError($"expected '{s}' near '{t[p].S}'"); }
        private string Ident() => t[p].K == 'i' ? t[p++].S : throw new SyntaxError($"expected a name near '{t[p].S}'");
        private void Semi() => Eat(";");

        public X Program()
        {
            var list = new List<X>();
            while (t[p].K != 'e') list.Add(Stmt());
            return new("block", kids: [.. list]);
        }

        private X Block()
        {
            Need("{");
            var list = new List<X>();
            while (!Eat("}")) { if (t[p].K == 'e') throw new SyntaxError("unterminated block"); list.Add(Stmt()); }
            return new("block", kids: [.. list]);
        }

        private X Stmt()
        {
            if (Eat(";")) return new("empty");
            if (Is("{")) return Block();
            if (Eat("import"))
            {
                if (t[p].K == 's') { ++p; Semi(); return new("empty"); }
                var decls = new List<X>();
                string? whole = null;
                var named = new List<(string Name, string Local)>();
                if (Eat("*")) { Need("as"); whole = Ident(); }
                else if (Eat("{"))
                    while (!Eat("}")) { string name = Ident(); named.Add((name, Eat("as") ? Ident() : name)); Eat(","); }
                else whole = Ident();
                Need("from");
                string module = t[p].K == 's' ? t[p++].S : throw new SyntaxError("import source");
                Semi();
                var source = new X("module", module);
                if (whole is not null) decls.Add(new("decl", whole, 0, source));
                decls.AddRange(named.Select(n => new X("decl", n.Local, 0, new X("mem", n.Name, 0, source))));
                return new("var", kids: [.. decls]);
            }
            if (Eat("export"))
            {
                if (Eat("default")) { X e = Is("function") ? Stmt() : new("expr", kids: [Assign()]); Semi(); return e; }
                if (Eat("{")) { while (!Eat("}")) ++p; Semi(); return new("empty"); }
                return Stmt();
            }
            if (Is("var") || Is("let") || Is("const")) { ++p; X d = Decls(); Semi(); return d; }
            if (Eat("function"))
            {
                if (Is("*")) throw new SyntaxError("generator");
                string name = Ident();
                return Func(name, "fdecl");
            }
            if (Eat("return"))
            {
                X? v = Is(";") || Is("}") || t[p].Nl || t[p].K == 'e' ? null : Expr();
                Semi();
                return new("ret", kids: [v]);
            }
            if (Eat("if"))
            {
                Need("("); X c = Expr(); Need(")");
                X a = Stmt();
                X? b = Eat("else") ? Stmt() : null;
                return new("if", kids: [c, a, b]);
            }
            if (Eat("for"))
            {
                Need("(");
                bool declared = Is("var") || Is("let") || Is("const");
                if (declared) ++p;
                if (t[p].K == 'i' && t[p + 1].K == 'i' && t[p + 1].S is "of" or "in")
                {
                    string name = Ident();
                    bool isIn = t[p++].S == "in";
                    X it = Expr(); Need(")");
                    return new("forof", name, isIn ? 1 : 0, it, Stmt());
                }
                X? init = declared ? Decls() : Is(";") ? null : new("expr", kids: [Expr()]);
                Need(";");
                X? cond = Is(";") ? null : Expr();
                Need(";");
                X? step = Is(")") ? null : Expr();
                Need(")");
                return new("for", kids: [init, cond, step, Stmt()]);
            }
            if (Eat("while")) { Need("("); X c = Expr(); Need(")"); return new("while", kids: [c, Stmt()]); }
            if (Eat("do")) { X body = Stmt(); Need("while"); Need("("); X c = Expr(); Need(")"); Semi(); return new("do", kids: [body, c]); }
            if (Eat("break")) { if (t[p].K == 'i' && !t[p].Nl) throw new SyntaxError("label"); Semi(); return new("break"); }
            if (Eat("continue")) { if (t[p].K == 'i' && !t[p].Nl) throw new SyntaxError("label"); Semi(); return new("continue"); }
            if (Eat("switch"))
            {
                Need("("); X d = Expr(); Need(")"); Need("{");
                var cases = new List<X> { d };
                while (!Eat("}"))
                {
                    X? test = null;
                    if (Eat("case")) test = Expr(); else Need("default");
                    Need(":");
                    var body = new List<X?> { test };
                    while (!Is("case") && !Is("default") && !Is("}")) body.Add(Stmt());
                    cases.Add(new("case", kids: [.. body]));
                }
                return new("switch", kids: [.. cases]);
            }
            if (Eat("try"))
            {
                X a = Block();
                string? name = null;
                X? c = null, f = null;
                if (Eat("catch")) { if (Eat("(")) { name = Ident(); Need(")"); } c = Block(); }
                if (Eat("finally")) f = Block();
                return new("try", name, 0, a, c, f);
            }
            if (Eat("throw")) { X v = Expr(); Semi(); return new("throw", kids: [v]); }
            if (Is("class") || Is("async")) throw new SyntaxError(t[p].S);
            X e2 = Expr();
            Semi();
            return new("expr", kids: [e2]);
        }

        private X Decls()
        {
            var list = new List<X>();
            do
            {
                if (Is("{") || Is("[")) throw new SyntaxError("destructuring");
                string name = Ident();
                list.Add(new("decl", name, 0, Eat("=") ? Assign() : null));
            } while (Eat(","));
            return new("var", kids: [.. list]);
        }

        private X Func(string? name, string op = "fn")
        {
            Need("(");
            var ps = new List<X>();
            while (!Eat(")"))
            {
                if (Is("...") || Is("{") || Is("[")) throw new SyntaxError("parameter pattern");
                string n = Ident();
                ps.Add(new("param", n, 0, Eat("=") ? Assign() : null));
                if (!Is(")")) Need(",");
            }
            return new(op, name, 0, new X("params", kids: [.. ps]), Block());
        }

        private X Expr()
        {
            X e = Assign();
            if (!Is(",")) return e;
            var list = new List<X> { e };
            while (Eat(",")) list.Add(Assign());
            return new("seq", kids: [.. list]);
        }

        private static readonly HashSet<string> AssignOps = ["=", "+=", "-=", "*=", "/=", "%=", "**=", "<<=", ">>=", ">>>=", "&=", "|=", "^=", "&&=", "||=", "??="];

        private X Assign()
        {
            if (t[p].K == 'i' && t[p + 1].K == 'p' && t[p + 1].S == "=>") { string n = Ident(); ++p; return Arrow([new("param", n)]); }
            if (Is("(") && ArrowAhead())
            {
                ++p;
                var ps = new List<X>();
                while (!Eat(")"))
                {
                    if (Is("...") || Is("{") || Is("[")) throw new SyntaxError("parameter pattern");
                    string n = Ident();
                    ps.Add(new("param", n, 0, Eat("=") ? Assign() : null));
                    if (!Is(")")) Need(",");
                }
                Need("=>");
                return Arrow(ps);
            }
            if (Is("async") || Is("yield")) throw new SyntaxError(t[p].S);
            X left = Cond();
            if (t[p].K == 'p' && AssignOps.Contains(t[p].S))
            {
                string op = t[p++].S;
                if (left.Op is not ("id" or "mem" or "idx")) throw new SyntaxError("assignment target");
                return new("asg", op, 0, left, Assign());
            }
            return left;
        }

        private bool ArrowAhead()
        {
            int depth = 0;
            for (int q = p; t[q].K != 'e'; ++q)
            {
                if (t[q].K != 'p') continue;
                if (t[q].S is "(" or "[" or "{") ++depth;
                else if (t[q].S is ")" or "]" or "}" && --depth == 0) return t[q + 1].K == 'p' && t[q + 1].S == "=>";
            }
            return false;
        }

        private X Arrow(List<X> ps)
        {
            X body = Is("{") ? Block() : Assign();
            return new("fn", null, body.Op == "block" ? 0 : 1, new X("params", kids: [.. ps]), body);
        }

        private X Cond()
        {
            X c = Bin(0);
            if (!Eat("?")) return c;
            X a = Assign(); Need(":"); X b = Assign();
            return new("cond", kids: [c, a, b]);
        }

        private static readonly string[][] Levels = [["??"], ["||"], ["&&"], ["|"], ["^"], ["&"], ["==", "!=", "===", "!=="],
            ["<", ">", "<=", ">=", "instanceof", "in"], ["<<", ">>", ">>>"], ["+", "-"], ["*", "/", "%"], ["**"]];

        private X Bin(int level)
        {
            if (level == Levels.Length) return Unary();
            X left = Bin(level + 1);
            while ((t[p].K == 'p' || t[p].S is "in" or "instanceof") && Levels[level].Contains(t[p].S))
            {
                string op = t[p++].S;
                X right = level == Levels.Length - 1 ? Bin(level) : Bin(level + 1);
                left = new(op is "&&" or "||" or "??" ? "log" : "bin", op, 0, left, right);
            }
            return left;
        }

        private X Unary()
        {
            if (t[p].K == 'p' && t[p].S is "!" or "-" or "+" or "~" || t[p].K == 'i' && t[p].S is "typeof" or "void" or "delete")
            {
                string op = t[p++].S;
                return new("un", op, 0, Unary());
            }
            if (t[p].K == 'i' && t[p].S == "await") throw new SyntaxError("await");
            if (Is("++") || Is("--")) { string op = t[p++].S; return new("upd", op, 1, Unary()); }
            X e = Postfix();
            if ((Is("++") || Is("--")) && !t[p].Nl) { string op = t[p++].S; return new("upd", op, 0, e); }
            return e;
        }

        private string Member() => t[p].K == 'i' ? t[p++].S : throw new SyntaxError("member name");

        private X Postfix()
        {
            X e = Primary();
            while (true)
            {
                if (Eat(".")) e = new("mem", Member(), 0, e);
                else if (Eat("?."))
                {
                    if (Is("(") || Is("[")) throw new SyntaxError("optional call");
                    e = new("mem", Member(), 1, e);
                }
                else if (Eat("[")) { X k = Expr(); Need("]"); e = new("idx", kids: [e, k]); }
                else if (Is("(")) e = new("call", kids: [e, .. Args()]);
                else if (t[p].K == 't') throw new SyntaxError("tagged template");
                else return e;
            }
        }

        private X[] Args()
        {
            Need("(");
            var list = new List<X>();
            while (!Eat(")"))
            {
                if (Is("...")) throw new SyntaxError("spread");
                list.Add(Assign());
                if (!Is(")")) Need(",");
            }
            return [.. list];
        }

        private X Primary()
        {
            Tok k = t[p];
            if (k.K == 'n') { ++p; return new("num", null, k.N); }
            if (k.K == 's') { ++p; return new("str", k.S); }
            if (k.K == 't') { ++p; return Template(k.S); }
            if (k.K == 'i')
            {
                ++p;
                switch (k.S)
                {
                    case "function":
                        if (Is("*")) throw new SyntaxError("generator");
                        return Func(t[p].K == 'i' ? Ident() : null);
                    case "new":
                        {
                            X callee = Primary();
                            while (Eat(".")) callee = new("mem", Member(), 0, callee);
                            return new("new", kids: [callee, .. Is("(") ? Args() : []]);
                        }
                    case "true": return new("num", null, 1);
                    case "false": return new("num", null, 0);
                    case "null": return new("null");
                    case "this": return new("id", "thisLayer");
                    case "class" or "async" or "yield" or "await" or "super" or "import": throw new SyntaxError(k.S);
                    default: return new("id", k.S);
                }
            }
            if (Eat("(")) { X e = Expr(); Need(")"); return e; }
            if (Eat("["))
            {
                var list = new List<X>();
                while (!Eat("]"))
                {
                    if (Is("...")) throw new SyntaxError("spread");
                    if (Is(",")) { ++p; list.Add(new("id", "undefined")); continue; }
                    list.Add(Assign());
                    if (!Is("]")) Need(",");
                }
                return new("arr", kids: [.. list]);
            }
            if (Eat("{"))
            {
                var list = new List<X>();
                while (!Eat("}"))
                {
                    if (Is("...") || Is("[")) throw new SyntaxError("computed or spread property");
                    Tok key = t[p++];
                    if (key.K is not ('i' or 's' or 'n')) throw new SyntaxError("property key");
                    string name = key.K == 'n' ? key.N.ToString("R", CultureInfo.InvariantCulture) : key.S;
                    if (key.K == 'i' && name is "get" or "set" && t[p].K == 'i') throw new SyntaxError("accessor");
                    X value = Is("(") ? Func(name) : Eat(":") ? Assign() : new X("id", name);
                    list.Add(new("prop", name, 0, value));
                    if (!Is("}")) Need(",");
                }
                return new("obj", kids: [.. list]);
            }
            throw new SyntaxError(k.S == "/" ? "regular expression" : $"unexpected '{k.S}'");
        }

        private static X Template(string raw)
        {
            var parts = new List<X>();
            var text = new StringBuilder();
            for (int i = 0; i < raw.Length;)
            {
                if (raw[i] == '\\') { ++i; text.Append(Escape(raw, ref i)); continue; }
                if (raw[i] == '$' && i + 1 < raw.Length && raw[i + 1] == '{')
                {
                    int depth = 1, j = i + 2;
                    for (; j < raw.Length && depth > 0; ++j) depth += raw[j] == '{' ? 1 : raw[j] == '}' ? -1 : 0;
                    parts.Add(new("str", text.ToString()));
                    text.Clear();
                    var inner = new Parser(raw[(i + 2)..(j - 1)]);
                    parts.Add(inner.Expr());
                    if (inner.t[inner.p].K != 'e') throw new SyntaxError("template expression");
                    i = j;
                    continue;
                }
                text.Append(raw[i++]);
            }
            parts.Add(new("str", text.ToString()));
            return new("tpl", kids: [.. parts]);
        }
    }

    // ---------------- 抽象值 ----------------

    private abstract record V;
    /// <summary>
    /// 数值（布尔按 0/1）的形态。K：'c' 常数 C；'u' 值不知道的常数；'l' A·t+C（C 为 NaN = 截距不知道）；'f' floor(A·t+C)；'p' 周期集合 P；
    /// 'e' 外部输入（Why 是输入名）；'r' 随机；'o' 说不清（Why 是原因码）。St：值来自跨帧状态。
    /// </summary>
    private sealed record N(char K, double C = 0, double A = 0, double[]? P = null, string? Why = null, bool St = false) : V;
    private sealed record Str(string Text, bool St = false) : V;
    private sealed record Ob(Dictionary<string, V> F, string? Cls = null) : V;
    private sealed record Ar(V[] E) : V;
    private sealed record Fn(X Node, Scope Env) : V;
    private sealed record Bi(Func<V?, V[], V> Call) : V;
    /// <summary>图层句柄；Name 为 null 是本层，"?" 是名字求不出的层。</summary>
    private sealed record Ly(string? Name) : V;
    /// <summary>内建对象：engine、input、Math、thisScene、shared、WEMath 等模块，按名字分派成员。</summary>
    private sealed record Host(string Name) : V;
    /// <summary>createScriptProperties() 的构造器：收集默认值，finish() 时用烘焙值覆盖。</summary>
    private sealed record Builder(Dictionary<string, V> Defaults) : V;
    private sealed record Un : V { public static readonly Un I = new(); }
    private sealed record Nul : V { public static readonly Nul I = new(); }

    private static N Const(double c, bool st = false) => new('c', c, St: st);
    private static N Opaque(string why, bool st = false) => new('o', Why: why, St: st);
    private static N Lin(double a, double c, bool st) => a == 0 ? (double.IsNaN(c) ? new N('u', St: st) : Const(c, st)) : new('l', c, a, St: st);
    private static N Per(bool st, params double[]?[] sets)
    {
        var list = new List<double>();
        foreach (double x in sets.Where(s => s is not null).SelectMany(s => s!))
            if (!list.Any(y => Math.Abs(x - y) <= 1e-12 * Math.Max(x, y))) list.Add(x);
        list.Sort();
        return new('p', P: [.. list], St: st);
    }

    private static string Key(V v) => v switch
    {
        N n => n.K switch
        {
            'c' => "c" + n.C.ToString("R", CultureInfo.InvariantCulture),
            'l' or 'f' => n.K + n.A.ToString("R", CultureInfo.InvariantCulture) + "," + n.C.ToString("R", CultureInfo.InvariantCulture),
            'p' => "p" + string.Join(",", n.P!.Select(x => x.ToString("R", CultureInfo.InvariantCulture))),
            _ => n.K + (n.Why ?? ""),
        },
        Str s => "s" + s.Text.Length + ":" + s.Text,
        Ob o => "{" + string.Join(",", o.F.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => f.Key + ":" + Key(f.Value))) + "}",
        Ar a => "[" + string.Join(",", a.E.Select(Key)) + "]",
        Fn f => "fn" + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(f.Node),
        Ly l => "ly" + (l.Name ?? ""),
        Host h => "h" + h.Name,
        Un => "undefined",
        Nul => "null",
        _ => "?",
    };

    private static bool Tainted(V v) => v switch
    {
        N n => n.St, Str s => s.St, Ob o => o.F.Values.Any(Tainted), Ar a => a.E.Any(Tainted), _ => false
    };

    private static V Taint(V v, bool st) => !st ? v : v switch
    {
        N n => n with { St = true },
        Str s => s with { St = true },
        Ob o => new Ob(o.F.ToDictionary(f => f.Key, f => Taint(f.Value, true)), o.Cls),
        Ar a => new Ar([.. a.E.Select(e => Taint(e, true))]),
        _ => v,
    };

    private static V Clean(V v) => v switch
    {
        N n => n with { St = false },
        Str s => s with { St = false },
        Ob o => new Ob(o.F.ToDictionary(f => f.Key, f => Clean(f.Value)), o.Cls),
        Ar a => new Ar([.. a.E.Select(Clean)]),
        _ => v,
    };

    /// <summary>新值里相对旧值变了的部分打上状态标记（对象按字段比）。</summary>
    private static V TaintDiff(V now, V? before) => now is Ob o && before is Ob b
        ? new Ob(o.F.ToDictionary(f => f.Key, f => TaintDiff(f.Value, b.F.GetValueOrDefault(f.Key))), o.Cls)
        : before is not null && Key(now) == Key(before) ? now : Taint(now, true);

    private static V F32(V v) => v switch
    {
        N { K: 'c' } n => n with { C = (float)n.C },
        Ob o => new Ob(o.F.ToDictionary(f => f.Key, f => F32(f.Value)), o.Cls),
        _ => v,
    };

    private static double? Lcm(IReadOnlyList<double> periods)
    {
        double joint = periods[0];
        foreach (double period in periods.Skip(1))
        {
            int k = 1;
            for (; k <= 1000; ++k)
            {
                double ratio = k * joint / period;
                if (Math.Abs(ratio - Math.Round(ratio)) <= 1e-9 * ratio) break;
            }
            if (k > 1000) return null;
            joint *= k;
        }
        return joint;
    }

    // ---------------- 解释器 ----------------

    private sealed class Bail(string code, bool proof = false, string detail = "") : Exception(code)
    {
        public readonly string Code = code; public readonly bool Proof = proof; public readonly string Detail = detail;
    }

    private sealed class Scope(Scope? parent)
    {
        public readonly Dictionary<string, V> Vars = new(StringComparer.Ordinal);
        public readonly Scope? Parent = parent;
    }

    private enum Flow { Normal, Return, Break, Continue }

    /// <summary>跨帧状态：update 的 value、模块级变量、本层属性（按属性名）。</summary>
    private sealed record State(V Value, Dictionary<string, V> Module, Dictionary<string, V> Self)
    {
        public IEnumerable<V> Parts => Module.Values.Concat(Self.Values).Prepend(Value);
        public string Key() => ScriptTime.Key(Value) + "|" + string.Join(",", Module.OrderBy(m => m.Key, StringComparer.Ordinal).Select(m => m.Key + "=" + ScriptTime.Key(m.Value))) +
            "|" + string.Join(",", Self.OrderBy(m => m.Key, StringComparer.Ordinal).Select(m => m.Key + "=" + ScriptTime.Key(m.Value)));
    }

    private sealed record Snap(Dictionary<Scope, Dictionary<string, V>> Vars, Dictionary<string, V> Writes);

    private sealed class Interp
    {
        public readonly Scope Root = new(null);
        public readonly HashSet<X> Executed = [];
        public readonly List<string> Names = [];
        public bool Unresolved, StateCond;
        private readonly X program;
        private readonly Binding binding;
        private readonly double frametime;
        private readonly bool collect;
        private V value = Un.I;
        private Dictionary<string, V> self = new(StringComparer.Ordinal);
        private Dictionary<string, V> writes = new(StringComparer.Ordinal);
        private char time = 'z';
        private double settle;
        private bool indirectRuntime;
        private V ret = Un.I;
        private (V Ret, Snap State, N Cond)? pending;
        private N? rate;
        private int depth;
        private long steps;

        public Interp(X program, Binding binding, double frametime, bool collect)
        {
            this.program = program; this.binding = binding; this.frametime = frametime; this.collect = collect;
            foreach (string name in (string[])["engine", "input", "Math", "console", "thisScene", "shared", "localStorage", "Date", "JSON", "Object", "Array",
                "Vec2", "Vec3", "Vec4", "createScriptProperties", "Number", "parseFloat", "parseInt", "String", "Boolean", "isNaN", "isFinite"])
                Root.Vars[name] = new Host(name);
            Root.Vars["thisLayer"] = Root.Vars["thisObject"] = new Ly(null);
            Root.Vars["undefined"] = Un.I;
            Root.Vars["NaN"] = Const(double.NaN);
            Root.Vars["Infinity"] = Const(double.PositiveInfinity);
        }

        /// <summary>模块顶层与 init 在 t=0 各跑一次；init 的返回值是属性的初值。</summary>
        public void Setup()
        {
            time = 'z';
            value = Parse(binding.Node["value"]);
            Exec(program, Root);
            if (Root.Vars.GetValueOrDefault("init") is Fn init && Call(init, null, [value]) is var first && first is not Un) value = first;
            CommitSelf();
            writes.Clear();
        }

        public State Capture() => new(value, Root.Vars.Where(v => v.Value is not (Fn or Host or Bi or Ly)).ToDictionary(v => v.Key, v => v.Value), new(self));

        public void Load(State state, State? before)
        {
            value = before is null ? state.Value : TaintDiff(Clean(state.Value), before.Value);
            foreach (var (key, v) in state.Module) Root.Vars[key] = before is null ? v : TaintDiff(Clean(v), before.Module.GetValueOrDefault(key));
            self = state.Self.ToDictionary(s => s.Key, s => before is null ? s.Value : TaintDiff(Clean(s.Value), before.Self.GetValueOrDefault(s.Key)));
            StateCond = false;
        }

        /// <summary>跑一帧 update：返回 (属性名, 值)——绑定属性的新值（没返回就不变，不算输出）与写到图层上的属性。</summary>
        public List<(string Property, V Value)> Frame(Fn update, char clock)
        {
            time = clock;
            writes.Clear();
            V result = Call(update, null, [value]);
            var outputs = writes.Select(w => (w.Key, w.Value)).ToList();
            if (result is not Un) { outputs.Add(("|" + binding.Name, result)); value = clock == 'n' ? F32(result) : result; }
            CommitSelf();
            return outputs;
        }

        private void CommitSelf()
        {
            foreach (var (key, v) in writes.Where(w => w.Key.StartsWith('|')))
                self[key[1..]] = time == 'n' ? F32(v) : v;
        }

        public Verdict Classify(List<(string Property, V Value)> outputs, double warmup)
        {
            AddSettle(warmup);
            var forms = new List<(string Property, N Form)>();
            void Flatten(string property, V v)
            {
                switch (v)
                {
                    case N n: forms.Add((property, n)); break;
                    case Ob o: foreach (V f in o.F.Values) Flatten(property, f); break;
                    case Ar a: foreach (V e in a.E) Flatten(property, e); break;
                }
            }
            foreach (var (property, v) in outputs) Flatten(property[(property.IndexOf('|') + 1)..], v);
            string[] inputs = [.. forms.Where(f => f.Form.K == 'e').Select(f => f.Form.Why!).Distinct()];
            if (inputs.Length > 0)
                return new(Outcome.Cannot, Code: "script_reads_external_input", Detail: "the script output depends on live input " + string.Join(", ", inputs));
            var periods = new List<double>();
            foreach (var (property, form) in forms.Where(f => f.Form.K is 'l' or 'f'))
            {
                // 角度按度：旋转 360° 回到原样；位置、缩放线性增长永不回头；其他属性（alpha、颜色等）渲染时可能被钳位，说不清
                if (property == "angles") periods.Add(360 / Math.Abs(form.A));
                else if (property is "origin" or "scale")
                    return new(Outcome.Cannot, Code: "drift", Detail: $"{property} changes linearly with time at {form.A.ToString("R", CultureInfo.InvariantCulture)} per second");
                else return new(Outcome.Unconverged, Code: "linear_time_output", Detail: $"{property} changes linearly with time");
            }
            if (forms.FirstOrDefault(f => f.Form.K is 'o' or 'r') is { Form: not null } bad)
                return new(Outcome.Unconverged, Code: bad.Form.K == 'r' ? "script_reads_random" : bad.Form.Why!, Detail: $"output {bad.Property}");
            periods.AddRange(forms.Where(f => f.Form.K == 'p').SelectMany(f => f.Form.P!));
            if (periods.Count == 0) return new(Outcome.Static, settle);
            double? joint = Lcm([.. periods.Distinct().OrderDescending()]);
            if (joint is null)
                return new(Outcome.Unconverged, settle, Code: "script_incommensurate_periods",
                    Detail: "periods " + string.Join(", ", periods.Distinct().Select(x => x.ToString("0.####", CultureInfo.InvariantCulture))) + " s have no common multiple");
            return new(Outcome.Periodic, settle, joint, Retimable: !indirectRuntime && binding.Pointer is not null);
        }

        /// <summary>跨帧状态：逐帧精确模拟（写回属性按单精度），状态回到出现过的值即闭合。</summary>
        public Verdict Simulate(Fn update, State start, ulong frameCap)
        {
            Load(start, null);
            var seen = new Dictionary<string, ulong>(StringComparer.Ordinal);
            string Hash(State s)
            {
                string key = s.Key();
                if (s.Parts.Any(Unknown)) throw new Bail("script_state_unknown", detail: "cross-frame state holds a value the analysis cannot know");
                return key.Length <= 256 ? key : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(key)));
            }
            seen[Hash(start)] = 0;
            for (ulong frame = 1; frame <= frameCap; ++frame)
            {
                foreach (var (property, v) in Frame(update, 'n'))
                    if (Unsettled(v) is N n)
                        throw n.K == 'e' ? new Bail("script_reads_external_input", true, "the script output depends on live input " + n.Why)
                            : new Bail(n.Why ?? "script_state_unknown", detail: $"output {property[(property.IndexOf('|') + 1)..]}");
                string key = Hash(Capture());
                if (seen.TryGetValue(key, out ulong first))
                {
                    ulong period = frame - first;
                    double warm = first == 0 ? 0 : (first + 1) * frametime;
                    return period == 1 ? new(Outcome.Static, warm) : new(Outcome.Periodic, warm, PeriodFrames: period);
                }
                seen[key] = frame;
            }
            throw new Bail("script_state_not_closed", detail: $"cross-frame state does not repeat within {frameCap} frames");
        }

        private static bool Unknown(V v) => v switch { N n => n.K != 'c', Ob o => o.F.Values.Any(Unknown), Ar a => a.E.Any(Unknown), _ => false };
        private static N? Unsettled(V v) => v switch
        {
            N n => n.K is 'c' or 'u' ? null : n,
            Ob o => o.F.Values.Select(Unsettled).FirstOrDefault(x => x is not null),
            Ar a => a.E.Select(Unsettled).FirstOrDefault(x => x is not null),
            _ => null
        };

        // ---- 值与场景 ----

        private static V Parse(JsonNode? node)
        {
            if (node is JsonObject { } bound && bound.ContainsKey("value")) node = bound["value"];
            if (node is not JsonValue v) return node is null ? Un.I : new N('u');
            if (v.TryGetValue(out bool flag)) return Const(flag ? 1 : 0);
            if (v.TryGetValue(out double number)) return Const(number);
            if (!v.TryGetValue(out string? text)) return new N('u');
            string[] parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var numbers = parts.Select(x => double.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : (double?)null).ToArray();
            if (parts.Length is >= 2 and <= 4 && numbers.All(x => x is not null))
                return Vec(parts.Length, [.. numbers.Select(x => (V)Const(x!.Value))]);
            return new Str(text);
        }

        private static Ob Vec(int size, V[] parts)
        {
            var fields = new Dictionary<string, V>(StringComparer.Ordinal);
            for (int i = 0; i < size; ++i) fields["xyzw"[i].ToString()] = i < parts.Length ? parts[i] : Const(0);
            return new(fields, "Vec" + size);
        }

        private static N Num(V v) => v switch
        {
            N n => n,
            Str s => double.TryParse(s.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? Const(d, s.St) : Const(s.Text.Trim().Length == 0 ? 0 : double.NaN, s.St),
            Un => Const(double.NaN),
            Nul => Const(0),
            _ => Opaque("object_to_number", Tainted(v)),
        };

        private static string? Text(V v) => v switch
        {
            Str s => s.Text,
            N { K: 'c' } n => double.IsInteger(n.C) && Math.Abs(n.C) < 1e21 ? n.C.ToString("0", CultureInfo.InvariantCulture) : n.C.ToString("R", CultureInfo.InvariantCulture),
            Un => "undefined",
            Nul => "null",
            _ => null,
        };

        private N Truth(V v)
        {
            switch (v)
            {
                case N { K: 'c' } n: return Const(n.C != 0 && !double.IsNaN(n.C) ? 1 : 0, n.St);
                case N { K: 'l' or 'f' } n:
                    if (double.IsNaN(n.C)) return Opaque("threshold_unknown", n.St);
                    AddSettle((-n.C + (n.K == 'f' ? Math.Sign(n.A) : 0)) / n.A);
                    return Const(1, n.St);
                case N n: return n;
                case Str s: return Const(s.Text.Length > 0 ? 1 : 0, s.St);
                case Un or Nul: return Const(0);
                default: return Const(1);
            }
        }

        private void AddSettle(double at) { if (at > settle) settle = at; }

        /// <summary>分支判定。收集图层名时回调会在不同状态下反复触发，if/switch/?: 两边都走（循环仍按实际条件）。</summary>
        private bool? Branch(N cond) => collect ? null : Decide(cond);

        private bool? Decide(N cond)
        {
            if (cond.St) StateCond = true;
            return cond.K == 'c' ? cond.C != 0 : null;
        }

        // ---- 形态上的运算 ----

        private static N? Worst(bool st, params N[] ns)
        {
            if (ns.Any(n => n.K == 'e')) return new('e', Why: string.Join(",", ns.Where(n => n.K == 'e').SelectMany(n => n.Why!.Split(',')).Distinct()), St: st);
            if (ns.Any(n => n.K == 'r')) return new('r', St: st);
            return ns.FirstOrDefault(n => n.K == 'o') is { } o ? o with { St = st } : null;
        }

        private static double Apply(string op, double a, double b) => op switch
        {
            "+" => a + b, "-" => a - b, "*" => a * b, "/" => a / b, "%" => a % b,
            "**" => Math.Pow(a, b),
            "&" => (int)(long)a & (int)(long)b, "|" => (int)(long)a | (int)(long)b, "^" => (int)(long)a ^ (int)(long)b,
            "<<" => (int)(long)a << ((int)(long)b & 31), ">>" => (int)(long)a >> ((int)(long)b & 31), ">>>" => (uint)(long)a >> ((int)(long)b & 31),
            _ => double.NaN,
        };

        private N Arith(string op, N a, N b)
        {
            bool st = a.St || b.St;
            if (Worst(st, a, b) is N worst) return worst;
            if (a.K == 'c' && b.K == 'c') return Const(Apply(op, a.C, b.C), st);
            if (a.K is 'c' or 'u' && b.K is 'c' or 'u') return new('u', St: st);
            bool lin = a.K is 'l' or 'f' || b.K is 'l' or 'f';
            if (!lin) return Per(st, a.P, b.P);     // 周期与常数之间的任何确定运算仍是周期
            if (a.K == 'p' || b.K == 'p') return Opaque("periodic_with_linear_time", st);
            switch (op)
            {
                case "+" or "-" when a.K != 'f' && b.K != 'f':
                    {
                        double sign = op == "+" ? 1 : -1;
                        double c = (a.K == 'u' ? double.NaN : a.C) + sign * (b.K == 'u' ? double.NaN : b.C);
                        return Lin((a.K == 'l' ? a.A : 0) + sign * (b.K == 'l' ? b.A : 0), c, st);
                    }
                case "-" when a.K == 'l' && b.K == 'f' && a.A == b.A && a.C.Equals(b.C):
                    return Per(st, [1 / Math.Abs(a.A)]);   // x − floor(x)
                case "*" when a.K == 'l' && b.K == 'c' || a.K == 'c' && b.K == 'l':
                    {
                        (N l, double k) = a.K == 'l' ? (a, b.C) : (b, a.C);
                        return Lin(l.A * k, l.C * k, st);
                    }
                case "/" when a.K == 'l' && b.K == 'c' && b.C != 0:
                    return Lin(a.A / b.C, a.C / b.C, st);
                case "%" when a.K is 'l' or 'f' && b.K == 'c' && b.C != 0 && (a.K == 'l' || double.IsInteger(b.C)):
                    return Per(st, [Math.Abs(b.C / a.A)]);
            }
            return Opaque(a.K == 'l' && b.K == 'l' && op == "*" ? "quadratic_time" : a.K == 'u' || b.K == 'u' ? "unknown_coefficient" : "nonlinear_time", st);
        }

        private N Compare(string op, V left, V right)
        {
            if (left is Str ls && right is Str rs)
            {
                int c = string.CompareOrdinal(ls.Text, rs.Text);
                return Const(op switch { "<" => c < 0, ">" => c > 0, "<=" => c <= 0, _ => c >= 0 } ? 1 : 0, ls.St || rs.St);
            }
            N a = Num(left), b = Num(right);
            if (a.K == 'c' && b.K == 'c')
                return Const(op switch { "<" => a.C < b.C, ">" => a.C > b.C, "<=" => a.C <= b.C, _ => a.C >= b.C } ? 1 : 0, a.St || b.St);
            N d = Arith("-", a, b);
            if (d.K is 'l' or 'f')
            {
                // 过交点后符号固定：阈值是有限常数，交点就是 settle 的上界
                if (double.IsNaN(d.C)) return Opaque("threshold_unknown", d.St);
                AddSettle((-d.C + (d.K == 'f' ? Math.Sign(d.A) : 0)) / d.A);
                return Const((op is ">" or ">=") == d.A > 0 ? 1 : 0, d.St);
            }
            return d;
        }

        private N Equal(V left, V right, bool strict)
        {
            bool st = Tainted(left) || Tainted(right);
            if (left is Un or Nul || right is Un or Nul)
                return Const(left is Un or Nul && right is Un or Nul && (!strict || left.GetType() == right.GetType()) ? 1 : 0, st);
            if (left is Str a && right is Str b) return Const(a.Text == b.Text ? 1 : 0, st);
            if (strict && (left is Str) != (right is Str)) return right is N { K: 'c' or 'u' or 'l' or 'f' or 'p' } || left is N { K: 'c' or 'u' or 'l' or 'f' or 'p' } ? Const(0, st) : Opaque("mixed_equality", st);
            if (left is not (N or Str) || right is not (N or Str)) return Const(Key(left) == Key(right) ? 1 : 0, st);
            N x = Num(left), y = Num(right);
            if (x.K == 'c' && y.K == 'c') return Const(x.C == y.C ? 1 : 0, st);
            N d = Arith("-", x, y);
            if (d.K == 'l')
            {
                if (double.IsNaN(d.C)) return Opaque("threshold_unknown", st);
                AddSettle(-d.C / d.A);
                return Const(0, st);
            }
            return d.K == 'f' ? Opaque("stair_equality", st) : d;
        }

        private N Not(N n) => n.K == 'c' ? Const(n.C != 0 && !double.IsNaN(n.C) ? 0 : 1, n.St) : n;

        /// <summary>按非常数条件合并两边的值：两边相同照旧；否则常数/周期按条件的周期合并，其余降为说不清。</summary>
        private V Join(V a, V b, N cond)
        {
            if (Key(a) == Key(b)) return Taint(a, cond.St);
            if (a is Ob oa && b is Ob ob && oa.F.Keys.Order().SequenceEqual(ob.F.Keys.Order()))
                return new Ob(oa.F.ToDictionary(f => f.Key, f => Join(f.Value, ob.F[f.Key], cond)), oa.Cls);
            if (a is Ar aa && b is Ar ab && aa.E.Length == ab.E.Length)
                return new Ar([.. aa.E.Zip(ab.E, (x, y) => Join(x, y, cond))]);
            N? Form(V v) => v switch { N n => n, Str s => new N('u', St: s.St), Un or Nul => new N('u'), _ => null };
            if (Form(a) is not N x || Form(b) is not N y) return Opaque("join_mismatch", cond.St);
            bool st = x.St || y.St || cond.St;
            if (Worst(st, x, y, cond) is N worst) return worst;
            if (x.K is 'l' or 'f' || y.K is 'l' or 'f') return Opaque("branch_mixes_linear_time", st);
            return cond.K == 'u' && x.K is 'c' or 'u' && y.K is 'c' or 'u' ? new N('u', St: st) : Per(st, x.P, y.P, cond.P);
        }

        // ---- 状态快照（按非常数条件分两边跑） ----

        private Snap Save(Scope s)
        {
            var vars = new Dictionary<Scope, Dictionary<string, V>>();
            for (Scope? c = s; c is not null; c = c.Parent) vars[c] = new(c.Vars, StringComparer.Ordinal);
            return new(vars, new(writes, StringComparer.Ordinal));
        }

        private void Restore(Snap snap)
        {
            foreach (var (scope, vars) in snap.Vars) { scope.Vars.Clear(); foreach (var (k, v) in vars) scope.Vars[k] = v; }
            writes = new(snap.Writes, StringComparer.Ordinal);
        }

        private Snap Merge(Snap a, Snap b, N cond)
        {
            var vars = new Dictionary<Scope, Dictionary<string, V>>();
            foreach (var (scope, x) in a.Vars)
                if (b.Vars.TryGetValue(scope, out var y))
                    vars[scope] = x.Keys.Union(y.Keys).ToDictionary(k => k, k => Join(x.GetValueOrDefault(k) ?? Un.I, y.GetValueOrDefault(k) ?? Un.I, cond), StringComparer.Ordinal);
            var w = a.Writes.Keys.Union(b.Writes.Keys).ToDictionary(k => k, k => a.Writes.TryGetValue(k, out V? x) && b.Writes.TryGetValue(k, out V? y)
                ? Join(x, y, cond) : Opaque("conditional_write", cond.St), StringComparer.Ordinal);
            return new(vars, w);
        }

        private V Fork(N cond, Func<V> a, Func<V> b, Scope s)
        {
            Snap snap = Save(s);
            V va = a(); Snap sa = Save(s);
            Restore(snap);
            V vb = b(); Snap sb = Save(s);
            Restore(Merge(sa, sb, cond));
            return Join(va, vb, cond);
        }

        // ---- 语句 ----

        private void Tick() { if (++steps > 50_000_000) throw new Bail("script_step_budget"); }

        private Flow Exec(X x, Scope s)
        {
            Tick();
            switch (x.Op)
            {
                case "block":
                    {
                        Scope inner = ReferenceEquals(x, program) ? s : new Scope(s);
                        foreach (X f in x.K.OfType<X>().Where(k => k.Op == "fdecl")) inner.Vars[f.Name!] = new Fn(f, inner);
                        foreach (X st in x.K.OfType<X>())
                        {
                            if (st.Op == "fdecl") continue;
                            Flow flow = Exec(st, inner);
                            if (flow != Flow.Normal) return flow;
                        }
                        return Flow.Normal;
                    }
                case "empty" or "fdecl": return Flow.Normal;
                case "var":
                    foreach (X d in x.K.OfType<X>()) s.Vars[d.Name!] = d.K[0] is X init ? Ev(init, s) : Un.I;
                    return Flow.Normal;
                case "expr": Ev(x.K[0]!, s); return Flow.Normal;
                case "ret": ret = x.K[0] is X r ? Ev(r, s) : Un.I; return Flow.Return;
                case "break": return Flow.Break;
                case "continue": return Flow.Continue;
                case "throw": throw new Bail("script_throws");
                case "if": return If(Truth(Ev(x.K[0]!, s)), x.K[1]!, x.K[2], s);
                case "try":
                    {
                        Flow flow = Exec(x.K[0]!, s);
                        if (x.K[2] is X fin && Exec(fin, s) is var f2 && f2 != Flow.Normal) return f2;
                        return flow;
                    }
                case "switch": return Switch(x, s);
                case "for" or "while" or "do": return Loop(x, s);
                case "forof": return ForOf(x, s);
            }
            throw new Bail("script_construct_unsupported", detail: x.Op);
        }

        private Flow If(N cond, X a, X? b, Scope s)
        {
            if (Branch(cond) is bool known) return known ? Exec(a, s) : b is null ? Flow.Normal : Exec(b, s);
            if (collect)
            {
                // 收集图层名：两边都跑（两边的 getLayer 都记下），之后的状态按两边合并；
                // 顺序跑会让后一边的赋值盖掉另一边，得出确切但错的名字（if (r > 21) r = 6 之后只剩下标 6）
                Snap before = Save(s);
                Exec(a, s); Snap thenState = Save(s);
                Restore(before);
                if (b is not null) Exec(b, s);
                Restore(Merge(thenState, Save(s), cond));
                return Flow.Normal;
            }
            Snap snap = Save(s);
            Flow fa = Exec(a, s); V ra = ret; Snap sa = Save(s);
            Restore(snap);
            Flow fb = b is null ? Flow.Normal : Exec(b, s); V rb = ret; Snap sb = Save(s);
            if (fa is Flow.Break or Flow.Continue || fb is Flow.Break or Flow.Continue) throw new Bail("loop_exit_on_time");
            if (fa == fb)
            {
                Restore(Merge(sa, sb, cond));
                if (fa == Flow.Return) ret = Join(ra, rb, cond);
                return fa;
            }
            // 一边返回、一边继续：返回的那边记下，函数结束时与最终结果合并
            var (retSnap, retValue, goOn) = fa == Flow.Return ? (sa, ra, sb) : (sb, rb, sa);
            pending = pending is { } old ? (Join(old.Ret, retValue, cond), Merge(old.State, retSnap, cond), Arith("+", cond, old.Cond))
                : (retValue, retSnap, cond);
            Restore(goOn);
            return Flow.Normal;
        }

        private Flow Switch(X x, Scope s)
        {
            V d = Ev(x.K[0]!, s);
            X[] cases = [.. x.K.Skip(1).OfType<X>()];
            int start = -1;
            for (int i = 0; i < cases.Length && start < 0; ++i)
                if (cases[i].K[0] is X test)
                {
                    N eq = Equal(d, Ev(test, s), strict: true);
                    if (Branch(eq) is bool known) { if (known) start = i; }
                    else if (collect)
                    {
                        // 同 If：每个 case 都从进 switch 时的状态跑，结果与"一个都没进"合并
                        Snap before = Save(s), merged = before;
                        foreach (X c in cases)
                        {
                            Restore(before);
                            foreach (X st in c.K.Skip(1).OfType<X>())
                                if (Exec(st, new Scope(s)) != Flow.Normal) break;
                            merged = Merge(merged, Save(s), eq);
                        }
                        Restore(merged);
                        return Flow.Normal;
                    }
                    else throw new Bail("switch_on_time");
                }
            if (start < 0) start = Array.FindIndex(cases, c => c.K[0] is null);
            if (start < 0) return Flow.Normal;
            var inner = new Scope(s);
            for (int i = start; i < cases.Length; ++i)
                foreach (X st in cases[i].K.Skip(1).OfType<X>())
                {
                    Flow flow = Exec(st, inner);
                    if (flow == Flow.Break) return Flow.Normal;
                    if (flow != Flow.Normal) return flow;
                }
            return Flow.Normal;
        }

        private Flow Loop(X x, Scope s)
        {
            var scope = new Scope(s);
            X? cond = x.Op switch { "for" => x.K[1], "while" => x.K[0], _ => x.K[1] };
            X body = x.Op switch { "for" => x.K[3]!, "while" => x.K[1]!, _ => x.K[0]! };
            if (x.Op == "for" && x.K[0] is X init) Exec(init, scope);
            for (int n = 0; ; ++n)
            {
                if (n > 100_000) throw new Bail("loop_bound");
                if (cond is not null && (x.Op != "do" || n > 0))
                {
                    N c = Truth(Ev(cond, scope));
                    bool? go = Decide(c);
                    // 收集图层名时次数定不下的循环：只跑一遍会漏掉后面各轮取的名字，退回旧规则
                    if (go is null && collect) { Unresolved = true; return Flow.Normal; }
                    if (go is null) throw new Bail("loop_on_time");
                    if (!go.Value) return Flow.Normal;
                }
                Flow flow = Exec(body, scope);
                if (flow == Flow.Return) return flow;
                if (flow == Flow.Break) return Flow.Normal;
                if (x.Op == "for" && x.K[2] is X step) Ev(step, scope);
            }
        }

        private Flow ForOf(X x, Scope s)
        {
            V it = Ev(x.K[0]!, s);
            V[] items = (x.Num == 1, it) switch
            {
                (true, Ob o) => [.. o.F.Keys.Select(k => (V)new Str(k))],
                (true, Ar a) => [.. Enumerable.Range(0, a.E.Length).Select(i => (V)new Str(i.ToString(CultureInfo.InvariantCulture)))],
                (false, Ar a) => a.E,
                (false, Str t) => [.. t.Text.Select(c => (V)new Str(c.ToString()))],
                _ => collect ? [new N('o', Why: "loop_item")] : throw new Bail("loop_on_unknown"),
            };
            foreach (V item in items)
            {
                var scope = new Scope(s);
                scope.Vars[x.Name!] = item;
                Flow flow = Exec(x.K[1]!, scope);
                if (flow == Flow.Return) return flow;
                if (flow == Flow.Break) break;
            }
            return Flow.Normal;
        }

        // ---- 调用 ----

        public V Call(V callee, V? self, V[] args)
        {
            switch (callee)
            {
                case Fn f:
                    {
                        if (++depth > 64) throw new Bail("recursion_depth");
                        Executed.Add(f.Node);
                        var scope = new Scope(f.Env);
                        X[] ps = [.. f.Node.K[0]!.K.OfType<X>()];
                        for (int i = 0; i < ps.Length; ++i)
                            scope.Vars[ps[i].Name!] = i < args.Length && args[i] is not Un ? args[i] : ps[i].K[0] is X d ? Ev(d, scope) : Un.I;
                        var saved = pending;
                        pending = null;
                        V result;
                        if (f.Node.Num == 1) result = Ev(f.Node.K[1]!, scope);
                        else
                        {
                            Flow flow = Exec(f.Node.K[1]!, scope);
                            result = flow == Flow.Return ? ret : Un.I;
                            if (pending is { } p)
                            {
                                Restore(Merge(p.State, Save(scope), p.Cond));
                                result = Join(p.Ret, result, p.Cond);
                            }
                        }
                        pending = saved;
                        --depth;
                        return result;
                    }
                case Bi b: return b.Call(self, args);
                case Host h: return CallHost(h, args);
                case N n: return args.Aggregate(n, (acc, a) => Arith("+", acc with { K = acc.K == 'c' ? 'u' : acc.K }, Num(a) is var m && m.K == 'c' ? new N('u', St: m.St) : m));
            }
            throw new Bail("script_runtime_error", detail: "call of a non-function");
        }

        // ---- 表达式 ----

        private V Lookup(string name, Scope s)
        {
            for (Scope? c = s; c is not null; c = c.Parent)
                if (c.Vars.TryGetValue(name, out V? v)) return v;
            return Opaque("unknown_name");
        }

        private void Store(string name, V v, Scope s)
        {
            for (Scope? c = s; c is not null; c = c.Parent)
                if (c.Vars.ContainsKey(name)) { c.Vars[name] = v; return; }
            Root.Vars[name] = v;
        }

        private V Ev(X x, Scope s)
        {
            Tick();
            switch (x.Op)
            {
                case "num": return Const(x.Num);
                case "str": return new Str(x.Name!);
                case "null": return Nul.I;
                case "id": return Lookup(x.Name!, s);
                case "module": return new Host("module:" + x.Name);
                case "tpl": return x.K.OfType<X>().Select(k => Ev(k, s)).Aggregate((V)new Str(""), Concat);
                case "arr": return new Ar([.. x.K.OfType<X>().Select(k => Ev(k, s))]);
                case "obj": return new Ob(x.K.OfType<X>().ToDictionary(k => k.Name!, k => Ev(k.K[0]!, s), StringComparer.Ordinal));
                case "fn": return new Fn(x, s);
                case "seq": { V last = Un.I; foreach (X k in x.K.OfType<X>()) last = Ev(k, s); return last; }
                case "mem":
                    {
                        V obj = Ev(x.K[0]!, s);
                        if (x.Num == 1 && obj is Un or Nul) return Un.I;
                        if (obj is Host { Name: "engine" } && x.Name == "runtime" && x.K[0] is not { Op: "id", Name: "engine" }) indirectRuntime = true;
                        return Get(obj, x.Name!);
                    }
                case "idx":
                    {
                        V obj = Ev(x.K[0]!, s), key = Ev(x.K[1]!, s);
                        if (obj is Ar a && Num(key) is var k && k.K != 'c' && k.K is 'p' or 'u')
                            return a.E.Length == 0 ? Un.I : a.E.Skip(1).Aggregate(a.E[0], (acc, e) => Join(acc, e, k));
                        if (Text(key) is not string name) return Opaque("dynamic_index", Tainted(key));
                        if (obj is Host { Name: "engine" } && name == "runtime") indirectRuntime = true;
                        return Get(obj, name);
                    }
                case "call":
                    {
                        X callee = x.K[0]!;
                        V[] args = [.. x.K.Skip(1).OfType<X>().Select(k => Ev(k, s))];
                        if (callee.Op is "mem" or "idx")
                        {
                            V self = Ev(callee.K[0]!, s);
                            string? name = callee.Op == "mem" ? callee.Name : Text(Ev(callee.K[1]!, s));
                            if (name is null) return Opaque("dynamic_method");
                            if (self is Ar arr && name is "push" or "pop" or "shift" or "unshift" or "splice" or "reverse" or "sort" or "fill")
                                return Mutate(arr, name, args, callee.K[0]!, s);
                            if (callee.Num == 1 && self is Un or Nul) return Un.I;
                            return Call(Get(self, name), self, args);
                        }
                        return Call(Ev(callee, s), null, args);
                    }
                case "new":
                    {
                        V[] args = [.. x.K.Skip(1).OfType<X>().Select(k => Ev(k, s))];
                        return Ev(x.K[0]!, s) switch
                        {
                            Host { Name: "Vec2" or "Vec3" or "Vec4" } h => MakeVec(h.Name[3] - '0', args),
                            Host { Name: "Date" } => External("wall_clock"),
                            Host { Name: "Array" } => new Ar([]),
                            Host { Name: "Object" } => new Ob(new(StringComparer.Ordinal)),
                            _ => throw new Bail("script_constructor"),
                        };
                    }
                case "un":
                    {
                        if (x.Name == "typeof") return new Str(Ev(x.K[0]!, s) switch { N => "number", Str => "string", Fn or Bi => "function", Un => "undefined", _ => "object" });
                        V v = Ev(x.K[0]!, s);
                        return x.Name switch
                        {
                            "!" => Not(Truth(v)),
                            "-" => Arith("-", Const(0), Num(v)),
                            "+" => Num(v),
                            "~" => Arith("^", Num(v), Const(-1)),
                            "void" => Un.I,
                            _ => Const(1),
                        };
                    }
                case "bin": return Binary(x.Name!, Ev(x.K[0]!, s), Ev(x.K[1]!, s));
                case "log":
                    {
                        V a = Ev(x.K[0]!, s);
                        if (x.Name == "??") return a is Un or Nul ? Ev(x.K[1]!, s) : a;
                        N t = Truth(a);
                        bool? d = Branch(t);
                        if (d is bool k) return k == (x.Name == "&&") ? Ev(x.K[1]!, s) : a;
                        return x.Name == "&&" ? Fork(t, () => Ev(x.K[1]!, s), () => a, s) : Fork(t, () => a, () => Ev(x.K[1]!, s), s);
                    }
                case "cond":
                    {
                        N t = Truth(Ev(x.K[0]!, s));
                        bool? d = Branch(t);
                        if (d is bool k) return Ev(x.K[k ? 1 : 2]!, s);
                        return Fork(t, () => Ev(x.K[1]!, s), () => Ev(x.K[2]!, s), s);
                    }
                case "asg":
                    {
                        V v;
                        if (x.Name == "=") v = Ev(x.K[1]!, s);
                        else if (x.Name is "&&=" or "||=" or "??=") throw new Bail("script_construct_unsupported", detail: x.Name);
                        else v = Binary(x.Name![..^1], Ev(x.K[0]!, s), Ev(x.K[1]!, s));
                        Assign(x.K[0]!, v, s);
                        return v;
                    }
                case "upd":
                    {
                        N old = Num(Ev(x.K[0]!, s));
                        N now = Arith(x.Name == "++" ? "+" : "-", old, Const(1));
                        Assign(x.K[0]!, now, s);
                        return x.Num == 1 ? now : old;
                    }
            }
            throw new Bail("script_construct_unsupported", detail: x.Op);
        }

        private V Concat(V a, V b) => Text(a) is string x && Text(b) is string y ? new Str(x + y, Tainted(a) || Tainted(b))
            : Arith("+", Num(a) is { K: 'c' } na ? new N('u', St: na.St) : Num(a), Num(b) is { K: 'c' } nb ? new N('u', St: nb.St) : Num(b));

        private V Binary(string op, V a, V b)
        {
            switch (op)
            {
                case "+" when a is Str || b is Str: return Concat(a, b);
                case "<" or ">" or "<=" or ">=": return Compare(op, a, b);
                case "==" or "===": return Equal(a, b, op == "===");
                case "!=" or "!==": return Not(Equal(a, b, op == "!=="));
                case "instanceof" or "in": return new N('u', St: Tainted(a) || Tainted(b));
            }
            return Arith(op, Num(a), Num(b));
        }

        private void Assign(X target, V v, Scope s)
        {
            switch (target.Op)
            {
                case "id": Store(target.Name!, v, s); return;
                case "mem" or "idx":
                    {
                        V obj = Ev(target.K[0]!, s);
                        string? key = target.Op == "mem" ? target.Name : Text(Ev(target.K[1]!, s));
                        if (key is null) throw new Bail("dynamic_member_write");
                        switch (obj)
                        {
                            case Ly l: Write(l, key, v); return;
                            case Host { Name: "shared" }: return;   // 别的脚本经 shared 读，由 Liveness 处理
                            case Host { Name: "animation" }:
                                // 本层动画速率：每次都写同一个烘焙期常量时，运行时观测给的轨道速率（跑完观测帧后读）就是它，轨道周期已按它缩放
                                if (key != "rate") throw new Bail("layer_api", detail: "animation." + key);
                                if (v is not N { K: 'c', St: false } r || rate is not null && rate.C != r.C)
                                    throw v is N { K: 'e' } e ? new Bail("script_reads_external_input", true, "animation rate depends on live input " + e.Why)
                                        : new Bail("animation_rate_varies", detail: "animation.rate = " + Key(v));
                                rate = r;
                                return;
                            case Ob o:
                                {
                                    var fields = new Dictionary<string, V>(o.F, StringComparer.Ordinal) { [key] = v };
                                    Assign(target.K[0]!, new Ob(fields, o.Cls), s);
                                    return;
                                }
                            case Ar a when int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out int index) && index < 100_000:
                                {
                                    var items = a.E.ToList();
                                    while (items.Count <= index) items.Add(Un.I);
                                    items[index] = v;
                                    Assign(target.K[0]!, new Ar([.. items]), s);
                                    return;
                                }
                        }
                        // 写进说不清的对象（thisLayer.getAnimation().rate 之类）：原因取那个对象的来源，不是运行时错误
                        if (obj is N { K: 'o', Why: string why }) throw new Bail(why, detail: "property write on " + Key(obj));
                        throw new Bail("script_runtime_error", detail: "property write on " + Key(obj));
                    }
            }
            throw new Bail("script_construct_unsupported", detail: "assignment target");
        }

        private V Mutate(Ar arr, string name, V[] args, X target, Scope s)
        {
            var items = arr.E.ToList();
            V result = Un.I;
            switch (name)
            {
                case "push": items.AddRange(args); result = Const(items.Count); break;
                case "unshift": items.InsertRange(0, args); result = Const(items.Count); break;
                case "pop": if (items.Count > 0) { result = items[^1]; items.RemoveAt(items.Count - 1); } break;
                case "shift": if (items.Count > 0) { result = items[0]; items.RemoveAt(0); } break;
                case "reverse": items.Reverse(); break;
                default: throw new Bail("script_construct_unsupported", detail: "array." + name);
            }
            Assign(target, new Ar([.. items]), s);
            return name == "reverse" ? new Ar([.. items]) : result;
        }

        private void Write(Ly layer, string property, V v)
        {
            if (layer.Name == "?") v = Opaque("write_to_unknown_layer", Tainted(v));
            writes[(layer.Name ?? "") + "|" + property] = v;
        }

        private static N External(string input) => new('e', Why: input);

        private V Get(V obj, string name)
        {
            switch (obj)
            {
                case Ob o:
                    if (o.F.TryGetValue(name, out V? field)) return field;
                    return o.Cls is not null ? VecMethod(o, name) : Un.I;
                case Ar a:
                    if (name == "length") return Const(a.E.Length);
                    if (int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out int index)) return index < a.E.Length ? a.E[index] : Un.I;
                    return ArrayMethod(a, name);
                case Str t:
                    if (name == "length") return Const(t.Text.Length, t.St);
                    return new Bi((_, args) => name is "toString" or "trim" ? t : new N('u', St: t.St || args.Any(Tainted)));
                case N n: return n.K == 'c' ? new Bi((_, _) => new N('u', St: n.St)) : n;
                case Ly l: return LayerGet(l, name);
                case Host h: return HostGet(h, name);
                case Builder b:
                    return new Bi((_, args) =>
                    {
                        if (name == "finish")
                        {
                            var values = new Dictionary<string, V>(b.Defaults, StringComparer.Ordinal);
                            foreach (var (key, baked) in binding.Node["scriptproperties"] as JsonObject ?? [])
                                values[key] = Parse(baked);
                            return new Ob(values);
                        }
                        var defaults = new Dictionary<string, V>(b.Defaults, StringComparer.Ordinal);
                        if (args.FirstOrDefault() is Ob spec && spec.F.GetValueOrDefault("name") is Str named) defaults[named.Text] = spec.F.GetValueOrDefault("value") ?? Un.I;
                        return new Builder(defaults);
                    });
                case Fn or Bi: return name is "call" or "apply" or "bind" ? throw new Bail("script_construct_unsupported", detail: name) : Un.I;
            }
            throw new Bail("script_runtime_error", detail: $"read of '{name}' on {Key(obj)}");
        }

        private V LayerGet(Ly l, string name)
        {
            // 本层时间轴动画：rate 按烘焙期常量接（见 Assign），其余成员同其他图层 API
            if (name == "getAnimation" && l.Name is null && !collect) return new Bi((_, _) => new Host("animation"));
            if (name.StartsWith("get", StringComparison.Ordinal) || name is "play" or "stop" or "pause" or "setAnimation" or "playSingleAnimation" or "setFrame")
            {
                if (collect && name is "getChildren" or "getParent" or "getChildByName") Unresolved = true;
                // 播放控制（play/stop/setFrame 等）改的是动画轨道，由运行时轨道的脚本控制播放检查处理，这里不算属性输出
                return new Bi((_, _) => Opaque("layer_api"));
            }
            if (l.Name is null)
            {
                if (writes.TryGetValue("|" + name, out V? written)) return written;
                if (self.TryGetValue(name, out V? held)) return held;
                return name == "name" ? new Str(binding.Owner["name"]?.ToString() ?? "") : Parse(binding.Owner[name]);
            }
            return name == "name" && l.Name != "?" ? new Str(l.Name) : Opaque("script_reads_layer");
        }

        private V HostGet(Host h, string name)
        {
            switch (h.Name)
            {
                case "engine":
                    switch (name)
                    {
                        case "runtime":
                            return time switch { 's' => new N('l', 0, 1), 'z' => Const(0), _ => throw new Bail("script_state_reads_time", detail: "cross-frame state also reads engine.runtime") };
                        case "frametime": return Const(frametime);
                        case "timeOfDay": return time == 'n' ? throw new Bail("script_reads_external_input", true, "wall_clock") : External("wall_clock");
                        case "canvasSize" or "screenResolution": return Vec(2, [new N('u'), new N('u')]);
                        case "userProperties": return new N('u');
                        case "registerAudioBuffers":
                            return new Bi((_, _) => time == 'n' ? throw new Bail("script_reads_external_input", true, "audio") : External("audio"));
                        case "setTimeout" or "setInterval":
                            return new Bi((_, args) =>
                            {
                                if (args.FirstOrDefault() is not Fn fn) return new N('u');
                                if (collect) { Call(fn, null, []); return new N('u'); }
                                // 定时回调在将来某个时刻才跑：它改动的模块状态与写出的属性都随时间变，记说不清
                                Snap before = Save(Root);
                                Call(fn, null, []);
                                foreach (var (key, v) in Root.Vars.ToArray())
                                    if (!before.Vars[Root].TryGetValue(key, out V? old) || Key(old) != Key(v)) Root.Vars[key] = Opaque("script_timer");
                                foreach (var (key, v) in writes.ToArray())
                                    if (!before.Writes.TryGetValue(key, out V? old) || Key(old) != Key(v)) writes[key] = Opaque("script_timer");
                                return new N('u');
                            });
                        case "clearTimeout" or "clearInterval" or "openUserShortcut": return new Bi((_, _) => Un.I);
                    }
                    return name.StartsWith("is", StringComparison.Ordinal) ? new Bi((_, _) => new N('u')) : Opaque("engine_api");
                case "input": return time == 'n' ? throw new Bail("script_reads_external_input", true, "pointer") : External("pointer");
                case "Date" when name == "now": return new Bi((_, _) => time == 'n' ? throw new Bail("script_reads_external_input", true, "wall_clock") : External("wall_clock"));
                case "console": return new Bi((_, _) => Un.I);
                case "animation": return name == "rate" && rate is not null ? rate : Opaque("layer_api");
                case "shared": return Opaque("shared_state");
                case "localStorage": return new Bi((_, _) => Opaque("persistent_storage"));
                case "thisScene":
                    if (name == "getLayer")
                        return new Bi((_, args) =>
                        {
                            if (args.FirstOrDefault() is Str layer) { Names.Add(layer.Text); return new Ly(layer.Text); }
                            Unresolved = true;
                            return new Ly("?");
                        });
                    if (collect) Unresolved = true;
                    return new Bi((_, _) => Opaque("scene_api"));
                case "createScriptProperties": return Un.I;
                case "Math": return MathMember(name);
                case "Number" or "String" or "Boolean" or "parseFloat" or "parseInt" or "isNaN" or "isFinite" or "Object" or "JSON" or "Array":
                    return new Bi((_, args) => Pure(args, null));
                case "Vec2" or "Vec3" or "Vec4": return Un.I;
            }
            if (h.Name.StartsWith("module:", StringComparison.Ordinal)) return new Bi((_, args) => Library(name, args));
            return Opaque("unknown_api");
        }

        /// <summary>Host 自身被当函数调：Vec3(...)、createScriptProperties()、Number(x) 等。</summary>
        private V CallHost(Host h, V[] args) => h.Name switch
        {
            "createScriptProperties" => new Builder(new(StringComparer.Ordinal)),
            "Vec2" or "Vec3" or "Vec4" => MakeVec(h.Name[3] - '0', args),
            "Number" or "parseFloat" or "parseInt" => args.Length == 0 ? Const(0) : Num(args[0]) is var n && h.Name == "parseInt" && n.K == 'c' ? Const(Math.Truncate(n.C), n.St) : Num(args[0]),
            "String" => args.Length == 0 ? new Str("") : Text(args[0]) is string text ? new Str(text, Tainted(args[0])) : Num(args[0]),
            "Boolean" => args.Length == 0 ? Const(0) : Truth(args[0]),
            "isNaN" or "isFinite" => Pure(args, v => (h.Name == "isNaN" ? double.IsNaN(v[0]) : double.IsFinite(v[0])) ? 1 : 0),
            "Date" => External("wall_clock"),
            _ => Pure(args, null),
        };

        private V MakeVec(int size, V[] args)
        {
            if (args.Length == 1 && args[0] is Ob from) return Vec(size, [.. "xyzw"[..size].Select(c => from.F.GetValueOrDefault(c.ToString()) ?? Const(0))]);
            if (args.Length == 1 && args[0] is N scalar) return Vec(size, [.. Enumerable.Repeat((V)scalar, size)]);
            return Vec(size, args);
        }

        private V VecMethod(Ob o, string name)
        {
            V[] Parts(Ob v) => [.. "xyzw"[..(o.Cls![3] - '0')].Select(c => v.F.GetValueOrDefault(c.ToString()) ?? Const(0))];
            V Each(V other, string op) => Vec(o.Cls![3] - '0', [.. Parts(o).Select((p, i) => (V)Arith(op, Num(p), other is Ob ob ? Num(Parts(ob)[i]) : Num(other)))]);
            return name switch
            {
                "add" => new Bi((_, a) => Each(a[0], "+")),
                "subtract" => new Bi((_, a) => Each(a[0], "-")),
                "multiply" => new Bi((_, a) => Each(a[0], "*")),
                "divide" => new Bi((_, a) => Each(a[0], "/")),
                "copy" => new Bi((_, _) => o),
                "length" => new Bi((_, _) => Pure(Parts(o), v => Math.Sqrt(v.Sum(x => x * x)))),
                _ => new Bi((_, a) => Pure([.. Parts(o), .. a], null)),
            };
        }

        private V ArrayMethod(Ar a, string name) => name switch
        {
            "map" => new Bi((_, args) => new Ar([.. a.E.Select((e, i) => Call(args[0], null, [e, Const(i), a]))])),
            "forEach" => new Bi((_, args) => { for (int i = 0; i < a.E.Length; ++i) Call(args[0], null, [a.E[i], Const(i), a]); return Un.I; }),
            "filter" => new Bi((_, args) =>
            {
                var kept = new List<V>();
                foreach (var (e, i) in a.E.Select((e, i) => (e, i)))
                    if (Decide(Truth(Call(args[0], null, [e, Const(i), a]))) is bool k) { if (k) kept.Add(e); }
                    else throw new Bail("filter_on_time");
                return new Ar([.. kept]);
            }),
            "includes" or "indexOf" => new Bi((_, args) =>
            {
                for (int i = 0; i < a.E.Length; ++i)
                    if (Decide(Equal(a.E[i], args[0], true)) is bool k) { if (k) return name == "includes" ? Const(1) : Const(i); }
                    else return new N('u', St: Tainted(args[0]));
                return name == "includes" ? Const(0) : Const(-1);
            }),
            "slice" or "concat" or "join" => new Bi((_, args) => name == "join" && a.E.All(e => Text(e) is not null)
                ? new Str(string.Join(args.FirstOrDefault() is Str sep ? sep.Text : ",", a.E.Select(e => Text(e)!)))
                : name == "concat" ? new Ar([.. a.E, .. args.SelectMany(x => x is Ar other ? other.E : [x])]) : throw new Bail("script_construct_unsupported", detail: "array." + name)),
            _ => throw new Bail("script_construct_unsupported", detail: "array." + name),
        };

        /// <summary>确定的纯函数：全是常数且给了实现就算出值；周期/常数进，周期出；线性时间进、没有专门规则的，说不清。</summary>
        private N Pure(V[] args, Func<double[], double>? f, Func<N, N>? linear = null)
        {
            var ns = new List<N>();
            void Add(V v) { if (v is Ob o) foreach (V x in o.F.Values) Add(x); else if (v is Ar a) foreach (V x in a.E) Add(x); else ns.Add(Num(v)); }
            foreach (V v in args) Add(v);
            bool st = ns.Any(n => n.St);
            if (Worst(st, [.. ns]) is N worst) return worst;
            if (ns.All(n => n.K == 'c')) return f is null ? new N('u', St: st) : Const(f([.. ns.Select(n => n.C)]), st);
            if (ns.Count == 1 && ns[0].K is 'l' or 'f' && linear is not null) return linear(ns[0]) with { St = st };
            if (ns.Any(n => n.K is 'l' or 'f')) return Opaque("nonlinear_time", st);
            return ns.Any(n => n.K == 'p') ? Per(st, [.. ns.Select(n => n.P)]) : new N('u', St: st);
        }

        private N MinMax(V[] args, bool min)
        {
            N acc = args.Length == 0 ? Const(min ? double.PositiveInfinity : double.NegativeInfinity) : Num(args[0]);
            foreach (V arg in args.Skip(1))
            {
                N b = Num(arg);
                if (acc.K == 'c' && b.K == 'c') { acc = Const(min ? Math.Min(acc.C, b.C) : Math.Max(acc.C, b.C), acc.St || b.St); continue; }
                N d = Arith("-", acc, b);
                if (d.K == 'l' && !double.IsNaN(d.C))
                {
                    // 过交点后固定取一边
                    AddSettle(-d.C / d.A);
                    acc = (d.A > 0) == min ? b with { St = acc.St || b.St } : acc with { St = acc.St || b.St };
                }
                else acc = Pure([acc, b], v => min ? Math.Min(v[0], v[1]) : Math.Max(v[0], v[1]));
            }
            return acc;
        }

        private V MathMember(string name)
        {
            switch (name)
            {
                case "PI": return Const(Math.PI);
                case "E": return Const(Math.E);
                case "LN2": return Const(Math.Log(2));
                case "LN10": return Const(Math.Log(10));
                case "SQRT2": return Const(Math.Sqrt(2));
                case "random": return new Bi((_, _) => time == 'n' ? throw new Bail("script_reads_random") : new N('r'));
                case "min" or "max": return new Bi((_, a) => MinMax(a, name == "min"));
                case "abs":
                    return new Bi((_, a) => Pure(a, v => Math.Abs(v[0]), l =>
                    {
                        if (l.K == 'f' || double.IsNaN(l.C)) return Opaque("nonlinear_time");
                        AddSettle(-l.C / l.A);
                        return Lin(Math.Abs(l.A), Math.Sign(l.A) * l.C, false);
                    }));
            }
            Func<double, double>? f = name switch
            {
                "sin" => Math.Sin, "cos" => Math.Cos, "tan" => Math.Tan, "floor" => Math.Floor, "ceil" => Math.Ceiling,
                "round" => x => Math.Floor(x + 0.5), "trunc" => Math.Truncate, "sqrt" => Math.Sqrt, "exp" => Math.Exp, "log" => Math.Log,
                "asin" => Math.Asin, "acos" => Math.Acos, "atan" => Math.Atan, "sign" => x => Math.Sign(x), "log2" => Math.Log2, "log10" => Math.Log10,
                "cbrt" => Math.Cbrt, "fround" => x => (float)x, "sinh" => Math.Sinh, "cosh" => Math.Cosh, "tanh" => Math.Tanh,
                _ => null,
            };
            Func<N, N>? linear = name switch
            {
                "sin" or "cos" => l => l.K == 'l' ? Per(false, [2 * Math.PI / Math.Abs(l.A)]) : Opaque("nonlinear_time"),
                "tan" => l => l.K == 'l' ? Per(false, [Math.PI / Math.Abs(l.A)]) : Opaque("nonlinear_time"),
                "floor" or "ceil" or "round" or "trunc" => l => l.K == 'l' ? new N('f', l.C, l.A) : Opaque("nonlinear_time"),
                _ => null,
            };
            if (f is not null) return new Bi((_, a) => Pure(a.Take(1).ToArray(), v => f(v[0]), linear));
            return name switch
            {
                "pow" => new Bi((_, a) => Pure(a, v => Math.Pow(v[0], v[1]))),
                "atan2" => new Bi((_, a) => Pure(a, v => Math.Atan2(v[0], v[1]))),
                "hypot" => new Bi((_, a) => Pure(a, v => Math.Sqrt(v.Sum(x => x * x)))),
                _ => Opaque("unknown_api"),
            };
        }

        /// <summary>WEMath / WEColor / WEVector：hsv2rgb 的色相按模 1（线性时间进 → 周期 1/a）；clamp、smoothStep 按比较；其余当确定的纯函数。</summary>
        private V Library(string name, V[] args)
        {
            switch (name)
            {
                case "hsv2rgb" when args.FirstOrDefault() is Ob hsv:
                    {
                        N h = Num(hsv.F.GetValueOrDefault("x") ?? Const(0)), sat = Num(hsv.F.GetValueOrDefault("y") ?? Const(0)), val = Num(hsv.F.GetValueOrDefault("z") ?? Const(0));
                        if (h.K == 'c' && sat.K == 'c' && val.K == 'c') return Vec(3, [.. Hsv(h.C, sat.C, val.C).Select(c => (V)Const(c, h.St || sat.St || val.St))]);
                        N hue = h.K == 'l' ? Per(h.St, [1 / Math.Abs(h.A)]) : h;
                        N form = Pure([hue, sat, val], null);
                        return Vec(3, [form, form, form]);
                    }
                case "clamp" when args.Length == 3: return MinMax([MinMax([args[0], args[2]], true), args[1]], false);
                case "smoothStep" or "smoothstep" when args.Length == 3:
                    {
                        N t = MinMax([MinMax([Arith("/", Arith("-", Num(args[2]), Num(args[0])), Arith("-", Num(args[1]), Num(args[0]))), Const(1)], true), Const(0)], false);
                        return Pure([t], v => v[0] * v[0] * (3 - 2 * v[0]));
                    }
            }
            V? shape = args.FirstOrDefault(a => a is Ob);
            N result = Pure(args, null);
            return shape is Ob o && o.Cls is not null ? Vec(o.Cls[3] - '0', [.. Enumerable.Repeat((V)result, o.Cls[3] - '0')]) : result;
        }

        private static double[] Hsv(double h, double s, double v)
        {
            h = (h % 1 + 1) % 1 * 6;
            int i = (int)Math.Floor(h) % 6;
            double f = h - Math.Floor(h), p = v * (1 - s), q = v * (1 - f * s), t = v * (1 - (1 - f) * s);
            return i switch { 0 => [v, t, p], 1 => [q, v, p], 2 => [p, v, t], 3 => [p, q, v], 4 => [t, p, v], _ => [v, p, q] };
        }
    }
}
