param([switch]$Test)

$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$outputRoot = Join-Path $projectRoot 'bin'
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null

$compilerCandidates = @(
    (Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'),
    (Join-Path $env:WINDIR 'Microsoft.NET/Framework/v4.0.30319/csc.exe')
)
$compilerPath = $compilerCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $compilerPath) { throw 'Microsoft .NET Framework compiler (csc.exe) was not found. Install the .NET Framework 4.8 Developer Pack or use a Windows machine with .NET Framework build tools.' }

$sqlitePath = Join-Path $projectRoot 'System.Data.SQLite.dll'
$configPath = Join-Path $projectRoot 'ExperimentMonitorDemo.exe.config'
$protocolPath = Join-Path $projectRoot 'protocol.json'
$manifestPath = Join-Path $projectRoot 'ExperimentMonitorDemo.manifest'
foreach ($required in @($sqlitePath,$configPath,$protocolPath,(Join-Path $projectRoot 'x64/SQLite.Interop.dll'),(Join-Path $projectRoot 'x86/SQLite.Interop.dll'))) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw ('Required build dependency is missing: ' + [IO.Path]::GetFileName($required)) }
}

$sources = @(Get-ChildItem -LiteralPath $projectRoot -Filter '*.cs' -File | Sort-Object Name | Select-Object -ExpandProperty FullName)
if ($sources.Count -eq 0) { throw 'No C# source files were found beside build-source.ps1.' }
$exePath = Join-Path $outputRoot 'ExperimentMonitorDemo.exe'
$compilerArguments = @(
    '/nologo',
    '/utf8output',
    '/target:winexe',
    '/optimize+',
    ('/out:' + $exePath),
    '/reference:System.dll',
    '/reference:System.Core.dll',
    '/reference:System.Data.dll',
    '/reference:System.Drawing.dll',
    '/reference:System.Windows.Forms.dll',
    '/reference:System.Web.Extensions.dll',
    '/reference:System.IO.Compression.dll',
    '/reference:System.IO.Compression.FileSystem.dll',
    '/reference:System.Security.dll',
    '/reference:System.Runtime.Serialization.dll',
    ('/reference:' + $sqlitePath)
)
if (Test-Path -LiteralPath $manifestPath -PathType Leaf) { $compilerArguments += ('/win32manifest:' + $manifestPath) }
$compilerArguments += $sources
& $compilerPath @compilerArguments
if ($LASTEXITCODE -ne 0) { throw ('C# compilation failed with exit code ' + $LASTEXITCODE) }

# Stage the runtime files needed beside the newly compiled executable.
foreach ($name in @('ExperimentMonitorDemo.exe.config','protocol.json','System.Data.SQLite.dll')) {
    Copy-Item -LiteralPath (Join-Path $projectRoot $name) -Destination $outputRoot -Force
}

function Copy-RuntimeFiles([string]$destination) {
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    foreach ($name in @('ExperimentMonitorDemo.exe','ExperimentMonitorDemo.exe.config','protocol.json','System.Data.SQLite.dll')) {
        $sourceRoot = if ($name -eq 'ExperimentMonitorDemo.exe') { $outputRoot } else { $projectRoot }
        $sourcePath = [IO.Path]::GetFullPath((Join-Path $sourceRoot $name))
        $targetPath = [IO.Path]::GetFullPath((Join-Path $destination $name))
        if ([String]::Equals($sourcePath,$targetPath,[StringComparison]::OrdinalIgnoreCase)) { continue }
        Copy-Item -LiteralPath $sourcePath -Destination $targetPath -Force
    }
    foreach ($architecture in @('x64','x86')) {
        $sourceDirectory = Join-Path $projectRoot $architecture
        $targetDirectory = Join-Path $destination $architecture
        New-Item -ItemType Directory -Path $targetDirectory -Force | Out-Null
        Get-ChildItem -LiteralPath $sourceDirectory -Force | Copy-Item -Destination $targetDirectory -Recurse -Force
    }
}

Copy-RuntimeFiles $outputRoot
Write-Output ('Build succeeded: ' + $exePath)

if ($Test) {
    # The executable resolves all runtime data relative to its base directory. Copy
    # it to a fresh directory so tests cannot read or write the developer's data,
    # settings, logs, or databases beside the source checkout.
    $testRoot = Join-Path $outputRoot ('self-test-' + [Guid]::NewGuid().ToString('N'))
    Copy-RuntimeFiles $testRoot
    $testTempRoot = Join-Path $testRoot 'temp'
    New-Item -ItemType Directory -Path $testTempRoot -Force | Out-Null
    $startInfo = New-Object Diagnostics.ProcessStartInfo
    $startInfo.FileName = Join-Path $testRoot 'ExperimentMonitorDemo.exe'
    $startInfo.Arguments = '--self-test'
    $startInfo.WorkingDirectory = $testRoot
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.EnvironmentVariables['TEMP'] = $testTempRoot
    $startInfo.EnvironmentVariables['TMP'] = $testTempRoot
    $process = [Diagnostics.Process]::Start($startInfo)
    try {
        if (-not $process.WaitForExit(600000)) {
            $process.Kill()
            $process.WaitForExit()
            throw ('Self-test timed out. Isolated evidence directory: ' + $testRoot)
        }
        if ($process.ExitCode -ne 0) {
            $errorPath = Join-Path $testRoot 'test-error.txt'
            if (Test-Path -LiteralPath $errorPath) { Get-Content -LiteralPath $errorPath }
            throw ('Self-test failed with exit code ' + $process.ExitCode + '. Isolated evidence directory: ' + $testRoot)
        }
    } finally { $process.Dispose() }

    $resultNames = @('self-test-result.json','core-test-result.json','records-test-result.json','cloud-test-result.json','trend-test-result.json','ui-test-result.json','workflow-test-result.json','extended-test-result.json')
    foreach ($name in $resultNames) {
        $resultPath = Join-Path $testRoot $name
        if (-not (Test-Path -LiteralPath $resultPath -PathType Leaf)) { throw ('Self-test did not produce ' + $name + '. Evidence directory: ' + $testRoot) }
        $result = Get-Content -LiteralPath $resultPath -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($result.passed -ne $true) { throw ('Self-test evidence failed: ' + $name + '. Evidence directory: ' + $testRoot) }
    }
    $uiResult = Get-Content -LiteralPath (Join-Path $testRoot 'ui-test-result.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($uiResult.cases.Count -ne 9 -or $uiResult.screenshots.Count -lt 49) { throw ('Self-test UI evidence is incomplete. Evidence directory: ' + $testRoot) }
    Write-Output ('Self-test passed. Isolated results: ' + $testRoot)
}
