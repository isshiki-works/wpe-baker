using System.Text.Json.Nodes;
using Baker.Core;
using Xunit;

[Trait("Layer", "L3"), Collection("L3 本机工具")]
public class NativeRuntimeBoundaryTests
{
    [Fact]
    public async Task SparseTailMetadataAndRawOptions()
    {
        Assert.SkipUnless(LocalTools.Tools is not null, LocalTools.Missing);
        await TestTemp.Run(async directory =>
        {
            string source = Path.Combine(LocalTools.RepositoryRoot, "tests", "fixtures", "native", "shader-clock");
            var runner = new NativeRenderRunner(LocalTools.Tools!);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var evidence = new JsonArray();
            JsonObject? first = null;
            foreach (ulong frames in new ulong[] { 9, 10 })
            {
                var render = await runner.RenderAsync(new(source, source, Path.Combine(directory, "sparse-" + frames),
                    128, 64, 30, 1, frames, WarmupFrames: 2, TraceScene: true,
                    FrameSamplesOnly: true, FrameSampleStride: 8, FrameSampleWidth: 32,
                    CaptureTarget: new(RuntimeRenderTarget: "_rt_default")), cancellationToken: timeout.Token);
                JsonObject native = render["native_result"]!.AsObject();
                Assert.Equal(frames + 2, native["simulated_frames"]!.GetValue<ulong>());
                Assert.Equal(2ul, native["drawn_frames"]!.GetValue<ulong>());
                Assert.Equal(frames, native["skipped_draw_frames"]!.GetValue<ulong>());
                Assert.Equal(frames == 10, native["last_step_draw_skipped"]!.GetValue<bool>());
                Assert.True(native["runtime_dependencies_complete"]!.GetValue<bool>());
                Assert.Equal("_rt_default", native["capture_source"]!["render_target"]!.GetValue<string>());
                Assert.Equal("observed", native["runtime_video_decoder_observation"]!["status"]!.GetValue<string>());
                if (first is not null)
                {
                    Assert.True(JsonNode.DeepEquals(first["capture_source"], native["capture_source"]));
                    Assert.True(JsonNode.DeepEquals(first["runtime_video_decoders"], native["runtime_video_decoders"]));
                }
                first = native;
                evidence.Add(new JsonObject { ["frames"] = frames, ["native"] = native.DeepClone() });
            }
            foreach (bool adaptive in new[] { false, true })
            {
                var raw = await runner.RenderRawAsync(new(source, source, Path.Combine(directory, "raw-" + adaptive),
                    128, 64, 30, 1, 1, EffectRenderScale: adaptive ? 1 : 0.5, MatchEffectResolution: adaptive), timeout.Token);
                Assert.Equal(adaptive ? 1 : 0.5, raw["native_result"]!["effect_render_scale"]!.GetValue<double>());
                Assert.Equal(adaptive, raw["native_result"]!["match_effect_resolution"]!.GetValue<bool>());
            }
            TestContext.Current.TestOutputHelper?.WriteLine(evidence.ToJsonString());
        });
    }
}
