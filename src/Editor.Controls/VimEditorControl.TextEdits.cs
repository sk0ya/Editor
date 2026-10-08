using System;
using Editor.Core.Buffer;
using Editor.Core.Engine;

namespace Editor.Controls;

/// <summary><see cref="VimEditorControl.TextEdited"/> の引数。どのバッファの、どの範囲が、何に変わったか。</summary>
public sealed class EditorTextEditedEventArgs(TextBufferChange change, VimBuffer buffer, bool isCurrentBuffer) : EventArgs
{
    /// <summary>変更そのもの（変更前の座標の範囲＋新しい文字列。LSP の contentChanges と同じ形）。</summary>
    public TextBufferChange Change { get; } = change;

    /// <summary>変更されたバッファのファイルパス（仮想文書なら null）。</summary>
    public string? FilePath { get; } = buffer.FilePath;

    /// <summary>仮想文書のホスト側 ID（ファイル由来なら null）。</summary>
    public string? DocumentId { get; } = buffer.DocumentId;

    /// <summary>いま表示しているバッファの変更か。<c>:bufdo</c> や別バッファへの workspace edit では false。</summary>
    public bool IsCurrentBuffer { get; } = isCurrentBuffer;
}

/// <summary>
/// 本文の変更を差分として外へ出す口。<see cref="BufferChanged"/> は「何か変わった」しか言わないので、
/// 同じ文書を別の場所に映すホスト（複製ビュー・抜粋・アンカー）は本文全体を比べ直すしかなかった——
/// キー入力のたびに文書まるごとの比較と複製になる。<see cref="TextEdited"/> は変わった範囲だけを渡す。
/// <para>購読者がいる間だけバッファに購読をつなぐ（<see cref="BufferManager.BufferTextChanged"/>）ので、
/// 使わないホストのキー入力のコストは変わらない。発火は変更を行ったスレッド（UI スレッド）で同期的に、
/// 複数手の操作の途中でも起きる。ハンドラーからこのエディタの本文を書き換えてはいけない。</para>
/// </summary>
public partial class VimEditorControl
{
    private EventHandler<EditorTextEditedEventArgs>? _textEdited;

    /// <summary>本文が変更されるたびに、その変更を差分として通知する（読み込み＝<see cref="TextBufferChangeKind.Reload"/> も含む）。
    /// 変更を順に本文の写しへ適用すれば、このエディタの本文と一致する。</summary>
    public event EventHandler<EditorTextEditedEventArgs>? TextEdited
    {
        add
        {
            var wasEmpty = _textEdited is null;
            _textEdited += value;
            if (wasEmpty && _textEdited is not null)
                _engine.BufferManager.BufferTextChanged += OnBufferTextChanged;
        }
        remove
        {
            _textEdited -= value;
            if (_textEdited is null)
                _engine.BufferManager.BufferTextChanged -= OnBufferTextChanged;
        }
    }

    /// <summary>
    /// 文書の一部を守る判定（<see cref="VimEngine.EditGuard"/>）。抜粋ビューの見出し行のように、
    /// ホストが組み立てた文書のうち書き換えさせたくない行を守る。断った編集は巻き戻され、
    /// <see cref="TextEdited"/> にも流れない。
    /// </summary>
    public Editor.Core.Editing.HostEditGuard? EditGuard
    {
        get => _engine.EditGuard;
        set => _engine.EditGuard = value;
    }

    /// <summary>行番号列の表示をホストが決める（<see cref="Rendering.EditorCanvas.SetLineNumberLabels"/>）。
    /// null を渡すと既定の行番号へ戻る。</summary>
    public void SetLineNumberLabels(Func<int, string?>? label, int width)
        => Canvas.SetLineNumberLabels(label, width);

    // ハンドラーごとに切り離す: 1つが投げても、ほかの購読者の写しに変更が届かなくなってはいけないし、
    // 例外が編集済みのキー処理へ巻き戻ってもいけない。
    private void OnBufferTextChanged(VimBuffer buffer, TextBufferChange change)
    {
        if (_textEdited is not { } handlers) return;
        var args = new EditorTextEditedEventArgs(change, buffer, ReferenceEquals(buffer, _engine.CurrentBuffer));
        foreach (var handler in handlers.GetInvocationList())
        {
            try { ((EventHandler<EditorTextEditedEventArgs>)handler)(this, args); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"VimEditorControl.TextEdited handler failed: {ex}"); }
        }
    }
}
