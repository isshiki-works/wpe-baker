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

    public string Raw { get; } = text;

    /// <summary>去掉 // 与 /* */ 注释、把连续空白压成一个空格后的文本。</summary>
    public string Normalized => normalized ??= Normalize(Raw);

    public string View(bool normalize) => normalize ? Normalized : Raw;

    /// <summary>shader uniform 注释里 material 名 → default 值（frag 在前，同名取先出现的）。</summary>
    public IReadOnlyDictionary<string, JsonNode?> UniformDefaults => uniformDefaults ??= ReadUniformDefaults(Raw);

    public static string Normalize(string shaderText) => Regex.Replace(
        Regex.Replace(shaderText, @"//[^\r\n]*|/\*.*?\*/", "", RegexOptions.CultureInvariant | RegexOptions.Singleline),
        @"\s+", " ", RegexOptions.CultureInvariant).Trim();

    /// <summary>
    /// 计数种类：word = 整词出现次数；assign = 任一赋值（+= -= *= /= =）；direct = 直接赋值 =；
    /// scaled = -= *= /=；swizzle = 带可选 .xyzw 分量的任一赋值；substr = 子串（不重叠）出现次数。
    /// </summary>
    public int Count(bool normalize, string kind, string identifier)
    {
        string view = View(normalize);
        string word = Regex.Escape(identifier);
        return kind switch
        {
            "word" => Regex.Matches(view, $@"\b{word}\b", RegexOptions.CultureInvariant).Count,
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
