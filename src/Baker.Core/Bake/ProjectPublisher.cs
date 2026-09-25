using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>
/// 把验证过的成品工程发布到用户选的壁纸目录。开工前核对目标（必须全新、与源和工作目录互不包含），
/// 收尾时只有成品与 static_only 才发布；被拒、失败、取消的结果不碰目标目录。
/// </summary>
internal static class ProjectPublisher
{
    /// <summary>规范化并核对发布目录；没选目录返回 null。任何渲染或写盘之前调用。</summary>
    internal static string? Destination(string? projectDirectory, ProjectSource source, string output)
    {
        if (projectDirectory is null) return null;
        string destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectDirectory));
        ProjectSource.EnsureNoReparsePoints(destination);
        if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("The destination project directory must be new.");
        foreach (string parent in new[] { source.DirectoryPath, output })
            if (destination.Equals(parent, StringComparison.OrdinalIgnoreCase) ||
                destination.StartsWith(Path.TrimEndingDirectorySeparator(parent) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                parent.StartsWith(destination + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The destination project must be separate from source and working directories.");
        return destination;
    }

    /// <summary>
    /// 成品与 static_only 解包到目标目录，报告改指向目标并记下工作目录，工作目录与目标目录各写一份 bake.json。
    /// 其余状态什么都不做。
    /// </summary>
    internal static async Task PublishAsync(JsonObject report, string project, string? destination, WorkLayout layout,
        StageTiming timing, IProgress<RenderProgress>? progress, CancellationToken cancellationToken)
    {
        if (destination is null || !StaticOnlyBake.Finished(report["status"]!.GetValue<string>())) return;
        progress?.Report(new("saving_project", 1, new Message("progress.saving_project")));
        using (timing.Measure(StageTiming.ProjectAssembly))
        {
            using var generated = new ProjectSource(project);
            await generated.ExtractAsync(destination, cancellationToken);
        }
        report["project_path"] = destination;
        report["work_directory"] = layout.Output;
        await BakeReportWriter.SaveAsync(layout.Report, report, timing, CancellationToken.None);
        await BakeReportWriter.WriteNewAsync(Path.Combine(destination, "bake.json"), report, null, cancellationToken);
    }
}
