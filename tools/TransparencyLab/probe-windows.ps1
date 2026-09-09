param([string[]]$Names = @('DeskBox','TransparencyLab'), [string]$ShotDir = "$PSScriptRoot\shots")
$src = @'
using System; using System.Text; using System.Runtime.InteropServices; using System.Collections.Generic;
public static class P {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumProc p, IntPtr l);
  [DllImport("user32.dll")] public static extern IntPtr GetDesktopWindow();
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
  [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint f);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr h, uint f);
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v);
  [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, uint a, out int v, int s);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
  public static List<IntPtr> All(uint pid) { var r = new List<IntPtr>(); EnumChildWindows(GetDesktopWindow(), (h,l)=>{ uint p; GetWindowThreadProcessId(h, out p); if (p==pid) r.Add(h); return true; }, IntPtr.Zero); return r; }
  public static string Cls(IntPtr h){ if (h==IntPtr.Zero) return "-"; var s=new StringBuilder(256); GetClassName(h,s,256); return s.ToString(); }
  public static string Txt(IntPtr h){ var s=new StringBuilder(256); GetWindowText(h,s,256); return s.ToString(); }
}
'@
Add-Type -TypeDefinition $src
[void][P]::SetProcessDpiAwarenessContext([IntPtr](-4))   # PerMonitorV2 so GetWindowRect/CopyFromScreen use physical pixels
Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Force $ShotDir | Out-Null

foreach ($name in $Names) {
  $p = Get-Process -Name $name -ErrorAction SilentlyContinue | Select-Object -First 1
  if (-not $p) { "===== $name : not running"; continue }
  $all = [P]::All([uint32]$p.Id)
  "===== $name pid=$($p.Id) windows=$($all.Count)"
  $i = 0
  foreach ($h in $all) {
    $r = New-Object P+RECT; [void][P]::GetWindowRect($h,[ref]$r)
    $w = $r.R-$r.L; $ht = $r.B-$r.T
    if ($w -lt 60 -or $ht -lt 40 -or -not [P]::IsWindowVisible($h)) { continue }
    $style=[P]::GetWindowLongPtr($h,-16).ToInt64() -band 0xFFFFFFFF
    $ex=[P]::GetWindowLongPtr($h,-20).ToInt64() -band 0xFFFFFFFF
    $owner=[P]::GetWindowLongPtr($h,-8); $par=[P]::GetParent($h); $root=[P]::GetAncestor($h,2)
    $cloak=0; [void][P]::DwmGetWindowAttribute($h,14,[ref]$cloak,4)
    $sbt=0; [void][P]::DwmGetWindowAttribute($h,38,[ref]$sbt,4)
    "hwnd=0x{0:X} class={1} title='{2}' rect=({3},{4}) {5}x{6} mon=0x{7:X}" -f $h.ToInt64(),[P]::Cls($h),[P]::Txt($h),$r.L,$r.T,$w,$ht,[P]::MonitorFromWindow($h,2).ToInt64()
    "  style=0x{0:X8} ex=0x{1:X8} owner=0x{2:X}({3}) parent=0x{4:X}({5}) root=0x{6:X}({7}) cloaked={8} sbt={9}" -f $style,$ex,$owner.ToInt64(),[P]::Cls($owner),$par.ToInt64(),[P]::Cls($par),$root.ToInt64(),[P]::Cls($root),$cloak,$sbt
    if ($w -le 2000 -and $ht -le 1200) {
      try {
        $bmp = New-Object System.Drawing.Bitmap $w,$ht
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.CopyFromScreen($r.L,$r.T,0,0,(New-Object System.Drawing.Size $w,$ht))
        $g.Dispose()
        $file = Join-Path $ShotDir ("{0}-{1}.png" -f $name,$i)
        $bmp.Save($file); $bmp.Dispose()
        "  shot=$file"
      } catch { "  shot failed: $($_.Exception.Message)" }
    }
    $i++
  }
}