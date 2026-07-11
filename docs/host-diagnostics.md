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
