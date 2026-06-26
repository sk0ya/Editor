using Editor.Core.Buffer;
using Editor.Core.Models;

namespace Editor.Core.Editing;

/// <summary>
/// Immutable snapshot of the editing state handed to an <see cref="IEditAssist"/>.
/// Pure logic — no WPF. The <see cref="Buffer"/> is mutated in place by the assist;
/// the resulting caret position is returned via <see cref="EditResult"/>.
/// </summary>
public sealed class EditContext
{
    public TextBuffer Buffer { get; }
    public CursorPosition Cursor { get; }
    public string? FilePath { get; }
    public int ShiftWidth { get; }
    public bool ExpandTab { get; }

    public EditContext(TextBuffer buffer, CursorPosition cursor, string? filePath, int shiftWidth, bool expandTab)
    {
        Buffer = buffer;
        Cursor = cursor;
        FilePath = filePath;
        ShiftWidth = shiftWidth;
        ExpandTab = expandTab;
    }
}

/// <summary>Outcome of an edit-assist hook.</summary>
public readonly record struct EditResult(bool Handled, CursorPosition Cursor)
{
    /// <summary>The assist declined; the engine should fall back to its default behaviour.</summary>
    public static EditResult NotHandled => new(false, default);

    /// <summary>The assist handled the key and mutated the buffer; <paramref name="cursor"/> is the new caret.</summary>
    public static EditResult Done(CursorPosition cursor) => new(true, cursor);
}

/// <summary>
/// A filetype-aware editing aid. Implementations add smart behaviour for individual
/// keystrokes (Enter continuation, Tab indentation, …) without touching the engine.
/// Register new implementations with <see cref="EditAssistRegistry"/>.
/// </summary>
public interface IEditAssist
{
    /// <summary>True when this assist should be used for the given file.</summary>
    bool AppliesTo(string? filePath);

    /// <summary>
    /// Handle an Enter/newline keypress. Return <see cref="EditResult.NotHandled"/>
    /// to let the engine insert a plain newline.
    /// </summary>
    EditResult OnEnter(EditContext ctx);

    /// <summary>
    /// Handle a Tab (<paramref name="shift"/> = Shift+Tab) keypress. Return
    /// <see cref="EditResult.NotHandled"/> to let the engine insert a plain tab.
    /// </summary>
    EditResult OnTab(EditContext ctx, bool shift);

    /// <summary>
    /// Returns the prefix (indent + any marker) to seed a line opened with <c>o</c>/<c>O</c>,
    /// or <c>null</c> to fall back to plain auto-indent. <paramref name="above"/> is true for <c>O</c>.
    /// </summary>
    string? OpenLinePrefix(EditContext ctx, bool above);
}

/// <summary>Convenience base that declines every hook; override only what you need.</summary>
public abstract class EditAssistBase : IEditAssist
{
    public abstract bool AppliesTo(string? filePath);
    public virtual EditResult OnEnter(EditContext ctx) => EditResult.NotHandled;
    public virtual EditResult OnTab(EditContext ctx, bool shift) => EditResult.NotHandled;
    public virtual string? OpenLinePrefix(EditContext ctx, bool above) => null;
}
