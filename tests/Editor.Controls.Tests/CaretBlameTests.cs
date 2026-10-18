using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using Editor.Controls.Git;
using Editor.Controls.Rendering;

namespace Editor.Controls.Tests;

/// <summary>
/// カーソル行の blame を行末に出す（ShowCaretLineBlame）と、折りたたみの保存・復元（GetClosedFolds / RestoreClosedFolds）。
/// </summary>
public sealed class CaretBlameTests
{
    private sealed class FakeGit : IEditorGitService
    {
        public Dictionary<int, EditorBlameLine> Blame { get; } = new()
        {
            [0] = new("abc1234", "Alice", "2026-10-01", "最初のコミット"),
            [2] = new("def5678", "Bob", "2026-10-10", "三行目を直す"),
        };
        public int BlameCalls;
        public Dictionary<int, GitLineState> GetDiff(string filePath) => [];
        public string? GetBranchName(string repoPath) => "main";
        public string GetStatusOutput(string repoPath) => "";
        public string GetDiffOutput(string filePath) => "";
        public string GetLogOutput(string repoPath, int count = 30) => "";
        public string GetFileHistoryOutput(string filePath, int? count = null) => "";
        public (bool Success, string Output) RunPush(string repoPath) => (true, "");
        public (bool Success, string Output) RunPull(string repoPath) => (true, "");
        public (bool Success, string Output) RunCommit(string filePath, string message) => (true, "");
        public (bool Success, string Output) StageHunk(string filePath, int line) => (true, "");
        public (bool Success, string Output) UnstageHunk(string filePath, int line) => (true, "");
        public Dictionary<int, EditorBlameLine> GetBlameLines(string filePath)
        {
            Interlocked.Increment(ref BlameCalls);
            return Blame;
        }
    }

    [Fact]
    public void Annotation_follows_the_caret_and_hides_on_uncommitted_lines_and_while_modified()
    {
        var git = new FakeGit();
        WithEditor(git, (editor, dir) =>
        {
            var path = Path.Combine(dir, "a.cs");
            File.WriteAllText(path, "one\ntwo\nthree\n");
            editor.LoadFile(path);
            editor.ShowCaretLineBlame = true;
            PumpUntil(() => CanvasOf(editor).LineAnnotation.Text is not null);

            Assert.Equal((0, "Alice, 2026-10-01 · 最初のコミット"), CanvasOf(editor).LineAnnotation);

            editor.NavigateTo(1, 0);   // コミットしていない行（blame に無い）
            Assert.Null(CanvasOf(editor).LineAnnotation.Text);

            editor.NavigateTo(2, 0);
            Assert.Equal(2, CanvasOf(editor).LineAnnotation.Line0);
            var calls = git.BlameCalls;

            // カーソル移動では git を起こさない。
            editor.NavigateTo(0, 0);
            Assert.Equal(calls, git.BlameCalls);

            // 未保存の編集の間は出さない（行がずれていれば別の行の来歴になる）。
            editor.ExecuteCommand("normal! Ox");
            Assert.True(editor.IsModified);
            Assert.Null(CanvasOf(editor).LineAnnotation.Text);

            editor.ShowCaretLineBlame = false;
            Assert.Null(CanvasOf(editor).LineAnnotation.Text);
        });
    }

    [Fact]
    public void Long_summaries_are_cut()
    {
        var text = VimEditorControl.FormatCaretBlame(new EditorBlameLine("a", "Alice", "2026-10-01", new string('x', 100)));
        Assert.EndsWith("…", text);
        Assert.True(text.Length < 90);
        Assert.Equal("Alice, 2026-10-01", VimEditorControl.FormatCaretBlame(new EditorBlameLine("a", "Alice", "2026-10-01", "")));
    }

    [Fact]
    public void Canvas_annotation_is_replaced_and_cleared()
    {
        WpfTestHost.Run(() =>
        {
            var canvas = new EditorCanvas();
            canvas.SetLineAnnotation(3, "Alice, 2026-10-01");
            Assert.Equal((3, "Alice, 2026-10-01"), canvas.LineAnnotation);
            canvas.SetLineAnnotation(-1, null);
            Assert.Null(canvas.LineAnnotation.Text);
        });
    }

    [Fact]
    public void Closed_manual_folds_round_trip_through_the_host()
    {
        WithEditor(new FakeGit(), (editor, dir) =>
        {
            var path = Path.Combine(dir, "b.cs");
            File.WriteAllText(path, string.Join("\n", Enumerable.Range(0, 20).Select(i => $"line {i}")));
            editor.LoadFile(path);
            // 2〜4 行目（0 始まり）を手で畳んだ状態を作る（zf と同じく手で畳んだ印が付く）。
            editor.RestoreClosedFolds([new Editor.Core.Folds.FoldRegion(2, 4, IsClosed: true, IsManual: true)]);

            var closed = editor.GetClosedFolds();
            var fold = Assert.Single(closed);
            Assert.Equal((2, 4, true), (fold.StartLine, fold.EndLine, fold.IsManual));

            // 別のファイルを載せる（ホストがタブを開き直したのと同じく、折りたたみの無い状態から戻す）。
            var other = Path.Combine(dir, "c.cs");
            File.WriteAllText(other, File.ReadAllText(path));
            editor.LoadFile(other);
            Assert.Empty(editor.GetClosedFolds());

            editor.RestoreClosedFolds(closed);

            Assert.Equal((2, 4), (editor.GetClosedFolds().Single().StartLine, editor.GetClosedFolds().Single().EndLine));
        });
    }

    private static void PumpUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
            Thread.Sleep(10);
        }
    }

    private static EditorCanvas CanvasOf(VimEditorControl editor)
        => (EditorCanvas)typeof(VimEditorControl)
            .GetField("Canvas", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(editor)!;

    private static void WithEditor(IEditorGitService git, Action<VimEditorControl, string> action)
        => WpfTestHost.Run(() =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "editor-caret-blame-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var editor = new VimEditorControl(new VimEditorControlOptions { GitServiceFactory = () => git });
            Window? window = null;
            try
            {
                window = WpfTestHost.Load(editor);
                action(editor, dir);
            }
            finally
            {
                try { window?.Close(); }
                finally
                {
                    if (window != null) window.Content = null;
                    editor.Dispose();
                    try { Directory.Delete(dir, recursive: true); } catch { }
                }
            }
        });
}
