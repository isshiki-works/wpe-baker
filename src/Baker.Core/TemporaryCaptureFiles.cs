using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>Removes explicitly named scratch files owned by a completed capture stage.</summary>
internal static class TemporaryCaptureFiles
{
    internal static void RequireFreeSpace(string outputPath, ulong expectedBytes = 0)
    {
        const ulong reserve = 1024UL * 1024 * 1024;
        string volume = Path.GetPathRoot(Path.GetFullPath(outputPath))!;
        long available = new DriveInfo(volume).AvailableFreeSpace;
        if (available < 0 || (UInt128)(ulong)available < (UInt128)expectedBytes + reserve)
            throw new IOException($"Insufficient free space on {volume}: {available:N0} bytes available; " +
                $"{expectedBytes:N0} bytes of capture data plus a 1 GiB free-space reserve are required.");
    }

    internal static async Task WaitWhileWritingAsync(Task completed, string outputPath, CancellationToken token)
    {
        while (!completed.IsCompleted)
        {
            await Task.WhenAny(completed, Task.Delay(1000, token));
            token.ThrowIfCancellationRequested();
            if (!completed.IsCompleted) RequireFreeSpace(outputPath);
        }
        await completed;
    }

    internal static void Delete(JsonObject report, string directory, params string[] relativePaths)
    {
        foreach (string relative in relativePaths)
        {
            string path = ProjectSource.ContainedPath(directory, relative);
            try
            {
                ProjectSource.EnsureNoReparsePoints(path);
                File.Delete(path);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                var errors = report["temporary_cleanup_errors"] as JsonArray;
                if (errors is null) report["temporary_cleanup_errors"] = errors = new JsonArray();
                errors.Add(new JsonObject { ["path"] = path, ["error"] = error.Message });
            }
        }
    }
}
