$ErrorActionPreference = 'Stop'
$proj = "d:\SynologyDrive\WhitebearATOM1\DGXSparkUtilWidget"
$exe  = Join-Path $proj "bin\Debug\net9.0-windows\win-x64\DGXSparkUtilWidget.exe"
$log  = "$env:APPDATA\DGXSparkUtilWidget\debug.log"

Get-Process DGXSparkUtilWidget -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 1

Add-Type -Path (Join-Path $proj "tools\Native.cs")

$before = (Get-Content $log).Count
$p = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds 8

$lines = Get-Content $log
$mainLine = ($lines | Select-String 'Z-order: main='   | Select-Object -Last 1).Line
$ovLine   = ($lines | Select-String 'Z-order: overlay=' | Select-Object -Last 1).Line
$mainHwnd = [IntPtr][Convert]::ToInt64(($mainLine -replace '.*main=0x([0-9A-Fa-f]+).*','$1'), 16)
$ovHwnd   = [IntPtr][Convert]::ToInt64(($ovLine   -replace '.*overlay=0x([0-9A-Fa-f]+).*','$1'), 16)

$r1 = New-Object 'Native+RECT'
[void][Native]::GetWindowRect($mainHwnd, [ref]$r1)
Write-Host "Before drag: main rect = ($($r1.Left),$($r1.Top))-($($r1.Right),$($r1.Bottom))"

$cx = [int](($r1.Left + $r1.Right) / 2)
$cy = [int](($r1.Top + $r1.Bottom) / 2)

$p0 = New-Object 'Native+POINT'
[void][Native]::GetCursorPos([ref]$p0)
Write-Host "cursor before: $($p0.X),$($p0.Y)"

# hover to make control bar appear, then simulate a drag
[void][Native]::SetCursorPos($cx, $cy)
Start-Sleep -Milliseconds 800
[Native]::mouse_event([Native]::MOUSEEVENTF_LEFTDOWN, 0, 0, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds 300
[void][Native]::SetCursorPos($cx + 60, $cy + 40)
Start-Sleep -Milliseconds 400
[void][Native]::SetCursorPos($cx + 90, $cy + 60)
Start-Sleep -Milliseconds 300
[Native]::mouse_event([Native]::MOUSEEVENTF_LEFTUP, 0, 0, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds 600

$r2 = New-Object 'Native+RECT'
[void][Native]::GetWindowRect($mainHwnd, [ref]$r2)
Write-Host "After drag: main rect = ($($r2.Left),$($r2.Top))-($($r2.Right),$($r2.Bottom))"

$ro = New-Object 'Native+RECT'
[void][Native]::GetWindowRect($ovHwnd, [ref]$ro)
Write-Host "After drag: overlay rect = ($($ro.Left),$($ro.Top))-($($ro.Right),$($ro.Bottom))"

$dX = $r2.Left - $r1.Left
$dY = $r2.Top  - $r1.Top
$follow = ($ro.Left -eq $r2.Left) -and ($ro.Top -eq $r2.Top)
if (($dX -ne 0) -or ($dY -ne 0)) { Write-Host "DRAG WORKS (main delta = $dX,$dY)" } else { Write-Host "DRAG FAILED (main did not move)" }
if ($follow) { Write-Host "OVERLAY FOLLOWS main" } else { Write-Host "OVERLAY DIVERGED from main!" }

# restore cursor
[void][Native]::SetCursorPos($p0.X, $p0.Y)
Start-Sleep -Milliseconds 400

Write-Host "--- NEW LOG ENTRIES ---"
Get-Content $log | Select-Object -Skip $before

Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
Write-Host "killed pid=$($p.Id)"

