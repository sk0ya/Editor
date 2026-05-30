using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace Editor.Controls.Git;

public partial class GitDiffProvider : IEditorGitService
{
    [GeneratedRegex(@"^@@ -\d+(?:,\d+)? \+(\d+)(?:,(\d+))? @@")]
    private static partial Regex HunkHeaderRegex();

    [GeneratedRegex(@"^@@ -(?<oldStart>\d+)(?:,(?<oldCount>\d+))? \+(?<newStart>\d+)(?:,(?<newCount>\d+))? @@")]
    private static partial Regex FullHunkHeaderRegex();

    public Dictionary<int, GitLineState> GetDiff(string filePath)
    {
        var result = new Dictionary<int, GitLineState>();
        if (!TryGetFileWorkDir(filePath, out var workDir)) return result;

        var output = RunGit(workDir, ["diff", "HEAD", "--", filePath]);
        if (string.IsNullOrEmpty(output)) return result;

        ParseUnifiedDiff(output, result);
        return result;
    }

    public string? GetBranchName(string repoPath)
    {
        var workDir = ResolveWorkDir(repoPath);
        if (string.IsNullOrEmpty(workDir)) return null;
        var root = FindGitRoot(workDir);
        if (root == null) return null;

        var branch = RunGit(root, ["branch", "--show-current"])?.Trim();
        if (!string.IsNullOrEmpty(branch)) return branch;

        var head = RunGit(root, ["rev-parse", "--short", "HEAD"])?.Trim();
        return string.IsNullOrEmpty(head) ? null : $"HEAD {head}";
    }

    public string GetStatusOutput(string repoPath)
    {
        string? workDir = ResolveWorkDir(repoPath);
        if (string.IsNullOrEmpty(workDir)) return "(no path)";
        var root = FindGitRoot(workDir);
        if (root == null) return "(not a git repository)";

        var output = RunGit(root, ["status", "--short", "--branch", "--untracked-files=all"], captureStderr: true);
        if (string.IsNullOrWhiteSpace(output)) return "## clean";
        return output;
    }

    public string GetDiffOutput(string filePath)
    {
        if (!TryGetFileWorkDir(filePath, out var workDir)) return "(no file or not a git repository)";
        var output = RunGit(workDir, ["diff", "HEAD", "--", filePath]);
        return string.IsNullOrEmpty(output) ? "(no changes)" : output;
    }

    public string GetLogOutput(string repoPath, int count = 30)
    {
        string? workDir = ResolveWorkDir(repoPath);
        if (string.IsNullOrEmpty(workDir)) return "(no path)";
        var root = FindGitRoot(workDir);
        if (root == null) return "(not a git repository)";
        var output = RunGit(root, ["log", "--oneline", $"-{count}"]);
        return string.IsNullOrEmpty(output) ? "(no commits)" : output;
    }

    public (bool Success, string Output) RunCommit(string filePath, string message)
    {
        string? workDir = null;
        if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir))
                workDir = FindGitRoot(dir);
        }
        workDir ??= FindGitRoot(Environment.CurrentDirectory);
        if (workDir == null)
            return (false, "Not a git repository");

        var output = RunGit(workDir, ["commit", "-m", message], captureStderr: true);
        bool success = !string.IsNullOrEmpty(output) && !output.StartsWith("ERROR:");
        return (success, output ?? "git commit failed");
    }

    public (bool Success, string Output) StageHunk(string filePath, int line)
    {
        if (!TryGetFileWorkDir(filePath, out var workDir))
            return (false, "Not a git repository");

        var diff = RunGit(workDir, ["diff", "--no-ext-diff", "--unified=3", "--", filePath], captureStderr: true);
        if (string.IsNullOrEmpty(diff) || diff == "(no changes)")
            return (false, "No unstaged changes");
        if (diff.StartsWith("ERROR:"))
            return (false, diff);

        if (!TryBuildSingleHunkPatch(diff, line, out var patch))
            return (false, "No unstaged hunk at cursor");

        var output = RunGitWithInput(workDir, ["apply", "--cached"], patch);
        if (output.StartsWith("ERROR:"))
            return (false, output);
        return (true, string.IsNullOrWhiteSpace(output) ? "Hunk staged" : output);
    }

    public (bool Success, string Output) UnstageHunk(string filePath, int line)
    {
        if (!TryGetFileWorkDir(filePath, out var workDir))
            return (false, "Not a git repository");

        var diff = RunGit(workDir, ["diff", "--cached", "--no-ext-diff", "--unified=3", "--", filePath], captureStderr: true);
        if (string.IsNullOrEmpty(diff) || diff == "(no changes)")
            return (false, "No staged changes");
        if (diff.StartsWith("ERROR:"))
            return (false, diff);

        var worktreeDiff = RunGit(workDir, ["diff", "--no-ext-diff", "--unified=3", "--", filePath], captureStderr: true);
        if (!string.IsNullOrEmpty(worktreeDiff) && worktreeDiff.StartsWith("ERROR:"))
            return (false, worktreeDiff);

        var indexLine = MapWorktreeLineToIndexLine(worktreeDiff ?? "", line);
        if (!TryBuildSingleHunkPatch(diff, indexLine, out var patch))
            return (false, "No staged hunk at cursor");

        var output = RunGitWithInput(workDir, ["apply", "--cached", "--reverse"], patch);
        if (output.StartsWith("ERROR:"))
            return (false, output);
        return (true, string.IsNullOrWhiteSpace(output) ? "Hunk unstaged" : output);
    }

    public Dictionary<int, string> GetBlameAnnotations(string filePath)
    {
        var result = new Dictionary<int, string>();
        if (!TryGetFileWorkDir(filePath, out var workDir)) return result;

        var output = RunGit(workDir, ["blame", "--porcelain", filePath]);
        if (string.IsNullOrEmpty(output)) return result;

        ParsePorcelainBlame(output, result);
        return result;
    }

    /// <summary>
    /// Validates that <paramref name="filePath"/> exists and is inside a git repo.
    /// On success sets <paramref name="workDir"/> to the file's directory; returns false otherwise.
    /// </summary>
    private static bool TryGetFileWorkDir(string filePath, out string workDir)
    {
        workDir = "";
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return false;
        var dir = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(dir) || FindGitRoot(dir) == null) return false;
        workDir = dir;
        return true;
    }

    private static string? ResolveWorkDir(string repoPath)
    {
        if (string.IsNullOrEmpty(repoPath)) return null;
        return File.Exists(repoPath) ? Path.GetDirectoryName(repoPath) : repoPath;
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

    /// <summary>
    /// Run a git command. When <paramref name="captureStderr"/> is true, stderr is also read,
    /// exit code is checked, and errors are returned as "ERROR: …" strings.
    /// </summary>
    private static string? RunGit(string workDir, string[] args, bool captureStderr = false)
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
            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = captureStderr ? proc.StandardError.ReadToEnd() : null;
            proc.WaitForExit(5000);

            if (captureStderr)
            {
                if (proc.ExitCode != 0)
                    return $"ERROR: {(string.IsNullOrEmpty(stderr) ? stdout : stderr!).Trim()}";
                return string.IsNullOrEmpty(stdout) ? stderr!.Trim() : stdout.Trim();
            }
            return stdout;
        }
        catch (Exception ex)
        {
            return captureStderr ? $"ERROR: {ex.Message}" : null;
        }
    }

    private static string RunGitWithInput(string workDir, string[] args, string input)
    {
        try
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = workDir,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var arg in args)
                psi.ArgumentList.Add(arg);

            using var proc = Process.Start(psi);
            if (proc == null) return "ERROR: Failed to start git";
            proc.StandardInput.Write(input);
            proc.StandardInput.Close();
            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(5000);

            if (proc.ExitCode != 0)
                return $"ERROR: {(string.IsNullOrWhiteSpace(stderr) ? stdout : stderr).Trim()}";
            return string.IsNullOrWhiteSpace(stdout) ? stderr.Trim() : stdout.Trim();
        }
        catch (Exception ex)
        {
            return $"ERROR: {ex.Message}";
        }
    }

    private static bool TryBuildSingleHunkPatch(string diffOutput, int zeroBasedLine, out string patch)
    {
        patch = "";
        var targetLine = zeroBasedLine + 1;
        var fileHeader = new List<string>();
        List<string>? currentHunk = null;
        bool currentContainsTarget = false;

        var diffLines = diffOutput.Split('\n');
        for (int i = 0; i < diffLines.Length; i++)
        {
            var rawLine = diffLines[i];
            if (i == diffLines.Length - 1 && rawLine.Length == 0)
                break;

            var line = rawLine.TrimEnd('\r');
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                if (currentHunk != null && currentContainsTarget)
                {
                    patch = BuildPatch(fileHeader, currentHunk);
                    return true;
                }

                currentHunk = [line];
                currentContainsTarget = HunkContainsLine(line, targetLine);
                continue;
            }

            if (currentHunk == null)
                fileHeader.Add(line);
            else
                currentHunk.Add(line);
        }

        if (currentHunk != null && currentContainsTarget)
        {
            patch = BuildPatch(fileHeader, currentHunk);
            return true;
        }

        return false;
    }

    private static int MapWorktreeLineToIndexLine(string diffOutput, int zeroBasedWorktreeLine)
    {
        if (string.IsNullOrWhiteSpace(diffOutput))
            return zeroBasedWorktreeLine;

        var targetNewLine = zeroBasedWorktreeLine + 1;
        var oldLine = 1;
        var newLine = 1;
        var inHunk = false;

        foreach (var rawLine in diffOutput.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                var match = FullHunkHeaderRegex().Match(line);
                if (!match.Success)
                    continue;

                var oldStart = int.Parse(match.Groups["oldStart"].Value);
                var newStart = int.Parse(match.Groups["newStart"].Value);

                if (targetNewLine < newStart)
                    return Math.Max(0, oldLine + (targetNewLine - newLine) - 1);

                oldLine = oldStart;
                newLine = newStart;
                inHunk = true;
                continue;
            }

            if (!inHunk || line.Length == 0)
                continue;

            switch (line[0])
            {
                case ' ':
                    if (newLine == targetNewLine)
                        return Math.Max(0, oldLine - 1);
                    oldLine++;
                    newLine++;
                    break;
                case '+':
                    if (newLine == targetNewLine)
                        return Math.Max(0, oldLine - 1);
                    newLine++;
                    break;
                case '-':
                    oldLine++;
                    break;
            }
        }

        return Math.Max(0, oldLine + (targetNewLine - newLine) - 1);
    }

    private static string BuildPatch(List<string> fileHeader, List<string> hunk)
    {
        var lines = new List<string>(fileHeader.Count + hunk.Count);
        lines.AddRange(fileHeader.Where(l => l.Length > 0));
        lines.AddRange(hunk);
        return string.Join('\n', lines) + "\n";
    }

    private static bool HunkContainsLine(string hunkHeader, int oneBasedLine)
    {
        var match = FullHunkHeaderRegex().Match(hunkHeader);
        if (!match.Success) return false;

        var newStart = int.Parse(match.Groups["newStart"].Value);
        var newCount = match.Groups["newCount"].Success
            ? int.Parse(match.Groups["newCount"].Value)
            : 1;

        var first = newStart;
        var last = newCount == 0 ? newStart : newStart + newCount - 1;
        return oneBasedLine >= first && oneBasedLine <= last;
    }

    private static void ParseUnifiedDiff(string diffOutput, Dictionary<int, GitLineState> result)
    {
        var hunkRegex = HunkHeaderRegex();
        int newLine = -1;
        int pendingDeletes = 0;
        bool inHeader = true;

        void FlushDeleted()
        {
            if (pendingDeletes <= 0 || newLine < 0) return;
            result.TryAdd(newLine, GitLineState.Deleted);
            pendingDeletes = 0;
        }

        foreach (var line in diffOutput.Split('\n'))
        {
            if (line.StartsWith("@@"))
            {
                FlushDeleted();
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
                    FlushDeleted();
                    newLine++;
                    break;
            }
        }

        FlushDeleted();
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
