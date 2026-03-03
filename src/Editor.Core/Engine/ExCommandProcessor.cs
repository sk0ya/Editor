using System.IO;
using System.Text.RegularExpressions;
using Editor.Core.Buffer;
using Editor.Core.Config;
using Editor.Core.Models;

namespace Editor.Core.Engine;

public record ExResult(bool Success, string? Message = null, VimEvent? Event = null);

public class ExCommandProcessor
{
    private readonly BufferManager _bufferManager;
    private readonly VimOptions _options;
    private readonly List<string> _history = [];
    private int _historyIndex = -1;

    public ExCommandProcessor(BufferManager bufferManager, VimOptions options)
    {
        _bufferManager = bufferManager;
        _options = options;
    }

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

        // :e [file] :edit [file]
        if (cmd == "e" || cmd == "edit" || cmd.StartsWith("e ") || cmd.StartsWith("edit "))
        {
            var path = cmd.StartsWith("edit ", StringComparison.Ordinal)
                ? cmd[5..].Trim()
                : cmd.StartsWith("e ", StringComparison.Ordinal)
                    ? cmd[2..].Trim()
                    : null;
            if (string.IsNullOrWhiteSpace(path)) return new ExResult(false, "No file name");
            return new ExResult(true, null, VimEvent.OpenFileRequested(path));
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

        return new ExResult(false, $"Not an editor command: {cmd}");
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

        var regexOpts = ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None;
        Regex regex;
        try { regex = new Regex(pattern, regexOpts); }
        catch (Exception ex) { return new ExResult(false, $"Invalid pattern: {ex.Message}"); }

        var buf = _bufferManager.Current.Text;
        int count = 0;

        int startLine = 0, endLine = buf.LineCount - 1;
        if (range == "%") { startLine = 0; endLine = buf.LineCount - 1; }
        else if (range == "." || range == "") { startLine = endLine = cursor.Line; }
        else
        {
            var rangeParts = range.Split(',');
            if (rangeParts.Length == 2 &&
                int.TryParse(rangeParts[0], out var s) &&
                int.TryParse(rangeParts[1], out var e))
            { startLine = s - 1; endLine = e - 1; }
        }

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

        return new ExResult(true, count > 0 ? $"{count} substitution(s) made" : "No matches");
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

    // ─────────────── TAB COMPLETION ───────────────

    private static readonly string[] AllCommandNames =
    [
        "q", "quit", "q!", "quit!",
        "wq", "wq!", "x", "x!", "xit", "exit",
        "qa", "qa!", "qall", "qall!",
        "w", "write",
        "e", "edit",
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
        "Format", "Rename",
        "Git blame", "Gblame",
        "copen", "cope", "clist", "cl",
        "cclose", "ccl",
        "cn", "cnext", "cp", "cprev",
        "grep", "vimgrep",
        "nmap", "nnoremap", "imap", "inoremap", "vmap", "vnoremap",
    ];

    private static readonly HashSet<string> FileCommands = new(StringComparer.Ordinal)
        { "e", "edit", "w", "write", "split", "sp", "vsplit", "vs", "new", "vnew", "tabnew", "tabedit", "tabe" };

    private static readonly string[] KnownColorschemes = ["dracula", "dark", "light"];

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
