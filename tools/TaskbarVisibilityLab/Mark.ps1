param(
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [Parameter(Mandatory = $true)][string]$DisplayId,
    [Parameter(Mandatory = $true)][ValidateSet('露出', '收起', '全屏覆盖', '恢复', '无法判断')][string]$Observed,
    [string]$Note = ''
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath (Join-Path $OutputDirectory 'observations.jsonl'))) { throw '找不到该次实验日志。' }
$marker = [ordered]@{
    kind = 'human_observation'; qpc = [Diagnostics.Stopwatch]::GetTimestamp()
    utc = [DateTimeOffset]::UtcNow.ToString('O'); frequency = [Diagnostics.Stopwatch]::Frequency
    display = $DisplayId; observed = $Observed; note = $Note
    timing = '人工标记包含反应时间，仅用于场景关联，不作为端到端延迟。'
}
($marker | ConvertTo-Json -Compress) | Add-Content -LiteralPath (Join-Path $OutputDirectory 'human-marks.jsonl') -Encoding UTF8
