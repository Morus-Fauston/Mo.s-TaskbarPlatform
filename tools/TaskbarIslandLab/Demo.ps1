param(
    [ValidateSet('menu','owned','top-level')][string]$Mode = 'menu',
    [ValidateSet('none','acrylic','mica')][string]$Material = 'none',
    [ValidateSet('0','0.5','1')][string]$Alpha = '0.5',
    [ValidateSet(0,1,30,60)][int]$Hz = 0,
    [ValidateRange(5,7200)][int]$TimeoutSeconds = 1800,
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
Set-Location -LiteralPath $repo
$exe = Join-Path $PSScriptRoot 'bin/Release/net10.0-windows10.0.26100.0/TaskbarIslandLab.exe'
if (-not (Test-Path -LiteralPath $exe)) {
    throw '请先执行：dotnet build tools/TaskbarIslandLab/TaskbarIslandLab.csproj --configuration Release'
}
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $repo ('.scratch/二期开发/evidence/05F/manual-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
}
$sessionPath = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $sessionPath) { throw '输出目录已存在；请换一个新目录。' }
New-Item -ItemType Directory -Path $sessionPath | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'observations.template.md') -Destination (Join-Path $sessionPath '人工观察记录.md')
$identity = [ordered]@{
    createdUtc = [DateTime]::UtcNow.ToString('o')
    branch = (git branch --show-current | Out-String).Trim()
    commit = (git rev-parse HEAD | Out-String).Trim()
    powerShell = $PSVersionTable.PSVersion.ToString()
    os = [Environment]::OSVersion.VersionString
    processors = [Environment]::ProcessorCount
    binarySha256 = (Get-FileHash -LiteralPath (Join-Path (Split-Path $exe) 'TaskbarIslandLab.dll') -Algorithm SHA256).Hash
    humanAcceptance = 'pending'
}
$identity | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $sessionPath 'session.json') -Encoding UTF8
$script:caseNumber = 0
$shellName = if ($PSVersionTable.PSEdition -eq 'Core') { 'pwsh.exe' } else { 'powershell.exe' }
$shellExe = Join-Path $PSHOME $shellName

function New-CasePath([string]$Label) {
    $script:caseNumber++
    Join-Path $sessionPath ('{0:D2}-{1}' -f $script:caseNumber, $Label)
}
function Quote-Argument([string]$Value) {
    '"' + [regex]::Replace([regex]::Replace($Value, '(\\*)"', '$1$1\"'), '(\\+)$', '$1$1') + '"'
}
function Select-Material {
    Write-Host '选择配置：1=none/alpha1；2=none/alpha0.5；3=none/alpha0；4=Acrylic/alpha0.5；5=Mica/alpha0.5'
    switch (Read-Host '配置编号') {
        '1' { return @{Material='none'; Alpha='1'} }
        '2' { return @{Material='none'; Alpha='0.5'} }
        '3' { return @{Material='none'; Alpha='0'} }
        '4' { return @{Material='acrylic'; Alpha='0.5'} }
        '5' { return @{Material='mica'; Alpha='0.5'} }
        default { throw '未选择有效配置；没有启动实例。' }
    }
}
function Select-ExplorerTarget {
    $path = New-CasePath 'targets'
    & "$PSScriptRoot/Run.ps1" -Mode explorer -Scenario list-targets -OutputDirectory $path | Out-Host
    $targets = @(Get-Content -Encoding UTF8 -Raw -LiteralPath (Join-Path $path 'targets.json') | ConvertFrom-Json)
    if ($targets.Count -eq 0) { throw '没有发现任务栏候选。保留证据，停止此项。' }
    for ($i=0; $i -lt $targets.Count; $i++) {
        $t = $targets[$i]
        Write-Host ('[{0}] 屏幕 {1}  HWND {2}  类名 {3}' -f ($i+1), $t.monitor, $t.window.hwnd, $t.window.windowClass)
    }
    $choice = 0
    if (-not [int]::TryParse((Read-Host '由你选择一个底部任务栏编号；直接回车取消'), [ref]$choice) -or $choice -lt 1 -or $choice -gt $targets.Count) {
        throw '未明确选择目标；没有绑定 Explorer。'
    }
    $x = 80
    $inputX = Read-Host '父客户区横坐标 X（像素；回车=80；选择不遮挡常用任务栏按钮的位置）'
    if ($inputX -and (-not [int]::TryParse($inputX, [ref]$x) -or $x -lt 0 -or $x -gt 32000)) { throw 'X 应为 0..32000 的整数。' }
    $target = $targets[$choice-1]
    @{Monitor=$target.monitor; ParentHwnd=$target.window.hwnd; X=$x}
}
function Run-DemoCase([string]$CaseMode, [hashtable]$Config, [hashtable]$Target = @{}, [bool]$Measure = $false) {
    $path = New-CasePath ($CaseMode + '-' + $Config.Material + '-' + $Config.Alpha + $(if ($Measure) {'-measure'} else {''}))
    New-Item -ItemType Directory -Path $path | Out-Null
    $scenario = if ($Measure) { 'measure' } else { 'manual' }
    $frequency = if ($Measure) { 30 } else { $Hz }
    $arguments = @('-NoProfile','-File',(Join-Path $PSScriptRoot 'Run.ps1'),'-Mode',$CaseMode,'-Scenario',$scenario,
        '-Material',$Config.Material,'-Alpha',$Config.Alpha,'-Hz',"$frequency",'-TimeoutSeconds',"$TimeoutSeconds",'-OutputDirectory',$path)
    if ($Measure) { $arguments += @('-VisibleSeconds','600','-HiddenSeconds','20') }
    if ($CaseMode -eq 'explorer') { $arguments += @('-Monitor',$Target.Monitor,'-ParentHwnd',$Target.ParentHwnd,'-X',"$($Target.X)") }
    Write-Host ''
    Write-Host ('将启动：{0} / {1} / alpha {2} / {3} Hz' -f $CaseMode,$Config.Material,$Config.Alpha,$frequency)
    Write-Host ('本次证据：' + $path)
    Write-Host '3 秒后显示。现在切换到记事本或背景窗口，可观察是否抢焦点。'
    Start-Sleep -Seconds 3
    $start = [Diagnostics.ProcessStartInfo]::new($shellExe, (($arguments | ForEach-Object { Quote-Argument $_ }) -join ' '))
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $runner = [Diagnostics.Process]::Start($start)
    $stdout = $runner.StandardOutput.ReadToEndAsync()
    $stderr = $runner.StandardError.ReadToEndAsync()
    $stopRequested = $false
    $lastPhase = ''
    $keyboardAvailable = -not [Console]::IsInputRedirected
    Write-Host '操作真实小窗口。停止：回到本终端按 Q，或右键小窗口的 +1 → 关闭实验。'
    if ($Measure) { Write-Host '按 H 记录你刚完成隐藏；按 R 记录你刚完成恢复。按键只记录时间，不操控任务栏。' }
    try {
        while (-not $runner.WaitForExit(500)) {
            $keyReady = $false
            if ($keyboardAvailable) {
                try { $keyReady = [Console]::KeyAvailable }
                catch {
                    $keyboardAvailable = $false
                    Write-Host '当前终端不支持单键读取；请右键关闭实验，或在另一终端使用 Stop.ps1。'
                }
            }
            if ($keyReady) {
                $key = [Console]::ReadKey($true).Key
                if ($key -eq [ConsoleKey]::Q -and (Test-Path -LiteralPath $path)) {
                    [IO.File]::WriteAllText((Join-Path $path 'stop.request'), 'human-demo-stop')
                    $stopRequested = $true
                    Write-Host '已请求正常停止，等待清理。'
                }
                if ($Measure -and $key -in @([ConsoleKey]::H,[ConsoleKey]::R)) {
                    [pscustomobject]@{utc=[DateTime]::UtcNow.ToString('o'); reportedAction=$key.ToString(); note='Human-reported marker; not measured visibility.'} | ConvertTo-Json -Compress | Add-Content -LiteralPath (Join-Path $path 'human-markers.jsonl') -Encoding UTF8
                    Write-Host ('已记录人工时点：' + $key)
                }
            }
            $eventPath = Join-Path $path 'events.jsonl'
            if ($Measure -and (Test-Path -LiteralPath $eventPath)) {
                $phases = @(Get-Content -Encoding UTF8 -LiteralPath $eventPath | Where-Object { $_ -like '*"phase-begin"*' } | ForEach-Object {
                    # A log line can still be in flight; retry on the next poll.
                    try { $_ | ConvertFrom-Json -ErrorAction Stop } catch { }
                })
                if ($phases.Count -gt 0 -and $phases[-1].value.phase -ne $lastPhase) {
                    $lastPhase = $phases[-1].value.phase
                    Write-Host ('阶段：' + $lastPhase)
                    if ($lastPhase -eq 'hidden-data-updating') { Write-Host '现在由你隐藏任务栏；完成后按 H。' }
                    if ($lastPhase -eq 'restored') { Write-Host '现在由你恢复任务栏；完成后按 R。' }
                }
            }
        }
        $runner.WaitForExit()
        $stdout.Result | Set-Content -LiteralPath (Join-Path $path 'demo-launcher-output.txt') -Encoding UTF8
        $stderr.Result | Set-Content -LiteralPath (Join-Path $path 'demo-launcher-error.txt') -Encoding UTF8
        Write-Host ('启动脚本退出码：' + $runner.ExitCode)
        $summaryPath = Join-Path $path 'summary.json'
        if (Test-Path -LiteralPath $summaryPath) {
            $summary = Get-Content -Encoding UTF8 -Raw -LiteralPath $summaryPath | ConvertFrom-Json
            Write-Host ('应用结果：{0}，退出码 {1}' -f $summary.reason,$summary.exitCode)
        } else { Write-Host '没有 summary.json；此项运行不完整，请保留本次目录。' }
        if ($runner.ExitCode -ne 0) { Write-Host '本次未正常完成。若应用退出码为 3，核对是否为本项预期的安全失效；其余查看日志。不能仅凭退出码判人工通过。' }
        Write-Host '运行结束不代表人工通过，请填写观察记录。'
        [pscustomobject]@{utc=[DateTime]::UtcNow.ToString('o'); mode=$CaseMode; material=$Config.Material; alpha=$Config.Alpha; scenario=$scenario; hz=$frequency; output=$path; runnerExitCode=$runner.ExitCode; stopRequested=$stopRequested; humanAcceptance='pending'} | ConvertTo-Json -Compress | Add-Content -LiteralPath (Join-Path $sessionPath 'cases.jsonl') -Encoding UTF8
        if ($Mode -ne 'menu' -and $runner.ExitCode -ne 0) { throw '演示启动失败，查看本次证据。' }
    } finally {
        if (-not $runner.HasExited) {
            if (Test-Path -LiteralPath $path) { [IO.File]::WriteAllText((Join-Path $path 'stop.request'), 'demo-finally-stop') }
            if (-not $runner.WaitForExit(15000)) { Write-Warning '启动器尚未退出。保留终端，按手册用 Stop.ps1 请求停止；外层 watchdog 仍有效。' }
        }
        $runner.Dispose()
    }
}
Write-Host ('本轮总目录：' + $sessionPath)
Write-Host ('观察表：' + (Join-Path $sessionPath '人工观察记录.md'))
if ($Mode -ne 'menu') {
    Run-DemoCase $Mode @{Material=$Material; Alpha=$Alpha}
    return
}
while ($true) {
    Write-Host ''
    Write-Host '05F 真实 WinUI 演示 / 人工验收'
    Write-Host '1 自有窗口 · 不透明对照（alpha 1）'
    Write-Host '2 自有窗口 · 半透明请求（alpha 0.5）'
    Write-Host '3 自有窗口 · 透明请求（alpha 0）'
    Write-Host '4 自有窗口 · Desktop Acrylic'
    Write-Host '5 自有窗口 · Mica'
    Write-Host '6 普通 WinUI 顶级窗口对照（再选配置）'
    Write-Host '7 Explorer 任务栏人工实验（由你选父级和配置）'
    Write-Host '8 Explorer 30 Hz / 10 分钟采样（视觉行为成立后执行）'
    Write-Host 'B 打开动态彩色背景；E 打开本轮证据；M 打开验收手册；0 退出'
    $choice = Read-Host '编号'
    if ($null -eq $choice) { return }
    try {
        switch ($choice.ToUpperInvariant()) {
            '1' { Run-DemoCase 'owned' @{Material='none';Alpha='1'} }
            '2' { Run-DemoCase 'owned' @{Material='none';Alpha='0.5'} }
            '3' { Run-DemoCase 'owned' @{Material='none';Alpha='0'} }
            '4' { Run-DemoCase 'owned' @{Material='acrylic';Alpha='0.5'} }
            '5' { Run-DemoCase 'owned' @{Material='mica';Alpha='0.5'} }
            '6' { $config=Select-Material; Run-DemoCase 'top-level' $config }
            '7' { $config=Select-Material; $target=Select-ExplorerTarget; Run-DemoCase 'explorer' $config $target }
            '8' {
                if ((Read-Host '先完成视觉/行为检查；决定开始时输入 MEASURE') -ne 'MEASURE') { break }
                $target=Select-ExplorerTarget
                Run-DemoCase 'explorer' @{Material='none';Alpha='0.5'} $target $true
            }
            'B' { Start-Process -FilePath (Join-Path $PSScriptRoot 'Backdrop.html') }
            'E' { Start-Process -FilePath $sessionPath }
            'M' { Start-Process -FilePath (Join-Path $PSScriptRoot '人工验收手册.md') }
            '0' { return }
            default { Write-Host '请输入菜单中的编号。' }
        }
    } catch { Write-Host ('本项未完成：' + $_.Exception.Message) -ForegroundColor Yellow }
}
