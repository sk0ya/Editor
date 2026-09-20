# Host diagnostics and quickfix integration

An embedding application can publish compiler, linter, or task results without referencing
`Editor.Core` or LSP model types. Lines and columns are zero-based, columns count UTF-16 code units,
and ranges are end-exclusive. `DocumentPath` is a host-defined relative/absolute path or absolute URI;
the editor neither normalizes nor opens it.

```csharp
using Editor.Controls.HostIntegration;

editor.ReplaceDiagnostics([
    new EditorDiagnostic(
        EditorTextRange.Create(4, 8, 4, 13),
        "Unknown name",
        EditorDiagnosticSeverity.Error,
        Source: "build",
        Code: "CS0103")
]);

editor.ReplaceQuickfixItems([
    new EditorQuickfixItem(
        @"C:\project\Program.cs",
        EditorTextRange.Create(4, 8, 4, 13),
        "Unknown name",
        EditorDiagnosticSeverity.Error,
        Source: "build",
        Code: "CS0103")
], "Build errors");
```

`ReplaceDiagnostics` renders host diagnostics together with diagnostics from the configured LSP
manager. `ClearDiagnostics` removes only host entries. The current immutable snapshots are available
through `HostDiagnostics`, `HostQuickfixItems`, and `HostQuickfixTitle`.
The collections cannot be mutated. Records are immutable, but an object supplied as `Data` is opaque
and is not deep-copied, so immutable metadata should be used when strict snapshot behavior is needed.
These synchronous APIs must be called on the editor's WPF Dispatcher thread; otherwise they throw.

Quickfix commands continue to raise `QuickfixOpenRequested`, `QuickfixNextRequested`,
`QuickfixPrevRequested`, and `QuickfixGotoRequested`. The host uses the supplied count/index to
navigate `HostQuickfixItems` and open its `DocumentPath` and `Range`. Subscribe to
`HostQuickfixNavigationRequested` to receive the resolved item directly. Its `Index` is zero-based;
`:cc N` remains Vim-compatible and uses a one-based `N`, while counts for `:cnext`/`:cprev` are deltas.
`HostQuickfixItemsChanged` when a separate results panel needs immediate updates after replacement.
`ClearQuickfixItems` atomically empties the list and raises the same event.

## Where diagnostics become visible: the scrollbar marks and the quick-fix bulb

Squiggles only exist where the text is on screen, so two surfaces make the same diagnostics
reachable from anywhere in the document.

**Scrollbar marks (overview ruler).** Every diagnostic — LSP and host alike, since both flow through
the same combined snapshot — is folded onto the vertical overlay scrollbar: one mark per line, worst
severity wins, and **every severity is kept**, including `Hint` (an unused `using` shows as faded text
in the body, which says nothing about the part of the file you cannot see). Severity picks the color
(`DiagnosticError`/`Warning`/`Info`/`Hint`), so a tidy-up hint stays quiet without disappearing.
Clicking a mark
moves the caret to that diagnostic and centres the viewport. Nothing to enable; the marks appear
whenever the vertical scrollbar does (a document that fits on screen has no hidden problems).
The fold and the track mapping are `Editor.Core.Lsp.DiagnosticOverviewMarks` (pure, unit tested).

**Quick-fix bulb (gutter column).** `editor.SetCodeActionBulbEnabled(true)` turns on the leftmost glyph
column (right of the blame margin) that shows a bulb on the caret line when that line
actually has quick fixes; clicking it opens the same list as `Alt+Enter`. The candidates come from the
host's `HostCodeActionProvider` first and then LSP `textDocument/codeAction` — the same path Alt+Enter
and the hover popup use, so a bulb never appears for a line where the command would say "no fixes".
The probe is debounced (250 ms) and only runs for lines that carry a diagnostic, so caret movement
does not flood the language server.

The column is **off by default** and, while on, keeps its width even when no bulb is showing — the
bulb comes and goes with the caret, and a column that folds away would make the text jump sideways.
Enable it for files that have a fix source (a configured language server, or a host provider for that
language) and leave it off for `.md`/`.txt`/binary views.
