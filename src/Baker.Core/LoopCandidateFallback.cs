using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>解析循环候选的执行顺序。</summary>
public static class LoopCandidateFallback
{
    /// <summary>Reorders already-admitted candidates only; budgets and solver equations remain unchanged.</summary>
    public static void PrioritizeShortest(JsonArray candidates)
    {
        JsonNode[] ordered = candidates.Select(node => node ?? throw new InvalidDataException("Loop candidate is null."))
            .OrderBy(node => node["frames"]!.GetValue<ulong>()).ToArray();
        candidates.Clear();
        foreach (JsonNode candidate in ordered) candidates.Add(candidate);
    }
}
