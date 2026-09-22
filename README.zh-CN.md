# WPE Baker

[English](README.md) ｜ 简体中文

**面向 Wallpaper Engine 的确定性动画烘焙工具**，基于 Periodica 引擎。

场景壁纸每一帧都在实时重复渲染同样的画面。WPE Baker 为壁纸里的动画建立数学模型（着色器时间、动画轨道、粒子循环、视频时间基准），在可见变化预算内微调各自的周期，让整个场景首尾闭合成一段循环，再把其中确定性的部分预先渲染成一段视频。输出是一个独立的 Wallpaper Engine 项目：画面几乎不变，播放只需要视频解码。运行时不调用任何模型，也不需要 Python。

官网：**https://isshiki-works.github.io/wpe-baker/zh.html** · 下载：**[Releases](https://github.com/isshiki-works/wpe-baker/releases/latest)** · 逐案数据与开发记录：[技术记录](https://github.com/isshiki-works/wpe-baker/blob/main/docs/technical-notes.zh-CN.md)

## 实测

官方 Wallpaper Engine 播放器，Intel Arc B390 笔记本，A/B/B/A 交替测试，RAPL 核显功耗，60 fps。原版壁纸对比烘焙成品。

| 壁纸 | 原版 | 烘焙后 | 变化 |
|---|---:|---:|---:|
| 虹夏（3650475846） | 22.51 W | 1.86 W | −91.7% |
| 绫波丽（3258032485） | 2.29 W | 0.19 W | −91.8% |
| 亚托莉（3669681034） | 8.45 W | 0.81 W | −90.4% |
| Alone（3448877775） | 5.66 W | 1.05 W | −81.5% |
| 奥特曼雷欧（3685247684） | 9.13 W | 2.46 W | −73.0% |
| 百合（3572877776） | 2.38 W | 0.71 W | −70.3% |
| 芙莉莲（3426865175） | 10.09 W | 3.50 W | −65.3% |
| Lost Landscape 3（3713073223） | 8.11 W | 3.48 W | −57.1% |

刷新率越高，差距越大：亚托莉在面板满速 165 Hz 下，原版核显 26.0 W，烘焙后 3.9 W（整机封装 41.4 W → 15.4 W）。每个实测成品的逐案数据，包括没省下来的那些，见[技术记录](https://github.com/isshiki-works/wpe-baker/blob/main/docs/technical-notes.zh-CN.md)。

## 快速开始

1. 从 [Releases](https://github.com/isshiki-works/wpe-baker/releases/latest) 下载 `WpeBaker-1.0.2-win-x64.zip`，**整个**解压到有写入权限的目录（例如 `D:\WpeBaker`）。不要放进 `C:\Program Files`，也不要在压缩软件窗口里直接运行。
2. 双击 `WpeBaker\WpeBaker.exe`。第一次运行如果弹出"Windows 已保护你的电脑"，点"更多信息"→"仍要运行"：程序没有购买代码签名证书，提示只说明这一点。
3. 把 `steamapps\workshop\content\431960\` 下你想烘的那张壁纸的文件夹拖进窗口，点"分析"。
4. 看结论，点"开始生成"。成品会直接出现在 Wallpaper Engine 的壁纸列表里。

界面只有两个主要选项：

- **动画精度**：效率（5%）、平衡（3%，默认）、质量（能闭合的最小改动）。更严的档位闭合不了时会自动降一档，结论里写明实际用的是哪一档。
- **交互处理**：保留、固定视角（默认）、关闭，决定鼠标和音频驱动的效果怎么处理。时钟、日期、媒体文字在任何模式下都保持实时。

命令行：

```
wpe-baker.exe analyze <scene.pkg> --out plan.json
wpe-baker.exe bake plan.json --out <输出目录>
```

## 适用范围

- 只支持场景（Scene）类壁纸。视频壁纸和网页壁纸本来就是视频或网页，不需要烘。
- 重型、以周期动画为主的壁纸收益最大。轻量壁纸本身没什么可省的，工具会在渲染之前说明。
- 依赖鼠标、音频的部分会保持实时，这类壁纸省得少。
- 烘焙耗时取决于显卡：RTX 5090 上阿米娅（1080p、平衡档）约 8 分 44 秒，核显会慢得多。

## 系统要求

Windows 10/11 64 位；Wallpaper Engine（Steam 版）；一块支持 Vulkan 的显卡用于离线渲染。播放烘焙成品只需要硬件视频解码。

## 反馈

遇到问题欢迎[开 issue](https://github.com/isshiki-works/wpe-baker/issues)，中文完全没问题。请附上输出目录里的 `plan.json` 和 `bake.json`、显卡型号和壁纸的创意工坊 id，这几样基本能定位原因。

## 许可

工具层 MIT，离线渲染器 GPL-2.0（源自 open-wallpaper-engine）。第三方组件与许可文本见 `THIRD-PARTY-NOTICES.md` 和源码包里的 `licenses/`。烘焙成品仅供在自己的电脑上使用，壁纸作品版权归创意工坊作者所有，请勿二次上传。本项目与 Wallpaper Engine 官方无关联。

从源码构建：完整源码、第三方声明与构建记录在 Releases 页的 `WpeBaker-1.0.2-source.zip`，步骤见其中的 `REBUILD.md`。

## 致谢

- **hypengw**：渲染器底下的 vvk、wavsen、rstd 等库。
- **isshiki**：方向、产品决策、硬件与测试。

开发中使用了 AI 编程代理：Claude Code（Anthropic）与 OpenAI Codex。
