namespace Editor.Core.Buffer;

/// <summary>What kind of change a <see cref="TextBufferChange"/> reports.</summary>
public enum TextBufferChangeKind
{
    /// <summary>An edit: the range was replaced by <see cref="TextBufferChange.NewText"/>.</summary>
    Edit,

    /// <summary>
    /// The document was (re)loaded (<see cref="TextBuffer.SetText"/>). The range still covers the
    /// whole old document, so applying it as an edit gives the right text, but positions tracked
    /// across it (anchors, excerpts) no longer mean anything and should be re-resolved.
    /// </summary>
    Reload,
}

/// <summary>
/// One change to a <see cref="TextBuffer"/>, in the same shape as an LSP
/// <c>TextDocumentContentChangeEvent</c>: the range is in the coordinates of the text
/// <b>before</b> this change (0-based lines, columns in UTF-16 code units, end exclusive) and is
/// replaced by <see cref="NewText"/> (lines separated by <c>"\n"</c>). Applying the changes in
/// the order they are raised to a copy of the text reproduces the buffer exactly.
/// </summary>
/// <param name="Version">The buffer's <see cref="TextBuffer.Version"/> after the change.</param>
public sealed record TextBufferChange(
    int StartLine, int StartColumn, int EndLine, int EndColumn,
    string NewText, long Version,
    TextBufferChangeKind Kind = TextBufferChangeKind.Edit)
{
    /// <summary>True when the change only inserts text (the replaced range is empty).</summary>
    public bool IsInsertion => StartLine == EndLine && StartColumn == EndColumn;
}
