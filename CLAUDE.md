# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

```bash
# Build entire solution
dotnet build Editor.sln

# Run all tests
dotnet test tests/Editor.Core.Tests/

# Run a single test by name
dotnet test tests/Editor.Core.Tests/ --filter "FullyQualifiedName~VimEngineTests.DD_DeletesLine"

# Run the standalone app
dotnet run --project src/Editor.App/

# Build release
dotnet build Editor.sln -c Release
```

## Architecture

This is a WPF Vim editor split into three layers with a strict dependency rule: **Editor.Core has zero WPF dependencies**.

```
Editor.App → Editor.Controls → Editor.Core
Editor.Core.Tests → Editor.Core
```

### Editor.Core (net9.0 — pure logic)

The Vim engine is driven by `VimEngine.ProcessKey(string key, bool ctrl, bool shift, bool alt) → IReadOnlyList<VimEvent>`. Callers feed raw key names and receive a list of events to act on (text changed, cursor moved, mode changed, save requested, etc.).

**Key data flow:**
1. `VimEngine.HandleNormal` feeds keys to `CommandParser.Feed(key)` which accumulates `[count][operator][motion]` sequences and returns `(CommandState, ParsedCommand?)`.
2. When `CommandState.Complete`, `ExecuteNormalCommand(ParsedCommand)` dispatches to motion/operator handlers.
3. Double-operator commands (`dd`, `yy`, `cc`) are identified by `ParsedCommand.LinewiseForced == true && Operator != null` and handled **before** the main switch in `ExecuteNormalCommand`.
4. `MotionEngine.Calculate(string motion, CursorPosition, int count)` handles all motion arithmetic against `TextBuffer`. It is stateless and takes the buffer as a constructor argument.
5. `ExCommandProcessor.Execute(string cmdLine, CursorPosition)` handles all `:` commands and returns `ExResult` which may contain a `VimEvent` (e.g. `QuitRequested`, `OpenFileRequested`).

**Critical parser rules:**
- `g` and `z` are **motion prefixes**, not operators — they are handled with explicit `if` checks before the operator `switch` in `CommandParser.TryParse`.
- `'v'` is **not** an operator — it goes through `ParseMotion` and becomes a complete single-key command.
- `FindNext` searches from `column + 1` (Vim `n` semantics — skips current position).

**Buffer system:** `BufferManager` manages multiple `VimBuffer` instances (each wraps a `TextBuffer` + `FilePath` + `UndoManager`). `UndoManager` stores snapshots (lines + cursor, max 1000 entries) and is driven by `VimEngine` — it calls `Snapshot()` before mutating operations and `Undo()`/`Redo()` on `u`/`Ctrl+R`.

**Registers:** `RegisterManager` (in `Editor.Core.Registers`) manages named registers `a–z`, unnamed `"`, clipboard `+`/`*`, and blackhole `_`. Uppercase register names (e.g. `"A`) append to the lowercase register. Clipboard is abstracted via `IClipboardProvider` so the core has no WPF dependency. The WPF implementation `WpfClipboardProvider` lives in `Editor.Controls`.

**Marks & Macros:** `MarkManager` stores marks by letter and a jump list (max 100, navigated via `Ctrl+O`/`Ctrl+I`). `MacroManager` records `VimKeyStroke` sequences into named registers and replays them.

**Config:** `VimConfig` loads `.vimrc`/`_vimrc` from home or the project directory on startup. It parses `set` options via `VimOptions` (30+ toggles and key=value settings like `tabstop=4`) and registers normal/insert/visual remaps (`nmap`, `imap`, `vmap`, `nnoremap`, etc.).

**VimEventType enum** (all values — needed when adding new events or handling them in `VimEditorControl`):
`ModeChanged`, `TextChanged`, `CursorMoved`, `SelectionChanged`, `SaveRequested`, `QuitRequested`, `OpenFileRequested`, `NewTabRequested`, `SplitRequested`, `NextTabRequested`, `PrevTabRequested`, `CloseTabRequested`, `ViewportAlignRequested`, `StatusMessage`, `SearchResultChanged`, `CommandLineChanged`

### Editor.Controls (net9.0-windows, WPF)

`VimEditorControl` is the public-facing `UserControl`. It owns a `VimEngine` instance and bridges WPF key events to `VimEngine.ProcessKey`, then processes the returned `VimEvent` list to update the UI. It includes extensive IME (Input Method Editor) support for international text input.

**Rendering:** `EditorCanvas` extends `FrameworkElement` and overrides `OnRender(DrawingContext)` — it does **not** use a `ScrollViewer` (passing Infinity to a FrameworkElement crashes). All scrolling is handled internally via `_scrollOffsetY`/`_scrollOffsetX`. `MeasureOverride` must clamp infinite sizes to finite fallback values.

Key events are translated from `System.Windows.Input.Key` → vim key strings in `GetVimKey(Key, bool shift)`. In Normal/Visual mode all printable keys are captured here; in Insert mode `TextCompositionEventArgs.Text` is used instead.

**Theme:** `EditorTheme` (in `Editor.Controls.Themes`) holds all colors. `EditorTheme.Dracula` is the default. Pass a theme instance to `VimEditorControl.SetTheme(EditorTheme)`.

### Editor.App (net9.0-windows, WPF)

Thin host: `MainWindow` wires `VimEditorControl` events (`SaveRequested`, `QuitRequested`, `OpenFileRequested`) to file dialogs and tab management. Command-line arguments are read in `Window_Loaded` — the first arg is treated as a file path to open.

The layout is: Title bar (30 px) → main area with Activity Bar (48 px vertical strip of icon toggle buttons) → collapsible Sidebar (220 px default, resizable via `GridSplitter`) → `TabControl` editor area. The Sidebar hosts a `TreeView` bound to `FileTreeItem` (nested class in `MainWindow.xaml.cs`) which lazy-loads directory children using a placeholder pattern.

## Adding a New Vim Command

1. **Normal mode motion** — add a case to `MotionEngine.Calculate(string, CursorPosition, int)` and handle the resulting `Motion` in `VimEngine.ExecuteNormalCommand`.
2. **Normal mode action** — add a `case "x":` branch directly in `VimEngine.ExecuteNormalCommand`.
3. **Ex command** — add a branch in `ExCommandProcessor.Execute`. Return `new ExResult(true, null, VimEvent.XxxRequested(...))` to communicate with the host app.
4. **New VimEvent type** — add to the `VimEventType` enum in `VimEvent.cs`, add a factory method and record subclass, then handle it in `VimEditorControl.ProcessVimEvents`.

## LSP (Language Server Protocol)

**The editor does not own an LSP session — the host does.** The split is "processes and protocol are
workspace-scoped, UI state is view-scoped":

- **`Editor.Core/Lsp/`** — the contracts. `ILspWorkspace` (the host's session: server pooling,
  `initialize`, document reference counting, `workspace/symbol`, workspace diagnostics, call/type
  hierarchy), `ILspDocument` (one handle on one open URI — document-scope requests + `didChange`),
  `ILspServerAdmin` (write access to the extension→server table), plus `ILspClient`, `LspModels`,
  `LspServerDef`/`LspServerEntry`/`LspExtensions`. Pure .NET, no WPF.
- **`Editor.Controls/Lsp/`** — `IEditorLspView` and `LspViewBridge`: one per `VimEditorControl`,
  holding **only** view state (completion / signature-help / code-action popups, breadcrumb, folding,
  inlay hints, semantic tokens, highlights) over a single `ILspDocument` handle. `NullLspView` is used
  when no host supplied a workspace.
- **`Editor.Controls.Defaults/Lsp/`** — reusable protocol parts a host implementation builds on:
  `LspProcess` (JSON-RPC 2.0 over stdio), `LspClient` (implements `ILspClient`), `JobObject`.

Enable LSP by passing `VimEditorControlOptions.LspWorkspace` (and `LspServerAdmin` for the `:Lsp*`
commands). `VimEditorControlDefaults.CreateOptions()` deliberately supplies **neither** — a standalone
editor runs with LSP off, because a session needs workspace roots and a process lifetime policy that
only a host can define.

**Threading contract:** `ILspWorkspace` / `ILspDocument` events fire on **background threads** (the
JSON-RPC read loop). Marshalling to a dispatcher is the subscriber's job — `LspViewBridge` is what does
it for the control, and every member/event of `IEditorLspView` is dispatcher-thread only.

**Sending is off the UI thread too, and `didChange` is coalesced.** `LspProcess` owns a second background
thread (`LspStdin`); callers only push onto `OutgoingMessageQueue` and return. Serialization *and* the pipe
write happen there, so a server that is slow to read its stdin (Roslyn re-analyses on every change) can never
block a keystroke — one queue, so **enqueue order is delivery order**. `didChange` goes through the overload
that takes a `Func<object?>` plus a coalesce key: the body is built **just before it is sent**, and a pending
change for the same document is replaced by the newer one, so overtaken versions are never even serialized
(a 300KB `.cs` cost 1.6–4ms of UI thread per keystroke before this). The diff is taken against *the last text
actually sent* — updated inside that same deferred factory — so skipping versions cannot desync the server.
Replacement **keeps the queue position**: move it to the back and a completion request queued in between would
reach the server ahead of the text it is about to be answered against. `DropCoalesceKey` is called around
`didOpen`/`didClose` so a reopen never folds into the old entry.

**Code actions / refactorings.** A code action is *not* just `{title, kind, edit}` — `edit` is usually absent:
Roslyn returns `data` only and builds the edit in `codeAction/resolve`, tsserver returns a `command` whose edit
comes back as a **server-initiated `workspace/applyEdit`**. So `LspCodeAction` carries `Command` + `RawJson`
(the untouched server JSON — `data` is server-private and must be echoed back verbatim), and applying one means
resolve-then-edit or execute-then-await-applyEdit. `ILspClient.ApplyEditRequested` is that inbound path; it fires
on the read thread and **the server is blocked until the handler returns** (`LspProcess.ServerRequestHandler`).
`GetCodeActionsAsync` takes a **range** and `only` — a bare position never surfaces Extract Method.
`LspWorkspaceEdit.FileOperations` carries `documentChanges`' create/rename/delete; dropping them makes
"extract to a new file" silently do nothing. Kinds are a dotted hierarchy — compare with
`LspCodeActionKinds.Matches`, not `==`. The client capabilities that make all of this arrive at all
(`codeActionLiteralSupport`, `resolveSupport`, `dataSupport`, `workspace.applyEdit`) are declared in
`LspClient.InitializeAsync`. Host-side design: Loomo `docs/設計/32-リファクタリング.md` (§32).

**Applying an edit the host owns:** `WorkspaceEditRequestedEventArgs` carries `Handled`, `Error` **and
`Cancelled`**. A host that asks the user (an edit preview, a confirmation) and is told "no" sets `Cancelled`,
not `Error` — the editor then leaves the current buffer untouched and reports "cancelled", where an `Error`
would have been reported as `Code action 'X' failed: …`, i.e. the user's own decision handed back as a
failure.

**Hover info (mouse):** resting the mouse on an identifier pops up its type and summary — the Rider/VS
tooltip. `Rendering/EditorCanvas.TextHover.cs` only decides *which word the pointer is on*
(`TextHoverChanged`; it hit-tests the text itself because `HitTest` rounds a point past end-of-line back
onto the last column) and `VimEditorControl.HoverInfo.cs` owns the dwell timer, the request and the popup.
The body is the **same** `IEditorLspView.RequestHoverAsync` path as `K` — server `textDocument/hover`, else
the host's `HostHoverProvider` — with the diagnostics covering that position (`EditorCanvas.DiagnosticsAt`)
listed first, and under them a **lightbulb** — pressing it opens that diagnostic's quick fixes inside the same
popup, and clicking one applies it. The fixes are **fetched on that press, never on hover**: the host's
`HostCodeActionProvider` has no cancellation parameter, so hovering would leave one uncancellable Roslyn/LSP
computation per squiggle behind as the mouse sweeps a file (same reason Loomo fills its own Quick Fix submenu
on `SubmenuOpened`). While the request is out the popup says so, and an empty answer says "no fixes" rather
than leaving a bulb that does nothing. Fixes come from the **same** `CollectQuickFixesAsync` as Alt+Enter (host
`HostCodeActionProvider` first, then LSP `only: ["quickfix"]` over the **diagnostic's own range**) and apply
through the same `ApplyCodeActionAsync`, so there is one path, not two. Servers do **not** honour `only` reliably
(a C# server returned "extract method" for an unused-variable warning), so `Editor.Core/Lsp/HoverFixSelection.cs`
keeps quickfix/fixAll kinds plus kind-less actions, puts `IsPreferred` first and caps the list. The bulb stays
collapsed by default: someone hovering to read a type should not get a stack of rewrites in their face.
Servers answer in **Markdown**, so `Editor.Core/Text/HoverMarkdown.cs` (pure, tested) splits it
into code / text / rule blocks and `Rendering/HoverContentBuilder.cs` renders them, running code fences
through the editor's own `SyntaxEngine` so a signature is coloured like code. Off switch:
`VimEditorControlOptions.HoverInfoEnabled` / `HoverInfoDelayMs` (runtime: `VimEditorControl.HoverInfoEnabled`).

**Latency is the dwell, not the server** (measured with `SK0YA_EDITOR_IDE_DIAG=1`, which logs
`shown: dwell X + request Y + build Z` and the LSP/host split to `%TEMP%\editor-lsp-debug.log`). A warm
Roslyn answers hover in **3–55ms** and the popup builds in **4–83ms**, so at the old 400ms dwell **77–98% of
the wait was the dwell itself**; the default is now **250ms**. The cold first hover is a different animal —
**5.5s**, all of it Roslyn loading the solution — and no dwell change touches it, so a request still
outstanding after 300ms puts up a 「説明を取得しています…」 popup rather than dead air. Because
`RequestHoverAsync` has **no cancellation** (a started host computation cannot be stopped), a shorter dwell
must not be allowed to stack work: the dwell tick re-arms instead of firing while `_hoverRequestInFlight`,
capping it at one in-flight request.
Distinct from the debug **DataTip** (`VimEditorControl.Debug.cs`), which evaluates a *value* while stopped.

**Pinning (📌 / `Ctrl+Shift+K`) and copying (📋 / `Ctrl+Shift+C`).** The popup used to close on *every*
one of: a keystroke, window deactivation, the pointer leaving it, a scroll, a click. Each is a **guess**
that the reader is done — and together they made the thing impossible to **screenshot**, because
Win+Shift+S and PrintScreen are themselves a keystroke plus a deactivation. Pinning suspends the guess:
while pinned only `Escape` (and pressing 📌 again) closes it, and `OnCanvasTextHoverChanged` stops
replacing it when the mouse moves to another word. So the split is **`HideHoverInfo()` = really close**
(Escape, `LoadFile`, unload, theme change, applying a fix) vs **`HideHoverInfoUnlessPinned()` = the guess**
— every "probably done" path must call the second one, or the pin silently does nothing there.
Both marks are **`Collapsed` until the pointer enters the popup**: they reserve no width, and by the time
a capture happens the mouse has left, so **they are not in the picture**.

**The popup body is a `FlowDocument` in a `FlowDocumentScrollViewer`, not a `StackPanel` of `TextBlock`s** —
`TextBlock` cannot be selected in WPF, so the text was readable but not extractable (a signature had to be
retyped). `HoverContentBuilder.Build` therefore returns a `FlowDocument`: prose and **code fences are
`Paragraph`s** (so the signature — the thing most worth taking — is inside the selection), while the
**lightbulb, fix rows and separators stay `Border`s inside `BlockUIContainer`**, which keeps their look and
their click handlers. The viewer must be **`Focusable=true`**: WPF text selection only starts once the control can take focus, so
with `Focusable=false` a drag selects **nothing, silently** — no exception, no warning, and no test goes red
(measured: false → empty, true → the text; `FlowDocument.ColumnWidth` is irrelevant, that was the wrong
suspect). Keyboard focus still belongs to the buffer, so it is only *borrowed*: `ReturnFocusToBuffer` hands it
back on `PreviewMouseLeftButtonUp` (posted at `Background` priority — returning it inline pre-empts the
selection being finalized) and again in `HideHoverInfo`. That is safe because **the selection survives losing
focus**, opening the popup steals nothing (the `Popup` itself is `Focusable=false`), and window activation is
never taken — but only with **`IsInactiveSelectionHighlightEnabled = true`**: WPF's default drops the
selection *highlight* the instant focus leaves, while keeping `Selection.Text`, so the range silently stops
being visible on mouse-up (measured: selection-coloured pixels 4032 → 0, and → 4032 with the flag on).
Watching `Selection.Text` cannot catch that; a selection you cannot see is barely a selection.
`SelectionOpacity` is pinned to 1.0 too, since the theme's `SelectionBg` already carries its own alpha and
the default 0.4 washes it out. `Viewer_IsFocusable_OrDraggingSelectsNothing` guards the invariant; `HoverSelectionTests` also
covers a real `VisualTreeHelper.HitTest` on a fix row (a synthetic `RaiseEvent` would pass even if the viewer
swallowed input). Because focus lives in the buffer, **plain `Ctrl+C` is taken only while the popup has a
selection**, otherwise it falls through to yank. Closing rules had to
learn about dragging too: `MouseLeave` is ignored while the left button is down, and a `MouseLeftButtonUp`
closes only when it was a real click (moved ≤ 3px) with nothing selected — otherwise a drag-select ended by
destroying its own selection. The popup has its own **right-click menu** (copy / select all / pin / close),
built fresh per click through the same `CreateThemedMenu` as the buffer's, and assigned to **both** the
viewer and the border (whichever is the innermost owner under the pointer). A `ContextMenu` opens and stays
open fine inside a `Popup` — what killed it was this side: the menu appearing fires the popup's `MouseLeave`,
so the hold must be taken on `PreviewMouseRightButtonDown`, **before** the menu opens; waiting for
`menu.Opened` is already too late and the popup takes the menu down with it. Hence
`HideHoverInfoUnlessPinned` became **`HideHoverInfoUnlessHeld`** — held = pinned **or** menu open — with a
400ms guard timer that releases the hold if no menu actually opened. The `Paragraph` code block loses the old `CornerRadius`; that is the price of
being selectable. 📋 copies via `Editor.Core/Text/HoverCopyText.cs` (pure, tested) — the **selection if there
is one**, else the whole popup in the **same order it is shown** (diagnostics → signature → prose, no fence
markers, rules dropped). It also owns `Origin()`, so the displayed and copied spelling of `Roslyn(CS0219)`
cannot drift.

**Key bindings:**
- `K` (Normal mode) — hover info in the same popup (it used to be one status-bar line, which showed
  nothing but the ```` ```csharp ```` fence for Markdown servers)
- `Ctrl+Shift+K` — pin / unpin the open hover popup (pinned: only `Escape` closes it)
- `Ctrl+Shift+C` — copy the open hover popup's text (the selection if there is one, else all of it)
- `Ctrl+C` — same, but only while the hover popup has a selection; otherwise it stays yank
  (all three are live **only while the popup is open**, so they take nothing away from editing)
- `Ctrl+Space` (Insert mode) — trigger completion popup
- `↓`/`Ctrl+N`, `↑`/`Ctrl+P` — navigate completion list
- `Tab`/`Enter` — insert selected completion item
- `Escape` — dismiss completion

**Server table & user configuration:** the extension→server map is **not** in this repo. It belongs to
the host, reached through `ILspServerAdmin`, because the executable is inseparable from the things a
host owns anyway (install commands, PATH detection, the settings UI). There is no `LspServerRegistry`
and no process-wide default — a "compatibility factory that news up an instance per access" is exactly
the trap that produced three divergent registries before this was split.

**Managing servers (user-facing, ex commands):**
- `:Lsp` / `:LspList` — show the effective extension→command table (built-in / custom / removed).
- `:LspAdd <ext> <executable> [args...]` — register or replace a server (e.g. `:LspAdd .zig zls`).
- `:LspRemove <ext>` — drop a custom server, or hide a built-in.
- `:LspReset <ext>` — discard user changes for an extension, restoring the built-in default.

These are an **input frontend only**: they delegate to the injected `ILspServerAdmin`, so they act on
the very table the host's settings UI and its `ILspWorkspace` use. With no admin injected they answer
"LSP: server configuration is not available in this host". Whether a change takes effect immediately
or on reopen is the host implementation's call.

**Diagnostics** are rendered as wavy underlines on `EditorCanvas`. Colors are defined on `EditorTheme` (`DiagnosticError`, `DiagnosticWarning`, `DiagnosticInfo`, `DiagnosticHint`).

## Document formatting (`:Format`)

`:Format` (also the "Format Document" context-menu item) is handled by `VimEditorControl.HandleFormatDocumentAsync`. It is **not** LSP-only — many text-LSP servers (e.g. `marksman`) never implement `textDocument/formatting`, so there is a CLI-formatter layer alongside it:

**Range/selection formatting:** `:Format` accepts an ex range — `:'<,'>Format`, `:10,20Format`, `:%Format`. Pressing `:` in Visual mode prefills the line range, so selecting text and typing `:Format` formats just the selection ("Format Selection" in the context menu does the same). The range travels as `FormatDocumentRequestedEvent(StartLine, EndLine)` (0-based inclusive, null = whole document). LSP uses `textDocument/rangeFormatting` (`IEditorLspView.RequestRangeFormattingAsync`); when the server doesn't advertise `documentRangeFormattingProvider` the request is **refused rather than widened** to the whole document. CLI formatters are stdin→stdout over a whole document, so a range format feeds the formatter only the selected lines, dedented to column 0 and re-indented afterwards (otherwise the formatter flattens a nested block). That slicing lives in `Editor.Core/Formatting/LineRangeText.cs` (pure, tested in `LineRangeTextTests`).

- **`Editor.Core/Formatting/FormatterRegistry.cs`** — extension→CLI-formatter map (`FormatterDef(Executable, Args)`, **stdin→stdout** convention; `{file}` in `Args` is replaced with the current path). Pure .NET, no WPF. Has a `Default` factory + `ConfigureDefault(path)` and JSON persistence to `%APPDATA%/sk0ya.Editor/formatters.json`, but ships **no built-in active mappings**. It still carries the same host-ownership defect the LSP table was split out for (see the Loomo design docs §30.9) — a later change moves it to the host too.
- **`Editor.Core/Formatting/KnownFormatters.cs`** — suggestion-only candidate catalog per extension (prettier/dprint/black/rustfmt/gofmt/…), **never auto-activated**.
- **`Editor.Controls/Formatting/FormatterRunner.cs`** — runs the formatter as a one-shot child process (buffer → stdin, stdout → buffer, UTF-8/no-BOM, timeout). A non-zero exit or launch failure returns an error and leaves the buffer **untouched**. `IsOnPath` probes PATH (+PATHEXT).

Resolution order in `HandleFormatDocumentAsync`: (1) a configured CLI formatter for the extension **wins** over LSP; (2) else LSP `textDocument/formatting`; (3) else probe `KnownFormatters` candidates on PATH and, if one is installed, use **and register** it (persisted via the host's `ConfigureDefault` store). Newline style is preserved (CRLF restored if the formatter emitted bare LF).

**Managing formatters (ex commands):** `:Fmt` / `:FmtList`, `:FmtSet <ext> <executable> [args...]` (use `{file}` for the path, e.g. `:FmtSet .md prettier --stdin-filepath {file}`), `:FmtRemove <ext>`.

## Gutter columns (host-driven)

The gutter is laid out left→right as **blame | breakpoint | test | line number | fold | text**. The two
host-driven columns (breakpoint, test) are **off by default and 0 px wide** until the host enables them, so a
standalone editor's layout is untouched. Widths come from `EditorCanvas.GetGutterMetrics()` and hit-testing from
`Rendering/GutterHitTester.cs`, whose `Boundaries` record lists the columns in left→right order. Adding a column
means touching **four** places: `GetGutterMetrics`, `Boundaries` + the neighbouring `TryHit*` ranges, the `Draw*`
call in `OnRender`, and `GutterRenderer.DrawLineNumberAndFold` (its signature plus the two x calculations inside
it, which offset the line number text and the fold chevron).

- **Breakpoints** (`Rendering/EditorCanvas.Breakpoints.cs`, `VimEditorControl.Debug.cs`) — `SetBreakpointsEnabled`,
  `SetBreakpoints`, `SetExecutionLine`, `BreakpointToggled`, plus the DataTip hover bridge.
- **Test glyphs** (`Rendering/EditorCanvas.TestGlyphs.cs`, `VimEditorControl.TestGlyphs.cs`) — the gutter side of
  "run this test ▶ / here's its result". **Discovery, execution and pass/fail judgement are entirely the host's
  job**; the editor only draws a glyph on a line and reports clicks. `SetTestGlyphsEnabled(bool)`,
  `SetTestGlyphs(IReadOnlyList<EditorTestGlyph>)` (**full replace**; an empty list clears), and
  `event Action<int>? TestGlyphClicked` (0-based **buffer** line, raised only for lines that carry a glyph;
  a click anywhere in the column is swallowed rather than moving the caret). `EditorTestGlyph(int Line0,
  TestGlyphKind Kind, string? Tooltip = null)` with `TestGlyphKind` = `Run`/`Passed`/`Failed`/`Skipped`/`Running`;
  `Tooltip` is shown on hover and re-read on every `SetTestGlyphs`, so a `Running`→`Failed` swap under a
  motionless cursor updates the open tooltip instead of leaving the stale text. Glyph colors come from
  `EditorTheme` (`GitAdded`/`DiagnosticError`/`DiagnosticHint`/`DiagnosticWarning`/`LineNumberFg`); only the hover
  ring is a fixed translucent brush, because the gutter background and the current-line background are nearly the
  same in some themes.

Both columns skip wrapped continuation rows, so a glyph is drawn once per buffer line.

**Host contract for test glyphs**, none of which the editor does for you:

- Glyphs are keyed by line number only, so they **do not follow inserted/deleted lines**. After any edit that
  shifts lines (and after a `TextChanged` burst settles) the host must re-send the whole list.
- `LoadFile` clears the glyphs, so switching documents in one control never shows the previous file's results;
  the host re-sends for the newly opened file.
- The column is **mouse-only** — there is deliberately no key binding, ex command or automation peer for it, and
  a screen reader sees nothing. That is a host responsibility: a host that offers "run the test at the caret"
  must expose it as its own command, and should treat the ▶ as a convenience on top, not the only route.

## Adding Syntax Highlighting for a New Language

Implement `ISyntaxLanguage` (in `Editor.Core.Syntax`) and register the instance in the array inside `SyntaxEngine`. The interface requires `Name`, `Extensions`, and `Tokenize(string[] lines) → LineTokens[]`. Available `TokenKind` values: `Text`, `Keyword`, `Type`, `String`, `Comment`, `Number`, `Operator`, `Preprocessor`, `Identifier`, `Attribute`.

## Filetype Edit Assists (smart Enter/Tab)

Filetype-aware editing aids live in `Editor.Core/Editing/` (pure .NET, no WPF):

- **`IEditAssist`** — `AppliesTo(filePath)` + `OnEnter(EditContext)` / `OnTab(EditContext, shift)` (each returning an `EditResult` = `Handled` + new `Cursor`) + `OpenLinePrefix(EditContext, above)` (the prefix to seed an `o`/`O`-opened line, or `null`). `EditAssistBase` declines every hook so implementations override only what they need. The assist mutates `EditContext.Buffer` in place and returns the new caret.
- **`EditAssistRegistry`** — resolves the first assist that applies to a file; `Default` is the process-wide singleton used by `VimEngine`. Add an assist with `Register(...)` (most-recently-registered wins).
- **`MarkdownEditAssist`** — `.md`/`.markdown`: Enter (and `o`/`O`) continues the current list item (`-`/`*`/`+`, or next ordinal for `1.`/`1)`; `O` keeps the ordinal) preserving indent; an empty item on Enter clears the marker (exits the list); Tab/Shift+Tab indent/outdent the list item even with no text yet.

`VimEngine` calls the resolved assist in `InsertNewline` (Enter), `TryEditAssistTab` (Tab), and `OpenLineBelow`/`OpenLineAbove` (`o`/`O`), falling back to default behaviour when the assist declines. The Enter/Tab path covers both Vim Insert mode and the plain (Vim-disabled) edit mode. **To add smart editing for a new filetype, implement `IEditAssist` and register it — no `VimEngine` changes needed.**

## Inline suggestions (ghost text)

The prediction shown in faint text ahead of the caret, accepted with **Tab** (whole line) or **Ctrl+Right**
(one word). `set inlinesuggest` / `set isg` turns it off; on by default.

- **`Editor.Core/Completion/`** (pure, no WPF) — `InlineSuggestion` (the text to insert at the caret, plus
  where it came from; `FirstLine` and `NextWordLength()` are the two ways it gets accepted) and
  **`BufferLinePredictor.Predict(lines, line, column)`**, which answers from *earlier lines of the same
  buffer*: it takes the caret line's text as the clue and returns the rest of the nearest line that starts the
  same way, searching outward from the caret and stopping at the first hit (so it never walks the whole file,
  and the only allocation is slicing the one line it keeps). It deliberately stays quiet — caret not at
  end of line, clue under 3 characters, no match within `SearchRadius` → nothing. **A wrong suggestion
  lingering in faint text is worse than no suggestion**, which is why the "don't show" conditions carry most
  of the tests.
- **`VimEditorControl.InlineSuggestion.cs`** — the state machine. Suggestions appear only in Insert mode,
  at end of line, with no completion popup, no active snippet, and no multi-cursor: each of those also wants
  Tab, and overlapping them makes it unreadable which one will take it. A keystroke that matches the first
  character of the live suggestion just trims that character instead of recomputing (the suggestion shrinks
  in place rather than flickering). Everything else restarts a 120ms debounce.
- **Host provider** — `VimEditorControlOptions.InlineSuggestionProvider` takes
  `(InlineSuggestionContext, CancellationToken) -> Task<InlineSuggestion?>`, for a host that has something
  smarter (a local LLM, say). The built-in prediction is shown **first** and the host's answer replaces it
  when it arrives, so a slow provider never leaves a gap; stale answers are dropped by generation + caret
  check, and a provider that throws is ignored rather than allowed to disturb typing.
- **Rendering** — `EditorCanvas.SetInlineSuggestion(text, line, column)`, drawn at `PushOpacity` over the
  theme's own foreground (a fixed grey disappears in light themes and shouts in dark ones). The canvas
  refuses to draw when the caret has left the anchor, so a caret move that skips the control's own paths
  (a mouse click, say) can't leave faint text stranded.

## Clipboard Image Paste (Markdown)

Pasting into a `.md`/`.markdown` file while the system clipboard holds an **image** saves the image to disk and inserts a Markdown link (`![alt](path)`) instead of pasting text. Triggered by `p`/`P` (Normal mode) or `Ctrl+V` (Insert mode); non-image pastes and non-Markdown files fall through to the normal paste path.

- **`Editor.Core/Editing/ImagePasteOptions.cs`** (pure, no WPF) — the configurable rules: `Directory` (save folder relative to the Markdown file, default `images`), `FileName` (default `{filename}-{datetime}.png`), and `AltText`. `Resolve(markdownPath, timestamp, fileExists)` computes the `ImagePasteTarget(AbsolutePath, LinkPath, AltText)`. Template placeholders: `{filename}` (Markdown stem), `{date}`, `{time}`, `{datetime}`, `{seq}` (uniquifying counter). Without `{seq}`, a taken name gets a `-N` suffix.
- **`Editor.Controls/ImagePasteHandler.cs`** (WPF) — reads the clipboard image (prefers a raw `PNG` payload, else re-encodes the DIB via `PngBitmapEncoder`), writes the file, returns the link text. Owns an `ImagePasteOptions`.
- **Wiring** — `VimEditorControl.TryHandleImagePaste` intercepts the paste keystroke at the top of `ProcessKey` and calls `VimEngine.PasteText(text, after)` (public; undo + events, backed by `ClipboardEditOps.PasteRawText`). **Config API:** set `VimEditorControlOptions.ImagePasteOptions` at construction, or mutate `VimEditorControl.ImagePasteOptions` at runtime.

## Tests

Tests live in `tests/Editor.Core.Tests/`. Key files: `VimEngineTests.cs` (core vim ops), `TextBufferTests.cs` (buffer mutations), `ExCommandProcessorTests.cs` (`:` commands), `MarkdownEditAssistTests.cs` (edit assists).

Test naming convention: `Subject_Behavior()` (e.g. `DD_DeletesLine`). Engine tests use a `CreateEngine(string text, VimConfig? config = null)` factory helper that returns a configured `VimEngine` with the given initial text. Assertions check `engine.Mode`, `engine.Cursor`, `engine.CurrentBuffer.GetText()`, and event lists via `events.Any(e => e.Type == VimEventType.X)`.
