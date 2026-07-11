using Editor.Controls.Rendering;

namespace Editor.Controls.Tests;

public class EditorCanvasVerticalColumnTests
{
    [Fact]
    public void ResolveVerticalColumn_UsesRenderedWidthForProportionalFont()
    {
        RunSta(() =>
        {
            var canvas = new EditorCanvas();
            canvas.UpdateFont("Segoe UI", 14);
            canvas.SetLines(["WW", "iiiiiiii"]);

            int column = canvas.ResolveVerticalColumn(0, 1, 1, 7);

            Assert.True(column > 1, $"Expected visual-column mapping past logical column 1, got {column}.");
        });
    }

    [Fact]
    public void ResolveVerticalColumn_GoalXInsideEmojiCluster_SnapsToClusterBoundaryNotMidCluster()
    {
        RunSta(() =>
        {
            // "a" + 🖼️ (U+1F5BC + U+FE0F variation selector, 3 UTF-16 units) + " b"
            string pic = char.ConvertFromUtf32(0x1F5BC) + "️";
            string line = "a" + pic + " b";
            var canvas = new EditorCanvas();
            canvas.UpdateFont("Consolas", 14);
            canvas.SetLines([line, ""]);

            // goalX comes from column 4 (the space right after the emoji cluster) on the
            // same line — this is what happens after j onto a blank line and back with k,
            // since the resolver maps the goal X against whichever line it's given.
            int column = canvas.ResolveVerticalColumn(0, 4, 0, line.Length - 1);

            // Must land on a real cluster boundary (1 = cluster start, 4 = right after it),
            // never inside the cluster (2 or 3), which measured zero cursor width and made
            // the block cursor invisible.
            Assert.True(column is 4, $"Expected column 4 (the space, right after the cluster), got {column}.");
        });
    }

    [Fact]
    public void ResolveVerticalColumn_IncludesMarkdownTableAlignmentOverrides()
    {
        RunSta(() =>
        {
            var canvas = new EditorCanvas();
            canvas.UpdateFont("Segoe UI", 14);
            canvas.SetLines(["| A | B |", "| --- | --- |", "| longer | B |"]);
            canvas.MarkdownTableAlignEnabled = true;

            int column = canvas.ResolveVerticalColumn(0, 4, 2, 13);

            Assert.Equal(9, column); // the second pipe on the visually aligned target row
        });
    }

    private static void RunSta(Action action)
        => WpfTestHost.Run(action);
}
