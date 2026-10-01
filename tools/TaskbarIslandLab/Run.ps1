param(
    [ValidateSet('owned','top-level','empty','explorer')][string]$Mode = 'owned',
    [ValidateSet('verify','measure','manual','list-targets')][string]$Scenario = 'verify',
    [string]$Monitor,
    [string]$ParentHwnd,
    [ValidateSet('none','acrylic','mica')][string]$Material = 'none',
    [ValidateSet('0','0.5','1')][string]$Alpha = '0.5',
    [ValidateSet('system','light','dark')][string]$Theme = 'system',
    [ValidateSet(0,1,30,60)][int]$Hz = 30,
    [int]$VisibleSeconds = 600,
    [int]$HiddenSeconds = 20,
    [int]$TimeoutSeconds = 45,
    [int]$X = 80,
    [int]$Y = 80,
    [switch]$Diagnostics,
    [string]$OutputDirectory,
    [switch]$Build
)
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'TaskbarIslandLab.csproj'
$exe = Join-Path $PSScriptRoot 'bin\Release\net10.0-windows10.0.26100.0\TaskbarIslandLab.exe'
if ($Build) {
    dotnet build $project --configuration Release
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
}
if (-not (Test-Path -LiteralPath $exe)) { throw 'Build the Release executable first or pass -Build.' }
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $PSScriptRoot ('..\..\.scratch\二期开发\verification\05F\run-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
}
$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath (Join-Path $outputPath 'events.jsonl')) { throw 'Use a fresh output directory; evidence is not overwritten.' }
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
if ($Scenario -eq 'manual' -and -not $PSBoundParameters.ContainsKey('TimeoutSeconds')) { $TimeoutSeconds = 1800 }
$argsList = @('--mode', $Mode, '--scenario', $Scenario, '--material', $Material, '--alpha', $Alpha, '--theme', $Theme,
    '--hz', "$Hz", '--visible-seconds', "$VisibleSeconds", '--hidden-seconds', "$HiddenSeconds", '--timeout-seconds', "$TimeoutSeconds",
    '--x', "$X", '--y', "$Y", '--output', $outputPath, '--diagnostics', $(if ($Diagnostics) { 'on' } else { 'off' }))
if ($Monitor) { $argsList += @('--monitor', $Monitor) }
if ($ParentHwnd) { $argsList += @('--parent-hwnd', $ParentHwnd) }
function Quote-NativeArgument([string]$Value) {
    '"' + [regex]::Replace([regex]::Replace($Value, '(\\*)"', '$1$1\"'), '(\\+)$', '$1$1') + '"'
}
$argumentText = ($argsList | ForEach-Object { Quote-NativeArgument $_ }) -join ' '
# Keep the process handle from creation so even an immediate failure has an exit code.
$startInfo = [Diagnostics.ProcessStartInfo]::new($exe, $argumentText)
$startInfo.UseShellExecute = $false
$startInfo.CreateNoWindow = $true
$startInfo.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
$run = [Diagnostics.Process]::Start($startInfo)
$record = [ordered]@{ processId = $run.Id; executable = $exe; args = $argsList; startedUtc = [DateTime]::UtcNow.ToString('o'); forcedTermination = $false }
$record | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $outputPath 'launcher.json') -Encoding UTF8
$limit = if ($Scenario -eq 'measure') { 5 + 10 + $VisibleSeconds + $HiddenSeconds + 10 + 45 } else { $TimeoutSeconds + 20 }
$watch = [Diagnostics.Stopwatch]::StartNew()
try {
    while (-not $run.WaitForExit(1000)) {
        if ($watch.Elapsed.TotalSeconds -gt $limit) {
            [IO.File]::WriteAllText((Join-Path $outputPath 'stop.request'), 'watchdog')
            if (-not $run.WaitForExit(10000)) { $run.Kill(); $run.WaitForExit(); $record.forcedTermination = $true }
            throw 'Watchdog timeout; only this lab process was stopped. Inspect cleanup evidence.'
        }
    }
    $run.WaitForExit()
    $record.exitCode = $run.ExitCode
    if ($run.ExitCode -ne 0) { throw "Lab exited with code $($run.ExitCode). Evidence: $outputPath" }
    if ($Scenario -ne 'list-targets') {
        $summaryPath = Join-Path $outputPath 'summary.json'
        if (-not (Test-Path -LiteralPath $summaryPath)) { throw 'Process exited without an application summary.' }
        $summary = Get-Content -Encoding UTF8 -Raw -LiteralPath $summaryPath | ConvertFrom-Json
        if ($summary.exitCode -ne 0) { throw 'Application summary reports failure.' }
        if ($Scenario -eq 'verify' -and $summary.reason -ne 'owned-integration-passed') { throw 'Verification did not finish.' }
        if ($Scenario -eq 'measure' -and $summary.reason -ne 'measurement-complete') { throw 'Measurement was stopped before completion.' }
    }
    Write-Output "PASS: $Mode / $Scenario; evidence: $outputPath"
}
finally {
    $record.finishedUtc = [DateTime]::UtcNow.ToString('o')
    $record | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $outputPath 'launcher.json') -Encoding UTF8
    $run.Dispose()
}
