using Editor.Core.Folds.Languages;

namespace Editor.Core.Folds;

/// <summary>
/// LSP が foldingRange をサポートしていない場合のフォールバック。
/// ファイル拡張子に対応する <see cref="IFoldLanguage"/> にディスパッチする。
/// </summary>
public static class SyntaxFoldDetector
{
    private static readonly IFoldLanguage[] _languages =
    [
        new BraceFoldLanguage(),
        new MarkdownFoldLanguage(),
        new XmlFoldLanguage(),
        new JsonFoldLanguage(),
        new PythonFoldLanguage(),
    ];

    // 拡張子 → 言語の逆引きテーブル（起動時に1回だけ構築）
    private static readonly Dictionary<string, IFoldLanguage> _byExtension =
        _languages
            .SelectMany(lang => lang.Extensions.Select(ext => (ext, lang)))
            .ToDictionary(t => t.ext, t => t.lang, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// <paramref name="extension"/> に対応する言語検出器でフォールド範囲を返す。
    /// 対応していない拡張子の場合は空リストを返す。
    /// </summary>
    public static IReadOnlyList<(int StartLine, int EndLine)> Detect(string extension, string[] lines)
    {
        if (!_byExtension.TryGetValue(extension, out var lang)) return [];
        var raw = lang.Detect(lines);
        return Deduplicate(raw);
    }

    // 同一 StartLine の重複を除去し、startLine 順にソートして返す
    private static IReadOnlyList<(int, int)> Deduplicate(IReadOnlyList<(int Start, int End)> folds)
    {
        if (folds.Count == 0) return folds;

        var dict = new Dictionary<int, int>();
        foreach (var (start, end) in folds)
        {
            if (!dict.TryGetValue(start, out int existing) || end > existing)
                dict[start] = end;
        }

        return dict
            .OrderBy(kv => kv.Key)
            .Select(kv => (kv.Key, kv.Value))
            .ToList();
    }
}
