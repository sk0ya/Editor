# Editor リファクタリング計画

## ステータス

| Phase | 対象 | 状態 |
|---|---|---|
| 1 | ExCommandProcessor → ドメイン別ハンドラクラス | ✅ 完了 |
| 2 | VimEngine → 関心事別ハンドラクラス | ✅ 完了(縮小スコープ→Phase 2拡張で追加抽出) |
| 3 | VimEditorControl → マネージャクラス抽出 | ✅ 完了(縮小スコープ) |
| 4 | EditorCanvas → レンダラークラス抽出 | ✅ 完了 |
| 5 | VimEditorControl → PopupKeyNavigator | ✅ 完了(手動GUI確認は省略) |
| 6 | EditorCanvas → GutterHitTester | ✅ 完了(手動GUI確認は省略) |
| 7 | MainWindow → コントローラクラス抽出 | ✅ 完了(手動GUI確認は省略) |

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

## Phase 2 拡張 — VimEngine: 見送っていた領域を含めて再着手 ✅(部分的にスコープ縮小)

**目的:** ユーザーから改めて「VimEngineを徹底的にリファクタリングしてコード行を減らして」との依頼があり、Phase 2で対象外とした`ModeTransitionCommands`/`EditingCommands`/`SearchNavCommands`(高リスク領域)を含めることも明示で確認した上で再着手。

**結果:** `VimEngine.cs` は 6,249 → 4,605行(-26.3%)。`src/Editor.Core/Engine/` 配下(ステートレス、`MotionEngine`と同じ流儀)に:
- `TextObjectEngine`(テキストオブジェクト: `GetRange`/`GetWordRange`/`FindEnclosingPair`/`FindEnclosingQuote`/`GetTagRange`/`GetSentenceRange`/`GetParagraphRange`/`WordEndBackward`) — `TextBuffer`をctorで受け取り、カーソルは呼び出し時の引数
- `BlockRangeCalculator`(static) — Visual Blockの座標計算(`GetBounds`/`GetLeftColumn`/`GetLineRanges`/`BuildEditColumns`/`BuildAppendToLineEndColumns`)。ブロック末尾拡張フラグ(`_visualBlockToLineEnd`等)は呼び出し元がそのまま引数で渡す
- `CompletionCollector`(static) — Ctrl+N/P・Ctrl+X Ctrl+F・Ctrl+X Ctrl+Lの補完候補収集。`BufferManager`のみに依存

`src/Editor.Core/Engine/Ops/` 配下(新設フォルダ。VimEngineへの参照は持たず、必要なコールバック(undo snapshot・イベント発火・カーソル移動)だけをctorで受け取り、`_cursor`はVimEngineが引き渡し/受け取る形で所有権を保持し続ける — `Commands.FoldCommands`の`Result`パターンと同型):
- `ClipboardEditOps` — delete/yank/paste系(`ExecuteDelete`/`DeleteLines`/`YankRange`/`YankLines`/`PasteAfter`/`PasteBefore`/`InsertLinewisePaste`/`InsertCharacterwiseText`/`CursorOnLastInsertedChar`/`ExecuteIndentedPaste`/`DeleteBlock`/`YankBlock`)
- `TextTransformOps` — join/大文字小文字変換/indent/コメントトグル/`gq`整形/surround(`JoinLines`系/`ToggleCase`/`ApplyCaseConversion`系/`IndentRange`/`ToggleCommentLines`系/`FormatText`/`ApplySurround`系/`ExecuteReplace`/`ExecuteIncrementNumber`)
- `RepeatTracker` — dot-repeat(`.`)状態機械。`_lastRepeatChange`等の状態を丸ごと保持し、`ExecuteNormalCommand`/`ProcessKeyInternal`/`ProcessStroke`への再帰呼び出しはコンストラクタ経由のコールバックデリゲートで対応
- `SearchOps` — 検索状態(`Pattern`/`Forward`/`PreSearchCursor`)と実行ロジック(`DoSearch`/`DoIncrSearch`/`FindGnMatch`/`RepeatFind`/`SearchNext`/`SearchWordUnderCursor`)を状態ごと保持

いずれも`ExecuteNormalCommand`/`HandleVisual`/`ApplyVisualMotion`等の既存switch/if-chainはそのまま残し、各case本体を`_xxxOps.Method(...)`という委譲に置き換えただけ。ClipboardEditOps/TextTransformOpsの抽出中、「ローカル変数で計算したカーソルをコールバックで`EmitText`する前に`_cursor`フィールドへ反映していないと`VimEvent.CursorMoved`が古い座標を運ぶ」という抽出特有のバグ候補を発見し、`emitTextAt(events, cursor)`(カーソルをセットしてから`EmitText`する)という専用コールバック形状で解消した(全テストgreenで検証済み)。

### 今回も対象外にしたもの

- **`ModeTransitions`(`EnterInsertMode`/`ExitInsertMode`/`EnterVisualMode`/`ExitVisualMode`/`EnterCommandMode`/`EnterSearchMode`/`ChangeMode`/`MoveCursor`/`UpdateSelection`)** — 実装前の調査で、これらが`_mode`/`_cursor`だけでなく、insertセッション状態(`_insertStart`/`_insertedText`/`_kwCompletion*`等)・visualセッション状態(`_visualStart`/`_lastVisual*`/`_awaitingVisual*`等)・コマンドライン状態(`_cmdLine`)など**25個以上のVimEngine内部フィールド**を横断的に読み書きしていることが判明した。抽出するには同数の`internal`アクセサをVimEngineに追加する必要があり、これは結合を減らさず「バックドアを増やすだけ」の見せかけの抽出になる(Phase 2/3で対象外にした領域と同一の構造で、むしろフィールド数はそれ以上)。ユーザーに確認の上、今回も対象外としRepeatTracker/SearchOpsに進むことで合意した。`_mode`/`_cursor`フィールド自体は数百箇所から直接参照されており、所有権移動はそもそも非現実的なためVimEngineに残置。
- `HandleNormal`/`HandleVisual`/`HandleInsert`/`ExecuteNormalCommand`のswitch/if-chain構造自体、および`EnterSearchMode`/`HandleCommandLine`の検索UI部分(コマンドライン状態と混在するため、`SearchOps`のプロパティへのフィールド参照置き換えのみ実施しVimEngine側に残置)。

**検証:** 各ステップ後に `dotnet build src/Editor.Core/Editor.Core.csproj` + `dotnet test tests/Editor.Core.Tests/`(全1051件)をグリーンで確認。最終段階で `dotnet build Editor.sln` + `dotnet test tests/Editor.Controls.Tests/`(全9件)もグリーン。挙動を変えない機械的な移動であることは各ステップのdiffで確認済み(手動GUI確認は実施せず、既存フェーズと同様ビルド・テストgreenを根拠とした)。

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

## Phase 4 — EditorCanvas.cs: `OnRender`の描画専用をレンダラークラスへ抽出 ✅

**目的:** 3,122行から、`OnRender`が既にフラットな`Draw*`呼び出し列になっている描画ドメインをレンダラークラスにまとめる。

**結果:** `EditorCanvas.cs` は 3,122 → 2,605行(-17%)。`src/Editor.Controls/Rendering/` 配下に以下を新設:
- `GlyphMetrics`(29行) — `FormatText`/`FormatScaledText`/`GetVisualX`/`LineHeight`/`CharWidth` を関数(メソッドグループ)として束ねるだけの小さなラッパー。ほぼ全ての`Draw*`が文字幅計測・整形に依存していたため、各レンダラーに`EditorCanvas`本体への参照を持たせず必要な計測だけを渡す橋渡し役として導入(`MotionEngine`が`TextBuffer`をctor引数に取るのと同じ発想)。`EditorCanvas.OnRender`冒頭で`BuildGlyphMetrics()`により1回だけ構築する。
- `PopupChrome`(20行) — signature help/completion/code-action/IME候補ポップアップが共有するテーマ非依存の背景色・枠線ブラシ(旧`s_popupBg1/2/3/DocBg/Border`)。
- `GutterRenderer`(116行) — 行番号+fold指標+gitdiffバー(`DrawLineNumberAndFold`)、changed-since-saveバー(`DrawSaveDiffBar`)、blame margin(`DrawBlameMargin`)の**描画のみ**。ヒットテスト(fold click, blame click, カラムリサイズ)は`EditorCanvas`に残した。
- `OverlayRenderer`(221行) — minimap、`ScrollbarLayout`構造体+`ComputeScrollbarLayout`+`DrawScrollbars`(スクロールバーの幾何計算は描画とマウスヒットテストの両方から参照されるためこのクラスに集約し、`EditorCanvas`側のドラッグ処理はこれを呼び出すだけにした)、color column、indent guides、whitespace issues、color swatches。
- `LspOverlayRenderer`(298行) — signature help、completion popup(+ `WrapDocText`、kind別ブラシ選択)、code action popup、inlay hints、document highlights。ポップアップ位置決めに使う`GetCursorPixelPosition()`の戻り値は`EditorCanvas`側で計算して`Point`として渡す(このメソッド自体はIME配置でも使われる`public`メソッドなので残置)。

計画時点で挙げた「semantic tokens」は独立した`Draw*`呼び出しではなく通常のテキスト描画(`DrawLineTextWithSegments`)に統合されているため、`LspOverlayRenderer`の対象から外した(描画呼び出し単位で見て実体が無かったため)。各レンダラーは`static`クラス・`static`メソッドとし、必要な状態(コレクション・スカラー値・`GlyphMetrics`・`EditorTheme`)を毎回引数で受け取る徹底したステートレス設計にした(フィールドを持たず、`EditorCanvas`が保持する状態を書き換えない)。`EditorCanvas.OnRender`のトップレベルの呼び出し順序・早期リターン構造は変更していない。

**検証:** 各抽出後に `dotnet build src/Editor.Controls/Editor.Controls.csproj` + `dotnet test tests/Editor.Core.Tests/`(全1051件)+ `dotnet test tests/Editor.Controls.Tests/`(全9件)をグリーンで確認。最終段階で `dotnet build Editor.sln` もグリーン。`run`スキルでのビフォーアフタースクリーンショット比較は、アプリ起動後のスクリーンキャプチャに本セッションと無関係な内容が写り込む環境問題が発生したためユーザーの判断でスキップし、ビルド成功+全テストgreenのみを根拠に完了とした。挙動を変えない機械的なコード移動(同じ算術・同じブラシ、暗黙のフィールド参照を明示的な引数に置き換えただけ)であることは差分レビューで確認済み。

---

## Phase 5 — VimEditorControl: LSP/パス補完ポップアップのキー処理を共通クラスへ集約 ✅

**目的:** 補完・コードアクション・パス補完で繰り返される「ポップアップ表示中: Down/Upで選択移動, Tab/Returnで適用, Escapeで閉じる」ブロック(旧3691-3788行付近)を、共通の`PopupKeyNavigator`クラスに集約。

**結果:** `src/Editor.Controls/PopupKeyNavigator.cs`(47行)を新設。`Action<int> move` / `Action apply` / `Action hide` の3デリゲートと、キーセット差分を吸収する3フラグ(`acceptCtrlNav`/`acceptJK`/`acceptTab`)をctorで受け取り、`TryHandle(Key, bool ctrl)`が「Down/J/Ctrl+N → move(1)」「Up/K/Ctrl+P → move(-1)」「Return/Tab → apply()」「Escape → hide()」の順で最初に一致した1つだけを実行して`true`を返す(既存if-chainの早期return列と同じ優先順位)。`VimEditorControl`は3つのポップアップぶんインスタンスをフィールドとして所有し、コンストラクタで各ポップアップ固有の副作用(パス補完のTab/Escapeが`_keyDownHandledByVim`を立てる、LSP補完のEscapeが`HideSignatureHelp`+`ProcessKey("Escape",...)`でInsertモードごと抜ける、コードアクションはCtrl+N/PもTabも受け付けずJ/Kのみ)をラムダに閉じ込めた。`OnKeyDown`側の3ブロックはガード条件(`Visible && mode && IME guard`)をそのまま残し、中身を`_xxxNavigator.TryHandle(key, ctrl)`の1呼び出しに置き換えただけで、呼び出し順・早期return構造は変更していない。

参照パネル(`gr`)のキー処理は`MainWindow.xaml.cs`(Editor.App層)にあり、当初の対象語("参照パネル")に反して本フェーズのスコープ外(Phase 7で扱う)と判明したため対象から外した。

**検証:** `dotnet build src/Editor.Controls/Editor.Controls.csproj` + `dotnet build Editor.sln` + `dotnet test tests/Editor.Core.Tests/`(全1051件)+ `dotnet test tests/Editor.Controls.Tests/`(全9件)をグリーンで確認。`run`スキルでアプリを起動し、キーボード操作のみでLSP補完ポップアップ(Down/Down→選択移動を目視確認、Tab→挿入されInsertモード維持、再トリガー→Escapeで非表示+Insert離脱の両方を確認)とパス補完ポップアップ(Down/Down→選択移動、Tab→挿入されInsertモード維持)を実際に確認済み。コードアクションポップアップ(J/K/Return/Escape)は、テスト用の診断/コードアクションを用意する過程でウィンドウのフォーカス制御が意図せず外れ、無関係な別ウィンドウをクリックしてしまう事故が発生したため、ユーザーの判断でGUI手動確認を中止。ビルドgreen・テストgreenに加え、diffレビュー(各ラムダが元のif-block本体の逐語コピーであること)を根拠に完了とした。

---

## Phase 6 — EditorCanvas: gutterのマウス/ヒットテストを共通クラスへ集約 ✅

**目的:** `OnMouseMove`/`OnMouseLeftButtonDown`でblame/breakpoint/fold/行番号ガター毎に重複している座標範囲チェックを統合。

**結果:** `src/Editor.Controls/Rendering/GutterHitTester.cs`(63行)を新設。`Boundaries`レコード構造体(`BlameColWidth`/`BpColWidth`/`LineNumWidth`/`GutterWidth` — `GetGutterMetrics()`の戻り値+`_blameColWidth`をそのまま渡す)と、`TryHitBlameGutter/TryHitBreakpointGutter/TryHitFoldGutter/TryHitLineNumberGutter(point, boundaries, out line)`の4メソッドを提供する。各メソッドは対象列の座標範囲チェックのみを行い、ヒットすればコンストラクタで受け取った`Func<Point,int> lineResolver`(`EditorCanvas.HitTestGutterLine`をそのまま渡す)でバッファ行を解決する。fold/blame/breakpoint固有の副作用(ツールチップ開閉・カーソル形状・ホバー状態・イベント発火)は元のまま`EditorCanvas`側に残し、座標範囲の判定式だけを置き換えた。

`_breakpointsEnabled`や`_showLineNumbers`によるガードの一部は、対応する幅(`bpColWidth`/`lineNumWidth`/`foldColWidth`)が`GetGutterMetrics()`側で既に0になっている(=範囲チェック自体が常にfalseになる)ため、`GutterHitTester`側では冗長として持ち込んでいない。呼び出し側で明示的にガードが必要な箇所(`_showLineNumbers`)はそのまま残した。

**検証:** `dotnet build src/Editor.Controls/Editor.Controls.csproj` + `dotnet build Editor.sln` + `dotnet test tests/Editor.Core.Tests/`(全1051件)+ `dotnet test tests/Editor.Controls.Tests/`(全9件)をグリーンで確認。過去のPhaseで自動マウス操作が実デスクトップの別ウィンドウを誤クリックする事故が発生していたため、ユーザーの判断で今回は手動GUIクリック/ホバー確認を省略し、ビルドgreen・テストgreenと、各分岐が元のif本体の座標式をそのまま`GutterHitTester`呼び出しに置き換えただけであることのdiffレビューを根拠に完了とした。

---

## Phase 7 — MainWindow.xaml.cs: コントローラクラスへの抽出 + 独立クラスのファイル移動 ✅

**目的:** App層のホスト(4,559行)にも同じ流儀を適用。

**結果:** `MainWindow.xaml.cs` は 4,559 → 2,652行(-42%)。`src/Editor.App/` 配下に新設:
- `SidebarController`(587行) — Explorerサイドバーの表示/非表示、ファイルツリーのキーボード操作・右クリックコンテキストメニュー・フォルダ読み込みを状態ごと丸ごと保持。`SidebarPanel` enumもここに合わせて移動(トップレベル`internal enum`化)。
- `TabManagerController`(268行) — `FileTab`一覧・`CreateEditor`(エディタペイン生成)・タブの追加/選択/クローズ。`FileTab`クラスもトップレベル`internal`化して移動。
- `MarkdownPreviewController`(384行) — プレビューペインのペインツリーへの着脱、WebView2でのHTML描画、エディタ⇔プレビュー間のスクロール同期。
- `ReferencesPanelController`(603行) — 参照/quickfixパネル、プロジェクトgrep/置換、バッファ単位のlocation list(diagnostics/search)。`ReferenceListItem`/`BufferLocationList`/`LocationListSource`もトップレベル`internal`化して移動。

各コントローラは必要なWPF要素(`TreeView`、`TabControl`、`ListBox`など)への参照と、所有権を持たない共有状態(フォーカス中のエディタ、全エディタ列挙、サイドバーのプロジェクトフォルダ、共有`ShowInputDialog`ヘルパー、ファイルを開く処理など)へのコールバックだけをコンストラクタで受け取り、`MainWindow`がフィールドとして所有する。既存のディスパッチ点(XAMLが名前で参照するイベントハンドラ、`WireEditorEvents`のif-chain)はそのまま残し、中身を`_sidebar.Xxx(...)`/`_tabs.Xxx(...)`/`_markdownPreview.Xxx(...)`/`_refs.Xxx(...)`という委譲に置き換えた。

実装に着手して判明した想定外の発見が2つあった:
- **`PaneNode`/`EditorPaneNode`/`SplitPaneNode`/`PreviewPaneNode` + `PaneToElement`/`FindParentSplit`/`FindEditorPane`(旧「Pane tree」領域)** は元々`MainWindow`にネストされたステートレスなツリー構造体+アルゴリズムで、ウィンドウ分割管理とMarkdown Previewのペイン着脱ロジックの両方から対等に参照される共有基盤だった。当初計画には無かったが、`MotionEngine`と同じ「ステートレスな協力者」パターンに合致するため、`PaneTree.cs`にトップレベル`internal`クラス+`PaneTreeHelpers`静的クラスとして先に切り出し(ゼロリスクな移動のみ)、その後の`MarkdownPreviewController`抽出がこれを`_globalRoot`の get/setコールバック越しに利用できるようにした。
- **`OutlineItem`/`FileTreeItem`/`NativeFolderPicker`/`ShellMenuContext`** はファイル末尾で`MainWindow`にネストしていなかった独立クラスで、計画通りゼロリスクな`FileTreeItem.cs`/`NativeFolderPicker.cs`/`ShellMenuContext.cs`への機械的移動のみで対応した。

`WireEditorEvents`自体(~25個のイベント購読)は`MainWindow`に残した。購読先の大半(git/terminal/session/quickfix等)が別コントローラの対象外の関心事にまたがる共有ディスパッチ窓口であり、Phase 2/3で見送った`ModeTransitionCommands`/`ImeCompositionManager`と同じ構造(「switchの委譲だけ動かす見せかけの抽出」か「20箇所以上の呼び出し元を書き換える高リスクな書き換え」の二択)だったため。`TabManagerController.CreateEditor`はこれを`wireEditorEvents`コールバック経由で呼び出すだけに留めた。同様に`OpenFile`/`ResolvePath`/`ShowInputDialog`/`_focusedEditor`/`_globalRoot`は複数コントローラ+`MainWindow`本体から横断的に参照される共有状態のため`MainWindow`側に残し、各コントローラへは狭いコールバック(`Func<VimEditorControl?>`など)として渡した。

**検証:** 各抽出後に `dotnet build Editor.sln` + `dotnet test tests/Editor.Core.Tests/`(全1051件)+ `dotnet test tests/Editor.Controls.Tests/`(全9件)をグリーンで確認。最終段階で `dotnet build Editor.sln -c Release` もグリーン(警告0件)。Phase 4-6で自動GUI操作がデスクトップ上の無関係なウィンドウを誤クリックする事故が繰り返し発生していたため、ユーザーの判断で今回も`run`スキルでの手動GUIスモークテストを省略し、ビルドgreen・テストgreenと、各移動が元のメソッド本体の逐語コピー+コールバック経由の参照置き換えに留まることのdiffレビューを根拠に完了とした。`Editor.App`にはテストプロジェクトが無いため、これがこのフェーズで得られる最も強い保証となる。

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
- `src/Editor.Core/Engine/Ops/`(Phase 2拡張で新設) — `ClipboardEditOps`/`TextTransformOps`/`RepeatTracker`/`SearchOps`。VimEngineへの参照は持たず、必要なコールバックだけをctorで受け取るパターンの参照実装
