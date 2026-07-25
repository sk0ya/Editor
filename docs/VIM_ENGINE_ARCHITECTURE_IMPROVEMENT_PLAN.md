# VimEngine アーキテクチャ改善計画

## 1. 目的

`VimEngine` に集中している状態管理、モード処理、編集処理、コマンド実行を段階的に分離し、次の状態を実現する。

- 不正な入力待ち状態を型によって表現できる
- 編集操作が必ず Undo、read-only 検査、イベント通知を通る
- Normal と Visual で motion の計算を共有できる
- モードごとの処理を独立してテストできる
- 新しいコマンドや文法を `VimEngine` の修正なしで追加できる
- エンジン間で拡張登録が意図せず共有されない
- 既存の Vim 動作と公開 API の互換性を維持する

## 2. 対象範囲

対象:

- `VimEngine`
- Normal、Insert、Replace、Visual、CommandLine、Plain 各モード
- pending input の状態管理
- Normal command と Visual motion の実行
- motion の計算と適用
- 編集トランザクション
- command parser とコマンド文法登録
- registry のライフサイクル

対象外:

- WPF コントロールの描画方式
- LSP プロトコル実装
- Syntax tokenizer のアルゴリズム
- Ex command 自体の全面的な再設計
- 新しい Vim 機能の追加

## 3. 現状の主な問題

### 3.1 `VimEngine` の責務過多

`VimEngine` が以下を同時に所有している。

- モードとサブモード
- カーソルと選択
- 入力待ち状態
- コマンド登録
- 編集操作
- Undo と repeat
- 検索
- マークとマクロ
- フォールド
- command line
- viewport
- `VimEvent` の生成

### 3.2 pending input が複数の bool で表現されている

複数フラグが同時に有効になる不正状態を型で防げない。状態の開始、キャンセル、モード変更時の破棄も各所に分散している。

### 3.3 編集処理に共通のトランザクション境界がない

各ハンドラーが個別に以下を管理している。

- read-only 検査
- Undo snapshot
- repeat tracking
- cursor clamp
- syntax invalidation
- `TextChanged` と `CursorMoved`

処理追加時の呼び忘れが、Undo や表示同期の不具合につながる。

### 3.4 コマンドハンドラーが `VimEngine` 全体へ依存している

組み込みハンドラーは private 状態へ直接アクセスし、拡張ハンドラーには `VimEngine` 本体が公開される。必要以上の操作が可能で、エンジンの不変条件を保証できない。

### 3.5 Normal と Visual の motion 適用が重複している

同じ motion について、Normal はカーソル移動、Visual はカーソル移動と選択更新を別実装している。片方だけ修正される可能性がある。

### 3.6 command parser の文法がハードコードされている

実行ハンドラーは登録可能でも、新しい operator、motion、複数キーコマンド、text object の追加には `CommandParser` の変更が必要になる。

### 3.7 mutable なグローバル registry

`Default` registry への登録が複数のエンジン、ウィンドウ、テストへ漏れる可能性がある。共有範囲が依存注入ではなく暗黙の singleton で決まっている。

### 3.8 dispatcher と registry の重複

exact match、pattern match、replace、restore、priority といった類似処理が複数クラスに分散している。

## 4. 目標アーキテクチャ

```text
VimEngine
 ├─ KeyInputPipeline
 ├─ ModeCoordinator
 │   ├─ NormalModeController
 │   ├─ InsertModeController
 │   ├─ VisualModeController
 │   ├─ CommandLineController
 │   └─ PlainEditController
 ├─ PendingInputState
 ├─ CommandRouter
 ├─ MotionService
 ├─ EditTransactionService
 └─ VimEngineServices
```

### 4.1 `VimEngine` の最終的な責務

- 現在のモードを保持する
- 入力を `KeyInputPipeline` へ渡す
- mode controller の切り替えを調停する
- controller が生成した結果を呼び出し元へ返す
- 公開 API の互換レイヤーを提供する

編集アルゴリズムや入力待ちの詳細は保持しない。

### 4.2 コマンド実行コンテキスト

ハンドラーには `VimEngine` 本体ではなく、必要な能力だけを公開する。

想定する能力:

- 読み取り専用の buffer 情報
- 現在の cursor、selection、mode
- motion の計算
- transaction を通した編集
- cursor と selection の更新
- event の追加
- register、mark、search などの限定サービス

### 4.3 編集トランザクション

すべての buffer 変更を一つの入口へ統合する。

トランザクションが保証するもの:

- read-only 検査
- 変更前 snapshot
- 失敗時に部分変更を残さないこと
- cursor clamp
- syntax invalidation
- Undo 単位
- repeat tracking
- `TextChanged` と `CursorMoved` の重複排除

### 4.4 motion の二段階処理

motion を「計算」と「モード別適用」に分ける。

```text
MotionRequest
  → MotionService.Calculate
  → MotionResult
      ├─ NormalMotionApplier
      ├─ VisualMotionApplier
      └─ OperatorMotionApplier
```

`MotionResult` は少なくとも以下を含む。

- 開始位置
- 到達位置
- inclusive/exclusive
- characterwise/linewise/blockwise
- jump list 追加の要否
- sticky column 更新の要否

## 5. 実施フェーズ

### 進捗（2026-07-25）

- Phase 0: 完了。Core の既存動作を特性テストで固定し、全テスト成功を確認済み。
- Phase 1: 完了。Normal、Insert、Visual の排他的な文字入力待ちを
  `PendingInputState` と `PendingInputController` へ移行し、旧 `_awaiting...`
  フラグを削除済み。parser が所有する register、find、mark、replace の待機も
  同じ controller から観測・キャンセルできる。
- Phase 2: 完了。`IEditTransactionService` を導入し、Normal、Insert/Replace、
  Visual、Plain、Ex、deferred surround、paste、formatter/LSP外部変更を共通境界へ
  移行済み。read-only、rollback、snapshot、cursor clamp、syntax invalidation、
  repeat metadata、イベント重複排除をtransactionが管理する。
- Phase 3: 完了。公開・組み込みNormal handlerは`INormalCommandContext`を受け取り、
  `VimEngine`本体を参照しない。bufferはread-only viewとして公開し、変更は
  contextのtransaction capabilityに限定した。cursor、motion、registerも限定能力
  として提供し、handlerをengineなしで単体テスト可能にした。
- Phase 4: 完了。`MotionRequest`、`MotionResult`、`MotionService`を導入し、
  Normal、Visual、Operatorのtarget計算を共通化した。operator固有の`cw`/`dw`、
  find、inclusive/exclusive、linewise metadataもserviceが解決し、fold/display
  awareな`IMotionOverride`拡張点を提供する。
- Phase 5: 完了。`ModeCoordinator`とNormal、Insert、Replace、Visual、
  CommandLine、Plainの独立controllerを導入した。controllerはbufferや相互参照を
  持たず、transactionで保護されたmode処理adapterだけを呼ぶ。入力dispatchと
  programmatic transitionはcoordinatorへ集約し、旧`Handle...` entry pointを削除した。
- Phase 6 以降: 未着手。

## Phase 0: ベースライン固定

目的:

- 構造変更前の挙動をテストで固定する

作業:

- 現在の Core テストをベースラインとして記録する
- Normal、Insert、Visual、operator-motion の代表的なイベント列を追加検証する
- Undo、repeat、register、read-only の組み合わせテストを追加する
- registry のエンジン間分離テストを追加する

完了条件:

- 現在の全テストが成功する
- 主要操作について buffer、cursor、mode、events を同時に検証できる

## Phase 1: pending input の状態型導入

目的:

- 複数 bool による暗黙状態機械を廃止する

作業:

- `PendingInputState` の discriminated hierarchy を導入する
- mark、register、replace、surround、digraph、completion の待機状態を移行する
- pending 状態の開始、完了、キャンセルを一つの controller に集約する
- Escape、モード変更、Vim 無効化時の状態破棄を統一する

移行方針:

- 最初は既存 bool と状態型を同期させない
- 一種類ずつ bool を削除し、完全に状態型へ移す
- 一つの pending 種類につき一つのコミットに分ける

完了条件:

- 対象となる `_awaiting...`、`_pending...` フラグが削除される
- 同時に複数の排他的待機状態を保持できない
- pending 状態単体のテストが可能になる

## Phase 2: 編集トランザクション導入

目的:

- buffer 変更時の不変条件を一箇所で保証する

作業:

- `IEditTransactionService` と `EditTransaction` を追加する
- snapshot、cursor clamp、event emission を transaction に移す
- delete、insert、replace、paste、indent、join を順次移行する
- read-only 検査を transaction 開始時へ移す
- repeat metadata を transaction result として返す

移行順:

1. 単一文字 insert/delete
2. paste
3. operator + motion
4. linewise edit
5. blockwise edit
6. formatterや外部変更

完了条件:

- buffer を直接変更するコードが許可された低レベル層だけに限定される
- mutating command が直接 `Snapshot()` や `EmitText()` を呼ばない
- 一操作につき Undo snapshot と TextChanged が一回だけ生成される

## Phase 3: command context の権限制限

目的:

- コマンドハンドラーと `VimEngine` の密結合を解消する

作業:

- `INormalCommandContext` を定義する
- `NormalCommandContext.Engine` を段階的に廃止する
- buffer、motion、transaction、events、register へ限定インターフェースを提供する
- 組み込みハンドラーも公開拡張と同じ context を使用する
- context 外からの cursor、selection、mode の直接変更を禁止する

完了条件:

- コマンドハンドラーが `VimEngine` 型を参照しない
- 拡張ハンドラーが Undo や event 規約を回避できない
- handler を `VimEngine` なしで単体テストできる

## Phase 4: motion 計算の統合

目的:

- Normal、Visual、Operator で重複している motion 処理を統合する

作業:

- `MotionRequest` と `MotionResult` を導入する
- Normal/Visual/Operator の motion 解決を `MotionService` に統合する
- mode ごとの差は applier へ移す
- find、search、paragraph、section、method jump を共通結果へ変換する
- fold-aware、display-line-aware motion の拡張点を定義する

完了条件:

- 同一 motion の移動計算が一実装だけになる
- Normal/Visual の違いは result の適用方法だけになる
- operator-motion が独自に motion を再計算しない

## Phase 5: mode controller 分離

目的:

- モード固有状態と入力処理を `VimEngine` から移す

作業:

- `NormalModeController` を抽出する
- `InsertModeController` と `ReplaceModeController` を抽出する
- `VisualModeController` を抽出する
- `CommandLineController` を抽出する
- `PlainEditController` を抽出する
- `ModeCoordinator` が遷移と enter/leave hook を管理する

controller の制約:

- buffer を直接変更しない
- 編集は transaction を使用する
- 他modeの内部状態へ直接アクセスしない
- 遷移要求は `ModeTransition` として返す

完了条件:

- `HandleNormal`、`HandleInsert`、`HandleVisual`、`HandleCommandLine`、`HandlePlainTextKey` が `VimEngine` から削除される
- 各controllerを独立してテストできる
- `VimEngine` が入力処理の詳細を持たない

## Phase 6: command grammar の登録型化

目的:

- 新しい複数キーコマンドを parser 修正なしで追加できるようにする

作業:

- `CommandDefinition` を導入する
- action、motion、operator、text object、prefix を定義可能にする
- 登録済み定義から prefix tree/trie を構築する
- count、register、operator + motion の合成規則を分離する
- `g`、`z`、`[`、`]` などのprefixをデータ定義へ移す
- parser diagnostic と不完全入力状態を型で返す

完了条件:

- 新規exact commandとprefix commandをparser本体の変更なしで追加できる
- parserにコマンド固有の文字列switchが存在しない
- 文法衝突を登録時に検出できる

## Phase 7: registry と dispatcher の共通化

目的:

- exact/pattern/priority/replace の重複実装を統合する

作業:

- 汎用 `CommandTable<TKey, TContext, TResult>` を導入する
- built-in、extension、user mapping のlayerを定義する
- priorityとshadowing規則を明文化する
- immutable snapshotによる読み取りを導入する
- 登録一覧と衝突diagnosticを提供する

完了条件:

- Normal、Visual、key bindingの登録解決が共通基盤を使う
- 優先順位が呼び出し順ではなく明示的なlayerで決まる
- 重複登録や到達不能登録を検出できる

## Phase 8: global Default の排除

目的:

- registry とserviceの共有範囲を明示する

作業:

- `VimEngineServices` を導入する
- registry、edit assist、syntax、clipboardなどをまとめて注入する
- アプリケーション単位のshared services factoryを用意する
- テストでは毎回isolated servicesを生成する
- `Default` は互換用factoryへ縮小し、mutable singletonを廃止する

完了条件:

- 新しい`VimEngine`が暗黙のmutable global stateを参照しない
- 二つのengine間で登録が漏れない
- ホストが意図した場合だけregistryを共有できる

## Phase 9: `VimEngine` の縮小と互換レイヤー整理

目的:

- `VimEngine` を調停役へ限定する

作業:

- controller、service、registryへ移行済みのprivate methodを削除する
- 公開APIをfacadeとして整理する
- obsolete予定のAPIを文書化する
- architecture dependency testを追加する

目標:

- `VimEngine` はおおむね500行以下
- private mutable stateはmode、cursor facade、service参照に限定
- command実装を持たない

完了条件:

- `VimEngine` の責務がクラスコメントで一文に説明できる
- 新しいコマンド追加で`VimEngine.cs`を変更しない
- mode機能追加で他mode controllerを変更しない

## 6. 推奨コミット単位

各フェーズを一括コミットしない。以下を原則とする。

- 新しい抽象と既存adapter
- 一種類の状態または一種類の編集操作を移行
- 対応テスト追加
- 旧経路削除

一コミットで構造変更と新機能追加を混在させない。

## 7. 互換性方針

- `VimEngine.ProcessKey` の署名を維持する
- 既存の `VimEvent` 順序を原則維持する
- `.vimrc` mapping の優先順位を維持する
- Undo と repeat の単位を変更しない
- public constructor の変更はoptional parameterまたはfactoryで吸収する
- deprecated API は少なくとも一リリース互換レイヤーを残す

## 8. 検証戦略

各フェーズで以下を実行する。

```bash
dotnet test tests/Editor.Core.Tests/
dotnet build Editor.sln
```

追加する検証:

- buffer text
- cursor
- mode
- selection
- register
- undo/redo
- repeat
- eventの型と順序
- pending state
- engine間のregistry分離

構造検証:

- mode controllerから別mode controllerへの参照禁止
- command handlerから`VimEngine`への参照禁止
- transaction外のbuffer mutation禁止
- CoreからWPF assemblyへの参照禁止

## 9. リスクと対策

### イベント順序の変化

対策:

- characterization testで既存順序を固定する
- transaction resultから決定的にeventを生成する

### Undo単位の変化

対策:

- operationごとにsnapshot数を検証する
- Insert sessionとrepeatの境界を専用テストで固定する

### mode遷移hookの欠落

対策:

- `ModeCoordinator`へenter/leaveを集約してからcontrollerを移す
- autocmd発火回数を検証する

### parser互換性の破壊

対策:

- grammar移行は最後に行う
- 既存parserと新parserを同一入力で比較する移行期間を設ける

### 過度な抽象化

対策:

- 一つ以上の実利用箇所ができるまで汎用化しない
- command handlerは小さく保ち、service境界を増やしすぎない

## 10. 実施順序

推奨順序:

1. Phase 0: ベースライン固定
2. Phase 1: pending state
3. Phase 2: edit transaction
4. Phase 3: command context
5. Phase 4: motion統合
6. Phase 5: mode controller
7. Phase 6: grammar登録
8. Phase 7: registry共通化
9. Phase 8: global state排除
10. Phase 9: `VimEngine`縮小

pending stateとedit transactionを先に行う。これらを残したままmode controllerを抽出すると、現在の暗黙状態と編集規約を複数クラスへ拡散させるためである。

## 11. 最終完了基準

- `VimEngine` がcommand実装を持たない
- 排他的pending stateが型で表現される
- buffer mutationがtransaction経由に限定される
- Normal/Visual/Operatorが共通motion計算を使用する
- 各modeが独立controllerとしてテスト可能である
- command grammarを登録で追加できる
- registryの共有範囲が明示される
- 全既存テストと追加architecture testが成功する
- ソリューション全体が警告なしでビルドできる
