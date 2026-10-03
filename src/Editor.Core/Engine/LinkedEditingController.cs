using Editor.Core.Buffer;
using Editor.Core.Editing;
using Editor.Core.Models;

namespace Editor.Core.Engine;

/// <summary>
/// 連動編集（開きタグ名を書き換えると閉じタグ名も追従する）をエンジンの打鍵の前後に差し込む。
///
/// <para><b>打鍵の前</b>には安いもの（バッファ・版・カーソル・カーソル行の参照・行数）だけを控え、
/// <b>打鍵の後</b>にカーソル行の差分を見る。連動の開始もこの「後から」で判定する — 挿入モードの
/// 1文字目だけでなく <c>ciw</c> / <c>cw</c> の削除で始まる編集も拾えるのは、編集前の文書が
/// 「今の文書のカーソル行だけを控えた旧行に戻したもの」として手元にあるから。</para>
///
/// <para>写し先の行はバッファへ直接書き、取り消し用のスナップショットは取らない。挿入モードの
/// 取り消し単位は挿入に入る時点のスナップショット（Vim 無効時は編集の一続き）なので、
/// <c>u</c> 1回で主範囲と写し先が一緒に戻る。</para>
/// </summary>
internal sealed class LinkedEditingController(
    Func<VimBuffer> currentBuffer,
    Func<CursorPosition> cursor,
    Action<CursorPosition> setCursor,
    Func<VimMode> mode,
    Func<bool> enabled,
    Func<bool> rejectsEdits,
    Action textChangedExternally)
{
    private LinkedEditingSession? _session;
    private VimBuffer? _sessionBuffer;
    private long _sessionVersion;
    // 対が見つからなかったタグ（バッファ, 行, '<' の列）。新しいタグ名を打ち込んでいる間は対が
    // 無いのが普通なので、同じタグの中で打ち続ける限り文書をなめ直さない。移動・タグの外への編集で忘れる。
    private (VimBuffer Buffer, int Line, int TagStart)? _noPair;

    /// <summary>ホスト（言語サーバー）が持つ範囲。引数は編集前の (行, 列, バッファの版)。
    /// null は「答えを持っていない」（エンジンはフォールバックを試す）、
    /// <see cref="LinkedEditingRanges.None"/> は「範囲は無い」。</summary>
    public Func<int, int, long, LinkedEditingRanges?>? Provider { get; set; }

    /// <summary>マルチカーソル中などホストが連動を止めたいとき true。</summary>
    public bool Suspended { get; set; }

    /// <summary>いま連動している範囲。バッファが外から書き換わっていれば空。</summary>
    public IReadOnlyList<LinkedRange> ActiveRanges =>
        _session is not null && IsSessionCurrent(currentBuffer()) ? _session.Ranges : [];

    public bool IsActive => ActiveRanges.Count > 0;

    public void End()
    {
        _session = null;
        _sessionBuffer = null;
    }

    public readonly record struct Before(
        VimBuffer Buffer, long Version, CursorPosition Cursor, int LineCount, string LineText);

    public Before Capture()
    {
        var buffer = currentBuffer();
        var text = buffer.Text;
        var c = cursor();
        var line = c.Line < text.LineCount ? text.GetLine(c.Line) : "";
        return new Before(buffer, text.Version, c, text.LineCount, line);
    }

    public void After(Before before, List<VimEvent> events)
    {
        // 打鍵の前の時点で、最後に写した版のままか（外からの書き換え・取り消しが挟まっていないか）。
        if (_session is not null && (!ReferenceEquals(_sessionBuffer, before.Buffer) || before.Version != _sessionVersion))
            End();

        var buffer = currentBuffer();
        var text = buffer.Text;
        var m = mode();
        bool inserting = m is VimMode.Insert or VimMode.Replace;
        var caret = cursor();

        if (!enabled() || Suspended || rejectsEdits() || !inserting || !ReferenceEquals(buffer, before.Buffer))
        {
            End();
            return;
        }

        if (text.Version == before.Version)
        {
            // 移動だけ: 範囲の外へ出たら連動を解く。
            if (_session is not null && !_session.Touches(caret.Line, caret.Column)) End();
            _noPair = null;
            return;
        }

        if (text.LineCount != before.LineCount || caret.Line != before.Cursor.Line)
        {
            End();
            return;
        }

        int line = before.Cursor.Line;
        var newLine = text.GetLine(line);
        if (string.Equals(newLine, before.LineText, StringComparison.Ordinal))
        {
            End(); // カーソル行以外が変わった（連動の想定外）
            return;
        }

        string LineOf(int l) => l == line ? before.LineText : text.GetLine(l);
        _session ??= TryStart(before, buffer, LineOf);
        if (_session is null) return;

        var update = _session.Apply(line, before.LineText, newLine, before.Cursor.Column, text.GetLine);
        if (update is null)
        {
            End();
            return;
        }

        if (update.Lines.Count > 0)
        {
            foreach (var (l, s) in update.Lines)
                text.ReplaceLine(l, s);
            textChangedExternally();
            if (update.CaretShift != 0)
            {
                var moved = caret with { Column = Math.Max(0, caret.Column + update.CaretShift) };
                setCursor(moved);
                events.Add(VimEvent.CursorMoved(moved));
            }
            if (!events.Any(e => e.Type == VimEventType.TextChanged))
                events.Add(VimEvent.TextChanged());
        }

        _sessionBuffer = buffer;
        _sessionVersion = text.Version;
    }

    private LinkedEditingSession? TryStart(Before before, VimBuffer buffer, Func<int, string> lineOf)
    {
        int line = before.Cursor.Line;
        int column = Math.Min(before.Cursor.Column, before.LineText.Length);
        if (!MarkupTagPairFinder.IsAtTagName(before.LineText, column))
        {
            _noPair = null;
            return null;
        }

        var ranges = Provider?.Invoke(line, column, before.Version);
        if (ranges is null)
        {
            var dialect = MarkupTagPairFinder.DialectFor(buffer.FilePath);
            if (dialect is null) return null;
            int tagStart = column;
            while (tagStart > 0 && before.LineText[tagStart - 1] != '<') tagStart--;
            var key = (buffer, line, tagStart);
            if (_noPair == key) return null;
            ranges = MarkupTagPairFinder.Find(lineOf, before.LineCount, line, column, dialect.Value);
            if (ranges is null)
            {
                _noPair = key;
                return null;
            }
        }
        if (!ranges.Ranges.Any(r => r.Touches(line, column))) return null;
        _noPair = null;
        return LinkedEditingSession.TryStart(ranges, lineOf);
    }

    private bool IsSessionCurrent(VimBuffer buffer) =>
        ReferenceEquals(_sessionBuffer, buffer) && buffer.Text.Version == _sessionVersion;
}
