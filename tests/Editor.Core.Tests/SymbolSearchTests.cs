using Editor.Core.Lsp;

namespace Editor.Core.Tests;

public class SymbolSearchTests
{
    private static LspSymbolInformation MakeSymbol(string name, SymbolKind kind, string? container = null)
        => new(name, kind,
               new LspLocation("file:///test.cs",
                   new LspRange(new LspPosition(0, 0), new LspPosition(0, 0))),
               container);

    // ── FilterByKind ────────────────────────────────────────────────────────

    [Fact]
    public void FilterByKind_IsClassFalse_ReturnsAllSymbols()
    {
        var symbols = new[]
        {
            MakeSymbol("MyClass",  SymbolKind.Class),
            MakeSymbol("myMethod", SymbolKind.Method),
            MakeSymbol("myVar",    SymbolKind.Variable),
        };
        var result = SymbolSearchFilter.FilterByKind(symbols, isClass: false);
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void FilterByKind_IsClassTrue_ReturnsOnlyTypeKinds()
    {
        var symbols = new[]
        {
            MakeSymbol("MyClass",     SymbolKind.Class),
            MakeSymbol("MyInterface", SymbolKind.Interface),
            MakeSymbol("MyEnum",      SymbolKind.Enum),
            MakeSymbol("MyStruct",    SymbolKind.Struct),
            MakeSymbol("T",           SymbolKind.TypeParameter),
            MakeSymbol("myMethod",    SymbolKind.Method),
            MakeSymbol("myVar",       SymbolKind.Variable),
            MakeSymbol("myField",     SymbolKind.Field),
        };
        var result = SymbolSearchFilter.FilterByKind(symbols, isClass: true);
        Assert.Equal(5, result.Count);
        Assert.All(result, s => Assert.True(SymbolSearchFilter.IsClassKind(s.Kind)));
    }

    [Fact]
    public void FilterByKind_EmptyInput_ReturnsEmpty()
    {
        var result = SymbolSearchFilter.FilterByKind([], isClass: true);
        Assert.Empty(result);
    }

    [Fact]
    public void FilterByKind_NoTypeSymbols_ReturnsEmptyWhenClassFilter()
    {
        var symbols = new[]
        {
            MakeSymbol("myMethod", SymbolKind.Method),
            MakeSymbol("myVar",    SymbolKind.Variable),
            MakeSymbol("myConst",  SymbolKind.Constant),
        };
        var result = SymbolSearchFilter.FilterByKind(symbols, isClass: true);
        Assert.Empty(result);
    }

    [Fact]
    public void FilterByKind_PreservesOrder()
    {
        var symbols = new[]
        {
            MakeSymbol("AClass",    SymbolKind.Class),
            MakeSymbol("aMethod",   SymbolKind.Method),
            MakeSymbol("BInterface",SymbolKind.Interface),
            MakeSymbol("bVar",      SymbolKind.Variable),
            MakeSymbol("CEnum",     SymbolKind.Enum),
        };
        var result = SymbolSearchFilter.FilterByKind(symbols, isClass: true);
        Assert.Equal(3, result.Count);
        Assert.Equal("AClass",     result[0].Name);
        Assert.Equal("BInterface", result[1].Name);
        Assert.Equal("CEnum",      result[2].Name);
    }

    [Fact]
    public void FilterByKind_IsClassFalse_ReturnsOriginalInstance()
    {
        IReadOnlyList<LspSymbolInformation> symbols = [MakeSymbol("X", SymbolKind.Class)];
        var result = SymbolSearchFilter.FilterByKind(symbols, isClass: false);
        Assert.Same(symbols, result);
    }

    // ── IsClassKind ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(SymbolKind.Class)]
    [InlineData(SymbolKind.Interface)]
    [InlineData(SymbolKind.Enum)]
    [InlineData(SymbolKind.Struct)]
    [InlineData(SymbolKind.TypeParameter)]
    public void IsClassKind_TypeKinds_ReturnsTrue(SymbolKind kind)
    {
        Assert.True(SymbolSearchFilter.IsClassKind(kind));
    }

    [Theory]
    [InlineData(SymbolKind.Method)]
    [InlineData(SymbolKind.Function)]
    [InlineData(SymbolKind.Variable)]
    [InlineData(SymbolKind.Field)]
    [InlineData(SymbolKind.Property)]
    [InlineData(SymbolKind.Constructor)]
    [InlineData(SymbolKind.Constant)]
    [InlineData(SymbolKind.Namespace)]
    [InlineData(SymbolKind.Module)]
    [InlineData(SymbolKind.EnumMember)]
    public void IsClassKind_NonTypeKinds_ReturnsFalse(SymbolKind kind)
    {
        Assert.False(SymbolSearchFilter.IsClassKind(kind));
    }

    // ── ContainerName preserved ─────────────────────────────────────────────

    [Fact]
    public void FilterByKind_PreservesContainerName()
    {
        var symbols = new[]
        {
            MakeSymbol("Inner", SymbolKind.Class, container: "Outer"),
        };
        var result = SymbolSearchFilter.FilterByKind(symbols, isClass: true);
        Assert.Single(result);
        Assert.Equal("Outer", result[0].ContainerName);
    }
}
