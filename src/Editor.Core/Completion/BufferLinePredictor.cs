namespace Editor.Core.Completion;

/// <summary>
/// 同じバッファの既出行から「この行の続き」を予想する。外部依存も待ち時間もない。
///
/// <para>効くのは<b>同じ形の行を続けて書いている</b>ときで、コードを書く時間の多くがそれに当たる
/// ——フィールドの列挙、<c>switch</c> の <c>case</c>、テストの <c>Assert</c> 行、設定ファイルの並び。
/// 逆に「初めて書く行」には何も出さない。出せないときに黙っていることが、この手の機能では
/// 当たっていることと同じくらい大事で、外れた提案が薄く出続けると読む方の負担になる。</para>
///
/// <para>探索はキャレットから<b>近い順に外へ</b>広げ、最初に当たった行で打ち切る。近い行ほど
/// 今の意図に近いという理由のほかに、打鍵のたびに走っても全行を舐めないための形でもある
/// （割り当ても、採用した 1 件を切り出すときの 1 回だけ）。</para>
/// </summary>
public static class BufferLinePredictor
{
    /// <summary>これより短い手掛かりでは予想しない。1〜2 文字ではどの行にも当たってしまう。</summary>
    public const int MinPrefixLength = 3;

    /// <summary>探索する上下の行数。これより遠い行は、近い行が無いときでも採らない。</summary>
    public const int SearchRadius = 400;

    /// <summary>
    /// キャレットの先に出す提案を返す。出せないときは null。
    ///
    /// <para>キャレットが行末にあるときだけ提案する。行の途中で出すと、
    /// 「挿入した結果どうなるか」が見た目から読めなくなる。</para>
    /// </summary>
    public static InlineSuggestion? Predict(IReadOnlyList<string> lines, int line, int column)
    {
        if (line < 0 || line >= lines.Count) return null;

        var current = lines[line];
        if (column < current.Length) return null;          // 行末でなければ出さない
        column = Math.Min(column, current.Length);

        int keyStart = IndentWidth(current, column);
        int keyLength = column - keyStart;
        if (keyLength < MinPrefixLength) return null;

        var key = current.AsSpan(keyStart, keyLength);
        if (key.IsWhiteSpace()) return null;

        for (int distance = 1; distance <= SearchRadius; distance++)
        {
            // 上を先に見る。書き終えた行の方が、これから書く行より意図に近い。
            int above = line - distance;
            if (above >= 0 && TryMatch(lines[above], key, out var fromAbove))
                return new InlineSuggestion(fromAbove, InlineSuggestionSource.BufferLine);

            int below = line + distance;
            if (below < lines.Count && TryMatch(lines[below], key, out var fromBelow))
                return new InlineSuggestion(fromBelow, InlineSuggestionSource.BufferLine);

            if (above < 0 && below >= lines.Count) break;
        }

        return null;
    }

    /// <summary>候補行がインデントを除いて <paramref name="key"/> で始まるなら、その残りを返す。</summary>
    private static bool TryMatch(string candidate, ReadOnlySpan<char> key, out string rest)
    {
        rest = "";
        int start = IndentWidth(candidate, candidate.Length);
        if (candidate.Length - start <= key.Length) return false;
        if (!candidate.AsSpan(start).StartsWith(key, StringComparison.Ordinal)) return false;

        rest = candidate[(start + key.Length)..];
        return rest.Length > 0;
    }

    /// <summary>先頭の空白（スペース／タブ）の幅。<paramref name="limit"/> を超えては数えない。</summary>
    private static int IndentWidth(string text, int limit)
    {
        int i = 0;
        while (i < limit && (text[i] == ' ' || text[i] == '\t')) i++;
        return i;
    }
}
