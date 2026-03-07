using System.IO;

namespace Editor.Core.Config;

/// <summary>
/// Persists command history and search history across sessions,
/// analogous to Vim's viminfo / Neovim's shada file.
/// </summary>
public static class ViminfoManager
{
    private const int MaxEntries = 50;

    private static string GetViminfoPath()
    {
        string dir;
        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            dir = Path.Combine(appData, "Editor");
        }
        else
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            dir = Path.Combine(home, ".config", "editor");
        }
        return Path.Combine(dir, "viminfo");
    }

    public static void Save(IReadOnlyList<string> cmdHistory, IReadOnlyList<string> searchHistory)
    {
        try
        {
            var path = GetViminfoPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var lines = new List<string>
            {
                "# viminfo -- Editor viminfo file",
                "# This file is overwritten automatically. Do not edit.",
                "",
                "# Command history (newest first):"
            };

            foreach (var entry in cmdHistory.Take(MaxEntries))
                lines.Add(":" + entry);

            lines.Add("");
            lines.Add("# Search history (newest first):");

            foreach (var entry in searchHistory.Take(MaxEntries))
                lines.Add("/" + entry);

            File.WriteAllLines(path, lines);
        }
        catch
        {
            // silently ignore I/O errors
        }
    }

    public static (IReadOnlyList<string> CmdHistory, IReadOnlyList<string> SearchHistory) Load()
    {
        var cmdHistory = new List<string>();
        var searchHistory = new List<string>();

        try
        {
            var path = GetViminfoPath();
            if (!File.Exists(path)) return (cmdHistory, searchHistory);

            bool inCmd = false;
            bool inSearch = false;

            foreach (var line in File.ReadLines(path))
            {
                if (line.StartsWith('#'))
                {
                    inCmd    = line.Contains("Command history");
                    inSearch = line.Contains("Search history");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    inCmd = inSearch = false;
                    continue;
                }

                if (inCmd && line.StartsWith(':'))
                    cmdHistory.Add(line[1..]);
                else if (inSearch && line.StartsWith('/'))
                    searchHistory.Add(line[1..]);
            }
        }
        catch
        {
            // silently ignore I/O errors
        }

        return (cmdHistory, searchHistory);
    }
}
