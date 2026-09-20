using System;
using System.Threading.Tasks;
using System.Windows.Threading;
using Editor.Core.Lsp;

namespace Editor.Controls;

/// <summary>
/// VimEditorControl 側のクイックフィックス電球。列そのものは
/// <see cref="Editor.Controls.Rendering.EditorCanvas"/>（<c>EditorCanvas.CodeActionBulb.cs</c>）にあり、
/// ここが持つのは<b>どの行に電球を出すか</b>——それは LSP／ホストに訊かないと判らないので、
/// キャレット行の様子が変わるたびに静かに問い合わせる。
///
/// <para><b>空振りする電球は出さない</b>。押しても「no fixes available」と出るだけの電球は、
/// 一度でも当たれば以後信用されなくなる。なので<see cref="CollectQuickFixesAsync"/>（Alt+Enter と
/// ホバーの電球と<b>同じ</b>経路）で実際に候補が返ったときにだけ点ける。</para>
///
/// <para><b>問い合わせは絞る</b>。キャレットが動くたびにサーバーへ投げると、Roslyn のような
/// 重いサーバーでは移動そのものが重くなる。そこで (1) 250ms の落ち着き待ち、
/// (2) <b>キャレット行に診断がかかっているときだけ</b>問う、(3) 同じ行・同じ診断なら問い直さない
/// ——の 3 段で抑える。診断が無い行の「抽出」系リファクタリングは従来どおり Alt+Enter の担当で、
/// 電球には出さない（出すには全移動で問い合わせが必要になる）。</para>
/// </summary>
public partial class VimEditorControl
{
    private DispatcherTimer? _codeActionBulbTimer;
    // 診断の世代。診断が届く／本文が変わるたびに進める。同じ (行, 世代) は問い直さない。
    private int _codeActionBulbGeneration;
    private int _codeActionBulbProbedLine = -1;
    private int _codeActionBulbProbedGeneration = -1;
    // 走っている問い合わせの識別。追い越された応答で電球を点けないための札。
    private int _codeActionBulbProbeToken;

    private const double CodeActionBulbDebounceMs = 250;

    /// <summary>電球列（blame の右＝ガター最左のグリフ余白）を有効化/無効化する。既定は無効（幅 0）。
    /// 言語サーバーもホストの修正提供も無いファイルで有効にすると、空の列が残るだけになる。</summary>
    public void SetCodeActionBulbEnabled(bool enabled)
    {
        Canvas.SetCodeActionBulbEnabled(enabled);
        if (enabled) ScheduleCodeActionBulbProbe();
        else StopCodeActionBulbProbe();
    }

    /// <summary>いま電球が出ている行（0始まり、無ければ -1）。</summary>
    internal int CodeActionBulbLine => Canvas.CodeActionBulbLine;

    /// <summary>診断が変わった／本文が変わった。世代を進めて（＝前の判定を無効にして）問い直す。</summary>
    private void InvalidateCodeActionBulb()
    {
        _codeActionBulbGeneration++;
        ScheduleCodeActionBulbProbe();
    }

    /// <summary>キャレット行の様子が変わったので、落ち着いたら問い合わせる。</summary>
    private void ScheduleCodeActionBulbProbe()
    {
        if (!Canvas.IsCodeActionBulbEnabled) return;

        _codeActionBulbTimer ??= CreateCodeActionBulbTimer();
        _codeActionBulbTimer.Stop();
        _codeActionBulbTimer.Start();
    }

    private DispatcherTimer CreateCodeActionBulbTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(CodeActionBulbDebounceMs),
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _ = ProbeCodeActionBulbAsync();
        };
        return timer;
    }

    /// <summary>別のファイルを載せた：点いている電球も、問い直さない記憶も捨てる。</summary>
    private void ResetCodeActionBulb()
    {
        _codeActionBulbTimer?.Stop();
        _codeActionBulbProbedLine = -1;
        _codeActionBulbProbedGeneration = -1;
        _codeActionBulbProbeToken++;
        Canvas.SetCodeActionBulbLine(-1);
        ScheduleCodeActionBulbProbe();
    }

    private void StopCodeActionBulbProbe()
    {
        _codeActionBulbTimer?.Stop();
        _codeActionBulbProbedLine = -1;
        _codeActionBulbProbedGeneration = -1;
        _codeActionBulbProbeToken++;   // 走っている応答を無効化する
        Canvas.SetCodeActionBulbLine(-1);
    }

    private async Task ProbeCodeActionBulbAsync()
    {
        if (!Canvas.IsCodeActionBulbEnabled) return;

        int line = _engine.Cursor.Line;
        int generation = _codeActionBulbGeneration;
        if (line == _codeActionBulbProbedLine && generation == _codeActionBulbProbedGeneration) return;

        // 診断のかかっていない行では問わない（問い合わせの数を抑える／§ クラス説明の (2)）。
        if (FirstDiagnosticOnLine(line) is not { } diagnostic)
        {
            _codeActionBulbProbedLine = line;
            _codeActionBulbProbedGeneration = generation;
            Canvas.SetCodeActionBulbLine(-1);
            return;
        }

        int token = ++_codeActionBulbProbeToken;
        // 行全体ではなく診断の範囲で問う——「この問題に対する修正」を集める Alt+Enter と同じ形。
        var (actions, _) = await CollectQuickFixesAsync(diagnostic.Range, announce: false);

        // 追い越されていたら（キャレットが動いた／診断が届いた）この結果は捨てる。
        if (token != _codeActionBulbProbeToken || !Canvas.IsCodeActionBulbEnabled) return;

        _codeActionBulbProbedLine = line;
        _codeActionBulbProbedGeneration = generation;
        Canvas.SetCodeActionBulbLine(actions is { Count: > 0 } ? line : -1);
    }

    /// <summary>その行にかかっている診断のうち最初のもの（無ければ null）。
    /// 重大度では絞らない——ヒント（未使用の using 等）こそ修正候補を持つ。</summary>
    private LspDiagnostic? FirstDiagnosticOnLine(int line)
    {
        foreach (var diagnostic in _lspDiagnosticPresentation ?? _lspView.CurrentDiagnostics)
            if (diagnostic.Range.Start.Line <= line && diagnostic.Range.End.Line >= line)
                return diagnostic;

        foreach (var diagnostic in _hostDiagnostics)
            if (diagnostic.Range.Start.Line <= line && diagnostic.Range.End.Line >= line)
                return new LspDiagnostic(
                    new LspRange(
                        new LspPosition(diagnostic.Range.Start.Line, diagnostic.Range.Start.Column),
                        new LspPosition(diagnostic.Range.End.Line, diagnostic.Range.End.Column)),
                    diagnostic.Message,
                    DiagnosticSeverity.Information);

        return null;
    }

    /// <summary>ガターの電球が押された：その行のクイックフィックスを出す。
    /// 候補は<b>押した時点で取り直す</b>——点灯時の一覧を持ち回ると、間の編集で古くなった候補を
    /// 適用してしまう。</summary>
    private void OnCanvasCodeActionBulbClicked(int bufferLine)
    {
        Focus();
        if (bufferLine != _engine.Cursor.Line)
            ProcessVimEvents(_engine.SetCursorPosition(new Editor.Core.Models.CursorPosition(bufferLine, 0)));
        _ = HandleQuickFixAsync();
    }

    /// <summary>スクロールバーの診断マーカーが押された：その問題へ飛ぶ。</summary>
    private void OnCanvasDiagnosticMarkClicked(DiagnosticOverviewMark mark)
    {
        JumpToLine(mark.Line, mark.Column);
        Focus();
    }
}
