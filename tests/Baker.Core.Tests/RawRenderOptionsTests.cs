using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Baker.Core;
using Xunit;

[Trait("Layer", "L0")]
public class RawRenderOptionsTests
{
    [Theory]
    [InlineData(1.0, false)]
    [InlineData(0.5, false)]
    [InlineData(1.0, true)]
    public void SharedJobAndReceiptPreserveEffectResolution(double scale, bool adaptive)
    {
        var request = new RenderRequest("source", ".", "output", 3, 5, 60, 1, 1,
            EffectRenderScale: scale, MatchEffectResolution: adaptive);
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
        foreach (bool stdout in new[] { false, true })
        {
            JsonNode job = JsonSerializer.SerializeToNode(RenderJob.From(request, "source", "output", stdout), options)!;
            Assert.Equal(scale == 1 ? null : (double?)scale, job["effect_render_scale"]?.GetValue<double>());
            Assert.Equal(adaptive, job["match_effect_resolution"]!.GetValue<bool>());
        }
        NativeRenderRunner.ConfirmRenderOptions(request, new RenderResult {
            EffectRenderScale = scale, MatchEffectResolution = adaptive });
        Assert.Throws<InvalidDataException>(() => NativeRenderRunner.ConfirmRenderOptions(request,
            new RenderResult { EffectRenderScale = scale == 1 ? .5 : 1, MatchEffectResolution = adaptive }));
        Assert.Throws<InvalidDataException>(() => NativeRenderRunner.ConfirmRenderOptions(request,
            new RenderResult { EffectRenderScale = scale, MatchEffectResolution = !adaptive }));
        if (scale != 1 || adaptive)
            Assert.Throws<InvalidDataException>(() => NativeRenderRunner.ConfirmRenderOptions(request, new RenderResult()));
        else NativeRenderRunner.ConfirmRenderOptions(request, new RenderResult());
    }

    [Theory]
    [InlineData(double.NaN, false)]
    [InlineData(double.PositiveInfinity, false)]
    [InlineData(0, false)]
    [InlineData(-.5, false)]
    [InlineData(1.5, false)]
    [InlineData(.5, true)]
    public async Task BothEntriesRejectInvalidOptionsBeforeCreatingOutput(double scale, bool adaptive) =>
        await TestTemp.Run(async root =>
        {
            var runner = new NativeRenderRunner(new("never-run", "never-run", "never-run", []));
            var request = new RenderRequest("missing-source", "missing-assets", Path.Combine(root, "output"),
                4, 4, 60, 1, 1, EffectRenderScale: scale, MatchEffectResolution: adaptive);
            ArgumentException raw = await Assert.ThrowsAsync<ArgumentException>(() => runner.RenderRawAsync(request,
                TestContext.Current.CancellationToken));
            ArgumentException video = await Assert.ThrowsAsync<ArgumentException>(() => runner.RenderAsync(request,
                cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal(video.Message, raw.Message);
            Assert.False(Directory.Exists(request.OutputDirectory));
        });
}
