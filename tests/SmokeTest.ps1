<#
  End-to-end smoke test for an installed Maroom (run after build.ps1).
  © 2026 Timo Dörsam

  Drives the real desktop for about 3–4 minutes: switches desktops, opens test windows, sends
  hotkeys and locks a test file. Do not use mouse or keyboard while it runs.
  Everything it changes is restored at the end (desktop 1 active, data folder unchanged).

  Usage (from the repository folder):  powershell -ExecutionPolicy Bypass -File .\tests\SmokeTest.ps1 [-SkipUninstall]
#>
param([switch]$SkipUninstall)
$ErrorActionPreference = 'Stop'

$dataDir  = Join-Path $env:LOCALAPPDATA 'Maroom'
$exe      = Join-Path $dataDir 'Maroom.exe'
$setup    = Join-Path $PSScriptRoot '..\dist\Maroom-Setup.exe'
$desktop  = Join-Path $env:USERPROFILE 'Desktop'
$results  = New-Object System.Collections.Generic.List[object]

Add-Type -ReferencedAssemblies System.Windows.Forms @'
using System; using System.Text; using System.Threading; using System.Collections.Generic; using System.Runtime.InteropServices;
[ComImport, Guid("A5CD92FF-29BE-454C-8D04-D82879FB3F1B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IVirtualDesktopManagerPublic { [PreserveSig] int IsWindowOnCurrentVirtualDesktop(IntPtr h, out int on); }
public static class SmokeHelper {
  [DllImport("user32.dll")] static extern void keybd_event(byte v, byte s, uint f, UIntPtr e);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, int m, IntPtr w, IntPtr l);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string c, string t);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr p, IntPtr a, string c, string t);
  [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int m, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint a, bool i, uint pid);
  [DllImport("kernel32.dll")] static extern IntPtr VirtualAllocEx(IntPtr p, IntPtr a, IntPtr s, uint t, uint pr);
  [DllImport("kernel32.dll")] static extern bool VirtualFreeEx(IntPtr p, IntPtr a, IntPtr s, uint t);
  [DllImport("kernel32.dll")] static extern bool ReadProcessMemory(IntPtr p, IntPtr a, byte[] b, IntPtr s, out IntPtr r);
  [DllImport("kernel32.dll")] static extern bool WriteProcessMemory(IntPtr p, IntPtr a, byte[] b, IntPtr s, out IntPtr r);
  [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
  [DllImport("user32.dll")] static extern IntPtr GetSystemMenu(IntPtr h, bool r);
  [DllImport("user32.dll")] static extern IntPtr GetSubMenu(IntPtr m, int p);
  [DllImport("user32.dll")] static extern int GetMenuItemCount(IntPtr m);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetMenuString(IntPtr m, uint i, StringBuilder s, int n, uint f);
  [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr h, int id, uint m, uint vk);
  [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr h, int id);

  static IVirtualDesktopManagerPublic vdm = (IVirtualDesktopManagerPublic)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("AA509086-5CA9-4C25-8F95-589D3C07B48A")));
  public static bool OnCurrentDesktop(IntPtr h) { int on; vdm.IsWindowOnCurrentVirtualDesktop(h, out on); return on != 0; }

  static void K(byte vk, bool up, bool ext) { keybd_event(vk, 0, (uint)((up ? 2 : 0) | (ext ? 1 : 0)), UIntPtr.Zero); }
  public static void WinCtrl(byte vk) { K(0x5B,false,true); K(0x11,false,false); K(vk,false,true); K(vk,true,true); K(0x11,true,false); K(0x5B,true,true); }
  public static void Hotkey(bool shift, byte vk) { K(0x12,false,false); if (shift) K(0x10,false,false); K(vk,false,false); K(vk,true,false); if (shift) K(0x10,true,false); K(0x12,true,false); }

  static IntPtr ListView() {
    IntPtr d = FindWindowEx(FindWindow("Progman", null), IntPtr.Zero, "SHELLDLL_DefView", null); IntPtr w = IntPtr.Zero;
    while (d == IntPtr.Zero && (w = FindWindowEx(IntPtr.Zero, w, "WorkerW", null)) != IntPtr.Zero) d = FindWindowEx(w, IntPtr.Zero, "SHELLDLL_DefView", null);
    return FindWindowEx(d, IntPtr.Zero, "SysListView32", null);
  }
  // "name@x,y" for every desktop icon
  public static string[] Icons() {
    IntPtr lv = ListView(); int n = (int)SendMessage(lv, 0x1004, IntPtr.Zero, IntPtr.Zero); uint pid; GetWindowThreadProcessId(lv, out pid);
    IntPtr hp = OpenProcess(0x38, false, pid); IntPtr mem = VirtualAllocEx(hp, IntPtr.Zero, (IntPtr)4096, 0x3000, 4); IntPtr x; var r = new List<string>();
    for (int i = 0; i < n; i++) {
      byte[] it = new byte[88]; BitConverter.GetBytes(1).CopyTo(it,0); BitConverter.GetBytes(i).CopyTo(it,4); BitConverter.GetBytes((long)mem+512).CopyTo(it,24); BitConverter.GetBytes(260).CopyTo(it,32);
      WriteProcessMemory(hp, (IntPtr)((long)mem+512), new byte[520], (IntPtr)520, out x); WriteProcessMemory(hp, mem, it, (IntPtr)88, out x); SendMessage(lv, 0x1073, (IntPtr)i, mem);
      byte[] t = new byte[520]; ReadProcessMemory(hp, (IntPtr)((long)mem+512), t, (IntPtr)520, out x); SendMessage(lv, 0x1010, (IntPtr)i, (IntPtr)((long)mem+2048));
      byte[] pt = new byte[8]; ReadProcessMemory(hp, (IntPtr)((long)mem+2048), pt, (IntPtr)8, out x);
      string s = Encoding.Unicode.GetString(t); s = s.Substring(0, s.IndexOf('\0')); r.Add(s + "@" + BitConverter.ToInt32(pt,0) + "," + BitConverter.ToInt32(pt,4));
    }
    VirtualFreeEx(hp, mem, IntPtr.Zero, 0x8000); CloseHandle(hp); r.Sort(StringComparer.Ordinal); return r.ToArray();
  }
  // Texts of the "Maroom" submenu in a window's system menu
  public static string MaroomSubmenu(IntPtr h) {
    IntPtr sys = GetSystemMenu(h, false); var sb = new StringBuilder(100);
    for (int i = 0; i < GetMenuItemCount(sys); i++) {
      sb.Length = 0; GetMenuString(sys, (uint)i, sb, 100, 0x400);
      if (sb.ToString() != "Maroom") continue;
      IntPtr sub = GetSubMenu(sys, i); var r = new List<string>();
      for (int k = 0; k < GetMenuItemCount(sub); k++) { sb.Length = 0; GetMenuString(sub, (uint)k, sb, 100, 0x400); if (sb.Length > 0) r.Add(sb.ToString().Split('\t')[0]); }
      return string.Join(" | ", r.ToArray());
    }
    return null;
  }
  // Opens the system menu and picks "Maroom > item #index" via posted keys (no focus needed)
  public static void PickFromWindowMenu(IntPtr h, int downs) {
    PostMessage(h, 0x0112, (IntPtr)0xF100, (IntPtr)0x20); Thread.Sleep(600);
    Action<int> key = vk => { PostMessage(FindWindow("#32768", null), 0x100, (IntPtr)vk, IntPtr.Zero); Thread.Sleep(350); };
    key(0x26); key(0x26); key(0x27); for (int i = 0; i < downs; i++) key(0x28); key(0x0D);
  }
}
'@

function Test-Step([string]$name, [scriptblock]$body) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    try { $detail = & $body; $ok = $true } catch { $detail = $_.Exception.Message; $ok = $false }
    $results.Add([pscustomobject]@{ Test = $name; Result = $(if ($ok) { 'PASS' } else { 'FAIL' }); Seconds = [Math]::Round($sw.Elapsed.TotalSeconds, 1); Detail = "$detail" })
    Write-Host ("{0,-4} {1} {2}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $name, $(if ($ok) { '' } else { "-> $detail" })) -ForegroundColor $(if ($ok) { 'Green' } else { 'Red' })
}
function Assert($condition, $message) { if (-not $condition) { throw $message } }
function Get-VirtualDesktop {
    $r = Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops'
    if ([BitConverter]::ToString($r.VirtualDesktopIDs[0..15]) -eq [BitConverter]::ToString($r.CurrentVirtualDesktop)) { 1 } else { 2 }
}
function Get-State { (Get-Content (Join-Path $dataDir 'state.txt') -Raw).Trim() }
function Wait-For([scriptblock]$condition, [int]$ms = 6000) { $sw = [Diagnostics.Stopwatch]::StartNew(); while ($sw.ElapsedMilliseconds -lt $ms) { if (& $condition) { return $true }; Start-Sleep -Milliseconds 150 }; & $condition }
function Stop-Maroom { for ($i = 0; $i -lt 30 -and (Get-Process Maroom -EA SilentlyContinue); $i++) { $e = $null; if ([Threading.EventWaitHandle]::TryOpenExisting('Maroom_Stop', [ref]$e)) { [void]$e.Set() }; Start-Sleep -Milliseconds 500 } }
function Start-Maroom { Start-Process $exe; Assert (Wait-For { [bool](Get-Process Maroom -EA SilentlyContinue) } 5000) 'Maroom does not start'; Start-Sleep 2 }
# Window hotkeys act on the foreground window. Only send them when the test window really is in the
# foreground, otherwise they would move or pin one of the user's windows.
function Send-WindowHotkey($window, [byte]$vk) {
    for ($i = 0; $i -lt 10 -and [SmokeHelper]::GetForegroundWindow() -ne $window.Handle; $i++) { [void][SmokeHelper]::SetForegroundWindow($window.Handle); Start-Sleep -Milliseconds 300 }
    Assert ([SmokeHelper]::GetForegroundWindow() -eq $window.Handle) 'test window could not be brought to the foreground, hotkey not sent'
    [SmokeHelper]::Hotkey($true, $vk)
}
function New-TestWindow([string]$title, [switch]$Fullscreen) {
    $extra = if ($Fullscreen) { '$f.FormBorderStyle=''None''; $f.WindowState=''Maximized'';' } else { '' }
    $p = Start-Process powershell -PassThru -ArgumentList '-NoProfile', '-Command', "Add-Type -AssemblyName System.Windows.Forms; `$f=New-Object Windows.Forms.Form; `$f.Text='$title'; $extra `$f.ShowDialog()"
    $h = [IntPtr]::Zero; for ($i = 0; $i -lt 60 -and $h -eq [IntPtr]::Zero; $i++) { Start-Sleep -Milliseconds 200; $h = (Get-Process -Id $p.Id).MainWindowHandle }
    [void][SmokeHelper]::SetForegroundWindow($h); Start-Sleep -Milliseconds 800
    [pscustomobject]@{ Process = $p; Handle = $h }
}

Write-Host "Maroom smoke test – do not use mouse or keyboard" -ForegroundColor Cyan
Assert (Test-Path $exe) "Maroom is not installed ($exe)"
if (-not (Get-Process Maroom -EA SilentlyContinue)) { Start-Maroom }
if ((Get-VirtualDesktop) -ne 1) { [SmokeHelper]::WinCtrl(0x25); Start-Sleep 3 }
$backup = Join-Path $env:TEMP ("maroom-smoketest-" + [Guid]::NewGuid().ToString('N'))
Copy-Item $dataDir $backup -Recurse
$iconsBefore = [SmokeHelper]::Icons()
$desktopItemsBefore = @(Get-ChildItem $desktop -Force).Count
$store2Before = @(Get-ChildItem (Join-Path $env:USERPROFILE 'Desktop 2') -Force).Count   # items of desktop 2 while desktop 1 is active
$version = (Get-Item $exe).VersionInfo.FileVersion

Test-Step 'Switch with Win+Ctrl+Arrow, icons and positions restored' {
    [SmokeHelper]::WinCtrl(0x27); Assert (Wait-For { (Get-VirtualDesktop) -eq 2 -and (Get-State) -eq '1' }) 'desktop 2 / state 1 not reached'
    Start-Sleep 2; [SmokeHelper]::WinCtrl(0x25); Assert (Wait-For { (Get-VirtualDesktop) -eq 1 -and (Get-State) -eq '0' }) 'desktop 1 / state 0 not reached'
    Start-Sleep 2; $diff = Compare-Object $iconsBefore ([SmokeHelper]::Icons())
    Assert ($null -eq $diff) "$(@($diff).Count) icon differences"; "$($iconsBefore.Count) icons identical"
}
Test-Step 'Switch with Alt+2 / Alt+1' {
    [SmokeHelper]::Hotkey($false, 0x32); Assert (Wait-For { (Get-VirtualDesktop) -eq 2 }) 'Alt+2 did not switch'
    Start-Sleep 2; [SmokeHelper]::Hotkey($false, 0x31); Assert (Wait-For { (Get-VirtualDesktop) -eq 1 -and (Get-State) -eq '0' }) 'Alt+1 did not switch back'; Start-Sleep 2
}
Test-Step 'Window: Alt+Shift+2 moves, Alt+Shift+0 pins and unpins' {
    $w = New-TestWindow 'Maroom smoke test'
    try {
        Send-WindowHotkey $w 0x32; Assert (Wait-For { -not [SmokeHelper]::OnCurrentDesktop($w.Handle) } 3000) 'window not moved to desktop 2'
        [SmokeHelper]::WinCtrl(0x27); Start-Sleep 3
        Send-WindowHotkey $w 0x30; Start-Sleep 1
        [SmokeHelper]::WinCtrl(0x25); Start-Sleep 3
        Assert ([SmokeHelper]::OnCurrentDesktop($w.Handle)) 'pinned window not visible on desktop 1'
        Send-WindowHotkey $w 0x30; Start-Sleep 1
        [SmokeHelper]::WinCtrl(0x27); Start-Sleep 3
        Assert (-not [SmokeHelper]::OnCurrentDesktop($w.Handle)) 'window still pinned after Alt+Shift+0'
    } finally { Stop-Process -Id $w.Process.Id -EA SilentlyContinue; if ((Get-VirtualDesktop) -ne 1) { [SmokeHelper]::WinCtrl(0x25); Start-Sleep 3 } }
}
Test-Step 'Window menu: entry present and "To desktop 2" moves the window' {
    $w = New-TestWindow 'Maroom smoke test'
    try {
        $menu = [SmokeHelper]::MaroomSubmenu($w.Handle); Assert ($menu) 'no Maroom entry in the window menu'
        [SmokeHelper]::PickFromWindowMenu($w.Handle, 1)
        Assert (Wait-For { -not [SmokeHelper]::OnCurrentDesktop($w.Handle) } 3000) "window not moved ($menu)"; $menu
    } finally { Stop-Process -Id $w.Process.Id -EA SilentlyContinue }
}
Test-Step 'Full screen pauses hotkeys' {
    $w = New-TestWindow 'Maroom fullscreen test' -Fullscreen
    try {
        Start-Sleep 1; $free = [SmokeHelper]::RegisterHotKey([IntPtr]::Zero, 7701, 0x4001, 0x31)
        if ($free) { [void][SmokeHelper]::UnregisterHotKey([IntPtr]::Zero, 7701) }
        Assert $free 'Alt+1 still taken by Maroom in full screen'
    } finally { Stop-Process -Id $w.Process.Id -EA SilentlyContinue }
    Start-Sleep 1; $taken = -not [SmokeHelper]::RegisterHotKey([IntPtr]::Zero, 7702, 0x4001, 0x31)
    if (-not $taken) { [void][SmokeHelper]::UnregisterHotKey([IntPtr]::Zero, 7702) }
    Assert $taken 'Alt+1 not re-registered after full screen'
}
Test-Step 'Locked file stays, is recorded and returns to its desktop' {
    $file = Join-Path $desktop 'maroom-smoketest-locked.txt'; 'test' | Set-Content $file
    $lock = [IO.File]::Open($file, 'Open', 'ReadWrite', 'None')
    try {
        [SmokeHelper]::WinCtrl(0x27); Assert (Wait-For { (Get-State) -eq '1' } 9000) 'swap to desktop 2 did not finish'
        Assert (Test-Path $file) 'locked file was moved'
        Assert ((Get-Content (Join-Path $dataDir 'pending.txt') -Raw) -match 'maroom-smoketest-locked') 'not recorded in pending.txt'
    } finally { $lock.Close() }
    [SmokeHelper]::WinCtrl(0x25); Assert (Wait-For { (Get-State) -eq '0' } 9000) 'swap back did not finish'
    Assert (Test-Path $file) 'file not back on desktop 1'
    Assert (-not (Test-Path (Join-Path $dataDir 'pending.txt'))) 'pending.txt not cleared'
    Remove-Item $file
}
Test-Step 'Log window opens (--log)' {
    $p = Start-Process $exe -ArgumentList '--log' -PassThru; Start-Sleep 2; $p.Refresh(); $title = $p.MainWindowTitle; Stop-Process -Id $p.Id
    Assert ($title -match 'Maroom') "unexpected title '$title'"; $title
}
Test-Step 'English UI (language=en) and back to automatic' {
    Stop-Maroom; $settings = Join-Path $dataDir 'settings.txt'
    $old = if (Test-Path $settings) { Get-Content $settings } else { @() }
    @($old | Where-Object { $_ -notmatch '^language=' }) + 'language=en' | Set-Content $settings -Encoding UTF8
    Start-Maroom; $w = New-TestWindow 'Maroom language test'
    try { $menu = [SmokeHelper]::MaroomSubmenu($w.Handle) } finally { Stop-Process -Id $w.Process.Id -EA SilentlyContinue }
    Stop-Maroom; if ($old.Count) { $old | Set-Content $settings -Encoding UTF8 } else { Remove-Item $settings }; Start-Maroom
    Assert ($menu -match 'To desktop 1') "window menu not English: $menu"; $menu
}
Test-Step 'Crash restarts 3 times, then stops' {
    Stop-Maroom; Remove-Item (Join-Path $dataDir 'restarts.txt') -EA SilentlyContinue
    $env:MAROOM_CRASHTEST = '1'; Start-Process $exe; $env:MAROOM_CRASHTEST = $null
    Start-Sleep 22
    $restarts = @(Get-Content (Join-Path $dataDir 'restarts.txt')).Count
    $running = [bool](Get-Process Maroom -EA SilentlyContinue)
    Remove-Item (Join-Path $dataDir 'restarts.txt') -EA SilentlyContinue; Stop-Maroom; Start-Maroom
    Assert ($restarts -eq 3 -and -not $running) "restarts=$restarts, still running=$running"; '3 restarts, then stopped'
}
if (Test-Path $setup) {
    Test-Step 'Setup rolls back when Maroom.exe is locked' {
        $lock = [IO.File]::Open($exe, 'Open', 'Read', 'Read')
        try { $p = Start-Process $setup -ArgumentList '/quiet' -PassThru; $p.WaitForExit() } finally { $lock.Close() }
        Assert ($p.ExitCode -eq 1) "exit code $($p.ExitCode)"
        Assert (Wait-For { [bool](Get-Process Maroom -EA SilentlyContinue) } 5000) 'Maroom not running after rollback'
    }
    if (-not $SkipUninstall) {
        Test-Step 'Uninstall on desktop 2 restores desktop 1, keeps Desktop 2, reinstall works' {
            [SmokeHelper]::WinCtrl(0x27); Assert (Wait-For { (Get-State) -eq '1' } 9000) 'not on desktop 2'
            $store2 = Join-Path $env:USERPROFILE 'Desktop 2'
            Start-Process (Join-Path $dataDir 'Maroom-Setup.exe') -ArgumentList '/uninstall', '/quiet'
            Assert (Wait-For { -not (Test-Path $dataDir) -and -not (Get-Process Maroom-Uninstall -EA SilentlyContinue) } 60000) 'uninstall did not finish'
            $desktopCount = @(Get-ChildItem $desktop -Force).Count
            $count2After = @(Get-ChildItem $store2 -Force).Count
            [SmokeHelper]::WinCtrl(0x25); Start-Sleep 2
            $p = Start-Process $setup -ArgumentList '/quiet' -PassThru; $p.WaitForExit()
            Stop-Maroom
            foreach ($item in Get-ChildItem $backup) { if ($item.Name -notlike '*.exe') { Copy-Item $item.FullName $dataDir -Recurse -Force } }
            Set-Content (Join-Path $dataDir 'state.txt') '0' -NoNewline
            Start-Maroom
            Assert ($count2After -eq $store2Before) "Desktop 2 has $count2After items after uninstall, expected $store2Before"
            Assert ($desktopCount -eq $desktopItemsBefore) "desktop folder has $desktopCount items, expected $desktopItemsBefore"
            "desktop 1 back ($desktopCount items), Desktop 2 complete ($count2After)"
        }
    }
}
Test-Step 'Final state: desktop 1, state 0, icons unchanged' {
    if ((Get-VirtualDesktop) -ne 1) { [SmokeHelper]::WinCtrl(0x25); Start-Sleep 3 }
    Assert ((Get-State) -eq '0') "state $(Get-State)"
    $diff = Compare-Object $iconsBefore ([SmokeHelper]::Icons()); Assert ($null -eq $diff) "$(@($diff).Count) icon differences"
}

Remove-Item $backup -Recurse -Force -EA SilentlyContinue
$failed = @($results | Where-Object Result -eq 'FAIL').Count
Write-Host ("`nMaroom {0}: {1} passed, {2} failed" -f $version, ($results.Count - $failed), $failed) -ForegroundColor $(if ($failed) { 'Red' } else { 'Green' })
$results | Format-Table -AutoSize | Out-String -Width 200 | Write-Host
exit $failed
