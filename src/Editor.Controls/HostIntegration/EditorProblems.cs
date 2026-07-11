namespace Editor.Controls.HostIntegration;

/// <summary>Severity of a problem supplied by an editor host.</summary>
public enum EditorDiagnosticSeverity { Error, Warning, Information, Hint }

/// <summary>A zero-based immutable position; columns count UTF-16 code units.</summary>
public readonly record struct EditorTextPosition(int Line, int Column);

/// <summary>A zero-based, end-exclusive text range.</summary>
public readonly record struct EditorTextRange(EditorTextPosition Start, EditorTextPosition End)
{
    /// <summary>Creates a range and validates that its positions are non-negative and ordered.</summary>
    public static EditorTextRange Create(int startLine, int startColumn, int endLine, int endColumn)
    {
        if (startLine < 0 || startColumn < 0 || endLine < 0 || endColumn < 0)
            throw new ArgumentOutOfRangeException(nameof(startLine), "Text positions cannot be negative.");
        if (endLine < startLine || (endLine == startLine && endColumn < startColumn))
            throw new ArgumentException("The end position must not precede the start position.");
        return new(new(startLine, startColumn), new(endLine, endColumn));
    }
}

/// <summary>A diagnostic supplied by a host, independent of Editor.Core and LSP types.</summary>
/// <param name="Range">Location in the current document.</param>
/// <param name="Message">Human-readable problem description.</param>
/// <param name="Severity">Problem severity.</param>
/// <param name="Source">Optional producer, such as a compiler or linter.</param>
/// <param name="Code">Optional producer-specific diagnostic code.</param>
/// <param name="Data">Optional opaque host data returned unchanged. Its object graph is not deep-copied; use an immutable value when snapshot semantics matter.</param>
public sealed record EditorDiagnostic(
    EditorTextRange Range,
    string Message,
    EditorDiagnosticSeverity Severity = EditorDiagnosticSeverity.Error,
    string? Source = null,
    string? Code = null,
    object? Data = null);

/// <summary>A host-owned entry shown and navigated through the editor quickfix commands.</summary>
/// <param name="DocumentPath">Absolute/relative host-defined file path or absolute URI. The editor does not normalize or open it.</param>
/// <param name="Range">Location within the target document.</param>
/// <param name="Message">Text displayed by the host's quickfix UI.</param>
/// <param name="Severity">Optional problem severity.</param>
/// <param name="Source">Optional producer name.</param>
/// <param name="Code">Optional producer-specific code.</param>
/// <param name="Data">Optional opaque host data. Its object graph is not deep-copied; use an immutable value when snapshot semantics matter.</param>
public sealed record EditorQuickfixItem(
    string DocumentPath,
    EditorTextRange Range,
    string Message,
    EditorDiagnosticSeverity? Severity = null,
    string? Source = null,
    string? Code = null,
    object? Data = null);

/// <summary>Describes an atomic replacement of the host quickfix list.</summary>
public sealed class EditorQuickfixChangedEventArgs(
    string title,
    IReadOnlyList<EditorQuickfixItem> items) : EventArgs
{
    public string Title { get; } = title;
    public IReadOnlyList<EditorQuickfixItem> Items { get; } = items;
}

/// <summary>Identifies the item resolved by a quickfix navigation command.</summary>
public sealed class EditorQuickfixNavigationEventArgs(int index, EditorQuickfixItem item) : EventArgs
{
    /// <summary>Zero-based index into the current quickfix snapshot.</summary>
    public int Index { get; } = index;
    public EditorQuickfixItem Item { get; } = item;
}
