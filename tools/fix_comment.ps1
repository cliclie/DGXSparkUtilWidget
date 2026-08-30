$p = 'd:\WhitebearATOM1\DGXSparkUtilWidget\MainWindow.xaml.cs'
$c = [System.IO.File]::ReadAllText($p)
$old = "// 上端48px（コントロールバー高）・左右各4px・下端4pxの余白はトラックの透明 border で確保。`r`n`t`t// （Chromium の scrollbar 擬似要素は border をサポートするが margin は非対応）"
$new = "// 上端48px（コントロールバー高）・下端4pxの余白はトラックの margin で確保。`r`n`t`t// （headless Edge 検証: track の margin-top/bottom でサムの移動範囲を上下に縮め`r`n`t`t//   余白を確保。border 方式ではサム位置が押し下げられないため margin を採用）"
if ($c.Contains($old)) {
    [System.IO.File]::WriteAllText($p, $c.Replace($old, $new))
    Write-Output "REPLACED CRLF"
} else {
    $oldLf = $old.Replace("`r`n", "`n"); $newLf = $new.Replace("`r`n", "`n")
    if ($c.Contains($oldLf)) {
        [System.IO.File]::WriteAllText($p, $c.Replace($oldLf, $newLf))
        Write-Output "REPLACED LF"
    } else {
        Write-Output "NOT FOUND"
    }
}