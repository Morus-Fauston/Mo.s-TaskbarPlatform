param(
    [ValidateSet('all', 'event', 'event-production', 'event-settings', 'broker', 'template', 'settings', 'dynamic', 'timer', 'hint', 'hint-window', 'interactive-hint', 'interactive-hint-motion', 'interactive-hint-production', 'flyout', 'flyout-production', 'flyout-create', 'flyout-scheduling', 'preset', 'organization', 'visual-environment', 'environment-recovery', 'combination-stability')][string]$Scenario = 'all',
    # Tiered entry for the combination fixture. 'smoke' is the daily automated tier (a few rounds);
    # 'soak' is the 50-round long run kept for a major version. Both drive the same fixture.
    [ValidateSet('smoke', 'soak')][string]$CombinationProfile = 'smoke',
    # Controlled counterexample for the exit-code contract: software | fixture | environment | resource.
    # Empty means a normal run. Used to prove each failure class is really reachable.
    [string]$InjectFailure = '',
    # Opt-in desktop isolation. Off by default so a normal run measures the product as shipped.
    [switch]$IsolateInput,
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
# Environment is inherited by the child, so the fixture's switches are set here rather than duplicated
# in every scenario's argument list. Run.ps1 keeps them visible in one place, and policy.json records
# what the run actually used.
if ($Scenario -eq 'combination-stability') {
    $run.StartInfo.Environment['MTP_COMBINATION_PROFILE'] = $CombinationProfile
    if ($InjectFailure) { $run.StartInfo.Environment['MTP_COMBINATION_INJECT_FAILURE'] = $InjectFailure }
    if ($IsolateInput) { $run.StartInfo.Environment['MTP_COMBINATION_ISOLATE_INPUT'] = '1' }
}
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
if ($Scenario -eq 'visual-environment') { $run.StartInfo.Arguments = '--visual-environment-only' }
if ($Scenario -eq 'environment-recovery') { $run.StartInfo.Arguments = '--environment-recovery-only' }
if ($Scenario -eq 'combination-stability') { $run.StartInfo.Arguments = "--combination-stability-only --combination-profile $CombinationProfile" }
if ($Scenario -eq 'preset') { $run.StartInfo.Arguments = '--preset-only' }
if ($Scenario -eq 'dynamic') { $run.StartInfo.Arguments = '--dynamic-only' }
if ($Scenario -eq 'settings') { $run.StartInfo.Arguments = '--settings-only' }
try {
    if (-not $run.Start()) { throw 'Could not start the WinUI regression process.' }
    # The combination fixture has its own work budget (3 min for smoke, 10 min for soak); allow bounded
    # cleanup and evidence flush on top, and widen it further for an injected counterexample run, which
    # tears the session down through the failure path.
    $timeoutMilliseconds = if ($Scenario -eq 'combination-stability') {
        if ($CombinationProfile -eq 'soak') { 620000 } else { 200000 }
    } else { 60000 }
    if (-not $run.WaitForExit($timeoutMilliseconds)) {
        $run.Kill()
        $run.WaitForExit()
        throw 'WinUI regression timed out; only the test process was terminated.'
    }
    $testExitCode = $run.ExitCode
    $log = Get-Content -LiteralPath $logPath
    $log
    # An injected counterexample is *expected* to fail. It passes only when it produced the failure class
    # and exit code the code under test claims, so the harness verifies the contract instead of the run.
    $expectedExit = switch ($InjectFailure) {
        'software' { 10 } 'fixture' { 11 } 'environment' { 12 } 'resource' { 13 } default { $null }
    }
    if ($null -ne $expectedExit) {
        if ($testExitCode -ne $expectedExit) { throw "Injected $InjectFailure run exited $testExitCode; expected $expectedExit." }
        $verdict = $log | Where-Object { $_ -match '^combination-stability-result: ' } | Select-Object -Last 1
        if (-not $verdict) { throw 'Injected combination run ended without its result marker.' }
        if ($verdict -notmatch "exitCode=$expectedExit;") { throw "Injected run reported the wrong exit code: $verdict" }
        if ($verdict -notmatch "failureClass=$InjectFailure-failure;") { throw "Injected run reported the wrong failure class: $verdict" }
        Write-Output "Injected $InjectFailure counterexample verified: $verdict"
    }
    elseif ($testExitCode -ne 0) { throw "WinUI regression failed with exit code $testExitCode." }
    if ($Scenario -eq 'combination-stability' -and $null -eq $expectedExit) {
        # The combination scenario never samples dock frames, so the shared "PASS: N native frame
        # samples." marker is a no-op here (it always reads PASS: 0). Verify this scenario's own
        # verdict instead: it must report every requested round of its profile with a verified cleanup.
        #
        # This runs before the generic exit-code check above would matter, and it re-derives the verdict
        # from the report rather than trusting the process code alone: the report is the artifact the
        # ticket asks a reader to consult, so the runner must agree with it.
        $verdict = $log | Where-Object { $_ -match '^combination-stability-result: ' } | Select-Object -Last 1
        if (-not $verdict) { throw 'Combination regression ended without its result marker.' }
        $expectedRounds = if ($CombinationProfile -eq 'soak') { 50 } else { 3 }
        if ($verdict -notmatch "roundsCompleted=$expectedRounds;") { throw "Combination regression did not complete $expectedRounds rounds: $verdict" }
        if ($verdict -notmatch "requestedRounds=$expectedRounds;") { throw "Combination regression ran the wrong profile: $verdict" }
        if ($testExitCode -ne 0) { throw "Combination regression exited $testExitCode; report: $verdict" }
        if ($verdict -notmatch 'cleanupVerified=True;') { throw "Combination regression did not verify cleanup: $verdict" }
        if ($verdict -notmatch 'verdict=passed;') { throw "Combination regression did not report a passing verdict: $verdict" }
        Write-Output "Combination $CombinationProfile verified: $verdict"
    }
    elseif ($Scenario -ne 'combination-stability' -and -not ($log -match '^PASS: \d+ native frame samples\.$')) { throw 'WinUI regression ended without its completion marker.' }
}
finally {
    $run.Dispose()
    New-Item -ItemType Directory -Path $evidencePath | Out-Null
    $capturedBinaries = Join-Path $evidencePath 'binary-identity'
    New-Item -ItemType Directory -Path $capturedBinaries | Out-Null
    foreach ($binaryName in @('Mtp.Host.dll', 'Mtp.Host.exe', 'Mtp.Host.WindowTests.dll', 'Mtp.Host.Runtime.dll', 'Mtp.Platform.Core.dll', 'Mtp.Contracts.dll', 'Mtp.Transport.dll')) {
        Copy-Item -LiteralPath (Join-Path $outputPath $binaryName) -Destination $capturedBinaries
    }
    if ($Scenario -eq 'combination-stability') {
        foreach ($child in @('Broker', 'CounterService')) {
            Copy-Item -LiteralPath (Join-Path $outputPath $child) -Destination $capturedBinaries -Recurse
        }
        Get-ChildItem -LiteralPath $capturedBinaries -File -Recurse | Get-FileHash -Algorithm SHA256 |
            Select-Object Path, Hash | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $evidencePath 'binary-hashes.json') -Encoding UTF8
        git -C "$PSScriptRoot/../.." rev-parse HEAD | Set-Content -LiteralPath (Join-Path $evidencePath 'source-head.txt')
        git -C "$PSScriptRoot/../.." status --short | Set-Content -LiteralPath (Join-Path $evidencePath 'source-status.txt')
        Get-ChildItem -LiteralPath "$PSScriptRoot/../../src", $PSScriptRoot -Recurse -File -Filter '*.cs' |
            Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } | Get-FileHash -Algorithm SHA256 |
            Select-Object Path, Hash | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $evidencePath 'source-hashes.json') -Encoding UTF8
    }
    # Keep binaries in bin; preserve this run's logs, screenshots and report directories separately.
    foreach ($file in Get-ChildItem -LiteralPath $outputPath -File) {
        if ($file.Extension -in @('.log', '.png', '.json', '.jsonl', '.csv') -and $file.LastWriteTimeUtc -ge $startedAt) {
            Copy-Item -LiteralPath $file.FullName -Destination $evidencePath
        }
    }
    foreach ($directory in Get-ChildItem -LiteralPath $outputPath -Directory) {
        if ($directory.Name -notin $previousDirectories -and ($directory.Name -like 'console-*' -or $directory.Name -like 'display-selection-*' -or $directory.Name -like 'broker-*' -or $directory.Name -like 'template-*' -or $directory.Name -like 'settings-*' -or $directory.Name -like 'dynamic-*' -or $directory.Name -like 'timer-*' -or $directory.Name -like 'preset-*' -or $directory.Name -like 'flyout-*' -or $directory.Name -like 'organization-*' -or $directory.Name -like 'event-*' -or $directory.Name -like 'flyout-stack-*' -or $directory.Name -like 'interactive-hint-*' -or $directory.Name -like 'hint-input-*' -or $directory.Name -like 'hint-settings-native-*' -or $directory.Name -like 'visual-environment-*' -or $directory.Name -like 'environment-recovery-*' -or $directory.Name -like 'combination-stability-*')) {
            Copy-Item -LiteralPath $directory.FullName -Destination $evidencePath -Recurse
        }
    }
    # handle-types.jsonl lives inside the per-run combination-stability-* directory, copied above; the
    # flat-file sweep only covers the output root, so state that explicitly to keep it discoverable.
    Get-ChildItem -LiteralPath $evidencePath -Recurse -Filter 'handle-types.jsonl' -ErrorAction SilentlyContinue | ForEach-Object { Write-Output "Handle type census archived: $($_.FullName)" }
    Write-Output "Evidence archived: $evidencePath"
}
exit 0
