# WPE Baker — technical notes

English (this page) | [中文](technical-notes.zh-CN.md)

> Development notes with implementation details and per-title measurements. For usage, see the [README](../README.md).

Offline Scene-wallpaper baker for Wallpaper Engine on Windows.

## What

WPE Baker analyzes a Wallpaper Engine **Scene** wallpaper, works out which parts
of it are actually deterministic, and bakes those parts into a single opaque
video. Layers that genuinely need to run live — a clock, an audio-reactive
spectrum, mouse parallax — are kept live. The output is an independent WPE
project folder: no Python, no build toolchain, no cloud service required to
run it.

Both the WPF GUI and the command-line tool share the same pipeline: analyze
the Scene, generate the video, keep the necessary live layers, produce a
standalone WPE project.

Automatic layer allocation and per-output verification are supported locally,
but what the tool can handle is decided by scene structure, live dependencies
and the real loop. A single sample succeeding, a passing check, or a finished
package does not mean any given wallpaper will save power.

**The tool tells you whether a wallpaper is worth baking before it renders
anything.** About half of all Scene wallpapers are worth the attempt: across
the 113 titles in the local library, each measured in its original form on a
laptop iGPU at 60 fps, 61 (54%) draw 3 W or more on the iGPU rail, and 17 of
those can be baked into a plain full-frame video. The other end of the range
is just as real: 31 titles draw under 1 W, and baking one of those costs
disk space and bake time and gives nothing back (see **Heavy scenes, light
scenes**). Where the platform exposes power counters, the tool plays the
original in the official player, measures it, says what it read, and tells
you outright when the original is too cheap to be worth baking.

## Why

Some Scene wallpapers are heavy: they re-render every frame from shaders,
particle systems, and animation tracks even though most of that content
repeats exactly on a fixed cycle. That is continuous GPU work for a
wallpaper sitting behind your desktop icons. WPE Baker targets exactly that
case — **heavy** Scene wallpapers, with real rendering load *and* a lot of
precomputable content.

**Where it works and where it does not.** This is built for scene wallpapers
that are mostly a picture plus periodic animation. Interaction-heavy scenes —
eye tracking, day/night switching, parallax, customization panels — get no
saving in this release, and the tool says so before rendering anything: among
the 30 most-subscribed titles in the local library, none of the 7 measured
outputs saved power. Audio-reactive layers stay live and do not block
generation.

That target is deliberately narrow, but it is not a rare one, and
measurement is what defined it. Every wallpaper for which this tool has
produced a measured saving so far is a heavy one: re-baked at the laptop's
own screen resolution, Atri, Yuri, Ayanami Rei and Ultraman Leo give back
49-96% of the iGPU-rail power and 17-60% of package power, depending on
title and frame rate (**About power** has the per-title table). Sweeping the
originals of all 113 titles in the local library puts 61 of them (54%) in
that heavy band at 3 W or more, against 21 titles between 1 and 3 W and 31
under 1 W. Wallpapers that are already efficient, or that are dominated by
live interaction, are meant to be skipped rather than forced through the
pipeline — and the tool now says so before a bake rather than leaving you to
find out after one.

**"Worth baking" is two questions, not one.** First the original's own draw:
3 W or more on the iGPU rail at 60 fps, or there is nothing to save. Then the
route: the measured savings all come from baking the **whole layer stack into
one full-frame video** — that is what the 70-96% iGPU-rail reductions above
are. The **effect-prefix** route, where only the front of a layer's effect
chain becomes a video and the layer itself keeps running live, only pays when
that prefix is most of the work (Ultraman Leo, -73%, is such a case); across
the five heavy effect-prefix titles measured this round the iGPU rail moved
between +0.3% and -15%, which is **not a saving**. The tool therefore attaches
"this route saves little unless the baked-away part is the bulk of the work"
to an effect-prefix verdict, and only promises power savings when both
conditions hold.

## How it works

- **Loop period is solved, never guessed.** The period comes only from the
  shader period formulas, animation tracks, and the video's exact timebase.
  An adjustment of up to 2% is allowed to align an irrational period to a
  whole frame count, so the cut is hard and lands on a frame. WPE Baker does not infer a
  loop point by comparing rendered frames for visual similarity, and it does
  not search for a "close enough" starting point or splice together a
  finite segment as a stand-in for a real period. If the period can't be
  solved, the tool says so — and says which kind of dead end it hit, in
  `loop.no_candidate_reason`.
- **Presets — efficiency, balanced, quality, compatibility — budget how much
  the motion may change; the loop length is what the solver returns.** A
  scene usually has many periods that close, and the shorter the period, the
  more the tempo of its components has to be adjusted. `--preset
  efficiency|balanced|quality|compatibility` (balanced by default;
  `preset_applied` in the plan names the preset used) sets that budget:
  efficiency allows up to 5% on any retimable component, balanced 3% and
  compatibility 10%; quality sets no visual budget and falls back to the
  general retime limit. `--retime-budget` (0–5%) overrides the preset's
  budget. All four presets cap the loop at 600 s, and that cap is a backstop
  rather than the knob. Within the cap and the budget the solver picks
  candidates with the preset's preference (performance for efficiency and
  compatibility, balanced for balanced, quality for quality); if no common
  loop fits the budget the scene takes the unresolved path. Analysis does not
  shorten the loop for the 2 GiB embedded-video limit; the bake judges size
  from this scene's trial encode and the bytes actually written. Where the
  retime lands: a shader component changes its pass's time scale, a knob
  that only this term uses (a uniform or a literal), or a vertex output
  component — when one scroll speed feeds both x and y with different
  periods, the capture override shader reruns the vertex program on each
  component's own time and keeps only that component, so the two axes are
  retimed separately; an animation track changes its rate or fps, and a
  video its playback rate. A retime changes a component's frequency, never
  its motion path. `retime_profile` in the plan records the preset, budget,
  cap and where each came from; changing preset means analysing again, not
  editing an existing plan.
- **A failed seam falls back to the next candidate, never to a looser
  threshold.** If the selected candidate fails the encoded seam check, the
  bake promotes the next analytic candidate and re-runs, up to three
  candidates in total. Every attempt and its seam readings are written into
  `bake.json`; if all of them fail, the candidate is rejected and the
  reason lists each attempt.
- **A seam that matches the source's own cut is accepted as faithful.**
  Some source clips simply do not close their own loop. When the period
  comes from a single video track that owns the whole uncropped canvas, a
  failed seam is compared against the source clip's own wrap: every flagged
  tile must stay within the source's own residual (×1.15 + 1/255), no
  unflagged tile may reach the absolute tile limit, and the whole-frame
  tile map must correlate with the source's at r ≥ 0.95. Shader- and
  animation-driven periods never qualify — their seams have to close on
  their own. Nothing is repaired or spliced; the tool only stops counting
  the author's own cut as a defect it introduced.
- **A scene that would bake into a copy of itself is refused by default.**
  Six structural tests decide whether the output would be the same machine
  as the original — one video decode plus one full-screen blit — and if all
  six hold, the plan carries a blocker instead of a green light. See
  **What's new in RC8** for the tests and the `--video-shell allow`
  override.
- **A full-frame opaque video is the default target.** The analyzer walks
  the real layer hierarchy, draw order, and script/shader inputs to decide
  what has to stay live. Parallax effects keep their mouse dependency even
  at zero layer depth. Audio-reactive particles and high-confidence
  single-shot animation tracks are kept live too. WPE Baker will not
  silently change scene properties, turn off parallax, or fall back to a
  smaller transparent/cropped video to make numbers look better — that
  regression was tried once and made real-world power worse, not better.
- **Occlusion conflicts are surfaced, not resolved silently.** If baking
  would change what occludes what, the tool lists the conflict. You can
  explicitly choose to move a standalone live text/particle layer to the
  foreground, or simplify a fixed live text effect — both are off by
  default and the affected objects are listed before you opt in. The one
  automatic remedy is the trailing-group demotion described below, and it
  is recorded field by field.
- **When full-frame is blocked, the report lists what you could turn off.**
  A layer is kept live because something outside the loop feeds it: the
  mouse, the audio stream, the wall clock, a media player, the previous
  frame. Analysis sorts those into what you can trade away (pointer effects,
  audio-reactive layers, parallax, clock and date, one-shot intros, media
  info, built-in BGM) and what you cannot (the subject itself, camera and
  script failures), then lists the smallest set of trades that turns this
  wallpaper into one full-frame video, with the cost of each item spelled
  out. On the 113-title sample, allowing those trades moves the full-frame
  count from 39 to 68 **in analysis**; 29 of the 54 candidate titles need only
  elements a user can reasonably give up, 8 of them a single item. **That 68
  is an analysis verdict only: 23 of those titles were then baked for real and
  none of them produced an output** — the bake stage's loop allocation turns
  particle systems and the main animation layers back to live, so full-frame
  no longer holds. **The tradeoff feature is therefore experimental, and the
  bake stage has the final say.** **Full-frame is not
  the same as nothing live**: following the minimum list, exactly one of the
  29 ended up with no live layer, 12 kept 1-3 and 5 kept more than 10, so the
  list states what stays live as well as what goes. Parallax is kept by
  default, and excluding a parent takes its children with it — turning off
  parallax on 3659877246 takes the clock, date and weekday with it — so the
  list is built from the plan's actual exclusion set and names what you
  actually lose. Nothing is turned off unless you ask for it.
- **Live layers are protected structurally.** When the period can't be
  fully solved, WPE Baker can retry with a smaller baking allocation: the
  whole author subtree containing the unresolved mechanism or particle
  system is kept live, then cost and composition are re-checked. If nothing
  is left worth baking, it stops rather than force a partial result.
  Analyze now runs that retry itself and records it in
  `loop_allocation_fallback`, handing you a `--retain-live <ids>` command
  instead of changing your allocation for you.
- **Whether to bake is your call, not a gatekeeper's.** There is no
  draw-time admission threshold: analysis does not run a cost probe and
  nothing is refused for being "not worth it." The allocation rule is
  deterministic — one opaque full-frame group if the scene can form one,
  otherwise whatever `--live-overlays foreground` / `--video-layout
  layered` you chose, and a "needs your decision" report listing the
  conflicting live layers if you chose neither. The video-shell test is the
  only default refusal, and it judges structure, not estimated savings.
  `wpe-baker cost-probe` stays as an optional diagnostic that measures but
  never decides. Real savings have to be compared on your own playback
  device.
- **The original's own draw is measured before you bake.** Where the platform
  exposes RAPL energy counters, `measure-official` plays the original in the
  official Wallpaper Engine player, waits for it to settle, samples for
  30-45 s, restores the wallpaper you had, and reports the iGPU-rail and
  package watts it read. If the original draws almost nothing on the iGPU
  rail, the verdict says baking will not win that back, and leaves the choice
  with you. The absolute verdict needs a real graphics domain counter (Intel
  PP1 / GFX / GT): a machine that publishes only package and core domains —
  the AMD desktop this is developed on is one — reports
  `unsupported_platform` and no verdict rather than a fabricated number. On a
  laptop with an Intel iGPU the same reading matched a manual counter read to
  0.36%. This is a measurement of the machine you run it on, not a promise
  about yours.
- **A few unresolved layers can be masked, under two recorded tests.** When
  a scene solves to a period but a handful of components stay unresolved,
  they may be smeared across a fixed 0.4-second whole-frame crossfade at
  the seam — but only if each one is *proven* non-periodic or random (a
  drifting shader, a sprite restarted after a `Math.random()` delay, a
  particle system with random emitters) and bounded in amplitude. "We
  could not recognise this mechanism" never qualifies. The period itself
  never moves; only the start phase is chosen. Both the hard-cut residual
  and the post-crossfade seam step are written into `plan.json` and
  `bake.json` with their limits. Every video group also gets a
  `seam-preview.mp4` in its directory once its seam check finishes (passed,
  rejected or matching the source cut): the last N loop frames then the
  first N (N = the crossfade window, 24 at 60 fps), once at normal speed and
  once at 0.25x, with loop frame numbers on a strip that flips from red to
  green at the seam. **This path has been
  validated on exactly one wallpaper so far** — three other structurally
  suitable candidates stop at layout admission before the residual code
  runs at all, so there is no second data point yet.
- **Hardware encoding is attempted only through a vendor-neutral channel,
  and on this build it still lands on software.** `--encoder
  software|mf|auto` (software by default) affects only the playback encode;
  the lossless master is always software-encoded. The bundled GPL ffmpeg is
  built with an encoder whitelist of `libx264 / libx264rgb / libx265 /
  h264_mf / hevc_mf / av1_mf / aac / pcm_* / rawvideo` — **there is no nvenc,
  qsv or amf in it**, so asking for a vendor encoder falls back to software
  and writes the reason into `bake.json`. The vendor-neutral channel is
  Windows Media Foundation (`h264_mf` / `hevc_mf`), chosen because the target
  playback device is an integrated GPU. On our machines it still ends on
  Microsoft's software MFT: upstream ffmpeg binds the D3D manager only when
  it encodes the first D3D11 frame, while the vendor MFTs require it earlier,
  at `SetOutputType`, so the hardware MFT is enumerated and then refuses with
  `MF_E_UNSUPPORTED_D3D_TYPE`. A full-feature ffmpeg fails identically, so
  this is not a missing build option. `bake.json` records `mf_hardware:
  false` with the reason rather than calling it hardware encoding. D3D12
  Video Encode (`h264_d3d12va` / `hevc_d3d12va`) does encode on an AMD iGPU
  in our tests; it is the next step, not a shipped feature.
- **Every bake records where its time went.** `bake.json` carries a
  top-level `stage_timing` whose mutually exclusive stages — capture,
  composition validation, start search, master render, seam check,
  crossfade, playback encode, encode quality check, hardware decode check,
  project assembly — sum to the total. Stages that did not happen are
  `null`, not zero.
- **The native renderer is a Vulkan port of open-wallpaper-engine**, patched
  for Windows; offline decode/render uses software decode and readback for
  measurement purposes, which is not the same performance profile as the
  official WPE player.

## What changed in 1.0.1 and 1.0.2 (2026-09-18)

- **Two independent controls replace the single preset.** Animation
  precision (efficiency 5% / balanced 3% / quality: smallest change) only
  bounds retiming; balanced is the default, quality can be selected
  explicitly and is capped at 600 s, and the verdict names the budget
  applied (`preset_applied`). Interaction (keep / fixed view / off)
  only decides what input-driven content does; it defaults to fixed view, and
  off additionally drops pointer effects and sampled, costly full-screen
  audio effects. Clocks, dates, media text, FPS counters and background music
  always stay live.
- **No silent fallback.** Settings that do not close are reported as such.
  Only when another pair of settings has actually been solved does the plan
  carry `suggested_change` (one sentence in both languages, the settings,
  `verified`, the plan path); the GUI applies it with one click.
  Sub-allocations verified inside the same analysis are adopted directly
  (Alone bakes as 4 groups at the defaults).
- **At most four video groups in a layered layout** (2–4 groups save; 6
  groups reversed at +106% on the iGPU rail, 9 at +10.9% on package). Live
  widgets are hoisted to the foreground by default (`--live-overlays
  foreground`).
- **Analysis cache:** re-analyzing the same output directory with different
  settings reuses scene loading and period solving (3426865175 at 4K: 4.98 s
  → 0.95 s).
- **Pre-bake power measurement of the original is off by default**
  (`--measure-source`; an optional checkbox in the GUI, about 60 s).
- **GUI:** four fixed verdict texts; Technical details collapses to a table
  of numbers; a "Not included in this bake" card lists only what was
  actually left out; edits in Advanced show as "Custom". The 1.0
  Compatibility mode (`--keep-live on`) has been removed; plans from older
  versions are not read and must be analyzed again.
- **1.0.2: analysis and bake share one admission check.** The
  loop-allocation verdict (particle stationarity, residual masking) now runs
  during analysis, so subtrees that must stay live are decided before the
  report says Ready to generate. A bake that fails is reported as failed;
  nothing is re-rendered automatically, and Retry stays available.
  Candidates are ordered shortest first. Amiya (3486806915) at 1080p,
  balanced: 8 min 44 s on an RTX 5090, seams passed.
- 1,437 automated checks.

## What changed after RC8

RC8 (below) was the last packaged build; this release adds the following,
each described in the section named after it:

- **Presets instead of loop preferences.** `--preset` budgets how much
  the motion may change, and the loop length comes out of the solve (**How it
  works**).
- **The frame rate is derived, not fixed at 120** — the lower of your
  Wallpaper Engine setting and your screen's refresh rate (**Quick start**,
  and the measurements behind it in **Heavy scenes, light scenes**).
- **The original's power draw is measured before a bake**, where the platform
  can measure it at all (**How it works**).
- **A trade-off list** when full-frame is blocked by live elements you may be
  willing to give up (**How it works**).
- **Encoder options narrowed to a vendor-neutral channel**; the vendor
  encoder names are gone because the bundled ffmpeg never had them (**How it
  works**).
- **The hardware-decode check now names the machine it ran on**, and warns
  about dimensions many integrated decoders refuse (**Scope & limits**).
- **Bakes are faster** on the same inputs, with byte-identical outputs
  (**How long a bake takes**).

## What's new in RC8 (2026-09-17)

Nine branches merged into the mainline at once (`fix/silent-rejections`,
`feat/single-shot-live`, `feat/layout-demotion`, `feat/no-candidate-reason`,
`feat/shader-verdict-dedup`, `feat/video-shell`, `feat/messages-i18n`,
`feat/video-control-scope`, `feat/sdr-closure`); the test suite went from 402
to **557** checks and the C++ native renderer was not touched. Every item
below is a verdict or an exit — no seam or period threshold was loosened, and
nothing is special-cased for an individual wallpaper.

- **A video shell is refused by default.** Six purely structural tests, all
  of which must hold: the route is whole-layer replacement; the periodic
  components are non-empty and every one of them starts with `video/`; there
  is no unresolved temporal mechanism; the candidate needs no retime; that
  video is carried by a single opaque, full-canvas, centered layer; and no
  baked layer has any effect pass. When they do, the output is the same
  machine as the original — one video decode plus one full-screen blit — so
  the plan records `video_dominant.status=video_shell`, appends a blocker and
  sets `status` to `requires_resolution`, and the bake refuses at its entry
  check. To bake it anyway, pass `--video-shell allow` to **analyze** (the
  `bake` subcommand has no such switch); the plan then records
  `override_accepted` with byte-identical `evidence`. Any missing piece of
  evidence yields `not_video_shell` — the conservative direction is fixed.
  There is no power number anywhere in this rule: it judges structure, not
  estimated savings.
- **Trailing-group demotion for full-frame admission.** When full-frame is
  refused because there is more than one video group, the only automatic
  remedy allowed is to return *every video root after the first opaque base
  group* to live, in place, with its parent transform, draw order and
  parallax depth intact. Three conditions must all hold, or the original
  refusal stands (there is no partial demotion): exactly one group remains,
  it carries the scene clear and is not transparent, and dependency closure
  pulls in no extra root; the sum of the demoted visible drawable layers'
  `canvas_fraction` is smaller than the largest one kept in the remaining
  group (like against like, no magic constant); and the demoted visible live
  roots are strictly increasing in `source_allocation_order`. The loop is
  re-solved when it applies, and the plan records
  `layout_admission_demotion` plus
  `video_layout_admission.status = planned_layout_allowed_after_demotion`.
  When it does not apply, the conflict text still hands you a runnable
  `--retain-live <ids>`.
- **Single-shot animation tracks are kept live.** A runtime track with
  `looping=false`, `playback_mode=single`, `confidence=high` and a mechanism
  other than video marks its owner layer live (reason
  `single_shot_animation`): a looping video would replay a once-only
  animation every period, while the original never jumps again after its
  duration. Lower confidence decides nothing and keeps the existing
  `loop.unresolved` path.
- **"Full-frame is unreachable" is stated as such.** When the only video
  group does not carry the scene clear, something visible draws in front of
  it, and those layers cannot be promoted to the foreground, the layout
  verdict is `full_frame_unreachable` and names each blocking layer and why
  it must stay live — instead of suggesting a re-analysis that cannot help
  in that structure. With no video group at all the verdict is
  `not_applicable_no_video_group`, and no useless layout advice is appended.
- **No candidate now carries a structured reason.** `loop.no_candidate_reason`
  is non-null only when the candidate list is empty, and gives the kind
  (fixed period beyond the search ceiling / no whole frame on the frame grid
  satisfies every component / no modelled temporal mechanism at all / no
  exact frame for a video retime), the ceiling in seconds, the fixed period,
  and three counts — shader components, runtime animation periods, runtime
  clock uniforms — so you can check for yourself whether nothing moves or
  the analysis simply did not understand it. A new top-level `suitability`
  gives a `verdict` of `suitable` / `unsupported_capture` / `not_suitable`
  (`not_suitable` is split into "cannot" with a proof and "not converged";
  nothing is left for the user to judge) with the rule name and a reason in
  both languages; capture gaps and layout conflicts only go into `notes` and
  never override the main verdict.
- **One shader on one layer is ruled on once.** A (layer, shader) pair that
  the equation rules already decided is no longer reported a second time as
  an unresolved runtime material. The old test was a role whitelist that
  only blocked `effect`, so an effect instantiated at runtime as both
  `source` and `effect` reported an already-solved shader twice — the false
  positive on Ayaka 3288381550. The mechanism knowledge residual masking
  needs also moved out of prose into the structured fields
  `bounded_displacement` and `mechanism`.
- **Script video control is scoped to its targets.** The old test was "any
  script anywhere in the scene calls a video control API, therefore every
  video track is controlled" — guilt by association across the whole scene.
  Now four parallel tests, any one of which means controlled: a
  non-initialization runtime write of a video property on that owner; a
  script containing video control calls reading that layer's `videoTexture`
  at runtime; that layer's own script binding containing a control call; or,
  only when the target cannot be resolved, the old scene-wide fallback. The
  plan records `video_control_scope` with `resolved_targets`,
  `unknown_scope_owners` and `scene_wide_fallback`. Separately, a particle
  definition whose emitter/initializer/operator has
  `audioprocessingmode ≠ 0` is kept live — audio is a live external input —
  and non-periodic particle reasons are now named (turbulent noise field,
  randomframe, random initializer, emitter extent, audio input, unreadable
  definition, or simply not modelled) in `particle_nonperiodic_reason`.
- **HDR is decided by a per-layer SDR radiance closure proof.** The
  `general.hdr` flag alone is no longer a refusal. Every visible drawing
  layer in a captured group must pass five tests: material closure (shader
  within the known flat / genericimage family / solidlayer set, no author
  effects, `VERSION` the only allowed combo), blend closure (pass blending
  and `colorBlendMode` within known-safe values), input closure (each
  texture's `.tex` header must be an 8-bit unsigned format, and a video
  package additionally needs an 8-bit decoder `pixel_format`), scalar
  closure (color/brightness/alpha and the captured `clearcolor` must be
  literals inside [0,1]; a script or animation binding counts as unknown),
  and no feedback path (nothing in the group may read an `_rt_` render
  target). All five must hold for `hdr_radiance_closure.status=closed`;
  **anything unknown fails**, and the blocker keeps its original sentence
  with an `Unproven:` list naming the group, the layer and the rule. The
  conservative direction is fixed on "unknown means no": animation-bound
  scalars are not guessed from keyframes, and only the one documented 8-bit
  container value is accepted.
- **No more zero-reason exits.** If `whole_layer.status=unavailable` while
  both `whole_layer.blockers` and `loop.unresolved` are empty, that is now
  an internal error, not a silent success (the effect-prefix route with a
  usable cache is exempt). A negative `source_static` verdict must name the
  layer and the mechanism. Analyze also runs the loop-allocation fallback
  the README has always promised and records it, handing you
  `--retain-live <ids>` when it works and the reason when it does not.
- **Entry classification and subcommand help.** A preset package (a
  `project.json` with `dependency` but no `file` and no `type`) no longer
  shares a sentence with real video wallpapers: stderr reports
  `status=not_applicable`, `kind=preset` and the Workshop id it depends on,
  and it exits with code **3** (video wallpapers keep 1). `analyze --help` /
  `-h` now prints that subcommand's own usage and exits 0, instead of
  treating `--help` as a source path.
- **Bilingual messages and a one-line verdict.** 42 message keys, each with
  a Chinese and an English text. The plan carries `blockers_localized`,
  `loop.unresolved_localized` and a top-level `summary` (verdict `bakeable` /
  `blocked` / `not_applicable` / `unknown`, with one sentence in each
  language). The CLI takes `--lang zh|en` (guessed from the UI culture when
  omitted) and prints one line of verdict to stderr at the end of analyze,
  leaving stdout as pure JSON. The historical English in the `blockers`
  array and in `loop.unresolved[].detail` is **unchanged word for word**, so
  downstream scripts matching those strings still work. **The GUI itself is
  still English**: this round only made the blocker text the GUI reads
  bilingual.

## Results

### What actually bakes today

Four wallpapers reach `candidate_generated`, all produced by the RC6
package's own CLI. Three ran on an Intel Arc B390 laptop; Amiya ran on an
RTX 5090. The RC8 code re-ran analyze **and** bake for all four on the
5090: route, group count, per-group flags, candidate count, selected period
length, start frame and `loop_validation` match RC6 field for field
(`reports-20260916/regress-rc8-round2.md`). **Power was not re-measured** —
the figures below are still the 2026-09-17 measurements of the RC6 outputs.

| Wallpaper (Workshop ID) | Route | Output | Bake time | Power (ABBA, 60 fps) |
|---|---|---|---|---|
| Ayanami Rei (3258032485) | full-frame opaque | 1 video layer, 0 static layers, 3372 frames / 56.2 s at the balanced preset, 4.678% total retime, `encoded_seams_passed` | 91 s | graphics −94%, package −11% |
| DJGun (3754908581) | full-frame opaque (source period) | 1800 frames / exactly 30.000 s, zero retime, `encoded_seams_match_source_discontinuity`; **refused by default from RC8 on** as a video shell, needs `--video-shell allow` at analyze time | 170 s | graphics −2.5%, package −2.3% (no saving) |
| Amiya (3486806915) | full-frame opaque + residual masking | 1 group, 6000 frames, start phase 2848, 24-frame seam crossfade (24 frames re-encoded, 5976 stream-copied), `encoded_seams_passed` | 737 s | graphics −96%, package −29% |
| Ultraman Leo (3685247684) | effect prefix | 1 effect-prefix group (owner layer 17), 1371 frames / 22.85 s, 1.72% total retime, `encoded_seams_passed` | 57 s | graphics −84%, package −43% |

All four pass their seam check, but only three of them save power. DJGun's
source is already a hardware-decoded video layer (11.5% video decode, 15% 3D
engine in the A state), and the baked output draws the same power (0.48 →
0.47 W graphics at 60 fps; the −7.1% package figure at 120 fps sits inside
the 11.22 ↔ 12.75 W drift between the source's own two A states and is not a
saving). It is listed to show the tool does not invent savings on a scene
that is already cheap, not as a win. RC8 turns that observation into a
structural rule: DJGun is now refused by default as a **video shell** —
the original and the output are both one H.264 decode plus one full-screen
blit — and only an explicit override bakes it. Full numbers for both frame
rates are in **About power**.

### The fixed ten-title sample

Ten Workshop titles, picked at random and never touched during development.
**Exactly one produced a full-frame baked video, and only because its seam
matches the source's own cut** (DJGun, below) — and from RC8 on it also has
to get past you: it is refused by default as a video shell, and with
`--video-shell allow` the output matches RC6 field for field (1800 frames,
`encoded_seams_match_source_discontinuity`) while measuring no power saving
at all. Three route to the effect-prefix path instead (Sunset Cat
3373818743, Ultraman Leo 3685247684, Gold/鎏金 3663810817). One looks like a
residual-masking candidate on paper but never gets there: Hacker 3019043758,
whose only unresolved component is a static-noise hash with an exact solved
period of 9.699321 s, needs four opaque groups for full-frame admission;
trailing-group demotion correctly refuses it on the coverage test (the
demoted content sums to 1.110092 against 0.516142 for the largest layer
kept, because its full-canvas image is larger than the background video), so
the bake still refuses at its entry check and no residual numbers are ever
produced — RC8 at least writes the runnable `--retain-live 13,188,217` into
the message. One needs a rule that does not exist yet (Ayaka 3288381550,
whose runtime material reads `g_Time` directly; RC8 only removed the
duplicate verdict on the same shader). Noah 3723257973 now passes
trailing-group demotion — three groups become one and the layout blocker
disappears — and is then refused as a video shell, because its base group is
itself a 60 fps video layer and baking it is close to a re-encode. Requiem
3715649803 (no input-independent visual group after dependency closure),
Evelyn 3676910198 and Daisies 3696323523 (both blocked on layout, Daisies
with no analytic candidate at all) **were not re-run this round** and keep
their RC6 verdicts.

DJGun 3754908581 is worth stating precisely, and its period reading has not
changed. **Its period is correct**: the wallpaper's video track is 900
frames at 30 fps, exactly 30.000 seconds, so 1800 frames at 60 fps with zero
retime. The seam check originally failed it because it read a 60 fps
capture's duplicated frames as ordinary motion; once the ordinary step is
measured across one *content* frame, the flagged tiles drop from 56 to 6 and
the velocity outlier goes to zero. Those last 6 tiles are the source video's
own cut between its frame 899 and its frame 0 — the baked seam and the
source's own seam match tile for tile (Pearson r = 0.9937). The bake
reproduces the author's cut faithfully rather than papering over it. Both
the cadence fix and the rule that accepts a seam matching the original's own
cut have been in since RC6, so DJGun is `candidate_generated` with
`loop_validation = encoded_seams_match_source_discontinuity`. Nothing is
repaired or spliced — a source-period encoding is still never patched up;
the tool just stops counting the author's own cut as a baking defect of its
own.

### A wider sample of heavy scenes

Twelve more heavy Scene wallpapers, analyzed (not baked). Under RC6, four
came out with zero blockers, zero unresolved components and
`planned_layout_allowed` (3655445220, 3570308528, 3600630828, 3691946745);
all four are a scene shell wrapped around one video file, where the period
*is* the video length and the retime cost is 0%. **Under RC8 all four are
refused as video shells** — they are precisely the shape the rule is for
(3655445220 first passes trailing-group demotion from two groups to one, and
is then stopped by the shell test). Two more (3470764447, 3326873240, the
two Elaina titles) looked like residual-masking candidates — a single proven
non-periodic post-process layer each — but were refused at the full-frame
layout gate before any residual measurement happened; under RC8 their
single-shot animation tracks are kept live, the video group count drops from
one to zero, the blocker becomes exactly one sentence ("no input-independent
visual group remains after dependency closure") and `suitability.verdict`
becomes `not_suitable` — an honest "there is nothing to pre-render here"
instead of a layout stall. Of the remaining six, blocked under RC6 on HDR
intermediate composition, perspective capture, or having no solvable period
structure: Minecraft Lo-Fi 3147346398 now passes the per-layer SDR radiance
closure proof and, with its single-shot layer kept live, is down to one
group — `status` moves from `requires_resolution` to
`requires_loop_analysis`, the blockers clear, and `suitability` reads
`suitable` (1 candidate / 1440 frames, unchanged from RC6; this batch runs
at what was then the CLI default of 120 fps; the default is now derived from
your Wallpaper Engine setting and screen refresh rate). Saturn 3589454154 and Live Solar System
3662790108 fail the closure proof (`open`), so the blocker stays and simply
names the layers and rules that stopped it.

### The compatibility sample (RC8 regression, analyze only)

The RC8 regression re-analyzed 51 keys against the RC6 baseline: **448 field
differences, every one of them attributable**, the 25 keys that had
candidates keep the same candidate count and selected period length case by
case, and **no case went from having candidates to having none**. Three
cases became *more* bakeable, each with explicit evidence rather than a
loosened threshold:

| Wallpaper | Change | Mechanism |
|---|---|---|
| 3605722997 (魔王-追悼) | 0 candidates → 1 candidate, 16016 frames / 133.47 s, 0% retime | script video control scoped to its resolved targets, so this track is no longer treated as playback-controlled |
| 3147346398 (Minecraft Lo-Fi) | `requires_resolution` → `requires_loop_analysis`, HDR blocker cleared | per-layer SDR radiance closure proof, plus the single-shot layer kept live leaving one group |
| 3796263268 | 0 candidates → one 1-frame static candidate | corrected static-evidence handling for effect materials, official assets and mipmapped textures |

Others only got clearer: 3588579284 becomes a single group after
trailing-group demotion and its layout blocker clears, though it still has 0
candidates; 2955378002 drops from five groups to one and from 24 unresolved
items to 1, still with 0 candidates (its fixed period exceeds the 180-second
search ceiling); 3287715210 and 3796219398 fail the HDR closure proof and
name the layers that stopped it; 3641690548 is classified as a preset
package and exits 3 instead of 1. **All of that is analyze output — none of
these cases has been baked and measured.** The outputs are still the same
four wallpapers, three of which save power.

### Heavy scenes, light scenes

All 113 titles in the local library were measured in their original form —
played in the official Wallpaper Engine player at 60 fps on the laptop's Arc
B390, iGPU rail and package rail, 113 of 113 runs valid (2026-09-18).
**61 of them (54%) draw 3 W or more on the iGPU rail**, which is the
population this tool exists for:

| Original draw at 60 fps | Titles | Full-frame | Keep layers live | Layered | Cannot be baked |
|---|---:|---:|---:|---:|---:|
| Heavy, ≥3 W | 61 | 17 | 18 | 8 | 18 |
| Medium, 1-3 W | 21 | 10 | — | — | — |
| Light, <1 W | 31 | 12 | — | — | — |

Only the full-frame count was broken out for the medium and light bands;
the route split above is complete for the heavy band (17 + 18 + 8 + 18 =
61).

The three heaviest titles draw 26.4, 25.0 and 22.8 W, and all three sit on
the layered or keep-live routes rather than full-frame; the heaviest title
that bakes to a plain full-frame video draws 14.2 W.

An earlier seven-title random draw from the same library was baked and
measured as well (2026-09-18, 7 titles x 60/120 fps, all 14 runs valid), and
none of those outputs saved a meaningful amount of power — but that sample
held exactly one heavy title, 14% against the library's 54%, so it says
nothing about the overall rate. What it does establish is two things about
frame rate and light wallpapers:

| Frame rate | Outcome across the 7 titles |
|---|---|
| 60 fps | 5 inside measurement noise, 1 more expensive than the original, 1 (the heavy one, kept partly live) iGPU −3.7% with package +12% |
| 120 fps | 4 more expensive (package +11% to +26%, one of them +38% on graphics), 3 flat or inside noise |

First, **120 fps at native resolution is not a free choice**: decoding a
120 fps video costs an integrated GPU more than drawing a light wallpaper
does, so the frame rate defaults to the lower of your Wallpaper Engine
frame-rate setting and your screen's refresh rate instead of a fixed 120.
Second, **"it can be baked" is not "it is worth baking"**: six of those
seven originals drew 0.09-1.1 W on the iGPU rail at 60 fps, so there was
nothing there to save, and the one heavy title (11.6 W) could only be baked
by keeping layers live, which took 3.7% off the iGPU rail while package
power went up 12%. That is why the original's own draw is measured first,
and why keeping layers live is offered as a trade with its measured cost
attached rather than as a silent fallback.

Full-library totals, re-run on 2026-09-18 on this machine's RTX 5090 with the
code published here (per-title table in
`reports-20260916/full113-rc11-bake.md`):

- **Analysis calls 84 of 113 titles bakeable (74%)**: 39 full-frame, 27
  retain-live, 18 layered — six of them only come back when the plan's own
  source-root suggestion is used for the re-run.
- **All 78 queued titles finished baking, and 48 of them produced an output
  with a video loop (62%, about 1.9 GB in total)** — 42% of the whole library
  (48/113). Six of those came from a second pass: the run script had taken 12
  one-frame retain-live results for finished bakes, and re-running those 12
  with `--video-layout layered` turned 6 of them into video loops
  (`reports-20260916/full113-rc11-layered12.md`). The six titles that only
  analyze as bakeable with the plan's source-root suggestion were baked as
  well and none produced a loop (5 unsolvable, 1 exception); they are not part
  of the 78.
- A further **11 titles produced a still picture (one frame, no video file)**.
  Those are counted separately on purpose: such a result is a static image
  plus every live element, and it is not a power saving (3715870843, for
  example: analysis found a 480-frame loop, the bake judged that group
  motionless and wrote one frame). From this release on such a result is
  recorded as `static_only` instead of sharing `candidate_generated` with
  outputs that carry a video loop.
- 19 titles failed: 12 with no solvable loop, 2 exceptions, 1 timeout (layered
  multi-group rendering, serialized), 1 seam rejection, 2 composition
  rejections, 1 hardware-decode rejection.
- **The renderer shipped here does not change the outputs**: across the 16
  comparable titles every output SHA256 matches the older renderer's, none
  differ.
- One bake slot took 9,665 s of wall clock for those 78 titles (about 1,070 s
  of it waiting on a lock held by another session), 0.0162 s of CLI time per
  frame.

**Bakeable does not mean cheaper.** 21 baked outputs have been measured so
far (60 fps, ABBA runs in the official player; per-title figures in
`reports-20260916/abba-heavy-rc11.md` and `abba-wholelayer-rc11.md`).
**7 of them met the bar — 30% or more off the iGPU rail with no rise in
package power — one in three.** Counting only originals that draw 3 W or
more, it is 5 of 19. By route: full-frame whole-layer outputs 4 of 5;
effect-prefix outputs 3 of 10 (Atri -90%, Ultraman Leo -73%, Lost Landscape
-57%), a route that only pays when the part baked away is the bulk of the
work; retain-live and layered outputs 2 of 8, and **5 of those 8 cost more
than the original** (package +8% to +23%): their 3D load barely moved and a
video decode was added on top. Nine video groups (3669680904) was the worst
case at +10.9% on package.

Trimmed mean over the 7 saving outputs (best and worst dropped): **-76.5% on
the iGPU rail and -29.9% on package at 60 fps**; at 120 fps only 4 outputs
exist, -85.3% on the iGPU rail. Four of the seven were re-baked at screen
resolution and three at 1080p, so the pool mixes two resolutions. Package
readings from the second and third rounds are usable (run-to-run spread is
far below the differences); the first heavy round's package figures are not,
and are not quoted anywhere.

Two things have to be read alongside that:

- **The two effect-prefix titles that dropped at 60 fps both reverse at
  120 fps** (-57% → +5%, -33% → +34%). The output was baked at 60 fps, so when
  the playback frame rate doubles, the baked side rises faster than the
  original does. Take the frame rate from your Wallpaper Engine setting:
  **do not bake at 60 and play at 120.**
- **Do not expect retain-live or layered outputs to save power in this
  release.** Whether an output saves depends on how much of the original's
  drawing work moves into video, not on the route name; when most layers stay
  live, the bake adds a decoder and removes little. Since 1.0.1 the default path allows at most four video groups in a
  layered layout, and when your settings fail it only reports a change that
  has been solved (2–4
  groups saved; 6 groups already reversed at +106% on the iGPU rail, 9 groups
  at +10.9% on package).

The table below lists the measured outputs that can be quoted today.

### How long a bake takes

Measured on the development desktop (Ryzen 7 9800X3D, RTX 5090), one
bake at a time, with outputs byte-identical to the slower path:

| Case | Before | Now |
|---|---|---|
| 25,599 frames, 1080p60, full-frame opaque | 2566 s | 214 s |
| 671 frames, full-frame opaque | 108 s | 15 s |
| 671 frames, same case on an AMD iGPU (2 CU) | 38 s | 22.5 s |

These are development machines, not a promise for yours: the 2566 s baseline
was measured with two bakes running in parallel, the 214 s and 15 s figures
with the 5090 to itself, and the last row is the same small case on that
desktop's integrated GPU. A bake is CPU-bound rather than GPU-bound — during
one 238 s case the 5090 sat at 1% utilization — so a faster graphics card
buys less than the numbers suggest.

### About power

All four outputs have been measured with an ABBA comparison in the official
Wallpaper Engine player on the playback laptop, at 60 and 120 fps (2026-09-17;
each state settles for 20 seconds and is then averaged over 45 seconds; all 32
states returned 45/45 valid samples, with no failed state):

| Wallpaper | Frame rate | Graphics power | Package power |
|---|---|---|---|
| Ultraman Leo (3685247684) | 60 fps | 9.13 W → 1.47 W (−83.9%) | 20.65 W → 11.75 W (−43.1%) |
| Ayanami Rei (3258032485) | 60 fps | 2.52 W → 0.14 W (−94.3%) | 10.44 W → 9.33 W (−10.7%) |
| Amiya (3486806915) | 60 fps | 3.71 W → 0.13 W (−96.4%) | 14.13 W → 10.02 W (−29.1%) |
| DJGun (3754908581) | 60 fps | 0.48 W → 0.47 W (−2.5%) | 10.13 W → 9.90 W (−2.3%) |
| Ultraman Leo (3685247684) | 120 fps | 17.24 W → 3.41 W (−80.2%) | 29.45 W → 14.25 W (−51.6%) |
| Ayanami Rei (3258032485) | 120 fps | 7.95 W → 0.27 W (−96.6%) | 17.90 W → 9.71 W (−45.8%) |
| Amiya (3486806915) | 120 fps | 9.43 W → 0.27 W (−97.2%) | 22.28 W → 10.18 W (−54.3%) |
| DJGun (3754908581) | 120 fps | 0.88 W → 0.89 W (+0.9%) | 11.99 W → 11.13 W (−7.1%) |

Three of the four save power. **DJGun does not**: its source is already a
hardware-decoded video layer, and the baked output draws the same power. It
is listed to show the tool does not invent savings on a scene that is already
cheap, not as a win.

Those numbers are 1080p output. Six titles were re-baked at the laptop's own
screen resolution (3072x1920, so playback does not upscale the video) and
measured again on 2026-09-17. Four save power, DJGun still does not, and
Amiya's re-bake was rejected by its seam check at the new resolution and
could not be measured:

| Wallpaper | Frame rate | Graphics power | Package power | Per hour |
|---|---|---|---|---|
| Atri (3669681034) | 60 fps | 8.45 W → 0.81 W (−90.4%) | 20.22 W → 10.69 W (−47.1%) | 9.52 Wh |
| Atri (3669681034) | 120 fps | 16.25 W → 1.51 W (−90.7%) | 30.34 W → 12.18 W (−59.9%) | 18.16 Wh |
| Yuri (3572877776) | 60 fps | 2.38 W → 0.71 W (−70.3%) | 13.49 W → 11.18 W (−17.1%) | 2.31 Wh |
| Yuri (3572877776) | 120 fps | 6.70 W → 1.35 W (−79.9%) | 19.70 W → 12.27 W (−37.7%) | 7.43 Wh |
| Ayanami Rei (3258032485) | 60 fps | 2.29 W → 0.19 W (−91.8%) | 12.44 W → 10.33 W (−17.0%) | not significant |
| Ayanami Rei (3258032485) | 120 fps | 7.77 W → 0.31 W (−96.0%) | 19.59 W → 10.86 W (−44.6%) | 8.73 Wh |
| Ultraman Leo (3685247684) | 60 fps | 9.13 W → 2.46 W (−73.0%) | 21.03 W → 12.90 W (−38.7%) | 8.13 Wh |
| Ultraman Leo (3685247684) | 120 fps | 17.29 W → 8.80 W (−49.1%) | 30.10 W → 20.63 W (−31.4%) | 9.46 Wh |
| DJGun (3754908581) | 60 fps | 0.51 W → 0.50 W (−2.6%) | 10.45 W → 10.55 W (+1.0%) | none |

At screen resolution the output costs more to decode (video decode engine
load rises from 6.6-9% at 1080p to 22.6-26%), so these savings are smaller
than the 1080p figures above; Ultraman Leo at 120 fps loses the most, from
−51.6% to −31.4% on package. Sharpness comes back in exchange (Laplacian
variance of the wallpaper area, output over original at 60 fps, rises from
0.085 to 0.58 on Atri and from 0.50 to 0.99 on Ultraman Leo). Ayanami Rei's
60 fps package figure is not significant: the original's own two A states
differ by 2.46 W. Ultraman Leo and Atri are effect-prefix outputs whose
dimensions follow the source image, so they land 2 px under the computed
size.

At 120 fps the source wallpapers roughly double their draw while the baked
outputs barely move (Amiya: 0.13 W → 0.27 W graphics), so package savings
widen from 11–43% at 60 fps to 46–54% at 120 fps. The 60 fps package figure
for Ayanami Rei is the smallest (−10.7%) because the baked video path
costs CPU-domain power (3.2 W → 5.1 W) that offsets part of the graphics
saving; at 120 fps that cost stops growing and the package figure reaches
−45.8%.

In battery terms on this laptop (package delta multiplied by one hour,
against a 92.04 Wh full charge), which is a direct conversion of a power
difference and not a measured runtime:

| Wallpaper | 60 fps | 120 fps |
|---|---|---|
| Ultraman Leo (3685247684) | 8.90 Wh saved per hour, 9.7% of the battery per hour | 15.20 Wh/h, 16.5%/h |
| Ayanami Rei (3258032485) | 1.12 Wh/h, 1.2%/h | 8.19 Wh/h, 8.9%/h |
| Amiya (3486806915) | 4.11 Wh/h, 4.5%/h | 12.10 Wh/h, 13.2%/h |

**DJGun is not listed**: its measured package delta (0.23 W at 60 fps,
0.85 W at 120 fps) falls inside its own source's A-state drift, so it does
not count as a saving.

Wallpaper Engine pauses wallpapers on battery by default
(`playbackonbattery=pause`), so this difference only occurs if you turn that
off. No eight-hour or all-day figure is extrapolated from it.

These are **RAPL package/graphics power on the playback laptop (Intel Arc
B390), official Wallpaper Engine ABBA at 60 and 120 fps, not whole-system
wall power** and not per-process power. 90 fps was not tested. Readings are
never frame-by-frame certified and never a guarantee for other wallpapers or
other hardware — compare on your own machine.

Older figures exist, but they were taken on earlier builds with different
solved periods, so they are not results for this release: a manual ABBA run
on Amiya (2026-07-26) showing 5.45 W → 0.83 W graphics and 35.4 W → 28.3 W
package, and IGCL device-power readings from 2026-09-14 (Sunset Cat −24.8%,
Ultraman Leo −76.1%, Gold −20.3%). That Ultraman Leo number belongs to a
~83.8s period solve which RC6 replaced with 22.85s; the −83.9% in the
table above is the measurement for this release, and −76.1% is no longer
quoted anywhere.

## Scope & limits

- 1.0 targets **heavy** Scene wallpapers: meaningful rendering load *and* a
  lot of precomputable content. Wallpapers that are already efficient, or
  that are dominated by live interaction, are skipped by design — that is
  expected behavior, not a failure. Sweeping the originals of all 113 titles
  in the local library puts 61 (54%) in the heavy band at 3 W or more, 21
  between 1 and 3 W and 31 under 1 W, so the narrow target is not a rare
  case (see **Heavy scenes, light scenes**).
- Across that same 113-title library, analysis calls 84 titles (74%) bakeable
  and the full run bakes **48 of the 78 queued titles into an output with a
  video loop (62%, 42% of the library)**; another 11 end up as a still
  picture with no video, which is not a saving, and 19 fail outright. The
  breakdown is under **Results**.
- The release branch and commit, the `Baker.Core.Tests` count and the sha256
  and byte size of both zips are listed in `build-records\` inside the
  package and in the checksum list on the release page. The presets, the
  pre-bake power measurement, the automatic frame rate, the trade-off list
  and the encoder changes described above are on the integration branch for
  this release; the RC8 package dated 2026-09-17 predates them, so the
  package published here has to be built from the branch that carries them.
- Two dependency-license items and two measurement gaps are open; they are
  listed under **Known issues** below.
- Analysis and baking are exercised on two GPUs: an Intel Arc B390
  integrated GPU and an RTX 5090. Analysis results match field for
  field across both. Power has only ever been measured on the Arc B390, but
  on all four outputs, at 60 and 120 fps: three of them save power and DJGun
  does not — see **About power**. Other GPU
  vendors and a clean Windows install are **not yet verified**.
- On the fixed ten-title sample, exactly one title produced a full-frame
  baked video, it passes only under the source-cut rule, and RC8 refuses it
  by default as a video shell: three route to effect prefix, one is an
  unbaked residual-masking candidate, one needs a rule that does not exist
  yet, and four produce nothing on layout or "nothing worth baking". The
  full-frame path is evidenced by other titles — see **Results**.
- Everything in **The compatibility sample** and **A wider sample** is
  analyze output only. Nothing there has been baked, and no power figure
  exists for any of it.
- Local seam repair has been removed (the `--local-seam-repair` switch is
  gone too); a source-period encoding is never patched up after the fact. If
  the seam does not pass, the candidate is rejected. The one narrow
  exception is a seam that matches the source clip's own cut tile for tile
  (see **How it works**): that is accepted as a faithful reproduction, not
  repaired.
- Residual masking — the path that lets a few proven non-periodic layers be
  smeared across a 0.4-second crossfade — is **validated on one wallpaper**.
  Three other structurally suitable candidates never reached it, because
  full-frame layout admission stopped them first.
- Output uses BT.709 SDR H.264/HEVC, chosen by size and frame rate; every
  full video gets a short hardware-decode check **on the display GPU(s) of
  the machine that baked it** (the render GPU when none drives a display), and the result now says so: `verified_on` lists each adapter with its
  vendor and whether it is integrated or discrete, and `target_caveat` states
  that the check covers this baking machine only. If you bake on a discrete
  GPU and play on an integrated one, copy the output over and run
  `wpe-baker decode-check` there. A vendor-neutral warning is added when the
  stream is wider or taller than 4096 (H.264), or beyond 8192 x 4320 or
  33,177,600 luma samples (HEVC), where many integrated decoders stop. The
  check is not a substitute for sustained official playback or a power
  comparison. AV1 has an independent decode check only and is not used for
  generation. Perspective
  capture, some script/puppet-driven layers, and irregular animation may
  still be rejected or have compatibility gaps. HDR is no longer refused on
  the flag alone, but a scene that cannot prove per-layer SDR radiance
  closure is still refused, with the failing layer and rule named.
- The interface language is switchable in the top-right corner and follows
  the system language by default; this release does not remember a manual
  switch. `--lang zh|en` only controls the CLI verdict line.
- A short local test build isn't proof of a general result: single-sample
  success, a passing check, or a completed package do not by themselves
  show that a given wallpaper saves power on your machine.

## Known issues

- **No performance data for read-back frame scanning without AVX2**: scanning
  was only measured on a 9800X3D (AVX2 / AVX-512). CPUs without AVX2 take the
  scalar fallback path, which is covered by unit tests (row widths under 8
  pixels already use it) but has **never been timed**; it may be markedly
  slower than the numbers quoted here.
- **Retain-live and layered outputs are usually more expensive than the
  original** (5 of 8 measured, package +8% to +23%): the live layers keep
  their cost and a video decode is added. This release does not refuse such
  plans; the next one adds a video-group limit and a fixed-view suggestion.
- **Experimental: day/night state splitting** (`--daytime-split on`, CLI
  only, off by default). A scene whose clock script only toggles layer
  visibility is split into one state per period and each state is analyzed
  and baked on its own, with the original script kept. Recognised on two of
  the three titles tried; the remaining live layers still decide whether a
  state becomes a full-frame video. With the switch off, the plan is
  byte-identical to the previous build.
- **A tradeoff plan can still fail at the bake stage**: the tradeoff list is
  an analysis verdict, and the bake stage's first step — loop allocation —
  decides again. When a particle system or a main animation layer has no
  period, it is turned back to live, the single opaque video group full-frame
  needs no longer holds, and the bake is refused. All 23 re-baked titles ended
  this way, with zero outputs. The list is a direction worth trying, not a
  promise that it will bake.
- **`--exclude-layers` is silently ignored for layers that must stay live**:
  naming a layer that has to run live (a particle system, a main animation
  with no period) in `--exclude-layers` leaves it out of the exclusion set
  echoed in the plan. The CLI neither errors nor warns, and the refusal reason
  does not change. A later release will turn this into an error.
- **The hardware-decode check only covers the baking machine**: the short
  decode check for each finished video is run on whichever machine baked it.
  `verified_on` lists each adapter's vendor and integrated/discrete class, and
  `target_caveat` states that the verdict covers the baking machine only. If
  you bake on a discrete GPU and play back on an integrated one, copy the
  output to the target machine and run `wpe-baker decode-check` there. The
  short check is not a substitute for sustained playback in the official host
  or for end-to-end power verification.

## Next steps (none of this is in this release)

- **A pipelined renderer.** `engine` branch `perf/render-pipeline`, commit
  `5326907` (committed before it was built, verified-source-binding), produces
  `wpe-render-pipe.exe`, SHA256
  `20C9B94945CB6E2A37A1F7EC4DA5D79A96D078C62D26EC0224E14C0EA80F3774`. Checked
  so far: three titles at 300 frames each and eight full outputs hash
  identically to the mt-r4b renderer shipped here, and offline-vulkan passes at
  every in-flight setting on both the RTX 5090 and the integrated GPU. It still
  needs a full-title bake sign-off, so it is **not part of this package**. The
  fallback, if it misbehaves, is `WPE_RENDER_FRAMES_IN_FLIGHT=1`.
- **Multi-process parallelism is worth measuring again.** On the pipelined
  renderer, five processes in parallel are no longer serialized (2.5× per
  process, about a third of the wall clock), so the next step is to re-measure
  the baker's existing `--group-parallel` with it (a 3184-frame layered title
  should go from 115 s to 70-80 s). **Single-process multi-group rendering is
  not the plan**: a minimal experiment ruled it out — five groups in one
  process is about the same as five processes run serially, because the
  bottleneck is the renderer's serialized record/submit/readback on its main
  thread, not GPU scheduling.
- **In-GPU encoding** (Vulkan Video Encode) exists only as a design.

Sources: `reports-20260916/perf-multi-group-renderer.md`,
`reports-20260916/perf-renderer-pipeline.md`.

## Quick start

Before the first run: the package is self-contained, so you do **not** need
to install .NET, Python, a build environment or FFmpeg, and nothing is
downloaded at run time. Extract the **whole** archive into a folder you can
write to — the program reads `renderer\`, `encoder\` and `tools.json` next to
the executable — and do not run it from inside your archive viewer.
`WpeBaker.exe` is the graphical interface; `wpe-baker.exe` beside it is the
command line and will only flash past if you double-click it. The first
launch shows the blue "Windows protected your PC" dialog because this build
is not code-signed: choose "More info" and then "Run anyway", or tick Unblock
in the downloaded archive's properties before extracting it.

1. Launch `WpeBaker.exe`. It looks for a currently-running WPE process first
   to locate your install (assets and `projects/myprojects` are filled in
   automatically); if nothing is running, it falls back to your Steam
   install info. On multi-monitor setups you can pick which screen's Scene
   to load, or click "re-detect."
2. Drag a Scene folder, JSON, or PKG into the window (or use the file/folder
   picker). Video wallpapers, web wallpapers, preset packages and other
   non-Scene projects are refused at the entry check — this tool only handles
   Scene wallpapers. The CLI reports `status=rejected_unsupported_source` with
   `reason` = `video` / `web` / `preset` / `other_type`, `message` and
   `message_localized`, and exits with code 3; a preset package also carries
   the Workshop id it depends on in `dependency`: bake that dependency instead.
   The window shows the same sentence in the verdict panel.
   If the native renderer cannot read one of the wallpaper's asset files (a
   3D model or a texture it does not parse yet), analysis cannot start and
   the CLI exits with code **4**: `--out` still receives a report whose
   `summary.verdict` is `tool_limitation`, naming each unreadable file, the
   field and file offset where parsing stopped, and the layer that uses it.
   That is a gap in the tool, not a verdict on the wallpaper; `bake` refuses
   the report. Exit codes in full: 0 plan written, 1 not a Scene wallpaper or
   analysis failed, 3 preset package, 4 tool limitation, 130 cancelled.
3. Set the two controls — Animation precision (efficiency / balanced /
   quality, balanced by default) and Interaction (keep / fixed view / off,
   fixed view by default) — and click Analyze. Precision only bounds
   retiming; the verdict names the budget applied. Interaction only decides
   what input-driven content does; clocks, dates and media text always stay
   live. Resolution and frame rate are worked out for you and shown before
   the run: **the frame rate is the lower of your Wallpaper Engine frame-rate
   setting and your screen's refresh rate**, replacing the old fixed default
   of 120, because at native resolution a 120 fps video costs an integrated
   GPU more than a light wallpaper does. Both stay editable, and the plan
   records for every value whether it came from the preset, from
   auto-detection or from your override. Properties start from the values you
   set for this wallpaper in Wallpaper Engine (or the wallpaper's own
   defaults when those cannot be read) — see **Wallpaper properties** below.
4. Read the report: it shows the chosen route, the video groups, the layer
   list with each layer's allocation, placement and visibility binding, the
   layout verdict, the one-line `summary`, and every unresolved mechanism
   together with the reason it could not be given a period. It also says
   whether this wallpaper is worth baking — on a platform with readable power
   counters, from a measurement of the original itself; elsewhere, by stating
   that this machine cannot measure it. Nothing is refused for being "not
   worth it" — the tool reports what it found and leaves the call to you.
   Use `--lang zh|en` to choose the language of that verdict.
5. If there's an occlusion conflict, either resolve it explicitly (move a
   layer to foreground, simplify an effect, or copy the `--retain-live`
   command from the message) or adjust scene properties and re-analyze. The "Not included in this bake" list names every input-driven element
   the chosen Interaction setting left out. If your settings do not close but
   another pair of Animation precision / Interaction settings has been solved
   in the same run, the plan carries `suggested_change` and the GUI offers it
   as a one-click re-analysis. Nothing is applied without that click.
6. If the plan says the scene is a video shell, baking it would produce the
   same machine you already have. Re-run analyze with `--video-shell allow`
   if you want it anyway — for a fixed-length loop or a repack, not for
   power.
7. Add to the queue and bake. A successful bake writes a clean, independent
   project (containing `project.json` and `bake.json`) into your chosen
   folder, recognized by WPE's local project library by default. A failed
   or canceled bake does not touch the library, and existing projects are
   never overwritten.
8. Verify visually in the official WPE player. The built-in check report
   can be reviewed directly; "measure current playback" only reads the
   currently running WPE process — it does not switch wallpapers or launch
   a test window for you. Frame-timing capture needs administrator rights
   on Windows.

Command line:

```
wpe-baker --help
wpe-baker analyze --help
wpe-baker analyze SOURCE --out PLAN.json
wpe-baker analyze SOURCE --preset quality --out PLAN.json
wpe-baker analyze SOURCE --fps 60 --fps-den 1 --lang zh --out PLAN.json
wpe-baker analyze SOURCE --video-shell allow --out PLAN.json
wpe-baker analyze SOURCE --exclude-layers 224,269,2242 --out PLAN.json
wpe-baker bake PLAN.json --out NEW_DIRECTORY
wpe-baker measure-official REQUEST.json
```

Analyze options that change what goes into the plan:

- `--preset efficiency|balanced|quality|compatibility` — how much the motion
  may change (5% / 3% / no visual budget / 10%), balanced by default; every
  preset caps the loop at 600 s, and `preset_applied` in the plan names the
  preset used. Advanced overrides: `--retime-budget
  PERCENT` (0..5) and `--loop-max-seconds`. The older `--loop-preference`,
  `--max-retime`, `--loop-length-max` and `--view-mode` names have been removed.
- `--interaction keep|fixed|off` — what input-driven content does: keep it
  live, fix the view (default), or also drop pointer effects and sampled,
  costly full-screen audio effects. Clocks, dates and media text stay live in
  every mode.
- `--measure-source on|off` — measure the original's power in the official
  player before analysis; off by default.
- `--fps N --fps-den D` — overrides the automatic frame rate; the plan marks
  the value as an override instead of auto-detected.

- `--properties-source wpe|defaults` — where property values start from:
  `wpe` (the default) reads what you set for this wallpaper in Wallpaper
  Engine, `defaults` uses the defaults in its `project.json` (see
  **Wallpaper properties**).
- `--properties PROPS.json` — a JSON object of property name → value applied
  on top of that starting point.
- `--exclude-layers ID,ID` — drops the listed layers and their subtrees as if
  they were switched off in the source: they enter neither the video, the
  live scene nor a static texture. Ids come from the plan's `layers` list; an
  id that is not in the scene is an error. Nothing is excluded unless you
  list it.

## Wallpaper properties

Optional elements a wallpaper ships with — sponsor codes, credit lines, clocks,
media info, rain on the lens and so on — are switched by that wallpaper's own
properties, exactly as in Wallpaper Engine. **By default the bake uses the
values you set for this wallpaper in Wallpaper Engine**, so the output looks
the way the wallpaper looks on your desktop. Wallpaper Engine keeps them per
wallpaper under `<user>.wproperties` in its `config.json`; WPE Baker only
reads that file and never writes it. It is looked for next to the running
`wallpaper64.exe`, then in the install folder found through the registry and
the Steam libraries, then at the Wallpaper Engine path in the GUI settings
(for the CLI, the folder above `--assets`). Each value is checked against the
property's type in `project.json` (switch, slider range, combo option, colour,
text); invalid values and Wallpaper Engine's own display settings (alignment,
colour correction) are ignored and listed in the plan.

Order: the wallpaper's defaults, then your Wallpaper Engine values, then
`--properties` (in the GUI, edits in the property panel; values that came from
Wallpaper Engine are marked "from WPE settings"). `--properties-source
defaults` skips the Wallpaper Engine values.

It falls back to the wallpaper's defaults, and says so in the plan and in the
one-line summary, when `config.json` cannot be found or read; when it holds
several users and not exactly one of them is the current Windows user or
Steam login; or when the wallpaper has different settings on several screens
and none of them is the one showing it. A wallpaper you never changed in
Wallpaper Engine keeps its defaults, and its plan is the same as before apart
from the two source fields.

The plan records `properties_source` (`wpe`, `defaults` or `wpe_unavailable`)
and `wpe_properties` (reason, which `config.json`, user, entry, screen, the
keys that took effect, those that differ from the default, those overridden
and the ignored values). The values actually used are in
`snapshot_properties` and carried into the bake. Checked on Amiya (3486806915)
with analyze only: the user had changed nine properties in Wallpaper Engine
(eight switches turned off, one slider); by default all nine went in and the
live set dropped from 21 to 14 layers, the same as passing those values with
`--properties`; with `--properties-source defaults` it is back to 21. Anything
drawn into the artwork itself cannot be switched off and stays in the output.

## Disclaimer & usage

- WPE Baker is an independent project. It is not affiliated with, endorsed by
  or sponsored by Wallpaper Engine or its developers (Wallpaper Engine Team /
  Kristjan Skutta). The name is used only to say what it works with.
- What it produces is a local conversion of wallpapers you have already
  subscribed to, for your own use on your own machine. Do not upload the
  output to the Steam Workshop or redistribute it anywhere else: the artwork,
  music and scripts inside still belong to their authors.
- The sample wallpapers in this README are real Steam Workshop items, cited by
  Workshop ID only to report test results
  (`https://steamcommunity.com/sharedfiles/filedetails/?id=<ID>`). Their
  artwork is copyright of their respective authors. No Workshop assets are
  included in this repository or in any package.

## Build from source

Regular users do not need this — it's for contributors building the native
renderer and managed tooling. The native renderer's source is `engine/`, a
GPL-2.0 fork of `waywallen/open-wallpaper-engine` based on upstream commit
`b866e8e711fdd7762385b23601affa1ea5539e3b`, with its full Git history; the
Windows changes are ordinary commits there. `build/native-mt22` holds the renderer shipped in
this release (the multithreaded-decode build); `build/native-speed22` is the
RC8 renderer, and `build/native-release22` is a 2026-09-09 leftover whose
provenance record points at a different build directory — left as the
default it is rejected, so both packaging commands need an explicit
`--native-build-dir build/native-mt22`.

```text
python scripts/fetch-dotnet.py
python scripts/build-managed.py src/Baker.Cli/Baker.Cli.csproj
python scripts/build-managed.py src/Baker.App/Baker.App.csproj
python scripts/build-native-cmake.py --target wpe-render --toolchain .tools/llvm-mingw-22 --build-dir build/native-speed22 --ffmpeg-root .deps/ffmpeg-lgpl21/prefix
python scripts/package-portable.py --native-build-dir build/native-speed22 --out dist/preview-LOCAL
python scripts/package-source.py --native-build-dir build/native-speed22 --output dist/WpeBaker-source-LOCAL.zip
```

Both packaging commands must use the same `--native-build-dir` — it has to
contain `bin/wpe-render.exe` and `provenance/build-wpe-render.json` from a
verified native build; the scripts refuse a combination with mismatched
renderer hash, source snapshot, or provenance.

Do not let Git rewrite the working tree before packaging. The source binding
checks every source file byte by byte, 6 of them tracked scripts
(`build-ffmpeg-lgpl21.py`, `build-native-cmake.py`,
`distribution-inputs.lock.json`, `fetch-ffmpeg-lgpl21-inputs.py`,
`native-inputs.lock.json`, `native-provenance.py`). On a machine with
`core.autocrlf=true` and no `.gitattributes` entry covering them, those files
are LF in the working tree (`native-inputs.lock.json` is even mixed) and would
come back CRLF if Git rewrote them, which breaks the binding silently. Move
between branches with `git fetch <bundle> <branch>:<branch>` plus
`git switch`, and keep `checkout -f`, `reset --hard`, `stash` and `clean -x`
away from `scripts/`; restore a single path rather than the tree.

Tests run with `dotnet test tests/Baker.Core.Tests -c Release` (xUnit;
`--filter "Layer!=L3"` skips the tests that need ffmpeg, the renderer, a GPU
or local fixtures; those read tool paths from `tools.json` or
`WPE_BAKER_TOOLS_JSON` and are skipped with a reason when missing). See `scripts/NATIVE-BUILD-STATE.md` and
`scripts/DISTRIBUTION-DEPENDENCIES.md` for native dependency versions and
full build notes. Synthetic test assets live in `tests/`; real Workshop
assets and generated output live in the git-ignored `artifacts/` folder and
are never part of the public source package.

## License

- **Tooling layer — MIT.** The C# GUI, CLI, core library and developer
  scripts; see `LICENSE`.
- **Native renderer — GPL v2.** `renderer/wpe-render.exe` is a Windows port of
  open-wallpaper-engine (upstream commit
  `b866e8e711fdd7762385b23601affa1ea5539e3b`); the license text ships as
  `licenses/open-wallpaper-engine.LICENSE`. Its FFmpeg decoder DLLs are built
  as LGPL 2.1 (notices in `licenses/renderer-codecs/`).
- **Bundled encoder — GPL v2.** `encoder/ffmpeg.exe`, `ffprobe.exe` and their
  DLLs are FFmpeg 8.1.2 built with x264 and x265; notices are in
  `encoder/licenses/`.
- **Corresponding source.** Every release publishes `WpeBaker-source.zip` next
  to the portable zip. It contains the modified renderer source (`engine/` in
  this repository, with its full history) and `scripts/dependency-patches/`,
  the renderer's dependencies,
  the complete FFmpeg, dav1d, x264 and x265 sources used for both FFmpeg
  builds, the build scripts and input locks, `REBUILD.md`, and the original
  license texts. `build-records/` in both archives name the same renderer
  SHA-256, so you can check that the source matches the binary. If you got a
  portable zip without its source zip, ask for it where you downloaded it.
- **vvk and wavsen: MIT OR Apache-2.0, confirmed by the author.** Both pinned
  revisions (vvk `f53d60c`, wavsen `77dfd33`) predate the commits that added
  their license files. On 2026-09-18 the author, hypengw, added MIT OR
  Apache-2.0 to vvk and confirmed it covers earlier commits
  (https://github.com/litocpp/vvk/issues/3), and confirmed the same terms for wavsen
  `77dfd33` (https://github.com/hypengw/wavsen/issues/5). The license texts ship in
  `licenses\`. Thanks to hypengw for the quick answer.
  Per-component versions, licenses, origins, modifications and patch locations
  are listed in `THIRD-PARTY-NOTICES.md`.
- Other bundled components (.NET runtime, LLVM-MinGW runtime DLLs, PresentMon)
  keep their own licenses, copied under `licenses/`.
