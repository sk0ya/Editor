using System.Windows.Input;

namespace Editor.Controls;

/// <summary>
/// Shared "popup open: Down/Up move selection, Tab/Return apply, Escape dismiss" key
/// dispatch used by <see cref="VimEditorControl"/> for its LSP completion, filesystem
/// path completion, and code-action popups. Each popup wires up its own delegates
/// (<paramref name="move"/>/<paramref name="apply"/>/<paramref name="hide"/> in the
/// constructor) — including whatever side effects that popup's Apply/Hide requires
/// (e.g. clearing the flag that suppresses a duplicate IME echo, or exiting Insert
/// mode) — so behaviour per popup is unchanged; only the key-matching is shared.
/// </summary>
internal sealed class PopupKeyNavigator(
    Action<int> move,
    Action apply,
    Action hide,
    bool acceptCtrlNav = true,
    bool acceptJK = false,
    bool acceptTab = true)
{
    /// <summary>
    /// Attempts to interpret <paramref name="key"/> as a popup navigation key.
    /// Returns true (and invokes the matching delegate) if it was one of the
    /// popup's move/apply/hide keys; false if the caller should fall through.
    /// </summary>
    public bool TryHandle(Key key, bool ctrl)
    {
        if (key == Key.Down || (acceptJK && key == Key.J) || (acceptCtrlNav && ctrl && key == Key.N))
        {
            move(1);
            return true;
        }
        if (key == Key.Up || (acceptJK && key == Key.K) || (acceptCtrlNav && ctrl && key == Key.P))
        {
            move(-1);
            return true;
        }
        if (key == Key.Return || (acceptTab && key == Key.Tab))
        {
            apply();
            return true;
        }
        if (key == Key.Escape)
        {
            hide();
            return true;
        }
        return false;
    }
}
