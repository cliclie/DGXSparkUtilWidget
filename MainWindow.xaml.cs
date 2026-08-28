using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace DGXSparkUtilWidget
{
    /// <summary>
    /// メインウィンドウ。フレームレス・角丸のWidgetとして
    /// DGX Spark Utility の Web UI を WebView2 で表示する。
    /// </summary>
    public partial class MainWindow : Window
    {
        private static readonly string SettingsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DGXSparkUtilWidget");

        private static readonly string SettingsPath =
            Path.Combine(SettingsDirectory, "settings.json");

        private string _currentUrl = string.Empty;
        private bool _isMaximized;
        private Rect _normalBounds;
        private DispatcherTimer? _hideTimer;

        // 独立した Topmost ウィンドウとして WebView2 のネイティブHWND 上に表示するため、
        // コントロールバー・入力遮断オーバーレイ・復帰ボタンはすべて別ウィンドウで実装する。
        private ControlBarWindow _controlBar = null!;
        private Window _overlay = null!;
        private bool _isDragging;
        private Point _dragStartScreenMouse;
        private Point _dragStartPos;

        public MainWindow()
        {
            InitializeComponent();
            _normalBounds = new Rect(Left, Top, Width, Height);

            _controlBar = CreateControlBarWindow();
            _overlay = CreateOverlayWindow();

            // メインウィンドウの WM_NCHITTEST をフック。ウィンドウモードでは HTTRANSPARENT を返し、
            // WebView2 のネイティブ子HWND（Chrome_WidgetWin_* 等、別プロセスのウィンドウ）を含めて
            // メインウィンドウ全体を OS レベルでクリックスルーにする。
            _mainHwndSource = HwndSource.FromHwnd(new WindowInteropHelper(this).EnsureHandle());
            _mainHwndSource?.AddHook(OnMainWindowHook);

            LocationChanged += (s, e) => UpdateFloatingWindows();
            SizeChanged += (s, e) => UpdateFloatingWindows();
            // メインウィンドウが再アクティベートされると topmost 帯内で最前面に来るため、
            // ウィンドウモードではオーバーレイを Z-order 直下に再固定し、コントロールバーを最前面に引き上げる。
            Activated += (s, e) =>
            {
                if (!_isWebMode)
                {
                    if (_overlay.Visibility == Visibility.Visible)
                    {
                        PinOverlayBelowMain();
                    }
                    if (_controlBar.Visibility == Visibility.Visible)
                    {
                        BringToFront(new WindowInteropHelper(_controlBar).EnsureHandle());
                    }
                }
            };
            StateChanged += (s, e) =>
            {
                if (WindowState == WindowState.Minimized)
                {
                    _overlay.Hide();
                    _controlBar.Hide();
                    _webModeButtonWindow?.Hide();
                }
                else if (_isWebMode)
                {
                    ShowWebModeButtonWindow();
                }
                else
                {
                    ShowOverlay();
                }
            };
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // 初期状態：ウィンドウモード（Web操作無効）。
            // WebView2 初期化が完了する前に入力遮断オーバーレイを表示し、
            // 起動直後にマウス入力が Web 側へ届くのを防ぐ。
            SetWebMode(false);

            try
            {
                await WebView.EnsureCoreWebView2Async();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"WebView2 の初期化に失敗しました。\nWebView2 Runtime を確認してください。\n\n詳細: {ex.Message}",
                    "初期化エラー", MessageBoxButton.OK, MessageBoxImage.Error);
                Close();
                return;
            }

            // 診断用: 各ナビゲーション完了時にネイティブウィンドウツリーを debug.log に出力する
            WebView.CoreWebView2.NavigationCompleted += (s, e) => LogWindowTree();

            AppSettings? settings = LoadSettings();
            LogDebug($"起動: 設定読込={settings is not null}, Url={settings?.Url}, Opacity={settings?.Opacity}");

            if (settings is not null && !string.IsNullOrWhiteSpace(settings.Url))
            {
                _currentUrl = settings.Url;
                ApplyOpacity(settings.Opacity);
                NavigateToUrl(settings.Url);
            }
            else
            {
                // 初回起動（設定値なし）: 設定ダイアログで URL 入力を促す
                // （入力遮断オーバーレイは一時的に非表示にしてダイアログを操作可能にする）
                _overlay.Hide();
                try
                {
                    var dialog = new SettingsDialog(string.Empty, 1.0) { Owner = this };
                    if (dialog.ShowDialog() == true)
                    {
                        SaveSettings(dialog.Url!, dialog.OpacityValue);
                        _currentUrl = dialog.Url!;
                        ApplyOpacity(dialog.OpacityValue);
                        NavigateToUrl(dialog.Url!);
                    }
                    else
                    {
                        // キャンセルされた場合: 案内ページを表示
                        WebView.CoreWebView2?.NavigateToString(PlaceholderHtml);
                    }
                }
                finally
                {
                    if (!_isWebMode) ShowOverlay();
                }
            }
        }

        private void RootBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState != MouseButtonState.Pressed) return;
            if (e.OriginalSource is UIElement source)
            {
                DependencyObject? node = source;
                while (node != null)
                {
                    if (node is Button or TextBox or Slider) return;
                    node = VisualTreeHelper.GetParent(node);
                }
            }
            ShowControlBar();
            try { DragMove(); }
            catch (InvalidOperationException) { }
        }

        private void RootBorder_MouseEnter(object sender, MouseEventArgs e)
        {
            ShowControlBar();
        }

        private void RootBorder_MouseLeave(object sender, MouseEventArgs e)
        {
            HideControlBar();
        }

        /// <summary>フローティングコントロールバー（独立ウィンドウ）を表示する。</summary>
        private void ShowControlBar()
        {
            _hideTimer?.Stop();
            if (_controlBar.Visibility != Visibility.Visible)
            {
                _controlBar.Show();
                BringToFront(new WindowInteropHelper(_controlBar).EnsureHandle());
                PositionControlBar();
            }
            if (_controlBar.Opacity < 0.5)
            {
                var anim = new DoubleAnimation(0.0, 1.0, new Duration(TimeSpan.FromMilliseconds(250)))
                { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
                _controlBar.BeginAnimation(OpacityProperty, anim);
            }
        }

        /// <summary>フローティングコントロールバーを隠す（ディレイ付き）。完全にフェードアウトしたらウィンドウ自体も非表示にする。</summary>
        private void HideControlBar()
        {
            _hideTimer?.Stop();
            _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _hideTimer.Tick += (s, e) =>
            {
                _hideTimer.Stop();
                if (_controlBar.Opacity > 0.5)
                {
                    var anim = new DoubleAnimation(_controlBar.Opacity, 0.0, new Duration(TimeSpan.FromMilliseconds(350)))
                    { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
                    anim.Completed += (_, __) =>
                    {
                        if (_controlBar.Opacity < 0.5) _controlBar.Hide();
                    };
                    _controlBar.BeginAnimation(OpacityProperty, anim);
                }
            };
            _hideTimer.Start();
        }

        private void BtnMinimize_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void BtnMaximizeRestore_Click(object sender, RoutedEventArgs e)
        {
            if (!_isMaximized)
            {
                _normalBounds = new Rect(Left, Top, Width, Height);
                var workArea = SystemParameters.WorkArea;
                Left = workArea.Left;
                Top = workArea.Top;
                Width = workArea.Width;
                Height = workArea.Height;
                _isMaximized = true;
                _controlBar.SetMaximizeIcon(true);
            }
            else
            {
                Left = _normalBounds.Left;
                Top = _normalBounds.Top;
                Width = _normalBounds.Width;
                Height = _normalBounds.Height;
                _isMaximized = false;
                _controlBar.SetMaximizeIcon(false);
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Application.Current.Shutdown();
        }

        private void BtnMenu_Click(object sender, RoutedEventArgs e)
        {
            // 入力遮断オーバーレイを一時的に非表示にしてダイアログを操作可能にする
            _overlay.Hide();
            try
            {
                var dialog = new SettingsDialog(_currentUrl, Opacity) { Owner = this };
                if (dialog.ShowDialog() == true)
                {
                    SaveSettings(dialog.Url!, dialog.OpacityValue);
                    _currentUrl = dialog.Url!;
                    ApplyOpacity(dialog.OpacityValue);
                    NavigateToUrl(dialog.Url!);
                }
            }
            finally
            {
                if (!_isWebMode) ShowOverlay();
            }
        }

        private bool _isWebMode;
        private bool _wasWebMode;

        // WM_NCHITTEST フック（ウィンドウモードでメインウィンドウ全体を HTTRANSPARENT にする）
        private HwndSource? _mainHwndSource;
        private int _hitTestLogCount;

        private const int WM_NCHITTEST = 0x0084;
        private const int HTCLIENT = 1;
        private const int HTTRANSPARENT = -1;

        private void BtnWebToggle_Click(object sender, RoutedEventArgs e)
        {
            SetWebMode(true);
        }

        /// <summary>
        /// Web操作モード / ウィンドウ操作モードを切り替える。
        /// Webモード: WebView2 のネイティブHWND がマウス操作を受け取る。復帰ボタン（ミニウィンドウ）を常時表示。
        /// ウィンドウモード: ネイティブHWND の入力を透過化してマウス操作を遮断し、ウィンドウドラッグ等が可能。
        /// </summary>
        private void SetWebMode(bool webMode)
        {
            _isWebMode = webMode;

            if (webMode)
            {
                // Web操作モード: オーバーレイを除去しネイティブHWNDが直接入力を受け取る。
                // コントロールバーを非表示、復帰ボタン（独立ミニウィンドウ）を表示。
                HideOverlay();
                _hideTimer?.Stop();
                _controlBar.Hide();
                ShowWebModeButtonWindow();
            }
            else
            {
                // ウィンドウ操作モード: オーバーレイを表示しマウス入力をすべてキャプチャ（Webページは操作不可）。
                // 復帰ボタンを非表示。コントロールバーはホバー時に表示される（オーバーレイの MouseEnter）。
                ShowOverlay();
                HideWebModeButtonWindow();
                // Webモードから戻った直後は、操作した場所の近く（右上）にコントロールバーを即表示する
                if (_wasWebMode) ShowControlBar();
            }
            _wasWebMode = webMode;
        }

        // ====================== 入力遮断オーバーレイ / 浮遊コントロールバー ======================

        /// <summary>
        /// 入力遮断オーバーレイを生成する。ウィンドウモードで WebView2 のネイティブHWND より手前に
        /// 独立した Topmost ウィンドウとして表示し、マウス入力をすべてキャプチャして Web ページの
        /// 操作を無効化する。見た目は完全に透明。
        /// WS_EX_NOACTIVATE を付与し、クリックしてもアクティベーションを奪わない（浮遊コントロールバーが
        /// 最前面のまま維持される）。
        /// </summary>
        private Window CreateOverlayWindow()
        {
            var overlay = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                Topmost = true,
                ShowInTaskbar = false,
                ResizeMode = ResizeMode.NoResize,
            };
            // 全領域を透明なBorderで覆い、WPFのヒットテストを確実に行わせる（背景ブラシがnullでないと
            // マウスイベントが受信されない）。見た目は完全に透明。
            overlay.Content = new Border { Background = Brushes.Transparent };
            overlay.Loaded += (s, e) =>
            {
                IntPtr h = new WindowInteropHelper(overlay).Handle;
                int ex = GetWindowLong(h, GWL_EXSTYLE);
                SetWindowLong(h, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE);
            };
            overlay.MouseDown += Overlay_MouseDown;
            overlay.MouseMove += Overlay_MouseMove;
            overlay.MouseUp += Overlay_MouseUp;
            overlay.MouseEnter += (s, e) =>
            {
                LogDebug("overlay MouseEnter -> ShowControlBar");
                ShowControlBar();
            };
            overlay.MouseLeave += (s, e) =>
            {
                LogDebug("overlay MouseLeave -> HideControlBar");
                HideControlBar();
            };
            return overlay;
        }

        /// <summary>浮遊コントロールバー（独立ウィンドウ）を生成し、ボタンのイベントを接続する。</summary>
        private ControlBarWindow CreateControlBarWindow()
        {
            var bar = new ControlBarWindow();
            bar.Loaded += (s, e) =>
            {
                // アクティベーションを奪わない（メインウィンドウのキーボードフォーカスを維持するため）
                IntPtr h = new WindowInteropHelper(bar).EnsureHandle();
                int ex = GetWindowLong(h, GWL_EXSTYLE);
                SetWindowLong(h, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE);
            };
            bar.BtnMinimize.Click += BtnMinimize_Click;
            bar.BtnMaximizeRestore.Click += BtnMaximizeRestore_Click;
            bar.BtnClose.Click += BtnClose_Click;
            bar.BtnMenu.Click += BtnMenu_Click;
            bar.BtnWebToggle.Click += BtnWebToggle_Click;
            bar.MouseEnter += (s, e) => _hideTimer?.Stop();
            bar.MouseLeave += (s, e) => HideControlBar();
            return bar;
        }

        private void Overlay_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left || e.ButtonState != MouseButtonState.Pressed) return;
            _isDragging = true;
            // スクリーン絶対座標で記録することで、ウィンドウが動いても基準がずれずドラッグが安定する。
            _dragStartScreenMouse = ((FrameworkElement)sender).PointToScreen(e.GetPosition(null));
            _dragStartPos = new Point(Left, Top);
            _overlay.CaptureMouse();
            LogDebug($"overlay MouseDown (drag start) main=({Left},{Top})");
        }

        private void Overlay_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isDragging) return;
            var fe = (FrameworkElement)sender;
            Point nowScreen = fe.PointToScreen(e.GetPosition(null));
            var src = PresentationSource.FromVisual(fe) as HwndSource;
            double scaleX = src?.CompositionTarget.TransformToDevice.M11 ?? 1.0;
            double scaleY = src?.CompositionTarget.TransformToDevice.M22 ?? 1.0;
            Left = _dragStartPos.X + (nowScreen.X - _dragStartScreenMouse.X) / scaleX;
            Top = _dragStartPos.Y + (nowScreen.Y - _dragStartScreenMouse.Y) / scaleY;
        }

        private void Overlay_MouseUp(object sender, MouseButtonEventArgs e)
        {
            _isDragging = false;
            _overlay.ReleaseMouseCapture();
        }

        private void ShowOverlay()
        {
            _overlay.Show();
            PinOverlayBelowMain();
            UpdateFloatingWindows();
            // キーボード入力をメインウィンドウに向かい、Web ページへキーが送られないようにする
            SetFocus(new WindowInteropHelper(this).Handle);
        }

        private void HideOverlay()
        {
            _overlay.Hide();
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        private static readonly IntPtr HWND_TOP = IntPtr.Zero;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOACTIVATE = 0x0010;

        /// <summary>ウィンドウをアクティベーション変化なしに最前面（topmost帯内で）に移動する。</summary>
        private static void BringToFront(IntPtr hwnd)
        {
            SetWindowPos(hwnd, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        /// <summary>
        /// メインウィンドウの HwndSource フック。ウィンドウモードでは WM_NCHITTEST に対し
        /// HTTRANSPARENT を返すことで、OS がメインウィンドウ配下の WebView2 ネイティブHWND
        /// （Chrome_WidgetWin_* 等、別プロセスのウィンドウのため下から制御不能）を含めて
        /// 全体をクリックスルーとする。透過したマウス入力は Z-order 直下の入力遮断オーバーレイに届く。
        /// Webモードではデフォルト処理（IntPtr.Zero）を返し、Webページが通常通り入力を受け取る。
        /// </summary>
        private IntPtr OnMainWindowHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_NCHITTEST && !_isWebMode && wParam.ToInt32() == HTCLIENT)
            {
                if (_hitTestLogCount < 3)
                {
                    _hitTestLogCount++;
                    LogDebug($"WM_NCHITTEST -> HTTRANSPARENT (ウィンドウモード・Web入力遮断) #{_hitTestLogCount}");
                }
                handled = true;
                return new IntPtr(HTTRANSPARENT);
            }
            return IntPtr.Zero;
        }

        /// <summary>
        /// 入力遮断オーバーレイを Z-order 上でメインウィンドウの直下に固定する。
        /// メインウィンドウが HTTRANSPARENT のとき、透過した入力は必ずこのオーバーレイに届く。
        /// </summary>
        private void PinOverlayBelowMain()
        {
            SetWindowPos(
                new WindowInteropHelper(_overlay).EnsureHandle(),
                new WindowInteropHelper(this).EnsureHandle(),
                0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        [DllImport("user32.dll")]
        private static extern bool SetFocus(IntPtr hWnd);

        /// <summary>オーバーレイ / コントロールバー / 復帰ボタンをメインウィンドウの位置・サイズに追従させる。</summary>
        private void UpdateFloatingWindows()
        {
            if (double.IsNaN(Left) || double.IsNaN(Top)) return;
            _overlay.Left = Left;
            _overlay.Top = Top;
            _overlay.Width = Width;
            _overlay.Height = Height;
            PositionControlBar();
            PositionWebModeButtonWindow();
        }

        private void PositionControlBar()
        {
            if (_controlBar is not { Visibility: Visibility.Visible }) return;
            _controlBar.Left = Left + Width - _controlBar.Width - 4;
            _controlBar.Top = Top + 4;
        }

        /// <summary>
        /// Webモード用復帰ボタンのミニウィンドウ。
        /// WebView2 のネイティブHWND は WPF レンダリング面の上に存在するため、
        /// WPF 要素として復帰ボタンを描いてもクリックできない。独立した Topmost ウィンドウとすることで
        /// 常にネイティブHWND より手前に表示される。
        /// </summary>
        private Window? _webModeButtonWindow;

        private void ShowWebModeButtonWindow()
        {
            if (_webModeButtonWindow is null)
            {
                var button = new Button
                {
                    Width = 40,
                    Height = 40,
                    Cursor = Cursors.Hand,
                    ToolTip = "ウィンドウ操作に戻る",
                    Template = CreateReturnButtonTemplate(),
                    Content = new System.Windows.Shapes.Path
                    {
                        Data = Geometry.Parse("M 6,8 L 26,8 L 26,24 L 6,24 Z M 6,14 L 26,14"),
                        Stroke = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)),
                        StrokeThickness = 1.5,
                        Fill = Brushes.Transparent,
                        SnapsToDevicePixels = true,
                    },
                };
                button.Click += (s, e) => SetWebMode(false);

                _webModeButtonWindow = new Window
                {
                    WindowStyle = WindowStyle.None,
                    AllowsTransparency = true,
                    Background = Brushes.Transparent,
                    Topmost = true,
                    ShowInTaskbar = false,
                    Width = 48,
                    Height = 48,
                    Content = button,
                };
            }
            PositionWebModeButtonWindow();
            _webModeButtonWindow.Show();
            _webModeButtonWindow.Activate();
        }

        private void HideWebModeButtonWindow()
        {
            _webModeButtonWindow?.Hide();
        }

        private void PositionWebModeButtonWindow()
        {
            if (_webModeButtonWindow is null || _webModeButtonWindow.Visibility != Visibility.Visible) return;
            _webModeButtonWindow.Left = Left + Width - _webModeButtonWindow.Width - 8;
            _webModeButtonWindow.Top = Top + 8;
        }

        private static ControlTemplate CreateReturnButtonTemplate()
        {
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
            border.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x2E)));
            border.SetValue(Border.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x66)));
            border.SetValue(Border.BorderThicknessProperty, new Thickness(1));

            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(content);

            return new ControlTemplate(typeof(Button)) { VisualTree = border };
        }

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_NOACTIVATE = 0x08000000;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr hWndParent, EnumChildProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        private delegate bool EnumChildProc(IntPtr hWnd, IntPtr lParam);

        /// <summary>メインウィンドウ配下のネイティブウィンドウツリーを debug.log に出力する（診断用）。</summary>
        private void LogWindowTree()
        {
            try
            {
                var sb = new StringBuilder();
                AppendWindowTree(new WindowInteropHelper(this).Handle, sb, "  ");
                LogDebug("ウィンドウツリー:\n" + sb.ToString());
            }
            catch
            {
                // 診断ログ失敗は無視
            }
        }

        private static void AppendWindowTree(IntPtr parent, StringBuilder sb, string indent)
        {
            EnumChildWindows(parent, (hWnd, _) =>
            {
                var name = new StringBuilder(256);
                GetClassName(hWnd, name, name.Capacity);
                int ex = GetWindowLong(hWnd, GWL_EXSTYLE);
                bool transparent = (ex & WS_EX_TRANSPARENT) != 0;
                bool noActivate = (ex & WS_EX_NOACTIVATE) != 0;
                sb.AppendLine($"{indent}[{hWnd.ToInt64():X}] \"{name}\" TRANSPARENT={transparent} NOACTIVATE={noActivate}");
                AppendWindowTree(hWnd, sb, indent + "  ");
                return true;
            }, IntPtr.Zero);
        }

        internal void ApplyOpacity(double opacity)
        {
            opacity = Math.Clamp(opacity, 0.2, 1.0);
            Opacity = opacity;

            // WebView2 は HwndHost であり WPF の Opacity 影響を受けないため、
            // Win32 API でネイティブHWNDの不透明度を別途設定する。
            try
            {
                IntPtr mainHwnd = new WindowInteropHelper(this).Handle;
                IntPtr webHwnd = FindWindowEx(mainHwnd, IntPtr.Zero, "Chrome_WidgetWin_1", null);
                if (webHwnd != IntPtr.Zero)
                {
                    byte alpha = (byte)(255 * opacity);
                    SetLayeredWindowAttributes(webHwnd, 0, alpha, LWA_ALPHA);
                }
            }
            catch
            {
                // WebView2 がまだ初期化されていない場合等に無視
            }
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint crKey, byte bAlpha, uint dwFlags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string? lpszClass, string? lpszWindow);

        private const uint LWA_ALPHA = 0x2;

        private void NavigateToUrl(string url)
        {
            try
            {
                if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
                {
                    WebView.Source = uri;
                    LogDebug($"Navigate: {url}");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"URL の読み込みに失敗しました。\n{ex.Message}",
                    "読み込みエラー", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>設定ディレクトリにデバッグログを書き出す。</summary>
        private static void LogDebug(string message)
        {
            try
            {
                Directory.CreateDirectory(SettingsDirectory);
                File.AppendAllText(
                    Path.Combine(SettingsDirectory, "debug.log"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
            }
            catch
            {
                // ログ書き込み失敗は無視
            }
        }

        private AppSettings? LoadSettings()
        {
            try
            {
                if (!File.Exists(SettingsPath)) return null;
                var json = File.ReadAllText(SettingsPath);
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var settings = JsonSerializer.Deserialize<AppSettings>(json, options);
                if (settings is null) return null;
                settings.Opacity = Math.Clamp(settings.Opacity, 0.2, 1.0);
                return settings;
            }
            catch (Exception ex)
            {
                LogDebug($"LoadSettingsエラー: {ex}");
                return null;
            }
        }

        private void SaveSettings(string url, double opacity)
        {
            var settings = new AppSettings { Url = url, Opacity = Math.Clamp(opacity, 0.2, 1.0) };
            try
            {
                Directory.CreateDirectory(SettingsDirectory);
                File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                MessageBox.Show($"設定の保存に失敗しました。\n{ex.Message}", "保存エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private const string PlaceholderHtml = """
            <!DOCTYPE html>
            <html lang="ja"><head><meta charset="utf-8">
            <style>html,body{margin:0;height:100%}body{display:flex;align-items:center;justify-content:center;background:#1E1E2E;color:#B0B0C0;font-family:"Segoe UI","Yu Gothic UI",sans-serif;font-size:15px;text-align:center;line-height:2}</style>
            </head><body><div>接続先URLが設定されていません<br/>右上のメニュー（≡）ボタンから設定してください</div></body></html>
            """;

        public sealed class AppSettings
        {
            public string Url { get; set; } = string.Empty;
            public double Opacity { get; set; } = 1.0;
        }
    }
}
