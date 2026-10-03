using System.Windows.Threading;
using Editor.Core.Buffer;
using Editor.Core.Editing;
using Editor.Core.Models;

namespace Editor.Controls;

/// <summary>
/// 連動編集（開きタグ名を書き換えると閉じタグ名も追従する — VS Code の Linked Editing）の
/// コントロール側。連動そのもの（範囲の追跡・写し・取り消し単位）はエンジン
/// （<see cref="Core.Engine.VimEngine.LinkedEditingRangeProvider"/>）が持ち、ここは
/// <list type="bullet">
/// <item>言語サーバーの <c>textDocument/linkedEditingRange</c> を<b>先回りで</b>問い合わせて控え、
/// エンジンが打鍵の後に同期的に引けるようにする（打鍵の経路で LSP を待たない）。</item>
/// <item>マルチカーソル中は連動を止める（両方が同じ行を書く）。</item>
/// <item>連動中の範囲をキャンバスへ渡す。</item>
/// </list>
///
/// <para>問い合わせはキャレットがタグ名の上に来たとき（1行だけ見る安い判定）に限り、手が止まってから
/// <see cref="LinkedEditingPrefetchDelayMs"/> 後に出す。答えは問い合わせた時点のバッファの版に紐づけ、
/// 版が変われば使わない。サーバーが無い・未対応の言語（XAML / XML など）はエンジンの自前判定
/// （<see cref="MarkupTagPairFinder"/>）が受け持つ。</para>
/// </summary>
public partial class VimEditorControl
{
    private const int LinkedEditingPrefetchDelayMs = 120;

    private DispatcherTimer? _linkedEditingPrefetchTimer;
    private CursorPosition _linkedEditingPrefetchAt;
    private int _linkedEditingPrefetchGeneration;
    private LinkedEditingPrefetch? _linkedEditingPrefetch;

    /// <summary>先回りの答え。<see cref="LinkedEditingRanges.None"/> なら「その場所に範囲は無い」と答えた。</summary>
    private sealed record LinkedEditingPrefetch(VimBuffer Buffer, long Version, int Line, int Column, LinkedEditingRanges Ranges);

    private void InitializeLinkedEditing()
    {
        _engine.LinkedEditingRangeProvider = ProvideLinkedEditingRanges;
    }

    /// <summary>エンジンからの問い合わせ（打鍵の後、UI スレッド上・同期）。控えた答えを返すだけ。</summary>
    private LinkedEditingRanges? ProvideLinkedEditingRanges(int line, int column, long version)
    {
        if (!_lspView.ServerSupportsLinkedEditingRange) return null;
        var cached = _linkedEditingPrefetch;
        if (cached is null || cached.Version != version || !ReferenceEquals(cached.Buffer, _engine.CurrentBuffer))
            return null;
        if (cached.Ranges.Ranges.Any(r => r.Touches(line, column))) return cached.Ranges;
        // 同じ場所で「無い」と言われたならそれに従う（自前判定で上書きしない）。別の場所なら知らない。
        return cached.Line == line && cached.Column == column ? LinkedEditingRanges.None : null;
    }

    /// <summary>キャレットが動いたら（Normal / Insert とも）、タグ名の上なら先回りの問い合わせを予約する。</summary>
    private void ScheduleLinkedEditingPrefetch(CursorPosition caret)
    {
        if (!_engine.Options.LinkedEditing || !_lspView.ServerSupportsLinkedEditingRange) return;
        if (_engine.LinkedEditingRanges.Count > 0) return; // 連動中は範囲を持っている
        var text = _engine.CurrentBuffer.Text;
        if (caret.Line >= text.LineCount || !MarkupTagPairFinder.IsAtTagName(text.GetLine(caret.Line), caret.Column))
        {
            _linkedEditingPrefetchTimer?.Stop();
            return;
        }

        var cached = _linkedEditingPrefetch;
        if (cached is not null && cached.Version == text.Version && ReferenceEquals(cached.Buffer, _engine.CurrentBuffer) &&
            (cached.Ranges.Ranges.Any(r => r.Touches(caret.Line, caret.Column)) ||
             (cached.Line == caret.Line && cached.Column == caret.Column)))
            return; // この版・この場所の答えはもう持っている

        _linkedEditingPrefetchAt = caret;
        _linkedEditingPrefetchTimer ??= CreateLinkedEditingPrefetchTimer();
        _linkedEditingPrefetchTimer.Stop();
        _linkedEditingPrefetchTimer.Start();
    }

    private DispatcherTimer CreateLinkedEditingPrefetchTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(LinkedEditingPrefetchDelayMs),
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _ = PrefetchLinkedEditingRangesAsync(_linkedEditingPrefetchAt);
        };
        return timer;
    }

    private async Task PrefetchLinkedEditingRangesAsync(CursorPosition at)
    {
        int generation = ++_linkedEditingPrefetchGeneration;
        var buffer = _engine.CurrentBuffer;
        long version = buffer.Text.Version;
        LinkedEditingRanges? ranges;
        try
        {
            ranges = await _lspView.RequestLinkedEditingRangesAsync(at.Line, at.Column);
        }
        catch
        {
            return; // サーバーの不調で編集を止めない（自前判定か、連動なしで続く）
        }
        if (ranges is null || generation != _linkedEditingPrefetchGeneration) return;
        // 応答を待つ間に打鍵があれば、その答えは古い版のもの
        if (!ReferenceEquals(buffer, _engine.CurrentBuffer) || buffer.Text.Version != version) return;
        _linkedEditingPrefetch = new LinkedEditingPrefetch(buffer, version, at.Line, at.Column, ranges);
    }

    /// <summary>連動中の範囲をキャンバスへ。打鍵・移動のたびに呼ばれる（参照を渡すだけ）。</summary>
    private void RefreshLinkedEditingRanges()
        => Canvas.SetLinkedEditingRanges(_engine.LinkedEditingRanges);
}
