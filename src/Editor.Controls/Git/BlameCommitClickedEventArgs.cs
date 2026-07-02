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

    /// <summary>注釈から解析した短縮コミットハッシュ。解析できなければ null。</summary>
    public string? CommitHash { get; }

    /// <summary>クリックされた行の注釈テキスト全体（例: "abc1234 (author, 2026-05-31)"）。</summary>
    public string Annotation { get; }

    public BlameCommitClickedEventArgs(int line, string annotation)
    {
        Line = line;
        Annotation = annotation ?? "";
        CommitHash = TryParseCommitHash(Annotation);
    }

    /// <summary>
    /// 注釈テキストの先頭トークンをコミットハッシュとして取り出す（16進 4〜40 桁のときだけ）。
    /// 既定の <see cref="IEditorGitService.GetBlameAnnotations"/> 実装（GitDiffProvider）の
    /// "hash (author, date)" 形式を想定。形式が異なるカスタム実装では null になりうる。
    /// </summary>
    public static string? TryParseCommitHash(string annotation)
    {
        var trimmed = annotation.AsSpan().Trim();
        int end = trimmed.IndexOf(' ');
        var token = end < 0 ? trimmed : trimmed[..end];
        if (token.Length is < 4 or > 40) return null;
        foreach (var ch in token)
            if (!char.IsAsciiHexDigitLower(ch)) return null;  // git のハッシュは小文字16進
        return token.ToString();
    }
}
