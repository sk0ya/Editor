using System.Windows.Automation.Provider;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Text;
using Editor.Controls.Rendering;
using Editor.Core.Models;

namespace Editor.Controls.Tests;

public class EditorCanvasAutomationTests
{
    [Fact]
    public void AutomationTextAndOffsets_RoundTripAcrossLines()
    {
        RunSta(() =>
        {
        var canvas = new EditorCanvas();
        canvas.SetLines(["abc", "日本語", ""]);

        Assert.Equal("abc\n日本語\n", canvas.AutomationText);
        Assert.Equal(5, canvas.AutomationOffset(new CursorPosition(1, 1)));
        Assert.Equal(new CursorPosition(1, 1), canvas.AutomationPosition(5));
        Assert.Equal(new CursorPosition(2, 0), canvas.AutomationPosition(canvas.AutomationText.Length));
        });
    }

    [Fact]
    public void TextProvider_ExposesDocumentCaretAndSelection()
    {
        RunSta(() =>
        {
        var canvas = new TestCanvas();
        canvas.SetLines(["one", "two"]);
        canvas.SetCursor(new CursorPosition(1, 1));
        var peer = Assert.IsAssignableFrom<ITextProvider>(canvas.CreatePeer());

        Assert.Equal("one\ntwo", peer.DocumentRange.GetText(-1));
        Assert.Equal(string.Empty, Assert.Single(peer.GetSelection()).GetText(-1));

        canvas.SetSelection(new Selection(new(0, 1), new(1, 2), SelectionType.Character));
        Assert.Equal("ne\ntw", Assert.Single(peer.GetSelection()).GetText(-1));
        });
    }

    [Fact]
    public void RangeProvider_ObservesSingleSelectionAndMovementContracts()
    {
        RunSta(() =>
        {
            var canvas = new TestCanvas(); canvas.SetLines(["one", "two"]); canvas.SetCursor(new(0, 2));
            var provider = (ITextProvider)canvas.CreatePeer(); var range = provider.DocumentRange;
            Assert.Equal(1, range.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, 1));
            Assert.Equal(6, range.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, 99));
            Assert.Equal(0, range.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, 1));
            Assert.Throws<InvalidOperationException>(range.AddToSelection);
            var other = (ITextProvider)new TestCanvas().CreatePeer();
            Assert.Throws<ArgumentException>(() => range.CompareEndpoints(TextPatternRangeEndpoint.Start, other.DocumentRange, TextPatternRangeEndpoint.Start));
            var before = canvas.AutomationSelectionOffsets; range.ScrollIntoView(true); Assert.Equal(before, canvas.AutomationSelectionOffsets);
            range.Select(); range.RemoveFromSelection(); Assert.Equal(canvas.AutomationSelectionOffsets.Start, canvas.AutomationSelectionOffsets.End);
        });
    }

    [Fact]
    public void BoundingRectangles_AreScreenCoordinatesForVisibleText()
    {
        RunSta(() =>
        {
            var canvas = new TestCanvas { Width = 400, Height = 200 }; canvas.SetLines(["abc"]);
            var rects = ((ITextProvider)canvas.CreatePeer()).DocumentRange.GetBoundingRectangles(); Assert.Equal(4, rects.Length); Assert.True(rects[2] > 0); Assert.True(rects[3] > 0);
        });
    }

    private sealed class TestCanvas : EditorCanvas
    {
        public AutomationPeer CreatePeer() => OnCreateAutomationPeer();
    }

    private static void RunSta(Action action)
        => WpfTestHost.Run(action);
}
