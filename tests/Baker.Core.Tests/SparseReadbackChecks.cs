using System.Text.Json;
using System.Text.Json.Nodes;
using Baker.Core;

internal static class SparseReadbackChecks
{
    internal static async Task RunCoverageAsync(string output)
    {
        output=Path.GetFullPath(output);
        if (Directory.Exists(output)) throw new IOException("Coverage check output must be new.");
        Directory.CreateDirectory(output);
        string original=Path.Combine(LocalTools.RepositoryRoot, "tests", "fixtures", "native", "shader-clock"), fixture=Path.Combine(output,"fixture");
        using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using (var source=new ProjectSource(original)) await source.ExtractAsync(fixture,timeout.Token);
        await File.WriteAllTextAsync(Path.Combine(fixture,"shaders/probe.frag"), """
            uniform float g_Time;
            uniform sampler2D g_Texture0;
            varying vec2 v_TexCoord;
            void main() {
                vec2 p=v_TexCoord;
                float center=mod(floor(g_Time*30.0+0.5),8.0)==4.0 ? 0.72 : 0.42;
                if (abs(p.x-center)>0.04 || p.y<0.35 || p.y>0.65) discard;
                gl_FragColor=vec4(1.0,0.3,0.1,1.0)*texSample2D(g_Texture0,p);
            }
            """);
        var tools=LocalTools.Tools!;
        var runner=new NativeRenderRunner(tools);
        var request=new RenderRequest(fixture,original,Path.Combine(output,"gpu"),128,96,30,1,37,
            WarmupFrames:7,Seed:17,FrameSamplesOnly:true,FrameSampleStride:8,FrameSampleWidth:31,
            FrameSampleIncludeAlpha:true,CollectSamplingCoverage:true,
            LayerSelection:new([1],TransparentBackground:true,IncludePostprocessing:false));
        var gpu=await runner.RenderAsync(request,cancellationToken:timeout.Token);
        var baseline=await runner.RenderAsync(request with { OutputDirectory=Path.Combine(output,"cpu"),
            CollectSamplingCoverage=false,CollectAlphaBounds=true,BoundsIncludeRgb=true },cancellationToken:timeout.Token);
        var coverage=gpu["sampling_coverage"]?.AsObject() ?? throw new InvalidDataException("GPU coverage was unavailable.");
        foreach (string key in new[] {"has_content","x","y","width","height","minimum_alpha","maximum_alpha","includes_rgb"})
            if (coverage[key]!.ToJsonString()!=baseline["alpha_bounds"]![key]!.ToJsonString())
                throw new InvalidDataException("Complete GPU coverage disagrees with CPU scanning: "+key);
        byte[] actual=await File.ReadAllBytesAsync(Path.Combine(output,"gpu/frame-samples.rgb"));
        byte[] expected=await File.ReadAllBytesAsync(Path.Combine(output,"cpu/frame-samples.rgb"));
        if (!actual.AsSpan().SequenceEqual(expected)) throw new InvalidDataException("Coverage changed the selected samples.");
        var sample=gpu["frame_samples"]!;
        int sw=sample["logical_width"]!.GetValue<int>(), sh=sample["height"]!.GetValue<int>(), right=-1;
        for (int f=0;f<5;f++) for (int y=0;y<sh;y++) for (int x=0;x<sw;x++)
            if (actual[((f*sh+y)*sw*2+sw+x)*3]!=0) right=Math.Max(right,x);
        if (coverage["x"]!.GetValue<int>()+coverage["width"]!.GetValue<int>() <= Math.Ceiling((right+1)*128.0/sw))
            throw new InvalidDataException("Coverage fixture did not include a boundary appearing only between sampled frames.");
        if (gpu["readback_frames"]!.GetValue<ulong>()!=5 || coverage["frames"]!.GetValue<ulong>()!=37 ||
            coverage["first_simulation_frame"]!.GetValue<ulong>()!=7)
            throw new InvalidDataException("Coverage did not span all output frames while preserving sparse readback.");
        await File.WriteAllTextAsync(Path.Combine(output,"report.json"),new JsonObject {
            ["status"]="passed",["samples_identical"]=true,["unsampled_boundary_included"]=true,
            ["readback_frames"]=5,["coverage"]=coverage.DeepClone()
        }.ToJsonString(new JsonSerializerOptions {WriteIndented=true}));
        Console.WriteLine("Sampling coverage: full interval matches CPU bounds, including an unsampled boundary; sparse samples unchanged.");
    }

    internal static async Task RunNativeAsync(string output)
    {
        output = Path.GetFullPath(output);
        if (Directory.Exists(output)) throw new IOException("Sparse check output must be new.");
        Directory.CreateDirectory(output);
        // 对照组是 tools.json 的 reference_renderer（旧版渲染器），新组是 renderer；编码器与运行库目录共用。
        NativeTools Tools(string renderer) => LocalTools.Tools! with {
            Renderer = renderer, RuntimeDirectories = [Path.GetDirectoryName(renderer)!, .. LocalTools.Tools!.RuntimeDirectories] };
        var oldTools = Tools(LocalTools.ReferenceRenderer!);
        var newTools = Tools(LocalTools.Tools!.Renderer);
        var results = new JsonArray();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        foreach (string fixtureName in new[] { "shader-clock", "random-clock" })
        foreach (ulong? phase in new ulong?[] { null, 3 })
        {
            string fixture = Path.Combine(LocalTools.RepositoryRoot, "tests", "fixtures", "native", fixtureName);
            string label = fixtureName + "-" + (phase?.ToString() ?? "zero");
            var request = new RenderRequest(fixture, fixture, "", 128, 96, 30, 1, 41,
                WarmupFrames: 7, Seed: 17, FrameSampleStride: 8, FrameSampleWidth: 31,
                FrameSamplePhaseFrames: phase, FrameSamplesOnly: true, FrameSampleIncludeAlpha: true);
            var baseline = await new NativeRenderRunner(oldTools).RenderAsync(request with {
                OutputDirectory = Path.Combine(output, label + "-old") }, cancellationToken: timeout.Token);
            var sparse = await new NativeRenderRunner(newTools).RenderAsync(request with {
                OutputDirectory = Path.Combine(output, label + "-sparse") }, cancellationToken: timeout.Token);
            ulong expected = (ulong)Enumerable.Range(0, 41).Count(frame => frame % 8 == 0 || phase == (ulong)(frame % 8));
            if (baseline["native_frame_transport"]?.GetValue<string>() != "full_rgba" ||
                sparse["native_frame_transport"]?.GetValue<string>() != "sampled_rgba" ||
                sparse["native_sample_backend"]?.GetValue<string>() != "gpu_box_mean" ||
                sparse["readback_frames"]?.GetValue<ulong>() != expected)
                throw new InvalidDataException("Sparse transport or sample count mismatch.");
            byte[] reference = await File.ReadAllBytesAsync(Path.Combine(output, label + "-old/frame-samples.rgb"));
            byte[] actual = await File.ReadAllBytesAsync(Path.Combine(output, label + "-sparse/frame-samples.rgb"));
            if (!reference.AsSpan().SequenceEqual(actual))
                throw new InvalidDataException("Sparse output changed deterministic samples: " + label);
            // A consumer that needs every frame must bypass sparse transport even on the new renderer.
            if (phase is null)
            {
                var full = await new NativeRenderRunner(newTools).RenderAsync(request with {
                    OutputDirectory = Path.Combine(output, label + "-full"), CollectAlphaBounds = true }, cancellationToken: timeout.Token);
                byte[] allFrames = await File.ReadAllBytesAsync(Path.Combine(output, label + "-full/frame-samples.rgb"));
                if (full["native_frame_transport"]?.GetValue<string>() != "full_rgba" ||
                    full["readback_frames"]?.GetValue<ulong>() != 41 || !reference.AsSpan().SequenceEqual(allFrames))
                    throw new InvalidDataException("Full-frame validation path changed.");
            }
            results.Add(new JsonObject { ["case"] = label, ["simulated_frames"] = 41,
                ["readback_frames"] = expected, ["pipe_rgba_bytes"] = sparse["pipe_rgba_bytes"]!.DeepClone(),
                ["native_sample_backend"] = sparse["native_sample_backend"]!.DeepClone(), ["samples_identical"] = true });
        }
        await File.WriteAllTextAsync(Path.Combine(output, "report.json"),
            new JsonObject { ["status"] = "passed", ["cases"] = results }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("Sparse readback: deterministic samples, warmup, phase and complete-input fallback passed.");
    }
}
