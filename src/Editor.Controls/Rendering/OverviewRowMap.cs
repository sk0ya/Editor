using System;

namespace Editor.Controls.Rendering;

/// <summary>
/// スクロールバーの印（差分・診断）で使う「バッファ行 → 表示行」の対応。
///
/// <para>サムは<b>表示行</b>（折り返し・CodeLens の注釈行・差分の空き行を含み、畳んだ行を含まない）で
/// 動いている。印をバッファ行で測ると、空き行・折り返し・畳み込みのぶんだけサムとずれ、同じスクロールバーの
/// 差分の印と診断の印も別々の高さに出る。だからどちらの印もここを通して表示行へ写す。</para>
///
/// <para>畳んで隠れた行は、その直前の見えている行（＝畳みの見出し行）へ写す。畳んだ中の変更や問題を
/// 消さずに、見出しの位置に出すため。</para>
/// </summary>
internal sealed class OverviewRowMap
{
    private EditorCanvas.VisualLineSegment[]? _layout;
    private int _lineCount = -1;
    private int[] _rowOfLine = [];

    /// <summary>表示行の総数（少なくとも 1）。</summary>
    public int RowCount { get; private set; } = 1;

    /// <summary>行レイアウトか行数が変わっていれば作り直す（同じなら何もしない）。</summary>
    public OverviewRowMap Update(EditorCanvas.VisualLineSegment[] layout, int lineCount)
    {
        if (ReferenceEquals(_layout, layout) && _lineCount == lineCount) return this;
        _layout = layout;
        _lineCount = lineCount;
        RowCount = Math.Max(1, layout.Length);

        var rows = new int[Math.Max(0, lineCount)];
        Array.Fill(rows, -1);
        for (int i = 0; i < layout.Length; i++)
        {
            var segment = layout[i];
            if (segment.IsVirtualRow || segment.IsContinuation) continue;
            if (segment.BufferLine >= 0 && segment.BufferLine < rows.Length && rows[segment.BufferLine] < 0)
                rows[segment.BufferLine] = i;
        }
        // 隠れた行は直前の見えている行へ（先頭から隠れていることは無いが、念のため 0）。
        int previous = 0;
        for (int line = 0; line < rows.Length; line++)
        {
            if (rows[line] < 0) rows[line] = previous;
            else previous = rows[line];
        }
        _rowOfLine = rows;
        return this;
    }

    /// <summary>バッファ行の先頭の表示行。範囲外の行は端へ寄せる。</summary>
    public int RowOf(int line)
    {
        if (_rowOfLine.Length == 0) return 0;
        return _rowOfLine[Math.Clamp(line, 0, _rowOfLine.Length - 1)];
    }

    /// <summary>そのバッファ行が畳まれて見えていないか（自分の表示行を持たないか）。</summary>
    public bool IsHidden(int line)
    {
        if (_layout is null || line < 0 || line >= _rowOfLine.Length) return false;
        var segment = _layout[_rowOfLine[line]];
        return segment.BufferLine != line;
    }
}
