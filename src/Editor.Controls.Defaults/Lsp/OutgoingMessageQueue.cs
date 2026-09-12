namespace Editor.Controls.Lsp;

/// <summary>送信待ちの 1 通。本文は送信直前に <see cref="PayloadFactory"/> で作る。</summary>
internal sealed record OutgoingMessage(
    Func<object> PayloadFactory, string? CoalesceKey, Action<Exception>? OnFailure);

/// <summary>
/// 言語サーバーへの送信待ち行列。積んだ順に出す FIFO だが、<c>CoalesceKey</c> を持つ通知は
/// 未送信のうちは<b>最新の 1 通だけ</b>を保持する（打鍵ごとの <c>didChange</c> のため）。
///
/// <para>置き換えのときキュー内の位置は動かさない。動かすと、あとから積まれた要求（補完など）が
/// 古い本文のまま先にサーバーへ届く——サーバーは受け取った順に処理するので、
/// 文書の更新は必ずそれを使う要求より前になければならない。</para>
///
/// <para>スレッド安全。<see cref="LspProcess"/> の書き込みスレッドが <see cref="TryDequeue"/> し、
/// 任意のスレッド（多くは UI スレッド）が <see cref="Enqueue"/> する。</para>
/// </summary>
internal sealed class OutgoingMessageQueue
{
    private readonly object _gate = new();
    private readonly LinkedList<OutgoingMessage> _queue = new();
    private readonly Dictionary<string, LinkedListNode<OutgoingMessage>> _coalesced = new(StringComparer.Ordinal);

    /// <summary>待っている通数。テストと診断用。</summary>
    public int Count { get { lock (_gate) return _queue.Count; } }

    /// <summary>積む。戻り値 true なら新しい項目が増えた（＝書き込みスレッドを起こす必要がある）、
    /// false なら未送信の同キーを置き換えただけで通数は変わっていない。</summary>
    public bool Enqueue(OutgoingMessage message)
    {
        lock (_gate)
        {
            if (message.CoalesceKey is not null && _coalesced.TryGetValue(message.CoalesceKey, out var existing))
            {
                existing.Value = message;
                return false;
            }

            var node = _queue.AddLast(message);
            if (message.CoalesceKey is not null) _coalesced[message.CoalesceKey] = node;
            return true;
        }
    }

    /// <summary>先頭を取り出す。空なら false。</summary>
    public bool TryDequeue(out OutgoingMessage message)
    {
        lock (_gate)
        {
            var node = _queue.First;
            if (node is null)
            {
                message = null!;
                return false;
            }

            _queue.RemoveFirst();
            message = node.Value;
            // 取り出した項目がそのキーの追跡対象だったときだけ外す。置き換えで別の項目が
            // 追跡対象になっていることはない（置き換えは同じ node の中身を差し替えるため）が、
            // DropCoalesceKey 後に積まれた新しい項目を巻き添えで外さないための確認。
            if (message.CoalesceKey is not null &&
                _coalesced.TryGetValue(message.CoalesceKey, out var tracked) && ReferenceEquals(tracked, node))
                _coalesced.Remove(message.CoalesceKey);
            return true;
        }
    }

    /// <summary>指定キーの追跡をやめる（積まれている項目自体はそのまま順番に送られる）。
    /// 文書を開き直すときに呼び、閉じる前の項目へ新しい本文が差し替わらないようにする。</summary>
    public void DropCoalesceKey(string coalesceKey)
    {
        lock (_gate) _coalesced.Remove(coalesceKey);
    }

    /// <summary>積み残しを捨てる。</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _queue.Clear();
            _coalesced.Clear();
        }
    }
}
