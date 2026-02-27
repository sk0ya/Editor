namespace WVim.Core.Config;

public class VimConfig
{
    public VimOptions Options { get; } = new();
    public Dictionary<string, string> NormalMaps { get; } = [];
    public Dictionary<string, string> InsertMaps { get; } = [];
    public Dictionary<string, string> VisualMaps { get; } = [];

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

            // Remove inline comments
            var commentIdx = line.IndexOf('"');
            if (commentIdx > 0) line = line[..commentIdx].Trim();

            ParseCommand(line);
        }
    }

    public string? ParseCommand(string cmd)
    {
        if (cmd.StartsWith("set ", StringComparison.OrdinalIgnoreCase))
            return Options.Apply(cmd[4..]);

        if (cmd.StartsWith("nmap ", StringComparison.OrdinalIgnoreCase))
        {
            ParseMap(NormalMaps, cmd[5..]);
            return null;
        }
        if (cmd.StartsWith("imap ", StringComparison.OrdinalIgnoreCase))
        {
            ParseMap(InsertMaps, cmd[5..]);
            return null;
        }
        if (cmd.StartsWith("vmap ", StringComparison.OrdinalIgnoreCase))
        {
            ParseMap(VisualMaps, cmd[5..]);
            return null;
        }
        if (cmd.StartsWith("nnoremap ", StringComparison.OrdinalIgnoreCase))
        {
            ParseMap(NormalMaps, cmd[9..]);
            return null;
        }
        if (cmd.StartsWith("inoremap ", StringComparison.OrdinalIgnoreCase))
        {
            ParseMap(InsertMaps, cmd[9..]);
            return null;
        }
        if (cmd.StartsWith("vnoremap ", StringComparison.OrdinalIgnoreCase))
        {
            ParseMap(VisualMaps, cmd[9..]);
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
        return null;
    }

    private static void ParseMap(Dictionary<string, string> maps, string rest)
    {
        var parts = rest.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2)
            maps[parts[0]] = parts[1];
    }
}
