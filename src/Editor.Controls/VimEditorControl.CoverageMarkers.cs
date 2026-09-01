using System.Collections.Generic;
using Editor.Controls.Rendering;

namespace Editor.Controls;

/// <summary>VimEditorControlのcoverage marker公開窓口。解析・レポート解釈はホストの責務。</summary>
public partial class VimEditorControl
{
    public void SetCoverageMarkersEnabled(bool enabled) => Canvas.SetCoverageMarkersEnabled(enabled);

    public void SetCoverageMarkers(IReadOnlyList<EditorCoverageMarker> markers)
        => Canvas.SetCoverageMarkers(markers);
}
