using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Editor.Controls.Rendering;

namespace Editor.Controls;

/// <summary>
/// VimEditorControl のデバッグ用ガター連携。ブレークポイント列・実行中行ハイライトの土台は
/// <see cref="Editor.Controls.Rendering.EditorCanvas"/> 側にあり、ここはホスト（デバッガ）向けの薄い公開窓口。
/// 既定では無効で、<see cref="SetBreakpointsEnabled"/> を呼ぶまで表示・挙動とも従来どおり。
/// デバッグ停止中は <see cref="SetDataTipsEnabled"/> を有効化し <see cref="DataTipEvaluator"/> を設定すると、
/// 本文ホバーで式を評価して値ポップアップ（DataTip）を表示する。
/// </summary>
public partial class VimEditorControl
{
    /// <summary>ガターのブレークポイント列がクリックされ、その行がトグルされたとき。引数はバッファ行（0始まり）。
    /// ホストはこれを購読してデバッグアダプタへ反映し、<see cref="SetBreakpoints(IReadOnlyList{EditorBreakpoint})"/> で確定状態を返す。</summary>
    public event Action<int>? BreakpointToggled;

    /// <summary>ブレークポイント列（左端のガター）を有効化/無効化する。</summary>
    public void SetBreakpointsEnabled(bool enabled) => Canvas.SetBreakpointsEnabled(enabled);

    /// <summary>このドキュメントの全ブレークポイント行（0始まり）を設定する（種別なしの単純なブレークポイント）。</summary>
    public void SetBreakpoints(IEnumerable<int> lines) => Canvas.SetBreakpoints(lines);

    /// <summary>このドキュメントの全ブレークポイントを種別・有効状態つきで設定する（条件付き＋／ログポイント◆／無効は中抜きで描き分ける）。</summary>
    public void SetBreakpoints(IReadOnlyList<EditorBreakpoint> breakpoints) => Canvas.SetBreakpoints(breakpoints);

    /// <summary>デバッガが停止している現在行（0始まり、無ければ -1）を設定する。</summary>
    public void SetExecutionLine(int bufferLine) => Canvas.SetExecutionLine(bufferLine);

    private void OnCanvasBreakpointToggled(int bufferLine)
    {
        BreakpointToggled?.Invoke(bufferLine);
        Focus();
    }

    // ───────────────────────── DataTip（デバッグ中のホバー値表示）─────────────────────────

    /// <summary>
    /// DataTip の評価ハンドラ。ホストが式を現在のスタックフレームで評価し、表示する値（例 <c>"42"</c> や
    /// <c>"{Length=3}"</c>）を返す。null/空を返すと何も表示しない。デバッグが停止していない間は
    /// <see cref="SetDataTipsEnabled"/> を false にしておけば呼ばれない。
    /// </summary>
    public Func<DataTipRequest, CancellationToken, Task<string?>>? DataTipEvaluator { get; set; }

    /// <summary>本文ホバーによる DataTip 連携の有効/無効を切り替える（通常は停止時に有効化し、再開・終了で無効化）。</summary>
    public void SetDataTipsEnabled(bool enabled)
    {
        Canvas.SetDataTipsEnabled(enabled);
        if (!enabled) HideDataTip();
    }

    private System.Windows.Threading.DispatcherTimer? _dataTipDwell;
    private Popup? _dataTipPopup;
    private TextBlock? _dataTipText;
    private CancellationTokenSource? _dataTipCts;
    private DataTipHover _pendingDataTip;
    private string? _shownDataTipExpr;

    // 評価前のドウェル（マウスが止まってから問い合わせるまで）。スイープ中の連打を防ぐ。
    private const int DataTipDwellMs = 350;

    private void OnCanvasDataTipHoverChanged(DataTipHover hover)
    {
        // 既に同じ式を表示中なら何もしない（ポップアップを開いたまま保つ）。
        if (_shownDataTipExpr == hover.Expression && _dataTipPopup is { IsOpen: true }) return;

        _pendingDataTip = hover;
        _dataTipDwell ??= CreateDataTipDwellTimer();
        _dataTipDwell.Stop();
        _dataTipDwell.Start();
    }

    private void OnCanvasDataTipHoverEnded() => HideDataTip();

    private System.Windows.Threading.DispatcherTimer CreateDataTipDwellTimer()
    {
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(DataTipDwellMs),
        };
        timer.Tick += async (_, _) =>
        {
            timer.Stop();
            await EvaluateAndShowDataTipAsync(_pendingDataTip);
        };
        return timer;
    }

    private async Task EvaluateAndShowDataTipAsync(DataTipHover hover)
    {
        var evaluator = DataTipEvaluator;
        if (evaluator is null) return;

        _dataTipCts?.Cancel();
        var cts = new CancellationTokenSource();
        _dataTipCts = cts;

        string? value;
        try
        {
            value = await evaluator(new DataTipRequest(hover.Line, hover.Column, hover.Expression), cts.Token);
        }
        catch (OperationCanceledException) { return; }
        catch { return; } // ホストの評価失敗は無言で無視（DataTip は補助的表示）

        if (cts.IsCancellationRequested || cts != _dataTipCts) return;
        if (string.IsNullOrEmpty(value)) { HideDataTip(); return; }

        ShowDataTip(hover, value!);
    }

    private void ShowDataTip(DataTipHover hover, string value)
    {
        EnsureDataTipPopup();
        _dataTipText!.Text = $"{hover.Expression} = {value}";
        _shownDataTipExpr = hover.Expression;

        _dataTipPopup!.PlacementTarget = Canvas;
        _dataTipPopup.Placement = PlacementMode.RelativePoint;
        _dataTipPopup.HorizontalOffset = hover.Anchor.X;
        _dataTipPopup.VerticalOffset = hover.Anchor.Y;
        _dataTipPopup.IsOpen = true;
    }

    private void HideDataTip()
    {
        _dataTipDwell?.Stop();
        _dataTipCts?.Cancel();
        _shownDataTipExpr = null;
        if (_dataTipPopup is not null) _dataTipPopup.IsOpen = false;
    }

    private void EnsureDataTipPopup()
    {
        if (_dataTipPopup is not null) return;

        var mono = new FontFamily("Cascadia Code, Consolas");
        _dataTipText = new TextBlock
        {
            FontFamily = mono,
            FontSize = 12.5,
            Foreground = _theme.Foreground,
            TextWrapping = TextWrapping.NoWrap,
            MaxWidth = 640,
        };
        var border = new Border
        {
            Background = _theme.LineNumberBg,
            BorderBrush = _theme.IndentGuideBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 4, 8, 5),
            Child = _dataTipText,
        };
        _dataTipPopup = new Popup
        {
            Child = border,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade,
            StaysOpen = true,       // ホバー終了・キー入力・無効化で自前で閉じる
            Focusable = false,
        };
    }
}

/// <summary>DataTip 評価のリクエスト。<paramref name="Line"/>/<paramref name="Column"/> は 0 始まりのバッファ位置、
/// <paramref name="Expression"/> はホバー位置から切り出した識別子チェーン（例 <c>"obj.Items.Count"</c>）。</summary>
public readonly record struct DataTipRequest(int Line, int Column, string Expression);
