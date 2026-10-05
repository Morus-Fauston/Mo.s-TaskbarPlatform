param(
    [ValidateSet('all', 'event', 'event-production', 'event-settings', 'broker', 'template', 'settings', 'dynamic', 'timer', 'hint', 'hint-window', 'interactive-hint', 'interactive-hint-motion', 'interactive-hint-production', 'flyout', 'flyout-production', 'flyout-create', 'flyout-scheduling', 'preset', 'organization')][string]$Scenario = 'all',
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
$env:MSBUILDDISABLENODEREUSE='1'
dotnet build "$PSScriptRoot/Mtp.Host.WindowTests.csproj" --configuration Release -nr:false "-p:OutDir=$outputPath/"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$startedAt = [DateTime]::UtcNow
$previousDirectories = @(Get-ChildItem -LiteralPath $outputPath -Directory | Select-Object -ExpandProperty Name)
$logPath = Join-Path $outputPath 'window-tests.log'
if (Test-Path -LiteralPath $logPath) { Remove-Item -LiteralPath $logPath }
$run = [Diagnostics.Process]::new()
$run.StartInfo = [Diagnostics.ProcessStartInfo]::new("$outputPath/Mtp.Host.WindowTests.exe")
$run.StartInfo.UseShellExecute = $false
$run.StartInfo.WindowStyle = 'Hidden'
if ($Scenario -eq 'event-production') { $run.StartInfo.Arguments = '--event-production-only' }
if ($Scenario -eq 'event') { $run.StartInfo.Arguments = '--event-only' }
if ($Scenario -eq 'event-settings') { $run.StartInfo.Arguments = '--event-settings-only' }
if ($Scenario -eq 'broker') { $run.StartInfo.Arguments = '--broker-only' }
if ($Scenario -eq 'template') { $run.StartInfo.Arguments = '--template-only' }
if ($Scenario -eq 'timer') { $run.StartInfo.Arguments = '--timer-only' }
if ($Scenario -eq 'flyout') { $run.StartInfo.Arguments = '--flyout-only' }
if ($Scenario -eq 'hint') { $run.StartInfo.Arguments = '--hint-only' }
if ($Scenario -eq 'interactive-hint-motion') { $run.StartInfo.Arguments = '--interactive-hint-motion-only' }
if ($Scenario -eq 'interactive-hint') { $run.StartInfo.Arguments = '--interactive-hint-only' }
if ($Scenario -eq 'interactive-hint-production') { $run.StartInfo.Arguments = '--interactive-hint-production-only' }
if ($Scenario -eq 'hint-window') { $run.StartInfo.Arguments = '--hint-window-only' }
if ($Scenario -eq 'flyout-scheduling') { $run.StartInfo.Arguments = '--flyout-scheduling-only' }
if ($Scenario -eq 'flyout-create') { $run.StartInfo.Arguments = '--flyout-create-only' }
if ($Scenario -eq 'flyout-production') { $run.StartInfo.Arguments = '--flyout-production-only' }
if ($Scenario -eq 'organization') { $run.StartInfo.Arguments = '--organization-only' }
if ($Scenario -eq 'preset') { $run.StartInfo.Arguments = '--preset-only' }
if ($Scenario -eq 'dynamic') { $run.StartInfo.Arguments = '--dynamic-only' }
if ($Scenario -eq 'settings') { $run.StartInfo.Arguments = '--settings-only' }
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
        if ($directory.Name -notin $previousDirectories -and ($directory.Name -like 'console-*' -or $directory.Name -like 'display-selection-*' -or $directory.Name -like 'broker-*' -or $directory.Name -like 'template-*' -or $directory.Name -like 'settings-*' -or $directory.Name -like 'dynamic-*' -or $directory.Name -like 'timer-*' -or $directory.Name -like 'preset-*' -or $directory.Name -like 'flyout-*' -or $directory.Name -like 'organization-*' -or $directory.Name -like 'event-*' -or $directory.Name -like 'flyout-stack-*' -or $directory.Name -like 'interactive-hint-*' -or $directory.Name -like 'hint-input-*' -or $directory.Name -like 'hint-settings-native-*')) {
            Copy-Item -LiteralPath $directory.FullName -Destination $evidencePath -Recurse
        }
    }
    Write-Output "Evidence archived: $evidencePath"
}
exit 0
