param(
    [string]$OutputDirectory = "$PSScriptRoot/bin/window-regression",
    [string]$EvidenceDirectory = "$PSScriptRoot/../../.scratch/二期开发/evidence/HostWindowRegression/run-$(Get-Date -Format 'yyyyMMdd-HHmmss-fff')-$([Guid]::NewGuid().ToString('N').Substring(0, 8))"
)
$ErrorActionPreference = 'Stop'
$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
$evidencePath = [IO.Path]::GetFullPath($EvidenceDirectory)
if ($evidencePath.TrimEnd('\', '/') -eq $outputPath.TrimEnd('\', '/') -or $evidencePath.StartsWith($outputPath.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'EvidenceDirectory must be separate from the build output directory.'
}
if (Test-Path -LiteralPath $evidencePath) { throw 'EvidenceDirectory already exists; select a new run directory.' }
dotnet build "$PSScriptRoot/Mtp.Host.WindowTests.csproj" --configuration Release "-p:OutDir=$outputPath/"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$startedAt = [DateTime]::UtcNow
$previousDirectories = @(Get-ChildItem -LiteralPath $outputPath -Directory | Select-Object -ExpandProperty Name)
$logPath = Join-Path $outputPath 'window-tests.log'
if (Test-Path -LiteralPath $logPath) { Remove-Item -LiteralPath $logPath }
$run = [Diagnostics.Process]::new()
$run.StartInfo = [Diagnostics.ProcessStartInfo]::new("$outputPath/Mtp.Host.WindowTests.exe")
$run.StartInfo.UseShellExecute = $false
$run.StartInfo.WindowStyle = 'Hidden'
try {
    if (-not $run.Start()) { throw 'Could not start the WinUI regression process.' }
    if (-not $run.WaitForExit(60000)) {
        $run.Kill()
        $run.WaitForExit()
        throw 'WinUI regression timed out; only the test process was terminated.'
    }
    $testExitCode = $run.ExitCode
    $log = Get-Content -LiteralPath $logPath
    $log
    if ($testExitCode -ne 0) { throw "WinUI regression failed with exit code $testExitCode." }
    if (-not ($log -match '^PASS: \d+ native frame samples\.$')) { throw 'WinUI regression ended without its completion marker.' }
}
finally {
    $run.Dispose()
    New-Item -ItemType Directory -Path $evidencePath | Out-Null
    $capturedBinaries = Join-Path $evidencePath 'binary-identity'
    New-Item -ItemType Directory -Path $capturedBinaries | Out-Null
    foreach ($binaryName in @('Mtp.Host.dll', 'Mtp.Host.exe')) {
        Copy-Item -LiteralPath (Join-Path $outputPath $binaryName) -Destination $capturedBinaries
    }
    # Keep binaries in bin; preserve this run's logs, screenshots and report directories separately.
    foreach ($file in Get-ChildItem -LiteralPath $outputPath -File) {
        if ($file.Extension -in @('.log', '.png', '.jsonl', '.csv') -and $file.LastWriteTimeUtc -ge $startedAt) {
            Copy-Item -LiteralPath $file.FullName -Destination $evidencePath
        }
    }
    foreach ($directory in Get-ChildItem -LiteralPath $outputPath -Directory) {
        if ($directory.Name -notin $previousDirectories -and ($directory.Name -like 'console-*' -or $directory.Name -like 'display-selection-*')) {
            Copy-Item -LiteralPath $directory.FullName -Destination $evidencePath -Recurse
        }
    }
    Write-Output "Evidence archived: $evidencePath"
}
exit 0
