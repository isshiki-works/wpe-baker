# WPE Baker

[English](README.md) ｜ 简体中文

**面向 Wallpaper Engine 的确定性动画烘焙工具**，基于 Periodica 引擎。

WPE Baker 为场景壁纸里的动画建立数学模型（着色器时间、动画轨道、粒子循环和视频时间基准），并在可见变化预算内微调周期，让周期动画首尾闭合。一次离线生成会把确定性部分预渲染为视频或静态缓存；成品可以包含多个视频和纹理。重复播放时，预计算动画通过视频解码播放，保留的鼠标交互、音频响应、时钟和昼夜效果等图层则按所选模式继续实时运行。运行时不调用任何模型，也不需要 Python。

官网：**https://isshiki-works.github.io/wpe-baker/zh.html** · 下载：**[Releases](https://github.com/isshiki-works/wpe-baker/releases/latest)** · 技术细节与项目记录：[技术记录](https://github.com/isshiki-works/wpe-baker/blob/main/docs/technical-notes.zh-CN.md)

## 快速开始

1. 从 [Releases](https://github.com/isshiki-works/wpe-baker/releases/latest) 下载 Windows 压缩包，**整个**解压到有写入权限的目录（例如 `D:\WpeBaker`）。不要放进 `C:\Program Files`，也不要在压缩软件窗口里直接运行。
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

从源码构建：Releases 页的源码包包含完整源码、第三方声明与构建记录，步骤见其中的 `REBUILD.md`。

## 致谢

- **hypengw**：渲染器底下的 vvk、wavsen、rstd 等库。
- **isshiki**：方向、产品决策、硬件与测试。

开发中使用了 AI 编程代理：Claude Code（Anthropic）与 OpenAI Codex。
