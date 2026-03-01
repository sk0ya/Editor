namespace Editor.Core.Config;

public class VimConfig
{
    public VimOptions Options { get; } = new();
    public Dictionary<string, string> NormalMaps { get; } = [];
    public Dictionary<string, string> InsertMaps { get; } = [];
    public Dictionary<string, string> VisualMaps { get; } = [];

    // The mapleader character (default backslash, set by `let mapleader=...`)
    public string Leader { get; private set; } = "\\";

    public static VimConfig LoadFromFile(string path)
    {
        var cfg = new VimConfig();
        if (!File.Exists(path)) return cfg;
        cfg.ParseLines(File.ReadAllLines(path));
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

        var cfg = LoadFromFile(vimrcPath);

        // Allow project-local overrides when running from a workspace.
        var localVimrcPath = Path.Combine(Environment.CurrentDirectory, ".vimrc");
        if (File.Exists(localVimrcPath))
            cfg.ParseLines(File.ReadAllLines(localVimrcPath));

        return cfg;
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
        if (cmd.StartsWith("let mapleader", StringComparison.OrdinalIgnoreCase))
        {
            Leader = ParseLeader(cmd);
            return null;
        }

        // Map commands — strip modifier flags (<silent>, <nowait>, <buffer>, <expr>, <unique>)
        // before splitting into LHS/RHS.
        if (TryParseMapCommand(cmd, out var dict, out var rest))
        {
            ParseMap(dict!, rest!, Leader);
            return null;
        }

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
        // Silently ignore: source, augroup, autocmd, filetype, scriptencoding, tnoremap, onoremap,
        // cnoremap, cmap, function, endfunction, if, endif, etc.
        return null;
    }

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
}
