using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace DGXSparkUtilWidget;

public partial class MainWindow : Window, IComponentConnector
{
	[Flags]
	private enum ResizeEdge
	{
		None = 0,
		Left = 1,
		Right = 2,
		Top = 4,
		Bottom = 8
	}

	private struct POINT
	{
		public int X;

		public int Y;
	}

	private struct RECT
	{
		public int Left;

		public int Top;

		public int Right;

		public int Bottom;
	}

	private delegate bool EnumChildProc(nint hWnd, nint lParam);

	public sealed class AppSettings
	{
		public string Url { get; set; } = string.Empty;

		public double Opacity { get; set; } = 1.0;

		public WindowBounds? WindowBounds { get; set; }
	}

	public sealed class WindowBounds
	{
		public double Left { get; set; }

		public double Top { get; set; }

		public double Width { get; set; }

		public double Height { get; set; }

		public WindowBounds()
		{
		}

		public WindowBounds(Rect r)
		{
			Left = r.Left;
			Top = r.Top;
			Width = r.Width;
			Height = r.Height;
		}

		public Rect ToRect()
		{
			return new Rect(Left, Top, Width, Height);
		}
	}

	// 単一ファイル公開ビルドでも exe の実配置フォルダを参照（BaseDirectory は単体exeでは一時展開先を指すため不使用）
	private static readonly string SettingsDirectory =
		System.IO.Path.GetDirectoryName(Environment.ProcessPath ?? string.Empty)
		?? System.IO.Directory.GetCurrentDirectory();

	private static readonly string SettingsPath = System.IO.Path.Combine(SettingsDirectory, "DGXSparkUtilWidget.json");

	private static readonly bool _debugMode = Environment.GetCommandLineArgs().Any(a => a.StartsWith("-", StringComparison.OrdinalIgnoreCase) && a.TrimStart('-').Equals("Debug", StringComparison.OrdinalIgnoreCase))
		|| (Environment.ProcessPath?.Contains("\\Debug\\", StringComparison.OrdinalIgnoreCase) ?? false);

	private string _currentUrl = string.Empty;

	private bool _isMaximized;

	private Rect _normalBounds;

	private DispatcherTimer? _hideTimer;

	private DispatcherTimer? _hoverWatchdog;

	private ControlBarWindow _controlBar = null;

	private Window _overlay = null;

	private bool _isDragging;

	private System.Windows.Point _dragStartScreenMouse;

	private System.Windows.Point _dragStartPos;

	private const double ResizeBand = 8.0;

	private const double ResizeMinWidth = 400.0;

	private const double ResizeMinHeight = 300.0;

	private bool _isResizing;

	private int _staleShowCount;

	private bool _hidePending;

	private int _resizeEdge;

	private System.Windows.Point _resizeStartScreenMouse;

	private Rect _resizeStartBounds;

	private Window[] _resizeBands = null;

	private bool _isWebMode;

	private double _pageOpacity = 1.0;

	// WebView2 ホストウィンドウ（Chrome_WidgetWin_1）のHWND。LWA_ALPHA の適用先。
	private nint _webViewHostHwnd;

	// LWA_ALPHA を定期再適用するウォッチドッグタイマー。
	private DispatcherTimer? _alphaWatchdog;

	private bool _wasWebMode;

	private HwndSource? _mainHwndSource;

	private int _hitTestLogCount;

	private const int WM_NCHITTEST = 132;

	private const int HTCLIENT = 1;

	private const int HTTRANSPARENT = -1;

	private const uint GW_HWNDNEXT = 2u;

	private static readonly nint HWND_TOP = IntPtr.Zero;

	private const uint SWP_NOMOVE = 2u;

	private const uint SWP_NOSIZE = 1u;

	private const uint SWP_NOACTIVATE = 16u;

	private const int SM_XVIRTUALSCREEN = 76;

	private const int SM_YVIRTUALSCREEN = 77;

	private const int SM_CXVIRTUALSCREEN = 78;

	private const int SM_CYVIRTUALSCREEN = 79;

	private const uint SWP_FRAMECHANGED = 32u;

	private const uint SWP_NOZORDER = 4u;

	private Window? _webModeButtonWindow;

	private DispatcherTimer? _webModePinTimer;

	private const int GWL_EXSTYLE = -20;

	private const int WS_EX_TRANSPARENT = 32;

	private const int WS_EX_NOACTIVATE = 134217728;

	private const string PlaceholderHtml = "<!DOCTYPE html>\r\n<html lang=\"ja\"><head><meta charset=\"utf-8\">\r\n<style>html,body{margin:0;height:100%}body{display:flex;align-items:center;justify-content:center;background:#1E1E2E;color:#B0B0C0;font-family:\"Segoe UI\",\"Yu Gothic UI\",sans-serif;font-size:15px;text-align:center;line-height:2}</style>\r\n</head><body><div>接続先URLが設定されていません<br/>右上のメニュー（≡）ボタンから設定してください</div></body></html>";


	public MainWindow()
	{
		InitializeComponent();
		RestoreWindowPosition(LoadSettings());
		base.Closing += OnWindowClosing;
		_normalBounds = new Rect(base.Left, base.Top, base.Width, base.Height);
		_controlBar = CreateControlBarWindow();
		_overlay = CreateOverlayWindow();
		_resizeBands = CreateResizeBands();
		base.SourceInitialized += OnSourceInitialized;
		base.LocationChanged += delegate
		{
			UpdateFloatingWindows();
		};
		base.SizeChanged += delegate
		{
			UpdateFloatingWindows();
		};
		base.Activated += delegate
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
		base.StateChanged += delegate
		{
			if (base.WindowState == WindowState.Minimized)
			{
				_overlay.Hide();
				Window[] resizeBands = _resizeBands;
				foreach (Window window in resizeBands)
				{
					window.Hide();
				}
				_controlBar.Hide();
				_webModeButtonWindow?.Hide();
			}
			else if (_isWebMode)
			{
				ShowResizeBands();
				ShowWebModeButtonWindow();
			}
			else
			{
				ShowOverlay();
			}
		};
	}

	private void OnSourceInitialized(object? sender, EventArgs e)
	{
		_mainHwndSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
		LogDebug($"SourceInitialized: HwndSource=({_mainHwndSource != null}), MainHwnd=0x{((IntPtr)new WindowInteropHelper(this).Handle).ToInt64():X}");
		_mainHwndSource?.AddHook(OnMainWindowHook);
	}

	private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
	{
		SetWebMode(webMode: false);
		StartHoverWatchdog();
		try
		{
			await WebView.EnsureCoreWebView2Async();
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			MessageBox.Show("WebView2 の初期化に失敗しました。\nWebView2 Runtime を確認してください。\n\n詳細: " + ex2.Message, "初期化エラー", MessageBoxButton.OK, MessageBoxImage.Hand);
			Close();
			return;
		}
		// WebView2 サーフェス自体をピクセル単位で透明にする（全体透過の前提）。
		// 背後の WPF レイヤーは完全透明なので、Web コンテンツの alpha がそのままデスクトップへの透過になる。
		WebView.DefaultBackgroundColor = System.Drawing.Color.Transparent;
		// 初回ロード時のデフォルトスクロールバー闪烁防止: 初期（非表示）CSS を先読み注入。
		// 以後はモード切替・ナビゲーション時に ApplyScrollbarCss() で上書きされる。
		_ = WebView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(ScrollbarCssScript("::-webkit-scrollbar{display:none !important}"));
		WebView.CoreWebView2.NavigationCompleted += delegate
		{
			LogWindowTree();
			ApplyScrollbarCss();
		};
		// フルナビゲーションでドキュメントが置き換わっても WebView2 ホストHWND は再利用されるため、
		// 初期化後に一度見つかれば以降はそのまま LWA_ALPHA を適用し続ける。
		StartWebViewAlphaFinder();
		AppSettings settings = LoadSettings();
		LogDebug($"起動: 設定読込={settings != null}, Url={settings?.Url}, Opacity={settings?.Opacity}");
		if (settings != null && !string.IsNullOrWhiteSpace(settings.Url))
		{
			_currentUrl = settings.Url;
			ApplyOpacity(settings.Opacity);
			NavigateToUrl(settings.Url);
			return;
		}
		_overlay.Hide();
		try
		{
			SettingsDialog dialog = new SettingsDialog(string.Empty, 1.0)
			{
				Owner = this
			};
			if (dialog.ShowDialog() == true)
			{
				SaveSettings(dialog.Url, dialog.OpacityValue, null);
				_currentUrl = dialog.Url;
				ApplyOpacity(dialog.OpacityValue);
				NavigateToUrl(dialog.Url);
			}
			else
			{
				WebView.CoreWebView2?.NavigateToString("<!DOCTYPE html>\r\n<html lang=\"ja\"><head><meta charset=\"utf-8\">\r\n<style>html,body{margin:0;height:100%}body{display:flex;align-items:center;justify-content:center;background:#1E1E2E;color:#B0B0C0;font-family:\"Segoe UI\",\"Yu Gothic UI\",sans-serif;font-size:15px;text-align:center;line-height:2}</style>\r\n</head><body><div>接続先URLが設定されていません<br/>右上のメニュー（≡）ボタンから設定してください</div></body></html>");
			}
		}
		finally
		{
			if (!_isWebMode)
			{
				ShowOverlay();
			}
		}
	}

	private void RootBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		if (e.ButtonState != MouseButtonState.Pressed)
		{
			return;
		}
		if (e.OriginalSource is UIElement uIElement)
		{
			for (DependencyObject dependencyObject = uIElement; dependencyObject != null; dependencyObject = VisualTreeHelper.GetParent(dependencyObject))
			{
				if ((dependencyObject is Button || dependencyObject is TextBox || dependencyObject is Slider) ? true : false)
				{
					return;
				}
			}
		}
		ShowControlBar();
		try
		{
			DragMove();
		}
		catch (InvalidOperationException)
		{
		}
	}

	private void RootBorder_MouseEnter(object sender, MouseEventArgs e)
	{
		ShowControlBar();
	}

	private void RootBorder_MouseLeave(object sender, MouseEventArgs e)
	{
		HideControlBar();
	}

	private void ShowControlBar()
	{
		if (!_isDragging && !_isResizing)
		{
			GetCursorPos(out var pt);
			HwndSource hwndSource = (HwndSource)PresentationSource.FromVisual(this);
			double num = hwndSource?.CompositionTarget.TransformToDevice.M11 ?? 1.0;
			double num2 = hwndSource?.CompositionTarget.TransformToDevice.M22 ?? 1.0;
			double num3 = (double)pt.X / num;
			double num4 = (double)pt.Y / num2;
			bool flag = num3 >= base.Left && num3 <= base.Left + base.Width && num4 >= base.Top && num4 <= base.Top + base.Height;
			bool flag2 = _controlBar.Visibility == Visibility.Visible && num3 >= _controlBar.Left && num3 <= _controlBar.Left + _controlBar.Width && num4 >= _controlBar.Top && num4 <= _controlBar.Top + _controlBar.Height;
			if (!flag && !flag2)
			{
				_staleShowCount++;
				if (_staleShowCount % 10 == 1)
				{
					LogDebug($"ShowControlBar: cursor outside (x={pt.X},y={pt.Y}) -> skip (stale count={_staleShowCount})");
				}
				return;
			}
		}
		_staleShowCount = 0;
		_hidePending = false;
		_hideTimer?.Stop();
		if (_controlBar.Visibility != 0)
		{
			LogDebug($"ShowControlBar: hidden bar -> show (opacity={_controlBar.Opacity:F2})");
			_controlBar.Show();
			PositionControlBar();
		}
		BringToFront(new WindowInteropHelper(_controlBar).EnsureHandle());
		if (_controlBar.Opacity < 0.5)
		{
			DoubleAnimation animation = new DoubleAnimation(_controlBar.Opacity, 1.0, new Duration(TimeSpan.FromMilliseconds(250L, 0L)))
			{
				EasingFunction = new QuadraticEase
				{
					EasingMode = EasingMode.EaseOut
				}
			};
			_controlBar.BeginAnimation(UIElement.OpacityProperty, animation);
		}
	}

	private void HideControlBar()
	{
		if (_isResizing || _hidePending)
		{
			return;
		}
		_hidePending = true;
		_hideTimer?.Stop();
		_hideTimer = new DispatcherTimer
		{
			Interval = TimeSpan.FromMilliseconds(300L, 0L)
		};
		_hideTimer.Tick += delegate
		{
			_hideTimer.Stop();
			if (_controlBar.Opacity > 0.5)
			{
				DoubleAnimation doubleAnimation = new DoubleAnimation(_controlBar.Opacity, 0.0, new Duration(TimeSpan.FromMilliseconds(350L, 0L)))
				{
					EasingFunction = new QuadraticEase
					{
						EasingMode = EasingMode.EaseOut
					}
				};
				doubleAnimation.Completed += delegate
				{
					if (_controlBar.Opacity < 0.5)
					{
						LogDebug("control bar -> Hide() (fade-out complete)");
						_hidePending = false;
						_controlBar.Hide();
					}
				};
				_controlBar.BeginAnimation(UIElement.OpacityProperty, doubleAnimation);
			}
			else
			{
				LogDebug("control bar -> Hide() (already faded)");
				_hidePending = false;
				_controlBar.Hide();
			}
		};
		_hideTimer.Start();
	}

	private void StartHoverWatchdog()
	{
		if (_hoverWatchdog == null)
		{
			_hoverWatchdog = new DispatcherTimer
			{
				Interval = TimeSpan.FromMilliseconds(250L, 0L)
			};
			_hoverWatchdog.Tick += delegate
			{
				HoverWatchdogTick();
			};
			_hoverWatchdog.Start();
		}
	}

	private void StopHoverWatchdog()
	{
		_hoverWatchdog?.Stop();
	}

	private void HoverWatchdogTick()
	{
		if (_isWebMode || double.IsNaN(base.Left))
		{
			return;
		}
		GetCursorPos(out var pt);
		HwndSource hwndSource = (HwndSource)PresentationSource.FromVisual(this);
		double num = hwndSource?.CompositionTarget.TransformToDevice.M11 ?? 1.0;
		double num2 = hwndSource?.CompositionTarget.TransformToDevice.M22 ?? 1.0;
		double num3 = (double)pt.X / num;
		double num4 = (double)pt.Y / num2;
		if (num3 >= base.Left && num3 <= base.Left + base.Width && num4 >= base.Top && num4 <= base.Top + base.Height)
		{
			if (_controlBar.Visibility != 0)
			{
				LogDebug("hover watchdog: cursor inside but bar hidden -> show");
			}
			ShowControlBar();
		}
		else
		{
			HideControlBar();
		}
	}

	private void BtnMinimize_Click(object sender, RoutedEventArgs e)
	{
		base.WindowState = WindowState.Minimized;
	}

	private void BtnMaximizeRestore_Click(object sender, RoutedEventArgs e)
	{
		if (!_isMaximized)
		{
			_normalBounds = new Rect(base.Left, base.Top, base.Width, base.Height);
			Rect workArea = SystemParameters.WorkArea;
			base.Left = workArea.Left;
			base.Top = workArea.Top;
			base.Width = workArea.Width;
			base.Height = workArea.Height;
			_isMaximized = true;
			_controlBar.SetMaximizeIcon(isMaximized: true);
		}
		else
		{
			base.Left = _normalBounds.Left;
			base.Top = _normalBounds.Top;
			base.Width = _normalBounds.Width;
			base.Height = _normalBounds.Height;
			_isMaximized = false;
			_controlBar.SetMaximizeIcon(isMaximized: false);
		}
	}

	private void BtnClose_Click(object sender, RoutedEventArgs e)
	{
		Application.Current.Shutdown();
	}

	private void BtnMenu_Click(object sender, RoutedEventArgs e)
	{
		_overlay.Hide();
		try
		{
			SettingsDialog settingsDialog = new SettingsDialog(_currentUrl, _pageOpacity)
			{
				Owner = this
			};
			if (settingsDialog.ShowDialog() == true)
			{
				SaveSettings(settingsDialog.Url, settingsDialog.OpacityValue, null);
				_currentUrl = settingsDialog.Url;
				ApplyOpacity(settingsDialog.OpacityValue);
				NavigateToUrl(settingsDialog.Url);
			}
		}
		finally
		{
			if (!_isWebMode)
			{
				ShowOverlay();
			}
		}
	}

	private void BtnWebToggle_Click(object sender, RoutedEventArgs e)
	{
		SetWebMode(webMode: true);
	}

	private void SetWebMode(bool webMode)
	{
		_isWebMode = webMode;
		if (webMode)
		{
			_overlay.Hide();
			ShowResizeBands();
			_hideTimer?.Stop();
			StopHoverWatchdog();
			_controlBar.Hide();
			ShowWebModeButtonWindow();
			SetMainHitTestTransparent(transparent: false);
			PinWebModeButtonAboveMain();
			// layered window (WS_EX_LAYERED) はピクセル alpha=0 で OS hit-test が
			// HTTRANSPARENT となり、WM_NCHITTEST フックで HTCLIENT を返しても
			// クリックが背面ウィンドウに透過する。
			// alpha=1 (視覚的には完全に透明) にすることで透過を防止する。
			RootBorder.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(1, 0, 0, 0));
		}
		else
		{
			ShowOverlay();
			Window[] resizeBands = _resizeBands;
			foreach (Window window in resizeBands)
			{
				window.Hide();
			}
			HideWebModeButtonWindow();
			StopWebModePinTimer();
			StartHoverWatchdog();
			if (_wasWebMode)
			{
				ShowControlBar();
			}
			SetMainHitTestTransparent(transparent: true);
			RootBorder.Background = System.Windows.Media.Brushes.Transparent;
		}
		_wasWebMode = webMode;
		ApplyScrollbarCss();
	}

	// 最上位ドキュメントのスクロールバーCSSを適用する（ウィンドウモード=非表示 / Webモード=カスタム）。
	// WebView2(Chromium)は ::-webkit-scrollbar 擬似要素でスタイリング可能なので、
	// <style id="wscroll"> 要素を注入・上書きする。ページ遷移後は NavigationCompleted で再適用する。
	private void ApplyScrollbarCss()
	{
		if (WebView?.CoreWebView2 == null)
		{
			return;
		}
		string css = _isWebMode
			// 各要素の scrollbar-width/scrollbar-color が auto 以外だと ::-webkit-scrollbar-* が無効化されるため、
			// auto に戻して WebKit スクロールバー擬似要素のスタイリングを必ず有効にする（MDN 記載の互換性ルール）。
			? "*{scrollbar-width:auto !important;scrollbar-color:auto !important}" +
			"::-webkit-scrollbar{width:16px !important;height:16px !important;background:transparent !important}" +
			"::-webkit-scrollbar-corner{background:transparent !important}" +
			// 上端48px（コントロールバー高）・下端4pxの余白はトラックの margin で確保。
				// （headless Edge 検証: track の margin-top/bottom でサムの移動範囲が上下に縮み、
		//   余白が確保される。border 方式はサム位置に影響しないため非採用）
			"::-webkit-scrollbar-track{background:transparent !important;margin-top:48px !important;margin-bottom:4px !important}" +
			// サム: 幅8px・角丸・濃灰。ホバー時は色のみ変えて幅は固定（ホバーで幅が広がる不具合の回避）。
			"::-webkit-scrollbar-thumb{width:8px !important;background:rgba(80,80,80,0.7) !important;border-radius:4px !important}" +
			"::-webkit-scrollbar-thumb:hover{width:8px !important;background:rgba(60,60,60,0.9) !important}"
			: "::-webkit-scrollbar{display:none !important}";
		_ = WebView.CoreWebView2.ExecuteScriptAsync(ScrollbarCssScript(css));
	}

	// wscroll スタイル要素の作成・上書き JS を生成する
	// CSS を JS の single-quoted string として安全に埋め込む（' と \ をエスケープ）
	private static string ScrollbarCssScript(string css)
	{
		string safe = css.Replace("\\", "\\\\").Replace("'", "\\'");
		return "(function(){var s=document.getElementById('wscroll');"
			+ "if(!s){s=document.createElement('style');s.id='wscroll';"
			+ "(document.head||document.documentElement).appendChild(s);}"
			+ "s.textContent='" + safe + "';})();";
	}

	private Window CreateOverlayWindow()
	{
		SolidColorBrush background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(1, 0, 0, 0));
		Window overlay = new Window
		{
			WindowStyle = WindowStyle.None,
			AllowsTransparency = true,
			Background = background,
			Topmost = true,
			ShowInTaskbar = false,
			ResizeMode = ResizeMode.NoResize
		};
		overlay.Content = new Border
		{
			Background = background
		};
		overlay.Loaded += delegate
		{
			nint handle = new WindowInteropHelper(overlay).Handle;
			int windowLong = GetWindowLong(handle, -20);
			SetWindowLong(handle, -20, windowLong | 0x8000000);
		};
		overlay.MouseDown += Overlay_MouseDown;
		overlay.MouseMove += Overlay_MouseMove;
		overlay.MouseUp += Overlay_MouseUp;
		overlay.MouseEnter += delegate
		{
			LogDebug("overlay MouseEnter -> ShowControlBar");
			ShowControlBar();
		};
		overlay.MouseLeave += delegate
		{
			LogDebug("overlay MouseLeave -> HideControlBar");
			HideControlBar();
		};
		return overlay;
	}

	private ControlBarWindow CreateControlBarWindow()
	{
		ControlBarWindow bar = new ControlBarWindow();
		bar.Loaded += delegate
		{
			nint hWnd = new WindowInteropHelper(bar).EnsureHandle();
			int windowLong = GetWindowLong(hWnd, -20);
			SetWindowLong(hWnd, -20, windowLong | 0x8000000);
		};
		bar.BtnMinimize.Click += BtnMinimize_Click;
		bar.BtnMaximizeRestore.Click += BtnMaximizeRestore_Click;
		bar.BtnClose.Click += BtnClose_Click;
		bar.BtnMenu.Click += BtnMenu_Click;
		bar.BtnWebToggle.Click += BtnWebToggle_Click;
		bar.MouseEnter += delegate
		{
			LogDebug("control bar MouseEnter");
			_hideTimer?.Stop();
		};
		bar.MouseLeave += delegate
		{
			LogDebug("control bar MouseLeave -> HideControlBar");
			HideControlBar();
		};
		return bar;
	}

	private void Overlay_MouseDown(object sender, MouseButtonEventArgs e)
	{
		if (e.ChangedButton != 0 || e.ButtonState != MouseButtonState.Pressed)
		{
			return;
		}
		GetCursorPos(out var pt);
		System.Windows.Point point = new System.Windows.Point(pt.X, pt.Y);
		ResizeEdge resizeEdge = GetResizeEdge(e.GetPosition(_overlay));
		if (resizeEdge != 0)
		{
			_isResizing = true;
			_resizeEdge = (int)resizeEdge;
			_resizeStartScreenMouse = point;
			_resizeStartBounds = new Rect(base.Left, base.Top, base.Width, base.Height);
			if (_isMaximized)
			{
				base.Left = _normalBounds.Left;
				base.Top = _normalBounds.Top;
				base.Width = _normalBounds.Width;
				base.Height = _normalBounds.Height;
				_resizeStartBounds = new Rect(base.Left, base.Top, base.Width, base.Height);
				_isMaximized = false;
				_controlBar.SetMaximizeIcon(isMaximized: false);
			}
			LogDebug($"overlay MouseDown (resize start) edge={resizeEdge} bounds=({_resizeStartBounds.Left},{_resizeStartBounds.Top},{_resizeStartBounds.Width:F0}x{_resizeStartBounds.Height:F0})");
		}
		else
		{
			_isDragging = true;
			_dragStartScreenMouse = point;
			_dragStartPos = new System.Windows.Point(base.Left, base.Top);
			LogDebug($"overlay MouseDown (drag start) main=({base.Left},{base.Top})");
		}
		_overlay.CaptureMouse();
	}

	private void Overlay_MouseMove(object sender, MouseEventArgs e)
	{
		if (_isResizing)
		{
			(double, double) resizeDeltaDip = GetResizeDeltaDip();
			ApplyResize(resizeDeltaDip.Item1, resizeDeltaDip.Item2);
			return;
		}
		if (!_isDragging)
		{
			_overlay.Cursor = GetResizeCursor(GetResizeEdge(e.GetPosition(_overlay)));
			if (!_isWebMode)
			{
				ShowControlBar();
			}
			return;
		}
		GetCursorPos(out var pt);
		HwndSource hwndSource = (HwndSource)PresentationSource.FromVisual(this);
		double num = hwndSource?.CompositionTarget.TransformToDevice.M11 ?? 1.0;
		double num2 = hwndSource?.CompositionTarget.TransformToDevice.M22 ?? 1.0;
		base.Left = _dragStartPos.X + ((double)pt.X - _dragStartScreenMouse.X) / num;
		base.Top = _dragStartPos.Y + ((double)pt.Y - _dragStartScreenMouse.Y) / num2;
		_overlay.Left = base.Left;
		_overlay.Top = base.Top;
		PositionControlBar();
		PositionWebModeButtonWindow();
	}

	private void Overlay_MouseUp(object sender, MouseButtonEventArgs e)
	{
		if (_isResizing)
		{
			LogDebug($"overlay MouseUp (resize end) bounds=({base.Left},{base.Top},{base.Width:F0}x{base.Height:F0})");
		}
		_isDragging = false;
		_isResizing = false;
		_resizeEdge = 0;
		_overlay.ReleaseMouseCapture();
		GetCursorPos(out var up);
		HwndSource hwndSource = (HwndSource)PresentationSource.FromVisual(_overlay);
		double num = hwndSource?.CompositionTarget.TransformToDevice.M11 ?? 1.0;
		double num2 = hwndSource?.CompositionTarget.TransformToDevice.M22 ?? 1.0;
		double num3 = (double)up.X / num;
		double num4 = (double)up.Y / num2;
		if (num3 >= base.Left && num3 <= base.Left + base.Width && num4 >= base.Top && num4 <= base.Top + base.Height)
		{
			double num5 = num4 - base.Top;
			double num6 = base.Top + base.Height - num4;
			double num7 = num3 - base.Left;
			double val = base.Left + base.Width - num3;
			double num8 = Math.Min(Math.Min(num5, num6), Math.Min(num7, val));
			int outX = up.X;
			int outY = up.Y;
			if (num8 == num5)
			{
				outY = (int)Math.Round(base.Top - 16.0 * num2);
			}
			else if (num8 == num6)
			{
				outY = (int)Math.Round(base.Top + base.Height + 16.0 * num2);
			}
			else if (num8 == num7)
			{
				outX = (int)Math.Round(base.Left - 16.0 * num);
			}
			else
			{
				outX = (int)Math.Round(base.Left + base.Width + 16.0 * num);
			}
			base.Dispatcher.BeginInvoke((Action)delegate
			{
				SetCursorPos(outX, outY);
				SetCursorPos(up.X, up.Y);
			}, DispatcherPriority.Input);
		}
		else
		{
			int insideX;
			int insideY;
			if (num4 < base.Top)
			{
				insideX = (int)Math.Round(base.Left + 24.0 * num);
				insideY = (int)Math.Round(base.Top + 24.0 * num2);
			}
			else if (num4 > base.Top + base.Height)
			{
				insideX = (int)Math.Round(base.Left + 24.0 * num);
				insideY = (int)Math.Round(base.Top + base.Height - 24.0 * num2);
			}
			else if (num3 < base.Left)
			{
				insideX = (int)Math.Round(base.Left + 24.0 * num);
				insideY = up.Y;
			}
			else
			{
				insideX = (int)Math.Round(base.Left + base.Width - 24.0 * num);
				insideY = up.Y;
			}
			base.Dispatcher.BeginInvoke((Action)delegate
			{
				SetCursorPos(insideX, insideY);
				SetCursorPos(up.X, up.Y);
			}, DispatcherPriority.Input);
		}
	}

	private ResizeEdge GetResizeEdge(System.Windows.Point p)
	{
		ResizeEdge resizeEdge = ResizeEdge.None;
		if (p.X <= 8.0)
		{
			resizeEdge |= ResizeEdge.Left;
		}
		else if (p.X >= base.Width - 8.0)
		{
			resizeEdge |= ResizeEdge.Right;
		}
		if (p.Y <= 8.0)
		{
			resizeEdge |= ResizeEdge.Top;
		}
		else if (p.Y >= base.Height - 8.0)
		{
			resizeEdge |= ResizeEdge.Bottom;
		}
		return resizeEdge;
	}

	private static Cursor GetResizeCursor(ResizeEdge edge)
	{
		bool flag = (edge & ResizeEdge.Left) != 0;
		bool flag2 = (edge & ResizeEdge.Right) != 0;
		bool flag3 = (edge & ResizeEdge.Top) != 0;
		bool flag4 = (edge & ResizeEdge.Bottom) != 0;
		if ((flag && flag3) || (flag2 && flag4))
		{
			return Cursors.SizeNWSE;
		}
		if ((flag2 && flag3) || (flag && flag4))
		{
			return Cursors.SizeNESW;
		}
		if (flag || flag2)
		{
			return Cursors.SizeWE;
		}
		if (flag3 || flag4)
		{
			return Cursors.SizeNS;
		}
		return Cursors.Arrow;
	}

	private void ApplyResize(double dx, double dy)
	{
		double left = _resizeStartBounds.Left;
		double top = _resizeStartBounds.Top;
		double num = _resizeStartBounds.Width;
		double num2 = _resizeStartBounds.Height;
		if ((_resizeEdge & 1) != 0)
		{
			num = Math.Max(400.0, _resizeStartBounds.Width - dx);
			left = _resizeStartBounds.Right - num;
		}
		if ((_resizeEdge & 2) != 0)
		{
			num = Math.Max(400.0, _resizeStartBounds.Width + dx);
		}
		if ((_resizeEdge & 4) != 0)
		{
			num2 = Math.Max(300.0, _resizeStartBounds.Height - dy);
			top = _resizeStartBounds.Bottom - num2;
		}
		if ((_resizeEdge & 8) != 0)
		{
			num2 = Math.Max(300.0, _resizeStartBounds.Height + dy);
		}
		base.Left = left;
		base.Top = top;
		base.Width = num;
		base.Height = num2;
		if (_isWebMode)
		{
			PositionResizeBands();
			PositionWebModeButtonWindow();
		}
	}

	private (double dx, double dy) GetResizeDeltaDip()
	{
		GetCursorPos(out var pt);
		HwndSource hwndSource = (HwndSource)PresentationSource.FromVisual(this);
		double num = hwndSource?.CompositionTarget.TransformToDevice.M11 ?? 1.0;
		double num2 = hwndSource?.CompositionTarget.TransformToDevice.M22 ?? 1.0;
		return (dx: ((double)pt.X - _resizeStartScreenMouse.X) / num, dy: ((double)pt.Y - _resizeStartScreenMouse.Y) / num2);
	}

	private void ShowOverlay()
	{
		_overlay.Show();
		PinOverlayBelowMain();
		UpdateFloatingWindows();
		SetFocus(new WindowInteropHelper(this).Handle);
		LogOverlayZOrder();
	}

	private void LogOverlayZOrder()
	{
		try
		{
			nint num = new WindowInteropHelper(_overlay).EnsureHandle();
			nint num2 = new WindowInteropHelper(this).EnsureHandle();
			if (num == IntPtr.Zero || num2 == IntPtr.Zero)
			{
				LogDebug($"Z-order: overlayHwnd={num} mainHwnd={num2} (null が存在)");
				return;
			}
			GetWindowRect(num, out var lpRect);
			GetWindowRect(num2, out var lpRect2);
			LogDebug($"Z-order: overlay=0x{((IntPtr)num).ToInt64():X} ex=0x{GetWindowLong(num, -20):X} rect=({lpRect.Left},{lpRect.Top})-({lpRect.Right},{lpRect.Bottom}) visible={_overlay.Visibility}");
			LogDebug($"Z-order: main=0x{((IntPtr)num2).ToInt64():X} ex=0x{GetWindowLong(num2, -20):X} rect=({lpRect2.Left},{lpRect2.Top})-({lpRect2.Right},{lpRect2.Bottom})");
			nint num3 = GetTopWindow(IntPtr.Zero);
			int num4 = 0;
			while (num3 != IntPtr.Zero && num4 < 50)
			{
				if (num3 == num2)
				{
					LogDebug($"Z-order: main は top-level #{num4}");
					break;
				}
				if (num3 == num)
				{
					LogDebug($"Z-order: overlay は top-level #{num4}");
				}
				num3 = GetWindow(num3, 2u);
				num4++;
			}
		}
		catch (Exception ex)
		{
			LogDebug("Z-order 診断失敗: " + ex.Message);
		}
	}

	private Window[] CreateResizeBands()
	{
		SolidColorBrush background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(1, 0, 0, 0));
		ResizeEdge[] array = new ResizeEdge[4]
		{
			ResizeEdge.Top,
			ResizeEdge.Bottom,
			ResizeEdge.Left,
			ResizeEdge.Right
		};
		Window[] array2 = new Window[4];
		for (int i = 0; i < 4; i++)
		{
			ResizeEdge edge = array[i];
			Window band = new Window
			{
				WindowStyle = WindowStyle.None,
				AllowsTransparency = true,
				Background = background,
				Content = new Border
				{
					Background = background
				},
				Topmost = true,
				ShowInTaskbar = false,
				ResizeMode = ResizeMode.NoResize,
				Cursor = ((edge == ResizeEdge.Left || edge == ResizeEdge.Right) ? Cursors.SizeWE : Cursors.SizeNS)
			};
			Window captured = band;
			band.Loaded += delegate
			{
				nint hWnd = new WindowInteropHelper(band).EnsureHandle();
				int windowLong = GetWindowLong(hWnd, -20);
				SetWindowLong(hWnd, -20, windowLong | 0x8000000);
			};
			band.MouseDown += delegate(object s, MouseButtonEventArgs e)
			{
				Band_MouseDown(captured, e);
			};
			band.MouseMove += delegate
			{
				UpdateBandCursor(captured, edge);
				Band_MouseMove();
			};
			band.MouseUp += delegate(object s, MouseButtonEventArgs e)
			{
				Band_MouseUp(captured, e);
			};
			array2[i] = band;
		}
		return array2;
	}

	private void ShowResizeBands()
	{
		PositionResizeBands();
		Window[] resizeBands = _resizeBands;
		foreach (Window window in resizeBands)
		{
			if (!window.IsVisible)
			{
				window.Show();
			}
		}
		nint hWndInsertAfter = ((_webModeButtonWindow != null) ? new WindowInteropHelper(_webModeButtonWindow).EnsureHandle() : new WindowInteropHelper(this).EnsureHandle());
		Window[] resizeBands2 = _resizeBands;
		foreach (Window window2 in resizeBands2)
		{
			SetWindowPos(new WindowInteropHelper(window2).EnsureHandle(), hWndInsertAfter, 0, 0, 0, 0, 19u);
		}
		LogResizeBands();
	}

	private void LogResizeBands()
	{
		try
		{
			string[] array = new string[4] { "top", "bottom", "left", "right" };
			for (int i = 0; i < _resizeBands.Length; i++)
			{
				Window window = _resizeBands[i];
				nint hWnd = new WindowInteropHelper(window).EnsureHandle();
				GetWindowRect(hWnd, out var lpRect);
				LogDebug($"[band] {array[i]}=0x{((IntPtr)hWnd).ToInt64():X} visible={window.IsVisible} rect=({lpRect.Left},{lpRect.Top})-({lpRect.Right},{lpRect.Bottom})");
			}
		}
		catch (Exception ex)
		{
			LogDebug("[band] 診断失敗: " + ex.Message);
		}
	}

	private void PositionResizeBands()
	{
		if (_isWebMode)
		{
			double width = base.Width;
			double height = base.Height;
			_resizeBands[0].Width = width;
			_resizeBands[0].Height = 8.0;
			_resizeBands[0].Left = base.Left;
			_resizeBands[0].Top = base.Top;
			_resizeBands[1].Width = width;
			_resizeBands[1].Height = 8.0;
			_resizeBands[1].Left = base.Left;
			_resizeBands[1].Top = base.Top + height - 8.0;
			_resizeBands[2].Width = 8.0;
			_resizeBands[2].Height = height;
			_resizeBands[2].Left = base.Left;
			_resizeBands[2].Top = base.Top;
			_resizeBands[3].Width = 8.0;
			_resizeBands[3].Height = height;
			_resizeBands[3].Left = base.Left + width - 8.0;
			_resizeBands[3].Top = base.Top;
		}
	}

	private void Band_MouseDown(Window band, MouseButtonEventArgs e)
	{
		if (e.ChangedButton == MouseButton.Left && e.ButtonState == MouseButtonState.Pressed)
		{
			GetCursorPos(out var pt);
			HwndSource hwndSource = (HwndSource)PresentationSource.FromVisual(this);
			double num = hwndSource?.CompositionTarget.TransformToDevice.M11 ?? 1.0;
			double num2 = hwndSource?.CompositionTarget.TransformToDevice.M22 ?? 1.0;
			ResizeEdge resizeEdge = GetResizeEdge(new System.Windows.Point((double)pt.X / num - base.Left, (double)pt.Y / num2 - base.Top));
			_isResizing = true;
			_resizeEdge = (int)resizeEdge;
			_resizeStartScreenMouse = new System.Windows.Point(pt.X, pt.Y);
			_resizeStartBounds = new Rect(base.Left, base.Top, base.Width, base.Height);
			if (_isMaximized)
			{
				base.Left = _normalBounds.Left;
				base.Top = _normalBounds.Top;
				base.Width = _normalBounds.Width;
				base.Height = _normalBounds.Height;
				_resizeStartBounds = new Rect(base.Left, base.Top, base.Width, base.Height);
				_isMaximized = false;
				_controlBar.SetMaximizeIcon(isMaximized: false);
			}
			LogDebug($"band MouseDown (resize start) edge={resizeEdge} bounds=({_resizeStartBounds.Left},{_resizeStartBounds.Top},{_resizeStartBounds.Width:F0}x{_resizeStartBounds.Height:F0})");
			band.CaptureMouse();
		}
	}

	private void UpdateBandCursor(Window band, ResizeEdge captured)
	{
		if (!_isResizing)
		{
			GetCursorPos(out var pt);
			HwndSource hwndSource = (HwndSource)PresentationSource.FromVisual(this);
			double num = hwndSource?.CompositionTarget.TransformToDevice.M11 ?? 1.0;
			double num2 = hwndSource?.CompositionTarget.TransformToDevice.M22 ?? 1.0;
			double num3 = (double)pt.X / num;
			double num4 = (double)pt.Y / num2;
			bool flag = (captured & ResizeEdge.Left) != 0;
			bool flag2 = (captured & ResizeEdge.Right) != 0;
			bool flag3 = (captured & ResizeEdge.Top) != 0;
			bool flag4 = (captured & ResizeEdge.Bottom) != 0;
			bool flag5 = false;
			if (flag && flag3)
			{
				flag5 = num3 <= base.Left + 16.0 && num4 <= base.Top + 16.0;
			}
			else if (flag2 && flag3)
			{
				flag5 = num3 >= base.Left + base.Width - 16.0 && num4 <= base.Top + 16.0;
			}
			else if (flag && flag4)
			{
				flag5 = num3 <= base.Left + 16.0 && num4 >= base.Top + base.Height - 16.0;
			}
			else if (flag2 && flag4)
			{
				flag5 = num3 >= base.Left + base.Width - 16.0 && num4 >= base.Top + base.Height - 16.0;
			}
			Cursor cursor2 = (band.Cursor = ((!flag5) ? ((flag || flag2) ? Cursors.SizeWE : Cursors.SizeNS) : (((flag && flag3) || (flag2 && flag4)) ? Cursors.SizeNWSE : Cursors.SizeNESW)));
			if (band.Content is FrameworkElement frameworkElement)
			{
				frameworkElement.Cursor = cursor2;
			}
		}
	}

	private void Band_MouseMove()
	{
		if (_isResizing)
		{
			(double, double) resizeDeltaDip = GetResizeDeltaDip();
			ApplyResize(resizeDeltaDip.Item1, resizeDeltaDip.Item2);
		}
	}

	private void Band_MouseUp(Window band, MouseButtonEventArgs e)
	{
		LogDebug($"band MouseUp (resize end) bounds=({base.Left},{base.Top},{base.Width:F0}x{base.Height:F0})");
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

	[DllImport("user32.dll")]
	private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool GetWindowRect(nint hWnd, out RECT lpRect);

	[DllImport("user32.dll")]
	private static extern nint GetTopWindow(nint hWnd);

	[DllImport("user32.dll")]
	private static extern nint GetWindow(nint hWnd, uint uCmd);

	private static void BringToFront(nint hwnd)
	{
		SetWindowPos(hwnd, HWND_TOP, 0, 0, 0, 0, 19u);
	}

	private nint OnMainWindowHook(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
	{
		if (msg != 132)
		{
			return IntPtr.Zero;
		}
		if (_hitTestLogCount < 10)
		{
			_hitTestLogCount++;
			LogDebug($"WM_NCHITTEST hwnd=0x{((IntPtr)hwnd).ToInt64():X} hit={((IntPtr)wParam).ToInt32()} webMode={_isWebMode} (#{_hitTestLogCount})");
		}
		if (!_isWebMode)
		{
			// ウィンドウモード: 入力を完全に遮断（オーバーレイウィンドウが担当）
			handled = true;
			return new IntPtr(-1); // HTTRANSPARENT
		}
		// Webモード: HTCLIENT を明示的に返す。
		// AllowsTransparency=True によりメインウィンドウは WS_EX_LAYERED であり、
		// WPF デフォルトハンドラが Background=Transparent のピクセルで
		// HTTRANSPARENT を返すと、クリックが背面ウィンドウに貫通する。
		// HTCLIENT を返すことで OS が ChildWindowFromPoint 経由で
		// WebView2 の子HWND（Chrome_RenderWidgetHostHWND）にルーティングする。
		handled = true;
		return new IntPtr(1); // HTCLIENT
	}

	private void PinOverlayBelowMain()
	{
		SetWindowPos(new WindowInteropHelper(_overlay).EnsureHandle(), new WindowInteropHelper(this).EnsureHandle(), 0, 0, 0, 0, 19u);
	}

	[DllImport("user32.dll")]
	private static extern bool SetFocus(nint hWnd);

	[DllImport("user32.dll")]
	private static extern int GetSystemMetrics(int nIndex);

	private void SetMainHitTestTransparent(bool transparent)
	{
		try
		{
			nint handle = new WindowInteropHelper(this).Handle;
			int windowLong = GetWindowLong(handle, -20);
			windowLong = ((!transparent) ? (windowLong & -33) : (windowLong | 0x20));
			SetWindowLong(handle, -20, windowLong);
			SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0, 55u);
			LogDebug($"Main WS_EX_TRANSPARENT = {transparent} (ex=0x{windowLong:X})");
		}
		catch (Exception ex)
		{
			LogDebug("SetMainHitTestTransparent error: " + ex.Message);
		}
	}

	private void UpdateFloatingWindows()
	{
		if (!double.IsNaN(base.Left) && !double.IsNaN(base.Top))
		{
			_overlay.Left = base.Left;
			_overlay.Top = base.Top;
			_overlay.Width = base.Width;
			_overlay.Height = base.Height;
			PositionControlBar();
			PositionWebModeButtonWindow();
			PositionResizeBands();
		}
	}

	private void PositionControlBar()
	{
		ControlBarWindow controlBar = _controlBar;
		if (controlBar != null && controlBar.Visibility == Visibility.Visible)
		{
			_controlBar.Left = base.Left + base.Width - _controlBar.Width - 8.0;
			_controlBar.Top = base.Top + 8.0;
		}
	}

	private void ShowWebModeButtonWindow()
	{
		if (_webModeButtonWindow == null)
		{
			Button button = new Button
			{
				Width = 46.0,
				Height = 32.0,
				Cursor = Cursors.Hand,
				ToolTip = "ウィンドウ操作に戻る",
				Template = CreateReturnButtonTemplate(),
				Content = new Viewbox
				{
					Width = 20.0,
					Height = 16.0,
					Child = new System.Windows.Shapes.Path
					{
						Data = Geometry.Parse("M 0.75,0.75 L 20.75,0.75 L 20.75,16.75 L 0.75,16.75 Z M 0.75,6.75 L 20.75,6.75"),
						Width = 21.5,
						Height = 17.5,
						Stroke = new SolidColorBrush(System.Windows.Media.Color.FromRgb(68, 68, 68)),
						StrokeThickness = 1.5,
						Fill = System.Windows.Media.Brushes.Transparent,
						SnapsToDevicePixels = true
					}
				}
			};
			button.Click += delegate
			{
				SetWebMode(webMode: false);
			};
			TextBlock element = new TextBlock
			{
				Text = "クリックで戻ります",
				Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(68, 68, 68)),
				VerticalAlignment = VerticalAlignment.Center,
				Margin = new Thickness(10.0, 0.0, 8.0, 0.0)
			};
			StackPanel stackPanel = new StackPanel
			{
				Orientation = Orientation.Horizontal
			};
			stackPanel.Children.Add(button);
			stackPanel.Children.Add(element);
			_webModeButtonWindow = new Window
			{
				WindowStyle = WindowStyle.None,
				AllowsTransparency = true,
				Background = System.Windows.Media.Brushes.Transparent,
				Topmost = true,
				ShowInTaskbar = false,
				Width = 230.0,
				Height = 32.0,
				Content = new Border
				{
					CornerRadius = new CornerRadius(8.0),
					Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(153, byte.MaxValue, byte.MaxValue, byte.MaxValue)),
					Child = stackPanel
				}
			};
		}
		_webModeButtonWindow.Left = base.Left + base.Width - _webModeButtonWindow.Width - 8.0;
		_webModeButtonWindow.Top = base.Top + 8.0;
		_webModeButtonWindow.Show();
		_webModeButtonWindow.Activate();
		LogWebModeButtonState("after Show/Activate");
		base.Dispatcher.BeginInvoke((Action)delegate
		{
			LogWebModeButtonState("after dispatcher idle");
		}, DispatcherPriority.ApplicationIdle);
		StartWebModePinTimer();
	}

	private void StartWebModePinTimer()
	{
		if (_webModePinTimer == null)
		{
			_webModePinTimer = new DispatcherTimer
			{
				Interval = TimeSpan.FromMilliseconds(400L, 0L)
			};
			_webModePinTimer.Tick += delegate
			{
				PinWebModeButtonAboveMain();
			};
		}
		_webModePinTimer.Start();
	}

	private void StopWebModePinTimer()
	{
		_webModePinTimer?.Stop();
	}

	private void PinWebModeButtonAboveMain()
	{
		if (!_isWebMode)
		{
			return;
		}
		try
		{
			nint num = ((_webModeButtonWindow != null) ? new WindowInteropHelper(_webModeButtonWindow).EnsureHandle() : IntPtr.Zero);
			nint num2 = new WindowInteropHelper(this).EnsureHandle();
			HashSet<nint> hashSet = new HashSet<nint>();
			nint num3 = GetTopWindow(IntPtr.Zero);
			int num4 = 0;
			while (num3 != IntPtr.Zero && num4 < 200 && num3 != num2)
			{
				hashSet.Add(num3);
				num3 = GetWindow(num3, 2u);
				num4++;
			}
			bool flag = false;
			Window webModeButtonWindow = _webModeButtonWindow;
			if (webModeButtonWindow != null && webModeButtonWindow.IsVisible && !hashSet.Contains(num))
			{
				flag = true;
			}
			Window[] resizeBands = _resizeBands;
			foreach (Window window in resizeBands)
			{
				if (window.IsVisible && !hashSet.Contains(new WindowInteropHelper(window).EnsureHandle()))
				{
					flag = true;
					break;
				}
			}
			if (!flag)
			{
				return;
			}
			LogDebug("[webmode-btn] z-order violation detected -> re-pinning (button>bands>main)");
			SetWindowPos(num2, IntPtr.Zero, 0, 0, 0, 0, 19u);
			Window[] resizeBands2 = _resizeBands;
			foreach (Window window2 in resizeBands2)
			{
				if (window2.IsVisible)
				{
					BringToFront(new WindowInteropHelper(window2).EnsureHandle());
				}
			}
			webModeButtonWindow = _webModeButtonWindow;
			if (webModeButtonWindow != null && webModeButtonWindow.IsVisible)
			{
				BringToFront(num);
			}
		}
		catch
		{
		}
	}

	private void LogWebModeButtonState(string when)
	{
		try
		{
			if (_webModeButtonWindow == null)
			{
				return;
			}
			nint num = new WindowInteropHelper(_webModeButtonWindow).EnsureHandle();
			nint num2 = new WindowInteropHelper(this).EnsureHandle();
			GetWindowRect(num, out var lpRect);
			GetWindowRect(num2, out var lpRect2);
			int value = -1;
			nint num3 = GetTopWindow(IntPtr.Zero);
			int num4 = 0;
			while (num3 != IntPtr.Zero && num4 < 200)
			{
				if (num3 == num)
				{
					value = num4;
					break;
				}
				if (num3 == num2)
				{
					break;
				}
				num3 = GetWindow(num3, 2u);
				num4++;
			}
			LogDebug($"[webmode-btn] {when}: btn=0x{((IntPtr)num).ToInt64():X} visible={_webModeButtonWindow.IsVisible} rect=({lpRect.Left},{lpRect.Top})-({lpRect.Right},{lpRect.Bottom}) mainRect=({lpRect2.Left},{lpRect2.Top})-({lpRect2.Right},{lpRect2.Bottom}) zIdx={value} (mainに先着=-1でボタンがmainの下)");
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
		if (_webModeButtonWindow != null && _webModeButtonWindow.Visibility == Visibility.Visible)
		{
			_webModeButtonWindow.Left = base.Left + base.Width - _webModeButtonWindow.Width - 8.0;
			_webModeButtonWindow.Top = base.Top + 8.0;
		}
	}

	private static ControlTemplate CreateReturnButtonTemplate()
	{
		FrameworkElementFactory frameworkElementFactory = new FrameworkElementFactory(typeof(Border));
		frameworkElementFactory.Name = "BtnBorder";
		frameworkElementFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(6.0));
		frameworkElementFactory.SetValue(Border.BackgroundProperty, System.Windows.Media.Brushes.Transparent);
		FrameworkElementFactory frameworkElementFactory2 = new FrameworkElementFactory(typeof(ContentPresenter));
		frameworkElementFactory2.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
		frameworkElementFactory2.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
		frameworkElementFactory.AppendChild(frameworkElementFactory2);
		Trigger trigger = new Trigger
		{
			Property = UIElement.IsMouseOverProperty,
			Value = true
		};
		trigger.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(System.Windows.Media.Color.FromArgb(64, 0, 0, 0)), "BtnBorder"));
		ControlTemplate controlTemplate = new ControlTemplate(typeof(Button))
		{
			VisualTree = frameworkElementFactory
		};
		controlTemplate.Triggers.Add(trigger);
		return controlTemplate;
	}

	[DllImport("user32.dll", SetLastError = true)]
	private static extern int GetWindowLong(nint hWnd, int nIndex);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern int SetWindowLong(nint hWnd, int nIndex, int dwNewLong);

	[DllImport("user32.dll")]
	private static extern bool EnumChildWindows(nint hWndParent, EnumChildProc lpEnumFunc, nint lParam);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern int GetClassName(nint hWnd, StringBuilder lpClassName, int nMaxCount);

	private void LogWindowTree()
	{
		try
		{
			StringBuilder stringBuilder = new StringBuilder();
			AppendWindowTree(new WindowInteropHelper(this).Handle, stringBuilder, "  ");
			LogDebug("ウィンドウツリー:\n" + stringBuilder.ToString());
		}
		catch
		{
		}
	}

	private static void AppendWindowTree(nint parent, StringBuilder sb, string indent)
	{
		EnumChildWindows(parent, delegate(nint hWnd, nint _)
		{
			StringBuilder stringBuilder = new StringBuilder(256);
			GetClassName(hWnd, stringBuilder, stringBuilder.Capacity);
			int windowLong = GetWindowLong(hWnd, -20);
			bool value = (windowLong & 0x20) != 0;
			bool value2 = (windowLong & 0x8000000) != 0;
			StringBuilder stringBuilder2 = sb;
			StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(30, 5, stringBuilder2);
			handler.AppendFormatted(indent);
			handler.AppendLiteral("[");
			handler.AppendFormatted(((IntPtr)hWnd).ToInt64(), "X");
			handler.AppendLiteral("] \"");
			handler.AppendFormatted(stringBuilder);
			handler.AppendLiteral("\" TRANSPARENT=");
			handler.AppendFormatted(value);
			handler.AppendLiteral(" NOACTIVATE=");
			handler.AppendFormatted(value2);
			stringBuilder2.AppendLine(ref handler);
			AppendWindowTree(hWnd, sb, indent + "  ");
			return true;
		}, IntPtr.Zero);
	}

	internal void ApplyOpacity(double opacity)
	{
		opacity = Math.Clamp(opacity, 0.2, 1.0);
		_pageOpacity = opacity;
		ApplyWebViewAlpha();
		LogDebug("ApplyOpacity: webview alpha -> " + opacity.ToString("0.###", CultureInfo.InvariantCulture));
	}

	// WPF の Window.Opacity は WebView2（子HWND）に効かないため、
	// WebView2 ホストウィンドウ（Chrome_WidgetWin_1）へ LWA_ALPHA を適用して
	// ウィンドウ全体をデスクトップに対して半透明にする（WinForms の Form.Opacity と同じ機構）。
	private void ApplyWebViewAlpha()
	{
		if (_webViewHostHwnd == 0)
		{
			return; // StartWebViewAlphaFinder が発見した時点で適用される
		}
		byte alpha = (byte)Math.Round(255.0 * _pageOpacity);
		// WS_EX_LAYERED フラグ自体がリセットされている場合は再付与する
		if ((GetWindowLong(_webViewHostHwnd, -20) & 0x80000) == 0)
		{
			SetWindowLong(_webViewHostHwnd, -20, GetWindowLong(_webViewHostHwnd, -20) | 0x80000);
		}
		bool ok = SetLayeredWindowAttributes(_webViewHostHwnd, 0, alpha, LWA_ALPHA);
		if (!ok)
		{
			LogDebug("ApplyWebViewAlpha: SetLayeredWindowAttributes failed err=" + Marshal.GetLastWin32Error());
		}
	}

	// WebView2 のホストHWND は初期化後に非同期で作られるため、タイマーでリトライして探索する。
	// 発見後は Chromium がページ読み込み等でホストウィンドウの属性をリセットし得るため、
	// ウォッチドッグで LWA_ALPHA を定期再適用する（1秒毎・Win32コール1回のみの負荷）。
	private void StartWebViewAlphaFinder()
	{
		if (_webViewHostHwnd != 0)
		{
			StartAlphaWatchdog();
			return;
		}
		DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
		timer.Tick += delegate
		{
			nint main = new WindowInteropHelper(this).Handle;
			nint found = 0;
			EnumChildProc proc = (h, _) =>
			{
				System.Text.StringBuilder sb = new System.Text.StringBuilder(256);
				GetClassName(h, sb, 256);
				if (sb.ToString() == "Chrome_WidgetWin_1")
				{
					found = h;
					return false;
				}
				return true;
			};
			EnumChildWindows(main, proc, 0);
			if (found != 0)
			{
				timer.Stop();
				_webViewHostHwnd = found;
				LogDebug("WebView2 host hwnd found: 0x" + found.ToInt64().ToString("X"));
				ApplyWebViewAlpha();
				StartAlphaWatchdog();
			}
		};
		timer.Start();
	}

	private void StartAlphaWatchdog()
	{
		if (_alphaWatchdog != null)
		{
			return;
		}
		_alphaWatchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
		_alphaWatchdog.Tick += delegate
		{
			ApplyWebViewAlpha();
		};
		_alphaWatchdog.Start();
	}

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool SetLayeredWindowAttributes(nint hWnd, uint crKey, byte bAlpha, uint dwFlags);

	private const uint LWA_ALPHA = 2;


	private void NavigateToUrl(string url)
	{
		try
		{
			if (Uri.TryCreate(url, UriKind.Absolute, out Uri result))
			{
				WebView.Source = result;
				LogDebug("Navigate: " + url);
			}
		}
		catch (Exception ex)
		{
			MessageBox.Show("URL の読み込みに失敗しました。\n" + ex.Message, "読み込みエラー", MessageBoxButton.OK, MessageBoxImage.Exclamation);
		}
	}

	private static void LogDebug(string message)
	{
		if (!_debugMode) return;
		try
		{
			Directory.CreateDirectory(SettingsDirectory);
			File.AppendAllText(System.IO.Path.Combine(SettingsDirectory, "DGXSparkUtilWidget.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
		}
		catch
		{
		}
	}

	private AppSettings? LoadSettings()
	{
		try
		{
			if (!File.Exists(SettingsPath))
			{
				return null;
			}
			string json = File.ReadAllText(SettingsPath);
			JsonSerializerOptions options = new JsonSerializerOptions
			{
				PropertyNameCaseInsensitive = true
			};
			AppSettings appSettings = JsonSerializer.Deserialize<AppSettings>(json, options);
			if (appSettings == null)
			{
				return null;
			}
			appSettings.Opacity = Math.Clamp(appSettings.Opacity, 0.2, 1.0);
			return appSettings;
		}
		catch (Exception value)
		{
			LogDebug($"LoadSettingsエラー: {value}");
			return null;
		}
	}

	private void SaveSettings(string url, double opacity, Rect? windowBounds = null)
	{
		AppSettings appSettings = LoadSettings();
		AppSettings value = new AppSettings
		{
			Url = url,
			Opacity = Math.Clamp(opacity, 0.2, 1.0),
			WindowBounds = (windowBounds.HasValue ? new WindowBounds(windowBounds.Value) : appSettings?.WindowBounds)
		};
		try
		{
			Directory.CreateDirectory(SettingsDirectory);
			File.WriteAllText(SettingsPath, JsonSerializer.Serialize(value, new JsonSerializerOptions
			{
				WriteIndented = true
			}));
		}
		catch (Exception ex)
		{
			MessageBox.Show("設定の保存に失敗しました。\n" + ex.Message, "保存エラー", MessageBoxButton.OK, MessageBoxImage.Hand);
		}
	}

	private Rect GetVirtualScreenRect()
	{
		HwndSource hwndSource = (HwndSource)PresentationSource.FromVisual(this);
		double num = hwndSource?.CompositionTarget.TransformToDevice.M11 ?? 1.0;
		double num2 = hwndSource?.CompositionTarget.TransformToDevice.M22 ?? 1.0;
		return new Rect((double)GetSystemMetrics(76) / num, (double)GetSystemMetrics(77) / num2, (double)GetSystemMetrics(78) / num, (double)GetSystemMetrics(79) / num2);
	}

	private void RestoreWindowPosition(AppSettings? settings)
	{
		WindowBounds windowBounds = settings?.WindowBounds;
		if (windowBounds != null && double.IsFinite(windowBounds.Left) && double.IsFinite(windowBounds.Top) && windowBounds.Width >= 400.0 && windowBounds.Height >= 300.0)
		{
			Rect rect = windowBounds.ToRect();
			Rect rect2 = rect;
			rect2.Intersect(GetVirtualScreenRect());
			if (!rect2.IsEmpty)
			{
				base.Left = windowBounds.Left;
				base.Top = windowBounds.Top;
				base.Width = windowBounds.Width;
				base.Height = windowBounds.Height;
				LogDebug($"起動: 保存済み位置を復元 ({windowBounds.Left},{windowBounds.Top},{windowBounds.Width:F0}x{windowBounds.Height:F0})");
				return;
			}
			LogDebug($"起動: 保存済み位置がオフスクリーン ({windowBounds.Left},{windowBounds.Top}) -> プライマリモニター中央へフォールバック");
			base.Width = windowBounds.Width;
			base.Height = windowBounds.Height;
		}
		Rect workArea = SystemParameters.WorkArea;
		base.Left = workArea.Left + (workArea.Width - base.Width) / 2.0;
		base.Top = workArea.Top + (workArea.Height - base.Height) / 2.0;
	}

	private void OnWindowClosing(object? sender, CancelEventArgs e)
	{
		Rect value = (_isMaximized ? _normalBounds : new Rect(base.Left, base.Top, base.Width, base.Height));
		LogDebug($"終了: 位置・サイズを保存 ({value.Left},{value.Top},{value.Width:F0}x{value.Height:F0})");
		SaveSettings(_currentUrl, _pageOpacity, value);
	}


}
