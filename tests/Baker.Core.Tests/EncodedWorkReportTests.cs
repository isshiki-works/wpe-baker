using System.Text.Json.Nodes;
using Baker.Core;
using Xunit;

[Trait("Layer", "L1")]
public class EncodedWorkReportTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (bool prefix in new[] { false, true })
        foreach (bool keep in new[] { false, true })
        foreach (bool publish in new[] { false, true })
        foreach (bool success in new[] { false, true })
            yield return [prefix, keep, publish, success];
    }

    // CPU contract for the two report builders and their shared save/publication/cleanup operations.
    // Rendering, encoder and composition gates are outside this test.
    [Theory]
    [MemberData(nameof(Cases))]
    public async Task EncodedWorkSurvivesReportPublicationAndCleanup(bool prefix, bool keep, bool publish, bool success) =>
        await TestTemp.Run(async root =>
        {
            var layout = new WorkLayout(Directory.CreateDirectory(Path.Combine(root, "work")).FullName);
            string project = Directory.CreateDirectory(Path.Combine(layout.Output, "project")).FullName;
            await File.WriteAllTextAsync(Path.Combine(project, "project.json"), """{"type":"scene","file":"scene.json"}""");
            await File.WriteAllTextAsync(Path.Combine(project, "scene.json"), """{"objects":[]}""");
            string master = Directory.CreateDirectory(Path.Combine(layout.Output, "group", "master")).FullName;
            var plan = new JsonObject { ["route"] = prefix ? "effect_prefix" : "whole_layer",
                ["settings"] = new JsonObject { ["fps_numerator"] = 30000, ["fps_denominator"] = 1001 } };
            var request = new HybridBakeRequest(2, plan, layout.Output, KeepIntermediates: keep);
            JsonObject report = prefix ? BakeReportWriter.EffectPrefixRunning("hash", plan)
                : BakeReportWriter.Running(request, "hash", 30, plan, 1, "source_period_no_repair");
            report["status"] = success ? "candidate_generated" : "candidate_rejected_seam";
            report["project_path"] = project;
            report["groups"]!.AsArray().Add(new JsonObject { ["status"] = "encoded", ["video_path"] = "packed.mp4",
                ["encoded_extent"] = new JsonArray(128, 48), ["packed_alpha"] = true });
            // The hardware preflight extent is the existing fallback when an explicit coded extent is absent.
            report["groups"]!.AsArray().Add(new JsonObject { ["status"] = "encoded", ["video_path"] = "opaque.mp4",
                ["hardware_decode_preflight"] = new JsonObject { ["encoded_extent"] = new JsonArray(64, 32) } });
            var timing = new StageTiming();
            JsonObject returned = await BakeReportWriter.WriteNewAsync(layout.Report, report, timing, CancellationToken.None);
            Assert.Same(report, returned);
            string? destination = publish ? Path.Combine(root, "published") : null;
            await ProjectPublisher.PublishAsync(returned, project, destination, layout, timing, null,
                TestContext.Current.CancellationToken);
            if (!keep)
            {
                Assert.Null(layout.RemoveIntermediates(WorkLayout.KeepsCompositionProbe(returned)));
                returned["intermediates_removed"] = true;
                await BakeReportWriter.SaveAsync(layout.Report, returned, null, CancellationToken.None);
            }
            JsonObject saved = JsonNode.Parse(await File.ReadAllTextAsync(layout.Report))!.AsObject();
            Assert.True(JsonNode.DeepEquals(returned, saved));
            Assert.Equal(keep, Directory.Exists(master));
            JsonNode? work = returned["encoded_video_work"];
            if (success)
            {
                Assert.Equal("measured_dimensions", work!["status"]!.GetValue<string>());
                Assert.Equal(2, work["video_streams"]!.GetValue<int>());
                Assert.Equal((128d * 48 + 64d * 32) * (30000d / 1001), work["coded_pixels_per_second"]!.GetValue<double>());
            }
            else Assert.Null(work);
            if (publish && success)
            {
                JsonObject published = JsonNode.Parse(await File.ReadAllTextAsync(returned["publication_report_path"]!.GetValue<string>()))!.AsObject();
                Assert.True(JsonNode.DeepEquals(work, published["encoded_video_work"]));
                Assert.Equal(returned["status"]!.GetValue<string>(), published["status"]!.GetValue<string>());
                Assert.Equal(returned["project_path"]!.GetValue<string>(), published["project_path"]!.GetValue<string>());
            }
            else
            {
                Assert.Null(returned["publication_report_path"]);
                if (destination is not null) Assert.False(Directory.Exists(destination));
            }
        });
}
