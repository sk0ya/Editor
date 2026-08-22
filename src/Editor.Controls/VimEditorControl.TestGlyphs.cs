using System;
using System.Collections.Generic;
using Editor.Controls.Rendering;

namespace Editor.Controls;

/// <summary>
/// VimEditorControl のテスト実行ガター連携。列そのものは
/// <see cref="Editor.Controls.Rendering.EditorCanvas"/> 側にあり、ここはホスト向けの薄い公開窓口。
/// テストの発見・実行・結果判定はすべてホストの責務で、エディタは「行にグリフを描き、クリックを通知する」だけ。
/// 既定では無効（列の幅 0）で、<see cref="SetTestGlyphsEnabled"/> を呼ぶまで表示・レイアウトとも従来どおり。
/// </summary>
public partial class VimEditorControl
{
    /// <summary>テスト列のグリフがクリックされたとき。引数はバッファ行（0始まり）。
    /// ホストはこれを購読してその行のテストを実行し、<see cref="SetTestGlyphs"/> で結果を返す。</summary>
    public event Action<int>? TestGlyphClicked;

    /// <summary>テスト列（ブレークポイント列の右、行番号列の左）を有効化/無効化する。既定は無効。</summary>
    public void SetTestGlyphsEnabled(bool enabled) => Canvas.SetTestGlyphsEnabled(enabled);

    /// <summary>このドキュメントのテストグリフを全置換する（空リストで消える）。</summary>
    public void SetTestGlyphs(IReadOnlyList<EditorTestGlyph> glyphs) => Canvas.SetTestGlyphs(glyphs);

    private void OnCanvasTestGlyphClicked(int bufferLine)
    {
        TestGlyphClicked?.Invoke(bufferLine);
        Focus();
    }
}
