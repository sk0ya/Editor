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
    private const int MaxFunctionCallDepth = 20;
    private const int MaxForListItems = 1000;
    private const int MaxForIterations = 10000;

    private readonly BufferManager _bufferManager;
    private readonly VimOptions _options;
    private readonly MarkManager _markManager;
    private readonly Dictionary<string, string> _abbreviations;
    private readonly RegisterManager? _registerManager;
    private readonly Dictionary<string, string> _normalMaps;
    private readonly Dictionary<string, string> _insertMaps;
    private readonly Dictionary<string, string> _visualMaps;
    private readonly Dictionary<string, string> _variables;
    private readonly Dictionary<string, VimFunctionDefinition> _functions;
    private readonly IReadOnlyList<string> _scriptNames;
    private readonly LspServerRegistry _lspRegistry;
    private readonly List<string> _history = [];
    private int _historyIndex = -1;
    private readonly List<string> _searchHistory = [];
    private int _searchHistoryIndex = -1;
    private int _suppressHistory;
    private int _executeDepth;
    private int _functionCallDepth;

    public ExCommandProcessor(BufferManager bufferManager, VimOptions options, MarkManager markManager,
        Dictionary<string, string>? abbreviations = null, RegisterManager? registerManager = null,
        Dictionary<string, string>? normalMaps = null, Dictionary<string, string>? insertMaps = null,
        Dictionary<string, string>? visualMaps = null, Dictionary<string, string>? variables = null,
        IReadOnlyList<string>? scriptNames = null,
        Dictionary<string, VimFunctionDefinition>? functions = null,
        LspServerRegistry? lspRegistry = null)
    {
        _bufferManager = bufferManager;
        _options = options;
        _markManager = markManager;
        _abbreviations = abbreviations ?? [];
        _registerManager = registerManager;
        _normalMaps = normalMaps ?? [];
        _insertMaps = insertMaps ?? [];
        _visualMaps = visualMaps ?? [];
        _variables = variables ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _functions = functions ?? new Dictionary<string, VimFunctionDefinition>(StringComparer.OrdinalIgnoreCase);
        _scriptNames = scriptNames ?? [];
        _lspRegistry = lspRegistry ?? LspServerRegistry.Default;
    }

    public string? LastCommand => _history.Count > 0 ? _history[0] : null;
    public IReadOnlyList<string> CommandHistory => _history.AsReadOnly();
    public IReadOnlyList<string> SearchHistory  => _searchHistory.AsReadOnly();

    public IReadOnlyDictionary<int, string> GetSubstitutePreview(string cmdLine, CursorPosition cursor)
    {
        if (!_options.IncCommand)
            return new Dictionary<int, string>();

        var cmd = cmdLine.Trim();
        if (string.IsNullOrEmpty(cmd))
            return new Dictionary<int, string>();

        string range = "";
        int cmdStart = 0;
        if (cmd.StartsWith('%')) { range = "%"; cmdStart = 1; }
        else if (cmd.StartsWith('.')) { range = "."; cmdStart = 1; }
        else if (char.IsDigit(cmd[0]))
        {
            int end = 0;
            while (end < cmd.Length && (char.IsDigit(cmd[end]) || cmd[end] == ','))
                end++;
            range = cmd[..end];
            cmdStart = end;
        }

        cmd = cmd[cmdStart..].Trim();
        if (cmd.Length < 3 || cmd[0] != 's' || cmd[1] is not ('/' or '!'))
            return new Dictionary<int, string>();

        char sep = cmd[1];
        var parts = cmd[2..].Split(sep);
        if (parts.Length < 2 || string.IsNullOrEmpty(parts[0]))
            return new Dictionary<int, string>();

        string pattern = parts[0];
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
        if (_suppressHistory == 0)
            AddHistory(cmdLine);
        var cmd = cmdLine.Trim();
        if (string.IsNullOrEmpty(cmd)) return new ExResult(true);

        // Range prefix: %, number, .
        string range = "";
        int cmdStart = 0;
        if (cmd.StartsWith('%')) { range = "%"; cmdStart = 1; }
        else if (cmd.StartsWith('.')) { range = "."; cmdStart = 1; }
        else if (char.IsDigit(cmd[0]))
        {
            int end = 0;
            while (end < cmd.Length && (char.IsDigit(cmd[end]) || cmd[end] == ','))
                end++;
            range = cmd[..end];
            cmdStart = end;
        }

        cmd = cmd[cmdStart..].Trim();

        // :q :q! :quit :quit! :wq :x :xit :exit
        if (cmd is "q" or "quit")
        {
            if (_bufferManager.Current.Text.IsModified)
                return new ExResult(false, "No write since last change (add ! to override)");
            return new ExResult(true, null, VimEvent.WindowCloseRequested(false));
        }
        if (cmd is "q!" or "quit!")
            return new ExResult(true, null, VimEvent.WindowCloseRequested(true));

        if (cmd is "wq" or "wq!" or "x" or "x!" or "xit" or "exit")
        {
            if (_bufferManager.Current.IsBinary)
                return new ExResult(false, "E21: Cannot write a binary file (read-only)");
            var buf = _bufferManager.Current;
            try { buf.Save(); }
            catch (Exception ex) { return new ExResult(false, ex.Message); }
            return new ExResult(true, null, VimEvent.QuitRequested(false));
        }

        if (cmd is "qa" or "qa!" or "qall" or "qall!")
            return new ExResult(true, null, VimEvent.QuitRequested(cmd.EndsWith('!')));

        // :w [file] :write [file]
        if (TryParseWriteCommand(cmd, out var writePath))
        {
            if (_bufferManager.Current.IsBinary && string.IsNullOrWhiteSpace(writePath))
                return new ExResult(false, "E21: Cannot write a binary file (read-only)");
            var buf = _bufferManager.Current;
            var targetPath = string.IsNullOrWhiteSpace(writePath) ? buf.FilePath : writePath;
            if (string.IsNullOrWhiteSpace(targetPath))
            {
                // Delegate unnamed-buffer saves to UI so it can prompt with SaveFileDialog.
                return new ExResult(true, null, VimEvent.SaveRequested(null));
            }
            return new ExResult(true, $"\"{targetPath}\" written", VimEvent.SaveRequested(targetPath));
        }

        // :e[!] [file] :edit[!] [file]
        if (cmd == "e" || cmd == "e!" || cmd == "edit" || cmd == "edit!" ||
            cmd.StartsWith("e ") || cmd.StartsWith("e! ") || cmd.StartsWith("edit ") || cmd.StartsWith("edit! "))
        {
            bool bang = cmd.StartsWith("e!") || cmd.StartsWith("edit!");
            string rest = cmd switch
            {
                _ when cmd.StartsWith("edit! ") => cmd[6..].Trim(),
                _ when cmd.StartsWith("edit ")  => cmd[5..].Trim(),
                _ when cmd.StartsWith("e! ")    => cmd[3..].Trim(),
                _ when cmd.StartsWith("e ")     => cmd[2..].Trim(),
                _ => ""
            };

            if (string.IsNullOrWhiteSpace(rest))
            {
                // :e / :e! with no argument — reload current file
                var buf = _bufferManager.Current;
                if (string.IsNullOrEmpty(buf.FilePath))
                    return new ExResult(false, "No file name");
                if (!bang && buf.Text.IsModified)
                    return new ExResult(false, "No write since last change (add ! to override)");
                return new ExResult(true, null, VimEvent.ReloadFileRequested(bang));
            }

            // :e [file] / :e! [file] — open a different file
            if (!bang && _bufferManager.Current.Text.IsModified)
                return new ExResult(false, "No write since last change (add ! to override)");
            return new ExResult(true, null, VimEvent.OpenFileRequested(rest));
        }

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

        // :Format — format document via LSP
        if (cmd is "Format" or "format")
            return new ExResult(true, null, VimEvent.FormatDocumentRequested());

        // :Rename [newname] — LSP workspace rename
        if (cmd.Equals("Rename", StringComparison.OrdinalIgnoreCase) ||
            cmd.StartsWith("Rename ", StringComparison.OrdinalIgnoreCase))
        {
            var newName = cmd.Length > 7 ? cmd[7..].Trim() : null;
            if (string.IsNullOrEmpty(newName)) newName = null;
            return new ExResult(true, null, VimEvent.LspRenameRequested(newName));
        }

        // :Symbols [query] / :sym [query] / :outline — list document or workspace symbols
        // With a query argument: workspace/symbol search. Without: document symbols outline.
        if (cmd.StartsWith("Symbols", StringComparison.OrdinalIgnoreCase) ||
            cmd.StartsWith("sym", StringComparison.OrdinalIgnoreCase) ||
            cmd.Equals("outline", StringComparison.OrdinalIgnoreCase))
        {
            var spaceIdx = cmd.IndexOf(' ');
            var query = spaceIdx >= 0 ? cmd[(spaceIdx + 1)..].Trim() : null;
            if (string.IsNullOrEmpty(query)) query = null;
            return new ExResult(true, null, VimEvent.SymbolsRequested(query));
        }

        // :CallHierarchy — LSP call hierarchy for symbol under cursor
        if (cmd.Equals("CallHierarchy", StringComparison.OrdinalIgnoreCase))
            return new ExResult(true, null, VimEvent.CallHierarchyRequested());

        // :TypeHierarchy — LSP type hierarchy for symbol under cursor
        if (cmd.Equals("TypeHierarchy", StringComparison.OrdinalIgnoreCase))
            return new ExResult(true, null, VimEvent.TypeHierarchyRequested());

        // :Git blame / :Gblame — toggle inline git blame annotations
        if (cmd is "Git blame" or "git blame" or "Gblame" or "gblame")
            return new ExResult(true, null, VimEvent.GitBlameRequested());

        // :Git status / :Gstatus / :gs — show repository status in a new buffer
        if (cmd is "Git status" or "git status" or "Gstatus" or "gstatus" or "gs")
            return new ExResult(true, null, VimEvent.GitStatusRequested());

        // :Git commit / :Gcommit — open commit message editor
        if (cmd is "Git commit" or "git commit" or "Gcommit" or "gcommit")
            return new ExResult(true, null, VimEvent.GitCommitRequested());

        // :Git stage / :Gstage — stage the current git hunk
        if (cmd is "Git stage" or "git stage" or "Gstage" or "gstage")
            return new ExResult(true, null, VimEvent.HunkStageRequested(true));

        // :Git unstage / :Gunstage — unstage the current git hunk
        if (cmd is "Git unstage" or "git unstage" or "Gunstage" or "gunstage")
            return new ExResult(true, null, VimEvent.HunkStageRequested(false));

        // :Git diff / :Gdiff — show git diff output in a new buffer
        if (cmd is "Git diff" or "git diff" or "Gdiff" or "gdiff")
            return new ExResult(true, null, VimEvent.GitDiffRequested());

        // :Git log / :Glog — show git log output in a new buffer
        if (cmd is "Git log" or "git log" or "Glog" or "glog")
            return new ExResult(true, null, VimEvent.GitLogRequested());

        // :Git push / :Gpush — push the current branch
        if (cmd is "Git push" or "git push" or "Gpush" or "gpush")
            return new ExResult(true, null, VimEvent.GitPushRequested());

        // :Git pull / :Gpull — pull into the current branch
        if (cmd is "Git pull" or "git pull" or "Gpull" or "gpull")
            return new ExResult(true, null, VimEvent.GitPullRequested());

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

        // :diagnostics — request workspace diagnostics via LSP
        if (cmd.Equals("diagnostics", StringComparison.OrdinalIgnoreCase) ||
            cmd.Equals("diag", StringComparison.OrdinalIgnoreCase))
            return new ExResult(true, null, VimEvent.WorkspaceDiagnosticsRequested());

        // :Lsp / :LspList — show the configured extension→language-server table
        if (cmd.Equals("Lsp", StringComparison.OrdinalIgnoreCase) ||
            cmd.Equals("LspList", StringComparison.OrdinalIgnoreCase))
            return new ExResult(true, FormatLspServers());

        // :LspAdd <ext> <executable> [args...] — register/replace the server for an extension
        if (cmd.StartsWith("LspAdd ", StringComparison.OrdinalIgnoreCase))
            return ExecuteLspAdd(cmd[7..]);

        // :LspRemove <ext> — remove a custom server, or hide a built-in one
        if (cmd.StartsWith("LspRemove ", StringComparison.OrdinalIgnoreCase))
            return ExecuteLspRemove(cmd[10..]);

        // :LspReset <ext> — drop user changes for an extension, restoring the built-in default
        if (cmd.StartsWith("LspReset ", StringComparison.OrdinalIgnoreCase))
            return ExecuteLspReset(cmd[9..]);

        // :Fmt / :FmtList — show the configured extension→CLI-formatter table
        if (cmd.Equals("Fmt", StringComparison.OrdinalIgnoreCase) ||
            cmd.Equals("FmtList", StringComparison.OrdinalIgnoreCase))
            return new ExResult(true, FormatFormatters());

        // :FmtSet <ext> <executable> [args...] — register/replace the CLI formatter for an extension
        if (cmd.StartsWith("FmtSet ", StringComparison.OrdinalIgnoreCase))
            return ExecuteFmtSet(cmd[7..]);

        // :FmtRemove <ext> — drop the configured formatter for an extension
        if (cmd.StartsWith("FmtRemove ", StringComparison.OrdinalIgnoreCase))
            return ExecuteFmtRemove(cmd[10..]);

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

        // :s/pattern/replace/flags (substitute)
        if (cmd.StartsWith("s/") || cmd.StartsWith("s!"))
        {
            return ExecuteSubstitute(cmd, range, cursor);
        }
        if (range.Length > 0 && (cmd.StartsWith("s/") || cmd.StartsWith("s!")))
        {
            return ExecuteSubstitute(cmd, range, cursor);
        }

        // :g/pattern/cmd  :g!/pattern/cmd  :v/pattern/cmd  :global  :vglobal
        if (IsGlobalCommand(cmd, out bool globalInverse, out string globalRest))
            return ExecuteGlobal(globalRest, globalInverse, range, cursor);

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

        // :[range]sort[!] [i] [n] [r] [/pat/]
        if (cmd == "sort" || (cmd.StartsWith("sort") && cmd.Length > 4 && cmd[4] is ' ' or '!'))
            return ExecuteSort(cmd, range, cursor);

        // :[range]retab[!] [N]
        if (cmd == "retab" || cmd.StartsWith("retab ") || cmd.StartsWith("retab!"))
            return ExecuteRetab(cmd, range);

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

        // :call Name(...) — execute a function defined while sourcing Vimscript.
        if (cmd == "call" || cmd.StartsWith("call "))
            return ExecuteFunctionCall(cmd, cursor);

        // Multi-line Vimscript functions are evaluated by VimConfig when
        // sourcing vimrc files. Interactive definition is intentionally minimal.
        if (cmd == "function" || cmd.StartsWith("function "))
            return new ExResult(false, "E126: Missing :endfunction");
        if (cmd == "endfunction")
            return new ExResult(false, "E193: :endfunction not inside a function");

        // Multi-line Vimscript conditionals are evaluated by VimConfig when
        // sourcing vimrc files. Interactive one-line input is not stateful.
        if (cmd == "if" || cmd.StartsWith("if "))
            return new ExResult(false, "E580: :endif missing");
        if (cmd == "else")
            return new ExResult(false, "E581: :else without :if");
        if (cmd == "endif")
            return new ExResult(false, "E580: :endif without :if");

        // Multi-line Vimscript loops are evaluated by VimConfig when sourcing
        // vimrc files. Interactive one-line input is not stateful.
        if (cmd == "for" || cmd.StartsWith("for "))
            return new ExResult(false, "E170: Missing :endfor");
        if (cmd == "endfor")
            return new ExResult(false, "E588: :endfor without :for");

        // :changes
        if (cmd == "changes")
            return new ExResult(true, _markManager.FormatChangeList());

        // :undolist — display the current linear undo/redo history
        if (cmd == "undolist")
            return new ExResult(true, _bufferManager.Current.Undo.FormatUndoList());

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

        // :let {var} = {expr} — assign a simple Vimscript variable.
        // :let with no argument lists currently stored variables.
        if (cmd == "let" || cmd.StartsWith("let "))
            return ExecuteLet(cmd);

        // :echo {expr} / :echomsg {expr} — print message
        if (cmd.StartsWith("echo ") || cmd == "echo" ||
            cmd.StartsWith("echomsg ") || cmd == "echomsg")
        {
            string expr;
            if (cmd.StartsWith("echomsg "))
                expr = cmd[8..].Trim();
            else if (cmd == "echomsg")
                expr = "";
            else
                expr = cmd.Length > 5 ? cmd[5..].Trim() : "";
            return new ExResult(true, EvalExpr(expr));
        }

        // :execute {expr} — evaluate string and run as Ex command
        if (cmd.StartsWith("execute ") || cmd == "execute")
        {
            if (_executeDepth >= 20)
                return new ExResult(false, "E169: Command too recursive");
            var expr = cmd.Length > 8 ? cmd[8..].Trim() : "";
            var resolved = EvalExpr(expr);
            if (string.IsNullOrEmpty(resolved))
                return new ExResult(false, "E471: Argument required");
            _executeDepth++;
            try { return Execute(resolved, cursor); }
            finally { _executeDepth--; }
        }

        // :[range]yank [reg] — yank lines to register
        if (cmd is "y" or "yank" || cmd.StartsWith("y ") || cmd.StartsWith("yank "))
        {
            char regName = '"';
            var parts = cmd.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 1 && parts[1].Length == 1 && char.IsLetter(parts[1][0]))
                regName = char.ToLower(parts[1][0]);

            var buf = _bufferManager.Current.Text;
            int startLine = cursor.Line, endLine = cursor.Line;
            if (!string.IsNullOrEmpty(range))
                ResolveRange(range, cursor, buf.LineCount, ref startLine, ref endLine);

            if (_registerManager == null)
                return new ExResult(false, "No register manager available");

            var text = string.Join("\n", buf.GetLines(startLine, endLine));
            _registerManager.SetYank(regName, new Register(text, RegisterType.Line));
            return new ExResult(true, $"{endLine - startLine + 1} line(s) yanked");
        }

        // :[line]put [reg] — paste register after line
        if (cmd is "pu" or "put" || cmd.StartsWith("pu ") || cmd.StartsWith("put "))
        {
            char regName = '"';
            var parts = cmd.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 1 && parts[1].Length == 1 && char.IsLetter(parts[1][0]))
                regName = char.ToLower(parts[1][0]);

            if (_registerManager == null)
                return new ExResult(false, "No register manager available");

            var reg = _registerManager.Get(regName);
            if (string.IsNullOrEmpty(reg.Text))
                return new ExResult(false, $"Nothing in register {regName}");

            var buf = _bufferManager.Current.Text;
            int insertAfter = cursor.Line;
            if (!string.IsNullOrEmpty(range))
            {
                int rStart = cursor.Line, rEnd = cursor.Line;
                ResolveRange(range, cursor, buf.LineCount, ref rStart, ref rEnd);
                insertAfter = rEnd;
            }

            var pasteLines = reg.Text.Split('\n');
            buf.InsertLines(insertAfter, pasteLines);

            return new ExResult(true, null, null, TextModified: true);
        }

        // :registers [names]  :reg [names] — display register contents
        if (cmd == "registers" || cmd == "reg" ||
            cmd.StartsWith("registers ") || cmd.StartsWith("reg "))
        {
            return ExecuteRegisters(cmd);
        }

        // :marks [names] — display marks
        if (cmd == "marks" || cmd.StartsWith("marks "))
        {
            return ExecuteMarks(cmd);
        }

        // :delmarks {marks} / :delmarks! — delete marks
        if (cmd is "delmarks" or "delm" or "delmarks!" or "delm!" ||
            cmd.StartsWith("delmarks ") || cmd.StartsWith("delm "))
        {
            return ExecuteDelmarks(cmd);
        }

        return new ExResult(false, $"Not an editor command: {cmd}");
    }

    private static bool IsGlobalCommand(string cmd, out bool inverse, out string rest)
    {
        inverse = false;
        rest = "";
        if (cmd.StartsWith("global!", StringComparison.Ordinal))      { inverse = true;  rest = cmd[7..]; return true; }
        if (cmd.StartsWith("global", StringComparison.Ordinal))        { inverse = false; rest = cmd[6..]; return true; }
        if (cmd.StartsWith("vglobal", StringComparison.Ordinal))       { inverse = true;  rest = cmd[7..]; return true; }
        if (cmd.Length >= 2 && cmd[0] == 'g' && cmd[1] == '!')        { inverse = true;  rest = cmd[2..]; return true; }
        if (cmd.Length >= 2 && cmd[0] == 'g' && !char.IsLetterOrDigit(cmd[1])) { inverse = false; rest = cmd[1..]; return true; }
        if (cmd.Length >= 2 && cmd[0] == 'v' && !char.IsLetterOrDigit(cmd[1])) { inverse = true;  rest = cmd[1..]; return true; }
        return false;
    }

    private ExResult ExecuteGlobal(string rest, bool inverse, string range, CursorPosition cursor)
    {
        if (rest.Length == 0)
            return new ExResult(false, "E148: Regular expression missing from global");

        char delim = rest[0];
        var secondDelim = rest.IndexOf(delim, 1);
        if (secondDelim < 1)
            return new ExResult(false, "E148: Regular expression missing from global");

        string pattern = rest[1..secondDelim];
        string subCmd = secondDelim < rest.Length - 1 ? rest[(secondDelim + 1)..].Trim() : "";
        if (string.IsNullOrEmpty(subCmd)) subCmd = "p";

        var regex = TryBuildRegex(pattern, _options.IgnoreCase, out var patternError);
        if (regex == null) return new ExResult(false, patternError);

        var buf = _bufferManager.Current.Text;
        int startLine = 0, endLine = buf.LineCount - 1;
        // :g defaults to whole file; only restrict when an explicit range was given
        if (!string.IsNullOrEmpty(range))
            ResolveRange(range, cursor, buf.LineCount, ref startLine, ref endLine);

        // Collect matching lines (indices snapshot before any mutation)
        var matchingLines = new List<int>();
        for (int l = startLine; l <= endLine && l < buf.LineCount; l++)
        {
            bool matches = regex.IsMatch(buf.GetLine(l));
            if (matches != inverse)
                matchingLines.Add(l);
        }

        if (matchingLines.Count == 0)
            return new ExResult(false, "Pattern not found");

        // ── delete ──────────────────────────────────────────────────────────
        if (subCmd is "d" or "delete" or "d!" or "delete!")
        {
            // Batch contiguous groups into single DeleteLines calls (from bottom up)
            for (int i = matchingLines.Count - 1; i >= 0; )
            {
                int hi = i;
                while (i > 0 && matchingLines[i - 1] == matchingLines[i] - 1) i--;
                buf.DeleteLines(matchingLines[i], matchingLines[hi]);
                i--;
            }
            return new ExResult(true, $"{matchingLines.Count} line(s) deleted", TextModified: true);
        }

        // ── print ────────────────────────────────────────────────────────────
        if (subCmd is "p" or "print")
        {
            var lines = matchingLines.Select(l => buf.GetLine(l));
            return new ExResult(true, string.Join("  |  ", lines));
        }

        // ── substitute ───────────────────────────────────────────────────────
        if (subCmd.StartsWith("s/", StringComparison.Ordinal) ||
            subCmd.Length >= 2 && subCmd[0] == 's' && !char.IsLetterOrDigit(subCmd[1]))
        {
            int totalSubs = 0;
            // Process top-to-bottom; substitute doesn't change line count
            foreach (int l in matchingLines)
            {
                var lineRange = $"{l + 1},{l + 1}";
                var res = ExecuteSubstitute(subCmd, lineRange, new CursorPosition(l, 0));
                if (res.Success && res.Message?.Contains("substitution") == true)
                    totalSubs++;
            }
            return new ExResult(true, totalSubs > 0 ? $"{totalSubs} substitution(s) made" : "No matches", TextModified: totalSubs > 0);
        }

        return new ExResult(false, $"Not supported in :global: {subCmd}");
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

    private static Regex? TryBuildRegex(string pattern, bool ignoreCase, out string? error)
    {
        error = null;
        var opts = ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None;
        try { return new Regex(VimRegex.ToDotNetPattern(pattern), opts); }
        catch (Exception ex) { error = $"Invalid pattern: {ex.Message}"; return null; }
    }

    internal static void ResolveRange(string range, CursorPosition cursor, int lineCount, ref int startLine, ref int endLine)
    {
        if (range == "%") { startLine = 0; endLine = lineCount - 1; }
        else if (range == "." || range == "") { startLine = endLine = cursor.Line; }
        else
        {
            var parts = range.Split(',');
            if (parts.Length == 2 &&
                int.TryParse(parts[0], out var s) &&
                int.TryParse(parts[1], out var e))
            { startLine = s - 1; endLine = e - 1; }
            else if (parts.Length == 1 && int.TryParse(parts[0], out var n))
            { startLine = endLine = n - 1; }
        }
    }

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

    private ExResult ExecuteSubstitute(string cmd, string range, CursorPosition cursor)
    {
        char sep = cmd[1];
        var parts = cmd[2..].Split(sep);
        if (parts.Length < 2) return new ExResult(false, "Invalid substitution");

        string pattern = parts[0];
        string replacement = parts.Length > 1 ? parts[1] : "";
        string flags = parts.Length > 2 ? parts[2] : "";

        bool global = flags.Contains('g');
        bool ignoreCase = flags.Contains('i') || (!flags.Contains('I') && _options.IgnoreCase);
        bool confirm = flags.Contains('c');

        var regex = TryBuildRegex(pattern, ignoreCase, out var patternError);
        if (regex == null) return new ExResult(false, patternError);

        var buf = _bufferManager.Current.Text;
        int count = 0;

        int startLine = 0, endLine = buf.LineCount - 1;
        ResolveRange(range, cursor, buf.LineCount, ref startLine, ref endLine);

        for (int l = startLine; l <= endLine && l < buf.LineCount; l++)
        {
            var line = buf.GetLine(l);
            var newLine = global
                ? regex.Replace(line, replacement)
                : regex.Replace(line, replacement, 1);
            if (newLine != line)
            {
                buf.ReplaceLine(l, newLine);
                count++;
            }
        }

        return new ExResult(true, count > 0 ? $"{count} substitution(s) made" : "No matches", TextModified: count > 0);
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

    // Extract the optional argument after a command verb: "reg ab" → "ab", "marks" → ""
    private static string GetCommandArg(string cmd)
    {
        var idx = cmd.IndexOf(' ');
        return idx >= 0 ? cmd[(idx + 1)..].Trim() : "";
    }

    private ExResult ExecuteRegisters(string cmd)
    {
        if (_registerManager == null)
            return new ExResult(false, "No register manager available");

        var filter = GetCommandArg(cmd);
        var allRegs = _registerManager.GetAll();
        if (allRegs.Count == 0)
            return new ExResult(true, "--- Registers ---\n(empty)");

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("--- Registers ---");
        bool any = false;

        foreach (var (name, reg) in allRegs)
        {
            if (filter.Length > 0 && !filter.Contains(name))
                continue;

            // Represent newlines as ^J (Vim-compatible)
            var display = reg.Text.Replace("\n", "^J");
            if (display.Length > 200) display = display[..200] + "…";
            var typeLabel = reg.Type switch { RegisterType.Line => "l", RegisterType.Block => "b", _ => "c" };
            sb.AppendLine($"\"{name}   {typeLabel}   {display}");
            any = true;
        }

        if (!any) sb.AppendLine("(no matches)");
        return new ExResult(true, sb.ToString().TrimEnd());
    }

    private ExResult ExecuteMarks(string cmd)
    {
        var filter = GetCommandArg(cmd);
        var allMarks = _markManager.GetAllMarks();
        if (allMarks.Count == 0)
            return new ExResult(true, "mark  line  col  text\n(no marks set)");

        var buf = _bufferManager.Current.Text;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("mark  line  col  text");
        bool any = false;

        foreach (var (name, pos) in allMarks)
        {
            if (filter.Length > 0 && !filter.Contains(name))
                continue;

            var lineText = pos.Line >= 0 && pos.Line < buf.LineCount
                ? buf.GetLine(pos.Line).TrimStart()
                : "";
            if (lineText.Length > 50) lineText = lineText[..50] + "…";
            sb.AppendLine($" {name}  {pos.Line + 1,5}  {pos.Column,3}  {lineText}");
            any = true;
        }

        if (!any) sb.AppendLine("(no matches)");
        return new ExResult(true, sb.ToString().TrimEnd());
    }

    private ExResult ExecuteDelmarks(string cmd)
    {
        if (cmd is "delmarks!" or "delm!")
        {
            _markManager.ClearMarks();
            return new ExResult(true, "All marks deleted");
        }

        var arg = GetCommandArg(cmd);
        if (string.IsNullOrWhiteSpace(arg))
            return new ExResult(false, "E471: Argument required");

        if (!TryParseMarkList(arg, out var marks, out var error))
            return new ExResult(false, error);

        int deleted = 0;
        foreach (var mark in marks)
        {
            if (_markManager.DeleteMark(mark))
                deleted++;
        }

        return new ExResult(true, $"{deleted} mark(s) deleted");
    }

    private static bool TryParseMarkList(string arg, out IReadOnlyList<char> marks, out string? error)
    {
        var result = new List<char>();
        error = null;

        for (int i = 0; i < arg.Length; i++)
        {
            var ch = arg[i];
            if (char.IsWhiteSpace(ch))
                continue;

            if (!IsValidMarkName(ch))
            {
                marks = [];
                error = $"E475: Invalid argument: {arg}";
                return false;
            }

            if (i + 2 < arg.Length && arg[i + 1] == '-' && IsValidMarkName(arg[i + 2]))
            {
                var end = arg[i + 2];
                if (ch > end)
                {
                    marks = [];
                    error = $"E475: Invalid argument: {arg}";
                    return false;
                }

                for (char mark = ch; mark <= end; mark++)
                {
                    if (IsValidMarkName(mark))
                        result.Add(mark);
                }
                i += 2;
                continue;
            }

            result.Add(ch);
        }

        marks = result.Distinct().ToList();
        return true;
    }

    private static bool IsValidMarkName(char mark)
    {
        return char.IsLetter(mark) || mark is '<' or '>' or '.' or '\'';
    }

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

    private ExResult ExecuteFunctionCall(string cmd, CursorPosition cursor)
    {
        if (!TryParseCallCommand(cmd, out var functionName, out var argumentExpressions))
            return new ExResult(false, "E476: Invalid command");

        if (!_functions.TryGetValue(functionName, out var function))
            return new ExResult(false, "E117: Unknown function: " + functionName);

        if (_functionCallDepth >= MaxFunctionCallDepth)
            return new ExResult(false, "E132: Function call depth is higher than 'maxfuncdepth'");

        if (argumentExpressions.Count < function.Parameters.Count)
            return new ExResult(false, "E119: Not enough arguments for function: " + functionName);
        if (argumentExpressions.Count > function.Parameters.Count)
            return new ExResult(false, "E118: Too many arguments for function: " + functionName);

        var boundVariables = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["a:0"] = _variables.TryGetValue("a:0", out var previousCount) ? previousCount : null
        };

        var evaluatedArgs = argumentExpressions.Select(EvalExpr).ToArray();
        for (var i = 0; i < function.Parameters.Count; i++)
        {
            var scopedName = "a:" + function.Parameters[i];
            boundVariables[scopedName] = _variables.TryGetValue(scopedName, out var previous) ? previous : null;
            _variables[scopedName] = evaluatedArgs[i];
        }
        _variables["a:0"] = evaluatedArgs.Length.ToString();

        _functionCallDepth++;
        try
        {
            var iterationBudget = MaxForIterations;
            return ExecuteFunctionBlock(function.Body, 0, function.Body.Count, cursor, ref iterationBudget).Result;
        }
        finally
        {
            _functionCallDepth--;
            foreach (var (name, previousValue) in boundVariables)
            {
                if (previousValue == null)
                    _variables.Remove(name);
                else
                    _variables[name] = previousValue;
            }
        }
    }

    private (int NextIndex, ExResult Result) ExecuteFunctionBlock(
        IReadOnlyList<string> lines,
        int start,
        int end,
        CursorPosition cursor,
        ref int iterationBudget)
    {
        var index = start;
        ExResult lastResult = new(true);

        while (index < end)
        {
            var line = lines[index];
            if (line.Length == 0)
            {
                index++;
                continue;
            }

            if (IsFunctionBlockTerminator(line))
                return (index, lastResult);

            if (TryParseIfCommand(line, out var ifExpression))
            {
                var branch = ExecuteFunctionIfBlock(lines, index, end, ifExpression, cursor, ref iterationBudget);
                if (!branch.Result.Success)
                    return branch;
                lastResult = branch.Result;
                index = branch.NextIndex;
                continue;
            }

            if (TryParseForCommand(line, out var loopVariable, out var listExpression))
            {
                var loop = ExecuteFunctionForBlock(lines, index, end, loopVariable, listExpression, cursor, ref iterationBudget);
                if (!loop.Result.Success)
                    return loop;
                lastResult = loop.Result;
                index = loop.NextIndex;
                continue;
            }

            lastResult = ExecuteNoHistory(line, cursor);
            if (!lastResult.Success)
                return (index + 1, lastResult);

            index++;
        }

        return (index, lastResult);
    }

    private (int NextIndex, ExResult Result) ExecuteFunctionIfBlock(
        IReadOnlyList<string> lines,
        int ifIndex,
        int end,
        string expression,
        CursorPosition cursor,
        ref int iterationBudget)
    {
        var (elseIndex, endifIndex) = FindIfBlock(lines, ifIndex, end);
        var blockEnd = endifIndex >= 0 ? endifIndex : end;
        var result = new ExResult(true);

        if (EvaluateCondition(expression))
        {
            var trueEnd = elseIndex >= 0 ? elseIndex : blockEnd;
            result = ExecuteFunctionBlock(lines, ifIndex + 1, trueEnd, cursor, ref iterationBudget).Result;
        }
        else if (elseIndex >= 0)
        {
            result = ExecuteFunctionBlock(lines, elseIndex + 1, blockEnd, cursor, ref iterationBudget).Result;
        }

        return (endifIndex >= 0 ? endifIndex + 1 : end, result);
    }

    private (int NextIndex, ExResult Result) ExecuteFunctionForBlock(
        IReadOnlyList<string> lines,
        int forIndex,
        int end,
        string variableName,
        string listExpression,
        CursorPosition cursor,
        ref int iterationBudget)
    {
        var endforIndex = FindForBlockEnd(lines, forIndex, end);
        if (endforIndex < 0)
            return (end, new ExResult(false, "E170: Missing :endfor"));

        if (!IsValidVariableName(variableName) ||
            !TryParseListLiteral(listExpression, out var items))
            return (endforIndex + 1, new ExResult(true));

        ExResult lastResult = new(true);
        foreach (var item in items.Take(MaxForListItems))
        {
            if (iterationBudget <= 0)
                break;

            iterationBudget--;
            _variables[variableName] = item;
            lastResult = ExecuteFunctionBlock(lines, forIndex + 1, endforIndex, cursor, ref iterationBudget).Result;
            if (!lastResult.Success)
                return (endforIndex + 1, lastResult);
        }

        return (endforIndex + 1, lastResult);
    }

    private ExResult ExecuteLet(string cmd)
    {
        var rest = cmd.Length > 3 ? cmd[3..].Trim() : "";
        if (rest.Length == 0)
        {
            if (_variables.Count == 0)
                return new ExResult(true, "(no variables)");

            var lines = _variables
                .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kv => $"{kv.Key} = {FormatVariableValue(kv.Value)}");
            return new ExResult(true, string.Join('\n', lines));
        }

        var eqIdx = rest.IndexOf('=');
        if (eqIdx < 0)
        {
            var name = rest.Trim();
            return _variables.TryGetValue(name, out var value)
                ? new ExResult(true, $"{name} = {FormatVariableValue(value)}")
                : new ExResult(false, $"E121: Undefined variable: {name}");
        }

        var varName = rest[..eqIdx].Trim();
        var expr = rest[(eqIdx + 1)..].Trim();
        if (!IsValidVariableName(varName))
            return new ExResult(false, $"E461: Illegal variable name: {varName}");

        _variables[varName] = EvalExpr(expr);
        return new ExResult(true);
    }

    private static bool IsValidVariableName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var bare = name.Length > 2 && name[1] == ':' ? name[2..] : name;
        if (bare.Length == 0 || !(char.IsLetter(bare[0]) || bare[0] == '_')) return false;
        return bare.All(ch => char.IsLetterOrDigit(ch) || ch == '_');
    }

    private static string FormatVariableValue(string value) =>
        int.TryParse(value, out _) ? value : $"\"{value}\"";

    private static bool TryParseCallCommand(string cmd, out string name, out IReadOnlyList<string> arguments)
    {
        name = "";
        arguments = [];

        if (!cmd.StartsWith("call", StringComparison.OrdinalIgnoreCase) ||
            (cmd.Length > 4 && !char.IsWhiteSpace(cmd[4])))
            return false;

        var rest = cmd.Length > 4 ? cmd[4..].Trim() : "";
        var openParen = rest.IndexOf('(');
        if (openParen <= 0 || !rest.EndsWith(')'))
            return false;

        name = rest[..openParen].Trim();
        var rawArguments = rest[(openParen + 1)..^1].Trim();
        if (rawArguments.Length == 0)
            return true;

        if (!TrySplitCommaSeparated(rawArguments, out var parts))
            return false;

        arguments = parts.Select(p => p.Trim()).ToArray();
        return arguments.All(arg => arg.Length > 0);
    }

    private static bool TryParseIfCommand(string line, out string expression)
    {
        expression = "";
        if (line.Length < 2 ||
            !line[..2].Equals("if", StringComparison.OrdinalIgnoreCase) ||
            (line.Length > 2 && !char.IsWhiteSpace(line[2])))
            return false;

        expression = line.Length > 2 ? line[2..].Trim() : "";
        return true;
    }

    private static bool TryParseForCommand(string line, out string variableName, out string listExpression)
    {
        variableName = "";
        listExpression = "";

        if (line.Length < 3 ||
            !line[..3].Equals("for", StringComparison.OrdinalIgnoreCase) ||
            (line.Length > 3 && !char.IsWhiteSpace(line[3])))
            return false;

        var rest = line.Length > 3 ? line[3..].TrimStart() : "";
        var variableEnd = 0;
        while (variableEnd < rest.Length && !char.IsWhiteSpace(rest[variableEnd]))
            variableEnd++;

        if (variableEnd == 0)
            return false;

        var inStart = variableEnd;
        while (inStart < rest.Length && char.IsWhiteSpace(rest[inStart]))
            inStart++;

        if (inStart + 2 > rest.Length ||
            !rest.AsSpan(inStart, 2).Equals("in", StringComparison.OrdinalIgnoreCase))
            return false;

        var expressionStart = inStart + 2;
        if (expressionStart < rest.Length && !char.IsWhiteSpace(rest[expressionStart]))
            return false;

        while (expressionStart < rest.Length && char.IsWhiteSpace(rest[expressionStart]))
            expressionStart++;

        if (expressionStart >= rest.Length)
            return false;

        variableName = rest[..variableEnd].Trim();
        listExpression = rest[expressionStart..].Trim();
        return true;
    }

    private static (int ElseIndex, int EndifIndex) FindIfBlock(IReadOnlyList<string> lines, int ifIndex, int end)
    {
        var depth = 0;
        var elseIndex = -1;

        for (var index = ifIndex + 1; index < end; index++)
        {
            var line = lines[index];
            if (line.Length == 0)
                continue;

            if (TryParseIfCommand(line, out _))
            {
                depth++;
                continue;
            }

            if (line.Equals("endif", StringComparison.OrdinalIgnoreCase))
            {
                if (depth == 0)
                    return (elseIndex, index);

                depth--;
                continue;
            }

            if (depth == 0 &&
                elseIndex < 0 &&
                line.Equals("else", StringComparison.OrdinalIgnoreCase))
            {
                elseIndex = index;
            }
        }

        return (elseIndex, -1);
    }

    private static int FindForBlockEnd(IReadOnlyList<string> lines, int forIndex, int end)
    {
        var depth = 0;

        for (var index = forIndex + 1; index < end; index++)
        {
            var line = lines[index];
            if (line.Length == 0)
                continue;

            if (TryParseForCommand(line, out _, out _))
            {
                depth++;
                continue;
            }

            if (line.Equals("endfor", StringComparison.OrdinalIgnoreCase))
            {
                if (depth == 0)
                    return index;

                depth--;
            }
        }

        return -1;
    }

    private bool EvaluateCondition(string expr)
    {
        expr = expr.Trim();
        if (expr.Length == 0)
            return false;

        if (expr[0] == '!')
            return !EvaluateCondition(expr[1..]);

        if (expr.Length >= 2 &&
            ((expr[0] == '"' && expr[^1] == '"') ||
             (expr[0] == '\'' && expr[^1] == '\'')))
            return IsTruthy(StripQuotes(expr));

        if (TryGetVariable(expr, out var variableValue))
            return IsTruthy(variableValue);

        if (ExpressionEvaluator.Evaluate(expr) is { } arithmeticValue)
            return IsTruthy(arithmeticValue);

        if (int.TryParse(expr, out var n))
            return n != 0;

        return false;
    }

    private static bool TryParseListLiteral(string expression, out List<string> items)
    {
        items = [];
        expression = expression.Trim();
        if (expression.Length < 2 || expression[0] != '[' || expression[^1] != ']')
            return false;

        var content = expression[1..^1].Trim();
        if (content.Length == 0)
            return true;

        if (!TrySplitCommaSeparated(content, out var rawItems, rejectNestedListBrackets: true))
            return false;

        foreach (var rawItem in rawItems)
        {
            var item = rawItem.Trim();
            if (item.Length == 0)
                return false;

            if (item.Length >= 2 &&
                ((item[0] == '"' && item[^1] == '"') ||
                 (item[0] == '\'' && item[^1] == '\'')))
            {
                if (items.Count < MaxForListItems)
                    items.Add(StripQuotes(item));
                continue;
            }

            var evaluated = ExpressionEvaluator.Evaluate(item);
            if (evaluated == null)
                return false;

            if (items.Count < MaxForListItems)
                items.Add(evaluated);
        }

        return true;
    }

    private static bool TrySplitCommaSeparated(
        string content,
        out List<string> items,
        bool rejectNestedListBrackets = false)
    {
        items = [];
        var start = 0;
        char quote = '\0';
        var escaped = false;

        for (var index = 0; index < content.Length; index++)
        {
            var ch = content[index];
            if (quote != '\0')
            {
                if (quote == '"' && escaped)
                {
                    escaped = false;
                    continue;
                }

                if (quote == '"' && ch == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (quote == '\'' && ch == '\'' && index + 1 < content.Length && content[index + 1] == '\'')
                {
                    index++;
                    continue;
                }

                if (ch == quote)
                    quote = '\0';
                continue;
            }

            if (ch is '\'' or '"')
            {
                quote = ch;
                continue;
            }

            if (rejectNestedListBrackets && ch is '[' or ']')
                return false;

            if (ch == ',')
            {
                items.Add(content[start..index]);
                start = index + 1;
            }
        }

        if (quote != '\0' || escaped)
            return false;

        items.Add(content[start..]);
        return true;
    }

    private static bool IsFunctionBlockTerminator(string line) =>
        line.Equals("else", StringComparison.OrdinalIgnoreCase) ||
        line.Equals("endif", StringComparison.OrdinalIgnoreCase) ||
        line.Equals("endfor", StringComparison.OrdinalIgnoreCase);

    private static bool IsTruthy(string value)
    {
        value = value.Trim();
        if (value.Length == 0)
            return false;

        if (double.TryParse(value, out var number))
            return number != 0;

        if (bool.TryParse(value, out var boolean))
            return boolean;

        return false;
    }

    private static readonly Regex NumericSortRegex = new(@"-?\d+", RegexOptions.Compiled);

    private ExResult ExecuteSort(string cmd, string range, CursorPosition cursor)
    {
        // Extract "!" from the command verb itself (like :grep! does), not from the flag list
        var arg = cmd.Length > 4 ? cmd[4..].TrimStart() : "";
        bool reverse = arg.StartsWith('!');
        if (reverse) arg = arg[1..].TrimStart();

        bool ignoreCase = false, numeric = false, sortOnMatch = false;
        while (arg.Length > 0 && arg[0] is 'i' or 'n' or 'r')
        {
            switch (arg[0])
            {
                case 'i': ignoreCase = true; break;
                case 'n': numeric = true; break;
                case 'r': sortOnMatch = true; break;
            }
            arg = arg[1..].TrimStart();
        }

        Regex? keyRegex = null;
        if (arg.StartsWith('/'))
        {
            var closeSlash = arg.IndexOf('/', 1);
            var pat = closeSlash > 0 ? arg[1..closeSlash] : arg[1..];
            if (!string.IsNullOrEmpty(pat))
            {
                keyRegex = TryBuildRegex(pat, ignoreCase, out var patErr);
                if (keyRegex == null) return new ExResult(false, patErr);
            }
        }

        var buf = _bufferManager.Current.Text;
        int startLine = 0, endLine = buf.LineCount - 1;
        if (!string.IsNullOrEmpty(range))
            ResolveRange(range, cursor, buf.LineCount, ref startLine, ref endLine);
        if (startLine > endLine) return new ExResult(false, "Invalid range");

        // GetLines clamps indices internally
        var lines = buf.GetLines(startLine, endLine);

        string SortKey(string line)
        {
            if (keyRegex == null) return line;
            var m = keyRegex.Match(line);
            if (!m.Success) return "";
            return sortOnMatch ? m.Value : line[(m.Index + m.Length)..];
        }

        string[] result;
        if (numeric)
        {
            long NumKey(string line) { var m = NumericSortRegex.Match(SortKey(line)); return m.Success ? long.Parse(m.Value) : 0L; }
            result = reverse
                ? [.. lines.OrderByDescending(NumKey)]
                : [.. lines.OrderBy(NumKey)];
        }
        else
        {
            var comparer = ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            result = reverse
                ? [.. lines.OrderByDescending(SortKey, comparer)]
                : [.. lines.OrderBy(SortKey, comparer)];
        }

        for (int i = 0; i < result.Length; i++)
            buf.ReplaceLine(startLine + i, result[i]);

        return new ExResult(true, $"{result.Length} line(s) sorted", TextModified: true);
    }

    private ExResult ExecuteRetab(string cmd, string range)
    {
        // retab[!] [N]: convert tabs<->spaces
        // retab  → expand tabs to spaces using tabstop
        // retab! → compress spaces to tabs where possible
        bool toTabs = cmd.StartsWith("retab!");
        string rest = cmd[(toTabs ? 6 : 5)..].Trim();
        int tabWidth = rest.Length > 0 && int.TryParse(rest, out int n) && n > 0 ? n : _options.TabStop;

        var buf = _bufferManager.Current.Text;
        int startLine = 0, endLine = buf.LineCount - 1;
        ResolveRange(range, new CursorPosition(0, 0), buf.LineCount, ref startLine, ref endLine);

        for (int i = startLine; i <= endLine; i++)
        {
            var line = buf.GetLine(i);
            string newLine;
            if (toTabs)
            {
                // Replace runs of spaces with tabs
                var sb = new System.Text.StringBuilder();
                int col = 0;
                int j = 0;
                while (j < line.Length)
                {
                    if (line[j] == ' ')
                    {
                        int spaceStart = j;
                        while (j < line.Length && line[j] == ' ') { j++; col++; }
                        int spaces = j - spaceStart;
                        int tabs = spaces / tabWidth;
                        int rem = spaces % tabWidth;
                        sb.Append('\t', tabs);
                        sb.Append(' ', rem);
                    }
                    else if (line[j] == '\t')
                    {
                        sb.Append('\t');
                        col = (col / tabWidth + 1) * tabWidth;
                        j++;
                    }
                    else
                    {
                        sb.Append(line[j]);
                        col++;
                        j++;
                    }
                }
                newLine = sb.ToString();
            }
            else
            {
                // Expand tabs to spaces
                var sb = new System.Text.StringBuilder();
                int col = 0;
                foreach (char ch in line)
                {
                    if (ch == '\t')
                    {
                        int spaces = tabWidth - (col % tabWidth);
                        sb.Append(' ', spaces);
                        col += spaces;
                    }
                    else
                    {
                        sb.Append(ch);
                        col++;
                    }
                }
                newLine = sb.ToString();
            }
            buf.ReplaceLine(i, newLine);
        }

        int count = endLine - startLine + 1;
        return new ExResult(true, $"Retabbed {count} line(s)", TextModified: true);
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
        "undolist", "earlier", "later",
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

    // ── :Lsp* — manage the extension→language-server table ─────────────────────

    private string FormatLspServers()
    {
        var entries = _lspRegistry.List();
        if (entries.Count == 0)
            return "(no language servers configured)";

        var lines = entries.Select(e =>
        {
            var args = e.Server.Args.Length > 0 ? " " + string.Join(' ', e.Server.Args) : "";
            var origin = e.Origin switch
            {
                LspServerOrigin.Custom   => "custom",
                LspServerOrigin.Removed  => "removed",
                _                        => "built-in",
            };
            // A hidden built-in is shown struck so the user can see what :LspReset would restore.
            var cmd = e.Origin == LspServerOrigin.Removed ? $"(disabled: {e.Server.Executable}{args})" : $"{e.Server.Executable}{args}";
            return $"  {e.Extension,-10} {cmd}  [{origin}]";
        });
        return "LSP servers (extension → command):\n" + string.Join('\n', lines);
    }

    private ExResult ExecuteLspAdd(string rest)
    {
        var parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
            return new ExResult(false, "Usage: :LspAdd <ext> <executable> [args...]");

        var ext = LspServerRegistry.NormalizeExt(parts[0]);
        if (ext.Length == 0)
            return new ExResult(false, "E474: invalid extension");

        var executable = parts[1];
        var args = parts.Length > 2 ? parts[2..] : [];
        // languageId is derived from the extension (".cs" → "cs"); servers key off the executable, not this id.
        var languageId = ext.TrimStart('.');
        _lspRegistry.Set(ext, new LspServerDef(executable, args, languageId));

        var argsText = args.Length > 0 ? " " + string.Join(' ', args) : "";
        return new ExResult(true, $"LSP: {ext} → {executable}{argsText} (reopen the file to apply)");
    }

    private ExResult ExecuteLspRemove(string rest)
    {
        var ext = LspServerRegistry.NormalizeExt(rest.Trim());
        if (ext.Length == 0)
            return new ExResult(false, "Usage: :LspRemove <ext>");

        return _lspRegistry.Remove(ext)
            ? new ExResult(true, $"LSP: removed {ext} (reopen the file to apply)")
            : new ExResult(false, $"LSP: no server configured for {ext}");
    }

    private ExResult ExecuteLspReset(string rest)
    {
        var ext = LspServerRegistry.NormalizeExt(rest.Trim());
        if (ext.Length == 0)
            return new ExResult(false, "Usage: :LspReset <ext>");

        return _lspRegistry.Reset(ext)
            ? new ExResult(true, $"LSP: reset {ext} to its built-in default (reopen the file to apply)")
            : new ExResult(false, $"LSP: nothing to reset for {ext}");
    }

    // ── :Fmt* — manage the extension→CLI-formatter table (used by :Format) ─────

    private static string FormatFormatters()
    {
        var entries = FormatterRegistry.Default.List();
        if (entries.Count == 0)
            return "(no formatters configured) — :FmtSet <ext> <cmd>, or run :Format to auto-detect";

        var lines = entries.Select(e =>
        {
            var args = e.Def.Args.Length > 0 ? " " + string.Join(' ', e.Def.Args) : "";
            return $"  {e.Extension,-10} {e.Def.Executable}{args}";
        });
        return "Formatters (extension → command, stdin→stdout):\n" + string.Join('\n', lines);
    }

    private static ExResult ExecuteFmtSet(string rest)
    {
        var parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
            return new ExResult(false, "Usage: :FmtSet <ext> <executable> [args...]  (use {file} for the path)");

        var ext = FormatterRegistry.NormalizeExt(parts[0]);
        if (ext.Length == 0)
            return new ExResult(false, "E474: invalid extension");

        var executable = parts[1];
        var args = parts.Length > 2 ? parts[2..] : [];
        FormatterRegistry.Default.Set(ext, new FormatterDef(executable, args));

        var argsText = args.Length > 0 ? " " + string.Join(' ', args) : "";
        return new ExResult(true, $"Format: {ext} → {executable}{argsText}");
    }

    private static ExResult ExecuteFmtRemove(string rest)
    {
        var ext = FormatterRegistry.NormalizeExt(rest.Trim());
        if (ext.Length == 0)
            return new ExResult(false, "Usage: :FmtRemove <ext>");

        return FormatterRegistry.Default.Remove(ext)
            ? new ExResult(true, $"Format: removed {ext}")
            : new ExResult(false, $"Format: no formatter configured for {ext}");
    }

    private static string MakeRelative(string basePath, string fullPath)
    {
        try { return Path.GetRelativePath(basePath, fullPath); }
        catch { return fullPath; }
    }

    // ── Expression evaluator for :echo / :echomsg / :execute ───────────────

    private string EvalExpr(string expr)
    {
        expr = expr.Trim();

        // String literal: "..." or '...'
        if (expr.Length >= 2 &&
            ((expr[0] == '"' && expr[^1] == '"') ||
             (expr[0] == '\'' && expr[^1] == '\'')))
            return StripQuotes(expr);

        // expand('%'), expand('%:t'), expand('%:h'), expand('%:r'), expand('%:e')
        if (expr.StartsWith("expand(", StringComparison.Ordinal))
            return EvalExpand(expr);

        // strftime('fmt') / strftime("fmt")
        if (expr.StartsWith("strftime(", StringComparison.Ordinal))
            return EvalStrftime(expr);

        if (TryGetVariable(expr, out var variableValue))
            return variableValue;

        if (ExpressionEvaluator.Evaluate(expr) is { } arithmeticValue)
            return arithmeticValue;

        // Numeric literal
        if (int.TryParse(expr, out var n)) return n.ToString();

        // Bare word / unrecognised — return as-is
        return expr;
    }

    private bool TryGetVariable(string expr, out string value)
    {
        if (_variables.TryGetValue(expr, out value!))
            return true;

        if (!expr.Contains(':') && _variables.TryGetValue("g:" + expr, out value!))
            return true;

        value = "";
        return false;
    }

    private string EvalExpand(string expr)
    {
        // expr like: expand('%') or expand('%:t') or expand('%:h') etc.
        var inner = ExtractFunctionArg(expr, "expand");
        if (inner == null) return expr;

        inner = StripQuotes(inner);

        var filePath = _bufferManager.Current.FilePath ?? "";

        return inner switch
        {
            "%"    => filePath,
            "%:t"  => Path.GetFileName(filePath),
            "%:h"  => Path.GetDirectoryName(filePath) ?? "",
            "%:r"  => Path.Combine(
                          Path.GetDirectoryName(filePath) ?? "",
                          Path.GetFileNameWithoutExtension(filePath)),
            "%:e"  => Path.GetExtension(filePath).TrimStart('.'),
            "%:p"  => string.IsNullOrEmpty(filePath) ? filePath : Path.GetFullPath(filePath),
            _      => filePath
        };
    }

    private static string EvalStrftime(string expr)
    {
        // expr like: strftime('%Y-%m-%d') or strftime("%H:%M")
        var fmt = ExtractFunctionArg(expr, "strftime");
        if (fmt == null) return expr;

        fmt = StripQuotes(fmt);

        // Convert strftime format specifiers to .NET equivalents
        fmt = fmt
            .Replace("%Y", "yyyy")
            .Replace("%y", "yy")
            .Replace("%m", "MM")
            .Replace("%d", "dd")
            .Replace("%H", "HH")
            .Replace("%M", "mm")
            .Replace("%S", "ss")
            .Replace("%A", "dddd")
            .Replace("%a", "ddd")
            .Replace("%B", "MMMM")
            .Replace("%b", "MMM")
            .Replace("%I", "hh")
            .Replace("%p", "tt");

        try { return DateTime.Now.ToString(fmt); }
        catch { return fmt; }
    }

    private static string? ExtractFunctionArg(string expr, string funcName)
    {
        // funcName + '(' ... ')'
        var prefix = funcName + "(";
        if (!expr.StartsWith(prefix, StringComparison.Ordinal)) return null;
        if (!expr.EndsWith(')')) return null;
        return expr[prefix.Length..^1].Trim();
    }

    private static string StripQuotes(string s) =>
        s.Length >= 2 && ((s[0] == '"' && s[^1] == '"') || (s[0] == '\'' && s[^1] == '\''))
            ? s[1..^1]
            : s;

    private static bool TryParseWriteCommand(string cmd, out string? path)
    {
        path = null;
        var trimmed = cmd.Trim();
        if (trimmed.Length == 0) return false;

        var firstSpace = trimmed.IndexOf(' ');
        var token = firstSpace >= 0 ? trimmed[..firstSpace] : trimmed;
        var remainder = firstSpace >= 0 ? trimmed[(firstSpace + 1)..].Trim() : null;

        var baseToken = token.EndsWith('!') ? token[..^1] : token;
        if (baseToken != "w" && baseToken != "write")
            return false;

        path = string.IsNullOrWhiteSpace(remainder) ? null : remainder;
        return true;
    }
}
