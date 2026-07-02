using System;

namespace Editor.Controls.Git;

/// <summary>
/// blame 左カラム（:Gblame 表示中）の行クリックをホストへ通知するイベント引数。
/// ホストはこれを受けて該当コミットの差分表示などを行う。
/// </summary>
public sealed class BlameCommitClickedEventArgs : EventArgs
{
    /// <summary>クリックされたバッファ行（0 始まり）。</summary>
    public int Line { get; }

    /// <summary>クリックされた行のコミット情報（短縮ハッシュ・著者・日付・メッセージ要約）。</summary>
    public EditorBlameLine Blame { get; }

    /// <summary>短縮コミットハッシュ（<see cref="Blame"/> の <see cref="EditorBlameLine.CommitHash"/>）。</summary>
    public string CommitHash => Blame.CommitHash;

    /// <summary>カラムに表示されていた注釈テキスト（<see cref="EditorBlameLine.Display"/>）。</summary>
    public string Annotation => Blame.Display;

    public BlameCommitClickedEventArgs(int line, EditorBlameLine blame)
    {
        Line = line;
        Blame = blame;
    }
}
