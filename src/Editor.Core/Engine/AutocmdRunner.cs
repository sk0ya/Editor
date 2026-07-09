using System.IO;
using Editor.Core.Config;
using Editor.Core.Models;

namespace Editor.Core.Engine;

/// <summary>
/// Owns <c>autocmd</c> execution and the vimrc-style config commands that can appear
/// both on the <c>:</c> command line and inside an autocmd body (<c>set</c>,
/// <c>colorscheme</c>, <c>syntax</c>, <c>nmap</c>/…, <c>let mapleader</c>,
/// <c>augroup</c>/<c>autocmd</c>). Recursion is bounded by an internal depth guard.
/// Takes the current cursor as a callback so <c>echo</c>/<c>echomsg</c> autocmds can
/// run through <see cref="ExCommandProcessor"/>.
/// </summary>
public sealed class AutocmdRunner(
    VimConfig config,
    ExCommandProcessor exProcessor,
    Func<CursorPosition> getCursor)
{
    private int _autocmdDepth;

    public bool TryExecuteConfigCommand(string cmdLine, out string? message, out string? error, out bool optionsChanged)
    {
        message = null;
        error = null;
        optionsChanged = false;

        var cmd = cmdLine.Trim();
        if (!IsConfigCommand(cmd))
            return false;

        error = config.ParseCommand(cmd);
        if (error != null)
            return true;

        if (IsMapCommand(cmd))
            message = "Key mapping registered";
        else if (cmd.Equals("autocmd", StringComparison.OrdinalIgnoreCase) ||
                 cmd.Equals("au", StringComparison.OrdinalIgnoreCase))
            message = config.Autocmds.Format();
        else if (cmd.StartsWith("autocmd ", StringComparison.OrdinalIgnoreCase) ||
                 cmd.StartsWith("au ", StringComparison.OrdinalIgnoreCase))
            message = cmd.Contains('!') ? "Autocommands cleared" : "Autocommand registered";
        else if (cmd.StartsWith("colorscheme ", StringComparison.OrdinalIgnoreCase))
            message = $"colorscheme: {config.Options.ColorScheme}";
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
            IsMapLeaderLetCommand(cmd) ||
            cmd.StartsWith("augroup", StringComparison.OrdinalIgnoreCase) ||
            cmd.StartsWith("autocmd", StringComparison.OrdinalIgnoreCase) ||
            cmd.Equals("au", StringComparison.OrdinalIgnoreCase) ||
            cmd.StartsWith("au ", StringComparison.OrdinalIgnoreCase) ||
            IsMapCommand(cmd);
    }

    private static bool IsMapLeaderLetCommand(string cmd)
    {
        if (!cmd.StartsWith("let ", StringComparison.OrdinalIgnoreCase))
            return false;

        var rest = cmd[3..].Trim();
        var eqIdx = rest.IndexOf('=');
        if (eqIdx < 0)
            return rest.Equals("mapleader", StringComparison.OrdinalIgnoreCase);

        var name = rest[..eqIdx].Trim();
        return name.Equals("mapleader", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMapCommand(string cmd) =>
        cmd.StartsWith("nmap ", StringComparison.OrdinalIgnoreCase) ||
        cmd.StartsWith("imap ", StringComparison.OrdinalIgnoreCase) ||
        cmd.StartsWith("vmap ", StringComparison.OrdinalIgnoreCase) ||
        cmd.StartsWith("nnoremap ", StringComparison.OrdinalIgnoreCase) ||
        cmd.StartsWith("inoremap ", StringComparison.OrdinalIgnoreCase) ||
        cmd.StartsWith("vnoremap ", StringComparison.OrdinalIgnoreCase);

    public void RunAutocmds(string eventName, string filePathOrType) =>
        RunAutocmds(eventName, [filePathOrType]);

    public void RunAutocmds(string eventName, IEnumerable<string> filePathOrTypes)
    {
        if (_autocmdDepth > 8)
            return;

        _autocmdDepth++;
        try
        {
            var autocmds = filePathOrTypes
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .SelectMany(v => config.Autocmds.Match(eventName, v))
                .Distinct()
                .ToArray();

            foreach (var autocmd in autocmds)
                ExecuteAutocmdCommand(autocmd.Command);
        }
        finally
        {
            _autocmdDepth--;
        }
    }

    private void ExecuteAutocmdCommand(string command)
    {
        foreach (var part in command.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (TryExecuteConfigCommand(part, out _, out _, out _))
                continue;

            if (part.StartsWith("echo ", StringComparison.OrdinalIgnoreCase) ||
                part.StartsWith("echomsg ", StringComparison.OrdinalIgnoreCase))
            {
                exProcessor.Execute(part, getCursor());
            }
        }
    }

    public static string[] GetFileTypeNames(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext switch
        {
            ".cs" => ["cs", "csharp"],
            ".py" => ["python"],
            ".xml" => ["xml"],
            ".md" or ".markdown" => ["markdown"],
            ".js" or ".jsx" => ["javascript"],
            ".ts" or ".tsx" => ["typescript"],
            ".rs" => ["rust"],
            ".json" => ["json"],
            ".toml" => ["toml"],
            ".yaml" or ".yml" => ["yaml"],
            ".sh" or ".bash" or ".zsh" or ".fish" => ["sh", "shell"],
            ".css" => ["css"],
            ".sql" => ["sql"],
            ".c" => ["c"],
            ".cpp" or ".cc" or ".cxx" or ".hpp" or ".hh" => ["cpp", "c"],
            ".h" => ["c", "cpp"],
            ".go" => ["go"],
            ".bat" or ".cmd" => ["dosbatch", "batch"],
            ".ps1" or ".psm1" or ".psd1" => ["ps1", "powershell"],
            _ => [],
        };
    }
}
