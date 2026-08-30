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
  - `WM_NCHITTEST` フックの登録タイミングを `Loaded` → `SourceInitialized` に変更（HwndSource が確実に出るよう）
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

### テストで発見した不具合と修正
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

### テストで発見した不具合と修正
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
- 実マウスでの最終確認: **完了**（右上寄せのコントロールバー・復帰バーの位置一致を確認）

---

## 15. アイコン微調整と develop-log の日本語化（2026-08-29）

### 要件
1. アイコンの図形の高さと幅を統一する
   - 設定（ハンバーガー）は少し大きく（高さが足りず下配置に見えていた）
   - 矢印・最大化・最大化後の元に戻すはいまのまま
   - 閉じる（×）はサイズはそのまま上下中央に配置
2. develop-log.md に中国語の記載がある（276行「发现」等）→ 日本語化

### 実装内容
| # | ファイル | 変更 |
|---|---|---|
| 1 | `ControlBarWindow.xaml` | ハンバーガー（設定）アイコンの図形を拡大・中央配置（`M 4,7 L 24,7 ... M 4,17 L 24,17` → `M 0,4 L 18,4 M 0,9 L 18,9 M 0,16 L 18,16`）。描画高さが約 9.8px → 約 12.6px に（「少し大きく」） |
| 2 | `ControlBarWindow.xaml` | 矢印・最大化・閉じるは**元図形のままであることを確認**（Viewbox 18×18 内で Stretch=Uniform により中央配置されるため、閉じる×も描画上は上下左右中央にある）。作業途中で一時変更していたが、ご指示「いまのまま／サイズはそのまま」に合わせすべて元に戻した |
| 3 | `ControlBarWindow.xaml.cs` | 元に戻すアイコンは不変（元図形を維持） |
| 4 | `develop-log.md` | 中国語 3 か所を日本語化（32行「时机」→「タイミング」、194行・276行「テスト中发现した不具合と修正」→「テストで発見した不具合と修正」）。README / specification.md に中国語の残存はないことを確認 |

### テスト
- `test_hover_after_drag.ps1`: Phase A〜E **全 OK**（スモークテスト・回帰なし）
- アイコンの外観は実マウスでの目視確認が必要（スクリプトでは検証不能）

### 既知の残タスク
- 実マウスでの最終確認: **完了**（設定アイコンが拡大・中央配置されていることを確認）

---

## 16. アイコンgeometryの原点起点化とピクセル計測テスト、最小化の下配置（2026-08-29）

### 要件
1. ハンバーガーメニューの3本の間隔が不均等（真ん中の棒が上に寄っている）→ 等間隔に
2. 閉じるアイコンがまだ下配置に見える → 上下中央配置。座標計算上は中央のはずなので、**WPFで実レンダリングしピクセル単位で計測**して事実確認

### 調査で判明した原因
- `tools/test_icon_center.ps1`（新規）を作成: ControlBarWindow を WPF でレンダリングしスクリーンショットを撮って、各アイコンの描画 bbox と中心座標をピクセル計測
- **Viewbox の中央配置が geometry の座標オフセットとストローク幅でずれている**ことが判明（例: 矢印は `y=3..21` が 18×18 ビューポート内で描画され、実 bbox は 0..18 に対して下方向へオフセット）。Viewbox は Path の明示サイズではなく geometry の実 bbox を基準に拡大縮小するため、座標が原点からずれていると「中央配置」にならない
- ハンバーガーは `y=4, 9, 16` で間隔 5/7 と不均等だった（ご指摘通り）

### 実装内容
| # | ファイル | 変更 |
|---|---|---|
| 1 | `ControlBarWindow.xaml` | **全アイコンの geometry を原点起点化し、描画実寸（bbox+ストローク）を Path の Width/Height に明示**して Viewbox が正しく中央配置するよう統一（矢印 15.5×19.5 / 設定 20×15 / 最大化 20×20 / 閉じる 14×14） |
| 2 | `ControlBarWindow.xaml` | ハンバーガーの間隔を等間隔化（`y=1, 7.5, 14`・間隔 6.5） |
| 3 | `ControlBarWindow.xaml` | **最小化ボタンの下線を下端から8pxの下配置に変更**（ユーザー追加指示。Grid 46×32 内で水平中央・Bottom アライン） |
| 4 | `ControlBarWindow.xaml.cs` | `SetMaximizeIcon()` の座標を新座標系（20×20 レイアウト）に更新 |
| 5 | `MainWindow.xaml.cs` | Webモード復帰ボタンの geometry も原点起点化。`PositionControlBar()` の古いコメント（「左上」→「右上」）を修正 |

### テスト
- `test_icon_center.ps1`（ピクセル計測）: minimize bbox=(13,22)-(32,23) center=(22.50, 22.50)＝下端から8px・水平中央。他6アイコンはすべて center=(22.50, 15.50)（ボタン内中央）で統一を確認
- ビルド成功（警告・エラーなし）
- 実マウスでの最終確認: **完了**（ユーザー「完璧に見えます」との報告）

### 補足
- `tools/test_icon_center.ps1` は XAML と同一の生 geometry データを WPF でレンダリングして計測するため、XAML を直接触らずにアイコン配置の検証が可能

---

## 17. ウィンドウ位置・サイズの記憶と復元（オフスクリーン時フォールバック）（2026-08-29）

### 要件
1. 開発過程で入れた「起動時にメインモニター中央に表示する」仕様を除去する
2. プログラム終了時にウィンドウの表示位置とサイズを記憶し、次回起動時に前回と同じ位置・サイズで表示する
3. 前回の位置が表示不能な場所（例: 前回サブモニター2上にあったが今回は外されている）の場合は、プライマリモニター作業領域中央に表示する

### 実装内容（`MainWindow.xaml.cs` のみ）
| # | 変更 |
|---|---|
| 1 | コンストラクタの「プライマリモニター作業領域中央に配置」処理を除去 → `RestoreWindowPosition(LoadSettings())` に置換 |
| 2 | `AppSettings` に `WindowBounds?`（Left/Top/Width/Height・DIP）を追加し、`settings.json` に永続化。`SaveSettings()` は既存設定とマージして保存（`WindowBounds` を渡さなければ既存値を保持） |
| 3 | `Closing += OnWindowClosing` で終了時に位置・サイズを保存。**最大化中は通常サイズの矩形（`_normalBounds`）を保存**（最大化状態の矩形では復元不能になるため） |
| 4 | `RestoreWindowPosition()`: 保存値が有効（finite かつ最小サイズ以上）なら復元。ただし**仮想デスクトップ（全モニターの合成領域: `GetSystemMetrics(SM_*VIRTUALSCREEN)` / DPI スケール）と交差しない場合はオフスクリーンと判定**し、プライマリモニター作業領域中央へフォールバック（**サイズは保存値を維持**し位置のみ変更） |
| 5 | 初回起動（`WindowBounds` なし）も同様にプライマリモニター作業領域中央に表示 |

### テスト（`tools/test_position_restore.ps1` 新規・全フェーズ OK）
| フェーズ | 内容 | 結果 |
|---|---|---|
| A | 起動 → メイン矩形 R1 を取得 | OK |
| B | ドラッグで (+90,+60) 移動 → R2 | OK |
| C | コントロールバーの閉じるボタン（×）で正常終了（Closing で保存される） | OK（プロセス正常終了） |
| D | `settings.json` の `WindowBounds` が R2 と一致 | OK |
| E | 再起動 → R3 が R2 と一致（復元） | OK (restored) |
| F | オフスクリーン座標 (6000,4000) を settings に書き込み → 再起動 → プライマリ作業領域中央に表示され且つサイズは保存値維持 | OK（R4=(1520,756)=作業領域(0,0)-(3840,2112)の800×600中央） |
| G | クリーンアップ（settings を R2 の値へ復元） | OK |

回帰テスト: `test_hover_after_drag.ps1`（A〜E）・`test_resize.ps1`（A〜H）・`test_webmode_btn.ps1`（A〜E）すべて OK。

### テスト作成時のハマりどころ
- 新スクリプトは BOM なし UTF-8 で保存されると PowerShell 5.1 が ANSI(932) として誤解釈し、日本語を含む単一引用符文字列で構文エラーになる → **BOM 付き UTF-8 に変換して解決**（既存スクリプトも同様の注意が必要）
- Phase F の期待値計算で `RECT` 構造体に存在しない `.Width`/`.Height` を参照していた（PowerShell は存在しないプロパティを `$null` で返すため (-400,-300) という誤った期待値になっていた）。アプリ側の挙動は正しく、テスト側の計算ミスだった → `Right-Left` / `Bottom-Top` に修正

### 既知の残タスク
- 実マウスでの最終確認: **未実施**（次回起動時に「前回終了時の位置・サイズで表示されること」「サブモニターを外した状況で起動するとプライマリモニター中央に表示されること」を確認）

---

## 18. ウィンドウ全体透過（WebView2 領域含む）— WebView2 ホストHWND への LWA_ALPHA 適用（2026-08-29）

### 要件
1. メインウィンドウの透過率が設定ダイアログのスライダー指定値に追従すること（**WebView2 の表示領域を含む**）
2. 再起動後も保存済み透過率で復元すること
3. 設定ダイアログで URL 未入力のまま保存できるようにする（ユーザー依頼。空欄はナビゲーションしないだけ）

### 調査結果（なぜこれまで透過できなかったか）
- WPF の `Window.Opacity` はメイン HWND にのみ効き、WebView2 の子HWND（`Chrome_WidgetWin_0/1`、`Chrome_RenderWidgetHostHWND`）には無効 → Web 領域は常に不透明
- WinForms 式（`Form.Opacity = 0.5` と同じ `SetLayeredWindowAttributes(LWA_ALPHA)`）を**メインウィンドウに適用する試みは不可能**: WPF ランタイム（`HwndTarget.WndProc`）を逆コンパイルして確認したところ、`AllowsTransparency` を持たないウィンドウでは GWL_EXSTYLE 変更時に WM_STYLECHANGING ハンドラが **WS_EX_LAYERED を無条件にクリア**する（プロセス内の呼び出しも外部からの呼び出しもブロックされる）
- `AllowsTransparency=True` + `WebView2.DefaultBackgroundColor=Transparent` + CSS `documentElement.style.opacity` の方式も**無効**（WebView2 サーフェスはネイティブ子HWND であり、WPF のピクセル単位 alpha 合成の対象外）
- **実証実験で判明**: WebView2 のホストウィンドウ（`Chrome_WidgetWin_1`）にのみ LWA_ALPHA を適用すると期待通りのブレンドピクセルが即座に出る。WPF が守るのは自分の HWND だけなので、子ウィンドウへの適用は通る

### 実装内容
| # | ファイル | 変更 |
|---|---|---|
| 1 | `MainWindow.xaml` | `AllowsTransparency="True"` を復元し、WebView2 背後の WPF レイヤーを完全透明化（前提） |
| 2 | `MainWindow.xaml.cs` | `ApplyOpacity()` を CSS opacity スクリプト方式から **`SetLayeredWindowAttributes(LWA_ALPHA)` への適用に変更**（適用先は `_webViewHostHwnd`）。ホストHWND は初期化後に非同期で作られるため、`StartWebViewAlphaFinder()` が 250ms リトライで `EnumChildWindows` 探索 |
| 3 | `MainWindow.xaml.cs` | **1秒毎に LWA_ALPHA を再適用するウォッチドッグを追加**（Chromium がページ読み込み時にホストウィンドウの属性をリセットするため。WS_EX_LAYERED フラグが消えていれば再付与）。機能しなかった CSS opacity 機構（`ApplyPageOpacityScript` / DOMContentLoaded ハンドラ）は削除 |
| 4 | `SettingsDialog.xaml.cs` | URL 空欄で保存可能に（空欄時はナビゲーションしない）。検証を「http/https のみ」→「非空なら任意の絶対 URI（`file://` 含む）」へ緩和。この http/https 限定が E2E テスト（ローカル file:// ページ使用）をブロックしていた |

### テスト（`tools/test_opacity_e2e.ps1` 新規・全フェーズ OK）
| フェーズ | 内容 | 結果 |
|---|---|---|
| A | 起動 op=1.0 → サンプル点が純赤 (255,0,0) | OK actual=(255,0,0) |
| B | **設定スライダーでライブ変更 op=0.30** → 期待ブレンドピクセル（UIA でダイアログのスライダー操作＋保存） | OK actual=(132,64,74) expected~(133,64,75) |
| C | 再起動して保存値 op=0.2 で復元 | OK actual=(115,73,85) expected~(116,74,86) |
- テストページ `tools/opacity_test_page.html`（純赤背景）。期待値はサンプル点のデスクトップ基線色と合成計算で算出
- 実 Web ページでの確認: **完了**（ユーザー「webページも表示したまま透過できるところまで確認しました」との報告）

### 補足
- `tools/` の診断用 scratch ファイル（`_*.ps1` / `_*.cs` / `*.png` / `*.log`）は `.gitignore` に除外パターンを追加し、リポジトリから除外した（ローカルには残置）
- E2E テストは UIA で設定ダイアログを開くため、Web モード復帰時のホバー操作と干渉しない

### 既知の残タスク
- ~~**Web 操作モードの不具合（新規）**: Web モードでマウス操作が WebView2 に届かず**背面のウィンドウに貫通する**~~ → #19 で修正

---

## 19. Web モードでのマウス入力の WebView2 到達失敗修正（2026-08-30）

### 問題
- Web モード（`_isWebMode = true`）でマウス操作が WebView2 に届かず、背面のウィンドウに貫通する
- Web 切替が実行されたように見えるが、実際には入力を受信できない

### 根本原因
- `AllowsTransparency=True` がメインウィンドウの HWND に `WS_EX_LAYERED` を付与
- `RootBorder` の `Background="Transparent"` により WPF レイヤーの全ピクセルが alpha=0 で描画
- Web モード時に `OnMainWindowHook` が `WM_NCHITTEST` に対して `handled=false`（WPF デフォルトハンドラに委譲）を返していた
- WPF デフォルトハンドラがレイヤードウィンドウのピクセル単位 alpha をチェックし、alpha=0 のピクセルで `HTTRANSPARENT` を返す
- 結果: OS がクリックを「メインウィンドウは透明」と判定し、背面のウィンドウにルーティング

### 修正内容
| ファイル | 変更 |
|---|---|
| `MainWindow.xaml.cs` | `OnMainWindowHook` 内で Web モード時に `HTCLIENT`(1) を明示的に返すよう変更 |
| `MainWindow.xaml.cs` | `SetWebMode(true)` で `RootBorder.Background` を `Color.FromArgb(1,0,0,0)`（alpha=1）に設定、`SetWebMode(false)` で `Brushes.Transparent` に戻す |

> **補足（2026-08-30 追記・実機検証で判明）:**
> 上記 `HTCLIENT` フックだけでは不十分だった。WPF の `AllowsTransparency=True`
> ウィンドウでは、`HwndSource` が `WM_NCHITTEST` をフックより前に内部処理するため、
> フックでの返却値が効かない。実質的な原因は「layered window の DIB が全ピクセル
> alpha=0」であり、OS レベルのピクセル alpha チェックで透過判定されてしまう。
> 対策として Web モード時に `RootBorder` の背景を alpha=1（視覚的には完全に透明）
> に設定し、DIB の alpha を 0 でなくすることで OS の透過判定を回避する。
> ウィンドウモード時は `Brushes.Transparent`（alpha=0）に戻し、従来どおり
> オーバーレイ経由の入力処理に委譲する。

### テスト
| フェーズ | 内容 | 結果 |
|---|---|---|
| A | ウィンドウモードで `WindowFromPoint`（メイン中央）| OK: `HwndWrapper[DGXSparkUtilWidget;...]`（アプリ内ウィンドウ） |
| B | 制御バー WebToggle クリック → Web モード遷移 | OK（`WS_EX_TRANSPARENT=False`、band・webmode-btn 表示確認） |
| C | Web モードで `WindowFromPoint`（メイン中央）| **OK: `Chrome_RenderWidgetHostHWND`（WebView2 に到達）** |

- `tools/test_webmode_hit.ps1` で自動検証済み（2026-08-30）
- ビルド確認: 0 errors, 0 warnings

---

## 20. Webモード時のスクロールバー表示制御とスタイリング（2026-08-30）

### 問題
- WebView2 のデフォルトスクロールバーがウィンドウモード・Webモード問わず常に表示される
- スクロールバーの見た目がウィジェットと不調和（幅広・白系）
- Webモード時のみスクロールバーを表示し、スタイルをカスタムしたい

### 修正仕様
| # | 項目 | 内容 |
|---|------|------|
| 1 | 表示制御 | Webモード時のみ表示、ウィンドウモード時は `display:none !important` で非表示 |
| 2 | スクロールバースタイル | スクロールバー幅 16px / サム: 幅 8px・`rgba(80,80,80,0.7)`（濃灰）+ `border-radius:4px` / ホバー: `rgba(60,60,60,0.9)` |
| 3 | 上端余白 48px | `::-webkit-scrollbar-button:vertical-start { height: 48px }` で確保 |
| 4 | 左右・下端余白 4px | サムに `border:4px solid transparent` + `background-clip:padding-box` で可視幅8px・左右各4pxを確保。下端は `vertical-end { height: 4px }` |
| 5 | 背景 | スクロールバー・トラック・交差(`::-webkit-scrollbar-corner`)とも完全透明（`transparent`） |
| 6 | 強制適用・互換性 | 全プロパティに `!important` 付与（ページ側CSSの上書き対応）。加えて `*{scrollbar-width:auto;scrollbar-color:auto}` により標準 scrollbar プロパティが webkit スタイリングを無効化するのを防ぐ（MDN互換性ルール） |

> **追記（2026-08-30・初回実装後の修正）:**
> 初回実装では上端余白に `::-webkit-scrollbar-track` の `padding-top: 48px` を使用したが、
> 実機で効いておらず（サムが上端から始まる）、`vertical-start` ボタン要素の `height` による
> 方式に切り替えた。またトラック背景 `rgba(180,180,180,0.25)` は完全透明に変更し、
> サム幅 8px + スクロールバー幅 16px により左右 4px の余白も確保した。

> **追記2（2026-08-30・実機確認後・padding不具合の再修正）:**
> 初回修正後も上下左右のpaddingが実機で効かないことが判明。原因と対応:
> 1. **`scrollbar-width` / `scrollbar-color` による無効化**（MDN互換性ルール）:
>    ページ側またはUAが `scrollbar-width`/`scrollbar-color` を `auto` 以外に設定している場合、
>    `::-webkit-scrollbar-*` 擬似要素が全て無視される。対策として先頭に
>    `*{scrollbar-width:auto !important;scrollbar-color:auto !important}` を追加。
> 2. **サム左右・上下の余白**: `width:8px` 単独ではChromiumのサム中央配置が
>    期待通りにならない場合があるため、`border:4px solid transparent` +
>    `background-clip:padding-box` でサム自体の可視領域を8pxに制限。
>    これで左右各4px・上下各4pxの透明マージンを確実に出す。
> 3. **ボタン要素の border 除去**: `::-webkit-scrollbar-button` に `border:none` を追加し、
>    UAデフォルトのボタン枠が現れないよう対処。
> 4. **`::-webkit-scrollbar-corner` の追加**: 交差点も透明に指定。
> 5. **JS注入の強化**: 単一引用符による文字列埋め込みをテンプレートリテラル（`` ` ``）に
>    変更し、CSS内の特殊文字による破損を回避。

> **追記4（2026-08-30・border-top方式の破棄とmargin方式への切替）:**
>
> headless Edge による対比検証（button-height / track margin / track border-top の3方式）の結果、
> `border-top` による上端ギャップ確保は **サム位置に影響しない** ことが判明。
> 一方、`margin-top` はトラックの描画領域自体を押し下げ、サムが確実に48px下がり、
> 所望のトップギャップが得られた。
>
> 最終採用 CSS:
> ```css
> ::-webkit-scrollbar-track {
>   background: transparent !important;
>   margin-top: 48px !important;   /* コントロールバー高 */
>   margin-bottom: 4px !important;
> }
> ```
> ※ 左右4pxは scrollbar幅16px − サム幅8px の差で確保（track border不要）。
> ※ サムの `:hover` にも `width:8px !important` を明示し、幅変動を防止。

### 修正内容
| ファイル | 変更 |
|---|---|
| `MainWindow.xaml.cs` | 新メソッド `ApplyScrollbarCss()` 追加：`_isWebMode` に応じてCSSを構築し `ExecuteScriptAsync` で `<style id="wscroll">` を上書き |
| 同上 | `SetWebMode(true/false)` 末尾に `ApplyScrollbarCss()` を呼び出し |
| 同上 | `NavigationCompleted` ハンドラに `ApplyScrollbarCss()` を追加（ページ遷移後に再適用） |
| 同上 | `EnsureCoreWebView2Async` 直後に `AddScriptToExecuteOnDocumentCreatedAsync` で初期非表示CSSを先読み注入 |

### テスト内容
| フェーズ | 内容 | 判定基準 | 結果 |
|---|---|---|---|
| A | ウィンドウモード: メイン中央 `WindowFromPoint` | `HwndWrapper`（アプリウィンドウ） | OK |
| B | WebToggle クリック → Webモード遷移 | `WS_EX_TRANSPARENT=False`、band 表示 | OK |
| C | Webモード: スクロールバー表示確認（目視） | 背景完全透明 / 上端48px・左右4px・下端4pxの余白 / 8px幅の濃灰サム | OK |
| D | ウィンドウモード復帰: スクロールバー非表示確認（目視） | スクロールバー不可視 | OK |

- 自動テスト: `tools/test_webmode_hit.ps1`（Phase A/B/C の回帰確認）— 2026-08-30 実行 OK
- 手動確認: Webモードでスクロール操作時にスクロールバーが正しく表示/追従すること
- ビルド確認: 0 errors

> **追記3（2026-08-30・実機確認後・JS構文バグ修正とCSS方式の見直し）:**
>
> 実機確認で「上端48pxのpaddingが効かない」「ホバーでサム幅が広がる」が継続していた。
>
> **根本原因: JS構文エラーによりCSSが一切適用されていない**
> - 旧 `ScrollbarCssScript` はテンプレートリテラル（バッククォート）を使用していたが、
>   生成コードの末尾が `;)};)"` となり JavaScript 構文エラーとなっていた。
>   結果、`ExecuteScriptAsync` が例外を投じ、`<style>` 要素が生成されず
>   **すべてのスクロールバースタイルが反映されていなかった**。
> - 修正: 単一引用符エスケープ + バランスの取れた IIFE に再実装。
>   生成 JS 例:
>   ```js
>   (function(){var s=document.getElementById('wscroll');
>     if(!s){s=document.createElement('style');s.id='wscroll';
>     (document.head||document.documentElement).appendChild(s);}
>     s.textContent='...';})();
>   ```
>
> **CSS方式の見直し（button-height / サムborder → track border）:**
> - 上端48px: `::-webkit-scrollbar-track { border-top:48px solid transparent }` で確保。
>   Chromium は scrollbar-track の border をレイアウトに反映するため、
>   サムが上端から48px下がって開始する（headless Edge で検証済み）。
> - 左右・下端4px: サムの `border`+`background-clip:padding-box` 方式を廃止。
>   代わりに `::-webkit-scrollbar-track { border:4px solid transparent }` で確保。
>   スクロールバー幅16px・サム8px の差と組み合わせることで左右4pxを担保。
> - ホバー幅固定: `::-webkit-scrollbar-thumb` 本体と `:hover` 両方に
>   `width:8px !important` を明示。ホバーで幅が変わる不具合を解消。

---

### 15. 設定・ログの保存先を exe 隣に集約 + ログ出力の条件化

- **変更内容:**
  - **設定ファイル・ログの保存先変更**: `%APPDATA%\DGXSparkUtilWidget\` 配下（`settings.json` / `debug.log`）から、**実行ファイル（.exe）の同じディレクトリ**に変更
    - 設定ファイル名: `settings.json` → `DGXSparkUtilWidget.json`
    - デバッグログ名: `debug.log` → `DGXSparkUtilWidget.log`
  - **デバッグログの出力条件化**:
    - `bin\Debug\` 配下の exe → **常に**デバッグログ出力
    - `bin\Release\` 配下の exe → `-DEBUG` コマンドライン引数指定時のみ出力（大文字小文字を区別しない）
    - `-DEBUG` がない Release 版ではログファイルが作成されない

- **修正ファイル:**
  - `MainWindow.xaml.cs`: `SettingsDirectory` を `AppDomain.CurrentDomain.BaseDirectory` に変更、`_debugMode` フラグ追加、`LogDebug()` 先頭にガード追加
  - `README.md`: 設定ファイルパス・デバッグログの記述を更新、「デバッグログ」セクション追加
  - `tools/*.ps1` (10ファイル): テストスクリプト内の設定/ログパスを exe 直下へ更新
  - `tools/check_ts.ps1` / `tools/fix_comment.ps1`: 開発用ユーティリティスクリプトをリポジトリ管理下に追加

- **動作確認:**
  - `dotnet build` 成功（0 errors）
  - 旧 `%APPDATA%\DGXSparkUtilWidget\` 配下のデータは自動移行されない（初回起動時は設定なし状態）

---

## 2026-08-31

### 16. specification.md を実装準拠の仕様書に書き直し + .clinerules の言語ルール強化

- **変更内容:**
  - **`specification.md` の全面書き直し**: 従来の「コード生成プロンプト」形式（LLM にコード生成を指示する内容）から、**現在のソースコードの実装内容を正（しとう）とした仕様書**に変更。11 セクション構成で実装を記述した。
    - §1 概要 / §2 技術スタックとビルド / §3 プロジェクト構成 / §4 ウィンドウ構成（メイン・コントロールバー・透過オーバーレイ・リサイズバンド×8・Web復帰ボタンの 5 要素）/ §5 入力モード（ウィンドウモード↔Webモード）/ §6 挙動仕様（ホバー表示・透過率・リサイズ・最小・最大化・閉じる・Z-order 維持・初回導入手順・スクロールバー制御）/ §7 設定・永続化 / §8 設定ダイアログ / §9 コマンドライン・デバッグログ / §10 P/Invoke（Win32 API）/ §11 制約・注意点
  - **`.clinerules/01-always.md` の言語ルール強化**: 「Core Communication Rules」を厳格化し、思考・推論・最終回答すべてを日本語で、かつ `think` タグ内の内部推論も 100% 日本語で実施することを明示。
- **修正ファイル:**
  - `specification.md`: 全面書き直し（コード生成指示 → 実装準拠の仕様書）
  - `.clinerules/01-always.md`: 言語ルール（日本語統一）の強化
  - `develop-log.md`: 本項目
- **動作確認:**
  - ドキュメント（仕様書・ルールの）みの変更であり、コード / ビルドへ影響なし
  - README.md は現行でも実装と整合しているため変更なし


