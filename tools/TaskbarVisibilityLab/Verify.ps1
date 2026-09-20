param([switch]$NoBuild)
$ErrorActionPreference = 'Stop'
if (-not $NoBuild) {
    dotnet build (Join-Path $PSScriptRoot 'TaskbarVisibilityLab.csproj') --configuration Release --no-restore --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
}
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
foreach ($mode in @('fixture', 'baseline')) {
    $runName = 'verify-{0}-{1}' -f $mode, [Guid]::NewGuid().ToString('N')
    $output = Join-Path $repoRoot ".scratch/二期开发/verification/05E/$runName"
    # Exercise the same launcher the user runs, including script decoding and path handling.
    $launch = & (Join-Path $PSScriptRoot 'Run.ps1') -Mode $mode -Seconds 2 -OutputDirectory $output -NoBuild
    $process = Get-Process -Id $launch.ProcessId
    $null = $process.Handle # Keep a handle so Windows PowerShell 5.1 can read ExitCode after exit.
    if (-not $process.WaitForExit(15000)) {
        # Only this test's own child is terminated; keep its diagnostics for investigation.
        $process.Kill()
        throw "Timed out: $mode; log: $output"
    }
    if ($process.ExitCode -ne 0) { throw "Process failed ($($process.ExitCode)): $output" }
    $summary = Get-Content -LiteralPath (Join-Path $output 'summary.json') -Raw | ConvertFrom-Json
    if (-not $summary.cleanupConfirmed -or $summary.frames -lt 5 -or
        ($mode -eq 'fixture' -and -not $summary.fixturePassed)) { throw "Incomplete verification: $output" }
    [pscustomobject]@{ Mode = $mode; Passed = $true; Frames = $summary.frames; Output = $output }
}
