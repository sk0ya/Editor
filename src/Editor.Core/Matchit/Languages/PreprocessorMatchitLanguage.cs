namespace Editor.Core.Matchit.Languages;

/// <summary>C-family preprocessor directive matching: #if/#ifdef/#ifndef, #elif, #else, #endif.</summary>
public class PreprocessorMatchitLanguage : IMatchitLanguage
{
    public string[] Extensions => [".c", ".h", ".cpp", ".hpp", ".cc", ".cxx", ".hh", ".cs", ".java"];

    public char[] ExtraWordChars => ['#'];

    public IReadOnlyList<string[][]> KeywordChains =>
    [
        [ ["#if", "#ifdef", "#ifndef"], ["#elif"], ["#else"], ["#endif"] ],
    ];
}
