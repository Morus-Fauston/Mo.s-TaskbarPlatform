param(
    [Parameter(Mandatory = $true)][string]$EvidenceRoot,
    [string]$CapturedBinaryDirectory
)
$ErrorActionPreference = 'Stop'
$reports = @(Get-ChildItem -LiteralPath $EvidenceRoot -Filter report.json -File -Recurse)
if ($reports.Count -eq 0) { throw 'No Host reports found.' }
$incomplete = 0
foreach ($file in $reports) {
    $report = Get-Content -LiteralPath $file.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
    if (@($report.EvidenceErrors).Count -gt 0) { $incomplete++ }
    if (-not $report.Identity.HostDllSha256 -or -not $report.Identity.Baseline) { throw "Missing identity: $($file.FullName)" }
    $dllPath = $report.Identity.HostDll
    $exePath = $report.Identity.HostExe
    if ($CapturedBinaryDirectory) {
        $dllPath = Join-Path $CapturedBinaryDirectory ([IO.Path]::GetFileName($dllPath))
        $exePath = Join-Path $CapturedBinaryDirectory ([IO.Path]::GetFileName($exePath))
    }
    $actualHash = (Get-FileHash -LiteralPath $dllPath -Algorithm SHA256).Hash
    if ($actualHash -ne $report.Identity.HostDllSha256) { throw 'Report does not match the current DLL at its captured path; preserve the original binary before comparing.' }
    if ((Get-FileHash -LiteralPath $exePath -Algorithm SHA256).Hash -ne $report.Identity.HostExeSha256) { throw 'Host executable identity differs.' }
    $samplePath = Join-Path $file.DirectoryName 'samples.jsonl'
    $raw = @()
    if (Test-Path -LiteralPath $samplePath) { $raw = @(Get-Content -LiteralPath $samplePath -Encoding UTF8 | ForEach-Object { $_ | ConvertFrom-Json }) }
    if ($raw.Count -ne @($report.Samples).Count) { throw 'Raw/report sample counts differ.' }
    for ($i = 0; $i -lt $raw.Count; $i++) {
        foreach ($field in @('Seconds', 'CpuSeconds', 'PrivateBytes', 'WorkingSetBytes', 'Handles', 'Threads', 'Updates', 'SamplingEpoch')) {
            if ($raw[$i].$field -ne $report.Samples[$i].$field) { throw "Raw/report mismatch: $field" }
        }
        if ($i -gt 0 -and $raw[$i].Seconds -le $raw[$i - 1].Seconds) { throw 'Non-increasing sampling times.' }
    }
    foreach ($interval in $report.Intervals) {
        if ($interval.First.SamplingEpoch -ne $interval.Last.SamplingEpoch) { throw 'CPU interval crossed a stopped sampling period.' }
        $single = 100.0 * ($interval.Last.CpuSeconds - $interval.First.CpuSeconds) / ($interval.EndSeconds - $interval.StartSeconds)
        if ([Math]::Abs($single - $interval.SingleCorePercent) -gt 0.000001) { throw 'Single-core CPU arithmetic differs.' }
        if ([Math]::Abs($single / $report.ProcessorCount - $interval.MachinePercent) -gt 0.000001) { throw 'Machine CPU arithmetic differs.' }
    }
    foreach ($metric in @('gpu', 'wakeups', 'presentation-fps', 'external-uia', 'external-display-dpi')) {
        if (-not $report.Results.$metric.Note) { throw "Missing evidence reason: $metric" }
    }
    if (-not (Test-Path -LiteralPath (Join-Path $file.DirectoryName 'report.md'))) { throw 'Human-readable report missing.' }
    if ($report.Identity.EvidenceKind -eq 'automated-owned-parent-fixture') {
        foreach ($result in $report.Results.PSObject.Properties) {
            if ($result.Value.Status -eq 'passed') { throw 'Automated fixture must not claim human acceptance.' }
        }
    }
}
"PASS: independently reread $($reports.Count) Host reports, raw samples, CPU arithmetic, binary identity and missing-metric reasons. Declared incomplete evidence: $incomplete."
