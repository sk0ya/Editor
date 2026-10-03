using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace Editor.Controls.Rendering;

/// <summary>
/// 行末に薄く出す 1 行分の注釈（デバッグ停止中の変数値＝VS Code の Inline Values）。
/// <paramref name="Line0"/> はバッファ行の 0 始まり、<paramref name="Text"/> は表示する文面そのもの
/// （例 <c>x = 3, name = "abc"</c>）。何をどう書くかはホストの責務で、エディタは描くだけ。
/// </summary>
public readonly record struct EditorInlineValue(int Line0, string Text);

/// <summary>
/// EditorCanvas の「行末の値表示」。テストグリフ・カバレッジと同じく、<b>中身はホストが決め、エディタは
/// 行に描くだけ</b>。行番号にしか結び付いていないので、別のファイルを載せたら捨てる（<c>LoadFile</c>）。
///
/// <para>折り返し表示では、その行の<b>最後の</b>表示行の末尾に描く（本文の続きに見えるように）。
/// 入力の先読みが同じ行に出ているときは描かない——行末に薄い文字が 2 種類並ぶと、どちらが Tab で
/// 入るのか読めなくなる。</para>
/// </summary>
public partial class EditorCanvas
{
    private readonly Dictionary<int, string> _inlineValues = new();

    /// <summary>本文の末尾と値の間の空き（文字幅の倍数）。</summary>
    private const double InlineValueGapChars = 3;

    /// <summary>値の薄さ。本文と見分けがつき、かつ読める線（入力の先読みよりわずかに濃い）。</summary>
    private const double InlineValueOpacity = 0.55;

    /// <summary>行末の値を全置換する（空リストで消える）。同じ行が複数あれば「, 」で連結する。
    /// 負の行・空文字は無視する。</summary>
    public void SetInlineValues(IReadOnlyList<EditorInlineValue> values)
    {
        if (_inlineValues.Count == 0 && values.Count == 0) return;
        _inlineValues.Clear();
        foreach (var value in values)
        {
            if (value.Line0 < 0 || string.IsNullOrEmpty(value.Text)) continue;
            _inlineValues[value.Line0] = _inlineValues.TryGetValue(value.Line0, out var existing)
                ? existing + ", " + value.Text
                : value.Text;
        }
        InvalidateVisual();
    }

    /// <summary>いま行末に値を持っている行の数（テスト用のシーム）。</summary>
    internal int InlineValueLineCount => _inlineValues.Count;

    /// <summary>その行に出している文面（無ければ null。テスト用のシーム）。</summary>
    internal string? InlineValueTextAt(int line0) => _inlineValues.TryGetValue(line0, out var text) ? text : null;

    private void DrawInlineValue(
        DrawingContext dc, GlyphMetrics metrics, int lineIndex, string lineText, double y, double textLeft,
        bool isLastSegment)
    {
        if (_inlineValues.Count == 0 || !isLastSegment) return;
        if (!_inlineValues.TryGetValue(lineIndex, out var text)) return;
        if (_inlineSuggestionText is not null && _inlineSuggestionLine == lineIndex) return;

        double x = textLeft + metrics.GetVisualX(lineText, lineText.Length) - _scrollOffsetX
                   + metrics.CharWidth * InlineValueGapChars;
        var ft = metrics.FormatText(text, Theme.Foreground);
        ft.SetFontStyle(FontStyles.Italic);
        dc.PushOpacity(InlineValueOpacity);
        dc.DrawText(ft, new Point(x, y));
        dc.Pop();
    }
}
