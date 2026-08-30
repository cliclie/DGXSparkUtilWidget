$now = Get-Date
Write-Output ("NOW: " + $now.ToString('yyyy-MM-dd HH:mm:ss'))
$paths = @(
  'd:\WhitebearATOM1\DGXSparkUtilWidget\bin\Debug\net9.0-windows\win-x64\DGXSparkUtilWidget.exe',
  'd:\WhitebearATOM1\DGXSparkUtilWidget\bin\Release\net9.0-windows\win-x64\DGXSparkUtilWidget.exe'
)
foreach ($p in $paths) {
  if (Test-Path $p) {
    $i = Get-Item $p
    Write-Output ("EXE: " + $p + " | " + $i.LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss'))
  } else {
    Write-Output ("MISSING: " + $p)
  }
}
$src = 'd:\WhitebearATOM1\DGXSparkUtilWidget\MainWindow.xaml.cs'
Write-Output ("SRC: " + (Get-Item $src).LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss'))