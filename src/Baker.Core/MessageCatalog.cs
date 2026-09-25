using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Baker.Core;

/// <summary>
/// 用户可见文案的中英对照表。只管文案，不参与任何判定。
/// <para>
/// 三层文本：<c>Legacy</c> 是历史英文原文，写进 plan.json 现有的 <c>blockers</c> / <c>unresolved[].detail</c>
/// 字段，逐字不变以兼容下游脚本与既有测试；<c>Zh</c> / <c>En</c> 是改写后的双语文案，只出现在新增的
/// <c>blockers_localized</c> / <c>unresolved_localized</c> / <c>summary</c> 里。
/// </para>
/// <para>
/// 程序不按文案判断，也不按英文反查：产生文案的一方带着 <see cref="Message"/>（键 + 参数）或 <see cref="Blocker"/>（编号 + 参数）走，
/// 输出时才渲染。Legacy 列只作 plan/bake v3 英文字段的渲染模板，C3 切 plan v4 时删。
/// </para>
/// </summary>
public static class MessageCatalog
{
    /// <summary>一条文案。Legacy 为 null 表示英文原文没有被改写，En 即原文。</summary>
    public sealed record Entry(string Zh, string En, string? Legacy = null)
    {
        /// <summary>legacy 模板；未改写时与 En 相同。</summary>
        public string LegacyTemplate => Legacy ?? En;
        /// <summary>Zh/En 实际用到的参数个数（legacy 专用的尾部参数不计入，不会写进 params）。</summary>
        public int PublicParameterCount { get; } = Math.Max(PlaceholderCount(Zh), PlaceholderCount(En));
    }

    public const string Chinese = "zh";
    public const string English = "en";

    private static readonly Regex Placeholder = new(@"\{(\d+)\}", RegexOptions.CultureInvariant);

    private static int PlaceholderCount(string template)
    {
        int highest = -1;
        foreach (Match match in Placeholder.Matches(template))
            highest = Math.Max(highest, int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture));
        return highest + 1;
    }

    // ---------------------------------------------------------------------------------------
    // 文案表。key 用稳定短名；新增 blocker 请在这里补一条，漏补只会回退英文，不会让分析失败。
    // ---------------------------------------------------------------------------------------
    private static readonly Dictionary<string, Entry> Table = new(StringComparer.Ordinal)
    {
        ["blocker.bake_allocation"] = new(Zh: "部分内容必须实时渲染，无法转为视频。", En: "Some content must render live and can't be turned into video.", Legacy: "Bake allocation rejected: {0}"),
        ["preset.generated"] = new(Zh: "可以生成", En: "Ready to generate"),
        ["preset.omitted"] = new(Zh: "本次未包含：{0}", En: "Not included: {0}"),
        ["preset.experimental"] = new(Zh: "已启用保留实时元素（实验性：可能增加 GPU 占用）", En: "Live elements retained (experimental: may increase GPU usage)"),
        ["preset.daytime"] = new(Zh: "按 {0} 时段生成", En: "Generated for the {0} time of day"),
        ["preset.too_many_video_groups"] = new(Zh: "当前设置需要的视频数量超过 {0}，无法生成。", En: "The current settings require more than {0} videos and cannot be generated."),
        ["interaction.suggest_fixed"] = new(Zh: "固定视角后可以生成。", En: "A fixed view allows generation."),
        ["interaction.suggest_off"] = new(Zh: "关闭鼠标效果后可以生成。", En: "Turning off mouse effects allows generation."),
        // ---- blockers ----
        ["blocker.missing_script_fault_evidence"] = new(
            Zh: "分析结果来自旧版本，需重新分析。运行时观测早于源脚本错误元数据，脚本报错判据不可用。",
            En: "This analysis is from an older version; analyze again. Runtime observation predates source script fault metadata, so the script fault criterion is unavailable.",
            Legacy: "Runtime observation predates source script fault metadata; analyze again with the current renderer."),

        // 无独立组只说明当前依赖分析没有可烘焙分组，不能据一项输入推断整幅画面主体或禁用结果。
        // legacy 英文保持不变。
        ["blocker.no_input_independent_group"] = new(
            Zh: "画面全部依赖实时输入（如鼠标、音频），无法转为视频。相关依赖包括：{0}。",
            En: "All visible content depends on live input such as the mouse or audio, so it can't be turned into video. Related dependencies include: {0}.",
            Legacy: "No input-independent visual group remains after dependency closure."),

        ["blocker.no_input_independent_group_generic"] = new(
            Zh: "画面全部依赖实时输入（如鼠标、音频），无法转为视频。",
            En: "All visible content depends on live input such as the mouse or audio, so it can't be turned into video.",
            Legacy: "No input-independent visual group remains after dependency closure."),

        // 第 2 批合并新增（feat/sdr-closure）：拒绝理由不再是"场景开了 hdr"，而是"这一组的输出证明不了落在 [0,1] 内"。
        // {0} 是判据点名的失败层与判据代号（英文，下游脚本按它对账），{1} 是 general.hdr 绑定的属性名，
        // {2} 是它的关闭值说明（分语言，PlanNarrative.HdrRadianceOpen 拼好）。只有 hdr 真正绑定属性时才用带属性的那条：
        // 名字里带 hdr 但绑在 bloom 等别处的属性关掉后 hdr 仍是 true，不算开关。
        // legacy 逐字沿用 SdrRadianceClosure.HdrBlocker + " Unproven: {0}"，写进 plan 的 blockers 一个字没变。
        ["blocker.hdr_radiance_open"] = new(
            Zh: "场景使用 HDR，暂不支持。待录分组内有图层未能证明输出落在 [0,1] 内，亮部可能被压缩。壁纸未提供可禁用 HDR 的属性。未通过项：{0}",
            En: "The scene uses HDR, which isn't supported yet. " + SdrRadianceClosure.HdrBlocker + " Unproven: {0}",
            Legacy: SdrRadianceClosure.HdrBlocker + " Unproven: {0}"),

        ["blocker.hdr_radiance_open_property"] = new(
            Zh: "场景使用 HDR，暂不支持。待录分组内有图层未能证明输出落在 [0,1] 内，亮部可能被压缩。壁纸提供 HDR 属性开关（{1}；{2}），在 Wallpaper Engine 中禁用后重新分析，或用 --properties 传入禁用后的属性。未通过项：{0}",
            En: "The scene uses HDR, which isn't supported yet. " + SdrRadianceClosure.HdrBlocker + " The wallpaper exposes an HDR property ({1}; {2}): disable it in Wallpaper Engine and analyze again, or pass the adjusted properties with --properties. Unproven: {0}",
            Legacy: SdrRadianceClosure.HdrBlocker + " Unproven: {0}"),

        ["blocker.perspective_needs_screenspace"] = new(
            Zh: "场景使用 3D 透视镜头，暂不支持。当前版本无法自动导出屏幕空间合成方案。",
            En: "The scene uses a 3D perspective camera, which isn't supported yet. The current version cannot derive a screen-space composition automatically.",
            Legacy: "Perspective capture needs an explicit screen-space composition before production."),

        ["blocker.camera_path_needs_envelope"] = new(
            Zh: "场景有镜头运动，暂不支持。需先捕获镜头运动包络，当前分析缺少该数据。",
            En: "The scene has camera movement, which isn't supported yet. An active authored camera path requires a captured motion envelope, which this analysis does not have.",
            Legacy: "An active authored camera path needs a captured motion envelope before production."),

        ["blocker.runtime_projection_required"] = new(
            Zh: "场景含摄像机，本次分析缺少所需数据，需重新分析。需要运行时摄像机投影数据，本次观测未采集。",
            En: "The scene has a camera and this analysis lacks the data it needs; analyze again. The runtime camera projection was not captured.",
            Legacy: "The runtime camera projection is required for a scene with a camera object."),

        // 文案① 的三种形态共用一份 legacy 模板：{0}=组数 {1}=交织实时层数 {2}=层名列表 {3}=legacy 专用中段
        ["blocker.fullframe_needs_opaque_group"] = new(
            Zh: "画面中间夹着必须实时渲染的图层（{2}），无法合成一整段视频。可录内容分为 {0} 组，共 {1} 个实时图层。可选方案：--live-overlays foreground 将实时图层前置后重新分析（遮挡关系改变），或 --video-layout layered 分层视频（播放开销需另行验证）。",
            En: "Layers that must render live ({2}) sit between the video parts, so one full-frame video isn't possible. The recordable content is split into {0} groups with {1} live layers in between. Options: --live-overlays foreground moves the live layers to the front and requires re-analysis (occlusion changes); --video-layout layered uses layered video (playback cost requires separate validation).",
            Legacy: "Full-frame mode requires one opaque video group; the selected scene settings currently need {0} group(s) or a transparent background.{3} Choose independent live overlays in the foreground and analyze again, change the scene settings, or explicitly choose layered video. Foreground placement changes occlusion; layered video requires separate playback-cost validation."),

        ["blocker.fullframe_needs_opaque_group_no_live"] = new(
            Zh: "画面分成 {0} 段，没有一段能单独作为底层，无法合成一整段视频。可选方案：--live-overlays foreground 将实时图层前置后重新分析（遮挡关系改变），或 --video-layout layered 分层视频（播放开销需另行验证）。",
            En: "The image splits into {0} parts and none can serve as the base alone, so one full-frame video isn't possible. Options: --live-overlays foreground moves the live layers to the front and requires re-analysis (occlusion changes); --video-layout layered uses layered video (playback cost requires separate validation).",
            Legacy: "Full-frame mode requires one opaque video group; the selected scene settings currently need {0} group(s) or a transparent background.{3} Choose independent live overlays in the foreground and analyze again, change the scene settings, or explicitly choose layered video. Foreground placement changes occlusion; layered video requires separate playback-cost validation."),

        ["blocker.fullframe_single_transparent_group"] = new(
            Zh: "可录的画面是透明的，没有不透明底层，无法合成一整段视频。可选方案：--live-overlays foreground 将实时图层前置后重新分析（遮挡关系改变），或 --video-layout layered 分层视频（播放开销需另行验证）。",
            En: "The recordable image is transparent with no opaque base, so one full-frame video isn't possible. Options: --live-overlays foreground moves the live layers to the front and requires re-analysis (occlusion changes); --video-layout layered uses layered video (playback cost requires separate validation).",
            Legacy: "Full-frame mode requires one opaque video group; the selected scene settings currently need {0} group(s) or a transparent background.{3} Choose independent live overlays in the foreground and analyze again, change the scene settings, or explicitly choose layered video. Foreground placement changes occlusion; layered video requires separate playback-cost validation."),

        ["blocker.fullframe_single_transparent_group_live"] = new(
            Zh: "可录的画面是透明的，且中间夹着必须实时渲染的图层（{2}），无法合成一整段视频。共 {1} 个实时图层。可选方案：--live-overlays foreground 将这些实时图层前置后重新分析（遮挡关系改变），或 --video-layout layered 分层视频（播放开销需另行验证）。",
            En: "The recordable image is transparent and layers that must render live ({2}) sit in between, so one full-frame video isn't possible. {1} live layers in total. Options: --live-overlays foreground moves them to the front and requires re-analysis (occlusion changes); --video-layout layered uses layered video (playback cost requires separate validation).",
            Legacy: "Full-frame mode requires one opaque video group; the selected scene settings currently need {0} group(s) or a transparent background.{3} Choose independent live overlays in the foreground and analyze again, change the scene settings, or explicitly choose layered video. Foreground placement changes occlusion; layered video requires separate playback-cost validation."),

        ["blocker.fullframe_no_video_group"] = new(
            Zh: "画面全部由实时输入驱动，没有可转为视频的内容。依赖闭包后剩余可录分组为 0 组。",
            En: "All visible content is driven by live input, so nothing can be turned into video. Dependency closure leaves 0 recordable groups.",
            Legacy: "Full-frame mode requires one opaque video group; the selected scene settings currently need {0} group(s) or a transparent background.{3} Choose independent live overlays in the foreground and analyze again, change the scene settings, or explicitly choose layered video. Foreground placement changes occlusion; layered video requires separate playback-cost validation."),

        ["blocker.foreground_splits_video_group"] = new(
            Zh: "图层结构暂不支持转为视频。实时图层前置会使一个中间根节点留在原位，其后的根节点仍在同一视频组内；保持原始叠放顺序需显式选择分层视频（--video-layout layered）。",
            En: "This layer structure can't be turned into video yet. Foreground placement would retain a middle root while a later root stays in the same video group; preserving source order requires an explicit layered allocation (--video-layout layered).",
            Legacy: "Foreground dependency closure would retain a middle root while a later root remains in the same video group; preserving source order requires an explicit layered allocation."),

        // HybridBakeService.AssembleObjects 的层级检查，经 CompositionHierarchyConflict 进 blockers。英文逐字沿用抛出处的原文，
        // 无参数，旧版 plan 也能靠原文反查出中文（实测 --retain-live 换成源作者根后 3441873795、3521337568、3602673806 会撞上它）。
        // 以下三条原先是 AssembleAllocationObjects 直接抛出的英文，经 CompositionHierarchyConflict 原样进 blockers，
        // blockers_localized 的 key 为 null（3674038504 的 layer_count 查询）。legacy 逐字沿用抛出处原文。
        ["blocker.public_layer_query"] = new(
            Zh: "图层结构暂不支持转为视频。保留实时的脚本读取了 {0}，而混合导出会改变公开的图层{1}。请让会改变这个脚本所见图层视图的图层保持实时，然后重新分析。",
            En: "This layer structure can't be turned into video yet. A retained script reads {0}, but hybrid export changes the public layer {1}. Analyze again without baking layers that alter this script's public layer view.",
            Legacy: "A retained script queried {0}, but hybrid export changes the public layer {1}. Re-analyze without baking layers that alter this script's public layer view."),
        ["blocker.omitted_snapshot_dependency"] = new(
            Zh: "图层结构暂不支持转为视频。保留实时的对象依赖一个被省略的快照祖先或身份。",
            En: "This layer structure can't be turned into video yet. A retained object depends on an omitted snapshot ancestor or identity.",
            Legacy: "A retained object depends on an omitted snapshot ancestor or identity."),
        ["blocker.replacement_parent_mismatch"] = new(
            Zh: "图层结构暂不支持转为视频。视频替身必须挂在计划中的源同级父节点下。",
            En: "This layer structure can't be turned into video yet. A video replacement must use its planned source sibling parent.",
            Legacy: "A video replacement must use its planned source sibling parent."),
        ["blocker.hierarchy_changes_draw_order"] = new(
            Zh: "图层结构暂不支持转为视频。保留实时的父级层级会改变计划中视频与实时图层的绘制顺序；该分配不保持作者的同级叠放顺序，遮挡关系将改变。",
            En: "This layer structure can't be turned into video yet. Retained parent hierarchies would change the planned video and live draw order; the allocation does not preserve source sibling order and occlusion would change.",
            Legacy: "Retained parent hierarchies would change the planned video/live draw order. The selected allocation does not preserve source sibling order."),

        ["blocker.foreground_outside_composition"] = new(
            Zh: "图层结构暂不支持转为视频。前景依赖闭包涉及现有视频/实时合成之外的根节点，前置需要一次未经验证的重排。",
            En: "This layer structure can't be turned into video yet. Foreground dependency closure reached a root outside the existing video/live composition; placing it would require an unverified reorder.",
            Legacy: "Foreground dependency closure reached a root outside the existing video/live composition; placing it would require an unverified reorder."),

        // 第 1 批合并新增：全幅冲突的"本场景可执行选项"形态（feat/layout-demotion）。
        // {0}=视频组数 {1}=实时层短语（分语言） {2}=可执行选项（分语言） {3}=legacy 专用中段 {4}=legacy 专用英文选项。
        // 双语文案按实际状态分四种（legacy 四者共用、逐字不变）：透明背景不是并列备选，而是 N=1 时被卡住的原因。
        // N≥2 且块与块之间夹着可见实时层：{1} 点名这些层。分块边界也可能来自父节点或视差深度，所以不写"全是实时层切的"。
        ["blocker.fullframe_needs_opaque_group_options"] = new(
            Zh: "画面中间夹着必须实时渲染的图层，无法合成一整段视频。可录内容分为 {0} 个视频层，其间夹有：{1}本场景可行方案：{2}。",
            En: "Layers that must render live sit between the video parts, so one full-frame video isn't possible. The recordable content is split into {0} video layers with these in between:{1} Available options for this scene: {2}.",
            Legacy: "Full-frame mode requires one opaque video group; the selected scene settings currently split the bakeable content into {0} video group(s) or leave a transparent background.{3} Options: {4}."),

        // N≥2 但中间没有可见实时层（分块来自父节点或视差深度不同）：不写"被实时图层切开"。
        ["blocker.fullframe_split_groups_options"] = new(
            Zh: "画面分成 {0} 个视频层，没有一层能单独作为底层，无法合成一整段视频。本场景可行方案：{2}。",
            En: "The image splits into {0} video layers and none can serve as the base alone, so one full-frame video isn't possible. Available options for this scene: {2}.",
            Legacy: "Full-frame mode requires one opaque video group; the selected scene settings currently split the bakeable content into {0} video group(s) or leave a transparent background.{3} Options: {4}."),

        // N=1：唯一的一块不含场景清屏、是透明的。{1} 点名排在它前面先画的实时层（没有时为空）。
        ["blocker.fullframe_single_transparent_group_options"] = new(
            Zh: "可录的画面是透明的，没有不透明底层，无法合成一整段视频。可录内容仅 {0} 个视频层，不含场景清屏{1}。本场景可行方案：{2}。",
            En: "The recordable image is transparent with no opaque base, so one full-frame video isn't possible. The recordable content is {0} video layer without the scene clear{1}. Available options for this scene: {2}.",
            Legacy: "Full-frame mode requires one opaque video group; the selected scene settings currently split the bakeable content into {0} video group(s) or leave a transparent background.{3} Options: {4}."),

        // N=0：一块可录的画面都没剩下。
        ["blocker.fullframe_no_video_group_options"] = new(
            Zh: "当前设置下没有可转为视频的内容。剩余可录分组为 {0} 组。本场景可行方案：{2}。",
            En: "Nothing can be turned into video under the current settings. {0} recordable groups remain. Available options for this scene: {2}.",
            Legacy: "Full-frame mode requires one opaque video group; the selected scene settings currently split the bakeable content into {0} video group(s) or leave a transparent background.{3} Options: {4}."),

        // 第 1 批合并新增：唯一视频组被搬不走的实时绘制挡住（feat/single-shot-live）。{0}=阻挡者列表。
        // feat/tradeoff-list 改写：原文说"改设置或改成分层都不会改变这一点"，与实测冲突——这 4 案 4/4
        // 只要把挡路的可取舍实时元素关掉就能进整幅（3680252478 单加 --interaction fixed 即可），
        // 所以改成给出解锁路径。{1}=本场景可执行的关法（分语言，SingleShotAllocation 拼好）。
        // legacy 英文逐字沿用原 En，写进 plan 的 blockers 一个字没变。
        ["blocker.fullframe_unreachable"] = new(
            Zh: "视频层前面有无法移动的实时图层（{0}），无法合成一整段视频。前景放置仅接受独立的文字或粒子子树，均不满足。可选方案：禁用前置的实时元素后生成整幅循环视频（{1}），或 --video-layout layered 分层视频（播放开销需另行验证）。",
            En: "Live layers that can't be moved ({0}) are drawn before the video layer, so one full-frame video isn't possible. Foreground placement accepts only independent text or particle trees, none of which qualifies. Options: disable the blocking live elements and generate the full-frame loop video ({1}); or --video-layout layered (playback cost requires separate validation).",
            Legacy: "Full-frame mode needs the single video group to carry the scene clear, but that group is drawn after realtime layers that keep drawing first: {0}. Foreground placement only accepts independent text or particle trees, and none of these qualifies, so the video group cannot become the opaque base of this composition. This scene has no full-frame layout as authored."),

        // 第 1 批合并新增：视频外壳（feat/video-shell）。中英原文就在 VideoDominance 里，这里直接引用，避免两处漂移。
        ["blocker.video_shell"] = new(Zh: VideoDominance.BlockerZh, En: VideoDominance.Blocker),
        // {0}=命中的条件（各语言一套）。预计不省电的方案一律拒绝；--no-benefit allow 只给调试、测功耗用。
        ["blocker.no_benefit_expected"] = new(
            Zh: "不支持：预计功耗高于原壁纸（{0}）。调试或测功耗时可用 --no-benefit allow 生成。",
            En: "Not supported: estimated power use is higher than the original wallpaper ({0}). For debugging or power measurement, use --no-benefit allow to generate anyway."),

        // fix/residual-layout-gate → feat/particle-crossfade：透明组与多组已允许淡化（packed 域线性混合即预乘空间淡化），
        // 只剩"可掩盖分量所在的图层不在任何视频组里"这一种布局确实淡化不了的情形。
        // {0}=布局名 {1}=视频组数 {2}=透明组说明（分语言） {3}=未解析分量列表（分语言） {4}=本场景可执行的出路（分语言）。
        ["blocker.residual_masking_layout"] = new(
            Zh: "部分动画无法在接缝处平滑过渡。循环候选存在未解析分量 {3}，需在接缝处由其所在视频组做整帧交叉淡化，但这些图层不属于任何视频组（当前 {0} 布局、{1} 个视频组{2}），无组可执行淡化。可选方案：{4}。",
            En: "Some animation can't be smoothed at the seam. The loop candidate leaves unresolved components {3} that require a whole-frame crossfade in their own video group at the seam, but those layers belong to no video group (current layout {0}, {1} video group(s){2}), so no group can perform it. Options: {4}.",
            Legacy: "This loop candidate leaves unresolved components: {3}. Baking would hide them with a whole-frame crossfade in the video groups that contain them, but they are not in any video group (this plan uses the {0} layout with {1} video group(s){2}), so no group can crossfade them and baking this plan would be rejected. Options: {4}."),

        // fix/narrative-rc10：整层不可用、没有未解析机制，但求解器给出了结构化的空候选原因（HybridScenePlanner.RecordSolverNoCandidateBlocker）。
        // {0}=着色器周期分量数 {1}=运行时动画周期数 {2}=公共步长秒数 {3}=循环上限秒数
        ["blocker.loop_no_common_frame"] = new(
            Zh: "{3} 秒内找不到循环周期。待录内容的 {0} 个着色器周期分量与 {1} 条运行时动画周期均已证明，但按 {2} 秒公共步长逐帧检查，上限内无同相位帧。保持部分图层实时或修改壁纸设置后重新分析，结论可能不同。",
            En: "No loop period was found. The {0} shader period component(s) and {1} runtime animation period(s) are all proven, but on their common step of {2} s no frame within the {3} s loop ceiling returns them to the start together. Keeping some layers live or changing the wallpaper settings and analyzing again may change this.",
            Legacy: "The {0} shader period component(s) and {1} runtime animation period(s) in the recorded content are all proven, but checking every frame on their common {2} s step, no frame within the {3}-second loop ceiling brings them back to the start together, so the analysis established no loop. Keeping some of those layers live or changing the wallpaper settings and analyzing again may change this."),

        // {0}=不可调速的运行时动画周期数 {1}=它们重新对齐所需秒数 {2}=循环上限秒数
        ["blocker.loop_fixed_period_exceeds_ceiling"] = new(
            Zh: "{2} 秒内找不到循环周期。待录内容含 {0} 条不可调速的运行时动画周期，重新对齐需 {1} 秒，超过循环上限。",
            En: "No loop period was found. The recorded content has {0} runtime animation period(s) that cannot be retimed and realign only after {1} s, beyond the {2} s loop ceiling.",
            Legacy: "The recorded content has {0} runtime animation period(s) that cannot be retimed and only line up again after {1} s, beyond the {2}-second loop ceiling, so no loop within the ceiling can close."),

        ["blocker.loop_no_exact_video_retime"] = new(
            Zh: "找不到循环周期。待录内容仅有一段视频在变化，调速后无落在输出帧网格上的整数帧长。",
            En: "No loop period was found. The only changing element is a single video, and no retime lands its length on a whole number of output frames.",
            Legacy: "The only thing changing in the recorded content is a single video, and no retime lands its length on a whole number of output frames, so the analysis established no loop."),

        // feat/particle-crossfade：残差掩盖的逐层判定理由（ResidualMasking）。{0}=图层 id {1}=机制名
        ["residual.displacement_not_maskable"] = new(
            Zh: "层 {0} 的位移类分量 {1} 不可掩盖：接缝两侧同一元素位置不同，交叉淡化产生半透明双影，视觉定标判定为明显。需解析闭合其周期，或保持该层实时。",
            En: "Layer {0}'s displacement component {1} cannot be masked: the same element sits at different positions on either side of the seam, and a crossfade yields a semi-transparent double image that visual calibration judged obvious. Its period must close analytically, or the layer must stay live.",
            Legacy: "Layer {0}'s displacement component {1} would show a visible position jump through a crossfade: the same element sits in different places on either side of the seam, and a crossfade only turns the jump into a semi-transparent double image, which visual calibration judged obvious. It cannot be masked; its period must close analytically, or the layer must stay live."),

        // {0}=图层 id
        ["residual.particle_verdict_missing"] = new(
            Zh: "层 {0} 的粒子未解析项不可掩盖：计划中无平稳随机判据结论（旧版计划），淡化替换无法证明成立。请重新分析。",
            En: "Layer {0}'s particle item cannot be masked: the plan carries no stationary-random verdict (an older plan), so crossfade replacement cannot be shown to hold. Analyze the source again.",
            Legacy: "Layer {0}'s particle item carries no stationary-random verdict (an older plan), so crossfade replacement cannot be shown to hold and it cannot be masked; analyze the source again."),

        // {0}=图层 id {1}=不满足的条件代号清单
        ["residual.particle_not_stationary"] = new(
            Zh: "层 {0} 的粒子系统不可掩盖：不满足平稳随机判据（{1}），接缝淡化替换为统计等价段不成立。该层应保持实时。",
            En: "Layer {0}'s particle system cannot be masked: it fails the stationary-random criteria ({1}), so replacing the seam with a statistically equivalent stretch does not hold. Keep this layer live.",
            Legacy: "Layer {0}'s particle system fails the stationary-random criteria ({1}), so replacing the seam with a statistically equivalent stretch does not hold and it cannot be masked; keep this layer live."),

        // {0}=图层 id
        ["residual.particle_warmup_missing"] = new(
            Zh: "层 {0} 的粒子系统不可掩盖：已通过平稳随机判据，但缺少预热时长，起点搜索与 master 无法跑过预热。请重新分析。",
            En: "Layer {0}'s particle system cannot be masked: it passes the stationary-random criteria but has no warm-up duration, so the start search and the master cannot run past warm-up. Analyze the source again.",
            Legacy: "Layer {0}'s particle system passes the stationary-random criteria but has no warm-up duration, so the start search and master cannot run past warm-up and it cannot be masked; analyze the source again."),

        ["residual.particle_stationary_proof"] = new(
            Zh: "粒子定义满足平稳随机判据 C1–C9：发射率恒定、每个粒子的标记独立随机抽取、算子仅依赖年龄与平稳噪声场、无外部输入。预热后画面统计分布不随时间变化，间隔超过寿命上界的两帧互不相关，接缝交叉淡化等价于替换为统计等价段。",
            En: "The particle definition meets the stationary-random criteria C1-C9: constant emission rate, independently drawn particle marks, operators depending only on age and a stationary noise field, no external input. After warm-up the image statistics are time-invariant and frames separated by more than the lifetime bound share no particle, so the seam crossfade replaces the image with a statistically equivalent stretch.",
            Legacy: "The particle definition meets the stationary-random criteria C1–C9 (constant emission rate, independently drawn particle marks, operators that depend only on age and stationary noise, no external input): after warm-up the image statistics do not change over time and frames further apart than the lifetime bound share no particle, so the seam crossfade replaces the image with a statistically equivalent stretch."),

        // fix/particle-cyclo-lock。{0}=周期帧数
        ["residual.particle_cyclostationary_proof"] = new(
            Zh: "粒子定义除封顶替换外满足平稳随机判据 C1–C9：槽位常满、寿命确定，按渲染器逐帧推进复刻的计数状态自周期态起点起严格以 {0} 帧为周期重复，每批粒子的标记独立随机抽取。循环长度锁定为 {0} 帧的整数倍，接缝两侧同相位，交叉淡化替换为同相位的统计等价批次。",
            En: "Apart from its capped replacement the particle definition meets the stationary-random criteria C1-C9: every slot is filled with a fixed lifetime, the count state replayed frame by frame from the renderer's rules repeats exactly every {0} frames from its cycle start, and each batch draws its marks independently. The loop length is locked to a multiple of {0} frames, so both sides of the seam share a phase and the crossfade substitutes a same-phase, statistically equivalent batch.",
            Legacy: "Apart from its capped replacement the particle definition meets the stationary-random criteria C1–C9; with every slot filled and a fixed lifetime, the count state replayed frame by frame from the renderer's rules repeats exactly every {0} frames from its cycle start, and each batch draws its marks independently. The loop length is locked to a multiple of {0} frames, so both sides of the seam sit in the same phase and the crossfade replaces it with a same-phase, statistically equivalent batch."),

        // fix/particle-cyclo-lock：bake 时核对循环长度。{0}=图层 id {1}=周期帧数 {2}=循环帧数 {3}=计划帧率 {4}=锁定时的帧率
        ["residual.particle_cycle_mismatch"] = new(
            Zh: "层 {0} 的粒子系统不可掩盖：替换周期锁定为 {1} 帧（{4} fps），本次循环为 {2} 帧（{3} fps），非其整数倍，接缝两侧相位不同，交叉淡化替换不成立。请重新分析后再生成。",
            En: "Layer {0}'s particle system cannot be masked: its replacement period is locked to {1} frames at {4} fps, while this loop is {2} frames at {3} fps, which is not a multiple of it; the two sides of the seam differ in phase and crossfade replacement does not hold. Analyze the source again before generating.",
            Legacy: "Layer {0}'s particle system is locked to a replacement period of {1} frames at {4} fps, but this loop is {2} frames at {3} fps, which is not a multiple of it; the two sides of the seam are not in the same phase, so crossfade replacement does not hold. Analyze the source again before baking."),

        // fix/mdl-lenient-strings：原生渲染器读不了某个素材文件，运行时观测起不来（AnalysisToolLimitation）。
        // {0}=文件名 {1}=字段 {2}=问题短语（分语言，取自 asset_problem.*） {3}=文件偏移 {4}=引用它的图层短语（分语言，可为空）。
        ["blocker.tool_unsupported_model_field"] = new(
            Zh: "不支持此壁纸中的 3D 模型文件 {0}{4}。原生渲染器在字段 {1} 出错（{2}，文件偏移 {3}），场景无法开始运行时观测。",
            En: "This wallpaper's 3D model file isn't supported. File: {0}{4}. The native renderer failed on field {1} ({2} at file offset {3}), so the scene could not start runtime observation.",
            Legacy: "The tool cannot read this 3D model file yet: {0}{4}. The native renderer failed on field {1} ({2} at file offset {3}), so the scene could not start its runtime observation and was not analyzed."),

        // 渲染器没给出字段与偏移时的形态。{0}=文件名 {1}=引用它的图层短语 {2}=渲染器原话（英文）。
        ["blocker.tool_unsupported_model"] = new(
            Zh: "不支持此壁纸中的 3D 模型文件 {0}{1}。原生渲染器报告：{2}。场景无法开始运行时观测。",
            En: "This wallpaper's 3D model file isn't supported. File: {0}{1}. The native renderer reported: {2}. The scene could not start runtime observation.",
            Legacy: "The tool cannot read this 3D model file yet: {0}{1}. The native renderer reported: {2}. The scene could not start its runtime observation and was not analyzed."),

        // {0}=渲染器报出的纹理名 {1}=被判无效的那部分数据（渲染器原词，如 mipmap）。
        ["blocker.tool_unsupported_texture"] = new(
            Zh: "不支持此壁纸中的纹理文件 {0}。原生渲染器判定其 {1} 数据无效。",
            En: "This wallpaper's texture file isn't supported. File: {0}; the native renderer rejected its {1} data as invalid.",
            Legacy: "The tool cannot read this texture file yet: {0} (the native renderer rejected its {1} data as invalid)."),

        // ---- 素材解析失败的问题短语与图层短语：只作为上面几条的参数嵌进句子 ----
        ["asset_problem.invalid_utf8"] = new(Zh: "字符串不是合法的 UTF-8", En: "the string is not valid UTF-8"),
        ["asset_problem.unterminated"] = new(Zh: "字符串没有结尾", En: "the string is unterminated"),
        ["asset_problem.truncated"] = new(Zh: "数据被截断", En: "the data is truncated"),
        ["asset_problem.unsupported"] = new(Zh: "取值不在已知范围内", En: "the value is outside the range the renderer knows",
            Legacy: "the value is not one the renderer knows"),
        ["asset_problem.invalid_boundary"] = new(Zh: "数据块边界非法", En: "the block boundary is invalid"),
        ["asset_problem.non_finite"] = new(Zh: "含有非有限数值", En: "it contains a non-finite number"),
        ["asset_layers.used_by"] = new(Zh: "（引用图层 {0}）", En: " (referenced by layer {0})",
            Legacy: " (used by layer {0})"),

        // ---- loop.unresolved[].detail ----
        ["unresolved.owner_or_duration_unresolved"] = new(
            Zh: "运行时动画的时长或所属图层无法精确确定，未计入循环。",
            En: "The runtime duration or source owner cannot be resolved exactly, so it is not counted toward the loop.",
            Legacy: "Runtime duration or source owner cannot be resolved exactly."),

        ["unresolved.video_needs_exact_duration"] = new(
            Zh: "视频播放需要精确的有理数源时长与不受脚本控制的固定循环，当前不满足；该项作为锁定的捕获约束保留，循环长度须与之对齐。",
            En: "Video playback requires an exact rational source duration and an uncontrolled fixed loop, which are not met; it remains a locked capture constraint and the loop length must align with it.",
            Legacy: "Video playback needs an exact rational source duration and an uncontrolled fixed loop; it remains a locked capture constraint."),

        ["unresolved.video_rate_not_one"] = new(
            Zh: "仅 1.0 倍视频播放速率可证明，本项速率不为 1.0，不对视频做调速。",
            En: "Only a video playback rate of exactly 1.0 is provable; this rate is not 1.0, so no video retiming is emitted.",
            Legacy: "Only an exactly one-times video playback rate is currently provable; video rate retiming is not emitted."),

        ["unresolved.animation_not_high_confidence"] = new(
            Zh: "该运行时动画不是高置信度的循环周期轨道（可能不循环、由事件触发，或观测置信度不足），无法据此推出周期。",
            En: "The runtime animation is not a high-confidence looping periodic track (it may not loop, may be event-driven, or observation confidence is insufficient), so no period follows from it.",
            Legacy: "Runtime animation is not a high-confidence looping periodic track."),

        // feat/video-control-scope 之后生产代码不再走这条笼统文案，改由下面七条具名理由代替；
        // 这里保留它，好让旧版本生成的 plan 仍能反查出中文。
        // 第 2 批合并新增（feat/video-control-scope）：粒子证明不出周期时，说清是哪一种随机源或外部输入。
        // 英文逐字沿用该分支的原文，中文是同一句话的改写。{n} 里的节点名用 Message 的 ZhArgs，中英各一套。
        ["unresolved.particle_definition_missing"] = new(
            Zh: "粒子精灵纹理周期不等于粒子系统的有效周期：该层未指明粒子定义文件，无法排除随机源。",
            En: "A particle sprite texture period does not establish the particle system's effective period: the layer names no particle definition, so no random source can be ruled out.",
            Legacy: "A particle sprite texture period does not establish the particle system's effective period: this layer names no particle definition, so no random source could be ruled out."),

        ["unresolved.particle_definition_unreadable"] = new(
            Zh: "粒子精灵纹理周期不等于粒子系统的有效周期：粒子定义 \"{0}\" 读取失败（{1}），无法排除随机源。",
            En: "A particle sprite texture period does not establish the particle system's effective period: its particle definition \"{0}\" could not be read ({1}), so no random source can be ruled out.",
            Legacy: "A particle sprite texture period does not establish the particle system's effective period: its particle definition \"{0}\" could not be read ({1}), so no random source could be ruled out."),

        ["unresolved.particle_audio_input"] = new(
            Zh: "粒子精灵纹理周期不等于粒子系统的有效周期：{0} 响应实时音频频谱（audioprocessingmode {1}），属外部实时输入，源中无可证明周期。",
            En: "A particle sprite texture period does not establish the particle system's effective period: {0} responds to the live audio spectrum (audioprocessingmode {1}), an external input with no source-provable period.",
            Legacy: "A particle sprite texture period does not establish the particle system's effective period: {0} responds to the live audio spectrum (audioprocessingmode {1}), which is an external input with no source-provable period."),

        ["unresolved.particle_turbulent_velocity"] = new(
            Zh: "粒子精灵纹理周期不等于粒子系统的有效周期：{0} 采样按自身时间尺度推进的湍流噪声场，源中未给出该场的相位周期。",
            En: "A particle sprite texture period does not establish the particle system's effective period: {0} samples a turbulent noise field advancing on its own time scale, and no source states the field's phase period.",
            Legacy: "A particle sprite texture period does not establish the particle system's effective period: {0} samples a turbulent noise field that advances on its own time scale, and no source states the field's phase period."),

        ["unresolved.particle_random_frame"] = new(
            Zh: "粒子精灵纹理周期不等于粒子系统的有效周期：精灵动画为 randomframe 模式，每个粒子的起播帧不可预测。",
            En: "A particle sprite texture period does not establish the particle system's effective period: its sprite animation runs in randomframe mode, so each particle's starting frame is unpredictable.",
            Legacy: "A particle sprite texture period does not establish the particle system's effective period: its sprite animation runs in randomframe mode, so every particle starts on an unpredictable frame."),

        ["unresolved.particle_random_initializer"] = new(
            Zh: "粒子精灵纹理周期不等于粒子系统的有效周期：{0} 为每个粒子在 {1} 与 {2} 之间抽取随机值，系统不回到先前状态。",
            En: "A particle sprite texture period does not establish the particle system's effective period: {0} draws every particle between {1} and {2}, so the system never returns to an earlier state."),

        ["unresolved.particle_emitter_extent"] = new(
            Zh: "粒子精灵纹理周期不等于粒子系统的有效周期：{0} 在非零 distancemax（{1}）范围内发射，出生位置为随机抽取。",
            En: "A particle sprite texture period does not establish the particle system's effective period: {0} spawns across a non-zero distancemax ({1}), so spawn positions are drawn at random."),

        ["unresolved.particle_effective_period_not_modelled"] = new(
            Zh: "粒子精灵纹理周期不等于粒子系统的有效周期：\"{0}\" 中的随机源均为退化（取值区间零宽），但由发射间隔、生命期与 operator 时间尺度合成的有效周期尚未建模。",
            En: "A particle sprite texture period does not establish the particle system's effective period: every random source in \"{0}\" is degenerate (zero-width ranges), but the effective period formed by emission interval, lifetime and operator time scales is not modelled yet.",
            Legacy: "A particle sprite texture period does not establish the particle system's effective period: every random source in \"{0}\" is degenerate, but the effective period built from emission interval, lifetime and operator time scales is not modelled yet."),

        // feat/particle-stationarity：没有精灵轨道的粒子层也记一条未解析项，结论来自 ParticleStationarity 的 C1–C9 判据。
        // {0} 是不满足条件的代号清单（例如 "C4 controlpoint_follows_cursor"），代号本身不分语言。
        ["unresolved.particle_stationary_random"] = new(
            Zh: "该粒子系统满足平稳随机判据（发射率恒定、每个粒子独立随机抽样、无外部输入）：无周期，但预热后画面统计分布不随时间变化，接缝可交叉淡化替换为统计等价段。",
            En: "This particle system meets the stationary-random criteria (constant emission rate, independently drawn particles, no external input): it has no period, but after warm-up its image statistics are time-invariant, so the seam can be crossfaded into a statistically equivalent stretch.",
            Legacy: "This particle system meets the stationary-random criteria (constant emission rate, independently drawn particles, no external input): it has no period, but after warm-up its image statistics do not change over time, so the seam can be crossfaded into a statistically equivalent stretch."),

        ["unresolved.particle_not_stationary"] = new(
            Zh: "该粒子系统无可证明周期，且不满足平稳随机判据（{0}），接缝不可由交叉淡化替换掩盖，该层应保持实时。",
            En: "This particle system has no provable period and fails the stationary-random criteria ({0}), so a crossfade cannot mask its seam; keep this layer live.",
            Legacy: "This particle system has no provable period and does not meet the stationary-random criteria ({0}), so a crossfade cannot hide its seam; keep this layer live."),

        // fix/particle-cyclo-lock：槽位常满、寿命确定的粒子层按替换周期锁定。{0}=周期帧数 {1}=周期秒数
        ["unresolved.particle_cyclostationary_locked"] = new(
            Zh: "该粒子系统槽位常满、寿命确定：每 {0} 帧（{1} 秒）整批替换同一组槽位，计数严格按该周期重复，仅每批粒子的随机属性不同。循环长度已锁定为其整数倍，接缝两侧同相位，可交叉淡化替换为同相位的统计等价批次。",
            En: "This particle system keeps every slot filled with a fixed lifetime: the same slots are replaced as a batch every {0} frames ({1} s), so its counts repeat exactly on that period and only each batch's random marks differ. The loop length is locked to a multiple of it, both sides of the seam share a phase, and a crossfade substitutes a same-phase, statistically equivalent batch.",
            Legacy: "This particle system keeps every slot filled with a fixed lifetime: the same slots are replaced as a batch every {0} frames ({1} s), so its counts repeat exactly on that period and only each batch's random marks differ. The loop length is locked to a multiple of it, both sides of the seam sit in the same phase, and a crossfade replaces the seam with a same-phase, statistically equivalent batch."),

        ["unresolved.script_random_restart"] = new(
            Zh: "脚本通过 Math.random() 延迟后重启该精灵动画，任何录制长度下均无周期，调整接缝位置也不重复。生成前保持该层实时或禁用该层。",
            En: "Script playback restarts this sprite animation after a Math.random() delay, so it has no period at any capture length and no seam placement makes it repeat. Keep this layer live or disable it before generating.",
            Legacy: "Script playback restarts this sprite animation after a Math.random() delay, so it has no period at any capture length and no seam placement can make it repeat. Keep this layer live or turn it off before baking."),

        ["unresolved.script_controlled_playback"] = new(
            Zh: "该动画的播放由脚本控制，底层动画时长不构成固定周期；仍需完整录制并验证接缝。",
            En: "Playback of this animation is script-controlled, so the underlying animation duration is not a fixed period; full capture and seam validation remain required.",
            Legacy: "Script-controlled playback is not a fixed period inferred from the underlying animation duration; full capture and seam validation remain required."),

        ["unresolved.script_time"] = new(
            Zh: "该层的脚本在运行中读取时间，其状态周期尚未建模；模型动画或材质的周期不能证明脚本状态也会闭合。需保持该层实时，或另行求解脚本状态的循环。",
            En: "This layer's script reads time during playback; a model or material period does not establish a loop for the script state. Keep the layer live or solve its script-state loop separately."),

        ["unresolved.script_frame_step_lcm_over_ceiling"] = new(
            Zh: "该层脚本每帧按常量步进、越界复位，按单精度逐帧模拟的周期为 {0} 帧；被烘的 {1} 条这类脚本合起来的公共周期为 {2} 帧，超过循环上限，没有能同时闭合它们的循环。",
            En: "This layer's script steps by a constant every frame and resets past a bound; simulated in single precision its period is {0} frames. The {1} such scripts being baked share a joint period of {2} frames, beyond the loop ceiling, so no loop closes all of them."),

        ["unresolved.playback_rate_unresolved"] = new(
            Zh: "运行时 playback_rate 无法确定精确的正周期。",
            En: "The runtime playback_rate cannot establish an exact positive period.",
            Legacy: "Runtime playback_rate cannot establish an exact positive period."),

        ["unresolved.sprite_float32_seam_mismatch"] = new(
            Zh: "精灵帧时长以 float32 存储，解析周期在输出帧网格上与起点相差一个精灵帧；自起点与整周期预热后均未能证明接缝闭合，相关候选已移除。",
            En: "Sprite frame times are stored as float32, so the analytic period lands one sprite frame from its start on the output frame grid; neither the origin nor a one-period warm-up proves the seam closed, and those candidates were removed.",
            Legacy: "Sprite frame times are stored as float32; the analytic period lands one sprite frame away from its start on the output frame grid, and neither the origin nor a one-period warmup proves the seam closed, so those candidates were removed."),

        ["unresolved.sprite_rate_not_one"] = new(
            Zh: "精灵时长仅在 playback_rate 为 1 时等于作者设定周期，当前速率不为 1。",
            En: "Sprite duration equals the authored period only at playback_rate 1, and the current rate is not 1.",
            Legacy: "Sprite duration is an authored period only at playback_rate 1."),

        ["unresolved.no_authored_rate_patch"] = new(
            Zh: "未找到与运行时 playback_rate 精确匹配的作者动画速率补丁，该轨道周期按固定值处理，不参与调速。",
            En: "No authored animation rate patch matches the runtime playback_rate exactly; the track's period is treated as fixed and excluded from retiming.",
            Legacy: "No exact authored animation rate patch matches runtime playback_rate; period remains fixed."),

        ["unresolved.material_uniforms_invalid"] = new(
            Zh: "运行时材质的 active_uniforms 含格式非法项，无法判定其是否随时间变化。",
            En: "The runtime material's active_uniforms contains an invalid entry, so its time dependence cannot be determined.",
            Legacy: "Runtime material active_uniforms has an invalid entry."),

        // 第 1 批合并改写：feat/shader-verdict-dedup 之后判据不再看 role，文案里的 non-effect 字样随之去掉。
        ["unresolved.material_omits_active_uniforms"] = new(
            Zh: "该运行时材质未给出 active_uniforms 列表，既不能证明静止，也无法分析其时间机制。",
            En: "The runtime material omits the active_uniforms list, so it can establish neither a static state nor an analyzed temporal state.",
            Legacy: "Runtime material omitted active_uniforms; it cannot establish a static or analyzed temporal state."),

        ["unresolved.material_role_not_string"] = new(
            Zh: "运行时材质的 role 字段不是字符串，认不出它是哪类材质，也就判断不了它会不会随时间变化。",
            En: "Runtime material role is not a string, so its temporal behavior is unknown."),

        ["unresolved.material_temporal_uniforms"] = new(
            Zh: "运行时 {0} 材质使用尚未建模的时间变量 {1}，周期无法证明。",
            En: "The runtime {0} material uses unmodeled temporal uniforms {1}, so its period cannot be proven.",
            Legacy: "Runtime {0} material uses unmodeled temporal uniforms: {1}."),

        ["unresolved.shader_runtime_clock_unverified"] = new(
            Zh: "该着色器使用运行时时钟或帧间隔（g_Runtime / g_Frametime 一类），而非已验证的周期性 g_Time 公式，无法证明循环。",
            En: "The shader uses a runtime or delta clock (g_Runtime / g_Frametime) rather than a verified periodic g_Time equation, so looping cannot be proven.",
            Legacy: "Shader uses a runtime or delta clock without a verified periodic g_Time equation."),

        ["unresolved.shader_mixed_clock"] = new(
            Zh: "该着色器混用 g_Time 与运行时时钟/帧间隔，周期无法证明。",
            En: "The shader mixes g_Time with a runtime or delta clock, so its period cannot be proven.",
            Legacy: "Shader mixes g_Time with a runtime or delta clock, so its period is not proven."),

        ["unresolved.shader_not_verified_periodic"] = new(
            Zh: "该着色器源码不匹配任何已验证的周期公式，无法证明循环。",
            En: "The shader source matches no verified periodic equation, so looping cannot be proven.",
            Legacy: "Shader source does not match a verified periodic equation."),

        // ---- bake 拒绝理由（写进 bake.json 的 reason / reason_localized） ----
        // 特效前缀按不透明视频规划，但全分辨率捕获读到 alpha<255。{0}=图层 id {1}=层名 {2}=帧 {3}/{4}=坐标
        // {5}=该点 alpha {6}=该帧非不透明像素数 {7}=该帧最低 alpha。
        ["bake.effect_prefix_nonopaque_capture"] = new(
            Zh: "不可生成：图层 {0}「{1}」的原尺寸首帧全像素不透明，但完整捕获在第 {2} 帧 ({3}, {4}) 读到 alpha={5}；该帧有 {6} 个像素非完全不透明（最低 alpha {7}）。首帧结论不能覆盖后续透明度变化，当前不透明编码会丢失这些信息，因此已中止本次生成；原壁纸未改动，相关内容保持实时。",
            En: "Layer {0} (\"{1}\") was opaque in the native-size first frame, but full capture read alpha={5} at ({3}, {4}) in frame {2}, affecting {6} pixel(s) (lowest alpha {7}). The first frame did not establish opacity throughout the animation. RGB encoding would lose this transparency, so generation stopped; the source is unchanged and remains live."),

        // 合成比对里候选的脚本报错多于原作。{0}=候选报错条数 {1}=原作报错条数 {2}=前几条多出来的报错（已按语言拼好）。
        ["bake.candidate_script_errors"] = new(
            Zh: "不可生成：候选工程在合成比对的短段渲染中产生 {0} 条脚本报错，原作以同一渲染器与参数渲染为 {1} 条。多出的报错表明保留的脚本依赖了已生成为视频或已丢弃的对象与数据（例如经 shared 全局对象提供的函数），候选画面不可信。已直接拒绝：未判定像素差异，未执行循环与接缝检查，未产出候选工程，原壁纸未改动。前几条多出报错：{2}",
            En: "Cannot generate: the candidate project raised {0} script error(s) in the short composition render, against {1} for the original rendered with the same renderer and parameters. The extra errors indicate that retained scripts depend on objects or data that were turned into video or dropped (for example functions provided through the shared global object), so the candidate image is not trustworthy. Rejected outright: pixel differences were not judged, no loop or seam check ran, no candidate project was created, and the source wallpaper is unchanged. First extra errors: {2}",
            Legacy: "The candidate project raised {0} script error(s) in the short composition render, while the original rendered with the same renderer and parameters raised {1}. The extra errors mean retained scripts depend on objects or data that were baked into video or dropped (for example functions provided through the shared global object), so the candidate image cannot be trusted and it was rejected outright: pixel differences were not judged, no loop or seam check ran, no candidate project was created and the source wallpaper is untouched. First extra errors: {2}"),

        // 一条脚本报错。{0}=图层 id {1}=层名 {2}=绑定属性 {3}=阶段 {4}=报错原文。
        ["bake.script_error_item"] = new(
            Zh: "图层 {0}「{1}」{2}/{3}：{4}",
            En: "layer {0} (\"{1}\") {2}/{3}: {4}"),

        // 同一对象、属性、阶段、报错原文重复多次时合成一条。{5}=条数。
        ["bake.script_error_item_repeated"] = new(
            Zh: "图层 {0}「{1}」{2}/{3}：{4}（{5} 条）",
            En: "layer {0} (\"{1}\") {2}/{3}: {4} ({5} times)"),

        ["bake.script_error_list_unavailable"] = new(
            Zh: "渲染结果里没有逐条报错明细。",
            En: "the render result has no per-error details."),

        // 残差掩盖路线：按起点排序依次在全分辨率 master 上复核的候选起点，第一层全部被拒。
        // {0}=尝试过的起点数 {1}=候选起点总数 {2}=每个起点的各组读数（分语言） {3}=瓦片上限 {4}=整幅上限 {5}=淡化窗口最后一个 k
        ["bake.residual_start_attempts_rejected"] = new(
            Zh: "不可生成：在锁定的解析周期内，按起点排序在全分辨率无损 master 上复核了 {0} 个候选起点（共 {1} 个），全部超出第一层阈值：{2}。第一层要求淡化窗口内每个 k = 0..{5} 的残差 Δ_k = f[P+k] − f[k] 最差 64px 瓦片不超过 {3}/255，硬切残差 Δ_0 的整幅 RGB MAE 不超过 {4}/255；超出后淡化重影与接缝步进可见。周期未更改，阈值未放宽，未做局部接缝修复，未自动调整分组。",
            En: "Cannot generate: within the locked analytic period, {0} candidate start frame(s) of {1} were re-checked in sort order on the full-resolution lossless master and all exceeded the first layer: {2}. The first layer requires the worst 64px tile of the residual Δ_k = f[P+k] − f[k] to stay within {3}/255 for every k = 0..{5} of the crossfade window, and the hard-cut residual Δ_0 to stay within {4}/255 whole-frame RGB MAE; beyond that the crossfade ghost and the seam step are visible. The period was not changed, no threshold was relaxed, no local seam repair was applied, and the grouping was not adjusted automatically.",
            Legacy: "Within the locked analytic period, {0} candidate start frame(s) (of {1}) were re-checked in sort order on the full-resolution lossless master and every one exceeded the first layer: {2}. The first layer requires the worst 64px tile of the residual Δ_k = f[P+k] − f[k] to stay within {3}/255 for every k = 0..{5} of the crossfade window, and the hard-cut residual Δ_0 to stay within {4}/255 whole-frame RGB MAE; beyond that the crossfade ghost and seam step become visible. The period was not changed, no threshold was relaxed, no local seam repair was applied and the grouping was not changed automatically."),

        // 残差掩盖路线：起点搜索的样本上没有一个起点过整幅 Δ_0 限，超限组含被掩盖的粒子。{0}=各组在选定起点的整幅读数 {1}=整幅上限
        ["bake.residual_start_search_rejected"] = new(
            Zh: "本遍不渲染：起点搜索的样本上没有一个起点的硬切残差 Δ_0 整幅 RGB MAE 在 {1}/255 以内（{0}），全分辨率复核必然超限；超限组里有被掩盖的粒子，改为让这些粒子保持实时后重新生成。",
            En: "Not rendered: no start frame in the start search kept the hard-cut residual Δ_0 within {1}/255 whole-frame RGB MAE ({0}), so the full-resolution check would exceed it; the rejected groups contain masked particles, which are kept live for the next generation."),

        // ---- bake 结果（界面一行说明） ----
        // ---- effect_prefix 终端捕获点（analyze 写进 loop.unresolved 与 effect_prefix_capture_probes，bake 写进 reason） ----
        // {0}=层名 {1}=层 id {2}=终端效果 id {3}=实际捕获点 {4}=这一层自己的渲染目标列表
        ["effect_prefix.capture_not_layer_target"] = new(
            Zh: "图层“{0}”的特效无法单独录制。",
            En: "The effects on layer \"{0}\" can't be recorded separately.",
            Legacy: "No effect-prefix cache for layer \"{0}\" (id {1}): terminal effect {2} is actually captured from {3}, which is not this layer's own render target ({4}). The renderer draws this layer's last effect straight into a shared buffer, so recording it would mix in other layers' pixels."),

        // {0}=层名 {1}=层 id {2}=终端效果 id {3}=探测报错原文
        ["effect_prefix.capture_probe_failed"] = new(
            Zh: "图层“{0}”的特效无法单独录制。",
            En: "The effects on layer \"{0}\" can't be recorded separately.",
            Legacy: "No effect-prefix cache for layer \"{0}\" (id {1}): the probe that confirms where terminal effect {2} is captured did not complete ({3}), so it cannot be shown to record this layer's own texture."),

        // ---- 硬件解码实测的适用范围（NativeRenderRunner.ProbeHardwareDecodeAsync 的结论文案）----
        // 实测只在烘焙机上做，用户在播放机上播；这几条只写进结果与界面文案，不参与任何判定。
        // {0}=硬解通过的显卡名单
        ["hardware_decode.verified_on_baking_machine"] = new(
            Zh: "硬件解码：在 {0} 上通过（仅测试本机）。",
            En: "Hardware decode: passed on {0} (this PC only).",
            Legacy: "Hardware decode was only verified on this baking machine: it passed on {0}. A playback machine with a different GPU (especially an integrated one) may still fail to decode it; to confirm, copy the finished file to that machine and run wpe-baker decode-check there."),

        // {0}=参与验证的显卡名单
        ["hardware_decode.none_passed_on_baking_machine"] = new(
            Zh: "硬件解码：本机显卡均未通过（{0}）。",
            En: "Hardware decode: no GPU on this PC passed ({0}).",
            Legacy: "No adapter on this baking machine decoded it in hardware ({0}), so a playback machine, especially an integrated one, cannot be expected to either."),

        ["hardware_decode.no_adapters_on_baking_machine"] = new(
            Zh: "硬件解码：本机无可测试的显卡。",
            En: "Hardware decode: no GPU to test on this PC.",
            Legacy: "No adapter suitable for hardware decode verification was found on this baking machine, so nothing was verified here and hardware decode on the playback machine is entirely unknown."),

        // {0}=参与验证的独显名单
        ["hardware_decode.no_integrated_verified"] = new(
            Zh: "仅测试了独立显卡（{0}），未测试核显；核显的硬件解码上限通常更低，在核显上播放风险更高。",
            En: "Only discrete GPUs were tested ({0}); integrated GPUs usually have lower decode limits, so playback on an integrated GPU is riskier.",
            Legacy: "Only discrete GPUs took part ({0}); no integrated GPU was tested. Integrated decoders usually have lower limits, so an integrated playback GPU is a markedly higher risk."),

        // {0}=编解码 {1}=码流尺寸 {2}=越限项 {3}=常见核显上限 {4}=依据（分语言）
        ["hardware_decode.beyond_integrated_ceiling"] = new(
            Zh: "该 {0} 码流为 {1}，超出常见核显的硬件解码上限 {3}（{2}），多数核显可能无法硬件解码（依据：{4}）。",
            En: "This {0} bitstream is {1}, beyond the common integrated-GPU decode ceiling of {3} ({2}), so most integrated GPUs may fail to decode it (basis: {4}).",
            Legacy: "This {0} bitstream is {1}, beyond the common integrated-GPU decode ceiling of {3} ({2}), so most integrated GPUs may fail to decode it (basis: {4}). This is advisory and changes no verdict here."),

        // ---- 编码后接缝校验（参照式）的拒绝理由 ----
        // {0}=P {1}=瓦片边长 {2}/{3}=最差瓦片坐标 {4}=RGB MAE {5}=透明组的 alpha 读数（bake.loop_not_closed_alpha，不透明为空） {6}=上限
        ["bake.loop_not_closed"] = new(
            Zh: "不可生成：渲染器自起点连续播放至第 {0} 帧未回到第 0 帧，最差 {1}px 瓦片 ({2}, {3}) 的 RGB MAE 为 {4}/255{5}，超过 8 位取整容差 {6}/255，{0} 帧不是该内容的真实周期（例如解析周期错误，或精灵帧取整偏差一帧）。",
            En: "Cannot generate: rendering on from the start to frame {0} did not return to frame 0; the worst {1}px tile at ({2}, {3}) differs by RGB MAE {4}/255{5}, beyond the 8-bit rounding allowance of {6}/255, so {0} frames is not the content's true period (for example a wrong analytic period, or a sprite frame rounded one frame off).",
            Legacy: "Rendering on from the start to frame {0} did not return to frame 0: the worst {1}px tile at ({2}, {3}) differs by RGB MAE {4}/255{5}, beyond the 8-bit rounding allowance of {6}/255, so {0} frames is not the content's true period (for example a wrong analytic period, or a sprite frame rounded one frame off)."),

        // {0}=alpha 最差瓦片 MAE
        ["bake.loop_not_closed_alpha"] = new(
            Zh: "，alpha 最差瓦片为 {0}/255",
            En: " (worst alpha tile {0}/255)"),

        // {0}=解码帧数 {1}=应有帧数
        ["bake.encoded_frame_count_mismatch"] = new(
            Zh: "成品解码出 {0} 帧，应为 {1} 帧。",
            En: "The encoded video decodes to {0} frames instead of {1}."),

        // {0}=实际帧率 {1}=应有帧率
        ["bake.encoded_frame_rate_mismatch"] = new(
            Zh: "成品帧率为 {0}，应为 {1}。",
            En: "The encoded video runs at {0} instead of {1}."),

        // {0}=上面几条之一或几条相连
        ["bake.encoded_seam_rejected"] = new(
            Zh: "不可生成：源周期编码未通过接缝校验：{0}未做接缝修复或尾段拼接，其余捕获、转换检查与工程组装已跳过。",
            En: "Cannot generate: the source-period encoding failed the required seam check: {0} No seam repair or tail splicing was applied; remaining captures, conversion checks and project assembly were skipped.",
            Legacy: "The original source-period encoding failed the required seam check: {0} No seam repair or tail splicing is applied; remaining captures, conversion checks and project assembly were skipped."),

        ["bake.effect_prefix_seam_rejected"] = new(
            Zh: "不可生成：特效前缀按源周期编码后未通过接缝校验：{0}未做修补。",
            En: "Cannot generate: the unmodified source-period prefix encoding failed its seam validation: {0} No repair was applied.",
            Legacy: "The unmodified source-period prefix encoding failed its seam validation: {0} No repair was applied."),

        // ---- CLI 入口（替换点留待与入口分类分支合并时处理） ----
        ["cli.not_scene_project"] = new(
            Zh: "不可生成：来源不是 Scene 类壁纸，本工具仅处理带 scene.pkg 的 Scene 壁纸。视频壁纸本身即为视频，直接使用原文件；预设包不含壁纸内容，改为分析其依赖的壁纸目录。",
            En: "Cannot generate: the source is not a Scene wallpaper. The baker handles only Scene wallpapers that ship a scene.pkg; a video wallpaper is already a video and needs no pre-rendering, and a preset package carries no wallpaper of its own - analyze the wallpaper it depends on instead.",
            Legacy: "Scene projects only. Video/Web media compression was removed."),

        ["cli.video_wallpaper"] = new(
            Zh: "不可生成：来源是视频壁纸（project.json type={0}，内容 {1}），本工具仅处理带 scene.pkg 的 Scene 类壁纸；视频壁纸本身即为视频，直接使用原文件。",
            En: "Cannot generate: the source is a video wallpaper (project.json type={0}, content {1}); the baker handles only Scene wallpapers that ship a scene.pkg, and a video wallpaper needs no pre-rendering.",
            Legacy: "Scene projects only. Video/Web media compression was removed."),

        ["cli.preset_package"] = new(
            Zh: "不可生成：该条目是预设（preset），不含壁纸内容：project.json 无 file 字段，仅有 dependency={0}。改为分析其依赖的壁纸目录 431960\\{0}。",
            En: "Cannot generate: this item is a preset and carries no wallpaper of its own - its project.json has no file entry, only dependency={0}. Analyze that wallpaper's folder (431960\\{0}) instead.",
            Legacy: "Scene projects only. Video/Web media compression was removed."),

        // ---- 来源判定（SourceDiagnosis）：GUI 拖入与 CLI analyze 共用同一批文案 ----
        // legacy 英文逐字沿用入口分类分支已经在用的那几句，scripts/test-cli-scene-only.py 按它对账。
        // 上面三条 cli.* 是这套文案的旧占位（legacy 是更早的一句话），保留原样不动。
        ["source.not_scene_project"] = new(
            Zh: "仅支持场景（Scene）类壁纸，此壁纸类型为 {0}。",
            En: "Only Scene wallpapers are supported; this one is {0}.",
            Legacy: "This project is not a Scene wallpaper (project.json type={0}). WPE Baker bakes Scene wallpapers only: the source must be a Scene project that ships scene.pkg or scene.json."),

        // {1} 是"，内容是 xxx"这半句；project.json 没写 file 时为空串，所以中英各带一套参数。
        ["source.video_wallpaper"] = new(
            Zh: "视频壁纸本身就是视频，无需生成。",
            En: "Video wallpapers are already video; nothing to generate.",
            Legacy: "This is a video wallpaper (project.json says type={0}{1}). WPE Baker bakes Scene wallpapers only, and a video wallpaper has no scene to pre-render; it is already a video, so play the original file."),

        ["source.web_wallpaper"] = new(
            Zh: "不支持网页壁纸。",
            En: "Web wallpapers aren't supported.",
            Legacy: "This is a web wallpaper (project.json says type={0}{1}). WPE Baker bakes Scene wallpapers only, and a web wallpaper has no scene to pre-render."),

        ["source.content_clause"] = new(Zh: "，内容是 {0}", En: ", content is {0}"),

        ["source.path_missing"] = new(
            Zh: "路径不存在：{0}",
            En: "Path not found: {0}",
            Legacy: "This path does not exist: {0}. Check the path, and that the drive it is on is still connected."),

        ["source.folder_not_wallpaper"] = new(
            Zh: "此文件夹中没有壁纸：{0}（缺少 project.json、scene.pkg 或 scene.json）",
            En: "No wallpaper in this folder: {0} (no project.json, scene.pkg or scene.json)",
            Legacy: "This folder holds no wallpaper: {0} contains no project.json, scene.pkg or scene.json. Choose the folder of a subscribed wallpaper instead (right-click the wallpaper in Wallpaper Engine and open its folder)."),

        ["source.file_not_wallpaper"] = new(
            Zh: "此文件不是壁纸：{0}",
            En: "Not a wallpaper file: {0}",
            Legacy: "This file is not a wallpaper source: {0}. Drop a Scene wallpaper folder, or the project.json, scene.json or scene.pkg inside one."),

        ["source.scene_without_objects"] = new(
            Zh: "壁纸文件不完整，可能已损坏或未下载完，可在 Wallpaper Engine 中重新订阅。",
            En: "The wallpaper files are incomplete or damaged; re-subscribing in Wallpaper Engine may fix it.",
            Legacy: "This Scene's scene file opens but has no objects array, so it is not a complete scene; it may be damaged or incompletely downloaded. Re-subscribe to it in Wallpaper Engine."),

        ["source.unreadable"] = new(
            Zh: "壁纸读取失败：{0}。文件可能已损坏或未下载完，可在 Wallpaper Engine 中重新订阅。",
            En: "The wallpaper couldn't be read: {0}. It may be damaged or incompletely downloaded; re-subscribing in Wallpaper Engine may fix it.",
            Legacy: "This wallpaper could not be read: {0}. It may be damaged or incompletely downloaded; re-subscribe to it in Wallpaper Engine."),

        // ---- 自检：便携包自身缺件 ----
        ["setup.tools_config_missing"] = new(
            Zh: "安装包未完整解压（缺少 tools.json），需完整解压后运行。",
            En: "The package isn't fully extracted (tools.json missing); extract the whole archive and run it again.",
            Legacy: "tools.json is missing next to this program, which means the package was not fully extracted. Extract the whole downloaded archive into one folder and run it from there - do not pull out only the .exe, and do not run it from inside the archive viewer."),

        // {0}=缺的是哪一个（渲染器/视频编码器…） {1}=它应该在的完整路径
        ["setup.tool_file_missing"] = new(
            Zh: "缺少随包的{0}：{1}。文件可能未解压或被杀毒软件删除，需重新完整解压；若仍缺失，从杀毒软件隔离区恢复并将该文件夹加入白名单。",
            En: "A bundled tool is missing - {0}: {1}. It may not have been extracted or was removed by antivirus software; extract the whole archive again, and if it is still missing, restore it from antivirus quarantine and allow this folder.",
            Legacy: "A bundled tool is missing - {0}: {1}. It was either not extracted or removed by antivirus software. Extract the whole archive again; if it is still missing, restore it from your antivirus quarantine and allow this folder."),

        ["setup.runtime_directory_missing"] = new(
            Zh: "缺少随包的运行库目录：{0}。安装包未完整解压，需完整解压后运行。",
            En: "A bundled runtime folder is missing: {0}. The package isn't fully extracted; extract the whole archive and run it again.",
            Legacy: "A bundled runtime folder is missing: {0}. The archive was not fully extracted; extract all of it again and run from there."),

        ["setup.tool_renderer"] = new(Zh: "渲染器 wpe-render.exe", En: "the renderer wpe-render.exe"),
        ["setup.tool_ffmpeg"] = new(Zh: "视频编码器 ffmpeg.exe", En: "the video encoder ffmpeg.exe"),
        ["setup.tool_ffprobe"] = new(Zh: "视频检查器 ffprobe.exe", En: "the media inspector ffprobe.exe"),

        // {0}=plan 里的 schema_version
        ["plan.legacy_version"] = new(
            Zh: "此结果由旧版本生成，需重新分析。",
            En: "This result is from an older version; analyze again."),
        ["setup.assets_missing"] = new(
            Zh: "未评估：未找到 Wallpaper Engine 的 assets 目录，分析需要它读取着色器与特效。已安装 Wallpaper Engine 时，手动指向安装目录下的 assets（通常为 …\\steamapps\\common\\wallpaper_engine\\assets）；命令行用 --assets 指定。",
            En: "Not evaluated: the Wallpaper Engine assets folder was not found; analysis requires it to read shaders and effects. If Wallpaper Engine is installed, point at the assets folder inside its install directory (usually ...\\steamapps\\common\\wallpaper_engine\\assets); on the command line pass --assets.",
            Legacy: "The Wallpaper Engine assets folder was not found; analysis needs it to read shaders and effects. If Wallpaper Engine is installed, point at the assets folder inside its install directory (usually ...\\steamapps\\common\\wallpaper_engine\\assets); on the command line pass --assets."),
        ["plan.legacy_unnumbered_blocker"] = new(
            Zh: "此结果由旧版本生成，需重新分析。",
            En: "This result is from an older version; analyze again."),

        // ---- C1.1d：原来直接写英文的 reason（plan / bake / 测量报告）。En 即原文。 ----
        ["reason.demotion_unavailable"] = new(
            Zh: "从末尾整根退回视频根，都剩不下一个能承担场景清屏的不透明视频组。",
            En: "No suffix of whole video roots leaves a single opaque group that carries the scene clear."),
        ["reason.demotion_available"] = new(
            Zh: "对这些根加 --retain-live 重新分析，就只剩一个不透明视频组；analyze 不会自己这么做。",
            En: "Re-running analyze with --retain-live for these roots leaves one opaque video group; analyze does not apply it on its own."),
        ["reason.demotion_not_full_frame"] = new(
            Zh: "尾组退回只适用于整幅布局。",
            En: "Tail-group demotion only applies to a full-frame layout."),
        ["reason.demotion_no_single_opaque_group"] = new(
            Zh: "把不透明底组之后的视频根退回实时后，剩下的不是恰好一个承担场景清屏的不透明组，或者依赖闭包会牵连到更多根。",
            En: "Demoting the video roots after the opaque base group does not leave exactly one opaque group that carries the scene clear, or its dependency closure would reach further roots."),
        ["reason.demotion_fraction_unknown"] = new(
            Zh: "退回的可见图层里有一层画布占比未知，没法和保留的视频组比大小。",
            En: "A demoted visible drawable layer has an unknown canvas fraction, so the comparison against the retained group is not decidable."),
        ["reason.demotion_video_not_dominant"] = new(
            Zh: "退回的内容占比不低于保留组里最大的可见图层，视频就不再是画面主体了，维持原来的拒绝。",
            En: "The demoted content does not stay below the retained group's largest visible drawable canvas fraction, so the video would no longer carry the image; the original rejection stands."),
        ["reason.demotion_order_changes"] = new(
            Zh: "退回后可见的实时根不再按原来的分配顺序绘制；绘制顺序不能变。",
            En: "The demoted composition would not keep visible drawable live roots in their recorded source allocation order; draw order must not change."),
        // {0}=退回实时的视频根个数
        ["reason.demotion_applied"] = new(
            Zh: "不透明底组上面的 {0} 个视频根原地改为实时，绘制顺序和遮挡关系都不变。",
            En: "The {0} video root(s) above the opaque base group stay realtime in place; draw order and occlusion are unchanged."),
        ["reason.foreground_occlusion"] = new(
            Zh: "把这些根放到前景会改变它们和后面源根之间的遮挡；原来的父子层级和变换保持不变。",
            En: "Foreground placement changes occlusion with later source roots. The original parent hierarchy and transforms are retained."),
        ["reason.composition_pass"] = new(
            Zh: "短时成对采样在全局、分块 RGB 和全局透明度三项固定限值之内。",
            En: "The short paired sample stayed within the fixed global, tile RGB, and global alpha limits."),
        ["reason.composition_rejected"] = new(
            Zh: "短时成对采样不完整，或至少超出了一项固定的合成限值。",
            En: "The short paired sample was incomplete or exceeded at least one fixed composition limit."),
        ["reason.sdr_scene_not_hdr"] = new(
            Zh: "场景没有开启 general.hdr，分组截取没有 HDR 中间缓冲，不存在需要裁掉的超范围亮度。",
            En: "Scene general.hdr is not enabled; the group capture has no HDR intermediate to clip."),
        ["reason.allocation_nothing_baked"] = new(
            Zh: "没有图层分进视频，也没有更小的视频分配可试。",
            En: "No layer is allocated to video, so there is no smaller bake allocation left to try."),
        ["reason.allocation_no_trigger"] = new(
            Zh: "转成视频的图层里没有不满足平稳随机判据的粒子，也没有带未解析循环机制的层，整棵保留作者子树得到的还是同一个分配。",
            En: "No baked layer is a particle system that fails the stationary-random criteria or owns an unresolved loop mechanism, so retaining whole author subtrees would keep the same allocation."),
        ["reason.allocation_nothing_left"] = new(
            Zh: "把所有未解析层和粒子层的作者子树都保留实时后，没有内容可以转成视频，缩小分配没有意义。",
            En: "Retaining the author subtrees of every unresolved or particle layer leaves no bakeable content, so a smaller allocation cannot help."),
        ["hardware_decode.owner_unreadable"] = new(
            Zh: "读不到缓存所属图层的模型、材质或底图纹理。",
            En: "The cache owner's model, material or base texture could not be read."),
        ["bake.loop_unresolved"] = new(
            Zh: "循环解析留下了未解析的时间分量，没有切视频，也没有生成工程。",
            En: "The analytic loop parse left unresolved temporal components. No video was cut or project generated."),
        ["bake.no_loop_candidate"] = new(
            Zh: "没有找到解析循环候选，没有切视频，也没有生成工程。",
            En: "No analytic loop candidate was found. No video was cut or project generated."),
        ["bake.late_script_fault"] = new(
            Zh: "完整截取时，转成视频的内容、保留的祖先或原本没保留实时的图层里出现了源脚本报错。请重新分析分配，让官方的报错隔离和原有属性值留在实时部分；这一组没有写替换图层。",
            En: "The complete capture observed a source script fault in baked content, a retained ancestor or a layer not already retained live. Re-analyze the allocation so official fault isolation and prior property values remain live; no replacement layer was written for this group."),
        ["bake.late_external_write"] = new(
            Zh: "完整截取时发现一次跨越分配边界的非初始化写入：要么写进了转成视频的内容或它保留的祖先，要么由转成视频的控制器写到已知的外部源对象。去掉写入方会丢实时更新，保留它又会让截下来的运动叠加两次。请重新分析分配；这一组没有写替换图层。",
            En: "The complete capture observed a non-initialization write crossing the allocation boundary, either into baked content/its retained ancestors or from a baked controller to a known external source object. Removing the writer can lose live updates; retaining it can apply captured motion twice. Re-analyze the allocation; no replacement layer was written for this group."),
        ["bake.late_external_input"] = new(
            Zh: "完整截取时，转成视频的内容或保留的祖先里出现了分析阶段没有保护到的实时外部输入。请重新分析分配；这一组没有写替换图层。",
            En: "The complete capture observed a live external input in baked content or a retained ancestor that the bounded analysis had not protected. Re-analyze the allocation; no replacement layer was written for this group."),
        ["bake.static_proof_changed"] = new(
            Zh: "一个按静止处理、不计入动态视频预算的组在完整截取中变了，没有导出超大视频布局。",
            En: "A group excluded from the dynamic-video budget changed during the full capture; no oversized video layout was exported."),
        ["bake.probe_no_encoded_group"] = new(
            Zh: "合成探测没有产出可外推的编码视频组。",
            En: "The composition probe produced no encoded video group to extrapolate."),
        ["bake.probe_all_static"] = new(
            Zh: "合成探测没有存下编码视频组（所有组都是静止的）。",
            En: "The composition probe stored no encoded video group (every group was static)."),
        // {0}=读取失败的异常消息（外部错误原文，不翻译）
        ["bake.probe_unreadable"] = new(
            Zh: "读不了合成探测的编码结果：{0}",
            En: "The composition probe encode could not be read: {0}"),
        ["bake.effect_prefix_quality_rejected"] = new(
            Zh: "GPU 编码的效果前缀对比 CPU Lanczos 没达到现有的播放画质门槛。",
            En: "GPU prefix encoding did not meet the existing playback quality threshold against CPU Lanczos."),
        ["bake.effect_prefix_hardware_decode_rejected"] = new(
            Zh: "按源周期编码的效果前缀没通过实际硬件解码检查。",
            En: "The source-period prefix encoding did not pass the actual hardware decode check."),
        ["bake.effect_prefix_opaque_unproven"] = new(
            Zh: "完整的末端截取没能证明每个编码源帧都是不透明像素。",
            En: "The full terminal capture did not prove opaque pixels for every encoded source frame."),
        // {0}=停下时已编出的视频流路数 {1}=不需要实测证明的路数上限
        ["bake.no_benefit_streams"] = new(
            Zh: "不支持：预计功耗高于原壁纸（已编出 {0} 路视频，超过 {1} 路，路数更多的成品通常比原作更费电）。已在此停止，未生成候选项目。调试或测功耗时可用 --no-benefit allow 重新分析并生成。",
            En: "Not supported: estimated power use is higher than the original wallpaper ({0} video streams are already encoded, above {1}; results with more streams usually draw more power than the original). Stopped here, no candidate project generated. For debugging or power measurement, use --no-benefit allow to analyze and generate anyway."),
        ["bake.no_visible_output"] = new(
            Zh: "不可生成：所有视频组在生成区间内都没有可见像素，没有可替换成视频的内容。",
            En: "Cannot generate: every video group was empty over the generated interval, so there is no content to replace with video."),
        ["bake.effect_prefix_composition_failed"] = new(
            Zh: "原始源 48 帧合成比对没通过。",
            En: "The pristine-source 48-frame composition comparison failed."),
        // 残差判定理由整族只出中文（ResidualMasking 用 Get(…, Chinese)），En 供将来带键时用。
        // {0}=各不可掩盖项的理由，用"；"连接
        ["residual.not_maskable_items"] = new(
            Zh: "未解析分量里有不可掩盖的项：{0}",
            En: "Some unresolved components cannot be masked: {0}"),
        ["residual.reason_missing"] = new(Zh: "未给出原因", En: "no reason given"),
        ["residual.layer_prefix"] = new(Zh: "层 {0} 的", En: "Layer {0}: "),
        // {0}=层 id，{1}=解析失败细节
        ["residual.sprite_unrecognized"] = new(
            Zh: "层 {0} 的运行时动画只是没能解析出周期（{1}），这属于识别不了而不是已证明非周期或随机，不允许被掩盖。",
            En: "Layer {0}'s runtime animation merely has no parsed period ({1}); that is unrecognized, not proven non-periodic or random, so it cannot be masked."),
        // {0}=层前缀（residual.layer_prefix 或空），{1}=机制种类，{2}=循环上限秒数，{3}=细节
        ["residual.proven_nonperiodic_unbounded"] = new(
            Zh: "{0}未解析分量 {1} 已由方程证明在 {2} 秒的循环上限内没有周期，而这套机制的位移没有幅度上界，接缝交叉淡化盖不住它（{3}）。",
            En: "{0}unresolved component {1} is proven by its equations to have no period within the {2}-second loop ceiling, and its displacement has no amplitude bound, so a seam crossfade cannot hide it ({3})."),
        // {0}=层前缀，{1}=机制种类，{2}=细节
        ["residual.unrecognized_unbounded"] = new(
            Zh: "{0}未解析分量 {1} 没有可用的非周期或随机证明，也没有幅度上界（{2}）。",
            En: "{0}unresolved component {1} has no usable non-periodic or random proof and no amplitude bound ({2})."),

        // ---- 应用到桌面 ----
        ["apply.wallpaper_engine_not_running"] = new(
            Zh: "Wallpaper Engine 未运行，无法应用。",
            En: "Wallpaper Engine isn't running, so the wallpaper can't be applied.",
            Legacy: "Wallpaper Engine is not running, so the wallpaper cannot be switched. Start Wallpaper Engine first (it should appear in the tray), then apply again."),

        ["apply.location_missing"] = new(
            Zh: "此屏幕已不在 Wallpaper Engine 中（{0}），可点“刷新”后重新选择。",
            En: "Wallpaper Engine no longer lists this screen ({0}); refresh and choose again.",
            Legacy: "Wallpaper Engine no longer lists this screen ({0}); the display may have been disconnected or rearranged. Refresh the screen list and choose again."),

        // fix/mdl-lenient-strings：把 analyze 写出的工具局限结论当成 plan 交给 bake 时的拒绝理由。
        ["cli.bake_tool_limitation_report"] = new(
            Zh: "该文件是 analyze 记录的工具局限结论，不是生成方案：渲染器无法读取作品中的某个素材文件，未生成 plan。原因见文件中的 summary 与 tool_limitation。",
            En: "This file records a tool limitation from analyze, not a generation plan: the renderer could not read an asset file, so no plan exists. See summary and tool_limitation in the file.",
            Legacy: "This file records a tool limitation from analyze, not a bake plan: the renderer could not read an asset file, so no plan exists. See summary and tool_limitation in the file."),

        // bake --help 里 --encoder 的说明：用法骨架与 analyze --help 一样是英文，只有这段说明按 --lang 出中文或英文。
        ["cli.bake_encoder_help"] = new(
            Zh: "默认 auto：支持的显卡上走 vulkan，直接生成成品，减少中间文件和重复编码。不可用的路径使用\n软件，并在 bake.json 中记录原因；画质不达标时先降 QP 重渲这一组，仍不达标改用软件。",
            En: "Default auto: Vulkan generates playback video directly on supported GPUs, reducing intermediate files\nand repeated encoding. Unavailable paths use software and record why in bake.json.\nA group below the quality gate is re-rendered once at a lower QP, then with software."),
        ["bake.gpu_quality_rejected"] = new(
            Zh: "GPU 编码画质未达标；可在高级里把视频编码器改为“软件”后重新生成。",
            En: "GPU encoding quality too low; set the video encoder to Software and regenerate."),

        ["cli.bake_parallel_help"] = new(
            Zh: "--encode-slots N 限制本机同时做成品编码的 wpe-baker 进程数（0 = 默认，不限）。渲染不受限制，\n只卡成品编码：多案并行时吃满 CPU 的就是这一路 ffmpeg。等槽位的时间单独记在\nstage_timing.stages.encode_slot_wait，不混进 encode_playback。\n--group-parallel N 让一个壁纸最多同时渲染 N 个视频组（默认：GPU 路线 3，其余 1 = 逐组渲染）。\n组的判定、编码与写入报告的顺序始终按组序串行，成品与逐组渲染逐字节相同；峰值磁盘与内存按 N 倍算。",
            En: "--encode-slots N limits how many wpe-baker processes on this machine encode the playback video at\nthe same time (0, the default, does not limit it). Rendering is never limited; only the playback\nencode is, because that ffmpeg is what saturates the CPU when several bakes run at once. Time spent\nwaiting for a slot is reported separately as stage_timing.stages.encode_slot_wait.\n--group-parallel N renders up to N video groups of one wallpaper at the same time (default: 3 on the\nGPU path, otherwise 1, one group at a time). Group judgment, encoding and report order stay strictly\nsequential, so the product is byte-identical to one group at a time; peak disk and memory scale with N."),

        ["cli.bake_keep_intermediates_help"] = new(
            Zh: "--keep-intermediates true 保留中间产物：捕获副本 capture-source、各组的无损 master、合成探针与参照。\n默认 false——生成结束（含拒绝与失败）后删除，只保留成品工程、bake.json、接缝预览与日志。\n中间产物是磁盘峰值的主要来源（单案实测 47 GB），仅在排查渲染或编码问题时保留。",
            En: "--keep-intermediates true keeps the intermediate files: the capture-source copy, each group's lossless\nmaster, and the composition probe and reference. The default, false, deletes them when generation ends\n(including rejections and failures), leaving the generated project, bake.json, the seam preview and the\nlogs. Those intermediates dominate the disk peak (47 GB on one measured wallpaper); keep them only\nwhile investigating a rendering or encoding problem.",
            Legacy: "--keep-intermediates true keeps the intermediate files: the capture-source copy, each group's lossless\nmaster, and the composition probe and reference. The default, false, deletes them when the bake ends\n(including rejections and failures), leaving the generated project, bake.json, the seam preview and the\nlogs. Those intermediates are the bulk of the disk peak (47 GB on one measured wallpaper); keep them\nonly while investigating a rendering or encoding problem."),

        // 单帧结果描述产物类型，不单独证明有无收益。
        ["summary.static_only_route"] = new(
            Zh: "该方案产出单帧静态图；收益取决于省去的特效计算、绘制和纹理开销，需另行确认。",
            En: "This route yields a still image; benefit depends on removed effects, drawing and texture costs and needs separate verification.",
            Legacy: "This way leaves only a still image and saves no power; making the picture in separate layers is what can capture the moving part."),

        // ---- 一行结论 ----
        ["summary.bakeable"] = new(
            Zh: "可生成：循环周期 {0} s（{1} 帧 @{2} fps，调速 {3}%）；图层 {4} 保持实时；接缝在生成阶段验证。",
            En: "Ready: loop period {0} s ({1} frames @{2} fps, {3}% retime); layers {4} stay live; seam verified during generation.",
            Legacy: "Bakeable: {0} s loop found ({1} frames @{2} fps, {3}% retime); layers {4} stay live; the seam is verified during baking."),

        ["summary.bakeable_no_live"] = new(
            Zh: "可生成：循环周期 {0} s（{1} 帧 @{2} fps，调速 {3}%）；无保持实时的图层；接缝在生成阶段验证。",
            En: "Ready: loop period {0} s ({1} frames @{2} fps, {3}% retime); no layer stays live; seam verified during generation.",
            Legacy: "Bakeable: {0} s loop found ({1} frames @{2} fps, {3}% retime); no layer needs to stay live; the seam is verified during baking."),

        ["summary.bakeable_static"] = new(
            Zh: "可生成：可录制部分为静态画面，仅需 1 帧；图层 {0} 保持实时；接缝在生成阶段验证。",
            En: "Ready: the recordable content is static and needs 1 frame; layers {0} stay live; seam verified during generation.",
            Legacy: "Bakeable: the recordable part is a still image and needs a single frame; layers {0} stay live; the seam is verified during baking."),

        ["summary.bakeable_static_no_live"] = new(
            Zh: "可生成：可录制部分为静态画面，仅需 1 帧；无保持实时的图层。",
            En: "Ready: the recordable content is static and needs 1 frame; no layer stays live.",
            Legacy: "Bakeable: the recordable part is a still image and needs a single frame; no layer needs to stay live."),

        // 效果前缀只移除部分工作；具体收益不能从路线名推出。
        ["summary.effect_prefix_limited_saving"] = new(
            Zh: "效果前缀路线保留其他实时计算；收益取决于被缓存效果的开销，需原作与生成结果对照。",
            En: "Effect-prefix caching retains other live work; benefit depends on the cached effects' cost and requires comparing the source and generated result.",
            Legacy: "This route (effect prefix) saves little power unless the part baked away is the bulk of the work."),

        // feat/sway-retime：摆动改频成立时补在结论行后面。feat/retime-budget：观感按相位差排序（百分比只是求解参数），
        // 所以先报一个循环内最坏偏多少圈相位、最慢可见项走几圈，再报改动百分比与预算。
        // {0}=一个循环内最坏相位差（圈） {1}=最慢可见项走的圈数 {2}=可见摆动项（周期 < 60 s）最大改动百分比
        // {3}=预算说明（"预算 3%" 或 "改动最小"） {4}=慢项（周期 ≥ 60 s）最大峰值速度偏差（像素/秒） {5}=冻结项个数 {6}=循环秒数
        ["summary.sway_retime"] = new(
            Zh: "摆动改频：单个循环内相位最大偏差 {0} 圈，最慢可见摆动项运行 {1} 圈（可见项周期 < 60 s，改频 {2}%，{3}）；慢项（周期 ≥ 60 s）峰值速度偏差最大 {4} px/s，其中冻结 {5} 项；摆动图层在 {6} s 循环内逐项精确闭合。",
            En: "Sway retime: maximum phase drift {0} cycle per loop; slowest visible sway term runs {1} cycles (visible terms period < 60 s, retimed {2}%, {3}); slow terms (period ≥ 60 s) peak speed deviation at most {4} px/s, {5} frozen; sway layers close exactly over the {6} s loop.",
            Legacy: "Sway retime: phase drifts by at most {0} cycle within one loop and the slowest visible sway term runs {1} cycles (visible terms have periods < 60 s, retimed by {2}%, {3}); slow terms (period ≥ 60 s) deviate by at most {4} px/s in peak speed, {5} of them frozen; sway layers close exactly over the {6} s loop."),

        // {0}=预算百分比。档位给的观感改动预算，写在结论行括号里。
        ["summary.sway_budget"] = new(Zh: "预算 {0}%", En: "budget {0}%"),
        ["summary.sway_budget_minimized"] = new(Zh: "按最小改动求解", En: "minimum-change solution",
            Legacy: "solved for the smallest change"),

        // {0}=--loop-max-seconds 秒数 {1}=慢项峰值速度偏差上限（像素/秒）
        ["sway_retime.no_multiple_meets_speed_limit"] = new(
            Zh: "摆动改频已启用，但 {0} s 循环长度上限内无合规 L = kP：存在周期 < 60 s 的可见摆动项走不满整圈（可见项不可冻结），或慢项冻结、改频后峰值速度偏差超过 {1} px/s；摆动分量按未解析项处理。",
            En: "Sway retime enabled, but no L = kP within the {0} s loop-length maximum qualifies: a visible sway term (period < 60 s) cannot complete a whole cycle (visible terms cannot be frozen), or a slow term's peak speed deviation after freezing or retiming exceeds {1} px/s; sway components remain unresolved.",
            Legacy: "Sway retime is on, but no L = kP within the {0} s loop-length maximum qualifies: either a visible sway term (period < 60 s) cannot complete a whole cycle and visible terms may not be frozen, or a slow term's peak speed deviation after freezing or retiming exceeds {1} px/s; the sway components stay unresolved."),

        // {0}=振幅换算不到输出像素的图层 id 列表
        ["sway_retime.amplitude_unknown"] = new(
            Zh: "摆动改频已启用，但图层 {0} 的摆动振幅无法换算为输出像素（图层尺寸不可读），慢项冻结或改频后的速度偏差无法判定；摆动分量按未解析项处理。",
            En: "Sway retime enabled, but the sway amplitude of layer(s) {0} cannot be converted to output pixels (layer size unreadable), so the speed deviation of slow terms cannot be evaluated; sway components remain unresolved.",
            Legacy: "Sway retime is on, but the sway amplitude of layer(s) {0} cannot be converted to output pixels (the layer size is unreadable), so the speed deviation of slow terms cannot be judged; the sway components stay unresolved."),

        // {0}=循环秒数。未解析项全是平稳随机粒子、且没有任何周期分量时，循环长度取默认值（见 HybridLoopService）。
        ["summary.particle_default_loop"] = new(
            Zh: "粒子系统无周期：循环长度取默认值 {0} s，接缝处交叉淡化。",
            En: "Particle systems have no period: loop length defaults to {0} s, seam crossfaded.",
            Legacy: "The particles have no period, so the loop is {0} s long and its seam is crossfaded."),

        // {0}=默认循环秒数 {1}=粒子最长寿命秒数
        ["loop_length_default.particle_lifetime_too_long"] = new(
            Zh: "未解析项均为平稳随机粒子系统，但粒子最长寿命 {1} s 不短于默认循环长度 {0} s：接缝两侧共享同一批粒子，交叉淡化替换前提不成立，不采用默认循环长度。",
            En: "All unresolved mechanisms are stationary-random particle systems, but the longest particle lifetime ({1} s) is not shorter than the default loop length ({0} s): both sides of the seam would share particles, so the crossfade replacement premise fails and the default loop length is not applied.",
            Legacy: "Every unresolved mechanism is a stationary-random particle system, but the longest particle lifetime ({1} s) is not shorter than the default loop length ({0} s): both sides of the seam would share particles, so the crossfade replacement premise fails and no default loop length is used."),

        // {0}=--loop-max-seconds 秒数
        ["sway_retime.no_multiple_within_maximum"] = new(
            Zh: "摆动改频已启用，但其余分量解出的循环周期均超过 {0} s 循环长度上限，无法取得 L = kP；摆动分量按未解析项处理。",
            En: "Sway retime enabled, but every loop period solved from the other components exceeds the {0} s loop-length maximum, so no L = kP exists; sway components remain unresolved.",
            Legacy: "Sway retime is on, but every loop period solved from the other components exceeds the {0} s loop-length maximum, so no L = kP exists; the sway components stay unresolved."),

        // {0}=循环长度上限秒数 {1}=观感改动预算百分比。预算太紧：不设预算时有解，说明卡住的是档位预算，不是速度闸。
        ["sway_retime.no_multiple_within_budget"] = new(
            Zh: "摆动改频已启用，但 {0} s 循环长度上限内无 L = kP 能把可见摆动项的改动压入 {1}% 观感预算；摆动分量按未解析项处理。放宽预算需改用更宽的档位或指定 --retime-budget。",
            En: "Sway retime enabled, but no L = kP within the {0} s loop-length maximum keeps the visible sway change inside the {1}% look budget; sway components remain unresolved. A wider preset or --retime-budget raises the budget.",
            Legacy: "Sway retime is on, but no L = kP within the {0} s loop-length maximum keeps the visible sway change inside the {1}% look budget; the sway components stay unresolved. A looser preset or --retime-budget raises the budget."),

        ["sway_retime.no_base_candidate"] = new(
            Zh: "摆动改频已启用，但其余时间分量未解出循环候选（上限 {0} s），无法取得 L = kP；摆动分量按未解析项处理。",
            En: "Sway retime enabled, but the other temporal components produced no loop candidate (maximum {0} s), so no L = kP exists; sway components remain unresolved.",
            Legacy: "Sway retime is on, but the other temporal components produced no loop candidate (maximum {0} s), so no L = kP exists; the sway components stay unresolved."),

        // {0}=视频组 {1}=帧数 {2}=秒数 {3}=预估大小（GiB） {4}=按试编码码率最长秒数
        ["bake.embedded_video_size_predicted"] = new(
            Zh: "视频超过 Wallpaper Engine 的 2 GiB 上限（约 {3} GiB），按当前码率最长约 {4} 秒；可降低分辨率或帧率后重试。",
            En: "The video exceeds Wallpaper Engine's 2 GiB limit (about {3} GiB); at this bitrate the loop can be about {4} s at most. Lower the resolution or frame rate and retry.",
            Legacy: "Extrapolated from the short trial encode, video group {0} ({1} frames, {2} s) would be about {3} GiB, above the 2 GiB embedded-video limit Wallpaper Engine can play (a larger video shows only the clear color). At this bitrate it fits about {4} s. Stopped before rendering; no candidate project was generated. Analyze again with a smaller --loop-max-seconds, a lower resolution, or a lower frame rate."),

        // {0}=视频组 {1}=实际大小（GiB） {2}=帧数 {3}=秒数 {4}=按实际码率最长秒数
        ["bake.embedded_video_size_rejected"] = new(
            Zh: "视频超过 Wallpaper Engine 的 2 GiB 上限（{1} GiB），按当前码率最长约 {4} 秒；可降低分辨率或帧率后重试。",
            En: "The video exceeds Wallpaper Engine's 2 GiB limit ({1} GiB); at this bitrate the loop can be about {4} s at most. Lower the resolution or frame rate and retry.",
            Legacy: "Video group {0} encoded to {1} GiB ({2} frames, {3} s), above the 2 GiB embedded-video limit Wallpaper Engine can play (a larger video shows only the clear color). At the actual bitrate it fits about {4} s. Stopped before the seam checks; no candidate project was generated. Analyze again with a smaller --loop-max-seconds, a lower resolution, or a lower frame rate."),

        // ---- 开烘前的磁盘闸门（BakeDiskBudget）----
        // {0}=预估峰值（GiB） {1}=固定余量（GiB） {2}=合计需要（GiB） {3}=输出所在盘 {4}=现有空闲（GiB）
        ["bake.insufficient_disk_space"] = new(
            Zh: "磁盘空间不足：需要约 {2} GiB，{3} 仅剩 {4} GiB；可清理空间或把输出目录改到其他磁盘。",
            En: "Not enough disk space: about {2} GiB needed, {4} GiB free on {3}; free up space or choose an output folder on another drive.",
            Legacy: "Not enough disk space, so this bake did not start: intermediate files are estimated to peak at about {0} GiB, plus a {1} GiB reserve, which needs {2} GiB in total; only {4} GiB is free on {3}. Free up space, or choose an output folder on another drive (the working directory follows the output folder) and generate again."),

        ["summary.not_suitable_current"] = new(
            Zh: "当前不适合生成：{0}",
            En: "Currently unsuitable for baking: {0}"),

        ["summary.blocked"] = new(
            Zh: "不可生成：{0}（阻断原因共 {1} 条，详见 plan.json 的 blockers_localized）",
            En: "Cannot generate: {0} ({1} blocker(s) in total; see blockers_localized in plan.json.)",
            Legacy: "Not bakeable: {0} ({1} blocker(s) in total; see blockers_localized in plan.json.)"),

        ["summary.unknown_with_reason"] = new(
            Zh: "未评估：无可生成候选，也无阻断原因；分析仅给出以下记录：{0}",
            En: "Not evaluated: neither a candidate nor a blocker was produced; the only record from the analysis: {0}",
            Legacy: "Unknown: neither a bakeable candidate nor a blocker was produced. The only clue from the analysis is: {0}"),

        // 没有候选、没有阻断、但 loop.unresolved 里有具体机制时的细分（只读 plan 已有字段，不改判定）。
        // {0}=证明不了周期的机制条数（不含 loop_allocation_fallback 这条补充分析记录，也不含满足平稳随机判据的粒子项）
        // {1}=首条机制的说明（分语言）。
        // 补充分析找到了更小的烘焙范围：{2}=要保持实时的图层名 {3}=--retain-live 的 id 列表
        // {4}=满足平稳随机判据的粒子说明（summary.stationary_particles_note，没有时为空）。
        ["summary.loop_unresolved_retain_live"] = new(
            Zh: "当前分配不可生成：录制内容中 {0} 处时间机制无法证明周期，整段无循环周期（首条：{1}）。{4}将图层 {2}（含子层）保持实时后，其余内容存在循环候选：以 --retain-live {3} 重新分析；接缝仍在生成阶段验证。",
            En: "Cannot generate under this allocation: {0} temporal mechanism(s) in the recorded content have no provable period, so no loop period covers the whole recording (first: {1}). {4}Keeping layers {2} and their children live leaves content with a loop candidate: re-run analyze with --retain-live {3}; the seam is still verified during generation.",
            Legacy: "Not bakeable as planned: {0} temporal mechanism(s) in the recorded content have no provable period, so no loop covers the whole recording (first: {1}). {4}Keeping layers {2} and their children live leaves content with a loop candidate: re-run analyze with --retain-live {3}; the seam is still verified during baking."),

        // 补充分析的重查只被全幅布局挡住，冲突给出了再保留哪些根（loop_allocation_fallback.replanned_retain_live_suggestion）。
        // {0}{1} 同上 {2}=补充分析已保持实时的图层名 {3}=冲突要求再保持实时的图层名 {4}=完整 --retain-live 列表 {5}=粒子说明。
        ["summary.loop_unresolved_retain_live_full_frame"] = new(
            Zh: "当前分配不可生成：录制内容中 {0} 处时间机制无法证明周期，整段无循环周期（首条：{1}）。{5}将图层 {2}（含子层）保持实时后重新分析，其余内容在全屏模式下无法构成单块不透明底；再将图层 {3} 保持实时后仅剩一组不透明视频：以 --retain-live {4} 重新分析。该分配尚未重新分析，是否存在循环周期由该次分析判定。",
            En: "Cannot generate under this allocation: {0} temporal mechanism(s) in the recorded content have no provable period, so no loop period covers the whole recording (first: {1}). {5}A re-analysis keeping layers {2} and their children live found that the remaining content cannot form the single opaque base required for full-frame video; keeping layers {3} live as well leaves one opaque video group: re-run analyze with --retain-live {4}. That allocation has not been re-analyzed; whether a loop period exists is determined by that analysis.",
            Legacy: "Not bakeable as planned: {0} temporal mechanism(s) in the recorded content have no provable period, so no loop covers the whole recording (first: {1}). {5}A re-analysis keeping layers {2} and their children live found that the remaining content cannot form the single opaque base full-frame video needs; keeping layers {3} live as well leaves one opaque video group: re-run analyze with --retain-live {4}. That allocation has not been re-analyzed yet, so whether it finds a loop depends on that analysis."),

        // {2}=下一步说明（分语言，按 loop_allocation_fallback.status 选择，前面可带 summary.stationary_particles_note）。
        ["summary.loop_unresolved"] = new(
            Zh: "不可生成：录制内容中 {0} 处时间机制无法证明周期，分析未确立循环周期（首条：{1}）。{2}",
            En: "Cannot generate: {0} temporal mechanism(s) in the recorded content have no provable period, so the analysis established no loop period (first: {1}). {2}",
            Legacy: "Not bakeable yet: {0} temporal mechanism(s) in the recorded content have no provable period, so the analysis established no loop (first: {1}). {2}"),

        // 未解析项里只剩满足平稳随机判据的粒子：它们本身不挡淡化，但定不出循环长度。
        // {0}=粒子层个数 {1}=粒子层名 {2}=定不出循环长度的原因（summary.stationary_only_*） {3}=下一步说明。
        ["summary.loop_unresolved_stationary_only"] = new(
            Zh: "不可生成：录制内容中无周期的部分仅为 {0} 个满足平稳随机判据的粒子系统（{1}），其接缝可交叉淡化替换；但{2}，分析未确立循环周期。{3}",
            En: "Cannot generate: the only content without a period is {0} particle system(s) meeting the stationary-random criteria ({1}), whose seam can be crossfaded; but {2}, so the analysis established no loop period. {3}",
            Legacy: "Not bakeable yet: the only content without a period is {0} particle system(s) that meet the stationary-random criteria ({1}), and their seam can be crossfaded; but {2}, so the analysis established no loop. {3}"),

        ["summary.stationary_only_no_period_source"] = new(
            Zh: "其余时间分量无已证明周期，无法确定循环长度",
            En: "no other temporal component has a proven period to set the loop length",
            Legacy: "nothing else in the recorded content has a proven period to set the loop length"),

        ["summary.stationary_only_no_candidate"] = new(
            Zh: "其余已证明周期的分量未构成循环候选",
            En: "the components with a proven period produced no loop candidate"),

        // 有证明不了周期的机制时，顺带说清另外那些满足平稳随机判据的粒子不算在里面。{0}=粒子层个数 {1}=粒子层名。
        ["summary.stationary_particles_note"] = new(
            Zh: "另有 {0} 个粒子系统（{1}）满足平稳随机判据，接缝可交叉淡化替换，不计入上述数量。",
            En: "{0} further particle system(s) ({1}) meet the stationary-random criteria; their seam can be crossfaded and they are not counted.",
            Legacy: "{0} more particle system(s) ({1}) meet the stationary-random criteria; their seam can be crossfaded, so they are not counted."),

        // ---- 结论行的下一步说明（按 loop_allocation_fallback.status 选择） ----
        ["summary.next_still_unavailable"] = new(
            Zh: "将上述机制所在图层保持实时后，其余部分仍无循环候选；当前设置下不可生成。",
            En: "Keeping the layers that own these mechanisms live still yields no loop candidate; no loop period exists under the current settings.",
            Legacy: "Keeping the layers that own these mechanisms live still leaves no loop, so this wallpaper has no loop under the current settings."),

        // 重查被阻断挡住（不是单纯找不到循环）。{0}=重查的阻断条数 {1}=阻断原文所在的字段路径。
        ["summary.next_still_unavailable_blocked"] = new(
            Zh: "将上述机制所在图层保持实时后重新分析，其余部分仍有 {0} 条阻断（原文见 plan.json 的 {1}），未得出可生成的分配。",
            En: "A re-analysis keeping the layers that own these mechanisms live still reports {0} blocker(s) (see {1} in plan.json) and produced no generable allocation.",
            Legacy: "A re-analysis keeping the layers that own these mechanisms live still has {0} blocker(s) (see {1} in plan.json), so it produced no bakeable allocation."),

        ["summary.next_not_applicable"] = new(
            Zh: "缩小生成范围后仍无循环周期；当前设置下不可生成。",
            En: "A smaller generation scope still yields no loop period; not generable under the current settings.",
            Legacy: "A smaller bake allocation cannot help, so this wallpaper has no loop under the current settings."),

        // {0}=补充分析记录的字段路径。
        ["summary.next_failed"] = new(
            Zh: "缩小生成范围的补充分析未完成，原因见 plan.json 的 {0}。",
            En: "The follow-up analysis of a smaller generation scope did not complete; see {0} in plan.json.",
            Legacy: "The follow-up analysis of a smaller bake allocation did not complete; see {0} in plan.json."),

        // {0}=逐条原因所在的字段路径。
        ["summary.next_see_unresolved"] = new(
            Zh: "逐条原因见 plan.json 的 {0}。",
            En: "Per-item reasons: see {0} in plan.json.",
            Legacy: "See {0} in plan.json for every reason."),

        ["summary.unknown_no_reason"] = new(
            Zh: "未评估：无可生成候选，无阻断原因，无未解析项；分析未给出内部原因。",
            En: "Not evaluated: no candidate, no blocker and no unresolved item were produced; the analysis gave no internal reason.",
            Legacy: "Unknown: no bakeable candidate, no blocker and no unresolved item were produced - the analysis gave no internal reason."),

        ["summary.failed"] = new(
            Zh: "分析失败：{0}",
            En: "Analysis failed: {0}"),

        // {0}=第一条读不了的素材（已本地化） {1}=读不了的素材文件数。
        ["summary.tool_limitation"] = new(
            Zh: "工具局限，未分析：{0}（共 {1} 个素材文件不可读，详见 plan.json 的 tool_limitation）。该结果为解析能力缺口，非对壁纸可生成性的判定。",
            En: "Tool limitation, not analyzed: {0} ({1} asset file(s) unreadable; see tool_limitation in plan.json). This is a parser gap, not a verdict on generability.",
            Legacy: "Tool limitation, not analyzed: {0} ({1} asset file(s) could not be read; see tool_limitation in plan.json). This is a gap in the tool, not a verdict on whether the wallpaper can be baked."),

        // ---- 输出分辨率（接在一行结论后面） ----
        // {0}=输出宽×高 {1}=场景画布宽×高 {2}=本机主显示器物理分辨率。
        ["summary.resolution_canvas_fit_display"] = new(
            Zh: "输出分辨率 {0}：场景画布缩放至铺满本机屏幕（画布 {1}，屏幕 {2}）。",
            En: "Output resolution {0}: the scene canvas scaled to cover this machine's screen (canvas {1}, screen {2}).",
            Legacy: "Baked at {0}, the scene canvas scaled to cover this machine's screen (canvas {1}, screen {2})."),

        ["summary.resolution_canvas_fit_display_capped"] = new(
            Zh: "输出分辨率 {0}，即场景画布原尺寸：画布不足以覆盖本机屏幕 {2}，不放大。",
            En: "Output resolution {0}, the scene canvas size: the canvas does not cover this machine's screen {2} and is not upscaled.",
            Legacy: "Baked at the scene canvas size {0}: the canvas does not cover this machine's screen {2}, and it is not upscaled."),

        ["summary.resolution_scene_canvas"] = new(
            Zh: "本机屏幕分辨率不可读，输出分辨率取场景画布原尺寸 {0}。",
            En: "Screen resolution unavailable; output resolution is the scene canvas size {0}.",
            Legacy: "The screen resolution is unavailable, so it is baked at the scene canvas size {0}."),

        ["summary.resolution_display"] = new(
            Zh: "输出分辨率取本机主显示器分辨率 {0}，与 WPE 按屏幕分辨率实时渲染一致。",
            En: "Output resolution is the primary display resolution {0}, as Wallpaper Engine renders at screen resolution."),

        ["summary.resolution_fallback"] = new(
            Zh: "场景无可用正交画布，主显示器分辨率不可读，输出分辨率取默认值 {0}。",
            En: "The scene has no usable orthographic canvas and the primary display resolution is unavailable; output resolution is the default {0}.",
            Legacy: "The scene has no usable orthographic canvas and the primary display resolution is unavailable, so it is baked at the default {0}."),

        ["summary.resolution_explicit"] = new(
            Zh: "输出分辨率取指定值 {0}。",
            En: "Output resolution set to the requested {0}.",
            Legacy: "Baked at the requested size {0}."),

        // ---- 属性来源（接在结论最后）----
        ["summary.properties_from_wpe"] = new(
            Zh: "属性来源：Wallpaper Engine 当前属性设置（{0} 项与默认值不同）。",
            En: "Properties: the current Wallpaper Engine settings for this wallpaper ({0} differ from the defaults).",
            Legacy: "Uses your Wallpaper Engine property settings for this wallpaper ({0} differ from the defaults)."),

        ["summary.properties_wpe_unavailable"] = new(
            Zh: "属性来源：壁纸自带默认属性；Wallpaper Engine 的属性设置不可读（{0}）。",
            En: "Properties: the wallpaper's own defaults; the Wallpaper Engine settings are unreadable ({0}).",
            Legacy: "Could not read your Wallpaper Engine property settings ({0}), so the wallpaper's own defaults are used."),

        ["properties.reason.config_not_found"] = new(
            Zh: "未找到 Wallpaper Engine 的 config.json",
            En: "Wallpaper Engine's config.json was not found"),

        ["properties.reason.config_unreadable"] = new(
            Zh: "config.json 不可读",
            En: "config.json could not be read"),

        ["properties.reason.no_profile"] = new(
            Zh: "config.json 中无用户记录",
            En: "config.json has no user entry"),

        ["properties.reason.profile_ambiguous"] = new(
            Zh: "config.json 中有多个用户，无法确定当前用户",
            En: "config.json has several user entries and the current one cannot be determined",
            Legacy: "config.json has several users and none is clearly the current one"),

        ["properties.reason.entry_ambiguous"] = new(
            Zh: "该壁纸在 config.json 中有多条互相矛盾的记录",
            En: "config.json has conflicting entries for this wallpaper"),

        ["properties.reason.location_ambiguous"] = new(
            Zh: "该壁纸在不同屏幕上的设置不一致，无法确定采用哪一套",
            En: "this wallpaper has different settings on different screens and the applicable set cannot be determined",
            Legacy: "this wallpaper has different settings on different screens"),

        ["properties.reason.unknown"] = new(
            Zh: "原因不明",
            En: "reason unknown"),

        ["summary.resolution_explicit_canvas"] = new(
            Zh: "输出分辨率取指定值 {0}（场景画布 {1}）。",
            En: "Output resolution set to the requested {0} (scene canvas {1}).",
            Legacy: "Baked at the requested size {0} (scene canvas {1})."),

        // ---- 接缝预览 ----
        ["summary.seam_preview"] = new(
            Zh: "接缝预览：{0}",
            En: "Seam preview: {0}"),

        // {0}=被拒的起点帧 {1}=下一个起点在排序里的序号（从 1 数） {2}=下一个起点帧
        ["progress.retrying_next_loop_start"] = new(
            Zh: "起点帧 {0} 在全分辨率下首层超限；改用排序中第 {1} 个候选起点（帧 {2}），重新渲染并复核。",
            En: "Start frame {0} exceeded the first layer at full resolution; re-rendering and re-checking with candidate start #{1} in sort order, frame {2}."),

        // {0}=解析周期帧数 P {1}=新的起点帧 S
        ["progress.retrying_source_start_offset"] = new(
            Zh: "源帧 0 为孤立的起点异常帧（闭合检验仅因该帧未通过）；周期 {0} 帧不变，起点顺延至源帧 {1}，重新渲染并复核。",
            En: "Source frame 0 is an isolated start anomaly (the closure check failed only because of it); keeping the {0}-frame period and re-rendering from source frame {1} for re-checking."),

        ["progress.exporting_seam_preview"] = new(
            Zh: "正在导出接缝预览…",
            En: "Exporting seam preview…"),

        // 烘焙中的进度句（界面任务行与状态栏按界面语言显示）。原先只写了一种语言的，另一种按原句直译补上，用词待定稿。
        ["progress.observing_without_audio_effects"] = new(
            Zh: "正在分析壁纸…",
            En: "Analyzing the wallpaper…"),
        ["progress.observing_scene"] = new(
            Zh: "正在分析壁纸…",
            En: "Analyzing the wallpaper…"),
        ["progress.refreshing_script_fault_evidence"] = new(
            Zh: "正在更新旧版分析结果…",
            En: "Updating an older analysis…"),
        // {0}=组 {1}=预热帧数 {2}=步长帧数 {3}=搜索窗帧数
        ["progress.searching_loop_start"] = new(
            Zh: "正在选择循环起点…",
            En: "Choosing loop start…"),
        ["progress.checking_composition"] = new(
            Zh: "正在生成短片段比对画面…",
            En: "Generating a short clip to compare…"),
        ["progress.saving_project"] = new(
            Zh: "正在保存到输出目录…",
            En: "Saving to the output folder…"),
        ["progress.rendering_pair"] = new(
            Zh: "正在比对画面…",
            En: "Comparing images…"),
        // {0}=已比较帧数 {1}=总帧数
        ["progress.comparing"] = new(Zh: "正在比对画面：{0}/{1} 帧", En: "Comparing images: {0}/{1} frames"),
        // {0}=循环上限秒数
        ["progress.capping_loop_length"] = new(
            Zh: "视频超过 2 GiB，缩短循环后重新生成…",
            En: "Video over 2 GiB; regenerating with a shorter loop…"),
        ["progress.reverting_intro_switch"] = new(
            Zh: "画面比对未通过，保留开场动画后重新生成…",
            En: "Image check failed; regenerating with the intro kept live…"),
        // {0}=视频组 {1}=该组自身的周期帧数
        ["progress.group_full_loop_fallback"] = new(
            Zh: "视频层 {0} 未能闭合，按完整循环重新生成…",
            En: "Video layer {0} didn't loop; regenerating at full loop length…"),
        ["progress.retaining_residual_particles"] = new(
            Zh: "接缝差异过大，保留粒子效果后重新生成…",
            En: "Seam too visible; regenerating with particles kept live…"),
        // {0}=第几组 {1}=组数 {2}=源图层数
        ["progress.rendering_group"] = new(Zh: "正在渲染第 {0}/{1} 个视频层…", En: "Rendering video layer {0} of {1}…"),
        ["progress.checking_seam_residual"] = new(
            Zh: "正在检查接缝…",
            En: "Checking seam…"),
        ["progress.applying_crossfade"] = new(
            Zh: "正在处理接缝淡入淡出…",
            En: "Crossfading the seam…"),
        ["progress.checking_hardware_decode"] = new(
            Zh: "正在检查硬件解码…",
            En: "Checking hardware decode…"),
        ["progress.retaining_nonlooping_layers"] = new(
            Zh: "保留无法循环的特效和粒子后重新检查…",
            En: "Keeping non-looping effects and particles live, then checking again…"),

        ["warning.seam_preview_failed"] = new(
            Zh: "视频组 {0} 的接缝预览未导出（{1}）；生成结论不受影响。",
            En: "The seam preview for video group {0} was not exported ({1}); the generation result is unaffected.",
            Legacy: "The seam preview for video group {0} was not exported ({1}); the bake result is unaffected."),

        // ---- 取舍清单（feat/tradeoff-list）：关掉哪些可取舍的实时元素能换整幅视频 ----
        // 子类型标签：前十个是可取舍的元素，后六个只出现在"关掉后还剩什么"里。
        ["tradeoff.kind.parallax"] = new(Zh: "鼠标视差", En: "mouse parallax",
            Legacy: "parallax (the view follows the mouse)"),
        ["tradeoff.kind.pointer"] = new(Zh: "鼠标交互（尾迹、涟漪、指针光效）", En: "pointer interaction (trails, ripples, cursor glows)"),
        ["tradeoff.kind.audio"] = new(Zh: "音频响应", En: "audio reactivity",
            Legacy: "audio reactivity (visuals driven by sound)"),
        ["tradeoff.kind.feedback"] = new(Zh: "帧反馈特效（基于上一帧的残影、拖影、扩散）", En: "frame-feedback effects reading the previous frame (afterimage, smear, diffusion)",
            Legacy: "feedback effects that read the previous frame (trails, smears, diffusion)"),
        ["tradeoff.kind.clock"] = new(Zh: "时钟、日期与系统信息", En: "clock, date and system readouts",
            Legacy: "the clock, date and system readouts"),
        ["tradeoff.kind.media"] = new(Zh: "正在播放的媒体信息", En: "now-playing media info",
            Legacy: "the now-playing media info"),
        ["tradeoff.kind.bgm"] = new(Zh: "壁纸内置 BGM 音轨", En: "the wallpaper's built-in BGM track",
            Legacy: "the wallpaper's own BGM track"),
        ["tradeoff.kind.fps"] = new(Zh: "帧率与运行状态显示", En: "framerate and status readout",
            Legacy: "the framerate and status readout"),
        ["tradeoff.kind.intro"] = new(Zh: "单次播放的入场动画", En: "one-shot intro animation",
            Legacy: "the one-shot intro animation"),
        ["tradeoff.kind.overlay"] = new(Zh: "赞助码、水印等覆盖层", En: "donation code or watermark overlay",
            Legacy: "a donation code or watermark overlay"),
        ["tradeoff.kind.hierarchy"] = new(Zh: "随父层保持实时的图层", En: "layers live by parent inheritance",
            Legacy: "layers that stay live with their parent"),
        ["tradeoff.kind.controller"] = new(Zh: "被实时脚本写入的图层", En: "layers written by a live script"),
        ["tradeoff.kind.foreground"] = new(Zh: "前景保留的实时图层", En: "live layers kept in the foreground"),
        ["tradeoff.kind.camera"] = new(Zh: "3D 透视相机路径", En: "the 3D perspective camera path"),
        ["tradeoff.kind.script_error"] = new(Zh: "源脚本报错", En: "source script error",
            Legacy: "a source script error"),
        ["tradeoff.kind.runtime_resource"] = new(Zh: "运行时资源依赖", En: "runtime resource dependency",
            Legacy: "a runtime resource dependency"),

        // {0}=方案数
        ["tradeoff.header"] = new(
            Zh: "取舍方案：共 {0} 个，按整幅可行性与禁用项数排序；禁用后需重新分析确认。",
            En: "Tradeoff options: {0} in total, ordered by full-frame feasibility and number of items disabled; re-analysis required after disabling.",
            Legacy: "Trading elements away can turn this into a full-frame video: the {0} option(s) below are ordered by whether they should reach full frame and how many things they turn off; analyze again to confirm."),

        // {0}=方案序号 {1}=关掉的项数 {2}=项目标签列表
        ["tradeoff.option_lead"] = new(
            Zh: "方案 {0}：禁用 {1} 项——{2}。",
            En: "Option {0}: disable {1} item(s) - {2}.",
            Legacy: "Option {0} (turns off {1}): turn off {2}."),

        // 实测：按这一档关掉后重新分析，29 案里 21 案真的进了整幅，所以这里说"最有希望"，不说死。
        ["tradeoff.route_full_frame"] = new(
            Zh: "禁用后预计无实时图层；整幅可行性需重新分析确认。",
            En: "No live drawing layer is expected to remain after disabling; full-frame feasibility requires re-analysis.",
            Legacy: "With those off no live drawing layer should remain, which is the best shot at a full-frame video (analyze again with them off to confirm)."),

        // {0}=预计残留的实时层数
        ["tradeoff.route_needs_check"] = new(
            Zh: "禁用后预计仍有 {0} 层实时渲染；整幅可行性需重新分析确认。",
            En: "An estimated {0} live layer(s) would still draw after disabling; full-frame feasibility requires re-analysis.",
            Legacy: "With those off an estimated {0} live layer(s) would still draw, so whether it reaches full frame needs another analysis."),

        // {0}=属性名清单 {1}=关闭值说明
        ["tradeoff.how_property"] = new(
            Zh: "可通过壁纸自带开关禁用：在 Wallpaper Engine 的壁纸设置中禁用属性 {0}（{1}）。",
            En: "Disable via the wallpaper's own switch: turn off the {0} property in its Wallpaper Engine settings ({1}).",
            Legacy: "The cleanest way is the wallpaper's own switch: turn off the {0} property in its settings in Wallpaper Engine ({1})."),

        // {0}=属性名清单 {1}=关闭值说明；这一条用在属性只盖住一部分要关的图层时。
        ["tradeoff.how_property_partial"] = new(
            Zh: "其中一部分可通过壁纸自带开关禁用：在 Wallpaper Engine 的壁纸设置中禁用属性 {0}（{1}）；其余图层无对应属性开关，只能从命令行禁用。",
            En: "Part of them can be disabled via the wallpaper's own switches: turn off the {0} property in its Wallpaper Engine settings ({1}); the remaining layers have no property switch and can only be disabled from the command line.",
            Legacy: "Some of it can be turned off with the wallpaper's own switches: turn off the {0} property in its settings in Wallpaper Engine ({1}); the remaining layers have no property switch and can only be turned off from the command line."),

        ["tradeoff.off_hint_each"] = new(
            Zh: "在属性面板中逐项禁用，各属性的关闭值见 plan 中该图层的 visible_property",
            En: "disable each of them in the property panel; each property's off value is in that layer's visible_property in the plan",
            Legacy: "turn each of them off in the property panel; each property's off value is in that layer's visible_property in the plan"),

        ["tradeoff.how_no_property"] = new(
            Zh: "这些图层的显示开关未绑定到壁纸属性，只能从命令行禁用。",
            En: "The visibility of these layers is not bound to a wallpaper property; they can only be disabled from the command line.",
            Legacy: "The visibility of these layers is not bound to a wallpaper property, so they can only be turned off from the command line."),

        // {0}=命令行参数
        ["tradeoff.how_command"] = new(Zh: "命令行写法：{0}。", En: "Command line: {0}."),

        // {0}=连带关掉的可画层数 {1}=层名清单
        ["tradeoff.collateral"] = new(
            Zh: "连带禁用：挂在这些图层下的 {0} 个可绘制图层同时被禁用（{1}）。",
            En: "Collateral: {0} drawable layer(s) under them are disabled as well ({1}).",
            Legacy: "Collateral: {0} drawable layer(s) sitting under them are turned off as well ({1})."),

        ["tradeoff.parallax_alternative"] = new(
            Zh: "鼠标视差另有一种改动更小的禁用方式：仅追加 --interaction fixed，不移除任何图层；重新分析后视差图层若仍需保持实时，再按上述清单排除。",
            En: "Mouse parallax has a lighter alternative: pass --interaction fixed alone, which removes no layer; if the parallax layers still stay live after re-analysis, exclude them as listed above.",
            Legacy: "There is a lighter way to turn parallax off: pass --interaction fixed alone, which removes no layer at all; if the parallax layers still have to stay live after analyzing again, exclude them as listed above."),

        // {0}=连带关掉的可取舍元素标签
        ["tradeoff.collateral_kinds"] = new(
            Zh: "其中还包括{0}。",
            En: "That includes {0}."),

        ["tradeoff.collateral_kinds_only"] = new(
            Zh: "连带禁用的还有{0}（无可绘制图层受影响）。",
            En: "Disabled along with them: {0} (no drawable layer is affected).",
            Legacy: "Turned off along with them: {0} (no drawable layer is affected)."),

        // 与实时元素无关的两种拦路：画面本来就被切成几块，或还没证明出可闭合的循环。
        ["tradeoff.caveat_structural"] = new(
            Zh: "结构性限制：画面已被拆成多块，且无一块可单独作为不透明底；该限制与实时元素无关，禁用下列项目未必能解决。",
            En: "Structural limitation: the wallpaper is already split into parts and none can serve as the opaque base on its own; this is unrelated to the live elements, and disabling the items below may not resolve it.",
            Legacy: "First, a caveat: this wallpaper is already split into parts with none of them able to serve as the opaque base on its own. That is a structural problem, unrelated to the live elements, and turning the items below off may not solve it."),

        ["tradeoff.caveat_no_loop"] = new(
            Zh: "尚未证明存在可闭合的循环周期；禁用实时元素未必改变该结论（详见 plan.json 的 loop）。",
            En: "No closing loop period has been proven yet; disabling live elements may not change that (see loop in the plan).",
            Legacy: "Also: no closing loop has been proven for this wallpaper yet, and turning live elements off may not change that (see loop in the plan)."),

        ["tradeoff.collateral_none"] = new(
            Zh: "无其它图层被连带禁用。",
            En: "No other layer is disabled along with them.",
            Legacy: "No other layer is turned off along with them."),

        // {0}=残留实时层数 {1}=残留类型标签
        ["tradeoff.residual"] = new(
            Zh: "整幅视频不等于零实时：禁用后预计仍有 {0} 层实时渲染（{1}），逐帧绘制，功耗收益相应降低。",
            En: "Full frame does not mean zero live layers: an estimated {0} layer(s) would still render every frame ({1}), reducing the power saving.",
            Legacy: "\"Full frame\" does not mean \"nothing live\": an estimated {0} layer(s) would still render every frame ({1}), so the power saving is reduced."),

        // {0}=残留实时层数 {1}=残留类型标签；这一条用在残留的都不画画面（例如只剩 BGM 音轨）时。
        ["tradeoff.residual_non_drawable"] = new(
            Zh: "画面无实时图层残留，仅{1}保持实时（不绘制画面，开销极低）。",
            En: "No live layer draws; only {1} stays live and draws nothing, so its cost is negligible.",
            Legacy: "No live layer draws any more; only {1} stays live, and it draws nothing, so its cost is small."),

        ["tradeoff.residual_none"] = new(
            Zh: "禁用后预计无实时图层残留。",
            En: "No live layer would remain after disabling.",
            Legacy: "No live layer would be left at all."),

        ["tradeoff.retain_live_note"] = new(
            Zh: "保留部分实时图层的路线实测无功耗收益：笔记本核显功耗 11.63 → 11.20 W，封装功耗 +12%。",
            En: "Keeping some layers live shows no measured saving: laptop iGPU power 11.63 -> 11.20 W, package power +12%.",
            Legacy: "Note: the \"keep a few layers live\" route measurably does not save power (laptop iGPU rail 11.63 -> 11.20 W, package +12%), so do not treat it as the power-saving fallback."),

        // 取舍清单只过了分析这一关：烘制阶段的循环分配会重新判定，粒子系统或主体动画没有周期时仍会失败。
        ["tradeoff.bake_stage_caveat"] = new(
            Zh: "禁用后需重新分析；生成阶段仍可能因粒子系统或主体动画无循环周期而失败。",
            En: "Re-analysis is required after disabling; generation can still fail when the particle system or the main animation has no loop period.",
            Legacy: "Also note: after turning these off you have to analyze again, and the bake stage can still fail when particles or the main animation cannot loop."),

        // {0}=画面主体是哪种实时输入
        ["tradeoff.subject_only"] = new(
            Zh: "画面主体由实时效果本身绘制（{0}），禁用后无剩余内容，因此无取舍方案：只能实时运行。",
            En: "The image is drawn by the live effect itself ({0}); disabling it leaves no content, so there are no tradeoff options: live rendering only.",
            Legacy: "The image here is the live effect itself ({0}); turning it off would leave nothing on screen, so there is no tradeoff list: this one can only run live."),

        ["tradeoff.dependency_blocked"] = new(
            Zh: "当前依赖分析未找到可生成的视频组；尚未确认关闭相关效果后的结果。相关依赖：{0}。",
            En: "Current dependency analysis found no video group; the result of disabling related effects has not been established. Related dependencies: {0}."),

        ["tradeoff.none_available"] = new(
            Zh: "无可取舍的实时元素：其余实时图层为技术性来源（透视相机、源脚本报错、运行时资源依赖）或随父层保持实时，均不可禁用。",
            En: "No tradeoff options: the remaining live layers are technical (perspective camera, source script error, runtime resource dependency) or live by parent inheritance, and cannot be disabled.",
            Legacy: "There is nothing to trade away here: the remaining live layers are either technical (perspective camera, source script error, runtime resource dependency) or merely live because their parent is."),

        // {0}=方案数
        ["summary.tradeoff_available"] = new(
            Zh: "取舍方案 {0} 个可换取整幅视频（详见 plan.json 的 tradeoff_options）。",
            En: "{0} tradeoff option(s) could yield a full-frame video (see tradeoff_options in the plan).",
            Legacy: "There are {0} tradeoff option(s) that could turn this into a full-frame video (see tradeoff_options in the plan)."),

        ["summary.tradeoff_subject_only"] = new(
            Zh: "画面主体即实时效果本身，无可取舍元素。",
            En: "The image is the live effect itself; nothing can be traded away.",
            Legacy: "The image here is the live effect itself, so there is nothing to trade away."),
        ["summary.tradeoff_dependency_blocked"] = new(
            Zh: "当前没有已确认的设置取舍方案。",
            En: "No settings tradeoff has been established for the current plan."),
    };

    /// <summary>全部 key，供测试与文档使用。</summary>
    public static IReadOnlyCollection<string> Keys => Table.Keys;

    /// <summary>取一条文案定义；未知 key 返回 null。</summary>
    public static Entry? Find(string key) => Table.GetValueOrDefault(key);

    /// <summary>把任意语言标记归一成 zh 或 en。</summary>
    public static string NormalizeLanguage(string? requested) =>
        requested is null ? English :
        requested.StartsWith("zh", StringComparison.OrdinalIgnoreCase) || requested.Contains("Hans", StringComparison.OrdinalIgnoreCase) ||
        requested.Contains("Hant", StringComparison.OrdinalIgnoreCase) ? Chinese : English;

    /// <summary>当前界面语言：zh-* 归为 zh，其余为 en。</summary>
    public static string DefaultLanguage() => NormalizeLanguage(CultureInfo.CurrentUICulture.Name);

    /// <summary>按语言取文案。未知 key 不抛异常，回退为 key 本身。</summary>
    public static string Get(string key, string language, params object?[] args)
    {
        if (Table.GetValueOrDefault(key) is not { } entry) return key;
        return Render(NormalizeLanguage(language) == Chinese ? entry.Zh : entry.En, args);
    }

    /// <summary>按 plan v3 模板渲染英文原文。未知 key 回退为 key 本身。</summary>
    public static string RenderLegacy(string key, params object?[] args) =>
        Table.GetValueOrDefault(key) is { } entry ? Render(entry.LegacyTemplate, args) : key;

    /// <summary>按 key 与中英各一套参数生成 {key, zh, en, params}。</summary>
    internal static JsonObject Localized(string key, object?[] chineseArgs, object?[] englishArgs) =>
        Table.GetValueOrDefault(key) is { } entry
            ? Build(key, entry, englishArgs, chineseArgs)
            : new JsonObject { ["key"] = key, ["zh"] = key, ["en"] = key, ["params"] = new JsonArray() };

    private static JsonObject Build(string key, Entry entry, object?[] args, object?[]? chineseArgs = null)
    {
        var parameters = new JsonArray();
        for (int i = 0; i < Math.Min(entry.PublicParameterCount, args.Length); ++i)
            parameters.Add(args[i] switch { null => null, int number => JsonValue.Create(number), _ => JsonValue.Create(args[i]!.ToString()) });
        // params 始终记英文参数：它是 plan 里 legacy 文本的原料，下游脚本按它对账。
        return new JsonObject { ["key"] = key, ["zh"] = Render(entry.Zh, chineseArgs ?? args), ["en"] = Render(entry.En, args), ["params"] = parameters };
    }

    private static string Render(string template, object?[] args)
    {
        if (args.Length == 0) return template;
        try { return string.Format(CultureInfo.InvariantCulture, template, args); }
        catch (FormatException) { return template; }  // 参数不足时宁可给出模板原文，也不让分析失败
    }

    /// <summary>层名清洗：控制字符、箭头、引号与过长的名字都会让一行结论看上去像程序输出乱了。</summary>
    public static string EscapeName(string? name, int maximumLength = 40)
    {
        string text = (name ?? "").Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
        text = text.Replace("-->", "->", StringComparison.Ordinal).Replace("<--", "<-", StringComparison.Ordinal);
        text = text.Replace('"', '\'').Replace('“', '\'').Replace('”', '\'');
        text = Regex.Replace(text, @"\s+", " ", RegexOptions.CultureInvariant).Trim();
        if (text.Length > maximumLength) text = text[..maximumLength].TrimEnd() + "…";
        return text;
    }

    /// <summary>把层名列成一句：最多 <paramref name="maximum"/> 个，超出以省略号收尾；总数由文案里的 {1} 单独给出。</summary>
    public static string NameList(IEnumerable<string?> names, int maximum = 5)
    {
        string?[] all = names.ToArray();
        string joined = string.Join(", ", all.Take(maximum).Select(name => "\"" + EscapeName(name) + "\""));
        return all.Length > maximum ? joined + " …" : joined;
    }
}
