namespace Baker.Core;

/// <summary>Keep authored resources intact when a project already contains a generation report.</summary>
public static class GenerationReportPath
{
    public static string NewPath(string project)
    {
        string normal = Path.Combine(Path.GetFullPath(project), "bake.json");
        return Path.Exists(normal) ? Path.Combine(Path.GetDirectoryName(normal)!, $"bake-{Guid.NewGuid():N}.json") : normal;
    }
}
