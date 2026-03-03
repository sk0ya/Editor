using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace Editor.Controls.Git;

public enum GitLineState { None, Added, Modified, Deleted }

public partial class GitDiffProvider
{
    [GeneratedRegex(@"^@@ -\d+(?:,\d+)? \+(\d+)(?:,(\d+))? @@")]
    private static partial Regex HunkHeaderRegex();

    public Dictionary<int, GitLineState> GetDiff(string filePath)
    {
        var result = new Dictionary<int, GitLineState>();
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return result;

        string? workDir = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(workDir) || FindGitRoot(workDir) == null) return result;

        var output = RunGit(workDir, ["diff", "HEAD", "--", filePath]);
        if (string.IsNullOrEmpty(output)) return result;

        ParseUnifiedDiff(output, result);
        return result;
    }

    public Dictionary<int, string> GetBlameAnnotations(string filePath)
    {
        var result = new Dictionary<int, string>();
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return result;

        string? workDir = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(workDir) || FindGitRoot(workDir) == null) return result;

        var output = RunGit(workDir, ["blame", "--porcelain", filePath]);
        if (string.IsNullOrEmpty(output)) return result;

        ParsePorcelainBlame(output, result);
        return result;
    }

    private static string? FindGitRoot(string dir)
    {
        var current = dir;
        while (!string.IsNullOrEmpty(current))
        {
            if (Directory.Exists(Path.Combine(current, ".git")))
                return current;
            current = Path.GetDirectoryName(current);
        }
        return null;
    }

    private static string? RunGit(string workDir, string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = workDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var arg in args)
                psi.ArgumentList.Add(arg);

            using var proc = Process.Start(psi);
            if (proc == null) return null;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5000);
            return output;
        }
        catch
        {
            return null;
        }
    }

    private static void ParseUnifiedDiff(string diffOutput, Dictionary<int, GitLineState> result)
    {
        var hunkRegex = HunkHeaderRegex();
        int newLine = -1;
        int pendingDeletes = 0;
        bool inHeader = true;

        foreach (var line in diffOutput.Split('\n'))
        {
            if (line.StartsWith("@@"))
            {
                var m = hunkRegex.Match(line);
                if (m.Success)
                {
                    newLine = int.Parse(m.Groups[1].Value) - 1; // 0-based
                    pendingDeletes = 0;
                    inHeader = false;
                }
                continue;
            }

            if (inHeader || newLine < 0 || line.Length == 0) continue;

            switch (line[0])
            {
                case '+':
                    result[newLine] = pendingDeletes > 0 ? GitLineState.Modified : GitLineState.Added;
                    if (pendingDeletes > 0) pendingDeletes--;
                    newLine++;
                    break;
                case '-':
                    pendingDeletes++;
                    break;
                case ' ':
                    pendingDeletes = 0;
                    newLine++;
                    break;
            }
        }
    }

    private static void ParsePorcelainBlame(string blameOutput, Dictionary<int, string> result)
    {
        string? currentHash = null;
        string? currentAuthor = null;
        string? currentDate = null;
        int resultLine = -1;

        foreach (var line in blameOutput.Split('\n'))
        {
            if (line.Length == 0) continue;

            if (char.IsAsciiHexDigit(line[0]) && line.Length > 40 && line[40] == ' ')
            {
                // Entry header: <40-char hash> <orig-lineno> <result-lineno> [<count>]
                var parts = line.Split(' ');
                currentHash = parts[0][..Math.Min(7, parts[0].Length)];
                resultLine = parts.Length > 2 && int.TryParse(parts[2], out int rl) ? rl - 1 : -1;
                currentAuthor = null;
                currentDate = null;
            }
            else if (line.StartsWith("author "))
            {
                currentAuthor = line[7..].Trim();
            }
            else if (line.StartsWith("author-time "))
            {
                if (long.TryParse(line[12..], out long ts))
                    currentDate = DateTimeOffset.FromUnixTimeSeconds(ts).LocalDateTime.ToString("yyyy-MM-dd");
            }
            else if (line[0] == '\t' && currentHash != null && currentAuthor != null
                     && currentDate != null && resultLine >= 0)
            {
                // Skip uncommitted lines (hash = all zeros)
                bool notCommitted = currentHash.All(c => c == '0');
                if (!notCommitted)
                    result[resultLine] = $"  {currentHash} ({currentAuthor}, {currentDate})";
            }
        }
    }
}
