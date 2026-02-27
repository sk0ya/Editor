using WVim.Core.Buffer;
using WVim.Core.Config;
using WVim.Core.Macros;
using WVim.Core.Marks;
using WVim.Core.Models;
using WVim.Core.Registers;
using WVim.Core.Syntax;

namespace WVim.Core.Engine;

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

    private VimMode _mode = VimMode.Normal;
    private CursorPosition _cursor = CursorPosition.Zero;
    private Selection? _selection;
    private CursorPosition _visualStart;

    private string _cmdLine = "";   // For : / ? modes
    private string _statusMsg = "";
    private string _searchPattern = "";
    private bool _searchForward = true;

    private char? _pendingReplaceChar;
    private bool _awaitingMark;
    private bool _awaitingMarkJump;
    private bool _awaitingMarkJumpLine;
    private int _preferredColumn = 0; // Sticky column for j/k

    private CursorPosition _insertStart;

    public VimMode Mode => _mode;
    public CursorPosition Cursor => _cursor;
    public Selection? Selection => _selection;
    public string CommandLine => _cmdLine;
    public string SearchPattern => _searchPattern;
    public string StatusMessage => _statusMsg;
    public VimOptions Options => _config.Options;
    public VimBuffer CurrentBuffer => _bufferManager.Current;
    public BufferManager BufferManager => _bufferManager;
    public SyntaxEngine Syntax => _syntaxEngine;

    public VimEngine(VimConfig? config = null)
    {
        _config = config ?? new VimConfig();
        _bufferManager = new BufferManager();
        _registerManager = new RegisterManager();
        _markManager = new MarkManager();
        _macroManager = new MacroManager();
        _syntaxEngine = new SyntaxEngine();
        _commandParser = new CommandParser();
        _exProcessor = new ExCommandProcessor(_bufferManager, _config.Options);
    }

    public void SetClipboardProvider(IClipboardProvider provider)
    {
        _registerManager.SetClipboardProvider(provider);
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

    // Process a key stroke and return events to update the UI
    public IReadOnlyList<VimEvent> ProcessKey(string key, bool ctrl = false, bool shift = false, bool alt = false)
    {
        var events = new List<VimEvent>();
        var stroke = new VimKeyStroke(key, ctrl, shift, alt);

        // Macro recording
        if (_macroManager.IsRecording && !(key == "q" && !ctrl && !shift))
            _macroManager.RecordKey(stroke);

        ProcessKeyInternal(key, ctrl, shift, alt, events);
        return events;
    }

    private void ProcessKeyInternal(string key, bool ctrl, bool shift, bool alt, List<VimEvent> events)
    {
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

        // Awaiting pending chars
        if (_awaitingMark) { _markManager.SetMark(key[0], _cursor); _awaitingMark = false; EmitCursor(events); return; }
        if (_awaitingMarkJump) { var m = _markManager.GetMark(key[0]); if (m.HasValue) MoveCursor(m.Value, events); _awaitingMarkJump = false; return; }
        if (_awaitingMarkJumpLine) { var m = _markManager.GetMark(key[0]); if (m.HasValue) MoveCursor(m.Value with { Column = 0 }, events); _awaitingMarkJumpLine = false; return; }
        if (_pendingReplaceChar.HasValue) { ExecuteReplace(key[0], events); _pendingReplaceChar = null; return; }

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
                case "d": Snapshot(); DeleteLines(_cursor.Line, endLine, events); return;
                case "c": Snapshot(); DeleteLines(_cursor.Line, endLine, events); EnterInsertMode(false, events); return;
                case "y": YankLines(_cursor.Line, endLine, cmd.Register ?? '"', events); return;
                case ">": IndentRange(_cursor.Line, endLine, true, events); return;
                case "<": IndentRange(_cursor.Line, endLine, false, events); return;
                case "=": AutoIndentRange(_cursor.Line, endLine, events); return;
            }
        }

        switch (cmd.Motion)
        {
            // Mode transitions
            case "i": EnterInsertMode(false, events); break;
            case "I": _cursor = motion.FindChar(_cursor, ' ', false, false); GoToLineStart(events); EnterInsertMode(false, events); break;
            case "a": _cursor = motion.MoveRight(_cursor, 1, true); EnterInsertMode(false, events); break;
            case "A": GoToLineEnd(true, events); EnterInsertMode(false, events); break;
            case "o": OpenLineBelow(events); EnterInsertMode(false, events); break;
            case "O": OpenLineAbove(events); EnterInsertMode(false, events); break;
            case "R": EnterReplaceMode(events); break;
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
            case "gg": MoveCursor(new CursorPosition(0, 0), events); break;
            case "G":
                var lastLine = count == 1 ? buf.LineCount - 1 : count - 1;
                MoveCursor(new CursorPosition(Math.Clamp(lastLine, 0, buf.LineCount - 1), 0), events);
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
            case "zz": EmitStatus(events, "zz"); break;

            // Editing
            case "x":
                // Delete [count] chars from cursor position
                var xEnd = _cursor with { Column = Math.Min(_cursor.Column + count - 1, Math.Max(0, buf.GetLineLength(_cursor.Line) - 1)) };
                ExecuteDelete(_cursor, xEnd, false, events);
                break;
            case "X":
                // Delete [count] chars before cursor
                var xStart = motion.MoveLeft(_cursor, count);
                if (xStart.Column < _cursor.Column) ExecuteDelete(xStart, _cursor with { Column = _cursor.Column - 1 }, false, events);
                break;
            case "s": ExecuteDelete(_cursor, motion.MoveRight(_cursor, count), false, events); EnterInsertMode(false, events); break;
            case "S": DeleteLines(_cursor.Line, _cursor.Line, events); EnterInsertMode(false, events); break;
            case "D": var eol = GetLineLength() - 1; if (eol >= _cursor.Column) ExecuteDelete(_cursor, _cursor with { Column = eol }, false, events); break;
            case "C": var ceol = GetLineLength() - 1; if (ceol >= _cursor.Column) ExecuteDelete(_cursor, _cursor with { Column = ceol }, false, events); EnterInsertMode(false, events); break;
            case "Y": YankLines(_cursor.Line, _cursor.Line + count - 1, cmd.Register ?? '"', events); break;
            case "p": PasteAfter(cmd.Register ?? '"', events); break;
            case "P": PasteBefore(cmd.Register ?? '"', events); break;
            case "u": ExecuteUndo(events); break;
            case ".": RepeatLastChange(events); break;
            case "J": JoinLines(count, events); break;
            case "~": ToggleCase(count, events); break;
            case ">>": IndentLine(true, count, events); break;
            case "<<": IndentLine(false, count, events); break;

            // r: replace char (needs next input)
            case var r when r?.StartsWith('r') == true && r.Length == 2:
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
        if (cmd.Operator == null) return;
        var buf = _bufferManager.Current.Text;
        var motion = new MotionEngine(buf);

        // f/F/t/T find
        if (cmd.Motion is "f" or "F" or "t" or "T" && cmd.FindChar.HasValue)
        {
            bool fwd = cmd.Motion is "f" or "t";
            bool before = cmd.Motion is "t" or "T";
            var found = motion.FindChar(_cursor, cmd.FindChar.Value, fwd, before, cmd.Count);
            if (cmd.Operator != null)
                ExecuteOperator(cmd.Operator, _cursor, found, cmd.Register ?? '"', false, events);
            else
                MoveCursor(found, events);
            return;
        }

        // Calculate motion target
        var mot = motion.Calculate(cmd.Motion, _cursor, cmd.Count);
        if (mot == null) return;

        bool linewise = mot.Value.Type == MotionType.Linewise || cmd.LinewiseForced;

        if (cmd.Operator != null)
            ExecuteOperator(cmd.Operator, _cursor, mot.Value.Target, cmd.Register ?? '"', linewise, events);
        else
            MoveCursor(mot.Value.Target, events);
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
                else { YankRange(register, start, end, false); ExecuteDelete(start, end, false, events); EnterInsertMode(false, events); }
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
        }
    }

    // ─────────────── INSERT MODE ───────────────
    private void HandleInsert(string key, bool ctrl, bool shift, bool alt, List<VimEvent> events)
    {
        var buf = _bufferManager.Current.Text;

        if (ctrl)
        {
            switch (key.ToLower())
            {
                case "[": // Ctrl+[
                case "escape" when !ctrl:
                    ExitInsertMode(events);
                    return;
                case "w": DeleteWordBack(events); return;
                case "u": DeleteLineBack(events); return;
                case "h": DeleteCharBack(events); return;
                case "j": InsertNewline(events); return;
                case "m": InsertNewline(events); return;
            }
        }

        switch (key)
        {
            case "Escape":
                ExitInsertMode(events);
                break;
            case "Back":
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
        var buf = _bufferManager.Current.Text;

        if (key == "Escape") { ExitVisualMode(events); return; }

        if (ctrl)
        {
            HandleNormalCtrl(key, events);
            UpdateSelection(events);
            return;
        }

        // Let most normal mode motions work in visual mode
        switch (key)
        {
            case "h": _cursor = new MotionEngine(buf).MoveLeft(_cursor); break;
            case "l": _cursor = new MotionEngine(buf).MoveRight(_cursor); break;
            case "j": _cursor = new MotionEngine(buf).MoveDown(_cursor); break;
            case "k": _cursor = new MotionEngine(buf).MoveUp(_cursor); break;
            case "w": _cursor = new MotionEngine(buf).WordForward(_cursor, 1, false); break;
            case "b": _cursor = new MotionEngine(buf).WordBackward(_cursor, 1, false); break;
            case "e": _cursor = new MotionEngine(buf).WordEnd(_cursor, 1, false); break;
            case "0": _cursor = _cursor with { Column = 0 }; break;
            case "$": _cursor = _cursor with { Column = Math.Max(0, buf.GetLineLength(_cursor.Line) - 1) }; break;
            case "gg": case "g" when key == "gg": _cursor = new CursorPosition(0, 0); break;
            case "G": _cursor = new CursorPosition(buf.LineCount - 1, 0); break;
            // Switch visual modes
            case "v": if (_mode == VimMode.Visual) ExitVisualMode(events); else EnterVisualMode(VimMode.Visual, events); return;
            case "V": if (_mode == VimMode.VisualLine) ExitVisualMode(events); else EnterVisualMode(VimMode.VisualLine, events); return;
            // Operators on selection
            case "d": ExecuteVisualDelete('"', events); return;
            case "y": ExecuteVisualYank('"', events); return;
            case "c": ExecuteVisualDelete('"', events); EnterInsertMode(false, events); return;
            case ">": ExecuteVisualIndent(true, events); return;
            case "<": ExecuteVisualIndent(false, events); return;
            case "~": ExecuteVisualToggleCase(events); return;
        }

        UpdateSelection(events);
    }

    // ─────────────── COMMAND LINE MODE ───────────────
    private void HandleCommandLine(string key, bool ctrl, bool shift, bool alt, List<VimEvent> events)
    {
        switch (key)
        {
            case "Escape":
                _cmdLine = "";
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
                        ChangeMode(VimMode.Normal, events);
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
                    if (_mode == VimMode.SearchForward || _mode == VimMode.SearchBackward)
                    {
                        // Incremental search
                        if (_config.Options.IncrSearch)
                            DoIncrSearch(events);
                    }
                }
                EmitCmdLine(events);
                break;
        }
    }

    private void ExecuteCommandLine(List<VimEvent> events)
    {
        if (_mode == VimMode.Command)
        {
            var result = _exProcessor.Execute(_cmdLine, _cursor);
            _cmdLine = "";
            ChangeMode(VimMode.Normal, events);
            if (!result.Success)
                EmitStatus(events, "E: " + result.Message);
            else if (result.Message != null)
                EmitStatus(events, result.Message);
            if (result.Event != null)
                events.Add(result.Event);
        }
        else // Search
        {
            _searchPattern = _cmdLine;
            _searchForward = _mode == VimMode.SearchForward;
            _cmdLine = "";
            ChangeMode(VimMode.Normal, events);
            DoSearch(_searchForward, events);
        }
        EmitCmdLine(events);
    }

    private void DoSearch(bool forward, List<VimEvent> events)
    {
        if (string.IsNullOrEmpty(_searchPattern)) return;
        var buf = _bufferManager.Current.Text;
        var ignoreCase = _config.Options.SmartCase
            ? !_searchPattern.Any(char.IsUpper)
            : _config.Options.IgnoreCase;

        var found = buf.FindNext(_searchPattern, _cursor, forward, ignoreCase);
        if (found.HasValue)
        {
            _markManager.AddJump(_cursor);
            MoveCursor(found.Value, events);
            var all = buf.FindAll(_searchPattern, ignoreCase);
            events.Add(VimEvent.SearchChanged(_searchPattern, all.Count));
        }
        else
        {
            EmitStatus(events, $"Pattern not found: {_searchPattern}");
        }
    }

    private void DoIncrSearch(List<VimEvent> events)
    {
        if (string.IsNullOrEmpty(_cmdLine)) return;
        var buf = _bufferManager.Current.Text;
        var ignoreCase = _config.Options.SmartCase
            ? !_cmdLine.Any(char.IsUpper)
            : _config.Options.IgnoreCase;
        var found = buf.FindNext(_cmdLine, _cursor, _mode == VimMode.SearchForward, ignoreCase);
        if (found.HasValue) MoveCursor(found.Value, events);
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

    private void EnterSearchMode(bool forward, List<VimEvent> events)
    {
        _cmdLine = "";
        _searchForward = forward;
        ChangeMode(forward ? VimMode.SearchForward : VimMode.SearchBackward, events);
        EmitCmdLine(events);
    }

    private void ChangeMode(VimMode newMode, List<VimEvent> events)
    {
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

    private void MoveVertical(int delta, List<VimEvent> events)
    {
        var buf = _bufferManager.Current.Text;
        var newLine = Math.Clamp(_cursor.Line + delta, 0, buf.LineCount - 1);
        var maxCol = Math.Max(0, buf.GetLineLength(newLine) - 1);
        var col = Math.Min(_preferredColumn, maxCol);
        _cursor = new CursorPosition(newLine, col);
        EmitCursor(events);
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

    private int GetFirstNonBlank()
    {
        var line = _bufferManager.Current.Text.GetLine(_cursor.Line);
        int col = 0;
        while (col < line.Length && char.IsWhiteSpace(line[col])) col++;
        return col;
    }

    private int GetLineLength() => _bufferManager.Current.Text.GetLineLength(_cursor.Line);

    private void OpenLineBelow(List<VimEvent> events)
    {
        Snapshot();
        var buf = _bufferManager.Current.Text;
        var indent = GetAutoIndent(buf, _cursor.Line);
        buf.InsertLines(_cursor.Line, [indent]);
        _cursor = new CursorPosition(_cursor.Line + 1, indent.Length);
        EmitText(events);
    }

    private void OpenLineAbove(List<VimEvent> events)
    {
        Snapshot();
        var buf = _bufferManager.Current.Text;
        var indent = GetAutoIndent(buf, _cursor.Line);
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

    private void ExecuteUndo(List<VimEvent> events)
    {
        var vbuf = _bufferManager.Current;
        var state = vbuf.Undo.Undo(vbuf.Text, _cursor);
        if (state != null)
        {
            _cursor = vbuf.Text.ClampCursor(state.Cursor);
            EmitText(events);
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
            EmitText(events);
            EmitStatus(events, "1 change redone");
        }
        else EmitStatus(events, "Already at newest change");
    }

    private void RepeatLastChange(List<VimEvent> events)
    {
        // Minimal implementation: replay last command string
        EmitStatus(events, ".");
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
        Snapshot();
        var sel = _selection.Value;
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
        var start = sel.NormalizedStart;
        var end = sel.NormalizedEnd;

        if (_mode == VimMode.VisualLine)
            YankLines(start.Line, end.Line, register, events);
        else
            YankRange(register, start, end, false);

        MoveCursor(start, events);
        ExitVisualMode(events);
    }

    private void ExecuteVisualIndent(bool indent, List<VimEvent> events)
    {
        if (_selection == null) { ExitVisualMode(events); return; }
        var sel = _selection.Value;
        IndentRange(sel.NormalizedStart.Line, sel.NormalizedEnd.Line, indent, events);
        ExitVisualMode(events);
    }

    private void ExecuteVisualToggleCase(List<VimEvent> events)
    {
        if (_selection == null) { ExitVisualMode(events); return; }
        Snapshot();
        var buf = _bufferManager.Current.Text;
        var sel = _selection.Value;
        var start = sel.NormalizedStart;
        var end = sel.NormalizedEnd;

        for (int l = start.Line; l <= end.Line; l++)
        {
            var line = buf.GetLine(l);
            var lineStart = l == start.Line ? start.Column : 0;
            var lineEnd = l == end.Line ? end.Column : line.Length - 1;
            for (int c = lineStart; c <= lineEnd && c < line.Length; c++)
            {
                var ch = line[c];
                var toggled = char.IsUpper(ch) ? char.ToLower(ch) : char.ToUpper(ch);
                buf.DeleteChar(l, c);
                buf.InsertChar(l, c, toggled);
                line = buf.GetLine(l); // refresh
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

    private void InsertNewline(List<VimEvent> events)
    {
        var buf = _bufferManager.Current.Text;
        var indent = _config.Options.AutoIndent ? GetAutoIndent(buf, _cursor.Line) : "";
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
        var vbuf = _bufferManager.Current;
        vbuf.Undo.Snapshot(vbuf.Text, _cursor);
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
