using Editor.Core.Buffer;
using Editor.Core.Config;
using Editor.Core.Models;

namespace Editor.Core.Engine.ExCommands;

/// <summary>Handles the :q/:quit/:wq/:w/:write/:e/:edit family of ex commands.</summary>
public class FileOpsCommands(BufferManager bufferManager, VimOptions options)
{
    public bool TryHandle(string cmd, out ExResult result)
    {
        // :q :q! :quit :quit! :wq :x :xit :exit
        if (cmd is "q" or "quit")
        {
            result = bufferManager.Current.Text.IsModified
                ? new ExResult(false, "No write since last change (add ! to override)")
                : new ExResult(true, null, VimEvent.WindowCloseRequested(false));
            return true;
        }
        if (cmd is "q!" or "quit!")
        {
            result = new ExResult(true, null, VimEvent.WindowCloseRequested(true));
            return true;
        }

        if (cmd is "wq" or "wq!" or "x" or "x!" or "xit" or "exit")
        {
            if (bufferManager.Current.IsBinary)
            {
                result = new ExResult(false, "E21: Cannot write a binary file (read-only)");
                return true;
            }
            var buf = bufferManager.Current;
            try { buf.Save(); }
            catch (Exception ex) { result = new ExResult(false, ex.Message); return true; }
            result = new ExResult(true, null, VimEvent.QuitRequested(false));
            return true;
        }

        if (cmd is "qa" or "qa!" or "qall" or "qall!")
        {
            result = new ExResult(true, null, VimEvent.QuitRequested(cmd.EndsWith('!')));
            return true;
        }

        // :w [file] :write [file]
        if (TryParseWriteCommand(cmd, out var writePath))
        {
            if (bufferManager.Current.IsBinary && string.IsNullOrWhiteSpace(writePath))
            {
                result = new ExResult(false, "E21: Cannot write a binary file (read-only)");
                return true;
            }
            var buf = bufferManager.Current;
            var targetPath = string.IsNullOrWhiteSpace(writePath) ? buf.FilePath : writePath;
            if (string.IsNullOrWhiteSpace(targetPath))
            {
                // Delegate unnamed-buffer saves to UI so it can prompt with SaveFileDialog.
                result = new ExResult(true, null, VimEvent.SaveRequested(null));
                return true;
            }
            result = new ExResult(true, $"\"{targetPath}\" written", VimEvent.SaveRequested(targetPath));
            return true;
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
                var buf = bufferManager.Current;
                if (string.IsNullOrEmpty(buf.FilePath))
                {
                    result = new ExResult(false, "No file name");
                    return true;
                }
                if (!bang && buf.Text.IsModified)
                {
                    result = new ExResult(false, "No write since last change (add ! to override)");
                    return true;
                }
                result = new ExResult(true, null, VimEvent.ReloadFileRequested(bang));
                return true;
            }

            // :e [file] / :e! [file] — open a different file. Switching buffers never loses
            // data here (BufferManager keeps the old buffer around, reachable via :bn/:bp), so
            // when 'hidden' is on this is allowed without '!' — matching real Vim's semantics
            // where 'hidden' lets you leave a modified buffer without writing it.
            if (!bang && !options.Hidden && bufferManager.Current.Text.IsModified)
            {
                result = new ExResult(false, "No write since last change (add ! to override)");
                return true;
            }
            result = new ExResult(true, null, VimEvent.OpenFileRequested(rest));
            return true;
        }

        result = default!;
        return false;
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
