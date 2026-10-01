param(
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [ValidateRange(600,3600)][int]$LongSeconds = 600,
    [ValidateRange(5,120)][int]$ShortSeconds = 20,
    [ValidateRange(2,120)][int]$HiddenSeconds = 20,
    [ValidateSet('none','acrylic','mica')][string]$Material = 'none',
    [ValidateSet('0','0.5','1')][string]$Alpha = '0.5'
)
$ErrorActionPreference = 'Stop'
$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $outputPath) { throw 'Use a new directory for the paired evidence set.' }
New-Item -ItemType Directory -Path $outputPath | Out-Null
$exe = Join-Path $PSScriptRoot 'bin\Release\net10.0-windows10.0.26100.0\TaskbarIslandLab.exe'
$dll = Join-Path $PSScriptRoot 'bin\Release\net10.0-windows10.0.26100.0\TaskbarIslandLab.dll'
$binaryHash = (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash
$environment = [ordered]@{
    startedUtc = [DateTime]::UtcNow.ToString('o')
    assemblySha256 = $binaryHash
    os = Get-CimInstance Win32_OperatingSystem | Select-Object Caption,Version,BuildNumber
    gpu = Get-CimInstance Win32_VideoController | Select-Object Name,DriverVersion,CurrentHorizontalResolution,CurrentVerticalResolution,CurrentRefreshRate
    dotnet = (dotnet --info | Out-String)
    commit = (git rev-parse HEAD | Out-String).Trim()
    branch = (git branch --show-current | Out-String).Trim()
    tree = (git status --short | Out-String)
    note = 'Serial processes; desktop remains user-controlled. GPU inventory is not GPU utilization. HDR/monitor identity/auto-hide need human recording.'
}
$environment | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $outputPath 'environment.json') -Encoding UTF8
foreach ($hz in @(30,0,1,60)) {
    foreach ($mode in @('empty','top-level','owned')) {
        if ((Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash -ne $binaryHash) { throw 'Binary changed during the paired measurement.' }
        $seconds = if ($hz -eq 30 -and $mode -ne 'empty') { $LongSeconds } else { $ShortSeconds }
        & "$PSScriptRoot/Run.ps1" -Mode $mode -Scenario measure -Hz $hz -VisibleSeconds $seconds -HiddenSeconds $HiddenSeconds -Material $Material -Alpha $Alpha -OutputDirectory (Join-Path $outputPath "$mode-$hz")
    }
}
# Quantify the optional per-update JSON diagnostics using a separate short pair.
foreach ($diagnostics in @($false,$true)) {
    & "$PSScriptRoot/Run.ps1" -Mode owned -Scenario measure -Hz 30 -VisibleSeconds $ShortSeconds -HiddenSeconds $HiddenSeconds -Material $Material -Alpha $Alpha -Diagnostics:$diagnostics -OutputDirectory (Join-Path $outputPath "diagnostics-$diagnostics")
}
& "$PSScriptRoot/Summarize.ps1" -InputDirectory $outputPath
