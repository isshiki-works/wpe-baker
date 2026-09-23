#pragma once

// 逐 pass GPU 计时（R7a）的输出格式与格式化函数。只用标准库，VulkanRender.cpp 与单测共用。
//
// 开关：环境变量 WPE_PASS_TIMING=<输出 jsonl 路径>，只在 CPU 读回（离线）模式生效，不进 job 格式。
// 不设时渲染器不建查询池、不写时间戳，录制内容与调度都和没有这个功能时相同。
// 设了也不改调度：时间戳结果在渲染器本来就要等的帧栅栏之后读——同步帧在本帧，
// 流水线帧（GPU 编码、覆盖率搜索）在下一帧开头的 finishPendingFrame 里——不额外等 GPU。
//
// 输出格式 pass-timing/1：UTF-8 jsonl，每个完成的帧一行，按帧序。例（折行只为排版）：
//   {"schema":"pass-timing/1","frame":12,"gpu_ns":4211375,"uploads_ns":10240,"passes":[
//     {"pass":"effects/shine_cast","layer":32,"role":"effect","effect":"ui_editor_effect_shine_title",
//      "effect_index":0,"rt":"_rt_effect_5__rt_HalfCompoBuffer2","w":2409,"h":1682,"gpu_ns":183229},...]}
// 帧字段：
//   frame        渲染器帧序号，从 0 起，含预热帧
//   gpu_ns       本帧渲染图录制段的 GPU 时间（第一个时间戳到最后一个）
//   uploads_ns   录制开头动态缓冲上传占的时间
//   passes       本帧实际录制的 pass，按渲染程序的 pass 顺序（也就是录制顺序）；未就绪、没录制的不出现
// pass 字段：
//   pass         pass 名（着色器名或内部名，如 frame/pre、copy）
//   layer        所属图层 id（场景对象 id；效果节点记宿主图层）；无对应场景节点时 -1
//   role         draw | effect | prefill | final-resolve | published | visible-resolve；无场景节点时 ""
//   effect       效果名（效果类 role）；否则 ""
//   effect_index 效果在图层效果列表里的序号；role 不是 effect 时 -1
//   rt           输出渲染目标名；w、h 是它的像素尺寸（copy pass 取源图尺寸）；无输出目标时 ""、0、0
//   gpu_ns       该 pass 的 GPU 时间
// 时间口径：每录完一个 pass（合并 render pass 里每次 draw 与 scope 结束各算一次）写一个 BOTTOM_OF_PIPE
// 时间戳，相邻两个时间戳之差记在后者名下，scope 结束的 store 记在该 scope 最后一个 pass 名下。
// GPU 可以让相邻 pass 重叠执行，所以单个 pass 的值是"完成时刻之差"，不是独占执行时间。
// 纳秒按累计时刻取整后再相减，所以各 pass 的 gpu_ns 与 uploads_ns 之和恰好等于帧的 gpu_ns。
// 汇总工具：tools/pass-timing/pass_timing.py。

#include <cmath>
#include <cstdint>
#include <cstdio>
#include <span>
#include <string>
#include <string_view>
#include <vector>

namespace owe::vulkan::pass_timing
{

// 一个 pass 的标注，下标与渲染程序的 pass 列表一致。
struct Pass {
    std::string   name;
    std::int32_t  layer { -1 };
    std::string   role;
    std::string   effect;
    std::int64_t  effect_index { -1 };
    std::string   rt;
    std::uint32_t width { 0 };
    std::uint32_t height { 0 };
};

inline void AppendJsonString(std::string& out, std::string_view text) {
    out.push_back('"');
    for (const unsigned char c : text) {
        if (c == '"' || c == '\\') {
            out.push_back('\\');
            out.push_back(static_cast<char>(c));
        } else if (c < 0x20) {
            char escaped[8];
            std::snprintf(escaped, sizeof(escaped), "\\u%04x", c);
            out += escaped;
        } else {
            out.push_back(static_cast<char>(c)); // UTF-8 原样输出
        }
    }
    out.push_back('"');
}

// 一帧的 jsonl 行（不含换行）。区间 k 是第 k 个到第 k+1 个时间戳，长 interval_ticks[k]，
// 归属 labels[k]：非负数为 passes 下标，负数为动态缓冲上传。period_ns 是一个时间戳刻度的纳秒数。
inline std::string FrameLine(std::uint64_t frame, std::span<const Pass> passes,
                             std::span<const std::int64_t> labels,
                             std::span<const std::uint64_t> interval_ticks, double period_ns) {
    std::vector<std::uint64_t> pass_ns(passes.size(), 0);
    std::vector<bool>          recorded(passes.size(), false);
    std::uint64_t              ticks = 0, ns = 0, uploads_ns = 0;
    for (std::size_t k = 0; k < labels.size(); ++k) {
        ticks += interval_ticks[k];
        const auto now = static_cast<std::uint64_t>(std::llround(static_cast<double>(ticks) * period_ns));
        const auto spent = now - ns;
        ns = now;
        if (labels[k] < 0) {
            uploads_ns += spent;
            continue;
        }
        const auto index = static_cast<std::size_t>(labels[k]);
        recorded[index]  = true;
        pass_ns[index] += spent;
    }

    std::string line = "{\"schema\":\"pass-timing/1\",\"frame\":" + std::to_string(frame) +
                       ",\"gpu_ns\":" + std::to_string(ns) + ",\"uploads_ns\":" + std::to_string(uploads_ns) +
                       ",\"passes\":[";
    bool first = true;
    for (std::size_t i = 0; i < passes.size(); ++i) {
        if (! recorded[i]) continue;
        const auto& pass = passes[i];
        line += first ? "{\"pass\":" : ",{\"pass\":";
        first = false;
        AppendJsonString(line, pass.name);
        line += ",\"layer\":" + std::to_string(pass.layer) + ",\"role\":";
        AppendJsonString(line, pass.role);
        line += ",\"effect\":";
        AppendJsonString(line, pass.effect);
        line += ",\"effect_index\":" + std::to_string(pass.effect_index) + ",\"rt\":";
        AppendJsonString(line, pass.rt);
        line += ",\"w\":" + std::to_string(pass.width) + ",\"h\":" + std::to_string(pass.height) +
                ",\"gpu_ns\":" + std::to_string(pass_ns[i]) + "}";
    }
    line += "]}";
    return line;
}

} // namespace owe::vulkan::pass_timing
