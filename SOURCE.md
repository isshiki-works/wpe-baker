# Source code / 源码说明

WPE Baker's release packages contain programs licensed under the GNU GPL v2 and the GNU LGPL
v2.1. This repository holds their source: the renderer is `engine/`, with its complete Git
history. Each release also publishes `WpeBaker-<version>-source.zip` **in the same place as the
portable package**, carrying the dependency source snapshots used for that build.

WPE Baker 发行包含有以 GNU GPL v2 与 LGPL v2.1 授权的程序。它们的源码在本仓库：渲染器就是
`engine/`，带完整 Git 历史。每次发行还会在便携包**同一位置**提供 `WpeBaker-<version>-source.zip`，
内含该次构建所用依赖的源码快照。

- Download page / 发布页：release information is published on the Product Hunt page and the
  GitHub Release, which link to each other / 发布信息见 Product Hunt 页面与 GitHub Release，两者互相链接。
- Source archive / 源码包：`WpeBaker-<version>-source.zip` — SHA256 is listed on the download page
  (it is generated when the archives are packed and is deliberately not written back into the
  package, which would change the source archive's own hash) / SHA256 见发布页（打包后生成，
  不写回包内，以免改变源码包自身的哈希）。
- Portable archive / 便携包：`WpeBaker-<version>-win-x64.zip` — SHA256 is listed on the download
  page, for the same reason / SHA256 见发布页（同样是打包后生成，不写回包内）。

If the source archive is missing from where you got a package, that redistribution is
incomplete — ask the redistributor for it, or get it from the download page above.
若你拿到发行包的地方没有源码包，那份再分发是不完整的，请向再分发者索取或到上面的发布页下载。

## The renderer: `engine/` / 渲染器：`engine/`

`engine/` is the complete source of the offline renderer (`renderer\wpe-render.exe`), with its
full Git history. It is a fork of
[waywallen/open-wallpaper-engine](https://github.com/waywallen/open-wallpaper-engine), licensed
GPL-2.0-only; our fork point is upstream commit `b866e8e711fdd7762385b23601affa1ea5539e3b`, and
the Windows offline-rendering changes are ordinary commits on top of it
(`git log b866e8e..d732d62`). It was merged into this repository with `git subtree` at engine
commit `d732d6223600cbb46a4fef5476a3bd749157b4d4` (merge commit `cabe223`), so every engine
commit keeps its original hash.

`engine/` 是离线渲染器（`renderer\wpe-render.exe`）的完整源码，带完整 Git 历史。它是
[waywallen/open-wallpaper-engine](https://github.com/waywallen/open-wallpaper-engine) 的分叉，
许可 GPL-2.0-only；分叉基点是上游提交 `b866e8e711fdd7762385b23601affa1ea5539e3b`，Windows
离线渲染的改动都是在此之上的普通提交（`git log b866e8e..d732d62`）。它以 `git subtree` 并入本仓库，
并入时的 engine 提交是 `d732d6223600cbb46a4fef5476a3bd749157b4d4`（合并提交 `cabe223`），
所有 engine 提交保留原哈希。

`engine/LICENSE` (GPL v2, without "or later") applies **only to `engine/`**. Everything else in
this repository is under the root `LICENSE` (MIT).
`engine/LICENSE`（GPL v2，无 "or later"）**只适用于 `engine/` 目录**；本仓库其余部分按根目录
`LICENSE`（MIT）。

Three of the renderer's dependencies carry our fixes: rstd, vvk and wavsen. The patches and
their checksums are in `scripts/dependency-patches/` (`manifest.json`); `rstd.patch` is stored
as `rstd.patch.xz` because of GitHub's file-size limit and is unpacked by
`scripts/apply-dependency-patches.py`.
渲染器有三个依赖带我们的修改：rstd、vvk、wavsen。补丁与校验值在
`scripts/dependency-patches/`（`manifest.json`）；`rstd.patch` 因 GitHub 单文件大小限制以
`rstd.patch.xz` 存放，由 `scripts/apply-dependency-patches.py` 自动解压。

## Which binaries need source / 哪些二进制需要对应源码

| Binary in the release package | License | Source |
|---|---|---|
| `renderer\wpe-render.exe` | GPL-2.0-only | `engine/` in this repository (commit per release: see below) + `scripts/dependency-patches/`; dependency snapshots under `.deps\` in the source archive |
| `renderer\avcodec-62.dll`, `avformat-62.dll`, `avutil-60.dll`, `swresample-6.dll`, `swscale-9.dll` | LGPL-2.1-or-later | source archive `.deps\ffmpeg-lgpl21\sources\ffmpeg`, `...\dav1d` |
| `encoder\ffmpeg.exe`, `ffprobe.exe` + 6 DLLs | GPL-2.0-or-later (x264 / x265 static) | source archive `.deps\ffmpeg-encoder-gpl2\sources\{ffmpeg,x264,x265}` |
| `renderer\libc++.dll`, `libunwind.dll` | Apache-2.0 with LLVM exception | upstream release `.tools\llvm-mingw-22`, see `scripts\native-inputs.lock.json` |

Statically linked third-party components, their versions and licenses are listed in
`THIRD-PARTY-NOTICES.md`; license texts ship under `licenses\` in the portable package.
静态链接的第三方组件、版本与许可见 `THIRD-PARTY-NOTICES.md`，许可全文随便携包在 `licenses\` 下。

Workshop wallpapers, baked masters and any generated user content are **not** included.
工坊壁纸、烘焙母版与任何用户生成内容**不在**仓库和发行包内。

## Historical releases / 历史发行

| Release | Tag | Renderer source | `wpe-render.exe` SHA256 |
|---|---|---|---|
| 1.0 | `v1.0` (`e5981c7`) | engine `2866b4ff22cde7f0c6cbc20f108d680bcdc1b8a0` | `A8F03257E3B02CD691813AB7302E76C9EEA68567D77B3A71B1EC434A14FC3292` |
| 1.0.1 | `v1.0.1` (`e411ed1`) | same | same |
| 1.0.2 | `v1.0.2` (`b8cd726`) | same | same |

All 1.0.x packages ship the same renderer binary (13,618,176 bytes, the multithreaded-decode
build). It was built from a clean engine tree at `2866b4f`, which is in `engine/`'s history,
with the dependency patches in `scripts/dependency-patches/` as of each release tag (the
wavsen patch there already contains the multithreaded video decode). Those releases' source
archives remain on their GitHub Release pages.

1.0.x 三个发行包的渲染器是同一个二进制（13,618,176 字节，多线程解码版）。它在 engine
`2866b4f` 的干净树上构建（该提交在 `engine/` 历史中），依赖补丁取各发行标签下的
`scripts/dependency-patches/`（其中的 wavsen 补丁已包含多线程视频解码）。这几次发行的源码包
仍在各自的 GitHub Release 页。

## Building / 构建

The native renderer is still built with the scripts in `scripts/`: fetch the pinned inputs,
apply the dependency patches, then build with CMake. Tool versions are pinned in
`scripts/native-inputs.lock.json`; fetching needs network access once, after that the build is
offline. Details are in `scripts/NATIVE-BUILD-STATE.md` and
`scripts/DISTRIBUTION-DEPENDENCIES.md`.

原生渲染器目前仍用 `scripts/` 下的脚本构建：先取锁定的输入，再应用依赖补丁，最后用 CMake 构建。
工具链版本锁在 `scripts/native-inputs.lock.json`，首次下载需要联网，之后构建完全离线。
细节见 `scripts/NATIVE-BUILD-STATE.md` 与 `scripts/DISTRIBUTION-DEPENDENCIES.md`。

```powershell
# 1) 取锁定的工具链与依赖源码到 .tools/、.deps/
python scripts/bootstrap-native.py
python scripts/fetch-native-extras.py
python scripts/fetch-ffmpeg-lgpl21-inputs.py
python scripts/build-ffmpeg-lgpl21.py --stage all

# 2) 把依赖补丁应用到 .deps（rstd / vvk / wavsen）
python scripts/apply-dependency-patches.py

# 3) 构建渲染器（源码取自 engine/）
python scripts/build-native-cmake.py --target wpe-render
```

The packaging scripts (`scripts\package-portable.py`, `scripts\package-source.py`,
`scripts\verify-package.ps1`) still expect the 1.0.x source layout and **do not work at the
moment**; they are being rebuilt together with the replacement of the build tooling.

打包脚本（`scripts\package-portable.py`、`scripts\package-source.py`、
`scripts\verify-package.ps1`）仍按 1.0.x 的源码包布局编写，**目前不可用**；它们正随构建生态的
替换一起重做。

## How to rebuild FFmpeg / 如何重建 FFmpeg

```powershell
python scripts/fetch-ffmpeg-lgpl21-inputs.py    # 解码运行库（LGPL 2.1 口径）
python scripts/build-ffmpeg-lgpl21.py --stage all
python scripts/verify-ffmpeg-lgpl21.py

python scripts/fetch-encoder-inputs.py          # 编码器（GPL v2 口径，x264 / x265）
python scripts/build-ffmpeg-encoder-gpl2.py --stage all
python scripts/verify-ffmpeg-encoder-gpl2.py
```

x264's `version.sh` needs its Git identity; recreate it from the fetched bundle:
x264 的 `version.sh` 需要 Git 身份，用取回的 bundle 还原：

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

For three years from the date of each release, we will also provide the complete
corresponding source code for the GPL / LGPL parts of that release on a physical medium or
by direct download, at no more than the cost of distribution. Contact: GitHub Issues (the
repository address is on the download page).

自每次发行之日起三年内，我们也可按不超过分发成本的费用，以物理介质或直接下载的方式提供该次发行
中 GPL / LGPL 部分的完整对应源码。联系方式：GitHub Issues（仓库地址见发布页）。
