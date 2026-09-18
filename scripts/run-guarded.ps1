<#
.SYNOPSIS
    给任意长任务子进程套一层资源看门狗。

.DESCRIPTION
    本机 2026-09-16 因为一次烘焙把内存吃光而整机假死，只能硬重启。长任务一律从这里启动：
    脚本周期性检查系统可用内存与目标盘剩余空间，任一越线就立刻终止整棵进程树，
    并把终止原因与当时的数值写进 status 文件；正常结束则写真实退出码。

    看门狗自身出错时（采样连续失败、status 文件被占、任何意外异常）会先杀掉整棵子进程树，
    再把 status 写成 watchdog_failed 与原因，然后才把异常抛出去——绝不会留下无人看管的子进程。

    Windows PowerShell 5.1 与 pwsh 7 都能跑：5.1 上没有 ProcessStartInfo.ArgumentList，也没有
    Process.Kill($true)，脚本会自动回退到按 Windows 命令行规则（CommandLineToArgvW）加引号的
    Arguments 字符串，以及 taskkill /T /F 杀整棵树；status 里的 argument_mode 记录走了哪条路。

    调用方式：必须用 `&` 在当前 PowerShell 进程里调用，或 `pwsh -Command "& scripts\run-guarded.ps1 ..."`。
    不支持 `pwsh -File`：那条路上 -Arguments 数组会被拍平，或整段变成一个带引号逗号的畸形参数
    （脚本会检出这种入参并报错）。

    也不要用 Invoke-CimMethod Win32_Process Create 去拉 MSIX（Microsoft Store）版 pwsh：
    那样起来的进程落在 Session 0，会静默退出，日志和 status 一个字都不写。计划任务或远程拉起
    请用 powershell.exe，或非 MSIX 安装的 pwsh.exe。

.EXAMPLE
    & scripts\run-guarded.ps1 -Executable src\Baker.Cli\bin\Release\net10.0\wpe-baker.exe `
        -Arguments @('generate', '--plan', 'plan.json') -LogPath work\bake.log -StatusPath work\bake.status.json
#>
[CmdletBinding()]
param(
    # 要运行的可执行文件。
    [Parameter(Mandatory = $true)][string]$Executable,
    # 传给它的参数，按数组给，不要自己拼引号。
    [string[]]$Arguments = @(),
    # 子进程的工作目录，默认当前目录。
    [string]$WorkingDirectory = (Get-Location).Path,
    # 标准输出日志；标准错误写到同名的 .err.log。
    [Parameter(Mandatory = $true)][string]$LogPath,
    # 状态文件，结束原因与实测数值写在这里；默认是日志同名的 .status.json。
    [string]$StatusPath,
    # 系统可用内存下限（GB），低于此值立刻终止。
    [double]$MinimumFreeMemoryGB = 4,
    # 目标盘剩余空间下限（GB），低于此值立刻终止。
    [double]$MinimumFreeDiskGB = 100,
    # 要盯的磁盘，默认盯工作目录所在盘。
    [string]$DiskPath,
    # 轮询间隔（秒）。
    [double]$PollSeconds = 15,
    # 超时（分钟），0 表示不限。
    [double]$TimeoutMinutes = 0,
    # 资源采样连续失败多少次就认定看门狗已失去保护能力：杀子进程树并写 watchdog_failed。
    [int]$MaximumSampleFailures = 4
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
# 本机 ACP 是 GBK，这里显式钉成 UTF-8，免得中文原因串写进日志和 status 变成乱码。
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$OutputEncoding = [Text.Encoding]::UTF8

$utf8NoBom = [Text.UTF8Encoding]::new($false)
$statusWriteFailures = 0
$lastStatusWriteError = $null

# .NET 的 GetFullPath 按进程 CWD 解析，而 PowerShell 的当前位置常常不是同一个目录：
# 相对路径一律按 PowerShell 当前位置补全，免得日志和 status 写到别处、或者盯错了盘。
function Resolve-FullPath {
    param([string]$Path)
    if ([IO.Path]::IsPathRooted($Path)) { return [IO.Path]::GetFullPath($Path) }
    [IO.Path]::GetFullPath([IO.Path]::Combine((Get-Location -PSProvider FileSystem).ProviderPath, $Path))
}

# pwsh -File 会把 -Arguments 数组整段塞成一个带引号逗号的参数，子进程收到畸形 argv 之后
# 往往静默失败。这种入参在这里就拦住，提示改用 & 调用。
if ($Arguments.Count -eq 1 -and $Arguments[0].Contains(',') -and
    ($Arguments[0].Contains("'") -or $Arguments[0].Contains('"'))) {
    throw ("-Arguments 只收到一个含引号和逗号的元素：$($Arguments[0])" +
        "；这是 pwsh -File 把数组拍平的结果。请改用 & scripts\run-guarded.ps1 -Arguments @('a','b') 在同一进程里调用。")
}

$executablePath = (Resolve-Path -LiteralPath $Executable).Path
$workingDirectory = (Resolve-Path -LiteralPath $WorkingDirectory).Path
if (-not $DiskPath) { $DiskPath = $workingDirectory }
$diskRoot = [IO.Path]::GetPathRoot((Resolve-FullPath $DiskPath))
$outLogPath = Resolve-FullPath $LogPath
if ($StatusPath) { $StatusPath = Resolve-FullPath $StatusPath }
else { $StatusPath = [IO.Path]::ChangeExtension($outLogPath, '.status.json') }
foreach ($target in @($outLogPath, $StatusPath)) {
    $parent = [IO.Path]::GetDirectoryName($target)
    if ($parent -and -not (Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Force $parent | Out-Null }
}
$errLogPath = $outLogPath + '.err.log'

# 系统可用物理内存（字节）。用 CIM 而不是性能计数器：计数器名在中文系统上是本地化的。
function Get-FreeMemoryBytes {
    [double](Get-CimInstance Win32_OperatingSystem -Property FreePhysicalMemory).FreePhysicalMemory * 1KB
}

function Get-FreeDiskBytes {
    [double]([IO.DriveInfo]::new($diskRoot).AvailableFreeSpace)
}

# 整棵进程树当前占用的工作集，用来给报告留一个峰值。
function Get-TreeWorkingSet {
    param([int]$RootId)
    $all = Get-CimInstance Win32_Process -Property ProcessId, ParentProcessId, WorkingSetSize
    $byParent = @{}
    foreach ($row in $all) {
        if (-not $byParent.ContainsKey([int]$row.ParentProcessId)) { $byParent[[int]$row.ParentProcessId] = @() }
        $byParent[[int]$row.ParentProcessId] += $row
    }
    $byId = @{}
    foreach ($row in $all) { $byId[[int]$row.ProcessId] = $row }
    $total = [double]0
    $pending = [Collections.Generic.Queue[int]]::new()
    $pending.Enqueue($RootId)
    $seen = [Collections.Generic.HashSet[int]]::new()
    while ($pending.Count -gt 0) {
        $id = $pending.Dequeue()
        if (-not $seen.Add($id)) { continue }
        if ($byId.ContainsKey($id)) { $total += [double]$byId[$id].WorkingSetSize }
        if ($byParent.ContainsKey($id)) { foreach ($child in $byParent[$id]) { $pending.Enqueue([int]$child.ProcessId) } }
    }
    $total
}

# .NET Framework（Windows PowerShell 5.1）的 Process.Kill 没有 bool 重载，
# ProcessStartInfo 也没有 ArgumentList。笔记本 SSH 默认落到 5.1，所以两条都要有回退。
$supportsKillTree = $null -ne [Diagnostics.Process].GetMethod('Kill', [type[]]@([bool]))
$supportsArgumentList = $null -ne [Diagnostics.ProcessStartInfo].GetProperty('ArgumentList')

# 5.1 回退路径用：把参数数组拼成一条命令行，规则与 CommandLineToArgvW 对齐——
# 反斜杠只在引号（或结尾引号）之前才成对转义，内嵌引号写成 \"，含空格/制表/引号或空串的参数加引号。
function ConvertTo-CommandLineString {
    param([string[]]$ArgumentList)
    $parts = @()
    foreach ($argument in $ArgumentList) {
        if ($argument.Length -gt 0 -and $argument -notmatch '[\s"]') {
            $parts += $argument
            continue
        }
        $builder = [Text.StringBuilder]::new()
        [void]$builder.Append('"')
        $index = 0
        while ($index -lt $argument.Length) {
            $char = $argument[$index]
            $index++
            if ($char -eq '\') {
                $slashes = 1
                while ($index -lt $argument.Length -and $argument[$index] -eq '\') { $index++; $slashes++ }
                if ($index -eq $argument.Length) { [void]$builder.Append('\' * ($slashes * 2)) }
                elseif ($argument[$index] -eq '"') {
                    [void]$builder.Append('\' * ($slashes * 2 + 1))
                    [void]$builder.Append('"')
                    $index++
                }
                else { [void]$builder.Append('\' * $slashes) }
            }
            elseif ($char -eq '"') { [void]$builder.Append('\"') }
            else { [void]$builder.Append($char) }
        }
        [void]$builder.Append('"')
        $parts += $builder.ToString()
    }
    $parts -join ' '
}

# 杀整棵进程树。pwsh 7 用 Kill($true)；5.1 上那个重载不存在，回退 taskkill /T /F。
function Stop-ProcessTree {
    param([Diagnostics.Process]$Process)
    # 函数内改偏好只作用于本函数：taskkill 往 stderr 写东西时，'Stop' 会把它当终止错误。
    $ErrorActionPreference = 'Continue'
    try { if ($Process.HasExited) { return $false } } catch { return $false }
    $killed = $false
    try {
        if ($supportsKillTree) {
            $Process.Kill($true)
            $killed = $true
        }
        else {
            $null = & taskkill.exe /T /F /PID $Process.Id 2>$null
            try { $null = $Process.WaitForExit(5000) } catch { }
            try { $killed = $Process.HasExited } catch { $killed = $true }
        }
    }
    catch { }
    $killed
}

# status 文件随时可能正被别人读着（tail、另一个 agent、编辑器），WriteAllText 撞上共享冲突就会抛。
# 写 status 是记录手段，不是看门狗的命根子：重试几次，实在写不进去也只记下来，绝不能把看门狗带走。
function Write-Status {
    param([hashtable]$Status)
    try {
        $Status['written_utc'] = (Get-Date).ToUniversalTime().ToString('o')
        $payload = $Status | ConvertTo-Json -Depth 6
    }
    catch {
        $script:statusWriteFailures++
        $script:lastStatusWriteError = $_.Exception.Message
        return
    }
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        try {
            [IO.File]::WriteAllText($StatusPath, $payload, $utf8NoBom)
            return
        }
        catch {
            $script:lastStatusWriteError = $_.Exception.Message
            if ($attempt -eq 3) { $script:statusWriteFailures++; return }
            Start-Sleep -Milliseconds (100 * $attempt)
        }
    }
}

$minimumMemoryBytes = $MinimumFreeMemoryGB * 1GB
$minimumDiskBytes = $MinimumFreeDiskGB * 1GB
$startFreeMemory = Get-FreeMemoryBytes
$startFreeDisk = Get-FreeDiskBytes

$status = @{
    schema_version          = 1
    status                  = 'running'
    executable              = $executablePath
    arguments               = $Arguments
    working_directory       = $workingDirectory
    disk_root               = $diskRoot
    minimum_free_memory_gb  = $MinimumFreeMemoryGB
    minimum_free_disk_gb    = $MinimumFreeDiskGB
    poll_seconds            = $PollSeconds
    timeout_minutes         = $TimeoutMinutes
    started_utc             = (Get-Date).ToUniversalTime().ToString('o')
    free_memory_gb_at_start = [math]::Round($startFreeMemory / 1GB, 2)
    free_disk_gb_at_start   = [math]::Round($startFreeDisk / 1GB, 2)
}
Write-Status $status

# 不用 Start-Process -PassThru：那条路上拿不到可靠的 ExitCode。直接起 Process 对象，
# 等它自己退出以后再读 ExitCode。
$info = [Diagnostics.ProcessStartInfo]::new()
$info.FileName = $executablePath
$info.WorkingDirectory = $workingDirectory
$info.UseShellExecute = $false
$info.CreateNoWindow = $true
$info.RedirectStandardOutput = $true
$info.RedirectStandardError = $true
if ($supportsArgumentList) {
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    $status['argument_mode'] = 'argument_list'
}
else {
    # 5.1：没有 ArgumentList，只能自己按 Windows 命令行规则拼引号。
    $info.Arguments = ConvertTo-CommandLineString -ArgumentList $Arguments
    $status['argument_mode'] = 'quoted_command_line'
    $status['command_line_arguments'] = $info.Arguments
}

$process = [Diagnostics.Process]::new()
$process.StartInfo = $info
# 用 FileShare.Read 打开：长任务跑几十分钟，日志必须能被另一个进程同时 tail。
$outStream = [IO.File]::Open($outLogPath, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::Read)
$errStream = [IO.File]::Open($errLogPath, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::Read)
$stopwatch = [Diagnostics.Stopwatch]::StartNew()
$terminated = $null
$minimumFreeMemorySeen = $startFreeMemory
$minimumFreeDiskSeen = $startFreeDisk
$peakTreeWorkingSet = [double]0
$pumps = @()
# 采样失败时沿用上一次的值，所以这三个要先有初值。
$freeMemory = $startFreeMemory
$freeDisk = $startFreeDisk
$tree = [double]0
$consecutiveSampleFailures = 0
$totalSampleFailures = 0
$lastSampleError = $null

try {
    if (-not $process.Start()) { throw "无法启动 $executablePath" }
    $pumps = @(
        $process.StandardOutput.BaseStream.CopyToAsync($outStream),
        $process.StandardError.BaseStream.CopyToAsync($errStream)
    )
    $status['process_id'] = $process.Id
    Write-Status $status

    while (-not $process.HasExited) {
        $null = $process.WaitForExit([int][math]::Max(250, $PollSeconds * 1000))
        if ($process.HasExited) { break }
        # WMI 恰好在内存紧张时最容易失败，而那正是最需要看门狗的时刻：单次采样抛异常不许把脚本带走，
        # 沿用上一次的值继续盯，只累计连续失败次数；连续 $MaximumSampleFailures 次拿不到数，
        # 说明保护已经失效，按终止条件处理（抛给外层 catch，那里会杀掉整棵进程树）。
        $sampleFailure = $null
        try { $freeMemory = Get-FreeMemoryBytes }
        catch { $sampleFailure = "可用内存采样失败：$($_.Exception.Message)" }
        try { $freeDisk = Get-FreeDiskBytes }
        catch { $sampleFailure = "$diskRoot 剩余空间采样失败：$($_.Exception.Message)" }
        try { $tree = Get-TreeWorkingSet -RootId $process.Id }
        catch { $sampleFailure = "进程树工作集采样失败：$($_.Exception.Message)" }
        if ($sampleFailure) {
            $consecutiveSampleFailures++
            $totalSampleFailures++
            $lastSampleError = $sampleFailure
            $status['sample_failures'] = $totalSampleFailures
            $status['consecutive_sample_failures'] = $consecutiveSampleFailures
            $status['last_sample_error'] = $sampleFailure
            Write-Status $status
            if ($consecutiveSampleFailures -ge $MaximumSampleFailures) {
                throw "资源采样连续失败 $consecutiveSampleFailures 次（上限 $MaximumSampleFailures），看门狗已无法保护子进程：$sampleFailure"
            }
        }
        else {
            $consecutiveSampleFailures = 0
            $status['consecutive_sample_failures'] = 0
        }
        if ($freeMemory -lt $minimumFreeMemorySeen) { $minimumFreeMemorySeen = $freeMemory }
        if ($freeDisk -lt $minimumFreeDiskSeen) { $minimumFreeDiskSeen = $freeDisk }
        if ($tree -gt $peakTreeWorkingSet) { $peakTreeWorkingSet = $tree }
        if ($freeMemory -lt $minimumMemoryBytes) {
            $terminated = "系统可用内存降到 $([math]::Round($freeMemory / 1GB, 2)) GB，低于下限 $MinimumFreeMemoryGB GB"
        }
        elseif ($freeDisk -lt $minimumDiskBytes) {
            $terminated = "$diskRoot 剩余空间降到 $([math]::Round($freeDisk / 1GB, 2)) GB，低于下限 $MinimumFreeDiskGB GB"
        }
        elseif ($TimeoutMinutes -gt 0 -and $stopwatch.Elapsed.TotalMinutes -gt $TimeoutMinutes) {
            $terminated = "运行 $([math]::Round($stopwatch.Elapsed.TotalMinutes, 2)) 分钟，超过上限 $TimeoutMinutes 分钟"
        }
        if ($terminated) {
            $status['status'] = 'terminated'
            $status['termination_reason'] = $terminated
            $status['free_memory_gb_at_termination'] = [math]::Round($freeMemory / 1GB, 2)
            $status['free_disk_gb_at_termination'] = [math]::Round($freeDisk / 1GB, 2)
            $status['process_tree_working_set_gb_at_termination'] = [math]::Round($tree / 1GB, 2)
            Write-Status $status
            # 整棵树一起杀：ffmpeg、渲染器都是子进程，只杀父进程会留下真正吃资源的那个。
            $null = Stop-ProcessTree -Process $process
            break
        }
        $status['elapsed_seconds'] = [math]::Round($stopwatch.Elapsed.TotalSeconds, 1)
        $status['free_memory_gb_now'] = [math]::Round($freeMemory / 1GB, 2)
        $status['free_disk_gb_now'] = [math]::Round($freeDisk / 1GB, 2)
        $status['peak_process_tree_gb'] = [math]::Round($peakTreeWorkingSet / 1GB, 2)
        Write-Status $status
    }

    $process.WaitForExit()
    foreach ($pump in $pumps) { try { $pump.Wait(10000) | Out-Null } catch { } }
    $stopwatch.Stop()
    $exitCode = $process.ExitCode
}
catch {
    # 看门狗自己出事了（采样连续失败、进程启动失败、任何意外异常）：先连整棵树一起杀掉，
    # 再把 watchdog_failed 和原因写进 status，最后才把异常抛出去。
    # 不这么做的话脚本一退出，子进程树就没人看管，status 会永远停在 running。
    $failureReason = $_.Exception.Message
    $childKilled = Stop-ProcessTree -Process $process
    $stopwatch.Stop()
    $status['status'] = 'watchdog_failed'
    $status['watchdog_failure_reason'] = $failureReason
    $status['child_process_killed'] = $childKilled
    $status['elapsed_seconds'] = [math]::Round($stopwatch.Elapsed.TotalSeconds, 1)
    $status['sample_failures'] = $totalSampleFailures
    $status['consecutive_sample_failures'] = $consecutiveSampleFailures
    if ($lastSampleError) { $status['last_sample_error'] = $lastSampleError }
    $status['minimum_free_memory_gb_seen'] = [math]::Round($minimumFreeMemorySeen / 1GB, 2)
    $status['minimum_free_disk_gb_seen'] = [math]::Round($minimumFreeDiskSeen / 1GB, 2)
    $status['peak_process_tree_gb'] = [math]::Round($peakTreeWorkingSet / 1GB, 2)
    $status['stdout_log'] = $outLogPath
    $status['stderr_log'] = $errLogPath
    Write-Status $status
    [Console]::Error.WriteLine("看门狗失效：$failureReason（子进程已终止：$childKilled）")
    throw
}
finally {
    # 无论走哪条路退出，子进程树都不许留在后台：还活着就连整棵树一起杀。
    $null = Stop-ProcessTree -Process $process
    # 先把两个泵等回来再关流，否则流被 Dispose、泵直接 fault，日志尾巴也跟着丢。
    foreach ($pump in $pumps) { try { $pump.Wait(2000) | Out-Null } catch { } }
    $outStream.Dispose()
    $errStream.Dispose()
    $process.Dispose()
}

$status['elapsed_seconds'] = [math]::Round($stopwatch.Elapsed.TotalSeconds, 1)
$status['exit_code'] = $exitCode
$status['minimum_free_memory_gb_seen'] = [math]::Round($minimumFreeMemorySeen / 1GB, 2)
$status['minimum_free_disk_gb_seen'] = [math]::Round($minimumFreeDiskSeen / 1GB, 2)
$status['peak_process_tree_gb'] = [math]::Round($peakTreeWorkingSet / 1GB, 2)
# 收尾采样同样不许抛：子进程已经结束，这里失败只该少一个数字，不该让 status 停在旧状态。
try { $status['free_memory_gb_at_end'] = [math]::Round((Get-FreeMemoryBytes) / 1GB, 2) } catch { $status['free_memory_gb_at_end'] = $null }
try { $status['free_disk_gb_at_end'] = [math]::Round((Get-FreeDiskBytes) / 1GB, 2) } catch { $status['free_disk_gb_at_end'] = $null }
$status['sample_failures'] = $totalSampleFailures
if ($lastSampleError) { $status['last_sample_error'] = $lastSampleError }
if ($statusWriteFailures -gt 0) {
    $status['status_write_failures'] = $statusWriteFailures
    $status['last_status_write_error'] = $lastStatusWriteError
}
$status['stdout_log'] = $outLogPath
$status['stderr_log'] = $errLogPath
if ($terminated) {
    $status['status'] = 'terminated'
    Write-Status $status
    # 不用 Write-Error：$ErrorActionPreference = 'Stop' 会让它直接抛出，拿不到约定的退出码。
    [Console]::Error.WriteLine("看门狗终止了子进程：$terminated")
    exit 87
}
$status['status'] = if ($exitCode -eq 0) { 'completed' } else { 'failed' }
Write-Status $status
"看门狗：退出码 $exitCode，用时 $($status['elapsed_seconds']) 秒，进程树峰值 $($status['peak_process_tree_gb']) GB，" +
"可用内存最低 $($status['minimum_free_memory_gb_seen']) GB，$diskRoot 剩余最低 $($status['minimum_free_disk_gb_seen']) GB"
exit $exitCode
