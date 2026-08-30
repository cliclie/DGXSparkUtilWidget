#Requires -Version 5.1
$ErrorActionPreference = 'Stop'
$proj = "d:\WhitebearATOM1\DGXSparkUtilWidget"
$exe  = Join-Path $proj "bin\Debug\net9.0-windows\win-x64\DGXSparkUtilWidget.exe"
$log  = Join-Path (Split-Path $exe) "DGXSparkUtilWidget.log"
Get-Process DGXSparkUtilWidget -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 1

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class N {
  [StructLayout(LayoutKind.Sequential)] public struct P { public int X, Y; }
  [StructLayout(LayoutKind.Sequential)] public struct Rect { public int L, T, R, B; }
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(P p);
  [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
  public static extern bool GetWindowRect(IntPtr h, out Rect r);
  [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
  public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll",CharSet=CharSet.Unicode)]
  public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, int x, int y, uint d, IntPtr e);
  public delegate bool EWP(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EWP cb, IntPtr l);
}
'@

function Get-MainHwnd {
    $lines = Get-Content $log
    $m = $lines | Select-String 'Z-order: main=0x([0-9A-Fa-f]+)' | Select-Object -Last 1
    if ($m) { return [IntPtr][Convert]::ToInt64($m.Matches[0].Groups[1].Value, 16) }
    return [IntPtr]::Zero
}

function Click-At([int]$x, [int]$y) {
    [void][N]::SetCursorPos($x, $y)
    Start-Sleep -Milliseconds 300
    [N]::mouse_event(2, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 100
    [N]::mouse_event(4, 0, 0, 0, [IntPtr]::Zero)
}

function Get-AppWins([int]$procId) {
    $script:_res = @()
    $script:_pid = $procId
    $cb = [N+EWP]{ param($h, $l)
        if ([N]::IsWindowVisible($h)) {
            $wp = 0
            [void][N]::GetWindowThreadProcessId($h, [ref]$wp)
            if ($wp -eq [uint32]$script:_pid) {
                $sb = New-Object System.Text.StringBuilder 256
                [void][N]::GetClassName($h, $sb, 256)
                $r = New-Object N+Rect
                [void][N]::GetWindowRect($h, [ref]$r)
                $script:_res += @{ H = $h; C = $sb.ToString(); R = $r }
            }
        }
        return $true
    }
    [N]::EnumWindows($cb, [IntPtr]::Zero)
    return $script:_res
}

$p = Start-Process -FilePath $exe -PassThru
try {
    Start-Sleep -Seconds 12
    if (-not (Get-Process -Id $p.Id -ErrorAction SilentlyContinue)) {
        Write-Host "FAIL: process not running"; exit 1
    }
    Write-Host "PID=$($p.Id)"

    $main = Get-MainHwnd
    if ($main -eq [IntPtr]::Zero) { Write-Host "FAIL: main hwnd not in log"; exit 1 }
    $mr = New-Object N+Rect
    [void][N]::GetWindowRect($main, [ref]$mr)
    $cx = [int](($mr.L + $mr.R) / 2)
    $cy = [int](($mr.T + $mr.B) / 2)
    Write-Host "Main=0x$("{0:X}" -f [int64]$main) center=($cx,$cy)"

    # Phase A: window mode baseline
    $pt = New-Object N+P; $pt.X = $cx; $pt.Y = $cy
    $hitA = [N]::WindowFromPoint($pt)
    $sbA = New-Object System.Text.StringBuilder 256
    [void][N]::GetClassName($hitA, $sbA, 256)
    Write-Host "PHASE A (window mode): hit=0x$("{0:X}" -f [int64]$hitA) '$($sbA.ToString())'"

    # Phase B: switch to web mode via control bar WebToggle
    [void][N]::SetCursorPos($cx, $cy)
    Start-Sleep -Milliseconds 900
    $bar = $null
    for ($i = 0; $i -lt 3; $i++) {
        $wins = @(Get-AppWins $p.Id)
        $bar = $wins | Where-Object {
            ($_.R.B - $_.R.T) -ge 24 -and ($_.R.B - $_.R.T) -le 40 -and
            ($_.R.R - $_.R.L) -ge 200 -and $_.H -ne $main
        } | Select-Object -First 1
        if ($bar) { break }
        Write-Host "  retry ${i} - control bar not found, waiting..."
        Start-Sleep -Seconds 2
    }
    if (-not $bar) { Write-Host "PHASE B: FAIL - control bar not found"; exit 1 }
    $dpi = [N]::GetDpiForWindow($main)
    $sc = [double]$dpi / 96.0
    $togX = [int]($bar.R.L + (23 * $sc))
    $togY = [int](($bar.R.T + $bar.R.B) / 2)
    Write-Host "PHASE B: clicking WebToggle at ($togX,$togY)"
    Click-At $togX $togY
    Start-Sleep -Seconds 2

    # Phase C: web mode - hit at center should reach main or Chrome child
    $pt.X = $cx; $pt.Y = $cy
    $hitC = [N]::WindowFromPoint($pt)
    $sbC = New-Object System.Text.StringBuilder 256
    [void][N]::GetClassName($hitC, $sbC, 256)
    $cls = $sbC.ToString()
    $ok = ($hitC -eq $main) -or ($cls -like "Chrome*")
    if ($ok) {
        Write-Host "PHASE C (web mode): OK - hit=0x$("{0:X}" -f [int64]$hitC) '$cls'"
    } else {
        Write-Host "PHASE C (web mode): FAIL - hit=0x$("{0:X}" -f [int64]$hitC) '$cls' (not main/chrome)"
        exit 1
    }

    Write-Host ""
    Write-Host "=== LOG tail ==="
    Get-Content $log | Select-Object -Last 20
}
finally {
    Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
    Write-Host "killed pid=$($p.Id)"
}