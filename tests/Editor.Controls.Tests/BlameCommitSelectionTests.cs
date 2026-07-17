using System.IO;
using System.Windows.Threading;
using Editor.Controls.Git;

namespace Editor.Controls.Tests;

/// <summary>
/// Reproduces the final step of MainWindow.ShowCommitInGitLogAsync: after the file-history list
/// is pushed into the editor via SetText, the blame-selected commit is selected with NavigateTo
/// deferred to Background priority. This guards the fix for "the commit isn't selected" — running
/// NavigateTo inline (before the freshly-set content is laid out) leaves the caret un-scrolled.
/// </summary>
public sealed class BlameCommitSelectionTests
{
    [Fact]
    public void DeferredNavigate_AfterSettingHistoryList_SelectsCommitAndScrollsIntoView()
    {
        WpfTestHost.Run(() =>
        {
            var editor = new VimEditorControl();
            // Small viewport so a deep commit line requires scrolling to be revealed.
            var window = WpfTestHost.Load(editor, width: 700, height: 300);
            try
            {
                // Fake `git log --oneline` output: 200 commits, each line "<7-hex-hash> <subject>".
                var log = string.Join("\n",
                    Enumerable.Range(0, 200).Select(i => $"{i:x7} commit subject {i}"));
                const int target = 150; // a deep commit, well off-screen initially

                editor.SetText(log);

                // Precondition: right after SetText the target line is not visible yet.
                Assert.True(editor.Canvas.LastVisibleLine < target,
                    $"target line {target} should start off-screen, last visible = {editor.Canvas.LastVisibleLine}");

                // The fix under test: defer selection to Background so it runs after layout.
                _ = editor.Dispatcher.BeginInvoke(DispatcherPriority.Background,
                    () => editor.NavigateTo(target, 0));

                // Pump the dispatcher through Background priority (runs the deferred NavigateTo).
                var frame = new DispatcherFrame();
                _ = editor.Dispatcher.BeginInvoke(DispatcherPriority.Background,
                    new Action(() => frame.Continue = false));
                Dispatcher.PushFrame(frame);

                // The commit line is now the caret line...
                Assert.Equal(target, editor.Engine.Cursor.Line);
                // ...and it has been scrolled into view (list moved down from the top).
                Assert.InRange(target, editor.Canvas.FirstVisibleLine, editor.Canvas.LastVisibleLine);
                Assert.True(editor.Canvas.FirstVisibleLine > 0,
                    "the history list should have scrolled to reveal the commit");
            }
            finally { window.Close(); }
        });
    }

    /// <summary>End-to-end of the data path with the real git service: every committed blame line's
    /// commit must be locatable in the file-history list that ShowCommitInGitLogAsync opens. This is
    /// the link that was broken before the fix (repo-wide `git log -30` often omitted the commit).</summary>
    [Fact]
    public void RealGit_EveryBlameCommit_IsFoundInFileHistoryList()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, ".git")))
            dir = dir.Parent;
        Assert.NotNull(dir); // test must run inside the git working tree

        // File history is unbounded by default so even old blame commits remain locatable.
        var file = Path.Combine(dir!.FullName, "src", "Editor.Controls", "Rendering", "EditorCanvas.cs");
        Assert.True(File.Exists(file), $"source file not found: {file}");

        var git = new GitDiffProvider();
        var blame = git.GetBlameLines(file);
        Assert.NotEmpty(blame); // committed file → has blame

        var history = git.GetFileHistoryOutput(file);

        foreach (var (_, info) in blame)
            Assert.True(FindCommitLine(history, info.CommitHash) >= 0,
                $"blame commit {info.CommitHash} was not found in the file-history list");
    }

    // Mirror of MainWindow.FindCommitLine: locate the `git log --oneline` line whose leading hash
    // matches the (possibly differently-abbreviated) blame hash by shared prefix.
    private static int FindCommitLine(string log, string hash)
    {
        if (string.IsNullOrEmpty(hash) || string.IsNullOrEmpty(log)) return -1;
        var lines = log.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var text = lines[i].TrimStart();
            int sp = text.IndexOf(' ');
            var token = sp < 0 ? text : text[..sp];
            if (token.Length == 0) continue;
            if (token.StartsWith(hash, StringComparison.OrdinalIgnoreCase) ||
                hash.StartsWith(token, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }
}
