#requires -Version 7
<#
    RC11 发行包对应性核对（GPL v2 第 3 条）。只读：解包到 -WorkDir，不改动输入 zip。

    用法：
      pwsh -File verify-package.ps1 -PortableZip <便携包.zip> -SourceZip <源码包.zip> `
           -WorkDir D:\WPE-rc11-pkg\verify [-ExpectedRenderer <SHA256>] [-RendererPatchDir <目录>] [-Keep]

    退出码：0 全部通过；1 有 FAIL；2 只有 WARN（人工确认后可放行）。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PortableZip,
    [Parameter(Mandatory)][string]$SourceZip,
    [Parameter(Mandatory)][string]$WorkDir,
    # 多线程渲染器 wpe-render-mt-r4b.exe
    [string]$ExpectedRenderer = 'A8F03257E3B02CD691813AB7302E76C9EEA68567D77B3A71B1EC434A14FC3292',
    # 可选：外部渲染器补丁包目录（比对它声明的 exe SHA 与补丁文件哈希）
    [string]$RendererPatchDir,
    [switch]$Keep
)

$ErrorActionPreference = 'Stop'
# 不开 StrictMode：本脚本大量用 $collection.Count，严格模式下标量会抛 PropertyNotFound。
$script:rows = [System.Collections.Generic.List[object]]::new()
$started = Get-Date

function Add-Row([string]$Area, [string]$Item, [string]$Result, [string]$Detail) {
    $script:rows.Add([pscustomobject]@{ 区域 = $Area; 检查项 = $Item; 结果 = $Result; 说明 = $Detail })
    $color = switch ($Result) { 'PASS' { 'Green' } 'FAIL' { 'Red' } default { 'Yellow' } }
    Write-Host ("[{0,-4}] {1,-22} {2}" -f $Result, $Item, $Detail) -ForegroundColor $color
}
function Sha([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant() }
function Need([string]$Root, [string]$Relative) { Join-Path $Root $Relative }

# ---------- 解包 ----------
foreach ($zip in @($PortableZip, $SourceZip)) {
    if (-not (Test-Path -LiteralPath $zip)) { throw "找不到输入 zip：$zip" }
}
if (Test-Path -LiteralPath $WorkDir) { Remove-Item -LiteralPath $WorkDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
$portableRoot = Join-Path $WorkDir 'portable'
$sourceRoot = Join-Path $WorkDir 'source'
foreach ($pair in @(@($PortableZip, $portableRoot), @($SourceZip, $sourceRoot))) {
    $t0 = Get-Date
    [System.IO.Compression.ZipFile]::ExtractToDirectory($pair[0], $pair[1])
    $secs = [math]::Round(((Get-Date) - $t0).TotalSeconds, 1)
    Add-Row '解包' (Split-Path $pair[0] -Leaf) 'INFO' ("$secs s，SHA256 " + (Sha $pair[0]).Substring(0, 16) + "…，$([math]::Round((Get-Item -LiteralPath $pair[0]).Length / 1MB, 1)) MB")
}
$bundle = Join-Path $portableRoot 'WpeBaker'
$src = Join-Path $sourceRoot 'WpeBaker-source'
if (-not (Test-Path $bundle)) { throw "便携包里没有 WpeBaker\ 根目录" }
if (-not (Test-Path $src)) { throw "源码包里没有 WpeBaker-source\ 根目录" }

# ---------- 1. 便携包自洽（package.json 逐文件） ----------
$manifestPath = Need $bundle 'package.json'
if (Test-Path $manifestPath) {
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $bad = 0; $checked = 0
    foreach ($property in $manifest.files.PSObject.Properties) {
        # package.json 本身写在清单之后，若被列进来其哈希必然自指，跳过
        if ($property.Name -eq 'package.json') { continue }
        $file = Join-Path $bundle $property.Name
        if (-not (Test-Path -LiteralPath $file)) { $bad++; continue }
        $checked++
        if ((Sha $file) -ne $property.Value.sha256.ToUpperInvariant()) { $bad++ }
    }
    Add-Row '便携包' 'package.json 自洽' ($bad -eq 0 ? 'PASS' : 'FAIL') "$checked 个文件逐一重算，$bad 个不符"
} else {
    Add-Row '便携包' 'package.json' 'FAIL' '缺 package.json，无法自洽核对'
}

# ---------- 2. 渲染器 exe ----------
$renderer = Need $bundle 'renderer\wpe-render.exe'
$rendererSha = $null
if (Test-Path $renderer) {
    $rendererSha = Sha $renderer
    Add-Row '渲染器' 'exe SHA256 = 期望值' (($rendererSha -eq $ExpectedRenderer.ToUpperInvariant()) ? 'PASS' : 'FAIL') $rendererSha
} else {
    Add-Row '渲染器' 'renderer\wpe-render.exe' 'FAIL' '便携包里没有渲染器'
}

# 便携包构建记录
$buildRecord = Need $bundle 'build-records\build-wpe-render.json'
if ((Test-Path $buildRecord) -and $rendererSha) {
    $record = Get-Content -LiteralPath $buildRecord -Raw | ConvertFrom-Json
    Add-Row '渲染器' '包内构建记录对应' (($record.binary.sha256.ToUpperInvariant() -eq $rendererSha) ? 'PASS' : 'FAIL') "记录 $($record.binary.sha256.Substring(0,16))…，status=$($record.status)"
    Add-Row '渲染器' '构建记录含源码摘要' ($record.compiled_source_sha256 ? 'PASS' : 'FAIL') "source digest $($record.compiled_source_sha256.Substring(0,16))…，engine_base $($record.engine_base.Substring(0,7))"
} else {
    Add-Row '渲染器' 'build-records\build-wpe-render.json' 'FAIL' '便携包缺构建记录'
}

# 源码包 source-bundle.json
$sourceBundle = Need $src 'source-bundle.json'
if ((Test-Path $sourceBundle) -and $rendererSha) {
    $sb = Get-Content -LiteralPath $sourceBundle -Raw | ConvertFrom-Json
    Add-Row '源码包' 'native_renderer_sha256 对应' (($sb.native_renderer_sha256.ToUpperInvariant() -eq $rendererSha) ? 'PASS' : 'FAIL') "$($sb.native_renderer_sha256.Substring(0,16))…，files=$(@($sb.files.PSObject.Properties).Count)"
} else {
    Add-Row '源码包' 'source-bundle.json' 'FAIL' '源码包缺 source-bundle.json'
}

# ---------- 3. 渲染器补丁（多线程版必须随源码） ----------
foreach ($dir in @(@{ Name = '源码包 patches\renderer-mt'; Path = (Need $src 'patches\renderer-mt') },
                   @{ Name = '外部补丁包'; Path = $RendererPatchDir })) {
    if (-not $dir.Path) { continue }
    if (-not (Test-Path -LiteralPath $dir.Path)) {
        Add-Row '渲染器补丁' $dir.Name 'FAIL' "目录不存在：$($dir.Path)"
        continue
    }
    $shaList = Join-Path $dir.Path 'sha256.txt'
    $missing = @('README.md', 'sha256.txt', 'engine-perf-video-decode-threads.patch',
        'parent-perf-video-decode-threads.patch') | Where-Object { -not (Test-Path (Join-Path $dir.Path $_)) }
    Add-Row '渲染器补丁' "$($dir.Name) 文件齐备" (($missing.Count -eq 0) ? 'PASS' : 'FAIL') ($missing.Count -eq 0 ? '4 个文件都在' : "缺 $($missing -join ', ')")
    if (-not (Test-Path $shaList)) { continue }
    $declared = @{}
    foreach ($line in Get-Content -LiteralPath $shaList) {
        if ($line -match '^([0-9A-Fa-f]{64})\s+\*?(.+?)\s*$') { $declared[$Matches[2]] = $Matches[1].ToUpperInvariant() }
    }
    # 补丁包声明的 exe SHA 必须与便携包里的渲染器一致
    $exeEntry = $declared.Keys | Where-Object { $_ -like '*wpe-render-mt-r4b*' } | Select-Object -First 1
    if ($exeEntry -and $rendererSha) {
        Add-Row '渲染器补丁' "$($dir.Name) 声明的 exe" (($declared[$exeEntry] -eq $rendererSha) ? 'PASS' : 'FAIL') "$exeEntry = $($declared[$exeEntry].Substring(0,16))…"
    } else {
        Add-Row '渲染器补丁' "$($dir.Name) 声明的 exe" 'WARN' 'sha256.txt 里没有 wpe-render-mt-r4b 条目'
    }
    # 两个 patch 文件的实际哈希 vs 声明
    foreach ($patch in @('engine-perf-video-decode-threads.patch', 'parent-perf-video-decode-threads.patch')) {
        $file = Join-Path $dir.Path $patch
        if (-not (Test-Path $file)) { continue }
        $actual = Sha $file
        if ($declared.ContainsKey($patch)) {
            Add-Row '渲染器补丁' "$patch 哈希" (($declared[$patch] -eq $actual) ? 'PASS' : 'FAIL') "$($actual.Substring(0,16))…"
        } else {
            Add-Row '渲染器补丁' "$patch 哈希" 'WARN' "sha256.txt 未声明，实际 $($actual.Substring(0,16))…"
        }
        # 内容必须是 diff，不能是误写入的终端输出
        $head = (Get-Content -LiteralPath $file -TotalCount 1 -Encoding utf8)
        Add-Row '渲染器补丁' "$patch 是 diff" (($head -like 'diff --git*' -or $head -like '---*') ? 'PASS' : 'FAIL') "首行：$($head.Substring(0, [math]::Min(40, $head.Length)))"
    }
}

# ---------- 4. LGPL / GPL 运行库与编码器 ----------
$verificationText = ''
foreach ($flavor in @('ffmpeg-lgpl21', 'ffmpeg-encoder-gpl2')) {
    $file = Need $src ".deps\$flavor\verification.json"
    if (Test-Path $file) { $verificationText += (Get-Content -LiteralPath $file -Raw).ToUpperInvariant() }
    else { Add-Row '源码包' "$flavor\verification.json" 'FAIL' '缺构建验证记录' }
}
$binaries = @()
$binaries += Get-ChildItem (Join-Path $bundle 'renderer') -Filter '*.dll' -ErrorAction SilentlyContinue
$binaries += Get-ChildItem (Join-Path $bundle 'encoder') -Include '*.dll', '*.exe' -Recurse -ErrorAction SilentlyContinue |
    Where-Object { -not $_.PSIsContainer }
$unmatched = @()
foreach ($binary in $binaries) {
    # libc++ / libunwind 来自 llvm-mingw 官方发行，不在 FFmpeg 验证记录里
    if ($binary.Name -in @('libc++.dll', 'libunwind.dll')) { continue }
    if ($verificationText -notlike "*$(Sha $binary.FullName)*") { $unmatched += $binary.Name }
}
Add-Row 'GPL/LGPL 二进制' 'SHA 能在验证记录中找到' (($unmatched.Count -eq 0) ? 'PASS' : 'FAIL') `
    ("核对 $($binaries.Count) 个" + ($unmatched.Count ? "，未匹配：$($unmatched -join ', ')" : '，全部匹配'))

# ---------- 5. 源码包必须包含 / 必须排除 ----------
$required = @('engine', 'engine-upstream.bundle', 'patches', 'scripts\dependency-patches\manifest.json',
    'scripts\dependency-patches\rstd.patch', 'scripts\dependency-patches\vvk.patch',
    'scripts\dependency-patches\wavsen.patch', 'THIRD-PARTY-NOTICES.md', 'SOURCE.md', 'REBUILD.md',
    '.deps\ffmpeg-lgpl21\sources\ffmpeg', '.deps\ffmpeg-encoder-gpl2\sources\x264',
    '.deps\ffmpeg-encoder-gpl2\sources\x265', '.deps\vvk', '.deps\wavsen', '.deps\rstd', '.deps\eigen',
    '.deps\freetype', '.deps\glslang', 'src', 'tests', 'scripts', 'LICENSE')
$missingRequired = $required | Where-Object { -not (Test-Path (Join-Path $src $_)) }
Add-Row '源码包' '必备条目齐备' (($missingRequired.Count -eq 0) ? 'PASS' : 'FAIL') ($missingRequired.Count -eq 0 ? "$($required.Count) 项都在" : "缺 $($missingRequired -join ', ')")
$engineFiles = (Get-ChildItem (Join-Path $src 'engine') -Recurse -File -ErrorAction SilentlyContinue).Count
Add-Row '源码包' 'engine 树非空' (($engineFiles -gt 100) ? 'PASS' : 'FAIL') "$engineFiles 个文件"

$forbidden = @('artifacts', 'work', 'dist', 'checkpoint', 'build')
$present = $forbidden | Where-Object { Test-Path (Join-Path $src $_) }
Add-Row '源码包' '无中间产物目录' (($present.Count -eq 0) ? 'PASS' : 'FAIL') ($present.Count -eq 0 ? "artifacts/work/dist/checkpoint/build 都不在" : "出现了 $($present -join ', ')")

# Workshop 素材特征：工坊 ID 目录、scene.pkg、project.json + 素材
$workshop = @(Get-ChildItem $src -Recurse -File -ErrorAction SilentlyContinue | Where-Object {
    $_.Name -eq 'scene.pkg' -or $_.Name -like '*.pkg' -or $_.FullName -match '\\[0-9]{9,10}\\'
} | Select-Object -First 10)
Add-Row '源码包' '无 Workshop 素材' (($workshop.Count -eq 0) ? 'PASS' : 'WARN') ($workshop.Count -eq 0 ? '没有 .pkg 与工坊 ID 目录' : "可疑：$(($workshop | ForEach-Object { $_.FullName.Substring($src.Length + 1) }) -join '; ')")

# ---------- 6. 便携包的许可与源码说明 ----------
$portableRequired = @('SOURCE.md', 'THIRD-PARTY-NOTICES.md', 'README.md', 'README.zh-CN.md', 'LICENSE',
    'licenses\open-wallpaper-engine.LICENSE', 'licenses\renderer-codecs\ffmpeg\COPYING.LGPLv2.1',
    'licenses\renderer-codecs\dav1d\COPYING', 'encoder\licenses\x264\COPYING', 'encoder\licenses\x265\COPYING',
    'encoder\licenses\ffmpeg\COPYING.GPLv2')
$missingPortable = $portableRequired | Where-Object { -not (Test-Path (Join-Path $bundle $_)) }
Add-Row '便携包' '源码说明与基础许可' (($missingPortable.Count -eq 0) ? 'PASS' : 'FAIL') ($missingPortable.Count -eq 0 ? "$($portableRequired.Count) 项都在" : "缺 $($missingPortable -join ', ')")

# 静态依赖许可文本（THIRD-PARTY-NOTICES 里列的那一批）
$staticLicenses = @('freetype.FTL.TXT', 'freetype.LICENSE.TXT', 'glslang.LICENSE.txt', 'lz4.lib.LICENSE',
    'quickjs-ng.LICENSE', 'vma.LICENSE.txt', 'spirv-reflect.LICENSE', 'eigen.COPYING.MPL2',
    'vulkan-headers.LICENSE.md', 'vulkan-loader.LICENSE.txt', 'rstd.LICENSE-MIT', 'rstd.LICENSE-APACHE',
    'wavsen.LICENSE-MIT', 'wavsen.LICENSE-APACHE')
$missingStatic = $staticLicenses | Where-Object { -not (Test-Path (Join-Path $bundle "licenses\$_")) }
Add-Row '便携包' '静态依赖许可文本' (($missingStatic.Count -eq 0) ? 'PASS' : 'FAIL') ($missingStatic.Count -eq 0 ? "$($staticLicenses.Count) 份都在" : "缺 $($missingStatic.Count) 份：$($missingStatic -join ', ')")

# SOURCE.md 里的占位符必须已回填
$sourceDoc = Join-Path $bundle 'SOURCE.md'
if (Test-Path $sourceDoc) {
    $placeholders = (Select-String -LiteralPath $sourceDoc -Pattern '【待填' -AllMatches).Matches.Count
    Add-Row '便携包' 'SOURCE.md 无占位符' (($placeholders -eq 0) ? 'PASS' : 'FAIL') "$placeholders 处【待填】"
}

# ---------- 7. 两包版本一致 ----------
foreach ($readme in @('README.md', 'README.zh-CN.md')) {
    $a = Join-Path $bundle $readme; $b = Join-Path $src $readme
    if ((Test-Path $a) -and (Test-Path $b)) {
        Add-Row '一致性' "$readme 两包同版" (((Sha $a) -eq (Sha $b)) ? 'PASS' : 'FAIL') (Sha $a).Substring(0, 16)
    } else {
        Add-Row '一致性' "$readme 两包同版" 'FAIL' '至少一个包里没有'
    }
}
$notices = @{ portable = (Join-Path $bundle 'THIRD-PARTY-NOTICES.md'); source = (Join-Path $src 'THIRD-PARTY-NOTICES.md') }
if ((Test-Path $notices.portable) -and (Test-Path $notices.source)) {
    Add-Row '一致性' 'NOTICES 两包同版' (((Sha $notices.portable) -eq (Sha $notices.source)) ? 'PASS' : 'FAIL') (Sha $notices.portable).Substring(0, 16)
}

# ---------- 汇总 ----------
$fail = @($script:rows | Where-Object { $_.结果 -eq 'FAIL' }).Count
$warn = @($script:rows | Where-Object { $_.结果 -eq 'WARN' }).Count
$pass = @($script:rows | Where-Object { $_.结果 -eq 'PASS' }).Count
Write-Host ''
$script:rows | Format-Table -AutoSize -Wrap
$elapsed = [math]::Round(((Get-Date) - $started).TotalSeconds, 1)
Write-Host ("核对完成：{0} PASS / {1} FAIL / {2} WARN，用时 {3} s" -f $pass, $fail, $warn, $elapsed) -ForegroundColor Cyan
$reportPath = Join-Path $WorkDir 'verify-package.json'
[pscustomobject]@{
    generated_utc = (Get-Date).ToUniversalTime().ToString('o'); portable_zip = $PortableZip; source_zip = $SourceZip
    portable_sha256 = (Sha $PortableZip); source_sha256 = (Sha $SourceZip); renderer_sha256 = $rendererSha
    expected_renderer = $ExpectedRenderer.ToUpperInvariant(); elapsed_seconds = $elapsed
    pass = $pass; fail = $fail; warn = $warn
    rows = $script:rows
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $reportPath -Encoding utf8NoBOM
Write-Host "核对表：$reportPath"
if (-not $Keep) { Write-Host "解包目录保留在 $WorkDir（加 -Keep 无区别，清理请手工删）" }
exit ($fail ? 1 : ($warn ? 2 : 0))
