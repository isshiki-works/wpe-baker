#requires -Version 7.0
[CmdletBinding()]
param(
    [datetime] $DeadlineUtc = [datetime]::UtcNow.AddHours(4),
    [string] $OutputRoot = 'D:\WPE-v101-two-axis',
    [string] $Manifest = 'D:\WPE-aggr\summary.csv',
    [string] $Assets = 'D:\Apps\Steam\steamapps\common\wallpaper_engine\assets',
    [string] $Tools = 'D:\WPE-rc11\bin-fd97869\tools.json'
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$OutputEncoding = [Console]::OutputEncoding
$env:DOTNET_ROOT = 'C:\Users\Alya\Desktop\Work\wpe-baker-next\.dotnet'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$cli = Join-Path $repo 'src/Baker.Cli/bin/Release/net10.0/wpe-baker.dll'
$dotnet = Join-Path $env:DOTNET_ROOT 'dotnet.exe'
$report = Join-Path $repo 'reports-20260916/v101-default-flip.md'
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$bakeRoot = Join-Path $OutputRoot 'bake'
$logRoot = Join-Path $OutputRoot 'batch-logs'
$lockPath = 'D:\WPE-perf\machine.lock'
$started = [datetime]::UtcNow
$minFree = [IO.DriveInfo]::new('D:\').AvailableFreeSpace
$startFree = $minFree
$stopReason = $null
$exceptionStreak = 0
$analysis = [Collections.Generic.List[object]]::new()
$bakes = [Collections.Generic.List[object]]::new()
$utf8 = [Text.UTF8Encoding]::new($false)
$items = @(Import-Csv -LiteralPath $Manifest)
if ($items.Count -ne 113 -or @($items.id | Sort-Object -Unique).Count -ne 113) { throw 'Expected exactly 113 distinct source IDs.' }
if ($items.Where({ $_.id -notmatch '^\d+$' }).Count) { throw 'Invalid source ID.' }
foreach ($directory in @($OutputRoot, $bakeRoot, $logRoot)) { [IO.Directory]::CreateDirectory($directory) | Out-Null }
if (Test-Path (Join-Path $OutputRoot 'batch-state.json')) { throw 'This batch already has state; inspect it before starting another batch.' }
$popularity = Get-Content 'D:\WPE-rc11\laptop\workshop-popularity.json' -Raw | ConvertFrom-Json -AsHashtable
$topIds = @($items | Sort-Object { [long]$popularity[$_.id].subs } -Descending | Select-Object -First 30 -ExpandProperty id)
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $repo 'src/Baker.Core/bin/Release/net10.0/Baker.Core.dll'))
$cleanup = $assembly.GetType('Baker.Core.HybridBakeService').GetMethod('RemoveIntermediates', [Reflection.BindingFlags]'NonPublic,Static')

function Guard {
    $free = [IO.DriveInfo]::new('D:\').AvailableFreeSpace
    $script:minFree = [math]::Min($script:minFree, $free)
    if ($free -lt 100GB) { return 'disk_below_100_GiB' }
    if ([datetime]::UtcNow -ge $DeadlineUtc.ToUniversalTime()) { return 'total_time_limit' }
    return $null
}
function Save-State {
    $state = [ordered]@{
        started_utc=$started; updated_utc=[datetime]::UtcNow; stop_reason=$script:stopReason
        analyze_attempted=$analysis.Count; analyze_bakeable=@($analysis | Where-Object bakeable).Count
        queue=$script:queue.Count; bake_attempted=$bakes.Count
        video=@($bakes | Where-Object category -eq 'video').Count
        static=@($bakes | Where-Object category -eq 'static').Count
        failed=@($bakes | Where-Object category -eq 'failed').Count
        start_free_bytes=$startFree; minimum_free_bytes=$script:minFree
        disk_growth_peak_bytes=[math]::Max(0, $startFree-$script:minFree)
        elapsed_seconds=([datetime]::UtcNow-$started).TotalSeconds
        bake_seconds=($bakes | Measure-Object seconds -Sum).Sum
    }
    [IO.File]::WriteAllText((Join-Path $OutputRoot 'batch-state.json'), ($state | ConvertTo-Json -Depth 6), $utf8)
    $line = "- 2.4 实时进度：分析 $($state.analyze_attempted)/113，可烘 $($state.analyze_bakeable)；烘焙 $($state.bake_attempted)/$($state.queue)，含视频 $($state.video)、静止 $($state.static)、失败 $($state.failed)。"
    $text = [IO.File]::ReadAllText($report)
    if ($text -match '(?m)^- 2\.4 实时进度：.*$') { $text = [regex]::Replace($text, '(?m)^- 2\.4 实时进度：.*$', $line) }
    else { $text += "`n$line`n" }
    [IO.File]::WriteAllText($report, $text, $utf8)
}
function Run-Cli([string[]] $Arguments, [string] $Name) {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $psi = [Diagnostics.ProcessStartInfo]::new($dotnet)
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.ArgumentList.Add($cli)
    foreach ($argument in $Arguments) { $psi.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $psi
    $stdout = [IO.File]::Create((Join-Path $logRoot "$Name.stdout.log"))
    $stderr = [IO.File]::Create((Join-Path $logRoot "$Name.stderr.log"))
    $reason = $null
    try {
        if (-not $process.Start()) { throw 'CLI did not start.' }
        $outCopy = $process.StandardOutput.BaseStream.CopyToAsync($stdout)
        $errCopy = $process.StandardError.BaseStream.CopyToAsync($stderr)
        while (-not $process.WaitForExit(1000)) {
            $reason = Guard
            if ($reason) { $script:stopReason = $reason; break }
            if ($clock.Elapsed.TotalMinutes -ge 30) { $reason = 'timeout'; break }
        }
        if ($reason -and -not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
        [Threading.Tasks.Task]::WhenAll([Threading.Tasks.Task[]]@($outCopy,$errCopy)).GetAwaiter().GetResult()
        return [pscustomobject]@{exit_code=$process.ExitCode; seconds=$clock.Elapsed.TotalSeconds; reason=$reason}
    }
    finally {
        if ($process.Id -and -not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
        $stdout.Dispose(); $stderr.Dispose(); $process.Dispose()
    }
}
function Clean-Intermediates([string] $Directory) {
    # The Core helper only removes its known intermediates; validate the entire owned subtree first.
    $full = [IO.Path]::GetFullPath($Directory)
    if (-not $full.StartsWith($bakeRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Cleanup escaped the batch bake directory.' }
    [Baker.Core.ProjectSource]::EnsureNoReparsePoints($full)
    foreach ($candidate in @($full, "$full.composition-probe", "$full.composition-reference", "$full.analysis-refresh")) {
        if (Test-Path -LiteralPath $candidate) {
            [Baker.Core.ProjectSource]::EnsureNoReparsePoints($candidate)
            if (Get-ChildItem -LiteralPath $candidate -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) { throw 'Linked cleanup input rejected.' }
        }
    }
    $errors = $cleanup.Invoke($null, [object[]]@($full,$true))
    if ($errors -and $errors.Count) { throw "Intermediate cleanup failed: $errors" }
}

$queue = @()
$lock = $null
try {
    $stopReason = Guard
    if ($stopReason) { return }
    # CreateNew atomically refuses a machine already reserved by another worker.
    $lock = [IO.FileStream]::new($lockPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
    $bytes = $utf8.GetBytes("codex-v101`n$PID`n$($started.ToString('O'))")
    $lock.Write($bytes); $lock.Flush()
    foreach ($item in $items) {
        $stopReason = Guard
        if ($stopReason) { break }
        $id = $item.id
        $planPath = Join-Path $OutputRoot "analyze/$id/plan.json"
        $result = Run-Cli @('analyze', "D:\WPE-regress-src\$id\scene.pkg", '--out', $planPath, '--assets', $Assets, '--tools', $Tools) "analyze-$id"
        $plan = if (Test-Path -LiteralPath $planPath) { Get-Content -LiteralPath $planPath -Raw | ConvertFrom-Json } else { $null }
        $record = [pscustomobject]@{id=$id; plan=$planPath; seconds=$result.seconds; exit_code=$result.exit_code; reason=$result.reason
            bakeable=($result.exit_code -eq 0 -and $plan.summary.key -like 'summary.bakeable*'); preset=$plan.preset_applied
            groups=@($plan.video_groups).Count; summary_key=$plan.summary.key; top30=($id -in $topIds); subs=[long]$popularity[$id].subs}
        $analysis.Add($record)
        [IO.File]::AppendAllText((Join-Path $OutputRoot 'analyze-results.jsonl'), ($record | ConvertTo-Json -Compress) + "`n", $utf8)
        if ($result.exit_code -ne 0 -or $result.reason) { $exceptionStreak++ } else { $exceptionStreak=0 }
        Save-State
        if ($exceptionStreak -ge 3) { $stopReason='three_consecutive_exceptions'; break }
        if ($stopReason) { break }
    }
    $queue = @($analysis | Where-Object bakeable | Sort-Object @{Expression='top30';Descending=$true},@{Expression='subs';Descending=$true})
    [IO.File]::WriteAllText((Join-Path $OutputRoot 'bake-queue.json'), ($queue | ConvertTo-Json -Depth 5), $utf8)
    [IO.File]::AppendAllText($report, "`n- 2.4 分析小节：已分析 $($analysis.Count)/113，可烘 $($queue.Count)；旧普查的固定取舍结果不能等价映射，已按新级联重算。`n", $utf8)
    Save-State
    if ($stopReason) { return }
    $exceptionStreak=0
    foreach ($item in $queue) {
        $stopReason = Guard
        if ($stopReason) { break }
        $id=$item.id
        $directory=Join-Path $bakeRoot $id
        if (Test-Path -LiteralPath $directory) { throw "Refusing to overwrite existing bake: $directory" }
        try {
            $result = Run-Cli @('bake', $item.plan, '--out', $directory, '--tools', $Tools, '--encode-slots', '2', '--group-parallel', '1', '--keep-intermediates', 'false') "bake-$id"
        }
        finally { Clean-Intermediates $directory }
        $bakePath=Join-Path $directory 'bake.json'
        $bake = if (Test-Path -LiteralPath $bakePath) { Get-Content -LiteralPath $bakePath -Raw | ConvertFrom-Json } else { $null }
        $status = if ($result.reason) { $result.reason } elseif ($bake.status) { $bake.status } else { 'exception' }
        $category = if ($result.exit_code -eq 0 -and -not $result.reason -and $status -eq 'candidate_generated' -and $bake.video_layers -gt 0) { 'video' }
            elseif ($result.exit_code -eq 0 -and -not $result.reason -and $status -eq 'static_only') { 'static' } else { 'failed' }
        $record=[pscustomobject]@{id=$id; status=$status; category=$category; seconds=$result.seconds; exit_code=$result.exit_code
            top30=$item.top30; intermediates_removed=$true; report=$bakePath}
        $bakes.Add($record)
        [IO.File]::AppendAllText((Join-Path $OutputRoot 'bake-results.jsonl'), ($record | ConvertTo-Json -Compress) + "`n", $utf8)
        if ($result.exit_code -ne 0 -or $result.reason) { $exceptionStreak++ } else { $exceptionStreak=0 }
        Save-State
        if ($exceptionStreak -ge 3) { $stopReason='three_consecutive_exceptions'; break }
        if ($stopReason) { break }
    }
}
catch {
    $stopReason = "exception: $($_.Exception.Message)"
    [IO.File]::AppendAllText((Join-Path $logRoot 'batch-error.log'), ($_ | Out-String), $utf8)
}
finally {
    if ($lock) { $lock.Dispose(); Remove-Item -LiteralPath $lockPath }
    Save-State
    $topAnalysis=@($analysis | Where-Object top30)
    $topQueue=@($queue | Where-Object top30)
    $topBakes=@($bakes | Where-Object top30)
    $topVideo=@($topBakes | Where-Object category -eq 'video').Count
    $topStatic=@($topBakes | Where-Object category -eq 'static').Count
    $topFailed=@($topBakes | Where-Object category -eq 'failed').Count
    $ending=if($stopReason){"停止条件/原因：$stopReason"}else{'队列已跑完'}
    [IO.File]::AppendAllText($report, "`n- 2.4 烘焙小节：$ending；前 30 已分析 $($topAnalysis.Count)/30，可烘 $($topQueue.Count)，已烘 $($topBakes.Count)，含视频 $topVideo、静止 $topStatic、失败 $topFailed。`n", $utf8)
    [IO.File]::AppendAllText($report, "- 2.4 用时/磁盘：批次 $([math]::Round(([datetime]::UtcNow-$started).TotalMinutes,2)) 分钟；D 盘最低 $([math]::Round($minFree/1GB,2)) GiB，批次期间磁盘占用最大净增 $([math]::Round(($startFree-$minFree)/1GB,2)) GiB（含同机其它活动）。逐案原始记录在 $OutputRoot/*-results.jsonl。`n", $utf8)
}
