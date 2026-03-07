# Editor

`Editor` は、WPF で作られた Vim ライクなテキストエディタです。
コアロジックと UI を分離した 3 層構成になっており、Vim 操作の多くを `Editor.Core` で処理します。

## 主な機能

- Normal / Insert / Visual / Visual Block / Visual Line モード
- ドットコマンド (`.`)、レジスタ、マーク、マクロ
- `:substitute`、コマンド履歴、`.vimrc` / `_vimrc` 読み込み
- シンタックスハイライト（複数言語対応）
- LSP 連携（補完、ホバー、診断、定義ジャンプ、参照検索、シグネチャヘルプ、フォーマット、リネーム、コードアクション）
- マルチバッファ、タブ、ウィンドウ分割（`:split` / `:vsplit` / `Ctrl+W` プレフィックス）
- Quickfix（`:copen` / `:cclose` / `:cn` / `:cp` / `:cc N` / `:cl`）
- コードフォールド（`za` / `zc` / `zo` / `zR` / `zM` / `zf`）
- サラウンド操作（`ys` / `cs` / `ds`）
- コメントトグル（`gc` / `gcc`）
- Git 差分表示・ブレームアノテーション（`:Git blame`）
- スペルチェック（`set spell`、`]s` / `[s` / `z=`）
- 統合ターミナル（`:terminal`）
- セッション管理（`:mksession` / `:source`）
- テキスト整形（`gq{motion}` / `gqq`）

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

# リリースビルド
dotnet build Editor.sln -c Release
```

## テスト

```bash
# Core テストを実行
dotnet test tests/Editor.Core.Tests/

# 特定テストを実行
dotnet test tests/Editor.Core.Tests/ --filter "FullyQualifiedName~VimEngineTests.DD_DeletesLine"
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

## LSP キーバインド

| キー | モード | 動作 |
|------|--------|------|
| `K` | Normal | ホバー情報をステータスバーに表示 |
| `gd` | Normal | 定義にジャンプ |
| `gr` | Normal | 全参照を検索（References パネル） |
| `ga` | Normal | コードアクション（j/k/Enter/Esc で選択） |
| `F2` | Normal | シンボルのリネーム |
| `Ctrl+Space` | Insert | 補完トリガー |
| `↓`/`Ctrl+N`、`↑`/`Ctrl+P` | Insert+補完 | 補完候補を移動 |
| `Tab`/`Enter` | Insert+補完 | 選択候補を挿入 |
| `:Format` | コマンド | LSP でドキュメントをフォーマット |
| `:Rename [name]` | コマンド | LSP リネーム（名前省略時はダイアログ） |

## LSP 対応サーバー

LSP サーバー本体は同梱していません。必要なサーバーを個別にインストールしてください。

| 拡張子 | サーバー |
|--------|----------|
| `.cs` | `csharp-ls` |
| `.py` | `pylsp` |
| `.ts` / `.js` | `typescript-language-server` |
| `.rs` | `rust-analyzer` |
| `.go` | `gopls` |
| `.c` / `.cpp` | `clangd` |
| `.lua` | `lua-language-server` |
| `.md` / `.markdown` | `marksman` |

## ウィンドウ分割

| コマンド | 動作 |
|----------|------|
| `:split` / `:sp` | 水平分割 |
| `:vsplit` / `:vs` | 垂直分割 |
| `Ctrl+W w` | 次のウィンドウへ |
| `Ctrl+W q` | ウィンドウを閉じる |
| `Ctrl+W h/j/k/l` | 方向を指定してウィンドウ移動 |
| `Ctrl+W v` | 垂直分割 |
| `Ctrl+W s` | 水平分割 |

## サラウンド操作

| コマンド | 動作 |
|----------|------|
| `ys{motion}{char}` | モーション範囲を囲む |
| `cs{old}{new}` | 囲み文字を変更 |
| `ds{char}` | 囲み文字を削除 |

## コメントトグル

| コマンド | 動作 |
|----------|------|
| `gcc` | カレント行のコメントをトグル |
| `gc{motion}` | モーション範囲のコメントをトグル |
| Visual + `gc` | 選択範囲のコメントをトグル |

## テーマ

- `Dracula`（デフォルト）
- `Nord`
- `TokyoNight`
- `OneDark`

テーマは `VimEditorControl.SetTheme(EditorTheme)` で変更できます。

## 設定（.vimrc）

起動時にホームディレクトリまたはプロジェクトディレクトリの `.vimrc` / `_vimrc` / `init.vim` を読み込みます。

```vim
set tabstop=4
set expandtab
set scrolloff=8
set number
set relativenumber
set spell
set textwidth=80

let mapleader = " "
nnoremap <Leader>w :w<CR>
nnoremap <Leader>q :q<CR>
```

## 参考

- 今後の実装計画と実装済み機能: `ROADMAP.md`
