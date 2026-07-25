using Editor.Core.Buffer;
using Editor.Core.Engine;
using Editor.Core.Extensibility;
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

    [Fact]
    public void Calculate_AllowsDisplayAwareOverride()
    {
        var buffers = new BufferManager();
        buffers.Current.Text.SetText("one\ntwo");
        var service = new MotionService(buffers, [new DisplayLineOverride()]);

        var result = service.Calculate(new MotionRequest(
            "gj", CursorPosition.Zero, Application: MotionApplication.Visual));

        Assert.Equal(new CursorPosition(0, 2), result!.Target);
    }

    private sealed class DisplayLineOverride : IMotionOverride
    {
        public bool TryCalculate(
            MotionRequest request,
            INormalBufferView buffer,
            out MotionResult? result)
        {
            if (request.Motion != "gj")
            {
                result = null;
                return false;
            }

            result = new MotionResult(
                request.Start,
                request.Start with { Column = buffer.GetLineLength(request.Start.Line) - 1 },
                MotionType.Exclusive,
                MotionShape.Characterwise,
                AddToJumpList: false,
                UpdateStickyColumn: false);
            return true;
        }
    }
}
