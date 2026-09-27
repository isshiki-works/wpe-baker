# C-PERFREAD：分析与烘焙提速只读审查

- 基线：`main` @ `337c671`（Merge PR #222）。本次只读代码，没有运行任何分析或烘焙。
- 行号都以 `337c671` 为准，逐条对照过源码。
- 文中带"约 / 估"的秒数和倍数，是按代码路径加上维护者给的实测数据推算出来的，**不是实测**。第 7 节列了该怎么复测。
- 设备缩写：**C1** = CPU 单线程，**Cn** = CPU 多线程，**GPU** = Vulkan 渲染，**ENC** = NVENC / Vulkan Video 编码器，**FF** = ffmpeg / ffprobe 子进程，**IO** = 磁盘读写。

---

## 0. 结论摘要

1. **分析慢，根子在编排层的"整套重分析"。它是全串行、不去重的：**
   - 退回（`RetreatAsync`）每轮对每个视频组各做一整套 `SolveAsync`。每个 `SolveAsync` 又是 档位 × 布局 ×（状态）次子分析。
   - 慢分量留实时、静止证明留实时、单次轨回退这几条外层循环，每一轮都把上面这一整套（含退回）从头再跑一遍。
   - 子分析之间几乎没有内存共享：
     - 每次都对整个作品目录做 2 次 SHA-256；
     - 每次都重新解析 scene；
     - 循环分析的缓存键带着整份 `videoGroups` 和被烘层集合，所以留实时集合、档位、布局任何一项变了都会整份重算；
     - `VerifyStaticGroupBudgetAsync` 每次 `TryAsync` 都要对每个组各跑一遍**不走缓存**的 `LoopAnalysis.Analyze`。
   - 全程没有一处并行，9800X3D 的 8 核 16 线程只用上了 1 个。
2. **烘焙的固定开销里，最大的一块是合成校验挡住了主渲染。**
   - 每组主渲染开始前要先 `await SizeEstimate`（`Bake/GroupRenderScheduler.cs:284`）。这个值要等合成校验道**整段做完**才会填上（`HybridBakeService.cs:415`）。所以注释里说的"P4 并行"（`HybridBakeService.cs:392-394`）实际只和解包、起点搜索并行，**和 master_render 并不并行**。
   - 2984118135 那张作品的 composition_validation 6.9 s，就是主渲染在空等。
   - 短循环（4–14 s）的固定开销大致包括：
     - 合成校验：G 次探针烘焙 + 2 次整场景 48 帧渲染 + libx264 试编；
     - 源树整份拷贝约 6 次；
     - 源树整份哈希约 11 + 4G 次；
     - 每次启动渲染器都要冷编着色器；
     - 画质门对短片整片软解；
     - 硬解实测。
3. **渲染器每帧最多只有一帧在 GPU 上**（单命令缓冲、单 fence、单 staging）。
   - GPU 直编模式下，只有 `simulate()`（脚本、字段动画、相机）能和上一帧的 GPU 重叠。粒子模拟、视频软解与 sws、uniform 求值、命令录制都排在"等上一帧 fence"之后，这段时间 GPU 是空的（`engine/src/Scene/OfflineSession.cpp:382-413`）。
   - 118 帧/秒的 2984118135 很可能卡在这段 CPU 串行上，不是 GPU 算力不够。要用 `frames.jsonl` 的分段计时确认。
4. **按性价比排的前三项：**
   - 修掉合成校验对主渲染的门控；
   - 渲染器着色器缓存改成跨作业共享；
   - 分析编排做"进程内记忆化 + 退回轮内并行"。

   前两项改动小，风险低。第三项对 p99 和最慢的那几张作品是数量级收益，第 6 节给了详细清单。

---

## 1. 分析流程阶段表

### 1.1 编排层（`src/Baker.Core/Analysis/AnalysisOrchestrator.cs`）

| # | 阶段 | 做什么 | 设备 | 解析 / 渲染器 / 重算 | 为什么需要 | 能否省、并、缓存、并行 |
|---|---|---|---|---|---|---|
| O1 | 资产戳（`:65-68`） | 枚举整个 assets 目录的每个文件，取 (路径, 长度, mtime) 做缓存目录键 | C1 + IO | 每次运行 1 次 | assets 变了就让缓存失效 | 可以。WE 的 assets 有上万文件，可以改用目录 mtime 加少量采样，或者只在进程内算一次 |
| O2 | `SelectAsync` → `SolveAsync`（`:133`、`:256-281`） | 从请求的档位往省电方向逐档试（≤3 档），每档再逐布局试（≤2 个），识别出状态时每个状态再各试一遍。第一个能生成的就返回 | C1 | 每档每布局 1 次整套子分析，最多 3 × 2 ×（1 + S）次 | 档位回退、布局回退 | 同一档内的两个布局、同一布局下的三个档，在"分配之前"的阶段完全相同，可以复用（见 2.4） |
| O3 | 交互替代（`:136-145`） | 请求的交互策略生成不了时，把其余策略（keep → fixed → off）各跑一次 `SolveAsync` | C1 | 每个替代策略一整套 O2 | 给出"关交互就能生成"的建议 | 可以和 O4 **并行** |
| O4 | `RetreatAsync`（`:222-253`） | 每轮把每个视频组各留一次实时，各跑一次 `SolveAsync`，从能生成的候选里挑最好的 | C1 | 每轮 G 次 O2，最多约 G−1 轮，总计 O(G²) 次 `SolveAsync` | 放进视频的层太多、反而不省电时退回 | **轮内 G 次完全独立，可以并行**；也可以改成"先估再验"（见 2.5） |
| O5 | 普通图层组还给实时（`:158-164`） | `PlainGroupRetainRootsAsync` 本身只读 JSON，但命中后会多跑一整次 `SolveAsync` | C1 | +1 次 O2 | 只有普通图层的视频组省不下渲染 | 可以和 O6 并行；也可以复用退回里已经跑过的同一个留实时集合 |
| O6 | 不省电时试关交互（`:169-175`） | `SolveAsync("off")` 之后再跑一遍 `RetreatAsync(off)` | C1（+GPU） | +1 次 O2（含 off 的 measure 布局轮与 `InteractionPolicy.ExclusionsAsync`）+ 一整套 O4 | 给"关交互就省电"的建议 | 这只是个建议项：可以放后台，或者按需再跑 |
| O7 | `AdoptAllocationAsync` → `VerifyStaticGroupBudgetAsync`（`:347-416`） | 每次 `TryAsync` 之后执行：重新打开源、重新解析 scene 与 runtime.json，然后**对每个组**用 `scene.DeepClone()` 跑一遍 `LoopAnalysis.Analyze`（`:396-397`）。采纳子 plan 时对子 plan 再做一遍（`:356`） | C1 | 每次子分析：scene 解析 1–2 次，`LoopAnalysis` G 次，**全都不走缓存** | 证明静态组不计入视频路数 | **应当记忆化**：结果只取决于 (sourceHash, traceHash, 组 layer_ids, fps) |
| O8 | 单次轨回退（`:72-79`） | 以 `SingleShotLive = true` 整套再跑一遍 `SelectAsync` | C1 | ×2（含 O2–O6） | 旧行为兜底 | 只要请求里带着 SingleShotLive，缓存键就不同。其中"分配之前"的阶段仍然可以复用 |
| O9 | 静止证明留实时（`:83-97`） | 循环：把点名的 `source_static` 层加进留实时集合，整套再跑 `SelectAsync`，直到不再点名新层 | C1 | 每轮一整套 O2–O6 | 拆分放开后，第一轮看不到的层会逐轮冒出来 | 可以把一轮里点名的层一次加完；轮与轮之间要记忆化 |
| O10 | 慢分量闭合预检（`:101-114`，`Bake/SlowClosureProbe.cs:12-68`） | 准备捕获工程（整树拷贝）、可能跑一次起点搜索（`:45`）；然后每个慢分量组**启动 2 次渲染器**：一次渲第 0 帧，一次预热 P 帧后渲第 P 帧（`:51-52`）。没闭合就把这些层留实时，整套再跑 `SelectAsync` | GPU + C1 | 每轮：2 × 慢分量组数次渲染器启动，外加一整套 O2–O6 | 慢分量在 P 处闭合不了，烘焙时接缝门必然拒绝 | **两次渲染可以合成一次**：预热 P 帧的那次在第 0 帧就已经渲过了，让它同时留存第 0 帧即可。各组之间也可以并行 |
| O11 | 逐状态导出（`:321-345`） | 每个状态 1 次子分析 | C1 | S 次 | CLI `--daytime-split on` | 可以复用 O2 里 StatesAsync 已经跑过的同一个状态 |
| O12 | 写 plan（`:122-124`） | — | IO | 1 次 | — | — |

### 1.2 单次子分析（`src/Baker.Core/HybridScenePlanner.cs:165-317`，`AnalyzeSingleAsync`）

"随何变化"一列说明这一步的结果依赖哪些请求字段。凡是**不**依赖 RetainLive、Preset、Layout 的步骤，在退回、档位、布局这几层循环里都是纯重复。

| # | 阶段（行号） | 做什么 | 设备 | 解析 / 渲染器 / 重算 | 随何变化 | 能否省、并、缓存、并行 |
|---|---|---|---|---|---|---|
| A1 | 打开源（`:184`，`ProjectSource.cs:24-95`） | 解析 pkg 目录表 | C1 + IO | 每次子分析 1 次 | 不随任何参数变 | 进程内缓存条目索引 |
| A2 | `SourceHashAsync`（`:186`、`:315`，实现在 `ProjectSource.cs:243-260`） | 对作品目录下**所有**松散文件（scene.pkg、预览、视频）逐字节做 SHA-256 | C1 + IO | **每次子分析 2 次**，递归重查再加 2 次 | 不随任何参数变 | 在 `RunAsync` 里算一次，往下传。"源在分析期间被改过"的检查改成比较 (长度, mtime)，或者只在 `RunAsync` 结束时再哈希一次 |
| A3 | 读 scene、project（`:189-190`） | 走磁盘 JSON 缓存（`AnalysisCache.cs:12-19`），每次都 `ReadAllText` + `Parse` | C1 | 每次 2 次完整解析 | 不随任何参数变 | 放一份内存只读母本，用的地方各自 DeepClone |
| A4 | `OutputResolution`、`SceneGraph`（`:193-196`） | — | C1 | 便宜 | 不随参数变 | — |
| A5 | `RuntimeObservation.ObserveAsync`（`:199`，实现在 `RuntimeObservation.cs:17-97`） | 未命中缓存时启动一次渲染器：48 帧，宽 ≤512（`:25`），带 trace（`NativeRuntimeObserver.cs:40-43`）。命中时解析 trace 并**重写一遍 runtime.json**（`:95`）。`AudioEffects = omit` 时**先整树拷贝工程**（`:80-81`）再查缓存 | GPU（未命中时）+ C1 + IO | 渲染器最多 1–2 次；trace 每次都要解析 | 随交互策略（gpuTiming）、DaytimeState、音频选择变；**不随** RetainLive、Preset、Layout、Excluded 变 | 内存缓存 trace；把拷贝挪到缓存未命中的分支里 |
| A6 | `DaytimeSplit.Detect`、`Liveness.Analyze`、`HybridVideoProjection`（`:202-226`） | 实时判定、投影 | C1 | 1 次 | 随 ViewMode、SingleShotLive、状态变 | 可以和 A5 一起按"分配之前"的键缓存 |
| A7 | `Allocation.Plan`（`:227`） | 分配 | C1 | 1 次 | **RetainLive 和 Excluded 只在这里被读到**（`Allocation.cs:193`、`:290`） | — |
| A8 | `Composer`、`Verdict.Initial`（`:228-233`） | 构图（取决于 VideoLayout，见 `Composer.cs:210`）、初判 | C1 | 1 次 | 随分配、布局变 | — |
| A9 | `AnalyzeLoopWithNotes`（`:243`，实现在 `:83-112`） | 循环分析，走磁盘缓存。键（`:91-93`）里包括冻结后的整份 scene、**整份 runtime trace**、bakedLayerIds、projection、**整份 videoGroups**、宽高、fps、档位。**只算键就要把 scene 和 trace 各序列化一遍再做 SHA-256**。未命中时跑 `LoopAnalysis.Analyze`，有组时钟步长时再完整跑一次（`:105`） | C1 | 命中时：1 次 DeepClone，外加 scene 和 trace 各序列化一次；未命中时见下面 A9a–A9d | 随被烘层、分组、档位变 | 键收窄：`LoopAnalysis` 实际只读组的 `id` 和 `layer_ids`（`LoopAnalysis.cs:113-114`），不读 projection、W、H。另外按子步骤做细粒度记忆化（见 2.4） |
| A9a | `ShaderPeriodAnalysis`（`LoopAnalysis.cs:27`） | 用 trace 里的 `time_signature`，读 shader 源码找旋钮。不编译，不启动进程 | C1 | 每次循环分析 1 次 | 随档位（retime%）、上限变 | 按 (所有者层, 档位) 记忆化 |
| A9b | `ScriptTime`（`LoopAnalysis.cs:583-624`，实现在 `ScriptTime.cs:71-102`、`:905-932`） | 自写的 JS 解释器。有跨帧状态的脚本每帧解释一次，直到状态重复，最多 `min(上限帧数, 1M)` 帧（120 fps × 600 s 就是 72,000 帧）；每帧拼一次状态键字符串（长的还要做 SHA-256） | C1 | **每个 binding 每次循环分析都重新模拟**；粒子判定里对同一个 binding 还会再模拟一次（`ParticleStationarity.cs:379-381`） | 只随脚本源码、绑定值、fps、上限变 | **最该记忆化的一项**：键 = (脚本源码, 绑定值, 所属层冻结后的 JSON, fps, frameCap) |
| A9c | `RuntimeTrackReader.Read`、`ParticleStationarity`（`LoopAnalysis.cs:38`） | 读动画轨道；粒子平稳性判定；封顶粒子做整数模拟；精灵帧表要把整份 `.tex` 读进来 | C1 + IO | 每次循环分析 1 次 | 随被烘层变，**不随档位变** | 按所有者层记忆化 |
| A9d | `CommonLoopSolver.Suggest`（`CommonLoopSolver.cs:114-186`） | 从第 1 帧线性扫到上限帧数，Balanced 档最多扫 2 遍（`:118-122`）。一次 `LoopAnalysis` 里会被调很多次：主求解、不锁粒子重解、逐层"不能"证明（`LoopAnalysis.cs:79-110`）、`NoCommonLoopOwners` 贪心（`:526-534`）、`PerGroupLoop`（`:460-470`）、`GroupPeriods`（`:343`） | C1 | 每次循环分析几十到上百次 | 键 = 分量的规范化列表 + 预算 + 上下限 | 按请求记忆化。逐层的子问题在各轮退回之间完全相同 |
| A10 | 特效前缀（`:247-273`，`Routes.cs:22`/`:31`/`:34`） | `EffectPrefixPlanner.Propose` 对每层、每个前缀长度 DeepClone 一遍 runtime，再做一次循环分析。一次子分析里**最多被调 3 次**，而且每次都从头算。`PrefixCaptureProbes` 未命中时启动一次 64×64、1 帧的渲染器；**探测失败的结果不写缓存**（`PrefixCaptureProbes.cs:58-59`），所以每次子分析都会重新探一遍 | C1 +（GPU） | Propose 最多 3 次；失败的探针每次都重新启动渲染器 | 随档位变，不随留实时集合变 | 单次子分析内记住 Propose 的结果；失败结果在本次运行内缓存到内存 |
| A11 | `EnumerateDevicesOrNull`（`:275`） | 每次都加载 vulkan-1 并 `vkCreateInstance` | C1 | 每次 1 次 | 不随参数变 | 用 `static Lazy` |
| A12 | `PlanWriter.Compose`、`Routes.SettleAsync`（`:276-290`） | 尾组降级时按新分组再做一次循环分析（`:285-287`） | C1 | 循环分析 +1 | 随布局变 | — |
| A13 | 第二次读 scene（`:309`）、`ResidualMasking.ResourceReader`（`:310`） | 直接从 pkg 解析，**不走缓存**；资源读取器每次调用都重新解析 JSON | C1 | 每次 +1 次 scene 解析 | 不随参数变 | 复用 A3 的母本 |
| A14 | `RecordLoopAllocationFallbackAsync`（`:311`，实现在 `:325-430`） | "留非循环层实时再查"：递归调用整次 `AnalyzeSingleAsync` 1 次，需要放宽到作者根时再调 1 次（`:351-372`）。只在 RetainLive 为空时触发（`:330`） | 同整次子分析 | ×1–2 次整次子分析 | 随被点名的层变 | 见 2.1(h)，另外有一个缓存目录嵌套 bug |
| A15 | `Verdict.Conclude`、`PlanWriter.WriteAsync`（`:313-314`） | 收尾裁定、写出 plan。不启动任何进程 | C1 + IO | 1 次 | — | — |

**单次子分析的固定成本**：2 次整目录哈希、3 次以上 scene 解析、1 次 Vulkan 实例、至少 2 次"序列化 scene + trace 再算 SHA-256"做缓存键、重写一次 runtime.json；循环分析未命中时还有 A9a–d。

从已知数据倒推每次子分析的平均耗时：2955378002 是 1165 s ÷ 187 ≈ **6.2 s**，3798688689 是 412 s ÷ 274 ≈ **1.5 s**。维护者给的分析中位数是 2.8 s，说明普通作品也就一两次子分析。由此判断：**慢的作品是"次数多 × 每次都重算"两个因素相乘**，只做其中一个方向都不够。

---

## 2. 分析的重查循环

### 2.1 会触发整套重分析的路径

记号：P = 档位数（≤3），L = 布局数（≤2），S = 识别出的状态数，G = 当前视频组数。
一次 `SolveAsync` 最多触发 P·L·(1+S) 次子分析（交互为 off 时再加 L 次 measure）。每次子分析又可能递归 1–2 次（A14）。

| 路径 | 位置 | 触发条件 | 每次重算的内容 | 最多次数 | 与上一轮完全相同的工作 |
|---|---|---|---|---|---|
| (a) 档位与布局回退 | `AnalysisOrchestrator.cs:264-279`、`:302-306` | 前一档或前一布局不能生成 | 整次子分析 | P·L·(1+S) | A1–A6 全部相同（观测缓存能命中，但哈希、解析、Liveness 都重做）；换布局时 A9 的 ScriptTime、粒子、Shader 结果也相同 |
| (b) 交互替代 | `:136-145` | 请求的交互策略不能生成 | 每个替代策略一整套 (a) | (I−1)·P·L·(1+S) | 换交互会改变 gpuTiming 键和 ViewMode，A5、A6 要重算；A9b 的脚本结论可以复用 |
| (c) **RetreatAsync** | `:222-253` | 能生成但不省电（`VideoCostOverSaving`），或者整层不能生成，且组数大于 1 | 每轮对每个组：`kept ∪ group_i` 作为留实时集合，跑一整套 (a) | 每轮 G 次 `SolveAsync`；轮数最多到留实时集合覆盖所有根层为止（每轮至少多加一个组）。**最坏 Σ G_r ≈ G²/2 次 `SolveAsync` = G²/2 · P·L·(1+S) 次子分析** | 同一轮里 G 个候选的 A1–A6 完全相同，A9 里只有被烘层集合不同；上一轮 `next` 的计算结果在下一轮会作为某个候选的"子集"再算一遍 |
| (d) 普通图层组还给实时 | `:158-164` | 退回之后仍有只含普通图层的组 | +1 次 `SolveAsync` | 1 | 这个留实时集合常常已经在 (c) 里算过 |
| (e) 不省电时试关交互 | `:169-175` | 判不省电且有指针或音频层 | `SolveAsync(off)` + `RetreatAsync(off)` | 1 次 (a) + 一整套 (c) | off 的 measure 轮与主路径里的 off 替代（如果跑过）完全相同 |
| (f) 单次轨回退 | `:72-79` | 结果不能生成、而且入场单次轨进了视频组 | 整套 `SelectAsync`（含 b–e） | ×1 | 除 SingleShotLive 相关的 Liveness 以外，A1–A5 全部相同 |
| (g) **静止证明留实时** | `:83-97` | 不能生成、且点名了新的 `source_static` 层 | 整套 `SelectAsync`（含 b–e） | 每轮至少新增 1 层，最多到没有新层可点名为止 | 上一轮退回里试过的"留实时集合 ∪ 组"，在这一轮可能再出现 |
| (h) **留非循环层实时再查** | `HybridScenePlanner.cs:325-430` | 整层 unavailable 且只剩采集能力缺口，并且请求没带 RetainLive | 递归整次子分析，需要放宽到作者根时再来一次 | 每次**不带** RetainLive 的子分析 ×1–2。也就是 (a)(b)(f) 以及 (e) 的非退回部分，每次 `TryAsync` 都可能触发 | 1）`AdoptAllocationAsync` 会采纳这次递归的结果。之后 (c) 退回时 `kept` 里已经包含递归点名的层，再用 `kept ∪ g` 重跑，递归时的前半段又算一遍。2）**缓存目录嵌套 bug**，见下 |
| (i) 成本试算 | `InteractionPolicy.cs:16-82` | 交互为 off | 每个满屏音频层启动一次渲染器：48 帧、512 px、GpuTiming | 每层 1 次。采样成功才写缓存（`:74`），失败的每次重试 | — |
| (j) 慢分量留实时 | `:101-114` | 有慢分量组的预检没闭合 | 每组 2 次渲染器启动，外加整套 `SelectAsync` | 每轮至少新增 1 层 | 同 (g) |
| (k) 预检重查（残差起点、编码预检这一类） | 分析侧只有 (j) 会启动渲染器；烘焙侧的 `ScriptEvidenceGate`（`Bake/Gates/ScriptEvidenceGate.cs:18`）对旧 plan 会触发重分析 | — | — | — | — |

**缓存目录嵌套 bug**：
- `HybridScenePlanner.cs:187-188` 把 `request.AnalysisCacheDirectory` 改成了 `cache/<sha>`。
- (h) 递归时（`:351`）传下去的就是这个已经改过的 request，于是子分析的缓存目录变成 `cache/<sha>/<sha>`。
- 结果：**第一次递归时，父分析已有的缓存全部用不上**，观测 trace、循环分析、捕获探针都要重做，观测这一步还要重新启动一次渲染器。
- 修法：保存基础目录，不要再往下拼一次 sourceHash。只需改一行，属于正确性问题。

**2955378002 的 187 次大概是怎么来的**：
- 11 次"留非循环层实时再查"说明 (h) 触发了 11 次。
- 假设 P = 3、L = 2，(c) 退回时有 5–6 个组，要 2–3 轮：约 15 次 `SolveAsync`，每次平均 3–6 次子分析，合计约 60–90 次。
- 再加上 (g) 或 (j) 的一轮外层循环，整套再跑一遍，就能到约 180 次。

这只是量级上的推算。实际分布要看 `run-*/` 下的目录名（`retreat-N`、`static-live-*`、`slow-live-*`、`plain-groups-live`、`single-shot-live`）来确认。

### 2.2 在所有重查之间完全重复的工作

1. 整目录 SHA-256：每次子分析 2 次，递归再加 2 次。
2. scene 解析：每次子分析至少 3 次（`:189`、`:309`，外加 O7 的 `AnalysisOrchestrator.cs:391`），`ApplyGenerationAdmission` 还要再解析（`Admission.cs:98-105`）。
3. 观测 trace 的解析，以及 runtime.json 的重写。
4. Liveness、Projection。它们只取决于交互策略、状态和 SingleShotLive。
5. ScriptTime 对同一个 binding 的模拟：每次循环分析都重做，而且结论与留实时集合、布局无关。
6. `CommonLoopSolver.Suggest` 的逐层子问题（`LoopAnalysis.cs:86`、`:100`、`:529`）。输入一样，结果就一样。
7. O7 的逐组静态证明。
8. 特效前缀的 Propose（一次子分析内最多 3 次）。
9. `vkCreateInstance`。

### 2.3 为什么只"缓存整次子分析"不够

维护者提出按"留实时集合 + 设置"做记忆化，这个方向对，但要分两层做：

- **整次子分析的记忆化**：键是规范化后的请求（去掉 `OutputDirectory` 和 `RuntimeTraceFile`，RetainLive 排序去重）。它能直接挡住 (d) 与 (c) 的重复、(g)/(j) 外层轮之间的重复，以及 (h) 递归结果与之后同一集合的主路径请求之间的重复。
  - 要注意：plan 里的 `runtime_evidence`、`analysis_directory`、`loop-allocation-analysis/plan.json` 这些路径指向旧的输出目录。命中时要么复制这些文件，要么明确允许 plan 引用旧的 `run-*/N` 目录（它们在同一个 run 目录下，本来就不会被删）。
  - 估计命中率：退回和外层循环里完全相同的请求约占 20–40%（估）。
- **阶段级的记忆化才是大头**。因为多数重查的请求**并不完全相同**，只差留实时集合或档位。

### 2.4 建议的记忆化分层

建议都放在进程内。`AnalysisOrchestrator.RunAsync` 持有一个 `AnalysisMemo` 对象，把它传进 `AnalyzeSingleAsync`。

| 层 | 键 | 值 | 能省下什么 |
|---|---|---|---|
| M0 源 | 运行开始时算一次 sourceHash，每个文件的 (长度, mtime) 作为防篡改检查 | 哈希、`ProjectSource` 条目索引、scene 和 project 的只读母本 | A1、A2、A3、A13，以及 O7 和 `Admission` 的重复解析 |
| M1 观测 | `ObservationKey`（已有） | trace 对象，外加预先算好的 `traceHash` | A5 的解析和写盘。所有缓存键里的 trace 都改用 `traceHash`，不用每次序列化整份 trace |
| M2 分配之前 | (sourceHash, 属性, 宽高, fps, Interaction, ViewMode, SingleShotLive, DaytimeSplit/State, AudioEffects) | observation、Liveness、Projection、DaytimeSplit 的结果 | A4–A6。档位、布局、留实时集合各种组合全部共用 |
| M3 ScriptTime | (脚本源码, 绑定值, 所属层冻结后的 JSON, fps, frameCap) | `Verdict` | A9b，包括粒子判定里的第二次模拟 |
| M4 按所有者层 | (frozenSceneHash, traceHash, 所有者层, fps, 上限[, 档位]) | Shader 分量、动画轨道、粒子判定 | A9a、A9c |
| M5 求解器 | 分量的规范化列表（id、精确周期、AllowRetime、上限）+ fps + 最小和最大时长 + 预算 + 取向 | `CommonLoopSearchResult` | A9d。逐层证明和贪心并入在各轮之间高度重复 |
| M6 循环报告 | (frozenSceneHash, traceHash, 排序后的被烘层, 分组只取 id 和 layer_ids, fps, profile, 取向, FullLoop) | `LoopReport` | A9 和 O7。O7 的逐组静态证明也用这一层 |
| M7 整次子分析 | 规范化请求 | plan（外加它引用的文件） | 见 2.3 |

预计效果（估）：
- 退回、档位、布局这三层循环里，每次子分析从"全量重算"降到"只做 A7–A8，外加 M6 未命中时的求解器部分"。
- 按 2955378002 的 6.2 s 一次算，降到 1–2 s 一次。**187 次 × 约 1.5 s ≈ 280 s，而不是 1165 s。** 这还没算上 2.5 节的减少次数和并行。

### 2.5 回退搜索：从"逐组线性试"改成更少的轮数

现状（`AnalysisOrchestrator.cs:231-251`）：
- 每一轮把 G 个组**各**留一次实时，各跑一整套 `SolveAsync`，取收益 `Margin` 最大的那个。
- 一个都不可行时，就从"离可行最近"的那个接着退。
- 轮数最多约 G−1，总计 O(G²) 次 `SolveAsync`。

建议分三步改，可以只做前一两步：

1. **轮内并行（语义不变）**
   - 一轮里的 G 次 `SolveAsync` 互相独立：各自有 `retreat-N` 目录和各自的 budget 对象；磁盘缓存本来就是先写临时文件再 move，是原子的。
   - 改法：`Task.WhenAll` 加一个 `SemaphoreSlim(Environment.ProcessorCount / 2)`。
   - 两处要改成线程安全：`retreats.Value` 的自增改 `Interlocked.Increment`；进度上报的顺序要按组序号排好再上报。
   - 所有候选跑完后仍按组序号比较 `Margin` 和 `Gap`，这样结果与串行逐字节相同。
   - 预计：退回这段的墙钟 ÷ min(G, 8)。
   - 风险：
     - 渲染器探针（A5、A10）可能在多个任务里同时触发。M1 要做成"同一个键同时只算一次"（例如 `ConcurrentDictionary<key, Lazy<Task>>`），不然会同时启动好几个渲染器。
     - 内存峰值会变成 G 份 scene 加 trace。
2. **先估再验（减少轮数）**
   - `Margin(plan) = RemovedPassCoverage − GroupCount × MinPassCoveragePerStream`。每个组对 `RemovedPassCoverage` 的贡献，在当前 plan 的 `bake_value` 里已经按组算出来了（按画布占比加权的 pass 数）。
   - 所以"把第 i 组留实时之后的 Margin"可以**不做分析、直接估出来**：当前 Margin − 第 i 组的覆盖贡献 + MinPassCoveragePerStream。
   - 按这个估值从高到低排序，只真正验证前 k 个（k = 2–3）；验证不过再往后试。
   - 平均每轮从 G 次降到约 2 次；最坏情况退化成现在的做法。
   - 风险：留实时会改变分组（拆分和合并），估值可能不准。所以估值只用来决定"先验证哪个"，最终选谁仍以真实分析结果为准。
3. **一次留多个（减少轮数）**
   - 每组的"单位视频路数带来的覆盖"低于 `MinPassCoveragePerStream` 的，本来就是负收益。可以一轮把所有负收益组一起留实时，验证一次就够。
   - 这样通常 1–2 轮就能收敛，而现在是 G−1 轮。
   - 不可行时再退回逐组试。
   - 风险：结果可能和现在的贪心不同（可能更保守地多留了实时层）。需要在 501 张作品上对比 `video_groups` 和 `bake_value`。

**外层循环也可以合并**：
- (g) 静止证明留实时和 (j) 慢分量留实时，每一轮都整套再跑 `SelectAsync`，其中包括又一次完整的 (c) 退回。
- 可以把这两条的点名合并进同一个留实时集合，一次性再分析。
- 可以把外层循环挪到退回之前，先把"必然要留实时的层"定下来，再做退回。
- 可以让 (j) 的预检复用上一轮的读数：没有变过的组不再渲染。

### 2.6 分析侧其它小项

- **O10 两次渲染合成一次**：预热 P 帧的那次渲染本来就经过了第 0 帧，让它同时留存第 0 帧即可。这样每组少启动一次渲染器；各组之间也可以并行（现在 `SlowClosureProbe.cs:41` 的并行度传的是 1）。
- **失败的前缀探针结果在进程内缓存**（`PrefixCaptureProbes.cs:58-59`）。现在一次失败的探测会在每次子分析里各重试一遍，每次都要启动渲染器。
- **观测渲染不必写帧**：`NativeRuntimeObserver` 只需要 trace，却写了 48 帧 RGBA（宽 ≤512，约 28 MB）再删掉（`NativeRuntimeObserver.cs:40-50`）。可以关掉读回，只保留光栅化，因为有些 trace 行为依赖光栅化。
- **给分析加 `analysis_timing`**：现在分析侧没有任何阶段计时。建议仿照烘焙的 `StageTiming`，在 plan 里按 A1–A15 和编排路径 (a)–(j) 记次数与耗时。这是确认上面这些推算的前提，改动很小。

---

## 3. 烘焙流程阶段表

入口是 `src/Baker.Core/HybridBakeService.cs`。阶段名定义在 `StageTiming.cs:10-21`。G = 组数，R = 残差组数，W = 预热帧数，P = 周期帧数，C = 淡化帧数（0.4 s，`ResidualMasking.cs:92-97`）。

| # | 阶段 | 做什么（行号） | 设备 | 渲染器启动 / 帧数 / 重算 | 为什么需要 | 能否省、并、缓存、并行、上 GPU |
|---|---|---|---|---|---|---|
| B0 | preflight（记在 other） | 源树整份哈希（`HybridBakeService.cs:377`）；闸门链（`IBakeGate.cs:65-68`）。旧 plan 会经 `ScriptEvidenceGate.cs:18` 触发重分析 | C1 + IO | 0 | 源没变、循环准入 | 哈希在同一次烘焙内按 (路径, 大小, mtime) 记忆 |
| B1 | **composition_validation**（整道墙钟记 overlapped，`:414`；主道先做完时只记等待的那段，`:531-535`） | `ProbeBake.cs:24` 递归烘一个 48 帧（加入场帧）的探针：每组各启动一次渲染器，写无损 master，再用 **libx264** 编码；然后解包原作参照（`:57`）；`CandidateValidation.CompareAsync`（`CandidateValidation.cs:69-72`）对原作和探针工程**各启动一次全分辨率整场景渲染**，stdout 锁步，CPU 上 AVX2 逐瓦片比较；最后 `EstimateEmbeddedVideoAsync`（`HybridBakeService.cs:927-957`）按 x264 试编结果外推体积 | GPU + Cn（x264）+ C1 | G + 2 次渲染器启动，3 次整树解包，G 次 x264 编码 | 合成正确性闸门；内嵌视频 2 GiB 外推 | 见 4.2 |
| B2 | source_capture（`:487`，另有 `CaptureSourceBuilder.cs:19-20`） | 两次整树拷贝（捕获副本 + 成品工程底稿，`ProjectSource.cs:283`） | C1 + IO | 0 | 捕获副本要冻结属性、打循环补丁 | `Task.WhenAll` 并行；同盘时用硬链接 |
| B3 | loop_start_search（`Bake/LoopStartSelector.cs:35`） | 每个残差组启动一次渲染器，最多 3 路并行（`:37`）。模拟 SearchWarmup + P + min(P, P_min + C) 帧，只光栅化步长 = **gcd(P, 16)** 的采样帧（`LoopStartSelector.cs:23`，`ResidualMasking.cs:83-89`），GPU 盒式降采样到 512 宽再读回；C# 端做瓦片残差比较 | 渲染器内 C1 模拟 + GPU 光栅 + C1 比较 | R 次启动；约 W + 2P 帧模拟 | 在周期内挑残差最小的相位 S | 见 4.3 |
| B4 | **master_render**（`:612-613`） | `GroupRenderScheduler.StartAsync`（`:282-455`）。在 5090 上走 GPU 直编：渲染 → 引擎内 RGBA→NV12 compute → NVENC（AV1/HEVC）或 Vulkan H.264（`GpuVideoEncoder.cpp:398-433`），**没有管道**。组间最多 3 路并行（`HybridBakeService.cs:506-507`）。长的非残差组、且没有视频纹理时分段（`GroupRenderScheduler.cs:294-298`，`NativeRenderRunner.Segments.cs:24`） | GPU + ENC + 渲染器内 C1 | 每组至少 1 次启动，每次都重新解析场景、冷编着色器；帧数 = W + S + P + C（残差组）或 P + 1；可能的重渲：覆盖度预通道、溢出、降 QP、超 2 GiB、换编码格式（`:301-445`） | 出成品视频 | 见第 5 节；残差组不能分段（`Segments.cs:24` 要求 `CrossfadeFrames: 0`） |
| B4a | 画质门（在 master 内，`NativeRenderRunner.Gpu.cs:15-71` → `FfmpegQualityComparer.cs:64-119`） | 硬件编码结果的 SSIM 门。**循环 ≤ 4096 帧时整片顺序软解**（`FfmpegQualityComparer.cs:112-118`，阈值见 `QualityGate.cs:33`），更长的按 9 个窗口 seek | FF（Cn 软解） | 每组 1 次 | 硬件编码的画质闸门 | 改用 `-hwaccel` 解码，或者在引擎里对留存帧直接算 SSIM |
| B4b | 本机硬解实测（在 master 内，`GroupRenderScheduler.cs:410-418`） | ffprobe，再对每个播放适配器用 D3D11VA 解 5 帧 | FF + NVDEC | 每组 1 次 | AV1/HEVC 在本机能不能硬解 | 编码格式和尺寸相同的组共用一次结果 |
| B5 | seam_check（残差，`:650-652`） | `MeasureSeamResidualAsync`（`NativeRenderRunner.Seam.cs:194-352`）：直接读渲染器留下的 loop-window.rgba，全分辨率比较 2C 帧的瓦片 | C1 | 0 | 残差的第一层裁决 | 已经很便宜 |
| B6 | crossfade（`:689-691`） | GPU 路线只拷贝 JSON；无损 master 路线走 `MasterRewrite.CrossfadeAsync` | ≈0 / FF | 0 | 接缝淡化 | GPU 路线已经是 0 |
| B7 | seam_check（编码后，`:729-731`） | `GroupVerdicts.SeamAsync` → `EncodedLoopValidator.cs:187-312`：比较留存的原帧，一次 ffprobe 读容器头；**不解码成品** | C1 + FF | 0 | 闭合、帧数、帧率 | 很便宜 |
| B8 | encode_slot_wait、encode_playback（`Bake/GroupEncoder.cs:52-57`） | 直编路线只接管成品；无损 master 路线用 x264 整片重编 | ≈0 / Cn | 0 | 成品编码 | 5090 上这一项约为 0 |
| B9 | hardware_decode_check（`:766-770`） | 优先复用 B4b 的结果；否则 `ProbeHardwareDecodeAsync`（`NativeRenderRunner.Hardware.cs:14-182`）：ffprobe 加上每个适配器一次 `ffmpeg -hwaccel d3d11va` 解 5 帧 | FF + NVDEC | 0 | 播放机器能不能硬解 | 同编码格式、同尺寸的组共用一次 |
| B10 | project_assembly（`:774-857`，`ProjectPublisher.cs:35-39`） | 视频重封成 .tex；写场景；发布时再整目录解包一次 | IO | 0 | 成品工程 | 发布时改为移动或硬链接 |
| — | renderer_wall | 各组 `wall_seconds` 的累加，与其他阶段重叠 | — | — | — | 芙莉莲 107 s 对 56.6 s，说明组间并行确实生效了 |

**每次烘焙的隐性固定成本**：
- **源树 SHA-256 约 11 + 4G + 2R 次**：
  - 每次 `RenderAsync` 2 次（`NativeRenderRunner.cs:196`，再加 `:373` 或 `:661`）；
  - 每次 `RenderRawAsync` 2 次（`NativeRenderRunner.Raw.cs:38`、`:86`）；
  - `CandidateValidation` 两边各 1 次；
  - `ProbeBake` 1 次（`ProbeBake.cs:31`）。
- **渲染器 exe 的 SHA-256**：每次渲染都算一遍（`NativeRenderRunner.cs:197-198`）。
- **整树拷贝约 6 次**：主道 2 次、探针 2 次、参照 1 次、发布 1 次。
- **着色器冷编译**：每次启动渲染器都要做，原因见 5.3。

---

## 4. 烘焙的固定开销，逐项

### 4.1 source_capture（2984118135 实测 2.2 s）

- 能否重叠：它已经和合成校验道并行了。它本身是两次整树拷贝，顺序执行（`HybridBakeService.cs:485-487`）。
- 能否省：
  - 两次拷贝改成 `Task.WhenAll`；
  - 成品工程底稿对未修改的大文件（视频、贴图）用硬链接，同盘时几乎为 0；
  - 原作参照（B1）的解包也可以和探针并行，或者直接复用这一份捕获副本。
- 短循环：固定 2 s 左右，对 4 s 的循环影响很大。**硬链接之后应该能降到 0.2 s 以内（估）。**
- 上 GPU：不适用。

### 4.2 composition_validation（2984118135 实测 6.9 s 空等；芙莉莲被起点搜索盖住）

- **能否与渲染重叠：现在实际上不能**，这是最高优先级的问题：
  - 每组 `StartAsync` 一开始就 `await SizeEstimate`（`GroupRenderScheduler.cs:284`），而 `SizeEstimate` 要在校验道的 `finally` 里才 `TrySetResult`（`HybridBakeService.cs:415`）。
  - **改法**：先用 `quantizer_offset = 0` 开始渲染。只有按码率上界估算（例如 NVENC 的目标码率 × 时长）接近 2 GiB 时，才去等外推结果。
  - 超限本来就有"按实际字节重渲"的兜底（`GroupRenderScheduler.cs:380-395`）；校验被拒时，提前开跑的产物本来也会整份丢掉（`HybridBakeService.cs:537-548`）。**所以语义不变。**
  - 预计：2984118135 省 4–7 s / 36 s（GPU 争用会吃掉一部分）。
  - 对芙莉莲这类长循环：起点搜索（12.4 s）和校验道现在是互相遮盖的。先修这一条，之后再优化起点搜索才有收益。
- 能否降采样：
  - 配对比较现在是**全输出分辨率**、整场景各 48 帧（`CandidateValidation.cs:69-72`）。合成正确性判的是"有没有摆错、漏层"，改成 1/2 分辨率的瓦片比较，阈值应该仍然成立。但这会改变判据，需要在 FINAL-A 集上对比误判。
  - 探针用的 libx264 试编只服务于体积外推：短循环可以跳过（见下）；长循环可以改用 NVENC 试编，让外推的码率律和真正的编码器一致。
- 能否上 GPU：比较本身是 AVX2 逐瓦片，已经很快。主要成本是 G + 2 次渲染器启动加冷编着色器，这一块靠 4.7 的共享着色器缓存解决。
- **短循环跳过或共用**：
  - 循环 ≤ 约 20 s 时，**体积外推没有意义**：60 fps 下 1200 帧，任何码率都到不了 2 GiB。可以直接 `SizeEstimate = null`。
  - 这时可以**直接拿正式成品做 48 帧配对比较**，不再单独烘探针：省掉 G 次探针渲染、G 次 x264 和 2 次解包，校验道从约 9 s 降到约 3–4 s（估）。
  - 代价是校验被拒时，已经渲完的正式成品就白渲了。但短循环本来就便宜，这个代价可以接受。

### 4.3 loop_start_search（芙莉莲 12.4 s，3436033033 22.2 s）

- 现状：
  - 渲染器要**模拟**约 W + 2P 帧，只**光栅化**采样帧（`OfflineSession.cpp:396-400`）。
  - 芙莉莲 P ≈ 32,000 帧，要模拟约 64,000 帧，也就是约 5000 帧/秒的纯 CPU 模拟。
- **陷阱**：步长是 `gcd(P, 16)`（`ResidualMasking.cs:83-89`）。**P 为奇数时步长等于 1**，每一帧都是候选、每一帧都要光栅化和读回。3436033033 的 22.2 s 可能就是这种情况（待核实：看它的 `start_search` 里的 stride）。
  - 可以改成：步长取不超过 16、且让 P 的"残余相位"可控的值。或者先粗搜（步长 16，允许候选不整除），再在最优值附近 ±16 帧细搜一次。
- 能否与渲染重叠：
  - 不能直接重叠。master 的起点 S 要等搜索结束才知道；非残差组也要共用同一个 S，以保持组间相位（`LoopStartSelector.cs:7-9`）。
  - 可以做的是：**非残差组如果与残差组没有相位约束**（`GroupFrames` 各自独立、没有共享动画，见 `LoopAnalysis.SharesAnimation`），可以先开始渲染。
- 能否降采样：已经降到 512 宽了。
- 能否缩窗：候选只取前 N 秒（例如 30 s）的相位，窗口从 2P 缩到 P + N。芙莉莲约从 12.4 s 降到约 7 s（估）。代价是可选的相位变少。
- 能否复用 master：
  - 搜索进程已经模拟过 [0, S+P+C)，master 又从 0 开始重新模拟 W + S 帧。
  - 彻底的做法是"搜索完成后，让同一个渲染器进程回到 S 继续编码"，这需要给渲染器加"快照 / 回滚"能力，**改动大**，暂不建议。
- 短循环：P 小，这一项本身就便宜；主要成本是渲染器启动一次，靠 4.7 解决。

### 4.4 seam_check（芙莉莲 1.1 s）

- 已经很便宜。B5 读的是留存的原帧，B7 不解码成品。
- 如果要改：B5 的瓦片比较可以 `Parallel.For` 按瓦片行切分，1.1 s 能降到约 0.2 s。优先级低。

### 4.5 hardware_decode_check

- 每个适配器约零点几秒到 1 秒（启动 ffmpeg 并初始化 D3D11VA）。AV1 和 HEVC 直编组已经复用了 B4b 的结果（`HybridBakeService.cs:768`）。
- 可以改：
  - 同一个编码格式和尺寸档，一次烘焙只测一次；
  - 同一台机器上，(编码格式, profile, 尺寸档, 驱动版本) 的结果可以落盘缓存，以后的烘焙直接取。
- 短循环：这是纯固定开销，**建议跨烘焙缓存**。

### 4.6 残差淡化

- GPU 路线：淡化在引擎内完成，master 多渲 C 帧（0.4 s），没有额外的渲染遍；`crossfade` 阶段只拷 JSON（`HybridBakeService.cs:690`）。已经没什么可省。
- 真正受淡化影响的是：**残差组不能分段**（`NativeRenderRunner.Segments.cs:24`），所以芙莉莲这种 533 s 的残差组只能单进程渲染。
  - 可以改成：首段留存 f[0..C)，末段留存 f[P..P+C)，头段按现有的 CPU 直编方式另外编码后拼接（`NativeRenderRunner.cs:506-535` 已经有拼接逻辑）。
  - 前提：单进程并没有把 GPU 或 NVENC 跑满。要先用 `frames.jsonl` 里的 `step_ms` 确认。
  - 预计：芙莉莲的 master_render 省 15–25 s / 56.6 s（估）。

### 4.7 跨项：渲染器冷启动

- 着色器缓存目录固定为 `job.output / "shader-cache"`（`engine/tools/SceneBake/main.cpp:234`），而 `output_dir` 每次都是新建的，所以**每次启动都是冷缓存**，要重新预处理并用 glslang 编译全部着色器，而且是单线程（`ShaderParser.cpp:2880-2918`）。
- 引擎里没有 `VkPipelineCache`。
- 一次烘焙的启动次数：G（master）+ G（探针）+ 2（配对比较）+ R（起点搜索）+ 各种重渲。分析阶段还有观测、前缀探针、成本试算、慢分量预检。
- **改法**：
  - cache 目录改成跨作业共享，例如 `%LOCALAPPDATA%/wpe-baker/shader-cache/<渲染器摘要>`。缓存本来就按内容寻址，读取时还会校验（`ShaderParser.cpp:2823-2862`），共享是安全的。写入要用"临时文件 + 原子重命名"。
  - 另外可以加一个磁盘上的 `VkPipelineCache`。
- 预计：每次启动省 0.5–2 s，着色器多的作品可能更多（估）。短循环一次烘焙约启动 2G + 3 次，**合计省 3–10 s**。

---

## 5. 渲染器每帧的 CPU 工作

主循环在 `engine/tools/SceneBake/main.cpp:676-741`。每帧 `OfflineSession::Impl::runFrame`（`OfflineSession.cpp:299-306`）依次执行 `beginFrame`、`simulate`、`draw`、`advance`。

| 工作项 | 位置 | 线程 | 与 GPU 的关系 |
|---|---|---|---|
| 脚本（QuickJS，单个 JSRuntime） | `Script.cpp` 中的 `TickAll`（`:4229-4357`），调用点 `OfflineSession.cpp:370` | C1 | 在 `simulate()` 里，**能**和上一帧 GPU 重叠（GPU 直编模式下） |
| 字段动画、相机路径、材质动画 | `Scene.cpp:2959-3018` | C1 | 同上。相机路径每帧新建 3 个 `HashSet<String>`（`:2962-2968`） |
| **等上一帧**：`finishPendingFrame` | `OfflineSession.cpp:382` → `VulkanRender.cpp:1183-1202` | 阻塞 | CPU 等 GPU |
| **粒子模拟 + Extract** | `BeforeRender`（`OfflineSession.cpp:391`）→ `ParticleRuntime.cpp:945-950` 逐子系统串行 | C1 | **排在 fence 之后，这段时间 GPU 空转** |
| **视频纹理**：软解 → sws 转 NV12 → memcpy → compute 转 RGBA | `hwdec = "none"` 写死（`VulkanRender.cpp:708-710`），`VideoSource.cpp:270` sws，`Nv12ToRgba.cpp:472-479` 自带 fence 并单独提交 | 解码有 ffmpeg 线程，但主线程同步等帧；sws 在 C1 | **排在 fence 之后，GPU 空转** |
| 字形图集上传 | `VulkanRender.cpp:501-538` → `TextureCache.cpp:955` 调 **`vkDeviceWaitIdle`** | 阻塞 | 有新字形的那一帧会把整个设备排空（包括编码） |
| 渲染图重建 | `OfflineSession.cpp:387-390`，由 `Scene.cpp:2296`（SortLayer）、`:2728`（效果可见性）触发 | C1 | 平时为 0；脚本切换效果可见性时出现尖峰 |
| uniform 求值与上传 | `UniformBuffer.cpp:593-648`（每个绑定每帧都堆分配一个 `Vec`，`:616`） | C1 | fence 之后 |
| 命令录制（单个命令缓冲，每帧重录） | `VulkanRender.cpp:1336-1353`；每个 buffer copy 各发一个 barrier（`BufferManager.cpp:481-496`） | C1 | fence 之后 |
| 提交 + **同步等 fence**（非直编模式） | `VulkanRender.cpp:1482-1488`：`defer_completion` 只在 `gpu_scene_overlap` 成立时为真 | 阻塞 | 完全串行 |
| 读回（单个 staging，memcpy 8.3 MB） | `VulkanRender.cpp:1549-1579`，staging 在 `:850-864` | C1 | 串行 |
| 写管道（`fwrite` 到 stdout） | `main.cpp:714-720` | C1 | 串行；管道背压会直接卡住渲染 |
| GPU 直编：RGBA→NV12 compute + NVENC | `GpuVideoEncoder.cpp:959-1177`，NVENC 环 8 个槽（`:158`） | GPU / ENC | 和下一帧的 `simulate()` 重叠 |
| trace_scene 依赖追踪 | `Core.cppm:192-198`：每次访问拼 6 段 `std::string` 再插哈希表 | C1 | master 也开着（`GroupRenderScheduler.cs:226` 设 `TraceScene: !probe`），因为 `HybridBakeService.cs:627` 要合并它的 `runtime_dependencies` |
| 色彩转换 | 直编路线在 GPU 上（`GpuVideoEncoder.cpp:772-857`，BT.709 limited）；CPU 路线交给 ffmpeg swscale（`NativeRenderRunner.cs:417-437`） | GPU / Cn | — |

**让 GPU 吃不满的原因，按影响排序**：

1. **只有一帧在飞，而且粒子、视频、uniform、录制都排在 fence 之后**（`OfflineSession.cpp:382` 之后才执行 `:391`、`:404`、`:409`、`:413`）。
   - 帧时间 ≈ max(GPU, simulate) + 粒子 + 视频 + 准备，而不是 max(GPU, 所有 CPU 工作)。
   - **最低成本的改法**：把 `ParticleRuntime::Tick`（模拟部分）以及视频的 `next_frame` / `to_nv12` 挪到 `finishPendingFrame` 之前，只把 `Extract`、`SetDirty` 和上传留在 fence 之后。
   - 要保证确定性：只调顺序，不改随机数的消费顺序（粒子用的是每个子系统自己的 `ObjectRandomScope`，见 `ParticleRuntime.cpp:947`）。
   - **彻底的改法**：命令缓冲、fence、staging、uniform 各做 2–3 份，也就是真正的帧双/三缓冲。
   - 预计：CPU 时间和 GPU 时间接近的场景最多 2 倍；**2984118135（118 帧/秒）如果以粒子或视频为主，预计 1.3–1.8 倍（估）。**
2. **视频软解 + sws + 独立 fence**。
   - 改法：每路视频一个预取解码线程，按 PTS 顺序出帧；源是 yuv420p 时跳过 sws，直接上传三个平面由 shader 读取；转换 dispatch 录进帧命令缓冲，去掉单独的 fence。NVDEC 要先验证和软解逐位一致，否则会影响可复现性。
3. **粒子单线程**。
   - 各子系统已经各用各的随机数引擎，可以跨子系统并行。
   - 前提：`object_random` 要预先 `try_emplace`；`trace()` 要改成按线程收集再合并。
   - 确定性风险较高，需要逐字节对比成品。
4. **trace_scene 的字符串键**：改成整数元组键。master 仍然需要这份数据，所以不能直接关掉。
5. **字形上传时 `vkDeviceWaitIdle`**：改用已有的上传环（`VulkanRender.cpp:1134-1152`）。
6. **CPU 路线（非 Vulkan 档位、回退、分析探针）完全同步**：staging、fence 做成环，写管道交给独立线程。5090 的主渲染不走这条路，但分析探针和校验道都走，而且 CPU 编码的机器全部走这条路。

**先确认 118 帧/秒卡在哪**：
- `frames.jsonl` 里每帧都有 `step_ms`；开 `gpu_timing` 后还有 `cpu_scene_ms`、`cpu_script_ms`、`cpu_pending_wait_ms`、`cpu_resources_ms`、`cpu_prepare_ms`、`cpu_render_wait_ms`（`main.cpp:723-734`）。
- 注意：`gpu_timing` 会关掉跨帧重叠（`VulkanRender.cpp:626`），所以测到的是串行口径。
- 判断方法：`cpu_resources_ms` 大（粒子、视频、字形），就按第 1–3 条做；`cpu_render_wait_ms` 大，才是真正的 GPU 瓶颈，这时再用 `WPE_PASS_TIMING` 看逐 pass 的耗时。

**和"任何作品 ≤ 3 分钟"的关系**：
- 循环上限默认 600 s，60 fps 下是 36,000 帧。要在 180 s 内渲完，扣掉固定开销后需要**约 220 帧/秒以上**。compatibility 档的上限是 1200 s，要 440 帧/秒以上。
- 118 帧/秒的作品如果循环接近上限，会超出底线：600 s ÷ 118 ≈ 305 s，只算渲染就已经超了。
- 所以第 1 条（帧重叠）和残差组分段（4.6）是满足这条底线的**必要条件**。仅靠削减固定开销，只能改善短循环。

---

## 6. 优先级清单（按性价比排序）

"预计节省"一列里，带出处的是按已知数据推算的，其余都是估计，请以实测为准。

| 优先级 | 改哪里 | 预计节省 | 改动量 | 风险 |
|---|---|---|---|---|
| **P0-1** | 给分析加 `analysis_timing`（按 A1–A15 以及路径 (a)–(j) 记次数和耗时） | 本身不提速，但决定下面各项的实际排序 | 小（约 100 行） | 极低 |
| **P0-2** | 修复分析缓存目录嵌套（`HybridScenePlanner.cs:187-188` 配合 `:351`） | 每次"留非循环层实时再查"少一次观测渲染，缓存重新生效；2955378002 这 11 次递归各省一整次冷分析 | 极小（几行） | 极低（属于正确性修复） |
| **P0-3** | 去掉 `SizeEstimate` 对主渲染的门控（`GroupRenderScheduler.cs:284`）；循环 ≤20 s 时直接跳过体积外推 | 2984118135 约 −5 s / 36 s；所有作品都去掉"校验道 → 主渲染"这条串行依赖 | 小 | 低（超限有重渲兜底，被拒时产物本来就丢弃） |
| **P0-4** | 渲染器着色器缓存改成跨作业共享（`main.cpp:234`），另加 `VkPipelineCache` | 每次启动 −0.5–2 s；短循环一次烘焙 −3–10 s；分析阶段的探针也受益 | 小 | 低（按内容寻址，写入改原子） |
| **P1-1** | 分析进程内记忆化 M0–M3（源哈希只算一次、scene 母本、观测 trace、分配之前的阶段、ScriptTime） | 退回和外层循环里每次子分析约 −50–70%；**2955378002 从 1165 s 降到约 300–500 s** | 中（约 300–500 行，要把一个 memo 对象一路传下去） | 中（要保证 DeepClone 的纪律：共享的母本不能被改写） |
| **P1-2** | O7 静态组证明和 A9 循环报告的键收窄，接到 M6 上 | 每次 `TryAsync` 少 G 次 `LoopAnalysis` | 小到中 | 低 |
| **P1-3** | `RetreatAsync` 轮内并行（2.5 第 1 步） | 退回段墙钟 ÷ min(G, 8)；对 187、274 次子分析这类作品，**总耗时约 ÷3–5** | 中（要处理并发下的计数器、进度、探针去重） | 中（结果按组序比较，保持逐字节一致） |
| **P1-4** | 源树哈希、渲染器 exe 哈希在单次烘焙内记忆（`NativeRenderRunner.cs:196-198`，`Raw.cs:38`/`:86`，`CandidateValidation`） | 每次烘焙 −1–5 s（视频贴图大的作品更多） | 小 | 低（防篡改检查改为比较 (长度, mtime)，在烘焙结束时再完整哈希一次） |
| **P1-5** | 渲染器：粒子模拟和视频解码挪到 `finishPendingFrame` 之前 | 118 帧/秒这类作品 ×1.3–1.8（估）；是长循环满足 ≤3 分钟的前提之一 | 中（C++，调整调用顺序） | 中（要验证确定性，逐字节对比成品） |
| **P2-1** | source_capture、参照解包并行加硬链接 | 每次烘焙 −1–2 s | 小 | 低 |
| **P2-2** | 起点搜索：避开步长为 1 的陷阱（粗搜加细搜），候选窗口缩短 | 3436033033 可能 −10 s 以上（如果 stride=1 这条成立）；芙莉莲约 −5 s | 小到中 | 中（选出的相位可能和现在不同，残差会跟着变） |
| **P2-3** | 残差组分段渲染（`Segments.cs:24`） | 芙莉莲 master_render −15–25 s | 中 | 中（要处理首尾留存帧的衔接和拼接） |
| **P2-4** | 退回"先估再验"或"一次留多个"（2.5 第 2、3 步） | 退回段的子分析次数 ÷2–5 | 中 | 中高（选出的方案可能和现在不同，要在 501 张作品上对比） |
| **P2-5** | 慢分量预检：两次渲染合成一次、各组并行（`SlowClosureProbe.cs:51-52`）；失败的前缀探针在进程内缓存 | 每轮预检 −1 次渲染器启动 × 组数 | 小 | 低 |
| **P2-6** | 短循环直接拿正式成品做合成比较，不再单独烘探针（4.2） | 短循环 −4–6 s；**是短循环达到超实时的关键一项** | 中 | 中（校验被拒时正式成品白渲了；闸门时序会变） |
| **P2-7** | 画质门解码改 hwaccel，或者在引擎里对留存帧算 SSIM；硬解实测跨烘焙缓存 | 每组 −1–4 s | 小到中 | 低到中 |
| **P3-1** | 渲染器帧双/三缓冲；读回走环形缓冲，管道写入交给独立线程 | 直编模式最多 ×2；CPU 路线 ×1.5–2.5 | 大 | 中高 |
| **P3-2** | 粒子多线程；视频预取线程加去掉 sws | 粒子、视频为主的作品明显提速 | 大 | 高（确定性） |
| **P3-3** | ScriptTime、求解器的算法级优化（例如求解器按分量周期的 LCM 结构跳着扫，不再逐帧线性扫） | 做完 M3/M5 之后收益有限 | 中 | 中 |

**建议的落地顺序**：
1. **先做 P0 四项**：都很小，而且能拿到数据。
2. **再做 P1-1 到 P1-3**：解决分析 p99 的问题。估计 7 张超过 60 s 的作品能降到原来的 1/3–1/5；最慢的那张从约 20 分钟降到约 2–5 分钟。
3. **P1-4、P2-1、P2-6、P0-4 合起来**解决短循环的固定开销：预计 4–14 s 的循环能从不到 1 倍提到 2–4 倍（估）。
4. **P1-5 和 P2-3** 解决长循环、低帧率作品"≤3 分钟"的底线。

---

## 7. 复测建议（维护者本机）

1. **分析**：
   - 对 2955378002、3798688689，统计 `run-*/` 下各类目录的数量：数字编号的、`retreat-*`、`static-live-*`、`slow-live-*`、`plain-groups-live`、`single-shot-live`，以及各自 `loop-allocation-analysis` 的数量。确认 187、274 次各自落在哪条路径上。
   - 用 dotnet-trace 或 `dotnet-counters` 采一次 CPU 火焰图，确认热点在 A2（哈希）、A9b（ScriptTime）、A9d（求解器）、O7 之中的哪一个。
2. **烘焙**：
   - 对 2984118135 开 `gpu_timing` 跑一次，看 `frames.jsonl` 的 `cpu_resources_ms` 和 `cpu_render_wait_ms` 各占多少。
   - 看 3436033033 的起点搜索用的 stride 是多少。
   - 记下每个作业 `shader-cache` 目录的大小和冷编译耗时，从 `native_result` 的启动阶段读。
3. **每改一项**，都在 FINAL-A 的 30 张上对比成品的 SHA-256。成品应当逐字节一致；预期会变的项（P2-2、P2-4）除外。
