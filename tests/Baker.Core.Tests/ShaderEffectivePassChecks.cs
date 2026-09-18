using System.Text.Json.Nodes;
using System.Buffers.Binary;
using Baker.Core;

internal static class ShaderEffectivePassChecks
{
    internal static void Run(Action<bool, string> check, string root)
    {
        string sourceDirectory = Path.Combine(root, "shader-effective-pass-source");
        void Write(string relative, string text)
        {
            string path = Path.Combine(sourceDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }
        Write("project.json", "{\"type\":\"scene\",\"file\":\"scene.json\"}");
        Write("shaders/sine.frag", """
            // [COMBO] {"combo":"NOISE","type":"options","default":0}
            #if NOISE
            #endif
            uniform float g_Time; uniform float g_Speed; uniform float g_Phase; uniform float g_Amount;
            void main(){float x=sin(g_Time * g_Speed + g_Phase * 6.28318530718) * g_Amount;}
            """);
        Write("materials/noisy.json", "{\"passes\":[{\"shader\":\"sine\",\"combos\":{\"NOISE\":1}}]}");
        Write("materials/definition-only.json", "{\"passes\":[{\"shader\":\"sine\",\"combos\":{\"NOISE\":0},\"constantshadervalues\":{\"speed\":2.0}}]}");
        Write("effects/definition-wins.json", "{\"passes\":[{\"material\":\"materials/noisy.json\",\"combos\":{\"NOISE\":0}}]}");
        Write("effects/material-wins.json", "{\"passes\":[{\"material\":\"materials/noisy.json\"}]}");
        Write("effects/definition-only.json", "{\"passes\":[{\"material\":\"materials/definition-only.json\"}]}");
        JsonObject Effect(string file, JsonObject? pass) => new() {
            ["file"] = file,
            ["passes"] = pass is null ? null : new JsonArray(pass) };
        JsonObject Pass(int? noise)
        {
            var pass = new JsonObject { ["constantshadervalues"] = new JsonObject { ["speed"] = 2.0 } };
            if (noise is int value) pass["combos"] = new JsonObject { ["NOISE"] = value };
            return pass;
        }
        var scene = new JsonObject { ["objects"] = new JsonArray(
            new JsonObject { ["id"] = 1, ["effects"] = new JsonArray(Effect("effects/definition-wins.json", Pass(null))) },
            new JsonObject { ["id"] = 2, ["effects"] = new JsonArray(Effect("effects/material-wins.json", Pass(null))) },
            new JsonObject { ["id"] = 3, ["effects"] = new JsonArray(Effect("effects/definition-only.json", null)) },
            new JsonObject { ["id"] = 4, ["effects"] = new JsonArray(Effect("effects/definition-wins.json", Pass(1))) }) };
        Write("scene.json", scene.ToJsonString());
        using var source = new ProjectSource(sourceDirectory);
        ShaderPeriodAnalysisResult analysis = ShaderPeriodAnalysis.Analyze(scene, source, null, [1, 2, 3, 4]);
        check(analysis.Components.Count == 1 && analysis.Components.Single().Patch.OwnerLayerId == 1,
            "definition combos override material defaults for an authored patchable period");
        check(analysis.Unresolved.Single(x => x.OwnerLayerId == 2).Kind == ShaderTemporalUnresolvedKind.NonPeriodicOrDriftingMechanism &&
            analysis.Unresolved.Single(x => x.OwnerLayerId == 4).Kind == ShaderTemporalUnresolvedKind.NonPeriodicOrDriftingMechanism,
            "material combos are inherited and authored combos remain the final override");
        check(analysis.Unresolved.Single(x => x.OwnerLayerId == 3).Kind == ShaderTemporalUnresolvedKind.MissingOrInvalidSpeed,
            "a definition-only time pass is reported but an inherited speed is not exposed as an unpatchable component");

        Write("effects/shine.json", "{\"passes\":[{\"material\":\"materials/shine-down.json\"},{\"material\":\"materials/shine-cast.json\"},{\"material\":\"materials/shine-extra.json\"}]}");
        Write("materials/shine-down.json", "{\"passes\":[{\"shader\":\"shine_downsample2\"}]}");
        Write("materials/shine-cast.json", "{\"passes\":[{\"shader\":\"shine_cast\"}]}");
        Write("materials/shine-extra.json", "{\"passes\":[{\"shader\":\"shine_extra\"}]}");
        Write("shaders/shine_downsample2.vert", "varying vec4 v_NoiseTexCoord; uniform float g_Time; uniform float g_NoiseSpeed; uniform float g_NoiseScale; void main(){v_NoiseTexCoord.xy = a_TexCoord + g_Time * g_NoiseSpeed; v_NoiseTexCoord.wz = vec2(a_TexCoord.y, -a_TexCoord.x) * 0.633 + vec2(-g_Time, g_Time) * 0.5 * g_NoiseSpeed; v_NoiseTexCoord *= g_NoiseScale;}");
        Write("shaders/shine_downsample2.frag", "varying vec4 v_NoiseTexCoord; uniform sampler2D g_Texture2; // {\"default\":\"util/clouds_256\"} void main(){float x=texSample2D(g_Texture2, v_NoiseTexCoord.xy).r * texSample2D(g_Texture2, v_NoiseTexCoord.zw).r;}");
        Write("shaders/shine_cast.vert", "// [COMBO] {\"combo\":\"EDGES\",\"default\":4}\nuniform float g_Time; uniform float g_Speed; void main(){vec2 baseDirection = rotateVec2(vec2(0, 0.5), g_Time * g_Speed); #if EDGES == 3 vec2 x=rotateVec2(baseDirection, 0); #endif #if EDGES == 4 vec2 y=rotateVec2(vec2(-baseDirection.y, baseDirection.x), 0); #endif}");
        Write("shaders/shine_cast.frag", "void main(){}");
        Write("shaders/shine_extra.frag", "uniform float g_Time; void main(){ float unknown = g_Time; }");
        string cloudPath = Path.Combine(sourceDirectory, "materials", "util", "clouds_256.tex");
        Directory.CreateDirectory(Path.GetDirectoryName(cloudPath)!);
        byte[] cloudHeader = new byte[26];
        "TEXV0005"u8.CopyTo(cloudHeader); "TEXI0001"u8.CopyTo(cloudHeader.AsSpan(9));
        BinaryPrimitives.WriteUInt32LittleEndian(cloudHeader.AsSpan(22), 0);
        File.WriteAllBytes(cloudPath, cloudHeader);
        var shineScene = new JsonObject { ["objects"] = new JsonArray(new JsonObject {
            ["id"] = 5,
            ["effects"] = new JsonArray(new JsonObject {
                ["file"] = "effects/shine.json",
                ["passes"] = new JsonArray(
                    new JsonObject { ["constantshadervalues"] = new JsonObject { ["noisespeed"] = .2, ["noisescale"] = 5.0 } },
                    new JsonObject { ["combos"] = new JsonObject { ["EDGES"] = 3 }, ["constantshadervalues"] = new JsonObject { ["speed"] = 0.0 } },
                    new JsonObject()) }) }) };
        ShaderPeriodAnalysisResult inheritedTexture = ShaderPeriodAnalysis.Analyze(shineScene, source, null, [5]);
        check(inheritedTexture.Components.Count == 1 && inheritedTexture.Components.Single().Component.Id.EndsWith("noise-speed", StringComparison.Ordinal) &&
                Math.Abs(inheritedTexture.Components.Single().Component.BasePeriod!.Seconds - 2) < 1e-12 &&
                inheritedTexture.Unresolved.Any(item => item.PassIndex == 2 &&
                item.Kind == ShaderTemporalUnresolvedKind.UnsupportedShaderMechanism),
            "a source-verified static shine cast emits only the complete repeat-noise period and still rejects a later unmodeled time pass");
        JsonObject fourDirectionStaticScene = shineScene.DeepClone().AsObject();
        fourDirectionStaticScene["objects"]![0]!["effects"]![0]!["passes"]![1]!["combos"]!["EDGES"] = 4;
        ShaderPeriodAnalysisResult fourDirectionStatic = ShaderPeriodAnalysis.Analyze(fourDirectionStaticScene, source, null, [5]);
        check(fourDirectionStatic.Components.Count == 1 && fourDirectionStatic.Components.Single().Component.Id.EndsWith("noise-speed", StringComparison.Ordinal) &&
                Math.Abs(fourDirectionStatic.Components.Single().Component.BasePeriod!.Seconds - 2) < 1e-12 &&
                fourDirectionStatic.Unresolved.Any(item => item.PassIndex == 2 &&
                item.Kind == ShaderTemporalUnresolvedKind.UnsupportedShaderMechanism),
            "the same static shine cast rule accepts EDGES=4 without emitting a ray-speed period");
    }
}
