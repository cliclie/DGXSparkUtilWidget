#Requires -Version 5.1
<#
test_icon_center.ps1
検証目的: コントロールバーの各アイコンがボタン（46x32）内で上下左右中央に描画されているか、
          WPF で実際にレンダリングしピクセル単位で測定する。

再現方法: ControlBarWindow.xaml と同じビジュアルツリーを C# で構築
          （Canvas 46x32 = ボタン内容領域, Viewbox 18x18 を (14,7) に中央配置, Path は XAML と同一図形）。
          RenderTargetBitmap で描画し、暗色（#444444）ピクセルの境界矩形と中心を算出。
#>
$ErrorActionPreference = 'Stop'

Add-Type -ReferencedAssemblies 'PresentationCore','PresentationFramework','WindowsBase','System.Xaml' -TypeDefinition @"
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

public static class IconTest
{
    // 46x32 のボタン内容領域にアイコンを描画して測定する（ControlBarWindow.xaml と完全同一の生データ）
    public static string Test(string name, string pathData, double strokeThick, double w, double h, double vbSize, bool bottom = false)
    {
        var geo = Geometry.Parse(pathData);
        var path = new Path
        {
            Data = geo,
            Stroke = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)),
            StrokeThickness = strokeThick,
            Fill = Brushes.Transparent,
            Width = w,
            Height = h,
        };
        UIElement child;
        if (bottom)
        {
            // 最小化ボタンの下線: 下端から8pxの下配置（XAML と同じ Grid + Bottom/Center/Margin）
            path.HorizontalAlignment = HorizontalAlignment.Center;
            path.VerticalAlignment = VerticalAlignment.Bottom;
            path.Margin = new Thickness(0, 0, 0, 8);
            child = path;
        }
        else
        {
            var vb = new Viewbox { Width = vbSize, Height = vbSize, Child = path };
            // ContentPresenter（HorizontalAlignment/VerticalAlignment=Center）と同等の Grid で中央配置
            vb.HorizontalAlignment = HorizontalAlignment.Center;
            vb.VerticalAlignment = VerticalAlignment.Center;
            child = vb;
        }
        var grid = new Grid { Width = 46, Height = 32 };
        grid.Children.Add(child);
        grid.Background = new SolidColorBrush(Color.FromRgb(0xE8, 0x11, 0x23)); // ホバー時の赤背景と同色（閉じるボタン）
        grid.Measure(new Size(46, 32));
        grid.Arrange(new Rect(0, 0, 46, 32));

        var rtb = new RenderTargetBitmap(46, 32, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(grid);
        byte[] px = new byte[46 * 32 * 4];
        rtb.CopyPixels(px, 46 * 4, 0);

        int minX = 46, maxX = -1, minY = 32, maxY = -1;
        for (int y = 0; y < 32; y++)
            for (int x = 0; x < 46; x++)
            {
                int i = (y * 46 + x) * 4;
                if (px[i + 2] < 100) // R チャネルが暗色（#444444 の R=68 / 背景 E8=232）
                {
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
        if (maxX < 0) return name + ": no dark pixels found";
        double cx = (minX + maxX) / 2.0, cy = (minY + maxY) / 2.0;
        return string.Format("{0,-12} bbox=({1},{2})-({3},{4}) center=({5:F2},{6:F2}) offsetFromCenter=({7:+F2},{8:+F2})",
            name, minX, minY, maxX, maxY, cx, cy, cx - 23.0, cy - 16.0);
    }
}
"@

# ControlBarWindow.xaml と完全同一の生データ（Data / StrokeThickness / Width / Height / Viewboxサイズ）
Write-Host "container = 46x32 button content area, center = (23,16)"
[IconTest]::Test('arrow',     'M 0.75,0.75 L 0.75,16.75 L 5.75,11.75 L 9.75,18.75 L 12.75,16.75 L 8.75,9.75 L 14.75,7.75 Z', 1.5, 15.5, 19.5, 18)
[IconTest]::Test('hamburger', 'M 1,1 L 19,1 M 1,7.5 L 19,7.5 M 1,14 L 19,14', 2, 20, 15, 20)
[IconTest]::Test('minimize',  'M 1,1 L 21,1', 2, 22, 2, 18, $true)
[IconTest]::Test('maximize',  'M 1,1 L 19,1 L 19,19 L 1,19 Z', 2, 20, 20, 18)
[IconTest]::Test('restore',   'M 7,1 L 19,1 L 19,13 L 7,13 Z M 1,7 L 13,7 L 13,19 L 1,19 Z', 2, 20, 20, 18)
[IconTest]::Test('close',     'M 1,1 L 13,13 M 13,1 L 1,13', 2, 14, 14, 18)
[IconTest]::Test('return',    'M 0.75,0.75 L 20.75,0.75 L 20.75,16.75 L 0.75,16.75 Z M 0.75,6.75 L 20.75,6.75', 1.5, 21.5, 17.5, 16)
