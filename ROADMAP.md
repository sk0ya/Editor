# Editor 機能ロードマップ

> 作成日: 2026-03-02 / 更新日: 2026-03-04 (:global コマンド)

---

## 高優先度（Vimの基本として欠かせないもの）

### ~~1. テキストオブジェクト拡張~~ ✅ 実装済み (2026-03-04)
→ 実装済み一覧を参照

### ~~2. `:global` コマンド~~ ✅ 実装済み (2026-03-04)
→ 実装済み一覧を参照

### ~~3. `Ctrl+A` / `Ctrl+X` — 数値のインクリメント/デクリメント~~ ✅ 実装済み (2026-03-03)
→ 実装済み一覧を参照

---

## 中優先度（生産性に大きく影響）

### 4. 大文字小文字変換オペレータ
- `gu{motion}` — 小文字化
- `gU{motion}` — 大文字化
- `g~{motion}` — 大小反転（現在は `~` が1文字のみ）
- **実装箇所:** `CommandParser` の g-prefix、`VimEngine.ExecuteNormalCommand` にオペレータ処理追加

### 5. テキスト整形 `gq{motion}`
- `gq{motion}` / `gqq` (1行) — `textwidth` の設定値で折り返し整形
- **実装箇所:** `VimEngine`、`VimOptions.TextWidth` を追加

### ~~6. LSP: 参照検索 (`gr`)~~ ✅ 実装済み (2026-03-04)
→ 実装済み一覧を参照

### ~~7. LSP: シンボルリネーム~~ ✅ 実装済み (2026-03-04)
→ 実装済み一覧を参照

### ~~8. LSP: コードアクション (`ga`)~~ ✅ 実装済み (2026-03-04)
→ 実装済み一覧を参照

### ~~9. コマンドライン Tab 補完~~ ✅ 実装済み (2026-03-04)
→ 実装済み一覧を参照

### 10. `:sort` コマンド
- `:[range]sort [i] [r /pat/]` — 行ソート（`i`=大小無視、`r`=パターン一致部分でソート）
- **実装箇所:** `ExCommandProcessor.Execute` に `sort` ブランチ

### 11. `:normal` コマンド
- `:[range]normal {commands}` — Exモードからノーマルモードのコマンドを実行
- 例: `:%normal A;`（全行末にセミコロン追加）
- **実装箇所:** `ExCommandProcessor` で範囲ループ + `VimEngine.ProcessKey` 呼び出し

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

### 14. `:read !cmd`
- `:read !ls` などシェルコマンド出力をバッファに挿入
- **実装箇所:** `ExCommandProcessor` の `:read` ブランチ拡張

### 15. 補完ドキュメントポップアップ
- 補完候補選択中に右側にドキュメント（`documentation` フィールド）を表示
- **実装箇所:** `EditorCanvas` の補完ポップアップ描画を拡張

### 16. Auto-pairs（括弧・クォートの自動補完）
- Insertモードで `(` 入力時に `)` を自動挿入し、カーソルを中央に
- `"`, `'`, `` ` ``, `[`, `{` も同様
- `set nopairs` で無効化
- **実装箇所:** `VimEngine.HandleInsert` にペア補完ロジック、`VimOptions` に `Pairs` 設定追加

### 17. コメントトグル (`gc` オペレータ)
- `gc{motion}` / `gcc`（1行） — vim-commentary 風のコメント切り替え
- 言語別コメント記号を `ISyntaxLanguage` から取得
- **実装箇所:** `CommandParser` に `gc` オペレータ追加、`ISyntaxLanguage` に `CommentPrefix`/`CommentWrap` プロパティ

### 18. Surround 操作
- `ys{motion}{char}` — モーション範囲を指定文字で囲む
- `cs{from}{to}` — 既存の囲み文字を変更
- `ds{char}` — 囲み文字を削除
- **実装箇所:** `VimEngine` に `ys/cs/ds` ハンドラ追加

### 19. `gf` / `gx` — カーソル下のファイル・URLを開く ✅
- `gf` — カーソル下のパス文字列を `OpenFileRequested` として発火（相対パス解決あり）
- `gx` — `http://` / `https://` / `ftp://` はデフォルトブラウザで開く、それ以外はファイルとして開く
- **実装箇所:** `CommandParser.cs`（`gf`/`gx` を認識）、`VimEngine.ExecuteNormalCommand`（`case "gf"` / `case "gx"`）、`VimEditorControl.xaml.cs`（`OpenUrlRequested` → `Process.Start`）

### 20. 不可視文字表示 (`set list`)
- `set list` — タブ(`→`)・行末スペース(`·`)・改行(`¶`)を表示
- `set listchars=tab:→\ ,trail:·` でカスタマイズ
- **実装箇所:** `EditorCanvas.OnRender` にリストchar描画、`VimOptions.List`/`ListChars` 追加

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

## 実装済み（参考）

| 機能 | 状態 |
|------|------|
| Normal / Insert / Visual / Visual Block / Replace モード | ✅ |
| ドットコマンド (`.`) | ✅ |
| `:substitute` (`:s/pattern/replacement/flags`) | ✅ |
| コマンド履歴 | ✅ |
| LSP: 補完・ホバー・診断・定義ジャンプ・シグネチャヘルプ・フォーマット | ✅ |
| マルチバッファ・タブ | ✅ |
| レジスタ・マーク・マクロ | ✅ |
| シンタックスハイライト（16言語） | ✅ |
| **ウィンドウ分割** (`:split`/`:vsplit`/`:new`/`:vnew`、`Ctrl+W w/W/h/j/k/l/q`) | ✅ 2026-03-03 |
| ファイルツリー | ✅ |
| vimrc 読み込み（30+ オプション、nmap/imap/vmap） | ✅ |
| **Quickfixリスト** (`:copen`/`:cclose`/`:cn`/`:cp`/`:cc N`/`:cl`) | ✅ 2026-03-02 |
| **コードフォールド** (`za`/`zc`/`zo`/`zR`/`zM`/`zf`、LSP+シンタックス検出、ネスト対応) | ✅ 2026-03-02 |
| **Fuzzy Finder** (`Ctrl+P`、ファジーマッチ、`.gitignore` 除外) | ✅ 2026-03-03 |
| **相対行番号** (`set relativenumber`/`rnu`) | ✅ 2026-03-03 |
| **プロジェクト横断 grep** (`:grep`/`:vimgrep`、Quickfix送り) | ✅ 2026-03-03 |
| **Git統合** (ガター diff バー、`:Git blame` インライン blame) | ✅ 2026-03-03 |
| **インクリメンタル検索ハイライト** (`set incsearch`) | ✅ 2026-03-03 |
| **テキストオブジェクト拡張** `iw/aw/iW/aW`・括弧`(){}[]`・クォート`"'\``・タグ`t`・センテンス`s`・パラグラフ`p`、Visualモード対応 | ✅ 2026-03-04 |
| **`Ctrl+A/X`** 数値インクリメント/デクリメント（10進・16進、count対応） | ✅ 2026-03-03 |
| `gu/gU/g~` ケース変換オペレータ | ❌ 未実装 → 中優先度 #4 |
| **LSP: 参照検索** (`gr` → Referencesパネル) | ✅ 2026-03-04 |
| **LSP: シンボルリネーム** (`F2` / `:Rename [name]`) | ✅ 2026-03-04 |
| **LSP: コードアクション** (`ga`、j/k/Enter/Escポップアップ) | ✅ 2026-03-04 |
| **コマンドライン Tab 補完** (`:e`/`:b`/`:colorscheme`/`:set`/コマンド名、Wildmenu表示) | ✅ 2026-03-04 |
| **`:global` / `:vglobal`** (`:g/pattern/d|p|s///`、`:g!`/`:v`逆一致、範囲指定対応) | ✅ 2026-03-04 |
