using System.Windows;
using System.Windows.Media;

namespace Editor.Controls.Rendering;

/// <summary>
/// Text measurement/formatting functions shared by <see cref="EditorCanvas"/> and the renderer
/// classes it composes (<see cref="GutterRenderer"/>, <see cref="OverlayRenderer"/>,
/// <see cref="LspOverlayRenderer"/>). EditorCanvas owns the actual font/DPI/table-override state;
/// this just bundles the existing measurement methods as delegates (method groups) so the
/// stateless renderer classes can format text and measure columns without holding a reference
/// back to EditorCanvas itself. Built fresh once per <c>OnRender</c> call.
/// </summary>
public sealed class GlyphMetrics(
    Func<string, Brush, FormattedText> formatText,
    Func<string, Brush, double, FormattedText> formatScaledText,
    Func<string, int, double> getVisualX,
    double lineHeight,
    double charWidth,
    Action<DrawingContext, string, Brush, double, double>? drawLabel = null,
    Func<string, Brush, double, TextLabel>? scaledLabel = null)
{
    public FormattedText FormatText(string text, Brush brush) => formatText(text, brush);

    /// <summary>Formats text at <paramref name="sizeScale"/> times the normal font size (e.g. italic inlay hints).</summary>
    public FormattedText FormatScaledText(string text, Brush brush, double sizeScale) => formatScaledText(text, brush, sizeScale);

    public double GetVisualX(string line, int col) => getVisualX(line, col);

    /// <summary>短い文字列（行番号など）を <paramref name="x"/> から、行 <paramref name="rowTop"/> の高さの中で
    /// 縦に中央へ描く。キャンバスが作り置きを持っていればそれを使う（毎フレーム組み直さない）。</summary>
    public void DrawLabel(DrawingContext dc, string text, Brush brush, double x, double rowTop)
    {
        if (drawLabel is not null)
        {
            drawLabel(dc, text, brush, x, rowTop);
            return;
        }
        var formatted = formatText(text, brush);
        dc.DrawText(formatted, new Point(x, rowTop + (lineHeight - formatted.Height) / 2));
    }

    /// <summary><paramref name="sizeScale"/> 倍の斜体で組んだ短い文字列（CodeLens のラベルなど）。幅を測ってから
    /// 描けるよう、組んだ結果を返す。キャンバスが作り置きを持っていればそれを返す（毎フレーム組み直さない）。</summary>
    public TextLabel ScaledLabel(string text, Brush brush, double sizeScale)
    {
        if (scaledLabel is not null) return scaledLabel(text, brush, sizeScale);
        var formatted = formatScaledText(text, brush, sizeScale);
        var group = new DrawingGroup();
        using (var context = group.Open())
            context.DrawText(formatted, new Point(0, 0));
        return new TextLabel(group, formatted.Width, formatted.Height);
    }

    public double LineHeight => lineHeight;
    public double CharWidth => charWidth;
}

/// <summary>組み終えた短い文字列の絵と大きさ。<see cref="Draw"/> で行の高さの中に縦中央で置く。</summary>
public sealed class TextLabel(Drawing drawing, double width, double height)
{
    public double Width { get; } = width;
    public double Height { get; } = height;

    public void Draw(DrawingContext dc, double x, double rowTop, double rowHeight)
    {
        var transform = new TranslateTransform(x, rowTop + (rowHeight - Height) / 2);
        transform.Freeze();
        dc.PushTransform(transform);
        dc.DrawDrawing(drawing);
        dc.Pop();
    }
}
