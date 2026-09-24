using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>
/// 捕获副本（<see cref="WorkLayout.CaptureSource"/>）：原作解包后写入冻结时间属性、快照省略与状态、关掉视差（固定视角）与
/// 镜头抖动、套循环补丁的场景，给起点搜索与各组主渲染用。成品工程另从源解包，不经这里。
/// </summary>
internal static class CaptureSourceBuilder
{
    /// <summary>
    /// 解包并写入捕获场景与 project.json。<paramref name="metadata"/> 会就地套上属性快照与场景文件名，
    /// 成品工程的 project.json 也从这份写出。摆动改频的记录写进 <paramref name="report"/>。
    /// </summary>
    internal static async Task PrepareAsync(string captureProject, ProjectSource source, JsonObject original, JsonObject metadata,
        JsonObject snapshot, JsonObject plan, HybridAnalyzeRequest settings, bool probe, JsonObject report, StageTiming timing,
        CancellationToken cancellationToken)
    {
        using (timing.Measure(StageTiming.SourceCapture)) await source.ExtractAsync(captureProject, cancellationToken);
        using (timing.Measure(StageTiming.SourceCapture))
        {
            await File.WriteAllTextAsync(ProjectSource.ContainedPath(captureProject, source.SceneResource),
                Scene(original, snapshot, plan, settings, probe).ToJsonString(), cancellationToken);
            // 摆动改频：按首个候选的 sway_retime 在捕获项目副本里写覆盖 shader，只影响捕获；成品项目另从源解包。
            if (!probe && plan["loop"]?["candidates"]?.AsArray().FirstOrDefault()?["sway_retime"] is JsonObject swayRetime)
            {
                report["sway_retime"] = swayRetime.DeepClone();
                report["sway_shader_patches"] = await ShaderTextPatch.WriteSwayRetimeAsync(captureProject, source, settings.Assets,
                    plan["loop"]!.AsObject(), cancellationToken);
            }
            ProjectWriter.ApplyPropertySnapshot(metadata, snapshot);
            metadata["file"] = source.SceneResource;
            await File.WriteAllTextAsync(Path.Combine(captureProject, "project.json"), metadata.ToJsonString(), cancellationToken);
        }
    }

    /// <summary>
    /// 捕获用场景。按状态规划的 plan（feat/daytime-split）冻结在该状态：选择器脚本不跑、本状态图层写死可见。
    /// 镜头抖动留在成品的实时镜头里，不能再烘进视频。探针不套循环补丁。
    /// </summary>
    private static JsonObject Scene(JsonObject original, JsonObject snapshot, JsonObject plan, HybridAnalyzeRequest settings, bool probe)
    {
        var scene = original.DeepClone().AsObject();
        PlanTransforms.FreezeTemporalProperties(scene, snapshot);
        PlanTransforms.ApplySnapshotOmissions(scene, plan);
        DaytimeSplit.ApplyState(scene, plan);
        // 与参照、候选工程同样把置顶的实时根挪到末尾：渲染器每个 job 只有一条全局 RNG，粒子按对象顺序取数，顺序不同就是另一次随机。
        PlanTransforms.ApplyOverlayPlacement(scene, plan);
        if (settings.ViewMode == "fixed_view") scene["general"]!["cameraparallax"] = false;
        scene["general"]!["camerashake"] = false;
        if (!probe) HybridLoopService.ApplyPatches(scene, plan["loop"]!.AsObject());
        return scene;
    }
}
