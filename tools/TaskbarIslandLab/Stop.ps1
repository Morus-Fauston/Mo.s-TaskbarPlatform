param([Parameter(Mandatory = $true)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
if (-not (Test-Path -LiteralPath (Join-Path $outputPath 'launcher.json'))) { throw 'This is not a launcher evidence directory.' }
[IO.File]::WriteAllText((Join-Path $outputPath 'stop.request'), 'human-stop')
Write-Output 'Stop requested. Check result, cleanup, summary.json and launcher.json for confirmation.'
