using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Editor.Controls.Rendering;
using Editor.Core.Lsp;
using Editor.Core.Syntax;
using Editor.Core.Text;

namespace Editor.Controls;

/// <summary>
/// 本文ホバーの説明ポップアップ（Rider / VS で識別子にマウスを乗せると型と要約が出るもの）。
///
/// <para>本文は既存の hover 経路と同じ <see cref="Editor.Controls.Lsp.IEditorLspView.RequestHoverAsync"/>
/// ——つまり言語サーバーの <c>textDocument/hover</c>、無ければホストの
/// <c>HostHoverProvider</c>——から取り、その位置に重なっている診断（エラー・警告）を先頭に添える。
/// これまで hover は <c>K</c> でステータスバーに<b>先頭 1 行</b>出るだけで、Markdown で返すサーバーでは
/// <c>```csharp</c> というフェンスしか読めなかった。</para>
///
/// <para>診断が乗っている位置では<b>電球</b>を出す。押すと修正（quickfix）の候補が同じポップアップの中に
/// 開き、選べばその場で直る——警告を読んだ人が Alt+Enter へ持ち替えずに済む。既定で閉じているのは、
/// 型を読みに来ただけのときに候補で視界を塞がないため。候補の集め方は Alt+Enter と同じ
/// <c>CollectQuickFixesAsync</c>、適用も同じ <c>ApplyCodeActionAsync</c> なので、経路が二重にならない。</para>
///
/// <para><b>候補は電球を押すまで問い合わせない。</b>ホバーしただけで投げると、マウスを走らせるだけで
/// 診断の数だけ Roslyn／言語サーバーの計算が積み上がる（ホストの provider には取り消しの口が無く、
/// 走り出したものは止められない）。Loomo の右クリック「Quick Fix」が<b>開いたときに</b>詰めるのと同じ作法。</para>
///
/// <para>デバッグ中の DataTip（<c>VimEditorControl.Debug.cs</c>）とは別物：あちらは停止中に式の<b>値</b>を
/// 評価して出すもので、こちらは常時の<b>型と説明</b>。同時に開くことは無い（DataTip が有効な位置では
/// どちらもマウス位置に出るため、ポップアップは相互に閉じる）。</para>
/// </summary>
public partial class VimEditorControl
{
    private SyntaxLanguageRegistry? _syntaxLanguages;
    private bool _hoverInfoEnabled = true;
    private int _hoverInfoDwellMs = 400;

    private System.Windows.Threading.DispatcherTimer? _hoverDwell;
    private System.Windows.Threading.DispatcherTimer? _hoverClose;
    private Popup? _hoverPopup;
    private Border? _hoverPopupBorder;
    private ScrollViewer? _hoverPopupScroll;
    private CancellationTokenSource? _hoverCts;
    private TextHover _pendingHover;
    private (int Line, int Start, int End)? _shownHoverSpan;
    private bool _pointerInHoverPopup;
    private Window? _hoverInfoWindow;

    // いま出ているポップアップの中身。電球の開閉で組み直すために持っておく。
    private IReadOnlyList<LspDiagnostic> _hoverDiagnostics = [];
    private IReadOnlyList<HoverBlock> _hoverBlocks = [];
    private IReadOnlyList<LspCodeAction>? _hoverFixes;
    private int _hoverHiddenFixes;
    private bool _hoverFixesExpanded;
    /// <summary>電球を出すか（＝この位置に診断がある）。候補が実際に有るかは押すまで分からない。</summary>
    private bool _hoverHasDiagnostics;
    /// <summary>候補を問い合わせ済みか。開き直すたびにサーバーへ聞かないための記憶。</summary>
    private bool _hoverFixesLoaded;
    private bool _hoverFixesLoading;

    /// <summary>マウスホバーで説明ポップアップを出すか。既定は有効。</summary>
    public bool HoverInfoEnabled
    {
        get => _hoverInfoEnabled;
        set
        {
            if (_hoverInfoEnabled == value) return;
            _hoverInfoEnabled = value;
            Canvas.SetTextHoverEnabled(value);
            if (!value) HideHoverInfo();
        }
    }

    /// <summary>マウスが止まってから問い合わせるまでの待ち（ms）。掃くように動かしている間は投げない。</summary>
    public int HoverInfoDelayMs
    {
        get => _hoverInfoDwellMs;
        set
        {
            _hoverInfoDwellMs = Math.Max(0, value);
            if (_hoverDwell is not null) _hoverDwell.Interval = TimeSpan.FromMilliseconds(_hoverInfoDwellMs);
        }
    }

    /// <summary>ウィンドウが非アクティブになったら閉じる。<see cref="Popup"/> は最前面に出るので、
    /// 別のアプリへ切り替えた人の画面に説明の板だけが残ってしまう。</summary>
    private void AttachHoverInfoWindowHook()
    {
        var window = Window.GetWindow(this);
        if (window is null || ReferenceEquals(window, _hoverInfoWindow)) return;
        DetachHoverInfoWindowHook();
        _hoverInfoWindow = window;
        window.Deactivated += OnHoverInfoWindowDeactivated;
    }

    private void DetachHoverInfoWindowHook()
    {
        if (_hoverInfoWindow is null) return;
        _hoverInfoWindow.Deactivated -= OnHoverInfoWindowDeactivated;
        _hoverInfoWindow = null;
    }

    private void OnHoverInfoWindowDeactivated(object? sender, EventArgs e) => HideHoverInfo();

    private void OnCanvasTextHoverChanged(TextHover hover)
    {
        if (!_hoverInfoEnabled) return;
        // 同じ語の上を動いているだけなら、開いているポップアップをそのまま保つ。
        if (_shownHoverSpan == (hover.Line, hover.StartColumn, hover.EndColumn) &&
            _hoverPopup is { IsOpen: true }) return;

        _pendingHover = hover;
        _hoverClose?.Stop();
        _hoverDwell ??= CreateHoverDwellTimer();
        _hoverDwell.Stop();
        _hoverDwell.Start();
    }

    /// <summary>語から外れたとき。すぐには閉じない——ポップアップ自体へマウスを移して読む（スクロールする）
    /// 途中で消えてしまうため、少し待ってからポップアップの上に居ないことを確かめて閉じる。</summary>
    private void OnCanvasTextHoverEnded()
    {
        _hoverDwell?.Stop();
        if (_hoverPopup is not { IsOpen: true }) return;

        _hoverClose ??= CreateHoverCloseTimer();
        _hoverClose.Stop();
        _hoverClose.Start();
    }

    private System.Windows.Threading.DispatcherTimer CreateHoverDwellTimer()
    {
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(_hoverInfoDwellMs),
        };
        timer.Tick += async (_, _) =>
        {
            timer.Stop();
            // async void 相当のハンドラ。ここから漏れた例外はディスパッチャ未処理例外＝アプリ停止になる。
            // ホバーは補助的表示なので、出せないときは黙って出さない（編集の邪魔をしない）。
            try { await RequestAndShowHoverInfoAsync(_pendingHover); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Hover info failed: {ex}"); }
        };
        return timer;
    }

    private System.Windows.Threading.DispatcherTimer CreateHoverCloseTimer()
    {
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(220),
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!_pointerInHoverPopup) HideHoverInfo();
        };
        return timer;
    }

    /// <param name="requireActiveWindow">背面ウィンドウでは出さないという判断。テストだけ false
    /// （テスト用ウィンドウは前面に来ないので、これを見たままだと何も確かめられない）。</param>
    private async Task RequestAndShowHoverInfoAsync(TextHover hover, bool requireActiveWindow = true)
    {
        // 背面のウィンドウでポップアップを出さない（WPF の Popup は最前面に出るので、
        // 別ウィンドウで作業中の人の前に割り込んでしまう）。
        if (requireActiveWindow && Window.GetWindow(this) is { IsActive: false }) return;
        if (_dataTipPopup is { IsOpen: true }) return;   // デバッグ中の値表示を上書きしない

        _hoverCts?.Cancel();
        var cts = new CancellationTokenSource();
        _hoverCts = cts;

        string? markdown;
        try
        {
            markdown = await _lspView.RequestHoverAsync(hover.Line, hover.StartColumn);
        }
        catch (OperationCanceledException) { return; }
        catch { return; }   // サーバーが応えないだけ。ホバーは補助的表示なので黙って諦める。

        if (cts.IsCancellationRequested || cts != _hoverCts) return;

        var diagnostics = Canvas.DiagnosticsAt(hover.Line, hover.StartColumn);
        var blocks = HoverMarkdown.Parse(markdown);
        if (diagnostics.Count == 0 && blocks.Count == 0) { HideHoverInfo(); return; }

        _shownHoverSpan = (hover.Line, hover.StartColumn, hover.EndColumn);
        ShowHoverInfo(hover.Anchor, diagnostics, blocks);
    }

    private void ShowHoverInfo(
        Point anchor, IReadOnlyList<LspDiagnostic> diagnostics, IReadOnlyList<HoverBlock> blocks)
    {
        EnsureHoverPopup();
        // 位置が変われば別の話。前の位置で開いていた候補は捨てて電球へ戻す。
        _hoverFixesExpanded = false;
        _hoverFixesLoaded = false;
        _hoverFixesLoading = false;
        _hoverFixes = null;
        _hoverHiddenFixes = 0;
        _hoverHasDiagnostics = diagnostics.Count > 0;
        SetHoverInfoContent(diagnostics, blocks);

        _hoverPopup!.PlacementTarget = Canvas;
        _hoverPopup.Placement = PlacementMode.RelativePoint;
        _hoverPopup.HorizontalOffset = anchor.X;
        _hoverPopup.VerticalOffset = anchor.Y;
        // 開いたまま位置だけ変えても追従しないので、開き直す。
        _hoverPopup.IsOpen = false;
        _hoverPopup.IsOpen = true;
    }

    private void SetHoverInfoContent(
        IReadOnlyList<LspDiagnostic> diagnostics, IReadOnlyList<HoverBlock> blocks)
    {
        _hoverDiagnostics = diagnostics;
        _hoverBlocks = blocks;
        RenderHoverInfoContent();
    }

    private void RenderHoverInfoContent()
    {
        _hoverPopupScroll!.Content = HoverContentBuilder.Build(
            _hoverDiagnostics, _hoverBlocks, _theme,
            new FontFamily($"{_editorFontFamily}, Consolas"), Math.Max(11, _editorFontSize - 1.5),
            _syntaxLanguages,
            new HoverFixSection(
                _hoverHasDiagnostics, _hoverFixesExpanded, _hoverFixesLoading, _hoverFixesLoaded,
                _hoverFixes, _hoverHiddenFixes, ToggleHoverFixes, OnHoverFixInvoked));
        _hoverPopupScroll.ScrollToTop();
    }

    /// <summary>電球が押された。開くときに<b>初めて</b>候補を問い合わせる。</summary>
    private void ToggleHoverFixes()
    {
        _hoverFixesExpanded = !_hoverFixesExpanded;
        RenderHoverInfoContent();
        if (_hoverFixesExpanded && !_hoverFixesLoaded && !_hoverFixesLoading)
            _ = LoadHoverFixesAsync(_hoverCts);
    }

    /// <summary>電球の中身（quickfix 候補）を取りに行く。押されたときだけ走る。</summary>
    private async Task LoadHoverFixesAsync(CancellationTokenSource? cts)
    {
        if (_hoverDiagnostics.Count == 0) return;

        _hoverFixesLoading = true;
        RenderHoverInfoContent();

        IReadOnlyList<LspCodeAction>? fixes = null;
        try
        {
            // 診断そのものの範囲で聞く（カーソル位置ではなく）。サーバーはこの範囲に紐づく修正を返す。
            (fixes, _) = await CollectQuickFixesAsync(_hoverDiagnostics[0].Range, announce: false);
        }
        catch (OperationCanceledException) { return; }
        catch { /* 応答が無いだけ。「候補はありません」として見せる。 */ }

        // 待っている間に別の語へ移った／閉じたなら、その表示を上書きしない。
        if (cts is not null && (cts.IsCancellationRequested || cts != _hoverCts)) return;
        if (_hoverPopup is not { IsOpen: true }) return;

        var (shown, hidden) = HoverFixSelection.Take(fixes ?? []);
        _hoverFixes = shown;
        _hoverHiddenFixes = hidden;
        _hoverFixesLoaded = true;
        _hoverFixesLoading = false;
        RenderHoverInfoContent();
    }

    /// <summary>ポップアップの修正行が押された。適用は Alt+Enter と同じ経路
    /// （解決 → コマンド実行 → workspace edit）へ渡す。</summary>
    private void OnHoverFixInvoked(LspCodeAction action)
    {
        HideHoverInfo();
        _ = ApplyCodeActionAsync(action);
    }

    /// <summary>ポップアップを閉じ、進行中の問い合わせを捨てる。</summary>
    private void HideHoverInfo()
    {
        _hoverDwell?.Stop();
        _hoverClose?.Stop();
        _hoverCts?.Cancel();
        _shownHoverSpan = null;
        _pointerInHoverPopup = false;
        _hoverFixesExpanded = false;
        _hoverFixesLoaded = false;
        _hoverFixesLoading = false;
        _hoverHasDiagnostics = false;
        _hoverFixes = null;
        _hoverHiddenFixes = 0;
        if (_hoverPopup is not null) _hoverPopup.IsOpen = false;
    }

    private void EnsureHoverPopup()
    {
        if (_hoverPopup is not null) return;

        _hoverPopupScroll = new ScrollViewer
        {
            MaxHeight = 340,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        _hoverPopupBorder = new Border
        {
            Background = _theme.Background,
            BorderBrush = _theme.IndentGuideBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 7, 10, 8),
            MaxWidth = 720,
            Child = _hoverPopupScroll,
        };
        // ポップアップの上へマウスを移した間は閉じない（長い説明を読む・スクロールするため）。
        _hoverPopupBorder.MouseEnter += (_, _) => { _pointerInHoverPopup = true; _hoverClose?.Stop(); };
        _hoverPopupBorder.MouseLeave += (_, _) => { _pointerInHoverPopup = false; HideHoverInfo(); };
        // 修正行以外を押したら閉じる（押しても何も起きない板を本文の上にかぶせたままにしない）。
        // 修正行は自分で Handled にするので、ここへは上がってこない。
        _hoverPopupBorder.MouseLeftButtonUp += (_, e) => { if (!e.Handled) HideHoverInfo(); };

        _hoverPopup = new Popup
        {
            Child = _hoverPopupBorder,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade,
            StaysOpen = true,     // キー入力・スクロール・ホバー終了で自前で閉じる
            Focusable = false,    // 入力フォーカスは本文に置いたまま（Rider と同じ挙動）
        };
    }

    /// <summary>テーマ変更をポップアップにも反映する（開いていなくても次回表示に効く）。</summary>
    private void ApplyThemeToHoverPopup()
    {
        if (_hoverPopupBorder is null) return;
        _hoverPopupBorder.Background = _theme.Background;
        _hoverPopupBorder.BorderBrush = _theme.IndentGuideBrush;
        HideHoverInfo();   // 中身は表示時に組み直すので、古い配色のまま残さない
    }

    // ───────────────────────── テスト用の窓口 ─────────────────────────
    // 実マウスの移動を合成せずに、同じ経路（問い合わせ → ポップアップ → 修正行）を走らせるための seam。

    /// <summary>テスト用：その位置にマウスを止めたのと同じ処理を走らせる。</summary>
    internal Task ShowHoverInfoForTestAsync(int line, int column) =>
        RequestAndShowHoverInfoAsync(
            new TextHover(line, column, column, new Point(0, 0)), requireActiveWindow: false);

    /// <summary>テスト用：いま出ているポップアップの中身（出ていなければ null）。</summary>
    internal FrameworkElement? HoverPopupContentForTest =>
        _hoverPopup is { IsOpen: true } ? _hoverPopupScroll?.Content as FrameworkElement : null;

    /// <summary>キャレット位置の説明を同じポップアップで出す（<c>K</c> / メニューの「Hover Info」）。</summary>
    private async Task ShowHoverInfoAtCaretAsync()
    {
        var cursor = _engine.Cursor;

        _hoverCts?.Cancel();
        var cts = new CancellationTokenSource();
        _hoverCts = cts;

        string? markdown;
        try
        {
            markdown = await _lspView.RequestHoverAsync(cursor.Line, cursor.Column);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            ActiveStatusBar.UpdateStatus($"Hover: {ex.Message}");
            return;
        }
        if (cts.IsCancellationRequested || cts != _hoverCts) return;

        var diagnostics = Canvas.DiagnosticsAt(cursor.Line, cursor.Column);
        var blocks = HoverMarkdown.Parse(markdown);
        if (diagnostics.Count == 0 && blocks.Count == 0)
        {
            ActiveStatusBar.UpdateStatus("Hover: no information at this position");
            return;
        }

        var caret = Canvas.GetCursorPixelPosition();
        _shownHoverSpan = null;   // マウスの語とは無関係なので、次のホバーで開き直させる
        ShowHoverInfo(new Point(caret.X, caret.Y + Canvas.LineHeight), diagnostics, blocks);
    }
}
