using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Editor.Core.Buffer;
using Editor.Core.Config;
using Editor.Core.Formatting;
using Editor.Core.Lsp;
using Editor.Core.Marks;
using Editor.Core.Models;
using Editor.Core;
using Editor.Core.Registers;
using Editor.Core.Extensibility;

namespace Editor.Core.Engine;

public record ExResult(
    bool Success,
    string? Message = null,
    VimEvent? Event = null,
    bool TextModified = false,
    CursorPosition? RestoredCursor = null,
    bool BufferRestored = false);

public class ExCommandProcessor
{
    private readonly BufferManager _bufferManager;
    private readonly VimOptions _options;
    private readonly MarkManager _markManager;
    private readonly Dictionary<string, string> _abbreviations;
    private readonly RegisterManager? _registerManager;
    private readonly Dictionary<string, string> _normalMaps;
    private readonly Dictionary<string, string> _insertMaps;
    private readonly Dictionary<string, string> _visualMaps;
    private readonly IReadOnlyList<string> _scriptNames;
    private readonly ExCommands.LspCommands _lspCommands;
    private readonly ExCommands.FileOpsCommands _fileOpsCommands;
    private readonly ExCommands.RangeResolver _rangeResolver;
    private readonly ExCommands.SubstituteCommands _substituteCommands;
    private readonly ExCommands.ScriptingCommands _scriptingCommands;
    private readonly ExCommands.RegisterMarkCommands _registerMarkCommands;
    private readonly List<string> _history = [];
    private int _historyIndex = -1;
    private readonly List<string> _searchHistory = [];
    private int _searchHistoryIndex = -1;
    private int _suppressHistory;
    private readonly EditorCommandRegistry _commandRegistry;
    private readonly IServiceProvider? _services;

    public ExCommandProcessor(BufferManager bufferManager, VimOptions options, MarkManager markManager,
        Dictionary<string, string>? abbreviations = null, RegisterManager? registerManager = null,
        Dictionary<string, string>? normalMaps = null, Dictionary<string, string>? insertMaps = null,
        Dictionary<string, string>? visualMaps = null, Dictionary<string, string>? variables = null,
        IReadOnlyList<string>? scriptNames = null,
        Dictionary<string, VimFunctionDefinition>? functions = null,
        LspServerRegistry? lspRegistry = null, EditorCommandRegistry? commandRegistry = null,
        IServiceProvider? services = null)
    {
        _bufferManager = bufferManager;
        _options = options;
        _markManager = markManager;
        _abbreviations = abbreviations ?? [];
        _registerManager = registerManager;
        _normalMaps = normalMaps ?? [];
        _insertMaps = insertMaps ?? [];
        _visualMaps = visualMaps ?? [];
        _scriptNames = scriptNames ?? [];
        _lspCommands = new ExCommands.LspCommands(lspRegistry ?? LspServerRegistry.Default);
        _fileOpsCommands = new ExCommands.FileOpsCommands(bufferManager, options);
        _rangeResolver = new ExCommands.RangeResolver(markManager);
        _substituteCommands = new ExCommands.SubstituteCommands(bufferManager, options, _rangeResolver,
            () => _searchHistory.Count > 0 ? _searchHistory[0] : "", AddSearchHistory);
        _scriptingCommands = new ExCommands.ScriptingCommands(bufferManager,
            variables ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            functions ?? new Dictionary<string, VimFunctionDefinition>(StringComparer.OrdinalIgnoreCase),
            Execute, ExecuteNoHistory);
        _registerMarkCommands = new ExCommands.RegisterMarkCommands(bufferManager, markManager, registerManager, _rangeResolver);
        _commandRegistry = commandRegistry ?? EditorCommandRegistry.Default;
        _services = services;
    }

    public string? LastCommand => _history.Count > 0 ? _history[0] : null;
    public IReadOnlyList<string> CommandHistory => _history.AsReadOnly();
    public IReadOnlyList<string> SearchHistory  => _searchHistory.AsReadOnly();
    public EditorCommandRegistry CommandRegistry => _commandRegistry;

    /// <summary>
    /// Executes a host-registered command, including asynchronous registrations, from a raw Ex line.
    /// Range and command parsing is identical to <see cref="Execute"/>; built-in or unknown commands return null.
    /// </summary>
    public ValueTask<EditorCommandResult?> ExecuteExtensionAsync(string rawCommand, CursorPosition cursor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rawCommand);
        var (command, range) = ParseExtensionCommand(rawCommand);
        if (command.Length == 0) return ValueTask.FromResult<EditorCommandResult?>(null);
        return _commandRegistry.ExecuteParsedAsync(command, rawCommand, range, cursor, _services, cancellationToken);
    }

    public IReadOnlyDictionary<int, string> GetSubstitutePreview(string cmdLine, CursorPosition cursor)
    {
        if (!_options.IncCommand)
            return new Dictionary<int, string>();

        var cmd = cmdLine.Trim();
        if (string.IsNullOrEmpty(cmd))
            return new Dictionary<int, string>();

        TryScanRangePrefix(cmd, out var range, out var cmdStart);

        cmd = cmd[cmdStart..].Trim();
        if (cmd.Length < 3 || cmd[0] != 's' || cmd[1] is not ('/' or '!'))
            return new Dictionary<int, string>();

        char sep = cmd[1];
        var parts = cmd[2..].Split(sep);
        if (parts.Length < 2)
            return new Dictionary<int, string>();

        string pattern = parts[0];
        if (string.IsNullOrEmpty(pattern))
            pattern = _searchHistory.Count > 0 ? _searchHistory[0] : "";
        if (string.IsNullOrEmpty(pattern))
            return new Dictionary<int, string>();
        string replacement = parts.Length > 1 ? parts[1] : "";
        string flags = parts.Length > 2 ? parts[2] : "";

        bool global = flags.Contains('g');
        bool ignoreCase = flags.Contains('i') || (!flags.Contains('I') && _options.IgnoreCase);
        var regex = TryBuildRegex(pattern, ignoreCase, out _);
        if (regex == null)
            return new Dictionary<int, string>();

        var buf = _bufferManager.Current.Text;
        int startLine = 0, endLine = buf.LineCount - 1;
        ResolveRange(range, cursor, buf.LineCount, ref startLine, ref endLine);
        startLine = Math.Clamp(startLine, 0, Math.Max(0, buf.LineCount - 1));
        endLine = Math.Clamp(endLine, startLine, Math.Max(0, buf.LineCount - 1));

        var preview = new Dictionary<int, string>();
        for (int l = startLine; l <= endLine && l < buf.LineCount; l++)
        {
            var line = buf.GetLine(l);
            var newLine = global
                ? regex.Replace(line, replacement)
                : regex.Replace(line, replacement, 1);
            if (newLine != line)
                preview[l] = newLine;
        }

        return preview;
    }

    public void LoadHistory(IReadOnlyList<string> cmdHistory, IReadOnlyList<string> searchHistory)
    {
        _history.Clear();
        _history.AddRange(cmdHistory.Take(_options.History));
        _historyIndex = -1;

        _searchHistory.Clear();
        _searchHistory.AddRange(searchHistory.Take(_options.History));
        _searchHistoryIndex = -1;
    }

    // ── shared history helpers ──────────────────────────────────────────────

    private void AddToHistory(List<string> list, ref int index, string entry)
    {
        if (!string.IsNullOrWhiteSpace(entry))
        {
            list.Remove(entry);
            list.Insert(0, entry);
            if (list.Count > _options.History)
                list.RemoveAt(list.Count - 1);
        }
        index = -1;
    }

    private static string? HistoryNavigatePrev(List<string> list, ref int index)
    {
        if (list.Count == 0) return null;
        index = Math.Min(index + 1, list.Count - 1);
        return list[index];
    }

    private static string? HistoryNavigateNext(List<string> list, ref int index)
    {
        if (index <= 0) { index = -1; return ""; }
        return list[--index];
    }

    private static bool TryParseTerminalNumber(string text, out int terminalNumber)
    {
        terminalNumber = 0;
        if (string.IsNullOrEmpty(text) || text.Any(ch => ch < '0' || ch > '9'))
            return false;

        return int.TryParse(text, out terminalNumber) && terminalNumber > 0;
    }

    // ── command history ─────────────────────────────────────────────────────

    public void AddHistory(string cmd) => AddToHistory(_history, ref _historyIndex, cmd);
    public void ResetHistoryIndex() => _historyIndex = -1;
    public string? HistoryPrev() => HistoryNavigatePrev(_history, ref _historyIndex);
    public string? HistoryNext() => HistoryNavigateNext(_history, ref _historyIndex);

    // ── search history ──────────────────────────────────────────────────────

    public void AddSearchHistory(string pattern) => AddToHistory(_searchHistory, ref _searchHistoryIndex, pattern);
    public void ResetSearchHistoryIndex() => _searchHistoryIndex = -1;
    public string? SearchHistoryPrev() => HistoryNavigatePrev(_searchHistory, ref _searchHistoryIndex);
    public string? SearchHistoryNext() => HistoryNavigateNext(_searchHistory, ref _searchHistoryIndex);

    public ExResult Execute(string cmdLine, CursorPosition cursor)
    {
        var rawCommand = cmdLine;
        if (_suppressHistory == 0)
            AddHistory(cmdLine);
        var cmd = cmdLine.Trim();
        if (string.IsNullOrEmpty(cmd)) return new ExResult(true);

        // Range prefix: %, number, ., or a mark reference ('x), and combinations
        // thereof separated by a comma (e.g. '<,'>  'a,'b  'a,5  .,'b).
        var parsed = ParseExtensionCommand(cmdLine);
        cmd = parsed.Command;
        var range = parsed.Range;

        // :q/:quit/:wq/:w/:write/:e/:edit — see ExCommands/FileOpsCommands.cs
        if (_fileOpsCommands.TryHandle(cmd, out var fileOpsResult))
            return fileOpsResult;

        // :set
        if (cmd.StartsWith("set ") || cmd == "set")
        {
            if (cmd == "set") return new ExResult(true, "Options: (use :set option)");
            var opt = cmd[4..].Trim();
            var err = _options.Apply(opt);
            // Sync fileformat/fileencoding options to the current buffer so Save() uses them.
            _bufferManager.Current.FileFormat   = _options.FileFormat;
            _bufferManager.Current.FileEncoding = _options.FileEncoding;
            return err == null ? new ExResult(true) : new ExResult(false, err);
        }

        // :colorscheme
        if (cmd.StartsWith("colorscheme ") || cmd.StartsWith("colo "))
        {
            var parts = cmd.Split(' ', 2);
            if (parts.Length > 1) _options.ColorScheme = parts[1].Trim();
            return new ExResult(true, $"colorscheme: {_options.ColorScheme}");
        }

        // :syntax
        if (cmd.StartsWith("syntax "))
        {
            _options.Syntax = cmd[7..].Trim().Equals("on", StringComparison.OrdinalIgnoreCase);
            return new ExResult(true);
        }

        // Buffer commands
        if (cmd == "bn" || cmd == "bnext") { _bufferManager.GoToNext(); return new ExResult(true); }
        if (cmd == "bp" || cmd == "bprev") { _bufferManager.GoToPrev(); return new ExResult(true); }
        if (cmd.StartsWith("b ") && int.TryParse(cmd[2..].Trim(), out var bNum))
        {
            _bufferManager.GoTo(bNum - 1);
            return new ExResult(true);
        }
        if (cmd == "bd" || cmd == "bdelete")
        {
            _bufferManager.CloseBuffer();
            return new ExResult(true);
        }

        // :nmap :imap :vmap :nnoremap :inoremap :vnoremap (with args — silently accept, already handled by VimConfig)
        if (cmd.StartsWith("nmap ") || cmd.StartsWith("nnoremap ") ||
            cmd.StartsWith("imap ") || cmd.StartsWith("inoremap ") ||
            cmd.StartsWith("vmap ") || cmd.StartsWith("vnoremap "))
        {
            return new ExResult(true, "Key mapping registered");
        }

        // :map/:nmap/:imap/:vmap with no args — list mappings (skip empty sections)
        if (cmd == "map")
        {
            var sections = new[] { ("n", _normalMaps), ("i", _insertMaps), ("v", _visualMaps) };
            var parts = sections.Where(s => s.Item2.Count > 0).Select(s => BuildMapListing(s.Item1, s.Item2));
            return new ExResult(true, string.Join('\n', parts) is { Length: > 0 } msg ? msg : "(no mappings)");
        }
        if (cmd == "nmap") return new ExResult(true, BuildMapListing("n", _normalMaps));
        if (cmd == "imap") return new ExResult(true, BuildMapListing("i", _insertMaps));
        if (cmd == "vmap") return new ExResult(true, BuildMapListing("v", _visualMaps));

        // :unmap {lhs} — remove from all modes
        if (cmd.StartsWith("unmap "))
        {
            var lhs = GetCommandArg(cmd);
            _normalMaps.Remove(lhs);
            _insertMaps.Remove(lhs);
            _visualMaps.Remove(lhs);
            return new ExResult(true);
        }

        // :nunmap/:iunmap/:vunmap {lhs} — mode-specific removal
        if (cmd.StartsWith("nunmap ")) { _normalMaps.Remove(GetCommandArg(cmd)); return new ExResult(true); }
        if (cmd.StartsWith("iunmap ")) { _insertMaps.Remove(GetCommandArg(cmd)); return new ExResult(true); }
        if (cmd.StartsWith("vunmap ")) { _visualMaps.Remove(GetCommandArg(cmd)); return new ExResult(true); }

        // :tabnew :tabedit :tabe
        if (cmd == "tabnew" || cmd.StartsWith("tabnew ") ||
            cmd == "tabedit" || cmd.StartsWith("tabedit ") ||
            cmd == "tabe" || cmd.StartsWith("tabe "))
        {
            var path = cmd.StartsWith("tabnew ", StringComparison.Ordinal)
                ? cmd[7..].Trim()
                : cmd.StartsWith("tabedit ", StringComparison.Ordinal)
                    ? cmd[8..].Trim()
                    : cmd.StartsWith("tabe ", StringComparison.Ordinal)
                        ? cmd[5..].Trim()
                        : null;
            if (string.IsNullOrWhiteSpace(path)) path = null;
            return new ExResult(true, null, VimEvent.NewTabRequested(path));
        }
        if (cmd is "tabn" or "tabnext")
            return new ExResult(true, null, VimEvent.NextTabRequested());
        if (cmd is "tabp" or "tabprev" or "tabprevious")
            return new ExResult(true, null, VimEvent.PrevTabRequested());
        if (cmd is "tabc" or "tabclose")
            return new ExResult(true, null, VimEvent.CloseTabRequested(false));
        if (cmd is "tabc!" or "tabclose!")
            return new ExResult(true, null, VimEvent.CloseTabRequested(true));

        // :Format/:Rename/:Symbols/:CallHierarchy/:TypeHierarchy — see ExCommands/LspCommands.cs
        (int Start, int End)? formatRange = null;
        if (range.Length > 0 && cmd is "Format" or "format")
        {
            var (_, fmtStart, fmtEnd) = ResolveRangeClamped(range, cursor);
            formatRange = (fmtStart, fmtEnd);
        }
        if (_lspCommands.TryHandle(cmd, out var lspSimpleResult, formatRange))
            return lspSimpleResult;

        // :Git* commands — see ExCommands/GitCommands.cs
        if (ExCommands.GitCommands.TryHandle(cmd, out var gitResult))
            return gitResult;

        // Quickfix commands
        if (cmd is "copen" or "cope" or "clist" or "cl")
            return new ExResult(true, null, VimEvent.QuickfixOpen());
        if (cmd is "cclose" or "ccl")
            return new ExResult(true, null, VimEvent.QuickfixClose());
        if (TryParseQuickfixNav(cmd, "cn", "cnext", out var cnCount))
            return new ExResult(true, null, VimEvent.QuickfixNext(ApplyRangeCount(range, cnCount)));
        if (TryParseQuickfixNav(cmd, "cp", "cprev", out var cpCount) ||
            TryParseQuickfixNav(cmd, "cp", "cprevious", out cpCount))
            return new ExResult(true, null, VimEvent.QuickfixPrev(ApplyRangeCount(range, cpCount)));
        if (TryParseQuickfixGoto(cmd, out var ccIndex))
            return new ExResult(true, null, VimEvent.QuickfixGoto(ccIndex));

        // Location list commands
        if (cmd is "lopen" or "lope" or "llist" or "lli")
            return new ExResult(true, null, VimEvent.LocationListOpen());
        if (cmd is "lclose" or "lcl")
            return new ExResult(true, null, VimEvent.LocationListClose());
        if (TryParseQuickfixNav(cmd, "ln", "lnext", out var lnCount))
            return new ExResult(true, null, VimEvent.LocationListNext(ApplyRangeCount(range, lnCount)));
        if (TryParseQuickfixNav(cmd, "lp", "lprev", out var lpCount) ||
            TryParseQuickfixNav(cmd, "lp", "lprevious", out lpCount))
            return new ExResult(true, null, VimEvent.LocationListPrev(ApplyRangeCount(range, lpCount)));
        if (TryParseLocationListGoto(cmd, out var llIndex))
            return new ExResult(true, null, VimEvent.LocationListGoto(llIndex));

        // :split [file] :vsplit [file]
        if (cmd == "split" || cmd == "sp" || cmd == "new" ||
            cmd.StartsWith("split ") || cmd.StartsWith("sp ") || cmd.StartsWith("new "))
        {
            var path = cmd.StartsWith("split ", StringComparison.Ordinal) ? cmd[6..].Trim()
                     : cmd.StartsWith("sp ", StringComparison.Ordinal)    ? cmd[3..].Trim()
                     : cmd.StartsWith("new ", StringComparison.Ordinal)   ? cmd[4..].Trim()
                     : null;
            return new ExResult(true, null, VimEvent.SplitRequested(false,
                string.IsNullOrWhiteSpace(path) ? null : path));
        }
        if (cmd == "vsplit" || cmd == "vs" || cmd == "vnew" ||
            cmd.StartsWith("vsplit ") || cmd.StartsWith("vs ") || cmd.StartsWith("vnew "))
        {
            var path = cmd.StartsWith("vsplit ", StringComparison.Ordinal) ? cmd[7..].Trim()
                     : cmd.StartsWith("vs ", StringComparison.Ordinal)     ? cmd[3..].Trim()
                     : cmd.StartsWith("vnew ", StringComparison.Ordinal)   ? cmd[5..].Trim()
                     : null;
            return new ExResult(true, null, VimEvent.SplitRequested(true,
                string.IsNullOrWhiteSpace(path) ? null : path));
        }

        // :number (go to line)
        if (int.TryParse(cmd, out var lineNum))
        {
            var line = Math.Clamp(lineNum - 1, 0, _bufferManager.Current.Text.LineCount - 1);
            return new ExResult(true, null, VimEvent.CursorMoved(new CursorPosition(line, 0)));
        }

        // :grep [!] {pattern} [{glob}]  /  :vimgrep [!] /{pattern}/[flags] [{glob}]
        if (cmd.StartsWith("grep") && (cmd.Length == 4 || cmd[4] is ' ' or '!'))
        {
            var rest = cmd.Length > 4 ? cmd[4..].TrimStart('!').Trim() : "";
            if (string.IsNullOrEmpty(rest)) return new ExResult(false, "E471: Argument required");
            ParseGrepPattern(rest, out var gpat, out var gglob, out var gic);
            return new ExResult(true, null, VimEvent.GrepRequested(gpat, gglob, gic));
        }
        if (cmd.StartsWith("vimgrep") && (cmd.Length == 7 || cmd[7] is ' ' or '!'))
        {
            var rest = cmd.Length > 7 ? cmd[7..].TrimStart('!').Trim() : "";
            if (string.IsNullOrEmpty(rest)) return new ExResult(false, "E471: Argument required");
            ParseVimgrepPattern(rest, out var vpat, out var vglob, out var vic);
            return new ExResult(true, null, VimEvent.GrepRequested(vpat, vglob, vic));
        }

        // :grepreplace /{pattern}/{replacement}/[flags] [{glob}]
        // :creplace {replacement} replaces the current grep quickfix results in the host UI.
        if (cmd.StartsWith("grepreplace") && (cmd.Length == 11 || cmd[11] is ' ' or '!') ||
            cmd.StartsWith("greplace") && (cmd.Length == 8 || cmd[8] is ' ' or '!'))
        {
            var verbLength = cmd.StartsWith("grepreplace", StringComparison.Ordinal) ? 11 : 8;
            var rest = cmd.Length > verbLength ? cmd[verbLength..].TrimStart('!').Trim() : "";
            if (string.IsNullOrEmpty(rest)) return new ExResult(false, "E471: Argument required");
            if (!TryParseProjectReplace(rest, out var pattern, out var replacement, out var glob, out var ignoreCase, out var parseError))
                return new ExResult(false, parseError);
            return new ExResult(true, null, VimEvent.ProjectReplaceRequested(pattern, replacement, glob, ignoreCase));
        }
        if (cmd.StartsWith("creplace") && (cmd.Length == 8 || cmd[8] is ' ' or '!'))
        {
            var rest = cmd.Length > 8 ? cmd[8..].TrimStart('!').Trim() : "";
            return new ExResult(true, null, VimEvent.QuickfixReplaceRequested(rest));
        }

        // :s/:g/:v/:global/:vglobal — see ExCommands/SubstituteCommands.cs
        if (_substituteCommands.TryHandle(cmd, range, cursor, out var substituteResult))
            return substituteResult;

        // :[range]!{cmd}  — with range: filter lines; without range: run command and show output
        if (cmd.StartsWith('!') && cmd.Length > 1)
        {
            var shellCmd = cmd[1..].Trim();
            if (string.IsNullOrEmpty(shellCmd)) return new ExResult(false, "No command");
            if (string.IsNullOrEmpty(range))
            {
                var (output, err) = RunShellCommand(shellCmd, null);
                if (err != null) return new ExResult(false, err);
                var preview = output.Split(NewlineSeparators, StringSplitOptions.None)[0];
                return new ExResult(true, preview.Length > 80 ? preview[..80] + "…" : preview);
            }
            var buf2 = _bufferManager.Current.Text;
            int filterStart = 0, filterEnd = buf2.LineCount - 1;
            ResolveRange(range, cursor, buf2.LineCount, ref filterStart, ref filterEnd);
            return ExecuteShellFilter(shellCmd, filterStart, filterEnd);
        }

        // :read !{cmd}  — insert shell command output after current line
        if (cmd.StartsWith("read !") || cmd.StartsWith("r !"))
        {
            var offset = cmd.StartsWith("read !") ? 6 : 3;
            var shellCmd = cmd[offset..].Trim();
            if (string.IsNullOrEmpty(shellCmd)) return new ExResult(false, "No command");
            return ExecuteReadShell(shellCmd, cursor);
        }

        // :preview  :mdpreview
        if (cmd == "preview" || cmd == "mdpreview")
            return new ExResult(true, null, VimEvent.MarkdownPreviewRequested());

        // :terms  :termnext  :termprev  :termselect N  :termclose[!] [N]
        if (cmd == "terms")
            return new ExResult(true, null, VimEvent.TerminalCommandRequested(TerminalCommandKind.List));

        if (cmd == "termnext")
            return new ExResult(true, null, VimEvent.TerminalCommandRequested(TerminalCommandKind.Next));

        if (cmd == "termprev")
            return new ExResult(true, null, VimEvent.TerminalCommandRequested(TerminalCommandKind.Previous));

        if (cmd == "termselect" || cmd.StartsWith("termselect "))
        {
            var rest = cmd.Length > "termselect".Length
                ? cmd["termselect".Length..].Trim()
                : "";
            if (!TryParseTerminalNumber(rest, out var terminalNumber))
                return new ExResult(false, "Invalid terminal number");
            return new ExResult(true, null, VimEvent.TerminalCommandRequested(TerminalCommandKind.Select, terminalNumber));
        }

        if (cmd == "termclose" || cmd == "termclose!" || cmd.StartsWith("termclose ") || cmd.StartsWith("termclose! "))
        {
            var force = cmd.StartsWith("termclose!");
            var prefixLength = force ? "termclose!".Length : "termclose".Length;
            var rest = cmd.Length > prefixLength ? cmd[prefixLength..].Trim() : "";
            if (rest.Length == 0)
                return new ExResult(true, null, VimEvent.TerminalCommandRequested(TerminalCommandKind.Close, force: force));
            if (!TryParseTerminalNumber(rest, out var terminalNumber))
                return new ExResult(false, "Invalid terminal number");
            return new ExResult(true, null, VimEvent.TerminalCommandRequested(TerminalCommandKind.Close, terminalNumber, force));
        }

        // :terminal [cmd]  :term [cmd]
        if (cmd == "terminal" || cmd == "term" || cmd.StartsWith("terminal ") || cmd.StartsWith("term "))
        {
            var rest = cmd.Contains(' ') ? cmd[(cmd.IndexOf(' ') + 1)..].Trim() : null;
            return new ExResult(true, null, VimEvent.TerminalRequested(rest?.Length > 0 ? rest : null));
        }

        // :mksession [file]  :mks [file]
        if (cmd.StartsWith("mksession") || cmd.StartsWith("mks"))
        {
            var rest = cmd.StartsWith("mksession") ? cmd[9..].Trim() : cmd[3..].Trim();
            var path = rest.Length > 0 ? rest : "Session.vim";
            return new ExResult(true, null, VimEvent.MkSessionRequested(path));
        }

        // :source [file]  :so [file]
        if (cmd.StartsWith("source ") || cmd.StartsWith("so ") || cmd == "source" || cmd == "so")
        {
            var rest = cmd.Contains(' ') ? cmd[(cmd.IndexOf(' ') + 1)..].Trim() : "";
            if (rest.Length == 0) return new ExResult(false, "E476: Invalid command");
            return new ExResult(true, null, VimEvent.SourceRequested(rest));
        }

        // :scriptnames — list sourced vim scripts in load order
        if (cmd == "scriptnames" || cmd == "script")
            return new ExResult(true, FormatScriptNames());

        // :call/:function/:if/:for (interactive stubs; real evaluation happens in ScriptingCommands) — see ExCommands/ScriptingCommands.cs
        if (_scriptingCommands.TryHandle(cmd, cursor, out var scriptingResult))
            return scriptingResult;

        // :changes
        if (cmd == "changes")
            return new ExResult(true, _markManager.FormatChangeList());

        // :undolist — display the current linear undo/redo history
        if (cmd == "undolist")
            return new ExResult(true, _bufferManager.Current.Undo.FormatUndoList());

        // :undo — single undo step (like normal-mode `u`); :undo {N} — jump to change N
        // (searches the current undo stack, the current redo stack, then archived branches).
        if (cmd == "undo" || cmd.StartsWith("undo "))
        {
            var space = cmd.IndexOf(' ');
            var argument = space >= 0 ? cmd[(space + 1)..].Trim() : "";
            return ExecuteUndoCommand(argument, cursor);
        }

        // :earlier {N}[s|m|h] / :later {N}[s|m|h]
        if (cmd == "earlier" || cmd.StartsWith("earlier ") ||
            cmd == "later"   || cmd.StartsWith("later "))
        {
            var space = cmd.IndexOf(' ');
            var verb = space >= 0 ? cmd[..space] : cmd;
            var argument = space >= 0 ? cmd[(space + 1)..].Trim() : "";
            return ExecuteUndoTraversal(verb, argument, cursor);
        }

        // :jumps
        if (cmd == "jumps")
            return new ExResult(true, _markManager.FormatJumpList());

        // :[range]center [width]  :[range]right [width]  :[range]left
        if (cmd == "center" || cmd.StartsWith("center ") ||
            cmd == "right"  || cmd.StartsWith("right ")  ||
            cmd == "left"   || cmd.StartsWith("left "))
        {
            int sp = cmd.IndexOf(' ');
            string verb = sp >= 0 ? cmd[..sp] : cmd;
            int width = _options.TextWidth;
            if (sp >= 0 && int.TryParse(cmd[(sp + 1)..].Trim(), out var w)) width = w;
            return ExecuteAlign(verb, width, range, cursor);
        }

        // :[range]move {addr}   :m[ove]
        if (cmd.StartsWith("move ") || cmd.StartsWith("m ") || cmd == "move" || cmd == "m")
        {
            int sp = cmd.IndexOf(' ');
            return ExecuteMove(sp >= 0 ? cmd[(sp + 1)..].Trim() : "", range, cursor);
        }

        // :[range]copy {addr}   :co[py]  :t
        if (cmd.StartsWith("copy ") || cmd.StartsWith("co ") || cmd.StartsWith("t ") ||
            cmd == "copy" || cmd == "co" || cmd == "t")
        {
            int sp = cmd.IndexOf(' ');
            return ExecuteCopy(sp >= 0 ? cmd[(sp + 1)..].Trim() : "", range, cursor);
        }

        // :digraphs — list all available digraphs
        if (cmd is "digraphs" or "digraph")
            return new ExResult(true, DiGraphs.DisplayText);

        // :abbreviate / :ab / :iabbrev / :iab — list or add abbreviations
        if (cmd is "abbreviate" or "ab" or "iabbrev" or "iab")
        {
            if (_abbreviations.Count == 0) return new ExResult(true, "No abbreviations defined");
            var list = string.Join(", ", _abbreviations.Select(kv => $"{kv.Key} => {kv.Value}"));
            return new ExResult(true, list);
        }
        if (cmd.StartsWith("abbreviate ") || cmd.StartsWith("ab ") ||
            cmd.StartsWith("iabbrev ") || cmd.StartsWith("iab "))
        {
            var rest = cmd[(cmd.IndexOf(' ') + 1)..].Trim();
            var parts = rest.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2) return new ExResult(false, "Usage: :iab <lhs> <rhs>");
            _abbreviations[parts[0]] = parts[1];
            return new ExResult(true);
        }

        // :unabbreviate / :una / :iunabbrev / :iuna — remove abbreviation
        if (cmd.StartsWith("unabbreviate ") || cmd.StartsWith("una ") ||
            cmd.StartsWith("iunabbrev ") || cmd.StartsWith("iuna "))
        {
            var lhs = cmd[(cmd.IndexOf(' ') + 1)..].Trim();
            if (!_abbreviations.Remove(lhs))
                return new ExResult(false, $"No such abbreviation: {lhs}");
            return new ExResult(true);
        }

        // :history [type]  :his [type]
        if (cmd == "history" || cmd == "his" ||
            cmd.StartsWith("history ") || cmd.StartsWith("his "))
        {
            var arg = cmd.Contains(' ') ? cmd[(cmd.IndexOf(' ') + 1)..].Trim() : "";
            return ExecuteHistory(arg);
        }

        // :bufdo {cmd}  — execute ex command on every buffer
        // :windo {cmd}  — treated as :bufdo (pane management lives in WPF layer)
        // :tabdo {cmd}  — treated as :bufdo (tab management lives in WPF layer)
        if (cmd.StartsWith("bufdo ") || cmd.StartsWith("windo ") || cmd.StartsWith("tabdo "))
        {
            var subCmd = cmd[(cmd.IndexOf(' ') + 1)..].Trim();
            if (string.IsNullOrEmpty(subCmd))
                return new ExResult(false, "E471: Argument required");
            return ExecuteBufdo(subCmd);
        }

        // :noh / :nohlsearch — temporarily clear search highlight without disabling hlsearch.
        // HlSearch stays true so that n/N will re-enable highlights on the next search move.
        if (cmd is "noh" or "nohl" or "nohlsearch" or "nohls")
            return new ExResult(true, null, VimEvent.SearchChanged("", 0));

        // :pwd — print working directory
        if (cmd is "pwd")
            return new ExResult(true, Directory.GetCurrentDirectory());

        // :cd [dir] / :lcd [dir] — change working directory
        if (cmd == "cd" || cmd.StartsWith("cd ") || cmd == "lcd" || cmd.StartsWith("lcd "))
        {
            string cdArg = GetCommandArg(cmd);
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string targetDir;
            if (string.IsNullOrEmpty(cdArg) || cdArg == "~")
                targetDir = home;
            else if (cdArg.StartsWith("~/") || cdArg.StartsWith("~\\"))
                targetDir = home + cdArg[1..];
            else
                targetDir = cdArg;

            try
            {
                Directory.SetCurrentDirectory(targetDir);
                return new ExResult(true, Directory.GetCurrentDirectory());
            }
            catch (Exception ex)
            {
                return new ExResult(false, ex.Message);
            }
        }

        // :yank/:put/:registers/:marks/:delmarks — see ExCommands/RegisterMarkCommands.cs
        if (_registerMarkCommands.TryHandle(cmd, range, cursor, out var registerMarkResult))
            return registerMarkResult;

        if (_commandRegistry.TryExecuteSynchronously(cmd, rawCommand, range, cursor, _services, out var extensionResult))
            return extensionResult;
        return new ExResult(false, $"Not an editor command: {cmd}");
    }

    private static (string Command, string Range) ParseExtensionCommand(string rawCommand)
    {
        var command = rawCommand.Trim();
        if (command.Length == 0) return ("", "");
        TryScanRangePrefix(command, out var range, out var commandStart);
        return (command[commandStart..].Trim(), range);
    }

    private ExResult ExecuteUndoCommand(string argument, CursorPosition cursor)
    {
        var vbuf = _bufferManager.Current;

        if (string.IsNullOrWhiteSpace(argument))
        {
            var state = vbuf.Undo.Undo(vbuf.Text, cursor);
            if (state == null) return new ExResult(true, "Already at oldest change");
            return new ExResult(true, "1 change undone", RestoredCursor: state.Cursor, BufferRestored: true);
        }

        if (!int.TryParse(argument, out var changeNumber) || changeNumber < 0)
            return new ExResult(false, "Invalid argument");

        var result = vbuf.Undo.JumpToChangeNumber(changeNumber, vbuf.Text, cursor);
        if (result == null)
            return new ExResult(false, $"E830: Undo number {changeNumber} not found");

        if (result.Count == 0)
            return new ExResult(true, "Already at oldest change");

        return new ExResult(
            true,
            $"Change {changeNumber}",
            RestoredCursor: result.State?.Cursor,
            BufferRestored: true);
    }

    private ExResult ExecuteUndoTraversal(string verb, string argument, CursorPosition cursor)
    {
        if (!TryParseUndoTraversalArgument(argument, out var amount, out var unit))
            return new ExResult(false, string.IsNullOrWhiteSpace(argument) ? "Argument required" : "Invalid argument");

        var vbuf = _bufferManager.Current;
        UndoTraversalResult result;
        if (unit == '\0')
        {
            result = verb == "earlier"
                ? vbuf.Undo.Earlier(vbuf.Text, cursor, amount)
                : vbuf.Undo.Later(vbuf.Text, cursor, amount);
        }
        else
        {
            var span = unit switch
            {
                's' => TimeSpan.FromSeconds(amount),
                'm' => TimeSpan.FromMinutes(amount),
                'h' => TimeSpan.FromHours(amount),
                _ => TimeSpan.Zero
            };
            result = verb == "earlier"
                ? vbuf.Undo.Earlier(vbuf.Text, cursor, span)
                : vbuf.Undo.Later(vbuf.Text, cursor, span);
        }

        if (result.Count == 0)
        {
            var edge = verb == "earlier" ? "oldest" : "newest";
            return new ExResult(true, $"Already at {edge} change");
        }

        var action = verb == "earlier" ? "undone" : "redone";
        var noun = result.Count == 1 ? "change" : "changes";
        return new ExResult(
            true,
            $"{result.Count} {noun} {action}",
            RestoredCursor: result.State?.Cursor,
            BufferRestored: true);
    }

    private static bool TryParseUndoTraversalArgument(string argument, out int amount, out char unit)
    {
        amount = 0;
        unit = '\0';

        var match = Regex.Match(argument.Trim(), @"^(\d+)([smh]?)$");
        if (!match.Success) return false;
        if (!int.TryParse(match.Groups[1].Value, out amount) || amount <= 0) return false;

        var unitText = match.Groups[2].Value;
        unit = unitText.Length == 0 ? '\0' : unitText[0];
        return true;
    }

    // Range-prefix scanning, range→line resolution, and regex construction — see ExCommands/RangeResolver.cs
    private static Regex? TryBuildRegex(string pattern, bool ignoreCase, out string? error) =>
        ExCommands.RangeResolver.TryBuildRegex(pattern, ignoreCase, out error);

    private static bool TryScanRangePrefix(string cmd, out string range, out int cmdStart) =>
        ExCommands.RangeResolver.TryScanRangePrefix(cmd, out range, out cmdStart);

    internal void ResolveRange(string range, CursorPosition cursor, int lineCount, ref int startLine, ref int endLine) =>
        _rangeResolver.ResolveRange(range, cursor, lineCount, ref startLine, ref endLine);

    // Returns 0-based destination line index, or -1 for "before line 0", or -2 on error.
    private static int ResolveAddress(string addr, CursorPosition cursor, int lineCount)
    {
        addr = addr.Trim();
        if (addr == "0") return -1;
        if (addr == "." || addr == "") return cursor.Line;
        if (addr == "$") return lineCount - 1;
        if (int.TryParse(addr, out var n)) return n - 1;
        if (addr.StartsWith(".+") && int.TryParse(addr[2..], out var dp)) return cursor.Line + dp;
        if (addr.StartsWith(".-") && int.TryParse(addr[2..], out var dm)) return cursor.Line - dm;
        if (addr.StartsWith("$-") && int.TryParse(addr[2..], out var sm)) return lineCount - 1 - sm;
        return -2;
    }

    private (TextBuffer buf, int startLine, int endLine) ResolveRangeClamped(string range, CursorPosition cursor)
    {
        var buf = _bufferManager.Current.Text;
        int startLine = cursor.Line, endLine = cursor.Line;
        ResolveRange(range, cursor, buf.LineCount, ref startLine, ref endLine);
        startLine = Math.Clamp(startLine, 0, buf.LineCount - 1);
        endLine = Math.Clamp(endLine, startLine, buf.LineCount - 1);
        return (buf, startLine, endLine);
    }

    private ExResult ExecuteMove(string addrStr, string range, CursorPosition cursor)
    {
        var (buf, startLine, endLine) = ResolveRangeClamped(range, cursor);

        int dest = ResolveAddress(addrStr, cursor, buf.LineCount);
        if (dest < -1) return new ExResult(false, "E14: Invalid address");
        if (dest >= startLine && dest <= endLine)
            return new ExResult(false, "E134: Move lines into themselves");

        var lines = buf.GetLines(startLine, endLine);
        buf.DeleteLines(startLine, endLine);

        // If destination was after the deleted range, shift it back
        int adjustedDest = dest > endLine ? dest - lines.Length : dest;
        buf.InsertLines(adjustedDest, lines);

        return new ExResult(true, $"{lines.Length} line(s) moved", TextModified: true);
    }

    private ExResult ExecuteCopy(string addrStr, string range, CursorPosition cursor)
    {
        var (buf, startLine, endLine) = ResolveRangeClamped(range, cursor);

        int dest = ResolveAddress(addrStr, cursor, buf.LineCount);
        if (dest < -1) return new ExResult(false, "E14: Invalid address");

        buf.InsertLines(dest, buf.GetLines(startLine, endLine));

        return new ExResult(true, $"{endLine - startLine + 1} line(s) copied", TextModified: true);
    }

    private ExResult ExecuteAlign(string verb, int width, string range, CursorPosition cursor)
    {
        var (buf, startLine, endLine) = ResolveRangeClamped(range, cursor);
        for (int l = startLine; l <= endLine; l++)
        {
            var line = buf.GetLine(l);
            var trimmed = line.Trim();
            string aligned = verb switch
            {
                "left"   => trimmed,
                "right"  => trimmed.PadLeft(width),
                _        => CenterText(trimmed, width),
            };
            if (aligned != line)
                buf.ReplaceLine(l, aligned);
        }
        return new ExResult(true, $"{endLine - startLine + 1} line(s) aligned", TextModified: true);
    }

    private static string CenterText(string text, int width)
    {
        if (text.Length >= width) return text;
        int totalPad = width - text.Length;
        return text.PadLeft(text.Length + totalPad / 2);
    }


    private static readonly string[] NewlineSeparators = ["\r\n", "\n"];

    /// <summary>Runs a shell command, optionally writing <paramref name="stdin"/> to the process.
    /// Returns (output, errorMessage). On success errorMessage is null.</summary>
    private static (string Output, string? Error) RunShellCommand(string shellCmd, string? stdin)
    {
        try
        {
            var isWindows = OperatingSystem.IsWindows();
            var psi = new ProcessStartInfo
            {
                FileName = isWindows ? "cmd.exe" : "/bin/sh",
                RedirectStandardInput = stdin != null,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            if (isWindows) { psi.ArgumentList.Add("/c"); psi.ArgumentList.Add(shellCmd); }
            else           { psi.ArgumentList.Add("-c"); psi.ArgumentList.Add(shellCmd); }

            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start shell");
            if (stdin != null)
            {
                proc.StandardInput.Write(stdin);
                proc.StandardInput.Close();
            }
            var output = proc.StandardOutput.ReadToEnd();
            if (!proc.WaitForExit(10_000))
            {
                try { proc.Kill(); } catch { /* best-effort */ }
                return ("", "Shell command timed out");
            }
            return (output, null);
        }
        catch (Exception ex)
        {
            return ("", $"Shell error: {ex.Message}");
        }
    }

    private static string[] SplitShellOutput(string output)
    {
        // Strip exactly one trailing newline (the standard line terminator), preserving intentional blank lines
        if (output.EndsWith("\r\n", StringComparison.Ordinal)) output = output[..^2];
        else if (output.EndsWith('\n')) output = output[..^1];
        return output.Split(NewlineSeparators, StringSplitOptions.None);
    }

    private ExResult ExecuteShellFilter(string shellCmd, int startLine, int endLine)
    {
        var buf = _bufferManager.Current.Text;
        var input = string.Join("\n", buf.GetLines(startLine, endLine));
        var (output, err) = RunShellCommand(shellCmd, input);
        if (err != null) return new ExResult(false, err);
        var newLines = SplitShellOutput(output);
        buf.DeleteLines(startLine, endLine);
        buf.InsertLines(startLine, newLines);
        return new ExResult(true, $"{newLines.Length} line(s)", TextModified: true);
    }

    private ExResult ExecuteReadShell(string shellCmd, CursorPosition cursor)
    {
        var (output, err) = RunShellCommand(shellCmd, null);
        if (err != null) return new ExResult(false, err);
        var lines = SplitShellOutput(output);
        _bufferManager.Current.Text.InsertLines(cursor.Line, lines);
        return new ExResult(true, $"{lines.Length} line(s) inserted", TextModified: true);
    }

    private static bool TryParseQuickfixNav(string cmd, string shortName, string longName, out int count)
    {
        count = 1;
        // :cn or :cnext
        if (cmd == shortName || cmd == longName) return true;
        // :3cn or :3cnext (count prefix)
        foreach (var name in new[] { shortName, longName })
        {
            if (cmd.EndsWith(name, StringComparison.Ordinal))
            {
                var prefix = cmd[..^name.Length];
                if (int.TryParse(prefix, out var n) && n > 0) { count = n; return true; }
            }
        }
        return false;
    }

    private static int ApplyRangeCount(string range, int count) =>
        int.TryParse(range, out var rangeCount) && rangeCount > 0 ? rangeCount : count;

    private static bool TryParseQuickfixGoto(string cmd, out int index)
    {
        index = 0;
        // :cc or :cc N
        if (cmd == "cc") { index = -1; return true; }
        if (cmd.StartsWith("cc ") || cmd.StartsWith("cc\t"))
        {
            var rest = cmd[2..].Trim();
            if (int.TryParse(rest, out var n) && n >= 1) { index = n - 1; return true; }
        }
        return false;
    }

    private static bool TryParseLocationListGoto(string cmd, out int index)
    {
        index = 0;
        // :ll or :ll N
        if (cmd == "ll") { index = -1; return true; }
        if (cmd.StartsWith("ll ") || cmd.StartsWith("ll\t"))
        {
            var rest = cmd[2..].Trim();
            if (int.TryParse(rest, out var n) && n >= 1) { index = n - 1; return true; }
        }
        return false;
    }

    // Parse ":grep pattern [glob]" or ":grep /pattern/[flags] [glob]"
    private static void ParseGrepPattern(string rest, out string pattern, out string? fileGlob, out bool ignoreCase)
    {
        ignoreCase = false;
        fileGlob = null;

        if (rest.StartsWith('/'))
        {
            var closeSlash = rest.IndexOf('/', 1);
            if (closeSlash > 0)
            {
                pattern = rest[1..closeSlash];
                ParseFlagsAndGlob(rest[(closeSlash + 1)..], out var flags, out var globPart);
                ignoreCase = flags.Contains('i');
                fileGlob = string.IsNullOrEmpty(globPart) ? null : globPart;
                return;
            }
        }

        // "pattern [glob]" — last whitespace token is glob if it looks like one
        var lastSpace = rest.LastIndexOf(' ');
        if (lastSpace >= 0)
        {
            var lastToken = rest[(lastSpace + 1)..];
            if (lastToken.Contains('*') || lastToken == "%")
            {
                pattern = rest[..lastSpace].Trim();
                fileGlob = lastToken;
                return;
            }
        }

        pattern = rest;
    }

    // Parse ":vimgrep /pattern/[flags] [glob]"
    private static void ParseVimgrepPattern(string rest, out string pattern, out string? fileGlob, out bool ignoreCase)
    {
        ignoreCase = false;
        fileGlob = null;

        if (rest.StartsWith('/'))
        {
            var closeSlash = rest.IndexOf('/', 1);
            if (closeSlash > 0)
            {
                pattern = rest[1..closeSlash];
                ParseFlagsAndGlob(rest[(closeSlash + 1)..], out var flags, out var globPart);
                ignoreCase = flags.Contains('i');
                fileGlob = string.IsNullOrEmpty(globPart) ? null : globPart;
                return;
            }
        }

        // Fallback: no delimiter → treat whole thing as pattern
        pattern = rest;
    }

    private static bool TryParseProjectReplace(
        string rest,
        out string pattern,
        out string replacement,
        out string? fileGlob,
        out bool ignoreCase,
        out string? error)
    {
        pattern = "";
        replacement = "";
        fileGlob = null;
        ignoreCase = false;
        error = null;

        if (string.IsNullOrEmpty(rest))
        {
            error = "E471: Argument required";
            return false;
        }

        var delimiter = rest[0];
        if (char.IsWhiteSpace(delimiter))
        {
            error = "E476: Invalid command";
            return false;
        }

        var patternEnd = FindUnescaped(rest, delimiter, 1);
        if (patternEnd < 0)
        {
            error = "E476: Invalid command";
            return false;
        }

        var replacementEnd = FindUnescaped(rest, delimiter, patternEnd + 1);
        if (replacementEnd < 0)
        {
            error = "E476: Invalid command";
            return false;
        }

        pattern = UnescapeDelimiter(rest[1..patternEnd], delimiter);
        replacement = UnescapeDelimiter(rest[(patternEnd + 1)..replacementEnd], delimiter);

        var after = rest[(replacementEnd + 1)..];
        if (after.Trim().Length == 0)
            return true;

        ParseFlagsAndGlob(after, out var flags, out var globPart);
        ignoreCase = flags.Contains('i');
        fileGlob = string.IsNullOrEmpty(globPart) ? null : globPart;
        return true;
    }

    private static void ParseFlagsAndGlob(string text, out string flags, out string glob)
    {
        flags = "";
        glob = "";

        if (string.IsNullOrEmpty(text))
            return;

        if (char.IsWhiteSpace(text[0]))
        {
            glob = text.Trim();
            return;
        }

        var splitIndex = text.IndexOfAny([' ', '\t']);
        if (splitIndex < 0)
        {
            flags = text;
            return;
        }

        flags = text[..splitIndex];
        glob = text[(splitIndex + 1)..].Trim();
    }

    private static int FindUnescaped(string text, char value, int startIndex)
    {
        var escaped = false;
        for (var i = startIndex; i < text.Length; i++)
        {
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (text[i] == '\\')
            {
                escaped = true;
                continue;
            }

            if (text[i] == value)
                return i;
        }

        return -1;
    }

    private static string UnescapeDelimiter(string text, char delimiter)
    {
        return text.Replace("\\" + delimiter, delimiter.ToString(), StringComparison.Ordinal);
    }

    private ExResult ExecuteHistory(string type)
    {
        bool showCmd    = type is "" or ":" or "cmd" or "command" or "all";
        bool showSearch = type is "" or "all" or "/" or "?" or "search";

        if (type.Length > 0 && !showCmd && !showSearch)
            return new ExResult(false, $"E488: Trailing characters: {type}");

        var sb = new System.Text.StringBuilder();

        if (showCmd && _history.Count > 0)
        {
            sb.AppendLine("  #  cmd history");
            for (int i = 0; i < _history.Count; i++)
                sb.AppendLine($"{_history.Count - i,4}  {_history[i]}");
        }

        if (showSearch && _searchHistory.Count > 0)
        {
            if (sb.Length > 0) sb.AppendLine();
            sb.AppendLine("  #  search history");
            for (int i = 0; i < _searchHistory.Count; i++)
                sb.AppendLine($"{_searchHistory.Count - i,4}  {_searchHistory[i]}");
        }

        if (sb.Length == 0)
            return new ExResult(true, "No history");

        return new ExResult(true, sb.ToString().TrimEnd());
    }

    // Extract the optional argument after a command verb — see ExCommands/RangeResolver.cs
    private static string GetCommandArg(string cmd) => ExCommands.RangeResolver.GetCommandArg(cmd);

    private ExResult ExecuteBufdo(string subCmd)
    {
        var buffers = _bufferManager.Buffers;
        if (buffers.Count == 0) return new ExResult(true);

        var errors = new List<string>();
        var zero = new CursorPosition(0, 0);
        for (int i = 0; i < buffers.Count; i++)
        {
            _bufferManager.GoTo(i);
            // Execute sub-command without recording it in history (it's an internal iteration).
            // Commands like :%s/…/…/g work correctly because they carry their own range.
            var result = ExecuteNoHistory(subCmd, zero);
            if (!result.Success && result.Message != null)
                errors.Add($"[{buffers[i].Name}] {result.Message}");
        }

        if (errors.Count > 0)
            return new ExResult(false, string.Join("; ", errors));
        return new ExResult(true, null, null, true);
    }

    // Execute a command without recording it in the command history (used for internal iteration).
    private ExResult ExecuteNoHistory(string cmdLine, CursorPosition cursor)
    {
        _suppressHistory++;
        try { return Execute(cmdLine, cursor); }
        finally { _suppressHistory--; }
    }


    // ─────────────── MAP LISTING ───────────────

    private static string BuildMapListing(string modePrefix, Dictionary<string, string> maps)
    {
        if (maps.Count == 0) return "(no mappings)";
        return string.Join('\n', maps.Select(kvp => $"{modePrefix}  {kvp.Key}  {kvp.Value}"));
    }

    // ─────────────── TAB COMPLETION ───────────────

    private static readonly string[] AllCommandNames =
    [
        "q", "quit", "q!", "quit!",
        "wq", "wq!", "x", "x!", "xit", "exit",
        "qa", "qa!", "qall", "qall!",
        "w", "write",
        "e", "e!", "edit", "edit!",
        "set",
        "colorscheme", "colo",
        "syntax",
        "bn", "bnext", "bp", "bprev",
        "b", "bd", "bdelete",
        "tabnew", "tabedit", "tabe",
        "tabn", "tabnext", "tabp", "tabprev", "tabprevious",
        "tabc", "tabc!", "tabclose", "tabclose!",
        "split", "sp", "new",
        "vsplit", "vs", "vnew",
        "Format", "Rename", "Symbols", "sym", "outline", "digraphs",
        "Lsp", "LspList", "LspAdd", "LspRemove", "LspReset",
        "Fmt", "FmtList", "FmtSet", "FmtRemove",
        "read", "r",
        "Git blame", "Gblame",
        "Git status", "Gstatus", "gs",
        "Git commit", "Gcommit",
        "Git stage", "Gstage",
        "Git unstage", "Gunstage",
        "Git diff", "Gdiff",
        "Git log", "Glog",
        "Git push", "Gpush",
        "Git pull", "Gpull",
        "copen", "cope", "clist", "cl",
        "cclose", "ccl",
        "cn", "cnext", "cp", "cprev",
        "diagnostics", "diag",
        "lopen", "lope", "llist", "lli",
        "lclose", "lcl",
        "ln", "lnext", "lp", "lprev", "lprevious", "ll",
        "sort",
        "move", "m", "copy", "co", "t",
        "center", "right", "left",
        "normal", "norm", "normal!", "norm!",
        "g", "global", "v", "vglobal",
        "grep", "vimgrep", "grepreplace", "greplace", "creplace",
        "nmap", "nnoremap", "imap", "inoremap", "vmap", "vnoremap",
        "map",
        "unmap", "nunmap", "iunmap", "vunmap",
        "let", "for", "endfor", "function", "endfunction", "call",
        "history", "his",
        "preview", "mdpreview",
        "terminal", "term",
        "terms", "termnext", "termprev", "termselect", "termclose", "termclose!",
        "mksession", "source",
        "scriptnames", "script",
        "undolist", "undo", "earlier", "later",
        "retab",
    ];

    private static readonly HashSet<string> FileCommands = new(StringComparer.Ordinal)
        { "e", "e!", "edit", "edit!", "w", "write", "split", "sp", "vsplit", "vs", "new", "vnew", "tabnew", "tabedit", "tabe" };

    private static readonly string[] KnownColorschemes = ["dracula", "nord", "tokyonight", "onedark", "dark", "light"];

    private static readonly string[] SetOptionNames =
    [
        "number", "nu", "nonumber", "nonu",
        "relativenumber", "rnu", "norelativenumber", "nornu",
        "cursorline", "cul", "nocursorline", "nocul",
        "wrap", "nowrap",
        "expandtab", "et", "noexpandtab", "noet",
        "autoindent", "ai", "noautoindent", "noai",
        "smartindent", "si", "nosmartindent", "nosi",
        "ignorecase", "ic", "noignorecase", "noic",
        "smartcase", "scs", "nosmartcase", "noscs",
        "hlsearch", "hls", "nohlsearch", "nohls",
        "incsearch", "is", "noincsearch", "nois",
        "inccommand", "icm", "noinccommand", "noicm",
        "wrapscan", "ws", "nowrapscan", "nows",
        "hidden", "nohidden",
        "ruler", "noruler",
        "showmode", "noshowmode",
        "showcmd", "noshowcmd",
        "syntax", "nosyntax",
        "tabstop=", "ts=",
        "shiftwidth=", "sw=",
        "scrolloff=", "so=",
        "history=",
        "fontsize=",
        "clipboard=", "cb=",
        "colorscheme=",
        "fileformat=", "ff=",
    ];

    /// <summary>
    /// Returns completion candidates for the given partial command-line text (without the leading ':').
    /// Each returned string is a full replacement for the command line.
    /// </summary>
    public string[] GetCompletions(string partial, string? workingDir = null)
    {
        if (string.IsNullOrEmpty(partial)) return [];

        var spaceIdx = partial.IndexOf(' ');

        // No space yet — complete command names
        if (spaceIdx < 0)
        {
            return AllCommandNames
                .Where(c => c.StartsWith(partial, StringComparison.OrdinalIgnoreCase))
                .OrderBy(c => c)
                .ToArray();
        }

        var cmd = partial[..spaceIdx];
        var arg = partial[(spaceIdx + 1)..];

        // Buffer name completion
        if (cmd is "b" or "buffer" or "bd" or "bdelete")
        {
            return _bufferManager.Buffers
                .Select(b => Path.GetFileName(b.FilePath) ?? "")
                .Where(n => n.Length > 0 && n.StartsWith(arg, StringComparison.OrdinalIgnoreCase))
                .OrderBy(n => n)
                .Select(n => $"{cmd} {n}")
                .ToArray();
        }

        // Colorscheme completion
        if (cmd is "colorscheme" or "colo")
        {
            return KnownColorschemes
                .Where(c => c.StartsWith(arg, StringComparison.OrdinalIgnoreCase))
                .Select(c => $"{cmd} {c}")
                .ToArray();
        }

        // Set option completion
        if (cmd == "set")
        {
            return SetOptionNames
                .Where(o => o.StartsWith(arg, StringComparison.OrdinalIgnoreCase))
                .OrderBy(o => o)
                .Select(o => $"set {o}")
                .ToArray();
        }

        // File path completion
        if (FileCommands.Contains(cmd))
            return CompleteFilePath(cmd, arg, workingDir);

        return [];
    }

    private static string[] CompleteFilePath(string cmdPrefix, string argPartial, string? workingDir)
    {
        try
        {
            var cwd = workingDir ?? Directory.GetCurrentDirectory();
            string baseDir = cwd;
            string prefix;

            if (Path.IsPathRooted(argPartial))
            {
                var dirPart = Path.GetDirectoryName(argPartial) ?? "";
                baseDir = string.IsNullOrEmpty(dirPart) ? (Path.GetPathRoot(argPartial) ?? cwd) : dirPart;
                prefix = Path.GetFileName(argPartial);
            }
            else
            {
                var slashIdx = argPartial.LastIndexOfAny(['/', '\\']);
                if (slashIdx >= 0)
                {
                    baseDir = Path.GetFullPath(Path.Combine(cwd, argPartial[..slashIdx]));
                    prefix = argPartial[(slashIdx + 1)..];
                }
                else
                {
                    prefix = argPartial;
                }
            }

            if (!Directory.Exists(baseDir)) return [];

            var results = new List<string>();

            // Directories first, filtering before sorting
            foreach (var dir in Directory.EnumerateDirectories(baseDir)
                .Where(d => Path.GetFileName(d).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .OrderBy(d => d))
            {
                results.Add($"{cmdPrefix} {MakeRelative(cwd, dir).Replace('\\', '/')}/");
            }

            // Then files, filtering before sorting
            foreach (var file in Directory.EnumerateFiles(baseDir)
                .Where(f => Path.GetFileName(f).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f))
            {
                results.Add($"{cmdPrefix} {MakeRelative(cwd, file).Replace('\\', '/')}");
            }

            return [.. results];
        }
        catch
        {
            return [];
        }
    }

    private string FormatScriptNames()
    {
        if (_scriptNames.Count == 0)
            return "(no scripts sourced)";

        return string.Join('\n', _scriptNames.Select((path, index) => $"{index + 1,3}: {path}"));
    }

    private static string MakeRelative(string basePath, string fullPath)
    {
        try { return Path.GetRelativePath(basePath, fullPath); }
        catch { return fullPath; }
    }

}
