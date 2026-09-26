using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Baker.Core;

/// <summary>改 shader 文本的补丁：在烘焙用的项目副本 shaders/ 下写同名覆盖文件。渲染器是包内文件优先，覆盖只影响捕获。</summary>
public static class ShaderTextPatch
{
    /// <summary>
    /// 给捕获场景里挂了 <see cref="ShaderPeriodAnalysis.TimeScaleKey"/> 的 pass 写覆盖 shader：g_Time 的每处使用换成
    /// (g_Time*g_PeriodicaTimeScale)，并声明这个材质常量（缺省 1，没挂倍率的 pass 行为不变）。原文取自源项目（包内优先，其次 assets）；
    /// 返回每个写出文件的记录（资源名、SHA256），写进 bake.json 以便复现。
    /// </summary>
    public static async Task<JsonArray> WriteTimeScaleAsync(string captureProject, ProjectSource source, string? assetsDirectory,
        JsonObject captureScene, CancellationToken cancellationToken)
    {
        var shaders = new SortedSet<string>(StringComparer.Ordinal);
        foreach (JsonObject owner in (captureScene["objects"] as JsonArray ?? []).OfType<JsonObject>())
            foreach (JsonObject effect in (owner["effects"] as JsonArray ?? []).OfType<JsonObject>())
            {
                JsonArray passes = effect["passes"] as JsonArray ?? [];
                string[] names = [.. ShaderPeriodAnalysis.EffectMaterialShaders(source, assetsDirectory, effect)];
                for (int pass = 0; pass < passes.Count && pass < names.Length; ++pass)
                    if (passes[pass]?["constantshadervalues"]?[ShaderPeriodAnalysis.TimeScaleKey] is not null) shaders.Add(names[pass]);
            }
        var written = new JsonArray();
        foreach (string name in shaders)
            foreach (string stage in new[] { ".frag", ".vert" })
            {
                string resource = "shaders/" + name + stage;
                if (!ShaderPeriodAnalysis.TryReadShaderStage(source, assetsDirectory, resource, out string text)) continue;
                // ponytail: 只改本 stage 文本；#include 进来的头文件若读 g_Time 不会被缩放（WE 自带头文件不读）
                int uses = 0;
                string body = TimeUse.Replace(text, _ => { ++uses; return "(g_Time*g_PeriodicaTimeScale)"; });
                if (uses == 0) continue;
                string patched = "uniform float g_PeriodicaTimeScale; // {\"material\":\"" + ShaderPeriodAnalysis.TimeScaleKey +
                    "\",\"default\":1}\n" + body;
                string path = ProjectSource.ContainedPath(captureProject, resource);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                byte[] bytes = new UTF8Encoding(false).GetBytes(patched);
                await File.WriteAllBytesAsync(path, bytes, cancellationToken);
                written.Add(new JsonObject { ["resource"] = resource, ["uses_rewritten"] = uses, ["sha256"] = Convert.ToHexString(SHA256.HashData(bytes)) });
            }
        return written;
    }

    // g_Time 的使用处，不含它自己的 uniform 声明
    private static readonly Regex TimeUse = new(@"(?<!\buniform\s+float\s+)\bg_Time\b", RegexOptions.CultureInvariant);
}
