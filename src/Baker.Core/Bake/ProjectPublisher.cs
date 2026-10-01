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
    /// 成品先在目标卷暂存，完整报告写好后才整体移动；来源自带的报告文件保留。
    /// 其余状态什么都不做。
    /// </summary>
    internal static async Task PublishAsync(JsonObject report, string project, string? destination, WorkLayout layout,
        StageTiming timing, IProgress<RenderProgress>? progress, CancellationToken cancellationToken)
    {
        if (destination is null || !StaticOnlyBake.Finished(report["status"]!.GetValue<string>())) return;
        try
        {
        progress?.Report(new("saving_project", 1, new Message("progress.saving_project")));
        cancellationToken.ThrowIfCancellationRequested();
        string stagingRoot = Path.Combine(Path.GetDirectoryName(destination)!, $".wpe-baker-work-{Guid.NewGuid():N}");
        if (Path.Exists(stagingRoot)) throw new IOException("The publication work directory must be new.");
        string staged = Path.Combine(stagingRoot, "project");
        report["publication_work_directory"] = stagingRoot;
        using (timing.Measure(StageTiming.ProjectAssembly))
        {
            using var generated = new ProjectSource(project);
            await generated.ExtractAsync(staged, cancellationToken);
        }
        string reportPath = GenerationReportPath.NewPath(staged);
        report["project_path"] = destination;
        report["work_directory"] = layout.Output;
        report["publication_report_path"] = Path.Combine(destination, Path.GetFileName(reportPath));
        await BakeReportWriter.SaveAsync(layout.Report, report, timing, CancellationToken.None);
        await BakeReportWriter.WriteNewAsync(reportPath, report, null, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        Directory.Move(staged, destination);
        try { Directory.Delete(stagingRoot); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        catch (Exception error)
        {
            report["status"] = cancellationToken.IsCancellationRequested || error is OperationCanceledException ? "cancelled" : "failed";
            report["project_path"] = project;
            report.Remove("publication_report_path");
            report["error_type"] = error.GetType().Name;
            report["error"] = error.Message;
            try { await BakeReportWriter.SaveAsync(layout.Report, report, timing, CancellationToken.None); }
            catch (Exception saveError) when (saveError is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }
}
