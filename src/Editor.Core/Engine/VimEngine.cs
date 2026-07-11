using System.IO;
using System.Text;
using Editor.Core.Buffer;
using Editor.Core.Config;
using Editor.Core.Editing;
using Editor.Core.Macros;
using Editor.Core.Marks;
using Editor.Core.Models;
using Editor.Core.Registers;
using Editor.Core.Spell;
using Editor.Core.Syntax;
using Editor.Core.Engine.Ops;
using Editor.Core;
using Editor.Core.Extensibility;

namespace Editor.Core.Engine;

public class VimEngine
{
    private readonly BufferManager _bufferManager;
    private readonly RegisterManager _registerManager;
    private readonly MarkManager _markManager;
    private readonly MacroManager _macroManager;
    private readonly ExCommandProcessor _exProcessor;
    private AutocmdRunner _autocmdRunner = null!;
    private readonly SyntaxEngine _syntaxEngine;
    private readonly CommandParser _commandParser;
    private readonly VimConfig _config;
    private readonly Commands.LspTriggerCommands _lspTriggerCommands = new();
    private readonly Commands.FoldCommands _foldCommands;
    private readonly Commands.FileNavCommands _fileNavCommands;
    private readonly ClipboardEditOps _clipboardOps;
    private readonly TextTransformOps _textTransform;
    private readonly RepeatTracker _repeatTracker;
    private readonly SearchOps _searchOps;
    private readonly KeywordCompletionOps _kwCompletionOps;
    private readonly BlockInsertOps _blockInsertOps;
    private readonly VisualEditOps _visualEditOps;
    private readonly NormalCommandExecutor _normalCmdExecutor;
    private readonly SpellChecker _spellChecker = new();
    private readonly EditAssistRegistry _editAssists = EditAssistRegistry.Default;

    private VimMode _mode = VimMode.Normal;
    private bool _vimEnabled = true;
    // Plain (Vim-disabled) editing groups a run of edits into a single undo step;
    // cursor movement ends the run so the next edit starts a fresh undo group.
    private bool _plainEditRunActive;
    // Plain selection is tracked as half-open caret boundaries [anchor, caret)
    // (standard text-box semantics), then converted to an inclusive cell Selection
    // for rendering/yank/delete. _plainSelActive marks an in-progress selection.
    private bool _plainSelActive;
    private CursorPosition _plainSelAnchor;
    private CursorPosition _cursor = CursorPosition.Zero;
    private bool _suppressSnapshot;
    private Selection? _selection;
    private CursorPosition _visualStart;

    private string _cmdLine = "";   // For : / ? modes
    private string _statusMsg = "";
    private string[] _completions = [];     // Tab completion candidates
    private int _completionIndex = -1;      // Currently selected completion (-1 = none)

    private char? _pendingReplaceChar;
    private bool _awaitingMark;
    private bool _awaitingMarkJump;
    private bool _awaitingMarkJumpLine;
    private bool _ctrlWPending;
    private char _awaitingVisualTextObj;  // 'i' or 'a' when pending text object in Visual mode
    private int _awaitingVisualTextObjCount = 1;
    private bool _discardingUnsupportedVisualTextObj;
    private bool _awaitingSurroundChar;   // ys{motion} — waiting for the surround character
    private bool _awaitingBlockReplace;   // Visual Block r — waiting for the replacement character
    private bool _awaitingVisualRegister; // Visual mode " — waiting for the register name
    private char? _visualPendingRegister; // Register selected via " for the next visual operator
    private bool _visualBlockToLineEnd;   // Ctrl+V $ — selected lines extend to their own EOL
    private int _visualBlockLineEndStartColumn;
    private bool _pendingInsertReturn;    // Ctrl+O in Insert mode — return to Insert after one Normal command
    private bool _awaitingInsertRegister; // Ctrl+R in Insert mode — waiting for register name
    private bool _awaitingExprRegister;   // Ctrl+R = in Insert mode — accumulating expression
    private string _exprBuffer = "";      // accumulated expression input
    private char? _digraphPendingChar;     // non-null = awaiting digraph input; holds first char once entered (null char = awaiting first)
    private bool _ctrlXPending;           // Ctrl+X sub-mode: waiting for F (file) or L (line)
    private char _ctrlXMode;              // 'f' = file-path, 'l' = whole-line, '\0' = keyword
    private CursorPosition _surroundStart, _surroundEnd;
    private bool _surroundLinewise;
    private bool _foldDisabled;
    private int _preferredColumn = 0; // Sticky column for j/k
    private int _preferredLine = 0;

    /// <summary>
    /// Optional display-layer resolver for vertical motions. The arguments are the line and
    /// column where the sticky goal was established, the target line, and its maximum column.
    /// Core falls back to logical-column movement when no resolver is supplied.
    /// </summary>
    public Func<int, int, int, int, int>? VerticalColumnResolver { get; set; }

    private int _viewportTopLine = 0;      // First visible buffer line in the viewport
    private int _viewportVisibleLines = 25; // Number of lines visible in the viewport

    private CursorPosition _insertStart;
    private CursorPosition _lastInsertPos;
    private StringBuilder? _insertedText; // literal text typed in the current Insert/Replace session, flushed to the "." register on exit
    private CursorPosition _lastVisualStart;
    private CursorPosition _lastVisualEnd;
    private VimMode _lastVisualMode = VimMode.Visual;
    private readonly List<VimKeyStroke> _pendingMappedInput = [];

    public VimMode Mode => _mode;

    /// <summary>
    /// When false, modal Vim key handling is bypassed and the engine stays in a
    /// plain (non-modal) insert state so the control behaves like an ordinary
    /// text editor. Defaults to true. Toggle via <see cref="SetVimEnabled"/>.
    /// </summary>
    public bool VimEnabled => _vimEnabled;

    public CursorPosition Cursor => _cursor;
    public Selection? Selection => _selection;
    public string CommandLine => _cmdLine;
    public string SearchPattern => _searchOps.Pattern;
    public string StatusMessage => _statusMsg;
    public VimOptions Options => _config.Options;
    public VimConfig Config => _config;
    public SpellChecker SpellChecker => _spellChecker;
    public VimBuffer CurrentBuffer => _bufferManager.Current;
    public BufferManager BufferManager => _bufferManager;
    public SyntaxEngine Syntax => _syntaxEngine;
    public ExCommandProcessor ExProcessor => _exProcessor;
    public bool FoldsDisabled => _foldDisabled;

    /// <summary>Executes a registered synchronous or asynchronous command from a raw Ex line.</summary>
    public ValueTask<EditorCommandResult?> ExecuteExtensionCommandAsync(string rawCommand,
        CancellationToken cancellationToken = default)
        => _exProcessor.ExecuteExtensionAsync(rawCommand, _cursor, cancellationToken);

    /// <summary>
    /// Returns the text covered by the current visual selection, honouring the
    /// selection type (character/line/block). Returns an empty string when there
    /// is no active selection. This has no side effects (registers untouched).
    /// </summary>
    public string GetSelectionText()
    {
        if (_selection is not { } sel || sel.IsEmpty) return string.Empty;
        var buf = _bufferManager.Current.Text;
        var start = sel.NormalizedStart;
        var end = sel.NormalizedEnd;

        switch (sel.Type)
        {
            case SelectionType.Line:
                return string.Join("\n", buf.GetLines(start.Line, end.Line));

            case SelectionType.Block:
            {
                var lines = new List<string>();
                foreach (var range in GetBlockLineRanges(sel))
                {
                    var text = buf.GetLine(range.Line);
                    if (text.Length <= range.StartColumn) { lines.Add(""); continue; }
                    var endExclusive = Math.Min(text.Length, range.EndColumn + 1);
                    lines.Add(text[range.StartColumn..endExclusive]);
                }
                return string.Join("\n", lines);
            }

            default: // Character
                if (start.Line == end.Line)
                {
                    int endCol = Math.Min(end.Column + 1, buf.GetLineLength(start.Line));
                    return buf.GetLine(start.Line)[start.Column..endCol];
                }
                var sb = new System.Text.StringBuilder();
                sb.Append(buf.GetLine(start.Line)[start.Column..]);
                for (int l = start.Line + 1; l < end.Line; l++)
                    sb.Append('\n').Append(buf.GetLine(l));
                sb.Append('\n').Append(buf.GetLine(end.Line)[..Math.Min(end.Column + 1, buf.GetLineLength(end.Line))]);
                return sb.ToString();
        }
    }

    /// <summary>Sets the viewport state so H/M/L motions target correct visible lines.</summary>
    public void SetViewportState(int topLine, int visibleLines)
    {
        _viewportTopLine = Math.Max(0, topLine);
        _viewportVisibleLines = Math.Max(1, visibleLines);
    }

    public VimEngine(VimConfig? config = null, SyntaxLanguageRegistry? syntaxLanguages = null,
        EditorCommandRegistry? commands = null, IServiceProvider? services = null)
    {
        _config = config ?? new VimConfig();
        _bufferManager = new BufferManager();
        _bufferManager.BufferWillWrite += (_, path) => _autocmdRunner.RunAutocmds("BufWritePre", path);
        _bufferManager.BufferDidWrite += (_, path) => _autocmdRunner.RunAutocmds("BufWritePost", path);
        _registerManager = new RegisterManager(_config.Options);
        _markManager = new MarkManager();
        _macroManager = new MacroManager();
        _syntaxEngine = new SyntaxEngine(syntaxLanguages);
        _commandParser = new CommandParser();
        _exProcessor = new ExCommandProcessor(_bufferManager, _config.Options, _markManager, _config.Abbreviations, _registerManager,
            _config.NormalMaps, _config.InsertMaps, _config.VisualMaps, _config.Variables, _config.ScriptNames, _config.Functions,
            commandRegistry: commands, services: services);
        _autocmdRunner = new AutocmdRunner(_config, _exProcessor, () => _cursor);
        _foldCommands = new Commands.FoldCommands(_bufferManager);
        _fileNavCommands = new Commands.FileNavCommands(_bufferManager);
        _clipboardOps = new ClipboardEditOps(_bufferManager, _registerManager, Snapshot,
            (events, cursor) => { _cursor = cursor; EmitText(events); }, EmitStatus);
        _textTransform = new TextTransformOps(_bufferManager, _syntaxEngine, _config.Options, Snapshot,
            EmitText, (events, cursor) => { _cursor = cursor; EmitText(events); }, EmitCursor, EmitStatus,
            (pos, events) => MoveCursor(pos, events));
        _repeatTracker = new RepeatTracker(_registerManager, ExecuteNormalCommand, ProcessKeyInternal, ProcessStroke);
        _searchOps = new SearchOps(_bufferManager, _markManager, _config.Options, _commandParser,
            (pos, events) => MoveCursor(pos, events), EmitStatus);
        _kwCompletionOps = new KeywordCompletionOps(_bufferManager,
            (events, cursor) => { _cursor = cursor; EmitText(events); }, EmitStatus);
        _blockInsertOps = new BlockInsertOps(_bufferManager, _config.Options,
            (events, cursor) => { _cursor = cursor; EmitText(events); });
        _visualEditOps = new VisualEditOps(_bufferManager, _registerManager, _clipboardOps, _textTransform, _repeatTracker,
            () => _selection, () => _mode, () => _visualBlockToLineEnd, () => _visualBlockLineEndStartColumn,
            cursor => _cursor = cursor, Snapshot, EmitText, ExitVisualMode, MoveCursor);
        _normalCmdExecutor = new NormalCommandExecutor(_bufferManager, _exProcessor, _commandParser,
            () => _cursor, cursor => _cursor = cursor, () => _mode, mode => _mode = mode,
            suppress => _suppressSnapshot = suppress, ProcessStroke, ProcessKeyInternal, EmitText, EmitCursor);
    }

    public void SetClipboardProvider(IClipboardProvider provider)
    {
        _registerManager.SetClipboardProvider(provider);
    }

    /// <summary>
    /// Inserts <paramref name="text"/> as a characterwise paste at the cursor, honouring
    /// the current mode: Insert mode inserts at the caret (like Ctrl+V), Normal/Visual mode
    /// pastes after the cursor (like <c>p</c>). Used by the host for synthesised pastes such
    /// as saving a clipboard image and dropping a Markdown link in its place. Participates
    /// in undo and emits TextChanged/CursorMoved, so callers can treat it like a keypress.
    /// </summary>
    public IReadOnlyList<VimEvent> PasteText(string text, bool after = true)
    {
        var events = new List<VimEvent>();
        if (string.IsNullOrEmpty(text)) return events;

        if (_mode == VimMode.Insert)
        {
            Snapshot();
            InsertTextAtCursor(text, events);
        }
        else
        {
            _cursor = _clipboardOps.PasteRawText(_cursor, text, after, events);
        }
        return events;
    }

    /// <summary>
    /// LSPのfoldingRangeレスポンスを現在のバッファに適用する。
    /// 既存の開閉状態は同じ範囲のフォールドに引き継がれる。
    /// </summary>
    public void LoadFoldRanges(IEnumerable<(int StartLine, int EndLine)> ranges)
    {
        CurrentBuffer.Folds.SetLspRanges(ranges);
    }

    public void LoadFile(string path)
    {
        if (_bufferManager.Current.FilePath is { } outgoingPath)
            _autocmdRunner.RunAutocmds("BufLeave", outgoingPath);

        // Real Vim skips BufReadPre/BufRead/BufReadPost for a path that doesn't exist yet and
        // fires BufNewFile instead; BufEnter/FileType still fire normally either way.
        bool isNewFile = !File.Exists(path);
        if (!isNewFile)
            _autocmdRunner.RunAutocmds("BufReadPre", path);
        var editorConfig = EditorConfig.LoadForFile(path);
        editorConfig.TryGetFileEncoding(out var preferredEncoding);
        _bufferManager.OpenFile(path, preferredEncoding);
        _cursor = CursorPosition.Zero;
        _registerManager.SetCurrentFileName(_bufferManager.Current.FilePath); // "%" register — current buffer's path
        _syntaxEngine.DetectLanguage(path);
        _syntaxEngine.Invalidate();
        _bufferManager.Current.Undo.Clear();
        // Sync detected file format and encoding to options so :set ff?/:set fenc? reflects the loaded file.
        _config.Options.FileFormat   = _bufferManager.Current.FileFormat;
        _config.Options.FileEncoding = _bufferManager.Current.FileEncoding;
        editorConfig.ApplyTo(_config.Options, _bufferManager.Current);
        _config.ApplyModelines(_bufferManager.Current);
        if (isNewFile)
        {
            _autocmdRunner.RunAutocmds("BufNewFile", path);
        }
        else
        {
            _autocmdRunner.RunAutocmds("BufRead", path);
            _autocmdRunner.RunAutocmds("BufReadPost", path);
        }
        _autocmdRunner.RunAutocmds("BufEnter", path);
        _autocmdRunner.RunAutocmds("FileType", AutocmdRunner.GetFileTypeNames(path));
    }

    /// <summary>
    /// Replace the whole buffer, keeping the caret on the same line/column as best it can,
    /// clamping to the new buffer's bounds. Callers that want the caret at the top of a fresh
    /// document (e.g. opening a new file) should set the cursor explicitly afterward.
    /// </summary>
    public void SetText(string text)
    {
        var old = _cursor;
        _bufferManager.Current.Text.SetText(text);
        _syntaxEngine.Invalidate();

        var buf = CurrentBuffer.Text;
        int line = Math.Clamp(old.Line, 0, buf.LineCount - 1);
        int lineLen = buf.GetLine(line).Length;
        int maxCol = _mode == VimMode.Insert ? lineLen : Math.Max(0, lineLen - 1);
        int col = Math.Clamp(old.Column, 0, maxCol);
        _cursor = new CursorPosition(line, col);
        SetPreferredColumn(col);
    }

    // Move cursor to an arbitrary position (used for mouse click).
    public IReadOnlyList<VimEvent> SetCursorPosition(CursorPosition pos)
    {
        var buf = CurrentBuffer;
        int line = Math.Clamp(pos.Line, 0, buf.Text.LineCount - 1);
        int lineLen = buf.Text.GetLine(line).Length;
        bool insertMode = _mode == VimMode.Insert;
        int maxCol = insertMode ? lineLen : Math.Max(0, lineLen - 1);
        int col = Math.Clamp(pos.Column, 0, maxCol);
        _cursor = new CursorPosition(line, col);
        SetPreferredColumn(col);

        var events = new List<VimEvent> { VimEvent.CursorMoved(_cursor) };

        if (_mode is VimMode.Visual or VimMode.VisualLine or VimMode.VisualBlock)
        {
            _selection = new Selection(_visualStart, _cursor, _mode switch
            {
                VimMode.VisualLine => SelectionType.Line,
                VimMode.VisualBlock => SelectionType.Block,
                _ => SelectionType.Character,
            });
            events.Add(VimEvent.SelectionChanged(_selection));
        }

        return events;
    }

    public IReadOnlyList<VimEvent> SetSelection(Selection selection)
    {
        var events = new List<VimEvent>();
        var targetMode = selection.Type switch
        {
            SelectionType.Line => VimMode.VisualLine,
            SelectionType.Block => VimMode.VisualBlock,
            _ => VimMode.Visual
        };

        _visualStart = CurrentBuffer.Text.ClampCursor(selection.Start);
        _cursor = CurrentBuffer.Text.ClampCursor(selection.End);
        SetPreferredColumn(_cursor.Column);

        if (_mode != targetMode)
            ChangeMode(targetMode, events);

        UpdateSelection(events);
        return events;
    }

    // Process a key stroke and return events to update the UI
    public IReadOnlyList<VimEvent> ProcessKey(string key, bool ctrl = false, bool shift = false, bool alt = false)
    {
        var events = new List<VimEvent>();
        ProcessStroke(new VimKeyStroke(key, ctrl, shift, alt), events, allowMapping: true);
        return events;
    }

    /// <summary>
    /// Executes an ex command line (e.g. "Gblame", "w", "1,5d") directly, without
    /// synthesizing ':' keystrokes. Unlike feeding keys through <see cref="ProcessKey"/>,
    /// this works in any mode (including Insert) and also while <see cref="VimEnabled"/>
    /// is false, so hosts can drive ex commands programmatically (menu items, toolbar
    /// buttons) without the keys being inserted into the buffer as text. A leading ':'
    /// is tolerated. The command is not added to the command-line history, and the
    /// current mode is preserved. Returns the events the host should process.
    /// </summary>
    public IReadOnlyList<VimEvent> ExecuteExCommand(string cmdLine)
    {
        var events = new List<VimEvent>();
        cmdLine = (cmdLine ?? "").TrimStart(':').Trim();
        if (cmdLine.Length == 0) return events;
        ExecuteExCommand(cmdLine, events);
        return events;
    }

    /// <summary>
    /// Enables or disables Vim key handling. When disabled, the engine drops into
    /// a plain insert (non-modal) state where keys insert text like an ordinary
    /// editor and Escape no longer leaves insert mode. When re-enabled, the engine
    /// returns to Normal mode. Returns the events the host should process to
    /// refresh its UI (mode, status line, selection).
    /// </summary>
    public IReadOnlyList<VimEvent> SetVimEnabled(bool enabled)
    {
        var events = new List<VimEvent>();
        if (_vimEnabled == enabled)
            return events;

        // Clear any pending modal state regardless of direction.
        _commandParser.Reset();
        _pendingMappedInput.Clear();
        if (_selection != null)
        {
            _selection = null;
            events.Add(VimEvent.SelectionChanged(null));
        }

        // If a command/search line is open, dismiss it so no stale ':' text lingers.
        if (_mode is VimMode.Command or VimMode.SearchForward or VimMode.SearchBackward)
        {
            _cmdLine = "";
            events.Add(VimEvent.CommandLineChanged(""));
        }

        _vimEnabled = enabled;
        _plainEditRunActive = false;
        _plainSelActive = false;

        if (!enabled)
        {
            // Present an Insert-mode resting state to the host so it renders a
            // text caret and routes IME/text input here. Key *handling* while
            // disabled is done entirely by HandlePlainTextKey via the gate in
            // ProcessStroke — this mode value is for the host's benefit only.
            _insertStart = _cursor;
            ChangeMode(VimMode.Insert, events);
        }
        else if (_mode == VimMode.Insert || _mode == VimMode.Replace)
        {
            // Cleanly return to Normal mode (Vim's resting state).
            ExitInsertMode(events);
        }
        else
        {
            ChangeMode(VimMode.Normal, events);
        }

        EmitStatus(events, "");
        return events;
    }

    // Like ProcessKey but bypasses insert/normal-mode mappings entirely.
    // Used to flush keys that were held back from the IME but ultimately
    // did not complete any mapped sequence.
    public IReadOnlyList<VimEvent> ProcessKeyLiteral(string key)
    {
        var events = new List<VimEvent>();
        ProcessStroke(new VimKeyStroke(key, false, false, false), events, allowMapping: false);
        return events;
    }

    // True when a multi-key mapping (e.g. `jj`) is half-typed and the engine is
    // holding the prefix waiting for the next key. The host arms a 'timeoutlen'
    // timer while this is set so a dangling prefix is eventually emitted as
    // literal text (Vim's 'timeout' behaviour) instead of being swallowed.
    public bool HasPendingMappedInput => _pendingMappedInput.Count > 0;

    // Flush a half-typed mapping prefix as literal input. Called by the host
    // when the 'timeoutlen' timer fires with no further key. No-op if nothing
    // is pending.
    public IReadOnlyList<VimEvent> FlushPendingMappings()
    {
        var events = new List<VimEvent>();
        if (_pendingMappedInput.Count > 0)
            FlushPendingMappedInput(events);
        return events;
    }

    private void ProcessStroke(VimKeyStroke stroke, List<VimEvent> events, bool allowMapping)
    {
        // Single decision point for the Vim-disabled (plain editor) state. Every
        // key is routed to one minimal handler — no modal mappings, macro
        // recording, pastetoggle, or Vim insert-mode key semantics run. This
        // replaces the previous approach of forcing Insert mode and patching its
        // individual exits, which leaked modal behaviour (e.g. Ctrl+A → Visual).
        if (!_vimEnabled)
        {
            HandlePlainTextKey(stroke.Key, stroke.Ctrl, stroke.Shift, stroke.Alt, events);
            return;
        }

        if (allowMapping && TryApplyMapping(stroke, events))
            return;

        var modeBefore = _mode;

        // Macro recording
        if (_macroManager.IsRecording && !(stroke.Key == "q" && !stroke.Ctrl && !stroke.Shift))
            _macroManager.RecordKey(stroke);

        ProcessKeyInternal(stroke.Key, stroke.Ctrl, stroke.Shift, stroke.Alt, events);
        SyncSpellChecker();
        _repeatTracker.TrackPendingInsertRepeat(stroke, modeBefore, _mode);
        _repeatTracker.TrackPendingVisualRepeat(stroke, modeBefore, _mode, events);
    }

    private bool TryApplyMapping(VimKeyStroke stroke, List<VimEvent> events)
    {
        var maps = GetMapsForMode(_mode);
        if ((maps == null || maps.Count == 0) && _pendingMappedInput.Count == 0)
            return false;

        _pendingMappedInput.Add(stroke);

        while (_pendingMappedInput.Count > 0)
        {
            maps = GetMapsForMode(_mode);
            if (maps == null || maps.Count == 0)
            {
                FlushPendingMappedInput(events);
                return true;
            }

            var match = KeyMappingResolver.ResolveMapMatch(maps, _pendingMappedInput);
            if (match.HasExactMatch)
            {
                if (match.HasLongerPrefix)
                    return true;

                _pendingMappedInput.Clear();
                foreach (var mappedStroke in KeyMappingResolver.ParseMappingSequence(match.MappedValue ?? ""))
                    ProcessStroke(mappedStroke, events, allowMapping: false);
                return true;
            }

            if (match.HasPrefix)
                return true;

            var literal = _pendingMappedInput[0];
            _pendingMappedInput.RemoveAt(0);
            ProcessStroke(literal, events, allowMapping: false);

            // If the command parser is now mid-sequence (e.g. buffer == "g" after
            // flushing the first 'g'), the remaining pending keys must also be
            // dispatched immediately as literals.  Without this, a user map that
            // starts with 'g' (like nnoremap gf …) causes the second 'g' of 'gg'
            // to be re-held as a potential map prefix, requiring an extra keypress.
            if (!string.IsNullOrEmpty(_commandParser.Buffer))
                FlushPendingMappedInput(events);
        }

        return true;
    }

    private Dictionary<string, string>? GetMapsForMode(VimMode mode) => mode switch
    {
        VimMode.Normal => _config.NormalMaps,
        VimMode.Insert or VimMode.Replace => _config.InsertMaps,
        VimMode.Visual or VimMode.VisualLine or VimMode.VisualBlock => _config.VisualMaps,
        _ => null
    };

    private void FlushPendingMappedInput(List<VimEvent> events)
    {
        while (_pendingMappedInput.Count > 0)
        {
            var literal = _pendingMappedInput[0];
            _pendingMappedInput.RemoveAt(0);
            ProcessStroke(literal, events, allowMapping: false);
        }
    }

    private void ProcessKeyInternal(string key, bool ctrl, bool shift, bool alt, List<VimEvent> events)
    {
        // pastetoggle — works in any mode
        var pt = _config.Options.PasteToggle;
        if (pt.Length > 0 && key == pt && !ctrl && !shift && !alt)
        {
            _config.Options.Paste = !_config.Options.Paste;
            EmitStatus(events, _config.Options.Paste ? "-- INSERT (paste) --" : "");
            return;
        }

        switch (_mode)
        {
            case VimMode.Normal:
                HandleNormal(key, ctrl, shift, alt, events);
                break;
            case VimMode.Insert:
            case VimMode.Replace:
                HandleInsert(key, ctrl, shift, alt, events);
                break;
            case VimMode.Visual:
            case VimMode.VisualLine:
            case VimMode.VisualBlock:
                HandleVisual(key, ctrl, shift, alt, events);
                break;
            case VimMode.Command:
            case VimMode.SearchForward:
            case VimMode.SearchBackward:
                HandleCommandLine(key, ctrl, shift, alt, events);
                break;
        }
    }

    // ─────────────── NORMAL MODE ───────────────
    private void HandleNormal(string key, bool ctrl, bool shift, bool alt, List<VimEvent> events)
    {
        var buf = _bufferManager.Current.Text;
        var motion = new MotionEngine(buf, _bufferManager.Current.FilePath);

        // Ctrl+W two-key window prefix
        if (_ctrlWPending)
        {
            _ctrlWPending = false;
            if (key == "Escape") { EmitStatus(events, ""); return; }
            HandleCtrlWSecondKey(key, ctrl, events);
            return;
        }

        // Awaiting pending chars
        if (_awaitingMark)
        {
            // '.' and '\'' are auto-managed; block user from clobbering them via m'/m.
            if (key[0] != '\'' && key[0] != '.')
                _markManager.SetMark(key[0], _cursor);
            _awaitingMark = false;
            EmitCursor(events);
            return;
        }
        if (_awaitingMarkJump)
        {
            var markKey = key[0] == '`' ? '\'' : key[0]; // `` → jump-from mark
            var m = _markManager.GetMark(markKey);
            if (m.HasValue) { _markManager.SetMark('\'', _cursor); MoveCursor(m.Value, events); }
            _awaitingMarkJump = false;
            return;
        }
        if (_awaitingMarkJumpLine)
        {
            var m = _markManager.GetMark(key[0]);
            if (m.HasValue) { _markManager.SetMark('\'', _cursor); MoveCursor(m.Value with { Column = 0 }, events); }
            _awaitingMarkJumpLine = false;
            return;
        }
        if (_pendingReplaceChar.HasValue) { _cursor = _textTransform.ExecuteReplace(_cursor, key[0], events); _pendingReplaceChar = null; return; }
        if (_awaitingSurroundChar)
        {
            _awaitingSurroundChar = false;
            if (key.Length == 1) _cursor = _textTransform.ApplySurround(_surroundStart, _surroundEnd, _surroundLinewise, key[0], events);
            return;
        }

        // Escape while _pendingInsertReturn is set: cancel Ctrl+O, stay in Normal
        if (key == "Escape" && _pendingInsertReturn)
        {
            _pendingInsertReturn = false;
            _commandParser.Reset();
            EmitStatus(events, "");
            return;
        }

        // Ctrl keys
        if (ctrl)
        {
            HandleNormalCtrl(key, events);
            MaybeReturnToInsertAfterCtrlO(events);
            return;
        }

        // Feed to command parser
        var (state, cmd) = _commandParser.Feed(key);

        if (state == CommandState.Incomplete)
        {
            EmitStatus(events, _commandParser.Buffer);
            return;
        }
        if (state == CommandState.Invalid || cmd == null)
        {
            // Invalid key during Ctrl+O: cancel the pending return so the flag doesn't leak.
            _pendingInsertReturn = false;
            EmitStatus(events, "");
            return;
        }

        ExecuteNormalCommand(cmd.Value, events);
        // Ctrl+O: return to Insert after one Normal command.
        // Commands like c/i that already enter Insert are left alone (_mode != Normal).
        MaybeReturnToInsertAfterCtrlO(events);
    }

    private void MaybeReturnToInsertAfterCtrlO(List<VimEvent> events)
    {
        if (!_pendingInsertReturn) return;
        _pendingInsertReturn = false;
        if (_mode == VimMode.Normal)
            ChangeMode(VimMode.Insert, events, suppressInsertAutocmd: true);
    }

    private void HandleNormalCtrl(string key, List<VimEvent> events)
    {
        var buf = _bufferManager.Current.Text;
        var motion = new MotionEngine(buf, _bufferManager.Current.FilePath);

        switch (key.ToLower())
        {
            case "d": ScrollHalfPage(true, events); break;
            case "u": ScrollHalfPage(false, events); break;
            case "f": ScrollPage(true, events); break;
            case "b": ScrollPage(false, events); break;
            case "e": ScrollLine(true, events); break;
            case "y": ScrollLine(false, events); break;
            case "r": ExecuteRedo(events); break;
            case "o": { var jb = _markManager.JumpBack(); if (jb.HasValue) { _markManager.SetMark('\'', _cursor); MoveCursor(jb.Value, events); } break; }
            case "i": { var jf = _markManager.JumpForward(); if (jf.HasValue) { _markManager.SetMark('\'', _cursor); MoveCursor(jf.Value, events); } break; }
            case "v": EnterVisualMode(VimMode.VisualBlock, events); break;
            case "w": _ctrlWPending = true; EmitStatus(events, "^W"); break;
            case "h": MoveCursor(motion.MoveLeft(_cursor, 1), events); SetPreferredColumn(_cursor.Column); break;
            case "j": MoveVertical(1, events); break;
            case "m": MoveLineAndFirstNonBlank(1, events); break;
            case "a":
            {
                int cnt = int.TryParse(_commandParser.Buffer, out var n) ? n : 1;
                _commandParser.Reset();
                _cursor = _textTransform.ExecuteIncrementNumber(_cursor, cnt, true, events);
                break;
            }
            case "x":
            {
                int cnt = int.TryParse(_commandParser.Buffer, out var n) ? n : 1;
                _commandParser.Reset();
                _cursor = _textTransform.ExecuteIncrementNumber(_cursor, cnt, false, events);
                break;
            }
            case "[":
                _commandParser.Reset();
                EmitStatus(events, "");
                break;
            case "l":
                _commandParser.Reset();
                EmitStatus(events, "");
                events.Add(VimEvent.CursorMoved(_cursor));
                break;
            case "]":
                _commandParser.Reset();
                events.Add(VimEvent.GoToDefinitionRequested());
                break;
            case "6":
            case "^":
                SwitchToAlternateBuffer(events);
                break;
            case "g":
                // g<C-g>: detailed file info (word/byte counts); plain Ctrl+G: brief file info
                if (_commandParser.Buffer.EndsWith("g"))
                {
                    _commandParser.Reset();
                    EmitFileInfo(events, brief: false);
                }
                else
                {
                    EmitFileInfo(events, brief: true);
                }
                break;
        }
    }

    private void HandleCtrlWSecondKey(string key, bool ctrl, List<VimEvent> events)
    {
        // Normalize: Ctrl+W Ctrl+X = same as Ctrl+W x (lowercase)
        var k = ctrl ? key.ToLower() : key;
        switch (k)
        {
            case "w": events.Add(VimEvent.WindowNavRequested(WindowNavDir.Next)); break;
            case "W": events.Add(VimEvent.WindowNavRequested(WindowNavDir.Prev)); break;
            case "q": case "c": events.Add(VimEvent.WindowCloseRequested(false)); break;
            case "v": events.Add(VimEvent.SplitRequested(true)); break;
            case "s": events.Add(VimEvent.SplitRequested(false)); break;
            case "h": events.Add(VimEvent.WindowNavRequested(WindowNavDir.Left)); break;
            case "j": events.Add(VimEvent.WindowNavRequested(WindowNavDir.Down)); break;
            case "k": events.Add(VimEvent.WindowNavRequested(WindowNavDir.Up)); break;
            case "l": events.Add(VimEvent.WindowNavRequested(WindowNavDir.Right)); break;
            default: EmitStatus(events, ""); break;
        }
    }

    /// <summary>
    /// When the current buffer is read-only (e.g. a binary file that was not loaded), emit the
    /// "cannot make changes" status message and return true so the caller can abort the mutation.
    /// </summary>
    private bool BlockedReadOnly(List<VimEvent> events)
    {
        if (!_bufferManager.Current.IsBinary) return false;
        EmitStatus(events, "E21: Cannot make changes (binary file is read-only)");
        return true;
    }

    /// <summary>True when the parsed normal-mode command would modify buffer text.</summary>
    private static bool CommandMutatesBuffer(ParsedCommand cmd)
    {
        // All operators change text except yank (d, c, >, <, =, gu, gU, g~, gc, gq, ys, ...).
        if (cmd.Operator != null) return cmd.Operator != "y";
        // Surround rewrites: cs{from}{to} and ds{char}.
        if (cmd.Motion is not null && (cmd.Motion.StartsWith("cs") || cmd.Motion.StartsWith("ds")))
            return true;
        return cmd.Motion switch
        {
            "i" or "I" or "a" or "A" or "o" or "O" or "R" or "gi"
            or "x" or "X" or "s" or "S" or "C" or "D"
            or "p" or "P" or "r"
            or "J" or "gJ" or "~" or "&" => true,
            _ => false,
        };
    }

    private void ExecuteNormalCommand(ParsedCommand cmd, List<VimEvent> events)
    {
        if (CommandMutatesBuffer(cmd) && BlockedReadOnly(events)) return;

        var buf = _bufferManager.Current.Text;
        var motion = new MotionEngine(buf, _bufferManager.Current.FilePath);
        int count = cmd.Count;

        // Double-operator: dd, cc, yy, >>, << (linewise on current line(s))
        if (cmd.LinewiseForced && cmd.Operator != null)
        {
            var endLine = Math.Min(buf.LineCount - 1, _cursor.Line + count - 1);
            switch (cmd.Operator)
            {
                case "d":
                    _repeatTracker.SetRepeatChange(cmd);
                    Snapshot();
                    _cursor = _clipboardOps.DeleteLines(_cursor.Line, endLine, events, cmd.Register ?? '"');
                    return;
                case "c":
                    _repeatTracker.BeginInsertRepeat(cmd);
                    Snapshot();
                    _cursor = _clipboardOps.DeleteLines(_cursor.Line, endLine, events, cmd.Register ?? '"');
                    EnterInsertMode(false, events);
                    return;
                case "y": _clipboardOps.YankLines(_cursor.Line, endLine, cmd.Register ?? '"', events); return;
                case ">":
                    _repeatTracker.SetRepeatChange(cmd);
                    _textTransform.IndentRange(_cursor.Line, endLine, true, events);
                    return;
                case "<":
                    _repeatTracker.SetRepeatChange(cmd);
                    _textTransform.IndentRange(_cursor.Line, endLine, false, events);
                    return;
                case "=":
                    _repeatTracker.SetRepeatChange(cmd);
                    AutoIndentRange(_cursor.Line, endLine, events);
                    return;
                case "gc":
                    _repeatTracker.SetRepeatChange(cmd);
                    _textTransform.ToggleCommentLines(_cursor.Line, endLine, events);
                    return;
                case "gq":
                    _repeatTracker.SetRepeatChange(cmd);
                    Snapshot();
                    _cursor = _textTransform.FormatText(_cursor, _cursor.Line, endLine, events);
                    return;
                case "ys":
                    // yss{char}: surround current line(s) — await surround char
                    Snapshot();
                    _surroundStart = new CursorPosition(_cursor.Line, 0);
                    _surroundEnd = new CursorPosition(endLine, buf.GetLine(endLine).TrimEnd().Length - 1);
                    _surroundLinewise = true;
                    _awaitingSurroundChar = true;
                    return;
                case "gu":
                    _repeatTracker.SetRepeatChange(cmd);
                    Snapshot();
                    _textTransform.ApplyCaseConversion(new CursorPosition(_cursor.Line, 0), new CursorPosition(endLine, 0), true, CaseConversion.Lower, events);
                    return;
                case "gU":
                    _repeatTracker.SetRepeatChange(cmd);
                    Snapshot();
                    _textTransform.ApplyCaseConversion(new CursorPosition(_cursor.Line, 0), new CursorPosition(endLine, 0), true, CaseConversion.Upper, events);
                    return;
                case "g~":
                    _repeatTracker.SetRepeatChange(cmd);
                    Snapshot();
                    _textTransform.ApplyCaseConversion(new CursorPosition(_cursor.Line, 0), new CursorPosition(endLine, 0), true, CaseConversion.Toggle, events);
                    return;
            }
        }

        // Surround: cs{from}{to} and ds{char}
        if (cmd.Operator == null && cmd.Motion?.StartsWith("cs") == true && cmd.Motion.Length >= 4)
        {
            Snapshot();
            _cursor = _textTransform.ExecuteChangeSurround(_cursor, cmd.Motion[2], cmd.Motion[3], events);
            return;
        }
        if (cmd.Operator == null && cmd.Motion?.StartsWith("ds") == true && cmd.Motion.Length >= 3)
        {
            Snapshot();
            _cursor = _textTransform.ExecuteDeleteSurround(_cursor, cmd.Motion[2], events);
            return;
        }

        // Operator + motion (dw, cw, de, d$, yw, >j, guw, ...). The movement switch
        // below has standalone cases for w/b/e/$/0/h/l/j/k/G/gg/... that move the
        // cursor and ignore cmd.Operator, so any command still carrying an operator
        // must be dispatched here first. Double-operator (dd/cc/yy), surround, and
        // linewise-forced operators are handled above; text objects, gn, and
        // f/t/F/T are resolved inside ExecuteOperatorMotion.
        if (cmd.Operator != null)
        {
            ExecuteOperatorMotion(cmd, events);
            return;
        }

        switch (cmd.Motion)
        {
            // Mode transitions
            case "i":
                _repeatTracker.BeginInsertRepeat(cmd);
                EnterInsertMode(false, events);
                break;
            case "I":
                _repeatTracker.BeginInsertRepeat(cmd);
                _cursor = motion.FindChar(_cursor, ' ', false, false);
                GoToLineStart(events);
                EnterInsertMode(false, events);
                break;
            case "a":
                _repeatTracker.BeginInsertRepeat(cmd);
                _cursor = motion.MoveRight(_cursor, 1, true);
                EmitCursor(events);
                EnterInsertMode(false, events);
                break;
            case "A":
                _repeatTracker.BeginInsertRepeat(cmd);
                GoToLineEnd(true, events);
                EnterInsertMode(false, events);
                break;
            case "o":
                _repeatTracker.BeginInsertRepeat(cmd);
                OpenLineBelow(events);
                EnterInsertMode(false, events);
                break;
            case "O":
                _repeatTracker.BeginInsertRepeat(cmd);
                OpenLineAbove(events);
                EnterInsertMode(false, events);
                break;
            case "R":
                _repeatTracker.BeginInsertRepeat(cmd);
                EnterReplaceMode(events);
                break;
            case "v": EnterVisualMode(VimMode.Visual, events); break;
            case "V": EnterVisualMode(VimMode.VisualLine, events); break;
            case ":": EnterCommandMode(events); break;
            case "/": EnterSearchMode(true, events); break;
            case "?": EnterSearchMode(false, events); break;

            // ZZ: save and quit (equivalent to :wq)
            case "ZZ":
            {
                var result = _exProcessor.Execute("wq", _cursor);
                if (!result.Success) EmitStatus(events, result.Message ?? "");
                else if (result.Event != null) events.Add(result.Event);
                break;
            }
            // ZQ: quit without saving (equivalent to :q!)
            case "ZQ":
                events.Add(VimEvent.WindowCloseRequested(true));
                break;

            // Movement
            case "h": MoveCursor(motion.MoveLeft(_cursor, count), events); SetPreferredColumn(_cursor.Column); break;
            case "l": MoveCursor(motion.MoveRight(_cursor, count), events); SetPreferredColumn(_cursor.Column); break;
            case "j": MoveVertical(count, events); break;
            case "k": MoveVertical(-count, events); break;
            case "0": MoveCursor(_cursor with { Column = 0 }, events); SetPreferredColumn(0); break;
            case "^": var fnb = GetFirstNonBlank(); MoveCursor(_cursor with { Column = fnb }, events); break;
            case "$": GoToLineEnd(false, events); break;
            case "w": MoveCursor(motion.WordForward(_cursor, count, false), events); break;
            case "W": MoveCursor(motion.WordForward(_cursor, count, true), events); break;
            case "b": MoveCursor(motion.WordBackward(_cursor, count, false), events); break;
            case "B": MoveCursor(motion.WordBackward(_cursor, count, true), events); break;
            case "e": MoveCursor(motion.WordEnd(_cursor, count, false), events); break;
            case "E": MoveCursor(motion.WordEnd(_cursor, count, true), events); break;
            case "ge": MoveCursor(new TextObjectEngine(buf).WordEndBackward(_cursor, count), events); break;
            case "gE": MoveCursor(new TextObjectEngine(buf).WordEndBackward(_cursor, count), events); break;
            case "gj": MoveVertical(count, events); break;
            case "gk": MoveVertical(-count, events); break;
            case "g_":
                var g_m = motion.Calculate("g_", _cursor, count);
                if (g_m.HasValue) MoveCursor(g_m.Value.Target, events);
                break;
            case "gg": _markManager.SetMark('\'', _cursor); MoveCursor(new CursorPosition(0, 0), events); break;
            case "G":
                var lastLine = count == 1 ? buf.LineCount - 1 : count - 1;
                _markManager.SetMark('\'', _cursor);
                MoveCursor(new CursorPosition(Math.Clamp(lastLine, 0, buf.LineCount - 1), 0), events);
                break;
            case "gt":
                for (int i = 0; i < count; i++)
                    events.Add(VimEvent.NextTabRequested());
                break;
            case "gT":
                for (int i = 0; i < count; i++)
                    events.Add(VimEvent.PrevTabRequested());
                break;
            case "gv":
                _visualStart = buf.ClampCursor(_lastVisualStart);
                _cursor = buf.ClampCursor(_lastVisualEnd);
                ChangeMode(_lastVisualMode, events);
                UpdateSelection(events);
                break;
            case "gi":
                MoveCursor(_bufferManager.Current.Text.ClampCursor(_lastInsertPos), events);
                EnterInsertMode(false, events);
                break;
            case "g;":
            {
                for (int i = 0; i < count; i++)
                {
                    var cp = _markManager.ChangeBack();
                    if (cp.HasValue) MoveCursor(buf.ClampCursor(cp.Value), events);
                }
                break;
            }
            case "g,":
            {
                for (int i = 0; i < count; i++)
                {
                    var cp = _markManager.ChangeForward();
                    if (cp.HasValue) MoveCursor(buf.ClampCursor(cp.Value), events);
                }
                break;
            }
            case "gJ":
                _repeatTracker.SetRepeatChange(cmd);
                _cursor = _textTransform.JoinLinesNoSpace(_cursor, count, events);
                break;
            case "gn":
            case "gN":
            {
                if (cmd.Operator != null)
                {
                    ExecuteOperatorMotion(cmd, events);
                    break;
                }
                bool gnForward = cmd.Motion == "gn";
                var gnMatch = _searchOps.FindGnMatch(_cursor, gnForward);
                if (gnMatch.HasValue)
                {
                    _visualStart = gnMatch.Value.Start;
                    _cursor = gnMatch.Value.End;
                    ChangeMode(VimMode.Visual, events);
                    UpdateSelection(events);
                }
                break;
            }
            case "gd":
            case "gr":
            case "ga":
            case "gch":
            case "gct":
                _lspTriggerCommands.TryHandle(cmd.Motion, events);
                break;
            case "gf":
            case "gx":
                _fileNavCommands.TryHandle(cmd.Motion, _cursor, events);
                break;
            case "]s": NavigateSpellError(true, count, events); break;
            case "[s": NavigateSpellError(false, count, events); break;
            case "]c": events.Add(VimEvent.HunkNavigateRequested(true)); break;
            case "[c": events.Add(VimEvent.HunkNavigateRequested(false)); break;
            case "[m": MethodJump(false, false, count, events); break;
            case "]m": MethodJump(true,  false, count, events); break;
            case "[M": MethodJump(false, true,  count, events); break;
            case "]M": MethodJump(true,  true,  count, events); break;
            case "[[": case "]]": case "[]": case "][":
            case "[{": case "]}": case "[(": case "])":
            {
                bool isSection = cmd.Motion is "[[" or "]]" or "[]" or "][";
                var m = new MotionEngine(buf, _bufferManager.Current.FilePath).Calculate(cmd.Motion, _cursor, count);
                if (m.HasValue && m.Value.Target != _cursor)
                {
                    if (isSection) _markManager.AddJump(_cursor);
                    MoveCursor(m.Value.Target, events);
                }
                break;
            }
            case "F2":
                events.Add(VimEvent.LspRenameRequested());
                break;
            case "K":
                events.Add(VimEvent.LspHoverRequested());
                break;
            case "+":
                MoveLineAndFirstNonBlank(count, events);
                break;
            case "-":
                MoveLineAndFirstNonBlank(-count, events);
                break;
            case "_":
                MoveLineAndFirstNonBlank(Math.Max(0, count - 1), events);
                break;
            case "|":
                MoveToColumn(count, events);
                break;
            case "{": MoveCursor(ParagraphBackward(), events); break;
            case "}": MoveCursor(ParagraphForward(), events); break;
            case "%": var mb = MatchBracket(buf, motion); if (mb.HasValue) MoveCursor(mb.Value, events); break;
            case "H": MoveCursor(ScreenPosition(Math.Max(0, count - 1)), events); break;
            case "M": MoveCursor(ScreenPosition(_viewportVisibleLines / 2), events); break;
            case "L": MoveCursor(ScreenPosition(Math.Max(0, _viewportVisibleLines - count)), events); break;
            case ";": _searchOps.RepeatFind(_cursor, false, events); break;
            case ",": _searchOps.RepeatFind(_cursor, true, events); break;
            case "n": _searchOps.SearchNext(_cursor, true, events); break;
            case "N": _searchOps.SearchNext(_cursor, false, events); break;
            case "*": _searchOps.SearchWordUnderCursor(_cursor, true, events); break;
            case "#": _searchOps.SearchWordUnderCursor(_cursor, false, events); break;
            case "zz": events.Add(VimEvent.ViewportAlignRequested(ViewportAlign.Center)); break;
            case "zt": events.Add(VimEvent.ViewportAlignRequested(ViewportAlign.Top)); break;
            case "zb": events.Add(VimEvent.ViewportAlignRequested(ViewportAlign.Bottom)); break;
            case "z=":
                ShowSpellSuggestions(events);
                break;
            case "za": case "zo": case "zc": case "zM": case "zR": case "zf":
            case "zj": case "zk": case "[z": case "]z": case "zd": case "zD": case "zE": case "zn": case "zN":
            {
                _foldCommands.TryHandle(cmd.Motion, _cursor, count, events, out var foldResult);
                if (foldResult.DirectCursor.HasValue) { _cursor = foldResult.DirectCursor.Value; EmitCursor(events); }
                if (foldResult.MoveCursor.HasValue) MoveCursor(foldResult.MoveCursor.Value, events);
                if (foldResult.FoldDisabled.HasValue) _foldDisabled = foldResult.FoldDisabled.Value;
                break;
            }

            // Editing
            case "x":
                _repeatTracker.SetRepeatChange(cmd);
                // Delete [count] chars from cursor position
                var xEnd = _cursor with { Column = Math.Min(_cursor.Column + count - 1, Math.Max(0, buf.GetLineLength(_cursor.Line) - 1)) };
                _cursor = _clipboardOps.ExecuteDelete(_cursor, xEnd, false, events, cmd.Register ?? '"');
                break;
            case "X":
                _repeatTracker.SetRepeatChange(cmd);
                // Delete [count] chars before cursor
                var xStart = motion.MoveLeft(_cursor, count);
                if (xStart.Column < _cursor.Column) _cursor = _clipboardOps.ExecuteDelete(xStart, _cursor with { Column = _cursor.Column - 1 }, false, events, cmd.Register ?? '"');
                break;
            case "s":
                _repeatTracker.BeginInsertRepeat(cmd);
                var sStart = _cursor;
                _cursor = _clipboardOps.ExecuteDelete(_cursor, motion.MoveRight(_cursor, count), false, events, cmd.Register ?? '"');
                _cursor = _bufferManager.Current.Text.ClampCursor(sStart, true);
                EmitCursor(events);
                EnterInsertMode(false, events);
                break;
            case "S":
                _repeatTracker.BeginInsertRepeat(cmd);
                _cursor = _clipboardOps.DeleteLines(_cursor.Line, _cursor.Line, events, cmd.Register ?? '"');
                EnterInsertMode(false, events);
                break;
            case "D":
                _repeatTracker.SetRepeatChange(cmd);
                var eol = GetLineLength() - 1;
                if (eol >= _cursor.Column) _cursor = _clipboardOps.ExecuteDelete(_cursor, _cursor with { Column = eol }, false, events, cmd.Register ?? '"');
                break;
            case "C":
                _repeatTracker.BeginInsertRepeat(cmd);
                var cStart = _cursor;
                var ceol = GetLineLength() - 1;
                if (ceol >= _cursor.Column) _cursor = _clipboardOps.ExecuteDelete(_cursor, _cursor with { Column = ceol }, false, events, cmd.Register ?? '"');
                _cursor = _bufferManager.Current.Text.ClampCursor(cStart, true);
                EmitCursor(events);
                EnterInsertMode(false, events);
                break;
            case "Y": _clipboardOps.YankLines(_cursor.Line, _cursor.Line + count - 1, cmd.Register ?? '"', events); break;
            case "p":
                _repeatTracker.SetRepeatChange(cmd);
                _cursor = _clipboardOps.PasteAfter(_cursor, cmd.Register ?? '"', events);
                break;
            case "P":
                _repeatTracker.SetRepeatChange(cmd);
                _cursor = _clipboardOps.PasteBefore(_cursor, cmd.Register ?? '"', events);
                break;
            case "gp":
                _repeatTracker.SetRepeatChange(cmd);
                _cursor = _clipboardOps.PasteAfter(_cursor, cmd.Register ?? '"', events, cursorAfterPaste: true);
                break;
            case "gP":
                _repeatTracker.SetRepeatChange(cmd);
                _cursor = _clipboardOps.PasteBefore(_cursor, cmd.Register ?? '"', events, cursorAfterPaste: true);
                break;
            case "]p":
                _repeatTracker.SetRepeatChange(cmd);
                _cursor = _clipboardOps.ExecuteIndentedPaste(_cursor, after: true, cmd.Register ?? '"', _config.Options.TabStop, events);
                break;
            case "[p":
                _repeatTracker.SetRepeatChange(cmd);
                _cursor = _clipboardOps.ExecuteIndentedPaste(_cursor, after: false, cmd.Register ?? '"', _config.Options.TabStop, events);
                break;
            case "u": ExecuteUndo(events); break;
            case "U": ExecuteUndo(events); break;
            case "\x12": ExecuteRedo(events); break;
            case "\x16": EnterVisualMode(VimMode.VisualBlock, events); break;
            case ".": _repeatTracker.RepeatLastChange(count, events); break;
            case "J":
                _repeatTracker.SetRepeatChange(cmd);
                _textTransform.JoinLines(_cursor, count, events);
                break;
            case "~":
                _repeatTracker.SetRepeatChange(cmd);
                _cursor = _textTransform.ToggleCase(_cursor, count, events);
                break;
            case ">>":
                _repeatTracker.SetRepeatChange(cmd);
                Snapshot();
                _textTransform.IndentRange(_cursor.Line, _cursor.Line, true, events);
                break;
            case "<<":
                _repeatTracker.SetRepeatChange(cmd);
                Snapshot();
                _textTransform.IndentRange(_cursor.Line, _cursor.Line, false, events);
                break;

            // r: replace char (needs next input)
            case var r when r?.StartsWith('r') == true && r.Length == 2:
                _repeatTracker.SetRepeatChange(cmd);
                _cursor = _textTransform.ExecuteReplace(_cursor, r[1], events);
                break;
            case "r": _pendingReplaceChar = null; /* wait */ break;

            // Marks
            case var m when m?.StartsWith('m') == true && m.Length == 2:
                _markManager.SetMark(m[1], _cursor);
                break;
            case "m": _awaitingMark = true; break;
            case var tick when tick?.StartsWith('`') == true && tick.Length == 2:
                var mk = _markManager.GetMark(tick[1] == '`' ? '\'' : tick[1]); if (mk.HasValue) { _markManager.SetMark('\'', _cursor); MoveCursor(mk.Value, events); } break;
            case "`": _awaitingMarkJump = true; break;
            case var apos when apos?.StartsWith('\'') == true && apos.Length == 2:
                var mk2 = _markManager.GetMark(apos[1]); if (mk2.HasValue) { _markManager.SetMark('\'', _cursor); MoveCursor(mk2.Value with { Column = 0 }, events); } break;
            case "'": _awaitingMarkJumpLine = true; break;

            // q: / q/ / q? — open command/search history in command line
            case "q:":
            {
                _exProcessor.ResetHistoryIndex();
                var qcCmd = _exProcessor.LastCommand ?? "";
                EnterCommandMode(events);
                if (qcCmd.Length > 0) { _cmdLine = qcCmd; EmitCmdLine(events); }
                break;
            }
            case "q/":
            case "q?":
            {
                _exProcessor.ResetSearchHistoryIndex();
                var qsLastSearch = _exProcessor.SearchHistory;
                var qsPattern = qsLastSearch.Count > 0 ? qsLastSearch[0] : "";
                EnterSearchMode(cmd.Motion == "q/", events);
                if (qsPattern.Length > 0) { _cmdLine = qsPattern; EmitCmdLine(events); }
                break;
            }

            // Macros
            case var q when q?.StartsWith('q') == true && q.Length == 2:
                if (_macroManager.IsRecording) _macroManager.StopRecording();
                else _macroManager.StartRecording(q[1]);
                EmitStatus(events, _macroManager.IsRecording ? $"recording @{q[1]}" : "");
                break;
            case var at when at?.StartsWith('@') == true && at.Length == 2:
                if (at[1] == ':')
                {
                    var lastCmd = _exProcessor.LastCommand;
                    if (lastCmd == null) { EmitStatus(events, "E80: Error while processing command"); break; }
                    for (int i = 0; i < count; i++)
                        ExecuteExCommand(lastCmd, events);
                }
                else
                    PlayMacro(at[1], count, events);
                break;

            // Operator + motion commands
            default:
                ExecuteOperatorMotion(cmd, events);
                break;
        }
    }

    private void ExecuteOperatorMotion(ParsedCommand cmd, List<VimEvent> events)
    {
        var buf = _bufferManager.Current.Text;
        var motion = new MotionEngine(buf, _bufferManager.Current.FilePath);
        motion.SetViewport(_viewportTopLine, _viewportVisibleLines);

        // Text objects
        if (cmd.Motion?.Length == 2 && (cmd.Motion[0] is 'i' or 'a'))
        {
            if (cmd.Operator == null) return;
            var range = new TextObjectEngine(buf).GetRange(cmd.Motion, _cursor, Math.Max(1, cmd.Count));
            if (range == null) return;

            if (cmd.Operator == "c") _repeatTracker.BeginInsertRepeat(cmd);
            else if (cmd.Operator is "d" or "<" or ">" or "=") _repeatTracker.SetRepeatChange(cmd);

            ExecuteOperator(cmd.Operator, range.Value.Start, range.Value.End, cmd.Register ?? '"', false, events);
            return;
        }

        // gn/gN — select next/prev search match, then apply operator
        if (cmd.Motion is "gn" or "gN")
        {
            var gnMatch = _searchOps.FindGnMatch(_cursor, cmd.Motion == "gn");
            if (gnMatch == null) return;
            if (cmd.Operator == "c") _repeatTracker.BeginInsertRepeat(cmd);
            else if (cmd.Operator is "d" or "<" or ">" or "=") _repeatTracker.SetRepeatChange(cmd);
            ExecuteOperator(cmd.Operator!, gnMatch.Value.Start, gnMatch.Value.End, cmd.Register ?? '"', false, events);
            return;
        }

        // f/F/t/T find
        if (cmd.Motion is "f" or "F" or "t" or "T" && cmd.FindChar.HasValue)
        {
            bool fwd = cmd.Motion is "f" or "t";
            bool before = cmd.Motion is "t" or "T";
            var found = motion.FindChar(_cursor, cmd.FindChar.Value, fwd, before, cmd.Count);

            if (cmd.Operator == null)
            {
                MoveCursor(found, events);
                return;
            }

            if (cmd.Operator == "c") _repeatTracker.BeginInsertRepeat(cmd);
            else if (cmd.Operator is "d" or "<" or ">" or "=") _repeatTracker.SetRepeatChange(cmd);
            ExecuteOperator(cmd.Operator, _cursor, found, cmd.Register ?? '"', false, events);
            return;
        }

        // Calculate motion target
        var motionName = cmd.Motion ?? "";

        // cw / cW behave like ce / cE when the cursor is on a non-blank (Vim special
        // case): the trailing whitespace after the word must be preserved.
        if (cmd.Operator == "c" && motionName is "w" or "W")
        {
            var lineText = buf.GetLine(_cursor.Line);
            if (_cursor.Column < lineText.Length && !char.IsWhiteSpace(lineText[_cursor.Column]))
                motionName = motionName == "w" ? "e" : "E";
        }

        // `dw`/`dW` (and other operators over w/W) extend over the whole last word of a
        // line, unlike the cursor-movement `w` which stops on its last character. Use the
        // unclamped operator word-end so the exclusive delete covers the word.
        var mot = (cmd.Operator != null && motionName is "w" or "W")
            ? motion.WordForwardOperatorEnd(_cursor, Math.Max(1, cmd.Count), motionName == "W")
            : motion.Calculate(motionName, _cursor, cmd.Count);
        if (mot == null) return;

        bool linewise = mot.Value.Type == MotionType.Linewise || cmd.LinewiseForced;

        if (cmd.Operator == null)
        {
            MoveCursor(mot.Value.Target, events);
            return;
        }

        if (cmd.Operator == "c") _repeatTracker.BeginInsertRepeat(cmd);
        else if (cmd.Operator is "d" or "<" or ">" or "=" or "gu" or "gU" or "g~") _repeatTracker.SetRepeatChange(cmd);

        var opFrom = _cursor;
        var opTo = mot.Value.Target;

        // ExecuteOperator deletes inclusively (through the char at the far endpoint).
        // An exclusive motion must NOT include that character, so step the far endpoint
        // back one position. At column 0 this moves to the end of the previous line —
        // matching Vim's exclusive-to-inclusive rule and keeping `dw` on the last word
        // of a line from joining the following line.
        if (!linewise && mot.Value.Type == MotionType.Exclusive)
        {
            var (lo, hi) = ComparePositions(opFrom, opTo) <= 0 ? (opFrom, opTo) : (opTo, opFrom);
            if (lo == hi) return; // empty motion — nothing to operate on
            var steppedHi = StepBackOnePosition(hi);
            if (ComparePositions(steppedHi, lo) < 0) return;
            if (opTo == hi) opTo = steppedHi; else opFrom = steppedHi;
        }

        ExecuteOperator(cmd.Operator, opFrom, opTo, cmd.Register ?? '"', linewise, events);
    }

    private static int ComparePositions(CursorPosition a, CursorPosition b) =>
        a.Line != b.Line ? a.Line.CompareTo(b.Line) : a.Column.CompareTo(b.Column);

    /// <summary>One buffer position earlier than <paramref name="pos"/>; at column 0 wraps to
    /// the last character of the previous line (or its column 0 if empty).</summary>
    private CursorPosition StepBackOnePosition(CursorPosition pos)
    {
        if (pos.Column > 0) return pos with { Column = pos.Column - 1 };
        if (pos.Line > 0)
        {
            int prevLen = _bufferManager.Current.Text.GetLineLength(pos.Line - 1);
            return new CursorPosition(pos.Line - 1, Math.Max(0, prevLen - 1));
        }
        return pos;
    }

    private void ExecuteOperator(string op, CursorPosition from, CursorPosition to,
        char register, bool linewise, List<VimEvent> events)
    {
        var buf = _bufferManager.Current.Text;
        Snapshot();

        // Normalize
        var start = from.Line < to.Line || (from.Line == to.Line && from.Column <= to.Column) ? from : to;
        var end = start == from ? to : from;

        switch (op)
        {
            case "d":
                if (linewise) _cursor = _clipboardOps.DeleteLines(start.Line, end.Line, events, register);
                else _cursor = _clipboardOps.ExecuteDelete(start, end, false, events, register);
                break;
            case "c":
                if (linewise) { _cursor = _clipboardOps.DeleteLines(start.Line, end.Line, events, register); EnterInsertMode(false, events); }
                else
                {
                    _cursor = _clipboardOps.ExecuteDelete(start, end, false, events, register);
                    _cursor = _bufferManager.Current.Text.ClampCursor(start, true);
                    EmitCursor(events);
                    EnterInsertMode(false, events);
                }
                break;
            case "y":
                if (linewise) _clipboardOps.YankLines(start.Line, end.Line, register, events);
                else _clipboardOps.YankRange(register, start, end, false);
                MoveCursor(start, events);
                EmitStatus(events, linewise ? $"{end.Line - start.Line + 1} lines yanked" : "yanked");
                break;
            case ">": _textTransform.IndentRange(start.Line, end.Line, true, events); break;
            case "<": _textTransform.IndentRange(start.Line, end.Line, false, events); break;
            case "=": AutoIndentRange(start.Line, end.Line, events); break;
            case "gc": _textTransform.ToggleCommentLines(start.Line, end.Line, events); break;
            case "gq": _cursor = _textTransform.FormatText(_cursor, start.Line, end.Line, events); break;
            case "ys":
                _surroundStart = start;
                _surroundEnd = end;
                _surroundLinewise = linewise;
                _awaitingSurroundChar = true;
                return;
            case "gu": _textTransform.ApplyCaseConversion(start, end, linewise, CaseConversion.Lower, events); break;
            case "gU": _textTransform.ApplyCaseConversion(start, end, linewise, CaseConversion.Upper, events); break;
            case "g~": _textTransform.ApplyCaseConversion(start, end, linewise, CaseConversion.Toggle, events); break;
        }
    }

    // ─────────────── INSERT MODE ───────────────
    private void HandleInsert(string key, bool ctrl, bool shift, bool alt, List<VimEvent> events)
    {
        _ctrlWPending = false;
        var buf = _bufferManager.Current.Text;

        // Read-only buffer (e.g. a binary file): never reachable via normal-mode entry (that is
        // already blocked), but guard here too so any direct/IME insert path cannot edit the buffer.
        if (_bufferManager.Current.IsBinary)
        {
            if (key == "Escape") { ExitInsertMode(events); return; }
            BlockedReadOnly(events);
            return;
        }

        if (_awaitingInsertRegister)
        {
            _awaitingInsertRegister = false;
            if (key == "=")
            {
                _awaitingExprRegister = true;
                _exprBuffer = "";
                events.Add(VimEvent.CommandLineChanged("="));
                return;
            }
            EmitStatus(events, "");
            if (key.Length == 1)
                InsertRegisterContent(key[0], events);
            return;
        }

        if (_awaitingExprRegister)
        {
            switch (key)
            {
                case "Escape":
                    _awaitingExprRegister = false;
                    _exprBuffer = "";
                    events.Add(VimEvent.CommandLineChanged(""));
                    return;
                case "Return":
                    _awaitingExprRegister = false;
                    var expr = _exprBuffer;
                    _exprBuffer = "";
                    events.Add(VimEvent.CommandLineChanged(""));
                    var result = ExpressionEvaluator.Evaluate(expr);
                    if (result != null)
                    {
                        Snapshot();
                        InsertTextAtCursor(result, events);
                    }
                    return;
                case "Back":
                    if (_exprBuffer.Length > 0)
                        _exprBuffer = _exprBuffer[..^1];
                    events.Add(VimEvent.CommandLineChanged("=" + _exprBuffer));
                    return;
                default:
                    if (key.Length == 1 && !ctrl)
                    {
                        _exprBuffer += key;
                        events.Add(VimEvent.CommandLineChanged("=" + _exprBuffer));
                    }
                    return;
            }
        }

        if (_digraphPendingChar.HasValue)
        {
            if (key == "Escape") { _digraphPendingChar = null; events.Add(VimEvent.CommandLineChanged("")); return; }
            if (key.Length != 1) return; // ignore non-printable keys while in digraph input
            if (_digraphPendingChar.Value == '\0')
            {
                // Received first char — store it and prompt for second
                _digraphPendingChar = key[0];
                events.Add(VimEvent.CommandLineChanged($"^K {key}"));
            }
            else
            {
                // Received second char — look up and insert
                var digraphKey = new string([_digraphPendingChar.Value, key[0]]);
                _digraphPendingChar = null;
                events.Add(VimEvent.CommandLineChanged(""));
                var ch = DiGraphs.Lookup(digraphKey);
                if (ch != null) { Snapshot(); InsertTextAtCursor(ch, events); }
                else EmitStatus(events, $"Unknown digraph: {digraphKey}");
            }
            return;
        }

        if (ctrl && (key.ToLower() == "n" || key.ToLower() == "p"))
        {
            _ctrlXPending = false;
            _ctrlXMode = '\0';
            _cursor = _kwCompletionOps.CycleKeyword(_cursor, key.ToLower() == "n" ? +1 : -1, events);
            return;
        }

        // Ctrl+X sub-mode: waiting for the completion type key
        if (_ctrlXPending)
        {
            _ctrlXPending = false;
            if (ctrl)
            {
                switch (key.ToLower())
                {
                    case "f": // Ctrl+X Ctrl+F — file path completion
                        _ctrlXMode = 'f';
                        _cursor = _kwCompletionOps.CycleFilePath(_cursor, +1, events);
                        return;
                    case "l": // Ctrl+X Ctrl+L — whole-line completion
                        _ctrlXMode = 'l';
                        _cursor = _kwCompletionOps.CycleLine(_cursor, +1, events);
                        return;
                    case "n": // Ctrl+X Ctrl+N — same as Ctrl+N
                        _ctrlXMode = '\0';
                        _cursor = _kwCompletionOps.CycleKeyword(_cursor, +1, events);
                        return;
                    case "p": // Ctrl+X Ctrl+P — same as Ctrl+P
                        _ctrlXMode = '\0';
                        _cursor = _kwCompletionOps.CycleKeyword(_cursor, -1, events);
                        return;
                }
            }
            // Any other key after Ctrl+X cancels sub-mode and falls through normally
            _ctrlXMode = '\0';
        }

        // Allow continuing an active Ctrl+X completion with just Ctrl+F or Ctrl+L
        if (ctrl && _ctrlXMode == 'f' && key.ToLower() == "f")
        {
            _cursor = _kwCompletionOps.CycleFilePath(_cursor, +1, events);
            return;
        }
        if (ctrl && _ctrlXMode == 'l' && key.ToLower() == "l")
        {
            _cursor = _kwCompletionOps.CycleLine(_cursor, +1, events);
            return;
        }

        if (_kwCompletionOps.HasCandidates)
        {
            _ctrlXMode = '\0';
            _kwCompletionOps.Reset();
        }

        if (ctrl)
        {
            switch (key.ToLower())
            {
                case "[": // Ctrl+[
                    ExitInsertMode(events);
                    return;
                case "w": _cursor = _textTransform.DeleteWordBack(_cursor, events); return;
                case "u": _cursor = _textTransform.DeleteLineBack(_cursor, events); return;
                case "h": _cursor = _textTransform.DeleteCharBack(_cursor, events); return;
                case "o": // Ctrl+O — execute one Normal command then return to Insert
                    _pendingInsertReturn = true;
                    ChangeMode(VimMode.Normal, events, suppressInsertAutocmd: true);
                    return;
                case "j": InsertNewline(events); return;
                case "m": InsertNewline(events); return;
                case "a": // Ctrl+A = Select All
                    SelectAllVisualLine(events);
                    return;
                case "c": // Ctrl+C = Copy current line to clipboard
                    _clipboardOps.YankLines(_cursor.Line, _cursor.Line, '+', events);
                    return;
                case "v": // Ctrl+V = Paste from clipboard
                    PasteAtCursorInsertMode(events);
                    return;
                case "x": // Ctrl+X = enter sub-completion mode (Ctrl+X Ctrl+F / Ctrl+X Ctrl+L)
                    _ctrlXPending = true;
                    EmitStatus(events, "^X");
                    return;
                case "r": // Ctrl+R {reg} = insert register contents
                    _awaitingInsertRegister = true;
                    EmitStatus(events, "\"");
                    return;
                case "k": // Ctrl+K {a}{b} = insert digraph
                    _digraphPendingChar = '\0';
                    events.Add(VimEvent.CommandLineChanged("^K"));
                    return;
                case ";": // Ctrl+; — insert current date (yyyy/MM/dd)
                    Snapshot();
                    InsertTextAtCursor(System.DateTime.Now.ToString("yyyy/MM/dd"), events);
                    return;
                case "t": // Ctrl+T — indent current line by shiftwidth
                    _textTransform.IndentRange(_cursor.Line, _cursor.Line, true, events);
                    _cursor = _cursor with { Column = _cursor.Column + _config.Options.ShiftWidth };
                    EmitCursor(events);
                    return;
                case "d": // Ctrl+D — dedent current line by shiftwidth
                {
                    var lineBeforeDedent = buf.GetLine(_cursor.Line);
                    int removedChars = 0;
                    var sw = _config.Options.ShiftWidth;
                    for (int i = 0; i < sw && i < lineBeforeDedent.Length && (lineBeforeDedent[i] == ' ' || lineBeforeDedent[i] == '\t'); i++)
                        removedChars++;
                    _textTransform.IndentRange(_cursor.Line, _cursor.Line, false, events);
                    _cursor = _cursor with { Column = Math.Max(0, _cursor.Column - removedChars) };
                    EmitCursor(events);
                    return;
                }
            }
            // Unhandled Ctrl combo — do not insert as text.
            return;
        }

        if (!ctrl && _mode == VimMode.Insert && _blockInsertOps.TryHandleKey(key, ref _cursor, events))
            return;

        switch (key)
        {
            case "Escape":
                ExitInsertMode(events);
                break;
            case "Back":
                if (_config.Options.Pairs && !_config.Options.Paste && _mode == VimMode.Insert && _cursor.Column > 0)
                {
                    var lineBack = buf.GetLine(_cursor.Line);
                    char prevChar = lineBack[_cursor.Column - 1];
                    char? pairClose = GetAutoPairClose(prevChar);
                    if (pairClose.HasValue && _cursor.Column < lineBack.Length && lineBack[_cursor.Column] == pairClose.Value)
                    {
                        buf.DeleteChar(_cursor.Line, _cursor.Column); // delete close first
                        buf.DeleteChar(_cursor.Line, _cursor.Column - 1); // then open
                        _cursor = _cursor with { Column = _cursor.Column - 1 };
                        EmitText(events);
                        break;
                    }
                }
                if (_textTransform.TryDeleteIndentBack(ref _cursor, events))
                    break;
                _cursor = _textTransform.DeleteCharBack(_cursor, events);
                break;
            case "Delete":
                buf.DeleteChar(_cursor.Line, _cursor.Column);
                EmitText(events);
                break;
            case "Return":
                TryExpandAbbreviation(buf, events);
                InsertNewline(events);
                _insertedText?.Append('\n');
                break;
            case "Left":
                var ml = new MotionEngine(buf, _bufferManager.Current.FilePath);
                _cursor = ml.MoveLeft(_cursor);
                SetPreferredColumn(_cursor.Column);
                EmitCursor(events);
                break;
            case "Right":
                var mr = new MotionEngine(buf, _bufferManager.Current.FilePath);
                _cursor = mr.MoveRight(_cursor, 1, true);
                SetPreferredColumn(_cursor.Column);
                EmitCursor(events);
                break;
            case "Up":
                MoveVerticalCursor(-1, insertMode: true);
                EmitCursor(events);
                break;
            case "Down":
                MoveVerticalCursor(1, insertMode: true);
                EmitCursor(events);
                break;
            case "Tab":
                if (TryEditAssistTab(shift, events))
                    break;
                if (shift) break; // Shift+Tab with no edit-assist is a no-op
                if (_config.Options.ExpandTab)
                {
                    var spaces = new string(' ', _config.Options.TabStop);
                    buf.InsertText(_cursor.Line, _cursor.Column, spaces);
                    _cursor = _cursor with { Column = _cursor.Column + spaces.Length };
                    _insertedText?.Append(spaces);
                }
                else
                {
                    buf.InsertChar(_cursor.Line, _cursor.Column, '\t');
                    _cursor = _cursor with { Column = _cursor.Column + 1 };
                    _insertedText?.Append('\t');
                }
                EmitText(events);
                break;
            default:
                if (key.Length == 1)
                {
                    _insertedText?.Append(key[0]);

                    if (_config.Options.Pairs && !_config.Options.Paste && _mode == VimMode.Insert)
                    {
                        char ch = key[0];

                        // Skip over auto-inserted asymmetric closing bracket only.
                        // Symmetric pairs (", ', `) are not skipped — typing a quote
                        // when one is at cursor should open a new pair, not skip.
                        if (ch is ')' or ']' or '}')
                        {
                            var lineStr = buf.GetLine(_cursor.Line);
                            if (_cursor.Column < lineStr.Length && lineStr[_cursor.Column] == ch)
                            {
                                _cursor = _cursor with { Column = _cursor.Column + 1 };
                                EmitCursor(events);
                                break;
                            }
                        }

                        // Auto-insert matching close char
                        char? pairClose = GetAutoPairClose(ch);
                        if (pairClose.HasValue)
                        {
                            buf.InsertChar(_cursor.Line, _cursor.Column, ch);
                            buf.InsertChar(_cursor.Line, _cursor.Column + 1, pairClose.Value);
                            _cursor = _cursor with { Column = _cursor.Column + 1 };
                            EmitText(events);
                            break;
                        }
                    }

                    if (_mode == VimMode.Insert)
                    {
                        var charResult = _editAssists.OnChar(MakeEditContext(), key[0]);
                        if (charResult.Handled)
                        {
                            _cursor = charResult.Cursor;
                            EmitText(events);
                            break;
                        }
                    }

                    if (_mode == VimMode.Replace)
                    {
                        if (_cursor.Column < buf.GetLineLength(_cursor.Line))
                            buf.DeleteChar(_cursor.Line, _cursor.Column);
                    }
                    buf.InsertChar(_cursor.Line, _cursor.Column, key[0]);
                    _cursor = _cursor with { Column = _cursor.Column + 1 };
                    // Expand abbreviation when a non-word character is typed as trigger
                    if (!MotionEngine.IsWordChar(key[0]))
                        TryExpandAbbreviation(buf, events, triggerCharAlreadyInserted: true);
                    EmitText(events);
                }
                break;
        }
    }

    // ─────────────── PLAIN (Vim-disabled) MODE ───────────────
    // Sole input handler when VimEnabled == false. Behaves like an ordinary text
    // box: text insertion, navigation and a small set of standard shortcuts.
    // Deliberately excludes every Vim-specific behaviour — no Normal/Visual/Command
    // modes, no mappings/abbreviations, no Vim insert-mode Ctrl keys (W/U/R/K/X/O),
    // digraphs, completion sub-modes, or pastetoggle. Nothing here ever changes
    // _mode, so modal editing cannot leak back in.
    private void HandlePlainTextKey(string key, bool ctrl, bool shift, bool alt, List<VimEvent> events)
    {
        var buf = _bufferManager.Current.Text;
        bool hasSelection = PlainSelectionRange() is not null;

        // Read-only buffer (e.g. a binary file): permit caret movement, selection and copy, but
        // reject anything that would edit the text.
        if (_bufferManager.Current.IsBinary)
        {
            bool isCopy = ctrl && !alt && string.Equals(key, "c", StringComparison.OrdinalIgnoreCase);
            bool isNav = key is "Left" or "Right" or "Up" or "Down" or "Home" or "End" or "Escape";
            if (!isCopy && !isNav) { BlockedReadOnly(events); return; }
        }

        if (ctrl && !alt)
        {
            switch (key.ToLowerInvariant())
            {
                case "z": _plainEditRunActive = false; ClearPlainSelection(events); ExecuteUndo(events); return;
                case "y": _plainEditRunActive = false; ClearPlainSelection(events); ExecuteRedo(events); return;
                case "c": // Copy selection (or, with no selection, the whole current line) to the clipboard.
                    if (hasSelection) CopyPlainSelectionToClipboard();
                    else CopyCurrentLineToClipboard();
                    return;
                case "x": // Cut selection to the clipboard.
                    if (hasSelection) { CopyPlainSelectionToClipboard(); BeginPlainEdit(); DeletePlainSelection(events); }
                    return;
                case "v": // Paste, replacing any selection.
                    BeginPlainEdit();
                    if (hasSelection) DeletePlainSelection(events);
                    PasteClipboardNoSnapshot(events);
                    return;
            }
            // Any other Ctrl combo is inert in plain mode (never inserted as text).
            return;
        }

        switch (key)
        {
            // Cursor movement: with Shift it extends the selection, otherwise it
            // ends the current edit run and drops any selection.
            case "Left":  PlainMoveCaret(new MotionEngine(buf, _bufferManager.Current.FilePath).MoveLeft(_cursor), shift, events); return;
            case "Right": PlainMoveCaret(new MotionEngine(buf, _bufferManager.Current.FilePath).MoveRight(_cursor, 1, true), shift, events); return;
            case "Up":    PlainMoveVertical(-1, shift, events); return;
            case "Down":  PlainMoveVertical(1, shift, events); return;
            case "Home":  PlainMoveCaret(_cursor with { Column = 0 }, shift, events); return;
            case "End":   PlainMoveCaret(_cursor with { Column = buf.GetLineLength(_cursor.Line) }, shift, events); return;
            case "Escape": ClearPlainSelection(events); return; // no mode to leave — just drop the selection
            case "Back":
                BeginPlainEdit();
                if (hasSelection) DeletePlainSelection(events);
                else if (!_textTransform.TryDeleteIndentBack(ref _cursor, events)) _cursor = _textTransform.DeleteCharBack(_cursor, events);
                return;
            case "Delete":
                if (hasSelection) { BeginPlainEdit(); DeletePlainSelection(events); return; }
                // Forward delete: remove the char under the caret, or join the next
                // line when at end-of-line. At end-of-buffer there is nothing to do,
                // so we take no snapshot and emit no (spurious) TextChanged.
                if (_cursor.Column < buf.GetLineLength(_cursor.Line))
                {
                    BeginPlainEdit();
                    buf.DeleteChar(_cursor.Line, _cursor.Column);
                    EmitText(events);
                }
                else if (_cursor.Line < buf.LineCount - 1)
                {
                    BeginPlainEdit();
                    buf.JoinLines(_cursor.Line);
                    EmitText(events);
                }
                return;
            case "Return":
                BeginPlainEdit();
                if (hasSelection) DeletePlainSelection(events);
                InsertNewline(events);
                return;
            case "Tab":
                {
                    BeginPlainEdit();
                    var tabResult = _editAssists.OnTab(MakeEditContext(), shift);
                    if (tabResult.Handled)
                    {
                        _cursor = tabResult.Cursor;
                        EmitText(events);
                        return;
                    }
                    // No assist handled it — fall through to default tab handling.
                }
                if (shift) return; // Shift+Tab with no edit-assist is a no-op
                BeginPlainEdit();
                if (hasSelection) DeletePlainSelection(events);
                if (_config.Options.ExpandTab)
                {
                    var spaces = new string(' ', _config.Options.TabStop);
                    buf.InsertText(_cursor.Line, _cursor.Column, spaces);
                    _cursor = _cursor with { Column = _cursor.Column + spaces.Length };
                }
                else
                {
                    buf.InsertChar(_cursor.Line, _cursor.Column, '\t');
                    _cursor = _cursor with { Column = _cursor.Column + 1 };
                }
                EmitText(events);
                return;
            default:
                if (key.Length == 1)
                {
                    BeginPlainEdit();
                    if (hasSelection) DeletePlainSelection(events);
                    buf.InsertChar(_cursor.Line, _cursor.Column, key[0]);
                    _cursor = _cursor with { Column = _cursor.Column + 1 };
                    EmitText(events);
                }
                return;
        }
    }

    // Snapshot once at the start of a contiguous run of plain-mode edits so the
    // whole run undoes as a single step (cursor movement resets the run).
    private void BeginPlainEdit()
    {
        if (_plainEditRunActive) return;
        Snapshot();
        _plainEditRunActive = true;
    }

    /// <summary>
    /// Sets a plain (non-modal) selection anchored at <paramref name="anchor"/>
    /// with the caret at <paramref name="caret"/>, without entering Visual mode.
    /// Both are half-open caret boundaries (the caret position is excluded), giving
    /// standard text-box selection. Used to drive mouse drag-selection while Vim is
    /// disabled.
    /// </summary>
    public IReadOnlyList<VimEvent> SetPlainSelection(CursorPosition anchor, CursorPosition caret)
    {
        var buf = _bufferManager.Current.Text;
        _plainSelActive = true;
        _plainSelAnchor = buf.ClampCursor(anchor, insertMode: true);
        var events = new List<VimEvent>();
        UpdatePlainSelection(buf.ClampCursor(caret, insertMode: true), events);
        SetPreferredColumn(_cursor.Column);
        return events;
    }

    /// <summary>Clears any active plain selection (e.g. on a plain mouse click).</summary>
    public IReadOnlyList<VimEvent> ClearPlainSelection()
    {
        var events = new List<VimEvent>();
        ClearPlainSelection(events);
        return events;
    }

    // Plain-mode keyboard caret move. With Shift it extends the selection from the
    // existing anchor (or the current caret if none); without Shift it drops any
    // selection and just moves the caret.
    private void PlainMoveCaret(CursorPosition target, bool shift, List<VimEvent> events)
    {
        _plainEditRunActive = false;
        if (shift)
        {
            if (!_plainSelActive) { _plainSelAnchor = _cursor; _plainSelActive = true; }
            UpdatePlainSelection(target, events);
        }
        else
        {
            ClearPlainSelection(events);
            _cursor = target;
            EmitCursor(events);
        }
        SetPreferredColumn(_cursor.Column);
    }

    // Vertical caret move in plain mode. Uses _preferredColumn as the goal column
    // (preserved across consecutive Up/Down) and an insert-mode column clamp so the
    // caret can rest at end-of-line — unlike the Normal-mode MotionEngine.MoveUp/Down,
    // which clamp to lineLen-1 and would strand the caret one cell short.
    private void PlainMoveVertical(int delta, bool shift, List<VimEvent> events)
    {
        var buf = _bufferManager.Current.Text;
        int line = Math.Clamp(_cursor.Line + delta, 0, buf.LineCount - 1);
        int maxCol = buf.GetLineLength(line);
        int col = ResolveVerticalColumn(line, maxCol);
        var target = new CursorPosition(line, col);
        _plainEditRunActive = false;
        if (shift)
        {
            if (!_plainSelActive) { _plainSelAnchor = _cursor; _plainSelActive = true; }
            UpdatePlainSelection(target, events);
        }
        else
        {
            ClearPlainSelection(events);
            _cursor = target;
            EmitCursor(events);
        }
        // _preferredColumn is intentionally left unchanged so the goal column
        // survives moving through shorter lines.
    }

    // Moves the caret to a boundary and recomputes the inclusive cell Selection for
    // the half-open range [anchor, caret). Emits cursor + selection events.
    private void UpdatePlainSelection(CursorPosition caret, List<VimEvent> events)
    {
        _cursor = caret;
        events.Add(VimEvent.CursorMoved(_cursor));

        if (PlainSelectionRange() is not { } range)
        {
            // Empty range — no characters selected.
            if (_selection != null) { _selection = null; events.Add(VimEvent.SelectionChanged(null)); }
            return;
        }

        _selection = new Selection(range.start, range.endCell, SelectionType.Character);
        events.Add(VimEvent.SelectionChanged(_selection));
    }

    // The active plain selection as a normalized inclusive cell range [start, endCell],
    // or null when there is no selection or it is empty. The source of truth is the
    // half-open caret range [_plainSelAnchor, _cursor); converting to inclusive cells
    // here means a single selected cell (anchor and caret one apart) is never mistaken
    // for an empty Selection (whose Start == End reads as IsEmpty).
    private (CursorPosition start, CursorPosition endCell)? PlainSelectionRange()
    {
        if (!_plainSelActive || _plainSelAnchor == _cursor) return null;
        var (low, high) = ComparePos(_plainSelAnchor, _cursor) <= 0
            ? (_plainSelAnchor, _cursor)
            : (_cursor, _plainSelAnchor);
        return (low, InclusiveEndCell(high));
    }

    // Converts the exclusive upper boundary of a selection to the inclusive cell
    // index of the last selected character.
    private CursorPosition InclusiveEndCell(CursorPosition boundary)
    {
        if (boundary.Column > 0)
            return boundary with { Column = boundary.Column - 1 };
        if (boundary.Line > 0)
            return new CursorPosition(boundary.Line - 1, _bufferManager.Current.Text.GetLineLength(boundary.Line - 1));
        return boundary;
    }

    private static int ComparePos(CursorPosition a, CursorPosition b)
        => a.Line != b.Line ? a.Line.CompareTo(b.Line) : a.Column.CompareTo(b.Column);

    private void ClearPlainSelection(List<VimEvent> events)
    {
        _plainSelActive = false;
        if (_selection == null) return;
        _selection = null;
        events.Add(VimEvent.SelectionChanged(null));
    }

    // Deletes the active plain selection, leaving the caret at its start. Assumes
    // a snapshot has already been taken (callers go through BeginPlainEdit).
    private void DeletePlainSelection(List<VimEvent> events)
    {
        if (PlainSelectionRange() is not { } range) return;
        _plainSelActive = false;
        _selection = null;
        events.Add(VimEvent.SelectionChanged(null));
        var prevSuppress = _suppressSnapshot;
        _suppressSnapshot = true; // BeginPlainEdit already snapshotted this run
        _cursor = _clipboardOps.ExecuteDelete(range.start, range.endCell, false, events);
        _suppressSnapshot = prevSuppress;
        _cursor = _bufferManager.Current.Text.ClampCursor(range.start, insertMode: true);
        EmitCursor(events);
    }

    private void CopyPlainSelectionToClipboard()
    {
        if (PlainSelectionRange() is not { } range) return;
        _clipboardOps.YankRange('+', range.start, range.endCell, false);
    }

    // Copy with no selection: yank the whole current line plus its trailing newline
    // as a charwise register, so a later paste reproduces a full line instead of
    // splicing the bare text into the middle of another line.
    private void CopyCurrentLineToClipboard()
    {
        var buf = _bufferManager.Current.Text;
        _registerManager.SetYank('+', new Register(buf.GetLine(_cursor.Line) + "\n", RegisterType.Character));
    }

    private void PasteClipboardNoSnapshot(List<VimEvent> events)
    {
        var reg = _registerManager.Get('+');
        if (reg.IsEmpty) return;
        InsertTextAtCursor(reg.Text, events);
    }

    // ─────────────── VISUAL MODE ───────────────
    private void HandleVisual(string key, bool ctrl, bool shift, bool alt, List<VimEvent> events)
    {
        _ctrlWPending = false;
        if (key == "Escape")
        {
            _commandParser.Reset();
            _discardingUnsupportedVisualTextObj = false;
            ExitVisualMode(events);
            return;
        }

        if (_discardingUnsupportedVisualTextObj && !ctrl)
        {
            _discardingUnsupportedVisualTextObj = false;
            return;
        }

        // Second char of visual text object (e.g. viw, va", vi{)
        if (_awaitingVisualTextObj != '\0' && !ctrl)
        {
            char prefix = _awaitingVisualTextObj;
            _awaitingVisualTextObj = '\0';
            int count = Math.Max(1, _awaitingVisualTextObjCount);
            _awaitingVisualTextObjCount = 1;
            string textObj = prefix.ToString() + key;
            var range = new TextObjectEngine(_bufferManager.Current.Text).GetRange(textObj, _cursor, count);
            if (range != null)
            {
                _visualStart = range.Value.Start;
                _cursor = range.Value.End;
                UpdateSelection(events);
            }
            return;
        }

        if (_awaitingBlockReplace)
        {
            _awaitingBlockReplace = false;
            if (key.Length == 1)
                ExecuteBlockReplace(key[0], events);
            return;
        }

        // Register prefix: "x selects register x for the next visual operator.
        if (_awaitingVisualRegister && !ctrl)
        {
            _awaitingVisualRegister = false;
            if (key.Length == 1)
                _visualPendingRegister = key[0];
            return;
        }
        if (key == "\"" && !ctrl)
        {
            _awaitingVisualRegister = true;
            return;
        }

        if (ctrl)
        {
            if (key == "[")
            {
                _commandParser.Reset();
                ExitVisualMode(events);
                return;
            }
            if (key == "v")
            {
                _commandParser.Reset();
                if (_mode != VimMode.VisualBlock)
                {
                    ChangeMode(VimMode.VisualBlock, events);
                    UpdateSelection(events);
                }
                return;
            }
            if (key == "c") // Ctrl+C = Copy selection to clipboard
            {
                _commandParser.Reset();
                _visualEditOps.ExecuteVisualYank('+', events);
                return;
            }
            if (key == "x") // Ctrl+X = Cut selection to clipboard
            {
                _commandParser.Reset();
                _visualEditOps.ExecuteVisualDelete('+', events);
                return;
            }
            if (key == "a") // Ctrl+A = Select All
            {
                _commandParser.Reset();
                SelectAllVisualLine(events);
                return;
            }
            HandleNormalCtrl(key, events);
            UpdateSelection(events);
            return;
        }

        if (_mode == VimMode.Visual &&
            (key is "i" or "a") &&
            TryParseVisualCount(_commandParser.Buffer, out var visualTextObjectCount))
        {
            _commandParser.Reset();
            if (_mode == VimMode.Visual)
            {
                _awaitingVisualTextObj = key[0];
                _awaitingVisualTextObjCount = visualTextObjectCount;
            }
            else
            {
                _discardingUnsupportedVisualTextObj = true;
            }
            return;
        }

        if (TryHandlePendingVisualMotion(key, events))
            return;

        // Visual operations and mode toggles
        switch (key)
        {
            case ":":
                _commandParser.Reset();
                EnterCommandModeFromVisual(events);
                return;
            case "v":
                _commandParser.Reset();
                if (_mode == VimMode.Visual) ExitVisualMode(events);
                else
                {
                    ChangeMode(VimMode.Visual, events);
                    UpdateSelection(events);
                }
                return;
            case "V":
                _commandParser.Reset();
                if (_mode == VimMode.VisualLine) ExitVisualMode(events);
                else
                {
                    ChangeMode(VimMode.VisualLine, events);
                    UpdateSelection(events);
                }
                return;
            case "\x16":
                _commandParser.Reset();
                if (_mode != VimMode.VisualBlock)
                {
                    ChangeMode(VimMode.VisualBlock, events);
                    UpdateSelection(events);
                }
                return;
            case "o":
            case "O":
                _commandParser.Reset();
                var oldCursor = _cursor;
                _cursor = _visualStart;
                _visualStart = oldCursor;
                UpdateSelection(events);
                return;
            case "I":
            case "i":
                if (_mode == VimMode.VisualBlock)
                {
                    _commandParser.Reset();
                    BeginVisualBlockInsert(events);
                    return;
                }
                if (key == "i")
                {
                    if (_mode == VimMode.Visual)
                    {
                        _awaitingVisualTextObj = 'i';
                        _awaitingVisualTextObjCount = 1;
                    }
                    else
                    {
                        _discardingUnsupportedVisualTextObj = true;
                    }
                    return;
                }
                break;
            case "A":
                if (_mode == VimMode.VisualBlock)
                {
                    _commandParser.Reset();
                    BeginVisualBlockAppend(events);
                    return;
                }
                break;
            case "a":
                if (_mode == VimMode.Visual)
                {
                    _awaitingVisualTextObj = 'a';
                    _awaitingVisualTextObjCount = 1;
                }
                else
                {
                    _discardingUnsupportedVisualTextObj = true;
                }
                return;
            // Operators on selection
            case "d":
            case "x":
            case "X":
            case "D":
                _commandParser.Reset();
                _visualEditOps.ExecuteVisualDelete(ConsumeVisualRegister(), events);
                return;
            case "y":
                _commandParser.Reset();
                _visualEditOps.ExecuteVisualYank(ConsumeVisualRegister(), events);
                return;
            case "p":
            case "P":
                _commandParser.Reset();
                _visualEditOps.ExecuteVisualPaste(ConsumeVisualRegister(), events);
                return;
            case "c":
            case "C":
            case "s":
            case "S":
                _commandParser.Reset();
                var changeRegister = ConsumeVisualRegister();
                if (_mode == VimMode.VisualBlock)
                    BeginVisualBlockChange(changeRegister, events);
                else
                {
                    _visualEditOps.ExecuteVisualDelete(changeRegister, events);
                    EnterInsertMode(false, events);
                }
                return;
            case ">":
                _commandParser.Reset();
                _visualEditOps.ExecuteVisualIndent(true, events);
                return;
            case "<":
                _commandParser.Reset();
                _visualEditOps.ExecuteVisualIndent(false, events);
                return;
            case "~":
                _commandParser.Reset();
                _visualEditOps.ExecuteVisualToggleCase(events);
                return;
            case "r":
                if (_mode == VimMode.VisualBlock)
                {
                    _commandParser.Reset();
                    _awaitingBlockReplace = true;
                    EmitStatus(events, "r");
                    return;
                }
                break;
        }

        var (state, cmd) = _commandParser.Feed(key);
        if (state == CommandState.Incomplete)
            return;
        if (state == CommandState.Invalid || cmd == null)
        {
            _commandParser.Reset();
            return;
        }

        if (!ApplyVisualMotion(cmd.Value, events))
            return;

        if (!IsVerticalVisualMotion(cmd.Value.Motion)) SetPreferredColumn(_cursor.Column);

        UpdateSelection(events);
    }

    private bool TryHandlePendingVisualMotion(string key, List<VimEvent> events)
    {
        if (string.IsNullOrEmpty(_commandParser.Buffer))
            return false;

        // gc/gu/gU/g~ in visual mode: operate on the selection
        if (_commandParser.Buffer == "g" && key is "c" or "u" or "U" or "~")
        {
            _commandParser.Reset();
            if (key == "c") _visualEditOps.ExecuteVisualComment(events);
            else _visualEditOps.ExecuteVisualCaseConvert(key switch { "u" => CaseConversion.Lower, "U" => CaseConversion.Upper, _ => CaseConversion.Toggle }, events);
            return true;
        }

        var (state, cmd) = _commandParser.Feed(key);
        if (state == CommandState.Incomplete)
            return true;

        if (state == CommandState.Invalid || cmd == null)
        {
            _commandParser.Reset();
            return false;
        }

        if (!ApplyVisualMotion(cmd.Value, events))
            return false;

        if (!IsVerticalVisualMotion(cmd.Value.Motion)) SetPreferredColumn(_cursor.Column);

        UpdateSelection(events);
        return true;
    }

    private static bool TryParseVisualCount(string buffer, out int count)
    {
        count = 1;
        if (string.IsNullOrEmpty(buffer))
            return false;

        for (int i = 0; i < buffer.Length; i++)
        {
            if (!char.IsDigit(buffer[i]) || (i == 0 && buffer[i] == '0'))
                return false;
        }

        return int.TryParse(buffer, out count) && count > 0;
    }

    private bool ApplyVisualMotion(ParsedCommand cmd, List<VimEvent> events)
    {
        if (cmd.Operator != null) return false;

        var buf = _bufferManager.Current.Text;
        var motion = new MotionEngine(buf, _bufferManager.Current.FilePath);
        int count = Math.Max(1, cmd.Count);
        if (_mode == VimMode.VisualBlock && cmd.Motion != "$" && !PreservesVisualBlockLineEnd(cmd.Motion))
            _visualBlockToLineEnd = false;

        switch (cmd.Motion)
        {
            case "Left":
            case "h":
                _cursor = motion.MoveLeft(_cursor, count);
                SetPreferredColumn(_cursor.Column);
                return true;
            case "Right":
            case "l":
                _cursor = motion.MoveRight(_cursor, count);
                SetPreferredColumn(_cursor.Column);
                return true;
            case "Up":
            case "k":
                MoveVerticalCursor(-count);
                return true;
            case "Down":
            case "j":
                MoveVerticalCursor(count);
                return true;
            case "0":
                _cursor = _cursor with { Column = 0 };
                SetPreferredColumn(0);
                return true;
            case "^":
                _cursor = _cursor with { Column = GetFirstNonBlank() };
                SetPreferredColumn(_cursor.Column);
                return true;
            case "$":
                _cursor = _cursor with { Column = Math.Max(0, GetLineLength() - 1) };
                SetPreferredColumn(_cursor.Column);
                if (_mode == VimMode.VisualBlock)
                {
                    _visualBlockToLineEnd = true;
                    _visualBlockLineEndStartColumn = _visualStart.Column;
                }
                return true;
            case "w":
                _cursor = motion.WordForward(_cursor, count, false);
                return true;
            case "W":
                _cursor = motion.WordForward(_cursor, count, true);
                return true;
            case "b":
                _cursor = motion.WordBackward(_cursor, count, false);
                return true;
            case "B":
                _cursor = motion.WordBackward(_cursor, count, true);
                return true;
            case "e":
                _cursor = motion.WordEnd(_cursor, count, false);
                return true;
            case "E":
                _cursor = motion.WordEnd(_cursor, count, true);
                return true;
            case "ge":
                _cursor = new TextObjectEngine(buf).WordEndBackward(_cursor, count);
                return true;
            case "gE":
                _cursor = new TextObjectEngine(buf).WordEndBackward(_cursor, count);
                return true;
            case "gj":
                MoveVerticalCursor(count);
                return true;
            case "gk":
                MoveVerticalCursor(-count);
                return true;
            case "g_":
                var g_vm = motion.Calculate("g_", _cursor, count);
                if (g_vm.HasValue) _cursor = g_vm.Value.Target;
                return true;
            case "gg":
                _cursor = new CursorPosition(0, 0);
                SetPreferredColumn(0);
                return true;
            case "G":
                var targetLine = count == 1 ? buf.LineCount - 1 : count - 1;
                _cursor = new CursorPosition(Math.Clamp(targetLine, 0, buf.LineCount - 1), 0);
                SetPreferredColumn(0);
                return true;
            case "+":
                var downLine = Math.Clamp(_cursor.Line + count, 0, buf.LineCount - 1);
                _cursor = new CursorPosition(downLine, GetFirstNonBlank(downLine));
                SetPreferredColumn(_cursor.Column);
                return true;
            case "-":
                var upLine = Math.Clamp(_cursor.Line - count, 0, buf.LineCount - 1);
                _cursor = new CursorPosition(upLine, GetFirstNonBlank(upLine));
                SetPreferredColumn(_cursor.Column);
                return true;
            case "_":
                var underLine = Math.Clamp(_cursor.Line + Math.Max(0, count - 1), 0, buf.LineCount - 1);
                _cursor = new CursorPosition(underLine, GetFirstNonBlank(underLine));
                SetPreferredColumn(_cursor.Column);
                return true;
            case "|":
                _cursor = _cursor with { Column = Math.Clamp(count - 1, 0, Math.Max(0, GetLineLength() - 1)) };
                SetPreferredColumn(_cursor.Column);
                return true;
            case "{":
                for (int i = 0; i < count; i++)
                    _cursor = ParagraphBackward();
                return true;
            case "}":
                for (int i = 0; i < count; i++)
                    _cursor = ParagraphForward();
                return true;
            case "%":
                var mb = MatchBracket(buf, motion);
                if (mb.HasValue)
                    _cursor = mb.Value;
                return true;
            case "H":
                _cursor = ScreenPosition(Math.Max(0, count - 1));
                return true;
            case "M":
                _cursor = ScreenPosition(_viewportVisibleLines / 2);
                return true;
            case "L":
                _cursor = ScreenPosition(Math.Max(0, _viewportVisibleLines - count));
                return true;
            case ";":
                for (int i = 0; i < count; i++)
                    _searchOps.RepeatFind(_cursor, false, events);
                return true;
            case ",":
                for (int i = 0; i < count; i++)
                    _searchOps.RepeatFind(_cursor, true, events);
                return true;
            case "n":
                for (int i = 0; i < count; i++)
                    _searchOps.SearchNext(_cursor, true, events);
                return true;
            case "N":
                for (int i = 0; i < count; i++)
                    _searchOps.SearchNext(_cursor, false, events);
                return true;
            case "gn":
            {
                var gnm = _searchOps.FindGnMatch(_cursor, true);
                if (gnm.HasValue) _cursor = gnm.Value.End;
                return true;
            }
            case "gN":
            {
                var gnm = _searchOps.FindGnMatch(_cursor, false);
                if (gnm.HasValue) _cursor = gnm.Value.Start;
                return true;
            }
            case "*":
                _searchOps.SearchWordUnderCursor(_cursor, true, events);
                return true;
            case "#":
                _searchOps.SearchWordUnderCursor(_cursor, false, events);
                return true;
            case "f":
            case "F":
            case "t":
            case "T":
                if (!cmd.FindChar.HasValue) return false;
                bool fwd = cmd.Motion is "f" or "t";
                bool before = cmd.Motion is "t" or "T";
                _cursor = motion.FindChar(_cursor, cmd.FindChar.Value, fwd, before, count);
                return true;
            case var r when r?.StartsWith('r') == true && r.Length == 2:
                _visualEditOps.ExecuteVisualReplace(r[1], events);
                return true;
            case "za": CurrentBuffer.Folds.ToggleFold(_cursor.Line); events.Add(VimEvent.FoldsChanged()); return false;
            case "zo": CurrentBuffer.Folds.OpenFold(_cursor.Line); events.Add(VimEvent.FoldsChanged()); return false;
            case "zc": CurrentBuffer.Folds.CloseFold(_cursor.Line); events.Add(VimEvent.FoldsChanged()); return false;
            case "zM": CurrentBuffer.Folds.CloseAll(); events.Add(VimEvent.FoldsChanged()); return false;
            case "zR": CurrentBuffer.Folds.OpenAll(); events.Add(VimEvent.FoldsChanged()); return false;
            case "zj": { int next = CurrentBuffer.Folds.NextFoldStart(_cursor.Line); if (next >= 0) MoveCursor(new CursorPosition(next, 0), events); return false; }
            case "zk": { int prev = CurrentBuffer.Folds.PrevFoldStart(_cursor.Line); if (prev >= 0) MoveCursor(new CursorPosition(prev, 0), events); return false; }
            case "[z": { int fs = CurrentBuffer.Folds.CurrentFoldStart(_cursor.Line); if (fs >= 0) MoveCursor(new CursorPosition(fs, 0), events); return false; }
            case "]z": { int fe = CurrentBuffer.Folds.CurrentFoldEnd(_cursor.Line); if (fe >= 0) MoveCursor(new CursorPosition(fe, 0), events); return false; }
            case "[m": MethodJump(false, false, count, events); return true;
            case "]m": MethodJump(true,  false, count, events); return true;
            case "[M": MethodJump(false, true,  count, events); return true;
            case "]M": MethodJump(true,  true,  count, events); return true;
            case "[[": case "]]": case "[]": case "][":
            { var sm = new MotionEngine(_bufferManager.Current.Text, _bufferManager.Current.FilePath).Calculate(cmd.Motion, _cursor, count); if (sm.HasValue && sm.Value.Target != _cursor) _cursor = sm.Value.Target; return true; }
            case "[{": case "]}": case "[(": case "])":
            { var bm = new MotionEngine(_bufferManager.Current.Text, _bufferManager.Current.FilePath).Calculate(cmd.Motion, _cursor, count); if (bm.HasValue && bm.Value.Target != _cursor) _cursor = bm.Value.Target; return true; }
            case "zd": CurrentBuffer.Folds.DeleteFold(_cursor.Line); events.Add(VimEvent.FoldsChanged()); ExitVisualMode(events); return false;
            case "zD": CurrentBuffer.Folds.DeleteFoldsAt(_cursor.Line); events.Add(VimEvent.FoldsChanged()); ExitVisualMode(events); return false;
            case "zE": CurrentBuffer.Folds.Clear(); events.Add(VimEvent.FoldsChanged()); return false;
            case "zn": _foldDisabled = true; events.Add(VimEvent.FoldsChanged()); return true;
            case "zN": _foldDisabled = false; events.Add(VimEvent.FoldsChanged()); return true;
            case "zf":
            {
                if (_selection != null)
                {
                    var selStart = Math.Min(_selection.Value.Start.Line, _selection.Value.End.Line);
                    var selEnd = Math.Max(_selection.Value.Start.Line, _selection.Value.End.Line);
                    if (selEnd > selStart)
                        CurrentBuffer.Folds.CreateFold(selStart, selEnd);
                    events.Add(VimEvent.FoldsChanged());
                    ExitVisualMode(events);
                }
                return false;
            }
            default:
                return false;
        }
    }

    private static bool PreservesVisualBlockLineEnd(string motion) =>
        motion is "Up" or "Down" or "k" or "j" or "gj" or "gk";

    // ─────────────── COMMAND LINE MODE ───────────────
    private void HandleCommandLine(string key, bool ctrl, bool shift, bool alt, List<VimEvent> events)
    {
        bool isSearch = _mode == VimMode.SearchForward || _mode == VimMode.SearchBackward;

        // Tab completion only in command (ex) mode, not search
        if (key == "Tab" && !isSearch)
        {
            CycleCompletion(!shift, events);
            return;
        }

        // Any key other than Tab resets completion state
        if (_completions.Length > 0)
        {
            _completions = [];
            _completionIndex = -1;
            events.Add(VimEvent.CommandCompletionChanged([], -1));
        }

        switch (key)
        {
            case "Escape":
                _cmdLine = "";
                if (isSearch)
                {
                    MoveCursor(_searchOps.PreSearchCursor, events);
                    // Restore highlights for the previous confirmed pattern (or clear)
                    if (!string.IsNullOrEmpty(_searchOps.Pattern) && _config.Options.HlSearch)
                    {
                        var buf2 = _bufferManager.Current.Text;
                        var ic2 = _searchOps.GetIgnoreCase(_searchOps.Pattern);
                        var all2 = buf2.FindAll(_searchOps.Pattern, ic2);
                        events.Add(VimEvent.SearchChanged(_searchOps.Pattern, all2.Count));
                    }
                    else
                    {
                        events.Add(VimEvent.SearchChanged("", 0));
                    }
                }
                ChangeMode(VimMode.Normal, events);
                EmitCmdLine(events);
                break;
            case "Return":
                ExecuteCommandLine(events);
                break;
            case "Back":
                if (_cmdLine.Length > 0)
                {
                    _cmdLine = _cmdLine[..^1];
                    if (_cmdLine.Length == 0)
                    {
                        if (isSearch)
                        {
                            MoveCursor(_searchOps.PreSearchCursor, events);
                            events.Add(VimEvent.SearchChanged("", 0));
                        }
                        ChangeMode(VimMode.Normal, events);
                    }
                    else if (isSearch && _config.Options.IncrSearch)
                    {
                        _searchOps.DoIncrSearch(_cmdLine, _mode == VimMode.SearchForward, events);
                    }
                }
                else ChangeMode(VimMode.Normal, events);
                EmitCmdLine(events);
                break;
            case "Up":
                if (isSearch)
                {
                    var sprev = _exProcessor.SearchHistoryPrev();
                    if (sprev != null) { _cmdLine = sprev; if (_config.Options.IncrSearch) _searchOps.DoIncrSearch(_cmdLine, _mode == VimMode.SearchForward, events); EmitCmdLine(events); }
                }
                else
                {
                    var prev = _exProcessor.HistoryPrev();
                    if (prev != null) { _cmdLine = prev; EmitCmdLine(events); }
                }
                break;
            case "Down":
                if (isSearch)
                {
                    var snext = _exProcessor.SearchHistoryNext();
                    if (snext != null) { _cmdLine = snext; if (_config.Options.IncrSearch) _searchOps.DoIncrSearch(_cmdLine, _mode == VimMode.SearchForward, events); EmitCmdLine(events); }
                }
                else
                {
                    var next = _exProcessor.HistoryNext();
                    if (next != null) { _cmdLine = next; EmitCmdLine(events); }
                }
                break;
            default:
                if (key.Length == 1 && !ctrl)
                {
                    _cmdLine += key;
                    if (isSearch && _config.Options.IncrSearch)
                        _searchOps.DoIncrSearch(_cmdLine, _mode == VimMode.SearchForward, events);
                }
                EmitCmdLine(events);
                break;
        }
    }

    private void CycleCompletion(bool forward, List<VimEvent> events)
    {
        if (_completions.Length == 0)
        {
            // Compute completions for the current cmdLine
            var dir = _bufferManager.Current.FilePath is { } fp
                ? System.IO.Path.GetDirectoryName(fp)
                : null;
            _completions = _exProcessor.GetCompletions(_cmdLine, dir);
            _completionIndex = -1;
        }

        if (_completions.Length == 0)
        {
            EmitCmdLine(events);
            return;
        }

        if (forward)
            _completionIndex = (_completionIndex + 1) % _completions.Length;
        else
            _completionIndex = (_completionIndex - 1 + _completions.Length) % _completions.Length;

        _cmdLine = _completions[_completionIndex];
        EmitCmdLine(events);
        events.Add(VimEvent.CommandCompletionChanged(_completions, _completionIndex));
    }

    private void ExecuteCommandLine(List<VimEvent> events)
    {
        if (_mode == VimMode.Command)
        {
            var cmdLine = _cmdLine;
            _cmdLine = "";
            ChangeMode(VimMode.Normal, events);
            ExecuteExCommand(cmdLine, events);
        }
        else // Search
        {
            _searchOps.Pattern = _cmdLine;
            _searchOps.Forward = _mode == VimMode.SearchForward;
            if (!string.IsNullOrEmpty(_cmdLine))
                _exProcessor.AddSearchHistory(_cmdLine);
            _cmdLine = "";
            _cursor = _searchOps.PreSearchCursor; // search from where we started, not incsearch preview pos
            ChangeMode(VimMode.Normal, events);
            _searchOps.DoSearch(_cursor, _searchOps.Forward, events);
        }
        EmitCmdLine(events);
    }

    /// <summary>
    /// Sets the active search pattern as if the user had searched for it, so all
    /// matches get highlighted (honouring <c>hlsearch</c>/<c>ignorecase</c>/<c>smartcase</c>),
    /// but WITHOUT moving the cursor or recording search history. Pass an empty
    /// string to clear the highlight. Intended for hosts that want to highlight a
    /// query (e.g. a grep hit opened from a sidebar) in the buffer. Matching is the
    /// same literal substring matching as <c>hlsearch</c>, so regex patterns are
    /// treated literally.
    /// </summary>
    public IReadOnlyList<VimEvent> SetSearchHighlight(string pattern)
    {
        var events = new List<VimEvent>();
        _searchOps.Pattern = pattern ?? "";
        if (!string.IsNullOrEmpty(_searchOps.Pattern) && _config.Options.HlSearch)
        {
            var ignoreCase = _searchOps.GetIgnoreCase(_searchOps.Pattern);
            var all = _bufferManager.Current.Text.FindAll(_searchOps.Pattern, ignoreCase);
            events.Add(VimEvent.SearchChanged(_searchOps.Pattern, all.Count));
        }
        else
        {
            events.Add(VimEvent.SearchChanged(_searchOps.Pattern, 0));
        }
        return events;
    }

    // ─────────────── HELPERS ───────────────

    private void EnterInsertMode(bool append, List<VimEvent> events)
    {
        Snapshot();
        _insertStart = _cursor;
        _insertedText = new StringBuilder();
        ChangeMode(VimMode.Insert, events);
    }

    private void EnterReplaceMode(List<VimEvent> events)
    {
        Snapshot();
        _insertedText = new StringBuilder();
        ChangeMode(VimMode.Replace, events);
    }

    private void ExitInsertMode(List<VimEvent> events)
    {
        // Move cursor left one (normal mode cursor)
        var buf = _bufferManager.Current.Text;
        if (_cursor.Column > 0 && _cursor.Column >= buf.GetLineLength(_cursor.Line))
            _cursor = _cursor with { Column = Math.Max(0, _cursor.Column - 1) };
        _lastInsertPos = _cursor;
        // "." register: the literal text typed during this Insert/Replace session (approximate —
        // does not retroactively account for Backspace, mirroring dot-repeat's own tolerance).
        _registerManager.SetLastInserted(_insertedText?.ToString() ?? "");
        _insertedText = null;
        _blockInsertOps.Clear();
        _awaitingInsertRegister = false;
        _awaitingExprRegister = false;
        _exprBuffer = "";
        _digraphPendingChar = null;
        _ctrlXPending = false;
        _ctrlXMode = '\0';
        _kwCompletionOps.Reset();
        ChangeMode(VimMode.Normal, events);
    }

    private void EnterVisualMode(VimMode visualMode, List<VimEvent> events)
    {
        _visualBlockToLineEnd = false;
        _visualBlockLineEndStartColumn = 0;
        _visualStart = _cursor;
        ChangeMode(visualMode, events);
        UpdateSelection(events);
    }

    private void ExitVisualMode(List<VimEvent> events)
    {
        _awaitingVisualTextObj = '\0';
        _awaitingVisualTextObjCount = 1;
        _discardingUnsupportedVisualTextObj = false;
        _awaitingVisualRegister = false;
        _visualPendingRegister = null;
        _lastVisualStart = _visualStart;
        _lastVisualEnd = _cursor;
        _lastVisualMode = _mode;

        // Set '<' and '>' marks to the normalized selection start/end.
        // For VisualLine mode, '>' lands on the last column of the end line (Vim behaviour).
        var selStart = _selection?.NormalizedStart ?? _visualStart;
        var selEnd   = _selection?.NormalizedEnd   ?? _cursor;

        if (_mode == VimMode.VisualLine)
        {
            selStart = selStart with { Column = 0 };
            var endLineLen = _bufferManager.Current.Text.GetLineLength(selEnd.Line);
            selEnd = selEnd with { Column = Math.Max(0, endLineLen - 1) };
        }

        _markManager.SetMark('<', selStart);
        _markManager.SetMark('>', selEnd);

        _selection = null;
        ChangeMode(VimMode.Normal, events);
        events.Add(VimEvent.SelectionChanged(null));
    }

    private void EnterCommandMode(List<VimEvent> events)
    {
        _cmdLine = "";
        ChangeMode(VimMode.Command, events);
        EmitCmdLine(events);
    }

    private void EnterCommandModeFromVisual(List<VimEvent> events)
    {
        int start = Math.Min(_visualStart.Line, _cursor.Line) + 1;
        int end = Math.Max(_visualStart.Line, _cursor.Line) + 1;
        _awaitingVisualTextObj = '\0';
        _awaitingVisualTextObjCount = 1;
        _discardingUnsupportedVisualTextObj = false;
        _selection = null;
        events.Add(VimEvent.SelectionChanged(null));
        _cmdLine = $"{start},{end}";
        ChangeMode(VimMode.Command, events);
        EmitCmdLine(events);
    }

    private void EnterSearchMode(bool forward, List<VimEvent> events)
    {
        _cmdLine = "";
        _searchOps.Forward = forward;
        _searchOps.PreSearchCursor = _cursor;
        ChangeMode(forward ? VimMode.SearchForward : VimMode.SearchBackward, events);
        EmitCmdLine(events);
    }

    // suppressInsertAutocmd: true for the two internal mode flips that implement Ctrl+O
    // (temporarily drop to Normal for one command, then return to Insert) — real Vim's
    // i_CTRL-O never fires InsertLeave/InsertEnter for that round trip since the user
    // never conceptually left Insert mode.
    private void ChangeMode(VimMode newMode, List<VimEvent> events, bool suppressInsertAutocmd = false)
    {
        var oldMode = _mode;
        _pendingMappedInput.Clear();
        if (newMode != VimMode.VisualBlock)
        {
            _visualBlockToLineEnd = false;
            _visualBlockLineEndStartColumn = 0;
        }
        _mode = newMode;
        events.Add(VimEvent.ModeChanged(newMode));

        bool wasInsertLike = oldMode is VimMode.Insert or VimMode.Replace;
        bool isInsertLike = newMode is VimMode.Insert or VimMode.Replace;
        if (!suppressInsertAutocmd && _bufferManager.Current.FilePath is { } path)
        {
            if (!wasInsertLike && isInsertLike)
                _autocmdRunner.RunAutocmds("InsertEnter", path);
            else if (wasInsertLike && !isInsertLike)
                _autocmdRunner.RunAutocmds("InsertLeave", path);
        }
    }

    private void MoveCursor(CursorPosition pos, List<VimEvent> events)
    {
        _cursor = _bufferManager.Current.Text.ClampCursor(pos, _mode == VimMode.Insert);
        SetPreferredColumn(_cursor.Column);
        EmitCursor(events);
    }

    private void UpdateSelection(List<VimEvent> events)
    {
        EmitCursor(events);
        _selection = new Selection(_visualStart, _cursor, _mode switch
        {
            VimMode.VisualLine => SelectionType.Line,
            VimMode.VisualBlock => SelectionType.Block,
            _ => SelectionType.Character
        });
        events.Add(VimEvent.SelectionChanged(_selection));
    }

    private static (int StartLine, int EndLine, int LeftColumn, int RightColumn) GetBlockBounds(Selection selection)
        => BlockRangeCalculator.GetBounds(selection);

    private int GetBlockLeftColumn(Selection selection)
        => BlockRangeCalculator.GetLeftColumn(selection, _visualBlockToLineEnd, _visualBlockLineEndStartColumn);

    private IEnumerable<BlockLineRange> GetBlockLineRanges(Selection selection)
        => BlockRangeCalculator.GetLineRanges(selection, _bufferManager.Current.Text, _visualBlockToLineEnd, _visualBlockLineEndStartColumn);

    private Dictionary<int, int> BuildBlockEditColumns(int startLine, int endLine, int column)
        => BlockRangeCalculator.BuildEditColumns(_bufferManager.Current.Text, startLine, endLine, column);

    private Dictionary<int, int> BuildBlockAppendToLineEndColumns(int startLine, int endLine)
        => BlockRangeCalculator.BuildAppendToLineEndColumns(_bufferManager.Current.Text, startLine, endLine);

    private void BeginVisualBlockInsert(List<VimEvent> events)
    {
        if (_selection == null) { ExitVisualMode(events); return; }
        var (startLine, endLine, _, _) = GetBlockBounds(_selection.Value);
        var leftColumn = GetBlockLeftColumn(_selection.Value);
        BeginVisualBlockEdit(startLine, endLine, leftColumn, events);
    }

    private void BeginVisualBlockAppend(List<VimEvent> events)
    {
        if (_selection == null) { ExitVisualMode(events); return; }
        var (startLine, endLine, _, rightColumn) = GetBlockBounds(_selection.Value);
        if (_visualBlockToLineEnd)
        {
            BeginVisualBlockEdit(startLine, endLine, BuildBlockAppendToLineEndColumns(startLine, endLine), events);
            return;
        }

        BeginVisualBlockEdit(startLine, endLine, rightColumn + 1, events);
    }

    // Shared setup for block-insert (I) and block-append (A): clears selection,
    // places cursor at the given column on the first selected line, and enters Insert mode.
    private void BeginVisualBlockEdit(int startLine, int endLine, int column, List<VimEvent> events)
    {
        BeginVisualBlockEdit(startLine, endLine, BuildBlockEditColumns(startLine, endLine, column), events);
    }

    private void BeginVisualBlockEdit(int startLine, int endLine, Dictionary<int, int> lineColumns, List<VimEvent> events)
    {
        _selection = null;
        events.Add(VimEvent.SelectionChanged(null));
        var column = lineColumns.TryGetValue(startLine, out var startColumn) ? startColumn : 0;
        _blockInsertOps.Begin(startLine, endLine, column, lineColumns);
        _cursor = _bufferManager.Current.Text.ClampCursor(new CursorPosition(startLine, column), insertMode: true);
        EnterInsertMode(false, events);
        EmitCursor(events);
    }

    private void BeginVisualBlockChange(char register, List<VimEvent> events)
    {
        if (_selection == null) { ExitVisualMode(events); return; }
        var sel = _selection.Value;
        var (startLine, endLine, _, _) = GetBlockBounds(sel);
        var leftColumn = GetBlockLeftColumn(sel);

        Snapshot();
        _clipboardOps.YankBlock(register, sel, _visualBlockToLineEnd, _visualBlockLineEndStartColumn, isDelete: true);
        _clipboardOps.DeleteBlock(sel, _visualBlockToLineEnd, _visualBlockLineEndStartColumn);
        _cursor = _bufferManager.Current.Text.ClampCursor(new CursorPosition(startLine, leftColumn), insertMode: true);
        EmitText(events);

        _selection = null;
        events.Add(VimEvent.SelectionChanged(null));
        _blockInsertOps.Begin(startLine, endLine, _cursor.Column, BuildBlockEditColumns(startLine, endLine, leftColumn));
        EnterInsertMode(false, events);
    }

    private void ExecuteBlockReplace(char ch, List<VimEvent> events)
    {
        if (_selection == null) { ExitVisualMode(events); return; }
        Snapshot();
        _textTransform.ReplaceBlock(_selection.Value, _visualBlockToLineEnd, _visualBlockLineEndStartColumn, ch);
        var (startLine, _, _, _) = GetBlockBounds(_selection.Value);
        var leftColumn = GetBlockLeftColumn(_selection.Value);
        _cursor = new CursorPosition(startLine, leftColumn);
        ExitVisualMode(events);
        EmitText(events);
    }

    private void MoveVertical(int delta, List<VimEvent> events)
    {
        MoveVerticalCursor(delta);
        EmitCursor(events);
    }

    private void MoveVerticalCursor(int delta, bool insertMode = false)
    {
        var buf = _bufferManager.Current.Text;
        var folds = CurrentBuffer.Folds;
        int[] visMap = folds.BuildVisibleLineMap(buf.LineCount);
        int currentVis = folds.BufferToVisualLine(_cursor.Line, visMap);
        if (currentVis < 0) currentVis = 0;
        int targetVis = Math.Clamp(currentVis + delta, 0, visMap.Length - 1);
        int newLine = visMap[targetVis];
        int maxCol = insertMode ? buf.GetLineLength(newLine) : Math.Max(0, buf.GetLineLength(newLine) - 1);
        _cursor = new CursorPosition(newLine, ResolveVerticalColumn(newLine, maxCol));
    }

    private static bool IsVerticalVisualMotion(string motion) =>
        motion is "Up" or "Down" or "j" or "k" or "gj" or "gk";


    private void GoToLineEnd(bool insertMode, List<VimEvent> events)
    {
        var buf = _bufferManager.Current.Text;
        var len = buf.GetLineLength(_cursor.Line);
        var col = insertMode ? len : Math.Max(0, len - 1);
        _cursor = _cursor with { Column = col };
        SetPreferredColumn(int.MaxValue);
        EmitCursor(events);
    }

    private void GoToLineStart(List<VimEvent> events)
    {
        _cursor = _cursor with { Column = 0 };
        EmitCursor(events);
    }

    private int GetFirstNonBlank(int lineNo)
    {
        var buf = _bufferManager.Current.Text;
        var line = buf.GetLine(Math.Clamp(lineNo, 0, buf.LineCount - 1));
        int col = 0;
        while (col < line.Length && char.IsWhiteSpace(line[col])) col++;
        return col;
    }

    private int GetFirstNonBlank() => GetFirstNonBlank(_cursor.Line);

    private void MoveLineAndFirstNonBlank(int lineDelta, List<VimEvent> events)
    {
        var buf = _bufferManager.Current.Text;
        var line = Math.Clamp(_cursor.Line + lineDelta, 0, buf.LineCount - 1);
        var col = GetFirstNonBlank(line);
        SetPreferredColumn(col);
        MoveCursor(new CursorPosition(line, col), events);
    }

    private void MoveToColumn(int oneBasedColumn, List<VimEvent> events)
    {
        var maxCol = Math.Max(0, _bufferManager.Current.Text.GetLineLength(_cursor.Line) - 1);
        var col = Math.Clamp(oneBasedColumn - 1, 0, maxCol);
        SetPreferredColumn(col);
        MoveCursor(_cursor with { Column = col }, events);
    }

    private int GetLineLength() => _bufferManager.Current.Text.GetLineLength(_cursor.Line);

    private void SetPreferredColumn(int column)
    {
        _preferredColumn = column;
        _preferredLine = _cursor.Line;
    }

    private int ResolveVerticalColumn(int targetLine, int maxColumn)
    {
        if (_preferredColumn == int.MaxValue) return maxColumn;
        if (VerticalColumnResolver == null) return Math.Min(_preferredColumn, maxColumn);
        return Math.Clamp(VerticalColumnResolver(_preferredLine, _preferredColumn, targetLine, maxColumn), 0, maxColumn);
    }

    private void OpenLineBelow(List<VimEvent> events)
    {
        Snapshot();
        var buf = _bufferManager.Current.Text;
        var indent = _editAssists.OpenLinePrefix(MakeEditContext(), above: false)
                     ?? GetAutoIndent(buf, _cursor.Line);
        CurrentBuffer.Folds.OnLinesInserted(_cursor.Line, 1);
        buf.InsertLines(_cursor.Line, [indent]);
        _cursor = new CursorPosition(_cursor.Line + 1, indent.Length);
        EmitText(events);
    }

    private void OpenLineAbove(List<VimEvent> events)
    {
        Snapshot();
        var buf = _bufferManager.Current.Text;
        var indent = _editAssists.OpenLinePrefix(MakeEditContext(), above: true)
                     ?? GetAutoIndent(buf, _cursor.Line);
        CurrentBuffer.Folds.OnLinesInserted(_cursor.Line - 1, 1);
        buf.InsertLineAbove(_cursor.Line, indent);
        _cursor = _cursor with { Column = indent.Length };
        EmitText(events);
    }

    private static string GetAutoIndent(TextBuffer buf, int line)
    {
        if (line < 0 || line >= buf.LineCount) return "";
        var ln = buf.GetLine(line);
        int i = 0;
        while (i < ln.Length && char.IsWhiteSpace(ln[i])) i++;
        return ln[..i];
    }

    private void SelectAllVisualLine(List<VimEvent> events)
    {
        var buf = _bufferManager.Current.Text;
        _visualStart = new CursorPosition(0, 0);
        _cursor = new CursorPosition(buf.LineCount - 1, 0);
        if (_mode != VimMode.VisualLine) ChangeMode(VimMode.VisualLine, events);
        UpdateSelection(events);
    }

    private void PasteAtCursorInsertMode(List<VimEvent> events)
    {
        var reg = _registerManager.Get('+');
        if (reg.IsEmpty) return;
        Snapshot();
        InsertTextAtCursor(reg.Text, events);
    }

    private void InsertRegisterContent(char regName, List<VimEvent> events)
    {
        var reg = _registerManager.Get(regName);
        if (reg.IsEmpty) return;
        Snapshot();
        InsertTextAtCursor(reg.Text, events);
    }

    private void InsertTextAtCursor(string rawText, List<VimEvent> events)
    {
        var buf = _bufferManager.Current.Text;
        var text = rawText.Replace("\r\n", "\n").Replace("\r", "\n");
        var parts = text.Split('\n');
        if (parts.Length == 1)
        {
            buf.InsertText(_cursor.Line, _cursor.Column, text);
            _cursor = _cursor with { Column = _cursor.Column + text.Length };
        }
        else
        {
            buf.InsertText(_cursor.Line, _cursor.Column, parts[0]);
            int line = _cursor.Line;
            int col = _cursor.Column + parts[0].Length;
            for (int i = 1; i < parts.Length; i++)
            {
                buf.BreakLine(line, col);
                line++;
                buf.InsertText(line, 0, parts[i]);
                col = parts[i].Length;
            }
            CurrentBuffer.Folds.OnLinesInserted(_cursor.Line, parts.Length - 1);
            _cursor = new CursorPosition(line, col);
        }
        EmitText(events);
    }

    private void ExecuteUndo(List<VimEvent> events)
    {
        var vbuf = _bufferManager.Current;
        var state = vbuf.Undo.Undo(vbuf.Text, _cursor);
        if (state != null)
        {
            _cursor = vbuf.Text.ClampCursor(state.Cursor);
            vbuf.Folds.Clear();
            EmitText(events);
            events.Add(VimEvent.FoldsChanged());
            EmitStatus(events, "1 change undone");
        }
        else EmitStatus(events, "Already at oldest change");
    }

    private void ExecuteRedo(List<VimEvent> events)
    {
        var vbuf = _bufferManager.Current;
        var state = vbuf.Undo.Redo(vbuf.Text, _cursor);
        if (state != null)
        {
            _cursor = vbuf.Text.ClampCursor(state.Cursor);
            vbuf.Folds.Clear();
            EmitText(events);
            events.Add(VimEvent.FoldsChanged());
            EmitStatus(events, "1 change redone");
        }
        else EmitStatus(events, "Already at newest change");
    }

    private void AutoIndentRange(int start, int end, List<VimEvent> events) { EmitText(events); }

    // Markdown list Tab indenting: in a .md/.markdown file, when the cursor line
    // is a list item, Tab should indent (and Shift+Tab dedent) the whole item by
    // one shiftwidth — matching Obsidian/VS Code — instead of inserting a tab at
    // the cursor. Callers test this first and fall back to normal Tab otherwise.
    // ─────────────── SPELL ───────────────
    private void SyncSpellChecker()
    {
        bool enabled = _config.Options.Spell;
        if (enabled && !_spellChecker.IsLoaded)
            _spellChecker.Load();
        _spellChecker.IsEnabled = enabled;
    }

    public IReadOnlyList<(int Start, int End)> GetSpellErrors(int lineIndex)
    {
        if (!_config.Options.Spell || !_spellChecker.IsLoaded) return [];
        var line = _bufferManager.Current.Text.GetLine(lineIndex);
        return _spellChecker.FindErrors(line);
    }

    private void MethodJump(bool forward, bool toEnd, int count, List<VimEvent> events)
    {
        var buf = _bufferManager.Current.Text;
        int line = _cursor.Line;
        int found = 0;
        int step = forward ? 1 : -1;
        line += step;
        while (line >= 0 && line < buf.LineCount)
        {
            var ln = buf.GetLine(line).AsSpan().Trim();
            bool matches = toEnd
                ? (ln.SequenceEqual("}") || ln.SequenceEqual("};"))
                : (ln.EndsWith("{") && ln.Length > 1);
            if (matches)
            {
                found++;
                if (found >= count)
                {
                    _markManager.AddJump(_cursor);
                    MoveCursor(new CursorPosition(line, 0), events);
                    return;
                }
            }
            line += step;
        }
    }

    private void NavigateSpellError(bool forward, int count, List<VimEvent> events)
    {
        if (!_config.Options.Spell) { EmitStatus(events, "Spell checking not enabled"); return; }
        if (!_spellChecker.IsLoaded) { EmitStatus(events, "No spell dictionary loaded"); return; }

        var buf = _bufferManager.Current.Text;
        int line = _cursor.Line;
        int col = _cursor.Column;

        for (int step = 0; step < count; step++)
        {
            bool found = false;
            if (forward)
            {
                for (int l = line; l < buf.LineCount && !found; l++)
                {
                    var errors = _spellChecker.FindErrors(buf.GetLine(l));
                    foreach (var (s, e) in errors)
                    {
                        if (l > line || s > col)
                        {
                            line = l; col = s; found = true; break;
                        }
                    }
                }
                if (!found) { EmitStatus(events, "No more misspelled words"); return; }
            }
            else
            {
                for (int l = line; l >= 0 && !found; l--)
                {
                    var errors = _spellChecker.FindErrors(buf.GetLine(l));
                    for (int i = errors.Count - 1; i >= 0; i--)
                    {
                        var (s, e) = errors[i];
                        if (l < line || s < col)
                        {
                            line = l; col = s; found = true; break;
                        }
                    }
                }
                if (!found) { EmitStatus(events, "No previous misspelled words"); return; }
            }
        }

        MoveCursor(new CursorPosition(line, col), events);
    }

    private void ShowSpellSuggestions(List<VimEvent> events)
    {
        if (!_config.Options.Spell) { EmitStatus(events, "Spell checking not enabled"); return; }
        if (!_spellChecker.IsLoaded) { EmitStatus(events, "No spell dictionary loaded"); return; }

        var buf = _bufferManager.Current.Text;
        var line = buf.GetLine(_cursor.Line);
        // Find the word under cursor
        int col = Math.Clamp(_cursor.Column, 0, Math.Max(0, line.Length - 1));
        int start = col;
        while (start > 0 && char.IsLetter(line[start - 1])) start--;
        int end = col;
        while (end < line.Length && char.IsLetter(line[end])) end++;
        if (start >= end) { EmitStatus(events, "No word under cursor"); return; }

        var word = line[start..end];
        var suggestions = _spellChecker.Suggest(word);
        if (suggestions.Count == 0)
            EmitStatus(events, $"No suggestions for '{word}'");
        else
            EmitStatus(events, $"Suggestions for '{word}': {string.Join(", ", suggestions.Take(5))}");
    }

    private void ScrollHalfPage(bool down, List<VimEvent> events)
    {
        var lines = down ? 15 : -15;
        var newLine = Math.Clamp(_cursor.Line + lines, 0, _bufferManager.Current.Text.LineCount - 1);
        _cursor = _cursor with { Line = newLine };
        EmitCursor(events);
    }

    private void ScrollPage(bool down, List<VimEvent> events)
    {
        var lines = down ? 30 : -30;
        var newLine = Math.Clamp(_cursor.Line + lines, 0, _bufferManager.Current.Text.LineCount - 1);
        _cursor = _cursor with { Line = newLine };
        EmitCursor(events);
    }

    /// <summary>
    /// Ctrl+E (down=true) / Ctrl+Y (down=false): scroll the viewport by 1 line,
    /// keeping the cursor in the same buffer position unless that position scrolls
    /// off-screen, in which case the cursor is clamped to the new top/bottom
    /// visible line.  The UI layer performs the actual scroll offset change;
    /// the engine emits ScrollLinesRequested so the canvas knows how far to move,
    /// plus a CursorMoved if the cursor had to be adjusted.
    /// </summary>
    private void ScrollLine(bool down, List<VimEvent> events)
    {
        int delta = down ? 1 : -1;
        int lineCount = _bufferManager.Current.Text.LineCount;

        // Clamp the new top line to valid range.
        int newTopLine = Math.Clamp(_viewportTopLine + delta, 0, Math.Max(0, lineCount - 1));

        // Update immediately so that batched calls (e.g. 5<C-e>) accumulate correctly.
        _viewportTopLine = newTopLine;

        // If the cursor would go above the new top line (Ctrl+E scrolled it off top),
        // move cursor down to the new top.
        // If cursor would go below the last visible line (Ctrl+Y scrolled it off bottom),
        // move cursor up to the new bottom.
        int newBottomLine = Math.Min(lineCount - 1, newTopLine + _viewportVisibleLines - 1);
        int newLine = Math.Clamp(_cursor.Line, newTopLine, newBottomLine);

        if (newLine != _cursor.Line)
        {
            _cursor = _cursor with { Line = newLine };
            EmitCursor(events);
        }

        // Tell the UI to scroll by delta lines.
        events.Add(VimEvent.ScrollLinesRequested(delta));
    }

    private CursorPosition ParagraphBackward()
    {
        int line = _cursor.Line - 1;
        while (line > 0 && !string.IsNullOrWhiteSpace(_bufferManager.Current.Text.GetLine(line)))
            line--;
        return new CursorPosition(Math.Max(0, line), 0);
    }

    private CursorPosition ParagraphForward()
    {
        var buf = _bufferManager.Current.Text;
        int line = _cursor.Line + 1;
        while (line < buf.LineCount && !string.IsNullOrWhiteSpace(buf.GetLine(line)))
            line++;
        return new CursorPosition(Math.Min(line, buf.LineCount - 1), 0);
    }

    private CursorPosition? MatchBracket(TextBuffer buf, MotionEngine motion)
    {
        var mot = motion.Calculate("%", _cursor);
        return mot?.Target;
    }

    private CursorPosition ScreenPosition(int offsetFromTop)
    {
        var lineCount = _bufferManager.Current.Text.LineCount;
        var line = Math.Clamp(_viewportTopLine + offsetFromTop, 0, lineCount - 1);
        return new CursorPosition(line, GetFirstNonBlank(line));
    }

    private void ExecuteExCommand(string cmdLine, List<VimEvent> events)
    {
        _registerManager.SetLastCommand(cmdLine); // ":" register — last executed Ex command line
        if (_autocmdRunner.TryExecuteConfigCommand(cmdLine, out var msg, out var err, out var optChanged))
        {
            if (err != null) EmitStatus(events, "E: " + err);
            else if (msg != null) EmitStatus(events, msg);
            if (optChanged) events.Add(VimEvent.OptionsChanged());
            return;
        }
        if (_normalCmdExecutor.TryExecute(cmdLine, events)) return;
        var preLines = CurrentBuffer.Text.Snapshot();
        var preCursor = _cursor;
        var result = _exProcessor.Execute(cmdLine, _cursor);
        _registerManager.SetCurrentFileName(_bufferManager.Current.FilePath); // "%" — reflect buffer switches (:bn/:bp/:b/:bd)
        if (!result.Success) EmitStatus(events, "E: " + result.Message);
        else if (result.Message != null) EmitStatus(events, result.Message);
        if (result.BufferRestored)
        {
            _cursor = CurrentBuffer.Text.ClampCursor(result.RestoredCursor ?? _cursor);
            CurrentBuffer.Folds.Clear();
            EmitText(events);
            events.Add(VimEvent.FoldsChanged());
        }
        else if (result.TextModified) { CurrentBuffer.Undo.Snapshot(preLines, preCursor); EmitText(events); }
        if (result.Event != null) events.Add(result.Event);
    }

    private void SwitchToAlternateBuffer(List<VimEvent> events)
    {
        if (!_bufferManager.GoToAlternate())
        {
            EmitStatus(events, "E23: No alternate file");
            return;
        }
        var filePath = _bufferManager.Current.FilePath;
        if (filePath != null)
            events.Add(VimEvent.OpenFileRequested(filePath));
        EmitText(events);
    }

    private void PlayMacro(char register, int count, List<VimEvent> events)
    {
        // @@ repeats the last played macro
        char resolved = register == '@' ? _macroManager.LastPlayedRegister : register;
        if (resolved == '\0') { EmitStatus(events, "E748: No previously used register"); return; }

        var macro = _macroManager.GetMacro(resolved);
        if (macro == null) { EmitStatus(events, $"No macro in register @{resolved}"); return; }
        _macroManager.SetLastPlayed(resolved);

        for (int r = 0; r < count; r++)
        {
            foreach (var stroke in macro)
                ProcessKeyInternal(stroke.Key, stroke.Ctrl, stroke.Shift, stroke.Alt, events);
        }
    }

    // Returns the register selected via a visual-mode " prefix (default ") and
    // clears the pending selection so it applies only to the next operator.
    private char ConsumeVisualRegister()
    {
        var register = _visualPendingRegister ?? '"';
        _visualPendingRegister = null;
        _awaitingVisualRegister = false;
        return register;
    }

    // ─── Abbreviation expansion ───
    // Called after inserting a non-word trigger character (space/punct) or before Return/Tab.
    // triggerCharAlreadyInserted=true  → cursor sits just after the trigger, word ends one before.
    // triggerCharAlreadyInserted=false → cursor sits at end of last word (Return/Tab case).
    private void TryExpandAbbreviation(TextBuffer buf, List<VimEvent> events,
        bool triggerCharAlreadyInserted = false)
    {
        if (_config.Abbreviations.Count == 0) return;
        var line = buf.GetLine(_cursor.Line);
        // The column just before the trigger (or at cursor for Return/Tab)
        int endCol = triggerCharAlreadyInserted ? _cursor.Column - 1 : _cursor.Column;
        if (endCol <= 0) return;
        // Walk back over word characters
        int startCol = endCol;
        while (startCol > 0 && MotionEngine.IsWordChar(line[startCol - 1]))
            startCol--;
        if (startCol == endCol) return; // no word found
        var word = line[startCol..endCol];
        if (!_config.Abbreviations.TryGetValue(word, out var expansion)) return;
        // Replace the abbreviated word with the expansion
        buf.DeleteRange(_cursor.Line, startCol, endCol);
        buf.InsertText(_cursor.Line, startCol, expansion);
        int delta = expansion.Length - word.Length;
        _cursor = _cursor with { Column = _cursor.Column + delta };
    }

    private static char? GetAutoPairClose(char open) => open switch
    {
        '(' => ')',
        '[' => ']',
        '{' => '}',
        '"' => '"',
        '`' => '`',
        _ => null
    };

    // Builds the context passed to filetype edit-assists for the current buffer/caret.
    private EditContext MakeEditContext() => new(
        _bufferManager.Current.Text,
        _cursor,
        _bufferManager.Current.FilePath,
        _config.Options.ShiftWidth,
        _config.Options.ExpandTab);

    // Lets a filetype edit-assist handle Tab/Shift+Tab. Returns true (and emits) when handled.
    private bool TryEditAssistTab(bool shift, List<VimEvent> events)
    {
        var result = _editAssists.OnTab(MakeEditContext(), shift);
        if (!result.Handled) return false;
        _cursor = result.Cursor;
        EmitText(events);
        return true;
    }

    private void InsertNewline(List<VimEvent> events)
    {
        var buf = _bufferManager.Current.Text;

        // Let a filetype edit-assist (e.g. Markdown list continuation) handle Enter first.
        var enterResult = _editAssists.OnEnter(MakeEditContext());
        if (enterResult.Handled)
        {
            _cursor = enterResult.Cursor;
            EmitText(events);
            return;
        }

        var indent = !_config.Options.Paste && _config.Options.AutoIndent ? GetAutoIndent(buf, _cursor.Line) : "";
        buf.BreakLine(_cursor.Line, _cursor.Column);
        _cursor = new CursorPosition(_cursor.Line + 1, 0);
        if (indent.Length > 0)
        {
            buf.InsertText(_cursor.Line, 0, indent);
            _cursor = _cursor with { Column = indent.Length };
        }
        EmitText(events);
    }

    private void Snapshot()
    {
        if (_suppressSnapshot) return;
        var vbuf = _bufferManager.Current;
        vbuf.Undo.Snapshot(vbuf.Text, _cursor);
        _markManager.AddChange(_cursor);
        _markManager.SetMark('.', _cursor);
    }

    // ─────────────── Event helpers ───────────────
    private void EmitCursor(List<VimEvent> events) => events.Add(VimEvent.CursorMoved(_cursor));
    private void EmitText(List<VimEvent> events) { _syntaxEngine.Invalidate(); events.Add(VimEvent.TextChanged()); events.Add(VimEvent.CursorMoved(_cursor)); }
    private void EmitStatus(List<VimEvent> events, string msg) { _statusMsg = msg; events.Add(VimEvent.StatusMessage(msg)); }

    /// <summary>Emit Ctrl+G / g&lt;C-g&gt; file-info status message.</summary>
    private void EmitFileInfo(List<VimEvent> events, bool brief)
        => EmitStatus(events, BufferInfoReporter.BuildFileInfo(_bufferManager.Current, _cursor, brief));

    private void EmitCmdLine(List<VimEvent> events)
    {
        var prefix = _mode switch { VimMode.Command => ":", VimMode.SearchForward => "/", VimMode.SearchBackward => "?", _ => "" };
        events.Add(VimEvent.CommandLineChanged(prefix + _cmdLine));
        if (_mode == VimMode.Command)
            events.Add(VimEvent.SubstitutePreviewChanged(_exProcessor.GetSubstitutePreview(_cmdLine, _cursor)));
        else
            events.Add(VimEvent.SubstitutePreviewChanged(new Dictionary<int, string>()));
    }

    // Expose motion helpers for MotionEngine
    private MotionEngine GetMotion() => new(_bufferManager.Current.Text);

}
