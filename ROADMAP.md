# Editor 機能ロードマップ

> 作成日: 2026-03-02 / 更新日: 2026-03-07 (Visual Block r)

---

## TODO — Vim 互換機能

### Visual Block モード強化

### Ex コマンド

| 機能 | 説明 |
|------|------|
| **`set scrolloff={N}`** | カーソル上下に確保する最小行数 |
| **`set foldmethod={expr/indent/marker/syntax}`** | `foldmethod` 追加形式 |

---

## TODO — テキストエディタ機能

### ファイル管理

| 機能 | 説明 |
|------|------|
| **`:e!`** | ディスクから強制リロード |
| **改行コード切り替え** | `set fileformat=unix/dos/mac` |

### LSP 拡張

| 機能 | 説明 |
|------|------|
~~| **LSP: `textDocument/semanticTokens`** | セマンティックトークンによる高精度ハイライト |~~

### Git 拡張

| 機能 | 説明 |
|------|------|
| ~~**`:Git diff`**~~ | ~~ファイルの git diff をエディタ内に表示~~ |
| ~~**`:Git log`**~~ | ~~コミットログをエディタ内に表示~~ |
| ~~**`:Git commit`**~~ | ~~コミットメッセージ入力 UI~~ |
| ~~**Hunk 操作**~~ | ~~`]c`/`[c` で diff hunk 間移動~~ |

---

## 実装済み（新規）

| 機能 | 日付 |
|------|------|
| **`r{char}` in `Ctrl+V`** VisualBlock のブロック選択範囲の文字を一括置換 | ✅ 2026-03-07 |
| **`c` in `Ctrl+V`** VisualBlock のブロック選択範囲を削除して一括変更（全選択行にライブ適用） | ✅ 2026-03-07 |
| **`I` / `A` in `Ctrl+V`** VisualBlock の先頭/末尾に一括挿入（全選択行にライブ適用） | ✅ 2026-03-07 |
| **ファイルウォッチャー** 外部変更を検知して自動リロード / 確認ダイアログ（`volatile` + 重複イベント抑止） | ✅ 2026-03-06 |
| **`:bufdo` / `:windo` / `:tabdo`** 全バッファに Ex コマンドを適用（`:windo`/`:tabdo` は `:bufdo` として動作） | ✅ 2026-03-06 |
| **対応括弧ハイライト** カーソル下の括弧とペアをハイライト表示（ネスト対応、全テーマ対応） | ✅ 2026-03-06 |
| **`set colorcolumn={N}`** (alias `cc`) 指定列に縦ガイドラインを表示 | ✅ 2026-03-06 |
| **`:jumps`** ジャンプリストを表示 | ✅ 2026-03-06 |
| **インデントガイド** `set indentguides` で各インデントレベルに縦ラインを描画 | ✅ 2026-03-06 |
| **`Ctrl+R =`** 式レジスタ — Insert モードで数式を入力して結果を挿入 | ✅ 2026-03-06 |
| **`Ctrl+K {a}{b}`** ダイグラフ文字の挿入（矢印・数学記号・アクセント文字・ギリシャ文字など） | ✅ 2026-03-06 |
| **`:digraphs`** 使用可能なダイグラフ一覧を表示 | ✅ 2026-03-06 |
| **`:abbreviate` / `:iab`** インサートモードの略語展開（`:una` で削除、vimrc 対応） | ✅ 2026-03-06 |
| **`q:` / `q/` / `q?`** コマンドライン/検索履歴をコマンドラインに展開、Up/Down で履歴ナビゲーション、`:history` で一覧表示 | ✅ 2026-03-06 |
| **`Ctrl+X Ctrl+F`** Insert モードでカーソル前のパスを補完、`Ctrl+F` で次候補サイクル | ✅ 2026-03-06 |
| **`Ctrl+X Ctrl+L`** Insert モードでバッファ内の行を前方一致で補完、`Ctrl+L` で次候補サイクル | ✅ 2026-03-06 |
| **スクロールバー** 垂直/水平オーバーレイスクロールバー（`set scrollbar` / `noscrollbar`） | ✅ 2026-03-06 |
| **`:Git diff` / `:Gdiff`** ファイルの git diff を新規タブで表示 | ✅ 2026-03-07 |
| **`:Git log` / `:Glog`** コミットログ（直近30件）を新規タブで表示 | ✅ 2026-03-07 |
| **ミニマップ** ファイル全体の縮小表示、クリックでスクロール（`set minimap`） | ✅ 2026-03-07 |
| **Inlay hints** 変数の型・パラメータ名をインライン表示（`set inlayhints`） | ✅ 2026-03-07 |
| **Workspace symbols** プロジェクト横断シンボル検索（`:Symbols {query}` / `:WorkspaceSymbols {query}`）、結果を References パネルに表示 | ✅ 2026-03-07 |
| **LSP semantic tokens** セマンティックトークンによる高精度ハイライト（`set semantictokens`）、レガシー正規表現より優先 | ✅ 2026-03-07 |
| **カラープレビュー** CSS/HTML カラーコード（`#RGB`/`#RRGGBB`/`#RRGGBBAA`/`rgb()`/`rgba()`）の横にインラインスウォッチ表示（`set colorpreview`） | ✅ 2026-03-07 |
| **Document symbols / Outline** `:Outline` コマンドまたはアクティビティバーボタンでサイドバーにシンボル一覧を表示、クリックでジャンプ | ✅ 2026-03-07 |
| **LSP Call hierarchy** `gch` で関数の呼び出し元/先を References パネルに表示（`callHierarchy/prepare` + `incomingCalls` + `outgoingCalls`） | ✅ 2026-03-07 |
| **LSP Type hierarchy** `gct` / `:TypeHierarchy` で型の継承ツリー（supertypes + subtypes）を References パネルに表示 | ✅ 2026-03-07 |
| **マルチカーソル** `Ctrl+D` で次の同単語にカーソル追加、`Ctrl+Alt+Down/Up` で上下にカーソル追加、Insert モードで全カーソルに同時編集、`Esc` (Normal) で終了 | ✅ 2026-03-07 |
| **スニペット** Insert モードでトリガーワード+`Tab` で展開、`$1`/`$2`/.../`$0` タブストップを `Tab`/`Shift+Tab` でナビゲート、LSP `insertTextFormat: Snippet` 対応、7言語組み込みスニペット、vimrc で `:snippet {trigger} {body}` 定義 | ✅ 2026-03-07 |
| **Breadcrumb** カーソル位置のシンボルパスをステータスバーに表示（`set breadcrumb`）、LSP `textDocument/documentSymbol` 利用、`MyNamespace > MyClass > MyMethod` 形式 | ✅ 2026-03-07 |

## 実装済み

| 機能 | 日付 |
|------|------|
| Normal / Insert / Visual / Visual Block / Replace モード | ✅ |
| ドットコマンド (`.`) | ✅ |
| `:substitute` (`:s/pattern/replacement/flags`) | ✅ |
| コマンド履歴 | ✅ |
| マルチバッファ・タブ | ✅ |
| レジスタ・マーク・マクロ | ✅ |
| シンタックスハイライト（16言語） | ✅ |
| ファイルツリー | ✅ |
| vimrc 読み込み（30+ オプション、nmap/imap/vmap） | ✅ |
| LSP: 補完・ホバー・診断・定義ジャンプ・シグネチャヘルプ・フォーマット | ✅ |
| **Quickfixリスト** (`:copen`/`:cclose`/`:cn`/`:cp`/`:cc N`/`:cl`) | ✅ 2026-03-02 |
| **コードフォールド** (`za`/`zc`/`zo`/`zR`/`zM`/`zf`、LSP+シンタックス検出、ネスト対応) | ✅ 2026-03-02 |
| **ウィンドウ分割** (`:split`/`:vsplit`/`:new`/`:vnew`、`Ctrl+W w/W/h/j/k/l/q`) | ✅ 2026-03-03 |
| **Fuzzy Finder** (`Ctrl+P`、ファジーマッチ、`.gitignore` 除外) | ✅ 2026-03-03 |
| **相対行番号** (`set relativenumber`/`rnu`) | ✅ 2026-03-03 |
| **プロジェクト横断 grep** (`:grep`/`:vimgrep`、Quickfix送り) | ✅ 2026-03-03 |
| **Git統合** (ガター diff バー、`:Git blame` インライン blame) | ✅ 2026-03-03 |
| **インクリメンタル検索ハイライト** (`set incsearch`) | ✅ 2026-03-03 |
| **`Ctrl+A/X`** 数値インクリメント/デクリメント（10進・16進、count対応） | ✅ 2026-03-03 |
| **Auto-pairs** 括弧・クォート自動補完、スキップオーバー、`set nopairs` で無効化 | ✅ 2026-03-04 |
| **コメントトグル** `gc{motion}` / `gcc` / Visual+`gc`（vim-commentary 風、17言語対応） | ✅ 2026-03-04 |
| **テキストオブジェクト拡張** `iw/aw`・括弧`(){}[]<>`・クォート`"'\``・タグ`t`・センテンス`s`・パラグラフ`p` | ✅ 2026-03-04 |
| **`:global` / `:vglobal`** (`:g/pattern/d\|p\|s///`、`:g!`/`:v` 逆一致、範囲指定対応) | ✅ 2026-03-04 |
| **LSP: 参照検索** (`gr` → Referencesパネル) | ✅ 2026-03-04 |
| **LSP: シンボルリネーム** (`F2` / `:Rename [name]`) | ✅ 2026-03-04 |
| **LSP: コードアクション** (`ga`、j/k/Enter/Escポップアップ) | ✅ 2026-03-04 |
| **コマンドライン Tab 補完** (`:e`/`:b`/`:colorscheme`/`:set`/コマンド名、Wildmenu表示) | ✅ 2026-03-04 |
| **`gf` / `gx`** カーソル下のファイル・URL を開く | ✅ 2026-03-04 |
| **`gu/gU/g~`** ケース変換オペレータ（`guu`/`gUU`/`g~~`=行全体、テキストオブジェクト対応、Visual対応） | ✅ 2026-03-04 |
| **不可視文字表示** (`set list`/`nolist`、`set listchars=tab:→ ,trail:·,eol:¶,space:·`) | ✅ 2026-03-05 |
| **`:read !cmd`** シェルコマンド出力をバッファに挿入 | ✅ 2026-03-05 |
| **`:normal` / `:norm`** (`:[range]normal[!] {cmds}`、`<Esc>`/`<CR>` 等スペシャルキー、単一 undo レコード) | ✅ 2026-03-06 |
| **`:sort`** (`:[range]sort [i] [r /pat/]`、大小無視・パターン一致部分ソート対応) | ✅ 2026-03-07 |
| **`gq{motion}` / `gqq`** テキスト整形（`textwidth` 設定値で折り返し） | ✅ 2026-03-04 |
| **補完ドキュメントポップアップ** (選択中アイテムの `documentation` を右パネルに表示) | ✅ 2026-03-04 |
| **Surround 操作** `ys{motion}{char}` / `cs{from}{to}` / `ds{char}` (vim-surround 互換) | ✅ 2026-03-04 |
| **`:retab[!] [N]`** タブ↔スペース相互変換（範囲指定対応） | ✅ 2026-03-04 |
| **スペルチェック** (`set spell`、`z=` 提案、`]s`/`[s` ナビ、辞書ファイル読み込み) | ✅ 2026-03-04 |
| **セッション管理** (`:mksession [file]`、`:source [file]`) | ✅ 2026-03-04 |
| **追加テーマ** Nord / Tokyo Night / One Dark (`set colorscheme=nord` 等) | ✅ 2026-03-04 |
| **組み込みターミナル** (`:terminal` / `:term`、コマンド実行、履歴、`cd` 組み込み) | ✅ 2026-03-04 |
| **`gv` / `gi` / `gJ`** 前回選択再選択・最終挿入位置ジャンプ・スペースなし行結合 | ✅ 2026-03-06 |
| **`g;` / `g,` / `:changes`** チェンジリストナビゲーション・一覧表示 | ✅ 2026-03-06 |
| **`Ctrl+6` / `@:`** alternate buffer 切り替え・最後の Ex コマンド繰り返し | ✅ 2026-03-06 |
| **`:[range]!cmd`** 選択範囲を外部コマンドでフィルター（例: `:%!sort`） | ✅ 2026-03-06 |
| **`Ctrl+R {reg}`** インサートモード中にレジスタ内容を挿入 | ✅ 2026-03-06 |
| **`Ctrl+N` / `Ctrl+P`** バッファ内キーワード補完（LSP なし） | ✅ 2026-03-06 |
| **`:[range]move` / `:[range]copy`** 行の移動・コピー（`:m`/`:co`/`:t` エイリアス対応） | ✅ 2026-03-06 |
| **`:[range]center` / `:right` / `:left`** 範囲内テキスト整列 | ✅ 2026-03-06 |
| **`set paste` / `pastetoggle`** ペーストモード（auto-indent・auto-pairs を一時無効化） | ✅ 2026-03-06 |
| **ファイルエンコーディング変換** `set fileencoding=utf-8` 等で BOM 自動検出・保存時エンコーディング変換（utf-8/utf-16/latin1/ascii/shift-jis/euc-jp 等対応） | ✅ 2026-03-06 |
| **`:Git commit` / `:Gcommit`** コミットメッセージ入力ダイアログ、`git commit -m` 実行、成否表示 | ✅ 2026-03-07 |
| **Hunk 操作 `]c` / `[c`** git diff hunk 間移動（wrap-around・状態バーに "Hunk N/M" 表示） | ✅ 2026-03-07 |
