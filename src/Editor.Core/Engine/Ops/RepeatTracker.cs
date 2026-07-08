using Editor.Core.Macros;
using Editor.Core.Models;
using Editor.Core.Registers;

namespace Editor.Core.Engine.Ops;

/// <summary>
/// Owns the dot-repeat (<c>.</c>) state machine: the last completed change plus the
/// in-progress insert/visual sequences being accumulated towards one. Takes the
/// handful of VimEngine re-entry points it needs to replay a change as callbacks
/// (same shape as the Editor.Controls managers, e.g. <c>MultiCursorManager</c>).
/// </summary>
public sealed class RepeatTracker(
    RegisterManager registerManager,
    Action<ParsedCommand, List<VimEvent>> executeNormalCommand,
    Action<string, bool, bool, bool, List<VimEvent>> processKeyInternal,
    Action<VimKeyStroke, List<VimEvent>, bool> processStroke)
{
    private sealed record RepeatChange(
        ParsedCommand? Command,
        IReadOnlyList<VimKeyStroke> Keys,
        IReadOnlyList<VimKeyStroke> InsertKeys,
        (char Name, Register Value)? RegisterSnapshot);

    private RepeatChange? _lastRepeatChange;
    private ParsedCommand? _pendingInsertRepeatCommand;
    private List<VimKeyStroke>? _pendingInsertRepeatKeys;
    private List<VimKeyStroke>? _pendingVisualRepeatKeys;

    public bool IsDotReplaying { get; private set; }
    public (char Name, Register Value)? PendingVisualRepeatRegister { get; set; }

    public void RepeatLastChange(int count, List<VimEvent> events)
    {
        if (_lastRepeatChange == null) return;

        IsDotReplaying = true;
        try
        {
            for (int i = 0; i < count; i++)
            {
                if (_lastRepeatChange.RegisterSnapshot is { } snapshot)
                    registerManager.Set(snapshot.Name, snapshot.Value);

                if (_lastRepeatChange.Command.HasValue)
                {
                    executeNormalCommand(_lastRepeatChange.Command.Value, events);
                    foreach (var stroke in _lastRepeatChange.InsertKeys)
                        processKeyInternal(stroke.Key, stroke.Ctrl, stroke.Shift, stroke.Alt, events);
                }
                else
                {
                    foreach (var stroke in _lastRepeatChange.Keys)
                        processStroke(stroke, events, false);
                }
            }
        }
        finally
        {
            IsDotReplaying = false;
        }
    }

    public void SetRepeatChange(ParsedCommand cmd)
    {
        if (IsDotReplaying) return;
        _lastRepeatChange = new RepeatChange(cmd, [], [], null);
        _pendingInsertRepeatCommand = null;
        _pendingInsertRepeatKeys = null;
        _pendingVisualRepeatKeys = null;
        PendingVisualRepeatRegister = null;
    }

    public void BeginInsertRepeat(ParsedCommand cmd)
    {
        if (IsDotReplaying) return;
        _pendingInsertRepeatCommand = cmd;
        _pendingInsertRepeatKeys = [];
    }

    public void TrackPendingInsertRepeat(VimKeyStroke stroke, VimMode modeBefore, VimMode currentMode)
    {
        if (IsDotReplaying || _pendingInsertRepeatKeys == null) return;
        if (modeBefore is not (VimMode.Insert or VimMode.Replace)) return;

        _pendingInsertRepeatKeys.Add(stroke);

        if (currentMode is VimMode.Insert or VimMode.Replace) return;
        if (_pendingInsertRepeatCommand == null) return;

        _lastRepeatChange = new RepeatChange(_pendingInsertRepeatCommand.Value, [], [.. _pendingInsertRepeatKeys], null);
        _pendingInsertRepeatCommand = null;
        _pendingInsertRepeatKeys = null;
    }

    public void TrackPendingVisualRepeat(VimKeyStroke stroke, VimMode modeBefore, VimMode currentMode, List<VimEvent> events)
    {
        if (IsDotReplaying) return;

        var wasVisual = IsVisualMode(modeBefore);
        var isVisual = IsVisualMode(currentMode);
        var wasInsert = modeBefore is VimMode.Insert or VimMode.Replace;
        var isInsert = currentMode is VimMode.Insert or VimMode.Replace;

        if (_pendingVisualRepeatKeys == null)
        {
            if (modeBefore == VimMode.Normal && isVisual)
                _pendingVisualRepeatKeys = [stroke];
            return;
        }

        _pendingVisualRepeatKeys.Add(stroke);

        if (isVisual || isInsert || IsCommandLineMode(currentMode))
            return;

        if (currentMode == VimMode.Normal)
        {
            if (wasInsert || (wasVisual && events.Any(e => e.Type == VimEventType.TextChanged)) ||
                (IsCommandLineMode(modeBefore) && events.Any(e => e.Type == VimEventType.TextChanged)))
            {
                SetRepeatChange([.. _pendingVisualRepeatKeys]);
            }
            else if (wasVisual || IsCommandLineMode(modeBefore))
            {
                _pendingVisualRepeatKeys = null;
                PendingVisualRepeatRegister = null;
            }

            return;
        }

        _pendingVisualRepeatKeys = null;
        PendingVisualRepeatRegister = null;
    }

    private static bool IsVisualMode(VimMode mode) =>
        mode is VimMode.Visual or VimMode.VisualLine or VimMode.VisualBlock;

    private static bool IsCommandLineMode(VimMode mode) =>
        mode is VimMode.Command or VimMode.SearchForward or VimMode.SearchBackward;

    public void SetRepeatChange(IReadOnlyList<VimKeyStroke> keys)
    {
        if (IsDotReplaying) return;
        _lastRepeatChange = new RepeatChange(null, keys, [], PendingVisualRepeatRegister);
        _pendingInsertRepeatCommand = null;
        _pendingInsertRepeatKeys = null;
        _pendingVisualRepeatKeys = null;
        PendingVisualRepeatRegister = null;
    }
}
