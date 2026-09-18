using System.IO.Compression;
using System.Text.Json.Nodes;
using Baker.Core;

internal static class CandidateExportChecks
{
    public static async Task RunAsync(Action<bool, string> check, string root)
    {
        string source = Path.Combine(root, "export-source");
        Directory.CreateDirectory(Path.Combine(source, "resources"));
        await File.WriteAllTextAsync(Path.Combine(source, "project.json"), "{\"type\":\"scene\",\"file\":\"scene.json\"}");
        await File.WriteAllTextAsync(Path.Combine(source, "scene.json"), "{\"objects\":[]}");
        byte[] payload = [1, 2, 3, 4];
        await File.WriteAllBytesAsync(Path.Combine(source, "resources", "tiny.bin"), payload);

        JsonObject report = await CandidateExporter.ExportAsync(new(1, source, Path.Combine(root, "export-output")));
        string archive = report["archive"]!.GetValue<string>();
        using var zip = ZipFile.OpenRead(archive);
        var entries = zip.Entries.ToDictionary(entry => entry.FullName, StringComparer.Ordinal);
        using var content = new MemoryStream();
        using var input = entries["project/resources/tiny.bin"].Open();
        await input.CopyToAsync(content);
        check(entries.Keys.ToHashSet().SetEquals(["project/project.json", "project/scene.json", "project/resources/tiny.bin"]) &&
            content.ToArray().SequenceEqual(payload), "export ZIP roundtrip retains readable project entries and payload bytes");
    }
}
