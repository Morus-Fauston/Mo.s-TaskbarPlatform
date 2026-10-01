param([Parameter(Mandatory = $true)][string]$InputDirectory)
$ErrorActionPreference = 'Stop'
$inputPath = [IO.Path]::GetFullPath($InputDirectory)
$rows = foreach ($file in Get-ChildItem -LiteralPath $inputPath -Filter summary.json -Recurse) {
    $run = Get-Content -Encoding UTF8 -LiteralPath $file.FullName -Raw | ConvertFrom-Json
    foreach ($phase in $run.phases) {
        [pscustomobject]@{
            Run = $file.Directory.Name
            Mode = $run.configuration.Mode
            Hz = $run.configuration.Hz
            Material = $run.configuration.Material
            Alpha = $run.configuration.Alpha
            Diagnostics = $run.configuration.Diagnostics
            ExitCode = $run.exitCode
            Phase = $phase.phase
            Seconds = $phase.metrics.durationSeconds
            Requested = $phase.metrics.requestedUpdates
            Actual = $phase.metrics.actualContentUpdates
            SingleCoreCpuPercent = $phase.metrics.cpu.SingleCorePercent
            MachineCpuPercent = $phase.metrics.cpu.MachinePercent
            AverageProcessCpuMsPerUpdate = $phase.metrics.cpu.MillisecondsPerUpdate
            PrivateBytesStart = $phase.metrics.first.PrivateBytes
            PrivateBytesEnd = $phase.metrics.last.PrivateBytes
            WorkingSetBytesEnd = $phase.metrics.last.WorkingSetBytes
            ThreadDelta = $phase.metrics.threadDelta
            HandleDelta = $phase.metrics.handleDelta
            Gpu = 'unmeasured'
            Wakeups = 'unmeasured'
            PresentationFps = 'unknown'
        }
    }
}
$rows | Export-Csv -LiteralPath (Join-Path $inputPath 'phases.csv') -NoTypeInformation -Encoding UTF8
$rows | Where-Object Phase -eq 'visible' | Format-Table Run,Seconds,Requested,Actual,SingleCoreCpuPercent,HandleDelta -AutoSize
