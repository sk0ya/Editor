using System.Diagnostics;
using System.IO;
using Editor.Controls.Git;

namespace Editor.Controls.Tests;

/// <summary>
/// ワークツリーでは .git がファイル（gitdir: …）になる。そこで開いたファイルも Git の中として扱えること。
/// </summary>
public sealed class GitWorktreeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "editor-worktree-" + Guid.NewGuid().ToString("N"));

    public GitWorktreeTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void FindGitRoot_AcceptsDotGitFile()
    {
        var worktree = Path.Combine(_root, "wt");
        var sub = Path.Combine(worktree, "src");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(worktree, ".git"), "gitdir: C:/somewhere/.git/worktrees/wt\n");

        Assert.Equal(worktree, GitDiffProvider.FindGitRoot(sub));
    }

    [Fact]
    public void GetBlameLines_WorksInsideWorktree()
    {
        var main = Path.Combine(_root, "main");
        Directory.CreateDirectory(main);
        Git(main, "init", "-q");
        Git(main, "config", "user.name", "tester");
        Git(main, "config", "user.email", "tester@example.com");
        File.WriteAllText(Path.Combine(main, "a.txt"), "one\ntwo\n");
        Git(main, "add", "a.txt");
        Git(main, "commit", "-q", "-m", "最初");

        var worktree = Path.Combine(_root, "wt");
        Git(main, "worktree", "add", "-q", worktree);
        Assert.True(File.Exists(Path.Combine(worktree, ".git")));

        var blame = new GitDiffProvider().GetBlameLines(Path.Combine(worktree, "a.txt"));

        Assert.Equal(2, blame.Count);
        Assert.Equal("tester", blame[0].Author);
        Assert.Equal("最初", blame[1].Summary);
    }

    private static void Git(string workDir, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEndAsync();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        Assert.True(proc.ExitCode == 0, $"git {string.Join(' ', args)} failed: {stderr}{stdout.Result}");
    }
}
