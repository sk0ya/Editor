using System;
using System.Windows;
using System.Windows.Media;

namespace Editor.Controls.Rendering;

/// <summary>
/// EditorCanvas のクイックフィックス列（電球）。<b>その行に直せる手があること</b>を、
/// キーを押す前に見せるための列。
///
/// <para>これまで修正候補への入口は Alt+Enter とホバーの電球しかなく、どちらも
/// 「そこに何かある」と<b>知っている人にしか</b>押せなかった（設計書 §30.19.1 で
/// 「押せていなかった」と記した側の、もう一段手前の問題）。</para>
///
/// <para>ブレークポイント列・テスト列と同じ流儀で、<see cref="_codeActionBulbEnabled"/> が
/// false の間は <c>GetGutterMetrics</c> が幅を 0 にし、描画もヒットテストも作動しない。
/// 有効な間は<b>電球が無い行でも幅を保つ</b>——電球はキャレット行にだけ出て消えるので、
/// 幅を出し入れすると本文が左右に踊る（<c>EditorTestGlyphColumns</c> がテスト列で学んだのと同じ話）。</para>
///
/// <para>「どの行に電球を出すか」はエディタでは決められない——候補があるかは LSP／ホストに
/// 訊かないと判らないので、判定は <c>VimEditorControl.CodeActionBulb.cs</c> が持ち、
/// ここは <see cref="SetCodeActionBulbLine"/> で受け取った 1 行を描いてクリックを通知するだけ。</para>
/// </summary>
public partial class EditorCanvas
{
    private bool _codeActionBulbEnabled;
    private int _codeActionBulbLine = -1;
    private bool _codeActionBulbHovered;

    // 電球は琥珀。テーマの DiagnosticWarning を借りると、テーマによっては警告の波線と
    // 同じ色になって「これも問題です」に見えるので、この用途専用の固定色で持つ
    // （ブレークポイント列の赤と同じ考え方）。
    private static readonly Brush CodeActionBulbBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xC8, 0x3D)));
    private static readonly Brush CodeActionBulbBaseBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xA8, 0x7A, 0x1B)));
    private static readonly Brush CodeActionBulbHoverBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)));

    /// <summary>電球が押されたとき。引数はバッファ行（0始まり）。ホストはクイックフィックスを出す。</summary>
    public event Action<int>? CodeActionBulbClicked;

    /// <summary>電球列の有効/無効を切り替える。既定は無効（幅 0 ＝従来のレイアウトと 1px も変わらない）。</summary>
    public void SetCodeActionBulbEnabled(bool enabled)
    {
        if (_codeActionBulbEnabled == enabled) return;
        _codeActionBulbEnabled = enabled;
        if (!enabled) { _codeActionBulbLine = -1; _codeActionBulbHovered = false; }
        // 列の分だけ本文の左端が動く（折り返し幅・可視桁数に影響）ので、再レイアウトも要求する。
        InvalidateArrange();
        InvalidateVisual();
    }

    /// <summary>電球を出す行（0始まり、無ければ -1）。列が無効なら何もしない。</summary>
    public void SetCodeActionBulbLine(int bufferLine)
    {
        int normalized = _codeActionBulbEnabled && bufferLine >= 0 ? bufferLine : -1;
        if (_codeActionBulbLine == normalized) return;
        _codeActionBulbLine = normalized;
        if (_codeActionBulbEnabled) InvalidateVisual();
    }

    /// <summary>電球列が有効か（ホストが問い合わせを行うかの判断に使う）。</summary>
    internal bool IsCodeActionBulbEnabled => _codeActionBulbEnabled;

    /// <summary>いま電球が出ている行（無ければ -1）。</summary>
    internal int CodeActionBulbLine => _codeActionBulbLine;

    /// <summary>電球列のクリック処理本体。テスト列・ブレークポイント列と同じく、マウスイベントから
    /// 切り離して座標だけで叩ける。列の外なら false（呼び出し側は後続の処理へ進む）。</summary>
    internal bool TryClickCodeActionBulbColumn(Point point)
    {
        if (!_codeActionBulbEnabled) return false;
        if (!_gutterHitTester.TryHitCodeActionBulbGutter(point, CurrentGutterBoundaries(), out int line)) return false;
        if (line >= 0 && line == _codeActionBulbLine) CodeActionBulbClicked?.Invoke(line);
        return true;
    }

    // 電球の上に居るか（ホバーの陰影と手カーソル用）。列の中でも電球の無い行は false。
    private void SetCodeActionBulbHovered(bool hovered)
    {
        if (_codeActionBulbHovered == hovered) return;
        _codeActionBulbHovered = hovered;
        if (_codeActionBulbEnabled) InvalidateVisual();
    }

    /// <summary>電球列（blame の右＝ガター最左）にその行の電球を描く。<paramref name="x"/> は列の左端。</summary>
    private void DrawCodeActionBulb(DrawingContext dc, int line, double y, double x, int colWidth)
    {
        if (line != _codeActionBulbLine) return;

        double cx = x + colWidth / 2.0;
        double cy = y + _lineHeight / 2.0;
        // ガラス球の半径。列の幅と行の高さの小さい方に収め、口金のぶんの余白を残す。
        // 小さすぎると「琥珀色の点」にしか見えない（電球だと判る大きさが要る）。
        double r = Math.Max(3.5, Math.Min(colWidth, _lineHeight) / 2.0 - 2.5);

        if (_codeActionBulbHovered)
            dc.DrawRectangle(CodeActionBulbHoverBrush, null, new Rect(x, y, colWidth, _lineHeight));

        // フォント依存の絵文字（💡）ではなくベクターで描く——フォールバックのフォント次第で
        // 大きさも色も変わるうえ、小さいセルでは潰れる（フォールドのシェブロンと同じ理由）。
        double bulbCy = cy - r * 0.35;
        dc.DrawEllipse(CodeActionBulbBrush, null, new Point(cx, bulbCy), r, r);

        // 口金（ねじ込み部）— 球の下に細い帯を 2 本。下の帯を狭めて先細りに見せる。
        double baseW = r * 1.0;
        double baseH = Math.Max(1.0, r * 0.3);
        double baseTop = bulbCy + r * 0.8;
        dc.DrawRectangle(CodeActionBulbBaseBrush, null, new Rect(cx - baseW / 2, baseTop, baseW, baseH));
        double narrowW = baseW * 0.6;
        dc.DrawRectangle(CodeActionBulbBaseBrush, null,
            new Rect(cx - narrowW / 2, baseTop + baseH * 1.6, narrowW, baseH));
    }
}
