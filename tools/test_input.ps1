$ErrorActionPreference = 'Stop'
$proj = "d:\SynologyDrive\WhitebearATOM1\DGXSparkUtilWidget"
$exe  = Join-Path $proj "bin\Debug\net9.0-windows\win-x64\DGXSparkUtilWidget.exe"
$log  = "$env:APPDATA\DGXSparkUtilWidget\debug.log"

Add-Type -Path (Join-Path $proj "tools\Native.cs")

$beforeLines = (Get-Content $log).Count

$p = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds 7

$lines = Get-Content $log
$srcLine  = ($lines | Select-String 'SourceInitialized' | Select-Object -Last 1).Line
$mainLine = ($lines | Select-String 'Z-order: main=' | Select-Object -Last 1).Line
Write-Host "SRC : $srcLine"
Write-Host "MAIN: $mainLine"

$rect = [regex]::Match($mainLine, 'rect=\((-?\d+),(-?\d+)\)-\((-?\d+),(-?\d+)\)')
if (-not $rect.Success) {
    Write-Host "ERROR: main rect not found; aborting cursor move"
    Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
    exit 1
}
$cx = ([int]$rect.Groups[1].Value + [int]$rect.Groups[3].Value) / 2
$cy = ([int]$rect.Groups[2].Value + [int]$rect.Groups[4].Value) / 2
Write-Host "Moving cursor to window center: ($cx,$cy)"

$p0 = New-Object 'Native+POINT'
[void][Native]::GetCursorPos([ref]$p0)
Write-Host "Cursor before: ($($p0.X),$($p0.Y))"

[void][Native]::SetCursorPos($cx, $cy)
Start-Sleep -Milliseconds 1500
[void][Native]::SetCursorPos($cx + 2, $cy + 2)   # 微小移動で MouseEnter を確実に発火
Start-Sleep -Seconds 2

[void][Native]::SetCursorPos($p0.X, $p0.Y)
Start-Sleep -Milliseconds 500

Write-Host "=== NEW LOG ENTRIES ==="
(Get-Content $log) | Select-Object -Skip $beforeLines

Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
Write-Host "killed pid=$($p.Id)"

