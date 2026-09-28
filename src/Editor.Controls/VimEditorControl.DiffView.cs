using System.Windows;
using Editor.Core.Editing;

namespace Editor.Controls;

/// <summary>
/// 2つのエディタを左右に並べて差分を見せるホスト向けの窓口。差分の計算・左右の対応付け・
/// スクロールの連動はホストの責務で、エディタは「行の背景」「空き行」「読み取り専用」と、
/// 連動に要る画素単位のスクロール位置を出すだけ。
/// </summary>
public partial class VimEditorControl
{
    /// <summary>
    /// 本文を書き換えさせない（移動・選択・ヤンク・検索はできる）。差分の旧側のように、
    /// 読んでコピーはするが編集はしない面に使う。<see cref="SetText"/> などホストからの差し替えは通る。
    /// </summary>
    public bool IsReadOnly
    {
        get => _engine.IsReadOnly;
        set => _engine.IsReadOnly = value;
    }

    /// <summary>差分の装飾（追加／削除行の背景と、反対側の行ぶんの空き行）を差し替える。null で消す。</summary>
    public void SetDiffDecorations(DiffDecorations? decorations) => Canvas.SetDiffDecorations(decorations);

    /// <summary>本文の縦スクロール位置（画素）。空き行・注釈行も1行ぶんの高さを持つ。</summary>
    public double VerticalOffset => Canvas.VerticalOffset;

    /// <summary>本文の横スクロール位置（画素）。</summary>
    public double HorizontalOffset => Canvas.HorizontalOffset;

    /// <summary>1行の高さ（画素）。行は一様な高さなので、表示行 i の上端は <c>i * LineHeight - VerticalOffset</c>。</summary>
    public double LineHeight => Canvas.LineHeight;

    /// <summary>本文領域の高さ（画素）。</summary>
    public double TextViewportHeight => Canvas.ViewportHeight;

    /// <summary>表示行の数（本文の折り返し・注釈行・空き行をすべて含む）。</summary>
    public int VisualLineCount => Canvas.VisualLineCount;

    /// <summary>バッファ行（0始まり）が載っている表示行。畳まれていれば -1。</summary>
    public int GetTextVisualLine(int bufferLine) => Canvas.GetTextVisualLine(bufferLine);

    /// <summary>画素単位でスクロールする（範囲外は収める）。スクロールが動けば <see cref="ViewportScrolled"/> が出る。</summary>
    public void ScrollToOffset(double verticalOffset, double horizontalOffset)
        => Canvas.ScrollTo(verticalOffset, horizontalOffset);

    /// <summary>
    /// 表示行 <paramref name="visualLine"/> の上端の Y 座標（このコントロール基準）。本文の上にある
    /// パンくずなどの帯のぶんも含むので、隣に並べた要素（差分の中央の帯など）をそのまま行に合わせられる。
    /// </summary>
    public double GetVisualLineTop(int visualLine)
    {
        var origin = Canvas.TranslatePoint(new Point(0, 0), this);
        return origin.Y + visualLine * Canvas.LineHeight - Canvas.VerticalOffset;
    }

    /// <summary>本文領域の上端の Y 座標（このコントロール基準）。</summary>
    public double TextAreaTop => Canvas.TranslatePoint(new Point(0, 0), this).Y;
}
