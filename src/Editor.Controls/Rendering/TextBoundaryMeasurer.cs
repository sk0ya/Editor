using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.TextFormatting;

namespace Editor.Controls.Rendering;

/// <summary>
/// Measures every UTF-16 caret boundary from one WPF shaping pass. This preserves kerning,
/// ligatures and fallback-font metrics without the quadratic cost of formatting every prefix.
/// </summary>
internal static class TextBoundaryMeasurer
{
    public static double[] Measure(string text, Typeface typeface, double fontSize, double pixelsPerDip)
    {
        var boundaries = new double[text.Length + 1];
        if (text.Length == 0) return boundaries;

        using var formatter = TextFormatter.Create();
        var runProperties = new RunProperties(typeface, fontSize, pixelsPerDip);
        var source = new StringTextSource(text, runProperties);
        var paragraphProperties = new ParagraphProperties(runProperties);

        // One TextLine does not necessarily cover the whole string: even with NoWrap,
        // TextFormatter stops at the paragraph width, so a very long line (a minified bundle,
        // a base64 data URL) comes back truncated - and asking a truncated line for a character
        // beyond its end throws ("cpFirst must be greater than or equal to 1"). Format
        // successive lines from where the previous one ended and accumulate their widths.
        int start = 0;
        double offset = 0;
        while (start < text.Length)
        {
            // WPF caps paragraph widths at roughly 3.58 million DIPs. This is effectively
            // unbounded for an editor line while remaining valid for TextFormatter.
            using var line = formatter.FormatLine(source, start, 1_000_000,
                paragraphProperties, null);
            // line.Length counts the end-of-paragraph run on the final chunk, so clamp to the text.
            int end = Math.Min(start + line.Length, text.Length);
            if (end <= start)
                break;      // no progress (should not happen): leave the rest at the last width
            for (int i = start + 1; i <= end; i++)
                boundaries[i] = offset + line.GetDistanceFromCharacterHit(new CharacterHit(i, 0));
            offset = boundaries[end];
            start = end;
        }
        // Anything left unmeasured (the break above) keeps the last measured width, which
        // degrades placement instead of throwing.
        for (int i = start + 1; i <= text.Length; i++)
            boundaries[i] = offset;

        return boundaries;
    }

    private sealed class StringTextSource(string text, TextRunProperties properties) : TextSource
    {
        public override TextRun GetTextRun(int textSourceCharacterIndex)
        {
            if (textSourceCharacterIndex >= text.Length)
                return new TextEndOfParagraph(1);
            return new TextCharacters(text, textSourceCharacterIndex,
                text.Length - textSourceCharacterIndex, properties);
        }

        public override TextSpan<CultureSpecificCharacterBufferRange> GetPrecedingText(int textSourceCharacterIndex)
        {
            int length = Math.Clamp(textSourceCharacterIndex, 0, text.Length);
            return new TextSpan<CultureSpecificCharacterBufferRange>(length,
                new CultureSpecificCharacterBufferRange(CultureInfo.CurrentCulture,
                    new CharacterBufferRange(text, 0, length)));
        }

        public override int GetTextEffectCharacterIndexFromTextSourceCharacterIndex(int textSourceCharacterIndex)
            => textSourceCharacterIndex;
    }

    private sealed class RunProperties : TextRunProperties
    {
        private readonly Typeface _typeface;
        private readonly double _fontSize;

        public RunProperties(Typeface typeface, double fontSize, double pixelsPerDip)
        {
            _typeface = typeface;
            _fontSize = fontSize;
            PixelsPerDip = pixelsPerDip;
        }

        public override Typeface Typeface => _typeface;
        public override double FontRenderingEmSize => _fontSize;
        public override double FontHintingEmSize => _fontSize;
        public override TextDecorationCollection? TextDecorations => null;
        public override Brush ForegroundBrush => Brushes.White;
        public override Brush? BackgroundBrush => null;
        public override CultureInfo CultureInfo => CultureInfo.CurrentCulture;
        public override TextEffectCollection? TextEffects => null;
        public override BaselineAlignment BaselineAlignment => BaselineAlignment.Baseline;
    }

    private sealed class ParagraphProperties(TextRunProperties defaultRunProperties)
        : TextParagraphProperties
    {
        public override FlowDirection FlowDirection => FlowDirection.LeftToRight;
        public override TextAlignment TextAlignment => TextAlignment.Left;
        public override double LineHeight => 0;
        public override bool FirstLineInParagraph => true;
        public override TextRunProperties DefaultTextRunProperties => defaultRunProperties;
        public override TextWrapping TextWrapping => TextWrapping.NoWrap;
        public override TextMarkerProperties? TextMarkerProperties => null;
        public override double Indent => 0;
    }
}
