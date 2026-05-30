using Editor.Core.Engine;
using Editor.Core.Buffer;
using Editor.Core.Snippets;

namespace Editor.Core.Config;

public class VimConfig
{
    private const int ModelineScanLineCount = 5;

    public VimOptions Options { get; } = new();
    public Dictionary<string, string> NormalMaps { get; } = [];
    public Dictionary<string, string> InsertMaps { get; } = [];
    public Dictionary<string, string> VisualMaps { get; } = [];
    public Dictionary<string, string> Abbreviations { get; } = [];
    public Dictionary<string, string> Variables { get; } = new(StringComparer.OrdinalIgnoreCase);
    public SnippetManager Snippets { get; } = new();
    public VimAutocmdRegistry Autocmds { get; } = new();
    public IReadOnlyList<string> ScriptNames => _scriptNames.AsReadOnly();

    // The mapleader character (default backslash, set by `let mapleader=...`)
    public string Leader { get; private set; } = "\\";
    private string? _currentAugroup;
    private readonly List<string> _scriptNames = [];
    private readonly HashSet<string> _activeScriptPaths = new(StringComparer.OrdinalIgnoreCase);
    private string? _currentScriptDirectory;

    public static VimConfig LoadFromFile(string path)
    {
        var cfg = new VimConfig();
        cfg.ParseFile(path);
        return cfg;
    }

    public static VimConfig LoadDefault()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var vimrcPath = Path.Combine(home, ".vimrc");
        if (!File.Exists(vimrcPath))
            vimrcPath = Path.Combine(home, "_vimrc");
        if (!File.Exists(vimrcPath))
            vimrcPath = Path.Combine(home, "init.vim");

        var cfg = new VimConfig();
        cfg.ParseFile(vimrcPath);

        // Allow project-local overrides when running from a workspace.
        var localVimrcPath = Path.Combine(Environment.CurrentDirectory, ".vimrc");
        if (File.Exists(localVimrcPath))
            cfg.ParseFile(localVimrcPath);

        return cfg;
    }

    public void RecordScript(string path)
    {
        var fullPath = Path.GetFullPath(path);
        _scriptNames.Add(fullPath);
    }

    public void ParseLines(IEnumerable<string> lines)
    {
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith('"')) continue;

            // Remove inline comments: only strip " when preceded by whitespace
            // (avoids cutting strings like let mapleader="\<Space>")
            var commentIdx = -1;
            for (int i = 1; i < line.Length; i++)
            {
                if (line[i] == '"' && char.IsWhiteSpace(line[i - 1]))
                {
                    commentIdx = i;
                    break;
                }
            }
            if (commentIdx > 0) line = line[..commentIdx].Trim();

            ParseCommand(line);
        }
    }

    public string? ParseCommand(string cmd)
    {
        if (cmd.StartsWith("set ", StringComparison.OrdinalIgnoreCase))
        {
            // `set` can take multiple space-separated options on one line
            var args = cmd[4..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (var arg in args)
                Options.Apply(arg);
            return null;
        }

        // let mapleader = "..." or let mapleader = '\<Space>'
        if (IsMapLeaderLetCommand(cmd))
        {
            Leader = ParseLeader(cmd);
            Variables["mapleader"] = Leader;
            return null;
        }

        if (TryParseLetAssignment(cmd, out var varName, out var varValue, out var letError))
        {
            if (letError != null) return letError;
            if (varName != null && varValue != null)
                Variables[varName] = varValue;
            return null;
        }

        // Map commands — strip modifier flags (<silent>, <nowait>, <buffer>, <expr>, <unique>)
        // before splitting into LHS/RHS.
        if (TryParseMapCommand(cmd, out var dict, out var rest))
        {
            ParseMap(dict!, rest!, Leader);
            return null;
        }

        // :iab / :iabbrev / :abbreviate / :ab — add abbreviation
        if (TryParseAbbrevCommand(cmd, out var abbrevLhs, out var abbrevRhs))
        {
            if (abbrevLhs != null && abbrevRhs != null)
                Abbreviations[abbrevLhs] = abbrevRhs;
            return null;
        }

        // :iunabbrev / :iuna / :unabbreviate / :una — remove abbreviation
        if (TryParseUnabbrevCommand(cmd, out var unabbrevLhs))
        {
            if (unabbrevLhs != null)
                Abbreviations.Remove(unabbrevLhs);
            return null;
        }

        // :snippet {trigger} {body} — define a user snippet
        if (cmd.StartsWith("snippet ", StringComparison.OrdinalIgnoreCase))
        {
            var snippetArgs = cmd[8..].Trim();
            var snippetSpace = snippetArgs.IndexOf(' ');
            if (snippetSpace > 0)
            {
                var trigger = snippetArgs[..snippetSpace].Trim();
                var body = snippetArgs[(snippetSpace + 1)..]; // preserve body as-is (may contain \n)
                if (!string.IsNullOrEmpty(trigger))
                    Snippets.Register(trigger, body);
            }
            return null;
        }

        // :unsnippet {trigger} — remove a user snippet
        if (cmd.StartsWith("unsnippet ", StringComparison.OrdinalIgnoreCase))
        {
            Snippets.Unregister(cmd[10..].Trim());
            return null;
        }

        if (TryParseAugroupCommand(cmd))
            return null;

        if (TryParseAutocmdCommand(cmd, out var autocmdError))
            return autocmdError;

        if (cmd.StartsWith("colorscheme ", StringComparison.OrdinalIgnoreCase))
        {
            Options.ColorScheme = cmd[12..].Trim();
            return null;
        }
        if (cmd.StartsWith("syntax ", StringComparison.OrdinalIgnoreCase))
        {
            Options.Syntax = cmd[7..].Trim().Equals("on", StringComparison.OrdinalIgnoreCase);
            return null;
        }
        if (cmd.StartsWith("source ", StringComparison.OrdinalIgnoreCase) ||
            cmd.StartsWith("so ", StringComparison.OrdinalIgnoreCase))
        {
            var sourcePath = cmd[(cmd.IndexOf(' ') + 1)..].Trim();
            if (sourcePath.Length > 0)
            {
                var resolved = ResolveScriptPath(sourcePath);
                ParseFile(resolved);
            }
            return null;
        }

        // Silently ignore: augroup, autocmd, filetype, scriptencoding, tnoremap, onoremap,
        // cnoremap, cmap, function, endfunction, if, endif, etc.
        return null;
    }

    public void ApplyModelines(VimBuffer buffer)
    {
        if (!Options.Modeline)
            return;

        foreach (var line in GetModelineCandidateLines(buffer.Text))
        {
            if (!TryExtractModelineOptions(line, out var settings))
                continue;

            foreach (var setting in settings.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                Options.Apply(setting);
        }

        buffer.FileFormat = Options.FileFormat;
        buffer.FileEncoding = Options.FileEncoding;
    }

    private void ParseFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath) || !_activeScriptPaths.Add(fullPath))
            return;

        RecordScript(fullPath);

        var previousDirectory = _currentScriptDirectory;
        _currentScriptDirectory = Path.GetDirectoryName(fullPath);
        try
        {
            ParseLines(File.ReadAllLines(fullPath));
        }
        finally
        {
            _currentScriptDirectory = previousDirectory;
            _activeScriptPaths.Remove(fullPath);
        }
    }

    private static IEnumerable<string> GetModelineCandidateLines(TextBuffer text)
    {
        var count = Math.Min(ModelineScanLineCount, text.LineCount);
        var indexes = new SortedSet<int>();

        for (int i = 0; i < count; i++)
            indexes.Add(i);

        for (int i = Math.Max(0, text.LineCount - count); i < text.LineCount; i++)
            indexes.Add(i);

        foreach (var index in indexes)
            yield return text.GetLine(index);
    }

    private static bool TryExtractModelineOptions(string line, out string settings)
    {
        settings = "";

        var markerIndex = line.IndexOf("vim:", StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
            return false;

        var rest = line[(markerIndex + "vim:".Length)..].TrimStart();
        if (!rest.StartsWith("set ", StringComparison.OrdinalIgnoreCase))
            return false;

        rest = rest[4..].TrimStart();
        var endIndex = rest.LastIndexOf(':');
        if (endIndex < 0)
            return false;

        settings = rest[..endIndex].Trim();
        return settings.Length > 0;
    }

    private string ResolveScriptPath(string path)
    {
        path = path.Trim('"', '\'');
        if (Path.IsPathRooted(path))
            return path;

        var baseDir = _currentScriptDirectory ?? Environment.CurrentDirectory;
        return Path.Combine(baseDir, path);
    }

    private bool TryParseAugroupCommand(string cmd)
    {
        if (!cmd.StartsWith("augroup", StringComparison.OrdinalIgnoreCase))
            return false;

        var rest = cmd.Length > 7 ? cmd[7..].Trim() : "";
        if (rest.Equals("END", StringComparison.OrdinalIgnoreCase))
            _currentAugroup = null;
        else if (rest.Length > 0)
            _currentAugroup = rest;

        return true;
    }

    private bool TryParseAutocmdCommand(string cmd, out string? error)
    {
        error = null;
        if (!cmd.StartsWith("autocmd", StringComparison.OrdinalIgnoreCase) &&
            !cmd.StartsWith("au ", StringComparison.OrdinalIgnoreCase) &&
            !cmd.Equals("au", StringComparison.OrdinalIgnoreCase))
            return false;

        var rest = cmd.StartsWith("autocmd", StringComparison.OrdinalIgnoreCase)
            ? cmd[7..].Trim()
            : cmd.Length > 2 ? cmd[2..].Trim() : "";

        if (rest.StartsWith('!'))
        {
            rest = rest[1..].Trim();
            if (rest.Length == 0)
            {
                Autocmds.Clear(_currentAugroup);
                return true;
            }

            var clearParts = rest.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            string? clearGroup = _currentAugroup;
            string? clearEvent = null;
            string? clearPattern = null;
            if (clearParts.Length > 0)
            {
                if (IsAutocmdEventList(clearParts[0]))
                {
                    clearEvent = clearParts[0];
                    clearPattern = clearParts.Length > 1 ? clearParts[1].Trim() : null;
                }
                else
                {
                    clearGroup = clearParts[0];
                    clearEvent = clearParts.Length > 1 ? clearParts[1] : null;
                    clearPattern = clearParts.Length > 2 ? clearParts[2].Trim() : null;
                }
            }

            Autocmds.Clear(clearGroup, clearEvent, clearPattern);
            return true;
        }

        if (rest.Length == 0)
            return true;

        var parts = rest.Split(' ', 4, StringSplitOptions.RemoveEmptyEntries);
        string group;
        string eventList;
        string patternList;
        string command;

        if (parts.Length >= 3 && IsAutocmdEventList(parts[0]))
        {
            group = _currentAugroup ?? "";
            eventList = parts[0];
            patternList = parts[1];
            command = parts.Length == 4 ? $"{parts[2]} {parts[3]}" : parts[2];
        }
        else if (parts.Length >= 4 && IsAutocmdEventList(parts[1]))
        {
            group = parts[0];
            eventList = parts[1];
            patternList = parts[2];
            command = parts[3];
        }
        else
        {
            error = "E216: No such group or event";
            return true;
        }

        foreach (var eventName in eventList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var pattern in patternList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                Autocmds.Add(group, eventName, pattern, command);
        }

        return true;
    }

    private static bool IsAutocmdEventList(string value)
    {
        foreach (var eventName in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!SupportedAutocmdEvents.Contains(eventName))
                return false;
        }

        return value.Length > 0;
    }

    private static readonly HashSet<string> SupportedAutocmdEvents = new(StringComparer.OrdinalIgnoreCase)
    {
        "BufReadPre",
        "BufRead",
        "BufReadPost",
        "BufEnter",
        "FileType",
    };

    private bool TryParseMapCommand(string cmd, out Dictionary<string, string>? dict, out string? rest)
    {
        dict = null;
        rest = null;

        // Determine the map command prefix and target dictionary
        Dictionary<string, string>? target = null;
        string? suffix = null;

        var prefixes = new (string prefix, Dictionary<string, string>? maps)[]
        {
            ("nnoremap ",  NormalMaps),
            ("nmap ",      NormalMaps),
            ("inoremap ",  InsertMaps),
            ("imap ",      InsertMaps),
            ("vnoremap ",  VisualMaps),
            ("vmap ",      VisualMaps),
            ("xnoremap ",  VisualMaps),  // x = visual (no select) — close enough
            ("xmap ",      VisualMaps),
            // operator-pending, terminal, command-line: silently skip
            ("onoremap ",  null),
            ("omap ",      null),
            ("tnoremap ",  null),
            ("tmap ",      null),
            ("cnoremap ",  null),
            ("cmap ",      null),
        };

        foreach (var (p, maps) in prefixes)
        {
            if (cmd.StartsWith(p, StringComparison.OrdinalIgnoreCase))
            {
                target = maps;
                suffix = cmd[p.Length..];
                break;
            }
        }

        if (suffix == null) return false;

        dict = target; // may be null → silently ignore
        rest = suffix;
        return true;
    }

    private static readonly string[] MapFlags = ["<silent>", "<nowait>", "<buffer>", "<expr>", "<unique>", "<special>"];

    private static void ParseMap(Dictionary<string, string>? maps, string rest, string leader)
    {
        // Strip modifier flags
        rest = rest.Trim();
        bool stripped;
        do
        {
            stripped = false;
            foreach (var flag in MapFlags)
            {
                if (rest.StartsWith(flag, StringComparison.OrdinalIgnoreCase))
                {
                    rest = rest[flag.Length..].TrimStart();
                    stripped = true;
                    break;
                }
            }
        } while (stripped);

        var parts = rest.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2) return;

        var lhs = ExpandLeader(parts[0], leader);
        var rhs = ExpandLeader(parts[1], leader);

        // Replace <Cmd>...<CR> with :...<CR> so the map executes an ex command
        rhs = rhs.Replace("<Cmd>", ":", StringComparison.OrdinalIgnoreCase);

        if (maps != null)
            maps[lhs] = rhs;
    }

    private static string ExpandLeader(string s, string leader)
    {
        // <Leader> → the leader character (escaped for map use)
        if (s.Contains("<Leader>", StringComparison.OrdinalIgnoreCase))
            s = s.Replace("<Leader>", leader, StringComparison.OrdinalIgnoreCase);
        return s;
    }

    private static string ParseLeader(string cmd)
    {
        // let mapleader = "\<Space>" or let mapleader = " " or let mapleader = ","
        var eqIdx = cmd.IndexOf('=');
        if (eqIdx < 0) return "\\";
        var val = cmd[(eqIdx + 1)..].Trim().Trim('"', '\'');

        // Handle special sequences
        return val.ToLowerInvariant() switch
        {
            "\\<space>" or "<space>" => " ",
            "\\<cr>" or "<cr>"       => "\r",
            "\\<tab>" or "<tab>"     => "\t",
            _ when val.Length == 1   => val,
            _ => "\\"
        };
    }

    private static bool TryParseLetAssignment(string cmd, out string? name, out string? value, out string? error)
    {
        name = null;
        value = null;
        error = null;

        if (!cmd.StartsWith("let ", StringComparison.OrdinalIgnoreCase) && cmd != "let")
            return false;

        var rest = cmd.Length > 3 ? cmd[3..].Trim() : "";
        if (rest.Length == 0)
            return true;

        var eqIdx = rest.IndexOf('=');
        if (eqIdx < 0)
        {
            error = "E121: Undefined variable: " + rest;
            return true;
        }

        name = rest[..eqIdx].Trim();
        var expr = rest[(eqIdx + 1)..].Trim();
        if (!IsValidVariableName(name))
        {
            error = "E461: Illegal variable name: " + name;
            return true;
        }

        value = EvalLetExpression(expr);
        return true;
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

    private static bool IsValidVariableName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var bare = name.Length > 2 && name[1] == ':' ? name[2..] : name;
        if (bare.Length == 0 || !(char.IsLetter(bare[0]) || bare[0] == '_')) return false;
        return bare.All(ch => char.IsLetterOrDigit(ch) || ch == '_');
    }

    private static string StripQuotes(string s) =>
        s.Length >= 2 && ((s[0] == '"' && s[^1] == '"') || (s[0] == '\'' && s[^1] == '\''))
            ? s[1..^1]
            : s;

    private static string EvalLetExpression(string expr)
    {
        expr = expr.Trim();

        if (expr.Length >= 2 &&
            ((expr[0] == '"' && expr[^1] == '"') ||
             (expr[0] == '\'' && expr[^1] == '\'')))
            return StripQuotes(expr);

        return ExpressionEvaluator.Evaluate(expr) ?? expr;
    }

    private static readonly string[] AbbrevPrefixes =
        ["iabbrev ", "iab ", "abbreviate ", "ab "];

    private static readonly string[] UnabbrevPrefixes =
        ["iunabbrev ", "iuna ", "unabbreviate ", "una "];

    // Returns true and sets suffix if cmd starts with any of the given prefixes.
    private static bool TryStripPrefix(string cmd, string[] prefixes, out string suffix)
    {
        foreach (var prefix in prefixes)
        {
            if (cmd.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                suffix = cmd[prefix.Length..].Trim();
                return true;
            }
        }
        suffix = "";
        return false;
    }

    private bool TryParseAbbrevCommand(string cmd, out string? lhs, out string? rhs)
    {
        lhs = null; rhs = null;
        if (!TryStripPrefix(cmd, AbbrevPrefixes, out var rest)) return false;
        var parts = rest.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2) { lhs = parts[0]; rhs = parts[1]; }
        return true;
    }

    private bool TryParseUnabbrevCommand(string cmd, out string? lhs)
    {
        if (!TryStripPrefix(cmd, UnabbrevPrefixes, out var rest)) { lhs = null; return false; }
        lhs = rest;
        return true;
    }
}
