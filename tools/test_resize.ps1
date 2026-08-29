#Requires -Version 5.1
<#
test_resize.ps1
検証目的: ウィンドウの縦横をエッジドラッグでリサイズできること（ウィンドウモード / Webモード両方）。
フェーズ A-H: ウィンドウ移動回帰 / エッジヒット確認 / 右エッジ・右下隅リサイズ /
              Webモード切替時のクリック透過 / Webモードでのリサイズ / 復帰ボタン / 最終回帰。
#>
$ErrorActionPreference = 'Stop'
$proj = "d:\WhitebearATOM1\DGXSparkUtilWidget"
$exe  = Join-Path $proj "bin\Debug\net9.0-windows\win-x64\DGXSparkUtilWidget.exe"
$log  = "$env:APPDATA\DGXSparkUtilWidget\debug.log"

Get-Process DGXSparkUtilWidget -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 1

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
    public static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, IntPtr dwExtraInfo);
    public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    public const uint MOUSEEVENTF_LEFTUP = 0x0004;

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hWnd);

    public class AppWin { public IntPtr Hwnd; public string Class; public RECT Rect; }
    public static List<AppWin> GetAppWindows()
    {
        var list = new List<AppWin>();
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            var sb = new StringBuilder(256);
            GetClassName(h, sb, sb.Capacity);
            if (sb.ToString().StartsWith("HwndWrapper[DGXSparkUtilWidget"))
            {
                RECT r;
                if (GetWindowRect(h, out r)) list.Add(new AppWin { Hwnd = h, Class = sb.ToString(), Rect = r });
            }
            return true;
        }, IntPtr.Zero);
        return list;
    }
}
"@

function HwndHex([IntPtr]$h) { if ($h -eq [IntPtr]::Zero) { "0" } else { '{0:X}' -f $h.ToInt64() } }
function Test-Hit([int]$x, [int]$y) {
    $pt = New-Object 'Native+POINT'; $pt.X = $x; $pt.Y = $y
    return [Native]::WindowFromPoint($pt)
}
function Click-At([int]$x, [int]$y) {
    [void][Native]::SetCursorPos($x, $y); Start-Sleep -Milliseconds 300
    [Native]::mouse_event([Native]::MOUSEEVENTF_LEFTDOWN, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 150
    [Native]::mouse_event([Native]::MOUSEEVENTF_LEFTUP, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 600
}
function Drag-From([int]$x, [int]$y, [int]$dx, [int]$dy) {
    [void][Native]::SetCursorPos($x, $y); Start-Sleep -Milliseconds 400
    [Native]::mouse_event([Native]::MOUSEEVENTF_LEFTDOWN, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 200
    [void][Native]::SetCursorPos($x + [int]($dx/2), $y + [int]($dy/2)); Start-Sleep -Milliseconds 250
    [void][Native]::SetCursorPos($x + $dx, $y + $dy); Start-Sleep -Milliseconds 250
    [Native]::mouse_event([Native]::MOUSEEVENTF_LEFTUP, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 600
}

$before = (Get-Content $log).Count
$p = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds 8

try {
    $lines = Get-Content $log
    $script:mainHwnd = [IntPtr][Convert]::ToInt64((($lines | Select-String 'Z-order: main='   | Select-Object -Last 1).Line -replace '.*main=0x([0-9A-Fa-f]+).*',   '$1'), 16)
    $script:ovHwnd   = [IntPtr][Convert]::ToInt64((($lines | Select-String 'Z-order: overlay=' | Select-Object -Last 1).Line -replace '.*overlay=0x([0-9A-Fa-f]+).*', '$1'), 16)
    $S = [double][Native]::GetDpiForWindow($script:mainHwnd) / 96.0
    Write-Host "main=0x$(HwndHex $script:mainHwnd) overlay=0x$(HwndHex $script:ovHwnd) DPI scale=$S"

    function Get-MainRect { $r = New-Object 'Native+RECT'; [void][Native]::GetWindowRect($script:mainHwnd, [ref]$r); return $r }
    function Get-OvRect   { $r = New-Object 'Native+RECT'; [void][Native]::GetWindowRect($script:ovHwnd,   [ref]$r); return $r }

    # カーソルをウィンドウ外に退避
    $r = Get-MainRect
    [void][Native]::SetCursorPos([int](($r.Left + $r.Right)/2), ([int]$r.Bottom + 150))
    Start-Sleep -Milliseconds 500

    # ---- Phase A: ウィンドウ移動（回帰）----
    Drag-From ([int](($r.Left+$r.Right)/2)) ([int](($r.Top+$r.Bottom)/2)) 60 40
    $ra = Get-MainRect; $oa = Get-OvRect
    $moveOk = ($ra.Left -ne $r.Left) -or ($ra.Top -ne $r.Top)
    $followA = ([math]::Abs(($oa.Right-$oa.Left)-($ra.Right-$ra.Left))) -lt 3
    Write-Host ("PHASE A: move {0} (delta={1},{2}), overlay follows={3}" -f $(if($moveOk){"OK"}else{"NG"}), ($ra.Left-$r.Left), ($ra.Top-$r.Top), $followA)

    # ---- Phase B: 右エッジ帯 -> オーバーレイにヒット ----
    $rb = Get-MainRect
    $ex = [int]($rb.Right - 4 * $S); $ey = [int](($rb.Top + $rb.Bottom)/2)
    [void][Native]::SetCursorPos($ex, $ey); Start-Sleep -Milliseconds 500
    $hitB = Test-Hit $ex $ey
    $hitBHex = HwndHex $hitB; $ovHex = HwndHex $script:ovHwnd
    $verdictB = if ($hitB -eq $script:ovHwnd) { "OK" } else { "NG" }
    Write-Host ("PHASE B: right-edge hit=0x{0} overlay=0x{1} -> {2}" -f $hitBHex, $ovHex, $verdictB)

    # ---- Phase C: 右エッジドラッグで幅拡大 ----
    Drag-From ([int]($rb.Right - 4 * $S)) ([int](($rb.Top + $rb.Bottom)/2)) 100 0
    $rc = Get-MainRect; $oc = Get-OvRect
    $wBefore = $rb.Right - $rb.Left; $wAfter = $rc.Right - $rc.Left
    $followC = ([math]::Abs(($oc.Right-$oc.Left) - ($rc.Right-$rc.Left))) -lt 3
    $verdictC = if (($wAfter - $wBefore) -ge 80 -and $followC) { "OK" } else { "NG" }
    Write-Host ("PHASE C: right-edge resize width {0} -> {1}, overlay follows={2} -> {3}" -f $wBefore, $wAfter, $followC, $verdictC)

    # ---- Phase D: 右下隅ドラッグで幅・高さ拡大 ----
    $rd = Get-MainRect
    Drag-From ([int]($rd.Right - 4 * $S)) ([int]($rd.Bottom - 4 * $S)) 80 60
    $re = Get-MainRect
    $dw = ($re.Right - $re.Left) - ($rd.Right - $rd.Left); $dh = ($re.Bottom - $re.Top) - ($rd.Bottom - $rd.Top)
    $verdictD = if (($dw -ge 60) -and ($dh -ge 45)) { "OK" } else { "NG" }
    Write-Host ("PHASE D: corner resize dW={0} dH={1} -> {2}" -f $dw, $dh, $verdictD)
    # ---- Phase E: Webモード切替 + クリック透過確認 ----
    # 直前のリサイズドラッグ終了後に WPF のホバー追跡が停止し得るため、
    # まず窓内中央へ移動して enter を発生させる（アプリ側でも MouseUp 時に再同期する）
    $rf = Get-MainRect
    [void][Native]::SetCursorPos([int](($rf.Left+$rf.Right)/2), [int](($rf.Top+$rf.Bottom)/2)); Start-Sleep -Milliseconds 600
    # 右上の窓内近傍にホバー（窓外へ出ると MouseLeave が発火してバーが非表示になるため）
    $hx = [int]($rf.Right - 60 * $S); $hy = [int]($rf.Top + 24 * $S)
    [void][Native]::SetCursorPos($hx, $hy); Start-Sleep -Milliseconds 900
    $bar = ([Native]::GetAppWindows() | Sort-Object { ($_.Rect.Right-$_.Rect.Left)*($_.Rect.Bottom-$_.Rect.Top) } | Select-Object -First 1)
    if ($null -eq $bar -or (($bar.Rect.Bottom - $bar.Rect.Top) -ge 100)) { Write-Host "PHASE E: NG - control bar not found"; exit 1 }
    # Web操作トグル（5番目のボタン）中心 = バー左端 + (2+4*36+16) * DPI
    $webBtnX = [int]($bar.Rect.Left + (162 * $S)); $webBtnY = [int](($bar.Rect.Top + $bar.Rect.Bottom)/2)
    Click-At $webBtnX $webBtnY
    Start-Sleep -Milliseconds 500
    $rg = Get-MainRect
    # リサイズ帯ウィンドウ（幅または高さが 10px 以下。Webモードではオーバーレイは非表示で帯4本のみ）
    $bandWins = [Native]::GetAppWindows() | Where-Object { (($_.Rect.Bottom - $_.Rect.Top) -le 10) -or ((($_.Rect.Right - $_.Rect.Left)) -le 10) }
    $hitCenter = Test-Hit ([int](($rg.Left+$rg.Right)/2)) ([int](($rg.Top+$rg.Bottom)/2))
    $centerIsBand = @($bandWins | Where-Object { $_.Hwnd -eq $hitCenter }).Count -gt 0
    $passOk = ($hitCenter -ne $script:mainHwnd) -and (-not $centerIsBand)   # WebView2 子HWND に届くはず
    $hitEdge = Test-Hit ([int]($rg.Right - 4 * $S)) ([int](($rg.Top+$rg.Bottom)/2))
    $edgeIsBand = @($bandWins | Where-Object { $_.Hwnd -eq $hitEdge }).Count -gt 0
    $hitCenterHex = HwndHex $hitCenter; $hitEdgeHex = HwndHex $hitEdge
    $verdictE = if ($passOk -and $edgeIsBand) { "OK" } else { "NG" }
    Write-Host ("PHASE E: web mode center hit=0x{0} (passthrough={1}), right-edge hit=0x{2} bandHit={3} -> {4}" -f `
        $hitCenterHex, $passOk, $hitEdgeHex, $edgeIsBand, $verdictE)

    # ---- Phase F: Webモードで右下隅リサイズ + 復帰ボタン追従 ----
    Drag-From ([int]($rg.Right - 4 * $S)) ([int]($rg.Bottom - 4 * $S)) 80 60
    $rh = Get-MainRect; $oh = Get-OvRect
    $dw2 = ($rh.Right - $rh.Left) - ($rg.Right - $rg.Left); $dh2 = ($rh.Bottom - $rh.Top) - ($rg.Bottom - $rg.Top)
    $followF = ([math]::Abs(($oh.Right-$oh.Left) - ($rh.Right-$rh.Left))) -lt 3
    # 復帰ボタン（48x48・右上）が新しい右上に追従しているか
    $btn = [Native]::GetAppWindows() | Where-Object { ($_.Rect.Bottom - $_.Rect.Top) -ge 40 -and ($_.Rect.Bottom - $_.Rect.Top) -le 60 } | Select-Object -First 1
    $btnOk = ($null -ne $btn) -and ([math]::Abs($btn.Rect.Right - $rh.Right) -lt 20) -and ([math]::Abs($btn.Rect.Top - $rh.Top) -lt 20)
    $verdictF = if (($dw2 -ge 60) -and ($dh2 -ge 45) -and $followF -and $btnOk) { "OK" } else { "NG" }
    Write-Host ("PHASE F: web-mode corner resize dW={0} dH={1}, overlay follows={2}, return button follows={3} -> {4}" -f `
        $dw2, $dh2, $followF, $btnOk, $verdictF)

    # ---- Phase G: 復帰ボタンクリック -> ウィンドウモード復帰 ----
    if ($null -ne $btn) { Click-At ([int](($btn.Rect.Left+$btn.Rect.Right)/2)) ([int](($btn.Rect.Top+$btn.Rect.Bottom)/2)) }
    Start-Sleep -Milliseconds 500
    $ri = Get-MainRect
    $hitBack = Test-Hit ([int](($ri.Left+$ri.Right)/2)) ([int](($ri.Top+$ri.Bottom)/2))
    $hitBackHex = HwndHex $hitBack
    $verdictG = if ($hitBack -eq $script:ovHwnd) { "OK" } else { "NG" }
    Write-Host ("PHASE G: return click, center hit=0x{0} overlay=0x{1} -> {2}" -f $hitBackHex, $ovHex, $verdictG)

    # ---- Phase H: 再度ウィンドウ移動（起動時状態の回帰）----
    Drag-From ([int](($ri.Left+$ri.Right)/2)) ([int](($ri.Top+$ri.Bottom)/2)) 60 40
    $rj = Get-MainRect; $oj = Get-OvRect
    $moveH = ($rj.Left -ne $ri.Left) -or ($rj.Top -ne $ri.Top)
    $followH = ([math]::Abs(($oj.Right-$oj.Left) - ($rj.Right-$rj.Left))) -lt 3
    $verdictH = if ($moveH -and $followH) { "OK" } else { "NG" }
    Write-Host ("PHASE H: move again {0}, overlay follows={1} -> {2}" -f $(if($moveH){"OK"}else{"NG"}), $followH, $verdictH)

    Write-Host ""
    Write-Host "=== NEW LOG ENTRIES (resize related) ==="
    Get-Content $log | Select-Object -Skip $before | Where-Object { $_ -match 'resize|z-order violation|WS_EX_TRANSPARENT' }
}
finally {
    Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
    Write-Host "killed pid=$($p.Id)"
}
