using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Editor.Core.Buffer;
using Editor.Core.Config;
using Editor.Core.Marks;
using Editor.Core.Models;
using Editor.Core;

namespace Editor.Core.Engine;

public record ExResult(bool Success, string? Message = null, VimEvent? Event = null, bool TextModified = false);

public class ExCommandProcessor
{
    private readonly BufferManager _bufferManager;
    private readonly VimOptions _options;
    private readonly MarkManager _markManager;
    private readonly Dictionary<string, string> _abbreviations;
    private readonly List<string> _history = [];
    private int _historyIndex = -1;

    public ExCommandProcessor(BufferManager bufferManager, VimOptions options, MarkManager markManager,
        Dictionary<string, string>? abbreviations = null)
    {
        _bufferManager = bufferManager;
        _options = options;
        _markManager = markManager;
        _abbreviations = abbreviations ?? [];
    }

    public string? LastCommand => _history.Count > 0 ? _history[0] : null;

    public void AddHistory(string cmd)
    {
        if (!string.IsNullOrWhiteSpace(cmd))
        {
            _history.Remove(cmd);
            _history.Insert(0, cmd);
            if (_history.Count > _options.History)
                _history.RemoveAt(_history.Count - 1);
        }
        _historyIndex = -1;
    }

    public string? HistoryPrev()
    {
        if (_history.Count == 0) return null;
        _historyIndex = Math.Min(_historyIndex + 1, _history.Count - 1);
        return _history[_historyIndex];
    }

    public string? HistoryNext()
    {
        if (_historyIndex <= 0) { _historyIndex = -1; return ""; }
        _historyIndex--;
        return _history[_historyIndex];
    }

    public ExResult Execute(string cmdLine, CursorPosition cursor)
    {
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

        // :nmap :imap :vmap :nnoremap :inoremap :vnoremap
        if (cmd.StartsWith("nmap ") || cmd.StartsWith("nnoremap "))
        {
            return new ExResult(true, "Key mapping registered");
        }

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

        // :Git blame / :Gblame — toggle inline git blame annotations
        if (cmd is "Git blame" or "git blame" or "Gblame" or "gblame")
            return new ExResult(true, null, VimEvent.GitBlameRequested());

        // Quickfix commands
        if (cmd is "copen" or "cope" or "clist" or "cl")
            return new ExResult(true, null, VimEvent.QuickfixOpen());
        if (cmd is "cclose" or "ccl")
            return new ExResult(true, null, VimEvent.QuickfixClose());
        if (TryParseQuickfixNav(cmd, "cn", "cnext", out var cnCount))
            return new ExResult(true, null, VimEvent.QuickfixNext(cnCount));
        if (TryParseQuickfixNav(cmd, "cp", "cprev", out var cpCount) ||
            TryParseQuickfixNav(cmd, "cp", "cprevious", out cpCount))
            return new ExResult(true, null, VimEvent.QuickfixPrev(cpCount));
        if (TryParseQuickfixGoto(cmd, out var ccIndex))
            return new ExResult(true, null, VimEvent.QuickfixGoto(ccIndex));

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

        // :changes
        if (cmd == "changes")
            return new ExResult(true, _markManager.FormatChangeList());

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

    private static Regex? TryBuildRegex(string pattern, bool ignoreCase, out string? error)
    {
        error = null;
        var opts = ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None;
        try { return new Regex(pattern, opts); }
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
                var after = rest[(closeSlash + 1)..].Trim();
                var spaceIdx = after.IndexOf(' ');
                var flags = spaceIdx >= 0 ? after[..spaceIdx] : after;
                var globPart = spaceIdx >= 0 ? after[(spaceIdx + 1)..].Trim() : "";
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
                var after = rest[(closeSlash + 1)..].Trim();
                var spaceIdx = after.IndexOf(' ');
                var flags = spaceIdx >= 0 ? after[..spaceIdx] : after;
                var globPart = spaceIdx >= 0 ? after[(spaceIdx + 1)..].Trim() : "";
                ignoreCase = flags.Contains('i');
                fileGlob = string.IsNullOrEmpty(globPart) ? null : globPart;
                return;
            }
        }

        // Fallback: no delimiter → treat whole thing as pattern
        pattern = rest;
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
        "Format", "Rename", "digraphs",
        "read", "r",
        "Git blame", "Gblame",
        "copen", "cope", "clist", "cl",
        "cclose", "ccl",
        "cn", "cnext", "cp", "cprev",
        "sort",
        "move", "m", "copy", "co", "t",
        "center", "right", "left",
        "normal", "norm", "normal!", "norm!",
        "g", "global", "v", "vglobal",
        "grep", "vimgrep",
        "nmap", "nnoremap", "imap", "inoremap", "vmap", "vnoremap",
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

    private static string MakeRelative(string basePath, string fullPath)
    {
        try { return Path.GetRelativePath(basePath, fullPath); }
        catch { return fullPath; }
    }

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
