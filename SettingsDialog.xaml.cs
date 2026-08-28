using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DGXSparkUtilWidget
{
    /// <summary>
    /// 設定ダイアログ。接続先URLと透過率の設定を行う。
    /// </summary>
    public partial class SettingsDialog : Window
    {
        /// <summary>保存された接続先URL（キャンセル時は null）</summary>
        public string? Url { get; private set; }

        /// <summary>保存された不透明度（キャンセル時は 0）</summary>
        public double OpacityValue { get; private set; }

        /// <summary>
        /// コンストラクタ。
        /// </summary>
        /// <param name="currentUrl">現在の接続先URL（プレフィル用）</param>
        /// <param name="currentOpacity">現在の不透明度（0.2〜1.0）</param>
        public SettingsDialog(string currentUrl, double currentOpacity)
        {
            InitializeComponent();
            TxtUrl.Text = currentUrl ?? string.Empty;
            OpacitySlider.Value = Math.Clamp(currentOpacity, 0.2, 1.0);
            UpdateOpacityLabel();
        }

        /// <summary>
        /// 透過率スライダーの値変更時にラベルを更新する。
        /// </summary>
        private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            UpdateOpacityLabel();
            // ライブプレビュー：メインウィンドウの不透明度を即時反映
            if (Owner is MainWindow mainWindow)
            {
                mainWindow.ApplyOpacity(Math.Clamp(e.NewValue, 0.2, 1.0));
            }
        }

        /// <summary>
        /// 透過率ラベルのテキストを更新する。
        /// </summary>
        private void UpdateOpacityLabel()
        {
            LblOpacity.Text = $"{OpacitySlider.Value:F2}";
        }

        /// <summary>
        /// 保存ボタンのクリック処理。
        /// URL を検証し、値を設定してダイアログを閉じる。
        /// </summary>
        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            var url = TxtUrl.Text.Trim();

            // URL が空でないか確認
            if (string.IsNullOrWhiteSpace(url))
            {
                MessageBox.Show(
                    "接続先URLを入力してください。",
                    "入力エラー",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning,
                    MessageBoxResult.OK);
                return;
            }

            // URL の形式を有効性チェック（http / https のみ許可）
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                MessageBox.Show(
                    "有効なURLを入力してください。\n（http:// または https:// で始まるURLを入力してください）",
                    "入力エラー",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning,
                    MessageBoxResult.OK);
                return;
            }

            // 値を保持し、ダイアログを閉じる
            Url = url;
            OpacityValue = Math.Clamp(OpacitySlider.Value, 0.2, 1.0);
            DialogResult = true;
            Close();
        }

        /// <summary>
        /// キャンセルボタンのクリック処理。変更を破棄して閉じる。
        /// </summary>
        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        /// <summary>
        /// ダイアログ右上の閉じるボタンのクリック処理。
        /// キャンセルと同様に処理する。
        /// </summary>
        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        /// <summary>
        /// ダイアログ本体（Border）のドラッグ移動処理。
        /// インタラクティブなコントロール（TextBox、Slider、Button）上のクリックではドラッグしない。
        /// </summary>
        private void RootBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState != MouseButtonState.Pressed)
            {
                return;
            }

            // インタラクティブなコントロール上ならドラッグしない
            if (e.OriginalSource is UIElement source)
            {
                DependencyObject? node = source;
                while (node != null)
                {
                    if (node is TextBox or Slider or Button)
                    {
                        return;
                    }
                    node = System.Windows.Media.VisualTreeHelper.GetParent(node);
                }
            }

            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
                // ドラッグ開始条件を満たさない場合は無視
            }
        }
    }
}
