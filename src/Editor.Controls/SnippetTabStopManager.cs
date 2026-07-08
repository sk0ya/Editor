using Editor.Core.Engine;
using Editor.Core.Models;
using Editor.Core.Snippets;

namespace Editor.Controls;

/// <summary>
/// Owns snippet tab-stop navigation state (Tab/Shift+Tab cycling through `$1`/`$2`/.../`$0`
/// placeholders) for a single <see cref="VimEditorControl"/>. Composed the same way as
/// <c>MultiCursorManager</c>: the control owns an instance and passes the WPF-side callbacks
/// (key replay, selection-range reset, event processing, canvas refresh) it needs to reuse the
/// control's existing insertion/cursor machinery instead of duplicating it.
/// </summary>
public sealed class SnippetTabStopManager(
    VimEngine engine,
    Action<string, bool, bool, bool> processKey,
    Action clearSelectionRangeState,
    Action<IReadOnlyList<VimEvent>> processVimEvents,
    Action updateAll)
{
    private SnippetTabStop[]? _tabStops;
    private int _index = -1;

    /// <summary>Advance to the next tab stop (Tab). Returns false if there is no active snippet or no next stop.</summary>
    public bool TryAdvance()
    {
        if (_tabStops == null || _index >= _tabStops.Length - 1) return false;
        _index++;
        JumpToCurrent();
        return true;
    }

    /// <summary>Go back to the previous tab stop (Shift+Tab). Returns false if there is no active snippet or no previous stop.</summary>
    public bool TryGoBack()
    {
        if (_tabStops == null || _index <= 0) return false;
        _index--;
        JumpToCurrent();
        return true;
    }

    public void Clear()
    {
        _tabStops = null;
        _index = -1;
    }

    /// <summary>
    /// Check whether the word immediately before the cursor is a snippet trigger.
    /// If so, delete the trigger, insert the expanded snippet, and arm tab-stop navigation.
    /// Returns true if a snippet was expanded.
    /// </summary>
    public bool TryExpandTriggerAtCursor()
    {
        var cursor = engine.Cursor;
        var lineText = engine.CurrentBuffer.Text.GetLine(cursor.Line);
        int col = cursor.Column;

        // Walk back over word characters to find the trigger
        int triggerStart = col;
        while (triggerStart > 0 && (char.IsLetterOrDigit(lineText[triggerStart - 1]) || lineText[triggerStart - 1] == '_'))
            triggerStart--;

        if (triggerStart >= col) return false; // no word before cursor

        var trigger = lineText[triggerStart..col];
        var ext = System.IO.Path.GetExtension(engine.CurrentBuffer.FilePath ?? "").ToLowerInvariant();

        if (!engine.Config.Snippets.TryGet(trigger, ext, out var body))
            return false;

        // Delete the trigger word
        for (int i = 0; i < trigger.Length; i++)
            processKey("Back", false, false, false);

        // Insert the expanded snippet
        var insertCursor = engine.Cursor;
        var expansion = SnippetManager.Expand(
            body,
            insertCursor.Line, insertCursor.Column,
            engine.Options.TabStop, engine.Options.ExpandTab);

        Apply(expansion);
        return true;
    }

    /// <summary>
    /// Insert the expanded snippet text into the buffer and arm tab-stop navigation.
    /// </summary>
    public void Apply(SnippetExpansion expansion)
    {
        // Insert each line of the snippet
        for (int li = 0; li < expansion.Lines.Length; li++)
        {
            var snippetLine = expansion.Lines[li];
            foreach (var ch in snippetLine)
                processKey(ch.ToString(), false, false, false);
            if (li < expansion.Lines.Length - 1)
                processKey("Return", false, false, false);
        }

        // Arm tab stops (skip if there's only $0 — nothing to navigate)
        if (expansion.TabStops.Count > 1)
        {
            _tabStops = [.. expansion.TabStops];
            _index = 0;
            JumpToCurrent();
        }
        else if (expansion.TabStops.Count == 1)
        {
            // Just jump to $0 (end position)
            _tabStops = null;
            _index = -1;
            var stop = expansion.TabStops[0];
            clearSelectionRangeState();
            var events = engine.SetCursorPosition(new CursorPosition(stop.Line, stop.Column));
            processVimEvents(events);
        }
    }

    /// <summary>Move the cursor to the current tab stop index.</summary>
    private void JumpToCurrent()
    {
        if (_tabStops == null || _index < 0 || _index >= _tabStops.Length) return;

        var stop = _tabStops[_index];

        // If this is the last tab stop ($0), clear snippet state after jump
        bool isLast = _index == _tabStops.Length - 1;

        clearSelectionRangeState();
        var events = engine.SetCursorPosition(new CursorPosition(stop.Line, stop.Column));
        processVimEvents(events);
        updateAll();

        if (isLast) Clear();
    }
}
