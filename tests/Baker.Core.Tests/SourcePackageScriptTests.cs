using System.Text.RegularExpressions;
using Xunit;

// 源码包脚本（package-source.py）与渲染器源码绑定（native-provenance.py）的文本核对：两者都跑在 Windows 打包机上，CI 不执行。
[Trait("Layer", "L1")]
public class SourcePackageScriptTests
{
    private static string Script(string name) => File.ReadAllText(Path.Combine(LocalTools.RepositoryRoot, "scripts", name));

    // engine/ 以 git subtree 并入本仓库、没有自己的 .git：在那里打 git bundle 打进的是整个仓库历史。
    [Fact]
    public void SourcePackageDoesNotBundleTheWholeRepositoryAsEngine()
    {
        Assert.DoesNotContain("\"bundle\", \"create\"", Script("package-source.py"));
        Assert.DoesNotContain("engine-upstream.bundle", Script("package-source.py"));
        Assert.DoesNotContain("engine-upstream.bundle", Script("verify-package.ps1"));
    }

    // 源码包与源码绑定必须覆盖相同依赖，避免新增已链接依赖时漏掉对应源码；
    // engine/third_party 同样需要绑定，防止构建后改头文件仍判 verified-source-binding。
    [Fact]
    public void NativeProvenanceAndSourcePackageCoverTheSameDependencies()
    {
        string provenance = Script("native-provenance.py");
        static string[] Names(string text) => [.. Regex.Matches(text, "\"([a-z0-9-]+)\"").Select(m => m.Groups[1].Value)];
        string bound = Regex.Match(provenance, @"SOURCE_DEPENDENCIES = \[(.*?)\]", RegexOptions.Singleline).Groups[1].Value;
        string packaged = Regex.Match(Script("package-source.py"), @"DEPENDENCIES = \((.*?)\)", RegexOptions.Singleline).Groups[1].Value;
        Assert.NotEmpty(Names(packaged));
        Assert.NotEmpty(Names(bound));
        Assert.Empty(Names(packaged).Except(Names(bound)));
        Assert.Empty(Names(bound).Except(Names(packaged)));
        Assert.Contains("ROOT / \"engine/third_party\"", provenance);
    }
}
