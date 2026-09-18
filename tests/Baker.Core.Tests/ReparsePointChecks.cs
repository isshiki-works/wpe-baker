using System.Diagnostics;
using Baker.Core;

/// <summary>
/// 路径上的 junction / 卷挂载点：项目<b>内部</b>的链接仍然拒绝，路径祖先上的链接放行
/// （mklink /J 搬过的 Steam 库、NTFS 卷挂载点都是这种形状，原先会让 analyze 与 bake 直接失败）。
/// </summary>
internal static class ReparsePointChecks
{
    /// <summary>建一个目录 junction。mklink /J 不需要管理员权限，失败时返回 false。</summary>
    private static bool TryCreateJunction(string link, string target)
    {
        try
        {
            using Process? process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            });
            if (process is null) return false;
            process.WaitForExit(30_000);
            return process.HasExited && process.ExitCode == 0 &&
                (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static void Run(Action<bool, string> check, string root)
    {
        ArgumentNullException.ThrowIfNull(check);
        string baseDirectory = Path.Combine(root, "reparse-points");
        string realParent = Path.Combine(baseDirectory, "real-parent");
        string project = Path.Combine(realParent, "project");
        string elsewhere = Path.Combine(baseDirectory, "elsewhere");
        Directory.CreateDirectory(Path.Combine(project, "materials"));
        Directory.CreateDirectory(elsewhere);
        File.WriteAllText(Path.Combine(project, "materials", "texture.tex"), "texture");

        string junction = Path.Combine(baseDirectory, "linked-parent");
        bool linkedParent = TryCreateJunction(junction, realParent);

        // ---- ① 祖先是 junction：源资源与输出目录都放行 ----
        string throughJunction = Path.Combine(junction, "project");
        bool ancestorAllowed;
        try
        {
            ProjectSource.ContainedPath(throughJunction, "materials/texture.tex");
            ProjectSource.EnsureNoReparsePoints(Path.Combine(throughJunction, "bake-output"));
            ancestorAllowed = true;
        }
        catch (InvalidDataException) { ancestorAllowed = false; }
        check(linkedParent && ancestorAllowed,
            "a junction above the project no longer blocks its source resources or its output directory");

        // ---- ② 项目内部的 junction 仍然拒绝：资源不能顺着链接跳出项目 ----
        string insideLink = Path.Combine(project, "linked-materials");
        bool insideLinked = TryCreateJunction(insideLink, elsewhere);
        bool insideRejected = false;
        try { ProjectSource.ContainedPath(project, "linked-materials/texture.tex"); }
        catch (InvalidDataException) { insideRejected = true; }
        check(insideLinked && insideRejected,
            "a junction inside the project is still rejected, so resources cannot follow a link out of it");

        // ---- ③ 目标自己就是链接时照旧拒绝：写入不跟随链接 ----
        bool targetRejected = false;
        try { ProjectSource.EnsureNoReparsePoints(junction); }
        catch (InvalidDataException) { targetRejected = true; }
        check(targetRejected, "a destination that is itself a junction is still rejected");

        // junction 要单独删，否则清理测试目录会跟着链接删到目标里去。
        foreach (string link in new[] { insideLink, junction })
            try { if (Directory.Exists(link)) Directory.Delete(link); } catch (IOException) { }
    }
}
