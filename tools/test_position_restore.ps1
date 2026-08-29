#Requires -Version 5.1
<#
test_position_restore.ps1
検証目的: ウィンドウの位置・サイズが終了時に記憶され、次回起動時に復元されること。
          記憶した位置がオフスクリーン（サブモニター消失を模擬）の場合はプライマリモニター作業領域中央へフォールバックすること。

フェーズ:
  A: 起動 → メイン矩形 R1 を取得
  B: ドラッグで (+90,+60) 移動 → R2
  C: コントロールバーの閉じるボタン（×）で正常終了（Closing で保存される）
  D: settings.json の WindowBounds が R2 と一致すること
  E: 再起動 → R3 が R2 と一致すること（復元）
  F: オフスクリーン座標（6000,4000）を settings.json に書き込み → 再起動 →
     プライマリモニター作業領域中央に表示されること（サイズは保存値維持）
  G: クリーンアップ（プロセス終了・settings.json を R2 の値へ復元）
#>
$ErrorActionPreference = 'Stop'
$proj = "d:\WhitebearATOM1\DGXSparkUtilWidget"
$exe  = Join-Path $proj "bin\Debug\net9.0-windows\win-x64\DGXSparkUtilWidget.exe"
$log  = "$env:APPDATA\DGXSparkUtilWidget\debug.log"
$cfg  = "$env:APPDATA\DGXSparkUtilWidget\settings.json"

Get-Process DGXSparkUtilWidget -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 1

# P/Invoke ヘルパー（test_webmode_btn.ps1 と同じパターン＋SystemParametersInfo）
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
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, IntPtr dwExtraInfo);

    // SPI_GETWORKAREA (0x0030): プライマリモニターの作業領域（物理ピクセル）
    [DllImport("user32.dll")]
    public static extern bool SystemParametersInfo(uint uiAction, uint uiParam, out RECT pvParam, uint fWinIni);

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

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    public class AppWin { public IntPtr Hwnd; public string Class; public Native.RECT Rect; }

    public static List<AppWin> GetAppWindows(int pid)
    {
        var list = new List<AppWin>();
        EnumWindows((h, _) =>
        {
            if (!Native.IsWindowVisible(h)) return true;
            uint wpid;
            GetWindowThreadProcessId(h, out wpid);
            if (wpid != (uint)pid) return true;
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

function Get-MainHwnd {
    $lines = Get-Content $log
    $line = ($lines | Select-String 'Z-order: main=' | Select-Object -Last 1).Line
    return [IntPtr][Convert]::ToInt64(($line -replace '.*main=0x([0-9A-Fa-f]+).*', '$1'), 16)
}

function Get-MainRect([IntPtr]$h) {
    $r = New-Object 'Native+RECT'
    [void][Native]::GetWindowRect($h, [ref]$r)
    return $r
}

# コントロールバー（幅>=200・高さ24〜40 のウィンドウ）を検出
function Get-Bar([int]$pid_) {
    foreach ($w in [Native2]::GetAppWindows($pid_)) {
        $wW = $w.Rect.Right - $w.Rect.Left
        $wH = $w.Rect.Bottom - $w.Rect.Top
        if (($wW -ge 200) -and ($wH -ge 24) -and ($wH -le 40)) { return $w }
    }
    return $null
}

function Click-Point([int]$x, [int]$y) {
    [void][Native]::SetCursorPos($x, $y)
    Start-Sleep -Milliseconds 300
    [Native]::mouse_event([Native]::MOUSEEVENTF_LEFTDOWN, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 150
    [Native]::mouse_event([Native]::MOUSEEVENTF_LEFTUP, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 600
}

function Close-AppViaButton([int]$pid_, [Native+RECT]$rect, [double]$S) {
    # ウィンドウ中央へホバーしてコントロールバーを表示し、閉じるボタン（5番目・左端+207px）をクリックする
    $cx = [int](($rect.Left + $rect.Right) / 2)
    $cy = [int](($rect.Top + $rect.Bottom) / 2)
    [void][Native]::SetCursorPos($cx, $cy)
    Start-Sleep -Milliseconds 900
    $bar = Get-Bar $pid_
    if ($null -eq $bar) { Write-Host "  (control bar not found for close click)"; return $false }
    Click-Point ([int]($bar.Rect.Left + 207 * $S)) ([int](($bar.Rect.Top + $bar.Rect.Bottom) / 2))
    return $true
}

$before = (Get-Content $log).Count

# ---- Phase A: 起動 → R1
$p = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds 8
try {
    $mainHwnd = Get-MainHwnd
    $S = [double][Native2]::GetDpiForWindow($mainHwnd) / 96.0
    Write-Host "DPI scale = $S"
    $R1 = Get-MainRect $mainHwnd
    Write-Host ("PHASE A: launched, R1 = ({0},{1})-({2},{3})" -f $R1.Left, $R1.Top, $R1.Right, $R1.Bottom)

    # ---- Phase B: ドラッグ (+90,+60) → R2
    $dx = [int](90 * $S); $dy = [int](60 * $S)
    $sx = [int]($R1.Left + 120 * $S); $sy = [int]($R1.Top + 300 * $S)
    [void][Native]::SetCursorPos($sx, $sy)
    Start-Sleep -Milliseconds 400
    [Native]::mouse_event([Native]::MOUSEEVENTF_LEFTDOWN, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 200
    [void][Native]::SetCursorPos($sx + $dx, $sy + $dy)
    Start-Sleep -Milliseconds 300
    [Native]::mouse_event([Native]::MOUSEEVENTF_LEFTUP, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 800
    $R2 = Get-MainRect $mainHwnd
    $dragOk = (($R2.Left - $R1.Left) -eq $dx) -and (($R2.Top - $R1.Top) -eq $dy)
    Write-Host ("PHASE B: dragged -> R2 = ({0},{1})-({2},{3}) {4}" -f $R2.Left, $R2.Top, $R2.Right, $R2.Bottom, $(if ($dragOk) { "OK" } else { "NG (delta=$($R2.Left-$R1.Left),$($R2.Top-$R1.Top))" }))

    # ---- Phase C: 閉じるボタンで正常終了
    [void](Close-AppViaButton $p.Id $R2 $S)
    $exited = $p.WaitForExit(15000)
    Write-Host ("PHASE C: close button -> process exited={0}" -f $exited)

    # ---- Phase D: settings.json の WindowBounds が R2 と一致するか
    Start-Sleep -Milliseconds 500
    $j = Get-Content $cfg -Raw | ConvertFrom-Json
    $wb = $j.WindowBounds
    if ($null -eq $wb) {
        Write-Host "PHASE D: NG - WindowBounds not saved"
    } else {
        $dOk = ([math]::Abs($wb.Left - $R2.Left) -le 5) -and ([math]::Abs($wb.Top - $R2.Top) -le 5) -and `
               ([math]::Abs($wb.Width - ($R2.Right - $R2.Left)) -le 5) -and ([math]::Abs($wb.Height - ($R2.Bottom - $R2.Top)) -le 5)
        Write-Host ("PHASE D: saved bounds = ({0},{1}) {2}x{3}, expected R2 = ({4},{5}) {6}x{7} -> {8}" -f `
            $wb.Left, $wb.Top, $wb.Width, $wb.Height, $R2.Left, $R2.Top, ($R2.Right-$R2.Left), ($R2.Bottom-$R2.Top), $(if ($dOk) { "OK" } else { "NG" }))
    }
    # ---- Phase E: 再起動 → R3 が R2 と一致するか（復元）
    $p = Start-Process -FilePath $exe -PassThru
    Start-Sleep -Seconds 8
    $mainHwnd = Get-MainHwnd
    $R3 = Get-MainRect $mainHwnd
    $eOk = ([math]::Abs($R3.Left - $R2.Left) -le 5) -and ([math]::Abs($R3.Top - $R2.Top) -le 5) -and `
           ([math]::Abs(($R3.Right-$R3.Left) - ($R2.Right-$R2.Left)) -le 5) -and ([math]::Abs(($R3.Bottom-$R3.Top) - ($R2.Bottom-$R2.Top)) -le 5)
    Write-Host ("PHASE E: relaunched, R3 = ({0},{1})-({2},{3}), expected R2 -> {4}" -f $R3.Left, $R3.Top, $R3.Right, $R3.Bottom, $(if ($eOk) { "OK (restored)" } else { "NG" }))

    # ---- Phase F: オフスクリーン座標で再起動 → プライマリ作業領域中央へフォールバック
    Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 1
    $j = Get-Content $cfg -Raw | ConvertFrom-Json
    $j.WindowBounds.Left = 6000
    $j.WindowBounds.Top = 4000
    $j | ConvertTo-Json -Depth 5 | Set-Content $cfg -Encoding UTF8
    Write-Host "PHASE F: settings off-screen (6000,4000), relaunching..."

    $p = Start-Process -FilePath $exe -PassThru
    Start-Sleep -Seconds 8
    $mainHwnd = Get-MainHwnd
    $R4 = Get-MainRect $mainHwnd
    $wa = New-Object 'Native+RECT'
    [void][Native]::SystemParametersInfo(0x0030, 0, [ref]$wa, 0)
    $w4 = $R4.Right - $R4.Left; $h4 = $R4.Bottom - $R4.Top
    $expX = [int]($wa.Left + (($wa.Right - $wa.Left) - $w4) / 2)
    $expY = [int]($wa.Top + (($wa.Bottom - $wa.Top) - $h4) / 2)
    $fPosOk = ([math]::Abs($R4.Left - $expX) -le 5) -and ([math]::Abs($R4.Top - $expY) -le 5)
    $fSizeOk = ([math]::Abs($w4 - ($R2.Right-$R2.Left)) -le 5) -and ([math]::Abs($h4 - ($R2.Bottom-$R2.Top)) -le 5)
    Write-Host ("PHASE F: R4 = ({0},{1})-({2},{3}), expected center=({4},{5}) size={6}x{7} -> pos:{8} size:{9}" -f `
        $R4.Left, $R4.Top, $R4.Right, $R4.Bottom, $expX, $expY, ($R2.Right-$R2.Left), ($R2.Bottom-$R2.Top), $(if ($fPosOk) { "OK" } else { "NG" }), $(if ($fSizeOk) { "OK (saved size kept)" } else { "NG" }))

    # ---- Phase G: クリーンアップ（settings.json を R2 の値へ復元）
    Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 1
    $j = Get-Content $cfg -Raw | ConvertFrom-Json
    $j.WindowBounds.Left = $R2.Left
    $j.WindowBounds.Top = $R2.Top
    $j.WindowBounds.Width = $R2.Right - $R2.Left
    $j.WindowBounds.Height = $R2.Bottom - $R2.Top
    $j | ConvertTo-Json -Depth 5 | Set-Content $cfg -Encoding UTF8
    Write-Host "PHASE G: cleanup done, settings restored to R2"

    Write-Host ""
    Write-Host "=== NEW LOG ENTRIES (position related) ==="
    Get-Content $log | Select-Object -Skip $before | Select-String '復元|フォールバック|保存|起動'
}
finally {
    Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
    Write-Host "killed pid=$($p.Id)"
}

