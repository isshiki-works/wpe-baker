using Xunit;

// 测试用临时目录：每次调用在 %TEMP% 下建一个 periodica-tests-*，把路径交给检查。
// 检查通过就整个删掉；检查抛异常（断言失败或崩溃）就保留目录，并把路径写进这个测试的输出
// （控制台的 Standard Output 和 trx 的 StdOut 都有），方便对着失败现场排查。
static class TestTemp
{
    public static async Task Run(Func<string, Task> check)
    {
        string dir = Directory.CreateTempSubdirectory("periodica-tests-").FullName;
        try
        {
            await check(dir);
        }
        catch
        {
            TestContext.Current.TestOutputHelper?.WriteLine("测试失败，临时目录保留在：" + dir);
            throw;
        }
        Directory.Delete(dir, true);
    }

    public static Task Run(Action<string> check) => Run(dir => { check(dir); return Task.CompletedTask; });
}
