# Editor

`Editor` は、WPF で作られた Vim ライクなテキストエディタです。  
コアロジックと UI を分離した 3 層構成になっており、Vim 操作の多くを `Editor.Core` で処理します。

## 主な機能

- Normal / Insert / Visual / Visual Block モード
- ドットコマンド (`.`)、レジスタ、マーク、マクロ
- `:substitute`、コマンド履歴、`.vimrc` / `_vimrc` 読み込み
- シンタックスハイライト（複数言語対応）
- LSP 連携（補完、ホバー、診断、定義ジャンプ、シグネチャヘルプ、フォーマット）
- マルチバッファ、タブ、基本的なウィンドウ分割
- Quickfix（`:copen` / `:cclose` / `:cn` / `:cp` / `:cc N` / `:cl`）
- コードフォールド（`za` / `zc` / `zo` / `zR` / `zM` / `zf`）

## 動作環境

- Windows 10/11（WPF アプリ）
- .NET SDK 9.0 以上

## ビルド・実行

```bash
# ソリューション全体をビルド
dotnet build Editor.sln

# アプリ起動
dotnet run --project src/Editor.App/

# 指定ファイルを開いて起動（引数1つ目をファイルパスとして解釈）
dotnet run --project src/Editor.App/ -- "C:\path\to\file.cs"
```

## テスト

```bash
# Core テストを実行
dotnet test tests/Editor.Core.Tests/
```

## アーキテクチャ

依存方向は次の通りです。

```text
Editor.App -> Editor.Controls -> Editor.Core
Editor.Core.Tests -> Editor.Core
```

- `src/Editor.Core`  
  WPF 非依存の Vim エンジン、バッファ、Ex コマンド、シンタックス、フォールドなどの純ロジック層
- `src/Editor.Controls`  
  `VimEditorControl` / `EditorCanvas` を含む WPF コントロール層
- `src/Editor.App`  
  メインウィンドウ、タブ管理、ファイルツリーなどのホストアプリ層
- `tests/Editor.Core.Tests`  
  `Editor.Core` のユニットテスト

## LSP について

LSP サーバー本体は同梱していません。必要なサーバーを個別にインストールしてください。  
現在のマッピング例（拡張子 -> サーバー）:

- `.cs` -> `csharp-ls`
- `.py` -> `pylsp`
- `.ts` / `.js` -> `typescript-language-server`
- `.rs` -> `rust-analyzer`
- `.go` -> `gopls`
- `.c` / `.cpp` -> `clangd`
- `.lua` -> `lua-language-server`
- `.md` / `.markdown` -> `marksman`

## 参考

- 今後の実装計画と実装済み機能: `ROADMAP.md`
