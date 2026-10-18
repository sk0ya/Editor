using System.Windows;
using System.Windows.Media;

namespace Editor.Controls.Rendering;

/// <summary>
/// EditorCanvas の「1 行だけの行末の注記」（カーソル行の blame＝VS Code の Git blame decoration）。
/// 行末の値（<see cref="SetInlineValues"/>）と同じく中身はホスト（コントロール）が決め、キャンバスは描くだけ。
/// 同じ行に行末の値か入力の先読みが出ているときは描かない——行末に薄い文字が 2 種類並ぶと読めない。
/// </summary>
public partial class EditorCanvas
{
    private int _lineAnnotationLine = -1;
    private string? _lineAnnotationText;

    /// <summary>注記の薄さ。行末の値より淡くして、デバッグ中の値と見分ける。</summary>
    private const double LineAnnotationOpacity = 0.4;

    /// <summary>行末の注記を置き換える（<paramref name="text"/> が null か空なら消す）。<paramref name="line0"/> は 0 始まり。</summary>
    public void SetLineAnnotation(int line0, string? text)
    {
        if (string.IsNullOrEmpty(text) || line0 < 0)
        {
            if (_lineAnnotationText is null) return;
            _lineAnnotationLine = -1;
            _lineAnnotationText = null;
            InvalidateVisual();
            return;
        }
        if (_lineAnnotationLine == line0 && _lineAnnotationText == text) return;
        _lineAnnotationLine = line0;
        _lineAnnotationText = text;
        InvalidateVisual();
    }

    /// <summary>いま出している注記（テスト用のシーム）。</summary>
    internal (int Line0, string? Text) LineAnnotation => (_lineAnnotationLine, _lineAnnotationText);

    private void DrawLineAnnotation(
        DrawingContext dc, GlyphMetrics metrics, int lineIndex, string lineText, double y, double textLeft,
        bool isLastSegment)
    {
        if (_lineAnnotationText is not { } text || lineIndex != _lineAnnotationLine || !isLastSegment) return;
        if (_inlineValues.ContainsKey(lineIndex)) return;
        if (_inlineSuggestionText is not null && _inlineSuggestionLine == lineIndex) return;

        double x = textLeft + metrics.GetVisualX(lineText, lineText.Length) - _scrollOffsetX
                   + metrics.CharWidth * InlineValueGapChars;
        var ft = metrics.FormatText(text, Theme.Foreground);
        dc.PushOpacity(LineAnnotationOpacity);
        dc.DrawText(ft, new Point(x, y));
        dc.Pop();
    }
}
