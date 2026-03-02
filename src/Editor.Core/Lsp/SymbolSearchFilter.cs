namespace Editor.Core.Lsp;

public static class SymbolSearchFilter
{
    private static readonly HashSet<SymbolKind> _classKinds =
    [
        SymbolKind.Class, SymbolKind.Interface, SymbolKind.Enum,
        SymbolKind.Struct, SymbolKind.TypeParameter
    ];

    /// <summary>Returns true when the kind represents a type definition.</summary>
    public static bool IsClassKind(SymbolKind kind) => _classKinds.Contains(kind);

    /// <summary>
    /// When <paramref name="isClass"/> is true, keeps only Class/Interface/Enum/Struct/TypeParameter symbols.
    /// When false, returns all symbols unchanged.
    /// </summary>
    public static IReadOnlyList<LspSymbolInformation> FilterByKind(
        IReadOnlyList<LspSymbolInformation> symbols, bool isClass)
    {
        if (!isClass) return symbols;
        var result = new List<LspSymbolInformation>(symbols.Count);
        foreach (var s in symbols)
            if (_classKinds.Contains(s.Kind))
                result.Add(s);
        return result;
    }
}
