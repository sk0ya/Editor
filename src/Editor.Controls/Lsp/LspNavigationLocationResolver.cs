using Editor.Core.Lsp;

namespace Editor.Controls.Lsp;

/// <summary>LSPの複数候補ナビゲーションを、表示可能なローカル位置へ正規化する。</summary>
internal static class LspNavigationLocationResolver
{
    public static IReadOnlyList<NavigableLspLocation> Resolve(
        IEnumerable<LspLocation> locations,
        Func<string, string> uriToPath,
        Func<string, bool> fileExists)
    {
        var result = new List<NavigableLspLocation>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var location in locations)
        {
            var path = uriToPath(location.Uri);
            if (string.IsNullOrWhiteSpace(path) || !fileExists(path)) continue;

            var candidate = new NavigableLspLocation(
                path,
                location.Range.Start.Line,
                location.Range.Start.Character);
            var key = $"{candidate.FilePath}\0{candidate.Line}\0{candidate.Column}";
            if (seen.Add(key)) result.Add(candidate);
        }
        return result;
    }
}

internal sealed record NavigableLspLocation(string FilePath, int Line, int Column);
