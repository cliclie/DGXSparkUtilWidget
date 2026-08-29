#Requires -Version 5.1
<#
test_hover_after_drag.ps1
検証目的: ウィンドウをドラッグ移動した後に、ウィンドウ外に出ずに右上アイコン位置へホバーしたとき、
          コントロールバーが再表示され且つクリック可能（WindowFromPoint がバーにヒット）になるか。

フェーズ:
  A: ベースライン — ウィンドウ外からアイコン位置へホバー → バー表示・ヒット確認
  B: 中央へ移動（バー自動非表示）→ ドラッグでウィンドウを移動
  C: 【本題】ドラッグ後、ウィンドウ外に出ずにアイコン位置へ直接ホバー → 再表示・ヒット確認
  D: 復帰確認 — ウィンドウ外に出て再入場 → 復帰するか（エッジトリガー仮説の裏取り）
  E: ドラッグなし版 — アイコン位置を離れて中央へ、戻ってくるだけ → C と同症状か

判定に WindowFromPoint を使用（WS_EX_TRANSPARENT のメインウィンドウは透過され、
バー表示時はバーに、バー非表示時はオーバーレイにヒットするはず）。
#>
$ErrorActionPreference = 'Stop'
$proj = "d:\WhitebearATOM1\DGXSparkUtilWidget"
$exe  = Join-Path $proj "bin\Debug\net9.0-windows\win-x64\DGXSparkUtilWidget.exe"
$log  = "$env:APPDATA\DGXSparkUtilWidget\debug.log"

Get-Process DGXSparkUtilWidget -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 1

# P/Invoke ヘルパー（tools\Native.cs と同じ内容＋EnumWindows 等を1ブロックに統合）
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

    // マウスイベント模擬
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

    // 表示中の本アプリのトップレベルウィンドウ一覧（WPF のクラス名で特定）
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

$script:mainHwnd = [IntPtr]::Zero
function Get-MainRect {
    $r = New-Object 'Native+RECT'
    [void][Native]::GetWindowRect($script:mainHwnd, [ref]$r)
    return $r
}

# 表示中のアプリウィンドウのうち面積最小のもの（=コントロールバー。非表示時はリストに存在しない）
function Get-BarWin {
    $wins = [Native2]::GetAppWindows()
    if ($wins.Count -eq 0) { return $null }
    return ($wins | Sort-Object { ($_.Rect.Right - $_.Rect.Left) * ($_.Rect.Bottom - $_.Rect.Top) } | Select-Object -First 1)
}

$before = (Get-Content $log).Count
$p = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds 8

try {
    # --- メイン / オーバーレイ の HWND を診断ログから取得
    $lines = Get-Content $log
    $script:mainHwnd = [IntPtr][Convert]::ToInt64((($lines | Select-String 'Z-order: main='   | Select-Object -Last 1).Line -replace '.*main=0x([0-9A-Fa-f]+).*',   '$1'), 16)
    $ovHwnd   = [IntPtr][Convert]::ToInt64((($lines | Select-String 'Z-order: overlay=' | Select-Object -Last 1).Line -replace '.*overlay=0x([0-9A-Fa-f]+).*', '$1'), 16)
    Write-Host "mainHwnd=0x$(HwndHex $script:mainHwnd) overlayHwnd=0x$(HwndHex $ovHwnd)"

    $S = [double][Native2]::GetDpiForWindow($script:mainHwnd) / 96.0
    Write-Host "DPI scale = $S"

    # カーソルをウィンドウ外（下側）に退避
    $r = Get-MainRect
    [void][Native]::SetCursorPos([int](($r.Left + $r.Right) / 2), ([int]$r.Bottom + 150))
    Start-Sleep -Milliseconds 500

    # ---- Phase A: ベースライン（外からアイコン位置へホバー）----
    $iconX = [int]($r.Right - 100 * $S)
    $iconY = [int]($r.Top + 16 * $S)
    [void][Native]::SetCursorPos($iconX, $iconY)
    Start-Sleep -Milliseconds 900
    $barA = Get-BarWin
    if ($null -eq $barA -or (($barA.Rect.Bottom - $barA.Rect.Top) -ge 100)) {
        Write-Host "PHASE A: NG - control bar not shown"
    } else {
        # バーの実際の矩形で中央を再取得し、確実にバー上へ
        $ax = [int](($barA.Rect.Left + $barA.Rect.Right) / 2)
        $ay = [int](($barA.Rect.Top + $barA.Rect.Bottom) / 2)
        [void][Native]::SetCursorPos($ax, $ay)
        Start-Sleep -Milliseconds 600
        $hitA = Test-Hit $ax $ay
        $verdict = if ($hitA -eq $barA.Hwnd) { "OK (hits bar)" } else { "NG (hits 0x$(HwndHex $hitA))" }
        Write-Host ("PHASE A: bar rect=({0},{1})-({2},{3}) hit=0x{4} bar=0x{5} -> {6}" -f `
            $barA.Rect.Left, $barA.Rect.Top, $barA.Rect.Right, $barA.Rect.Bottom, (HwndHex $hitA), (HwndHex $barA.Hwnd), $verdict)
    }
    # ---- Phase B: 中央へ移動（バー自動非表示）→ ドラッグでウィンドウ移動 ----
    $cx = [int](($r.Left + $r.Right) / 2); $cy = [int](($r.Top + $r.Bottom) / 2)
    [void][Native]::SetCursorPos($cx, $cy)
    Start-Sleep -Milliseconds 1300   # 非表示シーケンス（300ms+350ms）完了待ち

    $dragX = [int]($r.Left + 120 * $S); $dragY = [int]($r.Top + 300 * $S)
    [void][Native]::SetCursorPos($dragX, $dragY)
    Start-Sleep -Milliseconds 400
    [Native]::mouse_event([Native]::MOUSEEVENTF_LEFTDOWN, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 200
    [void][Native]::SetCursorPos($dragX + 90, $dragY + 60)
    Start-Sleep -Milliseconds 300
    [void][Native]::SetCursorPos($dragX + 150, $dragY + 90)
    Start-Sleep -Milliseconds 200
    [Native]::mouse_event([Native]::MOUSEEVENTF_LEFTUP, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 700

    $r2 = Get-MainRect
    Write-Host ("PHASE B: drag done. main ({0},{1}) -> ({2},{3})" -f $r.Left, $r.Top, $r2.Left, $r2.Top)

    # ---- Phase C: 【本題】ドラッグ後、ウィンドウ外に出ずにアイコン位置へ直接ホバー ----
    $iconX2 = [int]($r2.Right - 100 * $S)
    $iconY2 = [int]($r2.Top + 16 * $S)
    [void][Native]::SetCursorPos($iconX2, $iconY2)
    Start-Sleep -Milliseconds 900
    $barC = Get-BarWin
    $barVisibleC = ($null -ne $barC) -and (($barC.Rect.Bottom - $barC.Rect.Top) -lt 100)
    $hitC = Test-Hit $iconX2 $iconY2
    $verdictC = if ($barVisibleC -and $hitC -eq $barC.Hwnd) { "OK (bar shown, hits bar)" }
                elseif ($hitC -eq $ovHwnd)                  { "BUG REPRODUCED (hits overlay = icons unresponsive)" }
                else                                        { "? unexpected hit=0x$(HwndHex $hitC)" }
    Write-Host ("PHASE C: after drag, barVisible={0} hit=0x{1} overlay=0x{2} -> {3}" -f $barVisibleC, (HwndHex $hitC), (HwndHex $ovHwnd), $verdictC)

    # ---- Phase D: 復帰確認（ウィンドウ外に出て再入場）----
    [void][Native]::SetCursorPos([int](($r2.Left + $r2.Right) / 2), ([int]$r2.Bottom + 150))
    Start-Sleep -Milliseconds 600
    [void][Native]::SetCursorPos($iconX2, $iconY2)
    Start-Sleep -Milliseconds 900
    $barD = Get-BarWin
    $barVisibleD = ($null -ne $barD) -and (($barD.Rect.Bottom - $barD.Rect.Top) -lt 100)
    $hitD = Test-Hit $iconX2 $iconY2
    $verdictD = if ($barVisibleD -and $hitD -eq $barD.Hwnd) { "OK (recovered: hits bar)" } else { "NG (still broken, hit=0x$(HwndHex $hitD))" }
    Write-Host ("PHASE D: re-enter from outside, barVisible={0} -> {1}" -f $barVisibleD, $verdictD)

    # ---- Phase E: ドラッグなし版（アイコン位置→中央→戻り、ウィンドウ外には出ない）----
    [void][Native]::SetCursorPos([int](($r2.Left + $r2.Right) / 2), [int](($r2.Top + $r2.Bottom) / 2))
    Start-Sleep -Milliseconds 1300   # バー非表示待ち
    [void][Native]::SetCursorPos($iconX2, $iconY2)
    Start-Sleep -Milliseconds 900
    $barE = Get-BarWin
    $barVisibleE = ($null -ne $barE) -and (($barE.Rect.Bottom - $barE.Rect.Top) -lt 100)
    $hitE = Test-Hit $iconX2 $iconY2
    $verdictE = if ($barVisibleE -and $hitE -eq $barE.Hwnd) { "OK (bar shown, hits bar)" }
                elseif ($hitE -eq $ovHwnd)                  { "BUG REPRODUCED (no drag needed; hits overlay)" }
                else                                        { "? unexpected hit=0x$(HwndHex $hitE)" }
    Write-Host ("PHASE E: no-drag variant, barVisible={0} -> {1}" -f $barVisibleE, $verdictE)

    Write-Host ""
    Write-Host "=== NEW LOG ENTRIES ==="
    Get-Content $log | Select-Object -Skip $before
}
finally {
    Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
    Write-Host "killed pid=$($p.Id)"
}
