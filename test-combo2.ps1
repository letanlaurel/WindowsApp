# E2E combo test using raw keybd_event injection (SendKeys doesn't trigger global hotkeys)
$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public class WinEnum {
    delegate bool CB(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(CB cb, IntPtr l);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    public static List<IntPtr> OfProcess(uint pid) {
        var r = new List<IntPtr>();
        EnumWindows(delegate(IntPtr h, IntPtr l) {
            uint p;
            GetWindowThreadProcessId(h, out p);
            if (p == pid && IsWindowVisible(h)) r.Add(h);
            return true;
        }, IntPtr.Zero);
        return r;
    }
}
"@

function Count-Win([uint32]$procId) { [WinEnum]::OfProcess($procId).Count }
function Press-Key([byte]$vk) {
    [WinEnum]::keybd_event($vk, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 60
    [WinEnum]::keybd_event($vk, 0, 2, [UIntPtr]::Zero)  # KEYEVENTF_KEYUP
    Start-Sleep -Milliseconds 40
}
function Press-Combo([byte[]]$mods, [byte]$vk) {
    foreach ($m in $mods) { [WinEnum]::keybd_event($m, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 50 }
    [WinEnum]::keybd_event($vk, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 80
    [WinEnum]::keybd_event($vk, 0, 2, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 50
    for ($i = $mods.Count - 1; $i -ge 0; $i--) { [WinEnum]::keybd_event($mods[$i], 0, 2, [UIntPtr]::Zero); Start-Sleep -Milliseconds 30 }
}

$cfgPath = Join-Path $env:APPDATA "SnipPin\config.json"
$backup  = Join-Path $env:APPDATA "SnipPin\config.backup.json"
Copy-Item $cfgPath $backup -Force
$cfg = Get-Content $cfgPath -Raw -Encoding UTF8 | ConvertFrom-Json
$orig = $cfg.hotkeys.capture
$cfg.hotkeys.capture = "Ctrl+Alt+A"
$cfg | ConvertTo-Json -Depth 5 | Set-Content $cfgPath -Encoding UTF8
Write-Output "config: capture = Ctrl+Alt+A (was: $orig)"

$p = Start-Process "D:\WindowsApp\SnipPin\bin\Debug\net8.0-windows\win-x64\SnipPin.exe" -PassThru
Start-Sleep -Seconds 4
$n0 = Count-Win $p.Id
Write-Output "startup windows: $n0 (expect 1)"

# Baseline: plain F2 test key first? No - F2 is pinClipboard. Use Ctrl(0x11)+Alt(0x12)+A(0x41)
Press-Combo @(0x11, 0x12) 0x41
Start-Sleep -Seconds 2
$n1 = Count-Win $p.Id
Write-Output "after Ctrl+Alt+A windows: $n1 (expect 2 = overlay opened)"

if ($n1 -eq ($n0 + 1)) {
    # complete the flow: drag selection
    [WinEnum]::SetCursorPos(500, 400) | Out-Null
    Start-Sleep -Milliseconds 400
    [WinEnum]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 150
    1..10 | ForEach-Object {
        [WinEnum]::SetCursorPos(500 + $_ * 25, 400 + $_ * 18) | Out-Null
        Start-Sleep -Milliseconds 25
    }
    Start-Sleep -Milliseconds 150
    [WinEnum]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Seconds 2
    $n2 = Count-Win $p.Id
    Write-Output "after selection windows: $n2"
    [System.Windows.Forms.SendKeys]::SendWait("{ESC}")
    Start-Sleep -Seconds 1
    $n3 = Count-Win $p.Id
    Write-Output "after Esc windows: $n3"
}

$alive = [bool](Get-Process -Id $p.Id -ErrorAction SilentlyContinue)
Write-Output "process alive: $alive"

Copy-Item $backup $cfgPath -Force
Remove-Item $backup -Force -ErrorAction SilentlyContinue
Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
Write-Output "config restored to: $orig"

if ($n1 -eq ($n0 + 1) -and $alive) { Write-Output "RESULT: PASS - combo hotkey E2E works" }
else { Write-Output "RESULT: FAIL" }
