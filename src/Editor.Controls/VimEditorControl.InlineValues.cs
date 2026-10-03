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
}
