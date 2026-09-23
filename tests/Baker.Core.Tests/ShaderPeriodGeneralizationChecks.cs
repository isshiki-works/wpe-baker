/// <summary>
/// shake 规则与规则表逐条覆盖：全部断言在 tests/corpus/shader-corpus.json 的 shake 与 rule-coverage。
/// rule-coverage 是 C2.1b 测试消融补的例子，保证规则表每条可合成的指纹、门控与周期式被破坏时都有测试挂。
/// </summary>
internal static class ShaderPeriodGeneralizationChecks
{
    internal static void Run(Action<bool, string> check, string root)
    {
        ShaderCorpusChecks.Run(check, root, "shake");
        ShaderCorpusChecks.Run(check, root, "rule-coverage");
    }
}
