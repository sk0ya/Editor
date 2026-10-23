namespace Editor.Core.Syntax;

public interface ISyntaxLanguage
{
    string Name { get; }
    string[] Extensions { get; }
    string? LineCommentPrefix { get; }

    /// <summary>Opening delimiter of this language's block comment (e.g. "/*", "&lt;!--"), or null if none.</summary>
    string? BlockCommentPrefix => null;

    /// <summary>Closing delimiter of this language's block comment (e.g. "*/", "--&gt;"), or null if none.</summary>
    string? BlockCommentSuffix => null;

    LineTokens[] Tokenize(string[] lines);
}

public class SyntaxEngine
{
    public const int LargeFileLineThreshold = 5000;
    public const int LargeFileCharacterThreshold = 1_000_000;
    public const int VisibleRangeContextLineCount = 200;

    private readonly SyntaxLanguageRegistry _languages;

    private ISyntaxLanguage? _currentLanguage;
    private LineTokens[]? _cachedTokens;
    private string[]? _cachedLines;
    private LineTokens[]? _cachedVisibleTokens;
    private string[]? _cachedVisibleLines;
    private int _cachedVisibleStart = -1;
    private int _cachedVisibleEnd = -1;

    public string? LanguageName => _currentLanguage?.Name;

    public SyntaxEngine(SyntaxLanguageRegistry? languages = null) => _languages = languages ?? SyntaxLanguageRegistry.Default;

    public void DetectLanguage(string? filePath)
    {
        if (filePath == null) { _currentLanguage = null; Invalidate(); return; }
        var ext = Path.GetExtension(filePath).ToLower();
        _currentLanguage = _languages.CreateByExtension(ext);
        Invalidate();
    }

    public void SetLanguage(string name)
    {
        _currentLanguage = _languages.CreateByName(name);
        Invalidate();
    }

    public LineTokens[] Tokenize(string[] lines)
    {
        if (_currentLanguage == null) return [];
        if (lines == _cachedLines && _cachedTokens != null) return _cachedTokens;
        _cachedLines = lines;
        _cachedTokens = _currentLanguage is ILineStateSyntaxLanguage lineState
            ? TokenizeIncrementally(lineState, lines)
            : _currentLanguage.Tokenize(lines);
        return _cachedTokens;
    }

    /// <summary>
    /// 前回分けた結果を引き継いで、変わった行から先だけを分け直す。1打鍵ごとに数千行のファイル全体を
    /// UI スレッドで分け直していた（7500行の .cs で打鍵時間の 3 割）のを、ふつうの編集では数行で済ませる。
    /// <para>前回と同じ先頭の行はそのまま使い、変わった最初の行から分け直す。末尾の同じ行の範囲に入ったら、
    /// その行へ持ち込む状態が前回と同じかを見て、同じならそこから先は前回の結果を行番号だけずらして使う
    /// （ブロックコメントを開いた・閉じたなど状態が変わり続ける間は、一致するまで分け続ける）。</para>
    /// <para>前回の行は複製で持つ。呼び出し側が渡した配列をあとで書き換えても、差分の判定がずれない。</para>
    /// </summary>
    private LineTokens[] TokenizeIncrementally(ILineStateSyntaxLanguage language, string[] lines)
    {
        int n = lines.Length;
        var tokens = new LineTokens[n];
        var states = new object[n + 1];

        var oldLines = _incrementalLines;
        var oldTokens = _incrementalTokens;
        var oldStates = _incrementalStates;
        bool reuse = oldLines != null && oldTokens != null && oldStates != null
            && ReferenceEquals(_incrementalLanguage, language);
        int m = reuse ? oldLines!.Length : 0;
        int prefix = 0, suffix = 0;
        if (reuse)
        {
            int max = Math.Min(n, m);
            while (prefix < max && SameLine(oldLines![prefix], lines[prefix])) prefix++;
            while (suffix < max - prefix && SameLine(oldLines![m - 1 - suffix], lines[n - 1 - suffix])) suffix++;
            Array.Copy(oldTokens!, tokens, prefix);
            Array.Copy(oldStates!, states, prefix + 1);
        }
        else
        {
            states[0] = language.InitialState;
        }

        var state = states[prefix];
        int shift = m - n; // 末尾の同じ範囲で、新しい行 i は前回の行 i + shift
        for (int i = prefix; i < n; i++)
        {
            if (reuse && i >= n - suffix && Equals(state, oldStates![i + shift]))
            {
                for (int k = i; k < n; k++)
                {
                    tokens[k] = new LineTokens(k, oldTokens![k + shift].Tokens);
                    states[k + 1] = oldStates![k + shift + 1];
                }
                break;
            }
            tokens[i] = new LineTokens(i, language.TokenizeLine(lines[i], ref state));
            states[i + 1] = state;
        }

        _incrementalLanguage = language;
        _incrementalLines = (string[])lines.Clone();
        _incrementalTokens = tokens;
        _incrementalStates = states;
        return tokens;

        static bool SameLine(string a, string b) => ReferenceEquals(a, b) || string.Equals(a, b, StringComparison.Ordinal);
    }

    private ILineStateSyntaxLanguage? _incrementalLanguage;
    private string[]? _incrementalLines;
    private LineTokens[]? _incrementalTokens;
    private object[]? _incrementalStates;

    public LineTokens[] TokenizeVisible(string[] lines, int firstLine, int lastLine)
    {
        if (_currentLanguage == null || lines.Length == 0) return [];
        if (ExceedsCharacterBudget(lines)) return [];
        if (lines.Length <= LargeFileLineThreshold) return Tokenize(lines);
        if (RequiresFullDocumentContext(_currentLanguage)) return Tokenize(lines);

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

    private static bool ExceedsCharacterBudget(string[] lines)
    {
        int total = 0;
        foreach (var line in lines)
        {
            total += line.Length;
            if (total > LargeFileCharacterThreshold)
                return true;
        }
        return false;
    }

    public SyntaxToken[] TokenizeLine(string[] allLines, int lineIndex)
    {
        var all = Tokenize(allLines);
        var found = all.FirstOrDefault(t => t.Line == lineIndex);
        return found.Tokens ?? [];
    }

    public string? GetCommentPrefix() => _currentLanguage?.LineCommentPrefix;

    /// <summary>Returns the current language's block-comment delimiters, or null if it has none.</summary>
    public (string Prefix, string Suffix)? GetBlockComment()
    {
        var prefix = _currentLanguage?.BlockCommentPrefix;
        var suffix = _currentLanguage?.BlockCommentSuffix;
        if (prefix == null || suffix == null) return null;
        return (prefix, suffix);
    }

    private static bool RequiresFullDocumentContext(ISyntaxLanguage language) =>
        language.Name is "C#"
            or "Python"
            or "XML"
            or "Markdown"
            or "JavaScript"
            or "TypeScript"
            or "Rust"
            or "CSS"
            or "SQL"
            or "C++"
            or "Go"
            or "PowerShell"
            or "Shell"
            or "TOML";

    public void Invalidate()
    {
        _cachedTokens = null;
        _cachedLines = null;
        _cachedVisibleTokens = null;
        _cachedVisibleLines = null;
        _cachedVisibleStart = -1;
        _cachedVisibleEnd = -1;
        // 差分の基準（_incremental*）は残す。エンジンは本文が変わるたびにここを呼ぶが、差分は前回の
        // 行の<b>中身</b>と比べて決めるので、捨てると毎打鍵ファイル全体を分け直すことになる。
        // 言語が変われば言語のインスタンスが変わるので、基準は使われない。
    }
}
