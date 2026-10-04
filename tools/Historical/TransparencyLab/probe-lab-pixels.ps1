# 只读 A/B 取证：判断 Lab 窗口的透明色刷是否真的半透明。
# 关键点：
#  * 调用 SetProcessDpiAwarenessContext(PerMonitorV2) 让本探针的 GetWindowRect / SetWindowPos
#    与屏幕物理像素一致，避免 150% 缩放下的坐标错位；
#  * 把 Lab 移到主屏固定位置并置顶，再用 hit-test 校验采样点确实落在 Lab 上；
#  * 采样“窗口四角内边距”的纯背景区域（避开按钮条、文字、滚动条）；
#  * 比较“窗口在 / 窗口移开 900px”两种情况的像素颜色：
#      颜色不同 -> 窗口半透明；完全相同 -> alpha 被丢弃。
# 不修改系统状态、不写注册表；测完恢复窗口原位。
param([string]$ProcessName = 'TransparencyLab', [switch]$KeepPosition)

Add-Type -Namespace Dpi -Name X -MemberDefinition @'
[DllImport("user32.dll", SetLastError = true)] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
'@
$aware = [Dpi.X]::SetProcessDpiAwarenessContext([IntPtr](-4))
Write-Host "SetProcessDpiAwarenessContext(PerMonitorV2) = $aware"

Add-Type -AssemblyName System.Drawing
Add-Type -Namespace Px -Name W -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int cx, int cy, uint f);
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc p, IntPtr l);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
[DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, System.Text.StringBuilder s, int n);
[DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
[DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint f);
public delegate bool EnumProc(IntPtr h, IntPtr l);
[StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
[StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
'@

function Get-RootWindowAt([int]$x, [int]$y) {
	$p = New-Object Px.W+POINT
	$p.X = $x; $p.Y = $y
	$h = [Px.W]::WindowFromPoint($p)
	if ($h -eq [IntPtr]::Zero) { return [IntPtr]::Zero }
	return [Px.W]::GetAncestor($h, 2)
}

$proc = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue
if (-not $proc) { Write-Host "进程 $ProcessName 未运行。"; return }
$targetPid = $proc.Id

$found = [IntPtr]::Zero
[void][Px.W]::EnumWindows({
		param($h, $l)
		$p = 0
		[void][Px.W]::GetWindowThreadProcessId($h, [ref]$p)
		if ($p -eq $targetPid) {
			$cls = New-Object Text.StringBuilder 256
			[void][Px.W]::GetClassNameW($h, $cls, 256)
			if ($cls.ToString() -eq 'WinUIDesktopWin32WindowClass') { $script:found = $h; return $false }
		}
		return $true
	}, [IntPtr]::Zero)
if ($found -eq [IntPtr]::Zero) { Write-Host '未找到 Lab 主窗口。'; return }

$r = New-Object Px.W+RECT
[void][Px.W]::GetWindowRect($found, [ref]$r)
$origX = $r.L; $origY = $r.T
$w = $r.R - $r.L; $hgt = $r.B - $r.T
Write-Host ("窗口矩形(物理)={0},{1} {2}x{3}  dpi={4}" -f $origX, $origY, $w, $hgt, [Px.W]::GetDpiForWindow($found))

function Sample-Screen([int[]]$xs, [int[]]$ys) {
	$bmp = New-Object Drawing.Bitmap 4000, 2200
	$g = [Drawing.Graphics]::FromImage($bmp)
	$g.CopyFromScreen(0, 0, 0, 0, (New-Object Drawing.Size 4000, 2200))
	$result = @()
	for ($i = 0; $i -lt $xs.Count; $i++) {
		$c = $bmp.GetPixel($xs[$i], $ys[$i])
		$result += ('#{0:X2}{1:X2}{2:X2}' -f $c.R, $c.G, $c.B)
	}
	$g.Dispose(); $bmp.Dispose()
	return $result
}

$testX = 60; $testY = 60
[void][Px.W]::SetWindowPos($found, [IntPtr](-1), $testX, $testY, 0, 0, 0x0015)
Start-Sleep -Milliseconds 900

# 采样点：窗口内边距纯背景区（左上 14px、底部 12px 内），并全部做 hit-test 校验
$xs = @(
	($testX + 14),
	($testX + 14),
	($testX + [int]($w / 2)),
	($testX + $w - 14),
	($testX + $w - 14)
)
$ys = @(
	($testY + $hgt - 12),
	($testY + [int]($hgt * 0.62)),
	($testY + $hgt - 12),
	($testY + [int]($hgt * 0.62)),
	($testY + $hgt - 12)
)

$owned = 0
for ($i = 0; $i -lt $xs.Count; $i++) {
	if ((Get-RootWindowAt $xs[$i] $ys[$i]) -eq $found) { $owned++ }
	else { Write-Host ("  采样点 {0} 不属于 Lab（命中 0x{1:X}）" -f $i, (Get-RootWindowAt $xs[$i] $ys[$i]).ToInt64()) }
}
Write-Host ("采样点命中 Lab 的个数 = {0} / {1}" -f $owned, $xs.Count)

$before = Sample-Screen $xs $ys
Write-Host "`n[窗口在]   " ($before -join '  ')

[void][Px.W]::SetWindowPos($found, [IntPtr](-2), $testX, $testY + 900, 0, 0, 0x0015)
Start-Sleep -Milliseconds 900
$after = Sample-Screen $xs $ys
Write-Host "[窗口移开] " ($after -join '  ')

if (-not $KeepPosition) {
	[void][Px.W]::SetWindowPos($found, [IntPtr](-2), $origX, $origY, 0, 0, 0x0015)
	Start-Sleep -Milliseconds 400
	Write-Host ("`n窗口已恢复到 {0},{1}" -f $origX, $origY)
}

Write-Host "`n判定："
$same = 0
for ($i = 0; $i -lt $xs.Count; $i++) {
	if ($before[$i] -eq $after[$i]) { $verdict = '不透明（alpha 丢失）'; $same++ }
	else { $verdict = '半透明（透出桌面）' }
	Write-Host ("  ({0},{1}) 窗口在={2} 移开={3} -> {4}" -f $xs[$i], $ys[$i], $before[$i], $after[$i], $verdict)
}
Write-Host "`n不透明点数 = $same / $($xs.Count)"
