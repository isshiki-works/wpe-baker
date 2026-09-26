using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Baker.Core;

/// <summary>改 shader 文本的补丁：在烘焙用的项目副本 shaders/ 下写同名覆盖文件。渲染器是包内文件优先，覆盖只影响捕获。</summary>
public static class ShaderTextPatch
{
    /// <summary>
    /// 给捕获场景里挂了 <see cref="ShaderPeriodAnalysis.TimeScaleKey"/> 或旋钮键（<see cref="KnobPrefix"/>）的 pass 写覆盖 shader：
    /// 时间倍率把 g_Time 的每处使用换成 (g_Time*g_PeriodicaTimeScale)；旋钮把那一处 token 换成 (token*g_PeriodicaK_&lt;stage&gt;_&lt;id&gt;)。
    /// 两者都声明成材质常量（缺省 1，没挂的 pass 行为不变）。原文取自源项目（包内优先，其次 assets）；
    /// 返回每个写出文件的记录（资源名、SHA256），写进 bake.json 以便复现。
    /// </summary>
    public static async Task<JsonArray> WriteTimeScaleAsync(string captureProject, ProjectSource source, string? assetsDirectory,
        JsonObject captureScene, CancellationToken cancellationToken)
    {
        var shaders = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (JsonObject owner in (captureScene["objects"] as JsonArray ?? []).OfType<JsonObject>())
            foreach (JsonObject effect in (owner["effects"] as JsonArray ?? []).OfType<JsonObject>())
            {
                JsonArray passes = effect["passes"] as JsonArray ?? [];
                string[] names = [.. ShaderPeriodAnalysis.EffectMaterialShaders(source, assetsDirectory, effect)];
                for (int pass = 0; pass < passes.Count && pass < names.Length; ++pass)
                    foreach (var (key, _) in passes[pass]?["constantshadervalues"] as JsonObject ?? [])
                        if (key == ShaderPeriodAnalysis.TimeScaleKey || key.StartsWith(KnobPrefix, StringComparison.Ordinal))
                            (shaders.TryGetValue(names[pass], out var keys) ? keys : shaders[names[pass]] = new(StringComparer.Ordinal)).Add(key);
            }
        var written = new JsonArray();
        foreach (var (name, keys) in shaders)
            foreach (string stage in new[] { "frag", "vert" })
            {
                string resource = "shaders/" + name + "." + stage;
                if (!ShaderPeriodAnalysis.TryReadShaderStage(source, assetsDirectory, resource, out string body)) continue;
                // ponytail: 只改本 stage 文本；#include 进来的头文件若读 g_Time 不会被缩放（WE 自带头文件不读）
                int uses = 0;
                var header = new StringBuilder();
                foreach (string key in keys.Where(key => key.StartsWith(KnobPrefix + stage + "_", StringComparison.Ordinal)))
                {
                    // 分析时判过恰好一次；对不上说明源码变了
                    if (KnobUses(body, key) is not [Match use])
                        throw new InvalidDataException($"Shader knob {key} no longer occurs exactly once in {resource}.");
                    string uniform = "g_PeriodicaK_" + key[KnobPrefix.Length..];
                    body = body[..use.Index] + "(" + use.Value + "*" + uniform + ")" + body[(use.Index + use.Length)..];
                    header.Append("uniform float " + uniform + "; // {\"material\":\"" + key + "\",\"default\":1}\n");
                    ++uses;
                }
                if (keys.Contains(ShaderPeriodAnalysis.TimeScaleKey))
                {
                    int before = uses;
                    body = TimeUse.Replace(body, _ => { ++uses; return "(g_Time*g_PeriodicaTimeScale)"; });
                    if (uses > before) header.Append("uniform float g_PeriodicaTimeScale; // {\"material\":\"" + ShaderPeriodAnalysis.TimeScaleKey + "\",\"default\":1}\n");
                }
                if (uses == 0) continue;
                string patched = header + body;
                string path = ProjectSource.ContainedPath(captureProject, resource);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                byte[] bytes = new UTF8Encoding(false).GetBytes(patched);
                await File.WriteAllBytesAsync(path, bytes, cancellationToken);
                written.Add(new JsonObject { ["resource"] = resource, ["uses_rewritten"] = uses, ["sha256"] = Convert.ToHexString(SHA256.HashData(bytes)) });
            }
        return written;
    }

    /// <summary>旋钮材质常量键的前缀：periodica_k_&lt;stage&gt;_&lt;id&gt;。</summary>
    private const string KnobPrefix = "periodica_k_";

    /// <summary>引擎时间签名里一个旋钮（terms[].knobs[]）的材质常量键；id 是字面量的 float32 位（8 位十六进制）或 uniform 名。</summary>
    internal static string KnobKey(JsonObject knob) => KnobPrefix + knob["stage"]!.GetValue<string>() + "_" +
        (knob["uniform"] is JsonValue uniform ? uniform.GetValue<string>()
            : BitConverter.SingleToUInt32Bits((float)knob["literal"]!.GetValue<double>()).ToString("x8", CultureInfo.InvariantCulture));

    /// <summary>
    /// 旋钮键在一个 stage 源码里的出现处（注释除外；分析判"恰好一次"和写覆盖共用这一份）：
    /// 字面量按 float32 值匹配数字字面量 token，忽略符号；uniform 按标识符，不含声明行。
    /// </summary>
    internal static Match[] KnobUses(string text, string key)
    {
        string id = key[(key.IndexOf('_', KnobPrefix.Length) + 1)..];
        // 注释换成等长空白，下标仍对得上原文
        string code = Comment.Replace(text, match => new string(' ', match.Length));
        if (id.Length == 8 && uint.TryParse(id, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint bits))
            return [.. NumberToken.Matches(code).Where(match => float.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) &&
                (BitConverter.SingleToUInt32Bits(value) & 0x7FFFFFFF) == (bits & 0x7FFFFFFF))];
        return [.. Regex.Matches(code, @"\b" + Regex.Escape(id) + @"\b", RegexOptions.CultureInvariant).Where(match =>
            !code[(code.LastIndexOf('\n', Math.Max(0, match.Index - 1)) + 1)..match.Index].TrimStart().StartsWith("uniform", StringComparison.Ordinal))];
    }

    // g_Time 的使用处，不含它自己的 uniform 声明
    private static readonly Regex TimeUse = new(@"(?<!\buniform\s+float\s+)\bg_Time\b", RegexOptions.CultureInvariant);
    private static readonly Regex Comment = new(@"//[^\n]*|/\*.*?\*/", RegexOptions.Singleline | RegexOptions.CultureInvariant);
    // 十进制数字字面量（不含符号），可带 f 后缀；前后不贴标识符或小数点（排除 vec4、g_Texture0、0x1F）
    private static readonly Regex NumberToken = new(@"(?<![\w.])((?:\d+\.\d*|\.\d+|\d+)(?:[eE][+-]?\d+)?)[fF]?(?![\w.])", RegexOptions.CultureInvariant);
}
