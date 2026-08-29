#Requires -Version 5.1
<#
test_webmode_btn.ps1
讀懆ｨｼ逶ｮ逧・ Web謫堺ｽ懊Δ繝ｼ繝牙・譖ｿ蠕後・縲悟ｾｩ蟶ｰ繝懊ち繝ｳ縲阪′陦ｨ遉ｺ繝ｻ繧ｯ繝ｪ繝・け蜿ｯ閭ｽ縺九∝ｾｩ蟶ｰ蠕後↓襍ｷ蜍墓凾迥ｶ諷具ｼ医え繧｣繝ｳ繝峨え遘ｻ蜍募庄・峨↓謌ｻ繧九°縲・

繝輔ぉ繝ｼ繧ｺ:
  A: 襍ｷ蜍・竊・繝峨Λ繝・げ縺ｧ繧ｦ繧｣繝ｳ繝峨え遘ｻ蜍包ｼ亥燕謠千｢ｺ隱搾ｼ・
  B: 繝帙ヰ繝ｼ 竊・笞｡(WebToggle) 繧ｯ繝ｪ繝・け 竊・Web繝｢繝ｼ繝蛾・遘ｻ + 蠕ｩ蟶ｰ繝懊ち繝ｳ縺ｮ陦ｨ遉ｺ繝ｻ菴咲ｽｮ繝ｻZ-order繝ｻ繝偵ャ繝育｢ｺ隱・
  C: Web繝壹・繧ｸ荳ｭ螟ｮ繧ｯ繝ｪ繝・け・域桃菴懊す繝溘Η繝ｬ繝ｼ繧ｷ繝ｧ繝ｳ・俄・ 蠕ｩ蟶ｰ繝懊ち繝ｳ縺後∪縺陦ｨ遉ｺ繝ｻ蜑埼擇縺句・遒ｺ隱・
  D: 蠕ｩ蟶ｰ繝懊ち繝ｳ繧ｯ繝ｪ繝・け 竊・繧ｦ繧｣繝ｳ繝峨え繝｢繝ｼ繝牙ｾｩ蟶ｰ・・S_EX_TRANSPARENT=True・峨ｒ遒ｺ隱・
  E: 蜀榊ｺｦ繝峨Λ繝・げ 竊・繧ｦ繧｣繝ｳ繝峨え遘ｻ蜍輔〒縺阪ｋ・郁ｵｷ蜍墓凾迥ｶ諷九↓謌ｻ縺｣縺ｦ縺・ｋ・峨％縺ｨ繧堤｢ｺ隱・
#>
$ErrorActionPreference = 'Stop'
$proj = "d:\WhitebearATOM1\DGXSparkUtilWidget"
$exe  = Join-Path $proj "bin\Debug\net9.0-windows\win-x64\DGXSparkUtilWidget.exe"
$log  = "$env:APPDATA\DGXSparkUtilWidget\debug.log"

Get-Process DGXSparkUtilWidget -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 1

# P/Invoke 繝倥Ν繝代・・・numWindows / GetTopWindow 遲峨ｒ蜷ｫ繧邨ｱ蜷医ヶ繝ｭ繝・け・・
Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [DllImport("user32.dll")]
    public static extern bool SetCursorPos(int X, int Y);

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT pt);

    [DllImport("user32.dll")]
    public static extern IntPtr WindowFromPoint(POINT pt);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetTopWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll")]
    public static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, IntPtr dwExtraInfo);

    public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    public const uint MOUSEEVENTF_LEFTUP = 0x0004;
}

public static class Native2
{
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hWnd);

    public class AppWin { public IntPtr Hwnd; public string Class; public Native.RECT Rect; }

    public static List<AppWin> GetAppWindows()
    {
        var list = new List<AppWin>();
        EnumWindows((h, _) =>
        {
            if (!Native.IsWindowVisible(h)) return true;
            var sb = new StringBuilder(256);
            GetClassName(h, sb, sb.Capacity);
            if (sb.ToString().StartsWith("HwndWrapper[DGXSparkUtilWidget"))
            {
                Native.RECT r;
                if (Native.GetWindowRect(h, out r)) list.Add(new AppWin { Hwnd = h, Class = sb.ToString(), Rect = r });
            }
            return true;
        }, IntPtr.Zero);
        return list;
    }
}
"@

function HwndHex([IntPtr]$h) { if ($h -eq [IntPtr]::Zero) { "0" } else { '{0:X}' -f $h.ToInt64() } }

function Test-Hit([int]$x, [int]$y) {
    $pt = New-Object 'Native+POINT'
    $pt.X = $x; $pt.Y = $y
    return [Native]::WindowFromPoint($pt)
}

# topmost蟶ｯ蜈磯ｭ縺九ｉ縺ｮZ-order繧､繝ｳ繝・ャ繧ｯ繧ｹ・郁ｦ九▽縺九ｉ縺ｪ縺代ｌ縺ｰ -1・・
function Get-ZOrder([IntPtr]$target) {
    $h = [Native]::GetTopWindow([IntPtr]::Zero)
    for ($i = 0; $h -ne [IntPtr]::Zero -and $i -lt 200; $i++) {
        if ($h -eq $target) { return $i }
        $h = [Native]::GetWindow($h, 2)
    }
    return -1
}

function Click-Point([int]$x, [int]$y) {
    [void][Native]::SetCursorPos($x, $y)
    Start-Sleep -Milliseconds 300
    [Native]::mouse_event([Native]::MOUSEEVENTF_LEFTDOWN, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 150
    [Native]::mouse_event([Native]::MOUSEEVENTF_LEFTUP, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 600
}

# 蠕ｩ蟶ｰ繝懊ち繝ｳ・・8x48 縺ｮ譛蟆上え繧｣繝ｳ繝峨え・峨ｒ迚ｹ螳・
function Get-ReturnBtn {
    $wins = [Native2]::GetAppWindows()
    foreach ($w in $wins) {
        $wW = $w.Rect.Right - $w.Rect.Left
        $wH = $w.Rect.Bottom - $w.Rect.Top
        if (($wW -lt 60) -and ($wH -lt 60)) { return $w }
    }
    return $null
}

$before = (Get-Content $log).Count
$p = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds 8

try {
    $lines = Get-Content $log
    $mainHwnd = [IntPtr][Convert]::ToInt64((($lines | Select-String 'Z-order: main=' | Select-Object -Last 1).Line -replace '.*main=0x([0-9A-Fa-f]+).*', '$1'), 16)
    $S = [double][Native2]::GetDpiForWindow($mainHwnd) / 96.0
    Write-Host "mainHwnd=0x$(HwndHex $mainHwnd) DPI scale=$S"

    function Get-MainRect {
        $r = New-Object 'Native+RECT'
        [void][Native]::GetWindowRect($mainHwnd, [ref]$r)
        return $r
    }

    # 蠕ｩ蟶ｰ繝懊ち繝ｳ縺ｮ迥ｶ諷九ｒ縺ｾ縺ｨ繧√※蛻､螳壹・蜃ｺ蜉・
    function Check-ReturnBtn([string]$tag, $rect) {
        $btn = Get-ReturnBtn
        if ($null -eq $btn) {
            Write-Host "$tag : BUG - return button window NOT visible"
            return
        }
        $bx = [int](($btn.Rect.Left + $btn.Rect.Right) / 2)
        $by = [int](($btn.Rect.Top + $btn.Rect.Bottom) / 2)
        $zB = Get-ZOrder $btn.Hwnd
        $zM = Get-ZOrder $mainHwnd
        $hit = Test-Hit $bx $by
        $expX = [int]($rect.Right - 32 * $S); $expY = [int]($rect.Top + 32 * $S)
        $posOk = ([math]::Abs($btn.Rect.Left - ($rect.Right - 56 * $S)) -le 4)
        $verdict = if ($hit -eq $btn.Hwnd -and $zB -lt $zM) { "OK (visible, on top, clickable)" }
                   elseif ($hit -ne $btn.Hwnd)              { "BUG (hidden behind: hit=0x$(HwndHex $hit))" }
                   else                                     { "? unexpected" }
        Write-Host ("{0} : btn rect=({1},{2})-({3},{4}) expected~({5},{6}) posOk={7} zBtn={8} zMain={9} -> {10}" -f `
            $tag, $btn.Rect.Left, $btn.Rect.Top, $btn.Rect.Right, $btn.Rect.Bottom, $expX, $expY, $posOk, $zB, $zM, $verdict)
    }

    # ---- Phase A: 襍ｷ蜍募ｾ後√ラ繝ｩ繝・げ縺ｧ繧ｦ繧｣繝ｳ繝峨え遘ｻ蜍包ｼ亥燕謠千｢ｺ隱搾ｼ・---
    $r1 = Get-MainRect
    $cx = [int](($r1.Left + $r1.Right) / 2); $cy = [int](($r1.Top + $r1.Bottom) / 2)
    [void][Native]::SetCursorPos($cx, $cy)
    Start-Sleep -Milliseconds 800
    [Native]::mouse_event([Native]::MOUSEEVENTF_LEFTDOWN, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 200
    [void][Native]::SetCursorPos($cx + 150, $cy + 90)
    Start-Sleep -Milliseconds 300
    [Native]::mouse_event([Native]::MOUSEEVENTF_LEFTUP, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 700
    $r2 = Get-MainRect
    $movedA = (($r2.Left - $r1.Left) -ne 0) -or (($r2.Top - $r1.Top) -ne 0)
    Write-Host ("PHASE A: drag {0} -> main ({1},{2})" -f $(if ($movedA) { "OK" } else { "FAILED" }), $r2.Left, $r2.Top)

    # ---- Phase B: 繝帙ヰ繝ｼ 竊・笞｡ 繧ｯ繝ｪ繝・け 竊・Web繝｢繝ｼ繝・+ 蠕ｩ蟶ｰ繝懊ち繝ｳ遒ｺ隱・----
    $cx2 = [int](($r2.Left + $r2.Right) / 2); $cy2 = [int](($r2.Top + $r2.Bottom) / 2)
    [void][Native]::SetCursorPos($cx2, $cy2)
    Start-Sleep -Milliseconds 900   # 繧ｳ繝ｳ繝医Ο繝ｼ繝ｫ繝舌・陦ｨ遉ｺ蠕・■・医Ξ繝吶Ν繝医Μ繧ｬ繝ｼ・・
    $togX = [int]$r2.Right - 18 * $S; $togY = [int]$r2.Top + 20 * $S
    Write-Host "PHASE B: clicking WebToggle at ($togX,$togY)"
    Click-Point $togX $togY
    Start-Sleep -Seconds 1
    Check-ReturnBtn "PHASE B" $r2

    # ---- Phase C: Web繝壹・繧ｸ荳ｭ螟ｮ繧ｯ繝ｪ繝・け・域桃菴懊す繝溘Η繝ｬ繝ｼ繧ｷ繝ｧ繝ｳ・俄・ 蠕ｩ蟶ｰ繝懊ち繝ｳ蜀咲｢ｺ隱・----
    Write-Host "PHASE C: clicking web page center ($cx2,$cy2)"
    Click-Point $cx2 $cy2
    Start-Sleep -Seconds 1
    Check-ReturnBtn "PHASE C" $r2

    # ---- Phase D: 蠕ｩ蟶ｰ繝懊ち繝ｳ繧ｯ繝ｪ繝・け 竊・繧ｦ繧｣繝ｳ繝峨え繝｢繝ｼ繝牙ｾｩ蟶ｰ遒ｺ隱・----
    $trueCountBefore = ((Get-Content $log) | Select-String 'WS_EX_TRANSPARENT = True').Count
    $btnD = Get-ReturnBtn
    if ($null -ne $btnD) {
        $bx = [int](($btnD.Rect.Left + $btnD.Rect.Right) / 2)
        $by = [int](($btnD.Rect.Top + $btnD.Rect.Bottom) / 2)
        Write-Host "PHASE D: clicking return button at ($bx,$by)"
        Click-Point $bx $by
    } else {
        Write-Host "PHASE D: SKIP - return button not visible, cannot click"
    }
    Start-Sleep -Seconds 1
    $trueCountAfter = ((Get-Content $log) | Select-String 'WS_EX_TRANSPARENT = True').Count
    if ($trueCountAfter -gt $trueCountBefore) { Write-Host "PHASE D: OK (back to window mode, WS_EX_TRANSPARENT=True)" }
    else { Write-Host "PHASE D: BUG (still in web mode)" }

    # ---- Phase E: 蜀榊ｺｦ繝峨Λ繝・げ 竊・襍ｷ蜍墓凾迥ｶ諷具ｼ育ｧｻ蜍募庄・峨↓謌ｻ縺｣縺ｦ縺・ｋ縺・----
    $r3 = Get-MainRect
    $cx3 = [int](($r3.Left + $r3.Right) / 2); $cy3 = [int](($r3.Top + $r3.Bottom) / 2)
    [void][Native]::SetCursorPos($cx3, $cy3)
    Start-Sleep -Milliseconds 800
    [Native]::mouse_event([Native]::MOUSEEVENTF_LEFTDOWN, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 200
    [void][Native]::SetCursorPos($cx3 + 120, $cy3 + 70)
    Start-Sleep -Milliseconds 300
    [Native]::mouse_event([Native]::MOUSEEVENTF_LEFTUP, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 700
    $r4 = Get-MainRect
    $movedE = (($r4.Left - $r3.Left) -ne 0) -or (($r4.Top - $r3.Top) -ne 0)
    Write-Host ("PHASE E: drag {0} -> main ({1},{2})" -f $(if ($movedE) { "OK (window movable again)" } else { "FAILED (input still blocked?)" }), $r4.Left, $r4.Top)

    Write-Host ""
    Write-Host "=== NEW LOG ENTRIES ==="
    Get-Content $log | Select-Object -Skip $before
}
finally {
    Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
    Write-Host "killed pid=$($p.Id)"
}


