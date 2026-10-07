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
    'System.Data.SQLite.dll' = (Join-Path $projectRoot 'System.Data.SQLite.dll')
    'x64/SQLite.Interop.dll' = (Join-Path $projectRoot 'x64\SQLite.Interop.dll')
    'x86/SQLite.Interop.dll' = (Join-Path $projectRoot 'x86\SQLite.Interop.dll')
    'README.md' = (Join-Path $projectRoot 'README.md')
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
    $manifest = [ordered]@{
        app = 'PV AC Monitor'
        version = $Version
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
