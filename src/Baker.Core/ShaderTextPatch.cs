using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Baker.Core;

/// <summary>系数表达式里的一个 g_Speed 分支。Condition：abs_lt = abs(g_Speed - A) &lt; B；gt = g_Speed &gt; A；lt = g_Speed &lt; A。</summary>
public sealed record SwayCoefficientBranch(string Condition, double A, double B, IReadOnlyList<double> Values);

/// <summary>
/// foliagesway 一个时钟语句里 g_Speed * g_Time * 后面的系数表达式：可以是一个 vec4 字面量，也可以是
/// 若干按 g_Speed 选择的分支加兜底 vec4（本类自己写出的改频形态，以及样片代理的阈值形态）。
/// </summary>
public sealed record SwayCoefficientExpression(IReadOnlyList<SwayCoefficientBranch> Branches, IReadOnlyList<double> Fallback)
{
    /// <summary>按 GPU 的 float32 语义求这一层速度命中的 4 个系数。</summary>
    public IReadOnlyList<double> Evaluate(double speed)
    {
        float s = (float)speed;
        foreach (SwayCoefficientBranch branch in Branches)
        {
            bool hit = branch.Condition switch
            {
                "abs_lt" => MathF.Abs(s - (float)branch.A) < (float)branch.B,
                "gt" => s > (float)branch.A,
                "lt" => s < (float)branch.A,
                _ => false
            };
            if (hit) return branch.Values;
        }
        return Fallback;
    }

    public bool SameAs(SwayCoefficientExpression other) =>
        Fallback.SequenceEqual(other.Fallback) && Branches.Count == other.Branches.Count &&
        Branches.Zip(other.Branches).All(pair => pair.First.Condition == pair.Second.Condition &&
            pair.First.A == pair.Second.A && pair.First.B == pair.Second.B && pair.First.Values.SequenceEqual(pair.Second.Values));
}

/// <summary>
/// 改 shader 文本的补丁（与改材质常量的 <see cref="ShaderSpeedPatch"/> 并列）。只用于摆动改频：
/// 在烘焙用的项目副本 shaders/ 下写同名覆盖文件，把 sines / csines 两条时钟语句的系数表达式换成按 g_Speed
/// 选择的改频系数，未命中的速度落回原表达式，其余字节一律不动。渲染器是包内文件优先，覆盖只影响捕获。
/// </summary>
public static class ShaderTextPatch
{
    /// <summary>一条时钟语句：vec4 sines|csines = ... g_Speed * g_Time * EXPR;</summary>
    internal static readonly Regex ClockStatement = new(
        @"vec4\s+(?<name>c?sines)\s*=\s*(?<lead>[^;]*?)\bg_Speed\s*\*\s*g_Time\s*\*\s*(?<expr>[^;]+?)\s*;",
        RegexOptions.CultureInvariant);

    private static readonly Regex Number = new(@"\G[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?f?", RegexOptions.CultureInvariant);

    /// <summary>frag 与 vert 合在一起的解析结果。MaskedText 是把系数表达式换成占位符后的全文，供其余计数判据使用。</summary>
    public sealed record ClockTerms(SwayCoefficientExpression Sines, SwayCoefficientExpression CoSines, string MaskedText);

    /// <summary>
    /// 在 frag+vert 全文里找恰好两条 sines、两条 csines 时钟语句，解析系数表达式并要求两阶段一致。
    /// 接受 0 与已改写过的值；解析不了或两阶段不一致时返回 false（交给后面的规则判不认识）。
    /// </summary>
    public static bool TryParseClockTerms(string text, out ClockTerms terms)
    {
        terms = null!;
        MatchCollection matches = ClockStatement.Matches(text);
        var sines = new List<SwayCoefficientExpression>();
        var cosines = new List<SwayCoefficientExpression>();
        foreach (Match match in matches)
        {
            if (!TryParseExpression(match.Groups["expr"].Value, out SwayCoefficientExpression expression)) return false;
            (match.Groups["name"].Value == "sines" ? sines : cosines).Add(expression);
        }
        if (sines.Count != 2 || cosines.Count != 2 || !sines[0].SameAs(sines[1]) || !cosines[0].SameAs(cosines[1])) return false;
        string masked = ClockStatement.Replace(text, match =>
            match.Value[..(match.Groups["expr"].Index - match.Index)] + "COEFFICIENTS;");
        terms = new(sines[0], cosines[0], masked);
        return true;
    }

    public static bool TryParseExpression(string expression, out SwayCoefficientExpression parsed)
    {
        parsed = null!;
        string compact = Regex.Replace(expression, @"\s+", "", RegexOptions.CultureInvariant);
        int position = 0;
        if (!TryParseNode(compact, ref position, out parsed) || position != compact.Length) return false;
        return true;
    }

    private static bool TryParseNode(string text, ref int position, out SwayCoefficientExpression parsed)
    {
        parsed = null!;
        if (TryVec4(text, ref position, out double[] literal))
        {
            parsed = new([], literal);
            return true;
        }
        if (!Take(text, ref position, "(")) return false;
        string condition;
        double a, b = 0;
        if (Take(text, ref position, "abs(g_Speed-"))
        {
            if (!TryNumber(text, ref position, out a) || !Take(text, ref position, ")<") || !TryNumber(text, ref position, out b)) return false;
            condition = "abs_lt";
        }
        else if (Take(text, ref position, "g_Speed>"))
        {
            if (!TryNumber(text, ref position, out a)) return false;
            condition = "gt";
        }
        else if (Take(text, ref position, "g_Speed<"))
        {
            if (!TryNumber(text, ref position, out a)) return false;
            condition = "lt";
        }
        else return false;
        if (!Take(text, ref position, "?") || !TryVec4(text, ref position, out double[] values) || !Take(text, ref position, ":") ||
            !TryParseNode(text, ref position, out SwayCoefficientExpression rest) || !Take(text, ref position, ")")) return false;
        parsed = new([new(condition, a, b, values), .. rest.Branches], rest.Fallback);
        return true;
    }

    private static bool TryVec4(string text, ref int position, out double[] values)
    {
        values = new double[4];
        int start = position;
        if (!Take(text, ref position, "vec4(")) return false;
        for (int index = 0; index < 4; ++index)
        {
            if (index > 0 && !Take(text, ref position, ",")) { position = start; return false; }
            if (!TryNumber(text, ref position, out values[index])) { position = start; return false; }
        }
        if (!Take(text, ref position, ")")) { position = start; return false; }
        return true;
    }

    private static bool Take(string text, ref int position, string token)
    {
        if (position + token.Length > text.Length || string.CompareOrdinal(text, position, token, 0, token.Length) != 0) return false;
        position += token.Length;
        return true;
    }

    private static bool TryNumber(string text, ref int position, out double value)
    {
        value = 0;
        Match match = Number.Match(text, position);
        if (!match.Success) return false;
        if (!double.TryParse(match.Value.TrimEnd('f'), NumberStyles.Float, CultureInfo.InvariantCulture, out value) || !double.IsFinite(value))
            return false;
        position += match.Length;
        return true;
    }

    /// <summary>GLSL 浮点字面量：总带小数点、不用指数，15 位有效数字以内。</summary>
    public static string GlslFloat(double value)
    {
        if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
        if (value == 0) return "0.0";
        return value.ToString("0.0#####################", CultureInfo.InvariantCulture);
    }

    /// <summary>一个速度的改频系数：旧值用于核对 shader 没变，新值写进分支。</summary>
    public sealed record SpeedRetime(double Speed, double Tolerance, IReadOnlyList<double> OldCoefficients, IReadOnlyList<double> NewCoefficients);

    /// <summary>
    /// 生成改频表达式：按速度依次 (abs(g_Speed - s) &lt; tol ? vec4(新系数) : ...)，最内层是原表达式原文，
    /// 所以没有列出的速度（其余层、实时层）行为与原作逐位相同。
    /// </summary>
    public static string RetimedExpression(string originalExpression, IReadOnlyList<SpeedRetime> speeds, bool cosines)
    {
        string inner = originalExpression.Trim();
        for (int index = speeds.Count - 1; index >= 0; --index)
        {
            SpeedRetime speed = speeds[index];
            IEnumerable<double> values = speed.NewCoefficients.Skip(cosines ? 4 : 0).Take(4);
            inner = $"(abs(g_Speed - {GlslFloat((float)speed.Speed)}) < {GlslFloat(speed.Tolerance)} ? vec4({string.Join(", ", values.Select(GlslFloat))}) : {inner})";
        }
        return inner;
    }

    /// <summary>
    /// 改写一个阶段（frag 或 vert）的全文。每条时钟语句先核对原表达式在每个速度上求出的系数等于计划里的旧系数，
    /// 不等就抛（shader 在分析之后变了）。返回改写后的全文与改写的语句数。
    /// </summary>
    public static string RewriteStage(string text, IReadOnlyList<SpeedRetime> speeds, out int rewritten)
    {
        int count = 0;
        string result = ClockStatement.Replace(text, match =>
        {
            Group expr = match.Groups["expr"];
            bool cosines = match.Groups["name"].Value == "csines";
            if (!TryParseExpression(expr.Value, out SwayCoefficientExpression parsed))
                throw new InvalidDataException("A foliage sway clock statement no longer parses; analyze the source again.");
            foreach (SpeedRetime speed in speeds)
            {
                IReadOnlyList<double> actual = parsed.Evaluate(speed.Speed);
                IEnumerable<double> expected = speed.OldCoefficients.Skip(cosines ? 4 : 0).Take(4);
                if (!actual.Zip(expected).All(pair => Math.Abs(pair.First - pair.Second) <= 1e-12 * Math.Max(1, Math.Abs(pair.Second))))
                    throw new InvalidDataException("Foliage sway coefficients changed since the loop was analyzed; analyze the source again.");
            }
            ++count;
            int head = expr.Index - match.Index, tail = expr.Index + expr.Length - match.Index;
            return match.Value[..head] + RetimedExpression(expr.Value, speeds, cosines) + match.Value[tail..];
        });
        rewritten = count;
        return result;
    }

    /// <summary>
    /// 按计划里首个候选的 sway_retime 在捕获项目副本里写覆盖 shader。原文取自源项目（包内优先，其次 assets），
    /// 与分析读到的是同一份；返回每个写出文件的记录（资源名、改写语句数、SHA256），写进 bake.json 以便复现。
    /// 候选没有 sway_retime 时什么也不写。
    /// </summary>
    public static async Task<JsonArray> WriteSwayRetimeAsync(string captureProject, ProjectSource source, string? assetsDirectory,
        JsonObject loop, CancellationToken cancellationToken)
    {
        var written = new JsonArray();
        if (loop["candidates"]?.AsArray().FirstOrDefault() is not JsonObject candidate || candidate["sway_retime"] is not JsonObject retime)
            return written;
        foreach (JsonObject shader in retime["shaders"]?.AsArray().OfType<JsonObject>() ?? [])
        {
            string name = shader["shader"]!.GetValue<string>();
            SpeedRetime[] speeds = shader["speeds"]!.AsArray().OfType<JsonObject>().Select(item => new SpeedRetime(
                item["speed"]!.GetValue<double>(), item["tolerance"]!.GetValue<double>(),
                item["coefficients_old"]!.AsArray().Select(value => value!.GetValue<double>()).ToArray(),
                item["coefficients_new"]!.AsArray().Select(value => value!.GetValue<double>()).ToArray())).ToArray();
            if (speeds.Length == 0 || speeds.Any(speed => speed.OldCoefficients.Count != 8 || speed.NewCoefficients.Count != 8))
                throw new InvalidDataException("A sway retime entry needs eight old and eight new coefficients per speed.");
            foreach (string stage in new[] { ".frag", ".vert" })
            {
                string resource = "shaders/" + name + stage;
                if (!ShaderPeriodAnalysis.TryReadShaderStage(source, assetsDirectory, resource, out string text))
                    throw new InvalidDataException($"The sway shader stage {resource} could not be read for retiming.");
                string patched = RewriteStage(text, speeds, out int rewritten);
                if (rewritten != 2) throw new InvalidDataException($"The sway shader stage {resource} does not have exactly two clock statements.");
                string path = ProjectSource.ContainedPath(captureProject, resource);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                byte[] bytes = new UTF8Encoding(false).GetBytes(patched);
                await File.WriteAllBytesAsync(path, bytes, cancellationToken);
                written.Add(new JsonObject
                {
                    ["resource"] = resource, ["path"] = path, ["statements_rewritten"] = rewritten,
                    ["speeds"] = new JsonArray(speeds.Select(speed => (JsonNode)JsonValue.Create(speed.Speed)).ToArray()),
                    ["sha256"] = Convert.ToHexString(SHA256.HashData(bytes))
                });
            }
        }
        return written;
    }
}
