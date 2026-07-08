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
    double charWidth)
{
    public FormattedText FormatText(string text, Brush brush) => formatText(text, brush);

    /// <summary>Formats text at <paramref name="sizeScale"/> times the normal font size (e.g. italic inlay hints).</summary>
    public FormattedText FormatScaledText(string text, Brush brush, double sizeScale) => formatScaledText(text, brush, sizeScale);

    public double GetVisualX(string line, int col) => getVisualX(line, col);

    public double LineHeight => lineHeight;
    public double CharWidth => charWidth;
}
