// C0.2 由 D:/Periodica/runs/C0.2/convert.py 一次性生成后提交：每个原检查组一个 [Fact]，断言不变。
// 分层：L0 纯函数/表驱动，L1 合成场景/跨模块，L2 文案表结构，L3 需要 ffmpeg、渲染器、GPU 或本机夹具（缺了就跳过并说明）。
// L3 同属一个集合、彼此串行：共用 tools.json 的渲染器与 ffmpeg，取消检查按进程名认子进程，稀疏读回改进程级环境变量。
using Xunit;

// SourceDiagnosisChecks 探测缺件报错时要临时改进程当前目录（NativeEnvironment.FindTools 只认程序目录和当前目录），
// 当前目录是进程级的，和其它测试并行会让别处的相对路径、工具查找读到临时目录。放进禁并行集合，xUnit 等其它测试跑完再单独跑它。
[CollectionDefinition(Name, DisableParallelization = true)]
public class ProcessCurrentDirectoryCollection
{
    public const string Name = "改进程当前目录";
}

[Trait("Layer", "L2")]
public class MessagesTests
{
    [Fact]
    public void Run()
    {
        MessagesChecks.Run(Assert.True);
    }
}

[Trait("Layer", "L1")]
public class PresetCascadeTests
{
    [Fact]
    public async Task Run()
    {
        await PresetCascadeChecks.RunAsync(Assert.True, Directory.CreateTempSubdirectory("periodica-tests-").FullName);
    }
}

[Trait("Layer", "L1"), Collection(ProcessCurrentDirectoryCollection.Name)]
public class SourceDiagnosisTests
{
    [Fact]
    public void Run()
    {
        SourceDiagnosisChecks.Run(Assert.True, Directory.CreateTempSubdirectory("periodica-tests-").FullName);
    }
}

[Trait("Layer", "L2")]
public class NarrativePolishTests
{
    [Fact]
    public void Run()
    {
        NarrativePolishChecks.Run(Assert.True);
    }
}

[Trait("Layer", "L0")]
public class PlaybackEncodeProfileTests
{
    [Fact]
    public void Run()
    {
        PlaybackEncodeProfileChecks.Run(Assert.True);
    }
}

[Trait("Layer", "L0")]
public class HardwareDecodeDimensionsTests
{
    [Fact]
    public void Run()
    {
        HardwareDecodeDimensionsChecks.Run(Assert.True);
    }
}

[Trait("Layer", "L2")]
public class HardwareDecodeTargetTests
{
    [Fact]
    public void Run()
    {
        HardwareDecodeTargetChecks.Run(Assert.True);
    }
}

[Trait("Layer", "L0")]
public class EmbeddedVideoBudgetTests
{
    [Fact]
    public void Run()
    {
        EmbeddedVideoBudgetChecks.Run(Assert.True);
    }
}

[Trait("Layer", "L1")]
public class OutputResolutionTests
{
    [Fact]
    public async Task Run()
    {
        await OutputResolutionChecks.RunAsync(Assert.True, Directory.CreateTempSubdirectory("periodica-tests-").FullName);
    }
}

[Trait("Layer", "L1")]
public class OutputFrameRateTests
{
    [Fact]
    public async Task Run()
    {
        await OutputFrameRateChecks.RunAsync(Assert.True, Directory.CreateTempSubdirectory("periodica-tests-").FullName);
    }
}

[Trait("Layer", "L0")]
public class StageTimingTests
{
    [Fact]
    public void Run()
    {
        StageTimingChecks.Run(Assert.True);
    }
}

[Trait("Layer", "L0")]
public class ProgressCancellationTests
{
    [Fact]
    public void Run()
    {
        ProgressCancellationChecks.Run(Assert.True);
    }
}

[Trait("Layer", "L0")]
public class EncodeSlotTests
{
    [Fact]
    public async Task Run()
    {
        await EncodeSlotChecks.RunAsync(Assert.True);
    }
}

[Trait("Layer", "L1")]
public class TemporaryCaptureTests
{
    [Fact]
    public async Task Run()
    {
        await TemporaryCaptureChecks.RunAsync(Assert.True, Directory.CreateTempSubdirectory("periodica-tests-").FullName);
    }
}

[Trait("Layer", "L1")]
public class BakeDiskBudgetTests
{
    [Fact]
    public void Run()
    {
        BakeDiskBudgetChecks.Run(Assert.True, Directory.CreateTempSubdirectory("periodica-tests-").FullName);
    }
}

[Trait("Layer", "L1")]
public class ReparsePointTests
{
    [Fact]
    public void Run()
    {
        ReparsePointChecks.Run(Assert.True, Directory.CreateTempSubdirectory("periodica-tests-").FullName);
    }
}

[Trait("Layer", "L1")]
public class SegmentedMasterRewriteTests
{
    [Fact]
    public async Task Run()
    {
        await SegmentedMasterRewriteChecks.RunAsync(Assert.True, Directory.CreateTempSubdirectory("periodica-tests-").FullName);
    }
}

[Trait("Layer", "L0")]
public class SeamPreviewTests
{
    [Fact]
    public async Task Run()
    {
        await SeamPreviewChecks.RunAsync(Assert.True);
    }
}

[Trait("Layer", "L1")]
public class CandidateExportTests
{
    [Fact]
    public async Task Run()
    {
        await CandidateExportChecks.RunAsync(Assert.True, Directory.CreateTempSubdirectory("periodica-tests-").FullName);
    }
}

[Trait("Layer", "L1")]
public class SingleShotAllocationTests
{
    [Fact]
    public async Task Run()
    {
        await SingleShotAllocationChecks.RunAsync(Assert.True, Directory.CreateTempSubdirectory("periodica-tests-").FullName);
    }
}

[Trait("Layer", "L1")]
public class DaytimeSplitTests
{
    [Fact]
    public async Task Run()
    {
        await DaytimeSplitChecks.RunAsync(Assert.True, Directory.CreateTempSubdirectory("periodica-tests-").FullName);
    }
}

[Trait("Layer", "L1")]
public class TradeoffOptionsTests
{
    [Fact]
    public async Task Run()
    {
        await TradeoffOptionsChecks.RunAsync(Assert.True, Directory.CreateTempSubdirectory("periodica-tests-").FullName);
    }
}

[Trait("Layer", "L2")]
public class GuiPresetTradeoffTests
{
    [Fact]
    public void Run()
    {
        GuiPresetTradeoffChecks.Run(Assert.True);
    }
}

[Trait("Layer", "L2")]
public class PlainLanguageTests
{
    [Fact]
    public void Run()
    {
        PlainLanguageChecks.Run(Assert.True);
    }
}

[Trait("Layer", "L0")]
public class SourcePowerVerdictTests
{
    [Fact]
    public void Run()
    {
        SourcePowerVerdictChecks.Run(Assert.True);
    }
}

[Trait("Layer", "L1")]
public class FullFrameDemotionTests
{
    [Fact]
    public async Task Run()
    {
        await FullFrameDemotionChecks.RunAsync(Assert.True, Directory.CreateTempSubdirectory("periodica-tests-").FullName);
    }
}

[Trait("Layer", "L0")]
public class HybridCaptureViewportTests
{
    [Fact]
    public void Run()
    {
        HybridCaptureViewportChecks.Run(Assert.True);
    }
}

[Trait("Layer", "L0")]
public class HybridLoopAllocationTests
{
    [Fact]
    public void Run()
    {
        HybridLoopAllocationChecks.Run(Assert.True);
    }
}

[Trait("Layer", "L1")]
public class SdrRadianceClosureTests
{
    [Fact]
    public async Task Run()
    {
        await SdrRadianceClosureChecks.RunAsync(Assert.True, Directory.CreateTempSubdirectory("periodica-tests-").FullName);
    }
}

[Trait("Layer", "L0")]
public class LoopPreferenceTests
{
    [Fact]
    public void Run()
    {
        LoopPreferenceChecks.Run(Assert.True);
    }
}

[Trait("Layer", "L1")]
public class LoopCeilingTests
{
    [Fact]
    public void Run()
    {
        LoopCeilingChecks.Run(Assert.True, Directory.CreateTempSubdirectory("periodica-tests-").FullName);
    }
}

[Trait("Layer", "L0")]
public class EffectPrefixCaptureTargetTests
{
    [Fact]
    public void Run()
    {
        EffectPrefixCaptureTargetChecks.Run(Assert.True);
    }
}

[Trait("Layer", "L1")]
public class VideoDominanceTests
{
    [Fact]
    public async Task Run()
    {
        await VideoDominanceChecks.RunAsync(Assert.True, Directory.CreateTempSubdirectory("periodica-tests-").FullName);
    }
}

[Trait("Layer", "L1")]
public class ResidualLayoutGateTests
{
    [Fact]
    public async Task Run()
    {
        await ResidualLayoutGateChecks.RunAsync(Assert.True, Directory.CreateTempSubdirectory("periodica-tests-").FullName);
    }
}

[Trait("Layer", "L0")]
public class ScriptRootAssemblyTests
{
    [Fact]
    public void Run()
    {
        ScriptRootAssemblyChecks.Run(Assert.True);
    }
}

[Trait("Layer", "L1")]
public class AnalysisToolLimitationTests
{
    [Fact]
    public async Task Run()
    {
        await AnalysisToolLimitationChecks.RunAsync(Assert.True, Directory.CreateTempSubdirectory("periodica-tests-").FullName);
    }
}

[Trait("Layer", "L1")]
public class ProgramInlineTests
{
    [Fact]
    public async Task Run()
    {
        await ProgramInlineChecks.RunAsync(Assert.True, Directory.CreateTempSubdirectory("periodica-tests-").FullName);
    }
}

[Trait("Layer", "L0")]
public class HybridVideoWorkloadTests
{
    [Fact]
    public void Run()
    {
        HybridVideoWorkloadChecks.Run(Assert.True);
    }
}

[Trait("Layer", "L1")]
public class HybridPlanFormatTests
{
    [Fact]
    public async Task Run()
    {
        await HybridPlanFormatChecks.RunAsync(Assert.True, Directory.CreateTempSubdirectory("periodica-tests-").FullName);
    }
}

[Trait("Layer", "L1")]
public class ExactVideoLoopTests
{
    [Fact]
    public void Run()
    {
        ExactVideoLoopChecks.Run(Assert.True, Directory.CreateTempSubdirectory("periodica-tests-").FullName);
    }
}

[Trait("Layer", "L1")]
public class SpriteSeamPhaseTests
{
    [Fact]
    public void Run()
    {
        SpriteSeamPhaseChecks.Run(Assert.True, Directory.CreateTempSubdirectory("periodica-tests-").FullName);
    }
}

[Trait("Layer", "L0")]
public class LoopWarmupStackingTests
{
    [Fact]
    public void Run()
    {
        LoopWarmupStackingChecks.Run(Assert.True);
    }
}

[Trait("Layer", "L0")]
public class SourceStartOffsetTests
{
    [Fact]
    public void Run()
    {
        SourceStartOffsetChecks.Run(Assert.True);
    }
}

[Trait("Layer", "L1")]
public class LoopFixTests
{
    [Fact]
    public void Run()
    {
        LoopFixChecks.Run(Assert.True, Directory.CreateTempSubdirectory("periodica-tests-").FullName);
    }
}

[Trait("Layer", "L0")]
public class SavedLoopApplyTests
{
    [Fact]
    public void Run()
    {
        SavedLoopApplyChecks.Run(Assert.True);
    }
}

[Trait("Layer", "L1")]
public class CanonicalRepeatNoiseTests
{
    [Fact]
    public void Run()
    {
        CanonicalRepeatNoiseChecks.Run(Assert.True, Directory.CreateTempSubdirectory("periodica-tests-").FullName);
    }
}

[Trait("Layer", "L1")]
public class SourceStaticLoopTests
{
    [Fact]
    public void Run()
    {
        SourceStaticLoopChecks.Run(Assert.True, Directory.CreateTempSubdirectory("periodica-tests-").FullName);
    }
}

[Trait("Layer", "L1")]
public class SuitabilityVerdictTests
{
    [Fact]
    public void Run()
    {
        SuitabilityVerdictChecks.Run(Assert.True, Directory.CreateTempSubdirectory("periodica-tests-").FullName);
    }
}

[Trait("Layer", "L1")]
public class HybridLoopGeneralizationTests
{
    [Fact]
    public void Run()
    {
        HybridLoopGeneralizationChecks.Run(Assert.True, Directory.CreateTempSubdirectory("periodica-tests-").FullName);
    }
}

[Trait("Layer", "L1")]
public class ShaderPeriodGeneralizationTests
{
    [Fact]
    public void Run()
    {
        ShaderPeriodGeneralizationChecks.Run(Assert.True, Directory.CreateTempSubdirectory("periodica-tests-").FullName);
    }
}

[Trait("Layer", "L1")]
public class ShaderEffectivePassTests
{
    [Fact]
    public void Run()
    {
        ShaderEffectivePassChecks.Run(Assert.True, Directory.CreateTempSubdirectory("periodica-tests-").FullName);
    }
}

[Trait("Layer", "L1")]
public class ShaderAdditionalPeriodTests
{
    [Fact]
    public void Run()
    {
        ShaderAdditionalPeriodChecks.Run(Assert.True, Directory.CreateTempSubdirectory("periodica-tests-").FullName);
    }
}

[Trait("Layer", "L1")]
public class WaterRippleSineClockTests
{
    [Fact]
    public void Run()
    {
        WaterRippleSineClockChecks.Run(Assert.True, Directory.CreateTempSubdirectory("periodica-tests-").FullName);
    }
}

[Trait("Layer", "L1")]
public class ShaderSplitPeriodTests
{
    [Fact]
    public void Run()
    {
        ShaderSplitPeriodChecks.Run(Assert.True, Directory.CreateTempSubdirectory("periodica-tests-").FullName);
    }
}

[Trait("Layer", "L1")]
public class WaterFlowPeriodTests
{
    [Fact]
    public void Run()
    {
        WaterFlowPeriodChecks.Run(Assert.True, Directory.CreateTempSubdirectory("periodica-tests-").FullName);
    }
}

[Trait("Layer", "L0")]
public class ResidualMaskingTests
{
    [Fact]
    public void Run()
    {
        ResidualMaskingChecks.Run(Assert.True);
    }
}

[Trait("Layer", "L1")]
public class SwayRetimeTests
{
    [Fact]
    public async Task Run()
    {
        await SwayRetimeChecks.RunAsync(Assert.True, Directory.CreateTempSubdirectory("periodica-tests-").FullName);
    }
}

[Trait("Layer", "L0")]
public class RetimeBudgetTests
{
    [Fact]
    public void Run()
    {
        RetimeBudgetChecks.Run(Assert.True);
    }
}

[Trait("Layer", "L1")]
public class EffectPrefixProfileTests
{
    [Fact]
    public void Run()
    {
        EffectPrefixProfileChecks.Run(Assert.True, Directory.CreateTempSubdirectory("periodica-tests-").FullName);
    }
}

[Trait("Layer", "L1")]
public class HybridHierarchyGeneralizationTests
{
    [Fact]
    public async Task Run()
    {
        await HybridHierarchyGeneralizationChecks.RunAsync(Assert.True, Directory.CreateTempSubdirectory("periodica-tests-").FullName);
    }
}

[Trait("Layer", "L1")]
public class OverlayExclusionTests
{
    [Fact]
    public async Task Run()
    {
        await OverlayExclusionChecks.RunAsync(Assert.True, Directory.CreateTempSubdirectory("periodica-tests-").FullName);
    }
}

[Trait("Layer", "L1")]
public class WallpaperEnginePropertiesTests
{
    [Fact]
    public async Task Run()
    {
        await WallpaperEnginePropertiesChecks.RunAsync(Assert.True, Directory.CreateTempSubdirectory("periodica-tests-").FullName);
    }
}

[Trait("Layer", "L1")]
public class ParticleRealtimeTests
{
    [Fact]
    public async Task Run()
    {
        await ParticleRealtimeChecks.RunAsync(Assert.True, Directory.CreateTempSubdirectory("periodica-tests-").FullName);
    }
}

[Trait("Layer", "L3"), Collection("L3 本机工具")]
public class ParticleStationarityTests
{
    [Fact]
    public void Run()
    {
        Assert.SkipUnless(File.Exists(ParticleStationarityChecks.SurveyPath), "缺本机粒子普查夹具：" + ParticleStationarityChecks.SurveyPath + "（用 WPE_PARTICLE_SURVEY 指定目录）");
        ParticleStationarityChecks.Run(Assert.True, Directory.CreateTempSubdirectory("periodica-tests-").FullName);
    }
}

[Trait("Layer", "L1")]
public class NativeFrameSampleTests
{
    [Fact]
    public async Task Run()
    {
        await NativeFrameSampleChecks.RunAsync(Assert.True, Directory.CreateTempSubdirectory("periodica-tests-").FullName);
    }
}

[Trait("Layer", "L1")]
public class OpaqueCaptureRejectionTests
{
    [Fact]
    public async Task Run()
    {
        await OpaqueCaptureRejectionChecks.RunAsync(Assert.True, Directory.CreateTempSubdirectory("periodica-tests-").FullName);
    }
}

[Trait("Layer", "L0")]
public class FrameScanTests
{
    [Fact]
    public void Run()
    {
        FrameScanChecks.Run(Assert.True);
    }
}

[Trait("Layer", "L3"), Collection("L3 本机工具")]
public class ExactFrameRangeTests
{
    [Fact]
    public async Task Run()
    {
        Assert.SkipUnless(LocalTools.Tools is not null, LocalTools.Missing);
        await ExactFrameRangeChecks.RunAsync(Assert.True, Directory.CreateTempSubdirectory("periodica-tests-").FullName);
    }
}

[Trait("Layer", "L3"), Collection("L3 本机工具")]
public class ReferenceSeamTests
{
    [Fact]
    public async Task Run()
    {
        Assert.SkipUnless(LocalTools.Tools is not null, LocalTools.Missing);
        await ReferenceSeamChecks.RunAsync(Assert.True, Directory.CreateTempSubdirectory("periodica-tests-").FullName);
    }
}

[Trait("Layer", "L0")]
public class OfficialTraceLossTests
{
    [Fact]
    public void Run()
    {
        OfficialTraceLossChecks.Run(Assert.True);
    }
}

[Trait("Layer", "L0")]
public class SharedLoopStartTests
{
    [Fact]
    public void Run()
    {
        SharedLoopStartChecks.Run(Assert.True);
    }
}

[Trait("Layer", "L3"), Collection("L3 本机工具")]
public class NativeGpuEncodeTests
{
    [Fact]
    public async Task Run()
    {
        Assert.SkipUnless(LocalTools.Tools is not null, LocalTools.Missing);
        await NativeGpuEncodeChecks.RunAsync(Path.Combine(Directory.CreateTempSubdirectory("periodica-tests-").FullName, "out"));
    }
}

[Trait("Layer", "L3"), Collection("L3 本机工具")]
public class NativeSamplingCoverageTests
{
    [Fact]
    public async Task Run()
    {
        Assert.SkipUnless(LocalTools.Tools is not null, LocalTools.Missing);
        await SparseReadbackChecks.RunCoverageAsync(Path.Combine(Directory.CreateTempSubdirectory("periodica-tests-").FullName, "out"));
    }
}

[Trait("Layer", "L3"), Collection("L3 本机工具")]
public class NativeSparseReadbackTests
{
    [Fact]
    public async Task Run()
    {
        Assert.SkipUnless(LocalTools.Tools is not null, LocalTools.Missing);
        Assert.SkipUnless(LocalTools.ReferenceRenderer is not null, "tools.json 没有 reference_renderer（对照用的旧版 wpe-render.exe）");
        await SparseReadbackChecks.RunNativeAsync(Path.Combine(Directory.CreateTempSubdirectory("periodica-tests-").FullName, "out"));
    }
}

[Trait("Layer", "L3"), Collection("L3 本机工具")]
public class NativeProgressCancelTests
{
    [Fact]
    public async Task Run()
    {
        Assert.SkipUnless(LocalTools.Tools is not null, LocalTools.Missing);
        await ProgressCancellationChecks.RunNativeAsync(Path.Combine(Directory.CreateTempSubdirectory("periodica-tests-").FullName, "out"));
    }
}

[Trait("Layer", "L0")]
public class CommonLoopSolverCaseTests
{
    [Fact]
    public void Run()
    {
        CommonLoopSolverCaseChecks.Run(Assert.True);
    }
}

[Trait("Layer", "L1")]
public class HybridCompositionValidatorCaseTests
{
    [Fact]
    public async Task Run()
    {
        await HybridCompositionValidatorCaseChecks.RunAsync(Assert.True, Directory.CreateTempSubdirectory("periodica-tests-").FullName);
    }
}
