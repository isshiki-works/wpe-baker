using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Baker.Core;

internal static class AnalysisCache
{
    internal static string Key(params object?[] values) => Convert.ToHexStringLower(SHA256.HashData(
        Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(values))));

    /// <summary>类型化键：按声明类型序列化一条键记录再取 SHA-256，键里有哪些成分由记录类型写明。</summary>
    internal static string KeyOf<T>(T value) where T : notnull => Convert.ToHexStringLower(SHA256.HashData(
        System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(value)));

    internal static JsonObject? Read(string? directory, string key)
    {
        if (directory is null) return null;
        string path = Path.Combine(directory, key + ".json");
        if (!File.Exists(path)) return null;
        try { return JsonNode.Parse(File.ReadAllText(path))?.AsObject(); }
        catch (System.Text.Json.JsonException) { return null; }
    }

    internal static void Write(string? directory, string key, JsonObject value)
    {
        if (directory is null) return;
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, key + ".json");
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, value.ToJsonString());
        File.Move(temporary, path, true);
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
