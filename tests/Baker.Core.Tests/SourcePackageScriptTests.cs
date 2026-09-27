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

    // 源码包按 DEPENDENCIES 与整个 engine/ 打包；源码绑定要覆盖同样的依赖和 engine/third_party，
    // 否则构建后改了这些头文件，包里的源码与二进制对不上也照样判 verified-source-binding。
    [Fact]
    public void NativeProvenanceCoversEverySourcePackagedDependency()
    {
        string provenance = Script("native-provenance.py");
        static string[] Names(string text) => [.. Regex.Matches(text, "\"([a-z0-9-]+)\"").Select(m => m.Groups[1].Value)];
        string bound = Regex.Match(provenance, @"SOURCE_DEPENDENCIES = \[(.*?)\]", RegexOptions.Singleline).Groups[1].Value;
        string packaged = Regex.Match(Script("package-source.py"), @"DEPENDENCIES = \((.*?)\)", RegexOptions.Singleline).Groups[1].Value;
        Assert.NotEmpty(Names(packaged));
        Assert.Empty(Names(packaged).Except(Names(bound)));
        Assert.Contains("ROOT / \"engine/third_party\"", provenance);
    }
}
