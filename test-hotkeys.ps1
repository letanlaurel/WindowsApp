# Probe which global hotkey combos are free on this machine
$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public class HK {
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr h, int id, uint m, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr h, int id);
    public static bool Try(uint m, uint vk) {
        bool ok = RegisterHotKey(IntPtr.Zero, 0xBEEF, m | 0x4000, vk);
        if (ok) UnregisterHotKey(IntPtr.Zero, 0xBEEF);
        return ok;
    }
}
"@

$ALT = 1; $CTRL = 2; $SHIFT = 4; $A = 0x41
$tests = @(
    @("Alt+Shift+A",       $ALT + $SHIFT),
    @("Ctrl+Alt+A",        $CTRL + $ALT),
    @("Ctrl+Shift+A",      $CTRL + $SHIFT),
    @("Ctrl+Alt+Shift+A",  $CTRL + $ALT + $SHIFT),
    @("Alt+A",             $ALT),
    @("Ctrl+A",            $CTRL),
    @("Shift+A",           $SHIFT)
)
foreach ($t in $tests) {
    $ok = [HK]::Try([uint32]$t[1], [uint32]$A)
    $s = "FAIL (occupied)"
    if ($ok) { $s = "OK (can register)" }
    Write-Output ("{0,-20} : {1}" -f $t[0], $s)
}
