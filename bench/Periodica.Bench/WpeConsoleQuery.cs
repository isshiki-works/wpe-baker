using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace Periodica.Bench;

/// <summary>WPE's GUI query attaches to its parent's console; redirected stdout alone can be empty.</summary>
internal static class WpeConsoleQuery
{
    internal static string CleanOutput(string output) => Regex.Replace(output,
        @"\x1B(?:\[[0-?]*[ -/]*[@-~]|\][^\x07\x1B]*(?:\x07|\x1B\\))", "").Trim();

    internal static string ExistingPath(string output)
    {
        string[] lines = CleanOutput(output).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string path = lines.Length == 1 ? lines[0].Trim('"') : "";
        return Path.IsPathFullyQualified(path) && File.Exists(path) ? path : "";
    }

    internal static async Task<string> ReadAsync(string executable, int monitor, CancellationToken token)
    {
        nint inputRead = 0, inputWrite = 0, outputRead = 0, outputWrite = 0, console = 0, attributes = 0, environment = 0;
        ProcessInfo process = default;
        Task<string>? reader = null;
        bool attributesReady = false;
        try
        {
            Check(CreatePipe(out inputRead, out inputWrite, 0, 0));
            Check(CreatePipe(out outputRead, out outputWrite, 0, 0));
            var stream = new FileStream(new SafeFileHandle(outputRead, ownsHandle: true), FileAccess.Read);
            outputRead = 0;
            // Drain during initialization too: even failed CreateProcess must not block ConPTY teardown.
            reader = Task.Run(() => { using var text = new StreamReader(stream, Encoding.UTF8); return text.ReadToEnd(); });
            // A wide buffer avoids wrapping ordinary project paths. No cursor-inheritance handshake.
            Marshal.ThrowExceptionForHR(CreatePseudoConsole(new Coord(2000, 4), inputRead, outputWrite, 0, out console));
            nuint size = 0;
            InitializeProcThreadAttributeList(0, 1, 0, ref size);
            attributes = Marshal.AllocHGlobal(checked((int)size));
            Check(InitializeProcThreadAttributeList(attributes, 1, 0, ref size));
            attributesReady = true;
            Check(UpdateProcThreadAttribute(attributes, 0, 0x20016, console, (nuint)nint.Size, 0, 0));
            var startup = new StartupInfoEx { Info = new StartupInfo { Size = Marshal.SizeOf<StartupInfoEx>() }, Attributes = attributes };
            // Expansion occurs once in this process-private block; %, &, spaces and Unicode in the value stay quoted.
            var variables = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables()) variables[(string)entry.Key] = (string)entry.Value!;
            variables["PERIODICA_QUERY_EXE"] = Path.GetFullPath(executable);
            environment = Marshal.StringToHGlobalUni(string.Join('\0', variables.Select(pair => pair.Key + "=" + pair.Value)) + "\0\0");
            string cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            var command = new StringBuilder($"\"{cmd}\" /d /q /v:off /s /c \"\"%PERIODICA_QUERY_EXE%\" -control getWallpaper -monitor {monitor.ToString(System.Globalization.CultureInfo.InvariantCulture)}\"");
            Check(CreateProcessW(cmd, command, 0, 0, false, 0x80000 | 0x400, environment,
                Path.GetDirectoryName(executable)!, ref startup, out process));
            CloseHandle(inputRead); inputRead = 0;
            CloseHandle(outputWrite); outputWrite = 0;
            // Keep draining while ClosePseudoConsole emits its final frame, including on Windows 10.
            using var cancelled = token.Register(() => KillOwnedQuery(process));
            uint wait = await Task.Run(() =>
            {
                uint result = WaitForSingleObject(process.Process, 15_000);
                if (result == uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error());
                return result;
            });
            if (wait == 258) { KillOwnedQuery(process); throw new TimeoutException("Official Wallpaper Engine console query exceeded 15 seconds."); }
            token.ThrowIfCancellationRequested();
            Check(GetExitCodeProcess(process.Process, out uint exit));
            ClosePseudoConsole(console); console = 0;
            string output = await reader.WaitAsync(TimeSpan.FromSeconds(3));
            if (exit != 0) throw new IOException($"Official Wallpaper Engine console query exited {exit}: {CleanOutput(output)}");
            return ExistingPath(output);
        }
        finally
        {
            // This session contains only our cmd/query clients, never the already-running WPE main process.
            if (console != 0) ClosePseudoConsole(console);
            foreach (nint handle in new[] { inputRead, inputWrite, outputRead, outputWrite, process.Thread, process.Process })
                if (handle != 0) CloseHandle(handle);
            if (attributesReady) DeleteProcThreadAttributeList(attributes);
            if (attributes != 0) Marshal.FreeHGlobal(attributes);
            if (environment != 0) Marshal.FreeHGlobal(environment);
        }
    }

    private static void Check(bool ok) { if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error()); }
    private static void KillOwnedQuery(ProcessInfo process)
    {
        try
        {
            if (WaitForSingleObject(process.Process, 0) != 258) return;
            using Process owned = Process.GetProcessById(checked((int)process.Pid));
            owned.Kill(entireProcessTree: true);
        }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception) { }
    }
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Coord(short X, short Y);
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        internal int Size;
        internal nint Reserved, Desktop, Title;
        internal uint X, Y, XSize, YSize, XChars, YChars, Fill, Flags;
        internal ushort Show, ReservedSize;
        internal nint ReservedBytes, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx { internal StartupInfo Info; internal nint Attributes; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo { internal nint Process, Thread; internal uint Pid, Tid; }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out nint read, out nint write, nint security, uint size);
    [DllImport("kernel32.dll")]
    private static extern int CreatePseudoConsole(Coord size, nint input, nint output, uint flags, out nint console);
    [DllImport("kernel32.dll")]
    private static extern void ClosePseudoConsole(nint console);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(nint list, uint count, uint flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(nint list, uint flags, nuint attribute, nint value, nuint size, nint previous, nint returned);
    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(nint list);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(string application, StringBuilder command, nint processSecurity, nint threadSecurity,
        bool inherit, uint flags, nint environment, string directory, ref StartupInfoEx startup, out ProcessInfo process);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(nint process, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(nint process, out uint code);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint handle);
}
