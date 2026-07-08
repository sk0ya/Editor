using System.Windows.Media;

namespace Editor.Controls.Rendering;

/// <summary>
/// Shared chrome (background/border brushes) for the floating popups drawn over the text area —
/// IME candidates (in <see cref="EditorCanvas"/>), signature help / completion / code actions (in
/// <see cref="LspOverlayRenderer"/>). Theme-independent, so defined once and frozen.
/// </summary>
internal static class PopupChrome
{
    public static readonly SolidColorBrush Bg1    = Freeze(new SolidColorBrush(Color.FromArgb(0xF0, 0x25, 0x26, 0x33)));
    public static readonly SolidColorBrush Bg2    = Freeze(new SolidColorBrush(Color.FromArgb(0xF0, 0x1E, 0x1F, 0x29)));
    public static readonly SolidColorBrush Bg3    = Freeze(new SolidColorBrush(Color.FromArgb(0xEE, 0x1E, 0x1F, 0x29)));
    public static readonly SolidColorBrush DocBg  = Freeze(new SolidColorBrush(Color.FromArgb(0xF0, 0x26, 0x27, 0x35)));
    public static readonly Pen             Border = FreezePen(new Pen(Freeze(new SolidColorBrush(Color.FromRgb(0x63, 0x65, 0x72))), 1));

    private static SolidColorBrush Freeze(SolidColorBrush b) { b.Freeze(); return b; }
    private static Pen FreezePen(Pen p) { p.Freeze(); return p; }
}
