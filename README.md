# CraftPlanner

ゲームのアイテム・素材・レシピを登録し、目標アイテムの制作に必要な素材と不足数を、自由に配置できるキャンバス上で整理・計算する Windows アプリです。

![icon](src/CraftPlanner.App/Assets/icon.png)

## 主な機能

- アイテムとレシピの登録・検索（1 アイテムに複数レシピ、素材 0 種類のレシピも可）
- 複数の制作計画をタブで管理。キャンバスに目標アイテムを置き、素材ツリーを自動展開
- 折りたたみ／切断と再接続／レシピの比較と採用／メモノード／Undo・Redo
- 複数目標の需要を合算し、所持数と制作余剰を考慮した不足数を計算（循環レシピも検出）
- 自動保存・手動保存、世代バックアップと復元、JSON / CSV の入出力
- 外部通信なし。アプリ専用フォルダー（`%LOCALAPPDATA%\CraftPlanner`）以外のファイルは、明示的に選んだものだけを読み書き

## ダウンロード

[Releases](../../releases) から `CraftPlanner-vX.Y.Z-win-x64.zip` を取得し、展開して `CraftPlanner.exe` を実行してください。.NET ランタイムのインストールは不要です。

## 使い方

[docs/USER_GUIDE.md](docs/USER_GUIDE.md) を参照してください。設計の詳細は [docs/DESIGN.md](docs/DESIGN.md) にあります。

## 開発

- 必要なもの: .NET 10 SDK（Visual Studio 2022 以降の「.NET デスクトップ開発」でも可）
- ビルド: `dotnet build`
- テスト: `dotnet test`
- 実行: `dotnet run --project src/CraftPlanner.App`

## リリース手順

`v0.1.0` のようなタグを push すると、GitHub Actions がビルド・テスト・配布 zip の作成・Release の公開まで自動で行います。

```bash
git tag v0.1.0
git push origin v0.1.0
```
