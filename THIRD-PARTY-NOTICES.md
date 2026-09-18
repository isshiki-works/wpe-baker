# 第三方组件与许可声明（RC11）

本文件随 WpeBaker 便携包（`WpeBaker-win-x64-preview.zip`）与源码包
（`WpeBaker-source.zip`）一起分发，列出成品里包含或静态链接的全部第三方组件。
许可全文在便携包 `licenses\` 目录下；对应的完整源码在源码包里，获取方式见 `SOURCE.md`。

「修改」一列指我们是否改动过该组件的源码；补丁路径都是源码包内的相对路径。
许可文本一列是 `licenses\` 下的文件名（本文件同目录的 `licenses\` 是打包用的核对副本，
2026-09-18 从各上游按锁定版本下载，文本 SHA256 见本文件末尾）。

## 1. 引擎本体（GPL v2，随包提供源码）

| 组件 | 版本 / 提交 | 许可 | 来源 | 修改 | 补丁位置 |
|---|---|---|---|---|---|
| open-wallpaper-engine（`renderer\wpe-render.exe`） | 父仓 gitlink `ce30024`；发布用多线程版另加 `perf/video-decode-threads`（engine `2866b4f`，父仓 `028aad68`） | GPL-2.0-only（无 "or later"） | https://github.com/waywallen/open-wallpaper-engine （上游基线 `b866e8e`） | 是 | `engine/` 整树即修改后源码；`engine-upstream.bundle` 给出上游 Git 身份；`patches\embedded-video-runtime.patch`、`patches\scene-script-offline.patch`、`patches\wavsen-offline.patch`；多线程版补丁 `patches\renderer-mt\` |

许可文本：`licenses\open-wallpaper-engine.LICENSE`（GPL v2 全文）。
GPL v2 第 3 条要求二进制与对应完整源码一起提供 —— 两个 zip 必须发布在同一位置。
**引擎 LICENSE 没有 "or later"，所以不能合入 GPL v3 代码。**

## 2. 渲染器静态链接的依赖

| 组件 | 版本 / 提交 | 许可 | 来源 | 修改 | 补丁位置 | 许可文本 |
|---|---|---|---|---|---|---|
| FreeType | `VER-2-14-1`（2.14.1） | FTL（二选一，我们选 FTL） | https://github.com/freetype/freetype | 否 | — | `freetype.FTL.TXT` + `freetype.LICENSE.TXT` |
| glslang | `275822a6261ee689aadb1da5f09a0ec2f058685c` | 以 BSD-3-Clause 为主，含 BSD-2、MIT、Apache-2.0 | https://github.com/KhronosGroup/glslang | 否 | — | `glslang.LICENSE.txt` |
| LZ4 | `v1.10.0` | 库部分 BSD-2-Clause（`lib/LICENSE`），仓库其余 GPL-2.0（只用库） | https://github.com/lz4/lz4 | 否 | — | `lz4.lib.LICENSE` + `lz4.LICENSE` |
| quickjs-ng | `3c051980ab7e783dfbfb1c70c014ce5e05ecf24c` | MIT | https://github.com/quickjs-ng/quickjs | 否 | — | `quickjs-ng.LICENSE` |
| VulkanMemoryAllocator | `3aa921224c154a0d2c43912bc88e1c42ce1f7607` | MIT | https://github.com/GPUOpen-LibrariesAndSDKs/VulkanMemoryAllocator | 否 | — | `vma.LICENSE.txt` |
| SPIRV-Reflect（hypengw fork） | `355785128c1b6ba808e3a7d0e344814fe6cff502` | Apache-2.0 | https://github.com/hypengw/SPIRV-Reflect | 否 | — | `spirv-reflect.LICENSE` |
| Eigen | `bc3b39870ecb690a623a3f49149a358b95c5781d` | MPL-2.0 为主，个别文件 BSD 等 MPL2 兼容许可 | https://gitlab.com/libeigen/eigen | 否 | — | `eigen.COPYING.MPL2` + `eigen.COPYING.README` |
| Vulkan-Headers | `v1.4.321` | Apache-2.0 OR MIT | https://github.com/KhronosGroup/Vulkan-Headers | 否 | — | `vulkan-headers.LICENSE.md` + `vulkan-headers.Apache-2.0.txt` + `vulkan-headers.MIT.txt` |
| Vulkan-Loader | `v1.4.321` | Apache-2.0 | https://github.com/KhronosGroup/Vulkan-Loader | 否 | — | `vulkan-loader.LICENSE.txt` |
| rstd | `456fec5cc2b87acdb56800e298b5712ea69cdd47` | MIT OR Apache-2.0 | https://github.com/litocpp/rstd | **是** | `scripts\dependency-patches\rstd.patch`（manifest 记 143 个文件，其中 138 个是误捕获的 lito 构建缓存，真实源码改动 5 个文件） | `rstd.LICENSE-MIT` + `rstd.LICENSE-APACHE` |
| wavsen | `77dfd33d07112c05df4682e08b98e19153ebe3ab` | MIT OR Apache-2.0（上游 2026-09-08 在 `5a0ddb9` 才加入许可文件） | https://github.com/hypengw/wavsen | **是** | `scripts\dependency-patches\wavsen.patch`（18 个文件）；多线程解码追加改动见 `patches\renderer-mt\parent-perf-video-decode-threads.patch` | `wavsen.LICENSE-MIT` + `wavsen.LICENSE-APACHE`（取自 `5a0ddb9`，见下方说明） |
| vvk | `f53d60cc70938d0485802750deeb15d18ba033ea` | **未声明，见第 5 节** | https://github.com/litocpp/vvk | **是** | `scripts\dependency-patches\vvk.patch`（6 个文件） | 无 |

FreeType 选 FTL 时上游要求在文档里致谢，发布文档与便携包 README 的 License 节须包含：

> Portions of this software are copyright © 2026 The FreeType Project (www.freetype.org). All rights reserved.

Eigen 是 MPL-2.0，MPL 第 3.2 条要求告知源码获取方式：Eigen 完整源码在源码包
`.deps\eigen\`，上游副本为
`https://gitlab.com/libeigen/eigen/-/archive/bc3b39870ecb690a623a3f49149a358b95c5781d/eigen-bc3b39870ecb690a623a3f49149a358b95c5781d.zip`
（SHA256 `f0209070eb3a00ad0fe5d1cc4ccdac9c0b41e95ef7062f356fd01b7b2208630c`，见
`scripts\native-inputs.lock.json`）。我们没有修改 Eigen。

### wavsen 锁定版本与许可文本的时间差（必须照这个口径写）

我们锁定的 `77dfd33`（2026-08-31）早于上游加入许可文件的 `5a0ddb9`（2026-09-08），
本地快照里**没有**许可文件，已联网核对：`77dfd33` 的 `LICENSE-MIT` 返回 404。
附带的 `wavsen.LICENSE-MIT` / `wavsen.LICENSE-APACHE` 取自 `5a0ddb9`，代表**上游当前**
的许可意图，不是锁定版本自带的文件。两条处置路径（发布前至少做一条）：

1. **首选**：把锁定版本升到 `5a0ddb9` 或更新，重捕 `wavsen.patch`、重建渲染器并重跑回归；
   这样许可文本与源码版本一致，本节的说明可以删掉。
2. 次选：请作者（hypengw）书面确认 `77dfd33` 同样适用 MIT OR Apache-2.0，把确认件
   随包提供，并保留本节说明。

## 3. 渲染器附带的解码运行库（LGPL 2.1，动态链接）

`renderer\` 下 5 个 DLL：`avcodec-62.dll`、`avformat-62.dll`、`avutil-60.dll`、
`swresample-6.dll`、`swscale-9.dll`。

| 组件 | 版本 / 提交 | 许可 | 来源 | 修改 |
|---|---|---|---|---|
| FFmpeg | `n8.1.2` = `38b88335f99e76ed89ff3c93f877fdefce736c13`，`--disable-gpl --disable-version3 --disable-nonfree` | LGPL-2.1-or-later | https://github.com/FFmpeg/FFmpeg | 否（无源码补丁） |
| dav1d | `54706fc6bc0cdecab7e9593974a4039cc038fca7`（1.5.4，静态进 avcodec） | BSD-2-Clause | https://github.com/videolan/dav1d | 否 |

许可文本：`licenses\renderer-codecs\ffmpeg\COPYING.LGPLv2.1`、
`licenses\renderer-codecs\ffmpeg\LICENSE.md`、`licenses\renderer-codecs\dav1d\COPYING`
（RC8 包里已在位，RC11 沿用）。源码在源码包 `.deps\ffmpeg-lgpl21\sources\`。

## 4. 编码器（GPL v2+，静态链接 x264 / x265）

`encoder\` 下 `ffmpeg.exe`、`ffprobe.exe` 与 6 个 DLL。

| 组件 | 版本 / 提交 | 许可 | 来源 | 修改 |
|---|---|---|---|---|
| FFmpeg | 同上 `38b8833`，按 GPL 口径配置 | GPL-2.0-or-later | https://github.com/FFmpeg/FFmpeg | 否 |
| x264 | `b35605ace3ddf7c1a5d67a2eb553f034aef41d55`（r3222） | GPL-2.0-or-later | https://code.videolan.org/videolan/x264 | 否（`x264.patch` 为 0 字节空补丁） |
| x265 | `4.2`（版本串 `4.2+1-e444744`） | GPL-2.0-or-later | https://download.videolan.org/pub/videolan/x265/x265_4.2.tar.gz | 否 |
| MinGW-w64 runtime / LLVM 运行库 | `.tools\llvm-mingw-22`（Clang 22.1.8 UCRT） | MinGW-w64 runtime license / Apache-2.0 with LLVM exception | https://github.com/mstorsjo/llvm-mingw | 否 |

许可文本：`encoder\licenses\{ffmpeg,x264,x265,llvm,mingw}\`（RC8 已在位）、
`licenses\llvm-mingw.LICENSE.txt`。源码在源码包 `.deps\ffmpeg-encoder-gpl2\sources\`。

## 5. vvk：许可待作者确认（发布阻断项）

- 上游 `litocpp/vvk` 在锁定版 `f53d60c` 与当前 HEAD 都**没有任何许可声明**：
  根目录无 `LICENSE*`，`lito.toml` 无 license 字段，GitHub API 的 `license` 字段为 `null`
  （2026-09-18 联网核对，上游最后 push 2026-09-17）。
- 没有许可声明意味着默认「保留所有权利」，我们**没有再分发授权**；vvk 的代码被静态链接进
  `wpe-render.exe`，我们还打了 6 个文件的补丁。
- 处置：发布前请作者给出明确许可（MIT / Apache-2.0 等），或确认允许按引擎 GPL v2 再分发。
  拿不到确认就只能在「已知问题」里如实披露，并准备在作者要求时下架。

发布包「已知问题」建议措辞（中文）：

> **依赖许可未确认**：渲染器静态链接的 Vulkan 绑定库 vvk（`litocpp/vvk`，锁定提交
> `f53d60c`）在上游仓库没有任何许可声明，我们已联系作者确认许可，尚未得到答复。在此之前，
> 本包中与 vvk 相关的部分没有明确的再分发授权；如作者提出要求，我们会立即下架或替换。
> vvk 的完整源码与我们的补丁都在源码包 `.deps\vvk\`、
> `scripts\dependency-patches\vvk.patch` 里，便于第三方自行核查与替换。

英文版：

> **Unconfirmed dependency license**: vvk (`litocpp/vvk`, pinned commit `f53d60c`), the Vulkan
> binding library statically linked into the renderer, carries no license statement upstream.
> We have asked the author to clarify and have not received an answer. Until then the vvk
> portion of this package has no explicit redistribution grant; we will take it down or replace
> it at the author's request. vvk's complete source and our patch are included in the source
> archive (`.deps\vvk`, `scripts\dependency-patches\vvk.patch`).

## 6. 其他随包程序

| 组件 | 版本 | 许可 | 来源 | 修改 | 许可文本 |
|---|---|---|---|---|---|
| PresentMon | `2.5.1`（官方发行的 `PresentMon-2.5.1-x64.exe`，SHA256 `9bec3083…`） | MIT | https://github.com/GameTechDev/PresentMon/tree/v2.5.1 | 否 | `licenses\PresentMon.LICENSE.txt` + `PresentMon.THIRD_PARTY.txt` |
| .NET 10.0.400 运行时（self-contained 发布） | SDK 10.0.400 / 运行时 10.0.11 | MIT | https://dotnet.microsoft.com | 否 | `licenses\dotnet.LICENSE.txt` + `dotnet.ThirdPartyNotices.txt` |
| WpeBaker 本体（`src\`、`tests\`、`scripts\`） | 见发布说明的提交号 | 见仓库根 `LICENSE` | 本项目 | — | 包内 `LICENSE` |

## 7. 兼容性提醒（不下法律结论）

GPL-2.0-only 的引擎静态链接 Apache-2.0-only 的组件（SPIRV-Reflect、Vulkan-Loader 导入库），
按 FSF 的观点存在兼容性争议。这个结构是上游本来就有的，本文件只做记录。

## 8. 核对副本的文本 SHA256（2026-09-18 下载）

| 文件 | SHA256（前 16 位） |
|---|---|
| freetype-LICENSE.TXT | `bd36c8b474855fa2` |
| freetype-FTL.TXT | `5a5ee54c5001bbad` |
| glslang-LICENSE.txt | `17e70c676e1521ff` |
| lz4-LICENSE | `4bc9c403f6b679cc` |
| lz4-lib-LICENSE | `8b58c446121a109c` |
| quickjs-ng-LICENSE | `96f73f9d2a16c21a` |
| vma-LICENSE.txt | `52df2c03d6cfc9ff` |
| spirv-reflect-LICENSE | `c71d239df91726fc` |
| eigen-COPYING.MPL2 | `66a3107d5ad6a058` |
| eigen-COPYING.README | `db640ff2bd90c6ab` |
| vulkan-headers-LICENSE.md | `ac24e5ea920e4318` |
| vulkan-headers-LICENSES-Apache-2.0.txt | `cfc7749b96f63bd3` |
| vulkan-headers-LICENSES-MIT.txt | `1ca3502222d967f3` |
| vulkan-loader-LICENSE.txt | `43c0a37e6a0fa7ff` |
| rstd-LICENSE-MIT | `dc69d4de4e50e20b` |
| rstd-LICENSE-APACHE | `cfc7749b96f63bd3` |
| wavsen-LICENSE-MIT | `2d1edaf74e77c63e` |
| wavsen-LICENSE-APACHE | `cfc7749b96f63bd3` |
| ffmpeg-COPYING.LGPLv2.1 | `246041b6ecf9bc32` |
| ffmpeg-COPYING.GPLv2 | `8177f97513213526` |
| dav1d-COPYING | `dd92c3c2247c5651` |
| x264-COPYING | `32b1062f7da84967` |
| x265-COPYING | `d8afb1bcc7a2cfc6` |

打包时优先从构建机 `.deps\<组件>\` 里的原始许可文件拷贝（那是真正参与构建的副本）；
本目录的 23 份下载件是台式机 `.deps` 为空时的兜底与核对基准。
