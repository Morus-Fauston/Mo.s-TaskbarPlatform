param([Parameter(Mandatory = $true)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $outputPath) { throw 'Fresh output directory required.' }
New-Item -ItemType Directory -Path $outputPath | Out-Null
function Start-LabProcess([string]$ArgumentText, [bool]$CaptureError = $false) {
    # Process.Start retains the native process handle even when the child exits immediately.
    $info = [Diagnostics.ProcessStartInfo]::new($exe, $ArgumentText)
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $info.RedirectStandardError = $CaptureError
    [Diagnostics.Process]::Start($info)
}
$records = [Collections.Generic.List[object]]::new()
$exe = [IO.Path]::GetFullPath('tools/TaskbarIslandLab/bin/Release/net10.0-windows10.0.26100.0/TaskbarIslandLab.exe')
$invalid = @(
    @{Name='implicit-explorer'; Args=@('--mode','explorer')},
    @{Name='owned-parent-argument'; Args=@('--mode','owned','--parent-hwnd','123')},
    @{Name='invalid-alpha'; Args=@('--alpha','NaN')},
    @{Name='unknown-option'; Args=@('--unexpected','value')}
)
foreach ($case in $invalid) {
    $dir = Join-Path $outputPath $case.Name
    if(Test-Path -LiteralPath $dir){throw 'Fresh directory required'}
    New-Item -ItemType Directory -Path $dir | Out-Null
    $arguments = @($case.Args) + @('--output', $dir)
    $text = ($arguments | ForEach-Object {'"' + $_ + '"'}) -join ' '
    $proc = Start-LabProcess $text $true
    try {
        if(-not $proc.WaitForExit(15000)){ $proc.Kill(); $proc.WaitForExit(); throw 'Invalid argument process did not exit within 15 seconds' }
        $proc.WaitForExit()
        $proc.StandardError.ReadToEnd() | Set-Content -LiteralPath (Join-Path $dir 'stderr.txt') -Encoding UTF8
        $row = [pscustomobject]@{Test=$case.Name; ExitCode=$proc.ExitCode; NoApplicationEvents=(-not (Test-Path (Join-Path $dir 'events.jsonl'))); Passed=($proc.ExitCode -eq 2 -and -not (Test-Path (Join-Path $dir 'events.jsonl')))}
        $records.Add($row)
    } finally { $proc.Dispose() }
}
$dir = Join-Path $outputPath 'stop-check'
if(Test-Path -LiteralPath $dir){throw 'Fresh directory required'}
New-Item -ItemType Directory -Path $dir | Out-Null
$argsList=@('--mode','owned','--scenario','measure','--hz','30','--visible-seconds','600','--output',$dir)
$argText=($argsList | ForEach-Object {'"'+$_+'"'}) -join ' '
$proc=Start-LabProcess $argText
$launcher=[ordered]@{processId=$proc.Id; executable=$exe; args=$argsList; startedUtc=[DateTime]::UtcNow.ToString('o'); forcedTermination=$false}
$launcher | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $dir 'launcher.json') -Encoding UTF8
try {
    $watch=[Diagnostics.Stopwatch]::StartNew()
    $initialized=$false
    while($watch.Elapsed.TotalSeconds -lt 15 -and -not $proc.HasExited) {
        $eventPath=Join-Path $dir 'events.jsonl'
        if(Test-Path $eventPath) { $initialized=@(Get-Content -Encoding UTF8 $eventPath | ConvertFrom-Json | Where-Object name -eq 'initialized').Count -gt 0 }
        if($initialized){break}
        Start-Sleep -Milliseconds 200
    }
    if(-not $initialized){throw 'Owned stop fixture did not initialize'}
    & powershell.exe -NoProfile -File tools/TaskbarIslandLab/Stop.ps1 -OutputDirectory $dir
    if($LASTEXITCODE -ne 0){throw 'Stop.ps1 failed'}
    if(-not $proc.WaitForExit(15000)){$proc.Kill();$proc.WaitForExit();$launcher.forcedTermination=$true;throw 'Stop fixture did not exit'}
    $proc.WaitForExit()
    $s=Get-Content -Encoding UTF8 -Raw (Join-Path $dir 'summary.json') | ConvertFrom-Json
    $events=@(Get-Content -Encoding UTF8 (Join-Path $dir 'events.jsonl') | ConvertFrom-Json)
    $cleanup=@($events | Where-Object name -eq 'cleanup')
    $badCleanup=@($cleanup | Where-Object {$_.value.state -ne 'Closed' -or $_.value.errors.Count -gt 0 -or $_.value.hostAlive -or $_.value.bridgeAlive -or $_.value.ownedParentAlive})
    $records.Add([pscustomobject]@{Test='Stop.ps1 owned process'; ExitCode=$proc.ExitCode; Reason=$s.reason; Cleaned=($cleanup.Count -gt 0 -and $badCleanup.Count -eq 0); Passed=($proc.ExitCode -eq 0 -and $s.exitCode -eq 0 -and $s.reason -eq 'stop-file' -and $cleanup.Count -gt 0 -and $badCleanup.Count -eq 0)})
} finally {
    if(-not $proc.HasExited){$proc.Kill();$proc.WaitForExit();$launcher.forcedTermination=$true}
    $launcher.exitCode=$proc.ExitCode
    $launcher.finishedUtc=[DateTime]::UtcNow.ToString('o')
    $launcher | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $dir 'launcher.json') -Encoding UTF8
    $proc.Dispose()
}
foreach ($case in @(
    @{Name='launcher-invalid-parent'; Arguments=@{Mode='owned'; ParentHwnd='123'}; ExitCode=2},
    @{Name='launcher-owned-verify'; Arguments=@{Mode='owned'; Scenario='verify'}; ExitCode=0}
)) {
    $dir = Join-Path $outputPath $case.Name
    $arguments = $case.Arguments
    $threw = $false
    try { & "$PSScriptRoot/../Run.ps1" @arguments -OutputDirectory $dir }
    catch { $threw = $true; $_ | Out-String | Set-Content -LiteralPath (Join-Path $dir 'launcher-error.txt') -Encoding UTF8 }
    $launch = Get-Content -Encoding UTF8 -Raw -LiteralPath (Join-Path $dir 'launcher.json') | ConvertFrom-Json
    $passed = $launch.exitCode -eq $case.ExitCode -and -not $launch.forcedTermination -and $threw -eq ($case.ExitCode -ne 0)
    if ($case.ExitCode -eq 0) {
        $summary = Get-Content -Encoding UTF8 -Raw -LiteralPath (Join-Path $dir 'summary.json') | ConvertFrom-Json
        $passed = $passed -and $summary.reason -eq 'owned-integration-passed' -and $summary.exitCode -eq 0
    } else {
        $passed = $passed -and -not (Test-Path -LiteralPath (Join-Path $dir 'events.jsonl'))
    }
    $records.Add([pscustomobject]@{Test=$case.Name; ExitCode=$launch.exitCode; Threw=$threw; Passed=$passed})
}
$records | ConvertTo-Json -Depth 5 | Tee-Object -FilePath (Join-Path $outputPath 'process-boundaries.json')
if(@($records | Where-Object {-not $_.Passed}).Count -gt 0){exit 1}
