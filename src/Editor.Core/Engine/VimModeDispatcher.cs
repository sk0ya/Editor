using Editor.Core.Models;

namespace Editor.Core.Engine;

/// <summary>
/// Routes resolved keys to the handler for the current mode. Keeping mode
/// grouping here prevents input-pipeline concerns from leaking into handlers.
/// </summary>
internal sealed class VimModeDispatcher(
    Action<string, bool, bool, bool, List<VimEvent>> normal,
    Action<string, bool, bool, bool, List<VimEvent>> insert,
    Action<string, bool, bool, bool, List<VimEvent>> visual,
    Action<string, bool, bool, bool, List<VimEvent>> commandLine)
{
    public void Dispatch(
        VimMode mode,
        string key,
        bool ctrl,
        bool shift,
        bool alt,
        List<VimEvent> events)
    {
        var handler = mode switch
        {
            VimMode.Normal => normal,
            VimMode.Insert or VimMode.Replace => insert,
            VimMode.Visual or VimMode.VisualLine or VimMode.VisualBlock => visual,
            VimMode.Command or VimMode.SearchForward or VimMode.SearchBackward => commandLine,
            _ => null,
        };

        handler?.Invoke(key, ctrl, shift, alt, events);
    }
}
