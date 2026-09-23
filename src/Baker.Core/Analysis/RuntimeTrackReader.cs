using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Baker.Core;

/// <summary>
/// 一条可进求解器的运行时轨道（动画、精灵或视频）：锁定分量与可调速分量、作者 rate 补丁位置、片源帧率与精灵帧表。
/// </summary>
internal sealed record RuntimeTrack(int OwnerLayerId, string TrackName, int[] AnimationLayerIds, double[] AuthoredRates, bool CanRetime,
    CommonLoopComponent LockedComponent, CommonLoopComponent RetimableComponent, bool IsVideo, CommonLoopRational? ClipFrameRate = null,
    float[]? SpriteFrameTimes = null);

/// <summary>
/// 读运行时证据里被烘图层的时间机制：动画/精灵/视频轨道变成求解分量，证明不了周期的记成类型化未解析项；
/// 另含运行时材质时钟、作者脚本读时钟两类未解析项与粒子判据。从 HybridLoopService 原样搬出（C2.2c）。
/// </summary>
internal static class RuntimeTrackReader
{
    internal static List<RuntimeTrack> Read(JsonObject scene, ProjectSource source, string? assetsDirectory,
        JsonObject runtime, IReadOnlyCollection<int> bakedLayerIds, List<LoopUnresolved> unresolved, VideoControlScope videoControlScope,
        ParticleStationarity.FrameClock clock, out Dictionary<int, ParticleStationarity.Result> particleVerdicts)
    {
        var owners = scene["objects"]?.AsArray().OfType<JsonObject>().ToDictionary(x => x["id"]!.GetValue<int>())
            ?? throw new InvalidDataException("Scene has no objects.");
        var selected = new HashSet<int>(bakedLayerIds);
        var output = new Dictionary<string, RuntimeTrack>(StringComparer.Ordinal);
        // 粒子平稳随机判据按层只算一次；读不到的资源交给判据记成不满足，不在这里抛。
        var stationarity = new Dictionary<int, ParticleStationarity.Result>();
        particleVerdicts = stationarity;
        JsonObject? ReadParticleResource(string resource)
        {
            try { return SceneAnalyzer.ReadResourceJson(source, assetsDirectory, resource); }
            catch (Exception error) when (error is IOException or InvalidDataException or System.Text.Json.JsonException or
                InvalidOperationException or UnauthorizedAccessException or ArgumentException) { return null; }
        }
        ParticleStationarity.Result Stationarity(int ownerId, JsonObject owner)
        {
            if (!stationarity.TryGetValue(ownerId, out ParticleStationarity.Result? verdict))
                stationarity[ownerId] = verdict = ParticleStationarity.Evaluate(owner, owners, runtime, ReadParticleResource, clock);
            return verdict;
        }
        static RuntimeTrackUnresolved Animation(int ownerId, string key) => new(false, ownerId, new Message(key));
        static RuntimeTrackUnresolved Track(bool video, int ownerId, string? trackName, string key) =>
            new(video, ownerId, new Message(key)) { HasTrackName = true, TrackName = trackName };
        foreach (JsonObject trace in runtime["runtime_animation_periods"]?.AsArray().OfType<JsonObject>() ?? [])
        {
            int ownerId = trace["source_owner_layer_id"]?.GetValue<int>() ?? -1;
            if (!selected.Contains(ownerId)) continue;
            string? trackName = trace["track_name"]?.GetValue<string>();
            bool video = string.Equals(trace["mechanism"]?.GetValue<string>(), "video", StringComparison.OrdinalIgnoreCase);
            if (!owners.TryGetValue(ownerId, out JsonObject? owner))
            { unresolved.Add(Animation(ownerId, "unresolved.owner_or_duration_unresolved")); continue; }
            if (video)
            {
                if (trace["looping"]?.GetValue<bool>() != true || trace["event_driven"]?.GetValue<bool>() == true ||
                    !TryExactVideoDuration(trace, out CommonLoopRational videoDuration) || VideoPlaybackIsControlled(trace, runtime, ownerId, videoControlScope))
                {
                    unresolved.Add(Track(true, ownerId, trackName, "unresolved.video_needs_exact_duration"));
                    continue;
                }
                JsonValue? videoRate = trace["playback_rate"] as JsonValue ?? trace["current_rate"] as JsonValue;
                if (videoRate is null || !TryRational(videoRate.ToJsonString(), out CommonLoopRational lockedVideoRate) || lockedVideoRate != new CommonLoopRational(1))
                {
                    unresolved.Add(Track(true, ownerId, trackName, "unresolved.video_rate_not_one"));
                    continue;
                }
                string videoTrack = string.IsNullOrWhiteSpace(trackName) ? "(anonymous)" : trackName;
                string videoKey = $"video/{ownerId}/{videoTrack}/{videoDuration.Numerator}/{videoDuration.Denominator}";
                if (!output.ContainsKey(videoKey)) output[videoKey] = new(ownerId, videoTrack, [], [], true,
                    new CommonLoopComponent(videoKey, new CommonLoopPeriod(videoDuration.ToSeconds(), CommonLoopPeriodEvidence.Analytic, videoDuration)),
                    new CommonLoopComponent(videoKey, new CommonLoopPeriod(videoDuration.ToSeconds(), CommonLoopPeriodEvidence.Analytic), true), true,
                    TryClipFrameRate(trace, videoDuration, out CommonLoopRational clipRate) ? clipRate : null);
                continue;
            }
            if (trace["looping"]?.GetValue<bool>() != true || trace["event_driven"]?.GetValue<bool>() == true ||
                !string.Equals(trace["confidence"]?.GetValue<string>(), "high", StringComparison.OrdinalIgnoreCase))
            { unresolved.Add(Animation(ownerId, "unresolved.animation_not_high_confidence")); continue; }
            // 优先读渲染器给的有理时长（已吸附成 float 精度区间内的最简分数，如 12/5）；duration_seconds 是 double 真值
            // （2.400000035762787），按十进制文字取有理数会把 float 误差带进公倍数。旧渲染器或溢出时没有有理字段，才退回十进制。
            if (!TryExactVideoDuration(trace, out CommonLoopRational duration) &&
                (trace["duration_seconds"] is not JsonValue durationValue || !TryRational(durationValue.ToJsonString(), out duration)))
            { unresolved.Add(Animation(ownerId, "unresolved.owner_or_duration_unresolved")); continue; }
            bool spriteDurationIsPeriod = string.Equals(trace["mechanism"]?.GetValue<string>(), "sprite", StringComparison.OrdinalIgnoreCase);
            if (spriteDurationIsPeriod && owner["particle"] is not null)
            {
                // 证明不了周期时要说清是哪一种随机源或外部输入，不是一句笼统的拒绝。分配不变：本次不合成粒子有效周期。
                // detail 走 MessageCatalog（每条理由一个 key），与 i18n 分支口径一致：legacy 英文写进 plan，中文由 Localize 反查。
                // particle_stationarity 是下游（更小分配回退、残差掩盖）读的结构化结论，detail 与理由代号保持原样。
                (string code, Message detail) = ParticleInputAnalysis.NonperiodicReason(owner, source, assetsDirectory);
                unresolved.Add(new RuntimeTrackUnresolved(false, ownerId, detail) { HasTrackName = true, TrackName = trackName,
                    ParticleNonperiodicReason = code, Particle = Stationarity(ownerId, owner) });
                continue;
            }
            string resource = spriteDurationIsPeriod ? "textureAnimation" : "animation";
            bool observedControl = runtime["runtime_dependencies"]?.AsArray().OfType<JsonObject>().Any(dependency =>
                dependency["target"]?.GetValue<int>() == ownerId && dependency["operation"]?.GetValue<string>() == "write" &&
                dependency["property"]?.GetValue<string>() == resource && dependency["initialization"]?.GetValue<bool>() != true) == true;
            if (observedControl || spriteDurationIsPeriod && HasSpritePlaybackControl(owner))
            {
                // 随机重启的证明只对精灵轨道成立，而且必须在同一段脚本里闭合：取到这条纹理动画、对它做播放控制、
                // 用 Math.random 喂定时器延迟。骨骼/属性动画轨道被脚本控制时不借用这份证明；脚本别处拿
                // Math.random 做颜色之类也不算。结论写成结构化字段，下游（残差掩盖）读字段，不再匹配文案。
                bool randomRestart = spriteDurationIsPeriod && HasRandomSpriteRestart(owner, trackName);
                unresolved.Add(new RuntimeTrackUnresolved(false, ownerId,
                    new Message(randomRestart ? "unresolved.script_random_restart" : "unresolved.script_controlled_playback")) {
                    HasTrackName = true, TrackName = trackName,
                    Mechanism = spriteDurationIsPeriod ? "sprite" : trace["mechanism"]?.GetValue<string>() ?? "animation",
                    RandomRestart = randomRestart });
                continue;
            }
            JsonValue? traceRate = trace["playback_rate"] as JsonValue ?? trace["current_rate"] as JsonValue;
            CommonLoopRational rate = new(1);
            if ((traceRate is not null && !TryRational(traceRate.ToJsonString(), out rate)) || (!spriteDurationIsPeriod && traceRate is null))
            { unresolved.Add(Track(false, ownerId, trackName, "unresolved.playback_rate_unresolved")); continue; }
            if (spriteDurationIsPeriod && rate != new CommonLoopRational(1))
            { unresolved.Add(Track(false, ownerId, trackName, "unresolved.sprite_rate_not_one")); continue; }
            CommonLoopRational period = spriteDurationIsPeriod ? duration : Divide(duration, rate);
            // Duration is one authored traversal; a mirror track returns to its start after two.
            if (!spriteDurationIsPeriod && string.Equals(trace["playback_mode"]?.GetValue<string>(), "mirror", StringComparison.OrdinalIgnoreCase))
                period = new CommonLoopRational(checked(period.Numerator * 2), period.Denominator);
            string track = string.IsNullOrWhiteSpace(trackName) ? "(anonymous)" : trackName;
            string key = $"animation/{ownerId}/{track}/{duration.Numerator}/{duration.Denominator}/{rate.Numerator}/{rate.Denominator}";
            JsonObject[] layers = string.IsNullOrWhiteSpace(trackName) ? [] :
                owner["animationlayers"]?.AsArray().OfType<JsonObject>().Where(x => x["name"]?.GetValue<string>() == trackName).ToArray() ?? [];
            var matched = layers.Where(x => x["rate"] is JsonValue).ToArray();
            int[] ids = matched.Select(x => x["id"]?.GetValue<int>() ?? -1).Where(x => x >= 0).Distinct().ToArray();
            var authoredRates = new double[matched.Length];
            bool finiteAuthoredRates = true;
            for (int index = 0; index < matched.Length; ++index)
                finiteAuthoredRates &= TryFiniteRate(matched[index]["rate"], out authoredRates[index]);
            bool canRetime = !spriteDurationIsPeriod && matched.Length != 0 && ids.Length == matched.Length && finiteAuthoredRates &&
                matched.All(x => Float32RateEquals(x["rate"], rate));
            if (!canRetime && matched.Length != 0)
                unresolved.Add(Track(false, ownerId, track, "unresolved.no_authored_rate_patch"));
            if (!output.ContainsKey(key)) output[key] = new(ownerId, track, ids, authoredRates, canRetime, new CommonLoopComponent(key,
                new CommonLoopPeriod(period.ToSeconds(), CommonLoopPeriodEvidence.Observed, period)),
                new CommonLoopComponent(key, new CommonLoopPeriod(period.ToSeconds(), CommonLoopPeriodEvidence.Observed), true), false,
                SpriteFrameTimes: spriteDurationIsPeriod ? ReadSpriteFrameTimes(source, assetsDirectory, trackName, duration) : null);
        }
        // 没有精灵轨道的粒子层（雨、雪、雾大多如此）运行时观测里没有任何轨道，以前对周期分析不可见，
        // 只能靠更小分配回退把所有粒子层一律留实时。这里给每个被烘的粒子层补一条未解析项：
        // 粒子没有周期，接缝要么淡化替换（判据通过），要么这层留实时（判据不通过）。
        foreach (int ownerId in bakedLayerIds.Distinct())
        {
            if (!owners.TryGetValue(ownerId, out JsonObject? owner) || owner["particle"] is null || stationarity.ContainsKey(ownerId)) continue;
            ParticleStationarity.Result verdict = Stationarity(ownerId, owner);
            unresolved.Add(new RuntimeTrackUnresolved(false, ownerId, ParticleDetail(verdict)) { Mechanism = ParticleSystemMechanism, Particle = verdict });
        }
        return output.Values.ToList();
    }

    /// <summary>无精灵轨道的粒子层补记条目上的 mechanism。</summary>
    private const string ParticleSystemMechanism = "particle_system";

    /// <summary>粒子未解析项的 detail：锁定周期、平稳随机、不满足判据三种，文案走 MessageCatalog。</summary>
    private static Message ParticleDetail(ParticleStationarity.Result verdict) => verdict.Stationary
        ? verdict.Lock is ParticleStationarity.CyclostationaryLock cycle
            ? new Message("unresolved.particle_cyclostationary_locked", [cycle.PeriodFrames.ToString(CultureInfo.InvariantCulture),
                cycle.PeriodSeconds.ToSeconds().ToString("0.######", CultureInfo.InvariantCulture)])
            : new Message("unresolved.particle_stationary_random")
        : new Message("unresolved.particle_not_stationary", [verdict.FailureSummary()]);

    /// <summary>判据结论改变（锁定周期退回拒绝）后，换掉对应层条目上的判据结论；补记的 particle_system 条目连 detail 一起换。</summary>
    internal static void RewriteParticleItems(List<LoopUnresolved> unresolved, IReadOnlyDictionary<int, ParticleStationarity.Result> verdicts)
    {
        for (int index = 0; index < unresolved.Count; ++index)
        {
            if (unresolved[index] is not RuntimeTrackUnresolved { Particle: not null } item ||
                !verdicts.TryGetValue(item.OwnerLayerId, out ParticleStationarity.Result? verdict)) continue;
            unresolved[index] = item.Mechanism == ParticleSystemMechanism
                ? item with { Particle = verdict, Detail = ParticleDetail(verdict) }
                : item with { Particle = verdict };
        }
    }

    /// <summary>
    /// 被烘图层上作者脚本对时钟的非初始化读取：模型/着色器周期证明不了脚本推进的状态。
    /// 保留观测到的所有者，让分配回退能把那棵子树留实时。
    /// </summary>
    internal static void AddScriptTimeUnresolved(JsonObject runtime, IReadOnlyCollection<int> bakedLayerIds, List<LoopUnresolved> unresolved)
    {
        foreach (var dependency in (runtime["runtime_dependencies"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(item => item["operation"]?.GetValue<string>() == "time" &&
                item["initialization"]?.GetValue<bool>() != true &&
                SceneGraph.Int(item["owner"]) is int owner && bakedLayerIds.Contains(owner))
            .DistinctBy(item => (SceneGraph.Int(item["owner"]), item["binding"]?.GetValue<string>())))
            unresolved.Add(new ScriptTimeUnresolved(SceneGraph.Int(dependency["owner"])!.Value,
                dependency["binding"]?.DeepClone(), dependency["property"]?.DeepClone()));
    }

    /// <summary>
    /// 运行时材质里没被建模的时钟 uniform。一个图层的时间行为由「哪个 shader、哪套常量」决定，不由它在运行时
    /// 以 source 还是 effect 角色实例化决定：<paramref name="ruledMaterials"/> 里已经被方程规则裁定过的
    /// (层, shader) 不再重复计一条——direct-draw 效果层会把同一个 effect 实例化成 source + effect 两份材质，
    /// 旧的 role=="effect" 白名单只挡住其中一份，另一份就成了假阳性。
    /// </summary>
    internal static void AddMaterialClockUnresolved(JsonObject runtime, IReadOnlyCollection<int> bakedLayerIds,
        List<LoopUnresolved> unresolved, IReadOnlySet<(int OwnerLayerId, string Shader)> ruledMaterials)
    {
        if (runtime["runtime_layers"] is not JsonArray layers) return;
        var selected = new HashSet<int>(bakedLayerIds);
        foreach (JsonObject layer in layers.OfType<JsonObject>())
        {
            int? owner = SceneGraph.Int(layer["owner"]);
            if (owner is null || !selected.Contains(owner.Value) || layer["materials"] is not JsonArray materials) continue;
            foreach (JsonNode? node in materials)
            {
                if (node is not JsonObject material) continue;
                if (material["role"] is JsonNode roleNode && (roleNode is not JsonValue roleValue || !roleValue.TryGetValue<string>(out _)))
                {
                    AddOnce(new RuntimeMaterialUnresolved(owner.Value, new Message("unresolved.material_role_not_string")), unresolved);
                    continue;
                }
                string? role = material["role"]?.GetValue<string>();
                // 键必须带层 id：同名 shader 在别的层上是另一套常量，那边的裁定不能借到这里来。
                if (material["shader"] is JsonValue shaderValue && shaderValue.TryGetValue(out string? shaderName) &&
                    shaderName is not null && ruledMaterials.Contains((owner.Value, shaderName))) continue;
                if (material["active_uniforms"] is not JsonArray uniforms)
                {
                    if (role is not null) AddOnce(new RuntimeMaterialUnresolved(owner.Value, new Message("unresolved.material_omits_active_uniforms")), unresolved);
                    continue;
                }
                if (uniforms.Any(uniform => uniform is not JsonValue value || !value.TryGetValue<string>(out _)))
                {
                    AddOnce(new RuntimeMaterialUnresolved(owner.Value, new Message("unresolved.material_uniforms_invalid")), unresolved);
                    continue;
                }
                string[] clocks = uniforms.OfType<JsonValue>().Select(value => value.GetValue<string>())
                    .Where(IsRuntimeClock).Distinct(StringComparer.Ordinal).ToArray();
                if (clocks.Length != 0)
                    AddOnce(new RuntimeMaterialUnresolved(owner.Value,
                        new Message("unresolved.material_temporal_uniforms", [role ?? "unknown-role", string.Join(", ", clocks)])), unresolved);
            }
        }
    }

    /// <summary>追加一条运行时材质未解析项；同一条（同层、同文案键与参数）已在就不重复加。</summary>
    private static void AddOnce(RuntimeMaterialUnresolved item, List<LoopUnresolved> unresolved)
    {
        if (!unresolved.OfType<RuntimeMaterialUnresolved>().Any(existing => existing.OwnerLayerId == item.OwnerLayerId &&
            existing.Detail.Key == item.Detail.Key && existing.Detail.Args.SequenceEqual(item.Detail.Args))) unresolved.Add(item);
    }

    internal static bool IsRuntimeClock(string uniform) => uniform is "g_Time" or "g_Runtime" or "g_Frametime" or "g_DeltaTime";

    /// <summary>
    /// 数出要进视频的图层上、非 effect 运行时材质里出现的时钟 uniform 条数（每个材质内去重）。
    /// 这个计数只进裁定理由供用户核对，不参与任何判定。
    /// </summary>
    internal static int CountRuntimeClockUniforms(JsonObject runtime, IReadOnlyCollection<int> bakedLayerIds)
    {
        if (runtime["runtime_layers"] is not JsonArray layers) return 0;
        var selected = new HashSet<int>(bakedLayerIds);
        int count = 0;
        foreach (JsonObject layer in layers.OfType<JsonObject>())
        {
            int? owner = SceneGraph.Int(layer["owner"]);
            if (owner is null || !selected.Contains(owner.Value) || layer["materials"] is not JsonArray materials) continue;
            foreach (JsonObject material in materials.OfType<JsonObject>())
            {
                if (material["role"] is JsonValue roleValue && roleValue.TryGetValue<string>(out string? role) &&
                    string.Equals(role, "effect", StringComparison.Ordinal)) continue;
                if (material["active_uniforms"] is not JsonArray uniforms) continue;
                count += uniforms.OfType<JsonValue>()
                    .Where(value => value.TryGetValue<string>(out string? name) && IsRuntimeClock(name))
                    .Select(value => value.GetValue<string>()).Distinct(StringComparer.Ordinal).Count();
            }
        }
        return count;
    }

    private static readonly Regex TextureAnimationCall = new(@"getTextureAnimation\s*\(\s*(?:['""]([^'""]*)['""]\s*)?\)", RegexOptions.CultureInvariant);
    private static readonly Regex PlaybackControlCall = new(
        @"(?:\.\s*(?:play|pause|stop|setFrame|join)\b|\[\s*['""](?:play|pause|stop|setFrame|join)['""]\s*\]|\.\s*rate\s*[+\-*/%]?=(?!=))", RegexOptions.CultureInvariant);
    private static readonly Regex RandomCall = new(@"\bMath\s*\.\s*random\s*\(", RegexOptions.CultureInvariant);
    private static readonly Regex TimerCall = new(@"\bset(?:Timeout|Interval)\s*\(", RegexOptions.CultureInvariant);

    private static bool HasSpritePlaybackControl(JsonObject owner) => SceneAnalyzer.Walk(owner).OfType<JsonObject>()
        .Any(binding => binding["script"] is JsonValue script && script.TryGetValue<string>(out string? code) &&
            code.Contains("getTextureAnimation", StringComparison.Ordinal) && PlaybackControlCall.IsMatch(code));

    /// <summary>
    /// 随机重启比一般的脚本控制说得更强：精灵在任何采集长度下都没有周期。证明必须在同一段脚本里闭合——
    /// 取到这条纹理动画（无参调用即本层默认动画；带名字则必须与轨道同名）、对它做播放控制、并用 Math.random
    /// 喂给定时器。三者缺一都只是"脚本控制"，不是随机证明；脚本别处的 Math.random（颜色、位置）不算。
    /// </summary>
    private static bool HasRandomSpriteRestart(JsonObject owner, string? trackName) => SceneAnalyzer.Walk(owner).OfType<JsonObject>()
        .Any(binding => binding["script"] is JsonValue script && script.TryGetValue<string>(out string? code) &&
            TextureAnimationCall.Matches(code).Any(match => !match.Groups[1].Success || string.IsNullOrWhiteSpace(trackName) ||
                string.Equals(match.Groups[1].Value, trackName, StringComparison.Ordinal)) &&
            PlaybackControlCall.IsMatch(code) && RandomCall.IsMatch(code) && TimerCall.IsMatch(code));

    private static CommonLoopRational Divide(CommonLoopRational left, CommonLoopRational right)
    {
        if ((Int128)left.Numerator * right.Denominator > long.MaxValue || (Int128)left.Denominator * right.Numerator > long.MaxValue) throw new ArgumentOutOfRangeException();
        return new CommonLoopRational(checked(left.Numerator * right.Denominator), checked(left.Denominator * right.Numerator));
    }

    private static bool Float32RateEquals(JsonNode? sourceRate, CommonLoopRational runtimeRate) => sourceRate is JsonValue value &&
        value.TryGetValue<double>(out double authored) && double.IsFinite(authored) &&
        float.IsFinite((float)authored) && float.IsFinite((float)runtimeRate.ToSeconds()) &&
        (float)authored == (float)runtimeRate.ToSeconds();

    private static bool TryFiniteRate(JsonNode? sourceRate, out double rate)
    {
        rate = 0;
        return sourceRate is JsonValue value && value.TryGetValue<double>(out rate) && double.IsFinite(rate);
    }

    private static bool TryExactVideoDuration(JsonObject trace, out CommonLoopRational duration)
    {
        duration = default;
        if (!TryPositiveInteger(trace["duration_numerator"], out long numerator) ||
            !TryPositiveInteger(trace["duration_denominator"], out long denominator)) return false;
        try { duration = new CommonLoopRational(numerator, denominator); return true; }
        catch (ArgumentOutOfRangeException) { return false; }
    }

    /// <summary>片源自身的原生帧率 = 帧数 / 时长。运行时探针已记录 frame_count，这里只是把它变成精确有理数。</summary>
    private static bool TryClipFrameRate(JsonObject trace, CommonLoopRational duration, out CommonLoopRational rate)
    {
        rate = default;
        if (!TryPositiveInteger(trace["frame_count"], out long frames)) return false;
        try
        {
            rate = new CommonLoopRational(checked(frames * duration.Denominator), duration.Numerator);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentOutOfRangeException or OverflowException) { return false; }
    }

    private static bool TryPositiveInteger(JsonNode? node, out long value)
    {
        value = 0;
        if (node is not JsonValue json) return false;
        if (json.TryGetValue<long>(out value)) return value > 0;
        if (json.TryGetValue<int>(out int integer)) { value = integer; return value > 0; }
        return false;
    }

    /// <summary>
    /// 四条并列判据，命中任一即受控：运行时对该轨的非初始化播放属性写入（A1）、
    /// 脚本已把这条轨的 videoTexture 拿到手或自身脚本控制自身（A2/A3，见 <see cref="VideoControlScope"/>），
    /// 以及目标解析不出来时退回全场景连坐（A4）。
    /// </summary>
    private static bool VideoPlaybackIsControlled(JsonObject trace, JsonObject runtime, int ownerId, VideoControlScope videoControlScope)
    {
        if (trace["dynamic_controlled"] is JsonValue dynamic && (!dynamic.TryGetValue<bool>(out bool controlled) || controlled)) return true;
        bool observed = runtime["runtime_dependencies"]?.AsArray().OfType<JsonObject>().Any(dependency =>
            dependency["target"]?.GetValue<int>() == ownerId && dependency["operation"]?.GetValue<string>() == "write" &&
            dependency["initialization"]?.GetValue<bool>() != true && IsVideoPlaybackProperty(dependency["property"]?.GetValue<string>())) == true;
        return observed || videoControlScope.Controls(ownerId);
    }

    private static bool IsVideoPlaybackProperty(string? property) => property is not null &&
        (property.Equals("video", StringComparison.OrdinalIgnoreCase) || property.Equals("videoPlayback", StringComparison.OrdinalIgnoreCase) ||
         property.Equals("video_texture", StringComparison.OrdinalIgnoreCase));

    private static bool TryRational(string token, out CommonLoopRational value)
    {
        value = default;
        if (!decimal.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal number) || number <= 0) return false;
        int scale = (decimal.GetBits(number)[3] >> 16) & 0x7f; decimal factor = 1;
        for (int i = 0; i < scale; i++) factor *= 10;
        decimal numerator = number * factor;
        if (numerator != decimal.Truncate(numerator) || numerator > long.MaxValue || factor > long.MaxValue) return false;
        value = new((long)numerator, (long)factor); return true;
    }

    /// <summary>
    /// 读精灵纹理的 float32 帧表（先项目内 materials/，再全局 assets/materials/，与渲染器 vfs 顺序一致）。
    /// 帧表总和必须与运行时 trace 的时长（std::to_string 的 6 位小数）一致，否则不是同一张纹理，不用。
    /// </summary>
    private static float[]? ReadSpriteFrameTimes(ProjectSource source, string? assetsDirectory, string? texture, CommonLoopRational traceDuration)
    {
        if (string.IsNullOrWhiteSpace(texture)) return null;
        try
        {
            string resource = $"materials/{texture}.tex";
            byte[]? data = source.Contains(resource) ? source.Read(resource, 256 * 1024 * 1024) : null;
            if (data is null && !string.IsNullOrWhiteSpace(assetsDirectory))
            {
                string path = Path.GetFullPath(Path.Combine(assetsDirectory, "materials", texture + ".tex"));
                if (path.StartsWith(Path.GetFullPath(assetsDirectory), StringComparison.OrdinalIgnoreCase) && File.Exists(path))
                    data = File.ReadAllBytes(path);
            }
            if (data is null || !SpriteSeamPhase.TryReadFrameTimes(data, out float[] times)) return null;
            double sum = times.Sum(x => (double)x);
            return Math.Abs(sum - traceDuration.ToSeconds()) <= 5.01e-7 ? times : null;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException) { return null; }
    }
}
