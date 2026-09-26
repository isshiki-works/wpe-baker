using System.Globalization;
using System.Text.Json.Nodes;
using Baker.Core;

namespace Baker.App;

/// <summary>
/// 结论区与取舍卡片的人话文案：只读 plan，不做任何判定、不改 plan。
/// <para>
/// 分析的技术原文（档位、观感预算、相位差、阻塞原因、命令行）一句都没删，全部移到界面上那个
/// 折叠的"技术细节"面板里，由 <see cref="AppJsonPresentation"/> 原样生成；这里只负责把同一份
/// 结果讲成不懂术语的人也能读懂的三行结论和一张勾选卡片。
/// </para>
/// <para>
/// 界面文案一律走 <see cref="L"/>（中文在前、英文在后），禁用词扫描按这个形状取字符串。
/// </para>
/// </summary>
internal static class PlainLanguage
{
    private static string L(bool english, string zh, string en) => english ? en : zh;

    // ---------------------------------------------------------------------------------------
    // 第一行：一句话结论
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// 结论第一行：固定四种状态文本之一，不带数字、不带依据、不拼句。依据一律进"详情"面板。
    /// 还没分析时返回空串，界面自己显示"未评估"。
    /// </summary>
    public static string Verdict(JsonObject? plan, bool english)
    {
        if (plan is null) return "";
        if (NoBenefitExpected(plan) || CannotBake(plan)) return L(english, "不支持", "Not supported");
        string? value = plan[BakeValueAssessment.Field]?["status"]?.GetValue<string>();
        if (value == "potential_gain") return L(english, "可以生成，预计省电", "Ready to generate, likely saves power");
        if (value == "low_value") return L(english, "可以生成，预计省电较少", "Ready to generate, small power saving expected");
        if (value == "unknown") return L(english, "可以生成，省电效果未知", "Ready to generate, power saving unknown");
        return L(english, "可以生成", "Ready to generate");
    }

    /// <summary>分析判定预计不省电：界面上一律不生成（只有命令行 --no-benefit allow 能覆盖）。</summary>
    public static bool NoBenefitExpected(JsonObject? plan) => plan?[NoBenefit.Field]?["status"]?.GetValue<string>() == NoBenefit.ExpectedStatus;

    /// <summary>结论第二行：固定一句；命中的条件（主因）进"详情"。</summary>
    public static string NoBenefitLine(bool english) =>
        L(english, "预计功耗高于原壁纸", "Estimated power use is higher than the original wallpaper.");

    /// <summary>
    /// 结论第二行：生成不了时指向详情，其余显示数字行（见 <see cref="Numbers"/>）。
    /// </summary>
    public static string NextAction(JsonObject? plan, bool english) =>
        plan is not null && CannotBake(plan)
            ? L(english, "未找到可用的生成方式", "No usable way to generate was found") : Numbers(plan, english);

    /// <summary>
    /// 烘不了（能烘、含"关掉几样就能烘"时为 false）。原因句不在这里写，界面第二行读 blockers_localized 的第一条。
    /// 判据全部来自 plan 已有的字段：取舍清单的状态、suitability 的 rule、阻塞原因的 key、有没有循环。
    /// </summary>
    private static bool CannotBake(JsonObject plan)
    {
        string[] keys = BlockerKeys(plan);
        string rule = plan["suitability"]?["rule"]?.GetValue<string>() ?? "";
        // 这几类关掉实时元素也救不回来，先于取舍卡片判定：依赖挡住、主体就是实时效果、HDR/3D 相机、没东西可烘或只剩静态。
        if (plan[TradeoffOptions.Field]?["status"]?.GetValue<string>() is "dependency_blocked" or "subject_only" ||
            rule is "nothing_to_bake" or "no_temporal_mechanism_in_video" ||
            keys.Any(key => key.StartsWith("blocker.hdr", StringComparison.Ordinal) || key is "blocker.perspective_needs_screenspace"
                or "blocker.camera_path_needs_envelope" or "blocker.runtime_projection_required"))
            return true;
        // 还有取舍方案可选时不算烘不了：第一行会说"关掉几样就能烘"。
        return !HasTurnOffCard(plan) && (keys.Length > 0 || !HasLoop(plan));
    }

    private static string[] BlockerKeys(JsonObject plan) =>
        [.. (plan["blockers_localized"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(item => item["key"]?.GetValue<string>()).OfType<string>()];

    private static bool HasLoop(JsonObject plan) =>
        (plan["loop"]?["candidates"] as JsonArray)?.Count > 0 ||
        (plan["effect_prefix_caches"] as JsonArray)?.Count > 0;

    /// <summary>
    /// 不根据原作功耗阈值改变取舍卡片的优先级。
    /// </summary>
    public static bool TradeoffFirst(JsonObject? plan) =>
        false;

    /// <summary>有没有可勾的取舍卡片：只有"关掉几样就能整张录成视频"的清单才出卡片，主体类不出。</summary>
    public static bool HasTurnOffCard(JsonObject? plan) =>
        plan?.ContainsKey("preset_applied") != true && plan?[TradeoffOptions.Field]?["status"]?.GetValue<string>() == "available";

    /// <summary>这次分析烘不了：结论第一行会是"不支持"。界面用它决定结论第二行放哪句话。</summary>
    public static bool CannotGenerate(JsonObject? plan) => plan is not null && CannotBake(plan);

    /// <summary>plan 里如果写了这次实际按哪个档位出的方案（quality / balanced / efficiency / custom），结论区多出的一行；没有这个字段就不显示。</summary>
    public static string PresetAppliedLine(JsonObject? plan, string requested, bool english)
    {
        string? applied = plan?["preset_applied"]?.GetValue<string>();
        if (applied is null || applied == requested || !RetimeProfile.IsKnownPreset(applied)) return "";
        string name = AppJsonPresentation.PresetLabel(applied, english), selected = AppJsonPresentation.PresetLabel(requested, english);
        return L(english, $"已改用动画精度“{name}”（“{selected}”无法生成）", $"Switched Animation precision to {name} ({selected} not possible)");
    }

    public static string[] AppliedChangeLines(JsonObject? plan, bool english)
    {
        var lines = (plan?["applied_tradeoffs"]?["turn_off_kinds"] as JsonArray ?? [])
            .Select(k => k?.GetValue<string>()).OfType<string>().Distinct().Select(k => KindLabel(k, english)).ToList();
        if (plan?["applied_tradeoffs"]?["daytime_state"]?.GetValue<string>() is string state)
            lines.Add(L(english, $"按 {state} 时段生成", $"Generated for the {state} time of day"));
        return lines.ToArray();
    }

    // ---------------------------------------------------------------------------------------
    // 第二行：数字
    // ---------------------------------------------------------------------------------------

    /// <summary>结论第二行：循环长度 · 画面改动档 · 帧率 · 画面尺寸；数字缺一项就少一段，不拿零充数。</summary>
    public static string Numbers(JsonObject? plan, bool english)
    {
        if (plan is null) return "";
        var parts = new List<string>();
        JsonObject? candidate = plan["loop"]?["candidates"]?.AsArray().OfType<JsonObject>().FirstOrDefault();
        if (AppJsonPresentation.Number(candidate?["seconds"]) is double seconds && seconds > 0)
        {
            parts.Add(L(english, "循环周期 ", "Loop period ") + Duration(seconds, english));
            parts.Add(L(english, "画面差异：", "frame difference: ") + ChangeLevel(candidate!, english));
        }
        if (FrameRate(plan) is string fps) parts.Add(fps);
        if (FrameSize(plan) is string size) parts.Add(size);
        return string.Join(" · ", parts);
    }

    /// <summary>循环长度的人话写法：不到一分钟只说秒，超过就是"2 分 31 秒"。</summary>
    internal static string Duration(double seconds, bool english)
    {
        long total = (long)Math.Round(seconds, MidpointRounding.AwayFromZero);
        if (total < 60) return total.ToString(CultureInfo.InvariantCulture) + L(english, " 秒", " s");
        long minutes = total / 60, rest = total % 60;
        string head = minutes.ToString(CultureInfo.InvariantCulture) + L(english, " 分", " min");
        // 正好整分钟就不写"0 秒"。
        return rest == 0 ? head : head + " " + rest.ToString(CultureInfo.InvariantCulture) + L(english, " 秒", " s");
    }

    /// <summary>
    /// 画面改动的三档人话映射（基本看不出来 / 略有改动 / 改动明显）。判据是分析已经算出来的量：摆动改频的
    /// 可见改动（sway_retime.max_change_visible_percent；没有摆动解时退回整体调速 total_retime_cost_percent）
    /// 与一个循环内最坏相位差（sway_retime.phase_drift_cycles，单位是摆动的圈数）。
    /// <list type="bullet">
    /// <item>基本看不出来：可见改动 ≤ 3% 且相位差 &lt; 0.5 圈；</item>
    /// <item>略有改动：可见改动 ≤ 5%；</item>
    /// <item>改动明显：可见改动 &gt; 5%，或位移分量没闭合（sway_retime.residual_pixels &gt; 0）。</item>
    /// </list>
    /// 档位与这三档对齐：平衡档预算 3%、效率档 5%。依据是 2026-09-18 用户看片后的结论——
    /// sway-budget-scan.md §3 的五个样片里，3% 与 5% 档的解（阿米娅 3486806915 平衡档 2.81%、
    /// 3750317749 的 2.03%）看下来都属于"基本看不出来"，此前把平衡档 3% 判成"改动明显"与实际观感不符。
    /// 相位差 0.5 圈是改频求解自身的上界（每项取 n = round(x) 个整圈），越界只会来自没闭合的位移分量。
    /// 一个改动量都没有（没开摆动改频、也没有调速）时同样按"基本看不出来"：分析没改动画面。
    /// </summary>
    internal static string ChangeLevel(JsonObject candidate, bool english)
    {
        JsonObject? sway = candidate["sway_retime"] as JsonObject;
        double drift = AppJsonPresentation.Number(sway?["phase_drift_cycles"]) ?? 0;
        double visible = AppJsonPresentation.Number(sway?["max_change_visible_percent"]) ??
            AppJsonPresentation.Number(candidate["total_retime_cost_percent"]) ?? 0;
        double residual = AppJsonPresentation.Number(sway?["residual_pixels"]) ?? 0;
        if (residual <= 0 && visible <= 3 && drift < 0.5) return L(english, "可忽略", "negligible");
        if (residual <= 0 && visible <= 5) return L(english, "轻微", "slight");
        return L(english, "明显", "noticeable");
    }

    /// <summary>帧率：整数帧率直接写，60000/1001 一类写成两位小数。</summary>
    private static string? FrameRate(JsonObject plan)
    {
        double numerator = AppJsonPresentation.Number(plan["settings"]?["fps_numerator"]) ?? 0;
        double denominator = AppJsonPresentation.Number(plan["settings"]?["fps_denominator"]) ?? 0;
        if (numerator <= 0 || denominator <= 0) return null;
        double fps = numerator / denominator;
        return (denominator == 1 ? fps.ToString("0", CultureInfo.InvariantCulture)
            : fps.ToString("0.##", CultureInfo.InvariantCulture)) + " fps";
    }

    /// <summary>画面尺寸：优先用分析算出的实际输出尺寸，没有就用设置里的。</summary>
    private static string? FrameSize(JsonObject plan)
    {
        double width = AppJsonPresentation.Number(plan["output_resolution"]?["width"]) ??
            AppJsonPresentation.Number(plan["settings"]?["width"]) ?? 0;
        double height = AppJsonPresentation.Number(plan["output_resolution"]?["height"]) ??
            AppJsonPresentation.Number(plan["settings"]?["height"]) ?? 0;
        return width > 0 && height > 0
            ? width.ToString("0", CultureInfo.InvariantCulture) + "×" + height.ToString("0", CultureInfo.InvariantCulture)
            : null;
    }

    // ---------------------------------------------------------------------------------------
    // 取舍卡片
    // ---------------------------------------------------------------------------------------

    /// <summary>卡片里的一项：一个可关掉的东西、关掉之后会怎样，以及要不要默认勾上。</summary>
    internal sealed record TurnOffItem(string Kind, string Label, string Consequence, bool Recommended);

    /// <summary>界面上这一项的排列次序：先纯装饰，再有功能的，最后是声音。</summary>
    private static readonly string[] Order =
        ["pointer", "parallax", "feedback", "intro", "clock", "media", "fps", "overlay", "audio", "bgm"];

    /// <summary>
    /// 卡片里的勾选项：只列这张壁纸上真有的、关得掉的东西。
    /// 默认勾上的是"最有希望的那一档"——清单里排第一的方案（分析已按"预计能整张录成视频"排过序）。
    /// </summary>
    public static TurnOffItem[] TurnOffItems(JsonObject? plan, bool english)
    {
        var options = AppJsonPresentation.TradeoffOptionViews(plan, english);
        if (options.Length == 0) return [];
        var recommended = options[0].Kinds.ToHashSet(StringComparer.Ordinal);
        var available = options.SelectMany(option => option.Kinds).ToHashSet(StringComparer.Ordinal);
        return [.. Order.Where(available.Contains)
            .Select(kind => new TurnOffItem(kind, KindLabel(kind, english), KindConsequence(kind, english), recommended.Contains(kind)))];
    }

    /// <summary>可禁用项的名字，界面上前缀"禁用"。</summary>
    internal static string KindLabel(string kind, bool english) => kind switch
    {
        "pointer" => L(english, "鼠标跟随特效", "Mouse-follow effects"),
        "parallax" => L(english, "鼠标视差", "Mouse parallax"),
        "feedback" => L(english, "拖影效果", "Trail effects"),
        "intro" => L(english, "开场动画", "Intro animation"),
        "clock" => L(english, "时钟与日期", "Clock and date"),
        "media" => L(english, "当前播放曲目", "Now-playing track"),
        "fps" => L(english, "帧率显示", "Frame rate readout"),
        "overlay" => L(english, "角标图像", "Corner overlay image"),
        "audio" => L(english, "音频响应", "Audio response"),
        "bgm" => L(english, "壁纸背景音乐", "Wallpaper background music"),
        _ => kind
    };

    /// <summary>关掉这一项之后画面上会少什么，一句话。</summary>
    internal static string KindConsequence(string kind, bool english) => kind switch
    {
        "pointer" => L(english, "移除鼠标位置驱动的光效与涟漪", "removes glow and ripple driven by pointer position"),
        "parallax" => L(english, "移除随鼠标位移的视角偏移", "removes view offset driven by pointer position"),
        "feedback" => L(english, "移除帧间反馈产生的拖尾", "removes frame-feedback trails"),
        "intro" => L(english, "移除一次性入场动画", "removes the one-shot intro animation"),
        "clock" => L(english, "移除时钟显示", "removes the clock readout"),
        "media" => L(english, "移除曲目名显示", "removes the track-name readout"),
        "fps" => L(english, "移除帧率显示", "removes the frame rate readout"),
        "overlay" => L(english, "移除角标图像", "removes the corner overlay image"),
        "audio" => L(english, "移除音频频谱驱动的动画", "removes animation driven by the audio spectrum"),
        "bgm" => L(english, "停止壁纸自带背景音乐", "stops the wallpaper's own background music"),
        _ => ""
    };

    /// <summary>
    /// 勾选组合对应哪个方案：先挑"要关的项都在勾选里"的方案（勾多了不影响），取关得最多的那个；
    /// 一个都没有就退而求其次，挑与勾选重合最多的方案。全都不沾边时返回 null。
    /// </summary>
    public static AppJsonPresentation.AppTradeoffOption? Match(
        IReadOnlyList<AppJsonPresentation.AppTradeoffOption> options, IReadOnlyCollection<string> selected)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(selected);
        var picked = selected.ToHashSet(StringComparer.Ordinal);
        var covered = options.Where(option => option.Kinds.All(picked.Contains))
            .OrderByDescending(option => option.Kinds.Length).ToArray();
        if (covered.Length > 0) return covered[0];
        return options.Select(option => (Option: option, Shared: option.Kinds.Count(picked.Contains)))
            .Where(pair => pair.Shared > 0)
            .OrderByDescending(pair => pair.Shared).ThenBy(pair => pair.Option.Kinds.Length)
            .Select(pair => pair.Option).FirstOrDefault();
    }

    /// <summary>卡片底部那行小字：关掉之后还剩多少实时效果。</summary>
    public static string ResidualNote(AppJsonPresentation.AppTradeoffOption? option, bool english)
    {
        string caveat = L(english, "禁用后需重新分析；生成阶段仍可能因粒子系统或主体动画无循环周期而失败。",
            "Re-analysis is required after disabling; generation can still fail when particle systems or subject animation have no loop period.");
        if (option is null)
            return L(english, "未选择项目：当前设置下无整幅预渲染方案。", "No items selected: no full-frame pre-rendering route under the current settings.")
                + " " + caveat;
        if (option.ResidualLiveCount <= 0)
            return L(english, "禁用后无剩余实时图层。", "No live layers remain after disabling.") + " " + caveat;
        string count = option.ResidualLiveCount.ToString(CultureInfo.InvariantCulture);
        return L(english, $"禁用后仍有 {count} 个实时图层，功耗收益下降。",
            $"{count} live layers remain after disabling; power saving is reduced.") + " " + caveat;
    }

    // ---------------------------------------------------------------------------------------
    // 禁用词扫描要覆盖的 MessageCatalog 条目
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// 界面上会原样显示出来的 Baker.Core 文案（属性面板顶部那句的原因部分）。
    /// 其余 MessageCatalog 条目只出现在折叠的"技术细节"面板和报告文件里，不在这个清单里。
    /// </summary>
    public static readonly string[] GuiMessageKeys = [
        "properties.reason.config_not_found", "properties.reason.config_unreadable",
        "properties.reason.no_profile", "properties.reason.profile_ambiguous",
        "properties.reason.entry_ambiguous", "properties.reason.location_ambiguous",
        // fix/first-run 那批报错：拖入/选择来源（SourceDiagnosis）、启动自检缺件、应用到桌面失败，
        // 三类都会原样出现在状态栏或队列里，所以一并纳入扫描。
        // setup.assets_missing 只有命令行在用（界面走 ValidationText），它要写 --assets，不在清单里。
        "source.not_scene_project", "source.video_wallpaper", "source.web_wallpaper", "source.content_clause",
        "source.path_missing", "source.folder_not_wallpaper", "source.file_not_wallpaper",
        "source.scene_without_objects", "source.unreadable",
        "setup.tools_config_missing", "setup.tool_file_missing", "setup.runtime_directory_missing",
        "setup.tool_renderer", "setup.tool_ffmpeg", "setup.tool_ffprobe",
        "apply.wallpaper_engine_not_running", "apply.location_missing",
        "summary.static_only_route"];
}
