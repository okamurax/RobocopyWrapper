# Robocopy Wrapper

Windows の robocopy コマンドを GUI で操作できるラッパーアプリケーションです。

## 機能

- **コピー元/コピー先の指定** - テキスト入力、フォルダ参照ダイアログ、ドラッグ＆ドロップに対応
- **robocopy オプション** - 任意のオプションを自由に指定可能
- **リアルタイム進捗表示** - robocopy の出力を色分けして表示（コピー中/スキップ/エラー等）
- **エラーログ** - エラー行を専用パネルに抽出表示、ダブルクリックでエクスプローラを開く
- **一時停止/再開/中止** - 実行中のrobocopyプロセスを制御可能
- **設定の自動保存** - ウィンドウ位置・サイズ、入力値を次回起動時に復元

## 動作環境

- Windows 10/11
- .NET 8.0

## ビルド

```
dotnet build
```

## 実行

```
dotnet run
```

## 技術詳細

- Windows Forms (.NET 8.0)
- RichTextBox による RTF 一括挿入で高速な色付きログ表示
- ConcurrentQueue + Timer によるバッファリングで UI スレッドの負荷を軽減
- NtSuspendProcess/NtResumeProcess による一時停止/再開
- Shift-JIS (CP932) エンコーディング対応
