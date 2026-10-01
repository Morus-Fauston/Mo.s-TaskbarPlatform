param(
    [string]$PairedDirectory = '.scratch/二期开发/verification/05F/current-paired-run',
    [Parameter(Mandatory = $true)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $outputPath) { throw 'Fresh output directory required.' }
New-Item -ItemType Directory -Path $outputPath | Out-Null
$checks = [Collections.Generic.List[object]]::new()
$rows = [Collections.Generic.List[object]]::new()
function Check([string]$Name, [bool]$Passed) {
    $checks.Add([pscustomobject]@{ name = $Name; passed = $Passed })
}
$expected = @('empty-30','top-level-30','owned-30','empty-0','top-level-0','owned-0','empty-1','top-level-1','owned-1','empty-60','top-level-60','owned-60','diagnostics-False','diagnostics-True')
$actual = @(Get-ChildItem -LiteralPath $PairedDirectory -Directory -Force | Select-Object -ExpandProperty Name)
Check 'exactly 14 expected independent measurement runs' (@(Compare-Object $expected $actual).Count -eq 0)
$longTrends = [Collections.Generic.List[object]]::new()
foreach ($name in $expected) {
    $dir = Join-Path $PairedDirectory $name
    $s = Get-Content -Encoding UTF8 -Raw -LiteralPath (Join-Path $dir 'summary.json') | ConvertFrom-Json
    $l = Get-Content -Encoding UTF8 -Raw -LiteralPath (Join-Path $dir 'launcher.json') | ConvertFrom-Json
    $events = @(Get-Content -Encoding UTF8 -LiteralPath (Join-Path $dir 'events.jsonl') | ConvertFrom-Json)
    $envInfo = ($events | Where-Object name -eq 'environment' | Select-Object -First 1).value
    Check "$name completed normally without watchdog termination" ($s.exitCode -eq 0 -and $s.reason -eq 'measurement-complete' -and $l.exitCode -eq 0 -and -not $l.forcedTermination)
    Check "$name preserves human acceptance as pending" ($s.humanAcceptance -eq 'pending')
    Check "$name same material alpha theme and no Explorer target" ($s.configuration.Material -eq 'none' -and $s.configuration.Alpha -eq 0.5 -and $s.configuration.Theme -eq 'system' -and $s.configuration.ParentHwnd -eq 0 -and $null -eq $s.configuration.Monitor)
    Check "$name all five phases in order" (($s.phases.phase -join ',') -eq 'warmup,idle,visible,hidden-data-updating,restored')
    Check "$name no failed assertions or runtime faults" (@($events | Where-Object { ($_.name -eq 'assertion' -and -not $_.value.passed) -or $_.name -in @('fatal','unhandled','start-failed','tick-failed','update-failed','cleanup-failed') }).Count -eq 0)
    $cleanup = @($events | Where-Object name -eq 'cleanup')
    Check "$name cleanup has no surviving owned windows" ($cleanup.Count -gt 0 -and @($cleanup | Where-Object { $_.value.state -ne 'Closed' -or $_.value.errors.Count -gt 0 -or $_.value.hostAlive -or $_.value.bridgeAlive -or $_.value.ownedParentAlive }).Count -eq 0)
    if ($s.configuration.Mode -ne 'empty') {
        $initialized = ($events | Where-Object name -eq 'initialized' | Select-Object -First 1).value
        Check "$name real loaded same content size assertions" (@($events | Where-Object { $_.name -eq 'assertion' -and $_.value.passed }).Count -eq 2)
        Check "$name matched DPI and pixel layout" ($initialized.dpi -eq 144 -and $initialized.dip.width -eq 260 -and $initialized.dip.height -eq 56 -and $initialized.pixels.width -eq 390 -and $initialized.pixels.height -eq 84)
    }
    foreach ($p in $s.phases) {
        $m = $p.metrics
        $minimum = switch ($p.phase) { 'warmup' {5} 'idle' {10} 'visible' {$s.configuration.VisibleSeconds} 'hidden-data-updating' {$s.configuration.HiddenSeconds} 'restored' {10} }
        Check "$name / $($p.phase) minimum duration $minimum seconds" ($m.durationSeconds -ge $minimum)
        $core = ($m.last.CpuSeconds - $m.first.CpuSeconds) / ($m.last.WallSeconds - $m.first.WallSeconds) * 100
        Check "$name / $($p.phase) CPU formula independently recomputed" ([Math]::Abs($core - $m.cpu.SingleCorePercent) -lt 0.000001 -and [Math]::Abs($core / $envInfo.processors - $m.cpu.MachinePercent) -lt 0.000001)
        Check "$name / $($p.phase) update counters consistent" ($m.requestedUpdates -eq $m.last.Requested - $m.first.Requested -and $m.actualContentUpdates -eq $m.last.Actual - $m.first.Actual -and $m.actualContentUpdates -ge 0 -and $m.actualContentUpdates -le $m.requestedUpdates)
        if ($s.configuration.Mode -eq 'empty' -or $s.configuration.Hz -eq 0 -or $p.phase -eq 'idle') {
            Check "$name / $($p.phase) no content updates in empty static or idle condition" ($m.actualContentUpdates -eq 0)
        } else {
            Check "$name / $($p.phase) actually executed content updates" ($m.actualContentUpdates -gt 0 -and $m.requestedUpdates -gt 0)
        }
        $rows.Add([pscustomobject]@{Run=$name; Phase=$p.phase; Seconds=$m.durationSeconds; Requested=$m.requestedUpdates; Actual=$m.actualContentUpdates; ActualUpdatesPerSecond=$m.actualContentUpdates/$m.durationSeconds; SingleCoreCpuPercent=$core; MachineCpuPercent=$core/$envInfo.processors; AverageProcessCpuMsPerUpdate=$m.cpu.MillisecondsPerUpdate; PrivateMiBStart=$m.first.PrivateBytes/1MB; PrivateMiBEnd=$m.last.PrivateBytes/1MB; WorkingSetMiBStart=$m.first.WorkingSetBytes/1MB; WorkingSetMiBEnd=$m.last.WorkingSetBytes/1MB; ThreadDelta=$m.threadDelta; HandleDelta=$m.handleDelta})
    }
    Check "$name summary counters equal phase sums" ($s.requestedUpdates -eq ($s.phases.metrics.requestedUpdates | Measure-Object -Sum).Sum -and $s.actualContentUpdates -eq ($s.phases.metrics.actualContentUpdates | Measure-Object -Sum).Sum)
    Check "$name unmeasured metrics explicitly reported" ($s.gpu -like 'unmeasured:*' -and $s.wakeups -like 'unmeasured:*' -and $s.presentationFps -eq 'unknown')
    if ($name -in @('top-level-30','owned-30')) {
        $visible = ($s.phases | Where-Object phase -eq 'visible').metrics
        Check "$name visible dynamic stage at least 600 seconds" ($visible.durationSeconds -ge 600)
        $samples = @($events | Where-Object { $_.name -eq 'sample' -and $_.value.phase -eq 'visible' } | ForEach-Object {$_.value.snapshot})
        for ($minute=0; $minute -lt 10; $minute++) {
            $slice = @($samples | Where-Object { $elapsed=$_.WallSeconds-$visible.first.WallSeconds; $elapsed -ge $minute*60 -and $elapsed -lt ($minute+1)*60 })
            if ($slice.Count -gt 0) {
                $longTrends.Add([pscustomobject]@{Run=$name; Minute=$minute+1; Samples=$slice.Count; PrivateMiBMin=($slice.PrivateBytes|Measure-Object -Minimum).Minimum/1MB; PrivateMiBMax=($slice.PrivateBytes|Measure-Object -Maximum).Maximum/1MB; PrivateMiBMean=($slice.PrivateBytes|Measure-Object -Average).Average/1MB; WorkingSetMiBMean=($slice.WorkingSetBytes|Measure-Object -Average).Average/1MB; ThreadsMin=($slice.Threads|Measure-Object -Minimum).Minimum; ThreadsMax=($slice.Threads|Measure-Object -Maximum).Maximum; HandlesMin=($slice.Handles|Measure-Object -Minimum).Minimum; HandlesMax=($slice.Handles|Measure-Object -Maximum).Maximum})
            }
        }
    }
}
$checks | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $outputPath 'paired-validation-checks.json') -Encoding UTF8
$rows | Export-Csv -LiteralPath (Join-Path $outputPath 'paired-independent-metrics.csv') -NoTypeInformation -Encoding UTF8
$longTrends | Export-Csv -LiteralPath (Join-Path $outputPath 'long-run-resource-trends.csv') -NoTypeInformation -Encoding UTF8
$failed = @($checks | Where-Object { -not $_.passed })
[pscustomobject]@{Checks=$checks.Count; Passed=$checks.Count-$failed.Count; Failed=$failed.Count; Runs=$expected.Count; Phases=$rows.Count} | ConvertTo-Json | Tee-Object -FilePath (Join-Path $outputPath 'paired-validation-summary.json')
if ($failed.Count -gt 0) { $failed | Format-Table -AutoSize; exit 1 }
