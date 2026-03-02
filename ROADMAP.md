# Editor 機能ロードマップ

> 作成日: 2026-03-02 / 更新日: 2026-03-02

## 高優先度（Vimの基本として欠かせないもの）

### 1. コードフォールド
- `za` — トグル、`zc` — 閉じる、`zo` — 開く
- `zR` — 全展開、`zM` — 全折りたたみ
- インデントベース（`foldmethod=indent`）またはマーカーベース（`{{{`/`}}}`）
- **実装箇所:** `MotionEngine`/`VimEngine` に `z` プレフィックスコマンド追加、`TextBuffer` にフォールド状態保持、`EditorCanvas` でレンダリングをスキップ

### 2. ~~Quickfixリスト~~ ✅ 実装済み (2026-03-02)
→ 実装済み一覧を参照

### 3. `:global` コマンド
- `:g/pattern/cmd` — パターン一致行に一括コマンド実行
- `:v/pattern/cmd` (`:g!`) — 不一致行に実行
- 例: `:g/^$/d`（空行削除）、`:g/TODO/yank A`（TODO行をレジスタAに収集）
- **実装箇所:** `ExCommandProcessor.Execute` に `g` ブランチ追加

---

## 中優先度（生産性に大きく影響）

### 4. ファイル Fuzzy Finder
- `Ctrl+P` でファイル名インクリメンタル検索
- ファジーマッチング（部分文字列/スコアリング）
- プロジェクトルートから再帰検索、`.gitignore` 除外
- **実装箇所:** `Editor.App` に `FuzzyFinderWindow`、`Editor.Core` にマッチングロジック

### 5. プロジェクト横断 grep
- `:grep pattern` / `:vimgrep /pattern/ **/*.cs`
- 結果を Quickfix リストに送る
- **実装箇所:** `ExCommandProcessor` に `:grep` / `:vimgrep` 追加、バックグラウンド検索

### 6. インクリメンタル検索ハイライト（`incsearch`）
- `/` 入力中にリアルタイムでマッチ箇所をハイライト
- カーソルを最初のマッチにプレビュー移動
- `set incsearch` オプションで制御（`VimOptions` には存在済み）
- **実装箇所:** `VimEditorControl` の CommandLine 入力イベントで `EditorCanvas.SetSearchMatches` を随時呼ぶ

### 7. 相対行番号
- `set relativenumber` — 現在行からの相対距離を表示
- `set number relativenumber` — 現在行は絶対番号、他は相対
- **実装箇所:** `EditorCanvas.OnRender` の行番号描画部分、`VimOptions.RelativeNumber` を参照

### 8. ウィンドウ分割操作の強化
- `Ctrl+W w` / `Ctrl+W Ctrl+W` — 分割間フォーカス移動
- `Ctrl+W =` — 均等サイズ
- `Ctrl+W +` / `Ctrl+W -` — 高さ調整
- `Ctrl+W h/j/k/l` — 方向指定フォーカス移動
- **実装箇所:** `VimEditorControl.ProcessKey` に `Ctrl+W` プレフィックス処理、`MainWindow` に分割管理ロジック

---

## 低優先度（あると便利）

### 9. Git 統合
- 行番号横に diff 記号（`+` 追加、`~` 変更、`-` 削除）
- `:Git blame` でインライン blame 表示
- **実装箇所:** `Editor.Controls` に `GitDiffProvider`（`git diff` をパース）、`EditorCanvas` でガター描画

### 10. ターミナル
- `:terminal` / `:term` で組み込みターミナルを開く
- **実装箇所:** `Editor.App` に `TerminalPane`（`System.Diagnostics.Process` + VT100パーサー）

### 11. スペルチェック
- `set spell` で有効化
- `z=` でカーソル下の単語の修正候補ポップアップ
- `]s` / `[s` でスペルミスを移動
- **実装箇所:** `Editor.Core` に `SpellChecker`（辞書ファイルベース）

### 12. `:read !cmd`
- `:read !ls` などシェルコマンド出力をバッファに挿入
- **実装箇所:** `ExCommandProcessor` の `:read` ブランチ拡張

### 13. 補完ドキュメントポップアップ
- 補完候補選択中に右側にドキュメント（`documentation` フィールド）を表示
- **実装箇所:** `EditorCanvas` の補完ポップアップ描画を拡張

---

## 実装済み（参考）

| 機能 | 状態 |
|------|------|
| Normal / Insert / Visual / Visual Block モード | ✅ |
| ドットコマンド (`.`) | ✅ |
| `:substitute` (`:s/pattern/replacement/flags`) | ✅ |
| コマンド履歴 | ✅ |
| LSP: 補完・ホバー・診断・定義ジャンプ・シグネチャヘルプ・フォーマット | ✅ |
| マルチバッファ・タブ | ✅ |
| レジスタ・マーク・マクロ | ✅ |
| シンタックスハイライト | ✅ |
| ウィンドウ分割（基本） | ✅ |
| ファイルツリー | ✅ |
| 検索ポップアップ (Ctrl+N) | ✅ |
| vimrc 読み込み | ✅ |
| **Quickfixリスト** (`:copen`/`:cclose`/`:cn`/`:cp`/`:cc N`/`:cl`) | ✅ 2026-03-02 |
