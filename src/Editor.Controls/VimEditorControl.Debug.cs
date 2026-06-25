using System;
using System.Collections.Generic;

namespace Editor.Controls;

/// <summary>
/// VimEditorControl のデバッグ用ガター連携。ブレークポイント列・実行中行ハイライトの土台は
/// <see cref="Editor.Controls.Rendering.EditorCanvas"/> 側にあり、ここはホスト（デバッガ）向けの薄い公開窓口。
/// 既定では無効で、<see cref="SetBreakpointsEnabled"/> を呼ぶまで表示・挙動とも従来どおり。
/// </summary>
public partial class VimEditorControl
{
    /// <summary>ガターのブレークポイント列がクリックされ、その行がトグルされたとき。引数はバッファ行（0始まり）。
    /// ホストはこれを購読してデバッグアダプタへ反映し、<see cref="SetBreakpoints"/> で確定状態を返す。</summary>
    public event Action<int>? BreakpointToggled;

    /// <summary>ブレークポイント列（左端のガター）を有効化/無効化する。</summary>
    public void SetBreakpointsEnabled(bool enabled) => Canvas.SetBreakpointsEnabled(enabled);

    /// <summary>このドキュメントの全ブレークポイント行（0始まり）を設定する。</summary>
    public void SetBreakpoints(IEnumerable<int> lines) => Canvas.SetBreakpoints(lines);

    /// <summary>デバッガが停止している現在行（0始まり、無ければ -1）を設定する。</summary>
    public void SetExecutionLine(int bufferLine) => Canvas.SetExecutionLine(bufferLine);

    private void OnCanvasBreakpointToggled(int bufferLine)
    {
        BreakpointToggled?.Invoke(bufferLine);
        Focus();
    }
}
