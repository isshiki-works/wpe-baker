using Baker.Core;

namespace Baker.App;

internal static class ProgressPresentation
{
    internal static string Timing(RenderProgress? value, double elapsedSeconds, bool english)
    {
        string elapsed = Duration(elapsedSeconds);
        string text = english ? $"Elapsed {elapsed}" : $"已用时 {elapsed}";
        if (value?.Stage != "rendering") return text;
        if (value.StageRemainingSeconds is not double remaining || !double.IsFinite(remaining) || remaining < 0)
            return text + (english ? " · Estimating time left…" : " · 正在估算剩余时间…");
        string left = Duration(remaining);
        string total = Duration((value.StageElapsedSeconds ?? 0) + remaining);
        return text + (english
            ? $" · Render: about {left} left of {total}"
            : $" · 渲染剩余约 {left}（共 {total}）");
    }

    internal static string Duration(double seconds)
    {
        // Round up instead of showing zero seconds while work remains.
        long value = (long)Math.Ceiling(Math.Clamp(double.IsFinite(seconds) ? seconds : 0, 0, 315360000));
        return value >= 3600 ? $"{value / 3600}:{value / 60 % 60:00}:{value % 60:00}"
            : $"{value / 60}:{value % 60:00}";
    }
}
