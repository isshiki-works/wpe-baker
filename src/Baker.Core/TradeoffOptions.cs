using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>
/// 取舍清单：把"关掉哪些可取舍的实时元素，就能把这张换成整幅视频"列成最多三个方案。
/// <para>
/// 只读 plan 已经算好的字段（layers/reasons/parent/live_layer_ids/blockers_localized/summary/settings），
/// 不做任何判定，也不承诺结果：每个方案都写明"关掉后要重新分析确认"，残留实时层数是按层级关系推的估计值。
/// </para>
/// <para>
/// 三类划分是产品口径：<b>取舍类</b>关掉后画面仍自洽（视差、鼠标、音频律动、反馈特效、时钟、帧率、
/// 一次性入场动画、媒体信息、自带 BGM、赞助码一类覆盖层）；<b>主体类</b>整张画面就是那个实时效果画出来的，
/// 关掉就没有内容，直接拒绝；<b>技术类</b>（透视相机、源脚本报错、运行时资源依赖）与用户偏好无关，关不掉。
/// </para>
/// </summary>
public static class TradeoffOptions
{
    public const string Tradeoff = "tradeoff";
    public const string Subject = "subject";
    public const string Technical = "technical";
    public const string Derived = "derived";

    /// <summary>plan 里挂清单的字段名。</summary>
    public const string Field = "tradeoff_options";

    /// <summary>实时理由 → (分类, 子类型)。</summary>
    private static readonly Dictionary<string, (string Class, string Kind)> ReasonMap = new(StringComparer.Ordinal)
    {
        ["pointer_api"] = (Tradeoff, "pointer"),
        ["observed_pointer"] = (Tradeoff, "pointer"),
        ["particle_pointer_input"] = (Tradeoff, "pointer"),
        ["active_shader_pointer_input"] = (Tradeoff, "pointer"),
        ["audio_api"] = (Tradeoff, "audio"),
        ["observed_audio"] = (Tradeoff, "audio"),
        ["active_shader_audio_spectrum"] = (Tradeoff, "audio"),
        ["particle_audio_input"] = (Tradeoff, "audio"),
        ["soundtrack"] = (Tradeoff, "bgm"),
        ["wall_clock_api"] = (Tradeoff, "clock"),
        ["observed_wall_clock"] = (Tradeoff, "clock"),
        ["media_api"] = (Tradeoff, "media"),
        ["live_frame_status"] = (Tradeoff, "fps"),
        ["active_shader_parallax_input"] = (Tradeoff, "parallax"),
        ["unresolved_runtime_parallax_depth"] = (Tradeoff, "parallax"),
        ["mixed_parallax_depth_hierarchy"] = (Tradeoff, "parallax"),
        ["runtime_parallax_depth_change"] = (Tradeoff, "parallax"),
        ["animated_parallax_depth"] = (Tradeoff, "parallax"),
        // 用户自己用 --retain-live 要求留着的层：清单不劝人关它，按派生处理。
        ["retained_by_cost_trial"] = (Derived, "requested_live"),
        // 读上一帧的残影/拖影/扩散与一次性入场动画：分析上是结构问题，对用户是"关掉就好"的观感取舍。
        ["reads_current_framebuffer"] = (Tradeoff, "feedback"),
        [SingleShotAllocation.LiveReason] = (Tradeoff, "intro"),
        ["scene_camera"] = (Technical, "camera"),
        ["source_script_error"] = (Technical, "script_error"),
        ["live_runtime_resource_dependency"] = (Technical, "runtime_resource"),
        ["shares_live_hierarchy"] = (Derived, "hierarchy"),
        ["written_by_live_controller"] = (Derived, "controller"),
        ["hidden_script_controller"] = (Derived, "controller"),
        ["reads_live_object"] = (Derived, "controller"),
        ["retained_as_foreground_suffix"] = (Derived, "foreground"),
    };

    /// <summary>子类型的排列次序：清单里按它排，标签也按它取。</summary>
    private static readonly string[] KindOrder =
        ["parallax", "pointer", "audio", "feedback", "clock", "media", "bgm", "fps", "intro", "overlay"];

    /// <summary>
    /// 方案分档：越靠前关掉的东西越不伤内容——先是纯观感的（残影、入场动画），再是装饰交互（鼠标、音频、
    /// 媒体信息、帧率、覆盖层），然后才是视差、时钟这类有功能的，最后是自带 BGM。
    /// 与反事实扫描的 C1/C2/C3 同一个方向，只是把"只关观感"单独拆出来，好让最小的那个组合先被列出来。
    /// </summary>
    private static readonly string[][] Tiers = [
        ["feedback", "intro"],
        ["parallax"],
        ["feedback", "intro", "pointer", "audio", "media", "fps", "overlay"],
        ["feedback", "intro", "pointer", "audio", "media", "fps", "overlay", "parallax"],
        ["feedback", "intro", "pointer", "audio", "media", "fps", "overlay", "parallax", "clock"],
        ["feedback", "intro", "pointer", "audio", "media", "fps", "overlay", "parallax", "clock", "bgm"]];

    /// <summary>这条实时理由属于哪一类、哪个子类型；认不出的理由返回 null（不当成可关项）。</summary>
    public static (string Class, string Kind)? ClassifyReason(string reason) =>
        ReasonMap.TryGetValue(reason, out var found) ? found : null;

    /// <summary>
    /// 按一层的全部实时理由定它的分类与子类型。分类优先级：技术类 &gt; 取舍类 &gt; 派生类
    /// （既有取舍机制又有技术机制的层，关掉取舍那一半也退不出实时，所以归技术类）。
    /// <paramref name="subject"/> 仅供已有独立主体证据的调用方使用；无输入无关分组本身不构成这种证据。
    /// </summary>
    public static (string Class, string[] Kinds) Classify(IEnumerable<string> reasons, bool subject = false, bool suspectedOverlay = false)
    {
        ArgumentNullException.ThrowIfNull(reasons);
        var kinds = new List<string>();
        bool technical = false, tradeoff = false, derived = false;
        foreach (string reason in reasons)
        {
            if (ClassifyReason(reason) is not { } found) continue;
            if (found.Class == Technical) technical = true;
            else if (found.Class == Tradeoff) tradeoff = true;
            else derived = true;
            if (!kinds.Contains(found.Kind, StringComparer.Ordinal)) kinds.Add(found.Kind);
        }
        if (tradeoff && suspectedOverlay && !kinds.Contains("overlay", StringComparer.Ordinal)) kinds.Add("overlay");
        string classification = technical ? Technical : tradeoff ? subject ? Subject : Tradeoff : derived ? Derived : Technical;
        // 技术类与派生类的子类型不参与取舍清单，但仍写进 plan 供排查。
        return (classification, [.. KindOrder.Where(kind => kinds.Contains(kind, StringComparer.Ordinal))
            .Concat(kinds.Where(kind => !KindOrder.Contains(kind, StringComparer.Ordinal)))]);
    }

    /// <summary>子类型的用户标签（中英）。</summary>
    public static string KindLabel(string kind, string language) => MessageCatalog.Get("tradeoff.kind." + kind, language);

    /// <summary>给 plan 挂上 <c>tradeoff_options</c>；plan 缺字段时挂一条 status 说明，不抛异常。</summary>
    public static void Attach(JsonObject plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        plan[Field] = Describe(plan);
    }

    /// <summary>算出取舍清单。只读 plan，不改它。</summary>
    public static JsonObject Describe(JsonObject plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var layers = (plan["layers"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(layer => Number(layer["id"]) is not null).ToDictionary(layer => (int)Number(layer["id"])!.Value);
        // 在 PlanNarrative.Attach 里 Finish 之后运行，plan 已编号：按编号判断，不比键字符串。
        BlockerCode[] blockerCodes = [.. PlanBlockers.Codes(plan)];
        bool dependencyBlocked = blockerCodes.Any(code => code is BlockerCode.NoInputIndependentGroup or BlockerCode.NoInputIndependentGroupGeneric);
        var record = new JsonObject {
            ["basis"] = "Read from this plan only: turning these off requires analyzing again; residual live layer counts are estimates.",
            ["retain_live_measured"] = "Keeping layers live measurably does not save power (laptop iGPU rail 11.63 -> 11.20 W, package +12% at 60 fps).",
            ["retain_live_note_zh"] = MessageCatalog.Get("tradeoff.retain_live_note", MessageCatalog.Chinese),
            ["retain_live_note_en"] = MessageCatalog.Get("tradeoff.retain_live_note", MessageCatalog.English) };
        if (dependencyBlocked)
        {
            // 只有当前依赖分析的结果，没有禁用效果后的反事实证据；列出相关机制，不推断整幅主体。
            string[] mechanisms = [.. KindOrder.Where(kind => layers.Values.Any(layer => Flag(layer["live"]) == true &&
                Flag(layer["drawable"]) == true && Classification(layer) is Subject or Tradeoff &&
                Kinds(layer).Contains(kind, StringComparer.Ordinal))).Take(3)];
            record["status"] = "dependency_blocked";
            record["related_kinds"] = new JsonArray([.. mechanisms.Select(kind => (JsonNode)JsonValue.Create(kind))]);
            record["options"] = new JsonArray();
            record["zh"] = MessageCatalog.Get("tradeoff.dependency_blocked", MessageCatalog.Chinese, KindList(mechanisms, MessageCatalog.Chinese));
            record["en"] = MessageCatalog.Get("tradeoff.dependency_blocked", MessageCatalog.English, KindList(mechanisms, MessageCatalog.English));
            return record;
        }
        string summaryKey = Text(plan["summary"]?["key"]) ?? "";
        bool alreadyFullFrame = summaryKey.StartsWith("summary.bakeable", StringComparison.Ordinal) &&
            Text(plan["settings"]?["video_layout"]) == "full_frame" && Text(plan["route"]) == "whole_layer";
        int[] live = [.. (plan["live_layer_ids"] as JsonArray ?? []).Select(Number).OfType<double>().Select(id => (int)id).Where(layers.ContainsKey)];
        if (alreadyFullFrame)
        {
            record["status"] = "not_needed";
            record["options"] = new JsonArray();
            return record;
        }
        var tradeoffLive = live.Where(id => Classification(layers[id]) == Tradeoff).ToArray();
        if (tradeoffLive.Length == 0)
        {
            record["status"] = "no_tradeoff_elements";
            record["options"] = new JsonArray();
            record["zh"] = MessageCatalog.Get("tradeoff.none_available", MessageCatalog.Chinese);
            record["en"] = MessageCatalog.Get("tradeoff.none_available", MessageCatalog.English);
            return record;
        }
        bool viewFixed = Text(plan["settings"]?["view_mode"]) == "fixed_view";
        var options = new List<JsonObject>();
        var seen = new List<string>();
        foreach (string[] tier in Tiers)
        {
            var targets = tradeoffLive.Where(id => Kinds(layers[id]).Any(kind => tier.Contains(kind, StringComparer.Ordinal))).ToHashSet();
            if (targets.Count == 0) continue;
            var option = BuildOption(plan, layers, live, targets, tier, viewFixed);
            // 后一档关的项更多却关掉同一批图层（例如时钟本来就挂在视差层下面）时不重复列。
            string fingerprint = Text(option["view_mode"]) + "|" + string.Join(",", (option["excluded_layer_ids"] as JsonArray ?? [])
                .Select(id => id?.ToJsonString()));
            if (seen.Contains(fingerprint, StringComparer.Ordinal)) continue;
            seen.Add(fingerprint);
            options.Add(option);
        }
        // 排序：预计能进整幅的排前面，其次关的项数少的；最多给三个。
        var ranked = options
            .OrderBy(option => Flag(option["expected_full_frame"]) == true ? 0 : 1)
            .ThenBy(option => Number(option["turn_off_count"]) ?? 0)
            .ThenBy(option => Number(option["collateral_drawable_layers"]) ?? 0)
            .ToList();
        // 一个方案都没把握进整幅时，末位让给"关得最彻底"的那个：用户这时最想知道关到什么程度才有戏。
        JsonObject[] ordered = ranked.Count > 3 && !ranked.Any(option => Flag(option["expected_full_frame"]) == true)
            ? [.. ranked.Take(2), ranked[^1]]
            : [.. ranked.Take(3)];
        for (int index = 0; index < ordered.Length; ++index)
        {
            ordered[index]["rank"] = index + 1;
            foreach (string language in new[] { MessageCatalog.Chinese, MessageCatalog.English })
                ordered[index][language] = Narrate(ordered[index], index + 1, language);
        }
        record["status"] = ordered.Length > 0 ? "available" : "no_tradeoff_elements";
        record["options"] = new JsonArray([.. ordered.Select(option => (JsonNode)option)]);
        // 这张还卡着与实时元素无关的问题时先说清楚：画面本来就被切成几块，或者根本还没证明出可闭合的循环。
        // 关掉可取舍元素不一定解决这两种，别让清单看起来像保票。
        var caveats = new List<string>();
        if (blockerCodes.Contains(BlockerCode.FullframeSplitGroupsOptions)) caveats.Add("tradeoff.caveat_structural");
        if ((plan["loop"]?["candidates"] as JsonArray ?? []).Count == 0 &&
            Text(plan["loop"]?["no_candidate_reason"]?["kind"]) is not null) caveats.Add("tradeoff.caveat_no_loop");
        record["caveats"] = new JsonArray([.. caveats.Select(key => (JsonNode)JsonValue.Create(key))]);
        record["zh"] = string.Join("", caveats.Select(key => MessageCatalog.Get(key, MessageCatalog.Chinese))
            .Append(MessageCatalog.Get("tradeoff.header", MessageCatalog.Chinese, ordered.Length)));
        record["en"] = string.Join(" ", caveats.Select(key => MessageCatalog.Get(key, MessageCatalog.English))
            .Append(MessageCatalog.Get("tradeoff.header", MessageCatalog.English, ordered.Length)));
        return record;
    }

    /// <summary>一个方案：要关什么、怎么关、连带关掉什么、关掉后还剩多少实时。</summary>
    private static JsonObject BuildOption(JsonObject plan, Dictionary<int, JsonObject> layers, int[] live,
        HashSet<int> targets, string[] tier, bool viewFixed)
    {
        bool parallax = tier.Contains("parallax", StringComparer.Ordinal) &&
            (targets.Any(id => Kinds(layers[id]).Contains("parallax", StringComparer.Ordinal)) || Flag(plan["has_parallax"]) == true);
        // 视差靠固定视角关；扫描实测 C2 档是"排除视差层 + 固定视角"，这里照同一口径给。
        bool fixedView = parallax && !viewFixed;
        // 显式排除清单：祖先已在清单里的层不必再写，--exclude-layers 本来就连带整棵子树。
        int[] excludeRoots = [.. targets.Where(id => !Ancestors(layers, id).Any(targets.Contains)).Order()];
        var removed = new HashSet<int>(layers.Keys.Where(id => excludeRoots.Contains(id) || Ancestors(layers, id).Any(excludeRoots.Contains)));
        int[] collateral = [.. removed.Except(targets).Order()];
        int[] collateralDrawable = [.. collateral.Where(id => Flag(layers[id]["drawable"]) == true && Flag(layers[id]["visible"]) != false)];
        int[] residual = EstimateResidual(layers, live, removed);
        string[] kinds = [.. KindOrder.Where(kind => targets.Any(id => Kinds(layers[id]).Contains(kind, StringComparer.Ordinal)) &&
            tier.Contains(kind, StringComparer.Ordinal))];
        if (fixedView && !kinds.Contains("parallax", StringComparer.Ordinal) && parallax) kinds = [.. kinds.Append("parallax")];
        var properties = Properties(layers, targets);
        // 命令行等价写法：属性关不掉的那部分靠 --exclude-layers，视差靠 --interaction fixed。
        string command = string.Join(" ", new[] {
            excludeRoots.Length == 0 ? null : "--exclude-layers " + string.Join(",", excludeRoots),
            fixedView ? "--interaction fixed" : null }.OfType<string>());
        return new JsonObject {
            ["turn_off_kinds"] = new JsonArray([.. kinds.Select(kind => (JsonNode)JsonValue.Create(kind))]),
            ["turn_off_count"] = kinds.Length,
            ["turn_off_layer_ids"] = new JsonArray([.. targets.Order().Select(id => (JsonNode)JsonValue.Create(id))]),
            ["properties"] = properties,
            ["exclude_layers"] = new JsonArray([.. excludeRoots.Select(id => (JsonNode)JsonValue.Create(id))]),
            ["view_mode"] = fixedView ? "fixed_view" : null,
            ["command"] = command,
            ["excluded_layer_ids"] = new JsonArray([.. removed.Order().Select(id => (JsonNode)JsonValue.Create(id))]),
            ["collateral_layer_ids"] = new JsonArray([.. collateral.Select(id => (JsonNode)JsonValue.Create(id))]),
            ["collateral_drawable_layers"] = collateralDrawable.Length,
            // 连带关掉的里面也有可取舍元素（例如挂在视差层下面的时钟、跟着一起没的自带 BGM）：单独列出来，
            // 免得用户以为只关了自己勾的那几样。
            ["collateral_kinds"] = new JsonArray([.. KindOrder.Where(kind => collateral.Any(id =>
                Classification(layers[id]) is Tradeoff or Subject && Kinds(layers[id]).Contains(kind, StringComparer.Ordinal)) &&
                !kinds.Contains(kind, StringComparer.Ordinal)).Select(kind => (JsonNode)JsonValue.Create(kind))]),
            ["collateral_names"] = new JsonArray([.. collateralDrawable.Take(8).Select(id => layers[id]["name"]?.DeepClone() ?? JsonValue.Create(id))]),
            ["estimated_residual_live_layers"] = residual.Length,
            ["estimated_residual_kinds"] = new JsonArray([.. KindOrder
                .Concat(["hierarchy", "controller", "foreground", "camera", "script_error", "runtime_resource"])
                .Where(kind => residual.Any(id => Kinds(layers[id]).Contains(kind, StringComparer.Ordinal)))
                .Select(kind => (JsonNode)JsonValue.Create(kind))]),
            ["estimated_residual_layer_ids"] = new JsonArray([.. residual.Select(id => (JsonNode)JsonValue.Create(id))]),
            // 残留实时画面层为 0 才敢说"最有希望进整幅"；仍有实时画面层时只说需要重新分析。
            // 判据是保守的：实测按这一档重跑，29 案里 21 案真的进了整幅，另有几案留着实时前景也能进。
            ["expected_full_frame"] = residual.All(id => Flag(layers[id]["drawable"]) != true),
            ["expected_full_frame_basis"] = "no_live_drawing_layer_remains",
            ["verification"] = "estimated_from_plan_reanalyze_to_confirm" };
    }

    /// <summary>关掉这批层之后还会实时的层：派生层若所在分配根已经没有自带实时理由的层，就跟着退出实时。</summary>
    private static int[] EstimateResidual(Dictionary<int, JsonObject> layers, int[] live, HashSet<int> removed)
    {
        int[] remaining = [.. live.Where(id => !removed.Contains(id))];
        var anchored = remaining.Where(id => Classification(layers[id]) != Derived).ToHashSet();
        var anchoredRoots = anchored.Select(id => Root(layers, id)).ToHashSet();
        return [.. remaining.Where(id => anchored.Contains(id) || anchoredRoots.Contains(Root(layers, id)))];
    }

    /// <summary>这批层的显示开关绑在哪些壁纸属性上；只收推得出关闭办法的。</summary>
    private static JsonArray Properties(Dictionary<int, JsonObject> layers, HashSet<int> targets)
    {
        var byKey = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (int id in targets.Order())
        {
            if (layers[id]["visible_property"] is not JsonObject property || Text(property["key"]) is not string key) continue;
            if (Text(property["status"]) is not ("resolved" or "conditional")) continue;
            if (!byKey.TryGetValue(key, out var entry))
            {
                entry = property.DeepClone().AsObject();
                entry["layer_ids"] = new JsonArray();
                byKey[key] = entry;
            }
            entry["layer_ids"]!.AsArray().Add(id);
        }
        return new JsonArray([.. byKey.Values.Select(entry => (JsonNode)entry)]);
    }

    /// <summary>方案的一段话（中英各一版）。</summary>
    private static string Narrate(JsonObject option, int rank, string language) =>
        string.Join(language == MessageCatalog.Chinese ? "" : " ", OptionLines(option, language, rank).Select(part => part.Text));

    /// <summary>
    /// 方案说明的分段：每段带字段名（lead 要关什么 / how 怎么关 / alternative 更轻的替代 /
    /// collateral 连带关掉什么 / route 关掉后的路线 / residual 残留实时层 / retain_live 保留实时不省电）。
    /// CLI 按这个顺序连成一段（<see cref="Narrate"/>），界面按自己的顺序分行显示，两处文案永远同一份。
    /// </summary>
    /// <param name="rank">方案序号；省略时读 option 上的 <c>rank</c>，没有就算第一个。</param>
    public static (string Field, string Text)[] OptionLines(JsonObject option, string language, int? rank = null)
    {
        ArgumentNullException.ThrowIfNull(option);
        language = MessageCatalog.NormalizeLanguage(language);
        int index = rank ?? (int)(Number(option["rank"]) ?? 1);
        string[] kinds = [.. (option["turn_off_kinds"] as JsonArray ?? []).Select(Text).OfType<string>()];
        var parts = new List<(string Field, string Text)> {
            ("lead", MessageCatalog.Get("tradeoff.option_lead", language, index, kinds.Length, KindList(kinds, language))) };
        int residual = (int)(Number(option["estimated_residual_live_layers"]) ?? 0);
        string[] residualKinds = [.. (option["estimated_residual_kinds"] as JsonArray ?? []).Select(Text).OfType<string>()];
        parts.Add(("route", Flag(option["expected_full_frame"]) == true
            ? MessageCatalog.Get("tradeoff.route_full_frame", language)
            : MessageCatalog.Get("tradeoff.route_needs_check", language, residual)));
        // 关法：能在 Wallpaper Engine 里关掉属性的先说属性，命令行写法始终给出。
        var properties = (option["properties"] as JsonArray ?? []).OfType<JsonObject>().ToArray();
        if (properties.Length > 0)
        {
            // 属性只盖住一部分图层时说清楚：剩下的还得走命令行，别让用户以为关掉属性就完事了。
            var covered = properties.SelectMany(entry => (entry["layer_ids"] as JsonArray ?? []).Select(Number))
                .OfType<double>().Select(id => (int)id).ToHashSet();
            bool whole = (option["turn_off_layer_ids"] as JsonArray ?? []).Select(Number).OfType<double>()
                .All(id => covered.Contains((int)id));
            // 关闭值只在单个属性时写具体值；多个属性各有各的关法，说"逐个关掉"，不拿第一个的值以偏概全。
            string hint = properties.Length == 1
                ? Text(properties[0]["off_hint_" + language]) ?? Text(properties[0]["off_hint_en"]) ?? ""
                : MessageCatalog.Get("tradeoff.off_hint_each", language);
            parts.Add(("how", MessageCatalog.Get(whole ? "tradeoff.how_property" : "tradeoff.how_property_partial", language,
                MessageCatalog.NameList(properties.Select(entry => Label(entry, language))), hint)));
        }
        else parts.Add(("how", MessageCatalog.Get("tradeoff.how_no_property", language)));
        if (Text(option["command"]) is { Length: > 0 } command) parts.Add(("how", MessageCatalog.Get("tradeoff.how_command", language, command)));
        // 视差有更轻的一条：固定视角不删任何图层。实测两者对视差等价，但哪一案够用没逐案验过，所以只作提示。
        if (Text(option["view_mode"]) == "fixed_view") parts.Add(("alternative", MessageCatalog.Get("tradeoff.parallax_alternative", language)));
        int collateral = (int)(Number(option["collateral_drawable_layers"]) ?? 0);
        string[] collateralKinds = [.. (option["collateral_kinds"] as JsonArray ?? []).Select(Text).OfType<string>()];
        parts.Add(("collateral", collateral == 0 && collateralKinds.Length == 0 ? MessageCatalog.Get("tradeoff.collateral_none", language)
            : collateral == 0 ? MessageCatalog.Get("tradeoff.collateral_kinds_only", language, KindList(collateralKinds, language))
            : MessageCatalog.Get("tradeoff.collateral", language, collateral,
                MessageCatalog.NameList((option["collateral_names"] as JsonArray ?? []).Select(Text))) +
                (collateralKinds.Length == 0 ? "" : (language == MessageCatalog.Chinese ? "" : " ") +
                    MessageCatalog.Get("tradeoff.collateral_kinds", language, KindList(collateralKinds, language)))));
        // 残留的层全都不画画面（例如只剩 BGM 音轨）时不说"省电打折"，那会把音轨说成逐帧渲染。
        string residualKey = residual == 0 ? "tradeoff.residual_none"
            : Flag(option["expected_full_frame"]) == true ? "tradeoff.residual_non_drawable" : "tradeoff.residual";
        parts.Add(("residual", MessageCatalog.Get(residualKey, language, residual, KindList(residualKinds, language))));
        parts.Add(("retain_live", MessageCatalog.Get("tradeoff.retain_live_note", language)));
        // 分析解锁不等于烘得出来：23 案真烘复验 0 通过，所以卡片底部收一句烘制阶段的免责。
        parts.Add(("bake_caveat", MessageCatalog.Get("tradeoff.bake_stage_caveat", language)));
        return [.. parts];
    }

    private static string Label(JsonObject property, string language)
    {
        string key = Text(property["key"]) ?? "";
        string? label = Text(property["label"]);
        return string.IsNullOrWhiteSpace(label) || label == key ? key : label + " / " + key;
    }

    /// <summary>子类型标签连成一句："视差（画面跟随鼠标）、鼠标交互（尾迹/涟漪/指针光效）"。</summary>
    public static string KindList(IEnumerable<string> kinds, string language)
    {
        ArgumentNullException.ThrowIfNull(kinds);
        string[] labels = [.. kinds.Select(kind => KindLabel(kind, language))];
        return string.Join(language == MessageCatalog.Chinese ? "、" : ", ", labels);
    }

    private static string[] Kinds(JsonObject layer) =>
        [.. (layer["tradeoff_kinds"] as JsonArray ?? []).Select(Text).OfType<string>()];

    private static string Classification(JsonObject layer) => Text(layer["tradeoff_class"]) ?? Derived;

    private static IEnumerable<int> Ancestors(Dictionary<int, JsonObject> layers, int id)
    {
        var seen = new HashSet<int> { id };
        for (int? cursor = Number(layers[id]["parent"]) is double parent ? (int)parent : null;
            cursor is int current && layers.ContainsKey(current) && seen.Add(current);
            cursor = Number(layers[current]["parent"]) is double next ? (int)next : null)
            yield return current;
    }

    private static int Root(Dictionary<int, JsonObject> layers, int id) =>
        Number(layers[id]["allocation_root"] ?? layers[id]["root"]) is double root ? (int)root : id;

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    private static bool? Flag(JsonNode? node) => node is JsonValue value && value.TryGetValue(out bool flag) ? flag : null;

    private static double? Number(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue(out double number) && double.IsFinite(number)) return number;
        if (value.TryGetValue(out long integer)) return integer;
        if (value.TryGetValue(out int small)) return small;
        return null;
    }


    /// <summary>结论行后面那半句："有 N 个取舍方案…"；没有清单时返回 null。</summary>
    internal static (string Zh, string En)? SummarySentence(JsonObject plan)
    {
        if (plan[Field] is not JsonObject record) return null;
        string status = Text(record["status"]) ?? "";
        int count = (record["options"] as JsonArray)?.Count ?? 0;
        if (status == "available" && count > 0)
            return (MessageCatalog.Get("summary.tradeoff_available", MessageCatalog.Chinese, count),
                MessageCatalog.Get("summary.tradeoff_available", MessageCatalog.English, count));
        if (status == "subject_only")
            return (MessageCatalog.Get("summary.tradeoff_subject_only", MessageCatalog.Chinese),
                MessageCatalog.Get("summary.tradeoff_subject_only", MessageCatalog.English));
        if (status == "dependency_blocked")
            return (MessageCatalog.Get("summary.tradeoff_dependency_blocked", MessageCatalog.Chinese),
                MessageCatalog.Get("summary.tradeoff_dependency_blocked", MessageCatalog.English));
        return null;
    }

    /// <summary>plan 里的清单打印成给人看的几行（CLI 的 stderr 用）。没有清单时返回空数组。</summary>
    public static string[] Lines(JsonObject plan, string language)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan[Field] is not JsonObject record) return [];
        string normalized = MessageCatalog.NormalizeLanguage(language);
        var lines = new List<string>();
        if (Text(record[normalized]) is { Length: > 0 } header) lines.Add(header);
        foreach (var option in (record["options"] as JsonArray ?? []).OfType<JsonObject>())
            if (Text(option[normalized]) is { Length: > 0 } text) lines.Add(text);
        return [.. lines];
    }
}
