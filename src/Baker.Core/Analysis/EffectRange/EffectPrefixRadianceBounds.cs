using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Baker.Core.Analysis.EffectRange;

/// <summary>Reviewed shader transfer functions, composed over the authored pass/RT graph.
/// This requires shader ABI 21 (HLSL NaN-safe saturation and bounded smoothstep).
/// Fingerprints include BOTH stages and their annotations: changing a default or alias
/// changes the program identity. No resource name or wallpaper ID grants eligibility.</summary>
internal static class EffectPrefixRadianceBounds
{
    private const double MinNormalFloat = 1.1754943508222875e-38;
    private readonly record struct Range(double Lo, double Hi, bool Opaque)
    {
        public double Magnitude => Math.Max(Math.Abs(Lo), Math.Abs(Hi));
    }

    private static readonly Dictionary<string, string> Programs = new(StringComparer.Ordinal)
    {
        ["3d7b423731a1e72da3ec4de69a2aec60581b276182edc7052424dd152523d9df"] = "source",
        ["f1c57fdc5eb9bb92d633601812476aeea999148024a4718ed75a5c597a5f442d"] = "sharpen",
        ["5470f1c64bed9f6ec6d3f126635c5fdf9012835bf908adb2ef396cf58ebe95bb"] = "caustics",
        ["cd206712ea5633dcab46a2c4c8022790cf5accde5cb36ca64b3343c97be9e1c3"] = "ray-source",
        ["5cd4531fb59f855fd75fc107020b4032360987c44465a73ae6035051e7d1a514"] = "ray-cast",
        ["09fa0ef5e61f338b5ea24d357397cfa66e52e8ffacac3c75f3525286cc449909"] = "ray-blur",
        ["06338d7943314c6f97d5793b1d8f7214b176e76db53b598c53d7331aedb88ea3"] = "ray-combine",
        ["5a9ae0ab6c858b19f2a52862a171e74e45b5d9bbcf1677a95c1d3268eb87b85c"] = "resample",
        ["34082f5471de8390b1217f31cf4903b617e7e34527b6ca374c117c78feeddcfc"] = "light-map",
        ["3465eccb8ed077f256858f53c7a602c7c0e45d1575c35e3c1fba462191567b9b"] = "box-blur",
        ["16c601d6461d96aa190b151b80b8b5e0fad827c539714cd076d16b336543d5d9"] = "downsample",
        ["f762376f067628877d9c994e9a5bdf8d23a2cd0ca8b5232fb0b11481b2a787c7"] = "upsample",
        ["8fedd61a7c3034a926a35b803af232d7f18f65928b5ce3d08e2dc752bed9b99d"] = "bloom-add",
        ["1b52117cb0f17e96fdace598d3eae791ad085098f104f01821c5868709373b0c"] = "fog-multiply"
    };

    private static readonly Dictionary<string, string> Headers = new(StringComparer.Ordinal)
    {
        ["common.h"] = "e8799e15466827f8748723d00e6e9cab5823bc5e61eb4d41bc9259c7d65433c5",
        ["common_blending.h"] = "1f3b99356509292a2b332e93d608d43c4f806f18ce1801bf2180ac8cce5b093b",
        ["common_blur.h"] = "5a51e38193fa21caacf3452fbecaf48b3f75a2fcfe5004b234993cc54edefee5",
        ["common_vertex.h"] = "50b783226e2de410ea2ea95fad6306a02a85fe55f63afe5b1d862149428d20b3",
        ["common_perspective.h"] = "57a37d592cf98895475dc8b39427882ca00b3a1c4f360481421fa6356dae63b0",
        ["common_pbr.h"] = "6b9b0430769dc8d4ae61be37416fdb2453496a376fe0e240a2027ca91fbda893",
        ["common_pbr_2.h"] = "56c78a435c2da2f2eea3ded74ca6adb5e0b909bfd452427a1a39190dfbb5e322",
        ["common_fog.h"] = "9689d27bc5ce27d141fbd36c43a16de6e281f2459134b45b39d63679472022fd"
    };

    internal static bool TryProve(JsonObject layer, JsonObject properties, ProjectSource source,
        string? assets, out double lower, out double upper, out string detail)
    {
        lower = upper = 0;
        detail = "";
        try
        {
            var evidence = new List<string>();
            var checkedHeaders = new HashSet<string>(StringComparer.Ordinal);
            JsonObject Read(string path) => SceneAnalyzer.ReadResourceJson(source, assets, path);
            string Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) && s is not null
                ? s : throw new InvalidDataException("Unresolved resource reference.");
            double[] Numbers(JsonNode? raw, string fallback)
            {
                var value = SceneGraph.Resolve(raw ?? JsonValue.Create(fallback), properties);
                var result = new List<double>();
                if (value is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.Number &&
                    double.TryParse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) result.Add(d);
                else if (value is JsonValue t && t.TryGetValue<string>(out var s))
                    Need(SdrRadianceCriteria.TryParseComponents(s, result), "Invalid numeric value.");
                Need(result.Count > 0 && result.All(double.IsFinite), "Nonliteral/nonfinite shader value.");
                double[] floats = result.Select(n => (double)(float)n).ToArray();
                Need(floats.All(double.IsFinite), "Shader value exceeds FP32.");
                return floats;
            }
            void Includes(string text)
            {
                foreach (Match match in Regex.Matches(text, "#include\\s+\"([^\"]+)\""))
                {
                    string path = match.Groups[1].Value;
                    if (!checkedHeaders.Add(path)) continue;
                    Need(Headers.TryGetValue(path, out var hash) &&
                        ShaderPeriodAnalysis.TryReadShaderStage(source, assets, "shaders/" + path, out string header) &&
                        EffectRangeRules.Fingerprint(header) == hash, "Unreviewed include: " + path);
                    ShaderPeriodAnalysis.TryReadShaderStage(source, assets, "shaders/" + path, out string contents);
                    Includes(contents);
                }
            }
            string Program(JsonObject pass)
            {
                string name = Text(pass["shader"]);
                Need(ShaderPeriodAnalysis.TryReadShaderStage(source, assets, "shaders/" + name + ".frag", out string frag) &&
                    ShaderPeriodAnalysis.TryReadShaderStage(source, assets, "shaders/" + name + ".vert", out _), "Missing shader stage.");
                ShaderPeriodAnalysis.TryReadShaderStage(source, assets, "shaders/" + name + ".vert", out string vert);
                string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
                    Regex.Replace(frag + "\n" + vert, @"\s+", " ").Trim())));
                Need(Programs.ContainsKey(hash), "Unreviewed shader program: " + name);
                Includes(frag + "\n" + vert);
                return Programs[hash];
            }
            JsonObject Material(string path)
            {
                var passes = Read(path)["passes"] as JsonArray;
                Need(passes is { Count: 1 } && passes[0] is JsonObject, "Material must have one reviewed pass.");
                return passes![0]!.AsObject();
            }
            double maskLow = 0, maskHigh = 1;
            bool Texture(string? texture, bool opaque = false, bool mask = false)
            {
                if (texture is null) return !opaque && !mask;
                string resource = "materials/" + texture + ".tex";
                if (!TextureContainer.TryReadHeader(source, assets, resource, out var header, out _) || header.IsVideo ||
                    header.Width == 0 || header.Height == 0 || !(TextureContainer.IsEightBitUnsignedFormat(header.Format) || header.Format is 8 or 9)) return false;
                if (!opaque && !mask) return true;
                if (!source.Contains(resource) && (assets is null || new FileInfo(ProjectSource.ContainedPath(assets, resource)).Length > 32 * 1024 * 1024)) return false;
                byte[] bytes = source.Contains(resource) ? source.Read(resource) : File.ReadAllBytes(ProjectSource.ContainedPath(assets!, resource));
                return OpaqueTexture(bytes, mask, out maskLow, out maskHigh);
            }

            Need(!SceneAnalyzer.Walk(layer).OfType<JsonObject>().Any(n => n.ContainsKey("script") || n.ContainsKey("animation")),
                "Dynamic layer values have no range proof.");
            foreach (string field in new[] { "alpha", "brightness", "color" })
                Need(Numbers(layer[field] ?? layer["properties"]?[field], "1").All(v => v == 1), "Source modulation must be unit/opaque.");
            Need(Numbers(layer["colorBlendMode"] ?? layer["properties"]?["colorBlendMode"], "0").All(v => v == 0), "Source color blend mode is not normal.");
            Need(layer["clampuvs"]?.GetValue<bool>() == true, "Source sampling must clamp to the opaque image.");
            JsonObject general = source.ReadJson(source.SceneResource)["general"]?.AsObject() ?? new();
            Need(SceneGraph.Resolve(general["fogdistance"], properties)?.ToJsonString() is null or "false" &&
                SceneGraph.Resolve(general["fogheight"], properties)?.ToJsonString() is null or "false", "Scene fog changes the source range.");
            JsonObject model = Read(Text(layer["image"]));
            Need(!model.ContainsKey("mesh") && !model.ContainsKey("puppet"), "Source must be a flat image.");
            JsonObject basePass = Material(Text(model["material"]));
            Need(Program(basePass) == "source" && basePass["constantshadervalues"] is null &&
                (basePass["combos"] as JsonObject ?? []).Count == 0 &&
                Text(basePass["blending"]) is "normal" or "translucent", "Source program needs unmodified range-preserving defaults.");
            Need(basePass["textures"] is JsonArray { Count: 1 } textures && Texture(Text(textures[0]), opaque: true),
                "Source texture alpha is not proven one.");
            Range current = new(0, 1, true);

            foreach (var effect in (layer["effects"] as JsonArray ?? []).OfType<JsonObject>())
            {
                var visible = SceneGraph.Resolve(effect["visible"], properties)?.ToJsonString();
                if (visible == "false") continue;
                Need(visible is null or "true", "Dynamic effect visibility.");
                var definition = Read(Text(effect["file"]));
                var declared = new HashSet<string>(StringComparer.Ordinal);
                foreach (JsonObject fbo in (definition["fbos"] as JsonArray ?? []).OfType<JsonObject>())
                {
                    Need(Text(fbo["format"]) == "rgba_backbuffer", "Unreviewed RT format.");
                    Need(Numbers(fbo["scale"], "1").All(v => v > 0), "Invalid RT scale.");
                    declared.Add(Text(fbo["name"]));
                }
                var targets = new Dictionary<string, Range>(StringComparer.Ordinal) { ["previous"] = current };
                var passes = definition["passes"] as JsonArray ?? throw new InvalidDataException("Missing effect passes.");
                var authored = effect["passes"] as JsonArray ?? [];
                Need(passes.Count > 0 && authored.Count <= passes.Count, "Pass count mismatch.");
                for (int index = 0; index < passes.Count; ++index)
                {
                    JsonObject pass = passes[index]!.AsObject();
                    Need(!pass.ContainsKey("command"), "Effect command is outside the acyclic RT proof.");
                    Need(index + 1 == passes.Count || pass["target"] is not null, "Intermediate passes require explicit RT targets.");
                    JsonObject material = Material(Text(pass["material"]));
                    JsonObject instance = index < authored.Count ? authored[index]!.AsObject() : new();
                    Need(Text(material["blending"]) == "normal", "Effect pass must overwrite its RT.");
                    string kind = Program(material);
                    JsonNode? Value(string field, string name) => instance[field]?[name] ?? material[field]?[name];
                    double[] V(string name, string fallback, double min, double max)
                    {
                        var values = Numbers(Value("constantshadervalues", name), fallback);
                        Need(values.All(v => v >= min && v <= max), "Constant outside reviewed bounds: " + name);
                        if (name is "color" or "Tint" or "Fog color" or "ui_editor_properties_color_start" or "ui_editor_properties_color_end")
                            Need(values.Length == 3, "Expected RGB triplet: " + name);
                        return values;
                    }
                    double C(string name, double fallback, double min, double max)
                    {
                        var values = V(name, fallback.ToString("R", CultureInfo.InvariantCulture), min, max);
                        Need(values.Length == 1, "Expected scalar: " + name);
                        return values[0];
                    }
                    int Combo(string name, int fallback, params int[] allowed)
                    {
                        var values = Numbers(Value("combos", name), fallback.ToString(CultureInfo.InvariantCulture));
                        Need(values.Length == 1 && allowed.Contains((int)values[0]) && values[0] == (int)values[0], "Unreviewed combo: " + name);
                        return (int)values[0];
                    }
                    string? External(int slot, string? fallback = null)
                    {
                        JsonNode? At(JsonNode? array) => array is JsonArray a && slot < a.Count ? a[slot] : null;
                        var node = At(instance["textures"]) ?? At(material["textures"]);
                        string? texture = node is null ? fallback : Text(SceneGraph.Resolve(node, properties));
                        Need(Texture(texture), "External texture is not UNORM: " + texture);
                        return texture;
                    }
                    var inputs = new Dictionary<int, Range> { [0] = targets["previous"] };
                    foreach (JsonObject bind in (pass["bind"] as JsonArray ?? []).OfType<JsonObject>())
                    {
                        int slot = bind["index"]!.GetValue<int>();
                        Need(slot is >= 0 and <= 5 && targets.ContainsKey(Text(bind["name"])), "Unproven RT input.");
                        inputs[slot] = targets[Text(bind["name"])];
                    }
                    Range Input(int slot) => inputs.TryGetValue(slot, out var r) ? r : throw new InvalidDataException("Unbound range input.");
                    foreach (var pair in inputs)
                        foreach (JsonObject p in new[] { material, instance })
                            Need(p["textures"] is not JsonArray a || pair.Key >= a.Count || a[pair.Key] is null, "Texture overrides an RT input.");
                    // Unknown authored combo names can enable an unreviewed path even when a
                    // known combo retains its default. Unknown scalar overrides also fail.
                    var comboNames = new HashSet<string>(StringComparer.Ordinal);
                    var constantNames = new HashSet<string>(StringComparer.Ordinal);
                    void Names(string combos, string constants)
                    {
                        comboNames.UnionWith(combos.Split(' ', StringSplitOptions.RemoveEmptyEntries));
                        constantNames.UnionWith(constants.Split('|', StringSplitOptions.RemoveEmptyEntries));
                    }
                    Range a0 = Input(0), result;
                    bool nonnegative = false;
                    switch (kind)
                    {
                        case "sharpen":
                            Names("INVERT OPACITY", "Strength|Radius");
                            Combo("INVERT", 0, 0, 1); Combo("OPACITY", 0, 0, 1);
                            External(1, "util/white");
                            C("Radius", 1, -float.MaxValue, float.MaxValue);
                            double s = C("Strength", 1, -float.MaxValue, float.MaxValue);
                            // The signed kernel is bounded for every finite strength. The
                            // restriction here concerns alpha, not the editor's [0,5] slider:
                            // rounding (1+t)-t must stay inside the FP16 rounding cell of 1.
                            Need((1 + Math.Abs(s)) / (1 << 24) < 1.0 / (1 << 12), "Sharpen alpha-one FP16 rounding proof is unresolved at this strength.");
                            double center = 1 + .75 * s, side = -.75 * s;
                            double positive = Math.Max(center, 0) + Math.Max(side, 0), negative = -Math.Min(center, 0) - Math.Min(side, 0);
                            Need(a0.Opaque, "Sharpen alpha is not constant one.");
                            result = new(Math.Min(a0.Lo, positive * a0.Lo - negative * a0.Hi), Math.Max(a0.Hi, positive * a0.Hi - negative * a0.Lo), true);
                            break;
                        case "caustics":
                            Names("BLENDMODE MODE MASK PERSPECTIVE", "ui_editor_properties_blur|ui_editor_properties_brightness|ui_editor_properties_chromatic_aberration|ui_editor_properties_color_end|ui_editor_properties_color_start|ui_editor_properties_distortion|ui_editor_properties_glow|ui_editor_properties_granularity|ui_editor_properties_speed|ui_editor_properties_time_offset");
                            Combo("BLENDMODE", 32, 32); Combo("MODE", 0, 0); Combo("MASK", 0, 0, 1); Combo("PERSPECTIVE", 0, 0);
                            External(1); External(2, "pattern/voronoi_local"); External(3, "util/uniform_256"); External(4, "util/perlin_256"); External(5, "pattern/voronoi");
                            C("ui_editor_properties_blur", 0, 0, 1); C("ui_editor_properties_glow", .5, -float.MaxValue, float.MaxValue);
                            C("ui_editor_properties_distortion", 1, -float.MaxValue, float.MaxValue); C("ui_editor_properties_granularity", 2, -float.MaxValue, float.MaxValue);
                            C("ui_editor_properties_chromatic_aberration", 1, -float.MaxValue, float.MaxValue); C("ui_editor_properties_speed", 1, -float.MaxValue, float.MaxValue); C("ui_editor_properties_time_offset", 0, -float.MaxValue, float.MaxValue);
                            double color = V("ui_editor_properties_color_start", ".7 .9 1", 0, float.MaxValue).Concat(V("ui_editor_properties_color_end", ".4 .6 1", 0, float.MaxValue)).Max();
                            double gain = 1 + C("ui_editor_properties_brightness", 1, 0, float.MaxValue) * color;
                            result = new(Math.Min(a0.Lo, a0.Lo * gain), Math.Max(a0.Hi, a0.Hi * gain), a0.Opaque);
                            nonnegative = a0.Lo >= 0;
                            break;
                        case "ray-source":
                            Names("NOISE MASK", "noiseamount|noisescale|noisespeed|raythreshold");
                            Combo("NOISE", 1, 0, 1); Combo("MASK", 0, 0, 1); External(1); External(2, "util/clouds_256");
                            C("noiseamount", .4, 0, 1); C("noisescale", 1, -float.MaxValue, float.MaxValue); C("noisespeed", .1, -float.MaxValue, float.MaxValue); C("raythreshold", .5, -float.MaxValue, float.MaxValue);
                            Need(a0.Opaque, "Ray extraction requires opaque input.");
                            result = new(Math.Min(0, a0.Lo), Math.Max(0, a0.Hi), false); nonnegative = a0.Lo >= 0;
                            break;
                        case "ray-cast":
                            Names("EDGES SAMPLES", "color|direction|rayintensity|raylength|speed");
                            int edges = Combo("EDGES", 4, 2, 3, 4, 5); Combo("SAMPLES", 1, 0, 1, 2, 3, 4);
                            C("direction", 0, -float.MaxValue, float.MaxValue); C("speed", 0, -float.MaxValue, float.MaxValue); C("raylength", .1, -float.MaxValue, float.MaxValue);
                            double rays = 1.5 * edges * C("rayintensity", 1, 0, float.MaxValue) * V("color", "1 1 1", 0, float.MaxValue).Max();
                            result = new(Math.Min(0, a0.Lo * rays), Math.Max(0, a0.Hi * rays), false); nonnegative = a0.Lo >= 0;
                            break;
                        case "ray-blur":
                            Names("KERNEL VERTICAL", "scale"); Combo("KERNEL", 0, 0, 1, 2); Combo("VERTICAL", 0, 0, 1); V("scale", "1 1", -float.MaxValue, float.MaxValue);
                            result = a0; nonnegative = a0.Lo >= 0;
                            break;
                        case "ray-combine":
                            Names("BLENDMODE COPYBG", ""); Combo("BLENDMODE", 9, 9); Combo("COPYBG", 0, 0);
                            Range basis = Input(1);
                            Need(basis.Opaque, "Ray combine base alpha is not one.");
                            result = new(Math.Min(basis.Lo, basis.Lo + a0.Lo), Math.Max(basis.Hi, 1), true); nonnegative = result.Lo >= 0;
                            break;
                        case "resample":
                            Names("MASK TIMEOFFSET DUALWAVES PERSPECTIVE", "direction|exponent|scale|speed|strength");
                            Combo("MASK", 0, 0, 1); Combo("TIMEOFFSET", 0, 0, 1); Combo("DUALWAVES", 0, 0); Combo("PERSPECTIVE", 0, 0);
                            External(1); External(2, "util/black"); C("direction", 0, -float.MaxValue, float.MaxValue); C("exponent", 1, MinNormalFloat, float.MaxValue); C("scale", 200, -float.MaxValue, float.MaxValue); C("speed", 5, -float.MaxValue, float.MaxValue); C("strength", .1, -float.MaxValue, float.MaxValue);
                            result = a0; nonnegative = a0.Lo >= 0;
                            break;
                        case "light-map":
                            Names("MODE EMITTERMASK", "gamma|opacity|radius|strength|threshold"); Combo("MODE", 0, 0); Combo("EMITTERMASK", 0, 0, 1); External(1);
                            Need(a0.Opaque, "Light-map division requires constant positive alpha.");
                            C("gamma", 2.4, MinNormalFloat, float.MaxValue); C("threshold", .1, -float.MaxValue, float.MaxValue); C("opacity", 1, -float.MaxValue, float.MaxValue);
                            double light = C("strength", 1, 0, float.MaxValue) * C("radius", 4, 0, float.MaxValue) * .25;
                            result = new(0, light, true); nonnegative = true;
                            break;
                        case "box-blur":
                            Names("HIGH_QUALITY VERTICAL", "opacity|radius|strength"); Combo("HIGH_QUALITY", 0, 0, 1); Combo("VERTICAL", 0, 0, 1);
                            C("opacity", 1, -float.MaxValue, float.MaxValue); C("strength", 1, -float.MaxValue, float.MaxValue); C("radius", 4, -float.MaxValue, float.MaxValue);
                            result = new(Math.Min(0, a0.Lo), Math.Max(0, a0.Hi), true); nonnegative = a0.Lo >= 0;
                            break;
                        case "downsample":
                            Need(a0.Opaque, "Downsample weights require alpha one.");
                            result = a0; nonnegative = a0.Lo >= 0;
                            break;
                        case "upsample":
                            result = a0 with { Opaque = true }; nonnegative = a0.Lo >= 0;
                            break;
                        case "bloom-add":
                            Names("BLENDMODE OPACITY", "Tint|opacity"); Combo("BLENDMODE", 31, 31); Combo("OPACITY", 0, 0, 1); External(1);
                            double tint = V("Tint", "1 1 1", 0, float.MaxValue).Max(), opacity = C("opacity", 1, 0, float.MaxValue);
                            Range background = Input(2);
                            result = new(background.Lo + Math.Min(0, 2 * a0.Lo * tint * opacity), background.Hi + Math.Max(0, 2 * a0.Hi * tint * opacity), background.Opaque);
                            nonnegative = result.Lo >= 0;
                            break;
                        case "fog-multiply":
                            Names("BLENDMODE INVERT LIGHTING DEPTHLEVELS NOISE RETAINLIGHTS OPACITY DEPTH", "Brightness|Density|Fog color|Opacity|Depth levels multiplier|Depth levels exponent|Depth levels offset|Noise 1 offset|Noise 1 scale|Noise 2 offset|Noise 2 scale|Noise 1 magnitude|Noise 2 magnitude");
                            Combo("BLENDMODE", 0, 2); Combo("INVERT", 0, 0); Combo("LIGHTING", 0, 0); Combo("DEPTHLEVELS", 0, 0); Combo("NOISE", 0, 0); Combo("RETAINLIGHTS", 1, 0, 1); Combo("OPACITY", 0, 0, 1); Combo("DEPTH", 0, 0, 1);
                            Need(Texture(External(1, "util/white"), mask: true) && maskLow + .001 > .01, "Fog mask must prove the active branch over every mip.");
                            double opacityLow = maskLow + .001, opacityHigh = maskHigh + .001;
                            External(2, "util/white");
                            C("Density", 1, -float.MaxValue, float.MaxValue); double fogOpacity = C("Opacity", 1, -float.MaxValue, float.MaxValue);
                            double brightness = C("Brightness", 1, -float.MaxValue, float.MaxValue);
                            double[] colors = V("Fog color", "1 1 1", -float.MaxValue, float.MaxValue);
                            var extrema = new List<double>();
                            // f(a,d)=m(1-d)a² + (1-m+m*d*B)a. It is affine
                            // in d and B, so endpoints plus quadratic vertices suffice.
                            foreach (double m in new[] { opacityLow * fogOpacity, opacityHigh * fogOpacity })
                            foreach (double d in new[] { .001, 1.001 })
                                foreach (double b in new[] { colors.Min() * brightness, colors.Max() * brightness })
                                {
                                    double q = m * (1 - d), p = 1 - m + m * d * b;
                                    double F(double x) => q * x * x + p * x;
                                    extrema.Add(F(a0.Lo)); extrema.Add(F(a0.Hi));
                                    if (q != 0 && -p / (2 * q) is double x && x >= a0.Lo && x <= a0.Hi) extrema.Add(F(x));
                                }
                            result = new(extrema.Min(), extrema.Max(), a0.Opaque);
                            break;
                        default: throw new InvalidDataException("Source shader cannot be used as an effect transfer.");
                    }
                    foreach (JsonObject p in new[] { material, instance })
                    {
                        Need((p["combos"] as JsonObject ?? []).All(k => comboNames.Contains(k.Key)), "Unreviewed combo name.");
                        foreach (var item in p["constantshadervalues"] as JsonObject ?? [])
                        {
                            Need(constantNames.Contains(item.Key), "Unreviewed constant name: " + item.Key);
                            Numbers(item.Value, "0");
                        }
                    }
                    current = RoundRt(result, Math.Max(a0.Magnitude, inputs.Values.Max(r => r.Magnitude)), nonnegative);
                    evidence.Add($"{kind}[{index}]=[{current.Lo:R},{current.Hi:R}]; alpha={(current.Opaque ? "1" : "[0,1]")}");
                    if (pass["target"] is JsonNode target)
                    {
                        string name = Text(target);
                        Need(declared.Contains(name) && name != "previous" && index + 1 < passes.Count, "Unreviewed RT target.");
                        targets[name] = current;
                    }
                }
            }
            Need(current.Opaque, "Terminal alpha is not constant one.");
            lower = current.Lo; upper = current.Hi;
            detail = "Reviewed shader ABI 21 interval proof; outward FP16 RT rounding. " + string.Join("; ", evidence);
            return true;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or System.Text.Json.JsonException or InvalidOperationException or ArgumentException or OverflowException)
        {
            detail = error.Message;
            return false;
        }
    }

    private static void Need(bool condition, string reason)
    {
        if (!condition) throw new InvalidDataException(reason);
    }

    private static Range RoundRt(Range r, double inputMagnitude, bool nonnegative)
    {
        // At most 512 radiance arithmetic operations in these reviewed kernels
        // (the largest is 5 x 30 weighted ray samples). gamma_512 bounds FP32
        // roundoff; 8M bounds the absolute intermediate sums in the formulas.
        // Saturated transcendental results are bounded independently by [0,1].
        const double unit = 1.0 / (1 << 24), gamma = 512 * unit / (1 - 512 * unit);
        double error = gamma * 8 * Math.Max(1, Math.Max(inputMagnitude, r.Magnitude));
        double lo = (double)Half.BitDecrement((Half)(r.Lo - error));
        double hi = (double)Half.BitIncrement((Half)(r.Hi + error));
        if (nonnegative) lo = Math.Max(0, lo);
        Need(double.IsFinite(lo) && double.IsFinite(hi), "RT range exceeds finite FP16.");
        return new(lo, hi, r.Opaque);
    }

    private static bool OpaqueTexture(byte[] bytes, bool mask, out double redLow, out double redHigh)
    {
        redLow = 1; redHigh = 0;
        // Reuse the TEX header contract; inspect only simple uncompressed mip
        // payloads. PNG RGB/gray without tRNS and RGBA8 alpha=255 prove opacity.
        if (!TextureContainer.TryReadHeader(bytes, out var header) || header.Format != 0 || bytes.Length < 67) return false;
        using var stream = new MemoryStream(bytes, false);
        using var reader = new BinaryReader(stream);
        stream.Position = 46;
        string version = Encoding.ASCII.GetString(reader.ReadBytes(9));
        if (version is not ("TEXB0001\0" or "TEXB0002\0" or "TEXB0003\0" or "TEXB0004\0") || reader.ReadInt32() != 1) return false;
        int v = version[7] - '0';
        int imageType = v >= 3 ? reader.ReadInt32() : -1;
        if (v >= 4 && reader.ReadInt32() != 0) return false;
        int mips = reader.ReadInt32();
        if (mips is < 1 or > 32) return false;
        for (int mip = 0; mip < mips; ++mip)
        {
            int width = reader.ReadInt32(), height = reader.ReadInt32();
            if (width <= 0 || height <= 0) return false;
            if (v >= 2) { if (reader.ReadInt32() != 0) return false; reader.ReadInt32(); }
            int length = reader.ReadInt32();
            if (length < 0 || length > stream.Length - stream.Position) return false;
            byte[] data = reader.ReadBytes(length);
            if (imageType == -1)
            {
                if ((long)width * height * 4 != length) return false;
                for (int i = 0; i < data.Length; ++i)
                {
                    if (!mask && i % 4 == 3 && data[i] != 255) return false;
                    if (i % 4 == 0) { redLow = Math.Min(redLow, data[i] / 255.0); redHigh = Math.Max(redHigh, data[i] / 255.0); }
                }
            }
            else
            {
                if (mask || imageType != 13 || data.Length < 33 ||
                    !data.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
                    !data.AsSpan(12, 4).SequenceEqual("IHDR"u8) || data[25] is not (0 or 2)) return false;
                bool ended = false;
                for (int offset = 8; offset <= data.Length - 12;)
                {
                    uint size = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));
                    if (size > data.Length - offset - 12) return false;
                    var tag = data.AsSpan(offset + 4, 4);
                    if (tag.SequenceEqual("tRNS"u8)) return false;
                    if (tag.SequenceEqual("IEND"u8)) { ended = true; break; }
                    offset += checked((int)size + 12);
                }
                if (!ended) return false;
            }
        }
        return true;
    }
}
