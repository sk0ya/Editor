using Editor.Core.Buffer;
using Editor.Core.Config;
using Editor.Core.Extensibility;
using Editor.Core.Models;
using Editor.Core.Registers;
using Editor.Core.Spell;
using Editor.Core.Syntax;

namespace Editor.Core.Engine;

/// <summary>
/// Public facade that coordinates input with the internal Vim runtime while
/// exposing the stable host and extension API.
/// </summary>
public class VimEngine
{
    private readonly VimEngineRuntime _runtime;

    public VimEngine(
        VimConfig? config = null,
        SyntaxLanguageRegistry? syntaxLanguages = null,
        EditorCommandRegistry? commands = null,
        IServiceProvider? services = null,
        VimKeyBindingRegistry? keyBindings = null,
        NormalCommandRegistry? normalCommands = null,
        CommandGrammar? commandGrammar = null,
        VimEngineServices? engineServices = null,
        Lsp.ILspServerAdmin? lspServerAdmin = null)
    {
        _runtime = new VimEngineRuntime(
            this,
            config,
            syntaxLanguages,
            commands,
            services,
            keyBindings,
            normalCommands,
            commandGrammar,
            engineServices,
            lspServerAdmin);
    }

    public Func<int, int, int, int, int>? VerticalColumnResolver
    {
        get => _runtime.VerticalColumnResolver;
        set => _runtime.VerticalColumnResolver = value;
    }

    public VimMode Mode => _runtime.Mode;
    public bool VimEnabled => _runtime.VimEnabled;
    public CursorPosition Cursor => _runtime.Cursor;
    public Selection? Selection => _runtime.Selection;
    public string CommandLine => _runtime.CommandLine;
    public string SearchPattern => _runtime.SearchPattern;
    public string StatusMessage => _runtime.StatusMessage;
    public VimOptions Options => _runtime.Options;
    public VimConfig Config => _runtime.Config;
    public SpellChecker SpellChecker => _runtime.SpellChecker;
    public VimBuffer CurrentBuffer => _runtime.CurrentBuffer;
    public BufferManager BufferManager => _runtime.BufferManager;
    public SyntaxEngine Syntax => _runtime.Syntax;
    public ExCommandProcessor ExProcessor => _runtime.ExProcessor;
    public bool FoldsDisabled => _runtime.FoldsDisabled;
    public VimKeyBindingRegistry KeyBindings => _runtime.KeyBindings;
    public NormalCommandRegistry NormalCommands => _runtime.NormalCommands;
    public VimEngineServices Services => _runtime.Services;
    public PendingInputState PendingInput => _runtime.PendingInput;
    public bool HasPendingMappedInput => _runtime.HasPendingMappedInput;

    public ValueTask<EditorCommandResult?> ExecuteExtensionCommandAsync(
        string rawCommand,
        CancellationToken cancellationToken = default) =>
        _runtime.ExecuteExtensionCommandAsync(rawCommand, cancellationToken);

    public string GetSelectionText() => _runtime.GetSelectionText();
    public void SetViewportState(int topLine, int visibleLines) =>
        _runtime.SetViewportState(topLine, visibleLines);
    public void SetClipboardProvider(IClipboardProvider provider) =>
        _runtime.SetClipboardProvider(provider);
    public IReadOnlyList<VimEvent> PasteText(string text, bool after = true) =>
        _runtime.PasteText(text, after);
    public void LoadFoldRanges(IEnumerable<(int StartLine, int EndLine)> ranges) =>
        _runtime.LoadFoldRanges(ranges);
    public void LoadFile(string path) => _runtime.LoadFile(path);
    public void SetText(string text) => _runtime.SetText(text);
    public IReadOnlyList<VimEvent> ApplyExternalText(string text) =>
        _runtime.ApplyExternalText(text);
    public IReadOnlyList<VimEvent> SetCursorPosition(CursorPosition position) =>
        _runtime.SetCursorPosition(position);
    public IReadOnlyList<VimEvent> SetSelection(Selection selection) =>
        _runtime.SetSelection(selection);
    public IReadOnlyList<VimEvent> ProcessKey(
        string key,
        bool ctrl = false,
        bool shift = false,
        bool alt = false) =>
        _runtime.ProcessKey(key, ctrl, shift, alt);
    public IReadOnlyList<VimEvent> ExecuteExCommand(string commandLine) =>
        _runtime.ExecuteExCommand(commandLine);
    public IReadOnlyList<VimEvent> SetVimEnabled(bool enabled) =>
        _runtime.SetVimEnabled(enabled);
    public IReadOnlyList<VimEvent> ProcessKeyLiteral(string key) =>
        _runtime.ProcessKeyLiteral(key);
    public IReadOnlyList<VimEvent> FlushPendingMappings() =>
        _runtime.FlushPendingMappings();
    public IReadOnlyList<VimEvent> SetPlainSelection(
        CursorPosition anchor,
        CursorPosition caret) =>
        _runtime.SetPlainSelection(anchor, caret);
    public IReadOnlyList<VimEvent> ClearPlainSelection() =>
        _runtime.ClearPlainSelection();
    public IReadOnlyList<VimEvent> SetSearchHighlight(string pattern) =>
        _runtime.SetSearchHighlight(pattern);
    public IReadOnlyList<(int Start, int End)> GetSpellErrors(int lineIndex) =>
        _runtime.GetSpellErrors(lineIndex);
}
