using Editor.Controls.Lsp;
using Editor.Core.Lsp;

namespace Editor.Controls.Tests;

public sealed class LspNavigationLocationResolverTests
{
    [Fact]
    public void Resolve_filters_non_navigable_locations_and_deduplicates_preserving_order()
    {
        var locations = new[]
        {
            Location("file:///one.cs", 4, 2),
            Location("file:///ONE.cs", 4, 2),
            Location("file:///missing.cs", 1, 0),
            Location("file:///one.cs", 8, 1),
        };

        var result = LspNavigationLocationResolver.Resolve(
            locations,
            uri => uri["file:///".Length..],
            path => !string.Equals(path, "missing.cs", StringComparison.OrdinalIgnoreCase));

        Assert.Collection(result,
            first =>
            {
                Assert.Equal("one.cs", first.FilePath);
                Assert.Equal(4, first.Line);
                Assert.Equal(2, first.Column);
            },
            second =>
            {
                Assert.Equal("one.cs", second.FilePath);
                Assert.Equal(8, second.Line);
                Assert.Equal(1, second.Column);
            });
    }

    private static LspLocation Location(string uri, int line, int character)
        => new(uri, new LspRange(
            new LspPosition(line, character), new LspPosition(line, character + 1)));
}
