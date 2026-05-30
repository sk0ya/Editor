using Editor.Core.Syntax.Languages;

namespace Editor.Core.Syntax;

public interface ISyntaxLanguage
{
    string Name { get; }
    string[] Extensions { get; }
    string? LineCommentPrefix { get; }
    LineTokens[] Tokenize(string[] lines);
}

public class SyntaxEngine
{
    public const int LargeFileLineThreshold = 5000;
    public const int VisibleRangeContextLineCount = 200;

    private static readonly ISyntaxLanguage[] _languages =
    [
        new CSharpSyntax(),
        new PythonSyntax(),
        new XmlSyntax(),
        new MarkdownSyntax(),
        new JavaScriptSyntax(),
        new TypeScriptSyntax(),
        new RustSyntax(),
        new JsonSyntax(),
        new TomlSyntax(),
        new YamlSyntax(),
        new ShellSyntax(),
        new CssSyntax(),
        new SqlSyntax(),
        new CppSyntax(),
        new GoSyntax(),
        new BatchSyntax(),
        new PowerShellSyntax(),
    ];

    private ISyntaxLanguage? _currentLanguage;
    private LineTokens[]? _cachedTokens;
    private string[]? _cachedLines;
    private LineTokens[]? _cachedVisibleTokens;
    private string[]? _cachedVisibleLines;
    private int _cachedVisibleStart = -1;
    private int _cachedVisibleEnd = -1;

    public string? LanguageName => _currentLanguage?.Name;

    public void DetectLanguage(string? filePath)
    {
        if (filePath == null) { _currentLanguage = null; Invalidate(); return; }
        var ext = Path.GetExtension(filePath).ToLower();
        _currentLanguage = _languages.FirstOrDefault(l => l.Extensions.Contains(ext));
        Invalidate();
    }

    public void SetLanguage(string name)
    {
        _currentLanguage = _languages.FirstOrDefault(l =>
            l.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        Invalidate();
    }

    public LineTokens[] Tokenize(string[] lines)
    {
        if (_currentLanguage == null) return [];
        if (lines == _cachedLines && _cachedTokens != null) return _cachedTokens;
        _cachedLines = lines;
        _cachedTokens = _currentLanguage.Tokenize(lines);
        return _cachedTokens;
    }

    public LineTokens[] TokenizeVisible(string[] lines, int firstLine, int lastLine)
    {
        if (_currentLanguage == null || lines.Length == 0) return [];
        if (lines.Length <= LargeFileLineThreshold) return Tokenize(lines);

        firstLine = Math.Clamp(firstLine, 0, lines.Length - 1);
        lastLine = Math.Clamp(lastLine, firstLine, lines.Length - 1);

        if (lines == _cachedVisibleLines &&
            firstLine == _cachedVisibleStart &&
            lastLine == _cachedVisibleEnd &&
            _cachedVisibleTokens != null)
            return _cachedVisibleTokens;

        int contextStart = Math.Max(0, firstLine - VisibleRangeContextLineCount);
        int windowLength = lastLine - contextStart + 1;
        var window = new string[windowLength];
        Array.Copy(lines, contextStart, window, 0, windowLength);

        int visibleStartInWindow = firstLine - contextStart;
        int visibleEndInWindow = lastLine - contextStart;
        var windowTokens = _currentLanguage.Tokenize(window);
        _cachedVisibleTokens = windowTokens
            .Where(t => t.Line >= visibleStartInWindow && t.Line <= visibleEndInWindow)
            .Select(t => new LineTokens(t.Line + contextStart, t.Tokens))
            .ToArray();

        _cachedVisibleLines = lines;
        _cachedVisibleStart = firstLine;
        _cachedVisibleEnd = lastLine;
        return _cachedVisibleTokens;
    }

    public SyntaxToken[] TokenizeLine(string[] allLines, int lineIndex)
    {
        var all = Tokenize(allLines);
        var found = all.FirstOrDefault(t => t.Line == lineIndex);
        return found.Tokens ?? [];
    }

    public string? GetCommentPrefix() => _currentLanguage?.LineCommentPrefix;

    public void Invalidate()
    {
        _cachedTokens = null;
        _cachedLines = null;
        _cachedVisibleTokens = null;
        _cachedVisibleLines = null;
        _cachedVisibleStart = -1;
        _cachedVisibleEnd = -1;
    }
}
