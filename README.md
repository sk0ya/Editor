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
- プロジェクト横断検索・置換（`:grep` / `:vimgrep` / `:grepreplace` / `:creplace`、Quickfix 結果から確認付き一括置換）
- コードフォールド（`za` / `zc` / `zo` / `zR` / `zM` / `zf`）
- サラウンド操作（`ys` / `cs` / `ds`）
- コメントトグル（`gc` / `gcc`）
- Git 差分表示・ブレームアノテーション（`:Git blame`）
- スペルチェック（`set spell`、`]s` / `[s` / `z=`）
- 大規模ファイル向けの可視範囲構文解析・描画キャッシュ
- 統合ターミナル（`:terminal` / `:term`、複数起動、`:terms` / `:termnext` / `:termprev` / `:termselect N` / `:termclose [N]`）
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
Editor.App -> Editor.Controls.Defaults
Editor.Controls.Defaults -> Editor.Controls
Editor.Controls.Defaults -> Editor.Core
Editor.Core.Tests -> Editor.Core
```

- `src/Editor.Core`
  WPF 非依存の Vim エンジン、バッファ、Ex コマンド、シンタックス、フォールドなどの純ロジック層
- `src/Editor.Controls`
  `VimEditorControl` / `EditorCanvas` を含む、NuGet 化対象の WPF コントロール層
- `src/Editor.Controls.Defaults`
  Git / LSP の既定実装をまとめた補助 DLL。`Editor.Controls` に注入して使う
- `src/Editor.App`
  メインウィンドウ、タブ管理、ファイルツリーなどのホストアプリ層
- `tests/Editor.Core.Tests`
  `Editor.Core` のユニットテスト

## NuGet パッケージ

- `Editor.Core`
  Vim エンジンやテキスト処理のコアロジック
- `Editor.Controls`
  再利用可能な `VimEditorControl` 本体
- `Editor.Controls.Defaults`
  `Editor.Controls` 用の既定 Git/LSP 実装

このリポジトリの NuGet 配布先は GitHub Packages です。利用には GitHub 認証が必要です。

- 利用者は GitHub アカウントの `PAT classic` を作成してください
- 必要な scope は最低でも `read:packages` です
- package が private のままの場合、token の所有ユーザーにも package への read 権限が必要です
- token を repo に commit しないでください

まず `NuGet.Config.template` を `NuGet.Config` にコピーし、`YOUR_GITHUB_USERNAME` と `YOUR_GITHUB_PAT_CLASSIC` を自分の値に置き換えます。`NuGet.Config` は `.gitignore` 済みです。

```bash
copy NuGet.Config.template NuGet.Config
```

`NuGet.Config` を使って restore する方法でも構いませんが、ローカル環境の NuGet source として登録しておく方が扱いやすいです。

```bash
dotnet nuget add source --username YOUR_GITHUB_USER --password YOUR_GITHUB_PAT --store-password-in-clear-text --name github "https://nuget.pkg.github.com/sk0ya/index.json"
```

利用側のアプリでは、コントロール本体と既定実装を追加します。

```bash
dotnet add package Editor.Controls --version 0.1.1
dotnet add package Editor.Controls.Defaults --version 0.1.1
```

`Editor.App` では `VimEditorControlDefaults.CreateOptions()` を使って既定の Git/LSP 実装を注入しています。

GitHub 公式ドキュメント:

- GitHub Packages の NuGet registry: https://docs.github.com/en/enterprise-cloud@latest/packages/working-with-a-github-packages-registry/working-with-the-nuget-registry
- package permissions: https://docs.github.com/en/packages/learn-github-packages/about-permissions-for-github-packages

## GitHub Packages 公開

`.github/workflows/publish-nuget.yml` は次の条件でパッケージを公開します。

- `v*` 形式の Git タグを push したとき
- GitHub Actions の `workflow_dispatch` で手動実行したとき

タグ `v0.1.1` を push すると、`0.1.1` を package version として `Editor.Core` / `Editor.Controls` / `Editor.Controls.Defaults` を GitHub Packages に push します。

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
