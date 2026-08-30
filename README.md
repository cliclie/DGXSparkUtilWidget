# DGXSparkUtilWidget

DGX Spark Utility の Web 画面を Widget 風に表示する Windows デスクトップアプリケーション。

## 概要

- **フレームワーク**: WPF (.NET 9)
- **出力形式**: 自己完結型・単一ファイル実行形式（ランタイム内包の単一 `.exe`）
- **ブラウザエンジン**: WebView2 (Microsoft Edge Chromium)

## 機能

| 機能 | 説明 |
|------|------|
| フレームレス・角丸ウィンドウ | `WindowStyle=None` + `CornerRadius=10` の角丸ウィンドウ |
| 常に最前面 | `Topmost=True` により他のウィンドウの上に表示 |
| ドラッグ移動 | 背景（Border）を左クリックドラッグでウィンドウ移動 |
| ウィンドウ操作モード（既定） | Web ページへの OS 入力を遮断し、ウィンドウのドラッグ移動などが可能 |
| Web 操作トグル | コントロールバーの ⚡ ボタンで Web ページを直接操作可能に。右上の復帰ボタンで戻す |
| フローティングコントロールバー | マウスオーバー時に右上にフェードイン表示（独立 Topmost ウィンドウ） |
| 最小化 / 最大化 / 閉じる | コントロールバーのボタンで操作 |
| 設定ダイアログ | ハンバーガーメニューから URL・透過率の設定 |
| 透過率調整 | 0.2〜1.0 で無段階にウィンドウ全体の不透明度を変更 |
| 設定永続化 | 実行ファイル（.exe）の同じディレクトリに `DGXSparkUtilWidget.json` として保存 |
| 初回起動時の導入手順 | 設定値がない場合は設定ダイアログを自動表示 |

## プロジェクト構成

```
DGXSparkUtilWidget/
├── DGXSparkUtilWidget.sln           # ソリューションファイル
├── DGXSparkUtilWidget.csproj        # プロジェクトファイル
├── App.xaml / App.xaml.cs           # アプリエントリポイント
├── MainWindow.xaml / .xaml.cs       # メインウィンドウ（WebView2 + 入力遮断オーバーレイ）
├── ControlBarWindow.xaml / .xaml.cs # フローティングコントロールバー（独立 Topmost ウィンドウ）
├── SettingsDialog.xaml / .xaml.cs   # 設定ダイアログ
├── images/
│   ├── DGXSpark.ico                 # アプリケーションアイコン
│   └── DGXSpark.png                 # アイコン元画像
├── tools/                           # 診断・テスト用 PowerShell スクリプト（ビルド対象外）
├── develop-log.md                   # 開発ログ
├── specification.md                 # 仕様書
└── README.md
```

## ビルド

### 前提条件

- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- [WebView2 Runtime](https://developer.microsoft.com/en-us/microsoft-edge/webview2/)（Windows 10/11 にプリインストールされている場合が多い）

### 開発用ビルド

```bash
dotnet build
```

### 単一ファイル公開（自己完結型 .exe）

```bash
dotnet publish -c Release -r win-x64
```

公開先: `bin/Release/net9.0-windows/win-x64/publish/DGXSparkUtilWidget.exe`

## 設定ファイル

- **パス**: 実行ファイル（.exe）と同じディレクトリの `DGXSparkUtilWidget.json`
- **形式**:

```json
{
  "Url": "http://192.168.0.110:8080",
  "Opacity": 1.0
}
```

| キー | 型 | 説明 | 既定値 |
|------|----|------|--------|
| `Url` | string | 接続先 URL（http/https） | 空文字列 |
| `Opacity` | double | ウィンドウの不透明度（0.2〜1.0） | 1.0 |

## デバッグログ

- 起動時に `-DEBUG` オプション（大文字小文字を区別しない）を指定すると、デバッグログが `.exe` の同じディレクトリに `DGXSparkUtilWidget.log` として出力されます。

```bash
DGXSparkUtilWidget.exe -DEBUG
```

- 開発ビルド（`bin\Debug\`）で実行する場合は、オプションなしでもデバッグログが出力されます。
- 公開ビルド（`bin\Release\`）で `-DEBUG` を指定しない場合、ログは出力されません。

## 操作ガイド

| 操作 | 方法 |
|------|------|
| ウィンドウ移動 | 背景（暗色部分）を左クリックドラッグ |
| 最小化 | 右上コントロールバーの `─` ボタン |
| 最大化 / 元に戻す | 右上コントロールバーの `□` / `❐` ボタン |
| 閉じる | 右上コントロールバーの `✕` ボタン |
| 設定変更 | 右上コントロールバーの `≡` ボタン |
| Web ページを操作する | 右上コントロールバーの ⚡ ボタン（Web 操作モード） |
| ウィンドウ操作に戻る | Web 操作モード中表示の右上復帰ボタン |

