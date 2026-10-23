using Editor.Core.Editing;

namespace Editor.Core.Tests;

/// <summary>変更前（1.0.105）の <c>BracketGuideScanner.Build</c> をそのまま写したもの。走査を1行ずつの経路へ
/// 組み替えた後も、全体の走査・差分の走査の両方がこれと寸分違わず一致することを確かめる物差し。</summary>
internal static class ReferenceBracketGuideScanner
{
    public static IReadOnlyList<BracketGuide> Build(string[] lines, BracketGuideSyntax? syntax = null)
    {
        if (lines.Length == 0) return [];

        int total = 0;
        foreach (var line in lines)
        {
            total += line.Length;
            if (total > BracketGuideScanner.MaxCharacters) return [];
        }

        syntax ??= BracketGuideSyntax.CFamily;
        var guides = new List<BracketGuide>();
        var stack = new List<(char Close, int Line, int Column)>();
        bool inBlockComment = false;

        // 1 文字ごとに文字列比較を走らせると 400KB のファイルで 17ms かかった（打鍵ごとに払える額
        // ではない）。先頭 1 文字が一致したときだけ比較する。
        char lineCommentHead = syntax.LineComment is { Length: > 0 } lc ? lc[0] : '\0';
        char blockCommentHead = syntax.BlockCommentStart is { Length: > 0 } bc && syntax.BlockCommentEnd is { Length: > 0 }
            ? bc[0] : '\0';

        for (int l = 0; l < lines.Length; l++)
        {
            string line = lines[l];
            int i = 0;
            while (i < line.Length)
            {
                if (inBlockComment)
                {
                    int end = syntax.BlockCommentEnd is { Length: > 0 } be
                        ? line.IndexOf(be, i, StringComparison.Ordinal)
                        : -1;
                    if (end < 0) break;                       // 行末までコメント
                    i = end + syntax.BlockCommentEnd!.Length;
                    inBlockComment = false;
                    continue;
                }

                char c = line[i];

                if (c == lineCommentHead && Matches(line, i, syntax.LineComment)) break;   // 行末までコメント

                if (c == blockCommentHead && Matches(line, i, syntax.BlockCommentStart))
                {
                    i += syntax.BlockCommentStart!.Length;
                    inBlockComment = true;
                    continue;
                }

                if ((c == '"' && syntax.DoubleQuote) || (c == '\'' && syntax.SingleQuote))
                {
                    i = SkipQuoted(line, i, c);
                    continue;
                }

                char close = c switch { '{' => '}', '(' => ')', '[' => ']', _ => '\0' };
                if (close != '\0')
                {
                    if (stack.Count < BracketGuideScanner.MaxDepth) stack.Add((close, l, i));
                    i++;
                    continue;
                }

                if (c is '}' or ')' or ']')
                {
                    int match = -1;
                    for (int s = stack.Count - 1; s >= 0; s--)
                        if (stack[s].Close == c) { match = s; break; }

                    if (match >= 0)
                    {
                        var open = stack[match];
                        // 対応しない内側の括弧（書きかけの行など）は捨てる
                        stack.RemoveRange(match, stack.Count - match);
                        if (l > open.Line && guides.Count < BracketGuideScanner.MaxGuides)
                        {
                            var (anchorLine, anchorColumn) = Anchor(lines, open.Line, open.Column, l, i);
                            guides.Add(new BracketGuide(open.Line, open.Column, l, i, anchorLine, anchorColumn, match));
                        }
                    }
                    i++;
                    continue;
                }

                i++;
            }
        }

        return guides;
    }

    private static (int Line, int Column) Anchor(string[] lines, int openLine, int openColumn, int closeLine, int closeColumn)
    {
        if (IsFirstNonWhitespace(lines[closeLine], closeColumn)) return (closeLine, closeColumn);
        return (openLine, openColumn);
    }

    private static bool IsFirstNonWhitespace(string line, int column)
    {
        for (int i = 0; i < column && i < line.Length; i++)
            if (!char.IsWhiteSpace(line[i])) return false;
        return true;
    }

    private static bool Matches(string line, int index, string? token)
    {
        if (token is not { Length: > 0 }) return false;
        if (index + token.Length > line.Length) return false;
        return string.CompareOrdinal(line, index, token, 0, token.Length) == 0;
    }

    /// <summary>開き引用符の位置から、閉じ引用符の次（無ければ行末）まで飛ばす。</summary>
    private static int SkipQuoted(string line, int start, char quote)
    {
        for (int i = start + 1; i < line.Length; i++)
        {
            if (line[i] == '\\') { i++; continue; }
            if (line[i] == quote) return i + 1;
        }
        return line.Length;
    }
}
