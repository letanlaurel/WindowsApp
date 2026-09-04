# F1 截图全流程自动化验证：F1 -> 拖拽框选 -> Esc 取消
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class InputSim {
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
}
"@

# 1. 发送 F1 触发截图
Start-Sleep -Milliseconds 600
[System.Windows.Forms.SendKeys]::SendWait("{F1}")
Start-Sleep -Seconds 2

$alive1 = [bool](Get-Process SnipPin -ErrorAction SilentlyContinue)
Write-Output "F1 press, process alive: $alive1"
if (-not $alive1) { Write-Output "RESULT: CRASH after F1"; exit 1 }

# 2. 模拟拖拽框选
[InputSim]::SetCursorPos(500, 400) | Out-Null
Start-Sleep -Milliseconds 400
[InputSim]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero)   # LEFTDOWN
Start-Sleep -Milliseconds 150
1..10 | ForEach-Object {
    [InputSim]::SetCursorPos(500 + $_ * 25, 400 + $_ * 18) | Out-Null
    Start-Sleep -Milliseconds 25
}
Start-Sleep -Milliseconds 150
[InputSim]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)   # LEFTUP
Start-Sleep -Seconds 2

$alive2 = [bool](Get-Process SnipPin -ErrorAction SilentlyContinue)
Write-Output "After selection, process alive: $alive2"
if (-not $alive2) { Write-Output "RESULT: CRASH after selection"; exit 1 }

# 3. Esc 关闭标注编辑器
[System.Windows.Forms.SendKeys]::SendWait("{ESC}")
Start-Sleep -Seconds 1

$alive3 = [bool](Get-Process SnipPin -ErrorAction SilentlyContinue)
Write-Output "After Esc, process alive: $alive3"

# 4. 检查错误日志
$log = Join-Path $env:APPDATA "SnipPin\error.log"
if (Test-Path $log) {
    Write-Output "--- error.log ---"
    Get-Content $log
} else {
    Write-Output "No error log (no exceptions)"
}
if ($alive1 -and $alive2 -and $alive3) { Write-Output "RESULT: PASS" } else { Write-Output "RESULT: FAIL" }
