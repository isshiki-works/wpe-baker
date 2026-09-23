/// <summary>shake 规则：全部断言在 tests/corpus/shader-corpus.json 的 shake。</summary>
internal static class ShaderPeriodGeneralizationChecks
{
    internal static void Run(Action<bool, string> check, string root) => ShaderCorpusChecks.Run(check, root, "shake");
}
