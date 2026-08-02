using Editor.Controls;
using Editor.Controls.Lsp;
using Editor.Core.Lsp;

namespace Editor.Controls.Tests;

public class CompletionBehaviorTests
{
    [Fact]
    public void Commit_character_is_accepted_only_when_declared_by_selected_item()
    {
        var item = new LspCompletionItem("WriteLine", CommitCharacters: [".", "("]);
        Assert.True(VimEditorControl.IsCommitCharacter(item, "."));
        Assert.True(VimEditorControl.IsCommitCharacter(item, "("));
        Assert.False(VimEditorControl.IsCommitCharacter(item, ";"));
        Assert.False(VimEditorControl.IsCommitCharacter(item, ".."));
    }

    [Fact]
    public void Text_edit_replaces_the_server_supplied_multiline_range_only()
    {
        var edit = new LspTextEdit(
            new(new(0, 6), new(1, 3)),
            "Value");

        var result = VimEditorControl.ApplyTextEdits("beforeOLD\nENDafter", [edit]);

        Assert.Equal("beforeValueafter", result);
    }

    [Fact]
    public void Preselected_item_is_kept_inside_the_initial_ten_row_window()
    {
        var items = Enumerable.Range(0, 25)
            .Select(i => new LspCompletionItem($"item{i}", Preselect: i == 17)).ToList();
        var selection = LspViewBridge.FindInitialSelection(items);
        var scroll = LspViewBridge.InitialScrollOffset(selection);

        Assert.Equal(17, selection);
        Assert.InRange(selection, scroll, scroll + 9);
    }
}
