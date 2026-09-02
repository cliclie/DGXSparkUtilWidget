# DGXSparkUtilWidget 仕様書

> 本ドキュメントは、**現在のソースコードの実装内容を正（しとう）とした仕様書**である。
> ソースと乖離した記述を含まないことを前提に、システムの全体像・各コンポーネントの挙動・制約を記述する。
> 記述言語は日本語とする。

---

## 1. 概要

DGX Spark Utility の Web 画面を、**最前面固定なし（通常ウィンドウ）・フレームレス・角丸**のウィジェットとして表示する Windows デスクトップアプリ。
既定では Web ページへの OS 入力を遮断し「ウィンドウそのものとして扱う」（移動・リサイズ・最小化・最大化）ことができ、
必要に応じて Web ページを直接操作するモードに切り替えられる。

- Web 画面の常時表示（ウィジェットとしての利用）
- Web への入力を既定でブロックし、ウィンドウ操作を優先
- 任意のタイミングで Web 直接操作モードに切り替え、復帰も可能
- 透過率（不透明度）調整
- 接続先 URL / 透過率 / ウィンドウ位置・サイズの永続化

## 2. 技術スタックとビルド

| 項目 | 値 |
|------|----|
| 言語 / フレームワーク | C# / WPF |
| ターゲットフレームワーク | `net9.0-windows` |
| ランタイム ID | `win-x64`（全構成で固定、出力パス `bin\<cfg>\net9.0-windows\win-x64\`） |
| 出力形式 | Debug: フレームワーク依存（.NET 9 Desktop Runtime 前提の高速ビルド）/ Release: 自己完結型（`SelfContained`、ランタイム内包） |
| 公開形式 | 単一ファイル `.exe`（`PublishSingleFile` + `IncludeNativeLibrariesForSelfExtract`、Release の publish 時のみ適用） |
| 依存パッケージ | `Microsoft.Web.WebView2` `1.0.2651.64` |
| アプリケーションアイコン | `images\DGXSpark.ico`（リソースとしても組み込み） |

- `UseWPF` / `Nullable` / `ImplicitUsings` を有効。
- `tools\**\*.cs` はビルド対象から除外する（診断用 P/Invoke ヘルパーがアプリに混入しないため）。

ビルド:
- 開発ビルド: `dotnet build`（Debug、フレームワーク依存 → 実行時に .NET 9 Desktop Runtime が必要、ビルド高速）
- Release ビルド: `dotnet build -c Release`（自己完結型、exe + ランタイムDLL群）
- 公開ビルド（単一ファイル）: `dotnet publish -c Release -r win-x64`
  - 公開先: `bin/Release/net9.0-windows/win-x64/publish/DGXSparkUtilWidget.exe`
  - 単一の `.exe` が生成され、別フォルダに exe 単体をコピーして実行可能。設定ファイル・ログは exe と同じフォルダに保存される（§7.1, §9 参照）

## 3. プロジェクト構成

```
DGXSparkUtilWidget/
├── DGXSparkUtilWidget.sln           # ソリューションファイル
├── DGXSparkUtilWidget.csproj        # プロジェクトファイル
├── App.xaml / App.xaml.cs           # エントリポイント（StartupUri = MainWindow）
├── MainWindow.xaml / .xaml.cs       # メインウィンドウ（WebView2 + 入力遮断・位置・透過・設定）
├── ControlBarWindow.xaml / .xaml.cs # フローティングコントロールバー（独立ウィンドウ）
├── SettingsDialog.xaml / .xaml.cs   # 設定ダイアログ
├── images/
│   ├── DGXSpark.ico                 # アプリケーションアイコン
│   └── DGXSpark.png                 # アイコン元画像
├── tools/                           # 診断・テスト用 PowerShell スクリプト（ビルド対象外）
├── develop-log.md                   # 開発ログ
├── specification.md                 # 本仕様書
└── README.md
```

---

## 4. ウィンドウ構成

アプリは、機能分離のため**複数の（最前面固定なしの）小型ウィンドウ**を組み合わせて構成する。

### 4.1 メインウィンドウ（`MainWindow`）
Web 画面本体を表示するウィンドウ。

| 項目 | 値 / 挙動 |
|------|-----------|
| `WindowStyle` | `None`（フレームレス） |
| `AllowsTransparency` / `Background` | `True` / `Transparent` |
| `ResizeMode` | `NoResize`（リサイズはリサイズバンドで行う。§6.3 参照） |
| `Topmost` | `False`（最前面固定しない通常のウィンドウ） |
| `WindowStartupLocation` | `Manual`（初期位置は §7.4 で決定） |
| 初期サイズ | `800 x 600`、最小 `400 x 300` |
| アイコン | `images/DGXSpark.ico` |

- ルート要素は `CornerRadius=10`・`ClipToBounds=True` の `Border`（`RootBorder`）。角丸を表現し、WebView2 が角からはみ出さないようクリップ。
- 内部コンテンツは `WebView2` のみ（`Horizontal/VerticalAlignment=Stretch`）。
- `RootBorder` の左クリックでウィンドウをドラッグ移動（`DragMove`）。

### 4.2 コントロールバー（`ControlBarWindow`）
ホバーで表示されるフローティング操作バー。WebView2 はネイティブ子 HWND を作るため WPF レイヤーではクリックが通らないため、独立したウィンドウとして配置。

| 項目 | 値 / 挙動 |
|------|-----------|
| サイズ | `230 x 32` |
| 外観 | フレームレス・透過背景、`CornerRadius=8` の半透明白（`#99FFFFFF`） |
| `ShowInTaskbar` | `False` |

メインウィンドウの**右上**に配置（`Left = main.Left + main.Width - bar.Width - 8`、`Top = main.Top + 8`）。メインウィンドウの位置・サイズ変化時に追従再配置。

**ボタン（左から順）：**

| 順 | ボタン | 名前(XAML) | 機能 |
|----|--------|-----------|------|
| 1 | ⚡ | `BtnWebToggle` | Web 操作モード切替（§5）。Web モード中は白反色アイコン |
| 2 | ≡ | `BtnMenu` | 設定ダイアログを表示 |
| 3 | ─ | `BtnMinimize` | メインウィンドウを最小化 |
| 4 | □ / ❐ | `BtnMaximizeRestore` | 最大化 / 通常サイズをトグル |
| 5 | ✕ | `BtnClose` | アプリを終了 |

- 共通スタイル `ControlButtonStyle`（46×32、ホバー `#40000000`、押下 `#66000000`）。
- 閉じるボタンのみ `CloseButtonStyle`（ホバーで赤 `#E81123`）。

### 4.3 透過オーバーレイ（内部ウィンドウ）
ウィンドウモード時に WebView2 への入力を遮断する、メインと同位置・同サイズの透明ウィンドウ。
- `WS_EX_TRANSPARENT` を付与し、メインより前面に固定。
- 背景色 `Color.FromArgb(1, 0, 0, 0)`（alpha=1）で OS ヒットテスト上不透過扱い。
- これにより Web への入力が遮断され、代わりにメインのドラッグ移動が有効。

### 4.4 リサイズバンド（内部ウィンドウ × 8）
`ResizeMode=NoResize` のため、上下左右 4 辺 + 4 隅の計 8 個の透明ウィンドウ（幅/高 8px）でリサイズを実現。Web モード時のみ表示。

### 4.5 Web 復帰ボタン（内部ウィンドウ）
Web モード中に左上に現れる「ウィンドウ操作へ戻る」ボタンウィンドウ（230×32）。
- `WS_EX_TRANSPARENT` なオーバーレイの下で入力が通らないため独立ウィンドウ。
- `SetWindowPos(SWP_NOACTIVATE)` で表示し焦点を奪わない。
- ボタン＋「クリックで戻ります」ラベルの構成。

---

## 5. 入力モード（ウィンドウモード / Web モード）

アプリは 2 つのモードで動作する。既定は**ウィンドウモード**。

### 5.1 既定状態（ウィンドウモード）
- メインウィンドウに `WS_EX_TRANSPARENT` を付与（`SetMainHitTestTransparent(true)`）し、親が透明状態の子ツリー全体が OS ヒットテスト対象外になる。
- 透過オーバーレイが Web 入力を遮断し、代わりにメインウィンドウ（`RootBorder`）上でドラッグ移動が有効。
- ウィンドウモードではリサイズバンドは非表示のためリサイズできない（リサイズは Web モードのみ）。

### 5.2 Web モードへの切替（⚡ ボタン）
`BtnWebToggle_Click` → `SetWebMode(webMode: true)` を実行:
1. メインウィンドウの `WS_EX_TRANSPARENT` を解除（`SetMainHitTestTransparent(false)`）→ WebView2 が直接入力を受け取る。
2. 透過オーバーレイを非表示。
3. 8 個のリサイズバンドを表示（§6.3）。
4. 左上の Web 復帰ボタンウィンドウを表示（`ShowWebModeButtonWindow`）。
5. ホバーウォッチドッグ / 非表示タイマーを停止し、コントロールバーを非表示。
6. `RootBorder` の背景を `Color.FromArgb(1,0,0,0)`（alpha=1 のほぼ透明）に変更。これは `WS_EX_LAYERED`（`AllowsTransparency=True`）の層状ウィンドウが alpha=0 だと OS hit-test が `HTTRANSPARENT` になりクリックが背面に透過するのを防ぐため。
7. `PinWebModeButtonAboveMain` と 400ms 周期の `_webModePinTimer` で Z-order を維持。

### 5.3 ウィンドウモードへの復帰
Web 復帰ボタン（§4.5）または ⚡ ボタンで `SetWebMode(webMode: false)`:
1. `WS_EX_TRANSPARENT` を再付与し Web 入力を遮断。
2. 透過オーバーレイを再表示。
3. リサイズバンド・Web 復帰ボタンを非表示、`_webModePinTimer` を停止。
4. ホバーウォッチドッグを再開し、必要に応じてコントロールバーを表示。
5. `RootBorder` の背景を元の透過（`Transparent`）に戻す。

状態は `MainWindow._isWebMode` / `_wasWebMode` で管理する。

---

## 6. 挙動仕様

### 6.1 コントロールバーの表示 / 非表示（ホバー）
- `RootBorder` / オーバーレイ / コントロールバーのいずれかの `MouseEnter` で `ShowControlBar`、`MouseLeave` で `HideControlBar` を呼ぶ。
- 加えて **ホバーウォッチドッグ**（`_hoverWatchdog`、250ms 周期）が `GetCursorPos` でカーソル位置を検査し、メインウィンドウ内にカーソルがあるなら表示・外なら非表示を強制。これにより WPF の MouseEnter/Leave が取りこぼすケースも補完する。
- **表示**: コントロールバーが非表示なら `Show()` して右上に配置し、`BringToFront`。不透明度が 0.5 未満なら 250ms の `DoubleAnimation`（QuadraticEase/EaseOut）でフェードイン。
- **非表示**: `_hideTimer`（300ms）で待機 → 350ms のフェードアウト完了後に `Hide()`。この間にホバーが再発生すると `_hideTimer` が止まり非表示は取り消される。
- ドラッグ中 / リサイズ中は表示・非表示を抑制。

### 6.2 透過率（不透明度）
- 範囲は **0.2 〜 1.0**（`Math.Clamp` でクランプ）、保持値は `_pageOpacity`。
- **WPF の `Window.Opacity` は子 HWND（WebView2）に効かない**ため、WebView2 ホストウィンドウ（`Chrome_WidgetWin_1`）に `SetLayeredWindowAttributes`（`LWA_ALPHA`）を適用し、ウィンドウ全体をデスクトップに対して半透明にする。
- WebView2 ホスト HWND は初期化後に非同期で作られるため、`DispatcherTimer`（250ms）で子ウィンドウを列挙し `Chrome_WidgetWin_1` を探し発見する（`StartWebViewAlphaFinder`）。
- フルナビゲーションでドキュメントが置き換わってもホスト HWND は再利用されるため、一度見つかればそのまま適用を継続。
- 発見後、Chromium がページ読み込み等でホストウィンドウ属性をリセットし得るため、**1 秒毎のウォッチドッグ**（`_alphaWatchdog`）で `LWA_ALPHA` を再適用する。
- `WS_EX_LAYERED` フラグがリセットされていた場合は再付与してから適用。
- `WebView.DefaultBackgroundColor = Color.Transparent` とし、WPF レイヤーは完全透明にして Web コンテンツの alpha がそのままデスクトップ透過になる前提にする。
- 適用値は `_pageOpacity` に保持し、`ApplyOpacity`（公開、設定ダイアログからライブプレビュー用）で呼び出される。

### 6.3 リサイズ（リサイズバンド）
- 8 個のバンドで上下左右 4 辺 + 4 隅を捕捉。マウスダウンで `ResizeEdge` フラグを取得し、ドラッグ中は `GetCursorPos`（画面座標）と DPI 換算（`TransformToDevice`）で差分を算出してメインウィンドウを `SetWindowPos` でリサイズ。
- 最小サイズ `400 x 300` を維持（`ResizeMinWidth` / `ResizeMinHeight`）。
- 角のバンドは 16px 以内にマウスがある場合のみ角キャプチャ扱い。
- リサイズ中はバンドのカーソルがキャプチャ方向（`SizeNS` / `SizeWE` / `SizeNWSE` / `SizeNESW`）に切り替わる。

### 6.4 最小化 / 最大化 / 閉じる
- **最小化**（`BtnMinimize_Click`）: `WindowState = Minimized`。最小化時イベントでオーバーレイ・リサイズバンド・コントロールバー・Web 復帰ボタンを**すべて非表示**。
- **最大化 / 元に戻す**（`BtnMaximizeRestore_Click`）: 最大化時はプライマリモニタ作業領域（`SystemParameters.WorkArea`）全体に拡大し通常状態を `_normalBounds` に保持。元に戻す時は `_normalBounds` を復元。アイコンは `SetMaximizeIcon` で □ ↔ ❐ に切替。
- **閉じる**（`BtnClose_Click`）: `Application.Current.Shutdown()` でアプリを終了。
- 最小化からの復帰時（`StateChanged`）:
  - ウィンドウモード → オーバーレイを再表示（`ShowOverlay`）。
  - Web モード → リサイズバンドと Web 復帰ボタンを再表示。

### 6.5 Z-order の維持
- ウィンドウモードではオーバーレイがメインより前面、Web モードでは「Web 復帰ボタン > リサイズバンド > メイン」の順を維持。
- 各ウィンドウは独立したトップレベルウィンドウのため Z-order 競合が起き得る。`_webModePinTimer`（400ms）で `GetTopWindow` 経由の Z-order を走査し、関係が崩れたら `SetWindowPos(HWND_TOP)` / `BringToFront` で再固定（re-pin）。
- メインウィンドウがアクティブ化（`Activated`）したときも、オーバーレイ / コントロールバーを前面に固定し直す。
- **クリックによる前面化（`RaiseMainToForeground`）**: 全ウィンドウが非 Topmost（通常 Z-order 帯）のため、他ウィンドウの下に沈んだ状態で本体をクリックしても自動的に前面化されない。そこで `Overlay_MouseDown`（ウィンドウモード）と `RootBorder_MouseLeftButtonDown`（Web モード）で `RaiseMainToForeground()` を呼び、`SetWindowPos(HWND_TOP)` + `SetForegroundWindow` でメインを最前面化・アクティベートする。これに伴う `Activated` ハンドラがオーバーレイ・コントロールバーの Z 関係を再固定する。

### 6.6 初回起動時の導入手順
- `MainWindow_Loaded` で WebView2 を初期化し、設定を読み込み（`LoadSettings`）。
- 設定値に有効な URL がある場合: `_currentUrl` に設定し `ApplyOpacity` + `NavigateToUrl` で直接表示。
- URL が空（または読込不能）の場合: オーバーレイを非表示にして**設定ダイアログを自動表示**。保存すれば設定を保存し透過率を適用して URL にナビゲート。キャンセルならプレースホルダ HTML（「接続先 URL が設定されていません…」）を表示。
- 上記のいずれの場合も、finally でウィンドウモードならオーバーレイを再表示。

### 6.7 WebView2 の初期化 / スクロールバー
- `EnsureCoreWebView2Async` で初期化。失敗時はメッセージボックス表示して `Close()`。
- 有効な絶対 URI なら `WebView.Source` に設定（`NavigateToUrl`）。無効 / 例外時はメッセージボックスでエラー表示。
- **スクロールバー制御**（`ApplyScrollbarCss`）: 初期とウィンドウモード時は `::-webkit-scrollbar{display:none}` で隠す（初回ロード時のちらつき防止のため先に注入）。Web モード時は WebKit スクロールバーを表示し、幅 16px・サム幅 8px・角丸・濃灰、ホバー時は色のみ変化、トラック上下に余白（上 48px / 下 4px）を確保するスタイルを `ExecuteScriptAsync` で動的注入。`<style id="wscroll">` を作成・上書きし、`'` と `\` をエスケープして JS 埋め込み。

---

## 7. 設定 / 永続化

### 7.1 設定ファイル
- **パス**: exe と同じディレクトリの `DGXSparkUtilWidget.json`。ディレクトリは `Environment.ProcessPath`（実行中 exe の実パス）の親ディレクトリから取得する。単一ファイル公開ビルドでは `AppDomain.CurrentDomain.BaseDirectory` が一時展開先を指すため使用しない（exe をフォルダ間で移動しても設定が exe に追従する）。
- **形式**: JSON（読み込み時はプロパティ名の大文字小文字を無視、書き込み時はインデント付き）。

| キー | 型 | 説明 | 既定値 |
|------|----|------|--------|
| `Url` | string | 接続先 URL（http / https / file 等のスキームを許容） | 空文字列 |
| `Opacity` | double | 不透明度（0.2〜1.0、読み込み時にクランプ） | 1.0 |
| `WindowBounds` | object | `{ Left, Top, Width, Height }`（double） | null（保存なし） |

```json
{
  "Url": "http://192.168.0.110:8080",
  "Opacity": 1.0,
  "WindowBounds": { "Left": 100, "Top": 100, "Width": 800, "Height": 600 }
}
```

### 7.2 読み込み（`LoadSettings`）
- ファイル不存在 / 破損時は `null` を返し、デフォルトにフォールバック。例外はデバッグログのみ。

### 7.3 保存（`SaveSettings`）
- 引数で渡されない `WindowBounds` は、既存設定値を**保持**（位置・サイズが失われない）。
- `Opacity` は 0.2〜1.0 にクランプして保存。

### 7.4 ウィンドウ位置・サイズの復元（`RestoreWindowPosition`）
- 保存済みの `WindowBounds`（`Width>=400` かつ `Height>=300` で有限値）かつ仮想スクリーンと交差がある場合、その位置・サイズで復元。
- オフスクリーン（仮想スクリーンと交差しない）ならプライマリモニタ作業領域中央へフォールバック（サイズのみ保持）。
- 保存値がなければプライマリモニタ（`SystemParameters.WorkArea`）中央に配置。
- 閉じる時（`OnWindowClosing`）に現在の位置・サイズ（最大化時は `_normalBounds`）と URL・透過率を保存する。

---

## 8. 設定ダイアログ（`SettingsDialog`）

| 項目 | 値 / 挙動 |
|------|-----------|
| サイズ | `440 x 340` |
| 外観 | フレームレス・透過背景、`CornerRadius=10`、背景色 `#FF1E1E2E`（ダーク） |
| `ShowInTaskbar` | `False` |
| 位置 | `WindowStartupLocation=CenterOwner` |
| アイコン | `images/DGXSpark.ico` |

**内容（日本語ラベル）**
1. タイトルバー「設定」＋右上の閉じる（✕）ボタン。
2. **接続先 URL**: `TextBox`（`TxtUrl`）。
3. **透過率（不透明度）**: `Slider`（`OpacitySlider`）、`0.2〜1.0`、スナップなし。現在値を右側に `0.00` 形式で表示（`LblOpacity`）。
4. アクションボタン: 「キャンセル」「保存」。

**挙動**
- コンストラクタに現在の URL と不透明度を受け取りプレフィルする（不透明度は 0.2〜1.0 にクランプ）。
- スライダー値変更時はラベル更新のうえ、`Owner` が `MainWindow` なら `ApplyOpacity` を呼び**メインウィンドウの不透明度を即時ライブプレビュー**。
- 「保存」: URL をトリムし、非空の場合 `Uri.TryCreate(Absolute)` で検証（失敗なら警告メッセージボックスで中断）。`Url` / `OpacityValue` を保持し `DialogResult=true` で閉じる。
- 「キャンセル」/ 右上✕: `DialogResult=false` で閉じる（変更は破棄）。
- 本体（`RootBorder`）をドラッグで移動可能。ただし `TextBox` / `Slider` / `Button` 上のクリックではドラッグしない（親階層を辿って判断）。

**MainWindow 側の呼び出し**
- 保存成功時は `SaveSettings(url, opacity, null)` で保存し、`ApplyOpacity` + `NavigateToUrl` を実行。
- メニュー（≡）ボタンから表示される。

---

## 9. コマンドライン / デバッグログ

- `-DEBUG` オプション（大文字小文字を区別しない、`-` 接頭辞で一致）を指定したとき、または**実行中の exe のフルパスに `...\Debug\` を含む場合**（Debug ビルドを実行ディレクトリから実行した場合）にデバッグログを有効化。単一ファイル公開 exe はパスが移動先になるため、`-DEBUG` 指定時のみ有効になる。
- ログは `DGXSparkUtilWidget.log`（設定ディレクトリ）に追記形式で書き出される。
- 公開ビルドで `-DEBUG` なしの場合、ログは出力されない。
- ログ対象例: 起動・WebView2 初期化・ホスト HWND 発見・透過適用・ナビゲート・モード切替・ドラッグ / リサイズ / ホバー状態・最小化 / 最大化 / 閉じる・Z-order 再固定 等。

```bash
DGXSparkUtilWidget.exe -DEBUG
```

---

## 10. P/Invoke（Win32）

実装で使用される主な Win32 API:

| API | 用途 |
|-----|------|
| `SetWindowLong` / `GetWindowLong` (`GWL_EXSTYLE`) | `WS_EX_TRANSPARENT`（0x20）/ `WS_EX_LAYERED`（0x80000）の付与・除去 |
| `SetWindowPos` | 位置 / サイズ変更、Z-order 固定（`HWND_TOP`） |
| `SetLayeredWindowAttributes` (`LWA_ALPHA`) | WebView2 ホストの透過率適用 |
| `EnumChildWindows` / `GetClassName` | `Chrome_WidgetWin_1`（WebView2 ホスト）の探索 |
| `GetCursorPos` / `SetCursorPos` | ドラッグ / リサイズ用の画面座標取得 |
| `GetWindowRect` | 境界取得（リサイズ・診断） |
| `GetTopWindow` / `GetWindow` (`GW_HWNDNEXT`) | Z-order 走査（再固定用） |
| `GetSystemMetrics` (`SM_X/Y/CX/CY VIRTUALSCREEN`) | 仮想スクリーン矩形（オフスクリーン判定） |
| `WM_NCHITTEST`（メッセージフック） | ウィンドウモードで `HTTRANSPARENT`（-1）、Web モードで `HTCLIENT`（1）を返す |

---

## 11. 制約・注意点

- **透過は WebView2 ホスト HWND への `LWA_ALPHA` 適用**であり、WPF の `Window.Opacity` では実現できない。
- **Web 入力遮断**は `WS_EX_TRANSPARENT`（親が透明で子ツリーがヒットテスト対象外になる特性）+ 透過オーバーレイで実現。`WM_NCHITTEST` フックのみでは WebView2 の子 HWND が直接 OS ヒットテストを受けるため不十分。
- **独立したトップレベルウィンドウ**は、WebView2 がネイティブ子 HWND を作るため WPF レイヤーではクリックが通らないことと、Z-order 競合を解消するために採用。各ウィンドウの Z 関係はタイマーで維持する必要がある。
- リサイズは `ResizeMode=NoResize` のため 8 個のバンドウィンドウで補完する（Web モード時のみ表示）。
- 最小サイズ `400 x 300` はリサイズ・復元時に強制される。
- `tools\` 配下の C# はビルド対象外。

