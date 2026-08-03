using Editor.Controls.Lsp;
using Editor.Core.Lsp;

namespace Editor.Controls.Tests;

/// <summary>Regression coverage for the "hangs forever" failure mode: a language server that is alive as a
/// process but never answers used to leave <see cref="LspProcess.SendRequestAsync"/> pending for the rest of
/// the session, so <see cref="LspClient.InitializeAsync"/> never returned and the host sat in "connecting"
/// forever. Every request must now be resolved by a timeout, and the pending table must not leak the
/// abandoned entry. The tests drive the real class with a child process that stays alive and never writes to
/// stdout (same style as <see cref="LspProcessExitedTests"/>), with the timeout shortened through the
/// internal seam so they finish in milliseconds.</summary>
public sealed class LspProcessRequestTimeoutTests
{
    /// <summary>応答を返さないまま生き続けるダミーサーバー。stdout は nul に捨てるので、
    /// 読み取りループは EOF にも到達せず「生きているのに黙っている」状態を再現できる。</summary>
    private static readonly string[] MuteServerArgs =
        ["/c", "ping", "-n", "6", "127.0.0.1", ">", "nul"];

    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(300);

    /// <summary>タイムアウトより十分長く、かつ「テストが固まった」と判断できる待ち上限。</summary>
    private static readonly TimeSpan TestGiveUp = TimeSpan.FromSeconds(10);

    /// <summary>指定時間内に完了しなければ「永久待ちの再発」としてテストを落とす。</summary>
    private static async Task<Task> WaitForCompletionAsync(Task task)
    {
        var finished = await Task.WhenAny(task, Task.Delay(TestGiveUp));
        Assert.True(ReferenceEquals(finished, task), "要求がタイムアウトで決着せず待ち続けている");
        return task;
    }

    /// <summary>
    /// 正常な応答を1件だけ返す疑似サーバー。要求が pending に登録されるより先に書き込んでしまうと
    /// 応答が捨てられてテストが不安定になるので、少し待ってから書く（登録はマイクロ秒単位なので
    /// 600ms あれば十分。上限は <see cref="TestGiveUp"/> 側で担保する）。
    /// </summary>
    private static string[] EchoServerArgs(int id) =>
    [
        "-NoProfile", "-Command",
        "Start-Sleep -Milliseconds 600; " +
        $"$b = '{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{{\"ok\":true}}}}'; " +
        "[Console]::Out.Write(\"Content-Length: $($b.Length)`r`n`r`n$b\"); " +
        "[Console]::Out.Flush(); Start-Sleep -Seconds 5",
    ];

    // タイムアウト導入で SendRequestAsync は async 化＋リンク CTS＋登録破棄と丸ごと書き換わった。
    // 無応答の検証だけでは、全 LSP 要求が通る唯一の道（正常系）が未検証のままになる。
    [Fact]
    public async Task Response_completes_the_request_and_clears_the_pending_table()
    {
        using var process = new LspProcess("powershell.exe", EchoServerArgs(id: 1));

        var result = await WaitForCompletionAsync(process.SendRequestAsync("initialize", new { }));

        var element = await (Task<System.Text.Json.JsonElement?>)result;
        Assert.NotNull(element);
        Assert.True(element!.Value.GetProperty("ok").GetBoolean());
        Assert.Equal(0, process.PendingRequestCount);
    }

    // 製品既定がずれたら気づけるようにする（テスト用シームで上書きできる以上、固定しておく）。
    [Fact]
    public void Default_timeout_is_conservative_enough_for_slow_but_healthy_servers()
    {
        Assert.Equal(TimeSpan.FromSeconds(60), LspProcess.DefaultRequestTimeout);
        using var process = new LspProcess("cmd.exe", MuteServerArgs);
        Assert.Equal(LspProcess.DefaultRequestTimeout, process.RequestTimeout);
    }

    [Fact]
    public async Task Request_without_a_response_times_out_instead_of_hanging_forever()
    {
        using var process = new LspProcess("cmd.exe", MuteServerArgs) { RequestTimeout = ShortTimeout };

        var request = process.SendRequestAsync("initialize", new { });

        await WaitForCompletionAsync(request);
        await Assert.ThrowsAsync<TimeoutException>(() => request);
    }

    [Fact]
    public async Task Timed_out_request_is_removed_from_the_pending_table()
    {
        using var process = new LspProcess("cmd.exe", MuteServerArgs) { RequestTimeout = ShortTimeout };

        var request = process.SendRequestAsync("workspace/symbol", new { query = "x" });
        Assert.Equal(1, process.PendingRequestCount);

        await WaitForCompletionAsync(request);

        Assert.Equal(0, process.PendingRequestCount);
    }

    [Fact]
    public async Task Caller_cancellation_still_wins_and_is_reported_as_cancellation_not_timeout()
    {
        // タイムアウトを合成しても、呼び出し側の CancellationToken の経路が壊れていないこと。
        using var process = new LspProcess("cmd.exe", MuteServerArgs)
        {
            RequestTimeout = TimeSpan.FromMinutes(5),
        };
        using var cts = new CancellationTokenSource();

        var request = process.SendRequestAsync("textDocument/completion", new { }, cts.Token);
        cts.Cancel();

        await WaitForCompletionAsync(request);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.Equal(0, process.PendingRequestCount);
    }

    [Fact]
    public async Task LspClient_feature_requests_degrade_to_empty_results_on_timeout()
    {
        // タイムアウトが致命的エラーに化けないこと: 各機能メソッドの try/catch が拾い、
        // 従来の「応答なし＝結果なし」と同じ見え方に収束する。
        using var client = new LspClient("cmd.exe", MuteServerArgs) { RequestTimeout = ShortTimeout };

        var completion = client.GetCompletionAsync("file:///c:/tmp/a.cs", new LspPosition(0, 0));

        await WaitForCompletionAsync(completion);
        Assert.Empty(await completion);
    }

    [Fact]
    public async Task LspClient_initialize_fails_fast_instead_of_leaving_the_host_in_connecting_state()
    {
        // InitializeAsync だけは例外を握りつぶさない。ホスト側 (LspClientPool) が Failed 状態へ落として
        // 理由を表示できるようにするためで、「初期化中のまま永久に固まる」よりは常に良い。
        using var client = new LspClient("cmd.exe", MuteServerArgs) { RequestTimeout = ShortTimeout };

        var initialize = client.InitializeAsync("file:///c:/tmp/");

        await WaitForCompletionAsync(initialize);
        await Assert.ThrowsAsync<TimeoutException>(() => initialize);
    }
}
