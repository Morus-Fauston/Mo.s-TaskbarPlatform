param(
    [ValidateSet('observe', 'baseline', 'fixture')][string]$Mode = 'observe',
    [ValidateRange(1, 600)][int]$Seconds = 120,
    [ValidateRange(10, 100)][int]$IntervalMs = 16,
    [string]$OutputDirectory,
    [switch]$NoBuild
)
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if (-not $OutputDirectory) {
    $runName = '{0}-{1}-{2}' -f $Mode, (Get-Date -Format 'yyyyMMdd-HHmmss'), ([Guid]::NewGuid().ToString('N').Substring(0, 6))
    $OutputDirectory = Join-Path $repoRoot ".scratch/二期开发/verification/05E/$runName"
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) { throw '请使用一个尚不存在的输出目录。' }
if (-not $NoBuild) {
    dotnet build (Join-Path $PSScriptRoot 'TaskbarVisibilityLab.csproj') --configuration Release --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw '实验工具构建失败。' }
}
$executable = Join-Path $PSScriptRoot 'bin/Release/net10.0-windows10.0.26100.0/TaskbarVisibilityLab.exe'
if (-not (Test-Path -LiteralPath $executable)) { throw '请先构建实验工具。' }
if ($OutputDirectory.Contains('"')) { throw '输出路径不能包含引号。' }
$arguments = '--mode {0} --seconds {1} --interval-ms {2} --output "{3}"' -f $Mode, $Seconds, $IntervalMs, $OutputDirectory
$process = Start-Process -FilePath $executable -ArgumentList $arguments -WindowStyle Hidden -PassThru
[pscustomobject]@{ ProcessId = $process.Id; Mode = $Mode; Output = $OutputDirectory; StopFile = (Join-Path $OutputDirectory 'stop.request') }
