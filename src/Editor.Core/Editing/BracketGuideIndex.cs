namespace Editor.Core.Editing;

/// <summary>
/// <see cref="BracketGuideScanner.Build"/> と同じ結果を、編集のたびに<b>変わった行から先だけ</b>の走査で作る。
/// <para>全体の走査は1打鍵ごとに走っていた（7500行の .cs で 0.6ms、16000行で 1.7ms）。行頭の状態
/// （閉じていない開き括弧の積み重ね・ブロックコメントの中か）を行ごとに持っておき、変わった最初の行から
/// 走査し直す。末尾の同じ行の範囲に入ってから、行頭の状態が前回と（行番号のずれを除いて）同じになったら、
/// そこから先は前回の結果を行番号だけずらして使う。</para>
/// <para>ガイドの上限（<see cref="BracketGuideScanner.MaxGuides"/>）に届くファイルでは、上限で切った結果を
/// 引き継ぐと切り方が全体の走査とずれうるので、引き継がずに毎回全体を走査する。</para>
/// </summary>
public sealed class BracketGuideIndex
{
    private BracketGuideSyntax? _syntax;
    private string[]? _lines;
    private BracketGuideScanner.OpenBracket?[] _startStacks = [];
    private bool[] _startInComment = [];
    private BracketGuide[][] _closedOn = [];
    private int[] _guidesBefore = [];
    private IReadOnlyList<BracketGuide> _guides = [];

    /// <summary>いまの行配列のガイド一覧。<paramref name="lines"/> は前回と別の配列でよい（中身を比べる）。</summary>
    public IReadOnlyList<BracketGuide> Update(string[] lines, BracketGuideSyntax? syntax = null)
    {
        syntax ??= BracketGuideSyntax.CFamily;
        if (lines.Length == 0 || BracketGuideScanner.ExceedsCharacterLimit(lines))
        {
            Reset();
            return [];
        }

        bool reuse = _lines != null && Equals(_syntax, syntax)
            && _guidesBefore[^1] < BracketGuideScanner.MaxGuides;
        var result = Scan(lines, syntax, reuse);
        if (result.Count > BracketGuideScanner.MaxGuides)
            result = Scan(lines, syntax, reuse: false); // 引き継いだ分で上限を超えた：上限は先頭から数えて切る
        return result;
    }

    public void Reset()
    {
        _syntax = null;
        _lines = null;
        _startStacks = [];
        _startInComment = [];
        _closedOn = [];
        _guidesBefore = [];
        _guides = [];
    }

    private IReadOnlyList<BracketGuide> Scan(string[] lines, BracketGuideSyntax syntax, bool reuse)
    {
        int n = lines.Length;
        var startStacks = new BracketGuideScanner.OpenBracket?[n + 1];
        var startInComment = new bool[n + 1];
        var closedOn = new BracketGuide[n][];
        var guidesBefore = new int[n + 1];

        int m = reuse ? _lines!.Length : 0;
        int prefix = 0, suffix = 0;
        if (reuse)
        {
            int max = Math.Min(n, m);
            while (prefix < max && SameLine(_lines![prefix], lines[prefix])) prefix++;
            while (suffix < max - prefix && SameLine(_lines![m - 1 - suffix], lines[n - 1 - suffix])) suffix++;
            Array.Copy(_startStacks, startStacks, prefix + 1);
            Array.Copy(_startInComment, startInComment, prefix + 1);
            Array.Copy(_closedOn, closedOn, prefix);
            Array.Copy(_guidesBefore, guidesBefore, prefix + 1);
        }

        var scanner = new BracketGuideScanner.LineScanner(lines, syntax);
        var stack = startStacks[prefix];
        bool inComment = startInComment[prefix];
        int oldOffset = m - n;      // 末尾の同じ範囲で、新しい行 i は前回の行 i + oldOffset
        int lineShift = n - m;      // 前回の末尾側の行番号を今回の番号へ直す量
        int oldSuffixStart = m - suffix;
        var buffer = new List<BracketGuide>();

        for (int i = prefix; i < n; i++)
        {
            if (reuse && i >= n - suffix
                && inComment == _startInComment[i + oldOffset]
                && SameStack(stack, _startStacks[i + oldOffset]))
            {
                AdoptOldTail(i);
                break;
            }
            buffer.Clear();
            scanner.ScanLine(i, ref stack, ref inComment, buffer, guidesBefore[i]);
            closedOn[i] = buffer.Count == 0 ? [] : buffer.ToArray();
            guidesBefore[i + 1] = guidesBefore[i] + closedOn[i].Length;
            startStacks[i + 1] = stack;
            startInComment[i + 1] = inComment;
        }

        var guides = new List<BracketGuide>(guidesBefore[n]);
        foreach (var lineGuides in closedOn)
            guides.AddRange(lineGuides);

        _syntax = syntax;
        _lines = (string[])lines.Clone();
        _startStacks = startStacks;
        _startInComment = startInComment;
        _closedOn = closedOn;
        _guidesBefore = guidesBefore;
        _guides = guides;
        return guides;

        // 前回の行番号を今回の番号へ。先頭の同じ範囲はそのまま、末尾の同じ範囲はずらす。間の行は対応しない。
        int MapOldLine(int oldLine) =>
            oldLine < prefix ? oldLine : oldLine >= oldSuffixStart ? oldLine + lineShift : -1;

        // 行番号のずれを除いて同じ積み重ねか。同じ節を共有していれば（先頭の範囲で開いたもの）そこから下も同じ。
        bool SameStack(BracketGuideScanner.OpenBracket? current, BracketGuideScanner.OpenBracket? old)
        {
            while (current is not null && old is not null)
            {
                if (ReferenceEquals(current, old)) return true;
                if (current.Close != old.Close || current.Column != old.Column || current.Line != MapOldLine(old.Line))
                    return false;
                current = current.Next;
                old = old.Next;
            }
            return current is null && old is null;
        }

        void AdoptOldTail(int from)
        {
            var remapped = new Dictionary<BracketGuideScanner.OpenBracket, BracketGuideScanner.OpenBracket>(ReferenceEqualityComparer.Instance);
            for (int k = from; k < n; k++)
            {
                int j = k + oldOffset;
                closedOn[k] = lineShift == 0 ? _closedOn[j] : RemapGuides(_closedOn[j]);
                guidesBefore[k + 1] = guidesBefore[k] + closedOn[k].Length;
                startStacks[k + 1] = lineShift == 0 ? _startStacks[j + 1] : RemapStack(_startStacks[j + 1], remapped);
                startInComment[k + 1] = _startInComment[j + 1];
            }
        }

        BracketGuide[] RemapGuides(BracketGuide[] old)
        {
            if (old.Length == 0) return old;
            var moved = new BracketGuide[old.Length];
            for (int g = 0; g < old.Length; g++)
            {
                var guide = old[g];
                moved[g] = guide with
                {
                    OpenLine = MapOldLine(guide.OpenLine),
                    CloseLine = MapOldLine(guide.CloseLine),
                    AnchorLine = MapOldLine(guide.AnchorLine),
                };
            }
            return moved;
        }

        BracketGuideScanner.OpenBracket? RemapStack(
            BracketGuideScanner.OpenBracket? node,
            Dictionary<BracketGuideScanner.OpenBracket, BracketGuideScanner.OpenBracket> remapped)
        {
            if (node is null || node.Line < prefix) return node; // 先頭の範囲で開いたものは番号が変わらない（下も同じ）
            if (remapped.TryGetValue(node, out var done)) return done;
            var moved = new BracketGuideScanner.OpenBracket(node.Close, MapOldLine(node.Line), node.Column, RemapStack(node.Next, remapped));
            remapped[node] = moved;
            return moved;
        }
    }

    private static bool SameLine(string a, string b) => ReferenceEquals(a, b) || string.Equals(a, b, StringComparison.Ordinal);
}
