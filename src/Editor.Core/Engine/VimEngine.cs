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

    /// <summary>
    /// ホストが指定する読み取り専用。移動・選択・ヤンク・検索はそのままで、本文を変える操作だけを
    /// 断る（バイナリファイルと同じ E21）。差分表示の旧側のように「読んでコピーはするが書き換えない」
    /// 面に使う。<see cref="SetText"/> などホストからの差し替えは対象外。
    /// </summary>
    public bool IsReadOnly
    {
        get => _runtime.IsReadOnly;
        set => _runtime.IsReadOnly = value;
    }
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
    /// <summary>Opens a file whose disk side was already read off the caller's thread
    /// (<see cref="PreparedFileLoad.Prepare"/>).</summary>
    public void LoadFile(string path, PreparedFileLoad? prepared) => _runtime.LoadFile(path, prepared);
    public void RebaseFilePath(string newPath) => _runtime.RebaseFilePath(newPath);
    public void SetText(string text) => _runtime.SetText(text);
    public IReadOnlyList<VimEvent> ApplyExternalText(string text) =>
        _runtime.ApplyExternalText(text);
    /// <summary>Host transaction rollback用。Undo履歴へ新しいcheckpointを追加しない。</summary>
    public IReadOnlyList<VimEvent> RestoreExternalText(string text) =>
        _runtime.RestoreExternalText(text);
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
    public IReadOnlyList<VimEvent> CompleteStatement() => _runtime.CompleteStatement();

    /// <summary>
    /// 連動編集（タグ名の開き/閉じを同時に書き換える）の範囲をホストが供給する口。引数は編集前の
    /// (行, 列, <see cref="Buffer.TextBuffer.Version"/>) で、ホストはその版に対して言語サーバーへ
    /// <c>textDocument/linkedEditingRange</c> を先回りで問い合わせておいた答えを同期的に返す。
    /// null は「答えを持っていない」（エンジンはマークアップ用の自前フォールバックを試す）、
    /// <see cref="Editing.LinkedEditingRanges.None"/> は「範囲は無い」。
    /// 有効/無効は <c>set linkedediting</c>（<see cref="Config.VimOptions.LinkedEditing"/>）。
    /// </summary>
    public Func<int, int, long, Editing.LinkedEditingRanges?>? LinkedEditingRangeProvider
    {
        get => _runtime.LinkedEditingRangeProvider;
        set => _runtime.LinkedEditingRangeProvider = value;
    }

    /// <summary>true の間は連動編集を始めない（進行中のものも解く）。マルチカーソル中など。</summary>
    public bool LinkedEditingSuspended
    {
        get => _runtime.LinkedEditingSuspended;
        set => _runtime.LinkedEditingSuspended = value;
    }

    /// <summary>いま連動している範囲（描画用）。連動していなければ空。</summary>
    public IReadOnlyList<Editing.LinkedRange> LinkedEditingRanges => _runtime.LinkedEditingRanges;

    /// <summary>進行中の連動編集を解く。</summary>
    public void EndLinkedEditing() => _runtime.EndLinkedEditing();
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
