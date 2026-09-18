# 渲染器 mt-r4b 源码补丁（随二进制分发，GPL v2）

对应二进制：`D:\WPE-perf\renderer-r1\wpe-render-mt-r4b.exe`
SHA256 `A8F03257E3B02CD6…`（完整值见同目录 `sha256.txt`），`--version` source digest `e2f62b09…`。
发布用的这份二进制是在 engine `2866b4f` 的干净树上重建的（先提交后构建，verified-source-binding），
provenance 见 `build\native-mt22\provenance\build-wpe-render.json`；重建前那份（`76B28255…` / source digest
`10cd7196…`）的编译内容从未入库，已作废。重建后成品逐字节等价（3772073136 的 `cache.mp4` 仍是 `262BF7AF…`）。
运行库 7 个 DLL 与包内 `renderer\` 逐字节相同，不需要替换。

## 两份补丁

| 文件 | 目标仓库 | 基线 | 分支/提交 | 内容 |
|---|---|---|---|---|
| `parent-perf-video-decode-threads.patch`（SHA256 `80CCFAF86189F5BC…`） | `wpe-baker-next` 父仓 | `codex/native-offline` | `perf/video-decode-threads` `028aad6` | `scripts/dependency-patches/manifest.json`、`scripts/dependency-patches/wavsen.patch`：wavsen（`.deps/wavsen`）视频软解开帧级/切片级多线程，环境变量 `WAVSEN_VIDEO_DECODE_THREADS` 控制（未设/0 自动，1 = 原单线程，n = n，上限 64；libavcodec 自动上限 16） |
| `engine-perf-video-decode-threads.patch`（SHA256 `E9E42ED0829F5FF2…`） | `engine` 子模块 | `codex/windows-offline` | `perf/video-decode-threads` `ef59f20` + `2866b4f` | `src/Scene/VulkanRender/VulkanRender.cpp`、`VulkanRender.cppm`、`src/Scene/SceneWallpaper.cpp`：离线回读像素缓冲复用（R4），含 2866b4f 的归还时机修正 |

## 应用步骤

```
cd <wpe-baker-next>
git checkout codex/native-offline
git apply --check patches/parent-perf-video-decode-threads.patch && git apply patches/parent-perf-video-decode-threads.patch
python scripts/apply-dependency-patches.py          # 把 scripts/dependency-patches/wavsen.patch 应用到 .deps/wavsen
cd engine
git checkout codex/windows-offline
git apply --check ../patches/engine-perf-video-decode-threads.patch && git apply ../patches/engine-perf-video-decode-threads.patch
cd ..
python scripts/build-native-cmake.py --target wpe-render --build-dir build/native-mt22 --jobs 14
```

产物 `build/native-mt22/bin/wpe-render.exe`。构建环境见 `scripts/NATIVE-BUILD-STATE.md`（`.tools/llvm-mingw-22`、cmake、ninja、pkgconf，FFmpeg 开发库 `.deps/ffmpeg-lgpl21/prefix`）。

## 验证过的事实（`reports-20260916\perf-renderer-video-decode.md`）

- 逐字节：3660962877（4K H.264 视频层）、3685247684、3598808038 各 300 帧 `frames.rgba` 与 RC8 渲染器（`F445D24D…`）SHA256 相同；跨视频周期窗口（warmup 1150 + 120 帧）老 / 多线程 / 单线程一致。
- 每帧 step p50：5090 视频案 26.4 → 11.5 ms，5090 无视频案 2.29 → 1.35 ms，AMD 核显视频案 39.0 → 24.0 ms。
- 风险：每个 4K 视频解码器峰值工作集 +331 MB；多视频层时线程超配。
- 退回：设 `WAVSEN_VIDEO_DECODE_THREADS=1`，或换回 RC8 的 `wpe-render.exe`。

## 已知未验证

其它编码格式（HEVC/VP9/AV1）、非 60 fps、`input_timeline` 案的渲染输出逐字节；多视频层内存；笔记本 Arc 核显数字。判据线的 113 案全量用本二进制跑即是回归，成品 SHA 与旧渲染器不一致的案按旧 exe 重烘。
