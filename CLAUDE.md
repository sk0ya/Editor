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

LSP support lives in two layers:

- **`Editor.Core/Lsp/`** — `ILspClient`, `LspModels`, `LspServerRegistry` (extension → server command map; pure .NET, no WPF)
- **`Editor.Controls.Defaults/Lsp/`** — `LspProcess` (JSON-RPC 2.0 over stdio), `LspClient` (implements `ILspClient`), `LspManager` (bridges `VimEditorControl` with LSP)

`LspManager` is owned by `VimEditorControl`. It starts/shares language server processes per executable, syncs documents, and fires `StateChanged` to update `EditorCanvas` diagnostics and completion popup.

**Key bindings:**
- `K` (Normal mode) — hover info shown in status bar
- `Ctrl+Space` (Insert mode) — trigger completion popup
- `↓`/`Ctrl+N`, `↑`/`Ctrl+P` — navigate completion list
- `Tab`/`Enter` — insert selected completion item
- `Escape` — dismiss completion

**Built-in servers** (auto-detected by file extension, must be installed separately):
`csharp-ls` (.cs), `pylsp` (.py), `typescript-language-server` (.ts/.js), `rust-analyzer` (.rs), `gopls` (.go), `clangd` (.c/.cpp), `lua-language-server` (.lua), `solargraph` (.rb), `marksman` (.md/.markdown)

**Server registry & user configuration:** the extension→server map is `LspServerRegistry` (`Editor.Core/Lsp/LspServerRegistry.cs`). It layers user changes (additions, replacements, hidden built-ins) over the built-in table and **persists them as JSON** to `%APPDATA%/sk0ya.Editor/lsp-servers.json`, so the configured set survives restarts. `LspServerRegistry.Default` is the process-wide singleton shared by `LspManager` (which resolves servers through it) and the ex commands below; `LspManager(dispatcher, registry?)` and `ExCommandProcessor(..., lspRegistry?)` both default to it (tests inject an in-memory instance by passing a registry with no store path). The editor owns this config — hosts only **enable** LSP by supplying `VimEditorControlOptions.LspManagerFactory` (`VimEditorControlDefaults.CreateOptions()` wires `new LspManager(d)`).

**Managing servers (user-facing, ex commands):**
- `:Lsp` / `:LspList` — show the effective extension→command table (built-in / custom / removed).
- `:LspAdd <ext> <executable> [args...]` — register or replace a server (e.g. `:LspAdd .zig zls`).
- `:LspRemove <ext>` — drop a custom server, or hide a built-in.
- `:LspReset <ext>` — discard user changes for an extension, restoring the built-in default.
Changes apply to a file when it is (re)opened. Adding a new built-in default still means adding an entry to `LspServerRegistry`'s `Builtins` table.

**Diagnostics** are rendered as wavy underlines on `EditorCanvas`. Colors are defined on `EditorTheme` (`DiagnosticError`, `DiagnosticWarning`, `DiagnosticInfo`, `DiagnosticHint`).

## Document formatting (`:Format`)

`:Format` (also the "Format Document" context-menu item) is handled by `VimEditorControl.HandleFormatDocumentAsync`. It is **not** LSP-only — many text-LSP servers (e.g. `marksman`) never implement `textDocument/formatting`, so there is a CLI-formatter layer alongside it:

- **`Editor.Core/Formatting/FormatterRegistry.cs`** — extension→CLI-formatter map (`FormatterDef(Executable, Args)`, **stdin→stdout** convention; `{file}` in `Args` is replaced with the current path). Pure .NET, no WPF. Mirrors `LspServerRegistry` (`Default` singleton, `ConfigureDefault(path)`, JSON persistence to `%APPDATA%/sk0ya.Editor/formatters.json`) but ships **no built-in active mappings**.
- **`Editor.Core/Formatting/KnownFormatters.cs`** — suggestion-only candidate catalog per extension (prettier/dprint/black/rustfmt/gofmt/…), **never auto-activated**.
- **`Editor.Controls/Formatting/FormatterRunner.cs`** — runs the formatter as a one-shot child process (buffer → stdin, stdout → buffer, UTF-8/no-BOM, timeout). A non-zero exit or launch failure returns an error and leaves the buffer **untouched**. `IsOnPath` probes PATH (+PATHEXT).

Resolution order in `HandleFormatDocumentAsync`: (1) a configured CLI formatter for the extension **wins** over LSP; (2) else LSP `textDocument/formatting`; (3) else probe `KnownFormatters` candidates on PATH and, if one is installed, use **and register** it (persisted via the host's `ConfigureDefault` store). Newline style is preserved (CRLF restored if the formatter emitted bare LF).

**Managing formatters (ex commands):** `:Fmt` / `:FmtList`, `:FmtSet <ext> <executable> [args...]` (use `{file}` for the path, e.g. `:FmtSet .md prettier --stdin-filepath {file}`), `:FmtRemove <ext>`.

## Adding Syntax Highlighting for a New Language

Implement `ISyntaxLanguage` (in `Editor.Core.Syntax`) and register the instance in the array inside `SyntaxEngine`. The interface requires `Name`, `Extensions`, and `Tokenize(string[] lines) → LineTokens[]`. Available `TokenKind` values: `Text`, `Keyword`, `Type`, `String`, `Comment`, `Number`, `Operator`, `Preprocessor`, `Identifier`, `Attribute`.

## Filetype Edit Assists (smart Enter/Tab)

Filetype-aware editing aids live in `Editor.Core/Editing/` (pure .NET, no WPF):

- **`IEditAssist`** — `AppliesTo(filePath)` + `OnEnter(EditContext)` / `OnTab(EditContext, shift)` (each returning an `EditResult` = `Handled` + new `Cursor`) + `OpenLinePrefix(EditContext, above)` (the prefix to seed an `o`/`O`-opened line, or `null`). `EditAssistBase` declines every hook so implementations override only what they need. The assist mutates `EditContext.Buffer` in place and returns the new caret.
- **`EditAssistRegistry`** — resolves the first assist that applies to a file; `Default` is the process-wide singleton used by `VimEngine`. Add an assist with `Register(...)` (most-recently-registered wins).
- **`MarkdownEditAssist`** — `.md`/`.markdown`: Enter (and `o`/`O`) continues the current list item (`-`/`*`/`+`, or next ordinal for `1.`/`1)`; `O` keeps the ordinal) preserving indent; an empty item on Enter clears the marker (exits the list); Tab/Shift+Tab indent/outdent the list item even with no text yet.

`VimEngine` calls the resolved assist in `InsertNewline` (Enter), `TryEditAssistTab` (Tab), and `OpenLineBelow`/`OpenLineAbove` (`o`/`O`), falling back to default behaviour when the assist declines. The Enter/Tab path covers both Vim Insert mode and the plain (Vim-disabled) edit mode. **To add smart editing for a new filetype, implement `IEditAssist` and register it — no `VimEngine` changes needed.**

## Tests

Tests live in `tests/Editor.Core.Tests/`. Key files: `VimEngineTests.cs` (core vim ops), `TextBufferTests.cs` (buffer mutations), `ExCommandProcessorTests.cs` (`:` commands), `MarkdownEditAssistTests.cs` (edit assists).

Test naming convention: `Subject_Behavior()` (e.g. `DD_DeletesLine`). Engine tests use a `CreateEngine(string text, VimConfig? config = null)` factory helper that returns a configured `VimEngine` with the given initial text. Assertions check `engine.Mode`, `engine.Cursor`, `engine.CurrentBuffer.GetText()`, and event lists via `events.Any(e => e.Type == VimEventType.X)`.
