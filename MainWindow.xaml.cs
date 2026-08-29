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

        // リサイズ（エッジドラッグ）状態
        private const double ResizeBand = 8;      // エッジのリサイズ帯幅（DIP）
        private const double ResizeMinWidth = 400;   // リサイズ時の最小幅
        private const double ResizeMinHeight = 300;  // リサイズ時の最小高さ
        private bool _isResizing;
        private int _resizeEdge;                  // ResizeEdge ビットマスク
        private Point _resizeStartScreenMouse;
        private Rect _resizeStartBounds;          // ドラッグ開始時の Left/Top/Width/Height

        [Flags]
        private enum ResizeEdge { None = 0, Left = 1, Right = 2, Top = 4, Bottom = 8 }

        // Webモード用リサイズ帯ウィンドウ（上/下/左/右）。Webモードではフルオーバーレイを非表示にし、
        // 外側 ResizeBand px の帯だけを表示してリサイズを受け取る（中心は WebView2 に直接届く）。
        private Window[] _resizeBands = null!;

        public MainWindow()
        {
            InitializeComponent();

            // CenterScreen は起動時アクティブモニター基準になるため、上段モニターで起動した
            // 際に上段モニター中央に表示されてしまい見えなくなることがある。
            // プライマリモニター（タスクバー側）の作業領域中央に明示的に配置する。
            var workArea = SystemParameters.WorkArea;
            Left = workArea.Left + (workArea.Width - Width) / 2;
            Top = workArea.Top + (workArea.Height - Height) / 2;

            _normalBounds = new Rect(Left, Top, Width, Height);

            _controlBar = CreateControlBarWindow();
            _overlay = CreateOverlayWindow();
            _resizeBands = CreateResizeBands();

            // メインウィンドウの WM_NCHITTEST をフック。ウィンドウモードでは HTTRANSPARENT を返し、
            // WebView2 のネイティブ子HWND（Chrome_WidgetWin_* 等、別プロセスのウィンドウ）を含めて
            // メインウィンドウ全体を OS レベルでクリックスルーにする。
            // HwndSource はウィンドウハンドル生成後（SourceInitialized）で取得する。
            SourceInitialized += OnSourceInitialized;

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
                    foreach (var b in _resizeBands) b.Hide();
                    _controlBar.Hide();
                    _webModeButtonWindow?.Hide();
                }
                else if (_isWebMode)
                {
                    // 最小化復帰時: リサイズ帯と復帰ボタンを再表示・再配置する
                    ShowResizeBands();
                    ShowWebModeButtonWindow();
                }
                else
                {
                    ShowOverlay();
                }
            };
        }

        /// <summary>
        /// HwndSource が生成された直後に WM_NCHITTEST フックを設置する。
        /// （SourceInitialized は WPF の標準的なハンドルの取得タイミング）
        /// </summary>
        private void OnSourceInitialized(object? sender, EventArgs e)
        {
            _mainHwndSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            LogDebug($"SourceInitialized: HwndSource=({_mainHwndSource != null}), MainHwnd=0x{new WindowInteropHelper(this).Handle.ToInt64():X}");
            _mainHwndSource?.AddHook(OnMainWindowHook);
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

        /// <summary>フローティングコントロールバー（独立ウィンドウ）を表示する。ホバー時のレベルトリガーとして頻繁に呼ばれるため冪等である。</summary>
        private void ShowControlBar()
        {
            _hideTimer?.Stop();
            if (_controlBar.Visibility != Visibility.Visible)
            {
                LogDebug($"ShowControlBar: hidden bar -> show (opacity={_controlBar.Opacity:F2})");
                _controlBar.Show();
                PositionControlBar();
            }
            // 常に最前面へ再固定する。ドラッグ中に HWND がオーバーレイの下に沈むことがあるため、
            // 表示遷移時のみでは不十分（SetWindowPos(HWND_TOP) は低コスト）。
            BringToFront(new WindowInteropHelper(_controlBar).EnsureHandle());
            if (_controlBar.Opacity < 0.5)
            {
                // 現在の opacity から開始することで、MouseMove からの繰り返し呼び出しでフェードがリセットされない
                var anim = new DoubleAnimation(_controlBar.Opacity, 1.0, new Duration(TimeSpan.FromMilliseconds(250)))
                { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
                _controlBar.BeginAnimation(OpacityProperty, anim);
            }
        }

        /// <summary>フローティングコントロールバーを隠す（ディレイ付き）。完全にフェードアウトしたらウィンドウ自体も非表示にする。</summary>
        private void HideControlBar()
        {
            // リサイズ中はエッジ（右上隅付近）からドラッグするため、バーが自動非表示にならないよう無視する
            if (_isResizing) return;
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
                        if (_controlBar.Opacity < 0.5)
                        {
                            LogDebug("control bar -> Hide() (fade-out complete)");
                            _controlBar.Hide();
                        }
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
                // Web操作モード: WebView2 が直接入力を受け取る。復帰ボタン（独立ミニウィンドウ）を表示。
                // フルオーバーレイは非表示にし、外側8pxのリサイズ帯ウィンドウ4本だけを表示して
                // エッジドラッグリサイズを可能にする（中心のクリックは WebView2 に直接届く）。
                _overlay.Hide();
                ShowResizeBands();
                _hideTimer?.Stop();
                _controlBar.Hide();
                ShowWebModeButtonWindow();
                SetMainHitTestTransparent(false); // メインウィンドウをヒットテスト可能に（Web が操作可能に）
                PinWebModeButtonAboveMain(); // 初期状態から「ボタン > リサイズ帯 > メイン」を保証する
            }
            else
            {
                // ウィンドウ操作モード: オーバーレイを表示しマウス入力をすべてキャプチャ（Webページは操作不可）。
                // 復帰ボタン・リサイズ帯を非表示。コントロールバーはホバー時に表示される（オーバーレイの MouseEnter）。
                ShowOverlay();
                foreach (var b in _resizeBands) b.Hide();
                HideWebModeButtonWindow();
                StopWebModePinTimer();
                // Webモードから戻った直後は、操作した場所の近く（右上）にコントロールバーを即表示する
                if (_wasWebMode) ShowControlBar();
                SetMainHitTestTransparent(true); // メインウィンドウを OS レベルでヒットテスト不能に（Web 入力を遮断）
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
            // WPF のAllowsTransparencyウィンドウで alpha=0（Brushes.Transparent）だと、
            // レイヤーウィンドウのヒットテストで全ピクセルが HTTRANSPARENT 扱いになり
            // マウスイベントが受信できない。alpha=1（事実上透明）にすることで
            // OS へのヒットテストは HTCLIENT になり、マウス入力を受信できる。
            var hitTestBrush = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
            var overlay = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = hitTestBrush,
                Topmost = true,
                ShowInTaskbar = false,
                ResizeMode = ResizeMode.NoResize,
            };
            // 全領域を alpha=1 の Border で覆い、WPF のヒットテストを確実に行わせる
            overlay.Content = new Border { Background = hitTestBrush };
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
            bar.MouseEnter += (s, e) => { LogDebug("control bar MouseEnter"); _hideTimer?.Stop(); };
            bar.MouseLeave += (s, e) => { LogDebug("control bar MouseLeave -> HideControlBar"); HideControlBar(); };
            return bar;
        }

        private void Overlay_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left || e.ButtonState != MouseButtonState.Pressed) return;
            GetCursorPos(out POINT p);
            var pos = new Point(p.X, p.Y);

            // エッジ帯内ならリサイズドラッグ、それ以外なら移動ドラッグ
            var edge = GetResizeEdge(e.GetPosition(_overlay));
            if (edge != ResizeEdge.None)
            {
                _isResizing = true;
                _resizeEdge = (int)edge;
                _resizeStartScreenMouse = pos;
                _resizeStartBounds = new Rect(Left, Top, Width, Height);
                // 最大化状態からのリサイズはまず通常サイズへ復元してから開始する
                if (_isMaximized)
                {
                    Left = _normalBounds.Left;
                    Top = _normalBounds.Top;
                    Width = _normalBounds.Width;
                    Height = _normalBounds.Height;
                    _resizeStartBounds = new Rect(Left, Top, Width, Height);
                    _isMaximized = false;
                    _controlBar.SetMaximizeIcon(false);
                }
                LogDebug($"overlay MouseDown (resize start) edge={edge} bounds=({_resizeStartBounds.Left},{_resizeStartBounds.Top},{_resizeStartBounds.Width:F0}x{_resizeStartBounds.Height:F0})");
            }
            else
            {
                _isDragging = true;
                _dragStartScreenMouse = pos;
                _dragStartPos = new Point(Left, Top);
                LogDebug($"overlay MouseDown (drag start) main=({Left},{Top})");
            }
            _overlay.CaptureMouse();
        }

        private void Overlay_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isResizing)
            {
                var d = GetResizeDeltaDip();
                ApplyResize(d.dx, d.dy);
                return;
            }
            if (!_isDragging)
            {
                // エッジ帯の上ならリサイズカーソル（縦/横/コーナー）を表示する
                _overlay.Cursor = GetResizeCursor(GetResizeEdge(e.GetPosition(_overlay)));
                // レベルトリガー: カーソルがウィンドウ内で動いている限りバーを表示する。
                // ドラッグ終了後に WPF の enter/leave 追跡が停止しても（エッジイベントが発火しなくても）
                // カーソルの移動だけでバーが再表示・再固定される。
                if (!_isWebMode) ShowControlBar();
                return;
            }
            GetCursorPos(out POINT cur);
            var src = (HwndSource)PresentationSource.FromVisual(this);
            double scaleX = src?.CompositionTarget.TransformToDevice.M11 ?? 1.0;
            double scaleY = src?.CompositionTarget.TransformToDevice.M22 ?? 1.0;
            Left = _dragStartPos.X + (cur.X - _dragStartScreenMouse.X) / scaleX;
            Top = _dragStartPos.Y + (cur.Y - _dragStartScreenMouse.Y) / scaleY;
            // オーバーレイとコントロールバーをメインウィンドウに追従させる
            _overlay.Left = Left;
            _overlay.Top = Top;
            PositionControlBar();
            PositionWebModeButtonWindow();
        }

        private void Overlay_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_isResizing)
            {
                LogDebug($"overlay MouseUp (resize end) bounds=({Left},{Top},{Width:F0}x{Height:F0})");
            }
            _isDragging = false;
            _isResizing = false;
            _resizeEdge = 0;
            _overlay.ReleaseMouseCapture();
            // ドラッグ（移動/リサイズ）終了後に WPF のホバー追跡（enter/leave）が停止して、
            // 次回入場時に MouseEnter/MouseMove が発火しないことがある。原因は、ドラッグ中に
            // カーソルがオーバーレイ以外のウィンドウ上を通過すると WM_MOUSEMOVE が届かず、
            // WPF のホバー状態が実カーソル位置と乖離するため。カーソルを一瞬外へ出して再入場させ、
            // 確実な enter/leave を発生させてホバー状態をリシンクする（1フレーム内の往復で視覚影響は最小）。
            GetCursorPos(out POINT up);
            var src = (HwndSource)PresentationSource.FromVisual(_overlay);
            double sx = src?.CompositionTarget.TransformToDevice.M11 ?? 1.0;
            double sy = src?.CompositionTarget.TransformToDevice.M22 ?? 1.0;
            double mx = up.X / sx, my = up.Y / sy;
            bool inside = (mx >= Left && mx <= Left + Width && my >= Top && my <= Top + Height);
            if (inside)
            {
                // 内側で終了: 近接エッジの外へ一瞬出す（16px）
                double dTop = my - Top, dBottom = Top + Height - my;
                double dLeft = mx - Left, dRight = Left + Width - mx;
                double minD = Math.Min(Math.Min(dTop, dBottom), Math.Min(dLeft, dRight));
                int outX = up.X, outY = up.Y;
                if (minD == dTop) outY = (int)Math.Round(Top - 16 * sy);
                else if (minD == dBottom) outY = (int)Math.Round(Top + Height + 16 * sy);
                else if (minD == dLeft) outX = (int)Math.Round(Left - 16 * sx);
                else outX = (int)Math.Round(Left + Width + 16 * sx);
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    SetCursorPos(outX, outY);
                    SetCursorPos(up.X, up.Y);
                }), System.Windows.Threading.DispatcherPriority.Input);
            }
            else
            {
                // 外側で終了: 内側へ一瞬戻す（角から24px内側）
                int insideX, insideY;
                if (my < Top) { insideX = (int)Math.Round(Left + 24 * sx); insideY = (int)Math.Round(Top + 24 * sy); }
                else if (my > Top + Height) { insideX = (int)Math.Round(Left + 24 * sx); insideY = (int)Math.Round(Top + Height - 24 * sy); }
                else if (mx < Left) { insideX = (int)Math.Round(Left + 24 * sx); insideY = up.Y; }
                else { insideX = (int)Math.Round(Left + Width - 24 * sx); insideY = up.Y; }
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    SetCursorPos(insideX, insideY);
                    SetCursorPos(up.X, up.Y);
                }), System.Windows.Threading.DispatcherPriority.Input);
            }
        }

        /// <summary>ウィンドウ内座標（DIP）からリサイズエッジを判定する。帯幅は ResizeBand。</summary>
        private ResizeEdge GetResizeEdge(Point p)
        {
            var edge = ResizeEdge.None;
            if (p.X <= ResizeBand) edge |= ResizeEdge.Left;
            else if (p.X >= Width - ResizeBand) edge |= ResizeEdge.Right;
            if (p.Y <= ResizeBand) edge |= ResizeEdge.Top;
            else if (p.Y >= Height - ResizeBand) edge |= ResizeEdge.Bottom;
            return edge;
        }

        /// <summary>リサイズエッジに対応するカーソルを返す（コーナーは斜め両方向）。</summary>
        private static Cursor GetResizeCursor(ResizeEdge edge)
        {
            bool l = (edge & ResizeEdge.Left) != 0, r = (edge & ResizeEdge.Right) != 0;
            bool t = (edge & ResizeEdge.Top) != 0, b = (edge & ResizeEdge.Bottom) != 0;
            if ((l && t) || (r && b)) return Cursors.SizeNWSE;
            if ((r && t) || (l && b)) return Cursors.SizeNESW;
            if (l || r) return Cursors.SizeWE;
            if (t || b) return Cursors.SizeNS;
            return Cursors.Arrow;
        }

        /// <summary>リサイズドラッグ中、カーソルの画面移動量（DIP）からメインウィンドウの Left/Top/Width/Height を更新する。</summary>
        private void ApplyResize(double dx, double dy)
        {
            double left = _resizeStartBounds.Left, top = _resizeStartBounds.Top;
            double width = _resizeStartBounds.Width, height = _resizeStartBounds.Height;
            if ((_resizeEdge & (int)ResizeEdge.Left) != 0)
            {
                width = Math.Max(ResizeMinWidth, _resizeStartBounds.Width - dx);
                left = _resizeStartBounds.Right - width;
            }
            if ((_resizeEdge & (int)ResizeEdge.Right) != 0)
            {
                width = Math.Max(ResizeMinWidth, _resizeStartBounds.Width + dx);
            }
            if ((_resizeEdge & (int)ResizeEdge.Top) != 0)
            {
                height = Math.Max(ResizeMinHeight, _resizeStartBounds.Height - dy);
                top = _resizeStartBounds.Bottom - height;
            }
            if ((_resizeEdge & (int)ResizeEdge.Bottom) != 0)
            {
                height = Math.Max(ResizeMinHeight, _resizeStartBounds.Height + dy);
            }

            Left = left;
            Top = top;
            Width = width;
            Height = height;
            // Webモードではリサイズ帯ウィンドウ4本を新しい矩形に同期する
            // （ウィンドウモードでは SizeChanged -> UpdateFloatingWindows が追従する）
            if (_isWebMode)
            {
                PositionResizeBands();
                PositionWebModeButtonWindow();
            }
        }

        /// <summary>
        /// ドラッグ開始点からのカーソル画面移動量（DIP）を返す。
        /// ウィンドウ/帯の座標基準にすると、左・上エッジドラッグでウィンドウ自体が動くため
        /// delta が半分になってしまうので、必ず画面座標基準を使う。
        /// </summary>
        private (double dx, double dy) GetResizeDeltaDip()
        {
            GetCursorPos(out POINT cur);
            var src = (HwndSource)PresentationSource.FromVisual(this);
            double sx = src?.CompositionTarget.TransformToDevice.M11 ?? 1.0;
            double sy = src?.CompositionTarget.TransformToDevice.M22 ?? 1.0;
            return ((cur.X - _resizeStartScreenMouse.X) / sx, (cur.Y - _resizeStartScreenMouse.Y) / sy);
        }

        private void ShowOverlay()
        {
            _overlay.Show();
            PinOverlayBelowMain();
            UpdateFloatingWindows();
            // キーボード入力をメインウィンドウに向かい、Web ページへキーが送られないようにする
            SetFocus(new WindowInteropHelper(this).Handle);
            LogOverlayZOrder();
        }

        /// <summary>オーバーレイとメインウィンドウの HWND・拡張スタイル・矩形・トップレベル Z-order を診断ログに出力する。</summary>
        private void LogOverlayZOrder()
        {
            try
            {
                IntPtr oh = new WindowInteropHelper(_overlay).EnsureHandle();
                IntPtr mh = new WindowInteropHelper(this).EnsureHandle();
                if (oh == IntPtr.Zero || mh == IntPtr.Zero)
                {
                    LogDebug($"Z-order: overlayHwnd={oh} mainHwnd={mh} (null が存在)");
                    return;
                }
                RECT or, mr;
                GetWindowRect(oh, out or);
                GetWindowRect(mh, out mr);
                LogDebug($"Z-order: overlay=0x{oh.ToInt64():X} ex=0x{GetWindowLong(oh, GWL_EXSTYLE):X} rect=({or.Left},{or.Top})-({or.Right},{or.Bottom}) visible={_overlay.Visibility}");
                LogDebug($"Z-order: main=0x{mh.ToInt64():X} ex=0x{GetWindowLong(mh, GWL_EXSTYLE):X} rect=({mr.Left},{mr.Top})-({mr.Right},{mr.Bottom})");
                IntPtr h = GetTopWindow(IntPtr.Zero);
                int i = 0;
                while (h != IntPtr.Zero && i < 50)
                {
                    if (h == mh)
                    {
                        LogDebug($"Z-order: main は top-level #{i}");
                        break;
                    }
                    if (h == oh)
                    {
                        LogDebug($"Z-order: overlay は top-level #{i}");
                    }
                    h = GetWindow(h, GW_HWNDNEXT);
                    i++;
                }
            }
            catch (Exception ex)
            {
                LogDebug("Z-order 診断失敗: " + ex.Message);
            }
        }

        /// <summary>
        /// Webモード用リサイズ帯ウィンドウ4本（上/下/左/右）を生成する。
        /// 幅または高さが ResizeBand px の薄い Topmost ウィンドウで、見た目は完全に透明。
        /// Webモードではこれらだけがマウス入力を受け取り、中心は WebView2 に直接届く。
        /// </summary>
        private Window[] CreateResizeBands()
        {
            var hitTestBrush = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
            ResizeEdge[] edges = { ResizeEdge.Top, ResizeEdge.Bottom, ResizeEdge.Left, ResizeEdge.Right };
            var bands = new Window[4];
            for (int i = 0; i < 4; i++)
            {
                ResizeEdge edge = edges[i];
                var band = new Window
                {
                    WindowStyle = WindowStyle.None,
                    AllowsTransparency = true,
                    Background = hitTestBrush,
                    Content = new Border { Background = hitTestBrush },
                    Topmost = true,
                    ShowInTaskbar = false,
                    ResizeMode = ResizeMode.NoResize,
                    Cursor = (edge == ResizeEdge.Left || edge == ResizeEdge.Right) ? Cursors.SizeWE : Cursors.SizeNS,
                };
                Window captured = band; // クロージャで対象ウィンドウを保持（キャプチャ中は sender が変わっても安全）
                band.Loaded += (s, e) =>
                {
                    IntPtr h = new WindowInteropHelper(band).EnsureHandle();
                    int ex = GetWindowLong(h, GWL_EXSTYLE);
                    SetWindowLong(h, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE);
                };
                band.MouseDown += (s, e) => Band_MouseDown(captured, e);
                band.MouseMove += (s, e) => { UpdateBandCursor(captured, edge); Band_MouseMove(); };
                band.MouseUp += (s, e) => Band_MouseUp(captured, e);
                bands[i] = band;
            }
            return bands;
        }

        /// <summary>リサイズ帯を表示し、復帰ボタンの直下（= メインの直上）に Z-order 固定する。</summary>
        private void ShowResizeBands()
        {
            // 必ず Show() より前に矩形を設定する。WPF は Show() 時点のサイズでウィンドウを生成するため、
            // 未設定（0x0）のまま表示するとヒットテスト対象にならずリサイズ不能になる。
            PositionResizeBands();
            foreach (var b in _resizeBands)
            {
                if (!b.IsVisible) b.Show();
            }
            IntPtr bh = _webModeButtonWindow is not null
                ? new WindowInteropHelper(_webModeButtonWindow).EnsureHandle()
                : new WindowInteropHelper(this).EnsureHandle();
            foreach (var b in _resizeBands)
            {
                SetWindowPos(new WindowInteropHelper(b).EnsureHandle(), bh,
                    0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            }
            LogResizeBands();
        }

        /// <summary>リサイズ帯の HWND・矩形を診断ログに出力する。</summary>
        private void LogResizeBands()
        {
            try
            {
                string[] names = { "top", "bottom", "left", "right" };
                for (int i = 0; i < _resizeBands.Length; i++)
                {
                    var b = _resizeBands[i];
                    IntPtr h = new WindowInteropHelper(b).EnsureHandle();
                    RECT r;
                    GetWindowRect(h, out r);
                    LogDebug($"[band] {names[i]}=0x{h.ToInt64():X} visible={b.IsVisible} rect=({r.Left},{r.Top})-({r.Right},{r.Bottom})");
                }
            }
            catch (Exception ex)
            {
                LogDebug("[band] 診断失敗: " + ex.Message);
            }
        }

        /// <summary>リサイズ帯4本をメインウィンドウの各辺に沿って配置する（Webモード時のみ有効）。</summary>
        private void PositionResizeBands()
        {
            if (!_isWebMode) return;
            double w = Width, h = Height;
            _resizeBands[0].Width = w; _resizeBands[0].Height = ResizeBand; _resizeBands[0].Left = Left; _resizeBands[0].Top = Top;                     // 上
            _resizeBands[1].Width = w; _resizeBands[1].Height = ResizeBand; _resizeBands[1].Left = Left; _resizeBands[1].Top = Top + h - ResizeBand;    // 下
            _resizeBands[2].Width = ResizeBand; _resizeBands[2].Height = h; _resizeBands[2].Left = Left; _resizeBands[2].Top = Top;                     // 左
            _resizeBands[3].Width = ResizeBand; _resizeBands[3].Height = h; _resizeBands[3].Left = Left + w - ResizeBand; _resizeBands[3].Top = Top;    // 右
        }

        /// <summary>帯からのリサイズドラッグ開始。エッジはメインウィンドウに対するカーソル位置で判定する（コーナーで幅・高の両次元に対応）。</summary>
        private void Band_MouseDown(Window band, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left || e.ButtonState != MouseButtonState.Pressed) return;
            GetCursorPos(out POINT p);
            var src = (HwndSource)PresentationSource.FromVisual(this);
            double sx = src?.CompositionTarget.TransformToDevice.M11 ?? 1.0;
            double sy = src?.CompositionTarget.TransformToDevice.M22 ?? 1.0;
            var edge = GetResizeEdge(new Point(p.X / sx - Left, p.Y / sy - Top));
            _isResizing = true;
            _resizeEdge = (int)edge;
            _resizeStartScreenMouse = new Point(p.X, p.Y);
            _resizeStartBounds = new Rect(Left, Top, Width, Height);
            // 最大化状態からのリサイズはまず通常サイズへ復元してから開始する
            if (_isMaximized)
            {
                Left = _normalBounds.Left;
                Top = _normalBounds.Top;
                Width = _normalBounds.Width;
                Height = _normalBounds.Height;
                _resizeStartBounds = new Rect(Left, Top, Width, Height);
                _isMaximized = false;
                _controlBar.SetMaximizeIcon(false);
            }
            LogDebug($"band MouseDown (resize start) edge={edge} bounds=({_resizeStartBounds.Left},{_resizeStartBounds.Top},{_resizeStartBounds.Width:F0}x{_resizeStartBounds.Height:F0})");
            band.CaptureMouse();
        }

        /// <summary>リサイズ帯の上でコーナー領域（角 16px）にいると斜めカーソルを表示する。</summary>
        private void UpdateBandCursor(Window band, ResizeEdge captured)
        {
            if (_isResizing) return;
            // Screen 座標でコーナー判定する
            GetCursorPos(out POINT p);
            var src = (HwndSource)PresentationSource.FromVisual(this);
            double sx = src?.CompositionTarget.TransformToDevice.M11 ?? 1.0;
            double sy = src?.CompositionTarget.TransformToDevice.M22 ?? 1.0;
            double mx = p.X / sx, my = p.Y / sy;
            const double corner = 16;
            bool l = (captured & ResizeEdge.Left) != 0, r = (captured & ResizeEdge.Right) != 0;
            bool t = (captured & ResizeEdge.Top) != 0, b = (captured & ResizeEdge.Bottom) != 0;
            bool atCorner = false;
            if (l && t) atCorner = mx <= Left + corner && my <= Top + corner;
            else if (r && t) atCorner = mx >= Left + Width - corner && my <= Top + corner;
            else if (l && b) atCorner = mx <= Left + corner && my >= Top + Height - corner;
            else if (r && b) atCorner = mx >= Left + Width - corner && my >= Top + Height - corner;
            var cursor = atCorner ? ((l && t || r && b) ? Cursors.SizeNWSE : Cursors.SizeNESW)
                                  : (l || r ? Cursors.SizeWE : Cursors.SizeNS);
            band.Cursor = cursor;
            if (band.Content is FrameworkElement fe) fe.Cursor = cursor; // 子要素の既定カーソルを上書き
        }

        private void Band_MouseMove()
        {
            if (!_isResizing) return;
            var d = GetResizeDeltaDip();
            ApplyResize(d.dx, d.dy);
        }

        private void Band_MouseUp(Window band, MouseButtonEventArgs e)
        {
            LogDebug($"band MouseUp (resize end) bounds=({Left},{Top},{Width:F0}x{Height:F0})");
            _isResizing = false;
            _resizeEdge = 0;
            band.ReleaseMouseCapture();
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out POINT pt);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetCursorPos(int X, int Y);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern IntPtr GetTopWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

        private const uint GW_HWNDNEXT = 2;
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
            if (msg != WM_NCHITTEST) return IntPtr.Zero;
            if (_hitTestLogCount < 10)
            {
                _hitTestLogCount++;
                LogDebug($"WM_NCHITTEST hwnd=0x{hwnd.ToInt64():X} hit={wParam.ToInt32()} webMode={_isWebMode} (#{_hitTestLogCount})");
            }
            // ウィンドウモード: クライアント領域のヒットコード（HTCLIENT 等）はすべて HTTRANSPARENT とし、
            // メインウィンドウ配下の WebView2 ネイティブHWND 全体を入力透過にする。
            // Webモード: デフォルト処理を返し、Webページが通常通り入力を受け取る。
            if (!_isWebMode)
            {
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

        private const uint SWP_FRAMECHANGED = 0x0020;
        private const uint SWP_NOZORDER = 0x0004;

        /// <summary>
        /// メインウィンドウ自体を OS レベルでマウスヒットテスト不能にする（ウィンドウモード時）。
        /// 単なる WM_NCHITTEST=HTTRANSPARENT では、レイヤーウィンドウのレイヤー bitmap が不透明な
        /// 領域では OS が WM_NCHITTEST を送らず WebView2 のネイティブ子HWNDに入力が渡る場合があるため、
        /// WS_EX_TRANSPARENT を付与してウィンドウ全体（子HWND含む）をヒットテストから除外する。
        /// 透過した入力は Z-order 直下の入力遮断オーバーレイに届く。
        /// </summary>
        private void SetMainHitTestTransparent(bool transparent)
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
                if (transparent) ex |= WS_EX_TRANSPARENT;
                else ex &= ~WS_EX_TRANSPARENT;
                SetWindowLong(hwnd, GWL_EXSTYLE, ex);
                // スタイル変更を即座に適用（位置・サイズ・Z-order は維持）
                SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED | SWP_NOACTIVATE);
                LogDebug($"Main WS_EX_TRANSPARENT = {transparent} (ex=0x{ex:X})");
            }
            catch (Exception ex)
            {
                LogDebug("SetMainHitTestTransparent error: " + ex.Message);
            }
        }

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
            PositionResizeBands();
        }

        /// <summary>浮遊コントロールバーを右上に配置する。Webモードの復帰ボタンと同じアンカー（右端56px・上8px）で揃える。</summary>
        private void PositionControlBar()
        {
            if (_controlBar is not { Visibility: Visibility.Visible }) return;
            _controlBar.Left = Left + Width - _controlBar.Width - 56;
            _controlBar.Top = Top + 8;
        }

        /// <summary>
        /// Webモード用復帰ボタンのミニウィンドウ。
        /// WebView2 のネイティブHWND は WPF レンダリング面の上に存在するため、
        /// WPF 要素として復帰ボタンを描いてもクリックできない。独立した Topmost ウィンドウとすることで
        /// 常にネイティブHWND より手前に表示される。
        /// </summary>
        private Window? _webModeButtonWindow;
        private DispatcherTimer? _webModePinTimer;

        private void ShowWebModeButtonWindow()
        {
            if (_webModeButtonWindow is null)
            {
                var button = new Button
                {
                    Width = 46,
                    Height = 32,
                    Cursor = Cursors.Hand,
                    ToolTip = "ウィンドウ操作に戻る",
                    Template = CreateReturnButtonTemplate(),
                    Content = new Viewbox
                    {
                        Width = 20,
                        Height = 16,
                        Child = new System.Windows.Shapes.Path
                        {
                            Data = Geometry.Parse("M 6,8 L 26,8 L 26,24 L 6,24 Z M 6,14 L 26,14"),
                            Stroke = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)),
                            StrokeThickness = 1.5,
                            Fill = Brushes.Transparent,
                            SnapsToDevicePixels = true,
                        },
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
                    Width = 50,
                    Height = 32,
                    Content = button,
                };
            }
            _webModeButtonWindow.Left = Left + Width - 56;
            _webModeButtonWindow.Top = Top + 8;
            _webModeButtonWindow.Show();
            _webModeButtonWindow.Activate();
            LogWebModeButtonState("after Show/Activate");
            // WPF の非同期処理（アクティベーションによる Topmost 再アサート等）完了後の状態も記録する
            Dispatcher.BeginInvoke(new Action(() => LogWebModeButtonState("after dispatcher idle")), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            StartWebModePinTimer();
        }

        /// <summary>
        /// Webモード中、復帰ボタンがメインウィンドウより上（前面）にあることを維持する。
        /// メインウィンドウがアクティベートされるたびに WPF が Topmost を再アサートし
        /// topmost 帯内でメインが最前面に上がるため、復帰ボタンが WebView2 に隠れてしまう。
        /// </summary>
        private void StartWebModePinTimer()
        {
            if (_webModePinTimer is null)
            {
                _webModePinTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
                _webModePinTimer.Tick += (_, __) => PinWebModeButtonAboveMain();
            }
            _webModePinTimer.Start();
        }

        private void StopWebModePinTimer()
        {
            _webModePinTimer?.Stop();
        }

        /// <summary>復帰ボタン・リサイズ帯がすべてメインより上（正しいZ-order）にあることを維持する。</summary>
        private void PinWebModeButtonAboveMain()
        {
            if (!_isWebMode) return;
            try
            {
                IntPtr bh = _webModeButtonWindow is not null
                    ? new WindowInteropHelper(_webModeButtonWindow).EnsureHandle()
                    : IntPtr.Zero;
                IntPtr mh = new WindowInteropHelper(this).EnsureHandle();
                // topmost 帯の先頭から走査し、メインより上（前面）にある HWND を集める。
                // 復帰ボタンの位置だけで打ち切ると「ボタン > メイン > リサイズ帯」の違反を見逃すため、
                // メインに当たるまで必ず走査しきる。
                var aboveMain = new HashSet<IntPtr>();
                IntPtr h = GetTopWindow(IntPtr.Zero);
                for (int i = 0; h != IntPtr.Zero && i < 200; i++)
                {
                    if (h == mh) break; // ここより下はメインの裏側
                    aboveMain.Add(h);
                    h = GetWindow(h, GW_HWNDNEXT);
                }
                bool needPin = false;
                if (_webModeButtonWindow is { IsVisible: true } && !aboveMain.Contains(bh)) needPin = true;
                foreach (var b in _resizeBands)
                {
                    if (b.IsVisible && !aboveMain.Contains(new WindowInteropHelper(b).EnsureHandle())) { needPin = true; break; }
                }
                if (!needPin) return;
                LogDebug("[webmode-btn] z-order violation detected -> re-pinning (button>bands>main)");
                // メインを帯の最前へ上げてから、リサイズ帯 > 復帰ボタンの順で前面に再固定する
                SetWindowPos(mh, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE); // HWND_TOP
                foreach (var b in _resizeBands)
                {
                    if (b.IsVisible) BringToFront(new WindowInteropHelper(b).EnsureHandle());
                }
                if (_webModeButtonWindow is { IsVisible: true }) BringToFront(bh);
            }
            catch
            {
                // 診断・再固定の失敗は無視（次回タイマーで再試行）
            }
        }

        /// <summary>復帰ボタンウィンドウの表示状態・矩形・topmost帯内Z-order を診断ログに出力する。</summary>
        private void LogWebModeButtonState(string when)
        {
            try
            {
                if (_webModeButtonWindow is null) return;
                IntPtr bh = new WindowInteropHelper(_webModeButtonWindow).EnsureHandle();
                IntPtr mh = new WindowInteropHelper(this).EnsureHandle();
                RECT br, mr;
                GetWindowRect(bh, out br);
                GetWindowRect(mh, out mr);
                // topmost帯内での相対位置: main の上（前方）にボタンがあるか
                int idx = -1;
                IntPtr h = GetTopWindow(IntPtr.Zero);
                for (int i = 0; h != IntPtr.Zero && i < 200; i++)
                {
                    if (h == bh) { idx = i; break; }
                    if (h == mh) break; // main に先に当たった = ボタンは main より下
                    h = GetWindow(h, GW_HWNDNEXT);
                }
                LogDebug($"[webmode-btn] {when}: btn=0x{bh.ToInt64():X} visible={_webModeButtonWindow.IsVisible} rect=({br.Left},{br.Top})-({br.Right},{br.Bottom}) mainRect=({mr.Left},{mr.Top})-({mr.Right},{mr.Bottom}) zIdx={idx} (mainに先着=-1でボタンがmainの下)");
            }
            catch (Exception ex)
            {
                LogDebug("[webmode-btn] 診断失敗: " + ex.Message);
            }
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
            border.Name = "BtnBorder";
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
            border.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x2E)));
            border.SetValue(Border.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x66)));
            border.SetValue(Border.BorderThicknessProperty, new Thickness(1));

            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(content);

            // ホバー時に背景色を変更する（コントロールバーのホバーと同様の薄い白系）
            var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hoverTrigger.Setters.Add(new Setter(Border.BackgroundProperty,
                new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), "BtnBorder"));

            var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
            template.Triggers.Add(hoverTrigger);
            return template;
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
