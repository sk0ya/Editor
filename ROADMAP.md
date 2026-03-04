# Editor 機能ロードマップ

> 作成日: 2026-03-02 / 更新日: 2026-03-07

---

## 中優先度（生産性に大きく影響）

### 5. テキスト整形 `gq{motion}`
- `gq{motion}` / `gqq` (1行) — `textwidth` の設定値で折り返し整形
- **実装箇所:** `VimEngine`、`VimOptions.TextWidth` を追加

### ~~10. `:sort` コマンド~~ ✅

### ~~11. `:normal` コマンド~~ ✅
- `:[range]normal {commands}` — Exモードからノーマルモードのコマンドを実行
- 例: `:%normal A;`（全行末にセミコロン追加）
- **実装箇所:** `VimEngine.TryExecuteNormalCmd` — 範囲ループで各行に `ProcessStroke` 呼び出し、単一 undo レコード、`<Esc>`/`<CR>` 等スペシャルキー対応

---

## 低優先度（あると便利）

### 12. ターミナル
- `:terminal` / `:term` で組み込みターミナルを開く
- **実装箇所:** `Editor.App` に `TerminalPane`（`System.Diagnostics.Process` + VT100パーサー）

### 13. スペルチェック
- `set spell` で有効化
- `z=` でカーソル下の単語の修正候補ポップアップ
- `]s` / `[s` でスペルミスを移動
- **実装箇所:** `Editor.Core` に `SpellChecker`（辞書ファイルベース）

### ~~14. `:read !cmd`~~ ✅
- `:read !ls` などシェルコマンド出力をバッファに挿入
- **実装箇所:** `ExCommandProcessor.ExecuteReadShell` — `ArgumentList` でクロスプラットフォーム対応、タイムアウト時 `Kill()`

### 15. 補完ドキュメントポップアップ
- 補完候補選択中に右側にドキュメント（`documentation` フィールド）を表示
- **実装箇所:** `EditorCanvas` の補完ポップアップ描画を拡張

### 18. Surround 操作
- `ys{motion}{char}` — モーション範囲を指定文字で囲む
- `cs{from}{to}` — 既存の囲み文字を変更
- `ds{char}` — 囲み文字を削除
- **実装箇所:** `VimEngine` に `ys/cs/ds` ハンドラ追加

### ~~20. 不可視文字表示 (`set list`)~~ ✅
- `set list` — タブ(`→`)・行末スペース(`·`)・改行(`¶`)を表示
- `set listchars=tab:→\ ,trail:·,eol:¶,space:·` でカスタマイズ
- **実装箇所:** `EditorCanvas.DrawListChars()` でオーバーレイ描画、`VimOptions.List`/`ListChars` 追加

### 21. `:retab`
- `:[range]retab [N]` — タブとスペースを相互変換
- **実装箇所:** `ExCommandProcessor` に `:retab` ブランチ

### 22. セッション管理
- `:mksession [file]` — 開いているファイル・タブ・分割状態を保存
- `:source [file]` — セッションファイルを読み込み
- **実装箇所:** `Editor.App` に `SessionManager`

### 23. 追加テーマ
- Dracula に加えて Nord、Tokyo Night、One Dark などを追加
- `EditorTheme.Nord` / `EditorTheme.OneDark` クラスを追加

---

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
| **不可視文字表示** (`set list`/`nolist`、`set listchars=tab:→ ,trail:·,eol:¶,space:·`) | ✅ 2026-03-05 |
| **`gf` / `gx`** カーソル下のファイル・URL を開く | ✅ 2026-03-04 |
| **`gu/gU/g~`** ケース変換オペレータ（`guu`/`gUU`/`g~~`=行全体、テキストオブジェクト対応、Visual対応） | ✅ 2026-03-04 |
| **`:normal` / `:norm`** (`:[range]normal[!] {cmds}`、`<Esc>`/`<CR>` 等スペシャルキー、単一 undo レコード) | ✅ 2026-03-06 |
| **`:sort`** (`:[range]sort [i] [r /pat/]`、大小無視・パターン一致部分ソート対応) | ✅ 2026-03-07 |
