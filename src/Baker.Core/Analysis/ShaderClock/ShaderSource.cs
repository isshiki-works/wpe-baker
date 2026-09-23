using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Baker.Core.Analysis.ShaderClock;

/// <summary>
/// 一段着色器源码（frag + "\n" + vert）的一次性索引：原文、去注释压空白后的归一化文本（第一次用到时算一次）、
/// 按视图计数的函数，以及 uniform 注释里 material 名 → default 的表。
/// 规则表里的每条指纹都在这里求值；同一段源码不再被每条规则各自归一化、各自数一遍。
/// </summary>
internal sealed class ShaderSource(string text)
{
    private string? normalized;
    private Dictionary<string, JsonNode?>? uniformDefaults;
    private List<(string Name, JsonObject Annotation)>? uniformAnnotations;
    private List<(string Name, JsonObject Annotation)>? comboAnnotations;

    public string Raw { get; } = text;

    /// <summary>去掉 // 与 /* */ 注释、把连续空白压成一个空格后的文本。</summary>
    public string Normalized => normalized ??= Normalize(Raw);

    public string View(bool normalize) => normalize ? Normalized : Raw;

    /// <summary>shader uniform 注释里 material 名 → default 值（frag 在前，同名取先出现的）。</summary>
    public IReadOnlyDictionary<string, JsonNode?> UniformDefaults => uniformDefaults ??= ReadUniformDefaults(Raw);

    /// <summary>
    /// 每个带注释 JSON 的 uniform 声明：uniform 名 → 注释对象（同名多次声明各记一条）。注释在归一化文本里已被去掉，
    /// 规则要核对的 material / default / range 从这里按解析后的 JSON 比，与注释里的空白写法无关。
    /// </summary>
    public IReadOnlyList<(string Name, JsonObject Annotation)> UniformAnnotations => uniformAnnotations ??= ReadAnnotations(Raw,
        @"uniform\s+\w+\s+(?<name>\w+)\s*;\s*//\s*(?<json>\{[^\r\n]*\})", annotation => null);

    /// <summary>每个 // [COMBO] 注释：combo 名 → 注释对象（同上，按解析后的 JSON 比）。</summary>
    public IReadOnlyList<(string Name, JsonObject Annotation)> ComboAnnotations => comboAnnotations ??= ReadAnnotations(Raw,
        @"//\s*\[COMBO\]\s*(?<json>\{[^\r\n]*\})",
        annotation => annotation["combo"] is JsonValue combo && combo.TryGetValue(out string? name) ? name : null);

    /// <summary>注释：// 到行尾，/* */ 可跨行。归一化文本与预处理指令求值共用这一条。</summary>
    private const string Comments = @"//[^\r\n]*|/\*.*?\*/";

    public static string Normalize(string shaderText) => Regex.Replace(
        Regex.Replace(shaderText, Comments, "", RegexOptions.CultureInvariant | RegexOptions.Singleline),
        @"\s+", " ", RegexOptions.CultureInvariant).Trim();

    /// <summary>
    /// 有一行的记号序列以 <paramref name="directive"/> 的记号开头（规则表写法，如 "#if NOISE"、"#if AUDIOPROCESSING == 0"）。
    /// 按预处理指令的词法比：注释换成一个空格（同 C 预处理），空白只分隔记号，行首、# 后、记号之间有没有、有几个都一样；
    /// 标识符与数字各是一个整记号，按原文比、区分大小写（GLSL 宏区分大小写）。所以 "#if NOISE" 认 "\t# if NOISE == 1"，
    /// 不认 "#if NOISE_X"、"#ifdef NOISE"、"#if defined(NOISE)"、"#if noise"，也不认注释里的 #if。
    /// </summary>
    public bool HasDirective(string directive)
    {
        string[] wanted = Tokens(directive);
        return Regex.Replace(Raw, Comments, " ", RegexOptions.CultureInvariant | RegexOptions.Singleline).Split('\n')
            .Any(line => Tokens(line).Take(wanted.Length).SequenceEqual(wanted));
    }

    private static string[] Tokens(string line) => [.. Regex.Matches(line, @"\w+|\S", RegexOptions.CultureInvariant).Select(token => token.Value)];

    /// <summary>
    /// 标识符在代码里被用到：归一化文本（已去注释）里的整词次数多于它的 uniform 声明次数。
    /// 只声明不使用的时钟 uniform（例如 glitter_combine 里的 g_Time）不是时钟输入。
    /// </summary>
    public bool Uses(string identifier) => Count(true, "use", identifier) > 0;

    /// <summary>
    /// 计数种类：word = 整词出现次数；use = 整词次数减去 uniform 声明次数；assign = 任一赋值（+= -= *= /= =）；direct = 直接赋值 =；
    /// scaled = -= *= /=；swizzle = 带可选 .xyzw 分量的任一赋值；substr = 子串（不重叠）出现次数。
    /// </summary>
    public int Count(bool normalize, string kind, string identifier)
    {
        string view = View(normalize);
        string word = Regex.Escape(identifier);
        return kind switch
        {
            "word" => Regex.Matches(view, $@"\b{word}\b", RegexOptions.CultureInvariant).Count,
            "use" => Regex.Matches(view, $@"\b{word}\b", RegexOptions.CultureInvariant).Count -
                Regex.Matches(view, $@"\buniform\s+\w+\s+{word}\s*;", RegexOptions.CultureInvariant).Count,
            "assign" => Regex.Matches(view, $@"\b{word}\s*(?:\+=|-=|\*=|/=|=(?!=))", RegexOptions.CultureInvariant).Count,
            "direct" => Regex.Matches(view, $@"\b{word}\s*=(?!=)", RegexOptions.CultureInvariant).Count,
            "scaled" => Regex.Matches(view, $@"\b{word}\s*(?:-=|\*=|/=)", RegexOptions.CultureInvariant).Count,
            "swizzle" => Regex.Matches(view, $@"\b{word}(?:\.[xyzw]+)?\s*(?:\+=|-=|\*=|/=|=(?!=))", RegexOptions.CultureInvariant).Count,
            "substr" => CountSubstring(view, identifier),
            _ => throw new InvalidDataException($"Unknown shader count kind '{kind}'."),
        };
    }

    public static bool HasAlternateClock(string view) => Regex.IsMatch(view,
        @"\b(?:g_Frametime|g_Runtime|g_DeltaTime)\b", RegexOptions.CultureInvariant);

    private static int CountSubstring(string source, string value)
    {
        int count = 0;
        for (int index = 0; (index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0; index += value.Length) ++count;
        return count;
    }

    private static List<(string, JsonObject)> ReadAnnotations(string shaderText, string pattern, Func<JsonObject, string?> nameOf)
    {
        var annotations = new List<(string, JsonObject)>();
        foreach (Match match in Regex.Matches(shaderText, pattern, RegexOptions.CultureInvariant))
        {
            try
            {
                if (JsonNode.Parse(match.Groups["json"].Value) is JsonObject annotation &&
                    (match.Groups["name"].Success ? match.Groups["name"].Value : nameOf(annotation)) is string name)
                    annotations.Add((name, annotation));
            }
            catch (System.Text.Json.JsonException) { }
        }
        return annotations;
    }

    private static Dictionary<string, JsonNode?> ReadUniformDefaults(string shaderText)
    {
        var defaults = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(shaderText, @"uniform\s+\w+\s+\w+\s*;\s*//\s*(?<json>\{[^\r\n]*\})", RegexOptions.CultureInvariant))
        {
            try
            {
                if (JsonNode.Parse(match.Groups["json"].Value) is JsonObject annotation &&
                    annotation["material"] is JsonValue material && material.TryGetValue<string>(out string? name) && name is not null)
                    defaults.TryAdd(name, annotation["default"]?.DeepClone());
            }
            catch (System.Text.Json.JsonException) { }
        }
        return defaults;
    }
}
