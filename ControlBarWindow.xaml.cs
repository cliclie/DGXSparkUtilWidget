using System.Windows;
using System.Windows.Media;

namespace DGXSparkUtilWidget
{
    /// <summary>
    /// 浮遊コントロールバー用ウィンドウ。
    /// WebView2 のネイティブHWND は WPF レンダリング面の上にあるため、
    /// 独立したウィンドウとすることで WebView2 の上に表示・クリック可能になる。
    /// </summary>
    public partial class ControlBarWindow : Window
    {
        public ControlBarWindow()
        {
            InitializeComponent();
        }

        /// <summary>最大化 / 元に戻すボタンのアイコンを更新する（20x20 レイアウト・ストローク2 の座標系）。</summary>
        public void SetMaximizeIcon(bool isMaximized)
        {
            IconMaximize.Data = Geometry.Parse(
                isMaximized
                    ? "M 7,1 L 19,1 L 19,13 L 7,13 Z M 1,7 L 13,7 L 13,19 L 1,19 Z"
                    : "M 1,1 L 19,1 L 19,19 L 1,19 Z");
        }
    }
}