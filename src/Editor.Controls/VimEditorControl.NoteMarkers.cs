using System;
using System.Collections.Generic;
using Editor.Controls.Rendering;

namespace Editor.Controls;

/// <summary>
/// VimEditorControl の注記の印（行に付いた会話の吹き出し。例：PR のレビューのコメント）。列そのものは
/// <see cref="EditorCanvas"/> 側にあり、ここはホスト向けの薄い窓口。会話を読む・行を合わせる・押したときに何をするかは
/// すべてホストの責務で、エディタは「行に吹き出しを描き、乗れば中身を出し、押されたら知らせる」だけ。
/// 既定では無効（列の幅 0）で、<see cref="SetNoteMarkersEnabled"/> を呼ぶまで見た目は従来どおり。
/// </summary>
public partial class VimEditorControl
{
    /// <summary>乗ったときの中身をホストが組み立てる（画像入りの会話など）。null を返せば印の文字列を出す。</summary>
    public Func<int, object?>? NoteToolTipContentProvider
    {
        get => Canvas.NoteToolTipContentProvider;
        set => Canvas.NoteToolTipContentProvider = value;
    }

    /// <summary>印が押されたとき。引数はバッファ行（0始まり）。</summary>
    public event Action<int>? NoteMarkerClicked;

    /// <summary>注記の列（テストの列の右、行番号の左）を有効/無効にする。既定は無効。</summary>
    public void SetNoteMarkersEnabled(bool enabled) => Canvas.SetNoteMarkersEnabled(enabled);

    /// <summary>このドキュメントの印を全部置き換える（空で消える）。編集で行がずれたら、ホストが付け直して送り直す。</summary>
    public void SetNoteMarkers(IReadOnlyList<EditorNoteMarker> markers) => Canvas.SetNoteMarkers(markers);

    private void OnCanvasNoteMarkerClicked(int bufferLine)
    {
        NoteMarkerClicked?.Invoke(bufferLine);
        Focus();
    }
}
