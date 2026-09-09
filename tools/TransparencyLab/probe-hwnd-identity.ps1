# 只读：查指定 HWND 的身份（类名、标题、进程、风格、可见性、是否桌面层）。
param([string[]]$Hwnds = @('0x1022E'))

Add-Type -Namespace Id -Name W -MemberDefinition @'
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, System.Text.StringBuilder s, int n);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, System.Text.StringBuilder s, int n);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
[DllImport("user32.dll")] public static extern IntPtr GetWindowLongPtrW(IntPtr h, int i);
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
[DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint f);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string cls, string title);
[DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, uint a, out int v, int s);
[StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
'@

foreach ($hex in $Hwnds) {
	$h = [IntPtr][Convert]::ToInt64($hex.Substring(2), 16)
	$cls = New-Object Text.StringBuilder 256; [void][Id.W]::GetClassNameW($h, $cls, 256)
	$t = New-Object Text.StringBuilder 256; [void][Id.W]::GetWindowTextW($h, $t, 256)
	$wp = 0; [void][Id.W]::GetWindowThreadProcessId($h, [ref]$wp)
	$pn = (Get-Process -Id $wp -ErrorAction SilentlyContinue).ProcessName
	$r = New-Object Id.W+RECT; [void][Id.W]::GetWindowRect($h, [ref]$r)
	$style = [uint32]([long][Id.W]::GetWindowLongPtrW($h, -16))
	$ex = [uint32]([long][Id.W]::GetWindowLongPtrW($h, -20))
	$owner = [Id.W]::GetWindowLongPtrW($h, -8)
	$cloak = -1; [void][Id.W]::DwmGetWindowAttribute($h, 14, [ref]$cloak, 4)
	$root = [Id.W]::GetAncestor($h, 2)
	"{0}: class='{1}' title='{2}' pid={3}({4})" -f $hex, $cls.ToString(), $t.ToString(), $wp, $pn
	"    rect={0},{1} {2}x{3}  visible={4}  cloak={5}  style=0x{6:X8} ex=0x{7:X8}  owner=0x{8:X}  root=0x{9:X}" -f $r.L, $r.T, ($r.R - $r.L), ($r.B - $r.T), [Id.W]::IsWindowVisible($h), $cloak, $style, $ex, $owner.ToInt64(), $root.ToInt64()
}

Write-Host "`n=== 桌面层相关 ==="
$progman = [Id.W]::FindWindowExW([IntPtr]::Zero, [IntPtr]::Zero, 'Progman', $null)
$workerw = [Id.W]::FindWindowExW([IntPtr]::Zero, [IntPtr]::Zero, 'WorkerW', $null)
"Progman=0x{0:X}  WorkerW=0x{1:X}" -f $progman.ToInt64(), $workerw.ToInt64()
