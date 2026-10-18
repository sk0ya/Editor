using System.Collections.Generic;
using Editor.Controls.Rendering;

namespace Editor.Controls;

/// <summary>
/// VimEditorControl の「行末の値表示」（デバッグ停止中の Inline Values）の公開窓口。
/// 値の取得・どの行に何を出すか・いつ消すかはすべてホストの責務で、エディタは行末に薄く描くだけ。
/// <see cref="LoadFile(string)"/> で別のファイルを載せると捨てる（行番号にしか結び付いていないため）。
/// </summary>
public partial class VimEditorControl
{
    /// <summary>行末の値を全置換する（空リストで消える）。<see cref="EditorInlineValue.Line0"/> は 0 始まり。</summary>
    public void SetInlineValues(IReadOnlyList<EditorInlineValue> values) => Canvas.SetInlineValues(values);

    /// <summary>
    /// いま閉じている折りたたみ（ホストがタブと一緒に保存し、開き直したときに <see cref="RestoreClosedFolds"/> で戻す）。
    /// 行は 0 始まり。<see cref="Editor.Core.Folds.FoldRegion.IsManual"/> は手で畳んだもの（<c>zf</c> など）。
    /// </summary>
    public IReadOnlyList<Editor.Core.Folds.FoldRegion> GetClosedFolds()
    {
        var closed = new List<Editor.Core.Folds.FoldRegion>();
        foreach (var fold in _engine.CurrentBuffer.Folds.Folds)
            if (fold.IsClosed) closed.Add(fold);
        return closed;
    }

    /// <summary>
    /// 前に閉じていた折りたたみを戻す。ファイルを載せたあと（<see cref="LoadFile(string)"/> は折りたたみを捨てる）に呼ぶ。
    /// 言語サーバーの範囲がまだ届いていなければ、届いた最初の 1 回で閉じる。
    /// </summary>
    public void RestoreClosedFolds(IEnumerable<Editor.Core.Folds.FoldRegion> folds)
    {
        _engine.CurrentBuffer.Folds.RestoreClosed(folds, _engine.CurrentBuffer.Text.LineCount);
        UpdateAll();
    }
}
