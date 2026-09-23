using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Baker.Core;

/// <summary>
/// 脚本对视频播放的控制是<b>按目标</b>成立的，不是按场景成立的：一段脚本拿到某条视频轨的 videoTexture
/// 才控制得到它，场景里别的视频轨不因此失去周期。这里把"谁被控制"解出来：
/// <list type="bullet">
/// <item>已解析目标 = 含视频控制调用的脚本在运行时读到 videoTexture 的那些层，加上这些脚本自身所在的层；</item>
/// <item>未知作用域 owner = 脚本含视频控制调用，却在 initialization 阶段既没读到任何 videoTexture、自身也不是视频轨。</item>
/// </list>
/// init() 是每次运行的必经路径。它都没解析出任何视频目标，说明目标藏在这次捕获没走到的回调里，
/// 于是退回旧的全场景连坐。这条兜底只会让判定比今天更严或与今天一致，不会放行未经证明的视频轨。
/// </summary>
internal sealed class VideoControlScope
{
    private readonly HashSet<int> resolvedTargets;
    private readonly int[] unknownScopeOwners;

    private VideoControlScope(HashSet<int> resolvedTargets, int[] unknownScopeOwners)
    {
        this.resolvedTargets = resolvedTargets;
        this.unknownScopeOwners = unknownScopeOwners;
    }

    /// <summary>有脚本的控制目标解析不出来时，场景里每条视频轨都按受控处理。</summary>
    internal bool SceneWideFallback => unknownScopeOwners.Length != 0;

    internal bool Controls(int ownerLayerId) => SceneWideFallback || resolvedTargets.Contains(ownerLayerId);

    /// <summary>写进 loop 报告：拒绝理由可追溯，下游读字段而不是匹配文案。</summary>
    internal JsonObject ToJson() => new() {
        ["resolved_targets"] = new JsonArray(resolvedTargets.Order().Select(id => (JsonNode)id).ToArray()),
        ["unknown_scope_owners"] = new JsonArray(unknownScopeOwners.Select(id => (JsonNode)id).ToArray()),
        ["scene_wide_fallback"] = SceneWideFallback };

    internal static VideoControlScope Resolve(JsonObject scene, JsonObject runtime)
    {
        JsonObject[] dependencies = runtime["runtime_dependencies"]?.AsArray().OfType<JsonObject>().ToArray() ?? [];
        var videoOwners = new HashSet<int>((runtime["runtime_animation_periods"]?.AsArray().OfType<JsonObject>() ?? [])
            .Where(trace => string.Equals(trace["mechanism"]?.GetValue<string>(), "video", StringComparison.OrdinalIgnoreCase))
            .Select(trace => SceneGraph.Int(trace["source_owner_layer_id"])).OfType<int>());
        // 整个判定都锚在"含视频控制调用的脚本"上，和今天的全场景连坐同一个触发条件：
        // 场景里一段控制脚本都没有时结论与今天完全一致，有控制脚本时只锁它解析到的目标。
        // 读到 videoTexture 却不做任何播放控制的脚本不构成受控证据——今天也不构成。
        var controlOwners = (scene["objects"]?.AsArray().OfType<JsonObject>() ?? [])
            .Where(ControlsVideoPlayback).Select(owner => SceneGraph.Int(owner["id"])).OfType<int>().ToArray();
        var resolved = new HashSet<int>();
        var unknown = new List<int>();
        foreach (int id in controlOwners)
        {
            resolved.Add(id);
            JsonObject[] reads = dependencies.Where(dependency => IsVideoTextureRead(dependency) &&
                SceneGraph.Int(dependency["owner"]) == id).ToArray();
            foreach (JsonObject read in reads)
                if (SceneGraph.Int(read["target"]) is int target && target >= 0) resolved.Add(target);
            // 自身就是那条视频轨时，脚本不必解析别的目标：控制面已经完备。
            if (videoOwners.Contains(id)) continue;
            if (!reads.Any(read => read["initialization"]?.GetValue<bool>() == true)) unknown.Add(id);
        }
        unknown.Sort();
        return new VideoControlScope(resolved, [.. unknown]);
    }

    private static bool IsVideoTextureRead(JsonObject dependency) =>
        dependency["operation"]?.GetValue<string>() == "read" &&
        string.Equals(dependency["property"]?.GetValue<string>(), "videoTexture", StringComparison.OrdinalIgnoreCase);

    private static bool ControlsVideoPlayback(JsonObject owner) => SceneAnalyzer.Walk(owner).OfType<JsonObject>()
        .Any(binding => binding["script"] is JsonValue script && script.TryGetValue<string>(out string? code) &&
            ControlsVideoPlayback(code));

    /// <summary>一段脚本既取到视频对象、又对它做播放控制，才算控制调用。</summary>
    internal static bool ControlsVideoPlayback(string code)
    {
        if (!Regex.IsMatch(code, @"(?:getVideo|videoPlayback|video_texture)", RegexOptions.IgnoreCase)) return false;
        return Regex.IsMatch(code,
                   @"(?:\.\s*(?:play|pause|stop|seek|setCurrentTime|setFrame|join)\b|\[\s*['""](?:play|pause|stop|seek|setCurrentTime|setFrame|join)['""]\s*\]|(?:\.|\[\s*['""]?)rate(?:['""]?\s*\])?\s*[+\-*/%]?=(?!=))") ||
            Regex.IsMatch(code, @"\[\s*(?!['""](?:duration|rate|volume|getCurrentTime|isPlaying|play|stop|pause|setCurrentTime)['""]\s*\])");
    }
}
