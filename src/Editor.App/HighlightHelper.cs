using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Editor.App;

/// <summary>
/// Attached properties that populate a TextBlock's Inlines with highlighted runs
/// where the query string matches within the text (case-insensitive substring match).
/// The highlight color is taken from the "AccentBrush" application resource.
/// </summary>
internal static class HighlightHelper
{
    private static readonly Brush FallbackBrush;

    static HighlightHelper()
    {
        FallbackBrush = new SolidColorBrush(Color.FromRgb(0x61, 0x48, 0xDE));
        FallbackBrush.Freeze();
    }

    public static readonly DependencyProperty TextProperty =
        DependencyProperty.RegisterAttached("Text", typeof(string), typeof(HighlightHelper),
            new PropertyMetadata(null, OnChanged));

    public static readonly DependencyProperty QueryProperty =
        DependencyProperty.RegisterAttached("Query", typeof(string), typeof(HighlightHelper),
            new PropertyMetadata(null, OnChanged));

    public static string? GetText(DependencyObject obj)  => (string?)obj.GetValue(TextProperty);
    public static void    SetText(DependencyObject obj, string? v) => obj.SetValue(TextProperty, v);
    public static string? GetQuery(DependencyObject obj) => (string?)obj.GetValue(QueryProperty);
    public static void    SetQuery(DependencyObject obj, string? v) => obj.SetValue(QueryProperty, v);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock tb) return;

        var text  = GetText(tb)  ?? "";
        var query = GetQuery(tb) ?? "";

        tb.Inlines.Clear();

        if (text.Length == 0) return;

        if (string.IsNullOrEmpty(query))
        {
            tb.Inlines.Add(new Run(text));
            return;
        }

        var accentBrush = (tb.TryFindResource("AccentBrush") as Brush) ?? FallbackBrush;

        int idx = 0;
        while (idx < text.Length)
        {
            int matchIdx = text.IndexOf(query, idx, StringComparison.OrdinalIgnoreCase);
            if (matchIdx < 0)
            {
                tb.Inlines.Add(new Run(text[idx..]));
                break;
            }

            if (matchIdx > idx)
                tb.Inlines.Add(new Run(text[idx..matchIdx]));

            tb.Inlines.Add(new Run(text[matchIdx..(matchIdx + query.Length)])
            {
                Foreground = accentBrush,
                FontWeight = FontWeights.Bold,
            });

            idx = matchIdx + query.Length;
        }
    }
}
