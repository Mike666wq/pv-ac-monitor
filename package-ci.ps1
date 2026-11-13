param(
    [string]$Version = '',
    [string]$OutputDirectory = ''
)

$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$binRoot = Join-Path $projectRoot 'bin'
$exePath = Join-Path $binRoot 'ExperimentMonitorDemo.exe'
if (-not (Test-Path -LiteralPath $exePath -PathType Leaf)) {
    throw 'Run build-source.ps1 -Test before packaging.'
}

$fileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($exePath).FileVersion
$detectedVersion = $fileVersion -replace '\.0$',''
if ([string]::IsNullOrWhiteSpace($Version)) { $Version = $detectedVersion }
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Version must use x.y.z format.' }
if ($Version -ne $detectedVersion) { throw "Package version $Version does not match EXE version $detectedVersion." }

# 文档版本必须与发布版本一致。
# 这个漂移在本项目里反复出现过（0.8.0 -> 0.8.8 期间有五次 README 忘了跟着升），
# 每次都是在发布之后才被人工发现。既然能自动查，就不要靠记性。
$readmePath = Join-Path $projectRoot 'README.md'
if (Test-Path -LiteralPath $readmePath -PathType Leaf) {
    $readmeText = Get-Content -LiteralPath $readmePath -Raw -Encoding UTF8
    $declared = [regex]::Match($readmeText, '当前版本[:：]\s*\*\*(\d+\.\d+\.\d+)\*\*')
    if (-not $declared.Success) {
        throw 'README.md 里找不到「当前版本：**x.y.z**」，无法校验版本一致性。'
    }
    if ($declared.Groups[1].Value -ne $Version) {
        throw ("README.md 声明的当前版本是 " + $declared.Groups[1].Value + "，与实际打包版本 " + $Version + " 不一致。请先更新 README.md。")
    }
}

$changeLogPath = Join-Path $projectRoot 'CHANGELOG.md'
if (Test-Path -LiteralPath $changeLogPath -PathType Leaf) {
    $changeLogText = Get-Content -LiteralPath $changeLogPath -Raw -Encoding UTF8
    if ($changeLogText -notmatch [regex]::Escape('## ' + $Version + ' —')) {
        throw ("CHANGELOG.md 缺少 '## " + $Version + " — ...' 这一条。请先补变更记录。")
    }
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $projectRoot 'artifacts'
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

$stageRoot = Join-Path ([IO.Path]::GetTempPath()) ('pv-ac-monitor-package-' + [Guid]::NewGuid().ToString('N'))
$appRoot = Join-Path $stageRoot 'ExperimentMonitorDemo'
$zipPath = Join-Path $OutputDirectory ("PV-AC-Monitor-Windows-$Version.zip")

$runtimeFiles = [ordered]@{
    'ExperimentMonitorDemo.exe' = $exePath
    'ExperimentMonitorDemo.exe.config' = (Join-Path $projectRoot 'ExperimentMonitorDemo.exe.config')
    'protocol.json' = (Join-Path $projectRoot 'protocol.json')
    'control-config.json' = (Join-Path $projectRoot 'control-config.json')
    'CONTROL-DESIGN.md' = (Join-Path $projectRoot 'CONTROL-DESIGN.md')
    'System.Data.SQLite.dll' = (Join-Path $projectRoot 'System.Data.SQLite.dll')
    'x64/SQLite.Interop.dll' = (Join-Path $projectRoot 'x64\SQLite.Interop.dll')
    'x86/SQLite.Interop.dll' = (Join-Path $projectRoot 'x86\SQLite.Interop.dll')
    'README.md' = (Join-Path $projectRoot 'README.md')
    'docs\field\INSTALL.md' = (Join-Path $projectRoot 'docs\field\INSTALL.md')
    'docs\field\TROUBLESHOOTING.md' = (Join-Path $projectRoot 'docs\field\TROUBLESHOOTING.md')
    'docs\field\OPERATIONS.md' = (Join-Path $projectRoot 'docs\field\OPERATIONS.md')
    'docs\field\CONTROL-ENABLE.md' = (Join-Path $projectRoot 'docs\field\CONTROL-ENABLE.md')
    'docs\field\ACCEPTANCE-CHECKLIST.md' = (Join-Path $projectRoot 'docs\field\ACCEPTANCE-CHECKLIST.md')
    'docs\field\BLOCK-READ-VERIFICATION.md' = (Join-Path $projectRoot 'docs\field\BLOCK-READ-VERIFICATION.md')
    'docs\field\LIVE-VERIFICATION.md' = (Join-Path $projectRoot 'docs\field\LIVE-VERIFICATION.md')
    'CHANGELOG.md' = (Join-Path $projectRoot 'CHANGELOG.md')
}

try {
    if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
    New-Item -ItemType Directory -Path $appRoot -Force | Out-Null
    $hashes = @()
    foreach ($relative in $runtimeFiles.Keys) {
        $inputFile = $runtimeFiles[$relative]
        if (-not (Test-Path -LiteralPath $inputFile -PathType Leaf)) { throw ('Missing runtime dependency: ' + $relative) }
        $outputFile = Join-Path $appRoot $relative
        New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($outputFile)) -Force | Out-Null
        Copy-Item -LiteralPath $inputFile -Destination $outputFile -Force
        $hashes += [ordered]@{
            path = $relative
            sha256 = (Get-FileHash -LiteralPath $outputFile -Algorithm SHA256).Hash
            bytes = (Get-Item -LiteralPath $outputFile).Length
        }
    }
    $commitSha = $env:GITHUB_SHA
    if (-not $commitSha) { try { $commitSha = (& git -C $PSScriptRoot rev-parse HEAD 2>$null) } catch { } }
    if (-not $commitSha) { $commitSha = 'unknown' }
    $manifest = [ordered]@{
        app = 'PV AC Monitor'
        version = $Version
        commit = $commitSha
        built_at = [DateTimeOffset]::UtcNow.ToString('o')
        platform = 'Windows x86/x64'
        framework = '.NET Framework 4.8'
        signature = 'unsigned'
        files = $hashes
    }
    [IO.File]::WriteAllText(
        (Join-Path $appRoot 'package-manifest.json'),
        ($manifest | ConvertTo-Json -Depth 5),
        (New-Object Text.UTF8Encoding($false))
    )
    Compress-Archive -LiteralPath $appRoot -DestinationPath $zipPath -CompressionLevel Optimal
    Write-Output ('Package: ' + $zipPath)
    Get-FileHash -LiteralPath $zipPath -Algorithm SHA256
}
finally {
    if (Test-Path -LiteralPath $stageRoot) { Remove-Item -LiteralPath $stageRoot -Recurse -Force }
}
