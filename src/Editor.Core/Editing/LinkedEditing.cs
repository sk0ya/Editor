using System.Text.RegularExpressions;

namespace Editor.Core.Editing;

/// <summary>連動編集の1範囲。タグ名は改行を含まないので、範囲は常に1行に収まる（半開区間）。</summary>
public readonly record struct LinkedRange(int Line, int Start, int End)
{
    public int Length => End - Start;

    /// <summary>カーソルがこの範囲で編集しているとみなせるか。名前の直後（End）も含める —
    /// <c>&lt;div|&gt;</c> で打ち足すのは名前の編集だから。</summary>
    public bool Touches(int line, int column) => line == Line && column >= Start && column <= End;
}

/// <summary>
/// 同時に書き換わる範囲の組（LSP の <c>textDocument/linkedEditingRange</c> の応答、または
/// <see cref="MarkupTagPairFinder"/> の結果）。<see cref="WordPattern"/> は範囲の中身として
/// 許される文字列（LSP の <c>wordPattern</c>）。外れた編集で連動は解除される。
/// </summary>
public sealed record LinkedEditingRanges(IReadOnlyList<LinkedRange> Ranges, string? WordPattern = null)
{
    /// <summary>「問い合わせたが連動する範囲は無い」という答え。ホストの供給元がこれを返すと、
    /// エンジンは自前のフォールバックを試さない（null は「答えを持っていない」）。</summary>
    public static LinkedEditingRanges None { get; } = new([]);
}

/// <summary>
/// 連動編集の1回分（タグ名の編集が始まってから、範囲の外へ出るまで）。純粋ロジックで、
/// バッファには触らない。主範囲で起きた編集を「旧行 → 新行」の差分として受け取り、
/// ほかの範囲へ写す行の新しい本文を返す。
///
/// <para>キー単位の再生ではなく<b>結果の差分</b>から主範囲の新しい中身を求めるので、
/// 文字入力・Backspace・Delete・<c>ciw</c> の削除・IME の確定・貼り付けを区別しなくてよい。</para>
/// </summary>
public sealed class LinkedEditingSession
{
    private readonly List<LinkedRange> _ranges;
    private readonly Regex? _wordPattern;

    private LinkedEditingSession(List<LinkedRange> ranges, Regex? wordPattern)
    {
        _ranges = ranges;
        _wordPattern = wordPattern;
    }

    public IReadOnlyList<LinkedRange> Ranges => _ranges;

    /// <summary>2つ以上の範囲があり、すべて同じ中身のときだけセッションを始める
    /// （食い違った範囲を連動させると、片方の中身でもう片方を上書きしてしまう）。</summary>
    public static LinkedEditingSession? TryStart(LinkedEditingRanges ranges, Func<int, string> getLine)
    {
        if (ranges.Ranges.Count < 2) return null;
        string? text = null;
        foreach (var r in ranges.Ranges)
        {
            if (r.Start < 0 || r.End < r.Start) return null;
            var line = getLine(r.Line);
            if (r.End > line.Length) return null;
            var s = line.Substring(r.Start, r.Length);
            if (text is null) text = s;
            else if (!string.Equals(text, s, StringComparison.Ordinal)) return null;
        }
        var sorted = ranges.Ranges.OrderBy(r => r.Line).ThenBy(r => r.Start).ToList();
        for (int i = 1; i < sorted.Count; i++)
        {
            // 同じ行で重なる範囲は写し先が決まらない。
            if (sorted[i].Line == sorted[i - 1].Line && sorted[i].Start < sorted[i - 1].End) return null;
        }
        return new LinkedEditingSession(sorted, CompileWordPattern(ranges.WordPattern));
    }

    private static Regex? CompileWordPattern(string? pattern)
    {
        if (string.IsNullOrEmpty(pattern)) return null;
        try
        {
            return new Regex(@"\A(?:" + pattern + @")\z", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
        }
        catch (ArgumentException)
        {
            return null; // JS 方言の正規表現で .NET が読めないもの — 既定の検査に任せる
        }
    }

    public bool Touches(int line, int column) => _ranges.Any(r => r.Touches(line, column));

    /// <summary>
    /// 行 <paramref name="line"/> が <paramref name="oldText"/> から <paramref name="newText"/> へ
    /// 変わったことを受けて、ほかの範囲へ写す。<paramref name="caretHint"/> は編集前のカーソル列で、
    /// 同じ文字が並ぶときの差分の取り方の曖昧さ（<c>dd</c> に <c>d</c> を足す等）をカーソル側へ寄せる。
    /// 主範囲の外の編集・名前として許されない中身になった編集では null を返す（＝連動を解除する）。
    /// </summary>
    public LinkedEditingUpdate? Apply(int line, string oldText, string newText, int caretHint, Func<int, string> getLine)
    {
        if (string.Equals(oldText, newText, StringComparison.Ordinal)) return null;
        var (prefix, oldEnd, newEnd) = ChangedRegion(oldText, newText, caretHint);

        int primary = -1;
        for (int i = 0; i < _ranges.Count; i++)
        {
            var r = _ranges[i];
            if (r.Line == line && prefix >= r.Start && oldEnd <= r.End) { primary = i; break; }
        }
        if (primary < 0) return null;

        int delta = newEnd - oldEnd;
        var p = _ranges[primary];
        int newLength = p.Length + delta;
        if (newLength < 0 || p.Start + newLength > newText.Length) return null;
        var name = newText.Substring(p.Start, newLength);
        if (!IsAcceptable(name)) return null;

        var ranges = new List<LinkedRange>(_ranges);
        ShiftAfter(ranges, primary, p.End, delta);
        ranges[primary] = p with { End = p.End + delta };

        var lines = new Dictionary<int, string> { [line] = newText };
        int caretShift = 0;
        for (int i = 0; i < ranges.Count; i++)
        {
            if (i == primary) continue;
            var r = ranges[i];
            if (!lines.TryGetValue(r.Line, out var text)) text = getLine(r.Line);
            if (r.End > text.Length) return null;
            if (string.Equals(text.Substring(r.Start, r.Length), name, StringComparison.Ordinal)) continue;
            lines[r.Line] = string.Concat(text.AsSpan(0, r.Start), name, text.AsSpan(r.End));
            int d = name.Length - r.Length;
            ShiftAfter(ranges, i, r.End, d);
            ranges[i] = r with { End = r.End + d };
            if (r.Line == line && r.Start < ranges[primary].Start) caretShift += d;
        }

        _ranges.Clear();
        _ranges.AddRange(ranges);
        if (string.Equals(lines[line], newText, StringComparison.Ordinal)) lines.Remove(line);
        return new LinkedEditingUpdate(lines, caretShift);
    }

    private static void ShiftAfter(List<LinkedRange> ranges, int changed, int oldEnd, int delta)
    {
        if (delta == 0) return;
        int line = ranges[changed].Line;
        for (int j = 0; j < ranges.Count; j++)
        {
            if (j == changed || ranges[j].Line != line || ranges[j].Start < oldEnd) continue;
            ranges[j] = ranges[j] with { Start = ranges[j].Start + delta, End = ranges[j].End + delta };
        }
    }

    private bool IsAcceptable(string name)
    {
        if (name.Length == 0) return true; // 名前を消し切った途中（ciw 直後）は空で連動させる
        if (_wordPattern is not null)
        {
            try { return _wordPattern.IsMatch(name); }
            catch (RegexMatchTimeoutException) { return false; }
        }
        foreach (var c in name)
            if (!MarkupTagPairFinder.IsTagNameChar(c)) return false;
        return true;
    }

    /// <summary>旧行・新行の差分領域。戻り値は (共通の先頭長, 旧行での変更終端, 新行での変更終端)。</summary>
    internal static (int Prefix, int OldEnd, int NewEnd) ChangedRegion(string oldText, string newText, int caretHint)
    {
        int max = Math.Min(oldText.Length, newText.Length);
        int prefix = 0;
        while (prefix < max && oldText[prefix] == newText[prefix]) prefix++;
        int hint = Math.Clamp(caretHint, 0, oldText.Length);
        prefix = Math.Min(prefix, hint);

        int suffixCap = Math.Min(oldText.Length - hint, max - prefix);
        int suffix = 0;
        while (suffix < suffixCap &&
               oldText[oldText.Length - 1 - suffix] == newText[newText.Length - 1 - suffix])
            suffix++;
        return (prefix, oldText.Length - suffix, newText.Length - suffix);
    }
}

/// <summary>連動の結果としてバッファへ書く行（行番号 → 新しい本文）と、カーソル行で
/// カーソルより前の範囲が伸び縮みしたぶんのカーソル列の補正。</summary>
public sealed record LinkedEditingUpdate(IReadOnlyDictionary<int, string> Lines, int CaretShift);
