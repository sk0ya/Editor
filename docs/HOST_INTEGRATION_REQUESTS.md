# ホスト統合のための API 追加要望（from Loomo）

このドキュメントは、`sk0ya.Editor.Controls`（`VimEditorControl`）を**ホストアプリに埋め込んで**使う側
（具体的には Loomo: C#/WPF の AI エージェントアプリ）からの API 追加要望をまとめたものです。

対象バージョン: `sk0ya.Editor.Controls` 1.0.0 / `Editor.Core` 1.0.0
作成日: 2026-05-31

> **ステータス: 実装済み（1.0.1）。** 要望 1〜5 はすべて 1.0.1 で対応:
> `VimEditorControl.Save(string?)` / `OpenVirtualDocument(title, content, syntax?)` / `MarkSaved(string?)` /
> `IsModified` / `IsVirtualDocument`、および `SaveRequestedEventArgs` への `IsVirtual` / `DocumentId` 追加。
> Loomo は 1.0.1 を参照し、仮想ドキュメント方式（一時ファイル不要）へ移行済み。以下は当初要望の記録。

---

## 背景：ホストでやりたいこと

Loomo では、設定の**長文項目**（システムプロンプト、危険コマンドのブロックリスト＝正規表現の一覧）を、
狭いサイドバーの `TextBox` ではなく**中央のエディタペインで編集**させたい。理想的には：

1. 任意のテキスト（ファイルに紐づかない「仮想ドキュメント」）をエディタで開く。
2. ユーザーが `:w` で保存したら、その**内容をホストのコールバック**へ渡す（永続先はホストが決める＝
   Loomo では `settings.json`。エディタにファイルを書かせたいわけではない）。
3. 保存後、エディタの **modified フラグが解除**され、`:q` で「未保存」警告が出ないこと。

---

## 現状の API でできること / 困っていること

現行 1.0.0 でも**一応実装は可能**で、Loomo は次のように実装している（ワークアラウンド）：

- `LoadFile(tempPath)` … 一時ファイル（`%TEMP%/Loomo/...`）に内容を書いてから開く（仮想バッファが無いため）。
- `SaveRequested` イベント購読 … `:w` の検知。エディタは自前で書き込まず、ホストに保存を委譲する契約。
- 保存ハンドラ内で `editor.OnSaveStarted()` → **`editor.Engine.CurrentBuffer.Save(path)`** → `editor.OnSaveFinished()`
  を呼んで実書き込み＋modified 解除。
- `editor.Text` で最新内容を取得し、ホストのコールバックへ渡す。

### 困っている点

1. **`Engine.CurrentBuffer.Save(...)` という内部に手を伸ばす必要がある。**
   ホストが保存を完了させる手段が「`Engine`（`Editor.Core` の型）→ `CurrentBuffer`（`VimBuffer`）→ `Save`」しか
   なく、`Editor.Core` の内部構造に密結合する。`Editor.Controls` だけを参照して完結できない。

2. **ファイルに紐づかない「仮想ドキュメント」を開く正規の手段が無い。**
   そのため一時ファイルを作る必要があり、本来ディスクに残したくない内容（設定）がスクラッチファイルとして
   `%TEMP%` に残る。

3. **modified フラグやドキュメント種別をホストから扱う公開 API が乏しい**
   （`IsModified` は公開されておらず、`SaveRequestedEventArgs` は `FilePath` のみで、仮想ドキュメントの識別子が無い）。

---

## 要望（優先度順）

### 1.（高）ホスト向けの保存メソッド `VimEditorControl.Save(string? path = null)`

`SaveRequested` ハンドラ内でホストが呼ぶだけで、`OnSaveStarted` → バッファ保存 → modified 解除 → `OnSaveFinished`
までを内部で完結させるショートカット。`Engine.CurrentBuffer` への到達を不要にする。

```csharp
// 期待する使い方
editor.SaveRequested += (s, e) =>
{
    var content = editor.Text;          // 先に内容を取得
    editor.Save(e.FilePath);            // 内部で OnSaveStarted/CurrentBuffer.Save/OnSaveFinished 相当
    Persist(content);                   // ホスト側の永続化
};
```

これだけでも要望 1（内部依存）は解消する。

### 2.（中）仮想ドキュメントのサポート

ファイルを介さずに「タイトル付きの編集可能バッファ」を開けるようにする。

```csharp
// 例
string id = editor.OpenVirtualDocument(title: "loomo-system-prompt", content: initialText, syntax: "markdown");
```

- ディスクにファイルを作らない（タブのタイトルには `title` を表示）。
- `:w` 時に `SaveRequested` を発火するが、**仮想ドキュメントである識別子**をイベントで渡せること（下記 3）。

### 3.（中）`SaveRequestedEventArgs` の拡張

仮想ドキュメントの保存を識別・処理できるよう、最低限どちらかを追加：

```csharp
public class SaveRequestedEventArgs : EventArgs
{
    public string? FilePath { get; }        // 既存
    public bool   IsVirtual { get; }        // 追加: 仮想ドキュメントか
    public string? DocumentId { get; }      // 追加: OpenVirtualDocument が返した id
    // 内容自体は editor.Text で取得できるため Args に含めなくても可
}
```

### 4.（低）`VimEditorControl.IsModified { get; }` の公開

ホストが「未保存あり」を UI 表示・終了確認に使えるようにする（現状は内部のみ）。

### 5.（低）仮想ドキュメントの modified 解除 `editor.MarkSaved(string? documentId = null)`

ホストが永続化を終えた後に modified を落とすための明示 API（要望 1 の `Save` で代替できるなら不要）。

---

## まとめ

- **要望 1（`Save` メソッド）だけでも実装が大幅にきれいになる**（`Editor.Core` 内部への依存が消える）。これが最優先。
- 仮想ドキュメント（要望 2・3）が入ると、一時ファイルのワークアラウンドが不要になり、設定のような
  「ディスクに残したくない内容」をエディタで安全に編集できる。
- 現状でも Loomo 側は動作するため、これらは**ブロッカーではなく改善要望**。

---

## 追加: メタ情報取得 API（1.0.2）

ホストが「いまエディタで何が起きているか」を読み取れるよう、`VimEditorControl` に
読み取り専用のメタ情報 API を追加。型はすべて `Editor.Controls` に定義され、
ホストは `Editor.Controls` 参照だけで完結する（`Engine` 内部へ手を伸ばす必要なし）。

```csharp
// カーソル位置（0-based。Display* は 1-based 表示用）
CaretInfo caret = editor.Caret;
editor.CaretMoved += (s, c) => ShowStatus($"Ln {c.DisplayLine}, Col {c.DisplayColumn}");

// 選択テキスト
if (editor.HasSelection)
{
    string text = editor.SelectedText;          // 選択中の文字列（Vim 準拠・inclusive）
    TextSelectionInfo? sel = editor.Selection;  // Start/End/Kind(Character/Line/Block) + Text
}
editor.SelectionChanged += (s, sel) => { /* sel == null で選択解除 */ };

// ファイル/ドキュメント メタ
DocumentMeta meta = editor.DocumentInfo;
//   FilePath, IsVirtual, DocumentId, IsModified, LineCount, Language, Mode
```

- 公開型: `CaretInfo` / `TextSelectionInfo` / `SelectionKind` / `DocumentMeta`（すべて `Editor.Controls`）。
- 公開メンバ: `Caret` / `CaretMoved`、`HasSelection` / `SelectedText` / `Selection` / `SelectionChanged`、`DocumentInfo`。
- 選択テキスト抽出は `VimEngine.GetSelectionText()`（副作用なし＝レジスタ非破壊）に集約。
- マウス位置の公開は今回は見送り（必要になれば `EditorCanvas` のヒットテストを中継して追加可能）。

---

## 追加: 分割・タブ ウィンドウ API（1.0.5）

`:split` / `:vsplit` / `:tabnew` / `Ctrl+W` といったウィンドウ・タブ操作を、
ホストがキーストロークを合成せずに**プログラムから直接呼べる**よう、
`VimEditorControl` に強く型付けされたメソッドを追加。これらは対応する既存の
イベントを発火するだけで、実際のペイン／タブのレイアウトは従来どおり**ホスト側**
（イベント購読側）が実装する。`theme` / `font` / `VimEnabled` の公開と同じパターン。

```csharp
editor.SplitVertical();              // 縦分割（:vsplit 相当）
editor.SplitHorizontal("notes.md");  // 横分割して notes.md を開く（:split 相当）
editor.NewTab();                     // 新規タブ（:tabnew 相当）
editor.NextTab();  editor.PrevTab(); // gt / gT 相当
editor.CloseTab(force: true);        // 現タブを閉じる（:tabclose! 相当）
editor.FocusWindow(WindowNavDir.Right); // 隣のペインへフォーカス（Ctrl+W l 相当）
editor.CloseWindow();                // 現在の分割ウィンドウを閉じる（Ctrl+W q / :close 相当）
```

| メソッド | 相当する Vim 操作 | 発火イベント |
|---|---|---|
| `SplitHorizontal(filePath?)` | `:split` | `SplitRequested (Vertical=false)` |
| `SplitVertical(filePath?)` | `:vsplit` | `SplitRequested (Vertical=true)` |
| `NewTab(filePath?)` | `:tabnew` | `NewTabRequested` |
| `NextTab()` / `PrevTab()` | `gt` / `gT` | `NextTabRequested` / `PrevTabRequested` |
| `CloseTab(force?)` | `:tabclose` | `CloseTabRequested` |
| `FocusWindow(WindowNavDir)` | `Ctrl+W h/j/k/l/w/W` | `WindowNavRequested` |
| `CloseWindow(force?)` | `Ctrl+W q` / `:close` | `WindowCloseRequested` |

- `Split*` / `CloseTab` / `CloseWindow` のハンドラは `sender`（＝呼び出したエディタ
  インスタンス）を対象に作用する。`NextTab` / `PrevTab` / `FocusWindow` は Vim の
  `gt` / `Ctrl+W` と同様、ホスト側のグローバル状態（選択タブ・フォーカス中ペイン）に作用する。
- ホストがこれらのイベントを購読していない（例: `VimEditorControl` を単体で使う）場合、
  各メソッドは安全に no-op となる（`?.Invoke`）。
- 公開型: `WindowNavDir`（`Editor.Core.Models`。`Next/Prev/Left/Right/Up/Down`）。
