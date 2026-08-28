using System.Windows;
using System.Windows.Media;

namespace DGXSparkUtilWidget
{
    /// <summary>
    /// 浮遊コントロールバー用ウィンドウ。
    /// WebView2 のネイティブHWND は WPF レンダリング面の上にあるため、
    /// 独立した Topmost ウィンドウとすることで常に最前面に表示・クリック可能になる。
    /// </summary>
    public partial class ControlBarWindow : Window
    {
        public ControlBarWindow()
        {
            InitializeComponent();
        }

        /// <summary>最大化 / 元に戻すボタンのアイコンを更新する。</summary>
        public void SetMaximizeIcon(bool isMaximized)
        {
            IconMaximize.Data = Geometry.Parse(
                isMaximized
                    ? "M 5,9 L 19,9 L 19,23 L 5,23 Z M 9,5 L 23,5 L 23,19 L 9,19 Z"
                    : "M 5,5 L 23,5 L 23,23 L 5,23 Z");
        }
    }
}