using System.Text;
using System.Text.RegularExpressions;
using Baker.Core;
using Xunit;

// C1.1d：理由/明细必须带键。C1.1b 删反查后 HybridLoopService 有一处英文字面量悄悄退回 key=null，语料没走到那条路径，
// "grep 证明无文本判断"也查不到"生产点是否带键"。这两条 L2 补这个口子：生产点不许写裸文案，文案表不养无人生产的键。
[Trait("Layer", "L2")]
public class BareTextTests
{
    private static readonly string SourceRoot = Path.Combine(LocalTools.RepositoryRoot, "src");

    private static IEnumerable<(string Path, string Text)> Sources() =>
        Directory.EnumerateFiles(SourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part is "obj" or "bin"))
            .Select(path => (path, File.ReadAllText(path)));

    /// <summary>JSON 的 reason / detail 字段赋值：<c>x["reason"] = …</c> 与对象初始化器里的 <c>["reason"] = …</c>。</summary>
    private static readonly Regex Producer = new(@"\[""(?:reason|detail)""\]\s*=(?!=)", RegexOptions.CultureInvariant);

    /// <summary>
    /// 赋值右边（到同层的 <c>;</c> / <c>,</c> 或闭括号为止）出现的字符串字面量内容。认普通、逐字（@）、内插（$）字符串，跳过注释和字符字面量。
    /// </summary>
    internal static List<string> RightHandLiterals(string text, int index)
    {
        var literals = new List<string>();
        int depth = 0;
        while (index < text.Length)
        {
            char c = text[index];
            if (c == '/' && index + 1 < text.Length && text[index + 1] == '/')
            {
                int end = text.IndexOf('\n', index);
                index = end < 0 ? text.Length : end;
                continue;
            }
            if (c is '"' or '$' or '@')
            {
                int quote = index;
                while (quote < text.Length && text[quote] is '$' or '@') quote++;
                if (quote < text.Length && text[quote] == '"')
                {
                    bool verbatim = text.AsSpan(index, quote - index).Contains('@');
                    var literal = new StringBuilder();
                    int i = quote + 1;
                    for (; i < text.Length; ++i)
                    {
                        if (verbatim && text[i] == '"' && i + 1 < text.Length && text[i + 1] == '"') { literal.Append('"'); ++i; continue; }
                        if (!verbatim && text[i] == '\\') { literal.Append(text[i + 1]); ++i; continue; }
                        if (text[i] == '"') break;
                        literal.Append(text[i]);
                    }
                    literals.Add(literal.ToString());
                    index = i + 1;
                    continue;
                }
            }
            if (c == '\'')
            {
                int i = index + 1;
                while (i < text.Length && text[i] != '\'') i += text[i] == '\\' ? 2 : 1;
                index = i + 1;
                continue;
            }
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}') { if (depth == 0) break; depth--; }
            else if (c is ';' or ',' && depth == 0) break;
            index++;
        }
        return literals;
    }

    /// <summary>
    /// 是不是给人看的文案：含字母，并且含空白或非 ASCII 字符。文案键（unresolved.x）与状态码（no_saved_properties）
    /// 没有空白、全 ASCII，不算；单个全角分隔符（；）没有字母，也不算。
    /// </summary>
    internal static bool IsProse(string literal) =>
        literal.Any(char.IsLetter) && literal.Any(ch => char.IsWhiteSpace(ch) || ch > 127);

    [Fact]
    public void ReasonAndDetailProducersCarryKeys()
    {
        // 允许清单为空：reason / detail 要写文案就用 new Message(键, 参数).Write(target, "reason")，或文案表 Get。
        var offenders = new List<string>();
        foreach ((string path, string text) in Sources())
            foreach (Match match in Producer.Matches(text))
                if (RightHandLiterals(text, match.Index + match.Length).FirstOrDefault(IsProse) is string literal)
                    offenders.Add($"{Path.GetRelativePath(SourceRoot, path)}:{text[..match.Index].Count(ch => ch == '\n') + 1}: \"{literal}\"");
        Assert.True(offenders.Count == 0, "reason/detail 写了裸文案：\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void ScannerSeesBareText()
    {
        // 扫描器本身的底线：三种字面量都认得出，键和状态码不误报。
        string sample = """
            a["reason"] = "Plain English reason.";
            b["detail"] = cond ? $"Layer {id} failed" : null;
            c["reason"] = "未给出原因";
            d["reason"] = new Message("reason.x").Text;
            e["reason"] = "no_saved_properties";
            """;
        int[] hits = Producer.Matches(sample).Select(match => RightHandLiterals(sample, match.Index + match.Length).Count(IsProse)).ToArray();
        Assert.Equal(new[] { 1, 1, 1, 0, 0 }, hits);
    }

    /// <summary>
    /// 这几族的键都由产品代码按字面量构造（new Message("…") 或 MessageCatalog.Get("…")）。其余族不在这里查：
    /// blocker.* 由编号表管（BlockerCatalogTests），tradeoff.kind.* / asset_problem.* / sway_retime.* / properties.reason.* 按拼接构造，
    /// cli.* / preset.* / progress.* 是命令行与界面文案，归 C2.5。
    /// </summary>
    private static readonly string[] ProducedFamilies =
        ["unresolved.", "reason.", "bake.", "residual.", "plan.", "hardware_decode.", "effect_prefix.", "setup.", "apply."];

    [Fact]
    public void EveryReasonKeyHasProducer()
    {
        string source = string.Join("\n", Sources().Where(file => Path.GetFileName(file.Path) != "MessageCatalog.cs").Select(file => file.Text));
        string[] orphans = MessageCatalog.Keys
            .Where(key => ProducedFamilies.Any(family => key.StartsWith(family, StringComparison.Ordinal)))
            .Where(key => !source.Contains('"' + key + '"', StringComparison.Ordinal)).Order().ToArray();
        Assert.Empty(orphans);
    }
}
