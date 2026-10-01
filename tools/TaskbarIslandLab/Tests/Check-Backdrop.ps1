param([Parameter(Mandatory = $true)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $outputPath) { throw 'Fresh output directory required.' }
New-Item -ItemType Directory -Path $outputPath | Out-Null
$results = [Collections.Generic.List[object]]::new()
foreach ($case in @(
    @{Name='owned-acrylic-system-a0'; Mode='owned'; Material='acrylic'; Alpha='0'; Theme='system'; Scenario='verify'},
    @{Name='owned-acrylic-light-a05'; Mode='owned'; Material='acrylic'; Alpha='0.5'; Theme='light'; Scenario='verify'},
    @{Name='owned-acrylic-dark-a1'; Mode='owned'; Material='acrylic'; Alpha='1'; Theme='dark'; Scenario='verify'},
    @{Name='owned-mica-system-a0'; Mode='owned'; Material='mica'; Alpha='0'; Theme='system'; Scenario='verify'},
    @{Name='owned-mica-light-a05'; Mode='owned'; Material='mica'; Alpha='0.5'; Theme='light'; Scenario='verify'},
    @{Name='owned-mica-dark-a1'; Mode='owned'; Material='mica'; Alpha='1'; Theme='dark'; Scenario='verify'},
    @{Name='top-level-acrylic-system-a0'; Mode='top-level'; Material='acrylic'; Alpha='0'; Theme='system'; Scenario='manual'},
    @{Name='owned-none-system-a0'; Mode='owned'; Material='none'; Alpha='0'; Theme='system'; Scenario='verify'}
)) {
    $path = Join-Path $outputPath $case.Name
    & "$PSScriptRoot/../Run.ps1" -Mode $case.Mode -Scenario $case.Scenario -Material $case.Material -Alpha $case.Alpha -Theme $case.Theme -Hz 0 -TimeoutSeconds 10 -OutputDirectory $path
    $events = @(Get-Content -LiteralPath (Join-Path $path 'events.jsonl') -Encoding UTF8 | ConvertFrom-Json)
    $initialized = @($events | Where-Object name -eq 'host-backdrop-initialized')
    $expected = if ($case.Material -ne 'acrylic') { $initialized.Count -eq 0 } else {
        $initialized.Count -gt 0 -and @($initialized | Where-Object { $_.value.hresult -ne '0x00000000' -or -not $_.value.ownedTopLevel -or $_.value.appearance -ne 'pending-human' }).Count -eq 0
    }
    $cleanup = @($events | Where-Object name -eq 'cleanup')
    $policies = @($events | Where-Object name -eq 'acrylic-input-policy')
    $policyValid = if ($case.Material -eq 'acrylic') { $policies.Count -gt 0 -and @($policies | Where-Object { -not $_.value.materialActive -or $_.value.focusChanged }).Count -eq 0 } else { $policies.Count -eq 0 }
    $material = @($events | Where-Object name -eq 'material-api')
    $materialValid = $material.Count -gt 0 -and @($material | Where-Object { $_.value.result -ne 'assigned' }).Count -eq 0
    if ($case.Material -eq 'acrylic') { $materialValid = $materialValid -and @($events | Where-Object name -eq 'acrylic-controller-state').Count -gt 0 }
    $clean = $cleanup.Count -gt 0 -and @($cleanup | Where-Object { $_.value.state -ne 'Closed' -or $_.value.errors.Count -gt 0 -or $_.value.hostAlive -or $_.value.bridgeAlive -or $_.value.ownedParentAlive }).Count -eq 0
    $summary = Get-Content -LiteralPath (Join-Path $path 'summary.json') -Encoding UTF8 -Raw | ConvertFrom-Json
    $results.Add([pscustomobject]@{Case=$case.Name; MaterialInitialized=$expected; InputPolicy=$policyValid; MaterialAssigned=$materialValid; Cleanup=$clean; HumanAcceptance=$summary.humanAcceptance; Passed=($expected -and $policyValid -and $materialValid -and $clean -and $summary.exitCode -eq 0 -and $summary.humanAcceptance -eq 'pending')})
}
$results | ConvertTo-Json | Tee-Object -FilePath (Join-Path $outputPath 'backdrop-checks.json')
if (@($results | Where-Object { -not $_.Passed }).Count -gt 0) { exit 1 }
