using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Baker.Core;

internal static class AnalysisCache
{
    internal static string Key(params object?[] values) => Convert.ToHexStringLower(SHA256.HashData(
        Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(values))));

    internal static JsonObject? Read(string? directory, string key)
    {
        if (directory is null) return null;
        string path = Path.Combine(directory, key + ".json");
        if (!File.Exists(path)) return null;
        try { return JsonNode.Parse(File.ReadAllText(path))?.AsObject(); }
        // 退回并行时别的子分析可能正替换同一个键：读不到当作没命中。
        catch (Exception error) when (error is System.Text.Json.JsonException or IOException or UnauthorizedAccessException) { return null; }
    }

    internal static void Write(string? directory, string key, JsonObject value)
    {
        if (directory is null) return;
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, key + ".json");
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, value.ToJsonString());
        try { File.Move(temporary, path, true); }
        // 同一个键的内容由键决定：别的子分析（退回并行）同时写了或正读着这份，留着它就行。
        catch (Exception error) when (error is IOException or UnauthorizedAccessException && File.Exists(path)) { File.Delete(temporary); }
    }

    internal static JsonObject Get(string? directory, string key, Func<JsonObject> create)
    {
        JsonObject? cached = Read(directory, key);
        if (cached is not null) return cached;
        JsonObject value = create();
        Write(directory, key, value);
        return value;
    }
}
