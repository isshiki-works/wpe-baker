using System.Buffers.Binary;
using System.Text.Json.Nodes;
using Baker.Core;

internal static class CanonicalRepeatNoiseChecks
{
    internal static void Run(Action<bool, string> check, string root)
    {
        string sourceDirectory = Path.Combine(root, "canonical-repeat-noise-source");
        void Write(string relative, string text)
        {
            string path = Path.Combine(sourceDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }
        const string vertex = """
            varying vec4 v_NoiseTexCoord;
            uniform float g_Time;
            uniform float g_NoiseSpeed;
            uniform float g_NoiseScale;
            void main() {
                v_NoiseTexCoord.xy = a_TexCoord + g_Time * g_NoiseSpeed;
                v_NoiseTexCoord.wz = vec2(a_TexCoord.y, -a_TexCoord.x) * 0.633 + vec2(-g_Time, g_Time) * 0.5 * g_NoiseSpeed;
                v_NoiseTexCoord *= g_NoiseScale;
            }
            """;
        const string fragment = """
            // [COMBO] {"combo":"NOISE","type":"options","default":1}
            varying vec4 v_NoiseTexCoord;
            uniform sampler2D g_Texture2; // {"default":"util/clouds_256"}
            void main() { float x = texSample2D(g_Texture2, v_NoiseTexCoord.xy).r * texSample2D(g_Texture2, v_NoiseTexCoord.zw).r; }
            """;
        Write("project.json", "{\"type\":\"scene\",\"file\":\"scene.json\"}");
        Write("shaders/noise.vert", vertex);
        Write("shaders/noise.frag", fragment);
        Write("shaders/changed.vert", vertex.Replace("g_Time * g_NoiseSpeed", "g_Time * g_NoiseSpeed * 2.0", StringComparison.Ordinal));
        Write("shaders/changed.frag", fragment);
        Write("shaders/clock.vert", vertex + "\nfloat extra = g_Runtime;");
        Write("shaders/clock.frag", fragment);
        Write("shaders/write.vert", vertex + "\nv_NoiseTexCoord.xy += vec2(0.0);");
        Write("shaders/write.frag", fragment);
        Write("materials/noise.json", "{\"passes\":[{\"shader\":\"noise\"}]}");
        Write("materials/changed.json", "{\"passes\":[{\"shader\":\"changed\"}]}");
        Write("materials/clock.json", "{\"passes\":[{\"shader\":\"clock\"}]}");
        Write("materials/write.json", "{\"passes\":[{\"shader\":\"write\"}]}");
        Write("materials/override.json", "{\"passes\":[{\"shader\":\"noise\",\"textures\":[null,null,\"custom/nonrepeat\"]}]}");
        foreach (string name in new[] { "noise", "changed", "clock", "override", "write" })
            Write($"effects/{name}.json", $"{{\"passes\":[{{\"material\":\"materials/{name}.json\"}}]}}");
        string cloudPath = Path.Combine(sourceDirectory, "materials", "util", "clouds_256.tex");
        Directory.CreateDirectory(Path.GetDirectoryName(cloudPath)!);
        byte[] cloudHeader = new byte[26];
        "TEXV0005"u8.CopyTo(cloudHeader); "TEXI0001"u8.CopyTo(cloudHeader.AsSpan(9));
        BinaryPrimitives.WriteUInt32LittleEndian(cloudHeader.AsSpan(22), 0);
        File.WriteAllBytes(cloudPath, cloudHeader);

        JsonObject Owner(int id, string effect) => new()
        {
            ["id"] = id,
            ["effects"] = new JsonArray(new JsonObject
            {
                ["file"] = $"effects/{effect}.json",
                ["passes"] = new JsonArray(new JsonObject
                {
                    ["constantshadervalues"] = new JsonObject { ["noisespeed"] = .25, ["noisescale"] = 4.0 }
                })
            })
        };
        var scene = new JsonObject { ["objects"] = new JsonArray(Owner(1, "noise"), Owner(2, "changed"), Owner(3, "override"), Owner(4, "clock"), Owner(5, "write")) };
        Write("scene.json", scene.ToJsonString());
        using var source = new ProjectSource(sourceDirectory);
        ShaderPeriodAnalysisResult analysis = ShaderPeriodAnalysis.Analyze(scene, source, null, [1, 2, 3, 4, 5]);
        ShaderPeriodComponent component = analysis.Components.Single();
        check(component.Patch.OwnerLayerId == 1 && component.Patch.ConstantKey == "noisespeed" &&
            Math.Abs(component.Component.BasePeriod!.Seconds - 2) < 1e-12,
            "canonical repeat noise yields one retimable noisespeed period");
        check(analysis.Unresolved.Single(x => x.OwnerLayerId == 2).Kind == ShaderTemporalUnresolvedKind.UnsupportedShaderMechanism &&
            analysis.Unresolved.Single(x => x.OwnerLayerId == 3).Kind == ShaderTemporalUnresolvedKind.UnsupportedShaderMechanism &&
            analysis.Unresolved.Single(x => x.OwnerLayerId == 4).Kind == ShaderTemporalUnresolvedKind.UnsupportedShaderMechanism &&
            analysis.Unresolved.Single(x => x.OwnerLayerId == 5).Kind == ShaderTemporalUnresolvedKind.UnsupportedShaderMechanism,
            "changed timing, overridden noise texture, an extra clock, and an extra UV write remain unresolved");
    }
}
