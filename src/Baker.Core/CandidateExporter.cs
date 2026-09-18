using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Baker.Core;

public sealed record CandidateExportRequest(int SchemaVersion, string Project, string OutputDirectory, bool CreateZip = true);

public static class CandidateExporter
{
    public static async Task<JsonObject> ExportAsync(CandidateExportRequest request, CancellationToken cancellationToken = default)
    {
        if (request.SchemaVersion != 1) throw new InvalidDataException("Unsupported export request version.");
        using var source = new ProjectSource(request.Project);
        if (source.PackageVersion is not null) throw new InvalidDataException("Export expects the generated editable project; use extract for a source PKG.");
        string output = Path.GetFullPath(request.OutputDirectory);
        ProjectSource.EnsureNoReparsePoints(output);
        if (Directory.Exists(output) || File.Exists(output)) throw new IOException("Export output must be a new directory.");
        if (output.StartsWith(source.DirectoryPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Export output cannot be inside its source project.");
        string hash = await source.SourceHashAsync(cancellationToken);
        Directory.CreateDirectory(output);
        string project = Path.Combine(output, "project");
        await source.ExtractAsync(project, cancellationToken);
        using (var copy = new ProjectSource(project))
            if (hash != await copy.SourceHashAsync(cancellationToken)) throw new IOException("Exported project does not match the selected project.");
        if (hash != await source.SourceHashAsync(cancellationToken)) throw new IOException("Project changed during export.");
        string? archive = null;
        if (request.CreateZip)
        {
            archive = Path.Combine(output, "wallpaper.zip");
            var expectedEntries = new HashSet<string>(StringComparer.Ordinal);
            using (var zipped = ZipFile.Open(archive, ZipArchiveMode.Create))
            {
                foreach (string path in Directory.EnumerateFiles(project, "*", SearchOption.AllDirectories))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string entry = "project/" + Path.GetRelativePath(project, path).Replace('\\', '/');
                    expectedEntries.Add(entry);
                    zipped.CreateEntryFromFile(path, entry, CompressionLevel.Fastest);
                }
            }
            using var archiveReader = ZipFile.OpenRead(archive);
            if (archiveReader.Entries.Count != expectedEntries.Count || archiveReader.Entries.Any(e => !expectedEntries.Contains(e.FullName)))
                throw new IOException("Archive entries do not match exported project files.");
        }
        var report = new JsonObject { ["schema_version"] = 1, ["status"] = "exported", ["project"] = project,
            ["project_sha256"] = hash, ["project_kind"] = source.Kind, ["archive"] = archive, ["source_digest_scope"] = ProjectSource.DigestScope,
            ["playback_and_performance"] = "Export preserves the selected project; it does not add playback or performance verification." };
        await File.WriteAllTextAsync(Path.Combine(output, "export.json"), report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
        return report;
    }
}
