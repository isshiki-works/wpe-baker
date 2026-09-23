/// <summary>material → definition → 作者 pass 的 combo 合并与 shine 两 pass 规则：全部断言在 tests/corpus/shader-corpus.json 的 effective-pass。</summary>
internal static class ShaderEffectivePassChecks
{
    internal static void Run(Action<bool, string> check, string root) => ShaderCorpusChecks.Run(check, root, "effective-pass");
}
