$ErrorActionPreference = 'Stop'
$proj = "d:\WhitebearATOM1\DGXSparkUtilWidget"
$exe  = Join-Path $proj "bin\Debug\net9.0-windows\win-x64\DGXSparkUtilWidget.exe"
$log  = "$env:APPDATA\DGXSparkUtilWidget\debug.log"

Get-Process DGXSparkUtilWidget -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 1

Add-Type -Path (Join-Path $proj "tools\Native.cs")

function Click-Point($x, $y) {
    [void][Native]::SetCursorPos($x, $y)
    Start-Sleep -Milliseconds 300
    [Native]::mouse_event([Native]::MOUSEEVENTF_LEFTDOWN, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 150
    [Native]::mouse_event([Native]::MOUSEEVENTF_LEFTUP, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 500
}

$before = (Get-Content $log).Count
$p = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds 8

$lines = Get-Content $log
$mainLine = ($lines | Select-String 'Z-order: main=' | Select-Object -Last 1).Line
$mainHwnd = [IntPtr][Convert]::ToInt64(($mainLine -replace '.*main=0x([0-9A-Fa-f]+).*','$1'), 16)
$r = New-Object 'Native+RECT'
[void][Native]::GetWindowRect($mainHwnd, [ref]$r)
$cx = [int](($r.Left + $r.Right) / 2)
$cy = [int](($r.Top + $r.Bottom) / 2)
Write-Host "main rect: ($($r.Left),$($r.Top))-($($r.Right),$($r.Bottom)) center: ($cx,$cy)"

$p0 = New-Object 'Native+POINT'
[void][Native]::GetCursorPos([ref]$p0)
Write-Host "cursor before: $($p0.X),$($p0.Y)"

# 1) hover over the window center -> control bar should appear (top-right of main)
[void][Native]::SetCursorPos($cx, $cy)
Start-Sleep -Seconds 1

# 2) click the WebToggle button (rightmost button of the 180px control bar)
$webToggleX = [int]$r.Right - 4 - 18
$webToggleY = [int]$r.Top + 4 + 16
Write-Host "clicking WebToggle at ($webToggleX,$webToggleY)"
Click-Point $webToggleX $webToggleY
Start-Sleep -Seconds 1

# 3) web mode now: click the return button (48x48 window at main.Top+8, right edge -8)
$returnX = [int]$r.Right - 8 - 24
$returnY = [int]$r.Top + 8 + 24
Write-Host "clicking return button at ($returnX,$returnY)"
Click-Point $returnX $returnY
Start-Sleep -Seconds 1

# restore cursor
[void][Native]::SetCursorPos($p0.X, $p0.Y)
Start-Sleep -Milliseconds 400

Write-Host "--- NEW LOG ENTRIES ---"
Get-Content $log | Select-Object -Skip $before

Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
Write-Host "killed pid=$($p.Id)"
