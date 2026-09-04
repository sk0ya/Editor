using Editor.Controls.Lsp;

namespace Editor.Controls.Tests;

/// <summary>Regression coverage for the reconnect mechanism: a language-server process that dies
/// unexpectedly (crash, broken pipe) must be detectable via <see cref="LspProcess.Exited"/> with
/// <see cref="LspProcess.IsRunning"/> already false by the time it fires — previously nothing tore the
/// process down when the read loop merely stopped reading, so a dead connection could sit there
/// reporting itself as running forever. A deliberate <see cref="LspProcess.Dispose"/> must NOT raise
/// <see cref="LspProcess.Exited"/> (that would incorrectly trigger a reconnect for a connection the
/// owner closed on purpose, e.g. because the tab closed).</summary>
public sealed class LspProcessExitedTests
{
    [Fact]
    public async Task Exited_fires_and_IsRunning_becomes_false_when_the_child_process_exits_on_its_own()
    {
        using var process = new LspProcess("cmd.exe", ["/c", "exit", "0"]);
        var exited = new TaskCompletionSource();
        process.Exited += () => exited.TrySetResult();

        var completed = await Task.WhenAny(exited.Task, Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.Same(exited.Task, completed);
        Assert.False(process.IsRunning);
    }

    [Fact]
    public async Task Dispose_does_not_raise_Exited_even_though_killing_the_process_also_closes_its_stdout()
    {
        var process = new LspProcess("cmd.exe", ["/c", "pause>nul"]); // stays alive until killed
        var exitedRaised = false;
        process.Exited += () => exitedRaised = true;

        process.Dispose();
        // Give the read loop a moment to notice the pipe closing from Dispose's Kill() — if the guard
        // in RaiseExitedAndTearDown didn't work, this is when the false-positive Exited would fire.
        await Task.Delay(500);

        Assert.False(exitedRaised);
        Assert.False(process.IsRunning);
    }

    /// <summary>
    /// 構築直後の <see cref="LspProcess.Dispose"/>。読み取りスレッドが最初の一行を実行する前に
    /// <c>Process.Dispose()</c> が済んでいると、以前はそのスレッドが <c>_process.StandardOutput</c> に
    /// 触れて <see cref="ObjectDisposedException"/> を投げ、背景スレッドの未処理例外として
    /// テストホスト（＝ホストアプリ）ごと落としていた。タイミング勝負なので繰り返して当てる。
    /// </summary>
    /// <summary>
    /// 構築直後の <see cref="LspProcess.Dispose"/>。読み取りスレッドが最初の一行を実行する前に
    /// <c>Process.Dispose()</c> が済んでいると、以前はそのスレッドが <c>_process.StandardOutput</c> に
    /// 触れて <see cref="ObjectDisposedException"/> を投げ、背景スレッドの未処理例外として
    /// テストホスト（＝ホストアプリ）ごと落としていた。
    /// 実際に競り勝てるかは負荷次第でこのテスト単体では狙って再現できない（読み取りスレッドが
    /// ほぼ必ず先着する）ので、これは再現テストではなく「構築直後に捨てても落ちない」という
    /// 契約の記録。保証しているのは ReadLoop 側の捕獲であってこのループではない。
    /// </summary>
    [Fact]
    public async Task Disposing_immediately_after_construction_does_not_crash_the_read_loop()
    {
        for (int i = 0; i < 25; i++)
        {
            var process = new LspProcess("cmd.exe", ["/c", "pause>nul"]);
            process.Dispose();
            Assert.False(process.IsRunning);
        }

        // 未処理例外はこのスレッドでは観測できない（落ちるとしたらホストごと）ので、
        // 読み取りスレッドが出揃うまで待ってからテストを終える。
        await Task.Delay(500);
    }

    [Fact]
    public async Task Exited_fires_at_most_once()
    {
        using var process = new LspProcess("cmd.exe", ["/c", "exit", "0"]);
        var count = 0;
        process.Exited += () => Interlocked.Increment(ref count);

        // Give the read loop time to observe EOF and fire; then poll briefly to make sure a second
        // firing doesn't sneak in from some other code path (e.g. a later Dispose by the test's `using`).
        await Task.Delay(500);
        Assert.Equal(1, count);
    }
}
