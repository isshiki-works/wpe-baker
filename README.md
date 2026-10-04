# WPE Baker 2.0

English ｜ [简体中文](README.zh-CN.md)

**Deterministic animation baking for Wallpaper Engine.** The first product built on Periodica.

WPE Baker models a scene's animation — shader time, animation tracks, particle cycles and video timebases — and retunes periodic motion within a visible-change budget until it closes into a loop. One offline generation pass pre-renders the deterministic parts as video or static caches; the result can contain multiple videos and textures. On repeat playback, the precomputed animation is decoded while retained layers such as mouse interaction, audio response, clocks and day/night effects keep running according to the selected mode. No model calls or Python are needed at runtime.

Website: **https://isshiki-works.github.io/wpe-baker/** · Download: **[2.0.0-rc.1](https://github.com/isshiki-works/wpe-baker/releases/tag/v2.0.0-rc.1)** · 中文：[README.zh-CN.md](README.zh-CN.md)

2.0 replaces the old library of period equations with shader time analysis. It applies abstract interpretation to the compiled SPIR-V, following time through the data flow to derive cycles, continuous drift and animation that settles after a while. New effects no longer need a hand-written equation of their own; unfamiliar custom shaders can be analyzed too.

Technical details and project notes: [technical notes](https://github.com/isshiki-works/wpe-baker/blob/main/docs/technical-notes.md).

## Quick start

1. Download `WpeBaker-2.0.0-rc.1-win-x64.zip` from [2.0.0-rc.1](https://github.com/isshiki-works/wpe-baker/releases/tag/v2.0.0-rc.1) and unzip the **whole** archive into a folder you can write to (not `C:\Program Files`). No installer.
2. Run `WpeBaker\WpeBaker.exe`. If Windows SmartScreen appears, choose More info → Run anyway; the build is not code-signed.
3. Drag a Scene wallpaper folder from `steamapps\workshop\content\431960\` into the window and click Analyze.
4. Read the verdict and click Start generating. The output appears in Wallpaper Engine's own list.

Two controls:

- **Animation precision**: efficiency (5 %), balanced (3 %, default) or quality (smallest change that closes). If a stricter level does not close, it steps down one level and the verdict names the one used.
- **Interaction**: keep, fixed view (default) or off; decides what mouse- and audio-driven effects do. Clocks, dates and media text stay live in every mode.

Command line:

```
wpe-baker.exe analyze <scene.pkg> --out plan.json
wpe-baker.exe bake plan.json --out <output-dir>
```

To compare playback cost after generation, use `wpe-baker.exe compare <original-folder> --baked <output-folder> --wallpaper-engine <wallpaper64.exe> --out <new-report-folder>`. It temporarily switches the wallpaper through A/B/B/A playback and reopens the previous assignment, resuming playback by default. Add `--restore-playback paused` to return it paused. Reopening restarts playback position; saved configuration cannot reveal the prior pause state. This requires one connected display and a single wallpaper assignment. The report separates iGPU and CPU-package power; opposing changes are shown as a tradeoff. Missing evidence gives no recommendation, and visual correctness is checked separately.

## How it works

- **Measure** — optionally plays the original in the official player first and reads the GPU power counters.
- **Solve** — each periodic component is solved for its own period, then retuned within the preset's budget (efficiency 5 %, balanced 3 %, quality: smallest change that closes) into a loop for its cache group.
- **Bake** — deterministic parts are pre-rendered as video or static caches. Encoded loop edges and a bounded sample of the assembled scene are checked before delivery. Retained real-time layers continue to use live input according to the selected interaction mode.
- **Input stays input** — mouse parallax, audio-reactive effects, clocks and interactive panels are treated as input: kept live, fixed, or left out, and always listed before generation.

## Feedback

Issues are welcome in English or Chinese. Attach `plan.json` and `bake.json` from the output folder, your GPU and the Workshop id; that is usually enough to find the cause.

## Requirements

Windows 10/11 64-bit, Wallpaper Engine, and a Vulkan-capable GPU for offline rendering. During playback, Wallpaper Engine decodes the baked videos and renders the retained real-time layers.

## Building from source

`WpeBaker-2.0.0-rc.1-source.zip` on the release page contains the corresponding source, third-party notices and build records. See `REBUILD.md` in that archive. Older saved plans should be re-analyzed with 2.0.

## Licenses

The MIT license in `LICENSE` covers the C# application/tool layer and the developer scripts. `engine/` (the offline renderer, derived from open-wallpaper-engine) and its modifications keep the upstream GPL-2.0; see `engine/LICENSE`. FFmpeg, x264 and other dependencies keep their own licenses.

Third-party components and license texts: `THIRD-PARTY-NOTICES.md` and `licenses/` in the source archive. Baked outputs are for personal use on your own machine; wallpaper artwork remains the property of its Steam Workshop authors. WPE Baker is not affiliated with or endorsed by Wallpaper Engine.

## Credits

- **hypengw** — vvk, wavsen and rstd, the libraries under the renderer.
- **isshiki** — direction, product decisions, hardware, testing.

Developed with AI coding agents: Claude Code (Anthropic) and OpenAI Codex.
