using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Baker.Core;

public sealed record WallpaperTarget(string Profile, string Location, string CurrentFile, JsonObject Assignment);
public sealed record WallpaperObservation(string File, string Evidence);
public sealed record WallpaperApplyRequest(int SchemaVersion, string Project, string Profile, string Location, string OutputDirectory);
internal sealed record WallpaperWindowObservation(bool Exists, long Handle, int ProcessId, int ClientWidth,
    int ClientHeight, uint? Dpi, string Evidence, bool ClientDimensionsArePhysical = false, bool IsVisible = false);

/// <summary>Explicit, single-location actions through Wallpaper Engine's documented command line.</summary>
public sealed class WallpaperController(string executable)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private string Executable => Path.GetFullPath(executable);
    private string ConfigPath => Path.Combine(Path.GetDirectoryName(Executable)!, "config.json");

    public async Task<IReadOnlyList<WallpaperTarget>> ReadTargetsAsync(CancellationToken cancellationToken = default)
    {
        var config = JsonNode.Parse(await File.ReadAllTextAsync(ConfigPath, cancellationToken))!.AsObject();
        var targets = new List<WallpaperTarget>();
        foreach (var profile in config)
        {
            if (profile.Value is not JsonObject account || account["general"]?["wallpaperconfig"]?["selectedwallpapers"] is not JsonObject selected) continue;
            foreach (var target in selected)
                if (target.Value is JsonObject assignment)
                    targets.Add(new(profile.Key, target.Key, assignment["file"]?.GetValue<string>() ?? "", assignment.DeepClone().AsObject()));
        }
        return targets;
    }

    public async Task<IReadOnlyList<WallpaperTarget>> ReadCurrentTargetsAsync(CancellationToken cancellationToken = default)
    {
        if (!NativeEnvironment.WallpaperRunning(Executable)) return [];
        var targets = (await ReadTargetsAsync(cancellationToken))
            .Where(target => target.Profile.Equals(Environment.UserName, StringComparison.OrdinalIgnoreCase)).ToArray();
        var result = new List<WallpaperTarget>();
        int count = GetSystemMetrics(80); // SM_CMONITORS: connected desktop displays, not cached WPE assignments.
        for (int index = 0; index < count; ++index)
        {
            string current = (await RunAsync(["-control", "getWallpaper", "-monitor", index.ToString(System.Globalization.CultureInfo.InvariantCulture)], cancellationToken)).Trim().Trim('"');
            WallpaperTarget? target = targets.SingleOrDefault(item => item.Location == $"Monitor{index}");
            if (target is null && current.Length > 0)
            {
                var matches = targets.Where(item => SameWallpaper(item.CurrentFile, current)).ToArray();
                if (matches.Length == 1) target = matches[0];
            }
            // Unrecognized monitor identifiers require an official response; never guess a cached/disconnected location.
            if (target is not null && result.All(item => item.Location != target.Location))
                result.Add(current.Length == 0 ? target : target with { CurrentFile = current });
        }
        return result;
    }

    public async Task<string> GetWallpaperAsync(string location, CancellationToken cancellationToken = default)
        => (await ObserveAsync(location, cancellationToken)).File;

    public async Task OpenInWindowAsync(string project, string windowName, uint width, uint height,
        CancellationToken cancellationToken = default, bool activate = false)
    {
        string projectFile = Path.GetFullPath(project);
        if (Directory.Exists(projectFile)) projectFile = Path.Combine(projectFile, "project.json");
        if (!File.Exists(projectFile)) throw new FileNotFoundException("A project.json is required for window playback.", projectFile);
        if (string.IsNullOrWhiteSpace(windowName) || width == 0 || height == 0 || width > int.MaxValue || height > int.MaxValue)
            throw new ArgumentException("Window playback requires a unique name and positive supported dimensions.");
        var arguments = new List<string> { "-control", "openWallpaper", "-file", projectFile, "-playInWindow", windowName,
            "-width", width.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-height", height.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        if (activate) arguments.Add("-activate");
        await RunAsync(arguments.ToArray(), cancellationToken);
    }

    public Task CloseWindowAsync(string windowName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(windowName)) throw new ArgumentException("A private window name is required.");
        return RunAsync(["-control", "closeWallpaper", "-location", windowName], cancellationToken);
    }

    internal WallpaperWindowObservation ObserveWindow(string windowName)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Wallpaper window observation requires Windows.");
        if (string.IsNullOrWhiteSpace(windowName)) throw new ArgumentException("A private window name is required.");
        nint handle = FindWindow(null, windowName);
        if (handle == 0) return new(false, 0, 0, 0, 0, null, "find_window_no_match");
        _ = GetWindowThreadProcessId(handle, out uint processId);
        // Limit DPI awareness to this synchronous read; restore before returning to the caller.
        // Otherwise CLI callers can observe virtualized dimensions unlike the WPF caller.
        nint previousContext = 0;
        try { previousContext = SetThreadDpiAwarenessContext(new nint(-4)); }
        catch (EntryPointNotFoundException) { }
        bool hasRect;
        Rect rect;
        uint? dpi = null;
        try
        {
            hasRect = GetClientRect(handle, out rect);
            try { uint value = GetDpiForWindow(handle); if (value > 0) dpi = value; }
            catch (EntryPointNotFoundException) { }
        }
        finally
        {
            if (previousContext != 0) _ = SetThreadDpiAwarenessContext(previousContext);
        }
        return new(true, handle.ToInt64(), checked((int)processId), hasRect ? rect.Right - rect.Left : 0,
            hasRect ? rect.Bottom - rect.Top : 0, dpi, hasRect ? "find_window_client_rect" : "find_window_rect_unavailable",
            hasRect && previousContext != 0, IsWindowVisible(handle));
    }

    public async Task<WallpaperObservation> ObserveAsync(string location, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(location)) throw new ArgumentException("An explicit Wallpaper Engine location is required.");
        string response = (await RunAsync(["-control", "getWallpaper", "-location", location], cancellationToken)).Trim().Trim('"');
        if (!string.IsNullOrWhiteSpace(response)) return new(response, "official_getWallpaper");
        // Some WPE builds return no redirected stdout from a windowless command process.
        // The saved assignment is usable for target control, but is not evidence of rendered playback.
        var matches = (await ReadTargetsAsync(cancellationToken)).Where(t => t.Location == location).ToArray();
        var target = matches.SingleOrDefault(t => t.Profile.Equals(Environment.UserName, StringComparison.OrdinalIgnoreCase))
            ?? (matches.Length == 1 ? matches[0] : null);
        if (target is null) throw new InvalidDataException("No unambiguous assignment is available for this location.");
        return new(target.CurrentFile, "saved_configuration_getWallpaper_returned_empty");
    }

    public async Task<JsonObject> ApplyAsync(WallpaperApplyRequest request, CancellationToken cancellationToken = default)
    {
        if (request.SchemaVersion != 1 || string.IsNullOrWhiteSpace(request.Profile) || string.IsNullOrWhiteSpace(request.Location))
            throw new ArgumentException("A version 1 request must select one profile and location.");
        using var source = new ProjectSource(request.Project);
        string project = Path.Combine(source.DirectoryPath, "project.json");
        if (!File.Exists(project)) throw new FileNotFoundException("An independent project.json is required for application.", project);
        string output = Path.GetFullPath(request.OutputDirectory);
        ProjectSource.EnsureNoReparsePoints(output);
        if (File.Exists(output) || Directory.Exists(output)) throw new IOException("The application record directory must be new.");
        if (output.StartsWith(source.DirectoryPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The application record cannot be inside the project.");
        // WPE 没在运行时 ReadCurrentTargetsAsync 一律返回空，以前会被报成"这块屏幕不见了"，
        // 用户按着提示去查显示器也查不出问题；先把"没启动"和"屏幕没了"分开说。
        if (!NativeEnvironment.WallpaperRunning(Executable))
            throw new InvalidDataException(Messages.Emit("apply.wallpaper_engine_not_running"));
        var targets = await ReadCurrentTargetsAsync(cancellationToken);
        var target = targets.SingleOrDefault(t => t.Profile == request.Profile && t.Location == request.Location)
            ?? throw new InvalidDataException(Messages.Emit("apply.location_missing", request.Location));
        string? playlist = PlaylistName(target.Assignment);
        var before = await ObserveAsync(request.Location, cancellationToken);
        string current = before.File;
        if (string.IsNullOrWhiteSpace(current) || !File.Exists(current))
            throw new InvalidDataException("Wallpaper Engine did not return a restorable current wallpaper for this location.");
        if (playlist is null && !SameWallpaper(current, target.CurrentFile))
            throw new InvalidDataException("Live wallpaper differs from the saved assignment. Refresh the target list before applying.");
        string hash = await source.SourceHashAsync(cancellationToken);
        Directory.CreateDirectory(output);
        // This backup is for manual recovery only. Automatic rollback never rewrites the full configuration.
        File.Copy(ConfigPath, Path.Combine(output, "config.before.json"));
        var report = new JsonObject { ["schema_version"] = 1, ["status"] = "prepared", ["executable"] = Executable,
            ["profile"] = request.Profile, ["location"] = request.Location, ["before_file"] = current,
            ["before_assignment"] = target.Assignment.DeepClone(), ["before_playlist"] = playlist,
            ["project"] = project, ["project_sha256"] = hash, ["started_utc"] = DateTimeOffset.UtcNow.ToString("O"),
            ["before_evidence"] = before.Evidence,
            ["visual_playback"] = "not_confirmed" };
        string manifest = Path.Combine(output, "apply.json");
        await SaveAsync(manifest, report);
        try
        {
            if (!SameWallpaper(current, await GetWallpaperAsync(request.Location, cancellationToken)))
                throw new InvalidDataException("The selected wallpaper changed before application; refresh and try again.");
            await RunAsync(["-control", "openWallpaper", "-file", project, "-location", request.Location], cancellationToken);
            report["status"] = "command_sent";
            await SaveAsync(manifest, report);
            var observed = await WaitForWallpaperAsync(request.Location, project, cancellationToken);
            report["observed_file"] = observed.File;
            report["assignment_evidence"] = observed.Evidence;
            report["status"] = observed.Evidence == "official_getWallpaper" ? "applied" : "assignment_confirmed";
            report["finished_utc"] = DateTimeOffset.UtcNow.ToString("O");
            await SaveAsync(manifest, report);
            return report;
        }
        catch (Exception error)
        {
            report["status"] = cancellationToken.IsCancellationRequested ? "cancelled_state_requires_check" : "failed_state_requires_check";
            report["error"] = error.Message;
            await SaveAsync(manifest, report);
            throw;
        }
    }

    public async Task<JsonObject> RollbackAsync(string manifestPath, CancellationToken cancellationToken = default)
    {
        string path = Path.GetFullPath(manifestPath);
        var report = JsonNode.Parse(await File.ReadAllTextAsync(path, cancellationToken))!.AsObject();
        if (report["schema_version"]?.GetValue<int>() != 1 || !string.Equals(report["executable"]?.GetValue<string>(), Executable, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Application record does not match this Wallpaper Engine installation.");
        string location = report["location"]?.GetValue<string>() ?? throw new InvalidDataException("Application record has no target location.");
        string project = report["project"]?.GetValue<string>() ?? throw new InvalidDataException("Application record has no applied project.");
        string previous = report["before_file"]?.GetValue<string>() ?? throw new InvalidDataException("Application record has no previous wallpaper.");
        if (report["status"]?.GetValue<string>() is "restored" or "restore_assignment_confirmed") return report;
        if (!SameWallpaper(await GetWallpaperAsync(location, cancellationToken), project))
            throw new InvalidDataException("This location is no longer displaying the applied project. Restore was stopped to preserve your later selection.");
        string? playlist = report["before_playlist"]?.GetValue<string>();
        if (playlist is null && !File.Exists(previous)) throw new FileNotFoundException("The previous wallpaper is no longer available.", previous);
        report["rollback_started_utc"] = DateTimeOffset.UtcNow.ToString("O");
        await SaveAsync(path, report);
        if (playlist is not null)
            await RunAsync(["-control", "openPlaylist", "-playlist", playlist, "-location", location], cancellationToken);
        else
            await RunAsync(["-control", "openWallpaper", "-file", previous, "-location", location], cancellationToken);
        if (playlist is null)
        {
            var observed = await WaitForWallpaperAsync(location, previous, cancellationToken);
            report["restored_file"] = observed.File;
            report["restore_evidence"] = observed.Evidence;
            report["status"] = observed.Evidence == "official_getWallpaper" ? "restored" : "restore_assignment_confirmed";
        }
        else
        {
            // getWallpaper reports the playing item, not the active playlist name.
            report["restored_file"] = await GetWallpaperAsync(location, cancellationToken);
            report["status"] = "playlist_restore_command_sent";
        }
        report["rollback_finished_utc"] = DateTimeOffset.UtcNow.ToString("O");
        await SaveAsync(path, report);
        return report;
    }

    private async Task<WallpaperObservation> WaitForWallpaperAsync(string location, string expected, CancellationToken token)
    {
        var observed = new WallpaperObservation("", "none");
        for (int attempt = 0; attempt < 20; attempt++)
        {
            observed = await ObserveAsync(location, token);
            if (SameWallpaper(observed.File, expected)) return observed;
            await Task.Delay(250, token);
        }
        throw new IOException($"Wallpaper Engine did not confirm the requested project. Observed: {observed.File} ({observed.Evidence})");
    }

    private async Task<string> RunAsync(string[] arguments, CancellationToken token)
    {
        if (!NativeEnvironment.WallpaperRunning(Executable)) throw new InvalidOperationException("Start Wallpaper Engine normally before sending a control command.");
        var info = new ProcessStartInfo(Executable) { UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(Executable)!, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        using var child = Process.Start(info) ?? throw new IOException("Could not send the Wallpaper Engine command.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        // Only this transient command process is owned by the controller; the running desktop process is never stopped.
        using var cancelled = timeout.Token.Register(() => { try { if (!child.HasExited) child.Kill(); } catch (InvalidOperationException) { } });
        var stdout = child.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = child.StandardError.ReadToEndAsync(timeout.Token);
        await Task.WhenAll(stdout, stderr, child.WaitForExitAsync(timeout.Token));
        if (child.ExitCode != 0) throw new IOException($"Wallpaper Engine command exited {child.ExitCode}: {stderr.Result}");
        return stdout.Result;
    }

    private static string? PlaylistName(JsonObject assignment)
    {
        if (assignment["playlist"] is null) return null;
        if (assignment["playlist"] is JsonValue value && value.TryGetValue<bool>(out bool active) && !active) return null;
        if (assignment["playlist"] is JsonValue name && name.TryGetValue<string>(out string? text) && !string.IsNullOrWhiteSpace(text)) return text;
        throw new InvalidDataException("This playlist assignment has no restorable playlist name; apply it manually in Wallpaper Engine.");
    }

    private static bool SameWallpaper(string first, string second)
    {
        static string Identity(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "";
            string full = Path.GetFullPath(path);
            if (Path.GetFileName(full).Equals("project.json", StringComparison.OrdinalIgnoreCase) && File.Exists(full))
            {
                var project = JsonNode.Parse(File.ReadAllText(full));
                if (project?["file"] is JsonValue file && file.TryGetValue<string>(out string? resource) && !string.IsNullOrWhiteSpace(resource))
                {
                    string directory = Path.GetDirectoryName(full)!;
                    string named = Path.GetFullPath(Path.Combine(directory, resource));
                    if (File.Exists(named)) return named;
                    // 创意工坊原作的 project.json 常写着未打包的 scene.json，目录里实际只有打包后的 scene.pkg，
                    // 官方 Wallpaper Engine 记录的也是 scene.pkg。两者指的是同一张壁纸。
                    string packed = Path.Combine(directory, "scene.pkg");
                    return File.Exists(packed) ? packed : named;
                }
            }
            return full;
        }
        return string.Equals(Identity(first), Identity(second), StringComparison.OrdinalIgnoreCase);
    }

    private static Task SaveAsync(string path, JsonObject data) => File.WriteAllTextAsync(path, data.ToJsonString(JsonOptions), CancellationToken.None);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "FindWindowW", ExactSpelling = true)]
    private static extern nint FindWindow(string? className, string windowName);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(nint window, out Rect rect);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")]
    private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
