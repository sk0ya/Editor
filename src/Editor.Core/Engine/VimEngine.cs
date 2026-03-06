using Editor.Core.Buffer;
using Editor.Core.Config;
using Editor.Core.Macros;
using Editor.Core.Marks;
using Editor.Core.Models;
using Editor.Core.Registers;
using Editor.Core.Spell;
using Editor.Core.Syntax;
using Editor.Core;

namespace Editor.Core.Engine;

public class VimEngine
{
    private readonly BufferManager _bufferManager;
    private readonly RegisterManager _registerManager;
    private readonly MarkManager _markManager;
    private readonly MacroManager _macroManager;
    private readonly ExCommandProcessor _exProcessor;
    private readonly SyntaxEngine _syntaxEngine;
    private readonly CommandParser _commandParser;
    private readonly VimConfig _config;
    private readonly SpellChecker _spellChecker = new();

    private VimMode _mode = VimMode.Normal;
    private CursorPosition _cursor = CursorPosition.Zero;
    private bool _suppressSnapshot;
    private Selection? _selection;
    private CursorPosition _visualStart;

    private string _cmdLine = "";   // For : / ? modes
    private string _statusMsg = "";
    private string[] _completions = [];     // Tab completion candidates
    private int _completionIndex = -1;      // Currently selected completion (-1 = none)
    private string _searchPattern = "";
    private bool _searchForward = true;
    private CursorPosition _preSearchCursor;

    private char? _pendingReplaceChar;
    private bool _awaitingMark;
    private bool _awaitingMarkJump;
    private bool _awaitingMarkJumpLine;
    private bool _ctrlWPending;
    private char _awaitingVisualTextObj;  // 'i' or 'a' when pending text object in Visual mode
    private bool _awaitingSurroundChar;   // ys{motion} — waiting for the surround character
    private bool _awaitingInsertRegister; // Ctrl+R in Insert mode — waiting for register name
    private bool _awaitingExprRegister;   // Ctrl+R = in Insert mode — accumulating expression
    private string _exprBuffer = "";      // accumulated expression input
    private char? _digraphPendingChar;     // non-null = awaiting digraph input; holds first char once entered (null char = awaiting first)
    private string[] _kwCompletions = []; // Ctrl+N/P keyword completion candidates
    private int _kwCompletionIndex = -1;  // current completion index (-1 = prefix only)
    private string _kwCompletionPrefix = "";
    private int _kwCompletionApplied = 0; // length of completion text currently in buffer
    private CursorPosition _surroundStart, _surroundEnd;
    private bool _surroundLinewise;
    private int _preferredColumn = 0; // Sticky column for j/k

    private CursorPosition _insertStart;
    private CursorPosition _lastInsertPos;
    private CursorPosition _lastVisualStart;
    private CursorPosition _lastVisualEnd;
    private VimMode _lastVisualMode = VimMode.Visual;
    private RepeatChange? _lastRepeatChange;
    private ParsedCommand? _pendingInsertRepeatCommand;
    private List<VimKeyStroke>? _pendingInsertRepeatKeys;
    private bool _isDotReplaying;
    private BlockInsertState? _blockInsertState;
    private readonly List<VimKeyStroke> _pendingMappedInput = [];

    private sealed record RepeatChange(ParsedCommand Command, IReadOnlyList<VimKeyStroke> InsertKeys);
    private sealed record BlockInsertState(int StartLine, int EndLine, int Column);

    public VimMode Mode => _mode;
    public CursorPosition Cursor => _cursor;
    public Selection? Selection => _selection;
    public string CommandLine => _cmdLine;
    public string SearchPattern => _searchPattern;
    public string StatusMessage => _statusMsg;
    public VimOptions Options => _config.Options;
    public VimConfig Config => _config;
    public SpellChecker SpellChecker => _spellChecker;
    public VimBuffer CurrentBuffer => _bufferManager.Current;
    public BufferManager BufferManager => _bufferManager;
    public SyntaxEngine Syntax => _syntaxEngine;

    public VimEngine(VimConfig? config = null)
    {
        _config = config ?? new VimConfig();
        _bufferManager = new BufferManager();
        _registerManager = new RegisterManager(_config.Options);
        _markManager = new MarkManager();
        _macroManager = new MacroManager();
        _syntaxEngine = new SyntaxEngine();
        _commandParser = new CommandParser();
        _exProcessor = new ExCommandProcessor(_bufferManager, _config.Options, _markManager);
    }

    public void SetClipboardProvider(IClipboardProvider provider)
    {
        _registerManager.SetClipboardProvider(provider);
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
        _bufferManager.OpenFile(path);
        _cursor = CursorPosition.Zero;
        _syntaxEngine.DetectLanguage(path);
        _syntaxEngine.Invalidate();
        _bufferManager.Current.Undo.Clear();
    }

    public void SetText(string text)
    {
        _bufferManager.Current.Text.SetText(text);
        _cursor = CursorPosition.Zero;
        _syntaxEngine.Invalidate();
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
        _preferredColumn = col;

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

    // Process a key stroke and return events to update the UI
    public IReadOnlyList<VimEvent> ProcessKey(string key, bool ctrl = false, bool shift = false, bool alt = false)
    {
        var events = new List<VimEvent>();
        ProcessStroke(new VimKeyStroke(key, ctrl, shift, alt), events, allowMapping: true);
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

    private void ProcessStroke(VimKeyStroke stroke, List<VimEvent> events, bool allowMapping)
    {
        if (allowMapping && TryApplyMapping(stroke, events))
            return;

        var modeBefore = _mode;

        // Macro recording
        if (_macroManager.IsRecording && !(stroke.Key == "q" && !stroke.Ctrl && !stroke.Shift))
            _macroManager.RecordKey(stroke);

        ProcessKeyInternal(stroke.Key, stroke.Ctrl, stroke.Shift, stroke.Alt, events);
        SyncSpellChecker();
        TrackPendingInsertRepeat(stroke, modeBefore);
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

            var match = ResolveMapMatch(maps, _pendingMappedInput);
            if (match.HasExactMatch)
            {
                if (match.HasLongerPrefix)
                    return true;

                _pendingMappedInput.Clear();
                foreach (var mappedStroke in ParseMappingSequence(match.MappedValue ?? ""))
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

    private static MapMatch ResolveMapMatch(Dictionary<string, string> maps, IReadOnlyList<VimKeyStroke> input)
    {
        bool hasPrefix = false;
        bool hasLongerPrefix = false;
        string? mappedValue = null;
        int exactLength = -1;

        foreach (var kv in maps)
        {
            var lhs = ParseMappingSequence(kv.Key);
            if (lhs.Count == 0 || !StartsWith(lhs, input))
                continue;

            if (lhs.Count == input.Count)
            {
                if (lhs.Count > exactLength)
                {
                    exactLength = lhs.Count;
                    mappedValue = kv.Value;
                }
            }
            else
            {
                hasPrefix = true;
                if (exactLength >= 0)
                    hasLongerPrefix = true;
            }
        }

        return new MapMatch(
            HasExactMatch: exactLength >= 0,
            HasPrefix: hasPrefix,
            HasLongerPrefix: hasLongerPrefix,
            MappedValue: mappedValue);
    }

    private static bool StartsWith(IReadOnlyList<VimKeyStroke> candidate, IReadOnlyList<VimKeyStroke> input)
    {
        if (input.Count > candidate.Count)
            return false;

        for (int i = 0; i < input.Count; i++)
        {
            if (!AreSameStroke(candidate[i], input[i]))
                return false;
        }

        return true;
    }

    private static bool AreSameStroke(VimKeyStroke left, VimKeyStroke right) =>
        left.Ctrl == right.Ctrl &&
        left.Shift == right.Shift &&
        left.Alt == right.Alt &&
        string.Equals(left.Key, right.Key, StringComparison.Ordinal);

    private static IReadOnlyList<VimKeyStroke> ParseMappingSequence(string sequence)
    {
        var strokes = new List<VimKeyStroke>();
        for (int i = 0; i < sequence.Length; i++)
        {
            if (sequence[i] == '<')
            {
                int end = sequence.IndexOf('>', i + 1);
                if (end > i)
                {
                    var token = sequence[i..(end + 1)];
                    if (TryParseMapToken(token, out var parsed))
                    {
                        strokes.Add(parsed);
                        i = end;
                        continue;
                    }
                }
            }

            strokes.Add(new VimKeyStroke(sequence[i].ToString(), false, false, false));
        }

        return strokes;
    }

    private static bool TryParseMapToken(string token, out VimKeyStroke stroke)
    {
        stroke = default;
        if (token.Length < 3 || token[0] != '<' || token[^1] != '>')
            return false;

        var inner = token[1..^1];
        if (string.IsNullOrWhiteSpace(inner))
            return false;

        var parts = inner.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool ctrl = false, shift = false, alt = false;
        string keyPart;

        if (parts.Length == 1)
        {
            keyPart = parts[0];
        }
        else
        {
            for (int i = 0; i < parts.Length - 1; i++)
            {
                switch (parts[i].ToLowerInvariant())
                {
                    case "c":
                    case "ctrl":
                    case "control":
                        ctrl = true;
                        break;
                    case "s":
                    case "shift":
                        shift = true;
                        break;
                    case "a":
                    case "alt":
                    case "m":
                    case "meta":
                        alt = true;
                        break;
                    default:
                        return false;
                }
            }

            keyPart = parts[^1];
        }

        var key = NormalizeMapKeyName(keyPart);
        if (key == null)
            return false;

        stroke = new VimKeyStroke(key, ctrl, shift, alt);
        return true;
    }

    private static string? NormalizeMapKeyName(string keyName)
    {
        if (string.IsNullOrWhiteSpace(keyName))
            return null;

        var lowered = keyName.ToLowerInvariant();
        return lowered switch
        {
            "esc" or "escape" => "Escape",
            "cr" or "enter" or "return" => "Return",
            "tab" => "Tab",
            "bs" or "backspace" or "back" => "Back",
            "del" or "delete" => "Delete",
            "space" => " ",
            "left" => "Left",
            "right" => "Right",
            "up" => "Up",
            "down" => "Down",
            "home" => "Home",
            "end" => "End",
            "lt" => "<",
            "bar" => "|",
            _ when keyName.Length == 1 => keyName,
            _ => null
        };
    }

    private readonly record struct MapMatch(
        bool HasExactMatch,
        bool HasPrefix,
        bool HasLongerPrefix,
        string? MappedValue);

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
        var motion = new MotionEngine(buf);

        // Ctrl+W two-key window prefix
        if (_ctrlWPending)
        {
            _ctrlWPending = false;
            if (key == "Escape") { EmitStatus(events, ""); return; }
            HandleCtrlWSecondKey(key, ctrl, events);
            return;
        }

        // Awaiting pending chars
        if (_awaitingMark) { _markManager.SetMark(key[0], _cursor); _awaitingMark = false; EmitCursor(events); return; }
        if (_awaitingMarkJump) { var m = _markManager.GetMark(key[0]); if (m.HasValue) MoveCursor(m.Value, events); _awaitingMarkJump = false; return; }
        if (_awaitingMarkJumpLine) { var m = _markManager.GetMark(key[0]); if (m.HasValue) MoveCursor(m.Value with { Column = 0 }, events); _awaitingMarkJumpLine = false; return; }
        if (_pendingReplaceChar.HasValue) { ExecuteReplace(key[0], events); _pendingReplaceChar = null; return; }
        if (_awaitingSurroundChar)
        {
            _awaitingSurroundChar = false;
            if (key.Length == 1) ApplySurround(_surroundStart, _surroundEnd, _surroundLinewise, key[0], events);
            return;
        }

        // Ctrl keys
        if (ctrl)
        {
            HandleNormalCtrl(key, events);
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
            EmitStatus(events, "");
            return;
        }

        ExecuteNormalCommand(cmd.Value, events);
    }

    private void HandleNormalCtrl(string key, List<VimEvent> events)
    {
        var buf = _bufferManager.Current.Text;
        var motion = new MotionEngine(buf);

        switch (key.ToLower())
        {
            case "d": ScrollHalfPage(true, events); break;
            case "u": ScrollHalfPage(false, events); break;
            case "f": ScrollPage(true, events); break;
            case "b": ScrollPage(false, events); break;
            case "r": ExecuteRedo(events); break;
            case "o": var jb = _markManager.JumpBack(); if (jb.HasValue) MoveCursor(jb.Value, events); break;
            case "i": var jf = _markManager.JumpForward(); if (jf.HasValue) MoveCursor(jf.Value, events); break;
            case "v": EnterVisualMode(VimMode.VisualBlock, events); break;
            case "w": _ctrlWPending = true; EmitStatus(events, "^W"); break;
            case "h": MoveCursor(motion.MoveLeft(_cursor, 1), events); _preferredColumn = _cursor.Column; break;
            case "j": MoveVertical(1, events); break;
            case "m": MoveLineAndFirstNonBlank(1, events); break;
            case "a":
            {
                int cnt = int.TryParse(_commandParser.Buffer, out var n) ? n : 1;
                _commandParser.Reset();
                ExecuteIncrementNumber(cnt, true, events);
                break;
            }
            case "x":
            {
                int cnt = int.TryParse(_commandParser.Buffer, out var n) ? n : 1;
                _commandParser.Reset();
                ExecuteIncrementNumber(cnt, false, events);
                break;
            }
            case "[":
                _commandParser.Reset();
                EmitStatus(events, "");
                break;
            case "6":
            case "^":
                SwitchToAlternateBuffer(events);
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

    private void ExecuteNormalCommand(ParsedCommand cmd, List<VimEvent> events)
    {
        var buf = _bufferManager.Current.Text;
        var motion = new MotionEngine(buf);
        int count = cmd.Count;

        // Double-operator: dd, cc, yy, >>, << (linewise on current line(s))
        if (cmd.LinewiseForced && cmd.Operator != null)
        {
            var endLine = Math.Min(buf.LineCount - 1, _cursor.Line + count - 1);
            switch (cmd.Operator)
            {
                case "d":
                    SetRepeatChange(cmd);
                    Snapshot();
                    DeleteLines(_cursor.Line, endLine, events);
                    return;
                case "c":
                    BeginInsertRepeat(cmd);
                    Snapshot();
                    DeleteLines(_cursor.Line, endLine, events);
                    EnterInsertMode(false, events);
                    return;
                case "y": YankLines(_cursor.Line, endLine, cmd.Register ?? '"', events); return;
                case ">":
                    SetRepeatChange(cmd);
                    IndentRange(_cursor.Line, endLine, true, events);
                    return;
                case "<":
                    SetRepeatChange(cmd);
                    IndentRange(_cursor.Line, endLine, false, events);
                    return;
                case "=":
                    SetRepeatChange(cmd);
                    AutoIndentRange(_cursor.Line, endLine, events);
                    return;
                case "gc":
                    SetRepeatChange(cmd);
                    ToggleCommentLines(_cursor.Line, endLine, events);
                    return;
                case "gq":
                    SetRepeatChange(cmd);
                    Snapshot();
                    FormatText(_cursor.Line, endLine, events);
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
                    SetRepeatChange(cmd);
                    Snapshot();
                    ApplyCaseConversion(new CursorPosition(_cursor.Line, 0), new CursorPosition(endLine, 0), true, CaseConversion.Lower, events);
                    return;
                case "gU":
                    SetRepeatChange(cmd);
                    Snapshot();
                    ApplyCaseConversion(new CursorPosition(_cursor.Line, 0), new CursorPosition(endLine, 0), true, CaseConversion.Upper, events);
                    return;
                case "g~":
                    SetRepeatChange(cmd);
                    Snapshot();
                    ApplyCaseConversion(new CursorPosition(_cursor.Line, 0), new CursorPosition(endLine, 0), true, CaseConversion.Toggle, events);
                    return;
            }
        }

        // Surround: cs{from}{to} and ds{char}
        if (cmd.Operator == null && cmd.Motion?.StartsWith("cs") == true && cmd.Motion.Length >= 4)
        {
            Snapshot();
            ExecuteChangeSurround(cmd.Motion[2], cmd.Motion[3], events);
            return;
        }
        if (cmd.Operator == null && cmd.Motion?.StartsWith("ds") == true && cmd.Motion.Length >= 3)
        {
            Snapshot();
            ExecuteDeleteSurround(cmd.Motion[2], events);
            return;
        }

        switch (cmd.Motion)
        {
            // Mode transitions
            case "i":
                BeginInsertRepeat(cmd);
                EnterInsertMode(false, events);
                break;
            case "I":
                BeginInsertRepeat(cmd);
                _cursor = motion.FindChar(_cursor, ' ', false, false);
                GoToLineStart(events);
                EnterInsertMode(false, events);
                break;
            case "a":
                BeginInsertRepeat(cmd);
                _cursor = motion.MoveRight(_cursor, 1, true);
                EnterInsertMode(false, events);
                break;
            case "A":
                BeginInsertRepeat(cmd);
                GoToLineEnd(true, events);
                EnterInsertMode(false, events);
                break;
            case "o":
                BeginInsertRepeat(cmd);
                OpenLineBelow(events);
                EnterInsertMode(false, events);
                break;
            case "O":
                BeginInsertRepeat(cmd);
                OpenLineAbove(events);
                EnterInsertMode(false, events);
                break;
            case "R":
                BeginInsertRepeat(cmd);
                EnterReplaceMode(events);
                break;
            case "v": EnterVisualMode(VimMode.Visual, events); break;
            case "V": EnterVisualMode(VimMode.VisualLine, events); break;
            case ":": EnterCommandMode(events); break;
            case "/": EnterSearchMode(true, events); break;
            case "?": EnterSearchMode(false, events); break;

            // Movement
            case "h": MoveCursor(motion.MoveLeft(_cursor, count), events); _preferredColumn = _cursor.Column; break;
            case "l": MoveCursor(motion.MoveRight(_cursor, count), events); _preferredColumn = _cursor.Column; break;
            case "j": MoveVertical(count, events); break;
            case "k": MoveVertical(-count, events); break;
            case "0": MoveCursor(_cursor with { Column = 0 }, events); _preferredColumn = 0; break;
            case "^": var fnb = GetFirstNonBlank(); MoveCursor(_cursor with { Column = fnb }, events); break;
            case "$": GoToLineEnd(false, events); break;
            case "w": MoveCursor(motion.WordForward(_cursor, count, false), events); break;
            case "W": MoveCursor(motion.WordForward(_cursor, count, true), events); break;
            case "b": MoveCursor(motion.WordBackward(_cursor, count, false), events); break;
            case "B": MoveCursor(motion.WordBackward(_cursor, count, true), events); break;
            case "e": MoveCursor(motion.WordEnd(_cursor, count, false), events); break;
            case "E": MoveCursor(motion.WordEnd(_cursor, count, true), events); break;
            case "ge": MoveCursor(WordEndBackward(count), events); break;
            case "gj": MoveVertical(count, events); break;
            case "gk": MoveVertical(-count, events); break;
            case "g_":
                var g_m = motion.Calculate("g_", _cursor, count);
                if (g_m.HasValue) MoveCursor(g_m.Value.Target, events);
                break;
            case "gg": MoveCursor(new CursorPosition(0, 0), events); break;
            case "G":
                var lastLine = count == 1 ? buf.LineCount - 1 : count - 1;
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
                SetRepeatChange(cmd);
                JoinLinesNoSpace(count, events);
                break;
            case "gd":
                events.Add(VimEvent.GoToDefinitionRequested());
                break;
            case "gr":
                events.Add(VimEvent.FindReferencesRequested());
                break;
            case "ga":
                events.Add(VimEvent.CodeActionRequested());
                break;
            case "gf":
            {
                var line = buf.GetLine(_cursor.Line);
                var path = ExtractFilePathUnderCursor(line, _cursor.Column);
                if (!string.IsNullOrEmpty(path))
                {
                    if (!System.IO.Path.IsPathRooted(path))
                    {
                        var dir = _bufferManager.Current.FilePath is { } fp
                            ? System.IO.Path.GetDirectoryName(fp)
                            : null;
                        if (dir != null)
                            path = System.IO.Path.GetFullPath(System.IO.Path.Combine(dir, path));
                    }
                    events.Add(VimEvent.OpenFileRequested(path));
                }
                break;
            }
            case "gx":
            {
                var line = buf.GetLine(_cursor.Line);
                var token = ExtractFilePathUnderCursor(line, _cursor.Column);
                if (!string.IsNullOrEmpty(token))
                {
                    if (token.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                        token.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                        token.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase))
                    {
                        events.Add(VimEvent.OpenUrlRequested(token));
                    }
                    else
                    {
                        if (!System.IO.Path.IsPathRooted(token))
                        {
                            var dir = _bufferManager.Current.FilePath is { } fp
                                ? System.IO.Path.GetDirectoryName(fp)
                                : null;
                            if (dir != null)
                                token = System.IO.Path.GetFullPath(System.IO.Path.Combine(dir, token));
                        }
                        events.Add(VimEvent.OpenFileRequested(token));
                    }
                }
                break;
            }
            case "]s": NavigateSpellError(true, count, events); break;
            case "[s": NavigateSpellError(false, count, events); break;
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
            case "H": MoveCursor(ScreenPosition(0), events); break;
            case "M": MoveCursor(ScreenPosition(10), events); break;
            case "L": MoveCursor(ScreenPosition(20), events); break;
            case ";": RepeatFind(false, events); break;
            case ",": RepeatFind(true, events); break;
            case "n": SearchNext(true, events); break;
            case "N": SearchNext(false, events); break;
            case "*": SearchWordUnderCursor(true, events); break;
            case "#": SearchWordUnderCursor(false, events); break;
            case "zz": events.Add(VimEvent.ViewportAlignRequested(ViewportAlign.Center)); break;
            case "zt": events.Add(VimEvent.ViewportAlignRequested(ViewportAlign.Top)); break;
            case "zb": events.Add(VimEvent.ViewportAlignRequested(ViewportAlign.Bottom)); break;
            case "za":
                CurrentBuffer.Folds.ToggleFold(_cursor.Line);
                _cursor = ClampCursorToVisible(_cursor);
                EmitCursor(events);
                events.Add(VimEvent.FoldsChanged());
                break;
            case "zo":
                CurrentBuffer.Folds.OpenFold(_cursor.Line);
                events.Add(VimEvent.FoldsChanged());
                break;
            case "zc":
                CurrentBuffer.Folds.CloseFold(_cursor.Line);
                _cursor = ClampCursorToVisible(_cursor);
                EmitCursor(events);
                events.Add(VimEvent.FoldsChanged());
                break;
            case "zM":
                CurrentBuffer.Folds.CloseAll();
                _cursor = ClampCursorToVisible(_cursor);
                EmitCursor(events);
                events.Add(VimEvent.FoldsChanged());
                break;
            case "zR":
                CurrentBuffer.Folds.OpenAll();
                events.Add(VimEvent.FoldsChanged());
                break;
            case "zf":
            {
                int foldEnd = Math.Min(CurrentBuffer.Text.LineCount - 1, _cursor.Line + count - 1);
                if (foldEnd > _cursor.Line)
                {
                    CurrentBuffer.Folds.CreateFold(_cursor.Line, foldEnd);
                    events.Add(VimEvent.FoldsChanged());
                }
                break;
            }
            case "z=":
                ShowSpellSuggestions(events);
                break;

            // Editing
            case "x":
                SetRepeatChange(cmd);
                // Delete [count] chars from cursor position
                var xEnd = _cursor with { Column = Math.Min(_cursor.Column + count - 1, Math.Max(0, buf.GetLineLength(_cursor.Line) - 1)) };
                ExecuteDelete(_cursor, xEnd, false, events);
                break;
            case "X":
                SetRepeatChange(cmd);
                // Delete [count] chars before cursor
                var xStart = motion.MoveLeft(_cursor, count);
                if (xStart.Column < _cursor.Column) ExecuteDelete(xStart, _cursor with { Column = _cursor.Column - 1 }, false, events);
                break;
            case "s":
                BeginInsertRepeat(cmd);
                var sStart = _cursor;
                ExecuteDelete(_cursor, motion.MoveRight(_cursor, count), false, events);
                _cursor = _bufferManager.Current.Text.ClampCursor(sStart, true);
                EnterInsertMode(false, events);
                break;
            case "S":
                BeginInsertRepeat(cmd);
                DeleteLines(_cursor.Line, _cursor.Line, events);
                EnterInsertMode(false, events);
                break;
            case "D":
                SetRepeatChange(cmd);
                var eol = GetLineLength() - 1;
                if (eol >= _cursor.Column) ExecuteDelete(_cursor, _cursor with { Column = eol }, false, events);
                break;
            case "C":
                BeginInsertRepeat(cmd);
                var cStart = _cursor;
                var ceol = GetLineLength() - 1;
                if (ceol >= _cursor.Column) ExecuteDelete(_cursor, _cursor with { Column = ceol }, false, events);
                _cursor = _bufferManager.Current.Text.ClampCursor(cStart, true);
                EnterInsertMode(false, events);
                break;
            case "Y": YankLines(_cursor.Line, _cursor.Line + count - 1, cmd.Register ?? '"', events); break;
            case "p":
                SetRepeatChange(cmd);
                PasteAfter(cmd.Register ?? '"', events);
                break;
            case "P":
                SetRepeatChange(cmd);
                PasteBefore(cmd.Register ?? '"', events);
                break;
            case "u": ExecuteUndo(events); break;
            case "U": ExecuteUndo(events); break;
            case "\x12": ExecuteRedo(events); break;
            case "\x16": EnterVisualMode(VimMode.VisualBlock, events); break;
            case ".": RepeatLastChange(count, events); break;
            case "J":
                SetRepeatChange(cmd);
                JoinLines(count, events);
                break;
            case "~":
                SetRepeatChange(cmd);
                ToggleCase(count, events);
                break;
            case ">>":
                SetRepeatChange(cmd);
                IndentLine(true, count, events);
                break;
            case "<<":
                SetRepeatChange(cmd);
                IndentLine(false, count, events);
                break;

            // r: replace char (needs next input)
            case var r when r?.StartsWith('r') == true && r.Length == 2:
                SetRepeatChange(cmd);
                ExecuteReplace(r[1], events);
                break;
            case "r": _pendingReplaceChar = null; /* wait */ break;

            // Marks
            case var m when m?.StartsWith('m') == true && m.Length == 2:
                _markManager.SetMark(m[1], _cursor);
                break;
            case "m": _awaitingMark = true; break;
            case var tick when tick?.StartsWith('`') == true && tick.Length == 2:
                var mk = _markManager.GetMark(tick[1]); if (mk.HasValue) MoveCursor(mk.Value, events); break;
            case "`": _awaitingMarkJump = true; break;
            case var apos when apos?.StartsWith('\'') == true && apos.Length == 2:
                var mk2 = _markManager.GetMark(apos[1]); if (mk2.HasValue) MoveCursor(mk2.Value with { Column = 0 }, events); break;
            case "'": _awaitingMarkJumpLine = true; break;

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
        var motion = new MotionEngine(buf);

        // Text objects
        if (cmd.Motion?.Length == 2 && cmd.Motion[0] is 'i' or 'a')
        {
            if (cmd.Operator == null) return;
            var range = GetTextObjectRange(cmd.Motion);
            if (range == null) return;

            if (cmd.Operator == "c") BeginInsertRepeat(cmd);
            else if (cmd.Operator is "d" or "<" or ">" or "=") SetRepeatChange(cmd);

            ExecuteOperator(cmd.Operator, range.Value.Start, range.Value.End, cmd.Register ?? '"', false, events);
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

            if (cmd.Operator == "c") BeginInsertRepeat(cmd);
            else if (cmd.Operator is "d" or "<" or ">" or "=") SetRepeatChange(cmd);
            ExecuteOperator(cmd.Operator, _cursor, found, cmd.Register ?? '"', false, events);
            return;
        }

        // Calculate motion target
        var mot = motion.Calculate(cmd.Motion, _cursor, cmd.Count);
        if (mot == null) return;

        bool linewise = mot.Value.Type == MotionType.Linewise || cmd.LinewiseForced;

        if (cmd.Operator == null)
        {
            MoveCursor(mot.Value.Target, events);
            return;
        }

        if (cmd.Operator == "c") BeginInsertRepeat(cmd);
        else if (cmd.Operator is "d" or "<" or ">" or "=" or "gu" or "gU" or "g~") SetRepeatChange(cmd);

        ExecuteOperator(cmd.Operator, _cursor, mot.Value.Target, cmd.Register ?? '"', linewise, events);
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
                if (linewise) DeleteLines(start.Line, end.Line, events);
                else { YankRange(register, start, end, false); ExecuteDelete(start, end, false, events); }
                break;
            case "c":
                if (linewise) { DeleteLines(start.Line, end.Line, events); EnterInsertMode(false, events); }
                else
                {
                    YankRange(register, start, end, false);
                    ExecuteDelete(start, end, false, events);
                    _cursor = _bufferManager.Current.Text.ClampCursor(start, true);
                    EnterInsertMode(false, events);
                }
                break;
            case "y":
                if (linewise) YankLines(start.Line, end.Line, register, events);
                else YankRange(register, start, end, false);
                MoveCursor(start, events);
                EmitStatus(events, linewise ? $"{end.Line - start.Line + 1} lines yanked" : "yanked");
                break;
            case ">": IndentRange(start.Line, end.Line, true, events); break;
            case "<": IndentRange(start.Line, end.Line, false, events); break;
            case "=": AutoIndentRange(start.Line, end.Line, events); break;
            case "gc": ToggleCommentLines(start.Line, end.Line, events); break;
            case "gq": FormatText(start.Line, end.Line, events); break;
            case "ys":
                _surroundStart = start;
                _surroundEnd = end;
                _surroundLinewise = linewise;
                _awaitingSurroundChar = true;
                return;
            case "gu": ApplyCaseConversion(start, end, linewise, CaseConversion.Lower, events); break;
            case "gU": ApplyCaseConversion(start, end, linewise, CaseConversion.Upper, events); break;
            case "g~": ApplyCaseConversion(start, end, linewise, CaseConversion.Toggle, events); break;
        }
    }

    // ─────────────── INSERT MODE ───────────────
    private void HandleInsert(string key, bool ctrl, bool shift, bool alt, List<VimEvent> events)
    {
        _ctrlWPending = false;
        var buf = _bufferManager.Current.Text;

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
            CycleKeywordCompletion(key.ToLower() == "n" ? +1 : -1, events);
            return;
        }

        if (_kwCompletions.Length > 0)
        {
            _kwCompletions = [];
            _kwCompletionIndex = -1;
            _kwCompletionApplied = 0;
            _kwCompletionPrefix = "";
        }

        if (ctrl)
        {
            switch (key.ToLower())
            {
                case "[": // Ctrl+[
                    ExitInsertMode(events);
                    return;
                case "w": DeleteWordBack(events); return;
                case "u": DeleteLineBack(events); return;
                case "h": DeleteCharBack(events); return;
                case "j": InsertNewline(events); return;
                case "m": InsertNewline(events); return;
                case "a": // Ctrl+A = Select All
                    SelectAllVisualLine(events);
                    return;
                case "c": // Ctrl+C = Copy current line to clipboard
                    YankLines(_cursor.Line, _cursor.Line, '+', events);
                    return;
                case "v": // Ctrl+V = Paste from clipboard
                    PasteAtCursorInsertMode(events);
                    return;
                case "x": // Ctrl+X = Cut current line to clipboard
                {
                    Snapshot();
                    var bufX = _bufferManager.Current.Text;
                    var lineX = bufX.GetLine(_cursor.Line);
                    _registerManager.Set('+', new Register(lineX, RegisterType.Line));
                    if (bufX.LineCount > 1)
                    {
                        CurrentBuffer.Folds.OnLinesDeleted(_cursor.Line, 1);
                        bufX.DeleteLines(_cursor.Line, _cursor.Line);
                        var newLine = Math.Min(_cursor.Line, bufX.LineCount - 1);
                        _cursor = new CursorPosition(newLine, 0);
                    }
                    else
                    {
                        bufX.ReplaceLine(0, "");
                        _cursor = new CursorPosition(0, 0);
                    }
                    EmitText(events);
                    EmitStatus(events, "1 line cut");
                    return;
                }
                case "r": // Ctrl+R {reg} = insert register contents
                    _awaitingInsertRegister = true;
                    EmitStatus(events, "\"");
                    return;
                case "k": // Ctrl+K {a}{b} = insert digraph
                    _digraphPendingChar = '\0';
                    events.Add(VimEvent.CommandLineChanged("^K"));
                    return;
            }
            // Unhandled Ctrl combo — do not insert as text.
            return;
        }

        if (!ctrl && _mode == VimMode.Insert && HandleBlockInsertKey(key, events))
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
                DeleteCharBack(events);
                break;
            case "Delete":
                buf.DeleteChar(_cursor.Line, _cursor.Column);
                EmitText(events);
                break;
            case "Return":
                InsertNewline(events);
                break;
            case "Left":
                var ml = new MotionEngine(buf);
                _cursor = ml.MoveLeft(_cursor);
                EmitCursor(events);
                break;
            case "Right":
                var mr = new MotionEngine(buf);
                _cursor = mr.MoveRight(_cursor, 1, true);
                EmitCursor(events);
                break;
            case "Up":
                _cursor = new MotionEngine(buf).MoveUp(_cursor);
                EmitCursor(events);
                break;
            case "Down":
                _cursor = new MotionEngine(buf).MoveDown(_cursor);
                EmitCursor(events);
                break;
            case "Tab":
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
                break;
            default:
                if (key.Length == 1)
                {
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

                    if (_mode == VimMode.Replace)
                    {
                        if (_cursor.Column < buf.GetLineLength(_cursor.Line))
                            buf.DeleteChar(_cursor.Line, _cursor.Column);
                    }
                    buf.InsertChar(_cursor.Line, _cursor.Column, key[0]);
                    _cursor = _cursor with { Column = _cursor.Column + 1 };
                    EmitText(events);
                }
                break;
        }
    }

    // ─────────────── VISUAL MODE ───────────────
    private void HandleVisual(string key, bool ctrl, bool shift, bool alt, List<VimEvent> events)
    {
        _ctrlWPending = false;
        if (key == "Escape")
        {
            _commandParser.Reset();
            ExitVisualMode(events);
            return;
        }

        // Second char of visual text object (e.g. viw, va", vi{)
        if (_awaitingVisualTextObj != '\0' && !ctrl)
        {
            char prefix = _awaitingVisualTextObj;
            _awaitingVisualTextObj = '\0';
            string textObj = prefix.ToString() + key;
            var range = GetTextObjectRange(textObj);
            if (range != null)
            {
                _visualStart = range.Value.Start;
                _cursor = range.Value.End;
                UpdateSelection(events);
            }
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
                ExecuteVisualYank('+', events);
                return;
            }
            if (key == "x") // Ctrl+X = Cut selection to clipboard
            {
                _commandParser.Reset();
                ExecuteVisualDelete('+', events);
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
                if (key == "i") { _awaitingVisualTextObj = 'i'; return; }
                break;
            case "a":
                _awaitingVisualTextObj = 'a';
                return;
            // Operators on selection
            case "d":
            case "x":
            case "X":
            case "D":
                _commandParser.Reset();
                ExecuteVisualDelete('"', events);
                return;
            case "y":
                _commandParser.Reset();
                ExecuteVisualYank('"', events);
                return;
            case "c":
            case "C":
            case "s":
            case "S":
                _commandParser.Reset();
                if (_mode == VimMode.VisualBlock)
                    BeginVisualBlockChange('"', events);
                else
                {
                    ExecuteVisualDelete('"', events);
                    EnterInsertMode(false, events);
                }
                return;
            case ">":
                _commandParser.Reset();
                ExecuteVisualIndent(true, events);
                return;
            case "<":
                _commandParser.Reset();
                ExecuteVisualIndent(false, events);
                return;
            case "~":
                _commandParser.Reset();
                ExecuteVisualToggleCase(events);
                return;
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
            if (key == "c") ExecuteVisualComment(events);
            else ExecuteVisualCaseConvert(key switch { "u" => CaseConversion.Lower, "U" => CaseConversion.Upper, _ => CaseConversion.Toggle }, events);
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

        UpdateSelection(events);
        return true;
    }

    private bool ApplyVisualMotion(ParsedCommand cmd, List<VimEvent> events)
    {
        if (cmd.Operator != null) return false;

        var buf = _bufferManager.Current.Text;
        var motion = new MotionEngine(buf);
        int count = Math.Max(1, cmd.Count);

        switch (cmd.Motion)
        {
            case "Left":
            case "h":
                _cursor = motion.MoveLeft(_cursor, count);
                _preferredColumn = _cursor.Column;
                return true;
            case "Right":
            case "l":
                _cursor = motion.MoveRight(_cursor, count);
                _preferredColumn = _cursor.Column;
                return true;
            case "Up":
            case "k":
                for (int i = 0; i < count; i++)
                    _cursor = motion.MoveUp(_cursor);
                return true;
            case "Down":
            case "j":
                for (int i = 0; i < count; i++)
                    _cursor = motion.MoveDown(_cursor);
                return true;
            case "0":
                _cursor = _cursor with { Column = 0 };
                _preferredColumn = 0;
                return true;
            case "^":
                _cursor = _cursor with { Column = GetFirstNonBlank() };
                _preferredColumn = _cursor.Column;
                return true;
            case "$":
                _cursor = _cursor with { Column = Math.Max(0, GetLineLength() - 1) };
                _preferredColumn = _cursor.Column;
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
                _cursor = WordEndBackward(count);
                return true;
            case "gj":
                for (int i = 0; i < count; i++)
                    _cursor = motion.MoveDown(_cursor);
                return true;
            case "gk":
                for (int i = 0; i < count; i++)
                    _cursor = motion.MoveUp(_cursor);
                return true;
            case "g_":
                var g_vm = motion.Calculate("g_", _cursor, count);
                if (g_vm.HasValue) _cursor = g_vm.Value.Target;
                return true;
            case "gg":
                _cursor = new CursorPosition(0, 0);
                _preferredColumn = 0;
                return true;
            case "G":
                var targetLine = count == 1 ? buf.LineCount - 1 : count - 1;
                _cursor = new CursorPosition(Math.Clamp(targetLine, 0, buf.LineCount - 1), 0);
                _preferredColumn = 0;
                return true;
            case "+":
                var downLine = Math.Clamp(_cursor.Line + count, 0, buf.LineCount - 1);
                _cursor = new CursorPosition(downLine, GetFirstNonBlank(downLine));
                _preferredColumn = _cursor.Column;
                return true;
            case "-":
                var upLine = Math.Clamp(_cursor.Line - count, 0, buf.LineCount - 1);
                _cursor = new CursorPosition(upLine, GetFirstNonBlank(upLine));
                _preferredColumn = _cursor.Column;
                return true;
            case "_":
                var underLine = Math.Clamp(_cursor.Line + Math.Max(0, count - 1), 0, buf.LineCount - 1);
                _cursor = new CursorPosition(underLine, GetFirstNonBlank(underLine));
                _preferredColumn = _cursor.Column;
                return true;
            case "|":
                _cursor = _cursor with { Column = Math.Clamp(count - 1, 0, Math.Max(0, GetLineLength() - 1)) };
                _preferredColumn = _cursor.Column;
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
                _cursor = ScreenPosition(0);
                return true;
            case "M":
                _cursor = ScreenPosition(10);
                return true;
            case "L":
                _cursor = ScreenPosition(20);
                return true;
            case ";":
                for (int i = 0; i < count; i++)
                    RepeatFind(false, events);
                return true;
            case ",":
                for (int i = 0; i < count; i++)
                    RepeatFind(true, events);
                return true;
            case "n":
                for (int i = 0; i < count; i++)
                    SearchNext(true, events);
                return true;
            case "N":
                for (int i = 0; i < count; i++)
                    SearchNext(false, events);
                return true;
            case "*":
                SearchWordUnderCursor(true, events);
                return true;
            case "#":
                SearchWordUnderCursor(false, events);
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
                ExecuteVisualReplace(r[1], events);
                return true;
            case "za": CurrentBuffer.Folds.ToggleFold(_cursor.Line); events.Add(VimEvent.FoldsChanged()); return false;
            case "zo": CurrentBuffer.Folds.OpenFold(_cursor.Line); events.Add(VimEvent.FoldsChanged()); return false;
            case "zc": CurrentBuffer.Folds.CloseFold(_cursor.Line); events.Add(VimEvent.FoldsChanged()); return false;
            case "zM": CurrentBuffer.Folds.CloseAll(); events.Add(VimEvent.FoldsChanged()); return false;
            case "zR": CurrentBuffer.Folds.OpenAll(); events.Add(VimEvent.FoldsChanged()); return false;
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
                    MoveCursor(_preSearchCursor, events);
                    // Restore highlights for the previous confirmed pattern (or clear)
                    if (!string.IsNullOrEmpty(_searchPattern) && _config.Options.HlSearch)
                    {
                        var buf2 = _bufferManager.Current.Text;
                        var ic2 = _config.Options.SmartCase
                            ? !_searchPattern.Any(char.IsUpper)
                            : _config.Options.IgnoreCase;
                        var all2 = buf2.FindAll(_searchPattern, ic2);
                        events.Add(VimEvent.SearchChanged(_searchPattern, all2.Count));
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
                            MoveCursor(_preSearchCursor, events);
                            events.Add(VimEvent.SearchChanged("", 0));
                        }
                        ChangeMode(VimMode.Normal, events);
                    }
                    else if (isSearch && _config.Options.IncrSearch)
                    {
                        DoIncrSearch(events);
                    }
                }
                else ChangeMode(VimMode.Normal, events);
                EmitCmdLine(events);
                break;
            case "Up":
                var prev = _exProcessor.HistoryPrev();
                if (prev != null) { _cmdLine = prev; EmitCmdLine(events); }
                break;
            case "Down":
                var next = _exProcessor.HistoryNext();
                if (next != null) { _cmdLine = next; EmitCmdLine(events); }
                break;
            default:
                if (key.Length == 1 && !ctrl)
                {
                    _cmdLine += key;
                    if (isSearch && _config.Options.IncrSearch)
                        DoIncrSearch(events);
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
            _searchPattern = _cmdLine;
            _searchForward = _mode == VimMode.SearchForward;
            _cmdLine = "";
            _cursor = _preSearchCursor; // search from where we started, not incsearch preview pos
            ChangeMode(VimMode.Normal, events);
            DoSearch(_searchForward, events);
        }
        EmitCmdLine(events);
    }

    private bool TryExecuteConfigCommand(string cmdLine, out string? message, out string? error, out bool optionsChanged)
    {
        message = null;
        error = null;
        optionsChanged = false;

        var cmd = cmdLine.Trim();
        if (!IsConfigCommand(cmd))
            return false;

        error = _config.ParseCommand(cmd);
        if (error != null)
            return true;

        if (IsMapCommand(cmd))
            message = "Key mapping registered";
        else if (cmd.StartsWith("colorscheme ", StringComparison.OrdinalIgnoreCase))
            message = $"colorscheme: {_config.Options.ColorScheme}";
        else if (cmd.StartsWith("set ", StringComparison.OrdinalIgnoreCase) ||
                 cmd.StartsWith("syntax ", StringComparison.OrdinalIgnoreCase))
            optionsChanged = true;

        return true;
    }

    private static bool IsConfigCommand(string cmd)
    {
        if (cmd.Equals("set", StringComparison.OrdinalIgnoreCase))
            return false;

        return cmd.StartsWith("set ", StringComparison.OrdinalIgnoreCase) ||
            cmd.StartsWith("colorscheme ", StringComparison.OrdinalIgnoreCase) ||
            cmd.StartsWith("syntax ", StringComparison.OrdinalIgnoreCase) ||
            IsMapCommand(cmd);
    }

    private static bool IsMapCommand(string cmd) =>
        cmd.StartsWith("nmap ", StringComparison.OrdinalIgnoreCase) ||
        cmd.StartsWith("imap ", StringComparison.OrdinalIgnoreCase) ||
        cmd.StartsWith("vmap ", StringComparison.OrdinalIgnoreCase) ||
        cmd.StartsWith("nnoremap ", StringComparison.OrdinalIgnoreCase) ||
        cmd.StartsWith("inoremap ", StringComparison.OrdinalIgnoreCase) ||
        cmd.StartsWith("vnoremap ", StringComparison.OrdinalIgnoreCase);

    private void DoSearch(bool forward, List<VimEvent> events)
    {
        if (string.IsNullOrEmpty(_searchPattern)) return;
        var buf = _bufferManager.Current.Text;
        var ignoreCase = _config.Options.SmartCase
            ? !_searchPattern.Any(char.IsUpper)
            : _config.Options.IgnoreCase;

        var found = buf.FindNext(_searchPattern, _cursor, forward, ignoreCase, _config.Options.WrapScan);
        if (found.HasValue)
        {
            _markManager.AddJump(_cursor);
            MoveCursor(found.Value, events);
            var all = buf.FindAll(_searchPattern, ignoreCase);
            events.Add(VimEvent.SearchChanged(_searchPattern, all.Count));
        }
        else
        {
            var msg = _config.Options.WrapScan
                ? $"Pattern not found: {_searchPattern}"
                : $"Search hit {(forward ? "BOTTOM" : "TOP")}, continuing at {(forward ? "TOP" : "BOTTOM")} not done (no wrapscan)";
            EmitStatus(events, msg);
        }
    }

    private void DoIncrSearch(List<VimEvent> events)
    {
        var buf = _bufferManager.Current.Text;
        if (string.IsNullOrEmpty(_cmdLine))
        {
            MoveCursor(_preSearchCursor, events);
            events.Add(VimEvent.SearchChanged("", 0));
            return;
        }
        var ignoreCase = _config.Options.SmartCase
            ? !_cmdLine.Any(char.IsUpper)
            : _config.Options.IgnoreCase;
        var found = buf.FindNext(_cmdLine, _preSearchCursor, _mode == VimMode.SearchForward, ignoreCase);
        MoveCursor(found ?? _preSearchCursor, events);
        var all = buf.FindAll(_cmdLine, ignoreCase);
        events.Add(VimEvent.SearchChanged(_cmdLine, all.Count));
    }

    // ─────────────── HELPERS ───────────────

    private void EnterInsertMode(bool append, List<VimEvent> events)
    {
        Snapshot();
        _insertStart = _cursor;
        ChangeMode(VimMode.Insert, events);
    }

    private void EnterReplaceMode(List<VimEvent> events)
    {
        Snapshot();
        ChangeMode(VimMode.Replace, events);
    }

    private void ExitInsertMode(List<VimEvent> events)
    {
        // Move cursor left one (normal mode cursor)
        var buf = _bufferManager.Current.Text;
        if (_cursor.Column > 0 && _cursor.Column >= buf.GetLineLength(_cursor.Line))
            _cursor = _cursor with { Column = Math.Max(0, _cursor.Column - 1) };
        _lastInsertPos = _cursor;
        _blockInsertState = null;
        _awaitingInsertRegister = false;
        _awaitingExprRegister = false;
        _exprBuffer = "";
        _digraphPendingChar = null;
        _kwCompletions = [];
        _kwCompletionIndex = -1;
        _kwCompletionApplied = 0;
        _kwCompletionPrefix = "";
        ChangeMode(VimMode.Normal, events);
    }

    private void EnterVisualMode(VimMode visualMode, List<VimEvent> events)
    {
        _visualStart = _cursor;
        ChangeMode(visualMode, events);
        UpdateSelection(events);
    }

    private void ExitVisualMode(List<VimEvent> events)
    {
        _awaitingVisualTextObj = '\0';
        _lastVisualStart = _visualStart;
        _lastVisualEnd = _cursor;
        _lastVisualMode = _mode;
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
        _selection = null;
        events.Add(VimEvent.SelectionChanged(null));
        _cmdLine = $"{start},{end}";
        ChangeMode(VimMode.Command, events);
        EmitCmdLine(events);
    }

    private void EnterSearchMode(bool forward, List<VimEvent> events)
    {
        _cmdLine = "";
        _searchForward = forward;
        _preSearchCursor = _cursor;
        ChangeMode(forward ? VimMode.SearchForward : VimMode.SearchBackward, events);
        EmitCmdLine(events);
    }

    private void ChangeMode(VimMode newMode, List<VimEvent> events)
    {
        _pendingMappedInput.Clear();
        _mode = newMode;
        events.Add(VimEvent.ModeChanged(newMode));
    }

    private void MoveCursor(CursorPosition pos, List<VimEvent> events)
    {
        _cursor = _bufferManager.Current.Text.ClampCursor(pos, _mode == VimMode.Insert);
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

    private bool HandleBlockInsertKey(string key, List<VimEvent> events)
    {
        if (_blockInsertState == null) return false;
        var state = _blockInsertState;
        var buf = _bufferManager.Current.Text;

        switch (key)
        {
            case "Back":
                if (_cursor.Column <= state.Column)
                    return true;
                for (int line = state.StartLine; line <= state.EndLine; line++)
                {
                    var lineLen = buf.GetLineLength(line);
                    var deleteCol = Math.Min(_cursor.Column - 1, lineLen - 1);
                    if (deleteCol >= state.Column && deleteCol >= 0)
                        buf.DeleteChar(line, deleteCol);
                }
                _cursor = _cursor with { Column = _cursor.Column - 1 };
                EmitText(events);
                return true;
            case "Delete":
                for (int line = state.StartLine; line <= state.EndLine; line++)
                {
                    if (_cursor.Column < buf.GetLineLength(line))
                        buf.DeleteChar(line, _cursor.Column);
                }
                EmitText(events);
                return true;
            case "Tab":
                var insert = _config.Options.ExpandTab
                    ? new string(' ', _config.Options.TabStop)
                    : "\t";
                for (int line = state.StartLine; line <= state.EndLine; line++)
                {
                    var col = Math.Min(_cursor.Column, buf.GetLineLength(line));
                    buf.InsertText(line, col, insert);
                }
                _cursor = _cursor with { Column = _cursor.Column + insert.Length };
                EmitText(events);
                return true;
            default:
                if (key.Length == 1)
                {
                    for (int line = state.StartLine; line <= state.EndLine; line++)
                    {
                        var col = Math.Min(_cursor.Column, buf.GetLineLength(line));
                        buf.InsertChar(line, col, key[0]);
                    }
                    _cursor = _cursor with { Column = _cursor.Column + 1 };
                    EmitText(events);
                    return true;
                }
                return false;
        }
    }

    private static (int StartLine, int EndLine, int LeftColumn, int RightColumn) GetBlockBounds(Selection selection)
    {
        var startLine = Math.Min(selection.Start.Line, selection.End.Line);
        var endLine = Math.Max(selection.Start.Line, selection.End.Line);
        var leftColumn = Math.Min(selection.Start.Column, selection.End.Column);
        var rightColumn = Math.Max(selection.Start.Column, selection.End.Column);
        return (startLine, endLine, leftColumn, rightColumn);
    }

    private void BeginVisualBlockInsert(List<VimEvent> events)
    {
        if (_selection == null) { ExitVisualMode(events); return; }
        var (startLine, endLine, leftColumn, _) = GetBlockBounds(_selection.Value);
        _selection = null;
        events.Add(VimEvent.SelectionChanged(null));
        _blockInsertState = new BlockInsertState(startLine, endLine, leftColumn);
        _cursor = _bufferManager.Current.Text.ClampCursor(new CursorPosition(startLine, leftColumn), insertMode: true);
        EnterInsertMode(false, events);
        EmitCursor(events);
    }

    private void BeginVisualBlockChange(char register, List<VimEvent> events)
    {
        if (_selection == null) { ExitVisualMode(events); return; }
        var (startLine, endLine, leftColumn, rightColumn) = GetBlockBounds(_selection.Value);

        Snapshot();
        YankBlock(register, startLine, endLine, leftColumn, rightColumn);
        DeleteBlock(startLine, endLine, leftColumn, rightColumn);
        _cursor = _bufferManager.Current.Text.ClampCursor(new CursorPosition(startLine, leftColumn), insertMode: true);
        EmitText(events);

        _selection = null;
        events.Add(VimEvent.SelectionChanged(null));
        _blockInsertState = new BlockInsertState(startLine, endLine, leftColumn);
        EnterInsertMode(false, events);
    }

    private void MoveVertical(int delta, List<VimEvent> events)
    {
        var buf = _bufferManager.Current.Text;
        var folds = CurrentBuffer.Folds;
        int[] visMap = folds.BuildVisibleLineMap(buf.LineCount);
        int currentVis = folds.BufferToVisualLine(_cursor.Line, visMap);
        if (currentVis < 0) currentVis = 0;
        int targetVis = Math.Clamp(currentVis + delta, 0, visMap.Length - 1);
        int newLine = visMap[targetVis];
        int maxCol = Math.Max(0, buf.GetLineLength(newLine) - 1);
        _cursor = new CursorPosition(newLine, Math.Min(_preferredColumn, maxCol));
        EmitCursor(events);
    }

    private CursorPosition ClampCursorToVisible(CursorPosition cursor)
    {
        var hiding = CurrentBuffer.Folds.GetHidingFold(cursor.Line);
        if (hiding.HasValue)
        {
            int foldStart = hiding.Value.StartLine;
            int maxCol = Math.Max(0, CurrentBuffer.Text.GetLineLength(foldStart) - 1);
            return new CursorPosition(foldStart, Math.Min(cursor.Column, maxCol));
        }
        return cursor;
    }

    private void GoToLineEnd(bool insertMode, List<VimEvent> events)
    {
        var buf = _bufferManager.Current.Text;
        var len = buf.GetLineLength(_cursor.Line);
        var col = insertMode ? len : Math.Max(0, len - 1);
        _cursor = _cursor with { Column = col };
        _preferredColumn = int.MaxValue;
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
        _preferredColumn = col;
        MoveCursor(new CursorPosition(line, col), events);
    }

    private void MoveToColumn(int oneBasedColumn, List<VimEvent> events)
    {
        var maxCol = Math.Max(0, _bufferManager.Current.Text.GetLineLength(_cursor.Line) - 1);
        var col = Math.Clamp(oneBasedColumn - 1, 0, maxCol);
        _preferredColumn = col;
        MoveCursor(_cursor with { Column = col }, events);
    }

    private static string? ExtractFilePathUnderCursor(string line, int col)
    {
        if (string.IsNullOrEmpty(line) || col < 0 || col >= line.Length)
            return null;

        // Path chars: anything except whitespace, quotes, and common delimiters
        static bool IsPathChar(char c) =>
            !char.IsWhiteSpace(c) && c != '"' && c != '\'' && c != '<' && c != '>' &&
            c != '(' && c != ')' && c != '[' && c != ']' && c != '{' && c != '}' &&
            c != ',' && c != ';';

        if (!IsPathChar(line[col])) return null;

        int start = col;
        while (start > 0 && IsPathChar(line[start - 1])) start--;

        int end = col;
        while (end < line.Length - 1 && IsPathChar(line[end + 1])) end++;

        return line[start..(end + 1)];
    }

    private CursorPosition WordEndBackward(int count)
    {
        var buf = _bufferManager.Current.Text;
        var pos = _cursor;

        for (int c = 0; c < count; c++)
        {
            int line = pos.Line;
            int col = pos.Column - 1;

            while (true)
            {
                var text = buf.GetLine(line);

                if (text.Length == 0 || col < 0)
                {
                    if (line == 0)
                    {
                        pos = CursorPosition.Zero;
                        break;
                    }
                    line--;
                    col = buf.GetLine(line).Length - 1;
                    continue;
                }

                while (col >= 0 && char.IsWhiteSpace(text[col])) col--;
                if (col >= 0)
                {
                    pos = new CursorPosition(line, col);
                    break;
                }

                if (line == 0)
                {
                    pos = CursorPosition.Zero;
                    break;
                }

                line--;
                col = buf.GetLine(line).Length - 1;
            }
        }

        return pos;
    }

    private (CursorPosition Start, CursorPosition End)? GetTextObjectRange(string textObject)
    {
        if (textObject.Length != 2) return null;

        bool around = textObject[0] == 'a';
        char kind = textObject[1];

        return kind switch
        {
            'w' or 'W' => GetWordRange(kind == 'W', around),
            '(' or ')' or 'b' => FindEnclosingPair('(', ')', around),
            '{' or '}' or 'B' => FindEnclosingPair('{', '}', around),
            '[' or ']' => FindEnclosingPair('[', ']', around),
            '<' or '>' => FindEnclosingPair('<', '>', around),
            '"' => FindEnclosingQuote('"', around),
            '\'' => FindEnclosingQuote('\'', around),
            '`' => FindEnclosingQuote('`', around),
            't' => GetTagRange(around),
            's' => GetSentenceRange(around),
            'p' => GetParagraphRange(around),
            _ => null
        };
    }

    private (CursorPosition Start, CursorPosition End)? GetWordRange(bool bigWord, bool around)
    {
        var buf = _bufferManager.Current.Text;
        var lineNo = _cursor.Line;
        var line = buf.GetLine(lineNo);
        if (line.Length == 0) return null;

        bool IsWordChar(char ch) => bigWord ? !char.IsWhiteSpace(ch) : MotionEngine.IsWordChar(ch);

        int col = Math.Clamp(_cursor.Column, 0, Math.Max(0, line.Length - 1));

        if (!IsWordChar(line[col]))
        {
            int right = col;
            while (right < line.Length && !IsWordChar(line[right])) right++;
            if (right < line.Length) col = right;
            else
            {
                int left = col;
                while (left >= 0 && !IsWordChar(line[left])) left--;
                if (left < 0) return null;
                col = left;
            }
        }

        int start = col;
        while (start > 0 && IsWordChar(line[start - 1])) start--;

        int end = col;
        while (end + 1 < line.Length && IsWordChar(line[end + 1])) end++;

        if (around)
        {
            int trailingEnd = end;
            while (trailingEnd + 1 < line.Length && char.IsWhiteSpace(line[trailingEnd + 1])) trailingEnd++;
            if (trailingEnd > end)
                end = trailingEnd;
            else
                while (start > 0 && char.IsWhiteSpace(line[start - 1])) start--;
        }

        return (new CursorPosition(lineNo, start), new CursorPosition(lineNo, end));
    }

    // Shared helper: trim inner content to exclude delimiter characters at (openLine,openCol) and (closeLine,closeCol)
    private (CursorPosition Start, CursorPosition End)? InnerRange(
        TextBuffer buf, int innerOpenLine, int innerOpenCol, int innerCloseLine, int innerCloseCol)
    {
        int iLine = innerOpenLine, iCol = innerOpenCol;
        if (iCol >= buf.GetLine(iLine).Length && iLine + 1 < buf.LineCount)
        { iLine++; iCol = 0; }

        int eLine = innerCloseLine, eCol = innerCloseCol;
        if (eCol < 0 && eLine > 0)
        { eLine--; eCol = buf.GetLine(eLine).Length - 1; }

        if (eLine < iLine || (eLine == iLine && eCol < iCol))
            return null;
        return (new CursorPosition(iLine, iCol), new CursorPosition(eLine, eCol));
    }

    // Find the innermost enclosing bracket pair (possibly multi-line, ±500 line limit)
    private (CursorPosition Start, CursorPosition End)? FindEnclosingPair(char open, char close, bool around)
    {
        var buf = _bufferManager.Current.Text;
        int curLine = _cursor.Line;
        int curCol = Math.Clamp(_cursor.Column, 0, Math.Max(0, buf.GetLine(curLine).Length - 1));

        // Search backward for the unmatched opening bracket
        int depth = 0;
        int openLine = -1, openCol = -1;

        for (int l = curLine; l >= Math.Max(0, curLine - 500); l--)
        {
            var lineText = buf.GetLine(l);
            int startC = (l == curLine) ? curCol : lineText.Length - 1;

            for (int c = startC; c >= 0; c--)
            {
                char ch = lineText[c];
                if (ch == close) depth++;
                else if (ch == open)
                {
                    if (depth == 0) { openLine = l; openCol = c; goto foundOpen; }
                    depth--;
                }
            }
        }
        return null;

        foundOpen:
        // Search forward from opening bracket for the matching close
        depth = 0;
        for (int l = openLine; l < buf.LineCount; l++)
        {
            var lineText = buf.GetLine(l);
            int startC = (l == openLine) ? openCol : 0;

            for (int c = startC; c < lineText.Length; c++)
            {
                char ch = lineText[c];
                if (ch == open) depth++;
                else if (ch == close)
                {
                    depth--;
                    if (depth == 0)
                    {
                        if (around)
                            return (new CursorPosition(openLine, openCol), new CursorPosition(l, c));
                        return InnerRange(buf, openLine, openCol + 1, l, c - 1);
                    }
                }
            }
        }
        return null;
    }

    // Find enclosing quote pair on the current line (single-pass, no allocation)
    private (CursorPosition Start, CursorPosition End)? FindEnclosingQuote(char quote, bool around)
    {
        var buf = _bufferManager.Current.Text;
        int lineNo = _cursor.Line;
        var line = buf.GetLine(lineNo);
        if (line.Length == 0) return null;

        int col = Math.Clamp(_cursor.Column, 0, line.Length - 1);

        // Walk left to find opening quote (respecting backslash escapes)
        int q1 = -1;
        for (int i = col; i >= 0; i--)
        {
            if (i > 0 && line[i - 1] == '\\') continue;
            if (line[i] == quote) { q1 = i; break; }
        }
        if (q1 < 0) return null;

        // Walk right from q1+1 to find closing quote
        int q2 = -1;
        for (int i = q1 + 1; i < line.Length; i++)
        {
            if (line[i] == '\\') { i++; continue; }
            if (line[i] == quote) { q2 = i; break; }
        }
        if (q2 < 0) return null;

        if (around)
            return (new CursorPosition(lineNo, q1), new CursorPosition(lineNo, q2));
        if (q2 > q1 + 1)
            return (new CursorPosition(lineNo, q1 + 1), new CursorPosition(lineNo, q2 - 1));
        return null; // empty quotes
    }

    // Find enclosing HTML/XML tag  <tag>...</tag>
    private (CursorPosition Start, CursorPosition End)? GetTagRange(bool around)
    {
        var buf = _bufferManager.Current.Text;
        int curLine = _cursor.Line;
        int curCol = Math.Clamp(_cursor.Column, 0, Math.Max(0, buf.GetLine(curLine).Length - 1));

        // Search backward for an opening tag <tagname>
        string? tagName = null;
        int openLine = -1, openCol = -1, openEnd = -1;

        for (int l = curLine; l >= Math.Max(0, curLine - 200); l--)
        {
            var lineText = buf.GetLine(l);
            int endC = (l == curLine) ? curCol : lineText.Length - 1;

            for (int c = endC; c >= 0; c--)
            {
                if (lineText[c] != '<') continue;
                if (c + 1 < lineText.Length && lineText[c + 1] == '/') continue; // skip closing tags

                int nameStart = c + 1;
                int nameEnd = nameStart;
                while (nameEnd < lineText.Length &&
                       lineText[nameEnd] != '>' && lineText[nameEnd] != ' ' &&
                       lineText[nameEnd] != '\t' && lineText[nameEnd] != '/')
                    nameEnd++;

                if (nameEnd <= nameStart || nameEnd >= lineText.Length) continue;

                // Find closing '>' of the opening tag
                int closeGt = nameEnd;
                while (closeGt < lineText.Length && lineText[closeGt] != '>') closeGt++;
                if (closeGt >= lineText.Length) continue;
                if (lineText[closeGt - 1] == '/') continue; // skip self-closing tags <br/> <img />

                tagName = lineText[nameStart..nameEnd];
                openLine = l;
                openCol = c;
                openEnd = closeGt;
                goto foundOpenTag;
            }
        }
        return null;

        foundOpenTag:
        string closeTag = $"</{tagName}>";
        for (int l = openLine; l < Math.Min(buf.LineCount, openLine + 200); l++)
        {
            var lineText = buf.GetLine(l);
            int startC = (l == openLine) ? openEnd + 1 : 0;
            int idx = lineText.IndexOf(closeTag, startC, StringComparison.Ordinal);
            if (idx >= 0)
            {
                if (around)
                    return (new CursorPosition(openLine, openCol),
                            new CursorPosition(l, idx + closeTag.Length - 1));
                return InnerRange(buf, openLine, openEnd + 1, l, idx - 1);
            }
        }
        return null;
    }

    // Sentence text object (single-line approximation)
    private (CursorPosition Start, CursorPosition End)? GetSentenceRange(bool around)
    {
        var buf = _bufferManager.Current.Text;
        int lineNo = _cursor.Line;
        var line = buf.GetLine(lineNo);
        if (line.Length == 0) return null;

        int col = Math.Clamp(_cursor.Column, 0, line.Length - 1);

        static bool IsSentenceTerminator(char c) => c is '.' or '!' or '?';

        // Find sentence start: go left past whitespace, then to after previous terminator
        int start = col;
        while (start > 0 && char.IsWhiteSpace(line[start])) start--;
        // Walk left until we hit a sentence terminator or BOL
        while (start > 0 && !IsSentenceTerminator(line[start - 1])) start--;
        // Skip leading whitespace after terminator
        while (start < line.Length && char.IsWhiteSpace(line[start])) start++;

        // Find sentence end: walk right to terminator or EOL
        int end = col;
        while (end < line.Length - 1 && !IsSentenceTerminator(line[end])) end++;
        // end is at terminator or last char

        if (around)
        {
            // Include trailing whitespace
            int te = end;
            while (te + 1 < line.Length && char.IsWhiteSpace(line[te + 1])) te++;
            end = te;
        }

        return (new CursorPosition(lineNo, start), new CursorPosition(lineNo, end));
    }

    // Paragraph text object (multi-line, blank-line delimited)
    private (CursorPosition Start, CursorPosition End)? GetParagraphRange(bool around)
    {
        var buf = _bufferManager.Current.Text;
        int curLine = _cursor.Line;

        bool IsBlank(int l) => string.IsNullOrWhiteSpace(buf.GetLine(l));

        if (IsBlank(curLine))
        {
            if (!around) return null;
            // ap on blank line: expand blank block then grab next paragraph
            int start = curLine;
            while (start > 0 && IsBlank(start - 1)) start--;
            int end = curLine;
            while (end + 1 < buf.LineCount && IsBlank(end + 1)) end++;
            // include next paragraph
            while (end + 1 < buf.LineCount && !IsBlank(end + 1)) end++;
            return (new CursorPosition(start, 0),
                    new CursorPosition(end, Math.Max(0, buf.GetLine(end).Length - 1)));
        }

        // Find paragraph boundaries (non-blank block containing cursor)
        int pStart = curLine;
        while (pStart > 0 && !IsBlank(pStart - 1)) pStart--;

        int pEnd = curLine;
        while (pEnd + 1 < buf.LineCount && !IsBlank(pEnd + 1)) pEnd++;

        if (around)
        {
            // Include trailing blank lines
            int end = pEnd;
            while (end + 1 < buf.LineCount && IsBlank(end + 1)) end++;
            return (new CursorPosition(pStart, 0),
                    new CursorPosition(end, Math.Max(0, buf.GetLine(end).Length - 1)));
        }

        return (new CursorPosition(pStart, 0),
                new CursorPosition(pEnd, Math.Max(0, buf.GetLine(pEnd).Length - 1)));
    }

    private int GetLineLength() => _bufferManager.Current.Text.GetLineLength(_cursor.Line);

    private void OpenLineBelow(List<VimEvent> events)
    {
        Snapshot();
        var buf = _bufferManager.Current.Text;
        var indent = GetAutoIndent(buf, _cursor.Line);
        CurrentBuffer.Folds.OnLinesInserted(_cursor.Line, 1);
        buf.InsertLines(_cursor.Line, [indent]);
        _cursor = new CursorPosition(_cursor.Line + 1, indent.Length);
        EmitText(events);
    }

    private void OpenLineAbove(List<VimEvent> events)
    {
        Snapshot();
        var buf = _bufferManager.Current.Text;
        var indent = GetAutoIndent(buf, _cursor.Line);
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

    private void ExecuteDelete(CursorPosition from, CursorPosition to, bool linewise, List<VimEvent> events)
    {
        Snapshot();
        var buf = _bufferManager.Current.Text;
        YankRange('"', from, to, linewise);

        if (linewise)
        {
            DeleteLines(from.Line, to.Line, events);
            return;
        }

        if (from.Line == to.Line)
        {
            buf.DeleteRange(from.Line, from.Column, to.Column + 1);
            _cursor = buf.ClampCursor(from);
        }
        else
        {
            // Multi-line delete
            buf.DeleteRange(from.Line, from.Column, buf.GetLineLength(from.Line));
            buf.DeleteRange(to.Line, 0, to.Column + 1);
            for (int l = to.Line - 1; l > from.Line; l--)
                buf.DeleteLines(l, l);
            buf.JoinLines(from.Line);
            _cursor = buf.ClampCursor(from);
        }
        EmitText(events);
    }

    private void DeleteLines(int start, int end, List<VimEvent> events)
    {
        var buf = _bufferManager.Current.Text;
        YankLines(start, end, '"', events);
        CurrentBuffer.Folds.OnLinesDeleted(start, end);
        buf.DeleteLines(start, end);
        _cursor = buf.ClampCursor(new CursorPosition(start, 0));
        EmitText(events);
    }

    private void YankRange(char register, CursorPosition from, CursorPosition to, bool linewise)
    {
        var buf = _bufferManager.Current.Text;
        string text;
        if (from.Line == to.Line)
            text = buf.GetLine(from.Line)[from.Column..(Math.Min(to.Column + 1, buf.GetLineLength(from.Line)))];
        else
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(buf.GetLine(from.Line)[from.Column..]);
            for (int l = from.Line + 1; l < to.Line; l++)
                sb.Append('\n').Append(buf.GetLine(l));
            sb.Append('\n').Append(buf.GetLine(to.Line)[..Math.Min(to.Column + 1, buf.GetLineLength(to.Line))]);
            text = sb.ToString();
        }
        _registerManager.SetYank(register, new Register(text, linewise ? RegisterType.Line : RegisterType.Character));
    }

    private void YankLines(int start, int end, char register, List<VimEvent> events)
    {
        var buf = _bufferManager.Current.Text;
        var lines = buf.GetLines(start, end);
        var text = string.Join("\n", lines);
        _registerManager.SetYank(register, new Register(text, RegisterType.Line));
        EmitStatus(events, $"{lines.Length} line(s) yanked");
    }

    private void PasteAfter(char register, List<VimEvent> events)
    {
        Snapshot();
        var reg = _registerManager.Get(register);
        if (reg.IsEmpty) return;
        var buf = _bufferManager.Current.Text;

        if (reg.Type == RegisterType.Line)
        {
            var lines = reg.GetLines();
            CurrentBuffer.Folds.OnLinesInserted(_cursor.Line, lines.Length);
            buf.InsertLines(_cursor.Line, lines);
            _cursor = new CursorPosition(_cursor.Line + 1, 0);
        }
        else
        {
            var col = Math.Min(_cursor.Column + 1, buf.GetLineLength(_cursor.Line));
            buf.InsertText(_cursor.Line, col, reg.Text);
            _cursor = _cursor with { Column = col + reg.Text.Length - 1 };
        }
        EmitText(events);
    }

    private void PasteBefore(char register, List<VimEvent> events)
    {
        Snapshot();
        var reg = _registerManager.Get(register);
        if (reg.IsEmpty) return;
        var buf = _bufferManager.Current.Text;

        if (reg.Type == RegisterType.Line)
        {
            var lines = reg.GetLines();
            CurrentBuffer.Folds.OnLinesInserted(_cursor.Line - 1, 1);
            buf.InsertLineAbove(_cursor.Line);
            for (int i = 0; i < lines.Length; i++)
                buf.ReplaceLine(_cursor.Line + i, lines[i]);
            _cursor = new CursorPosition(_cursor.Line, 0);
        }
        else
        {
            buf.InsertText(_cursor.Line, _cursor.Column, reg.Text);
            _cursor = _cursor with { Column = _cursor.Column + reg.Text.Length - 1 };
        }
        EmitText(events);
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

    private void CycleKeywordCompletion(int dir, List<VimEvent> events)
    {
        var buf = _bufferManager.Current.Text;
        if (_kwCompletions.Length == 0)
        {
            // Start a new completion session: extract word prefix before cursor
            var line = buf.GetLine(_cursor.Line);
            int col = _cursor.Column;
            int start = col;
            while (start > 0 && MotionEngine.IsWordChar(line[start - 1]))
                start--;
            _kwCompletionPrefix = line[start..col];
            _kwCompletions = CollectBufferKeywords(_kwCompletionPrefix);
            _kwCompletionIndex = -1;
            _kwCompletionApplied = _kwCompletionPrefix.Length;
            if (_kwCompletions.Length == 0)
            {
                EmitStatus(events, "Pattern not found");
                return;
            }
        }

        _kwCompletionIndex = dir > 0
            ? (_kwCompletionIndex + 1) % _kwCompletions.Length
            : (_kwCompletionIndex - 1 + _kwCompletions.Length) % _kwCompletions.Length;

        var completion = _kwCompletions[_kwCompletionIndex];
        // Replace the previously applied text with the new completion
        int delStart = _cursor.Column - _kwCompletionApplied;
        if (_kwCompletionApplied > 0)
            buf.DeleteRange(_cursor.Line, delStart, delStart + _kwCompletionApplied - 1);
        buf.InsertText(_cursor.Line, delStart, completion);
        _cursor = _cursor with { Column = delStart + completion.Length };
        _kwCompletionApplied = completion.Length;
        EmitText(events);
        EmitStatus(events, $"\"{completion}\"");
    }

    private string[] CollectBufferKeywords(string prefix)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var vbuf in _bufferManager.Buffers)
        {
            for (int l = 0; l < vbuf.Text.LineCount; l++)
            {
                var line = vbuf.Text.GetLine(l);
                int i = 0;
                while (i < line.Length)
                {
                    if (MotionEngine.IsWordChar(line[i]))
                    {
                        int start = i;
                        while (i < line.Length && MotionEngine.IsWordChar(line[i]))
                            i++;
                        var word = line[start..i];
                        if (word.Length > prefix.Length &&
                            word.StartsWith(prefix, StringComparison.Ordinal) &&
                            seen.Add(word))
                            result.Add(word);
                    }
                    else i++;
                }
            }
        }
        return [.. result];
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

    private void ExecuteReplace(char ch, List<VimEvent> events)
    {
        Snapshot();
        var buf = _bufferManager.Current.Text;
        if (_cursor.Column < buf.GetLineLength(_cursor.Line))
        {
            buf.DeleteChar(_cursor.Line, _cursor.Column);
            buf.InsertChar(_cursor.Line, _cursor.Column, ch);
        }
        EmitText(events);
    }

    private void ExecuteIncrementNumber(int count, bool increment, List<VimEvent> events)
    {
        var buf = _bufferManager.Current.Text;
        var line = buf.GetLine(_cursor.Line);
        int col = _cursor.Column;

        int numStart = -1, numEnd = -1;
        bool isHex = false, hexUpper = false;

        for (int i = col; i < line.Length; i++)
        {
            // Hex: 0x... or 0X...
            if (line[i] == '0' && i + 1 < line.Length && (line[i + 1] == 'x' || line[i + 1] == 'X'))
            {
                hexUpper = line[i + 1] == 'X';
                int j = i + 2;
                while (j < line.Length && char.IsAsciiHexDigit(line[j])) j++;
                if (j > i + 2) { numStart = i; numEnd = j; isHex = true; break; }
            }
            // Negative decimal: -N (not preceded by word char or underscore)
            if (line[i] == '-' && i + 1 < line.Length && char.IsDigit(line[i + 1])
                && (i == 0 || (!char.IsLetterOrDigit(line[i - 1]) && line[i - 1] != '_')))
            {
                int j = i + 1;
                while (j < line.Length && char.IsDigit(line[j])) j++;
                numStart = i; numEnd = j; break;
            }
            // Decimal: walk backward first to find the true start of the number
            if (char.IsDigit(line[i]))
            {
                int start = i;
                while (start > 0 && char.IsDigit(line[start - 1])) start--;
                // Check for negative sign before the number
                if (start > 0 && line[start - 1] == '-'
                    && (start < 2 || (!char.IsLetterOrDigit(line[start - 2]) && line[start - 2] != '_')))
                    start--;
                int end = i + 1;
                while (end < line.Length && char.IsDigit(line[end])) end++;
                numStart = start; numEnd = end; break;
            }
        }

        if (numStart == -1) return;

        long delta = increment ? count : -(long)count;
        string numStr = line[numStart..numEnd];
        string newStr;

        if (isHex)
        {
            ulong hexVal = Convert.ToUInt64(numStr[2..], 16);
            long newVal = (long)hexVal + delta;
            int digits = numStr.Length - 2;
            if (newVal >= 0)
            {
                string fmt = hexUpper ? "X" : "x";
                newStr = (hexUpper ? "0X" : "0x") + ((ulong)newVal).ToString(fmt).PadLeft(digits, '0');
            }
            else
            {
                newStr = newVal.ToString();
            }
        }
        else
        {
            long decVal = long.Parse(numStr);
            newStr = (decVal + delta).ToString();
        }

        Snapshot();
        string newLine = line[..numStart] + newStr + line[numEnd..];
        buf.ReplaceLine(_cursor.Line, newLine);
        _cursor = _cursor with { Column = Math.Min(numStart + newStr.Length - 1, Math.Max(0, newLine.Length - 1)) };
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

    private void RepeatLastChange(int count, List<VimEvent> events)
    {
        if (_lastRepeatChange == null) return;

        _isDotReplaying = true;
        try
        {
            for (int i = 0; i < count; i++)
            {
                ExecuteNormalCommand(_lastRepeatChange.Command, events);
                foreach (var stroke in _lastRepeatChange.InsertKeys)
                    ProcessKeyInternal(stroke.Key, stroke.Ctrl, stroke.Shift, stroke.Alt, events);
            }
        }
        finally
        {
            _isDotReplaying = false;
        }
    }

    private void SetRepeatChange(ParsedCommand cmd)
    {
        if (_isDotReplaying) return;
        _lastRepeatChange = new RepeatChange(cmd, []);
        _pendingInsertRepeatCommand = null;
        _pendingInsertRepeatKeys = null;
    }

    private void BeginInsertRepeat(ParsedCommand cmd)
    {
        if (_isDotReplaying) return;
        _pendingInsertRepeatCommand = cmd;
        _pendingInsertRepeatKeys = [];
    }

    private void TrackPendingInsertRepeat(VimKeyStroke stroke, VimMode modeBefore)
    {
        if (_isDotReplaying || _pendingInsertRepeatKeys == null) return;
        if (modeBefore is not (VimMode.Insert or VimMode.Replace)) return;

        _pendingInsertRepeatKeys.Add(stroke);

        if (_mode is VimMode.Insert or VimMode.Replace) return;
        if (_pendingInsertRepeatCommand == null) return;

        _lastRepeatChange = new RepeatChange(_pendingInsertRepeatCommand.Value, [.. _pendingInsertRepeatKeys]);
        _pendingInsertRepeatCommand = null;
        _pendingInsertRepeatKeys = null;
    }

    private void JoinLines(int count, List<VimEvent> events)
    {
        Snapshot();
        var buf = _bufferManager.Current.Text;
        for (int i = 0; i < count; i++)
        {
            if (_cursor.Line < buf.LineCount - 1)
            {
                var len = buf.GetLineLength(_cursor.Line);
                buf.JoinLines(_cursor.Line);
                // Add space if needed
                if (len > 0 && _cursor.Column < buf.GetLineLength(_cursor.Line))
                {
                    var joined = buf.GetLine(_cursor.Line);
                    if (joined.Length > len && joined[len] != ' ')
                        buf.InsertChar(_cursor.Line, len, ' ');
                }
            }
        }
        EmitText(events);
    }

    private void JoinLinesNoSpace(int count, List<VimEvent> events)
    {
        Snapshot();
        var buf = _bufferManager.Current.Text;
        int joinCol = 0;
        for (int i = 0; i < count; i++)
        {
            if (_cursor.Line < buf.LineCount - 1)
            {
                // Strip all leading whitespace from next line before joining (matches Vim gJ)
                var nextLine = buf.GetLine(_cursor.Line + 1);
                int trimStart = nextLine.Length - nextLine.TrimStart().Length;
                if (trimStart > 0)
                    buf.DeleteRange(_cursor.Line + 1, 0, trimStart);
                joinCol = buf.GetLineLength(_cursor.Line);
                buf.JoinLines(_cursor.Line);
            }
        }
        _cursor = buf.ClampCursor(_cursor with { Column = Math.Max(0, joinCol - 1) });
        EmitText(events);
    }

    private void ToggleCase(int count, List<VimEvent> events)
    {
        Snapshot();
        var buf = _bufferManager.Current.Text;
        for (int i = 0; i < count; i++)
        {
            var line = buf.GetLine(_cursor.Line);
            if (_cursor.Column < line.Length)
            {
                var ch = line[_cursor.Column];
                var toggled = char.IsUpper(ch) ? char.ToLower(ch) : char.ToUpper(ch);
                buf.DeleteChar(_cursor.Line, _cursor.Column);
                buf.InsertChar(_cursor.Line, _cursor.Column, toggled);
                if (_cursor.Column < buf.GetLineLength(_cursor.Line) - 1)
                    _cursor = _cursor with { Column = _cursor.Column + 1 };
            }
        }
        EmitText(events);
    }

    private enum CaseConversion { Lower, Upper, Toggle }

    private void ApplyCaseConversion(CursorPosition from, CursorPosition to, bool linewise, CaseConversion mode, List<VimEvent> events)
    {
        var buf = _bufferManager.Current.Text;
        var start = from.Line < to.Line || (from.Line == to.Line && from.Column <= to.Column) ? from : to;
        var end = start == from ? to : from;

        for (int l = start.Line; l <= end.Line; l++)
        {
            var line = buf.GetLine(l);
            int colStart = (!linewise && l == start.Line) ? start.Column : 0;
            int colEnd = (!linewise && l == end.Line) ? end.Column : line.Length - 1;
            var chars = line.ToCharArray();
            bool changed = false;
            for (int c = colStart; c <= colEnd && c < chars.Length; c++)
            {
                char converted = mode switch
                {
                    CaseConversion.Lower => char.ToLower(chars[c]),
                    CaseConversion.Upper => char.ToUpper(chars[c]),
                    _ => char.IsUpper(chars[c]) ? char.ToLower(chars[c]) : char.ToUpper(chars[c])
                };
                if (converted != chars[c]) { chars[c] = converted; changed = true; }
            }
            if (changed) buf.ReplaceLine(l, new string(chars));
        }
        MoveCursor(start, events);
        EmitText(events);
    }

    private void IndentLine(bool indent, int count, List<VimEvent> events)
    {
        Snapshot();
        IndentRange(_cursor.Line, _cursor.Line, indent, events);
    }

    private void IndentRange(int start, int end, bool indent, List<VimEvent> events)
    {
        Snapshot();
        var buf = _bufferManager.Current.Text;
        var sw = _config.Options.ShiftWidth;
        var indentStr = _config.Options.ExpandTab ? new string(' ', sw) : "\t";

        for (int l = start; l <= end; l++)
        {
            if (indent)
                buf.InsertText(l, 0, indentStr);
            else
            {
                var line = buf.GetLine(l);
                int toRemove = 0;
                for (int i = 0; i < sw && i < line.Length && (line[i] == ' ' || line[i] == '\t'); i++)
                    toRemove++;
                if (toRemove > 0) buf.DeleteRange(l, 0, toRemove);
            }
        }
        EmitText(events);
    }

    private void AutoIndentRange(int start, int end, List<VimEvent> events) { EmitText(events); }

    private void ToggleCommentLines(int startLine, int endLine, List<VimEvent> events)
    {
        var prefix = _syntaxEngine.GetCommentPrefix();
        if (prefix == null)
        {
            EmitStatus(events, "No comment prefix for this file type");
            return;
        }

        var buf = _bufferManager.Current.Text;
        Snapshot();

        // Collect line data and detect if ALL non-empty lines are already commented
        int count = endLine - startLine + 1;
        var lines = new (string Raw, string Trimmed, int Indent)[count];
        bool allCommented = true;
        for (int i = 0; i < count; i++)
        {
            var raw = buf.GetLine(startLine + i);
            var trimmed = raw.TrimStart();
            lines[i] = (raw, trimmed, raw.Length - trimmed.Length);
            if (trimmed.Length > 0 && !trimmed.StartsWith(prefix))
                allCommented = false;
        }

        for (int i = 0; i < count; i++)
        {
            var (raw, trimmed, indent) = lines[i];
            if (allCommented)
            {
                // Remove comment prefix (and one trailing space if present)
                if (!trimmed.StartsWith(prefix)) continue;
                string uncommented = trimmed[prefix.Length..];
                if (uncommented.StartsWith(" ")) uncommented = uncommented[1..];
                buf.ReplaceLine(startLine + i, raw[..indent] + uncommented);
            }
            else
            {
                // Add comment prefix; skip blank lines
                if (trimmed.Length == 0) continue;
                buf.ReplaceLine(startLine + i, raw[..indent] + prefix + " " + trimmed);
            }
        }

        EmitText(events);
    }

    private void FormatText(int startLine, int endLine, List<VimEvent> events)
    {
        var buf = _bufferManager.Current.Text;
        int tw = _config.Options.TextWidth;
        if (tw <= 0) tw = 79;

        // Collect lines, preserving leading indent of first line in each paragraph
        var result = new List<string>();
        int i = startLine;
        while (i <= endLine)
        {
            var line = buf.GetLine(i);
            // Blank line: preserve as-is and start new paragraph
            if (string.IsNullOrWhiteSpace(line))
            {
                result.Add(line);
                i++;
                continue;
            }

            // Detect indent from first line of paragraph
            var indent = "";
            int indentLen = 0;
            while (indentLen < line.Length && (line[indentLen] == ' ' || line[indentLen] == '\t'))
                indentLen++;
            indent = line[..indentLen];

            // Collect all non-blank lines of this paragraph
            var words = new List<string>();
            while (i <= endLine && !string.IsNullOrWhiteSpace(buf.GetLine(i)))
            {
                var l = buf.GetLine(i).TrimStart();
                words.AddRange(l.Split(' ', StringSplitOptions.RemoveEmptyEntries));
                i++;
            }

            // Wrap words at textwidth
            var currentLine = new System.Text.StringBuilder(indent);
            int currentLen = indent.Length;
            bool first = true;
            foreach (var word in words)
            {
                if (first)
                {
                    currentLine.Append(word);
                    currentLen += word.Length;
                    first = false;
                }
                else if (currentLen + 1 + word.Length <= tw)
                {
                    currentLine.Append(' ');
                    currentLine.Append(word);
                    currentLen += 1 + word.Length;
                }
                else
                {
                    result.Add(currentLine.ToString());
                    currentLine = new System.Text.StringBuilder(indent);
                    currentLine.Append(word);
                    currentLen = indent.Length + word.Length;
                }
            }
            if (currentLine.Length > 0)
                result.Add(currentLine.ToString());
        }

        // Replace lines startLine..endLine with result lines
        // First replace existing lines, then insert/delete extras
        int origCount = endLine - startLine + 1;
        int newCount = result.Count;
        int replaceCount = Math.Min(origCount, newCount);
        for (int j = 0; j < replaceCount; j++)
            buf.ReplaceLine(startLine + j, result[j]);
        if (newCount > origCount)
        {
            for (int j = origCount; j < newCount; j++)
            {
                buf.InsertLineAbove(startLine + j, result[j]);
            }
        }
        else if (newCount < origCount)
        {
            buf.DeleteLines(startLine + newCount, startLine + origCount - 1);
        }

        _cursor = _cursor with { Line = Math.Min(startLine, buf.LineCount - 1), Column = 0 };
        EmitText(events);
        EmitCursor(events);
        EmitStatus(events, $"Formatted {endLine - startLine + 1} lines");
    }

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

    // ─────────────── SURROUND ───────────────
    private static (string Open, string Close) GetSurroundPair(char ch) => ch switch
    {
        '(' or 'b' => ("( ", " )"),
        ')'        => ("(", ")"),
        '{' or 'B' => ("{ ", " }"),
        '}'        => ("{", "}"),
        '['        => ("[ ", " ]"),
        ']'        => ("[", "]"),
        '<'        => ("< ", " >"),
        '>'        => ("<", ">"),
        _          => (ch.ToString(), ch.ToString())
    };

    private static char GetSurroundOpen(char ch) => ch switch
    {
        ')' or 'b' => '(',
        '}' or 'B' => '{',
        ']'        => '[',
        '>'        => '<',
        _          => ch
    };

    private static char GetSurroundClose(char ch) => ch switch
    {
        '(' or 'b' => ')',
        '{' or 'B' => '}',
        '['        => ']',
        '<'        => '>',
        _          => ch
    };

    private void ApplySurround(CursorPosition start, CursorPosition end, bool linewise, char ch, List<VimEvent> events)
    {
        var buf = _bufferManager.Current.Text;
        var (open, close) = GetSurroundPair(ch);

        if (start.Line == end.Line)
        {
            // Single-line: insert close first, then open (to preserve column indices)
            int closeCol = Math.Min(end.Column + 1, buf.GetLineLength(end.Line));
            buf.InsertText(end.Line, closeCol, close);
            buf.InsertText(start.Line, start.Column, open);
            _cursor = start with { Column = start.Column };
        }
        else
        {
            // Multi-line: add close at end of last line, open at start of first line
            var lastLine = buf.GetLine(end.Line).TrimEnd();
            buf.ReplaceLine(end.Line, lastLine + close);
            var firstLine = buf.GetLine(start.Line);
            buf.ReplaceLine(start.Line, open + firstLine);
            _cursor = start with { Column = 0 };
        }

        EmitText(events);
        EmitCursor(events);
    }

    private void ExecuteDeleteSurround(char ch, List<VimEvent> events)
    {
        var buf = _bufferManager.Current.Text;
        char openCh = GetSurroundOpen(ch);
        char closeCh = GetSurroundClose(ch);

        (CursorPosition Start, CursorPosition End)? pair;
        if (ch is '"' or '\'' or '`')
            pair = FindEnclosingQuote(ch, true);
        else
            pair = FindEnclosingPair(openCh, closeCh, true);

        if (pair == null) { EmitStatus(events, $"No surrounding '{ch}' found"); return; }

        var (s, e) = pair.Value;
        // Delete close char first (higher position), then open char
        if (e.Line > s.Line || (e.Line == s.Line && e.Column > s.Column))
        {
            buf.DeleteChar(e.Line, e.Column);
            buf.DeleteChar(s.Line, s.Column);
        }
        else
        {
            buf.DeleteChar(s.Line, s.Column);
        }
        _cursor = buf.ClampCursor(s, false);
        EmitText(events);
        EmitCursor(events);
    }

    private void ExecuteChangeSurround(char from, char to, List<VimEvent> events)
    {
        var buf = _bufferManager.Current.Text;
        char openFrom = GetSurroundOpen(from);
        char closeFrom = GetSurroundClose(from);

        (CursorPosition Start, CursorPosition End)? pair;
        if (from is '"' or '\'' or '`')
            pair = FindEnclosingQuote(from, true);
        else
            pair = FindEnclosingPair(openFrom, closeFrom, true);

        if (pair == null) { EmitStatus(events, $"No surrounding '{from}' found"); return; }

        var (s, e) = pair.Value;
        var (openStr, closeStr) = GetSurroundPair(to);

        // Replace close first, then open (to preserve column indices)
        if (e.Line > s.Line)
        {
            // Multi-line: replace chars
            var closeLine = buf.GetLine(e.Line);
            buf.ReplaceLine(e.Line, closeLine[..e.Column] + closeStr + (e.Column + 1 < closeLine.Length ? closeLine[(e.Column + 1)..] : ""));
            var openLine = buf.GetLine(s.Line);
            buf.ReplaceLine(s.Line, openLine[..s.Column] + openStr + (s.Column + 1 < openLine.Length ? openLine[(s.Column + 1)..] : ""));
        }
        else if (e.Column > s.Column)
        {
            buf.DeleteRange(e.Line, e.Column, e.Column);
            buf.InsertText(e.Line, e.Column, closeStr);
            buf.DeleteRange(s.Line, s.Column, s.Column);
            buf.InsertText(s.Line, s.Column, openStr);
        }
        else
        {
            buf.DeleteRange(s.Line, s.Column, s.Column);
            buf.InsertText(s.Line, s.Column, openStr + closeStr);
        }

        _cursor = buf.ClampCursor(s, false);
        EmitText(events);
        EmitCursor(events);
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

    private void RepeatFind(bool reverse, List<VimEvent> events)
    {
        if (_commandParser.LastFindChar == null) return;
        var motion = new MotionEngine(_bufferManager.Current.Text);
        bool fwd = reverse ? !_commandParser.LastFindForward : _commandParser.LastFindForward;
        var pos = motion.FindChar(_cursor, _commandParser.LastFindChar.Value, fwd, _commandParser.LastFindBefore);
        MoveCursor(pos, events);
    }

    private void SearchNext(bool sameDir, List<VimEvent> events)
    {
        var forward = sameDir ? _searchForward : !_searchForward;
        DoSearch(forward, events);
    }

    private void SearchWordUnderCursor(bool forward, List<VimEvent> events)
    {
        var line = _bufferManager.Current.Text.GetLine(_cursor.Line);
        int start = _cursor.Column;
        while (start > 0 && MotionEngine.IsWordChar(line[start - 1])) start--;
        int end = _cursor.Column;
        while (end < line.Length && MotionEngine.IsWordChar(line[end])) end++;
        if (end > start)
        {
            _searchPattern = line[start..end];
            _searchForward = forward;
            DoSearch(forward, events);
        }
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

    private CursorPosition ScreenPosition(int offset)
    {
        var line = Math.Clamp(_cursor.Line + offset, 0, _bufferManager.Current.Text.LineCount - 1);
        return new CursorPosition(line, 0);
    }

    private void ExecuteExCommand(string cmdLine, List<VimEvent> events)
    {
        if (TryExecuteConfigCommand(cmdLine, out var msg, out var err, out var optChanged))
        {
            if (err != null) EmitStatus(events, "E: " + err);
            else if (msg != null) EmitStatus(events, msg);
            if (optChanged) events.Add(VimEvent.OptionsChanged());
            return;
        }
        if (TryExecuteNormalCmd(cmdLine, events)) return;
        var preLines = CurrentBuffer.Text.Snapshot();
        var preCursor = _cursor;
        var result = _exProcessor.Execute(cmdLine, _cursor);
        if (!result.Success) EmitStatus(events, "E: " + result.Message);
        else if (result.Message != null) EmitStatus(events, result.Message);
        if (result.TextModified) { CurrentBuffer.Undo.Snapshot(preLines, preCursor); EmitText(events); }
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
        var macro = _macroManager.GetMacro(register);
        if (macro == null) { EmitStatus(events, $"No macro in register @{register}"); return; }
        _macroManager.SetLastPlayed(register);

        for (int r = 0; r < count; r++)
        {
            foreach (var stroke in macro)
                ProcessKeyInternal(stroke.Key, stroke.Ctrl, stroke.Shift, stroke.Alt, events);
        }
    }

    private void ExecuteVisualDelete(char register, List<VimEvent> events)
    {
        if (_selection == null) { ExitVisualMode(events); return; }
        var sel = _selection.Value;
        if (_mode == VimMode.VisualBlock)
        {
            var (startLine, endLine, leftColumn, rightColumn) = GetBlockBounds(sel);
            Snapshot();
            YankBlock(register, startLine, endLine, leftColumn, rightColumn);
            DeleteBlock(startLine, endLine, leftColumn, rightColumn);
            _cursor = _bufferManager.Current.Text.ClampCursor(new CursorPosition(startLine, leftColumn));
            EmitText(events);
            ExitVisualMode(events);
            return;
        }

        Snapshot();
        var start = sel.NormalizedStart;
        var end = sel.NormalizedEnd;

        if (_mode == VimMode.VisualLine)
        {
            YankLines(start.Line, end.Line, register, events);
            _bufferManager.Current.Text.DeleteLines(start.Line, end.Line);
            _cursor = _bufferManager.Current.Text.ClampCursor(new CursorPosition(start.Line, 0));
        }
        else
        {
            YankRange(register, start, end, false);
            ExecuteDelete(start, end, false, events);
        }
        ExitVisualMode(events);
    }

    private void ExecuteVisualYank(char register, List<VimEvent> events)
    {
        if (_selection == null) { ExitVisualMode(events); return; }
        var sel = _selection.Value;
        if (_mode == VimMode.VisualBlock)
        {
            var (startLine, endLine, leftColumn, rightColumn) = GetBlockBounds(sel);
            YankBlock(register, startLine, endLine, leftColumn, rightColumn);
            MoveCursor(new CursorPosition(startLine, leftColumn), events);
            ExitVisualMode(events);
            return;
        }

        var start = sel.NormalizedStart;
        var end = sel.NormalizedEnd;

        if (_mode == VimMode.VisualLine)
            YankLines(start.Line, end.Line, register, events);
        else
            YankRange(register, start, end, false);

        MoveCursor(start, events);
        ExitVisualMode(events);
    }

    private void DeleteBlock(int startLine, int endLine, int leftColumn, int rightColumn)
    {
        var buf = _bufferManager.Current.Text;
        for (int line = startLine; line <= endLine; line++)
        {
            var length = buf.GetLineLength(line);
            if (length <= leftColumn) continue;
            var endExclusive = Math.Min(length, rightColumn + 1);
            buf.DeleteRange(line, leftColumn, endExclusive);
        }
    }

    private void YankBlock(char register, int startLine, int endLine, int leftColumn, int rightColumn)
    {
        var buf = _bufferManager.Current.Text;
        var lines = new List<string>();
        for (int line = startLine; line <= endLine; line++)
        {
            var text = buf.GetLine(line);
            if (text.Length <= leftColumn)
            {
                lines.Add("");
                continue;
            }

            var endExclusive = Math.Min(text.Length, rightColumn + 1);
            lines.Add(text[leftColumn..endExclusive]);
        }

        _registerManager.SetYank(register, new Register(string.Join("\n", lines), RegisterType.Block));
    }

    private void ExecuteVisualIndent(bool indent, List<VimEvent> events)
    {
        if (_selection == null) { ExitVisualMode(events); return; }
        var sel = _selection.Value;
        IndentRange(sel.NormalizedStart.Line, sel.NormalizedEnd.Line, indent, events);
        ExitVisualMode(events);
    }

    private void ExecuteVisualComment(List<VimEvent> events)
    {
        if (_selection == null) { ExitVisualMode(events); return; }
        var sel = _selection.Value;
        ToggleCommentLines(sel.NormalizedStart.Line, sel.NormalizedEnd.Line, events);
        ExitVisualMode(events);
    }

    private void ExecuteVisualToggleCase(List<VimEvent> events) =>
        ExecuteVisualCaseConvert(CaseConversion.Toggle, events);

    private void ExecuteVisualCaseConvert(CaseConversion mode, List<VimEvent> events)
    {
        if (_selection == null) { ExitVisualMode(events); return; }
        Snapshot();
        var sel = _selection.Value;
        bool linewise = _mode == VimMode.VisualLine;
        ApplyCaseConversion(sel.NormalizedStart, sel.NormalizedEnd, linewise, mode, events);
        ExitVisualMode(events);
    }

    private void ExecuteVisualReplace(char replacement, List<VimEvent> events)
    {
        if (_selection == null) { ExitVisualMode(events); return; }
        Snapshot();
        var buf = _bufferManager.Current.Text;
        var sel = _selection.Value;

        if (_mode == VimMode.VisualBlock)
        {
            var (startLine, endLine, leftColumn, rightColumn) = GetBlockBounds(sel);
            for (int lineNo = startLine; lineNo <= endLine; lineNo++)
            {
                var line = buf.GetLine(lineNo);
                if (line.Length <= leftColumn) continue;
                var endCol = Math.Min(rightColumn, line.Length - 1);
                for (int col = leftColumn; col <= endCol; col++)
                {
                    buf.DeleteChar(lineNo, col);
                    buf.InsertChar(lineNo, col, replacement);
                }
            }
        }
        else
        {
            var start = sel.NormalizedStart;
            var end = sel.NormalizedEnd;

            for (int lineNo = start.Line; lineNo <= end.Line; lineNo++)
            {
                var line = buf.GetLine(lineNo);
                if (line.Length == 0) continue;

                var startCol = lineNo == start.Line ? start.Column : 0;
                var endCol = lineNo == end.Line ? end.Column : line.Length - 1;
                startCol = Math.Clamp(startCol, 0, Math.Max(0, line.Length - 1));
                endCol = Math.Clamp(endCol, startCol, Math.Max(0, line.Length - 1));

                for (int col = startCol; col <= endCol; col++)
                {
                    buf.DeleteChar(lineNo, col);
                    buf.InsertChar(lineNo, col, replacement);
                }
            }
        }

        EmitText(events);
        ExitVisualMode(events);
    }

    private void DeleteWordBack(List<VimEvent> events)
    {
        var buf = _bufferManager.Current.Text;
        if (_cursor.Column == 0 && _cursor.Line > 0)
        {
            var prevLen = buf.GetLineLength(_cursor.Line - 1);
            buf.JoinLines(_cursor.Line - 1);
            _cursor = new CursorPosition(_cursor.Line - 1, prevLen);
        }
        else
        {
            var line = buf.GetLine(_cursor.Line);
            int col = _cursor.Column - 1;
            while (col > 0 && char.IsWhiteSpace(line[col - 1])) col--;
            while (col > 0 && !char.IsWhiteSpace(line[col - 1])) col--;
            buf.DeleteRange(_cursor.Line, col, _cursor.Column);
            _cursor = _cursor with { Column = col };
        }
        EmitText(events);
    }

    private void DeleteLineBack(List<VimEvent> events)
    {
        var buf = _bufferManager.Current.Text;
        buf.DeleteRange(_cursor.Line, 0, _cursor.Column);
        _cursor = _cursor with { Column = 0 };
        EmitText(events);
    }

    private void DeleteCharBack(List<VimEvent> events)
    {
        var buf = _bufferManager.Current.Text;
        if (_cursor.Column > 0)
        {
            buf.DeleteChar(_cursor.Line, _cursor.Column - 1);
            _cursor = _cursor with { Column = _cursor.Column - 1 };
        }
        else if (_cursor.Line > 0)
        {
            var prevLen = buf.GetLineLength(_cursor.Line - 1);
            buf.JoinLines(_cursor.Line - 1);
            _cursor = new CursorPosition(_cursor.Line - 1, prevLen);
        }
        EmitText(events);
    }

    private static char? GetAutoPairClose(char open) => open switch
    {
        '(' => ')',
        '[' => ']',
        '{' => '}',
        '"' => '"',
        '\'' => '\'',
        '`' => '`',
        _ => null
    };

    private void InsertNewline(List<VimEvent> events)
    {
        var buf = _bufferManager.Current.Text;
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
    }

    // ─────────────── Event helpers ───────────────
    private void EmitCursor(List<VimEvent> events) => events.Add(VimEvent.CursorMoved(_cursor));
    private void EmitText(List<VimEvent> events) { _syntaxEngine.Invalidate(); events.Add(VimEvent.TextChanged()); events.Add(VimEvent.CursorMoved(_cursor)); }
    private void EmitStatus(List<VimEvent> events, string msg) { _statusMsg = msg; events.Add(VimEvent.StatusMessage(msg)); }
    private void EmitCmdLine(List<VimEvent> events)
    {
        var prefix = _mode switch { VimMode.Command => ":", VimMode.SearchForward => "/", VimMode.SearchBackward => "?", _ => "" };
        events.Add(VimEvent.CommandLineChanged(prefix + _cmdLine));
    }

    // Expose motion helpers for MotionEngine
    private MotionEngine GetMotion() => new(_bufferManager.Current.Text);

    // ─────────────── :normal implementation ───────────────

    /// <summary>
    /// Handles :[range]normal[!] {commands} — executes normal-mode key sequences on each line in range.
    /// Returns true if the command line was a :normal command (consumed), false otherwise.
    /// </summary>
    private bool TryExecuteNormalCmd(string cmdLine, List<VimEvent> events)
    {
        var trimmed = cmdLine.Trim();

        // Parse optional range prefix (%, ., number, number,number)
        string range = "";
        int pos = 0;
        if (trimmed.StartsWith('%')) { range = "%"; pos = 1; }
        else if (trimmed.StartsWith('.')) { range = "."; pos = 1; }
        else if (pos < trimmed.Length && char.IsDigit(trimmed[pos]))
        {
            while (pos < trimmed.Length && (char.IsDigit(trimmed[pos]) || trimmed[pos] == ','))
                pos++;
            range = trimmed[..pos];
        }

        var rest = trimmed[pos..].TrimStart();

        // Detect "normal" or "norm" keyword
        int keywordLen;
        if (rest.StartsWith("normal", StringComparison.OrdinalIgnoreCase) &&
            (rest.Length == 6 || rest[6] is ' ' or '!'))
            keywordLen = 6;
        else if (rest.StartsWith("norm", StringComparison.OrdinalIgnoreCase) &&
                 (rest.Length == 4 || rest[4] is ' ' or '!'))
            keywordLen = 4;
        else
            return false;

        rest = rest[keywordLen..];
        bool ignoreMapping = rest.StartsWith('!');
        if (ignoreMapping) rest = rest[1..];

        // Require a space followed by at least one command character
        if (rest.Length == 0 || rest[0] != ' ') return false;
        var commands = rest[1..];
        if (string.IsNullOrEmpty(commands)) return false;

        var strokes = ParseNormalKeySequence(commands);

        // Resolve line range
        var buf = CurrentBuffer.Text;
        int startLine = _cursor.Line, endLine = _cursor.Line;
        ExCommandProcessor.ResolveRange(range, _cursor, buf.LineCount, ref startLine, ref endLine);
        startLine = Math.Clamp(startLine, 0, buf.LineCount - 1);
        endLine = Math.Clamp(endLine, 0, buf.LineCount - 1);

        // Snapshot before mutations for undo; suppress inner Snapshot() calls so the
        // entire :normal range executes as a single undo record.
        var preLines = buf.Snapshot();
        var preCursor = _cursor;

        _mode = VimMode.Normal;
        _commandParser.Reset();
        _suppressSnapshot = true;
        var innerEvents = new List<VimEvent>();
        bool anyChange = false;

        try
        {
            for (int l = startLine; l <= endLine; l++)
            {
                if (l >= CurrentBuffer.Text.LineCount) break;
                _cursor = new CursorPosition(l, 0);

                foreach (var stroke in strokes)
                {
                    innerEvents.Clear();
                    ProcessStroke(stroke, innerEvents, allowMapping: !ignoreMapping);
                    if (!anyChange)
                        foreach (var ev in innerEvents)
                            if (ev.Type == VimEventType.TextChanged) { anyChange = true; break; }
                }

                // Implicitly exit Insert/Visual mode after command sequence (like Vim does)
                if (_mode != VimMode.Normal)
                {
                    innerEvents.Clear();
                    ProcessKeyInternal("Escape", false, false, false, innerEvents);
                }
            }
        }
        finally
        {
            _suppressSnapshot = false;
        }

        if (anyChange)
        {
            CurrentBuffer.Undo.Snapshot(preLines, preCursor);
            EmitText(events);
        }
        else
        {
            EmitCursor(events);
        }

        return true;
    }

    private static List<VimKeyStroke> ParseNormalKeySequence(string seq)
    {
        var strokes = new List<VimKeyStroke>();
        int i = 0;
        while (i < seq.Length)
        {
            if (seq[i] == '<')
            {
                var close = seq.IndexOf('>', i + 1);
                if (close > i)
                {
                    var keyName = seq[(i + 1)..close];
                    strokes.Add(new VimKeyStroke(MapNormalSpecialKey(keyName), false, false, false));
                    i = close + 1;
                    continue;
                }
            }
            strokes.Add(new VimKeyStroke(seq[i].ToString(), false, false, false));
            i++;
        }
        return strokes;
    }

    private static string MapNormalSpecialKey(string name) => name.ToUpperInvariant() switch
    {
        "ESC" or "ESCAPE" => "Escape",
        "CR" or "ENTER" or "RETURN" => "Return",
        "BS" or "BACKSPACE" => "Back",
        "TAB" => "Tab",
        "SPACE" => " ",
        "DEL" or "DELETE" => "Delete",
        _ => name
    };
}

// Extension for MotionEngine public access
public static class MotionEngineExtensions
{
    public static CursorPosition WordForward(this MotionEngine me, CursorPosition cursor, int count, bool WORD)
    {
        var mot = me.Calculate(WORD ? "W" : "w", cursor, count);
        return mot?.Target ?? cursor;
    }

    public static CursorPosition WordBackward(this MotionEngine me, CursorPosition cursor, int count, bool WORD)
    {
        var mot = me.Calculate(WORD ? "B" : "b", cursor, count);
        return mot?.Target ?? cursor;
    }

    public static CursorPosition WordEnd(this MotionEngine me, CursorPosition cursor, int count, bool WORD)
    {
        var mot = me.Calculate(WORD ? "E" : "e", cursor, count);
        return mot?.Target ?? cursor;
    }
}
