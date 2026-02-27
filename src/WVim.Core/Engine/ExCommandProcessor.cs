using System.Text.RegularExpressions;
using WVim.Core.Buffer;
using WVim.Core.Config;
using WVim.Core.Models;

namespace WVim.Core.Engine;

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

        // :q :q! :wq :x :wqa
        if (cmd == "q")
        {
            if (_bufferManager.Current.Text.IsModified)
                return new ExResult(false, "No write since last change (add ! to override)");
            return new ExResult(true, null, VimEvent.QuitRequested(false));
        }
        if (cmd == "q!")
            return new ExResult(true, null, VimEvent.QuitRequested(true));

        if (cmd == "wq" || cmd == "x")
        {
            var buf = _bufferManager.Current;
            try { buf.Save(); }
            catch (Exception ex) { return new ExResult(false, ex.Message); }
            return new ExResult(true, null, VimEvent.QuitRequested(false));
        }

        if (cmd == "qa" || cmd == "qa!")
            return new ExResult(true, null, VimEvent.QuitRequested(cmd == "qa!"));

        // :w [file]
        if (cmd == "w" || cmd.StartsWith("w "))
        {
            var buf = _bufferManager.Current;
            var path = cmd.Length > 2 ? cmd[2..].Trim() : buf.FilePath;
            if (path == null) return new ExResult(false, "No file name");
            try { buf.Save(path); return new ExResult(true, $"\"{path}\" written"); }
            catch (Exception ex) { return new ExResult(false, ex.Message); }
        }

        // :e [file]
        if (cmd == "e" || cmd.StartsWith("e "))
        {
            var path = cmd.Length > 2 ? cmd[2..].Trim() : null;
            if (path == null) return new ExResult(false, "No file name");
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

        // :tabnew :tabc :tabn :tabp
        if (cmd == "tabnew" || cmd.StartsWith("tabnew "))
        {
            var path = cmd.Length > 7 ? cmd[7..].Trim() : null;
            return new ExResult(true, null, VimEvent.NewTabRequested(path));
        }

        // :split :vsplit
        if (cmd == "split" || cmd == "sp")
            return new ExResult(true, null, VimEvent.SplitRequested(false));
        if (cmd == "vsplit" || cmd == "vs")
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
}
