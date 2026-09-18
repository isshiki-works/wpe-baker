# Source code for this package / 本包的源码获取说明

This package contains programs licensed under the GNU GPL v2 and the GNU LGPL v2.1.
Their complete corresponding source code is distributed **in the same place as this
package**, as `WpeBaker-source.zip`.

本包含有以 GNU GPL v2 与 LGPL v2.1 授权的程序，对应的完整源码以
`WpeBaker-source.zip` 的形式**与本包发布在同一位置**。

- Download page / 发布页：release information is published on the Product Hunt page and the
  GitHub Release, which link to each other / 发布信息见 Product Hunt 页面与 GitHub Release，两者互相链接。
- Source archive / 源码包：`WpeBaker-source.zip` — SHA256 is listed on the download page
  (it is generated when the archives are packed and is deliberately not written back into the
  package, which would change the source archive's own hash) / SHA256 见发布页（打包后生成，
  不写回包内，以免改变源码包自身的哈希）。
- Portable archive / 便携包：`WpeBaker-win-x64-preview.zip` — SHA256 is listed on the download
  page, for the same reason / SHA256 见发布页（同样是打包后生成，不写回包内）。
- Engine Git identity / 引擎 Git 身份：`WpeBaker-source.engine.bundle`（与源码包同目录）

If the source archive is missing from where you got this package, that redistribution is
incomplete — ask the redistributor for it, or get it from the download page above.
若你拿到本包的地方没有源码包，那份再分发是不完整的，请向再分发者索取或到上面的发布页下载。

## Which binaries need source / 哪些二进制需要对应源码

| Binary in this package | License | Source in the source archive |
|---|---|---|
| `renderer\wpe-render.exe` | GPL-2.0-only | `engine\` (modified tree) + `engine-upstream.bundle` + `patches\` |
| `renderer\avcodec-62.dll`, `avformat-62.dll`, `avutil-60.dll`, `swresample-6.dll`, `swscale-9.dll` | LGPL-2.1-or-later | `.deps\ffmpeg-lgpl21\sources\ffmpeg`, `...\dav1d` |
| `encoder\ffmpeg.exe`, `ffprobe.exe` + 6 DLLs | GPL-2.0-or-later (x264 / x265 static) | `.deps\ffmpeg-encoder-gpl2\sources\{ffmpeg,x264,x265}` |
| `renderer\libc++.dll`, `libunwind.dll` | Apache-2.0 with LLVM exception | upstream release `.tools\llvm-mingw-22`, see `scripts\native-inputs.lock.json` |

Statically linked third-party components, their versions and licenses are listed in
`THIRD-PARTY-NOTICES.md` next to this file; license texts are under `licenses\`.
静态链接的第三方组件、版本与许可见同目录 `THIRD-PARTY-NOTICES.md`，许可全文在 `licenses\`。

## What is inside the source archive / 源码包里有什么

```
WpeBaker-source/
  engine/                        修改后的引擎源码（GPL v2 主体）
  engine-upstream.bundle         上游 Git 身份（供 git fetch 重建历史）
  patches/                       引擎侧补丁与记录
  patches/renderer-mt/           多线程渲染器补丁（父仓 + engine 各一份，含 README 与 sha256.txt）
  scripts/dependency-patches/    rstd / vvk / wavsen 的依赖补丁与 manifest.json
  .deps/<dependency>/            各依赖的完整源码快照（含各自许可文件）
  .deps/ffmpeg-lgpl21/sources/   FFmpeg + dav1d 源码（解码运行库）
  .deps/ffmpeg-encoder-gpl2/     FFmpeg + x264 + x265 源码、构建配置与验证记录
  src/ tests/ scripts/           WpeBaker 本体、单测与构建脚本
  build-records/                 渲染器构建溯源（源码摘要与二进制 SHA256）
  REBUILD.md                     重建步骤
  source-bundle.json             源码包内每个文件的 SHA256
  THIRD-PARTY-NOTICES.md         第三方组件与许可清单
```

Workshop wallpapers, baked masters and any generated user content are **not** included.
工坊壁纸、烘焙母版与任何用户生成内容**不在**包内。

## How to rebuild the renderer / 如何重建渲染器

Full steps are in `REBUILD.md`, `scripts\NATIVE-BUILD-STATE.md` and
`scripts\DISTRIBUTION-DEPENDENCIES.md` inside the source archive. Short version:

源码包内 `REBUILD.md`、`scripts\NATIVE-BUILD-STATE.md`、
`scripts\DISTRIBUTION-DEPENDENCIES.md` 有完整步骤，摘要如下：

The sources in this archive **already include** the multithreaded-decode changes: `engine\`
is the patched tree and `scripts\dependency-patches\wavsen.patch` is the updated patch. The two
files under `patches\renderer-mt\` only identify those changes against their upstream baselines
— do **not** apply them again.

本包内的源码**已经包含**多线程解码改动：`engine\` 是打过补丁的源码树，
`scripts\dependency-patches\wavsen.patch` 已是更新后的补丁。`patches\renderer-mt\`
里的两份补丁只是把改动相对上游基线标识出来，**不要再应用一遍**。

```powershell
# 1) 还原引擎与 x264 的 Git 身份（不覆盖工作文件）
git -C engine init
git -C engine fetch ../engine-upstream.bundle HEAD
git -C engine reset --mixed FETCH_HEAD

# 2) 把依赖补丁应用到 .deps（rstd / vvk / wavsen）
python scripts/apply-dependency-patches.py

# 3) 构建渲染器（--build-dir 必须与 build-records 里记录的目录一致）
python scripts/build-native-cmake.py --target wpe-render --build-dir build/native-mt22 --jobs 14
```

The produced `build\native-mt22\bin\wpe-render.exe` must have the SHA256 recorded in
`patches\renderer-mt\sha256.txt` and in `build-records\build-wpe-render.json`. Tool versions
are pinned in `scripts\native-inputs.lock.json`; downloading them needs network access once.

产物的 SHA256 应与 `patches\renderer-mt\sha256.txt`、
`build-records\build-wpe-render.json` 记录一致。工具链版本锁在
`scripts\native-inputs.lock.json`，首次下载需要联网，之后构建完全离线。

Repackaging with `scripts\package-portable.py` / `scripts\package-source.py` requires an
explicit `--native-build-dir build/native-mt22`: the default build directory's provenance
record does not describe the renderer shipped here and will be rejected.

用 `scripts\package-portable.py` / `scripts\package-source.py` 重新打包时必须显式给出
`--native-build-dir build/native-mt22`；默认构建目录的溯源记录描述的不是本包的渲染器，会被拒绝。

## How to rebuild FFmpeg / 如何重建 FFmpeg

```powershell
python scripts/fetch-ffmpeg-lgpl21-inputs.py    # 解码运行库（LGPL 2.1 口径）
python scripts/build-ffmpeg-lgpl21.py --stage all
python scripts/verify-ffmpeg-lgpl21.py

python scripts/fetch-encoder-inputs.py          # 编码器（GPL v2 口径，x264 / x265）
python scripts/build-ffmpeg-encoder-gpl2.py --stage all
python scripts/verify-ffmpeg-encoder-gpl2.py
```

x264's `version.sh` needs its Git identity; recreate it from the included bundle:
x264 的 `version.sh` 需要 Git 身份，用包内 bundle 还原：

```powershell
git -C .deps/ffmpeg-encoder-gpl2/sources/x264 init
git -C .deps/ffmpeg-encoder-gpl2/sources/x264 fetch ../../../../.tools/downloads/x264-b35605ace3ddf7c1a5d67a2eb553f034aef41d55.bundle refs/heads/source-pin:refs/heads/source-pin refs/heads/master-pin:refs/remotes/origin/master
git -C .deps/ffmpeg-encoder-gpl2/sources/x264 reset --mixed source-pin
```

Encoder build details are in `.deps\ffmpeg-encoder-gpl2\ENCODER-BUILD.md` and
`scripts\DISTRIBUTION-DEPENDENCIES.md`.
编码器的构建细节见 `.deps\ffmpeg-encoder-gpl2\ENCODER-BUILD.md` 与
`scripts\DISTRIBUTION-DEPENDENCIES.md`。

## Written offer / 书面要约

For three years from the date of this release, we will also provide the complete
corresponding source code for the GPL / LGPL parts of this package on a physical medium or
by direct download, at no more than the cost of distribution. Contact: GitHub Issues (the
repository address is on the download page).

自本次发布之日起三年内，我们也可按不超过分发成本的费用，以物理介质或直接下载的方式提供本包
中 GPL / LGPL 部分的完整对应源码。联系方式：GitHub Issues（仓库地址见发布页）。
