# Editor リファクタリング計画

## ステータス

| Phase | 対象 | 状態 |
|---|---|---|
| 1 | ExCommandProcessor → ドメイン別ハンドラクラス | ✅ 完了 |
| 2 | VimEngine → 関心事別ハンドラクラス | ✅ 完了(縮小スコープ) |
| 3 | VimEditorControl → マネージャクラス抽出 | ✅ 完了(縮小スコープ) |
| 4 | EditorCanvas → レンダラークラス抽出 | 未着手 |
| 5 | VimEditorControl → PopupKeyNavigator | 未着手 |
| 6 | EditorCanvas → GutterHitTester | 未着手 |
| 7 | MainWindow → コントローラクラス抽出 | 未着手 |

## Context

このリポジトリは長期稼働中の実働アプリで、`VimEngineTests.cs`(384件)や `ExCommandProcessorTests.cs`(114件)など強力なテストがある一方、WPF層(`Editor.Controls`/`Editor.App`)には自動テストがほぼ無い。目的は「動作を変えずに、肥大化した神クラスを機能単位に分割し、今後の変更コストを下げる」こと。ビッグバン書き換えは避け、各フェーズが独立してビルド・テスト可能な、機械的な抽出に限定する。DIコンテナ導入やMVVM化などのパラダイム変更は行わない。

**重要な制約: partial class の新規作成は禁止。** 分割は既存クラスをファイル分割(`ClassName.Concern.cs`)するのではなく、関心事ごとに独立したクラスを新規作成し、それをメインクラスがフィールドとして保持・コンストラクタ経由で必要な協力者だけを渡して合成(composition)する形で行う。これは既存コードベースで既に使われているパターンそのもの:
- `MotionEngine` — ステートレス、`TextBuffer`をctor引数に取り`Calculate(motion, cursor, count)`を提供
- `BufferManager`/`RegisterManager`/`MarkManager`/`MacroManager`/`FoldManager` — `VimEngine`がフィールドとして所有
- `LspManager`/`GitDiffProvider` — `VimEditorControl`がフィールドとして所有

メインクラス(`VimEngine`/`ExCommandProcessor`/`VimEditorControl`/`EditorCanvas`/`MainWindow`)は既存のディスパッチ点(switch/if-chain)をそのまま残し、各caseの中身を「合成したハンドラクラスのメソッド呼び出し」に置き換える。

対象ファイルの規模(計画時点):
| ファイル | 行数 | 問題 |
|---|---|---|
| `src/Editor.Core/Engine/VimEngine.cs` | ~6,397 | 9個のサブシステムを直接所有する神オブジェクト。`ExecuteNormalCommand`のswitchが~490行 |
| `src/Editor.Controls/VimEditorControl.xaml.cs` | ~6,282 | IME/TSF・LSPポップアップ・パス補完・スニペット・マルチカーソルが1ファイルに混在 |
| `src/Editor.App/MainWindow.xaml.cs` | ~4,559 | サイドバー・タブ・プレビュー・参照パネル・メニューが1ファイルに混在 |
| `src/Editor.Controls/Rendering/EditorCanvas.cs` | ~3,122 | 15種類の描画対象(gutter, minimap, LSPポップアップ等)が1ファイルに混在 |
| `src/Editor.Core/Engine/ExCommandProcessor.cs` | ~2,958 | `Execute()`が759行のif/elseチェーンで60以上の`:`コマンドを処理 |

各フェーズは「移動するだけ(振る舞い不変)」と「制御フローを触る(挙動温存が必要)」を明確に分け、後者はリスクを上げて手動検証を必須にする。

---

## Phase 1 — ExCommandProcessor: exコマンドのドメイン別ハンドラクラスへの委譲 ✅

**目的:** 2,958行の`Execute()`(if/elseチェーン)が処理する~60コマンドを、ドメイン別の独立クラスに委譲する。

**結果:** `ExCommandProcessor.cs` は 2,958 → 1,406行(-52%)。`src/Editor.Core/Engine/ExCommands/` 配下に以下を抽出:
- `GitCommands`(76行) — blame/status/commit/stage/diff/log/push/pull
- `LspCommands`(228行) — Format/Rename/Symbols/CallHierarchy/TypeHierarchy/Lsp*/Fmt*
- `FileOpsCommands`(128行) — q/w/e/wq
- `RangeResolver`(110行) — 共有ユーティリティ(範囲プレフィックス解析・正規表現構築・`GetCommandArg`)
- `SubstituteCommands`(358行) — `:s`, `:g`/`:v`, `:sort`, `:retab`
- `ScriptingCommands`(729行) — let/echo/execute/call/if/for ブロック評価器
- `RegisterMarkCommands`(240行) — yank/put/registers/marks/delmarks

各クラスは必要な協力者(`BufferManager`, `RegisterManager`, `LspServerRegistry`など)だけをコンストラクタで受け取り、`ExCommandProcessor`はこれらをフィールドとして所有する。`Execute()`のif-chainはそのまま残り、各ブロックの中身を`_gitCommands.TryHandle(cmd, out result)`のような呼び出しに置き換えた。副産物として、到達不能だった重複分岐(`:s`コマンドの二重チェック)を削除。

`:execute`/関数呼び出しブロックが`ExCommandProcessor.Execute`/`ExecuteNoHistory`に再帰する箇所は、`Func<string, CursorPosition, ExResult>`デリゲートをコンストラクタ経由で渡すことで対応(循環参照を避けつつ委譲を維持)。

**検証:** 各抽出後に `dotnet build src/Editor.Core/Editor.Core.csproj` + `dotnet test tests/Editor.Core.Tests/`(全1051件)をグリーンで確認。

---

## Phase 2 — VimEngine: `ExecuteNormalCommand`のswitchを関心事別ハンドラクラスへ委譲 ✅(縮小スコープ)

**目的:** 最大の神オブジェクト(6,397行)の中核である~490行のswitchを分割。`ProcessKey → HandleNormal → CommandParser.Feed → ExecuteNormalCommand`のディスパッチ契約は変えない。

**結果:** 当初計画した6クラスのうち、実際に自己完結していた3クラスのみ抽出して完了とした(理由は下記「スコープ縮小の経緯」を参照)。`src/Editor.Core/Engine/Commands/` 配下に:
- `LspTriggerCommands`(gd/gr/ga/gch/gct) — 一行イベント発火のみ、依存なし
- `FoldCommands`(za/zo/zc/zM/zR/zf/zj/zk/[z/]z/zd/zD/zE/zn/zN) — `BufferManager`経由で`FoldManager`を操作し、カーソル適用は`Result`(DirectCursor/MoveCursor/FoldDisabled)を介してVimEngine側の2つの既存適用経路(直接クランプ vs `MoveCursor`)にそのまま接続
- `FileNavCommands`(gf/gx) — パス抽出(`ExtractFilePathUnderCursor`)ごと完全移動、他に呼び出し元なし

各クラスは`BufferManager`など必要な協力者だけをctorで受け取り、`VimEngine`がフィールドとして所有。`ExecuteNormalCommand`のswitchはそのまま残り、対象`case`本体を`_xxxCommands.TryHandle(...)`という数行の委譲に置き換えた。

### スコープ縮小の経緯

計画時点では`ModeTransitionCommands`(i/I/a/A/o/O/R/v/V)・`EditingCommands`(x/X/s/S/D/C/Y/p/P)・`SearchNavCommands`(n/N/*/#/;/,)も同じ要領で抽出する想定だったが、実装に着手して判明した実態は次の通り:

- これらのswitch本体は既に`EnterInsertMode`/`ExecuteDelete`/`DoSearch`等、**VimEngine内の広く共有された private メソッドへの1〜4行の薄い委譲**でしかない。
- その実体側(`EnterInsertMode`, `EnterVisualMode`, `BeginInsertRepeat`, `ExecuteDelete`, `PasteAfter/Before`, `DeleteLines`, `SetRepeatChange`(31箇所!), `DoSearch`, `_searchPattern`/`_searchForward`等)は、ノーマルモードのswitch以外にも演算子+モーション処理・dot-repeat再生・ビジュアルモード・検索モードUIなど**10〜30箇所から再利用**されている神クラス内共有ロジックであり、`FoldManager`のような独立したサブシステムを持たない。

つまり、この3つは「switchの委譲だけ動かす」か「実体ごと動かす」の二択になり、前者はVimEngine.csの行数もカップリングも実質改善しない見せかけの抽出、後者は10〜30箇所の呼び出し元を書き換える必要があり、計画が想定した「中低」リスクを大きく超える(Phase 1のExCommandProcessorとは異なり、VimEngineのswitchケースは独立したドメインの寄せ集めではなく、共有状態機械への窓口が大半を占めていたため)。

この判断はユーザーに確認の上、**この3クラスは対象外としてPhase 2を完了とする**ことで合意した。ビッグバン書き換え回避・小さく安全なコミット単位という本計画の大方針そのものに従った結果である。

**検証:** サブステップごとに `dotnet test tests/Editor.Core.Tests/` フル実行(全1051件グリーン)。手動確認不要。

---

## Phase 3 — VimEditorControl.xaml.cs: 独立したWPF関心事のマネージャクラスへの抽出 ✅(縮小スコープ)

**目的:** 6,282行から、既にセクションコメントで区切られ相互依存の薄いブロックを、`LspManager`/`GitDiffProvider`と同じ「合成されたマネージャクラス」パターンで抽出。

**結果:** 当初計画した4クラスのうち、自己完結していた3クラスのみ抽出して完了とした(理由は下記「スコープ縮小の経緯」を参照)。`src/Editor.Controls/` 配下に:
- `MultiCursorManager`(Ctrl+D/Ctrl+Alt+Down/Up) — `_extraCursors`/`_multiCursorMode`等の状態を丸ごと保持。`VimEngine`・`EditorCanvas`と、ステータス更新・再描画のコールバック2つだけをctorで受け取る
- `SnippetTabStopManager`(スニペット展開・タブストップ間ナビゲーション) — `VimEngine`と、`ProcessKey`/`ClearSelectionRangeState`/`ProcessVimEvents`/`UpdateAll`という既存の挿入・カーソル移動経路をコールバックとして受け取り、状態(`_tabStops`/`_index`)を丸ごと保持
- `PathCompletionManager`(ファイルシステムパス補完、LSP非依存) — `VimEngine`・`EditorCanvas`・`IEditorLspManager`(ポップアップの受け渡し用)・`ProcessKey`コールバックを受け取り、`_pathCompletionItems`等の状態を丸ごと保持

各クラスは呼び出し元(`OnKeyDown`/`OnPreviewKeyDown`/`ProcessKey`)からdelegateされる薄いラッパー越しに使われ、既存のディスパッチのif-chainはそのまま残した。`VimEditorControl`はこれらをフィールドとして所有する。

### スコープ縮小の経緯

計画時点では`ImeCompositionManager`(Win32 P/Invoke, カスタムTSF text store配線, `IEditorTextStoreHost`実装)も同じ要領で抽出する想定だったが、実装に着手する前の調査で判明した実態は次の通り:

- `_imeInsertBuffer`/`_imeCompositionSeq`/`_customTextStoreActive`といった状態は、`OnPreviewKeyDown`/`OnKeyDown`/`OnTextInput`という巨大なキー処理ディスパッチの**30箇所以上**から読み書きされている(mapping replay・IME確定・Escape処理・タイムアウトフラッシュなど)。
- `VimEditorControl`自体が`Editor.Controls.Ime.IEditorTextStoreHost`を明示的インターフェース実装しており、そのメソッド(`OnCompositionUpdated`/`OnCompositionCommitted`等)は`InsertCommittedText`・`Canvas`・`_engine.Mode`など、クラス内の広く共有された処理に直接依存する。

これはPhase 2で`ModeTransitionCommands`/`EditingCommands`/`SearchNavCommands`を対象外にしたのと同一の構造(独立したサブシステムではなく、巨大な共有ディスパッチへの窓口)であり、「switchの委譲だけ動かす見せかけの抽出」か「30箇所以上の呼び出し元を書き換える高リスクな書き換え」の二択になる。加えてWindows IME/TSF回りは自動カバレッジがほぼ無く(`EditorTextStoreTests`のみ)、日本語変換という壊れた場合の実害が大きい機能である。

この判断はユーザーに確認の上、**`ImeCompositionManager`は対象外としてPhase 3を完了とする**ことで合意した(Phase 2と同じ判断枠組み)。

**検証:** 各抽出後に `dotnet build src/Editor.Controls/Editor.Controls.csproj` + `dotnet test tests/Editor.Core.Tests/`(全1051件)+ `dotnet test tests/Editor.Controls.Tests/`(全9件)をグリーンで確認。挙動を変えない機械的な状態移動のみのため、手動でのIME/パス補完/スニペット/マルチカーソル確認は今回省略した。

---

## Phase 4 — EditorCanvas.cs: `OnRender`の描画専用をレンダラークラスへ抽出

**目的:** 3,122行から、`OnRender`が既にフラットな`Draw*`呼び出し列になっている描画ドメイン(~15種)をレンダラークラスにまとめる。
**対象ファイル:** `src/Editor.Controls/Rendering/EditorCanvas.cs`(`OnRender`本体と共有レイアウト計算は残す)、新規クラス(`src/Editor.Controls/Rendering/`配下):
- `GutterRenderer`(行番号/fold/blame margin/changed-lines の**描画のみ**、必要な状態は引数で渡す。ヒットテストは含めない)
- `OverlayRenderer`(minimap, scrollbar, color column, indent guides, whitespace, color swatches)
- `LspOverlayRenderer`(signature help/completion popup/code-action popup/inlay hints/semantic tokens/document highlights)

各レンダラーは`Draw(DrawingContext dc, <必要な状態>)`のようなメソッドを提供するステートレスなクラスにし(`MotionEngine`と同じ流儀)、`EditorCanvas`が`OnRender`内で呼び出す。状態(`_closedFoldStarts`, `_hoveredBlameLine`等)は`EditorCanvas`側に残し、メソッド引数として渡す。
**リスク:** 中(自動描画テストが無いため正しさは目視確認頼み)。
**検証:** `dotnet build`、`run`スキルでのビフォーアフタースクリーンショット比較: 行番号on/off、fold折りたたみ/展開とホバー表示、git blame margin、changed-linesガター、minimap、indent guides、whitespaceグリフ、inlay hints、セマンティックトークン色、補完/コードアクションポップアップ。

---

## Phase 5 — VimEditorControl: LSP/パス補完ポップアップのキー処理を共通クラスへ集約

**目的:** 補完・コードアクション・パス補完・参照パネルで繰り返される「ポップアップ表示中: Down/Upで選択移動, Tab/Returnで適用, Escapeで閉じる」ブロック(~3736-3799行付近)を、共通の`PopupKeyNavigator`クラスに集約。
**対象ファイル:** `src/Editor.Controls/VimEditorControl.xaml.cs`、新規クラス `src/Editor.Controls/PopupKeyNavigator.cs`。
**パターン:** `PopupKeyNavigator`は「選択移動」「確定」「キャンセル」のデリゲート(`Action<int> Move`, `Action Apply`, `Action Hide`)を受け取り、キー入力から統一的にディスパッチする小さなクラス。各ポップアップ(補完/コードアクション/パス補完/参照)ごとに`VimEditorControl`側でこのクラスのインスタンスを構築し呼び出す形にする。制御フロー(呼び出し順=早期リターンの優先順位)は厳密に温存する。
**リスク:** 中高 — 初めて制御フロー構造(順序/早期リターン)に触れるフェーズ。カバレッジが薄いファイル。
**検証:** `dotnet build`、`dotnet test tests/Editor.Controls.Tests/`。**手動確認必須**(`run`スキル): LSP補完のトリガー→Down/Up/Ctrl+N/Ctrl+P移動→Tab/Returnで確定→Escapeでキャンセル(Insertモードも維持されるか)。コードアクション(J/K/Down/Up、Return、Escape)。パス補完(Tab/Return、Escape)。ポップアップ間でキーが誤って漏れないこと。

---

## Phase 6 — EditorCanvas: gutterのマウス/ヒットテストを共通クラスへ集約

**目的:** `OnMouseMove`/`OnMouseLeftButtonDown`(831-1123行付近)でblame/breakpoint/fold/行番号ガター毎に重複している座標範囲チェックを統合。
**対象ファイル:** `src/Editor.Controls/Rendering/EditorCanvas.cs`、新規クラス `src/Editor.Controls/Rendering/GutterHitTester.cs`。
**パターン:** `GutterHitTester`が`_blameColWidth + bpColWidth + lineNumWidth`の境界を1箇所で計算するメソッド群(`TryHitBlameGutter/TryHitBreakpointGutter/TryHitFoldGutter/TryHitLineNumberGutter(point, boundaries, out line)`)を提供。`EditorCanvas`は`OnMouseMove`(hover)と`OnMouseLeftButtonDown`(click)の両方からこのクラスを呼び出すだけにする。
**リスク:** 高 — 自動カバレッジ皆無、gutter境界のオフバイワンは目に見えにくい形で混入しやすい。Phase4・5でパターンが確立してから最後に行う。
**検証:** `dotnet build`。`run`スキルで境界ピクセルでの手動クリック/ホバー確認: foldマーカークリック、git blameホバー(ツールチップ表示/非表示、リサイズグリップのドラッグ)、breakpointガタークリック、行番号ガタークリックがfold/blameと誤爆しないこと、gutter幅変更時の組み合わせ。

---

## Phase 7 — MainWindow.xaml.cs: コントローラクラスへの抽出 + 独立クラスのファイル移動

**目的:** App層のホスト(4,559行)にも同じ流儀を適用。
**対象ファイル:** `src/Editor.App/MainWindow.xaml.cs`、新規クラス(`src/Editor.App/`配下):
- `SidebarController`(ファイルツリー+コンテキストメニュー)
- `TabManagerController`
- `MarkdownPreviewController`
- `ReferencesPanelController`

各コントローラは必要なWPF要素(`TreeView`、`TabControl`など)への参照とコールバックだけをコンストラクタで受け取り、`MainWindow`がフィールドとして所有する。

加えて、ファイル末尾の独立クラス(`FileTreeModel`、フォルダ選択COMラッパー、シェルコンテキストメニューCOMラッパー)は`MainWindow`にネストしていないため、単純に別ファイルへ移動するだけでゼロリスク(これはpartial class化ではなく、元々独立したトップレベルクラスの置き場所を変えるだけ)。
**リスク:** 低(独立クラスの移動)〜中(コントローラ抽出。`Editor.App`にはテストプロジェクトが無いため正しさは手動確認のみ)。
**検証:** `dotnet build Editor.sln -c Release`。`run`スキルでのフルスモークテスト: サイドバーからファイルを開く、タブ切り替え、Markdownプレビュー、参照パネル、メニュー項目、Ctrl+F検索ポップアップ、git blame/statusコマンドがMainWindowのイベントハンドラまで正しく到達すること。

---

## あえてフェーズ化しないもの

- **`VimEvent.cs`**(321行、62個のファクトリメソッド): ファイル自体は小さく、各ファクトリは1行の定型コード。テーブル化などの再設計は複雑さを増すだけで見返りが薄く、「再設計しない」という制約に反する。具体的な痛みが顕在化するまでは現状維持を推奨。
- DIコンテナ、MVVM書き換え、ディスパッチテーブル化などのパラダイム変更は一切行わない。
- **partial classによるファイル分割は行わない**(ユーザー指示により禁止)。すべてのフェーズは、既存の分岐ロジックをそのまま維持しつつ、必要な協力者だけをコンストラクタで受け取る独立クラスへの委譲として実施する。

## 実施順序の理由

Phase 1-2(Core層)は最も安全網が強く(グリーンテストで完了判定が明確)、「合成クラスへの委譲」というワークフロー自体をここで検証してから、カバレッジの薄いWPF層(Phase 3-7)に進む。Phase 5-6は「移動」ではなく「制御フロー抽出」なので、Phase 3-4で確立したワークフローに慣れてから着手する。

## 重要ファイル

- `src/Editor.Core/Engine/ExCommandProcessor.cs`
- `src/Editor.Core/Engine/VimEngine.cs`
- `src/Editor.Controls/VimEditorControl.xaml.cs`
- `src/Editor.Controls/Rendering/EditorCanvas.cs`
- `src/Editor.App/MainWindow.xaml.cs`
- `tests/Editor.Core.Tests/VimEngineTests.cs`(Phase2の回帰ゲート)
- `tests/Editor.Core.Tests/ExCommandProcessorTests.cs`(Phase1の回帰ゲート)
- 既存の合成パターン前例: `src/Editor.Core/Engine/MotionEngine.cs`(ステートレス、ctorでTextBufferを受け取る)、`VimEngine`が所有する`BufferManager`/`RegisterManager`/`MarkManager`/`MacroManager`/`FoldManager`、`VimEditorControl`が所有する`LspManager`/`GitDiffProvider`
