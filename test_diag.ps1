$ErrorActionPreference = 'Stop'
$proj = "d:\SynologyDrive\WhitebearATOM1\DGXSparkUtilWidget"
$exe  = Join-Path $proj "bin\Debug\net9.0-windows\win-x64\DGXSparkUtilWidget.exe"
$log  = "$env:APPDATA\DGXSparkUtilWidget\debug.log"

# Kill old instances
Get-Process DGXSparkUtilWidget -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 1

Add-Type -Path (Join-Path $proj "Native.cs")

$before = (Get-Content $log).Count
$p = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds 8

$lines = Get-Content $log
$src      = ($lines | Select-String 'SourceInitialized' | Select-Object -Last 1).Line
$mainLine = ($lines | Select-String 'Z-order: main='   | Select-Object -Last 1).Line
Write-Host "SRC  : $src"
Write-Host "MAIN : $mainLine"

$rect = [regex]::Match($mainLine, 'rect=\((-?\d+),(-?\d+)\)-\((-?\d+),(-?\d+)\)')
$r1 = [int]$rect.Groups[1].Value; $r2 = [int]$rect.Groups[2].Value
$r3 = [int]$rect.Groups[3].Value; $r4 = [int]$rect.Groups[4].Value
$cx = ($r1 + $r3) / 2
$cy = ($r2 + $r4) / 2
Write-Host "window center = ($cx, $cy)"

Write-Host "--- top-level Z-order (top 12) ---"
$h = [Native]::GetTopWindow([IntPtr]::Zero)
$i = 0
while ($h -ne [IntPtr]::Zero -and $i -lt 12) {
    $r = New-Object 'Native+RECT'
    [void][Native]::GetWindowRect($h, [ref]$r)
    $ex = [Native]::GetWindowLong($h, -20)
    Write-Host ("#{0}: hwnd=0x{1:X} rect=({2},{3})-({4},{5}) ex=0x{6:X}" -f $i, [int64]$h, $r.Left, $r.Top, $r.Right, $r.Bottom, $ex)
    $h = [Native]::GetWindow($h, 2)
    $i++
}

$pt = New-Object 'Native+POINT'
$pt.X = $cx
$pt.Y = $cy
$wfp = [Native]::WindowFromPoint($pt)
Write-Host "WindowFromPoint(center) = 0x$([int64]$wfp:X)"

$p0 = New-Object 'Native+POINT'
[void][Native]::GetCursorPos([ref]$p0)
Write-Host "cursor before: $($p0.X),$($p0.Y)"
[void][Native]::SetCursorPos($cx, $cy)
Start-Sleep -Seconds 2
[void][Native]::SetCursorPos($cx + 2, $cy + 2)
Start-Sleep -Seconds 2
[void][Native]::SetCursorPos($p0.X, $p0.Y)
Start-Sleep -Milliseconds 500

Write-Host "--- NEW LOG ENTRIES ---"
Get-Content $log | Select-Object -Skip $before

Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
Write-Host "killed pid=$($p.Id)"
