namespace Editor.Core.Lsp;

/// <summary>
/// Builds the breadcrumb path (outermost → innermost enclosing symbol) for a cursor
/// position from a set of <see cref="DocumentSymbol"/>s. Pure .NET so it can be shared
/// by the LSP path and the non-LSP fallback extractor, and unit-tested without WPF.
///
/// Works for both hierarchical trees and flat lists: it collects every symbol whose range
/// contains the cursor (recursing the whole tree) and orders them outermost-first by range
/// span. The naive "descend only into children of the first match" approach dropped the
/// inner symbol (e.g. the method) when a server returned a flat <c>SymbolInformation</c> list.
/// </summary>
public static class BreadcrumbBuilder
{
    public static IReadOnlyList<BreadcrumbSegment> GetSegments(
        IReadOnlyList<DocumentSymbol> symbols, int line, int col)
    {
        if (symbols.Count == 0) return [];
        var matches = new List<DocumentSymbol>();
        Collect(symbols, line, col, matches);
        matches.Sort((a, b) => RangeSpan(b.Range).CompareTo(RangeSpan(a.Range)));
        return matches
            .Select(s => new BreadcrumbSegment(s.Name, s.Kind,
                s.SelectionRange.Start.Line, s.SelectionRange.Start.Character))
            .ToList();
    }

    private static void Collect(IReadOnlyList<DocumentSymbol> symbols, int line, int col, List<DocumentSymbol> acc)
    {
        foreach (var sym in symbols)
        {
            if (Contains(sym.Range, line, col)) acc.Add(sym);
            if (sym.Children is { Length: > 0 })
                Collect(sym.Children, line, col, acc);
        }
    }

    private static bool Contains(LspRange range, int line, int col)
    {
        if (line < range.Start.Line || line > range.End.Line) return false;
        if (line == range.Start.Line && col < range.Start.Character) return false;
        if (line == range.End.Line && col > range.End.Character) return false;
        return true;
    }

    // Coarse range size (lines dominate, characters break ties) for outermost-first ordering.
    private static long RangeSpan(LspRange r)
        => ((long)(r.End.Line - r.Start.Line) << 20) + (r.End.Character - r.Start.Character);
}
