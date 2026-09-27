using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>
/// 一次 analyze（<see cref="AnalysisOrchestrator.RunAsync"/>）内各编排器与子分析共用的状态：退回重查的序号，
/// 以及退回、档位、布局、状态各轮之间输入不变的中间结果（源哈希、scene 母本、观测 trace、Liveness、逐组静态证明），每个键只算一次。
/// 同一个键并发请求时只算一次（退回轮内并行），算失败的不记，下次照常重算。
/// 母本只由这里持有：JSON 交出去的是 DeepClone，Liveness 交出去的是新副本，调用方可以随意改写。
/// </summary>
/// <param name="retreatParallelism">退回一轮里同时重查的组数上限（实际取 min(组数, 它)）；测试传 1 得到串行结果做对照。</param>
internal sealed class AnalysisMemo(int retreatParallelism = 8)
{
    private readonly ConcurrentDictionary<string, Lazy<Task<object>>> entries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> hashed = new(StringComparer.OrdinalIgnoreCase);

    internal int RetreatParallelism { get; } = retreatParallelism;
    /// <summary>退回重查的次数：子分析目录（retreat-N）与进度序号都按它，主路径与关交互试探的退回不撞。</summary>
    internal int Retreats;

    private async Task<T> OnceAsync<T>(string key, Func<Task<T>> create, Func<T, bool>? keep = null) where T : class
    {
        var entry = entries.GetOrAdd(key, _ => new Lazy<Task<object>>(async () => await create()));
        T value;
        try { value = (T)await entry.Value; }
        catch
        {
            entries.TryRemove(new(key, entry));
            throw;
        }
        if (keep is not null && !keep(value)) entries.TryRemove(new(key, entry));
        return value;
    }

    private static JsonObject Clone(JsonObject master)
    {
        // 解析出来的 JsonObject 首次访问才展开，并发读不安全：母本只在锁里读。
        lock (master) return master.DeepClone().AsObject();
    }

    /// <summary>源文件的 SHA-256（<see cref="ProjectSource.SourceHashAsync"/>），一次 analyze 只算一次；分析结束时 <see cref="VerifySourcesAsync"/> 再算一次核对。</summary>
    internal async Task<string> SourceHashAsync(ProjectSource source, CancellationToken token)
    {
        string path = source.SourcePath;
        string hash = await OnceAsync("source-hash|" + path, () => source.SourceHashAsync(token));
        hashed.TryAdd(path, hash);
        return hash;
    }

    /// <summary>本次用过的源都重算一遍哈希：分析期间被改过就报错（原先每次子分析收尾各查一次）。</summary>
    internal async Task VerifySourcesAsync(CancellationToken token)
    {
        foreach (var (path, hash) in hashed)
        {
            using var source = new ProjectSource(path);
            if (hash != await source.SourceHashAsync(token)) throw new IOException("Source changed during analysis.");
        }
    }

    /// <summary>JSON 母本（scene、project、runtime trace……）：按键只建一次，每次交出一份 DeepClone。</summary>
    internal JsonObject Json(string key, Func<JsonObject> create) =>
        Clone(OnceAsync(key, () => Task.FromResult(create())).GetAwaiter().GetResult());

    /// <inheritdoc cref="Json"/>
    /// <param name="keep">结果要不要记住；不记的下次重算（与没有记忆时一样）。</param>
    internal async Task<JsonObject> JsonAsync(string key, Func<Task<JsonObject>> create, Func<JsonObject, bool>? keep = null) =>
        Clone(await OnceAsync(key, create, keep));

    /// <summary>实时判定：按键只算一次，每次交出挂在本次场景图上的新副本（分配阶段还会往里加原因）。</summary>
    internal Liveness Liveness(string key, SceneGraph graph, Func<Liveness> create)
    {
        Liveness master = OnceAsync(key, () => Task.FromResult(create())).GetAwaiter().GetResult();
        lock (master) return master.CopyFor(graph);
    }

    /// <summary>逐组静态证明（只看这组图层有没有源与运行时的静止证明）。</summary>
    internal async Task<bool> StaticProofAsync(string key, Func<Task<bool>> create) =>
        (bool)await OnceAsync<object>(key, async () => (object)await create());
}
