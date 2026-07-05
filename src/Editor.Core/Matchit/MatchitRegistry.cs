using Editor.Core.Matchit.Languages;

namespace Editor.Core.Matchit;

/// <summary>
/// Resolves the <see cref="IMatchitLanguage"/> for a file's extension, analogous to
/// <see cref="Editor.Core.Folds.SyntaxFoldDetector"/>.
/// </summary>
public static class MatchitRegistry
{
    private static readonly IMatchitLanguage[] _languages =
    [
        new LuaMatchitLanguage(),
        new RubyMatchitLanguage(),
        new PreprocessorMatchitLanguage(),
    ];

    private static readonly Dictionary<string, IMatchitLanguage> _byExtension =
        _languages
            .SelectMany(lang => lang.Extensions.Select(ext => (ext, lang)))
            .ToDictionary(t => t.ext, t => t.lang, StringComparer.OrdinalIgnoreCase);

    public static IMatchitLanguage? Resolve(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return null;
        var ext = Path.GetExtension(filePath);
        if (string.IsNullOrEmpty(ext)) return null;
        return _byExtension.TryGetValue(ext, out var lang) ? lang : null;
    }
}
