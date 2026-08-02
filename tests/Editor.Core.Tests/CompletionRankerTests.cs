using Editor.Core.Lsp;

namespace Editor.Core.Tests;

public class CompletionRankerTests
{
    [Fact]
    public void Rank_orders_prefix_then_word_boundary_then_substring()
    {
        var items = new[] { new LspCompletionItem("megaxalphaz"), new LspCompletionItem("MyAlpha"), new LspCompletionItem("alphaValue") };
        Assert.Equal(["alphaValue", "MyAlpha", "megaxalphaz"], CompletionRanker.Rank(items, "alpha").Select(x => x.Label));
    }

    [Fact]
    public void Rank_uses_sortText_then_server_order_for_ties()
    {
        var first = new LspCompletionItem("alphaZ", SortText: "02");
        var second = new LspCompletionItem("alphaA", SortText: "01");
        var third = new LspCompletionItem("alphaB", SortText: "01");
        Assert.Equal([second, third, first], CompletionRanker.Rank([first, second, third], "alpha"));
    }

    [Fact]
    public void Rank_uses_filterText_and_drops_non_matches()
    {
        var alias = new LspCompletionItem("Display", FilterText: "actualName");
        Assert.Equal([alias], CompletionRanker.Rank([new LspCompletionItem("Other"), alias], "actual"));
    }
}
