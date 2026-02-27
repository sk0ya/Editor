using System.Windows.Media;
using Editor.Core.Syntax;

namespace Editor.Controls.Themes;

public class EditorTheme
{
    public Brush Background { get; init; } = Brushes.Black;
    public Brush Foreground { get; init; } = Brushes.White;
    public Brush CursorBackground { get; init; } = Brushes.White;
    public Brush CursorForeground { get; init; } = Brushes.Black;
    public Brush InsertCursor { get; init; } = Brushes.White;
    public Brush LineNumberFg { get; init; } = Brushes.Gray;
    public Brush LineNumberBg { get; init; } = Brushes.Black;
    public Brush CurrentLineBg { get; init; } = new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x30));
    public Brush SelectionBg { get; init; } = new SolidColorBrush(Color.FromArgb(0x80, 0x44, 0x88, 0xCC));
    public Brush SearchHighlightBg { get; init; } = new SolidColorBrush(Color.FromArgb(0xA0, 0xFF, 0xCC, 0x00));
    public Brush StatusBarNormal { get; init; } = new SolidColorBrush(Color.FromRgb(0x00, 0x5F, 0x87));
    public Brush StatusBarInsert { get; init; } = new SolidColorBrush(Color.FromRgb(0x00, 0x87, 0x00));
    public Brush StatusBarVisual { get; init; } = new SolidColorBrush(Color.FromRgb(0x87, 0x5F, 0x00));
    public Brush StatusBarReplace { get; init; } = new SolidColorBrush(Color.FromRgb(0x87, 0x00, 0x00));
    public Brush StatusBarFg { get; init; } = Brushes.White;

    // Token colors
    public Brush TokenKeyword { get; init; } = new SolidColorBrush(Color.FromRgb(0x56, 0x9C, 0xD6));
    public Brush TokenString { get; init; } = new SolidColorBrush(Color.FromRgb(0xCE, 0x91, 0x78));
    public Brush TokenComment { get; init; } = new SolidColorBrush(Color.FromRgb(0x6A, 0x99, 0x55));
    public Brush TokenNumber { get; init; } = new SolidColorBrush(Color.FromRgb(0xB5, 0xCE, 0xA8));
    public Brush TokenPreprocessor { get; init; } = new SolidColorBrush(Color.FromRgb(0x9B, 0x9B, 0x9B));
    public Brush TokenType { get; init; } = new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0xB0));
    public Brush TokenAttribute { get; init; } = new SolidColorBrush(Color.FromRgb(0x9C, 0xDC, 0xFE));
    public Brush TokenIdentifier { get; init; } = new SolidColorBrush(Color.FromRgb(0xDC, 0xDC, 0xDC));

    public Brush GetTokenBrush(TokenKind kind) => kind switch
    {
        TokenKind.Keyword => TokenKeyword,
        TokenKind.String => TokenString,
        TokenKind.Comment => TokenComment,
        TokenKind.Number => TokenNumber,
        TokenKind.Preprocessor => TokenPreprocessor,
        TokenKind.Type => TokenType,
        TokenKind.Attribute => TokenAttribute,
        TokenKind.Identifier => TokenIdentifier,
        _ => Foreground
    };

    public static EditorTheme Dracula { get; } = new()
    {
        Background = new SolidColorBrush(Color.FromRgb(0x28, 0x2A, 0x36)),
        Foreground = new SolidColorBrush(Color.FromRgb(0xF8, 0xF8, 0xF2)),
        CursorBackground = new SolidColorBrush(Color.FromRgb(0xF8, 0xF8, 0xF2)),
        CursorForeground = new SolidColorBrush(Color.FromRgb(0x28, 0x2A, 0x36)),
        InsertCursor = new SolidColorBrush(Color.FromRgb(0xF8, 0xF8, 0xF2)),
        LineNumberFg = new SolidColorBrush(Color.FromRgb(0x63, 0x65, 0x72)),
        LineNumberBg = new SolidColorBrush(Color.FromRgb(0x21, 0x22, 0x2C)),
        CurrentLineBg = new SolidColorBrush(Color.FromRgb(0x35, 0x37, 0x46)),
        SelectionBg = new SolidColorBrush(Color.FromArgb(0x80, 0x44, 0x47, 0x5A)),
        StatusBarNormal = new SolidColorBrush(Color.FromRgb(0x61, 0x48, 0xDE)),
        StatusBarInsert = new SolidColorBrush(Color.FromRgb(0x50, 0xFA, 0x7B)),
        StatusBarVisual = new SolidColorBrush(Color.FromRgb(0xFF, 0xB8, 0x6C)),
        TokenKeyword = new SolidColorBrush(Color.FromRgb(0xFF, 0x79, 0xC6)),
        TokenString = new SolidColorBrush(Color.FromRgb(0xF1, 0xFA, 0x8C)),
        TokenComment = new SolidColorBrush(Color.FromRgb(0x63, 0x65, 0x72)),
        TokenNumber = new SolidColorBrush(Color.FromRgb(0xBD, 0x93, 0xF9)),
        TokenType = new SolidColorBrush(Color.FromRgb(0x8B, 0xE9, 0xFD)),
        TokenAttribute = new SolidColorBrush(Color.FromRgb(0x50, 0xFA, 0x7B)),
        TokenIdentifier = new SolidColorBrush(Color.FromRgb(0xF8, 0xF8, 0xF2)),
        StatusBarFg = new SolidColorBrush(Color.FromRgb(0x28, 0x2A, 0x36)),
        SearchHighlightBg = new SolidColorBrush(Color.FromArgb(0xA0, 0xFF, 0xB8, 0x6C)),
    };

    public static EditorTheme Dark { get; } = new();

    public static EditorTheme GetByName(string name) => name.ToLower() switch
    {
        "dracula" => Dracula,
        _ => Dark
    };
}
