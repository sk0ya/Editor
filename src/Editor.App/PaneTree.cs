using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Editor.Controls;

namespace Editor.App;

// ─────────── Pane tree ──────────────────────────────────────
// The binary tree of editor/split/preview panes backing a tab's content area.
// Shared by window-split management (MainWindow) and the markdown-preview
// pane-splicing logic (MarkdownPreviewController).

internal abstract class PaneNode
{
    public abstract IEnumerable<VimEditorControl> AllEditors();
}

internal sealed class EditorPaneNode : PaneNode
{
    public required VimEditorControl Editor { get; init; }
    public override IEnumerable<VimEditorControl> AllEditors() { yield return Editor; }
}

internal sealed class PreviewPaneNode : PaneNode
{
    public required Grid Panel { get; init; }
    public override IEnumerable<VimEditorControl> AllEditors() => [];
}

internal sealed class SplitPaneNode : PaneNode
{
    public bool Vertical { get; init; }
    public required PaneNode First { get; set; }
    public required PaneNode Second { get; set; }
    public required Grid Container { get; set; }

    public override IEnumerable<VimEditorControl> AllEditors()
        => First.AllEditors().Concat(Second.AllEditors());

    public static Grid BuildGrid(bool vertical, UIElement first, UIElement second)
    {
        var grid = new Grid();
        var splitter = new GridSplitter
        {
            Background = new SolidColorBrush(Color.FromRgb(0x44, 0x47, 0x5A)),
            ResizeBehavior = GridResizeBehavior.PreviousAndNext,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        if (vertical)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            splitter.Width = 4;
            Grid.SetColumn(first, 0); Grid.SetColumn(splitter, 1); Grid.SetColumn(second, 2);
        }
        else
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(4) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            splitter.Height = 4;
            Grid.SetRow(first, 0); Grid.SetRow(splitter, 1); Grid.SetRow(second, 2);
        }
        grid.Children.Add(first);
        grid.Children.Add(splitter);
        grid.Children.Add(second);
        return grid;
    }

    public void ReplaceChild(UIElement oldChild, UIElement newChild)
    {
        int idx = Container.Children.IndexOf(oldChild);
        if (idx < 0) return;
        if (Vertical) Grid.SetColumn(newChild, Grid.GetColumn(oldChild));
        else          Grid.SetRow(newChild, Grid.GetRow(oldChild));
        Container.Children.RemoveAt(idx);
        Container.Children.Insert(idx, newChild);
    }
}

internal static class PaneTreeHelpers
{
    public static UIElement PaneToElement(PaneNode node) => node switch
    {
        EditorPaneNode e => e.Editor,
        PreviewPaneNode p => p.Panel,
        SplitPaneNode s  => s.Container,
        _ => throw new InvalidOperationException()
    };

    public static SplitPaneNode? FindParentSplit(PaneNode root, PaneNode target)
    {
        if (root is SplitPaneNode spn)
        {
            if (spn.First == target || spn.Second == target) return spn;
            return FindParentSplit(spn.First, target) ?? FindParentSplit(spn.Second, target);
        }
        return null;
    }

    public static EditorPaneNode? FindEditorPane(PaneNode root, VimEditorControl editor)
    {
        if (root is EditorPaneNode epn && epn.Editor == editor) return epn;
        if (root is SplitPaneNode spn)
            return FindEditorPane(spn.First, editor) ?? FindEditorPane(spn.Second, editor);
        return null;
    }
}
