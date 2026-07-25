using Editor.Core.Models;

namespace Editor.Core.Engine;

public readonly record struct ModeKeyInput(
    string Key,
    bool Ctrl = false,
    bool Shift = false,
    bool Alt = false);

public sealed record ModeTransition(
    VimMode Target,
    bool SuppressInsertAutocmd = false);

public sealed record ModeControllerResult(ModeTransition? Transition = null)
{
    public static ModeControllerResult Handled { get; } = new();
    public static ModeControllerResult TransitionTo(
        VimMode target,
        bool suppressInsertAutocmd = false) =>
        new(new ModeTransition(target, suppressInsertAutocmd));
}

public interface IModeController
{
    IReadOnlySet<VimMode> Modes { get; }
    ModeControllerResult Handle(ModeKeyInput input, List<VimEvent> events);
}

public abstract class ModeControllerBase(
    IEnumerable<VimMode> modes,
    Func<ModeKeyInput, List<VimEvent>, ModeControllerResult> handler) : IModeController
{
    public IReadOnlySet<VimMode> Modes { get; } = new HashSet<VimMode>(modes);

    public ModeControllerResult Handle(ModeKeyInput input, List<VimEvent> events) =>
        handler(input, events);
}

public sealed class NormalModeController(
    Func<ModeKeyInput, List<VimEvent>, ModeControllerResult> handler)
    : ModeControllerBase([VimMode.Normal], handler);

public sealed class InsertModeController(
    Func<ModeKeyInput, List<VimEvent>, ModeControllerResult> handler)
    : ModeControllerBase([VimMode.Insert], handler);

public sealed class ReplaceModeController(
    Func<ModeKeyInput, List<VimEvent>, ModeControllerResult> handler)
    : ModeControllerBase([VimMode.Replace], handler);

public sealed class VisualModeController(
    Func<ModeKeyInput, List<VimEvent>, ModeControllerResult> handler)
    : ModeControllerBase(
        [VimMode.Visual, VimMode.VisualLine, VimMode.VisualBlock],
        handler);

public sealed class CommandLineController(
    Func<ModeKeyInput, List<VimEvent>, ModeControllerResult> handler)
    : ModeControllerBase(
        [VimMode.Command, VimMode.SearchForward, VimMode.SearchBackward],
        handler);

public sealed class PlainEditController(
    Func<ModeKeyInput, List<VimEvent>, ModeControllerResult> handler)
    : ModeControllerBase([], handler);

/// <summary>
/// Routes input to one independent mode controller and owns requested transitions.
/// Controllers never reference one another.
/// </summary>
public sealed class ModeCoordinator
{
    private readonly Dictionary<VimMode, IModeController> _controllers;
    private readonly PlainEditController _plain;
    private readonly Action<ModeTransition, List<VimEvent>> _transition;

    public ModeCoordinator(
        IEnumerable<IModeController> controllers,
        PlainEditController plain,
        Action<ModeTransition, List<VimEvent>> transition)
    {
        ArgumentNullException.ThrowIfNull(controllers);
        _plain = plain ?? throw new ArgumentNullException(nameof(plain));
        _transition = transition ?? throw new ArgumentNullException(nameof(transition));
        _controllers = [];
        foreach (var controller in controllers)
            foreach (var mode in controller.Modes)
                if (!_controllers.TryAdd(mode, controller))
                    throw new InvalidOperationException(
                        $"Mode '{mode}' has more than one controller.");
    }

    public void Dispatch(
        VimMode mode,
        ModeKeyInput input,
        List<VimEvent> events) =>
        Apply(_controllers.TryGetValue(mode, out var controller)
            ? controller.Handle(input, events)
            : ModeControllerResult.Handled, events);

    public void DispatchPlain(ModeKeyInput input, List<VimEvent> events) =>
        Apply(_plain.Handle(input, events), events);

    public void TransitionTo(
        VimMode target,
        List<VimEvent> events,
        bool suppressInsertAutocmd = false) =>
        _transition(new ModeTransition(target, suppressInsertAutocmd), events);

    private void Apply(ModeControllerResult result, List<VimEvent> events)
    {
        if (result.Transition is { } transition)
            _transition(transition, events);
    }
}
