using System.Globalization;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Baker.Core;

public enum ShaderTemporalUnresolvedKind
{
    ResourceUnavailable,
    UnsupportedShaderMechanism,
    NonPeriodicOrDriftingMechanism,
    MissingOrInvalidSpeed
}

/// <summary>
/// 一条未被方程规则解成周期的时间分量。<paramref name="BoundedDisplacement"/> 与 <paramref name="Mechanism"/>
/// 是机制知识的唯一来源：前者说这套方程的输出位移有没有解析幅度上界，后者给机制取一个可检索的名字
/// （uv_sway / uv_linear_drift / irrational_sine_hash / uv_normal_scroll …）。下游按这两个字段判残差能不能被接缝淡化掩盖，
/// 不再按资源名匹配字样。
/// </summary>
public sealed record ShaderTemporalUnresolved(int OwnerLayerId, int EffectIndex, int PassIndex,
    string Resource, ShaderTemporalUnresolvedKind Kind, string Detail,
    bool BoundedDisplacement = false, string Mechanism = "", SwayModel? Sway = null)
{
    /// <summary>Detail 由文案表生成时的键与参数；只有英文明细的为 null。</summary>
    public Message? Message { get; init; }

    public ShaderTemporalUnresolved(int ownerLayerId, int effectIndex, int passIndex, string resource,
        ShaderTemporalUnresolvedKind kind, Message message)
        : this(ownerLayerId, effectIndex, passIndex, resource, kind, message.Text) => Message = message;
}

/// <summary>Pinpoints one scalar in the authored scene effect pass; no patch is applied by this analysis.</summary>
public sealed record ShaderSpeedPatch(int OwnerLayerId, int EffectIndex, int PassIndex,
    string ConstantKey, int ValueIndex, double OldValue, double SpeedExponent = 1,
    string? CompanionConstantKey = null, int CompanionValueIndex = 0, double? CompanionOldValue = null,
    double CompanionSpeedExponent = -1);

public sealed record ShaderPeriodComponent(CommonLoopComponent Component, ShaderSpeedPatch Patch,
    string ShaderResource, string Evidence);

/// <summary>
/// <paramref name="RuledMaterials"/> 是已被方程规则裁定过的 (层, 材质 shader) 集合：凡进入过规则链并得出
/// 裁定的（产出分量、产出未解析项，或证明该 pass 没有运动）都在其中。键里必须带层 id——同名 shader 在别的
/// 层上是另一套常量，裁定不能跨层借用。shader 名与 runtime.json 里 materials[].shader 同一写法。
/// </summary>
public sealed record ShaderPeriodAnalysisResult(IReadOnlyList<ShaderPeriodComponent> Components,
    IReadOnlyList<ShaderTemporalUnresolved> Unresolved,
    IReadOnlySet<(int OwnerLayerId, string Shader)> RuledMaterials);

/// <summary>
/// Conservatively maps verified shader time equations to loop components. Shader names alone are
/// never evidence: the referenced source must contain the recognized equation.
/// </summary>
public static class ShaderPeriodAnalysis
{
    /// <summary>
    /// 机制名：填进 <see cref="ShaderTemporalUnresolved.Mechanism"/>，下游按它分派，不按资源名匹配字样。
    /// uv_sway 有解析幅度上界（八项正弦和乘以 strength²·0.005），uv_linear_drift 与 irrational_sine_hash 没有。
    /// </summary>
    public const string FoliageSwayMechanism = "uv_sway";
    public const string LightShaftDriftMechanism = "uv_linear_drift";
    public const string IrisSaccadeMechanism = "irrational_sine_hash";
    /// <summary>
    /// waterripple 的法线贴图滚动：位移是 normal.xy·strength²·mask，|normal.xy| ≤ 1，幅度有上界；
    /// 只在已算出精确周期、而周期超出循环上限时用这个名字记为未解析。
    /// </summary>
    public const string WaterRippleScrollMechanism = "uv_normal_scroll";

    /// <summary>lightshafts.frag:101-102 两次噪声查表的四条 UV 速率系数，实际速率是它们各自乘以 rayspeed（每秒）。</summary>
    public static readonly double[] LightShaftDriftRates = [0.003, 0.000375111, 0.0047111, 0.0007399];

    /// <summary>
    /// 四条速率同时回到整数纹理圈所需的 rayspeed·t：上面四个十进制常量的公共分母决定了它是 1e9 个单位，
    /// 因此联合周期 = 1e9 / rayspeed 秒。
    /// </summary>
    public const double LightShaftJointRepeatUnits = 1e9;


    /// <param name="loopCeilingSeconds">循环时长上限（秒，= --loop-length-max，缺省 600），只进拒绝文案，不改判定。</param>
    public static ShaderPeriodAnalysisResult Analyze(JsonObject scene, ProjectSource source, string? assetsDirectory,
        IReadOnlyCollection<int> selectedLayerIds, double? loopCeilingSeconds = null)
    {
        double ceiling = loopCeilingSeconds ?? CommonLoopSolver.DefaultMaximumSeconds;
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(selectedLayerIds);
        var selected = new HashSet<int>(selectedLayerIds);
        var components = new List<ShaderPeriodComponent>();
        var unresolved = new List<ShaderTemporalUnresolved>();
        var ruled = new HashSet<(int OwnerLayerId, string Shader)>();
        JsonArray objects = scene["objects"]?.AsArray() ?? throw new InvalidDataException("Scene has no object array.");
        // 摆动方程要父链 scale 才能把振幅换到输出像素；这里只做宽松索引，格式问题仍由下面的主循环报。
        var objectsById = new Dictionary<int, JsonObject>();
        foreach (JsonObject item in objects.OfType<JsonObject>())
            if (item["id"] is JsonValue idValue && idValue.TryGetValue<int>(out int itemId)) objectsById.TryAdd(itemId, item);
        foreach (JsonNode? node in objects)
        {
            JsonObject owner = node?.AsObject() ?? throw new InvalidDataException("Scene object must be an object.");
            int ownerId = owner["id"]?.GetValue<int>() ?? throw new InvalidDataException("Scene object lacks an id.");
            if (!selected.Contains(ownerId) || owner["effects"] is not JsonArray effects) continue;
            for (int effectIndex = 0; effectIndex < effects.Count; ++effectIndex)
            {
                JsonObject effect = effects[effectIndex]?.AsObject() ?? throw new InvalidDataException("Effect entry must be an object.");
                if (effect["visible"] is JsonValue visible && visible.TryGetValue<bool>(out bool isVisible) && !isVisible) continue;
                string effectResource = effect["file"]?.GetValue<string>() ?? "";
                if (string.IsNullOrWhiteSpace(effectResource))
                {
                    unresolved.Add(new(ownerId, effectIndex, -1, effectResource, ShaderTemporalUnresolvedKind.ResourceUnavailable,
                        "Effect definition has no resource path."));
                    continue;
                }
                JsonObject definition;
                try { definition = SceneAnalyzer.ReadResourceJson(source, assetsDirectory, effectResource); }
                catch (Exception error) when (error is IOException or InvalidDataException)
                {
                    unresolved.Add(new(ownerId, effectIndex, -1, effectResource, ShaderTemporalUnresolvedKind.ResourceUnavailable,
                        "Effect definition could not be read: " + error.Message));
                    continue;
                }
                JsonArray definitionPasses = definition["passes"]?.AsArray() ?? [];
                JsonArray authoredPasses = effect["passes"]?.AsArray() ?? [];
                bool shineHandled = TryHandleShine(ownerId, effectIndex, definitionPasses, authoredPasses, source, assetsDirectory, components, unresolved, ruled);
                int passIndex = 0;
                foreach (JsonObject definitionPass in definitionPasses.OfType<JsonObject>())
                {
                    string? materialResource = definitionPass["material"]?.GetValue<string>();
                    if (string.IsNullOrWhiteSpace(materialResource)) continue;
                    int authoredPassIndex = passIndex++;
                    // Shine consumes exactly its verified downsample/cast pair. Later passes remain independent effects.
                    if (shineHandled && authoredPassIndex < 2) continue;
                    JsonObject? authoredPass = authoredPassIndex < authoredPasses.Count
                        ? authoredPasses[authoredPassIndex]?.AsObject() ?? throw new InvalidDataException("Effect pass must be an object.")
                        : null;
                    if (!TryReadMaterialShader(source, assetsDirectory, materialResource, out string shader, out JsonObject materialPass, out string reason))
                    {
                        unresolved.Add(new(ownerId, effectIndex, authoredPassIndex, materialResource, ShaderTemporalUnresolvedKind.ResourceUnavailable, reason));
                        continue;
                    }
                    // 这一层的这个 shader 从这里起进入方程规则链，下面每条出口都是一次裁定（产出分量、产出
                    // 未解析项，或证明它没有运动）。登记在这里，运行时材质便不会再以「未建模时钟」重复计一次。
                    ruled.Add((ownerId, shader));
                    JsonObject effectivePass = EffectiveAnalysisPass(materialPass, definitionPass, authoredPass);
                    if (!TryReadShader(source, assetsDirectory, shader, out string shaderResource, out string shaderText, out reason))
                    {
                        unresolved.Add(new(ownerId, effectIndex, authoredPassIndex, shader, ShaderTemporalUnresolvedKind.ResourceUnavailable, reason));
                        continue;
                    }
                    bool wallClock = shaderText.Contains("g_Time", StringComparison.Ordinal);
                    bool alternateClock = shaderText.Contains("g_Runtime", StringComparison.Ordinal) ||
                        shaderText.Contains("g_Frametime", StringComparison.Ordinal) || shaderText.Contains("g_DeltaTime", StringComparison.Ordinal);
                    if (!wallClock)
                    {
                        if (alternateClock) unresolved.Add(new(ownerId, effectIndex, authoredPassIndex, shaderResource,
                            ShaderTemporalUnresolvedKind.UnsupportedShaderMechanism,
                            new Message("unresolved.shader_runtime_clock_unverified")));
                        continue;
                    }
                    if (alternateClock)
                    {
                        unresolved.Add(new(ownerId, effectIndex, authoredPassIndex, shaderResource,
                            ShaderTemporalUnresolvedKind.UnsupportedShaderMechanism,
                            new Message("unresolved.shader_mixed_clock")));
                        continue;
                    }
                    ComboRead dualWaves = ReadCombo(effectivePass, "DUALWAVES", out int dualWavesValue);
                    if (dualWaves == ComboRead.Unreadable)
                    {
                        unresolved.Add(new(ownerId, effectIndex, authoredPassIndex, shaderResource,
                            ShaderTemporalUnresolvedKind.UnsupportedShaderMechanism,
                            "DUALWAVES combo value is neither an integer nor a boolean, so the active wave branch is not proven."));
                        continue;
                    }
                    if (dualWaves == ComboRead.Value && dualWavesValue == 1)
                    {
                        AddDualWaterWave(ownerId, effectIndex, authoredPassIndex, effectivePass, shaderResource, shaderText, components, unresolved);
                        continue;
                    }
                    if (HasOnlyCanonicalWaterWaveClock(shaderText))
                    {
                        AddWaterWave(ownerId, effectIndex, authoredPassIndex, effectivePass, shaderResource, components, unresolved);
                        continue;
                    }
                    if (TryHandleCanonicalRepeatNoiseTranslation(ownerId, effectIndex, authoredPassIndex, effectivePass,
                        shaderResource, shaderText, source, assetsDirectory, components, unresolved))
                        continue;
                    if (TryHandleScroll(ownerId, effectIndex, authoredPassIndex, effectivePass, shaderResource, shaderText, components, unresolved))
                        continue;
                    if (TryHandleWaterFlow(ownerId, effectIndex, authoredPassIndex, effectivePass, shaderResource, shaderText, components, unresolved))
                        continue;
                    if (TryHandleLinearShimmer(ownerId, effectIndex, authoredPassIndex, effectivePass, shaderResource, shaderText, components, unresolved))
                        continue;
                    if (TryHandleShake(ownerId, effectIndex, authoredPassIndex, effectivePass, shaderResource, shaderText, components, unresolved))
                        continue;
                    if (TryHandleNoiseDisabledPulse(ownerId, effectIndex, authoredPassIndex, effectivePass, shaderResource, shaderText, components, unresolved))
                        continue;
                    if (TryHandleSine(ownerId, effectIndex, authoredPassIndex, effectivePass, shaderResource, shaderText, components, unresolved))
                        continue;
                    if (TryHandleFrameFractionGrain(ownerId, effectIndex, authoredPassIndex, shaderResource, shaderText, components))
                        continue;
                    if (TryHandleCrtScanArtifacts(ownerId, effectIndex, authoredPassIndex, effectivePass, shaderResource, shaderText, components, unresolved))
                        continue;
                    if (TryHandleLightShaftRays(ownerId, effectIndex, authoredPassIndex, effectivePass, shaderResource, shaderText, unresolved, ceiling))
                        continue;
                    if (TryHandleIrisSaccade(ownerId, effectIndex, authoredPassIndex, shaderResource, shaderText, unresolved))
                        continue;
                    if (TryHandleFoliageSway(ownerId, effectIndex, authoredPassIndex, effectivePass, shader, shaderResource, shaderText,
                        owner, objectsById, unresolved, ceiling))
                        continue;
                    if (TryHandleShadowMaskHash(ownerId, effectIndex, authoredPassIndex, effectivePass, shaderResource, shaderText, unresolved, ceiling))
                        continue;
                    if (HasOnlyCanonicalAutoSwayClock(shaderText))
                    {
                        AddAutoSway(ownerId, effectIndex, authoredPassIndex, effectivePass, shaderResource, shaderText, components, unresolved);
                        continue;
                    }
                    if (TryHandleWaterRipple(ownerId, effectIndex, authoredPassIndex, effectivePass, shaderResource, shaderText,
                        owner, source, assetsDirectory, components, unresolved, ceiling))
                        continue;
                    if (TryHandleStructuredSineClock(ownerId, effectIndex, authoredPassIndex, effectivePass, shaderResource, shaderText,
                        components, unresolved))
                        continue;
                    unresolved.Add(new(ownerId, effectIndex, authoredPassIndex, shaderResource, ShaderTemporalUnresolvedKind.UnsupportedShaderMechanism,
                        new Message("unresolved.shader_not_verified_periodic")));
                }
            }
        }
        return new(components, unresolved, ruled);
    }

    /// <summary>
    /// 一个作者效果引用到的材质 shader 名（与 runtime.json 里 materials[].shader 同一写法）。读不出的资源
    /// 直接跳过：这里只用来把运行时证据投影到被分析的效果集合上，读不出就当它不在集合里，不做任何裁定。
    /// </summary>
    public static IEnumerable<string> EffectMaterialShaders(ProjectSource source, string? assetsDirectory, JsonObject effect)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(effect);
        string resource = effect["file"] is JsonValue file && file.TryGetValue(out string? path) && path is not null ? path : "";
        if (string.IsNullOrWhiteSpace(resource)) yield break;
        JsonObject? definition = TryReadResource(source, assetsDirectory, resource);
        if (definition is null) yield break;
        foreach (JsonObject pass in (definition["passes"]?.AsArray() ?? []).OfType<JsonObject>())
        {
            string? material = pass["material"] is JsonValue value && value.TryGetValue(out string? name) ? name : null;
            if (string.IsNullOrWhiteSpace(material)) continue;
            if (TryReadMaterialShader(source, assetsDirectory, material, out string shader, out _, out _)) yield return shader;
        }
    }

    private static JsonObject? TryReadResource(ProjectSource source, string? assetsDirectory, string resource)
    {
        try { return SceneAnalyzer.ReadResourceJson(source, assetsDirectory, resource); }
        catch (Exception error) when (error is IOException or InvalidDataException) { return null; }
    }

    private static bool TryHandleShine(int ownerId, int effectIndex, JsonArray definitionPasses, JsonArray authoredPasses,
        ProjectSource source, string? assetsDirectory, List<ShaderPeriodComponent> components, List<ShaderTemporalUnresolved> unresolved,
        HashSet<(int OwnerLayerId, string Shader)> ruled)
    {
        if (definitionPasses.Count < 2) return false;
        JsonObject downsampleDefinition = definitionPasses[0]?.AsObject() ?? new JsonObject();
        JsonObject castDefinition = definitionPasses[1]?.AsObject() ?? new JsonObject();
        string? downsampleMaterial = downsampleDefinition["material"]?.GetValue<string>();
        string reason = "";
        if (string.IsNullOrWhiteSpace(downsampleMaterial) ||
            !TryReadMaterialShader(source, assetsDirectory, downsampleMaterial, out string downsampleShader, out JsonObject downsampleMaterialPass, out _)
            || !downsampleShader.EndsWith("shine_downsample2", StringComparison.Ordinal)) return false;
        string? castMaterial = castDefinition["material"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(castMaterial) ||
            !TryReadMaterialShader(source, assetsDirectory, castMaterial, out string castShader, out JsonObject castMaterialPass, out reason) ||
            !castShader.EndsWith("shine_cast", StringComparison.Ordinal) ||
            !TryReadShader(source, assetsDirectory, downsampleShader, out string noiseResource, out string noiseText, out reason) ||
            !TryReadShader(source, assetsDirectory, castShader, out string castResource, out string castText, out reason))
        {
            ruled.Add((ownerId, downsampleShader));
            unresolved.Add(new(ownerId, effectIndex, -1, downsampleMaterial, ShaderTemporalUnresolvedKind.ResourceUnavailable,
                "Shine pass resources could not be verified: " + reason));
            return true;
        }
        // shine 把前两个 pass 当一个机制整体裁定，主循环不会再单独走它们，所以两个 shader 都在这里登记。
        ruled.Add((ownerId, downsampleShader));
        ruled.Add((ownerId, castShader));
        JsonObject noisePass = EffectiveAnalysisPass(downsampleMaterialPass, downsampleDefinition,
            authoredPasses.ElementAtOrDefault(0)?.AsObject());
        JsonObject castPass = EffectiveAnalysisPass(castMaterialPass, castDefinition,
            authoredPasses.ElementAtOrDefault(1)?.AsObject());
        bool canonicalNoise = IsCanonicalRepeatNoiseTranslation(noiseText) && HasOnlyCanonicalRepeatNoiseClock(noiseText);
        // 读不出的 EDGES 值不能算成已证明的四方向变体，下面的 fourDirections 因此为 false。
        ComboRead edges = ReadCombo(castPass, "EDGES", out int edgesValue);
        bool fourDirections = castText.Contains("rotateVec2(vec2(0, 0.5), g_Time * g_Speed)", StringComparison.Ordinal) &&
            castText.Contains("#if EDGES == 4", StringComparison.Ordinal) && castText.Contains("rotateVec2(vec2(-baseDirection.y, baseDirection.x)", StringComparison.Ordinal) &&
            (edges == ComboRead.Absent || (edges == ComboRead.Value && edgesValue == 4)) &&
            ((edges == ComboRead.Value && edgesValue == 4) || castText.Contains("\"default\":4", StringComparison.Ordinal)) &&
            HasOnlyCanonicalShineCastClock(castText);
        bool hasCastSpeed = TryScalar(castPass, "speed", out double speed, out int speedIndex, out string speedToken);
        bool staticCast = hasCastSpeed && speed == 0 && HasOnlyCanonicalShineCastClock(castText);
        bool canonicalRepeatTexture = UsesDefaultRepeatCloudTexture(noisePass, source, assetsDirectory);
        if (!canonicalNoise || (!fourDirections && !staticCast) || !canonicalRepeatTexture)
        {
            unresolved.Add(new(ownerId, effectIndex, -1, noiseResource + " / " + castResource, ShaderTemporalUnresolvedKind.UnsupportedShaderMechanism,
                "Shine source, supported cast variant, or canonical repeat noise texture is not proven."));
            return true;
        }
        if ((!staticCast && (!hasCastSpeed || speed == 0)) ||
            !TryScalar(noisePass, "noisespeed", out double noiseSpeed, out int noiseSpeedIndex, out string noiseToken) || noiseSpeed == 0 ||
            !TryScalar(noisePass, "noisescale", out double noiseScale, out _, out string scaleToken) || noiseScale <= 0)
        {
            unresolved.Add(new(ownerId, effectIndex, -1, noiseResource, ShaderTemporalUnresolvedKind.MissingOrInvalidSpeed,
                "Verified shine equations need a supported cast speed or static cast, nonzero noisespeed, and positive noisescale constants."));
            return true;
        }
        string prefix = $"shader/{ownerId}/{effectIndex}";
        if (!staticCast)
            components.Add(new(new(prefix + "/ray-speed", new CommonLoopPeriod(Math.PI / (2 * Math.Abs(speed)), CommonLoopPeriodEvidence.Analytic), true),
                new(ownerId, effectIndex, 1, "speed", speedIndex, speed), castResource,
                $"Verified four-direction rotateVec2(g_Time * g_Speed) kernel; period π/(2*abs({speedToken}))."));
        components.Add(new(new(prefix + "/noise-speed", new CommonLoopPeriod(2 / (Math.Abs(noiseSpeed) * noiseScale), CommonLoopPeriodEvidence.Analytic), true),
            new(ownerId, effectIndex, 0, "noisespeed", noiseSpeedIndex, noiseSpeed), noiseResource,
            $"Verified full/half-speed canonical repeat UV sampling; period 2/(abs({noiseToken})*{scaleToken})." +
            (staticCast ? " Cast rotate clock is static (speed is zero)." : "")));
        return true;
    }

    private static bool TryHandleCanonicalRepeatNoiseTranslation(int ownerId, int effectIndex, int passIndex, JsonObject pass,
        string shaderResource, string shaderText, ProjectSource source, string? assetsDirectory,
        List<ShaderPeriodComponent> components, List<ShaderTemporalUnresolved> unresolved)
    {
        if (!IsCanonicalRepeatNoiseTranslation(shaderText) || !HasOnlyCanonicalRepeatNoiseClock(shaderText)) return false;
        // 读不出的 NOISE 值按"未证明启用"处理，下面立即落到 unresolved。
        bool noiseEnabled = ReadCombo(pass, "NOISE", out int noiseValue) switch
        {
            ComboRead.Value => noiseValue == 1,
            ComboRead.Absent => Regex.IsMatch(shaderText, @"""combo""\s*:\s*""NOISE""[^\r\n}]*""default""\s*:\s*1", RegexOptions.CultureInvariant),
            _ => false,
        };
        if (!noiseEnabled || !UsesDefaultRepeatCloudTexture(pass, source, assetsDirectory))
        {
            unresolved.Add(new(ownerId, effectIndex, passIndex, shaderResource, ShaderTemporalUnresolvedKind.UnsupportedShaderMechanism,
                "Canonical repeat noise requires the enabled default clouds_256 texture with verified repeat addressing."));
            return true;
        }
        if (!TryScalar(pass, "noisespeed", out double speed, out int valueIndex, out string speedToken) || speed == 0 ||
            !TryScalar(pass, "noisescale", out double scale, out _, out string scaleToken) || scale <= 0)
        {
            unresolved.Add(new(ownerId, effectIndex, passIndex, shaderResource, ShaderTemporalUnresolvedKind.MissingOrInvalidSpeed,
                "Canonical repeat noise needs nonzero noisespeed and positive noisescale constants."));
            return true;
        }
        components.Add(new(new($"shader/{ownerId}/{effectIndex}/{passIndex}/noise-speed",
            new CommonLoopPeriod(2 / (Math.Abs(speed) * scale), CommonLoopPeriodEvidence.Analytic), true),
            new(ownerId, effectIndex, passIndex, "noisespeed", valueIndex, speed), shaderResource,
            $"Verified full/half-speed canonical repeat UV sampling; period 2/(abs({speedToken})*{scaleToken})."));
        return true;
    }

    private static bool IsCanonicalRepeatNoiseTranslation(string shaderText) =>
        shaderText.Contains("v_NoiseTexCoord.xy = a_TexCoord + g_Time * g_NoiseSpeed", StringComparison.Ordinal) &&
        shaderText.Contains("vec2(-g_Time, g_Time) * 0.5 * g_NoiseSpeed", StringComparison.Ordinal) &&
        shaderText.Contains("v_NoiseTexCoord *= g_NoiseScale", StringComparison.Ordinal) &&
        shaderText.Contains("texSample2D(g_Texture2, v_NoiseTexCoord.xy)", StringComparison.Ordinal) &&
        shaderText.Contains("texSample2D(g_Texture2, v_NoiseTexCoord.zw)", StringComparison.Ordinal) &&
        shaderText.Contains("default\":\"util/clouds_256", StringComparison.Ordinal);

    private static bool HasOnlyCanonicalRepeatNoiseClock(string shaderText) =>
        HasCanonicalRepeatNoiseAssignments(shaderText) &&
        Regex.Matches(shaderText, @"\bg_Time\b", RegexOptions.CultureInvariant).Count == 4 &&
        Regex.Matches(shaderText, @"\bg_NoiseSpeed\b", RegexOptions.CultureInvariant).Count == 3 &&
        Regex.Matches(shaderText, @"\bg_NoiseScale\b", RegexOptions.CultureInvariant).Count == 2 &&
        Regex.Matches(shaderText, @"\bv_NoiseTexCoord\b", RegexOptions.CultureInvariant).Count == 7 &&
        Regex.Matches(shaderText, @"\bv_NoiseTexCoord(?:\.(?:xy|wz|zw|x|y|z|w))?\s*(?:\+=|-=|\*=|/=|=)", RegexOptions.CultureInvariant).Count == 3 &&
        !Regex.IsMatch(shaderText, @"\b(?:g_Frametime|g_Runtime|g_DeltaTime)\b", RegexOptions.CultureInvariant);

    private static bool HasCanonicalRepeatNoiseAssignments(string shaderText) =>
        shaderText.Contains("v_NoiseTexCoord.xy = a_TexCoord + g_Time * g_NoiseSpeed;", StringComparison.Ordinal) &&
        shaderText.Contains("v_NoiseTexCoord.wz = vec2(a_TexCoord.y, -a_TexCoord.x) * 0.633 + vec2(-g_Time, g_Time) * 0.5 * g_NoiseSpeed;", StringComparison.Ordinal) &&
        shaderText.Contains("v_NoiseTexCoord *= g_NoiseScale;", StringComparison.Ordinal);

    private static bool HasOnlyCanonicalShineCastClock(string shaderText)
    {
        string source = NormalizeShaderSource(shaderText);
        return source.Contains("vec2 baseDirection = rotateVec2(vec2(0, 0.5), g_Time * g_Speed);", StringComparison.Ordinal) &&
            CountWord(source, "g_Time") == 2 && CountWord(source, "g_Speed") == 2 &&
            CountDirectAssignments(source, "baseDirection") == 1 && !HasAlternateClock(source);
    }

    // These are source-family fingerprints, not shader-name heuristics.  In particular, all
    // g_Time occurrences are accounted for so a second clock use cannot piggyback on a wave.
    private static bool HasOnlyCanonicalWaterWaveClock(string shaderText)
    {
        string source = NormalizeShaderSource(shaderText);
        bool singleClockEquation = source.Contains("float distance = g_Time * g_Speed;", StringComparison.Ordinal) &&
            source.Contains("sin(distance);", StringComparison.Ordinal) &&
            CountWord(source, "g_Time") == 2 && CountWord(source, "g_Speed") == 2 &&
            CountWord(source, "distance") == 2 && CountAssignments(source, "distance") == 1;
        bool stockWaterWave = source.Contains("float distance = g_Time * g_Speed + dot(texCoordMotion, v_Direction) * g_Scale;", StringComparison.Ordinal) &&
            source.Contains("float val1 = sin(distance);", StringComparison.Ordinal) &&
            source.Contains("distance *= step(0.0, v_TexCoordPerspective.z);", StringComparison.Ordinal) &&
            source.Contains("distance += timeOffset;", StringComparison.Ordinal) &&
            CountWord(source, "g_Time") == 4 && CountWord(source, "g_Speed") == 2 &&
            CountWord(source, "distance") == 4 && CountAssignments(source, "distance") == 3;
        return (singleClockEquation || stockWaterWave) && !HasAlternateClock(source);
    }

    private static bool HasOnlyCanonicalAutoSwayClock(string shaderText)
    {
        string source = NormalizeShaderSource(shaderText);
        bool singleClockEquation = source.Contains("float thisMotionTime = g_Time * g_Speed;", StringComparison.Ordinal) &&
            source.Contains("sin(thisMotionTime * M_PI_2);", StringComparison.Ordinal) &&
            CountWord(source, "g_Time") == 2 && CountWord(source, "g_Speed") == 2 &&
            CountWord(source, "thisMotionTime") == 2 && CountAssignments(source, "thisMotionTime") == 1;
        // The stock auto-sway source also retains its mutually-exclusive AA_VERSION 1 formula
        // in the fragment stage. The static source therefore contains three closed clock uses,
        // two thisMotionTime initializations, and 24 static phase additions.
        bool stockAutoSway = CountSubstring(source, "float thisMotionTime = g_GlobalTimeOffset + g_Time * g_Speed;") == 2 &&
            CountSubstring(source, "thisMotionRadian = sin(thisMotionTime * M_PI_2);") == 2 &&
            CountWord(source, "g_Time") == 5 && CountWord(source, "g_Speed") == 5 &&
            CountWord(source, "thisMotionTime") == 32 && CountAssignments(source, "thisMotionTime") == 26 &&
            CountDirectAssignments(source, "thisMotionTime") == 2 && CountScaledOrSubtractedAssignments(source, "thisMotionTime") == 0;
        return (singleClockEquation || stockAutoSway) && !HasAlternateClock(source);
    }

    private static string NormalizeShaderSource(string shaderText) => Regex.Replace(
        Regex.Replace(shaderText, @"//[^\r\n]*|/\*.*?\*/", "", RegexOptions.CultureInvariant | RegexOptions.Singleline),
        @"\s+", " ", RegexOptions.CultureInvariant).Trim();

    private static int CountWord(string source, string identifier) =>
        Regex.Matches(source, $@"\b{Regex.Escape(identifier)}\b", RegexOptions.CultureInvariant).Count;

    private static int CountAssignments(string source, string identifier) =>
        Regex.Matches(source, $@"\b{Regex.Escape(identifier)}\s*(?:\+=|-=|\*=|/=|=(?!=))", RegexOptions.CultureInvariant).Count;

    private static int CountDirectAssignments(string source, string identifier) =>
        Regex.Matches(source, $@"\b{Regex.Escape(identifier)}\s*=(?!=)", RegexOptions.CultureInvariant).Count;

    private static int CountScaledOrSubtractedAssignments(string source, string identifier) =>
        Regex.Matches(source, $@"\b{Regex.Escape(identifier)}\s*(?:-=|\*=|/=)", RegexOptions.CultureInvariant).Count;

    private static int CountSubstring(string source, string value)
    {
        int count = 0;
        for (int index = 0; (index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0; index += value.Length) ++count;
        return count;
    }

    private static bool HasAlternateClock(string source) => Regex.IsMatch(source,
        @"\b(?:g_Frametime|g_Runtime|g_DeltaTime)\b", RegexOptions.CultureInvariant);

    private static bool UsesDefaultRepeatCloudTexture(JsonObject pass, ProjectSource source, string? assetsDirectory) =>
        (pass["textures"] is not JsonArray textures || textures.Count < 3 || textures[2] is null) &&
        HasRepeatCloudTexture(source, assetsDirectory);

    private static void AddWaterWave(int ownerId, int effectIndex, int passIndex, JsonObject pass, string shaderResource,
        List<ShaderPeriodComponent> components, List<ShaderTemporalUnresolved> unresolved)
    {
        if (!TryScalar(pass, "speed", out double speed, out int valueIndex, out string numericText) || speed == 0)
        {
            unresolved.Add(new(ownerId, effectIndex, passIndex, shaderResource, ShaderTemporalUnresolvedKind.MissingOrInvalidSpeed,
                "Verified water-wave timing needs a nonzero scalar 'speed' constant."));
            return;
        }
        var patch = new ShaderSpeedPatch(ownerId, effectIndex, passIndex, "speed", valueIndex, speed);
        string id = $"shader/{ownerId}/{effectIndex}/{passIndex}/speed";
        var period = new CommonLoopPeriod(2 * Math.PI / Math.Abs(speed), CommonLoopPeriodEvidence.Analytic);
        components.Add(new(new(id, period, AllowRetime: true), patch, shaderResource,
            $"Verified sin(g_Time * g_Speed + spatial phase) in {shaderResource}; source speed token is {numericText}."));
    }

    private static void AddDualWaterWave(int ownerId, int effectIndex, int passIndex, JsonObject pass, string shaderResource,
        string shaderText, List<ShaderPeriodComponent> components, List<ShaderTemporalUnresolved> unresolved)
    {
        const string Distance1 = "float distance = g_Time * g_Speed + dot(texCoordMotion, v_Direction) * g_Scale;";
        const string Distance2 = "float distance2 = (g_Time + g_Offset2) * g_Speed2 + dot(texCoordMotion, v_Direction2) * g_Scale2;";
        const string Product = "texCoord += val1 * s1 * val2 * s2 * offset * strength * mask;";
        if (!shaderText.Contains(Distance1, StringComparison.Ordinal) ||
            !shaderText.Contains(Distance2, StringComparison.Ordinal) ||
            !shaderText.Contains("float val1 = sin(distance);", StringComparison.Ordinal) ||
            !shaderText.Contains("float val2 = sin(distance2);", StringComparison.Ordinal) ||
            !shaderText.Contains(Product, StringComparison.Ordinal) ||
            !shaderText.Contains("uniform float g_Speed2; // {\"material\":\"speed2\"", StringComparison.Ordinal) ||
            !shaderText.Contains("uniform float g_Offset2; // {\"material\":\"offset2\"", StringComparison.Ordinal) ||
            Regex.Matches(shaderText, @"\bg_Time\b", RegexOptions.CultureInvariant).Count != 4 ||
            Regex.Matches(shaderText, @"\bg_Speed\b", RegexOptions.CultureInvariant).Count != 2 ||
            Regex.Matches(shaderText, @"\bg_Speed2\b", RegexOptions.CultureInvariant).Count != 2 ||
            Regex.Matches(shaderText, @"\bg_Offset2\b", RegexOptions.CultureInvariant).Count != 2 ||
            Regex.IsMatch(shaderText, @"\b(?:g_Frametime|g_Runtime|g_DeltaTime)\b", RegexOptions.CultureInvariant))
        {
            unresolved.Add(new(ownerId, effectIndex, passIndex, shaderResource, ShaderTemporalUnresolvedKind.UnsupportedShaderMechanism,
                "Dual water-wave source has an unverified equation or additional clock input."));
            return;
        }
        if (!TryScalar(pass, "speed", out double speed1, out int speedIndex1, out string token1) || speed1 == 0 ||
            !TryScalar(pass, "speed2", out double speed2, out int speedIndex2, out string token2) || speed2 == 0 ||
            !TryScalar(pass, "offset2", out double offset2, out int offsetIndex, out _) ||
            !TrySimpleRational(Math.Abs(speed1), out CommonLoopRational rational1) ||
            !TrySimpleRational(Math.Abs(speed2), out CommonLoopRational rational2) ||
            !TryRationalGreatestCommonDivisor(rational1, rational2, out CommonLoopRational jointSpeed))
        {
            unresolved.Add(new(ownerId, effectIndex, passIndex, shaderResource, ShaderTemporalUnresolvedKind.MissingOrInvalidSpeed,
                "Dual water waves need nonzero simple-rational speeds and a finite authored offset2 to establish one joint period."));
            return;
        }
        string prefix = $"shader/{ownerId}/{effectIndex}/{passIndex}";
        var period = new CommonLoopPeriod(2 * Math.PI / jointSpeed.ToSeconds(), CommonLoopPeriodEvidence.Analytic);
        components.Add(new(new(prefix + "/speed", period, AllowRetime: true),
            new(ownerId, effectIndex, passIndex, "speed", speedIndex1, speed1), shaderResource,
            $"Verified dual sine product with simple-rational speeds {token1} and {token2}; joint period is 2π/gcd(abs(speed),abs(speed2))."));
        components.Add(new(new(prefix + "/speed2", period, AllowRetime: true),
            new(ownerId, effectIndex, passIndex, "speed2", speedIndex2, speed2,
                CompanionConstantKey: offset2 == 0 ? null : "offset2", CompanionValueIndex: offsetIndex,
                CompanionOldValue: offset2 == 0 ? null : offset2), shaderResource,
            "The second speed shares the joint-period multiplier; a nonzero offset2 receives the inverse multiplier to preserve its initial phase."));
    }

    private static bool TryHandleScroll(int ownerId, int effectIndex, int passIndex, JsonObject pass, string shaderResource,
        string shaderText, List<ShaderPeriodComponent> components, List<ShaderTemporalUnresolved> unresolved)
    {
        const string SquaredSpeed = "scroll = sign(scroll) * pow(vec2(g_ScrollX, g_ScrollY), CAST2(2.0));";
        const string RepeatedUv = "frac((v_TexCoord + v_Scroll) * g_Scale)";
        if (!shaderText.Contains(SquaredSpeed, StringComparison.Ordinal) || !shaderText.Contains(RepeatedUv, StringComparison.Ordinal)) return false;
        if (!TryVec2(pass, "repeat", out double repeatX, out double repeatY) || repeatX <= 0 || repeatY <= 0)
        {
            unresolved.Add(new(ownerId, effectIndex, passIndex, shaderResource, ShaderTemporalUnresolvedKind.MissingOrInvalidSpeed,
                "Verified scroll timing needs a static positive two-axis 'repeat' constant."));
            return true;
        }
        AddScrollAxis("x", "speedx", repeatX);
        AddScrollAxis("y", "speedy", repeatY);
        return true;

        void AddScrollAxis(string axis, string key, double repeat)
        {
            if (!TryScalar(pass, key, out double speed, out int valueIndex, out string numericText))
            {
                unresolved.Add(new(ownerId, effectIndex, passIndex, shaderResource, ShaderTemporalUnresolvedKind.MissingOrInvalidSpeed,
                    $"Verified scroll timing needs a scalar '{key}' constant."));
                return;
            }
            if (speed == 0) return;
            var period = new CommonLoopPeriod(1 / (speed * speed * repeat), CommonLoopPeriodEvidence.Analytic);
            components.Add(new(new($"shader/{ownerId}/{effectIndex}/{passIndex}/scroll-{axis}", period, AllowRetime: true),
                new(ownerId, effectIndex, passIndex, key, valueIndex, speed, SpeedExponent: 2), shaderResource,
                $"Verified frac((uv + sign(speed)*speed²*time)*repeat) scroll axis {axis}; speed token is {numericText}."));
        }
    }

    private static bool TryHandleSine(int ownerId, int effectIndex, int passIndex, JsonObject pass, string shaderResource,
        string shaderText, List<ShaderPeriodComponent> components, List<ShaderTemporalUnresolved> unresolved)
    {
        const string SineTime = "sin(g_Time * g_Speed + g_Phase * 6.28318530718) * g_Amount";
        if (!shaderText.Contains(SineTime, StringComparison.Ordinal)) return false;
        const string NoiseDisabledDefault = "\"combo\":\"NOISE\",\"type\":\"options\",\"default\":0";
        if (shaderText.Contains("#if NOISE", StringComparison.Ordinal) &&
            ComboEnabledOrUnproven(pass, "NOISE", !shaderText.Contains(NoiseDisabledDefault, StringComparison.Ordinal)))
        {
            unresolved.Add(new(ownerId, effectIndex, passIndex, shaderResource, ShaderTemporalUnresolvedKind.NonPeriodicOrDriftingMechanism,
                "Verified sine timing has enabled or unproven NOISE, so its simple speed period does not prove closure."));
            return true;
        }
        if (!TryScalar(pass, "speed", out double speed, out int valueIndex, out string numericText))
        {
            unresolved.Add(new(ownerId, effectIndex, passIndex, shaderResource, ShaderTemporalUnresolvedKind.MissingOrInvalidSpeed,
                "Verified sine timing needs a scalar 'speed' constant."));
            return true;
        }
        if (speed == 0) return true;
        components.Add(new(new($"shader/{ownerId}/{effectIndex}/{passIndex}/speed", new CommonLoopPeriod(2 * Math.PI / Math.Abs(speed), CommonLoopPeriodEvidence.Analytic), AllowRetime: true),
            new(ownerId, effectIndex, passIndex, "speed", valueIndex, speed), shaderResource,
            $"Verified sin(g_Time * g_Speed + g_Phase * 2π) timing; speed token is {numericText}."));
        return true;
    }

    private static bool TryHandleWaterFlow(int ownerId, int effectIndex, int passIndex, JsonObject pass, string shaderResource,
        string shaderText, List<ShaderPeriodComponent> components, List<ShaderTemporalUnresolved> unresolved)
    {
        const string SpeedUniform = "uniform float g_FlowSpeed; // {\"material\":\"speed\"";
        const string Cycle0 = "vec4 cycles = vec4(frac(g_Time * g_FlowSpeed),";
        const string Cycle1 = "frac(g_Time * g_FlowSpeed + 0.5),";
        const string Cycle2 = "frac(0.25 + g_Time * g_FlowSpeed),";
        const string Cycle3 = "frac(0.25 + g_Time * g_FlowSpeed + 0.5));";
        const string StaticPhase = "float flowPhase = texSample2D(g_Texture2, v_TexCoord.xy * g_FlowPhaseScale).r;";
        if (!shaderText.Contains(SpeedUniform, StringComparison.Ordinal) ||
            !shaderText.Contains(Cycle0, StringComparison.Ordinal) ||
            !shaderText.Contains(Cycle1, StringComparison.Ordinal) ||
            !shaderText.Contains(Cycle2, StringComparison.Ordinal) ||
            !shaderText.Contains(Cycle3, StringComparison.Ordinal) ||
            !shaderText.Contains(StaticPhase, StringComparison.Ordinal) ||
            Regex.Matches(shaderText, @"\bg_Time\b", RegexOptions.CultureInvariant).Count != 5 ||
            Regex.Matches(shaderText, @"\bg_FlowSpeed\b", RegexOptions.CultureInvariant).Count != 5 ||
            Regex.Matches(shaderText, @"\bflowPhase\b", RegexOptions.CultureInvariant).Count != 2 ||
            Regex.IsMatch(shaderText, @"(?m)^\s*#(?:if|ifdef|ifndef|elif)\b", RegexOptions.CultureInvariant) ||
            Regex.IsMatch(shaderText, @"\b(?:g_Frametime|g_Runtime|g_DeltaTime)\b", RegexOptions.CultureInvariant)) return false;
        if (!TryScalar(pass, "speed", out double speed, out int valueIndex, out string numericText))
        {
            unresolved.Add(new(ownerId, effectIndex, passIndex, shaderResource, ShaderTemporalUnresolvedKind.MissingOrInvalidSpeed,
                "Verified four-phase water flow timing needs a finite scalar 'speed' constant."));
            return true;
        }
        if (speed == 0) return true;
        components.Add(new(new($"shader/{ownerId}/{effectIndex}/{passIndex}/speed",
            new CommonLoopPeriod(1 / Math.Abs(speed), CommonLoopPeriodEvidence.Analytic), AllowRetime: true),
            new(ownerId, effectIndex, passIndex, "speed", valueIndex, speed), shaderResource,
            $"Verified four frac(g_Time * g_FlowSpeed) phases return together after 1/abs(speed); source speed token is {numericText}. Auxiliary texture timing remains an independent constraint."));
        return true;
    }

    private static bool TryHandleLinearShimmer(int ownerId, int effectIndex, int passIndex, JsonObject pass, string shaderResource,
        string shaderText, List<ShaderPeriodComponent> components, List<ShaderTemporalUnresolved> unresolved)
    {
        const string SpeedUniform = "uniform float u_speed; // {\"material\":\"ui_editor_properties_speed\",\"default\":1,\"range\":[0,5]}";
        const string ScaleUniform = "uniform float u_scale; // {\"material\":\"ui_editor_properties_granularity\",\"default\":1,\"range\":[1,5]}";
        const string DelayUniform = "uniform float u_delay; // {\"material\":\"ui_editor_properties_delay\",\"default\":2,\"range\":[1,5]}";
        const string LinearTiming = "shimmerCoord.x += u_offset + u_speed * (g_Time + offset);";
        const string MirrorTiming = "shimmerCoord.x += u_offset + u_width * sin(u_speed * g_Time + offset);";
        const string WrappedCoordinate = "shimmerCoord.x = saturate(frac(shimmerCoord.x / (u_scale * u_delay)) * u_scale * u_delay);";
        const string DefaultMode = "// [COMBO] {\"material\":\"ui_editor_properties_style\",\"combo\":\"MODE\",\"default\":0,";
        if (!shaderText.Contains(SpeedUniform, StringComparison.Ordinal) ||
            !shaderText.Contains(ScaleUniform, StringComparison.Ordinal) ||
            !shaderText.Contains(DelayUniform, StringComparison.Ordinal) ||
            !shaderText.Contains("#if MODE == 1", StringComparison.Ordinal) ||
            !shaderText.Contains(LinearTiming, StringComparison.Ordinal) ||
            !shaderText.Contains(MirrorTiming, StringComparison.Ordinal) ||
            !shaderText.Contains(WrappedCoordinate, StringComparison.Ordinal) ||
            Regex.Matches(shaderText, @"\bg_Time\b", RegexOptions.CultureInvariant).Count != 3 ||
            Regex.Matches(shaderText, @"\bu_speed\b", RegexOptions.CultureInvariant).Count != 3 ||
            Regex.Matches(shaderText, @"\bu_scale\b", RegexOptions.CultureInvariant).Count != 4 ||
            Regex.Matches(shaderText, @"\bu_delay\b", RegexOptions.CultureInvariant).Count != 3 ||
            Regex.IsMatch(shaderText, @"\b(?:g_Frametime|g_Runtime|g_DeltaTime)\b", RegexOptions.CultureInvariant)) return false;

        // 读不出的 MODE 值不能算成已证明的线性默认分支。
        bool modeZero = ReadCombo(pass, "MODE", out int shimmerMode) switch
        {
            ComboRead.Value => shimmerMode == 0,
            ComboRead.Absent => shaderText.Contains(DefaultMode, StringComparison.Ordinal),
            _ => false,
        };
        if (!modeZero)
        {
            unresolved.Add(new(ownerId, effectIndex, passIndex, shaderResource, ShaderTemporalUnresolvedKind.UnsupportedShaderMechanism,
                "Shimmer MODE is not the verified linear default branch."));
            return true;
        }
        if (!TryScalar(pass, "ui_editor_properties_speed", out double speed, out int speedIndex, out string speedToken) || speed == 0 ||
            !TryScalar(pass, "ui_editor_properties_granularity", out double scale, out _, out string scaleToken) || scale <= 0 ||
            !TryScalar(pass, "ui_editor_properties_delay", out double delay, out _, out string delayToken) || delay <= 0)
        {
            unresolved.Add(new(ownerId, effectIndex, passIndex, shaderResource, ShaderTemporalUnresolvedKind.MissingOrInvalidSpeed,
                "Verified linear shimmer timing needs nonzero speed and positive granularity and delay constants."));
            return true;
        }
        components.Add(new(new($"shader/{ownerId}/{effectIndex}/{passIndex}/speed",
            new CommonLoopPeriod(Math.Abs(scale * delay / speed), CommonLoopPeriodEvidence.Analytic), AllowRetime: true),
            new(ownerId, effectIndex, passIndex, "ui_editor_properties_speed", speedIndex, speed), shaderResource,
            $"Verified linear MODE=0 shimmer frac coordinate; period is abs({scaleToken}*{delayToken}/{speedToken})."));
        return true;
    }

    private static bool TryHandleShake(int ownerId, int effectIndex, int passIndex, JsonObject pass, string shaderResource,
        string shaderText, List<ShaderPeriodComponent> components, List<ShaderTemporalUnresolved> unresolved)
    {
        const string WrappedTime = "float time = g_Speed * g_Time + flowPhase;";
        const string WrappedSine = "offset = sin(frac(time / M_PI_2) * M_PI_2);";
        const string WrappedBranch = "float base = step(0.0, cos(time));";
        const string NoiseTime = "vec4 sines = flowPhase + frac(g_Speed * g_Time / M_PI_2 * vec4(1, -0.16161616, 0.0083333, -0.00019841)) * M_PI_2;";
        const string StaticPhase = "float flowPhase = 0.0;";
        const string TexturePhase = "flowPhase = texSample2D(g_Texture2, v_TexCoord.zw).r * M_PI_2;";
        if (!shaderText.Contains(WrappedTime, StringComparison.Ordinal) ||
            !shaderText.Contains(WrappedSine, StringComparison.Ordinal) ||
            !shaderText.Contains(WrappedBranch, StringComparison.Ordinal) ||
            !shaderText.Contains(NoiseTime, StringComparison.Ordinal) ||
            !shaderText.Contains(StaticPhase, StringComparison.Ordinal) ||
            !shaderText.Contains(TexturePhase, StringComparison.Ordinal) ||
            Regex.Matches(shaderText, @"\bg_Time\b", RegexOptions.CultureInvariant).Count != 3 ||
            Regex.Matches(shaderText, @"\bflowPhase\b", RegexOptions.CultureInvariant).Count != 4) return false;

        const string NoiseDisabledDefault = "\"combo\":\"NOISE\",\"type\":\"options\",\"default\":0";
        if (shaderText.Contains("#if NOISE", StringComparison.Ordinal) &&
            ComboEnabledOrUnproven(pass, "NOISE", !shaderText.Contains(NoiseDisabledDefault, StringComparison.Ordinal)))
        {
            unresolved.Add(new(ownerId, effectIndex, passIndex, shaderResource, ShaderTemporalUnresolvedKind.NonPeriodicOrDriftingMechanism,
                "Shake NOISE is enabled or unproven; its multiple time coefficients do not establish a short exact loop."));
            return true;
        }
        const string AudioDisabledDefault = "\"combo\":\"AUDIOPROCESSING\",\"type\":\"audioprocessingoptions\",\"default\":0";
        if (shaderText.Contains("#if AUDIOPROCESSING == 0", StringComparison.Ordinal) &&
            ComboEnabledOrUnproven(pass, "AUDIOPROCESSING", !shaderText.Contains(AudioDisabledDefault, StringComparison.Ordinal)))
        {
            unresolved.Add(new(ownerId, effectIndex, passIndex, shaderResource, ShaderTemporalUnresolvedKind.NonPeriodicOrDriftingMechanism,
                "Shake is driven by enabled or unproven external audio instead of its periodic time equation."));
            return true;
        }
        if (!TryScalar(pass, "speed", out double speed, out int valueIndex, out string numericText))
        {
            unresolved.Add(new(ownerId, effectIndex, passIndex, shaderResource, ShaderTemporalUnresolvedKind.MissingOrInvalidSpeed,
                "Verified wrapped shake timing needs a finite scalar 'speed' constant."));
            return true;
        }
        if (speed == 0) return true;
        components.Add(new(new($"shader/{ownerId}/{effectIndex}/{passIndex}/speed",
            new CommonLoopPeriod(2 * Math.PI / Math.Abs(speed), CommonLoopPeriodEvidence.Analytic), AllowRetime: true),
            new(ownerId, effectIndex, passIndex, "speed", valueIndex, speed), shaderResource,
            $"Verified wrapped sin/cos(g_Time * g_Speed + static phase) shake timing; source speed token is {numericText}."));
        return true;
    }

    private static bool TryHandleNoiseDisabledPulse(int ownerId, int effectIndex, int passIndex, JsonObject pass, string shaderResource,
        string shaderText, List<ShaderPeriodComponent> components, List<ShaderTemporalUnresolved> unresolved)
    {
        const string SpeedUniform = "uniform float g_PulseSpeed; // {\"material\":\"speed\"";
        const string NoiseAmountUniform = "uniform float g_NoiseAmount; // {\"material\":\"noiseamount\"";
        const string NoiseEquation = "float noise = texSample2D(g_Texture1, vec2(g_Time * 0.08333333, g_Time * 0.02777777) * g_NoiseSpeed).r * g_NoiseAmount;";
        if (!shaderText.Contains(SpeedUniform, StringComparison.Ordinal) ||
            !shaderText.Contains(NoiseAmountUniform, StringComparison.Ordinal) ||
            !shaderText.Contains(NoiseEquation, StringComparison.Ordinal)) return false;
        int timeUniforms = Regex.Matches(shaderText, @"uniform\s+float\s+g_Time\s*;", RegexOptions.CultureInvariant).Count;
        int pulseSines = Regex.Matches(shaderText, @"sin\s*\(\s*g_Time\s*\*\s*g_PulseSpeed\s*\+", RegexOptions.CultureInvariant).Count;
        if (pulseSines == 0 || timeUniforms != pulseSines ||
            Regex.Matches(shaderText, @"\bg_Time\b", RegexOptions.CultureInvariant).Count != timeUniforms + pulseSines + 2 ||
            Regex.Matches(shaderText, @"\bg_PulseSpeed\b", RegexOptions.CultureInvariant).Count != timeUniforms + pulseSines ||
            Regex.Matches(shaderText, @"\bg_NoiseSpeed\b", RegexOptions.CultureInvariant).Count != 2 ||
            Regex.Matches(shaderText, @"\bg_NoiseAmount\b", RegexOptions.CultureInvariant).Count != 2 ||
            Regex.IsMatch(shaderText, @"\b(?:g_Frametime|g_Runtime|g_DeltaTime)\b", RegexOptions.CultureInvariant)) return false;

        const string AudioDisabledDefault = "\"combo\":\"AUDIOPROCESSING\",\"type\":\"audioprocessingoptions\",\"default\":0";
        if (ComboEnabledOrUnproven(pass, "AUDIOPROCESSING", !shaderText.Contains(AudioDisabledDefault, StringComparison.Ordinal)))
        {
            unresolved.Add(new(ownerId, effectIndex, passIndex, shaderResource, ShaderTemporalUnresolvedKind.NonPeriodicOrDriftingMechanism,
                "Pulse is driven by enabled or unproven external audio instead of its periodic time equation."));
            return true;
        }
        if (!TryScalar(pass, "noiseamount", out double noiseAmount, out _, out _))
        {
            unresolved.Add(new(ownerId, effectIndex, passIndex, shaderResource, ShaderTemporalUnresolvedKind.MissingOrInvalidSpeed,
                "Verified pulse timing needs a finite authored 'noiseamount' to prove that noise drift is disabled."));
            return true;
        }
        if (noiseAmount != 0)
        {
            unresolved.Add(new(ownerId, effectIndex, passIndex, shaderResource, ShaderTemporalUnresolvedKind.NonPeriodicOrDriftingMechanism,
                "Pulse noise UV drift is enabled, so the simple pulse speed does not prove closure."));
            return true;
        }
        if (!TryScalar(pass, "speed", out double speed, out int valueIndex, out string numericText))
        {
            unresolved.Add(new(ownerId, effectIndex, passIndex, shaderResource, ShaderTemporalUnresolvedKind.MissingOrInvalidSpeed,
                "Verified noise-disabled pulse timing needs a finite scalar 'speed' constant."));
            return true;
        }
        if (speed == 0) return true;
        components.Add(new(new($"shader/{ownerId}/{effectIndex}/{passIndex}/speed",
            new CommonLoopPeriod(2 * Math.PI / Math.Abs(speed), CommonLoopPeriodEvidence.Analytic), AllowRetime: true),
            new(ownerId, effectIndex, passIndex, "speed", valueIndex, speed), shaderResource,
            $"Verified all active g_Time terms reduce to sin(g_Time * g_PulseSpeed + static phase) when authored noiseamount is zero; source speed token is {numericText}."));
        return true;
    }

    // filmgrain.vert:25-28 wraps the only clock use in frac(g_Time) and derives both noise
    // coordinates from that single scalar, so the whole pass repeats once per second. The rate is
    // a shader literal: no material constant exists, hence the empty patch key and no retiming.
    private static bool TryHandleFrameFractionGrain(int ownerId, int effectIndex, int passIndex, string shaderResource,
        string shaderText, List<ShaderPeriodComponent> components)
    {
        const string Clock = "float t = frac(g_Time);";
        const string FirstLayer = "v_TexCoordNoise.xy = (a_TexCoord.xy + t) * g_NoiseScale;";
        const string SecondLayer = "v_TexCoordNoise.zw = (a_TexCoord.xy - t * 2.5) * g_NoiseScale * 0.52;";
        string source = NormalizeShaderSource(shaderText);
        if (!source.Contains(Clock, StringComparison.Ordinal) ||
            !source.Contains(FirstLayer, StringComparison.Ordinal) ||
            !source.Contains(SecondLayer, StringComparison.Ordinal) ||
            CountWord(source, "g_Time") != 2 || CountWord(source, "t") != 3 ||
            CountAssignments(source, "t") != 1 || HasAlternateClock(source)) return false;
        var exact = new CommonLoopRational(1);
        components.Add(new(new($"shader/{ownerId}/{effectIndex}/{passIndex}/frame-fraction",
            new CommonLoopPeriod(exact.ToSeconds(), CommonLoopPeriodEvidence.Analytic, exact), AllowRetime: false),
            new(ownerId, effectIndex, passIndex, "", 0, 0), shaderResource,
            "Verified film-grain clock: frac(g_Time) is the only g_Time use and both noise coordinates are functions of it, " +
            "so the exact period is one second. The rate is a shader literal, so the pass has no retimable material constant."));
        return true;
    }

    // crt_screen.frag:86-88 folds the clock into mod(... + g_Time * u_frequency, 1.0) and
    // mod(... + g_Time * u_frequency, 2.0); both return together after 2/abs(frequency) seconds.
    private static bool TryHandleCrtScanArtifacts(int ownerId, int effectIndex, int passIndex, JsonObject pass,
        string shaderResource, string shaderText, List<ShaderPeriodComponent> components, List<ShaderTemporalUnresolved> unresolved)
    {
        const string ArtifactsCombo = "\"combo\":\"ARTIFACTS\",\"type\":\"options\",\"default\":1";
        const string FrequencyUniform = "uniform float u_frequency; // {\"material\":\"Frequency\"";
        const string Clock = "float speed = g_Time * u_frequency;";
        const string ScanLine = "albedo.rgb *= 1.0 - min(1.0, mod(1.0 - uv.y * u_amount1 + speed, 1.0) * u_size1) * u_strength1;";
        const string Flicker = "albedo.rgb *= 1.0 - abs(mod(1.0 - (uv.y + u_offset2) * u_amount2 + speed, 2.0) - 1.0) * u_strength2;";
        if (!shaderText.Contains(FrequencyUniform, StringComparison.Ordinal) ||
            !shaderText.Contains(Clock, StringComparison.Ordinal) ||
            !shaderText.Contains(ScanLine, StringComparison.Ordinal) ||
            !shaderText.Contains(Flicker, StringComparison.Ordinal) ||
            !shaderText.Contains("#if ARTIFACTS", StringComparison.Ordinal) ||
            Regex.Matches(shaderText, @"\bg_Time\b", RegexOptions.CultureInvariant).Count != 2 ||
            Regex.Matches(shaderText, @"\bu_frequency\b", RegexOptions.CultureInvariant).Count != 2 ||
            Regex.Matches(shaderText, @"\bspeed\b", RegexOptions.CultureInvariant).Count != 3 ||
            Regex.Matches(shaderText, @"\bspeed\s*=(?!=)", RegexOptions.CultureInvariant).Count != 1 ||
            Regex.IsMatch(shaderText, @"\b(?:g_Frametime|g_Runtime|g_DeltaTime)\b", RegexOptions.CultureInvariant)) return false;

        // combo 值可能是 WE 编辑器绑定 bool 用户属性后产出的 JSON true/false，也可能是 1.0 或 "1"；
        // 读不出来时既不能当启用也不能当关闭，只能按未证明处理。
        ComboRead artifactsRead = ReadCombo(pass, "ARTIFACTS", out int artifactsValue);
        if (artifactsRead == ComboRead.Unreadable)
        {
            unresolved.Add(new(ownerId, effectIndex, passIndex, shaderResource, ShaderTemporalUnresolvedKind.UnsupportedShaderMechanism,
                "CRT ARTIFACTS combo value is neither an integer nor a boolean, so the scan-line branch is not proven disabled."));
            return true;
        }
        bool artifacts = artifactsRead == ComboRead.Value
            ? artifactsValue != 0
            : shaderText.Contains(ArtifactsCombo, StringComparison.Ordinal);
        // Both g_Time uses live inside the artifacts branch, so a disabled branch leaves no clock.
        if (!artifacts) return true;
        if (!TryScalar(pass, "Frequency", out double frequency, out int valueIndex, out string numericText))
        {
            unresolved.Add(new(ownerId, effectIndex, passIndex, shaderResource, ShaderTemporalUnresolvedKind.MissingOrInvalidSpeed,
                "Verified CRT scan-line timing needs a finite scalar 'Frequency' constant."));
            return true;
        }
        if (frequency == 0) return true;
        components.Add(new(new($"shader/{ownerId}/{effectIndex}/{passIndex}/frequency",
            new CommonLoopPeriod(2 / Math.Abs(frequency), CommonLoopPeriodEvidence.Analytic), AllowRetime: true),
            new(ownerId, effectIndex, passIndex, "Frequency", valueIndex, frequency), shaderResource,
            $"Verified scan-line mod(...,1.0) and flicker mod(...,2.0) over g_Time * u_frequency; joint period is 2/abs({numericText})."));
        return true;
    }

    // lightshafts.frag:101-102 translates two noise lookups at four different rates. Even granting
    // repeat addressing on the noise texture, the axes close together only after an astronomical
    // time, so the pass has no loop that a bounded retime can reach.
    private static bool TryHandleLightShaftRays(int ownerId, int effectIndex, int passIndex, JsonObject pass,
        string shaderResource, string shaderText, List<ShaderTemporalUnresolved> unresolved, double ceiling)
    {
        const string SpeedUniform = "uniform float g_Speed; // {\"material\":\"rayspeed\"";
        const string FirstDrift = "fxCoord.xy += g_Time * g_Speed * vec2(0.003, 0.000375111);";
        const string SecondDrift = "fxCoord2.xy -= g_Time * g_Speed * vec2(0.0047111, 0.0007399);";
        if (!shaderText.Contains(SpeedUniform, StringComparison.Ordinal) ||
            !shaderText.Contains(FirstDrift, StringComparison.Ordinal) ||
            !shaderText.Contains(SecondDrift, StringComparison.Ordinal) ||
            Regex.Matches(shaderText, @"\bg_Time\b", RegexOptions.CultureInvariant).Count != 3 ||
            Regex.Matches(shaderText, @"\bg_Speed\b", RegexOptions.CultureInvariant).Count != 3 ||
            Regex.IsMatch(shaderText, @"\b(?:g_Frametime|g_Runtime|g_DeltaTime)\b", RegexOptions.CultureInvariant)) return false;
        if (!TryScalar(pass, "rayspeed", out double speed, out _, out string numericText))
        {
            unresolved.Add(new(ownerId, effectIndex, passIndex, shaderResource, ShaderTemporalUnresolvedKind.MissingOrInvalidSpeed,
                "Verified light-shaft drift needs a finite scalar 'rayspeed' constant to state its period."));
            return true;
        }
        if (speed == 0) return true;
        double rate = Math.Abs(speed);
        // The four UV rates are rayspeed * (0.003, 0.000375111, 0.0047111, 0.0007399) per second.
        // A shared return needs rayspeed * t * rate to be a whole texture repeat on every axis at
        // once; over those exact decimals the smallest such t is 1e9 / rayspeed seconds.
        double fastest = 1 / (LightShaftDriftRates.Max() * rate), slowest = 1 / (LightShaftDriftRates.Min() * rate),
            joint = LightShaftJointRepeatUnits / rate;
        unresolved.Add(new(ownerId, effectIndex, passIndex, shaderResource, ShaderTemporalUnresolvedKind.NonPeriodicOrDriftingMechanism,
            $"Light-shaft noise UVs translate at rayspeed {numericText} * (0.003, 0.000375111, 0.0047111, 0.0007399) per second. " +
            $"The fastest axis alone repeats only every {fastest.ToString("0.#", CultureInfo.InvariantCulture)} s and the slowest every " +
            $"{slowest.ToString("0.#", CultureInfo.InvariantCulture)} s; all four close together only after " +
            $"{joint.ToString("0.###E+00", CultureInfo.InvariantCulture)} s. Every rate is proportional to rayspeed, so a retime " +
            // 单轴周期可能落在上限内（rayspeed 1 时最快轴约 212 秒），说不回来的是四轴联合回归。
            $"scales them equally and cannot bring the joint return inside the {CeilingText(ceiling)}-second loop ceiling.",
            // UV 偏移随 t 线性增长，没有幅度上界：接缝淡化盖不住它。
            BoundedDisplacement: false, Mechanism: LightShaftDriftMechanism));
        return true;
    }

    // iris.vert:31-41 drives the eye from floor(g_Time * g_Speed + phase). The per-step offsets are
    // sin(1.9*n) and sin(2.5*n + c) at integer n, whose frequencies are irrational multiples of 2π.
    private static bool TryHandleIrisSaccade(int ownerId, int effectIndex, int passIndex, string shaderResource,
        string shaderText, List<ShaderTemporalUnresolved> unresolved)
    {
        const string Clock = "float time = (g_Time * g_Speed) + g_PhaseOffset;";
        const string Step = "float lowDt = floor(time);";
        const string PairHash = "vec2 motion2 = sin(1.9 * (lowDt + vec2(0, 1)));";
        const string QuadHash = "vec4 motion4 = sin(2.5 * (lowDt + vec4(0, 0, 1, 1)) + vec4(1, 2, 1, 2));";
        const string SmoothX = "da.x += sin(time) * g_NoiseAmount;";
        const string SmoothY = "da.y += cos(time) * g_NoiseAmount;";
        if (!shaderText.Contains(Clock, StringComparison.Ordinal) || !shaderText.Contains(Step, StringComparison.Ordinal) ||
            !shaderText.Contains(PairHash, StringComparison.Ordinal) || !shaderText.Contains(QuadHash, StringComparison.Ordinal) ||
            !shaderText.Contains(SmoothX, StringComparison.Ordinal) || !shaderText.Contains(SmoothY, StringComparison.Ordinal) ||
            Regex.Matches(shaderText, @"\bg_Time\b", RegexOptions.CultureInvariant).Count != 2 ||
            Regex.Matches(shaderText, @"\bg_Speed\b", RegexOptions.CultureInvariant).Count != 2 ||
            Regex.Matches(shaderText, @"\btime\b", RegexOptions.CultureInvariant).Count != 5 ||
            Regex.Matches(shaderText, @"\blowDt\b", RegexOptions.CultureInvariant).Count != 3 ||
            Regex.IsMatch(shaderText, @"\b(?:g_Frametime|g_Runtime|g_DeltaTime)\b", RegexOptions.CultureInvariant)) return false;
        unresolved.Add(new(ownerId, effectIndex, passIndex, shaderResource, ShaderTemporalUnresolvedKind.NonPeriodicOrDriftingMechanism,
            "Iris motion is indexed by the integer step floor(g_Time * g_Speed + phase) and offset by sin(1.9 * step) and " +
            "sin(2.5 * step + c). Repeating those offsets needs 1.9 and 2.5 times a whole number of steps to be whole multiples " +
            "of 2*pi, which no integer satisfies because 1.9/(2*pi) and 2.5/(2*pi) are irrational. The step blend frac(time) " +
            "(period 1 in time) and the sin(time)/cos(time) terms (period 2*pi in time) are incommensurable for the same reason, " +
            "so no speed value and no retime give this pass an exact period.",
            // 眼球跳变的幅度由 moveStart/moveEnd 决定，这条规则不解析它，因此不声称有界。
            BoundedDisplacement: false, Mechanism: IrisSaccadeMechanism));
        return true;
    }

    // foliagesway samples eight sines whose slowest term is speed * 0.000024801587 rad/s. Even at the
    // fastest speed the shader accepts, that single term needs hours, so no loop exists in range.
    private static bool TryHandleFoliageSway(int ownerId, int effectIndex, int passIndex, JsonObject pass, string shader,
        string shaderResource, string shaderText, JsonObject owner, IReadOnlyDictionary<int, JsonObject> objectsById,
        List<ShaderTemporalUnresolved> unresolved, double ceiling)
    {
        const string VertexSpeed = "uniform float g_Speed; // {\"material\":\"speed\"";
        const string FragmentSpeed = "uniform float g_Speed; // {\"material\":\"speeduv\"";
        // 系数按 vec4 数值解析（接受 0 与已改写成按 g_Speed 分支的形态），两阶段必须一致；其余计数判据在
        // 去掉系数表达式后的全文上做，stock 文本的判定与旧的字面量匹配完全相同。
        if (!ShaderTextPatch.TryParseClockTerms(shaderText, out ShaderTextPatch.ClockTerms clock) ||
            !shaderText.Contains(VertexSpeed, StringComparison.Ordinal) ||
            !shaderText.Contains(FragmentSpeed, StringComparison.Ordinal) ||
            Regex.Matches(clock.MaskedText, @"\bg_Time\b", RegexOptions.CultureInvariant).Count != 6 ||
            Regex.Matches(clock.MaskedText, @"\bg_Speed\b", RegexOptions.CultureInvariant).Count != 6 ||
            Regex.IsMatch(shaderText, @"\b(?:g_Frametime|g_Runtime|g_DeltaTime)\b", RegexOptions.CultureInvariant)) return false;
        // MODE 1 sways vertices with 'speed'; the default MODE 0 sways UVs with 'speeduv'.
        // 读不出 MODE 时两条幅度门控都无法证明，直接标 unresolved 而不是走下面的零振幅豁免。
        ComboRead modeRead = ReadCombo(pass, "MODE", out int mode);
        if (modeRead == ComboRead.Unreadable)
        {
            unresolved.Add(new(ownerId, effectIndex, passIndex, shaderResource, ShaderTemporalUnresolvedKind.UnsupportedShaderMechanism,
                "Foliage sway MODE combo value is neither an integer nor a boolean, so neither its vertex nor its UV amplitude gate is proven."));
            return true;
        }
        bool vertexMode = modeRead == ComboRead.Value && mode == 1;
        string key = vertexMode ? "speed" : "speeduv";
        bool hasSpeed = TryScalar(pass, key, out double speed, out _, out string numericText);
        // Both stages scale the whole sine sum by strength before it reaches a UV or a vertex, and
        // the vertex stage additionally gates its two axes by directionweights. A zero amplitude, or
        // a stopped clock, leaves the pass with no motion at all, so it constrains no loop.
        if (hasSpeed && speed == 0) return true;
        if (TryScalar(pass, "strength", out double strength, out _, out _) && strength == 0) return true;
        if (vertexMode && TryVec2(pass, "directionweights", out double weightX, out double weightY) &&
            weightX == 0 && weightY == 0) return true;
        SwayModel? model = BuildSwayModel(ownerId, effectIndex, passIndex, pass, shader, shaderText, clock, vertexMode, key,
            hasSpeed ? speed : (double?)null, owner, objectsById);
        // 已改写的 shader 可能把这个速度的 8 项全冻结：没有运动，不约束循环。
        if (model is not null && model.Coefficients.All(value => value == 0)) return true;
        bool canonical = clock.Sines.Branches.Count == 0 && clock.CoSines.Branches.Count == 0 &&
            clock.Sines.Fallback.Concat(clock.CoSines.Fallback).SequenceEqual(SwayModel.CanonicalCoefficients);
        if (!canonical)
        {
            string coefficients = model is null
                ? "coefficients that depend on g_Speed"
                : "(" + string.Join(", ", model.Coefficients.Select(value => value.ToString("R", CultureInfo.InvariantCulture))) + ")";
            unresolved.Add(new(ownerId, effectIndex, passIndex, shaderResource, ShaderTemporalUnresolvedKind.NonPeriodicOrDriftingMechanism,
                $"Foliage sway adds eight sines at {key} * {coefficients} rad/s. These are not the stock foliagesway literals, and this " +
                $"analysis establishes no common period for them within the {CeilingText(ceiling)}-second loop ceiling; a retime scales every term equally, " +
                "so this pass has no usable loop.",
                BoundedDisplacement: true, Mechanism: FoliageSwayMechanism, Sway: model));
            return true;
        }
        const double Slowest = 0.000024801587;
        double slowestTermSeconds = 2 * Math.PI / ((hasSpeed && speed != 0 ? Math.Abs(speed) : 20) * Slowest);
        // 1/443520 is the common base of the nearest simple fractions of the eight coefficients:
        // lcm(1, 99, 120, 5040, 2, 24, 720, 40320) = 443520 once 16/99 contributes its factor 11.
        const double CommonBase = 443520;
        string rate = hasSpeed && speed != 0
            ? $"{numericText}, so that term alone needs {(2 * Math.PI / (Math.Abs(speed) * Slowest)).ToString("0.#", CultureInfo.InvariantCulture)} s"
            : "the shader range [0.01, 20], so that term alone needs at least " +
              $"{(2 * Math.PI / (20 * Slowest)).ToString("0.#", CultureInfo.InvariantCulture)} s";
        string common = hasSpeed && speed != 0
            ? $"{(2 * Math.PI * CommonBase / Math.Abs(speed)).ToString("0.#", CultureInfo.InvariantCulture)} s"
            : $"at least {(2 * Math.PI * CommonBase / 20).ToString("0.#", CultureInfo.InvariantCulture)} s";
        unresolved.Add(new(ownerId, effectIndex, passIndex, shaderResource, ShaderTemporalUnresolvedKind.NonPeriodicOrDriftingMechanism,
            $"Foliage sway adds eight sines at {key} * (1, -0.16161616, 0.0083333, -0.00019841, -0.5, 0.041666666, -0.0013888889, " +
            $"0.000024801587) rad/s. The slowest coefficient is 0.000024801587 with {rate}. Read as their nearest simple fractions " +
            $"(1, 16/99, 1/120, 1/5040, 1/2, 1/24, 1/720, 1/40320), which is the most generous reading available, the eight share " +
            $"the base 1/443520, so they return together only after 2*pi*443520/{key} s ({common}); the shipped literals are " +
            "truncations of those fractions and can only make the true return longer. " +
            (slowestTermSeconds > ceiling ? "Both bounds are" : "The common return is") +
            $" outside the {CeilingText(ceiling)}-second loop ceiling, and a retime scales every term equally, so this pass has no usable loop.",
            // 八项正弦和整体乘以 strength²·0.005 才进 UV 或顶点，峰值位移有解析上界，残差掩盖据此判定。
            BoundedDisplacement: true, Mechanism: FoliageSwayMechanism, Sway: model));
        return true;
    }

    /// <summary>
    /// 摆动方程的结构化参数。缺省常量取 shader uniform 注释里的 default；速度读不出（非有限）时返回 null，
    /// 这条分量就只能照旧留作未解析项。图层尺寸与父链 scale 读不到时尺寸记 null、scale 记 1。
    /// </summary>
    private static SwayModel? BuildSwayModel(int ownerId, int effectIndex, int passIndex, JsonObject pass, string shader,
        string shaderText, ShaderTextPatch.ClockTerms clock, bool vertexMode, string speedKey, double? authoredSpeed,
        JsonObject owner, IReadOnlyDictionary<int, JsonObject> objectsById)
    {
        Dictionary<string, JsonNode?> defaults = UniformDefaults(shaderText);
        double Scalar(string key, double fallback)
        {
            if (TryScalar(pass, key, out double value, out _, out _)) return value;
            return defaults.GetValueOrDefault(key) is JsonValue json && json.TryGetValue<double>(out double parsed) && double.IsFinite(parsed)
                ? parsed : fallback;
        }
        double speed = authoredSpeed ?? Scalar(speedKey, double.NaN);
        if (!double.IsFinite(speed) || speed == 0 || !float.IsFinite((float)speed)) return null;
        double gpuSpeed = (float)speed;
        double[] coefficients = [.. clock.Sines.Evaluate(gpuSpeed), .. clock.CoSines.Evaluate(gpuSpeed)];
        double weightX = 1, weightY = 0.2;
        if (vertexMode && !TryVec2(pass, "directionweights", out weightX, out weightY))
        {
            (weightX, weightY) = (1, 0.2);
            if (defaults.GetValueOrDefault("directionweights") is JsonValue text && text.TryGetValue<string>(out string? words))
            {
                string[] parts = words.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double x) &&
                    double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double y)) (weightX, weightY) = (x, y);
            }
        }
        double? width = null, height = null;
        double scaleX = 1, scaleY = 1;
        try
        {
            if (owner["size"] is not null)
            {
                var size = HybridVideoProjection.Vector(owner["size"], (double.NaN, double.NaN));
                if (double.IsFinite(size.X) && double.IsFinite(size.Y) && size.X > 0 && size.Y > 0) (width, height) = (size.X, size.Y);
            }
            var seen = new HashSet<int> { ownerId };
            JsonObject? current = owner;
            while (current is not null)
            {
                var scale = HybridVideoProjection.Vector(current["scale"], (1, 1));
                (scaleX, scaleY) = (scaleX * scale.X, scaleY * scale.Y);
                current = current["parent"] is JsonValue parent && parent.TryGetValue<int>(out int parentId) && seen.Add(parentId)
                    ? objectsById.GetValueOrDefault(parentId) : null;
            }
        }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException or FormatException)
        {
            (scaleX, scaleY) = (1, 1);
        }
        return new(ownerId, effectIndex, passIndex, shader, vertexMode ? 1 : 0, speedKey, gpuSpeed, coefficients,
            Scalar("strength", 0.4), Scalar("ratio", 0.3), Scalar("scrolldirection", 0), Scalar("power", 1),
            weightX, weightY, width, height, scaleX, scaleY);
    }

    /// <summary>shader uniform 注释里 material 名 → default 值（frag 在前，同名取先出现的）。</summary>
    private static Dictionary<string, JsonNode?> UniformDefaults(string shaderText)
    {
        var defaults = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(shaderText, @"uniform\s+\w+\s+\w+\s*;\s*//\s*(?<json>\{[^\r\n]*\})", RegexOptions.CultureInvariant))
        {
            try
            {
                if (JsonNode.Parse(match.Groups["json"].Value) is JsonObject annotation &&
                    annotation["material"] is JsonValue material && material.TryGetValue<string>(out string? name) && name is not null)
                    defaults.TryAdd(name, annotation["default"]?.DeepClone());
            }
            catch (System.Text.Json.JsonException) { }
        }
        return defaults;
    }

    private static string CeilingText(double seconds) => seconds.ToString("0.###", CultureInfo.InvariantCulture);

    // The workshop CRT shadow-map noise is hash(round(uv) + g_Time) with frac(p * 0.1031) as its
    // first step, so it is exactly 10000/1031-second periodic; that period is a shader literal on
    // an awkward denominator, so it cannot be retimed onto the output frame grid.
    private static bool TryHandleShadowMaskHash(int ownerId, int effectIndex, int passIndex, JsonObject pass,
        string shaderResource, string shaderText, List<ShaderTemporalUnresolved> unresolved, double ceiling)
    {
        const string HashScale = "vec3 p3 = frac(CAST3(p.xyx) * 0.1031);";
        const string HashMix = "p3 += dot(p3, p3.yzx + 33.33);";
        const string HashResult = "return frac((p3.x + p3.y) * p3.z);";
        const string NoiseUse = "albedo.rgb *= 1.0 - hash(round(uv) + g_Time) * u_noise;";
        if (!shaderText.Contains(HashScale, StringComparison.Ordinal) || !shaderText.Contains(HashMix, StringComparison.Ordinal) ||
            !shaderText.Contains(HashResult, StringComparison.Ordinal) || !shaderText.Contains(NoiseUse, StringComparison.Ordinal) ||
            Regex.Matches(shaderText, @"\bg_Time\b", RegexOptions.CultureInvariant).Count != 2 ||
            Regex.Matches(shaderText, @"\bu_noise\b", RegexOptions.CultureInvariant).Count != 2 ||
            Regex.Matches(shaderText, @"\bp3\b", RegexOptions.CultureInvariant).Count != 7 ||
            Regex.IsMatch(shaderText, @"\b(?:g_Frametime|g_Runtime|g_DeltaTime)\b", RegexOptions.CultureInvariant)) return false;
        // A zero static-noise amount removes the only clock term from the output.
        if (TryScalar(pass, "Static noise", out double amount, out _, out _) && amount == 0) return true;
        unresolved.Add(new(ownerId, effectIndex, passIndex, shaderResource, ShaderTemporalUnresolvedKind.UnsupportedShaderMechanism,
            "The static-noise hash reduces to frac((round(uv) + g_Time) * 0.1031), so its exact period is 10000/1031 s " +
            "(9.699321 s). 0.1031 is a shader literal with no material constant, so the period is fixed, and 10000/1031 s " +
            $"lands on the 60 fps frame grid only after 600000 frames (10000 s), past the {CeilingText(ceiling)}-second loop ceiling. " +
            "Set the pass 'Static noise' amount to zero to remove the clock."));
        return true;
    }

    // waterripple.vert（PERSPECTIVE 0）与 waterripple.frag（PERSPECTIVE 1）用同一组时间式平移两次法线贴图查表：
    //   c1 = (uv + t·a² + t·s²·d)·scale，c2 = (1.333·uv − t·a² + t·s²·d)·scale，之后 .xz 乘 W/H、.yw 乘 ratio；
    //   a = animationspeed，s = scrollspeed，d = rotateVec2(vec2(0, 1), dir) = (−sin dir, cos dir)，
    //   W×H 是效果输入 rt（g_Texture0Resolution.xy）。PERSPECTIVE 只换静态的 uv 与遮罩，不改时间项。
    // 法线贴图按 repeat 寻址时查表坐标平移整数圈结果不变。s = 0 时两条轴每秒平移 a²·|scale|·(W/H, |ratio|) 圈，
    // 同时回到整数圈的最小时长是 1/(a²·|scale|·gcd(W/H, |ratio|))。s ≠ 0 时两次查表、两条轴的速率比取决于
    // 方向的 sin/cos 与 a²、s² 的比，这里不建立周期。
    private static bool TryHandleWaterRipple(int ownerId, int effectIndex, int passIndex, JsonObject pass, string shaderResource,
        string shaderText, JsonObject owner, ProjectSource source, string? assetsDirectory,
        List<ShaderPeriodComponent> components, List<ShaderTemporalUnresolved> unresolved, double ceiling)
    {
        const string Scroll = "vec2 scroll = rotateVec2(vec2(0, 1), g_Direction) * g_ScrollSpeed * g_ScrollSpeed * g_Time;";
        const string Adjustment = "float rippleTextureAdjustment = (g_Texture0Resolution.x / g_Texture0Resolution.y);";
        string[] exactlyOnce = [
            "v_TexCoordRipple.xy = coordsRotated + g_Time * g_AnimationSpeed * g_AnimationSpeed + scroll;",
            "v_TexCoordRipple.zw = coordsRotated2 - g_Time * g_AnimationSpeed * g_AnimationSpeed + scroll;",
            "v_TexCoordRipple *= g_Scale;", "v_TexCoordRipple.xz *= rippleTextureAdjustment;", "v_TexCoordRipple.yw *= g_Ratio;",
            "rippleCoords = v_TexCoordRipple;",
            "rippleCoords.xy = coordsRotated + g_Time * g_AnimationSpeed * g_AnimationSpeed + scroll;",
            "rippleCoords.zw = coordsRotated2 - g_Time * g_AnimationSpeed * g_AnimationSpeed + scroll;",
            "rippleCoords *= g_Scale;", "rippleCoords.xz *= rippleTextureAdjustment;", "rippleCoords.yw *= g_Ratio;",
            "vec3 n1 = texSample2D(g_Texture2, rippleCoords.xy).xyz * 2 - 1;",
            "vec3 n2 = texSample2D(g_Texture2, rippleCoords.zw).xyz * 2 - 1;"];
        if (!shaderText.Contains("g_AnimationSpeed", StringComparison.Ordinal)) return false;
        string text = NormalizeShaderSource(shaderText);
        // 计数穷尽每个时钟与查表坐标的出现：两阶段各一个 g_Time 声明加三处使用，两阶段各一条 scroll 定义加两处使用，
        // 查表坐标只有上面列出的赋值与两次法线贴图采样。多出任何一处都不再是这组方程。
        if (CountSubstring(text, Scroll) != 2 || CountSubstring(text, Adjustment) != 2 ||
            exactlyOnce.Any(line => CountSubstring(text, line) != 1) ||
            CountWord(text, "g_Time") != 8 || CountWord(text, "g_AnimationSpeed") != 10 || CountWord(text, "g_ScrollSpeed") != 6 ||
            CountWord(text, "scroll") != 6 || CountWord(text, "rippleTextureAdjustment") != 4 ||
            CountWord(text, "v_TexCoordRipple") != 8 || CountWord(text, "rippleCoords") != 9 || CountWord(text, "g_Texture2") != 3 ||
            CountSwizzleAssignments(text, "v_TexCoordRipple") != 5 || CountSwizzleAssignments(text, "rippleCoords") != 6 ||
            HasAlternateClock(text)) return false;

        Dictionary<string, JsonNode?> defaults = UniformDefaults(shaderText);
        // animationspeed 必须是场景里写明的标量：改频要改写它。其余三个常量缺省时取 shader 声明的默认值。
        if (!RippleConstant("animationspeed", authoredOnly: true, out double speed, out int speedIndex, out string speedToken) ||
            !RippleConstant("scrollspeed", authoredOnly: false, out double scrollSpeed, out _, out string scrollToken) ||
            !RippleConstant("scale", authoredOnly: false, out double scale, out _, out string scaleToken) ||
            !RippleConstant("ratio", authoredOnly: false, out double ratio, out _, out string ratioToken))
        {
            unresolved.Add(new(ownerId, effectIndex, passIndex, shaderResource, ShaderTemporalUnresolvedKind.MissingOrInvalidSpeed,
                "Verified water-ripple timing needs an authored finite scalar 'animationspeed' and finite 'scrollspeed', 'scale' and 'ratio' constants (authored or shader default)."));
            return true;
        }
        double animation = speed * speed, drift = scrollSpeed * scrollSpeed;
        if (scale == 0 || (animation == 0 && drift == 0)) return true;
        if (!UsesStaticRepeatTexture(pass, 2, source, assetsDirectory))
        {
            unresolved.Add(new(ownerId, effectIndex, passIndex, shaderResource, ShaderTemporalUnresolvedKind.UnsupportedShaderMechanism,
                "Water-ripple normal lookups repeat only when texture slot 2 is a still texture with repeat addressing; that is not proven for this pass."));
            return true;
        }
        if (drift != 0)
        {
            unresolved.Add(new(ownerId, effectIndex, passIndex, shaderResource, ShaderTemporalUnresolvedKind.UnsupportedShaderMechanism,
                $"Water-ripple scroll is on (scrollspeed {scrollToken}). Both normal lookups then also translate by t*scrollspeed²*(-sin, cos)(direction) " +
                "on top of the opposite ±t*animationspeed² terms, so the two lookups and the two texture axes move at rates whose ratios depend on " +
                "the direction's sine and cosine and on animationspeed² against scrollspeed². The verified water-ripple period covers scrollspeed 0 only; " +
                "no period is established for this pass."));
            return true;
        }
        if (!TryEffectTargetExtent(owner, out long width, out long height))
        {
            unresolved.Add(new(ownerId, effectIndex, passIndex, shaderResource, ShaderTemporalUnresolvedKind.UnsupportedShaderMechanism,
                "Water-ripple timing scales the x axis by the effect render-target aspect g_Texture0Resolution.x / g_Texture0Resolution.y. " +
                "This layer is fullscreen or has no readable size, so that aspect is not proven."));
            return true;
        }
        var aspect = new CommonLoopRational(width, height);
        CommonLoopRational step = aspect;
        if (ratio != 0 && (!TrySimpleRational(Math.Abs(ratio), out CommonLoopRational ratioRational) ||
            !TryRationalGreatestCommonDivisor(aspect, ratioRational, out step)))
        {
            unresolved.Add(new(ownerId, effectIndex, passIndex, shaderResource, ShaderTemporalUnresolvedKind.MissingOrInvalidSpeed,
                $"Water-ripple x and y lookups return together only through gcd({width}/{height}, abs(ratio)); ratio {ratioToken} is not a simple rational, so no joint period is established."));
            return true;
        }
        double period = 1 / (animation * Math.Abs(scale) * step.ToSeconds());
        string equation = $"normal lookups translate ±t*animationspeed² with animationspeed {speedToken}, scale {scaleToken}, render target {width}/{height} " +
            $"and ratio {ratioToken}; both axes return to a whole texture repeat together every 1/(animationspeed²*abs(scale)*gcd(W/H, abs(ratio))) = " +
            $"{period.ToString("0.###", CultureInfo.InvariantCulture)} s";
        // 最大调速也够不进上限的精确周期不交给求解器：作为分量它只会让整张壁纸无候选，而作为未解析项，
        // 分配回退还能把这一层留实时、让其余图层照常找循环。
        // 上限是本次分析实际用的循环时长上限（--loop-length-max，含内嵌视频收紧），由 Analyze 传入，不取求解器缺省值。
        if (period > ceiling * (1 + MaximumRetimeFraction))
        {
            unresolved.Add(new(ownerId, effectIndex, passIndex, shaderResource, ShaderTemporalUnresolvedKind.NonPeriodicOrDriftingMechanism,
                $"Water-ripple scroll is off, so its {equation}. That is past the {CeilingText(ceiling)}-second loop ceiling " +
                "even with the largest retime, and a retime scales both axes equally.",
                BoundedDisplacement: true, Mechanism: WaterRippleScrollMechanism));
            return true;
        }
        components.Add(new(new($"shader/{ownerId}/{effectIndex}/{passIndex}/animationspeed",
            new CommonLoopPeriod(period, CommonLoopPeriodEvidence.Analytic), AllowRetime: true),
            new(ownerId, effectIndex, passIndex, "animationspeed", speedIndex, speed, SpeedExponent: 2), shaderResource,
            $"Verified water-ripple normal scroll with scrollspeed 0: {equation}."));
        return true;

        bool RippleConstant(string key, bool authoredOnly, out double value, out int index, out string token)
        {
            if (pass["constantshadervalues"]?[key] is not null) return TryScalar(pass, key, out value, out index, out token);
            (value, index, token) = (0, 0, "");
            if (authoredOnly || defaults.GetValueOrDefault(key) is not JsonValue json || !json.TryGetValue(out value) || !double.IsFinite(value))
                return false;
            token = json.ToJsonString() + " (shader default)";
            return true;
        }
    }

    /// <summary>求解器允许的单个分量最大调速比例；与 HybridLoopService 的 0–2% 入参上限一致。</summary>
    private const double MaximumRetimeFraction = 0.02;

    private static int CountSwizzleAssignments(string source, string identifier) =>
        Regex.Matches(source, $@"\b{Regex.Escape(identifier)}(?:\.[xyzw]+)?\s*(?:\+=|-=|\*=|/=|=(?!=))", RegexOptions.CultureInvariant).Count;

    /// <summary>
    /// 图层效果链输入 rt 的像素尺寸，即效果 pass 里 g_Texture0Resolution.xy。原生渲染器按图层 size 截断取整
    /// （不足 1 记 1）分配这张 rt，与输出分辨率无关；全屏图层改用相机尺寸，这里不推断，返回 false。
    /// </summary>
    private static bool TryEffectTargetExtent(JsonObject owner, out long width, out long height)
    {
        width = height = 0;
        if (owner["fullscreen"] is JsonNode fullscreen &&
            !(fullscreen is JsonValue flag && flag.TryGetValue(out bool isFullscreen) && !isFullscreen)) return false;
        if (owner["size"] is null) return false;
        try
        {
            var size = HybridVideoProjection.Vector(owner["size"], (double.NaN, double.NaN));
            if (!double.IsFinite(size.X) || !double.IsFinite(size.Y)) return false;
            (width, height) = (Dimension((float)size.X), Dimension((float)size.Y));
            return true;
        }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException or FormatException) { return false; }

        static long Dimension(float value) => value < 1 ? 1 : (long)value;
    }

    /// <summary>
    /// 纹理槽 <paramref name="slot"/> 指向一张 repeat 寻址的静止贴图：TEXV0005/TEXI0001 头，flags 不含
    /// ClampUVs(0x2)、精灵(0x4)、视频(0x20)。读不到或头部不认识一律 false。
    /// </summary>
    private static bool UsesStaticRepeatTexture(JsonObject pass, int slot, ProjectSource source, string? assetsDirectory)
    {
        if (pass["textures"] is not JsonArray textures || textures.Count <= slot || textures[slot] is not JsonValue value ||
            !value.TryGetValue(out string? name) || string.IsNullOrWhiteSpace(name)) return false;
        string resource = "materials/" + name + ".tex";
        try
        {
            byte[] header;
            if (source.Contains(resource)) header = source.ReadPrefix(resource, 64);
            else
            {
                if (assetsDirectory is null) return false;
                string path = ProjectSource.ContainedPath(assetsDirectory, resource);
                if (!File.Exists(path)) return false;
                using var input = File.OpenRead(path);
                header = new byte[Math.Min(64, checked((int)input.Length))];
                input.ReadExactly(header);
            }
            return header.Length >= 26 && header.AsSpan(0, 9).SequenceEqual("TEXV0005\0"u8) &&
                header.AsSpan(9, 9).SequenceEqual("TEXI0001\0"u8) && (BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(22, 4)) & 0x26) == 0;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException) { return false; }
    }

    /// <summary>
    /// 结构化的正弦时钟：挑出 combo 分支后，每个 g_Time 都只出现在 <c>float v = g_Time * g_Speed [* M_PI] + 静态项;</c>
    /// 形式的定义里，v 之后只接受 <c>v *= step(0.0, x);</c>（乘 0 或 1）、<c>v += 静态项;</c> 与 <c>sin(v)</c>。
    /// 输出因此只经 sin(v) 依赖时间，周期是 2π/(k·|speed|)，k 为 1 或 π。官方 waterwaves 已由上面的指纹规则处理；
    /// 这条规则接住加了静态相位、输出偏置或标准化时间单位（STD_TIME）的同构变体。
    /// </summary>
    private static bool TryHandleStructuredSineClock(int ownerId, int effectIndex, int passIndex, JsonObject pass, string shaderResource,
        string shaderText, List<ShaderPeriodComponent> components, List<ShaderTemporalUnresolved> unresolved)
    {
        const string SpeedUniform = "uniform float g_Speed; // {\"material\":\"speed\"";
        if (!shaderText.Contains(SpeedUniform, StringComparison.Ordinal) || !shaderText.Contains("sin(", StringComparison.Ordinal)) return false;
        if (SelectComboBranches(shaderText, pass) is not string selected) return false;
        // 分支挑完后只允许 #include 残留；#define 之类可能改写标识符，出现就不裁定。
        string[] lines = selected.Split('\n');
        if (lines.Any(line => line.TrimStart().StartsWith('#') && !line.TrimStart().StartsWith("#include", StringComparison.Ordinal))) return false;
        string text = NormalizeShaderSource(string.Join('\n', lines.Where(line => !line.TrimStart().StartsWith('#'))));
        if (HasAlternateClock(text)) return false;
        string[] statements = text.Split([';', '{', '}'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var definition = new Regex(@"^float (\w+) = g_Time \* g_Speed( \* M_PI)? \+ (.+)$", RegexOptions.CultureInvariant);
        var clocks = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (string statement in statements.Where(item => Regex.IsMatch(item, @"\bg_Time\b", RegexOptions.CultureInvariant)))
        {
            if (statement == "uniform float g_Time") continue;
            Match match = definition.Match(statement);
            if (!match.Success || Regex.IsMatch(match.Groups[3].Value, @"\bg_Time\b", RegexOptions.CultureInvariant)) return false;
            double coefficient = match.Groups[2].Success ? Math.PI : 1;
            if (clocks.TryGetValue(match.Groups[1].Value, out double existing) && existing != coefficient) return false;
            clocks[match.Groups[1].Value] = coefficient;
        }
        // 没有时钟定义，或两个时钟系数不同（例如 STD_TIME 值未知、两条分支都留着）：不是这条规则能裁定的形态。
        if (clocks.Count == 0 || clocks.Values.Distinct().Count() != 1) return false;
        foreach (string clock in clocks.Keys)
        {
            string word = $@"\b{Regex.Escape(clock)}\b";
            foreach (string statement in statements.Where(item => Regex.IsMatch(item, word, RegexOptions.CultureInvariant)))
            {
                Match own = definition.Match(statement);
                if (own.Success && own.Groups[1].Value == clock)
                {
                    if (Regex.IsMatch(own.Groups[3].Value, word, RegexOptions.CultureInvariant)) return false;
                    continue;
                }
                if (Regex.IsMatch(statement, $@"^{Regex.Escape(clock)} \*= step\(0\.0, [\w.]+\)$", RegexOptions.CultureInvariant)) continue;
                Match offset = Regex.Match(statement, $@"^{Regex.Escape(clock)} \+= (.+)$", RegexOptions.CultureInvariant);
                if (offset.Success)
                {
                    string addend = offset.Groups[1].Value;
                    if (Regex.IsMatch(addend, word, RegexOptions.CultureInvariant) || Regex.IsMatch(addend, @"\bg_Time\b", RegexOptions.CultureInvariant)) return false;
                    continue;
                }
                if (Regex.IsMatch(statement.Replace($"sin({clock})", "", StringComparison.Ordinal), word, RegexOptions.CultureInvariant)) return false;
            }
        }
        double k = clocks.Values.First();
        if (!TryScalar(pass, "speed", out double speed, out int valueIndex, out string numericText))
        {
            unresolved.Add(new(ownerId, effectIndex, passIndex, shaderResource, ShaderTemporalUnresolvedKind.MissingOrInvalidSpeed,
                "Verified structured sine clock needs a finite scalar 'speed' constant."));
            return true;
        }
        if (speed == 0) return true;
        string unit = k == 1 ? "" : " * M_PI";
        components.Add(new(new($"shader/{ownerId}/{effectIndex}/{passIndex}/speed",
            new CommonLoopPeriod(2 * Math.PI / (k * Math.Abs(speed)), CommonLoopPeriodEvidence.Analytic), AllowRetime: true),
            new(ownerId, effectIndex, passIndex, "speed", valueIndex, speed), shaderResource,
            $"Verified every g_Time use is float v = g_Time * g_Speed{unit} + static phase, and v reaches the output only through sin(v) " +
            $"after static offsets or a step(0, x) gate; period 2π/({(k == 1 ? "" : "π*")}abs(speed)) with source speed token {numericText}."));
        return true;
    }

    /// <summary>
    /// 按 pass 的 combo 值挑出预处理分支后的源码。只认 <c>#if NAME</c>、<c>#if NAME == N</c>、<c>#if NAME != N</c>、
    /// <c>#else</c>、<c>#endif</c>。combo 值来自 pass，缺省时取 shader 里 <c>// [COMBO]</c> 声明的 default；
    /// 值读不出（Unreadable）返回 null。值未知（既没写也没声明，例如随纹理槽自动定义的 MASK）或指令不认识
    /// （#elif、#ifdef …）的块，所有分支都保留，调用方的判据必须对任一分支组合成立。
    /// </summary>
    private static string? SelectComboBranches(string shaderText, JsonObject pass)
    {
        var declared = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(shaderText, @"//\s*\[COMBO\]\s*(?<json>\{[^\r\n]*\})", RegexOptions.CultureInvariant))
        {
            try
            {
                if (JsonNode.Parse(match.Groups["json"].Value) is JsonObject combo && combo["combo"] is JsonValue name &&
                    name.TryGetValue(out string? comboName) && comboName is not null &&
                    combo["default"] is JsonValue fallback && fallback.TryGetValue(out int value))
                    declared.TryAdd(comboName, value);
            }
            catch (System.Text.Json.JsonException) { }
        }
        var output = new StringBuilder();
        // 每层 #if 记三态：true/false 为已知条件，null 为未知（两边都保留）；else 标记当前在不在 #else 分支里。
        var frames = new Stack<(bool? Condition, bool Else)>();
        foreach (string rawLine in shaderText.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.StartsWith('#'))
            {
                Match ifMatch = Regex.Match(line, @"^#if\s+(\w+)\s*(?:(==|!=)\s*(-?\d+))?\s*$", RegexOptions.CultureInvariant);
                if (ifMatch.Success)
                {
                    string name = ifMatch.Groups[1].Value;
                    int? value;
                    switch (ReadCombo(pass, name, out int authored))
                    {
                        case ComboRead.Value: value = authored; break;
                        case ComboRead.Unreadable: return null;
                        default: value = declared.TryGetValue(name, out int fallback) ? fallback : null; break;
                    }
                    bool? condition = value is not int known ? null : !ifMatch.Groups[2].Success ? known != 0
                        : ifMatch.Groups[2].Value == "==" ? known == int.Parse(ifMatch.Groups[3].Value, CultureInfo.InvariantCulture)
                        : known != int.Parse(ifMatch.Groups[3].Value, CultureInfo.InvariantCulture);
                    frames.Push((condition, false));
                    continue;
                }
                if (Regex.IsMatch(line, @"^#(?:if|ifdef|ifndef)\b", RegexOptions.CultureInvariant)) { frames.Push((null, false)); continue; }
                if (Regex.IsMatch(line, @"^#else\b", RegexOptions.CultureInvariant) && frames.Count > 0)
                {
                    var top = frames.Pop();
                    frames.Push((top.Condition, true));
                    continue;
                }
                // #elif 把该层降为未知：之前与之后的分支都保留。
                if (Regex.IsMatch(line, @"^#elif\b", RegexOptions.CultureInvariant) && frames.Count > 0)
                {
                    frames.Pop();
                    frames.Push((null, false));
                    continue;
                }
                if (Regex.IsMatch(line, @"^#endif\b", RegexOptions.CultureInvariant))
                {
                    if (frames.Count == 0) return null;
                    frames.Pop();
                    continue;
                }
            }
            if (frames.All(frame => frame.Condition is not bool condition || condition != frame.Else)) output.Append(rawLine).Append('\n');
        }
        return frames.Count == 0 ? output.ToString() : null;
    }

    private static void AddAutoSway(int ownerId, int effectIndex, int passIndex, JsonObject pass, string shaderResource,
        string shaderText, List<ShaderPeriodComponent> components, List<ShaderTemporalUnresolved> unresolved)
    {
        if (HasEnabledNoise(pass, shaderText))
        {
            unresolved.Add(new(ownerId, effectIndex, passIndex, shaderResource, ShaderTemporalUnresolvedKind.NonPeriodicOrDriftingMechanism,
                "Auto-sway noise is enabled, so the simple speed period does not prove closure."));
            return;
        }
        if (!TryScalar(pass, "speed", out double speed, out int valueIndex, out string numericText) || speed == 0 ||
            !TryReciprocalRational(numericText, out CommonLoopRational exactPeriod))
        {
            unresolved.Add(new(ownerId, effectIndex, passIndex, shaderResource, ShaderTemporalUnresolvedKind.MissingOrInvalidSpeed,
                "Verified auto-sway timing needs a nonzero decimal scalar 'speed' to establish an exact fixed period."));
            return;
        }
        var patch = new ShaderSpeedPatch(ownerId, effectIndex, passIndex, "speed", valueIndex, speed);
        string id = $"shader/{ownerId}/{effectIndex}/{passIndex}/speed";
        var period = new CommonLoopPeriod(exactPeriod.ToSeconds(), CommonLoopPeriodEvidence.Analytic, exactPeriod);
        components.Add(new(new(id, period, AllowRetime: false), patch, shaderResource,
            $"Verified sin((g_Time * g_Speed + offset) * M_PI_2) in {shaderResource}; period is 1/abs({numericText})."));
    }

    private static bool HasEnabledNoise(JsonObject pass, string shaderText)
    {
        if (!shaderText.Contains("#if NOISE", StringComparison.Ordinal)) return false;
        return ComboEnabledOrUnproven(pass, "NOISE", false);
    }

    /// <summary>combo 读取的三态结果。</summary>
    private enum ComboRead
    {
        /// <summary>该 combo 有值且解析成了整数。</summary>
        Value,
        /// <summary>pass 上没有这个 combo，调用方按 shader 声明的默认值处理。</summary>
        Absent,
        /// <summary>该 combo 有值但解析不出整数语义，调用方必须按未证明处理。</summary>
        Unreadable
    }

    /// <summary>
    /// 读取一个 combo 的整数值。WE 编辑器把 combo 绑定到 bool 用户属性时会写出 JSON true/false，
    /// 冻结后的场景里同一个 combo 也可能是 1.0 或 "1"，因此这里依次尝试 int、bool、整值 double 和
    /// 十进制字符串。任何解析不出来的值都返回 <see cref="ComboRead.Unreadable"/>，绝不静默当成 0。
    /// </summary>
    private static ComboRead ReadCombo(JsonObject pass, string name, out int value)
    {
        value = 0;
        if (pass["combos"]?[name] is not JsonNode node) return ComboRead.Absent;
        if (node is not JsonValue scalar) return ComboRead.Unreadable;
        if (scalar.TryGetValue<int>(out int parsed)) { value = parsed; return ComboRead.Value; }
        if (scalar.TryGetValue<bool>(out bool flag)) { value = flag ? 1 : 0; return ComboRead.Value; }
        if (scalar.TryGetValue<double>(out double number) && double.IsFinite(number) &&
            number == Math.Truncate(number) && number >= int.MinValue && number <= int.MaxValue)
        {
            value = (int)number;
            return ComboRead.Value;
        }
        if (scalar.TryGetValue<string>(out string? text) && text is not null &&
            int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
        {
            value = parsed;
            return ComboRead.Value;
        }
        return ComboRead.Unreadable;
    }

    /// <summary>
    /// 判定一个 combo 分支是否"启用或未证明关闭"。缺省时用 shader 声明的默认值，
    /// 值读不出来时一律返回 true，让调用方标 unresolved 而不是当成分支关闭。
    /// </summary>
    private static bool ComboEnabledOrUnproven(JsonObject pass, string name, bool defaultEnabled)
        => ReadCombo(pass, name, out int value) switch
        {
            ComboRead.Value => value != 0,
            ComboRead.Absent => defaultEnabled,
            _ => true,
        };

    private static JsonObject EffectiveAnalysisPass(JsonObject materialPass, JsonObject definitionPass, JsonObject? authoredPass)
    {
        // Constants stay authored: every emitted rate patch must have a source-scene value to verify and replace.
        JsonObject result = authoredPass?.DeepClone().AsObject() ?? new JsonObject();
        var combos = new JsonObject();
        var textures = new JsonArray();
        Merge(materialPass); Merge(definitionPass); if (authoredPass is not null) Merge(authoredPass);
        if (combos.Count > 0) result["combos"] = combos;
        else result.Remove("combos");
        if (textures.Count > 0) result["textures"] = textures;
        else result.Remove("textures");
        return result;

        void Merge(JsonObject source)
        {
            if (source["combos"] is JsonObject values)
                foreach ((string key, JsonNode? value) in values) combos[key] = value?.DeepClone();
            if (source["textures"] is not JsonArray slots) return;
            while (textures.Count < slots.Count) textures.Add(null);
            for (int index = 0; index < slots.Count; ++index)
                if (slots[index] is JsonValue slot && slot.TryGetValue<string>(out string? texture) && texture.Length > 0)
                    textures[index] = texture;
        }
    }

    private static bool TryReadMaterialShader(ProjectSource source, string? assetsDirectory, string resource,
        out string shader, out JsonObject pass, out string reason)
    {
        shader = "";
        pass = new JsonObject();
        reason = "";
        try
        {
            JsonObject material = SceneAnalyzer.ReadResourceJson(source, assetsDirectory, resource);
            pass = material["passes"]?.AsArray().FirstOrDefault()?.AsObject() ?? new JsonObject();
            shader = pass["shader"]?.GetValue<string>() ?? "";
            if (!string.IsNullOrWhiteSpace(shader)) return true;
            reason = "Material has no shader entry.";
        }
        catch (Exception error) when (error is IOException or InvalidDataException)
        {
            reason = "Material could not be read: " + error.Message;
        }
        return false;
    }

    private static bool HasRepeatCloudTexture(ProjectSource source, string? assetsDirectory)
    {
        const string resource = "materials/util/clouds_256.tex";
        try
        {
            byte[] header;
            if (source.Contains(resource)) header = source.Read(resource, 1024);
            else
            {
                if (assetsDirectory is null) return false;
                string path = ProjectSource.ContainedPath(assetsDirectory, resource);
                if (!File.Exists(path)) return false;
                using var input = File.OpenRead(path);
                header = new byte[Math.Min(64, checked((int)input.Length))];
                input.ReadExactly(header);
            }
            return header.Length >= 26 && header.AsSpan(0, 8).SequenceEqual("TEXV0005"u8) &&
                header.AsSpan(9, 8).SequenceEqual("TEXI0001"u8) && (BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(22, 4)) & 0x2) == 0;
        }
        catch (Exception error) when (error is IOException or InvalidDataException) { return false; }
    }

    private static bool TryReadShader(ProjectSource source, string? assetsDirectory, string shader, out string resource, out string text, out string reason)
    {
        string baseResource = "shaders/" + shader;
        resource = baseResource + ".frag";
        text = "";
        reason = "";
        try
        {
            bool fragment = TryReadShaderStage(source, assetsDirectory, resource, out string fragmentText);
            string vertexResource = baseResource + ".vert";
            bool vertex = TryReadShaderStage(source, assetsDirectory, vertexResource, out string vertexText);
            if (!fragment && !vertex) { reason = "Neither fragment nor vertex shader source is available."; return false; }
            resource = vertex ? resource + " + " + vertexResource : resource;
            text = fragmentText + "\n" + vertexText;
            return true;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or DecoderFallbackException)
        {
            reason = "Shader could not be read: " + error.Message;
            return false;
        }
    }

    internal static bool TryReadShaderStage(ProjectSource source, string? assetsDirectory, string resource, out string text)
    {
        text = "";
        if (source.Contains(resource)) { text = Encoding.UTF8.GetString(source.Read(resource)); return true; }
        if (assetsDirectory is null) return false;
        string path = ProjectSource.ContainedPath(assetsDirectory, resource);
        if (!File.Exists(path)) return false;
        if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new InvalidDataException("Shader source exceeds the analysis limit.");
        text = File.ReadAllText(path, Encoding.UTF8);
        return true;
    }

    private static bool TryScalar(JsonObject pass, string key, out double value, out int valueIndex, out string numericText)
    {
        value = 0;
        valueIndex = 0;
        numericText = "";
        JsonNode? node = pass["constantshadervalues"]?[key];
        if (node is JsonArray values)
        {
            if (values.Count != 1 || values[0] is null) return false;
            node = values[0];
        }
        if (node is not JsonValue scalar || !scalar.TryGetValue<double>(out value) || !double.IsFinite(value)) return false;
        numericText = scalar.ToJsonString();
        return true;
    }

    private static bool TryVec2(JsonObject pass, string key, out double x, out double y)
    {
        x = y = 0;
        JsonNode? node = pass["constantshadervalues"]?[key];
        if (node is JsonArray vector && vector.Count == 2 && vector[0] is JsonValue first && vector[1] is JsonValue second &&
            first.TryGetValue<double>(out x) && second.TryGetValue<double>(out y)) return double.IsFinite(x) && double.IsFinite(y);
        if (node is JsonValue scalar && scalar.TryGetValue<string>(out string? text))
        {
            string[] terms = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return terms.Length == 2 && double.TryParse(terms[0], CultureInfo.InvariantCulture, out x) &&
                double.TryParse(terms[1], CultureInfo.InvariantCulture, out y) && double.IsFinite(x) && double.IsFinite(y);
        }
        return false;
    }

    private static bool TryReciprocalRational(string numericText, out CommonLoopRational period)
    {
        period = default;
        if (!decimal.TryParse(numericText, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal speed) || speed == 0) return false;
        speed = decimal.Abs(speed);
        // WPE stores material scalars as float32, so authored simple values commonly arrive as
        // 0.18000001. A bounded rational recovery avoids turning that representation residue into
        // a billion-frame exact-period constraint while refusing broad numerical approximation.
        if (TrySimpleRational((double)speed, out CommonLoopRational simple))
        {
            period = new CommonLoopRational(simple.Denominator, simple.Numerator);
            return true;
        }
        int scale = (decimal.GetBits(speed)[3] >> 16) & 0x7f;
        decimal multiplier = 1;
        for (int i = 0; i < scale; ++i) multiplier *= 10;
        decimal numerator = speed * multiplier;
        if (numerator != decimal.Truncate(numerator) || numerator > long.MaxValue || multiplier > long.MaxValue) return false;
        try { period = new CommonLoopRational((long)multiplier, (long)numerator); return true; }
        catch (ArgumentOutOfRangeException) { return false; }
    }

    private static bool TrySimpleRational(double value, out CommonLoopRational rational)
    {
        rational = default;
        for (long denominator = 1; denominator <= 1000; ++denominator)
        {
            long numerator = checked((long)Math.Round(value * denominator, MidpointRounding.AwayFromZero));
            if (numerator <= 0) continue;
            double candidate = (double)numerator / denominator;
            if (Math.Abs(candidate - value) <= 2e-7 * Math.Max(1, Math.Abs(value)))
            {
                rational = new CommonLoopRational(numerator, denominator);
                return true;
            }
        }
        return false;
    }

    private static bool TryRationalGreatestCommonDivisor(CommonLoopRational left, CommonLoopRational right,
        out CommonLoopRational result)
    {
        result = default;
        try
        {
            long denominatorGcd = GreatestCommonDivisor(left.Denominator, right.Denominator);
            long denominator = checked(left.Denominator / denominatorGcd * right.Denominator);
            long leftNumerator = checked(left.Numerator * (denominator / left.Denominator));
            long rightNumerator = checked(right.Numerator * (denominator / right.Denominator));
            result = new CommonLoopRational(GreatestCommonDivisor(leftNumerator, rightNumerator), denominator);
            return true;
        }
        catch (OverflowException) { return false; }
    }

    private static long GreatestCommonDivisor(long left, long right)
    {
        while (right != 0) (left, right) = (right, left % right);
        return left;
    }
}
