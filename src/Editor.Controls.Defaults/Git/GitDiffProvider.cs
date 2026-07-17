using System.Diagnostics;
using System.IO;
using System.Text;
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

    public string GetFileHistoryOutput(string filePath, int? count = null)
    {
        if (!TryGetFileWorkDir(filePath, out var workDir)) return "(no file or not a git repository)";
        // --follow so renames don't truncate the history that blame can attribute a line to.
        var args = new List<string> { "log", "--oneline", "--follow" };
        if (count is > 0)
            args.Add($"-{count}");
        args.Add("--");
        args.Add(filePath);
        var output = RunGit(workDir, args.ToArray());
        return string.IsNullOrEmpty(output) ? "(no commits)" : output;
    }

    public (bool Success, string Output) RunPush(string repoPath)
    {
        var root = ResolveGitRoot(repoPath);
        if (root == null)
            return (false, "Not a git repository");

        var output = RunGit(root, ["push"], captureStderr: true);
        var success = !string.IsNullOrEmpty(output) && !output.StartsWith("ERROR:", StringComparison.Ordinal);
        return (success, string.IsNullOrWhiteSpace(output) ? "Already up to date" : output);
    }

    public (bool Success, string Output) RunPull(string repoPath)
    {
        var root = ResolveGitRoot(repoPath);
        if (root == null)
            return (false, "Not a git repository");

        var output = RunGit(root, ["pull", "--ff-only"], captureStderr: true);
        var success = !string.IsNullOrEmpty(output) && !output.StartsWith("ERROR:", StringComparison.Ordinal);
        return (success, string.IsNullOrWhiteSpace(output) ? "Already up to date" : output);
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

    public Dictionary<int, EditorBlameLine> GetBlameLines(string filePath)
    {
        if (!TryGetFileWorkDir(filePath, out var workDir)) return [];

        var output = RunGit(workDir, ["blame", "--porcelain", filePath]);
        if (string.IsNullOrEmpty(output)) return [];

        return ParsePorcelainBlame(output);
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

    private static string? ResolveGitRoot(string repoPath)
    {
        var workDir = ResolveWorkDir(repoPath);
        return string.IsNullOrEmpty(workDir) ? null : FindGitRoot(workDir);
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
                // git はコミットメッセージ等を UTF-8 で出力する。未指定だとコンソール既定
                // （日本語 Windows では cp932）で読んでしまい blame の summary 等が文字化けする。
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
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
                // 出力を UTF-8 で読む（RunGit と同じ理由）＋パッチの入力も UTF-8（BOM なし）で渡す。
                // 既定（cp932）だと日本語を含むパッチのバイト列が壊れて apply が失敗する。
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
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

    // コミットメタの可変ホルダー。porcelain はコミット情報（author 等）を最初の出現時にしか出力しない
    // ので、ハッシュ→メタでキャッシュし、2回目以降の行にも同じ情報を割り当てる。
    private sealed class BlameCommitMeta
    {
        public string Author = "";
        public string Date = "";
        public string Summary = "";
    }

    /// <summary>
    /// `git blame --porcelain` の出力を行→コミット情報に変換する（0始まり行）。各行はヘッダ
    /// 「&lt;40桁hash&gt; &lt;元行&gt; &lt;結果行&gt; [&lt;行数&gt;]」＋（コミット初出時のみ）author/summary 等の
    /// メタ行＋タブ始まりの本文行から成る。未コミット行（hash が全て 0）は含めない。
    /// </summary>
    internal static Dictionary<int, EditorBlameLine> ParsePorcelainBlame(string blameOutput)
    {
        var result = new Dictionary<int, EditorBlameLine>();
        var metas = new Dictionary<string, BlameCommitMeta>();
        string? currentHash = null;
        BlameCommitMeta? currentMeta = null;
        int resultLine = -1;
        int origLine = 0;

        foreach (var line in blameOutput.Split('\n'))
        {
            if (line.Length == 0) continue;

            if (char.IsAsciiHexDigit(line[0]) && line.Length > 40 && line[40] == ' ')
            {
                // Entry header: <40-char hash> <orig-lineno> <result-lineno> [<count>]
                var parts = line.Split(' ');
                currentHash = parts[0];
                origLine = parts.Length > 1 && int.TryParse(parts[1], out int ol) ? ol : 0;
                resultLine = parts.Length > 2 && int.TryParse(parts[2], out int rl) ? rl - 1 : -1;
                if (!metas.TryGetValue(currentHash, out currentMeta))
                    metas[currentHash] = currentMeta = new BlameCommitMeta();
            }
            else if (line.StartsWith("author ") && currentMeta != null)
            {
                currentMeta.Author = line[7..].Trim();
            }
            else if (line.StartsWith("author-time ") && currentMeta != null)
            {
                if (long.TryParse(line[12..], out long ts))
                    currentMeta.Date = DateTimeOffset.FromUnixTimeSeconds(ts).LocalDateTime.ToString("yyyy-MM-dd");
            }
            else if (line.StartsWith("summary ") && currentMeta != null)
            {
                currentMeta.Summary = line[8..].Trim();
            }
            else if (line[0] == '\t' && currentHash != null && currentMeta != null && resultLine >= 0)
            {
                // Skip uncommitted lines (hash = all zeros)
                bool notCommitted = currentHash.All(c => c == '0');
                if (!notCommitted)
                    result[resultLine] = new EditorBlameLine(
                        currentHash[..Math.Min(7, currentHash.Length)],
                        currentMeta.Author, currentMeta.Date, currentMeta.Summary, origLine);
            }
        }

        return result;
    }
}
