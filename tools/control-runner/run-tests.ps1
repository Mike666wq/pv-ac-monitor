param(
    [string[]]$Suites = @('control','storage','workflow','smoke','demo'),
    [switch]$KeepRoots
)

# 仓库内自测入口：编译 ControlRunner，把每个套件跑在 test-output\<套件>-<guid> 目录里。
# 用法： powershell -ExecutionPolicy Bypass -File .\tools\control-runner\run-tests.ps1
#        powershell -ExecutionPolicy Bypass -File .\tools\control-runner\run-tests.ps1 -Suites control,smoke
$ErrorActionPreference = 'Stop'
$toolRoot = $PSScriptRoot
$projectRoot = (Resolve-Path (Join-Path $toolRoot '..\..')).Path
$outputRoot = Join-Path $projectRoot 'test-output'
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null

$compilerCandidates = @(
    (Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'),
    (Join-Path $env:WINDIR 'Microsoft.NET/Framework/v4.0.30319/csc.exe')
)
$compilerPath = $compilerCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $compilerPath) { throw 'csc.exe (.NET Framework 4.8) not found.' }

$runnerExe = Join-Path $outputRoot 'ControlRunner.exe'
$sources = @(Get-ChildItem -LiteralPath $projectRoot -Filter '*.cs' -File |
    Where-Object { $_.Name -ne 'Program.cs' -and $_.Name -ne 'SelfTests.cs' } |
    Sort-Object Name | Select-Object -ExpandProperty FullName)
$sources += (Join-Path $toolRoot 'Runner.cs')

$arguments = @(
    '/nologo','/utf8output','/target:exe','/optimize+',('/out:' + $runnerExe),
    '/reference:System.dll','/reference:System.Core.dll','/reference:System.Data.dll',
    '/reference:System.Drawing.dll','/reference:System.Windows.Forms.dll',
    '/reference:System.Web.Extensions.dll','/reference:System.IO.Compression.dll',
    '/reference:System.IO.Compression.FileSystem.dll','/reference:System.Security.dll',
    '/reference:System.Runtime.Serialization.dll',
    ('/reference:' + (Join-Path $projectRoot 'System.Data.SQLite.dll'))
)
$arguments += $sources
& $compilerPath @arguments
if ($LASTEXITCODE -ne 0) { throw ('runner compile failed: ' + $LASTEXITCODE) }

# 测试进程需要协议与配置在 exe 旁边（demo 套件会从这里复制到隔离目录）。
foreach ($name in @('protocol.json','control-config.json','System.Data.SQLite.dll')) {
    Copy-Item -LiteralPath (Join-Path $projectRoot $name) -Destination $outputRoot -Force
}
Copy-Item -LiteralPath (Join-Path $projectRoot 'x64\SQLite.Interop.dll') -Destination (Join-Path $outputRoot 'x64') -Force -ErrorAction SilentlyContinue

# 存储套件会写 Path.GetTempPath()，这里把 TEMP 指到仓库内，避免沙箱临时目录权限问题。
$env:TEMP = Join-Path $outputRoot 'temp'
$env:TMP = $env:TEMP
New-Item -ItemType Directory -Path $env:TEMP -Force | Out-Null

$failed = 0
foreach ($suite in $Suites) {
    $root = Join-Path $outputRoot ($suite + '-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $root -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $projectRoot 'protocol.json') -Destination $root -Force
    Copy-Item -LiteralPath (Join-Path $projectRoot 'control-config.json') -Destination $root -Force
    Write-Host ('=== ' + $suite + ' ===')
    & $runnerExe $root $suite
    if ($LASTEXITCODE -ne 0) { $failed++; Write-Host ('FAILED: ' + $suite) }
    if (-not $KeepRoots) {
        # 失败时保留现场；成功时只保留 demo 套件的截图。
        if ($LASTEXITCODE -eq 0 -and $suite -ne 'demo') { Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue }
    }
}
if ($failed -gt 0) { throw ($failed.ToString() + ' suite(s) failed; see ' + $outputRoot) }
Write-Host ('ALL PASS: ' + ($Suites -join ', '))
