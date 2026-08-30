# E2E test: window-wide transparency via transparent WebView2 + CSS page opacity.
# Phases:
#   A: Opacity=1.0 at startup  -> webview area pixel ~ pure red
#   B: live settings slider -> 0.30 + Save -> pixel ~ red*0.30 + desktop*0.70
#   C: restart with Opacity=0.2 from settings.json -> pixel ~ red*0.20 + desktop*0.80
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class T {
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
}
"@

$AE = [System.Windows.Automation.AutomationElement]
$root = $AE::RootElement
$exe = 'D:\WhitebearATOM1\DGXSparkUtilWidget\bin\Debug\net9.0-windows\win-x64\DGXSparkUtilWidget.exe'
$cfg = Join-Path (Split-Path $exe) 'DGXSparkUtilWidget.json'
Copy-Item $cfg "$cfg.bak" -Force

function Set-Cfg([double]$op) {
  $j = Get-Content $cfg | ConvertFrom-Json
  $j.Url = 'file:///D:/WhitebearATOM1/DGXSparkUtilWidget/tools/opacity_test_page.html'
  $j.Opacity = $op
  $j | ConvertTo-Json -Depth 5 | Set-Content $cfg -Encoding UTF8
}

function Restart-App {
  Get-Process DGXSparkUtilWidget -ErrorAction SilentlyContinue | Stop-Process -Force
  Start-Sleep -Milliseconds 800
  Start-Process $exe
}

function Get-Pixel([int]$x, [int]$y) {
  $bmp = New-Object System.Drawing.Bitmap(1, 1)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen($x, $y, 0, 0, (New-Object System.Drawing.Size(1, 1)))
  $c = $bmp.GetPixel(0, 0)
  $g.Dispose(); $bmp.Dispose()
  return @($c.R, $c.G, $c.B)
}

function Blend([double]$o) {
  # expected = red(255,0,0)*o + desktop*(1-o)
  return @( [int][Math]::Round(255*$o + $desk[0]*(1-$o)),
            [int][Math]::Round($desk[1]*(1-$o)),
            [int][Math]::Round($desk[2]*(1-$o)) )
}

function Check([string]$label, [double]$o) {
  $p = Get-Pixel $sx $sy
  $e = Blend $o
  $dR = [Math]::Abs($p[0]-$e[0]); $dG = [Math]::Abs($p[1]-$e[1]); $dB = [Math]::Abs($p[2]-$e[2])
  $ok = ($dR -le 14 -and $dG -le 14 -and $dB -le 14)
  Write-Host ("{0}: actual=({1},{2},{3}) expected~({4},{5},{6}) delta=({7},{8},{9}) -> {10}" -f $label, $p[0],$p[1],$p[2], $e[0],$e[1],$e[2], $dR,$dG,$dB, $(if($ok){'OK'}else{'FAIL'}))
  return $ok
}

function Click-At([int]$x, [int]$y) {
  [T]::SetCursorPos($x, $y) | Out-Null
  Start-Sleep -Milliseconds 120
  [T]::mouse_event(0x0002, 0, 0, 0, [IntPtr]::Zero)
  [T]::mouse_event(0x0004, 0, 0, 0, [IntPtr]::Zero)
}

try {
  # window center from saved WindowBounds (app restores it exactly)
  $j0 = Get-Content $cfg | ConvertFrom-Json
  $wx = [int]$j0.WindowBounds.Left; $wy = [int]$j0.WindowBounds.Top
  $ww = [int]$j0.WindowBounds.Width; $wh = [int]$j0.WindowBounds.Height
  $sx = $wx + [int]($ww/2); $sy = $wy + [int]($wh/2)
  Write-Host ("window rect=({0},{1}) {2}x{3}, sample point=({4},{5})" -f $wx,$wy,$ww,$wh,$sx,$sy)

  # desktop baseline at sample point (before app starts)
  Get-Process DGXSparkUtilWidget -ErrorAction SilentlyContinue | Stop-Process -Force
  Start-Sleep -Milliseconds 800
  $desk = Get-Pixel $sx $sy
  Write-Host ("desktop pixel at sample point: R={0} G={1} B={2}" -f $desk[0],$desk[1],$desk[2])

  # ---- Phase A: startup with Opacity=1.0 ----
  Set-Cfg 1.0
  Restart-App
  Start-Sleep -Seconds 10
  $mainEl = $root.FindFirst([System.Windows.Automation.TreeScope]::Children,
    (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, "DGX Spark Utility")))
  if ($null -eq $mainEl) { Write-Host "FAIL: main window not found"; exit 1 }
  $r = $mainEl.Current.BoundingRectangle
  Write-Host ("main rect=({0},{1}) {2}x{3}" -f [int]$r.X,[int]$r.Y,[int]$r.Width,[int]$r.Height)
  $okA = Check "Phase A (startup op=1.0)" 1.0

  # ---- Phase B: live slider -> 0.30 via settings dialog ----
  # hover window center to show control bar, then click menu button (2nd from left in 230px bar)
  [T]::SetCursorPos($sx, $sy) | Out-Null
  Start-Sleep -Milliseconds 1000
  $menuX = [int]$r.X + [int]$r.Width - 230 - 8 + 46 + 23
  $menuY = [int]$r.Y + 8 + 16
  Write-Host ("clicking menu button at ({0},{1})" -f $menuX, $menuY)
  Click-At $menuX $menuY
  Start-Sleep -Milliseconds 1500

  $dlgTitle = [string][char]0x8A2D + [char]0x5B9A  # "settei" (settings)
  # ShowDialog() で開いたダイアログは UIA ツリー上、オーナー(メインウィンドウ)の子に付く。
  # デスクトップ直下では見つからないため、メインウィンドウ要素から Descendants 検索する。
  $dlgEl = $mainEl.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
    (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $dlgTitle)))
  if ($null -eq $dlgEl) { Write-Host "FAIL: settings dialog not found"; exit 1 }
  Write-Host "settings dialog opened"

  $slider = $dlgEl.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
    (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Slider)))
  if ($null -eq $slider) { Write-Host "FAIL: slider not found"; exit 1 }
  $range = $slider.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern)
  Write-Host ("slider before=" + $range.Current.Value)
  $range.SetValue(0.30)
  Start-Sleep -Milliseconds 500

  $saveName = [string][char]0x4FDD + [char]0x5B58  # "hozon" (save)
  $saveBtn = $dlgEl.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
    (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $saveName)))
  if ($null -eq $saveBtn) { Write-Host "FAIL: save button not found"; exit 1 }
  $saveBtn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
  Start-Sleep -Milliseconds 2000
  # move mouse away from window (hide control bar)
  [T]::SetCursorPos($sx, $sy + 400) | Out-Null
  Start-Sleep -Milliseconds 800
  $okB = Check "Phase B (live slider op=0.3)" 0.30

  # ---- Phase C: restart with Opacity=0.2 from settings.json ----
  Set-Cfg 0.2
  Restart-App
  Start-Sleep -Seconds 10
  $okC = Check "Phase C (restart op=0.2)" 0.20

  Write-Host ("SUMMARY: A={0} B={1} C={2}" -f $(if($okA){'OK'}else{'FAIL'}), $(if($okB){'OK'}else{'FAIL'}), $(if($okC){'OK'}else{'FAIL'}))
}
finally {
  # cleanup: stop app, restore original settings
  Get-Process DGXSparkUtilWidget -ErrorAction SilentlyContinue | Stop-Process -Force
  Start-Sleep -Milliseconds 500
  if (Test-Path "$cfg.bak") { Move-Item "$cfg.bak" $cfg -Force }
  [T]::SetCursorPos(10, 10) | Out-Null
  Write-Host "cleanup done (settings restored)"
}

