# WPE Baker 2.0

[English](README.md) ｜ 简体中文

**你的壁纸一直在实时渲染。烘焙一次就够了。**

WPE Baker 将 Wallpaper Engine 场景中反复播放的动画提前渲染成无缝循环视频。壁纸照常动，显卡不必一遍遍重算相同的效果；时钟、音乐响应等实时内容也可以保留。

它是 Periodica 的首个应用。Periodica 通过分析和协调动画周期，让原本各自运动的画面形成循环。整个过程在本机完成，无需上传壁纸，也不调用 AI 模型。

官网：**https://isshiki-works.github.io/wpe-baker/zh.html** · 下载：**[2.0.0-rc.1](https://github.com/isshiki-works/wpe-baker/releases/tag/v2.0.0-rc.1)** · 技术细节与项目记录：[技术记录](https://github.com/isshiki-works/wpe-baker/blob/main/docs/technical-notes.zh-CN.md)

## 2.0 更新了什么

- **从逐项编写方程，到直接分析着色器。** 程序沿编译后 SPIR-V 的数据流追踪时间，识别循环、持续运动和逐渐停止的动画，并将作者脚本纳入分析。支持范围不再只取决于预先整理了多少种效果。
- **独立的动画，分别协调。** 通过局部调速缩短循环，彼此独立的视频组采用各自的周期。烘焙内容放回原场景时，透明度、混合顺序和 HDR 也一并处理。
- **生成更快。** 在 GPU 上完成缩放后再回读，减少原始帧搬运，并接通 Intel、AMD、NVIDIA 的硬件编码。Intel Arc B390 上，亚托莉以 3072×1920、60 FPS 完整生成，用时约 4 分 55 秒。
- **以液态玻璃美学重新设计界面。** 当前壁纸、生成设置与任务队列集中在同一窗口。预览图直接从场景渲染，不再放大小尺寸封面。

![WPE Baker 2.0 界面](https://isshiki-works.github.io/wpe-baker/site/gui-2.0-zh.jpg)

## 快速开始

1. 从 [2.0.0-rc.1](https://github.com/isshiki-works/wpe-baker/releases/tag/v2.0.0-rc.1) 下载 `WpeBaker-2.0.0-rc.1-win-x64.zip`，**整个**解压到有写入权限的目录（例如 `D:\WpeBaker`）。不要放进 `C:\Program Files`，也不要在压缩软件窗口里直接运行。
2. 双击 `WpeBaker\WpeBaker.exe`。
3. 选择当前壁纸，或把场景壁纸的文件夹、`scene.pkg` 拖进窗口，点 **分析**。
4. 查看方案，调整帧率和分辨率，点 **开始生成**。
5. 选中已完成的任务，即可在 Wallpaper Engine 中预览、应用到指定屏幕，或导出 ZIP。

两个主要选项：

- **动画精度**：效率（5%）、平衡（3%，默认）、质量（能闭合的最小改动）。更严的档位闭合不了时会自动降一档，结论里写明实际用的是哪一档。
- **交互处理**：保留、固定视角（默认）、关闭，决定鼠标和音频驱动的效果怎么处理。时钟、日期、媒体文字在任何模式下都保持实时。

也可以选择 **优化实时场景**，合并兼容的效果，保留场景实时运行，不生成视频。

命令行：

```
wpe-baker.exe analyze <scene.pkg> --out plan.json
wpe-baker.exe bake plan.json --out <输出目录>
```

生成后可用 `wpe-baker.exe compare <原作目录> --baked <成品目录> --wallpaper-engine <wallpaper64.exe> --out <新报告目录>` 比较实际播放成本。命令会临时按 A/B/B/A 切换壁纸，结束后重新打开原分配，默认继续播放；加 `--restore-playback paused` 可让它结束后保持暂停。播放位置不会被保存或恢复，原来的暂停状态无法从保存配置读取。目前要求只连接一块显示器且未使用播放列表。报告分别展示核显与 CPU 封装功耗，方向相反时标为取舍；证据不完整时不给推荐，画面正确性另行检查。

## 适用范围

- 只支持场景（Scene）类壁纸。视频壁纸和网页壁纸本来就是视频或网页，不需要烘。
- 重型、以周期动画为主的壁纸是主要应用场景。分析会列出可缓存的动画和需要保留的实时图层。
- 鼠标与音频响应按所选交互模式处理，时钟、日期和媒体文字保持实时。
- 离线渲染按帧推进，烘焙耗时取决于场景复杂度、循环长度、输出分辨率和显卡。

## 系统要求

烘焙需要 Windows 10/11 64 位、Wallpaper Engine（Steam 版）和支持 Vulkan 的显卡。播放时，Wallpaper Engine 解码烘焙视频，并渲染保留的实时图层。

## 反馈

遇到问题欢迎[开 issue](https://github.com/isshiki-works/wpe-baker/issues)，中文完全没问题。请附上输出目录里的 `plan.json` 和 `bake.json`、显卡型号和壁纸的创意工坊 id，这几样基本能定位原因。

## 许可

`LICENSE` 中的 MIT 许可适用于 C# 应用/工具层和开发脚本。`engine/`（离线渲染器，源自 open-wallpaper-engine）及其修改沿用上游的 GPL-2.0，见 `engine/LICENSE`。FFmpeg、x264 等依赖保留各自的许可。

第三方组件与许可文本见 `THIRD-PARTY-NOTICES.md` 和源码包里的 `licenses/`。烘焙成品仅供在自己的电脑上使用，壁纸作品版权归创意工坊作者所有，请勿二次上传。本项目与 Wallpaper Engine 官方无关联。

从源码构建：Releases 页的 `WpeBaker-2.0.0-rc.1-source.zip` 包含对应源码、第三方声明与构建记录，步骤见其中的 `REBUILD.md`。旧版本保存的方案请用 2.0 重新分析。

## 致谢

- **hypengw**：渲染器底下的 vvk、wavsen、rstd 等库。
- **isshiki**：方向、产品决策、硬件与测试。

开发中使用了 AI 编程代理：Claude Code（Anthropic）与 OpenAI Codex。
