param([string]$OutputDirectory = "$PSScriptRoot/bin/window-regression")
$ErrorActionPreference = 'Stop'
$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
dotnet build "$PSScriptRoot/Mtp.Host.WindowTests.csproj" --configuration Release "-p:OutDir=$outputPath/"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$run = Start-Process -FilePath "$outputPath/Mtp.Host.WindowTests.exe" -WindowStyle Hidden -PassThru
if (-not $run.WaitForExit(20000)) {
    $run.Kill()
    $run.WaitForExit()
    throw 'WinUI regression timed out; only the test process was terminated.'
}
Get-Content -LiteralPath "$outputPath/window-tests.log"
exit $run.ExitCode
