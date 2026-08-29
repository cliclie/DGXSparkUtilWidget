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
├── test_toggle.ps1          # Web 切替往復検証
└── test_resize.ps1          # エッジドラッグリサイズ検証（#10 で追加）
```

### 既知の残タスク / リスク

- #8 のレベルトリガー化により、ドラッグ後に WPF のホバー追跡が停止した状態ではカーソルがウィンドウ外に出てもバーが消えない場合がある（見た目の問題のみ・機能には影響なし）
- `WS_EX_TRANSPARENT` は子ツリー全体に効くため、将来メインウィンドウ内にネイティブコントロールを追加する場合は影響を確認する必要がある

---

## 10. ウィンドウの縦横を可変に（エッジドラッグリサイズ）— 2026-08-29

### 要件
画面の縦横が固定長（800×600）なので可変にしたい。ウィンドウモード・Web操作モードの両方でエッジドラッグによるリサイズを可能にする。

### 実装方針
- `MainWindow.xaml`: `MinWidth="400" MinHeight="300"` を追加（リサイズ下限）。`ResizeMode="NoResize"` はそのままで、OS の NC リサイズではなく**アプリ側がエッジドラッグを検出して矩形を直接更新する方式**を採用
- **ウィンドウモード**: フルオーバーレイ（入力遮断）がメインと同矩形で覆っているため、オーバーレイの MouseDown でカーソル位置からエッジ（左/右/上/下/コーナー）を判定し、ドラッグ中にメインの `Left/Top/Width/Height` を直接更新。`UpdateFloatingWindows()` がオーバーレイ・コントロールバーを追従
- **Web操作モード**: メインは WebView2 入力をそのまま受ける必要があるため、**外側 8px の透明リサイズ帯ウィンドウ4本**（上/下/左/右・独立 Topmost・`WS_EX_NOACTIVATE`）を表示。フルオーバーレイは非表示のまま。中心のクリックは WebView2 に直接届く
  - 帯は alpha=1 の事実上透明ブラシでヒットテスト可能に（#5 で確定した手法と同じ）
  - `ShowResizeBands()` は必ず `Show()` より前に矩形を設定（0×0 のまま表示するとヒット対象にならない）
  - リサイズ中は `PositionResizeBands()` / `PositionWebModeButtonWindow()` で帯・復帰ボタンを新矩形に同期
- **Z-order 維持**: Webモードでは「復帰ボタン > リサイズ帯4本 > メイン」の順を保つ。`PinWebModeButtonAboveMain()`（400ms タイマー）が topmost 帯を走査して違反を検出したら再固定

### テスト中发现した不具合と修正
1. **ピンタイマーがリサイズ帯の Z-order を見ていなかった** — 復帰ボタンの位置だけで走査を打ち切っていたため、「ボタン > メイン > リサイズ帯」の違反を見逃し、エッジのヒットが WebView2 に抜けてリサイズ不能になる（ログに `band MouseDown` が無い・`WindowFromPoint` が WebView2 子HWND を返すことで確認）。修正: topmost 帯を**メインに当たるまで必ず走査**し、復帰ボタンと表示中の帯4本すべてがメインより上であることを検証。さらに `SetWebMode(true)` の末尾で1回同期的に再固定して初期状態を保証
2. **テストスクリプトの帯判定フィルタが不十分** — `test_resize.ps1` Phase E が「高さ ≤ 10px」のみで帯を特定していたため、左右帯（幅8×全高）を見逃し正しくても NG になる。修正: 「幅または高さ ≤ 10px」に変更
3. **`test_webmode_btn.ps1` Phase B の WebToggle クリック座標が古い** — メイン矩形基準の固定座標（`Right-18, Top+20`）でコントロールバー上の ⚡ ボタンに当たらず、Webモードに入れないまま後続フェーズが連鎖失敗していた。修正: `test_resize.ps1` と同じくコントロールバーを動的検出（高さ 24〜40px のアプリウィンドウ）し、5番目ボタン中心（バー左端+162px）をクリック

### テスト結果（tools/test_resize.ps1・全フェーズ OK）
| フェーズ | 内容 | 結果 |
|---|---|---|
| A | 起動 → ウィンドウ移動（回帰）＋オーバーレイ追従 | OK |
| B | ウィンドウモード: 右エッジのヒットがオーバーレイに届く | OK |
| C | 右エッジドラッグで幅 800→900 | OK |
| D | 右下コーナードラッグで幅・高の同時リサイズ（+80/+60） | OK |
| E | Webモード: 中心クリックは WebView2 に透過、右エッジは帯にヒット | OK |
| F | Webモード: コーナーリサイズ＋復帰ボタンの新右上への追従 | OK |
| G | 復帰ボタンクリック → ウィンドウモード復帰（オーバーレイ復帰） | OK |
| H | 再度ウィンドウ移動（起動時状態の回帰） | OK |

- 回帰確認: `test_hover_after_drag.ps1`（#8）・`test_webmode_btn.ps1`（#9）も全フェーズ OK
- **実マウスでの最終確認: 完了**（ユーザー確認。リサイズに問題なしと報告あり）

---

## 11. 見た目の修正（カーソル・背景色・位置・アイコンサイズ）（2026-08-29）

### 要件
1. リサイズ可能領域上でマウスカーソルを変更する（縦 / 横 / コーナーの斜め両方向）
2. ウィンドウ操作モードのコントロールバー表示中は背景を薄い白の透過色にする。非表示時は完全に透明
3. ウィンドウ操作モードのアイコンと Web操作モードの復帰ボタンの位置を揃える
4. 両モードのアイコンサイズを Windows OS のキャプションボタン（46×32px）に合わせる

### 実装内容
| # | ファイル | 変更 |
|---|---|---|
| 1a | `MainWindow.xaml.cs` | ウィンドウモード: `Overlay_MouseMove` でエッジ判定 → `GetResizeCursor()`（新規）で `SizeNS` / `SizeWE` / `SizeNWSE` / `SizeNESW` を設定。エッジ帯外は Arrow 復帰 |
| 1b | 同上 | Webモード: リサイズ帯の MouseMove で `UpdateBandCursor()`（新規）を呼び、角 16px のコーナー領域にいると斜めカーソルに切替（それ以外は従来の上下=SizeNS・左右=SizeWE）。layered window では子要素の既定カーソルが優先されるため、band とその Content（Border）両方に設定 |
| 2 | `ControlBarWindow.xaml` | バックグラウンドを暗色 `#CC1E1E2E` → 薄い白の透過 `#99FFFFFF`。アイコンストロークは可読性のため `#444444`（ホバー背景も黒系へ）。非表示時は既存のフェードアウトで opacity=0（完全に透明）になるため変更不要 |
| 3 | `MainWindow.xaml.cs` | `PositionControlBar()` を復帰ボタンと同じアンカー（右端 56px・上 8px）に統一（従来は右端 64px・上 4px） |
| 4 | `ControlBarWindow.xaml` / `MainWindow.xaml.cs` | ボタンを 32×32 → **46×32** に、バー幅 180 → 250。復帰ボタンウィンドウ 48×48（内側 40×40）→ 50×32（内側 46×32）、位置も右端 56px・上 8px に統一 |

### テスト
- `test_hover_after_drag.ps1` / `test_resize.ps1` / `test_webmode_btn.ps1` を再実行し全フェーズ OK
  - テスト側の前提更新: WebToggle クリック位置（バー左端 +162px → +225px、46px ボタン・余白 2px 換算）、復帰ボタン検出フィルタ（高さ 40〜60px → 28〜40px）
- カーソル画像の変更は WPF の `Cursors` によるためスクリプト検証は不可 → **実マウスでの最終確認: 完了**（ユーザー確認。カーソル・背景色は OK と報告）

---

## 12. アイコン配置・サイズの微調整（2026-08-29）

### 要件
1. コントロールバーの幅が広い → ホバー時にボタン間に余白が出ないよう密接配置
2. Web操作モードの復帰ボタンにもホバー時の背景色変化を追加
3. アイコン自体を縮小し、検知領域（46×32）内で上下左右中央に配置（検知領域は現状維持）
4. アイコンの並びを変更: `最小化 最大化 閉じる 設定 操作モード切替` → **`操作モード切替 設定 最小化 最大化 閉じる`**

### 実装内容
| # | ファイル | 変更 |
|---|---|---|
| 1 | `ControlBarWindow.xaml` | ボタン間マージン `2,0` → `0`、バー幅 250 → **230**（46×5・密接） |
| 2 | `MainWindow.xaml.cs` | `CreateReturnButtonTemplate()` に `IsMouseOver` トリガーを追加しホバー時に背景を薄い白系（`#40FFFFFF`）に変更。復帰ボタンアイコンも濃いグレー（`#444444`）へ |
| 3 | `ControlBarWindow.xaml` / `MainWindow.xaml.cs` | 各アイコンを `Viewbox`（18×18、復帰ボタンは 20×16）で縮小。ContentPresenter の中央配置により検知領域内で上下左右中央に配置される |
| 4 | `ControlBarWindow.xaml` | ボタン順を **WebToggle → Menu → Minimize → Maximize/Restore → Close** に変更 |

### テスト
- WebToggle が先頭ボタンになったため、テスト2本のクリック位置をバー左端 +225px → **+23px**（46/2）に更新
- `test_hover_after_drag.ps1` / `test_resize.ps1` / `test_webmode_btn.ps1` を順に再実行し全フェーズ OK（バー幅 230・復帰ボタン位置・切替動作を確認）
- ホバー色・アイコンサイズはスクリプトでは検証不可 → **実マウスでの最終確認: 完了**（並び・余白・ホバー色・復帰ボタンの見た目を確認済み）

---

## 13. コントロールバー左寄せと復帰バーの統一（2026-08-29）

### 要件
1. コントロールバーを**左寄せ**にする
2. Web操作モードの復帰ボタンを**コントロールバーと同じ幅・位置**にし、復帰ボタンの右に「クリックで戻ります」ラベルを追加

### 実装内容
| # | ファイル | 変更 |
|---|---|---|
| 1 | `MainWindow.xaml.cs` | `PositionControlBar()` を左寄せ（`Left + 8, Top + 8`）に変更 |
| 2 | `MainWindow.xaml.cs` | 復帰ボタンを単体ウィンドウ（50×32・右上）から**復帰バー**（230×32・左上・角丸＋薄い白背景 `#99FFFFFF`、復帰ボタン＋「クリックで戻ります」ラベルの横並び StackPanel）に変更 |
| 3 | `MainWindow.xaml.cs` | `CreateReturnButtonTemplate()` を明るいバー前提のスタイルへ（透明背景・ホバー時だけ黒系ハイライト `#40000000`） |

### テスト中发现した不具合と修正
| # | 内容 | 修正 |
|---|---|---|
| 1 | **アプリ本体バグ**: `PositionWebModeButtonWindow()`（移動/リサイズ時の再配置）が旧の右上寄せ式のままだったため、Webモード中リサイズすると復帰バーが右上へ飛んでいた（初期表示は左上・追従時に右上という非対称） | 再配置式を左寄せ（`Left + 8`）に統一 |
| 2 | テストスクリプトのウィンドウ列挙がクラス名のみで絞り込んでおり、**ツールチップ等の別ウィンドウを復帰バーと誤検出**するケースがあった | 全3本の `GetAppWindows()` に **PID フィルタ**（`GetWindowThreadProcessId`）を追加。バー判定は幅 ≥200px でツールチップを除外 |
| 3 | `test_hover_after_drag.ps1` Phase C〜E がドラッグ後の WPF ホバー追跡停止で不安定になる場合がある | `Show-BarAndMove` に表示ポーリング（最大3秒）＋外→再入場リトライ（最大3回）を追加 |

### テスト
- `test_resize.ps1` / `test_webmode_btn.ps1` / `test_hover_after_drag.ps1` を順に実行し**全フェーズ OK**
  - Phase F: Webモードのリサイズ後も復帰バーが左上（main +8px）に追従
  - Phase B/C: 復帰バーの位置 (main.Left+8, main.Top+8)・前面性・クリック可能を確認
- **既知の制約**: 復帰バー（230×32）は Webページの左上領域を覆うため、その範囲内の Web 要素はクリックできない（要件どおりの見た目とのトレードオフ）
- 実マウスでの最終確認: **未実施**（確認手順: 起動 → コントロールバーが左上に表示されること → ⚡ で Web操作モード → 左上に復帰バー＋「クリックで戻ります」ラベルが表示され、リサイズしても追従すること → クリックで戻れること）

> **注記（第14節で訂正）**: ユーザーの指示ミスにより、本節の「コントロールバー左寄せ」は**右上寄せに戻す**ことになった。復帰バー自体は本節の実装のまま（ただし位置式が右上揃えに更新）。最終状態は第14節を参照すること。

---

## 14. コントロールバー・復帰バーの右上寄せ統一とホバー watchdog（2026-08-29）

### 要件（ユーザー訂正）
- 第13節の「コントロールバー左寄せ」は指示ミス → **右寄せに戻す**
- 復帰バーは第13節の実装のまま（230×32・復帰ボタン＋「クリックで戻ります」ラベル）
- 両バーが**同一位置（右上・8pxオフセット）・同一幅**になり、モード切替で同じ場所のまま切り替わるように見える

### 実装内容
| # | ファイル | 変更 |
|---|---|---|
| 1 | `MainWindow.xaml.cs` | `PositionControlBar()` / `ShowWebModeButtonWindow()` / `PositionWebModeButtonWindow()` を右上寄せ（`Left + Width - 幅 - 8, Top + 8`）に統一。復帰バー内のボタンは左端のまま（＝コントロールバーの ⚡ WebToggle と完全に重なる位置になり、切替時に同じ場所のまま「⚡ → 復帰ボタン＋ラベル」に変化する） |
| 2 | `MainWindow.xaml.cs` | **ホバー watchdog**（`DispatcherTimer` 250ms）を追加。ウィンドウモード中にカーソルの実位置をポーリングし、ウィンドウ内なら `ShowControlBar()`・外なら `HideControlBar()` を補正する |

### watchdog 追加の経緯（テスト失敗の解析）
- 右上寄せ化後の `test_hover_after_drag.ps1` で Phase D/E が失敗（ドラッグ後にカーソルをウィンドウ外→内へ戻してもバーが表示されない）
- ログ解析から原因確定: ドラッグ終了時の nudge リシンクは `SetCursorPos`（瞬間移動）でカーソルを外→内へ動かすが、**WM_MOUSELEAVE は物理的なマウス移動でのみ発生する**ため、WPF のホバー状態が「外にいる」と誤認したまま固定（stuck）になり、以降の MouseEnter/MouseMove が発火しなくなる
- 実マウスでは自然なカーソル移動でリシンクされるためユーザー操作では顕在化しないが、アプリ側の堅牢性としてポーリングで補強（Webモード中は停止）

### テスト
- `test_hover_after_drag.ps1`: Phase A〜E **全 OK**（修正前は D/E 失敗）。バー矩形 (2082,764)-(2312,796) で右上配置を確認
- `test_resize.ps1`: Phase A〜H **全 OK**（Phase F: Webモードリサイズ後も復帰バーが右上に追従）
- `test_webmode_btn.ps1`: Phase A〜E **全 OK**。復帰バー (2232,854)-(2462,886) はコントロールバーと同一位置・同一幅（main.Right-238, main.Top+8 / 230×32）を確認
- テストスクリプトは変更不要（復帰ボタンはバー左端のままのため、クリック位置の「バー左端+23px」が引き続き有効）

### 既知の制約・残タスク
- watchdog のポーリングは 250ms 間隔のため、ホバー応答に最大 250ms の遅れが生じ得る（実マウス操作では WPF イベントが優先して発火するため体感影響なし）
- 実マウスでの最終確認: **未実施**（確認手順: 起動 → コントロールバーが右上に表示されること → ⚡ で Web操作モード → 同じ右上位置に復帰バー＋「クリックで戻ります」ラベルが表示されること → クリックで戻れること）
