using Editor.Core.Syntax.Languages;

namespace Editor.Core.Syntax;

public interface ISyntaxLanguage
{
    string Name { get; }
    string[] Extensions { get; }
    LineTokens[] Tokenize(string[] lines);
}

public class SyntaxEngine
{
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
    ];

    private ISyntaxLanguage? _currentLanguage;
    private LineTokens[]? _cachedTokens;
    private string[]? _cachedLines;

    public string? LanguageName => _currentLanguage?.Name;

    public void DetectLanguage(string? filePath)
    {
        if (filePath == null) { _currentLanguage = null; return; }
        var ext = Path.GetExtension(filePath).ToLower();
        _currentLanguage = _languages.FirstOrDefault(l => l.Extensions.Contains(ext));
        _cachedTokens = null;
    }

    public void SetLanguage(string name)
    {
        _currentLanguage = _languages.FirstOrDefault(l =>
            l.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        _cachedTokens = null;
    }

    public LineTokens[] Tokenize(string[] lines)
    {
        if (_currentLanguage == null) return [];
        if (lines == _cachedLines && _cachedTokens != null) return _cachedTokens;
        _cachedLines = lines;
        _cachedTokens = _currentLanguage.Tokenize(lines);
        return _cachedTokens;
    }

    public SyntaxToken[] TokenizeLine(string[] allLines, int lineIndex)
    {
        var all = Tokenize(allLines);
        var found = all.FirstOrDefault(t => t.Line == lineIndex);
        return found.Tokens ?? [];
    }

    public void Invalidate() => _cachedTokens = null;
}
