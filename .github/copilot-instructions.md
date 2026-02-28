## Purpose

Short, actionable guidance to help an AI coding agent be productive in this repository.

## Quick workspace commands (Windows / PowerShell)

- Build solution: `dotnet build Editor.sln`
- Run all tests (core tests): `dotnet test tests/Editor.Core.Tests/`
- Run a single test: `dotnet test tests/Editor.Core.Tests/ --filter "FullyQualifiedName~VimEngineTests.DD_DeletesLine"`
- Run the UI app: `dotnet run --project src/Editor.App/`

## Big picture (must-know)

- Projects: `Editor.App` (WPF host) → `Editor.Controls` (WPF controls, rendering) → `Editor.Core` (pure logic). The dependency rule is strict: Editor.Core contains NO WPF or platform APIs.
- Core API: drive the editor via `VimEngine.ProcessKey(string key, bool ctrl, bool shift, bool alt)` → returns `IReadOnlyList<VimEvent>` which callers apply to UI/state.

## Key files & entry points (examples an agent will edit or call)

- Vim engine: `src/Editor.Core/Engine/VimEngine.cs` (normal/insert/motion dispatch)
- Parser: `src/Editor.Core/Engine/CommandParser.cs` (accumulates counts/operators/motions)
- Motion logic: `src/Editor.Core/Engine/MotionEngine.cs` (stateless calculations; takes `TextBuffer`)
- Ex commands: `src/Editor.Core/Engine/ExCommandProcessor.cs`
- Buffer model: `src/Editor.Core/Buffer/TextBuffer.cs`, undo in `UndoManager.cs`
- Controls & rendering: `src/Editor.Controls/VimEditorControl.xaml.cs` and `src/Editor.Controls/Rendering/EditorCanvas.cs`
- App host: `src/Editor.App/MainWindow.xaml(.cs)` wires `VimEditorControl` events to file dialogs/tabs

## Project-specific patterns and gotchas

- No UI in core: use `IClipboardProvider`/Registers to interact with platform clipboard. If you need clipboard behavior in core, add or use an `IClipboardProvider` instance.
- Motion prefixes: `g` and `z` are motion prefixes (not operators). Check `CommandParser` parsing logic before adding new operators.
- Double-operator commands (e.g. `dd`, `yy`) are recognized by `ParsedCommand.LinewiseForced` and must be handled early in `VimEngine.ExecuteNormalCommand`.
- Rendering: `EditorCanvas` manages its own scrolling offsets; do NOT assume a `ScrollViewer` is wrapping the canvas.
- Input translation: keys are translated from WPF `Key` values to Vim key strings in `VimEditorControl` (look for `GetVimKey`).

## How to add changes safely (recommended workflow)

1. Modify core logic in `src/Editor.Core` and add or update unit tests in `tests/Editor.Core.Tests/`.
2. Run `dotnet test` locally and ensure green before touching `Editor.Controls` or `Editor.App`.
3. When changing events sent from core, update `VimEvent` definitions in `src/Editor.Core/Engine/VimEvent.cs` and adjust `VimEditorControl.ProcessVimEvents`.
4. Keep changes small and include at least one focused test covering the behavioural change.

## Examples of common edits

- Add Normal-mode command: update `VimEngine.ExecuteNormalCommand` and add targeted tests in `VimEngineTests.cs`.
- Add Motion: add logic in `MotionEngine.Calculate` and adapt any callers in `VimEngine`/`CommandParser`.
- Add Ex command: add a branch in `ExCommandProcessor.Execute` and return an `ExResult` with a `VimEvent` to notify the host.

## Tests and verification

- Unit tests live in `tests/Editor.Core.Tests/` and target core behaviour. Use test filters to run a specific test quickly.
- Prefer small, deterministic unit tests for engine and buffer logic (no WPF required).

## Where to look first when debugging

- Key-to-event flow: `VimEditorControl` → `VimEngine.ProcessKey` → `CommandParser`/`MotionEngine`/`ExCommandProcessor` → `VimEvent` → `VimEditorControl.ProcessVimEvents`.
- Buffer edits: `TextBuffer.cs` and `UndoManager.cs`.

## Style / commit guidance for AI agents

- Keep each change focused and include tests for core logic changes.
- Mention the edited csproj(s) only if adding package references; prefer using framework libs already in the solution.

If anything here is unclear or you'd like the file tuned for a different agent style (more examples, stricter rules, or a checklist), tell me which parts to expand.
