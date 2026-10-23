namespace Editor.Core.Editing;

/// <summary>
/// 括弧を数えるときに「これはコードではない」と判断するための、言語ごとの目印。
/// 文字列やコメントの中の <c>{</c> を数えてしまうと、そこから下のガイドが丸ごとずれるため。
/// </summary>
/// <param name="LineComment">行コメントの開始（<c>//</c>, <c>#</c>, <c>--</c> …）。null でコメントなし。</param>
/// <param name="BlockCommentStart">ブロックコメントの開始（<c>/*</c>, <c>&lt;!--</c> …）。</param>
/// <param name="BlockCommentEnd">ブロックコメントの終了（<c>*/</c>, <c>--&gt;</c> …）。</param>
/// <param name="DoubleQuote"><c>"</c> で囲まれた文字列を飛ばすか。</param>
/// <param name="SingleQuote"><c>'</c> で囲まれた文字列／文字リテラルを飛ばすか。</param>
public sealed record BracketGuideSyntax(
    string? LineComment = "//",
    string? BlockCommentStart = "/*",
    string? BlockCommentEnd = "*/",
    bool DoubleQuote = true,
    bool SingleQuote = true)
{
    /// <summary>C / C# / Java / JS / TS / Rust / Go 系。</summary>
    public static readonly BracketGuideSyntax CFamily = new();

    /// <summary>コメントも文字列も飛ばさない（言語が分からないとき）。</summary>
    public static readonly BracketGuideSyntax Plain = new(null, null, null, false, false);
}

/// <summary>
/// 開き括弧の行から閉じ括弧の行までを結ぶ縦線 1 本。
/// <paramref name="AnchorLine"/>/<paramref name="AnchorColumn"/> は線を引く桁を測る位置
/// （タブや全角文字があるので、桁番号ではなく「どの行の何文字目か」で持つ）。
/// </summary>
public readonly record struct BracketGuide(
    int OpenLine,
    int OpenColumn,
    int CloseLine,
    int CloseColumn,
    int AnchorLine,
    int AnchorColumn,
    int Depth);

/// <summary>
/// バッファ全体を 1 度走査して、複数行にまたがる括弧ペアの一覧を作る純粋ロジック。
/// 走査結果は行配列が変わるまで使い回せる（呼び出し側でキャッシュする前提）。
/// </summary>
public static class BracketGuideScanner
{
    /// <summary>
    /// これを超える文字数のファイルではガイドを作らない。走査は編集のたびに 1 回（行配列が
    /// 変わったときだけ）走るので、打鍵の裏で払える額に収める必要がある — 実測で 390KB /
    /// 15,900 行が Release 1.7ms、よくある 1,000 行のファイルなら 0.1ms 程度。
    /// </summary>
    public const int MaxCharacters = 400_000;

    /// <summary>入れ子の上限。これより深い括弧はガイドを作らない。</summary>
    public const int MaxDepth = 64;

    /// <summary>作るガイドの上限。</summary>
    public const int MaxGuides = 20_000;

    public static IReadOnlyList<BracketGuide> Build(string[] lines, BracketGuideSyntax? syntax = null)
    {
        if (lines.Length == 0 || ExceedsCharacterLimit(lines)) return [];

        var scanner = new LineScanner(lines, syntax ?? BracketGuideSyntax.CFamily);
        var guides = new List<BracketGuide>();
        OpenBracket? stack = null;
        bool inBlockComment = false;
        for (int l = 0; l < lines.Length; l++)
            scanner.ScanLine(l, ref stack, ref inBlockComment, guides, guides.Count);
        return guides;
    }

    internal static bool ExceedsCharacterLimit(string[] lines)
    {
        int total = 0;
        foreach (var line in lines)
        {
            total += line.Length;
            if (total > MaxCharacters) return true;
        }
        return false;
    }

    /// <summary>
    /// まだ閉じていない開き括弧の積み重ね。各行の行頭の状態として持つので、変更できない連結リストにして
    /// 行どうしで共有する（行ごとに複製すると行数×深さになる）。<see cref="Depth"/> は底からの位置。
    /// </summary>
    internal sealed class OpenBracket(char close, int line, int column, OpenBracket? next)
    {
        public char Close { get; } = close;
        public int Line { get; } = line;
        public int Column { get; } = column;
        public OpenBracket? Next { get; } = next;
        public int Depth { get; } = next is null ? 0 : next.Depth + 1;
    }

    /// <summary>1行を走査する。全体の走査（<see cref="Build"/>）と差分の走査（<see cref="BracketGuideIndex"/>）が
    /// 同じこれを通すので、両者の結果は構造上一致する。</summary>
    internal readonly struct LineScanner
    {
        private readonly string[] _lines;
        private readonly BracketGuideSyntax _syntax;
        // 1 文字ごとに文字列比較を走らせると 400KB のファイルで 17ms かかった（打鍵ごとに払える額
        // ではない）。先頭 1 文字が一致したときだけ比較する。
        private readonly char _lineCommentHead;
        private readonly char _blockCommentHead;

        public LineScanner(string[] lines, BracketGuideSyntax syntax)
        {
            _lines = lines;
            _syntax = syntax;
            _lineCommentHead = syntax.LineComment is { Length: > 0 } lc ? lc[0] : '\0';
            _blockCommentHead = syntax.BlockCommentStart is { Length: > 0 } bc && syntax.BlockCommentEnd is { Length: > 0 }
                ? bc[0] : '\0';
        }

        /// <param name="guidesBefore">この行より前までに作ったガイドの数（<see cref="MaxGuides"/> の判定用）。</param>
        public void ScanLine(int l, ref OpenBracket? stack, ref bool inBlockComment, List<BracketGuide> sink, int guidesBefore)
        {
            var syntax = _syntax;
            string line = _lines[l];
            int made = 0;
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

                if (c == _lineCommentHead && Matches(line, i, syntax.LineComment)) break;   // 行末までコメント

                if (c == _blockCommentHead && Matches(line, i, syntax.BlockCommentStart))
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
                    if ((stack?.Depth + 1 ?? 0) < MaxDepth) stack = new OpenBracket(close, l, i, stack);
                    i++;
                    continue;
                }

                if (c is '}' or ')' or ']')
                {
                    var open = stack;
                    while (open is not null && open.Close != c) open = open.Next;

                    if (open is not null)
                    {
                        // 対応しない内側の括弧（書きかけの行など）は捨てる
                        stack = open.Next;
                        if (l > open.Line && guidesBefore + made < MaxGuides)
                        {
                            var (anchorLine, anchorColumn) = Anchor(_lines, open.Line, open.Column, l, i);
                            sink.Add(new BracketGuide(open.Line, open.Column, l, i, anchorLine, anchorColumn, open.Depth));
                            made++;
                        }
                    }
                    i++;
                    continue;
                }

                i++;
            }
        }
    }

    /// <summary>
    /// カーソルを内側に含むいちばん内側のガイドの添字。含むものが無ければ -1。
    /// 括弧そのものの上に立っているときも「含む」とみなす（そこが範囲の端なので）。
    /// </summary>
    public static int ActiveIndex(IReadOnlyList<BracketGuide> guides, int cursorLine, int cursorColumn)
    {
        int best = -1;
        int bestSpan = int.MaxValue;
        int bestDepth = -1;

        for (int i = 0; i < guides.Count; i++)
        {
            var g = guides[i];
            if (cursorLine < g.OpenLine || cursorLine > g.CloseLine) continue;
            if (cursorLine == g.OpenLine && cursorColumn < g.OpenColumn) continue;
            if (cursorLine == g.CloseLine && cursorColumn > g.CloseColumn) continue;

            int span = g.CloseLine - g.OpenLine;
            if (g.Depth > bestDepth || (g.Depth == bestDepth && span < bestSpan))
            {
                best = i;
                bestDepth = g.Depth;
                bestSpan = span;
            }
        }

        return best;
    }

    /// <summary>
    /// 線を引く桁。閉じ括弧が行頭（前が空白だけ）なら閉じ括弧の桁 — そこに縦に揃うのが
    /// <c>{</c> と <c>}</c> を「繋いで」見せる形。そうでなければ開き括弧の桁。
    /// </summary>
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
