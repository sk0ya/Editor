using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
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
/// <para><b>ピン留め（📌 / Ctrl+Shift+K）と写し（📋 / Ctrl+Shift+C）。</b>この表示はもともと
/// <b>打鍵・ウィンドウの非アクティブ化・マウスが語から離れる・スクロール・クリック</b>のどれでも消えた。
/// どれも「たぶんもう用は無い」という<b>推測</b>で、読んでいる最中に消えないためだけの作りだったが、
/// その推測は<b>説明を持ち出したい人</b>を締め出していた——スクリーンショットを撮る手（Win+Shift+S も
/// PrintScreen も、打鍵かつ非アクティブ化）が全部ふさがっていて、画面に出ている説明を画像にする方法が
/// 無かった。ピンはその推測を止める札で、留めている間は Escape（と 📌 の押し直し）だけが閉じる合図になる。
/// 中身も <c>TextBlock</c> のままでは選択できず読めても取り出せないので、📋 で
/// <see cref="Editor.Core.Text.HoverCopyText"/> を通した素のテキストをクリップボードへ渡す。</para>
///
/// <para>両方とも<b>幅を予約しない</b>：印はポップアップへマウスを入れたときだけ右上に現れ、離れれば消える。
/// 撮るときはマウスがキャプチャツールへ抜けているので、<b>画像には印が写らない</b>。</para>
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
    private FlowDocumentScrollViewer? _hoverPopupViewer;
    private StackPanel? _hoverChrome;
    private Border? _hoverPinChip;
    private TextBlock? _hoverPinGlyph;
    private Border? _hoverCopyChip;
    private TextBlock? _hoverCopyGlyph;
    /// <summary>ピン留め中か。閉じる合図を Escape だけに絞り、「たぶんもう用は無い」で閉じる経路を止める。</summary>
    private bool _hoverPinned;
    /// <summary>ポップアップを押した位置。離した位置と比べて「クリック」か「選択のドラッグ」かを見分ける。</summary>
    private Point _hoverPressPoint;
    /// <summary>ポップアップの右クリックメニューを開いている間。</summary>
    private bool _hoverContextMenuOpen;
    /// <summary>メニューが結局開かなかったときに、引き止めを解くための見張り。</summary>
    private System.Windows.Threading.DispatcherTimer? _hoverMenuGuard;
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
    /// 別のアプリへ切り替えた人の画面に説明の板だけが残ってしまう。
    /// ——ただしピン留めされていれば残す。スクリーンショットを撮る操作（Win+Shift+S など）は
    /// <b>まさにこの非アクティブ化</b>なので、ここで閉じる限り画像には写らない。</summary>
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

    private void OnHoverInfoWindowDeactivated(object? sender, EventArgs e) => HideHoverInfoUnlessHeld();

    private void OnCanvasTextHoverChanged(TextHover hover)
    {
        if (!_hoverInfoEnabled) return;
        // ピン留め中は別の語へ移っても差し替えない（固定したのは<b>その</b>説明であって、
        // マウスの行き先ではない）。
        if (_hoverPinned) return;
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
            if (!_pointerInHoverPopup) HideHoverInfoUnlessHeld();
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
        if (diagnostics.Count == 0 && blocks.Count == 0) { HideHoverInfoUnlessHeld(); return; }

        _shownHoverSpan = (hover.Line, hover.StartColumn, hover.EndColumn);
        ShowHoverInfo(hover.Anchor, diagnostics, blocks);
    }

    private void ShowHoverInfo(
        Point anchor, IReadOnlyList<LspDiagnostic> diagnostics, IReadOnlyList<HoverBlock> blocks)
    {
        EnsureHoverPopup();
        // 位置が変われば別の話。前の位置で開いていた候補は捨てて電球へ戻す。
        // ピンも同じ——留めたのは前の説明なので、新しい説明には引き継がない。
        _hoverPinned = false;
        ShowHoverChrome(false);
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
        _hoverPopupViewer!.Document = HoverContentBuilder.Build(
            _hoverDiagnostics, _hoverBlocks, _theme,
            new FontFamily($"{_editorFontFamily}, Consolas"), Math.Max(11, _editorFontSize - 1.5),
            _syntaxLanguages,
            new HoverFixSection(
                _hoverHasDiagnostics, _hoverFixesExpanded, _hoverFixesLoading, _hoverFixesLoaded,
                _hoverFixes, _hoverHiddenFixes, ToggleHoverFixes, OnHoverFixInvoked));
        // 先頭へ戻す明示の呼び出しは要らない：組み直すたびに<b>別の</b> FlowDocument を渡しているので、
        // 表示器は新しい文書の先頭から測り直す（FlowDocumentScrollViewer に ScrollToTop は無い）。
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

    /// <summary>ポップアップを閉じ、進行中の問い合わせを捨てる。ピン留めも解く——
    /// <b>本当に閉じる</b>経路（Escape・ファイルを開き直す・テーマ変更・アンロード・修正の適用）はこちら。</summary>
    private void HideHoverInfo()
    {
        _hoverDwell?.Stop();
        _hoverClose?.Stop();
        _hoverMenuGuard?.Stop();
        _hoverCts?.Cancel();
        _shownHoverSpan = null;
        _pointerInHoverPopup = false;
        _hoverPinned = false;
        _hoverContextMenuOpen = false;
        _hoverFixesExpanded = false;
        _hoverFixesLoaded = false;
        _hoverFixesLoading = false;
        _hoverHasDiagnostics = false;
        _hoverFixes = null;
        _hoverHiddenFixes = 0;
        ShowHoverChrome(false);
        // 焦点を貸したまま閉じると、キーの行き先が消えたポップアップになる。
        ReturnFocusToBuffer();
        if (_hoverPopup is not null) _hoverPopup.IsOpen = false;
    }

    /// <summary>引き止めている理由が無ければ閉じる。「マウスが離れた」「別のアプリへ切り替えた」
    /// 「打鍵した」——<b>たぶんもう用は無い</b>という推測で閉じる経路はすべてこちらを通す。</summary>
    private void HideHoverInfoUnlessHeld()
    {
        if (IsHoverHeld) return;
        HideHoverInfo();
    }

    /// <summary>閉じる推測を止めている理由があるか。ピン留めのほか、<b>右クリックメニューを開いている間</b>も
    /// 止める——メニューは別ウィンドウなので、そちらへマウスを移した瞬間にポップアップの
    /// <c>MouseLeave</c> が起き、メニューごと消えていた（「右クリックしても何も出ない」の正体）。</summary>
    private bool IsHoverHeld => _hoverPinned || _hoverContextMenuOpen;

    /// <summary>ピン留めの入切。留めている間は Escape（と押し直し）だけが閉じる合図になる。</summary>
    private void ToggleHoverPin()
    {
        if (_hoverPopup is not { IsOpen: true }) return;

        _hoverPinned = !_hoverPinned;
        UpdateHoverChrome();
        ActiveStatusBar.UpdateStatus(_hoverPinned
            ? "Hover: ピン留めしました（Esc で閉じる）"
            : "Hover: ピン留めを外しました");

        // 外した＝もう用が無い。マウスがポップアップの上に残っているなら、
        // 「離れたら閉じる」という普段の作法へ戻すだけにする。
        if (!_hoverPinned && !_pointerInHoverPopup) HideHoverInfo();
    }

    /// <summary>ポップアップの右クリックメニュー。本文の右クリックと同じ外装
    /// （<c>CreateThemedMenu</c>）を使うので、テーマも見た目も揃う。</summary>
    private ContextMenu BuildHoverContextMenu()
    {
        var itemStyle = (Style)FindResource("EditorMenuItem");
        var menu = CreateThemedMenu();
        var selected = HoverSelectionText();

        MenuItem Item(string header, string gesture, Action onClick, bool enabled = true)
        {
            var item = new MenuItem
            {
                Header = header,
                InputGestureText = gesture,
                Style = itemStyle,
                IsEnabled = enabled,
            };
            item.Click += (_, _) => onClick();
            return item;
        }

        // 何がコピーされるのかを名前で言い切る（「コピー」だけだと、選んだ分なのか全体なのか分からない）。
        menu.Items.Add(selected.Length > 0
            ? Item("選択範囲をコピー", "Ctrl+C", CopyHoverInfo)
            : Item("すべてコピー", "Ctrl+Shift+C", CopyHoverInfo,
                enabled: _hoverDiagnostics.Count > 0 || _hoverBlocks.Count > 0));
        // 「すべて選択」にキーは割り当てない。Ctrl+A は本文の編集でよく使う和音で、ホバーが
        // 出ているだけで奪うには重すぎる（Ctrl+Shift+K / Ctrl+Shift+C は本文が使わない和音）。
        menu.Items.Add(Item("すべて選択", "", SelectAllHoverText));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(
            _hoverPinned ? "ピン留めを解除" : "ピン留め", "Ctrl+Shift+K", ToggleHoverPin));
        menu.Items.Add(Item("閉じる", "Esc", HideHoverInfo));

        // 閉じたら引き止める理由が消えるので、普段の作法（マウスが乗っていなければ閉じる）へ戻す。
        menu.Closed += (_, _) => ReleaseHoverContextMenu();
        return menu;
    }

    /// <summary>右クリックメニューが出ている間としてポップアップを引き止める。</summary>
    private void HoldHoverForContextMenu()
    {
        _hoverContextMenuOpen = true;
        // 結局どちらのメニューも開かなかったときに、引き止めたまま残さないための保険。
        _hoverMenuGuard ??= CreateHoverMenuGuardTimer();
        _hoverMenuGuard.Stop();
        _hoverMenuGuard.Start();
    }

    private void ReleaseHoverContextMenu()
    {
        _hoverMenuGuard?.Stop();
        _hoverContextMenuOpen = false;
        if (!_pointerInHoverPopup) HideHoverInfoUnlessHeld();
    }

    private System.Windows.Threading.DispatcherTimer CreateHoverMenuGuardTimer()
    {
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(400),
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (IsHoverContextMenuOpen()) return;
            ReleaseHoverContextMenu();
        };
        return timer;
    }

    private bool IsHoverContextMenuOpen() =>
        _hoverPopupBorder?.ContextMenu is { IsOpen: true } ||
        _hoverPopupViewer?.ContextMenu is { IsOpen: true };

    /// <summary>ポップアップの説明をすべて選ぶ。</summary>
    private void SelectAllHoverText()
    {
        if (_hoverPopupViewer?.Document is not { } document) return;
        _hoverPopupViewer.Selection?.Select(document.ContentStart, document.ContentEnd);
    }

    /// <summary>ポップアップへ貸していた入力フォーカスを本文へ返す。選択はそのまま残る。</summary>
    private void ReturnFocusToBuffer()
    {
        if (_hoverPopupViewer is not { IsKeyboardFocusWithin: true }) return;
        Canvas.Focus();
        // クリックで本文へ戻るときと同じ作法（共有 HWND なので TSF の焦点も指し直す）。
        AssertImeStoreFocus();
    }

    /// <summary>ポップアップの中で文字が選ばれているか。</summary>
    private bool HasHoverSelection() => HoverSelectionText().Length > 0;

    /// <summary>選ばれている文字（無ければ空）。</summary>
    private string HoverSelectionText() => _hoverPopupViewer?.Selection?.Text ?? "";

    /// <summary>押した位置から離れて離されたか（＝クリックではなく選択のドラッグ）。</summary>
    private bool IsHoverDrag(Point released) =>
        Math.Abs(released.X - _hoverPressPoint.X) > HoverDragThreshold ||
        Math.Abs(released.Y - _hoverPressPoint.Y) > HoverDragThreshold;

    /// <summary>この距離を超えて動いていたら、クリックではなく選択のドラッグとみなす。</summary>
    private const double HoverDragThreshold = 3;

    /// <summary>選んだ文字を——選んでいなければ説明まるごとを——クリップボードへ。</summary>
    private void CopyHoverInfo()
    {
        // 部分を選んでいるなら、その人が欲しいのは選んだところ。丸ごと渡すと選んだ意味が消える。
        var selected = HoverSelectionText();
        var text = selected.Length > 0
            ? selected
            : HoverCopyText.Build(_hoverDiagnostics, _hoverBlocks);
        if (text.Length == 0)
        {
            ActiveStatusBar.UpdateStatus("Hover: コピーできる文字がありません");
            return;
        }

        try { Clipboard.SetText(text); }
        catch
        {
            // クリップボードは他プロセスに握られていることがある。落とさず、黙って失敗しない。
            ActiveStatusBar.UpdateStatus("Hover: クリップボードを開けませんでした");
            return;
        }

        // マウスで押したときは印そのものが応える。キーで撮ったときはステータスバーが唯一の合図。
        if (_hoverCopyGlyph is not null) _hoverCopyGlyph.Text = "✓";
        ActiveStatusBar.UpdateStatus($"Hover: コピーしました（{text.Length} 文字）");
    }

    /// <summary>説明ポップアップが出ている間のキー。ピン留めと写しの合図をここで受け取り、
    /// ピン中の Escape だけを「閉じる」に使う。それ以外のキーは、ピンが無ければポップアップを
    /// 引っ込めてから、あれば<b>出したまま</b>、本来の処理へ通す。</summary>
    /// <returns>このキーをここで使い切ったか（true なら本文へは渡さない）。</returns>
    private bool HandleHoverPopupKey(Key key, ModifierKeys modifiers)
    {
        var chord = modifiers & (ModifierKeys.Control | ModifierKeys.Shift | ModifierKeys.Alt);
        if (chord == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            // K はホバーそのもののキー、C は写し。どちらもポップアップが出ている間だけ効くので、
            // 本文の編集から奪うキーにはならない。
            if (key == Key.K) { ToggleHoverPin(); return true; }
            if (key == Key.C) { CopyHoverInfo(); return true; }
        }

        // 素の Ctrl+C は<b>ポップアップの中で文字を選んでいるときだけ</b>もらう。フォーカスは本文に
        // 置いたままなので、選んだ人が普通に押すのはこれ——けれど選んでいなければヤンク（本文のコピー）
        // のままでなければ困るので、条件は「選択がある」に絞る。
        if (chord == ModifierKeys.Control && key == Key.C && HasHoverSelection())
        {
            CopyHoverInfo();
            return true;
        }

        if (!_hoverPinned) { HideHoverInfo(); return false; }
        if (key != Key.Escape) return false;
        HideHoverInfo();
        return true;
    }

    private void EnsureHoverPopup()
    {
        if (_hoverPopup is not null) return;

        // 中身は FlowDocument（<see cref="HoverContentBuilder"/>）。TextBlock を積んでいた頃は
        // 読めても一文字も選べず、シグネチャは手で打ち直すしかなかった。
        //
        // Focusable は true でなければならない。WPF のテキスト選択はフォーカスを取れたときだけ
        // 始まる——false のままだとドラッグしても<b>一文字も選べない</b>（実測：Focusable=false は
        // 空、true は選択が返る。ColumnWidth は無関係だった）。とはいえ入力フォーカスの置き場は
        // 本文であり続けるので、<see cref="ReturnFocusToBuffer"/> でマウスを離した直後に返す。
        // 選択はフォーカスを返しても残るので（これも実測）、「選ぶ間だけ借りる」で両立する。
        // 開いただけでは奪わない（Popup 自体が Focusable=false なので、焦点は押されて初めて動く）。
        _hoverPopupViewer = new FlowDocumentScrollViewer
        {
            MaxHeight = 340,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            IsSelectionEnabled = true,
            Focusable = true,
            IsTabStop = false,     // Tab の巡回先にはしない（借りるのはマウスのときだけ）
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            SelectionBrush = _theme.SelectionBg,
        };
        // 印（📌 / 📋）は本文の<b>上へ重ねる</b>——列を分けて幅を予約すると、印を使わない大多数の
        // ホバーまで細くなる。重ねてよいのは、出ているのがマウスを乗せている間だけだから。
        var layout = new Grid();
        layout.Children.Add(_hoverPopupViewer);
        layout.Children.Add(BuildHoverChrome());

        _hoverPopupBorder = new Border
        {
            Background = _theme.Background,
            BorderBrush = _theme.IndentGuideBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 7, 10, 8),
            MaxWidth = 720,
            Child = layout,
        };
        // ポップアップの上へマウスを移した間は閉じない（長い説明を読む・スクロールするため）。
        // 印もこのときだけ出す：撮るころにはマウスは抜けているので、画像に印は写らない。
        _hoverPopupBorder.MouseEnter += (_, _) =>
        {
            _pointerInHoverPopup = true;
            _hoverClose?.Stop();
            ShowHoverChrome(true);
        };
        _hoverPopupBorder.MouseLeave += (_, _) =>
        {
            // ドラッグで文字を選んでいる最中は、枠からはみ出しても閉じない——選択は端まで引いて
            // 伸ばすものなので、ここで閉じると「選べるが選び切れない」になる。
            if (Mouse.LeftButton == MouseButtonState.Pressed) return;
            _pointerInHoverPopup = false;
            ShowHoverChrome(false);
            HideHoverInfoUnlessHeld();
        };
        // 修正行以外を押したら閉じる（押しても何も起きない板を本文の上にかぶせたままにしない）。
        // 修正行と印は自分で Handled にするので、ここへは上がってこない。
        _hoverPopupBorder.PreviewMouseLeftButtonDown += (_, e) =>
            _hoverPressPoint = e.GetPosition(_hoverPopupBorder);
        // 押して離したら、借りていた入力フォーカスを本文へ返す。Preview 段で拾うのは、
        // 修正行や印が Handled にしたクリックでも必ず通るため。実際に返すのは<b>後回し</b>で、
        // その場で返すと選択を締めくくる処理より先に割り込んでしまう。
        _hoverPopupBorder.PreviewMouseLeftButtonUp += (_, _) =>
            Dispatcher.BeginInvoke(
                ReturnFocusToBuffer, System.Windows.Threading.DispatcherPriority.Background);
        // 右クリックメニューは押されたときに組む（本文の右クリックと同じ作法——そのときの
        // 選択やピンの状態で項目名が変わるので、使い回さず作り直す）。
        // 表示器と枠の<b>両方</b>に載せる：文字の上で押したときは表示器が、印や余白で押したときは
        // 枠が最も内側の持ち主になる。同じ実体を二つの要素に持たせると置き場所の解決が濁るので、
        // それぞれに作る（開くのは片方だけ）。
        _hoverPopupBorder.PreviewMouseRightButtonDown += (_, _) =>
        {
            // 引き止めるのは<b>メニューが開く前</b>。メニューが出た拍子にポップアップの
            // MouseLeave が走るので、menu.Opened を待ってからでは間に合わず、ポップアップが
            // 閉じてメニューも道連れになる——「右クリックしても何も出ない」の正体（実測）。
            HoldHoverForContextMenu();
            _hoverPopupBorder.ContextMenu = BuildHoverContextMenu();
            if (_hoverPopupViewer is not null)
                _hoverPopupViewer.ContextMenu = BuildHoverContextMenu();
        };
        _hoverPopupBorder.MouseLeftButtonUp += (_, e) =>
        {
            if (e.Handled) return;
            // 「押して離した」だけが閉じる合図。文字を選ぶドラッグの終わりで閉じては、
            // 選んだそばから消えてコピーできない。
            if (IsHoverDrag(e.GetPosition(_hoverPopupBorder)) || HasHoverSelection()) return;
            HideHoverInfoUnlessHeld();
        };

        _hoverPopup = new Popup
        {
            Child = _hoverPopupBorder,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade,
            StaysOpen = true,     // キー入力・スクロール・ホバー終了で自前で閉じる
            Focusable = false,    // 入力フォーカスは本文に置いたまま（Rider と同じ挙動）
        };
    }

    /// <summary>ポップアップ右上の印（ピン留めと写し）。既定は畳んだまま＝<b>幅も高さも取らない</b>ので、
    /// 印を使わないホバーの見た目は前のまま。</summary>
    private StackPanel BuildHoverChrome()
    {
        _hoverPinGlyph = new TextBlock { Text = "📌", FontSize = 11 };
        _hoverPinChip = BuildHoverChip(_hoverPinGlyph, ToggleHoverPin);

        _hoverCopyGlyph = new TextBlock { Text = "📋", FontSize = 11 };
        _hoverCopyChip = BuildHoverChip(_hoverCopyGlyph, CopyHoverInfo);
        _hoverCopyChip.ToolTip = "この説明をコピー（Ctrl+Shift+C）";

        _hoverChrome = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            // 枠の内側の余白へ食い込ませて、本文の読める幅を削らない。
            Margin = new Thickness(0, -4, -6, 0),
            Visibility = Visibility.Collapsed,
        };
        _hoverChrome.Children.Add(_hoverCopyChip);
        _hoverChrome.Children.Add(_hoverPinChip);
        UpdateHoverChrome();
        return _hoverChrome;
    }

    private Border BuildHoverChip(TextBlock glyph, Action onClick)
    {
        var chip = new Border
        {
            Child = glyph,
            Padding = new Thickness(4, 1, 4, 2),
            Margin = new Thickness(3, 0, 0, 0),
            CornerRadius = new CornerRadius(3),
            BorderThickness = new Thickness(1),
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        // Handled にして、ポップアップ全体の「クリックで閉じる」より先にここで受け取る。
        chip.MouseLeftButtonUp += (_, e) => { e.Handled = true; onClick(); };
        return chip;
    }

    /// <summary>印を出す／畳む。畳んである間はレイアウトに現れないので、幅の予約にならない。</summary>
    private void ShowHoverChrome(bool visible)
    {
        if (_hoverChrome is null) return;
        _hoverChrome.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (visible) UpdateHoverChrome();
    }

    /// <summary>印の見た目（ピンの入切・写しの合図・テーマ）を今の状態に合わせる。</summary>
    private void UpdateHoverChrome()
    {
        if (_hoverPinChip is null || _hoverPinGlyph is null) return;

        _hoverPinChip.Background = _hoverPinned ? _theme.CurrentLineBg : _theme.LineNumberBg;
        _hoverPinChip.BorderBrush = _theme.IndentGuideBrush;
        _hoverPinGlyph.Foreground = _hoverPinned ? _theme.Foreground : _theme.LineNumberFg;
        _hoverPinChip.ToolTip = _hoverPinned
            ? "ピン留め中（Esc で閉じる）"
            : "ピン留め（Ctrl+Shift+K）— マウスを離しても、他のアプリへ切り替えても消えません";

        if (_hoverCopyChip is null || _hoverCopyGlyph is null) return;
        _hoverCopyChip.Background = _theme.LineNumberBg;
        _hoverCopyChip.BorderBrush = _theme.IndentGuideBrush;
        _hoverCopyGlyph.Text = "📋";
        _hoverCopyGlyph.Foreground = _theme.LineNumberFg;
    }

    /// <summary>テーマ変更をポップアップにも反映する（開いていなくても次回表示に効く）。</summary>
    private void ApplyThemeToHoverPopup()
    {
        if (_hoverPopupBorder is null) return;
        _hoverPopupBorder.Background = _theme.Background;
        _hoverPopupBorder.BorderBrush = _theme.IndentGuideBrush;
        HideHoverInfo();   // 中身は表示時に組み直すので、古い配色のまま残さない
        UpdateHoverChrome();
    }

    // ───────────────────────── テスト用の窓口 ─────────────────────────
    // 実マウスの移動を合成せずに、同じ経路（問い合わせ → ポップアップ → 修正行）を走らせるための seam。

    /// <summary>テスト用：その位置にマウスを止めたのと同じ処理を走らせる。</summary>
    internal Task ShowHoverInfoForTestAsync(int line, int column) =>
        RequestAndShowHoverInfoAsync(
            new TextHover(line, column, column, new Point(0, 0)), requireActiveWindow: false);

    /// <summary>テスト用：いま出ているポップアップの中身（出ていなければ null）。</summary>
    internal FlowDocument? HoverPopupContentForTest =>
        _hoverPopup is { IsOpen: true } ? _hoverPopupViewer?.Document : null;

    /// <summary>テスト用：ポップアップを載せている表示器（選択の確認用）。</summary>
    internal FlowDocumentScrollViewer? HoverPopupViewerForTest => _hoverPopupViewer;

    /// <summary>テスト用：ポップアップの枠（クリックの届き先）。</summary>
    internal FrameworkElement? HoverPopupBorderForTest => _hoverPopupBorder;

    /// <summary>テスト用：説明の全体を選んだことにする（実マウスのドラッグの代わり）。</summary>
    internal void SelectAllHoverTextForTest() => SelectAllHoverText();

    /// <summary>テスト用：右クリックで組まれるメニュー。</summary>
    internal ContextMenu BuildHoverContextMenuForTest() => BuildHoverContextMenu();

    /// <summary>テスト用：右クリックメニューが開いている間として扱わせる。</summary>
    internal void SetHoverContextMenuOpenForTest(bool open) => _hoverContextMenuOpen = open;

    /// <summary>テスト用：いま選ばれている文字。</summary>
    internal string HoverSelectionTextForTest => HoverSelectionText();

    /// <summary>テスト用：ピン留め中か。</summary>
    internal bool HoverPinnedForTest => _hoverPinned;

    /// <summary>テスト用：右上の印（出ていなければ null）。押す＝ピン留めの入切。</summary>
    internal FrameworkElement? HoverPinChipForTest => _hoverPopup is { IsOpen: true } ? _hoverPinChip : null;

    /// <summary>テスト用：右上の写しの印（出ていなければ null）。</summary>
    internal FrameworkElement? HoverCopyChipForTest => _hoverPopup is { IsOpen: true } ? _hoverCopyChip : null;

    /// <summary>テスト用：印を出しているか（＝マウスがポップアップの上に居るときだけ true）。</summary>
    internal bool HoverChromeVisibleForTest => _hoverChrome is { Visibility: Visibility.Visible };

    /// <summary>テスト用：ウィンドウが非アクティブになったのと同じ処理を走らせる
    /// （テスト用ウィンドウは前面に来ないので、実際の切り替えでは確かめられない）。</summary>
    internal void RaiseWindowDeactivatedForTest() => OnHoverInfoWindowDeactivated(null, EventArgs.Empty);

    /// <summary>テスト用：ポップアップが出ている間のキーを 1 つ流す。</summary>
    internal bool SendHoverPopupKeyForTest(Key key, ModifierKeys modifiers) =>
        _hoverPopup is { IsOpen: true } && HandleHoverPopupKey(key, modifiers);

    /// <summary>テスト用：マウスがポップアップへ出入りしたのと同じ処理を走らせる。</summary>
    internal void SetPointerInHoverPopupForTest(bool inside)
    {
        _pointerInHoverPopup = inside;
        ShowHoverChrome(inside);
        if (!inside) HideHoverInfoUnlessHeld();
    }

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
