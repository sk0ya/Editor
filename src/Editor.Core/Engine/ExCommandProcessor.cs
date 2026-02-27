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
            return new ExResult(true, null, VimEvent.QuitRequested(false));
        }
        if (cmd is "q!" or "quit!")
            return new ExResult(true, null, VimEvent.QuitRequested(true));

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
            try { buf.Save(targetPath); return new ExResult(true, $"\"{targetPath}\" written"); }
            catch (Exception ex) { return new ExResult(false, ex.Message); }
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

        // :split :vsplit
        if (cmd == "split" || cmd == "sp" || cmd == "new")
            return new ExResult(true, null, VimEvent.SplitRequested(false));
        if (cmd == "vsplit" || cmd == "vs" || cmd == "vnew")
            return new ExResult(true, null, VimEvent.SplitRequested(true));

        // :number (go to line)
        if (int.TryParse(cmd, out var lineNum))
        {
            var line = Math.Clamp(lineNum - 1, 0, _bufferManager.Current.Text.LineCount - 1);
            return new ExResult(true, null, VimEvent.CursorMoved(new CursorPosition(line, 0)));
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
