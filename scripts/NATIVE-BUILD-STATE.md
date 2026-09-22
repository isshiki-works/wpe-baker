# Native build state — 2026-09-08

The native Windows renderer has compiled, linked and passed real file-render tests. The current build includes deterministic timing, local effect capture, device UUID selection, GPU timing, corrected video color conversion and mixed-size video sampling. Official Wallpaper Engine playback and power remain separate checks.

## Working build entry

From `C:\Users\Alya\Desktop\Work\wpe-baker-next`:

```powershell
python scripts/build-native-cmake.py --target wpe-render
```

New builds default to `.tools/llvm-mingw-22`, isolated `build/native-release22`, and the verified `.deps/ffmpeg-lgpl21/prefix` development libraries. `--ffmpeg-root` explicitly selects a different project-local prefix. Existing `build/native22` uses `.deps/ffmpeg` and remains preserved; reusing a build directory with a different FFmpeg prefix is rejected. Clang 22 and 23 module caches must never be mixed. CMake uses Clang's own dependency scanner; each engine package has a hand-written CMakeLists.txt, tool paths live in `engine/CMakePresets.json`, and the module-free third-party libraries are prebuilt once per lock hash under `build/third-party/<key>/`. FetchContent is disconnected.

The LGPL 2.1 libraries and new full `native-release22` renderer build are ready. The capture/video source changes are included. No native22 reconfiguration was performed while adding this selection or producing the separate release build.

| Tool | Executable | Verified version |
| --- | --- | --- |
| LLVM-MinGW UCRT | `.tools/llvm-mingw-22/bin/clang++.exe` | Clang 22.1.8, x86_64-w64-windows-gnu |
| CMake | `.tools/cmake/bin/cmake.exe` | 4.4.3 |
| Ninja | `.tools/ninja/ninja.exe` | 1.13.2 |
| pkgconf | `.tools/pkgconf/bin/pkgconf.exe` | 3.0.7 |

pkgconf was extracted from the official MSI with read-only database/cabinet APIs; no MSI installation occurred. All tools are project-local.

## LGPL 2.1 renderer build

- Executable: `build/native-release22/bin/wpe-render.exe`
- SHA256: `39b45fdad6fd01a724aa624e720f1b2aa90a43d1a533ff91e7e6fc1ebc727dcd`
- Actual source digest: `0cf891b4a3e7e195351e77d2418ec6b5074c0be208e9d0e48fc0ec0b2ddde782`
- Provenance status: `verified-source-binding`
- `--version` loaded and ran with exit 0 using the selected LGPL 2.1 runtime.
- `owe-offline-vulkan-test` also compiled and linked; its GPU execution is owned by the Vulkan worker.
- `wavsen_offline_audio_tests` compiled, linked and ran with exit 0 against the new libraries.

The matching records are in `build/native-release22/provenance`. The current mixed-size video regression compares three differently sized videos alone, together and in reverse order: all 20 frames are byte-identical across arrangements. The final Miku project was also compared over 24 frames at 120 FPS and 1920×1080 with a changing pointer; maximum channel error was 11/255, mean RGBA error 0.0143296/255 and alpha error zero. Those are finite offline comparisons, not official playback certification.

The September 8 build adds `gpu_draw_ms` ending after program recording and before readback, while preserving `gpu_total_ms`. Optional-live cost trials use draw time at the requested resolution. The draw timing checks passed with identical pixels when timing was enabled or disabled; these remain native-renderer measurements, not official Wallpaper Engine load.

## Historical first renderer checkpoint

- Executable: `build/native22/bin/wpe-render.exe`
- Size: 13,242,368 bytes
- SHA256 at checkpoint: `0e89ce4d2dc158da85c555b6a971641a62977351c2becfb68a65cc0663e96f87`
- Actual source digest: `8357a110724fc8e4bceba80ca4bc2dd139cbabac4c2347d98352e869a2047d1b`
- Upstream base: `b866e8e711fdd7762385b23601affa1ea5539e3b`
- `--version` loaded and ran successfully, printing both base and actual source digest.

Full binding: `build/native22/provenance/build-wpe-render.json` and `source-wpe-render.json`. These cover actual engine/dependency sources, FFmpeg headers/import libraries, tool versions/hashes, input lock, compile_commands/CMakeCache and executable SHA256. New builds additionally bind the selected FFmpeg prefix and its runtime DLLs into the source snapshot. The driver refuses to certify an executable if source files changed during its build. Future successful rebuilds supersede checkpoint hashes.

For new LGPL 2.1 builds, prepend `.tools/llvm-mingw-22/bin` and `.deps/ffmpeg-lgpl21/prefix/bin` to the child process PATH. The historical native22 artifact still uses `.deps/ffmpeg/bin`. Never modify system PATH. Direct imports include libc++.dll, libunwind.dll, five FFmpeg core DLLs and Windows system DLLs including Vulkan and DWrite. The final portable package must resolve the complete runtime DLL closure.

## Verification

- Clang 22 independent C++20 module compile/link/run: native-module-ok.
- Real rstd, Vulkan, glslang, QuickJS, FreeType, LZ4 and wavsen libraries built.
- wavsen_offline_audio_tests linked and ran with exit 0. Explicit Release-mode checks cover PCM addition, mute advancing sources, pause preserving position, stream errors, invalid buffers and post-shutdown failure. Inputs are deterministic synthetic SoundStreams; this alone does not prove file-decoder fidelity. The test executable has its own provenance record.

The Vulkan worker owns the separate owe-offline-vulkan-test GPU regression target. Root owns real scene/120 FPS/fractional FPS CLI fixtures.

## Inputs and reproducibility

Sources in .deps: rstd, vvk, wavsen, SPIRV-Reflect, glslang, quickjs-ng, VulkanMemoryAllocator, LZ4, FreeType, Vulkan-Headers, Vulkan-Loader and Eigen. `scripts/native-inputs.lock.json` records their source pins and the historical FFmpeg input. The new shared FFmpeg development headers/import libraries/DLLs are in `.deps/ffmpeg-lgpl21/prefix`; `scripts/distribution-inputs.lock.json` records its source/build inputs. Original archives remain in project downloads directories. See `DISTRIBUTION-DEPENDENCIES.md` for reproduction and license verification.

`capture-dependency-patches.py` compares dependency sources against the verified archives and records checksummed patches plus before/after hashes in scripts/dependency-patches. `apply-dependency-patches.py` applies only to matching source bytes and preserves unexpected local edits. Re-capture after any further dependency fix.

The Vulkan import library was generated from the pinned official Vulkan-Loader export definition and uses the existing system driver loader. No driver/global SDK installation occurred. Fonts use real DirectWrite resolution and FreeType; no native Fontconfig dependency.

## Route decisions and failures

Lito 0.7.1 performed a real Eigen build but its custom frontend could not scan MinGW intrinsic preprocessor macros. Its CMake receipt parser also rejected CRLF. The custom-frontend failure justified the CMake route; the Lito route (`build-native.py`, `cmake-lito.cpp`, all `lito.toml`/`lito.lock`) was removed in T1 (2026-09-23).

Clang 23.1.0 built core/wavsen/Vulkan but crashed during Graphics.cppm IR generation at EmitStartEHSpec. A reduced-BMI experiment reproduced it. Stable Clang 22.1.8 passed using its own output tree. Old build/native is diagnostic only.

Other changes are actual header, source or linker corrections: Windows UTF-8/wide paths, global module visibility of standard headers, GDI Arc collision, sizeof(long)=4, Synchronization import library, and platform-isolated Linux FD/device paths. No core behavior/checks were disabled to manufacture successful results.

## Pending distribution boundary

The historical BtbN lgpl-shared package actually identifies LGPL-3.0-or-later (`--enable-version3`) in ffmpeg -L. It remains for local validation. The isolated FFmpeg 8.1.2 / dav1d 1.5.4 build now reports LGPL 2.1 or later from ffprobe -L and all five actual DLL license APIs. Eight software file-decode cases passed; `.deps/ffmpeg-lgpl21/verification.json` records the evidence. The renderer has now completed a full build and startup check against this prefix; real scene/video/capture acceptance and final bundle assembly remain separate checks.

The engine carries GPL v2 text without a project-specific SPDX version election. Its standard LICENSE appendix sample is not enough to infer GPL-2.0-or-later. Preserve upstream terms and do not merge Almamu GPL v3 core code.
