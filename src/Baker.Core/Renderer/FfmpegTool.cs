using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Baker.Core;

/// <summary>
/// 全仓唯一的外部进程封装（wpe-render / ffmpeg / ffprobe）。启动参数（PATH 前置运行库目录、不开窗口、重定向）、
/// stdout/stderr 的编码（UTF-8）、取消（杀整棵进程树）与退出后回收在这里各只有一份。
/// 超时不另设参数：调用方给的 token 就是超时（硬解探测用链接的 30 秒 token），取消与超时走同一条路。
/// 四种跑法对应原来四处自带的封装：stderr 落日志文件、stderr 收进字符串、stdout 定长字节、stdout/stderr 都落文件；
/// 需要边跑边读 stdout 的（渲染器帧流、渲染→编码管道、按帧号解码）用 <see cref="Start"/> 拿到进程自己读。
/// </summary>
internal sealed class FfmpegTool(NativeTools tools)
{
    private static readonly Encoding Utf8 = new UTF8Encoding(false);

    public NativeTools Tools => tools;

    /// <summary>启动一个进程；token 取消时杀掉整棵进程树。释放时先杀（若还在跑）再等它退出。</summary>
    public NativeProcess Start(string executable, IEnumerable<string> arguments, CancellationToken token, bool redirectInput = false)
    {
        var info = new ProcessStartInfo(Path.GetFullPath(executable)) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = redirectInput,
            StandardOutputEncoding = Utf8, StandardErrorEncoding = Utf8 };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        info.Environment["PATH"] = string.Join(Path.PathSeparator, tools.RuntimeDirectories.Select(Path.GetFullPath)) +
            Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        var process = new Process { StartInfo = info };
        try
        {
            if (!process.Start()) throw new IOException($"Could not start {Path.GetFileName(executable)}.");
        }
        catch { process.Dispose(); throw; }
        return new NativeProcess(process, token);
    }

    /// <summary>
    /// stdout 收成文本返回，stderr 原样落日志（给了 <paramref name="stderrLine"/> 就逐行转写并回调，用于进度行）。
    /// 非 ffprobe 进程先做磁盘余量检查并在写日志时持续盯余量。退出码非 0 抛 IOException 指向日志。
    /// </summary>
    public async Task<string> RunTextAsync(string executable, string[] arguments, string logPath, CancellationToken token,
        Action<string>? stderrLine = null)
    {
        bool probe = string.Equals(executable, tools.Ffprobe, StringComparison.OrdinalIgnoreCase);
        if (!probe) TemporaryCaptureFiles.RequireFreeSpace(logPath);
        await using var log = new FileStream(logPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        await using NativeProcess run = Start(executable, arguments, token);
        Process process = run.Process;
        async Task DrainErrorsAsync()
        {
            try
            {
                if (stderrLine is null) await process.StandardError.BaseStream.CopyToAsync(log, CancellationToken.None);
                else
                {
                    await using var writer = new StreamWriter(log, Utf8, leaveOpen: true) { AutoFlush = true };
                    while (await process.StandardError.ReadLineAsync(CancellationToken.None) is { } line)
                    {
                        await writer.WriteLineAsync(line);
                        stderrLine(line);
                    }
                }
            }
            catch { run.Stop(); throw; }
        }
        Task stderr = DrainErrorsAsync();
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(token);
        try
        {
            Task completed = Task.WhenAll(process.WaitForExitAsync(token), stderr, stdout);
            if (probe) await completed;
            else await TemporaryCaptureFiles.WaitWhileWritingAsync(completed, logPath, token);
            if (process.ExitCode != 0) throw new IOException($"{Path.GetFileName(executable)} exited {process.ExitCode}; see {logPath}");
            return stdout.Result;
        }
        catch
        {
            run.Stop();
            try { await Task.WhenAll(stderr, stdout); } catch { }
            throw;
        }
    }

    /// <summary>stdout 收成文本返回，stderr 收进字符串；退出码非 0 抛 InvalidDataException 并带上 stderr 摘要。不写日志文件。</summary>
    public async Task<string> RunCapturedAsync(string executable, string[] arguments, CancellationToken token)
    {
        await using NativeProcess run = Start(executable, arguments, token);
        Process process = run.Process;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(token), stderr = process.StandardError.ReadToEndAsync(token);
        await Task.WhenAll(process.WaitForExitAsync(token), stdout, stderr);
        if (process.ExitCode != 0) throw new InvalidDataException($"ffprobe failed: {Trim(stderr.Result)}");
        return stdout.Result;
    }

    /// <summary>跑一次输出原始字节到 stdout 的 ffmpeg；字节数必须正好等于 <paramref name="expectedBytes"/>。</summary>
    public async Task<byte[]> RunBytesAsync(IEnumerable<string> arguments, long expectedBytes, CancellationToken token)
    {
        if (!File.Exists(tools.Ffmpeg)) throw new FileNotFoundException("Required native tool is missing.", tools.Ffmpeg);
        if (expectedBytes <= 0 || expectedBytes > Array.MaxLength) throw new ArgumentException("Raw ffmpeg output must fit one managed buffer.");
        await using NativeProcess run = Start(tools.Ffmpeg, arguments, token);
        Process process = run.Process;
        Task<string> stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        byte[] data = new byte[expectedBytes];
        int read = 0;
        while (read < data.Length)
        {
            int count = await process.StandardOutput.BaseStream.ReadAsync(data.AsMemory(read), token);
            if (count == 0) break;
            read += count;
        }
        bool extra = read == data.Length && await process.StandardOutput.BaseStream.ReadAsync(new byte[1], token) != 0;
        await Task.WhenAll(process.WaitForExitAsync(token), stderr);
        if (process.ExitCode != 0) throw new InvalidDataException($"ffmpeg exited {process.ExitCode}: {Trim(stderr.Result)}");
        if (read != data.Length || extra)
            throw new InvalidDataException($"ffmpeg produced {(extra ? "more than" : read.ToString(CultureInfo.InvariantCulture))} bytes; expected exactly {expectedBytes}.");
        return data;
    }

    /// <summary>stdout 与 stderr 都原样落文件（两个文件都必须是新建的），返回退出码，不按退出码抛异常。</summary>
    public async Task<int> RunToFilesAsync(string executable, string[] arguments, string stdoutPath, string stderrPath, CancellationToken token)
    {
        await using var stdout = new FileStream(stdoutPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        await using var stderr = new FileStream(stderrPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        token.ThrowIfCancellationRequested();
        await using NativeProcess run = Start(executable, arguments, token);
        Process process = run.Process;
        await Task.WhenAll(process.StandardOutput.BaseStream.CopyToAsync(stdout, CancellationToken.None),
            process.StandardError.BaseStream.CopyToAsync(stderr, CancellationToken.None), process.WaitForExitAsync(token));
        return process.ExitCode;
    }

    internal static string Trim(string text) => text.Length <= 2048 ? text.Trim() : text[..2048].Trim() + "…";
}

/// <summary><see cref="FfmpegTool.Start"/> 起的进程：取消即杀进程树；释放时杀掉还在跑的并等它退出。</summary>
internal sealed class NativeProcess : IAsyncDisposable
{
    private readonly CancellationTokenRegistration cancelled;

    internal NativeProcess(Process process, CancellationToken token)
    {
        Process = process;
        cancelled = token.Register(Stop);
    }

    public Process Process { get; }

    /// <summary>杀掉整棵进程树；已退出或从未启动时什么也不做。</summary>
    public void Stop()
    {
        try { if (!Process.HasExited) Process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
    }

    /// <summary>杀掉（若还在跑）并等它退出；需要在释放前先收尾 stderr 的调用方用它。</summary>
    public async Task StopAndWaitAsync()
    {
        Stop();
        try { await Process.WaitForExitAsync(CancellationToken.None); }
        catch (InvalidOperationException) { } // 管道里的第二个进程可能根本没起来。
    }

    public async ValueTask DisposeAsync()
    {
        await cancelled.DisposeAsync();
        await StopAndWaitAsync();
        Process.Dispose();
    }
}
