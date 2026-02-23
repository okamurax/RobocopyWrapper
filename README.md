# Robocopy Wrapper

Windows の robocopy コマンドを GUI で操作できるラッパーアプリケーションです。

## 機能

- **コピー元/コピー先の指定** - テキスト入力、フォルダ参照ダイアログ、ドラッグ＆ドロップに対応
- **robocopy オプション** - 任意のオプションを自由に指定可能
- **進捗サマリー表示** - コピー/スキップ/EXTRA/エラーのカウントをリアルタイム表示（100ファイルごと）
- **操作結果ログ** - コピー・EXTRA等の実操作をフルパス付きで表示、ダブルクリックでエクスプローラを開く
- **エラーログ** - エラー行を専用パネルに抽出表示、ダブルクリックでエクスプローラを開く
- **一時停止/再開/中止** - 実行中のrobocopyプロセスを制御可能
- **定期実行スケジューラー** - 1〜24時間間隔で自動バックアップ、スリープ復帰にも対応
- **チェックサム検証** - SHA256ハッシュによるコピー元/先の全ファイル整合性チェック（中止可能）
- **タスクトレイ常駐** - ウィンドウを閉じてもバックグラウンドで動作、バルーン通知付き
- **設定の自動保存** - ウィンドウ位置・サイズ、入力値、スプリッター位置を次回起動時に復元

## ダウンロード

[Releases](https://github.com/okamurax/RobocopyWrapper/releases) からダウンロードできます。

| ファイル | サイズ | 説明 |
|---|---|---|
| `RobocopyWrapper-*-win-x64.zip` | ~100 KB | .NET 8.0 Desktop Runtime が必要 |
| `RobocopyWrapper-*-win-x64-selfcontained.zip` | ~63 MB | Runtime同梱、インストール不要 |

## 動作環境

- Windows 10/11
- .NET 8.0 Desktop Runtime（selfcontained版は不要）

## ビルド

```
dotnet build
```

## リリースビルド

```bash
# フレームワーク依存（.NET Runtime 必要）
dotnet publish -c Release -r win-x64 --self-contained false -o ./publish

# 自己完結型（単一EXE、Runtime不要）
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o ./publish-single
```

## 技術詳細

- Windows Forms (.NET 8.0)
- 3パネル構成（進捗ログ / 操作結果 / エラーログ）のネスト SplitContainer レイアウト
- ConcurrentQueue + Timer によるバッファリングで UI スレッドの負荷を軽減
- NtSuspendProcess/NtResumeProcess による一時停止/再開
- Shift-JIS (CP932) エンコーディング対応
- robocopy 出力の日本語/英語両ステータスを正規表現でパース
