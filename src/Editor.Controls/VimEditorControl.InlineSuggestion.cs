using System.Windows.Threading;
using Editor.Core.Completion;
using Editor.Core.Engine;
using Editor.Core.Lsp;
using Editor.Core.Models;

namespace Editor.Controls;

/// <summary>
/// 入力の先読み。キャレットの先に「この行はこう続くだろう」を薄く出し、Tab で受け入れる。
///
/// <para>提案の出どころは 2 つある。内蔵の <see cref="BufferLinePredictor"/> は同じバッファの
/// 既出行から即座に答える（外部依存も待ち時間もない）。ホストが
/// <see cref="VimEditorControlOptions.InlineSuggestionProvider"/> を差していれば、そちらへも
/// 非同期で尋ね、返ってきたら内蔵の答えを置き換える——内蔵の提案が<b>先に</b>出るので、
/// 遅い供給元を待つあいだ何も出ない時間ができない。</para>
///
/// <para>出す条件は意図して狭い。Insert モード、キャレットが行末、選択なし、
/// 補完ポップアップもスニペットも動いていないとき<b>だけ</b>。重なるとどれが Tab を取るのか
/// 読めなくなるし、外れた提案が薄く出続けるのは何も出ないより悪い。</para>
/// </summary>
public partial class VimEditorControl
{
    /// <summary>手が止まったと見なすまで。短すぎると打鍵のたびにちらつき、長いと出てこない。</summary>
    private const int InlineSuggestionDebounceMs = 120;

    /// <summary>ホストが差した供給元（未設定なら内蔵の予測だけで動く）。</summary>
    private readonly Func<InlineSuggestionContext, CancellationToken, Task<InlineSuggestion?>>? _inlineSuggestionProvider;

    private InlineSuggestion? _inlineSuggestion;
    private int _inlineSuggestionLine = -1;
    private int _inlineSuggestionColumn = -1;
    private DispatcherTimer? _inlineSuggestionDebounce;
    private int _inlineSuggestionGeneration;

    /// <summary>いま提案が出ているか。Tab をどちらが取るかの判断に使う。</summary>
    private bool InlineSuggestionVisible => _inlineSuggestion is not null;

    /// <summary>打鍵・カーソル移動・モード変更のあとに呼ぶ。出せる状況なら作り直し、そうでなければ消す。</summary>
    private void UpdateInlineSuggestion()
    {
        if (!CanShowInlineSuggestion())
        {
            ClearInlineSuggestion();
            return;
        }

        // いま出ている提案が、打った 1 文字ぶんそのまま前へ進んだだけなら作り直さない。
        // 予測が当たっている間は提案が動かずに縮んでいく（ちらつかない）。
        if (TryAdvanceInlineSuggestion()) return;

        ClearInlineSuggestion();
        _inlineSuggestionDebounce ??= CreateInlineSuggestionDebounce();
        _inlineSuggestionDebounce.Stop();
        _inlineSuggestionDebounce.Start();
    }

    private bool CanShowInlineSuggestion()
    {
        if (!_engine.Options.InlineSuggest) return false;
        if (_engine.Mode != VimMode.Insert) return false;
        if (_lspView.CompletionVisible || _pathCompletionManager.Visible) return false;
        if (_snippetTabStopManager.IsActive) return false;
        if (_multiCursorManager.IsActive) return false;

        var cursor = _engine.Cursor;
        var line = _engine.CurrentBuffer.Text.GetLine(cursor.Line);
        return cursor.Column >= line.Length;   // 行末だけ
    }

    /// <summary>
    /// 出ている提案の先頭 1 文字がいま打った文字と同じなら、その 1 文字を削って据え置く。
    /// 予測が当たり続けている間、提案を作り直さずに済ませるための道。
    /// </summary>
    private bool TryAdvanceInlineSuggestion()
    {
        if (_inlineSuggestion is not { } suggestion) return false;

        var cursor = _engine.Cursor;
        if (cursor.Line != _inlineSuggestionLine) return false;
        if (cursor.Column != _inlineSuggestionColumn + 1) return false;

        var line = _engine.CurrentBuffer.Text.GetLine(cursor.Line);
        if (cursor.Column > line.Length || cursor.Column == 0) return false;

        var text = suggestion.Text;
        if (text.Length == 0 || text[0] != line[cursor.Column - 1]) return false;

        var rest = text[1..];
        if (rest.Length == 0)
        {
            ClearInlineSuggestion();   // 予測しきった
            return true;
        }

        SetInlineSuggestion(suggestion with { Text = rest }, cursor.Line, cursor.Column);
        return true;
    }

    private DispatcherTimer CreateInlineSuggestionDebounce()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(InlineSuggestionDebounceMs) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!CanShowInlineSuggestion()) return;

            var buffer = _engine.CurrentBuffer;
            var cursor = _engine.Cursor;
            var lines = GetCachedLines(buffer);
            int generation = ++_inlineSuggestionGeneration;

            var builtIn = BufferLinePredictor.Predict(lines, cursor.Line, cursor.Column);
            if (builtIn is { } immediate)
                SetInlineSuggestion(immediate, cursor.Line, cursor.Column);

            RequestHostInlineSuggestion(
                new InlineSuggestionContext(lines, cursor.Line, cursor.Column, buffer.FilePath), generation);
        };
        return timer;
    }

    /// <summary>ホストの供給元（ローカル LLM など）へ非同期で尋ねる。差されていなければ何もしない。</summary>
    private void RequestHostInlineSuggestion(InlineSuggestionContext context, int generation)
    {
        var provider = _inlineSuggestionProvider;
        if (provider is null) return;

        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _inlineSuggestionCts, cts)?.Cancel();

        _ = Task.Run(async () =>
        {
            InlineSuggestion? suggestion;
            try
            {
                suggestion = await provider(context, cts.Token);
            }
            catch (OperationCanceledException) { return; }
            catch { return; }   // 供給元の失敗で入力を邪魔しない

            if (suggestion is not { Text.Length: > 0 } result) return;

            await Dispatcher.BeginInvoke(() =>
            {
                // 待っている間に打たれていたら捨てる。古い予測で本文を上書きさせない。
                if (generation != _inlineSuggestionGeneration) return;
                if (!CanShowInlineSuggestion()) return;
                var cursor = _engine.Cursor;
                if (cursor.Line != context.Line || cursor.Column != context.Column) return;

                SetInlineSuggestion(result, cursor.Line, cursor.Column);
            });
        });
    }

    private void SetInlineSuggestion(InlineSuggestion suggestion, int line, int column)
    {
        _inlineSuggestion = suggestion;
        _inlineSuggestionLine = line;
        _inlineSuggestionColumn = column;
        Canvas.SetInlineSuggestion(suggestion.FirstLine, line, column);
    }

    private void ClearInlineSuggestion()
    {
        _inlineSuggestionDebounce?.Stop();
        Interlocked.Exchange(ref _inlineSuggestionCts, null)?.Cancel();
        _inlineSuggestionGeneration++;

        if (_inlineSuggestion is null) return;
        _inlineSuggestion = null;
        _inlineSuggestionLine = -1;
        _inlineSuggestionColumn = -1;
        Canvas.SetInlineSuggestion(null, -1, 0);
    }

    private CancellationTokenSource? _inlineSuggestionCts;

    /// <summary>
    /// 提案を本文へ入れる。<paramref name="wordOnly"/> なら 1 語ぶんだけ入れ、残りは提案として残す
    /// （提案の一部だけが欲しいときに、全部消してから打ち直さずに済む）。
    /// </summary>
    private bool AcceptInlineSuggestion(bool wordOnly)
    {
        if (_inlineSuggestion is not { } suggestion) return false;

        var cursor = _engine.Cursor;
        if (cursor.Line != _inlineSuggestionLine || cursor.Column != _inlineSuggestionColumn)
        {
            ClearInlineSuggestion();
            return false;
        }

        var whole = suggestion.FirstLine;
        int take = wordOnly ? suggestion.NextWordLength() : whole.Length;
        if (take <= 0) return false;
        var accepted = whole[..take];

        var original = _engine.CurrentBuffer.Text.GetText();
        var position = new LspPosition(cursor.Line, cursor.Column);
        var updated = ApplyTextEdits(original, [new LspTextEdit(new LspRange(position, position), accepted)]);

        ProcessVimEvents(_engine.ApplyExternalText(updated));
        ProcessVimEvents(_engine.SetCursorPosition(new CursorPosition(cursor.Line, cursor.Column + accepted.Length)));
        _lspView.OnTextChanged(updated);
        UpdateAll();

        var rest = whole[take..];
        if (rest.Length == 0)
        {
            ClearInlineSuggestion();
            UpdateInlineSuggestion();   // 続きがまだあるかもしれない
        }
        else
        {
            SetInlineSuggestion(suggestion with { Text = rest }, cursor.Line, cursor.Column + accepted.Length);
        }
        return true;
    }
}
