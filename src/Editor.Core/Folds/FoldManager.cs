namespace Editor.Core.Folds;

public record struct FoldRegion(int StartLine, int EndLine, bool IsClosed);

public class FoldManager
{
    private readonly List<FoldRegion> _folds = [];
    private int[]? _cachedVisMap;
    private int _cachedTotalLines = -1;

    public IReadOnlyList<FoldRegion> Folds => _folds;

    private void InvalidateCache() { _cachedVisMap = null; }

    public void CreateFold(int startLine, int endLine)
    {
        if (endLine <= startLine) return;
        if (_folds.Any(f => f.StartLine <= endLine && f.EndLine >= startLine)) return;
        _folds.Add(new FoldRegion(startLine, endLine, IsClosed: true));
        _folds.Sort((a, b) => a.StartLine.CompareTo(b.StartLine));
        InvalidateCache();
    }

    public void ToggleFold(int line)
    {
        var idx = FindFoldIndex(line);
        if (idx >= 0)
        {
            _folds[idx] = _folds[idx] with { IsClosed = !_folds[idx].IsClosed };
            InvalidateCache();
        }
    }

    public void OpenFold(int line)
    {
        var idx = FindFoldIndex(line);
        if (idx >= 0) { _folds[idx] = _folds[idx] with { IsClosed = false }; InvalidateCache(); }
    }

    public void CloseFold(int line)
    {
        var idx = FindFoldIndex(line);
        if (idx >= 0) { _folds[idx] = _folds[idx] with { IsClosed = true }; InvalidateCache(); }
    }

    public void OpenAll()
    {
        for (int i = 0; i < _folds.Count; i++)
            _folds[i] = _folds[i] with { IsClosed = false };
        InvalidateCache();
    }

    public void CloseAll()
    {
        for (int i = 0; i < _folds.Count; i++)
            _folds[i] = _folds[i] with { IsClosed = true };
        InvalidateCache();
    }

    public void Clear() { _folds.Clear(); InvalidateCache(); }

    // LSPのfoldingRangeレスポンスからフォールドを設定する。
    // 既存の開閉状態を同じ範囲のフォールドに引き継ぐ。
    public void SetLspRanges(IEnumerable<(int StartLine, int EndLine)> ranges)
    {
        var existing = _folds.ToDictionary(f => (f.StartLine, f.EndLine), f => f.IsClosed);
        _folds.Clear();
        foreach (var (start, end) in ranges)
        {
            if (end <= start) continue;
            bool isClosed = existing.TryGetValue((start, end), out bool c) && c;
            _folds.Add(new FoldRegion(start, end, IsClosed: isClosed));
        }
        _folds.Sort((a, b) => a.StartLine.CompareTo(b.StartLine));
        InvalidateCache();
    }

    // 指定バッファ行が閉じたフォールドに隠れているか（StartLine 自体は隠れない）
    public FoldRegion? GetHidingFold(int bufferLine)
    {
        foreach (var f in _folds)
            if (f.IsClosed && f.StartLine < bufferLine && f.EndLine >= bufferLine)
                return f;
        return null;
    }

    // 指定行を含む最も内側のフォールドを返す
    public FoldRegion? FindFoldAt(int line)
    {
        FoldRegion? best = null;
        foreach (var f in _folds)
            if (f.StartLine <= line && f.EndLine >= line && (best == null || f.StartLine > best.Value.StartLine))
                best = f;
        return best;
    }

    // 可視行マップ: map[visualIndex] = bufferLine
    public int[] BuildVisibleLineMap(int totalLines)
    {
        if (_cachedVisMap != null && _cachedTotalLines == totalLines)
            return _cachedVisMap;

        // Pre-index closed folds by StartLine for O(1) lookup per visible line
        Dictionary<int, int>? closedByStart = null;
        foreach (var f in _folds)
        {
            if (!f.IsClosed) continue;
            closedByStart ??= new Dictionary<int, int>(_folds.Count);
            closedByStart[f.StartLine] = f.EndLine;
        }

        var map = new List<int>(totalLines);
        int bi = 0;
        while (bi < totalLines)
        {
            map.Add(bi);
            // 閉じたフォールドの先頭行なら EndLine+1 へジャンプ
            bi = closedByStart != null && closedByStart.TryGetValue(bi, out int endLine)
                ? endLine + 1
                : bi + 1;
        }

        _cachedVisMap = map.ToArray();
        _cachedTotalLines = totalLines;
        return _cachedVisMap;
    }

    // バッファ行 → ビジュアル行インデックス（隠れている場合は -1）
    public int BufferToVisualLine(int bufferLine, int[] visibleMap)
    {
        for (int i = 0; i < visibleMap.Length; i++)
            if (visibleMap[i] == bufferLine) return i;
        return -1;
    }

    public int VisibleLineCount(int totalLines) => BuildVisibleLineMap(totalLines).Length; // uses cache

    // 行削除時のフォールド調整
    public void OnLinesDeleted(int startLine, int endLine)
    {
        int count = endLine - startLine + 1;
        bool changed = false;
        for (int i = _folds.Count - 1; i >= 0; i--)
        {
            var f = _folds[i];
            if (f.StartLine >= startLine && f.EndLine <= endLine) { _folds.RemoveAt(i); changed = true; continue; }
            if (f.StartLine > endLine) { _folds[i] = f with { StartLine = f.StartLine - count, EndLine = f.EndLine - count }; changed = true; continue; }
            // 部分的な重なり → 削除
            if (f.EndLine >= startLine && f.StartLine <= endLine) { _folds.RemoveAt(i); changed = true; continue; }
        }
        if (changed) InvalidateCache();
    }

    // 行挿入時のフォールド調整
    public void OnLinesInserted(int afterLine, int count)
    {
        bool changed = false;
        for (int i = 0; i < _folds.Count; i++)
        {
            var f = _folds[i];
            if (f.StartLine > afterLine)
            {
                _folds[i] = f with { StartLine = f.StartLine + count, EndLine = f.EndLine + count };
                changed = true;
            }
        }
        if (changed) InvalidateCache();
    }

    // ネスト構造で最も内側のフォールドを返す（StartLine が最大かつ line を含むもの）
    private int FindFoldIndex(int line)
    {
        int bestIdx = -1;
        int bestStart = -1;
        for (int i = 0; i < _folds.Count; i++)
        {
            var f = _folds[i];
            if (f.StartLine <= line && f.EndLine >= line && f.StartLine > bestStart)
            {
                bestIdx = i;
                bestStart = f.StartLine;
            }
        }
        return bestIdx;
    }

    // 指定行の最も内側のフォールドを削除する
    public void DeleteFold(int line)
    {
        var idx = FindFoldIndex(line);
        if (idx >= 0) { _folds.RemoveAt(idx); InvalidateCache(); }
    }

    // 指定行を含む全てのフォールドを削除する
    public void DeleteFoldsAt(int line)
    {
        bool changed = false;
        for (int i = _folds.Count - 1; i >= 0; i--)
        {
            var f = _folds[i];
            if (f.StartLine <= line && f.EndLine >= line) { _folds.RemoveAt(i); changed = true; }
        }
        if (changed) InvalidateCache();
    }

    // 指定行より下にある最初のフォールドの StartLine を返す（なければ -1）
    // _folds は StartLine 昇順ソート済みなので、最初に line を超えた要素が答え
    public int NextFoldStart(int line)
    {
        foreach (var f in _folds)
            if (f.StartLine > line) return f.StartLine;
        return -1;
    }

    // 指定行より上にある最後のフォールドの StartLine を返す（なければ -1）
    // _folds は StartLine 昇順ソート済みなので、逆順走査で最初に line 未満の要素が答え
    public int PrevFoldStart(int line)
    {
        for (int i = _folds.Count - 1; i >= 0; i--)
            if (_folds[i].StartLine < line) return _folds[i].StartLine;
        return -1;
    }

    // 指定行を含む最も内側のフォールドの StartLine を返す（なければ -1）
    public int CurrentFoldStart(int line) => FindFoldAt(line)?.StartLine ?? -1;

    // 指定行を含む最も内側のフォールドの EndLine を返す（なければ -1）
    public int CurrentFoldEnd(int line) => FindFoldAt(line)?.EndLine ?? -1;
}
