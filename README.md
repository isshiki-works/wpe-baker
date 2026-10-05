# WPE Baker 2.0

English ｜ [简体中文](README.zh-CN.md)

**Bake your animated wallpaper once. Play it with less GPU work.**

WPE Baker turns repeating animation in Wallpaper Engine scenes into seamless video loops. Your wallpaper keeps moving, while the GPU spends less time rendering the same effects over and over. Clocks, music response and other live content can stay in the scene.

It is the first application of Periodica, an engine that finds and coordinates animation periods. Everything runs locally, with no uploads or AI model calls.

Website: **https://isshiki-works.github.io/wpe-baker/** · Download: **[2.0.0-rc.1](https://github.com/isshiki-works/wpe-baker/releases/tag/v2.0.0-rc.1)** · 中文：[README.zh-CN.md](README.zh-CN.md)

## What's new in 2.0

- **Shader analysis replaces hand-written period equations.** Periodica follows time through compiled SPIR-V shaders to find loops, continuous motion and animations that settle. It also analyzes author scripts, so support can extend beyond a fixed catalog of effects.
- **Independent animations get their own loops.** Local speed adjustments coordinate motion without forcing the whole scene into one long cycle. Transparency, blending and HDR are preserved when the baked content is placed back into the scene.
- **Faster generation.** GPU resizing before readback reduces frame transfers, and hardware encoding is available on Intel, AMD and NVIDIA GPUs. On an Intel Arc B390, the full Atri example took about 4 min 55 s at 3072×1920 and 60 FPS.
- **A redesigned liquid glass interface.** Load the current wallpaper, adjust the output and manage jobs in one window. Scene previews are rendered directly from the wallpaper, instead of enlarging its thumbnail.

![WPE Baker 2.0 interface](https://isshiki-works.github.io/wpe-baker/site/gui-2.0-en.jpg)

Technical details and project notes: [technical notes](https://github.com/isshiki-works/wpe-baker/blob/main/docs/technical-notes.md).

## Quick start

1. Download `WpeBaker-2.0.0-rc.1-win-x64.zip` from [2.0.0-rc.1](https://github.com/isshiki-works/wpe-baker/releases/tag/v2.0.0-rc.1) and unzip the **whole** archive into a folder you can write to (not `C:\Program Files`). No installer.
2. Run `WpeBaker\WpeBaker.exe`.
3. Choose the current wallpaper, or drag in a Scene wallpaper folder or `scene.pkg`, then click **Analyze**.
4. Review the plan, choose your frame rate and resolution, and click **Start generating**.
5. Select the completed job to preview it in Wallpaper Engine, apply it to a screen, or export a ZIP.

The main choices:

- **Animation precision**: efficiency (5 %), balanced (3 %, default) or quality (smallest change that closes). If a stricter level does not close, it steps down one level and the verdict names the one used.
- **Interaction**: keep, fixed view (default) or off; decides what mouse- and audio-driven effects do. Clocks, dates and media text stay live in every mode.

Command line:

```
wpe-baker.exe analyze <scene.pkg> --out plan.json
wpe-baker.exe bake plan.json --out <output-dir>
```

To compare playback cost after generation, use `wpe-baker.exe compare <original-folder> --baked <output-folder> --wallpaper-engine <wallpaper64.exe> --out <new-report-folder>`. It temporarily switches the wallpaper through A/B/B/A playback and reopens the previous assignment, resuming playback by default. Add `--restore-playback paused` to return it paused. Reopening restarts playback position; saved configuration cannot reveal the prior pause state. This requires one connected display and a single wallpaper assignment. The report separates iGPU and CPU-package power; opposing changes are shown as a tradeoff. Missing evidence gives no recommendation, and visual correctness is checked separately.

## How it works

- **Analyze the motion.** Find the periods in shaders, animation tracks, particles and embedded videos, and identify content that needs live input.
- **Find a practical loop.** Adjust animation speeds within the chosen precision budget. Independent groups can repeat at different intervals.
- **Render once.** Save the repeating parts as video or static textures, then combine them with the live layers. Generation checks the loop seam, image quality, composition and hardware decoding.

You can also choose **Optimize live scene** to combine compatible effects while keeping the scene live, without generating video.

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
