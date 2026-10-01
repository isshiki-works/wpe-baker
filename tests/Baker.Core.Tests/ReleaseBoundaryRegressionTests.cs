using System.Text.Json.Nodes;
using Baker.Core;
using Xunit;

public class ReleaseBoundaryRegressionTests
{
    private sealed class SyncProgress(Action<RenderProgress> action) : IProgress<RenderProgress>
    {
        public void Report(RenderProgress value) => action(value);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("copy")]
    [InlineData("report")]
    public async Task PublicationFailureDoesNotExposeFinalProject(string failure)
    {
        await TestTemp.Run(async root =>
        {
            string project = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
            File.WriteAllText(Path.Combine(project, "project.json"), "{\"type\":\"scene\",\"file\":\"scene.json\"}");
            File.WriteAllText(Path.Combine(project, "scene.json"), "{\"objects\":[]}");
            var layout = new WorkLayout(Directory.CreateDirectory(Path.Combine(root, "work")).FullName);
            string destination = Path.Combine(root, "final");
            using var cancellation = new CancellationTokenSource();
            using FileStream? locked = failure == "copy"
                ? new FileStream(Path.Combine(project, "scene.json"), FileMode.Open, FileAccess.Read, FileShare.None) : null;
            if (failure == "report") Directory.CreateDirectory(layout.Report);
            var progress = new SyncProgress(_ => { if (failure == "cancel") cancellation.Cancel(); });
            var report = new JsonObject { ["status"] = "candidate_generated" };
            if (failure == "cancel")
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProjectPublisher.PublishAsync(report,
                    project, destination, layout, new StageTiming(), progress, cancellation.Token));
            else
            {
                Exception error = await Assert.ThrowsAnyAsync<Exception>(() => ProjectPublisher.PublishAsync(report,
                    project, destination, layout, new StageTiming(), progress, cancellation.Token));
                Assert.True(error is IOException or UnauthorizedAccessException);
            }
            Assert.False(Directory.Exists(destination));
            Assert.True(File.Exists(Path.Combine(project, "project.json")));
            if (failure != "report")
            {
                JsonObject saved = JsonNode.Parse(File.ReadAllText(layout.Report))!.AsObject();
                Assert.Equal(failure == "cancel" ? "cancelled" : "failed", saved["status"]!.GetValue<string>());
                Assert.Equal(project, saved["project_path"]!.GetValue<string>());
                Assert.NotNull(saved["error_type"]);
            }
        });
    }

    [Fact]
    public async Task DestinationRaceKeepsExistingDataAndMarksWorkingReportFailed()
    {
        await TestTemp.Run(async root =>
        {
            string project = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
            File.WriteAllText(Path.Combine(project, "project.json"), "{\"type\":\"scene\",\"file\":\"scene.json\"}");
            File.WriteAllText(Path.Combine(project, "scene.json"), "{\"objects\":[]}");
            var layout = new WorkLayout(Directory.CreateDirectory(Path.Combine(root, "work")).FullName);
            using var source = new ProjectSource(project);
            string destination = Path.Combine(root, "final");
            Assert.Equal(destination, ProjectPublisher.Destination(destination, source, layout.Output));
            var progress = new SyncProgress(_ =>
            {
                Directory.CreateDirectory(destination);
                File.WriteAllText(Path.Combine(destination, "sentinel.txt"), "existing target");
            });
            var report = new JsonObject { ["status"] = "candidate_generated" };
            await Assert.ThrowsAsync<IOException>(() => ProjectPublisher.PublishAsync(report, project, destination,
                layout, new StageTiming(), progress, TestContext.Current.CancellationToken));
            Assert.Equal("existing target", File.ReadAllText(Path.Combine(destination, "sentinel.txt")));
            JsonObject saved = JsonNode.Parse(File.ReadAllText(layout.Report))!.AsObject();
            Assert.Equal("failed", saved["status"]!.GetValue<string>());
            Assert.Equal(project, saved["project_path"]!.GetValue<string>());
            Assert.Null(saved["publication_report_path"]);
        });
    }

    [Theory]
    [InlineData("bake.json")]
    [InlineData("BAKE.JSON")]
    public async Task PublicationPreservesSourceReportAndWritesSeparateNewReport(string originalName)
    {
        await TestTemp.Run(async root =>
        {
            string project = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
            File.WriteAllText(Path.Combine(project, "project.json"), "{\"type\":\"scene\",\"file\":\"scene.json\"}");
            File.WriteAllText(Path.Combine(project, "scene.json"), "{\"objects\":[]}");
            File.WriteAllText(Path.Combine(project, originalName), "author-owned sentinel");
            using var source = new ProjectSource(project);
            string before = await source.SourceHashAsync(TestContext.Current.CancellationToken);
            string destination = Path.Combine(root, "final");
            var layout = new WorkLayout(Directory.CreateDirectory(Path.Combine(root, "work")).FullName);
            var report = new JsonObject { ["status"] = "candidate_generated", ["source_sha256"] = before };
            await ProjectPublisher.PublishAsync(report, project, destination, layout, new StageTiming(), null, TestContext.Current.CancellationToken);
            string written = report["publication_report_path"]!.GetValue<string>();
            Assert.NotEqual(Path.Combine(destination, "bake.json").ToLowerInvariant(), written.ToLowerInvariant());
            Assert.Equal("author-owned sentinel", File.ReadAllText(Path.Combine(destination, originalName)));
            Assert.Equal(before, await source.SourceHashAsync(TestContext.Current.CancellationToken));
            JsonObject saved = JsonNode.Parse(File.ReadAllText(written))!.AsObject();
            Assert.Equal(destination, saved["project_path"]!.GetValue<string>());
            Assert.Equal(written, JsonNode.Parse(File.ReadAllText(layout.Report))!["publication_report_path"]!.GetValue<string>());
        });
    }

    [Fact]
    public async Task ParameterFailurePreservesUnownedRefreshAndSource()
    {
        await TestTemp.Run(async root =>
        {
            string output = Path.Combine(root, "output"), refresh = output + ".analysis-refresh";
            Directory.CreateDirectory(refresh);
            string sentinel = Path.Combine(refresh, "sentinel.txt");
            await File.WriteAllTextAsync(sentinel, "pre-existing");
            await File.WriteAllTextAsync(Path.Combine(refresh, "project.json"), "{\"type\":\"scene\",\"file\":\"scene.json\"}");
            await File.WriteAllTextAsync(Path.Combine(refresh, "scene.json"), "{\"objects\":[]}");
            await Assert.ThrowsAsync<ArgumentException>(() => new HybridBakeService(new("never-run", "never-run", "never-run", []))
                .BakeAsync(new(2, new JsonObject { ["source"] = refresh }, output, EffectRenderScale: .5, MatchEffectResolution: true),
                    cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal("pre-existing", await File.ReadAllTextAsync(sentinel));
            Assert.True(File.Exists(Path.Combine(refresh, "scene.json")));
            Assert.False(Directory.Exists(output));
            Assert.Throws<IOException>(() => new WorkLayout(output).RequireNewAnalysisRefresh());
        });
    }

    [Fact]
    public async Task CleanupRemovesOnlyRegisteredRefresh()
    {
        await TestTemp.Run(root =>
        {
            var layout = new WorkLayout(Path.Combine(root, "work"));
            layout.RequireNewAnalysisRefresh();
            Directory.CreateDirectory(layout.AnalysisRefresh);
            File.WriteAllText(Path.Combine(layout.AnalysisRefresh, "trace.json"), "{}");
            layout.RemoveIntermediates(false);
            Assert.True(Directory.Exists(layout.AnalysisRefresh));
            layout.AnalysisRefreshCreated();
            layout.RemoveIntermediates(false);
            Assert.False(Directory.Exists(layout.AnalysisRefresh));
            return Task.CompletedTask;
        });
    }
}
