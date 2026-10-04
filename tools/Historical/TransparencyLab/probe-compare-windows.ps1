# 只读对照探针：并排比较 DeskBox（透明正常）与 TransparencyLab（部分屏不透明）的顶层窗口状态。
# 只调用 GetWindowLongPtr / DwmGetWindowAttribute 等读取接口，不修改任何状态。
param([string[]]$ProcessNames = @('DeskBox', 'TransparencyLab'))

Add-Type -Namespace Cmp -Name W -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc p, IntPtr l);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, System.Text.StringBuilder s, int n);
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
[DllImport("user32.dll")] public static extern IntPtr GetWindowLongPtrW(IntPtr h, int i);
[DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr h, uint f);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern bool GetMonitorInfoW(IntPtr m, ref MI i);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
[DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, uint a, out int v, int s);
public delegate bool EnumProc(IntPtr h, IntPtr l);
[StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct MI { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice; }
'@

$targets = @{}
foreach ($name in $ProcessNames) {
	$p = Get-Process -Name $name -ErrorAction SilentlyContinue
	if ($p) { foreach ($one in $p) { $targets[[int]$one.Id] = $name } }
	else { Write-Host "进程 $name 未运行" }
}
if ($targets.Count -eq 0) { return }

$rows = New-Object System.Collections.ArrayList
[void][Cmp.W]::EnumWindows({
		param($h, $l)
		$wp = 0
		[void][Cmp.W]::GetWindowThreadProcessId($h, [ref]$wp)
		if (-not $targets.ContainsKey([int]$wp)) { return $true }

		$cls = New-Object Text.StringBuilder 256
		[void][Cmp.W]::GetClassNameW($h, $cls, 256)
		if ($cls.ToString() -notlike '*WindowClass*' -and $cls.ToString() -notlike '*Widget*') { return $true }

		$rect = New-Object Cmp.W+RECT
		[void][Cmp.W]::GetWindowRect($h, [ref]$rect)
		$mon = [Cmp.W]::MonitorFromWindow($h, 2)
		$mi = New-Object Cmp.W+MI
		$mi.cbSize = [Runtime.InteropServices.Marshal]::SizeOf($mi)
		[void][Cmp.W]::GetMonitorInfoW($mon, [ref]$mi)

		$style = [uint32]([long][Cmp.W]::GetWindowLongPtrW($h, -16))
		$exStyle = [uint32]([long][Cmp.W]::GetWindowLongPtrW($h, -20))
		$owner = [Cmp.W]::GetWindowLongPtrW($h, -8)

		$cloak = -1; $backdropType = -1; $redirAlpha = -1; $ncrp = -1; $hostBrush = -1
		[void][Cmp.W]::DwmGetWindowAttribute($h, 14, [ref]$cloak, 4)
		[void][Cmp.W]::DwmGetWindowAttribute($h, 38, [ref]$backdropType, 4)
		[void][Cmp.W]::DwmGetWindowAttribute($h, 39, [ref]$redirAlpha, 4)
		[void][Cmp.W]::DwmGetWindowAttribute($h, 2, [ref]$ncrp, 4)
		[void][Cmp.W]::DwmGetWindowAttribute($h, 17, [ref]$hostBrush, 4)

		[void]$rows.Add([pscustomobject]@{
				Proc         = $targets[[int]$wp]
				Hwnd         = '0x{0:X}' -f $h.ToInt64()
				Class        = $cls.ToString()
				Vis          = [Cmp.W]::IsWindowVisible($h)
				Cloak        = $cloak
				Monitor      = $mi.szDevice
				Rect         = ("{0},{1} {2}x{3}" -f $rect.L, $rect.T, ($rect.R - $rect.L), ($rect.B - $rect.T))
				Style        = '0x{0:X8}' -f $style
				ExStyle      = '0x{0:X8}' -f $exStyle
				Owner        = '0x{0:X}' -f $owner.ToInt64()
				BackdropType = $backdropType
				RedirAlpha39 = $redirAlpha
				NcRendPolicy = $ncrp
				HostBrush17  = $hostBrush
			})
		return $true
	}, [IntPtr]::Zero)

$rows | Format-List

Write-Host "`n=== 关键位检查 ==="
foreach ($row in $rows) {
	$ex = [Convert]::ToUInt32($row.ExStyle.Substring(2), 16)
	$layered = [bool]($ex -band 0x80000)
	$noredir = [bool]($ex -band 0x200000)
	$toolwin = [bool]($ex -band 0x80)
	$child = [bool]([Convert]::ToUInt32($row.Style.Substring(2), 16) -band 0x40000000)
	"{0} {1} {2}: LAYERED={3} NOREDIRECTION={4} TOOLWINDOW={5} WS_CHILD={6} owner={7}" -f $row.Proc, $row.Hwnd, $row.Monitor, $layered, $noredir, $toolwin, $child, $row.Owner
}
