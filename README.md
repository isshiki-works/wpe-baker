# WPE Baker

**Deterministic animation baking for Wallpaper Engine.** The first product built on Periodica.

Your wallpaper renders the same frames forever. WPE Baker models a scene's animation math — shader time, animation tracks, particle cycles, video timebases — retunes the periods within a visible-change budget until the whole scene closes into one loop, and pre-renders the deterministic part into a single video. The output is a standalone Wallpaper Engine project: same look, decoded for almost nothing, no model calls at runtime, no Python.

Website: **https://isshiki-works.github.io/wpe-baker/** · Download: **[Releases](https://github.com/isshiki-works/wpe-baker/releases/latest)** · 中文详细说明：[README.zh-CN.md](docs/README-full.zh-CN.md)

## Measured

Official Wallpaper Engine player, Intel Arc B390 laptop, A/B/B/A runs, RAPL iGPU rail, 60 fps. Original scene against its baked output.

| Wallpaper | Original | Baked | Change |
|---|---:|---:|---:|
| Nijika (3650475846) | 22.51 W | 1.86 W | −91.7% |
| Ayanami Rei (3258032485) | 2.29 W | 0.19 W | −91.8% |
| Atri (3669681034) | 8.45 W | 0.81 W | −90.4% |
| Alone (3448877775) | 5.66 W | 1.05 W | −81.5% |
| Ultraman Leo (3685247684) | 9.13 W | 2.46 W | −73.0% |
| Yuri (3572877776) | 2.38 W | 0.71 W | −70.3% |
| Frieren (3426865175) | 10.09 W | 3.50 W | −65.3% |
| Lost Landscape 3 (3713073223) | 8.11 W | 3.48 W | −57.1% |

At the panel's full refresh rate the gap widens: Atri at 165 Hz draws 26.0 W as the original and 3.9 W baked (package 41.4 W → 15.4 W). Per-title tables for every measured output, including the ones that did not save, are in [the full documentation](docs/README-full.md).

## Quick start

1. Download `WpeBaker-1.0-win-x64.zip` from Releases and unzip it. No installer.
2. Run `WpeBaker\WpeBaker.exe`, drag a Scene wallpaper folder from `steamapps\workshop\content\431960\` into the window, choose a preset, click Analyze.
3. Read the one-line verdict, click Generate, then apply the output from the Wallpaper Engine project list.

Command line:

```
wpe-baker.exe analyze <scene.pkg> --out plan.json
wpe-baker.exe bake plan.json --out <output-dir>
```

## How it works

- **Measure** — optionally plays the original in the official player first and reads the GPU power counters.
- **Solve** — each periodic component is solved for its own period, then retuned within the preset's budget (efficiency 5 %, balanced 3 %, quality: smallest change that closes) into one common loop.
- **Bake** — everything deterministic becomes one video; the output is rendered past its loop point and checked tile by tile against the original's next frame before it is accepted.
- **Input stays input** — mouse parallax, audio-reactive effects, clocks and interactive panels are treated as input: kept live, fixed, or left out, and always listed before generation.

## Requirements

Windows 10/11, Wallpaper Engine, a GPU for the offline render (any modern iGPU works; a discrete GPU is faster). Playback needs only hardware video decode.

## Building from source

Full source, third-party notices and build records are in `WpeBaker-1.0-source.zip` on the release page. On GitHub, `scripts/dependency-patches/rstd.patch` is stored as `rstd.patch.xz` (GitHub's 100 MB limit); `scripts/apply-dependency-patches.py` unpacks it automatically. See `REBUILD.md` in the source archive.

## Licenses

Tooling: MIT. Offline renderer: GPL-2.0 (derived from open-wallpaper-engine). Third-party components and license texts: `THIRD-PARTY-NOTICES.md` and `licenses/` in the source archive. Baked outputs are for personal use on your own machine; wallpaper artwork remains the property of its Steam Workshop authors. WPE Baker is not affiliated with or endorsed by Wallpaper Engine.

## Credits

Built for the GPT-6 Astra Challenge.

- **GPT-6 Astra (OpenAI Codex)** — the period solver, the Vulkan offline renderer for Wallpaper Engine scenes, the layer allocator, the encoder pipeline with hardware-decode checks, the power-measurement rig, and the 1.0.1 preset cascade.
- **isshiki** — direction, product decisions, hardware, testing.
