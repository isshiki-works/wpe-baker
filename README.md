# WPE Baker 2.1

English ｜ [简体中文](README.zh-CN.md)

**Deterministic animation baking for Wallpaper Engine.** Built on Periodica.

Periodica analyzes animation in Wallpaper Engine scenes, coordinates its periods into seamless loops, and renders those loops as video. Clocks, music response and other live content can remain in the scene, reducing repeated rendering while preserving the animated wallpaper. Its first application is WPE Baker for Wallpaper Engine.

The output is a standalone Wallpaper Engine project. The video decoder plays the repeating animation while retained layers continue responding to live input. Analysis and generation run locally, with no wallpaper uploads or AI model calls.

[Website](https://isshiki-works.github.io/wallpaper-engine-baker/) · [Download 2.1.2](https://github.com/isshiki-works/wallpaper-engine-baker/releases/tag/v2.1.2)

## Measured

Measured in the official Wallpaper Engine player on an Intel Arc B390 laptop on AC power. Original and baked scenes alternate in A/B/B/A order, with 20 seconds sampled per segment. The table below uses Balanced precision and default wallpaper properties. Baked output is 1920×1080 at 60 FPS; originals render normally on the 3072×1920 display.

| Wallpaper | Original iGPU | Baked iGPU | Change | CPU package: original → baked |
|---|---:|---:|---:|---:|
| Nijika (3650475846) | 20.98 W | 14.74 W | −29.7% | 31.13 → 26.95 W |
| Ayanami Rei (3258032485) | 2.28 W | 0.13 W | −94.4% | 8.85 → 7.15 W |
| Atri (3669681034) | 8.39 W | 0.26 W | −96.9% | 16.11 → 7.94 W |
| Alone (3448877775) | 5.74 W | 4.90 W | −14.7% | 15.08 → 15.03 W |
| Ultraman Leo (3685247684) | 8.99 W | 1.43 W | −84.1% | 17.87 → 9.71 W |
| Yuri (3572877776) | 2.25 W | 0.45 W | −80.0% | 10.55 → 8.40 W |
| Frieren (3426865175) | 10.67 W | 7.79 W | −27.0% | 20.00 → 18.74 W |
| Lost Landscape 3 (3713073223) | 8.14 W | 3.48 W | −57.2% | 17.13 → 12.84 W |

Power savings also extend to high refresh rates. These tests use the same alternating playback method, with both original and baked output at the display's native 3072×1920 resolution:

| Wallpaper and frame rate | Original iGPU | Baked iGPU | Change | CPU package: original → baked |
|---|---:|---:|---:|---:|
| Atri · 60 FPS | 8.40 W | 0.26 W | −96.9% | 16.22 → 8.14 W |
| Atri · 165 FPS | 25.56 W | 2.24 W | −91.2% | 38.30 → 13.77 W |
| Ayanami Rei · 120 FPS | 8.62 W | 1.50 W | −82.6% | 18.00 → 11.56 W |

## 2.1 · More efficient rendering

2.1 reduces repeated work in the renderer: resource updates follow actual changes, shader analysis is reused, and drawing avoids repeating state that is already active. Author scripts run through an optimized runtime, while link-time optimization improves the engine as a whole. These changes apply to shared rendering paths, preserving author scripts, animation precision and the existing analysis and generation workflow.

The portable package includes the updated renderer and its runtime dependencies. The matching source archive includes the new allocator source and license, the runtime optimization profile and the exact native build records.

## 2.0 · Performance update

**From period equations to shader analysis.** 2.0 applies abstract interpretation to compiled SPIR-V, following time through the data flow to derive cycles, continuous drift and animation that settles after a while. New effects no longer need a hand-written equation of their own; unfamiliar custom shaders can be analyzed too. Author scripts are also included in time analysis.

**Independent motion, separate adjustments.** Waves, glows and scrolling within one effect no longer have to speed up together. Motions that can be controlled independently are adjusted separately, and independent video groups repeat at their own intervals. Transparency and blending order are preserved when baked content returns to the scene. HDR intermediate images are mapped into video and restored by a generated shader.

### Redesigning the rendering pipeline for offline generation

Building on open-wallpaper-engine, Periodica reworks scene execution and the Vulkan offline rendering pipeline. Shaders, scripts, particles and video advance in deterministic time steps, with simulation, drawing and sampling scheduled separately. Warm-up no longer reads back images that will be discarded, loop-start searches draw only the frames needed for comparison, and independent video groups are generated at their own periods.

In the previous baking pipeline, transparent frames passed through full-frame readback, CPU scanning, lossless intermediate video and a second encoding pass. Version 2.0 moves cropping, transparency processing and loop crossfades into the GPU pipeline, then feeds the result directly to the hardware encoder. Fewer data transfers, intermediate files and repeated computations make the entire generation process faster.

In a same-scene comparison on an RTX 5090 D v2, full 4K baking time fell from **108 seconds to 17 seconds, more than a 6-fold speedup**. A separate long-scene 4K test reached **3.7 times real-time speed** from analysis through generation, producing **4 minutes 18 seconds of 4K 60 FPS animation in about 70 seconds**.

**A redesigned interface.** 2.0 brings liquid glass styling to wallpaper selection, generation settings and the task queue, all in one window. Previews are rendered directly from the scene. Completed jobs can be previewed, applied or exported.

![WPE Baker 2.1 interface](https://isshiki-works.github.io/wallpaper-engine-baker/site/gui-2.1-en.png)

## Quick start

Requires Windows 10/11 64-bit, Wallpaper Engine from Steam and a Vulkan-capable GPU. The portable package includes its runtime dependencies; no separate .NET, Python or FFmpeg installation is needed.

1. Download `WpeBaker-2.1.2-win-x64.zip` from the release page and extract the whole archive into a writable directory.
2. Open the extracted `WpeBaker` folder and run `WpeBaker.exe`.
3. Choose the current wallpaper, or drop in a wallpaper folder or `scene.pkg`. Workshop wallpapers are usually under `steamapps\workshop\content\431960` in your Steam library.
4. Choose animation precision, interaction, frame rate and resolution, then click **Analyze**. The plan shows what can be baked, what stays live and which settings will be used. Analyze again after changing settings.
5. Click **Start generating**. Select the completed task to preview it in Wallpaper Engine, apply it to a screen, or export a ZIP. You can restore the previous wallpaper after applying it.

### Main controls

- **Animation precision:** Efficiency, Balanced (default) or Quality. Efficiency and Balanced use visible-change budgets of 5% and 3% to constrain animation adjustments. Quality seeks the smallest change that forms a loop. The analysis shows the preset actually used.
- **Interaction:** Keep, Fixed view (default) or Off, for retaining mouse effects, fixing the view or turning mouse effects off. The plan lists how mouse- and audio-driven content will be handled. Clocks, dates and media text stay live.
- **Output:** Set the frame rate and resolution. Higher settings increase generation time and file size.
- **Wallpaper properties:** Adjust switches, colors and other controls supplied by the author before analyzing and generating.

You can also choose **Optimize live scene** to combine compatible water-wave effects while keeping the scene live, without generating video. Re-analyze plans saved by older versions with 2.1.

## How it works and where it helps

The program analyzes time in shaders, animation tracks, particles and videos, then adjusts motion within the selected precision to form repeatable loops. Periodic content is pre-rendered as video or static textures and combined with layers that need to stay live. Generation checks loop seams, image quality, composition and hardware decoding.

WPE Baker works with Scene wallpapers. Scenes with demanding, repeating animation usually offer more room for savings; lightweight scenes have less rendering work to remove. Wallpaper Engine still plays the finished project and renders its live layers.

### Offline scene renderer

Periodica also provides an offline **Wallpaper Engine scene renderer** for Windows. Developers can load `scene.pkg` or `scene.json`, specify the resolution, frame rate and frame count in a JSON job, and export raw RGBA frames or hardware-encoded video for other video tools. Source is in [engine](engine/); the [rendering entry point](engine/tools/SceneBake/main.cpp) defines the parameters.

## Command line

The command-line program is `wpe-baker.exe` in the same folder. Open PowerShell there:

```powershell
.\wpe-baker.exe analyze "D:\Wallpapers\scene.pkg" --out plan.json
.\wpe-baker.exe bake plan.json --out "D:\Wallpapers\BakedScene"
.\wpe-baker.exe --help
```

For `bake`, `--out` must name a new output directory. After generation, use `compare` to measure playback power for the original and baked projects:

```powershell
.\wpe-baker.exe compare "D:\Wallpapers\Original" --baked "D:\Wallpapers\BakedScene" --wallpaper-engine "D:\SteamLibrary\steamapps\common\wallpaper_engine\wallpaper64.exe" --out "D:\Wallpapers\PowerReport"
```

Measurement requires one display and a single wallpaper assignment, and temporarily switches the desktop wallpaper. It reopens the original wallpaper afterward, restarting playback. Add `--restore-playback paused` to leave it paused instead. The report records iGPU and CPU-package power separately.

## Feedback

[Issues](https://github.com/isshiki-works/wallpaper-engine-baker/issues) are welcome in English or Chinese. Include the application version, GPU, Workshop ID, steps to reproduce and error messages. If analysis or generation produced reports, attach `plan.json`, `bake.json` and relevant logs.

## Source and licenses

The release includes the matching `WpeBaker-2.1.2-source.zip`, with dependency sources, third-party notices and build records. Complete build instructions are in its `REBUILD.md`.

The C# application and developer scripts use the [MIT license](LICENSE). The offline renderer derives from [open-wallpaper-engine](https://github.com/waywallen/open-wallpaper-engine) and uses [GPL-2.0](engine/LICENSE). See [third-party notices](THIRD-PARTY-NOTICES.md) for dependency licenses and [SOURCE.md](SOURCE.md) for corresponding source details. Wallpaper artwork belongs to its respective authors. WPE Baker is not affiliated with Wallpaper Engine.

## Credits

- **hypengw:** vvk, wavsen, rstd and related libraries.
- **isshiki:** product direction, design, hardware and testing.

Developed with Claude Code (Anthropic) and OpenAI Codex.
