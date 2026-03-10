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
    public Brush CurrentLineNumberFg { get; init; } = Brushes.White;
    public Brush LineNumberBg { get; init; } = Brushes.Black;
    public Brush CurrentLineBg { get; init; } = new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x30));
    public Brush SelectionBg { get; init; } = new SolidColorBrush(Color.FromArgb(0x80, 0x44, 0x88, 0xCC));
    public Brush SearchHighlightBg { get; init; } = new SolidColorBrush(Color.FromArgb(0xA0, 0xFF, 0xCC, 0x00));
    public Brush StatusBarNormal { get; init; } = new SolidColorBrush(Color.FromRgb(0x00, 0x5F, 0x87));
    public Brush StatusBarInsert { get; init; } = new SolidColorBrush(Color.FromRgb(0x00, 0x87, 0x00));
    public Brush StatusBarVisual { get; init; } = new SolidColorBrush(Color.FromRgb(0x87, 0x5F, 0x00));
    public Brush StatusBarReplace { get; init; } = new SolidColorBrush(Color.FromRgb(0x87, 0x00, 0x00));
    public Brush StatusBarFg { get; init; } = Brushes.White;

    // Git diff colors
    public Brush GitAdded    { get; init; } = new SolidColorBrush(Color.FromRgb(0x00, 0xA0, 0x00));
    public Brush GitModified { get; init; } = new SolidColorBrush(Color.FromRgb(0xFF, 0xA0, 0x00));
    public Brush GitDeleted  { get; init; } = new SolidColorBrush(Color.FromRgb(0xCC, 0x00, 0x00));

    // Matching bracket highlight
    public Brush MatchingBracketBackground { get; init; } = new SolidColorBrush(Color.FromArgb(0xB0, 0x4F, 0x4F, 0x7A));

    // Document highlight (LSP textDocument/documentHighlight)
    public Brush DocumentHighlightBackground { get; init; } = new SolidColorBrush(Color.FromArgb(0x44, 0xBD, 0x93, 0xF9));

    // Color column guide line
    public Brush ColorColumnBrush { get; init; } = new SolidColorBrush(Color.FromRgb(0x3D, 0x3D, 0x3D));

    // Listchars color
    public Brush ListCharBrush { get; init; } = new SolidColorBrush(Color.FromRgb(0x60, 0x60, 0x60));

    // Indent guide lines
    public Brush IndentGuideBrush { get; init; } = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x4A));

    // Git merge conflict marker colors
    public SolidColorBrush ConflictOursHeader   { get; init; } = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0x6B, 0x6B));
    public SolidColorBrush ConflictSeparator    { get; init; } = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xD7, 0x00));
    public SolidColorBrush ConflictTheirsHeader { get; init; } = new SolidColorBrush(Color.FromArgb(0x55, 0x6B, 0x9D, 0xFF));
    public SolidColorBrush ConflictOurs         { get; init; } = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0x6B, 0x6B));
    public SolidColorBrush ConflictTheirs       { get; init; } = new SolidColorBrush(Color.FromArgb(0x22, 0x6B, 0x9D, 0xFF));

    // Scrollbar
    public Brush ScrollbarTrack { get; init; } = new SolidColorBrush(Color.FromArgb(0x40, 0x00, 0x00, 0x00));
    public Brush ScrollbarThumb { get; init; } = new SolidColorBrush(Color.FromArgb(0x80, 0xAA, 0xAA, 0xAA));

    // Minimap
    public Brush MinimapBackground { get; init; } = new SolidColorBrush(Color.FromArgb(0xC8, 0x1A, 0x1A, 0x1A));
    public Brush MinimapViewport   { get; init; } = new SolidColorBrush(Color.FromArgb(0x55, 0xAA, 0xAA, 0xAA));

    // Diagnostic colors
    public Brush DiagnosticError   { get; init; } = new SolidColorBrush(Color.FromRgb(0xFF, 0x00, 0x00));
    public Brush DiagnosticWarning { get; init; } = new SolidColorBrush(Color.FromRgb(0xFF, 0xA5, 0x00));
    public Brush DiagnosticInfo    { get; init; } = new SolidColorBrush(Color.FromRgb(0x00, 0xBF, 0xFF));
    public Brush DiagnosticHint    { get; init; } = new SolidColorBrush(Color.FromRgb(0x90, 0x90, 0x90));

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
        CurrentLineNumberFg = new SolidColorBrush(Color.FromRgb(0xF8, 0xF8, 0xF2)),
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
        DiagnosticError   = new SolidColorBrush(Color.FromRgb(0xFF, 0x55, 0x55)),
        DiagnosticWarning = new SolidColorBrush(Color.FromRgb(0xFF, 0xB8, 0x6C)),
        DiagnosticInfo    = new SolidColorBrush(Color.FromRgb(0x8B, 0xE9, 0xFD)),
        DiagnosticHint    = new SolidColorBrush(Color.FromRgb(0x63, 0x65, 0x72)),
        GitAdded    = new SolidColorBrush(Color.FromRgb(0x50, 0xFA, 0x7B)),
        GitModified = new SolidColorBrush(Color.FromRgb(0xFF, 0xB8, 0x6C)),
        GitDeleted  = new SolidColorBrush(Color.FromRgb(0xFF, 0x55, 0x55)),
        ListCharBrush = new SolidColorBrush(Color.FromRgb(0x44, 0x47, 0x5A)),
        ColorColumnBrush = new SolidColorBrush(Color.FromRgb(0x3D, 0x3D, 0x50)),
        MatchingBracketBackground = new SolidColorBrush(Color.FromArgb(0xB0, 0x4F, 0x4F, 0x7A)),
        IndentGuideBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3C, 0x4E)),
        MinimapBackground = new SolidColorBrush(Color.FromArgb(0xD0, 0x1E, 0x1F, 0x29)),
        MinimapViewport   = new SolidColorBrush(Color.FromArgb(0x55, 0xBB, 0xBB, 0xCC)),
        DocumentHighlightBackground = new SolidColorBrush(Color.FromArgb(0x44, 0xBD, 0x93, 0xF9)),
        ConflictOursHeader   = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0x55, 0x55)),
        ConflictSeparator    = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xB8, 0x6C)),
        ConflictTheirsHeader = new SolidColorBrush(Color.FromArgb(0x55, 0x8B, 0xE9, 0xFD)),
        ConflictOurs         = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0x55, 0x55)),
        ConflictTheirs       = new SolidColorBrush(Color.FromArgb(0x22, 0x8B, 0xE9, 0xFD)),
    };

    public static EditorTheme Dark { get; } = new();

    // Nord — https://www.nordtheme.com
    public static EditorTheme Nord { get; } = new()
    {
        Background          = new SolidColorBrush(Color.FromRgb(0x2E, 0x34, 0x40)),
        Foreground          = new SolidColorBrush(Color.FromRgb(0xD8, 0xDE, 0xE9)),
        CursorBackground    = new SolidColorBrush(Color.FromRgb(0xD8, 0xDE, 0xE9)),
        CursorForeground    = new SolidColorBrush(Color.FromRgb(0x2E, 0x34, 0x40)),
        InsertCursor        = new SolidColorBrush(Color.FromRgb(0x88, 0xC0, 0xD0)),
        LineNumberFg        = new SolidColorBrush(Color.FromRgb(0x4C, 0x56, 0x6A)),
        CurrentLineNumberFg = new SolidColorBrush(Color.FromRgb(0xD8, 0xDE, 0xE9)),
        LineNumberBg        = new SolidColorBrush(Color.FromRgb(0x27, 0x2C, 0x36)),
        CurrentLineBg       = new SolidColorBrush(Color.FromRgb(0x3B, 0x42, 0x52)),
        SelectionBg         = new SolidColorBrush(Color.FromArgb(0x80, 0x43, 0x4C, 0x5E)),
        StatusBarNormal     = new SolidColorBrush(Color.FromRgb(0x5E, 0x81, 0xAC)),
        StatusBarInsert     = new SolidColorBrush(Color.FromRgb(0xA3, 0xBE, 0x8C)),
        StatusBarVisual     = new SolidColorBrush(Color.FromRgb(0xEB, 0xCB, 0x8B)),
        StatusBarFg         = new SolidColorBrush(Color.FromRgb(0xEC, 0xEF, 0xF4)),
        SearchHighlightBg   = new SolidColorBrush(Color.FromArgb(0xA0, 0xEB, 0xCB, 0x8B)),
        TokenKeyword        = new SolidColorBrush(Color.FromRgb(0x81, 0xA1, 0xC1)),
        TokenString         = new SolidColorBrush(Color.FromRgb(0xA3, 0xBE, 0x8C)),
        TokenComment        = new SolidColorBrush(Color.FromRgb(0x4C, 0x56, 0x6A)),
        TokenNumber         = new SolidColorBrush(Color.FromRgb(0xB4, 0x8E, 0xAD)),
        TokenType           = new SolidColorBrush(Color.FromRgb(0x8F, 0xBC, 0xBB)),
        TokenAttribute      = new SolidColorBrush(Color.FromRgb(0x88, 0xC0, 0xD0)),
        TokenIdentifier     = new SolidColorBrush(Color.FromRgb(0xD8, 0xDE, 0xE9)),
        DiagnosticError     = new SolidColorBrush(Color.FromRgb(0xBF, 0x61, 0x6A)),
        DiagnosticWarning   = new SolidColorBrush(Color.FromRgb(0xEB, 0xCB, 0x8B)),
        DiagnosticInfo      = new SolidColorBrush(Color.FromRgb(0x88, 0xC0, 0xD0)),
        DiagnosticHint      = new SolidColorBrush(Color.FromRgb(0x4C, 0x56, 0x6A)),
        GitAdded            = new SolidColorBrush(Color.FromRgb(0xA3, 0xBE, 0x8C)),
        GitModified         = new SolidColorBrush(Color.FromRgb(0xEB, 0xCB, 0x8B)),
        GitDeleted          = new SolidColorBrush(Color.FromRgb(0xBF, 0x61, 0x6A)),
        ListCharBrush       = new SolidColorBrush(Color.FromRgb(0x43, 0x4C, 0x5E)),
        ColorColumnBrush    = new SolidColorBrush(Color.FromRgb(0x3B, 0x42, 0x52)),
        MatchingBracketBackground = new SolidColorBrush(Color.FromArgb(0xB0, 0x4C, 0x60, 0x80)),
        IndentGuideBrush    = new SolidColorBrush(Color.FromRgb(0x38, 0x3F, 0x4D)),
        MinimapBackground   = new SolidColorBrush(Color.FromArgb(0xD0, 0x22, 0x27, 0x30)),
        MinimapViewport     = new SolidColorBrush(Color.FromArgb(0x55, 0x88, 0xC0, 0xD0)),
        DocumentHighlightBackground = new SolidColorBrush(Color.FromArgb(0x44, 0x88, 0xC0, 0xD0)),
        ConflictOursHeader   = new SolidColorBrush(Color.FromArgb(0x55, 0xBF, 0x61, 0x6A)),
        ConflictSeparator    = new SolidColorBrush(Color.FromArgb(0x55, 0xEB, 0xCB, 0x8B)),
        ConflictTheirsHeader = new SolidColorBrush(Color.FromArgb(0x55, 0x5E, 0x81, 0xAC)),
        ConflictOurs         = new SolidColorBrush(Color.FromArgb(0x22, 0xBF, 0x61, 0x6A)),
        ConflictTheirs       = new SolidColorBrush(Color.FromArgb(0x22, 0x5E, 0x81, 0xAC)),
    };

    // Tokyo Night — dark variant
    public static EditorTheme TokyoNight { get; } = new()
    {
        Background          = new SolidColorBrush(Color.FromRgb(0x1A, 0x1B, 0x26)),
        Foreground          = new SolidColorBrush(Color.FromRgb(0xA9, 0xB1, 0xD6)),
        CursorBackground    = new SolidColorBrush(Color.FromRgb(0xA9, 0xB1, 0xD6)),
        CursorForeground    = new SolidColorBrush(Color.FromRgb(0x1A, 0x1B, 0x26)),
        InsertCursor        = new SolidColorBrush(Color.FromRgb(0x7A, 0xA2, 0xF7)),
        LineNumberFg        = new SolidColorBrush(Color.FromRgb(0x3B, 0x3D, 0x57)),
        CurrentLineNumberFg = new SolidColorBrush(Color.FromRgb(0x73, 0x7A, 0xA2)),
        LineNumberBg        = new SolidColorBrush(Color.FromRgb(0x16, 0x17, 0x21)),
        CurrentLineBg       = new SolidColorBrush(Color.FromRgb(0x1F, 0x20, 0x35)),
        SelectionBg         = new SolidColorBrush(Color.FromArgb(0x80, 0x28, 0x3B, 0x4D)),
        StatusBarNormal     = new SolidColorBrush(Color.FromRgb(0x36, 0x4A, 0x82)),
        StatusBarInsert     = new SolidColorBrush(Color.FromRgb(0x41, 0xA6, 0xB5)),
        StatusBarVisual     = new SolidColorBrush(Color.FromRgb(0xFF, 0x9E, 0x64)),
        StatusBarFg         = new SolidColorBrush(Color.FromRgb(0xC0, 0xCA, 0xF5)),
        SearchHighlightBg   = new SolidColorBrush(Color.FromArgb(0xA0, 0xFF, 0x9E, 0x64)),
        TokenKeyword        = new SolidColorBrush(Color.FromRgb(0x9D, 0x7C, 0xD8)),
        TokenString         = new SolidColorBrush(Color.FromRgb(0x9E, 0xCE, 0x6A)),
        TokenComment        = new SolidColorBrush(Color.FromRgb(0x56, 0x5F, 0x89)),
        TokenNumber         = new SolidColorBrush(Color.FromRgb(0xFF, 0x9E, 0x64)),
        TokenType           = new SolidColorBrush(Color.FromRgb(0x2A, 0xC3, 0xDE)),
        TokenAttribute      = new SolidColorBrush(Color.FromRgb(0x7A, 0xA2, 0xF7)),
        TokenIdentifier     = new SolidColorBrush(Color.FromRgb(0xA9, 0xB1, 0xD6)),
        DiagnosticError     = new SolidColorBrush(Color.FromRgb(0xF7, 0x76, 0x8E)),
        DiagnosticWarning   = new SolidColorBrush(Color.FromRgb(0xE0, 0xAF, 0x68)),
        DiagnosticInfo      = new SolidColorBrush(Color.FromRgb(0x2A, 0xC3, 0xDE)),
        DiagnosticHint      = new SolidColorBrush(Color.FromRgb(0x56, 0x5F, 0x89)),
        GitAdded            = new SolidColorBrush(Color.FromRgb(0x9E, 0xCE, 0x6A)),
        GitModified         = new SolidColorBrush(Color.FromRgb(0xE0, 0xAF, 0x68)),
        GitDeleted          = new SolidColorBrush(Color.FromRgb(0xF7, 0x76, 0x8E)),
        ListCharBrush       = new SolidColorBrush(Color.FromRgb(0x28, 0x3B, 0x4D)),
        ColorColumnBrush    = new SolidColorBrush(Color.FromRgb(0x1F, 0x20, 0x35)),
        MatchingBracketBackground = new SolidColorBrush(Color.FromArgb(0xB0, 0x36, 0x4A, 0x82)),
        IndentGuideBrush    = new SolidColorBrush(Color.FromRgb(0x25, 0x27, 0x38)),
        MinimapBackground   = new SolidColorBrush(Color.FromArgb(0xD0, 0x13, 0x14, 0x1E)),
        MinimapViewport     = new SolidColorBrush(Color.FromArgb(0x55, 0x7A, 0xA2, 0xF7)),
        DocumentHighlightBackground = new SolidColorBrush(Color.FromArgb(0x44, 0x7A, 0xA2, 0xF7)),
        ConflictOursHeader   = new SolidColorBrush(Color.FromArgb(0x55, 0xF7, 0x76, 0x8E)),
        ConflictSeparator    = new SolidColorBrush(Color.FromArgb(0x55, 0xE0, 0xAF, 0x68)),
        ConflictTheirsHeader = new SolidColorBrush(Color.FromArgb(0x55, 0x7A, 0xA2, 0xF7)),
        ConflictOurs         = new SolidColorBrush(Color.FromArgb(0x22, 0xF7, 0x76, 0x8E)),
        ConflictTheirs       = new SolidColorBrush(Color.FromArgb(0x22, 0x7A, 0xA2, 0xF7)),
    };

    // One Dark — Atom One Dark inspired
    public static EditorTheme OneDark { get; } = new()
    {
        Background          = new SolidColorBrush(Color.FromRgb(0x28, 0x2C, 0x34)),
        Foreground          = new SolidColorBrush(Color.FromRgb(0xAB, 0xB2, 0xBF)),
        CursorBackground    = new SolidColorBrush(Color.FromRgb(0xAB, 0xB2, 0xBF)),
        CursorForeground    = new SolidColorBrush(Color.FromRgb(0x28, 0x2C, 0x34)),
        InsertCursor        = new SolidColorBrush(Color.FromRgb(0x61, 0xAF, 0xEF)),
        LineNumberFg        = new SolidColorBrush(Color.FromRgb(0x4B, 0x51, 0x63)),
        CurrentLineNumberFg = new SolidColorBrush(Color.FromRgb(0xAB, 0xB2, 0xBF)),
        LineNumberBg        = new SolidColorBrush(Color.FromRgb(0x21, 0x25, 0x2B)),
        CurrentLineBg       = new SolidColorBrush(Color.FromRgb(0x2C, 0x31, 0x3A)),
        SelectionBg         = new SolidColorBrush(Color.FromArgb(0x80, 0x3E, 0x44, 0x50)),
        StatusBarNormal     = new SolidColorBrush(Color.FromRgb(0x52, 0x85, 0xBE)),
        StatusBarInsert     = new SolidColorBrush(Color.FromRgb(0x98, 0xC3, 0x79)),
        StatusBarVisual     = new SolidColorBrush(Color.FromRgb(0xD1, 0x9A, 0x66)),
        StatusBarFg         = new SolidColorBrush(Color.FromRgb(0xAB, 0xB2, 0xBF)),
        SearchHighlightBg   = new SolidColorBrush(Color.FromArgb(0xA0, 0xD1, 0x9A, 0x66)),
        TokenKeyword        = new SolidColorBrush(Color.FromRgb(0xC6, 0x78, 0xDD)),
        TokenString         = new SolidColorBrush(Color.FromRgb(0x98, 0xC3, 0x79)),
        TokenComment        = new SolidColorBrush(Color.FromRgb(0x5C, 0x63, 0x70)),
        TokenNumber         = new SolidColorBrush(Color.FromRgb(0xD1, 0x9A, 0x66)),
        TokenType           = new SolidColorBrush(Color.FromRgb(0xE5, 0xC0, 0x7B)),
        TokenAttribute      = new SolidColorBrush(Color.FromRgb(0x61, 0xAF, 0xEF)),
        TokenIdentifier     = new SolidColorBrush(Color.FromRgb(0xAB, 0xB2, 0xBF)),
        DiagnosticError     = new SolidColorBrush(Color.FromRgb(0xE0, 0x6C, 0x75)),
        DiagnosticWarning   = new SolidColorBrush(Color.FromRgb(0xD1, 0x9A, 0x66)),
        DiagnosticInfo      = new SolidColorBrush(Color.FromRgb(0x61, 0xAF, 0xEF)),
        DiagnosticHint      = new SolidColorBrush(Color.FromRgb(0x5C, 0x63, 0x70)),
        GitAdded            = new SolidColorBrush(Color.FromRgb(0x98, 0xC3, 0x79)),
        GitModified         = new SolidColorBrush(Color.FromRgb(0xD1, 0x9A, 0x66)),
        GitDeleted          = new SolidColorBrush(Color.FromRgb(0xE0, 0x6C, 0x75)),
        ListCharBrush       = new SolidColorBrush(Color.FromRgb(0x3E, 0x44, 0x50)),
        ColorColumnBrush    = new SolidColorBrush(Color.FromRgb(0x2C, 0x31, 0x3A)),
        MatchingBracketBackground = new SolidColorBrush(Color.FromArgb(0xB0, 0x3E, 0x52, 0x7A)),
        IndentGuideBrush    = new SolidColorBrush(Color.FromRgb(0x33, 0x38, 0x42)),
        MinimapBackground   = new SolidColorBrush(Color.FromArgb(0xD0, 0x1C, 0x1F, 0x26)),
        MinimapViewport     = new SolidColorBrush(Color.FromArgb(0x55, 0x61, 0xAF, 0xEF)),
        DocumentHighlightBackground = new SolidColorBrush(Color.FromArgb(0x44, 0x61, 0xAF, 0xEF)),
        ConflictOursHeader   = new SolidColorBrush(Color.FromArgb(0x55, 0xE0, 0x6C, 0x75)),
        ConflictSeparator    = new SolidColorBrush(Color.FromArgb(0x55, 0xD1, 0x9A, 0x66)),
        ConflictTheirsHeader = new SolidColorBrush(Color.FromArgb(0x55, 0x61, 0xAF, 0xEF)),
        ConflictOurs         = new SolidColorBrush(Color.FromArgb(0x22, 0xE0, 0x6C, 0x75)),
        ConflictTheirs       = new SolidColorBrush(Color.FromArgb(0x22, 0x61, 0xAF, 0xEF)),
    };

    public static EditorTheme GetByName(string name) => name.ToLower() switch
    {
        "dracula"     => Dracula,
        "nord"        => Nord,
        "tokyonight"  => TokyoNight,
        "tokyo-night" => TokyoNight,
        "onedark"     => OneDark,
        "one-dark"    => OneDark,
        _ => Dark
    };

    public EditorTheme WithBackground(Color bg)
    {
        var darker = Color.FromRgb(
            (byte)(bg.R > 12 ? bg.R - 12 : 0),
            (byte)(bg.G > 12 ? bg.G - 12 : 0),
            (byte)(bg.B > 12 ? bg.B - 12 : 0));
        var lighter = Color.FromRgb(
            (byte)Math.Min(bg.R + 14, 255),
            (byte)Math.Min(bg.G + 14, 255),
            (byte)Math.Min(bg.B + 14, 255));
        return new EditorTheme
        {
            Background = new SolidColorBrush(bg),
            Foreground = Foreground,
            CursorBackground = CursorBackground,
            CursorForeground = new SolidColorBrush(bg),
            InsertCursor = InsertCursor,
            LineNumberFg = LineNumberFg,
            CurrentLineNumberFg = CurrentLineNumberFg,
            LineNumberBg = new SolidColorBrush(darker),
            CurrentLineBg = new SolidColorBrush(lighter),
            SelectionBg = SelectionBg,
            SearchHighlightBg = SearchHighlightBg,
            StatusBarNormal = StatusBarNormal,
            StatusBarInsert = StatusBarInsert,
            StatusBarVisual = StatusBarVisual,
            StatusBarReplace = StatusBarReplace,
            StatusBarFg = StatusBarFg,
            DiagnosticError = DiagnosticError,
            DiagnosticWarning = DiagnosticWarning,
            DiagnosticInfo = DiagnosticInfo,
            DiagnosticHint = DiagnosticHint,
            TokenKeyword = TokenKeyword,
            TokenString = TokenString,
            TokenComment = TokenComment,
            TokenNumber = TokenNumber,
            TokenPreprocessor = TokenPreprocessor,
            TokenType = TokenType,
            TokenAttribute = TokenAttribute,
            TokenIdentifier = TokenIdentifier,
            MatchingBracketBackground = MatchingBracketBackground,
        };
    }

    public EditorTheme WithAccent(Color accent)
    {
        return new EditorTheme
        {
            Background = Background,
            Foreground = Foreground,
            CursorBackground = CursorBackground,
            CursorForeground = CursorForeground,
            InsertCursor = InsertCursor,
            LineNumberFg = LineNumberFg,
            CurrentLineNumberFg = CurrentLineNumberFg,
            LineNumberBg = LineNumberBg,
            CurrentLineBg = CurrentLineBg,
            SelectionBg = SelectionBg,
            SearchHighlightBg = SearchHighlightBg,
            StatusBarNormal = new SolidColorBrush(accent),
            StatusBarInsert = StatusBarInsert,
            StatusBarVisual = StatusBarVisual,
            StatusBarReplace = StatusBarReplace,
            StatusBarFg = StatusBarFg,
            DiagnosticError = DiagnosticError,
            DiagnosticWarning = DiagnosticWarning,
            DiagnosticInfo = DiagnosticInfo,
            DiagnosticHint = DiagnosticHint,
            TokenKeyword = TokenKeyword,
            TokenString = TokenString,
            TokenComment = TokenComment,
            TokenNumber = TokenNumber,
            TokenPreprocessor = TokenPreprocessor,
            TokenType = TokenType,
            TokenAttribute = TokenAttribute,
            TokenIdentifier = TokenIdentifier,
            MatchingBracketBackground = MatchingBracketBackground,
        };
    }
}
