namespace Editor.Core.Folds;

/// <summary><paramref name="IsManual"/> は「人が畳んだ」印（<c>zf</c>、ホストが読込直後に閉じる
/// using 節など）。サーバーの foldingRange が届いても捨てない目印として使う。</summary>
public record struct FoldRegion(int StartLine, int EndLine, bool IsClosed, bool IsManual = false);

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
        _folds.Add(new FoldRegion(startLine, endLine, IsClosed: true, IsManual: true));
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

    public void Clear() { _folds.Clear(); _pendingClosed.Clear(); InvalidateCache(); }

    /// <summary>復元で「閉じておく」と頼まれたが、まだ範囲が届いていない（言語サーバーの foldingRange 待ち）もの。
    /// 最初に届いた <see cref="SetLspRanges"/> で、同じ範囲を閉じて消える。</summary>
    private readonly HashSet<(int StartLine, int EndLine)> _pendingClosed = [];

    /// <summary>
    /// 前に閉じていた範囲を戻す（ホストのタブの復元）。手で畳んだもの（<see cref="FoldRegion.IsManual"/>）はその場で作って閉じる。
    /// 言語サーバーの範囲は届くまで覚えておき、届いた最初の 1 回で同じ範囲を閉じる（届く前に作ると、サーバーの範囲と
    /// 重なって二重になる）。<paramref name="lineCount"/> を超える範囲は捨てる（ファイルが外で縮んだ）。
    /// </summary>
    public void RestoreClosed(IEnumerable<FoldRegion> folds, int lineCount)
    {
        foreach (var fold in folds)
        {
            if (fold.StartLine < 0 || fold.EndLine <= fold.StartLine || fold.EndLine >= lineCount) continue;
            if (fold.IsManual)
            {
                CreateFold(fold.StartLine, fold.EndLine);
                continue;
            }
            var idx = _folds.FindIndex(f => f.StartLine == fold.StartLine && f.EndLine == fold.EndLine);
            if (idx >= 0)
                _folds[idx] = _folds[idx] with { IsClosed = true };
            else
                _pendingClosed.Add((fold.StartLine, fold.EndLine));
        }
        InvalidateCache();
    }

    // LSPのfoldingRangeレスポンスからフォールドを設定する。
    // 既存の開閉状態を同じ範囲のフォールドに引き継ぐ。
    public void SetLspRanges(IEnumerable<(int StartLine, int EndLine)> ranges)
    {
        var existing = _folds.ToDictionary(f => (f.StartLine, f.EndLine), f => f.IsClosed);
        // 手で畳んだものは残す。サーバーが同じ範囲を報せるとは限らない——実測で、Roslyn は
        // using 節を範囲として返さないことがあり、捨てていた頃は「開いた直後に閉じた using が
        // 0.5秒後に勝手に開いて本文が動く」になっていた。畳んだのは人の意図なので上書きしない。
        var manual = _folds.Where(f => f.IsManual).ToArray();
        _folds.Clear();
        _folds.AddRange(manual);
        foreach (var (start, end) in ranges)
        {
            if (end <= start) continue;
            if (manual.Any(m => m.StartLine == start && m.EndLine == end)) continue;
            bool isClosed = (existing.TryGetValue((start, end), out bool c) && c) || _pendingClosed.Contains((start, end));
            _folds.Add(new FoldRegion(start, end, IsClosed: isClosed));
        }
        // 復元の保留は最初の 1 回だけ効かせる（以後、人が開いたものを次の応答で閉じ直さない）。
        _pendingClosed.Clear();
        _folds.Sort((a, b) => a.StartLine.CompareTo(b.StartLine));
        InvalidateCache();
    }

    /// <summary>その行を隠している閉じた折りたたみを全部開く（入れ子なら外側も）。開いたものがあれば true。
    /// 飛んだ先を見せるため（ホストからの移動・ジャンプ・<c>:行番号</c>）。見出しの行は隠れていないので開かない。</summary>
    public bool OpenFoldsHiding(int bufferLine)
    {
        var opened = false;
        for (int i = 0; i < _folds.Count; i++)
        {
            var f = _folds[i];
            if (f.IsClosed && f.StartLine < bufferLine && f.EndLine >= bufferLine)
            {
                _folds[i] = f with { IsClosed = false };
                opened = true;
            }
        }
        if (opened) InvalidateCache();
        return opened;
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
