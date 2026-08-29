# DGXSparkUtilWidget 開発ログ

## 2026-08-28

### 1. プロジェクト初期セットアップ

- **commit:** `99288d1` — Initial commit
- プロジェクト基盤の作成（.sln / .csproj / .gitignore / .clinerules / specification.md）
- WPF + WebView2 ベースのウィジェットアプリの骨格

---

### 2. メイン実装 — WPF/WebView2 ウィジェット + 入力ブロックオーバーレイ

- **commit:** `a40cb46` — Add WPF/WebView2 widget app with input-blocking overlay
- **追加ファイル:** App.xaml(.cs), MainWindow.xaml(.cs), ControlBarWindow.xaml(.cs), SettingsDialog.xaml(.cs), images/DGXSpark.ico, images/DGXSpark.png
- **主な機能:**
  - フレームレス半透明ウィンドウ（WebView2 で DGX Spark Utility Web UI を表示）
  - ホバーで表示/非表示になるコントロールバー（最小化・設定・Web切替）
  - ウィンドウモード（Web入力ブロック）/ Webモード（入力許可）の切り替え
  - 透過オーバーレイ（Layered Window, alpha=0）で WebView2 への OS 入力遮断
  - ドラッグ移動（MouseLeftButtonDown → SetWindowPos）
  - 設定ダイアログ（URL / Opacity）
- **16 files, +1492 / -1**

---

### 3. フックタイミング修正 + ドラッグ追跡修正 + Z-order 診断

- **commit:** `628cca1` — fix: move hook to SourceInitialized, fix drag tracking with GetCursorPos, add Z-order diagnostics
- **変更:**
  - `WM_NCHITTEST` フックの登録时机を `Loaded` → `SourceInitialized` に変更（HwndSource が確実に出るよう）
  - ドラッグ時に `MouseEventArgs.Location`（ウィンドウ座標）→ `GetCursorPos`（画面座標）に変更し、座標変換ミスでウィンドウが飛ぶ問題を修正
  - Z-order / WindowFromPoint 診断ログを追加（起動時にツリー出力）
- **1 file changed, +97 / -16**

---

### 4. プライマリモニター中央配置の明示化

- **commit:** `a48fd85` — fix: explicitly center window on primary monitor work area
- **問題:** `WindowStartupLocation.CenterScreen` が「アクティブモニター」基準で、デュアルディスプレイ環境で上段モニターに映り、ユーザーに「起動したが見えない」と認識されていた
- **修正:** `MainWindow` コンストラクタ内で `SystemParameters.WorkArea`（プライマリモニタ作業領域）を取得し、明示的に中央配置
- XAML の `WindowStartupLocation="CenterScreen"` は残すが、コード側の明示指定が優先される
- **2 files changed, +9 / -1**

---

### 5. オーバーレイが HTTRANSPARENT になる問題（alpha=0 layered window）

- **commit:** `267b3e9` — fix: overlay must be mouse-hit-testable - alpha=0 layered window is HTTRANSPARENT
- **問題:** ウィンドウモードで WebView2 に入力されないはずが、Web ページでクリック・ホバーが効いていた
- **根因:** オーバーレイが `Background=Transparent`（alpha=0）+ `AllowsTransparency=true` であり、WPF が Layered Window の alpha channel を 0 に設定 → OS の hit test が `HTTRANSPARENT` を返す → マウス入力が透過して WebView2 まで届いていた
- **修正:** オーバーレイ背景を `Color.FromArgb(1, 0, 0, 0)`（alpha=1）に変更。視覚的に透明だが OS hit test では不透過扱いになり、マウスイベントを受け取れる
- 診断用に `tools/Native.cs`（WindowFromPoint / GetWindowLong / EnumWindows 等）、`tools/test_diag.ps1`（Z-order ダンプ）、`tools/test_input.ps1`（WindowFromPoint 検証）を追加
- **4 files changed, +147 / -4**

---

### 6. WS_EX_TRANSPARENT で WebView2 入力を完全ブロック + 復帰ボタン位置修正 + ツール整理

- **commit:** `513f610` — fix: WS_EX_TRANSPARENT blocks WebView2 input in window mode; fix return button position; move tools to tools/
- **6a. WebView2 入力ブロックの根本修正:**
  - 従来のオーバーレイ＋`WM_NCHITTEST` フックだけでは不十分だった
  - WebView2 は `Chrome_RenderWidgetHostHWND` など独立したネイティブ子HWND を作り、これらが直接 OS hit test を受けていた
  - **修正:** ウィンドウモードでメインウィンドウに `WS_EX_TRANSPARENT`（0x00000020）を付与 → 親が transparent になると子ツリー全体が hit test 対象外になる
  - Web モード復帰時はフラグ解除 → WebView2 が通常通り入力を受け取る
  - 実装: `SetMainHitTestTransparent(bool)` メソッドを追加
- **6b. 復帰ボタン位置バグ:**
  - `ShowWebModeButtonWindow()` が `PositionWebModeButtonWindow()` を `Show()` 前に呼んでいた
  - `PositionWebModeButtonWindow()` 内の `Visibility != Visible` ガードが早期 return → 位置がデフォルト（左上）のまま表示されていた
  - **修正:** `ShowWebModeButtonWindow()` 内で `Left` / `Top` を直接設定してから `Show()`
- **6c. ツール整理:**
  - `Native.cs` → `tools/Native.cs` へ移動
  - `test_diag.ps1` / `test_input.ps1` → `tools/` へ移動
  - `tools/test_drag.ps1` を新規作成（ドラッグテスト）
  - `tools/test_toggle.ps1` を新規作成（Web 切替往復テスト）
  - `DGXSparkUtilWidget.csproj` に `<Compile Remove="tools\**\*.cs" />` を追加（tools 配下の .cs がビルドに混入しないよう）
- **7 files changed, +224 / -6**

---

### 7. 一時診断コードの削除 + 最終検証

- **対象:**
  - `MainWindow.xaml.cs` の `OnSourceInitialized` 内にあった `System.Threading.Timer`（DIAG WindowFromPoint ログ、5秒後1回実行）を削除
  - それ専用だった P/Invoke（`NativePoint` struct, `WindowFromPoint`, `GetForegroundWindow`）を削除
  - `tools/test_toggle.ps1` に付いていた一時 Z-order ダンプブロックを削除
  - 一時ヘルパー `tools/dump_section.ps1` / `tools/delete_lines.ps1` を削除
- **検証（最終 exe、`test_toggle.ps1` + `test_drag.ps1`）:**

| テスト | 結果 |
|--------|------|
| ウィンドウモードで Web 入力がブロックされる（`WS_EX_TRANSPARENT=True`, ex=0xC0028） | ✅ |
| `WindowFromPoint` が overlay をヒット（Chrome_RenderWidgetHostHWND ではない） | ✅ |
| ホバーでコントロールバー表示（`MouseEnter → ShowControlBar`） | ✅ |
| ドラッグでメインウィンドウ移動（delta=90,60）+ overlay が追従 | ✅ |
| Web切替（toggle）→ `WS_EX_TRANSPARENT=False` → Web 入力可能 | ✅ |
| 復帰ボタンクリック → `WS_EX_TRANSPARENT=True` → 再び入力ブロック | ✅ |
| DIAG ログが出ない（一時コード削除済み） | ✅ |

---

### 8. ウィンドウ移動後にコントロールバーのアイコンが反応しなくなる問題の修正（2026-08-29）

- **症状:** 起動後にウィンドウをドラッグで移動すると、右上のアイコンが表示されず／反応しない。動かさない限りほぼ正常。
- **再現・解析（`tools/test_hover_after_drag.ps1` 新規作成＋診断ログ追加）:**
  - `WindowFromPoint` による判定で再現: ドラッグ後、バーのウィンドウが存在するのにヒットテストがオーバーレイを返す（＝クリック不能）
  - **欠陥1:** `ShowControlBar()` が `BringToFront` を Hidden→Visible 遷移時のみ実行していた → ドラッグ中にバーの HWND がオーバーレイの下に沈んでも再固定されない
  - **欠陥2:** ドラッグ終了（`CaptureMouse()` の解放＋ウィンドウ移動）後、オーバーレイに対する WPF のマウス enter/leave 追跡が停止する → `ShowControlBar()` が二度と呼ばれずバー自体も再表示されない
  - 当初の「MouseEnter エッジトリガー非対称」仮説はログにより否定（ドラッグ中にも `MouseEnter` は発火していた）
- **修正案:**
  - **Fix 1:** `ShowControlBar()` の `BringToFront` を Visibility ガード外へ移動し毎回実行（`SetWindowPos(HWND_TOP)` は低コスト）
  - **Fix 2:** `Overlay_MouseMove` でドラッグ中でないとき `ShowControlBar()` を呼ぶ（レベルトリガー化: カーソルがウィンドウ内で動いている限り表示。欠陥2の回避にもなる）
  - フェードインアニメーションを現在の opacity から開始に変更（MouseMove からの頻繁な呼び出しでフェードがリセットされるのを防ぐ）
  - Fix 3（`UpdateFloatingWindows()` での Z-order 再固定）は Fix 1+2 で実質カバーされるため今回は見送り
- **テスト計画:**
  1. `tools/test_hover_after_drag.ps1` を再実行 → Phase C / D が `OK (hits bar)` になること（A / B は従来通り正常）
  2. `tools/test_drag.ps1` を実行し、ドラッグ移動・オーバーレイ追従に回帰がないことを確認
  3. 実マウスでの最終確認
- **結果（2026-08-29 実測）:**
  - `tools/test_hover_after_drag.ps1`: **5フェーズすべて OK**（修正前: C=BUG REPRODUCED / D=NG / E=BUG REPRODUCED → 修正後: すべて `OK (hits bar)`）
  - ドラッグ後も enter/leave イベントが正常に発火し、`ShowControlBar: hidden bar -> show` が記録されることを確認
  - `tools/test_drag.ps1`: DRAG WORKS（delta=90,60）+ OVERLAY FOLLOWS main（回帰なし）
  - 診断ログ（overlay/バーの MouseEnter・MouseLeave、ShowControlBar の表示遷移、Hide() 完了）はイベント単位で低ノイズのため**常設として残置**
  - `tools/test_drag.ps1` の `$proj` が旧パス（`d:\SynologyDrive\...`）を指していたため現ワークスペースへ更新
  - **実マウスでの最終確認: 完了**（起動後にドラッグでウィンドウを移動しても右上ホバーでコントロールバーが表示・操作できることをユーザーが確認）

---

### 9. Web操作モード切替後に復帰ボタンが表示されない問題の修正（2026-08-29）

- **症状:** ⚡ ボタンで Web操作モードに切り替えた後、右上に表示されるべき復帰ボタンが見えない。
- **再現・解析（`tools/test_webmode_btn.ps1` 新規作成＋診断ログ `[webmode-btn]` 追加）:**
  - 復帰ボタンは独立 Topmost ウィンドウ（48x48）。位置・表示状態は正常
  - 切替直後は topmost 帯内で復帰ボタンがメインより上（前面）＝見える
  - **Webページをクリックした瞬間にメインウィンドウがアクティベートされ、WPF が Topmost を再アサート（`SetWindowPos(HWND_TOPMOST)`）して topmost 帯内でメインが最前面に上がる** → 復帰ボタンが WebView2 の下に隠れる（`WindowFromPoint` が `Chrome_RenderWidgetHostHWND` を返す）
- **修正:**
  - Webモード中に 400ms 間隔の `DispatcherTimer`（`_webModePinTimer`）を起動し、topmost 帯の Z-order を走査して復帰ボタンがメインより下になっていれば `BringToFront` で再固定（`PinWebModeButtonAboveMain()`）
  - Webモードに入ったらタイマー開始（`StartWebModePinTimer()`）、ウィンドウモードに戻ったら停止（`StopWebModePinTimer()`）
  - 診断ログ: `[webmode-btn]`（復帰ボタンの矩形・Z-order、再固定検出時）
- **テスト計画（ユーザー指定手順）:** 起動 → ウィンドウ移動 → Web操作モード → （Web操作）→ 復帰ボタンクリック → 起動時状態（ウィンドウ移動可）
- **結果（2026-08-29 実測、`tools/test_webmode_btn.ps1`）:**

| フェーズ | 内容 | 修正前 | 修正後 |
|---|---|---|---|
| A | 起動後ドラッグ移動 | OK | OK |
| B | ⚡ クリック → Webモード・復帰ボタン表示確認 | OK（切替直後は前面） | OK |
| C | Webページクリック後・復帰ボタン再確認 | **BUG（メインの下に隠れる）** | **OK（re-pinning で前面維持）** |
| D | 復帰ボタンクリック → ウィンドウモード復帰 | BUG（クリック不能） | OK（`WS_EX_TRANSPARENT=True`） |
| E | 再度ドラッグ → 起動時状態に戻っているか | FAILED | OK |

  - 回帰確認: `tools/test_hover_after_drag.ps1` 全フェーズ OK（#8 の修正に影響なし）
  - **実マウスでの最終確認: 完了**（Web操作モードで Web ページを操作しても復帰ボタンが表示され続け、クリックで戻りウィンドウ移動できることをユーザーが確認）
  - `tools/test_diag.ps1` / `test_input.ps1` / `test_toggle.ps1` の `$proj` が旧パス（`d:\SynologyDrive\...`）を指していたため現ワークスペースへ更新（`D:\WhitebearATOM1\DGXSparkUtilWidget` は `D:\SynologyDrive\WhitebearATOM1\DGXSparkUtilWidget` へのシンボリックリンクで、内容は同一）

---

### 現在の tools/ 構成

```
tools/
├── Native.cs                # P/Invoke ヘルパー（スクリプト用、ビルド対象外）
├── test_diag.ps1            # Z-order / WindowFromPoint 診断
├── test_input.ps1           # 入力遮断検証
├── test_drag.ps1            # ドラッグ移動検証
├── test_hover_after_drag.ps1 # ドラッグ後のホバー再現テスト（#8 で追加）
├── test_webmode_btn.ps1      # Web操作モードの復帰ボタン検証（#9 で追加）
└── test_toggle.ps1          # Web 切替往復検証
```

### 既知の残タスク / リスク

- #8 のレベルトリガー化により、ドラッグ後に WPF のホバー追跡が停止した状態ではカーソルがウィンドウ外に出てもバーが消えない場合がある（見た目の問題のみ・機能には影響なし）
- `WS_EX_TRANSPARENT` は子ツリー全体に効くため、将来メインウィンドウ内にネイティブコントロールを追加する場合は影響を確認する必要がある
