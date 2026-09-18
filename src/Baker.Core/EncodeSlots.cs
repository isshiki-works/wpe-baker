using System.Diagnostics;

namespace Baker.Core;

/// <summary>
/// 成品编码的跨进程槽位配额：同一台机器上同时处于成品编码阶段的烘焙进程不超过配额数。
/// 多槽并行跑批时，一个案的渲染器只占约 1 个核，成品编码那一路 ffmpeg 峰值要十几个核，
/// 槽一多就是编码在互相抢 CPU。这里只卡成品编码，渲染与无损 master 编码不受限制。
///
/// 用 N 个 Global\ 命名互斥体而不是一个命名信号量：看门狗硬杀一个案时互斥体会被系统标成 abandoned，
/// 下一个等待者照样拿得到；命名信号量的计数不随进程退出归还，被杀一次就永久少一个槽，
/// 杀够 N 次整批编码全部死锁。互斥体要求谁申请谁释放，所以每个租约由一个专用线程持有，
/// 烘焙流程只等它把租约交出来。名字里带上配额数：配额不同的两批不会混用同一组槽位。
/// </summary>
public sealed class EncodeSlots
{
    /// <summary>默认的槽位名前缀；同名同配额的进程共用一组槽位。</summary>
    public const string DefaultName = "WpeBakerEncodePlayback";

    /// <summary>WaitHandle.WaitAny 一次最多等 64 个句柄，配额不能超过这个数。</summary>
    public const int MaximumQuota = 64;

    private const int PollMilliseconds = 250;

    /// <summary>不限并发的配额；Quota 为 0，取租约不等待。</summary>
    public static EncodeSlots Unlimited { get; } = new(0, DefaultName);

    /// <summary>0 表示不限并发。</summary>
    public int Quota { get; }

    private readonly string name;

    private EncodeSlots(int quota, string name) { Quota = quota; this.name = name; }

    /// <summary>配额 ≤ 0 当作不限；负数按不限处理会掩盖笔误，所以直接拒绝。</summary>
    public static EncodeSlots Create(int quota, string? name = null)
    {
        if (quota < 0) throw new ArgumentOutOfRangeException(nameof(quota), "成品编码槽位配额不能是负数。");
        if (quota > MaximumQuota) throw new ArgumentOutOfRangeException(nameof(quota), $"成品编码槽位配额最多 {MaximumQuota} 个。");
        return quota == 0 ? Unlimited : new(quota, string.IsNullOrWhiteSpace(name) ? DefaultName : name!);
    }

    /// <summary>这个配额下第 index 个槽位的内核对象名，从 1 开始数。</summary>
    public string HandleName(int index) => $@"Global\{name}-{index}-of-{Quota}";

    /// <summary>取一个槽位；不限并发时立即返回一个等待 0 秒的空租约。租约 Dispose 时归还槽位。</summary>
    public Task<Lease> AcquireAsync(CancellationToken cancellationToken = default)
    {
        if (Quota == 0) return Task.FromResult(Lease.Free);
        cancellationToken.ThrowIfCancellationRequested();
        var acquired = new TaskCompletionSource<Lease>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new ManualResetEventSlim(false);
        var thread = new Thread(() => Hold(this, acquired, release, cancellationToken))
        {
            IsBackground = true,
            Name = "wpe-baker encode slot"
        };
        thread.Start();
        return acquired.Task;
    }

    private static void Hold(EncodeSlots owner, TaskCompletionSource<Lease> acquired,
        ManualResetEventSlim release, CancellationToken cancellationToken)
    {
        var handles = new Mutex[owner.Quota];
        bool handedOver = false;
        try
        {
            for (int i = 0; i < handles.Length; ++i) handles[i] = new Mutex(false, owner.HandleName(i + 1));
            long started = Stopwatch.GetTimestamp();
            int index;
            while (true)
            {
                try { index = WaitHandle.WaitAny(handles, PollMilliseconds); }
                // 上一个持有者进程被杀掉了：槽位照样归这次等待，没有永久泄漏。
                catch (AbandonedMutexException abandoned) { index = abandoned.MutexIndex; }
                if (index != WaitHandle.WaitTimeout) break;
                if (cancellationToken.IsCancellationRequested) { acquired.TrySetCanceled(cancellationToken); return; }
            }
            var lease = new Lease(release, Stopwatch.GetElapsedTime(started).TotalSeconds);
            // 只有把租约交出去才继续持有；交不出去（已取消）就当场归还。
            if (acquired.TrySetResult(lease)) { handedOver = true; release.Wait(); }
            handles[index].ReleaseMutex();
        }
        catch (Exception error)
        {
            if (!handedOver) acquired.TrySetException(error);
        }
        finally
        {
            foreach (var handle in handles) handle?.Dispose();
            release.Dispose();
        }
    }

    /// <summary>一个成品编码槽位的租约；Dispose 归还。</summary>
    public sealed class Lease : IDisposable
    {
        internal static readonly Lease Free = new(null, 0);

        /// <summary>等到这个槽位花掉的墙钟秒；不限并发时是 0。</summary>
        public double WaitSeconds { get; }

        private readonly ManualResetEventSlim? release;
        private int disposed;

        internal Lease(ManualResetEventSlim? release, double waitSeconds)
        {
            this.release = release;
            WaitSeconds = waitSeconds;
        }

        public void Dispose()
        {
            if (release is null || Interlocked.Exchange(ref disposed, 1) != 0) return;
            release.Set();
        }
    }
}
