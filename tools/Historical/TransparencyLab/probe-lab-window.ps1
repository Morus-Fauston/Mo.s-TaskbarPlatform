# 只读探针：报告 TransparencyLab 窗口的矩形、所属显示器、可见性与 DWM 状态。
# 不修改任何系统状态。用法： powershell -ExecutionPolicy Bypass -File probe-lab-window.ps1
param([string]$ProcessName = 'TransparencyLab')

Add-Type -Namespace Probe -Name Win -MemberDefinition @'
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, System.Text.StringBuilder s, int n);
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc p, IntPtr l);
[DllImport("user32.dll")] public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr r, MonProc p, IntPtr l);
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
[DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr h, uint f);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern bool GetMonitorInfoW(IntPtr m, ref MI i);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
[DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
[DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, uint a, out int v, int s);
public delegate bool EnumProc(IntPtr h, IntPtr l);
public delegate bool MonProc(IntPtr m, IntPtr hdc, IntPtr r, IntPtr l);
[StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct MI { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice; }
'@

$proc = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue
if (-not $proc) { Write-Host "进程 $ProcessName 未运行。"; return }
$targetPid = $proc.Id

Write-Host "=== 显示器 ==="
[void][Probe.Win]::EnumDisplayMonitors([IntPtr]::Zero, [IntPtr]::Zero, {
	param($m, $hdc, $r, $l)
	$mi = New-Object Probe.Win+MI
	$mi.cbSize = [Runtime.InteropServices.Marshal]::SizeOf($mi)
	[void][Probe.Win]::GetMonitorInfoW($m, [ref]$mi)
	"{0} primary={1} 起点={2},{3} 尺寸={4}x{5}" -f $mi.szDevice, [bool]($mi.dwFlags -band 1), $mi.rcMonitor.L, $mi.rcMonitor.T, ($mi.rcMonitor.R - $mi.rcMonitor.L), ($mi.rcMonitor.B - $mi.rcMonitor.T)
	return $true
}, [IntPtr]::Zero)

Write-Host "`n=== 窗口 ==="
$rows = New-Object System.Collections.ArrayList
[void][Probe.Win]::EnumWindows({
	param($h, $l)
	$p = 0
	[void][Probe.Win]::GetWindowThreadProcessId($h, [ref]$p)
	if ($p -eq $targetPid) {
		$cls = New-Object Text.StringBuilder 256
		[void][Probe.Win]::GetClassNameW($h, $cls, 256)
		$rect = New-Object Probe.Win+RECT
		[void][Probe.Win]::GetWindowRect($h, [ref]$rect)
		$cloak = 0
		[void][Probe.Win]::DwmGetWindowAttribute($h, 14, [ref]$cloak, 4)
		$mon = [Probe.Win]::MonitorFromWindow($h, 2)
		$mi = New-Object Probe.Win+MI
		$mi.cbSize = [Runtime.InteropServices.Marshal]::SizeOf($mi)
		[void][Probe.Win]::GetMonitorInfoW($mon, [ref]$mi)
		[void]$rows.Add([pscustomobject]@{
			Hwnd    = '0x{0:X}' -f $h.ToInt64()
			Class   = $cls.ToString()
			Visible = [Probe.Win]::IsWindowVisible($h)
			Min     = [Probe.Win]::IsIconic($h)
			Cloaked = $cloak
			Rect    = ("{0},{1} {2}x{3}" -f $rect.L, $rect.T, ($rect.R - $rect.L), ($rect.B - $rect.T))
			Monitor = $mi.szDevice
			Primary = [bool]($mi.dwFlags -band 1)
		})
	}
	return $true
}, [IntPtr]::Zero)
$rows | Format-Table -AutoSize
