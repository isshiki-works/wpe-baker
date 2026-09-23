using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>
/// wpe-render 的能力集合，来自一次 <c>--version</c> 握手（见 <see cref="RendererClient.CapabilitiesAsync"/>）。
/// 现行格式是一行文本：<c>wpe-render &lt;版本&gt; upstream=… source=… features=a,b,c</c>；特性串冻结到 R9，
/// 且 <c>sparse-readback-v1</c> 必须排第一（旧渲染器靠这个位置区分稀疏读回）。
/// 输出若是 JSON 对象（将来的 <c>--version --json</c>），读它的 <c>features</c> 数组。
/// </summary>
internal sealed record RendererCapabilities(IReadOnlyList<string> Features)
{
    public const string SparseReadbackFeature = "sparse-readback-v1";

    public bool Has(string feature) => Features.Contains(feature, StringComparer.Ordinal);

    /// <summary>稀疏读回：只认排在第一位的 sparse-readback-v1（原来按 "features=sparse-readback-v1" 子串判断）。</summary>
    public bool SparseReadback => Features.Count > 0 && Features[0] == SparseReadbackFeature;

    public static RendererCapabilities Parse(string versionOutput)
    {
        string text = versionOutput.Trim();
        if (text.StartsWith('{'))
        {
            JsonArray features = JsonNode.Parse(text)?["features"] as JsonArray
                ?? throw new InvalidDataException("Renderer --version JSON has no features array.");
            return new(features.Select(item => item?.GetValue<string>() ?? throw new InvalidDataException("Renderer feature must be a string.")).ToArray());
        }
        const string key = "features=";
        foreach (string line in text.Split('\n', StringSplitOptions.TrimEntries))
            foreach (string token in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                if (token.StartsWith(key, StringComparison.Ordinal))
                    return new(token[key.Length..].Split(',', StringSplitOptions.RemoveEmptyEntries));
        return new(Array.Empty<string>());
    }
}
