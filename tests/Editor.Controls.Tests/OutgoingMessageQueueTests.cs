using Editor.Controls.Lsp;

namespace Editor.Controls.Tests;

/// <summary>
/// 打鍵ごとの <c>didChange</c> を UI スレッドから外したときの中核。キューの不変条件が崩れると
/// 「サーバーが古い本文を見たまま補完する」「本文の更新が届かない」といった、実機でしか見えない
/// 壊れ方をするので、純粋な部分をここで固定しておく。
/// </summary>
public sealed class OutgoingMessageQueueTests
{
    private static OutgoingMessage Message(string payload, string? coalesceKey = null)
        => new(() => payload, coalesceKey, null);

    private static string Dequeue(OutgoingMessageQueue queue)
    {
        Assert.True(queue.TryDequeue(out var message), "取り出せるはずの項目が無い");
        return (string)message.PayloadFactory();
    }

    [Fact]
    public void Messages_without_a_coalesce_key_come_out_in_the_order_they_went_in()
    {
        var queue = new OutgoingMessageQueue();

        Assert.True(queue.Enqueue(Message("a")));
        Assert.True(queue.Enqueue(Message("b")));

        Assert.Equal(2, queue.Count);
        Assert.Equal("a", Dequeue(queue));
        Assert.Equal("b", Dequeue(queue));
        Assert.False(queue.TryDequeue(out _));
    }

    [Fact]
    public void A_pending_message_with_the_same_key_is_replaced_instead_of_queued_again()
    {
        var queue = new OutgoingMessageQueue();

        Assert.True(queue.Enqueue(Message("v1", "doc")));
        Assert.False(queue.Enqueue(Message("v2", "doc")));   // 通数は増えない＝起こす必要もない
        Assert.False(queue.Enqueue(Message("v3", "doc")));

        Assert.Equal(1, queue.Count);
        Assert.Equal("v3", Dequeue(queue));
    }

    /// <summary>置き換えは「最後に積まれた場所」ではなく「元の場所」で起きる。ここが崩れると、
    /// あとから積んだ補完要求が古い本文のまま先にサーバーへ届く。</summary>
    [Fact]
    public void Replacing_keeps_the_original_queue_position_so_later_requests_still_follow_it()
    {
        var queue = new OutgoingMessageQueue();

        queue.Enqueue(Message("change v1", "doc"));
        queue.Enqueue(Message("completion request"));
        queue.Enqueue(Message("change v2", "doc"));

        Assert.Equal(2, queue.Count);
        Assert.Equal("change v2", Dequeue(queue));
        Assert.Equal("completion request", Dequeue(queue));
    }

    [Fact]
    public void A_key_is_free_to_be_queued_again_once_its_message_has_been_taken()
    {
        var queue = new OutgoingMessageQueue();

        queue.Enqueue(Message("v1", "doc"));
        Assert.Equal("v1", Dequeue(queue));

        Assert.True(queue.Enqueue(Message("v2", "doc")));
        Assert.Equal(1, queue.Count);
        Assert.Equal("v2", Dequeue(queue));
    }

    /// <summary>文書を開き直す場面。追跡をやめたあとの本文は、閉じる前の項目へ差し替わらず
    /// 新しい項目として後ろに積まれる（＝didClose / didOpen より後に届く）。</summary>
    [Fact]
    public void Dropping_a_key_leaves_the_queued_message_alone_and_starts_a_new_one()
    {
        var queue = new OutgoingMessageQueue();

        queue.Enqueue(Message("change before close", "doc"));
        queue.DropCoalesceKey("doc");
        queue.Enqueue(Message("close"));
        queue.Enqueue(Message("open"));
        Assert.True(queue.Enqueue(Message("change after open", "doc")));

        Assert.Equal("change before close", Dequeue(queue));
        Assert.Equal("close", Dequeue(queue));
        Assert.Equal("open", Dequeue(queue));
        Assert.Equal("change after open", Dequeue(queue));
    }

    /// <summary>取り出しは「その項目が今も追跡対象か」を見て外す。Drop 後に積み直した項目を
    /// 巻き添えで追跡解除すると、次の打鍵が置き換えではなく新規項目として積まれ続ける。</summary>
    [Fact]
    public void Taking_an_untracked_message_does_not_release_the_key_of_a_newer_one()
    {
        var queue = new OutgoingMessageQueue();

        queue.Enqueue(Message("stale", "doc"));
        queue.DropCoalesceKey("doc");
        queue.Enqueue(Message("fresh v1", "doc"));

        Assert.Equal("stale", Dequeue(queue));           // 追跡対象ではない項目を取り出す
        Assert.False(queue.Enqueue(Message("fresh v2", "doc")));   // 新しい方はまだ追跡されている

        Assert.Equal(1, queue.Count);
        Assert.Equal("fresh v2", Dequeue(queue));
    }

    [Fact]
    public void Clear_drops_everything_including_the_coalesce_tracking()
    {
        var queue = new OutgoingMessageQueue();

        queue.Enqueue(Message("a"));
        queue.Enqueue(Message("v1", "doc"));
        queue.Clear();

        Assert.Equal(0, queue.Count);
        Assert.False(queue.TryDequeue(out _));
        Assert.True(queue.Enqueue(Message("v2", "doc")));
    }

    /// <summary>本文は送信直前にしか作らない。追い越された版の組み立て（数百 KB の文字列化）を
    /// 丸ごと省けることが、この仕組みの効き目そのもの。</summary>
    [Fact]
    public void A_replaced_message_never_builds_its_payload()
    {
        var queue = new OutgoingMessageQueue();
        int built = 0;

        for (int i = 0; i < 10; i++)
            queue.Enqueue(new OutgoingMessage(() => { built++; return "payload"; }, "doc", null));

        Assert.Equal(0, built);
        Dequeue(queue);
        Assert.Equal(1, built);
    }
}
