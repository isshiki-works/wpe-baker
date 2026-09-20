# Local distribution dependencies — 2026-09-08

The isolated development prefix `.deps/ffmpeg-lgpl21/prefix` contains the five shared FFmpeg libraries, import libraries, public headers, pkg-config files and ffprobe. The historical `.deps/ffmpeg` directory and its validated renderer remain unchanged. No global installation, WSL, registry change or foreground interaction was used.

## FFmpeg inputs and evidence

FFmpeg 8.1.2 is pinned to `38b88335f99e76ed89ff3c93f877fdefce736c13`. AV1 software decoding uses statically linked dav1d 1.5.4, pinned to `54706fc6bc0cdecab7e9593974a4039cc038fca7`, under BSD-2-Clause. Both complete source trees remain under `.deps/ffmpeg-lgpl21/sources`; their original archives and SHA256 records remain in `.tools/downloads` and `distribution-inputs.lock.json`.

The source build uses the existing LLVM-MinGW 22.1.8 UCRT toolchain, NASM 3.02, Meson 1.12.0 and MSYS GNU Make 4.4.1. Git for Windows supplies its existing Bash/MSYS runtime; MSYS make is only extracted into `.tools/ffmpeg-build`, without package installation. Native mingw32-make cannot resolve configure's `/c/...` paths and is not the selected build tool. The pinned renderer's glslang executable supplies the build-time Vulkan shader compiler.

`--disable-gpl --disable-version3 --disable-nonfree --disable-autodetect` are explicit. Shared libraries, software decoders, libdav1d, Vulkan, D3D11VA, DXVA2 and Windows threads are enabled. Network, avdevice, avfilter and programs other than ffprobe are disabled. This prefix supplies renderer development libraries; its ffprobe is not an export encoder CLI.

The generated `config.h` has GPL, NONFREE, VERSION3 and GPLV3 set to 0. `ffprobe -L` and the actual `avutil_license`, `avcodec_license`, `avformat_license`, `swscale_license` and `swresample_license` calls all report `LGPL version 2.1 or later`. `.deps/ffmpeg-lgpl21/verification.json` records each DLL's SHA256, version, configuration, imports and verification results. Their direct imports consist of the selected FFmpeg DLLs and Windows system libraries; the decoder runtime does not require MSYS.

Using a child PATH containing only the new prefix and Windows system directories, ffprobe decoded H.264 and AV1 (explicitly forced to libdav1d), plus PCM, AAC, MP3, Vorbis, Opus and FLAC. Both video cases returned six frames. PCM, MP3, Opus and FLAC returned exactly 12,000 audio samples; AAC returned 12,288 and Vorbis returned 11,872. No decoder errors were emitted. These checks cover software file decoding; hardware decode and renderer integration require separate tests after the new renderer build.

The source license documents are also copied to `prefix/share/licenses/ffmpeg` and `prefix/share/licenses/dav1d`. Preserve the complete source archives and the source LICENSE notices, including third-party notices, when preparing the final distribution. This dependency preparation does not itself publish or complete a distributable bundle.

```powershell
python scripts/fetch-ffmpeg-lgpl21-inputs.py
python scripts/build-ffmpeg-lgpl21.py --stage all
python scripts/verify-ffmpeg-lgpl21.py
```

## Selecting the renderer libraries

The supported build driver defaults to the new prefix and separate output tree. The explicit equivalent is:

```powershell
python scripts/build-native-cmake.py --target wpe-render --build-dir build/native-release22 --ffmpeg-root .deps/ffmpeg-lgpl21/prefix
```

The direct CMake cache option is `WPE_FFMPEG_ROOT`. Existing CMake caches retain their detected FFmpeg root when this option was absent. Both entry points reject switching FFmpeg prefixes within an existing output tree, so headers, import libraries and C++ module caches cannot silently mix. The driver selects the matching pkg-config/runtime paths and records the selected headers, import libraries and DLLs in its provenance. The compatibility command for the existing local-validation build is `--build-dir build/native22 --ffmpeg-root .deps/ffmpeg`; it is not the release default.

The current renderer includes the mixed-size software video sampling correction and separate draw/readback timing. `build/native-release22/bin/wpe-render.exe` has SHA256 `39b45fdad6fd01a724aa624e720f1b2aa90a43d1a533ff91e7e6fc1ebc727dcd`, source digest `0cf891b4a3e7e195351e77d2418ec6b5074c0be208e9d0e48fc0ec0b2ddde782`, and `verified-source-binding` provenance. Runtime PATH for these artifacts must prepend `.tools/llvm-mingw-22/bin` and `.deps/ffmpeg-lgpl21/prefix/bin` as absolute paths. The preserved native22 build is unchanged. See `NATIVE-BUILD-STATE.md` for the current targeted verification and historical build records.

## Portable .NET toolchain

`.dotnet/dotnet.exe` is the verified official .NET SDK 10.0.400, with MSBuild 18.9.6 and .NET/ASP.NET/WindowsDesktop runtimes 10.0.11. The 300,546,129-byte archive matched the SHA512 from Microsoft's release metadata. `.dotnet/toolchain-verification.json` preserves URLs, checksums, the successful English `--info` output and available reference packs. `scripts/fetch-dotnet.py` reproduces the local download/extraction.

Use child-process values for `DOTNET_ROOT=.dotnet`, `DOTNET_CLI_HOME=.tools/dotnet-home`, `NUGET_PACKAGES=.tools/nuget-packages`, `DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1`, `DOTNET_CLI_TELEMETRY_OPTOUT=1` and `DOTNET_CLI_UI_LANGUAGE=en-US`. No global PATH or SDK registration is required.

## Portable GPL 2 H.264 / HEVC encoder and hardware decode probe

The separately built `.deps/ffmpeg-encoder-gpl2/portable` directory contains `ffmpeg.exe`, `ffprobe.exe`, their six adjacent DLLs and original license notices. FFmpeg is the same pinned 8.1.2 source; x264 comes from VideoLAN's official Git stable revision `b35605ace3ddf7c1a5d67a2eb553f034aef41d55` (r3222). Both source trees were compared against their original ZIPs: all 10,135 FFmpeg files and 270 x264 files matched, with no source patches. The x264 Git bundle preserves both pinned refs used by version.sh for offline reconstruction.

Both programs' actual `-L` output and all six DLL license APIs report GPL version 2 or later. Version-3 and nonfree options are disabled. Production uses `libx264`, `libx264rgb` and the newly included static 8-bit `libx265` 4.2 (upstream version string `4.2+1-e444744`); rawvideo and the existing audio encoders remain available. The official x265 tarball and SHA256 are in `encoder-inputs.lock.json`. The runtime imports only the packaged FFmpeg libraries and Windows system libraries.

D3D11VA H.264, HEVC and AV1 decode is enabled using the existing Windows headers. Actual 3072×1920 H.264, HEVC and AV1 samples each decoded five mandatory hardware frames on this machine's NVIDIA and AMD adapters. The probe selects exact DXGI indices, records LUIDs, requires hardware surfaces and `hwdownload`, and rejects an early EOF even if FFmpeg exits zero. This does not certify sustained FPS or WPE Scene playback.

Media Foundation H.264, HEVC and AV1 encoder entry points are compiled for compatibility experiments but are not selected for production. This machine's H.264/HEVC attempts returned `MF_E_UNSUPPORTED_D3D_TYPE`; AV1 returned dimensions different from the request. Encoder enumeration is not treated as evidence of a usable encoder.

Real tests used the existing C# export argument shape: 12 frames at 120000/1001 FPS, BT.709 limited-range yuv420p, RGB H.264, RGB+alpha side-by-side packing, and AAC stereo mux/decode. RGB and packed RGB were byte-exact after FFmpeg software decoding. This encoder check does not claim byte-exact native Vulkan video playback. The renderer's five LGPL 2.1 DLLs were separately checked against their existing verified hashes and remain unchanged.

The checked source-inclusive package is `.deps/ffmpeg-encoder-gpl2/ffmpeg-8.1.2-x264-x265-gpl2-win-x64-with-source.zip` (43,640,429 bytes, SHA256 `d90c028844966532f2d400f3c8788e4982aad97463d40d14d229c78f1356e53e`). It includes binaries, sources, the offline x264 bundle, original notices, configured build files, build recipes, input/tool locks and verification records. `ENCODER-BUILD.md` inside it describes reconstruction in the project layout; per-binary hashes are in the accompanying verification record.

```powershell
python scripts/fetch-encoder-inputs.py
python scripts/build-ffmpeg-encoder-gpl2.py --stage all
python scripts/verify-ffmpeg-encoder-gpl2.py
python scripts/package-ffmpeg-encoder-gpl2.py
```

Encoder records use their own `encoder-inputs.lock.json` and `encoder-build-tools.lock.json`; they do not change the renderer's selected dependency inputs.

## Current local build (2026-09-19)

Development and packaging now run on the desktop at `D:/Periodica/wpe-baker`.
The clean engine remains at `2866b4f`; no renderer implementation changed during migration.
Use `build/native-local22` with `--jobs 1` while the desktop has single-channel 24 GB RAM.
The locally built renderer is 13,606,400 bytes, SHA256
`81E6B456AFAF701C61E3CC103CB58D51708DD4A7D1B468E6B219C032657F19BE`,
source digest `8ffdb8f577306009bd368ddb384d1474f403215cccd507e1cb1386c7d2d06e27`.
Its record is `build/native-local22/provenance/build-wpe-render.json`, with
`verified-source-binding`. Both packaging commands require
`--native-build-dir build/native-local22`; pass the current binary SHA to
`verify-package.ps1 -ExpectedRenderer`. `patches/renderer-mt/sha256.txt` now records this build.
The laptop sources and original 1.0.2 distribution are preserved.

## Historical RC11 renderer record (mt-r4b; paths and hashes below describe the old build)

RC11 ships the multithreaded-decode renderer build, not the RC8 renderer. Because it is GPL v2, the archive pair must carry the exact sources it was built from.

**Origin.** Built during the 2026-09-17/18 renderer performance session; the binary and its 7 runtime DLLs live outside this repository at `D:/WPE-perf/renderer-r1/`. The measurements are in `reports-20260916/perf-renderer-video-decode.md`. The patch set itself is committed here under `patches/renderer-mt/` (`README.md`, `sha256.txt` and the two patches).

**Binary identity.** `wpe-render-mt-r4b.exe`, SHA256 `A8F03257E3B02CD691813AB7302E76C9EEA68567D77B3A71B1EC434A14FC3292` (13,618,176 bytes); `--version` reports source digest `e2f62b099966acc08b13e11e0318c3963acfa15e2db18749c0bdbdc6fa08f8a0`. The RC8 renderer it replaces is `F445D24D…`. The 7 runtime DLLs are byte-identical to the ones already in the portable package's `renderer/`, so they do not need to be replaced.

**Provenance.** The published binary was rebuilt on a clean `engine` tree at `2866b4f`, committed before it was built (verified-source-binding); the record is `build/native-mt22/provenance/build-wpe-render.json`. The earlier build of this renderer (`76B28255…`, source digest `10cd7196…`) was compiled from a working tree whose contents were never committed, so it is void and must not be shipped. Outputs are unchanged by the rebuild: 3772073136's `cache.mp4` is still `262BF7AF…`.

**Corresponding sources.** Two commits, both represented in this branch:

| Repository | Branch | Commit | Files |
|---|---|---|---|
| this repository | `perf/video-decode-threads` | `028aad68aa7f89c7602aad1dcd991b0c29ffedd5` | `scripts/dependency-patches/manifest.json`, `scripts/dependency-patches/wavsen.patch` (applied on this branch) |
| `engine` | `perf/video-decode-threads` | `2866b4ff22cde7f0c6cbc20f108d680bcdc1b8a0` | `src/Scene/VulkanRender/VulkanRender.cpp`, `VulkanRender.cppm`, `src/Scene/SceneWallpaper.cpp` (the `engine` gitlink on this branch points here) |

The wavsen change makes the software video decoder use libavcodec frame/slice threading, controlled by `WAVSEN_VIDEO_DECODE_THREADS`. The engine change reuses the offline read-back pixel buffer (R4), including the `2866b4f` fix to the recycle point. Rendered output is byte-identical to the RC8 renderer on the three verified cases.

**Rebuild.** `engine` here is a standalone Git repository in a subdirectory, not a submodule, so the gitlink and the engine working tree are maintained separately. On the build machine the engine working tree is already at `2866b4f` (`ce30024..2866b4f` is exactly `ef59f20` + `2866b4f`, the whole content of the engine-side patch), and the parent-side patch is already applied on this branch — so **neither patch has to be applied again**, only the gitlink had to be moved, which this branch does. On a machine where the engine tree is still at `ce30024`:

```powershell
git -C engine cat-file -t 2866b4ff22cde7f0c6cbc20f108d680bcdc1b8a0   # is the target object present?
git -C engine checkout 2866b4ff22cde7f0c6cbc20f108d680bcdc1b8a0      # or: git -C engine apply ../patches/renderer-mt/engine-perf-video-decode-threads.patch
python scripts/apply-dependency-patches.py
python scripts/build-native-cmake.py --target wpe-render --build-dir build/native-mt22 --jobs 14
```

**Order matters.** The parent-side patch has to be applied *before* `apply-dependency-patches.py` runs. Reversed, the script finds `.deps/wavsen` already in the multithreaded state while the old manifest only recognizes the old `after_sha256`, and raises `Unrecognized local edits`. On a checkout of this branch the patch is already in, so `apply-dependency-patches.py` is simply idempotent and prints `wavsen: recorded patch already applied`.

The produced `build/native-mt22/bin/wpe-render.exe` must match the SHA256 in `patches/renderer-mt/sha256.txt` before it is packaged. Build environment: `scripts/NATIVE-BUILD-STATE.md` (`.tools/llvm-mingw-22`, cmake, ninja, pkgconf, FFmpeg development libraries in `.deps/ffmpeg-lgpl21/prefix`).

**Packaging this renderer.** Both packaging scripts need an explicit `--native-build-dir build/native-mt22`. The default (`build/native-release22`) has a provenance record whose `binary.path` points at a different build directory, so `load_verified_build` rejects it:

```powershell
python scripts/package-portable.py --out dist/wpe-baker-1.0-rc11-<date> --native-build-dir build/native-mt22
python scripts/package-source.py --output dist/wpe-baker-1.0-rc11-<date>/WpeBaker-source.zip --native-build-dir build/native-mt22
```

**Do not let Git rewrite the working tree before packaging.** `load_verified_build` binds the renderer to 8594 files byte by byte, 7 of which are tracked scripts (`build-ffmpeg-lgpl21.py`, `build-native-cmake.py`, `build-native.py`, `distribution-inputs.lock.json`, `fetch-ffmpeg-lgpl21-inputs.py`, `native-inputs.lock.json`, `native-provenance.py`). With `core.autocrlf=true` and no `.gitattributes` entry covering them, they are LF (and `native-inputs.lock.json` is mixed) in the working tree but would come back CRLF if Git rewrote them, which silently breaks the binding. Move between branches with `git fetch <bundle> <branch>:<branch>` plus `git switch`, and keep `checkout -f`, `reset --hard`, `stash` and `clean -x` away from `scripts/`. If a single file does need restoring, restore that one path, not the tree.

**Measured effect and risk.** Per-frame step p50 on an RTX 5090: 26.4 → 11.5 ms for the 4K H.264 video case, 2.29 → 1.35 ms with no video layer; on an AMD integrated GPU, 39.0 → 24.0 ms. CPU time rises 12–16%. Each 4K decoder adds up to 331 MB peak working set, and several video layers oversubscribe threads.

**Backing out.** Set `WAVSEN_VIDEO_DECODE_THREADS=1` for the previous single-threaded decode behaviour (measured 26.2 ms versus the old binary's 26.4 ms, byte-identical output); the read-back buffer reuse has no switch but changes no output bytes. To revert completely, package the RC8 renderer `F445D24D…` instead and move the `engine` gitlink back to `ce30024`, keeping the source archive in step with whichever binary ships.
