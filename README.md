# WPE Baker 2.0

English ｜ [简体中文](README.zh-CN.md)

**Deterministic animation baking for Wallpaper Engine.** Built on Periodica.

Periodica analyzes animation in Wallpaper Engine scenes, coordinates its periods into seamless loops, and renders those loops as video. Clocks, music response and other live content can remain in the scene, reducing repeated rendering while preserving the animated wallpaper. Its first application is WPE Baker for Wallpaper Engine.

The output is a standalone Wallpaper Engine project. The video decoder plays the repeating animation while retained layers continue responding to live input. Analysis and generation run locally, with no wallpaper uploads or AI model calls.

[Website](https://isshiki-works.github.io/wpe-baker/) · [Download 2.0.0](https://github.com/isshiki-works/wpe-baker/releases/tag/v2.0.0)

## Measured

Official Wallpaper Engine player, Intel Arc B390 laptop on AC power. Original and baked scenes alternate in A/B/B/A order, with 20 seconds sampled per segment. The table below uses Balanced precision and default wallpaper properties. Baked output is 1920×1080 at 60 FPS; originals render normally on the 3072×1920 display.

| Wallpaper | Original iGPU | Baked iGPU | Change | CPU package: original → baked |
|---|---:|---:|---:|---:|
| Nijika (3650475846) | 20.87 W | 14.63 W | −29.9% | 31.80 → 26.51 W |
| Ayanami Rei (3258032485) | 2.16 W | 0.14 W | −93.4% | 11.23 → 8.72 W |
| Atri (3669681034) | 8.51 W | 0.27 W | −96.8% | 17.85 → 9.26 W |
| Alone (3448877775) | 5.52 W | 4.81 W | −12.8% | 15.45 → 16.17 W |
| Ultraman Leo (3685247684) | 9.08 W | 1.44 W | −84.1% | 19.58 → 11.58 W |
| Yuri (3572877776) | 2.10 W | 0.48 W | −77.4% | 11.59 → 9.45 W |
| Frieren (3426865175) | 10.57 W | 7.23 W | −31.6% | 19.62 → 19.64 W |
| Lost Landscape 3 (3713073223) | 8.10 W | 3.30 W | −59.2% | 17.96 → 13.61 W |

Power savings also extend to high refresh rates. These tests use the same alternating playback method, with both original and baked output at the display's native 3072×1920 resolution:

| Wallpaper and frame rate | Original iGPU | Baked iGPU | Change | CPU package: original → baked |
|---|---:|---:|---:|---:|
| Atri · 60 FPS | 8.50 W | 0.30 W | −96.5% | 16.53 → 9.33 W |
| Atri · 165 FPS | 25.55 W | 2.19 W | −91.4% | 37.67 → 12.84 W |
| Ayanami Rei · 120 FPS | 8.75 W | 1.97 W | −77.5% | 18.62 → 13.72 W |

## 2.0 · Performance update

**From period equations to shader analysis.** 2.0 applies abstract interpretation to compiled SPIR-V, following time through the data flow to derive cycles, continuous drift and animation that settles after a while. New effects no longer need a hand-written equation of their own; unfamiliar custom shaders can be analyzed too. Author scripts are also included in time analysis.

**Independent motion, separate adjustments.** Waves, glows and scrolling within one effect no longer have to speed up together. Motions that can be controlled independently are adjusted separately, and independent video groups repeat at their own intervals. Transparency and blending order are preserved when baked content returns to the scene. HDR intermediate images are mapped into video and restored by a generated shader.

**A high-performance renderer built for offline generation.** Building on open-wallpaper-engine, Periodica substantially reworks scene execution and the Vulkan rendering pipeline. Shaders, scripts, particles and video advance in deterministic time steps, with simulation, drawing and sampling scheduled separately. The direct GPU encoding path connects cropping, transparency processing and encoding in GPU memory, reducing repeated computation, synchronization waits and data transfers. This renderer underpins WPE Baker 2.0 and provides its own command-line interface for scene rendering, animation sampling and automated validation.

In RTX 5090 optimization benchmarks, 4K HEVC encoding throughput rose from 0.52 Gpixel/s with Vulkan Video to 5.67 Gpixel/s with direct NVENC encoding, a 10.9-fold increase. Full generation of a 4K scene with transparency fell from 89.5 to 40.6 seconds using the same plan. [Implementation and measurements](https://github.com/isshiki-works/wpe-baker/pull/128). Intel and AMD have their own hardware encoding paths: on Intel Arc B390, Atri generated all 15,456 frames at 3072×1920 and 60 FPS in about 4 min 55 s, using default properties and Balanced precision.

**A redesigned interface.** 2.0 brings liquid glass styling to wallpaper selection, generation settings and the task queue, all in one window. Previews are rendered directly from the scene. Completed jobs can be previewed, applied or exported.

![WPE Baker 2.0 interface](https://isshiki-works.github.io/wpe-baker/site/gui-2.0-en.jpg)

## Quick start

Requires Windows 10/11 64-bit, Wallpaper Engine from Steam and a Vulkan-capable GPU. The portable package includes its runtime dependencies; no separate .NET, Python or FFmpeg installation is needed.

1. Download `WpeBaker-2.0.0-win-x64.zip` from the release page and extract the whole archive into a writable directory.
2. Open the extracted `WpeBaker` folder and run `WpeBaker.exe`.
3. Choose the current wallpaper, or drop in a wallpaper folder or `scene.pkg`. Workshop wallpapers are usually under `steamapps\workshop\content\431960` in your Steam library.
4. Choose animation precision, interaction, frame rate and resolution, then click **Analyze**. The plan shows what can be baked, what stays live and which settings will be used. Analyze again after changing settings.
5. Click **Start generating**. Select the completed task to preview it in Wallpaper Engine, apply it to a screen, or export a ZIP. You can restore the previous wallpaper after applying it.

### Main controls

- **Animation precision:** Efficiency, Balanced (default) or Quality. Efficiency and Balanced use visible-change budgets of 5% and 3% to constrain animation adjustments. Quality seeks the smallest change that forms a loop. The analysis shows the preset actually used.
- **Interaction:** Keep, Fixed view (default) or Off, for retaining mouse effects, fixing the view or turning mouse effects off. The plan lists how mouse- and audio-driven content will be handled. Clocks, dates and media text stay live.
- **Output:** Set the frame rate and resolution. Higher settings increase generation time and file size.
- **Wallpaper properties:** Adjust switches, colors and other controls supplied by the author before analyzing and generating.

You can also choose **Optimize live scene** to combine compatible water-wave effects while keeping the scene live, without generating video. Re-analyze plans saved by older versions with 2.0.

## How it works and where it helps

The program analyzes time in shaders, animation tracks, particles and videos, then adjusts motion within the selected precision to form repeatable loops. Periodic content is pre-rendered as video or static textures and combined with layers that need to stay live. Generation checks loop seams, image quality, composition and hardware decoding.

WPE Baker works with Scene wallpapers. Scenes with demanding, repeating animation usually offer more room for savings; lightweight scenes have less rendering work to remove. Wallpaper Engine still plays the finished project and renders its live layers.

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

[Issues](https://github.com/isshiki-works/wpe-baker/issues) are welcome in English or Chinese. Include the application version, GPU, Workshop ID, steps to reproduce and error messages. If analysis or generation produced reports, attach `plan.json`, `bake.json` and relevant logs.

## Source and licenses

The release includes the matching `WpeBaker-2.0.0-source.zip`, with dependency sources, third-party notices and build records. Complete build instructions are in its `REBUILD.md`.

The C# application and developer scripts use the [MIT license](LICENSE). The offline renderer derives from [open-wallpaper-engine](https://github.com/waywallen/open-wallpaper-engine) and uses [GPL-2.0](engine/LICENSE). See [third-party notices](THIRD-PARTY-NOTICES.md) for dependency licenses and [SOURCE.md](SOURCE.md) for corresponding source details. Wallpaper artwork belongs to its respective authors. WPE Baker is not affiliated with Wallpaper Engine.

## Credits

- **hypengw:** vvk, wavsen, rstd and related libraries.
- **isshiki:** product direction, design, hardware and testing.

Developed with Claude Code (Anthropic) and OpenAI Codex.
