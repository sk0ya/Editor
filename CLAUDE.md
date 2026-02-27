# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

```bash
# Build entire solution
dotnet build WVIM.sln

# Run all tests
dotnet test tests/WVim.Core.Tests/

# Run a single test by name
dotnet test tests/WVim.Core.Tests/ --filter "FullyQualifiedName~VimEngineTests.DD_DeletesLine"

# Run the standalone app
dotnet run --project src/WVim.App/

# Build release
dotnet build WVIM.sln -c Release
```

## Architecture

This is a WPF Vim editor split into three layers with a strict dependency rule: **WVim.Core has zero WPF dependencies**.

```
WVim.App → WVim.Controls → WVim.Core
WVim.Core.Tests → WVim.Core
```

### WVim.Core (net9.0 — pure logic)

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

**Clipboard is abstracted** via `IClipboardProvider` (in `WVim.Core.Registers`) so the core has no WPF dependency. The WPF implementation `WpfClipboardProvider` lives in `WVim.Controls`.

### WVim.Controls (net9.0-windows, WPF)

`VimEditorControl` is the public-facing `UserControl`. It owns a `VimEngine` instance and bridges WPF key events to `VimEngine.ProcessKey`, then processes the returned `VimEvent` list to update the UI.

**Rendering:** `EditorCanvas` extends `FrameworkElement` and overrides `OnRender(DrawingContext)` — it does **not** use a `ScrollViewer` (passing Infinity to a FrameworkElement crashes). All scrolling is handled internally via `_scrollOffsetY`/`_scrollOffsetX`. `MeasureOverride` must clamp infinite sizes to finite fallback values.

Key events are translated from `System.Windows.Input.Key` → vim key strings in `GetVimKey(Key, bool shift)`. In Normal/Visual mode all printable keys are captured here; in Insert mode `TextCompositionEventArgs.Text` is used instead.

**Theme:** `EditorTheme` (in `WVim.Controls.Themes`) holds all colors. `EditorTheme.Dracula` is the default. Pass a theme instance to `VimEditorControl.SetTheme(EditorTheme)`.

### WVim.App (net9.0-windows, WPF)

Thin host: `MainWindow` wires `VimEditorControl` events (`SaveRequested`, `QuitRequested`, `OpenFileRequested`) to file dialogs and tab management. Command-line arguments are read in `Window_Loaded` — the first arg is treated as a file path to open.

## Adding a New Vim Command

1. **Normal mode motion** — add a case to `MotionEngine.Calculate(string, CursorPosition, int)` and handle the resulting `Motion` in `VimEngine.ExecuteNormalCommand`.
2. **Normal mode action** — add a `case "x":` branch directly in `VimEngine.ExecuteNormalCommand`.
3. **Ex command** — add a branch in `ExCommandProcessor.Execute`. Return `new ExResult(true, null, VimEvent.XxxRequested(...))` to communicate with the host app.
4. **New VimEvent type** — add to the `VimEventType` enum in `VimEvent.cs`, add a factory method and record subclass, then handle it in `VimEditorControl.ProcessVimEvents`.

## Adding Syntax Highlighting for a New Language

Implement `ISyntaxLanguage` (in `WVim.Core.Syntax`) and register the instance in the array inside `SyntaxEngine`. The interface requires `Name`, `Extensions`, and `Tokenize(string[] lines) → LineTokens[]`.
