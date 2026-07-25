using Editor.Core.Engine;
using Editor.Core.Models;

namespace Editor.Core.Tests;

public class ModeCoordinatorTests
{
    [Fact]
    public void Dispatch_OnlyInvokesControllerForCurrentMode()
    {
        var normalCalls = 0;
        var visualCalls = 0;
        var coordinator = CreateCoordinator(
            new NormalModeController((_, _) =>
            {
                normalCalls++;
                return ModeControllerResult.Handled;
            }),
            new VisualModeController((_, _) =>
            {
                visualCalls++;
                return ModeControllerResult.Handled;
            }));

        coordinator.Dispatch(VimMode.Visual, new ModeKeyInput("x"), []);

        Assert.Equal(0, normalCalls);
        Assert.Equal(1, visualCalls);
    }

    [Fact]
    public void Dispatch_AppliesControllerTransitionRequest()
    {
        ModeTransition? applied = null;
        var controller = new NormalModeController((_, _) =>
            ModeControllerResult.TransitionTo(VimMode.Insert));
        var coordinator = new ModeCoordinator(
            [controller],
            new PlainEditController((_, _) => ModeControllerResult.Handled),
            (transition, _) => applied = transition);

        coordinator.Dispatch(VimMode.Normal, new ModeKeyInput("i"), []);

        Assert.Equal(VimMode.Insert, applied!.Target);
    }

    [Fact]
    public void Constructor_RejectsControllersForSameMode()
    {
        var first = new NormalModeController((_, _) => ModeControllerResult.Handled);
        var second = new NormalModeController((_, _) => ModeControllerResult.Handled);

        Assert.Throws<InvalidOperationException>(() =>
            CreateCoordinator(first, second));
    }

    [Fact]
    public void PlainController_IsDispatchedIndependently()
    {
        var received = "";
        var coordinator = new ModeCoordinator(
            [],
            new PlainEditController((input, _) =>
            {
                received = input.Key;
                return ModeControllerResult.Handled;
            }),
            (_, _) => { });

        coordinator.DispatchPlain(new ModeKeyInput("a"), []);

        Assert.Equal("a", received);
    }

    private static ModeCoordinator CreateCoordinator(params IModeController[] controllers) =>
        new(
            controllers,
            new PlainEditController((_, _) => ModeControllerResult.Handled),
            (_, _) => { });
}
