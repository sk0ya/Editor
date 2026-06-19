namespace Editor.Controls.Ime;

/// <summary>
/// Bridge from <see cref="EditorTextStore"/> back to the editor control. Keeps the
/// text store free of WPF / VimEngine dependencies and makes the integration points
/// explicit. All members are invoked on the UI (STA) thread.
/// </summary>
internal interface IEditorTextStoreHost
{
    /// <summary>True only when an IME composition should be accepted by the current editor mode.</summary>
    bool IsCompositionAllowed { get; }

    /// <summary>Native window handle backing the editor (for GetWnd / coordinate mapping).</summary>
    IntPtr WindowHandle { get; }

    /// <summary>
    /// The composition string changed (text and/or caret). <paramref name="caret"/> is the
    /// character offset of the IME caret within <paramref name="text"/>.
    /// </summary>
    void OnCompositionUpdated(string text, int caret);

    /// <summary>The composition was finalized; <paramref name="text"/> is the committed string.</summary>
    void OnCompositionCommitted(string text);

    /// <summary>The composition ended with no committed text (cancelled).</summary>
    void OnCompositionCanceled();

    /// <summary>
    /// Screen rectangle (device pixels) of the composition caret, used to place the
    /// IME candidate window. Returns false when no layout is available yet.
    /// </summary>
    bool TryGetCaretScreenRect(out int left, out int top, out int right, out int bottom);

    /// <summary>Screen rectangle (device pixels) of the editor's client area.</summary>
    bool TryGetClientScreenRect(out int left, out int top, out int right, out int bottom);
}
