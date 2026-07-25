using Editor.Core.Buffer;
using Editor.Core.Engine;
using Editor.Core.Models;

namespace Editor.Core.Tests;

public class MotionServiceTests
{
    [Theory]
    [InlineData(MotionApplication.Normal)]
    [InlineData(MotionApplication.Visual)]
    [InlineData(MotionApplication.Operator)]
    public void Calculate_SameWordMotionUsesOneTarget(MotionApplication application)
    {
        var buffers = new BufferManager();
        buffers.Current.Text.SetText("one two");
        var service = new MotionService(buffers);

        var result = service.Calculate(new MotionRequest(
            "e", CursorPosition.Zero, Application: application));

        Assert.Equal(new CursorPosition(0, 2), result!.Target);
        Assert.Equal(MotionShape.Characterwise, result.Shape);
    }

    [Fact]
    public void Calculate_OperatorChangeWordUsesEndMotion()
    {
        var buffers = new BufferManager();
        buffers.Current.Text.SetText("one two");
        var service = new MotionService(buffers);

        var result = service.Calculate(new MotionRequest(
            "w",
            CursorPosition.Zero,
            Application: MotionApplication.Operator,
            Operator: "c"));

        Assert.Equal(new CursorPosition(0, 2), result!.Target);
    }

    [Fact]
    public void Calculate_ReturnsApplicationMetadata()
    {
        var buffers = new BufferManager();
        buffers.Current.Text.SetText("one\ntwo");
        var service = new MotionService(buffers);

        var result = service.Calculate(new MotionRequest(
            "G", CursorPosition.Zero));

        Assert.Equal(MotionShape.Linewise, result!.Shape);
        Assert.True(result.AddToJumpList);
        Assert.True(result.UpdateStickyColumn);
    }
}
