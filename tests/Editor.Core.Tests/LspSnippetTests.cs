using Editor.Core.Snippets;

namespace Editor.Core.Tests;

public class LspSnippetTests
{
    [Fact]
    public void Same_number_placeholder_reuses_the_first_default_text()
    {
        var expansion = SnippetManager.ExpandLsp("var ${1:name} = $1;$0", 0, 0, 4, true);

        Assert.Equal("var name = name;", Assert.Single(expansion.Lines));
        Assert.Equal([1, 0], expansion.TabStops.Select(x => x.Index));
        Assert.Equal(4, expansion.TabStops[0].Column);
        Assert.Equal(4, expansion.TabStops[0].Length);
    }

    [Fact]
    public void Unsupported_variables_degrade_to_default_or_plain_empty_text()
    {
        var expansion = SnippetManager.ExpandLsp("${TM_FILENAME_BASE:File}-$UNKNOWN-$0", 0, 0, 4, true);
        Assert.Equal("File--", Assert.Single(expansion.Lines));
    }
}
