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
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) throw failure;
    }
}
