/// <summary>material → definition → 作者 pass 的 combo 合并与 shine 两 pass 规则：全部断言在 tests/corpus/shader-corpus.json 的 effective-pass。</summary>
internal static class ShaderEffectivePassChecks
{
    internal static void Run(Action<bool, string> check, string root)
    {
        ShaderCorpusChecks.Run(check, root, "effective-pass");
        // 工程自带真实尺寸（约 200 KB）的 clouds_256：只读头部即可证明 repeat 寻址；ClampUVs 版仍要拒。
        ShaderCorpusChecks.Run(check, root, "bundled-clouds");
        ShaderCorpusChecks.Run(check, root, "bundled-clouds-clamp");
    }
}
