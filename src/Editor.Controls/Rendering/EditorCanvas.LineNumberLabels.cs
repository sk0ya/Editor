using System;

namespace Editor.Controls.Rendering;

/// <summary>
/// 行番号列の表示をホストが差し替える口。1枚の文書に別々のファイルの抜粋を並べるビューでは、
/// 文書の行番号ではなく「元ファイルの何行目か」を出したい（見出し行は空欄）。
/// </summary>
public partial class EditorCanvas
{
    private Func<int, string?>? _lineNumberLabel;
    private int _lineNumberLabelWidth;

    /// <summary>
    /// 行番号列に出す文字列を行ごとに決める関数（null を返した行は空欄）。<paramref name="width"/> は
    /// 出しうる最も長い文字列の桁数で、列の幅をそれに合わせる。<paramref name="label"/> に null を渡すと既定へ戻る。
    /// 相対行番号の設定より優先する。
    /// </summary>
    public void SetLineNumberLabels(Func<int, string?>? label, int width)
    {
        _lineNumberLabel = label;
        _lineNumberLabelWidth = label is null ? 0 : Math.Max(0, width);
        RecomputeLineNumberWidth();
        // 列の幅が変わると本文の幅も変わる。折り返しの位置を引き直さないと、次の編集まで古い幅のまま。
        RebuildVisualLayout();
        InvalidateArrange();
        InvalidateVisual();
    }

    /// <summary>行番号列の桁数：文書の行数の桁（最低 3）と、ホストの表示の幅の大きい方。</summary>
    private void RecomputeLineNumberWidth()
        => _lineNumberWidth = Math.Max(Math.Max(3, _lines.Length.ToString().Length), _lineNumberLabelWidth);
}
