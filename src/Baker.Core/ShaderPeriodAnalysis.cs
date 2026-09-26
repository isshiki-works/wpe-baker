using System.Globalization;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Baker.Core.Analysis.ShaderClock;

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
    /// <summary>waterripple 滚动开且方向非零：两次查表速率比含 sin(方向)，是无理数，没有精确周期（位移同样有上界）。</summary>
    public const string WaterRippleIncommensurateScrollMechanism = "uv_incommensurate_scroll";

    /// <summary>lightshafts.frag:101-102 两次噪声查表的四条 UV 速率系数，实际速率是它们各自乘以 rayspeed（每秒）。</summary>
    public static readonly double[] LightShaftDriftRates = [0.003, 0.000375111, 0.0047111, 0.0007399];

    /// <summary>
    /// 四条速率同时回到整数纹理圈所需的 rayspeed·t：上面四个十进制常量的公共分母决定了它是 1e9 个单位，
    /// 因此联合周期 = 1e9 / rayspeed 秒。
    /// </summary>
    public const double LightShaftJointRepeatUnits = 1e9;

    private static ClockRuleTable Table => ClockRuleTable.Default;

    /// <param name="loopCeilingSeconds">循环时长上限（秒，= --loop-max-seconds，缺省 600），只进拒绝文案，不改判定。</param>
    /// <param name="maximumRetimePercent">
    /// 本次求解允许的单个分量最大调速（百分比，0–10）：即 <see cref="LoopAnalysis"/> 交给求解器的同一个数
    /// （档位预算 RetimeProfile.CommonRetimePercent：效率 5、平衡 3、质量无预算时回落到请求的 2，关通用调速时 0；--retime-budget 覆盖）。
    /// 缺省 2 与 HybridLoopService.Analyze、CommonLoopRequest 的缺省一致。
    /// </param>
    public static ShaderPeriodAnalysisResult Analyze(JsonObject scene, ProjectSource source, string? assetsDirectory,
        IReadOnlyCollection<int> selectedLayerIds, double? loopCeilingSeconds = null, double maximumRetimePercent = DefaultRetimePercent)
    {
        double ceiling = loopCeilingSeconds ?? CommonLoopSolver.DefaultMaximumSeconds;
        if (!double.IsFinite(maximumRetimePercent) || maximumRetimePercent < 0 || maximumRetimePercent > RetimeProfile.MaximumCommonRetimePercent)
            throw new ArgumentOutOfRangeException(nameof(maximumRetimePercent), "Retiming must be between zero and ten percent.");
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
            if (!selected.Contains(ownerId)) continue;
            AnalyzeBaseMaterial(owner, ownerId, objectsById, source, assetsDirectory, ceiling, maximumRetimePercent, components, unresolved, ruled);
            if (owner["effects"] is not JsonArray effects) continue;
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
                    // 时钟预筛按"被用到"判：去注释后的整词，且不算 uniform 声明。只声明不用的时钟不是时钟输入
                    // （例如 glitter_combine 声明了 g_Time 却不读它），不进规则链，也就不会落到"未证明周期"。
                    // 用到备用时钟的 pass 不进规则链，所以规则链里原文视图上的备用时钟检查都是多余的。
                    var shaderSource = new ShaderSource(shaderText);
                    bool wallClock = shaderSource.Uses("g_Time");
                    bool alternateClock = AlternateClocks.Any(shaderSource.Uses);
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
                    ShaderVerdict verdict = Judge(new(ownerId, effectIndex, authoredPassIndex, effectivePass, shader, shaderResource,
                        shaderSource, owner, objectsById, source, assetsDirectory, ceiling, maximumRetimePercent));
                    components.AddRange(verdict.Components);
                    unresolved.AddRange(verdict.Unresolved);
                }
            }
        }
        return new(components, unresolved, ruled);
    }

    /// <summary>一个进入规则链的 pass：定位、合并后的分析 pass、索引过的源码，以及个别规则要的场景上下文。</summary>
    private sealed record PassContext(int OwnerId, int EffectIndex, int PassIndex, JsonObject Pass, string Shader, string Resource,
        ShaderSource Source, JsonObject Owner, IReadOnlyDictionary<int, JsonObject> ObjectsById, ProjectSource Project, string? Assets,
        double Ceiling, double RetimePercent)
    {
        /// <summary>这是图层自身的基底材质，不是作者效果：常量在材质资源文件里，没有可改写的捕获场景值。</summary>
        public bool BaseMaterial { get; init; }

        public ShaderVerdict Refuse(ShaderTemporalUnresolvedKind kind, string detail, bool bounded = false, string mechanism = "",
            SwayModel? sway = null) =>
            ShaderVerdict.Of(new ShaderTemporalUnresolved(OwnerId, EffectIndex, PassIndex, Resource, kind, detail, bounded, mechanism, sway));

        public string Id(string suffix) => $"shader/{OwnerId}/{EffectIndex}/{PassIndex}/{suffix}";

        public ShaderSpeedPatch Patch(string key, int index, double value, double exponent = 1) =>
            new(OwnerId, EffectIndex, PassIndex, key, index, value, exponent);
    }

    /// <summary>按规则表顺序试规则：指纹全部成立才交给处理器，处理器返回 null 表示不认领；都不认领就判未证明周期。</summary>
    private static ShaderVerdict Judge(PassContext c)
    {
        foreach (ClockRule rule in Table.Rules)
        {
            if (!rule.Match.All(name => Table.Matches(name, c.Source))) continue;
            if (Dispatch(c, rule) is ShaderVerdict verdict) return verdict;
        }
        return ShaderVerdict.Of(new ShaderTemporalUnresolved(c.OwnerId, c.EffectIndex, c.PassIndex, c.Resource,
            ShaderTemporalUnresolvedKind.UnsupportedShaderMechanism, new Message("unresolved.shader_not_verified_periodic")));
    }

    /// <summary>
    /// 图层自身的基底材质（image → model → material 的第一个 pass）只试规则表里标了 base_material 的规则：
    /// 这些规则只产出锁定分量（常量在材质资源文件里，捕获场景里没有可改写的值）。有规则认领才登记为已裁定，
    /// 运行时材质便不再以"未建模时钟"重复计一次；没有规则认领就什么都不做，仍由运行时材质那条路径判。
    /// </summary>
    private static void AnalyzeBaseMaterial(JsonObject owner, int ownerId, IReadOnlyDictionary<int, JsonObject> objectsById,
        ProjectSource source, string? assetsDirectory, double ceiling, double retimePercent,
        List<ShaderPeriodComponent> components, List<ShaderTemporalUnresolved> unresolved, HashSet<(int OwnerLayerId, string Shader)> ruled)
    {
        if (owner["image"] is not JsonValue image || !image.TryGetValue(out string? model) || string.IsNullOrWhiteSpace(model) ||
            TryReadResource(source, assetsDirectory, model)?["material"] is not JsonValue materialValue ||
            !materialValue.TryGetValue(out string? material) || string.IsNullOrWhiteSpace(material) ||
            !TryReadMaterialShader(source, assetsDirectory, material, out string shader, out JsonObject pass, out _) ||
            !TryReadShader(source, assetsDirectory, shader, out string resource, out string text, out _)) return;
        var shaderSource = new ShaderSource(text);
        var c = new PassContext(ownerId, BaseMaterialEffectIndex, 0, pass, shader, resource, shaderSource, owner, objectsById, source,
            assetsDirectory, ceiling, retimePercent) { BaseMaterial = true };
        foreach (ClockRule rule in Table.Rules)
        {
            if (rule.Data["base_material"] is not JsonValue flag || !flag.GetValue<bool>() ||
                !rule.Match.All(name => Table.Matches(name, shaderSource)) || Dispatch(c, rule) is not ShaderVerdict verdict) continue;
            ruled.Add((ownerId, shader));
            components.AddRange(verdict.Components);
            unresolved.AddRange(verdict.Unresolved);
            return;
        }
    }

    private static ShaderVerdict? Dispatch(PassContext c, ClockRule rule) =>
            rule.Action switch
            {
                "scalar_period" => ScalarPeriod(c, rule),
                "dual_waves" => DualWaterWave(c, rule),
                "repeat_noise" => CanonicalRepeatNoiseTranslation(c),
                "scroll" => Scroll(c),
                "shimmer" => LinearShimmer(c),
                "film_grain" => FrameFractionGrain(c),
                "light_shafts" => LightShaftRays(c),
                "caustics" => CausticsDrift(c),
                "iris" => IrisSaccade(c),
                "foliage_sway" => FoliageSway(c),
                "shadow_hash" => ShadowMaskHash(c),
                "auto_sway" => AutoSway(c, rule),
                "water_ripple" => WaterRipple(c),
                "structured_sine" => StructuredSineClock(c),
                "glitter" => Glitter(c),
                "frac_linear" => FracLinearClock(c),
                "literal_step" => LiteralStepClock(c),
                _ => throw new InvalidDataException($"Unknown shader clock action '{rule.Action}'."),
            };

    /// <summary>
    /// 规则表里的门控，按顺序求值：combo_off = 分支启用或未证明关闭就拒；combo_on = 分支关闭即无运动、值读不出就拒；
    /// constant_zero = 常量必须写明且为零。都通过返回 null。
    /// directive 是一行预处理指令的开头（如 "#if NOISE"），按预处理词法查（ShaderSource.HasDirective）；
    /// absent_enabled_unless / absent_enabled_if 是规则表里的指纹名，与规则的 match 同一套求值：
    /// combo 缺省值按 [COMBO] 注释解析后的 JSON 比（数字按数值，1 与 1.0 相同）。
    /// </summary>
    private static ShaderVerdict? Gates(PassContext c, ClockRule rule)
    {
        foreach (JsonObject gate in (rule.Data["gates"]?.AsArray() ?? []).OfType<JsonObject>())
        {
            string Text(string key) => gate[key]!.GetValue<string>();
            ShaderTemporalUnresolvedKind kind = Enum.Parse<ShaderTemporalUnresolvedKind>(Text("kind"));
            switch (Text("type"))
            {
                case "combo_off":
                    bool absentEnabled = gate["absent_enabled_unless"] is JsonValue unless &&
                        !Table.Matches(unless.GetValue<string>(), c.Source);
                    if ((gate["directive"] is not JsonValue directive || c.Source.HasDirective(directive.GetValue<string>())) &&
                        ComboEnabledOrUnproven(c.Pass, Text("combo"), absentEnabled))
                        return c.Refuse(kind, Text("detail"));
                    break;
                case "combo_on":
                    // combo 值可能是 WE 编辑器绑定 bool 用户属性后产出的 JSON true/false，也可能是 1.0 或 "1"；
                    // 读不出来时既不能当启用也不能当关闭，只能按未证明处理。
                    ComboRead read = ReadCombo(c.Pass, Text("combo"), out int value);
                    if (read == ComboRead.Unreadable) return c.Refuse(kind, Text("detail"));
                    bool enabled = read == ComboRead.Value ? value != 0 : Table.Matches(Text("absent_enabled_if"), c.Source);
                    // 时钟全在这个分支里，分支关闭就没有时钟。
                    if (!enabled) return ShaderVerdict.NoMotion;
                    break;
                case "constant_zero":
                    if (!TryScalar(c.Pass, Text("key"), out double amount, out _, out _))
                        return c.Refuse(ShaderTemporalUnresolvedKind.MissingOrInvalidSpeed, Text("missing"));
                    if (amount != 0) return c.Refuse(kind, Text("detail"));
                    break;
                case "repeat_texture":
                    // 线性漂移的 UV 只有落在 repeat 寻址的静止贴图上才回得来；pass 没写该槽时取 shader 注释里的默认贴图。
                    int slot = gate["slot"]!.GetValue<int>();
                    string? fallback = c.Source.UniformAnnotations.FirstOrDefault(u => u.Name == $"g_Texture{slot}").Annotation?["default"]
                        is JsonValue named && named.TryGetValue(out string? texture) ? texture : null;
                    if (!UsesStaticRepeatTexture(c.Pass, slot, c.Project, c.Assets, fallback))
                        return c.Refuse(kind, Text("detail"), mechanism: LightShaftDriftMechanism);
                    break;
                default: throw new InvalidDataException($"Unknown shader clock gate '{Text("type")}'.");
            }
        }
        return null;
    }

    /// <summary>
    /// 单个速度常量决定周期的规则：周期 = period_numerator / |speed|（"2pi" 即 2π），补丁改 speed_key 本身。
    /// zero 为 missing 时零速度按缺常量拒，为 static 时零速度即停住的时钟、没有运动。
    /// </summary>
    private static ShaderVerdict ScalarPeriod(PassContext c, ClockRule rule)
    {
        if (Gates(c, rule) is ShaderVerdict gated) return gated;
        string key = rule.Data["speed_uniform"] is JsonValue uniform
            ? MaterialKey(c.Source, uniform.GetValue<string>(), rule.Text("speed_key")) : rule.Text("speed_key");
        bool read = TryScalar(c.Pass, key, out double speed, out int valueIndex, out string numericText);
        if (!read || (speed == 0 && rule.Text("zero") == "missing"))
            return c.Refuse(ShaderTemporalUnresolvedKind.MissingOrInvalidSpeed, rule.Text("missing"));
        if (speed == 0) return ShaderVerdict.NoMotion;
        double numerator = rule.Data["period_numerator"] is JsonValue named && named.TryGetValue(out string? symbol)
            ? symbol == "2pi" ? 2 * Math.PI : throw new InvalidDataException($"Rule '{rule.Id}' has unknown period numerator '{symbol}'.")
            : rule.Data["period_numerator"]!.GetValue<double>();
        return ShaderVerdict.Of(new ShaderPeriodComponent(new(c.Id(rule.Text("component")),
            new CommonLoopPeriod(numerator / Math.Abs(speed), CommonLoopPeriodEvidence.Analytic), AllowRetime: true),
            c.Patch(key, valueIndex, speed), c.Resource,
            rule.Text("evidence").Replace("{resource}", c.Resource, StringComparison.Ordinal).Replace("{speed}", numericText, StringComparison.Ordinal)));
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
        var noiseSource = new ShaderSource(noiseText);
        var castSource = new ShaderSource(castText);
        bool canonicalNoise = Table.Matches("repeat_noise", noiseSource);
        bool castClock = Table.Matches("shine_cast_clock", castSource);
        // 读不出的 EDGES 值不能算成已证明的四方向变体，下面的 fourDirections 因此为 false。
        ComboRead edges = ReadCombo(castPass, "EDGES", out int edgesValue);
        bool fourDirections = Table.Matches("shine_cast_four_directions", castSource) &&
            (edges == ComboRead.Absent || (edges == ComboRead.Value && edgesValue == 4)) &&
            ((edges == ComboRead.Value && edgesValue == 4) || Table.Matches("shine_edges_default_four", castSource)) &&
            castClock;
        string castKey = MaterialKey(castSource, "g_Speed", "speed"), noiseKey = MaterialKey(noiseSource, "g_NoiseSpeed", "noisespeed");
        bool hasCastSpeed = TryScalar(castPass, castKey, out double speed, out int speedIndex, out string speedToken);
        bool staticCast = hasCastSpeed && speed == 0 && castClock;
        bool canonicalRepeatTexture = UsesDefaultRepeatCloudTexture(noisePass, source, assetsDirectory);
        if (!canonicalNoise || (!fourDirections && !staticCast) || !canonicalRepeatTexture)
        {
            unresolved.Add(new(ownerId, effectIndex, -1, noiseResource + " / " + castResource, ShaderTemporalUnresolvedKind.UnsupportedShaderMechanism,
                "Shine source, supported cast variant, or canonical repeat noise texture is not proven."));
            return true;
        }
        if ((!staticCast && (!hasCastSpeed || speed == 0)) ||
            !TryScalar(noisePass, noiseKey, out double noiseSpeed, out int noiseSpeedIndex, out string noiseToken) || noiseSpeed == 0 ||
            !TryScalar(noisePass, MaterialKey(noiseSource, "g_NoiseScale", "noisescale"), out double noiseScale, out _, out string scaleToken) || noiseScale <= 0)
        {
            unresolved.Add(new(ownerId, effectIndex, -1, noiseResource, ShaderTemporalUnresolvedKind.MissingOrInvalidSpeed,
                "Verified shine equations need a supported cast speed or static cast, nonzero noisespeed, and positive noisescale constants."));
            return true;
        }
        string prefix = $"shader/{ownerId}/{effectIndex}";
        if (!staticCast)
            components.Add(new(new(prefix + "/ray-speed", new CommonLoopPeriod(Math.PI / (2 * Math.Abs(speed)), CommonLoopPeriodEvidence.Analytic), true),
                new(ownerId, effectIndex, 1, castKey, speedIndex, speed), castResource,
                $"Verified four-direction rotateVec2(g_Time * g_Speed) kernel; period π/(2*abs({speedToken}))."));
        components.Add(new(new(prefix + "/noise-speed", new CommonLoopPeriod(2 / (Math.Abs(noiseSpeed) * noiseScale), CommonLoopPeriodEvidence.Analytic), true),
            new(ownerId, effectIndex, 0, noiseKey, noiseSpeedIndex, noiseSpeed), noiseResource,
            $"Verified full/half-speed canonical repeat UV sampling; period 2/(abs({noiseToken})*{scaleToken})." +
            (staticCast ? " Cast rotate clock is static (speed is zero)." : "")));
        return true;
    }

    private static ShaderVerdict CanonicalRepeatNoiseTranslation(PassContext c)
    {
        // 读不出的 NOISE 值按"未证明启用"处理，下面立即落到 unresolved。
        bool noiseEnabled = ReadCombo(c.Pass, "NOISE", out int noiseValue) switch
        {
            ComboRead.Value => noiseValue == 1,
            ComboRead.Absent => Table.Matches("repeat_noise_default_on", c.Source),
            _ => false,
        };
        // NOISE 关闭（例如 godrays 降采样）：按 pass 的 combo 挑完分支后不再用到 g_Time，时钟全在关闭的分支里，pass 静止。
        // 值未知或指令不认识的块两支都保留，所以这是对任一分支组合都成立的证明。
        if (!noiseEnabled && SelectComboBranches(c.Source.Raw, c.Pass) is string selected && !new ShaderSource(selected).Uses("g_Time"))
            return ShaderVerdict.NoMotion;
        if (!noiseEnabled || !UsesDefaultRepeatCloudTexture(c.Pass, c.Project, c.Assets))
            return c.Refuse(ShaderTemporalUnresolvedKind.UnsupportedShaderMechanism,
                "Canonical repeat noise requires the enabled default clouds_256 texture with verified repeat addressing.");
        string speedKey = MaterialKey(c.Source, "g_NoiseSpeed", "noisespeed");
        if (!TryScalar(c.Pass, speedKey, out double speed, out int valueIndex, out string speedToken) || speed == 0 ||
            !TryScalar(c.Pass, MaterialKey(c.Source, "g_NoiseScale", "noisescale"), out double scale, out _, out string scaleToken) || scale <= 0)
            return c.Refuse(ShaderTemporalUnresolvedKind.MissingOrInvalidSpeed,
                "Canonical repeat noise needs nonzero noisespeed and positive noisescale constants.");
        return ShaderVerdict.Of(new ShaderPeriodComponent(new(c.Id("noise-speed"),
            new CommonLoopPeriod(2 / (Math.Abs(speed) * scale), CommonLoopPeriodEvidence.Analytic), true),
            c.Patch(speedKey, valueIndex, speed), c.Resource,
            $"Verified full/half-speed canonical repeat UV sampling; period 2/(abs({speedToken})*{scaleToken})."));
    }

    // 槽 2 没写、或写明的就是默认的 util/clouds_256（新版编辑器会把默认贴图名写进场景），都是同一张贴图。
    private static bool UsesDefaultRepeatCloudTexture(JsonObject pass, ProjectSource source, string? assetsDirectory) =>
        (pass["textures"] is not JsonArray textures || textures.Count < 3 || textures[2] is null ||
            (textures[2] is JsonValue named && named.TryGetValue(out string? texture) && texture == "util/clouds_256")) &&
        HasRepeatCloudTexture(source, assetsDirectory);

    /// <summary>
    /// uniform 在 shader 注释里声明的材质键，即场景 constantshadervalues 里绑定它的键。工程 pkg 里内嵌的旧版官方 shader
    /// 用旧键（如 ui_editor_properties_speed），与现行版（speed）不同；没有注释时用 <paramref name="fallback"/>。
    /// </summary>
    private static string MaterialKey(ShaderSource source, string uniform, string fallback) =>
        source.UniformAnnotations.FirstOrDefault(item => item.Name == uniform).Annotation?["material"] is JsonValue value &&
        value.TryGetValue(out string? key) && !string.IsNullOrEmpty(key) ? key : fallback;

    private static ShaderVerdict? DualWaterWave(PassContext c, ClockRule rule)
    {
        ComboRead dualWaves = ReadCombo(c.Pass, rule.Text("combo"), out int dualWavesValue);
        if (dualWaves == ComboRead.Unreadable) return c.Refuse(ShaderTemporalUnresolvedKind.UnsupportedShaderMechanism, rule.Text("unreadable"));
        if (dualWaves != ComboRead.Value || dualWavesValue != 1) return null;
        // 分支已由 combo 认领：指纹不成立也是这条规则的裁定，而不是交给后面的规则。
        if (!Table.Matches(rule.Id, c.Source)) return c.Refuse(ShaderTemporalUnresolvedKind.UnsupportedShaderMechanism, rule.Text("mismatch"));
        if (!TryScalar(c.Pass, "speed", out double speed1, out int speedIndex1, out string token1) || speed1 == 0 ||
            !TryScalar(c.Pass, "speed2", out double speed2, out int speedIndex2, out string token2) || speed2 == 0 ||
            !TryScalar(c.Pass, "offset2", out double offset2, out int offsetIndex, out _) ||
            !TrySimpleRational(Math.Abs(speed1), out CommonLoopRational rational1) ||
            !TrySimpleRational(Math.Abs(speed2), out CommonLoopRational rational2) ||
            !TryRationalGreatestCommonDivisor(rational1, rational2, out CommonLoopRational jointSpeed))
            return c.Refuse(ShaderTemporalUnresolvedKind.MissingOrInvalidSpeed,
                "Dual water waves need nonzero simple-rational speeds and a finite authored offset2 to establish one joint period.");
        var period = new CommonLoopPeriod(2 * Math.PI / jointSpeed.ToSeconds(), CommonLoopPeriodEvidence.Analytic);
        return ShaderVerdict.Of(
            new ShaderPeriodComponent(new(c.Id("speed"), period, AllowRetime: true), c.Patch("speed", speedIndex1, speed1), c.Resource,
                $"Verified dual sine product with simple-rational speeds {token1} and {token2}; joint period is 2π/gcd(abs(speed),abs(speed2))."),
            new ShaderPeriodComponent(new(c.Id("speed2"), period, AllowRetime: true),
                new(c.OwnerId, c.EffectIndex, c.PassIndex, "speed2", speedIndex2, speed2,
                    CompanionConstantKey: offset2 == 0 ? null : "offset2", CompanionValueIndex: offsetIndex,
                    CompanionOldValue: offset2 == 0 ? null : offset2), c.Resource,
                "The second speed shares the joint-period multiplier; a nonzero offset2 receives the inverse multiplier to preserve its initial phase."));
    }

    private static ShaderVerdict Scroll(PassContext c)
    {
        if (!TryVec2(c.Pass, "repeat", out double repeatX, out double repeatY) || repeatX <= 0 || repeatY <= 0)
            return c.Refuse(ShaderTemporalUnresolvedKind.MissingOrInvalidSpeed,
                "Verified scroll timing needs a static positive two-axis 'repeat' constant.");
        var components = new List<ShaderPeriodComponent>();
        var unresolved = new List<ShaderTemporalUnresolved>();
        AddScrollAxis("x", "speedx", repeatX);
        AddScrollAxis("y", "speedy", repeatY);
        return new(components, unresolved);

        void AddScrollAxis(string axis, string key, double repeat)
        {
            if (!TryScalar(c.Pass, key, out double speed, out int valueIndex, out string numericText))
            {
                unresolved.AddRange(c.Refuse(ShaderTemporalUnresolvedKind.MissingOrInvalidSpeed,
                    $"Verified scroll timing needs a scalar '{key}' constant.").Unresolved);
                return;
            }
            if (speed == 0) return;
            var period = new CommonLoopPeriod(1 / (speed * speed * repeat), CommonLoopPeriodEvidence.Analytic);
            components.Add(new(new(c.Id($"scroll-{axis}"), period, AllowRetime: true),
                c.Patch(key, valueIndex, speed, exponent: 2), c.Resource,
                $"Verified frac((uv + sign(speed)*speed²*time)*repeat) scroll axis {axis}; speed token is {numericText}."));
        }
    }

    private static ShaderVerdict LinearShimmer(PassContext c)
    {
        // 读不出的 MODE 值不能算成已证明的线性默认分支。
        bool modeZero = ReadCombo(c.Pass, "MODE", out int shimmerMode) switch
        {
            ComboRead.Value => shimmerMode == 0,
            ComboRead.Absent => Table.Matches("shimmer_default_mode", c.Source),
            _ => false,
        };
        if (!modeZero)
            return c.Refuse(ShaderTemporalUnresolvedKind.UnsupportedShaderMechanism, "Shimmer MODE is not the verified linear default branch.");
        if (!TryScalar(c.Pass, "ui_editor_properties_speed", out double speed, out int speedIndex, out string speedToken) || speed == 0 ||
            !TryScalar(c.Pass, "ui_editor_properties_granularity", out double scale, out _, out string scaleToken) || scale <= 0 ||
            !TryScalar(c.Pass, "ui_editor_properties_delay", out double delay, out _, out string delayToken) || delay <= 0)
            return c.Refuse(ShaderTemporalUnresolvedKind.MissingOrInvalidSpeed,
                "Verified linear shimmer timing needs nonzero speed and positive granularity and delay constants.");
        return ShaderVerdict.Of(new ShaderPeriodComponent(new(c.Id("speed"),
            new CommonLoopPeriod(Math.Abs(scale * delay / speed), CommonLoopPeriodEvidence.Analytic), AllowRetime: true),
            c.Patch("ui_editor_properties_speed", speedIndex, speed), c.Resource,
            $"Verified linear MODE=0 shimmer frac coordinate; period is abs({scaleToken}*{delayToken}/{speedToken})."));
    }

    // filmgrain.vert:25-28 wraps the only clock use in frac(g_Time) and derives both noise
    // coordinates from that single scalar, so the whole pass repeats once per second. The rate is
    // a shader literal: no material constant exists, hence the empty patch key and no retiming.
    private static ShaderVerdict FrameFractionGrain(PassContext c)
    {
        var exact = new CommonLoopRational(1);
        return ShaderVerdict.Of(new ShaderPeriodComponent(new(c.Id("frame-fraction"),
            new CommonLoopPeriod(exact.ToSeconds(), CommonLoopPeriodEvidence.Analytic, exact), AllowRetime: false),
            c.Patch("", 0, 0), c.Resource,
            "Verified film-grain clock: frac(g_Time) is the only g_Time use and both noise coordinates are functions of it, " +
            "so the exact period is one second. The rate is a shader literal, so the pass has no retimable material constant."));
    }

    // lightshafts.frag:101-102 translates two noise lookups at four different rates. Even granting
    // repeat addressing on the noise texture, the axes close together only after an astronomical
    // time, so the pass has no loop that a bounded retime can reach.
    private static ShaderVerdict LightShaftRays(PassContext c)
    {
        if (!TryScalar(c.Pass, "rayspeed", out double speed, out _, out string numericText))
            return c.Refuse(ShaderTemporalUnresolvedKind.MissingOrInvalidSpeed,
                "Verified light-shaft drift needs a finite scalar 'rayspeed' constant to state its period.");
        if (speed == 0) return ShaderVerdict.NoMotion;
        double rate = Math.Abs(speed);
        // The four UV rates are rayspeed * (0.003, 0.000375111, 0.0047111, 0.0007399) per second.
        // A shared return needs rayspeed * t * rate to be a whole texture repeat on every axis at
        // once; over those exact decimals the smallest such t is 1e9 / rayspeed seconds.
        double fastest = 1 / (LightShaftDriftRates.Max() * rate), slowest = 1 / (LightShaftDriftRates.Min() * rate),
            joint = LightShaftJointRepeatUnits / rate;
        return c.Refuse(ShaderTemporalUnresolvedKind.NonPeriodicOrDriftingMechanism,
            $"Light-shaft noise UVs translate at rayspeed {numericText} * (0.003, 0.000375111, 0.0047111, 0.0007399) per second. " +
            $"The fastest axis alone repeats only every {fastest.ToString("0.#", CultureInfo.InvariantCulture)} s and the slowest every " +
            $"{slowest.ToString("0.#", CultureInfo.InvariantCulture)} s; all four close together only after " +
            $"{joint.ToString("0.###E+00", CultureInfo.InvariantCulture)} s. Every rate is proportional to rayspeed, so a retime " +
            // 单轴周期可能落在上限内（rayspeed 1 时最快轴约 212 秒），说不回来的是四轴联合回归。
            $"scales them equally and cannot bring the joint return inside the {CeilingText(c.Ceiling)}-second loop ceiling.",
            // UV 偏移随 t 线性增长，没有幅度上界：接缝淡化盖不住它。
            bounded: false, mechanism: LightShaftDriftMechanism);
    }

    // caustics.frag 用 time = g_Time·speed + offset 平移四次噪声查表，速率 time·(0.005, 0.004111, 0.003777, 0.01)。
    // 同 lightshafts：四个十进制常量的公共分母使四轴同时回到整数纹理圈要 speed·t 为 1e6 的倍数，调速等比缩放，回不到上限内。
    private static ShaderVerdict CausticsDrift(PassContext c)
    {
        if (!TryScalar(c.Pass, "ui_editor_properties_speed", out double speed, out _, out string numericText))
            return c.Refuse(ShaderTemporalUnresolvedKind.MissingOrInvalidSpeed,
                "Verified caustics drift needs a finite scalar 'ui_editor_properties_speed' constant to state its period.");
        if (speed == 0) return ShaderVerdict.NoMotion;
        return c.Refuse(ShaderTemporalUnresolvedKind.NonPeriodicOrDriftingMechanism,
            $"Caustics noise UVs translate at speed {numericText} * (0.005, 0.004111, 0.003777, 0.01) per second; all four close together only after " +
            $"{(1e6 / Math.Abs(speed)).ToString("0.###E+00", CultureInfo.InvariantCulture)} s. Every rate is proportional to the speed, so a retime " +
            $"scales them equally and cannot bring the joint return inside the {CeilingText(c.Ceiling)}-second loop ceiling.",
            bounded: false, mechanism: LightShaftDriftMechanism);
    }

    // iris.vert:31-41 drives the eye from floor(g_Time * g_Speed + phase). The per-step offsets are
    // sin(1.9*n) and sin(2.5*n + c) at integer n, whose frequencies are irrational multiples of 2π.
    private static ShaderVerdict IrisSaccade(PassContext c) => c.Refuse(ShaderTemporalUnresolvedKind.NonPeriodicOrDriftingMechanism,
        "Iris motion is indexed by the integer step floor(g_Time * g_Speed + phase) and offset by sin(1.9 * step) and " +
        "sin(2.5 * step + c). Repeating those offsets needs 1.9 and 2.5 times a whole number of steps to be whole multiples " +
        "of 2*pi, which no integer satisfies because 1.9/(2*pi) and 2.5/(2*pi) are irrational. The step blend frac(time) " +
        "(period 1 in time) and the sin(time)/cos(time) terms (period 2*pi in time) are incommensurable for the same reason, " +
        "so no speed value and no retime give this pass an exact period.",
        // 眼球跳变的幅度由 moveStart/moveEnd 决定，这条规则不解析它，因此不声称有界。
        bounded: false, mechanism: IrisSaccadeMechanism);

    // foliagesway samples eight sines whose slowest term is speed * 0.000024801587 rad/s. Even at the
    // fastest speed the shader accepts, that single term needs hours, so no loop exists in range.
    private static ShaderVerdict? FoliageSway(PassContext c)
    {
        // 系数按 vec4 数值解析（接受 0 与已改写成按 g_Speed 分支的形态），两阶段必须一致；其余计数判据在
        // 去掉系数表达式后的全文上做，stock 文本的判定与旧的字面量匹配完全相同。
        if (!ShaderTextPatch.TryParseClockTerms(c.Source.Raw, out ShaderTextPatch.ClockTerms clock) ||
            !Table.Matches("foliage_sway_masked_clock", new ShaderSource(clock.MaskedText))) return null;
        JsonObject pass = c.Pass;
        double ceiling = c.Ceiling;
        // MODE 1 sways vertices with 'speed'; the default MODE 0 sways UVs with 'speeduv'.
        // 读不出 MODE 时两条幅度门控都无法证明，直接标 unresolved 而不是走下面的零振幅豁免。
        ComboRead modeRead = ReadCombo(pass, "MODE", out int mode);
        if (modeRead == ComboRead.Unreadable)
            return c.Refuse(ShaderTemporalUnresolvedKind.UnsupportedShaderMechanism,
                "Foliage sway MODE combo value is neither an integer nor a boolean, so neither its vertex nor its UV amplitude gate is proven.");
        bool vertexMode = modeRead == ComboRead.Value && mode == 1;
        string key = vertexMode ? "speed" : "speeduv";
        bool hasSpeed = TryScalar(pass, key, out double speed, out _, out string numericText);
        // Both stages scale the whole sine sum by strength before it reaches a UV or a vertex, and
        // the vertex stage additionally gates its two axes by directionweights. A zero amplitude, or
        // a stopped clock, leaves the pass with no motion at all, so it constrains no loop.
        if (hasSpeed && speed == 0) return ShaderVerdict.NoMotion;
        if (TryScalar(pass, "strength", out double strength, out _, out _) && strength == 0) return ShaderVerdict.NoMotion;
        if (vertexMode && TryVec2(pass, "directionweights", out double weightX, out double weightY) &&
            weightX == 0 && weightY == 0) return ShaderVerdict.NoMotion;
        SwayModel? model = BuildSwayModel(c, clock, vertexMode, key, hasSpeed ? speed : (double?)null);
        // 已改写的 shader 可能把这个速度的 8 项全冻结：没有运动，不约束循环。
        if (model is not null && model.Coefficients.All(value => value == 0)) return ShaderVerdict.NoMotion;
        bool canonical = clock.Sines.Branches.Count == 0 && clock.CoSines.Branches.Count == 0 &&
            clock.Sines.Fallback.Concat(clock.CoSines.Fallback).SequenceEqual(SwayModel.CanonicalCoefficients);
        if (!canonical)
        {
            string coefficients = model is null
                ? "coefficients that depend on g_Speed"
                : "(" + string.Join(", ", model.Coefficients.Select(value => value.ToString("R", CultureInfo.InvariantCulture))) + ")";
            return c.Refuse(ShaderTemporalUnresolvedKind.NonPeriodicOrDriftingMechanism,
                $"Foliage sway adds eight sines at {key} * {coefficients} rad/s. These are not the stock foliagesway literals, and this " +
                $"analysis establishes no common period for them within the {CeilingText(ceiling)}-second loop ceiling; a retime scales every term equally, " +
                "so this pass has no usable loop.",
                bounded: true, mechanism: FoliageSwayMechanism, sway: model);
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
        return c.Refuse(ShaderTemporalUnresolvedKind.NonPeriodicOrDriftingMechanism,
            $"Foliage sway adds eight sines at {key} * (1, -0.16161616, 0.0083333, -0.00019841, -0.5, 0.041666666, -0.0013888889, " +
            $"0.000024801587) rad/s. The slowest coefficient is 0.000024801587 with {rate}. Read as their nearest simple fractions " +
            $"(1, 16/99, 1/120, 1/5040, 1/2, 1/24, 1/720, 1/40320), which is the most generous reading available, the eight share " +
            $"the base 1/443520, so they return together only after 2*pi*443520/{key} s ({common}); the shipped literals are " +
            "truncations of those fractions and can only make the true return longer. " +
            (slowestTermSeconds > ceiling ? "Both bounds are" : "The common return is") +
            $" outside the {CeilingText(ceiling)}-second loop ceiling, and a retime scales every term equally, so this pass has no usable loop.",
            // 八项正弦和整体乘以 strength²·0.005 才进 UV 或顶点，峰值位移有解析上界，残差掩盖据此判定。
            bounded: true, mechanism: FoliageSwayMechanism, sway: model);
    }

    /// <summary>
    /// 摆动方程的结构化参数。缺省常量取 shader uniform 注释里的 default；速度读不出（非有限）时返回 null，
    /// 这条分量就只能照旧留作未解析项。图层尺寸与父链 scale 读不到时尺寸记 null、scale 记 1。
    /// </summary>
    private static SwayModel? BuildSwayModel(PassContext c, ShaderTextPatch.ClockTerms clock, bool vertexMode, string speedKey,
        double? authoredSpeed)
    {
        (int ownerId, int effectIndex, int passIndex, JsonObject pass, JsonObject owner) = (c.OwnerId, c.EffectIndex, c.PassIndex, c.Pass, c.Owner);
        IReadOnlyDictionary<string, JsonNode?> defaults = c.Source.UniformDefaults;
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
                    ? c.ObjectsById.GetValueOrDefault(parentId) : null;
            }
        }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException or FormatException)
        {
            (scaleX, scaleY) = (1, 1);
        }
        return new(ownerId, effectIndex, passIndex, c.Shader, vertexMode ? 1 : 0, speedKey, gpuSpeed, coefficients,
            Scalar("strength", 0.4), Scalar("ratio", 0.3), Scalar("scrolldirection", 0), Scalar("power", 1),
            weightX, weightY, width, height, scaleX, scaleY);
    }

    private static string CeilingText(double seconds) => seconds.ToString("0.###", CultureInfo.InvariantCulture);

    // The workshop CRT shadow-map noise is hash(round(uv) + g_Time) with frac(p * 0.1031) as its
    // first step, so it is exactly 10000/1031-second periodic; that period is a shader literal on
    // an awkward denominator, so it cannot be retimed onto the output frame grid.
    private static ShaderVerdict ShadowMaskHash(PassContext c)
    {
        // A zero static-noise amount removes the only clock term from the output.
        if (TryScalar(c.Pass, "Static noise", out double amount, out _, out _) && amount == 0) return ShaderVerdict.NoMotion;
        return c.Refuse(ShaderTemporalUnresolvedKind.UnsupportedShaderMechanism,
            "The static-noise hash reduces to frac((round(uv) + g_Time) * 0.1031), so its exact period is 10000/1031 s " +
            "(9.699321 s). 0.1031 is a shader literal with no material constant, so the period is fixed, and 10000/1031 s " +
            $"lands on the 60 fps frame grid only after 600000 frames (10000 s), past the {CeilingText(c.Ceiling)}-second loop ceiling. " +
            "Set the pass 'Static noise' amount to zero to remove the clock.");
    }

    // waterripple.vert（PERSPECTIVE 0）与 waterripple.frag（PERSPECTIVE 1）用同一组时间式平移两次法线贴图查表：
    //   c1 = (uv + t·a² + t·s²·d)·scale，c2 = (1.333·uv − t·a² + t·s²·d)·scale，之后 .xz 乘 W/H、.yw 乘 ratio；
    //   a = animationspeed，s = scrollspeed，d = rotateVec2(vec2(0, 1), dir) = (−sin dir, cos dir)，
    //   W×H 是效果输入 rt（g_Texture0Resolution.xy）。PERSPECTIVE 只换静态的 uv 与遮罩，不改时间项。
    // 法线贴图按 repeat 寻址时查表坐标平移整数圈结果不变。s = 0 时两条轴每秒平移 a²·|scale|·(W/H, |ratio|) 圈，
    // 同时回到整数圈的最小时长是 1/(a²·|scale|·gcd(W/H, |ratio|))。s ≠ 0 时两次查表、两条轴的速率比取决于
    // 方向的 sin/cos 与 a²、s² 的比，这里不建立周期。
    // 指纹（规则表 water_ripple）的计数穷尽每个时钟与查表坐标的出现：两阶段各一个 g_Time 声明加三处使用，两阶段各一条
    // scroll 定义加两处使用，查表坐标只有列出的赋值与两次法线贴图采样。多出任何一处都不再是这组方程。
    private static ShaderVerdict WaterRipple(PassContext c)
    {
        JsonObject pass = c.Pass;
        IReadOnlyDictionary<string, JsonNode?> defaults = c.Source.UniformDefaults;
        // 常量按 uniform 注释的材质键读（pkg 内嵌的旧版用 ui_editor_properties_* 旧键）。旧版没有 g_Ratio 时 y 轴不缩放，即 ratio = 1。
        string speedKey = MaterialKey(c.Source, "g_AnimationSpeed", "animationspeed");
        bool hasRatio = c.Source.Uses("g_Ratio");
        double ratio = 1;
        string ratioToken = "1 (no ratio uniform)";
        // animationspeed 必须是场景里写明的标量：改频要改写它。其余三个常量缺省时取 shader 声明的默认值。
        if (!RippleConstant(speedKey, authoredOnly: true, out double speed, out int speedIndex, out string speedToken) ||
            !RippleConstant(MaterialKey(c.Source, "g_ScrollSpeed", "scrollspeed"), authoredOnly: false, out double scrollSpeed, out _, out string scrollToken) ||
            !RippleConstant(MaterialKey(c.Source, "g_Scale", "scale"), authoredOnly: false, out double scale, out _, out string scaleToken) ||
            (hasRatio && !RippleConstant(MaterialKey(c.Source, "g_Ratio", "ratio"), authoredOnly: false, out ratio, out _, out ratioToken)))
            return c.Refuse(ShaderTemporalUnresolvedKind.MissingOrInvalidSpeed,
                "Verified water-ripple timing needs an authored finite scalar 'animationspeed' and finite 'scrollspeed', 'scale' and 'ratio' constants (authored or shader default).");
        double animation = speed * speed, drift = scrollSpeed * scrollSpeed;
        if (scale == 0 || (animation == 0 && drift == 0)) return ShaderVerdict.NoMotion;
        // 法线贴图槽：现行版在槽 2，旧版在槽 1（指纹已要求两次查表同槽）。
        int normalSlot = Regex.Match(c.Source.Normalized, @"texSample2D\(g_Texture(\d), (?:v_TexCoordRipple|rippleCoords)\.xy\)", RegexOptions.CultureInvariant)
            .Groups[1].Value is "1" ? 1 : 2;
        if (!UsesStaticRepeatTexture(pass, normalSlot, c.Project, c.Assets))
            return c.Refuse(ShaderTemporalUnresolvedKind.UnsupportedShaderMechanism,
                $"Water-ripple normal lookups repeat only when texture slot {normalSlot} is a still texture with repeat addressing; that is not proven for this pass.");
        // 滚动开、a ≠ 0、方向 d 是非零有限浮点（有理数）时：两次查表 x 轴速率比 (a² − s²·sin d)/(−a² − s²·sin d) 若是有理数，
        // sin d 就是有理数；而非零有理 d 的 sin d 是超越数（Lindemann–Weierstrass），所以永不同时回到整数圈，统一调速不改比值。
        if (drift != 0 && animation != 0 &&
            RippleConstant(MaterialKey(c.Source, "g_Direction", "scrolldirection"), authoredOnly: false, out double direction, out _, out string directionToken) && direction != 0)
            return c.Refuse(ShaderTemporalUnresolvedKind.NonPeriodicOrDriftingMechanism,
                $"Water-ripple scroll is on (scrollspeed {scrollToken}, scrolldirection {directionToken}, animationspeed {speedToken}). " +
                "Along x the two normal lookups translate at rates proportional to a² − s²·sin d and −a² − s²·sin d (a = animationspeed, " +
                "s = scrollspeed, d = scrolldirection). Their ratio is rational only if sin d is rational, but sin d is transcendental for every " +
                "nonzero rational d (Lindemann–Weierstrass), so the two lookups never return to whole texture repeats together: the pass has no " +
                "exact period, and a retime scales both rates equally.",
                bounded: true, mechanism: WaterRippleIncommensurateScrollMechanism);
        if (drift != 0)
            return c.Refuse(ShaderTemporalUnresolvedKind.UnsupportedShaderMechanism,
                $"Water-ripple scroll is on (scrollspeed {scrollToken}). Both normal lookups then also translate by t*scrollspeed²*(-sin, cos)(direction) " +
                "on top of the opposite ±t*animationspeed² terms, so the two lookups and the two texture axes move at rates whose ratios depend on " +
                "the direction's sine and cosine and on animationspeed² against scrollspeed². The verified water-ripple period covers scrollspeed 0 only; " +
                "no period is established for this pass.");
        if (!TryEffectTargetExtent(c.Owner, out long width, out long height))
            return c.Refuse(ShaderTemporalUnresolvedKind.UnsupportedShaderMechanism,
                "Water-ripple timing scales the x axis by the effect render-target aspect g_Texture0Resolution.x / g_Texture0Resolution.y. " +
                "This layer is fullscreen or has no readable size, so that aspect is not proven.");
        var aspect = new CommonLoopRational(width, height);
        CommonLoopRational step = aspect;
        if (ratio != 0 && (!TrySimpleRational(Math.Abs(ratio), out CommonLoopRational ratioRational) ||
            !TryRationalGreatestCommonDivisor(aspect, ratioRational, out step)))
            return c.Refuse(ShaderTemporalUnresolvedKind.MissingOrInvalidSpeed,
                $"Water-ripple x and y lookups return together only through gcd({width}/{height}, abs(ratio)); ratio {ratioToken} is not a simple rational, so no joint period is established.");
        double period = 1 / (animation * Math.Abs(scale) * step.ToSeconds());
        string equation = $"normal lookups translate ±t*animationspeed² with animationspeed {speedToken}, scale {scaleToken}, render target {width}/{height} " +
            $"and ratio {ratioToken}; both axes return to a whole texture repeat together every 1/(animationspeed²*abs(scale)*gcd(W/H, abs(ratio))) = " +
            $"{period.ToString("0.###", CultureInfo.InvariantCulture)} s";
        // 最大调速也够不进上限的精确周期不交给求解器：作为分量它只会让整张壁纸无候选，而作为未解析项，
        // 分配回退还能把这一层留实时、让其余图层照常找循环。
        // 上限是本次分析实际用的循环时长上限（--loop-max-seconds，含内嵌视频收紧），由 Analyze 传入，不取求解器缺省值；
        // 调速余量同理是本次求解实际允许的调速预算（档位预算），不再写死 2%。
        if (period > c.Ceiling * (1 + c.RetimePercent / 100))
            return c.Refuse(ShaderTemporalUnresolvedKind.NonPeriodicOrDriftingMechanism,
                $"Water-ripple scroll is off, so its {equation}. That is past the {CeilingText(c.Ceiling)}-second loop ceiling " +
                $"even with the largest retime this analysis allows ({CeilingText(c.RetimePercent)}%), and a retime scales both axes equally.",
                bounded: true, mechanism: WaterRippleScrollMechanism);
        return ShaderVerdict.Of(new ShaderPeriodComponent(new(c.Id("animationspeed"),
            new CommonLoopPeriod(period, CommonLoopPeriodEvidence.Analytic), AllowRetime: true),
            c.Patch(speedKey, speedIndex, speed, exponent: 2), c.Resource,
            $"Verified water-ripple normal scroll with scrollspeed 0: {equation}."));

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

    /// <summary>调用方没给调速预算时的缺省（百分比）：与 HybridLoopService.Analyze、CommonLoopRequest 的缺省 2% 一致。</summary>
    public const double DefaultRetimePercent = 2;

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
    private static bool UsesStaticRepeatTexture(JsonObject pass, int slot, ProjectSource source, string? assetsDirectory,
        string? fallback = null)
    {
        string? name = pass["textures"] is JsonArray textures && textures.Count > slot && textures[slot] is JsonValue value &&
            value.TryGetValue(out string? authored) ? authored : fallback;
        if (string.IsNullOrWhiteSpace(name)) return false;
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
    private static ShaderVerdict? StructuredSineClock(PassContext c)
    {
        if (SelectComboBranches(c.Source.Raw, c.Pass) is not string selected) return null;
        // 分支挑完后只允许 #include 残留；#define 之类可能改写标识符，出现就不裁定。
        string[] lines = selected.Split('\n');
        if (lines.Any(line => line.TrimStart().StartsWith('#') && !line.TrimStart().StartsWith("#include", StringComparison.Ordinal))) return null;
        string text = ShaderSource.Normalize(string.Join('\n', lines.Where(line => !line.TrimStart().StartsWith('#'))));
        if (ShaderSource.HasAlternateClock(text)) return null;
        string[] statements = text.Split([';', '{', '}'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var definition = new Regex(@"^float (\w+) = g_Time \* g_Speed( \* M_PI)? \+ (.+)$", RegexOptions.CultureInvariant);
        var clocks = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (string statement in statements.Where(item => Regex.IsMatch(item, @"\bg_Time\b", RegexOptions.CultureInvariant)))
        {
            if (statement == "uniform float g_Time") continue;
            Match match = definition.Match(statement);
            if (!match.Success || Regex.IsMatch(match.Groups[3].Value, @"\bg_Time\b", RegexOptions.CultureInvariant)) return null;
            double coefficient = match.Groups[2].Success ? Math.PI : 1;
            if (clocks.TryGetValue(match.Groups[1].Value, out double existing) && existing != coefficient) return null;
            clocks[match.Groups[1].Value] = coefficient;
        }
        // 没有时钟定义，或两个时钟系数不同（例如 STD_TIME 值未知、两条分支都留着）：不是这条规则能裁定的形态。
        if (clocks.Count == 0 || clocks.Values.Distinct().Count() != 1) return null;
        foreach (string clock in clocks.Keys)
        {
            string word = $@"\b{Regex.Escape(clock)}\b";
            foreach (string statement in statements.Where(item => Regex.IsMatch(item, word, RegexOptions.CultureInvariant)))
            {
                Match own = definition.Match(statement);
                if (own.Success && own.Groups[1].Value == clock)
                {
                    if (Regex.IsMatch(own.Groups[3].Value, word, RegexOptions.CultureInvariant)) return null;
                    continue;
                }
                if (Regex.IsMatch(statement, $@"^{Regex.Escape(clock)} \*= step\(0\.0, [\w.]+\)$", RegexOptions.CultureInvariant)) continue;
                Match offset = Regex.Match(statement, $@"^{Regex.Escape(clock)} \+= (.+)$", RegexOptions.CultureInvariant);
                if (offset.Success)
                {
                    string addend = offset.Groups[1].Value;
                    if (Regex.IsMatch(addend, word, RegexOptions.CultureInvariant) || Regex.IsMatch(addend, @"\bg_Time\b", RegexOptions.CultureInvariant)) return null;
                    continue;
                }
                if (Regex.IsMatch(statement.Replace($"sin({clock})", "", StringComparison.Ordinal), word, RegexOptions.CultureInvariant)) return null;
            }
        }
        double k = clocks.Values.First();
        if (!TryScalar(c.Pass, "speed", out double speed, out int valueIndex, out string numericText))
            return c.Refuse(ShaderTemporalUnresolvedKind.MissingOrInvalidSpeed, "Verified structured sine clock needs a finite scalar 'speed' constant.");
        if (speed == 0) return ShaderVerdict.NoMotion;
        string unit = k == 1 ? "" : " * M_PI";
        return ShaderVerdict.Of(new ShaderPeriodComponent(new(c.Id("speed"),
            new CommonLoopPeriod(2 * Math.PI / (k * Math.Abs(speed)), CommonLoopPeriodEvidence.Analytic), AllowRetime: true),
            c.Patch("speed", valueIndex, speed), c.Resource,
            $"Verified every g_Time use is float v = g_Time * g_Speed{unit} + static phase, and v reaches the output only through sin(v) " +
            $"after static offsets or a step(0, x) gate; period 2π/({(k == 1 ? "" : "π*")}abs(speed)) with source speed token {numericText}."));
    }

    /// <summary>基底材质在 pass 定位（effect_index）里用的下标：它不是任何作者效果。</summary>
    public const int BaseMaterialEffectIndex = -1;

    private static readonly string[] AlternateClocks = ["g_Runtime", "g_Frametime", "g_DeltaTime"];

    /// <summary>
    /// 官方 glitter_prepare：g_Time 只经 <c>time = g_Time * g_Speed * density</c>（density = g_Density²）进入
    /// <c>frac(静态噪声 * 100 + time)</c>，其后全是 timer0 的函数，所以严格以 1/|speed·density²| 为周期（指纹见规则表 glitter）。
    /// 补丁改 speed（指数 1）；density 没写时取 shader 注释默认值，它不参与补丁。
    /// </summary>
    private static ShaderVerdict Glitter(PassContext c)
    {
        if (!TryScalar(c.Pass, "speed", out double speed, out int speedIndex, out string speedToken))
            return c.Refuse(ShaderTemporalUnresolvedKind.MissingOrInvalidSpeed, "Verified glitter timing needs a finite scalar 'speed' constant.");
        if (!AuthoredOrDefault(c, "density", out double density, out string densityToken))
            return c.Refuse(ShaderTemporalUnresolvedKind.MissingOrInvalidSpeed, "Verified glitter timing needs a finite 'density' constant or shader default.");
        double rate = speed * density * density;
        if (rate == 0) return ShaderVerdict.NoMotion;
        double period = 1 / Math.Abs(rate);
        string equation = $"glitter twinkle: g_Time enters only through frac(static noise*100 + g_Time*speed*density²), so the pass repeats every " +
            $"1/abs(speed*density²) = {period.ToString("0.###", CultureInfo.InvariantCulture)} s with speed {speedToken} and density {densityToken}";
        if (period > c.Ceiling * (1 + c.RetimePercent / 100))
            return c.Refuse(ShaderTemporalUnresolvedKind.NonPeriodicOrDriftingMechanism,
                $"Verified {equation}. That is past the {CeilingText(c.Ceiling)}-second loop ceiling even with the largest retime this analysis allows ({CeilingText(c.RetimePercent)}%).");
        return ShaderVerdict.Of(new ShaderPeriodComponent(new(c.Id("speed"),
            new CommonLoopPeriod(period, CommonLoopPeriodEvidence.Analytic), AllowRetime: true),
            c.Patch("speed", speedIndex, speed), c.Resource, $"Verified {equation}."));
    }

    /// <summary>
    /// frac 线性时钟：去注释后每一处 g_Time（不算 uniform 声明）都恰好是 <c>frac(g_Time * U)</c> 或 <c>frac(g_Time * U + 静态项)</c>
    /// 的开头，全场只有一个速率 uniform U，静态项里没有别的 g_Time。输出只经这些 frac 值依赖时间，每个都以 1/|U| 为周期，
    /// 所以整个 pass 严格以 1/|U| 为周期（flowmap 两相、四相混合都是这个形态）。U 的材质键取 uniform 注释的 material。
    /// 作者效果上常量写在场景里时可调速（补丁改该键）；基底材质或常量没写（取 shader 默认值）时没有可改写的值，
    /// 作为锁定分量交出，这时要求十进制常量能恢复成精确有理周期。
    /// </summary>
    private static ShaderVerdict? FracLinearClock(PassContext c)
    {
        if (ClockCode(c, ["frac"]) is not string code) return null;
        var uses = Regex.Matches(code, @"\bg_Time\b", RegexOptions.CultureInvariant);
        var fracs = Regex.Matches(code, @"(?<![\w.])frac\(\s*g_Time\s*\*\s*(?<u>\w+)\s*(?:\)|\+)", RegexOptions.CultureInvariant);
        if (fracs.Count == 0 || fracs.Count != uses.Count || fracs.Select(m => m.Groups["u"].Value).Distinct().Count() != 1) return null;
        string uniform = fracs[0].Groups["u"].Value;
        if (c.Source.UniformAnnotations.FirstOrDefault(item => item.Name == uniform).Annotation?["material"] is not JsonValue keyValue ||
            !keyValue.TryGetValue(out string? key) || string.IsNullOrEmpty(key)) return null;
        bool authored = c.Pass["constantshadervalues"]?[key] is not null;
        int valueIndex = 0;
        double speed;
        string token;
        if (!(authored ? TryScalar(c.Pass, key, out speed, out valueIndex, out token) : AuthoredOrDefault(c, key, out speed, out token)))
            return c.Refuse(ShaderTemporalUnresolvedKind.MissingOrInvalidSpeed, $"Verified frac(g_Time * {uniform}) clock needs a finite scalar '{key}' constant.");
        if (speed == 0) return ShaderVerdict.NoMotion;
        double period = 1 / Math.Abs(speed);
        string equation = $"every g_Time use is frac(g_Time * {uniform} [+ static phase]) with material '{key}' {token}, so the pass repeats every 1/abs({key}) = " +
            $"{period.ToString("0.###", CultureInfo.InvariantCulture)} s";
        if (period > c.Ceiling * (1 + c.RetimePercent / 100))
            return c.Refuse(ShaderTemporalUnresolvedKind.NonPeriodicOrDriftingMechanism,
                $"Verified {equation}. That is past the {CeilingText(c.Ceiling)}-second loop ceiling even with the largest retime this analysis allows ({CeilingText(c.RetimePercent)}%).");
        if (authored && !c.BaseMaterial)
            return ShaderVerdict.Of(new ShaderPeriodComponent(new(c.Id(key), new CommonLoopPeriod(period, CommonLoopPeriodEvidence.Analytic), AllowRetime: true),
                c.Patch(key, valueIndex, speed), c.Resource, $"Verified {equation}."));
        if (!TryReciprocalRational(token.Split(' ')[0], out CommonLoopRational exact))
            return c.Refuse(ShaderTemporalUnresolvedKind.MissingOrInvalidSpeed,
                $"Verified {equation}, but '{key}' is not a scene constant this analysis can retime and is not a simple decimal, so no exact fixed period is established.");
        return ShaderVerdict.Of(new ShaderPeriodComponent(new(c.Id(key), new CommonLoopPeriod(exact.ToSeconds(), CommonLoopPeriodEvidence.Analytic, exact),
            AllowRetime: false), c.Patch("", 0, 0), c.Resource,
            $"Verified {equation}. The rate is not a scene constant (material resource or shader default), so it is a locked exact period."));
    }

    /// <summary>
    /// 字面值步进时钟：去注释后每一处 g_Time（不算 uniform 声明）都恰好是 <c>mod(floor(g_Time * A), B)</c>，A、B 是同一对正十进制字面值，
    /// B 为整数。floor 每 1/A 秒加一，对整数 B 取模后 B 步一圈，所以周期严格是 B/A（序列帧动画的常见写法）。速率写死在 shader 里，
    /// 没有可改写的常量，作为锁定分量交出。
    /// </summary>
    private static ShaderVerdict? LiteralStepClock(PassContext c)
    {
        if (ClockCode(c, ["mod", "floor"]) is not string code) return null;
        const string literal = @"\d+(?:\.\d*)?";
        var steps = Regex.Matches(code, $@"(?<![\w.])mod\(\s*floor\(\s*g_Time\s*\*\s*(?<a>{literal})\s*\)\s*,\s*(?<b>{literal})\s*\)", RegexOptions.CultureInvariant);
        if (steps.Count == 0 || steps.Count != Regex.Matches(code, @"\bg_Time\b", RegexOptions.CultureInvariant).Count) return null;
        var pairs = steps.Select(m => (A: decimal.Parse(m.Groups["a"].Value, CultureInfo.InvariantCulture), B: decimal.Parse(m.Groups["b"].Value, CultureInfo.InvariantCulture)))
            .Distinct().ToArray();
        if (pairs.Length != 1 || pairs[0].A <= 0 || pairs[0].B <= 0 || pairs[0].B != decimal.Truncate(pairs[0].B)) return null;
        (decimal a, decimal b) = pairs[0];
        // A = 分子/10^scale，周期 B/A = B·10^scale/分子。
        int scale = (decimal.GetBits(a)[3] >> 16) & 0x7f;
        decimal power = 1;
        for (int i = 0; i < scale; ++i) power *= 10;
        decimal numerator = b * power, denominator = a * power;
        if (numerator > long.MaxValue || denominator > long.MaxValue) return null;
        var exact = new CommonLoopRational((long)numerator, (long)denominator);
        return ShaderVerdict.Of(new ShaderPeriodComponent(new(c.Id("frame-step"),
            new CommonLoopPeriod(exact.ToSeconds(), CommonLoopPeriodEvidence.Analytic, exact), AllowRetime: false),
            c.Patch("", 0, 0), c.Resource,
            $"Verified every g_Time use is mod(floor(g_Time * {a.ToString(CultureInfo.InvariantCulture)}), {b.ToString(CultureInfo.InvariantCulture)}): " +
            $"the step counter wraps every {exact.Numerator}/{exact.Denominator} s. The rate is a shader literal, so the pass has no retimable material constant."));
    }

    /// <summary>
    /// 结构化时钟规则的公共前提：去注释压空白后的全文（所有预处理分支都在，所以结论对任一分支组合成立），去掉 g_Time 的
    /// uniform 声明。用到备用时钟、或有 #define 改写 g_Time 与规则依赖的函数名时返回 null（不裁定）。
    /// </summary>
    private static string? ClockCode(PassContext c, string[] functions)
    {
        string text = c.Source.Normalized;
        if (ShaderSource.HasAlternateClock(text) ||
            Regex.IsMatch(text, $@"#\s*define\s+(?:g_Time|{string.Join('|', functions)})\b", RegexOptions.CultureInvariant)) return null;
        return Regex.Replace(text, @"\buniform\s+\w+\s+g_Time\s*;", " ", RegexOptions.CultureInvariant);
    }

    /// <summary>pass 上写了就按写的读（必须是有限标量），没写取 shader uniform 注释的默认值（材质键 → default）。</summary>
    private static bool AuthoredOrDefault(PassContext c, string key, out double value, out string token)
    {
        if (c.Pass["constantshadervalues"]?[key] is not null) return TryScalar(c.Pass, key, out value, out _, out token);
        (value, token) = (0, "");
        if (c.Source.UniformDefaults.GetValueOrDefault(key) is not JsonValue json || !json.TryGetValue(out value) || !double.IsFinite(value)) return false;
        token = json.ToJsonString() + " (shader default)";
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

    private static ShaderVerdict AutoSway(PassContext c, ClockRule rule)
    {
        if (Gates(c, rule) is ShaderVerdict gated) return gated;
        if (!TryScalar(c.Pass, "speed", out double speed, out int valueIndex, out string numericText) || speed == 0 ||
            !TryReciprocalRational(numericText, out CommonLoopRational exactPeriod))
            return c.Refuse(ShaderTemporalUnresolvedKind.MissingOrInvalidSpeed,
                "Verified auto-sway timing needs a nonzero decimal scalar 'speed' to establish an exact fixed period.");
        var period = new CommonLoopPeriod(exactPeriod.ToSeconds(), CommonLoopPeriodEvidence.Analytic, exactPeriod);
        return ShaderVerdict.Of(new ShaderPeriodComponent(new(c.Id("speed"), period, AllowRetime: false),
            c.Patch("speed", valueIndex, speed), c.Resource,
            $"Verified sin((g_Time * g_Speed + offset) * M_PI_2) in {c.Resource}; period is 1/abs({numericText})."));
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
            // 只要头部：工程自带的真实 clouds_256（256×256，约 200 KB）也只读前 64 字节，与 WE 资源目录一侧同一口径。
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
