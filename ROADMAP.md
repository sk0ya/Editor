# Editor 機能ロードマップ

> 作成日: 2026-03-02 / 更新日: 2026-03-04

---

## 実装済み（新規）

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
